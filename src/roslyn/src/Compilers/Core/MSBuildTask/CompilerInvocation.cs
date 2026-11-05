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

internal sealed class CompilerInvocation(ManagedCompiler compiler, ITaskItem[] sourceRoots, ITaskItem[] additionalInputs)
{
    public ITaskItem[] Inputs { get; private set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] Outputs { get; private set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] OutputTrees { get; private set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] PathMappings { get; private set; } = Array.Empty<ITaskItem>();

    public void NormalizeParameters()
    {
        // Captured invocations retain empty strings; ordinary MSBuild leaves them unset.
        foreach (var name in compiler._store.Where(property => property.Value is string { Length: 0 }).Select(property => property.Key).ToArray())
        {
            compiler._store.Remove(name);
        }

        compiler.TargetType = compiler.TargetType?.ToLowerInvariant();
        compiler.DebugType = compiler.DebugType?.ToLowerInvariant();
    }

    public bool Prepare(bool deterministicSourcePaths, bool noStandardLib, string? toolPath, string? toolExe, string? documentationFile, string? pdbFile)
    {
        void reject(bool condition, string parameter)
        {
            if (condition)
            {
                compiler.Log.LogErrorFromResources("FormatCsc.UnsupportedParameter", parameter);
            }
        }

#if NETFRAMEWORK
        reject(true, nameof(compiler.TargetFramework));
#endif
        if (!compiler.Deterministic)
        {
            compiler.Log.LogErrorFromResources("FormatCsc.RequiredParameter", nameof(compiler.Deterministic));
        }
        if (!deterministicSourcePaths)
        {
            compiler.Log.LogErrorFromResources("FormatCsc.RequiredParameter", "DeterministicSourcePaths");
        }
        reject(compiler.ResponseFiles?.Length > 0, nameof(compiler.ResponseFiles));
        reject(compiler.AdditionalLibPaths?.Length > 0, nameof(compiler.AdditionalLibPaths));
        reject(!string.IsNullOrEmpty(compiler.KeyContainer), nameof(compiler.KeyContainer));
        reject(!string.IsNullOrEmpty(compiler.KeyFile) && !compiler.PublicSign && !compiler.DelaySign, nameof(compiler.KeyFile));
        reject(!string.IsNullOrEmpty(compiler.CodeAnalysisRuleSet), nameof(compiler.CodeAnalysisRuleSet));
        reject(!string.IsNullOrEmpty(compiler.ErrorLog), nameof(compiler.ErrorLog));
        reject(compiler.EnvironmentVariables?.Length > 0, nameof(compiler.EnvironmentVariables));
        reject(!string.IsNullOrEmpty(toolPath), "CompilerToolPath");
        reject(!string.IsNullOrEmpty(toolExe), "CompilerToolExe");
        reject(compiler.HostObject != null, nameof(compiler.HostObject));
        reject(compiler.SkipCompilerExecution || compiler.ProvideCommandLineArgs, nameof(compiler.SkipCompilerExecution));
        reject(!compiler.NoConfig, nameof(compiler.NoConfig));
        reject(!noStandardLib, "NoStandardLib");
        reject(compiler.OutputAssembly == null || string.IsNullOrEmpty(compiler.OutputAssembly.ItemSpec), nameof(compiler.OutputAssembly));
        reject(compiler.TargetType is not ("library" or "exe" or "winexe" or "module"), nameof(compiler.TargetType));
        reject(!string.IsNullOrEmpty(compiler.DebugType) && compiler.DebugType is not ("none" or "portable" or "embedded"), nameof(compiler.DebugType));
        reject(compiler.Features?.Split(',', ';').Any(feature => feature.Split('=')[0].Trim() == "debug-determinism") == true, nameof(compiler.Features));
        if (compiler.Log.HasLoggedErrors)
        {
            return false;
        }

        if (compiler.CodePage == 0)
        {
            compiler.CodePage = 65001;
        }
        if (string.IsNullOrEmpty(compiler.DebugType) && compiler.EmitDebugInformation)
        {
            compiler.DebugType = "portable";
        }

        compiler.Sources = compiler.Sources?.Select(source =>
        {
            var item = new TaskItem(source);
            item.ItemSpec = EscapeItem(compiler.TaskEnvironment.GetAbsolutePath(source.ItemSpec).Value);
            return (ITaskItem)item;
        }).ToArray();

        var inputs = new List<ITaskItem>();
        void addPath(List<ITaskItem> paths, string? path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(new TaskItem(EscapeItem(compiler.TaskEnvironment.GetAbsolutePath(path).Value)));
            }
        }
        foreach (var items in new[] { compiler.Sources, compiler.References, compiler.Analyzers, compiler.AnalyzerConfigFiles,
            compiler.AdditionalFiles, compiler.EmbeddedFiles, compiler.Resources, compiler.LinkResources, additionalInputs })
        {
            foreach (var item in items ?? Array.Empty<ITaskItem>())
            {
                addPath(inputs, item.ItemSpec);
            }
        }
        foreach (var module in compiler.AddModules ?? Array.Empty<string>())
        {
            addPath(inputs, module);
        }
        foreach (var path in new[] { compiler.SourceLink, compiler.KeyFile, compiler.Win32Icon, compiler.Win32Manifest, compiler.Win32Resource })
        {
            addPath(inputs, path);
        }

