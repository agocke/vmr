// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Framework;
using System.Collections.Generic;
using System.IO;
using XliffTasks.Model;

namespace XliffTasks.Tasks;

[MSBuildMultiThreadableTask]
[MSBuildDeclaredIOTask(SandboxVersion = 1)]
public sealed class TranslateSource : XlfTask
{
    [Required]
    public ITaskItem XlfFile { get; set; }

    protected override void ExecuteCore()
    {
        string sourcePath = XlfFile.GetMetadataOrThrow(MetadataKey.XlfSource);
        string sourceFormat = XlfFile.GetMetadataOrThrow(MetadataKey.XlfSourceFormat);
        string language = XlfFile.GetMetadataOrThrow(MetadataKey.XlfLanguage);
        string translatedFullPath = XlfFile.GetMetadataOrThrow(MetadataKey.XlfTranslatedFullPath);

        AbsolutePath logicalSourcePath = TaskEnvironment.GetAbsolutePath(sourcePath);
        AbsolutePath logicalTranslatedPath = TaskEnvironment.GetAbsolutePath(translatedFullPath);
        AbsolutePath logicalXlfPath = TaskEnvironment.GetAbsolutePath(XlfFile.ItemSpec);
        if (TaskEnvironment.IsSandboxed
            && (PathComparer.Equals(logicalSourcePath.GetCanonicalForm().Value, logicalTranslatedPath.GetCanonicalForm().Value)
                || PathComparer.Equals(logicalXlfPath.GetCanonicalForm().Value, logicalTranslatedPath.GetCanonicalForm().Value)))
        {
            Log.LogError("Sandboxed source translation requires distinct input and output paths.");
            return;
        }

        AbsolutePath sourceAbsolutePath = TaskEnvironment.GetInputPath(sourcePath);
        AbsolutePath translatedAbsolutePath = TaskEnvironment.GetOutputPath(translatedFullPath);
        TranslatableDocument sourceDocument = XlfTask.LoadSourceDocument(
            logicalSourcePath, sourceAbsolutePath, XlfFile.GetMetadata(MetadataKey.XlfSourceFormat));
        XlfDocument xlfDocument = XlfTask.LoadXlfDocument(
            logicalXlfPath, TaskEnvironment.GetInputPath(XlfFile.ItemSpec));

        bool validationFailed = false;
        xlfDocument.Validate(validationError =>
        {
            validationFailed = true;
            Log.LogErrorInFile(XlfFile.ItemSpec, validationError.LineNumber, validationError.Message);
        });

        IReadOnlyDictionary<string, string> translations = validationFailed
            ? new Dictionary<string, string>()
            : xlfDocument.GetTranslations();

        sourceDocument.Translate(translations);

        Directory.CreateDirectory(TaskEnvironment.GetExecutionDirectory(
            Path.GetDirectoryName(TaskEnvironment.GetAbsolutePath(translatedFullPath))));

        sourceDocument.RewriteRelativePathsToAbsolute(logicalSourcePath.GetCanonicalForm());
        sourceDocument.Save(new FileInfo(translatedAbsolutePath));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
