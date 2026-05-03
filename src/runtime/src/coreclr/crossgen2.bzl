"""Rules for running crossgen2 (ReadyToRun AOT compiler) on managed assemblies.

Both rules use the NativeAOT-compiled crossgen2 binary (crossgen2-publish)
which is a standalone native executable — no .NET runtime or runfiles needed.
The binary loads libjitinterface via NativeLibrary.Load, so we set
LD_LIBRARY_PATH / DYLD_LIBRARY_PATH to the directory containing the
jitinterface shared library.
"""

load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyRuntimeInfo",
)

def _crossgen_corelib_impl(ctx):
    output = ctx.outputs.out

    # Get the IL assembly from the csharp_library provider
    runtime_info = ctx.attr.assembly[DotnetAssemblyRuntimeInfo]
    il_assembly = runtime_info.libs[0]

    jitinterface_file = ctx.file.jitinterface
    clrjit_file = ctx.file.clrjit

    args = [
        "--jitpath:%s" % clrjit_file.path,
        "-o:%s" % output.path,
        "--targetarch:%s" % ctx.attr.target_arch,
        "--targetos:%s" % ctx.attr.target_os,
        "-O",
    ]

    if ctx.attr.verify_type_and_field_layout:
        args.append("--verify-type-and-field-layout")
        args.append("--enable-cached-interface-dispatch-support")

    # MIBC PGO data (multiple .mibc files from the optimization NuGet package).
    mibc_files = ctx.files.mibc
    for m in mibc_files:
        args.append("-m:%s" % m.path)
    if mibc_files:
        args.append("--embed-pgo-data")

    args.append(il_assembly.path)

    inputs = [il_assembly, clrjit_file, jitinterface_file] + mibc_files

    native_exe = ctx.executable.native_crossgen2
    all_inputs = inputs + [native_exe]
    native_runfiles = ctx.attr.native_crossgen2[DefaultInfo].default_runfiles
    if native_runfiles and native_runfiles.files:
        all_inputs = all_inputs + native_runfiles.files.to_list()

    args_str = " ".join(["'%s'" % a for a in args])
    jitinterface_dir = jitinterface_file.dirname
    cmd = (
        "export LD_LIBRARY_PATH=\"{jitdir}:${{LD_LIBRARY_PATH:-}}\" && ".format(jitdir = jitinterface_dir) +
        "export DYLD_LIBRARY_PATH=\"{jitdir}:${{DYLD_LIBRARY_PATH:-}}\" && ".format(jitdir = jitinterface_dir) +
        "{exe} {args}".format(exe = native_exe.path, args = args_str)
    )

    ctx.actions.run_shell(
        command = cmd,
        inputs = all_inputs,
        outputs = [output],
        mnemonic = "Crossgen2",
        progress_message = "Crossgen2 compiling %s" % il_assembly.short_path,
    )

crossgen_corelib = rule(
    implementation = _crossgen_corelib_impl,
    attrs = {
        "assembly": attr.label(
            mandatory = True,
            providers = [DotnetAssemblyRuntimeInfo],
            doc = "The IL assembly target (csharp_library) to compile with crossgen2.",
        ),
        "out": attr.output(
            mandatory = True,
            doc = "The output R2R assembly.",
        ),
        "target_arch": attr.string(
            default = "x64",
            doc = "Target architecture (x64, arm64, etc.).",
        ),
        "target_os": attr.string(
            default = "linux",
            doc = "Target OS (linux, windows, osx).",
        ),
        "verify_type_and_field_layout": attr.bool(
            default = False,
            doc = "Enable type and field layout verification (Debug/Checked builds).",
        ),
        "clrjit": attr.label(
            mandatory = True,
            allow_single_file = True,
            doc = "The clrjit shared library.",
        ),
        "jitinterface": attr.label(
            mandatory = True,
            allow_single_file = True,
            doc = "The jitinterface shared library.",
        ),
        "mibc": attr.label_list(
            allow_files = [".mibc"],
            doc = "MIBC PGO data files passed as -m: (enables --embed-pgo-data).",
        ),
        "native_crossgen2": attr.label(
            mandatory = True,
            cfg = "exec",
            executable = True,
            doc = "NativeAOT-compiled crossgen2 binary.",
        ),
    },
)

