// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public class ValidateNoWinMDReferences : TaskBase
{
    public string TargetFrameworkMoniker { get; set; } = string.Empty;

    public ITaskItem[] ReferencePaths { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();

    protected override void ExecuteCore()
    {
        foreach (ITaskItem referencePath in ReferencePaths)
        {
            if (string.Equals(Path.GetExtension(referencePath.ItemSpec), ".winmd", StringComparison.OrdinalIgnoreCase))
            {
                Log.LogError(Strings.WinMDReferenceNotSupportedOnTargetFramework,
                    TargetFrameworkMoniker, Path.GetFileName(referencePath.ItemSpec));
            }
        }
    }
}
