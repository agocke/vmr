// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

if (args[0] == "graph")
{
    string source = Path.GetFullPath(args[2]);
    string assembly = Path.GetFullPath(args[3]);
    int sourceOwners = 0, compilerOwners = 0;
    foreach (string file in Directory.GetFiles(args[1] + ".actions", "*.json"))
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement action = document.RootElement;
        JsonElement parameters = action.GetProperty("parameters");
        string[] Paths(string name) => parameters.GetProperty(name).GetProperty("items").EnumerateArray()
            .Select(item => Path.GetFullPath(item.GetProperty("itemSpec").GetString(), action.GetProperty("workingDirectory").GetString())).ToArray();
        string[] inputs = Paths("DeclaredInputs");
        string[] outputs = Paths("DeclaredOutputs");
        string task = action.GetProperty("taskType").GetString().Split('.').Last();
        if (outputs.Contains(source))
        {
            if (task != "WriteLinesToFile" || inputs.Length != 0 || !outputs.SequenceEqual(new[] { source }))
            {
                throw new Exception("Forwarded-type source must have one exact output and no file inputs.");
            }
            sourceOwners++;
        }
        if (outputs.Contains(assembly))
        {
            if (task != "CompileManaged" || !inputs.Contains(source))
            {
                throw new Exception("Compilation must consume the future generated source.");
            }
            compilerOwners++;
        }
    }
    if (sourceOwners != 1 || compilerOwners != 1)
    {
        throw new Exception("Generated source and compiler assembly must each have one owner.");
    }
}
else
{
    using PEReader pe = new(File.OpenRead(args[1]));
    MetadataReader metadata = pe.GetMetadataReader();
    TypeDefinition[] types = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).ToArray();
    if (!types.Any(type => metadata.GetString(type.Namespace) == "ForwardedFixture"
                          && metadata.GetString(type.Name) == "Plain" + args[2]))
    {
        throw new Exception("The assembly must contain the generated plain type.");
    }
    TypeDefinition container = types.Single(type => metadata.GetString(type.Namespace) == "ForwardedFixture"
                                                   && metadata.GetString(type.Name) == "Container");
    string[] nested = container.GetNestedTypes().Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name)).Order().ToArray();
    if (!nested.SequenceEqual(new[] { "First", "Second" }))
    {
        throw new Exception("InnerClass metadata must generate both nested types.");
    }
}
