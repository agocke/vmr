// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace Microsoft.NET.Build.Tests
{
    [TestClass]
    public class GivenFrameworkReferences : SdkTest
    {

        private const string FrameworkReferenceEmptyProgramSource = @"
using System;

namespace FrameworkReferenceTest
{
    public class Program
    {
        public static void Main(string [] args)
        {
        }
    }
}";

        [TestMethod]
        [OSCondition(OperatingSystems.Windows), RequiresMSBuildVersion("17.0.0.32901")]
        [DataRow(ToolsetInfo.CurrentTargetFramework, true)]
        [DataRow("netcoreapp3.1", false)]
        public void Multiple_frameworks_are_written_to_runtimeconfig_when_there_are_multiple_FrameworkReferences(string targetFramework, bool shouldIncludeBaseFramework)
        {
            var testProject = new TestProject()
            {
                Name = "MultipleFrameworkReferenceTest",
                TargetFrameworks = targetFramework,
                IsExe = true
            };

            testProject.FrameworkReferences.Add("Microsoft.ASPNETCORE.App");
            testProject.FrameworkReferences.Add("Microsoft.WindowsDesktop.App");

            testProject.SourceFiles.Add("Program.cs", FrameworkReferenceEmptyProgramSource);

            var testAsset = TestAssetsManager.CreateTestProject(testProject, identifier: targetFramework);

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            var outputDirectory = buildCommand.GetOutputDirectory(testProject.TargetFrameworks);

            string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
            var runtimeFrameworkNames = GetRuntimeFrameworks(runtimeConfigFile);

            if (shouldIncludeBaseFramework)
            {
                runtimeFrameworkNames.Should().BeEquivalentTo("Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.NETCore.App");
            }
            else
            {
                runtimeFrameworkNames.Should().BeEquivalentTo("Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App");
            }
        }

        [TestMethod]
        [DataRow("netcoreapp3.0", false)]
        [DataRow(ToolsetInfo.CurrentTargetFramework, true)]
        public void Multiple_frameworks_are_written_to_runtimeconfig_for_self_contained_apps(string tfm, bool shouldHaveIncludedFrameworks)
        {
            if (tfm == "netcoreapp3.0" &&
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                //  https://github.com/dotnet/sdk/issues/49665
                //  error NETSDK1084: There is no application host available for the specified RuntimeIdentifier 'osx-arm64'.
                return;
            }

            var testProject = new TestProject()
            {
                Name = "MultipleFrameworkReferenceTest",
                TargetFrameworks = tfm,
                IsExe = true,
                SelfContained = "true"
            };

            testProject.RuntimeIdentifier = EnvironmentInfo.GetCompatibleRid(testProject.TargetFrameworks);

            if (tfm == ToolsetInfo.CurrentTargetFramework)
            {
                testProject.FrameworkReferences.Add("Microsoft.ASPNETCORE.App");
            }

            testProject.SourceFiles.Add("Program.cs", FrameworkReferenceEmptyProgramSource);

            TestAsset testAsset = TestAssetsManager.CreateTestProject(testProject, identifier: tfm)
                .Restore(Log, testProject.Name);

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            DirectoryInfo outputDirectory = buildCommand.GetOutputDirectory(runtimeIdentifier: testProject.RuntimeIdentifier);

            string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
            List<string> includedFrameworkNames = GetIncludedFrameworks(runtimeConfigFile);
            if (shouldHaveIncludedFrameworks)
            {
                includedFrameworkNames.Should().BeEquivalentTo("Microsoft.NETCore.App", "Microsoft.AspNetCore.App");
            }
            else
            {
                includedFrameworkNames.Should().BeEmpty();
            }
        }

        [TestMethod]
        public void ForceGenerateRuntimeConfigurationFiles_works_even_on_netFramework_tfm()
        {
            var testProject = new TestProject()
            {
                Name = "NETFrameworkTFMTest",
                TargetFrameworks = "net472",
                IsExe = true
            };

            testProject.SourceFiles.Add("Program.cs", FrameworkReferenceEmptyProgramSource);
            testProject.AdditionalProperties.Add("GenerateRuntimeConfigurationFiles", "true");

            TestAsset testAsset = TestAssetsManager.CreateTestProject(testProject)
                .Restore(Log, testProject.Name);

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            DirectoryInfo outputDirectory = buildCommand.GetOutputDirectory(testProject.TargetFrameworks);
            string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
            Assert.IsTrue(File.Exists(runtimeConfigFile), $"Expected to generate runtime config file '{runtimeConfigFile}'");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows), RequiresMSBuildVersion("17.0.0.32901")]
        public void DuplicateFrameworksAreNotWrittenToRuntimeConfigWhenThereAreDifferentProfiles()
        {
            var testProject = new TestProject()
            {
                Name = "MultipleProfileFrameworkReferenceTest",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true
            };

            testProject.FrameworkReferences.Add("Microsoft.WindowsDesktop.App.WPF");
            testProject.FrameworkReferences.Add("Microsoft.WindowsDesktop.App.WindowsForms");

            testProject.SourceFiles.Add("Program.cs", FrameworkReferenceEmptyProgramSource);

            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            var outputDirectory = buildCommand.GetOutputDirectory(testProject.TargetFrameworks);

            string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
            var runtimeFrameworkNames = GetRuntimeFrameworks(runtimeConfigFile);

            runtimeFrameworkNames.Should().BeEquivalentTo("Microsoft.WindowsDesktop.App", "Microsoft.NETCore.App");
        }

        [TestMethod]
        public void The_build_fails_when_there_is_an_unknown_FrameworkReference()
        {
            var testProject = new TestProject()
            {
                Name = "UnknownFrameworkReferenceTest",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true
            };

            var testAsset = TestAssetsManager.CreateTestProject(testProject)
                .WithProjectChanges(project =>
                {
                    var ns = project.Root.Name.Namespace;

                    var itemGroup = new XElement(ns + "ItemGroup");
                    project.Root.Add(itemGroup);

                    itemGroup.Add(new XElement(ns + "FrameworkReference",
                                               new XAttribute("Include", "NotAKnownFramework")));
                    itemGroup.Add(new XElement(ns + "FrameworkReference",
                                               new XAttribute("Include", "AnotherUnknownFramework")));

                });

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute()
                .Should()
                .Fail()
                .And.HaveStdOutContaining("NETSDK1073")
                .And.HaveStdOutContaining("NotAKnownFramework")
                .And.HaveStdOutContaining("AnotherUnknownFramework")
                ;
        }

        [TestMethod]
        [DataRow("netcoreapp2.1", false)]
        [DataRow(ToolsetInfo.CurrentTargetFramework, true)]
        public void KnownFrameworkReferencesOnlyApplyToCorrectTargetFramework(string targetFramework, bool shouldPass)
        {
            var testProject = new TestProject()
            {
                Name = "FrameworkReferenceTest",
                TargetFrameworks = targetFramework,
                IsExe = true
            };

            var testAsset = TestAssetsManager.CreateTestProject(testProject, identifier: targetFramework)
                .WithProjectChanges(project =>
                {
                    var ns = project.Root.Name.Namespace;

                    var itemGroup = new XElement(ns + "ItemGroup");
                    project.Root.Add(itemGroup);

                    itemGroup.Add(new XElement(ns + "FrameworkReference",
                                               new XAttribute("Include", "Microsoft.ASPNETCORE.App")));
                });

            var buildCommand = new BuildCommand(testAsset);

            var result = buildCommand.Execute();

            if (shouldPass)
            {
                result.Should().Pass();
            }
            else
            {
                result
                    .Should()
                    .Fail()
                    .And.HaveStdOutContaining("NETSDK1073")
                    .And.HaveStdOutContaining("Microsoft.ASPNETCORE.App");
            }
        }

        [TestMethod]
        public void KnownFrameworkReferencesOnlyApplyToCorrectTargetPlatform()
        {
            var testProject = new TestProject()
            {
                Name = "FrameworkReferenceTest",
                TargetFrameworks = "net5.0-windows",
                IsExe = true
            };

            var testAsset = TestAssetsManager.CreateTestProject(testProject)
                .WithProjectChanges(project =>
                {
                    var ns = project.Root.Name.Namespace;

                    var itemGroup = new XElement(ns + "ItemGroup");
                    project.Root.Add(itemGroup);

                    //  Add a KnownFrameworkReference where the TargetPlatformVersion matches but the TargetPlatformIdentifier does not

                    itemGroup.Add(new XElement(ns + "KnownFrameworkReference",
                                               new XAttribute("Include", "NonExistentTestFrameworkReference"),
                                               new XAttribute("TargetFramework", "net5.0-notwindows7.0"),
                                               new XAttribute("RuntimeFrameworkName", "NonExistentTestFrameworkReference"),
                                               new XAttribute("DefaultRuntimeFrameworkVersion", "7.0"),
                                               new XAttribute("LatestRuntimeFrameworkVersion", "7.0"),
                                               new XAttribute("TargetingPackName", "NonExistentTestFrameworkReference"),
                                               new XAttribute("TargetingPackVersion", "7.0")));
                });

            var buildCommand = new BuildCommand(testAsset);

            //  The build should succeed because the fake KnownFrameworkReference should not match, and the SDK shouldn't try to download
            //  the nonexistent targeting pack.
            buildCommand.Execute()
                .Should()
                .Pass();
        }

        [TestMethod]
        public void TargetingPackDownloadCanBeDisabled()
        {
            var testProject = new TestProject()
            {
                Name = "DisableTargetingPackDownload",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true
            };

            testProject.AdditionalProperties["EnableTargetingPackDownload"] = "False";

            //  Set targeting pack folder to nonexistent folder so the project won't use installed targeting packs
            testProject.AdditionalProperties["NetCoreTargetingPackRoot"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            //  Package pruning may load data from the targeting packs directory.  Since we're disabling the targeting pack
            //  root, we need to allow it to succeed even if it can't find that data.
            testProject.AdditionalProperties["AllowMissingPrunePackageData"] = "true";

            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            string nugetPackagesFolder = Path.Combine(testAsset.TestRoot, "packages");


            var restoreCommand = new RestoreCommand(testAsset)
                .WithEnvironmentVariable("NUGET_PACKAGES", nugetPackagesFolder);
            restoreCommand.Execute().Should().Pass();


            var buildCommand = new BuildCommand(testAsset)
                .WithEnvironmentVariable("NUGET_PACKAGES", nugetPackagesFolder);

            buildCommand
                .Execute()
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("NETSDK1127");
        }

        [TestMethod]
        [DataRow("Major", "netcoreapp3.0", true)]
        [DataRow("Major", "netcoreapp2.0", true)]
        [DataRow("latestMinor", "netcoreapp3.0", true)]
        [DataRow("Invalid", "netcoreapp3.0", false)]
        public void RollForwardCanBeSpecifiedViaProperty(string rollForwardValue, string tfm, bool valid)
        {
            var testProject = new TestProject()
            {
                Name = "RollForwardSetting",
                TargetFrameworks = tfm,
                IsExe = true
            };

            testProject.AdditionalProperties["RollForward"] = rollForwardValue;

            var testAsset = TestAssetsManager.CreateTestProject(testProject, identifier: rollForwardValue + tfm);

            var buildCommand = new BuildCommand(testAsset);

            if (valid)
            {
                buildCommand
                    .Execute()
                    .Should()
                    .Pass();

                var outputDirectory = buildCommand.GetOutputDirectory(testProject.TargetFrameworks);

                string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
                JObject runtimeConfig = ReadRuntimeConfig(runtimeConfigFile);
                runtimeConfig["runtimeOptions"]["rollForward"].Value<string>()
                    .Should().Be(rollForwardValue);
            }
            else
            {
                buildCommand
                    .Execute()
                    .Should()
                    .Fail()
                    .And
                    .HaveStdOutContaining("NETSDK1104");
            }
        }

        [TestMethod]
        [DataRow("Major", true)]
        [DataRow("LatestMajor", true)]
        [DataRow("latestMAJOR", true)]
        [DataRow("Disable", false)]
        [DataRow("LatestPatch", false)]
        [DataRow("Minor", false)]
        [DataRow("LatestMinor", false)]
        [DataRow("LATESTminor", false)]
        public void RollForwardIsNotSupportedOn22(string rollForwardValue, bool valid)
        {
            var testProject = new TestProject()
            {
                Name = "RollForwardSettingNotSupported",
                TargetFrameworks = "netcoreapp2.2",
                IsExe = true
            };

            testProject.AdditionalProperties["RollForward"] = rollForwardValue;

            var testAsset = TestAssetsManager.CreateTestProject(testProject, identifier: rollForwardValue.GetHashCode().ToString());

            var buildCommand = new BuildCommand(testAsset);

            var result = buildCommand.Execute();

            if (valid)
            {
                result
                    .Should()
                    .Pass();
            }
            else
            {
                result
                    .Should()
                    .Fail()
                    .And
                    .HaveStdOutContaining("NETSDK1103");
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void BuildFailsIfRuntimePackIsNotAvailableForRuntimeIdentifier()
        {
            var testProject = new TestProject()
            {
                Name = "RuntimePackNotAvailable",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true,
                RuntimeIdentifier = "linux-x64",
                SelfContained = "true"
            };

            var testAsset = TestAssetsManager.CreateTestProject(testProject)
                .WithProjectChanges(project =>
                {
                    var ns = project.Root.Name.Namespace;

                    var itemGroup = new XElement(ns + "ItemGroup");
                    project.Root.Add(itemGroup);

                    var frameworkReference = new XElement(ns + "FrameworkReference",
                                               new XAttribute("Include", "Microsoft.WindowsDesktop.App"));
                    itemGroup.Add(frameworkReference);
                });

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                //  Pass "/clp:summary" so that we can check output for string "1 Error(s)"
                .Execute("/clp:summary")
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("NETSDK1082")
                .And
                .HaveStdOutContaining("1 Error(s)");
        }

        [TestMethod]
        public void BuildFailsIfInvalidRuntimeIdentifierIsSpecified()
        {
            var testProject = new TestProject()
            {
                Name = "RuntimePackNotAvailable",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true,
                RuntimeIdentifier = "invalid-rid",
                SelfContained = "true"
            };

            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            var restoreCommand = new RestoreCommand(testAsset);

            restoreCommand
                //  Pass "/clp:summary" so that we can check output for string "1 Error(s)"
                .Execute("/clp:summary")
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("NETSDK1083")
                .And
                .HaveStdOutContaining("1 Error(s)");
        }

        [TestMethod]
        public void BuildFailsIfRuntimePackHasNotBeenRestored()
        {
            var testProject = new TestProject()
            {
                Name = "RuntimePackNotRestored",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true,
            };

            //  Use a test-specific packages folder
            testProject.AdditionalProperties["RestorePackagesPath"] = @"$(MSBuildProjectDirectory)\packages";

            var runtimeIdentifier = EnvironmentInfo.GetCompatibleRid(testProject.TargetFrameworks);

            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            var restoreCommand = new RestoreCommand(testAsset);

            restoreCommand
                .Execute()
                .Should()
                .Pass();

            var buildCommand = new BuildCommand(testAsset);

            //  If we do the work in https://github.com/dotnet/cli/issues/10528,
            //  then we should add a new error message here indicating that the runtime pack hasn't
            //  been downloaded.
            string expectedErrorCode = "NETSDK1047";

            buildCommand
                .ExecuteWithoutRestore($"/p:RuntimeIdentifier={runtimeIdentifier}")
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining(expectedErrorCode);

        }

        [TestMethod]
        public void RuntimeFrameworkVersionCanBeSpecifiedOnFrameworkReference()
        {
            var testProject = new TestProject();

            string runtimeFrameworkVersion = "3.0.0-runtimeframeworkversion-attribute";
            string targetingPackVersion = "3.0.0-targetingpackversion";

            testProject.AdditionalProperties["RuntimeFrameworkVersion"] = "3.0.0-runtimeframeworkversion-property";
            testProject.SelfContained = "true";

            var resolvedVersions = GetResolvedVersions(testProject,
                project =>
                {
                    var ns = project.Root.Name.Namespace;

                    project.Root.Elements(ns + "ItemGroup")
                        .Elements(ns + "FrameworkReference")
                        .Single(fr => fr.Attribute("Include").Value.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
                        .SetAttributeValue("RuntimeFrameworkVersion", runtimeFrameworkVersion);
                });

            resolvedVersions.RuntimeFramework["Microsoft.NETCore.App"].Should().Be(runtimeFrameworkVersion);
            resolvedVersions.PackageDownload["Microsoft.NETCore.App.Ref"].Should().Be(targetingPackVersion);
            string runtimePackName = resolvedVersions.PackageDownload.Keys
                .Where(k => k.StartsWith("Microsoft.NETCore.App.Runtime."))
                .Single();
            resolvedVersions.PackageDownload[runtimePackName].Should().Be(runtimeFrameworkVersion);
            resolvedVersions.TargetingPack["Microsoft.NETCore.App"].Should().Be(targetingPackVersion);
            resolvedVersions.RuntimePack[runtimePackName].Should().Be(runtimeFrameworkVersion);
            resolvedVersions.AppHostPack["AppHost"].Should().Be("3.0.0-runtimeframeworkversion-property");
        }

        [TestMethod]
        public void RuntimeFrameworkVersionCanBeSpecifiedViaProperty()
        {
            var testProject = new TestProject();

            string runtimeFrameworkVersion = "3.0.0-runtimeframeworkversion-property";
            string targetingPackVersion = "3.0.0-targetingpackversion";

            testProject.AdditionalProperties["RuntimeFrameworkVersion"] = runtimeFrameworkVersion;
            testProject.SelfContained = "true";

            var resolvedVersions = GetResolvedVersions(testProject);

            resolvedVersions.RuntimeFramework["Microsoft.NETCore.App"].Should().Be(runtimeFrameworkVersion);
            resolvedVersions.PackageDownload["Microsoft.NETCore.App.Ref"].Should().Be(targetingPackVersion);
            string runtimePackName = resolvedVersions.PackageDownload.Keys
                .Where(k => k.StartsWith("Microsoft.NETCore.App.Runtime."))
                .Single();
            resolvedVersions.PackageDownload[runtimePackName].Should().Be(runtimeFrameworkVersion);
            resolvedVersions.TargetingPack["Microsoft.NETCore.App"].Should().Be(targetingPackVersion);
            resolvedVersions.RuntimePack[runtimePackName].Should().Be(runtimeFrameworkVersion);
            resolvedVersions.AppHostPack["AppHost"].Should().Be(runtimeFrameworkVersion);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void TargetLatestPatchCanBeSpecifiedOnFrameworkReference(bool attributeValue)
        {
            var testProject = new TestProject();

            string targetingPackVersion = "3.0.0-targetingpackversion";

            testProject.AdditionalProperties["TargetLatestRuntimePatch"] = (!attributeValue).ToString();
            testProject.SelfContained = "true";

            var resolvedVersions = GetResolvedVersions(testProject,
                project =>
                {
                    var ns = project.Root.Name.Namespace;

                    project.Root.Elements(ns + "ItemGroup")
                        .Elements(ns + "FrameworkReference")
                        .Single(fr => fr.Attribute("Include").Value.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
                        .SetAttributeValue("TargetLatestRuntimePatch", attributeValue.ToString());
                },
                identifier: attributeValue.ToString());

            string expectedRuntimeFrameworkVersion = attributeValue ? "3.0.0-latestversion" : "3.0.0-defaultversion";

            resolvedVersions.RuntimeFramework["Microsoft.NETCore.App"].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.PackageDownload["Microsoft.NETCore.App.Ref"].Should().Be(targetingPackVersion);
            string runtimePackName = resolvedVersions.PackageDownload.Keys
                .Where(k => k.StartsWith("Microsoft.NETCore.App.Runtime."))
                .Single();
            resolvedVersions.PackageDownload[runtimePackName].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.TargetingPack["Microsoft.NETCore.App"].Should().Be(targetingPackVersion);
            resolvedVersions.RuntimePack[runtimePackName].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.AppHostPack["AppHost"].Should().Be("3.0.0-apphostversion");
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void TargetLatestPatchCanBeSpecifiedViaProperty(bool propertyValue)
        {
            var testProject = new TestProject();

            string targetingPackVersion = "3.0.0-targetingpackversion";

            testProject.AdditionalProperties["TargetLatestRuntimePatch"] = propertyValue.ToString();
            testProject.SelfContained = "true";

            var resolvedVersions = GetResolvedVersions(testProject, identifier: propertyValue.ToString());

            string expectedRuntimeFrameworkVersion = propertyValue ? "3.0.0-latestversion" : "3.0.0-defaultversion";

            resolvedVersions.RuntimeFramework["Microsoft.NETCore.App"].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.PackageDownload["Microsoft.NETCore.App.Ref"].Should().Be(targetingPackVersion);
            string runtimePackName = resolvedVersions.PackageDownload.Keys
                .Where(k => k.StartsWith("Microsoft.NETCore.App.Runtime."))
                .Single();
            resolvedVersions.PackageDownload[runtimePackName].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.TargetingPack["Microsoft.NETCore.App"].Should().Be(targetingPackVersion);
            resolvedVersions.RuntimePack[runtimePackName].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.AppHostPack["AppHost"].Should().Be("3.0.0-apphostversion");
        }

        [TestMethod]
        public void TargetingPackVersionCanBeSpecifiedOnFrameworkReference()
        {
            var testProject = new TestProject();

            string targetingPackVersion = "3.0.0-tpversionfromframeworkreference";
            testProject.SelfContained = "true";

            var resolvedVersions = GetResolvedVersions(testProject,
                project =>
                {
                    var ns = project.Root.Name.Namespace;

                    project.Root.Elements(ns + "ItemGroup")
                        .Elements(ns + "FrameworkReference")
                        .Single(fr => fr.Attribute("Include").Value.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
                        .SetAttributeValue("TargetingPackVersion", targetingPackVersion);
                });

            string expectedRuntimeFrameworkVersion = "3.0.0-latestversion";

            resolvedVersions.RuntimeFramework["Microsoft.NETCore.App"].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.PackageDownload["Microsoft.NETCore.App.Ref"].Should().Be(targetingPackVersion);
            string runtimePackName = resolvedVersions.PackageDownload.Keys
                .Where(k => k.StartsWith("Microsoft.NETCore.App.Runtime."))
                .Single();
            resolvedVersions.PackageDownload[runtimePackName].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.TargetingPack["Microsoft.NETCore.App"].Should().Be(targetingPackVersion);
            resolvedVersions.RuntimePack[runtimePackName].Should().Be(expectedRuntimeFrameworkVersion);
            resolvedVersions.AppHostPack["AppHost"].Should().Be("3.0.0-apphostversion");
        }

        [TestMethod]
        public void TransitiveFrameworkReferenceFromProjectReference()
        {
            var testProject = new TestProject()
            {
                Name = "TransitiveFrameworkReference",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true
            };

            var referencedProject = new TestProject()
            {
                Name = "ReferencedProject",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
            };

            referencedProject.FrameworkReferences.Add("Microsoft.ASPNETCORE.App");

            testProject.ReferencedProjects.Add(referencedProject);

            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            var outputDirectory = buildCommand.GetOutputDirectory(testProject.TargetFrameworks);

            string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
            var runtimeFrameworkNames = GetRuntimeFrameworks(runtimeConfigFile);

            //  When we remove the workaround for https://github.com/dotnet/core-setup/issues/4947 in GenerateRuntimeConfigurationFiles,
            //  Microsoft.NETCore.App will need to be added to this list
            runtimeFrameworkNames.Should().BeEquivalentTo("Microsoft.AspNetCore.App", "Microsoft.NETCore.App");
        }

        [TestMethod]
        public void TransitiveFrameworkReferenceFromPackageReference()
        {
            var referencedPackage = new TestProject()
            {
                Name = "ReferencedPackage",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
            };
            referencedPackage.FrameworkReferences.Add("Microsoft.ASPNETCORE.App");

            var packageAsset = TestAssetsManager.CreateTestProject(referencedPackage);

            var packCommand = new PackCommand(packageAsset);

            packCommand.Execute()
                .Should()
                .Pass();

            var nupkgFolder = packCommand.GetPackageDirectory();

            var testProject = new TestProject()
            {
                Name = "TransitiveFrameworkReference",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true
            };

            testProject.PackageReferences.Add(new TestPackageReference(referencedPackage.Name, "1.0.0", nupkgFolder.FullName));
            testProject.AdditionalProperties.Add("RestoreAdditionalProjectSources",
                                     "$(RestoreAdditionalProjectSources);" + nupkgFolder);


            var testAsset = TestAssetsManager.CreateTestProject(testProject);
            string nugetPackagesFolder = Path.Combine(testAsset.TestRoot, "packages");

            var buildCommand = (BuildCommand)new BuildCommand(testAsset)
                .WithEnvironmentVariable("NUGET_PACKAGES", nugetPackagesFolder);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            var outputDirectory = buildCommand.GetOutputDirectory(testProject.TargetFrameworks);

            string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
            var runtimeFrameworkNames = GetRuntimeFrameworks(runtimeConfigFile);

            //  When we remove the workaround for https://github.com/dotnet/core-setup/issues/4947 in GenerateRuntimeConfigurationFiles,
            //  Microsoft.NETCore.App will need to be added to this list
            runtimeFrameworkNames.Should().BeEquivalentTo("Microsoft.NETCore.App", "Microsoft.AspNetCore.App");
        }

        [TestMethod]
        public void IsTrimmableDefaultsComeFromKnownFrameworkReference()
        {
            var testProject = new TestProject();

            var runtimeAssetTrimInfo = GetRuntimeAssetTrimInfo(testProject);

            string runtimePackName = runtimeAssetTrimInfo.Keys
                .Where(k => k.StartsWith("Microsoft.NETCore.App.Runtime."))
                .Single();

            foreach (var runtimeAsset in runtimeAssetTrimInfo[runtimePackName])
            {
                runtimeAsset.isTrimmable.Should().Be("");
            }
        }

        [TestMethod]
        public void IsTrimmableCanBeSpecifiedOnFrameworkReference()
        {
            var testProject = new TestProject();

            var runtimeAssetTrimInfo = GetRuntimeAssetTrimInfo(testProject,
                project =>
                {
                    var ns = project.Root.Name.Namespace;

                    var itemGroup = new XElement(ns + "ItemGroup");
                    project.Root.Add(itemGroup);

                    itemGroup.Add(new XElement(ns + "FrameworkReference",
                                               new XAttribute("Include", "Microsoft.NETCore.App"),
                                               new XAttribute("IsTrimmable", "false")));
                });

            string runtimePackName = runtimeAssetTrimInfo.Keys
                .Where(k => k.StartsWith("Microsoft.NETCore.App.Runtime."))
                .Single();

            foreach (var runtimeAsset in runtimeAssetTrimInfo[runtimePackName])
            {
                runtimeAsset.isTrimmable.Should().Be("false");
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void ResolvedFrameworkReferences_are_generated()
        {
            var testProject = new TestProject()
            {
                Name = "ResolvedFrameworkReferenceTest",
                IsExe = true,
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                RuntimeIdentifier = EnvironmentInfo.GetCompatibleRid(),
                SelfContained = "true"
            };

            testProject.FrameworkReferences.Add("Microsoft.AspNetCore.App");
            testProject.FrameworkReferences.Add("Microsoft.WindowsDesktop.App");

            var testAsset = TestAssetsManager.CreateTestProject(testProject);

            var projectFolder = Path.Combine(testAsset.TestRoot, testProject.Name);

            var buildCommand = new BuildCommand(testAsset);

            var expectedMetadata = new[]
            {
                "OriginalItemSpec",
                "IsImplicitlyDefined",
                "TargetingPackName",
                "TargetingPackVersion",
                "TargetingPackPath",
                "RuntimePackName",
                "RuntimePackVersion",
                "RuntimePackPath"
            };

            var getValuesCommand = new GetValuesCommand(Log, projectFolder, testProject.TargetFrameworks,
                "ResolvedFrameworkReference", GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveFrameworkReferences"
            };
            getValuesCommand.MetadataNames.AddRange(expectedMetadata);

            getValuesCommand.Execute().Should().Pass();

            var resolvedFrameworkReferences = getValuesCommand.GetValuesWithMetadata();

            resolvedFrameworkReferences.Select(rfr => rfr.value)
                .Should()
                .BeEquivalentTo(
                    "Microsoft.NETCore.App",
                    "Microsoft.AspNetCore.App",
                    "Microsoft.WindowsDesktop.App");

            foreach (var resolvedFrameworkReference in resolvedFrameworkReferences)
            {
                foreach (var expectedMetadataName in expectedMetadata)
                {
                    if (expectedMetadataName == "IsImplicitlyDefined" &&
                        resolvedFrameworkReference.value != "Microsoft.NETCore.App")
                    {
                        continue;
                    }

                    resolvedFrameworkReference.metadata[expectedMetadataName]
                        .Should()
                        .NotBeNullOrEmpty(because:
                            $"ResolvedFrameworkReference for {resolvedFrameworkReference.value} should have " +
                            $"{expectedMetadataName} metadata");
                }
            }

        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow(true)]
        [DataRow(false)]
        public void WindowsFormsFrameworkReference(bool selfContained)
        {
            TestFrameworkReferenceProfiles(
                frameworkReferences: new[] { "Microsoft.WindowsDesktop.App.WindowsForms" },
                expectedReferenceNames: new[] { "Microsoft.Win32.Registry", "System.Windows.Forms" },
                notExpectedReferenceNames: Enumerable.Empty<string>(),
                selfContained);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow(true)]
        [DataRow(false)]
        public void WPFFrameworkReference(bool selfContained)
        {
            TestFrameworkReferenceProfiles(
                frameworkReferences: new[] { "Microsoft.WindowsDesktop.App.WPF" },
                expectedReferenceNames: new[] { "Microsoft.Win32.Registry", "System.Windows.Presentation" },
                notExpectedReferenceNames: Enumerable.Empty<string>(),
                selfContained);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow(true)]
        [DataRow(false)]
        public void WindowsFormAndWPFFrameworkReference(bool selfContained)
        {
            TestFrameworkReferenceProfiles(
                frameworkReferences: new[] { "Microsoft.WindowsDesktop.App.WindowsForms", "Microsoft.WindowsDesktop.App.WPF" },
                expectedReferenceNames: new[] { "Microsoft.Win32.Registry", "System.Windows.Forms", "System.Windows.Presentation" },
                notExpectedReferenceNames: Enumerable.Empty<string>(),
                selfContained);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow(true)]
        [DataRow(false)]
        public void WindowsDesktopFrameworkReference(bool selfContained)
        {
            TestFrameworkReferenceProfiles(
                frameworkReferences: new[] { "Microsoft.WindowsDesktop.App" },
                expectedReferenceNames: new[] { "Microsoft.Win32.Registry", "System.Windows.Forms",
                                                "System.Windows.Presentation", "WindowsFormsIntegration" },
                notExpectedReferenceNames: Enumerable.Empty<string>(),
                selfContained);
        }

        [TestMethod]
        [CoreMSBuildOnly]
        public void TransitiveFrameworkReferencesAreNotIncludedInRestore()
        {
            var testProject = new TestProject()
            {
                Name = "TransitiveFrameworkRef",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsSdkProject = true
            };
            testProject.PackageReferences.Add(new TestPackageReference("Microsoft.AspNetCore.Authentication.JwtBearer", "5.0.0"));
            var testAsset = TestAssetsManager.CreateTestProject(testProject).WithProjectChanges((project) =>
            {
                var ns = project.Root.Name.Namespace;
                var target = XElement.Parse(@"  <Target Name=""GetFrameworkRefResults"" AfterTargets=""Build"" DependsOnTargets=""CollectFrameworkReferences"" >
    <Message Text=""Framework References: @(_FrameworkReferenceForRestore)"" Importance=""High"" />
  </Target>");
                project.Root.Add(target);
            });

            var buildCommand = new BuildCommand(testAsset);
            buildCommand.Execute()
                .Should()
                .Pass()
                .And
                .HaveStdOutContaining("Microsoft.NETCore.App")
                .And
                .NotHaveStdOutContaining("Microsoft.AspNetCore.App");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DirectRestoreCapturesFrameworkReferencesOnlyWhenRequested(bool captureGraphInputs)
        {
            var project = CreateFrameworkManifestTestProject();
            var asset = TestAssetsManager.CreateTestProject(project, identifier: captureGraphInputs.ToString());
            string projectFolder = Path.Combine(asset.TestRoot, project.Name);
            string manifestPath = GetFrameworkReferenceFetchManifestPath(projectFolder, project.Name, captureGraphInputs);

            new MSBuildCommand(asset, "Restore")
                .ExecuteWithoutRestore($"-p:MSBuildRestoreGraphInputs={captureGraphInputs}")
                .Should().Pass();

            File.Exists(manifestPath).Should().Be(captureGraphInputs);
        }

        [TestMethod]
        public void RestoreGraphInputsRefreshesFrameworkSnapshotWithoutImportingIt()
        {
            var project = CreateFrameworkManifestTestProject();
            var asset = TestAssetsManager.CreateTestProject(project);
            string projectFolder = Path.Combine(asset.TestRoot, project.Name);
            string manifestPath = GetFrameworkReferenceFetchManifestPath(projectFolder, project.Name, captureGraphInputs: true);

            new MSBuildCommand(asset, "Restore")
                .ExecuteWithoutRestore("-p:MSBuildRestoreGraphInputs=true")
                .Should().Pass();

            string manifestContents = File.ReadAllText(manifestPath);
            DateTime timestamp = DateTime.UtcNow.AddDays(-1);
            File.SetLastWriteTimeUtc(manifestPath, timestamp);
            timestamp = File.GetLastWriteTimeUtc(manifestPath);

            new MSBuildCommand(asset, "Restore")
                .ExecuteWithoutRestore("-p:MSBuildRestoreGraphInputs=true")
                .Should().Pass();
            File.GetLastWriteTimeUtc(manifestPath).Should().Be(timestamp);

            File.WriteAllText(manifestPath, "not an MSBuild project");
            new MSBuildCommand(asset, "Restore")
                .ExecuteWithoutRestore("-p:MSBuildRestoreGraphInputs=true", "-p:EnableFrameworkReferenceFetchManifest=true")
                .Should().Pass();
            File.ReadAllText(manifestPath).Should().Be(manifestContents);

            File.Delete(Path.Combine(projectFolder, "obj", "project.assets.json"));
            var references = new GetValuesCommand(
                Log, projectFolder, project.TargetFrameworks, "ReferencePath", GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveReferences",
                MetadataNames = { "ReferenceSourceTarget" },
            };

            references.ExecuteWithoutRestore("-p:EnableFrameworkReferenceFetchManifest=true", $"-p:FrameworkReferenceFetchManifestPath={manifestPath}")
                .Should().Pass();
            references.GetValuesWithMetadata().Should().NotBeEmpty()
                .And.OnlyContain(reference => reference.metadata["ReferenceSourceTarget"] == "FrameworkReferenceManifest");
        }

        [TestMethod]
        public void FrameworkReferenceManifestCandidatesCanResolveCompileReferencesAndAppHostWithoutRestore()
        {
            var baselineProject = CreateFrameworkManifestTestProject();
            var baselineAsset = TestAssetsManager.CreateTestProject(baselineProject, identifier: "baseline");
            var baselineBuild = new BuildCommand(baselineAsset);

            baselineBuild.Execute().Should().Pass();

            string baselineProjectFolder = Path.Combine(baselineAsset.TestRoot, baselineProject.Name);
            var baselineReferencesCommand = new GetValuesCommand(
                Log,
                baselineProjectFolder,
                baselineProject.TargetFrameworks,
                "ReferencePath",
                GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveReferences",
                MetadataNames = { "ReferenceAssembly" },
            };

            baselineReferencesCommand.ExecuteWithoutRestore().Should().Pass();
            List<(string value, Dictionary<string, string> metadata)> baselineReferences =
                baselineReferencesCommand.GetValuesWithMetadata();

            var runtimeFrameworkCommand = new GetValuesCommand(
                Log,
                baselineProjectFolder,
                baselineProject.TargetFrameworks,
                "RuntimeFramework",
                GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveFrameworkReferences",
                MetadataNames = { "FrameworkName", "Version", "Profile" },
            };

            runtimeFrameworkCommand.ExecuteWithoutRestore().Should().Pass();
            List<(string value, Dictionary<string, string> metadata)> runtimeFrameworks =
                runtimeFrameworkCommand.GetValuesWithMetadata();

            var resolvedFrameworkReferenceCommand = new GetValuesCommand(
                Log,
                baselineProjectFolder,
                baselineProject.TargetFrameworks,
                "ResolvedFrameworkReference",
                GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveFrameworkReferences",
                MetadataNames = { "IsImplicitlyDefined", "TargetingPackName", "TargetingPackVersion", "TargetingPackPath", "OriginalItemSpec", "Profile" },
            };

            resolvedFrameworkReferenceCommand.ExecuteWithoutRestore().Should().Pass();
            List<(string value, Dictionary<string, string> metadata)> resolvedFrameworkReferences =
                resolvedFrameworkReferenceCommand.GetValuesWithMetadata();

            var appHostSourcePathCommand = new GetValuesCommand(
                Log,
                baselineProjectFolder,
                baselineProject.TargetFrameworks,
                "AppHostSourcePath")
            {
                DependsOnTargets = "_GetAppHostPaths",
            };

            appHostSourcePathCommand.ExecuteWithoutRestore().Should().Pass();
            string appHostSourcePath = appHostSourcePathCommand.GetValues().Single();

            var appHostRuntimeIdentifierCommand = new GetValuesCommand(
                Log,
                baselineProjectFolder,
                baselineProject.TargetFrameworks,
                "AppHostRuntimeIdentifier")
            {
                DependsOnTargets = "_GetAppHostPaths",
            };

            appHostRuntimeIdentifierCommand.ExecuteWithoutRestore().Should().Pass();
            string appHostRuntimeIdentifier = appHostRuntimeIdentifierCommand.GetValues().Single();

            var manifestProject = CreateFrameworkManifestTestProject();
            manifestProject.AdditionalProperties["EnableFrameworkReferenceFetchManifest"] = "true";

            var manifestAsset = TestAssetsManager.CreateTestProject(manifestProject, identifier: "manifest");
            string manifestProjectFolder = Path.Combine(manifestAsset.TestRoot, manifestProject.Name);
            new RestoreCommand(manifestAsset).Execute().Should().Pass();

            string manifestPath = GetFrameworkReferenceFetchManifestPath(manifestProjectFolder, manifestProject.Name);
            File.Exists(manifestPath).Should().BeTrue();

            string projectAssetsFile = Path.Combine(manifestProjectFolder, "obj", "project.assets.json");
            File.Delete(projectAssetsFile);

            var manifestBuild = new BuildCommand(manifestAsset);
            manifestBuild.ExecuteWithoutRestore().Should().Pass();

            File.Exists(projectAssetsFile).Should().BeFalse();

            var manifestReferencesCommand = new GetValuesCommand(
                Log,
                manifestProjectFolder,
                manifestProject.TargetFrameworks,
                "ReferencePath",
                GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveReferences",
                MetadataNames = { "ReferenceSourceTarget" },
            };

            manifestReferencesCommand.ExecuteWithoutRestore().Should().Pass();
            List<(string value, Dictionary<string, string> metadata)> manifestReferences =
                manifestReferencesCommand.GetValuesWithMetadata();

            manifestReferences.Select(reference => reference.value)
                .Should().Equal(baselineReferences.Select(reference => reference.value));
            manifestReferences.Should().OnlyContain(
                reference => reference.metadata["ReferenceSourceTarget"] == "FrameworkReferenceManifest");

            var manifestRuntimeFrameworkCommand = new GetValuesCommand(
                Log,
                manifestProjectFolder,
                manifestProject.TargetFrameworks,
                "RuntimeFramework",
                GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveFrameworkReferences",
                MetadataNames = { "FrameworkName", "Version", "Profile" },
            };

            manifestRuntimeFrameworkCommand.ExecuteWithoutRestore().Should().Pass();
            manifestRuntimeFrameworkCommand.GetValuesWithMetadata()
                .Should().BeEquivalentTo(runtimeFrameworks, options => options.WithStrictOrdering());

            var manifestResolvedFrameworkReferenceCommand = new GetValuesCommand(
                Log,
                manifestProjectFolder,
                manifestProject.TargetFrameworks,
                "ResolvedFrameworkReference",
                GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ResolveFrameworkReferences",
                MetadataNames = { "IsImplicitlyDefined", "TargetingPackName", "TargetingPackVersion", "TargetingPackPath", "OriginalItemSpec", "Profile" },
            };

            manifestResolvedFrameworkReferenceCommand.ExecuteWithoutRestore().Should().Pass();
            manifestResolvedFrameworkReferenceCommand.GetValuesWithMetadata()
                .Should().BeEquivalentTo(resolvedFrameworkReferences, options => options.WithStrictOrdering());

            var manifestAppHostSourcePathCommand = new GetValuesCommand(
                Log,
                manifestProjectFolder,
                manifestProject.TargetFrameworks,
                "AppHostSourcePath")
            {
                DependsOnTargets = "_GetAppHostPaths",
            };

            manifestAppHostSourcePathCommand.ExecuteWithoutRestore().Should().Pass();
            manifestAppHostSourcePathCommand.GetValues().Single().Should().Be(appHostSourcePath);

            var manifestAppHostRuntimeIdentifierCommand = new GetValuesCommand(
                Log,
                manifestProjectFolder,
                manifestProject.TargetFrameworks,
                "AppHostRuntimeIdentifier")
            {
                DependsOnTargets = "_GetAppHostPaths",
            };

            manifestAppHostRuntimeIdentifierCommand.ExecuteWithoutRestore().Should().Pass();
            manifestAppHostRuntimeIdentifierCommand.GetValues().Single().Should().Be(appHostRuntimeIdentifier);

            DirectoryInfo outputDirectory = manifestBuild.GetOutputDirectory(manifestProject.TargetFrameworks);
            outputDirectory.Should().HaveFile(manifestProject.Name + ".dll");
            outputDirectory.Should().HaveFile(manifestProject.Name + EnvironmentInfo.ExecutableExtension);
        }

        [TestMethod]
        [DataRow("PackageReference", "Unsupported.Package")]
        [DataRow("ProjectReference", "missing-project.csproj")]
        [DataRow("Reference", "Unsupported.Assembly")]
        [DataRow("RuntimeIdentifier", "unsupported-rid")]
        [DataRow("RuntimeIdentifiers", "unsupported-rid-1;unsupported-rid-2")]
        [DataRow("TargetFrameworks", "multiple")]
        public void FrameworkReferenceFetchManifestRejectsUnsupportedInputsAndRemovesStaleManifest(
            string inputName,
            string inputValue)
        {
            if (inputName == "TargetFrameworks")
            {
                inputValue = $"net10.0;{ToolsetInfo.CurrentTargetFramework}";
            }

            var project = CreateFrameworkManifestTestProject();
            project.AdditionalProperties["EnableFrameworkReferenceFetchManifest"] = "true";

            var testAsset = TestAssetsManager.CreateTestProject(project, identifier: inputName);
            string projectFolder = Path.Combine(testAsset.TestRoot, project.Name);
            string manifestPath = GetFrameworkReferenceFetchManifestPath(projectFolder, project.Name);

            new RestoreCommand(testAsset).Execute().Should().Pass();
            File.Exists(manifestPath).Should().BeTrue();

            AddUnsupportedFrameworkReferenceFetchManifestInput(
                Path.Combine(projectFolder, $"{project.Name}.csproj"),
                inputName,
                inputValue);

            new BuildCommand(testAsset)
                .ExecuteWithoutRestore()
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("NETSDK1247")
                .And
                .HaveStdOutContaining($"MSBuild input '{inputName}'")
                .And
                .HaveStdOutContaining(inputValue);

            File.Exists(manifestPath).Should().BeTrue();

            new RestoreCommand(testAsset)
                .Execute()
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("NETSDK1247")
                .And
                .HaveStdOutContaining($"MSBuild input '{inputName}'")
                .And
                .HaveStdOutContaining(inputValue);

            File.Exists(manifestPath).Should().BeFalse();
        }

        [TestMethod]
        public void FrameworkReferenceFetchManifestReportsAllUnsupportedInputsBeforeStopping()
        {
            var project = CreateFrameworkManifestTestProject();
            project.AdditionalProperties["EnableFrameworkReferenceFetchManifest"] = "true";

            var testAsset = TestAssetsManager.CreateTestProject(project, identifier: "all-unsupported-inputs");
            string projectFolder = Path.Combine(testAsset.TestRoot, project.Name);
            string projectPath = Path.Combine(projectFolder, $"{project.Name}.csproj");
            string manifestPath = GetFrameworkReferenceFetchManifestPath(projectFolder, project.Name);

            new RestoreCommand(testAsset).Execute().Should().Pass();
            File.Exists(manifestPath).Should().BeTrue();

            AddUnsupportedFrameworkReferenceFetchManifestInput(projectPath, "PackageReference", "Unsupported.Package");
            AddUnsupportedFrameworkReferenceFetchManifestInput(projectPath, "ProjectReference", "missing-project.csproj");
            AddUnsupportedFrameworkReferenceFetchManifestInput(projectPath, "Reference", "Unsupported.Assembly");
            AddUnsupportedFrameworkReferenceFetchManifestInput(projectPath, "RuntimeIdentifier", "unsupported-rid");
            AddUnsupportedFrameworkReferenceFetchManifestInput(projectPath, "RuntimeIdentifiers", "unsupported-rid-1;unsupported-rid-2");

            new RestoreCommand(testAsset)
                .Execute()
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("Unsupported.Package")
                .And
                .HaveStdOutContaining("missing-project.csproj")
                .And
                .HaveStdOutContaining("Unsupported.Assembly")
                .And
                .HaveStdOutContaining("unsupported-rid")
                .And
                .HaveStdOutContaining("unsupported-rid-1;unsupported-rid-2")
                .And
                .HaveStdOutContaining("NETSDK1248");

            File.Exists(manifestPath).Should().BeFalse();
        }

        private static TestProject CreateFrameworkManifestTestProject()
        {
            var project = new TestProject
            {
                Name = "FrameworkReferenceManifest",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true,
            };

            project.AdditionalProperties["GenerateDependencyFile"] = "false";
            project.AdditionalProperties["GenerateRuntimeConfigurationFiles"] = "false";
            return project;
        }

        private static string GetFrameworkReferenceFetchManifestPath(string projectFolder, string projectName, bool captureGraphInputs = false)
            => captureGraphInputs
                ? Path.Combine(projectFolder, "obj", "frameworkreferences", "Debug", "AnyCPU", ToolsetInfo.CurrentTargetFramework, $"{projectName}.csproj.g.props")
                : Path.Combine(projectFolder, "obj", "frameworkreferences", $"{projectName}.csproj.g.props");

        private static void AddUnsupportedFrameworkReferenceFetchManifestInput(
            string projectPath,
            string inputName,
            string inputValue)
        {
            XDocument project = XDocument.Load(projectPath);
            XNamespace ns = project.Root.Name.Namespace;

            if (inputName is "RuntimeIdentifier" or "RuntimeIdentifiers")
            {
                project.Root.Element(ns + "PropertyGroup").Add(new XElement(ns + inputName, inputValue));
            }
            else if (inputName == "TargetFrameworks")
            {
                XElement targetFramework = project.Root.Descendants(ns + "TargetFramework").Single();
                targetFramework.Name = ns + "TargetFrameworks";
                targetFramework.Value = inputValue;
            }
            else
            {
                if (inputName == "FrameworkReference" && inputValue == "Microsoft.NETCore.App")
                {
                    project.Root.Element(ns + "PropertyGroup").Add(
                        new XElement(ns + "DisableImplicitFrameworkReferences", "true"));
                }

                project.Root.Add(
                    new XElement(
                        ns + "ItemGroup",
                        new XElement(ns + inputName, new XAttribute("Include", inputValue))));
            }

            project.Save(projectPath);
        }

        private void TestFrameworkReferenceProfiles(
            IEnumerable<string> frameworkReferences,
            IEnumerable<string> expectedReferenceNames,
            IEnumerable<string> notExpectedReferenceNames,
            bool selfContained,
            [CallerMemberName] string callingMethod = "")
        {
            var testProject = new TestProject()
            {
                Name = "WindowsFormsFrameworkReference",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true
            };
            testProject.FrameworkReferences.AddRange(frameworkReferences);

            if (selfContained)
            {
                testProject.RuntimeIdentifier = EnvironmentInfo.GetCompatibleRid(testProject.TargetFrameworks);
                testProject.SelfContained = "true";
            }

            string identifier = selfContained ? "_selfcontained" : string.Empty;

            var testAsset = TestAssetsManager.CreateTestProject(testProject, callingMethod, identifier);

            string projectFolder = Path.Combine(testAsset.TestRoot, testProject.Name);

            var buildCommand = new BuildCommand(testAsset);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            var getValuesCommand = new GetValuesCommand(Log, projectFolder, testProject.TargetFrameworks, "Reference", GetValuesCommand.ValueType.Item);

            getValuesCommand.Execute().Should().Pass();

            var references = getValuesCommand.GetValues();
            var referenceNames = references.Select(Path.GetFileNameWithoutExtension);

            referenceNames.Should().Contain(expectedReferenceNames);

            if (notExpectedReferenceNames.Any())
            {
                referenceNames.Should().NotContain(notExpectedReferenceNames);
            }

            if (selfContained)
            {
                var outputDirectory = buildCommand.GetOutputDirectory(testProject.TargetFrameworks, runtimeIdentifier: testProject.RuntimeIdentifier);

                //  The output directory should have the DLLs which are not referenced at compile time but are
                //  still part of the shared framework.
                outputDirectory.Should().HaveFiles(expectedReferenceNames.Concat(notExpectedReferenceNames)
                    .Select(n => n + ".dll"));
            }
        }

        private JObject ReadRuntimeConfig(string runtimeConfigPath)
        {
            string runtimeConfigContents = File.ReadAllText(runtimeConfigPath);
            return JObject.Parse(runtimeConfigContents);
        }

        private List<string> GetRuntimeFrameworks(string runtimeConfigPath)
        {
            JObject runtimeConfig = ReadRuntimeConfig(runtimeConfigPath);

            var runtimeFrameworksList = (JArray)runtimeConfig["runtimeOptions"]["frameworks"];
            if (runtimeFrameworksList == null)
            {
                var runtimeFrameworkName = runtimeConfig["runtimeOptions"]["framework"]["name"].Value<string>();
                return new List<string>() { runtimeFrameworkName };
            }
            else
            {
                var runtimeFrameworkNames = runtimeFrameworksList.Select(element => ((JValue)element["name"]).Value<string>())
                    .ToList();

                return runtimeFrameworkNames;
            }
        }

        private List<string> GetIncludedFrameworks(string runtimeConfigPath)
        {
            JObject runtimeConfig = ReadRuntimeConfig(runtimeConfigPath);

            var runtimeFrameworksList = (JArray)runtimeConfig["runtimeOptions"]["includedFrameworks"];
            return runtimeFrameworksList == null
                ? new List<string>()
                : runtimeFrameworksList.Select(element => ((JValue)element["name"]).Value<string>()).ToList();
        }

        private ResolvedVersionInfo GetResolvedVersions(TestProject testProject,
            Action<XDocument> projectChanges = null,
            [CallerMemberName] string callingMethod = null,
            string identifier = null)
        {
            testProject.Name = "ResolvedVersionsTest";
            testProject.TargetFrameworks = ToolsetInfo.CurrentTargetFramework;
            testProject.IsExe = true;
            testProject.AdditionalProperties["DisableImplicitFrameworkReferences"] = "true";
            testProject.RuntimeIdentifier = EnvironmentInfo.GetCompatibleRid(testProject.TargetFrameworks);

            var testAsset = TestAssetsManager.CreateTestProject(testProject, callingMethod, identifier)
                .WithProjectChanges(project =>
                {
                    var ns = project.Root.Name.Namespace;

                    var itemGroup = new XElement(ns + "ItemGroup");
                    project.Root.Add(itemGroup);

                    var frameworkReference = new XElement(ns + "FrameworkReference",
                                               new XAttribute("Include", "Microsoft.NETCore.APP"));
                    itemGroup.Add(frameworkReference);

                    var knownFrameworkReferenceUpdate = new XElement(ns + "KnownFrameworkReference",
                                                                     new XAttribute("Update", "Microsoft.NETCore.App"),
                                                                     new XAttribute("DefaultRuntimeFrameworkVersion", "3.0.0-defaultversion"),
                                                                     new XAttribute("LatestRuntimeFrameworkVersion", "3.0.0-latestversion"),
                                                                     new XAttribute("TargetingPackVersion", "3.0.0-targetingpackversion"));
                    itemGroup.Add(knownFrameworkReferenceUpdate);

                    var knownAppHostPackUpdate = new XElement(ns + "KnownAppHostPack",
                                                            new XAttribute("Update", "Microsoft.NETCore.App"),
                                                            new XAttribute("AppHostPackVersion", "3.0.0-apphostversion"));

                    itemGroup.Add(knownAppHostPackUpdate);

                    string writeResolvedVersionsTarget = @"
<Target Name=`WriteResolvedVersions` DependsOnTargets=`PrepareForBuild;ProcessFrameworkReferences`>
    <ItemGroup>
      <LinesToWrite Include=`RuntimeFramework%09%(RuntimeFramework.Identity)%09%(RuntimeFramework.Version)`/>
      <LinesToWrite Include=`PackageDownload%09%(PackageDownload.Identity)%09%(PackageDownload.Version)`/>
      <LinesToWrite Include=`TargetingPack%09%(TargetingPack.Identity)%09%(TargetingPack.NuGetPackageVersion)`/>
      <LinesToWrite Include=`RuntimePack%09%(RuntimePack.Identity)%09%(RuntimePack.NuGetPackageVersion)`/>
      <LinesToWrite Include=`AppHostPack%09%(AppHostPack.Identity)%09%(AppHostPack.NuGetPackageVersion)`/>
    </ItemGroup>
    <WriteLinesToFile File=`$(OutputPath)resolvedversions.txt`
                      Lines=`@(LinesToWrite)`
                      Overwrite=`true`
                      Encoding=`Unicode`/>

  </Target>";
                    writeResolvedVersionsTarget = writeResolvedVersionsTarget.Replace('`', '"');

                    project.Root.Add(XElement.Parse(writeResolvedVersionsTarget));
                });

            if (projectChanges != null)
            {
                testAsset = testAsset.WithProjectChanges(projectChanges);
            }

            var command = new MSBuildCommand(testAsset, "WriteResolvedVersions");

            command.ExecuteWithoutRestore()
                .Should()
                .Pass();

            var outputDirectory = command.GetOutputDirectory(testProject.TargetFrameworks, runtimeIdentifier: testProject.RuntimeIdentifier);
            var resolvedVersions = ResolvedVersionInfo.ParseFrom(Path.Combine(outputDirectory.FullName, "resolvedversions.txt"));

            return resolvedVersions;
        }

        private Dictionary<string, List<(string asset, string isTrimmable)>> GetRuntimeAssetTrimInfo(TestProject testProject,
            Action<XDocument> projectChanges = null,
            [CallerMemberName] string callingMethod = null,
            string identifier = null)
        {
            string targetFramework = ToolsetInfo.CurrentTargetFramework;

            testProject.Name = "TrimInfoTest";
            testProject.TargetFrameworks = targetFramework; ;
            testProject.IsExe = true;
            testProject.RuntimeIdentifier = EnvironmentInfo.GetCompatibleRid(testProject.TargetFrameworks);
            testProject.SelfContained = "true";

            var testAsset = TestAssetsManager.CreateTestProject(testProject, callingMethod, identifier);
            if (projectChanges != null)
            {
                testAsset = testAsset.WithProjectChanges(projectChanges);
            }

            var command = new GetValuesCommand(Log, Path.Combine(testAsset.Path, testProject.Name), targetFramework,
                                                        "ResolvedFileToPublish", GetValuesCommand.ValueType.Item)
            {
                DependsOnTargets = "ComputeFilesToPublish",
                MetadataNames = { "NuGetPackageId", "IsTrimmable" },
            };

            command.Execute().Should().Pass();
            var items = from item in command.GetValuesWithMetadata()
                        select new
                        {
                            Identity = item.value,
                            PackageName = item.metadata["NuGetPackageId"],
                            IsTrimmable = item.metadata["IsTrimmable"]
                        };

            var trimInfo = new Dictionary<string, List<(string asset, string isTrimmable)>>();
            foreach (var item in items)
            {
                List<(string asset, string isTrimmable)> assets;
                if (!trimInfo.TryGetValue(item.PackageName, out assets))
                {
                    assets = trimInfo[item.PackageName] = new List<(string asset, string isTrimmable)>(3);
                }
                assets.Add((item.Identity, item.IsTrimmable));
            }

            return trimInfo;
        }

        private class ResolvedVersionInfo
        {
            public Dictionary<string, string> RuntimeFramework { get; } = new Dictionary<string, string>();
            public Dictionary<string, string> PackageDownload { get; } = new Dictionary<string, string>();
            public Dictionary<string, string> TargetingPack { get; } = new Dictionary<string, string>();
            public Dictionary<string, string> RuntimePack { get; } = new Dictionary<string, string>();
            public Dictionary<string, string> AppHostPack { get; } = new Dictionary<string, string>();

            public static ResolvedVersionInfo ParseFrom(string path)
            {
                var versionInfo = new ResolvedVersionInfo();
                foreach (var line in File.ReadAllLines(path))
                {
                    var fields = line.Split('\t');
                    if (fields.Length >= 3)
                    {
                        string itemType = fields[0];
                        string itemIdentity = fields[1];
                        string version = fields[2];
                        Dictionary<string, string> dict;
                        switch (itemType)
                        {
                            case "RuntimeFramework":
                                dict = versionInfo.RuntimeFramework;
                                break;
                            case "PackageDownload":
                                //  PackageDownload versions are enclosed in [brackets]
                                dict = versionInfo.PackageDownload;
                                version = version.Substring(1, version.Length - 2);
                                break;
                            case "TargetingPack":
                                dict = versionInfo.TargetingPack;
                                break;
                            case "RuntimePack":
                                dict = versionInfo.RuntimePack;
                                break;
                            case "AppHostPack":
                                dict = versionInfo.AppHostPack;
                                break;
                            default:
                                throw new InvalidOperationException("Unexpected item type: " + itemType);
                        }
                        dict[itemIdentity] = version;
                    }
                }
                return versionInfo;
            }
        }
    }
}
