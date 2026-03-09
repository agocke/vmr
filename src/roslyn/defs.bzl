# Roslyn Bazel build definitions

load("@bazel_skylib//rules:write_file.bzl", "write_file")

# ── Live runtime reference assemblies ────────────────────────────────
# These are the ref assemblies built from src/runtime/ sources. Roslyn
# compiles against these instead of the SDK targeting pack so that the
# entire stack (runtime + compiler) builds from source in the VMR.
#
# Mirrors CORE_ROOT_LIBS in @dotnet_runtime//src/libraries:defs.bzl but
# with @dotnet_runtime// prefixes so labels resolve correctly from the
# VMR module.
ROSLYN_LIVE_REFPACK_DEPS = [
    "@dotnet_runtime//src/libraries:ref_System.Runtime",
    "@dotnet_runtime//src/libraries:ref_Microsoft.Win32.Primitives",
    "@dotnet_runtime//src/libraries:ref_System.ComponentModel.Primitives",
    "@dotnet_runtime//src/libraries:ref_System.Diagnostics.Process",
    "@dotnet_runtime//src/libraries/System.Collections:ref_System.Collections",
    "@dotnet_runtime//src/libraries/System.Collections.Concurrent:ref_System.Collections.Concurrent",
    "@dotnet_runtime//src/libraries/System.Collections.Immutable:ref_System.Collections.Immutable",
    "@dotnet_runtime//src/libraries/System.Collections.NonGeneric:ref_System.Collections.NonGeneric",
    "@dotnet_runtime//src/libraries/System.Collections.Specialized:ref_System.Collections.Specialized",
    "@dotnet_runtime//src/libraries/System.ComponentModel:ref_System.ComponentModel",
    "@dotnet_runtime//src/libraries/System.Console:ref_System.Console",
    "@dotnet_runtime//src/libraries/System.Diagnostics.FileVersionInfo:ref_System.Diagnostics.FileVersionInfo",
    "@dotnet_runtime//src/libraries/System.Diagnostics.Tracing:ref_System.Diagnostics.Tracing",
    "@dotnet_runtime//src/libraries/System.IO.MemoryMappedFiles:ref_System.IO.MemoryMappedFiles",
    "@dotnet_runtime//src/libraries/System.Linq:ref_System.Linq",
    "@dotnet_runtime//src/libraries/System.Memory:ref_System.Memory",
    "@dotnet_runtime//src/libraries/System.Numerics.Vectors:ref_System.Numerics.Vectors",
    "@dotnet_runtime//src/libraries/System.ObjectModel:ref_System.ObjectModel",
    "@dotnet_runtime//src/libraries/System.Reflection.Emit:ref_System.Reflection.Emit",
    "@dotnet_runtime//src/libraries/System.Reflection.Emit.ILGeneration:ref_System.Reflection.Emit.ILGeneration",
    "@dotnet_runtime//src/libraries/System.Reflection.Emit.Lightweight:ref_System.Reflection.Emit.Lightweight",
    "@dotnet_runtime//src/libraries/System.Reflection.Metadata:ref_System.Reflection.Metadata",
    "@dotnet_runtime//src/libraries/System.Reflection.Primitives:ref_System.Reflection.Primitives",
    "@dotnet_runtime//src/libraries/System.Reflection.TypeExtensions:ref_System.Reflection.TypeExtensions",
    "@dotnet_runtime//src/libraries/System.Runtime.InteropServices:ref_System.Runtime.InteropServices",
    "@dotnet_runtime//src/libraries/System.Runtime.Intrinsics:ref_System.Runtime.Intrinsics",
    "@dotnet_runtime//src/libraries/System.Runtime.Loader:ref_System.Runtime.Loader",
    "@dotnet_runtime//src/libraries/System.Runtime.Numerics:ref_System.Runtime.Numerics",
    "@dotnet_runtime//src/libraries/System.Runtime.Serialization.Primitives:ref_System.Runtime.Serialization.Primitives",
    "@dotnet_runtime//src/libraries/System.Security.Cryptography:ref_System.Security.Cryptography",
    "@dotnet_runtime//src/libraries/System.Text.Encoding.Extensions:ref_System.Text.Encoding.Extensions",
    "@dotnet_runtime//src/libraries/System.Text.Encodings.Web:ref_System.Text.Encodings.Web",
    "@dotnet_runtime//src/libraries/System.Text.RegularExpressions:ref_System.Text.RegularExpressions",
    "@dotnet_runtime//src/libraries/System.Threading:ref_System.Threading",
    "@dotnet_runtime//src/libraries/System.Threading.Overlapped:ref_System.Threading.Overlapped",
    "@dotnet_runtime//src/libraries/System.Threading.Tasks.Parallel:ref_System.Threading.Tasks.Parallel",
    "@dotnet_runtime//src/libraries/System.Threading.Thread:ref_System.Threading.Thread",
    "@dotnet_runtime//src/libraries/System.Threading.ThreadPool:ref_System.Threading.ThreadPool",
    # Additional assemblies needed by Roslyn beyond CORE_ROOT_LIBS
    "@dotnet_runtime//src/libraries/System.IO.Compression:ref_System.IO.Compression",
    "@dotnet_runtime//src/libraries/System.IO.Pipes:ref_System.IO.Pipes",
    "@dotnet_runtime//src/libraries/System.Xml.ReaderWriter:ref_System.Xml.ReaderWriter",
    "@dotnet_runtime//src/libraries/System.Xml.XDocument:ref_System.Xml.XDocument",
    "@dotnet_runtime//src/libraries/System.Diagnostics.Contracts:ref_System.Diagnostics.Contracts",
    "@dotnet_runtime//src/libraries/System.Diagnostics.StackTrace:ref_System.Diagnostics.StackTrace",
    "@dotnet_runtime//src/libraries/System.Text.Encoding.CodePages:ref_System.Text.Encoding.CodePages",
    "@dotnet_runtime//src/libraries/System.Xml.XPath.XDocument:ref_System.Xml.XPath.XDocument",
    "@dotnet_runtime//src/libraries/System.Linq.Expressions:ref_System.Linq.Expressions",
    "@dotnet_runtime//src/libraries/System.Diagnostics.TraceSource:ref_System.Diagnostics.TraceSource",
    "@dotnet_runtime//src/libraries/System.Security.AccessControl:ref_System.Security.AccessControl",
    "@dotnet_runtime//src/libraries/System.IO.Pipes.AccessControl:ref_System.IO.Pipes.AccessControl",
    "@dotnet_runtime//src/libraries/System.Net.Sockets:ref_System.Net.Sockets",
    "@dotnet_runtime//src/libraries/System.Security.Principal.Windows:ref_System.Security.Principal.Windows",
    "@dotnet_runtime//src/libraries/System.Security.Claims:ref_System.Security.Claims",
]

