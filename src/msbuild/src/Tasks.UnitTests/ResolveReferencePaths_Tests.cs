// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.Tasks.UnitTests;

public sealed class ResolveReferencePaths_Tests(ITestOutputHelper output)
{
    [Fact]
    public void ResolvesGeneratedArtifactsWithoutOpeningThemAndPreservesMetadata()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        TaskItem reference = new("missing/renamed.dll");
        reference.SetMetadata("ReferenceAssembly", "missing/ref/renamed.dll");
        reference.SetMetadata("DebugSymbolsFile", "missing/renamed.pdb");
        reference.SetMetadata("ReferenceSourceTarget", "ProjectReference");
        reference.SetMetadata("OriginalProjectReferenceItemSpec", "../DifferentProject/Project.csproj");
        reference.SetMetadata("Aliases", "first,second");
        reference.SetMetadata("Custom", "literal%3bvalue");
        ResolveReferencePaths task = CreateTask(root, reference);

        task.Execute().ShouldBeTrue();

        ITaskItem resolved = task.ResolvedFiles.ShouldHaveSingleItem();
        resolved.ItemSpec.ShouldBe(Path.Combine(root, "missing", "renamed.dll"));
        resolved.GetMetadata("ReferenceAssembly").ShouldBe(Path.Combine(root, "missing", "ref", "renamed.dll"));
        resolved.GetMetadata("Aliases").ShouldBe("first,second");
        resolved.GetMetadata("Custom").ShouldBe(reference.GetMetadata("Custom"));
        resolved.GetMetadata("ProjectReferenceOriginalItemSpec").ShouldBe("../DifferentProject/Project.csproj");
        resolved.GetMetadata("CopyLocal").ShouldBe("true");
        task.RelatedFiles.ShouldHaveSingleItem().ItemSpec.ShouldBe(Path.Combine(root, "missing", "renamed.pdb"));
        task.CopyLocalFiles.Length.ShouldBe(2);
        reference.ItemSpec.ShouldBe("missing/renamed.dll");
        reference.GetMetadata("CopyLocal").ShouldBeEmpty();
        Directory.Exists(Path.Combine(root, "missing")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "", "", true)]
    [InlineData("false", "", "", false)]
    [InlineData("true", "false", "", true)]
    [InlineData("", "false", "", false)]
    [InlineData("", "", "true", false)]
    public void HonorsExplicitCopyLocalAndFrameworkMetadata(string privateValue, string copyLocal, string frameworkFile, bool expected)
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        TaskItem reference = new("reference.dll");
        reference.SetMetadata("Private", privateValue);
        reference.SetMetadata("CopyLocal", copyLocal);
        reference.SetMetadata("FrameworkFile", frameworkFile);
        reference.SetMetadata("DebugSymbolsFile", "reference.pdb");
        ResolveReferencePaths task = CreateTask(env.CreateFolder().Path, reference);

        task.Execute().ShouldBeTrue();

        task.ResolvedFiles.ShouldHaveSingleItem().GetMetadata("CopyLocal").ShouldBe(expected ? "true" : "false");
        task.CopyLocalFiles.Length.ShouldBe(expected ? 2 : 0);
    }

    [Theory]
    [InlineData("System.Runtime")]
    [InlineData("Example, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null")]
    [InlineData("{GAC}")]
    [InlineData("{TargetFrameworkDirectory}/Example.dll")]
    [InlineData("missing/*.dll")]
    [InlineData("https://example.invalid/Example.dll")]
    [InlineData("file:/Example.dll")]
    [InlineData("C:Example.dll")]
    [InlineData("missing/")]
    [InlineData("missing/..")]
    [InlineData("missing/.")]
    public void RejectsNamesAndSearchesEvenWithAnExplicitHintPath(string value)
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        TaskItem reference = new(value);
        reference.SetMetadata("HintPath", Path.Combine(env.CreateFolder().Path, "Example.dll"));
        ResolveReferencePaths task = CreateTask(env.CreateFolder().Path, new TaskItem("valid.dll"), reference);

        task.Execute().ShouldBeFalse();

        ((MockEngine)task.BuildEngine).Log.ShouldContain("MSB3671");
        task.ResolvedFiles.ShouldBeEmpty();
        task.CopyLocalFiles.ShouldBeEmpty();
    }

    [Fact]
    public void ReportsInvalidPathCharactersWithoutThrowing()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        ResolveReferencePaths task = CreateTask(env.CreateFolder().Path, new TaskItem("invalid\0.dll"));

        task.Execute().ShouldBeFalse();

        ((MockEngine)task.BuildEngine).Errors.ShouldBe(1);
        task.ResolvedFiles.ShouldBeEmpty();
    }

    [Fact]
    public void PreservesExplicitProjectReferenceIdentity()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        TaskItem reference = new("reference.dll");
        reference.SetMetadata("ReferenceSourceTarget", "ProjectReference");
        reference.SetMetadata("ProjectReferenceOriginalItemSpec", "../Explicit/Project.csproj");
        ResolveReferencePaths task = CreateTask(env.CreateFolder().Path, reference);

        task.Execute().ShouldBeTrue();

        task.ResolvedFiles.ShouldHaveSingleItem().GetMetadata("ProjectReferenceOriginalItemSpec").ShouldBe("../Explicit/Project.csproj");
    }

    [Theory]
    [InlineData("ReferenceAssembly", "AssemblyName")]
    [InlineData("DebugSymbolsFile", "*.pdb")]
    [InlineData("Private", "not-a-boolean")]
    public void RejectsInvalidMetadataWithoutPartialResults(string name, string value)
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        TaskItem reference = new("reference.dll");
        reference.SetMetadata(name, value);
        ResolveReferencePaths task = CreateTask(env.CreateFolder().Path, reference);

        task.Execute().ShouldBeFalse();

        ((MockEngine)task.BuildEngine).Errors.ShouldBe(1);
        task.ResolvedFiles.ShouldBeEmpty();
        task.RelatedFiles.ShouldBeEmpty();
        task.CopyLocalFiles.ShouldBeEmpty();
    }

    private ResolveReferencePaths CreateTask(string root, params ITaskItem[] references) => new()
    {
        BuildEngine = new MockEngine(output),
        TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(root),
        References = references
    };
}
