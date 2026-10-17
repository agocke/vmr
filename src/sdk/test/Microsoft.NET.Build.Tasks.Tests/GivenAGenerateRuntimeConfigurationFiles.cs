// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks.UnitTests
{
    [TestClass]
    public sealed class GivenAWriteRuntimeConfig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "runtime-config-" + Guid.NewGuid().ToString("N"));

        public GivenAWriteRuntimeConfig()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "assets.json"),
                """
                {"version":3,"targets":{"net8.0":{},"net8.0/linux-x64":{}},"libraries":{},"projectFileDependencyGroups":{"net8.0":[]},"packageFolders":{},
                 "project":{"version":"1.0.0","restore":{"projectUniqueName":"App.csproj","projectName":"App","projectPath":"App.csproj","projectStyle":"PackageReference"},
                 "frameworks":{"net8.0":{"targetAlias":"net8.0"}}}}
                """);
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void DeclaredWriterPreservesFrameworksTemplatePrecedenceAndDevOptions(bool selfContained, bool multipleFrameworks)
        {
            File.WriteAllText(Path.Combine(_directory, "template.json"),
                """{ /* supported template syntax */ "configProperties": { "count": 4, "enabled": false, "text": "a+b", }, }""");
            var task = CreateTask(dev: true);
            task.IsSelfContained = selfContained;
            task.RuntimeIdentifier = selfContained ? "linux-x64" : null;
            task.WriteIncludedFrameworks = selfContained;
            if (multipleFrameworks)
            {
                task.RuntimeFrameworks = [Framework("Microsoft.NETCore.App"), Framework("Microsoft.AspNetCore.App"), Framework("Microsoft.AspNetCore.App")];
            }
            task.RollForward = "LatestMinor";
            task.UserRuntimeConfig = "template.json";
            task.DeclaredInputs = [new TaskItem("assets.json"), new TaskItem("template.json")];
            task.HostConfigurationOptions = [new TaskItem("count", new Dictionary<string, string> { ["Value"] = "12" }),
                new TaskItem("enabled", new Dictionary<string, string> { ["Value"] = "true" })];
            File.WriteAllText(Path.Combine(_directory, task.RuntimeConfigPath), "old main config");
            File.WriteAllText(Path.Combine(_directory, task.RuntimeConfigDevPath), "old dev config");

            task.Execute().Should().BeTrue();

            var options = ReadOptions(task.RuntimeConfigPath);
            options["tfm"]!.GetValue<string>().Should().Be("net8.0");
            var frameworks = selfContained ? (JsonArray)options["includedFrameworks"]! :
                multipleFrameworks ? (JsonArray)options["frameworks"]! : new JsonArray(options["framework"]!.DeepClone());
            frameworks.Select(framework => framework!["name"]!.GetValue<string>()).Should().Equal(
                multipleFrameworks ? ["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"] : ["Microsoft.NETCore.App"]);
            options.ContainsKey("rollForward").Should().Be(!selfContained);
            options["configProperties"]!["count"]!.GetValue<int>().Should().Be(12);
            options["configProperties"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
            options["configProperties"]!["text"]!.GetValue<string>().Should().Be("a+b");
            ReadOptions(task.RuntimeConfigDevPath)["configProperties"]!["System.Reflection.Metadata.MetadataUpdater.IsSupported"]!.GetValue<bool>().Should().BeTrue();
            ((MockBuildEngine)task.BuildEngine).Errors.Should().BeEmpty();

            var ordinary = new GenerateRuntimeConfigurationFiles
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = task.TaskEnvironment,
                AssetsFilePath = task.AssetsFilePath,
                TargetFramework = task.TargetFramework,
                TargetFrameworkMoniker = task.TargetFrameworkMoniker,
                RuntimeConfigPath = "ordinary.json",
                RuntimeConfigDevPath = "ordinary.dev.json",
                RuntimeFrameworks = task.RuntimeFrameworks,
                RuntimeIdentifier = task.RuntimeIdentifier,
                IsSelfContained = selfContained,
                WriteIncludedFrameworks = selfContained,
                AlwaysIncludeCoreFramework = true,
                GenerateHotReloadRuntimeOptionsToRuntimeConfigDevFile = true,
                RollForward = task.RollForward,
                UserRuntimeConfig = task.UserRuntimeConfig,
                HostConfigurationOptions = task.HostConfigurationOptions
            };
            ordinary.Execute().Should().BeTrue();
            Compare(task.RuntimeConfigPath, ordinary.RuntimeConfigPath);
            Compare(task.RuntimeConfigDevPath, ordinary.RuntimeConfigDevPath);

            void Compare(string actual, string baseline)
            {
                string content = File.ReadAllText(Path.Combine(_directory, actual));
                content.Should().Be(File.ReadAllText(Path.Combine(_directory, baseline)).Replace("\r\n", "\n"));
                content.Should().NotContain("\r");
                File.ReadAllBytes(Path.Combine(_directory, actual)).Take(3).Should().NotEqual([0xef, 0xbb, 0xbf]);
            }
        }

        [TestMethod]
        public void DeclaredWriterReadsNewTemplateContentsAtUnchangedTimestamp()
        {
            string path = Path.Combine(_directory, "template.json");
            File.WriteAllText(path, """{"setting":"first"}""");
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            var task = CreateTask();
            task.UserRuntimeConfig = "template.json";
            task.DeclaredInputs = [new TaskItem("assets.json"), new TaskItem("template.json")];

            task.Execute().Should().BeTrue();
            ReadOptions(task.RuntimeConfigPath)["setting"]!.GetValue<string>().Should().Be("first");
            File.WriteAllText(path, """{"setting":"changed"}""");
            File.SetLastWriteTimeUtc(path, timestamp);
            task.Execute().Should().BeTrue();

            ReadOptions(task.RuntimeConfigPath)["setting"]!.GetValue<string>().Should().Be("changed");
        }

        [TestMethod]
        public void DeclaredWriterReadsNewAssetsContentsAtUnchangedTimestamp()
        {
            string path = Path.Combine(_directory, "assets.json");
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            var task = CreateTask();
            task.WriteAdditionalProbingPathsToMainConfig = true;
            task.Execute().Should().BeTrue();

            var assets = JsonNode.Parse(File.ReadAllText(path))!;
            assets["packageFolders"] = new JsonObject { ["changed/packages/"] = new JsonObject() };
            File.WriteAllText(path, assets.ToJsonString());
            File.SetLastWriteTimeUtc(path, timestamp);
            task.Execute().Should().BeTrue();

            ReadOptions(task.RuntimeConfigPath)["additionalProbingPaths"]!.AsArray()
                .Select(probe => probe!.GetValue<string>()).Should().Equal("changed/packages");
        }

        [TestMethod]
        [DataRow("main")]
        [DataRow("dev")]
        [DataRow("template")]
        public void DeclaredWriterNormalizesPortableProbesWithoutOpeningTheirDirectories(string source)
        {
            var task = CreateTask(dev: source == "dev");
            task.AdditionalProbingPaths = [new TaskItem(@"missing\packages\")];
            task.WriteAdditionalProbingPathsToMainConfig = source == "main";
            task.GenerateProbingPathsToRuntimeConfigDevFile = source == "dev";
            if (source == "template")
            {
                File.WriteAllText(Path.Combine(_directory, "template.json"), """{"additionalProbingPaths":["missing\\packages\\"]}""");
                task.UserRuntimeConfig = "template.json";
                task.DeclaredInputs = [new TaskItem("assets.json"), new TaskItem("template.json")];
            }

            task.Execute().Should().BeTrue();

            var probes = ReadOptions(source == "dev" ? task.RuntimeConfigDevPath : task.RuntimeConfigPath)["additionalProbingPaths"]!;
            probes.AsArray().Select(probe => probe!.GetValue<string>()).Should().Equal("missing/packages");
            Directory.Exists(Path.Combine(_directory, "missing")).Should().BeFalse();
        }

        [TestMethod]
        [DataRow("undeclared-assets")]
        [DataRow("undeclared-template")]
        [DataRow("extra-output")]
        [DataRow("missing-dev-path")]
        [DataRow("duplicate-output")]
        [DataRow("roll-forward")]
        [DataRow("template-probe")]
        [DataRow("package-probe")]
        [DataRow("windows-probe")]
        public void InvalidDeclaredInputsAndOptionsLeaveBothOutputsUntouched(string invalid)
        {
            var task = CreateTask(dev: true);
            File.WriteAllText(Path.Combine(_directory, task.RuntimeConfigPath), "old main");
            File.WriteAllText(Path.Combine(_directory, task.RuntimeConfigDevPath), "old dev");
            if (invalid == "undeclared-assets")
            {
                task.DeclaredInputs = [];
            }
            else if (invalid == "undeclared-template")
            {
                task.UserRuntimeConfig = "undeclared.json";
            }
            else if (invalid == "extra-output")
            {
                task.DeclaredOutputs = [.. task.DeclaredOutputs, new TaskItem("extra.json")];
            }
            else if (invalid == "missing-dev-path")
            {
                task.RuntimeConfigDevPath = "";
            }
            else if (invalid == "duplicate-output")
            {
                task.RuntimeConfigDevPath = task.RuntimeConfigPath;
            }
            else if (invalid == "roll-forward")
            {
                task.RollForward = "Invalid";
            }
            else if (invalid == "template-probe")
            {
                File.WriteAllText(Path.Combine(_directory, "template.json"), """{"additionalProbingPaths":["/machine/packages"]}""");
                task.UserRuntimeConfig = "template.json";
                task.DeclaredInputs = [new TaskItem("assets.json"), new TaskItem("template.json")];
            }
            else if (invalid == "package-probe")
            {
                var assets = JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, "assets.json")))!;
                assets["packageFolders"] = new JsonObject { [_directory] = new JsonObject() };
                File.WriteAllText(Path.Combine(_directory, "assets.json"), assets.ToJsonString());
                task.GenerateProbingPathsToRuntimeConfigDevFile = true;
            }
            else
            {
                task.AdditionalProbingPaths = [new TaskItem(@"C:\machine\packages")];
                task.GenerateProbingPathsToRuntimeConfigDevFile = true;
            }

            task.Execute().Should().BeFalse();

            ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle();
            File.ReadAllText(Path.Combine(_directory, "app.runtimeconfig.json")).Should().Be("old main");
            File.ReadAllText(Path.Combine(_directory, "app.runtimeconfig.dev.json")).Should().Be("old dev");
        }

        [TestMethod]
        [DataRow("missing")]
        [DataRow("malformed")]
        [DataRow("null")]
        public void ExplicitTemplatesMustExistAndContainAnObject(string invalid)
        {
            var task = CreateTask(dev: true);
            task.UserRuntimeConfig = "template.json";
            task.DeclaredInputs = [new TaskItem("assets.json"), new TaskItem("template.json")];
            if (invalid != "missing")
            {
                File.WriteAllText(Path.Combine(_directory, "template.json"), invalid == "null" ? "null" : "not JSON");
            }

            if (invalid == "missing")
            {
                Assert.ThrowsExactly<FileNotFoundException>(() => task.Execute());
            }
            else
            {
                Assert.Throws<JsonException>(() => task.Execute());
            }

            File.Exists(Path.Combine(_directory, task.RuntimeConfigPath)).Should().BeFalse();
            File.Exists(Path.Combine(_directory, task.RuntimeConfigDevPath)).Should().BeFalse();
        }

        private WriteRuntimeConfig CreateTask(bool dev = false) => new()
        {
            BuildEngine = new MockBuildEngine(),
            TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_directory),
            AssetsFilePath = "assets.json",
            TargetFramework = "net8.0",
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0",
            RuntimeConfigPath = "app.runtimeconfig.json",
            RuntimeConfigDevPath = "app.runtimeconfig.dev.json",
            RuntimeFrameworks = [Framework("Microsoft.NETCore.App")],
            AlwaysIncludeCoreFramework = true,
            GenerateHotReloadRuntimeOptionsToRuntimeConfigDevFile = dev,
            DeclaredInputs = [new TaskItem("assets.json")],
            DeclaredOutputs = dev
                ? [new TaskItem("app.runtimeconfig.json"), new TaskItem("app.runtimeconfig.dev.json")]
                : [new TaskItem("app.runtimeconfig.json")]
        };

        private static ITaskItem Framework(string name) =>
            new TaskItem(name, new Dictionary<string, string> { ["FrameworkName"] = name, ["Version"] = "8.0.0" });

        private JsonObject ReadOptions(string path) =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, path)))!["runtimeOptions"]!.AsObject();

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    [TestClass]
    public class GivenAGenerateRuntimeConfigurationFiles
    {
        private readonly string _runtimeConfigPath;
        private readonly string _runtimeConfigDevPath;

        public GivenAGenerateRuntimeConfigurationFiles()
        {
            string testTempDir = Path.Combine(Path.GetTempPath(), "dotnetSdkTests");
            Directory.CreateDirectory(testTempDir);
            _runtimeConfigPath =
                Path.Combine(testTempDir, nameof(ItCanGenerateWithoutAssetFile) + "runtimeconfig.json");
            _runtimeConfigDevPath =
                Path.Combine(testTempDir, nameof(ItCanGenerateWithoutAssetFile) + "runtimeconfig.dev.json");
            if (File.Exists(_runtimeConfigPath))
            {
                File.Delete(_runtimeConfigPath);
            }

            if (File.Exists(_runtimeConfigDevPath))
            {
                File.Delete(_runtimeConfigDevPath);
            }
        }

        [TestMethod]
        public void ItCanGenerateWithoutAssetFile()
        {
            var task = new TestableGenerateRuntimeConfigurationFiles
            {
                BuildEngine = new MockNeverCacheBuildEngine4(),
                TargetFrameworkMoniker = $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}",
                RuntimeConfigPath = _runtimeConfigPath,
                RuntimeConfigDevPath = _runtimeConfigDevPath,
                RuntimeFrameworks = new[]
                {
                    new MockTaskItem(
                        "Microsoft.NETCore.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.NETCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    )
                },
                RollForward = "LatestMinor"
            };

            Action a = () => task.PublicExecuteCore();
            a.Should().NotThrow();

            File.ReadAllText(_runtimeConfigPath).Should()
                .Be(
                    $@"{{
  ""runtimeOptions"": {{
    ""tfm"": ""{ToolsetInfo.CurrentTargetFramework}"",
    ""rollForward"": ""LatestMinor"",
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
    }}
  }}
}}");
            File.Exists(_runtimeConfigDevPath).Should().BeFalse("No nuget involved, so no extra probing path");
        }

        [TestMethod]
        public void Given3RuntimeFrameworksItCanGenerateWithoutAssetFile()
        {
            var task = new TestableGenerateRuntimeConfigurationFiles
            {
                BuildEngine = new MockNeverCacheBuildEngine4(),
                TargetFrameworkMoniker = $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}",
                RuntimeConfigPath = _runtimeConfigPath,
                RuntimeConfigDevPath = _runtimeConfigDevPath,
                RuntimeFrameworks = new[]
                {
                    new MockTaskItem(
                        "Microsoft.NETCore.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.NETCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    ),
                    new MockTaskItem(
                        "Microsoft.WindowsDesktop.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.WindowsDesktop.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    ),
                    new MockTaskItem(
                        "Microsoft.AspNetCore.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.AspNetCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    )
                },
                RollForward = "LatestMinor"
            };

            Action a = () => task.PublicExecuteCore();
            a.Should().NotThrow();

            File.ReadAllText(_runtimeConfigPath).Should()
                .Be(
                    $@"{{
  ""runtimeOptions"": {{
    ""tfm"": ""{ToolsetInfo.CurrentTargetFramework}"",
    ""rollForward"": ""LatestMinor"",
    ""frameworks"": [
      {{
        ""name"": ""Microsoft.WindowsDesktop.App"",
        ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
      }},
      {{
        ""name"": ""Microsoft.AspNetCore.App"",
        ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
      }}
    ]
  }}
}}",
                    "There is no Microsoft.NETCore.App. And it is under frameworkS.");
        }

        [TestMethod]
        public void Given2RuntimeFrameworksItCanGenerateWithoutAssetFile()
        {
            var task = new TestableGenerateRuntimeConfigurationFiles
            {
                BuildEngine = new MockNeverCacheBuildEngine4(),
                TargetFrameworkMoniker = $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}",
                RuntimeConfigPath = _runtimeConfigPath,
                RuntimeConfigDevPath = _runtimeConfigDevPath,
                RuntimeFrameworks = new[]
                {
                    new MockTaskItem(
                        "Microsoft.NETCore.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.NETCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    ),
                    new MockTaskItem(
                        "Microsoft.WindowsDesktop.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.WindowsDesktop.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    )
                },
                RollForward = "LatestMinor"
            };

            Action a = () => task.PublicExecuteCore();
            a.Should().NotThrow();

            File.ReadAllText(_runtimeConfigPath).Should()
                .Be(
                    $@"{{
  ""runtimeOptions"": {{
    ""tfm"": ""{ToolsetInfo.CurrentTargetFramework}"",
    ""rollForward"": ""LatestMinor"",
    ""framework"": {{
      ""name"": ""Microsoft.WindowsDesktop.App"",
      ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
    }}
  }}
}}",
                    "There is no Microsoft.NETCore.App.");
        }

        [TestMethod]
        public void GivenTargetMonikerItGeneratesShortName()
        {
            var task = new TestableGenerateRuntimeConfigurationFiles
            {
                BuildEngine = new MockNeverCacheBuildEngine4(),
                TargetFrameworkMoniker = $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}",
                RuntimeConfigPath = _runtimeConfigPath,
                RuntimeConfigDevPath = _runtimeConfigDevPath,
                RuntimeFrameworks = new[]
                {
                    new MockTaskItem(
                        "Microsoft.NETCore.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.NETCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    )
                },
                RollForward = "LatestMinor"
            };

            Action a = () => task.PublicExecuteCore();
            a.Should().NotThrow();

            File.ReadAllText(_runtimeConfigPath).Should()
                .Be(
                    $@"{{
  ""runtimeOptions"": {{
    ""tfm"": ""{ToolsetInfo.CurrentTargetFramework}"",
    ""rollForward"": ""LatestMinor"",
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
    }}
  }}
}}");
        }

        [TestMethod]
        public void GivenHostConfigurationOptionsItWritesConfigProperties()
        {
            var task = new TestableGenerateRuntimeConfigurationFiles
            {
                BuildEngine = new MockNeverCacheBuildEngine4(),
                TargetFrameworkMoniker = $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}",
                RuntimeConfigPath = _runtimeConfigPath,
                RuntimeConfigDevPath = _runtimeConfigDevPath,
                RuntimeFrameworks = new[]
                {
                    new MockTaskItem(
                        "Microsoft.NETCore.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.NETCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    )
                },
                RollForward = "LatestMinor",
                HostConfigurationOptions = new[]
                {
                    new MockTaskItem("System.GC.Server", new Dictionary<string, string> { {"Value", "true"} }),
                    new MockTaskItem("MaxThreads", new Dictionary<string, string> { {"Value", "42"} }),
                    new MockTaskItem("SomeText", new Dictionary<string, string> { {"Value", "hello"} })
                }
            };

            Action a = () => task.PublicExecuteCore();
            a.Should().NotThrow();

            // configProperties is the [JsonExtensionData] bag, written after the declared
            // properties. Bool/int/string host option values keep their JSON types
            // (true, 42, "hello"), matching the previous reflection-based serializer.
            File.ReadAllText(_runtimeConfigPath).Should()
                .Be(
                    $@"{{
  ""runtimeOptions"": {{
    ""tfm"": ""{ToolsetInfo.CurrentTargetFramework}"",
    ""rollForward"": ""LatestMinor"",
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
    }},
    ""configProperties"": {{
      ""System.GC.Server"": true,
      ""MaxThreads"": 42,
      ""SomeText"": ""hello""
    }}
  }}
}}");
        }

        [TestMethod]
        public void GivenAdditionalProbingPathsItWritesThemToMainConfig()
        {
            var task = new TestableGenerateRuntimeConfigurationFiles
            {
                BuildEngine = new MockNeverCacheBuildEngine4(),
                TargetFrameworkMoniker = $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}",
                RuntimeConfigPath = _runtimeConfigPath,
                RuntimeConfigDevPath = _runtimeConfigDevPath,
                RuntimeFrameworks = new[]
                {
                    new MockTaskItem(
                        "Microsoft.NETCore.App",
                        new Dictionary<string, string>
                        {
                            {"FrameworkName", "Microsoft.NETCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                        }
                    )
                },
                RollForward = "LatestMinor",
                WriteAdditionalProbingPathsToMainConfig = true,
                AdditionalProbingPaths = new[]
                {
                    new MockTaskItem("/probing/path1", new Dictionary<string, string>()),
                    new MockTaskItem("/probing/path2", new Dictionary<string, string>())
                }
            };

            Action a = () => task.PublicExecuteCore();
            a.Should().NotThrow();

            File.ReadAllText(_runtimeConfigPath).Should()
                .Be(
                    $@"{{
  ""runtimeOptions"": {{
    ""tfm"": ""{ToolsetInfo.CurrentTargetFramework}"",
    ""rollForward"": ""LatestMinor"",
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
    }},
    ""additionalProbingPaths"": [
      ""/probing/path1"",
      ""/probing/path2""
    ]
  }}
}}");
        }

        [TestMethod]
        public void GivenUserRuntimeConfigItMergesExtensionData()
        {
            var userConfigPath = Path.Combine(
                Path.GetTempPath(), "dotnetSdkTests", nameof(GivenUserRuntimeConfigItMergesExtensionData) + ".template.json");
            File.WriteAllText(
                userConfigPath,
                "{\"configProperties\":{\"TestProperty\":true},\"customSection\":{\"value\":42}}");
            try
            {
                var task = new TestableGenerateRuntimeConfigurationFiles
                {
                    BuildEngine = new MockNeverCacheBuildEngine4(),
                    TargetFrameworkMoniker = $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}",
                    RuntimeConfigPath = _runtimeConfigPath,
                    RuntimeConfigDevPath = _runtimeConfigDevPath,
                    RuntimeFrameworks = new[]
                    {
                        new MockTaskItem(
                            "Microsoft.NETCore.App",
                            new Dictionary<string, string>
                            {
                                {"FrameworkName", "Microsoft.NETCore.App"}, {"Version", $"{ToolsetInfo.CurrentTargetFrameworkVersion}.0"}
                            }
                        )
                    },
                    RollForward = "LatestMinor",
                    UserRuntimeConfig = userConfigPath
                };

                Action a = () => task.PublicExecuteCore();
                a.Should().NotThrow();

                // The user template's properties flow through [JsonExtensionData] and are written
                // after the declared properties, preserving nested objects and value types exactly.
                File.ReadAllText(_runtimeConfigPath).Should()
                    .Be(
                        $@"{{
  ""runtimeOptions"": {{
    ""tfm"": ""{ToolsetInfo.CurrentTargetFramework}"",
    ""rollForward"": ""LatestMinor"",
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{ToolsetInfo.CurrentTargetFrameworkVersion}.0""
    }},
    ""configProperties"": {{
      ""TestProperty"": true
    }},
    ""customSection"": {{
      ""value"": 42
    }}
  }}
}}");
            }
            finally
            {
                if (File.Exists(userConfigPath))
                {
                    File.Delete(userConfigPath);
                }
            }
        }

        private class TestableGenerateRuntimeConfigurationFiles : GenerateRuntimeConfigurationFiles
        {
            public TestableGenerateRuntimeConfigurationFiles()
            {
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest();
            }

            public void PublicExecuteCore()
            {
                base.ExecuteCore();
            }
        }
    }
}
