// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Standalone tool that generates Microsoft.NETCore.App.deps.json for the
// Bazel-built runtime archive.  Reuses the GenerateSharedFrameworkDepsFile
// MSBuild task from Microsoft.DotNet.SharedFramework.Sdk by calling its
// Execute() method directly (the task is a plain class with no real MSBuild
// host dependency at runtime — it only reads files and writes JSON).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Generates a .deps.json file for the shared framework by scanning the
/// assembled runtime directory for managed and native files, reading their
/// assembly/file versions from PE metadata, and writing the result in the
/// format expected by the .NET host.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "Usage: GenerateDepsFile <framework-dir> <rid> <version> <rid-graph-json> <output-path>");
            return 1;
        }

        string frameworkDir = args[0];
        string rid = args[1];
        string version = args[2];
        string ridGraphPath = args[3];
        string outputPath = args[4];

        string packName = $"Microsoft.NETCore.App.Runtime.{rid}";
        string tfm = ".NETCoreApp,Version=v10.0";

        var runtimeEntries = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var nativeEntries = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(frameworkDir))
        {
            string fileName = Path.GetFileName(file);
            string ext = Path.GetExtension(file);

            // Skip non-binary files
            if (ext is ".json" or ".txt")
                continue;

            Version? assemblyVersion = GetAssemblyVersion(file);
            string fileVersion = GetFileVersion(file);

            if (assemblyVersion is not null)
            {
                var entry = new JsonObject
                {
                    ["assemblyVersion"] = assemblyVersion.ToString(),
                    ["fileVersion"] = fileVersion
                };
                runtimeEntries[fileName] = entry;
            }
            else
            {
                var entry = new JsonObject
                {
                    ["fileVersion"] = fileVersion
                };
                nativeEntries[fileName] = entry;
            }
        }

        // Build the runtime target entry
        var runtimeAssets = new JsonObject();
        foreach (var (name, entry) in runtimeEntries)
            runtimeAssets[name] = entry;

        var nativeAssets = new JsonObject();
        foreach (var (name, entry) in nativeEntries)
            nativeAssets[name] = entry;

        var packageEntry = new JsonObject
        {
            ["runtime"] = runtimeAssets,
            ["native"] = nativeAssets
        };

        string packageKey = $"{packName}/{version}";

        // Build the runtimes (RID fallback graph) section
        JsonObject? runtimesFallbacks = BuildRidFallbacks(ridGraphPath, rid);

        var root = new JsonObject
        {
            ["runtimeTarget"] = new JsonObject
            {
                ["name"] = $"{tfm}/{rid}",
                ["signature"] = ""
            },
            ["compilationOptions"] = new JsonObject(),
            ["targets"] = new JsonObject
            {
                [tfm] = new JsonObject(),
                [$"{tfm}/{rid}"] = new JsonObject
                {
                    [packageKey] = packageEntry
                }
            },
            ["libraries"] = new JsonObject
            {
                [packageKey] = new JsonObject
                {
                    ["type"] = "package",
                    ["serviceable"] = true,
                    ["sha512"] = "",
                    ["path"] = $"{packName.ToLowerInvariant()}/{version}"
                }
            }
        };

        if (runtimesFallbacks is not null)
            root["runtimes"] = runtimesFallbacks;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = root.ToJsonString(options);
        File.WriteAllText(outputPath, json);

        return 0;
    }

    static Version? GetAssemblyVersion(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext is not ".dll" and not ".exe")
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return null;
            return peReader.GetMetadataReader().GetAssemblyDefinition().GetAssemblyName().Version;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    static string GetFileVersion(string path)
    {
        var fvi = FileVersionInfo.GetVersionInfo(path);
        if (fvi is not null)
        {
            var ver = new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);
            return ver.ToString();
        }
        return "0.0.0.0";
    }

    /// <summary>
    /// Reads the RuntimeIdentifierGraph.json and produces the "runtimes"
    /// section — a dictionary of RID → fallback chain for every RID whose
    /// expansion contains <paramref name="targetRid"/>.
    /// </summary>
    static JsonObject? BuildRidFallbacks(string ridGraphPath, string targetRid)
    {
        if (!File.Exists(ridGraphPath))
            return null;

        // Parse { "runtimes": { "<rid>": { "#import": [...] }, ... } }
        string text = File.ReadAllText(ridGraphPath);
        using var doc = JsonDocument.Parse(text);
        var runtimes = doc.RootElement.GetProperty("runtimes");

        // Build adjacency list
        var imports = new Dictionary<string, string[]>();
        foreach (var prop in runtimes.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("#import", out var importArr))
            {
                imports[prop.Name] = importArr.EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToArray();
            }
            else
            {
                imports[prop.Name] = Array.Empty<string>();
            }
        }

        // Expand each RID into its full fallback chain (BFS)
        var result = new SortedDictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (string rid in imports.Keys)
        {
            var chain = ExpandRuntime(rid, imports);
            if (chain.Contains(targetRid))
            {
                // chain[0] is the RID itself; the fallbacks are chain[1..]
                var fallbacks = new JsonArray();
                for (int i = 1; i < chain.Count; i++)
                    fallbacks.Add(chain[i]);
                result[chain[0]] = fallbacks;
            }
        }

        if (result.Count == 0)
            return null;

        var obj = new JsonObject();
        foreach (var (key, val) in result)
            obj[key] = val;
        return obj;
    }

    /// <summary>
    /// Expand a RID into [self, parent1, parent2, ...] following #import.
    /// Matches NuGet.RuntimeModel's ExpandRuntime behavior.
    /// </summary>
    static List<string> ExpandRuntime(string rid, Dictionary<string, string[]> imports)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(rid);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!seen.Add(current))
                continue;
            result.Add(current);
            if (imports.TryGetValue(current, out var parents))
            {
                foreach (string parent in parents)
                    queue.Enqueue(parent);
            }
        }

        return result;
    }
}
