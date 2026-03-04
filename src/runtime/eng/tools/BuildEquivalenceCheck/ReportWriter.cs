// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildEquivalenceCheck;

public static class ReportWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void WriteConsoleReport(EquivalenceReport report, bool verbose)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("  Build Equivalence Check: Bazel vs CMake/MSBuild");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        // Native summary
        if (report.NativeResults.Count > 0 || report.OnlyInCMake.Count > 0 || report.OnlyInBazel.Count > 0)
        {
            Console.WriteLine("── Native C/C++ ──────────────────────────────────────");
            var nativeMatches = report.NativeResults.Count(r => r.IsMatch);
            var nativeMismatches = report.NativeResults.Count(r => !r.IsMatch);
            Console.WriteLine($"  Compared: {report.NativeResults.Count} source files");
            WriteColored($"  Matched:  {nativeMatches}", ConsoleColor.Green);
            if (nativeMismatches > 0)
                WriteColored($"  Differ:   {nativeMismatches}", ConsoleColor.Red);
            if (report.OnlyInCMake.Count > 0)
                WriteColored($"  Only in CMake: {report.OnlyInCMake.Count}", ConsoleColor.Yellow);
            if (report.OnlyInBazel.Count > 0)
                WriteColored($"  Only in Bazel: {report.OnlyInBazel.Count}", ConsoleColor.Yellow);
            Console.WriteLine();

            if (verbose || nativeMismatches > 0)
            {
                foreach (var result in report.NativeResults.Where(r => !r.IsMatch))
                {
                    WriteColored($"  ✗ {result.Name}", ConsoleColor.Red);
                    foreach (var diff in result.Differences)
                        WriteDifference(diff);
                }

                if (report.OnlyInCMake.Count > 0 && verbose)
                {
                    Console.WriteLine("  Files only in CMake:");
                    foreach (var f in report.OnlyInCMake.Take(20))
                        Console.WriteLine($"    - {f}");
                    if (report.OnlyInCMake.Count > 20)
                        Console.WriteLine($"    ... and {report.OnlyInCMake.Count - 20} more");
                }

                if (report.OnlyInBazel.Count > 0 && verbose)
                {
                    Console.WriteLine("  Files only in Bazel:");
                    foreach (var f in report.OnlyInBazel.Take(20))
                        Console.WriteLine($"    - {f}");
                    if (report.OnlyInBazel.Count > 20)
                        Console.WriteLine($"    ... and {report.OnlyInBazel.Count - 20} more");
                }
            }
        }

        // Managed summary
        if (report.ManagedResults.Count > 0 || report.OnlyInMSBuild.Count > 0 || report.OnlyInBazelManaged.Count > 0)
        {
            Console.WriteLine("── Managed C# ────────────────────────────────────────");
            var managedMatches = report.ManagedResults.Count(r => r.IsMatch);
            var managedMismatches = report.ManagedResults.Count(r => !r.IsMatch);
            Console.WriteLine($"  Compared: {report.ManagedResults.Count} assemblies");
            WriteColored($"  Matched:  {managedMatches}", ConsoleColor.Green);
            if (managedMismatches > 0)
                WriteColored($"  Differ:   {managedMismatches}", ConsoleColor.Red);
            if (report.OnlyInMSBuild.Count > 0)
                WriteColored($"  Only in MSBuild: {report.OnlyInMSBuild.Count}", ConsoleColor.Yellow);
            if (report.OnlyInBazelManaged.Count > 0)
                WriteColored($"  Only in Bazel:   {report.OnlyInBazelManaged.Count}", ConsoleColor.Yellow);
            Console.WriteLine();

            if (verbose || managedMismatches > 0)
            {
                foreach (var result in report.ManagedResults.Where(r => !r.IsMatch))
                {
                    WriteColored($"  ✗ {result.Name}", ConsoleColor.Red);
                    foreach (var diff in result.Differences)
                        WriteDifference(diff);
                }

                if (report.OnlyInMSBuild.Count > 0 && verbose)
                {
                    Console.WriteLine("  Assemblies only in MSBuild:");
                    foreach (var a in report.OnlyInMSBuild.Take(20))
                        Console.WriteLine($"    - {a}");
                    if (report.OnlyInMSBuild.Count > 20)
                        Console.WriteLine($"    ... and {report.OnlyInMSBuild.Count - 20} more");
                }

                if (report.OnlyInBazelManaged.Count > 0 && verbose)
                {
                    Console.WriteLine("  Assemblies only in Bazel:");
                    foreach (var a in report.OnlyInBazelManaged.Take(20))
                        Console.WriteLine($"    - {a}");
                    if (report.OnlyInBazelManaged.Count > 20)
                        Console.WriteLine($"    ... and {report.OnlyInBazelManaged.Count - 20} more");
                }
            }
        }

        // Overall
        Console.WriteLine("── Overall ───────────────────────────────────────────");
        Console.WriteLine($"  Total comparisons: {report.TotalComparisons}");
        Console.WriteLine($"  Matches:           {report.Matches}");
        Console.WriteLine($"  Mismatches:        {report.Mismatches}");
        Console.WriteLine();

        if (report.IsEquivalent)
            WriteColored("  PASS: Build inputs are equivalent.", ConsoleColor.Green);
        else
            WriteColored("  FAIL: Build inputs differ.", ConsoleColor.Red);

        Console.WriteLine();
    }

    public static void WriteJsonReport(EquivalenceReport report, string outputPath)
    {
        var jsonReport = new JsonReport
        {
            IsEquivalent = report.IsEquivalent,
            Summary = new JsonSummary
            {
                TotalComparisons = report.TotalComparisons,
                Matches = report.Matches,
                Mismatches = report.Mismatches,
                NativeOnlyInCMake = report.OnlyInCMake.Count,
                NativeOnlyInBazel = report.OnlyInBazel.Count,
                ManagedOnlyInMSBuild = report.OnlyInMSBuild.Count,
                ManagedOnlyInBazel = report.OnlyInBazelManaged.Count,
            },
            NativeDifferences = report.NativeResults
                .Where(r => !r.IsMatch)
                .Select(ToJsonDiff)
                .ToList(),
            ManagedDifferences = report.ManagedResults
                .Where(r => !r.IsMatch)
                .Select(ToJsonDiff)
                .ToList(),
            OnlyInCMake = report.OnlyInCMake,
            OnlyInBazel = report.OnlyInBazel,
            OnlyInMSBuild = report.OnlyInMSBuild,
            OnlyInBazelManaged = report.OnlyInBazelManaged,
        };

        var json = JsonSerializer.Serialize(jsonReport, s_jsonOptions);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"  JSON report written to: {outputPath}");
    }

    private static JsonDiff ToJsonDiff(ComparisonResult result) => new()
    {
        Name = result.Name,
        Category = result.Category,
        Fields = result.Differences.Select(d => new JsonFieldDiff
        {
            Field = d.Field,
            OnlyInCMake = d.OnlyInCMake.Count > 0 ? d.OnlyInCMake : null,
            OnlyInBazel = d.OnlyInBazel.Count > 0 ? d.OnlyInBazel : null,
            CMakeValue = d.CMakeValue,
            BazelValue = d.BazelValue,
        }).ToList(),
    };

    private static void WriteDifference(Difference diff)
    {
        if (diff.CMakeValue is not null || diff.BazelValue is not null)
        {
            Console.WriteLine($"      {diff.Field}: cmake={diff.CMakeValue ?? "(none)"} bazel={diff.BazelValue ?? "(none)"}");
            return;
        }

        if (diff.OnlyInCMake.Count > 0)
        {
            var items = string.Join(", ", diff.OnlyInCMake.Take(5));
            var more = diff.OnlyInCMake.Count > 5 ? $" (+{diff.OnlyInCMake.Count - 5} more)" : "";
            Console.WriteLine($"      {diff.Field} only in cmake: {items}{more}");
        }

        if (diff.OnlyInBazel.Count > 0)
        {
            var items = string.Join(", ", diff.OnlyInBazel.Take(5));
            var more = diff.OnlyInBazel.Count > 5 ? $" (+{diff.OnlyInBazel.Count - 5} more)" : "";
            Console.WriteLine($"      {diff.Field} only in bazel: {items}{more}");
        }
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }
}

