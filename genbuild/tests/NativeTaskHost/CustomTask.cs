// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.IO;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Tasks;

[MSBuildDeclaredIOTask]
public sealed class CopyFiles : ITask
{
    public IBuildEngine BuildEngine { get; set; } = null!;
    public ITaskHost HostObject { get; set; } = null!;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public bool Execute()
    {
        File.WriteAllText(DeclaredOutputs[0].ItemSpec, "custom implementation");
        return true;
    }
}
