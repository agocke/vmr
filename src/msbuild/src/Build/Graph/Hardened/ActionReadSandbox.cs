// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Graph.Hardened;

internal sealed class ActionReadSandbox
{
    private readonly string[] _readFiles;
    private readonly string[] _executables;
    private readonly string[] _directories;
    private readonly string[] _processFiles;
    private readonly string _scratchParent;
    internal string Launcher { get; }
    internal string Configuration { get; }
    internal IEnumerable<string> ImageFiles
    {
        get
        {
            foreach (string file in _readFiles)
            {
                // These exact runtime grants have inode-lifetime, not content-change, timestamps.
                if (file is not ("/proc/meminfo" or "/proc/sys/vm/overcommit_memory"))
                {
                    yield return file;
                }
            }

            foreach (string file in _executables)
            {
                yield return file;
            }
        }
    }

    private ActionReadSandbox(ProjectInstance project, IReadOnlyDictionary<string, string> environment)
    {
        string[] names = ["MSBuildActionSandboxLauncher", "MSBuildActionSandboxReadFiles",
            "MSBuildActionSandboxExecutableFiles", "MSBuildActionSandboxReadDirectories",
            "MSBuildActionSandboxProcessFiles"];
        foreach (string name in names)
        {
            Check(project.GetPropertyValue(name).Length == 0 || project.GlobalProperties.ContainsKey(name),
                project, name + " must be a global construction input");
        }

        Check(project.GetPropertyValue(HardenedActionGraph.HostOSProperty) == "linux",
            project, "action read enforcement requires Linux");
        Launcher = Absolute(project.GetPropertyValue(names[0]), project);
        Check(File.Exists(Launcher) && !Directory.Exists(Launcher), project, "missing sandbox launcher");
        _readFiles = Paths(names[1], directory: false);
        _executables = Paths(names[2], directory: false);
        _directories = Paths(names[3], directory: true);
        SortedSet<string> processFiles = new(StringComparer.Ordinal);
        foreach (string path in ExpressionShredder.SplitSemiColonSeparatedList(project.GetPropertyValue(names[4])))
        {
            Check(path is "/proc/self/maps" or "/proc/self/mountinfo", project,
                "unsupported process-local sandbox runtime file: " + path);
            processFiles.Add(path);
        }

        _processFiles = [.. processFiles];
        Check(_readFiles.Length != 0, project, "an explicit sandbox execution-image read-file list is required");
        string? temporary = null;
        foreach (string variable in new[] { "TMPDIR", "TMP", "TEMP" })
        {
            if (environment.TryGetValue(variable, out temporary))
            {
                break;
            }
        }

        Check(!string.IsNullOrEmpty(temporary), project, "sandbox execution requires an explicitly captured TMPDIR/TMP/TEMP");
        _scratchParent = Absolute(temporary!, project);
        Configuration = string.Join("\0", [Launcher, _scratchParent,
            "read", .. _readFiles, "execute", .. _executables, "directory", .. _directories,
            "process", .. _processFiles]);

        string[] Paths(string name, bool directory)
        {
            SortedSet<string> paths = new(StringComparer.Ordinal);
            foreach (string path in ExpressionShredder.SplitSemiColonSeparatedList(project.GetPropertyValue(name)))
            {
                string absolute = Absolute(path, project);
                Check(directory ? Directory.Exists(absolute) : File.Exists(absolute) && !Directory.Exists(absolute),
                    project, "missing or incorrectly typed sandbox image path: " + absolute);
                Check(!directory || absolute != "/", project, "sandbox image directory enumeration cannot grant the filesystem root");
                paths.Add(absolute);
            }

            return [.. paths];
        }
    }

    internal static ActionReadSandbox? Create(ProjectInstance project, IReadOnlyDictionary<string, string> environment)
    {
        if (project.GetPropertyValue("MSBuildActionSandboxLauncher").Length != 0)
        {
            return new(project, environment);
        }

        Check(project.GetPropertyValue("MSBuildActionSandboxReadFiles").Length == 0 &&
            project.GetPropertyValue("MSBuildActionSandboxExecutableFiles").Length == 0 &&
            project.GetPropertyValue("MSBuildActionSandboxReadDirectories").Length == 0 &&
            project.GetPropertyValue("MSBuildActionSandboxProcessFiles").Length == 0,
            project, "sandbox image settings require an explicit MSBuildActionSandboxLauncher");
        return null;
    }

    internal byte[] Policy(IEnumerable<string> inputs, IEnumerable<string> tools, string manifest, string executable)
    {
        SortedDictionary<string, bool> files = new(StringComparer.Ordinal);
        foreach (string path in inputs)
        {
            files[path] = false;
        }

        foreach (string path in tools)
        {
            files[path] = false;
        }

        foreach (string path in _readFiles)
        {
            files[path] = false;
        }

        files[manifest] = false;
        foreach (string path in _executables)
        {
            files[path] = true;
        }

        files[executable] = true;
        files[Launcher] = true;
        using MemoryStream stream = new();
        Write("genbuild-landlock-v1");
        Write("scratch-parent");
        Write(_scratchParent);
        foreach (KeyValuePair<string, bool> file in files)
        {
            Write(file.Value ? "execute-file" : "read-file");
            Write(file.Key);
        }

        foreach (string directory in _directories)
        {
            Write("read-directory");
            Write(directory);
        }

        foreach (string processFile in _processFiles)
        {
            Write("read-self-file");
            Write(processFile);
        }

        return stream.ToArray();

        void Write(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }
    }

    private static string Absolute(string path, ProjectInstance project)
    {
        Check(Path.IsPathRooted(path) && path.IndexOf('\0') < 0, project, "sandbox paths must be absolute");
        return Path.GetFullPath(path);
    }

    private static void Check(bool condition, ProjectInstance project, string message)
    {
        if (!condition)
        {
            ProjectErrorUtilities.ThrowInvalidProject(project.ProjectFileLocation,
                "HardenedGraphUnsupportedConstruct", message, "action read sandbox");
        }
    }
}
