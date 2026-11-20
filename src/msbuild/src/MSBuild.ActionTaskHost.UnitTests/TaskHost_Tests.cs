// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;

namespace Microsoft.Build.ActionTaskHost.UnitTests;

public sealed class TaskHost_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [LinuxOnlyTheory]
    [InlineData("private", true)]
    [InlineData("public", false)]
    [InlineData("nonempty", false)]
    public void ValidatesAndAdoptsLauncherOwnedStagingRoot(string kind, bool expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string staging = Path.Combine(directory, "launcher-root");
        Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (kind == "public")
        {
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        }
        else if (kind == "nonempty")
        {
            File.WriteAllText(Path.Combine(staging, "untrusted"), "not a fresh root");
        }

        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), ProbeParameters());
        Run(manifest, out string log, staging).ShouldBe(expected, log);
        if (expected)
        {
            using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "probe.json")));
            result.RootElement.GetProperty("workingDirectory").GetString()!.ShouldStartWith(staging + "/execroot/");
            result.RootElement.GetProperty("constructorTemporaryDirectory").GetString().ShouldBe(staging);
            Directory.Exists(staging).ShouldBeFalse();
        }
        else
        {
            Directory.Exists(staging).ShouldBeTrue();
            File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
        }
    }

    [LinuxOnlyTheory]
    [InlineData("MSBUILD_ACTION_STAGING_ROOT")]
    [InlineData("MSBUILD_ACTION_BROKER_LAUNCHER")]
    [InlineData("MSBUILD_ACTION_BROKER_SOCKET")]
    public void RejectsCapturedLauncherControlVariable(string variable)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), ProbeParameters(),
            new() { [variable] = directory });
        Run(manifest, out string log).ShouldBeFalse();
        log.ShouldContain("cannot be captured");
        File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
    }

    [LinuxOnlyTheory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    public void CapturesCompleteBrokerContextOutsideLogicalEnvironment(bool launcher, bool socket, bool root, bool expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string staging = Path.Combine(directory, "launcher-root");
        Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Dictionary<string, string> controls = [];
        if (launcher)
        {
            controls["MSBUILD_ACTION_BROKER_LAUNCHER"] = "/bin/true";
        }
        if (socket)
        {
            controls["MSBUILD_ACTION_BROKER_SOCKET"] = "@test-broker";
        }

        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), ProbeParameters());
        Run(manifest, out string log, root ? staging : null, controls).ShouldBe(expected, log);
        if (expected)
        {
            using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "probe.json")));
            result.RootElement.GetProperty("brokerLauncher").GetString().ShouldBe("/bin/true");
            result.RootElement.GetProperty("brokerSocket").GetString().ShouldBe("@test-broker");
            result.RootElement.GetProperty("logicalBrokerVariable").ValueKind.ShouldBe(JsonValueKind.Null);
            result.RootElement.GetProperty("ambientBrokerVariable").ValueKind.ShouldBe(JsonValueKind.Null);
            Directory.Exists(staging).ShouldBeTrue();
        }
        else
        {
            log.ShouldContain("requires a staging root");
            File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
        }
    }

    [LinuxOnlyFact]
    public void ExecutesWriteLinesToFileFromInvocationFile()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder(subfolder: "action host $ files").Path;
        string destination = Path.Combine(directory, "result %3B.txt");
        string manifest = WriteInvocation(directory, typeof(WriteLinesToFile), new()
        {
            ["File"] = destination,
            ["Lines"] = new[] { "first;line", "literal%3Bvalue", "last\\line", destination },
            ["Overwrite"] = "yes",
            ["DeclaredInputs"] = Array.Empty<string>(),
            ["DeclaredOutputs"] = new[] { destination },
        });

        bool succeeded = Run(manifest, out _);

        succeeded.ShouldBeTrue();
        File.ReadAllLines(destination).ShouldBe(["first;line", "literal%3Bvalue", "last\\line", destination]);
    }

    [LinuxOnlyFact]
    public void StagesInProcessReadsAndWritesWithoutChangingLogicalValues()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string input = env.CreateFile(Path.Combine(directory, "input %3B.txt"), "declared contents").Path;
        string destination = Path.Combine(directory, "probe.json");
        Dictionary<string, object?> parameters = ProbeParameters();
        parameters["InputFile"] = input;
        parameters["Value"] = input;
        parameters["Items"] = new[] { new { itemSpec = Path.GetFileName(input), metadata = new { Tag = destination } } };
        parameters["CaptureFullPath"] = true;
        parameters["DeclaredInputs"] = new[] { input };
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters, new() { ["TMPDIR"] = directory });

        Run(manifest, out _).ShouldBeTrue();

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(destination));
        JsonElement root = result.RootElement;
        root.GetProperty("inputContents").GetString().ShouldBe("declared contents");
        root.GetProperty("value").GetString().ShouldBe(input);
        root.GetProperty("logicalInput").GetString().ShouldBe(input);
        root.GetProperty("projectDirectory").GetString().ShouldBe(directory);
        root.GetProperty("items")[0].GetProperty("itemSpec").GetString().ShouldBe(Path.GetFileName(input));
        root.GetProperty("items")[0].GetProperty("tag").GetString().ShouldBe(destination);
        root.GetProperty("items")[0].GetProperty("fullPath").GetString().ShouldBe(input);
        string physicalInput = root.GetProperty("inputPath").GetString()!;
        string physicalOutput = root.GetProperty("outputPath").GetString()!;
        physicalInput.ShouldEndWith("/execroot/root" + input);
        physicalOutput.ShouldEndWith("/execroot/root" + destination);
        root.GetProperty("workingDirectory").GetString().ShouldBe(Path.GetDirectoryName(physicalOutput));
        Directory.Exists(physicalOutput[..physicalOutput.IndexOf("/execroot/", StringComparison.Ordinal)]).ShouldBeFalse();
        File.ReadAllText(input).ShouldBe("declared contents");
    }

    [LinuxOnlyFact]
    public void ExecutesCopyFilesWithDeclaredPathResolution()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string input = env.CreateFile(Path.Combine(directory, "source.txt"), "copied contents").Path;
        string destination = Path.Combine(directory, "nested", "copy.txt");
        string manifest = WriteInvocation(directory, typeof(CopyFiles), new()
        {
            ["SourceFiles"] = new[] { input },
            ["DestinationFiles"] = new[] { destination },
            ["DeclaredInputs"] = new[] { input },
            ["DeclaredOutputs"] = new[] { destination },
        }, new() { ["TMPDIR"] = directory });

        Run(manifest, out _).ShouldBeTrue();

        File.ReadAllText(destination).ShouldBe("copied contents");
        File.ReadAllText(input).ShouldBe("copied contents");
        Directory.GetDirectories(directory, "msbuild-stage-*").ShouldBeEmpty();
    }

    [LinuxOnlyTheory]
    [InlineData("resources")]
    [InlineData("attributes")]
    [InlineData("xslt")]
    public void StagesCoreFileTasksWithoutRewritingContents(string kind)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string source = env.CreateFile(Path.Combine(directory, "source.xml"),
            "<root><data name=\"Message\"><value>hello</value></data></root>").Path;
        string destination = Path.Combine(directory, "result." + (kind == "resources" ? "resources" : "txt"));
        Type type;
        Dictionary<string, object?> parameters = new()
        {
            ["DeclaredInputs"] = Array.Empty<string>(),
            ["DeclaredOutputs"] = new[] { destination },
        };
        if (kind == "resources")
        {
            string resx = env.CreateFile(Path.Combine(directory, "source.resx"), File.ReadAllText(source)).Path;
            type = typeof(CompileResources);
            parameters["SourceFile"] = resx;
            parameters["OutputFile"] = destination;
            parameters["DeclaredInputs"] = new[] { resx };
        }
        else if (kind == "attributes")
        {
            type = typeof(WriteCodeFragment);
            parameters["Language"] = "C#";
            parameters["OutputFile"] = destination;
            parameters["AssemblyAttributes"] = new[]
            {
                new { itemSpec = "System.Reflection.AssemblyMetadataAttribute", metadata = new { _Parameter1 = "logical", _Parameter2 = source } },
            };
        }
        else
        {
            string stylesheet = env.CreateFile(Path.Combine(directory, "style.xsl"),
                "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
                "<xsl:param name=\"logical\"/><xsl:output method=\"text\"/>" +
                "<xsl:template match=\"/\"><xsl:value-of select=\"$logical\"/></xsl:template></xsl:stylesheet>").Path;
            type = typeof(XslTransformation);
            parameters["XmlInputPaths"] = new[] { source };
            parameters["XslInputPath"] = stylesheet;
            parameters["OutputPaths"] = new[] { destination };
            parameters["Parameters"] = "<Parameter Name=\"logical\" Value=\"" + source + "\"/>";
            parameters["ProhibitDtd"] = true;
            parameters["DeclaredInputs"] = new[] { source, stylesheet };
        }

        string manifest = WriteInvocation(directory, type, parameters, new() { ["TMPDIR"] = directory });
        Run(manifest, out string log).ShouldBeTrue(log);

        if (kind == "resources")
        {
            using ResourceReader reader = new(destination);
            IDictionaryEnumerator entries = reader.GetEnumerator();
            entries.MoveNext().ShouldBeTrue();
            entries.Key.ShouldBe("Message");
            entries.Value.ShouldBe("hello");
            entries.MoveNext().ShouldBeFalse();
        }
        else
        {
            string text = File.ReadAllText(destination);
            System.Text.RegularExpressions.Regex.Replace(text, "\"\\s*\\+\\s*\"", string.Empty).ShouldContain(source);
            File.ReadAllText(destination).ShouldNotContain("msbuild-stage-");
        }

        Directory.GetDirectories(directory, "msbuild-stage-*").ShouldBeEmpty();
    }

    [LinuxOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolTasksUseHostStagingWithoutNesting(bool useSandbox)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string script = env.CreateFile(Path.Combine(directory, "probe.sh"),
            "IFS= read -r line < \"$2\"\nprintf '%s\\n%s\\n%s\\n' \"$line\" \"$PWD\" \"$TMPDIR\" > \"$1\"\n").Path;
        string destination = Path.Combine(directory, "result.txt");
        string manifest = WriteInvocation(directory, typeof(HostedToolProbe), new()
        {
            ["Script"] = script,
            ["Destination"] = destination,
            ["UseSandbox"] = useSandbox,
            ["DeclaredInputs"] = new[] { "/bin/sh", script },
            ["DeclaredOutputs"] = new[] { destination },
        }, new() { ["TMPDIR"] = directory });

        Run(manifest, out string log).ShouldBeTrue(log);

        string[] lines = File.ReadAllLines(destination);
        lines[0].ShouldBe("literal response contents");
        lines[1].ShouldEndWith("/execroot/root" + directory);
        lines[2].ShouldEndWith("/scratch");
        lines[2].Split("msbuild-stage-").Length.ShouldBe(2);
        Directory.GetDirectories(directory, "msbuild-stage-*").ShouldBeEmpty();
    }

    [LinuxOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalizesGeneratedDyndepBeforePublishingAnyOutput(bool malformed)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string root = Path.Combine(directory, "generated");
        string archive = env.CreateFile(root + ".tree.zip", "previous archive").Path;
        string dyndep = env.CreateFile(root + ".tree.dd", "previous dyndep").Path;
        string manifest = WriteInvocation(directory, typeof(Microsoft.CodeAnalysis.BuildTasks.CompileManaged), new()
        {
            ["Malformed"] = malformed,
            ["DeclaredInputs"] = Array.Empty<string>(),
            ["DeclaredOutputs"] = new[] { archive, dyndep },
            ["DeclaredOutputTrees"] = new[]
            {
                new { itemSpec = root, metadata = new { ArchiveFile = archive, DyndepFile = dyndep, ReceiptFile = root + ".tree.json" } },
            },
        }, new() { ["TMPDIR"] = directory });

        Run(manifest, out _).ShouldBe(!malformed);

        if (malformed)
        {
            File.ReadAllText(archive).ShouldBe("previous archive");
            File.ReadAllText(dyndep).ShouldBe("previous dyndep");
        }
        else
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
            zip.Entries.Single().FullName.ShouldBe("nested/Generated.cs");
            File.ReadAllText(dyndep).ShouldContain(root + "/nested/Generated.cs");
            File.ReadAllText(dyndep).ShouldNotContain("msbuild-stage-");
        }

        Directory.Exists(root).ShouldBeFalse();
        Directory.GetDirectories(directory, "msbuild-stage-*").ShouldBeEmpty();
    }

    [LinuxOnlyTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsUndeclaredFileResolutionWithoutPublishing(bool input)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string existing = env.CreateFile(Path.Combine(directory, "probe.json"), "previous output").Path;
        env.CreateFile(Path.Combine(directory, "undeclared.txt"), "must not consume");
        Dictionary<string, object?> parameters = ProbeParameters();
        parameters[input ? "InputFile" : "File"] = "undeclared.txt";
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters, new() { ["TMPDIR"] = directory });

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldContain("not a declared " + (input ? "input" : "output"));
        File.ReadAllText(existing).ShouldBe("previous output");
        File.ReadAllText(Path.Combine(directory, "undeclared.txt")).ShouldBe("must not consume");
        Directory.GetDirectories(directory, "msbuild-stage-*").ShouldBeEmpty();
    }

    [Fact]
    public void OrdinaryFileResolutionRetainsProjectRelativeIO()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string input = env.CreateFile(Path.Combine(directory, "source.txt"), "ordinary contents").Path;
        TaskEnvironment environment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(directory, new Dictionary<string, string>());
        using (FileStream source = File.OpenRead(environment.GetInputPath("source.txt")))
        using (FileStream destination = File.Create(environment.GetOutputPath("destination.txt")))
        {
            source.CopyTo(destination);
        }

        File.ReadAllText(Path.Combine(directory, "destination.txt")).ShouldBe("ordinary contents");
        environment.GetAbsolutePath("source.txt").Value.ShouldBe(input);
        Directory.GetDirectories(directory, "msbuild-stage-*").ShouldBeEmpty();
    }

    [Fact]
    public void RejectsUnmigratedContractBeforeConstructingTask()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string manifest = WriteInvocation(directory, typeof(UnmigratedTask), ProbeParameters());

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldContain("SandboxVersion=1");
        log.ShouldNotContain("unmigrated-constructor-executed");
        File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
    }

    [LinuxOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparesDeclaredOutputParentsBeforeExecutingTask(bool absolute)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string destination = Path.Combine("nested $ parent", "deeper", "probe.json");
        if (absolute)
        {
            destination = Path.Combine(directory, destination);
        }

        Dictionary<string, object?> parameters = ProbeParameters();
        parameters["File"] = destination;
        parameters["DeclaredOutputs"] = new[] { destination };
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters);

        Directory.Exists(Path.Combine(directory, "nested $ parent")).ShouldBeFalse();
        Run(manifest, out _).ShouldBeTrue();

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, destination)));
        result.RootElement.GetProperty("engineLoaded").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void InvalidFootprintDoesNotCreateOutputParents()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = ProbeParameters();
        parameters["File"] = "must-not-create/probe.json";
        parameters["DeclaredInputs"] = new[] { "missing-input.txt" };
        parameters["DeclaredOutputs"] = new[] { "must-not-create/probe.json" };
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters);

        Run(manifest, out _).ShouldBeFalse();

        Directory.Exists(Path.Combine(directory, "must-not-create")).ShouldBeFalse();
    }

    [LinuxOnlyFact]
    public void OutputParentFileCollisionDoesNotRemoveTheFile()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string collision = Path.Combine(directory, "blocked");
        File.WriteAllText(collision, "keep-existing-file");
        Dictionary<string, object?> parameters = ProbeParameters();
        parameters["File"] = "blocked/probe.json";
        parameters["DeclaredOutputs"] = new[] { "blocked/probe.json" };
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters);

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldContain("blocked");
        File.ReadAllText(collision).ShouldBe("keep-existing-file");
    }

    [LinuxOnlyFact]
    public void PreservesTypedValuesItemsMetadataAndExplicitEnvironmentWithoutEngine()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("ACTION_HOST_AMBIENT", "must-not-leak");
        string directory = env.CreateFolder().Path;
        string destination = Path.Combine(directory, "bound.json");
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), new()
        {
            ["File"] = "bound.json",
            ["Value"] = "",
            ["Values"] = Array.Empty<string>(),
            ["Enabled"] = "on",
            ["Count"] = 42,
            ["Mode"] = "second",
            ["Items"] = new object[]
            {
                new { itemSpec = "one%3B;$(literal)\\value", metadata = new { Tag = "tag%25;value" } },
                "two",
            },
            ["Forbidden"] = "",
            ["DeclaredInputs"] = Array.Empty<string>(),
            ["DeclaredOutputs"] = new[] { "bound.json" },
            ["RequiredItems"] = Array.Empty<string>(),
        }, new() { ["ACTION_HOST_EXPLICIT"] = "present" });

        Run(manifest, out _).ShouldBeTrue();

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(destination));
        JsonElement root = result.RootElement;
        root.GetProperty("value").GetString().ShouldBe("");
        root.GetProperty("values").GetArrayLength().ShouldBe(0);
        root.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        root.GetProperty("count").GetInt32().ShouldBe(42);
        root.GetProperty("mode").GetString().ShouldBe("Second");
        root.GetProperty("items")[0].GetProperty("itemSpec").GetString().ShouldBe("one%3B;$(literal)\\value");
        root.GetProperty("items")[0].GetProperty("tag").GetString().ShouldBe("tag%25;value");
        root.GetProperty("items")[1].GetProperty("itemSpec").GetString().ShouldBe("two");
        root.GetProperty("projectDirectory").GetString().ShouldBe(directory);
        root.GetProperty("workingDirectory").GetString().ShouldEndWith("/execroot/root" + directory);
        root.GetProperty("explicitEnvironment").GetString().ShouldBe("present");
        root.GetProperty("ambientEnvironment").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("engineLoaded").GetBoolean().ShouldBeFalse();
        root.GetProperty("processId").GetInt32().ShouldNotBe(Environment.ProcessId);
    }

    [LinuxOnlyFact]
    public void BindsCapturedParametersUsingActualPropertyTypesWithoutReevaluation()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = CapturedProbeParameters();
        parameters["Value"] = CapturedParameter("literal%3B;$(MustNotExpand);@(NoItems)", null);
        parameters["Values"] = CapturedParameter("first;part;second$(literal)",
            [CapturedItem("first;part"), CapturedItem("second$(literal)")]);
        parameters["Enabled"] = CapturedParameter("on", [CapturedItem("on")]);
        parameters["Count"] = CapturedParameter("42", [CapturedItem("42")]);
        parameters["Mode"] = CapturedParameter("second", [CapturedItem("second")]);
        parameters["Items"] = CapturedParameter("one%3B;$(literal);two",
        [
            new { itemSpec = "one%3B;$(literal)", metadata = new { Tag = "tag%25;$(metadata)" } },
            CapturedItem("two"),
        ]);
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters, version: 2);

        Run(manifest, out _).ShouldBeTrue();

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "probe.json")));
        JsonElement root = result.RootElement;
        root.GetProperty("value").GetString().ShouldBe("literal%3B;$(MustNotExpand);@(NoItems)");
        root.GetProperty("values").EnumerateArray().Select(value => value.GetString()).ShouldBe(["first;part", "second$(literal)"]);
        root.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        root.GetProperty("count").GetInt32().ShouldBe(42);
        root.GetProperty("mode").GetString().ShouldBe("Second");
        root.GetProperty("items").GetArrayLength().ShouldBe(2);
        root.GetProperty("items")[0].GetProperty("itemSpec").GetString().ShouldBe("one%3B;$(literal)");
        root.GetProperty("items")[0].GetProperty("tag").GetString().ShouldBe("tag%25;$(metadata)");
        root.GetProperty("items")[1].GetProperty("itemSpec").GetString().ShouldBe("two");
        root.GetProperty("engineLoaded").GetBoolean().ShouldBeFalse();
    }

    [LinuxOnlyTheory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void OmittedOrCapturedEmptyOptionalParametersKeepTaskDefaults(int version, bool supplyEmpty)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = version == 1 ? ProbeParameters() : CapturedProbeParameters();
        if (supplyEmpty)
        {
            foreach (string name in new[] { "Value", "Values", "Items", "OptionalItem" })
            {
                parameters[name] = CapturedParameter("", []);
            }
        }

        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters, version: version);

        Run(manifest, out _).ShouldBeTrue();

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "probe.json")));
        result.RootElement.GetProperty("value").GetString().ShouldBe("default");
        result.RootElement.GetProperty("values").EnumerateArray().Select(value => value.GetString()).ShouldBe(["default"]);
        result.RootElement.GetProperty("items").GetArrayLength().ShouldBe(1);
        result.RootElement.GetProperty("items")[0].GetProperty("itemSpec").GetString().ShouldBe("constructor-item");
        result.RootElement.GetProperty("optionalItem").GetString().ShouldBe("constructor-item");
        result.RootElement.GetProperty("requiredItems").GetArrayLength().ShouldBe(0);
        result.RootElement.GetProperty("declaredInputs").GetArrayLength().ShouldBe(0);
        result.RootElement.GetProperty("declaredOutputs").EnumerateArray().Select(value => value.GetString()).ShouldBe(["probe.json"]);
    }

    [Theory]
    [InlineData("Items", "Items")]
    [InlineData("Unsupported", "Unsupported")]
    [InlineData("DeclaredOutputs", "persistent regular-file output")]
    public void CapturedInvalidVectorsAndEmptyOutputDeclarationsFailBeforeExecution(string parameter, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = CapturedProbeParameters();
        parameters[parameter] = parameter == "Items"
            ? CapturedParameter("@(MustNotReevaluate)", null)
            : CapturedParameter("", []);
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters, version: 2);

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldContain(expected);
        File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
    }

    [LinuxOnlyTheory]
    [InlineData("Fail")]
    [InlineData("LogError")]
    [InlineData("Throw")]
    [InlineData("NestedBuild")]
    [InlineData("ProduceOutput")]
    public void TaskFailuresReturnUnsuccessfulExit(string failure)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = ProbeParameters();
        parameters[failure] = failure != "ProduceOutput";
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters);
        string destination = Path.Combine(directory, "probe.json");
        File.WriteAllText(destination, "previous output");

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldNotBeNullOrWhiteSpace();
        File.ReadAllText(destination).ShouldBe("previous output");
        if (failure == "NestedBuild")
        {
            log.ShouldContain("BuildProjectFile");
        }
        else if (failure == "Throw")
        {
            log.ShouldContain("probe-exception");
        }
        else if (failure == "LogError")
        {
            log.ShouldContain("probe-error");
        }
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Required")]
    [InlineData("Duplicate")]
    [InlineData("Null")]
    [InlineData("Array")]
    [InlineData("HostService")]
    [InlineData("Declarations")]
    [InlineData("InPlace")]
    [InlineData("Unset")]
    [InlineData("EmptyUnsupportedArray")]
    [InlineData("NullMetadata")]
    [InlineData("MissingInput")]
    [InlineData("NoOutputs")]
    [InlineData("OverlappingOutputs")]
    public void InvalidParametersFailBeforeTaskExecution(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = ProbeParameters();
        switch (invalid)
        {
            case "Unknown":
                parameters["NoSuchParameter"] = "value";
                break;
            case "Required":
                parameters.Remove("File");
                break;
            case "Duplicate":
                parameters["file"] = "other.json";
                break;
            case "Null":
                parameters["Value"] = null;
                break;
            case "Array":
                parameters["Values"] = "not-an-array";
                break;
            case "HostService":
                parameters["BuildEngine"] = "not-a-service";
                break;
            case "Declarations":
                parameters.Remove("DeclaredInputs");
                break;
            case "InPlace":
                env.CreateFile(Path.Combine(directory, "input.txt"), "original");
                parameters["DeclaredInputs"] = new[] { "input.txt" };
                parameters["DeclaredOutputs"] = new[] { "input.txt" };
                break;
            case "Unset":
                parameters["Forbidden"] = "value";
                break;
            case "EmptyUnsupportedArray":
                parameters["Unsupported"] = Array.Empty<string>();
                break;
            case "NullMetadata":
                parameters["Items"] = new object[]
                {
                    new { itemSpec = "input", metadata = new Dictionary<string, string?> { ["Tag"] = null } },
                };
                break;
            case "MissingInput":
                parameters["DeclaredInputs"] = new[] { "absent.txt" };
                break;
            case "NoOutputs":
                parameters["DeclaredOutputs"] = Array.Empty<string>();
                break;
            case "OverlappingOutputs":
                parameters["DeclaredOutputs"] = new[] { "probe.json", Path.Combine("probe.json", "nested.txt") };
                break;
        }

        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters);

        Run(manifest, out _).ShouldBeFalse();

        File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
        if (invalid == "InPlace")
        {
            File.ReadAllText(Path.Combine(directory, "input.txt")).ShouldBe("original");
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"version\": 987}")]
    [InlineData("{\"version\": 1, \"version\": 1}")]
    public void RejectsInvalidInvocationFiles(string contents)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string manifest = env.CreateFile(".json", contents).Path;

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldContain(manifest);
    }

    [Theory]
    [InlineData("Version", "987")]
    [InlineData("UnknownField", "unknownField")]
    [InlineData("NullEnvironment", "ACTION_HOST_EXPLICIT")]
    public void RejectsInvalidInvocationContext(string invalid, string expectedDiagnostic)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), ProbeParameters());
        JsonNode invocation = JsonNode.Parse(File.ReadAllText(manifest))!;
        switch (invalid)
        {
            case "Version":
                invocation["version"] = 987;
                break;
            case "UnknownField":
                invocation["unknownField"] = "value";
                break;
            case "NullEnvironment":
                invocation["environment"] = new JsonObject { ["ACTION_HOST_EXPLICIT"] = null };
                break;
        }

        File.WriteAllText(manifest, invocation.ToJsonString());

        Run(manifest, out string log).ShouldBeFalse();

        File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
        log.ShouldContain(expectedDiagnostic);
    }

    private static Dictionary<string, object?> ProbeParameters() => new()
    {
        ["File"] = "probe.json",
        ["RequiredItems"] = Array.Empty<string>(),
        ["DeclaredInputs"] = Array.Empty<string>(),
        ["DeclaredOutputs"] = new[] { "probe.json" },
    };

    private static Dictionary<string, object?> CapturedProbeParameters() => new()
    {
        ["File"] = CapturedParameter("probe.json", [CapturedItem("probe.json")]),
        ["RequiredItems"] = CapturedParameter("", []),
        ["DeclaredInputs"] = CapturedParameter("", []),
        ["DeclaredOutputs"] = CapturedParameter("probe.json", [CapturedItem("probe.json")]),
    };

    private static object CapturedParameter(string scalar, object[]? items) => new { scalar, items };

    private static object CapturedItem(string itemSpec) => new { itemSpec, metadata = new Dictionary<string, string>() };

    private static string WriteInvocation(
        string directory,
        Type taskType,
        Dictionary<string, object?> parameters,
        Dictionary<string, string>? environment = null,
        int version = 1)
    {
        string path = Path.Combine(directory, "invocation.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version,
            taskAssembly = taskType.Assembly.Location,
            taskType = taskType.FullName,
            workingDirectory = directory,
            projectFile = Path.Combine(directory, "build.proj"),
            environment = environment ?? [],
            parameters,
        }));
        return path;
    }

    private bool Run(string manifest, out string log, string? stagingRoot = null, Dictionary<string, string>? controls = null)
    {
        Dictionary<string, string> environment = controls is null ? [] : new(controls);
        if (stagingRoot is not null)
        {
            environment["MSBUILD_ACTION_STAGING_ROOT"] = stagingRoot;
        }

        string host = Path.Combine(AppContext.BaseDirectory, "MSBuild.ActionTaskHost.dll");
        string tests = typeof(TaskHost_Tests).Assembly.Location;
        string arguments = $"exec --runtimeconfig \"{Path.ChangeExtension(tests, ".runtimeconfig.json")}\""
            + $" --depsfile \"{Path.ChangeExtension(tests, ".deps.json")}\" \"{host}\" --invocation \"{manifest}\"";
        log = RunnerUtilities.RunProcessAndGetOutput(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            arguments,
            out bool succeeded,
            outputHelper: _output,
            timeoutMilliseconds: 30_000,
            environmentVariables: environment);
        return succeeded;
    }
}

