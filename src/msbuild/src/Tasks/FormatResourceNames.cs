// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#nullable enable

namespace Microsoft.Build.Tasks;

/// <summary>
/// Formats C# resource names from paths and metadata without discovering or reading source files.
/// </summary>
[MSBuildMultiThreadableTask]
[MSBuildDeclaredIOTask]
public sealed class FormatResourceNames : TaskExtension
{
    [Required]
    public ITaskItem[] ResourceFiles { get; set; } = [];

    public string RootNamespace { get; set; } = string.Empty;

    public bool PrependCultureAsDirectory { get; set; } = true;

    public bool EnableCustomCulture { get; set; }

    public bool UseDependentUponConvention { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public ITaskItem[] ResourceFilesWithManifestResourceNames { get; private set; } = [];

    public override bool Execute()
    {
        ResourceFilesWithManifestResourceNames = [];
        if (UseDependentUponConvention)
        {
            Log.LogErrorWithCodeFromResources("CommonSdk.HardenedResourceName", nameof(UseDependentUponConvention));
            return false;
        }

        ITaskItem[] result = new ITaskItem[ResourceFiles.Length];
        for (int i = 0; i < ResourceFiles.Length; i++)
        {
            ITaskItem source = ResourceFiles[i];
            if (source.GetMetadata("DependentUpon").Length != 0)
            {
                Log.LogErrorWithCodeFromResources("CommonSdk.HardenedResourceName", $"{source.ItemSpec}: DependentUpon");
                return false;
            }

            try
            {
                string name = CreateCSharpManifestResourceName.CreateManifestNameImpl(
                    source.ItemSpec,
                    source.GetMetadata("TargetPath"),
                    PrependCultureAsDirectory,
                    RootNamespace,
                    dependentUponFileName: null,
                    source.GetMetadata("Culture"),
                    binaryStream: null,
                    Log,
                    ConversionUtilities.ValidBooleanFalse(source.GetMetadata("WithCulture")),
                    EnableCustomCulture,
                    deterministic: true);

                TaskItem resource = new(source);
                resource.SetMetadata("ManifestResourceName", EscapingUtilities.Escape(name));
                if (resource.GetMetadata("LogicalName").Length == 0
                    && string.Equals(resource.GetMetadata("Type"), "Non-Resx", StringComparison.OrdinalIgnoreCase))
                {
                    resource.SetMetadata("LogicalName", EscapingUtilities.Escape(name));
                }
                result[i] = resource;
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                Log.LogErrorWithCodeFromResources("CreateManifestResourceName.Error", source.ItemSpec, e.Message);
                return false;
            }
        }

        ResourceFilesWithManifestResourceNames = result;
        return true;
    }
}
