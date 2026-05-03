// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.DotNet.GenFacades;

string outputPath = "";
string message = "";
string langVersion = "preview";
string apiExclusionListPath = "";
var sourceFiles = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i].TrimStart('\'').TrimEnd('\'');
    if (StartsWith(arg, "--output-path=", out var outPath))
    {
        outputPath = outPath;
    }
    else if (StartsWith(arg, "--message=", out var msg))
    {
        message = msg;
    }
    else if (StartsWith(arg, "--lang-version=", out var lang))
    {
        langVersion = lang;
    }
    else if (StartsWith(arg, "--api-exclusion-list=", out var excl))
    {
        apiExclusionListPath = excl;
    }
    else if (StartsWith(arg, "--source=", out var src))
    {
        sourceFiles.Add(src);
    }
    else
    {
        throw new ArgumentException($"Unknown argument: {arg}");
    }
}

if (string.IsNullOrEmpty(outputPath))
    throw new ArgumentException("--output-path is required");
if (string.IsNullOrEmpty(message))
    throw new ArgumentException("--message is required");
if (sourceFiles.Count == 0)
    throw new ArgumentException("At least one --source is required");

// Process each source file individually (the task generates one output per
// source file), then concatenate all results into the single output path.
string tempDir = Path.Combine(Path.GetTempPath(), "gen_pnse_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);

try
{
    var taskItems = new ITaskItem[sourceFiles.Count];
    for (int i = 0; i < sourceFiles.Count; i++)
    {
        string tempOutput = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(sourceFiles[i]) + ".notsupported.cs");
        var item = new TaskItem(sourceFiles[i]);
        item.SetMetadata("OutputPath", tempOutput);
        taskItems[i] = item;
    }

    var generator = new NotSupportedAssemblyGenerator
    {
        BuildEngine = new SimpleBuildEngine(),
        SourceFiles = taskItems,
        Message = message,
        LangVersion = langVersion,
        ApiExclusionListPath = apiExclusionListPath,
    };

    if (!generator.Execute())
    {
        Environment.Exit(1);
    }

    // Concatenate all generated files into the final output
    using var writer = new StreamWriter(outputPath);
    foreach (var item in taskItems)
    {
        string tempOutput = item.GetMetadata("OutputPath");
        if (File.Exists(tempOutput))
        {
            writer.Write(File.ReadAllText(tempOutput));
        }
    }
}
finally
{
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, recursive: true);
}

static bool StartsWith(string s, string prefix, [NotNullWhen(true)] out string? rest)
{
    if (s.StartsWith(prefix))
    {
        rest = s[prefix.Length..];
        return true;
    }
    rest = null;
    return false;
}

/// <summary>
/// Minimal MSBuild BuildEngine implementation for running tasks outside MSBuild.
/// </summary>
sealed class SimpleBuildEngine : IBuildEngine
{
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "";

    public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => false;
    public void LogCustomEvent(CustomBuildEventArgs e) => Console.Error.WriteLine(e.Message);
    public void LogErrorEvent(BuildErrorEventArgs e) => Console.Error.WriteLine($"error: {e.Message}");
    public void LogMessageEvent(BuildMessageEventArgs e) { }
    public void LogWarningEvent(BuildWarningEventArgs e) => Console.Error.WriteLine($"warning: {e.Message}");
}
