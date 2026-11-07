// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.CommandLine;
using Xunit;

namespace Microsoft.CodeAnalysis.BuildTasks.UnitTests
{
#if !NETFRAMEWORK
    public sealed class ResolveCompilerDependenciesTests
    {
        [Fact]
        public void ResolveCompilerDependenciesSelectsRecursiveCandidatesWithoutExistingDirectories()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var projectDirectory = Path.Combine(directory, "project");
            var sdkDirectory = Path.Combine(directory, "sdk", "analyzers", "cs");
            var task = new ResolveCompilerDependencies
            {
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(projectDirectory),
                Analyzers =
                [
                    new TaskItem("../packages/example/analyzers/dotnet/cs/Analyzer.dll"),
                    new TaskItem(Path.Combine(sdkDirectory, "SdkAnalyzer.dll")),
                ],
                CandidateFiles =
                [
                    new TaskItem("../packages/example/analyzers/dotnet/cs/Analyzer.dll"),
                    new TaskItem("../packages/example/analyzers/dotnet/cs/Dependency.dll"),
                    new TaskItem("../packages/example/analyzers/dotnet/cs/resources/deep/Analyzer.resources.dll"),
                    new TaskItem(Path.Combine(sdkDirectory, "helpers", "SdkDependency.dll")),
                ],
            };

            Assert.True(task.Execute());
            Assert.Equal(
                new[]
                {
                    Path.Combine(directory, "packages", "example", "analyzers", "dotnet", "cs", "Analyzer.dll"),
                    Path.Combine(directory, "packages", "example", "analyzers", "dotnet", "cs", "Dependency.dll"),
                    Path.Combine(directory, "packages", "example", "analyzers", "dotnet", "cs", "resources", "deep", "Analyzer.resources.dll"),
                    Path.Combine(sdkDirectory, "helpers", "SdkDependency.dll"),
                },
                task.Dependencies.Select(item => item.ItemSpec));
            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public void ResolveCompilerDependenciesExcludesInactiveAndPrefixLookalikeDirectories()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var task = new ResolveCompilerDependencies
            {
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory),
                Analyzers = [new TaskItem("packages/example/analyzers/dotnet/roslyn4.0/cs/Analyzer.dll")],
                CandidateFiles =
                [
                    new TaskItem("packages/example/analyzers/dotnet/roslyn4.0/cs/Dependency.dll"),
                    new TaskItem("packages/example/analyzers/dotnet/roslyn4.0/vb/Inactive.dll"),
                    new TaskItem("packages/example/analyzers/dotnet/roslyn4.0/csharp/Lookalike.dll"),
                    new TaskItem("packages/example/analyzers/dotnet/roslyn4.0/cs-extra/Lookalike.dll"),
                    new TaskItem("packages/example/analyzers/dotnet/roslyn4.0/Shared.dll"),
                    new TaskItem("packages/example/analyzers/dotnet/roslyn3.0/cs/Inactive.dll"),
                    new TaskItem("packages/example/analyzers/dotnet/roslyn5.0/cs/Inactive.dll"),
                    new TaskItem("packages/other/analyzers/dotnet/roslyn4.0/cs/Inactive.dll"),
                    new TaskItem("packages/example/analyzers/dotnet/roslyn4.0/cs/../vb/NormalizedSibling.dll"),
                ],
            };

            Assert.True(task.Execute());
            Assert.Equal(
                new[] { Path.Combine(directory, "packages", "example", "analyzers", "dotnet", "roslyn4.0", "cs", "Dependency.dll") },
                task.Dependencies.Select(item => item.ItemSpec));
        }

        [Fact]
        public void ResolveCompilerDependenciesUsesFinalFutureAnalyzersAndStablePathDeduplication()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var dependency = Path.Combine(directory, "obj", "future", "Dependency.dll");
            var differentlyCasedDependency = Path.Combine(directory, "obj", "future", "dependency.dll");
            var analyzer = Path.Combine(directory, "obj", "future", "Generator.dll");
            var nestedDependency = Path.Combine(directory, "obj", "future", "helpers", "Companion.dll");
            var task = new ResolveCompilerDependencies
            {
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory),
                CandidateFiles =
                [
                    new TaskItem("obj/future/Dependency.dll"),
                    new TaskItem(dependency),
                    new TaskItem("obj/future/helpers/Companion.dll"),
                    new TaskItem("obj/future/./Dependency.dll"),
                    new TaskItem("obj/future/dependency.dll"),
                    new TaskItem("obj/future/Generator.dll"),
                    new TaskItem("obj/inactive/Dependency.dll"),
                ],
            };

            Assert.True(task.Execute());
            Assert.Empty(task.Dependencies);

            // A producer can supply the final analyzer after candidates are collected, before either file exists.
            task.Analyzers = [new TaskItem(analyzer), new TaskItem("obj/future/./Generator.dll")];
            Assert.True(task.Execute());
            Assert.Equal(
                Path.DirectorySeparatorChar == '\\'
                    ? new[] { dependency, nestedDependency, analyzer }
                    : new[] { dependency, nestedDependency, differentlyCasedDependency, analyzer },
                task.Dependencies.Select(item => item.ItemSpec));
            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public void ResolveCompilerDependenciesHandlesAnalyzersAtFilesystemRoot()
        {
            var root = Path.GetPathRoot(Path.GetTempPath())!;
            var dependency = Path.Combine(root, "Dependency.dll");
            var nestedDependency = Path.Combine(root, "helpers", "Companion.dll");
            var task = new ResolveCompilerDependencies
            {
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(root),
                Analyzers = [new TaskItem(Path.Combine(root, "Analyzer.dll"))],
                CandidateFiles = [new TaskItem(dependency), new TaskItem(nestedDependency)],
            };

            Assert.True(task.Execute());
            Assert.Equal(new[] { dependency, nestedDependency }, task.Dependencies.Select(item => item.ItemSpec));
        }

        [Fact]
        public void ResolveCompilerDependenciesPreservesEscapedSemicolonAndPercentPaths()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "project;100%");
            var dependency = Path.Combine(directory, "analyzers;100%", "Dependency;%3B.dll");
            var task = new ResolveCompilerDependencies
            {
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory),
                Analyzers = [new TaskItem("analyzers%3B100%25/Analyzer.dll")],
                CandidateFiles =
                [
                    new TaskItem("analyzers%3B100%25/Dependency%3B%253B.dll"),
                    new TaskItem("analyzers%3B100%25-other/Inactive.dll"),
                ],
            };

            Assert.True(task.Execute());
            var item = Assert.Single(task.Dependencies);
            Assert.Equal(dependency, item.ItemSpec);
            Assert.Equal(dependency.Replace("%", "%25").Replace(";", "%3B"), ((ITaskItem2)item).EvaluatedIncludeEscaped);
            Assert.False(Directory.Exists(directory));
        }
    }
#endif
}