        var outputs = new List<ITaskItem>();
        addPath(outputs, compiler.OutputAssembly!.ItemSpec);
        addPath(outputs, compiler.OutputRefAssembly?.ItemSpec);
        addPath(outputs, documentationFile);
        if (!compiler.RefOnly && compiler.DebugType == "portable")
        {
            addPath(outputs, string.IsNullOrEmpty(pdbFile) ? Path.ChangeExtension(compiler.OutputAssembly.ItemSpec, ".pdb") : pdbFile);
        }
        if (!string.IsNullOrEmpty(compiler.GeneratedFilesOutputPath))
        {
            compiler.GeneratedFilesOutputPath = compiler.TaskEnvironment.GetAbsolutePath(compiler.GeneratedFilesOutputPath).Value
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tree = new TaskItem(EscapeItem(compiler.GeneratedFilesOutputPath));
            tree.SetMetadata("ArchiveFile", EscapeItem(compiler.GeneratedFilesOutputPath + ".tree.zip"));
            tree.SetMetadata("DyndepFile", EscapeItem(compiler.GeneratedFilesOutputPath + ".tree.dd"));
            tree.SetMetadata("ReceiptFile", EscapeItem(compiler.GeneratedFilesOutputPath + ".tree.json"));
            addPath(outputs, tree.GetMetadata("ArchiveFile"));
            addPath(outputs, tree.GetMetadata("DyndepFile"));
            OutputTrees = new ITaskItem[] { tree };
        }

        var roots = sourceRoots.Select(root => (ITaskItem)new TaskItem(root)).ToList();
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        void addRoot(string directory)
        {
            var fullPath = compiler.TaskEnvironment.GetFullPathNoThrow(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!roots.Any(root => fullPath.StartsWith(compiler.TaskEnvironment.GetFullPathNoThrow(root.ItemSpec), comparison)))
            {
                roots.Add(new TaskItem(EscapeItem(fullPath)));
            }
        }

        // Preserve repository aliases; more-specific mappings would break SourceLink URLs.
        addRoot(compiler.TaskEnvironment.ProjectDirectory.Value);
        foreach (var source in compiler.Sources ?? Array.Empty<ITaskItem>())
        {
            addRoot(Path.GetDirectoryName(compiler.TaskEnvironment.GetAbsolutePath(source.ItemSpec).Value)!);
        }
        foreach (var output in outputs)
        {
            addRoot(Path.GetDirectoryName(output.ItemSpec)!);
        }
        foreach (var tree in OutputTrees)
        {
            addRoot(tree.ItemSpec);
        }

        var mapper = new MapSourceRoots
        {
            BuildEngine = compiler.BuildEngine,
            TaskEnvironment = compiler.TaskEnvironment,
            Deterministic = true,
            SourceRoots = roots.ToArray(),
        };
        if (!mapper.Execute())
        {
            return false;
        }
        PathMappings = mapper.MappedSourceRoots!.Where(root => string.IsNullOrEmpty(root.GetMetadata("NestedRoot"))).ToArray();
        compiler.PathMap = string.Join(",", PathMappings.Select(root => EscapeMap(root.ItemSpec) + "=" + EscapeMap(root.GetMetadata("MappedPath"))));
        Inputs = inputs.ToArray();
        Outputs = outputs.ToArray();
        return true;
    }

    private static string EscapeMap(string path) => path.Replace(",", ",,").Replace("=", "==");
    internal static string EscapeItem(string value) => value.Replace("%", "%25").Replace("$", "%24").Replace("@", "%40")
        .Replace("(", "%28").Replace(")", "%29").Replace(";", "%3B").Replace("'", "%27").Replace("*", "%2A").Replace("?", "%3F");
}
