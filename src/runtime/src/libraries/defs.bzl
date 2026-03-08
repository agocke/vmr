
load(
    "//:defs.bzl",
    "NETCOREAPP_CURRENT",
    "csharp_library",
    "gen_assembly_info",
    "gen_illink_substitutions",
    "gen_target_framework_attrs",
)

load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",
    "DotnetTargetingPackInfo",
)

load("@bazel_skylib//rules:run_binary.bzl", "run_binary")

LIVE_NETCOREAPP_DEPS = [
#    "//src/libraries:live_System.Runtime",
#    "//src/libraries:live_System.Console",
#    "//src/libraries/System.Runtime.InteropServices:live_System.Runtime.InteropServices",
#    "//src/libraries:live_Microsoft.Win32.Primitives",
#    "//src/libraries:live_System.Collections",
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

# Convenience macro for defining a ref assembly for the NetCoreApp framework.
def netcoreapp_ref_assembly(
    name,
    srcs,
    deps = [],
    nowarn = [],
    compiler_options = [],
    keyfile = None,
    cls_compliant = True,
    assembly_version = "10.0.0.0",
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
        keyfile = keyfile if keyfile else "@nuget.microsoft.dotnet.arcade.sdk.v10.0.0-beta.26102.102//:tools/snk/MSFT.snk",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        nowarn = nowarn,
        compiler_options = compiler_options,
        ref_assembly = True,
        debug_type = "none",
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
            default = Label("//src/tools/GenFacades:GenFacades"),
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
            default = Label("//src/tools/GenerateResxSource:GenerateResxSource"),
            cfg = "exec",
            executable = True,
        ),
    }
)

def netcoreapp_impl_assembly(
    name,
    srcs = [],
    deps = [],
    defines = [],
    compiler_options = [],
    generate_facades = False,
    facade_contract_assembly = None,
    facade_omit_types = [],
    resx_file = None,
    exclude_sr = False,
    keyfile = None,
    cls_compliant = True,
    is_trimmable = True,
    is_aot_compatible = True,
    allow_unsafe_blocks = True,
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
    skip_cs1591 = False,
    pnse = False,
    multitarget = False,
    supported_os_platforms = [],
    supported_os_platforms_short = [],
    unsupported_os_platforms = [],
    **kwargs
):
    base_name = name[len("impl_"):]

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

    # MSBuild's intellisense packaging adds CS1591 to most source assemblies
    # via SkipIntellisenseNoWarnCS1591. Assemblies that don't get CS1591:
    # - skip_cs1591=True: 4 libraries that enforce doc comments AND pure shim
    #   assemblies (type forwarders in src/libraries/shims/) which have
    #   SkipIntellisenseNoWarnCS1591=true via packaging infrastructure.
    if not skip_cs1591:
        nowarn = nowarn + ["CS1591"]

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

    # Multi-targeted assemblies that also target netstandard/net4x suppress
    # warnings about APIs that don't exist in older TFMs.
    if multitarget:
        nowarn = nowarn + [
            "CA1510", "CA1511", "CA1512", "CA1513",
            "CA1845", "CA1846", "CA1847", "CP0003",
        ]

    if not exclude_sr:
        srcs = srcs + [
            "//src/libraries/Common:src/System/SR.cs",
        ]

    # Add SkipLocalsInit.cs for non-shim assemblies (matches MSBuild's inclusion
    # via Common items). Pure shim assemblies (type forwarders with both
    # exclude_sr=True and skip_cs1591=True) don't include it.
    if not (exclude_sr and skip_cs1591):
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
    # MSBuild adds DefaultDllImportSearchPathsAttribute when any ReferencePath
    # has Filename of System.Runtime.InteropServices or System.Private.CoreLib.
    # Auto-detect from deps if not explicitly specified.
    if include_dll_safe_search_path == None:
        _include_dll_safe_search = False
        for d in deps:
            dep_str = str(d)
            if "System.Private.CoreLib" in dep_str or "System.Runtime.InteropServices" in dep_str:
                _include_dll_safe_search = True
                break
    else:
        _include_dll_safe_search = include_dll_safe_search_path

    assembly_info_target = "assemblyinfo_" + base_name
    assembly_info_kwargs = {}
    if assembly_version != None:
        assembly_info_kwargs["assembly_version"] = assembly_version
    if assembly_description:
        assembly_info_kwargs["assembly_description"] = assembly_description
    gen_assembly_info(
        name = assembly_info_target,
        out = name + "/" + base_name + ".AssemblyInfo.cs",
        assembly_name = base_name,
        cls_compliant = cls_compliant,
        is_trimmable = is_trimmable,
        is_aot_compatible = is_aot_compatible,
        include_dll_safe_search_path = _include_dll_safe_search,
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
        keyfile = keyfile if keyfile else "@nuget.microsoft.dotnet.arcade.sdk.v10.0.0-beta.26102.102//:tools/snk/MSFT.snk",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        compiler_options = compiler_options,
        resx_file = resx_file,
        resources = resources,
        resource_logical_names = resource_logical_names,
        internals_visible_to = internals_visible_to,
        # Match MSBuild's langversion:preview
        langversion = "preview",
        nowarn = nowarn,
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

def live_csharp_library(
    name,
    deps = [],
    nullable = "enable",
    compiler_options = [],
    **kwargs
):
    deps = deps + LIVE_REFPACK_DEPS

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
        target_frameworks = [ NETCOREAPP_CURRENT ],
        **kwargs
    )