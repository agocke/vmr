// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static void Main(string[] args)
    {
        switch (args[0])
        {
            case "run":
                CheckGeneratedCode(args[1], args[2]);
                break;
            case "compare":
                CompareTrees(args[1], args[2]);
                break;
            case "graph":
                CheckGraph(args[1], args[2], args[3]);
                break;
            case "recovery-failures":
                CheckRecoveryFailures(args[1], args[2], args[3], args[4]);
                break;
            case "empty":
                Require(Directory.Exists(args[1]), "Zero sources must publish a real directory.");
                Require(Directory.GetFileSystemEntries(args[1]).Length == 0, "Empty tree retained owned files.");
                using (ZipArchive archive = ZipFile.OpenRead(args[1] + ".tree.zip"))
                {
                    Require(archive.Entries.Count == 0, "Empty tree archive has entries.");
                }
                Require(File.Exists(args[1] + ".tree.json"), "Empty tree has no receipt.");
                break;
            default:
                throw new InvalidOperationException("Unknown fixture command: " + args[0]);
        }
    }

    private static void CheckGeneratedCode(string hint, string root)
    {
        Type? generated = Assembly.GetExecutingAssembly().GetType("GeneratedFixture");
        if (hint == "none")
        {
            Require(generated is null, "Zero sources still compiled GeneratedFixture.");
            return;
        }

        Require(generated is not null, "Generator did not contribute compiled code.");
        Require((string?)generated!.GetProperty("Text")!.GetValue(null) == "generated café λ",
            "Generated UTF-8 and UTF-16 source did not execute correctly.");
        string expected = root + "/CompilerGeneratedGenerator/ConfigurableGenerator/" + hint;
        string actual = (string)generated.GetMethod("Path")!.Invoke(null, null)!;
        Require(actual == expected, $"CallerFilePath changed logical generated path: {actual} != {expected}");

        using FileStream pdb = File.OpenRead(Path.ChangeExtension(
            Path.Combine(AppContext.BaseDirectory, "CompilerGeneratedFixture.dll"), ".pdb"));
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
        MetadataReader reader = provider.GetMetadataReader();
        string[] documents = reader.Documents.Select(handle => reader.GetString(reader.GetDocument(handle).Name)).ToArray();
        Require(documents.Contains(expected, StringComparer.Ordinal),
            "Portable PDB does not preserve the logical generated document: " + expected);
        Require(!documents.Any(path => path.Contains(".tree.zip", StringComparison.Ordinal)),
            "Portable PDB leaked an archive path.");
    }

    private static void CompareTrees(string action, string ordinary)
    {
        string[] expected =
        [
            "CompilerGeneratedGenerator/ConfigurableGenerator/First.g.cs",
            "CompilerGeneratedGenerator/ConfigurableGenerator/nested/Stable.g.cs",
        ];
        string[] actionFiles = RelativeFiles(action);
        Require(actionFiles.SequenceEqual(expected), "Action changed standard generator filenames: " + string.Join(", ", actionFiles));
        Require(RelativeFiles(ordinary).SequenceEqual(expected), "Ordinary SDK generator filenames differ.");
        using ZipArchive archive = ZipFile.OpenRead(action + ".tree.zip");
        Require(archive.Entries.Select(entry => entry.FullName).SequenceEqual(expected),
            "Archive entries must use standard, ordinal-sorted relative names.");
        for (int i = 0; i < expected.Length; i++)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(action, expected[i]));
            Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(ordinary, expected[i]))),
                "Generated source encoding/content differs from ordinary compilation: " + expected[i]);
            using Stream entry = archive.Entries[i].Open();
            using var buffer = new MemoryStream();
            entry.CopyTo(buffer);
            Require(bytes.SequenceEqual(buffer.ToArray()), "Archive bytes differ from published source: " + expected[i]);
        }
        Require(!File.Exists(ordinary + ".tree.zip") && !File.Exists(ordinary + ".tree.dd") &&
            !File.Exists(ordinary + ".tree.json"), "Ordinary SDK compilation unexpectedly produced tree sidecars.");
    }

    private static void CheckGraph(string graph, string root, string logicalRoot)
    {
        int compilerCount = 0;
        foreach (string file in Directory.GetFiles(graph + ".actions", "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement invocation = document.RootElement;
            if (invocation.GetProperty("taskType").GetString() != "Microsoft.CodeAnalysis.BuildTasks.CompileCsc")
            {
                continue;
            }

            compilerCount++;
            JsonElement parameters = invocation.GetProperty("parameters");
            JsonElement trees = parameters.GetProperty("DeclaredOutputTrees").GetProperty("items");
            Require(trees.GetArrayLength() == 1, "CompileCsc must declare exactly one generated-source tree.");
            JsonElement tree = trees[0];
            Require(tree.GetProperty("itemSpec").GetString() == root, "Formatter changed the exact tree root.");
            JsonElement metadata = tree.GetProperty("metadata");
            Require(metadata.GetProperty("ArchiveFile").GetString() == root + ".tree.zip", "Wrong tree archive metadata.");
            Require(metadata.GetProperty("DyndepFile").GetString() == root + ".tree.dd", "Wrong tree dyndep metadata.");
            Require(metadata.GetProperty("ReceiptFile").GetString() == root + ".tree.json", "Wrong tree receipt metadata.");
            JsonElement mapping = parameters.GetProperty("PathMappings").GetProperty("items")
                .EnumerateArray().Where(item => root.StartsWith(item.GetProperty("itemSpec").GetString()!,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(item => item.GetProperty("itemSpec").GetString()!.Length).First();
            string mappedRoot = mapping.GetProperty("metadata").GetProperty("MappedPath").GetString()! +
                root[mapping.GetProperty("itemSpec").GetString()!.Length..].Replace('\\', '/');
            Require(mappedRoot == logicalRoot, $"Captured path mapping changed: {mappedRoot} != {logicalRoot}");
        }

        Require(compilerCount == 1, "Consumer must have exactly one CompileCsc action.");
        Require(File.ReadAllText(graph).Contains("dyndep =", StringComparison.Ordinal),
            "Generated tree materialization must consume Ninja dyndep.");
    }

    private static string[] RelativeFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .OrderBy(static file => file, StringComparer.Ordinal).ToArray();

    private static void CheckRecoveryFailures(string graph, string root, string host, string actionHost)
    {
        Require(root.Contains("/genbuild-smoke/", StringComparison.Ordinal) ||
            root.Contains("\\genbuild-smoke\\", StringComparison.Ordinal), "Recovery tests require an isolated smoke fixture.");
        string invocation = Directory.GetFiles(graph + ".actions", "*.json").Single(file =>
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.GetProperty("taskType").GetString() == "Microsoft.CodeAnalysis.BuildTasks.CompileCsc";
        });
        byte[] archive = File.ReadAllBytes(root + ".tree.zip");
        byte[] receipt = File.ReadAllBytes(root + ".tree.json");
        DateTime archiveTime = File.GetLastWriteTimeUtc(root + ".tree.zip");
        DateTime receiptTime = File.GetLastWriteTimeUtc(root + ".tree.json");
        string source = Path.Combine(root, "CompilerGeneratedGenerator", "ConfigurableGenerator", "First.g.cs");
        byte[] sourceBytes = File.ReadAllBytes(source);
        DateTime sourceTime = File.GetLastWriteTimeUtc(source);
        try
        {
            File.WriteAllText(root + ".tree.zip", "not a ZIP archive");
            Reject("malformed ZIP");
            File.WriteAllBytes(root + ".tree.zip", archive);
            File.SetLastWriteTimeUtc(root + ".tree.zip", archiveTime);

            string escapedFile = Path.Combine(Path.GetDirectoryName(root)!, "escaped.g.cs");
            Require(!File.Exists(escapedFile), "Traversal fixture destination already exists.");
            RejectEntries("ZIP traversal entry", ["../escaped.g.cs"]);
            Require(!File.Exists(escapedFile), "Rejected traversal archive wrote outside the generated tree.");
            RejectEntries("ZIP duplicate entries", ["Duplicate.g.cs", "Duplicate.g.cs"]);
            RejectEntries("ZIP file/descendant collision", ["Shape.g.cs", "Shape.g.cs/Nested.g.cs"]);
            RejectEntries("ZIP symlink entry", ["Linked.g.cs"], symbolicLink: true);

            File.WriteAllText(root + ".tree.json", "{ malformed receipt");
            Reject("malformed receipt");
            File.WriteAllBytes(root + ".tree.json", receipt);
            File.SetLastWriteTimeUtc(root + ".tree.json", receiptTime);

            File.WriteAllText(root + ".tree.json", "null");
            Reject("null receipt");
            File.WriteAllBytes(root + ".tree.json", receipt);
            File.SetLastWriteTimeUtc(root + ".tree.json", receiptTime);

            // Windows needs additional privileges for filesystem symlinks; Unix does not.
            if (!OperatingSystem.IsWindows())
            {
                string sentinel = Path.Combine(Path.GetDirectoryName(root)!, "symlink-sentinel.txt");
                File.WriteAllText(sentinel, "unowned symlink target");
                File.Delete(source);
                File.CreateSymbolicLink(source, sentinel);
                Reject("generated-file symlink");
                Require(new FileInfo(source).LinkTarget == sentinel, "Rejected recovery replaced the symlink.");
                Require(File.ReadAllText(sentinel) == "unowned symlink target", "Recovery modified the symlink target.");
                File.Delete(sentinel);
                Require(!File.Exists(sentinel) && new FileInfo(source).LinkTarget == sentinel,
                    "Broken-symlink fixture still has a live target.");
                Reject("broken generated-file symlink");
                Require(new FileInfo(source).LinkTarget == sentinel && !File.Exists(sentinel),
                    "Rejected recovery replaced the broken symlink or created its target.");
                File.Delete(source);
                File.WriteAllBytes(source, sourceBytes);
                File.SetLastWriteTimeUtc(source, sourceTime);
            }
            else
            {
                Console.WriteLine("Symlink recovery case not run on Windows (requires symlink privileges).");
            }
        }
        finally
        {
            File.WriteAllBytes(root + ".tree.zip", archive);
            File.SetLastWriteTimeUtc(root + ".tree.zip", archiveTime);
            File.WriteAllBytes(root + ".tree.json", receipt);
            File.SetLastWriteTimeUtc(root + ".tree.json", receiptTime);
            if (new FileInfo(source).LinkTarget is not null)
            {
                File.Delete(source);
            }
            File.WriteAllBytes(source, sourceBytes);
            File.SetLastWriteTimeUtc(source, sourceTime);
        }

        void Reject(string label)
        {
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Append(root + ".tree.json").OrderBy(static file => file, StringComparer.Ordinal).ToArray();
            byte[][] before = files.Select(ReadPublishedBytes).ToArray();
            DateTime[] times = files.Select(File.GetLastWriteTimeUtc).ToArray();
            var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(actionHost);
            start.ArgumentList.Add("--materialize-tree");
            start.ArgumentList.Add(invocation);
            start.ArgumentList.Add(root);
            using Process process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Require(process.ExitCode != 0, $"Materializer accepted {label}: {stdout.Result} {stderr.Result}");
            Require(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Append(root + ".tree.json")
                .OrderBy(static file => file, StringComparer.Ordinal).SequenceEqual(files),
                $"Rejected {label} changed the published file set.");
            for (int i = 0; i < files.Length; i++)
            {
                Require(ReadPublishedBytes(files[i]).SequenceEqual(before[i]) &&
                    File.GetLastWriteTimeUtc(files[i]) == times[i], $"Rejected {label} modified {files[i]}.");
            }
            Console.WriteLine($"Recovery rejection passed: {label}.");
        }

        static byte[] ReadPublishedBytes(string file) =>
            new FileInfo(file).LinkTarget is string target
                ? Encoding.UTF8.GetBytes("symlink:" + target)
                : File.ReadAllBytes(file);

        void RejectEntries(string label, string[] names, bool symbolicLink = false)
        {
            using (FileStream output = File.Create(root + ".tree.zip"))
            using (var invalid = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (string name in names)
                {
                    ZipArchiveEntry entry = invalid.CreateEntry(name);
                    if (symbolicLink)
                    {
                        const int UnixSymbolicLinkAttributes = unchecked((int)0xA0000000);
                        entry.ExternalAttributes = UnixSymbolicLinkAttributes;
                    }
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("invalid archive fixture");
                }
            }
            Reject(label);
            File.WriteAllBytes(root + ".tree.zip", archive);
            File.SetLastWriteTimeUtc(root + ".tree.zip", archiveTime);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
