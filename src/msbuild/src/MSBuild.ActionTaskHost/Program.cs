// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

namespace Microsoft.Build.ActionTaskHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? materializeRoot = null;
        bool cleanTrees = false;
        string invocationFile;
        if (args is ["--invocation", string file])
        {
            invocationFile = file;
        }
        else if (args is ["--materialize-tree", string treeFile, string root])
        {
            invocationFile = treeFile;
            materializeRoot = root;
        }
        else if (args is ["--clean-trees", string firstFile, ..])
        {
            invocationFile = firstFile;
            cleanTrees = true;
        }
        else
        {
            Console.Error.WriteLine(HostResources.Format("Usage"));
            return 1;
        }

        try
        {
            if (cleanTrees)
            {
                List<Action> cleanup = [];
                for (int i = 1; i < args.Length; i++)
                {
                    invocationFile = args[i];
                    foreach (GeneratedSourceTree tree in GeneratedSourceTree.ReadForCleanup(TaskInvocation.Read(invocationFile)))
                    {
                        cleanup.Add(tree.PrepareCleanup());
                    }
                }

                foreach (Action clean in cleanup)
                {
                    clean();
                }

                return 0;
            }

            TaskInvocation invocation = TaskInvocation.Read(invocationFile);
            return Execute(invocation, materializeRoot) ? 0 : 1;
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            Console.Error.WriteLine(HostResources.Format("InvocationFailed", invocationFile, ex));
            return 1;
        }
    }

    private static bool Execute(TaskInvocation invocation, string? materializeRoot)
    {
        ActionExecution.Prepare(invocation);
        TaskAssemblyLoadContext context = new(invocation.TaskAssembly);
        Assembly assembly = context.LoadFromAssemblyPath(invocation.TaskAssembly);
        Type taskType = assembly.GetType(invocation.TaskType, throwOnError: false, ignoreCase: true)
            ?? ResolveTaskType(assembly, invocation.TaskType);
        if (!typeof(ITask).IsAssignableFrom(taskType) || taskType.IsAbstract || !taskType.IsPublic)
        {
            throw new InvalidDataException(HostResources.Format("InvalidTaskType", invocation.TaskType));
        }

        ActionExecution.ValidateTaskAttributes(taskType);
        TaskEnvironment environment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(invocation.WorkingDirectory, invocation.Environment);
        ConstructorInfo? constructor = taskType.GetConstructor([typeof(TaskEnvironment)]);
        ITask task = (ITask)(constructor is not null
            ? constructor.Invoke([environment])
            : Activator.CreateInstance(taskType)!);
        ActionBuildEngine engine = ActionExecution.Initialize(task, invocation, environment);
        try
        {
            Dictionary<string, object> parameters = TaskParameterBinder.Bind(task, invocation.Parameters, invocation.Version == 2);
            IReadOnlyList<string> outputs = ActionExecution.ValidateFootprint(taskType, parameters);
            GeneratedSourceTree[] trees = GeneratedSourceTree.Read(taskType, parameters);
            if (trees.Length != 0)
            {
                throw new NotSupportedException(HostResources.Format("UnsupportedSandboxContract", taskType.FullName));
            }

            if (materializeRoot is not null)
            {
                GeneratedSourceTree? selected = Array.Find(trees, tree => FileUtilities.PathsEqual(tree.Root, materializeRoot));
                if (selected is null)
                {
                    throw new InvalidDataException(HostResources.Format("InvalidGeneratedTree", materializeRoot));
                }

                selected.Materialize();
                return true;
            }

            return ActionExecution.Execute(task, engine, outputs, invocation, parameters, () =>
            {
                foreach (GeneratedSourceTree tree in trees)
                {
                    tree.WriteDyndep();
                }
            });
        }
        finally
        {
            (task as IDisposable)?.Dispose();
        }
    }

    private static Type ResolveTaskType(Assembly assembly, string name)
    {
        Type? match = null;
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!typeof(ITask).IsAssignableFrom(type) || type.IsAbstract ||
                !(type.FullName!.EndsWith("." + name, StringComparison.OrdinalIgnoreCase) ||
                  type.FullName.EndsWith("+" + name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidDataException(HostResources.Format("InvalidTaskType", name));
            }

            match = type;
        }

        return match ?? throw new InvalidDataException(HostResources.Format("InvalidTaskType", name));
    }
}
