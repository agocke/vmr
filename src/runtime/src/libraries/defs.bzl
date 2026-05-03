
load(
    "//:defs.bzl",
    "ASPNETCORE_SNK",
    "CI_INFORMATIONAL_VERSION",
    "ECMA_SNK",
    "MSFT_SNK",
    "NETCOREAPP_CURRENT",
    "OPEN_SNK",
    "SHAREDLIB1024_SNK",
    "csharp_library",
    "gen_assembly_info",
    "gen_illink_substitutions",
    "gen_pnse_source",
    "gen_target_framework_attrs",
)

load(
    "//eng/bazel:version.bzl",
    "PRODUCT_VERSION",
)

load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",
    "DotnetTargetingPackInfo",
)

load("@bazel_skylib//rules:run_binary.bzl", "run_binary")

_NETCOREAPP_CURRENT_ASSEMBLY_VERSION = NETCOREAPP_CURRENT[len("net"):].split(".")[0] + "." + NETCOREAPP_CURRENT[len("net"):].split(".")[1] + ".0.0"

LIVE_NETCOREAPP_DEPS = [
#    "//src/libraries:live_System.Runtime",
#    "//src/libraries:live_System.Console",
#    "//src/libraries/System.Runtime.InteropServices:live_System.Runtime.InteropServices",
#    "//src/libraries:live_Microsoft.Win32.Primitives",
#    "//src/libraries:live_System.Collections",
]

# Nowarns for assemblies that also target netstandard/net4x in MSBuild.
# Suppresses warnings about APIs that don't exist in older TFMs.
MULTITARGET_NOWARN = [
    "CA1510", "CA1511", "CA1512", "CA1513",
    "CA1845", "CA1846", "CA1847",
]

# MSBuild flows these SDK-shipped source generators into a broad set of source
# library projects via the targeting pack. Reuse the shared list for assemblies
# that need that analyzer parity in compare-bazel.
COMMON_GENERATOR_ANALYZERS = [
    "//src/libraries/System.Runtime.InteropServices:ComInterfaceGenerator",
    "//src/libraries/System.Text.Json:JsonSourceGenerator",
    "//src/libraries/System.Text.RegularExpressions:RegexGenerator",
]

