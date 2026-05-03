"""Roslyn xunit test rule.

Wraps a csharp_library test assembly and runs it with the xunit console runner
via the SDK dotnet host. Similar to the runtime's library_test but simplified
for Roslyn (no testhost/Core_Root dependency).
"""

load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@bazel_skylib//rules:common_settings.bzl", "BuildSettingInfo")
load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",
)
load(
    "@rules_dotnet//dotnet/private:common.bzl",
    "collect_transitive_runfiles",
    "get_toolchain",
    "to_rlocation_path",
)
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("@rules_dotnet//dotnet/private/rules/csharp/actions:csharp_assembly.bzl", "AssemblyAction")
load("@rules_dotnet//dotnet/private/macros:register_tfms.bzl", "get_tfm_value")
load("@rules_dotnet//dotnet/private/rules/common:attrs.bzl", "CSHARP_LIBRARY_COMMON_ATTRS")
load("@rules_dotnet//dotnet/private:common.bzl", "is_debug", "resolve_debug_type")

COPY_EXECUTION_REQUIREMENTS = {"no-remote": "1"}

def _compile_csharp_library(ctx, tfm):
    """Compile a C# library."""
    toolchain = get_toolchain(ctx)
    return AssemblyAction(
        ctx.actions,
        ctx.executable._compiler_wrapper_bat if ctx.target_platform_has_constraint(
            ctx.attr._windows_constraint[platform_common.ConstraintValueInfo],
        ) else ctx.executable._compiler_wrapper_sh,
        additionalfiles = ctx.files.additionalfiles,
        debug = is_debug(ctx),
        debug_type = resolve_debug_type(ctx),
        defines = ctx.attr.defines,
        deps = ctx.attr.deps,
        exports = [],
        targeting_pack = ctx.attr._targeting_pack[0],
        internals_visible_to = ctx.attr.internals_visible_to,
        cls_compliant = False,
        assembly_version = ctx.attr.assembly_version,
        keyfile = ctx.file.keyfile,
        langversion = ctx.attr.langversion if ctx.attr.langversion != "" else toolchain.dotnetinfo.csharp_default_version,
        resources = ctx.files.resources,
        resource_logical_names = getattr(ctx.attr, "resource_logical_names", {}),
        srcs = ctx.files.srcs,
        data = ctx.files.data,
        appsetting_files = [],
        compile_data = ctx.files.compile_data,
        out = ctx.attr.out,
        target = "library",
        target_name = ctx.attr.name,
        target_framework = tfm,
        toolchain = toolchain,
        strict_deps = toolchain.strict_deps[BuildSettingInfo].value,
        generate_documentation_file = ctx.attr.generate_documentation_file,
        include_host_model_dll = False,
        label = ctx.label,
        direct_analyzers = ctx.attr.analyzers if hasattr(ctx.attr, "analyzers") else [],
        treat_warnings_as_errors = ctx.attr.treat_warnings_as_errors,
        warnings_as_errors = ctx.attr.warnings_as_errors,
        warnings_not_as_errors = ctx.attr.warnings_not_as_errors,
        warning_level = ctx.attr.warning_level,
        nowarn = ctx.attr.nowarn,
        project_sdk = ctx.attr.project_sdk,
        allow_unsafe_blocks = ctx.attr.allow_unsafe_blocks,
        nullable = ctx.attr.nullable,
        run_analyzers = ctx.attr.run_analyzers,
        is_analyzer = False,
        is_language_specific_analyzer = False,
        analyzer_configs = ctx.files.analyzer_configs if hasattr(ctx.attr, "analyzer_configs") else [],
        compiler_options = ctx.attr.compiler_options,
        override_debug = False,
        ref_assembly = False,
        is_windows = ctx.target_platform_has_constraint(
            ctx.attr._windows_constraint[platform_common.ConstraintValueInfo],
        ),
        shared_compilation_worker = None,
        pathmap = {},
    )

