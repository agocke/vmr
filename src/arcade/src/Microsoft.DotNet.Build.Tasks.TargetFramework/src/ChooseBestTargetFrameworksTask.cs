// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Frameworks;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.DotNet.Build.Tasks.TargetFramework;

[MSBuildMultiThreadableTask]
public class ChooseBestTargetFrameworksTask : Task, IMultiThreadableTask
{
    /// <summary>Injected by MSBuild so paths resolve against the project directory in multithreaded builds.</summary>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Required]
    public ITaskItem[]? BuildTargetFrameworks { get; set; }

    [Required]
    public string? RuntimeGraph { get; set; }

    [Required]
    public string[]? SupportedTargetFrameworks { get; set; }

    // Returns distinct items only. Compares the include values. Metadata is ignored.
    public bool Distinct { get; set; }

    [Output]
    public ITaskItem[]? BestTargetFrameworks { get; set; }

    public override bool Execute()
    {
        TargetFrameworkResolver targetframeworkResolver = TargetFrameworkResolver.CreateOrGet(TaskEnvironment.GetAbsolutePath(RuntimeGraph!));
        BestTargetFrameworks = ChooseFrameworks(BuildTargetFrameworks!, SupportedTargetFrameworks!, targetframeworkResolver, Distinct);
        return !Log.HasLoggedErrors;
    }

    internal static ITaskItem[] ChooseFrameworks(
        ITaskItem[] buildTargetFrameworks,
        string[] supportedTargetFrameworks,
        TargetFrameworkResolver resolver,
        bool distinct)
    {
        List<ITaskItem> bestTargetFrameworkList = new(buildTargetFrameworks.Length);

        foreach (ITaskItem buildTargetFramework in buildTargetFrameworks)
        {
            NuGetFramework framework = NuGetFramework.ParseFolder(buildTargetFramework.ItemSpec);
            string? bestTargetFramework = resolver.GetNearest(supportedTargetFrameworks, framework);
            if (bestTargetFramework != null && (!distinct || !bestTargetFrameworkList.Any(b => b.ItemSpec == bestTargetFramework)))
            {
                TaskItem item = new(bestTargetFramework);
                buildTargetFramework.CopyMetadataTo(item);
                bestTargetFrameworkList.Add(item);
            }
        }

        return bestTargetFrameworkList.ToArray();
    }
}
