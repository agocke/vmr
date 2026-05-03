"""Rules for running ILLink (IL Trimmer) on managed assemblies.

ILLink removes unreachable internal code from an assembly, matching the
post-compilation trimming step in MSBuild (eng/illink.targets).  The rule
produces a single trimmed DLL.

Typical pipeline:  csharp_library → illink_trim → crossgen_assembly
"""

load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",
)

def _illink_trim_impl(ctx):
    output = ctx.outputs.out

    # Get the IL assembly (and optional PDB) from the csharp_library provider.
    runtime_info = ctx.attr.assembly[DotnetAssemblyRuntimeInfo]
    il_assembly = runtime_info.libs[0]
    il_pdb = runtime_info.pdbs[0] if runtime_info.pdbs else None

    # Assembly name = output basename without .dll extension.
    assembly_name = output.basename.removesuffix(".dll")

    # Collect reference assembly files and unique directories for -d args.
    #
    # For ref assemblies (ref_assembly=True csharp_library targets),
    # DefaultInfo.files is empty — the DLL is only in
    # DotnetAssemblyCompileInfo.irefs. Fall back to that provider when
    # dep.files yields no DLLs.
    ref_files = []
    ref_dirs = {}
    for dep in ctx.attr.refs:
        dlls = [f for f in dep.files.to_list() if f.extension == "dll"]
        if not dlls and DotnetAssemblyCompileInfo in dep:
            dlls = list(dep[DotnetAssemblyCompileInfo].irefs)
        for f in dlls:
            ref_files.append(f)
            ref_dirs[f.dirname] = True

    # Build ILLink argument list matching eng/illink.targets ILLinkTrimAssembly.
    # The MSBuild ILLink task (LinkTask.cs) also emits --warnaserror- in its
    # response file (from _treatWarningsAsErrors defaulting to null).
    illink_args = [
        "--ignore-link-attributes", "true",
        "--skip-unresolved", "true",
        "--trim-mode", "skip",
        "--action", "skip",
        "--action", "link", assembly_name,
        "--nowarn", "IL2008;IL2009;IL2121;IL2025;IL2035",
        "--warnaserror-",
    ]

    # MSBuild passes -b true and --preserve-symbol-paths when PDBs exist
    # (eng/illink.targets lines 212-213).  ILLink requires the PDB to be
    # next to the input assembly; we stage both into a temp dir below.
    if il_pdb:
        illink_args.extend(["-b", "true", "--preserve-symbol-paths"])

    for d in sorted(ref_dirs.keys()):
        illink_args.extend(["-d", d])

    for desc in ctx.files.descriptors:
        illink_args.extend(["-x", desc.path])

    for supp in ctx.files.suppressions:
        illink_args.extend(["--link-attributes", supp.path])

    # Per-assembly ILLink options (e.g. System.Linq.Expressions disables ipconstprop).
    if ctx.attr.disable_opt_ipconstprop:
        illink_args.extend(["--disable-opt", "ipconstprop"])

    illink_exe = ctx.executable._illink

    # Collect all inputs including the illink tool's runfiles.
    all_inputs = [il_assembly] + ref_files + ctx.files.descriptors + ctx.files.suppressions
    if il_pdb:
        all_inputs.append(il_pdb)
    all_inputs.append(illink_exe)
    runfiles = ctx.attr._illink[DefaultInfo].default_runfiles
    if runfiles and runfiles.files:
        all_inputs = all_inputs + runfiles.files.to_list()
    runfiles_bash_files = ctx.attr._runfiles_bash[DefaultInfo].files.to_list()
    all_inputs = all_inputs + runfiles_bash_files

    # Quote all args for shell.
    args_str = " ".join(["'%s'" % a for a in illink_args])

    illink_exe = ctx.executable._illink

    # ILLink writes to an output directory and expects the rooted assembly to be
    # resolved by name from an assembly search path, matching eng/illink.targets.
    # Stage the root DLL (and optional PDB) into a temp dir, add that dir as a
    # search path, root by simple assembly name, then copy the trimmed result.
    maybe_copy_pdb = ""
    if il_pdb:
        maybe_copy_pdb = 'cp "{pdb}" "$STAGE/{pdb_basename}" && '.format(
            pdb = il_pdb.path,
            pdb_basename = il_pdb.basename,
        )

    cmd = (
        'RUNFILES_DIR="{exe}.runfiles" && '.format(exe = illink_exe.path) +
        "export RUNFILES_DIR && " +
        "STAGE=$(mktemp -d) && OUTDIR=$(mktemp -d) && " +
        'trap "rm -rf $STAGE $OUTDIR" EXIT && ' +
        'cp "{dll}" "$STAGE/{basename}" && '.format(dll = il_assembly.path, basename = il_assembly.basename) +
        maybe_copy_pdb +
        '{exe} {args} -d "$STAGE" -a \'{assembly_name}\' library -out "$OUTDIR" && '.format(
            exe = illink_exe.path,
            args = args_str,
            assembly_name = assembly_name,
        ) +
        'cp "$OUTDIR/{basename}" "{out}"'.format(basename = output.basename, out = output.path)
    )

    ctx.actions.run_shell(
        command = cmd,
        inputs = all_inputs,
        outputs = [output],
        tools = [ctx.executable._illink],
        mnemonic = "ILLinkTrim",
        progress_message = "ILLink trimming %s" % il_assembly.short_path,
    )

    return [DefaultInfo(files = depset([output]))]

illink_trim = rule(
    implementation = _illink_trim_impl,
    attrs = {
        "assembly": attr.label(
            mandatory = True,
            providers = [DotnetAssemblyRuntimeInfo],
            doc = "The IL assembly target (csharp_library) to trim.",
        ),
        "out": attr.output(
            mandatory = True,
            doc = "The output trimmed assembly (e.g. System.Collections.NonGeneric.dll).",
        ),
        "refs": attr.label_list(
            allow_files = True,
            doc = "Reference assemblies whose directories are passed as -d search paths to ILLink.",
        ),
        "descriptors": attr.label_list(
            allow_files = [".xml"],
            doc = "ILLink.Descriptors.LibraryBuild.xml files passed as -x (type/method preservation).",
        ),
        "suppressions": attr.label_list(
            allow_files = [".xml"],
            doc = "ILLink.Suppressions.LibraryBuild.xml files passed as --link-attributes.",
        ),
        "disable_opt_ipconstprop": attr.bool(
            default = False,
            doc = "Pass --disable-opt ipconstprop (used by System.Linq.Expressions).",
        ),
        "_illink": attr.label(
            default = Label("//src/tools/illink/src/linker:illink"),
            cfg = "exec",
            executable = True,
        ),
        "_runfiles_bash": attr.label(
            default = Label("@bazel_tools//tools/bash/runfiles"),
            cfg = "exec",
        ),
    },
)
