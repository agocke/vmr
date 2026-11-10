// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.DotNet.ApiCompat.Task;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.DotNet.ApiCompatibility.Tests;

[TestClass]
public sealed class ExplicitApiCompatFileCatalogTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(ExplicitApiCompatFileCatalogTests), Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void Format_NonexistentCatalog_SelectsOnlyRequestedDirectoryAndRootSiblings()
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.LeftAssemblies = ["left/Contract.dll"];
        task.RightAssemblies = ["right/Contract.dll"];
        task.LeftAssembliesReferences = ["pack/ref"];
        task.SuppressionFiles = ["config/suppressions.xml"];
        task.ExcludeAttributesFiles = ["config/attributes.txt"];
        task.ReferenceFileCandidates = Items(
            "pack/ref/B.dll", "pack/ref/A.dll", "pack/other/Unused.dll",
            "pack/ref/nested/Unused.dll", "left/Dependency.dll", "right/Dependency.dll");

        Assert.IsFalse(Directory.Exists(_root));
        Assert.IsTrue(task.Execute());

        AssertInputs(task, "left/Contract.dll", "left/Dependency.dll",
            "right/Contract.dll", "right/Dependency.dll", "pack/ref/A.dll", "pack/ref/B.dll",
            "config/suppressions.xml", "config/attributes.txt");
        CollectionAssert.AreEqual(SortedPaths("left", "right", "pack/ref", "config"),
            Paths(task.ReferenceDirectories));
        CollectionAssert.AreEqual(new[] { FullPath("out/apicompat.semaphore") }, Paths(task.FileOutputs));
        Assert.IsTrue(task.FileInputs.All(item => item.GetMetadata("RequireProducer") == ""));
        Assert.IsFalse(Directory.Exists(_root), "Graph formatting must not create or discover catalog paths.");
    }

    [TestMethod]
    public void Format_ExplicitReferenceFile_SelectsParentSiblingsWithoutOtherDirectories()
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.LeftAssembliesReferences = ["pack/ref/Requested.dll"];
        task.RightAssembliesReferences = ["other/Tool.exe,metadata/Contract.winmd"];
        task.ReferenceFileCandidates = Items("pack/ref/Sibling.dll", "pack/unused/Ignored.dll");

        Assert.IsTrue(task.Execute());

        AssertInputs(task, "pack/ref/Requested.dll", "pack/ref/Sibling.dll", "other/Tool.exe", "metadata/Contract.winmd");
        CollectionAssert.AreEqual(SortedPaths("pack/ref", "other", "metadata"), Paths(task.ReferenceDirectories));
    }

    [TestMethod]
    public void Format_AcquiredPackageCatalog_IgnoresNonDllMembers()
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.LeftAssembliesReferences = ["pack/ref"];
        task.ReferenceFileCandidates = Items("pack/ref/Contract.DLL", "pack/ref/Contract.xml",
            "pack/ref/Contract.pdb", "pack/ref/tool.exe", "pack/README.md");

        Assert.IsTrue(task.Execute());

        AssertInputs(task, "pack/ref/Contract.DLL");
        CollectionAssert.AreEqual(SortedPaths("pack/ref"), Paths(task.ReferenceDirectories));
    }

    [TestMethod]
    public void Format_ProducedCatalog_OverridesStaleCandidatesAndMarksSelectedFiles()
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.LeftAssemblies = ["generated/Contract.dll"];
        task.LeftAssembliesReferences = ["generated"];
        task.ReferenceFileCandidates = Items("generated/Stale.dll", "generated/Contract.dll",
            "acquired/Unused.dll", "unselected/Stale.dll");
        task.ProducedReferenceDirectories = Items("generated", "unselected");
        task.ProducedReferenceFiles = Items("generated/Contract.dll", "generated/Dependency.dll", "unselected/Unused.dll");

        Assert.IsTrue(task.Execute());

        AssertInputs(task, "generated/Contract.dll", "generated/Dependency.dll");
        CollectionAssert.AreEqual(SortedPaths("generated", "unselected"), Paths(task.ReferenceDirectories));
        CollectionAssert.AreEqual(SortedPaths("generated/Contract.dll", "generated/Dependency.dll"),
            task.FileInputs.Where(item => item.GetMetadata("RequireProducer") == "true").Select(item => item.ItemSpec).ToArray());
        Assert.IsTrue(task.FileInputs.Where(item => !item.ItemSpec.StartsWith(FullPath("generated") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .All(item => item.GetMetadata("RequireProducer") == ""));
    }

    [TestMethod]
    public void Format_EmptyProducedDirectory_IsValidAndDoesNotUseAcquiredMembers()
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.LeftAssembliesReferences = ["generated"];
        task.ProducedReferenceDirectories = Items("generated");
        task.ReferenceFileCandidates = Items("generated/Stale.dll");

        Assert.IsTrue(task.Execute());

        AssertInputs(task);
        CollectionAssert.AreEqual(SortedPaths("generated"), Paths(task.ReferenceDirectories));
    }

    [TestMethod]
    [DataRow("outside/Contract.dll")]
    [DataRow("generated/Contract.xml")]
    public void Format_InvalidProducedFile_RejectsOutsideDirectoryOrNonDll(string member)
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.ProducedReferenceDirectories = Items("generated");
        task.ProducedReferenceFiles = Items(member);

        Assert.ThrowsExactly<ArgumentException>(() => task.Execute());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1")]
    public void Format_UnsupportedSnapshotVersion_IsRejected(string version)
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.FormatVersion = version;

        Assert.ThrowsExactly<ArgumentException>(() => task.Execute());
    }

    [TestMethod]
    [DataRow("unknown/directory")]
    [DataRow("pack/ref/*.dll")]
    [DataRow("pack/ref/Contract?.dll")]
    public void Format_UnknownDirectoryOrWildcardOperand_IsRejected(string operand)
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.ReferenceFileCandidates = Items("pack/ref/Contract.dll");
        task.LeftAssembliesReferences = [operand];

        Assert.ThrowsExactly<ArgumentException>(() => task.Execute());
    }

    [TestMethod]
    public void Format_WildcardCatalogMember_IsRejected()
    {
        FormatApiCompatInputs task = CreateFormatter();
        task.ReferenceFileCandidates = Items("pack/ref/*.dll");

        Assert.ThrowsExactly<ArgumentException>(() => task.Execute());
    }

    [TestMethod]
    public void Format_ReorderedDuplicateCatalogs_AreDeterministicAndPreserveReferenceGroups()
    {
        string[] left = ["second/Contract.dll,first,second/Contract.dll", "first"];
        string[] right = ["first/Dependency.dll,second", "first"];
        string[] originalLeft = (string[])left.Clone();
        string[] originalRight = (string[])right.Clone();
        FormatApiCompatInputs first = CreateFormatter();
        first.LeftAssembliesReferences = left;
        first.RightAssembliesReferences = right;
        first.ReferenceFileCandidates = Items("second/Contract.dll", "first/Dependency.dll",
            "first/./Dependency.dll", "second/Sibling.dll", "first/Dependency.dll");
        FormatApiCompatInputs second = CreateFormatter();
        second.LeftAssembliesReferences = [right[1], right[0]];
        second.RightAssembliesReferences = [left[1], left[0]];
        second.ReferenceFileCandidates = first.ReferenceFileCandidates.Reverse().ToArray();

        Assert.IsTrue(first.Execute());
        Assert.IsTrue(second.Execute());

        AssertInputs(first, "first/Dependency.dll", "second/Contract.dll", "second/Sibling.dll");
        CollectionAssert.AreEqual(Paths(first.FileInputs), Paths(second.FileInputs));
        CollectionAssert.AreEqual(Paths(first.ReferenceDirectories), Paths(second.ReferenceDirectories));
        CollectionAssert.AreEqual(Paths(first.FileOutputs), Paths(second.FileOutputs));
        Assert.AreSame(left, first.LeftAssembliesReferences);
        Assert.AreSame(right, first.RightAssembliesReferences);
        CollectionAssert.AreEqual(originalLeft, left);
        CollectionAssert.AreEqual(originalRight, right);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Prepare_DefaultSuppressionSnapshot_ContainsOnlyVersionAndRawOptionalFile(bool present)
    {
        string suppression = FullPath("config/default$@();'%.xml");
        string output = FullPath("snapshot/inputs.props");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(suppression)!);
            File.WriteAllText(FullPath("config/Unrelated.dll"), "not an assembly");
            if (present)
            {
                File.WriteAllText(suppression, "<Suppressions />");
            }
            PrepareApiCompatInputs task = new()
            {
                TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(_root),
                DefaultSuppressionFile = Path.GetRelativePath(_root, suppression),
                OutputFile = output
            };

            Assert.IsTrue(task.Execute());

            XElement project = XElement.Load(output);
            Assert.AreEqual("Project", project.Name.LocalName);
            XElement properties = project.Elements().Single();
            Assert.AreEqual("PropertyGroup", properties.Name.LocalName);
            CollectionAssert.AreEqual(new[] { "_ApiCompatInputsVersion", "_ApiCompatPreparedDefaultSuppressionFile" },
                properties.Elements().Select(element => element.Name.LocalName).ToArray());
            Assert.AreEqual("2", properties.Element("_ApiCompatInputsVersion")!.Value);
            string expected = present
                ? FullPath("config/default%24%40%28%29%3B%27%25.xml")
                : "";
            Assert.AreEqual(expected, properties.Element("_ApiCompatPreparedDefaultSuppressionFile")!.Value);
            Assert.IsFalse(File.ReadAllBytes(output).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        }
        finally
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private FormatApiCompatInputs CreateFormatter() => new()
    {
        TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(_root),
        FormatVersion = "2",
        RoslynAssembliesPath = "roslyn",
        SemaphoreFile = "out/apicompat.semaphore"
    };

    private static ITaskItem[] Items(params string[] paths)
        => paths.Select(path => (ITaskItem)new TaskItem(path)).ToArray();

    private static string[] Paths(ITaskItem[] items)
        => items.Select(item => item.ItemSpec).ToArray();

    private string FullPath(string path) => Path.GetFullPath(path, _root);

    private string[] SortedPaths(params string[] paths)
        => paths.Select(FullPath).Distinct().Order(StringComparer.Ordinal).ToArray();

    private void AssertInputs(FormatApiCompatInputs task, params string[] expected)
        => CollectionAssert.AreEqual(
            SortedPaths([.. expected, "roslyn/Microsoft.CodeAnalysis.dll", "roslyn/Microsoft.CodeAnalysis.CSharp.dll"]),
            Paths(task.FileInputs));
}
