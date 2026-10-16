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
/// Executes a prepared C# invocation with an explicit compiler and runtime host.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class CompileCsc : ToolTask
{
    public CompileCsc() : base(ErrorString.ResourceManager)
    {
    }

    public ITaskItem[] DeclaredInputs { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] DeclaredOutputs { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string DotNetHost { get; set; } = "";

    [Required]
    public string CompilerAssembly { get; set; } = "";

    [Required]
    public string CompilerArguments { get; set; } = "";

    [Required]
    public ITaskItem[] PathMappings { get; set; } = Array.Empty<ITaskItem>();

    protected override string ToolName => Path.GetFileName(DotNetHost);
    protected override string GenerateFullPathToTool() => DotNetHost;
    protected override Encoding StandardOutputEncoding => Encoding.UTF8;
    protected override Encoding StandardErrorEncoding => Encoding.UTF8;

    protected override bool ValidateParameters()
    {
        var projectDirectory = TaskEnvironment.ProjectDirectory.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.IsPathRooted(DotNetHost) || !Path.IsPathRooted(CompilerAssembly) ||
            !DeclaredInputs.Any(input => input.ItemSpec == DotNetHost) ||
            !DeclaredInputs.Any(input => input.ItemSpec == CompilerAssembly) ||
            UseCommandProcessor || EnvironmentVariables?.Length > 0 ||
            !string.IsNullOrEmpty(ToolPath) || ToolExe != ToolName || HostObject != null ||
            TaskEnvironment.GetEnvironmentVariable("MSBUILDPRESERVETOOLTEMPFILES") == "1" ||
            PathMappings.Length == 0 ||
            !PathMappings.Any(root => projectDirectory.StartsWith(root.ItemSpec, comparison)) ||
            PathMappings.Any(root => !Path.IsPathRooted(root.ItemSpec) ||
                !root.ItemSpec.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                !root.GetMetadata("MappedPath").StartsWith("/_", StringComparison.Ordinal) ||
                !root.GetMetadata("MappedPath").EndsWith("/", StringComparison.Ordinal)) ||
            CommandLineUtilities.SplitCommandLineIntoArguments(CompilerArguments, removeHashComments: true)
                .Any(argument => argument.TrimStart('"').StartsWith("@", StringComparison.Ordinal)))
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
        commandLine.AppendFileNameIfNotNull(CompilerAssembly);
        // This must be on the direct command line, not inside the temporary response file.
        commandLine.AppendSwitch("/noconfig");
        return commandLine.ToString();
    }

    protected override string GenerateResponseFileCommands()
    {
        var commandLine = new CommandLineBuilder();
        commandLine.AppendSwitch("/deterministic+");
        var pathMap = string.Join(",", PathMappings.Select(root =>
            Escape(root.ItemSpec) + "=" + Escape(root.GetMetadata("MappedPath"))));
        commandLine.AppendSwitchIfNotNull("/pathmap:", pathMap);
        return CompilerArguments + "\n" + commandLine;
    }

    private static string Escape(string path) => path.Replace(",", ",,").Replace("=", "==");

    protected override ProcessStartInfo GetProcessStartInfo(string pathToTool, string commandLineCommands, string responseFileSwitch)
    {
        var info = base.GetProcessStartInfo(pathToTool, commandLineCommands, responseFileSwitch);
        info.WorkingDirectory = TaskEnvironment.ProjectDirectory.Value;
#if NETFRAMEWORK
        var systemRoot = info.EnvironmentVariables["SystemRoot"];
        info.EnvironmentVariables.Clear();
        if (systemRoot != null)
        {
            info.EnvironmentVariables["SystemRoot"] = systemRoot;
        }
#else
        info.Environment.TryGetValue("SystemRoot", out var systemRoot);
        info.Environment.Clear();
        if (systemRoot != null)
        {
            info.Environment["SystemRoot"] = systemRoot;
        }
#endif
        return info;
    }
}
