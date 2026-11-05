// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

internal static class Program
{
    private static string s_runtime = null!;
    private static string s_app = null!;
    private static string s_native = null!;
    private static string s_root = null!;

    private static void Main(string[] args)
    {
        s_runtime = Path.GetFullPath(args[0]);
        s_app = Path.GetFullPath(args[1]);
        s_native = Path.GetFullPath(args[2]);
        s_root = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(s_root);
        string toolset = Path.Combine(Path.GetDirectoryName(s_app)!, "toolset");
        string managed = Path.Combine(toolset, "action-host", "MSBuild.ActionTaskHost.dll");
        string graph = Path.Combine(s_root, "native.ninja");
        string source = Path.Combine(s_root, "source.txt");
        string destination = Path.Combine(s_root, "copy.txt");
        string project = Path.Combine(s_root, "Build.proj");
        new XDocument(new XElement("Project",
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", source), new XAttribute("Lines", "first%3Bline;second"),
                    new XAttribute("Overwrite", "yes"), new XAttribute("DeclaredInputs", ""),
                    new XAttribute("DeclaredOutputs", source)),
                new XElement("CopyFiles",
                    new XAttribute("SourceFiles", source), new XAttribute("DestinationFiles", destination),
                    new XAttribute("DeclaredInputs", source), new XAttribute("DeclaredOutputs", destination))))).Save(project);
        Build(project, graph);
        Require(!File.Exists(source) && !File.Exists(destination), "Construction executed builtin tasks.");
        string[] commands = Commands(graph);
        Require(commands.Length == 2 && commands.All(line => line.Contains(s_native, StringComparison.Ordinal) &&
            !line.Contains(s_runtime, StringComparison.Ordinal)), "Builtins were not routed directly to the native host.");
        Require(File.ReadAllText(graph).Contains(s_native + ".json", StringComparison.Ordinal),
            "The native catalog is not tracked as an action input.");
        Run("ninja", ["-f", graph]);
        byte[] expected = Encoding.UTF8.GetBytes("first;line" + Environment.NewLine + "second" + Environment.NewLine);
        Require(File.ReadAllBytes(destination).SequenceEqual(expected), "Native task output differs.");
        Require(Run("ninja", ["-f", graph]).Contains("no work to do", StringComparison.Ordinal),
            "Native graph did not become no-work.");

        JsonObject copy = FindInvocation(graph, "CopyFiles");
        JsonObject write = FindInvocation(graph, "WriteLinesToFile");
        string isolated = Path.Combine(s_root, "isolated %;file.txt");
        Set(copy, "DestinationFiles", isolated);
        Set(copy, "DeclaredOutputs", isolated);
        Set(write, "File", isolated);
        Set(write, "DeclaredOutputs", isolated);
        Set(write, "Lines", "caf\u00e9", "literal %;$(text)");
        foreach (int version in new[] { 1, 2 })
        {
            JsonObject versionedCopy = Version(copy, version);
            JsonObject versionedWrite = Version(write, version);
            foreach (JsonObject invocation in new[] { versionedCopy, versionedWrite })
            {
                byte[] nativeBytes = Invoke(invocation, true, isolated, managed, native: true);
                byte[] managedBytes = Invoke(invocation, true, isolated, managed, native: false);
                Require(nativeBytes.SequenceEqual(managedBytes), "Native and managed binding differ.");
            }
        }

        foreach (string boolean in new[] { "true", "on", "yes", "!false", "false", "off", "no", "!true" })
        {
            JsonObject invocation = (JsonObject)write.DeepClone();
            Set(invocation, "Overwrite", boolean);
            byte[] nativeBytes = Invoke(invocation, true, isolated, managed, native: true);
            byte[] managedBytes = Invoke(invocation, true, isolated, managed, native: false);
            Require(nativeBytes.SequenceEqual(managedBytes), "MSBuild boolean binding changed: " + boolean);
        }

        foreach (string parameter in new[] { "File", "DeclaredOutputs" })
        {
            JsonObject invocation = (JsonObject)write.DeepClone();
            invocation["parameters"]!.AsObject().Remove(parameter);
            CheckFailure(invocation, isolated, managed);
        }

        foreach (string parameter in new[] { "DeclaredInputs", "DeclaredOutputs" })
        {
            JsonObject invocation = (JsonObject)copy.DeepClone();
            invocation["parameters"]!.AsObject().Remove(parameter);
            CheckFailure(invocation, isolated, managed);
        }

        JsonObject missing = (JsonObject)copy.DeepClone();
        Set(missing, "SourceFiles", Path.Combine(s_root, "missing.txt"));
        Set(missing, "DeclaredInputs", Path.Combine(s_root, "missing.txt"));
        CheckFailure(missing, isolated, managed);
        JsonObject undeclared = (JsonObject)copy.DeepClone();
        Set(undeclared, "DeclaredOutputs", Path.Combine(s_root, "unrelated.txt"));
        CheckFailure(undeclared, isolated, managed);
        JsonObject duplicate = (JsonObject)copy.DeepClone();
        duplicate["parameters"]!["sourcefiles"] = duplicate["parameters"]!["SourceFiles"]!.DeepClone();
        CheckFailure(duplicate, isolated, managed);
        JsonObject invalidBoolean = (JsonObject)write.DeepClone();
        Set(invalidBoolean, "Overwrite", "not a boolean");
        CheckFailure(invalidBoolean, isolated, managed);
        JsonObject invalidEncoding = (JsonObject)write.DeepClone();
        Set(invalidEncoding, "Encoding", "not an encoding");
        CheckFailure(invalidEncoding, isolated, managed);
        JsonObject wrongLength = (JsonObject)copy.DeepClone();
        Set(wrongLength, "SourceFiles", source, source);
        CheckFailure(wrongLength, isolated, managed);
        JsonObject unknown = (JsonObject)copy.DeepClone();
        Set(unknown, "BuildEngine", "not allowed");
        CheckFailure(unknown, isolated, managed);
        JsonObject invalidVersion = (JsonObject)copy.DeepClone();
        invalidVersion["version"] = 3;
        CheckFailure(invalidVersion, isolated, managed);
        JsonObject malformed = (JsonObject)copy.DeepClone();
        malformed["parameters"]!["SourceFiles"]!["items"] = null;
        CheckFailure(malformed, isolated, managed);

        JsonObject different = (JsonObject)write.DeepClone();
        Set(different, "WriteOnlyWhenDifferent", "true");
        byte[] same = Invoke(different, true, isolated, managed, native: true);
        string invocationPath = Save(different);
        DateTime before = File.GetLastWriteTimeUtc(isolated);
        Run(s_native, ["--invocation", invocationPath], emptyEnvironment: true);
        Require(File.GetLastWriteTimeUtc(isolated) == before && File.ReadAllBytes(isolated).SequenceEqual(same),
            "WriteOnlyWhenDifferent changed identical output.");

        Run("ninja", ["-f", graph, "clean"]);
        Require(!File.Exists(source) && !File.Exists(destination), "Native clean retained graph outputs.");
        Run("ninja", ["-f", graph]);
        Require(File.ReadAllBytes(destination).SequenceEqual(expected), "Native clean/rebuild changed output.");

        string managedGraph = Path.Combine(s_root, "managed.ninja");
        Build(project, managedGraph, "-p:MSBuildNativeActionTaskHost=");
        Require(Commands(managedGraph).All(line => line.Contains(managed, StringComparison.Ordinal) &&
            !line.Contains(s_native, StringComparison.Ordinal)), "Explicit managed-only selection was ignored.");

        string customDirectory = Path.Combine(s_root, "custom");
        string customAssembly = Path.Combine(customDirectory, "Microsoft.Build.Tasks.Core.dll");
        foreach (string dependency in new[] { "Microsoft.Build.Framework.dll", "Microsoft.Build.Utilities.Core.dll", "Microsoft.NET.StringTools.dll" })
        {
            File.Copy(Path.Combine(toolset, dependency), Path.Combine(customDirectory, dependency), overwrite: true);
        }

        string customOutput = Path.Combine(s_root, "custom.txt");
        string customProject = Path.Combine(s_root, "Custom.proj");
        new XDocument(new XElement("Project",
            new XElement("UsingTask", new XAttribute("TaskName", "Microsoft.Build.Tasks.CopyFiles"),
                new XAttribute("AssemblyFile", customAssembly)),
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("Microsoft.Build.Tasks.CopyFiles", new XAttribute("DeclaredInputs", ""),
                    new XAttribute("DeclaredOutputs", customOutput))))).Save(customProject);
        string customGraph = Path.Combine(s_root, "custom.ninja");
        Build(customProject, customGraph);
        Require(Commands(customGraph).Single().Contains(managed, StringComparison.Ordinal),
            "A different task assembly with the builtin type/name did not fall back to managed execution.");
        Run("ninja", ["-f", customGraph]);
        Require(File.ReadAllText(customOutput) == "custom implementation", "The custom task did not execute.");
        JsonObject custom = FindInvocation(customGraph, "CopyFiles");
        Invoke(custom, false, customOutput, managed, native: true);
        Require(File.ReadAllText(customOutput) == "sentinel", "Native identity rejection modified output.");

        string stale = Path.Combine(s_root, "stale-native");
        File.Copy(s_native, stale);
        File.SetUnixFileMode(stale, File.GetUnixFileMode(s_native));
        File.Copy(s_native + ".json", stale + ".json");
        JsonObject staleCatalog = JsonNode.Parse(File.ReadAllText(stale + ".json"))!.AsObject();
        staleCatalog["hostSha256"] = new string('0', 64);
        File.WriteAllText(stale + ".json", staleCatalog.ToJsonString());
        string staleGraph = Path.Combine(s_root, "stale.ninja");
        Run(s_runtime, [s_app, "build", project, "-p:MSBuildEmitActionGraph=" + staleGraph,
            "-p:MSBuildNativeActionTaskHost=" + stale], success: false);
        Require(!File.Exists(staleGraph), "A stale native catalog was published.");
        Console.WriteLine("Native builtin selection, binding parity, refusal, fallback, clean, and no-work checks passed.");
    }

    private static void CheckFailure(JsonObject invocation, string output, string managed)
    {
        Invoke(invocation, false, output, managed, native: true);
        Invoke(invocation, false, output, managed, native: false);
    }

    private static byte[] Invoke(JsonObject invocation, bool success, string output, string managed, bool native)
    {
        File.WriteAllText(output, "sentinel");
        string file = Save(invocation);
        Run(native ? s_native : s_runtime,
            native ? ["--invocation", file] : [managed, "--invocation", file], success,
            emptyEnvironment: native);
        if (!success)
        {
            Require(File.ReadAllText(output) == "sentinel", "A refused task modified output.");
        }

        return File.ReadAllBytes(output);
    }

    private static string Save(JsonObject invocation)
    {
        string file = Path.Combine(s_root, "invocation.json");
        File.WriteAllText(file, invocation.ToJsonString());
        return file;
    }

    private static JsonObject Version(JsonObject invocation, int version)
    {
        JsonObject copy = (JsonObject)invocation.DeepClone();
        copy["version"] = version;
        if (version == 1)
        {
            JsonObject parameters = copy["parameters"]!.AsObject();
            foreach (string name in parameters.Select(pair => pair.Key).ToArray())
            {
                parameters[name] = name is "Overwrite" or "Encoding" or "WriteOnlyWhenDifferent" or "FailIfNotIncremental"
                    ? parameters[name]!["scalar"]!.DeepClone()
                    : name == "File" ? parameters[name]!["items"]![0]!.DeepClone()
                    : parameters[name]!["items"]!.DeepClone();
            }
        }

        return copy;
    }

    private static void Set(JsonObject invocation, string name, params string[] values)
    {
        JsonArray items = new();
        foreach (string value in values)
        {
            items.Add(new JsonObject { ["itemSpec"] = value, ["metadata"] = new JsonObject() });
        }

        invocation["parameters"]![name] = new JsonObject { ["scalar"] = string.Join(";", values), ["items"] = items };
    }

    private static JsonObject FindInvocation(string graph, string task)
        => Directory.GetFiles(graph + ".actions", "*.json")
            .Select(file => JsonNode.Parse(File.ReadAllText(file))!.AsObject())
            .Single(invocation => invocation["taskType"]!.GetValue<string>().EndsWith(task, StringComparison.Ordinal));

    private static string[] Commands(string graph)
        => File.ReadAllLines(graph).Where(line => line.StartsWith("  command = ", StringComparison.Ordinal) &&
            line.Contains("--invocation", StringComparison.Ordinal)).ToArray();

    private static void Build(string project, string graph, params string[] extra)
        => Run(s_runtime, new[] { s_app, "build", project, "-p:MSBuildEmitActionGraph=" + graph }.Concat(extra).ToArray());

    private static string Run(string executable, string[] arguments, bool success = true, bool emptyEnvironment = false)
    {
        ProcessStartInfo start = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (emptyEnvironment)
        {
            start.Environment.Clear();
        }

        using Process process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Require((process.ExitCode == 0) == success, executable + ": " + text);
        Require(!text.Contains("MissingMetadataException", StringComparison.Ordinal) &&
            !text.Contains("MissingManifestResourceException", StringComparison.Ordinal), "Incomplete native metadata: " + text);
        return text;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
