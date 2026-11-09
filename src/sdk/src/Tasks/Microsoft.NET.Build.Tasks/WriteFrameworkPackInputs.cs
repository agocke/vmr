// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Versioning;

namespace Microsoft.NET.Build.Tasks;

[MSBuildMultiThreadableTask]
public sealed class WriteFrameworkPackInputs : TaskBase, IMultiThreadableTask
{
    private static readonly object s_captureLock = new();
    private sealed class ResourceCache
    {
        public Dictionary<string, XElement> Packs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> RuntimeGraphs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Written { get; } = new(StringComparer.Ordinal);
    }
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    [Required]
    public string OutputFile { get; set; } = null!;
    [Required]
    public string ResourceDirectory { get; set; } = null!;
    public ITaskItem[] PackRequests { get; set; } = [];
    public ITaskItem[] LookupFacts { get; set; } = [];
    public ITaskItem[] VersionFacts { get; set; } = [];
    public ITaskItem[] PackageFolders { get; set; } = [];
    public ITaskItem[] PackMembers { get; set; } = [];
    public string RuntimeGraphPath { get; set; } = "";
    public string TargetingPackRoot { get; set; } = "";
    public string NetCoreRoot { get; set; } = "";
    public string NETCoreSdkVersion { get; set; } = "";
    public string TargetPlatformIdentifier { get; set; } = "";
    public string TargetPlatformVersion { get; set; } = "";
    public string TargetPlatformDisplayName { get; set; } = "";

