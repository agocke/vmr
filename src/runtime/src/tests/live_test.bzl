load("//:defs.bzl", "NETCOREAPP_CURRENT", "csharp_library")
load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@bazel_skylib//rules:common_settings.bzl", "BuildSettingInfo")
load("@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",)
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("@rules_dotnet//dotnet/private/rules/csharp:binary.bzl", "compile_csharp_exe")
load("@rules_dotnet//dotnet/private/rules/csharp/actions:csharp_assembly.bzl", "AssemblyAction")
load("@rules_dotnet//dotnet/private:common.bzl",
    "collect_transitive_runfiles",
    "generate_runtimeconfig",
    "get_toolchain",
    "is_core_framework",
    "is_debug",
    "is_standard_framework",
    "to_rlocation_path",)
load("@rules_dotnet//dotnet/private/macros:register_tfms.bzl", "get_tfm_value")
load("//src/libraries:defs.bzl", "LIVE_REFPACK_DEPS", "CORE_ROOT_REFPACK_DEPS")
load("//src/tests:defs.bzl", "COMMON_ATTRS", "build_binary", "create_launcher", "COPY_EXECUTION_REQUIREMENTS")

# Match src/tests/Directory.Build.props NoWarn
_TEST_NOWARN = [
    "CS0078", "CS0162", "CS0164", "CS0168", "CS0169", "CS0219",
    "CS0251", "CS0252", "CS0414", "CS0429", "CS0618", "CS0642",
    "CS0649", "CS0652", "CS0659", "CS0675", "CS1691", "CS1717",
    "CS1718", "CS3001", "CS3002", "CS3003", "CS3005", "CS3008",
    "CS3016", "CS8981",
]

# Label for the Roslyn compiler server persistent worker binary.
_SHARED_COMPILATION_WORKER = "@rules_dotnet//dotnet/private/tools/compiler_worker"

def _live_csharp_test_impl(ctx):
    result = build_binary(ctx, compile_csharp_exe)
    return result

def _to_dict(s):
    return {
        key: getattr(s, key) for key in dir(s)
        if key != "to_json" and key != "to_proto" and key != "aspect_ids"
    }


_live_csharp_test = rule(
    _live_csharp_test_impl,
    doc = """Compile a C# exe for the live framework""",
    attrs = dicts.add(
        COMMON_ATTRS,
        {
            "_launcher_sh": attr.label(
                doc = "A template file for the launcher on Linux/MacOS",
                default = "//eng:run_test.sh.tpl",
                allow_single_file = True,
            ),
        }),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def live_csharp_test(
    name,
    deps = [],
    analyzers = [],
    nowarn = [],
    size = "small",
    use_shared_compilation = True,
    **kwargs
):
    analyzers = analyzers + [
        "//src/tests/Common:XUnitWrapperGenerator",
    ]
    deps = deps + LIVE_REFPACK_DEPS
    _live_csharp_test(
        name = name,
        deps = deps,
        analyzers = analyzers,
        target_frameworks = [NETCOREAPP_CURRENT],
        nowarn = nowarn + [ "CS1701" ] + _TEST_NOWARN,
        size = size,
        use_shared_compilation = use_shared_compilation,
        shared_compilation_worker = _SHARED_COMPILATION_WORKER if use_shared_compilation else None,
        # Match MSBuild: GenerateAssemblyInfo=false (no CLSCompliant attribute),
        # GenerateDocumentationFile=false, Nullable=annotations.
        generate_documentation_file = False,
        **kwargs
    )

def _compile_csharp_library(ctx, tfm):
    """Compile action that produces a library instead of exe."""
    toolchain = get_toolchain(ctx)
    return AssemblyAction(
        ctx.actions,
        ctx.executable._compiler_wrapper_bat if ctx.target_platform_has_constraint(ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]) else ctx.executable._compiler_wrapper_sh,
        label = ctx.label,
        additionalfiles = ctx.files.additionalfiles,
        direct_analyzers = ctx.attr.analyzers,
        debug = is_debug(ctx),
        defines = ctx.attr.defines,
        deps = ctx.attr.deps,
        exports = [],
        targeting_pack = ctx.attr._targeting_pack[0],
        internals_visible_to = ctx.attr.internals_visible_to,
        cls_compliant = ctx.attr.cls_compliant,
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
        analyzer_configs = ctx.files.analyzer_configs,
        compiler_options = ctx.attr.compiler_options,
        override_debug = False,
        ref_assembly = False,
        is_windows = ctx.target_platform_has_constraint(ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]),
        shared_compilation_worker = ctx.executable.shared_compilation_worker if ctx.attr.use_shared_compilation else None,
        use_shared_compilation = ctx.attr.use_shared_compilation,
    )

