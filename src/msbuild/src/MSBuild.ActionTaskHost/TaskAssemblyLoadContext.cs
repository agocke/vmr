// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.ActionTaskHost;

internal sealed class TaskAssemblyLoadContext(string taskAssembly) : AssemblyLoadContext
{
    // Without a deps file the runtime resolver enumerates every neighboring file.
    // Exact adjacent-name probes suffice for manifest-less action task assemblies.
    private readonly AssemblyDependencyResolver? _resolver =
        File.Exists(Path.ChangeExtension(taskAssembly, ".deps.json")) ? new(taskAssembly) : null;
    private readonly string _directory = Path.GetDirectoryName(taskAssembly)!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, "Microsoft.Build", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(HostResources.Format("EngineDependencyNotSupported", taskAssembly));
        }

        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(ITask).Assembly.GetName()))
        {
            return typeof(ITask).Assembly;
        }

        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(TaskItem).Assembly.GetName()))
        {
            return typeof(TaskItem).Assembly;
        }

        string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            string adjacentPath = Path.Combine(_directory, assemblyName.Name + ".dll");
            if (File.Exists(adjacentPath))
            {
                path = adjacentPath;
            }
            else if (_resolver is null)
            {
                string adjacentExecutablePath = Path.Combine(_directory, assemblyName.Name + ".exe");
                if (File.Exists(adjacentExecutablePath))
                {
                    path = adjacentExecutablePath;
                }
            }
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
