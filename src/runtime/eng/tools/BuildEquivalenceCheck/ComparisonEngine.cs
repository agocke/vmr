// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace BuildEquivalenceCheck;

/// <summary>
/// Comparison results for a single matched pair (by source file or assembly name).
/// </summary>
public sealed class ComparisonResult
{
    public required string Name { get; init; }
    public required string Category { get; init; } // "native" or "managed"
    public bool IsMatch => Differences.Count == 0;
    public List<Difference> Differences { get; } = [];
}

public sealed class Difference
{
    public required string Field { get; init; }
    public required SortedSet<string> OnlyInCMake { get; init; }
    public required SortedSet<string> OnlyInBazel { get; init; }
    public string? CMakeValue { get; init; }
    public string? BazelValue { get; init; }
}

/// <summary>
/// Full report of the equivalence check.
/// </summary>
public sealed class EquivalenceReport
{
    public List<ComparisonResult> NativeResults { get; } = [];
    public List<ComparisonResult> ManagedResults { get; } = [];
    public List<string> OnlyInCMake { get; } = [];
    public List<string> OnlyInBazel { get; } = [];
    public List<string> OnlyInMSBuild { get; } = [];
    public List<string> OnlyInBazelManaged { get; } = [];

    public int TotalComparisons => NativeResults.Count + ManagedResults.Count;
    public int Matches => NativeResults.Count(r => r.IsMatch) + ManagedResults.Count(r => r.IsMatch);
    public int Mismatches => TotalComparisons - Matches;
    public bool IsEquivalent => Mismatches == 0
        && OnlyInCMake.Count == 0
        && OnlyInBazel.Count == 0
        && OnlyInMSBuild.Count == 0
        && OnlyInBazelManaged.Count == 0;
}

public static class ComparisonEngine
{
    // Flags injected by Bazel's cc_toolchain that CMake doesn't use.
    // Only include flags that are genuinely from the Bazel C++ toolchain
    // and have no CMake equivalent — NOT flags from .bazelrc that mirror CMake.
    private static readonly HashSet<string> BazelToolchainFlags =
    [
        "-U_FORTIFY_SOURCE",
        "-fstack-protector",
        "-Wthread-safety",
        "-Wself-assign",
        "-Wunused-but-set-parameter",
        "-Wno-free-nonheap-object",
        "-fcolor-diagnostics",
        "-fno-canonical-system-headers",
        "-no-canonical-prefixes",
        "-Wno-builtin-macro-redefined",
        "-fdata-sections",
        "-g0",
    ];

    // Flags injected by CMake that Bazel doesn't use.
    private static readonly HashSet<string> CMakeToolchainFlags =
    [
        "-Wno-null-conversion",
        "-glldb",
        "-Wvla",
    ];

    // Defines injected by Bazel toolchain
    private static readonly HashSet<string> BazelToolchainDefines =
    [
        "_FORTIFY_SOURCE=1",
        "__DATE__=\"redacted\"",
        "__TIMESTAMP__=\"redacted\"",
        "__TIME__=\"redacted\"",
    ];

    // CMake defines not relevant to Bazel (CMake configure-time detection only)
    private static readonly HashSet<string> CMakeToolchainDefines =
    [
        "COMPILER_SUPPORTS_W_RESERVED_IDENTIFIER",
    ];

    public static EquivalenceReport CompareNative(
        List<NativeCompilationRecord> cmakeRecords,
        List<NativeCompilationRecord> bazelRecords)
    {
        var report = new EquivalenceReport();

        var cmakeByFile = cmakeRecords
            .GroupBy(r => r.SourceFile)
            .ToDictionary(g => g.Key, g => g.First());
        var bazelByFile = bazelRecords
            .GroupBy(r => r.SourceFile)
            .ToDictionary(g => g.Key, g => g.First());

        var allFiles = cmakeByFile.Keys.Union(bazelByFile.Keys).Order().ToList();

        foreach (var file in allFiles)
        {
            var inCMake = cmakeByFile.TryGetValue(file, out var cmake);
            var inBazel = bazelByFile.TryGetValue(file, out var bazel);

            if (inCMake && !inBazel)
            {
                report.OnlyInCMake.Add(file);
                continue;
            }

            if (!inCMake && inBazel)
            {
                report.OnlyInBazel.Add(file);
                continue;
            }

            var result = CompareNativeRecords(file, cmake!, bazel!);
            report.NativeResults.Add(result);
        }

        return report;
    }