def _xunit_library_test_impl(ctx):
    """Build a library test and create a launcher that runs xunit.console.dll."""
    tfm = get_tfm_value(ctx.attr._target_framework)

    if is_standard_framework(tfm):
        fail("It doesn't make sense to build a test for " + tfm)

    (compile_provider, runtime_provider) = _compile_csharp_library(ctx, tfm)
    dll = runtime_provider.libs[0]
    additional_runfiles = list(runtime_provider.pdbs)

    # Copy the xunit console runner files to the same output directory as the test DLL.
    xunit_console_dll = None
    for f in ctx.files._xunit_runner:
        dst = ctx.actions.declare_file("%s/%s/%s" % (ctx.label.name, tfm, f.basename))
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

    # Copy xunit.runner.json next to the test DLL to match MSBuild behavior.
    # Key settings: preEnumerateTheories=false avoids expensive upfront theory
    # enumeration, and diagnosticMessages=true enables long-running test warnings.
    xunit_runner_json = ctx.file._xunit_runner_config
    dst = ctx.actions.declare_file("%s/%s/%s" % (ctx.label.name, tfm, xunit_runner_json.basename))
    ctx.actions.run_shell(
        inputs = [xunit_runner_json],
        outputs = [dst],
        command = "cp -f \"$1\" \"$2\"",
        arguments = [xunit_runner_json.path, dst.path],
        mnemonic = "CopyFile",
        progress_message = "Copying %s" % xunit_runner_json.basename,
        use_default_shell_env = True,
        execution_requirements = COPY_EXECUTION_REQUIREMENTS,
    )
    additional_runfiles.append(dst)

    # Copy non-framework transitive runtime deps to the output directory.
    # Framework assemblies are already in the testhost via Core_Root, so only
    # test helpers (TestUtilities, RemoteExecutor, etc.) need to be copied.
    # Deduplicate by basename to avoid conflicts when NuGet packages provide
    # DLLs for multiple TFMs (e.g., net8.0 + netstandard2.0).
    framework_basenames = {f.basename: True for f in ctx.files._framework_assemblies}
    copied_basenames = {}
    transitive_runtime_deps = runtime_provider.deps.to_list()
    for dep in transitive_runtime_deps:
        for lib in dep.libs:
            if lib.extension == "dll" and lib.basename not in framework_basenames and lib.basename not in copied_basenames:
                copied_basenames[lib.basename] = True
                dst = ctx.actions.declare_file("%s/%s/%s" % (ctx.label.name, tfm, lib.basename))
                ctx.actions.run_shell(
                    inputs = [lib],
                    outputs = [dst],
                    command = "cp -f \"$1\" \"$2\"",
                    arguments = [lib.path, dst.path],
                    mnemonic = "CopyFile",
                    progress_message = "Copying files",
                    use_default_shell_env = True,
                    execution_requirements = COPY_EXECUTION_REQUIREMENTS,
                )
                additional_runfiles.append(dst)

    # Copy data files to the output directory next to the test DLL.
    # Preserves directory structure: for local files, paths are relative to
    # the package; for NuGet content files, the contentFiles/any/any/ prefix
    # is stripped to get the expected relative path.
    # csharp_library/csharp_binary outputs are flattened: their paths look like
    # <Target>/net10.0/<Target>.dll but tests expect <Target>.dll in the CWD.
    pkg_prefix = ctx.label.package + "/"
    for f in ctx.files.data:
        sp = f.short_path
        if "contentFiles/any/any/" in sp:
            # NuGet content file: strip up to contentFiles/any/any/
            rel = sp[sp.index("contentFiles/any/any/") + len("contentFiles/any/any/"):]
        elif sp.startswith(pkg_prefix):
            # Local file in same package: strip package path
            rel = sp[len(pkg_prefix):]
            # Flatten csharp_library/csharp_binary output paths
            # e.g. "STAMain/net10.0/STAMain.dll" -> "STAMain.dll"
            parts = rel.split("/")
            if len(parts) == 3 and parts[1] == tfm:
                rel = parts[2]
        else:
            # Fallback: just use basename
            rel = f.basename
        dst = ctx.actions.declare_file("%s/%s/%s" % (ctx.label.name, tfm, rel))
        ctx.actions.run_shell(
            inputs = [f],
            outputs = [dst],
            command = "cp -f \"$1\" \"$2\" && chmod u+rw \"$2\"",
            arguments = [f.path, dst.path],
            mnemonic = "CopyFile",
            progress_message = "Copying %s" % rel,
            use_default_shell_env = True,
            execution_requirements = COPY_EXECUTION_REQUIREMENTS,
        )
        additional_runfiles.append(dst)

    toolchain = get_toolchain(ctx)
    sdk_version = toolchain.dotnetinfo.runtime_version

    # Generate runtimeconfig.json files:
    # 1. For the test assembly - RemoteExecutor.InitializePaths() walks the stack
    #    to find a runtimeconfig.json from calling assemblies.
    # 2. For Microsoft.DotNet.RemoteExecutor.dll - so "dotnet exec" child
    #    processes resolve libhostpolicy.so from the testhost.
    test_runtimeconfig = _generate_runtimeconfigs(ctx, dll, tfm, sdk_version, additional_runfiles)

    test_depsfile = _generate_test_depsfile(ctx, dll, tfm, additional_runfiles)

    # Get the shared testhost directory from the attribute
    testhost = ctx.file._shared_testhost

    windows_constraint = ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]
    launcher = ctx.actions.declare_file("{}.{}".format(dll.basename, "bat" if ctx.target_platform_has_constraint(windows_constraint) else "sh"), sibling = dll)
    ctx.actions.expand_template(
        template = ctx.file._launcher_sh,
        output = launcher,
        substitutions = {
            "TEMPLATED_testhost": to_rlocation_path(ctx, testhost),
            "TEMPLATED_xunit_console": to_rlocation_path(ctx, xunit_console_dll),
            "TEMPLATED_entry_dll": to_rlocation_path(ctx, dll),
            "TEMPLATED_depsfile": to_rlocation_path(ctx, test_depsfile),
            "TEMPLATED_runtimeconfig": to_rlocation_path(ctx, test_runtimeconfig),
            "TEMPLATED_writable_test_dir": "true" if ctx.attr.writable_test_dir else "false",
        },
        is_executable = True,
    )
    additional_runfiles.append(testhost)
    additional_runfiles.extend(ctx.files._bash_runfiles)

    default_info = DefaultInfo(
        executable = launcher,
        runfiles = collect_transitive_runfiles(ctx, runtime_provider, ctx.attr.deps).merge(ctx.runfiles(files = additional_runfiles)).merge(ctx.attr._bash_runfiles[DefaultInfo].default_runfiles),
        files = depset([dll]),
    )

    return [default_info, compile_provider, runtime_provider]

