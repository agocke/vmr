// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.BuildTasks.UnitTests.TestUtilities;
using Microsoft.CodeAnalysis.CommandLine;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.BuildTasks.UnitTests
{
    public sealed class CscTests : TestBase
    {
        public ITestOutputHelper TestOutputHelper { get; }

        public CscTests(ITestOutputHelper testOutputHelper)
        {
            TestOutputHelper = testOutputHelper;
        }

#if !NETFRAMEWORK
        private static FormatCsc CreateFormatter(string directory) => new FormatCsc
        {
            BuildEngine = new MockEngine(),
            TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory),
            Deterministic = true,
            DeterministicSourcePaths = true,
            NoConfig = true,
            NoStandardLib = true,
            TargetType = "library",
            DebugType = "portable",
            OutputAssembly = new TaskItem("obj/app.dll"),
            Sources = new[] { new TaskItem("Program.cs") },
        };

        [Fact]
        public void DeclaredCompilerPreparationDoesNotReadFiles()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var task = CreateFormatter(Path.Combine(directory, "project"));
            task.SourceRoots = new[] { new TaskItem(directory + Path.DirectorySeparatorChar, new Dictionary<string, string> { ["SourceControl"] = "git" }) };
            task.Analyzers = MSBuildUtil.CreateTaskItems("missing/generator.dll");
            task.AdditionalFiles = MSBuildUtil.CreateTaskItems("missing/data.txt");
            task.AnalyzerConfigFiles = MSBuildUtil.CreateTaskItems("missing/.editorconfig");
            task.AdditionalCompilerInputs = MSBuildUtil.CreateTaskItems("missing/dependency.dll");
            task.PathMap = "ignored=/physical-checkout/";

            Assert.True(task.Execute(), ((MockEngine)task.BuildEngine).Log);
            Assert.Equal("/_/", Assert.Single(task.PathMappings).GetMetadata("MappedPath"));
            Assert.Contains("/deterministic+", task.CompilerArguments);
            Assert.Contains("/nosdkpath", task.CompilerArguments);
            Assert.DoesNotContain("physical-checkout", task.CompilerArguments);
            Assert.Equal(new[] { "app.dll", "app.pdb" }, task.CompilerOutputs.Select(item => Path.GetFileName(item.ItemSpec)));
            Assert.Contains(task.CompilerInputs, item => item.ItemSpec.EndsWith("generator.dll", StringComparison.Ordinal));
            Assert.Contains(task.CompilerInputs, item => item.ItemSpec.EndsWith("dependency.dll", StringComparison.Ordinal));
            Assert.Contains(task.CompilerInputs, item => item.ItemSpec.EndsWith(".editorconfig", StringComparison.Ordinal));
            Assert.Contains(task.CompilerInputs, item => item.ItemSpec.EndsWith("data.txt", StringComparison.Ordinal));
            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public void DeclaredCompilerEmptyOptionsRemainAbsent()
        {
            var task = CreateFormatter(Path.GetTempPath());
            task.DocumentationFile = "";
            task.PdbFile = "";
            task.Platform = "";
            task.CodeAnalysisRuleSet = "";
            task.Win32Manifest = "";
            Assert.True(task.Execute(), ((MockEngine)task.BuildEngine).Log);
            Assert.DoesNotContain("/doc:", task.CompilerArguments);
            Assert.DoesNotContain("/pdb:", task.CompilerArguments);
            Assert.DoesNotContain("/platform:", task.CompilerArguments);
            Assert.DoesNotContain("/ruleset:", task.CompilerArguments);
            Assert.DoesNotContain("/win32manifest:", task.CompilerArguments);
        }

        [Fact]
        public void CompilerToolManifestEscapesProjectSyntax()
        {
            using var root = new TempRoot();
            var directory = root.CreateDirectory();
            var input = Path.Combine(directory.Path, "a & b;$(literal).dll");
            var manifest = Path.Combine(directory.Path, "inputs.props");
            var task = new WriteCscInputs
            {
                BuildEngine = new MockEngine(),
                Inputs = new[] { new TaskItem(input.Replace("$", "%24").Replace(";", "%3B")) },
                OutputFile = manifest,
            };

            Assert.True(task.Execute());
            var document = XDocument.Load(manifest);
            using var projects = new Microsoft.Build.Evaluation.ProjectCollection();
            var project = new Microsoft.Build.Evaluation.Project(document.CreateReader(), null, null, projects);
            Assert.Equal(input, Assert.Single(project.GetItems("_CscPreparedToolInput")).EvaluatedInclude);
        }

        [Theory]
        [InlineData("portable", false, false, true)]
        [InlineData("PORTABLE", false, false, true)]
        [InlineData("portable", true, true, false)]
        [InlineData("embedded", true, false, false)]
        [InlineData("none", true, false, false)]
        public void DeclaredCompilerPdbFootprint(string debugType, bool emitDebug, bool refOnly, bool hasPdb)
        {
            var task = CreateFormatter(Path.GetTempPath());
            task.DebugType = debugType;
            task.EmitDebugInformation = emitDebug;
            task.RefOnly = refOnly;
            Assert.True(task.Execute(), ((MockEngine)task.BuildEngine).Log);
            Assert.Equal(hasPdb, task.CompilerOutputs.Any(item => item.ItemSpec.EndsWith(".pdb", StringComparison.Ordinal)));
        }

        [Theory]
        [InlineData("deterministic")]
        [InlineData("paths")]
        [InlineData("response")]
        [InlineData("generated-directory")]
        [InlineData("features")]
        public void DeclaredCompilerRejectsUnsafeModes(string mode)
        {
            var task = CreateFormatter(Path.GetTempPath());
            switch (mode)
            {
                case "deterministic": task.Deterministic = false; break;
                case "paths": task.DeterministicSourcePaths = false; break;
                case "response": task.ResponseFiles = MSBuildUtil.CreateTaskItems("override.rsp"); break;
                case "generated-directory": task.GeneratedFilesOutputPath = "generated"; break;
                case "features": task.Features = "debug-determinism"; break;
            }

            Assert.False(task.Execute());
            Assert.Empty(task.CompilerArguments);
            Assert.Empty(task.CompilerOutputs);
        }

        [Fact]
        public void DeclaredCompilerRelocation()
        {
            using var root = new TempRoot();
            var first = root.CreateDirectory();
            var second = root.CreateDirectory().CreateDirectory("a much longer checkout,with=delimiters");
            var compiler = Path.Combine(AppContext.BaseDirectory, "bincore", "csc.dll");
            var host = RuntimeHostInfo.GetDotNetHostPath(TaskEnvironment.Fallback.BuildEnvironment)!;
            const string source = """
                public class C
                {
                    public static string Get([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
                    public static string Value => Get();
                }
                """;

            foreach (var directory in new[] { first, second })
            {
                directory.CreateFile("Program.cs").WriteAllText(source);
                directory.CreateDirectory("obj");
                var format = CreateFormatter(directory.Path);
                format.References = new[] { new TaskItem(typeof(object).Assembly.Location) };
                Assert.True(format.Execute(), ((MockEngine)format.BuildEngine).Log);
                var unsafePath = directory.Path.Replace(",", ",,").Replace("=", "==") + Path.DirectorySeparatorChar;
                var overrides = new CommandLineBuilder();
                overrides.AppendSwitch("/deterministic-");
                overrides.AppendSwitchIfNotNull("/pathmap:", unsafePath + "=" + unsafePath);
                var compile = new CompileCsc
                {
                    BuildEngine = new MockEngine(),
                    TaskEnvironment = format.TaskEnvironment,
                    DotNetHost = host,
                    CompilerAssembly = compiler,
                    CompilerArguments = format.CompilerArguments + " " + overrides,
                    PathMappings = format.PathMappings,
                    DeclaredInputs = MSBuildUtil.CreateTaskItems(host, compiler).Concat(format.CompilerInputs).ToArray(),
                    DeclaredOutputs = format.CompilerOutputs,
                };
                Assert.True(compile.Execute(), ((MockEngine)compile.BuildEngine).Log);

                using var pe = new PEReader(File.OpenRead(Path.Combine(directory.Path, "obj", "app.dll")));
                var codeView = pe.ReadCodeViewDebugDirectoryData(Assert.Single(pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView)));
                Assert.Equal("/_/obj/app.pdb", codeView.Path.Replace('\\', '/'));
                using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.Combine(directory.Path, "obj", "app.pdb")));
                var pdb = provider.GetMetadataReader();
                Assert.All(pdb.Documents, document => Assert.Equal("/_/Program.cs", pdb.GetString(pdb.GetDocument(document).Name).Replace('\\', '/')));

                var metadata = pe.GetMetadataReader();
                var getter = metadata.MethodDefinitions.Select(handle => metadata.GetMethodDefinition(handle))
                    .Single(method => metadata.GetString(method.Name) == "get_Value");
                var il = pe.GetMethodBody(getter.RelativeVirtualAddress).GetILBytes()!;
                var stringToken = BitConverter.ToInt32(il, Array.IndexOf(il, (byte)0x72) + 1);
                Assert.Equal("/_/Program.cs", metadata.GetUserString(MetadataTokens.UserStringHandle(stringToken)).Replace('\\', '/'));
            }

            Assert.Equal(File.ReadAllBytes(Path.Combine(first.Path, "obj", "app.dll")), File.ReadAllBytes(Path.Combine(second.Path, "obj", "app.dll")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(first.Path, "obj", "app.pdb")), File.ReadAllBytes(Path.Combine(second.Path, "obj", "app.pdb")));
        }
