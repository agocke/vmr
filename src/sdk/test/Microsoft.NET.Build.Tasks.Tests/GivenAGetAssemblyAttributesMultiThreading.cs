// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using FluentAssertions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks.UnitTests
{
    [TestClass]
    public class GivenAGetAssemblyAttributesMultiThreading
    {
        [TestMethod]
        public void ItResolvesRelativePathsViaTaskEnvironment()
        {
            // Create a temp directory to act as a fake project dir (different from CWD)
            var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(projectDir);
            try
            {
                // We need a real .NET assembly file for FileVersionInfo.GetVersionInfo to work.
                // Copy the test assembly itself to the project directory with a relative-path-friendly name.
                var sourceAssembly = typeof(GivenAGetAssemblyAttributesMultiThreading).Assembly.Location;
                var relativeFileName = "template.dll";
                var expectedAbsPath = Path.Combine(projectDir, relativeFileName);
                File.Copy(sourceAssembly, expectedAbsPath);

                var task = new GetAssemblyAttributes
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                    PathToTemplateFile = relativeFileName,
                };

                var result = task.Execute();

                result.Should().BeTrue("the task should succeed when the file exists under the project directory");
                task.AssemblyAttributes.Should().NotBeNull();
            }
            finally
            {
                Directory.Delete(projectDir, true);
            }
        }

        [TestMethod]
        public void SatelliteGeneratorUsesStagedInputsAndOutputs()
        {
            string projectDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            string executionDirectory = Path.Combine(projectDirectory, "execution");
            string stagedTemplate = Path.Combine(executionDirectory, "inputs", "template.dll");
            string stagedOutput = Path.Combine(executionDirectory, "outputs", "Generated.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedTemplate)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedOutput)!);
            try
            {
                string logicalTemplate = Path.Combine(projectDirectory, "template.dll");
                string logicalOutput = Path.Combine(projectDirectory, "Generated.cs");
                File.Copy(typeof(GivenAGetAssemblyAttributesMultiThreading).Assembly.Location, stagedTemplate);

                var inputPaths = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [logicalTemplate] = stagedTemplate
                };
                var outputPaths = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [logicalOutput] = stagedOutput
                };
                var task = new GenerateSatelliteAssemblySource
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForStagedTest(
                        projectDirectory,
                        executionDirectory,
                        path => inputPaths[path],
                        path => outputPaths[path]),
                    PathToTemplateFile = "template.dll",
                    OutputFile = "Generated.cs",
                    Culture = "fr",
                    DeclaredInputs = [new TaskItem("template.dll")],
                    DeclaredOutputs = [new TaskItem("Generated.cs")]
                };

                task.Execute().Should().BeTrue();

                File.Exists(stagedOutput).Should().BeTrue();
                File.Exists(logicalOutput).Should().BeFalse();
                File.ReadAllText(stagedOutput).Should().Contain("AssemblyCultureAttribute")
                    .And.Contain("\"fr\"");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    Directory.Delete(projectDirectory, recursive: true);
                }
            }
        }
    }
}