def _generate_runtimeconfigs(ctx, dll, tfm, sdk_version, additional_runfiles):
    """Generate runtimeconfig.json files needed by the test and RemoteExecutor.

    Two runtimeconfig files are generated:
    1. <test_assembly>.runtimeconfig.json - RemoteExecutor.InitializePaths() walks
       the stack trace looking for runtimeconfig files from calling assemblies
       (excluding itself). Without this, RuntimeConfigPath is null and
       RemoteExecutor.Invoke with RuntimeConfigurationOptions throws.
    2. Microsoft.DotNet.RemoteExecutor.runtimeconfig.json - needed by the dotnet
       host when RemoteExecutor spawns child processes via "dotnet exec
       Microsoft.DotNet.RemoteExecutor.dll" so it resolves libhostpolicy.so from
       the testhost's shared framework directory.
    """
    runtimeconfig_content = """\
{{
  "runtimeOptions": {{
    "tfm": "{tfm}",
    "framework": {{
      "name": "Microsoft.NETCore.App",
      "version": "{version}"
    }},
    "configProperties": {{
      "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": true
    }}
  }}
}}
""".format(tfm = tfm, version = sdk_version)

    # Always generate a runtimeconfig for the test assembly itself.
    test_name = dll.basename.replace(".dll", "")
    test_runtimeconfig = ctx.actions.declare_file(
        "%s/%s/%s.runtimeconfig.json" % (ctx.label.name, tfm, test_name),
    )
    ctx.actions.write(output = test_runtimeconfig, content = runtimeconfig_content)
    additional_runfiles.append(test_runtimeconfig)

    # Also generate one for RemoteExecutor if it's a dependency.
    has_remote_executor = False
    for f in additional_runfiles:
        if f.basename == "Microsoft.DotNet.RemoteExecutor.dll":
            has_remote_executor = True
            break

    if has_remote_executor:
        re_runtimeconfig = ctx.actions.declare_file(
            "%s/%s/Microsoft.DotNet.RemoteExecutor.runtimeconfig.json" % (ctx.label.name, tfm),
        )
        ctx.actions.write(output = re_runtimeconfig, content = runtimeconfig_content)
        additional_runfiles.append(re_runtimeconfig)

    # Generate runtimeconfig.json for data DLLs (helper executables like
    # STAMain.dll, MTAMain.dll) so they can be launched with "dotnet exec".
    for f in ctx.files.data:
        if f.basename.endswith(".dll"):
            helper_name = f.basename.replace(".dll", "")
            helper_runtimeconfig = ctx.actions.declare_file(
                "%s/%s/%s.runtimeconfig.json" % (ctx.label.name, tfm, helper_name),
            )
            ctx.actions.write(output = helper_runtimeconfig, content = runtimeconfig_content)
            additional_runfiles.append(helper_runtimeconfig)

    return test_runtimeconfig