# ── Core_Root library set ─────────────────────────────────────────────
# This is the single source of truth for what libraries are available to
# JIT/coreclr tests at both compile time (refs) and runtime (impls).
# 
# Format: (library_name, package_path_or_none)
#   - If package_path is None, the target is at //src/libraries:ref_/impl_<name>
#   - If package_path is a string, the target is at that path:ref_/impl_<name>
#
# IMPORTANT: If a JIT test needs a library, add it here. This ensures both
# the ref (for compilation) and impl (for runtime) are available.
CORE_ROOT_LIBS = [
    # Core libraries (these have refs and impls at //src/libraries:)
    ("System.Runtime", None),
    ("Microsoft.Win32.Primitives", None),
    ("System.ComponentModel.Primitives", None),
    ("System.Diagnostics.Process", None),
    
    # Standard libraries with their own packages
    ("System.Collections", "//src/libraries/System.Collections"),
    ("System.Collections.Concurrent", "//src/libraries/System.Collections.Concurrent"),
    ("System.Collections.Immutable", "//src/libraries/System.Collections.Immutable"),
    ("System.Collections.NonGeneric", "//src/libraries/System.Collections.NonGeneric"),
    ("System.Collections.Specialized", "//src/libraries/System.Collections.Specialized"),
    ("System.ComponentModel", "//src/libraries/System.ComponentModel"),
    ("System.Console", "//src/libraries/System.Console"),
    ("System.Diagnostics.FileVersionInfo", "//src/libraries/System.Diagnostics.FileVersionInfo"),
    ("System.Diagnostics.Tracing", "//src/libraries/System.Diagnostics.Tracing"),
    ("System.IO.MemoryMappedFiles", "//src/libraries/System.IO.MemoryMappedFiles"),
    ("System.Linq", "//src/libraries/System.Linq"),
    ("System.Memory", "//src/libraries/System.Memory"),
    ("System.Numerics.Vectors", "//src/libraries/System.Numerics.Vectors"),
    ("System.ObjectModel", "//src/libraries/System.ObjectModel"),
    ("System.Reflection.Emit", "//src/libraries/System.Reflection.Emit"),
    ("System.Reflection.Emit.ILGeneration", "//src/libraries/System.Reflection.Emit.ILGeneration"),
    ("System.Reflection.Emit.Lightweight", "//src/libraries/System.Reflection.Emit.Lightweight"),
    ("System.Reflection.Metadata", "//src/libraries/System.Reflection.Metadata"),
    ("System.Reflection.Primitives", "//src/libraries/System.Reflection.Primitives"),
    ("System.Reflection.TypeExtensions", "//src/libraries/System.Reflection.TypeExtensions"),
    ("System.Runtime.InteropServices", "//src/libraries/System.Runtime.InteropServices"),
    ("System.Runtime.Intrinsics", "//src/libraries/System.Runtime.Intrinsics"),
    ("System.Runtime.Loader", "//src/libraries/System.Runtime.Loader"),
    ("System.Runtime.Numerics", "//src/libraries/System.Runtime.Numerics"),
    ("System.Runtime.Serialization.Primitives", "//src/libraries/System.Runtime.Serialization.Primitives"),
    ("System.Security.Cryptography", "//src/libraries/System.Security.Cryptography"),
    ("System.Text.Encoding.Extensions", "//src/libraries/System.Text.Encoding.Extensions"),
    ("System.Text.Encodings.Web", "//src/libraries/System.Text.Encodings.Web"),
    ("System.Text.RegularExpressions", "//src/libraries/System.Text.RegularExpressions"),
    ("System.Threading", "//src/libraries/System.Threading"),
    ("System.Threading.Overlapped", "//src/libraries/System.Threading.Overlapped"),
    ("System.Threading.Tasks.Parallel", "//src/libraries/System.Threading.Tasks.Parallel"),
    ("System.Threading.Thread", "//src/libraries/System.Threading.Thread"),
    ("System.Threading.ThreadPool", "//src/libraries/System.Threading.ThreadPool"),
]

def _lib_to_ref(lib_tuple):
    """Convert a CORE_ROOT_LIBS entry to a ref assembly label."""
    name, pkg = lib_tuple
    if pkg:
        return "%s:ref_%s" % (pkg, name)
    else:
        return "//src/libraries:ref_%s" % name

def _lib_to_impl(lib_tuple):
    """Convert a CORE_ROOT_LIBS entry to an impl assembly label."""
    name, pkg = lib_tuple
    if pkg:
        return "%s:impl_%s" % (pkg, name)
    else:
        return "//src/libraries:impl_%s" % name

# Ref assemblies for Core_Root - used by coreclr_test for compilation
CORE_ROOT_REFPACK_DEPS = [_lib_to_ref(lib) for lib in CORE_ROOT_LIBS]

# Impl assemblies for Core_Root - used at runtime (exported for BUILD.bazel)
CORE_ROOT_IMPL_DEPS = [_lib_to_impl(lib) for lib in CORE_ROOT_LIBS]

