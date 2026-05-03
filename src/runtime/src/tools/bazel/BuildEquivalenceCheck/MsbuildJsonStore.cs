// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildEquivalenceCheck;

/// <summary>
/// Serializes and deserializes MSBuild managed compilation records to/from a
/// compact JSON file. This avoids re-parsing large .binlog files for repeated
/// equivalence checks.
/// </summary>
public static class MsbuildJsonStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(List<ManagedCompilationRecord> records, string path, string? repoRoot = null)
    {
        var root = repoRoot is not null
            ? Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
            : null;

        var dtos = records.Select(r => new RecordDto
        {
            AssemblyName = r.AssemblyName,
            SourceFiles = [.. r.SourceFiles],
            Defines = [.. r.Defines],
            References = [.. r.References],
            ReferencePaths = r.ReferencePaths.Count > 0
                ? NormalizeRefPaths(r.ReferencePaths, r.IsReferenceAssembly, root)
                : null,
            Analyzers = r.Analyzers.Count > 0 ? [.. r.Analyzers] : null,
            AnalyzerPaths = r.AnalyzerPaths.Count > 0
                ? NormalizeAnalyzerPaths(r.AnalyzerPaths, root)
                : null,
            Flags = r.Flags.Count > 0 ? [.. r.Flags] : null,
            TargetType = r.TargetType != "library" ? r.TargetType : null,
            LangVersion = !string.IsNullOrEmpty(r.LangVersion) ? r.LangVersion : null,
            OutputPath = !string.IsNullOrEmpty(r.OutputPath) ? r.OutputPath : null,
            IsReferenceAssembly = r.IsReferenceAssembly ? true : null,
        }).ToList();

        var json = JsonSerializer.Serialize(dtos, s_options);
        File.WriteAllText(path, json);
    }

    public static List<ManagedCompilationRecord> Load(string path)
    {
        var json = File.ReadAllText(path);
        var dtos = JsonSerializer.Deserialize<List<RecordDto>>(json, s_readOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize MSBuild JSON from {path}");

        return dtos.Select(d =>
        {
            // Merge legacy NoWarn entries into Flags for backward compatibility
            // with JSON files that still have the separate noWarn field.
            var flags = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var f in d.Flags ?? [])
                flags.Add(NormalizeLegacyFlag(f));
            if (d.NoWarn is not null)
            {
                foreach (var code in d.NoWarn)
                    flags.Add("/nowarn:" + BinlogParser.NormalizeWarningCode(code));
            }

            return new ManagedCompilationRecord
            {
                AssemblyName = d.AssemblyName,
                SourceFiles = new SortedSet<string>(d.SourceFiles ?? [], StringComparer.Ordinal),
                SourceFileOriginalPaths = [],
                Defines = new SortedSet<string>(d.Defines ?? [], StringComparer.Ordinal),
                References = new SortedSet<string>(d.References ?? [], StringComparer.Ordinal),
                ReferencePaths = d.ReferencePaths ?? [],
                Analyzers = new SortedSet<string>(d.Analyzers ?? [], StringComparer.Ordinal),
                AnalyzerPaths = d.AnalyzerPaths ?? [],
                Flags = flags,
                TargetType = d.TargetType ?? "library",
                LangVersion = d.LangVersion ?? "",
                BuildSystem = "msbuild",
                OutputPath = d.OutputPath ?? "",
                IsReferenceAssembly = d.IsReferenceAssembly ?? false,
            };
        }).ToList();
    }

    private sealed class RecordDto
    {
        public required string AssemblyName { get; set; }
        public List<string>? SourceFiles { get; set; }
        public List<string>? Defines { get; set; }
        public List<string>? References { get; set; }
        public Dictionary<string, string>? ReferencePaths { get; set; }
        public List<string>? NoWarn { get; set; }
        public List<string>? Analyzers { get; set; }
        public Dictionary<string, string>? AnalyzerPaths { get; set; }
        public List<string>? Flags { get; set; }
        public string? TargetType { get; set; }
        public string? LangVersion { get; set; }
        public string? OutputPath { get; set; }
        public bool? IsReferenceAssembly { get; set; }
    }

    /// <summary>
    /// Normalize legacy MSBuild property-format flags to csc command-line format.
    /// The old BinlogParser emitted flags as property names (e.g. /allowunsafeblocks:True)
    /// instead of csc switches (e.g. /unsafe+). This converts them so legacy JSON files
    /// compare correctly against csc command-line flags from Bazel or the new BinlogParser.
    /// </summary>
    private static string NormalizeLegacyFlag(string flag)
    {
        return flag switch
        {
            "/allowunsafeblocks:True" => "/unsafe+",
            "/allowunsafeblocks:False" => "/unsafe-",
            "/checkforoverflowunderflow:True" => "/checked+",
            "/checkforoverflowunderflow:False" => "/checked-",
            "/deterministic:True" => "/deterministic+",
            "/deterministic:False" => "/deterministic-",
            "/highentropyva:True" => "/highentropyva+",
            "/highentropyva:False" => "/highentropyva-",
            "/optimize:True" => "/optimize+",
            "/optimize:False" => "/optimize-",
            _ => flag,
        };
    }

    /// <summary>
    /// Normalize reference paths to repo-relative and skip ref-assembly/large-ref-set
    /// records to keep the JSON file small. Only impl records with small ref sets
    /// (i.e., not targeting-pack consumers) get their paths preserved.
    /// </summary>
    private static Dictionary<string, string>? NormalizeRefPaths(
        Dictionary<string, string> paths, bool isRefAssembly, string? repoRoot)
    {
        if (isRefAssembly || paths.Count > 50)
            return null;

        var result = new Dictionary<string, string>(paths.Count);
        foreach (var (name, fullPath) in paths)
        {
            if (repoRoot is not null && fullPath.StartsWith(repoRoot, StringComparison.Ordinal))
                result[name] = fullPath[repoRoot.Length..];
            else
                result[name] = fullPath;
        }
        return result;
    }

    /// <summary>
    /// Normalize analyzer paths to repo-relative for compact, portable JSON.
    /// </summary>
    private static Dictionary<string, string> NormalizeAnalyzerPaths(
        Dictionary<string, string> paths, string? repoRoot)
    {
        var result = new Dictionary<string, string>(paths.Count);
        foreach (var (name, fullPath) in paths)
        {
            if (repoRoot is not null && fullPath.StartsWith(repoRoot, StringComparison.Ordinal))
                result[name] = fullPath[repoRoot.Length..];
            else
                result[name] = fullPath;
        }
        return result;
    }
}
