// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Microsoft.DotNet.ApiCompat.Task;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class FormatApiCompatReferenceFiles : MSBuildTask, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public string ReferenceDirectory { get; set; } = "";
    public ITaskItem[] LayoutFiles { get; set; } = [];
    public ITaskItem[] TransitiveReferences { get; set; } = [];

    [Output]
    public ITaskItem[] ReferenceFiles { get; private set; } = [];

    public override bool Execute()
    {
        string directory = ApiCompatInputPaths.Canonical(ReferenceDirectory, TaskEnvironment);
        HashSet<string> files = new(ApiCompatInputPaths.Comparer);
        foreach (ITaskItem item in LayoutFiles)
        {
            string path = ApiCompatInputPaths.Canonical(item.ItemSpec, TaskEnvironment);
            if (IsReference(path))
            {
                files.Add(path);
            }
        }
        foreach (ITaskItem item in TransitiveReferences)
        {
            string path = ApiCompatInputPaths.Canonical(item.ItemSpec, TaskEnvironment);
            if (!IsReference(path))
            {
                throw ApiCompatInputPaths.Invalid(item.ItemSpec);
            }
            files.Add(path);
        }
        ReferenceFiles = files.Order(StringComparer.Ordinal).Select(path => (ITaskItem)new TaskItem(path)).ToArray();
        return true;

        bool IsReference(string path) => ApiCompatInputPaths.Comparer.Equals(Path.GetDirectoryName(path), directory) &&
            ApiCompatInputPaths.Comparer.Equals(Path.GetExtension(path), ".dll");
    }
}
