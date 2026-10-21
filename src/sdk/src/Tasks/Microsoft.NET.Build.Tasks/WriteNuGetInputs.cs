// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

public sealed class WriteNuGetInputs : TaskBase
{
    [Required]
    public string InputFile { get; set; } = null!;

    [Required]
    public string OutputFile { get; set; } = null!;

    protected override void ExecuteCore()
    {
        string content;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(InputFile));
            content = GenerateManifest(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.LogError(Strings.ErrorReadingAssetsFile, $"{InputFile}: {ex.Message}");
            return;
        }

        if (File.Exists(OutputFile) && File.ReadAllText(OutputFile) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile)!);
        File.WriteAllText(OutputFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string GenerateManifest(JsonElement root)
    {
        StringBuilder content = new();
        using (XmlWriter writer = XmlWriter.Create(content, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Entitize,
        }))
        {
            writer.WriteStartElement("Project");
            writer.WriteStartElement("PropertyGroup");
            writer.WriteElementString("_NuGetInputsFormatVersion", "1");
            writer.WriteElementString("_NuGetInputsSourceFile", Escape(InputFile));
            writer.WriteEndElement();
            writer.WriteStartElement("ItemGroup");
            WriteValue(writer, root, "$");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return content.ToString();
    }

    private static void WriteValue(XmlWriter writer, JsonElement value, string path)
    {
        writer.WriteStartElement("_NuGetInput");
        writer.WriteAttributeString("Include", Escape(path));
        writer.WriteElementString("Kind", value.ValueKind.ToString());
        if (value.ValueKind == JsonValueKind.String)
        {
            writer.WriteElementString("Value", Escape(value.GetString()!));
        }
        else if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            writer.WriteElementString("Value", Escape(value.GetRawText()));
        }
        writer.WriteEndElement();

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    // JSON Pointer escaping preserves keys containing slashes or tildes.
                    string key = property.Name.Replace("~", "~0").Replace("/", "~1");
                    WriteValue(writer, property.Value, path + "/" + key);
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement element in value.EnumerateArray())
                {
                    WriteValue(writer, element, path + "/" + index.ToString(CultureInfo.InvariantCulture));
                    index++;
                }
                break;
        }
    }

    private static string Escape(string value)
    {
        string escaped = ProjectCollection.Escape(value);
        StringBuilder? result = null;
        for (int i = 0; i < escaped.Length; i++)
        {
            char character = escaped[i];
            if (character < ' ' && character is not '\t' and not '\r' and not '\n')
            {
                // XML 1.0 cannot represent these characters, but MSBuild escapes can.
                result ??= new StringBuilder(escaped.Length).Append(escaped, 0, i);
                result.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                result?.Append(character);
            }
        }

        return result?.ToString() ?? escaped;
    }
}