# Microsoft Shared Library public key (PublicKeyToken=31bf3856ad364e35)
ROSLYN_PUBLIC_KEY = "0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9"

# Strong name key file for public signing
ROSLYN_KEYFILE = "//src/arcade/src/Microsoft.DotNet.Arcade.Sdk/tools/snk:35MSSharedLib1024.snk"

# Assembly version that matches the NuGet packages (4.11.0)
ROSLYN_ASSEMBLY_VERSION = "4.11.0.0"

# Informational version: matches Versions.props VersionPrefix
ROSLYN_INFORMATIONAL_VERSION = "4.11.0"

# Metadata from Arcade SDK ProjectDefaults.props and Roslyn's Settings.props
_ROSLYN_COMPANY = "Microsoft Corporation"
_ROSLYN_COPYRIGHT = "\\u00a9 Microsoft Corporation. All rights reserved."
_ROSLYN_NEUTRAL_LANGUAGE = "en-US"

def roslyn_assembly_info(
        name,
        assembly_version = ROSLYN_ASSEMBLY_VERSION,
        informational_version = ROSLYN_INFORMATIONAL_VERSION,
        product = "",
        title = "",
        **kwargs):
    """Generate an AssemblyInfo.cs matching MSBuild's GenerateAssemblyInfo output.

    Produces the same attributes as the .NET SDK's
    Microsoft.NET.GenerateAssemblyInfo.targets for Roslyn projects.
    """
    content = [
        "using System.Reflection;",
        "using System.Resources;",
        "using System.Runtime.InteropServices;",
        "",
        '[assembly: AssemblyVersion("%s")]' % assembly_version,
        '[assembly: AssemblyFileVersion("%s")]' % assembly_version,
        '[assembly: AssemblyInformationalVersion("%s")]' % informational_version,
        '[assembly: AssemblyCompany("%s")]' % _ROSLYN_COMPANY,
        '[assembly: AssemblyCopyright("%s")]' % _ROSLYN_COPYRIGHT,
        '[assembly: NeutralResourcesLanguage("%s")]' % _ROSLYN_NEUTRAL_LANGUAGE,
    ]
    if product:
        content.append('[assembly: AssemblyProduct("%s")]' % product)
    if title:
        content.append('[assembly: AssemblyTitle("%s")]' % title)
    content.append('[assembly: ComVisible(false)]')
    content.append("")

    write_file(
        name = name,
        out = name + ".cs",
        content = content,
        **kwargs
    )
