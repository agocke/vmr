// FixVersionBumps.cs
//
// Deterministic script that updates Bazel NuGet version-pinned labels
// when eng/Version.Details.props bumps package versions.
//
// Usage: dotnet run FixVersionBumps.cs -- <base-ref> <head-ref> [--repo-root <path>]
//
// Reads the git diff of eng/Version.Details.props between base-ref and head-ref,
// extracts old→new version mappings, cross-references against paket.dependencies,
// and updates:
//   1. paket.dependencies (source of truth for paket2bazel)
//   2. defs.bzl (centralized versioned repo name constants)
//   3. MODULE.bazel (use_repo entries for versioned repos)
//
// After running this script, run sync-paket.sh to regenerate paket/paket.main.bzl.

using System.Diagnostics;
using System.Text.RegularExpressions;

// ─── Parse arguments ──────────────────────────────────────────────────────────

string? baseRef = null;
string? headRef = null;
string repoRoot = ".";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repo-root" when i + 1 < args.Length:
            repoRoot = args[++i];
            break;
        default:
            if (baseRef is null) baseRef = args[i];
            else if (headRef is null) headRef = args[i];
            else
            {
                Console.Error.WriteLine($"Unexpected argument: {args[i]}");
                return 2;
            }
            break;
    }
}

if (baseRef is null || headRef is null)
{
    Console.Error.WriteLine("Usage: dotnet run FixVersionBumps.cs -- <base-ref> <head-ref> [--repo-root <path>]");
    return 2;
}

// ─── Step 1: Get version bump diff ────────────────────────────────────────────

var diffOutput = RunGit($"diff {baseRef}..{headRef} -- eng/Version.Details.props eng/Versions.props", repoRoot);
if (string.IsNullOrWhiteSpace(diffOutput))
{
    Console.WriteLine("No version file changes detected.");
    return 0;
}

// Parse diff lines to extract old→new version pairs per package.
// Lines look like:
//   -    <MicrosoftDotNetArcadeSdkPackageVersion>10.0.0-beta.26102.102</...>
//   +    <MicrosoftDotNetArcadeSdkPackageVersion>10.0.0-beta.26110.124</...>

var versionPropRegex = new Regex(@"^([+-])\s*<(\w+)PackageVersion>([^<]+)</\w+PackageVersion>");

// Group by property name, collecting removed (-) and added (+) versions
var versionChanges = new Dictionary<string, (string? OldVersion, string? NewVersion)>();

foreach (var line in diffOutput.Split('\n'))
{
    var match = versionPropRegex.Match(line);
    if (!match.Success)
        continue;

    var sign = match.Groups[1].Value;
    var propName = match.Groups[2].Value;    // e.g., "MicrosoftDotNetArcadeSdk"
    var version = match.Groups[3].Value;     // e.g., "10.0.0-beta.26102.102"

    if (!versionChanges.TryGetValue(propName, out var entry))
        entry = (null, null);

    if (sign == "-")
        entry = (version, entry.NewVersion);
    else
        entry = (entry.OldVersion, version);

    versionChanges[propName] = entry;
}

// Filter to entries that have both old and new, and they differ
var bumps = versionChanges
    .Where(kv => kv.Value.OldVersion is not null
              && kv.Value.NewVersion is not null
              && kv.Value.OldVersion != kv.Value.NewVersion)
    .ToDictionary(kv => kv.Key, kv => (Old: kv.Value.OldVersion!, New: kv.Value.NewVersion!));

if (bumps.Count == 0)
{
    Console.WriteLine("No version bumps found in diff.");
    return 0;
}

Console.WriteLine($"Found {bumps.Count} version bump(s) in eng/Version.Details.props:");
foreach (var (prop, versions) in bumps)
    Console.WriteLine($"  {prop}: {versions.Old} → {versions.New}");

// ─── Step 2: Cross-reference against paket.dependencies ──────────────────────

var paketDepsPath = Path.Combine(repoRoot, "paket.dependencies");
if (!File.Exists(paketDepsPath))
{
    Console.Error.WriteLine($"paket.dependencies not found at {paketDepsPath}");
    return 1;
}

var paketDepsContent = File.ReadAllText(paketDepsPath);

