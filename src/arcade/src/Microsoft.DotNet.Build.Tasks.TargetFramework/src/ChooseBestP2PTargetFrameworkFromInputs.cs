// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.RuntimeModel;

namespace Microsoft.DotNet.Build.Tasks.TargetFramework;

[MSBuildDeclaredIOTask]
public sealed class ChooseBestP2PTargetFrameworkFromInputs : Task
{
    [Required]
    public string RuntimeGraphContents { get; set; } = null!;

    [Required]
    public string CurrentProjectTargetFramework { get; set; } = null!;

    public string? CurrentProjectTargetPlatform { get; set; }

    public bool OmitIncompatibleProjectReferences { get; set; }

    public ITaskItem[]? AnnotatedProjectReferences { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public ITaskItem[]? AssignedProjects { get; set; }

    public override bool Execute()
    {
        bool result = ChooseBestP2PTargetFrameworkTask.AssignFrameworks(
            AnnotatedProjectReferences,
            CurrentProjectTargetFramework,
            CurrentProjectTargetPlatform,
            OmitIncompatibleProjectReferences,
            CreateResolver,
            Log,
            out var assignedProjects);
        AssignedProjects = assignedProjects;
        return result;
    }

    private TargetFrameworkResolver CreateResolver()
    {
        using StringReader reader = new(RuntimeGraphContents);
        return new TargetFrameworkResolver(JsonRuntimeFormat.ReadRuntimeGraph(reader));
    }
}
