// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.Tasks.UnitTests;

public sealed class ILLinkInputs_Tests(ITestOutputHelper output)
{
    [Fact]
    public void ExplicitInputsDoNotReadAssembliesOrDiscoverAdjacentFiles()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string references = Path.Combine(root, "refs");
        Directory.CreateDirectory(references);
        File.WriteAllText(Path.Combine(references, "Reference.dll"), "not a PE file");
        File.WriteAllText(Path.Combine(references, "Shadow.dll"), "undeclared");
        File.WriteAllText(Path.Combine(references, "Reference.dll.config"), "undeclared");
        ResolveILLinkInputs resolve = CreateResolver(root);
        resolve.AssemblyPaths = [new TaskItem("refs/Reference.dll")];
        resolve.CompanionFiles = [new TaskItem("refs/Reference.pdb"), new TaskItem("refs/module.netmodule")];
        resolve.ToolFiles = [new TaskItem("dotnet"), new TaskItem("illink.dll")];
        resolve.ExtraArgs = "-x \"descriptor with spaces.xml\" --substitutions substitutions.xml --link-attributes attributes.xml";

        resolve.Execute().ShouldBeTrue();

        resolve.Inputs.Select(item => item.ItemSpec).ShouldBe(
            new[] { "refs/Reference.dll", "refs/Reference.pdb", "refs/module.netmodule", "dotnet", "illink.dll",
                "descriptor with spaces.xml", "substitutions.xml", "attributes.xml" }
                .Select(file => Path.Combine(root, file)).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void FutureProducerFilesIncludeCompanionsAndToolClosureWithoutDiscovery()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string[] files =
        [
            "PreTrim/Root.dll", "PreTrim/Root.pdb", "PreTrim/Root.dll.config", "PreTrim/Root.dll.mdb",
            "PreTrim/module.netmodule", "PreTrim/fr/Root.resources.dll",
            "refs/Reference.dll", "refs/Shadow.exe", "refs/Shadow.pdb",
            "task/ILLink.Tasks.dll", "task/ILLink.Tasks.deps.json", "tools/illink.dll", "tools/illink.deps.json",
            "tools/illink.runtimeconfig.json", "tools/Mono.Cecil.dll"
        ];
        string[] absolute = files.Select(file => Path.GetFullPath(Path.Combine(root, file))).ToArray();
        ResolveILLinkInputs resolve = CreateResolver(root);
        resolve.AssemblyPaths = [new TaskItem("PreTrim/Root.dll"), new TaskItem("refs/Reference.dll")];
        resolve.ProducedFiles = absolute.Append(Path.Combine(root, "unrelated", "Other.dll"))
            .Select(file => (ITaskItem)new TaskItem(file)).ToArray();
        resolve.ToolFiles = [new TaskItem("task/ILLink.Tasks.dll"), new TaskItem("tools/illink.dll")];
        resolve.ExtraArgs = "--ignore-link-attributes true --skip-unresolved true --trim-mode skip --action skip --action link Root -b true --preserve-symbol-paths --nowarn IL2008;IL2009 --disable-opt ipconstprop";

        resolve.Execute().ShouldBeTrue();

        resolve.Inputs.Select(item => item.ItemSpec).ShouldBe(absolute.OrderBy(path => path, StringComparer.Ordinal));
        Directory.GetFileSystemEntries(root).ShouldBeEmpty();
    }

    [Fact]
    public void ExplicitItemsPreserveLiteralMSBuildEscapes()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string assembly = Path.Combine(root, "refs %3B;$(Literal)", "Reference.dll");
        ResolveILLinkInputs resolve = CreateResolver(root);
        resolve.AssemblyPaths = [new TaskItem(EscapingUtilities.Escape(assembly))];

        resolve.Execute().ShouldBeTrue();

        resolve.Inputs.Select(item => item.ItemSpec).ShouldBe([assembly]);
        Directory.GetFileSystemEntries(root).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("--custom-step external.dll")]
    [InlineData("-d missing")]
    [InlineData("\"-d\" \"directory with spaces\"")]
    [InlineData("@inputs.rsp")]
    [InlineData("--link-attributes @inputs.txt")]
    [InlineData("--explicit-io")]
    [InlineData("-reference undeclared.dll")]
    [InlineData("-x \"unterminated")]
    [InlineData("-x")]
    [InlineData("-x *.xml")]
    public void RejectsImplicitOrUnsupportedInputsWithoutPartialResults(string arguments)
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        ResolveILLinkInputs resolve = CreateResolver(root);
        resolve.ToolFiles = [new TaskItem("dotnet")];
        resolve.ExtraArgs = arguments;

        resolve.Execute().ShouldBeFalse();

        resolve.Inputs.ShouldBeEmpty();
        ((MockEngine)resolve.BuildEngine).Errors.ShouldBe(1);
        Directory.GetFileSystemEntries(root).ShouldBeEmpty();
    }

    private ResolveILLinkInputs CreateResolver(string root) => new()
    {
        BuildEngine = new MockEngine(output),
        TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(root)
    };
}
