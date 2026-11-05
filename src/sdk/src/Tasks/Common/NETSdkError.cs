// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks
{
    /// <summary>
    /// Provides a localizable mechanism for logging an error from the SDK targets.
    /// </summary>
    [MSBuildDeclaredIOTask]
    public class
#if EXTENSIONS
        // This task source is shared with multiple task Dlls.  Since both tasks
        // may be loaded into the same project and each task accesses only resources
        // in its own assembly they must have a unique name so-as not to clash.
        NETBuildExtensionsError
#else
        NETSdkError
#endif
     : MessageBase
    {
        public ITaskItem[] DeclaredInputs { get; set; } = [];

        public ITaskItem[] DeclaredOutputs { get; set; } = [];

        protected override void LogMessage(string message)
        {
            Log.LogError(message);
        }
    }
}
