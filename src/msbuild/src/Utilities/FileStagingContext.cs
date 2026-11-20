// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Build.Framework;

#nullable enable

namespace Microsoft.Build.Utilities;

internal sealed class FileStagingContext
{
    private readonly string _projectDirectory = string.Empty;
    private readonly string _root = string.Empty;
    private readonly string _executionRoot = string.Empty;
    private readonly HashSet<string> _inputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outputs = new(StringComparer.Ordinal);

    internal string Scratch { get; }
    internal string WorkingDirectory { get; }
    internal Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    internal FileStagingContext(TaskEnvironment environment, string? existingRoot = null)
    {
#if NET8_0_OR_GREATER
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Staged tool execution currently requires Linux and modern .NET.");
        }

        _projectDirectory = Path.GetFullPath(environment.ProjectDirectory);
        string parent = environment.GetEnvironmentVariable("TMPDIR")
            ?? environment.GetEnvironmentVariable("TMP")
            ?? environment.GetEnvironmentVariable("TEMP")
            ?? Path.GetTempPath();
        parent = Path.GetFullPath(Path.Combine(_projectDirectory, parent));
        if (existingRoot is null)
        {
            _root = Path.Combine(parent, "msbuild-stage-" + Guid.NewGuid().ToString("N"));
            // mkdir is exclusive and establishes 0700 before any staged content is created.
            if (Mkdir(_root, 448) != 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot create private staging root: " + _root);
            }
        }
        else
        {
            if (!Path.IsPathFullyQualified(existingRoot) || Path.GetFullPath(existingRoot) != existingRoot)
            {
                throw new InvalidOperationException("Launcher staging root must be a canonical absolute path.");
            }

            _root = existingRoot;
            RejectLinks(_root, "/");
            if (Statx(-100, _root, 0, 0x10F, out FileIdentity identity) != 0 ||
                (identity.Mode & 0xFFFF) != 0x41C0 || identity.UserId != GetEffectiveUserId())
            {
                throw new InvalidOperationException("Launcher staging root must be an owned mode-0700 directory.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(_root))
            {
                throw new InvalidOperationException("Launcher staging root must be empty: " + entry);
            }
        }

        _executionRoot = Path.Combine(_root, "execroot", "root");
        Scratch = Path.Combine(_root, "scratch");
        WorkingDirectory = MapAbsolute(_projectDirectory);
        foreach (string name in new[] { "SystemRoot", "DOTNET_EnableDiagnostics" })
        {
            string? value = environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                Environment.Add(name, value);
            }
        }
#else
        throw new PlatformNotSupportedException("Staged tool execution currently requires Linux and modern .NET.");
#endif
    }

