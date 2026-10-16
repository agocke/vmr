// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.BuildTasks;

/// <summary>
/// Formats a global analyzer configuration from supplied properties and metadata without file I/O.
/// Relative paths are resolved against the task's project directory.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class FormatAnalyzerConfig : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public ITaskItem[] PropertyItems { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public ITaskItem[] MetadataItems { get; set; } = Array.Empty<ITaskItem>();

    [Output]
    public string ConfigFileContents { get; private set; } = string.Empty;

    public FormatAnalyzerConfig() : base(ErrorString.ResourceManager)
    {
    }

    public override bool Execute()
    {
        foreach (var item in MetadataItems)
        {
            string metadataName = item.GetMetadata("MetadataName");
            if (!string.IsNullOrWhiteSpace(item.GetMetadata("ItemType")) &&
                (metadataName.Equals("ModifiedTime", StringComparison.OrdinalIgnoreCase) ||
                 metadataName.Equals("CreatedTime", StringComparison.OrdinalIgnoreCase) ||
                 metadataName.Equals("AccessedTime", StringComparison.OrdinalIgnoreCase)))
            {
                Log.LogErrorFromResources("FormatAnalyzerConfig.FileMetadataNotSupported", metadataName, item.ItemSpec);
            }
        }

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        ConfigFileContents = GenerateMSBuildEditorConfig.Format(PropertyItems, MetadataItems, GetFullPath, GetMetadata);
        return true;
    }

    private string GetFullPath(ITaskItem item)
    {
        var path = PlatformInformation.IsUnix ? item.ItemSpec.Replace('\\', '/') : item.ItemSpec;
#pragma warning disable RS0030 // The project directory is explicit; this only canonicalizes an absolute path.
        return Path.GetFullPath(TaskEnvironment.GetAbsolutePath(path).Value);
#pragma warning restore RS0030
    }

    private string GetMetadata(ITaskItem item, string name)
    {
        if (name.Equals("FullPath", StringComparison.OrdinalIgnoreCase))
        {
            return GetFullPath(item);
        }

        if (name.Equals("RootDir", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory", StringComparison.OrdinalIgnoreCase))
        {
            // These modifiers derive from FullPath. Do not let a relative item use ambient CWD.
            return new TaskItem(GetFullPath(item)).GetMetadata(name);
        }

        return item.GetMetadata(name);
    }
}
