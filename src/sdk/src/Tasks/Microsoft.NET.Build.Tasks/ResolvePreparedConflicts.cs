// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.Build.Tasks.ConflictResolution;

namespace Microsoft.NET.Build.Tasks;

[MSBuildDeclaredIOTask]
public sealed class ResolvePreparedConflicts : TaskBase
{
    [Required]
    public string FrameworkFactsVersion { get; set; } = "";
    [Required]
    public string PackageFactsVersion { get; set; } = "";
    [Required]
    public string ProjectDirectory { get; set; } = "";
    public ITaskItem[] FileFacts { get; set; } = [];
    public ITaskItem[] PlatformItems { get; set; } = [];
    public ITaskItem[] References { get; set; } = [];
    public ITaskItem[] Analyzers { get; set; } = [];
    public ITaskItem[] ReferenceCopyLocalPaths { get; set; } = [];
    public ITaskItem[] OtherRuntimeItems { get; set; } = [];
    public ITaskItem[] PackageOverrides { get; set; } = [];
    public string[] PreferredPackages { get; set; } = [];
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public ITaskItem[] ReferencesWithoutConflicts { get; set; } = [];
    [Output]
    public ITaskItem[] AnalyzersWithoutConflicts { get; set; } = [];
    [Output]
    public ITaskItem[] ReferenceCopyLocalPathsWithoutConflicts { get; set; } = [];
    [Output]
    public ITaskItem[] Conflicts { get; set; } = [];

    protected override void ExecuteCore()
    {
        try
        {
            if (FrameworkFactsVersion != "1" || PackageFactsVersion != "1" || !Path.IsPathRooted(ProjectDirectory))
            {
                throw new FormatException("Missing or unsupported conflict facts or absolute project directory.");
            }
            Dictionary<string, ITaskItem> files = new(StringComparer.Ordinal);
            foreach (ITaskItem fact in FileFacts)
            {
                files.Add(AbsolutePath(fact.ItemSpec), fact);
            }
            Dictionary<ITaskItem, ITaskItem> originals = [];
            ITaskItem[] CopyItems(ITaskItem[] items) => items.Select(item =>
            {
                ParseVersion(item, "AssemblyVersion");
                ParseVersion(item, "FileVersion");
                TaskItem copy = new(item);
                // The hardened path resolver has already validated ItemSpec; HintPath is not a second identity.
                copy.RemoveMetadata("HintPath");
                originals.Add(copy, item);
                return (ITaskItem)copy;
            }).ToArray();

            ResolvePackageFileConflicts selection = new(Log)
            {
                References = CopyItems(References),
                Analyzers = CopyItems(Analyzers),
                ReferenceCopyLocalPaths = CopyItems(ReferenceCopyLocalPaths),
                OtherRuntimeItems = CopyItems(OtherRuntimeItems),
                PackageOverrides = PackageOverrides,
                PreferredPackages = PreferredPackages,
            };
            // RAR has already added direct-reference copies; do not compare an artifact with its own copy.
            IEnumerable<string> CopyPaths(ITaskItem item)
            {
                yield return item.ItemSpec;
                string symbols = item.GetMetadata("DebugSymbolsFile");
                if (symbols.Length != 0)
                {
                    yield return symbols;
                }
            }
            HashSet<string> directReferencePaths = new(selection.References!
                .Where(item => ItemUtilities.GetReferenceTargetPath(item) != null)
                .SelectMany(CopyPaths), StringComparer.Ordinal);
            ITaskItem[] directCopies = selection.ReferenceCopyLocalPaths!
                .Where(item => directReferencePaths.Contains(item.ItemSpec)).ToArray();
            selection.ReferenceCopyLocalPaths = selection.ReferenceCopyLocalPaths!
                .Where(item => !directReferencePaths.Contains(item.ItemSpec)).ToArray();
            ConflictItem CreateItem(ITaskItem item, ConflictItemType type)
                => new(item, type,
                    () => ReadFileVersion(item, "AssemblyVersion"),
                    () => ReadFileVersion(item, "FileVersion"));
            Version? ReadFileVersion(ITaskItem item, string name)
            {
                if (!files.TryGetValue(AbsolutePath(item.ItemSpec), out ITaskItem? fact))
                {
                    throw new FormatException($"Missing conflict version facts for '{item.ItemSpec}'; supply external facts or producer version metadata.");
                }
                return ParseVersion(fact, name);
            }

            selection.ResolveConflicts(null, PlatformItems.Select(item => new ConflictItem(
                item.ItemSpec, item.GetMetadata("PackageId"), ParseVersion(item, "AssemblyVersion"), ParseVersion(item, "FileVersion"))),
                CreateItem, (first, second) =>
                {
                    if (first.SourcePath != second.SourcePath)
                    {
                        throw new FormatException($"SDK policy could not choose between '{first.DisplayName}' and '{second.DisplayName}'.");
                    }
                });
            if (Log.HasLoggedErrors)
            {
                return;
            }

            ITaskItem[] RestoreMetadata(ITaskItem[]? items)
            {
                foreach (ITaskItem item in items ?? [])
                {
                    string hintPath = originals[item].GetMetadata("HintPath");
                    if (hintPath.Length != 0)
                    {
                        item.SetMetadata("HintPath", ProjectCollection.Escape(hintPath));
                    }
                }
                return items ?? [];
            }
            ReferencesWithoutConflicts = RestoreMetadata(selection.ReferencesWithoutConflicts);
            foreach (ITaskItem reference in ReferencesWithoutConflicts)
            {
                if (ItemUtilities.GetReferenceTargetPath(reference) == null)
                {
                    reference.SetMetadata("CopyLocal", "false");
                }
            }
            AnalyzersWithoutConflicts = RestoreMetadata(selection.AnalyzersWithoutConflicts);
            HashSet<string> survivingCopies = new(ReferencesWithoutConflicts
                .Where(item => ItemUtilities.GetReferenceTargetPath(item) != null)
                .SelectMany(CopyPaths), StringComparer.Ordinal);
            ReferenceCopyLocalPathsWithoutConflicts = RestoreMetadata((selection.ReferenceCopyLocalPathsWithoutConflicts ?? [])
                .Concat(directCopies.Where(item => survivingCopies.Contains(item.ItemSpec))).ToArray());
            Conflicts = selection.Conflicts ?? [];
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Log.LogError(Strings.InvalidReferenceConflictFacts, ex.Message);
        }
    }

    private string AbsolutePath(string path) => Path.GetFullPath(Path.Combine(ProjectDirectory, path));

    private static Version? ParseVersion(ITaskItem item, string name)
    {
        string value = item.GetMetadata(name);
        if (value.Length == 0)
        {
            return null;
        }
        if (!Version.TryParse(value, out Version? version))
        {
            throw new FormatException($"Invalid {name} '{value}' for '{item.ItemSpec}'.");
        }
        return version;
    }
}
