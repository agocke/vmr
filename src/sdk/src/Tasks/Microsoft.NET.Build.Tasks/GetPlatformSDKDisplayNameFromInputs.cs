// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

[MSBuildDeclaredIOTask]
public sealed class GetPlatformSDKDisplayNameFromInputs : TaskBase
{
    public string InputsVersion { get; set; } = "";
    public ITaskItem[] SdkDisplayNames { get; set; } = [];
    [Required]
    public string TargetPlatformIdentifier { get; set; } = "";
    public string TargetPlatformVersion { get; set; } = "";
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    [Output]
    public string DisplayName { get; private set; } = "";

    protected override void ExecuteCore()
    {
        try
        {
            DisplayName = PlatformSDKInputs.Select(
                InputsVersion, SdkDisplayNames, TargetPlatformIdentifier, TargetPlatformVersion);
        }
        catch (FormatException ex)
        {
            Log.LogError(ex.Message);
        }
    }
}
