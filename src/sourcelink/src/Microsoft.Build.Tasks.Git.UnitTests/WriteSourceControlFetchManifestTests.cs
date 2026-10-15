// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using TestUtilities;
using Xunit;

namespace Microsoft.Build.Tasks.Git.UnitTests
{
    public class WriteSourceControlFetchManifestTests
    {
        [Fact]
        public void WritesImportablePropertiesAndItems()
        {
            using var temp = new TempRoot();
            TempDirectory outputDirectory = temp.CreateDirectory();
            string outputFile = Path.Combine(outputDirectory.Path, "project.sourcelink.g.targets");
            string repositoryRoot = Path.Combine(outputDirectory.Path, "repo") + Path.DirectorySeparatorChar;
            string repositoryUrl = "https://github.com/test/repo%2Fname";
            string revisionId = "0123456789012345678901234567890123456789";
            string branchName = "refs/heads/test";
            string untrackedFile = Path.Combine(repositoryRoot, "Generated.cs");

            var sourceRoot = new MockItem(
                repositoryRoot,
                new KeyValuePair<string, string>("SourceControl", "git"),
                new KeyValuePair<string, string>("ScmRepositoryUrl", repositoryUrl),
                new KeyValuePair<string, string>("RevisionId", revisionId),
                new KeyValuePair<string, string>("BranchName", branchName));

            var task = new WriteSourceControlFetchManifest
            {
                BuildEngine = new MockEngine(),
                OutputFile = outputFile,
                RepositoryRoot = repositoryRoot,
                RepositoryUrl = repositoryUrl,
                RevisionId = revisionId,
                BranchName = branchName,
                SourceRoots = new ITaskItem[] { sourceRoot },
                UntrackedFiles = new ITaskItem[] { new MockItem(untrackedFile) },
                RepositoryUrlsTranslated = true,
            };

            Assert.True(task.Execute());

            using var projectCollection = new ProjectCollection();
            var project = new Project(outputFile, null, null, projectCollection);

            Assert.Equal("2", project.GetPropertyValue("_SourceLinkFetchManifestVersion"));
            Assert.Equal("true", project.GetPropertyValue("_SourceLinkFetchManifestImported"));
            Assert.Equal("true", project.GetPropertyValue("_SourceLinkFetchManifestContainsTranslatedUrls"));
            Assert.Equal(repositoryRoot, project.GetPropertyValue("_SourceLinkRepositoryRoot"));
            Assert.Equal(repositoryUrl, project.GetPropertyValue("ScmRepositoryUrl"));
            Assert.Equal(revisionId, project.GetPropertyValue("SourceRevisionId"));
            Assert.Equal(branchName, project.GetPropertyValue("SourceBranchName"));
            Assert.Equal("git", project.GetPropertyValue("RepositoryType"));

            ProjectItem evaluatedSourceRoot = Assert.Single(project.GetItems("SourceRoot"));
            Assert.Equal(repositoryRoot, evaluatedSourceRoot.EvaluatedInclude);
            Assert.Equal(repositoryUrl, evaluatedSourceRoot.GetMetadataValue("ScmRepositoryUrl"));
            Assert.Equal(revisionId, evaluatedSourceRoot.GetMetadataValue("RevisionId"));

            Assert.Equal(
                untrackedFile,
                Assert.Single(project.GetItems("_SourceLinkFetchedUntrackedFile")).EvaluatedInclude);
        }

        [Fact]
        public void PreservesAnExplicitRevision()
        {
            using var temp = new TempRoot();
            string outputFile = Path.Combine(temp.CreateDirectory().Path, "project.sourcelink.g.targets");
            var task = new WriteSourceControlFetchManifest
            {
                BuildEngine = new MockEngine(),
                OutputFile = outputFile,
                RevisionId = "fetched",
            };

            Assert.True(task.Execute());

            using var projectCollection = new ProjectCollection();
            var project = new Project(
                outputFile,
                new Dictionary<string, string> { ["SourceRevisionId"] = "explicit" },
                null,
                projectCollection);

            Assert.Equal("explicit", project.GetPropertyValue("SourceRevisionId"));
            Assert.Equal("false", project.GetPropertyValue("_SourceLinkFetchManifestContainsTranslatedUrls"));
        }
    }
}
