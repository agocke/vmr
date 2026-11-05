// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.ActionTaskHost;

internal static class TaskParameterReader
{
    internal static ExpandedValue ReadExpanded(JsonElement value)
        => value.Deserialize(TaskInvocation.JsonContext.ExpandedValue) ?? throw new JsonException();

    internal static bool TryUnwrap(JsonElement value, bool expanded, bool vector, bool singleItem,
        bool required, bool declaration, out JsonElement concrete)
    {
        concrete = value;
        if (!expanded)
        {
            return true;
        }

        ExpandedValue interpretations = ReadExpanded(value);
        if (!vector)
        {
            if (interpretations.Scalar.Length == 0)
            {
                return false;
            }

            concrete = value.GetProperty("scalar");
            return true;
        }

        JsonElement items = interpretations.Items;
        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException();
        }

        if (items.GetArrayLength() == 0 && !declaration && !required)
        {
            return false;
        }

        if (singleItem)
        {
            if (items.GetArrayLength() == 0)
            {
                return false;
            }

            if (items.GetArrayLength() != 1)
            {
                throw new JsonException();
            }

            concrete = items[0];
        }
        else
        {
            concrete = items;
        }

        return true;
    }

    internal static string ReadScalar(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => throw new JsonException(),
        };

    internal static ITaskItem[] ReadItems(JsonElement value, bool expanded)
    {
        JsonElement items = expanded ? ReadExpanded(value).Items : value;
        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException();
        }

        ITaskItem[] result = new ITaskItem[items.GetArrayLength()];
        int index = 0;
        foreach (JsonElement element in items.EnumerateArray())
        {
            result[index++] = ReadItem(element);
        }

        return result;
    }

    internal static ITaskItem ReadItem(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return new TaskItem(EscapingUtilities.Escape(value.GetString()!), treatAsFilePath: false);
        }

        ItemValue item = value.Deserialize(TaskInvocation.JsonContext.ItemValue) ?? throw new JsonException();
        TaskItem result = new(EscapingUtilities.Escape(item.ItemSpec), treatAsFilePath: false);
        HashSet<string> metadataNames = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (KeyValuePair<string, string> metadata in item.Metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata.Value);
            if (!metadataNames.Add(metadata.Key))
            {
                throw new InvalidDataException(HostResources.Format("DuplicateParameter", metadata.Key));
            }

            ((ITaskItem2)result).SetMetadataValueLiteral(metadata.Key, metadata.Value);
        }

        return result;
    }

    internal sealed class ItemValue
    {
        public required string ItemSpec { get; init; }

        public Dictionary<string, string> Metadata { get; init; } = [];
    }

    internal sealed class ExpandedValue
    {
        public required string Scalar { get; init; }

        public required JsonElement Items { get; init; }
    }
}