#endif

        [Fact]
        public void SingleSource()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void MultipleSourceFiles()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test1.cs", "test2.cs");
            Assert.Equal("/out:test1.exe test1.cs test2.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void PathMapOption()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.PathMap = "K1=V1,K2=V2";
            Assert.Equal("/pathmap:\"K1=V1,K2=V2\" /out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void DeterministicFlag()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Deterministic = true;
            Assert.Equal("/out:test.exe /deterministic+ test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Deterministic = false;
            Assert.Equal("/out:test.exe /deterministic- test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void PublicSignFlag()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.PublicSign = true;
            Assert.Equal("/out:test.exe /publicsign+ test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.PublicSign = false;
            Assert.Equal("/out:test.exe /publicsign- test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void RuntimeMetadataVersionFlag()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.RuntimeMetadataVersion = "v1234";
            Assert.Equal("/out:test.exe /runtimemetadataversion:v1234 test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.RuntimeMetadataVersion = null;
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void LangVersionFlag()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.LangVersion = "iso-1";
            Assert.Equal("/out:test.exe /langversion:iso-1 test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void ChecksumAlgorithmOption()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.ChecksumAlgorithm = "sha256";
            Assert.Equal("/out:test.exe /checksumalgorithm:sha256 test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.ChecksumAlgorithm = null;
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.ChecksumAlgorithm = "";
            Assert.Equal("/out:test.exe /checksumalgorithm: test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void InstrumentTestNamesFlag()
        {
            var csc = new Csc();
            csc.Instrument = null;
            Assert.Equal(string.Empty, csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Instrument = "TestCoverage";
            Assert.Equal("/instrument:TestCoverage", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Instrument = "TestCoverage,Mumble";
            Assert.Equal("/instrument:TestCoverage,Mumble", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Instrument = "TestCoverage,Mumble;Stumble";
            Assert.Equal("/instrument:TestCoverage,Mumble,Stumble", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void TargetTypeDll()
        {
            var csc = new Csc();
            csc.TargetType = "library";
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.dll /target:library test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void TargetTypeBad()
        {
            var csc = new Csc();
            csc.TargetType = "bad";
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.exe /target:bad test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void OutputAssembly()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.OutputAssembly = MSBuildUtil.CreateTaskItem("x.exe");
            Assert.Equal("/out:x.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void DefineConstantsSimple()
        {
            Action<string> test = (s) =>
            {
                var csc = new Csc();
                csc.DefineConstants = s;
                csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
                Assert.Equal("/define:D1;D2 /out:test.exe test.cs", csc.GenerateResponseFileContents());
            };

            test("D1;D2");
            test("D1,D2");
            test("D1 D2");
        }

        [Fact]
        public void Features()
        {
            Action<string> test = (s) =>
            {
                var csc = new Csc();
                csc.Features = s;
                csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
                Assert.Equal("/out:test.exe /features:a /features:b test.cs", csc.GenerateResponseFileContents());
            };

            test("a;b");
            test("a,b");
            test("a b");
            test(",a;b ");
            test(";a;;b;");
            test(",a,,b,");
        }

        [Fact]
        public void FeaturesInterceptors()
        {
            var csc = new Csc();
            csc.InterceptorsNamespaces = "NS1.NS2;NS3.NS4";
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            AssertEx.Equal("""/features:"InterceptorsNamespaces=NS1.NS2;NS3.NS4" /out:test.exe test.cs""", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void FeaturesInterceptorsPreview()
        {
            var csc = new Csc();
            csc.InterceptorsPreviewNamespaces = "NS1.NS2;NS3.NS4";
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            AssertEx.Equal("""/features:"InterceptorsNamespaces=NS1.NS2;NS3.NS4" /out:test.exe test.cs""", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void FeaturesInterceptorsPreviewBoth()
        {
            var csc = new Csc();
            csc.InterceptorsNamespaces = "NS1.NS2;NS3.NS4";
            csc.InterceptorsPreviewNamespaces = "NS5.NS6;NS7.NS8";
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            AssertEx.Equal("""/features:"InterceptorsNamespaces=NS1.NS2;NS3.NS4;NS5.NS6;NS7.NS8" /out:test.exe test.cs""", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void FeaturesEmpty()
        {
            foreach (var cur in new[] { "", null })
            {
                var csc = new Csc();
                csc.Features = cur;
                csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
                Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
            }
        }

        [Fact]
        public void DebugType()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "full";
            Assert.Equal("/debug:full /out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "pdbonly";
            Assert.Equal("/debug:pdbonly /out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "portable";
            Assert.Equal("/debug:portable /out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "embedded";
            Assert.Equal("/debug:embedded /out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = null;
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "";
            Assert.Equal("/debug: /out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void SourceLink()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "portable";
            csc.SourceLink = @"C:\x y\z.json";
            Assert.Equal(@"/debug:portable /out:test.exe /sourcelink:""C:\x y\z.json"" test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "portable";
            csc.SourceLink = null;
            Assert.Equal(@"/debug:portable /out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "portable";
            csc.SourceLink = "";
            Assert.Equal(@"/debug:portable /out:test.exe /sourcelink: test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void Embed()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "portable";
            csc.EmbeddedFiles = MSBuildUtil.CreateTaskItems(@"test.cs", @"test.txt");
            Assert.Equal(@"/debug:portable /out:test.exe /embed:test.cs /embed:test.txt test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "portable";
            csc.EmbeddedFiles = MSBuildUtil.CreateTaskItems(@"C:\x y\z.json");
            Assert.Equal(@"/debug:portable /out:test.exe /embed:""C:\x y\z.json"" test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "portable";
            csc.EmbeddedFiles = null;
            Assert.Equal(@"/debug:portable /out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.DebugType = "full";
            csc.EmbeddedFiles = MSBuildUtil.CreateTaskItems();
            Assert.Equal(@"/debug:full /out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void EmbedAllSources()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.EmbeddedFiles = MSBuildUtil.CreateTaskItems(@"test.cs", @"test.txt");
            csc.EmbedAllSources = true;

            Assert.Equal(@"/out:test.exe /embed /embed:test.cs /embed:test.txt test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.EmbedAllSources = true;

            Assert.Equal(@"/out:test.exe /embed test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void RefOut()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.OutputRefAssembly = MSBuildUtil.CreateTaskItem("ref\\test.dll");
            Assert.Equal("/out:test.exe /refout:ref\\test.dll test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void RefOnly()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.RefOnly = true;
            Assert.Equal("/out:test.exe /refonly test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void NullableReferenceTypes_Enabled()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Nullable = "enable";
            Assert.Equal("/nullable:enable /out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void NullableReferenceTypes_Disabled()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Nullable = "disable";
            Assert.Equal("/nullable:disable /out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void NullableReferenceTypes_Safeonly()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Nullable = "safeonly";
            Assert.Equal("/nullable:safeonly /out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void NullableReferenceTypes_Warnings()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Nullable = "warnings";
            Assert.Equal("/nullable:warnings /out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void NullableReferenceTypes_Safeonlywarnings()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Nullable = "safeonlywarnings";
            Assert.Equal("/nullable:safeonlywarnings /out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void NullableReferenceTypes_Default_01()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.Nullable = null;
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void NullableReferenceTypes_Default_02()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact, WorkItem(29252, "https://github.com/dotnet/roslyn/issues/29252")]
        public void DisableSdkPath()
        {
            var csc = new Csc();
            csc.DisableSdkPath = true;
            Assert.Equal(@"/nosdkpath", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void SharedCompilationId()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.UseSharedCompilation = true;
            csc.SharedCompilationId = "testPipeName";
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.UseSharedCompilation = false;
            csc.SharedCompilationId = "testPipeName";
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.SharedCompilationId = "testPipeName";
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void EmptyCscToolPath()
        {
            var csc = new Csc();
            csc.ToolPath = "";
            csc.ToolExe = Path.Combine("path", "to", "custom_csc");
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("", csc.GenerateCommandLineContents());
            Assert.Equal(Path.Combine("path", "to", "custom_csc"), csc.GeneratePathToTool());

            csc = new Csc();
            csc.ToolExe = Path.Combine("path", "to", "custom_csc");
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("", csc.GenerateCommandLineContents());
            Assert.Equal(Path.Combine("path", "to", "custom_csc"), csc.GeneratePathToTool());
        }

        [Fact]
        public void EmptyCscToolExe()
        {
            var csc = new Csc();
            csc.ToolPath = Path.Combine("path", "to", "custom_csc");
            csc.ToolExe = "";
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("", csc.GenerateCommandLineContents());
            AssertEx.Equal(Path.Combine("path", "to", "custom_csc", $"csc{PlatformInformation.ExeExtension}"), csc.GeneratePathToTool());

            csc = new Csc();
            csc.ToolPath = Path.Combine("path", "to", "custom_csc");
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("", csc.GenerateCommandLineContents());
            AssertEx.Equal(Path.Combine("path", "to", "custom_csc", $"csc{PlatformInformation.ExeExtension}"), csc.GeneratePathToTool());
        }

        /// <summary>
        /// Setting ToolExe to "csc.exe" should use the built-in compiler regardless of apphost being used or not.
        /// </summary>
        [Theory, CombinatorialData, WorkItem("https://devdiv.visualstudio.com/DevDiv/_workitems/edit/2615118")]
        public void BuiltInToolExe(bool useAppHost, bool setToolExe)
        {
            using var temp = new TempRoot();
            var projectDirectory = temp.CreateDirectory();
            var taskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(
                projectDirectory.Path,
                new Dictionary<string, string>
                {
                    [RuntimeHostInfo.DotNetHostPathEnvironmentName] = TestHelpers.GetRootedPath(RuntimeHostInfo.DotNetHostExecutableName)
                });

            var csc = new Csc()
            {
                UseAppHost_TestOnly = useAppHost,
                TaskEnvironment = taskEnvironment,
            };

            if (setToolExe)
            {
                csc.ToolExe = $"csc{PlatformInformation.ExeExtension}";
            }
            if (useAppHost)
            {
                AssertEx.Equal(csc.PathToBuiltInTool, csc.GeneratePathToTool());
                AssertEx.Equal("", csc.GenerateCommandLineContents());
            }
            else
            {
                AssertEx.Equal(RuntimeHostInfo.GetDotNetHostPath(taskEnvironment.BuildEnvironment), csc.GeneratePathToTool());
                AssertEx.Equal(RuntimeHostInfo.GetDotNetExecCommandLine(csc.PathToBuiltInTool, ""), csc.GenerateCommandLineContents());
            }
        }

        /// <summary>
        /// The value of `DOTNET_HOST_PATH` will be preferred as is when present
        /// </summary>
        [Fact]
        public void BuiltInToolUsesTaskEnvironmentDotNetHostPathRelative()
        {
            var projectDirectory = Temp.CreateDirectory();
            var dotnetPath = Path.Combine(TestHelpers.Root, RuntimeHostInfo.DotNetHostExecutableName);
            var csc = new Csc
            {
                UseAppHost_TestOnly = false,
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(
                    projectDirectory.Path,
                    new Dictionary<string, string>
                    {
                        [RuntimeHostInfo.DotNetHostPathEnvironmentName] = dotnetPath,
                    }),
            };

            Assert.Equal(dotnetPath, csc.GeneratePathToTool());
        }

        [Fact]
        public void BuiltInToolUsesTaskEnvironmentDotNetHostWhenNull()
        {
            var projectDirectory = Temp.CreateDirectory();
            var csc = new Csc
            {
                UseAppHost_TestOnly = false,
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(
                    projectDirectory.Path,
                    new Dictionary<string, string>())
            };

            Assert.Equal(RuntimeHostInfo.DotNetHostExecutableName, csc.GeneratePathToTool());
        }

        /// <summary>
        /// A full path will be returned as is.
        /// </summary>
        [Fact]
        public void BuiltInToolUsesTaskEnvironmentDotNetHostPathFull()
        {
            var projectDirectory = Temp.CreateDirectory();
            var dotnetPath = Path.Combine(TestHelpers.Root, "dotnet-host");
            var csc = new Csc
            {
                UseAppHost_TestOnly = false,
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(
                    projectDirectory.Path,
                    new Dictionary<string, string>
                    {
                        [RuntimeHostInfo.DotNetHostPathEnvironmentName] = dotnetPath
                    }),
            };

            Assert.Equal(dotnetPath, csc.GeneratePathToTool());
        }

        [Fact]
        public void EditorConfig()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.AnalyzerConfigFiles = MSBuildUtil.CreateTaskItems(".editorconfig");
            Assert.Equal(@"/out:test.exe /analyzerconfig:.editorconfig test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs", "subdir\\test.cs");
            csc.AnalyzerConfigFiles = MSBuildUtil.CreateTaskItems(".editorconfig", "subdir\\.editorconfig");
            Assert.Equal($@"/out:test.exe /analyzerconfig:.editorconfig /analyzerconfig:subdir\.editorconfig test.cs subdir{Path.DirectorySeparatorChar}test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.AnalyzerConfigFiles = MSBuildUtil.CreateTaskItems("..\\.editorconfig", "sub dir\\.editorconfig");
            Assert.Equal(@"/out:test.exe /analyzerconfig:..\.editorconfig /analyzerconfig:""sub dir\.editorconfig"" test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        [WorkItem(40926, "https://github.com/dotnet/roslyn/issues/40926")]
        public void SkipAnalyzersFlag()
        {
            var csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.SkipAnalyzers = true;
            Assert.Equal("/out:test.exe /skipanalyzers+ test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            csc.SkipAnalyzers = false;
            Assert.Equal("/out:test.exe /skipanalyzers- test.cs", csc.GenerateResponseFileContents());

            csc = new Csc();
            csc.Sources = MSBuildUtil.CreateTaskItems("test.cs");
            Assert.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [Fact]
        [WorkItem(52467, "https://github.com/dotnet/roslyn/issues/52467")]
        public void UnexpectedExceptionLogsMessage()
        {
            var engine = new MockEngine();
            var csc = new Csc()
            {
                BuildEngine = engine,
            };

            csc.ExecuteTool(@"q:\path\csc.exe", "", "", new TestableCompilerServerLogger()
            {
                LogFunc = delegate { throw new Exception(""); }
            });
            Assert.False(string.IsNullOrEmpty(engine.Log));
        }

        [Fact]
        public void ReportIVTsSwitch()
        {
            var csc = new Csc();
            csc.ReportIVTs = true;
            AssertEx.Equal("/reportivts", csc.GenerateResponseFileContents());
        }

        [Fact]
        public void CommandLineArgs1()
        {
            var engine = new MockEngine(TestOutputHelper);
            var csc = new Csc()
            {
                BuildEngine = engine,
                Sources = MSBuildUtil.CreateTaskItems("test.cs"),
            };

            TaskTestUtil.AssertCommandLine(csc, engine, "/out:test.exe", "test.cs");
        }

        [Fact]
        public void CommandLineArgs2()
        {
            var engine = new MockEngine(TestOutputHelper);
            var csc = new Csc()
            {
                BuildEngine = engine,
                Sources = MSBuildUtil.CreateTaskItems("test.cs", "blah.cs"),
                TargetType = "library"
            };

            TaskTestUtil.AssertCommandLine(csc, engine, "/out:test.dll", "/target:library", "test.cs", "blah.cs");
        }

        [ConditionalFact(typeof(WindowsOnly), Reason = "https://github.com/dotnet/roslyn/issues/71571")]
        public void ReferenceForms()
        {
            parse(@"util.dll", null, @"/reference:util.dll");
            parse(@"util.dll", "global", @"/reference:util.dll");
            parse(@"util.dll", "lib", @"/reference:lib=util.dll");
            parse(@"""util.dll""", "global", @"/reference:""\""util.dll\""""");
            parse(@"c:\a=util.dll", null, @"/reference:""c:\a=util.dll""");
            parse(@"c:\a=util.dll", "global", @"/reference:""c:\a=util.dll""");
            parse(@"""c:\a=util.dll""", "global", @"/reference:""\""c:\a=util.dll\""""");
            parse(@"a=util.dll", null, @"/reference:""a=util.dll""");
            parse(@"util.dll", "lib", @"/reference:lib=util.dll");
            parse(@"c:\a=util.dll", "lib", @"/reference:lib=c:\a=util.dll");
            parse(@"util.dll", null, @"/link:util.dll", true);
            parse(@"util.dll", "global", @"/link:util.dll", true);
            parse(@"util.dll", "lib", @"/link:lib=util.dll", true);
            parseMultiple(@"util.dll", "global,a", @"/reference:util.dll", @"/reference:a=util.dll");
            parseMultiple(@"util.dll", "b,a", @"/reference:b=util.dll", @"/reference:a=util.dll");
            parseMultiple(@"c:\a=b\util.dll", "global,c", @"/reference:""c:\a=b\util.dll""", @"/reference:c=c:\a=b\util.dll");
            parseMultiple(@"c:\a=b\util.dll", "x,z", @"/reference:x=c:\a=b\util.dll", @"/reference:z=c:\a=b\util.dll");

            void parse(string refText, string? alias, string expectedArg, bool embedInteropTypes = false) =>
                parseCore(refText, alias, embedInteropTypes, [expectedArg]);

            void parseMultiple(string refText, string? alias, params string[] expectedArgs) =>
                parseCore(refText, alias, embedInteropTypes: false, expectedArgs);

            void parseCore(string refText, string? alias, bool embedInteropTypes, string[] expectedArgs)
            {
                var engine = new MockEngine(TestOutputHelper);
                var csc = new Csc()
                {
                    BuildEngine = engine,
                    Sources = MSBuildUtil.CreateTaskItems("test.cs"),
                    TargetType = "library",
                    References = [SimpleTaskItem.CreateReference(refText, alias: alias, embedInteropTypes)],
                };

                TaskTestUtil.AssertCommandLine(csc, engine, [.. expectedArgs, "/out:test.dll", "/target:library", "test.cs"]);
            }
        }

        [Fact]
        public void ReferenceErrors()
        {
            parseRef(@"util.dll", "a=b");
            parseRef(@"util.dll", "a b");
            parseRef(@"util.dll", "a;b");
            parseRef(@"util.dll", @"a""b");

            void parseRef(string refText, string alias)
            {
                var engine = new MockEngine(TestOutputHelper);
                var csc = new Csc()
                {
                    BuildEngine = engine,
                    Sources = MSBuildUtil.CreateTaskItems("test.cs"),
                    TargetType = "library",
                    References = [SimpleTaskItem.CreateReference(refText, alias)]
                };

                Assert.Throws<ArgumentException>(() => csc.GenerateResponseFileContents());
            }
        }

        /// <summary>
        /// The multithreaded task migration requires relative references to be resolved against the
        /// task's <see cref="TaskEnvironment.ProjectDirectory"/> rather than the shared process
        /// working directory. Two task instances pointed at different project directories must resolve
        /// the same relative reference independently.
        /// </summary>
        [Fact]
        public void ReferenceExistenceResolvesAgainstTaskProjectDirectory()
        {
            var directoryWithReference = Temp.CreateDirectory();
            var directoryWithoutReference = Temp.CreateDirectory();
            directoryWithReference.CreateFile("ref.dll");

            var taskInDirectoryWithReference = new TestableCsc()
            {
                BuildEngine = new MockEngine(TestOutputHelper),
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directoryWithReference.Path),
                References = MSBuildUtil.CreateTaskItems("ref.dll"),
            };
            var taskInDirectoryWithoutReference = new TestableCsc()
            {
                BuildEngine = new MockEngine(TestOutputHelper),
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directoryWithoutReference.Path),
                References = MSBuildUtil.CreateTaskItems("ref.dll"),
            };

            Assert.True(taskInDirectoryWithReference.CheckReferences());
            Assert.False(taskInDirectoryWithoutReference.CheckReferences());
        }

        private sealed class TestableCsc : Csc
        {
            public bool CheckReferences() => CheckAllReferencesExistOnDisk();
        }

        /// <summary>
        /// An empty reference <see cref="ITaskItem.ItemSpec"/> must be reported as a missing reference
        /// (matching the pre-migration <c>File.Exists</c> behavior) rather than throwing from
        /// <see cref="TaskEnvironment.GetAbsolutePath"/>.
        /// </summary>
        [Fact]
        public void EmptyReferenceItemSpecIsReportedAsMissingWithoutThrowing()
        {
            var engine = new MockEngine(TestOutputHelper);
            var task = new TestableCsc()
            {
                BuildEngine = engine,
                References = MSBuildUtil.CreateTaskItems(""),
            };

            Assert.False(task.CheckReferences());
            Assert.Contains("MSB3104", engine.Log);
        }

        [Fact]
        public void InvalidReferencePathIsReportedAsMissingWithoutThrowing()
        {
            var projectDirectory = Temp.CreateDirectory();
            var engine = new MockEngine(TestOutputHelper);
            var task = new TestableCsc()
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(projectDirectory.Path),
                References = MSBuildUtil.CreateTaskItems("bad|ref.dll"),
            };

            Assert.False(task.CheckReferences());
            Assert.Contains("MSB3104", engine.Log);
        }

        [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/79907")]
        public void StdLib()
        {
            var csc = new Csc
            {
                Sources = MSBuildUtil.CreateTaskItems("test.cs"),
            };

            AssertEx.Equal("/out:test.exe test.cs", csc.GenerateResponseFileContents());
        }

        [ConditionalFact(typeof(UnixLikeOnly)), WorkItem("https://github.com/dotnet/roslyn/issues/80865")]
        public void SourceFileInRootDirectoryOnUnix()
        {
            // On Unix, a source file path starting with "/" without another "/" 
            // should be prefixed with "./" to avoid being misinterpreted as a command-line switch
            var csc = new Csc
            {
                Sources = MSBuildUtil.CreateTaskItems("/Program.cs"),
            };

            var responseFileContents = csc.GenerateResponseFileContents();
            Assert.Contains("/./Program.cs", responseFileContents);
            Assert.DoesNotContain(" /Program.cs", responseFileContents);
        }

        [ConditionalFact(typeof(UnixLikeOnly)), WorkItem("https://github.com/dotnet/roslyn/issues/80865")]
        public void MultipleSourceFilesWithRootDirectoryOnUnix()
        {
            // Test multiple files where some are in root and some are not
            // Also test that /dir/file.cs is NOT transformed (has second '/')
            var csc = new Csc
            {
                Sources = MSBuildUtil.CreateTaskItems("/Program.cs", "src/Test.cs", "/App.cs", "/dir/File.cs"),
            };

            var responseFileContents = csc.GenerateResponseFileContents();
            Assert.Contains("/./Program.cs", responseFileContents);
            Assert.Contains("/./App.cs", responseFileContents);
            Assert.Contains(" src/Test.cs", responseFileContents);
            // /dir/File.cs should NOT be transformed (contains second '/')
            Assert.Contains(" /dir/File.cs", responseFileContents);
            Assert.DoesNotContain("/./dir/File.cs", responseFileContents);
        }

        [ConditionalFact(typeof(WindowsOnly)), WorkItem("https://github.com/dotnet/roslyn/issues/80865")]
        public void SourceFilePathsOnWindows()
        {
            // On Windows, paths should not be transformed even if they start with "/"
            var csc = new Csc
            {
                Sources = MSBuildUtil.CreateTaskItems("test.cs", "/test.cs"),
            };

            var responseFileContents = csc.GenerateResponseFileContents();
            Assert.Contains(" test.cs", responseFileContents);
            // On Windows, /test.cs should NOT be transformed
            Assert.Contains(" /test.cs", responseFileContents);
            Assert.DoesNotContain("/./test.cs", responseFileContents);
        }
    }
}
