// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks;

[MSBuildDeclaredIOTask]
public sealed class ReadNuGetAssets : TaskBase
{
    [Required]
    public string FormatVersion { get; set; } = null!;

    public ITaskItem[] Facts { get; set; } = [];

    [Required]
    public string TargetName { get; set; } = null!;

    [Output]
    public ITaskItem[] Libraries { get; set; } = [];

    [Output]
    public ITaskItem[] Assets { get; set; } = [];

    [Output]
    public ITaskItem[] Dependencies { get; set; } = [];

    [Output]
    public ITaskItem[] Files { get; set; } = [];

    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    protected override void ExecuteCore()
    {
        Libraries = [];
        Assets = [];
        Dependencies = [];
        Files = [];

        try
        {
            if (FormatVersion != "1")
            {
                throw new FormatException($"Unsupported fact format '{FormatVersion}'.");
            }

            NuGetInput root = NuGetInput.Read(Facts);
            string assetsVersion = root.GetRequired("version").Require(JsonValueKind.Number).Value;
            if (assetsVersion is not ("3" or "4"))
            {
                throw new FormatException($"Unsupported assets version '{assetsVersion}'.");
            }

            NuGetInput target = root.GetRequired("targets").Require(JsonValueKind.Object)
                .GetRequired(TargetName).Require(JsonValueKind.Object);
            NuGetInput libraryDefinitions = root.GetRequired("libraries").Require(JsonValueKind.Object);
            List<ITaskItem> libraries = [];
            List<ITaskItem> assets = [];
            List<ITaskItem> dependencies = [];
            List<ITaskItem> files = [];

            foreach (NuGetInput library in target.Children)
            {
                library.Require(JsonValueKind.Object);
                NuGetInput definition = libraryDefinitions.GetRequired(library.Name).Require(JsonValueKind.Object);
                int separator = library.Name.LastIndexOf('/');
                if (separator <= 0 || separator == library.Name.Length - 1)
                {
                    throw new FormatException($"Invalid library identity '{library.Name}'.");
                }

                string type = library.GetString("type");
                if (type != definition.GetString("type") || type is not ("package" or "project"))
                {
                    throw new FormatException($"Invalid or inconsistent library type at '{library.Path}'.");
                }

                TaskItem libraryItem = CreateItem(library.Name, library, library);
                Set(libraryItem, "Name", library.Name.Substring(0, separator));
                Set(libraryItem, "Version", library.Name.Substring(separator + 1));
                Set(libraryItem, "Type", type);
                CopyString(definition, libraryItem, "path", "Path");
                CopyString(definition, libraryItem, "msbuildProject", "MSBuildProject");
                libraries.Add(libraryItem);

                foreach (NuGetInput group in library.Children)
                {
                    if (group.Name is "compile" or "runtime" or "native" or "resource" or "runtimeTargets" or "contentFiles" or "analyzers")
                    {
                        foreach (NuGetInput asset in group.Require(JsonValueKind.Object).Children)
                        {
                            asset.Require(JsonValueKind.Object);
                            TaskItem item = CreateItem(asset.Name, asset, library);
                            Set(item, "AssetGroup", group.Name);
                            CopyString(asset, item, "aliases", "Aliases");
                            CopyString(asset, item, "related", "Related");
                            CopyString(asset, item, "locale", "Locale");
                            CopyString(asset, item, "rid", "RuntimeIdentifier");
                            CopyString(asset, item, "assetType", "AssetType");
                            CopyString(asset, item, "buildAction", "BuildAction");
                            CopyString(asset, item, "codeLanguage", "CodeLanguage");
                            CopyString(asset, item, "outputPath", "OutputPath");
                            CopyString(asset, item, "ppOutputPath", "PPOutputPath");
                            CopyBoolean(asset, item, "copyToOutput", "CopyToOutput");
                            CopyBoolean(asset, item, "flatten", "Flatten");
                            assets.Add(item);
                        }
                    }
                    else if (group.Name == "dependencies")
                    {
                        foreach (NuGetInput dependency in group.Require(JsonValueKind.Object).Children)
                        {
                            TaskItem item = CreateItem(dependency.Name, dependency, library);
                            Set(item, "VersionRange", dependency.Require(JsonValueKind.String).Value);
                            dependencies.Add(item);
                        }
                    }
                }

                if (definition.Get("files") is NuGetInput inventory)
                {
                    foreach (NuGetInput file in inventory.Require(JsonValueKind.Array).Children)
                    {
                        files.Add(CreateItem(file.Require(JsonValueKind.String).Value, file, library));
                    }
                }
            }

            // Do not expose a partially validated target to later graph construction.
            Libraries = libraries.ToArray();
            Assets = assets.ToArray();
            Dependencies = dependencies.ToArray();
            Files = files.ToArray();
        }
        catch (FormatException ex)
        {
            Log.LogError(Strings.InvalidNuGetInputFacts, ex.Message);
        }
    }

    private TaskItem CreateItem(string identity, NuGetInput fact, NuGetInput library)
    {
        if (identity.Length == 0)
        {
            throw new FormatException($"Empty item identity at '{fact.Path}'.");
        }
        TaskItem item = new(ProjectCollection.Escape(identity));
        Set(item, "TargetName", TargetName);
        Set(item, "Library", library.Name);
        Set(item, "LibraryType", library.GetString("type"));
        Set(item, "FactPath", fact.Path);
        return item;
    }

    private static void CopyString(NuGetInput source, TaskItem item, string name, string metadata)
    {
        if (source.Get(name) is NuGetInput value)
        {
            Set(item, metadata, value.Require(JsonValueKind.String).Value);
        }
    }

    private static void CopyBoolean(NuGetInput source, TaskItem item, string name, string metadata)
    {
        if (source.Get(name) is NuGetInput value)
        {
            if (value.Kind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new FormatException($"Expected Boolean at '{value.Path}'.");
            }
            Set(item, metadata, value.Value);
        }
    }

    private static void Set(TaskItem item, string name, string value) =>
        item.SetMetadata(name, ProjectCollection.Escape(value));
}
