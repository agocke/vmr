load("@rules_dotnet//dotnet/private/rules/csharp:library.bzl", _base_csharp_library="csharp_library")
load("@rules_dotnet//dotnet/private/rules/csharp:binary.bzl", _base_csharp_binary="csharp_binary")
load("//eng/bazel:version.bzl", "PRODUCT_VERSION")

# The TFM that we're building
NETCOREAPP_CURRENT = "net10.0"
# The TFM used by our LKG SDK
NETCOREAPP_TOOL_CURRENT = "net10.0"

# ─── Centralized versioned NuGet repo names ──────────────────────────────────
# These are the Bazel repo names for version-pinned NuGet packages.
# When package versions change, update ONLY these constants + paket.dependencies,
# then re-run sync-paket.sh to regenerate paket/paket.main.bzl.
#
# The repo names follow the pattern: nuget.<lowercased-package-name>.v<version>

ARCADE_SDK_REPO = "nuget.microsoft.dotnet.arcade.sdk.v10.0.0-beta.26170.102"
GENFACADES_REPO = "nuget.microsoft.dotnet.genfacades.v10.0.0-beta.26170.102"
XUNIT_CONSOLE_RUNNER_REPO = "nuget.microsoft.dotnet.xunitconsolerunner.v2.9.3-beta.26170.102"
UNICODE_DATA_REPO = "nuget.system.private.runtime.unicodedata.v10.0.0-beta.25418.1"
ILLINK_TASKS_NET8_REPO = "nuget.microsoft.net.illink.tasks.v8.0.22"
ILLINK_TASKS_NET9_REPO = "nuget.microsoft.net.illink.tasks.v9.0.11"
ILLINK_TASKS_NET10_REPO = "nuget.microsoft.net.illink.tasks.v10.0.0"
MIBC_LINUX_X64_REPO = "nuget.optimization.linux-x64.mibc.runtime.v1.0.0-prerelease.26080.1"
MIBC_LINUX_ARM64_REPO = "nuget.optimization.linux-arm64.mibc.runtime.v1.0.0-prerelease.26080.1"

# Analyzer NuGet packages (from eng/Analyzers.targets)
CODEANALYSIS_ANALYZERS_REPO = "nuget.microsoft.codeanalysis.analyzers.v5.0.0-2.26170.102"
CODEANALYSIS_NETANALYZERS_REPO = "nuget.microsoft.codeanalysis.netanalyzers.v10.0.106"
CODEANALYSIS_CSHARP_CODESTYLE_REPO = "nuget.microsoft.codeanalysis.csharp.codestyle.v4.14.0"
DOTNET_CODEANALYSIS_REPO = "nuget.microsoft.dotnet.codeanalysis.v10.0.0-beta.26170.102"
STYLECOP_ANALYZERS_UNSTABLE_REPO = "nuget.stylecop.analyzers.unstable.v1.2.0.556"
XUNIT_ANALYZERS_REPO = "nuget.xunit.analyzers.v1.22.0"
STATICCS_REPO = "nuget.staticcs.v0.2.0"
BANNEDAPI_ANALYZERS_REPO = "nuget.microsoft.codeanalysis.bannedapianalyzers.v3.3.5-beta1.23270.2"

# ─── Pre-built labels for commonly used assets ───────────────────────────────
# SNK signing keys are copied to a stable path in //eng:snk/ (via copy_file)
# so that Arcade SDK version bumps don't invalidate the cache for every
# signed assembly.  See eng/BUILD.bazel for the copy rules.

MSFT_SNK = "//eng:snk/MSFT.snk"
OPEN_SNK = "//eng:snk/Open.snk"
ASPNETCORE_SNK = "//eng:snk/AspNetCore.snk"
ECMA_SNK = "//eng:snk/ECMA.snk"
SHAREDLIB1024_SNK = "//eng:snk/35MSSharedLib1024.snk"
SILVERLIGHT_SNK = "//eng:snk/SilverlightPlatformPublicKey.snk"
DEFAULT_RULESET = "//eng:Default.ruleset"

# MIBC PGO optimization data files from NuGet (matched to target architecture).
# MSBuild equivalent: eng/restore/optimizationData.targets selects the right
# optimization.<OS>-<ARCH>.mibc.runtime package, then crossgen-corelib.proj
# merges all .mibc files into StandardOptimizationData.mibc via dotnet-pgo.
# Crossgen2 accepts multiple -m: flags natively, so we skip the merge step.
_MIBC_FILE_NAMES = [
    "data/DotNet_Adhoc.mibc",
    "data/DotNet_FSharp.mibc",
    "data/DotNet_FirstTimeXP.mibc",
    "data/DotNet_HelloWorld.mibc",
    "data/DotNet_OrchardCore.mibc",
    "data/DotNet_TechEmpower.mibc",
]

