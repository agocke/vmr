// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.IO.Compression;
using Microsoft.Build.Framework;

namespace Microsoft.CodeAnalysis.BuildTasks;

[MSBuildDeclaredIOTask(SandboxVersion = 1)]
public sealed class CompileManaged : Microsoft.Build.Utilities.Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputTrees { get; set; } = [];
    public bool Malformed { get; set; }

    public override bool Execute()
    {
        string archive = TaskEnvironment.GetOutputPath(DeclaredOutputTrees[0].GetMetadata("ArchiveFile")).Value;
        if (Malformed)
        {
            File.WriteAllText(archive, "not a ZIP archive");
        }
        else
        {
            using ZipArchive zip = new(File.Create(archive), ZipArchiveMode.Create);
            using StreamWriter writer = new(zip.CreateEntry("nested/Generated.cs").Open());
            writer.Write("class Generated {}");
        }

        return true;
    }
}
