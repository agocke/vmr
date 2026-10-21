// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;
#if NETFRAMEWORK
using Path = Microsoft.IO.Path;
#else
using Path = System.IO.Path;
#endif

#nullable enable

namespace Microsoft.Build.Tasks;

/// <summary>
/// Transforms explicit reference artifacts without opening assemblies or discovering dependencies.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class ResolveReferencePaths : TaskExtension, IMultiThreadableTask
{
    public ITaskItem[] References { get; set; } = [];
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Output]
    public ITaskItem[] ResolvedFiles { get; private set; } = [];

    [Output]
    public ITaskItem[] RelatedFiles { get; private set; } = [];

    [Output]
    public ITaskItem[] CopyLocalFiles { get; private set; } = [];

    public override bool Execute()
    {
        ResolvedFiles = [];
        RelatedFiles = [];
        CopyLocalFiles = [];
        List<ITaskItem> resolved = new(References.Length);
        List<ITaskItem> related = [];
        List<ITaskItem> copyLocal = [];

        foreach (ITaskItem source in References)
        {
            if (!TryGetPath(source.ItemSpec, nameof(References), out string path))
            {
                return false;
            }

            TaskItem reference = new(source) { ItemSpec = EscapingUtilities.Escape(path) };
            if (!TryGetBoolean(reference, "FrameworkFile", false, out bool frameworkFile)
                || !TryGetBoolean(reference, "CopyLocal", !frameworkFile, out bool copy)
                || !TryGetBoolean(reference, "Private", copy, out copy))
            {
                return false;
            }

            reference.SetMetadata("CopyLocal", copy ? "true" : "false");
            if (reference.GetMetadata("ResolvedFrom").Length == 0)
            {
                reference.SetMetadata("ResolvedFrom", "{RawFileName}");
            }
            if (string.Equals(reference.GetMetadata("ReferenceSourceTarget"), "ProjectReference", StringComparison.OrdinalIgnoreCase)
                && reference.GetMetadata("ProjectReferenceOriginalItemSpec").Length == 0)
            {
                reference.SetMetadata("ProjectReferenceOriginalItemSpec", EscapingUtilities.Escape(reference.GetMetadata("OriginalProjectReferenceItemSpec")));
            }

            string referenceAssembly = reference.GetMetadata("ReferenceAssembly");
            if (referenceAssembly.Length != 0)
            {
                if (!TryGetPath(referenceAssembly, "ReferenceAssembly", out string referencePath))
                {
                    return false;
                }
                reference.SetMetadata("ReferenceAssembly", EscapingUtilities.Escape(referencePath));
            }

            resolved.Add(reference);
            if (copy)
            {
                copyLocal.Add(reference);
            }

            string debugSymbols = reference.GetMetadata("DebugSymbolsFile");
            if (debugSymbols.Length != 0)
            {
                if (!TryGetPath(debugSymbols, "DebugSymbolsFile", out string symbolsPath))
                {
                    return false;
                }

                reference.SetMetadata("DebugSymbolsFile", EscapingUtilities.Escape(symbolsPath));
                TaskItem symbols = new(reference) { ItemSpec = EscapingUtilities.Escape(symbolsPath) };
                related.Add(symbols);
                if (copy)
                {
                    copyLocal.Add(symbols);
                }
            }
        }

        ResolvedFiles = [.. resolved];
        RelatedFiles = [.. related];
        CopyLocalFiles = [.. copyLocal];
        return true;
    }

    private bool TryGetPath(string value, string parameter, out string path)
    {
        path = string.Empty;
        try
        {
            string extension = Path.GetExtension(value);
            bool hasDirectory = value.IndexOfAny(['/', '\\']) >= 0;
            bool fileName = string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".winmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileName(value);
            if (!string.IsNullOrWhiteSpace(value)
                && (hasDirectory || fileName)
                && name.Length != 0 && name != "." && name != ".."
                && value[value.Length - 1] != '\\'
                && value[0] != '{'
                && value.IndexOfAny(['*', '?', '\0']) < 0
                && !value.Contains("://")
                && !((Path.IsPathRooted(value) || value.IndexOf(':') >= 0) && !Path.IsPathFullyQualified(value)))
            {
                path = FileUtilities.NormalizePath(TaskEnvironment.GetAbsolutePath(value)).Value;
                return true;
            }
        }
        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
        {
            Log.LogErrorWithCodeFromResources("General.InvalidArgument", e.Message);
            return false;
        }

        Log.LogErrorWithCodeFromResources("CommonSdk.HardenedReferencePath", parameter, value);
        return false;
    }

    private bool TryGetBoolean(ITaskItem reference, string name, bool defaultValue, out bool value)
    {
        string metadata = reference.GetMetadata(name);
        value = defaultValue;
        if (metadata.Length == 0 || ConversionUtilities.TryConvertStringToBool(metadata, out value))
        {
            return true;
        }

        Log.LogErrorWithCodeFromResources("General.InvalidValue", $"{nameof(References)}.{name} ({reference.ItemSpec})", nameof(ResolveReferencePaths));
        return false;
    }
}
