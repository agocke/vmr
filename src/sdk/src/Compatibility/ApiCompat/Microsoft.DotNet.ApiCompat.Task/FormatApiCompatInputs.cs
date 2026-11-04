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
    public string[] LeftAssemblies { get; set; } = [];
    public string[] RightAssemblies { get; set; } = [];
    public string[] LeftAssembliesReferences { get; set; } = [];
    public string[] RightAssembliesReferences { get; set; } = [];
    public string[] SuppressionFiles { get; set; } = [];
    public string[] ExcludeAttributesFiles { get; set; } = [];
    public ITaskItem[] PreparedReferenceDirectories { get; set; } = [];
    public ITaskItem[] PreparedReferenceFiles { get; set; } = [];
    public ITaskItem[] ProducedReferenceDirectories { get; set; } = [];
    public ITaskItem[] ProducedReferenceFiles { get; set; } = [];
    public string RoslynAssembliesPath { get; set; } = "";
    public string SemaphoreFile { get; set; } = "";
    public bool GenerateSuppressionFile { get; set; }

    [Output]
    public ITaskItem[] FileInputs { get; private set; } = [];

    [Output]
    public ITaskItem[] FileOutputs { get; private set; } = [];

    public override bool Execute()
    {
        if (GenerateSuppressionFile)
        {
            throw ApiCompatInputPaths.Invalid(nameof(GenerateSuppressionFile));
        }

        HashSet<string> inputs = new(ApiCompatInputPaths.Comparer);
        HashSet<string> producedDirectories = new(ProducedReferenceDirectories.Select(
            item => Canonical(item.ItemSpec)), ApiCompatInputPaths.Comparer);
        HashSet<string> directories = new(PreparedReferenceDirectories.Concat(ProducedReferenceDirectories).Select(
            item => Canonical(item.ItemSpec)), ApiCompatInputPaths.Comparer);
        HashSet<string> producedFiles = new(ApiCompatInputPaths.Comparer);
        foreach (ITaskItem member in ProducedReferenceFiles)
        {
            string path = Canonical(member.ItemSpec);
            if (!producedDirectories.Contains(Path.GetDirectoryName(path)!))
            {
                throw ApiCompatInputPaths.Invalid(member.ItemSpec);
            }
            producedFiles.Add(path);
        }
        foreach (string path in LeftAssemblies.Concat(RightAssemblies)
            .Concat(SuppressionFiles).Concat(ExcludeAttributesFiles))
        {
            string fullPath = Canonical(path);
            if (directories.Contains(fullPath))
            {
                throw ApiCompatInputPaths.Invalid(path);
            }
            inputs.Add(fullPath);
            AddPreparedMembers(Path.GetDirectoryName(fullPath));
        }

        foreach (string group in LeftAssembliesReferences.Concat(RightAssembliesReferences))
        {
            foreach (string path in group.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string fullPath = Canonical(path);
                string? directory = directories.Contains(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                if (!directories.Contains(fullPath))
                {
                    inputs.Add(fullPath);
                }
                AddPreparedMembers(directory);
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
        return true;

        void AddPreparedMembers(string? directory)
        {
            foreach (ITaskItem member in PreparedReferenceFiles)
            {
                if (ApiCompatInputPaths.Comparer.Equals(Canonical(member.GetMetadata("ReferenceDirectory")), directory))
                {
                    if (producedDirectories.Contains(directory!))
                    {
                        throw ApiCompatInputPaths.Invalid(member.ItemSpec);
                    }
                    inputs.Add(Canonical(member.ItemSpec));
                }
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
