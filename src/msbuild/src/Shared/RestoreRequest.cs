// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;

#nullable enable

namespace Microsoft.Build.Shared;

internal static class RestoreRequest
{
    internal const string PropertiesMetadata = "_MSBuildRestoreRequestProperties";
    internal const string IdentityMetadata = "_MSBuildRestoreRequestId";

    internal static string Serialize(IReadOnlyDictionary<string, string> properties)
    {
        List<string> names = new(properties.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(names.Count);
            foreach (string name in names)
            {
                writer.Write(name.ToUpperInvariant());
                writer.Write(EscapingUtilities.Escape(EscapingUtilities.UnescapeAll(properties[name])));
            }
        }

        return Convert.ToBase64String(stream.ToArray());
    }

    internal static Dictionary<string, string> Deserialize(string value)
    {
        using MemoryStream stream = new(Convert.FromBase64String(value), writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8);
        if (reader.ReadInt32() != 1)
        {
            throw new InvalidDataException("Unsupported Restore request format.");
        }

        int count = reader.ReadInt32();
        if (count < 0 || count > stream.Length / 2)
        {
            throw new InvalidDataException("Invalid Restore request property count.");
        }

        Dictionary<string, string> properties = new(MSBuildNameIgnoreCaseComparer.Default);
        for (int i = 0; i < count; i++)
        {
            properties.Add(reader.ReadString(), reader.ReadString());
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Unexpected Restore request data.");
        }

        return properties;
    }

    internal static string GetIdentity(string project, string properties)
    {
        string path = NativeMethodsShared.IsWindows ? project.ToUpperInvariant() : project;
        using SHA256 hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(path + "\0" + properties)));
    }

    internal static bool Apply(
        ITaskItem project,
        IBuildEngine3 engine,
        Action<string> logError,
        out Dictionary<string, string>? properties,
        out List<string>? removals)
    {
        properties = null;
        removals = null;
        if (engine is not IBuildEngine6 globals)
        {
            logError("Restore request replay requires access to the caller's global properties.");
            return false;
        }

        try
        {
            string encoded = project.GetMetadata(PropertiesMetadata);
            properties = Deserialize(encoded);
            if (!string.Equals(project.GetMetadata(IdentityMetadata), GetIdentity(project.ItemSpec, encoded), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Restore request identity does not match its project and properties.");
            }

            removals = [];
            foreach (string name in globals.GetGlobalProperties().Keys)
            {
                if (!properties.ContainsKey(name))
                {
                    removals.Add(name);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or ArgumentException)
        {
            logError($"Invalid Restore request for '{project.ItemSpec}': {ex.Message}");
            return false;
        }
    }
}
