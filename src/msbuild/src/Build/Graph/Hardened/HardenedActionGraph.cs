// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;
#if NETFRAMEWORK
using Path = Microsoft.IO.Path;
#else
using Path = System.IO.Path;
#endif

namespace Microsoft.Build.Graph.Hardened;

internal sealed class HardenedActionGraph
{
    internal const string HostOSProperty = "MSBuildGraphHostOS";
    internal const string HostArchitectureProperty = "MSBuildGraphHostArchitecture";
    internal const string HostRuntimeIdentifierProperty = "MSBuildGraphHostRuntimeIdentifier";
    internal const string RequiredOutputDirectoryItem = "_MSBuildRequiredOutputDirectory";

    private static readonly HashSet<string> s_intrinsicFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Add", "Subtract", "Multiply", "Divide", "Modulo", "BitwiseAnd", "BitwiseOr",
        "BitwiseXor", "BitwiseNot", "LeftShift", "RightShift", "Escape", "Unescape",
        "EnsureTrailingSlash", "ValueOrDefault", "NormalizePath", "NormalizeDirectory",
        "VersionEquals", "VersionNotEquals", "VersionGreaterThan", "VersionGreaterThanOrEquals",
        "VersionLessThan", "VersionLessThanOrEquals",
        "GetTargetFrameworkIdentifier", "GetTargetFrameworkVersion", "IsTargetFrameworkCompatible",
        "GetTargetPlatformIdentifier", "GetTargetPlatformVersion", "FilterTargetFrameworks",
        nameof(IntrinsicFunctions.IsOSPlatform),
    };

    internal static string GetHostOS(string? value, IElementLocation location)
    {
        string hostOS =
            string.Equals(value, "windows", StringComparison.OrdinalIgnoreCase) ? "windows" :
            string.Equals(value, "linux", StringComparison.OrdinalIgnoreCase) ? "linux" :
            string.Equals(value, "osx", StringComparison.OrdinalIgnoreCase) ? "osx" :
            string.Equals(value, "freebsd", StringComparison.OrdinalIgnoreCase) ? "freebsd" :
            string.Empty;
        if (hostOS.Length == 0)
        {
            ProjectErrorUtilities.ThrowInvalidProject(
                location, "HardenedGraphUnsupportedConstruct",
                "missing or unsupported global MSBuildGraphHostOS; supply windows, linux, osx, or freebsd",
                "action graph construction");
        }

        return hostOS;
    }

    internal static Architecture GetHostArchitecture(string? value, IElementLocation location)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out Architecture architecture) ||
            !string.Equals(value, Enum.GetName(typeof(Architecture), architecture), StringComparison.OrdinalIgnoreCase))
        {
            ProjectErrorUtilities.ThrowInvalidProject(
                location, "HardenedGraphUnsupportedConstruct",
                "missing or unsupported global MSBuildGraphHostArchitecture; supply a named System.Runtime.InteropServices.Architecture value",
                "action graph construction");
        }

        return architecture;
    }

    internal static string GetHostRuntimeIdentifier(string? value, IElementLocation location)
    {
        bool valid = !string.IsNullOrEmpty(value);
        foreach (char character in value ?? string.Empty)
        {
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_'))
            {
                valid = false;
                break;
            }
        }

        if (!valid)
        {
            ProjectErrorUtilities.ThrowInvalidProject(
                location, "HardenedGraphUnsupportedConstruct",
                "missing or unsupported global MSBuildGraphHostRuntimeIdentifier; supply a nonempty RID containing only ASCII letters, digits, '.', '-', or '_'",
                "action graph construction");
        }

        return value!;
    }

    internal static bool IsHostContextProperty(string name)
        => MSBuildNameIgnoreCaseComparer.Default.Equals(name, HostOSProperty) ||
           MSBuildNameIgnoreCaseComparer.Default.Equals(name, HostArchitectureProperty) ||
           MSBuildNameIgnoreCaseComparer.Default.Equals(name, HostRuntimeIdentifierProperty);

    internal static bool IsAllowedFunction(Type receiver, string member)
    {
        string name = receiver.FullName ?? "";
        if (HardenedTargetValidator.IsProhibitedFunction(name.AsSpan(), member.AsSpan()))
        {
            return false;
        }

        return receiver.IsPrimitive || receiver.IsEnum ||
            (receiver.IsArray && string.Equals(member, nameof(Array.GetValue), StringComparison.OrdinalIgnoreCase)) ||
            name is "System.String" or "System.Math" or "System.MathF" or "System.Convert" or
                "System.Decimal" or "System.Version" or "System.Guid" or "System.DateTime" or
                "System.DateTimeOffset" or "System.TimeSpan" or "System.IO.Path" ||
            (receiver == typeof(RuntimeInformation) &&
                (string.Equals(member, nameof(RuntimeInformation.ProcessArchitecture), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(member, "RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))) ||
            (receiver == typeof(IntrinsicFunctions) && s_intrinsicFunctions.Contains(member));
    }

    private readonly string _destination;
    private readonly string _host;
    private readonly string _dotnet;
    private readonly string _directory;
    private readonly string _hostOS;
    private readonly Architecture _hostArchitecture;
    private readonly string _hostRuntimeIdentifier;
    private readonly Dictionary<string, string> _environment;
    private readonly List<ActionInvocation> _actions;
    private readonly Dictionary<string, ElementLocation> _requiredOutputDirectories;
    private readonly Dictionary<int, HardenedTargetValidator> _validators;
    private readonly ElementLocation _location;

    internal HardenedActionGraph(ProjectInstance project, TaskEnvironment environment, HardenedActionGraph? parent = null)
    {
        _location = project.ProjectFileLocation;
        _directory = project.Directory;
        Require(project.GlobalProperties.ContainsKey(HostOSProperty),
            $"{HostOSProperty} not supplied as a global construction input");
        _hostOS = GetHostOS(project.GlobalProperties[HostOSProperty], _location);
        Require(project.GlobalProperties.ContainsKey(HostArchitectureProperty),
            $"{HostArchitectureProperty} not supplied as a global construction input");
        _hostArchitecture = GetHostArchitecture(project.GlobalProperties[HostArchitectureProperty], _location);
        Require(project.GlobalProperties.ContainsKey(HostRuntimeIdentifierProperty),
            $"{HostRuntimeIdentifierProperty} not supplied as a global construction input");
        _hostRuntimeIdentifier = GetHostRuntimeIdentifier(project.GlobalProperties[HostRuntimeIdentifierProperty], _location);
        Require(project.GlobalProperties.ContainsKey("MSBuildEmitActionGraph"),
            "MSBuildEmitActionGraph set inside a project instead of as a global property");
        Require(Path.IsPathRooted(project.GetPropertyValue("MSBuildEmitActionGraph")),
            "a nonabsolute MSBuildEmitActionGraph destination");
        _destination = Path.GetFullPath(project.GetPropertyValue("MSBuildEmitActionGraph"));
        _host = project.GetPropertyValue("MSBuildActionTaskHost");
        _dotnet = project.GetPropertyValue("MSBuildActionDotNetHost");
        Dictionary<string, string> actionEnvironment = new(StringComparer.Ordinal);
        foreach (string name in ExpressionShredder.SplitSemiColonSeparatedList(project.GetPropertyValue("MSBuildActionEnvironment")))
        {
            string? value = environment.GetEnvironmentVariable(name);
            Require(value is not null, $"missing explicitly selected environment variable '{name}'");
            actionEnvironment.Add(name, value!);
        }

        _environment = actionEnvironment;
        if (parent is not null)
        {
            Require(FileUtilities.PathsEqual(_destination, parent._destination) &&
                FileUtilities.PathsEqual(_host, parent._host) &&
                FileUtilities.PathsEqual(_dotnet, parent._dotnet) &&
                _hostOS == parent._hostOS &&
                _hostArchitecture == parent._hostArchitecture &&
                _hostRuntimeIdentifier == parent._hostRuntimeIdentifier,
                "a child project changing action graph destination, hosts, or host context");
            Require(_environment.Count == parent._environment.Count,
                "a child project changing the action environment");
            foreach (KeyValuePair<string, string> variable in _environment)
            {
                Require(parent._environment.TryGetValue(variable.Key, out string? value) && value == variable.Value,
                    "a child project changing the action environment");
            }
        }

        _actions = parent?._actions ?? [];
        _requiredOutputDirectories = parent?._requiredOutputDirectories ?? new(FileUtilities.PathComparer);
        _validators = parent?._validators ?? [];
        Require(!NativeMethodsShared.IsWindows, "Windows action command quoting");
        Require(Path.IsPathRooted(_host) && File.Exists(_host), "a missing or nonabsolute MSBuildActionTaskHost");
        Require(Path.IsPathRooted(_dotnet) && File.Exists(_dotnet), "a missing or nonabsolute MSBuildActionDotNetHost");
    }

    internal TaskEnvironment CreateConstructionEnvironment()
        => TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(
            _directory, _environment);

    internal HardenedTargetValidator GetValidator(int configurationId)
    {
        if (!_validators.TryGetValue(configurationId, out HardenedTargetValidator? validator))
        {
            validator = new HardenedTargetValidator();
            _validators.Add(configurationId, validator);
        }

        return validator;
    }

    internal void RequireOutputDirectories(IEnumerable<ProjectItemInstance> directories, ElementLocation location)
    {
        foreach (ProjectItemInstance directory in directories)
        {
            string path = Path.TrimEndingDirectorySeparator(CanonicalPath(directory.EvaluatedInclude, location));
            if (!_requiredOutputDirectories.ContainsKey(path))
            {
                _requiredOutputDirectories.Add(path, location);
            }
        }
    }

    // Capture both concrete parameter interpretations; only the executor knows the parameter types.
    internal bool Record(ProjectInstance project, ProjectTaskInstance node, ItemBucket bucket, LoggingContext loggingContext, ITaskHost? hostObject)
    {
        Dictionary<string, (string, ElementLocation)> expressions = new(node.ParametersForBuild, MSBuildNameIgnoreCaseComparer.Default);
        Require(expressions.TryGetValue("DeclaredInputs", out var input), "missing DeclaredInputs", node.Location);
        Require(expressions.TryGetValue("DeclaredOutputs", out var output), "missing DeclaredOutputs", node.Location);
        string[] inputs = Paths([.. bucket.Expander.ExpandIntoTaskItemsLeaveEscaped(input.Item1, ExpanderOptions.ExpandAll, input.Item2)]);
        string[] outputs = Paths([.. bucket.Expander.ExpandIntoTaskItemsLeaveEscaped(output.Item1, ExpanderOptions.ExpandAll, output.Item2)]);
        if (inputs.Length == 0 && outputs.Length == 0)
        {
            return false;
        }

        Require(outputs.Length != 0, "a file-I/O action without outputs", node.Location);
        Require(node.Outputs.Count == 0, "Output mappings on file-I/O actions", node.Location);
        Require(hostObject is null, "task host objects", node.Location);
        TaskHostParameters identity = TaskBuilder.GatherTaskIdentityParameters(node, bucket.Expander);
        bool registered = project.TaskRegistry.TryGetRegisteredTaskForAction(
            node.Name, identity, true, loggingContext, out AssemblyLoadInfo assembly, out string typeName);
        if (!registered)
        {
            registered = project.TaskRegistry.TryGetRegisteredTaskForAction(
                node.Name, identity, false, loggingContext, out assembly, out typeName);
        }

        if (!registered && TaskClassRegistry.TryGetRegistration(node.Name, out TaskClassRegistration? registration) &&
            registration.TryGetLoadedTypeWithoutConstruction(out LoadedType? loaded))
        {
            assembly = AssemblyLoadInfo.Create(null, loaded.Type.Assembly.Location);
            typeName = loaded.Type.FullName!;
        }

        Require(assembly is not null && !string.IsNullOrEmpty(assembly.AssemblyFile),
            "a task without an explicit assembly registration, a custom task factory, or specialized hosting", node.Location);
        string taskAssembly = CanonicalPath(assembly!.AssemblyFile, node.Location);
        foreach (string path in outputs)
        {
            Require(!Directory.Exists(path), $"directory output '{path}'", node.Location);
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter json = new(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 2);
            json.WriteString("taskAssembly", taskAssembly);
            json.WriteString("taskType", typeName);
            json.WriteString("workingDirectory", _directory);
            json.WriteString("projectFile", node.Location.File);
            json.WriteNumber("lineNumber", node.Location.Line);
            json.WriteNumber("columnNumber", node.Location.Column);
            json.WriteStartObject("environment");
            foreach (string name in SortedKeys(_environment))
            {
                json.WriteString(name, _environment[name]);
            }

            json.WriteEndObject();
            json.WriteStartObject("parameters");
            foreach (string name in SortedKeys(expressions))
            {
                json.WritePropertyName(name);
                (string expression, ElementLocation location) = expressions[name];
                json.WriteStartObject();
                json.WriteString("scalar", bucket.Expander.ExpandIntoStringAndUnescape(expression, ExpanderOptions.ExpandAll, location));
                json.WritePropertyName("items");
                IList<TaskItem>? items = null;
                try
                {
                    items = bucket.Expander.ExpandIntoTaskItemsLeaveEscaped(expression, ExpanderOptions.ExpandAll, location);
                }
                catch (InvalidProjectFileException)
                {
                    // A valid scalar expression need not be a valid item-vector expression.
                }

                if (items is null)
                {
                    json.WriteNullValue();
                }
                else
                {
                    json.WriteStartArray();
                    foreach (TaskItem item in items)
                    {
                        WriteItem(json, item);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        _actions.Add(new(stream.ToArray(), [.. inputs, taskAssembly], outputs, taskAssembly));
        return true;
    }

    internal void Publish()
    {
        Dictionary<string, int> producers = new(FileUtilities.PathComparer);
        for (int i = 0; i < _actions.Count; i++)
        {
            foreach (string output in _actions[i].Outputs)
            {
                foreach (string existing in producers.Keys)
                {
                    Require(!Conflicts(existing, output), $"conflicting outputs '{existing}' and '{output}'");
                }

                Require(!Conflicts(output, _destination) && !Conflicts(output, _destination + ".actions"),
                    $"task output '{output}' overlapping graph metadata");
                producers.Add(output, i);
            }
        }

        if (_requiredOutputDirectories.Count > 0)
        {
            HashSet<string> outputParents = new(FileUtilities.PathComparer);
            foreach (string output in producers.Keys)
            {
                string? parent = Path.GetDirectoryName(output);
                while (parent is not null && outputParents.Add(parent))
                {
                    parent = Path.GetDirectoryName(parent);
                }
            }

            foreach (KeyValuePair<string, ElementLocation> requirement in _requiredOutputDirectories)
            {
                Require(outputParents.Contains(requirement.Key),
                    $"directory requirement '{requirement.Key}' without a declared file output beneath it; standalone directory effects are unsupported",
                    requirement.Value);
            }
        }

        for (int i = 0; i < _actions.Count; i++)
        {
            foreach (string input in _actions[i].Inputs)
            {
                if (producers.TryGetValue(input, out int producer))
                {
                    Require(producer < i, $"in-place, cyclic, or forward-produced input '{input}'");
                }
                else
                {
                    Require(File.Exists(input) && !Directory.Exists(input), $"missing regular-file input '{input}'");
                    foreach (string output in producers.Keys)
                    {
                        Require(!Conflicts(input, output), $"overlapping input/output paths '{input}' and '{output}'");
                    }
                }
            }
        }

        string manifestDirectory = _destination + ".actions";
        StringBuilder ninja = new("ninja_required_version = 1.10\n");
        ninja.Append("msbuild_construction_host_os = ").Append(_hostOS).Append('\n');
        ninja.Append("msbuild_construction_host_architecture = ").Append(_hostArchitecture.ToString().ToLowerInvariant()).Append('\n');
        ninja.Append("msbuild_construction_host_runtime_identifier = ").Append(_hostRuntimeIdentifier).Append("\n\n");
        List<string> previousOutputs = [];
        List<string> allOutputs = [];
        List<(string Path, byte[] Contents)> manifests = [];
        for (int i = 0; i < _actions.Count; i++)
        {
            ActionInvocation action = _actions[i];
            using SHA256 hash = SHA256.Create();
            string digest = BitConverter.ToString(hash.ComputeHash(action.Contents)).Replace("-", "").ToLowerInvariant();
            string manifest = Path.Combine(manifestDirectory, $"{i}-{digest}.json");
            manifests.Add((manifest, action.Contents));
            string rule = "action_" + i.ToString(CultureInfo.InvariantCulture);
            ninja.Append("rule ").Append(rule).Append("\n  command = ")
                .Append(Command(_dotnet)).Append(' ').Append(Command(_host))
                .Append(" --invocation ").Append(Command(manifest)).Append('\n');
            ninja.Append("build");
            AppendPaths(ninja, action.Outputs);
            ninja.Append(": ").Append(rule);
            AppendPaths(ninja, action.Inputs);
            ninja.Append(" | ").Append(EscapePath(manifest));
            HashSet<string> tools = new(FileUtilities.PathComparer) { _host, _dotnet };
            AddToolDirectory(tools, Path.GetDirectoryName(_host)!);
            if (!producers.ContainsKey(action.Assembly))
            {
                AddToolDirectory(tools, Path.GetDirectoryName(action.Assembly)!);
            }
            List<string> sortedTools = [.. tools];
            sortedTools.Sort(StringComparer.Ordinal);
            AppendPaths(ninja, sortedTools);
            if (previousOutputs.Count != 0)
            {
                ninja.Append(" ||");
                AppendPaths(ninja, previousOutputs);
            }

            ninja.Append("\n\n");
            previousOutputs = [.. action.Outputs];
            allOutputs.AddRange(action.Outputs);
        }

        if (allOutputs.Count != 0)
        {
            ninja.Append("default");
            AppendPaths(ninja, allOutputs);
            ninja.Append('\n');
        }

        // Validate and format the entire graph before publishing any executable entry point.
        Directory.CreateDirectory(manifestDirectory);
        foreach ((string path, byte[] contents) in manifests)
        {
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, contents);
            }
        }

        string text = ninja.ToString();
        if (File.Exists(_destination) && File.ReadAllText(_destination) == text)
        {
            return;
        }

        string temporary = _destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(_destination))
            {
                File.Replace(temporary, _destination, null);
            }
            else
            {
                File.Move(temporary, _destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteItem(Utf8JsonWriter json, ITaskItem item)
    {
        json.WriteStartObject();
        json.WriteString("itemSpec", item.ItemSpec);
        json.WriteStartObject("metadata");
        IDictionary metadata = item.CloneCustomMetadata();
        List<string> names = [];
        foreach (string name in metadata.Keys)
        {
            names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
        {
            json.WriteString(name, (string)metadata[name]!);
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private string[] Paths(ITaskItem[] items)
    {
        string[] paths = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            paths[i] = CanonicalPath(items[i].ItemSpec);
        }

        return paths;
    }

    private string CanonicalPath(string path, ElementLocation? location = null)
    {
        Require(!string.IsNullOrWhiteSpace(path) && path.IndexOfAny(['*', '?']) < 0, $"nonexact declared path '{path}'", location);
        return Path.GetFullPath(Path.Combine(_directory, path));
    }

    private static List<string> SortedKeys<T>(IReadOnlyDictionary<string, T> dictionary)
    {
        List<string> keys = [.. dictionary.Keys];
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static bool Conflicts(string first, string second)
        => FileUtilities.PathsEqual(first, second)
            || first.StartsWith(second.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, FileUtilities.PathComparison)
            || second.StartsWith(first.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, FileUtilities.PathComparison);

    private static void AddToolDirectory(HashSet<string> tools, string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (Path.GetExtension(file) is ".dll" or ".json" or ".so" or ".dylib")
            {
                tools.Add(file);
            }
        }
    }

    private static void AppendPaths(StringBuilder ninja, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            ninja.Append(' ').Append(EscapePath(path));
        }
    }

    private string Command(string value)
    {
        Require(value.IndexOfAny(['\r', '\n']) < 0, "newlines in action command paths");
        return "'" + value.Replace("'", "'\"'\"'").Replace("$", "$$") + "'";
    }

    private static string EscapePath(string path)
    {
        ProjectErrorUtilities.VerifyThrowInvalidProject(
            path.IndexOfAny(['\r', '\n', '\t', '|', '#']) < 0,
            ElementLocation.EmptyLocation, "HardenedGraphUnsupportedConstruct", $"Ninja path '{path}'", "action graph construction");
        return path.Replace("$", "$$").Replace(" ", "$ ").Replace(":", "$:");
    }

    private void Require(bool condition, string construct, ElementLocation? location = null)
        => ProjectErrorUtilities.VerifyThrowInvalidProject(condition, location ?? _location,
            "HardenedGraphUnsupportedConstruct", construct, "action graph construction");

    private sealed record ActionInvocation(byte[] Contents, string[] Inputs, string[] Outputs, string Assembly);
}
