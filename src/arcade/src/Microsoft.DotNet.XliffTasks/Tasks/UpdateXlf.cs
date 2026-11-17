// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using System.IO;
using XliffTasks.Model;

namespace XliffTasks.Tasks;

[MSBuildMultiThreadableTask]
public sealed class UpdateXlf : XlfTask
{
    [Required]
    public ITaskItem[] Sources { get; set; }

    [Required]
    public string[] Languages { get; set; }

    [Required]
    public bool AllowModification { get; set; }

    private const string HowToUpdate =
        "Run `msbuild /t:UpdateXlf` to update .xlf files or set UpdateXlfOnBuild=true"
        + " to update them on every build, but note that it is strongly discouraged to set"
        + " UpdateXlfOnBuild=true in official/CI build environments as they should not"
        + " modify source code during the build.";

    protected override void ExecuteCore()
    {
        if (TaskEnvironment.IsSandboxed)
        {
            Log.LogError("UpdateXlf is not supported in sandboxed execution because it modifies XLF files in place.");
            return;
        }

        UpdateSources(Sources, Languages, AllowModification, TaskEnvironment);
    }

    internal static void UpdateSources(
        ITaskItem[] sources, string[] languages, bool allowModification,
        TaskEnvironment taskEnvironment)
    {
        if (allowModification && taskEnvironment.IsSandboxed)
        {
            throw new BuildErrorException("UpdateXlf cannot modify source XLF files in place during sandboxed execution.");
        }

        foreach (ITaskItem item in sources)
        {
            string sourcePath = item.ItemSpec;
            string sourceDocumentPath = item.GetMetadataOrDefault(MetadataKey.SourceDocumentPath, item.ItemSpec);
            string sourceFormat = item.GetMetadataOrThrow(MetadataKey.XlfSourceFormat);
            AbsolutePath logicalSourcePath = taskEnvironment.GetAbsolutePath(sourcePath);
            TranslatableDocument sourceDocument = XlfTask.LoadSourceDocument(
                logicalSourcePath, taskEnvironment.GetInputPath(sourcePath), sourceFormat);
            string sourceDocumentId = XlfTask.GetSourceDocumentId(sourcePath);

            foreach (string language in languages)
            {
                string xlfPath = XlfTask.GetXlfPath(sourceDocumentPath, language);
                AbsolutePath absoluteXlfPath = taskEnvironment.GetAbsolutePath(xlfPath);
                AbsolutePath inputXlfPath = taskEnvironment.GetInputPath(xlfPath);
                XlfDocument xlfDocument;

                try
                {
                    xlfDocument = XlfTask.LoadXlfDocument(
                        absoluteXlfPath, inputXlfPath, language, createIfNonExistent: allowModification);
                }
                // The helper records the logical path in FileName rather than exposing the staged path.
                catch (FileNotFoundException fileNotFoundEx) when (fileNotFoundEx.FileName == absoluteXlfPath.GetCanonicalForm().Value)
                {
                    Release.Assert(!allowModification);
                    throw new BuildErrorException($"'{xlfPath}' for '{sourcePath}' does not exist. {HowToUpdate}");
                }
                catch (System.Xml.XmlException xmlEx)
                {
                    throw new BuildErrorException($"Unable to load file: {xmlEx.Message}")
                    {
                        RelatedFile = xlfPath
                    };
                }

                bool updated = xlfDocument.Update(sourceDocument, sourceDocumentId);

                if (!updated)
                {
                    continue; // no changes
                }

                if (!allowModification)
                {
                    throw new BuildErrorException($"'{xlfPath}' is out-of-date with '{sourcePath}'. {HowToUpdate}");
                }

                Directory.CreateDirectory(taskEnvironment.GetExecutionDirectory(
                    Path.GetDirectoryName(taskEnvironment.GetAbsolutePath(xlfPath))));
                xlfDocument.Save(new FileInfo(taskEnvironment.GetOutputPath(xlfPath)));
            }
        }
    }
}
