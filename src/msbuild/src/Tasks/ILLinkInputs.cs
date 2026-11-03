// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#nullable enable

namespace Microsoft.Build.Tasks;

public sealed class WriteILLinkInputs : TaskExtension
{
    public ITaskItem[] AssemblyPaths { get; set; } = [];
    public ITaskItem[] ExcludedDirectories { get; set; } = [];

    [Required]
    public string OutputFile { get; set; } = null!;

    public override bool Execute()
    {
        try
        {
            HashSet<string> directories = new(FileUtilities.PathComparer);
            Dictionary<string, XElement> assemblies = new(FileUtilities.PathComparer);
            XElement inventory = new("ILLinkInputs", new XAttribute("Version", "1"));
            foreach (ITaskItem assembly in AssemblyPaths)
            {
                string path = Path.GetFullPath(assembly.ItemSpec);
                bool excluded = false;
                foreach (ITaskItem exclusion in ExcludedDirectories)
                {
                    if (ILLinkInputPaths.IsUnder(path, Path.GetFullPath(exclusion.ItemSpec)))
                    {
                        excluded = true;
                        break;
                    }
                }

                if (!excluded && File.Exists(path))
                {
                    AddDirectory(Path.GetDirectoryName(path)!);
                    AddAssembly(path);
                }
            }

            XElement project = new("Project", new XElement("PropertyGroup",
                new XElement("_ILLinkInputsData", EscapingUtilities.Escape(inventory.ToString(SaveOptions.DisableFormatting)))));
            string content = project + "\n";
            if (!File.Exists(OutputFile) || File.ReadAllText(OutputFile) != content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputFile))!);
                File.WriteAllText(OutputFile, content);
            }

            return true;

            void AddDirectory(string directory)
            {
                if (!directories.Add(directory))
                {
                    return;
                }

                XElement element = new("Directory", new XAttribute("Path", directory));
                inventory.Add(element);
                List<string> candidates = [];
                foreach (string candidate in Directory.EnumerateFiles(directory))
                {
                    if (ILLinkInputPaths.IsAssembly(candidate))
                    {
                        candidates.Add(Path.GetFullPath(candidate));
                    }
                }

                candidates.Sort(StringComparer.Ordinal);
                foreach (string candidate in candidates)
                {
                    element.Add(new XElement("Candidate", new XAttribute("Path", candidate)));
                    AddAssembly(candidate);
                }
            }

            void AddAssembly(string path)
            {
                path = Path.GetFullPath(path);
                if (assemblies.ContainsKey(path))
                {
                    return;
                }

                XElement element = new("Assembly", new XAttribute("Path", path));
                assemblies.Add(path, element);
                inventory.Add(element);
                AddFile(path);
                AddOptionalFile(path + ".config");
                AddOptionalFile(path + ".mdb");
                AddOptionalFile(Path.ChangeExtension(path, ".pdb"));
                string directory = Path.GetDirectoryName(path)!;
                string satelliteName = Path.GetFileNameWithoutExtension(path) + ".resources.dll";
                List<string> satelliteDirectories = [.. Directory.EnumerateDirectories(directory)];
                satelliteDirectories.Sort(StringComparer.Ordinal);
                foreach (string satelliteDirectory in satelliteDirectories)
                {
                    AddOptionalFile(Path.Combine(satelliteDirectory, satelliteName));
                }

                using FileStream input = File.OpenRead(path);
                using PEReader pe = new(input);
                if (!pe.HasMetadata)
                {
                    throw new BadImageFormatException("ILLink input has no managed metadata.", path);
                }
                MetadataReader metadata = pe.GetMetadataReader();
                foreach (AssemblyFileHandle handle in metadata.AssemblyFiles)
                {
                    AssemblyFile file = metadata.GetAssemblyFile(handle);
                    string filePath = Path.GetFullPath(Path.Combine(directory, metadata.GetString(file.Name)));
                    AddFile(filePath);
                    if (file.ContainsMetadata)
                    {
                        AddAssembly(filePath);
                        element.Add(new XElement("Module", new XAttribute("Path", filePath)));
                    }
                }

                void AddFile(string file) => element.Add(new XElement("File", new XAttribute("Path", file)));

                void AddOptionalFile(string file)
                {
                    if (File.Exists(file))
                    {
                        AddFile(file);
                    }
                }
            }
        }
        catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex) || ex is BadImageFormatException or System.Xml.XmlException)
        {
            Log.LogErrorFromException(ex);
            return false;
        }
    }
}

