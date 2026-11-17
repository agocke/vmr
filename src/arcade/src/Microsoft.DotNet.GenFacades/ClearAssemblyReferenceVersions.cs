// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Microsoft.DotNet.GenFacades;

/// <summary>
/// Rewrites an Assembly's references to be version 0.0.0.0.
/// </summary>
[MSBuildDeclaredIOTask(SandboxVersion = 1)]
public class ClearAssemblyReferenceVersions : Task, IMultiThreadableTask
{
    /// <summary>Injected by MSBuild so paths resolve against the project directory in multithreaded builds.</summary>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    /// <summary>
    /// Assembly to rewrite.
    /// </summary>
    [Required]
    public string Assembly { get; set; }

    /// <summary>
    /// Destination assembly. When omitted, the input is rewritten in place.
    /// </summary>
    public string OutputAssembly { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; }

    public ITaskItem[] DeclaredOutputs { get; set; }

    public override bool Execute()
    {
        try
        {
            string logicalInput = TaskEnvironment.GetAbsolutePath(Assembly).GetCanonicalForm();
            string logicalOutput = TaskEnvironment.GetAbsolutePath(string.IsNullOrEmpty(OutputAssembly) ? Assembly : OutputAssembly).GetCanonicalForm();
            StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            bool inPlace = comparer.Equals(logicalInput, logicalOutput);
            if (inPlace && TaskEnvironment.IsSandboxed)
            {
                Log.LogError("Sandboxed assembly rewriting requires a separate output assembly.");
                return false;
            }

            if (DeclaredInputs is not null || DeclaredOutputs is not null)
            {
                if (DeclaredInputs is null || DeclaredInputs.Length != 1
                    || !comparer.Equals(logicalInput, TaskEnvironment.GetAbsolutePath(DeclaredInputs[0].ItemSpec).GetCanonicalForm())
                    || DeclaredOutputs is null || DeclaredOutputs.Length != 1
                    || !comparer.Equals(logicalOutput, TaskEnvironment.GetAbsolutePath(DeclaredOutputs[0].ItemSpec).GetCanonicalForm())
                    || inPlace)
                {
                    Log.LogError("Declared I/O requires exactly the input assembly and a different output assembly.");
                    return false;
                }
            }

            string input = TaskEnvironment.GetInputPath(Assembly);
            string output = TaskEnvironment.GetOutputPath(string.IsNullOrEmpty(OutputAssembly) ? Assembly : OutputAssembly);
            if (inPlace)
            {
                using FileStream stream = File.Open(input, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                Rewrite(stream);
            }
            else
            {
                byte[] contents = File.ReadAllBytes(input);
                using (MemoryStream stream = new(contents, writable: true))
                {
                    Rewrite(stream);
                }

                Directory.CreateDirectory(TaskEnvironment.GetExecutionDirectory(Path.GetDirectoryName(logicalOutput)));
                File.WriteAllBytes(output, contents);
            }
        }
        catch (Exception e)
        {
            Log.LogErrorFromException(e, showStackTrace: false);
        }

        return !Log.HasLoggedErrors;
    }

    private static void Rewrite(Stream stream)
    {
        using PEReader peReader = new(stream);
        using BinaryWriter writer = new(stream);
        MetadataReader mdReader = peReader.GetMetadataReader();
        int assemblyRefOffset = mdReader.GetTableMetadataOffset(TableIndex.AssemblyRef);
        int numAssemblyRef = mdReader.GetTableRowCount(TableIndex.AssemblyRef);
        int sizeAssemblyRefRow = mdReader.GetTableRowSize(TableIndex.AssemblyRef);

        for (int assemblyRefRow = 0; assemblyRefRow < numAssemblyRef; assemblyRefRow++)
        {
            stream.Position = peReader.PEHeaders.MetadataStartOffset + assemblyRefOffset + assemblyRefRow * sizeAssemblyRefRow;
            // The AssemblyRef row starts with four 2-byte version components (ECMA-335 II.22.5).
            writer.Write((long)0);
        }

        if (peReader.PEHeaders.PEHeader.CertificateTableDirectory.Size != 0)
        {
            // Metadata changes invalidate an existing Authenticode signature.
            int certificateTableDirectoryOffset = peReader.PEHeaders.PEHeader.Magic == PEMagic.PE32Plus ? 144 : 128;
            stream.Position = peReader.PEHeaders.PEHeaderStartOffset + certificateTableDirectoryOffset;
            writer.Write((long)0);
        }
    }
}