# Full refpack for library tests (superset of CORE_ROOT_REFPACK_DEPS)
LIVE_REFPACK_DEPS = [
    # Roughly topologically sorted
    "//src/libraries:ref_System.Runtime",
    "//src/libraries/System.Runtime.Loader:ref_System.Runtime.Loader",
    "//src/libraries/System.Console:ref_System.Console",
    "//src/libraries/System.Collections:ref_System.Collections",
    "//src/libraries/System.Linq:ref_System.Linq",
    "//src/libraries/System.Collections.NonGeneric:ref_System.Collections.NonGeneric",
    "//src/libraries/System.ComponentModel:ref_System.ComponentModel",
    "//src/libraries/System.Diagnostics.FileVersionInfo:ref_System.Diagnostics.FileVersionInfo",
    "//src/libraries:ref_System.Diagnostics.Process",
    "//src/libraries/System.Memory:ref_System.Memory",
    "//src/libraries/System.Runtime.Intrinsics:ref_System.Runtime.Intrinsics",
    "//src/libraries/System.Numerics.Vectors:ref_System.Numerics.Vectors",
    "//src/libraries/System.ObjectModel:ref_System.ObjectModel",
    "//src/libraries:ref_System.ComponentModel.Primitives",
    "//src/libraries/System.Collections.Specialized:ref_System.Collections.Specialized",
    "//src/libraries/System.Runtime.InteropServices:ref_System.Runtime.InteropServices",
]

def _default_strong_name_keyfile(base_name, keyfile):
    if keyfile != None:
        return keyfile

    # Match src/libraries/Directory.Build.props defaults for source projects.
    # Many inbox assemblies still override this explicitly in their local
    # Directory.Build.props, so MSFT_SNK remains the general fallback.
    if base_name.startswith("Microsoft.Extensions."):
        return ASPNETCORE_SNK
    if base_name.startswith("Microsoft.Bcl."):
        return OPEN_SNK

    return MSFT_SNK

def _dedupe(items):
    result = []
    for item in items:
        if item not in result:
            result.append(item)
    return result

# Convenience macro for defining a ref assembly for the NetCoreApp framework.
def netcoreapp_ref_assembly(
    name,
    srcs,
    deps = [],
    nowarn = [],
    compiler_options = [],
    keyfile = None,
    cls_compliant = True,
    assembly_version = _NETCOREAPP_CURRENT_ASSEMBLY_VERSION,
    **kwargs
):
    compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/publicsign+",
    ]
    nowarn = nowarn + [
        # Match Directory.Build.props IsReferenceAssemblyProject NoWarn
        "CS0169",
        "CS0649",
        "CS8618",
        "CS8597",
        "CS8625",
        "CS8617",
    ]
    base_name = name[len("ref_"):]
    csharp_library(
        name = name,
        out = base_name,
        srcs = srcs,
        deps = deps,
        cls_compliant = cls_compliant,
        assembly_version = assembly_version,
        visibility = [ "//visibility:public" ],
        nullable = "annotations",
        keyfile = _default_strong_name_keyfile(base_name, keyfile),
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        nowarn = nowarn,
        compiler_options = compiler_options,
        # Match MSBuild's LangVersion=preview from Directory.Build.props.
        langversion = "preview",
        ref_assembly = True,
        debug_type = "none",
        include_runtime_async = False,
        **kwargs
    )

def _gen_facades_impl(ctx):
    libs_paths = [r[DotnetAssemblyCompileInfo].irefs[0] for r in ctx.attr.ref_paths]
    contract_assembly = ctx.attr.facade_contract_assembly[DotnetAssemblyCompileInfo].irefs[0]

    args = [
        "--outputSourcePath=%s" % ctx.outputs.out.path,
        "--contractAssembly=%s" % contract_assembly.path,
    ]

    if len(ctx.attr.defines) > 0:
        args.append("--defines=%s" % ";".join(ctx.attr.defines))

    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = ctx.files.srcs + libs_paths + [contract_assembly],
        outputs = [ctx.outputs.out],
        arguments = args
            + [ "--src=%s" % s.path for s in ctx.files.srcs ]
            + [ "--omitType=%s" % t for t in ctx.attr.facade_omit_types ]
            + ["--ref-path=%s" % p.path for p in libs_paths],
    )

