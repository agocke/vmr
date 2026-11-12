// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Build.Tasks.TargetFramework;

[MSBuildDeclaredIOTask]
public sealed class ChooseBestTargetFrameworksFromInputs : Task
{
    [Required]
    public string RuntimeGraphPath { get; set; } = null!;

    [Required]
    public string RuntimeGraphInputsVersion { get; set; } = null!;

    [Required]
    public ITaskItem[] RuntimeGraphResources { get; set; } = [];

    [Required]
    public ITaskItem[] BuildTargetFrameworks { get; set; } = [];

    [Required]
    public string[] SupportedTargetFrameworks { get; set; } = [];

    public bool Distinct { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public ITaskItem[]? BestTargetFrameworks { get; set; }

    public override bool Execute()
    {
        TargetFrameworkResolver resolver = new(RuntimeGraphInputs.ReadItems(
            RuntimeGraphInputsVersion, RuntimeGraphPath, RuntimeGraphResources));
        BestTargetFrameworks = ChooseBestTargetFrameworksTask.ChooseFrameworks(
            BuildTargetFrameworks, SupportedTargetFrameworks, resolver, Distinct);
        return !Log.HasLoggedErrors;
    }
}
