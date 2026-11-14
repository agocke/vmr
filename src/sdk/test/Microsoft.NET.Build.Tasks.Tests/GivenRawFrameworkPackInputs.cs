// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;
using Microsoft.NET.Build.Tasks.ConflictResolution;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NuGet.RuntimeModel;

namespace Microsoft.NET.Build.Tasks.UnitTests;

[TestClass]
public class GivenRawFrameworkPackInputs
{
    [TestMethod]
    [DataRow("myplatform", "01.0", "Installed;100%$(Literal)")]
    [DataRow("MyPlatform", "1.0.0", "")]
    [DataRow("MyPlatform", "1.0.0.0", "MyPlatform 1.0.0.0")]
    [DataRow("Unknown", "1.0", "Unknown 1.0")]
    [DataRow("MyPlatform", "invalid", "MyPlatform invalid")]
    [DataRow("MyPlatform", "", "MyPlatform ")]
    [DataRow("Alias", "2.0", "Alias SDK")]
    public void PlatformDisplayNameSelectionUsesOnlyNativeFacts(string identifier, string version, string expected)
    {
        var engine = new RecordingEngine();
        var select = new GetPlatformSDKDisplayNameFromInputs
        {
            BuildEngine = engine,
            InputsVersion = "1",
            TargetPlatformIdentifier = identifier,
            TargetPlatformVersion = version,
            SdkDisplayNames = [
                Item("MyPlatform", ("Version", "1.0"), ("DisplayName", "Installed;100%$(Literal)")),
                Item("MyPlatform", ("Version", "1.0.0"), ("DisplayName", "")),
                Item("Alias", ("Version", "2.0"), ("DisplayName", "Alias SDK"))]
        };
        Assert.IsTrue(select.Execute(), string.Join("\n", engine.Errors));
        Assert.AreEqual(expected, select.DisplayName);
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("unsupported-version")]
    [DataRow("duplicate")]
    [DataRow("missing-name")]
    [DataRow("bad-version")]
    public void PlatformDisplayNameSelectionRejectsInvalidFacts(string invalid)
    {
        ITaskItem row = Item("MyPlatform", ("Version", "1.0"), ("DisplayName", "SDK"));
        var engine = new RecordingEngine();
        var select = new GetPlatformSDKDisplayNameFromInputs
        {
            BuildEngine = engine,
            InputsVersion = invalid == "version" ? "" : invalid == "unsupported-version" ? "2" : "1",
            TargetPlatformIdentifier = "MyPlatform",
            TargetPlatformVersion = "1.0",
            SdkDisplayNames = invalid == "duplicate"
                ? [row, Item("myplatform", ("Version", "01.0"), ("DisplayName", "Other SDK"))] : [row]
        };
        if (invalid == "missing-name")
        {
            row.RemoveMetadata("DisplayName");
        }
        else if (invalid == "bad-version")
        {
            row.SetMetadata("Version", "invalid");
        }
        Assert.IsFalse(select.Execute());
        Assert.AreEqual(1, engine.Errors.Count);
    }

