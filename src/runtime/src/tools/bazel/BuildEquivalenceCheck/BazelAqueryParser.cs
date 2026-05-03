// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace BuildEquivalenceCheck;

/// <summary>
/// Parses Bazel aquery JSON output into normalized compilation records.
/// Handles both CppCompile (native) and CSharpCompile (managed) actions.
/// </summary>
public static class BazelAqueryParser
{
    public static List<NativeCompilationRecord> ParseNativeActions(string aqueryJsonPath, string repoRoot)
    {
        var records = new List<NativeCompilationRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(aqueryJsonPath));
        var root = doc.RootElement;

        var targets = ParseTargets(root);
        var execConfigs = DetectExecConfigurations(root);
        var actions = root.GetProperty("actions");

        foreach (var action in actions.EnumerateArray())
        {
            var mnemonic = action.GetProperty("mnemonic").GetString();
            if (mnemonic is not "CppCompile")
                continue;

            // Skip exec/tool configuration actions (e.g. ilasm, mscorpe built for host)
            var configId = action.GetProperty("configurationId").ToString();
            if (execConfigs.Contains(configId))
                continue;

            var targetId = action.GetProperty("targetId").ToString();
            var targetLabel = targets.GetValueOrDefault(targetId, "");
            var args = ParseArguments(action);
            var record = ParseNativeArguments(args, targetLabel, repoRoot);

            // Deduplicate: keep only the first action per source file
            // (transition-variant configs produce duplicate actions with identical flags)
            if (record is not null && seen.Add(record.SourceFile))
                records.Add(record);
        }

