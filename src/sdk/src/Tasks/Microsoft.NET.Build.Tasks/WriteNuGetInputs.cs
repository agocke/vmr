// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using NuGet.ProjectModel;

namespace Microsoft.NET.Build.Tasks;

public sealed class WriteNuGetInputs : TaskBase
{
    [Required]
    public string InputFile { get; set; } = null!;

    [Required]
    public string OutputFile { get; set; } = null!;

    public bool CapturePackageContext { get; set; }

    public string CompilerApiVersion { get; set; } = "";

    public string ProjectLanguage { get; set; } = "";

    protected override void ExecuteCore()
    {
        string content;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(InputFile));
            content = GenerateManifest(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or BuildErrorException or BadImageFormatException)
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
            if (CapturePackageContext)
            {
                writer.WriteElementString("_NuGetPackageContextVersion", "1");
                writer.WriteElementString("_NuGetConflictFactsVersion", "1");
                writer.WriteElementString("_NuGetCompilerApiVersion", Escape(CompilerApiVersion));
            }
            writer.WriteEndElement();
            writer.WriteStartElement("ItemGroup");
            WriteValue(writer, root, "$");
            if (CapturePackageContext)
            {
                WritePackageContext(writer, root);
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return content.ToString();
    }

    private void WritePackageContext(XmlWriter writer, JsonElement root)
    {
        if ((ProjectLanguage is "C#" or "VB") && string.IsNullOrEmpty(CompilerApiVersion))
        {
            throw new FormatException("Missing compiler API identity during package preparation.");
        }
        // Discovery is confined to preparation. Build reconstructs only the model
        // fields used by selection, directly from the persisted facts.
        LockFile lockFile = new LockFileFormat().Parse(root.GetRawText(), InputFile);
        if (lockFile.Version is not (3 or 4))
        {
            throw new FormatException("Invalid assets model during package preparation.");
        }
        NuGetPackageResolver resolver = NuGetPackageResolver.CreateResolver(lockFile);
        foreach (LockFileLibrary library in lockFile.Libraries)
        {
            if (!library.IsPackage())
            {
                continue;
            }
            string directory = resolver.GetPackageDirectory(library.Name, library.Version, out string packageRoot);
            if (directory == null)
            {
                throw new FormatException($"Missing package '{library.Name}/{library.Version}' during preparation.");
            }
            WriteContextItem(writer, "_NuGetPackageLocation", library.Name + "/" + library.Version.ToNormalizedString(),
                ("PackageDirectory", directory), ("PackageRoot", packageRoot));
        }
        HashSet<string> relatedFiles = new(StringComparer.Ordinal);
        HashSet<string> locales = new(StringComparer.Ordinal);
        HashSet<string> versionFiles = new(StringComparer.Ordinal);
        foreach (LockFileTarget target in lockFile.Targets)
        {
            foreach (LockFileTargetLibrary library in target.Libraries)
            {
                if (!library.IsPackage())
                {
                    continue;
                }
                foreach (LockFileItem asset in library.CompileTimeAssemblies.Concat(library.RuntimeAssemblies)
                    .Concat(library.NativeLibraries).Concat(library.ResourceAssemblies).Concat(library.RuntimeTargets))
                {
                    if (asset.IsPlaceholderFile())
                    {
                        continue;
                    }
                    string assetPath = resolver.ResolvePackageAssetPath(library, asset.Path);
                    if (versionFiles.Add(assetPath))
                    {
                        var versions = RuntimeFileVersions.Read(assetPath);
                        WriteContextItem(writer, "_NuGetConflictFile", assetPath,
                            ("AssemblyVersion", versions.AssemblyVersion ?? ""), ("FileVersion", versions.FileVersion));
                    }
                    if (!asset.Properties.TryGetValue("related", out string? related))
                    {
                        continue;
                    }
                    foreach (string extension in related.Split(';'))
                    {
                        if (extension is not (".pdb" or ".xml"))
                        {
                            continue;
                        }
                        string path = Path.ChangeExtension(resolver.ResolvePackageAssetPath(library, asset.Path), extension);
                        if (relatedFiles.Add(path))
                        {
                            WriteContextItem(writer, "_NuGetRelatedFile", path, ("Exists", File.Exists(path) ? "true" : "false"));
                        }
                    }
                }
                foreach (LockFileItem asset in library.ResourceAssemblies)
                {
                    if (asset.IsPlaceholderFile())
                    {
                        continue;
                    }
                    if (!asset.Properties.TryGetValue("locale", out string? locale))
                    {
                        throw new FormatException($"Missing resource locale for '{asset.Path}'.");
                    }
                    if (locales.Add(locale))
                    {
                        try
                        {
                            string normalized = CultureInfo.GetCultureInfo(locale).Name;
                            WriteContextItem(writer, "_NuGetLocale", "$" + locale, ("Locale", locale), ("Name", normalized), ("Valid", "true"));
                        }
                        catch (CultureNotFoundException)
                        {
                            WriteContextItem(writer, "_NuGetLocale", "$" + locale, ("Locale", locale), ("Name", locale), ("Valid", "false"));
                        }
                    }
                }
            }
        }
    }

    private static void WriteContextItem(XmlWriter writer, string type, string identity, params (string Name, string Value)[] metadata)
    {
        writer.WriteStartElement(type);
        writer.WriteAttributeString("Include", Escape(identity));
        foreach ((string name, string value) in metadata)
        {
            writer.WriteElementString(name, Escape(value));
        }
        writer.WriteEndElement();
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
