// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace Microsoft.NET.Build.Tasks;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class GenerateSatelliteAssemblySource : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Required]
    public string PathToTemplateFile { get; set; } = null!;

    [Required]
    public string OutputFile { get; set; } = null!;

    public string? Culture { get; set; }

    public ITaskItem[] AssemblyAttributes { get; set; } = [];

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public override bool Execute()
    {
        GetAssemblyAttributes reader = new()
        {
            BuildEngine = BuildEngine,
            TaskEnvironment = TaskEnvironment,
            PathToTemplateFile = PathToTemplateFile
        };
        if (!reader.Execute())
        {
            return false;
        }

        List<ITaskItem> attributes = new(AssemblyAttributes);
        attributes.AddRange(reader.AssemblyAttributes);
        if (!string.IsNullOrEmpty(Culture))
        {
            TaskItem cultureAttribute = new("System.Reflection.AssemblyCultureAttribute");
            cultureAttribute.SetMetadata("_Parameter1", Culture);
            attributes.Add(cultureAttribute);
        }

        WriteCodeFragment writer = new()
        {
            BuildEngine = BuildEngine,
            AssemblyAttributes = attributes.ToArray(),
            Language = "C#",
            OutputFile = new TaskItem(TaskEnvironment.GetAbsolutePath(OutputFile).Value)
        };

        return writer.Execute();
    }
}
