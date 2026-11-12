// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using NuGet.Frameworks;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace Microsoft.Build.Shared;

internal static class RuntimeGraphInputs
{
    public static XElement Item(string path, XElement row)
        => new("_RuntimeGraphResource", new XAttribute("Include", ProjectCollection.Escape(path)),
            new XElement("Kind", row.Name.LocalName),
            row.Attributes().Select(attribute => new XElement(
                "Attribute_" + XmlConvert.EncodeLocalName(attribute.Name.LocalName),
                ProjectCollection.Escape(attribute.Value))));

    public static RuntimeGraph ReadItems(string version, string path, ITaskItem[] resources)
    {
        if (version != "1")
        {
            throw new FormatException("Unsupported native runtime-graph facts. Run Restore.");
        }
        StringComparer paths = System.IO.Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        XElement[] rows = resources.Where(item => paths.Equals(item.ItemSpec, path)).Select(item =>
        {
            if (item.GetMetadata("Content").Length != 0)
            {
                throw new FormatException("Opaque runtime-graph content is not supported.");
            }
            XElement row = new(item.GetMetadata("Kind"));
            foreach (string name in item.MetadataNames)
            {
                if (name.StartsWith("Attribute_", StringComparison.Ordinal))
                {
                    row.Add(new XAttribute(XmlConvert.DecodeName(name.Substring("Attribute_".Length)),
                        item.GetMetadata(name)));
                }
            }
            return row;
        }).ToArray();
        if (rows.Count(row => row.Name == "RuntimeGraph") != 1)
        {
            throw new FormatException($"Missing or duplicate acquired runtime graph '{path}'. Run Restore.");
        }
        return Read(rows);
    }

    public static IEnumerable<XElement> Write(RuntimeGraph graph)
    {
        yield return new XElement("RuntimeGraph");
        foreach (RuntimeDescription runtime in graph.Runtimes.Values)
        {
            yield return Row("Runtime", ("Runtime", runtime.RuntimeIdentifier));
            foreach (string import in runtime.InheritedRuntimes)
            {
                yield return Row("RuntimeImport", ("Runtime", runtime.RuntimeIdentifier), ("Import", import));
            }
            foreach (RuntimeDependencySet set in runtime.RuntimeDependencySets.Values)
            {
                yield return Row("RuntimeDependencySet", ("Runtime", runtime.RuntimeIdentifier), ("PackageId", set.Id));
                foreach (RuntimePackageDependency dependency in set.Dependencies.Values)
                {
                    yield return Row("RuntimeDependency", ("Runtime", runtime.RuntimeIdentifier), ("PackageId", set.Id),
                        ("DependencyId", dependency.Id), ("VersionRange", dependency.VersionRange.ToString()));
                }
            }
        }
        foreach (CompatibilityProfile profile in graph.Supports.Values)
        {
            yield return Row("CompatibilityProfile", ("Profile", profile.Name));
            foreach (FrameworkRuntimePair context in profile.RestoreContexts)
            {
                yield return Row("CompatibilityContext", ("Profile", profile.Name),
                    ("Framework", context.Framework.GetShortFolderName()), ("Runtime", context.RuntimeIdentifier));
            }
        }
    }

    public static RuntimeGraph Read(XElement[] rows)
    {
        try
        {
            Dictionary<string, List<string>> imports = new(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, List<RuntimePackageDependency>>> dependencies = new(StringComparer.Ordinal);
            Dictionary<string, List<FrameworkRuntimePair>> profiles = new(StringComparer.Ordinal);
            foreach (XElement row in rows)
            {
                switch (row.Name.LocalName)
                {
                    case "RuntimeGraph":
                        break;
                    case "Runtime":
                        string runtime = Required(row, "Runtime");
                        imports.Add(runtime, []);
                        dependencies.Add(runtime, new(StringComparer.OrdinalIgnoreCase));
                        break;
                    case "RuntimeImport":
                        imports[Required(row, "Runtime")].Add(Required(row, "Import"));
                        break;
                    case "RuntimeDependencySet":
                        dependencies[Required(row, "Runtime")].Add(Required(row, "PackageId"), []);
                        break;
                    case "RuntimeDependency":
                        dependencies[Required(row, "Runtime")][Required(row, "PackageId")].Add(
                            new RuntimePackageDependency(Required(row, "DependencyId"),
                                VersionRange.Parse(Required(row, "VersionRange"))));
                        break;
                    case "CompatibilityProfile":
                        profiles.Add(Required(row, "Profile"), []);
                        break;
                    case "CompatibilityContext":
                        profiles[Required(row, "Profile")].Add(new FrameworkRuntimePair(
                            NuGetFramework.Parse(Required(row, "Framework")), Required(row, "Runtime")));
                        break;
                    default:
                        throw new FormatException($"Unknown runtime-graph fact kind '{row.Name}'.");
                }
            }
            return new RuntimeGraph(imports.Select(runtime => new RuntimeDescription(runtime.Key, runtime.Value,
                dependencies[runtime.Key].Select(set => new RuntimeDependencySet(set.Key, set.Value)))),
                profiles.Select(profile => new CompatibilityProfile(profile.Key, profile.Value)));
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            throw new FormatException("Invalid acquired runtime-graph facts.", ex);
        }
    }

    private static XElement Row(string kind, params (string Name, string Value)[] attributes)
        => new(kind, attributes.Select(attribute => new XAttribute(attribute.Name, attribute.Value)));

    private static string Required(XElement row, string name)
        => (string?)row.Attribute(name) ?? throw new FormatException($"Missing runtime-graph '{name}' attribute.");
}