    [TestMethod]
    public void PlatformCatalogCompilesInstalledSDKNamesAndAliasesForOfflineSelection()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        string root = fixture.Environment.GetAbsolutePath("installed-sdks").Value;
        string sdk = Path.Combine(root, "SyntheticPlatform", "1.0");
        string alias = Path.Combine(sdk, "Platforms", "SyntheticAlias", "2.0");
        string empty = Path.Combine(root, "SyntheticPlatform", "1.0.0", "SDKManifest.xml");
        string absent = Path.Combine(root, "SyntheticNoName", "1.0", "SDKManifest.xml");
        Directory.CreateDirectory(alias);
        Directory.CreateDirectory(Path.GetDirectoryName(empty)!);
        Directory.CreateDirectory(Path.GetDirectoryName(absent)!);
        string manifest = Path.Combine(sdk, "SDKManifest.xml");
        File.WriteAllText(manifest, """<FileList DisplayName="Installed;100%$(Literal)" />""");
        File.WriteAllText(empty, """<FileList DisplayName="" />""");
        File.WriteAllText(absent, "<FileList />");
        File.WriteAllText(Path.Combine(alias, "Platform.xml"), "<ApplicationPlatform />");
        string registryRoot = "SOFTWARE\\GenbuildTests\\" + Guid.NewGuid().ToString("N");
        XElement[] rows = PlatformSDKInputs.Capture([root], registryRoot).ToArray();
        Assert.AreEqual("Installed;100%$(Literal)", ToolLocationHelper.GetPlatformSDKDisplayName(
            "SyntheticAlias", "2.0", root, registryRoot));
        Assert.AreEqual("SyntheticPlatform 1.0.0", ToolLocationHelper.GetPlatformSDKDisplayName(
            "SyntheticPlatform", "1.0.0", root, registryRoot));
        Assert.AreEqual("SyntheticNoName 1.0", ToolLocationHelper.GetPlatformSDKDisplayName(
            "SyntheticNoName", "1.0", root, registryRoot));
        string output = fixture.Environment.GetAbsolutePath("platforms.g.props").Value;
        PlatformSDKInputs.Write(output, new XElement("Project",
            new XElement("PropertyGroup", new XElement("_PlatformSDKDisplayNamesVersion", "1")),
            new XElement("ItemGroup", rows)));
        File.Delete(manifest);
        File.Delete(empty);
        File.Delete(absent);
        File.Delete(Path.Combine(alias, "Platform.xml"));
        using var collection = new ProjectCollection();
        Project project = collection.LoadProject(output);
        ITaskItem[] facts = project.GetItems("_PlatformSDKDisplayName").Select(item =>
            Item(item.EvaluatedInclude, item.Metadata.Select(metadata => (metadata.Name, metadata.EvaluatedValue)).ToArray())).ToArray();
        Assert.AreEqual(2, facts.Length);
        Assert.AreEqual("Installed;100%$(Literal)", PlatformSDKInputs.Select("1", facts, "syntheticplatform", "01.0"));
        Assert.AreEqual("Installed;100%$(Literal)", PlatformSDKInputs.Select("1", facts, "SyntheticAlias", "2.0"));
        Assert.AreEqual("SyntheticPlatform 1.0.0", PlatformSDKInputs.Select("1", facts, "SyntheticPlatform", "1.0.0"));
        Assert.AreEqual("SyntheticNoName 1.0", PlatformSDKInputs.Select("1", facts, "SyntheticNoName", "1.0"));
        Assert.AreEqual("NeverAcquired 3.0", PlatformSDKInputs.Select("1", facts, "NeverAcquired", "3.0"));
    }

    [TestMethod]
    [DataRow("assets")]
    [DataRow("output")]
    [DataRow("catalog")]
    [DataRow("conflict")]
    public void AcquisitionRejectsConflictingPackLocatorsBeforePublication(string conflict)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        ITaskItem[] first = fixture.Bindings();
        ITaskItem[] second = fixture.Bindings();
        string output = first[0].GetMetadata("OutputFile");
        string assets = first[0].GetMetadata("AssetsFile");
        string original = File.ReadAllText(assets);
        string locator = conflict switch
        {
            "assets" => assets,
            "output" => output,
            "catalog" => Path.Combine(first[0].GetMetadata("ResourceDirectory"), "platform-sdk-display-names.g.props"),
            _ => fixture.Environment.GetAbsolutePath("locator.g.props").Value
        };
        first[0].SetMetadata("LocatorFile", ProjectCollection.Escape(locator));
        second[0].SetMetadata("LocatorFile", ProjectCollection.Escape(locator));
        if (conflict == "conflict")
        {
            second[0].SetMetadata("OutputFile", ProjectCollection.Escape(output + ".other"));
        }
        var engine = new RecordingEngine();
        var translate = new WriteFrameworkRestoreInputs
        {
            BuildEngine = engine,
            Requests = first.Concat(second).ToArray()
        };
        Assert.IsFalse(translate.Execute());
        Assert.AreEqual(0, translate.WrittenModels);
        Assert.IsFalse(File.Exists(output));
        Assert.AreEqual(original, File.ReadAllText(assets));
        Assert.AreEqual(1, engine.Errors.Count);
    }

    [TestMethod]
    public void RuntimeGraphAcquisitionSharesNativeResourcesAndRepublishesMissingFiles()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        string graph = fixture.Environment.GetAbsolutePath("runtime;literal%.json").Value;
        string other = fixture.Environment.GetAbsolutePath("other-runtime.json").Value;
        File.WriteAllText(graph, """
            {"runtimes":{"specific":{"#import":["second","first"],"Root":{"Dependency":"[1.0,2.0)"},"Empty":{}},"first":{},"second":{}},
             "supports":{"profile":{"net8.0":["second","first"]},"empty":{}}}
            """);
        File.WriteAllText(other, """{"runtimes":{"different":{}}}""");
        RuntimeGraph expected = JsonRuntimeFormat.ReadRuntimeGraph(graph);
        string output = fixture.Environment.GetAbsolutePath("native/inputs.g.props").Value;
        string secondOutput = fixture.Environment.GetAbsolutePath("other/inputs.g.props").Value;
        string directory = fixture.Environment.GetAbsolutePath("shared").Value;
        ITaskItem Binding(string input, string file) => Item(Guid.NewGuid().ToString(),
            ("RuntimeGraphPath", input), ("OutputFile", file), ("ResourceDirectory", directory));
        var engine = new RecordingEngine();
        var writer = new WriteRuntimeGraphInputs
        {
            BuildEngine = engine,
            Requests = [Binding(graph, output), Binding(graph, secondOutput), Binding(other, output)]
        };
        Assert.IsTrue(writer.Execute(), string.Join("\n", engine.Errors));
        Assert.AreEqual(2, writer.WrittenResources);
        Assert.AreEqual(2, Directory.GetFiles(directory).Length);
        string resource = XDocument.Load(secondOutput).Root!.Element("Import")!.Attribute("Project")!.Value;
        File.Delete(output);
        File.Delete(resource);
        Assert.IsTrue(writer.Execute(), string.Join("\n", engine.Errors));
        Assert.IsTrue(File.Exists(output));
        Assert.IsTrue(File.Exists(resource));
        using var collection = new ProjectCollection();
        Project evaluated = collection.LoadProject(output);
        ITaskItem[] rows = evaluated.GetItems("_RuntimeGraphResource").Select(item =>
            Item(item.EvaluatedInclude, item.Metadata.Select(metadata => (metadata.Name, metadata.EvaluatedValue)).ToArray())).ToArray();
        File.Delete(graph);
        File.Delete(other);
        Assert.IsTrue(expected.Equals(RuntimeGraphInputs.ReadItems("1", graph, rows)));
        Assert.AreEqual("different", RuntimeGraphInputs.ReadItems("1", other, rows).Runtimes.Single().Key);
        Assert.IsTrue(rows.All(item => item.GetMetadata("Content").Length == 0));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("opaque")]
    [DataRow("order")]
    [DataRow("kind")]
    public void NativeRuntimeGraphProcessingRejectsInvalidFactsWithoutReadingResources(string invalid)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        string graph = fixture.Environment.GetAbsolutePath("not-on-disk.json").Value;
        string version = invalid == "version" ? "future" : "1";
        ITaskItem[] rows = [Item(graph, ("Kind", "RuntimeGraph")), Item(graph, ("Kind", "Runtime"), ("Attribute_Runtime", "any"))];
        switch (invalid)
        {
            case "missing":
                graph += ".other";
                break;
            case "duplicate":
                rows = [.. rows, rows[0]];
                break;
            case "opaque":
                rows[0].SetMetadata("Content", "{}");
                break;
            case "order":
                rows = [rows[0], Item(graph, ("Kind", "RuntimeImport"), ("Attribute_Runtime", "any"), ("Attribute_Import", "missing")), rows[1]];
                break;
            case "kind":
                rows[1].SetMetadata("Kind", "Unknown");
                break;
        }
        Assert.ThrowsExactly<FormatException>(() => RuntimeGraphInputs.ReadItems(version, graph, rows));
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow("alias")]
    [DataRow("scope")]
    [DataRow("malformed")]
    public void RuntimeGraphAcquisitionRejectsIncompatibleBindings(string invalid)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        string graph = fixture.Environment.GetAbsolutePath("runtime.json").Value;
        string output = fixture.Environment.GetAbsolutePath("native.g.props").Value;
        string directory = fixture.Environment.GetAbsolutePath("resources").Value;
        ITaskItem first = Item("first", ("RuntimeGraphPath", graph), ("OutputFile", output), ("ResourceDirectory", directory));
        ITaskItem second = new TaskItem(first);
        if (invalid == "relative")
        {
            second.SetMetadata("RuntimeGraphPath", "relative.json");
        }
        else if (invalid == "alias")
        {
            second.SetMetadata("OutputFile", ProjectCollection.Escape(graph));
        }
        else if (invalid == "scope")
        {
            second.SetMetadata("ResourceDirectory", ProjectCollection.Escape(directory + "-other"));
        }
        else
        {
            File.WriteAllText(graph, "{");
        }
        var engine = new RecordingEngine();
        var writer = new WriteRuntimeGraphInputs { BuildEngine = engine, Requests = [first, second] };
        Assert.IsFalse(writer.Execute());
        Assert.AreEqual(1, engine.Errors.Count);
        Assert.AreEqual(0, writer.WrittenResources);
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public void AcquisitionResultsTranslateAllBindingsWithoutConfiguredPreparation()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        ITaskItem[] first = fixture.Bindings();
        ITaskItem[] second = fixture.Bindings();
        string locator = fixture.Environment.GetAbsolutePath("obj/frameworkpackinputs/Project.csproj.g.props").Value;
        first[0].SetMetadata("LocatorFile", ProjectCollection.Escape(locator));
        second[0].SetMetadata("LocatorFile", ProjectCollection.Escape(locator));
        string graph = fixture.Environment.GetAbsolutePath("runtime.json").Value;
        File.WriteAllText(graph, """{"runtimes":{"specific":{"#import":["any"]},"any":{}}}""");
        first[0].SetMetadata("RuntimeGraphPath", ProjectCollection.Escape(graph));
        string otherGraph = fixture.Environment.GetAbsolutePath("other-runtime.json").Value;
        File.WriteAllText(otherGraph, """{"runtimes":{"specific":{"#import":["other"]},"other":{}}}""");
        second[0].SetMetadata("RuntimeGraphPath", ProjectCollection.Escape(otherGraph));
        var engine = new RecordingEngine();
        var translator = new WriteFrameworkRestoreInputs { BuildEngine = engine, Requests = [.. first, .. second] };
        Assert.IsTrue(translator.Execute(), string.Join("\n", engine.Errors));
        Assert.AreEqual(1, translator.WrittenModels);
        Assert.AreEqual(first[0].GetMetadata("OutputFile"),
            ProjectCollection.Unescape(XDocument.Load(locator).Root!.Element("PropertyGroup")!.Element("FrameworkPackInputsPath")!.Value));
        string output = first[0].GetMetadata("OutputFile");
        (ITaskItem[] facts, ITaskItem[] resources) = PackFixture.ReadCapture(output);
        Assert.IsNull(XDocument.Load(output).Root!.Element("PropertyGroup")!.Element("_FrameworkReferencePlatformSDKFactsVersion"));
        File.Delete(graph);
        File.Delete(otherGraph);
        Directory.Delete(fixture.PackRoot, recursive: true);
        var snapshot = new FrameworkPackResources("2", facts, resources, fixture.Environment);
        Assert.IsNull(snapshot.Lookup("Framework", "Synthetic.Ref", "1.0.0"));
        Assert.AreEqual(fixture.PackRoot, snapshot.Lookup("Package", "Synthetic.Ref", "1.0.0"));
        Assert.IsTrue(snapshot.RuntimeGraph(graph).AreCompatible("specific", "any"));
        Assert.IsTrue(snapshot.RuntimeGraph(otherGraph).AreCompatible("specific", "other"));
        Assert.IsFalse(snapshot.RuntimeGraph(otherGraph).AreCompatible("specific", "any"));
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), "client", "C#");
        prepared.ResourceFactsVersion = "2";
        prepared.ResourceFacts = facts;
        prepared.PackResources = resources;
        Assert.IsTrue(prepared.Execute(), string.Join("\n", ((RecordingEngine)prepared.BuildEngine).Errors));
        CollectionAssert.AreEqual(new[] { fixture.PathInPack("ref/netcoreapp5.0/Common.dll") },
            prepared.ReferencesToAdd.Select(item => item.ItemSpec).ToArray());
    }

    [TestMethod]
    [DataRow("context")]
    [DataRow("folders")]
    [DataRow("unbound")]
    [DataRow("overwrite")]
    [DataRow("assets")]
    [DataRow("catalogs")]
    [DataRow("lookup")]
    public void AcquisitionTranslatorRejectsIncompatibleBindingsBeforePublishing(string invalid)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        ITaskItem[] first = fixture.Bindings();
        ITaskItem[] second = fixture.Bindings();
        ITaskItem header = second[0];
        switch (invalid)
        {
            case "context":
                header.SetMetadata("TargetingPackRoot", ProjectCollection.Escape(fixture.Environment.GetAbsolutePath("different").Value));
                break;
            case "folders":
                string assets = fixture.Environment.GetAbsolutePath("other-assets.json").Value;
                File.WriteAllText(assets, JsonSerializer.Serialize(new
                {
                    packageFolders = new Dictionary<string, object>
                    {
                        [fixture.Environment.GetAbsolutePath("other-packages").Value] = new { }
                    }
                }));
                header.SetMetadata("AssetsFile", ProjectCollection.Escape(assets));
                break;
            case "unbound":
                second[1].SetMetadata("Binding", "missing");
                break;
            case "overwrite":
                header.SetMetadata("OutputFile", ProjectCollection.Escape(first[0].GetMetadata("AssetsFile")));
                break;
            case "assets":
                File.WriteAllText(header.GetMetadata("AssetsFile"), "invalid-json");
                break;
            case "catalogs":
                first = [.. first, Item("first-member", ("Type", "FrameworkPackInput"), ("Binding", first[0].ItemSpec),
                    ("InputKind", "Member"), ("InputIdentity", "first"), ("PackRoot", fixture.PackRoot), ("Path", "first.dll"))];
                second = [.. second, Item("second-member", ("Type", "FrameworkPackInput"), ("Binding", second[0].ItemSpec),
                    ("InputKind", "Member"), ("InputIdentity", "second"), ("PackRoot", fixture.PackRoot), ("Path", "second.dll"))];
                break;
            case "lookup":
                second.Single(item => item.GetMetadata("InputKind") == "Lookup")
                    .SetMetadata("Path", ProjectCollection.Escape(fixture.PackRoot));
                break;
        }
        var engine = new RecordingEngine();
        var translator = new WriteFrameworkRestoreInputs { BuildEngine = engine, Requests = [.. first, .. second] };
        Assert.IsFalse(translator.Execute());
        Assert.AreEqual(0, translator.WrittenModels);
        Assert.AreEqual(1, engine.Errors.Count);
        Assert.IsFalse(File.Exists(first[0].GetMetadata("OutputFile")));
    }

    [TestMethod]
    public void UpToDateAcquisitionRepublishesDeletedFrameworkFacts()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        ITaskItem[] bindings = fixture.Bindings();
        string locator = fixture.Environment.GetAbsolutePath("obj/frameworkpackinputs/Project.csproj.g.props").Value;
        bindings[0].SetMetadata("LocatorFile", ProjectCollection.Escape(locator));
        var translator = new WriteFrameworkRestoreInputs { BuildEngine = new RecordingEngine(), Requests = bindings };
        Assert.IsTrue(translator.Execute());
        string file = bindings[0].GetMetadata("OutputFile");
        string expected = File.ReadAllText(file);
        string resource = ImportedResourceFile(file);
        string expectedResource = File.ReadAllText(resource);
        string catalog = Path.Combine(bindings[0].GetMetadata("ResourceDirectory"), "platform-sdk-display-names.g.props");
        string expectedCatalog = File.ReadAllText(catalog);
        File.Delete(file);
        File.Delete(resource);
        File.Delete(locator);
        File.Delete(catalog);
        Assert.IsTrue(translator.Execute());
        Assert.AreEqual(expected, File.ReadAllText(file));
        Assert.AreEqual(expectedResource, File.ReadAllText(resource));
        Assert.AreEqual(expectedCatalog, File.ReadAllText(catalog));
        Assert.AreEqual(file, ProjectCollection.Unescape(
            XDocument.Load(locator).Root!.Element("PropertyGroup")!.Element("FrameworkPackInputsPath")!.Value));
        Assert.AreEqual(1, translator.WrittenModels);
    }

    [TestMethod]
    public void CaptureCompilesFrameworkAttributesAndConflictRecordsIntoNativeItems()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        string listPath = fixture.PathInPack("data/FrameworkList.xml");
        XElement list = XElement.Load(listPath);
        string literal = "literal%;$(NoExpansion);@(NoExpansion)&<value>";
        list.Elements("File").First().SetAttributeValue("FutureAttribute", literal);
        list.Save(listPath);
        File.WriteAllText(fixture.PathInPack("data/PlatformManifest.txt"),
            "# comment\n Example.dll | Other.Package | 1.2.3.4 | 5.6.7.8 \n");
        File.WriteAllText(fixture.PathInPack("data/PackageOverrides.txt"),
            "Other.Package|2.0.0\nOther.Package|1.0.0;Invalid|not-a-version");

        WriteFrameworkPackInputs capture = fixture.CaptureTask();
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture(capture);
        using var collection = new ProjectCollection();
        Project evaluated = collection.LoadProject(capture.OutputFile);
        ITaskItem[] evaluatedResources = evaluated.GetItems("_FrameworkPackResource").Select(item =>
            Item(item.EvaluatedInclude, item.Metadata.Select(metadata =>
                (metadata.Name, metadata.EvaluatedValue)).ToArray())).ToArray();
        CollectionAssert.AreEqual(resources.Select(item => item.GetMetadata("Kind")).ToArray(),
            evaluatedResources.Select(item => item.GetMetadata("Kind")).ToArray());
        Assert.IsTrue(evaluatedResources.All(item => item.GetMetadata("Content").Length == 0));
        Assert.AreEqual(literal, evaluatedResources.First(item => item.GetMetadata("Kind") == "FrameworkFile")
            .GetMetadata("Attribute_FutureAttribute"));

        Directory.Delete(fixture.PackRoot, recursive: true);
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), "", "C#");
        prepared.ResourceFactsVersion = "2";
        prepared.ResourceFacts = facts;
        prepared.PackResources = evaluatedResources;
        Assert.IsTrue(prepared.Execute(), string.Join("\n", ((RecordingEngine)prepared.BuildEngine).Errors));
        ITaskItem platform = prepared.PlatformConflictItems.Single();
        Assert.AreEqual("Example.dll", platform.ItemSpec);
        Assert.AreEqual("Other.Package", platform.GetMetadata("PackageId"));
        Assert.AreEqual("1.2.3.4", platform.GetMetadata("AssemblyVersion"));
        Assert.AreEqual("5.6.7.8", platform.GetMetadata("FileVersion"));
        Assert.AreEqual("1.0.0", PackageOverride.Create(prepared.PackageConflictOverrides.Single())
            .OverriddenPackages["Other.Package"].ToString());
    }

    [TestMethod]
    public void RuntimeGraphTranslationPreservesOrderedImportsDependenciesAndCompatibilityProfiles()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        string path = fixture.Environment.GetAbsolutePath("runtime.json").Value;
        File.WriteAllText(path, """
            {
              "runtimes": {
                "specific": {
                  "#import": ["second", "first"],
                  "Root.Package": {"Dependency": "[1.0.0,2.0.0)"},
                  "Empty.Package": {}
                },
                "first": {},
                "second": {}
              },
              "supports": {
                "profile": {"net8.0": ["second", "first"], "net9.0": "specific"},
                "empty": {}
              }
            }
            """);
        RuntimeGraph expected = JsonRuntimeFormat.ReadRuntimeGraph(path);
        WriteFrameworkPackInputs capture = fixture.CaptureTask();
        capture.RuntimeGraphPath = path;
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture(capture);
        Assert.IsTrue(resources.All(item => item.GetMetadata("Content").Length == 0));
        File.Delete(path);
        Directory.Delete(fixture.PackRoot, recursive: true);
        var prepared = new FrameworkPackResources("2", facts, resources, fixture.Environment);
        RuntimeGraph actual = prepared.RuntimeGraph(path);
        Assert.IsTrue(expected.Equals(actual));
        CollectionAssert.AreEqual(new[] { "second", "first" }, actual.Runtimes["specific"].InheritedRuntimes.ToArray());
        Assert.AreEqual("[1.0.0, 2.0.0)", actual.Runtimes["specific"].RuntimeDependencySets["Root.Package"]
            .Dependencies["Dependency"].VersionRange.ToString());
        Assert.AreEqual(0, actual.Runtimes["specific"].RuntimeDependencySets["Empty.Package"].Dependencies.Count);
        Assert.AreEqual(0, actual.Supports["empty"].RestoreContexts.Count);
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("future")]
    public void PreparedResourcesRejectNonNativeFormatVersions(string version)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        Assert.ThrowsExactly<FormatException>(() => new FrameworkPackResources(version, facts, resources, fixture.Environment));
    }

    [TestMethod]
    [DataRow("opaque")]
    [DataRow("unknown")]
    [DataRow("duplicate")]
    [DataRow("missing-attribute")]
    public void PreparedResourcesRejectMalformedNativeRecords(string malformed)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        ITaskItem header = resources.Single(item => item.GetMetadata("Kind") == "Pack");
        switch (malformed)
        {
            case "opaque":
                header.SetMetadata("Content", "&lt;Pack /&gt;");
                break;
            case "unknown":
                header.SetMetadata("Kind", "Unknown");
                break;
            case "duplicate":
                resources = [.. resources, header];
                break;
            case "missing-attribute":
                resources.First(item => item.GetMetadata("Kind") == "Probe").RemoveMetadata("Attribute_Exists");
                break;
        }
        Assert.ThrowsExactly<FormatException>(() => new FrameworkPackResources("2", facts, resources, fixture.Environment));
    }

    [TestMethod]
    public void ExcludedFrameworkRowsDoNotRequireSelectionOnlyAttributes()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        string path = fixture.PathInPack("data/FrameworkList.xml");
        XElement list = XElement.Load(path);
        list.Add(new XElement("File", new XAttribute("ReferencedByDefault", "false")));
        list.Save(path);
        var ordinary = fixture.Selection(new ResolveTargetingPackAssets(), "", "C#");
        Assert.IsTrue(ordinary.Execute());
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        Directory.Delete(fixture.PackRoot, recursive: true);
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), "", "C#");
        prepared.ResourceFactsVersion = "2";
        prepared.ResourceFacts = facts;
        prepared.PackResources = resources;
        Assert.IsTrue(prepared.Execute(), string.Join("\n", ((RecordingEngine)prepared.BuildEngine).Errors));
        CollectionAssert.AreEqual(ordinary.ReferencesToAdd.Select(item => item.ItemSpec).ToArray(),
            prepared.ReferencesToAdd.Select(item => item.ItemSpec).ToArray());
    }

    [TestMethod]
    [DataRow("", "C#", false)]
    [DataRow("client", "C#", false)]
    [DataRow("Full", "VB", false)]
    [DataRow("", "C#", true)]
    [DataRow("client", "C#", true)]
    [DataRow("Full", "VB", true)]
    public void PreparedSelectionUsesCurrentProfileAndLanguageAfterCapture(string profile, string language, bool usePathFallback)
    {
        using var fixture = new PackFixture(usePathFallback);
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        ResolveTargetingPackAssets ordinary = fixture.Selection(new ResolveTargetingPackAssets(), profile, language);
        Assert.IsTrue(ordinary.Execute());

        // Capture is deliberately shared across profiles/languages; it must not freeze a selection.
        Directory.Delete(fixture.PackRoot, recursive: true);
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), profile, language);
        prepared.ResourceFactsVersion = "2";
        prepared.ResourceFacts = facts;
        prepared.PackResources = resources;
        Assert.IsTrue(prepared.Execute(), string.Join("\n", ((RecordingEngine)prepared.BuildEngine).Errors));

        string[] expectedNames = profile switch
        {
            "client" => ["Common"],
            "Full" => ["Common", "FullOnly"],
            _ => ["Common", "FullOnly", "NoProfile"]
        };
        string folder = usePathFallback ? "lib" : "ref/netcoreapp5.0";
        CollectionAssert.AreEqual(expectedNames.Select(name => fixture.PathInPack(folder + "/" + name + ".dll")).ToArray(),
            prepared.ReferencesToAdd.Select(item => item.ItemSpec).ToArray());
        CollectionAssert.AreEqual(ordinary.ReferencesToAdd.Select(item => item.ItemSpec).ToArray(),
            prepared.ReferencesToAdd.Select(item => item.ItemSpec).ToArray());
        string languageAnalyzer = language == "C#" ? "cs/CSharp" : "vb/VisualBasic";
        string[] expectedAnalyzers =
        [
            fixture.PathInPack("analyzers/dotnet/CommonAnalyzer.dll"),
            fixture.PathInPack("analyzers/dotnet/" + languageAnalyzer + ".dll")
        ];
        CollectionAssert.AreEqual(expectedAnalyzers, prepared.AnalyzersToAdd.Select(item => item.ItemSpec).ToArray());
        CollectionAssert.AreEqual(ordinary.AnalyzersToAdd.Select(item => item.ItemSpec).ToArray(),
            prepared.AnalyzersToAdd.Select(item => item.ItemSpec).ToArray());
        foreach (ITaskItem reference in prepared.ReferencesToAdd)
        {
            Assert.AreEqual("Synthetic.Framework", reference.GetMetadata("FrameworkReferenceName"));
            Assert.AreEqual("Synthetic.Ref", reference.GetMetadata("NuGetPackageId"));
            Assert.AreEqual("1.0.0", reference.GetMetadata("FrameworkReferenceVersion"));
            Assert.AreEqual("1.2.3.4", reference.GetMetadata("AssemblyVersion"));
            Assert.AreEqual("5.6.7.8", reference.GetMetadata("FileVersion"));
            Assert.AreEqual("false", reference.GetMetadata("Private"));
            Assert.AreEqual("true", reference.GetMetadata("ExternallyResolved"));
        }
        CollectionAssert.AreEqual(new[] { "Synthetic.Runtime" }, prepared.UsedRuntimeFrameworks.Select(item => item.ItemSpec).ToArray());
        Assert.AreEqual("Synthetic.Ref", prepared.PackageConflictPreferredPackages);
        CollectionAssert.AreEqual(new[] { fixture.PathInPack("data/PlatformManifest.txt") },
            prepared.PlatformManifests.Select(item => item.ItemSpec).ToArray());
        Assert.AreEqual("1.0.0", PackageOverride.Create(prepared.PackageConflictOverrides.Single())
            .OverriddenPackages["Other.Package"].ToString());
    }

    [TestMethod]
    public void CapturePreservesLocalizedAnalyzerCompanionsNotListedInFrameworkList()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        Directory.Delete(fixture.PackRoot, recursive: true);
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), "client", "C#");
        prepared.ResourceFactsVersion = "2";
        prepared.ResourceFacts = facts;
        prepared.PackResources = resources;
        Assert.IsTrue(prepared.Execute());

        CollectionAssert.AreEquivalent(new[]
        {
            fixture.PathInPack("analyzers/dotnet/CommonAnalyzer.dll"),
            fixture.PathInPack("analyzers/dotnet/cs/CSharp.dll"),
            fixture.PathInPack("analyzers/dotnet/vb/VisualBasic.dll"),
            fixture.PathInPack("analyzers/dotnet/cs/fr/CSharp.resources.dll")
        }, prepared.FrameworkAnalyzerFiles.Select(item => item.ItemSpec).ToArray());
        Assert.IsFalse(prepared.AnalyzersToAdd.Any(item => item.ItemSpec.EndsWith(".resources.dll", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("context")]
    [DataRow("pack")]
    [DataRow("probe")]
    public void PreparedSelectionRejectsMissingAcquisitionFactsInsteadOfProbingDisk(string missing)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), "", "C#");
        prepared.ResourceFactsVersion = missing == "version" ? "" : "2";
        prepared.ResourceFacts = missing == "context" ? [] : facts;
        prepared.PackResources = missing == "pack" ? [] : resources;
        if (missing == "probe")
        {
            prepared.PackResources = resources.Where(item =>
                item.GetMetadata("Kind") != "Probe" || item.GetMetadata("Attribute_Path") != "ref/net5.0").ToArray();
        }

        // The complete pack still exists, so filesystem fallback would make this incorrectly succeed.
        Assert.IsTrue(File.Exists(fixture.PathInPack("data/FrameworkList.xml")));
        Assert.IsFalse(prepared.Execute());
        Assert.AreEqual(1, ((RecordingEngine)prepared.BuildEngine).Errors.Count);
        StringAssert.Contains(((RecordingEngine)prepared.BuildEngine).Errors.Single(), missing switch
        {
            "version" => "raw framework-pack facts",
            "context" => "acquisition context",
            "pack" or "probe" => "presence fact",
            _ => throw new InvalidOperationException()
        });
    }

    [TestMethod]
    public void PreparedPackageLookupDistinguishesAcquiredAbsenceFromUnacquiredLookup()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        var resourcesWithLookup = new FrameworkPackResources("2", facts, resources, fixture.Environment);
        Assert.AreEqual(fixture.PackRoot, resourcesWithLookup.Lookup("Package", "Synthetic.Ref", "1.0.0"));
        Assert.ThrowsExactly<FormatException>(() => resourcesWithLookup.Lookup("Package", "Unacquired.Ref", "1.0.0"));
        Assert.ThrowsExactly<FormatException>(() => resourcesWithLookup.ResolveVersion("Unacquired.Ref", "**FromWorkload**"));

        ITaskItem absent = Item("Package/Absent.Ref/1.0.0", ("Kind", "Lookup"), ("Path", ""));
        var resourcesWithAbsence = new FrameworkPackResources("2", [.. facts, absent], resources, fixture.Environment);
        Assert.IsNull(resourcesWithAbsence.Lookup("Package", "Absent.Ref", "1.0.0"));
    }

    [TestMethod]
    [DataRow("../escape.dll")]
    [DataRow("/absolute.dll")]
    [DataRow("ref//empty.dll")]
    [DataRow("ref/./dot.dll")]
    [DataRow("ref\\backslash.dll")]
    [DataRow("")]
    public void CaptureAndPreparedResourcesRejectMalformedArchiveMemberPaths(string member)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        fixture.AddArchiveMember(member);
        WriteFrameworkPackInputs capture = fixture.CaptureTask();
        Assert.IsFalse(capture.Execute());
        StringAssert.Contains(((RecordingEngine)capture.BuildEngine).Errors.Single(), "member path");
        Assert.IsFalse(File.Exists(capture.OutputFile));

        ITaskItem context = Item("Context", ("Kind", "Context"), ("IsWindows", "false"));
        ITaskItem resource = Item(fixture.PackRoot, ("Kind", "Member"), ("Attribute_Path", member));
        Assert.ThrowsExactly<FormatException>(() =>
            new FrameworkPackResources("2", [context],
                [Item(fixture.PackRoot, ("Kind", "Pack")), resource], fixture.Environment));
    }

    [TestMethod]
    public void DifferentExplicitCatalogsForOneLooseRootPreserveBothSnapshotsInOneBuild()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        var engine = new RecordingEngine();
        WriteFrameworkPackInputs first = fixture.CaptureTask();
        first.BuildEngine = engine;
        first.PackRequests = [];
        first.PackageFolders = [];
        first.LookupFacts = [Item("Installed/Synthetic.Ref/1.0.0", ("Kind", "Lookup"), ("Path", fixture.PackRoot))];
        first.PackMembers = [Item("CSharp", ("PackRoot", fixture.PackRoot), ("Path", "analyzers/dotnet/cs/CSharp.dll"))];
        fixture.Capture(first);
        string firstResourceFile = ImportedResourceFile(first.OutputFile);
        string firstResourceContent = File.ReadAllText(firstResourceFile);

        WriteFrameworkPackInputs second = fixture.CaptureTask();
        second.BuildEngine = engine;
        second.OutputFile = fixture.Environment.GetAbsolutePath("second-inputs.props").Value;
        second.PackRequests = [];
        second.PackageFolders = [];
        second.LookupFacts = first.LookupFacts;
        second.PackMembers = [Item("VisualBasic", ("PackRoot", fixture.PackRoot), ("Path", "analyzers/dotnet/vb/VisualBasic.dll"))];
        fixture.Capture(second);

        Assert.AreNotEqual(firstResourceFile, ImportedResourceFile(second.OutputFile));
        Assert.AreEqual(firstResourceContent, File.ReadAllText(firstResourceFile));
        Directory.Delete(fixture.PackRoot, recursive: true);
        (ITaskItem[] firstFacts, ITaskItem[] firstResources) = PackFixture.ReadCapture(first.OutputFile);
        (ITaskItem[] secondFacts, ITaskItem[] secondResources) = PackFixture.ReadCapture(second.OutputFile);
        var firstSnapshot = new FrameworkPackResources("2", firstFacts, firstResources, fixture.Environment);
        var secondSnapshot = new FrameworkPackResources("2", secondFacts, secondResources, fixture.Environment);
        CollectionAssert.AreEqual(new[] { fixture.PathInPack("analyzers/dotnet/cs/CSharp.dll") },
            firstSnapshot.AnalyzerCandidates().Select(item => item.ItemSpec).ToArray());
        CollectionAssert.AreEqual(new[] { fixture.PathInPack("analyzers/dotnet/vb/VisualBasic.dll") },
            secondSnapshot.AnalyzerCandidates().Select(item => item.ItemSpec).ToArray());
    }

    [TestMethod]
    [DataRow("duplicate-data")]
    [DataRow("parentless-legacy-file")]
    public void MalformedPackResourcesReportControlledTaskErrors(string malformed)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture();
        if (malformed == "duplicate-data")
        {
            resources = [.. resources, resources.Single(item =>
                item.GetMetadata("Kind") == "Data" && item.GetMetadata("Attribute_Path") == "data/FrameworkList.xml")];
        }
        else
        {
            resources = [.. resources, Item(fixture.PackRoot, ("Kind", "LegacyFile"), ("Attribute_Path", "Orphan.dll"))];
        }
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), "", "C#");
        if (malformed == "parentless-legacy-file")
        {
            prepared.ResolvedTargetingPacks.Single().SetMetadata("TargetingPackFormat", "NETStandardLegacy");
        }
        prepared.ResourceFactsVersion = "2";
        prepared.ResourceFacts = facts;
        prepared.PackResources = resources;

        Assert.IsFalse(prepared.Execute());
        Assert.AreEqual(1, ((RecordingEngine)prepared.BuildEngine).Errors.Count);
        StringAssert.Contains(((RecordingEngine)prepared.BuildEngine).Errors.Single(),
            malformed == "duplicate-data" ? "Duplicate framework-pack data" : "Invalid legacy framework-pack member path");
    }

    [TestMethod]
    public void RelativeCaptureOutputsResolveAgainstTaskEnvironmentProjectDirectory()
    {
        using var fixture = new PackFixture(usePathFallback: false);
        WriteFrameworkPackInputs capture = fixture.CaptureTask();
        string relativeDirectory = "relative-framework-pack-" + Guid.NewGuid().ToString("N");
        capture.OutputFile = Path.Combine(relativeDirectory, "inputs.props");
        capture.ResourceDirectory = Path.Combine(relativeDirectory, "resources");
        string expectedOutput = fixture.Environment.GetAbsolutePath(capture.OutputFile).Value;
        string expectedResourceDirectory = fixture.Environment.GetAbsolutePath(capture.ResourceDirectory).Value;
        Assert.IsFalse(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), relativeDirectory)));

        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture(capture);

        Assert.AreEqual(expectedOutput, capture.OutputFile);
        Assert.AreEqual(expectedResourceDirectory, capture.ResourceDirectory);
        Assert.IsTrue(File.Exists(expectedOutput));
        Assert.AreEqual(expectedResourceDirectory, Path.GetDirectoryName(ImportedResourceFile(expectedOutput)));
        Assert.IsFalse(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), relativeDirectory)));
        var snapshot = new FrameworkPackResources("2", facts, resources, fixture.Environment);
        Assert.AreEqual(fixture.PackRoot, snapshot.Lookup("Package", "Synthetic.Ref", "1.0.0"));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void LegacyTargetingInventoryIsRequiredForFreshAndCachedCaptures(bool hasInventory, bool warmCache)
    {
        using var fixture = new PackFixture(usePathFallback: false);
        fixture.PrepareLegacyPack();
        var engine = new RecordingEngine();
        WriteFrameworkPackInputs capture = fixture.CaptureTask();
        capture.BuildEngine = engine;
        capture.PackageFolders = [];
        capture.LookupFacts = [Item("Installed/Synthetic.Ref/1.0.0", ("Kind", "Lookup"), ("Path", fixture.PackRoot))];
        capture.PackRequests = [Item("Synthetic.Framework", ("NuGetPackageId", "Synthetic.Ref"),
            ("NuGetPackageVersion", "1.0.0"), ("TargetFramework", "netstandard1.6"), ("Path", fixture.PackRoot))];
        capture.PackMembers = hasInventory
            ? [
                Item("netstandard", ("PackRoot", fixture.PackRoot), ("Path", "build/netstandard1.6/ref/netstandard.dll")),
                Item("Facade", ("PackRoot", fixture.PackRoot), ("Path", "build/netstandard1.6/ref/Facade.dll"))
            ]
            : [];
        if (warmCache)
        {
            // Acquisition without a targeting request can cache this loose root before admission.
            WriteFrameworkPackInputs warmup = fixture.CaptureTask();
            warmup.BuildEngine = engine;
            warmup.OutputFile = fixture.Environment.GetAbsolutePath("warm-inputs.props").Value;
            warmup.PackageFolders = [];
            warmup.PackRequests = [];
            warmup.LookupFacts = capture.LookupFacts;
            warmup.PackMembers = capture.PackMembers;
            fixture.Capture(warmup);
        }

        if (!hasInventory)
        {
            Assert.IsFalse(capture.Execute());
            Assert.AreEqual(1, engine.Errors.Count);
            StringAssert.Contains(engine.Errors.Single(), "requires an acquired archive or explicit member catalog");
            Assert.IsFalse(File.Exists(capture.OutputFile));
            return;
        }

        (ITaskItem[] facts, ITaskItem[] resources) = fixture.Capture(capture);
        Directory.Delete(fixture.PackRoot, recursive: true);
        var prepared = fixture.Selection(new Prepared.ResolveTargetingPackAssets(), "", "C#");
        prepared.ResolvedTargetingPacks.Single().SetMetadata("TargetingPackFormat", "NETStandardLegacy");
        prepared.ResolvedTargetingPacks.Single().SetMetadata("TargetFramework", "netstandard1.6");
        prepared.ResourceFactsVersion = "2";
        prepared.ResourceFacts = facts;
        prepared.PackResources = resources;
        Assert.IsTrue(prepared.Execute(), string.Join("\n", ((RecordingEngine)prepared.BuildEngine).Errors));
        CollectionAssert.AreEquivalent(new[]
        {
            fixture.PathInPack("build/netstandard1.6/ref/netstandard.dll"),
            fixture.PathInPack("build/netstandard1.6/ref/Facade.dll")
        }, prepared.ReferencesToAdd.Select(item => item.ItemSpec).ToArray());
        Assert.AreEqual("", prepared.ReferencesToAdd.Single(item => Path.GetFileName(item.ItemSpec) == "netstandard.dll").GetMetadata("Facade"));
        Assert.AreEqual("true", prepared.ReferencesToAdd.Single(item => Path.GetFileName(item.ItemSpec) == "Facade.dll").GetMetadata("Facade"));
        foreach (ITaskItem reference in prepared.ReferencesToAdd)
        {
            Assert.AreEqual("Synthetic.Framework", reference.GetMetadata("FrameworkReferenceName"));
            Assert.AreEqual("Synthetic.Ref", reference.GetMetadata("NuGetPackageId"));
            Assert.AreEqual("1.0.0", reference.GetMetadata("NuGetPackageVersion"));
            Assert.AreEqual("false", reference.GetMetadata("Private"));
            Assert.AreEqual("true", reference.GetMetadata("ExternallyResolved"));
        }
        Assert.AreEqual(0, prepared.AnalyzersToAdd.Length);
    }

    private static string ImportedResourceFile(string outputFile) => ProjectCollection.Unescape(
        XDocument.Load(outputFile).Root!.Elements("Import").Select(import => import.Attribute("Project")!.Value)
            .Single(path => Path.GetFileName(path) != "platform-sdk-display-names.g.props"));

    private static TaskItem Item(string identity, params (string Name, string Value)[] metadata)
    {
        var item = new TaskItem(ProjectCollection.Escape(identity));
        foreach ((string name, string value) in metadata)
        {
            item.SetMetadata(name, ProjectCollection.Escape(value));
        }
        return item;
    }

    private sealed class PackFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "framework-pack-tests-" + Guid.NewGuid().ToString("N"));
        private readonly string _packageFolder;
        private readonly string _archive;
        public string PackRoot { get; }
        public TaskEnvironment Environment { get; }

        public PackFixture(bool usePathFallback)
        {
            _packageFolder = Path.Combine(_directory, "packages");
            PackRoot = Path.Combine(_packageFolder, "synthetic.ref", "1.0.0");
            Environment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(_directory, new Dictionary<string, string>());
            _archive = Path.Combine(PackRoot, "synthetic.ref.1.0.0.nupkg");
            Directory.CreateDirectory(PackRoot);
            File.WriteAllText(Path.Combine(PackRoot, "synthetic.ref.1.0.0.nupkg.sha512"), "synthetic");
            File.WriteAllText(Path.Combine(PackRoot, ".nupkg.metadata"), "{}");
            XElement list = new("FileList",
                FileEntry("Common", "lib/Common.dll", "Managed", "Full;Client"),
                FileEntry("FullOnly", "lib/FullOnly.dll", "Managed", "Full"),
                FileEntry("NoProfile", "lib/NoProfile.dll", "Managed", null),
                FileEntry("OptIn", "lib/OptIn.dll", "Managed", "Full;Client", referencedByDefault: false),
                FileEntry("CommonAnalyzer", "analyzers/dotnet/CommonAnalyzer.dll", "Analyzer", "Full;Client"),
                FileEntry("CSharp", "analyzers/dotnet/cs/CSharp.dll", "Analyzer", "Full;Client", language: "cs"),
                FileEntry("VisualBasic", "analyzers/dotnet/vb/VisualBasic.dll", "Analyzer", "Full;Client", language: "vb"));
            Write("data/FrameworkList.xml", list.ToString());
            Write("data/PlatformManifest.txt", "");
            Write("data/PackageOverrides.txt", "Other.Package|1.0.0");
            foreach (string name in new[] { "Common", "FullOnly", "NoProfile", "OptIn" })
            {
                Write((usePathFallback ? "lib/" : "ref/netcoreapp5.0/") + name + ".dll", "");
            }
            foreach (string path in new[]
            {
                "analyzers/dotnet/CommonAnalyzer.dll", "analyzers/dotnet/cs/CSharp.dll",
                "analyzers/dotnet/vb/VisualBasic.dll", "analyzers/dotnet/cs/fr/CSharp.resources.dll"
            })
            {
                Write(path, "");
            }
            using ZipArchive archive = ZipFile.Open(_archive, ZipArchiveMode.Create);
            foreach (string path in Directory.GetFiles(PackRoot, "*", SearchOption.AllDirectories))
            {
                if (path != _archive)
                {
                    archive.CreateEntry(Path.GetRelativePath(PackRoot, path).Replace('\\', '/'));
                }
            }
        }

        public string PathInPack(string path) => Path.Combine(PackRoot, path.Replace('/', Path.DirectorySeparatorChar));

        public void PrepareLegacyPack()
        {
            File.Delete(_archive);
            File.Delete(PathInPack("data/FrameworkList.xml"));
            Write("build/netstandard1.6/ref/netstandard.dll", "");
            Write("build/netstandard1.6/ref/Facade.dll", "");
            Write("build/netstandard1.6/ref/Notes.txt", "");
        }

        public void AddArchiveMember(string path)
        {
            using ZipArchive archive = ZipFile.Open(_archive, ZipArchiveMode.Update);
            // ZipArchive itself rejects an empty name; a raw resource exercises that case below.
            archive.CreateEntry(path.Length == 0 ? "ref//empty.dll" : path);
        }

        public WriteFrameworkPackInputs CaptureTask() => new()
        {
            BuildEngine = new RecordingEngine(),
            TaskEnvironment = Environment,
            OutputFile = Path.Combine(_directory, "inputs.props"),
            ResourceDirectory = Path.Combine(_directory, "resources"),
            PackageFolders = [Item(_packageFolder)],
            PackRequests = [Item("Synthetic.Framework", ("NuGetPackageId", "Synthetic.Ref"),
                ("NuGetPackageVersion", "1.0.0"), ("TargetFramework", "net5.0"))]
        };

        public ITaskItem[] Bindings()
        {
            string assets = Environment.GetAbsolutePath("project.assets.json").Value;
            File.WriteAllText(assets, JsonSerializer.Serialize(new
            {
                packageFolders = new Dictionary<string, object>
                {
                    [_packageFolder] = new { }
                }
            }));
            WriteFrameworkPackInputs capture = CaptureTask();
            var collect = new CollectFrameworkPackInputs
            {
                BuildEngine = new RecordingEngine(),
                TaskEnvironment = Environment,
                ProjectPath = Environment.GetAbsolutePath("Project.csproj").Value,
                AssetsFile = assets,
                OutputFile = capture.OutputFile,
                ResourceDirectory = capture.ResourceDirectory,
                PackRequests = capture.PackRequests,
                LookupFacts = [Item("Framework/Synthetic.Ref/1.0.0", ("Kind", "Lookup"), ("Path", ""))],
            };
            Assert.IsTrue(collect.Execute());
            return collect.Bindings;
        }

        public (ITaskItem[] Facts, ITaskItem[] Resources) Capture(WriteFrameworkPackInputs? capture = null)
        {
            capture ??= CaptureTask();
            Assert.IsTrue(capture.Execute(), string.Join("\n", ((RecordingEngine)capture.BuildEngine).Errors));
            return ReadCapture(capture.OutputFile);
        }

        public static (ITaskItem[] Facts, ITaskItem[] Resources) ReadCapture(string outputFile)
        {
            XElement project = XDocument.Load(outputFile).Root!;
            Assert.AreEqual("2", project.Element("PropertyGroup")!.Element("_RawFrameworkPackFactsVersion")!.Value);
            Assert.IsFalse(project.Descendants().Any(item => item.Name.LocalName.Contains("CompileReference", StringComparison.Ordinal)));
            ITaskItem[] facts = project.Element("ItemGroup")!.Elements().Select(ReadItem).ToArray();
            ITaskItem[] resources = project.Elements("Import").SelectMany(import =>
                XDocument.Load(ProjectCollection.Unescape(import.Attribute("Project")!.Value))
                    .Root!.Element("ItemGroup")!.Elements("_FrameworkPackResource").Select(ReadItem)).ToArray();
            Assert.AreEqual(1, resources.Count(item => item.GetMetadata("Kind") == "Pack"));
            return (facts, resources);
        }

        public T Selection<T>(T task, string profile, string language) where T : ResolveTargetingPackAssets
        {
            task.BuildEngine = new RecordingEngine();
            task.TaskEnvironment = Environment;
            task.FrameworkReferences = [Item("Synthetic.Framework")];
            task.ResolvedTargetingPacks = [Item("Synthetic.Framework", ("Path", PackRoot),
                ("NuGetPackageId", "Synthetic.Ref"), ("NuGetPackageVersion", "1.0.0"),
                ("TargetFramework", "net5.0"), ("Profile", profile), ("PackageConflictPreferredPackages", "Synthetic.Ref"))];
            task.RuntimeFrameworks = [Item("Synthetic.Runtime", ("FrameworkName", "Synthetic.Framework")),
                Item("Unused.Runtime", ("FrameworkName", "Unused.Framework"))];
            task.ProjectLanguage = language;
            task.GenerateErrorForMissingTargetingPacks = true;
            return task;
        }

        private static TaskItem ReadItem(XElement element) => Item(
            ProjectCollection.Unescape(element.Attribute("Include")!.Value),
            element.Elements().Select(metadata => (metadata.Name.LocalName, ProjectCollection.Unescape(metadata.Value))).ToArray());

        private static XElement FileEntry(string name, string path, string type, string? profile,
            string? language = null, bool referencedByDefault = true) => new("File",
                new XAttribute("AssemblyName", name), new XAttribute("Path", path), new XAttribute("Type", type),
                new XAttribute("AssemblyVersion", "1.2.3.4"), new XAttribute("FileVersion", "5.6.7.8"),
                new XAttribute("PublicKeyToken", "null"),
                profile is null ? null : new XAttribute("Profile", profile),
                language is null ? null : new XAttribute("Language", language),
                new XAttribute("ReferencedByDefault", referencedByDefault));

        private void Write(string path, string content)
        {
            string file = PathInPack(path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class RecordingEngine : IBuildEngine4
    {
        private readonly Dictionary<(object, RegisteredTaskObjectLifetime), object> _objects = [];
        public List<string> Errors { get; } = [];
        public bool IsRunningMultipleNodes => false;
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "";
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? "");
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
            => throw new NotSupportedException();
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties,
            IDictionary targetOutputs, string toolsVersion) => throw new NotSupportedException();
        public bool BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, IDictionary[] globalProperties,
            IDictionary[] targetOutputsPerProject, string[] toolsVersion, bool useResultsCache, bool unloadProjectsOnCompletion)
            => throw new NotSupportedException();
        public BuildEngineResult BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames,
            IDictionary[] globalProperties, IList<string>[] removeGlobalProperties, string[] toolsVersion, bool returnTargetOutputs)
            => throw new NotSupportedException();
        public void Yield() => throw new NotSupportedException();
        public void Reacquire() => throw new NotSupportedException();
        public void RegisterTaskObject(object key, object obj, RegisteredTaskObjectLifetime lifetime, bool allowEarlyCollection)
            => _objects.TryAdd((key, lifetime), obj);
        public object? GetRegisteredTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
            => _objects.TryGetValue((key, lifetime), out object? value) ? value : null;
        public object? UnregisterTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        {
            _objects.Remove((key, lifetime), out object? value);
            return value;
        }
    }
}