[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ResolveILLinkInputs : TaskExtension, IMultiThreadableTask
{
    [Required]
    public string Data { get; set; } = null!;

    public ITaskItem[] AssemblyPaths { get; set; } = [];
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
            XElement inventory = XElement.Parse(Data);
            if (inventory.Name != "ILLinkInputs" || (string?)inventory.Attribute("Version") != "1")
            {
                throw new ArgumentException("Missing or unsupported prepared ILLink input inventory; run Restore.");
            }

            Dictionary<string, XElement> assemblies = new(FileUtilities.PathComparer);
            Dictionary<string, XElement> directories = new(FileUtilities.PathComparer);
            foreach (XElement element in inventory.Elements())
            {
                (element.Name == "Assembly" ? assemblies : directories).Add(PathOf(element), element);
            }

            HashSet<string> produced = new(FileUtilities.PathComparer);
            foreach (ITaskItem item in ProducedFiles)
            {
                produced.Add(GetPath(item.ItemSpec));
            }

            SortedSet<string> inputs = new(StringComparer.Ordinal);
            HashSet<string> visited = new(FileUtilities.PathComparer);
            foreach (ITaskItem assembly in AssemblyPaths)
            {
                AddAssembly(GetPath(assembly.ItemSpec));
            }

            foreach (ITaskItem file in ToolFiles)
            {
                string path = GetPath(file.ItemSpec);
                inputs.Add(path);
                AddProducedFiles(Path.GetDirectoryName(path)!);
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
                    case "-d":
                        string directory = GetPath(ReadArgument());
                        bool known = directories.TryGetValue(directory, out XElement? facts);
                        if (facts is not null)
                        {
                            foreach (XElement candidate in facts.Elements("Candidate"))
                            {
                                AddAssembly(PathOf(candidate));
                            }
                        }

                        foreach (string file in produced)
                        {
                            if (FileUtilities.PathComparer.Equals(Path.GetDirectoryName(file), directory) &&
                                ILLinkInputPaths.IsAssembly(file))
                            {
                                known = true;
                                AddAssembly(file);
                            }
                        }

                        if (!known)
                        {
                            throw new ArgumentException($"Unprepared ILLink search directory '{directory}'; run Restore with its assembly inputs.");
                        }
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
                        throw new ArgumentException($"Unsupported ILLink input-discovery argument '{option}'.");
                }
            }

            List<ITaskItem> result = [];
            foreach (string input in inputs)
            {
                result.Add(new TaskItem(EscapingUtilities.Escape(input)));
            }

            Inputs = [.. result];
            return true;

            string ReadArgument() => arguments.Count > 0 ? arguments.Dequeue() :
                throw new ArgumentException("Missing ILLink argument value.");

            void AddProducedFiles(string directory)
            {
                // Future PE/tool discovery is unavailable. Include producer files below
                // each assembly/tool directory, covering modules, satellites and runfiles.
                foreach (string file in produced)
                {
                    if (ILLinkInputPaths.IsUnder(file, directory))
                    {
                        inputs.Add(file);
                    }
                }
            }

            void AddAssembly(string path)
            {
                if (!visited.Add(path))
                {
                    return;
                }

                if (produced.Contains(path))
                {
                    inputs.Add(path);
                    AddProducedFiles(Path.GetDirectoryName(path)!);
                }
                else if (assemblies.TryGetValue(path, out XElement? facts))
                {
                    foreach (XElement file in facts.Elements("File"))
                    {
                        inputs.Add(PathOf(file));
                    }
                    foreach (XElement module in facts.Elements("Module"))
                    {
                        AddAssembly(PathOf(module));
                    }
                }
                else
                {
                    throw new ArgumentException($"Unprepared ILLink assembly '{path}'; run Restore or retain its producer in the graph.");
                }
            }
        }
        catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex) || ex is System.Xml.XmlException)
        {
            Log.LogErrorFromException(ex);
            return false;
        }
    }

    private string GetPath(string value) => FileUtilities.NormalizePath(TaskEnvironment.GetAbsolutePath(value)).Value;

    private static string PathOf(XElement element)
    {
        string path = (string?)element.Attribute("Path") ?? "";
        if (!Path.IsPathRooted(path) || path.IndexOfAny(['*', '?', '\0']) >= 0)
        {
            throw new ArgumentException("Invalid path in prepared ILLink inventory.");
        }
        return Path.GetFullPath(path);
    }
}

internal static class ILLinkInputPaths
{
    internal static bool IsAssembly(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".winmd", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUnder(string file, string directory)
        => file.StartsWith(FileUtilities.EnsureTrailingSlash(directory),
            FileUtilities.PathComparison);

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
