// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.Build.Framework;

namespace Microsoft.CodeAnalysis.BuildTasks;

/// <summary>
/// Prepares a deterministic C# invocation and its file footprint without running the compiler.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class FormatCsc : Csc
{
    public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] SourceRoots { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] AdditionalCompilerInputs { get; set; } = Array.Empty<ITaskItem>();
    public bool DeterministicSourcePaths { get; set; }
    public string? CompilerToolExe { get; set; }
    public string? CompilerToolPath { get; set; }
    public bool UseSandbox { get; set; }

    [Output]
    public string CompilerArguments { get; private set; } = "";

    [Output]
    public string SandboxArguments { get; private set; } = "";

    [Output]
    public ITaskItem[] ArgumentPaths { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] CompilerInputs { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] CompilerOutputs { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] CompilerOutputTrees { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] PathMappings { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        var invocation = new CompilerInvocation(this, SourceRoots, AdditionalCompilerInputs);
        invocation.NormalizeParameters();
        Reject(!string.IsNullOrEmpty(ApplicationConfiguration), nameof(ApplicationConfiguration));
        DisableSdkPath = true;
        if (!invocation.Prepare(DeterministicSourcePaths, NoStandardLib, CompilerToolPath, CompilerToolExe, DocumentationFile, PdbFile))
        {
            return false;
        }

        CompilerArguments = GenerateResponseFileContents();
        CompilerInputs = invocation.Inputs;
        CompilerOutputs = invocation.Outputs;
        CompilerOutputTrees = invocation.OutputTrees;
        PathMappings = invocation.PathMappings;
        SandboxArguments = "";
        ArgumentPaths = Array.Empty<ITaskItem>();
        if (UseSandbox || System.IO.Path.DirectorySeparatorChar == '/')
        {
            SandboxArguments = invocation.FormatSandboxArguments(CompilerArguments, GenerateResponseFileContents);
            ArgumentPaths = invocation.ArgumentPaths;
        }
        return !Log.HasLoggedErrors;
    }

    private void Reject(bool condition, string parameter)
    {
        if (condition)
        {
            Log.LogErrorFromResources("FormatCsc.UnsupportedParameter", parameter);
        }
    }
}
