// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.DotNet.Utilities;

namespace Microsoft.NET.Build.Tests
{
    [TestClass]
    public class GivenThatWeWantToBuildANetCoreAppAndPassingALogger : SdkTest
    {

        private string CreateTargetFrameworkEvalTelemetryJson(
            string targetFrameworkVersion,
            string targetPlatformIdentifier = "null",
            string runtimeIdentifier = "null",
            string selfContained = "null",
            string useApphost = "null",
            string outputType = "Library",
            string useArtifactsOutput = "null",
            string artifactsPathLocationType = "null",
            string useMonoRuntime = "null",
            string publishAot = "null",
            string publishTrimmed = "null",
            string publishSelfContained = "null",
            string publishReadyToRun = "null",
            string publishReadyToRunComposite = "false",
            string publishProtocol = "null",
            string configuration = "Debug")
        {
            return $"{{\"EventName\":\"targetframeworkeval\",\"Properties\":{{\"TargetFrameworkVersion\":\"{targetFrameworkVersion}\",\"RuntimeIdentifier\":\"{runtimeIdentifier}\",\"SelfContained\":\"{selfContained}\",\"UseApphost\":\"{useApphost}\",\"OutputType\":\"{outputType}\",\"UseArtifactsOutput\":\"{useArtifactsOutput}\",\"ArtifactsPathLocationType\":\"{artifactsPathLocationType}\",\"TargetPlatformIdentifier\":\"{targetPlatformIdentifier}\",\"UseMonoRuntime\":\"{useMonoRuntime}\",\"PublishAot\":\"{publishAot}\",\"PublishTrimmed\":\"{publishTrimmed}\",\"PublishSelfContained\":\"{publishSelfContained}\",\"PublishReadyToRun\":\"{publishReadyToRun}\",\"PublishReadyToRunComposite\":\"{publishReadyToRunComposite}\",\"PublishProtocol\":\"{publishProtocol}\",\"Configuration\":\"{Sha256Hasher.HashWithNormalizedCasing(configuration)}\"}}";
        }

        [TestMethod]
        [CoreMSBuildOnly]
        public void It_collects_TargetFramework_version_and_other_properties()
        {
            string targetFramework = ToolsetInfo.CurrentTargetFramework;
            var testProject = new TestProject()
            {
                Name = "FrameworkTargetTelemetryTest",
                TargetFrameworks = targetFramework,
            };
            Type loggerType = typeof(LogTelemetryToStdOutForTest);
            var TelemetryTestLogger = new[]
                {
                    $"/Logger:{loggerType.FullName},{loggerType.GetTypeInfo().Assembly.Location}"
                };
            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute(TelemetryTestLogger)
                .StdOut.Should()
                .Contain(CreateTargetFrameworkEvalTelemetryJson(
                    $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}"));
        }

        [TestMethod]
        [CoreMSBuildOnly]
        [DataRow(false)]
        [DataRow(true)]
        public void It_skips_target_framework_telemetry_only_for_hardened_graph_construction(bool hardened)
        {
            var testProject = new TestProject()
            {
                Name = "HardenedFrameworkTelemetryTest",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
            };
            var testAsset = TestAssetsManager.CreateTestProject(testProject, identifier: hardened.ToString());
            Type loggerType = typeof(LogTelemetryToStdOutForTest);
            var result = new MSBuildCommand(testAsset, "_CollectTargetFrameworkForTelemetry")
                .Execute(
                    $"-p:MSBuildHardenedGraph={hardened}",
                    $"/Logger:{loggerType.FullName},{loggerType.GetTypeInfo().Assembly.Location}");

            result.Should().Pass();
            if (hardened)
            {
                result.StdOut.Should().NotContain("\"EventName\":\"targetframeworkeval\"");
            }
            else
            {
                result.StdOut.Should().Contain(CreateTargetFrameworkEvalTelemetryJson(
                    $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}"));
            }
        }

        [TestMethod]
        [CoreMSBuildOnly]
        public void It_collects_multi_TargetFramework_version_and_other_properties()
        {
            string targetFramework = $"net46;{ToolsetInfo.CurrentTargetFramework}";

            var testProject = new TestProject()
            {
                Name = "MultitargetTelemetry",
                TargetFrameworks = targetFramework,
            };
            Type loggerType = typeof(LogTelemetryToStdOutForTest);
            var TelemetryTestLogger = new[]
                {
                    $"/Logger:{loggerType.FullName},{loggerType.GetTypeInfo().Assembly.Location}"
                };
            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            var buildCommand = new BuildCommand(testAsset);

            var result = buildCommand
                .Execute(TelemetryTestLogger);

            result
                .StdOut.Should()
                .Contain(CreateTargetFrameworkEvalTelemetryJson(
                    ".NETFramework,Version=v4.6",
                    targetPlatformIdentifier: "Windows",
                    publishReadyToRunComposite: "null"))
                .And
                .Contain(CreateTargetFrameworkEvalTelemetryJson(
                    $".NETCoreApp,Version=v{ToolsetInfo.CurrentTargetFrameworkVersion}"));
        }
    }
}