// Parse nuget lines: "nuget PackageName Version"
var paketLineRegex = new Regex(@"^nuget\s+(\S+)\s+(\S+)", RegexOptions.Multiline);
var paketPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (Match m in paketLineRegex.Matches(paketDepsContent))
    paketPackages[m.Groups[1].Value] = m.Groups[2].Value;

// Build lookup: normalized MSBuild prop name → paket package name
// e.g., "microsoftdotnetarcadesdk" → "microsoft.dotnet.arcade.sdk"
var normalizedToPaketName = new Dictionary<string, string>();
foreach (var name in paketPackages.Keys)
{
    var normalized = name.Replace(".", "").ToLowerInvariant();
    normalizedToPaketName[normalized] = name;
}

// Match bumped MSBuild properties to paket packages
var replacements = new List<(string PackageName, string OldVersion, string NewVersion)>();

foreach (var (propName, versions) in bumps)
{
    var normalizedProp = propName.ToLowerInvariant();
    if (normalizedToPaketName.TryGetValue(normalizedProp, out var paketName))
    {
        replacements.Add((paketName, versions.Old, versions.New));
    }
}

if (replacements.Count == 0)
{
    Console.WriteLine("No bumped packages are used in Bazel NuGet deps.");
    return 0;
}

Console.WriteLine($"\n{replacements.Count} package(s) affect Bazel:");
foreach (var r in replacements)
    Console.WriteLine($"  {r.PackageName}: {r.OldVersion} → {r.NewVersion}");

// ─── Step 3: Update paket.dependencies ────────────────────────────────────────

Console.WriteLine("\nUpdating paket.dependencies...");
var updatedPaketDeps = paketDepsContent;

foreach (var (packageName, oldVersion, newVersion) in replacements)
{
    // Replace: "nuget PackageName OldVersion" → "nuget PackageName NewVersion"
    var pattern = $@"(nuget\s+{Regex.Escape(packageName)}\s+){Regex.Escape(oldVersion)}";
    var regex = new Regex(pattern, RegexOptions.IgnoreCase);
    if (regex.IsMatch(updatedPaketDeps))
    {
        updatedPaketDeps = regex.Replace(updatedPaketDeps, $"${{1}}{newVersion}");
        Console.WriteLine($"  paket.dependencies: {packageName} {oldVersion} → {newVersion}");
    }
}

File.WriteAllText(paketDepsPath, updatedPaketDeps);

// ─── Step 4: Update defs.bzl centralized constants ────────────────────────────

Console.WriteLine("\nUpdating defs.bzl...");
var defsPath = Path.Combine(repoRoot, "defs.bzl");

if (File.Exists(defsPath))
{
    var defsContent = File.ReadAllText(defsPath);
    var defsUpdated = false;

    foreach (var (packageName, oldVersion, newVersion) in replacements)
    {
        var lowerName = packageName.ToLowerInvariant();
        var oldRepoName = $"nuget.{lowerName}.v{oldVersion}";
        var newRepoName = $"nuget.{lowerName}.v{newVersion}";

        if (defsContent.Contains(oldRepoName))
        {
            var count = CountOccurrences(defsContent, oldRepoName);
            defsContent = defsContent.Replace(oldRepoName, newRepoName);
            Console.WriteLine($"  defs.bzl: {count} replacement(s) ({oldRepoName})");
            defsUpdated = true;
        }
    }

    if (defsUpdated)
        File.WriteAllText(defsPath, defsContent);
    else
        Console.WriteLine("  defs.bzl: no changes needed");
}

// ─── Step 5: Update MODULE.bazel use_repo entries ─────────────────────────────

Console.WriteLine("\nUpdating MODULE.bazel...");
var modulePath = Path.Combine(repoRoot, "MODULE.bazel");

