// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using XliffTasks.Tasks;
using Xunit;

namespace XliffTasks.Tests;

public class GatherXlfTests
{
    [Theory]
    [InlineData("Strings.resx", "Strings.fr.xlf", "Strings.xlf")]
    [InlineData("Settings.json", "Settings.json.fr.xlf", "Settings.json.xlf")]
    public void FileInputsIncludeNeutralXlfAndHonorSourceDocumentPath(
        string filename, string translatedXlf, string neutralXlf)
    {
        TaskItem source = new(Path.Combine("generated", filename));
        source.SetMetadata("SourceDocumentPath", Path.Combine("sources", filename));
        GatherXlf task = new()
        {
            Sources = new[] { source },
            Languages = new[] { "fr", "en" },
            TranslatedOutputDirectory = "translated"
        };

        Assert.True(task.Execute());
        Assert.Collection(task.FileInputs,
            item => Assert.Equal(source.ItemSpec, item.ItemSpec),
            item => Assert.Equal(Path.Combine("sources", "xlf", translatedXlf), item.ItemSpec),
            item => Assert.Equal(Path.Combine("sources", "xlf", neutralXlf), item.ItemSpec));
        Assert.Equal(Path.Combine("sources", "xlf", translatedXlf), Assert.Single(task.Outputs).ItemSpec);
        Assert.Equal(source.ItemSpec, task.Outputs[0].GetMetadata("XlfSource"));
    }

    [Fact]
    public void GatherTasksArePureDeclaredIOTasks()
    {
        AssertPureDeclaredIOContract(typeof(GatherXlf));
        AssertPureDeclaredIOContract(typeof(GatherTranslatedSource));

        GatherXlf gatherXlf = new()
        {
            Sources = [new TaskItem("Strings.resx")],
            Languages = ["fr", "en"],
            TranslatedOutputDirectory = "translated",
            DeclaredInputs = [],
            DeclaredOutputs = []
        };

        Assert.True(gatherXlf.Execute());
        Assert.Single(gatherXlf.Outputs);
        Assert.Equal(3, gatherXlf.FileInputs.Length);

        TaskItem xlf = new("xlf/Strings.fr.xlf");
        xlf.SetMetadata("XlfTranslatedFullPath", "translated/Strings.fr.resx");
        xlf.SetMetadata("XlfLanguage", "fr");
        xlf.SetMetadata("XlfSource", "Strings.resx");
        GatherTranslatedSource gatherTranslatedSource = new()
        {
            XlfFiles = [xlf],
            DeclaredInputs = [],
            DeclaredOutputs = []
        };

        Assert.True(gatherTranslatedSource.Execute());
        ITaskItem output = Assert.Single(gatherTranslatedSource.Outputs);
        Assert.Equal("translated/Strings.fr.resx", output.ItemSpec);
        Assert.Equal("Strings.fr.resx", output.GetMetadata("Link"));
        Assert.Empty(gatherXlf.DeclaredInputs);
        Assert.Empty(gatherXlf.DeclaredOutputs);
        Assert.Empty(gatherTranslatedSource.DeclaredInputs);
        Assert.Empty(gatherTranslatedSource.DeclaredOutputs);
    }

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
