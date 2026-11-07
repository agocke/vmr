// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#nullable enable

namespace Microsoft.Build.Tasks;

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ResolveILLinkInputs : TaskExtension, IMultiThreadableTask
{
    public ITaskItem[] AssemblyPaths { get; set; } = [];
    public ITaskItem[] CompanionFiles { get; set; } = [];
    public ITaskItem[] ProducedFiles { get; set; } = [];
    public ITaskItem[] ToolFiles { get; set; } = [];
    public string ExtraArgs { get; set; } = "";
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Output]
    public ITaskItem[] Inputs { get; private set; } = [];

    public override bool Execute()
    {
        Inputs = [];
        try
        {
            SortedSet<string> inputs = new(StringComparer.Ordinal);
            HashSet<string> directories = new(FileUtilities.PathComparer);
            foreach (ITaskItem file in AssemblyPaths)
            {
                AddFileAndDirectory(file);
            }
            foreach (ITaskItem file in ToolFiles)
            {
                AddFileAndDirectory(file);
            }
            foreach (ITaskItem file in CompanionFiles)
            {
                inputs.Add(GetPath(file.ItemSpec));
            }

            // Producer outputs are already explicit graph inputs, not a filesystem inventory.
            foreach (ITaskItem file in ProducedFiles)
            {
                string path = GetPath(file.ItemSpec);
                foreach (string directory in directories)
                {
                    if (ILLinkInputPaths.IsUnder(path, directory))
                    {
                        inputs.Add(path);
                        break;
                    }
                }
            }

            Queue<string> arguments = new(ILLinkInputPaths.SplitArguments(ExtraArgs));
            while (arguments.Count > 0)
            {
                string option = arguments.Dequeue();
                switch (option)
                {
                    case "-x":
                    case "--substitutions":
                    case "--link-attributes":
                        inputs.Add(GetPath(ReadArgument()));
                        break;
                    case "--action":
                        ReadArgument();
                        if (arguments.Count > 0 && !arguments.Peek().StartsWith("-", StringComparison.Ordinal))
                        {
                            ReadArgument();
                        }
                        break;
                    case "--ignore-link-attributes":
                    case "--skip-unresolved":
                    case "--trim-mode":
                    case "--nowarn":
                    case "--disable-opt":
                    case "-b":
                        ReadArgument();
                        break;
                    case "--preserve-symbol-paths":
                        break;
                    default:
                        throw new ArgumentException($"Unsupported declared-I/O ILLink argument '{option}'; use explicit assembly and companion files, not search directories or response files.");
                }
            }

            List<ITaskItem> result = [];
            foreach (string input in inputs)
            {
                result.Add(new TaskItem(EscapingUtilities.Escape(input)));
            }
            Inputs = [.. result];
            return true;

            string ReadArgument()
            {
                string argument = arguments.Count > 0 ? arguments.Dequeue() :
                    throw new ArgumentException("Missing ILLink argument value.");
                if (argument.StartsWith("@", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Declared-I/O ILLink requires explicit files, not file lists.");
                }
                return argument;
            }

            void AddFileAndDirectory(ITaskItem file)
            {
                string path = GetPath(file.ItemSpec);
                inputs.Add(path);
                directories.Add(Path.GetDirectoryName(path)!);
            }
        }
        catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
        {
            Log.LogErrorFromException(ex);
            return false;
        }
    }

    private string GetPath(string value)
    {
        if (value.IndexOfAny(['*', '?', '\0']) >= 0)
        {
            throw new ArgumentException("ILLink requires explicit file paths.");
        }
        return FileUtilities.NormalizePath(TaskEnvironment.GetAbsolutePath(value)).Value;
    }
}

internal static class ILLinkInputPaths
{
    internal static bool IsUnder(string file, string directory)
        => file.StartsWith(FileUtilities.EnsureTrailingSlash(directory), FileUtilities.PathComparison);

    internal static IEnumerable<string> SplitArguments(string arguments)
    {
        int index = 0;
        while (index < arguments.Length)
        {
            while (index < arguments.Length && char.IsWhiteSpace(arguments[index]))
            {
                index++;
            }
            if (index == arguments.Length)
            {
                yield break;
            }

            bool quoted = arguments[index] == '"';
            int start = quoted ? ++index : index;
            while (index < arguments.Length && (quoted ? arguments[index] != '"' : !char.IsWhiteSpace(arguments[index])))
            {
                if (!quoted && arguments[index] == '"')
                {
                    throw new ArgumentException("Unsupported quote in ILLink arguments.");
                }
                index++;
            }
            if (quoted && index == arguments.Length)
            {
                throw new ArgumentException("Unterminated quoted ILLink argument.");
            }

            string argument = arguments.Substring(start, index - start);
            if (quoted && ++index < arguments.Length && !char.IsWhiteSpace(arguments[index]))
            {
                throw new ArgumentException("Invalid quoted ILLink argument.");
            }
            yield return argument;
        }
    }
}
