// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.CommandLine;

namespace Microsoft.CodeAnalysis.BuildTasks;

/// <summary>
/// Publishes Restore's discovered compiler and analyzer tool closure for a fresh Build evaluation.
/// </summary>
[MSBuildMultiThreadableTask]
public sealed class WriteCscInputs : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Required]
    public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string OutputFile { get; set; } = "";

    public override bool Execute()
    {
        var items = new XElement("ItemGroup");
        foreach (var input in Inputs)
        {
            var path = TaskEnvironment.GetAbsolutePath(input.ItemSpec).Value;
            var escapedPath = path.Replace("%", "%25").Replace("$", "%24").Replace("@", "%40")
                .Replace("(", "%28").Replace(")", "%29").Replace(";", "%3B")
                .Replace("'", "%27").Replace("*", "%2A").Replace("?", "%3F");
            items.Add(new XElement("_CscPreparedToolInput", new XAttribute("Include", escapedPath)));
        }

        try
        {
            TaskEnvironment.FileWriteAllText(
                TaskEnvironment.GetAbsolutePath(OutputFile),
                new XElement("Project", items).ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (IOException ex)
        {
            Log.LogErrorFromException(ex);
            return false;
        }
    }
}