// JSON report data model
internal sealed class JsonReport
{
    public bool IsEquivalent { get; init; }
    public required JsonSummary Summary { get; init; }
    public required List<JsonDiff> NativeDifferences { get; init; }
    public required List<JsonDiff> ManagedDifferences { get; init; }
    public required List<string> OnlyInCMake { get; init; }
    public required List<string> OnlyInBazel { get; init; }
    public required List<string> OnlyInMSBuild { get; init; }
    public required List<string> OnlyInBazelManaged { get; init; }
}

internal sealed class JsonSummary
{
    public int TotalComparisons { get; init; }
    public int Matches { get; init; }
    public int Mismatches { get; init; }
    public int NativeOnlyInCMake { get; init; }
    public int NativeOnlyInBazel { get; init; }
    public int ManagedOnlyInMSBuild { get; init; }
    public int ManagedOnlyInBazel { get; init; }
}

internal sealed class JsonDiff
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required List<JsonFieldDiff> Fields { get; init; }
}

internal sealed class JsonFieldDiff
{
    public required string Field { get; init; }
    public SortedSet<string>? OnlyInCMake { get; init; }
    public SortedSet<string>? OnlyInBazel { get; init; }
    public string? CMakeValue { get; init; }
    public string? BazelValue { get; init; }
}