    public static void CompareManaged(
        EquivalenceReport report,
        List<ManagedCompilationRecord> msbuildRecords,
        List<ManagedCompilationRecord> bazelRecords)
    {
        var msbuildByName = msbuildRecords
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g => g.First());
        var bazelByName = bazelRecords
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g => g.First());

        var allNames = msbuildByName.Keys.Union(bazelByName.Keys).Order().ToList();

        foreach (var name in allNames)
        {
            var inMSBuild = msbuildByName.TryGetValue(name, out var msbuild);
            var inBazel = bazelByName.TryGetValue(name, out var bazel);

            if (inMSBuild && !inBazel)
            {
                report.OnlyInMSBuild.Add(name);
                continue;
            }

            if (!inMSBuild && inBazel)
            {
                report.OnlyInBazelManaged.Add(name);
                continue;
            }

            var result = CompareManagedRecords(name, msbuild!, bazel!);
            report.ManagedResults.Add(result);
        }
    }

    private static ComparisonResult CompareNativeRecords(string file, NativeCompilationRecord cmake, NativeCompilationRecord bazel)
    {
        var result = new ComparisonResult { Name = file, Category = "native" };

        // Compare defines (filter known toolchain noise)
        var cmakeDefines = new SortedSet<string>(cmake.Defines.Where(d => !CMakeToolchainDefines.Contains(d)), StringComparer.Ordinal);
        var bazelDefines = new SortedSet<string>(bazel.Defines.Where(d => !BazelToolchainDefines.Contains(d)), StringComparer.Ordinal);
        AddSetDifference(result, "defines", cmakeDefines, bazelDefines);

        // Compare undefines
        var cmakeUndefs = new SortedSet<string>(cmake.Undefines, StringComparer.Ordinal);
        var bazelUndefs = new SortedSet<string>(bazel.Undefines.Where(u => u != "_FORTIFY_SOURCE"), StringComparer.Ordinal);
        AddSetDifference(result, "undefines", cmakeUndefs, bazelUndefs);

        // Compare language standard
        if (cmake.LanguageStandard != bazel.LanguageStandard)
        {
            result.Differences.Add(new Difference
            {
                Field = "language_standard",
                OnlyInCMake = [],
                OnlyInBazel = [],
                CMakeValue = cmake.LanguageStandard,
                BazelValue = bazel.LanguageStandard,
            });
        }

        // Compare optimization level
        if (cmake.OptimizationLevel != bazel.OptimizationLevel)
        {
            result.Differences.Add(new Difference
            {
                Field = "optimization",
                OnlyInCMake = [],
                OnlyInBazel = [],
                CMakeValue = cmake.OptimizationLevel,
                BazelValue = bazel.OptimizationLevel,
            });
        }

        // Compare flags (filter toolchain noise from both sides)
        var cmakeFlags = new SortedSet<string>(cmake.Flags.Where(f => !CMakeToolchainFlags.Contains(f)), StringComparer.Ordinal);
        var bazelFlags = new SortedSet<string>(bazel.Flags.Where(f => !BazelToolchainFlags.Contains(f)), StringComparer.Ordinal);
        AddSetDifference(result, "flags", cmakeFlags, bazelFlags);

        return result;
    }

    private static ComparisonResult CompareManagedRecords(string name, ManagedCompilationRecord msbuild, ManagedCompilationRecord bazel)
    {
        var result = new ComparisonResult { Name = name, Category = "managed" };

        AddSourceFileDifference(result, msbuild, bazel);
        AddSetDifference(result, "defines", msbuild.Defines, bazel.Defines);
        AddSetDifference(result, "references", msbuild.References, bazel.References);
        AddSetDifference(result, "nowarn", msbuild.NoWarn, bazel.NoWarn);
        // Analyzers are intentionally not compared — Bazel does not wire Roslyn
        // analyzers yet, so the diff would always be MSBuild-only noise.

        var msbuildLang = NormalizeLangVersion(msbuild.LangVersion);
        var bazelLang = NormalizeLangVersion(bazel.LangVersion);
        if (!string.Equals(msbuildLang, bazelLang, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(msbuildLang)
            && !string.IsNullOrEmpty(bazelLang))
        {
            result.Differences.Add(new Difference
            {
                Field = "lang_version",
                OnlyInCMake = [],
                OnlyInBazel = [],
                CMakeValue = msbuild.LangVersion,
                BazelValue = bazel.LangVersion,
            });
        }

        if (!string.Equals(msbuild.TargetType, bazel.TargetType, StringComparison.OrdinalIgnoreCase))
        {
            result.Differences.Add(new Difference
            {
                Field = "target_type",
                OnlyInCMake = [],
                OnlyInBazel = [],
                CMakeValue = msbuild.TargetType,
                BazelValue = bazel.TargetType,
            });
        }

        return result;
    }

    /// <summary>
    /// Compare source file sets with special handling for generated files.
    /// Generated files may live in different directories across build systems
    /// (e.g. artifacts/obj/ vs bazel-out/). After exact-path matching,
    /// unmatched files are matched by filename. Filename-matched pairs are
    /// verified by content; mismatches are reported.
    /// </summary>
    private static void AddSourceFileDifference(
        ComparisonResult result,
        ManagedCompilationRecord msbuild,
        ManagedCompilationRecord bazel)
    {
        var onlyInMSBuild = new SortedSet<string>(msbuild.SourceFiles.Except(bazel.SourceFiles), StringComparer.Ordinal);
        var onlyInBazel = new SortedSet<string>(bazel.SourceFiles.Except(msbuild.SourceFiles), StringComparer.Ordinal);

        // Filter out SDK-generated AssemblyAttributes.cs files. These contain
        // only TargetFrameworkAttribute and are not meaningful for equivalence.
        onlyInMSBuild.RemoveWhere(f => Path.GetFileName(f).EndsWith("AssemblyAttributes.cs", StringComparison.Ordinal));
        onlyInBazel.RemoveWhere(f => Path.GetFileName(f).EndsWith("AssemblyAttributes.cs", StringComparison.Ordinal));

        if (onlyInMSBuild.Count == 0 && onlyInBazel.Count == 0)
            return;

        // Match remaining files by filename (ignoring directory).
        var msbuildByName = onlyInMSBuild
            .GroupBy(f => Path.GetFileName(f)!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var bazelByName = onlyInBazel
            .GroupBy(f => Path.GetFileName(f)!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var contentMismatches = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in msbuildByName.Keys.Intersect(bazelByName.Keys).ToList())
        {
            if (msbuildByName[key].Count == 1 && bazelByName[key].Count == 1)
            {
                var msbuildPath = msbuildByName[key][0];
                var bazelPath = bazelByName[key][0];

                // Verify content matches via original disk paths.
                if (!GeneratedContentMatches(msbuildPath, bazelPath, msbuild, bazel))
                {
                    contentMismatches.Add($"{key} (content differs: msbuild={msbuildPath}, bazel={bazelPath})");
                }

                onlyInMSBuild.Remove(msbuildPath);
                onlyInBazel.Remove(bazelPath);
            }
        }

        if (onlyInMSBuild.Count > 0 || onlyInBazel.Count > 0)
        {
            result.Differences.Add(new Difference
            {
                Field = "source_files",
                OnlyInCMake = onlyInMSBuild,
                OnlyInBazel = onlyInBazel,
            });
        }

        if (contentMismatches.Count > 0)
        {
            result.Differences.Add(new Difference
            {
                Field = "generated_file_content",
                OnlyInCMake = contentMismatches,
                OnlyInBazel = [],
            });
        }
    }

    /// <summary>
    /// Compare the content of two generated files using their original disk paths.
    /// Returns true if content matches or if either file cannot be read.
    /// </summary>
    private static bool GeneratedContentMatches(
        string msbuildNormalized,
        string bazelNormalized,
        ManagedCompilationRecord msbuild,
        ManagedCompilationRecord bazel)
    {
        if (!msbuild.SourceFileOriginalPaths.TryGetValue(msbuildNormalized, out var msbuildDisk)
            || !bazel.SourceFileOriginalPaths.TryGetValue(bazelNormalized, out var bazelDisk))
            return true; // Can't verify — assume match

        try
        {
            if (!File.Exists(msbuildDisk) || !File.Exists(bazelDisk))
                return true; // Can't verify — assume match

            // Normalize line endings — MSBuild may produce \r\n, Bazel produces \n.
            var msbuildContent = File.ReadAllText(msbuildDisk).ReplaceLineEndings("\n");
            var bazelContent = File.ReadAllText(bazelDisk).ReplaceLineEndings("\n");

            return msbuildContent == bazelContent;
        }
        catch
        {
            return true; // Can't verify — assume match
        }
    }

    private static void AddSetDifference(ComparisonResult result, string field, SortedSet<string> left, SortedSet<string> right)
    {
        var onlyInLeft = new SortedSet<string>(left.Except(right), StringComparer.Ordinal);
        var onlyInRight = new SortedSet<string>(right.Except(left), StringComparer.Ordinal);

        if (onlyInLeft.Count > 0 || onlyInRight.Count > 0)
        {
            result.Differences.Add(new Difference
            {
                Field = field,
                OnlyInCMake = onlyInLeft,
                OnlyInBazel = onlyInRight,
            });
        }
    }

    /// <summary>
    /// Normalize language version strings so that "preview" and the
    /// corresponding numeric version (e.g. "14.0") compare as equal.
    /// </summary>
    private static string NormalizeLangVersion(string version)
    {
        // MSBuild uses "preview" while rules_dotnet emits the numeric version.
        // Map "preview" to the latest known C# version so they compare equal.
        if (string.Equals(version, "preview", StringComparison.OrdinalIgnoreCase))
            return "14.0";

        return version;
    }
}
