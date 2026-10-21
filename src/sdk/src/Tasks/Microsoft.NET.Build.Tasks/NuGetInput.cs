// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

internal sealed class NuGetInput(string path, string name, JsonValueKind kind, string value)
{
    private readonly Dictionary<string, NuGetInput> _properties = new(StringComparer.Ordinal);

    public string Path { get; } = path;
    public string Name { get; } = name;
    public JsonValueKind Kind { get; } = kind;
    public string Value { get; } = value;
    public List<NuGetInput> Children { get; } = [];

    public static NuGetInput Read(ITaskItem[] facts)
    {
        if (facts.Length == 0)
        {
            throw new FormatException("Missing root fact '$'.");
        }

        NuGetInput root = ReadValue(facts[0], "");
        if (root.Path != "$")
        {
            throw new FormatException("The first fact must be the root '$'.");
        }
        root.Require(JsonValueKind.Object);

        Stack<NuGetInput> parents = new();
        parents.Push(root);
        for (int i = 1; i < facts.Length; i++)
        {
            string path = facts[i].ItemSpec;
            int separator = path.LastIndexOf('/');
            if (!path.StartsWith("$/", StringComparison.Ordinal) || separator < 1)
            {
                throw new FormatException($"Invalid fact path '{path}'.");
            }

            string parentPath = path.Substring(0, separator);
            while (parents.Count != 0 && parents.Peek().Path != parentPath)
            {
                parents.Pop();
            }
            if (parents.Count == 0)
            {
                throw new FormatException($"Missing or out-of-order parent for '{path}'.");
            }

            string segment = path.Substring(separator + 1);
            for (int j = 0; j < segment.Length; j++)
            {
                if (segment[j] == '~' && (++j == segment.Length || segment[j] is not ('0' or '1')))
                {
                    throw new FormatException($"Invalid JSON Pointer escape in '{path}'.");
                }
            }

            string name = segment.Replace("~1", "/").Replace("~0", "~");
            NuGetInput parent = parents.Peek();
            NuGetInput child = ReadValue(facts[i], name);
            if (parent.Kind == JsonValueKind.Array &&
                name != parent.Children.Count.ToString(CultureInfo.InvariantCulture))
            {
                throw new FormatException($"Non-contiguous array index in '{path}'.");
            }
            if (parent._properties.ContainsKey(name))
            {
                throw new FormatException($"Duplicate fact '{path}'.");
            }

            parent._properties.Add(name, child);
            parent.Children.Add(child);
            if (child.Kind is JsonValueKind.Object or JsonValueKind.Array)
            {
                parents.Push(child);
            }
        }

        return root;
    }

    public NuGetInput Require(JsonValueKind kind)
    {
        if (Kind != kind)
        {
            throw new FormatException($"Expected {kind} at '{Path}', not {Kind}.");
        }
        return this;
    }

    public NuGetInput? Get(string name) => _properties.TryGetValue(name, out NuGetInput? value) ? value : null;

    public NuGetInput GetRequired(string name) =>
        Get(name) ?? throw new FormatException($"Missing '{name}' in '{Path}'.");

    public string GetString(string name) => GetRequired(name).Require(JsonValueKind.String).Value;

    private static NuGetInput ReadValue(ITaskItem fact, string name)
    {
        string kindText = fact.GetMetadata("Kind");
        string value = fact.GetMetadata("Value");
        if (!Enum.TryParse(kindText, out JsonValueKind kind) || kind == JsonValueKind.Undefined || kind.ToString() != kindText)
        {
            throw new FormatException($"Invalid Kind '{kindText}' at '{fact.ItemSpec}'.");
        }

        switch (kind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
            case JsonValueKind.Null:
                if (value.Length != 0)
                {
                    throw new FormatException($"Unexpected scalar value at '{fact.ItemSpec}'.");
                }
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                if (value != (kind == JsonValueKind.True ? "true" : "false"))
                {
                    throw new FormatException($"Invalid Boolean at '{fact.ItemSpec}'.");
                }
                break;

            case JsonValueKind.Number:
                try
                {
                    using JsonDocument number = JsonDocument.Parse(value);
                    if (number.RootElement.ValueKind != JsonValueKind.Number || number.RootElement.GetRawText() != value)
                    {
                        throw new FormatException($"Invalid number at '{fact.ItemSpec}'.");
                    }
                }
                catch (JsonException ex)
                {
                    throw new FormatException($"Invalid number at '{fact.ItemSpec}'.", ex);
                }
                break;
        }

        return new NuGetInput(fact.ItemSpec, name, kind, value);
    }
}
