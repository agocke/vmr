// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.BuildTasks;

/// <summary>
/// Executes a prepared managed compiler invocation with an explicit compiler and runtime host.
/// </summary>
[MSBuildDeclaredIOTask(SandboxVersion = 1)]
[MSBuildMultiThreadableTask]
public sealed class CompileManaged : SandboxedToolTask
{
    public CompileManaged() : base(ErrorString.ResourceManager)
    {
    }

    public ITaskItem[] DeclaredOutputTrees { get; set; } = Array.Empty<ITaskItem>();
    public string SandboxArguments { get; set; } = "";
    public ITaskItem[] ArgumentPaths { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string DotNetHost { get; set; } = "";

    [Required]
    public string CompilerAssembly { get; set; } = "";

    [Required]
    public string CompilerArguments { get; set; } = "";

    [Required]
    public ITaskItem[] PathMappings { get; set; } = Array.Empty<ITaskItem>();

    protected override string ToolName => Path.GetFileName(DotNetHost);
    protected override string GenerateFullPathToTool() => MapInputPath(DotNetHost);
    protected override Encoding StandardOutputEncoding => Encoding.UTF8;
    protected override Encoding StandardErrorEncoding => Encoding.UTF8;

    protected override ITaskItem[] GetToolOutputs()
        => DeclaredOutputTrees.Length == 1
            ? DeclaredOutputs.Where(output => output.ItemSpec != DeclaredOutputTrees[0].GetMetadata("DyndepFile")).ToArray()
            : base.GetToolOutputs();

    protected override bool ValidateParameters()
    {
        var projectDirectory = TaskEnvironment.ProjectDirectory.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var arguments = CommandLineUtilities.SplitCommandLineIntoArguments(CompilerArguments, removeHashComments: true).ToArray();
        var generatedDirectories = arguments.Where(argument =>
            argument.StartsWith("/generatedfilesout:", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-generatedfilesout:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var generatedDirectory = new CommandLineBuilder();
        if (DeclaredOutputTrees.Length == 1)
        {
            generatedDirectory.AppendSwitchIfNotNull("/generatedfilesout:", DeclaredOutputTrees[0].ItemSpec);
        }
        if (!Path.IsPathRooted(DotNetHost) || !Path.IsPathRooted(CompilerAssembly) ||
            !DeclaredInputs.Any(input => input.ItemSpec == DotNetHost) ||
            !DeclaredInputs.Any(input => input.ItemSpec == CompilerAssembly) ||
            UseCommandProcessor || EnvironmentVariables?.Length > 0 ||
            !string.IsNullOrEmpty(ToolPath) || ToolExe != ToolName || HostObject != null ||
            TaskEnvironment.GetEnvironmentVariable("MSBUILDPRESERVETOOLTEMPFILES") == "1" ||
            DeclaredOutputTrees.Length > 1 ||
            DeclaredOutputTrees.Any(tree => !Path.IsPathRooted(tree.ItemSpec) ||
                !DeclaredOutputs.Any(output => output.ItemSpec == tree.GetMetadata("ArchiveFile")) ||
                !DeclaredOutputs.Any(output => output.ItemSpec == tree.GetMetadata("DyndepFile"))) ||
            generatedDirectories.Length != DeclaredOutputTrees.Length ||
            generatedDirectories.Any(argument =>
                !string.Equals(argument.Substring(argument.IndexOf(':') + 1),
                    generatedDirectory.ToString().Substring("/generatedfilesout:".Length), comparison)) ||
            arguments.Any(argument =>
                argument.StartsWith("/generatedfilesarchive", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-generatedfilesarchive", StringComparison.OrdinalIgnoreCase)) ||
            PathMappings.Length == 0 ||
            !PathMappings.Any(root => projectDirectory.StartsWith(root.ItemSpec, comparison)) ||
            PathMappings.Any(root => !Path.IsPathRooted(root.ItemSpec) ||
                !root.ItemSpec.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                !root.GetMetadata("MappedPath").StartsWith("/_", StringComparison.Ordinal) ||
                !root.GetMetadata("MappedPath").EndsWith("/", StringComparison.Ordinal)) ||
            arguments.Any(argument => argument.TrimStart('"').StartsWith("@", StringComparison.Ordinal)))
        {
            Log.LogErrorFromResources("CompileCsc.ExplicitToolInputs");
            return false;
        }
        if ((UseSandbox || IsSandboxed) && (string.IsNullOrEmpty(SandboxArguments) || ArgumentPaths.Length == 0 ||
            ArgumentPaths.Any(item => item.GetMetadata("Role") is not ("Input" or "Output") ||
                !SandboxArguments.Contains("\"" + item.GetMetadata("Token") + "\""))))
        {
            Log.LogErrorFromResources("CompileCsc.ExplicitToolInputs");
            return false;
        }

        return base.ValidateParameters();
    }

    protected override string GenerateCommandLineCommands()
    {
        var commandLine = new CommandLineBuilder();
        commandLine.AppendSwitch("exec");
        commandLine.AppendSwitch("--roll-forward");
        commandLine.AppendSwitch("Major");
        commandLine.AppendFileNameIfNotNull(MapInputPath(CompilerAssembly));
        // This must be on the direct command line, not inside the temporary response file.
        commandLine.AppendSwitch("/noconfig");
        return commandLine.ToString();
    }

    protected override string GenerateResponseFileCommands()
    {
        var commandLine = new CommandLineBuilder();
        commandLine.AppendSwitch("/deterministic+");
        var pathMap = string.Join(",", PathMappings.Select(root =>
            Escape(MapDirectory(root.ItemSpec).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar) +
            "=" + Escape(root.GetMetadata("MappedPath"))));
        if (IsSandboxed && DeclaredOutputTrees.Length == 1)
        {
            // Archive mode retains logical generated-source paths; only physical archive writes move.
            pathMap += "," + string.Join(",", PathMappings.Select(root =>
                Escape(root.ItemSpec) + "=" + Escape(root.GetMetadata("MappedPath"))));
        }
        commandLine.AppendSwitchIfNotNull("/pathmap:", pathMap);
        if (DeclaredOutputTrees.Length == 1)
        {
            commandLine.AppendSwitchIfNotNull("/generatedfilesarchive:", MapOutputPath(DeclaredOutputTrees[0].GetMetadata("ArchiveFile")));
        }
        var arguments = CompilerArguments;
        if (IsSandboxed)
        {
            arguments = SandboxArguments;
            foreach (var item in ArgumentPaths)
            {
                var path = item.GetMetadata("Role") == "Input" ? MapInputPath(item.ItemSpec) : MapOutputPath(item.ItemSpec);
                var quoted = new CommandLineBuilder();
                quoted.AppendFileNameIfNotNull(path);
                arguments = arguments.Replace("\"" + item.GetMetadata("Token") + "\"", quoted.ToString());
            }
        }
        return arguments + "\n" + commandLine;
    }

    private static string Escape(string path) => path.Replace(",", ",,").Replace("=", "==");

    protected override ProcessStartInfo GetProcessStartInfo(string pathToTool, string commandLineCommands, string responseFileSwitch)
    {
        var info = base.GetProcessStartInfo(pathToTool, commandLineCommands, responseFileSwitch);
        info.WorkingDirectory = MapDirectory(TaskEnvironment.ProjectDirectory.Value);
#if NETFRAMEWORK
        var environment = info.EnvironmentVariables;
#else
        var environment = info.Environment;
#endif
        environment.Clear();
        foreach (var name in new[] { "SystemRoot", "TMPDIR", "TMP", "TEMP", "DOTNET_EnableDiagnostics" })
        {
            var value = TaskEnvironment.GetEnvironmentVariable(name);
            if (value != null)
            {
                environment[name] = value;
            }
        }
        if (IsSandboxed)
        {
            foreach (var name in new[] { "TMPDIR", "TMP", "TEMP" })
            {
                environment[name] = SandboxTemporaryDirectory;
            }
        }
        return info;
    }
}
