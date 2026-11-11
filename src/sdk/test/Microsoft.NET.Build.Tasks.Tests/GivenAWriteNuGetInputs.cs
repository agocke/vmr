// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks.UnitTests;

[TestClass]
public sealed class GivenAWriteNuGetInputs : SdkTest
{
    [TestMethod]
    public void ItPreservesUnderlyingFactsWithoutSelectingAssetsOrReadingProjectOutputs()
    {
        string directory = TestAssetsManager.CreateTestDirectory().Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string outputFile = Path.Combine(directory, "inputs", "nuget.props");
        File.WriteAllText(inputFile, """
            {
              "targets": {
                "net10.0": {
                  "Package/1.0.0": {
                    "compile": { "ref/net10.0/Package.dll": {} },
                    "runtime": { "lib/net10.0/Package.dll": {} },
                    "dependencies": { "Other": "2.0.0" }
                  },
                  "Library/1.0.0": {
                    "type": "project",
                    "compile": { "bin/placeholder/Library.dll": {} }
                  }
                },
                "net10.0/linux-x64": {
                  "Package/1.0.0": {
                    "runtimeTargets": {
                      "runtimes/linux-x64/native/library.so": { "rid": "linux-x64", "assetType": "native" }
                    }
                  }
                },
                "net11.0": {}
              },
              "libraries": {
                "Package/1.0.0": { "files": [ "ref/net10.0/Package.dll", "analyzers/dotnet/cs/Generator.dll" ] },
                "Library/1.0.0": { "type": "project", "msbuildProject": "../Library.csproj" }
              },
              "packageFolders": { "/second/cache/": {}, "/first/cache/": {} },
              "future": { "null": null, "emptyString": "", "emptyArray": [], "emptyObject": {},
                          "true": true, "false": false, "number": 12.500e+200,
                          "largeNumber": 123456789012345678901234567890, "A": "upper", "a": "lower" }
            }
            """);

        Write(inputFile, outputFile).Execute().Should().BeTrue();
        File.Delete(inputFile);

        using ProjectCollection collection = new();
        Project project = collection.LoadProject(outputFile);
        var facts = project.GetItems("_NuGetInput").ToDictionary(item => item.EvaluatedInclude, StringComparer.Ordinal);
        facts["$/targets/net10.0/Package~11.0.0/compile/ref~1net10.0~1Package.dll"].GetMetadataValue("Kind").Should().Be("Object");
        facts["$/targets/net10.0/Package~11.0.0/runtime/lib~1net10.0~1Package.dll"].GetMetadataValue("Kind").Should().Be("Object");
        facts["$/targets/net10.0/Package~11.0.0/dependencies/Other"].GetMetadataValue("Value").Should().Be("2.0.0");
        facts["$/targets/net10.0/Library~11.0.0/compile/bin~1placeholder~1Library.dll"].GetMetadataValue("Kind").Should().Be("Object");
        facts["$/targets/net10.0~1linux-x64/Package~11.0.0/runtimeTargets/runtimes~1linux-x64~1native~1library.so/rid"].GetMetadataValue("Value").Should().Be("linux-x64");
        facts["$/targets/net11.0"].GetMetadataValue("Kind").Should().Be("Object");
        facts["$/libraries/Library~11.0.0/msbuildProject"].GetMetadataValue("Value").Should().Be("../Library.csproj");
        facts["$/libraries/Package~11.0.0/files/0"].GetMetadataValue("Value").Should().Be("ref/net10.0/Package.dll");
        facts["$/libraries/Package~11.0.0/files/1"].GetMetadataValue("Value").Should().Be("analyzers/dotnet/cs/Generator.dll");
        facts["$/future/null"].GetMetadataValue("Kind").Should().Be("Null");
        facts["$/future/emptyString"].GetMetadataValue("Kind").Should().Be("String");
        facts["$/future/emptyString"].GetMetadataValue("Value").Should().BeEmpty();
        facts["$/future/emptyArray"].GetMetadataValue("Kind").Should().Be("Array");
        facts["$/future/emptyObject"].GetMetadataValue("Kind").Should().Be("Object");
        facts["$/future/true"].GetMetadataValue("Value").Should().Be("true");
        facts["$/future/false"].GetMetadataValue("Value").Should().Be("false");
        facts["$/future/number"].GetMetadataValue("Value").Should().Be("12.500e+200");
        facts["$/future/largeNumber"].GetMetadataValue("Value").Should().Be("123456789012345678901234567890");
        facts["$/future/A"].GetMetadataValue("Value").Should().Be("upper");
        facts["$/future/a"].GetMetadataValue("Value").Should().Be("lower");
        project.GetItems("_NuGetInput").Where(item => item.EvaluatedInclude.StartsWith("$/packageFolders/", StringComparison.Ordinal))
            .Select(item => item.EvaluatedInclude).Should().Equal("$/packageFolders/~1second~1cache~1", "$/packageFolders/~1first~1cache~1");
        project.GetPropertyValue("SkipResolvePackageAssets").Should().BeEmpty();
        Directory.Exists(Path.Combine(directory, "bin")).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ItEscapesLiteralKeysAndValuesForFreshMSBuildEvaluation(bool emitBom)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: emitBom.ToString()).Path;
        string inputFile = Path.Combine(directory, "project %24 $(literal).assets.json");
        string outputFile = Path.Combine(directory, "nuget.props");
        const string key = "~/$([System.IO.File]::ReadAllText('missing'));%(Identity)@(*)%24\\windows\u0001";
        const string value = " \t\r\n$(Configuration);%3b;@(Reference);%(Identity);<>&\"'\u00e9\uD83D\uDE00\u0000\u0001\u000b\u000c\u001f\r\n ";
        File.WriteAllText(inputFile, JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value }), new UTF8Encoding(emitBom));

        Write(inputFile, outputFile).Execute().Should().BeTrue();

        using ProjectCollection collection = new();
        Project project = collection.LoadProject(outputFile);
        var facts = project.GetItems("_NuGetInput").ToArray();
        facts.Should().HaveCount(2);
        facts[1].EvaluatedInclude.Should().Be("$/~0~1$([System.IO.File]::ReadAllText('missing'));%(Identity)@(*)%24\\windows\u0001");
        facts[1].GetMetadataValue("Value").Should().Be(value);
        project.GetPropertyValue("_NuGetInputsSourceFile").Should().Be(inputFile);
    }

    [TestMethod]
    public void ItReadsChangedContentsDespiteUnchangedTimestamps()
    {
        string directory = TestAssetsManager.CreateTestDirectory().Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string outputFile = Path.Combine(directory, "nuget.props");
        File.WriteAllText(inputFile, """{"version":"first"}""");
        DateTime timestamp = File.GetLastWriteTimeUtc(inputFile);
        Write(inputFile, outputFile).Execute().Should().BeTrue();

        File.WriteAllText(inputFile, """{"version":"other"}""");
        File.SetLastWriteTimeUtc(inputFile, timestamp);
        Write(inputFile, outputFile).Execute().Should().BeTrue();

        using ProjectCollection collection = new();
        Project project = collection.LoadProject(outputFile);
        project.GetItems("_NuGetInput").Single(item => item.EvaluatedInclude == "$/version")
            .GetMetadataValue("Value").Should().Be("other");
    }

    [TestMethod]
    [DataRow(null, "missing")]
    [DataRow("{", "truncated")]
    [DataRow("{} {}", "extra-root")]
    public void ItReportsUnreadableInputsWithoutPublishingAReplacement(string? json, string identifier)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: identifier).Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string outputFile = Path.Combine(directory, "nuget.props");
        if (json != null)
        {
            File.WriteAllText(inputFile, json);
        }

        var task = Write(inputFile, outputFile);
        task.Execute().Should().BeFalse();
        File.Exists(outputFile).Should().BeFalse();
        var error = ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle().Which;
        error.Code.Should().Be("NETSDK1060");
        error.Message.Should().Contain(inputFile);

        File.WriteAllText(outputFile, "previous successful capture");
        Write(inputFile, outputFile).Execute().Should().BeFalse();
        File.ReadAllText(outputFile).Should().Be("previous successful capture");
    }

    [TestMethod]
    public void ItDeduplicatesEquivalentFrameworkRequests()
    {
        string directory = TestAssetsManager.CreateTestDirectory().Path;
        string inputFile = Path.Combine(directory, "project %24 $(literal).assets.json");
        string outputFile = Path.Combine(directory, "inputs", "nuget.props");
        WriteEmptyAssets(inputFile);
        var task = WriteRequests(
            Request(inputFile, outputFile, framework: "net10.0"),
            Request(inputFile, outputFile, framework: "net11.0"));

        task.Execute().Should().BeTrue();
        task.WrittenModels.Should().Be(1);
        ((MockBuildEngine)task.BuildEngine).Errors.Should().BeEmpty();
        File.Delete(inputFile);

        using ProjectCollection collection = new();
        Project project = collection.LoadProject(outputFile);
        project.GetPropertyValue("_NuGetInputsSourceFile").Should().Be(inputFile);
        project.GetPropertyValue("_NuGetPackageContextVersion").Should().Be("1");
        project.GetPropertyValue("_NuGetConflictFactsVersion").Should().Be("1");
        project.GetPropertyValue("_NuGetCompilerApiVersion").Should().Be("roslyn4.0");
        project.GetItems("_NuGetInput").Single(item => item.EvaluatedInclude == "$/targets/net10.0")
            .GetMetadataValue("Kind").Should().Be("Object");
        project.GetItems("_NuGetInput").Single(item => item.EvaluatedInclude == "$/targets/net11.0")
            .GetMetadataValue("Kind").Should().Be("Object");
    }

    [TestMethod]
    public void ItWritesEveryRequestedDestinationWithItsOwnAssetsFacts()
    {
        string directory = TestAssetsManager.CreateTestDirectory().Path;
        string firstInput = Path.Combine(directory, "first.assets.json");
        string secondInput = Path.Combine(directory, "second.assets.json");
        WriteEmptyAssets(firstInput, "1.0.0");
        WriteEmptyAssets(secondInput, "2.0.0");
        var bindings = new[]
        {
            (Input: firstInput, Output: Path.Combine(directory, "first.props"), Version: "1.0.0"),
            (Input: firstInput, Output: Path.Combine(directory, "nested", "second.props"), Version: "1.0.0"),
            (Input: secondInput, Output: Path.Combine(directory, "third.props"), Version: "2.0.0"),
        };
        var task = WriteRequests(bindings.Select(binding => Request(binding.Input, binding.Output)).ToArray());

        task.Execute().Should().BeTrue();
        task.WrittenModels.Should().Be(3);
        File.Delete(firstInput);
        File.Delete(secondInput);

        using ProjectCollection collection = new();
        foreach (var binding in bindings)
        {
            Project project = collection.LoadProject(binding.Output);
            project.GetPropertyValue("_NuGetInputsSourceFile").Should().Be(binding.Input);
            project.GetPropertyValue("_NuGetPackageContextVersion").Should().Be("1");
            project.GetItems("_NuGetInput").Single(item => item.EvaluatedInclude == "$/project/version")
                .GetMetadataValue("Value").Should().Be(binding.Version);
        }
    }

    [TestMethod]
    [DataRow("assets")]
    [DataRow("compiler")]
    [DataRow("language")]
    public void ItRejectsIncompatibleBindingsBeforeOverwritingAnyOutput(string conflict)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: conflict).Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string otherInput = Path.Combine(directory, "other.assets.json");
        string priorOutput = Path.Combine(directory, "prior.props");
        string sharedOutput = Path.Combine(directory, "shared.props");
        WriteEmptyAssets(inputFile);
        WriteEmptyAssets(otherInput, "2.0.0");
        File.WriteAllText(priorOutput, "prior successful capture");
        File.WriteAllText(sharedOutput, "shared successful capture");
        var task = WriteRequests(
            Request(inputFile, priorOutput),
            Request(inputFile, sharedOutput),
            Request(conflict == "assets" ? otherInput : inputFile, sharedOutput,
                compiler: conflict == "compiler" ? "roslyn5.0" : "roslyn4.0",
                language: conflict == "language" ? "VB" : "C#"));

        task.Execute().Should().BeFalse();
        task.WrittenModels.Should().Be(0);
        File.ReadAllText(priorOutput).Should().Be("prior successful capture");
        File.ReadAllText(sharedOutput).Should().Be("shared successful capture");
        var error = ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle().Which;
        error.Code.Should().Be("NETSDK1060");
        error.Message.Should().Contain(sharedOutput);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ItRejectsOutputsThatReplaceAnyRequestsAssets(bool crossRequest)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: crossRequest.ToString()).Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string otherInput = Path.Combine(directory, "other.assets.json");
        string priorOutput = Path.Combine(directory, "prior.props");
        string otherOutput = Path.Combine(directory, "other.props");
        WriteEmptyAssets(inputFile);
        WriteEmptyAssets(otherInput, "2.0.0");
        string originalInput = File.ReadAllText(inputFile);
        string originalOtherInput = File.ReadAllText(otherInput);
        File.WriteAllText(priorOutput, "prior successful capture");
        var requests = new List<TaskItem>
        {
            Request(inputFile, priorOutput),
            Request(inputFile, crossRequest ? otherInput : inputFile),
        };
        if (crossRequest)
        {
            requests.Add(Request(otherInput, otherOutput));
        }
        var task = WriteRequests(requests.ToArray());

        task.Execute().Should().BeFalse();
        task.WrittenModels.Should().Be(0);
        File.ReadAllText(priorOutput).Should().Be("prior successful capture");
        File.ReadAllText(inputFile).Should().Be(originalInput);
        File.ReadAllText(otherInput).Should().Be(originalOtherInput);
        File.Exists(otherOutput).Should().BeFalse();
        var error = ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle().Which;
        error.Code.Should().Be("NETSDK1060");
        error.Message.Should().Contain(crossRequest ? otherInput : inputFile);
    }

    [TestMethod]
    [DataRow("C#")]
    [DataRow("VB")]
    public void ItRejectsMissingCompilerIdentityBeforeWritingAnyOutput(string language)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: language).Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string priorOutput = Path.Combine(directory, "prior.props");
        string outputFile = Path.Combine(directory, "nuget.props");
        WriteEmptyAssets(inputFile);
        File.WriteAllText(priorOutput, "prior successful capture");
        var task = WriteRequests(
            Request(inputFile, priorOutput),
            Request(inputFile, outputFile, compiler: "", language: language));

        task.Execute().Should().BeFalse();
        task.WrittenModels.Should().Be(0);
        File.ReadAllText(priorOutput).Should().Be("prior successful capture");
        File.Exists(outputFile).Should().BeFalse();
        ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle()
            .Which.Code.Should().Be("NETSDK1060");
    }

    [TestMethod]
    [DataRow(null, "missing")]
    [DataRow("{", "malformed")]
    public void ItPreservesAnExistingModelWhenAcquiredAssetsCannotBeRead(string? json, string identifier)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: identifier).Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string outputFile = Path.Combine(directory, "nuget.props");
        if (json is not null)
        {
            File.WriteAllText(inputFile, json);
        }
        File.WriteAllText(outputFile, "previous successful capture");
        var task = WriteRequests(Request(inputFile, outputFile));

        task.Execute().Should().BeFalse();
        task.WrittenModels.Should().Be(0);
        File.ReadAllText(outputFile).Should().Be("previous successful capture");
        var error = ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle().Which;
        error.Code.Should().Be("NETSDK1060");
        error.Message.Should().Contain(inputFile);
    }

    [TestMethod]
    public void ItPreservesAcquiredPackageFolderPrecedenceInAFreshProjectCollection()
    {
        string directory = TestAssetsManager.CreateTestDirectory().Path;
        string inputFile = Path.Combine(directory, "project.assets.json");
        string outputFile = Path.Combine(directory, "nuget.props");
        string preferredFolder = Path.Combine(directory, "z preferred %24 $(literal)");
        string fallbackFolder = Path.Combine(directory, "a fallback");
        Directory.CreateDirectory(preferredFolder);
        Directory.CreateDirectory(fallbackFolder);
        WriteEmptyAssets(inputFile, "1.0.0", preferredFolder, fallbackFolder);
        var task = WriteRequests(Request(inputFile, outputFile));

        task.Execute().Should().BeTrue();
        task.WrittenModels.Should().Be(1);
        File.Delete(inputFile);
        Directory.Delete(preferredFolder);
        Directory.Delete(fallbackFolder);

        using ProjectCollection collection = new();
        Project project = collection.LoadProject(outputFile);
        project.GetItems("_NuGetAcquiredPackageFolder").Select(item => item.EvaluatedInclude)
            .Should().Equal(preferredFolder, fallbackFolder);
        project.GetPropertyValue("_NuGetPackageContextVersion").Should().Be("1");
    }

    private static void WriteEmptyAssets(string inputFile, string projectVersion = "1.0.0", params string[] packageFolders)
    {
        File.WriteAllText(inputFile, $$"""
            {
              "version": 3,
              "targets": { "net10.0": {}, "net11.0": {} },
              "libraries": {},
              "packageFolders": {{JsonSerializer.Serialize(packageFolders.ToDictionary(folder => folder, _ => new { }))}},
              "project": {
                "version": {{JsonSerializer.Serialize(projectVersion)}},
                "frameworks": {
                  "net10.0": { "targetAlias": "net10.0" },
                  "net11.0": { "targetAlias": "net11.0" }
                }
              }
            }
            """);
    }

    private static TaskItem Request(string inputFile, string outputFile, string compiler = "roslyn4.0",
        string language = "C#", string framework = "net10.0")
    {
        var request = new TaskItem(ProjectCollection.Escape("App-" + framework));
        request.SetMetadata("AssetsFile", ProjectCollection.Escape(inputFile));
        request.SetMetadata("OutputFile", ProjectCollection.Escape(outputFile));
        request.SetMetadata("CompilerApiVersion", ProjectCollection.Escape(compiler));
        request.SetMetadata("ProjectLanguage", ProjectCollection.Escape(language));
        request.SetMetadata("TargetFramework", ProjectCollection.Escape(framework));
        return request;
    }

    private static WriteNuGetRestoreInputs WriteRequests(params TaskItem[] requests) => new()
    {
        BuildEngine = new MockBuildEngine(),
        Requests = requests,
    };

    private static WriteNuGetInputs Write(string inputFile, string outputFile) => new()
    {
        BuildEngine = new MockBuildEngine(),
        InputFile = inputFile,
        OutputFile = outputFile,
    };
}
