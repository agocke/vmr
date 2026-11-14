// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

public sealed class WriteApiCompatInputs : TaskBase
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
            StringComparer paths = Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Dictionary<string, string> outputs = new(paths);
            foreach (ITaskItem request in Requests)
            {
                string input = Absolute(request.GetMetadata("DefaultSuppressionFile"));
                string output = Absolute(request.GetMetadata("OutputFile"));
                if (outputs.TryGetValue(output, out string? previous) && !paths.Equals(previous, input))
                {
                    throw new FormatException($"Conflicting ApiCompat suppression inputs for '{output}'.");
                }
                outputs[output] = input;
            }
            HashSet<string> inputs = new(outputs.Values, paths);
            if (outputs.Keys.Any(inputs.Contains))
            {
                throw new FormatException("ApiCompat facts cannot replace acquisition inputs.");
            }
            Dictionary<string, bool> presence = inputs.ToDictionary(path => path, File.Exists, paths);
            foreach (var output in outputs)
            {
                string content = new XElement("Project",
                    new XElement("PropertyGroup",
                        new XElement("_ApiCompatInputsVersion", "2"),
                        new XElement("_ApiCompatPreparedDefaultSuppressionFile",
                            presence[output.Value] ? ProjectCollection.Escape(output.Value) : ""))).ToString();
                if (!File.Exists(output.Key) || File.ReadAllText(output.Key) != content)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output.Key)!);
                    File.WriteAllText(output.Key, content, new UTF8Encoding(false));
                }
            }
            WrittenModels = outputs.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Log.LogError(ex.Message);
        }
    }

    private static string Absolute(string value)
        => Path.IsPathRooted(value) ? Path.GetFullPath(value)
            : throw new FormatException($"ApiCompat bindings require absolute paths: '{value}'.");
}