def _generate_test_depsfile(ctx, dll, tfm, additional_runfiles):
    """Generate a deps.json listing all test output DLLs so dotnet adds them to TPA.

    The shared testhost only contains framework assemblies (SDK + Core_Root).
    Non-framework DLLs (xunit, test helpers, the library under test) live in the
    app directory alongside xunit.console.dll.  The deps.json must list these by
    filename so the host probes the app directory for them.
    """
    test_name = dll.basename.replace(".dll", "")
    test_depsfile = ctx.actions.declare_file(
        "%s/%s/%s.deps.json" % (ctx.label.name, tfm, test_name),
    )

    # Collect all DLL basenames from the output directory.
    dll_entries = []
    seen = {}
    for f in additional_runfiles:
        if f.path.endswith(".dll") and f.basename not in seen:
            seen[f.basename] = True
            dll_entries.append('          "%s": {}' % f.basename)

    # Also include the test assembly itself.
    if dll.basename not in seen:
        dll_entries.append('          "%s": {}' % dll.basename)

    runtime_block = ",\n".join(dll_entries)

    depsfile_content = """\
{{
  "runtimeTarget": {{
    "name": ".NETCoreApp,Version=v10.0",
    "signature": ""
  }},
  "targets": {{
    ".NETCoreApp,Version=v10.0": {{
      "{test_name}/1.0.0": {{
        "runtime": {{
{runtime_block}
        }}
      }}
    }}
  }},
  "libraries": {{
    "{test_name}/1.0.0": {{
      "type": "project",
      "serviceable": false,
      "sha512": ""
    }}
  }}
}}
""".format(test_name = test_name, runtime_block = runtime_block)
    ctx.actions.write(output = test_depsfile, content = depsfile_content)
    additional_runfiles.append(test_depsfile)

    return test_depsfile