MIBC_FILES = select({
    "@platforms//cpu:arm64": ["@{}//:{}".format(MIBC_LINUX_ARM64_REPO, f) for f in _MIBC_FILE_NAMES],
    "//conditions:default": ["@{}//:{}".format(MIBC_LINUX_X64_REPO, f) for f in _MIBC_FILE_NAMES],
})

# Label for the Roslyn compiler server persistent worker binary.
_SHARED_COMPILATION_WORKER = "@rules_dotnet//dotnet/private/tools/compiler_worker"

# Version constants matching eng/Versions.props
_MAJOR_VERSION = PRODUCT_VERSION.split(".")[0]
_MINOR_VERSION = PRODUCT_VERSION.split(".")[1]
_ASSEMBLY_VERSION = _MAJOR_VERSION + "." + _MINOR_VERSION + ".0.0"
FILE_VERSION = "42.42.42.42424"
_FILE_VERSION = FILE_VERSION
INFORMATIONAL_VERSION = PRODUCT_VERSION + "-dev"
_INFORMATIONAL_VERSION = INFORMATIONAL_VERSION
CI_INFORMATIONAL_VERSION = select({
    "//:ci_build": PRODUCT_VERSION + "-ci",
    "//conditions:default": PRODUCT_VERSION + "-dev",
})

def _gen_resx_source_impl(ctx):
    resource_name = ctx.attr.resource_name if ctx.attr.resource_name else ("FxResources.%s.SR" % ctx.attr.assembly_name)
    args = [
        "--output-path=%s" % ctx.outputs.out.path,
        "--resource-name=%s" % resource_name,
        "--resource-file=%s" % ctx.file.resx_file.path,
    ]
    if ctx.attr.resource_class_name:
        args.append("--resource-class-name=%s" % ctx.attr.resource_class_name)
    if not ctx.attr.include_default_values:
        args.append("--include-default-values=false")
    if ctx.attr.omit_getresourcestring:
        args.append("--omit-getresourcestring")
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = [ctx.file.resx_file],
        outputs = [ctx.outputs.out],
        arguments = args,
    )

gen_resx_source = rule(
    implementation = _gen_resx_source_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "assembly_name": attr.string(mandatory = True),
        "resource_name": attr.string(mandatory = False, default = ""),
        "resource_class_name": attr.string(mandatory = False, default = ""),
        # MSBuild only includes default resource values in Debug builds
        # (eng/resources.targets:11).  Default to False (Release) so that
        # direct callers match Release MSBuild output.  The csharp_library
        # wrapper passes an explicit select() to vary by libs_config.
        "include_default_values": attr.bool(default = False),
        "omit_getresourcestring": attr.bool(default = False),
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

def _gen_pnse_source_impl(ctx):
    args = [
        "--output-path=%s" % ctx.outputs.out.path,
        "--message=%s" % ctx.attr.message,
    ]
    if ctx.attr.api_exclusion_list:
        args.append("--api-exclusion-list=%s" % ctx.file.api_exclusion_list.path)
    inputs = []
    for src in ctx.files.srcs:
        args.append("--source=%s" % src.path)
        inputs.append(src)
    if ctx.file.api_exclusion_list:
        inputs.append(ctx.file.api_exclusion_list)
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = inputs,
        outputs = [ctx.outputs.out],
        arguments = args,
    )

gen_pnse_source = rule(
    implementation = _gen_pnse_source_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "srcs": attr.label_list(mandatory = True, allow_files = True),
        "message": attr.string(mandatory = True),
        "api_exclusion_list": attr.label(
            mandatory = False,
            allow_single_file = True,
        ),
        "_exe": attr.label(
            default = Label("//src/tools/bazel/GenNotSupportedSource:GenNotSupportedSource"),
            cfg = "exec",
            executable = True,
        ),
    },
)

def _resgen_impl(ctx):
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = [ctx.file.resx_file],
        outputs = [ctx.outputs.out],
        arguments = [
            "--src-path=%s" % ctx.file.resx_file.path,
            "--out-path=%s" % ctx.outputs.out.path,
        ],
    )

resgen = rule(
    implementation = _resgen_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "resx_file": attr.label(
            mandatory = True,
            allow_single_file = True,
        ),
        "_exe": attr.label(
            default = Label("//src/tools/bazel/ResGen:ResGen"),
            cfg = "exec",
            executable = True,
        ),
    }
)

