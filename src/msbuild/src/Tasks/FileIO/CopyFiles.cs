// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;

#nullable enable

namespace Microsoft.Build.Tasks;

/// <summary>
/// Copies file contents between explicit, disjoint input and output paths.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class CopyFiles : TaskExtension, IMultiThreadableTask
{
    public ITaskItem[] SourceFiles { get; set; } = [];
    public ITaskItem[] DestinationFiles { get; set; } = [];
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    public override bool Execute()
    {
        if (SourceFiles.Length != DestinationFiles.Length)
        {
            Log.LogErrorWithCodeFromResources("General.TwoVectorsMustHaveSameLength",
                SourceFiles.Length, DestinationFiles.Length, nameof(SourceFiles), nameof(DestinationFiles));
            return false;
        }

        string[] sources = GetPaths(SourceFiles);
        string[] destinations = GetPaths(DestinationFiles);
        HashSet<string> inputs = new(sources, FileUtilities.PathComparer);
        HashSet<string> outputs = new(destinations, FileUtilities.PathComparer);
        if (!inputs.SetEquals(GetPaths(DeclaredInputs)))
        {
            return Invalid(nameof(DeclaredInputs));
        }
        if (!outputs.SetEquals(GetPaths(DeclaredOutputs)))
        {
            return Invalid(nameof(DeclaredOutputs));
        }
        if (outputs.Count != destinations.Length || outputs.Overlaps(inputs))
        {
            return Invalid(nameof(DestinationFiles));
        }

        for (int i = 0; i < sources.Length; i++)
        {
            try
            {
                using FileStream source = File.OpenRead(sources[i]);
                using FileStream destination = File.Create(destinations[i]);
                source.CopyTo(destination);
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                Log.LogErrorWithCodeFromResources("Copy.Error", sources[i], destinations[i], e.Message);
                return false;
            }
        }

        return true;
    }

    private string[] GetPaths(ITaskItem[] items) =>
        Array.ConvertAll(items, item => FileUtilities.NormalizePath(TaskEnvironment.GetAbsolutePath(item.ItemSpec)).Value);

    private bool Invalid(string parameter)
    {
        Log.LogErrorWithCodeFromResources("General.InvalidValue", parameter, nameof(CopyFiles));
        return false;
    }
}
