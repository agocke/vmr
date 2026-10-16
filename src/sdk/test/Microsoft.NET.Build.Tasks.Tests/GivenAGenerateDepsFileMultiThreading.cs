// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Runtime.CompilerServices;
using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.NET.Build.Tasks.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class GivenAGenerateDepsFilePathResolution : IDisposable
    {
        private readonly List<string> _tempDirs = new();
        private readonly string _originalCwd = Directory.GetCurrentDirectory();

        [TestMethod]
        public void DeclaredWriterUsesExplicitContextAndMetadataWithoutOpeningAssemblies()
        {
            string directory = CreateTestProjectDirectory();
            CreateAssetsFile(directory, "assets.json");
            var task = CreateDeclaredTask(directory);
            task.TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory,
                new Dictionary<string, string> { ["DOTNET_REFERENCE_ASSEMBLIES_PATH"] = Path.Combine(directory, "decoy") });
            task.CompilerOptions = new TaskItem("CompilerOptions");
            task.ReferenceAssembliesPath = Path.Combine(directory, "framework");
            task.ReferenceAssemblies = [new TaskItem(Path.Combine(directory, "framework", "v4.8", "Framework.dll"))];
            task.AssemblySatelliteAssemblies =
            [
                new MockTaskItem("missing.resources.dll", new Dictionary<string, string>
                {
                    ["Culture"] = "de",
                    ["TargetPath"] = "de/TestApp.resources.dll"
                })
            ];
            File.WriteAllText(Path.Combine(directory, "result.deps.json"), "obsolete content that must not survive");

            task.Execute().Should().BeTrue();

            var json = JObject.Parse(File.ReadAllText(Path.Combine(directory, "result.deps.json")));
            var target = (JObject)json["targets"][".NETCoreApp,Version=v8.0"];
            target["Framework/0.0.0.0"]["compile"]["v4.8/Framework.dll"].Should().NotBeNull();
            target["TestApp/1.0.0.0"]["resources"]["de/TestApp.resources.dll"]["locale"].Value<string>().Should().Be("de");
            Directory.Exists(Path.Combine(directory, "framework")).Should().BeFalse();
            ((BasicBuildEngine)task.BuildEngine).Errors.Should().BeEmpty();
        }

        [TestMethod]
        public void DeclaredWriterReadsChangedBinaryContentsWithUnchangedTimestamp()
        {
            string directory = CreateTestProjectDirectory();
            CreateAssetsFile(directory, "assets.json");
            string assemblyPath = Path.Combine(directory, "Dependency.dll");
            EmitAssembly(assemblyPath, new Version(1, 2, 3, 4), "5.6.7.8");
            DateTime timestamp = File.GetLastWriteTimeUtc(assemblyPath);
            var task = CreateDeclaredTask(directory);
            task.IncludeRuntimeFileVersions = true;
            task.ReferencePaths = task.UserRuntimeAssemblies = [new TaskItem(assemblyPath)];
            task.DeclaredInputs = [new TaskItem("assets.json"), new TaskItem(assemblyPath)];

            task.Execute().Should().BeTrue();
            RuntimeVersions().Should().Be(("1.2.3.4", "5.6.7.8"));

            EmitAssembly(assemblyPath, new Version(9, 10, 11, 12), "13.14.15.16");
            File.SetLastWriteTimeUtc(assemblyPath, timestamp);
            task.Execute().Should().BeTrue();
            RuntimeVersions().Should().Be(("9.10.11.12", "13.14.15.16"));

            (string, string) RuntimeVersions()
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(directory, "result.deps.json")));
                var file = json["targets"][".NETCoreApp,Version=v8.0"]["Dependency/0.0.0.0"]["runtime"]["Dependency.dll"];
                return (file["assemblyVersion"].Value<string>(), file["fileVersion"].Value<string>());
            }
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void DeclaredProjectReferencesUseExplicitOutputsInsteadOfProjectNamesOrExistenceFallback(bool versions)
        {
            string directory = CreateTestProjectDirectory();
            string assets = CreateAssetsFile(directory, "assets.json");
            var json = JObject.Parse(File.ReadAllText(assets));
            json["targets"]["net8.0"]["Logical.Project/2.0.0"] = new JObject { ["type"] = "project" };
            json["libraries"]["Logical.Project/2.0.0"] = new JObject
            {
                ["type"] = "project",
                ["path"] = "Logical.Project.csproj",
                ["msbuildProject"] = "Logical.Project.csproj"
            };
            ((JArray)json["projectFileDependencyGroups"]["net8.0"]).Add("Logical.Project >= 2.0.0");
            File.WriteAllText(assets, json.ToString());
            string assemblyPath = Path.Combine(directory, "Different.Assembly.dll");
            if (versions)
            {
                EmitAssembly(assemblyPath, new Version(3, 4, 5, 6), "7.8.9.10");
            }

            var task = CreateDeclaredTask(directory);
            task.IncludeRuntimeFileVersions = versions;
            task.ReferencePaths = task.UserRuntimeAssemblies =
            [
                new MockTaskItem(assemblyPath, new Dictionary<string, string>
                {
                    ["ReferenceSourceTarget"] = "ProjectReference",
                    ["MSBuildSourceProjectFile"] = Path.Combine(directory, "Logical.Project.csproj"),
                    ["DestinationSubDirectory"] = "plugins/"
                })
            ];
            task.DeclaredInputs = versions
                ? [new TaskItem("assets.json"), new TaskItem(assemblyPath)]
                : [new TaskItem("assets.json")];

            task.Execute().Should().BeTrue();

            var result = JObject.Parse(File.ReadAllText(Path.Combine(directory, "result.deps.json")));
            var file = result["targets"][".NETCoreApp,Version=v8.0"]["Logical.Project/2.0.0"]["runtime"]["Different.Assembly.dll"];
            file["localPath"].Value<string>().Should().Be("plugins/Different.Assembly.dll");
            if (versions)
            {
                file["assemblyVersion"].Value<string>().Should().Be("3.4.5.6");
                file["fileVersion"].Value<string>().Should().Be("7.8.9.10");
                File.Delete(assemblyPath);
                Action execute = () => task.Execute();
                execute.Should().Throw<FileNotFoundException>();
            }
            else
            {
                ((JObject)file).Properties().Select(property => property.Name).Should().Equal("localPath");
            }
        }

        [TestMethod]
        [DataRow("undeclared")]
        [DataRow("undeclared-binary")]
        [DataRow("output")]
        [DataRow("absolute-key")]
        [DataRow("absolute-resource")]
        [DataRow("malformed-assets")]
        public void DeclaredWriterFailsWithoutPublishingForInvalidInputs(string invalidInput)
        {
            string directory = CreateTestProjectDirectory();
            CreateAssetsFile(directory, "assets.json");
            var task = CreateDeclaredTask(directory);
            if (invalidInput == "undeclared")
            {
                task.DeclaredInputs = [];
            }
            else if (invalidInput == "undeclared-binary")
            {
                task.IncludeRuntimeFileVersions = true;
                task.ReferencePaths = task.UserRuntimeAssemblies = [new TaskItem("missing.dll")];
            }
            else if (invalidInput == "output")
            {
                task.DeclaredOutputs = [new TaskItem("different.deps.json")];
            }
            else if (invalidInput == "absolute-key")
            {
                task.CompilerOptions = new MockTaskItem("CompilerOptions", new Dictionary<string, string>
                {
                    ["AssemblyOriginatorKeyFile"] = Path.Combine(directory, "key.snk")
                });
            }
            else if (invalidInput == "absolute-resource")
            {
                task.AssemblySatelliteAssemblies =
                [
                    new MockTaskItem("unused.resources.dll", new Dictionary<string, string>
                    {
                        ["Culture"] = "de",
                        ["TargetPath"] = Path.Combine(directory, "physical.resources.dll")
                    })
                ];
            }
            else
            {
                File.WriteAllText(Path.Combine(directory, "assets.json"), "not JSON");
            }

            task.Execute().Should().BeFalse();

            ((BasicBuildEngine)task.BuildEngine).Errors.Should().ContainSingle();
            File.Exists(Path.Combine(directory, "result.deps.json")).Should().BeFalse();
        }

        private WriteDepsFile CreateDeclaredTask(string directory) => new()
        {
            BuildEngine = new BasicBuildEngine(),
            TaskEnvironment = TaskEnvironmentHelper.CreateForTest(directory),
            ProjectPath = "TestApp.csproj",
            AssetsFilePath = "assets.json",
            DepsFilePath = "result.deps.json",
            DeclaredInputs = [new TaskItem("assets.json")],
            DeclaredOutputs = [new TaskItem("result.deps.json")],
            TargetFramework = "net8.0",
            RuntimeFrameworks = [new TaskItem("Microsoft.NETCore.App")],
            AssemblyName = "TestApp",
            AssemblyExtension = ".dll",
            AssemblyVersion = "1.0.0.0",
            IncludeMainProject = true,
            CompileReferences = [],
            ResolvedNuGetFiles = [],
            ResolvedRuntimeTargetsFiles = []
        };

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void SelfContainedWriterRequiresItsDeclaredRuntimeGraph(bool declared)
        {
            string directory = CreateTestProjectDirectory();
            CreateAssetsFile(directory, "assets.json");
            var task = CreateDeclaredTask(directory);
            task.IsSelfContained = true;
            task.RuntimeGraphPath = "runtime.json";
            if (declared)
            {
                task.DeclaredInputs = [new TaskItem("assets.json"), new TaskItem("runtime.json")];
            }

            task.Execute().Should().Be(declared);

            File.Exists(Path.Combine(directory, "result.deps.json")).Should().Be(declared);
            ((BasicBuildEngine)task.BuildEngine).Errors.Should().HaveCount(declared ? 0 : 1);
        }

        [TestMethod]
        [DataRow(null, "0.0.0.0", false)]
        [DataRow("1.2", "1.2.0.0", false)]
        [DataRow("1.2.3.4", "1.2.3.4", true)]
        public void ManagedVersionsComeFromMetadataRatherThanNativeResources(string attribute, string expected, bool localAttribute)
        {
            string path = Path.Combine(CreateTestProjectDirectory(), "managed.dll");
            EmitAssembly(path, new Version(5, 6, 7, 8), attribute, localAttribute: localAttribute,
                resources: new VersionResources(FixedVersion(9, 10, 11, 12)));

            RuntimeFileVersions.Read(path).Should().Be(("5.6.7.8", expected));
        }

        [TestMethod]
        [DataRow("valid")]
        [DataRow("conflicting")]
        [DataRow("malformed")]
        public void NativePeVersionsArePortableAndRejectAmbiguousResources(string resourceKind)
        {
            string path = Path.Combine(CreateTestProjectDirectory(), "native.dll");
            byte[] resource = resourceKind == "malformed" ? new byte[92] : FixedVersion(1, 2, 3, 4);
            byte[] second = resourceKind == "conflicting" ? FixedVersion(5, 6, 7, 8) : resource;
            EmitAssembly(path, new Version(9, 10, 11, 12), null, native: true, resources: new VersionResources(resource, second));

            if (resourceKind == "valid")
            {
                RuntimeFileVersions.Read(path).Should().Be((null, "1.2.3.4"));
            }
            else if (resourceKind == "conflicting")
            {
                Assert.ThrowsExactly<BuildErrorException>(() => RuntimeFileVersions.Read(path));
            }
            else
            {
                Assert.ThrowsExactly<BadImageFormatException>(() => RuntimeFileVersions.Read(path));
            }
        }

        [TestMethod]
        [DataRow("invalid")]
        [DataRow("1.2.70000.4")]
        public void UnsupportedManagedFileVersionsFailInsteadOfUsingAmbientFallbacks(string version)
        {
            string path = Path.Combine(CreateTestProjectDirectory(), "invalid.dll");
            EmitAssembly(path, new Version(1, 2, 3, 4), version);

            Assert.ThrowsExactly<BuildErrorException>(() => RuntimeFileVersions.Read(path));
        }

        private static byte[] FixedVersion(ushort major, ushort minor, ushort build, ushort revision)
        {
            var value = new BlobBuilder();
            value.WriteUInt16(92);
            value.WriteUInt16(52);
            value.WriteUInt16(0);
            value.WriteUTF16("VS_VERSION_INFO\0");
            value.Align(4);
            value.WriteUInt32(0xFEEF04BD);
            value.WriteUInt32(0x00010000);
            value.WriteUInt32(((uint)major << 16) | minor);
            value.WriteUInt32(((uint)build << 16) | revision);
            value.WriteBytes(0, 36);
            return value.ToArray();
        }

        private sealed class VersionResources(params byte[][] resources) : ResourceSectionBuilder
        {
            protected override void Serialize(BlobBuilder builder, SectionLocation location)
            {
                Directory(1);
                builder.WriteUInt32(16);
                builder.WriteUInt32(0x80000018);
                Directory(1);
                builder.WriteUInt32(1);
                builder.WriteUInt32(0x80000030);
                Directory((ushort)resources.Length);
                int entryOffset = 64 + resources.Length * 8;
                for (int i = 0; i < resources.Length; i++)
                {
                    builder.WriteUInt32((uint)i);
                    builder.WriteUInt32((uint)(entryOffset + i * 16));
                }

                int contentOffset = entryOffset + resources.Length * 16;
                foreach (byte[] resource in resources)
                {
                    builder.WriteUInt32((uint)(location.RelativeVirtualAddress + contentOffset));
                    builder.WriteUInt32((uint)resource.Length);
                    builder.WriteUInt64(0);
                    contentOffset += resource.Length;
                }

                foreach (byte[] resource in resources)
                {
                    builder.WriteBytes(resource);
                }

                void Directory(ushort entries)
                {
                    builder.WriteBytes(0, 14);
                    builder.WriteUInt16(entries);
                }
            }
        }

        private static void EmitAssembly(string path, Version version, string fileVersion, bool localAttribute = false, bool native = false, ResourceSectionBuilder resources = null)
        {
            var metadata = new MetadataBuilder();
            metadata.AddModule(0, metadata.GetOrAddString("Dependency.dll"), metadata.GetOrAddGuid(new Guid("5f3c8cac-5ba0-4096-82c1-21d9e765c5b3")), default, default);
            var assembly = metadata.AddAssembly(metadata.GetOrAddString("Dependency"), version, default, default, 0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            if (fileVersion is not null)
            {
                var signature = new BlobBuilder();
                new BlobEncoder(signature).MethodSignature(isInstanceMethod: true).Parameters(1, result => result.Void(), parameters => parameters.AddParameter().Type().String());
                EntityHandle constructor;
                if (localAttribute)
                {
                    metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("System.Reflection"), metadata.GetOrAddString("AssemblyFileVersionAttribute"),
                        default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
                    constructor = metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                        MethodImplAttributes.Runtime, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(signature), 0, MetadataTokens.ParameterHandle(1));
                }
                else
                {
                    var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(8, 0, 0, 0), default, default, 0, default);
                    var attribute = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Reflection"), metadata.GetOrAddString("AssemblyFileVersionAttribute"));
                    constructor = metadata.AddMemberReference(attribute, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(signature));
                }

                var value = new BlobBuilder();
                value.WriteUInt16(1);
                value.WriteSerializedString(fileVersion);
                value.WriteUInt16(0);
                metadata.AddCustomAttribute(assembly, constructor, metadata.GetOrAddBlob(value));
            }

            var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
                new MetadataRootBuilder(metadata), new BlobBuilder(), nativeResources: resources, flags: CorFlags.ILOnly);
            var image = new BlobBuilder();
            pe.Serialize(image);
            byte[] bytes = image.ToArray();
            if (native)
            {
                using var reader = new PEReader(new MemoryStream(bytes));
                int directoriesOffset = reader.PEHeaders.PEHeader.Magic == PEMagic.PE32Plus ? 112 : 96;
                Array.Clear(bytes, reader.PEHeaders.PEHeaderStartOffset + directoriesOffset + 14 * 8, 8);
            }

            File.WriteAllBytes(path, bytes);
        }

        private sealed class BasicBuildEngine : IBuildEngine
        {
            public List<BuildErrorEventArgs> Errors { get; } = [];
            public bool ContinueOnError => false;
            public int LineNumberOfTaskNode => 0;
            public int ColumnNumberOfTaskNode => 0;
            public string ProjectFileOfTaskNode => "";
            public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
            public void LogWarningEvent(BuildWarningEventArgs e) { }
            public void LogMessageEvent(BuildMessageEventArgs e) { }
            public void LogCustomEvent(CustomBuildEventArgs e) { }
            public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => throw new NotSupportedException();
        }

        [TestMethod]
        public void ProjectAndDepsFilePathsAreResolvedThroughTaskEnvironment()
        {
            string projectDir = CreateTestProjectDirectory();
            string decoyCwd = CreateTestProjectDirectory("DecoyDir");
            string relativeDepsPath = Path.Combine("bin", "Debug", "net8.0", "TestApp.deps.json");

            Directory.SetCurrentDirectory(decoyCwd);

            var task = CreateMinimalTask("TestApp.csproj", relativeDepsPath, projectDir);
            task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);

            bool result = task.Execute();

            result.Should().BeTrue("task should resolve relative paths from TaskEnvironment.ProjectDirectory");
            File.Exists(Path.Combine(projectDir, relativeDepsPath)).Should().BeTrue(
                "deps file should be written relative to TaskEnvironment.ProjectDirectory, not process CWD");
            File.Exists(Path.Combine(decoyCwd, relativeDepsPath)).Should().BeFalse(
                "deps file should not be written to the process CWD");
            task.FilesWritten.Should().ContainSingle().Which.ItemSpec.Should().Be(relativeDepsPath,
                "FilesWritten should preserve the caller's original relative output path");
        }

        [TestMethod]
        public void AssetsAndRuntimeGraphPathsAreResolvedThroughTaskEnvironment()
        {
            string projectDir = CreateTestProjectDirectory();
            // Decoy CWD is intentionally empty (no runtime.json) so that resolving
            // RuntimeGraphPath against the process CWD would fail; only resolution
            // through TaskEnvironment.ProjectDirectory can succeed.
            string decoyCwd = CreateEmptyDecoyDirectory();
            string relativeAssetsPath = Path.Combine("obj", "project.assets.json");
            CreateAssetsFile(projectDir, relativeAssetsPath);

            Directory.SetCurrentDirectory(decoyCwd);

            var task = CreateMinimalTask("TestApp.csproj", "selfcontained.deps.json", projectDir);
            task.AssetsFilePath = relativeAssetsPath;
            task.RuntimeGraphPath = "runtime.json";
            task.IsSelfContained = true;
            task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);

            bool result = task.Execute();

            result.Should().BeTrue(
                "AssetsFilePath and RuntimeGraphPath should be resolved from TaskEnvironment.ProjectDirectory, not process CWD. Errors: {0}",
                GetLoggedErrors(task));
            File.Exists(Path.Combine(projectDir, "selfcontained.deps.json")).Should().BeTrue(
                "the relative runtime graph should be read from the task environment project directory");
            File.Exists(Path.Combine(decoyCwd, "selfcontained.deps.json")).Should().BeFalse(
                "deps file should not be written under the process CWD");
        }

        [TestMethod]
        public void SatelliteAssemblyTargetPathFlowsVerbatimToDepsJsonResources()
        {
            // Encodes the contract that AssemblySatelliteAssemblies metadata is passed through
            // to deps.json verbatim. The task reads only Culture and TargetPath; TargetPath is
            // relative by design (deps.json format requirement) and must NOT be absolutized
            // against TaskEnvironment.ProjectDirectory or the process CWD.
            string projectDir = CreateTestProjectDirectory();
            string decoyCwd = CreateEmptyDecoyDirectory();
            string relativeDepsPath = Path.Combine("bin", "Debug", "net8.0", "TestApp.deps.json");
            string satelliteRelativeTargetPath = Path.Combine("de", "TestApp.resources.dll");

            Directory.SetCurrentDirectory(decoyCwd);

            var task = CreateMinimalTask("TestApp.csproj", relativeDepsPath, projectDir);
            task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);
            task.AssemblySatelliteAssemblies = new ITaskItem[]
            {
                // ItemSpec is intentionally not a real on-disk path: the task only reads
                // metadata from satellite items, never opens the underlying file.
                new MockTaskItem(
                    itemSpec: @"de\TestApp.resources.dll",
                    metadata: new Dictionary<string, string>
                    {
                        { "Culture", "de" },
                        { "TargetPath", satelliteRelativeTargetPath },
                    }),
            };

            bool result = task.Execute();

            result.Should().BeTrue("task should succeed: {0}", GetLoggedErrors(task));
            string depsPath = Path.Combine(projectDir, relativeDepsPath);
            File.Exists(depsPath).Should().BeTrue("deps file should be produced");

            JObject depsJson = JObject.Parse(File.ReadAllText(depsPath));
            JObject targets = (JObject)depsJson["targets"];
            targets.Should().NotBeNull("deps.json must have a targets section");
            JObject targetFramework = (JObject)targets.Properties().First().Value;
            JObject mainLibrary = (JObject)targetFramework["TestApp/1.0.0.0"];
            mainLibrary.Should().NotBeNull("the main project library should be present in deps.json");

            JObject resources = (JObject)mainLibrary["resources"];
            resources.Should().NotBeNull("the satellite assembly should produce a resources section");

            // The deps.json writer normalizes path separators to '/'; the path itself is
            // otherwise the TargetPath metadata byte-for-byte. Critically it is NOT:
            //   - prefixed with TaskEnvironment.ProjectDirectory,
            //   - prefixed with the process CWD,
            //   - resolved or absolutized in any way.
            string expectedRelativePath = satelliteRelativeTargetPath.Replace('\\', '/');
            resources.Properties().Should().ContainSingle(
                "exactly one satellite was supplied")
                .Which.Name.Should().Be(expectedRelativePath,
                    "TargetPath metadata flows verbatim into deps.json (slashes normalized only)");
            resources[expectedRelativePath]["locale"].Value<string>().Should().Be("de",
                "Culture metadata is preserved as the locale");
        }

        private string CreateEmptyDecoyDirectory([CallerMemberName] string testName = null)
        {
            string decoyDir = Path.Combine(Path.GetTempPath(), $"GenerateDepsFileTest_{testName}_decoy_{Guid.NewGuid():N}");
            Directory.CreateDirectory(decoyDir);
            _tempDirs.Add(decoyDir);
            return decoyDir;
        }

        private string CreateTestProjectDirectory([CallerMemberName] string testName = null)
        {
            string testDir = Path.Combine(Path.GetTempPath(), $"GenerateDepsFileTest_{testName}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(testDir);
            Directory.CreateDirectory(Path.Combine(testDir, "bin", "Debug", "net8.0"));
            _tempDirs.Add(testDir);

            // Create a minimal runtime.json
            string runtimeJsonPath = Path.Combine(testDir, "runtime.json");
            File.WriteAllText(runtimeJsonPath, @"{
  ""runtimes"": {
    ""win"": {},
    ""win-x64"": { ""#import"": [ ""win"" ] }
  }
}");

            return testDir;
        }

        private GenerateDepsFile CreateMinimalTask(string projectPath, string depsFilePath, string projectDirectory = null)
        {
            string projectDir = projectDirectory ?? Path.GetDirectoryName(projectPath);
            projectDir.Should().NotBeNull("relative project paths require the test project directory to be provided");

            return new GenerateDepsFile
            {
                BuildEngine = new MockBuildEngine(),
                ProjectPath = projectPath,
                DepsFilePath = depsFilePath,
                TargetFramework = "net8.0",
                AssemblyName = "TestApp",
                AssemblyExtension = ".dll",
                AssemblyVersion = "1.0.0.0",
                IncludeMainProject = true,
                CompileReferences = Array.Empty<ITaskItem>(),
                ResolvedNuGetFiles = Array.Empty<ITaskItem>(),
                ResolvedRuntimeTargetsFiles = Array.Empty<ITaskItem>(),
                RuntimeGraphPath = Path.Combine(projectDir, "runtime.json"),
            };
        }

        private static string CreateAssetsFile(string projectDir, string relativeAssetsPath)
        {
            string assetsPath = Path.Combine(projectDir, relativeAssetsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(assetsPath));
            string projectPath = Path.Combine(projectDir, "TestApp.csproj");

            File.WriteAllText(assetsPath, $$"""
                {
                  "version": 3,
                  "targets": { "net8.0": {} },
                  "libraries": {},
                  "projectFileDependencyGroups": { "net8.0": [] },
                  "packageFolders": {},
                  "project": {
                    "version": "1.0.0",
                    "restore": {
                      "projectUniqueName": {{JsonConvert.ToString(projectPath)}},
                      "projectName": "TestApp",
                      "projectPath": {{JsonConvert.ToString(projectPath)}},
                      "packagesPath": {{JsonConvert.ToString(Path.Combine(projectDir, ".nuget", "packages"))}},
                      "outputPath": {{JsonConvert.ToString(Path.Combine(projectDir, "obj"))}},
                      "projectStyle": "PackageReference",
                      "fallbackFolders": [],
                      "configFilePaths": [],
                      "originalTargetFrameworks": [ "net8.0" ],
                      "sources": {},
                      "frameworks": { "net8.0": { "targetAlias": "net8.0" } },
                      "warningProperties": { "warnAsError": [ "NU1605" ] }
                    },
                    "frameworks": {
                      "net8.0": {
                        "targetAlias": "net8.0",
                        "runtimeIdentifierGraphPath": {{JsonConvert.ToString(Path.Combine(projectDir, "runtime.json"))}}
                      }
                    }
                  }
                }
                """);
            return assetsPath;
        }

        private static string GetLoggedErrors(GenerateDepsFile task)
        {
            return string.Join(Environment.NewLine, ((MockBuildEngine)task.BuildEngine).Errors.Select(e => e.Message));
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_originalCwd);
            foreach (var dir in _tempDirs)
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
