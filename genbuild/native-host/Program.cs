// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks;

namespace Microsoft.Build.ActionTaskHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        string path = args.Length > 1 ? args[1] : string.Empty;
        try
        {
            if (args is ["--write-catalog", _])
            {
                WriteCatalog(path);
                return 0;
            }

            if (args is not ["--invocation", _])
            {
                Console.Error.WriteLine(HostResources.Format("Usage"));
                return 1;
            }

            TaskInvocation invocation = TaskInvocation.Read(path);
            ActionExecution.Prepare(invocation);
            string directory = Path.GetDirectoryName(invocation.TaskAssembly)!;
            if (!string.Equals(Path.GetFileName(invocation.TaskAssembly), "Microsoft.Build.Tasks.Core.dll", StringComparison.Ordinal) ||
                !MatchesAssemblies(directory))
            {
                throw new InvalidDataException(HostResources.Format("InvalidTaskType", invocation.TaskType));
            }

            ITask task = IsTask(invocation.TaskType, typeof(CopyFiles)) ? new CopyFiles() :
                IsTask(invocation.TaskType, typeof(WriteLinesToFile)) ? new WriteLinesToFile() :
                throw new InvalidDataException(HostResources.Format("InvalidTaskType", invocation.TaskType));
            ActionExecution.ValidateTaskAttributes(task.GetType());
            ActionBuildEngine engine = ActionExecution.Initialize(task, invocation);
            try
            {
                Dictionary<string, object> bound = BuiltInParameterBinder.Bind(task, invocation);
                return ActionExecution.Execute(task, engine, ActionExecution.ValidateFootprint(task.GetType(), bound), invocation, bound) ? 0 : 1;
            }
            finally
            {
                (task as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            Console.Error.WriteLine(HostResources.Format("InvocationFailed", path, ex));
            return 1;
        }
    }

    private static bool IsTask(string name, Type type)
        => string.Equals(name, type.FullName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, type.Name, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAssemblies(string directory)
    {
        foreach ((string name, string hash) in BuiltInAssemblies.Files)
        {
            string file = Path.Combine(directory, name);
            if (!File.Exists(file) || Hash(file) != hash)
            {
                return false;
            }
        }

        return true;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteCatalog(string path)
    {
        using MemoryStream stream = new();
        using Utf8JsonWriter json = new(stream);
        json.WriteStartObject();
        json.WriteNumber("version", 1);
        json.WriteString("hostSha256", Hash(Environment.ProcessPath!));
        json.WriteStartObject("assemblies");
        foreach ((string name, string hash) in BuiltInAssemblies.Files)
        {
            json.WriteString(name, hash);
        }

        json.WriteEndObject();
        json.WriteStartArray("tasks");
        json.WriteStringValue(typeof(CopyFiles).FullName);
        json.WriteStringValue(typeof(WriteLinesToFile).FullName);
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        byte[] contents = stream.ToArray();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(contents))
        {
            File.WriteAllBytes(path, contents);
        }
    }
}
