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
    private readonly string _runtimeGraphPath;
    private readonly string _runtimeGraphJson;
    private readonly ITaskItem _context;
    private RuntimeGraph? _runtimeGraph;

    public bool IsWindows { get; }

    public FrameworkPackResources(string version, ITaskItem[] facts, ITaskItem[] resources,
                                 TaskEnvironment environment)
    {
        if (version != "1")
        {
            throw new FormatException("Missing or unsupported raw framework-pack facts.");
        }
        _environment = environment;
        StringComparer paths = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _packs = new(paths);
        _presence = new(paths);
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
        _runtimeGraphPath = context.GetMetadata("RuntimeGraphPath");
        _runtimeGraphJson = "";
        foreach (ITaskItem resource in resources)
        {
            string root = Absolute(resource.ItemSpec);
            if (resource.GetMetadata("Kind") == "RuntimeGraph")
            {
                if (!paths.Equals(root, _runtimeGraphPath) || _runtimeGraphJson.Length != 0)
                {
                    throw new FormatException("Invalid or duplicate framework runtime graph resource.");
                }
                _runtimeGraphJson = resource.GetMetadata("Content");
                continue;
            }
            XElement pack = XElement.Parse(resource.GetMetadata("Content"));
            if (pack.Name != "Pack" || _packs.ContainsKey(root))
            {
                throw new FormatException($"Invalid or duplicate framework-pack resource '{root}'.");
            }
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
        if (!_packs.Comparer.Equals(Absolute(path), _runtimeGraphPath) ||
            _runtimeGraphJson.Length == 0)
        {
            throw new FormatException("Missing acquired framework runtime graph.");
        }
        return _runtimeGraph ??= JsonRuntimeFormat.ReadRuntimeGraph(new StringReader(_runtimeGraphJson));
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

    public string Text(string root, string name)
    {
        XElement pack = Pack(root);
        XElement? file = pack.Elements("Data").SingleOrDefault(item =>
            string.Equals((string?)item.Attribute("Path"), name, StringComparison.Ordinal));
        return file?.Value ?? throw new FormatException($"Missing acquired pack data '{name}'.");
    }

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
}