def _roslyn_xunit_test_impl(ctx):
    tfm = get_tfm_value(ctx.attr._target_framework)

    (compile_provider, runtime_provider) = _compile_csharp_library(ctx, tfm)
    dll = runtime_provider.libs[0]
    additional_runfiles = list(runtime_provider.pdbs)

    # Copy xunit console runner files next to test DLL
    xunit_console_dll = None
    for f in ctx.files._xunit_runner:
        dst = ctx.actions.declare_file(
            "%s/%s/%s" % (ctx.label.name, tfm, f.basename),
        )
        ctx.actions.run_shell(
            inputs = [f],
            outputs = [dst],
            command = "cp -f \"$1\" \"$2\"",
            arguments = [f.path, dst.path],
            mnemonic = "CopyFile",
            progress_message = "Copying %s" % f.basename,
            use_default_shell_env = True,
            execution_requirements = COPY_EXECUTION_REQUIREMENTS,
        )
        additional_runfiles.append(dst)
        if f.basename == "xunit.console.dll":
            xunit_console_dll = dst

    if xunit_console_dll == None:
        fail("xunit.console.dll not found in xunit runner files")

    # Copy transitive runtime deps next to test DLL
    copied_basenames = {}
    transitive_runtime_deps = runtime_provider.deps.to_list()
    for dep in transitive_runtime_deps:
        for lib in dep.libs:
            if lib.extension == "dll" and lib.basename not in copied_basenames:
                copied_basenames[lib.basename] = True
                dst = ctx.actions.declare_file(
                    "%s/%s/%s" % (ctx.label.name, tfm, lib.basename),
                )
                ctx.actions.run_shell(
                    inputs = [lib],
                    outputs = [dst],
                    command = "cp -f \"$1\" \"$2\"",
                    arguments = [lib.path, dst.path],
                    mnemonic = "CopyFile",
                    progress_message = "Copying %s" % lib.basename,
                    use_default_shell_env = True,
                    execution_requirements = COPY_EXECUTION_REQUIREMENTS,
                )
                additional_runfiles.append(dst)

    # Generate runtimeconfig.json
    toolchain = get_toolchain(ctx)
    sdk_version = toolchain.dotnetinfo.runtime_version
    test_name = dll.basename.replace(".dll", "")
    runtimeconfig = ctx.actions.declare_file(
        "%s/%s/%s.runtimeconfig.json" % (ctx.label.name, tfm, test_name),
    )
    ctx.actions.write(
        output = runtimeconfig,
        content = """\
{{
  "runtimeOptions": {{
    "tfm": "{tfm}",
    "framework": {{
      "name": "Microsoft.NETCore.App",
      "version": "{version}"
    }}
  }}
}}
""".format(tfm = tfm, version = sdk_version),
    )
    additional_runfiles.append(runtimeconfig)

    # Create launcher script - use dotnet from toolchain
    toolchain = get_toolchain(ctx)
    dotnet_runtime_files = toolchain.dotnetinfo.runtime_files
    dotnet_host = dotnet_runtime_files[0] if dotnet_runtime_files else None
    if dotnet_host == None:
        fail("No dotnet host found in toolchain")

    launcher = ctx.actions.declare_file(
        "%s/%s/%s.sh" % (ctx.label.name, tfm, dll.basename),
    )
    ctx.actions.expand_template(
        template = ctx.file._launcher_sh,
        output = launcher,
        substitutions = {
            "TEMPLATED_dotnet": to_rlocation_path(ctx, dotnet_host),
            "TEMPLATED_xunit_console": to_rlocation_path(ctx, xunit_console_dll),
            "TEMPLATED_entry_dll": to_rlocation_path(ctx, dll),
        },
        is_executable = True,
    )

    additional_runfiles.extend(dotnet_runtime_files)
    additional_runfiles.extend(ctx.files._bash_runfiles)

    default_info = DefaultInfo(
        executable = launcher,
        runfiles = collect_transitive_runfiles(
            ctx,
            runtime_provider,
            ctx.attr.deps,
        ).merge(ctx.runfiles(files = additional_runfiles)).merge(
            ctx.attr._bash_runfiles[DefaultInfo].default_runfiles,
        ),
        files = depset([dll]),
    )

    return [default_info, compile_provider, runtime_provider]


_roslyn_xunit_test = rule(
    _roslyn_xunit_test_impl,
    doc = "Compile a C# library test and run with xunit console runner",
    attrs = dicts.add(
        CSHARP_LIBRARY_COMMON_ATTRS,
        {
            "_launcher_sh": attr.label(
                doc = "Launcher template",
                default = "//src/roslyn:run_xunit_test.sh.tpl",
                allow_single_file = True,
            ),
            "_xunit_runner": attr.label(
                doc = "The xunit console runner files",
                default = "//src/roslyn:xunit_console_runner",
                allow_files = True,
            ),
            "_bash_runfiles": attr.label(
                doc = "Bash runfiles library",
                default = "@bazel_tools//tools/bash/runfiles",
            ),
        },
    ),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def roslyn_xunit_test(
        name,
        deps = [],
        nowarn = [],
        size = "medium",
        **kwargs):
    """Macro for Roslyn xunit tests."""
    _roslyn_xunit_test(
        name = name,
        deps = deps,
        target_frameworks = ["net10.0"],
        nowarn = nowarn + ["CS1701"],
        size = size,
        generate_documentation_file = False,
        **kwargs
    )
