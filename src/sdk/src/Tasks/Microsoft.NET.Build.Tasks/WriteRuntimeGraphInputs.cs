// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Newtonsoft.Json;
using NuGet.RuntimeModel;

namespace Microsoft.NET.Build.Tasks;

public sealed class WriteRuntimeGraphInputs : TaskBase
{
    [Required]
    public ITaskItem[] Requests { get; set; } = [];

    [Output]
    public int WrittenResources { get; private set; }

    protected override void ExecuteCore()
    {
        WrittenResources = 0;
        try
        {
            StringComparer paths = Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Dictionary<string, string> outputs = new(paths);
            Dictionary<string, List<string>> graphs = new(paths);
            HashSet<string> inputs = new(paths);
            foreach (ITaskItem request in Requests)
            {
                string path = Absolute(request.GetMetadata("RuntimeGraphPath"));
                string output = Absolute(request.GetMetadata("OutputFile"));
                string directory = Absolute(request.GetMetadata("ResourceDirectory"));
                inputs.Add(path);
                if (outputs.TryGetValue(output, out string? previous) && !paths.Equals(previous, directory))
                {
                    throw new FormatException($"Conflicting runtime-graph resource directories for '{output}'.");
                }
                outputs[output] = directory;
                if (!graphs.TryGetValue(output, out List<string>? resources))
                {
                    graphs.Add(output, resources = []);
                }
                if (!resources.Contains(path, paths))
                {
                    resources.Add(path);
                }
            }
            if (outputs.Keys.Any(inputs.Contains))
            {
                throw new FormatException("Runtime-graph facts cannot replace acquisition inputs.");
            }
            Dictionary<string, XElement[]> translated = new(paths);
            Dictionary<string, XElement> publications = new(paths);
            foreach (var output in outputs)
            {
                List<XElement> imports = [];
                foreach (string path in graphs[output.Key])
                {
                    if (!translated.TryGetValue(path, out XElement[]? rows))
                    {
                        rows = RuntimeGraphInputs.Write(JsonRuntimeFormat.ReadRuntimeGraph(path)).ToArray();
                        translated.Add(path, rows);
                    }
                    using SHA256 hash = SHA256.Create();
                    string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "");
                    string resource = Path.Combine(output.Value, key + ".g.props");
                    if (inputs.Contains(resource) || outputs.ContainsKey(resource))
                    {
                        throw new FormatException($"Runtime-graph resource would overwrite an acquisition binding: '{resource}'.");
                    }
                    publications[resource] = new XElement("Project",
                        new XElement("ItemGroup", rows.Select(row => RuntimeGraphInputs.Item(path, row))));
                    imports.Add(new XElement("Import", new XAttribute("Project", ProjectCollection.Escape(resource))));
                }
                publications.Add(output.Key, new XElement("Project",
                    new XElement("PropertyGroup", new XElement("_RuntimeGraphInputsVersion", "1")), imports));
            }
            foreach (var publication in publications)
            {
                Write(publication.Key, publication.Value);
            }
            WrittenResources = translated.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or JsonException)
        {
            Log.LogError(ex.Message);
        }
    }

    private static string Absolute(string value)
        => Path.IsPathRooted(value) ? Path.GetFullPath(value)
            : throw new FormatException($"Runtime-graph bindings require absolute paths: '{value}'.");

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
