// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.ActionTaskHost;

internal static class ActionExecution
{
    internal static void Prepare(TaskInvocation invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation.TaskType);
        RequireAbsolutePath(invocation.TaskAssembly);
        RequireAbsolutePath(invocation.WorkingDirectory);
        if (invocation.LineNumber < 0 || invocation.ColumnNumber < 0)
        {
            throw new InvalidDataException(HostResources.Format("InvalidSourceLocation"));
        }

        Directory.SetCurrentDirectory(invocation.WorkingDirectory);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            Environment.SetEnvironmentVariable((string)variable.Key, null);
        }

        foreach (KeyValuePair<string, string> variable in invocation.Environment)
        {
            if (variable.Value is null)
            {
                throw new InvalidDataException(HostResources.Format("InvalidEnvironmentValue", variable.Key));
            }

            Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }

        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    internal static ActionBuildEngine Initialize(ITask task, TaskInvocation invocation, TaskEnvironment? environment = null)
    {
        ActionBuildEngine engine = new(invocation);
        task.BuildEngine = engine;
        if (task is IMultiThreadableTask multiThreadableTask)
        {
            multiThreadableTask.TaskEnvironment = environment
                ?? TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(invocation.WorkingDirectory, invocation.Environment);
        }

        return engine;
    }

    internal static bool Execute(ITask task, ActionBuildEngine engine, IReadOnlyList<string> outputs,
        TaskInvocation invocation, Dictionary<string, object> parameters, Action? publish = null)
    {
        if (task is not IMultiThreadableTask isolatedTask)
        {
            throw new NotSupportedException(HostResources.Format("UnsupportedSandboxContract", invocation.TaskType));
        }

        TaskEnvironment environment = isolatedTask.TaskEnvironment;
        FileStagingContext staging = new(environment);
        string previousDirectory = Environment.CurrentDirectory;
        string[] temporaryVariables = ["TMPDIR", "TMP", "TEMP"];
        string?[] previousTemporaryValues = new string?[temporaryVariables.Length];
        for (int i = 0; i < temporaryVariables.Length; i++)
        {
            previousTemporaryValues[i] = environment.GetEnvironmentVariable(temporaryVariables[i]);
        }

        try
        {
            staging.Prepare((ITaskItem[])parameters["DeclaredInputs"], (ITaskItem[])parameters["DeclaredOutputs"]);
            environment.SetFilePathResolvers(path => staging.MapFile(path, input: true),
                path => staging.MapFile(path, input: false), staging.WorkingDirectory);
            Directory.SetCurrentDirectory(staging.WorkingDirectory);
            foreach (string variable in temporaryVariables)
            {
                environment.SetEnvironmentVariable(variable, staging.Scratch);
                Environment.SetEnvironmentVariable(variable, staging.Scratch);
            }

            bool succeeded = task.Execute();
            if (!succeeded || engine.HasLoggedErrors)
            {
                if (!engine.HasLoggedErrors)
                {
                    Console.Error.WriteLine(HostResources.Format("TaskFailed", invocation.TaskType));
                }

                return false;
            }

            staging.Publish();
            Directory.SetCurrentDirectory(previousDirectory);
            publish?.Invoke();
            foreach (string output in outputs)
            {
                RequireRegularFile(output);
            }

            return true;
        }
        finally
        {
            environment.SetFilePathResolvers(null, null, null);
            Directory.SetCurrentDirectory(previousDirectory);
            for (int i = 0; i < temporaryVariables.Length; i++)
            {
                environment.SetEnvironmentVariable(temporaryVariables[i], previousTemporaryValues[i]);
                Environment.SetEnvironmentVariable(temporaryVariables[i], previousTemporaryValues[i]);
            }

            staging.Cleanup();
        }
    }

    internal static void ValidateTaskAttributes(Type type)
    {
        bool declaredIO = false;
        bool stagedIO = false;
        foreach (CustomAttributeData attribute in type.GetCustomAttributesData())
        {
            string? name = attribute.AttributeType.FullName;
            if (name == typeof(MSBuildDeclaredIOTaskAttribute).FullName)
            {
                declaredIO = true;
                foreach (CustomAttributeNamedArgument argument in attribute.NamedArguments)
                {
                    if (argument.MemberName == nameof(MSBuildDeclaredIOTaskAttribute.SandboxVersion))
                    {
                        stagedIO = argument.TypedValue.Value is 1;
                    }
                }
            }

            if (name is "Microsoft.Build.Framework.LoadInSeparateAppDomainAttribute" or "Microsoft.Build.Framework.RunInSTAAttribute")
            {
                throw new NotSupportedException(HostResources.Format("UnsupportedTaskHosting", type.FullName, name));
            }
        }

        if (!declaredIO)
        {
            throw new InvalidDataException(HostResources.Format("MissingContract", type.FullName));
        }

        if (!stagedIO || !typeof(IMultiThreadableTask).IsAssignableFrom(type))
        {
            throw new NotSupportedException(HostResources.Format("UnsupportedSandboxContract", type.FullName));
        }
    }

    internal static IReadOnlyList<string> ValidateFootprint(Type taskType, Dictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("DeclaredInputs", out object? inputValue) || inputValue is not ITaskItem[] inputs ||
            !parameters.TryGetValue("DeclaredOutputs", out object? outputValue) || outputValue is not ITaskItem[] outputs)
        {
            throw new InvalidDataException(HostResources.Format("MissingDeclarations", taskType.FullName));
        }

        foreach (CustomAttributeData attribute in taskType.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName == typeof(MSBuildDeclaredIORequiresUnsetAttribute).FullName)
            {
                if (attribute.ConstructorArguments is not [{ Value: string parameterName }])
                {
                    throw new InvalidDataException(HostResources.Format("InvalidContract", taskType.FullName));
                }

                if (!parameters.TryGetValue(parameterName, out object? value))
                {
                    continue;
                }

                if (value is not string { Length: 0 } && value is not Array { Length: 0 })
                {
                    throw new InvalidDataException(HostResources.Format("ParameterMustBeUnset", parameterName));
                }
            }
        }

        if (outputs.Length == 0)
        {
            throw new NotSupportedException(HostResources.Format("PersistentOutputRequired", taskType.FullName));
        }

        List<string> outputPaths = new(outputs.Length);
        foreach (ITaskItem output in outputs)
        {
            string path = ResolveDeclaredPath(output);
            foreach (string existing in outputPaths)
            {
                if (PathsConflict(path, existing))
                {
                    throw new InvalidDataException(HostResources.Format("ConflictingPath", path));
                }
            }

            if (Directory.Exists(path))
            {
                throw new NotSupportedException(HostResources.Format("RegularFileRequired", path));
            }

            outputPaths.Add(path);
        }

        foreach (ITaskItem input in inputs)
        {
            string path = ResolveDeclaredPath(input);
            RequireRegularFile(path);
            foreach (string output in outputPaths)
            {
                if (PathsConflict(path, output))
                {
                    throw new NotSupportedException(HostResources.Format("ConflictingPath", path));
                }
            }
        }

        return outputPaths;
    }

    private static bool PathsConflict(string first, string second)
        => FileUtilities.PathsEqual(first, second)
            || first.StartsWith(Path.TrimEndingDirectorySeparator(second) + Path.DirectorySeparatorChar, FileUtilities.PathComparison)
            || second.StartsWith(Path.TrimEndingDirectorySeparator(first) + Path.DirectorySeparatorChar, FileUtilities.PathComparison);

    private static string ResolveDeclaredPath(ITaskItem item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ItemSpec);
        if (item.ItemSpec.IndexOfAny(['*', '?']) >= 0)
        {
            throw new InvalidDataException(HostResources.Format("ExactPathRequired", item.ItemSpec));
        }

        return Path.GetFullPath(item.ItemSpec);
    }

    private static void RequireAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException(HostResources.Format("AbsolutePathRequired", path));
        }
    }

    private static void RequireRegularFile(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path))
        {
            throw new InvalidDataException(HostResources.Format("RegularFileRequired", path));
        }
    }
}
