// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
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
}
