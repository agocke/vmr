// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Graph.Hardened;
using Microsoft.Build.Shared.FileSystem;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.UnitTests.Evaluation;

public sealed class DirectoryImport_Tests
{
    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    public void ResolveSelectsNearestFileAndIncludesRepositoryRoot(string fileName)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        string nearest = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        string start = Directory.CreateDirectory(Path.Combine(nearest, "deep")).FullName;
        string rootFile = Path.Combine(root, fileName);
        string nearestFile = Path.Combine(nearest, fileName);
        File.WriteAllText(rootFile, "<Project />");
        File.WriteAllText(nearestFile, "<Project />");
        var resolver = new DirectoryImportResolver(root, FileSystems.Default);

        foreach (bool returnFile in new[] { false, true })
        {
            resolver.Resolve(start, fileName, returnFile, MockElementLocation.Instance)
                .ShouldBe(returnFile ? nearestFile : nearest);
            resolver.Resolve(root, fileName, returnFile, MockElementLocation.Instance)
                .ShouldBe(returnFile ? rootFile : root);
        }

        File.Delete(nearestFile);

        foreach (bool returnFile in new[] { false, true })
        {
            resolver.Resolve(start, fileName, returnFile, MockElementLocation.Instance)
                .ShouldBe(returnFile ? rootFile : root);
        }
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    public void ResolveDoesNotFindPoisonedFileAboveRepositoryRoot(string fileName)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string parent = environment.CreateFolder().Path;
        string root = Directory.CreateDirectory(Path.Combine(parent, "repo")).FullName;
        string start = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllText(Path.Combine(parent, fileName), "<not-a-project />");
        var resolver = new DirectoryImportResolver(root, FileSystems.Default);

        foreach (bool returnFile in new[] { false, true })
        {
            resolver.Resolve(start, fileName, returnFile, MockElementLocation.Instance).ShouldBeEmpty();
            resolver.Resolve(root, fileName, returnFile, MockElementLocation.Instance).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-repo")]
    public void ResolveRequiresAbsoluteRepositoryRoot(string? root)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string start = environment.CreateFolder().Path;
        File.WriteAllText(Path.Combine(start, "Directory.Build.props"), "<Project />");
        var resolver = new DirectoryImportResolver(root, FileSystems.Default);

        InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(() =>
            resolver.Resolve(start, "Directory.Build.props", returnFile: true, MockElementLocation.Instance));

        exception.ErrorCode.ShouldBe("MSB4286");
    }

    [Theory]
    [InlineData("relative", "MSB4286")]
    [InlineData("outside", "MSB4290")]
    [InlineData("sibling-prefix", "MSB4290")]
    [InlineData("parent-traversal", "MSB4290")]
    public void ResolveRejectsRelativeAndEscapingStarts(string scenario, string errorCode)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string parent = environment.CreateFolder().Path;
        string root = Directory.CreateDirectory(Path.Combine(parent, "repo")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(parent, "outside")).FullName;
        string sibling = Directory.CreateDirectory(Path.Combine(parent, "repo-sibling")).FullName;
        File.WriteAllText(Path.Combine(outside, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(sibling, "Directory.Build.props"), "<Project />");
        string start = scenario switch
        {
            "relative" => "src",
            "outside" => outside,
            "sibling-prefix" => sibling,
            _ => Path.Combine(root, "..", "outside"),
        };
        var resolver = new DirectoryImportResolver(root, FileSystems.Default);

        InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(() =>
            resolver.Resolve(start, "Directory.Build.props", returnFile: true, MockElementLocation.Instance));

        exception.ErrorCode.ShouldBe(errorCode);
    }

    [Fact]
    public void ResolveRejectsGenericFileNameEvenWhenItExistsInsideRoot()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string root = environment.CreateFolder().Path;
        File.WriteAllText(Path.Combine(root, "custom.props"), "<Project />");
        var resolver = new DirectoryImportResolver(root, FileSystems.Default);

        InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(() =>
            resolver.Resolve(root, "custom.props", returnFile: true, MockElementLocation.Instance));

        exception.ErrorCode.ShouldBe("MSB4287");
    }

#if NET
    [RequiresSymbolicLinksFact]
    public void ResolveRejectsDirectoryAndFileSymlinkEscapes()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string parent = environment.CreateFolder().Path;
        string root = Directory.CreateDirectory(Path.Combine(parent, "repo")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(parent, "outside")).FullName;
        string outsideFile = Path.Combine(outside, "Directory.Build.props");
        File.WriteAllText(outsideFile, "<Project />");
        string linkedDirectory = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        File.CreateSymbolicLink(Path.Combine(root, "Directory.Build.props"), outsideFile);
        var resolver = new DirectoryImportResolver(root, FileSystems.Default);

        foreach (bool returnFile in new[] { false, true })
        {
            InvalidProjectFileException directoryException = Should.Throw<InvalidProjectFileException>(() =>
                resolver.Resolve(linkedDirectory, "Directory.Build.props", returnFile, MockElementLocation.Instance));
            directoryException.ErrorCode.ShouldBe("MSB4290");

            InvalidProjectFileException fileException = Should.Throw<InvalidProjectFileException>(() =>
                resolver.Resolve(root, "Directory.Build.props", returnFile, MockElementLocation.Instance));
            fileException.ErrorCode.ShouldBe("MSB4290");
        }
    }