    private string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A declared or mapped path cannot be empty.");
        }

        string full = Path.GetFullPath(Path.Combine(_projectDirectory, path)).TrimEnd(Path.DirectorySeparatorChar);
        return full.Length == 0 ? "/" : full;
    }

    private string MapAbsolute(string path)
    {
        string mapped = Path.GetFullPath(Path.Combine(_executionRoot, path.TrimStart('/')));
        if (mapped != _executionRoot && !mapped.StartsWith(_executionRoot + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Mapped path escapes the execution root: " + path);
        }

        return mapped;
    }

    internal string MapFile(string path, bool input)
    {
        string canonical = Canonical(path);
        if (!(input ? _inputs : _outputs).Contains(canonical))
        {
            throw new InvalidOperationException("Path is not a declared " + (input ? "input: " : "output: ") + path);
        }

        return MapAbsolute(canonical);
    }

    internal string MapDirectory(string path)
    {
        string canonical = Canonical(path);
        CheckAncestors(canonical, includeSelf: true);
        string mapped = MapAbsolute(canonical);
        Directory.CreateDirectory(mapped);
        return mapped;
    }

    private void CheckAncestors(string path, bool includeSelf)
    {
        for (string? ancestor = includeSelf ? path : Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if (_inputs.Contains(ancestor) || _outputs.Contains(ancestor))
            {
                throw new InvalidOperationException("Declared file collides with a structural directory: " + ancestor);
            }
        }
    }

    internal void Prepare(ITaskItem[] inputs, ITaskItem[] outputs)
    {
        foreach (ITaskItem item in inputs)
        {
            _inputs.Add(Canonical(item.ItemSpec));
        }

        foreach (ITaskItem item in outputs)
        {
            string output = Canonical(item.ItemSpec);
            if (!_outputs.Add(output) || _inputs.Contains(output))
            {
                throw new InvalidOperationException("Overlapping or duplicate declared output: " + output);
            }
        }

        foreach (string path in _inputs)
        {
            CheckAncestors(path, includeSelf: false);
            RequireRegularFile(path);
        }

        foreach (string path in _outputs)
        {
            CheckAncestors(path, includeSelf: false);
            ValidateDestination(path);
        }

        CheckAncestors(_projectDirectory, includeSelf: true);
        Directory.CreateDirectory(Scratch);
        Directory.CreateDirectory(WorkingDirectory);
        foreach (string path in _inputs)
        {
            string mapped = MapAbsolute(path);
            Directory.CreateDirectory(Path.GetDirectoryName(mapped)!);
#if NET8_0_OR_GREATER
                File.CreateSymbolicLink(mapped, path);
#endif
        }

        foreach (string path in _outputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapAbsolute(path))!);
        }
    }

    internal void Publish()
    {
        // Validate every member before replacing any destination. Publication is per-file,
        // not an atomic multi-file transaction, and does not defend against adversarial races.
        foreach (string path in _outputs)
        {
            string mapped = MapAbsolute(path);
            RejectLinks(mapped, _root);
            RequireRegularFile(mapped);
            ValidateDestination(path);
        }

        foreach (string path in _outputs)
        {
            string parent = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(parent);
            string sibling = Path.Combine(parent, ".msbuild-publish-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var destination = new FileStream(sibling, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var source = File.OpenRead(MapAbsolute(path)))
                {
                    source.CopyTo(destination);
                }

#if NET8_0_OR_GREATER
                    File.Move(sibling, path, overwrite: true);
#endif
            }
            finally
            {
                File.Delete(sibling);
            }
        }
    }

    private void ValidateDestination(string path)
    {
        RejectLinks(path, "/");
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("Declared outputs must be files, not directories: " + path);
        }

        if (File.Exists(path))
        {
            FileIdentity output = RequireRegularFile(path);
            foreach (string input in _inputs)
            {
                FileIdentity source = RequireRegularFile(input);
                if (output.Inode == source.Inode && output.DeviceMajor == source.DeviceMajor && output.DeviceMinor == source.DeviceMinor)
                {
                    throw new InvalidOperationException("Output aliases a declared input: " + path);
                }
            }
        }
    }

    private static void RejectLinks(string path, string stop)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Symlinks are not permitted in output paths: " + current);
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (current == stop)
            {
                break;
            }
        }
    }

    private static FileIdentity RequireRegularFile(string path)
    {
#if NET8_0_OR_GREATER
        if (Statx(-100, path, 0, 0x107, out FileIdentity identity) != 0)
        {
            throw new IOException("Cannot stat declared file: " + path, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        if ((identity.Mode & 0xF000) != 0x8000)
        {
            throw new InvalidOperationException("Declared path is not a regular file: " + path);
        }

        return identity;
#else
        throw new PlatformNotSupportedException("Staged tool execution requires modern .NET.");
#endif
    }

    internal void Cleanup() => DeleteWithoutFollowingLinks(_root);

    private static void DeleteWithoutFollowingLinks(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteWithoutFollowingLinks(entry);
        }

        Directory.Delete(path);
    }

    // Linux statx has a fixed, architecture-independent ABI. Only type and identity are used.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileIdentity
    {
        [FieldOffset(20)]
        internal uint UserId;
        [FieldOffset(28)]
        internal ushort Mode;
        [FieldOffset(32)]
        internal ulong Inode;
        [FieldOffset(136)]
        internal uint DeviceMajor;
        [FieldOffset(140)]
        internal uint DeviceMinor;
    }

#if NET8_0_OR_GREATER
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out FileIdentity identity);
#endif
}