def _quote_csharp_string_cstyle(value, indent):
    """Replicate CSharpCodeGenerator.QuoteSnippetStringCStyle: escape chars and split at 80-char boundaries."""
    MAX_LINE_LENGTH = 80
    escape_map = {"\r": "\\r", "\t": "\\t", "\"": "\\\"", "'": "\\'", "\\": "\\\\", "\0": "\\0", "\n": "\\n"}
    parts = []
    buf = ["\""]
    is_multiline = False
    for i in range(len(value)):
        ch = value[i]
        esc = escape_map.get(ch, None)
        if esc != None:
            buf.append(esc)
        else:
            buf.append(ch)
        if i > 0 and (i % MAX_LINE_LENGTH) == 0 and i != len(value) - 1:
            buf.append("\" +")
            parts.append("".join(buf))
            buf = [indent + "\""]
            is_multiline = True
    buf.append("\"")
    parts.append("".join(buf))
    result = "\n".join(parts)
    if is_multiline:
        result = "(" + result + ")"
    return result

def _quote_csharp_string_verbatim(value):
    """Replicate CSharpCodeGenerator.QuoteSnippetStringVerbatimStyle."""
    return '@"' + value.replace('"', '""') + '"'

def _quote_csharp_string(value, indent):
    """Replicate CSharpCodeGenerator.QuoteSnippetString: pick C-style vs verbatim based on length."""
    if len(value) < 256 or len(value) > 1500:
        return _quote_csharp_string_cstyle(value, indent)
    return _quote_csharp_string_verbatim(value)

def _gen_assembly_info_impl(ctx):
    """Generate an AssemblyInfo.cs matching MSBuild's WriteCodeFragment output."""
    lines = [
        "//------------------------------------------------------------------------------",
        "// <auto-generated>",
        "//     This code was generated by a tool.",
        "//",
        "//     Changes to this file may cause incorrect behavior and will be lost if",
        "//     the code is regenerated.",
        "// </auto-generated>",
        "//------------------------------------------------------------------------------",
        "",
        "using System;",
        "using System.Reflection;",
        "",
    ]

    assembly_name = ctx.attr.assembly_name

    if ctx.attr.not_supported:
        lines.append('[assembly: System.Reflection.AssemblyMetadata("NotSupported", "True")]')

    if ctx.attr.include_serviceable:
        lines.append('[assembly: System.Reflection.AssemblyMetadata("Serviceable", "True")]')
        lines.append('[assembly: System.Reflection.AssemblyMetadata("PreferInbox", "True")]')
        lines.append('[assembly: System.Reflection.AssemblyDefaultAliasAttribute("%s")]' % assembly_name)

    if ctx.attr.include_neutral_resources_language:
        lines.append('[assembly: System.Resources.NeutralResourcesLanguageAttribute("en-US")]')

    if ctx.attr.cls_compliant:
        lines.append("[assembly: CLSCompliantAttribute(true)]")

    if ctx.attr.is_trimmable:
        lines.append('[assembly: System.Reflection.AssemblyMetadata("IsTrimmable", "True")]')

    if ctx.attr.is_aot_compatible:
        lines.append('[assembly: System.Reflection.AssemblyMetadata("IsAotCompatible", "True")]')

    # Short-form platform attributes (from _SupportedOSPlatforms MSBuild property) go here
    for platform in ctx.attr.supported_os_platforms_short:
        lines.append('[assembly: System.Runtime.Versioning.SupportedOSPlatform("%s")]' % platform)
    for platform in ctx.attr.unsupported_os_platforms:
        lines.append('[assembly: System.Runtime.Versioning.UnsupportedOSPlatform("%s")]' % platform)

    if ctx.attr.include_dll_safe_search_path < 0:
        _include_dll_safe_search = False
        for d in ctx.attr.ref_deps:
            dep_str = str(d.label)
            if "System.Private.CoreLib" in dep_str or "System.Runtime.InteropServices" in dep_str:
                _include_dll_safe_search = True
                break
    else:
        _include_dll_safe_search = ctx.attr.include_dll_safe_search_path > 0

    if _include_dll_safe_search:
        lines.append("[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute(System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory | System.Runtime.InteropServices.DllImportSearchPath.System32)]")

    lines.append('[assembly: System.Reflection.AssemblyCompanyAttribute("Microsoft Corporation")]')

    if ctx.attr.assembly_configuration:
        lines.append('[assembly: System.Reflection.AssemblyConfigurationAttribute("%s")]' % ctx.attr.assembly_configuration)

    lines.append('[assembly: System.Reflection.AssemblyCopyrightAttribute("© Microsoft Corporation. All rights reserved.")]')
    if ctx.attr.include_description:
        desc = ctx.attr.assembly_description if ctx.attr.assembly_description else assembly_name
        # MSBuild's WriteCodeFragment uses CSharpCodeGenerator.QuoteSnippetString which
        # picks C-style (escaped, 80-char chunked) or verbatim (@"") based on length.
        # Indent is 4 spaces (1 indent level in CodeDOM default).
        desc_str = _quote_csharp_string(desc, "    ")
        lines.append('[assembly: System.Reflection.AssemblyDescriptionAttribute(%s)]' % desc_str)
    lines.append('[assembly: System.Reflection.AssemblyFileVersionAttribute("%s")]' % ctx.attr.file_version)
    lines.append('[assembly: System.Reflection.AssemblyInformationalVersionAttribute("%s")]' % ctx.attr.informational_version)
    lines.append('[assembly: System.Reflection.AssemblyProductAttribute("Microsoft® .NET")]')
    lines.append('[assembly: System.Reflection.AssemblyTitleAttribute("%s")]' % assembly_name)
    lines.append('[assembly: System.Reflection.AssemblyVersionAttribute("%s")]' % ctx.attr.assembly_version)
    lines.append('[assembly: System.Reflection.AssemblyMetadataAttribute("RepositoryUrl", "https://github.com/dotnet/runtime")]')

    # Long-form platform attributes (from TFM-based targeting) go after RepositoryUrl
    for platform in ctx.attr.supported_os_platforms:
        lines.append('[assembly: System.Runtime.Versioning.SupportedOSPlatformAttribute("%s")]' % platform)

    # InternalsVisibleTo attributes — MSBuild puts these in the AssemblyInfo file
    for ivt in ctx.attr.internals_visible_to:
        lines.append('[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(@"%s")]' % ivt)

    lines.append("")
    lines.append("// Generated by the MSBuild WriteCodeFragment class.")
    lines.append("")
    lines.append("")

    ctx.actions.write(ctx.outputs.out, "\n".join(lines))

