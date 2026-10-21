// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace Microsoft.NET.Build.Tasks;

internal static class NuGetAssets
{
    // Only data consumed by package selection is projected. The original facts retain
    // the rest; in particular, this never invokes NuGet's ambient project-spec reader.
    public static LockFile Read(NuGetInput root, string source)
    {
        string version = root.GetRequired("version").Require(JsonValueKind.Number).Value;
        if (version is not ("3" or "4"))
        {
            throw new FormatException($"Unsupported assets version '{version}'.");
        }

        LockFile result = new()
        {
            Version = version == "3" ? 3 : 4,
            Path = source,
            PackageSpec = new PackageSpec { RestoreMetadata = new ProjectRestoreMetadata() },
        };
        NuGetInput project = root.GetRequired("project").Require(JsonValueKind.Object);
        foreach (NuGetInput framework in project.GetRequired("frameworks").Require(JsonValueKind.Object).Children)
        {
            framework.Require(JsonValueKind.Object);
            string name = String(framework, "framework") ?? framework.Name;
            NuGetFramework parsed = NuGetFramework.Parse(name);
            if (parsed.IsUnsupported)
            {
                throw new FormatException($"Unsupported framework '{name}' at '{framework.Path}'.");
            }
            result.PackageSpec.TargetFrameworks.Add(new TargetFrameworkInformation
            {
                FrameworkName = parsed,
                TargetAlias = String(framework, "targetAlias") ?? framework.Name,
            });
        }
        if (project.Get("restore") is NuGetInput restore)
        {
            restore.Require(JsonValueKind.Object);
            if (restore.Get("restoreEnableAnalyzerAssets") is NuGetInput analyzers)
            {
                if (analyzers.Kind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new FormatException($"Expected Boolean at '{analyzers.Path}'.");
                }
                result.PackageSpec.RestoreMetadata.RestoreEnableAnalyzerAssets = analyzers.Kind == JsonValueKind.True;
            }
        }

        foreach (NuGetInput target in root.GetRequired("targets").Require(JsonValueKind.Object).Children)
        {
            target.Require(JsonValueKind.Object);
            int separator = target.Name.IndexOf('/');
            string alias = separator < 0 ? target.Name : target.Name.Substring(0, separator);
            TargetFrameworkInformation? framework = result.PackageSpec.TargetFrameworks.FirstOrDefault(
                item => item.TargetAlias == alias || item.FrameworkName.DotNetFrameworkName == alias);
            if (framework == null)
            {
                throw new FormatException($"Missing framework definition for '{target.Name}'.");
            }
            LockFileTarget model = new()
            {
                Name = target.Name,
                TargetAlias = framework.TargetAlias,
                TargetFramework = framework.FrameworkName,
                RuntimeIdentifier = separator < 0 ? null : target.Name.Substring(separator + 1),
            };
            foreach (NuGetInput library in target.Children)
            {
                library.Require(JsonValueKind.Object);
                (string name, NuGetVersion packageVersion) = Identity(library.Name);
                LockFileTargetLibrary item = new()
                {
                    Name = name,
                    Version = packageVersion,
                    Type = library.GetString("type"),
                    Framework = String(library, "framework"),
                    CompileTimeAssemblies = Items(library, "compile", path => new LockFileItem(path)),
                    RuntimeAssemblies = Items(library, "runtime", path => new LockFileItem(path)),
                    NativeLibraries = Items(library, "native", path => new LockFileItem(path)),
                    ResourceAssemblies = Items(library, "resource", path => new LockFileItem(path)),
                    AnalyzerAssets = Items(library, "analyzers", path => new LockFileItem(path)),
                    RuntimeTargets = Items(library, "runtimeTargets", path => new LockFileRuntimeTarget(path)),
                    ContentFiles = Items(library, "contentFiles", path => new LockFileContentFile(path)),
                    FrameworkAssemblies = Strings(library.Get("frameworkAssemblies")),
                    FrameworkReferences = Strings(library.Get("frameworkReferences")),
                };
                if (item.Type is not ("package" or "project"))
                {
                    throw new FormatException($"Unsupported library type at '{library.Path}'.");
                }
                if (library.Get("dependencies") is NuGetInput dependencies)
                {
                    foreach (NuGetInput dependency in dependencies.Require(JsonValueKind.Object).Children)
                    {
                        if (!VersionRange.TryParse(dependency.Require(JsonValueKind.String).Value, out VersionRange? range))
                        {
                            throw new FormatException($"Invalid dependency range at '{dependency.Path}'.");
                        }
                        item.Dependencies.Add(new PackageDependency(dependency.Name, range));
                    }
                }
                model.Libraries.Add(item);
            }
            result.Targets.Add(model);
        }

        Dictionary<string, LockFileLibrary> definitions = new(StringComparer.OrdinalIgnoreCase);
        foreach (NuGetInput library in root.GetRequired("libraries").Require(JsonValueKind.Object).Children)
        {
            library.Require(JsonValueKind.Object);
            (string name, NuGetVersion packageVersion) = Identity(library.Name);
            LockFileLibrary definition = new()
            {
                Name = name,
                Version = packageVersion,
                Type = library.GetString("type"),
                Path = String(library, "path"),
                MSBuildProject = String(library, "msbuildProject"),
                Files = Strings(library.Get("files")),
            };
            string identity = name + "/" + packageVersion.ToNormalizedString();
            if (definitions.ContainsKey(identity))
            {
                throw new FormatException($"Duplicate library identity '{identity}'.");
            }
            definitions.Add(identity, definition);
            result.Libraries.Add(definition);
        }
        foreach (LockFileTarget target in result.Targets)
        {
            foreach (LockFileTargetLibrary library in target.Libraries)
            {
                if (!definitions.TryGetValue(library.Name + "/" + library.Version!.ToNormalizedString(), out LockFileLibrary? definition) ||
                    definition.Type != library.Type)
                {
                    throw new FormatException($"Missing or inconsistent library definition for '{library.Name}/{library.Version}'.");
                }
            }
        }
        foreach (NuGetInput folder in root.GetRequired("packageFolders").Require(JsonValueKind.Object).Children)
        {
            result.PackageFolders.Add(new LockFileItem(folder.Name));
        }
        foreach (NuGetInput group in root.GetRequired("projectFileDependencyGroups").Require(JsonValueKind.Object).Children)
        {
            result.ProjectFileDependencyGroups.Add(new ProjectFileDependencyGroup(group.Name, Strings(group)));
        }
        if (root.Get("logs") is NuGetInput logs)
        {
            foreach (NuGetInput message in logs.Require(JsonValueKind.Array).Children)
            {
                message.Require(JsonValueKind.Object);
                if (!Enum.TryParse(message.GetString("level"), ignoreCase: true, out LogLevel level) ||
                    !Enum.TryParse(message.GetString("code"), out NuGetLogCode code))
                {
                    throw new FormatException($"Invalid diagnostic at '{message.Path}'.");
                }
                result.LogMessages.Add(new AssetsLogMessage(level, code, message.GetString("message"))
                {
                    LibraryId = String(message, "libraryId"),
                    TargetGraphs = Strings(message.Get("targetGraphs")).ToArray(),
                });
            }
        }
        return result;
    }

