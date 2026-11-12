// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.RuntimeModel;

namespace Microsoft.NET.Build.Tasks;

internal sealed class FrameworkPackResources
{
    private readonly Dictionary<string, ITaskItem> _lookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XElement> _packs;
    private readonly Dictionary<string, bool> _presence;
    private readonly TaskEnvironment _environment;
    private readonly Dictionary<string, XElement[]> _runtimeGraphRows;
    private readonly ITaskItem _context;
    private readonly Dictionary<string, RuntimeGraph> _runtimeGraphs;

    public bool IsWindows { get; }

    public FrameworkPackResources(string version, ITaskItem[] facts, ITaskItem[] resources,
                                 TaskEnvironment environment)
    {
        if (version != "2")
        {
            throw new FormatException("Missing or unsupported raw framework-pack facts.");
        }
        _environment = environment;
        StringComparer paths = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _packs = new(paths);
        _presence = new(paths);
        _runtimeGraphRows = new(paths);
        _runtimeGraphs = new(paths);
        ITaskItem? context = null;
        foreach (ITaskItem fact in facts)
        {
            switch (fact.GetMetadata("Kind"))
            {
                case "Lookup":
                    if (_lookups.ContainsKey(fact.ItemSpec))
                    {
                        throw new FormatException($"Duplicate framework-pack lookup '{fact.ItemSpec}'.");
                    }
                    _lookups.Add(fact.ItemSpec, fact);
                    break;
                case "Version":
                    if (_versions.ContainsKey(fact.ItemSpec))
                    {
                        throw new FormatException($"Duplicate framework-pack version '{fact.ItemSpec}'.");
                    }
                    _versions.Add(fact.ItemSpec, fact.GetMetadata("ResolvedVersion"));
                    break;
                case "Context":
                    if (context is not null)
                    {
                        throw new FormatException("Duplicate framework-pack acquisition context.");
                    }
                    context = fact;
                    break;
                default:
                    throw new FormatException("Unknown framework-pack fact kind.");
            }
        }
        if (context is null || !bool.TryParse(context.GetMetadata("IsWindows"), out bool isWindows))
        {
            throw new FormatException("Missing framework-pack acquisition context.");
        }
        IsWindows = isWindows;
        _context = context;
        foreach (IGrouping<string, ITaskItem> group in resources.GroupBy(resource => Absolute(resource.ItemSpec), paths))
        {
            string root = group.Key;
            XElement[] rows = group.Select(ReadRow).ToArray();
            if (rows.Any(row => row.Name == "RuntimeGraph"))
            {
                if (rows.Count(row => row.Name == "RuntimeGraph") != 1)
                {
                    throw new FormatException("Invalid or duplicate framework runtime graph resource.");
                }
                _runtimeGraphRows.Add(root, rows);
                continue;
            }
            if (rows.Count(row => row.Name == "Pack") != 1)
            {
                throw new FormatException($"Invalid or duplicate framework-pack resource '{root}'.");
            }
            XElement pack = new("Pack", rows.Where(row => row.Name != "Pack"));
            _packs.Add(root, pack);
            _presence.Add(root, true);
            HashSet<string> dataPaths = new(StringComparer.Ordinal);
            foreach (XElement data in pack.Elements("Data"))
            {
                string path = Required(data, "Path");
                ValidateRelativePath(path);
                if (!dataPaths.Add(path))
                {
                    throw new FormatException($"Duplicate framework-pack data '{path}'.");
                }
            }
            foreach (XElement row in pack.Elements())
            {
                switch (row.Name.LocalName)
                {
                    case "FrameworkList":
                        if (pack.Elements("FrameworkList").Count() != 1)
                        {
                            throw new FormatException("Duplicate framework-pack framework list.");
                        }
                        break;
                    case "FrameworkFile":
                        if (row.Attribute("Path") is not null)
                        {
                            ValidateRelativePath(Required(row, "Path"));
                        }
                        break;
                    case "PlatformFile":
                        foreach (string attribute in new[] { "FileName", "PackageId", "AssemblyVersion", "FileVersion" })
                        {
                            Required(row, attribute);
                        }
                        break;
                    case "PackageOverride":
                        Required(row, "PackageId");
                        if (!NuGet.Versioning.NuGetVersion.TryParse(Required(row, "Version"), out _))
                        {
                            throw new FormatException("Invalid framework-pack package override version.");
                        }
                        break;
                    case "Data":
                    case "Member":
                    case "LegacyFile":
                    case "Probe":
                        break;
                    default:
                        throw new FormatException($"Unknown framework-pack resource kind '{row.Name}'.");
                }
            }
            foreach (XElement member in pack.Elements().Where(item => item.Name == "Member" || item.Name == "LegacyFile"))
            {
                string path = Required(member, "Path");
                ValidateRelativePath(path);
                if (member.Name == "LegacyFile" && path.IndexOf('/') < 0)
                {
                    throw new FormatException("Invalid legacy framework-pack member path.");
                }
            }
            foreach (XElement probe in pack.Elements("Probe"))
            {
                string relative = Required(probe, "Path");
                ValidateRelativePath(relative);
                string path = Absolute(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!bool.TryParse(Required(probe, "Exists"), out bool exists) ||
                    _presence.ContainsKey(path))
                {
                    throw new FormatException($"Invalid framework-pack presence fact '{path}'.");
                }
                _presence.Add(path, exists);
            }
        }
    }

