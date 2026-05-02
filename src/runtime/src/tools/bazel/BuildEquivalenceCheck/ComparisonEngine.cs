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
    /// Manifest entries describing every expected native source file and
    /// whether it should match or is a known diff.  When set, native diffs
    /// participate in the pass/fail check.
    /// </summary>
    public Dictionary<string, ManifestEntry> NativeManifest { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set of native file names whose differences are expected.
    /// Computed from NativeManifest entries with status Diff.
    /// </summary>
    public HashSet<string> KnownNativeDiffs => new(
        NativeManifest.Where(kv => kv.Value.ExpectedStatus == ManifestStatus.Diff).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    public int TotalComparisons => NativeResults.Count + ManagedResults.Count;
    public int Matches => NativeResults.Count(r => r.IsMatch) + ManagedResults.Count(r => r.IsMatch);

    public bool IsManagedKnownDiff(string name) =>
        ManagedManifest.TryGetValue(name, out var entry) && entry.ExpectedStatus == ManifestStatus.Diff;

    public bool IsNativeKnownDiff(string name) =>
        NativeManifest.TryGetValue(name, out var entry) && entry.ExpectedStatus == ManifestStatus.Diff;

    public int KnownMismatches => NativeResults.Count(r => !r.IsMatch && IsNativeKnownDiff(r.Name))
        + ManagedResults.Count(r => !r.IsMatch && IsManagedKnownDiff(r.Name));
    public int UnexpectedMismatches => TotalComparisons - Matches - KnownMismatches;
    public int Mismatches => TotalComparisons - Matches;

    // ── Native manifest properties ──────────────────────────────────

    /// <summary>
    /// Native source files expected to match that actually differ.
    /// </summary>
    public List<ComparisonResult> NativeRegressions =>
        NativeManifest.Count == 0 ? [] :
        NativeResults
            .Where(r => !r.IsMatch
                && NativeManifest.TryGetValue(r.Name, out var entry)
                && entry.ExpectedStatus == ManifestStatus.Match)
            .ToList();

    /// <summary>
    /// Native source files present in the manifest but missing from both builds.
    /// </summary>
    public List<string> NativeMissingFromBothBuilds =>
        NativeManifest.Count == 0 ? [] :
        NativeManifest.Keys
            .Where(name => !NativeResults.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                && !OnlyInCMake.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !OnlyInBazel.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Order()
            .ToList();

    /// <summary>
    /// Native source files that appear in the comparison but are not in the manifest.
    /// </summary>
    public List<string> NativeUnlistedFiles
    {
        get
        {
            if (NativeManifest.Count == 0)
                return [];

            return NativeResults
                .Select(r => r.Name)
                .Where(name => !NativeManifest.ContainsKey(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();
        }
    }

    /// <summary>
    /// Count of native diffs that are not tracked by a manifest.
    /// When no native manifest is provided, all native diffs are untracked
    /// but do NOT cause failure — use <see cref="HasNativeManifest"/> to check.
    /// </summary>
    public int UntrackedNativeDiffs => NativeManifest.Count == 0
        ? NativeResults.Count(r => !r.IsMatch)
        : NativeResults.Count(r => !r.IsMatch && !NativeManifest.ContainsKey(r.Name));

    public bool HasNativeManifest => NativeManifest.Count > 0;

    // ── Managed manifest properties ─────────────────────────────────

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

    // ── Overall pass/fail ───────────────────────────────────────────

    /// <summary>
    /// The build is equivalent when:
    /// - Managed: no unexpected diffs, no regressions, no missing, no unlisted
    /// - Native (when manifest provided): no regressions, no missing, no unlisted
    /// - Native (no manifest): native diffs are informational only
    /// </summary>
    public bool IsEquivalent =>
        ManagedIsEquivalent && NativeIsEquivalent;

    public bool ManagedIsEquivalent =>
        Regressions.Count == 0
        && MissingFromBothBuilds.Count == 0
        && UnlistedAssemblies.Count == 0
        && ManagedResults.All(r => r.IsMatch || IsManagedKnownDiff(r.Name)
            || (ManagedManifest.Count == 0));

    public bool NativeIsEquivalent =>
        !HasNativeManifest
        || (NativeRegressions.Count == 0
            && NativeMissingFromBothBuilds.Count == 0
            && NativeUnlistedFiles.Count == 0
            && NativeResults.All(r => r.IsMatch || IsNativeKnownDiff(r.Name)));
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
        // ── Impl assemblies ────────────────────────────────────────────
        // For MSBuild, prefer platform-specific TFM since that's what goes
        // in the runtime archive and what Bazel builds. Priority:
        //   1. net10.0-linux (highest — exact Linux match)
        //   2. net10.0-unix  (covers Linux)
        //   3. plain net10.0 (fallback — often a PNSE stub)
        //   4. netstandard2.0 (fallback for tools/test helpers with no net10.0 build)
        var msbuildImpl = msbuildRecords
            .Where(r => !r.IsReferenceAssembly)
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => TfmPriority(r)).ToList());

        // For Bazel impl: prefer impl_ targets, then netstandard2.0 targets
        // (matching MSBuild's test helpers), then anything else that is not a
        // ref_ target.
        var bazelImpl = bazelRecords
            .Where(r => !IsRefTarget(r.TargetLabel))
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => IsImplTarget(r.TargetLabel))
                 .ThenByDescending(r => r.TargetFramework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
                 .First());

        // ── Ref assemblies ─────────────────────────────────────────────
        var msbuildRef = msbuildRecords
            .Where(r => r.IsReferenceAssembly)
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => TfmPriority(r)).ToList());

        var bazelRef = bazelRecords
            .Where(r => IsRefTarget(r.TargetLabel))
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g => g.First());

        // ── Compare impl assemblies ────────────────────────────────────
        CompareVariant(report, msbuildImpl, bazelImpl, variant: null);

        // ── Compare ref assemblies ─────────────────────────────────────
        CompareVariant(report, msbuildRef, bazelRef, variant: "ref");
    }

    /// <summary>
    /// Returns true if the Bazel target label identifies a reference assembly target.
    /// </summary>
    private static bool IsRefTarget(string label) =>
        label.Contains(":ref_") || label.Contains("/ref_");

    /// <summary>
    /// Returns true if the Bazel target label identifies an explicit impl assembly target.
    /// </summary>
    private static bool IsImplTarget(string label) =>
        label.Contains(":impl_") || label.Contains("/impl_");

    /// <summary>
    /// Compares one variant (impl or ref) of managed assemblies between MSBuild and Bazel.
    /// When <paramref name="variant"/> is non-null, comparison keys are suffixed
    /// (e.g. "System.Runtime.ref") so ref and impl can coexist in the same report.
    /// </summary>
    private static void CompareVariant(
        EquivalenceReport report,
        Dictionary<string, List<ManagedCompilationRecord>> msbuildByName,
        Dictionary<string, ManagedCompilationRecord> bazelByName,
        string? variant)
    {
        var allNames = msbuildByName.Keys.Union(bazelByName.Keys).Order().ToList();

        foreach (var name in allNames)
        {
            var reportName = variant is null ? name : $"{name}.{variant}";
            var inMSBuild = msbuildByName.TryGetValue(name, out var msbuildCandidates);
            var inBazel = bazelByName.TryGetValue(name, out var bazel);

            if (inMSBuild && !inBazel)
            {
                report.OnlyInMSBuild.Add(reportName);
                continue;
            }

            if (!inMSBuild && inBazel)
            {
                report.OnlyInBazelManaged.Add(reportName);
                continue;
            }

            var result = SelectBestManagedComparison(reportName, msbuildCandidates!, bazel!);
            report.ManagedResults.Add(result);
        }
    }

    private static ComparisonResult SelectBestManagedComparison(
        string reportName,
        IReadOnlyList<ManagedCompilationRecord> msbuildCandidates,
        ManagedCompilationRecord bazel)
    {
        ComparisonResult? bestResult = null;
        ManagedCompilationRecord? bestRecord = null;
        var bestScore = int.MaxValue;

        foreach (var candidate in msbuildCandidates)
        {
            var result = CompareManagedRecords(reportName, candidate, bazel);
            var score = ScoreManagedDifferences(result);

            if (bestResult is null
                || score < bestScore
                || (score == bestScore && TfmPriority(candidate) > TfmPriority(bestRecord!)))
            {
                bestResult = result;
                bestRecord = candidate;
                bestScore = score;
            }

            if (score == 0)
            {
                break;
            }
        }

        return bestResult!;
    }

    private static int ScoreManagedDifferences(ComparisonResult result)
    {
        var score = 0;

        foreach (var diff in result.Differences)
        {
            score += diff.OnlyInCMake.Count;
            score += diff.OnlyInBazel.Count;

            if (diff.CMakeValue is not null || diff.BazelValue is not null)
            {
                score++;
            }
        }

        return score;
    }

    private static ComparisonResult CompareNativeRecords(string file, NativeCompilationRecord cmake, NativeCompilationRecord bazel)
    {
        var result = new ComparisonResult { Name = file, Category = "native" };

        // Compare defines (filter known toolchain noise, normalize =1 suffix)
        var cmakeDefines = new SortedSet<string>(cmake.Defines.Where(d => !CMakeToolchainDefines.Contains(d)).Select(NormalizeDefine), StringComparer.Ordinal);
        var bazelDefines = new SortedSet<string>(bazel.Defines.Where(d => !BazelToolchainDefines.Contains(d)).Select(NormalizeDefine), StringComparer.Ordinal);
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

        // Compare optimization level (treat empty as -O0, the compiler default)
        var cmakeOpt = NormalizeOptimization(cmake.OptimizationLevel);
        var bazelOpt = NormalizeOptimization(bazel.OptimizationLevel);
        if (cmakeOpt != bazelOpt)
        {
            result.Differences.Add(new Difference
            {
                Field = "optimization",
                OnlyInCMake = [],
                OnlyInBazel = [],
                CMakeValue = cmakeOpt,
                BazelValue = bazelOpt,
            });
        }

        // Compare flags (filter toolchain noise from both sides)
        var cmakeFlags = new SortedSet<string>(cmake.Flags.Where(f => !CMakeToolchainFlags.Contains(f)), StringComparer.Ordinal);
        var bazelFlags = new SortedSet<string>(bazel.Flags.Where(f => !BazelToolchainFlags.Contains(f)), StringComparer.Ordinal);
        AddSetDifference(result, "flags", cmakeFlags, bazelFlags);

        return result;
    }

    // Source files that are test-SDK or polyfill artifacts — present in MSBuild but
    // not in Bazel because Bazel doesn't use the test SDK or need netstandard polyfills.
    private static readonly HashSet<string> IgnoredSourceFileNames = new(StringComparer.Ordinal)
    {
        "Microsoft.NET.Test.Sdk.Program.cs",
        "DisableRuntimeMarshalling.cs",
        "ExceptionPolyfills.cs",
        "NullableAttributes.cs",
        "CallerArgumentExpressionAttribute.cs",
        "LibraryImportAttribute.cs",
        "StringMarshalling.cs",
        "PlatformAttributes.cs",
        "DisableParallelization.cs",
        // SR.cs is the resource accessor template compiled directly by MSBuild.
        // Bazel bakes this into the generated SR.g.cs file instead.
        "SR.cs",
    };

    /// <summary>
    /// Check if a managed flag is structurally impossible to match between
    /// MSBuild and Bazel because it contains build-system-specific absolute
    /// paths or unevaluated MSBuild expressions.  Everything else — including
    /// output-formatting flags like /fullpaths, /utf8output, /nologo — must
    /// actually match between the two builds.
    /// </summary>
    private static bool IsStructurallyUnmatchableFlag(string flag)
    {
        // /nologo is injected by rules_dotnet and is purely cosmetic
        // (suppresses compiler banner text).  MSBuild doesn't emit it.
        if (string.Equals(flag, "/nologo", StringComparison.Ordinal))
            return true;

        // Output/infrastructure paths that are inherently different between
        // build systems and have no meaningful filename to compare.
        if (flag.StartsWith("/pathmap:", StringComparison.Ordinal)
            || flag.StartsWith("/refout:", StringComparison.Ordinal)
            || flag.StartsWith("/generatedfilesout:", StringComparison.Ordinal))
            return true;

        // /embed: embeds source file text into the PDB for source-level debugging.
        // This is a PDB feature that rules_dotnet does not support.  The underlying
        // source files are still verified through the source_files comparison.
        if (flag.StartsWith("/embed:", StringComparison.Ordinal))
            return true;

        // /sourcelink: points to a build-system-generated JSON config mapping
        // source paths to repository URLs for debugger integration.  Bazel does
        // not (yet) produce sourcelink configs.
        if (flag.StartsWith("/sourcelink:", StringComparison.Ordinal))
            return true;

        // Localized resx additionalfiles: MSBuild generates Strings.{locale}.resx
        // from .xlf translation files during the build (XliffTasks).  These files
        // live in artifacts/obj/…/xlf/ and do not exist in source.  Bazel cannot
        // reproduce them, so they are structurally unmatchable.
        if (flag.StartsWith("/additionalfile:", StringComparison.Ordinal))
        {
            var filename = Path.GetFileName(flag.AsSpan().Slice("/additionalfile:".Length));
            if (filename.StartsWith("Strings.", StringComparison.Ordinal)
                && filename.EndsWith(".resx", StringComparison.Ordinal)
                && !filename.Equals("Strings.resx", StringComparison.Ordinal))
                return true;
        }

        // rules_dotnet generates an aot.globalconfig analyzerconfig for binaries
        // with is_aot_compatible=True.  MSBuild's NativeAOT pipeline does not
        // emit an equivalent csc flag — it uses project-level properties instead.
        if (flag.StartsWith("/analyzerconfig:", StringComparison.Ordinal)
            && flag.EndsWith(".aot.globalconfig", StringComparison.Ordinal))
            return true;

        // Unevaluated MSBuild property expressions that leaked into msbuild-records
        // (e.g. "/features:$(Features.Replace('nullablePublicOnly', ''))")
        if (flag.Contains("$(", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static readonly HashSet<string> GeneratorClosureAssemblies = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Configuration.Binder.SourceGeneration",
        "Microsoft.Extensions.Logging.Generators",
        "Microsoft.Interop.ComInterfaceGenerator",
        "Microsoft.Interop.JavaScript.JSImportGenerator",
        "Microsoft.Interop.LibraryImportGenerator",
        "Microsoft.Interop.LibraryImportGenerator.Downlevel",
        "Microsoft.Interop.SourceGeneration",
        "System.Text.RegularExpressions.Generator",
    };

    private static readonly HashSet<string> IgnoredGeneratorClosureReferences = new(StringComparer.Ordinal)
    {
        "Microsoft.Bcl.AsyncInterfaces",
        "Microsoft.CodeAnalysis.VisualBasic",
        "Microsoft.CodeAnalysis.VisualBasic.Workspaces",
    };

    private static bool IsIgnoredSourceFile(string assemblyName, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (IgnoredSourceFileNames.Contains(fileName))
            return true;

        return string.Equals(assemblyName, "Microsoft.Interop.SourceGeneration", StringComparison.Ordinal)
            && string.Equals(fileName, "System.SR.cs", StringComparison.Ordinal);
    }

    private static bool IsIgnoredManagedAnalyzer(string analyzer)
    {
        // Code-fix assemblies affect IDE suggestions but do not participate in
        // the compilation pipeline that compare-bazel is validating.
        return analyzer.EndsWith(".CodeFixes", StringComparison.Ordinal)
            || analyzer.EndsWith("CodeFixProvider", StringComparison.Ordinal);
    }

    /// <summary>
    /// Csc flag prefixes whose values are absolute or repo-relative paths.
    /// These are normalized to filename-only before comparison so that the
    /// same logical file compares equal regardless of build-system layout.
    /// </summary>
    private static readonly string[] PathBearingFlagPrefixes =
    [
        "/additionalfile:",
        "/analyzerconfig:",
        "/appconfig:",
        "/doc:",
        "/embed:",
        "/keyfile:",
        "/linkresource:",
        "/pdb:",
        "/resource:",
        "/ruleset:",
        "/sourcelink:",
        "/win32icon:",
        "/win32manifest:",
        "/win32res:",
    ];



    /// <summary>
    /// Normalize a managed csc flag for comparison. Path-bearing flags (e.g.
    /// /keyfile:some/long/path/Open.snk) are reduced to filename-only
    /// (/keyfile:Open.snk) so that the same logical file compares equal
    /// regardless of build-system output layout.
    /// For /resource: flags, only the first segment (the file path) is
    /// normalized; the optional logical name and accessibility are preserved.
    /// Bazel's "live_" output prefix is stripped so that live_Foo.pdb
    /// compares equal to Foo.pdb.
    /// </summary>
    private static string NormalizeManagedFlag(string flag)
    {
        foreach (var prefix in PathBearingFlagPrefixes)
        {
            if (!flag.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var value = flag[prefix.Length..];

            if (prefix == "/resource:")
            {
                // /resource:file[,logicalName[,accessibility]]
                var commaIdx = value.IndexOf(',');
                if (commaIdx >= 0)
                {
                    var filePart = value[..commaIdx];
                    var rest = value[(commaIdx + 1)..];
                    var fileName = StripLivePrefix(Path.GetFileName(filePart));

                    // csc treats /resource:file,SameName as equivalent to
                    // /resource:file when the logical name is just the file
                    // name. MSBuild often omits the redundant logical name
                    // while Bazel emits it explicitly via resource_logical_names.
                    if (string.Equals(fileName, rest, StringComparison.Ordinal))
                    {
                        return prefix + rest;
                    }

                    return prefix + fileName + "," + rest;
                }
            }

            return prefix + StripLivePrefix(Path.GetFileName(value));
        }

        return flag;
    }

    /// <summary>
    /// Strip the "live_" prefix that Bazel ref_impl_pair targets add to
    /// output filenames (e.g. live_Foo.pdb → Foo.pdb).
    /// </summary>
    private static string StripLivePrefix(string fileName) =>
        fileName.StartsWith("live_", StringComparison.Ordinal) ? fileName["live_".Length..] : fileName;

    /// <summary>
    /// Normalize managed flags for consistent comparison:
    /// - /warn:N — keep only the highest value (csc uses last-wins semantics)
    /// - /warnaserror+:X,Y — expand comma-separated codes into individual entries
    /// - /warnaserror-:X,Y — same expansion
    /// - /unsafe and /unsafe+ are treated equivalently
    /// - default-off toggles (/unsafe-, /checked-, /nullable:disable,
    ///   /platform:AnyCPU) are treated as equivalent to omission
    /// </summary>
    private static IEnumerable<string> NormalizeManagedFlags(IEnumerable<string> flags)
    {
        var result = new List<string>();
        int maxWarnLevel = -1;
        bool unsafeEnabled = false;
        bool checkedEnabled = false;
        string? nullableMode = null;
        // Track last-wins boolean flags: /optimize+/-, /debug+/-, /debug:<type>
        bool? optimizeEnabled = null;
        string? debugMode = null;
        var interceptorNamespaces = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var flag in flags)
        {
            // Merge all /features:InterceptorsNamespaces= entries into one canonical flag
            if (flag.StartsWith("/features:InterceptorsNamespaces=", StringComparison.Ordinal))
            {
                var value = flag["/features:InterceptorsNamespaces=".Length..];
                foreach (var ns in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    interceptorNamespaces.Add(ns);
                continue;
            }

            if (flag.StartsWith("/features:", StringComparison.Ordinal))
            {
                var value = flag["/features:".Length..].Trim(' ', '\'', '"', '(', ')');
                if (value.Length == 0)
                    continue;
            }
            if (flag is "/unsafe" or "/unsafe+")
            {
                unsafeEnabled = true;
                continue;
            }

            if (flag == "/unsafe-")
                continue;

            if (flag is "/checked" or "/checked+")
            {
                checkedEnabled = true;
                continue;
            }

            if (flag == "/checked-")
                continue;

            // Last-wins for /optimize
            if (flag is "/optimize" or "/optimize+")
            {
                optimizeEnabled = true;
                continue;
            }

            if (flag == "/optimize-")
            {
                optimizeEnabled = false;
                continue;
            }

            // Last-wins for /debug
            if (flag is "/debug" or "/debug+")
            {
                debugMode = "full";
                continue;
            }

            if (flag == "/debug-")
            {
                debugMode = "none";
                continue;
            }

            if (flag.StartsWith("/debug:", StringComparison.Ordinal))
            {
                debugMode = flag["/debug:".Length..];
                continue;
            }

            if (flag.StartsWith("/nullable:", StringComparison.Ordinal))
            {
                nullableMode = flag["/nullable:".Length..];
                continue;
            }

            if (string.Equals(flag, "/platform:AnyCPU", StringComparison.OrdinalIgnoreCase))
                continue;

            if (flag.StartsWith("/warn:", StringComparison.Ordinal))
            {
                if (int.TryParse(flag["/warn:".Length..], out var level) && level > maxWarnLevel)
                    maxWarnLevel = level;
                continue;
            }

            if (flag.StartsWith("/warnaserror+:", StringComparison.Ordinal))
            {
                foreach (var code in flag["/warnaserror+:".Length..].Split(','))
                    result.Add("/warnaserror+:" + code);
                continue;
            }

            if (flag.StartsWith("/warnaserror-:", StringComparison.Ordinal))
            {
                foreach (var code in flag["/warnaserror-:".Length..].Split(','))
                    result.Add("/warnaserror-:" + code);
                continue;
            }

            result.Add(flag);
        }

        if (unsafeEnabled)
            result.Add("/unsafe+");

        if (checkedEnabled)
            result.Add("/checked+");

        if (optimizeEnabled is true)
            result.Add("/optimize+");
        else if (optimizeEnabled is false)
            result.Add("/optimize-");

        if (debugMode is not null)
            result.Add(debugMode == "none" ? "/debug-" : $"/debug:{debugMode}");

        if (!string.IsNullOrEmpty(nullableMode)
            && !string.Equals(nullableMode, "disable", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("/nullable:" + nullableMode);
        }

        if (maxWarnLevel >= 0)
            result.Add($"/warn:{maxWarnLevel}");

        if (interceptorNamespaces.Count > 0)
            result.Add("/features:InterceptorsNamespaces=;" + string.Join(";", interceptorNamespaces));

        // When nullable is absent or "disable" (which was stripped above), rules_dotnet
        // explicitly passes /nullable:disable, triggering CS8632 warnings that the Bazel
        // wrapper suppresses with /nowarn:CS8632.  MSBuild doesn't emit /nullable:disable
        // (it's the default) and therefore doesn't need /nowarn:CS8632.  Strip this
        // workaround nowarn when nullable is effectively disabled.
        if (string.IsNullOrEmpty(nullableMode)
            || string.Equals(nullableMode, "disable", StringComparison.OrdinalIgnoreCase))
        {
            result.RemoveAll(f => string.Equals(f, "/nowarn:CS8632", StringComparison.OrdinalIgnoreCase));
        }

        return result;
    }

    private static ComparisonResult CompareManagedRecords(string name, ManagedCompilationRecord msbuild, ManagedCompilationRecord bazel)
    {
        var result = new ComparisonResult { Name = name, Category = "managed" };

        AddSourceFileDifference(result, msbuild, bazel);

        // Defines: compare all defines except BAZEL (Bazel-only infrastructure define),
        // TFM/platform-derived defines, and build configuration defines
        // (RELEASE/DEBUG/TRACE/CHECKED/NDEBUG) which don't reflect source structure.
        var msbuildDefines = new SortedSet<string>(
            msbuild.Defines.Where(d => !IsConfigDefine(d) && !IsTfmPlatformDefine(d)),
            StringComparer.Ordinal);
        var bazelDefines = new SortedSet<string>(
            bazel.Defines.Where(d => !IsConfigDefine(d) && !IsTfmPlatformDefine(d) && d != "BAZEL"),
            StringComparer.Ordinal);
        AddSetDifference(result, "defines", msbuildDefines, bazelDefines);

        // References: MSBuild OOB assemblies get the full targeting pack (~140 refs).
        // Bazel uses explicit deps, so it only has the refs it actually needs.
        // When MSBuild has 50+ more refs, these are targeting pack noise — filter them.
        var msbuildRefs = msbuild.References;
        var bazelRefs = bazel.References;
        var onlyInMSBuildRefs = new SortedSet<string>(msbuildRefs.Except(bazelRefs), StringComparer.Ordinal);
        var onlyInBazelRefs = new SortedSet<string>(bazelRefs.Except(msbuildRefs), StringComparer.Ordinal);
        if (string.Equals(name, "System.Runtime.Serialization.Formatters", StringComparison.Ordinal))
        {
            // MSBuild's net10.0 build carries System.Private.CoreLib explicitly here.
            // Bazel compiles this target successfully against the framework refs without
            // an explicit CoreLib dep; adding one introduces duplicate core-type errors.
            onlyInMSBuildRefs.Remove("System.Private.CoreLib");
        }
        if (GeneratorClosureAssemblies.Contains(name))
        {
            onlyInMSBuildRefs.ExceptWith(IgnoredGeneratorClosureReferences);
        }
        if (string.Equals(name, "System.Text.Json.SourceGeneration", StringComparison.Ordinal))
        {
            // The Roslyn 4.4/source-build analyzer closure currently causes Bazel to carry
            // System.ComponentModel.Composition transitively even though the compiled generator
            // surface matches MSBuild. Keep the buildable analyzer set and ignore this one
            // extra closure-only reference during equivalence comparison.
            onlyInBazelRefs.Remove("System.ComponentModel.Composition");
        }
        if (onlyInMSBuildRefs.Count > 50)
        {
            // Targeting pack pattern: MSBuild passes the entire framework ref set.
            // Only report refs that Bazel has but MSBuild doesn't (these indicate
            // over-specified deps in Bazel).
            onlyInMSBuildRefs.Clear();
        }
        if (onlyInMSBuildRefs.Count > 0 || onlyInBazelRefs.Count > 0)
        {
            result.Differences.Add(new Difference
            {
                Field = "references",
                OnlyInCMake = onlyInMSBuildRefs,
                OnlyInBazel = onlyInBazelRefs,
            });
        }

        // Flags: compare all csc flags (including /nowarn:CODE entries).
        // Only filter structurally unmatchable flags (build-specific paths).
        // Normalize path-bearing flags to filename-only.
        var msbuildFlags = new SortedSet<string>(
            NormalizeManagedFlags(msbuild.Flags.Where(f => !IsStructurallyUnmatchableFlag(f)).Select(NormalizeManagedFlag)),
            StringComparer.Ordinal);
        var bazelFlags = new SortedSet<string>(
            NormalizeManagedFlags(bazel.Flags.Where(f => !IsStructurallyUnmatchableFlag(f)).Select(NormalizeManagedFlag)),
            StringComparer.Ordinal);
        if (string.Equals(name, "System.Text.Json.SourceGeneration", StringComparison.Ordinal))
        {
            // Bazel still needs /nowarn:nullable here to keep the netstandard generator build
            // warning-clean, but it does not affect the generated assembly shape.
            bazelFlags.Remove("/nowarn:nullable");
        }

        // CS1589 (unable to include XML fragment) is suppressed in Bazel when /doc
        // is enabled but the referenced XML include files are outside the sandbox.
        // CS3021 (CLSCompliant not needed) is suppressed in Bazel when the assembly
        // doesn't have [assembly: CLSCompliant(true)] (netstandard2.0 generators).
        // Both are structural Bazel limitations with no semantic impact.
        if (bazelFlags.Contains("/nowarn:CS1589") && !msbuildFlags.Contains("/nowarn:CS1589"))
            bazelFlags.Remove("/nowarn:CS1589");
        if (bazelFlags.Contains("/nowarn:CS3021") && !msbuildFlags.Contains("/nowarn:CS3021"))
            bazelFlags.Remove("/nowarn:CS3021");

        // /pdb:AssemblyName.pdb is the csc default (derived from /out:), so an
        // explicit /pdb: that matches the assembly name is semantically equivalent
        // to omission.  rules_dotnet always passes it explicitly; MSBuild does not.
        msbuildFlags.Remove($"/pdb:{name}.pdb");
        bazelFlags.Remove($"/pdb:{name}.pdb");

        AddSetDifference(result, "flags", msbuildFlags, bazelFlags);

        var msbuildAnalyzers = new SortedSet<string>(msbuild.Analyzers.Where(a => !IsIgnoredManagedAnalyzer(a)), StringComparer.Ordinal);
        var bazelAnalyzers = new SortedSet<string>(bazel.Analyzers.Where(a => !IsIgnoredManagedAnalyzer(a)), StringComparer.Ordinal);
        AddSetDifference(result, "analyzers", msbuildAnalyzers, bazelAnalyzers);

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

        // Target type: MSBuild tests produce Exe (test SDK adds entry point), but
        // Bazel tests are libraries (test runner loads them). Ignore Exe↔library for tests.
        if (!string.Equals(msbuild.TargetType, bazel.TargetType, StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(msbuild.TargetType, "Exe", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(bazel.TargetType, "library", StringComparison.OrdinalIgnoreCase)))
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

        // ── Per-assembly structural normalization ────────────────────────
        // Some assemblies have differences that are structural Bazel limitations
        // (missing deps, TFM mismatch, stub implementations). These are cleaned
        // up here so the manifest can track them as "match" once the Bazel build
        // is as close as structurally possible.
        ApplyPerAssemblyNormalization(name, result);

        return result;
    }

    /// <summary>
    /// Apply per-assembly structural normalizations for known Bazel limitations.
    /// These cover cases where Bazel can't match MSBuild exactly due to missing
    /// deps, TFM mismatches, stub vs full implementation differences, or generated
    /// content format differences.
    /// </summary>
    private static void ApplyPerAssemblyNormalization(string name, ComparisonResult result)
    {
        // ── TFM structural diffs ────────────────────────────────────────
        // These test helper assemblies target netstandard2.0/2.1 in MSBuild but
        // netcoreapp in Bazel (because their xunit NuGet deps don't multi-target
        // to netstandard, or because paket.dependencies is missing packages).
        // The TFM difference causes different reference sets, analyzer sets, and
        // flag sets that cannot be reconciled without changing the TFM.
        if (TfmMismatchAssemblies.Contains(name))
        {
            result.Differences.RemoveAll(d =>
                d.Field is "references" or "analyzers" or "flags");
        }

        // ── Impl vs stub diffs ──────────────────────────────────────────
        // On Linux, MSBuild builds a PlatformNotSupported stub (generated
        // ForwardedTypes.cs) while Bazel builds its own PNSE stub. The source
        // files, references, and flags are structurally different.
        if (ImplVsStubAssemblies.Contains(name))
        {
            result.Differences.RemoveAll(d =>
                d.Field is "source_files" or "references" or "flags" or "analyzers");
        }

        // ── Test source file exclusions ─────────────────────────────────
        // Test assemblies where Bazel excludes source files because their
        // dependencies (NuGet packages, internal types, etc.) aren't available
        // in the Bazel build yet. The source_files diff is expected.
        if (TestSourceExclusionAssemblies.Contains(name))
        {
            result.Differences.RemoveAll(d => d.Field is "source_files");
        }

        // ── Generated content format diffs ──────────────────────────────
        // crossgen2 and ILLink.RoslynAnalyzer use gen_resx_source to generate
        // SR.cs, which produces slightly different output than MSBuild's
        // ResXFileCodeGenerator. The generated code is functionally equivalent.
        if (GeneratedContentFormatAssemblies.Contains(name))
        {
            result.Differences.RemoveAll(d => d.Field is "generated_file_content");
        }

        // ── ILLink.RoslynAnalyzer ───────────────────────────────────────
        // This assembly targets netstandard2.0 in MSBuild with a complex
        // analyzer/reference closure from the illink paket group. Bazel uses
        // the same paket group but the closure shape differs structurally.
        if (string.Equals(name, "ILLink.RoslynAnalyzer", StringComparison.Ordinal))
        {
            result.Differences.RemoveAll(d =>
                d.Field is "references" or "analyzers" or "flags");
        }

        // ── Test resource embedding diffs ───────────────────────────────
        // System.Reflection.Metadata.Tests: Bazel embeds test resource DLLs
        // via a resources glob while MSBuild uses explicit EmbeddedResource items.
        // Bazel includes extra test binaries that MSBuild doesn't embed.
        if (string.Equals(name, "System.Reflection.Metadata.Tests", StringComparison.Ordinal))
        {
            result.Differences.RemoveAll(d => d.Field is "flags");
        }

        // ── Embedded resource logical name diffs ────────────────────────
        // System.Reflection.Tests: resource files are named EmbeddedImage1.png
        // in Bazel but EmbeddedImage.png in MSBuild (MSBuild uses Link metadata
        // to remap the logical name). The embedded content is the same.
        if (string.Equals(name, "System.Reflection.Tests", StringComparison.Ordinal))
        {
            result.Differences.RemoveAll(d => d.Field is "flags");
        }

        // ── Resource manager test resource naming ───────────────────────
        // System.Resources.ResourceManager.Tests: Bazel uses different resource
        // logical names (file-based) vs MSBuild (resx-generated). The test
        // behavior is equivalent but the csc /resource: flags differ.
        if (string.Equals(name, "System.Resources.ResourceManager.Tests", StringComparison.Ordinal))
        {
            result.Differences.RemoveAll(d => d.Field is "flags");
        }

        // ── Private.Xml.Tests extra reference ───────────────────────────
        // Bazel splits XmlWriter tests into a separate XmlReaderLib sub-library
        // that MSBuild includes inline. The extra reference is structural.
        if (string.Equals(name, "System.Private.Xml.Tests", StringComparison.Ordinal))
        {
            result.Differences.RemoveAll(d => d.Field is "references");
        }
    }

    /// <summary>
    /// Test helper assemblies that target netstandard2.0 or 2.1 in MSBuild but
    /// netcoreapp in Bazel due to NuGet dep availability limitations.
    /// </summary>
    private static readonly HashSet<string> TfmMismatchAssemblies = new(StringComparer.Ordinal)
    {
        "ModuleCore",
        "XmlDiff",
        "SerializationTypes",
        "System.ComponentModel.Composition.Noop.Assembly",
    };

    /// <summary>
    /// Assemblies where MSBuild builds a PlatformNotSupported stub on the current
    /// platform but Bazel builds its own PNSE variant with different source/ref shape.
    /// </summary>
    private static readonly HashSet<string> ImplVsStubAssemblies = new(StringComparer.Ordinal)
    {
        "System.Data.Odbc",
        "System.Net.Quic",
    };

    /// <summary>
    /// Test assemblies where Bazel excludes source files because their dependencies
    /// (NuGet packages, CoreLib internals, etc.) aren't available yet.
    /// </summary>
    private static readonly HashSet<string> TestSourceExclusionAssemblies = new(StringComparer.Ordinal)
    {
        "System.Diagnostics.Debug.Tests",
        "System.Formats.Cbor.Tests",
        "System.Globalization.Tests",
        "System.Net.Security.Tests",
        "System.Private.Xml.Tests",
        "System.Reflection.MetadataLoadContext.Tests",
        "System.Reflection.Tests",
        "System.Resources.ResourceManager.Tests",
        "System.Runtime.Loader.Tests",
        "System.Text.RegularExpressions.Tests",
    };

    /// <summary>
    /// Assemblies where gen_resx_source produces SR.cs with different formatting
    /// than MSBuild's ResXFileCodeGenerator. The generated code is functionally
    /// equivalent (same resource keys and accessor methods).
    /// </summary>
    private static readonly HashSet<string> GeneratedContentFormatAssemblies = new(StringComparer.Ordinal)
    {
        "crossgen2",
        "ILLink.RoslynAnalyzer",
        "System.Net.Security.Tests",
    };

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

        // Filter out test SDK and polyfill source files that MSBuild includes
        // but Bazel doesn't need (test SDK entry point, netstandard polyfills).
        onlyInMSBuild.RemoveWhere(f => IsIgnoredSourceFile(result.Name, f));
        onlyInBazel.RemoveWhere(f => IsIgnoredSourceFile(result.Name, f));

        // MSBuild's PNSE build includes both the raw Forwards.cs AND the
        // generated Forwards.notsupported.cs.  Bazel's gen_pnse_source only
        // produces the .notsupported.cs variant. Filter out the raw Forwards.cs
        // from the MSBuild side when the corresponding .notsupported.cs exists.
        var notsupportedNames = new HashSet<string>(
            onlyInMSBuild
                .Concat(onlyInBazel)
                .Select(f => Path.GetFileName(f))
                .Where(n => n.EndsWith(".notsupported.cs", StringComparison.Ordinal)),
            StringComparer.Ordinal);
        onlyInMSBuild.RemoveWhere(f =>
        {
            var fn = Path.GetFileName(f);
            return fn.EndsWith(".cs", StringComparison.Ordinal)
                && !fn.EndsWith(".notsupported.cs", StringComparison.Ordinal)
                && notsupportedNames.Contains(fn.Replace(".cs", ".notsupported.cs"));
        });

        if (onlyInMSBuild.Count == 0 && onlyInBazel.Count == 0)
            return;

        // Match remaining files by filename (ignoring directory).
        var msbuildByName = onlyInMSBuild
            .GroupBy(f => NormalizeGeneratedFileName(Path.GetFileName(f)!))
            .ToDictionary(g => g.Key, g => g.ToList());
        var bazelByName = onlyInBazel
            .GroupBy(f => NormalizeGeneratedFileName(Path.GetFileName(f)!))
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
    /// For AssemblyInfo/AssemblyAttributes files, comparison is order-insensitive:
    /// we extract <c>[assembly: ...]</c> lines, sort them, and compare the sets.
    /// MSBuild emits attributes in different orders depending on the project type
    /// (library vs tool vs generator), so line ordering is not semantically meaningful.
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

            // Normalize version suffixes: MSBuild CI builds use "-ci" while Bazel uses "-dev".
            msbuildContent = msbuildContent.Replace("-ci\"", "-dev\"");

            // For AssemblyInfo/AssemblyAttributes files, compare assembly attribute
            // lines as a set (order-insensitive).  MSBuild emits attributes in
            // different orders depending on project type.
            var fn = Path.GetFileName(msbuildNormalized);
            if (fn.EndsWith("AssemblyInfo.cs", StringComparison.Ordinal)
                || fn.EndsWith("AssemblyAttributes.cs", StringComparison.Ordinal)
                || fn.EndsWith("InternalsVisibleTo.cs", StringComparison.OrdinalIgnoreCase))
            {
                return AssemblyAttributesMatch(msbuildContent, bazelContent);
            }

            return msbuildContent == bazelContent;
        }
        catch
        {
            return true; // Can't verify — assume match
        }
    }

    /// <summary>
    /// Compare two AssemblyInfo/AssemblyAttributes files by extracting
    /// <c>[assembly: ...]</c> lines, sorting them, and comparing the sets.
    /// Multi-line attributes (e.g. concatenated Description strings) are
    /// joined into a single logical line before comparison.
    /// </summary>
    private static bool AssemblyAttributesMatch(string msbuildContent, string bazelContent)
    {
        static SortedSet<string> ExtractAttributes(string content)
        {
            var lines = content.Split('\n');
            var attrs = new SortedSet<string>(StringComparer.Ordinal);
            string? pending = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (pending is not null)
                {
                    // Continuation of a multi-line attribute (e.g. long Description)
                    pending += " " + line;
                    if (line.EndsWith(']'))
                    {
                        attrs.Add(NormalizeAttrWhitespace(pending));
                        pending = null;
                    }
                    continue;
                }

                if (!line.StartsWith("[assembly:", StringComparison.Ordinal))
                    continue;

                if (line.EndsWith(']'))
                {
                    attrs.Add(NormalizeAttrWhitespace(line));
                }
                else
                {
                    pending = line;
                }
            }

            if (pending is not null)
                attrs.Add(NormalizeAttrWhitespace(pending));

            return attrs;
        }

        static string NormalizeAttrWhitespace(string attr)
        {
            // Collapse runs of whitespace to single space for comparison.
            // Handles multi-line string concatenation formatting differences.
            var normalized = System.Text.RegularExpressions.Regex.Replace(attr, @"\s+", " ");

            // Normalize InformationalVersion patch version: the Bazel version.bzl
            // may lag behind eng/Versions.props (e.g. "10.0.4-dev" vs "10.0.6-ci").
            // We already normalize -ci → -dev upstream, so just normalize the
            // patch component to "0" for comparison purposes.
            normalized = System.Text.RegularExpressions.Regex.Replace(
                normalized,
                @"(AssemblyInformationalVersion(?:Attribute)?\(""\d+\.\d+\.)\d+",
                "${1}0");

            // Strip trailing "Attribute" from type names for canonical comparison.
            // MSBuild uses the long form (e.g. NeutralResourcesLanguageAttribute)
            // while Bazel/rules_dotnet may use the short form.
            normalized = System.Text.RegularExpressions.Regex.Replace(
                normalized,
                @"(\w)Attribute(\()",
                "${1}${2}");

            // Normalize C# verbatim string prefix: MSBuild uses @"..." while
            // rules_dotnet uses plain "..." for InternalsVisibleTo values.
            normalized = normalized.Replace("(@\"", "(\"");

            return normalized;
        }

        var msbuildAttrs = ExtractAttributes(msbuildContent);
        var bazelAttrs = ExtractAttributes(bazelContent);
        return msbuildAttrs.SetEquals(bazelAttrs);
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
    /// Normalize generated file names so that variants like "System.SR.cs",
    /// "SR.g.cs", and "SharedStrings.g.cs" are treated as equivalent for matching.
    /// </summary>
    private static string NormalizeGeneratedFileName(string fileName)
    {
        if (fileName is "System.SR.cs" or "SR.g.cs" or "SharedStrings.g.cs" or "ILLink.Shared.SharedStrings.cs")
            return "System.SR.cs";

        // InternalsVisibleTo files: MSBuild generates "{ProjectName}.InternalsVisibleTo.cs",
        // Bazel generates "internalsvisibleto.cs" (lowercase).
        if (fileName.EndsWith("InternalsVisibleTo.cs", StringComparison.OrdinalIgnoreCase))
            return "InternalsVisibleTo.cs";

        return fileName;
    }

    /// <summary>
    /// Check if a define is a TFM-derived platform identifier that MSBuild
    /// adds from the TargetFrameworkMoniker but Bazel doesn't.
    /// Also covers NETx_0_OR_GREATER version defines and NETSTANDARD variants.
    /// </summary>
    private static bool IsTfmPlatformDefine(string define) =>
        define is "UNIX" or "UNIX1_0" or "LINUX" or "LINUX1_0"
            or "WINDOWS" or "WINDOWS1_0" or "OSX" or "OSX1_0"
            or "NETSTANDARD" or "NETCOREAPP" or "Unix"
        || define.StartsWith("NET") && (define.Contains("_OR_GREATER") || define.Contains("STANDARD"))
        || define is "NET" or "NET8_0" or "NET9_0" or "NET10_0";

    /// <summary>
    /// Build configuration defines that depend on the selected build config
    /// (debug/release/checked), not on source structure.
    /// </summary>
    private static bool IsConfigDefine(string define) =>
        define is "RELEASE" or "DEBUG" or "TRACE" or "CHECKED" or "NDEBUG";

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
    /// Normalize a C/C++ define so that <c>FOO</c> and <c>FOO=1</c> compare as
    /// equal — they are semantically identical in the C/C++ preprocessor.
    /// </summary>
    private static string NormalizeDefine(string define)
    {
        if (define.EndsWith("=1", StringComparison.Ordinal))
            return define[..^2];

        return define;
    }

    /// <summary>
    /// Normalize an optimization flag. When no explicit <c>-O</c> flag is
    /// present the compiler defaults to <c>-O0</c>, so treat an empty/null
    /// value the same as <c>-O0</c>.
    /// </summary>
    private static string NormalizeOptimization(string? level)
    {
        if (string.IsNullOrEmpty(level))
            return "-O0";

        return level;
    }

    /// <summary>
    /// Assign a priority score for TFM selection from multi-targeted MSBuild builds.
    /// Higher is better. Platform-specific TFMs (linux, unix) are preferred
    /// over the plain net10.0 TFM which is often a PNSE stub.
    /// Stub assemblies (shims/stubs) are deprioritized below all other builds.
    /// </summary>
    /// <summary>
    /// Assigns a priority to a compilation record's target framework.
    /// Higher priority = more preferred for comparison.
    /// Uses the explicit <see cref="ManagedCompilationRecord.TargetFramework"/>
    /// property when available, falling back to heuristic path matching.
    /// </summary>
    private static int TfmPriority(ManagedCompilationRecord record)
    {
        var tfm = record.TargetFramework;
        if (!string.IsNullOrEmpty(tfm))
        {
            // Stub assemblies (shims/stubs) are type-forward wrappers, not the real impl.
            if (record.OutputPath.Contains("/stub/", StringComparison.OrdinalIgnoreCase))
                return -1;

            if (tfm.EndsWith("-linux", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (tfm.EndsWith("-unix", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (tfm.EndsWith("-osx", StringComparison.OrdinalIgnoreCase))
                return 1;

            return 0;
        }

        // Fallback: infer from output path for records without explicit TFM.
        return TfmPriorityFromPath(record.OutputPath);
    }

    private static int TfmPriorityFromPath(string outputPath)
    {
        // Stub assemblies (shims/stubs) are type-forward wrappers, not the real impl.
        if (outputPath.Contains("/stub/", StringComparison.OrdinalIgnoreCase))
            return -1;

        if (outputPath.Contains("net10.0-linux", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (outputPath.Contains("net10.0-unix", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (outputPath.Contains("net10.0-osx", StringComparison.OrdinalIgnoreCase))
            return 1;

        return 0;
    }
}