    protected override void ExecuteCore()
    {
        try
        {
            lock (s_captureLock)
            {
                const string key = "Microsoft.NET.Build.Tasks.WriteFrameworkPackInputs.Resources.v1";
                ResourceCache cache = BuildEngine4?.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build)
                    as ResourceCache ?? new();
                if (BuildEngine4 is not null && BuildEngine4.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build) is null)
                {
                    BuildEngine4.RegisterTaskObject(key, cache, RegisteredTaskObjectLifetime.Build, allowEarlyCollection: false);
                }
                Capture(cache);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                  or InvalidDataException or System.Xml.XmlException)
        {
            Log.LogError(ex.Message);
        }
    }

    private void Capture(ResourceCache cache)
    {
        OutputFile = TaskEnvironment.GetAbsolutePath(OutputFile).Value;
        ResourceDirectory = TaskEnvironment.GetAbsolutePath(ResourceDirectory).Value;
        List<ITaskItem> facts = [];
        Dictionary<string, string> roots = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> frameworksByRoot = new(StringComparer.Ordinal);
        foreach (ITaskItem lookup in LookupFacts)
        {
            AddFact(facts, lookup);
            string path = lookup.GetMetadata("Path");
            if (path.Length != 0)
            {
                roots[TaskEnvironment.GetAbsolutePath(path).Value] = "";
            }
        }
        foreach (ITaskItem version in VersionFacts)
        {
            AddFact(facts, version);
        }
        NuGetPackageResolver resolver = NuGetPackageResolver.CreateResolver(
            PackageFolders.Select(item => TaskEnvironment.GetAbsolutePath(item.ItemSpec).Value).ToArray());
        foreach (ITaskItem request in PackRequests)
        {
            string id = request.GetMetadata(MetadataKeys.NuGetPackageId);
            string version = request.GetMetadata(MetadataKeys.NuGetPackageVersion);
            if (id.Length == 0 || version.Length == 0)
            {
                continue;
            }
            string? directory = resolver.GetPackageDirectory(id, NuGetVersion.Parse(version), out _);
            TaskItem lookup = new(ProjectCollection.Escape(
                FrameworkPackResources.LookupKey("Package", id, version)));
            lookup.SetMetadata("Kind", "Lookup");
            lookup.SetMetadata("Path", ProjectCollection.Escape(directory ?? ""));
            AddFact(facts, lookup);
            if (directory is not null)
            {
                roots[directory] = Path.Combine(directory,
                    id.ToLowerInvariant() + "." + NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant() + ".nupkg");
            }
            string installedPath = request.GetMetadata(MetadataKeys.Path);
            string? requestRoot = installedPath.Length != 0 && roots.ContainsKey(
                TaskEnvironment.GetAbsolutePath(installedPath).Value)
                ? TaskEnvironment.GetAbsolutePath(installedPath).Value : directory;
            string framework = request.GetMetadata("TargetFramework");
            if (requestRoot is not null && framework.Length != 0)
            {
                if (!frameworksByRoot.TryGetValue(requestRoot, out List<string>? frameworks))
                {
                    frameworksByRoot.Add(requestRoot, frameworks = []);
                }
                frameworks.Add(framework);
            }
        }

        List<XElement> imports = [];
        foreach (KeyValuePair<string, string> entry in roots)
        {
            string root = entry.Key;
            string[] explicitMembers = PackMembers.Where(item => item.GetMetadata("PackRoot").Length != 0 &&
                string.Equals(TaskEnvironment.GetAbsolutePath(item.GetMetadata("PackRoot")).Value,
                              root, StringComparison.Ordinal))
                .Select(item => item.GetMetadata("Path")).ToArray();
            string cacheKey = root + "\n" + entry.Value + "\n" + string.Join("\n", explicitMembers);
            if (!cache.Packs.TryGetValue(cacheKey, out XElement? resource))
            {
                resource = ReadPack(root, entry.Value, explicitMembers);
                cache.Packs.Add(cacheKey, resource);
            }
            if (frameworksByRoot.TryGetValue(root, out List<string>? frameworks))
            {
                if (!resource.Elements("Data").Any(item => (string?)item.Attribute("Path") == "data/FrameworkList.xml") &&
                    !resource.Elements("Member").Any())
                {
                    throw new FormatException($"Framework pack '{root}' requires an acquired archive or explicit member catalog.");
                }
                AddReferenceProbes(resource, root, frameworks);
            }
            string resourceFile = Path.Combine(ResourceDirectory, ResourceKey(cacheKey) + ".g.props");
            XElement item = new("_FrameworkPackResource", new XAttribute("Include", ProjectCollection.Escape(root)),
                new XElement("Content", ProjectCollection.Escape(resource.ToString(SaveOptions.DisableFormatting))));
            WriteResource(cache, resourceFile, new XElement("Project", new XElement("ItemGroup", item)));
            imports.Add(new XElement("Import", new XAttribute("Project", ProjectCollection.Escape(resourceFile))));
        }

        TaskItem context = new("FrameworkPackContext");
        context.SetMetadata("Kind", "Context");
        context.SetMetadata("IsWindows", (Path.DirectorySeparatorChar == '\\').ToString());
        context.SetMetadata("TargetingPackRoot", ProjectCollection.Escape(TargetingPackRoot.Length == 0
            ? "" : TaskEnvironment.GetAbsolutePath(TargetingPackRoot).Value));
        context.SetMetadata("NetCoreRoot", ProjectCollection.Escape(NetCoreRoot.Length == 0
            ? "" : TaskEnvironment.GetAbsolutePath(NetCoreRoot).Value));
        context.SetMetadata("NETCoreSdkVersion", ProjectCollection.Escape(NETCoreSdkVersion));
        if (RuntimeGraphPath.Length != 0)
        {
            string path = TaskEnvironment.GetAbsolutePath(RuntimeGraphPath).Value;
            context.SetMetadata("RuntimeGraphPath", ProjectCollection.Escape(path));
            if (!cache.RuntimeGraphs.TryGetValue(path, out string? json))
            {
                json = File.ReadAllText(path);
                cache.RuntimeGraphs.Add(path, json);
            }
            string resourceFile = Path.Combine(ResourceDirectory, ResourceKey("RuntimeGraph/" + path) + ".g.props");
            WriteResource(cache, resourceFile, new XElement("Project", new XElement("ItemGroup",
                new XElement("_FrameworkPackResource", new XAttribute("Include", ProjectCollection.Escape(path)),
                    new XElement("Kind", "RuntimeGraph"), new XElement("Content", ProjectCollection.Escape(json))))));
            imports.Add(new XElement("Import", new XAttribute("Project", ProjectCollection.Escape(resourceFile))));
        }
        facts.Add(context);
        XElement project = new("Project",
            new XElement("PropertyGroup",
                new XElement("_FrameworkReferenceFetchManifestVersion", "2"),
                new XElement("_RawFrameworkPackFactsVersion", "1"),
                new XElement("_FrameworkReferenceConflictFactsVersion", "1"),
                new XElement("_FrameworkReferencePlatformSDKFactsVersion", "1"),
                new XElement("_FrameworkReferenceManifestTargetPlatformIdentifier", ProjectCollection.Escape(TargetPlatformIdentifier)),
                new XElement("_FrameworkReferenceManifestTargetPlatformVersion", ProjectCollection.Escape(TargetPlatformVersion)),
                new XElement("_FrameworkReferenceManifestTargetPlatformDisplayName", ProjectCollection.Escape(TargetPlatformDisplayName)),
                new XElement("_UsingFrameworkReferenceManifest", "true"),
                new XElement("DisableCheckingDuplicateNuGetItems", "true"),
                new XElement("UseAppHostFromAssetsFile", "false")),
            imports,
            new XElement("ItemGroup", facts.Select(FactElement)));
        Write(OutputFile, project);
    }

    private XElement ReadPack(string root, string archive, string[] supplied)
    {
        XElement pack = new("Pack");
        foreach (string name in new[] { "FrameworkList.xml", "PlatformManifest.txt", "PackageOverrides.txt" })
        {
            string relative = "data/" + name;
            string path = Path.Combine(root, "data", name);
            bool exists = File.Exists(path);
            pack.Add(new XElement("Probe", new XAttribute("Path", relative), new XAttribute("Exists", exists)));
            if (exists)
            {
                pack.Add(new XElement("Data", new XAttribute("Path", relative), File.ReadAllText(path)));
            }
        }
        IEnumerable<string> members;
        if (archive.Length != 0 && File.Exists(archive))
        {
            using ZipArchive package = ZipFile.OpenRead(archive);
            members = package.Entries.Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal))
                .Select(entry => entry.FullName).ToArray();
        }
        else
        {
            XElement? frameworkList = pack.Elements("Data").SingleOrDefault(item =>
                (string?)item.Attribute("Path") == "data/FrameworkList.xml");
            if (supplied.Length == 0 && frameworkList is not null &&
                XElement.Parse(frameworkList.Value).Elements("File").Any(item =>
                    string.Equals((string?)item.Attribute("Type"), "Analyzer", StringComparison.OrdinalIgnoreCase)))
            {
                throw new FormatException($"Framework pack '{root}' requires an acquired archive or explicit member catalog.");
            }
            members = supplied;
        }
        foreach (string member in members)
        {
            FrameworkPackResources.ValidateRelativePath(member);
            pack.Add(new XElement("Member", new XAttribute("Path", member)));
        }
        string[] folders = members.Where(path => path.StartsWith("ref/", StringComparison.Ordinal))
            .Select(path => path.Split('/')[1]).Distinct(StringComparer.Ordinal).ToArray();
        AddReferenceProbes(pack, root, folders.Concat(new[] { "netcoreapp3.0", "net5.0", "netcoreapp5.0" }));
        foreach (string folder in members.Where(path => path.StartsWith("build/", StringComparison.Ordinal)
                     && path.Split('/').Length == 4 && path.Split('/')[2] == "ref")
                     .Select(path => "build/" + path.Split('/')[1] + "/ref").Distinct(StringComparer.Ordinal))
        {
            foreach (string file in Directory.GetFiles(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar)), "*.dll"))
            {
                pack.Add(new XElement("LegacyFile", new XAttribute("Path", folder + "/" + Path.GetFileName(file))));
            }
        }
        return pack;
    }

    private static void AddReferenceProbes(XElement pack, string root, IEnumerable<string> frameworks)
    {
        XElement? data = pack.Elements("Data").SingleOrDefault(item => (string?)item.Attribute("Path") == "data/FrameworkList.xml");
        XElement? list = data is null ? null : XElement.Parse(data.Value);
        foreach (string framework in frameworks
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string folder in framework.Equals("net5.0", StringComparison.OrdinalIgnoreCase)
                         ? new[] { framework, "netcoreapp5.0" } : new[] { framework })
            {
                string relative = "ref/" + folder;
                AddProbe(pack, relative, () => Directory.Exists(Path.Combine(root, "ref", folder)));
                XElement? first = list?.Elements("File").FirstOrDefault();
                if (first is not null)
                {
                    string file = relative + "/" + (string?)first.Attribute("AssemblyName") + ".dll";
                    AddProbe(pack, file, () => File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
        }
    }

    private static void AddProbe(XElement pack, string path, Func<bool> exists)
    {
        FrameworkPackResources.ValidateRelativePath(path);
        if (!pack.Elements("Probe").Any(item => (string?)item.Attribute("Path") == path))
        {
            pack.Add(new XElement("Probe", new XAttribute("Path", path), new XAttribute("Exists", exists())));
        }
    }

    private static string ResourceKey(string value)
    {
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value)))
            .Replace("-", "").ToLowerInvariant();
    }

    private static void WriteResource(ResourceCache cache, string file, XElement project)
    {
        string content = project.ToString() + "\n";
        if (!cache.Written.TryGetValue(file, out string? previous) || previous != content)
        {
            Write(file, project);
            cache.Written[file] = content;
        }
    }

    private static XElement FactElement(ITaskItem fact)
    {
        XElement item = new("_FrameworkPackFact", new XAttribute("Include", ProjectCollection.Escape(fact.ItemSpec)));
        foreach (System.Collections.DictionaryEntry entry in fact.CloneCustomMetadata())
        {
            item.Add(new XElement((string)entry.Key, ProjectCollection.Escape((string?)entry.Value ?? "")));
        }
        return item;
    }

    private static void AddFact(List<ITaskItem> facts, ITaskItem fact)
    {
        ITaskItem? previous = facts.FirstOrDefault(item =>
            item.ItemSpec.Equals(fact.ItemSpec, StringComparison.OrdinalIgnoreCase) &&
            item.GetMetadata("Kind") == fact.GetMetadata("Kind"));
        if (previous is null)
        {
            facts.Add(fact);
        }
        else if (previous.GetMetadata("Path") != fact.GetMetadata("Path") ||
                 previous.GetMetadata("ResolvedVersion") != fact.GetMetadata("ResolvedVersion"))
        {
            throw new FormatException($"Conflicting framework-pack observations '{fact.ItemSpec}'.");
        }
    }

    private static void Write(string file, XElement project)
    {
        string content = project.ToString() + "\n";
        if (!File.Exists(file) || File.ReadAllText(file) != content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }
    }
}
