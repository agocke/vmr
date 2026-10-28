// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.RuntimeModel;

namespace Microsoft.DotNet.Build.Tasks.TargetFramework;

[MSBuildDeclaredIOTask]
public sealed class ChooseBestTargetFrameworksFromInputs : Task
{
    [Required]
    public string RuntimeGraphContents { get; set; } = null!;

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
        using StringReader reader = new(RuntimeGraphContents);
        TargetFrameworkResolver resolver = new(JsonRuntimeFormat.ReadRuntimeGraph(reader));
        BestTargetFrameworks = ChooseBestTargetFrameworksTask.ChooseFrameworks(
            BuildTargetFrameworks, SupportedTargetFrameworks, resolver, Distinct);
        return !Log.HasLoggedErrors;
    }
}
