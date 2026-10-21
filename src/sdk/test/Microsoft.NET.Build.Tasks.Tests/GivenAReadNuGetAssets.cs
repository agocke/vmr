// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks.UnitTests;

[TestClass]
public sealed class GivenAReadNuGetAssets : SdkTest
{
    private const string AssetsJson = """
        {
          "version": 4,
          "targets": {
            "net11.0": {
              "Package/1.2.3": {
                "type": "package",
                "dependencies": { "Other": "[2.0.0, 3.0.0)" },
                "compile": { "ref/net11.0/Package.dll": { "aliases": "custom" }, "ref/net11.0/_._": {} },
                "runtime": { "lib/net11.0/Package.dll": { "related": ".xml;.pdb" } },
                "native": { "native/library.so": {} },
                "resource": { "lib/net11.0/fr/package.resources.dll": { "locale": "fr" } },
                "runtimeTargets": {
                  "runtimes/linux-x64/native/library.so": { "rid": "linux-x64", "assetType": "native" }
                },
                "contentFiles": {
                  "contentFiles/cs/any/file.pp": {
                    "buildAction": "Compile", "codeLanguage": "cs", "copyToOutput": true, "flatten": false,
                    "outputPath": "generated/file.cs", "ppOutputPath": "generated/file.pp"
                  }
                },
                "analyzers": { "analyzers/dotnet/roslyn4.0/cs/Generator.dll": {} },
                "future": { "A": "upper", "a": "lower", "array": [null, "", {}, [], 12.50e+100] }
              },
              "Library/1.0.0": {
                "type": "project", "compile": { "bin/placeholder/Renamed.Library.dll": {} }
              }
            },
            "net11.0/linux-x64": {
              "Package/1.2.3": {
                "type": "package", "runtime": { "runtimes/linux-x64/lib/net11.0/Package.dll": {} }
              }
            },
            "net10.0": {}
          },
          "libraries": {
            "Package/1.2.3": {
              "type": "package", "path": "package/1.2.3",
              "files": ["ref/net11.0/Package.dll", "analyzers/dotnet/roslyn4.0/cs/Generator.dll"]
            },
            "Library/1.0.0": { "type": "project", "path": "../Library/Library.csproj", "msbuildProject": "../Library/Library.csproj" }
          },
          "packageFolders": { "/nonexistent/cache/": {} }
        }
        """;

    [TestMethod]
    [DataRow(3, ".NETCoreApp,Version=v11.0")]
    [DataRow(4, "net11.0")]
    public void ItReadsOnlyTheExplicitTargetWithoutOpeningAssetsOrPackageDirectories(int version, string targetName)
    {
        string json = AssetsJson.Replace("\"version\": 4", $"\"version\": {version}")
            .Replace("\"net11.0\":", $"\"{targetName}\":");
        ReadNuGetAssets task = CreateTask(Capture(json), targetName);

        task.Execute().Should().BeTrue();

        task.Libraries.Select(item => item.ItemSpec).Should().Equal("Package/1.2.3", "Library/1.0.0");
        task.Libraries[0].GetMetadata("Name").Should().Be("Package");
        task.Libraries[0].GetMetadata("Version").Should().Be("1.2.3");
        task.Libraries[0].GetMetadata("Path").Should().Be("package/1.2.3");
        task.Libraries[1].GetMetadata("MSBuildProject").Should().Be("../Library/Library.csproj");
        task.Assets.Select(item => item.GetMetadata("AssetGroup")).Should().Equal(
            "compile", "compile", "runtime", "native", "resource", "runtimeTargets", "contentFiles", "analyzers", "compile");
        task.Assets[0].GetMetadata("Aliases").Should().Be("custom");
        task.Assets[1].ItemSpec.Should().Be("ref/net11.0/_._");
        task.Assets[2].GetMetadata("Related").Should().Be(".xml;.pdb");
        task.Assets[4].GetMetadata("Locale").Should().Be("fr");
        task.Assets[5].GetMetadata("RuntimeIdentifier").Should().Be("linux-x64");
        task.Assets[5].GetMetadata("AssetType").Should().Be("native");
        task.Assets[6].GetMetadata("BuildAction").Should().Be("Compile");
        task.Assets[6].GetMetadata("CodeLanguage").Should().Be("cs");
        task.Assets[6].GetMetadata("CopyToOutput").Should().Be("true");
        task.Assets[6].GetMetadata("Flatten").Should().Be("false");
        task.Assets[6].GetMetadata("OutputPath").Should().Be("generated/file.cs");
        task.Assets[6].GetMetadata("PPOutputPath").Should().Be("generated/file.pp");
        task.Assets[8].ItemSpec.Should().Be("bin/placeholder/Renamed.Library.dll");
        task.Assets[8].GetMetadata("LibraryType").Should().Be("project");
        task.Assets.Should().OnlyContain(item => item.GetMetadata("TargetName") == targetName);
        task.Dependencies.Should().ContainSingle().Which.GetMetadata("VersionRange").Should().Be("[2.0.0, 3.0.0)");
        task.Files.Select(item => item.ItemSpec).Should().Equal("ref/net11.0/Package.dll", "analyzers/dotnet/roslyn4.0/cs/Generator.dll");
    }

