// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks;

internal static class PlatformSDKInputs
{
    public static IEnumerable<XElement> LegacyProperties(string identifier, string version, string displayName)
    {
        yield return new XElement("_FrameworkReferencePlatformSDKFactsVersion", "1");
        yield return new XElement("_FrameworkReferenceManifestTargetPlatformIdentifier", ProjectCollection.Escape(identifier));
        yield return new XElement("_FrameworkReferenceManifestTargetPlatformVersion", ProjectCollection.Escape(version));
        yield return new XElement("_FrameworkReferenceManifestTargetPlatformDisplayName", ProjectCollection.Escape(displayName));
    }

    public static IEnumerable<XElement> Capture(string[]? diskRoots = null, string? registryRoot = null)
    {
        IList<TargetPlatformSDK> sdks = ToolLocationHelper.GetTargetPlatformSdks(diskRoots, registryRoot);
        Dictionary<string, (string Identifier, Version Version)> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (TargetPlatformSDK sdk in sdks)
        {
            Add(sdk.TargetPlatformIdentifier, sdk.TargetPlatformVersion);
            foreach (string platform in ToolLocationHelper.GetPlatformsForSDK(
                         sdk.TargetPlatformIdentifier, sdk.TargetPlatformVersion, diskRoots, registryRoot))
            {
                const string separator = ", Version=";
                int index = platform.LastIndexOf(separator, StringComparison.OrdinalIgnoreCase);
                if (index < 0 || !Version.TryParse(platform.Substring(index + separator.Length), out Version? version))
                {
                    throw new FormatException($"Invalid installed platform SDK moniker '{platform}'.");
                }
                Add(platform.Substring(0, index), version);
            }
        }
        StringComparison paths = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in candidates.Values)
        {
            string location = ToolLocationHelper.GetPlatformSDKLocation(
                candidate.Identifier, candidate.Version, diskRoots, registryRoot);
            TargetPlatformSDK? selected = sdks.FirstOrDefault(sdk => string.Equals(sdk.Path, location, paths));
            string? displayName = selected?.DisplayName;
            if (displayName is not null)
            {
                yield return new XElement("_PlatformSDKDisplayName",
                    new XAttribute("Include", ProjectCollection.Escape(candidate.Identifier)),
                    new XElement("Version", candidate.Version.ToString()),
                    new XElement("DisplayName", ProjectCollection.Escape(displayName)));
            }
        }

        void Add(string identifier, Version version)
            => candidates[identifier + "/" + version] = (identifier, version);
    }

    public static string Select(string version, ITaskItem[] facts, string identifier, string targetVersion)
    {
        if (version != "1")
        {
            throw new FormatException("Missing or unsupported platform SDK display-name catalog. Run Restore.");
        }
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem fact in facts)
        {
            if (fact.ItemSpec.Length == 0 || !Version.TryParse(fact.GetMetadata("Version"), out Version? sdkVersion) ||
                !fact.MetadataNames.Cast<string>().Contains("DisplayName", StringComparer.OrdinalIgnoreCase))
            {
                throw new FormatException("Invalid acquired platform SDK display-name facts.");
            }
            string key = fact.ItemSpec + "/" + sdkVersion;
            if (names.ContainsKey(key))
            {
                throw new FormatException($"Duplicate acquired platform SDK display name '{key}'.");
            }
            names.Add(key, fact.GetMetadata("DisplayName"));
        }
        return Version.TryParse(targetVersion, out Version? parsed) &&
               names.TryGetValue(identifier + "/" + parsed, out string? name)
            ? name : identifier + " " + targetVersion;
    }

    public static void Write(string file, XElement project)
    {
        string content = project.ToString() + "\n";
        if (!File.Exists(file) || File.ReadAllText(file) != content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }
    }
}
