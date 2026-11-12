// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks;

public sealed class WriteFrameworkPlatformInputs : TaskBase
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    [Required]
    public string OutputFile { get; set; } = "";
    public string TargetPlatformIdentifier { get; set; } = "";
    public string TargetPlatformVersion { get; set; } = "";
    public string TargetPlatformDisplayName { get; set; } = "";
    public string PackInputsFile { get; set; } = "";

    protected override void ExecuteCore()
    {
        string file = TaskEnvironment.GetAbsolutePath(OutputFile).Value;
        string content = new XElement("Project", new XElement("PropertyGroup", Properties(
            TargetPlatformIdentifier, TargetPlatformVersion, TargetPlatformDisplayName),
            PackInputsFile.Length == 0 ? null : new XElement("FrameworkPackInputsPath",
                ProjectCollection.Escape(TaskEnvironment.GetAbsolutePath(PackInputsFile).Value)))).ToString() + "\n";
        if (!File.Exists(file) || File.ReadAllText(file) != content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }
    }

    internal static IEnumerable<XElement> Properties(string identifier, string version, string displayName)
    {
        yield return new XElement("_FrameworkReferencePlatformSDKFactsVersion", "1");
        yield return new XElement("_FrameworkReferenceManifestTargetPlatformIdentifier", ProjectCollection.Escape(identifier));
        yield return new XElement("_FrameworkReferenceManifestTargetPlatformVersion", ProjectCollection.Escape(version));
        yield return new XElement("_FrameworkReferenceManifestTargetPlatformDisplayName", ProjectCollection.Escape(displayName));
    }
}
