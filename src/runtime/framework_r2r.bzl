"""Framework assembly ILLink + crossgen2 pipeline targets.

This file defines macros to generate ILLink trimming and crossgen2 R2R
compilation targets for all framework assemblies in the runtime pack.
"""

load("//:illink.bzl", "illink_trim")
load("//src/coreclr:crossgen2.bzl", "crossgen_assembly")

# Assembly name -> Bazel label for impl_ target.
# Excludes System.Private.CoreLib (has its own crossgen path) and
# assemblies not in the runtime pack (Microsoft.Extensions.*, etc.).
FRAMEWORK_ASSEMBLY_LABELS = {
    "Microsoft.CSharp": "//src/libraries/Microsoft.CSharp:impl_Microsoft.CSharp",
    "Microsoft.VisualBasic": "//src/libraries/shims/Microsoft.VisualBasic:impl_Microsoft.VisualBasic",
    "Microsoft.VisualBasic.Core": "//src/libraries/Microsoft.VisualBasic.Core/src:impl_Microsoft.VisualBasic.Core",
    "Microsoft.Win32.Primitives": "//src/libraries:impl_Microsoft.Win32.Primitives",
    "Microsoft.Win32.Registry": "//src/libraries/Microsoft.Win32.Registry:impl_Microsoft.Win32.Registry",
    "System": "//src/libraries/shims/System:impl_System",
    "System.AppContext": "//src/libraries/shims/System.AppContext:impl_System.AppContext",
    "System.Buffers": "//src/libraries/shims/System.Buffers:impl_System.Buffers",
    "System.Collections": "//src/libraries/System.Collections:impl_System.Collections",
    "System.Collections.Concurrent": "//src/libraries/System.Collections.Concurrent:impl_System.Collections.Concurrent",
    "System.Collections.Immutable": "//src/libraries/System.Collections.Immutable:impl_System.Collections.Immutable",
    "System.Collections.NonGeneric": "//src/libraries/System.Collections.NonGeneric:impl_System.Collections.NonGeneric",
    "System.Collections.Specialized": "//src/libraries/System.Collections.Specialized:impl_System.Collections.Specialized",
    "System.ComponentModel": "//src/libraries/System.ComponentModel:impl_System.ComponentModel",
    "System.ComponentModel.Annotations": "//src/libraries/System.ComponentModel.Annotations:impl_System.ComponentModel.Annotations",
    "System.ComponentModel.DataAnnotations": "//src/libraries/shims/System.ComponentModel.DataAnnotations:impl_System.ComponentModel.DataAnnotations",
    "System.ComponentModel.EventBasedAsync": "//src/libraries/System.ComponentModel.EventBasedAsync:impl_System.ComponentModel.EventBasedAsync",
    "System.ComponentModel.Primitives": "//src/libraries:impl_System.ComponentModel.Primitives",
    "System.ComponentModel.TypeConverter": "//src/libraries/System.ComponentModel.TypeConverter:impl_System.ComponentModel.TypeConverter",
    "System.Configuration": "//src/libraries/shims/System.Configuration:impl_System.Configuration",
    "System.Console": "//src/libraries/System.Console:impl_System.Console",
    "System.Core": "//src/libraries/shims/System.Core:impl_System.Core",
    "System.Data": "//src/libraries/shims/System.Data:impl_System.Data",
    "System.Data.Common": "//src/libraries/System.Data.Common:impl_System.Data.Common",
    "System.Data.DataSetExtensions": "//src/libraries/shims/System.Data.DataSetExtensions:impl_System.Data.DataSetExtensions",
    "System.Diagnostics.Contracts": "//src/libraries/System.Diagnostics.Contracts:impl_System.Diagnostics.Contracts",
    "System.Diagnostics.Debug": "//src/libraries/shims/System.Diagnostics.Debug:impl_System.Diagnostics.Debug",
    "System.Diagnostics.DiagnosticSource": "//src/libraries/System.Diagnostics.DiagnosticSource:impl_System.Diagnostics.DiagnosticSource",
    "System.Diagnostics.FileVersionInfo": "//src/libraries/System.Diagnostics.FileVersionInfo:impl_System.Diagnostics.FileVersionInfo",
    "System.Diagnostics.Process": "//src/libraries:impl_System.Diagnostics.Process",
    "System.Diagnostics.StackTrace": "//src/libraries/System.Diagnostics.StackTrace:impl_System.Diagnostics.StackTrace",
    "System.Diagnostics.TextWriterTraceListener": "//src/libraries/System.Diagnostics.TextWriterTraceListener:impl_System.Diagnostics.TextWriterTraceListener",
    "System.Diagnostics.Tools": "//src/libraries/shims/System.Diagnostics.Tools:impl_System.Diagnostics.Tools",
    "System.Diagnostics.TraceSource": "//src/libraries/System.Diagnostics.TraceSource:impl_System.Diagnostics.TraceSource",
    "System.Diagnostics.Tracing": "//src/libraries/System.Diagnostics.Tracing:impl_System.Diagnostics.Tracing",
    "System.Drawing": "//src/libraries/shims/System.Drawing:impl_System.Drawing",
    "System.Drawing.Primitives": "//src/libraries/System.Drawing.Primitives:impl_System.Drawing.Primitives",
    "System.Dynamic.Runtime": "//src/libraries/shims/System.Dynamic.Runtime:impl_System.Dynamic.Runtime",
    "System.Formats.Asn1": "//src/libraries/System.Formats.Asn1:impl_System.Formats.Asn1",
    "System.Formats.Tar": "//src/libraries/System.Formats.Tar:impl_System.Formats.Tar",
    "System.Globalization": "//src/libraries/shims/System.Globalization:impl_System.Globalization",
    "System.Globalization.Calendars": "//src/libraries/shims/System.Globalization.Calendars:impl_System.Globalization.Calendars",
    "System.Globalization.Extensions": "//src/libraries/shims/System.Globalization.Extensions:impl_System.Globalization.Extensions",
    "System.IO": "//src/libraries/shims/System.IO:impl_System.IO",
    "System.IO.Compression": "//src/libraries/System.IO.Compression:impl_System.IO.Compression",
    "System.IO.Compression.Brotli": "//src/libraries/System.IO.Compression.Brotli:impl_System.IO.Compression.Brotli",
    "System.IO.Compression.FileSystem": "//src/libraries/shims/System.IO.Compression.FileSystem:impl_System.IO.Compression.FileSystem",
    "System.IO.Compression.ZipFile": "//src/libraries/System.IO.Compression.ZipFile:impl_System.IO.Compression.ZipFile",
    "System.IO.FileSystem": "//src/libraries/shims/System.IO.FileSystem:impl_System.IO.FileSystem",
    "System.IO.FileSystem.AccessControl": "//src/libraries/System.IO.FileSystem.AccessControl:impl_System.IO.FileSystem.AccessControl",
    "System.IO.FileSystem.DriveInfo": "//src/libraries/System.IO.FileSystem.DriveInfo:impl_System.IO.FileSystem.DriveInfo",
    "System.IO.FileSystem.Primitives": "//src/libraries/shims/System.IO.FileSystem.Primitives:impl_System.IO.FileSystem.Primitives",
    "System.IO.FileSystem.Watcher": "//src/libraries/System.IO.FileSystem.Watcher:impl_System.IO.FileSystem.Watcher",
    "System.IO.IsolatedStorage": "//src/libraries/System.IO.IsolatedStorage:impl_System.IO.IsolatedStorage",
    "System.IO.MemoryMappedFiles": "//src/libraries/System.IO.MemoryMappedFiles:impl_System.IO.MemoryMappedFiles",
    "System.IO.Pipelines": "//src/libraries/System.IO.Pipelines:impl_System.IO.Pipelines",
    "System.IO.Pipes": "//src/libraries/System.IO.Pipes:impl_System.IO.Pipes",
    "System.IO.Pipes.AccessControl": "//src/libraries/System.IO.Pipes.AccessControl/src:impl_System.IO.Pipes.AccessControl",
    "System.IO.UnmanagedMemoryStream": "//src/libraries/shims/System.IO.UnmanagedMemoryStream:impl_System.IO.UnmanagedMemoryStream",
    "System.Linq": "//src/libraries/System.Linq:impl_System.Linq",
    "System.Linq.AsyncEnumerable": "//src/libraries/System.Linq.AsyncEnumerable:impl_System.Linq.AsyncEnumerable",
    "System.Linq.Expressions": "//src/libraries/System.Linq.Expressions:impl_System.Linq.Expressions",
    "System.Linq.Parallel": "//src/libraries/System.Linq.Parallel:impl_System.Linq.Parallel",
    "System.Linq.Queryable": "//src/libraries/System.Linq.Queryable:impl_System.Linq.Queryable",
    "System.Memory": "//src/libraries/System.Memory:impl_System.Memory",
    "System.Net": "//src/libraries/shims/System.Net:impl_System.Net",
    "System.Net.Http": "//src/libraries/System.Net.Http:impl_System.Net.Http",
    "System.Net.Http.Json": "//src/libraries/System.Net.Http.Json:impl_System.Net.Http.Json",
    "System.Net.HttpListener": "//src/libraries/System.Net.HttpListener:impl_System.Net.HttpListener",
    "System.Net.Mail": "//src/libraries/System.Net.Mail:impl_System.Net.Mail",
    "System.Net.NameResolution": "//src/libraries/System.Net.NameResolution:impl_System.Net.NameResolution",
    "System.Net.NetworkInformation": "//src/libraries/System.Net.NetworkInformation:impl_System.Net.NetworkInformation",
    "System.Net.Ping": "//src/libraries/System.Net.Ping:impl_System.Net.Ping",
    "System.Net.Primitives": "//src/libraries/System.Net.Primitives:impl_System.Net.Primitives",
    "System.Net.Quic": "//src/libraries/System.Net.Quic:impl_System.Net.Quic",
    "System.Net.Requests": "//src/libraries/System.Net.Requests:impl_System.Net.Requests",
    "System.Net.Security": "//src/libraries/System.Net.Security:impl_System.Net.Security",
    "System.Net.ServerSentEvents": "//src/libraries/System.Net.ServerSentEvents:impl_System.Net.ServerSentEvents",
    "System.Net.ServicePoint": "//src/libraries/shims/System.Net.ServicePoint:impl_System.Net.ServicePoint",
    "System.Net.Sockets": "//src/libraries/System.Net.Sockets:impl_System.Net.Sockets",
    "System.Net.WebClient": "//src/libraries/System.Net.WebClient:impl_System.Net.WebClient",
    "System.Net.WebHeaderCollection": "//src/libraries/System.Net.WebHeaderCollection:impl_System.Net.WebHeaderCollection",
    "System.Net.WebProxy": "//src/libraries/System.Net.WebProxy:impl_System.Net.WebProxy",
    "System.Net.WebSockets": "//src/libraries/System.Net.WebSockets:impl_System.Net.WebSockets",
    "System.Net.WebSockets.Client": "//src/libraries/System.Net.WebSockets.Client:impl_System.Net.WebSockets.Client",
    "System.Numerics": "//src/libraries/shims/System.Numerics:impl_System.Numerics",
    "System.Numerics.Vectors": "//src/libraries/System.Numerics.Vectors:impl_System.Numerics.Vectors",
    "System.ObjectModel": "//src/libraries/System.ObjectModel:impl_System.ObjectModel",
    "System.Private.DataContractSerialization": "//src/libraries/System.Private.DataContractSerialization:impl_System.Private.DataContractSerialization",
    "System.Private.Uri": "//src/libraries:impl_System.Private.Uri",
    "System.Private.Xml": "//src/libraries/System.Private.Xml:impl_System.Private.Xml",
    "System.Private.Xml.Linq": "//src/libraries/System.Private.Xml.Linq:impl_System.Private.Xml.Linq",
    "System.Reflection": "//src/libraries/shims/System.Reflection:impl_System.Reflection",
    "System.Reflection.DispatchProxy": "//src/libraries/System.Reflection.DispatchProxy:impl_System.Reflection.DispatchProxy",
    "System.Reflection.Emit": "//src/libraries/System.Reflection.Emit:impl_System.Reflection.Emit",
    "System.Reflection.Emit.ILGeneration": "//src/libraries/System.Reflection.Emit.ILGeneration:impl_System.Reflection.Emit.ILGeneration",
    "System.Reflection.Emit.Lightweight": "//src/libraries/System.Reflection.Emit.Lightweight:impl_System.Reflection.Emit.Lightweight",
    "System.Reflection.Extensions": "//src/libraries/shims/System.Reflection.Extensions:impl_System.Reflection.Extensions",
    "System.Reflection.Metadata": "//src/libraries/System.Reflection.Metadata:impl_System.Reflection.Metadata",
    "System.Reflection.Primitives": "//src/libraries/System.Reflection.Primitives:impl_System.Reflection.Primitives",
    "System.Reflection.TypeExtensions": "//src/libraries/System.Reflection.TypeExtensions:impl_System.Reflection.TypeExtensions",
    "System.Resources.Reader": "//src/libraries/shims/System.Resources.Reader:impl_System.Resources.Reader",
    "System.Resources.ResourceManager": "//src/libraries/shims/System.Resources.ResourceManager:impl_System.Resources.ResourceManager",
    "System.Resources.Writer": "//src/libraries/System.Resources.Writer:impl_System.Resources.Writer",
    "System.Runtime": "//src/libraries:impl_System.Runtime",
    "System.Runtime.CompilerServices.Unsafe": "//src/libraries/shims/System.Runtime.CompilerServices.Unsafe:impl_System.Runtime.CompilerServices.Unsafe",
    "System.Runtime.CompilerServices.VisualC": "//src/libraries/System.Runtime.CompilerServices.VisualC:impl_System.Runtime.CompilerServices.VisualC",
    "System.Runtime.Extensions": "//src/libraries/shims/System.Runtime.Extensions:impl_System.Runtime.Extensions",
    "System.Runtime.Handles": "//src/libraries/shims/System.Runtime.Handles:impl_System.Runtime.Handles",
    "System.Runtime.InteropServices": "//src/libraries/System.Runtime.InteropServices:impl_System.Runtime.InteropServices",
    "System.Runtime.InteropServices.JavaScript": "//src/libraries/System.Runtime.InteropServices.JavaScript/src:impl_System.Runtime.InteropServices.JavaScript",
    "System.Runtime.InteropServices.RuntimeInformation": "//src/libraries/shims/System.Runtime.InteropServices.RuntimeInformation:impl_System.Runtime.InteropServices.RuntimeInformation",
    "System.Runtime.Intrinsics": "//src/libraries/System.Runtime.Intrinsics:impl_System.Runtime.Intrinsics",
    "System.Runtime.Loader": "//src/libraries/System.Runtime.Loader:impl_System.Runtime.Loader",
    "System.Runtime.Numerics": "//src/libraries/System.Runtime.Numerics:impl_System.Runtime.Numerics",
    "System.Runtime.Serialization": "//src/libraries/shims/System.Runtime.Serialization:impl_System.Runtime.Serialization",
    "System.Runtime.Serialization.Formatters": "//src/libraries/System.Runtime.Serialization.Formatters:impl_System.Runtime.Serialization.Formatters",
    "System.Runtime.Serialization.Json": "//src/libraries/System.Runtime.Serialization.Json:impl_System.Runtime.Serialization.Json",
    "System.Runtime.Serialization.Primitives": "//src/libraries/System.Runtime.Serialization.Primitives:impl_System.Runtime.Serialization.Primitives",
    "System.Runtime.Serialization.Xml": "//src/libraries/System.Runtime.Serialization.Xml:impl_System.Runtime.Serialization.Xml",
    "System.Security": "//src/libraries/shims/System.Security:impl_System.Security",
    "System.Security.AccessControl": "//src/libraries/System.Security.AccessControl:impl_System.Security.AccessControl",
    "System.Security.Claims": "//src/libraries/System.Security.Claims:impl_System.Security.Claims",
    "System.Security.Cryptography": "//src/libraries/System.Security.Cryptography:impl_System.Security.Cryptography",
    "System.Security.Cryptography.Algorithms": "//src/libraries/shims/System.Security.Cryptography.Algorithms:impl_System.Security.Cryptography.Algorithms",
    "System.Security.Cryptography.Cng": "//src/libraries/shims/System.Security.Cryptography.Cng:impl_System.Security.Cryptography.Cng",
    "System.Security.Cryptography.Csp": "//src/libraries/shims/System.Security.Cryptography.Csp:impl_System.Security.Cryptography.Csp",
    "System.Security.Cryptography.Encoding": "//src/libraries/shims/System.Security.Cryptography.Encoding:impl_System.Security.Cryptography.Encoding",
    "System.Security.Cryptography.OpenSsl": "//src/libraries/shims/System.Security.Cryptography.OpenSsl:impl_System.Security.Cryptography.OpenSsl",
    "System.Security.Cryptography.Primitives": "//src/libraries/shims/System.Security.Cryptography.Primitives:impl_System.Security.Cryptography.Primitives",
    "System.Security.Cryptography.X509Certificates": "//src/libraries/shims/System.Security.Cryptography.X509Certificates:impl_System.Security.Cryptography.X509Certificates",
    "System.Security.Principal": "//src/libraries/shims/System.Security.Principal:impl_System.Security.Principal",
    "System.Security.Principal.Windows": "//src/libraries/System.Security.Principal.Windows:impl_System.Security.Principal.Windows",
    "System.Security.SecureString": "//src/libraries/shims/System.Security.SecureString:impl_System.Security.SecureString",
    "System.ServiceModel.Web": "//src/libraries/shims/System.ServiceModel.Web:impl_System.ServiceModel.Web",
    "System.ServiceProcess": "//src/libraries/shims/System.ServiceProcess:impl_System.ServiceProcess",
    "System.Text.Encoding": "//src/libraries/shims/System.Text.Encoding:impl_System.Text.Encoding",
    "System.Text.Encoding.CodePages": "//src/libraries/System.Text.Encoding.CodePages:impl_System.Text.Encoding.CodePages",
    "System.Text.Encoding.Extensions": "//src/libraries/System.Text.Encoding.Extensions:impl_System.Text.Encoding.Extensions",
    "System.Text.Encodings.Web": "//src/libraries/System.Text.Encodings.Web:impl_System.Text.Encodings.Web",
    "System.Text.Json": "//src/libraries/System.Text.Json:impl_System.Text.Json",
    "System.Text.RegularExpressions": "//src/libraries/System.Text.RegularExpressions:impl_System.Text.RegularExpressions",
    "System.Threading": "//src/libraries/System.Threading:impl_System.Threading",
    "System.Threading.AccessControl": "//src/libraries/System.Threading.AccessControl/src:impl_System.Threading.AccessControl",
    "System.Threading.Channels": "//src/libraries/System.Threading.Channels:impl_System.Threading.Channels",
    "System.Threading.Overlapped": "//src/libraries/System.Threading.Overlapped:impl_System.Threading.Overlapped",
    "System.Threading.Tasks": "//src/libraries/shims/System.Threading.Tasks:impl_System.Threading.Tasks",
    "System.Threading.Tasks.Dataflow": "//src/libraries/System.Threading.Tasks.Dataflow:impl_System.Threading.Tasks.Dataflow",
    "System.Threading.Tasks.Extensions": "//src/libraries/shims/System.Threading.Tasks.Extensions:impl_System.Threading.Tasks.Extensions",
    "System.Threading.Tasks.Parallel": "//src/libraries/System.Threading.Tasks.Parallel:impl_System.Threading.Tasks.Parallel",
    "System.Threading.Thread": "//src/libraries/System.Threading.Thread:impl_System.Threading.Thread",
    "System.Threading.ThreadPool": "//src/libraries/System.Threading.ThreadPool:impl_System.Threading.ThreadPool",
    "System.Threading.Timer": "//src/libraries/shims/System.Threading.Timer:impl_System.Threading.Timer",
    "System.Transactions": "//src/libraries/shims/System.Transactions:impl_System.Transactions",
    "System.Transactions.Local": "//src/libraries/System.Transactions.Local:impl_System.Transactions.Local",
    "System.ValueTuple": "//src/libraries/shims/System.ValueTuple:impl_System.ValueTuple",
    "System.Web": "//src/libraries/shims/System.Web:impl_System.Web",
    "System.Web.HttpUtility": "//src/libraries/System.Web.HttpUtility:impl_System.Web.HttpUtility",
    "System.Windows": "//src/libraries/shims/System.Windows:impl_System.Windows",
    "System.Xml": "//src/libraries/shims/System.Xml:impl_System.Xml",
    "System.Xml.Linq": "//src/libraries/shims/System.Xml.Linq:impl_System.Xml.Linq",
    "System.Xml.ReaderWriter": "//src/libraries/System.Xml.ReaderWriter:impl_System.Xml.ReaderWriter",
    "System.Xml.Serialization": "//src/libraries/shims/System.Xml.Serialization:impl_System.Xml.Serialization",
    "System.Xml.XDocument": "//src/libraries/System.Xml.XDocument:impl_System.Xml.XDocument",
    "System.Xml.XPath": "//src/libraries/System.Xml.XPath:impl_System.Xml.XPath",
    "System.Xml.XPath.XDocument": "//src/libraries/System.Xml.XPath.XDocument:impl_System.Xml.XPath.XDocument",
    "System.Xml.XmlDocument": "//src/libraries/shims/System.Xml.XmlDocument:impl_System.Xml.XmlDocument",
    "System.Xml.XmlSerializer": "//src/libraries/System.Xml.XmlSerializer:impl_System.Xml.XmlSerializer",
    "WindowsBase": "//src/libraries/shims/WindowsBase:impl_WindowsBase",
    "mscorlib": "//src/libraries/shims/mscorlib:impl_mscorlib",
    "netstandard": "//src/libraries/shims/netstandard:impl_netstandard",
}

