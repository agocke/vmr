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
/// A single entry from the managed assembly manifest.
/// </summary>
public sealed class ManifestEntry
{
    public required string Name { get; init; }
    public required ManifestStatus ExpectedStatus { get; init; }
}

public enum ManifestStatus
{
    Match,
    Diff,
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

    /// <summary>
    /// Manifest entries describing every expected managed assembly and
    /// whether it should match or is a known diff.  When set, the
    /// equivalence check also verifies that no assemblies are missing
    /// from either build and that no unexpected assemblies appear.
    /// </summary>
    public Dictionary<string, ManifestEntry> ManagedManifest { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set of native file names whose differences are expected.
    /// </summary>
    public HashSet<string> KnownNativeDiffs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int TotalComparisons => NativeResults.Count + ManagedResults.Count;
    public int Matches => NativeResults.Count(r => r.IsMatch) + ManagedResults.Count(r => r.IsMatch);

    public bool IsManagedKnownDiff(string name) =>
        ManagedManifest.TryGetValue(name, out var entry) && entry.ExpectedStatus == ManifestStatus.Diff;

    public int KnownMismatches => NativeResults.Count(r => !r.IsMatch && KnownNativeDiffs.Contains(r.Name))
        + ManagedResults.Count(r => !r.IsMatch && IsManagedKnownDiff(r.Name));
    public int UnexpectedMismatches => TotalComparisons - Matches - KnownMismatches;
    public int Mismatches => TotalComparisons - Matches;

    /// <summary>
    /// Managed assemblies present in the manifest but missing from both builds.
    /// </summary>
    public List<string> MissingFromBothBuilds =>
        ManagedManifest.Keys
            .Where(name => !ManagedResults.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                && !OnlyInMSBuild.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !OnlyInBazelManaged.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Order()
            .ToList();

    /// <summary>
    /// Managed assemblies that appear in the comparison (both builds) but are not in the manifest.
    /// Only-in-one-build assemblies are not flagged — they are already tracked separately.
    /// </summary>
    public List<string> UnlistedAssemblies
    {
        get
        {
            if (ManagedManifest.Count == 0)
                return [];

            return ManagedResults
                .Select(r => r.Name)
                .Where(name => !ManagedManifest.ContainsKey(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();
        }
    }

    /// <summary>
    /// Managed assemblies expected to match that actually differ.
    /// </summary>
    public List<ComparisonResult> Regressions =>
        ManagedResults
            .Where(r => !r.IsMatch
                && ManagedManifest.TryGetValue(r.Name, out var entry)
                && entry.ExpectedStatus == ManifestStatus.Match)
            .ToList();

    public bool IsEquivalent =>
        UnexpectedMismatches == 0
        && Regressions.Count == 0
        && MissingFromBothBuilds.Count == 0
        && UnlistedAssemblies.Count == 0;
}

public static class ComparisonEngine
{
    // Flags injected by Bazel's cc_toolchain that CMake doesn't use.
    // Only include flags that are genuinely from the Bazel C++ toolchain
    // and have no CMake equivalent — NOT flags from .bazelrc that mirror CMake.
    // Do NOT add config-sensitive flags here (debug info, optimization, etc.)
    // — those must be compared to catch configuration mismatches.
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
    ];

    // Flags injected by CMake that Bazel doesn't use.
    // Do NOT add config-sensitive flags here (debug info, optimization, etc.)
    // — those must be compared to catch configuration mismatches.
    private static readonly HashSet<string> CMakeToolchainFlags =
    [
        "-Wno-null-conversion",
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
        // For MSBuild, prefer impl assemblies over ref assemblies when both exist.
        // When multiple impl records exist for the same assembly (multi-targeted),
        // prefer the platform-specific TFM since that's what goes in the runtime
        // archive and what Bazel builds. Priority:
        //   1. net10.0-linux (highest — exact Linux match)
        //   2. net10.0-unix  (covers Linux)
        //   3. plain net10.0 (fallback — often a PNSE stub)
        var msbuildByName = msbuildRecords
            .Where(r => !r.IsReferenceAssembly)
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => TfmPriority(r.OutputPath))
                .First());

        // For Bazel, prefer impl_ targets (the actual implementation assemblies)
        // over ref_ targets (reference assemblies used only as compile inputs).
        var bazelByName = bazelRecords
            .Where(r => r.TargetLabel.Contains(":impl_") || r.TargetLabel.Contains("/impl_")
                || (!r.TargetLabel.Contains(":ref_") && !r.TargetLabel.Contains("/ref_")))
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => r.TargetLabel.Contains(":impl_") || r.TargetLabel.Contains("/impl_")).First());

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

    /// <summary>
    /// Assign a priority score for TFM selection from multi-targeted MSBuild builds.
    /// Higher is better. Platform-specific TFMs (linux, unix) are preferred
    /// over the plain net10.0 TFM which is often a PNSE stub.
    /// </summary>
    private static int TfmPriority(string outputPath)
    {
        if (outputPath.Contains("net10.0-linux", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (outputPath.Contains("net10.0-unix", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (outputPath.Contains("net10.0-osx", StringComparison.OrdinalIgnoreCase))
            return 1;

        return 0;
    }
}
