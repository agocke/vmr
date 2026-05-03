// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Build.Logging.StructuredLogger;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BuildEquivalenceCheck;

/// <summary>
/// Parses MSBuild binary log (.binlog) files to extract Csc task invocations
/// by reading the full compiler command line from each Csc task.
/// </summary>
public static class BinlogParser
{
    public static List<ManagedCompilationRecord> Parse(string binlogPath, string repoRoot)
    {
        var records = new List<ManagedCompilationRecord>();
        var build = BinaryLog.ReadBuild(binlogPath);

        build.VisitAllChildren<MSBuildTask>(task =>
        {
            if (!string.Equals(task.Name, "Csc", StringComparison.OrdinalIgnoreCase))
                return;

            var record = ExtractFromCscTask(task, repoRoot);
            if (record is not null)
                records.Add(record);
        });

        return records;
    }

    private static ManagedCompilationRecord? ExtractFromCscTask(MSBuildTask task, string repoRoot)
    {
        var commandLine = task.CommandLineArguments;
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        // Resolve relative source paths against the project directory, not CWD.
        var projectDirectory = task.GetNearestParent<Project>()?.ProjectDirectory ?? repoRoot;

        var args = SplitCommandLine(commandLine);
        return ParseCscArguments(args, projectDirectory, repoRoot);
    }

