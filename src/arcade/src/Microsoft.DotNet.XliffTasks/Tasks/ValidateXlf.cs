// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Microsoft.Build.Framework;

namespace XliffTasks.Tasks;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ValidateXlf : XlfTask
{
    [Required]
    public ITaskItem[] Sources { get; set; }

    [Required]
    public string[] Languages { get; set; }

    [Required]
    public string ValidationStamp { get; set; }

    protected override void ExecuteCore()
    {
        UpdateXlf.UpdateSources(Sources, Languages, allowModification: false, TaskEnvironment);
        string stamp = TaskEnvironment.GetAbsolutePath(ValidationStamp);
        Directory.CreateDirectory(Path.GetDirectoryName(stamp));
        File.WriteAllText(stamp, "XLF validation succeeded.\n");
    }
}
