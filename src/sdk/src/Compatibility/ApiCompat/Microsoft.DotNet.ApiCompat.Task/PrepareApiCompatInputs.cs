// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Microsoft.DotNet.ApiCompat.Task;

[MSBuildMultiThreadableTask]
public sealed class PrepareApiCompatInputs : MSBuildTask, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public string[] ReferencePaths { get; set; } = [];
    public ITaskItem[] ProducedReferenceDirectories { get; set; } = [];
    public string DefaultSuppressionFile { get; set; } = "";

    [Required]
    public string OutputFile { get; set; } = "";

    public override bool Execute()
    {
        HashSet<string> directories = new(ApiCompatInputPaths.Comparer);
        HashSet<string> producedDirectories = new(ProducedReferenceDirectories.Select(
            item => ApiCompatInputPaths.Canonical(item.ItemSpec, TaskEnvironment)), ApiCompatInputPaths.Comparer);
        foreach (string group in ReferencePaths)
        {
            foreach (string path in group.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string fullPath = ApiCompatInputPaths.Canonical(path, TaskEnvironment);
                if (producedDirectories.Contains(fullPath) ||
                    producedDirectories.Contains(Path.GetDirectoryName(fullPath)!))
                {
                    continue;
                }
                string? directory = Directory.Exists(fullPath) ? fullPath
                    : File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : null;
                if (directory is not null && Directory.Exists(directory))
                {
                    directories.Add(directory);
                }
            }
        }

        XElement items = new("ItemGroup");
        foreach (string directory in directories.Order(StringComparer.Ordinal))
        {
            items.Add(new XElement("_ApiCompatPreparedReferenceDirectory", new XAttribute("Include", Escape(directory))));
            foreach (string file in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
            {
                items.Add(new XElement("_ApiCompatPreparedReferenceFile", new XAttribute("Include", Escape(file)),
                    new XElement("ReferenceDirectory", Escape(directory))));
            }
        }

        XElement project = new("Project",
            new XElement("PropertyGroup",
                new XElement("_ApiCompatPreparedDefaultSuppressionFile",
                    File.Exists(TaskEnvironment.GetAbsolutePath(DefaultSuppressionFile).Value)
                        ? Escape(TaskEnvironment.GetAbsolutePath(DefaultSuppressionFile).Value) : "")),
            items);
        string destination = TaskEnvironment.GetAbsolutePath(OutputFile).Value;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, project.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static string Escape(string value)
        => value.Replace("%", "%25").Replace("$", "%24").Replace("@", "%40")
            .Replace("(", "%28").Replace(")", "%29").Replace(";", "%3B")
            .Replace("'", "%27").Replace("*", "%2A").Replace("?", "%3F");
}
