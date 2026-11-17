// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.BuildTasks.UnitTests
{
#if !NETFRAMEWORK
    public sealed class CompileManagedTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SandboxArgumentsBindTypedPathsWithoutChangingLiteralValues(bool visualBasic)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "project space");
            ManagedCompiler compiler = visualBasic
                ? new FormatVbc { NoStandardLib = true, NoVBRuntimeReference = true, DeterministicSourcePaths = true }
                : new FormatCsc { NoStandardLib = true, DeterministicSourcePaths = true };
            compiler.BuildEngine = new MockEngine();
            compiler.TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory);
            compiler.Deterministic = true;
            compiler.NoConfig = true;
            compiler.TargetType = "library";
            compiler.DebugType = "portable";
            compiler.OutputAssembly = new TaskItem("obj/output space/app.dll");
            compiler.Sources = new[] { new TaskItem("nested space/source." + (visualBasic ? "vb" : "cs")) };
            compiler.AnalyzerConfigFiles = new[]
            {
                new TaskItem(".editorconfig"),
                new TaskItem("nested space/.editorconfig"),
                new TaskItem(Path.Combine(directory, "config space/default.globalconfig")),
            };
            var configs = compiler.AnalyzerConfigFiles.Select(item => item.ItemSpec).ToArray();
            var source = Path.Combine(directory, compiler.Sources[0].ItemSpec);
            compiler.Resources = new[]
            {
                new TaskItem("data space/value.txt", new Dictionary<string, string> { ["LogicalName"] = "Stable.Resource" }),
                new TaskItem("data space/literal.txt", new Dictionary<string, string> { ["LogicalName"] = source }),
            };
            Assert.True(compiler.Execute(), ((MockEngine)compiler.BuildEngine).Log);
            var original = visualBasic ? ((FormatVbc)compiler).CompilerArguments : ((FormatCsc)compiler).CompilerArguments;
            if (compiler is FormatVbc vbc)
            {
                vbc.UseSandbox = true;
            }
            else
            {
                ((FormatCsc)compiler).UseSandbox = true;
            }
            Assert.True(compiler.Execute(), ((MockEngine)compiler.BuildEngine).Log);
            var template = visualBasic ? ((FormatVbc)compiler).SandboxArguments : ((FormatCsc)compiler).SandboxArguments;
            var paths = visualBasic ? ((FormatVbc)compiler).ArgumentPaths : ((FormatCsc)compiler).ArgumentPaths;
            var inputs = visualBasic ? ((FormatVbc)compiler).CompilerInputs : ((FormatCsc)compiler).CompilerInputs;
            Assert.Equal(original, visualBasic ? ((FormatVbc)compiler).CompilerArguments : ((FormatCsc)compiler).CompilerArguments);
            Assert.Contains(source, template);
            Assert.Contains("Stable.Resource", template);
            Assert.Contains(paths, item => item.ItemSpec == source && item.GetMetadata("Role") == "Input");
            Assert.Contains(paths, item => item.ItemSpec.EndsWith("app.dll", StringComparison.Ordinal) && item.GetMetadata("Role") == "Output");
            Assert.All(paths, item => Assert.Contains("\"" + item.GetMetadata("Token") + "\"", template));
            foreach (var config in configs)
            {
                var path = Path.Combine(directory, config);
                var binding = Assert.Single(paths, item => item.ItemSpec == path);
                Assert.Equal("Input", binding.GetMetadata("Role"));
                Assert.Contains("/analyzerconfig:\"" + binding.GetMetadata("Token") + "\"", template);
                Assert.Contains(inputs, item => item.ItemSpec == path);
            }
            Assert.Equal(configs, compiler.AnalyzerConfigFiles.Select(item => item.ItemSpec));
            Assert.Equal(source, compiler.Sources[0].ItemSpec);
            if (compiler is FormatVbc sandboxedVbc)
            {
                sandboxedVbc.UseSandbox = false;
            }
            else
            {
                ((FormatCsc)compiler).UseSandbox = false;
            }
            Assert.True(compiler.Execute(), ((MockEngine)compiler.BuildEngine).Log);
            Assert.Equal(original, visualBasic ? ((FormatVbc)compiler).CompilerArguments : ((FormatCsc)compiler).CompilerArguments);
            Assert.Empty(visualBasic ? ((FormatVbc)compiler).SandboxArguments : ((FormatCsc)compiler).SandboxArguments);
            Assert.Empty(visualBasic ? ((FormatVbc)compiler).ArgumentPaths : ((FormatCsc)compiler).ArgumentPaths);
            Assert.False(Directory.Exists(directory));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SandboxPreservesGeneratedTreeLogicalPaths(bool visualBasic)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            ManagedCompiler compiler = visualBasic
                ? new FormatVbc { NoStandardLib = true, NoVBRuntimeReference = true, DeterministicSourcePaths = true, UseSandbox = true }
                : new FormatCsc { NoStandardLib = true, DeterministicSourcePaths = true, UseSandbox = true };
            compiler.BuildEngine = new MockEngine();
            compiler.TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory);
            compiler.NoConfig = true;
            compiler.Deterministic = true;
            compiler.TargetType = "library";
            compiler.OutputAssembly = new TaskItem("obj/app.dll");
            compiler.Sources = new[] { new TaskItem("source." + (visualBasic ? "vb" : "cs")) };
            compiler.GeneratedFilesOutputPath = "obj/generated space";
            Assert.Equal(!visualBasic, compiler.Execute());
            if (!visualBasic)
            {
                var csc = (FormatCsc)compiler;
                var tree = Assert.Single(csc.CompilerOutputTrees);
                Assert.Equal(Path.Combine(directory, "obj/generated space"), tree.ItemSpec);
                Assert.Contains("/generatedfilesout:\"" + tree.ItemSpec + "\"", csc.SandboxArguments);
                Assert.DoesNotContain(csc.ArgumentPaths, item => item.ItemSpec == tree.ItemSpec);
                Assert.Contains(csc.CompilerOutputs, item => item.ItemSpec == tree.GetMetadata("ArchiveFile"));
                Assert.Contains(csc.CompilerOutputs, item => item.ItemSpec == tree.GetMetadata("DyndepFile"));
            }
            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public void GeneratedTreeDyndepRemainsDeclaredButIsFinalizedByHost()
        {
            var tree = new TaskItem("generated");
            tree.SetMetadata("ArchiveFile", "generated.tree.zip");
            tree.SetMetadata("DyndepFile", "generated.tree.dd");
            var task = new CompileManaged
            {
                DeclaredOutputTrees = new[] { tree },
                DeclaredOutputs = new[] { new TaskItem("app.dll"), new TaskItem("generated.tree.zip"), new TaskItem("generated.tree.dd") },
            };
            var method = typeof(CompileManaged).GetMethod("GetToolOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var outputs = (ITaskItem[])method.Invoke(task, null)!;
            Assert.Equal(new[] { "app.dll", "generated.tree.zip" }, outputs.Select(item => item.ItemSpec));
            Assert.Contains(task.DeclaredOutputs, item => item.ItemSpec == "generated.tree.dd");
        }

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