        return records;
    }

    public static List<ManagedCompilationRecord> ParseManagedActions(string aqueryJsonPath, string repoRoot)
    {
        var records = new List<ManagedCompilationRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(aqueryJsonPath));
        var root = doc.RootElement;

        var targets = ParseTargets(root);
        var execConfigs = DetectExecConfigurations(root);
        var actions = root.GetProperty("actions");

        foreach (var action in actions.EnumerateArray())
        {
            var mnemonic = action.GetProperty("mnemonic").GetString();
            if (mnemonic is not "CSharpCompile")
                continue;

            var configId = action.GetProperty("configurationId").ToString();
            if (execConfigs.Contains(configId))
                continue;

            var targetId = action.GetProperty("targetId").ToString();
            var targetLabel = targets.GetValueOrDefault(targetId, "");
            var args = ParseArguments(action);
            var record = ParseManagedArguments(args, targetLabel, repoRoot);

            // Dedup by assembly name + target label + output path so multiple
            // target-framework variants of the same Bazel target survive into
            // the comparison engine.
            if (record is not null && seen.Add(record.AssemblyName + "|" + targetLabel + "|" + record.OutputPath))
                records.Add(record);
        }

        return records;
    }

    private static Dictionary<string, string> ParseTargets(JsonElement root)
    {
        var targets = new Dictionary<string, string>();
        if (root.TryGetProperty("targets", out var targetsArray))
        {
            foreach (var t in targetsArray.EnumerateArray())
            {
                var id = t.GetProperty("id").ToString();
                var label = t.GetProperty("label").GetString() ?? "";
                targets[id] = label;
            }
        }

        return targets;
    }

    /// <summary>
    /// Detect exec-configuration actions by scanning output paths for "exec" in the
    /// config directory (e.g. "bazel-out/k8-opt-exec/bin/..."). Bazel aquery doesn't
    /// expose configuration metadata directly, but the output path config prefix is
    /// reliable.
    /// </summary>
    private static HashSet<string> DetectExecConfigurations(JsonElement root)
    {
        var execConfigs = new HashSet<string>();
        if (!root.TryGetProperty("actions", out var actions))
            return execConfigs;

        // Build a map of configurationId -> output path config prefix
        var configPrefixes = new Dictionary<string, string>();
        foreach (var action in actions.EnumerateArray())
        {
            var configId = action.GetProperty("configurationId").ToString();
            if (configPrefixes.ContainsKey(configId))
                continue;

            var args = ParseArguments(action);
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "-o" && i + 1 < args.Count)
                {
                    var outPath = args[i + 1];
                    // bazel-out/<CONFIG_PREFIX>/bin/...
                    var parts = outPath.Split('/');
                    if (parts.Length >= 3 && parts[0] == "bazel-out")
                    {
                        configPrefixes[configId] = parts[1];
                        if (parts[1].Contains("exec", StringComparison.OrdinalIgnoreCase))
                            execConfigs.Add(configId);
                    }

                    break;
                }
            }
        }

        return execConfigs;
    }

    private static List<string> ParseArguments(JsonElement action)
    {
        var args = new List<string>();
        if (action.TryGetProperty("arguments", out var argsArray))
        {
            foreach (var arg in argsArray.EnumerateArray())
            {
                args.Add(arg.GetString() ?? "");
            }
        }

        return args;
    }

    private static NativeCompilationRecord? ParseNativeArguments(List<string> args, string targetLabel, string repoRoot)
    {
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var undefines = new SortedSet<string>(StringComparer.Ordinal);
        var includes = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        string langStd = "";
        string optLevel = "";
        string? sourceFile = null;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("-D"))
            {
                defines.Add(arg[2..]);
            }
            else if (arg.StartsWith("-U"))
            {
                undefines.Add(arg[2..]);
            }
            else if (arg == "-isystem" || arg == "-iquote")
            {
                if (i + 1 < args.Count)
                    includes.Add(NormalizeBazelPath(args[++i], repoRoot));
            }
            else if (arg.StartsWith("-I"))
            {
                var path = arg.Length > 2 ? arg[2..] : (i + 1 < args.Count ? args[++i] : "");
                includes.Add(NormalizeBazelPath(path, repoRoot));
            }
            else if (arg.StartsWith("-std="))
            {
                langStd = arg;
            }
            else if (arg.StartsWith("-O"))
            {
                optLevel = arg;
            }
            else if (arg == "-c" && i + 1 < args.Count)
            {
                sourceFile = NormalizeBazelPath(args[++i], repoRoot);
            }
            else if (arg is "-o" or "-MF" or "-MQ" or "-MT")
            {
                if (i + 1 < args.Count)
                    i++; // skip argument
            }
            else if (arg is "-MD" or "-MMD")
            {
                // standalone flags, no argument to skip
            }
            else if (arg == "-frandom-seed" && i + 1 < args.Count)
            {
                i++; // skip argument
            }
            else if (arg.StartsWith("-frandom-seed="))
            {
                // skip
            }
            else if (arg.StartsWith("-W") || arg.StartsWith("-f") || arg.StartsWith("-g") || arg.StartsWith("-m"))
            {
                flags.Add(arg);
            }
        }

        if (sourceFile is null)
            return null;

        return new NativeCompilationRecord
        {
            SourceFile = sourceFile,
            Target = targetLabel,
            Defines = defines,
            Undefines = undefines,
            IncludePaths = includes,
            Flags = flags,
            LanguageStandard = langStd,
            OptimizationLevel = optLevel,
            BuildSystem = "bazel",
        };
    }

    private static ManagedCompilationRecord? ParseManagedArguments(List<string> args, string targetLabel, string repoRoot)
    {
        var sourceFiles = new SortedSet<string>(StringComparer.Ordinal);
        var sourceFileOriginalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var analyzers = new SortedSet<string>(StringComparer.Ordinal);
        var analyzerPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var cscFlags = new SortedSet<string>(StringComparer.Ordinal);
        string targetType = "library";
        string langVersion = "";
        string? assemblyName = null;
        string outputPath = "";

        foreach (var arg in args)
        {
            if (arg.StartsWith("/define:"))
            {
                foreach (var d in arg[8..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                    defines.Add(d);
            }
            else if (arg.StartsWith("/d:"))
            {
                foreach (var d in arg[3..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                    defines.Add(d);
            }
            else if (arg.StartsWith("/nowarn:"))
            {
                // Expand comma-separated codes into individual /nowarn: flags.
                foreach (var w in arg[8..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    cscFlags.Add("/nowarn:" + BinlogParser.NormalizeWarningCode(w.Trim()));
            }
            else if (arg.StartsWith("-r:") || arg.StartsWith("/r:") || arg.StartsWith("/reference:") || arg.StartsWith("-reference:"))
            {
                var refPath = arg[(arg.IndexOf(':') + 1)..];
                references.Add(ExtractAssemblyName(refPath));
            }
            else if (arg.StartsWith("/analyzer:"))
            {
                var analyzerPath = arg[10..];
                var name = ExtractAssemblyName(analyzerPath);
                analyzers.Add(name);
                analyzerPaths.TryAdd(name, analyzerPath);
            }
            else if (arg.StartsWith("/target:"))
            {
                targetType = arg[8..];
            }
            else if (arg.StartsWith("/langversion:"))
            {
                langVersion = arg[13..];
            }
            else if (arg.StartsWith("/out:"))
            {
                outputPath = arg[5..];
                assemblyName = ExtractAssemblyName(outputPath);
            }
            else if (arg is "/unsafe+" or "/unsafe-")
            {
                // rules_dotnet emits a default /unsafe- and then appends /unsafe+
                // when allow_unsafe_blocks is enabled. Model csc's last-wins
                // behavior so we compare the effective flag, not both entries.
                cscFlags.Remove("/unsafe+");
                cscFlags.Remove("/unsafe-");
                cscFlags.Add(arg);
            }
            else if (arg is "/checked+" or "/checked-")
            {
                cscFlags.Remove("/checked+");
                cscFlags.Remove("/checked-");
                cscFlags.Add(arg);
            }
            else if (arg.StartsWith('/'))
            {
                // Other csc flags
                cscFlags.Add(arg);
            }
            else if (!arg.StartsWith('-') && (arg.EndsWith(".cs") || arg.Contains(".cs")))
            {
                var normalized = NormalizeBazelPath(arg, repoRoot);
                sourceFiles.Add(normalized);
                // Bazel paths are relative to the execution root (repo root).
                sourceFileOriginalPaths.TryAdd(normalized, Path.GetFullPath(Path.Combine(repoRoot, arg)));
            }
        }

        if (assemblyName is null)
            return null;

        return new ManagedCompilationRecord
        {
            AssemblyName = assemblyName,
            SourceFiles = sourceFiles,
            SourceFileOriginalPaths = sourceFileOriginalPaths,
            Defines = defines,
            References = references,
            Analyzers = analyzers,
            AnalyzerPaths = analyzerPaths,
            Flags = cscFlags,
            TargetType = targetType,
            LangVersion = langVersion,
            BuildSystem = "bazel",
            OutputPath = outputPath,
            TargetLabel = targetLabel,
        };
    }

    private static string ExtractAssemblyName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        // Bazel ref_impl_pair targets are named "live_X" and produce DLLs
        // with that prefix.  Strip it so we compare against the plain assembly name.
        if (fileName.StartsWith("live_", StringComparison.Ordinal))
            fileName = fileName["live_".Length..];
        return fileName;
    }

    internal static string NormalizeBazelPath(string path, string repoRoot)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Bazel paths are relative to execution root.
        // Strip "bazel-out/<config>/bin/" prefix or "external/" prefix.
        if (path.StartsWith("bazel-out/"))
        {
            var parts = path.Split('/', 4);
            if (parts.Length >= 4 && parts[2] == "bin")
                return parts[3];
        }

        if (path.StartsWith("external/"))
            return path;

        // Already repo-relative
        return path;
    }
}
