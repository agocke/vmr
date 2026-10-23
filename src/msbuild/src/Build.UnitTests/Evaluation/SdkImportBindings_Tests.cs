// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph.Hardened;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Unittest;
using Shouldly;
using Xunit;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

#nullable enable

namespace Microsoft.Build.UnitTests.Evaluation;

public sealed class SdkImportBindings_Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveRoundTripsCompleteResultWithLiteralValues(bool propertyAndItemOnly)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = Path.Combine(root, "bindings");
        string projectPath = Path.Combine(root, "project.proj");
        string solutionPath = Path.Combine(root, "solution.sln");
        var request = new SdkReference("Requested.Sdk", "1.0", "0.9");
        var resolvedReference = new SdkReference("Resolver.Original.Sdk", "2.0", "1.5");
        const string Literal = "literal %3B;$(MustNotExpand);@(MustNotExpand);%(Identity) & <xml> \"quotes\" 'apostrophe'\nnext line";
        string[] paths = propertyAndItemOnly
            ? []
            : [Path.Combine(root, "sdk-z"), Path.Combine(root, "sdk-a"), Path.Combine(root, "sdk-m")];
        var original = new SdkResult(
            resolvedReference,
            paths: paths,
            version: "2.0.123",
            propertiesToAdd: new Dictionary<string, string>
            {
                ["LiteralProperty"] = Literal,
                ["EmptyProperty"] = string.Empty,
            },
            itemsToAdd: new Dictionary<string, SdkResultItem>
            {
                ["LiteralItem"] = new SdkResultItem(Literal, new Dictionary<string, string>
                {
                    ["LiteralMetadata"] = Literal,
                    ["EmptyMetadata"] = string.Empty,
                }),
            },
            warnings: ["first " + Literal, "second warning"],
            environmentVariablesToAdd: new Dictionary<string, string>
            {
                ["SDK_BINDING_LITERAL"] = Literal,
                ["SDK_BINDING_EMPTY"] = string.Empty,
            });
        var writer = new SdkImportBindings(
            directory, projectPath, interactive: true, isRunningInVisualStudio: true,
            FileSystems.Default, MockElementLocation.Instance);

        writer.Save(request, solutionPath, original, MockElementLocation.Instance);

        var reader = new SdkImportBindings(
            directory, projectPath, interactive: true, isRunningInVisualStudio: true,
            FileSystems.Default, MockElementLocation.Instance);
        reader.TryResolve(request, solutionPath, MockElementLocation.Instance, out SdkResult? result).ShouldBeTrue();

        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("2.0.123");
        result.SdkReference.Name.ShouldBe("Resolver.Original.Sdk");
        result.SdkReference.Version.ShouldBe("2.0");
        result.SdkReference.MinimumVersion.ShouldBe("1.5");
        result.Path.ShouldBe(propertyAndItemOnly ? null : paths[0]);
        (result.AdditionalPaths ?? []).ShouldBe(paths.Skip(1));
        result.PropertiesToAdd.Count.ShouldBe(2);
        result.PropertiesToAdd["LiteralProperty"].ShouldBe(Literal);
        result.PropertiesToAdd["EmptyProperty"].ShouldBeEmpty();
        result.ItemsToAdd.Count.ShouldBe(1);
        result.ItemsToAdd["LiteralItem"].ItemSpec.ShouldBe(Literal);
        Dictionary<string, string>? metadata = result.ItemsToAdd["LiteralItem"].Metadata;
        metadata.ShouldNotBeNull();
        metadata.Count.ShouldBe(2);
        metadata["LiteralMetadata"].ShouldBe(Literal);
        metadata["EmptyMetadata"].ShouldBeEmpty();
        result.EnvironmentVariablesToAdd.Count.ShouldBe(2);
        result.EnvironmentVariablesToAdd["SDK_BINDING_LITERAL"].ShouldBe(Literal);
        result.EnvironmentVariablesToAdd["SDK_BINDING_EMPTY"].ShouldBeEmpty();
        result.Warnings.ShouldBe(["first " + Literal, "second warning"]);
        Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories).Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("minimum-version")]
    [InlineData("project")]
    [InlineData("solution")]
    [InlineData("solution-added")]
    [InlineData("solution-removed")]
    [InlineData("interactive")]
    [InlineData("visual-studio")]
    public void TryResolveDoesNotReuseMissingOrChangedRequestContext(string change)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = Path.Combine(root, "bindings");
        string projectPath = Path.Combine(root, "project.proj");
        string? solutionPath = change == "solution-added" ? null : Path.Combine(root, "solution.sln");
        string sdkPath = Path.Combine(root, "sdk");
        var request = new SdkReference("Requested.Sdk", "1.0", "0.9");
        var original = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);

        original.TryResolve(request, solutionPath, MockElementLocation.Instance, out SdkResult? missing).ShouldBeFalse();
        missing.ShouldBeNull();
        original.Save(request, solutionPath, new SdkResult(request, sdkPath, "1.0", warnings: null), MockElementLocation.Instance);

        var changedRequest = new SdkReference(
            change == "name" ? "Other.Sdk" : request.Name,
            change == "version" ? "2.0" : request.Version,
            change == "minimum-version" ? "0.8" : request.MinimumVersion);
        string changedProject = change == "project" ? Path.Combine(root, "other.proj") : projectPath;
        string? changedSolution = change switch
        {
            "solution" or "solution-added" => Path.Combine(root, "other.sln"),
            "solution-removed" => null,
            _ => solutionPath,
        };
        var reader = new SdkImportBindings(
            directory, changedProject, interactive: change == "interactive", isRunningInVisualStudio: change == "visual-studio",
            FileSystems.Default, MockElementLocation.Instance);

        reader.TryResolve(changedRequest, changedSolution, MockElementLocation.Instance, out SdkResult? changed).ShouldBeFalse();
        changed.ShouldBeNull();
        original.TryResolve(request, solutionPath, MockElementLocation.Instance, out SdkResult? unchanged).ShouldBeTrue();
        unchanged.ShouldNotBeNull();
        unchanged.Path.ShouldBe(sdkPath);
    }

    [Fact]
    public void SaveReplacesPreviouslyPreparedResultWithoutStaleMetadataOrFiles()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = Path.Combine(root, "bindings");
        string projectPath = Path.Combine(root, "project.proj");
        var request = new SdkReference("Requested.Sdk", null, null);
        var writer = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);
        var original = new SdkResult(
            request,
            paths: [Path.Combine(root, "old-sdk"), Path.Combine(root, "old-extra-sdk")],
            version: "1.0",
            propertiesToAdd: new Dictionary<string, string> { ["OldProperty"] = "old value" },
            itemsToAdd: new Dictionary<string, SdkResultItem> { ["OldItem"] = new SdkResultItem("old item", null) },
            warnings: ["old warning"],
            environmentVariablesToAdd: new Dictionary<string, string> { ["SDK_BINDING_OLD"] = "old value" });
        writer.Save(request, solutionPath: null, original, MockElementLocation.Instance);
        string manifestPath = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories).Single();
        string replacementPath = Path.Combine(root, "new-sdk");
        var replacement = new SdkResult(request, replacementPath, "2.0", warnings: null);
        var preparation = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);

        preparation.Save(request, solutionPath: null, replacement, MockElementLocation.Instance);

        var reader = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);
        reader.TryResolve(request, solutionPath: null, MockElementLocation.Instance, out SdkResult? result).ShouldBeTrue();
        result.ShouldNotBeNull();
        result.Path.ShouldBe(replacementPath);
        result.Version.ShouldBe("2.0");
        (result.AdditionalPaths?.Count ?? 0).ShouldBe(0);
        (result.PropertiesToAdd?.Count ?? 0).ShouldBe(0);
        (result.ItemsToAdd?.Count ?? 0).ShouldBe(0);
        (result.EnvironmentVariablesToAdd?.Count ?? 0).ShouldBe(0);
        (result.Warnings ?? []).ShouldBeEmpty();
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ShouldBe([manifestPath]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<")]
    [InlineData("<UnexpectedManifest />")]
    [InlineData("<Project><Import Project=\"must-not-be-evaluated.props\" /></Project>")]
    public void TryResolveRejectsMalformedOrWrongFormatManifest(string contents)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = Path.Combine(root, "bindings");
        string projectPath = Path.Combine(root, "project.proj");
        var request = new SdkReference("Requested.Sdk", null, null);
        var writer = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);
        writer.Save(
            request, solutionPath: null,
            new SdkResult(request, Path.Combine(root, "sdk"), "1.0", warnings: null),
            MockElementLocation.Instance);
        string manifestPath = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories).Single();
        File.WriteAllText(manifestPath, contents);
        var reader = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);

        Should.Throw<InvalidProjectFileException>(() =>
            reader.TryResolve(request, solutionPath: null, MockElementLocation.Instance, out _));
    }

    [Theory]
    [InlineData("format-version")]
    [InlineData("request-name")]
    [InlineData("project")]
    [InlineData("primary-path")]
    [InlineData("additional-path")]
    [InlineData("missing-path-value")]
    [InlineData("unexpected-element")]
    [InlineData("duplicate-property")]
    public void TryResolveRejectsTamperedManifest(string change)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = Path.Combine(root, "bindings");
        string projectPath = Path.Combine(root, "project.proj");
        var request = new SdkReference("Requested.Sdk", "1.0", "0.9");
        var writer = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);
        writer.Save(
            request, solutionPath: null,
            new SdkResult(
                request, paths: [Path.Combine(root, "sdk"), Path.Combine(root, "extra-sdk")],
                version: "1.0",
                propertiesToAdd: new Dictionary<string, string> { ["ResolverProperty"] = "original" },
                itemsToAdd: null, warnings: null),
            MockElementLocation.Instance);
        string manifestPath = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories).Single();
        XElement manifest = XElement.Load(manifestPath);
        switch (change)
        {
            case "format-version":
                manifest.SetAttributeValue("FormatVersion", "999");
                break;
            case "request-name":
                manifest.Element("Request").ShouldNotBeNull().SetAttributeValue("Name", "Other.Sdk");
                break;
            case "project":
                manifest.Element("Request").ShouldNotBeNull().SetAttributeValue("Project", Path.Combine(root, "other.proj"));
                break;
            case "primary-path":
                manifest.Descendants("Path").First().SetAttributeValue("Value", "relative-sdk");
                break;
            case "additional-path":
                manifest.Descendants("Path").Last().SetAttributeValue("Value", "relative-extra-sdk");
                break;
            case "missing-path-value":
                manifest.Descendants("Path").First().Attribute("Value").ShouldNotBeNull().Remove();
                break;
            case "unexpected-element":
                manifest.Add(new XElement("Unexpected", "must not be ignored"));
                break;
            case "duplicate-property":
                XElement properties = manifest.Descendants("Properties").Single();
                properties.Add(new XElement(properties.Elements().Single()));
                break;
        }

        manifest.Save(manifestPath);
        var reader = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);

        Should.Throw<InvalidProjectFileException>(() =>
            reader.TryResolve(request, solutionPath: null, MockElementLocation.Instance, out _));
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(true, "relative-bindings")]
    [InlineData(false, "")]
    [InlineData(false, "relative-project.proj")]
    public void BindingsRequireAbsoluteDirectoryAndProjectPath(bool invalidDirectory, string invalidPath)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = invalidDirectory ? invalidPath : Path.Combine(root, "bindings");
        string projectPath = invalidDirectory ? Path.Combine(root, "project.proj") : invalidPath;
        var request = new SdkReference("Requested.Sdk", null, null);

        Should.Throw<InvalidProjectFileException>(() =>
        {
            var bindings = new SdkImportBindings(
                directory, projectPath, interactive: false, isRunningInVisualStudio: false,
                FileSystems.Default, MockElementLocation.Instance);
            bindings.TryResolve(request, solutionPath: null, MockElementLocation.Instance, out _);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-sdk")]
    public void SaveRejectsNonAbsolutePrimaryAndAdditionalSdkPaths(string invalidPath)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = Path.Combine(root, "bindings");
        string projectPath = Path.Combine(root, "project.proj");
        string sdkPath = Path.Combine(root, "sdk");
        var request = new SdkReference("Requested.Sdk", null, null);
        var bindings = new SdkImportBindings(
            directory, projectPath, interactive: false, isRunningInVisualStudio: false,
            FileSystems.Default, MockElementLocation.Instance);

        string[][] invalidPaths = [[invalidPath], [sdkPath, invalidPath]];
        foreach (string[] paths in invalidPaths)
        {
            var result = new SdkResult(request, paths, "1.0", propertiesToAdd: null, itemsToAdd: null, warnings: null);

            Should.Throw<InvalidProjectFileException>(() =>
                bindings.Save(request, solutionPath: null, result, MockElementLocation.Instance));
        }
    }

    [Fact]
    public void HardenedEvaluationReplaysPreparedBindingWithoutCallingResolver()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string directory = Path.Combine(root, "bindings");
        string projectPath = Path.Combine(root, "project.proj");
        string sdkPath = Directory.CreateDirectory(Path.Combine(root, "sdk")).FullName;
        File.WriteAllText(projectPath, """
            <Project>
              <PropertyGroup>
                <MustNotExpand>expanded</MustNotExpand>
              </PropertyGroup>
              <Import Project="Sdk.props" Sdk="Prepared.Sdk/1.0" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(sdkPath, "Sdk.props"), """
            <Project>
              <PropertyGroup>
                <ImportedSdkMarker>sdk-loaded</ImportedSdkMarker>
              </PropertyGroup>
            </Project>
            """);
        const string Literal = "%3B;$(MustNotExpand); & <xml> \"quoted\"\nnext line";
        int preparationCalls = 0;
        var preparationResolver = new SdkUtilities.ConfigurableMockSdkResolver((reference, _, _) =>
        {
            preparationCalls++;
            return new SdkResult(
                reference, sdkPath, "1.0", warnings: null,
                propertiesToAdd: new Dictionary<string, string> { ["LiteralProperty"] = Literal },
                itemsToAdd: new Dictionary<string, SdkResultItem>
                {
                    ["LiteralItem"] = new SdkResultItem(Literal, new Dictionary<string, string> { ["LiteralMetadata"] = Literal }),
                });
        });
        ProjectOptions preparationOptions = SdkUtilities.CreateProjectOptionsWithResolver(preparationResolver);
        preparationOptions.ProjectCollection = environment.CreateProjectCollection().Collection;
        preparationOptions.GlobalProperties = new Dictionary<string, string>
        {
            [SdkImportBindings.DirectoryProperty] = directory,
            ["MSBuildRestoreGraphInputs"] = "true",
        };

        Project.FromFile(projectPath, preparationOptions);

        preparationCalls.ShouldBe(1);
        Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories).Length.ShouldBe(1);
        int hardenedCalls = 0;
        var hardenedResolver = new SdkUtilities.ConfigurableMockSdkResolver((_, _, _) =>
        {
            hardenedCalls++;
            throw new InvalidOperationException("Prepared hardened evaluation must not invoke SDK resolvers.");
        });
        ProjectOptions hardenedOptions = SdkUtilities.CreateProjectOptionsWithResolver(hardenedResolver);
        hardenedOptions.ProjectCollection = environment.CreateProjectCollection().Collection;
        hardenedOptions.GlobalProperties = new Dictionary<string, string>
        {
            [SdkImportBindings.DirectoryProperty] = directory,
            ["MSBuildEmitActionGraph"] = Path.Combine(root, "graph.json"),
            ["MSBuildGraphHostOS"] = "linux",
            ["MSBuildGraphHostArchitecture"] = "x64",
            ["MSBuildGraphHostRuntimeIdentifier"] = "linux-x64",
            [DirectoryImportResolver.RepositoryRootProperty] = root,
        };

        Project project = Project.FromFile(projectPath, hardenedOptions);

        hardenedCalls.ShouldBe(0);
        project.GetPropertyValue("ImportedSdkMarker").ShouldBe("sdk-loaded");
        project.GetPropertyValue("LiteralProperty").ShouldBe(Literal);
        ProjectItem item = project.GetItems("LiteralItem").Single();
        item.EvaluatedInclude.ShouldBe(Literal);
        item.GetMetadataValue("LiteralMetadata").ShouldBe(Literal);
    }
}
