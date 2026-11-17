// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Arcade.Sdk;

[MSBuildDeclaredIOTask(SandboxVersion = 1)]
public sealed class ReplaceTextFile : Task, IMultiThreadableTask
{
    /// <summary>Injected by MSBuild so paths resolve against the project directory in multithreaded builds.</summary>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Required]
    public ITaskItem InputFile { get; set; } = null!;

    [Required]
    public ITaskItem OutputFile { get; set; } = null!;

    [Required]
    public ITaskItem[] Replacements { get; set; } = [];

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public override bool Execute()
    {
        AbsolutePath inputPath = TaskEnvironment.GetInputPath(InputFile.ItemSpec);
        AbsolutePath outputPath = TaskEnvironment.GetOutputPath(OutputFile.ItemSpec);
        string logicalInputPath = TaskEnvironment.GetAbsolutePath(InputFile.ItemSpec).GetCanonicalForm();
        string logicalOutputPath = TaskEnvironment.GetAbsolutePath(OutputFile.ItemSpec).GetCanonicalForm();
        if (TaskEnvironment.IsSandboxed
            && string.Equals(logicalInputPath, logicalOutputPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Log.LogError("Sandboxed file replacement requires distinct input and output paths.");
            return false;
        }

        string contents = File.ReadAllText(inputPath);
        foreach (ITaskItem replacement in Replacements)
        {
            contents = contents.Replace(replacement.ItemSpec, replacement.GetMetadata("Value"));
        }

        // Preserve the legacy WriteLinesToFile conversion, including whitespace and path separators.
        contents = new TaskItem(contents.Trim().Replace("%", "%25")).ItemSpec;
        Directory.CreateDirectory(TaskEnvironment.GetExecutionDirectory(Path.GetDirectoryName(logicalOutputPath)!));
        File.WriteAllText(outputPath, contents.Length == 0 ? "" : contents + Environment.NewLine, new UTF8Encoding(false, true));
        return true;
    }
}