# Assemblies that get ReadyToRun compilation (subset of ILLinked assemblies).
R2R_ASSEMBLIES = [
    "Microsoft.CSharp",
    "Microsoft.VisualBasic.Core",
    "Microsoft.Win32.Registry",
    "System.Collections",
    "System.Collections.Concurrent",
    "System.Collections.Immutable",
    "System.Collections.NonGeneric",
    "System.Collections.Specialized",
    "System.ComponentModel",
    "System.ComponentModel.Annotations",
    "System.ComponentModel.EventBasedAsync",
    "System.ComponentModel.Primitives",
    "System.ComponentModel.TypeConverter",
    "System.Console",
    "System.Data.Common",
    "System.Diagnostics.DiagnosticSource",
    "System.Diagnostics.FileVersionInfo",
    "System.Diagnostics.Process",
    "System.Diagnostics.StackTrace",
    "System.Diagnostics.TextWriterTraceListener",
    "System.Diagnostics.TraceSource",
    "System.Drawing.Primitives",
    "System.Formats.Asn1",
    "System.Formats.Tar",
    "System.IO.Compression",
    "System.IO.Compression.Brotli",
    "System.IO.Compression.ZipFile",
    "System.IO.FileSystem.AccessControl",
    "System.IO.FileSystem.DriveInfo",
    "System.IO.FileSystem.Watcher",
    "System.IO.IsolatedStorage",
    "System.IO.MemoryMappedFiles",
    "System.IO.Pipelines",
    # TODO: System.IO.Pipes hits a crossgen2 type resolution failure on
    # CriticalFinalizerObject after trimming. Investigate and re-enable once
    # the framework R2R pipeline can compile the trimmed assembly set.
    # "System.IO.Pipes",
    # "System.IO.Pipes.AccessControl",
    "System.Linq",
    "System.Linq.AsyncEnumerable",
    "System.Linq.Expressions",
    "System.Linq.Parallel",
    "System.Linq.Queryable",
    "System.Memory",
    # TODO: System.Net.Http triggers a type resolution crash in crossgen2.
    # Investigate and re-enable once the missing resource issue in
    # ILCompiler.TypeSystem is resolved.
    # "System.Net.Http",
    "System.Net.Http.Json",
    "System.Net.HttpListener",
    "System.Net.Mail",
    "System.Net.NameResolution",
    # TODO: These networking assemblies hit crossgen2 type resolution failures
    # after trimming in the framework R2R pipeline. Investigate and re-enable
    # once crossgen2 can compile the trimmed assembly set.
    # "System.Net.NetworkInformation",
    "System.Net.Ping",
    # "System.Net.Primitives",
    "System.Net.Quic",
    "System.Net.Requests",
    # "System.Net.Security",
    "System.Net.ServerSentEvents",
    # "System.Net.Sockets",
    "System.Net.WebClient",
    "System.Net.WebHeaderCollection",
    "System.Net.WebProxy",
    "System.Net.WebSockets",
    "System.Net.WebSockets.Client",
    "System.ObjectModel",
    "System.Private.DataContractSerialization",
    "System.Private.Uri",
    "System.Private.Xml",
    "System.Private.Xml.Linq",
    "System.Reflection.DispatchProxy",
    "System.Reflection.Emit",
    "System.Reflection.Metadata",
    "System.Reflection.TypeExtensions",
    "System.Resources.Writer",
    "System.Runtime.CompilerServices.VisualC",
    "System.Runtime.InteropServices",
    "System.Runtime.InteropServices.JavaScript",
    "System.Runtime.Numerics",
    "System.Runtime.Serialization.Formatters",
    "System.Runtime.Serialization.Primitives",
    "System.Security.AccessControl",
    "System.Security.Claims",
    "System.Security.Cryptography",
    "System.Security.Principal.Windows",
    "System.Text.Encoding.CodePages",
    "System.Text.Encodings.Web",
    "System.Text.Json",
    "System.Text.RegularExpressions",
    "System.Threading",
    "System.Threading.AccessControl",
    "System.Threading.Channels",
    "System.Threading.Tasks.Dataflow",
    # TODO: System.Threading.Tasks.Parallel hits a crossgen2 type resolution
    # failure for Replica after trimming in the framework R2R pipeline.
    # Investigate and re-enable once crossgen2 can compile the trimmed assembly.
    # "System.Threading.Tasks.Parallel",
    "System.Transactions.Local",
    "System.Web.HttpUtility",
    "System.Xml.XPath.XDocument",
]

