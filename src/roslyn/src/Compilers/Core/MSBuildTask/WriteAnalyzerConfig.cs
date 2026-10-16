// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.CommandLine;

namespace Microsoft.CodeAnalysis.BuildTasks;

/// <summary>
/// Writes exact analyzer configuration text as UTF-8 without a BOM.
/// Does not read or compare a previous output.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class WriteAnalyzerConfig : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string ConfigFileContents { get; set; } = string.Empty;

    [Required]
    public ITaskItem FileName { get; set; } = null!;

    public override bool Execute()
    {
        try
        {
            var path = TaskEnvironment.GetAbsolutePath(FileName.ItemSpec);
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            TaskEnvironment.FileWriteAllText(path, ConfigFileContents, encoding);
            return true;
        }
        catch (IOException ex)
        {
            Log.LogErrorFromException(ex);
            return false;
        }
    }
}
