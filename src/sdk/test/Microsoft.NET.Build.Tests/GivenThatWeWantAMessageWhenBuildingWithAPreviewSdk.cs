// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.Build.Tasks;

namespace Microsoft.NET.Build.Tests
{
    [TestClass]
    public class GivenThatWeWantAMessageWhenBuildingWithAPreviewSdk : SdkTest
    {

        [TestMethod]
        public void It_displays_a_preview_message_when_using_a_preview_Sdk()
        {
            TestAsset testAsset = TestAssetsManager
                .CopyTestAsset("HelloWorld")
                .WithSource();

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute("/p:_NETCoreSdkIsPreview=true")
                .Should()
                .Pass()
                .And.HaveStdOutContaining(Strings.UsingPreviewSdk);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void It_skips_the_preview_message_only_for_hardened_graph_construction(bool hardenedGraph)
        {
            var project = new TestProject
            {
                Name = "HardenedPreviewMessage",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
            };
            var testAsset = TestAssetsManager.CreateTestProject(project, identifier: hardenedGraph.ToString());

            new RestoreCommand(testAsset)
                .Execute("/p:MSBuildRestoreGraphInputs=true")
                .Should().Pass();

            var result = new MSBuildCommand(testAsset, "_CheckForNETCoreSdkIsPreview")
                .ExecuteWithoutRestore(
                    "/p:_NETCoreSdkIsPreview=true",
                    "/p:MSBuildRestoreGraphInputs=true",
                    $"/p:MSBuildHardenedGraph={hardenedGraph}");

            result.Should().Pass();
            if (hardenedGraph)
            {
                result.StdOut.Should().NotContain(Strings.UsingPreviewSdk);
            }
            else
            {
                result.StdOut.Should().Contain(Strings.UsingPreviewSdk);
            }
        }

        [TestMethod]
        public void It_does_not_display_preview_message_with_explicit_opt_out()
        {
            TestAsset testAsset = TestAssetsManager
                .CopyTestAsset("HelloWorld")
                .WithSource();

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute("/p:_NETCoreSdkIsPreview=true", "/p:SuppressNETCoreSdkPreviewMessage=true")
                .Should()
                .Pass()
                .And.NotHaveStdOutContaining(Strings.UsingPreviewSdk);
        }

        [TestMethod]
        public void It_does_not_display_preview_message_with_nowarn_opt_out()
        {
            TestAsset testAsset = TestAssetsManager
                .CopyTestAsset("HelloWorld")
                .WithSource();

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute("/p:_NETCoreSdkIsPreview=true", "/p:NoWarn=NETSDK1057")
                .Should()
                .Pass()
                .And.NotHaveStdOutContaining(Strings.UsingPreviewSdk);
        }

        [TestMethod]
        public void It_does_not_display_a_preview_message_when_using_a_release_Sdk()
        {
            TestAsset testAsset = TestAssetsManager
                .CopyTestAsset("HelloWorld")
                .WithSource();

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute("/p:_NETCoreSdkIsPreview=")
                .Should()
                .Pass()
                .And.NotHaveStdOutContaining(Strings.UsingPreviewSdk);
        }
    }
}