    [TestMethod]
    [DataRow("net11.0/linux-x64", 1)]
    [DataRow("net10.0", 0)]
    public void ItDistinguishesAnExplicitRidTargetFromAnEmptyTarget(string target, int assetCount)
    {
        ReadNuGetAssets task = CreateTask(Capture(AssetsJson), target);

        task.Execute().Should().BeTrue();

        task.Assets.Should().HaveCount(assetCount);
        if (assetCount != 0)
        {
            task.Assets[0].ItemSpec.Should().Be("runtimes/linux-x64/lib/net11.0/Package.dll");
            task.Assets[0].GetMetadata("FactPath").Should()
                .Be("$/targets/net11.0~1linux-x64/Package~11.2.3/runtime/runtimes~1linux-x64~1lib~1net11.0~1Package.dll");
        }
    }

    [TestMethod]
    public void ItPreservesLiteralAssetIdentitiesAndMetadataAcrossMSBuildEscaping()
    {
        const string assetPath = "ref/a;$(Configuration)@(*)%24~\u00e9.dll";
        const string aliases = "alias;%3b;$(Configuration);@(Reference);%(Identity)\u0001";
        string json = JsonSerializer.Serialize(new
        {
            version = 4,
            targets = new Dictionary<string, object>
            {
                ["net11.0"] = new Dictionary<string, object>
                {
                    ["Package/1.0.0"] = new
                    {
                        type = "package",
                        compile = new Dictionary<string, object> { [assetPath] = new { aliases } },
                    },
                },
            },
            libraries = new Dictionary<string, object> { ["Package/1.0.0"] = new { type = "package" } },
        });
        ReadNuGetAssets task = CreateTask(Capture(json));

        task.Execute().Should().BeTrue();

        task.Assets.Should().ContainSingle().Which.ItemSpec.Should().Be(assetPath);
        task.Assets[0].GetMetadata("Aliases").Should().Be(aliases);
    }

    [TestMethod]
    [DataRow("format")]
    [DataRow("assets-version")]
    [DataRow("missing-target")]
    [DataRow("missing-library")]
    [DataRow("library-type")]
    [DataRow("asset-kind")]
    [DataRow("metadata-kind")]
    [DataRow("dependency-kind")]
    [DataRow("inventory-kind")]
    public void ItRejectsInvalidSemanticViewsWithoutReturningPartialItems(string invalid)
    {
        string json = invalid switch
        {
            "assets-version" => AssetsJson.Replace("\"version\": 4", "\"version\": 5"),
            "missing-library" => AssetsJson.Replace("\"libraries\": {", "\"unusedLibraries\": {"),
            "library-type" => AssetsJson.Replace("\"type\": \"project\", \"path\":", "\"type\": \"package\", \"path\":"),
            "asset-kind" => AssetsJson.Replace("\"bin/placeholder/Renamed.Library.dll\": {}", "\"bin/placeholder/Renamed.Library.dll\": null"),
            "metadata-kind" => AssetsJson.Replace("\"copyToOutput\": true", "\"copyToOutput\": \"true\""),
            "dependency-kind" => AssetsJson.Replace("\"Other\": \"[2.0.0, 3.0.0)\"", "\"Other\": []"),
            "inventory-kind" => AssetsJson.Replace("\"files\": [", "\"files\": {\"unexpected\": [").Replace("Generator.dll\"]", "Generator.dll\"]}"),
            _ => AssetsJson,
        };
        ReadNuGetAssets task = CreateTask(Capture(json), invalid == "missing-target" ? "missing" : "net11.0");
        if (invalid == "format")
        {
            task.FormatVersion = "2";
        }

        AssertRejected(task);
    }

