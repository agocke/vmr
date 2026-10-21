// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks.UnitTests;

[TestClass]
public sealed class GivenAResolveNuGetAssets : SdkTest
{
    private const string Assets = """
        {
          "version": 4,
          "targets": {
            "net11.0": {
              "Package/1.2.3": {
                "type": "package",
                "compile": { "ref/net11.0/Package.dll": { "aliases": "custom", "related": ".xml" }, "ref/net11.0/_._": {} },
                "runtime": { "lib/net11.0/Package.dll": { "related": ".pdb" } },
                "native": { "native/library.so": {} },
                "resource": { "lib/net11.0/fr/package.resources.dll": { "locale": "fr" } },
                "runtimeTargets": { "runtimes/linux-x64/native/other.so": { "rid": "linux-x64", "assetType": "native" } },
                "analyzers": {
                  "analyzers/dotnet/roslyn4.0/cs/Generator.dll": { "codeLanguage": "cs", "compilerApiVersion": "roslyn4.0" },
                  "analyzers/dotnet/roslyn5.0/cs/Generator.dll": { "codeLanguage": "cs", "compilerApiVersion": "roslyn5.0" },
                  "analyzers/dotnet/roslyn4.0/vb/Generator.dll": { "codeLanguage": "vb", "compilerApiVersion": "roslyn4.0" }
                }
              },
              "Library/1.0.0": { "type": "project", "compile": { "bin/placeholder/Renamed.Library.dll": {} } }
            }
          },
          "libraries": {
            "Package/1.2.3": {
              "type": "package", "path": "package/1.2.3",
              "files": [
                "analyzers/dotnet/roslyn4.0/cs/Generator.dll", "analyzers/dotnet/roslyn5.0/cs/Generator.dll",
                "analyzers/dotnet/roslyn4.0/vb/Generator.dll"
              ]
            },
            "Library/1.0.0": { "type": "project", "path": "../Library/Library.csproj", "msbuildProject": "../Library/Library.csproj" }
          },
          "projectFileDependencyGroups": { "net11.0": [ "Package >= 1.2.3" ] },
          "packageFolders": {},
          "project": { "frameworks": { "net11.0": { "targetAlias": "net11.0" } }, "restore": { "restoreEnableAnalyzerAssets": false } },
          "logs": [ { "code": "NU1608", "level": "Warning", "message": "prepared warning", "libraryId": "Package", "targetGraphs": [ "net11.0" ] } ]
        }
        """;

