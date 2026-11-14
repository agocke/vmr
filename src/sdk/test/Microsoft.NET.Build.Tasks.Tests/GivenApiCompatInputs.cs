// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.TestFramework;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.NET.Build.Tasks.UnitTests;

[TestClass]
public sealed class GivenApiCompatInputs : SdkTest
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(GivenApiCompatInputs), Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Translate_EquivalentBindings_ShareFactsAndPreserveLiteralPaths(bool present)
    {
        string input = Path.Combine(_root, "100%;$(Literal)", "CompatibilitySuppressions.xml");
        string output = Path.Combine(_root, "obj", "apicompatinputs", "Project.csproj.g.props");
        if (present)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            File.WriteAllText(input, "Only presence, not suppression content, is translated.");
        }
        WriteApiCompatInputs task = Create(Binding(input, output), Binding(input, output));

        Assert.IsTrue(task.Execute());

        Assert.AreEqual(1, task.WrittenModels);
        XElement properties = XElement.Load(output).Element("PropertyGroup")!;
        Assert.AreEqual("2", properties.Element("_ApiCompatInputsVersion")!.Value);
        Assert.AreEqual(present ? input : "",
            ProjectCollection.Unescape(properties.Element("_ApiCompatPreparedDefaultSuppressionFile")!.Value));
        Assert.HasCount(2, properties.Elements());
    }

    [TestMethod]
    public void Translate_UpToDateAcquisition_RepublishesFactsAndRefreshesPresence()
    {
        string input = Path.Combine(_root, "CompatibilitySuppressions.xml");
        string output = Path.Combine(_root, "obj", "Project.g.props");
        Directory.CreateDirectory(_root);
        File.WriteAllText(input, "<Suppressions />");
        WriteApiCompatInputs task = Create(Binding(input, output));
        Assert.IsTrue(task.Execute());
        string original = File.ReadAllText(output);
        DateTime timestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(output, timestamp);

        Assert.IsTrue(task.Execute());
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(output), "Identical facts must not be rewritten.");
        File.Delete(output);
        Assert.IsTrue(task.Execute());
        Assert.AreEqual(original, File.ReadAllText(output));
        File.Delete(input);
        Assert.IsTrue(task.Execute());
        Assert.AreEqual("", XElement.Load(output).Element("PropertyGroup")!
            .Element("_ApiCompatPreparedDefaultSuppressionFile")!.Value);
    }

    [TestMethod]
    [DataRow("conflicting-inputs")]
    [DataRow("input-output")]
    [DataRow("relative-input")]
    [DataRow("relative-output")]
    [DataRow("missing-input")]
    [DataRow("missing-output")]
    public void Translate_InvalidBindings_RejectBeforePublishing(string invalid)
    {
        string input = Path.Combine(_root, "CompatibilitySuppressions.xml");
        string output = Path.Combine(_root, "obj", "Project.g.props");
        Directory.CreateDirectory(_root);
        File.WriteAllText(input, "<Suppressions />");
        ITaskItem bad = invalid switch
        {
            "conflicting-inputs" => Binding(Path.Combine(_root, "Other.xml"), output),
            "input-output" => Binding(Path.Combine(_root, "Other.xml"), input),
            "relative-input" => Binding("CompatibilitySuppressions.xml", Path.Combine(_root, "Other.g.props")),
            "relative-output" => Binding(input, "Other.g.props"),
            "missing-input" => Binding("", Path.Combine(_root, "Other.g.props")),
            _ => Binding(input, ""),
        };
        var engine = new MockBuildEngine();
        WriteApiCompatInputs task = Create(Binding(input, output), bad);
        task.BuildEngine = engine;

        Assert.IsFalse(task.Execute());

        Assert.HasCount(1, engine.Errors);
        Assert.AreEqual(0, task.WrittenModels);
        Assert.IsFalse(File.Exists(output), "Admission must finish before any facts are published.");
        Assert.AreEqual("<Suppressions />", File.ReadAllText(input));
    }

    private static WriteApiCompatInputs Create(params ITaskItem[] bindings)
        => new() { BuildEngine = new MockBuildEngine(), Requests = bindings };

    private static ITaskItem Binding(string input, string output)
    {
        var item = new TaskItem(Guid.NewGuid().ToString());
        item.SetMetadata("DefaultSuppressionFile", ProjectCollection.Escape(input));
        item.SetMetadata("OutputFile", ProjectCollection.Escape(output));
        return item;
    }
}
