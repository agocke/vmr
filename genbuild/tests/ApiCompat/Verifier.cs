// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class Verifier
{
    private static void Main(string[] args)
    {
        if (args[0] is "produced-prepared" or "produced-graph")
        {
            CheckProducedReferences(args, checkGraph: args[0] == "produced-graph");
            return;
        }
        if (args[0] == "produced-execution")
        {
            CheckProducedExecution(args[1], args[2], args[3], expectCompile: bool.Parse(args[4]));
            return;
        }
        if (args[0] == "success-diagnostics")
        {
            Require(Diagnostics(args[1]).Length == 0 && Diagnostics(args[2]).Length == 0,
                "Ordinary and produced-reference actions must both succeed without CP diagnostics.");
            return;
        }
        if (args[0] == "no-work")
        {
            string[] lines = File.ReadAllLines(args[1]).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            Require(lines.Length == int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) &&
                lines.All(line => line.Trim() == "ninja: no work to do."),
                "Every unchanged produced-reference graph must be a Ninja no-op.");
            return;
        }
        if (args[0] == "diagnostics")
        {
            string[] ordinary = Diagnostics(args[1]);
            string[] action = Diagnostics(args[2]);
            string diagnosticId = args.Length > 3 ? args[3] : "CP0002";
            Require(ordinary.Length == 1 && ordinary[0].StartsWith(diagnosticId + ":", StringComparison.Ordinal),
                "Expected precisely one " + diagnosticId + " diagnostic: " + string.Join("; ", ordinary));
            Require(ordinary.SequenceEqual(action), "Ordinary and action CP diagnostics differ.");
            return;
        }

        string root = args[1];
        string graph = Path.Combine(root, "build.ninja");
        string snapshot = Path.Combine(root, "obj/apicompatinputs/Debug/AnyCPU/net11.0/ApiCompat.csproj.g.props");
        XElement prepared = XElement.Load(snapshot);
        Require(SamePath(prepared.Descendants("_ApiCompatPreparedDefaultSuppressionFile").Single().Value,
            Path.Combine(root, "CompatibilitySuppressions.xml")), "Restore did not capture the default suppression existence fact.");
        string referenceDirectory = args[2];
        string[] inventory = prepared.Descendants("_ApiCompatPreparedReferenceFile")
            .Select(item => item.Attribute("Include")!.Value).ToArray();
        Require(prepared.Descendants("_ApiCompatPreparedReferenceDirectory").Any(item =>
            SamePath(item.Attribute("Include")!.Value, referenceDirectory)), "Restore did not prepare the reference directory.");
        Require(inventory.Length != 0, "Restore captured an empty reference-directory inventory.");
        foreach (string file in Directory.GetFiles(referenceDirectory, "*.dll"))
        {
            Require(inventory.Any(entry => SamePath(entry, file)), "Prepared inventory omitted directory member: " + file);
        }

        int comparisons = 0;
        foreach (string manifest in Directory.GetFiles(graph + ".actions", "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
            JsonElement invocation = document.RootElement;
            if (invocation.GetProperty("taskType").GetString() != "Microsoft.DotNet.ApiCompat.Task.ValidateAssembliesTask")
            {
                continue;
            }

            comparisons++;
            JsonElement parameters = invocation.GetProperty("parameters");
            string[] inputs = Items(parameters, "DeclaredInputs");
            string[] outputs = Items(parameters, "DeclaredOutputs");
            string implementation = Path.Combine(root, "obj/Debug/net11.0/ApiCompatFixture.dll");
            string semaphore = Path.Combine(root, "obj/Debug/net11.0/Microsoft.DotNet.ApiCompat.ValidateAssemblies.Common.semaphore");
            List<string> expected =
            [
                implementation,
                Path.GetFullPath(Path.Combine(root, "../contract/ApiCompatFixture.dll")),
                Path.Combine(root, "CompatibilitySuppressions.xml"),
                Path.Combine(root, "excluded-attributes.txt"),
                Path.Combine(args[3], "Microsoft.CodeAnalysis.dll"),
                Path.Combine(args[3], "Microsoft.CodeAnalysis.CSharp.dll"),
                .. inventory,
            ];
            foreach (string file in expected)
            {
                Require(inputs.Any(input => SamePath(input, file)), "ApiCompat footprint omitted: " + file);
            }
            Require(!inputs.Any(input => SamePath(input, referenceDirectory)), "A reference directory is not a file input.");
            Require(outputs.Length == 1 && SamePath(outputs[0], semaphore), "ApiCompat must declare only its success semaphore.");
            Require(!File.Exists(implementation) && !File.Exists(semaphore),
                "Construction read/produced a future implementation or semaphore.");
        }
        Require(comparisons == 1, "Expected exactly one source ValidateAssembliesTask action.");
    }

    private static void CheckProducedReferences(string[] args, bool checkGraph)
    {
        string root = args[1];
        string framework = args[2];
        string roslyn = args[3];
        string testCase = args[4];
        string compileReference = args[5];
        string future = Path.Combine(root, "future refs");
        string member = Path.Combine(future, "Dependency.dll");
        bool producesFile = testCase is "future" or "stale" or "precedence";
        XElement prepared = XElement.Load(Path.Combine(root,
            "obj/apicompatinputs/Debug/AnyCPU/net11.0/ApiCompat.csproj.g.props"));
        Require(!prepared.Descendants("_ApiCompatPreparedReferenceDirectory")
            .Any(item => SamePath(item.Attribute("Include")!.Value, future)),
            "Preparation must not inventory a producer-owned directory: " + testCase);
        Require(!prepared.Descendants("_ApiCompatPreparedReferenceFile")
            .Any(item => SamePath(item.Element("ReferenceDirectory")!.Value, future)),
            "Preparation captured a stale/undeclared producer-directory member: " + testCase);
        Require(!File.Exists(Path.Combine(root, "obj/Debug/net11.0/ApiCompatFixture.dll")) &&
            !File.Exists(Semaphore(root)) && !Directory.Exists(Path.Combine(root, "bin")),
            "Preparation/construction executed consumer products: " + testCase);
        if (testCase is not ("stale" or "missing" or "producerless"))
        {
            Require(!Directory.Exists(future), "Preparation/construction created the future directory: " + testCase);
        }
        if (producesFile)
        {
            Require(!File.Exists(member), "Preparation/construction executed the reference copy: " + testCase);
        }
        if (!checkGraph)
        {
            return;
        }

        (string File, JsonElement Invocation)[] invocations = Invocations(root);
        JsonElement comparison = invocations.Single(entry =>
            entry.Invocation.GetProperty("taskType").GetString() == "Microsoft.DotNet.ApiCompat.Task.ValidateAssembliesTask").Invocation;
        JsonElement parameters = comparison.GetProperty("parameters");
        List<string> expected =
        [
            Path.Combine(root, "obj/Debug/net11.0/ApiCompatFixture.dll"),
            Path.GetFullPath(Path.Combine(root, "../contract/ApiCompatFixture.dll")),
            Path.Combine(root, "CompatibilitySuppressions.xml"),
            Path.Combine(root, "excluded-attributes.txt"),
            Path.Combine(roslyn, "Microsoft.CodeAnalysis.dll"),
            Path.Combine(roslyn, "Microsoft.CodeAnalysis.CSharp.dll"),
            .. prepared.Descendants("_ApiCompatPreparedReferenceFile")
                .Where(item => SamePath(item.Element("ReferenceDirectory")!.Value, framework))
                .Select(item => item.Attribute("Include")!.Value),
        ];
        if (producesFile)
        {
            expected.Add(member);
        }
        if (testCase == "precedence")
        {
            expected.Add(compileReference);
        }
        RequirePaths(Items(parameters, "DeclaredInputs"), expected,
            "Produced-reference footprint must contain exactly the selected files: " + testCase);
        string[] requiredProducers = parameters.GetProperty("DeclaredInputs").GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("metadata").TryGetProperty("RequireProducer", out JsonElement value) &&
                string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.GetProperty("itemSpec").GetString()!).ToArray();
        RequirePaths(requiredProducers, producesFile ? [member] : [],
            "Exactly the selected produced DLL must carry RequireProducer=true: " + testCase);
        RequirePaths(Items(parameters, "DeclaredOutputs"), [Semaphore(root)], "Unexpected ApiCompat outputs.");
        Require(Items(parameters, "DeclaredReferenceDirectories").Count(path => SamePath(path, future)) == 1,
            "Validation must receive the authored future directory exactly once.");
        string[] references = testCase == "precedence" ? [framework, future, compileReference] : [framework, future];
        foreach (string side in new[] { "LeftAssembliesReferences", "RightAssembliesReferences" })
        {
            string[] groups = Items(parameters, side);
            Require(groups.Length == 1 && groups[0].Split(',').Length == references.Length &&
                groups[0].Split(',').Zip(references).All(pair => SamePath(pair.First, pair.Second)),
                "Formatter changed reference-group ordering or directory/explicit-file requests: " + side);
        }

        (string File, JsonElement Invocation)[] copies = invocations.Where(entry =>
            entry.Invocation.GetProperty("taskType").GetString() == "Microsoft.Build.Tasks.CopyFiles" &&
            Items(entry.Invocation.GetProperty("parameters"), "DestinationFiles").Any(path => SamePath(path, member))).ToArray();
        Require(copies.Length == (producesFile ? 1 : 0), "Expected one exact producer, or none for an empty directory.");
        if (producesFile)
        {
            JsonElement copy = copies[0].Invocation.GetProperty("parameters");
            string source = Path.GetFullPath(Path.Combine(root, "../inputs/Dependency.dll"));
            RequirePaths(Items(copy, "SourceFiles"), [source], "Reference copy changed its source.");
            RequirePaths(Items(copy, "DeclaredInputs"), [source], "Reference copy must read only its seed.");
            RequirePaths(Items(copy, "DestinationFiles"), [member], "Reference copy changed its destination.");
            RequirePaths(Items(copy, "DeclaredOutputs"), [member], "Reference copy must produce the exact selected DLL.");
        }
    }

    private static void CheckProducedExecution(string root, string outputFile, string testCase, bool expectCompile)
    {
        (string File, JsonElement Invocation)[] invocations = Invocations(root);
        string output = File.ReadAllText(outputFile);
        string comparison = invocations.Single(entry =>
            entry.Invocation.GetProperty("taskType").GetString() == "Microsoft.DotNet.ApiCompat.Task.ValidateAssembliesTask").File;
        string compiler = invocations.Single(entry =>
            entry.Invocation.GetProperty("taskType").GetString() == "Microsoft.CodeAnalysis.BuildTasks.CompileCsc").File;
        int comparisonIndex = output.IndexOf(comparison, StringComparison.Ordinal);
        Require(comparisonIndex >= 0, "The existing graph did not execute ApiCompat: " + testCase);
        Require(output.Contains(compiler, StringComparison.Ordinal) == expectCompile,
            "Reference-only invalidation must not recompile the unchanged consumer.");
        if (testCase is "future" or "stale" or "precedence")
        {
            string member = Path.Combine(root, "future refs/Dependency.dll");
            string copy = invocations.Single(entry =>
                entry.Invocation.GetProperty("taskType").GetString() == "Microsoft.Build.Tasks.CopyFiles" &&
                Items(entry.Invocation.GetProperty("parameters"), "DestinationFiles").Any(path => SamePath(path, member))).File;
            int copyIndex = output.IndexOf(copy, StringComparison.Ordinal);
            Require(copyIndex >= 0 && copyIndex < comparisonIndex,
                "The exact DLL producer must execute before the API comparison: " + testCase);
            Require(File.ReadAllBytes(member).SequenceEqual(File.ReadAllBytes(
                Path.GetFullPath(Path.Combine(root, "../inputs/Dependency.dll")))),
                "The produced reference bytes differ from the declared copy source: " + testCase);
        }
        Require(File.Exists(Semaphore(root)) && File.ReadAllBytes(Semaphore(root)).Length == 0,
            "Successful produced-reference validation must publish an empty semaphore.");
        if (testCase == "empty")
        {
            Require(!Directory.Exists(Path.Combine(root, "future refs")),
                "An unused empty future directory must not be materialized.");
        }
    }

    private static (string File, JsonElement Invocation)[] Invocations(string root) =>
        Directory.GetFiles(Path.Combine(root, "build.ninja.actions"), "*.json").Select(file =>
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            return (file, document.RootElement.Clone());
        }).ToArray();

    private static string Semaphore(string root) =>
        Path.Combine(root, "obj/Debug/net11.0/Microsoft.DotNet.ApiCompat.ValidateAssemblies.Common.semaphore");

    private static void RequirePaths(IEnumerable<string> actual, IEnumerable<string> expected, string message)
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string> expectedPaths = new(expected.Select(Path.GetFullPath), comparer);
        HashSet<string> actualPaths = new(actual.Select(Path.GetFullPath), comparer);
        Require(expectedPaths.SetEquals(actualPaths), message + " Missing: " +
            string.Join(", ", expectedPaths.Except(actualPaths, comparer)) + "; unexpected: " +
            string.Join(", ", actualPaths.Except(expectedPaths, comparer)));
    }

    private static string[] Items(JsonElement parameters, string name) =>
        parameters.GetProperty(name).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("itemSpec").GetString()!).ToArray();

    private static string[] Diagnostics(string path)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string text = File.ReadAllText(path)
            .Replace(Path.Combine(root, "ordinary"), "<consumer>", StringComparison.Ordinal)
            .Replace(Path.Combine(root, "action"), "<consumer>", StringComparison.Ordinal);
        return Regex.Matches(text, @"\bCP\d{4}: [^\r\n]+")
            .Select(match => Regex.Replace(match.Value, @" \[[^\r\n]*\]$", ""))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
