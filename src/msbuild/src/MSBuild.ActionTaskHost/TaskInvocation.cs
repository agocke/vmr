// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Build.ActionTaskHost;

internal sealed class TaskInvocation
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
    };

    // A context installs its resolver; keep the receipt serializer's options independent.
    internal static readonly InvocationJsonContext JsonContext = new(new JsonSerializerOptions(JsonOptions));

    public required int Version { get; init; }

    public required string TaskAssembly { get; init; }

    public required string TaskType { get; init; }

    public required string WorkingDirectory { get; init; }

    public string ProjectFile { get; init; } = string.Empty;

    public int LineNumber { get; init; }

    public int ColumnNumber { get; init; }

    public Dictionary<string, string> Environment { get; init; } = [];

    public required Dictionary<string, JsonElement> Parameters { get; init; }

    internal string? StagingRoot { get; set; }

    internal (string Launcher, string Socket)? ProcessLaunchBroker { get; set; }

    internal static TaskInvocation Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        TaskInvocation invocation = JsonSerializer.Deserialize(stream, JsonContext.TaskInvocation)
            ?? throw new InvalidDataException(HostResources.Format("InvalidInvocation", path));

        if (invocation.Version is not (1 or 2))
        {
            throw new InvalidDataException(HostResources.Format("UnsupportedVersion", invocation.Version));
        }

        return invocation;
    }
}

[JsonSerializable(typeof(TaskInvocation))]
[JsonSerializable(typeof(TaskParameterReader.ItemValue))]
[JsonSerializable(typeof(TaskParameterReader.ExpandedValue))]
internal sealed partial class InvocationJsonContext : JsonSerializerContext;
