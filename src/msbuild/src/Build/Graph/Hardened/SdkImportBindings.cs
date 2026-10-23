// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;
#if NETFRAMEWORK
using File = Microsoft.IO.File;
using Path = Microsoft.IO.Path;
#endif

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal sealed class SdkImportBindings
{
    internal const string DirectoryProperty = "MSBuildGraphSdkBindingsDirectory";

    private readonly string _directory;
    private readonly string _projectPath;
    private readonly bool _interactive;
    private readonly bool _isRunningInVisualStudio;
    private readonly IFileSystem _fileSystem;

    internal SdkImportBindings(
        string directory,
        string projectPath,
        bool interactive,
        bool isRunningInVisualStudio,
        IFileSystem fileSystem,
        IElementLocation location)
    {
        Require(Path.IsPathFullyQualified(directory) && Path.IsPathFullyQualified(projectPath),
            location, $"nonabsolute {DirectoryProperty} or project path");
        _directory = Path.GetFullPath(directory);
        _projectPath = Path.GetFullPath(projectPath);
        _interactive = interactive;
        _isRunningInVisualStudio = isRunningInVisualStudio;
        _fileSystem = fileSystem;
    }

    internal void Save(SdkReference reference, string? solutionPath, SdkResult result, IElementLocation location)
    {
        ValidateResult(result, location);
        XElement request = CreateRequest(reference, solutionPath);
        string path = GetPath(request);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_directory);
            temporaryPath = Path.Combine(_directory, Path.GetRandomFileName() + ".tmp");
            using (XmlWriter writer = XmlWriter.Create(temporaryPath, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Entitize,
            }))
            {
                CreateDocument(request, result).Save(writer);
            }

            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e) || e is XmlException or ArgumentException)
        {
            Require(false, location, $"writing prepared SDK binding '{path}': {e.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal bool TryResolve(
        SdkReference reference,
        string? solutionPath,
        IElementLocation location,
        out SdkResult? result)
    {
        XElement request = CreateRequest(reference, solutionPath);
        string path = GetPath(request);
        result = null;
        if (!_fileSystem.FileExists(path))
        {
            return false;
        }

        try
        {
            using Stream stream = File.OpenRead(path);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
            });
            XElement document = XElement.Load(reader);
            XElement binding = document.Element("Result")
                ?? throw new FormatException("Missing SDK result.");
            XElement originalReference = binding.Element("SdkReference")
                ?? throw new FormatException("Missing SDK result reference.");
            SdkReference resultReference = new(
                Attribute(originalReference, "Name"),
                (string?)originalReference.Attribute("Version"),
                (string?)originalReference.Attribute("MinimumVersion"));
            XElement? itemsElement = binding.Element("Items");
            Dictionary<string, SdkResultItem>? items = itemsElement?.Elements("Item").ToDictionary(
                item => Attribute(item, "Name"),
                item => new SdkResultItem(Attribute(item, "ItemSpec"), ReadValues(item, "Metadata")),
                StringComparer.OrdinalIgnoreCase);
            result = new SdkResult(
                resultReference,
                binding.Element("Paths")?.Elements("Path").Select(item => Attribute(item, "Value")).ToArray(),
                (string?)binding.Attribute("Version"),
                ReadValues(binding, "Properties"),
                items,
                binding.Element("Warnings")?.Elements("Warning").Select(item => Attribute(item, "Value")).ToArray(),
                ReadValues(binding, "Environment"));
            ValidateResult(result, location);
            Require(XNode.DeepEquals(document, CreateDocument(request, result)),
                location, $"a stale or invalid prepared SDK binding '{path}'; run Restore again");
            return true;
        }
        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e) || e is XmlException or FormatException or ArgumentException)
        {
            Require(false, location, $"reading prepared SDK binding '{path}': {e.Message}; run Restore again");
            return false;
        }
    }

    private XElement CreateRequest(SdkReference reference, string? solutionPath)
        => new("Request",
            new XAttribute("Project", _projectPath),
            OptionalAttribute("Solution", solutionPath),
            new XAttribute("Name", reference.Name),
            OptionalAttribute("Version", reference.Version),
            OptionalAttribute("MinimumVersion", reference.MinimumVersion),
            new XAttribute("Interactive", _interactive),
            new XAttribute("VisualStudio", _isRunningInVisualStudio));

    private string GetPath(XElement request)
    {
        using SHA256 hash = SHA256.Create();
        string digest = BitConverter.ToString(hash.ComputeHash(
            Encoding.UTF8.GetBytes(request.ToString(SaveOptions.DisableFormatting)))).Replace("-", "").ToLowerInvariant();
        return Path.Combine(_directory, digest + ".xml");
    }

    private static XElement CreateDocument(XElement request, SdkResult result)
        => new("SdkBinding", new XAttribute("FormatVersion", "1"), request,
            new XElement("Result",
                OptionalAttribute("Version", result.Version),
                new XElement("SdkReference",
                    new XAttribute("Name", result.SdkReference.Name),
                    OptionalAttribute("Version", result.SdkReference.Version),
                    OptionalAttribute("MinimumVersion", result.SdkReference.MinimumVersion)),
                result.Path is null ? null : new XElement("Paths",
                    new XElement("Path", new XAttribute("Value", result.Path)),
                    result.AdditionalPaths?.Select(path => new XElement("Path", new XAttribute("Value", path)))),
                WriteValues("Properties", result.PropertiesToAdd),
                result.ItemsToAdd is null ? null : new XElement("Items",
                    result.ItemsToAdd.Select(item => new XElement("Item",
                        new XAttribute("Name", item.Key),
                        new XAttribute("ItemSpec", item.Value.ItemSpec),
                        WriteValues("Metadata", item.Value.Metadata)))),
                result.Warnings is null ? null : new XElement("Warnings",
                    result.Warnings.Select(warning => new XElement("Warning", new XAttribute("Value", warning)))),
                WriteValues("Environment", result.EnvironmentVariablesToAdd)));

    private static XElement? WriteValues(string name, IDictionary<string, string>? values)
        => values is null ? null : new XElement(name,
            values.Select(value => new XElement("Value",
                new XAttribute("Name", value.Key), new XAttribute("Value", value.Value))));

    private static Dictionary<string, string>? ReadValues(XElement parent, string name)
        => parent.Element(name)?.Elements("Value").ToDictionary(
            value => Attribute(value, "Name"), value => Attribute(value, "Value"), StringComparer.OrdinalIgnoreCase);

    private static XAttribute? OptionalAttribute(string name, string? value)
        => value is null ? null : new XAttribute(name, value);

    private static string Attribute(XElement element, string name)
        => (string?)element.Attribute(name) ?? throw new FormatException($"Missing '{name}' on '{element.Name}'.");

    private static void ValidateResult(SdkResult result, IElementLocation location)
    {
        Require(result.Success, location, "capturing an unsuccessful SDK resolution");
        Require(result.SdkReference is not null, location, "a prepared SDK result without its original reference");
        Require(result.Path is null || Path.IsPathFullyQualified(result.Path),
            location, "a nonabsolute prepared SDK path");
        Require(result.AdditionalPaths is null ||
            (result.Path is not null && result.AdditionalPaths.All(Path.IsPathFullyQualified)),
            location, "nonabsolute or unrooted additional SDK paths");
    }

    private static void Require(bool condition, IElementLocation location, string construct)
        => ProjectErrorUtilities.VerifyThrowInvalidProject(
            condition, location, "HardenedGraphUnsupportedConstruct", construct, "prepared SDK imports");
}
