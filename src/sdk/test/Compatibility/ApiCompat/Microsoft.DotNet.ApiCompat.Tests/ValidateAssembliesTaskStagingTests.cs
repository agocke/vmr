// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.DotNet.ApiCompat.Task;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.DotNet.ApiCompat.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ValidateAssembliesTaskStagingTests
{
    [TestMethod]
    public void ValidateAssembliesUsesStagedFilesAndStructuralDirectories()
    {
        string projectDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string executionDirectory = Path.Combine(projectDirectory, "execution");
        string stagedAssembly = Path.Combine(executionDirectory, "inputs", "contract.dll");
        string stagedRoslynDirectory = Path.Combine(executionDirectory, "roslyn");
        string stagedSemaphore = Path.Combine(executionDirectory, "outputs", "validate.success");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedAssembly)!);
        Directory.CreateDirectory(stagedRoslynDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedSemaphore)!);
        try
        {
            string logicalAssembly = Path.Combine(projectDirectory, "contract.dll");
            string logicalRoslynDirectory = Path.Combine(projectDirectory, "roslyn");
            string logicalCodeAnalysis = Path.Combine(logicalRoslynDirectory, "Microsoft.CodeAnalysis.dll");
            string logicalCSharp = Path.Combine(logicalRoslynDirectory, "Microsoft.CodeAnalysis.CSharp.dll");
            string logicalSemaphore = Path.Combine(projectDirectory, "validate.success");
            File.Copy(typeof(ValidateAssembliesTaskStagingTests).Assembly.Location, stagedAssembly);
            File.Copy(typeof(Compilation).Assembly.Location, Path.Combine(stagedRoslynDirectory, Path.GetFileName(typeof(Compilation).Assembly.Location)));
            File.Copy(typeof(CSharpCompilation).Assembly.Location, Path.Combine(stagedRoslynDirectory, Path.GetFileName(typeof(CSharpCompilation).Assembly.Location)));

            Dictionary<string, string> inputPaths = new(StringComparer.Ordinal)
            {
                [logicalAssembly] = stagedAssembly,
                [logicalCodeAnalysis] = Path.Combine(stagedRoslynDirectory, "Microsoft.CodeAnalysis.dll"),
                [logicalCSharp] = Path.Combine(stagedRoslynDirectory, "Microsoft.CodeAnalysis.CSharp.dll")
            };
            ValidateAssembliesTask task = new()
            {
                BuildEngine = new TestBuildEngine(),
                TaskEnvironment = CreateStagedEnvironment(
                    projectDirectory,
                    executionDirectory,
                    inputPaths,
                    logicalRoslynDirectory,
                    stagedRoslynDirectory,
                    logicalSemaphore,
                    stagedSemaphore),
                LeftAssemblies = [logicalAssembly],
                RightAssemblies = [logicalAssembly],
                RoslynAssembliesPath = logicalRoslynDirectory,
                SemaphoreFile = logicalSemaphore,
                DeclaredInputs = inputPaths.Keys.Select(path => (ITaskItem)new TaskItem(path)).ToArray(),
                DeclaredOutputs = [new TaskItem(logicalSemaphore)]
            };

            Assert.IsTrue(task.Execute());
            Assert.IsTrue(File.Exists(stagedSemaphore));
            Assert.IsFalse(File.Exists(logicalSemaphore));

            File.WriteAllText(stagedSemaphore, "retain on failure");
            task.LeftAssemblies = [Path.Combine(projectDirectory, "undeclared.dll")];
            Action executeWithUndeclaredInput = () => task.Execute();
            Assert.ThrowsExactly<ArgumentException>(executeWithUndeclaredInput);
            Assert.AreEqual("retain on failure", File.ReadAllText(stagedSemaphore));
        }
        finally
        {
            if (Directory.Exists(projectDirectory))
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
        }
    }

    private static TaskEnvironment CreateStagedEnvironment(
        string projectDirectory,
        string executionDirectory,
        Dictionary<string, string> inputPaths,
        string logicalRoslynDirectory,
        string stagedRoslynDirectory,
        string logicalSemaphore,
        string stagedSemaphore)
    {
        TaskEnvironment environment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(projectDirectory);
        MethodInfo setter = typeof(TaskEnvironment).GetMethod("SetFilePathResolvers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TaskEnvironment does not expose its staged path resolver test seam.");
        setter.Invoke(environment,
        [
            (Func<string, string>)(path => inputPaths[path]),
            (Func<string, string>)(path => path == logicalSemaphore ? stagedSemaphore : throw new InvalidOperationException(path)),
            executionDirectory,
            (Func<string, string>)(path => path == logicalRoslynDirectory ? stagedRoslynDirectory : throw new InvalidOperationException(path))
        ]);
        return environment;
    }

    private sealed class TestBuildEngine : IBuildEngine
    {
        public int ColumnNumberOfTaskNode => 0;
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "";
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogErrorEvent(BuildErrorEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) { }
    }
}
