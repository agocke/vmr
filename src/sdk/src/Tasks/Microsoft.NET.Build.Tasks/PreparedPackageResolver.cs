// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Build.Framework;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace Microsoft.NET.Build.Tasks;

internal sealed class PreparedPackageResolver : IPackageResolver
{
    private readonly Dictionary<string, (string Directory, string Root)> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _relatedFiles = new(PathComparer);
    private readonly Dictionary<string, (string Name, bool Valid)> _locales = new(StringComparer.Ordinal);

    public PreparedPackageResolver(ITaskItem[] locations, ITaskItem[] relatedFiles, ITaskItem[] locales)
    {
        foreach (ITaskItem item in locations)
        {
            string directory = FullPath(item.GetMetadata("PackageDirectory"));
            string root = FullPath(item.GetMetadata("PackageRoot"));
            if (_packages.ContainsKey(item.ItemSpec))
            {
                throw new FormatException($"Duplicate package location '{item.ItemSpec}'.");
            }
            _packages.Add(item.ItemSpec, (directory, root));
        }
        foreach (ITaskItem item in relatedFiles)
        {
            string path = FullPath(item.ItemSpec);
            if (_relatedFiles.ContainsKey(path))
            {
                throw new FormatException($"Duplicate related-file fact '{path}'.");
            }
            _relatedFiles.Add(path, Boolean(item.GetMetadata("Exists")));
        }
        foreach (ITaskItem item in locales)
        {
            string locale = item.GetMetadata("Locale");
            if (_locales.ContainsKey(locale))
            {
                throw new FormatException($"Duplicate locale fact '{locale}'.");
            }
            _locales.Add(locale, (item.GetMetadata("Name"), Boolean(item.GetMetadata("Valid"))));
        }
    }

    public string GetPackageDirectory(string packageId, NuGetVersion version) =>
        Package(packageId, version).Directory;

    public string GetPackageDirectory(string packageId, NuGetVersion version, out string packageRoot)
    {
        (string directory, string root) = Package(packageId, version);
        packageRoot = root;
        return directory;
    }

    public string ResolvePackageAssetPath(LockFileTargetLibrary package, string relativePath)
    {
        string directory = GetPackageDirectory(package.Name!, package.Version!);
        string relative = NuGetPackageResolver.NormalizeRelativePath(relativePath);
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOfAny(['*', '?', ':']) >= 0)
        {
            throw new FormatException($"Invalid package-relative asset '{relativePath}'.");
        }
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new FormatException($"Asset '{relativePath}' escapes package '{package.Name}'.");
        }
        return path;
    }

    public bool FileExists(string path)
    {
        if (!_relatedFiles.TryGetValue(FullPath(path), out bool exists))
        {
            throw new FormatException($"Missing related-file presence fact for '{path}'.");
        }
        return exists;
    }

    public string NormalizeLocale(string locale)
    {
        if (!_locales.TryGetValue(locale, out var value))
        {
            throw new FormatException($"Missing locale fact for '{locale}'.");
        }
        if (!value.Valid)
        {
            throw new CultureNotFoundException(nameof(locale), locale, "Unknown locale recorded during Restore.");
        }
        return value.Name;
    }

    private (string Directory, string Root) Package(string id, NuGetVersion version)
    {
        string identity = id + "/" + version.ToNormalizedString();
        if (!_packages.TryGetValue(identity, out var location))
        {
            throw new FormatException($"Missing selected package location for '{identity}'.");
        }
        return location;
    }

    private static bool Boolean(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException($"Invalid prepared Boolean '{value}'."),
    };

    private static string FullPath(string path)
    {
        bool absolute = Path.DirectorySeparatorChar == '\\'
            ? path.StartsWith(@"\\", StringComparison.Ordinal) ||
                (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\')
            : path.StartsWith("/", StringComparison.Ordinal);
        if (!absolute)
        {
            throw new FormatException($"Prepared package context requires an absolute path, not '{path}'.");
        }
        return Path.GetFullPath(path);
    }

    private static StringComparer PathComparer =>
        Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