    public string? Lookup(string scope, string name, string version)
    {
        string key = LookupKey(scope, name, version);
        if (!_lookups.TryGetValue(key, out ITaskItem? fact))
        {
            throw new FormatException($"Unacquired framework-pack lookup '{key}'. Run Restore.");
        }
        string path = fact.GetMetadata("Path");
        return path.Length == 0 ? null : path;
    }

    public string ResolveVersion(string name, string version)
    {
        if (!version.Equals("**FromWorkload**", StringComparison.OrdinalIgnoreCase))
        {
            return version;
        }
        if (!_versions.TryGetValue(name, out string? resolved) || resolved.Length == 0)
        {
            throw new FormatException($"Missing acquired workload-pack version '{name}'.");
        }
        return resolved;
    }

    public RuntimeGraph RuntimeGraph(string path)
    {
        string absolute = Absolute(path);
        if (!_runtimeGraphRows.TryGetValue(absolute, out XElement[]? rows))
        {
            throw new FormatException("Missing acquired framework runtime graph.");
        }
        if (!_runtimeGraphs.TryGetValue(absolute, out RuntimeGraph? graph))
        {
            graph = RuntimeGraphInputs.Read(rows);
            _runtimeGraphs.Add(absolute, graph);
        }
        return graph;
    }

    public void ValidateContext(string targetingPackRoot, string? netCoreRoot = null, string? sdkVersion = null)
    {
        if (!SameDirectory(targetingPackRoot, _context.GetMetadata("TargetingPackRoot")) ||
            (netCoreRoot is not null && !SameDirectory(netCoreRoot, _context.GetMetadata("NetCoreRoot"))) ||
            (sdkVersion is not null && sdkVersion != _context.GetMetadata("NETCoreSdkVersion")))
        {
            throw new FormatException("Framework-pack acquisition context changed. Run Restore.");
        }
    }

