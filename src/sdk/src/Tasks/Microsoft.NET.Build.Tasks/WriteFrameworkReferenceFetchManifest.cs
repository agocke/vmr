// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Text;
using System.Xml;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

[MSBuildMultiThreadableTask]
public sealed class WriteFrameworkReferenceFetchManifest : TaskBase
{
    [Required]
    public string OutputFile { get; set; } = null!;

    [Required]
    public string TargetFramework { get; set; } = null!;

    [Required]
    public ITaskItem[] FrameworkReferences { get; set; } = [];

    public ITaskItem[] CompileReferences { get; set; } = [];

    public ITaskItem[] RuntimeFrameworks { get; set; } = [];

    public ITaskItem[] ResolvedFrameworkReferences { get; set; } = [];

    public string? AppHostSourcePath { get; set; }

    public string? AppHostRuntimeIdentifier { get; set; }

    protected override void ExecuteCore()
    {
        string content = GenerateManifest();
        if (File.Exists(OutputFile) && File.ReadAllText(OutputFile) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile)!);
        File.WriteAllText(OutputFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string GenerateManifest()
    {
        string frameworkReference = FrameworkReferences[0].ItemSpec;
        var content = new StringBuilder();
        using (XmlWriter writer = XmlWriter.Create(content, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true,
        }))
        {
            writer.WriteStartElement("Project");
            writer.WriteStartElement("PropertyGroup");
            WriteProperty(writer, "_FrameworkReferenceFetchManifestVersion", "1");
            WriteProperty(writer, "_UsingFrameworkReferenceManifest", "true");
            WriteProperty(writer, "SkipResolvePackageAssets", "true");
            WriteProperty(writer, "DisableCheckingDuplicateNuGetItems", "true");
            WriteProperty(writer, "UseAppHostFromAssetsFile", "false");
            writer.WriteEndElement();

            writer.WriteStartElement("ItemGroup");
            ITaskItem[] compileReferences = (ITaskItem[])CompileReferences.Clone();
            Array.Sort(
                compileReferences,
                static (left, right) =>
                {
                    int result = StringComparer.OrdinalIgnoreCase.Compare(left.ItemSpec, right.ItemSpec);
                    return result != 0
                        ? result
                        : StringComparer.Ordinal.Compare(left.ItemSpec, right.ItemSpec);
                });

            foreach (ITaskItem compileReference in compileReferences)
            {
                string sourceFrameworkReference = compileReference.GetMetadata("FrameworkReferenceName");
                WriteItem(
                    writer,
                    "_FrameworkReferenceManifestCompileReference",
                    compileReference,
                    string.IsNullOrEmpty(sourceFrameworkReference) ? frameworkReference : sourceFrameworkReference,
                    writeReferenceAssembly: true);
            }

            foreach (ITaskItem runtimeFramework in RuntimeFrameworks)
            {
                WriteItem(
                    writer,
                    "_FrameworkReferenceManifestRuntimeFramework",
                    runtimeFramework,
                    frameworkReference);
            }

            foreach (ITaskItem resolvedFrameworkReference in ResolvedFrameworkReferences)
            {
                WriteItem(
                    writer,
                    "_FrameworkReferenceManifestFrameworkReference",
                    resolvedFrameworkReference,
                    resolvedFrameworkReference.ItemSpec);
            }

            if (!string.IsNullOrEmpty(AppHostSourcePath))
            {
                writer.WriteStartElement("_FrameworkReferenceManifestAppHost");
                writer.WriteAttributeString("Include", Escape(AppHostSourcePath));
                WriteMetadata(writer, "TargetFramework", TargetFramework);
                WriteMetadata(writer, "FrameworkReference", frameworkReference);
                WriteMetadata(writer, "RuntimeIdentifier", AppHostRuntimeIdentifier);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return content.ToString();
    }

    private void WriteItem(
        XmlWriter writer,
        string itemType,
        ITaskItem item,
        string frameworkReference,
        bool writeReferenceAssembly = false)
    {
        writer.WriteStartElement(itemType);
        writer.WriteAttributeString("Include", Escape(item.ItemSpec));

        var metadata = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry entry in item.CloneCustomMetadata())
        {
            if (entry.Key is string metadataName && entry.Value is string metadataValue)
            {
                metadata.Add(new KeyValuePair<string, string>(metadataName, metadataValue));
            }
        }

        metadata.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        foreach (KeyValuePair<string, string> metadataEntry in metadata)
        {
            string metadataName = metadataEntry.Key;
            if (metadataName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase)
                || metadataName.Equals("FrameworkReference", StringComparison.OrdinalIgnoreCase)
                || metadataName.Equals("ReferenceAssembly", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            WriteMetadata(writer, metadataName, metadataEntry.Value);
        }

        if (writeReferenceAssembly)
        {
            WriteMetadata(writer, "ReferenceAssembly", item.ItemSpec);
        }

        WriteMetadata(writer, "TargetFramework", TargetFramework);
        WriteMetadata(writer, "FrameworkReference", frameworkReference);
        writer.WriteEndElement();
    }

    private static void WriteProperty(XmlWriter writer, string name, string value)
    {
        writer.WriteElementString(name, Escape(value));
    }

    private static void WriteMetadata(XmlWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteElementString(name, Escape(value));
        }
    }

    private static string Escape(string value)
        => ProjectCollection.Escape(value);
}
