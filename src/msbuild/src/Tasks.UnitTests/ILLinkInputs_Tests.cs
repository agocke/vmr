// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
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
    public void CapturesAllSearchCandidatesSidecarsSatellitesAndRecursiveAssemblyFiles()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string references = Path.Combine(root, "refs");
        Directory.CreateDirectory(Path.Combine(references, "fr"));
        string first = Path.Combine(references, "First.dll");
        string shadow = Path.Combine(references, "Other.WINMD");
        string module = Path.Combine(references, "first.netmodule");
        string nested = Path.Combine(references, "nested.netmodule");
        string data = Path.Combine(references, "data.bin");
        WriteAssembly(first, (Path.GetFileName(module), true), (Path.GetFileName(data), false));
        WriteAssembly(module, (Path.GetFileName(nested), true));
        WriteAssembly(nested);
        WriteAssembly(shadow);
        string[] optional =
        [
            first + ".config", first + ".mdb", Path.ChangeExtension(first, ".pdb"),
            Path.Combine(references, "fr", "First.resources.dll")
        ];
        foreach (string file in optional.Append(data))
        {
            File.WriteAllText(file, "input");
        }

        string manifest = Path.Combine(root, "inputs.props");
        WriteILLinkInputs capture = new()
        {
            BuildEngine = new MockEngine(output),
            AssemblyPaths = [new TaskItem(first)],
            OutputFile = manifest
        };
        capture.Execute().ShouldBeTrue();
        ResolveILLinkInputs resolve = CreateResolver(root, ReadData(manifest));
        resolve.AssemblyPaths = [new TaskItem(first)];
        resolve.ExtraArgs = $"-d \"{references}\" -x \"{Path.Combine(root, "descriptor.xml")}\" --substitutions \"{Path.Combine(root, "substitutions.xml")}\" --link-attributes \"{Path.Combine(root, "attributes.xml")}\"";
        resolve.ToolFiles = [new TaskItem(Path.Combine(root, "dotnet")), new TaskItem(Path.Combine(root, "illink.dll"))];

        // Selection must use prepared facts even when every live source is unavailable.
        Directory.Move(references, references + ".hidden");
        resolve.Execute().ShouldBeTrue();

        resolve.Inputs.Select(item => item.ItemSpec).ShouldBe(
            new[] { first, shadow, module, nested, data, Path.Combine(root, "descriptor.xml"), Path.Combine(root, "substitutions.xml"),
                Path.Combine(root, "attributes.xml"), Path.Combine(root, "dotnet"), Path.Combine(root, "illink.dll") }
                .Concat(optional).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void FutureProducerInventoryIncludesShadowingCandidatesAndToolClosureWithoutDiscovery()
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
        ResolveILLinkInputs resolve = CreateResolver(root, "<ILLinkInputs Version=\"1\" />");
        resolve.AssemblyPaths = [new TaskItem("PreTrim/Root.dll"), new TaskItem("refs/Reference.dll")];
        resolve.ProducedFiles = absolute.Select(file => (ITaskItem)new TaskItem(file)).ToArray();
        resolve.ToolFiles = [new TaskItem("task/ILLink.Tasks.dll"), new TaskItem("tools/illink.dll")];
        resolve.ExtraArgs = $"--ignore-link-attributes true --skip-unresolved true --trim-mode skip --action skip --action link Root -b true --preserve-symbol-paths --nowarn IL2008;IL2009 --disable-opt ipconstprop -d \"{root}/refs\"";

        resolve.Execute().ShouldBeTrue();

        resolve.Inputs.Select(item => item.ItemSpec).ShouldBe(absolute.OrderBy(path => path, StringComparer.Ordinal));
        Directory.GetFileSystemEntries(root).ShouldBeEmpty();
    }

    [Fact]
    public void PreparedPropertyPreservesLiteralMSBuildEscapes()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string references = Path.Combine(root, "refs %3B;$(Literal)");
        Directory.CreateDirectory(references);
        string assembly = Path.Combine(references, "Reference.dll");
        WriteAssembly(assembly);
        string manifest = Path.Combine(root, "inputs.props");
        WriteILLinkInputs capture = new()
        {
            BuildEngine = new MockEngine(output),
            AssemblyPaths = [new TaskItem(EscapingUtilities.Escape(assembly))],
            OutputFile = manifest
        };
        capture.Execute().ShouldBeTrue();
        using ProjectCollection collection = new();
        string data = collection.LoadProject(manifest).GetPropertyValue("_ILLinkInputsData");
        ResolveILLinkInputs resolve = CreateResolver(root, data);
        resolve.AssemblyPaths = [new TaskItem(EscapingUtilities.Escape(assembly))];
        resolve.ExtraArgs = $"-d \"{references}\"";

        resolve.Execute().ShouldBeTrue();

        resolve.Inputs.Select(item => item.ItemSpec).ShouldBe([assembly]);
    }

    [Theory]
    [InlineData("--custom-step external.dll", "<ILLinkInputs Version=\"1\" />")]
    [InlineData("-d missing", "<ILLinkInputs Version=\"1\" />")]
    [InlineData("-x \"unterminated", "<ILLinkInputs Version=\"1\" />")]
    [InlineData("-x", "<ILLinkInputs Version=\"1\" />")]
    [InlineData("", "<ILLinkInputs Version=\"2\" />")]
    [InlineData("", "<ILLinkInputs Version=\"1\"><Assembly Path=\"relative.dll\" /></ILLinkInputs>")]
    [InlineData("", "<invalid")]
    public void RejectsUnsupportedOrUnpreparedInputsWithoutPartialResults(string arguments, string data)
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        ResolveILLinkInputs resolve = CreateResolver(root, data);
        resolve.ToolFiles = [new TaskItem("dotnet")];
        resolve.ExtraArgs = arguments;

        resolve.Execute().ShouldBeFalse();

        resolve.Inputs.ShouldBeEmpty();
        ((MockEngine)resolve.BuildEngine).Errors.ShouldBe(1);
        Directory.GetFileSystemEntries(root).ShouldBeEmpty();
    }

    [Fact]
    public void PreparationDoesNotReadStaleProductDirectories()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string stale = Path.Combine(root, "products", "stale.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "not a PE file");
        string manifest = Path.Combine(root, "inputs.props");
        WriteILLinkInputs capture = new()
        {
            BuildEngine = new MockEngine(output),
            AssemblyPaths = [new TaskItem(stale)],
            ExcludedDirectories = [new TaskItem(Path.Combine(root, "products"))],
            OutputFile = manifest
        };

        capture.Execute().ShouldBeTrue();
        XElement.Parse(ReadData(manifest)).Elements().ShouldBeEmpty();
    }

    private ResolveILLinkInputs CreateResolver(string root, string data) => new()
    {
        BuildEngine = new MockEngine(output),
        TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(root),
        Data = data
    };

    private static string ReadData(string manifest) =>
        ProjectCollection.Unescape((string)XDocument.Load(manifest).Root!.Element("PropertyGroup")!.Element("_ILLinkInputsData")!);

    private static void WriteAssembly(string path, params (string Name, bool Metadata)[] files)
    {
        MetadataBuilder metadata = new();
        metadata.AddModule(0, metadata.GetOrAddString(Path.GetFileName(path)), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(Path.GetFileNameWithoutExtension(path)),
            new Version(1, 0), default, default, (AssemblyFlags)0, AssemblyHashAlgorithm.None);
        foreach ((string name, bool containsMetadata) in files)
        {
            metadata.AddAssemblyFile(metadata.GetOrAddString(name), default, containsMetadata);
        }
        ManagedPEBuilder pe = new(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder());
        BlobBuilder image = new();
        pe.Serialize(image);
        File.WriteAllBytes(path, image.ToArray());
    }
}