public enum ProbeMode
{
    First,
    Second,
}

[MSBuildDeclaredIOTask(SandboxVersion = 1)]
[MSBuildDeclaredIORequiresUnset(nameof(Forbidden))]
public sealed class ManifestProbeTask : Utilities.Task, IMultiThreadableTask
{
    private readonly string? _constructorTemporaryDirectory = Environment.GetEnvironmentVariable("TMPDIR");

    public ManifestProbeTask()
    {
    }

    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    public string InputFile { get; set; } = string.Empty;

    public bool CaptureFullPath { get; set; }

    [Required]
    public ITaskItem? File { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; } = [new Utilities.TaskItem("constructor-input")];

    public ITaskItem[] DeclaredOutputs { get; set; } = [new Utilities.TaskItem("constructor-output")];

    [Required]
    public ITaskItem[] RequiredItems { get; set; } = [new Utilities.TaskItem("constructor-required-item")];

    public string Value { get; set; } = "default";

    public string[] Values { get; set; } = ["default"];

    public bool Enabled { get; set; }

    public int Count { get; set; }

    public ProbeMode Mode { get; set; }

    public ITaskItem[] Items { get; set; } = [new Utilities.TaskItem("constructor-item")];

    public ITaskItem OptionalItem { get; set; } = new Utilities.TaskItem("constructor-item");

