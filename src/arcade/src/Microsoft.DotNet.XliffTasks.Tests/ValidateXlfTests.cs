// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using XliffTasks.Tasks;
using Xunit;

namespace XliffTasks.Tests;

public class ValidateXlfTests
{
    [Fact]
    public void WritesValidationStampToSandboxedDeclaredOutput()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "sandbox-tests", Guid.NewGuid().ToString("N"));
        string projectDirectory = Path.Combine(root, "project");
        string stagingDirectory = Path.Combine(root, "staging");
        string stamp = Path.Combine("output", "validated.stamp");
        string stagedStamp = MapPath(Path.Combine(stagingDirectory, "outputs"), projectDirectory, stamp);

        try
        {
            ValidateXlf task = new()
            {
                Sources = [],
                Languages = [],
                ValidationStamp = stamp,
                DeclaredInputs = [],
                DeclaredOutputs = [new Microsoft.Build.Utilities.TaskItem(stamp)],
                TaskEnvironment = CreateSandboxEnvironment(projectDirectory, stagingDirectory)
            };

            Assert.True(task.Execute());
            Assert.Equal("XLF validation succeeded.\n", File.ReadAllText(stagedStamp));
            Assert.False(File.Exists(Path.Combine(projectDirectory, stamp)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static TaskEnvironment CreateSandboxEnvironment(string projectDirectory, string stagingDirectory)
    {
        TaskEnvironment environment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(projectDirectory);
        MethodInfo setter = typeof(TaskEnvironment).GetMethod(
            "SetFilePathResolvers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(setter);
        setter.Invoke(environment,
        [
            (Func<string, string>)(path => MapPath(Path.Combine(stagingDirectory, "inputs"), projectDirectory, path)),
            (Func<string, string>)(path => MapPath(Path.Combine(stagingDirectory, "outputs"), projectDirectory, path)),
            stagingDirectory,
            (Func<string, string>)(path => MapPath(Path.Combine(stagingDirectory, "outputs"), projectDirectory, path))
        ]);
        return environment;
    }

    private static string MapPath(string root, string projectDirectory, string path) =>
        Path.Combine(root, Path.GetRelativePath(projectDirectory, Path.GetFullPath(path, projectDirectory)));
}
