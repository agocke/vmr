// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Arcade.Test.Common;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.DotNet.Arcade.Sdk.Tests;

public class GenerateResxSourceTests
{
    private readonly ITestOutputHelper _output;

    public GenerateResxSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(true, false, false, "TestStrings.EmitFormatMethods.cs.txt")]
    [InlineData(false, false, false, "TestStrings.Default.cs.txt")]
    [InlineData(false, true, true, "TestStrings.AsConstants.cs.txt")]
    [InlineData(false, false, true, "TestStrings.OmitGetResourceString.cs.txt")]
    public void GeneratesCSharp(bool emitFormatMethods, bool asConstants, bool omitGetResourceString, string expectedFileName)
    {
        var resx = Path.Combine(AppContext.BaseDirectory, "testassets", "Resources", "TestStrings.resx");
        var actualFile = Path.Combine(AppContext.BaseDirectory, Path.GetRandomFileName());

        var engine = new MockEngine(_output);
        var task = new GenerateResxSource
        {
            BuildEngine = engine,
            ResourceFile = resx,
            ResourceName = "Microsoft.DotNet.TestStrings",
            ResourceClassName = "Microsoft.DotNet.TestStrings",
            EmitFormatMethods = emitFormatMethods,
            AsConstants = asConstants,
            OmitGetResourceString = omitGetResourceString,
            Language = "C#",
            OutputPath = actualFile,
        };

        var expectedFile = Path.Combine(AppContext.BaseDirectory, "testassets", "Resources", expectedFileName);

        if (File.Exists(actualFile))
        {
            File.Delete(actualFile);
        }

        Assert.True(task.Execute(), "Task failed");

        Assert.Empty(engine.Warnings);

        Assert.True(File.Exists(actualFile), "Actual file does not exist");
        var actualFileContents = File.ReadAllText(actualFile);
        _output.WriteLine(actualFileContents);
        Assert.Equal(File.ReadAllText(expectedFile), actualFileContents, ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("A", "A")]
    [InlineData("_A", "_A")]
    [InlineData(".A", "_A")]
    [InlineData("4A", "_4A")]
    [InlineData("4(.-)A", "_4____A")]
    [InlineData("A\u0660\u2040\u0601\u0300\u0903", "A\u0660\u2040\u0601\u0300\u0903")]
    public void GetIdentifierFromResourceName(string name, string expectedIdentifier)
    {
        Assert.Equal(expectedIdentifier, GenerateResxSource.GetIdentifierFromResourceName(name));
    }

    [Fact]
    public void RewriteRepositoryUrlsIsPureDeclaredIOTask()
    {
        AssertPureDeclaredIOContract(typeof(RewriteRepositoryUrls));

        TaskItem sourceRoot = new("src");
        sourceRoot.SetMetadata("ScmRepositoryUrl", "https://github.com/dotnet/arcade");
        RewriteRepositoryUrls task = new()
        {
            RepositoryUrl = "https://github.com/dotnet/arcade",
            SourceRoots = [sourceRoot],
            Pattern = "github\\.com",
            Replacement = "example.com",
            DeclaredInputs = [],
            DeclaredOutputs = []
        };

        Assert.True(task.Execute());
        Assert.Equal("https://example.com/dotnet/arcade", task.RewrittenRepositoryUrl);
        Assert.Equal("https://example.com/dotnet/arcade", Assert.Single(task.RewrittenSourceRoots).GetMetadata("ScmRepositoryUrl"));
        Assert.Empty(task.DeclaredInputs);
        Assert.Empty(task.DeclaredOutputs);
    }

    [Fact]
    public void GeneratesCSharpFromSandboxedDeclaredFiles()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "sandbox-tests", Guid.NewGuid().ToString("N"));
        string projectDirectory = Path.Combine(root, "project");
        string stagingDirectory = Path.Combine(root, "staging");
        string input = Path.Combine("input", "TestStrings.resx");
        string output = Path.Combine("output", "Resources.cs");
        string stagedInput = MapPath(Path.Combine(stagingDirectory, "inputs"), projectDirectory, input);
        string stagedOutput = MapPath(Path.Combine(stagingDirectory, "outputs"), projectDirectory, output);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagedInput));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedOutput));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "testassets", "Resources", "TestStrings.resx"),
                stagedInput);

            var task = new GenerateResxSource
            {
                BuildEngine = new MockEngine(_output),
                TaskEnvironment = CreateSandboxEnvironment(projectDirectory, stagingDirectory),
                ResourceFile = input,
                ResourceName = "Microsoft.DotNet.TestStrings",
                ResourceClassName = "Microsoft.DotNet.TestStrings",
                Language = "C#",
                OutputPath = output,
                DeclaredInputs = [new TaskItem(input)],
                DeclaredOutputs = [new TaskItem(output)]
            };

            Assert.True(task.Execute(), "Task failed");
            Assert.True(File.Exists(stagedOutput));
            Assert.False(File.Exists(Path.Combine(projectDirectory, output)));
            Assert.Contains("internal static partial class TestStrings", File.ReadAllText(stagedOutput));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RegexReplaceFileUsesSandboxedDeclaredPaths()
    {
        AssertFileTaskUsesSandboxedPaths(
            (input, output, environment) => new RegexReplaceFile
            {
                InputFile = new TaskItem(input),
                OutputFile = new TaskItem(output),
                Pattern = "old",
                Replacement = "new",
                TaskEnvironment = environment,
                DeclaredInputs = [new TaskItem(input)],
                DeclaredOutputs = [new TaskItem(output)]
            },
            "old value",
            "new value" + Environment.NewLine);
    }

    [Fact]
    public void ReplaceTextFileUsesSandboxedDeclaredPaths()
    {
        AssertFileTaskUsesSandboxedPaths(
            (input, output, environment) =>
            {
                TaskItem replacement = new("old");
                replacement.SetMetadata("Value", "new");
                return new ReplaceTextFile
                {
                    InputFile = new TaskItem(input),
                    OutputFile = new TaskItem(output),
                    Replacements = [replacement],
                    TaskEnvironment = environment,
                    DeclaredInputs = [new TaskItem(input)],
                    DeclaredOutputs = [new TaskItem(output)]
                };
            },
            "old value",
            "new value" + Environment.NewLine);
    }

    private void AssertFileTaskUsesSandboxedPaths(
        Func<string, string, TaskEnvironment, Microsoft.Build.Utilities.Task> createTask,
        string inputContents,
        string expectedOutputContents)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "sandbox-tests", Guid.NewGuid().ToString("N"));
        string projectDirectory = Path.Combine(root, "project");
        string stagingDirectory = Path.Combine(root, "staging");
        string input = Path.Combine("input", "source.txt");
        string output = Path.Combine("output", "result.txt");
        string stagedInput = MapPath(Path.Combine(stagingDirectory, "inputs"), projectDirectory, input);
        string stagedOutput = MapPath(Path.Combine(stagingDirectory, "outputs"), projectDirectory, output);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagedInput));
            File.WriteAllText(stagedInput, inputContents);

            Microsoft.Build.Utilities.Task task = createTask(
                input, output, CreateSandboxEnvironment(projectDirectory, stagingDirectory));
            task.BuildEngine = new MockEngine(_output);

            Assert.True(task.Execute(), "Task failed");
            Assert.Equal(expectedOutputContents, File.ReadAllText(stagedOutput));
            Assert.False(File.Exists(Path.Combine(projectDirectory, output)));
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

    private static void AssertPureDeclaredIOContract(Type taskType)
    {
        object[] attributes = taskType.GetCustomAttributes(inherit: false);
        object declaredIO = Assert.Single(Array.FindAll(
            attributes,
            attribute => attribute.GetType().FullName == "Microsoft.Build.Framework.MSBuildDeclaredIOTaskAttribute"));
        PropertyInfo sandboxVersion = declaredIO.GetType().GetProperty("SandboxVersion");
        Assert.NotNull(sandboxVersion);
        Assert.Equal(0, (int)sandboxVersion.GetValue(declaredIO)!);
        Assert.True(Array.Exists(
            attributes,
            attribute => attribute.GetType().FullName == "Microsoft.Build.Framework.MSBuildMultiThreadableTaskAttribute"));
    }
}
