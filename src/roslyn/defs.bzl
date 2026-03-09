# Roslyn Bazel build definitions

load("@bazel_skylib//rules:write_file.bzl", "write_file")

# Microsoft Shared Library public key (PublicKeyToken=31bf3856ad364e35)
ROSLYN_PUBLIC_KEY = "0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9"

# Strong name key file for public signing
ROSLYN_KEYFILE = "//src/arcade/src/Microsoft.DotNet.Arcade.Sdk/tools/snk:35MSSharedLib1024.snk"

# Assembly version that matches the NuGet packages (4.11.0)
ROSLYN_ASSEMBLY_VERSION = "4.11.0.0"

def roslyn_assembly_info(name, assembly_version = ROSLYN_ASSEMBLY_VERSION, **kwargs):
    """Generate an AssemblyInfo.cs with version attributes for Roslyn assemblies."""
    write_file(
        name = name,
        out = name + ".cs",
        content = [
            "using System.Reflection;",
            '[assembly: AssemblyVersion("%s")]' % assembly_version,
            '[assembly: AssemblyFileVersion("%s")]' % assembly_version,
            "",
        ],
        **kwargs
    )
