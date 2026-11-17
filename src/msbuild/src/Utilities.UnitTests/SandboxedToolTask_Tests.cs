// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.UnitTests;

#if NET8_0_OR_GREATER
public sealed class SandboxedToolTask_Tests
{
    [LinuxOnlyFact]
    public void StagesRelativeLayoutResponseFileAndEnvironmentAndPublishesOnlyDeclaredFiles()
    {
        using var fixture = new Fixture();
        Probe task = fixture.Create("success");
        task.Execute().ShouldBeTrue(fixture.Engine.Log);

        string[] lines = File.ReadAllLines(fixture.Output);
        lines[0].ShouldBe("input contents");
        lines[1].ShouldBe(task.StagedDirectory);
        lines[2].ShouldBe(task.Scratch);
        lines[3].ShouldBe("explicit");
        lines[4].ShouldBe("absent");
        lines[5].ShouldBe("\uFEFFescaped response contents");
        task.StagedInput.ShouldBe(Path.Combine(task.StagedDirectory, "input with spaces.txt"));
        task.StagedOutput.ShouldBe(Path.Combine(task.StagedDirectory, "out", "result.txt"));
        task.StartInfo!.Environment["TMP"].ShouldBe(task.Scratch);
        task.StartInfo.Environment["TEMP"].ShouldBe(task.Scratch);
        task.StartInfo.Environment.ContainsKey("DOTNET_STARTUP_HOOKS").ShouldBeFalse();
        task.UndeclaredInputVisible.ShouldBeFalse();
        task.RootMode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Exists(task.OwnedRoot).ShouldBeFalse();
        Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Length.ShouldBe(4);
        File.ReadAllText(fixture.Input).ShouldBe("input contents\n");
    }