gen_assembly_info = rule(
    implementation = _gen_assembly_info_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "assembly_name": attr.string(mandatory = True),
        "assembly_version": attr.string(default = _ASSEMBLY_VERSION),
        "file_version": attr.string(default = _FILE_VERSION),
        "informational_version": attr.string(default = _INFORMATIONAL_VERSION),
        "cls_compliant": attr.bool(default = True),
        "is_trimmable": attr.bool(default = True),
        "is_aot_compatible": attr.bool(default = True),
        "include_serviceable": attr.bool(default = True),
        "include_dll_safe_search_path": attr.int(
            default = -1,
            doc = "Emit DefaultDllImportSearchPathsAttribute. " +
                  "-1 (default) = auto-detect from ref_deps, 0 = no, 1 = yes.",
        ),
        "ref_deps": attr.label_list(
            default = [],
            doc = "Assembly deps, used only to auto-detect include_dll_safe_search_path.",
        ),
        "include_neutral_resources_language": attr.bool(default = True),
        "assembly_description": attr.string(default = ""),
        "include_description": attr.bool(default = True),
        "assembly_configuration": attr.string(default = ""),
        "not_supported": attr.bool(default = False),
        "supported_os_platforms": attr.string_list(default = []),
        "supported_os_platforms_short": attr.string_list(default = []),
        "unsupported_os_platforms": attr.string_list(default = []),
        "internals_visible_to": attr.string_list(default = []),
    },
)

def _gen_target_framework_attrs_impl(ctx):
    """Generate the .NETCoreApp,Version=vX.Y.AssemblyAttributes.cs file.

    Also supports .NETStandard when framework_type is set to 'netstandard'.
    """
    if ctx.attr.framework_type == "netstandard":
        moniker = ".NETStandard"
        display = ".NET Standard"
    else:
        moniker = ".NETCoreApp"
        display = ".NET"
    content = '// <autogenerated />\nusing System;\nusing System.Reflection;\n[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute("%s,Version=v%s.%s", FrameworkDisplayName = "%s %s.%s")]\n' % (
        moniker, ctx.attr.major_version, ctx.attr.minor_version,
        display, ctx.attr.major_version, ctx.attr.minor_version,
    )
    ctx.actions.write(ctx.outputs.out, content)

