load("@rules_dotnet//dotnet/private/rules/csharp:library.bzl", _base_csharp_library="csharp_library")
load("@rules_dotnet//dotnet/private/rules/csharp:binary.bzl", _base_csharp_binary="csharp_binary")
load("//eng/bazel:version.bzl", "PRODUCT_VERSION")

# The TFM that we're building
NETCOREAPP_CURRENT = "net10.0"
# The TFM used by our LKG SDK
NETCOREAPP_TOOL_CURRENT = "net10.0"

# Label for the Roslyn compiler server persistent worker binary.
_SHARED_COMPILATION_WORKER = "@rules_dotnet//dotnet/private/tools/compiler_worker"

# Version constants matching eng/Versions.props
_MAJOR_VERSION = PRODUCT_VERSION.split(".")[0]
_MINOR_VERSION = PRODUCT_VERSION.split(".")[1]
_ASSEMBLY_VERSION = _MAJOR_VERSION + "." + _MINOR_VERSION + ".0.0"
_FILE_VERSION = "42.42.42.42424"
_INFORMATIONAL_VERSION = PRODUCT_VERSION + "-dev"

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
        "include_default_values": attr.bool(default = True),
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
        lines.append('[assembly: System.Resources.NeutralResourcesLanguage("en-US")]')

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

    if ctx.attr.include_dll_safe_search_path:
        lines.append("[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute(System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory | System.Runtime.InteropServices.DllImportSearchPath.System32)]")

    lines.append('[assembly: System.Reflection.AssemblyCompanyAttribute("Microsoft Corporation")]')
    lines.append('[assembly: System.Reflection.AssemblyCopyrightAttribute("© Microsoft Corporation. All rights reserved.")]')
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
        "include_dll_safe_search_path": attr.bool(default = False),
        "include_neutral_resources_language": attr.bool(default = True),
        "assembly_description": attr.string(default = ""),
        "not_supported": attr.bool(default = False),
        "supported_os_platforms": attr.string_list(default = []),
        "supported_os_platforms_short": attr.string_list(default = []),
        "unsupported_os_platforms": attr.string_list(default = []),
    },
)

def _gen_target_framework_attrs_impl(ctx):
    """Generate the .NETCoreApp,Version=vX.Y.AssemblyAttributes.cs file."""
    content = '// <autogenerated />\nusing System;\nusing System.Reflection;\n[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETCoreApp,Version=v%s.%s", FrameworkDisplayName = ".NET %s.%s")]\n' % (
        ctx.attr.major_version, ctx.attr.minor_version,
        ctx.attr.major_version, ctx.attr.minor_version,
    )
    ctx.actions.write(ctx.outputs.out, content)

gen_target_framework_attrs = rule(
    implementation = _gen_target_framework_attrs_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "major_version": attr.string(default = _MAJOR_VERSION),
        "minor_version": attr.string(default = _MINOR_VERSION),
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
    include_default_values = True,
    resources = [],
    resource_logical_names = {},
    nowarn = [],
    suffix_srcs = [],
    use_shared_compilation = True,
    **kwargs
):
    if out == None:
        out = name

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
        gen_resx_source(
            name = resx_target,
            out = name + "/System.SR.cs",
            assembly_name = out,
            resource_name = _resource_name,
            resource_class_name = resource_class_name if resource_class_name else "",
            resx_file = resx_file,
            include_default_values = include_default_values,
            omit_getresourcestring = omit_getresourcestring,
        )
        srcs = srcs + [ ":" + resx_target ]

    # suffix_srcs are appended after resx source, matching MSBuild's ordering
    # where AssemblyInfo.cs and Forwards.cs come after generated SR sources.
    srcs = srcs + suffix_srcs

    _base_csharp_library(
        name = name,
        srcs = srcs,
        out = out,
        resources = resources,
        resource_logical_names = resource_logical_names,
        use_shared_compilation = use_shared_compilation,
        shared_compilation_worker = _SHARED_COMPILATION_WORKER if use_shared_compilation else None,
        nowarn = nowarn + [
            "CS1701",
            # Match Directory.Build.props global NoWarn
            "CS8500",
            "CS8969",
            # Arcade SDK global NoWarn (Microsoft.DotNet.Arcade.Sdk targets)
            "CS1702",
            "CS1705",
            "NU5105",
            # Directory.Build.props global NoWarn
            "IDE0060",
            "IDE0100",
        ],
        **kwargs
    )

def csharp_binary(
    name,
    use_shared_compilation = True,
    **kwargs
):
    _base_csharp_binary(
        name = name,
        use_shared_compilation = use_shared_compilation,
        shared_compilation_worker = _SHARED_COMPILATION_WORKER if use_shared_compilation else None,
        **kwargs
    )