    [TestMethod]
    [DataRow(false, "C#", "roslyn4.0", 4)]
    [DataRow(true, "C#", "roslyn4.0", 4)]
    [DataRow(false, "C#", "roslyn5.0", 4)]
    [DataRow(true, "C#", "roslyn5.0", 4)]
    [DataRow(false, "VB", "roslyn4.0", 3)]
    [DataRow(true, "VB", "roslyn4.0", 3)]
    public void ItMatchesOrdinaryPolicyAfterAssetsAndPackagesAreRemoved(bool analyzerAssets, string language, string compiler, int version)
    {
        using var fixture = Prepare(root =>
        {
            root["version"] = version;
            root["project"]!["restore"]!["restoreEnableAnalyzerAssets"] = analyzerAssets;
        });
        ResolvePackageAssets ordinary = new()
        {
            BuildEngine = new MockBuildEngine(),
            ProjectAssetsFile = fixture.Input,
            ProjectAssetsCacheFile = fixture.Input + ".cache",
            ProjectPath = Path.Combine(fixture.Directory, "App.csproj"),
            TargetFramework = "net11.0",
            ProjectLanguage = language,
            CompilerApiVersion = compiler,
            DefaultImplicitPackages = "",
            DotNetAppHostExecutableNameWithoutExtension = "apphost",
            DisablePackageAssetsCache = true,
            CopyLocalRuntimeTargetAssets = true,
            EmitAssetsLogMessages = true,
            PackageReferences = [PublishExcludedPackage()],
        };
        ordinary.Execute().Should().BeTrue();
        ResolveNuGetAssets prepared = CreateTask(fixture.Project);
        prepared.ProjectLanguage = language;
        prepared.CompilerApiVersion = compiler;
        prepared.PackageReferences = [PublishExcludedPackage()];
        File.Delete(fixture.Input);
        Directory.Move(Path.Combine(fixture.Directory, "packages"), Path.Combine(fixture.Directory, "unavailable-packages"));

        prepared.Execute().Should().BeTrue();

        Snapshot(prepared.Analyzers).Should().Equal(Snapshot(ordinary.Analyzers));
        Snapshot(prepared.CompileTimeAssemblies).Should().Equal(Snapshot(ordinary.CompileTimeAssemblies));
        Snapshot(prepared.RuntimeAssemblies).Should().Equal(Snapshot(ordinary.RuntimeAssemblies));
        Snapshot(prepared.NativeLibraries).Should().Equal(Snapshot(ordinary.NativeLibraries));
        Snapshot(prepared.ResourceAssemblies).Should().Equal(Snapshot(ordinary.ResourceAssemblies));
        Snapshot(prepared.RuntimeTargets).Should().Equal(Snapshot(ordinary.RuntimeTargets));
        Snapshot(prepared.DebugSymbolsFiles).Should().Equal(Snapshot(ordinary.DebugSymbolsFiles));
        Snapshot(prepared.ReferenceDocumentationFiles).Should().Equal(Snapshot(ordinary.ReferenceDocumentationFiles));
        Snapshot(prepared.TransitiveProjectReferences).Should().Equal(Snapshot(ordinary.TransitiveProjectReferences));
        Snapshot(prepared.PackageDependenciesDesignTime).Should().Equal(Snapshot(ordinary.PackageDependenciesDesignTime));
        prepared.Analyzers.Should().ContainSingle().Which.ItemSpec.Should()
            .EndWith(Path.Combine(compiler, language == "C#" ? "cs" : "vb", "Generator.dll"));
        prepared.CompileTimeAssemblies.Should().ContainSingle().Which.GetMetadata("Aliases").Should().Be("custom");
        prepared.RuntimeAssemblies.Should().ContainSingle().Which.GetMetadata("CopyToPublishDirectory").Should().Be("false");
        prepared.DebugSymbolsFiles.Should().ContainSingle();
        prepared.ReferenceDocumentationFiles.Should().BeEmpty();
        prepared.TransitiveProjectReferences.Should().ContainSingle().Which.ItemSpec.Should()
            .Be(Path.Combine("..", "Library", "Library.csproj"));
        ((MockBuildEngine)prepared.BuildEngine).Warnings.Should().ContainSingle().Which.Code.Should().Be("NU1608");
    }

    [TestMethod]
    [DataRow("context")]
    [DataRow("location")]
    [DataRow("relative-location")]
    [DataRow("related")]
    [DataRow("locale")]
    [DataRow("compiler")]
    [DataRow("library")]
    public void ItRejectsIncompletePreparationWithoutReturningPartialItems(string missing)
    {
        using var fixture = Prepare();
        ResolveNuGetAssets task = CreateTask(fixture.Project);
        switch (missing)
        {
            case "context": task.PackageContextVersion = "2"; break;
            case "location": task.PackageLocations = []; break;
            case "relative-location": task.PackageLocations[0].SetMetadata("PackageDirectory", "relative"); break;
            case "related": task.RelatedFiles = []; break;
            case "locale": task.Locales = []; break;
            case "compiler": task.CompilerApiVersion = ""; break;
            case "library":
                task.Facts = task.Facts.Where(item => !item.ItemSpec.StartsWith("$/libraries/Package~11.2.3", StringComparison.Ordinal)).ToArray();
                break;
        }

        AssertRejected(task, "NETSDK1253");
    }

    [TestMethod]
    [DataRow("content")]
    [DataRow("framework")]
    [DataRow("framework-assembly")]
    [DataRow("compile-conflict")]
    [DataRow("runtime-conflict")]
    [DataRow("analyzer-conflict")]
    [DataRow("analyzer-only-conflict")]
    [DataRow("native-only-conflict")]
    [DataRow("platform-conflict")]
    public void ItRejectsUnsupportedPolicyInsteadOfSilentlyDroppingAssets(string feature)
    {
        using var fixture = Prepare(root =>
        {
            JsonObject library = root["targets"]!["net11.0"]!["Package/1.2.3"]!.AsObject();
            switch (feature)
            {
                case "content":
                    library["contentFiles"] = JsonNode.Parse("""{ "contentFiles/cs/any/file.pp": { "buildAction": "Compile", "codeLanguage": "cs", "ppOutputPath": "generated/file.cs" } }""");
                    break;
                case "framework": library["frameworkReferences"] = new JsonArray("Microsoft.AspNetCore.App"); break;
                case "framework-assembly": library["frameworkAssemblies"] = new JsonArray("System.Xml"); break;
                case "compile-conflict": library["compile"]!["other/Package.dll"] = new JsonObject(); break;
                case "runtime-conflict":
                case "native-only-conflict": library["native"]!["other/library.so"] = new JsonObject(); break;
            }
            if (feature is "analyzer-only-conflict" or "native-only-conflict")
            {
                library.Remove("compile");
                library.Remove("runtime");
            }
        });
        ResolveNuGetAssets task = CreateTask(fixture.Project);
        if (feature == "platform-conflict")
        {
            task.FrameworkCompileReferences = [new TaskItem("/framework/Package.dll")];
        }
        if (feature is "analyzer-conflict" or "analyzer-only-conflict")
        {
            task.ExistingAnalyzers = [new TaskItem("/analyzers/Generator.dll")];
        }

        AssertRejected(task, "NETSDK1254");
    }