# Assemblies with ILLink.Descriptors.LibraryBuild.xml files.
_DESCRIPTOR_ASSEMBLIES = [
    "System.ComponentModel.TypeConverter",
    "System.Data.Common",
    "System.Diagnostics.DiagnosticSource",
    "System.Diagnostics.StackTrace",
    "System.Net.Http",
    "System.Private.Xml",
    "System.Private.Xml.Linq",
    "System.Security.Claims",
    "System.Security.Cryptography",
    "System.Security.Principal.Windows",
    "System.Text.Json",
]

# Assemblies with ILLink.Suppressions.LibraryBuild.xml files.
_SUPPRESSION_ASSEMBLIES = [
    "System.ComponentModel.TypeConverter",
    "System.Data.Common",
]

# Some facade assemblies don't benefit from ILLink trimming, and linker
# execution can fail on them even though the input assembly is already
# effectively minimal. This includes shims and generated empty facades.
_ILLINK_BYPASS_ASSEMBLIES = [
    "System.Diagnostics.Contracts",
    "System.Diagnostics.Tracing",
    "System.IO.UnmanagedMemoryStream",
    "System.Reflection.Emit.ILGeneration",
    "System.Reflection.Emit.Lightweight",
    "System.Reflection.Primitives",
    "System.Text.Encoding.Extensions",
    "System.Threading.Overlapped",
    "System.Threading.Thread",
    "System.Threading.ThreadPool",
    "System.Xml.ReaderWriter",
    "System.Xml.XPath",
    "System.Xml.XmlSerializer",
]

