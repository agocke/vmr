// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Microsoft.DotNet.ApiCompat.Task;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class FormatApiCompatInputs : MSBuildTask, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public string FormatVersion { get; set; } = "";
    public string[] LeftAssemblies { get; set; } = [];
    public string[] RightAssemblies { get; set; } = [];
    public string[] LeftAssembliesReferences { get; set; } = [];
    public string[] RightAssembliesReferences { get; set; } = [];
    public string[] SuppressionFiles { get; set; } = [];
    public string[] ExcludeAttributesFiles { get; set; } = [];
    public ITaskItem[] ReferenceFileCandidates { get; set; } = [];
    public ITaskItem[] ProducedReferenceDirectories { get; set; } = [];
    public ITaskItem[] ProducedReferenceFiles { get; set; } = [];
    public string RoslynAssembliesPath { get; set; } = "";
    public string SemaphoreFile { get; set; } = "";
    public bool GenerateSuppressionFile { get; set; }

    [Output]
    public ITaskItem[] FileInputs { get; private set; } = [];

    [Output]
    public ITaskItem[] FileOutputs { get; private set; } = [];

    [Output]
    public ITaskItem[] ReferenceDirectories { get; private set; } = [];

    public override bool Execute()
    {
        if (FormatVersion != "2" || GenerateSuppressionFile)
        {
            throw ApiCompatInputPaths.Invalid(FormatVersion != "2" ? nameof(FormatVersion) : nameof(GenerateSuppressionFile));
        }

        HashSet<string> inputs = new(ApiCompatInputPaths.Comparer);
        HashSet<string> producedDirectories = new(ProducedReferenceDirectories.Select(
            item => Canonical(item.ItemSpec)), ApiCompatInputPaths.Comparer);
        HashSet<string> directories = new(producedDirectories, ApiCompatInputPaths.Comparer);
        Dictionary<string, HashSet<string>> members = new(ApiCompatInputPaths.Comparer);
        foreach (ITaskItem candidate in ReferenceFileCandidates)
        {
            if (!string.Equals(Path.GetExtension(candidate.ItemSpec), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string path = Canonical(candidate.ItemSpec);
            string directory = Path.GetDirectoryName(path)!;
            if (producedDirectories.Contains(directory))
            {
                continue;
            }
            if (!members.TryGetValue(directory, out HashSet<string>? files))
            {
                files = new(ApiCompatInputPaths.Comparer);
                members.Add(directory, files);
            }
            files.Add(path);
        }
        HashSet<string> producedFiles = new(ApiCompatInputPaths.Comparer);
        foreach (ITaskItem member in ProducedReferenceFiles)
        {
            string path = Canonical(member.ItemSpec);
            if (!producedDirectories.Contains(Path.GetDirectoryName(path)!) ||
                !string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw ApiCompatInputPaths.Invalid(member.ItemSpec);
            }
            producedFiles.Add(path);
        }
        foreach (string path in LeftAssemblies.Concat(RightAssemblies)
            .Concat(SuppressionFiles).Concat(ExcludeAttributesFiles))
        {
            string fullPath = Canonical(path);
            if (producedDirectories.Contains(fullPath) || members.ContainsKey(fullPath))
            {
                throw ApiCompatInputPaths.Invalid(path);
            }
            inputs.Add(fullPath);
            AddMembers(Path.GetDirectoryName(fullPath));
        }

        foreach (string group in LeftAssembliesReferences.Concat(RightAssembliesReferences))
        {
            foreach (string path in group.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string fullPath = Canonical(path);
                bool isDirectory = producedDirectories.Contains(fullPath) || members.ContainsKey(fullPath);
                string? directory = isDirectory ? fullPath : Path.GetDirectoryName(fullPath);
                if (!isDirectory)
                {
                    if (!string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Path.GetExtension(fullPath), ".winmd", StringComparison.OrdinalIgnoreCase))
                    {
                        throw ApiCompatInputPaths.Invalid(path);
                    }
                    inputs.Add(fullPath);
                }
                AddMembers(directory);
            }
        }

        inputs.Add(Canonical(Path.Combine(RoslynAssembliesPath, "Microsoft.CodeAnalysis.dll")));
        inputs.Add(Canonical(Path.Combine(RoslynAssembliesPath, "Microsoft.CodeAnalysis.CSharp.dll")));
        FileInputs = inputs.Order(StringComparer.Ordinal).Select(path =>
        {
            TaskItem item = new(path);
            if (producedFiles.Contains(path))
            {
                item.SetMetadata("RequireProducer", "true");
            }
            return (ITaskItem)item;
        }).ToArray();
        FileOutputs = [new TaskItem(Canonical(SemaphoreFile))];
        ReferenceDirectories = directories.Order(StringComparer.Ordinal).Select(path => (ITaskItem)new TaskItem(path)).ToArray();
        return true;

        void AddMembers(string? directory)
        {
            if (directory is null)
            {
                return;
            }
            directories.Add(directory);
            if (members.TryGetValue(directory, out HashSet<string>? files))
            {
                inputs.UnionWith(files);
            }
            foreach (ITaskItem member in ProducedReferenceFiles)
            {
                string path = Canonical(member.ItemSpec);
                if (ApiCompatInputPaths.Comparer.Equals(Path.GetDirectoryName(path), directory))
                {
                    inputs.Add(path);
                }
            }
        }
    }

    private string Canonical(string path) => ApiCompatInputPaths.Canonical(path, TaskEnvironment);
}

internal static class ApiCompatInputPaths
{
    internal static StringComparer Comparer => Path.DirectorySeparatorChar == '\\'
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Canonical(string path, TaskEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['*', '?']) >= 0)
        {
            throw Invalid(path);
        }
        return Path.TrimEndingDirectorySeparator(environment.GetAbsolutePath(path).Value);
    }

    internal static ArgumentException Invalid(string value)
        => new(string.Format(Resources.InvalidDeclaredInputs, value));
}
