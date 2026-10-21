// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Microsoft.Build.Evaluation;

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

    private static WriteNuGetInputs Write(string inputFile, string outputFile) => new()
    {
        BuildEngine = new MockBuildEngine(),
        InputFile = inputFile,
        OutputFile = outputFile,
    };
}