gen_facades = rule(
    implementation = _gen_facades_impl,
    attrs = {
        "srcs": attr.label_list(
            allow_files = True,
            doc = "The source files to generate facades for.",
        ),
        "defines": attr.string_list(
            doc = "The defines to use when generating facades.",
        ),
        "out": attr.output(mandatory = True),
        "ref_paths": attr.label_list(
            doc = "The paths to the reference assemblies.",
        ),
        "facade_contract_assembly": attr.label(
            doc = "The contract assembly to use.",
        ),
        "facade_omit_types": attr.string_list(
            doc = "The types to omit from the generated facades.",
        ),
        "_exe": attr.label(
            default = Label("//src/tools/bazel/GenFacades:GenFacades"),
            cfg = "exec",
            executable = True,
        ),
    }
)

def _gen_resx_source_impl(ctx):
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = [ctx.file.resx_file],
        outputs = [ctx.outputs.out],
        arguments = [
            "--output-path=%s" % ctx.outputs.out.path,
            "--resource-name=%s" % ("FxResources." + ctx.attr.assembly_name + ".SR"),
            "--resource-file=%s" % ctx.file.resx_file.path,
        ],
    )

gen_resx_source = rule(
    implementation = _gen_resx_source_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "assembly_name": attr.string(mandatory = True),
        "resx_file": attr.label(
            mandatory = True,
            allow_single_file = True,
        ),
        "_exe": attr.label(
            default = Label("//src/tools/bazel/GenerateResxSource:GenerateResxSource"),
            cfg = "exec",
            executable = True,
        ),
    }
)

