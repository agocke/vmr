// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Text;
using System.Xml;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks.SourceControl;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Tasks.Git
{
    [MSBuildMultiThreadableTask]
    public sealed class WriteSourceControlFetchManifest : Task
    {
        private static readonly string[] SourceRootMetadataNames =
        {
            Names.SourceRoot.SourceControl,
            Names.SourceRoot.ScmRepositoryUrl,
            Names.SourceRoot.RevisionId,
            Names.SourceRoot.BranchName,
            Names.SourceRoot.ContainingRoot,
            Names.SourceRoot.NestedRoot,
        };

        [Required]
        public string OutputFile { get; set; } = null!;

        public string? RepositoryRoot { get; set; }
        public string? RepositoryUrl { get; set; }
        public string? RevisionId { get; set; }
        public string? BranchName { get; set; }
        public ITaskItem[]? SourceRoots { get; set; }
        public ITaskItem[]? UntrackedFiles { get; set; }
        public bool RepositoryUrlsTranslated { get; set; }

        public override bool Execute()
        {
            if (!PathUtilities.IsPathFullyQualified(OutputFile))
            {
                Log.LogError(Resources.PathMustBeAbsolute);
                return false;
            }

            try
            {
                string content = GenerateManifest();
                if (File.Exists(OutputFile) && File.ReadAllText(OutputFile) == content)
                {
                    return true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(OutputFile)!);
                File.WriteAllText(OutputFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }
            catch (IOException e)
            {
                Log.LogErrorFromException(e);
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                Log.LogErrorFromException(e);
                return false;
            }
        }

        private string GenerateManifest()
        {
            var content = new StringBuilder();
            using (XmlWriter writer = XmlWriter.Create(content, new XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = true,
            }))
            {
                writer.WriteStartElement("Project");
                writer.WriteStartElement("PropertyGroup");
                WriteProperty(writer, "_SourceLinkFetchManifestVersion", "2");
                WriteProperty(writer, "_SourceLinkFetchManifestImported", "true");
                WriteProperty(
                    writer,
                    "_SourceLinkFetchManifestContainsTranslatedUrls",
                    RepositoryUrlsTranslated ? "true" : "false");
                WriteProperty(writer, "_SourceLinkRepositoryRoot", RepositoryRoot);
                WriteProperty(writer, "ScmRepositoryUrl", RepositoryUrl);
                WriteProperty(writer, "SourceRevisionId", RevisionId, "'$(SourceRevisionId)' == ''");
                WriteProperty(writer, "SourceBranchName", BranchName);
                WriteProperty(writer, "RepositoryType", "git", "'$(RepositoryType)' == ''");
                writer.WriteEndElement();

                writer.WriteStartElement("ItemGroup");
                if (SourceRoots != null)
                {
                    foreach (ITaskItem sourceRoot in SourceRoots)
                    {
                        writer.WriteStartElement(Names.SourceRoot.Name);
                        writer.WriteAttributeString("Include", Escape(sourceRoot.ItemSpec));

                        foreach (string metadataName in SourceRootMetadataNames)
                        {
                            string value = sourceRoot.GetMetadata(metadataName);
                            if (!string.IsNullOrEmpty(value))
                            {
                                writer.WriteElementString(metadataName, Escape(value));
                            }
                        }

                        writer.WriteEndElement();
                    }
                }

                if (UntrackedFiles != null)
                {
                    foreach (ITaskItem untrackedFile in UntrackedFiles)
                    {
                        writer.WriteStartElement("_SourceLinkFetchedUntrackedFile");
                        writer.WriteAttributeString("Include", Escape(untrackedFile.ItemSpec));
                        writer.WriteEndElement();
                    }
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            return content.ToString();
        }

        private static void WriteProperty(XmlWriter writer, string name, string? value, string? condition = null)
        {
            writer.WriteStartElement(name);
            if (condition != null)
            {
                writer.WriteAttributeString("Condition", condition);
            }

            writer.WriteString(Escape(value));
            writer.WriteEndElement();
        }

        private static string Escape(string? value)
            => ProjectCollection.Escape(value ?? string.Empty);
    }
}