#endif

    [Fact]
    public void GraphEvaluationPreservesGlobalRootThroughNestedAndChainedLookup()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        TransientTestProjectWithFiles files = environment.CreateTestProjectWithFiles(
            """
            <Project TreatAsLocalProperty="MSBuildGraphRepositoryRoot">
              <PropertyGroup>
                <MSBuildGraphRepositoryRoot>$(MSBuildProjectDirectory)</MSBuildGraphRepositoryRoot>
                <NearestDirectory>$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildProjectDirectory)', 'Directory.Build.props'))</NearestDirectory>
                <DefaultFile>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props'))</DefaultFile>
                <RootFile>$([MSBuild]::GetPathOfFileAbove('Directory.Build.targets', '$(MSBuildProjectDirectory)'))</RootFile>
                <Nested>$([System.IO.Path]::GetFileName($([MSBuild]::GetPathOfFileAbove('Directory.Packages.props'))))</Nested>
                <Chained>$([MSBuild]::GetPathOfFileAbove('Directory.Packages.props').Replace($([MSBuild]::GetPathOfFileAbove('Directory.Packages.props')), 'root-package'))</Chained>
              </PropertyGroup>
              <Import Project="$(RootFile)" />
            </Project>
            """,
            [
                "repo/Directory.Build.props",
                "repo/src/Directory.Build.props",
                "repo/Directory.Build.targets",
                "repo/Directory.Packages.props",
            ],
            relativePathFromRootToProject: "repo/src/deep");
        string root = Path.Combine(files.TestRoot, "repo");
        File.WriteAllText(
            Path.Combine(root, "Directory.Build.targets"),
            "<Project><PropertyGroup><SelectedImport>root-targets</SelectedImport></PropertyGroup></Project>");
        ProjectCollection collection = environment.CreateProjectCollection().Collection;

        var project = new Project(files.ProjectFile, GraphProperties(root), toolsVersion: null, collection);

        project.GetPropertyValue(DirectoryImportResolver.RepositoryRootProperty)
            .ShouldBe(Path.GetDirectoryName(files.ProjectFile));
        project.GetPropertyValue("NearestDirectory").ShouldBe(Path.Combine(root, "src"));
        project.GetPropertyValue("DefaultFile").ShouldBe(Path.Combine(root, "src", "Directory.Build.props"));
        project.GetPropertyValue("RootFile").ShouldBe(Path.Combine(root, "Directory.Build.targets"));
        project.GetPropertyValue("Nested").ShouldBe("Directory.Packages.props");
        project.GetPropertyValue("Chained").ShouldBe("root-package");
        project.GetPropertyValue("SelectedImport").ShouldBe("root-targets");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraphEvaluationCannotObtainRootFromAuthoredOrEnvironmentProperty(bool supplyEnvironmentRoot)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        TransientTestProjectWithFiles files = environment.CreateTestProjectWithFiles(
            """
            <Project>
              <PropertyGroup>
                <MSBuildGraphRepositoryRoot>$(MSBuildProjectDirectory)</MSBuildGraphRepositoryRoot>
                <Selected>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props'))</Selected>
              </PropertyGroup>
            </Project>
            """,
            ["Directory.Build.props"]);
        environment.SetEnvironmentVariable(
            DirectoryImportResolver.RepositoryRootProperty,
            supplyEnvironmentRoot ? files.TestRoot : null);
        Dictionary<string, string> properties = GraphProperties(files.TestRoot);
        properties.Remove(DirectoryImportResolver.RepositoryRootProperty);
        ProjectCollection collection = environment.CreateProjectCollection().Collection;

        InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(() =>
            new Project(files.ProjectFile, properties, toolsVersion: null, collection));

        exception.ErrorCode.ShouldBe("MSB4286");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryEvaluationIsBoundedOnlyWithExplicitGlobalRoot(bool supplyGlobalRoot)
    {
        using TestEnvironment environment = TestEnvironment.Create();
        TransientTestProjectWithFiles files = environment.CreateTestProjectWithFiles(
            """
            <Project>
              <PropertyGroup>
                <MSBuildGraphRepositoryRoot>$(MSBuildProjectDirectory)</MSBuildGraphRepositoryRoot>
                <SelectedDirectory>$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildProjectDirectory)', 'Directory.Build.props'))</SelectedDirectory>
                <SelectedFile>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props'))</SelectedFile>
              </PropertyGroup>
            </Project>
            """,
            relativePathFromRootToProject: "repo/src");
        string root = Path.Combine(files.TestRoot, "repo");
        string poisonFile = Path.Combine(files.TestRoot, "Directory.Build.props");
        File.WriteAllText(poisonFile, "<not-a-project />");
        Dictionary<string, string> properties = [];
        if (supplyGlobalRoot)
        {
            properties[DirectoryImportResolver.RepositoryRootProperty] = root;
        }

        ProjectCollection collection = environment.CreateProjectCollection().Collection;

        var project = new Project(files.ProjectFile, properties, toolsVersion: null, collection);

        project.GetPropertyValue("SelectedDirectory").ShouldBe(supplyGlobalRoot ? string.Empty : files.TestRoot);
        project.GetPropertyValue("SelectedFile").ShouldBe(supplyGlobalRoot ? string.Empty : poisonFile);
    }

    private static Dictionary<string, string> GraphProperties(string root) => new()
    {
        ["MSBuildEmitActionGraph"] = Path.Combine(root, "graph.json"),
        ["MSBuildGraphHostOS"] = "linux",
        ["MSBuildGraphHostArchitecture"] = "x64",
        [DirectoryImportResolver.RepositoryRootProperty] = root,
    };
}
