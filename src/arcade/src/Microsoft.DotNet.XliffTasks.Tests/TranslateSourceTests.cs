// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using XliffTasks.Tasks;
using Xunit;

namespace XliffTasks.Tests;

public class TranslateSourceTests
{
    [Fact]
    public void ReadsDeclaredInputsAndWritesDeclaredOutputFromSandboxedPaths()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "sandbox-tests", Guid.NewGuid().ToString("N"));
        string projectDirectory = Path.Combine(root, "project");
        string stagingDirectory = Path.Combine(root, "staging");
        string source = Path.Combine("input", "test.resx");
        string xlf = Path.Combine("input", "xlf", "test.fr.xlf");
        string output = Path.Combine("output", "test.fr.resx");
        string stagedSource = MapPath(Path.Combine(stagingDirectory, "inputs"), projectDirectory, source);
        string stagedXlf = MapPath(Path.Combine(stagingDirectory, "inputs"), projectDirectory, xlf);
        string stagedOutput = MapPath(Path.Combine(stagingDirectory, "outputs"), projectDirectory, output);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagedSource));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedXlf));
            File.WriteAllText(stagedSource, "<root><data name=\"Hello\"><value>Hello</value></data></root>");
            File.WriteAllText(stagedXlf,
@"<xliff xmlns=""urn:oasis:names:tc:xliff:document:1.2"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" version=""1.2"" xsi:schemaLocation=""urn:oasis:names:tc:xliff:document:1.2 xliff-core-1.2-transitional.xsd"">
  <file datatype=""xml"" source-language=""en"" target-language=""fr"" original=""../test.resx"">
    <body>
      <trans-unit id=""Hello"">
        <source>Hello</source>
        <target state=""translated"">Bonjour</target>
        <note />
      </trans-unit>
    </body>
  </file>
</xliff>");

            TaskItem xlfItem = new(xlf);
            xlfItem.SetMetadata("XlfSource", source);
            xlfItem.SetMetadata("XlfSourceFormat", "Resx");
            xlfItem.SetMetadata("XlfLanguage", "fr");
            xlfItem.SetMetadata("XlfTranslatedFullPath", output);

            TranslateSource task = new()
            {
                XlfFile = xlfItem,
                DeclaredInputs = [new TaskItem(source), new TaskItem(xlf)],
                DeclaredOutputs = [new TaskItem(output)],
                TaskEnvironment = CreateSandboxEnvironment(projectDirectory, stagingDirectory)
            };

            Assert.True(task.Execute());
            XDocument translated = XDocument.Load(stagedOutput);
            Assert.Equal("Bonjour", translated.Root.Element("data").Element("value").Value);
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
}
