// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#nullable enable

namespace Microsoft.Build.Tasks;

/// <summary>
/// Preserves acquisition requests without negotiating the configured build-reference graph.
/// </summary>
public sealed class PrepareRestoreRequests : TaskExtension
{
    public ITaskItem[] ProjectReferences { get; set; } = [];
    public ITaskItem[] Requests { get; set; } = [];
    public string? CurrentProject { get; set; }
    public string[] Properties { get; set; } = [];
    public string RemoveProperties { get; set; } = "";

    [Output]
    public ITaskItem[] PreparedRequests { get; private set; } = [];

    public override bool Execute()
    {
        PreparedRequests = [];
        if (BuildEngine is not IBuildEngine6 engine)
        {
            Log.LogError("Restore request preparation requires access to the current global properties.");
            return false;
        }

        IReadOnlyDictionary<string, string> inherited = engine.GetGlobalProperties();
        List<ITaskItem> results = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        try
        {
            foreach (ITaskItem request in Requests)
            {
                string encoded = request.GetMetadata(RestoreRequest.PropertiesMetadata);
                Dictionary<string, string> properties = RestoreRequest.Deserialize(encoded);
                Add(request.ItemSpec, properties);
            }

            foreach (ITaskItem reference in ProjectReferences)
            {
                if (reference.GetMetadata("ToolsVersion").Length != 0)
                {
                    Log.LogError("Restore acquisition uses the current toolset; reference '{0}' cannot override ToolsVersion.", reference.ItemSpec);
                    return false;
                }

                Dictionary<string, string> properties = new(MSBuildNameIgnoreCaseComparer.Default);
                foreach (KeyValuePair<string, string> property in inherited)
                {
                    properties.Add(property.Key, property.Value);
                }

                Remove(RemoveProperties);
                Remove(reference.GetMetadata("GlobalPropertiesToRemove"));
                Remove(reference.GetMetadata("UndefineProperties"));

                string overrides = reference.GetMetadata("Properties");
                if (overrides.Length != 0)
                {
                    if (!Apply(overrides.Split([';'], StringSplitOptions.RemoveEmptyEntries)))
                    {
                        return false;
                    }
                }
                else if (!Apply(Properties)
                    || !ApplyMetadata("SetConfiguration")
                    || !ApplyMetadata("SetPlatform")
                    || !ApplyMetadata("SetTargetFramework"))
                {
                    return false;
                }

                if (!ApplyMetadata("AdditionalProperties"))
                {
                    return false;
                }

                Add(reference.ItemSpec, properties);

                void Remove(string names)
                {
                    foreach (string name in names.Split([';'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        properties.Remove(name.Trim());
                    }
                }

                bool ApplyMetadata(string name) => Apply(reference.GetMetadata(name).Split([';'], StringSplitOptions.RemoveEmptyEntries));

                bool Apply(string[] values)
                {
                    if (!PropertyParser.GetTableWithEscaping(Log, "Restore acquisition properties", "ProjectReference", values, out Dictionary<string, string> additions))
                    {
                        return false;
                    }

                    foreach (KeyValuePair<string, string> addition in additions)
                    {
                        properties[addition.Key] = addition.Value;
                    }

                    return true;
                }
            }

            if (!string.IsNullOrEmpty(CurrentProject))
            {
                Add(CurrentProject!, inherited);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or ArgumentException)
        {
            Log.LogError("Invalid Restore acquisition request: {0}", ex.Message);
            return false;
        }

        PreparedRequests = results.ToArray();
        return !Log.HasLoggedErrors;

        void Add(string project, IReadOnlyDictionary<string, string> properties)
        {
            string path = Path.GetFullPath(project);
            string encoded = RestoreRequest.Serialize(properties);
            string identity = RestoreRequest.GetIdentity(path, encoded);
            if (seen.Add(identity))
            {
                TaskItem request = new(EscapingUtilities.Escape(path));
                request.SetMetadata(RestoreRequest.PropertiesMetadata, encoded);
                request.SetMetadata(RestoreRequest.IdentityMetadata, identity);
                results.Add(request);
            }
        }
    }
}