gen_target_framework_attrs = rule(
    implementation = _gen_target_framework_attrs_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "major_version": attr.string(default = _MAJOR_VERSION),
        "minor_version": attr.string(default = _MINOR_VERSION),
        "framework_type": attr.string(
            default = "netcoreapp",
            doc = "Framework type: 'netcoreapp' (default) or 'netstandard'.",
            values = ["netcoreapp", "netstandard"],
        ),
    },
)

def _gen_illink_substitutions_impl(ctx):
    """Generate an ILLink.Substitutions.xml for resource-key stripping."""
    assembly_name = ctx.attr.assembly_name
    resource_name = ctx.attr.resource_name
    # UTF-8 BOM (U+FEFF) to match MSBuild output
    content = (
        '﻿' +
        '<linker>\n' +
        '  <assembly fullname="{asm}" feature="System.Resources.UseSystemResourceKeys" featurevalue="true">\n' +
        '    <resource name="{res}.resources" action="remove" />\n' +
        '    <type fullname="System.SR">\n' +
        '      <method signature="System.Boolean UsingResourceKeys()" body="stub" value="true" />\n' +
        '      <method signature="System.Boolean GetUsingResourceKeysSwitchValue()" body="stub" value="true" />\n' +
        '    </type>\n' +
        '  </assembly>\n' +
        '  <assembly fullname="{asm}" feature="System.Resources.UseSystemResourceKeys" featurevalue="false">\n' +
        '    <type fullname="System.SR">\n' +
        '      <method signature="System.Boolean UsingResourceKeys()" body="stub" value="false" />\n' +
        '      <method signature="System.Boolean GetUsingResourceKeysSwitchValue()" body="stub" value="false" />\n' +
        '    </type>\n' +
        '  </assembly>\n' +
        '</linker>'
    ).format(asm = assembly_name, res = resource_name)
    ctx.actions.write(ctx.outputs.out, content)

gen_illink_substitutions = rule(
    implementation = _gen_illink_substitutions_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "assembly_name": attr.string(mandatory = True),
        "resource_name": attr.string(mandatory = True),
    },
)

