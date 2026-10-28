// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Arcade.Sdk;

[MSBuildDeclaredIOTask]
public sealed class ReplaceTextFile : Task
{
    [Required]
    public ITaskItem InputFile { get; set; } = null!;

    [Required]
    public ITaskItem OutputFile { get; set; } = null!;

    [Required]
    public ITaskItem[] Replacements { get; set; } = [];

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public override bool Execute()
    {
        string contents = File.ReadAllText(InputFile.ItemSpec);
        foreach (ITaskItem replacement in Replacements)
        {
            contents = contents.Replace(replacement.ItemSpec, replacement.GetMetadata("Value"));
        }

        // Preserve the legacy WriteLinesToFile conversion, including whitespace and path separators.
        contents = new TaskItem(contents.Trim().Replace("%", "%25")).ItemSpec;
        string outputPath = Path.GetFullPath(OutputFile.ItemSpec);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, contents.Length == 0 ? "" : contents + Environment.NewLine, new UTF8Encoding(false, true));
        return true;
    }
}
