// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Resources;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks.ResourceHandling;

#nullable enable

namespace Microsoft.Build.Tasks;

/// <summary>
/// Compiles inline string resources between explicitly declared paths without an incremental cache.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class CompileResources : TaskExtension, IMultiThreadableTask
{
    [Required]
    public ITaskItem SourceFile { get; set; } = null!;

    [Required]
    public ITaskItem OutputFile { get; set; } = null!;

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    public override bool Execute()
    {
        string source = GetPath(SourceFile);
        string output = GetPath(OutputFile);
        if (DeclaredInputs.Length != 1 || !FileUtilities.PathComparer.Equals(source, GetPath(DeclaredInputs[0])))
        {
            return Invalid(nameof(DeclaredInputs));
        }
        if (DeclaredOutputs.Length != 1 || !FileUtilities.PathComparer.Equals(output, GetPath(DeclaredOutputs[0])))
        {
            return Invalid(nameof(DeclaredOutputs));
        }
        if (!string.Equals(Path.GetExtension(source), ".resx", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(output), ".resources", StringComparison.OrdinalIgnoreCase))
        {
            Log.LogErrorWithCodeFromResources("CommonSdk.HardenedResourceCompilation", source);
            return false;
        }

        IReadOnlyList<IResource> resources;
        try
        {
            resources = MSBuildResXReader.GetResourcesFromFile(source, pathsRelativeToBasePath: true,
                Log, logWarningForBinaryFormatter: false, stringsOnly: true);
        }
        catch (Exception e) when (e is MSBuildResXException || ExceptionHandling.IsIoRelatedException(e))
        {
            Log.LogErrorWithCodeFromResources("General.InvalidResxFile", e.InnerException?.Message ?? e.Message);
            return false;
        }

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using ResourceWriter writer = new(output);
            foreach (IResource resource in resources)
            {
                resource.AddTo(writer);
            }
        }
        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
        {
            Log.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput", output, e.Message);
            return false;
        }

        return true;
    }

    private string GetPath(ITaskItem item) =>
        FileUtilities.NormalizePath(TaskEnvironment.GetAbsolutePath(item.ItemSpec)).Value;

    private bool Invalid(string parameter)
    {
        Log.LogErrorWithCodeFromResources("General.InvalidValue", parameter, nameof(CompileResources));
        return false;
    }
}