def csharp_library(
    name,
    srcs = [],
    out = None,
    resx_file = None,
    resource_name = None,
    resource_class_name = None,
    omit_getresourcestring = False,
    include_default_values = None,
    resources = [],
    resource_logical_names = {},
    nowarn = [],
    suffix_srcs = [],
    use_shared_compilation = True,
    compiler_options = [],
    compile_data = [],
    additionalfiles = [],
    analyzer_configs = [],
    analyzers = [],
    treat_warnings_as_errors = True,
    warnings_not_as_errors = [],
    generate_documentation_file = False,
    msbuild_analyzer_config = "none",
    include_default_ruleset = True,
    include_syslib_warnaserror = True,
    include_library_nowarn = True,
    skip_analyzers = False,
    interceptors_namespaces = None,
    editorconfig_name = None,
    extra_editorconfig_content = "",
    **kwargs
):
    if out == None:
        out = name
    if editorconfig_name == None:
        editorconfig_name = out

    if resx_file != None:
        _resource_name = resource_name if resource_name else "FxResources.%s.SR" % out
        resgen_target = "resgen_" + name
        # Use just the class name portion as the filename so that
        # map_resource_arg (which prepends the DLL name) produces the
        # correct manifest resource name.
        resgen_out = _resource_name.rsplit(".", 1)[-1] + ".resources" if resource_name else "FxResources.%s.SR.resources" % out
        resgen(
            name = resgen_target,
            out = resgen_out,
            resx_file = resx_file,
        )
        resources = resources + [ ":" + resgen_target ]
        # Use resource_logical_names to set the manifest name directly,
        # matching MSBuild's EmbeddedResource LogicalName metadata.
        resource_logical_names = dict(resource_logical_names)
        resource_logical_names[resgen_out] = _resource_name + ".resources"

        resx_target = "resx_" + name
        # MSBuild only includes default resource values in Debug builds
        # (eng/resources.targets:11). Match that behavior with a select().
        _include_default_values = include_default_values if include_default_values != None else select({
            "//:libs_debug": True,
            "//conditions:default": False,
        })
        gen_resx_source(
            name = resx_target,
            out = name + "/System.SR.cs",
            assembly_name = out,
            resource_name = _resource_name,
            resource_class_name = resource_class_name if resource_class_name else "",
            resx_file = resx_file,
            include_default_values = _include_default_values,
            omit_getresourcestring = omit_getresourcestring,
        )
        srcs = srcs + [ ":" + resx_target ]

    # suffix_srcs are appended after resx source, matching MSBuild's ordering
    # where AssemblyInfo.cs and Forwards.cs come after generated SR sources.
    srcs = srcs + suffix_srcs

    if msbuild_analyzer_config not in ["none", "style", "source"]:
        fail("msbuild_analyzer_config must be one of: none, style, source")

    if msbuild_analyzer_config != "none":
        if msbuild_analyzer_config == "source":
            disabled_analyzers_target = "disabled_analyzers_" + name
            native.genrule(
                name = disabled_analyzers_target,
                outs = [name + "/disabledAnalyzers.config"],
                cmd = ": > \"$@\"",
            )
            additionalfiles = additionalfiles + [":" + disabled_analyzers_target]

        editorconfig_target = "editorconfig_" + name
        _editorconfig_global_level = "global_level = 1\n" if extra_editorconfig_content else ""
        native.genrule(
            name = editorconfig_target,
            outs = [name + "/" + editorconfig_name + ".GeneratedMSBuildEditorConfig.editorconfig"],
            cmd = """cat >"$@" <<'EOF'
is_global = true
{global_level}build_property.InformationalVersion = {version}
build_property._SupportedPlatformList = Linux,macOS,Windows,Android,iOS,tvOS,macCatalyst,browser,wasi,illumos,Solaris,Haiku,Unix,FreeBSD
{extra}
EOF""".format(version = PRODUCT_VERSION, extra = extra_editorconfig_content, global_level = _editorconfig_global_level),
        )

        _msbuild_analyzer_configs = [
            ":" + editorconfig_target,
            "//src/tools/bazel:analysislevelstyle_default.globalconfig",
            "//:.editorconfig",
        ]
        if msbuild_analyzer_config == "source":
            _msbuild_analyzer_configs = _msbuild_analyzer_configs + [
                "//eng:CodeAnalysis.src.globalconfig",
                "//src/tools/bazel:analysislevel_11_default.globalconfig",
            ]

        analyzer_configs = analyzer_configs + _msbuild_analyzer_configs

        if resx_file != None and resx_file not in additionalfiles:
            additionalfiles = additionalfiles + [resx_file]

    # rules_dotnet explicitly passes /nullable:disable for assemblies with
    # nullable="disable", but MSBuild omits /nullable entirely (disable is
    # the default).  The explicit flag triggers CS8632 on nullable
    # annotations (e.g. string?) in shared source files like
    # Common/src/System/SR.cs.  Suppress it for those assemblies to match.
    # This is a rules_dotnet behavioral difference, not a source-level one.
    _nullable_nowarn = ["CS8632"] if kwargs.get("nullable") == "disable" else []

    # In CI mode, construct a pathmap entry that maps the Bazel PDB output
    # directory to MSBuild's CI-normalized equivalent.  The pathmap key is
    # the stable portion of the output path (after bazel-out/{hash}/bin/).
    # At execution time the rules_dotnet worker derives the unstable bin
    # prefix from the /pdb: argument and prepends it.
    _pkg = native.package_name()
    _pathmap_key = "%s/%s/%s" % (_pkg, name, NETCOREAPP_CURRENT)
    _pathmap_value = "/_/artifacts/obj/%s/Release/%s" % (out, NETCOREAPP_CURRENT)

    # Derive signing flags from keyfile. MSBuild only emits /delaysign-
    # and /publicsign when SignAssembly is true (i.e. a keyfile is present).
    # Open.snk and AspNetCore.snk are full key pairs → /publicsign-.
    # MSFT.snk, ECMA.snk, etc. are public-only → /publicsign+.
    _keyfile = kwargs.get("keyfile")
    _signing_flags = []
    if _keyfile != None:
        _publicsign = "/publicsign-" if _keyfile in (OPEN_SNK, ASPNETCORE_SNK) else "/publicsign+"
        _signing_flags = ["/delaysign-", _publicsign]

    _compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/platform:AnyCPU",
        "/features:strict",
        "/features:nullablePublicOnly",
        "/noconfig",
        # rules_dotnet restricts warning_level to [0..5] so we use
        # compiler_options to emit /warn:9999, matching MSBuild's
        # WarningLevel=9999.
        "/warn:9999",
        # MSBuild SDK defaults that csc always receives from the .NET SDK.
        "/fullpaths",
        "/errorreport:prompt",
    ] + _signing_flags
    if skip_analyzers:
        _compiler_options = _compiler_options + ["/skipanalyzers+"]
    if include_syslib_warnaserror:
        _compiler_options = _compiler_options + [
            # Arcade SDK promotes SYSLIB0011 to an error.  Test-support
            # assemblies that don't flow through Arcade's targets should
            # set include_syslib_warnaserror = False.
            "/warnaserror+:SYSLIB0011",
        ]
    if interceptors_namespaces != None:
        _compiler_options = _compiler_options + [
            "/features:InterceptorsNamespaces=" + interceptors_namespaces,
        ]
    if include_default_ruleset:
        _compiler_options = _compiler_options + [
            # Microsoft.DotNet.CodeAnalysis package supplies this ruleset to
            # source-build projects. compile_data makes it available in the sandbox.
            "/ruleset:eng/Default.ruleset",
        ]

    _base_csharp_library(
        name = name,
        srcs = srcs,
        out = out,
        resources = resources,
        resource_logical_names = resource_logical_names,
        use_shared_compilation = use_shared_compilation,
        shared_compilation_worker = _SHARED_COMPILATION_WORKER if use_shared_compilation else None,
        nowarn = nowarn + _nullable_nowarn + [
            "CS1701",
            # Arcade SDK global NoWarn (Microsoft.DotNet.Arcade.Sdk targets)
            "CS1702",
            "NU5105",
        ] + ([
            # src/libraries/Directory.Build.props global NoWarn — not present
            # in NativeAOT tool projects under src/coreclr/tools/.
            "CS8500",
            "CS8969",
            "CS1705",
            "IDE0060",
            "IDE0100",
        ] if include_library_nowarn else []),
        # Match MSBuild's TreatWarningsAsErrors=true from Directory.Build.props.
        treat_warnings_as_errors = treat_warnings_as_errors,
        # Match MSBuild's WarningsNotAsErrors from Directory.Build.props
        # (NuGet audit warnings demoted from errors for non-official builds).
        # rules_dotnet forbids warnings_not_as_errors when treat_warnings_as_errors
        # is false, so only add them when warnaserror is enabled.
        warnings_not_as_errors = (warnings_not_as_errors + [
            "NU1901",
            "NU1902",
            "NU1903",
            "NU1904",
        ]) if treat_warnings_as_errors else warnings_not_as_errors,
        # MSBuild only generates XML doc files for library source assemblies
        # (GenerateDocumentationFile=true in src/libraries/Directory.Build.props
        # when IsSourceProject=true).  Default to False to match MSBuild.
        generate_documentation_file = generate_documentation_file,
        # Match MSBuild's csc defaults. rules_dotnet emits its own baseline
        # flags, so keep these late in the command line for last-wins behavior.
        compiler_options = _compiler_options,
        compile_data = compile_data + ([DEFAULT_RULESET] if include_default_ruleset else []),
        additionalfiles = additionalfiles,
        analyzer_configs = analyzer_configs,
        analyzers = analyzers,
        # In CI mode, normalize PDB paths to match MSBuild's CI layout
        # (ContinuousIntegrationBuild=true → DeterministicSourcePaths → PathMap).
        pathmap = select({
            "//:ci_build": {_pathmap_key: _pathmap_value},
            "//conditions:default": {},
        }),
        **kwargs
    )

