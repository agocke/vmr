// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Logging.StructuredLogger;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BuildEquivalenceCheck;

/// <summary>
/// Parses MSBuild binary log (.binlog) files to extract Csc task invocations.
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
        // Resolve relative source paths against the project directory, not CWD.
        var projectDirectory = task.GetNearestParent<Project>()?.ProjectDirectory ?? repoRoot;

        var sourceFiles = new SortedSet<string>(StringComparer.Ordinal);
        var sourceFileOriginalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var noWarn = new SortedSet<string>(StringComparer.Ordinal);
        var analyzers = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        string targetType = "library";
        string langVersion = "";
        string? assemblyName = null;

        // Extract parameters from task children
        var folder = task.FindChild<Folder>("Parameters");
        if (folder is null)
            return null;

        foreach (var child in folder.Children)
        {
            if (child is Property prop)
            {
                switch (prop.Name)
                {
                    case "OutputAssembly":
                        assemblyName = Path.GetFileNameWithoutExtension(prop.Value);
                        break;
                    case "DefineConstants":
                        foreach (var d in prop.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                            defines.Add(d.Trim());
                        break;
                    case "NoWarn":
                        foreach (var w in prop.Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                            noWarn.Add(NormalizeWarningCode(w.Trim()));
                        break;
                    case "TargetType":
                        targetType = prop.Value;
                        break;
                    case "LangVersion":
                        langVersion = prop.Value;
                        break;
                    case "CommandLineArguments":
                        ParseCommandLineArguments(prop.Value, noWarn);
                        break;
                    case "Nullable":
                    case "Unsafe":
                    case "CheckForOverflowUnderflow":
                    case "Deterministic":
                    case "HighEntropyVA":
                    case "Optimize":
                    case "AllowUnsafeBlocks":
                        flags.Add($"/{prop.Name.ToLowerInvariant()}:{prop.Value}");
                        break;
                }
            }
            else if (child is Parameter param)
            {
                switch (param.Name)
                {
                    case "Sources":
                        foreach (var item in param.Children.OfType<Item>())
                        {
                            var normalized = NormalizePath(item.Text, repoRoot, projectDirectory);
                            sourceFiles.Add(normalized);
                            var diskPath = Path.IsPathRooted(item.Text)
                                ? item.Text
                                : Path.GetFullPath(Path.Combine(projectDirectory, item.Text));
                            sourceFileOriginalPaths.TryAdd(normalized, diskPath);
                        }
                        break;
                    case "References" or "ReferencePath":
                        foreach (var item in param.Children.OfType<Item>())
                            references.Add(Path.GetFileNameWithoutExtension(item.Text));
                        break;
                    case "Analyzers":
                        foreach (var item in param.Children.OfType<Item>())
                            analyzers.Add(Path.GetFileNameWithoutExtension(item.Text));
                        break;
                }
            }
        }

        if (assemblyName is null)
            return null;

        // The modern Csc task puts /nowarn flags in CommandLineArguments
        // which appears as a Message in the task, not a Property in Parameters.
        // Fall back to parsing messages if we didn't find NoWarn as a property.
        if (noWarn.Count == 0)
        {
            task.VisitAllChildren<Property>(p =>
            {
                if (p.Name == "CommandLineArguments")
                    ParseCommandLineArguments(p.Value, noWarn);
            });
        }

        // Also check task messages for the response file content
        if (noWarn.Count == 0)
        {
            task.VisitAllChildren<Message>(msg =>
            {
                if (msg.Text?.Contains("/nowarn:", StringComparison.Ordinal) == true)
                    ParseCommandLineArguments(msg.Text, noWarn);
            });
        }

        return new ManagedCompilationRecord
        {
            AssemblyName = assemblyName,
            SourceFiles = sourceFiles,
            SourceFileOriginalPaths = sourceFileOriginalPaths,
            Defines = defines,
            References = references,
            NoWarn = noWarn,
            Analyzers = analyzers,
            Flags = flags,
            TargetType = targetType,
            LangVersion = langVersion,
            BuildSystem = "msbuild",
        };
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
        var root = Path.GetFullPath(repoRoot).TrimEnd('/') + "/";
        if (normalized.StartsWith(root, StringComparison.Ordinal))
            return normalized[root.Length..];

        return normalized;
    }

    /// <summary>
    /// Parse /nowarn: flags from the Csc CommandLineArguments or response file text.
    /// Modern MSBuild puts NoWarn values here rather than as a separate property.
    /// </summary>
    private static void ParseCommandLineArguments(string commandLine, SortedSet<string> noWarn)
    {
        var span = commandLine.AsSpan();
        int idx;
        while ((idx = span.IndexOf("/nowarn:", StringComparison.Ordinal)) >= 0)
        {
            span = span[(idx + 8)..];
            // Read until whitespace or end of string
            var end = span.IndexOfAny([' ', '\t', '\r', '\n']);
            var value = end >= 0 ? span[..end] : span;
            foreach (var range in value.Split(','))
            {
                var code = value[range].Trim();
                if (!code.IsEmpty)
                    noWarn.Add(NormalizeWarningCode(code.ToString()));
            }
            if (end >= 0) span = span[end..];
            else break;
        }
    }

    /// <summary>
    /// Normalize warning codes to a consistent CS-prefixed format.
    /// MSBuild sometimes emits bare numbers (e.g. "1701") and sometimes
    /// prefixed codes (e.g. "CS1701"). Normalize to always use "CS" prefix
    /// for numeric codes so they match Bazel's format.
    /// </summary>
    private static string NormalizeWarningCode(string code)
    {
        if (code.Length > 0 && char.IsDigit(code[0]))
            return "CS" + code;

        return code;
    }
}