    [LinuxOnlyTheory]
    [InlineData("fail")]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("symlink")]
    [InlineData("ancestor")]
    [InlineData("logged")]
    [InlineData("cancel")]
    public void FailurePreservesAllPreviousOutputsAndCleansStaging(string mode)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Output)!);
        File.WriteAllText(fixture.Output, "previous output");
        string second = Path.Combine(fixture.Root, "second.txt");
        File.WriteAllText(second, "previous second");
        Probe task = fixture.Create(mode);
        task.DeclaredOutputs = [new TaskItem(fixture.Output), new TaskItem(second)];
        task.Execute().ShouldBeFalse();
        File.ReadAllText(fixture.Output).ShouldBe("previous output");
        File.ReadAllText(second).ShouldBe("previous second");
        Directory.Exists(task.OwnedRoot).ShouldBeFalse();
        File.ReadAllText(fixture.Input).ShouldBe("input contents\n");
    }

    [LinuxOnlyTheory]
    [InlineData("overlap")]
    [InlineData("duplicate")]
    [InlineData("ancestor")]
    [InlineData("directory")]
    [InlineData("missing")]
    [InlineData("undeclared")]
    [InlineData("destination-link")]
    public void RejectsInvalidDeclarationsBeforeStartingTool(string mode)
    {
        using var fixture = new Fixture();
        Probe task = fixture.Create("success");
        switch (mode)
        {
            case "overlap":
                task.DeclaredOutputs = [new TaskItem(fixture.Input)];
                break;
            case "duplicate":
                task.DeclaredOutputs = [new TaskItem(fixture.Output), new TaskItem(fixture.Output)];
                break;
            case "ancestor":
                task.DeclaredOutputs = [new TaskItem(fixture.Input + "/child")];
                break;
            case "directory":
                task.DeclaredInputs = [new TaskItem(fixture.Root)];
                break;
            case "missing":
                task.DeclaredInputs = [new TaskItem(Path.Combine(fixture.Root, "absent.txt"))];
                break;
            case "undeclared":
                task.DeclaredInputs = [new TaskItem(fixture.Script)];
                break;
            case "destination-link":
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Output)!);
                File.CreateSymbolicLink(fixture.Output, fixture.Input);
                break;
        }

        task.Execute().ShouldBeFalse();
        task.StartInfo.ShouldBeNull();
        Directory.GetDirectories(fixture.Root, "msbuild-stage-*").ShouldBeEmpty();
        File.ReadAllText(fixture.Input).ShouldBe("input contents\n");
    }

    [LinuxOnlyFact]
    public void LeavesHostFinalizedOutputsOutsideToolPublication()
    {
        using var fixture = new Fixture();
        Probe task = fixture.Create("host-output");
        string hostOutput = Path.Combine(fixture.Root, "host.txt");
        task.DeclaredOutputs = [new TaskItem(fixture.Output), new TaskItem(hostOutput)];
        task.Execute().ShouldBeTrue(fixture.Engine.Log);
        File.Exists(fixture.Output).ShouldBeTrue();
        File.Exists(hostOutput).ShouldBeFalse();
        task.DeclaredOutputs.Length.ShouldBe(2);
        Directory.Exists(task.OwnedRoot).ShouldBeFalse();
    }

    [LinuxOnlyFact]
    public void DisabledModeKeepsOriginalPathsWorkingDirectoryAndAmbientEnvironment()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Output)!);
        Probe task = fixture.Create("ordinary");
        task.UseSandbox = false;
        task.DeclaredInputs = [];
        task.DeclaredOutputs = [];
        task.Execute().ShouldBeTrue(fixture.Engine.Log);
        task.StagedInput.ShouldBe("input with spaces.txt");
        task.StagedOutput.ShouldBe(Path.Combine("out", "result.txt"));
        task.StagedDirectory.ShouldBe(".");
        task.StartInfo!.WorkingDirectory.ShouldBe(fixture.Root);
        task.StartInfo.Environment["AMBIENT_MARKER"].ShouldBe("inherited");
        File.ReadAllText(fixture.Output).ShouldStartWith("input contents\n");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "staged task tests " + Guid.NewGuid().ToString("N"));
        internal string Input => Path.Combine(Root, "input with spaces.txt");
        internal string Output => Path.Combine(Root, "out", "result.txt");
        internal string Script => Path.Combine(Root, "tool.sh");
        internal MockEngine Engine { get; } = new();

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Input, "input contents\n");
            File.WriteAllText(Path.Combine(Root, "undeclared.txt"), "not an input");
            File.WriteAllText(Script, """
                input="$1"
                output="$2"
                mode="$4"
                case "$mode" in
                  missing) exit 0 ;;
                  symlink) /bin/ln -s "$input" "$output"; exit 0 ;;
                  ancestor) /bin/rmdir "${output%/*}"; /bin/ln -s "${input%/*}" "${output%/*}"; exit 0 ;;
                  cancel) /bin/sleep 10; exit 0 ;;
                esac
                /bin/cat "$input" > "$output"
                /bin/pwd >> "$output"
                printf '%s\n' "$TMPDIR" "$CUSTOM_VALUE" "${AMBIENT_MARKER-absent}" >> "$output"
                if [ "$mode" != ordinary ]; then
                  /bin/cat "$5" >> "$output"
                  printf scratch > "$TMPDIR/unpublished.txt"
                fi
                if [ "$mode" = fail ]; then exit 7; fi
                if [ "$mode" = logged ]; then echo TASK_ERROR; fi
                exit 0
                """);
        }

        internal Probe Create(string mode) => new()
        {
            BuildEngine = Engine,
            UseSandbox = true,
            TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(Root, new Dictionary<string, string>
            {
                ["TMPDIR"] = Root,
                ["AMBIENT_MARKER"] = "inherited",
                ["DOTNET_STARTUP_HOOKS"] = "/ambient/injection.dll"
            }),
            EnvironmentVariables = ["CUSTOM_VALUE=explicit"],
            DeclaredInputs = [new TaskItem(Script), new TaskItem(Input)],
            DeclaredOutputs = [new TaskItem(Output)],
            Mode = mode
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Probe : SandboxedToolTask
    {
        internal string Mode { get; set; } = "";
        internal string StagedDirectory { get; private set; } = "";
        internal string StagedInput { get; private set; } = "";
        internal string StagedOutput { get; private set; } = "";
        internal string Scratch { get; private set; } = "";
        internal string OwnedRoot { get; private set; } = "";
        internal UnixFileMode RootMode { get; private set; }
        internal bool UndeclaredInputVisible { get; private set; }
        internal ProcessStartInfo? StartInfo { get; private set; }
        protected override string ToolName => "sh";
        protected override string GenerateFullPathToTool() => "/bin/sh";
        protected override string GetWorkingDirectory() => TaskEnvironment.ProjectDirectory;

        protected override ITaskItem[] GetToolOutputs()
            => Mode == "host-output" ? [DeclaredOutputs[0]] : base.GetToolOutputs();

        protected override string GenerateCommandLineCommands()
        {
            StagedInput = MapInputPath("input with spaces.txt");
            StagedOutput = MapOutputPath(Path.Combine("out", "result.txt"));
            StagedDirectory = MapDirectory(".");
            if (IsSandboxed)
            {
                Scratch = SandboxTemporaryDirectory;
                OwnedRoot = Path.GetDirectoryName(Scratch)!;
                if (OperatingSystem.IsLinux())
                {
                    RootMode = File.GetUnixFileMode(OwnedRoot);
                }
            }

            return $"\"{MapInputPath("tool.sh")}\" \"{StagedInput}\" \"{StagedOutput}\" \"{StagedDirectory}\" {Mode}";
        }

        protected override string GenerateResponseFileCommands() => IsSandboxed ? "response contents\n" : "";
        protected override string ResponseFileEscape(string responseString) => "escaped " + responseString;
        protected override string GetResponseFileSwitch(string responseFilePath) => "\"" + responseFilePath + "\"";

        protected override ProcessStartInfo GetProcessStartInfo(string pathToTool, string commandLineCommands, string responseFileSwitch)
        {
            StartInfo = base.GetProcessStartInfo(pathToTool, commandLineCommands, responseFileSwitch);
            return StartInfo;
        }

        protected override Process StartToolProcess(Process process)
        {
            UndeclaredInputVisible = File.Exists(Path.Combine(StagedDirectory, "undeclared.txt"));
            Process started = base.StartToolProcess(process);
            if (Mode == "cancel")
            {
                Cancel();
            }

            return started;
        }

        protected override void LogEventsFromTextOutput(string singleLine, MessageImportance messageImportance)
        {
            if (singleLine == "TASK_ERROR")
            {
                Log.LogError("Tool reported an error.");
            }
            else
            {
                base.LogEventsFromTextOutput(singleLine, messageImportance);
            }
        }
    }
}
#endif
