// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks.UnitTests;

[TestClass]
public class GivenAWriteFrameworkReferenceFetchManifest
{
    [TestMethod]
    [DataRow("")]
    [DataRow("Windows SDK 7.0 (a;b) & %$(literal)")]
    public void ItWritesImportableFrameworkCandidateItems(string platformDisplayName)
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string outputFile = Path.Combine(testDirectory, "framework-reference-manifest.props");

        try
        {
            var compileReference = new TaskItem("/packs/Microsoft.NETCore.App.Ref/ref/net11.0/System.Runtime.dll");
            compileReference.SetMetadata("FrameworkReferenceName", "Microsoft.NETCore.App");
            compileReference.SetMetadata("NuGetPackageId", "Microsoft.NETCore.App.Ref");

            var runtimeFramework = new TaskItem("Microsoft.NETCore.App");
            runtimeFramework.SetMetadata("Version", "11.0.0");

            var resolvedFrameworkReference = new TaskItem("Microsoft.NETCore.App");
            resolvedFrameworkReference.SetMetadata("TargetingPackName", "Microsoft.NETCore.App.Ref");

            var task = new WriteFrameworkReferenceFetchManifest
            {
                BuildEngine = new MockBuildEngine(),
                OutputFile = outputFile,
                TargetFramework = "net11.0",
                TargetPlatformIdentifier = "Windows",
                TargetPlatformVersion = "7.0",
                TargetPlatformDisplayName = platformDisplayName,
                CompileReferences = [compileReference],
                RuntimeFrameworks = [runtimeFramework],
                ResolvedFrameworkReferences = [resolvedFrameworkReference],
                AppHostSourcePath = "/packs/Microsoft.NETCore.App.Host/runtimes/linux-x64/native/apphost",
                AppHostRuntimeIdentifier = "linux-x64",
            };

            task.Execute().Should().BeTrue();

            XDocument manifest = XDocument.Load(outputFile);
            XElement project = manifest.Root!;
            project.Element("PropertyGroup")!.Element("_UsingFrameworkReferenceManifest")!.Value.Should().Be("true");
            XElement properties = project.Element("PropertyGroup")!;
            properties.Element("_FrameworkReferencePlatformSDKFactsVersion")!.Value.Should().Be("1");
            properties.Element("_FrameworkReferenceManifestTargetPlatformIdentifier")!.Value.Should().Be("Windows");
            properties.Element("_FrameworkReferenceManifestTargetPlatformVersion")!.Value.Should().Be("7.0");
            ProjectCollection.Unescape(properties.Element("_FrameworkReferenceManifestTargetPlatformDisplayName")!.Value)
                .Should().Be(platformDisplayName);

            XElement itemGroup = project.Element("ItemGroup")!;
            XElement compileCandidate = itemGroup.Element("_FrameworkReferenceManifestCompileReference")!;
            compileCandidate.Attribute("Include")!.Value.Should().Be(compileReference.ItemSpec);
            compileCandidate.Element("ReferenceAssembly")!.Value.Should().Be(compileReference.ItemSpec);
            compileCandidate.Element("TargetFramework")!.Value.Should().Be("net11.0");
            compileCandidate.Element("FrameworkReferenceName")!.Value.Should().Be("Microsoft.NETCore.App");
            compileCandidate.Element("NuGetPackageId")!.Value.Should().Be("Microsoft.NETCore.App.Ref");

            XElement appHostCandidate = itemGroup.Element("_FrameworkReferenceManifestAppHost")!;
            appHostCandidate.Attribute("Include")!.Value.Should().Be(task.AppHostSourcePath);
            appHostCandidate.Element("RuntimeIdentifier")!.Value.Should().Be("linux-x64");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