    [TestMethod]
    public void ItReplaysRestoreErrorsWithoutPublishingAnySelectedItems()
    {
        using var fixture = Prepare(root => root["logs"]![0]!["level"] = "Error");

        AssertRejected(CreateTask(fixture.Project), "NU1608");
    }

    [TestMethod]
    [DataRow("compiler")]
    [DataRow("package")]
    public void ItRejectsIncompleteDiscoveryWithoutReplacingPreviousFacts(string missing)
    {
        using var fixture = Prepare();
        string manifest = fixture.Project.FullPath;
        string previous = File.ReadAllText(manifest);
        if (missing == "package")
        {
            Directory.Move(Path.Combine(fixture.Directory, "packages"), Path.Combine(fixture.Directory, "unavailable-packages"));
        }
        WriteNuGetInputs capture = new()
        {
            BuildEngine = new MockBuildEngine(),
            InputFile = fixture.Input,
            OutputFile = manifest,
            CapturePackageContext = true,
            ProjectLanguage = "C#",
            CompilerApiVersion = missing == "compiler" ? "" : "roslyn4.0",
        };

        capture.Execute().Should().BeFalse();

        File.ReadAllText(manifest).Should().Be(previous);
        ((MockBuildEngine)capture.BuildEngine).Errors.Should().ContainSingle().Which.Code.Should().Be("NETSDK1060");
    }

    [TestMethod]
    public void ItCapturesTheSelectedFallbackRootRatherThanReplayingFolderSearch()
    {
        using var fixture = Prepare(root =>
        {
            string selectedRoot = root["packageFolders"]!.AsObject().Single().Key;
            root["packageFolders"] = new JsonObject
            {
                [Path.Combine(selectedRoot, "missing-first")] = new JsonObject(),
                [selectedRoot] = new JsonObject(),
            };
        });
        ResolveNuGetAssets task = CreateTask(fixture.Project);
        ITaskItem location = task.PackageLocations.Should().ContainSingle().Subject;
        location.GetMetadata("PackageDirectory").Should().Be(Path.Combine(fixture.Directory, "packages", "package", "1.2.3"));
        task.RelatedFiles.Select(item => item.GetMetadata("Exists")).Should().BeEquivalentTo("false", "true");
        Directory.Move(Path.Combine(fixture.Directory, "packages"), Path.Combine(fixture.Directory, "unavailable-packages"));

        task.Execute().Should().BeTrue();

        task.CompileTimeAssemblies.Should().ContainSingle().Which.ItemSpec.Should()
            .StartWith(location.GetMetadata("PackageDirectory"));
    }

    [TestMethod]
    [DataRow("fR-fR", "fr-FR")]
    [DataRow("/invalid", "/invalid")]
    [DataRow("", "")]
    public void ItUsesPreparedLocaleNormalizationAndInvalidCultureClassification(string locale, string expected)
    {
        using var fixture = Prepare(root =>
            root["targets"]!["net11.0"]!["Package/1.2.3"]!["resource"]!["lib/net11.0/fr/package.resources.dll"]!["locale"] = locale);
        ResolveNuGetAssets task = CreateTask(fixture.Project);

        task.Execute().Should().BeTrue();

        task.ResourceAssemblies.Should().ContainSingle().Which.GetMetadata("Culture").Should().Be(expected);
        if (locale != expected || locale == "/invalid")
        {
            ((MockBuildEngine)task.BuildEngine).Warnings.Should().HaveCount(2);
        }
    }

