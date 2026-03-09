// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Resources;
using System.Resources.Extensions;

#nullable disable

internal sealed class TypeConverterByteArrayResource : IResource
{
    public string Name { get; }
    public string TypeAssemblyQualifiedName { get; }
    public string OriginatingFile { get; }
    public byte[] Bytes { get; }

    public string TypeFullName => NameUtilities.FullNameFromAssemblyQualifiedName(TypeAssemblyQualifiedName);

    public TypeConverterByteArrayResource(string name, string assemblyQualifiedTypeName, byte[] bytes, string originatingFile)
    {
        Name = name;
        TypeAssemblyQualifiedName = assemblyQualifiedTypeName;
        Bytes = bytes;
        OriginatingFile = originatingFile;
    }

    public void AddTo(PreserializedResourceWriter writer)
    {
        writer.AddTypeConverterResource(Name, Bytes, TypeAssemblyQualifiedName);
    }

    void IResource.AddTo(IResourceWriter writer) => AddTo((PreserializedResourceWriter)writer);
}
