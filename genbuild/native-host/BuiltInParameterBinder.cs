// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks;

namespace Microsoft.Build.ActionTaskHost;

internal static class BuiltInParameterBinder
{
    private sealed record Parameter(string Name, Type Type, bool Required, Action<ITask, object> Set);

    private static readonly Parameter[] s_copy =
    [
        new(nameof(CopyFiles.SourceFiles), typeof(ITaskItem[]), false, (task, value) => ((CopyFiles)task).SourceFiles = (ITaskItem[])value),
        new(nameof(CopyFiles.DestinationFiles), typeof(ITaskItem[]), false, (task, value) => ((CopyFiles)task).DestinationFiles = (ITaskItem[])value),
        new(nameof(CopyFiles.DeclaredInputs), typeof(ITaskItem[]), false, (task, value) => ((CopyFiles)task).DeclaredInputs = (ITaskItem[])value),
        new(nameof(CopyFiles.DeclaredOutputs), typeof(ITaskItem[]), false, (task, value) => ((CopyFiles)task).DeclaredOutputs = (ITaskItem[])value),
    ];

    private static readonly Parameter[] s_write =
    [
        new(nameof(WriteLinesToFile.File), typeof(ITaskItem), true, (task, value) => ((WriteLinesToFile)task).File = (ITaskItem)value),
        new(nameof(WriteLinesToFile.Lines), typeof(ITaskItem[]), false, (task, value) => ((WriteLinesToFile)task).Lines = (ITaskItem[])value),
        new(nameof(WriteLinesToFile.Overwrite), typeof(bool), false, (task, value) => ((WriteLinesToFile)task).Overwrite = (bool)value),
        new(nameof(WriteLinesToFile.Encoding), typeof(string), false, (task, value) => ((WriteLinesToFile)task).Encoding = (string)value),
        new(nameof(WriteLinesToFile.WriteOnlyWhenDifferent), typeof(bool), false, (task, value) => ((WriteLinesToFile)task).WriteOnlyWhenDifferent = (bool)value),
        new(nameof(WriteLinesToFile.FailIfNotIncremental), typeof(bool), false, (task, value) => ((WriteLinesToFile)task).FailIfNotIncremental = (bool)value),
        new(nameof(WriteLinesToFile.DeclaredInputs), typeof(ITaskItem[]), false, (task, value) => ((WriteLinesToFile)task).DeclaredInputs = (ITaskItem[])value),
        new(nameof(WriteLinesToFile.DeclaredOutputs), typeof(ITaskItem[]), false, (task, value) => ((WriteLinesToFile)task).DeclaredOutputs = (ITaskItem[])value),
    ];

    internal static Dictionary<string, object> Bind(ITask task, TaskInvocation invocation)
    {
        Parameter[] definitions = task is CopyFiles ? s_copy : s_write;
        Dictionary<string, object> bound = new(MSBuildNameIgnoreCaseComparer.Default);
        HashSet<string> supplied = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach ((string name, JsonElement value) in invocation.Parameters)
        {
            Parameter? definition = null;
            foreach (Parameter candidate in definitions)
            {
                if (MSBuildNameIgnoreCaseComparer.Default.Equals(name, candidate.Name))
                {
                    definition = candidate;
                    break;
                }
            }

            if (definition is null)
            {
                throw new InvalidDataException(HostResources.Format("InvalidParameter", name, task.GetType().FullName));
            }

            if (!supplied.Add(definition.Name))
            {
                throw new InvalidDataException(HostResources.Format("DuplicateParameter", name));
            }

            object concrete;
            try
            {
                Type type = definition.Type;
                bool declaration = definition.Name is "DeclaredInputs" or "DeclaredOutputs";
                if (!TaskParameterReader.TryUnwrap(value, invocation.Version == 2,
                    type == typeof(ITaskItem[]) || type == typeof(ITaskItem), type == typeof(ITaskItem),
                    definition.Required, declaration, out JsonElement unwrapped))
                {
                    continue;
                }

                concrete = type == typeof(ITaskItem[]) ? TaskParameterReader.ReadItems(unwrapped, expanded: false) :
                    type == typeof(ITaskItem) ? TaskParameterReader.ReadItem(unwrapped) :
                    ValueTypeParser.Parse(TaskParameterReader.ReadScalar(unwrapped), type);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidDataException(HostResources.Format("InvalidParameterValue", name, definition.Type), ex);
            }

            definition.Set(task, concrete);
            bound.Add(definition.Name, concrete);
        }

        foreach (Parameter definition in definitions)
        {
            if (definition.Required && !bound.ContainsKey(definition.Name))
            {
                throw new InvalidDataException(HostResources.Format("RequiredParameter", definition.Name, task.GetType().FullName));
            }
        }

        return bound;
    }
}
