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
    string raw = Path.GetFullPath(args[2]);
    string rewritten = Path.GetFullPath(args[3]);
    string published = Path.GetFullPath(args[4]);
    int rawOwners = 0, rewriteOwners = 0, publicationOwners = 0;
    foreach (string file in Directory.GetFiles(args[1] + ".actions", "*.json"))
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement action = document.RootElement;
        JsonElement parameters = action.GetProperty("parameters");
        string[] Paths(string name) => parameters.GetProperty(name).GetProperty("items").EnumerateArray()
            .Select(item => Path.GetFullPath(item.GetProperty("itemSpec").GetString(), action.GetProperty("workingDirectory").GetString())).ToArray();
        string[] inputs = Paths("DeclaredInputs");
        string[] outputs = Paths("DeclaredOutputs");
        rawOwners += outputs.Contains(raw) ? 1 : 0;
        if (outputs.Contains(rewritten))
        {
            string task = action.GetProperty("taskType").GetString();
            if ((task != "ClearAssemblyReferenceVersions" && !task.EndsWith(".ClearAssemblyReferenceVersions"))
                || !inputs.SequenceEqual(new[] { raw }) || !outputs.SequenceEqual(new[] { rewritten }))
            {
                throw new Exception("The rewrite must have exactly the original input and a distinct output.");
            }
            rewriteOwners++;
        }
        if (outputs.Contains(published))
        {
            if (!inputs.SequenceEqual(new[] { rewritten }))
            {
                throw new Exception("Publication must consume the rewritten assembly.");
            }
            publicationOwners++;
        }
    }
    if (rawOwners != 1 || rewriteOwners != 1 || publicationOwners != 1)
    {
        throw new Exception("Each assembly must have exactly one producer.");
    }
}
else
{
    byte[] original = File.ReadAllBytes(args[1]);
    byte[] rewritten = File.ReadAllBytes(args[2]);
    if (original.SequenceEqual(rewritten)
        || !rewritten.SequenceEqual(File.ReadAllBytes(args[3]))
        || !original.SequenceEqual(File.ReadAllBytes(args[4])))
    {
        throw new Exception("Separate rewriting must match ordinary bytes and preserve the original compiler output.");
    }
    using PEReader source = new(new MemoryStream(original));
    using PEReader output = new(new MemoryStream(rewritten));
    MetadataReader before = source.GetMetadataReader();
    MetadataReader after = output.GetMetadataReader();
    if (!before.AssemblyReferences.Any(reference => before.GetAssemblyReference(reference).Version != new Version(0, 0, 0, 0))
        || after.AssemblyReferences.Any(reference => after.GetAssemblyReference(reference).Version != new Version(0, 0, 0, 0))
        || before.GetAssemblyDefinition().Version != after.GetAssemblyDefinition().Version
        || before.GetGuid(before.GetModuleDefinition().Mvid) != after.GetGuid(after.GetModuleDefinition().Mvid))
    {
        throw new Exception("Only reference versions must be cleared; the assembly version and MVID must be preserved.");
    }
    foreach (string backup in args.Skip(5))
    {
        if (!original.SequenceEqual(File.ReadAllBytes(backup)))
        {
            throw new Exception("Debug backups must contain the untouched compiler output.");
        }
    }
}
