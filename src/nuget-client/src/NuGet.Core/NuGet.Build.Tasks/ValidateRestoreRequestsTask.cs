// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using NuGet.Commands;
using NuGet.Common;
using NuGet.ProjectModel;

namespace NuGet.Build.Tasks;

public sealed class ValidateRestoreRequestsTask : Microsoft.Build.Utilities.Task
{
    private const string RequestIdentityMetadata = "_MSBuildRestoreRequestId";

    [Required]
    public ITaskItem[] RestoreGraphItems { get; set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] ValidatedGraphItems { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        ValidatedGraphItems = Array.Empty<ITaskItem>();
        var comparer = PathUtility.GetStringComparerBasedOnOS();
        var projects = new Dictionary<string, Dictionary<string, List<ITaskItem>>>(comparer);
        foreach (ITaskItem item in RestoreGraphItems)
        {
            string project = item.GetMetadata("ProjectUniqueName");
            string identity = item.GetMetadata(RequestIdentityMetadata);
            if (project.Length == 0 || identity.Length == 0)
            {
                Log.LogError("Restore graph entries must retain their acquisition request identity.");
                return false;
            }

            if (!projects.TryGetValue(project, out var requests))
            {
                requests = new Dictionary<string, List<ITaskItem>>(StringComparer.Ordinal);
                projects.Add(project, requests);
            }

            if (!requests.TryGetValue(identity, out var entries))
            {
                entries = new List<ITaskItem>();
                requests.Add(identity, entries);
            }

            entries.Add(item);
        }

        var selectedRequests = new Dictionary<string, string>(comparer);
        foreach (var project in projects)
        {
            if (project.Value.Count == 1)
            {
                continue;
            }

            PackageSpec? first = null;
            foreach (var request in project.Value)
            {
                var items = new List<IMSBuildItem>(request.Value.Count);
                foreach (ITaskItem item in request.Value)
                {
                    items.Add(new MSBuildTaskItem(item));
                }

                PackageSpec? specification = MSBuildRestoreUtility.GetPackageSpec(items);
                if (specification is null)
                {
                    continue;
                }

                if (first is null)
                {
                    first = specification;
                    selectedRequests.Add(project.Key, request.Key);
                }
                else if (!first.Equals(specification)
                    || !string.Equals(first.Name, specification.Name, StringComparison.Ordinal)
                    || !comparer.Equals(first.FilePath, specification.FilePath))
                {
                    Log.LogError(
                        "Distinct acquisition requests for '{0}' produced different NuGet specifications. "
                        + "This Restore profile requires a compatible acquisition batch with one specification per project path. "
                        + "Use separate Restore invocations with disjoint restore output paths for incompatible requests; "
                        + "conflicting specifications cannot share an assets file.",
                        project.Key);
                    return false;
                }
            }
        }

        var result = new List<ITaskItem>(RestoreGraphItems.Length);
        foreach (ITaskItem item in RestoreGraphItems)
        {
            if (string.Equals(item.GetMetadata("Type"), "RestoreSpec", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.GetMetadata("Type"), "NuGetInputRequest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.GetMetadata("Type"), "FrameworkPackInputRequest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.GetMetadata("Type"), "FrameworkPackInput", StringComparison.OrdinalIgnoreCase)
                || !selectedRequests.TryGetValue(item.GetMetadata("ProjectUniqueName"), out string? identity)
                || string.Equals(item.GetMetadata(RequestIdentityMetadata), identity, StringComparison.Ordinal))
            {
                result.Add(item);
            }
        }

        ValidatedGraphItems = result.ToArray();
        return true;
    }
}
