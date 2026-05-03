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
    Ignore,
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

    public bool IsManagedIgnored(string name) =>
        ManagedManifest.TryGetValue(name, out var entry) && entry.ExpectedStatus == ManifestStatus.Ignore;

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
    public bool HasManagedManifest => ManagedManifest.Count > 0;

    private IEnumerable<string> TrackedManagedManifestKeys =>
        ManagedManifest
            .Where(kv => kv.Value.ExpectedStatus != ManifestStatus.Ignore)
            .Select(kv => kv.Key);

    // ── Managed manifest properties ─────────────────────────────────

    /// <summary>
    /// Managed assemblies present in the manifest but missing from both builds.
    /// </summary>
    public List<string> MissingFromBothBuilds =>
        ManagedManifest.Count == 0 ? [] :
        TrackedManagedManifestKeys
            .Where(name => !ManagedResults.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                && !OnlyInMSBuild.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !OnlyInBazelManaged.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Order()
            .ToList();

    /// <summary>
    /// Managed assemblies listed in the manifest that are missing from MSBuild.
    /// These appear only in the Bazel-side managed graph.
    /// </summary>
    public List<string> MissingFromMSBuild =>
        ManagedManifest.Count == 0 ? [] :
        OnlyInBazelManaged
            .Where(name => ManagedManifest.TryGetValue(name, out var entry) && entry.ExpectedStatus != ManifestStatus.Ignore)
            .Order()
            .ToList();

    /// <summary>
    /// Managed assemblies listed in the manifest that are missing from Bazel.
    /// These appear only in the MSBuild-side managed graph.
    /// </summary>
    public List<string> MissingFromBazel =>
        ManagedManifest.Count == 0 ? [] :
        OnlyInMSBuild
            .Where(name => ManagedManifest.TryGetValue(name, out var entry) && entry.ExpectedStatus != ManifestStatus.Ignore)
            .Order()
            .ToList();

    /// <summary>
    /// Managed assemblies that appear anywhere in the comparison but are not in the manifest.
    /// This includes matched pairs as well as assemblies that appear in only one build.
    /// </summary>
    public List<string> UnlistedAssemblies
    {
        get
        {
            if (ManagedManifest.Count == 0)
                return [];

            return ManagedResults
                .Select(r => r.Name)
                .Concat(OnlyInMSBuild)
                .Concat(OnlyInBazelManaged)
                .Where(name => !ManagedManifest.TryGetValue(name, out var entry) || entry.ExpectedStatus != ManifestStatus.Ignore)
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
        && MissingFromMSBuild.Count == 0
        && MissingFromBazel.Count == 0
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
        // For MSBuild, prefer the highest-TFM platform-specific build since
        // that's what goes in the runtime archive and what Bazel builds.
        var msbuildImpl = msbuildRecords
            .Where(r => !r.IsReferenceAssembly)
            // Exclude netstandard2.0 builds — Bazel targets netcoreapp.
            // When MSBuild multi-targets, the netstandard build is for NuGet
            // packaging, not the runtime archive.  Comparing it against the
            // Bazel build produces meaningless diffs.
            .Where(r => !r.OutputPath.Contains("netstandard2.0", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => TfmPriority(r.OutputPath))
                .First());

        // For Bazel impl: prefer impl_ targets, then live_ targets, then
        // anything that is not a ref_ target. Within the same target kind,
        // prefer the highest-TFM platform-specific build.
        var bazelImpl = bazelRecords
            .Where(r => !IsRefTarget(r.TargetLabel))
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => IsImplTarget(r.TargetLabel))
                    .ThenByDescending(r => TfmPriority(r.OutputPath))
                    .First());

        // ── Ref assemblies ─────────────────────────────────────────────
        var msbuildRef = msbuildRecords
            .Where(r => r.IsReferenceAssembly)
            .Where(r => !r.OutputPath.Contains("netstandard2.0", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => TfmPriority(r.OutputPath))
                .First());

        var bazelRef = bazelRecords
            .Where(r => IsRefTarget(r.TargetLabel))
            .GroupBy(r => r.AssemblyName)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(r => TfmPriority(r.OutputPath)).First());

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
        Dictionary<string, ManagedCompilationRecord> msbuildByName,
        Dictionary<string, ManagedCompilationRecord> bazelByName,
        string? variant)
    {
        var allNames = msbuildByName.Keys.Union(bazelByName.Keys).Order().ToList();

        foreach (var name in allNames)
        {
            var reportName = variant is null ? name : $"{name}.{variant}";

            if (report.IsManagedIgnored(reportName))
                continue;

            var inMSBuild = msbuildByName.TryGetValue(name, out var msbuild);
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

            var result = CompareManagedRecords(reportName, msbuild!, bazel!);
            report.ManagedResults.Add(result);
        }
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

    // Csc flags that are pure output-formatting boilerplate — always emitted by one
    // system but not the other, with no semantic impact on compilation.
    private static readonly HashSet<string> IgnoredCscFlags = new(StringComparer.Ordinal)
    {
        // MSBuild output formatting defaults not emitted by Bazel
        "/fullpaths",
        "/utf8output",
        // Bazel output formatting default not emitted by MSBuild
        "/nologo",
        // rules_dotnet always emits /optimize+ in release mode; MSBuild omits
        // the flag (optimizer is on by default).  Not a semantic difference.
        "/optimize+",
        // rules_dotnet explicitly passes /nullable:disable (MSBuild omits it,
        // since disable is the default).  That triggers CS8632 on nullable
        // annotations in shared source files, so csharp_library suppresses it.
        // MSBuild doesn't need the suppression.  Not a semantic difference.
        "/nowarn:CS8632",
        // IDE style warnings that fire on the older ancestor SDK (10.0.100-rc.1)
        // but not on the newer release SDK.  Bazel suppresses them to avoid
        // errors; MSBuild doesn't need to because the SDK doesn't raise them.
        "/nowarn:IDE0031",
        "/nowarn:IDE0060",
        "/nowarn:IDE0100",
        // CP0003 is a packaging compatibility warning suppressed by the Bazel
        // branch.  Not raised by MSBuild on this repo because the Arcade SDK
        // handles it differently.  Not a semantic difference.
        "/nowarn:CP0003",
    };

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
        "/analyzerconfig:",
        "/additionalfile:",
        "/doc:",
        "/keyfile:",
        "/resource:",
        "/ruleset:",
    ];

    /// <summary>
    /// Check if a managed flag should be ignored entirely in comparisons.
    /// Only filters pure output-formatting boilerplate and build infrastructure
    /// flags that don't affect compiled assembly semantics.
    /// </summary>
    private static bool IsIgnoredManagedFlag(string flag)
    {
        if (IgnoredCscFlags.Contains(flag))
            return true;

        // MSBuild toolchain defaults with values
        if (flag.StartsWith("/errorreport:", StringComparison.Ordinal)
            || flag.StartsWith("/filealign:", StringComparison.Ordinal))
            return true;

        // Debug symbol format — both build systems produce debug info, but the
        // specific flags (/debug-, /debug:portable, /debug:pdbonly) are configuration
        // choices that don't affect source equivalence.
        if (flag.StartsWith("/debug", StringComparison.Ordinal))
            return true;

        // Strong naming signing mechanism — /publicsign, /delaysign are
        // toolchain config differences
        if (flag.StartsWith("/publicsign", StringComparison.Ordinal)
            || flag.StartsWith("/delaysign", StringComparison.Ordinal))
            return true;

        // InterceptorsNamespaces — the ancestor SDK (rc.1) doesn't set this
        // via FrameworkReferenceResolution.targets, but the Bazel build carries
        // it from the release/10.0 branch.  No semantic impact when no
        // interceptors are actually used.
        if (flag.StartsWith("/features:InterceptorsNamespaces=", StringComparison.Ordinal))
            return true;

        // Build infrastructure flags that have no Bazel equivalent and don't
        // affect the compiled assembly semantics:
        // /pathmap — deterministic build path remapping
        // /sourcelink — PDB source link JSON
        // /skipanalyzers — build-time optimization (don't run analyzers)
        // /pdb — PDB output path
        // /refout — ref assembly output path
        // /embed — embed source file in PDB
        // /generatedfilesout — generated files output directory
        if (flag.StartsWith("/pathmap:", StringComparison.Ordinal)
            || flag.StartsWith("/sourcelink:", StringComparison.Ordinal)
            || flag.StartsWith("/skipanalyzers", StringComparison.Ordinal)
            || flag.StartsWith("/pdb:", StringComparison.Ordinal)
            || flag.StartsWith("/refout:", StringComparison.Ordinal)
            || flag.StartsWith("/embed:", StringComparison.Ordinal)
            || flag.StartsWith("/generatedfilesout:", StringComparison.Ordinal))
            return true;

        // Analysis-level globalconfig — MSBuild's SDK injects these implicitly
        // via the AnalysisLevel property; Bazel must pass the equivalent file
        // explicitly as /analyzerconfig:analysislevel_NN_default.globalconfig.
        // Both deliver the same diagnostics configuration, just via different
        // mechanisms, so this flag is build-infrastructure noise.
        // The raw flag contains a full path, so we check the filename.
        if (flag.StartsWith("/analyzerconfig:", StringComparison.Ordinal))
        {
            var configPath = flag["/analyzerconfig:".Length..];
            var fileName = Path.GetFileName(configPath);
            if (fileName.StartsWith("analysislevel", StringComparison.Ordinal)
                && fileName.EndsWith("_default.globalconfig", StringComparison.Ordinal))
                return true;
        }

        // Unevaluated MSBuild property expressions that leaked into msbuild-records
        // (e.g. "/features:$(Features.Replace('nullablePublicOnly', ''))")
        if (flag.Contains("$(", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Normalize a managed csc flag for comparison. Path-bearing flags (e.g.
    /// /keyfile:some/long/path/Open.snk) are reduced to filename-only
    /// (/keyfile:Open.snk) so that the same logical file compares equal
    /// regardless of build-system output layout.
    /// For /resource: flags, only the first segment (the file path) is
    /// normalized; the optional logical name and accessibility are preserved.
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
                    var fileName = Path.GetFileName(filePart);

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

            return prefix + Path.GetFileName(value);
        }

        return flag;
    }

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

        if (!string.IsNullOrEmpty(nullableMode)
            && !string.Equals(nullableMode, "disable", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("/nullable:" + nullableMode);
        }

        if (maxWarnLevel >= 0)
            result.Add($"/warn:{maxWarnLevel}");

        if (interceptorNamespaces.Count > 0)
            result.Add("/features:InterceptorsNamespaces=;" + string.Join(";", interceptorNamespaces));

        return result;
    }

    private static ComparisonResult CompareManagedRecords(string name, ManagedCompilationRecord msbuild, ManagedCompilationRecord bazel)
    {
        var result = new ComparisonResult { Name = name, Category = "managed" };

        AddSourceFileDifference(result, msbuild, bazel);

        // Defines: compare all defines except BAZEL (Bazel-only infrastructure define)
        // and build configuration defines (RELEASE/DEBUG/TRACE/CHECKED/NDEBUG) which
        // depend on the build config, not source structure.
        var msbuildDefines = new SortedSet<string>(
            msbuild.Defines.Where(d => !IsConfigDefine(d)),
            StringComparer.Ordinal);
        var bazelDefines = new SortedSet<string>(
            bazel.Defines.Where(d => !IsConfigDefine(d) && d != "BAZEL"),
            StringComparer.Ordinal);
        AddSetDifference(result, "defines", msbuildDefines, bazelDefines);

        // References: MSBuild OOB assemblies get the full targeting pack (~140 refs).
        // Bazel uses explicit deps, so it only has the refs it actually needs.
        // When MSBuild has 50+ more refs, these are targeting pack noise — filter them.
        var msbuildRefs = msbuild.References;
        var bazelRefs = bazel.References;
        var onlyInMSBuildRefs = new SortedSet<string>(msbuildRefs.Except(bazelRefs), StringComparer.Ordinal);
        var onlyInBazelRefs = new SortedSet<string>(bazelRefs.Except(msbuildRefs), StringComparer.Ordinal);
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
        // Filter ignored flags, then normalize path-bearing flags to filename-only.
        var msbuildFlags = new SortedSet<string>(
            NormalizeManagedFlags(msbuild.Flags.Where(f => !IsIgnoredManagedFlag(f)).Select(NormalizeManagedFlag)),
            StringComparer.Ordinal);
        var bazelFlags = new SortedSet<string>(
            NormalizeManagedFlags(bazel.Flags.Where(f => !IsIgnoredManagedFlag(f)).Select(NormalizeManagedFlag)),
            StringComparer.Ordinal);
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

        // Filter out MSBuild-generated AssemblyInfo.cs files (e.g.
        // artifacts/obj/X/Release/.../X.AssemblyInfo.cs). In Bazel, rules_dotnet
        // generates equivalent AssemblyInfo content internally; it's not a separate
        // source file in the aquery output.  Also filter Bazel-generated
        // AssemblyInfo.g.cs from local genrules (ILCompiler/crossgen2 tools).
        onlyInMSBuild.RemoveWhere(f =>
        {
            var fn = Path.GetFileName(f);
            return fn.EndsWith("AssemblyInfo.cs", StringComparison.Ordinal)
                || fn == "AssemblyInfo.g.cs";
        });
        onlyInBazel.RemoveWhere(f =>
        {
            var fn = Path.GetFileName(f);
            return fn.EndsWith("AssemblyInfo.cs", StringComparison.Ordinal)
                || fn == "AssemblyInfo.g.cs";
        });

        // Filter out MSBuild-generated InternalsVisibleTo.cs files.
        // In Bazel, IVT attributes are set via the internals_visible_to parameter,
        // which generates a file named internalsvisibleto.cs (lowercase).
        onlyInMSBuild.RemoveWhere(f => Path.GetFileName(f).EndsWith("InternalsVisibleTo.cs", StringComparison.OrdinalIgnoreCase));
        onlyInBazel.RemoveWhere(f => Path.GetFileName(f).EndsWith("internalsvisibleto.cs", StringComparison.OrdinalIgnoreCase));

        // Filter out test SDK and polyfill source files that MSBuild includes
        // but Bazel doesn't need (test SDK entry point, netstandard polyfills).
        onlyInMSBuild.RemoveWhere(f => IgnoredSourceFileNames.Contains(Path.GetFileName(f)));
        onlyInBazel.RemoveWhere(f => IgnoredSourceFileNames.Contains(Path.GetFileName(f)));

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
    /// Normalize generated file names so that variants like "System.SR.cs",
    /// "SR.g.cs", and "SharedStrings.g.cs" are treated as equivalent for matching.
    /// </summary>
    private static string NormalizeGeneratedFileName(string fileName)
    {
        if (fileName is "System.SR.cs" or "SR.g.cs" or "SharedStrings.g.cs" or "ILLink.Shared.SharedStrings.cs")
            return "System.SR.cs";

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
            or "NETSTANDARD" or "Unix"
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
    /// Higher is better. Platform-specific TFMs are preferred over the plain
    /// netX.Y TFM which is often a PNSE stub. Within the same platform shape,
    /// newer TFMs win over older ones.
    /// </summary>
    private static int TfmPriority(string outputPath)
    {
        // Stub assemblies (shims/stubs) are type-forward wrappers, not the real impl.
        if (outputPath.Contains("/stub/", StringComparison.OrdinalIgnoreCase))
            return -1;

        int platformBonus = 0;
        if (outputPath.Contains("-linux", StringComparison.OrdinalIgnoreCase))
            platformBonus = 3000;
        else if (outputPath.Contains("-unix", StringComparison.OrdinalIgnoreCase))
            platformBonus = 2000;
        else if (outputPath.Contains("-osx", StringComparison.OrdinalIgnoreCase)
            || outputPath.Contains("-macos", StringComparison.OrdinalIgnoreCase))
            platformBonus = 1000;

        var match = System.Text.RegularExpressions.Regex.Match(
            outputPath,
            @"net(?<major>\d+)\.(?<minor>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return platformBonus;

        int major = int.Parse(match.Groups["major"].Value);
        int minor = int.Parse(match.Groups["minor"].Value);
        return platformBonus + (major * 10) + minor;
    }
}