    public bool Exists(string path)
    {
        string absolute = Absolute(path);
        if (!_presence.TryGetValue(absolute, out bool exists))
        {
            throw new FormatException($"Missing framework-pack presence fact '{absolute}'.");
        }
        return exists;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, string>> FrameworkFiles(string root)
    {
        XElement pack = Pack(root);
        if (!pack.Elements("FrameworkList").Any())
        {
            throw new FormatException("Missing acquired framework list.");
        }
        return pack.Elements("FrameworkFile").Select(row =>
            (IReadOnlyDictionary<string, string>)row.Attributes().ToDictionary(attribute =>
                attribute.Name.ToString(), attribute => attribute.Value, StringComparer.Ordinal)).ToArray();
    }

    public IEnumerable<ITaskItem> PlatformItems(string root)
        => Pack(root).Elements("PlatformFile").Select(row => Item(Required(row, "FileName"),
            ("PackageId", Required(row, "PackageId")), ("AssemblyVersion", Required(row, "AssemblyVersion")),
            ("FileVersion", Required(row, "FileVersion"))));

    public IEnumerable<TaskItem> PackageOverrides(string root, string packageId)
        => Pack(root).Elements("PackageOverride").Select(row => Item(packageId,
            ("OverriddenPackageId", Required(row, "PackageId")), ("OverriddenPackageVersion", Required(row, "Version"))));

    public string[] Members(string root)
        => Pack(root).Elements("Member").Select(item =>
            Path.Combine(root, Required(item, "Path").Replace('/', Path.DirectorySeparatorChar))).ToArray();

    public string[] LegacyFiles(string root, string folder)
        => Pack(root).Elements("LegacyFile").Select(item => Required(item, "Path"))
            .Where(path => string.Equals(path.Substring(0, path.LastIndexOf('/')), folder, StringComparison.Ordinal))
            .Select(path => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))).ToArray();

    public ITaskItem[] AnalyzerCandidates()
    {
        List<ITaskItem> files = [];
        foreach (KeyValuePair<string, XElement> entry in _packs)
        {
            foreach (XElement member in entry.Value.Elements("Member"))
            {
                string path = Required(member, "Path");
                if (path.StartsWith("analyzers/", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(new TaskItem(Microsoft.Build.Evaluation.ProjectCollection.Escape(
                        Path.Combine(entry.Key, path.Replace('/', Path.DirectorySeparatorChar)))));
                }
            }
        }
        return files.ToArray();
    }

    public IEnumerable<ITaskItem> ReferenceCandidates()
    {
        foreach (KeyValuePair<string, XElement> entry in _packs)
        {
            foreach (XElement member in entry.Value.Elements().Where(item => item.Name == "Member" || item.Name == "LegacyFile"))
            {
                string path = Required(member, "Path");
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new TaskItem(Microsoft.Build.Evaluation.ProjectCollection.Escape(
                        Path.Combine(entry.Key, path.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
        }
    }

    public static string LookupKey(string scope, string name, string version)
        => scope + "/" + name + "/" + version;

    private XElement Pack(string root)
        => _packs.TryGetValue(Absolute(root), out XElement? pack) ? pack
            : throw new FormatException($"Missing acquired framework pack '{root}'.");

    private string Absolute(string path) => _environment.GetAbsolutePath(path).Value;

    private bool SameDirectory(string left, string right)
        => left.Length == 0 || right.Length == 0 ? left == right
            : _packs.Comparer.Equals(Absolute(left).TrimEnd(Path.DirectorySeparatorChar),
                                     Absolute(right).TrimEnd(Path.DirectorySeparatorChar));

    internal static void ValidateRelativePath(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 ||
            path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new FormatException("Invalid framework-pack member path.");
        }
    }

    private static string Required(XElement element, string attribute)
        => (string?)element.Attribute(attribute)
            ?? throw new FormatException($"Missing framework-pack '{attribute}' attribute.");

    private static XElement ReadRow(ITaskItem resource)
    {
        string kind = resource.GetMetadata("Kind");
        if (kind.Length == 0 || System.Xml.XmlConvert.EncodeLocalName(kind) != kind ||
            resource.GetMetadata("Content").Length != 0)
        {
            throw new FormatException("Invalid native framework-pack resource row.");
        }
        XElement row = new(kind);
        foreach (System.Collections.DictionaryEntry metadata in resource.CloneCustomMetadata())
        {
            string name = (string)metadata.Key;
            if (name.StartsWith("Attribute_", StringComparison.Ordinal))
            {
                row.Add(new XAttribute(System.Xml.XmlConvert.DecodeName(name.Substring("Attribute_".Length)),
                    (string?)metadata.Value ?? ""));
            }
        }
        return row;
    }

    private static TaskItem Item(string identity, params (string Name, string Value)[] metadata)
    {
        TaskItem item = new(Microsoft.Build.Evaluation.ProjectCollection.Escape(identity));
        foreach ((string name, string value) in metadata)
        {
            item.SetMetadata(name, Microsoft.Build.Evaluation.ProjectCollection.Escape(value));
        }
        return item;
    }
}
