// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Arcade.Sdk;

[MSBuildDeclaredIOTask]
public sealed class RegexReplaceFile : Task
{
    [Required]
    public ITaskItem InputFile { get; set; }

    [Required]
    public ITaskItem OutputFile { get; set; }

    [Required]
    public string Pattern { get; set; }

    [Required]
    public string Replacement { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; }

    public ITaskItem[] DeclaredOutputs { get; set; }

    public override bool Execute()
    {
        string contents = Regex.Replace(File.ReadAllText(InputFile.ItemSpec), Pattern, Replacement);
        string outputPath = Path.GetFullPath(OutputFile.ItemSpec);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, contents + Environment.NewLine, new UTF8Encoding(false, true));
        return true;
    }
}
