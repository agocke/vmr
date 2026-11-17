// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Arcade.Sdk;

[MSBuildDeclaredIOTask(SandboxVersion = 1)]
public sealed class RegexReplaceFile : Task, IMultiThreadableTask
{
    /// <summary>Injected by MSBuild so paths resolve against the project directory in multithreaded builds.</summary>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Required]
    public ITaskItem InputFile { get; set; }

    [Required]
    public ITaskItem OutputFile { get; set; }

    [Required]
    public string Pattern { get; set; }

    [Required]
    public string Replacement { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; }

    public ITaskItem[] DeclaredOutputs { get; set; }

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

        string contents = Regex.Replace(File.ReadAllText(inputPath), Pattern, Replacement);
        Directory.CreateDirectory(TaskEnvironment.GetExecutionDirectory(Path.GetDirectoryName(logicalOutputPath)));
        File.WriteAllText(outputPath, contents + Environment.NewLine, new UTF8Encoding(false, true));
        return true;
    }
}
