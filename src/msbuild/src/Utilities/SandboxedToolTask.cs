// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Resources;
using Microsoft.Build.Framework;

#nullable enable

namespace Microsoft.Build.Utilities;

/// <summary>
/// Provides opt-in, declared-file staging for a tool. This is not OS confinement:
/// input symlinks are writable and a tool can still access absolute paths outside staging.
/// </summary>
public abstract class SandboxedToolTask : ToolTask
{
    private FileStagingContext? _context;

    /// <summary>Initializes a staged tool task.</summary>
    protected SandboxedToolTask()
    {
    }

    /// <summary>Initializes a staged tool task with task resources.</summary>
    protected SandboxedToolTask(ResourceManager taskResources) : base(taskResources)
    {
    }

    /// <summary>Gets or sets whether to stage execution. Defaults to false; staging is not a security boundary.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Gets or sets the exact regular-file inputs to expose through file symlinks.</summary>
    public ITaskItem[] DeclaredInputs { get; set; } = [];

    /// <summary>Gets or sets the regular-file outputs to publish only after successful execution and validation.</summary>
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    /// <summary>Gets whether a private staging context is active.</summary>
    protected bool IsSandboxed => _context is not null || TaskEnvironment.IsSandboxed;

    /// <summary>Gets the private scratch directory while staging is active.</summary>
    protected string SandboxTemporaryDirectory => _context?.Scratch
        ?? (TaskEnvironment.IsSandboxed ? TaskEnvironment.GetEnvironmentVariable("TMPDIR")! : Context.Scratch);

    /// <summary>Maps an exact declared input, or returns the original path when staging is disabled.</summary>
    protected string MapInputPath(string path) => _context is null
        ? TaskEnvironment.IsSandboxed ? TaskEnvironment.GetInputPath(path).Value : path
        : _context.MapFile(path, input: true);

    /// <summary>Maps an exact declared output, or returns the original path when staging is disabled.</summary>
    protected string MapOutputPath(string path) => _context is null
        ? TaskEnvironment.IsSandboxed ? TaskEnvironment.GetOutputPath(path).Value : path
        : _context.MapFile(path, input: false);

    /// <summary>Maps a structural directory without discovering files, or returns the original path when disabled.</summary>
    protected string MapDirectory(string path) => _context is null
        ? TaskEnvironment.IsSandboxed ? TaskEnvironment.GetExecutionDirectory(path).Value : path
        : _context.MapDirectory(path);

    /// <summary>Gets the declared files produced by the tool, excluding outputs finalized by the task host.</summary>
    protected virtual ITaskItem[] GetToolOutputs() => DeclaredOutputs;

    private FileStagingContext Context => _context ?? throw new InvalidOperationException("No staging context is active.");

    /// <inheritdoc />
    public override bool Execute()
    {
        if (TaskEnvironment.IsSandboxed && UseCommandProcessor)
        {
            Log.LogError("Staged execution does not support UseCommandProcessor.");
            return false;
        }

        if (!UseSandbox || TaskEnvironment.IsSandboxed)
        {
            return base.Execute();
        }

        bool success = false;
        try
        {
            if (UseCommandProcessor)
            {
                throw new InvalidOperationException("Staged execution does not support UseCommandProcessor.");
            }

            _context = new FileStagingContext(TaskEnvironment);
            _context.Prepare(DeclaredInputs, GetToolOutputs());
            success = base.Execute() && !HasLoggedErrors && !ToolCanceled.WaitOne(0);
            if (success)
            {
                _context.Publish();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            success = false;
        }
        finally
        {
            if (_context is not null)
            {
                try
                {
                    _context.Cleanup();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.LogErrorFromException(ex, showStackTrace: false);
                    success = false;
                }

                _context = null;
            }
        }

        return success;
    }

    /// <inheritdoc />
    protected override int ExecuteTool(string pathToTool, string responseFileCommands, string commandLineCommands)
    {
        if (!IsSandboxed)
        {
            return base.ExecuteTool(pathToTool, responseFileCommands, commandLineCommands);
        }

        if (!string.IsNullOrEmpty(responseFileCommands))
        {
            string responseFile = Path.Combine(SandboxTemporaryDirectory, Guid.NewGuid().ToString("N") + ".rsp");
            using (var stream = new FileStream(responseFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, ResponseFileEncoding))
            {
                writer.Write(ResponseFileEscape(responseFileCommands));
            }

            commandLineCommands += " " + GetResponseFileSwitch(responseFile);
        }

        return base.ExecuteTool(pathToTool, string.Empty, commandLineCommands);
    }

    /// <inheritdoc />
    protected override ProcessStartInfo GetProcessStartInfo(string pathToTool, string commandLineCommands, string responseFileSwitch)
    {
        ProcessStartInfo info = base.GetProcessStartInfo(pathToTool, commandLineCommands, responseFileSwitch);
        if (IsSandboxed)
        {
            info.WorkingDirectory = _context?.WorkingDirectory
                ?? TaskEnvironment.GetExecutionDirectory(TaskEnvironment.ProjectDirectory.Value).Value;
            info.Environment.Clear();
            foreach (KeyValuePair<string, string> pair in _context is null ? TaskEnvironment.GetEnvironmentVariables() : Context.Environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }

            if (EnvironmentVariables is not null)
            {
                foreach (string entry in EnvironmentVariables)
                {
                    int separator = entry.IndexOf('=');
                    if (separator <= 0)
                    {
                        throw new ArgumentException("EnvironmentVariables entries must have the form NAME=VALUE.");
                    }

                    info.Environment[entry.Substring(0, separator)] = entry.Substring(separator + 1);
                }
            }

            info.Environment["TMPDIR"] = SandboxTemporaryDirectory;
            info.Environment["TMP"] = SandboxTemporaryDirectory;
            info.Environment["TEMP"] = SandboxTemporaryDirectory;
        }

        return info;
    }
}
