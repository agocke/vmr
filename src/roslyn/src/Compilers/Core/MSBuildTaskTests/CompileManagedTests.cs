// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Xunit;

namespace Microsoft.CodeAnalysis.BuildTasks.UnitTests
{
#if !NETFRAMEWORK
    public sealed class CompileManagedTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ChildEnvironmentPreservesOnlyTaskRuntimeControls(bool includeControls)
        {
            var directory = Path.GetFullPath(".");
            var variables = new Dictionary<string, string>
            {
                ["PATH"] = "must not be inherited",
                ["DOTNET_STARTUP_HOOKS"] = "must not be inherited",
                ["COMPlus_JitDisasm"] = "must not be inherited",
            };
            var controls = new Dictionary<string, string>
            {
                ["SystemRoot"] = Path.Combine(directory, "system"),
                ["TMPDIR"] = Path.Combine(directory, "temporary"),
                ["TMP"] = Path.Combine(directory, "tmp"),
                ["TEMP"] = Path.Combine(directory, "temp"),
                ["DOTNET_EnableDiagnostics"] = "0",
            };
            if (includeControls)
            {
                foreach (var pair in controls)
                {
                    variables.Add(pair.Key, pair.Value);
                }
            }
            var task = new CompileManaged
            {
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory, variables),
            };

            var method = typeof(CompileManaged).GetMethod("GetProcessStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var info = (ProcessStartInfo)method.Invoke(task, new object[] { "dotnet", "", "" })!;

            Assert.Equal(directory, info.WorkingDirectory);
            Assert.Equal(includeControls ? controls.Count : 0, info.Environment.Count);
            foreach (var pair in controls)
            {
                Assert.Equal(includeControls, info.Environment.TryGetValue(pair.Key, out var value));
                Assert.Equal(includeControls ? pair.Value : null, value);
            }
            Assert.False(info.Environment.ContainsKey("PATH"));
            Assert.False(info.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
            Assert.False(info.Environment.ContainsKey("COMPlus_JitDisasm"));
        }
    }
#endif
}
