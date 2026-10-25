// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Text.RegularExpressions;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Arcade.Sdk;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class RewriteRepositoryUrls : Task
{
    public string? RepositoryUrl { get; set; }

    public ITaskItem[] SourceRoots { get; set; } = [];

    [Required]
    public string Pattern { get; set; } = "";

    [Required]
    public string Replacement { get; set; } = "";

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public string RewrittenRepositoryUrl { get; private set; } = "";

    [Output]
    public ITaskItem[] RewrittenSourceRoots { get; private set; } = [];

    public override bool Execute()
    {
        var regex = new Regex(Pattern);
        RewrittenRepositoryUrl = regex.Replace(RepositoryUrl ?? "", Replacement);
        RewrittenSourceRoots = new ITaskItem[SourceRoots.Length];
        for (int i = 0; i < SourceRoots.Length; i++)
        {
            var root = new TaskItem(SourceRoots[i]);
            root.SetMetadata("ScmRepositoryUrl", ProjectCollection.Escape(regex.Replace(root.GetMetadata("ScmRepositoryUrl"), Replacement)));
            RewrittenSourceRoots[i] = root;
        }

        return true;
    }
}
