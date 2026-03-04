// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BuildEquivalenceCheck;

// ── Argument parsing ────────────────────────────────────────────────
string? repoRoot = null;
var cmakeCompileCommandsPaths = new List<string>();
var binlogPaths = new List<string>();
string? bazelAqueryNativePath = null;
string? bazelAqueryManagedPath = null;
string? jsonOutputPath = null;
bool verbose = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repo-root":
            repoRoot = args[++i];
            break;
        case "--cmake-compile-commands":
            cmakeCompileCommandsPaths.Add(args[++i]);
            break;
        case "--binlog":
            binlogPaths.Add(args[++i]);
            break;
        case "--bazel-native-aquery":
            bazelAqueryNativePath = args[++i];
            break;
        case "--bazel-managed-aquery":
            bazelAqueryManagedPath = args[++i];
            break;
        case "--json-output":
            jsonOutputPath = args[++i];
            break;
        case "--verbose" or "-v":
            verbose = true;
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}

repoRoot ??= FindRepoRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("Could not determine repo root. Use --repo-root.");
    return 1;
}

repoRoot = Path.GetFullPath(repoRoot);

// ── Parse CMake compile_commands.json ───────────────────────────────
var cmakeNativeRecords = new List<NativeCompilationRecord>();
foreach (var path in cmakeCompileCommandsPaths)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"CMake compile_commands.json not found: {path}");
        continue;
    }

    Console.WriteLine($"Parsing CMake compile_commands: {path}");
    cmakeNativeRecords.AddRange(CMakeParser.Parse(path, repoRoot));
}

Console.WriteLine($"  CMake native records: {cmakeNativeRecords.Count}");

// ── Parse Bazel aquery (native) ─────────────────────────────────────
var bazelNativeRecords = new List<NativeCompilationRecord>();
if (bazelAqueryNativePath is not null && File.Exists(bazelAqueryNativePath))
{
    Console.WriteLine($"Parsing Bazel native aquery: {bazelAqueryNativePath}");
    bazelNativeRecords = BazelAqueryParser.ParseNativeActions(bazelAqueryNativePath, repoRoot);
    Console.WriteLine($"  Bazel native records: {bazelNativeRecords.Count}");
}

// ── Parse MSBuild binlogs ───────────────────────────────────────────
var msbuildManagedRecords = new List<ManagedCompilationRecord>();
foreach (var path in binlogPaths)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Binlog not found: {path}");
        continue;
    }

    Console.WriteLine($"Parsing MSBuild binlog: {path}");
    msbuildManagedRecords.AddRange(BinlogParser.Parse(path, repoRoot));
}

Console.WriteLine($"  MSBuild managed records: {msbuildManagedRecords.Count}");

// ── Parse Bazel aquery (managed) ────────────────────────────────────
var bazelManagedRecords = new List<ManagedCompilationRecord>();
if (bazelAqueryManagedPath is not null && File.Exists(bazelAqueryManagedPath))
{
    Console.WriteLine($"Parsing Bazel managed aquery: {bazelAqueryManagedPath}");
    bazelManagedRecords = BazelAqueryParser.ParseManagedActions(bazelAqueryManagedPath, repoRoot);
    Console.WriteLine($"  Bazel managed records: {bazelManagedRecords.Count}");
}

// ── Compare ─────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("Comparing build inputs...");

var report = ComparisonEngine.CompareNative(cmakeNativeRecords, bazelNativeRecords);
ComparisonEngine.CompareManaged(report, msbuildManagedRecords, bazelManagedRecords);

// ── Report ──────────────────────────────────────────────────────────
ReportWriter.WriteConsoleReport(report, verbose);

if (jsonOutputPath is not null)
{
    ReportWriter.WriteJsonReport(report, jsonOutputPath);
}

return report.IsEquivalent ? 0 : 1;

// ── Helpers ─────────────────────────────────────────────────────────

static string? FindRepoRoot()
{
    var dir = Directory.GetCurrentDirectory();
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir, "global.json")) &&
            File.Exists(Path.Combine(dir, "MODULE.bazel")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }

    return null;
}

static void PrintUsage()
{
    Console.WriteLine("""
        BuildEquivalenceCheck — Compare Bazel and CMake/MSBuild build inputs

        Usage:
          BuildEquivalenceCheck [options]

        Options:
          --repo-root <path>                 Repository root (auto-detected if not set)
          --cmake-compile-commands <path>    CMake compile_commands.json (can specify multiple)
          --binlog <path>                    MSBuild .binlog file (can specify multiple)
          --bazel-native-aquery <path>       Bazel aquery JSON for native CppCompile actions
          --bazel-managed-aquery <path>      Bazel aquery JSON for managed CSharpCompile actions
          --json-output <path>               Write detailed JSON report to file
          --verbose / -v                     Show all differences including only-in lists
          --help / -h                        Show this help
        """);
}
