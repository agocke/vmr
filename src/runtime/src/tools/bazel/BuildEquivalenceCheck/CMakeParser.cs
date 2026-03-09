// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace BuildEquivalenceCheck;

/// <summary>
/// Parses CMake compile_commands.json into normalized NativeCompilationRecords.
/// </summary>
public static class CMakeParser
{
    public static List<NativeCompilationRecord> Parse(string compileCommandsPath, string repoRoot)
    {
        var records = new List<NativeCompilationRecord>();
        var json = File.ReadAllText(compileCommandsPath);
        using var doc = JsonDocument.Parse(json);

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var command = entry.GetProperty("command").GetString()!;
            var file = entry.GetProperty("file").GetString()!;

            var sourceFile = NormalizePath(file, repoRoot);
            var record = ParseCommand(command, sourceFile, repoRoot);
            records.Add(record);
        }

        return records;
    }

    private static NativeCompilationRecord ParseCommand(string command, string sourceFile, string repoRoot)
    {
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var undefines = new SortedSet<string>(StringComparer.Ordinal);
        var includes = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        string langStd = "";
        string optLevel = "";
        string target = "";

        var tokens = TokenizeCommand(command);

        for (int i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            if (tok.StartsWith("-D"))
            {
                defines.Add(tok[2..]);
            }
            else if (tok.StartsWith("-U"))
            {
                undefines.Add(tok[2..]);
            }
            else if (tok.StartsWith("-I"))
            {
                var path = tok.Length > 2 ? tok[2..] : (i + 1 < tokens.Count ? tokens[++i] : "");
                includes.Add(NormalizePath(path, repoRoot));
            }
            else if (tok.StartsWith("-std="))
            {
                langStd = tok;
            }
            else if (tok.StartsWith("-O"))
            {
                optLevel = tok;
            }
            else if (tok == "-o" && i + 1 < tokens.Count)
            {
                target = InferTargetFromOutput(tokens[++i]);
            }
            else if (tok is "-c" or "-MF" or "-MQ" or "-MT")
            {
                if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                    i++; // skip argument
            }
            else if (tok is "-MD" or "-MMD")
            {
                // standalone flags, no argument to skip
            }
            else if (tok.StartsWith("-W") || tok.StartsWith("-f") || tok.StartsWith("-g") || tok.StartsWith("-m"))
            {
                flags.Add(tok);
            }
        }

        return new NativeCompilationRecord
        {
            SourceFile = sourceFile,
            Target = target,
            Defines = defines,
            Undefines = undefines,
            IncludePaths = includes,
            Flags = flags,
            LanguageStandard = langStd,
            OptimizationLevel = optLevel,
            BuildSystem = "cmake",
        };
    }

    private static string InferTargetFromOutput(string outputPath)
    {
        // e.g. "hostcommon/CMakeFiles/libhostcommon.dir/__/json_parser.cpp.o"
        // → extract "libhostcommon" from the CMakeFiles directory name
        var parts = outputPath.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "CMakeFiles" && i + 1 < parts.Length)
            {
                var dir = parts[i + 1];
                return dir.EndsWith(".dir") ? dir[..^4] : dir;
            }
        }

        return Path.GetDirectoryName(outputPath) ?? "";
    }

    private static List<string> TokenizeCommand(string command)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < command.Length)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i]))
                i++;
            if (i >= command.Length) break;

            if (command[i] == '"')
            {
                i++;
                var start = i;
                while (i < command.Length && command[i] != '"')
                {
                    if (command[i] == '\\' && i + 1 < command.Length)
                        i++;
                    i++;
                }
                tokens.Add(command[start..i]);
                if (i < command.Length) i++;
            }
            else if (command[i] == '\'')
            {
                i++;
                var start = i;
                while (i < command.Length && command[i] != '\'')
                    i++;
                tokens.Add(command[start..i]);
                if (i < command.Length) i++;
            }
            else
            {
                var start = i;
                while (i < command.Length && !char.IsWhiteSpace(command[i]))
                    i++;
                tokens.Add(command[start..i]);
            }
        }

        return tokens;
    }

    internal static string NormalizePath(string path, string repoRoot)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Make absolute if relative
        if (!Path.IsPathRooted(path))
            return path;

        // Strip repo root prefix to get repo-relative path
        var normalized = Path.GetFullPath(path);
        var root = Path.GetFullPath(repoRoot).TrimEnd('/') + "/";
        if (normalized.StartsWith(root, StringComparison.Ordinal))
            return normalized[root.Length..];

        return normalized;
    }
}
