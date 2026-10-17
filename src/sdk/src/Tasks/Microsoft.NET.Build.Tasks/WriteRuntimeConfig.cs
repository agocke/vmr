// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

/// <summary>
/// Writes runtime configuration from explicit metadata and declared JSON inputs.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class WriteRuntimeConfig : GenerateRuntimeConfigurationFiles
{
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    protected override void ExecuteCore()
    {
        if (!ValidateOptions())
        {
            return;
        }

        if ((GenerateProbingPathsToRuntimeConfigDevFile || GenerateHotReloadRuntimeOptionsToRuntimeConfigDevFile) &&
            string.IsNullOrEmpty(RuntimeConfigDevPath))
        {
            throw new BuildErrorException(Strings.UnsupportedRuntimeConfigParameter, nameof(RuntimeConfigDevPath), RuntimeConfigDevPath);
        }

        var lockFile = LockFileCache.ReadLockFile(Input(AssetsFilePath), Log);
        var configurations = CreateConfigurations(lockFile).ToArray();
        var outputs = new HashSet<string>(configurations.Select(configuration => TaskEnvironment.GetAbsolutePath(configuration.Path).Value), PathComparer);
        if (outputs.Count != configurations.Length || DeclaredOutputs.Length != outputs.Count ||
            !outputs.SetEquals(DeclaredOutputs.Select(item => TaskEnvironment.GetAbsolutePath(item.ItemSpec).Value)))
        {
            throw new BuildErrorException(Strings.UnsupportedRuntimeConfigParameter, nameof(DeclaredOutputs), string.Join(";", DeclaredOutputs.Select(item => item.ItemSpec)));
        }

        foreach (var configuration in configurations)
        {
            NormalizeProbingPaths(configuration.Content);
        }

        foreach (var configuration in configurations)
        {
            WriteConfiguration(configuration.Path, configuration.Content, "\n");
        }
    }

    protected override JsonObject? ReadUserRuntimeOptions() =>
        string.IsNullOrEmpty(UserRuntimeConfig) ? null : ReadUserRuntimeOptionsFile(Input(UserRuntimeConfig));

    private string Input(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new BuildErrorException(Strings.UnsupportedRuntimeConfigParameter, nameof(DeclaredInputs), path);
        }

        string fullPath = TaskEnvironment.GetAbsolutePath(path).Value;
        if (!DeclaredInputs.Any(item => PathComparer.Equals(TaskEnvironment.GetAbsolutePath(item.ItemSpec).Value, fullPath)))
        {
            throw new BuildErrorException(Strings.UndeclaredRuntimeConfigInput, fullPath);
        }

        return fullPath;
    }

    private static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void NormalizeProbingPaths(JsonObject content)
    {
        JsonNode? value = content["runtimeOptions"]?["additionalProbingPaths"];
        if (value is null)
        {
            return;
        }

        if (value is not JsonArray paths)
        {
            throw new BuildErrorException(Strings.UnsupportedRuntimeConfigParameter, "additionalProbingPaths", value.ToJsonString());
        }

        for (int i = 0; i < paths.Count; i++)
        {
            if (paths[i] is not JsonValue item || !item.TryGetValue<string>(out string? path) || string.IsNullOrEmpty(path) ||
                path[0] is '/' or '\\' || (path.Length > 1 && path[1] == ':'))
            {
                throw new BuildErrorException(Strings.UnsupportedRuntimeConfigParameter, "additionalProbingPaths", paths[i]?.ToJsonString() ?? "null");
            }

            paths[i] = JsonValue.Create(path.Replace('\\', '/').TrimEnd('/'));
        }
    }
}