    [TestMethod]
    public void ItPreservesLiteralPathsAndMetadataWithoutTreatingThemAsExpressions()
    {
        const string relative = "ref/a;%24(Unknown).dll";
        const string aliases = "custom;%3b;$(Unknown)";
        using var fixture = Prepare(root =>
        {
            JsonObject compile = root["targets"]!["net11.0"]!["Package/1.2.3"]!["compile"]!.AsObject();
            compile.Clear();
            compile[relative] = new JsonObject { ["aliases"] = aliases };
        });
        ResolveNuGetAssets task = CreateTask(fixture.Project);
        File.Delete(fixture.Input);

        task.Execute().Should().BeTrue();

        ITaskItem reference = task.CompileTimeAssemblies.Should().ContainSingle().Subject;
        reference.ItemSpec.Should().EndWith(relative.Replace('/', Path.DirectorySeparatorChar));
        reference.GetMetadata("HintPath").Should().Be(reference.ItemSpec);
        reference.GetMetadata("Aliases").Should().Be(aliases);
    }

    private Fixture Prepare(Action<JsonObject>? change = null)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: Guid.NewGuid().ToString("N")).Path;
        string packages = Path.Combine(directory, "packages");
        string package = Path.Combine(packages, "package", "1.2.3");
        Directory.CreateDirectory(Path.Combine(package, "lib", "net11.0"));
        File.WriteAllText(Path.Combine(package, "package.1.2.3.nupkg.sha512"), "fixture");
        File.WriteAllText(Path.Combine(package, "lib", "net11.0", "Package.pdb"), "fixture");
        JsonObject root = JsonNode.Parse(Assets)!.AsObject();
        root["packageFolders"] = new JsonObject { [packages + Path.DirectorySeparatorChar] = new JsonObject() };
        change?.Invoke(root);
        string input = Path.Combine(directory, "project.assets.json");
        string output = Path.Combine(directory, "inputs.props");
        File.WriteAllText(input, root.ToJsonString());
        WriteNuGetInputs capture = new()
        {
            BuildEngine = new MockBuildEngine(),
            InputFile = input,
            OutputFile = output,
            CapturePackageContext = true,
            CompilerApiVersion = "roslyn4.0",
        };
        capture.Execute().Should().BeTrue();
        // Evaluate the emitted item/property representation, including literal escaping.
        return new Fixture(directory, input, new Project(output, null, null, new ProjectCollection()));
    }

    private sealed record Fixture(string Directory, string Input, Project Project) : IDisposable
    {
        public void Dispose() => Project.ProjectCollection.Dispose();
    }

    private static ResolveNuGetAssets CreateTask(Project project) => new()
    {
        BuildEngine = new MockBuildEngine(),
        FormatVersion = project.GetPropertyValue("_NuGetInputsFormatVersion"),
        PackageContextVersion = project.GetPropertyValue("_NuGetPackageContextVersion"),
        ProjectPath = Path.Combine(project.DirectoryPath, "App.csproj"),
        ProjectAssetsFile = project.GetPropertyValue("_NuGetInputsSourceFile"),
        ProjectLanguage = "C#",
        CompilerApiVersion = project.GetPropertyValue("_NuGetCompilerApiVersion"),
        TargetFramework = "net11.0",
        Facts = Items(project, "_NuGetInput"),
        PackageLocations = Items(project, "_NuGetPackageLocation"),
        RelatedFiles = Items(project, "_NuGetRelatedFile"),
        Locales = Items(project, "_NuGetLocale"),
        CopyLocalRuntimeTargetAssets = true,
        EmitAssetsLogMessages = true,
    };

    private static ITaskItem[] Items(Project project, string type) =>
        project.GetItems(type).Select(item => (ITaskItem)new TaskItem(ProjectCollection.Escape(item.EvaluatedInclude),
            item.Metadata.ToDictionary(metadata => metadata.Name, metadata => ProjectCollection.Escape(metadata.EvaluatedValue)))).ToArray();

    private static TaskItem PublishExcludedPackage()
    {
        TaskItem item = new("Package");
        item.SetMetadata("Publish", "false");
        return item;
    }

    private static IEnumerable<string> Snapshot(ITaskItem[] items) => items.Select(item => item.ItemSpec + "|" +
        string.Join("|", item.MetadataNames.Cast<string>().OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => $"{name}={item.GetMetadata(name)}")));

    private static void AssertRejected(ResolveNuGetAssets task, string code)
    {
        task.Execute().Should().BeFalse();
        task.Analyzers.Should().BeEmpty();
        task.CompileTimeAssemblies.Should().BeEmpty();
        task.RuntimeAssemblies.Should().BeEmpty();
        task.NativeLibraries.Should().BeEmpty();
        task.ResourceAssemblies.Should().BeEmpty();
        task.RuntimeTargets.Should().BeEmpty();
        task.TransitiveProjectReferences.Should().BeEmpty();
        task.PackageDependencies.Should().BeEmpty();
        ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle().Which.Code.Should().Be(code);
    }
}
