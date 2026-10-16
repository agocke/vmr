// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.CommandLine;

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

    [Output]
    public string CompilerArguments { get; private set; } = "";

    [Output]
    public ITaskItem[] CompilerInputs { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] CompilerOutputs { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] PathMappings { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        // Captured invocations retain empty strings; ordinary MSBuild leaves those
        // parameters unset. Preserve Csc's distinction between absent and empty switches.
        foreach (var name in _store.Where(property => property.Value is string { Length: 0 }).Select(property => property.Key).ToArray())
        {
            _store.Remove(name);
        }

        TargetType = TargetType?.ToLowerInvariant();
        DebugType = DebugType?.ToLowerInvariant();
#if NETFRAMEWORK
        Reject(true, nameof(TargetFramework));
#endif
        if (!Deterministic)
        {
            Log.LogErrorFromResources("FormatCsc.RequiredParameter", nameof(Deterministic));
        }

        if (!DeterministicSourcePaths)
        {
            Log.LogErrorFromResources("FormatCsc.RequiredParameter", nameof(DeterministicSourcePaths));
        }

        Reject(ResponseFiles?.Length > 0, nameof(ResponseFiles));
        Reject(AdditionalLibPaths?.Length > 0, nameof(AdditionalLibPaths));
        Reject(!string.IsNullOrEmpty(GeneratedFilesOutputPath), nameof(GeneratedFilesOutputPath));
        Reject(!string.IsNullOrEmpty(KeyContainer), nameof(KeyContainer));
        Reject(!string.IsNullOrEmpty(KeyFile) && !PublicSign && !DelaySign, nameof(KeyFile));
        Reject(!string.IsNullOrEmpty(ApplicationConfiguration), nameof(ApplicationConfiguration));
        Reject(!string.IsNullOrEmpty(CodeAnalysisRuleSet), nameof(CodeAnalysisRuleSet));
        Reject(!string.IsNullOrEmpty(ErrorLog), nameof(ErrorLog));
        Reject(EnvironmentVariables?.Length > 0, nameof(EnvironmentVariables));
        Reject(!string.IsNullOrEmpty(CompilerToolPath), nameof(CompilerToolPath));
        Reject(!string.IsNullOrEmpty(CompilerToolExe), nameof(CompilerToolExe));
        Reject(HostObject != null, nameof(HostObject));
        Reject(SkipCompilerExecution || ProvideCommandLineArgs, nameof(SkipCompilerExecution));
        Reject(!NoConfig, nameof(NoConfig));
        Reject(!NoStandardLib, nameof(NoStandardLib));
        Reject(OutputAssembly == null || string.IsNullOrEmpty(OutputAssembly.ItemSpec), nameof(OutputAssembly));
        Reject(TargetType is not ("library" or "exe" or "winexe" or "module"), nameof(TargetType));
        Reject(!string.IsNullOrEmpty(DebugType) && DebugType is not ("none" or "portable" or "embedded"), nameof(DebugType));
        Reject(Features?.Split(',', ';').Any(feature => feature.Split('=')[0].Trim() == "debug-determinism") == true, nameof(Features));

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        // Do not inherit machine-specific compiler search paths or source encodings.
        DisableSdkPath = true;
        if (CodePage == 0)
        {
            CodePage = 65001;
        }

        if (string.IsNullOrEmpty(DebugType) && EmitDebugInformation)
        {
            DebugType = "portable";
        }

        Sources = Sources?.Select(source =>
        {
            var item = new TaskItem(source);
            item.ItemSpec = TaskEnvironment.GetAbsolutePath(source.ItemSpec).Value;
            return (ITaskItem)item;
        }).ToArray();

        var inputs = new List<ITaskItem>();
        AddItems(inputs, Sources);
        AddItems(inputs, References);
        AddItems(inputs, Analyzers);
        AddItems(inputs, AnalyzerConfigFiles);
        AddItems(inputs, AdditionalFiles);
        AddItems(inputs, EmbeddedFiles);
        AddItems(inputs, Resources);
        AddItems(inputs, LinkResources);
        AddItems(inputs, AdditionalCompilerInputs);
        foreach (var module in AddModules ?? Array.Empty<string>())
        {
            AddPath(inputs, module);
        }

        AddPath(inputs, SourceLink);
        AddPath(inputs, KeyFile);
        AddPath(inputs, Win32Icon);
        AddPath(inputs, Win32Manifest);
        AddPath(inputs, Win32Resource);

        var outputs = new List<ITaskItem>();
        AddPath(outputs, OutputAssembly!.ItemSpec);
        AddPath(outputs, OutputRefAssembly?.ItemSpec);
        AddPath(outputs, DocumentationFile);
        if (!RefOnly && DebugType == "portable")
        {
            AddPath(outputs, string.IsNullOrEmpty(PdbFile) ? Path.ChangeExtension(OutputAssembly.ItemSpec, ".pdb") : PdbFile);
        }

        if (!SetPathMap(outputs))
        {
            return false;
        }

        CompilerArguments = GenerateResponseFileContents();
        CompilerInputs = inputs.ToArray();
        CompilerOutputs = outputs.ToArray();
        return !Log.HasLoggedErrors;
    }

    private void Reject(bool condition, string parameter)
    {
        if (condition)
        {
            Log.LogErrorFromResources("FormatCsc.UnsupportedParameter", parameter);
        }
    }

    private void AddPath(List<ITaskItem> paths, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            paths.Add(new TaskItem(TaskEnvironment.GetAbsolutePath(path).Value));
        }
    }

    private void AddItems(List<ITaskItem> paths, ITaskItem[]? items)
    {
        foreach (var item in items ?? Array.Empty<ITaskItem>())
        {
            AddPath(paths, item.ItemSpec);
        }
    }

    private bool SetPathMap(List<ITaskItem> outputs)
    {
        var roots = SourceRoots.Select(root => (ITaskItem)new TaskItem(root)).ToList();
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        void addRoot(string directory)
        {
            var fullPath = TaskEnvironment.GetFullPathNoThrow(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!roots.Any(root => fullPath.StartsWith(TaskEnvironment.GetFullPathNoThrow(root.ItemSpec), comparison)))
            {
                roots.Add(new TaskItem(fullPath));
            }
        }

        // Preserve repository mappings: a more-specific project mapping would win in the
        // compiler's longest-prefix lookup and break the corresponding SourceLink URLs.
        addRoot(TaskEnvironment.ProjectDirectory.Value);
        foreach (var source in Sources ?? Array.Empty<ITaskItem>())
        {
            addRoot(Path.GetDirectoryName(TaskEnvironment.GetAbsolutePath(source.ItemSpec).Value)!);
        }

        // This also covers generator document paths and the PE's CodeView PDB path.
        foreach (var output in outputs)
        {
            addRoot(Path.GetDirectoryName(output.ItemSpec)!);
        }

        var mapper = new MapSourceRoots
        {
            BuildEngine = BuildEngine,
            TaskEnvironment = TaskEnvironment,
            Deterministic = true,
            SourceRoots = roots.ToArray(),
        };

        if (!mapper.Execute())
        {
            return false;
        }

        PathMappings = mapper.MappedSourceRoots!
            .Where(root => string.IsNullOrEmpty(root.GetMetadata("NestedRoot"))).ToArray();
        PathMap = string.Join(",", PathMappings
            .Select(root => Escape(root.ItemSpec) + "=" + Escape(root.GetMetadata("MappedPath"))));
        return true;
    }

    private static string Escape(string path) => path.Replace(",", ",,").Replace("=", "==");
}
