// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    [Fact]
    public void ExecutesWriteLinesToFileFromInvocationFile()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder(subfolder: "action host $ files").Path;
        string destination = Path.Combine(directory, "result %3B.txt");
        string manifest = WriteInvocation(directory, typeof(WriteLinesToFile), new()
        {
            ["File"] = destination,
            ["Lines"] = new[] { "first;line", "literal%3Bvalue", "last\\line" },
            ["Overwrite"] = "yes",
            ["DeclaredInputs"] = Array.Empty<string>(),
            ["DeclaredOutputs"] = new[] { destination },
        });

        bool succeeded = Run(manifest, out _);

        succeeded.ShouldBeTrue();
        File.ReadAllLines(destination).ShouldBe(["first;line", "literal%3Bvalue", "last\\line"]);
    }

    [Theory]
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

    [Fact]
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

    [Fact]
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
        root.GetProperty("workingDirectory").GetString().ShouldBe(directory);
        root.GetProperty("explicitEnvironment").GetString().ShouldBe("present");
        root.GetProperty("ambientEnvironment").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("engineLoaded").GetBoolean().ShouldBeFalse();
        root.GetProperty("processId").GetInt32().ShouldNotBe(Environment.ProcessId);
    }

    [Fact]
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

    [Theory]
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

    [Theory]
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

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldNotBeNullOrWhiteSpace();
        File.Exists(Path.Combine(directory, "probe.json")).ShouldBe(failure is "Fail" or "LogError");
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

    private bool Run(string manifest, out string log)
    {
        string host = Path.Combine(AppContext.BaseDirectory, "MSBuild.ActionTaskHost.dll");
        string tests = typeof(TaskHost_Tests).Assembly.Location;
        string arguments = $"exec --runtimeconfig \"{Path.ChangeExtension(tests, ".runtimeconfig.json")}\""
            + $" --depsfile \"{Path.ChangeExtension(tests, ".deps.json")}\" \"{host}\" --invocation \"{manifest}\"";
        log = RunnerUtilities.RunProcessAndGetOutput(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            arguments,
            out bool succeeded,
            outputHelper: _output,
            timeoutMilliseconds: 30_000);
        return succeeded;
    }
}

public enum ProbeMode
{
    First,
    Second,
}

[MSBuildDeclaredIOTask]
[MSBuildDeclaredIORequiresUnset(nameof(Forbidden))]
public sealed class ManifestProbeTask : Utilities.Task
{
    public ManifestProbeTask()
    {
    }

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
            System.IO.File.WriteAllText(File.ItemSpec, JsonSerializer.Serialize(new
            {
                value = Value,
                values = Values,
                enabled = Enabled,
                count = Count,
                mode = Mode.ToString(),
                items = Items.Select(item => new { itemSpec = item.ItemSpec, tag = item.GetMetadata("Tag") }).ToArray(),
                optionalItem = OptionalItem.ItemSpec,
                requiredItems = RequiredItems.Select(item => item.ItemSpec).ToArray(),
                declaredInputs = DeclaredInputs.Select(item => item.ItemSpec).ToArray(),
                declaredOutputs = DeclaredOutputs.Select(item => item.ItemSpec).ToArray(),
                workingDirectory = Environment.CurrentDirectory,
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
