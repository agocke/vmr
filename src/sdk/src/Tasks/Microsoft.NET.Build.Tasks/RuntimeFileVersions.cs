// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.NET.HostModel.Win32Resources;

namespace Microsoft.NET.Build.Tasks;

internal static class RuntimeFileVersions
{
    internal static (string? AssemblyVersion, string FileVersion) Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
        {
            return (null, "0.0.0.0");
        }

        stream.Position = 0;
        using var pe = new PEReader(stream);
        if (pe.HasMetadata)
        {
            var metadata = pe.GetMetadataReader();
            if (metadata.IsAssembly)
            {
                var assembly = metadata.GetAssemblyDefinition();
                string fileVersion = "0.0.0.0";
                foreach (var handle in assembly.GetCustomAttributes())
                {
                    var attribute = metadata.GetCustomAttribute(handle);
                    EntityHandle typeHandle = attribute.Constructor.Kind switch
                    {
                        HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                        HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                        _ => default
                    };
                    StringHandle typeNamespace;
                    StringHandle typeName;
                    if (typeHandle.Kind == HandleKind.TypeReference)
                    {
                        var type = metadata.GetTypeReference((TypeReferenceHandle)typeHandle);
                        typeNamespace = type.Namespace;
                        typeName = type.Name;
                    }
                    else if (typeHandle.Kind == HandleKind.TypeDefinition)
                    {
                        var type = metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                        typeNamespace = type.Namespace;
                        typeName = type.Name;
                    }
                    else
                    {
                        continue;
                    }

                    if (metadata.StringComparer.Equals(typeNamespace, "System.Reflection") &&
                        metadata.StringComparer.Equals(typeName, "AssemblyFileVersionAttribute"))
                    {
                        var value = metadata.GetBlobReader(attribute.Value);
                        if (value.ReadUInt16() != 1)
                        {
                            throw new BadImageFormatException();
                        }

                        string? version = value.ReadSerializedString();
                        if (!Version.TryParse(version, out var parsed) || parsed.Major > ushort.MaxValue || parsed.Minor > ushort.MaxValue ||
                            parsed.Build > ushort.MaxValue || parsed.Revision > ushort.MaxValue)
                        {
                            throw new BuildErrorException(Strings.UnsupportedDepsFileParameter, "AssemblyFileVersionAttribute", version ?? "");
                        }

                        fileVersion = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision)).ToString();
                    }
                }

                return (assembly.Version.ToString(), fileVersion);
            }
        }

        string? nativeVersion = null;
        foreach (var resource in new ResourceData(pe).GetAllResources().Where(resource => resource.type is ushort type && type == 16))
        {
            using var reader = new BinaryReader(new MemoryStream(resource.data), Encoding.Unicode);
            int length = reader.ReadUInt16();
            int valueLength = reader.ReadUInt16();
            reader.ReadUInt16();
            var key = new StringBuilder();
            char character;
            while ((character = reader.ReadChar()) != '\0')
            {
                key.Append(character);
            }

            reader.BaseStream.Position = (reader.BaseStream.Position + 3) & ~3L;
            // VS_VERSION_INFO contains a VS_FIXEDFILEINFO value (Win32 version-resource format).
            if (key.ToString() != "VS_VERSION_INFO" || valueLength < 52 || length > resource.data.Length ||
                reader.BaseStream.Position > length - 52 || reader.ReadUInt32() != 0xFEEF04BD)
            {
                throw new BadImageFormatException();
            }

            reader.ReadUInt32();
            uint high = reader.ReadUInt32();
            uint low = reader.ReadUInt32();
            string version = new Version((int)(high >> 16), (int)(high & 0xffff), (int)(low >> 16), (int)(low & 0xffff)).ToString();
            if (nativeVersion is not null && nativeVersion != version)
            {
                throw new BuildErrorException(Strings.UnsupportedDepsFileParameter, "native file version", path);
            }

            nativeVersion = version;
        }

        return (null, nativeVersion ?? "0.0.0.0");
    }
}