def framework_illink_targets():
    """Generate illink_trim targets for all framework assemblies."""
    for name, label in FRAMEWORK_ASSEMBLY_LABELS.items():
        desc = []
        supp = []

        # Descriptor files are under src/libraries/<Name>/src/ILLink/
        if name in _DESCRIPTOR_ASSEMBLIES:
            desc = ["//src/libraries/" + name + ":src/ILLink/ILLink.Descriptors.LibraryBuild.xml"]
        if name in _SUPPRESSION_ASSEMBLIES:
            supp = ["//src/libraries/" + name + ":src/ILLink/ILLink.Suppressions.LibraryBuild.xml"]

        if "/shims/" in label or name in _ILLINK_BYPASS_ASSEMBLIES:
            native.filegroup(
                name = "illink_" + name,
                srcs = [label],
            )
        else:
            illink_trim(
                name = "illink_" + name,
                assembly = label,
                out = "trimmed/" + name + ".dll",
                refs = [
                    "//src/libraries:impl_netcoreapp_base",
                    "//src/libraries:ref_System.Runtime",
                ],
                descriptors = desc,
                suppressions = supp,
                disable_opt_ipconstprop = (name == "System.Linq.Expressions"),
            )

def framework_crossgen_targets(clrjit, jitinterface, target_arch, target_os, mibc = [], native_crossgen2 = None):
    """Generate crossgen_assembly targets for R2R assemblies.

    Call after framework_illink_targets() so illink outputs exist.

    Args:
        clrjit: Label for the clrjit shared library.
        jitinterface: Label for the jitinterface shared library.
        target_arch: Target architecture (x64, arm64).
        target_os: Target OS (linux, osx, windows).
        mibc: MIBC PGO data files passed as -m: to crossgen2.
        native_crossgen2: Optional label for NativeAOT-compiled crossgen2 binary.
    """
    # All trimmed framework assemblies plus CoreLib as references.
    # CoreLib isn't in the ILLink pipeline (it has its own crossgen path)
    # but crossgen2 needs it to resolve core types.
    all_refs = [":illink_" + name for name in FRAMEWORK_ASSEMBLY_LABELS.keys()] + [
        "//src/coreclr/System.Private.CoreLib:impl_System.Private.CoreLib",
    ]
    for name in R2R_ASSEMBLIES:
        kwargs = dict(
            name = "crossgen_" + name,
            assembly = ":illink_" + name,
            out = "r2r/" + name + ".dll",
            refs = all_refs,
            mibc = mibc,
            clrjit = clrjit,
            jitinterface = jitinterface,
            tags = ["manual"],
            target_arch = target_arch,
            target_os = target_os,
        )
        if native_crossgen2:
            kwargs["native_crossgen2"] = native_crossgen2
        crossgen_assembly(**kwargs)
