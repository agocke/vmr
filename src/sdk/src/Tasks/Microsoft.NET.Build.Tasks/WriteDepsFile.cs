// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Extensions.DependencyModel;
using NuGet.RuntimeModel;

namespace Microsoft.NET.Build.Tasks;

/// <summary>
/// Writes a dependency manifest from explicit metadata and declared file contents.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class WriteDepsFile : GenerateDepsFile
{
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public string? ReferenceAssembliesPath { get; set; }

    protected override void ExecuteCore()
    {
        var inputs = new HashSet<string>(DeclaredInputs.Select(item => TaskEnvironment.GetAbsolutePath(item.ItemSpec).Value), PathComparer);
        string output = TaskEnvironment.GetAbsolutePath(DepsFilePath).Value;
        if (DeclaredOutputs.Length != 1 || !PathComparer.Equals(output, TaskEnvironment.GetAbsolutePath(DeclaredOutputs[0].ItemSpec).Value))
        {
            throw new BuildErrorException(Strings.UnsupportedDepsFileParameter, nameof(DeclaredOutputs), DepsFilePath);
        }

        string assetsPath = Input(AssetsFilePath);
        var lockFile = LockFileCache.ReadLockFile(assetsPath, Log);
        RuntimeGraph? runtimeGraph = IsSelfContained ? JsonRuntimeFormat.ReadRuntimeGraph(Input(RuntimeGraphPath)) : null;
        var context = BuildDependencyContext(
            TaskEnvironment.GetAbsolutePath(ProjectPath),
            lockFile,
            runtimeGraph,
            ReferenceAssembliesPath,
            ResolveRuntimeFile);
        ValidatePaths(context);
        WriteDependencyContext(context, TaskEnvironment.GetAbsolutePath(DepsFilePath));

        string Input(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new BuildErrorException(Strings.UnsupportedDepsFileParameter, nameof(DeclaredInputs), path);
            }

            string fullPath = TaskEnvironment.GetAbsolutePath(path).Value;
            if (!inputs.Contains(fullPath))
            {
                throw new BuildErrorException(Strings.UndeclaredDepsFileInput, fullPath);
            }

            return fullPath;
        }

        RuntimeFile ResolveRuntimeFile(string path, string sourcePath, string localPath)
        {
            if (!IncludeRuntimeFileVersions)
            {
                return new RuntimeFile(path, null, null, localPath);
            }

            var versions = RuntimeFileVersions.Read(Input(sourcePath));
            return new RuntimeFile(path, versions.AssemblyVersion, versions.FileVersion, localPath);
        }
    }

    private static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void ValidatePaths(DependencyContext context)
    {
        RelativePath(context.CompilationOptions.KeyFile);
        foreach (var library in context.CompileLibraries)
        {
            RelativePath(library.Path);
            RelativePath(library.HashPath);
            foreach (string path in library.Assemblies)
            {
                RelativePath(path);
            }
        }

        foreach (var library in context.RuntimeLibraries)
        {
            RelativePath(library.Path);
            RelativePath(library.HashPath);
            RelativePath(library.RuntimeStoreManifestName);
            foreach (var file in library.RuntimeAssemblyGroups.Concat(library.NativeLibraryGroups).SelectMany(group => group.RuntimeFiles))
            {
                RelativePath(file.Path);
                RelativePath(file.LocalPath);
            }

            foreach (var resource in library.ResourceAssemblies)
            {
                RelativePath(resource.Path);
                RelativePath(resource.LocalPath);
            }
        }
    }

    private static void RelativePath(string? path)
    {
        if (!string.IsNullOrEmpty(path) && (path[0] is '/' or '\\' || (path.Length > 1 && path[1] == ':')))
        {
            throw new BuildErrorException(Strings.UnsupportedDepsFileParameter, "manifest path", path);
        }
    }
}