def _shared_testhost_impl(ctx):
    """Build a shared testhost directory that all library tests can reference.

    This rule creates the testhost once, containing SDK + runtime assemblies.
    Individual tests reference this shared testhost instead of each building
    their own, eliminating ~90MB of redundant file copies per test.
    """
    toolchain = ctx.toolchains["@rules_dotnet//dotnet:toolchain_type"]
    dotnet_file = toolchain.dotnetinfo.runtime_files[0]
    sdk_version = toolchain.dotnetinfo.runtime_version

    testhost = ctx.actions.declare_directory("testhost")

    # Collect all runtime files into a single list for the shell command
    runtime_files = ctx.files.managed_assemblies + ctx.files.native_libs + ctx.files.runtime_binaries

    ctx.actions.run_shell(
        inputs = [dotnet_file] + runtime_files,
        outputs = [testhost],
        command = """\
SDK_ROOT=$(dirname "$(readlink -f "$1")")
VERSION="$2"
OUT="$3"
shift 3

FW_DIR="$OUT/shared/Microsoft.NETCore.App/$VERSION"
mkdir -p "$FW_DIR"
mkdir -p "$OUT/host/fxr/$VERSION"

# Copy the dotnet host binary so it resolves frameworks from this directory.
# Use -L to dereference symlinks - Bazel's sandbox presents inputs as symlinks,
# and we need actual files in the testhost to avoid broken absolute paths.
cp -aL "$SDK_ROOT/dotnet" "$OUT/dotnet"

# SDK host framework resolver
cp -aL "$SDK_ROOT/host/fxr/$VERSION/"* "$OUT/host/fxr/$VERSION/"

# SDK shared framework as base (lowest priority)
cp -aL "$SDK_ROOT/shared/Microsoft.NETCore.App/$VERSION/"* "$FW_DIR/"

# Copy Bazel-built runtime files (managed + native) over SDK
for f in "$@"; do
    cp -afL "$f" "$FW_DIR/"
done

# Generate a version-free deps.json matching MSBuild's testhost pattern.
# The SDK's deps.json contains assemblyVersion/fileVersion constraints that
# don't match Bazel-built assemblies (which have different versions than the
# SDK's R2R copies). A version-free deps.json lets the host resolve assemblies
# from the framework directory without version-checking.
{
  printf '{"runtimeTarget":{"name":".NETCoreApp,Version=v0.0/rid","signature":""},'
  printf '"compilationOptions":{},"targets":{".NETCoreApp,Version=v0.0":{},'
  printf '".NETCoreApp,Version=v0.0/rid":{"Microsoft.NETCore.App/%s":{"runtime":{' "$VERSION"
  first=true
  for dll in "$FW_DIR"/*.dll; do
    [ -f "$dll" ] || continue
    base=$(basename "$dll")
    if $first; then first=false; else printf ','; fi
    printf '"runtimes/rid/lib/netcoreapp0.0/%s":{}' "$base"
  done
  printf '},"native":{'
  first=true
  for native in "$FW_DIR"/*.so "$FW_DIR"/*.dylib; do
    [ -f "$native" ] || continue
    base=$(basename "$native")
    if $first; then first=false; else printf ','; fi
    printf '"runtimes/rid/native/%s":{}' "$base"
  done
  printf '}}}},"libraries":{"Microsoft.NETCore.App/%s":{"type":"package","serviceable":false,"sha512":""}}}' "$VERSION"
} > "$FW_DIR/Microsoft.NETCore.App.deps.json"

# Copy SDK ref assemblies so tests that create dynamic Roslyn compilations
# (e.g. RegexGenerator tests) can reference them.  The shared-framework
# CoreLib is crossgen'd (R2R) and Roslyn can't parse its metadata.
REF_DIR="$SDK_ROOT/packs/Microsoft.NETCore.App.Ref"
if [ -d "$REF_DIR" ]; then
    REF_VER=$(ls -1 "$REF_DIR" | sort -V | tail -1)
    if [ -d "$REF_DIR/$REF_VER/ref/net10.0" ]; then
        mkdir -p "$OUT/ref"
        cp -aL "$REF_DIR/$REF_VER/ref/net10.0/"*.dll "$OUT/ref/"
    fi
fi
""",
        arguments = [dotnet_file.path, sdk_version, testhost.path] + [f.path for f in runtime_files],
        mnemonic = "BuildTestHost",
        progress_message = "Building shared testhost",
    )

    return [DefaultInfo(files = depset([testhost]))]

