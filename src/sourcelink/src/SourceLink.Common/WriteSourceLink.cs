// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the License.txt file in the project root for more information.

using System;
using System.IO;
using System.Security;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks.SourceControl;
using Microsoft.Build.Utilities;

namespace Microsoft.SourceLink.Common
{
    [MSBuildDeclaredIOTask]
    [MSBuildMultiThreadableTask]
    public sealed class WriteSourceLink : Task
    {
        [Required]
        public string OutputFile { get; set; } = string.Empty;

        [Required]
        public string SourceLinkContent { get; set; } = string.Empty;

        public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            try
            {
                if (!PathUtilities.IsPathFullyQualified(OutputFile))
                {
                    throw new ArgumentException($"The path '{OutputFile}' must be fully qualified.", nameof(OutputFile));
                }

                File.WriteAllText(OutputFile, SourceLinkContent);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
            {
                Log.LogError(Resources.ErrorWritingToSourceLinkFile, OutputFile, e.Message);
                return false;
            }
        }
    }
}