    [TestMethod]
    [DataRow("no-root")]
    [DataRow("duplicate-root")]
    [DataRow("missing-parent")]
    [DataRow("out-of-order")]
    [DataRow("duplicate")]
    [DataRow("pointer")]
    [DataRow("array-index")]
    [DataRow("scalar-child")]
    [DataRow("kind")]
    [DataRow("numeric-kind")]
    [DataRow("number")]
    [DataRow("boolean")]
    [DataRow("container-value")]
    public void ItRejectsMalformedFactTreesWithoutReturningPartialItems(string invalid)
    {
        List<ITaskItem> facts = Capture(AssetsJson).ToList();
        ITaskItem Find(string path) => facts.Single(item => item.ItemSpec == path);
        switch (invalid)
        {
            case "no-root":
                facts.Clear();
                break;
            case "duplicate-root":
                facts.Add(new TaskItem(facts[0]));
                break;
            case "missing-parent":
                facts.Remove(Find("$/targets"));
                break;
            case "out-of-order":
                ITaskItem child = Find("$/targets/net11.0/Package~11.2.3/compile/ref~1net11.0~1Package.dll/aliases");
                facts.Remove(child);
                facts.Add(child);
                break;
            case "duplicate":
                facts.Add(new TaskItem(facts[^1]));
                break;
            case "pointer":
                Find("$/packageFolders/~1nonexistent~1cache~1").ItemSpec = "$/packageFolders/~2";
                break;
            case "array-index":
                Find("$/libraries/Package~11.2.3/files/0").ItemSpec = "$/libraries/Package~11.2.3/files/1";
                break;
            case "scalar-child":
                facts.Add(new TaskItem("$/packageFolders/~1nonexistent~1cache~1/child/descendant",
                    new Dictionary<string, string> { ["Kind"] = "String" }));
                break;
            case "kind":
                facts[^1].SetMetadata("Kind", "Unknown");
                break;
            case "numeric-kind":
                facts[^1].SetMetadata("Kind", "1");
                break;
            case "number":
                Find("$/version").SetMetadata("Value", "NaN");
                break;
            case "boolean":
                Find("$/targets/net11.0/Package~11.2.3/contentFiles/contentFiles~1cs~1any~1file.pp/copyToOutput").SetMetadata("Value", "True");
                break;
            case "container-value":
                facts[0].SetMetadata("Value", "unexpected");
                break;
        }

        AssertRejected(CreateTask(facts.ToArray()));
    }

    private ITaskItem[] Capture(string json)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: Guid.NewGuid().ToString("N")).Path;
        string input = Path.Combine(directory, "project.assets.json");
        string output = Path.Combine(directory, "inputs.props");
        File.WriteAllText(input, json);
        new WriteNuGetInputs { BuildEngine = new MockBuildEngine(), InputFile = input, OutputFile = output }
            .Execute().Should().BeTrue();
        File.Delete(input);

        using ProjectCollection collection = new();
        return collection.LoadProject(output).GetItems("_NuGetInput").Select(item => (ITaskItem)new TaskItem(
            ProjectCollection.Escape(item.EvaluatedInclude),
            new Dictionary<string, string>
            {
                ["Kind"] = ProjectCollection.Escape(item.GetMetadataValue("Kind")),
                ["Value"] = ProjectCollection.Escape(item.GetMetadataValue("Value")),
            })).ToArray();
    }

    private static ReadNuGetAssets CreateTask(ITaskItem[] facts, string target = "net11.0") => new()
    {
        BuildEngine = new MockBuildEngine(),
        FormatVersion = "1",
        Facts = facts,
        TargetName = target,
    };

    private static void AssertRejected(ReadNuGetAssets task)
    {
        task.Execute().Should().BeFalse();
        task.Libraries.Should().BeEmpty();
        task.Assets.Should().BeEmpty();
        task.Dependencies.Should().BeEmpty();
        task.Files.Should().BeEmpty();
        ((MockBuildEngine)task.BuildEngine).Errors.Should().ContainSingle().Which.Code.Should().Be("NETSDK1253");
    }
}
