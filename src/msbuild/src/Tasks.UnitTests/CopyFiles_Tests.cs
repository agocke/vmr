// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.Tasks.UnitTests;

public sealed class CopyFiles_Tests(ITestOutputHelper output)
{
    [Fact]
    public void ReplacesStaleBytesDespiteMatchingSizesAndTimestamps()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string source = Path.Combine(root, "source.bin");
        string destination = Path.Combine(root, "destination.bin");
        byte[] bytes = [0, 255, 1, 128];
        File.WriteAllBytes(source, bytes);
        File.WriteAllBytes(destination, [4, 3, 2, 1]);
        DateTime timestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, timestamp);
        File.SetLastWriteTimeUtc(destination, timestamp);
        CopyFiles task = CreateTask(root);

        task.Execute().ShouldBeTrue();

        File.ReadAllBytes(destination).ShouldBe(bytes);
        File.ReadAllBytes(source).ShouldBe(bytes);
        File.WriteAllBytes(source, [8, 7, 6, 5]);
        File.SetLastWriteTimeUtc(source, timestamp);
        task.Execute().ShouldBeTrue();
        File.ReadAllBytes(destination).ShouldBe(new byte[] { 8, 7, 6, 5 });
    }

    [Fact]
    public void CopiesOneInputToMultipleOutputsUsingCapturedContext()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        File.WriteAllText(Path.Combine(root, "source.bin"), "explicit project context");
        CopyFiles task = CreateTask(root);
        task.SourceFiles = [new TaskItem("source.bin"), new TaskItem("./source.bin")];
        task.DestinationFiles = [new TaskItem("destination.bin"), new TaskItem("second.bin")];
        task.DeclaredOutputs = task.DestinationFiles;

        task.Execute().ShouldBeTrue();

        File.ReadAllText(Path.Combine(root, "destination.bin")).ShouldBe("explicit project context");
        File.ReadAllText(Path.Combine(root, "second.bin")).ShouldBe("explicit project context");
    }

    [Theory]
    [InlineData("length")]
    [InlineData("input")]
    [InlineData("extra-input")]
    [InlineData("output")]
    [InlineData("extra-output")]
    [InlineData("duplicate-output")]
    [InlineData("overlap")]
    [InlineData("alias")]
    public void RejectsInvalidVectorsAndDeclarationsBeforeWriting(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string source = Path.Combine(root, "source.bin");
        string destination = Path.Combine(root, "destination.bin");
        File.WriteAllText(source, "source");
        File.WriteAllText(destination, "stale destination");
        CopyFiles task = CreateTask(root);
        switch (invalid)
        {
            case "length":
                task.DestinationFiles = [new TaskItem("destination.bin"), new TaskItem("extra.bin")];
                break;
            case "input":
                task.DeclaredInputs = [];
                break;
            case "extra-input":
                task.DeclaredInputs = [.. task.DeclaredInputs, new TaskItem("undeclared.bin")];
                break;
            case "output":
                task.DeclaredOutputs = [];
                break;
            case "extra-output":
                task.DeclaredOutputs = [.. task.DeclaredOutputs, new TaskItem("extra.bin")];
                break;
            case "duplicate-output":
                task.SourceFiles = [.. task.SourceFiles, .. task.SourceFiles];
                task.DestinationFiles = [.. task.DestinationFiles, .. task.DestinationFiles];
                break;
            case "overlap":
                task.SourceFiles = [new TaskItem("source.bin"), new TaskItem("destination.bin")];
                task.DeclaredInputs = task.SourceFiles;
                task.DestinationFiles = [new TaskItem("destination.bin"), new TaskItem("extra.bin")];
                task.DeclaredOutputs = task.DestinationFiles;
                break;
            case "alias":
                task.DestinationFiles = [new TaskItem(Path.Combine(root, ".", "source.bin"))];
                task.DeclaredOutputs = task.DestinationFiles;
                break;
        }

        task.Execute().ShouldBeFalse();

        ((MockEngine)task.BuildEngine).Log.ShouldContain(invalid == "length" ? "MSB3094" : "MSB3098");
        File.ReadAllText(source).ShouldBe("source");
        File.ReadAllText(destination).ShouldBe("stale destination");
        File.Exists(Path.Combine(root, "extra.bin")).ShouldBeFalse();
    }

    [Fact]
    public void MissingSourceFailsWithoutTruncatingDestination()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        string destination = Path.Combine(root, "destination.bin");
        File.WriteAllText(destination, "old destination");
        CopyFiles task = CreateTask(root);

        task.Execute().ShouldBeFalse();

        ((MockEngine)task.BuildEngine).Log.ShouldContain("MSB3021");
        File.ReadAllText(destination).ShouldBe("old destination");
    }

    [Fact]
    public void OutputDirectoryCollisionFailsWithoutChangingInput()
    {
        using TestEnvironment env = TestEnvironment.Create(output);
        string root = env.CreateFolder().Path;
        File.WriteAllText(Path.Combine(root, "source.bin"), "source");
        Directory.CreateDirectory(Path.Combine(root, "destination.bin"));
        CopyFiles task = CreateTask(root);

        task.Execute().ShouldBeFalse();

        ((MockEngine)task.BuildEngine).Log.ShouldContain("MSB3021");
        File.ReadAllText(Path.Combine(root, "source.bin")).ShouldBe("source");
        Directory.Exists(Path.Combine(root, "destination.bin")).ShouldBeTrue();
    }

    private CopyFiles CreateTask(string root) => new()
    {
        BuildEngine = new MockEngine(output),
        TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(root),
        SourceFiles = [new TaskItem("source.bin")],
        DestinationFiles = [new TaskItem("destination.bin")],
        DeclaredInputs = [new TaskItem("source.bin")],
        DeclaredOutputs = [new TaskItem("destination.bin")]
    };
}
