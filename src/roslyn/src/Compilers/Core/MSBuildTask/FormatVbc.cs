// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.CodeAnalysis.BuildTasks;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class FormatVbc : Vbc
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
        void reject(bool condition, string parameter)
        {
            if (condition)
            {
                Log.LogErrorFromResources("FormatCsc.UnsupportedParameter", parameter);
            }
        }

        reject(!string.IsNullOrEmpty(SdkPath), nameof(SdkPath));
        reject(TargetCompactFramework, nameof(TargetCompactFramework));
        var runtime = VBRuntime;
        var explicitRuntime = !string.IsNullOrEmpty(runtime) &&
            (Path.IsPathRooted(runtime) || runtime.IndexOfAny(new[] { '/', '\\' }) >= 0);
        reject(!(NoVBRuntimeReference && string.IsNullOrEmpty(runtime)) && !explicitRuntime &&
            !string.Equals(runtime, "None", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(runtime, "Embed", StringComparison.OrdinalIgnoreCase), nameof(VBRuntime));
        reject(!string.IsNullOrEmpty(VBRuntimePath) &&
            (!explicitRuntime || !string.Equals(runtime, VBRuntimePath, StringComparison.Ordinal)), nameof(VBRuntimePath));

        // Vbc's ordinary task moves the PDB after compilation; an action cannot hide that write.
        reject(!string.IsNullOrEmpty(PdbFile) && OutputAssembly != null &&
            TaskEnvironment.GetAbsolutePath(PdbFile).Value != TaskEnvironment.GetAbsolutePath(Path.ChangeExtension(OutputAssembly.ItemSpec, ".pdb")).Value,
            nameof(PdbFile));
        DisableSdkPath = true;
        if (GenerateDocumentation && string.IsNullOrEmpty(DocumentationFile) && OutputAssembly != null)
        {
            DocumentationFile = Path.ChangeExtension(OutputAssembly.ItemSpec, ".xml");
        }
        var documentationFile = _store[nameof(GenerateDocumentation)] is false ? null : DocumentationFile;
        if (!invocation.Prepare(DeterministicSourcePaths, NoStandardLib, CompilerToolPath, CompilerToolExe, documentationFile, PdbFile))
        {
            return false;
        }
        CompilerArguments = GenerateResponseFileContents();
        CompilerInputs = invocation.Inputs;
        if (explicitRuntime)
        {
            var inputs = new List<ITaskItem>(CompilerInputs)
            {
                new TaskItem(CompilerInvocation.EscapeItem(TaskEnvironment.GetAbsolutePath(runtime!).Value)),
            };
            CompilerInputs = inputs.ToArray();
        }
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
}
