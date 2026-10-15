// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the License.txt file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using LibGit2Sharp;
using TestUtilities;
using Xunit;

namespace Microsoft.SourceLink.IntegrationTests
{
    public class TargetTests : DotNetSdkTestBase
    {
        public TargetTests()
            : base("Microsoft.SourceLink.GitHub")
        {
        }

        [ConditionalFact(typeof(DotNetSdkAvailable))]
        public void RestoreGraphInputsCapturesAndRefreshesSourceControlWithoutImportingIt()
        {
            string revision;
            using (var repository = GitUtilities.CreateGitRepository(
                ProjectDir.Path, [ProjectFileName], "http://github.com/test-org/test-repo"))
            {
                revision = repository.Head.Tip.Sha;
            }

            void capture(string customProps = "") => VerifyValues(
                customProps: customProps,
                customTargets: "",
                targets: ["Restore"],
                expressions: ["$(_SourceLinkFetchManifestImported)"],
                expectedResults: [""],
                additionalCommandLineArgs: "/p:MSBuildRestoreGraphInputs=true");

            capture();
            string manifestPath = Path.Combine(ProjectObjDir.Path, "sourcelink", ProjectFileName + ".g.targets");
            string contents = File.ReadAllText(manifestPath);
            Assert.Contains(revision, contents);

            File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddDays(-1));
            DateTime timestamp = File.GetLastWriteTimeUtc(manifestPath);
            capture();
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(manifestPath));

            File.WriteAllText(manifestPath, "not an MSBuild project");
            capture("<PropertyGroup><EnableSourceControlFetchManifest>true</EnableSourceControlFetchManifest></PropertyGroup>");
            Assert.Equal(contents, File.ReadAllText(manifestPath));

            File.Delete(Path.Combine(ProjectDir.Path, ".git", "HEAD"));
            VerifyValues(
                customProps: "<PropertyGroup><EnableSourceControlFetchManifest>true</EnableSourceControlFetchManifest></PropertyGroup>",
                customTargets: "",
                targets: ["InitializeSourceControlInformation"],
                expressions: ["$(_SourceLinkFetchManifestImported)", "$(SourceRevisionId)"],
                expectedResults: ["true", revision],
                additionalCommandLineArgs: "/p:MSBuildHardenedGraph=true /p:MSBuildRestoreGraphInputs=true");
        }

        [ConditionalTheory(typeof(DotNetSdkAvailable))]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void RestoreDoesNotCaptureSourceControlWithoutBothOptIns(bool captureGraphInputs, bool enableQueries)
        {
            using var repository = GitUtilities.CreateGitRepository(
                ProjectDir.Path, [ProjectFileName], "http://github.com/test-org/test-repo");

            VerifyValues(
                customProps: "",
                customTargets: "",
                targets: ["Restore"],
                expressions: ["$(_SourceLinkFetchManifestImported)"],
                expectedResults: [""],
                additionalCommandLineArgs: $"/p:MSBuildRestoreGraphInputs={captureGraphInputs} /p:EnableSourceControlManagerQueries={enableQueries}");

            Assert.False(File.Exists(Path.Combine(ProjectObjDir.Path, "sourcelink", ProjectFileName + ".g.targets")));
        }

        [ConditionalFact(typeof(DotNetSdkAvailable))]
        public void GenerateSourceLinkFileTarget_EnableSourceLinkCondition()
        {
            GitUtilities.CreateGitRepository(ProjectDir.Path, new[] { ProjectFileName }, "http://github.com/test-org/test-repo");

            VerifyValues(
                customProps: @"
<PropertyGroup>
  <Test_DefaultEnableSourceControlManagerQueries>$(EnableSourceControlManagerQueries)</Test_DefaultEnableSourceControlManagerQueries>
  <Test_DefaultEnableSourceLink>$(EnableSourceLink)</Test_DefaultEnableSourceLink>
</PropertyGroup>

",
                customTargets: @"
<Target Name=""Test_SetEnableSourceLink"" BeforeTargets=""InitializeSourceControlInformation"">
  <PropertyGroup>
    <EnableSourceLink>false</EnableSourceLink>
  </PropertyGroup>
</Target>
",
                targets: new[]
                {
                    "Build"
                },
                expressions: new[]
                {
                    "@(SourceRoot)",
                    "@(SourceRoot->'%(BranchName)')",
                    "$(Test_DefaultEnableSourceControlManagerQueries)",
                    "$(Test_DefaultEnableSourceLink)",
                    "$(SourceLink)"
                },
                expectedResults: new[]
                {
                    NuGetPackageFolders,
                    ProjectSourceRoot,
                    "refs/heads/main",
                    "true",
                    "true",
                    ""
                });
        }

        [ConditionalFact(typeof(DotNetSdkAvailable))]
        public void DefaultValuesForEnableProperties_DesignTimeBuild()
        {
            GitUtilities.CreateGitRepository(ProjectDir.Path, new[] { ProjectFileName }, "http://github.com/test-org/test-repo");

            VerifyValues(
                customProps: @"
<PropertyGroup>
  <Test_DefaultEnableSourceControlManagerQueries>$(EnableSourceControlManagerQueries)</Test_DefaultEnableSourceControlManagerQueries>
  <Test_DefaultEnableSourceLink>$(EnableSourceLink)</Test_DefaultEnableSourceLink>
</PropertyGroup>

",
                customTargets: "",
                targets: new[]
                {
                    "Build"
                },
                expressions: new[]
                {
                    "@(SourceRoot)",
                    "@(SourceRoot->'%(BranchName)')",
                    "$(Test_DefaultEnableSourceControlManagerQueries)",
                    "$(Test_DefaultEnableSourceLink)",
                    "$(SourceLink)"
                },
                expectedResults: new[]
                {
                    NuGetPackageFolders,
                    ProjectSourceRoot,
                    "refs/heads/main",
                    "true",
                    "",
                    ""
                },
                additionalCommandLineArgs: "/p:DesignTimeBuild=true");
        }

        [ConditionalFact(typeof(DotNetSdkAvailable))]
        public void DefaultValuesForEnableProperties_BuildingForLiveUnitTesting()
        {
            GitUtilities.CreateGitRepository(ProjectDir.Path, new[] { ProjectFileName }, "http://github.com/test-org/test-repo");

            VerifyValues(
                customProps: @"
<PropertyGroup>
  <Test_DefaultEnableSourceControlManagerQueries>$(EnableSourceControlManagerQueries)</Test_DefaultEnableSourceControlManagerQueries>
  <Test_DefaultEnableSourceLink>$(EnableSourceLink)</Test_DefaultEnableSourceLink>
</PropertyGroup>

",
                customTargets: "",
                targets: new[]
                {
                    "Build"
                },
                expressions: new[]
                {
                    "@(SourceRoot)",
                    "@(SourceRoot->'%(BranchName)')",
                    "$(Test_DefaultEnableSourceControlManagerQueries)",
                    "$(Test_DefaultEnableSourceLink)",
                    "$(SourceLink)"
                },
                expectedResults: new[]
                {
                    NuGetPackageFolders,
                    ProjectSourceRoot,
                    "refs/heads/main",
                    "true",
                    "",
                    ""
                },
                additionalCommandLineArgs: "/p:BuildingForLiveUnitTesting=true");
        }
    }
}