_shared_testhost_rule = rule(
    _shared_testhost_impl,
    doc = """Build a shared testhost directory for library tests.""",
    attrs = {
        "managed_assemblies": attr.label(
            doc = "Filegroup containing managed assemblies (impl_netcoreapp)",
            default = "//src/libraries:impl_netcoreapp",
            allow_files = True,
        ),
        "native_libs": attr.label_list(
            doc = "Native libraries to include in the testhost",
            default = [
                "//src/native/libs/System.Native:System.Native",
                "//src/native/libs/System.Globalization.Native:System.Globalization.Native",
                "//src/native/libs/System.IO.Compression.Native:System.IO.Compression.Native",
                "//src/native/libs/System.IO.Ports.Native:System.IO.Ports.Native",
                "//src/native/libs/System.Net.Security.Native:System.Net.Security.Native",
            ],
            allow_files = True,
        ),
        "runtime_binaries": attr.label(
            doc = "Filegroup containing runtime binaries (coreclr, hostpolicy, etc.)",
            default = "//src/tests:testhost_runtime_binaries",
            allow_files = True,
        ),
    },
    toolchains = ["@rules_dotnet//dotnet:toolchain_type"],
)

def shared_testhost(name = "shared_testhost", **kwargs):
    """Create a shared testhost that all library tests can reference."""
    _shared_testhost_rule(name = name, **kwargs)

