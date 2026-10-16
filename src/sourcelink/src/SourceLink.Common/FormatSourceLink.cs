// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the License.txt file in the project root for more information.

using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.SourceLink.Common
{
    [MSBuildDeclaredIOTask]
    [MSBuildMultiThreadableTask]
    public sealed class FormatSourceLink : Task
    {
        [Required]
        public ITaskItem[] SourceRoots { get; set; } = Array.Empty<ITaskItem>();

        public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();

        public bool NoWarnOnMissingSourceControlInformation { get; set; }

        [Output]
        public string SourceLinkContent { get; private set; } = string.Empty;

        public override bool Execute()
        {
            SourceLinkContent = GenerateSourceLinkFile.GenerateSourceLinkContent(SourceRoots, Log) ?? string.Empty;
            if (SourceLinkContent.Length == 0 && !NoWarnOnMissingSourceControlInformation)
            {
                Log.LogWarning(Resources.SourceControlInformationIsNotAvailableGeneratedSourceLinkEmpty);
            }

            return !Log.HasLoggedErrors;
        }
    }
}