    public DateTimeOffset[] Unsupported { get; set; } = [];

    public string Forbidden { get; set; } = string.Empty;

    public bool Fail { get; set; }

    public bool LogError { get; set; }

    public bool Throw { get; set; }

    public bool NestedBuild { get; set; }

    public bool ProduceOutput { get; set; } = true;

    public override bool Execute()
    {
        ArgumentNullException.ThrowIfNull(File);
        if (Throw)
        {
            throw new InvalidOperationException("probe-exception");
        }

        if (NestedBuild)
        {
            return BuildEngine.BuildProjectFile("child.proj", [], new Hashtable(), new Hashtable());
        }

        if (ProduceOutput)
        {
            System.IO.File.WriteAllText(TaskEnvironment.GetOutputPath(File.ItemSpec), JsonSerializer.Serialize(new
            {
                value = Value,
                values = Values,
                enabled = Enabled,
                count = Count,
                mode = Mode.ToString(),
                items = Items.Select(item => new
                {
                    itemSpec = item.ItemSpec,
                    tag = item.GetMetadata("Tag"),
                    fullPath = CaptureFullPath ? item.GetMetadata("FullPath") : null,
                }).ToArray(),
                optionalItem = OptionalItem.ItemSpec,
                requiredItems = RequiredItems.Select(item => item.ItemSpec).ToArray(),
                declaredInputs = DeclaredInputs.Select(item => item.ItemSpec).ToArray(),
                declaredOutputs = DeclaredOutputs.Select(item => item.ItemSpec).ToArray(),
                projectDirectory = TaskEnvironment.ProjectDirectory.Value,
                workingDirectory = Environment.CurrentDirectory,
                constructorTemporaryDirectory = _constructorTemporaryDirectory,
                brokerLauncher = TaskEnvironment.ProcessLaunchBroker?.Launcher,
                brokerSocket = TaskEnvironment.ProcessLaunchBroker?.Socket,
                logicalBrokerVariable = TaskEnvironment.GetEnvironmentVariable("MSBUILD_ACTION_BROKER_SOCKET"),
                ambientBrokerVariable = Environment.GetEnvironmentVariable("MSBUILD_ACTION_BROKER_SOCKET"),
                outputPath = TaskEnvironment.GetOutputPath(File.ItemSpec).Value,
                logicalInput = InputFile.Length == 0 ? null : TaskEnvironment.GetAbsolutePath(InputFile).Value,
                inputPath = InputFile.Length == 0 ? null : TaskEnvironment.GetInputPath(InputFile).Value,
                inputContents = InputFile.Length == 0 ? null : System.IO.File.ReadAllText(TaskEnvironment.GetInputPath(InputFile)),
                explicitEnvironment = Environment.GetEnvironmentVariable("ACTION_HOST_EXPLICIT"),
                ambientEnvironment = Environment.GetEnvironmentVariable("ACTION_HOST_AMBIENT"),
                engineLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Microsoft.Build"),
                processId = Environment.ProcessId,
            }));
        }

        if (LogError)
        {
            Log.LogError("probe-error");
        }

        return !Fail;
    }
}

[MSBuildDeclaredIOTask]
public sealed class UnmigratedTask : Utilities.Task
{
    public UnmigratedTask() => throw new InvalidOperationException("unmigrated-constructor-executed");

    public override bool Execute() => throw new InvalidOperationException("unmigrated-task-executed");
}

[MSBuildDeclaredIOTask(SandboxVersion = 1)]
public sealed class HostedToolProbe : Utilities.SandboxedToolTask
{
    [Required]
    public string Script { get; set; } = string.Empty;

    [Required]
    public string Destination { get; set; } = string.Empty;

    protected override string ToolName => "sh";
    protected override string GenerateFullPathToTool() => MapInputPath("/bin/sh");
    protected override System.Text.Encoding ResponseFileEncoding => new System.Text.UTF8Encoding(false);
    protected override string GenerateResponseFileCommands() => "literal response contents\n";
    protected override string GetResponseFileSwitch(string responseFilePath) => "\"" + responseFilePath + "\"";

    protected override string GenerateCommandLineCommands()
    {
        Utilities.CommandLineBuilder command = new();
        command.AppendFileNameIfNotNull(MapInputPath(Script));
        command.AppendFileNameIfNotNull(MapOutputPath(Destination));
        return command.ToString();
    }
}