    /// <summary>
    /// Parse a list of csc command-line arguments into a <see cref="ManagedCompilationRecord"/>.
    /// Leading tool path arguments (dotnet host and/or csc.dll) are automatically skipped.
    /// </summary>
    private static ManagedCompilationRecord? ParseCscArguments(
        List<string> args, string projectDirectory, string repoRoot)
    {
        var sourceFiles = new SortedSet<string>(StringComparer.Ordinal);
        var sourceFileOriginalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var referencePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var analyzers = new SortedSet<string>(StringComparer.Ordinal);
        var analyzerPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        string targetType = "library";
        string langVersion = "";
        string? assemblyName = null;
        string? outputPath = null;

        // Skip leading tool path arguments. MSBuild CommandLineArguments may start
        // with the dotnet host and/or the csc.dll path, e.g.:
        //   /path/to/dotnet /path/to/csc.dll /out:...
        //   /path/to/csc.dll /out:...
        int startIndex = 0;
        for (int i = 0; i < args.Count && i < 3; i++)
        {
            var a = args[i];
            if (!IsCscFlag(a)
                && (a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || a.EndsWith("/dotnet", StringComparison.Ordinal)
                    || a.EndsWith("\\dotnet.exe", StringComparison.OrdinalIgnoreCase)
                    || a == "dotnet"))
            {
                startIndex = i + 1;
            }
            else
            {
                break;
            }
        }

        for (int i = startIndex; i < args.Count; i++)
        {
            var arg = args[i];

            // Source files: check .cs extension BEFORE flag prefix detection,
            // because absolute paths on Linux start with '/' which would
            // otherwise be mistaken for a csc flag. Use IsCscFlag to
            // distinguish /home/user/File.cs from /additionalfile:Foo.cs.
            if (arg.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !IsCscFlag(arg))
            {
                var normalized = NormalizePath(arg, repoRoot, projectDirectory);
                sourceFiles.Add(normalized);
                var diskPath = Path.IsPathRooted(arg)
                    ? arg
                    : Path.GetFullPath(Path.Combine(projectDirectory, arg));
                sourceFileOriginalPaths.TryAdd(normalized, diskPath);
            }
            else if (arg.StartsWith("/define:") || arg.StartsWith("/d:") || arg.StartsWith("-define:") || arg.StartsWith("-d:"))
            {
                var value = arg[(arg.IndexOf(':') + 1)..];
                foreach (var d in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    defines.Add(d.Trim());
            }
            else if (arg.StartsWith("/nowarn:") || arg.StartsWith("-nowarn:"))
            {
                // Expand comma-separated codes into individual /nowarn: flags.
                foreach (var w in arg[(arg.IndexOf(':') + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    flags.Add("/nowarn:" + NormalizeWarningCode(w.Trim()));
            }
            else if (arg.StartsWith("/r:") || arg.StartsWith("-r:")
                || arg.StartsWith("/reference:") || arg.StartsWith("-reference:"))
            {
                var refPath = arg[(arg.IndexOf(':') + 1)..];
                var name = Path.GetFileNameWithoutExtension(refPath);
                references.Add(name);
                var fullPath = Path.IsPathRooted(refPath)
                    ? Path.GetFullPath(refPath)
                    : Path.GetFullPath(Path.Combine(projectDirectory, refPath));
                referencePaths.TryAdd(name, fullPath);
            }
            else if (arg.StartsWith("/analyzer:") || arg.StartsWith("-analyzer:"))
            {
                var analyzerPath = arg[(arg.IndexOf(':') + 1)..];
                var name = Path.GetFileNameWithoutExtension(analyzerPath);
                analyzers.Add(name);
                var fullPath = Path.IsPathRooted(analyzerPath)
                    ? Path.GetFullPath(analyzerPath)
                    : Path.GetFullPath(Path.Combine(projectDirectory, analyzerPath));
                analyzerPaths.TryAdd(name, fullPath);
            }
            else if (arg.StartsWith("/target:") || arg.StartsWith("-target:"))
            {
                targetType = arg[(arg.IndexOf(':') + 1)..];
            }
            else if (arg.StartsWith("/langversion:") || arg.StartsWith("-langversion:"))
            {
                langVersion = arg[(arg.IndexOf(':') + 1)..];
            }
            else if (arg.StartsWith("/out:") || arg.StartsWith("-out:"))
            {
                var outPath = arg[(arg.IndexOf(':') + 1)..];
                assemblyName = Path.GetFileNameWithoutExtension(outPath);
                outputPath = outPath;
            }
            else if (arg.StartsWith('/') || arg.StartsWith('-'))
            {
                // Skip tool paths that leaked through (e.g. /path/to/csc.dll
                // when preceded by "dotnet exec"). Genuine csc flags are short
                // prefixes like /out:, /unsafe+, etc.—not multi-segment paths.
                if (arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                flags.Add(arg);
            }
            else if (arg.StartsWith('@'))
            {
                // Response file reference — skip
            }
        }

        if (assemblyName is null)
            return null;

        // Determine if this is a reference assembly by checking the project
        // directory or output path for a "/ref/" segment.
        var isRef = projectDirectory.Contains("/ref/") || projectDirectory.Contains("/ref\\")
            || (outputPath?.Contains("/ref/") == true) || (outputPath?.Contains("/ref\\") == true);

        return new ManagedCompilationRecord
        {
            AssemblyName = assemblyName,
            SourceFiles = sourceFiles,
            SourceFileOriginalPaths = sourceFileOriginalPaths,
            Defines = defines,
            References = references,
            ReferencePaths = referencePaths,
            Analyzers = analyzers,
            AnalyzerPaths = analyzerPaths,
            Flags = flags,
            TargetType = targetType,
            LangVersion = langVersion,
            BuildSystem = "msbuild",
            OutputPath = outputPath ?? "",
            IsReferenceAssembly = isRef,
        };
    }

    /// <summary>
    /// Split a command-line string into individual arguments, respecting
    /// double-quoted segments (quotes are stripped from the result).
    /// </summary>
    internal static List<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    args.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
            args.Add(sb.ToString());

        return args;
    }

    private static string NormalizePath(string path, string repoRoot, string projectDirectory)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Resolve relative paths against the project directory (not CWD) so that
        // paths like "System/Collections/Generic/LinkedList.cs" recorded in the
        // binlog relative to the .csproj become fully repo-relative.
        var basePath = Path.IsPathRooted(path) ? path : Path.Combine(projectDirectory, path);
        var normalized = Path.GetFullPath(basePath);
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (normalized.StartsWith(root, StringComparison.Ordinal))
            return normalized[root.Length..];

        return normalized;
    }

    /// <summary>
    /// Determine whether an argument is a csc flag (e.g. /out:Foo.dll, /unsafe+)
    /// versus a file path (e.g. /home/user/csc.dll, src/File.cs).
    /// Csc flags start with / or - followed by alphabetic characters, then : or +/-.
    /// File paths on Linux start with / but have a path separator within the name.
    /// </summary>
    private static bool IsCscFlag(string arg)
    {
        if (arg.Length < 2 || (arg[0] != '/' && arg[0] != '-'))
            return false;

        for (int i = 1; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c is ':' or '+' or '-')
                return true;
            if (c is '/' or '\\')
                return false;
            if (!char.IsLetter(c))
                return false;
        }

        // Bare flag like /noconfig (all letters after the prefix)
        return true;
    }

    /// <summary>
    /// Normalize warning codes to a consistent format.
    /// MSBuild sometimes emits bare numbers (e.g. "1701") and sometimes
    /// prefixed codes (e.g. "CS1701"). Normalize to always use "CS" prefix
    /// for numeric codes and pad CS codes to at least 4 digits so that
    /// CS649 and CS0649 compare as equal.
    /// </summary>
    internal static string NormalizeWarningCode(string code)
    {
        if (code.Length > 0 && char.IsDigit(code[0]))
            return "CS" + code.PadLeft(4, '0');

        // Normalize CSnnnn codes to at least 4 digits (e.g. CS649 → CS0649)
        if (code.StartsWith("CS", StringComparison.Ordinal) && code.Length > 2)
        {
            var numPart = code.AsSpan(2);
            if (numPart.Length > 0 && char.IsDigit(numPart[0]))
                return "CS" + numPart.ToString().PadLeft(4, '0');
        }

        return code;
    }
}
