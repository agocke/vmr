// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks.Prepared;

[MSBuildDeclaredIOTask]
public sealed class CheckForDuplicateFrameworkReferences : global::Microsoft.NET.Build.Tasks.CheckForDuplicateFrameworkReferences
{
}

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ProcessFrameworkReferences : global::Microsoft.NET.Build.Tasks.ProcessFrameworkReferences
{
    protected override void ExecuteCore()
    {
        try
        {
            PreparedResources = new(ResourceFactsVersion, ResourceFacts, PackResources, TaskEnvironment);
            PreparedResources.ValidateContext(TargetingPackRoot ?? "", NetCoreRoot ?? "", NETCoreSdkVersion ?? "");
            base.ExecuteCore();
        }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
        {
            Log.LogError(ex.Message);
        }
    }
}

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ResolveAppHosts : global::Microsoft.NET.Build.Tasks.ResolveAppHosts
{
    protected override void ExecuteCore()
    {
        try
        {
            PreparedResources = new(ResourceFactsVersion, ResourceFacts, PackResources, TaskEnvironment);
            PreparedResources.ValidateContext(TargetingPackRoot ?? "");
            base.ExecuteCore();
        }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
        {
            Log.LogError(ex.Message);
        }
    }
}

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class GetPackageDirectory : global::Microsoft.NET.Build.Tasks.GetPackageDirectory
{
    protected override void ExecuteCore()
    {
        try
        {
            ExecutePrepared(new(ResourceFactsVersion, ResourceFacts, PackResources, TaskEnvironment));
        }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
        {
            Log.LogError(ex.Message);
        }
    }
}

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ResolveFrameworkReferences : global::Microsoft.NET.Build.Tasks.ResolveFrameworkReferences
{
}

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ResolveTargetingPackAssets : global::Microsoft.NET.Build.Tasks.ResolveTargetingPackAssets
{
    protected override void ExecuteCore()
    {
        try
        {
            FrameworkPackResources resources = new(ResourceFactsVersion, ResourceFacts, PackResources, TaskEnvironment);
            ExecutePrepared(resources);
            if (Log.HasLoggedErrors)
            {
                return;
            }
            FrameworkAnalyzerFiles = resources.AnalyzerCandidates();
            if (CollectReferenceFiles)
            {
                FrameworkReferenceFiles = FrameworkReferenceFiles.Concat(resources.ReferenceCandidates())
                    .Concat(ReferencesToAdd).ToArray();
            }
            List<ITaskItem> platformItems = [];
            foreach (ITaskItem manifest in PlatformManifests)
            {
                string root = Path.GetDirectoryName(Path.GetDirectoryName(
                    TaskEnvironment.GetAbsolutePath(manifest.ItemSpec).Value))!;
                platformItems.AddRange(resources.PlatformItems(root));
            }
            PlatformConflictItems = platformItems.ToArray();
        }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
        {
            Log.LogError(ex.Message);
        }
    }
}