def impl_assembly(
    name,
    srcs = [],
    deps = [],
    defines = [],
    compiler_options = [],
    generate_facades = False,
    facade_contract_assembly = None,
    facade_omit_types = [],
    resx_file = None,
    resource_class_name = None,
    exclude_sr = False,
    keyfile = None,
    cls_compliant = True,
    is_trimmable = True,
    is_aot_compatible = True,
    allow_unsafe_blocks = False,
    nullable = "enable",
    internals_visible_to = [],
    resources = [],
    resource_logical_names = {},
    assembly_version = None,
    assembly_description = None,
    include_dll_safe_search_path = None,
    include_neutral_resources_language = None,
    os_targeted = False,
    nowarn = [],
    partial_facade = False,
    pnse = False,
    pnse_message = None,
    pnse_ref_srcs = None,
    pnse_api_exclusion_list = None,
    supported_os_platforms = [],
    supported_os_platforms_short = [],
    unsupported_os_platforms = [],
    generate_documentation_file = True,
    skip_locals_init = False,
    additionalfiles = [],
    analyzer_configs = [],
    analyzers = [],
    library_import_generator = True,
    interop_source_generation = None,
    com_interface_generator = False,
    jsimport_generator = True,
    include_editorconfig = True,
    interceptors_namespaces = None,
    include_runtime_async = True,
    event_source_generator = None,
    **kwargs
):
    base_name = name[len("impl_"):]
    if assembly_version == None:
        assembly_version = _NETCOREAPP_CURRENT_ASSEMBLY_VERSION

    # Assemblies whose MSBuild TFM includes an OS suffix receive OS-specific
    # implicit defines.  Match that in Bazel via select().
    # os_targeted can be True/"linux" for net10.0-linux, or "unix" for net10.0-unix.
    if os_targeted == "unix" or os_targeted == "Unix":
        defines = defines + select({
            "@platforms//os:linux": ["UNIX", "UNIX1_0"],
            "@platforms//os:macos": ["UNIX", "UNIX1_0"],
            "//conditions:default": [],
        })
    elif os_targeted:
        defines = defines + select({
            "@platforms//os:linux": ["LINUX", "LINUX1_0"],
            "@platforms//os:macos": ["OSX", "OSX1_0"],
            "//conditions:default": [],
        })

    # Partial facade assemblies (IsPartialFacadeAssembly=true in MSBuild) suppress
    # obsolete-API warnings for type-forwarding facades.
    if partial_facade:
        nowarn = nowarn + [
            "SYSLIB0003",
            "SYSLIB0004",
            "SYSLIB0015",
            "SYSLIB0017",
            "SYSLIB0021",
            "SYSLIB0022",
            "SYSLIB0023",
            "SYSLIB0025",
            "SYSLIB0032",
            "SYSLIB0036",
        ]

    # PNSE assemblies (GeneratePlatformNotSupportedAssembly in MSBuild) get extra
    # suppressions because the generated stub code triggers these warnings.
    if pnse:
        nowarn = nowarn + ["nullable", "CA1052", "CA1821", "CA1823", "CS0169"]

    # When pnse_message is set, generate one .notsupported.cs per ref source file,
    # matching MSBuild's GeneratePlatformNotSupportedAssemblyMessage (which produces
    # separate output files like Foo.notsupported.cs, Foo.Extensions.notsupported.cs).
    if pnse_message and pnse_ref_srcs:
        for pnse_src in pnse_ref_srcs:
            # Derive the output filename from the ref source: strip directory and
            # replace .cs with .notsupported.cs — e.g. ref/Foo.Extensions.cs →
            # Foo.Extensions.notsupported.cs
            pnse_src_str = str(pnse_src)
            pnse_basename = pnse_src_str.rsplit("/", 1)[-1].rsplit(":", 1)[-1]
            if pnse_basename.endswith(".cs"):
                pnse_out_name = pnse_basename[:-3] + ".notsupported.cs"
            else:
                pnse_out_name = pnse_basename + ".notsupported.cs"
            pnse_target = "pnse_" + name + "_" + pnse_out_name.replace(".", "_")
            gen_pnse_source(
                name = pnse_target,
                out = name + "/" + pnse_out_name,
                srcs = [pnse_src],
                message = pnse_message,
                api_exclusion_list = pnse_api_exclusion_list,
            )
            srcs = srcs + [":" + pnse_target]

    if not exclude_sr:
        srcs = srcs + [
            "//src/libraries/Common:src/System/SR.cs",
        ]

    # Add SkipLocalsInit.cs for IsNETCoreAppSrc assemblies (matches MSBuild's
    # Directory.Build.targets condition: IsNETCoreAppSrc=true).  The
    # netcoreapp_impl_assembly wrapper passes skip_locals_init=True; OOB
    # assemblies use impl_assembly directly (default False).
    if skip_locals_init:
        srcs = srcs + [
            "//src/libraries/Common:src/SkipLocalsInit.cs",
        ]

    # Generated files must be added in the same order as MSBuild:
    # 1. .NETCoreApp.AssemblyAttributes.cs
    # 2. System.SR.cs (resx-generated)
    # 3. AssemblyInfo.cs
    # 4. Forwards.cs

    # 1. Generate the TargetFrameworkAttribute file
    tfm_attrs_target = "tfmattrs_" + base_name
    gen_target_framework_attrs(
        name = tfm_attrs_target,
        out = name + "/.NETCoreApp.AssemblyAttributes.cs",
    )
    srcs = srcs + [":" + tfm_attrs_target]

    # 2. System.SR.cs is added by the csharp_library wrapper via resx_file

    # 3. Generate the full AssemblyInfo.cs matching MSBuild's WriteCodeFragment
    # 3. Generate the full AssemblyInfo.cs matching MSBuild's WriteCodeFragment
    # MSBuild's CalculateIncludeDllSafeSearchPathAttribute target
    # (eng/versioning.targets) adds DefaultDllImportSearchPathsAttribute when
    # any resolved reference has Filename of System.Runtime.InteropServices or
    # System.Private.CoreLib.  gen_assembly_info auto-detects from ref_deps at
    # analysis time, so select() in deps is fully supported.

    assembly_info_target = "assemblyinfo_" + base_name
    assembly_info_kwargs = {}
    if assembly_version != None:
        assembly_info_kwargs["assembly_version"] = assembly_version
    if assembly_description:
        assembly_info_kwargs["assembly_description"] = assembly_description
    if include_dll_safe_search_path != None:
        assembly_info_kwargs["include_dll_safe_search_path"] = 1 if include_dll_safe_search_path else 0
    gen_assembly_info(
        name = assembly_info_target,
        out = name + "/" + base_name + ".AssemblyInfo.cs",
        assembly_name = base_name,
        informational_version = CI_INFORMATIONAL_VERSION,
        cls_compliant = cls_compliant,
        is_trimmable = is_trimmable,
        is_aot_compatible = is_aot_compatible,
        ref_deps = deps,
        include_neutral_resources_language = include_neutral_resources_language if include_neutral_resources_language != None else (resx_file != None),
        not_supported = pnse,
        supported_os_platforms = supported_os_platforms,
        supported_os_platforms_short = supported_os_platforms_short,
        unsupported_os_platforms = unsupported_os_platforms,
        **assembly_info_kwargs
    )

    # 4. Forwards.cs (if facades)

    # Generate ILLink.Substitutions.xml for libraries with string resources
    resource_logical_names = dict(resource_logical_names)
    if resx_file != None:
        illink_target = "illink_" + base_name
        illink_out = name + "/ILLink.Substitutions.xml"
        gen_illink_substitutions(
            name = illink_target,
            out = illink_out,
            assembly_name = base_name,
            resource_name = "FxResources.%s.SR" % base_name,
        )
        resources = resources + [":" + illink_target]
        # Key is basename — rules_dotnet looks up resource_logical_names by f.basename
        resource_logical_names["ILLink.Substitutions.xml"] = "ILLink.Substitutions.xml"

    # Match MSBuild compiler options
    compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/publicsign+",
        # Match MSBuild's /features flags
        "/features:strict",
        "/features:nullablePublicOnly",
    ]

    # .NET SDK adds InterceptorsNamespaces for TFM >= 10.0 via
    # FrameworkReferenceResolution.targets.  The ancestor SDK (rc.1) does not
    # set this for regular library/test projects, so default to suppressing it.
    # Allow callers to override:
    #   None  → suppress the flag entirely (ancestor default)
    #   ""    → suppress the flag entirely
    #   other → use the caller-supplied value
    if interceptors_namespaces != None and interceptors_namespaces != "":
        compiler_options = compiler_options + [
            "/features:InterceptorsNamespaces=" + interceptors_namespaces,
        ]

    # ── Analyzer infrastructure (matching MSBuild's Analyzers.targets) ──────
    # Generate an empty disabledAnalyzers.config (MSBuild always passes this as
    # /additionalfile even though it's empty).
    _disabled_analyzers_target = "disabled_analyzers_" + base_name
    native.genrule(
        name = _disabled_analyzers_target,
        outs = [name + "/disabledAnalyzers.config"],
        cmd = ": > \"$@\"",
    )

    # Generate per-assembly GeneratedMSBuildEditorConfig.editorconfig matching
    # MSBuild's GenerateMSBuildEditorConfigFile task output.
    _editorconfig_target = "editorconfig_" + base_name
    native.genrule(
        name = _editorconfig_target,
        outs = [name + "/" + base_name + ".GeneratedMSBuildEditorConfig.editorconfig"],
        cmd = """cat >"$@" <<'EOF'
is_global = true
build_property.InformationalVersion = {version}
build_property._SupportedPlatformList = Linux,macOS,Windows,Android,iOS,tvOS,macCatalyst,browser,wasi,illumos,Solaris,Haiku,Unix,FreeBSD
EOF""".format(version = PRODUCT_VERSION),
    )

    # Merge caller-provided additionalfiles with the generated disabledAnalyzers.config.
    # MSBuild also passes resx files as /additionalfile for analyzer consumption.
    _additionalfiles = additionalfiles + [":" + _disabled_analyzers_target]
    if resx_file != None:
        _additionalfiles = _additionalfiles + [resx_file]

    # Merge caller-provided analyzer_configs with the standard source configs
    # and the per-assembly generated editorconfig.
    # include_editorconfig=False for facade/shim assemblies whose MSBuild
    # project doesn't emit a local .editorconfig analyzerconfig entry.
    _analyzer_configs = analyzer_configs + [
        "//:source_analyzer_configs",
        ":" + _editorconfig_target,
    ] if include_editorconfig else analyzer_configs + [
        "//:source_analyzer_configs_no_editorconfig",
        ":" + _editorconfig_target,
    ]

    # Analyzer scoping mirrors MSBuild's two delivery mechanisms:
    #
    # 1. Libraries with EventSourceGenerator (event_source_generator=True):
    #    eng/generators.targets adds EventSourceGenerator +
    #    Microsoft.Interop.SourceGeneration for IsSourceProject +
    #    DisableImplicitFrameworkReferences.  This covers both NCA inner
    #    source libs AND packable "abstractions" libraries that still live
    #    in the shared framework source tree.
    #
    # 2. Libraries without EventSourceGenerator (event_source_generator=False):
    #    Extensions generators (Logging, Options) and Interop.SourceGeneration
    #    flow via the local targeting pack (FrameworkReferenceResolution.targets).
    #
    # event_source_generator defaults to True (matching the majority of
    # impl_assembly users).  OOB libraries that don't get EventSourceGenerator
    # from MSBuild should set event_source_generator = False.
    #
    # ILLink.RoslynAnalyzer is always included — both delivery paths provide it.
    #
    # interop_source_generation defaults to library_import_generator when not
    # set explicitly (None).  Callers that need SourceGeneration without
    # LibraryImportGenerator (e.g. shim/facade assemblies) can pass
    # interop_source_generation = True, library_import_generator = False.
    _interop_source_generation = interop_source_generation if interop_source_generation != None else library_import_generator
    _event_source_generator = event_source_generator if event_source_generator != None else True
    _analyzers = analyzers + [
        "//:source_build_analyzers",
        "//src/tools/illink/src/ILLink.RoslynAnalyzer",
    ]

    if _event_source_generator:
        # eng/generators.targets: EventSourceGenerator + Interop.SourceGeneration
        # for IsSourceProject + DisableImplicitFrameworkReferences.
        _analyzers = _analyzers + [
            "//src/libraries/System.Private.CoreLib:EventSourceGenerator",
            "//src/libraries/System.Runtime.InteropServices:Microsoft.Interop.SourceGeneration",
        ]
    else:
        # Targeting-pack analyzers for OOB / non-EventSourceGenerator libs.
        _analyzers = _analyzers + [
            "//src/libraries/Microsoft.Extensions.Logging.Abstractions:LoggingGenerators",
            "//src/libraries/Microsoft.Extensions.Options:OptionsSourceGeneration",
        ]
        if _interop_source_generation:
            _analyzers = _analyzers + [
                "//src/libraries/System.Runtime.InteropServices:Microsoft.Interop.SourceGeneration",
            ]

    if library_import_generator:
        _analyzers = _analyzers + [
            "//src/libraries/System.Runtime.InteropServices:LibraryImportGenerator",
        ]
    if com_interface_generator:
        _analyzers = _analyzers + [
            "//src/libraries/System.Runtime.InteropServices:ComInterfaceGenerator",
        ]
    if jsimport_generator:
        _analyzers = _analyzers + [
            "//src/libraries/System.Runtime.InteropServices.JavaScript:JSImportGenerator",
        ]
    _analyzers = _dedupe(_analyzers)

    # Build suffix_srcs in MSBuild order: AssemblyInfo → Forwards
    # These go AFTER the resx-generated System.SR.cs (which is inserted by csharp_library)
    suffix_srcs = [":" + assembly_info_target]
    if generate_facades:
        forwards_cs = name + "/" + base_name + ".Forwards.cs"
        gen_facades(
            name = "facade_" + base_name,
            srcs = srcs,
            defines = defines,
            out = forwards_cs,
            ref_paths = deps,
            facade_contract_assembly = facade_contract_assembly,
            facade_omit_types = facade_omit_types,
        )
        suffix_srcs = suffix_srcs + [ ":facade_" + base_name ]

    csharp_library(
        name = name,
        out = base_name,
        srcs = srcs,
        suffix_srcs = suffix_srcs,
        defines = defines,
        deps = deps,
        # Suppress rules_dotnet's auto-generated AssemblyInfo (we provide our own via gen_assembly_info)
        assembly_version = "",
        cls_compliant = False,
        visibility = [ "//visibility:public" ],
        nullable = nullable,
        allow_unsafe_blocks = allow_unsafe_blocks,
        keyfile = _default_strong_name_keyfile(base_name, keyfile),
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        compiler_options = compiler_options,
        resx_file = resx_file,
        resource_class_name = resource_class_name,
        resources = resources,
        resource_logical_names = resource_logical_names,
        internals_visible_to = internals_visible_to,
        # Match MSBuild's langversion:preview
        langversion = "preview",
        nowarn = nowarn,
        # MSBuild sets GenerateDocumentationFile=true for IsSourceProject
        generate_documentation_file = generate_documentation_file,
        additionalfiles = _additionalfiles,
        analyzer_configs = _analyzer_configs,
        analyzers = _analyzers,
        include_runtime_async = include_runtime_async,
        **kwargs
    )