if (File.Exists(modulePath))
{
    var moduleContent = File.ReadAllText(modulePath);
    var moduleUpdated = false;

    foreach (var (packageName, oldVersion, newVersion) in replacements)
    {
        var lowerName = packageName.ToLowerInvariant();
        var oldRepoName = $"nuget.{lowerName}.v{oldVersion}";
        var newRepoName = $"nuget.{lowerName}.v{newVersion}";

        if (moduleContent.Contains(oldRepoName))
        {
            var count = CountOccurrences(moduleContent, oldRepoName);
            moduleContent = moduleContent.Replace(oldRepoName, newRepoName);
            Console.WriteLine($"  MODULE.bazel: {count} replacement(s) ({oldRepoName})");
            moduleUpdated = true;
        }
    }

    if (moduleUpdated)
        File.WriteAllText(modulePath, moduleContent);
    else
        Console.WriteLine("  MODULE.bazel: no changes needed");
}

Console.WriteLine("\nDone. Run sync-paket.sh to regenerate paket/paket.main.bzl.");

// ─── Step 6: Scan BUILD.bazel and .bzl files for hardcoded versioned repo names ──

Console.WriteLine("\nScanning BUILD.bazel files for hardcoded versioned repo names...");
var buildFiles = Directory.EnumerateFiles(repoRoot, "BUILD.bazel", SearchOption.AllDirectories)
    .Where(f => !f.Contains("paket/paket.main"))
    .ToList();

var totalBuildReplacements = 0;

foreach (var buildFile in buildFiles)
{
    var content = File.ReadAllText(buildFile);
    var updated = false;

    foreach (var (packageName, oldVersion, newVersion) in replacements)
    {
        var lowerName = packageName.ToLowerInvariant();
        var oldRepoName = $"nuget.{lowerName}.v{oldVersion}";
        var newRepoName = $"nuget.{lowerName}.v{newVersion}";

        if (content.Contains(oldRepoName))
        {
            var count = CountOccurrences(content, oldRepoName);
            content = content.Replace(oldRepoName, newRepoName);
            var relativePath = Path.GetRelativePath(repoRoot, buildFile);
            Console.WriteLine($"  {relativePath}: {count} replacement(s) ({oldRepoName})");
            totalBuildReplacements += count;
            updated = true;
        }
    }

    if (updated)
        File.WriteAllText(buildFile, content);
}

if (totalBuildReplacements == 0)
    Console.WriteLine("  No hardcoded versioned repo names found in BUILD.bazel files.");
else
    Console.WriteLine($"\n{totalBuildReplacements} total replacement(s) in BUILD.bazel files.");

Console.WriteLine("\nScanning .bzl files for hardcoded versioned repo names...");
var bzlFiles = Directory.EnumerateFiles(repoRoot, "*.bzl", SearchOption.AllDirectories)
    .Where(f => !f.Contains("paket/paket.main"))
    .ToList();

var totalBzlReplacements = 0;

foreach (var bzlFile in bzlFiles)
{
    var content = File.ReadAllText(bzlFile);
    var updated = false;

    foreach (var (packageName, oldVersion, newVersion) in replacements)
    {
        var lowerName = packageName.ToLowerInvariant();
        var oldRepoName = $"nuget.{lowerName}.v{oldVersion}";
        var newRepoName = $"nuget.{lowerName}.v{newVersion}";

        if (content.Contains(oldRepoName))
        {
            var count = CountOccurrences(content, oldRepoName);
            content = content.Replace(oldRepoName, newRepoName);
            var relativePath = Path.GetRelativePath(repoRoot, bzlFile);
            Console.WriteLine($"  {relativePath}: {count} replacement(s) ({oldRepoName})");
            totalBzlReplacements += count;
            updated = true;
        }
    }

    if (updated)
        File.WriteAllText(bzlFile, content);
}

if (totalBzlReplacements == 0)
    Console.WriteLine("  No hardcoded versioned repo names found in .bzl files.");
else
    Console.WriteLine($"\n{totalBzlReplacements} total replacement(s) in .bzl files.");

Console.WriteLine("\nDone. Run paket install then sync-paket.sh to regenerate paket files.");
return 0;

// ─── Helpers ──────────────────────────────────────────────────────────────────

static string RunGit(string arguments, string workingDir)
{
    var psi = new ProcessStartInfo("git", arguments)
    {
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var proc = Process.Start(psi)!;
    var output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    return output;
}

static int CountOccurrences(string text, string pattern)
{
    int count = 0;
    int idx = 0;
    while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
    {
        count++;
        idx += pattern.Length;
    }
    return count;
}