# =============================================================================
# crossgen_assembly: R2R compile a framework assembly (post-ILLink)
# =============================================================================
#
# Unlike crossgen_corelib which takes a DotnetAssemblyRuntimeInfo provider,
# this rule takes a raw file (the ILLink-trimmed DLL) as input.  It also
# supports reference assemblies (-r:), MIBC PGO data (-m: + --embed-pgo-data),
# and perfmap symbol generation (--perfmap).
#
# MSBuild equivalent: RunReadyToRunCompiler task in
# Microsoft.NET.CrossGen.targets, driven by PublishReadyToRun=true in
# Microsoft.NETCore.App.Runtime.CoreCLR.sfxproj.

def _crossgen_assembly_impl(ctx):
    output = ctx.outputs.out

    input_assembly = ctx.file.assembly
    jitinterface_file = ctx.file.jitinterface
    clrjit_file = ctx.file.clrjit

    args = [
        "--jitpath:%s" % clrjit_file.path,
        "--targetarch:%s" % ctx.attr.target_arch,
        "--targetos:%s" % ctx.attr.target_os,
        "-O",
    ]

    # Reference assemblies: -r: for each DLL in the closure.
    # MSBuild's RunReadyToRunCompiler skips the assembly being compiled
    # from the reference list (GetAssemblyReferencesCommands checks filename).
    ref_files = []
    input_basename = input_assembly.basename
    for dep in ctx.attr.refs:
        for f in dep.files.to_list():
            if f.extension == "dll" and f.basename != input_basename:
                ref_files.append(f)
                args.append("-r:%s" % f.path)

    # MIBC PGO data (StandardOptimizationData.mibc).
    mibc_files = ctx.files.mibc
    for m in mibc_files:
        args.append("-m:%s" % m.path)
    if mibc_files:
        args.append("--embed-pgo-data")

    # Output and input.
    args.append("--out:%s" % output.path)
    args.append(input_assembly.path)

    inputs = [input_assembly, clrjit_file, jitinterface_file] + ref_files + mibc_files

    native_exe = ctx.executable.native_crossgen2
    all_inputs = inputs + [native_exe]
    native_runfiles = ctx.attr.native_crossgen2[DefaultInfo].default_runfiles
    if native_runfiles and native_runfiles.files:
        all_inputs = all_inputs + native_runfiles.files.to_list()

    args_str = " ".join(["'%s'" % a for a in args])
    jitinterface_dir = jitinterface_file.dirname
    cmd = (
        "export LD_LIBRARY_PATH=\"{jitdir}:${{LD_LIBRARY_PATH:-}}\" && ".format(jitdir = jitinterface_dir) +
        "export DYLD_LIBRARY_PATH=\"{jitdir}:${{DYLD_LIBRARY_PATH:-}}\" && ".format(jitdir = jitinterface_dir) +
        "{exe} {args}".format(exe = native_exe.path, args = args_str)
    )

    ctx.actions.run_shell(
        command = cmd,
        inputs = all_inputs,
        outputs = [output],
        mnemonic = "Crossgen2",
        progress_message = "Crossgen2 R2R %s" % input_assembly.short_path,
    )

    return [DefaultInfo(files = depset([output]))]

crossgen_assembly = rule(
    implementation = _crossgen_assembly_impl,
    attrs = {
        "assembly": attr.label(
            mandatory = True,
            allow_single_file = [".dll"],
            doc = "The IL assembly file (typically ILLink-trimmed) to compile with crossgen2.",
        ),
        "out": attr.output(
            mandatory = True,
            doc = "The output R2R assembly.",
        ),
        "refs": attr.label_list(
            allow_files = True,
            doc = "Reference assemblies passed as -r: to crossgen2.",
        ),
        "mibc": attr.label_list(
            allow_files = [".mibc"],
            doc = "MIBC PGO data files passed as -m: (enables --embed-pgo-data).",
        ),
        "target_arch": attr.string(
            default = "x64",
            doc = "Target architecture (x64, arm64, etc.).",
        ),
        "target_os": attr.string(
            default = "linux",
            doc = "Target OS (linux, windows, osx).",
        ),
        "clrjit": attr.label(
            mandatory = True,
            allow_single_file = True,
            doc = "The clrjit shared library.",
        ),
        "jitinterface": attr.label(
            mandatory = True,
            allow_single_file = True,
            doc = "The jitinterface shared library.",
        ),
        "native_crossgen2": attr.label(
            mandatory = True,
            cfg = "exec",
            executable = True,
            doc = "NativeAOT-compiled crossgen2 binary.",
        ),
    },
)
