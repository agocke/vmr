// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks;

public sealed class WriteFrameworkRestoreInputs : TaskBase
{
    [Required]
    public ITaskItem[] Requests { get; set; } = [];
    [Output]
    public int WrittenModels { get; private set; }

    protected override void ExecuteCore()
    {
        WrittenModels = 0;
        try
        {
            StringComparer paths = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Dictionary<string, ITaskItem> headers = new(StringComparer.Ordinal);
            Dictionary<string, List<ITaskItem>> inputs = new(StringComparer.Ordinal);
            Dictionary<string, string[]> foldersByAssets = new(paths);
            HashSet<string> sourceFiles = new(paths);
            foreach (ITaskItem request in Requests)
            {
                if (request.GetMetadata("Type") == "FrameworkPackInputRequest")
                {
                    headers.Add(request.ItemSpec, request);
                    inputs.Add(request.ItemSpec, []);
                    foreach (string name in new[] { "AssetsFile", "OutputFile", "ResourceDirectory" })
                    {
                        Absolute(request, name);
                    }
                    string assets = Absolute(request, "AssetsFile");
                    sourceFiles.Add(assets);
                    string graph = request.GetMetadata("RuntimeGraphPath");
                    if (graph.Length != 0)
                    {
                        sourceFiles.Add(Absolute(request, "RuntimeGraphPath"));
                    }
                    if (!foldersByAssets.ContainsKey(assets))
                    {
                        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
                        foldersByAssets.Add(assets, document.RootElement.GetProperty("packageFolders")
                            .EnumerateObject().Select(folder => Absolute(folder.Name)).ToArray());
                    }
                }
            }
            foreach (ITaskItem request in Requests)
            {
                if (request.GetMetadata("Type") == "FrameworkPackInputRequest")
                {
                    continue;
                }
                if (request.GetMetadata("Type") != "FrameworkPackInput" ||
                    !inputs.TryGetValue(request.GetMetadata("Binding"), out List<ITaskItem>? rows))
                {
                    throw new FormatException("Unbound framework-pack acquisition input.");
                }
                if (request.GetMetadata("InputKind") is not ("PackRequest" or "Lookup" or "Version" or "Member"))
                {
                    throw new FormatException("Unknown framework-pack acquisition input kind.");
                }
                rows.Add(request);
            }

            List<WriteFrameworkPackInputs> writers = [];
            foreach (IGrouping<string, ITaskItem> group in headers.Values.GroupBy(request => Absolute(request, "OutputFile"), paths))
            {
                if (sourceFiles.Contains(group.Key))
                {
                    throw new FormatException($"Framework-pack facts cannot replace acquisition inputs: '{group.Key}'.");
                }
                ITaskItem first = group.First();
                string[] folders = foldersByAssets[Absolute(first, "AssetsFile")];
                Dictionary<string, string[]> catalogs = new(paths);
                foreach (ITaskItem request in group)
                {
                    foreach (string name in new[] { "ResourceDirectory", "TargetingPackRoot", "NetCoreRoot" })
                    {
                        if (!paths.Equals(OptionalAbsolute(request, name), OptionalAbsolute(first, name)))
                        {
                            throw new FormatException($"Incompatible framework-pack acquisition context for '{group.Key}': {name}.");
                        }
                    }
                    if (request.GetMetadata("NETCoreSdkVersion") != first.GetMetadata("NETCoreSdkVersion") ||
                        !folders.SequenceEqual(foldersByAssets[Absolute(request, "AssetsFile")], paths))
                    {
                        throw new FormatException($"Incompatible framework-pack acquisition SDK or package-folder precedence for '{group.Key}'.");
                    }
                    foreach (IGrouping<string, ITaskItem> catalog in inputs[request.ItemSpec]
                                 .Where(row => row.GetMetadata("InputKind") == "Member")
                                 .GroupBy(row => Absolute(row, "PackRoot"), paths))
                    {
                        string[] members = catalog.Select(row => row.GetMetadata("Path")).Distinct(StringComparer.Ordinal).ToArray();
                        if (catalogs.TryGetValue(catalog.Key, out string[]? previous) &&
                            !members.SequenceEqual(previous, StringComparer.Ordinal))
                        {
                            throw new FormatException($"Conflicting framework-pack member catalogs for '{catalog.Key}'. Use separate resource outputs.");
                        }
                        catalogs[catalog.Key] = members;
                    }
                }
                ITaskItem[] rows = group.SelectMany(request => inputs[request.ItemSpec]).ToArray();
                ITaskItem[] Select(string kind) => rows.Where(row => row.GetMetadata("InputKind") == kind).Select(row =>
                {
                    TaskItem item = new(row);
                    item.ItemSpec = ProjectCollection.Escape(row.GetMetadata("InputIdentity"));
                    foreach (string name in new[] { "Type", "ProjectUniqueName", "Binding", "InputKind", "InputIdentity" })
                    {
                        item.RemoveMetadata(name);
                    }
                    return (ITaskItem)item;
                }).ToArray();
                writers.Add(new WriteFrameworkPackInputs(Log)
                {
                    BuildEngine = BuildEngine,
                    OutputFile = group.Key,
                    ResourceDirectory = Absolute(first, "ResourceDirectory"),
                    TargetingPackRoot = OptionalAbsolute(first, "TargetingPackRoot"),
                    NetCoreRoot = OptionalAbsolute(first, "NetCoreRoot"),
                    NETCoreSdkVersion = first.GetMetadata("NETCoreSdkVersion"),
                    RuntimeGraphPaths = group.Select(request => request.GetMetadata("RuntimeGraphPath"))
                        .Where(path => path.Length != 0).Distinct(paths)
                        .Select(path => (ITaskItem)new TaskItem(ProjectCollection.Escape(Absolute(path)))).ToArray(),
                    PackageFolders = folders.Select(path => (ITaskItem)new TaskItem(ProjectCollection.Escape(path))).ToArray(),
                    PackRequests = Select("PackRequest"),
                    LookupFacts = Select("Lookup"),
                    VersionFacts = Select("Version"),
                    PackMembers = catalogs.SelectMany(catalog => catalog.Value.Select(path =>
                    {
                        TaskItem item = new(ProjectCollection.Escape(path));
                        CollectFrameworkPackInputs.Set(item, "PackRoot", catalog.Key);
                        CollectFrameworkPackInputs.Set(item, "Path", path);
                        return (ITaskItem)item;
                    })).ToArray(),
                    CapturePlatformSDK = false,
                });
            }
            foreach (WriteFrameworkPackInputs writer in writers)
            {
                if (!writer.Execute())
                {
                    return;
                }
                WrittenModels++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException
                                  or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.LogError(ex.Message);
        }
    }

    private static string OptionalAbsolute(ITaskItem item, string name)
        => item.GetMetadata(name).Length == 0 ? "" : Absolute(item, name);

    private static string Absolute(ITaskItem item, string name) => Absolute(item.GetMetadata(name));

    private static string Absolute(string value)
        => Path.IsPathRooted(value) ? Path.GetFullPath(value)
            : throw new FormatException($"Framework-pack input requests require absolute paths: '{value}'.");
}