    private static (string, NuGetVersion) Identity(string identity)
    {
        int separator = identity.LastIndexOf('/');
        if (separator <= 0 || !NuGetVersion.TryParse(identity.Substring(separator + 1), out NuGetVersion? version))
        {
            throw new FormatException($"Invalid library identity '{identity}'.");
        }
        return (identity.Substring(0, separator), version);
    }

    private static string? String(NuGetInput source, string name) =>
        source.Get(name)?.Require(JsonValueKind.String).Value;

    private static List<string> Strings(NuGetInput? source) =>
        source == null ? [] : source.Require(JsonValueKind.Array).Children
            .Select(item => item.Require(JsonValueKind.String).Value).ToList();

    private static List<T> Items<T>(NuGetInput library, string name, Func<string, T> create) where T : LockFileItem
    {
        List<T> items = [];
        if (library.Get(name) is NuGetInput group)
        {
            foreach (NuGetInput asset in group.Require(JsonValueKind.Object).Children)
            {
                if (asset.Name.Length == 0)
                {
                    throw new FormatException($"Empty asset path at '{asset.Path}'.");
                }
                T item = create(asset.Name);
                foreach (NuGetInput property in asset.Require(JsonValueKind.Object).Children)
                {
                    if (property.Kind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number))
                    {
                        throw new FormatException($"Unsupported asset property at '{property.Path}'.");
                    }
                    item.Properties.Add(property.Name, property.Value);
                }
                items.Add(item);
            }
        }
        return items;
    }
}
