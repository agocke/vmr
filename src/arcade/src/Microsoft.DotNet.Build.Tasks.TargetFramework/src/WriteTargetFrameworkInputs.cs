// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Text;
using System.Xml;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Build.Tasks.TargetFramework;

[MSBuildMultiThreadableTask]
public sealed class WriteTargetFrameworkInputs : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Required]
    public string RuntimeGraph { get; set; } = null!;

    [Required]
    public string OutputFile { get; set; } = null!;

    public override bool Execute()
    {
        string runtimeGraphPath = TaskEnvironment.GetAbsolutePath(RuntimeGraph);
        string outputPath = TaskEnvironment.GetAbsolutePath(OutputFile);
        string runtimeGraphContents = File.ReadAllText(runtimeGraphPath);
        StringBuilder content = new();
        using (XmlWriter writer = XmlWriter.Create(content, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true,
        }))
        {
            writer.WriteStartElement("Project");
            writer.WriteStartElement("PropertyGroup");
            writer.WriteElementString("_TargetFrameworkInputsVersion", "1");
            writer.WriteElementString("_TargetFrameworkInputsRuntimeGraphPath", ProjectCollection.Escape(runtimeGraphPath));
            writer.WriteElementString("_TargetFrameworkInputsRuntimeGraphContents", ProjectCollection.Escape(runtimeGraphContents));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        string text = content.ToString();
        if (!File.Exists(outputPath) || File.ReadAllText(outputPath) != text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return !Log.HasLoggedErrors;
    }
}