_xunit_library_test = rule(
    _xunit_library_test_impl,
    doc = """Compile a C# library test and run it with xunit.console.dll""",
    attrs = dicts.add(
        COMMON_ATTRS,
        {
            "_launcher_sh": attr.label(
                doc = "A template file for the launcher on Linux/MacOS",
                default = "//eng:run_library_test.sh.tpl",
                allow_single_file = True,
            ),
            "_xunit_runner": attr.label(
                doc = "The xunit console runner files",
                default = "//eng:xunit_console_runner",
                allow_files = True,
            ),
            "_xunit_runner_config": attr.label(
                doc = "The xunit.runner.json configuration file",
                default = "//eng:testing/xunit/xunit.runner.json",
                allow_single_file = True,
            ),
            "_framework_assemblies": attr.label(
                doc = "Framework impl assemblies already present in Core_Root. " +
                      "DLLs matching these basenames are excluded from the test " +
                      "output directory since the testhost provides them.",
                default = "//src/libraries:impl_netcoreapp",
            ),
            "_shared_testhost": attr.label(
                doc = "The shared testhost directory containing SDK + Core_Root. " +
                      "Built once and shared across all library tests to avoid " +
                      "redundant ~90MB file copies per test.",
                default = "//src/tests:shared_testhost",
                allow_single_file = True,
            ),
            "writable_test_dir": attr.bool(
                doc = "Copy test runtime files to a writable directory before running. " +
                      "Needed for tests that modify files next to the assembly (e.g. PDB rename). " +
                      "Off by default to avoid the copy overhead.",
                default = False,
            ),
        }),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def library_test(
    name,
    deps = [],
    analyzers = [],
    nowarn = [],
    size = "medium",
    use_shared_compilation = True,
    **kwargs
):
    """Test macro for library tests that compiles as library and runs via xunit.console.dll."""
    deps = deps + LIVE_REFPACK_DEPS
    # Match MSBuild default: src/libraries/Directory.Build.props sets
    # <Nullable>annotations</Nullable> for test projects.
    nullable = kwargs.pop("nullable", "annotations")
    _xunit_library_test(
        name = name,
        deps = deps,
        analyzers = analyzers,
        target_frameworks = [NETCOREAPP_CURRENT],
        nowarn = nowarn + [ "CS1701" ] + _TEST_NOWARN,
        size = size,
        nullable = nullable,
        use_shared_compilation = use_shared_compilation,
        shared_compilation_worker = _SHARED_COMPILATION_WORKER if use_shared_compilation else None,
        # Match MSBuild: GenerateAssemblyInfo=false (no CLSCompliant attribute),
        # GenerateDocumentationFile=false.
        generate_documentation_file = False,
        **kwargs
    )

def coreclr_test(
    name,
    deps = [],
    size = "small",
    pri = 0,
    tags = [],
    debug_type = "portable", # TODO: plum through to compiler
    optimize = False, # TODO: plum through to compiler
    compiler_options = [],
    use_shared_compilation = True,
    nullable = "annotations",
    **kwargs
):
    # Build complete deps list for JIT tests:
    # 1. User deps (filtered to remove any already in CORE_ROOT_REFPACK_DEPS)
    # 2. Xunit deps
    # 3. CORE_ROOT_REFPACK_DEPS (refs matching impls in Core_Root)
    core_root_set = {dep: True for dep in CORE_ROOT_REFPACK_DEPS}
    filtered_deps = [dep for dep in deps if dep not in core_root_set]

    all_deps = filtered_deps + [
        "@paket.main//microsoft.dotnet.xunitassert",
        "@paket.main//xunit.abstractions",
        "@paket.main//xunit.extensibility.core",
    ] + CORE_ROOT_REFPACK_DEPS

    compiler_options = [
        "/debug:%s" % debug_type,
        "/optimize%s" % ("" if optimize else "-"),
    ] + compiler_options

    analyzers = [
        "//src/tests/Common:XUnitWrapperGenerator",
    ]

    # Create a library target for the merged runner
    csharp_library(
        name = name + "_lib",
        deps = all_deps,
        nowarn = _TEST_NOWARN,
        tags = tags,
        visibility = ["//visibility:public"],
        compiler_options = compiler_options,
        langversion = "preview",
        disable_implicit_framework_refs = True,
        target_frameworks = [NETCOREAPP_CURRENT],
        use_shared_compilation = use_shared_compilation,
        nullable = nullable,
        generate_documentation_file = False,
        **kwargs
    )

    # Create the test target - calls _live_csharp_test directly to bypass LIVE_REFPACK_DEPS
    _live_csharp_test(
        name = name,
        deps = all_deps,
        analyzers = analyzers,
        size = size,
        tags = tags + ["pri%d" % pri],
        compiler_options = compiler_options,
        target_frameworks = [NETCOREAPP_CURRENT],
        nowarn = ["CS1701"] + _TEST_NOWARN,
        use_shared_compilation = use_shared_compilation,
        shared_compilation_worker = _SHARED_COMPILATION_WORKER if use_shared_compilation else None,
        nullable = nullable,
        generate_documentation_file = False,
        **kwargs
    )

def _transform_dep_impl(ctx):
    # Transform explicit dep into dep with extern alias
    dep = ctx.attr.dep
    compile = dep[DotnetAssemblyCompileInfo]
    compile_dict = _to_dict(compile)
    compile_dict.pop("alias")
    newcomp = DotnetAssemblyCompileInfo(
        alias = "_" + compile.name.replace(".", "_"),
        **compile_dict
    )
    default_info = dep[DefaultInfo]
    runtime_info = dep[DotnetAssemblyRuntimeInfo]
    return [
        default_info,
        newcomp,
        runtime_info,
    ]

_transform_dep = rule(
    _transform_dep_impl,
    attrs = {
        "dep": attr.label(
            doc = "The dependencies to transform",
            providers = [DotnetAssemblyCompileInfo],
        ),
    },
)

def _il_test_impl(ctx):
    args = []
    if ctx.attr.debug_type == "full":
        args.append("-debug")
    if ctx.attr.debug_type == "pdbonly":
        args.append("-debug=opt")
    if ctx.attr.optimize:
        args.append("-optimize")

    args.append("-output=%s" % ctx.outputs.out.path)

    for src in ctx.files.srcs:
        args.append(src.path)

    dll = ctx.outputs.out
    additional_runfiles = [dll]

    ctx.actions.run(
        inputs = ctx.files.srcs,
        outputs = [ctx.outputs.out],
        arguments = args,
        progress_message = "Compiling %s" % ctx.outputs.out.short_path,
        executable = ctx.executable.ilasm_exe,
    )

    # Copy runtime DLLs from deps alongside the test DLL
    for dep in ctx.attr.deps:
        runtime_info = dep[DotnetAssemblyRuntimeInfo]
        for lib in runtime_info.libs:
            if lib.extension == "dll":
                dst = ctx.actions.declare_file(lib.basename, sibling = dll)
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

    launcher = create_launcher(ctx, additional_runfiles, dll)

    default_info = DefaultInfo(
        executable = launcher,
        runfiles = ctx.runfiles(files = additional_runfiles).merge(ctx.attr._bash_runfiles[DefaultInfo].default_runfiles),
        files = depset([dll]),
    )

    providers = [default_info]
    if ctx.attr.env:
        providers.append(testing.TestEnvironment(ctx.attr.env))

    return providers

_il_test = rule(
    implementation = _il_test_impl,
    attrs = {
        "srcs": attr.label_list(
            doc = "The source files to compile",
            allow_files = True,
        ),
        "out": attr.output(
            mandatory = True,
            doc = "The output DLL.",
        ),
        "deps": attr.label_list(
            doc = "Runtime dependencies (DLLs copied alongside the test assembly)",
            providers = [DotnetAssemblyRuntimeInfo],
            default = [],
        ),
        "debug_type": attr.string(
            doc = "The debug type",
            default = "full",
        ),
        "optimize": attr.bool(
            doc = "Enable optimization.",
            default = False,
        ),
        "ilasm_exe": attr.label(
            default = Label("//src/coreclr/ilasm"),
            cfg = "exec",
            executable = True,
            allow_files = True,
        ),
        "_launcher_sh": attr.label(
            doc = "A template file for the launcher on Linux/MacOS",
            default = "//eng:run_test.sh.tpl",
            allow_single_file = True,
        ),
        "_windows_constraint": attr.label(default = "@platforms//os:windows"),
        "_core_root": attr.label(
            doc = "The host binary to use for the launcher",
            default = "//:Core_Root",
            allow_single_file = True,
        ),
        "_bash_runfiles": attr.label(
            default = "@bazel_tools//tools/bash/runfiles",
            allow_files = True,
        ),
        "env": attr.string_dict(
            doc = "Environment variables to set when running the test.",
        ),
    },
    test = True,
)

def il_coreclr_test(
    name,
    size = "small",
    pri = 0,
    tags = [],
    **kwargs
):
    _il_test(
        name = name,
        out = name + ".dll",
        size = size,
        tags = tags + [ "pri%d" % pri ],
        **kwargs
    )


def coreclr_merged_test(
    name,
    deps = [],
    test_deps = [],
    size = "medium",
    tags = [],
    **kwargs
):
    """ Create a merged test that includes all of the test_deps as test sources.

    Args:
        name: The name of the test
        deps: The dependencies of the test
        test_deps: The test dependencies to merge
        tags: The tags for the test
        **kwargs: Additional arguments to pass to live_csharp_test
    """

    # Tests may have the same types, so we need to add extern aliases
    transformed_deps = []
    for (i, dep) in enumerate(test_deps):
        transform_label_name = "_transform_dep_%s_%s" % (name, i)
        # coreclr_test creates two targets, one library and one test. We need the library target as
        # a dependency.
        dep_label = native.package_relative_label(dep)
        lib_dep =  dep_label.same_package_label(dep_label.name + "_lib")

        _transform_dep(
            name = transform_label_name,
            dep = lib_dep,
        )
        transformed_deps.append(":" + transform_label_name)

    # Test deps may reference types from common test infrastructure (e.g.,
    # PlatformDetection in TestLibrary via [ActiveIssue] attributes). With strict
    # deps these transitive references are not visible to the merged compilation,
    # so include them explicitly.
    merged_deps = deps + [
        "//src/tests/Common:TestLibrary",
        "@paket.main//microsoft.dotnet.xunitextensions",
    ]

    live_csharp_test(
        name = name,
        deps = merged_deps + transformed_deps,
        size = size,
        tags = tags + ["merged", "manual"],
        **kwargs
    )