def csharp_binary(
    name,
    srcs = [],
    nowarn = [],
    use_shared_compilation = True,
    compiler_options = [],
    compile_data = [],
    additionalfiles = [],
    analyzer_configs = [],
    analyzers = [],
    treat_warnings_as_errors = True,
    warnings_not_as_errors = [],
    generate_documentation_file = False,
    msbuild_analyzer_config = "none",
    include_default_ruleset = True,
    include_syslib_warnaserror = True,
    include_library_nowarn = True,
    skip_analyzers = False,
    interceptors_namespaces = None,
    editorconfig_name = None,
    extra_editorconfig_content = "",
    **kwargs
):
    if editorconfig_name == None:
        editorconfig_name = name

    if msbuild_analyzer_config not in ["none", "style", "source"]:
        fail("msbuild_analyzer_config must be one of: none, style, source")

    if msbuild_analyzer_config != "none":
        if msbuild_analyzer_config == "source":
            disabled_analyzers_target = "disabled_analyzers_" + name
            native.genrule(
                name = disabled_analyzers_target,
                outs = [name + "/disabledAnalyzers.config"],
                cmd = ": > \"$@\"",
            )
            additionalfiles = additionalfiles + [":" + disabled_analyzers_target]

        editorconfig_target = "editorconfig_" + name
        _editorconfig_global_level = "global_level = 1\n" if extra_editorconfig_content else ""
        native.genrule(
            name = editorconfig_target,
            outs = [name + "/" + editorconfig_name + ".GeneratedMSBuildEditorConfig.editorconfig"],
            cmd = """cat >"$@" <<'EOF'
is_global = true
{global_level}build_property.InformationalVersion = {version}
build_property._SupportedPlatformList = Linux,macOS,Windows,Android,iOS,tvOS,macCatalyst,browser,wasi,illumos,Solaris,Haiku,Unix,FreeBSD
{extra}
EOF""".format(version = PRODUCT_VERSION, extra = extra_editorconfig_content, global_level = _editorconfig_global_level),
        )

        _msbuild_analyzer_configs = [
            ":" + editorconfig_target,
            "//src/tools/bazel:analysislevelstyle_default.globalconfig",
            "//:.editorconfig",
        ]
        if msbuild_analyzer_config == "source":
            _msbuild_analyzer_configs = _msbuild_analyzer_configs + [
                "//eng:CodeAnalysis.src.globalconfig",
                "//src/tools/bazel:analysislevel_11_default.globalconfig",
            ]

        analyzer_configs = analyzer_configs + _msbuild_analyzer_configs

    # rules_dotnet explicitly passes /nullable:disable for assemblies with
    # nullable="disable", but MSBuild omits /nullable entirely (disable is
    # the default).  The explicit flag triggers CS8632 on nullable
    # annotations (e.g. string?) in shared source files.
    _nullable_nowarn = ["CS8632"] if kwargs.get("nullable") == "disable" else []

    _keyfile = kwargs.get("keyfile")
    _signing_flags = []
    if _keyfile != None:
        _publicsign = "/publicsign-" if _keyfile in (OPEN_SNK, ASPNETCORE_SNK) else "/publicsign+"
        _signing_flags = ["/delaysign-", _publicsign]

    _compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/platform:AnyCPU",
        "/features:strict",
        "/features:nullablePublicOnly",
        "/noconfig",
        # rules_dotnet restricts warning_level to [0..5] so we use
        # compiler_options to emit /warn:9999, matching MSBuild's
        # WarningLevel=9999.
        "/warn:9999",
        # MSBuild SDK defaults that csc always receives from the .NET SDK.
        "/fullpaths",
        "/errorreport:prompt",
    ] + _signing_flags + (["/warnaserror+:SYSLIB0011"] if include_syslib_warnaserror else [])
    if skip_analyzers:
        _compiler_options = _compiler_options + ["/skipanalyzers+"]
    if interceptors_namespaces != None:
        _compiler_options = _compiler_options + [
            "/features:InterceptorsNamespaces=" + interceptors_namespaces,
        ]
    if include_default_ruleset:
        _compiler_options = _compiler_options + [
            # Microsoft.DotNet.CodeAnalysis package supplies this ruleset to
            # source-build projects. compile_data makes it available in the sandbox.
            "/ruleset:eng/Default.ruleset",
        ]

    _base_csharp_binary(
        name = name,
        srcs = srcs,
        use_shared_compilation = use_shared_compilation,
        shared_compilation_worker = _SHARED_COMPILATION_WORKER if use_shared_compilation else None,
        nowarn = nowarn + _nullable_nowarn + [
            "CS1701",
            # Arcade SDK global NoWarn (Microsoft.DotNet.Arcade.Sdk targets)
            "CS1702",
            "NU5105",
        ] + ([
            # src/libraries/Directory.Build.props global NoWarn — not present
            # in NativeAOT tool projects under src/coreclr/tools/.
            "CS8500",
            "CS8969",
            "CS1705",
            "IDE0060",
            "IDE0100",
        ] if include_library_nowarn else []),
        # Match MSBuild's TreatWarningsAsErrors=true from Directory.Build.props.
        treat_warnings_as_errors = treat_warnings_as_errors,
        # Match MSBuild's WarningsNotAsErrors from Directory.Build.props
        # (NuGet audit warnings demoted from errors for non-official builds).
        # rules_dotnet forbids warnings_not_as_errors when treat_warnings_as_errors
        # is false, so only add them when warnaserror is enabled.
        warnings_not_as_errors = (warnings_not_as_errors + [
            "NU1901",
            "NU1902",
            "NU1903",
            "NU1904",
        ]) if treat_warnings_as_errors else warnings_not_as_errors,
        # MSBuild does not generate XML doc files for EXE projects by default.
        generate_documentation_file = generate_documentation_file,
        # Match MSBuild's csc defaults. rules_dotnet emits its own baseline
        # flags, so keep these late in the command line for last-wins behavior.
        compiler_options = _compiler_options,
        compile_data = compile_data + ([DEFAULT_RULESET] if include_default_ruleset else []),
        additionalfiles = additionalfiles,
        analyzer_configs = analyzer_configs,
        analyzers = analyzers,
        **kwargs
    )
