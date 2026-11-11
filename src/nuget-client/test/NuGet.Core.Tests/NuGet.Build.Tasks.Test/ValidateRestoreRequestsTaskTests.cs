// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Commands;
using NuGet.Versioning;
using Xunit;

namespace NuGet.Build.Tasks.Test
{
    public class ValidateRestoreRequestsTaskTests
    {
        private const string IdentityMetadata = "_MSBuildRestoreRequestId";

        [Fact]
        public void Execute_SingleRequest_RetainsCompleteGraph()
        {
            ITaskItem[] graph = CreateGraph("first");
            var task = new ValidateRestoreRequestsTask
            {
                BuildEngine = new TestBuildEngine(),
                RestoreGraphItems = graph
            };

            Assert.True(task.Execute());
            Assert.Equal(graph, task.ValidatedGraphItems);
            AssertPackageRequirements(task.ValidatedGraphItems);
        }

        [Fact]
        public void Execute_EquivalentInterleavedRequests_KeepsOneCompleteSpecAndAllRestoreMarkers()
        {
            ITaskItem[] first = CreateGraph("first");
            ITaskItem[] second = CreateGraph("second");
            ITaskItem[] interleaved = first.Zip(second, (left, right) => new[] { left, right })
                .SelectMany(pair => pair).ToArray();
            var task = new ValidateRestoreRequestsTask
            {
                BuildEngine = new TestBuildEngine(),
                RestoreGraphItems = interleaved
            };

            Assert.True(task.Execute());
            Assert.Equal(new[] { first[0], second[0] }.Concat(first.Skip(1)), task.ValidatedGraphItems);
            AssertPackageRequirements(task.ValidatedGraphItems);
            Assert.DoesNotContain(task.ValidatedGraphItems,
                item => item.GetMetadata(IdentityMetadata) == "second" && item.GetMetadata("Type") != "RestoreSpec");
        }

        [Theory]
        [InlineData("Dependency", "VersionRange", "[2.0.0]")]
        [InlineData("ProjectSpec", "OutputPath", "other-obj")]
        [InlineData("ProjectSpec", "ProjectName", "OtherName")]
        [InlineData("ProjectSpec", "ProjectPath", "other.csproj")]
        public void Execute_IncompatibleRequests_FailsWithoutPublishingGraph(string type, string metadata, string value)
        {
            ITaskItem[] first = CreateGraph("first");
            ITaskItem[] second = CreateGraph("second");
            ITaskItem changed = second.Single(item => item.GetMetadata("Type") == type);
            changed.SetMetadata(metadata,
                metadata == "OutputPath" || metadata == "ProjectPath" ? Path.GetFullPath(value) : value);
            var engine = new TestBuildEngine();
            var task = new ValidateRestoreRequestsTask
            {
                BuildEngine = engine,
                RestoreGraphItems = first.Concat(second).ToArray()
            };

            Assert.False(task.Execute());
            Assert.Empty(task.ValidatedGraphItems);
            string error = Assert.Single(engine.TestLogger.ErrorMessages);
            Assert.Contains(first[1].GetMetadata("ProjectUniqueName"), error);
        }

        [Theory]
        [InlineData("Dependency")]
        [InlineData("RestoreSpec")]
        public void Execute_MissingIdentityOnAnyRecord_FailsWithoutPublishingGraph(string type)
        {
            // TaskItem returns an empty string for absent metadata; MockTaskItem returns null.
            ITaskItem[] graph = CreateGraph("first").Select(item => (ITaskItem)new TaskItem(item)).ToArray();
            graph.Single(item => item.GetMetadata("Type") == type).RemoveMetadata(IdentityMetadata);
            var engine = new TestBuildEngine();
            var task = new ValidateRestoreRequestsTask
            {
                BuildEngine = engine,
                RestoreGraphItems = graph
            };

            Assert.False(task.Execute());
            Assert.Empty(task.ValidatedGraphItems);
            Assert.Single(engine.TestLogger.ErrorMessages);
        }

        private static ITaskItem[] CreateGraph(string identity)
        {
            string path = Path.GetFullPath("library.csproj");
            return new ITaskItem[]
            {
                Item("RestoreSpec"),
                Item("ProjectSpec",
                    ("ProjectName", "Library"),
                    ("ProjectPath", path),
                    ("ProjectStyle", "PackageReference"),
                    ("Version", "1.0.0"),
                    ("OutputPath", Path.GetFullPath("obj")),
                    ("TargetFrameworks", "net8.0;net9.0"),
                    ("CrossTargeting", "true")),
                Item("TargetFrameworkInformation",
                    ("TargetFramework", "net8.0"),
                    ("TargetFrameworkMoniker", ".NETCoreApp,Version=v8.0")),
                Item("TargetFrameworkInformation",
                    ("TargetFramework", "net9.0"),
                    ("TargetFrameworkMoniker", ".NETCoreApp,Version=v9.0")),
                Item("Dependency",
                    ("Id", "Example.Package"),
                    ("VersionRange", "[1.0.0]"),
                    ("TargetFrameworks", "net8.0;net9.0"))
            };

            MockTaskItem Item(string type, params (string Name, string Value)[] metadata)
            {
                var item = new MockTaskItem(identity + "-" + type)
                {
                    ["Type"] = type,
                    ["ProjectUniqueName"] = path,
                    [IdentityMetadata] = identity
                };
                foreach ((string name, string value) in metadata)
                {
                    item.SetMetadata(name, value);
                }

                return item;
            }
        }

        private static void AssertPackageRequirements(ITaskItem[] graph)
        {
            var specification = MSBuildRestoreUtility.GetPackageSpec(graph.Select(item => new MSBuildTaskItem(item)));
            Assert.Equal("Library", specification.Name);
            Assert.Equal(Path.GetFullPath("library.csproj"), specification.FilePath);
            Assert.Equal(Path.GetFullPath("obj"), specification.RestoreMetadata.OutputPath);
            Assert.Equal(new[] { "net8.0", "net9.0" },
                specification.TargetFrameworks.Select(framework => framework.TargetAlias));
            Assert.All(specification.TargetFrameworks, framework =>
            {
                var dependency = Assert.Single(framework.Dependencies);
                Assert.Equal("Example.Package", dependency.Name);
                Assert.Equal(VersionRange.Parse("[1.0.0]"), dependency.LibraryRange.VersionRange);
            });
        }
    }
}
