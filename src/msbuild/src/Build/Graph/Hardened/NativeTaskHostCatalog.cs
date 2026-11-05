// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Graph.Hardened;

internal sealed class NativeTaskHostCatalog
{
    private readonly Dictionary<string, string> _assemblies = new(StringComparer.Ordinal);
    private readonly List<string> _tasks = [];
    private readonly Dictionary<string, bool> _matches = new(FileUtilities.PathComparer);

    internal NativeTaskHostCatalog(string host)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(host + ".json"));
        JsonElement catalog = document.RootElement;
        if (catalog.GetProperty("version").GetInt32() != 1 ||
            catalog.GetProperty("hostSha256").GetString() != Hash(host))
        {
            throw new InvalidDataException("The native action host catalog does not match its executable.");
        }

        foreach (JsonProperty assembly in catalog.GetProperty("assemblies").EnumerateObject())
        {
            string hash = assembly.Value.GetString()!;
            if (Path.GetFileName(assembly.Name) != assembly.Name || hash is not { Length: 64 })
            {
                throw new InvalidDataException("Invalid native action host assembly identity.");
            }

            _assemblies.Add(assembly.Name, hash);
        }

        if (!_assemblies.ContainsKey("Microsoft.Build.Tasks.Core.dll"))
        {
            throw new InvalidDataException("Missing native action host task assembly identity.");
        }

        foreach (JsonElement task in catalog.GetProperty("tasks").EnumerateArray())
        {
            string name = task.GetString()!;
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidDataException("Invalid native action host task type.");
            }

            _tasks.Add(name);
        }
    }

    internal bool Supports(string taskType, string assembly, Dictionary<string, int> producers)
    {
        bool supported = false;
        foreach (string task in _tasks)
        {
            if (string.Equals(task, taskType, StringComparison.OrdinalIgnoreCase) ||
                (taskType.IndexOf('.') < 0 && task.EndsWith("." + taskType, StringComparison.OrdinalIgnoreCase)))
            {
                supported = true;
                break;
            }
        }

        if (!supported || Path.GetFileName(assembly) != "Microsoft.Build.Tasks.Core.dll")
        {
            return false;
        }

        string directory = Path.GetDirectoryName(assembly)!;
        foreach (string dependency in _assemblies.Keys)
        {
            if (producers.ContainsKey(Path.Combine(directory, dependency)))
            {
                return false;
            }
        }

        if (!_matches.TryGetValue(directory, out bool matches))
        {
            matches = true;
            foreach (KeyValuePair<string, string> dependency in _assemblies)
            {
                string path = Path.Combine(directory, dependency.Key);
                if (!File.Exists(path) || Hash(path) != dependency.Value)
                {
                    matches = false;
                    break;
                }
            }

            _matches.Add(directory, matches);
        }

        return matches;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
    }
}
