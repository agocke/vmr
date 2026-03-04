"""Rules for running crossgen2 (ReadyToRun AOT compiler) on managed assemblies."""

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

    args.append(il_assembly.path)

    inputs = [il_assembly, clrjit_file, jitinterface_file]

    # Native crossgen2 path: use the NativeAOT-compiled binary directly
    if ctx.attr.native_crossgen2:
        native_exe = ctx.executable.native_crossgen2
        all_inputs = inputs + [native_exe]
        native_runfiles = ctx.attr.native_crossgen2[DefaultInfo].default_runfiles
        if native_runfiles and native_runfiles.files:
            all_inputs = all_inputs + native_runfiles.files.to_list()

        ctx.actions.run(
            executable = native_exe,
            inputs = all_inputs,
            outputs = [output],
            arguments = args,
            mnemonic = "Crossgen2Native",
            progress_message = "Crossgen2 (native) compiling %s" % il_assembly.short_path,
        )
        return

    crossgen2_exe = ctx.executable._crossgen2
    args_str = " ".join(["'%s'" % a for a in args])

    # Collect all runfiles from crossgen2 as inputs
    runfiles = ctx.attr._crossgen2[DefaultInfo].default_runfiles
    all_inputs = inputs + [crossgen2_exe]
    if runfiles and runfiles.files:
        all_inputs = all_inputs + runfiles.files.to_list()

    # Add runfiles.bash from rules_shell as an explicit input
    runfiles_bash_files = ctx.attr._runfiles_bash[DefaultInfo].files.to_list()
    all_inputs = all_inputs + runfiles_bash_files

    # NativeLibrary.Load searches next to the calling assembly. Copy the
    # jitinterface library next to ILCompiler.ReadyToRun.dll in the runfiles.
    # This works on both Linux and macOS (where DYLD_LIBRARY_PATH is stripped by SIP).
    # The wrapper script needs RUNFILES_DIR set to find runfiles.bash.
    cmd = (
        "RUNFILES_DIR=\"{exe}.runfiles\" && ".format(exe = crossgen2_exe.path) +
        "export RUNFILES_DIR && " +
        # Copy jitinterface next to ILCompiler.ReadyToRun.dll
        "ILC_DIR=$(find \"$RUNFILES_DIR\" -name 'ILCompiler.ReadyToRun.dll' -print -quit 2>/dev/null | xargs dirname 2>/dev/null) && " +
        "if [ -n \"$ILC_DIR\" ]; then cp -f \"{src}\" \"$ILC_DIR/{basename}\" 2>/dev/null || true; fi && ".format(src = jitinterface_file.path, basename = jitinterface_file.basename) +
        # Also copy next to the shared framework DLLs
        "FX_DIR=$(find \"$RUNFILES_DIR\" -path '*/Microsoft.NETCore.App/*' -name 'System.Private.CoreLib.dll' -print -quit 2>/dev/null | xargs dirname 2>/dev/null) && " +
        "if [ -n \"$FX_DIR\" ]; then cp -f \"{src}\" \"$FX_DIR/{basename}\" 2>/dev/null || true; fi && ".format(src = jitinterface_file.path, basename = jitinterface_file.basename) +
        "{exe} {args}".format(exe = crossgen2_exe.path, args = args_str)
    )

    ctx.actions.run_shell(
        command = cmd,
        inputs = all_inputs,
        outputs = [output],
        tools = [ctx.executable._crossgen2],
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
        "native_crossgen2": attr.label(
            mandatory = False,
            cfg = "exec",
            executable = True,
            doc = "Optional NativeAOT-compiled crossgen2 binary. When set, uses this instead of the managed crossgen2.",
        ),
        "_crossgen2": attr.label(
            default = Label("//src/coreclr/tools/aot/crossgen2"),
            cfg = "exec",
            executable = True,
        ),
        "_runfiles_bash": attr.label(
            default = Label("@bazel_tools//tools/bash/runfiles"),
            cfg = "exec",
        ),
    },
)
