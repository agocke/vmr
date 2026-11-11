// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public sealed class PrepareRestoreRequests_Tests
{
    private const string PropertiesMetadata = "_MSBuildRestoreRequestProperties";
    private const string IdentityMetadata = "_MSBuildRestoreRequestId";
    private readonly ITestOutputHelper _output;

    public PrepareRestoreRequests_Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InheritedEscapedLiteralsSurvivePreparationAndForwarding()
    {
        PrepareRestoreRequests task = CreateTask(new Dictionary<string, string>
        {
            ["Literal"] = "left%3bright=100%25%253B",
            ["Empty"] = "",
        });
        task.ProjectReferences = [Reference("library.csproj")];

        task.Execute().ShouldBeTrue();
        task.PreparedRequests.Length.ShouldBe(1);
        ReadProperties(task.PreparedRequests[0]).ShouldBe(new Dictionary<string, string>
        {
            ["LITERAL"] = "left%3bright=100%25%253B",
            ["EMPTY"] = "",
        }, ignoreOrder: true);

        PrepareRestoreRequests forwarded = CreateTask(new Dictionary<string, string> { ["CallerOnly"] = "ignored" });
        forwarded.Requests = task.PreparedRequests;
        forwarded.Execute().ShouldBeTrue();
        forwarded.PreparedRequests.Length.ShouldBe(1);
        forwarded.PreparedRequests[0].GetMetadata(PropertiesMetadata).ShouldBe(task.PreparedRequests[0].GetMetadata(PropertiesMetadata));
        forwarded.PreparedRequests[0].GetMetadata(IdentityMetadata).ShouldBe(task.PreparedRequests[0].GetMetadata(IdentityMetadata));
        ReadProperties(forwarded.PreparedRequests[0]).ContainsKey("CALLERONLY").ShouldBeFalse();
    }

    [Fact]
    public void RemovalsPrecedeOverridesAndAdditionalPropertiesWin()
    {
        PrepareRestoreRequests task = CreateTask(new Dictionary<string, string>
        {
            ["CommonRemoved"] = "old",
            ["ReferenceRemoved"] = "old",
            ["Undefined"] = "old",
            ["Reintroduced"] = "old",
            ["Keep"] = "inherited",
            ["Winner"] = "inherited",
        });
        task.RemoveProperties = " commonremoved ; Reintroduced";
        task.Properties = ["Reintroduced=explicit", "Winner=common"];
        task.ProjectReferences =
        [
            Reference("library.csproj",
                ("GlobalPropertiesToRemove", " referenceremoved "),
                ("UndefineProperties", " undefined "),
                ("AdditionalProperties", "Winner=additional"))
        ];

        task.Execute().ShouldBeTrue();
        task.PreparedRequests.Length.ShouldBe(1);
        ReadProperties(task.PreparedRequests[0]).ShouldBe(new Dictionary<string, string>
        {
            ["KEEP"] = "inherited",
            ["REINTRODUCED"] = "explicit",
            ["WINNER"] = "additional",
        }, ignoreOrder: true);
    }

    [Fact]
    public void ExplicitConfigurationPlatformAndFrameworkAreAssignedWithoutNegotiation()
    {
        PrepareRestoreRequests task = CreateTask(new Dictionary<string, string>
        {
            ["Configuration"] = "Caller",
            ["Platform"] = "AnyCPU",
            ["TargetFramework"] = "net8.0",
        });
        task.Properties = ["Configuration=Common", "Platform=Common", "TargetFramework=net9.0"];
        task.ProjectReferences =
        [
            Reference("library.csproj",
                ("SetConfiguration", "Configuration=Release"),
                ("SetPlatform", "Platform=arm64"),
                ("SetTargetFramework", "TargetFramework=net10.0"))
        ];

        task.Execute().ShouldBeTrue();
        task.PreparedRequests.Length.ShouldBe(1);
        ReadProperties(task.PreparedRequests[0]).ShouldBe(new Dictionary<string, string>
        {
            ["CONFIGURATION"] = "Release",
            ["PLATFORM"] = "arm64",
            ["TARGETFRAMEWORK"] = "net10.0",
        }, ignoreOrder: true);
    }

    [Fact]
    public void AuthoredPropertiesReplaceCommonOverridesAndSetMetadata()
    {
        PrepareRestoreRequests task = CreateTask(new Dictionary<string, string> { ["Keep"] = "inherited" });
        task.Properties = ["CommonOnly=ignored"];
        task.ProjectReferences =
        [
            Reference("library.csproj",
                ("Properties", "Configuration=Authored;Platform=x64;TargetFramework=net9.0"),
                ("SetConfiguration", "Configuration=ignored"),
                ("SetPlatform", "Platform=ignored"),
                ("SetTargetFramework", "TargetFramework=ignored"),
                ("AdditionalProperties", "Configuration=Final"))
        ];

        task.Execute().ShouldBeTrue();
        task.PreparedRequests.Length.ShouldBe(1);
        ReadProperties(task.PreparedRequests[0]).ShouldBe(new Dictionary<string, string>
        {
            ["KEEP"] = "inherited",
            ["CONFIGURATION"] = "Final",
            ["PLATFORM"] = "x64",
            ["TARGETFRAMEWORK"] = "net9.0",
        }, ignoreOrder: true);
    }

    [Fact]
    public void NormalizedPathAndCanonicalPropertyNamesDeduplicateIdenticalContexts()
    {
        PrepareRestoreRequests task = CreateTask();
        task.ProjectReferences =
        [
            Reference("library.csproj", ("Properties", "Configuration=Release;Flavor=vanilla")),
            Reference(Path.Combine("nested", "..", "library.csproj"), ("Properties", "flavor=vanilla;CONFIGURATION=Release")),
        ];

        task.Execute().ShouldBeTrue();
        task.PreparedRequests.Length.ShouldBe(1);
        task.PreparedRequests[0].ItemSpec.ShouldBe(Path.GetFullPath("library.csproj"));
        task.PreparedRequests[0].GetMetadata(IdentityMetadata).ShouldNotBeNullOrEmpty();
        ReadProperties(task.PreparedRequests[0])["CONFIGURATION"].ShouldBe("Release");
        ReadProperties(task.PreparedRequests[0])["FLAVOR"].ShouldBe("vanilla");
    }

    [Theory]
    [InlineData("Flavor=Vanilla", "Flavor=vanilla")]
    [InlineData("Keep=value", "Keep=value;Flavor=")]
    public void PropertyValueCaseAndMissingVersusEmptyProduceDistinctRequests(string first, string second)
    {
        PrepareRestoreRequests task = CreateTask();
        task.ProjectReferences =
        [
            Reference("library.csproj", ("Properties", first)),
            Reference("library.csproj", ("Properties", second)),
        ];

        task.Execute().ShouldBeTrue();
        task.PreparedRequests.Length.ShouldBe(2);
        task.PreparedRequests[0].ItemSpec.ShouldBe(task.PreparedRequests[1].ItemSpec);
        task.PreparedRequests[0].GetMetadata(IdentityMetadata).ShouldNotBe(task.PreparedRequests[1].GetMetadata(IdentityMetadata));
        task.PreparedRequests[0].GetMetadata(PropertiesMetadata).ShouldNotBe(task.PreparedRequests[1].GetMetadata(PropertiesMetadata));
    }

    [Fact]
    public void CurrentProjectRetainsActualGlobalsRatherThanReferenceOverrides()
    {
        PrepareRestoreRequests task = CreateTask(new Dictionary<string, string> { ["Configuration"] = "Debug", ["CallerOnly"] = "keep" });
        task.CurrentProject = "library.csproj";
        task.RemoveProperties = "CallerOnly";
        task.Properties = ["Configuration=Release"];
        task.ProjectReferences = [Reference("library.csproj")];

        task.Execute().ShouldBeTrue();
        task.PreparedRequests.Length.ShouldBe(2);
        ReadProperties(task.PreparedRequests[0]).ShouldBe(new Dictionary<string, string> { ["CONFIGURATION"] = "Release" });
        ReadProperties(task.PreparedRequests[1]).ShouldBe(new Dictionary<string, string>
        {
            ["CONFIGURATION"] = "Debug",
            ["CALLERONLY"] = "keep",
        }, ignoreOrder: true);
        task.PreparedRequests[0].GetMetadata(IdentityMetadata).ShouldNotBe(task.PreparedRequests[1].GetMetadata(IdentityMetadata));
    }

    private PrepareRestoreRequests CreateTask(Dictionary<string, string>? globals = null) => new()
    {
        BuildEngine = new MockEngine(_output) { GlobalProperties = globals ?? [] },
    };

    private static TaskItem Reference(string path, params (string Name, string Value)[] metadata)
    {
        TaskItem item = new(path);
        foreach ((string name, string value) in metadata)
        {
            item.SetMetadata(name, value);
        }

        return item;
    }

    private static Dictionary<string, string> ReadProperties(ITaskItem request)
    {
        // Inspect the public task metadata, avoiding the shared helper's two assembly definitions.
        using MemoryStream stream = new(Convert.FromBase64String(request.GetMetadata(PropertiesMetadata)));
        using BinaryReader reader = new(stream);
        reader.ReadInt32().ShouldBe(1);
        int count = reader.ReadInt32();
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            properties.Add(reader.ReadString(), reader.ReadString());
        }

        stream.Position.ShouldBe(stream.Length);
        return properties;
    }
}
