// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.ActionTaskHost;

internal static class TaskParameterBinder
{
    internal static Dictionary<string, object> Bind(ITask task, Dictionary<string, JsonElement> parameters, bool unbound = false)
    {
        Type taskType = task.GetType();
        Dictionary<string, PropertyInfo> properties = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (PropertyInfo property in taskType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            properties.Add(property.Name, property);
        }

        Dictionary<string, object> bound = new(MSBuildNameIgnoreCaseComparer.Default);
        HashSet<string> supplied = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (KeyValuePair<string, JsonElement> parameter in parameters)
        {
            if (!properties.TryGetValue(parameter.Key, out PropertyInfo? property) ||
                property.SetMethod is not { IsPublic: true } ||
                property.GetIndexParameters().Length != 0 ||
                property.Name is nameof(ITask.BuildEngine) or nameof(ITask.HostObject)
                    or nameof(IMultiThreadableTask.TaskEnvironment))
            {
                throw new InvalidDataException(HostResources.Format("InvalidParameter", parameter.Key, taskType.FullName));
            }

            if (!supplied.Add(property.Name))
            {
                throw new InvalidDataException(HostResources.Format("DuplicateParameter", parameter.Key));
            }

            object value;
            try
            {
                JsonElement concrete = parameter.Value;
                if (unbound)
                {
                    Type type = property.PropertyType;
                    if (type.IsArray
                        ? type.GetArrayRank() != 1 || !IsSupportedScalarType(type.GetElementType()!)
                        : !IsSupportedScalarType(type))
                    {
                        throw new NotSupportedException(HostResources.Format("UnsupportedParameterType", type));
                    }

                    bool vector = property.PropertyType.IsArray || property.PropertyType == typeof(ITaskItem);
                    bool declaration = property.Name.Equals("DeclaredInputs", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("DeclaredOutputs", StringComparison.OrdinalIgnoreCase);
                    if (!TaskParameterReader.TryUnwrap(concrete, expanded: true, vector,
                        property.PropertyType == typeof(ITaskItem),
                        property.IsDefined(typeof(RequiredAttribute), inherit: true), declaration, out concrete))
                    {
                        continue;
                    }
                }

                value = ConvertValue(concrete, property.PropertyType, unbound);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidDataException(HostResources.Format("InvalidParameterValue", parameter.Key, property.PropertyType), ex);
            }

            property.SetValue(task, value);
            bound.Add(property.Name, value);
        }

        foreach (PropertyInfo property in properties.Values)
        {
            if (property.IsDefined(typeof(RequiredAttribute), inherit: true) && !bound.ContainsKey(property.Name))
            {
                throw new InvalidDataException(HostResources.Format("RequiredParameter", property.Name, taskType.FullName));
            }
        }

        return bound;
    }

    internal static ITaskItem[] ReadItems(JsonElement value, bool expanded)
        => TaskParameterReader.ReadItems(value, expanded);

    private static object ConvertValue(JsonElement value, Type type, bool expandedItems = false)
    {
        if (type.IsArray)
        {
            Type elementType = type.GetElementType()!;
            if (type.GetArrayRank() != 1 || !IsSupportedScalarType(elementType))
            {
                throw new NotSupportedException(HostResources.Format("UnsupportedParameterType", type));
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException();
            }

            Array array = Array.CreateInstance(elementType, value.GetArrayLength());
            int index = 0;
            foreach (JsonElement element in value.EnumerateArray())
            {
                JsonElement concrete = element;
                if (expandedItems && elementType != typeof(ITaskItem) && element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("itemSpec", out JsonElement itemSpec))
                {
                    concrete = itemSpec;
                }

                array.SetValue(ConvertValue(concrete, elementType), index++);
            }

            return array;
        }

        if (type == typeof(ITaskItem))
        {
            return TaskParameterReader.ReadItem(value);
        }

        if (!IsSupportedScalarType(type))
        {
            throw new NotSupportedException(HostResources.Format("UnsupportedParameterType", type));
        }

        string text = TaskParameterReader.ReadScalar(value);

        if (type == typeof(AbsolutePath) || type == typeof(FileInfo) || type == typeof(DirectoryInfo))
        {
            text = Path.GetFullPath(text);
        }

        return ValueTypeParser.Parse(text, type);
    }

    private static bool IsSupportedScalarType(Type type)
        => type == typeof(ITaskItem) || ValueTypeParser.IsSupportedType(type) || type.IsEnum;
}