def _ref_impl_pair(ctx):
    return [
        ctx.attr.ref[DotnetAssemblyCompileInfo],
        ctx.attr.lib[DotnetAssemblyRuntimeInfo],
    ]

ref_impl_pair = rule(
    implementation = _ref_impl_pair,
    attrs = {
        "ref": attr.label(
            doc = "The reference assembly to use.",
            providers = [DotnetAssemblyCompileInfo],
        ),
        "lib": attr.label(
            doc = "The libraries to use.",
            providers = [DotnetAssemblyRuntimeInfo],
        ),
    }
)

def netcoreapp_impl_assembly(skip_locals_init = True, jsimport_generator = False, allow_unsafe_blocks = None, **kwargs):
    """Wrapper for impl_assembly for assemblies in the shared framework (IsNETCoreAppSrc).

    Defaults skip_locals_init to True (includes SkipLocalsInit.cs), matching
    MSBuild's Directory.Build.targets condition for IsNETCoreAppSrc assemblies.
    Defaults jsimport_generator to False because shared-framework builds use the
    live generator outputs instead of the targeting-pack analyzer bundle that
    carries JSImportGenerator for OOB/test builds.

    When skip_locals_init is True (the default), allow_unsafe_blocks is also
    defaulted to True because [module: SkipLocalsInit] requires /unsafe in C#,
    matching MSBuild which always sets AllowUnsafeBlocks for such assemblies.
    """
    if allow_unsafe_blocks == None:
        allow_unsafe_blocks = skip_locals_init
    impl_assembly(
        skip_locals_init = skip_locals_init,
        jsimport_generator = jsimport_generator,
        allow_unsafe_blocks = allow_unsafe_blocks,
        **kwargs
    )

def live_csharp_library(
    name,
    deps = [],
    nullable = "enable",
    compiler_options = [],
    treat_warnings_as_errors = False,
    **kwargs
):
    deps = deps + LIVE_REFPACK_DEPS
    target_frameworks = kwargs.pop("target_frameworks", [NETCOREAPP_CURRENT])

    # Match MSBuild compiler options for features (nullable/strict)
    compiler_options = compiler_options + [
        "/features:strict",
        "/features:nullablePublicOnly",
    ]

    csharp_library(
        name = name,
        deps = deps,
        nullable = nullable,
        langversion = "preview",
        compiler_options = compiler_options,
        disable_implicit_framework_refs = True,
        target_frameworks = target_frameworks,
        treat_warnings_as_errors = treat_warnings_as_errors,
        **kwargs
    )
