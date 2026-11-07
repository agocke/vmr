// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.CommandLine;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.BuildTasks;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ResolveCompilerDependencies : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] Analyzers { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] CandidateFiles { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] Dependencies { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        Dependencies = Array.Empty<ITaskItem>();
        var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directories = Analyzers.Select(analyzer =>
            Path.GetDirectoryName(GetFullPath(analyzer))!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Distinct(comparer).ToArray();
        var selected = new HashSet<string>(comparer);
        var dependencies = new List<ITaskItem>();
        foreach (var candidate in CandidateFiles)
        {
            var path = GetFullPath(candidate);
            if (directories.Any(directory => path.StartsWith(directory, comparison)) && selected.Add(path))
            {
                dependencies.Add(new TaskItem(CompilerInvocation.EscapeItem(path)));
            }
        }

        Dependencies = dependencies.ToArray();
        return true;
    }

    private string GetFullPath(ITaskItem item)
    {
        var path = PlatformInformation.IsUnix ? item.ItemSpec.Replace('\\', '/') : item.ItemSpec;
#pragma warning disable RS0030 // Only canonicalizes a path relative to the explicit project directory.
        return Path.GetFullPath(TaskEnvironment.GetAbsolutePath(path).Value);
#pragma warning restore RS0030
    }
}
