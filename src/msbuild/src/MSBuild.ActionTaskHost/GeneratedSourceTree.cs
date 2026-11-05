// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

namespace Microsoft.Build.ActionTaskHost;

internal sealed class GeneratedSourceTree
{
    internal string Root { get; }
    private string Archive { get; }
    private string Dyndep { get; }
    private string Receipt { get; }

    private GeneratedSourceTree(ITaskItem item)
    {
        Root = Path.TrimEndingDirectorySeparator(ExactPath(item.ItemSpec));
        Archive = ExactPath(item.GetMetadata("ArchiveFile"));
        Dyndep = ExactPath(item.GetMetadata("DyndepFile"));
        Receipt = ExactPath(item.GetMetadata("ReceiptFile"));
        Require(Path.GetDirectoryName(Root) is not null, Root);
        string[] paths = [Root, Archive, Dyndep, Receipt];
        for (int i = 0; i < paths.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                Require(!Conflict(paths[i], paths[j]), paths[i]);
            }
        }
    }

    internal static GeneratedSourceTree[] Read(Type taskType, Dictionary<string, object> parameters)
        => Read(taskType.FullName!, parameters);

    internal static GeneratedSourceTree[] ReadForCleanup(TaskInvocation invocation)
    {
        Dictionary<string, object> parameters = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (KeyValuePair<string, JsonElement> parameter in invocation.Parameters)
        {
            if (parameter.Key.Equals("DeclaredInputs", StringComparison.OrdinalIgnoreCase) ||
                parameter.Key.Equals("DeclaredOutputs", StringComparison.OrdinalIgnoreCase) ||
                parameter.Key.Equals("DeclaredOutputTrees", StringComparison.OrdinalIgnoreCase))
            {
                Require(parameters.TryAdd(parameter.Key,
                    TaskParameterBinder.ReadItems(parameter.Value, invocation.Version == 2)), parameter.Key);
            }
        }

        Require(parameters.ContainsKey("DeclaredInputs") && parameters.ContainsKey("DeclaredOutputs") &&
            parameters.ContainsKey("DeclaredOutputTrees"), invocation.TaskType);
        return Read(invocation.TaskType, parameters);
    }

    private static GeneratedSourceTree[] Read(string taskType, Dictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("DeclaredOutputTrees", out object? value))
        {
            return [];
        }

        Require(value is ITaskItem[], taskType);
        ITaskItem[] items = (ITaskItem[])value;
        Require(items.Length == 0 || (items.Length == 1 &&
            taskType == "Microsoft.CodeAnalysis.BuildTasks.CompileCsc"), taskType);
        GeneratedSourceTree[] trees = new GeneratedSourceTree[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            GeneratedSourceTree tree = trees[i] = new(items[i]);
            ITaskItem[] outputs = (ITaskItem[])parameters["DeclaredOutputs"];
            Require(outputs.Any(output => FileUtilities.PathsEqual(ExactPath(output.ItemSpec), tree.Archive)) &&
                outputs.Any(output => FileUtilities.PathsEqual(ExactPath(output.ItemSpec), tree.Dyndep)), tree.Root);
            foreach (ITaskItem output in outputs)
            {
                string path = ExactPath(output.ItemSpec);
                Require(!Conflict(tree.Root, path) && !Conflict(tree.Receipt, path), path);
            }

            foreach (ITaskItem input in (ITaskItem[])parameters["DeclaredInputs"])
            {
                string path = ExactPath(input.ItemSpec);
                Require(!Conflict(tree.Root, path) && !Conflict(tree.Receipt, path), path);
            }

            RejectLinks(tree.Root);
            RejectLinks(tree.Archive);
            RejectLinks(tree.Dyndep);
            RejectLinks(tree.Receipt);
        }

        return trees;
    }

    internal void WriteDyndep()
    {
        using ZipArchive archive = ZipFile.OpenRead(Archive);
        string[] files = Entries(archive).Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        StringBuilder text = new("ninja_dyndep_version = 1\nbuild ");
        text.Append(Escape(Receipt)).Append(" | ").Append(Escape(Root));
        foreach (string name in files)
        {
            text.Append(' ').Append(Escape(MemberPath(name)));
        }

        text.Append(": dyndep\n");
        WriteAtomic(Dyndep, Encoding.UTF8.GetBytes(text.ToString()));
    }

    internal void Materialize()
    {
        RejectLinks(Root);
        RejectLinks(Receipt);
        using ZipArchive archive = ZipFile.OpenRead(Archive);
        Dictionary<string, ZipArchiveEntry> entries = Entries(archive);
        string[] files = entries.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        TreeReceipt? previous = ReadReceipt();
        HashSet<string> allowed = new(FileUtilities.PathComparer);
        HashSet<string> allowedDirectories = new(FileUtilities.PathComparer);
        foreach (string name in files)
        {
            AllowParents(MemberPath(name));
        }

        if (previous is not null)
        {
            foreach (string name in previous.Files)
            {
                string path = MemberPath(name);
                allowed.Add(path);
                AllowParents(path);
            }
        }

        if (Directory.Exists(Root))
        {
            Stack<string> directories = new();
            directories.Push(Root);
            while (directories.TryPop(out string? directory))
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    RejectLinks(path);
                    if (Directory.Exists(path))
                    {
                        Require(allowedDirectories.Contains(path), path);
                        directories.Push(path);
                    }
                    else
                    {
                        Require(allowed.Contains(path), path);
                    }
                }
            }
        }
        else
        {
            Require(!File.Exists(Root), Root);
        }

        if (previous is not null)
        {
            foreach (string name in previous.Files)
            {
                string path = MemberPath(name);
                if (entries.ContainsKey(name))
                {
                    continue;
                }

                File.Delete(path);
                string? directory = Path.GetDirectoryName(path);
                while (directory is not null && !FileUtilities.PathsEqual(directory, Root) &&
                    Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
        }

        Directory.CreateDirectory(Root);
        foreach (string name in files)
        {
            string path = MemberPath(name);
            Require(!Directory.Exists(path), path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using Stream source = entries[name].Open();
            WriteAtomic(path, source);
        }

        using FileStream archiveStream = File.OpenRead(Archive);
        TreeReceipt receipt = new(1, Root, Archive, Convert.ToHexString(SHA256.HashData(archiveStream)), files);
        WriteAtomic(Receipt, JsonSerializer.SerializeToUtf8Bytes(receipt, TaskInvocation.JsonOptions));
        Directory.SetLastWriteTimeUtc(Root, DateTime.UtcNow);

        void AllowParents(string file)
        {
            string? directory = Path.GetDirectoryName(file);
            while (directory is not null && !FileUtilities.PathsEqual(directory, Root) &&
                allowedDirectories.Add(directory))
            {
                directory = Path.GetDirectoryName(directory);
            }
        }
    }

    internal Action PrepareCleanup()
    {
        RejectLinks(Root);
        Require(!File.Exists(Root), Root);
        TreeReceipt? receipt = ReadReceipt();
        HashSet<string> owned = new(FileUtilities.PathComparer);
        if (receipt is not null)
        {
            foreach (string name in receipt.Files)
            {
                owned.Add(MemberPath(name));
            }
        }

        List<string> files = [];
        List<string> directories = [];
        if (Directory.Exists(Root))
        {
            Stack<string> pending = new();
            pending.Push(Root);
            while (pending.TryPop(out string? directory))
            {
                directories.Add(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    RejectLinks(path);
                    if (Directory.Exists(path))
                    {
                        pending.Push(path);
                    }
                    else
                    {
                        Require(owned.Contains(path), path);
                        files.Add(path);
                    }
                }
            }
        }

        // All trees are preflighted before any cleanup plan is executed.
        return () =>
        {
            foreach (string file in files)
            {
                RejectLinks(file);
                File.Delete(file);
            }

            for (int i = directories.Count - 1; i >= 0; i--)
            {
                RejectLinks(directories[i]);
                if (Directory.Exists(directories[i]))
                {
                    Directory.Delete(directories[i], recursive: false);
                }
            }

            RejectLinks(Receipt);
            File.Delete(Receipt);
        };
    }

    private TreeReceipt? ReadReceipt()
    {
        RejectLinks(Receipt);
        Require(!Directory.Exists(Receipt), Receipt);
        if (!File.Exists(Receipt))
        {
            return null;
        }

        TreeReceipt? receipt = JsonSerializer.Deserialize<TreeReceipt>(File.ReadAllText(Receipt), TaskInvocation.JsonOptions);
        Require(receipt is not null && receipt.Version == 1 && receipt.Root == Root &&
            receipt.Archive == Archive && receipt.Files is not null, Receipt);
        HashSet<string> paths = new(FileUtilities.PathComparer);
        foreach (string name in receipt.Files)
        {
            string path = MemberPath(name);
            foreach (string existing in paths)
            {
                Require(!Conflict(existing, path), path);
            }

            Require(paths.Add(path), path);
        }

        return receipt;
    }

    private Dictionary<string, ZipArchiveEntry> Entries(ZipArchive archive)
    {
        Dictionary<string, ZipArchiveEntry> entries = new(FileUtilities.PathComparer);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = MemberPath(entry.FullName);
            Require((entry.ExternalAttributes & unchecked((int)0xF0000000)) != unchecked((int)0xA0000000), path);
            foreach (string existing in entries.Keys)
            {
                Require(!Conflict(MemberPath(existing), path), path);
            }

            Require(entries.TryAdd(entry.FullName, entry), path);
        }

        return entries;
    }

    private string MemberPath(string name)
    {
        Require(!string.IsNullOrEmpty(name) && name.IndexOfAny(['\\', '\r', '\n', '\t', '|', '#']) < 0 &&
            !Path.IsPathRooted(name), name);
        foreach (string part in name.Split('/'))
        {
            Require(part.Length != 0 && part is not ("." or "..") &&
                part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, name);
        }

        string path = Path.GetFullPath(Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(Root + Path.DirectorySeparatorChar, FileUtilities.PathComparison), name);
        return path;
    }

    private static string ExactPath(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
            path.IndexOfAny(['*', '?', '\r', '\n', '\t', '|', '#']) < 0, path);
        return Path.GetFullPath(path);
    }

    private static bool Conflict(string first, string second)
        => FileUtilities.PathsEqual(first, second) ||
            first.StartsWith(second + Path.DirectorySeparatorChar, FileUtilities.PathComparison) ||
            second.StartsWith(first + Path.DirectorySeparatorChar, FileUtilities.PathComparison);

    private static string Escape(string path)
        => path.Replace("$", "$$").Replace(" ", "$ ").Replace(":", "$:");

    private static void RejectLinks(string path)
    {
        while (path is not null)
        {
            try
            {
                Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, path);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            path = Path.GetDirectoryName(path)!;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        WriteAtomic(path, stream);
    }

    private static void WriteAtomic(string path, Stream source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                created = true;
                source.CopyTo(destination);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (created)
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string path)
    {
        if (!condition)
        {
            throw new InvalidDataException(HostResources.Format("InvalidGeneratedTree", path));
        }
    }

    private sealed record TreeReceipt(int Version, string Root, string Archive, string Sha256, string[] Files);
}
