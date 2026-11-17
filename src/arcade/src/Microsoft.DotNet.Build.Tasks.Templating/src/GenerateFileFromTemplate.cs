// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Build.Tasks.Templating;

/// <summary>
/// <para>
/// Generates a new file at <see cref="OutputPath"/>.
/// </para>
/// <para>
/// The <see cref="TemplateFile"/> can define variables for substitution using <see cref="Properties"/>.
/// </para>
/// <example>
/// The input file might look like this:
/// <code>
/// 2 + 2 = ${Sum}
/// </code>
/// When the task is invoked like this, it will produce "2 + 2 = 4"
/// <code>
/// &lt;GenerateFileFromTemplate Properties="Sum=4;OtherValue=123;" ... &gt;
/// </code>
/// </example>
/// </summary>
[MSBuildMultiThreadableTask]
[MSBuildDeclaredIOTask(SandboxVersion = 1)]
public class GenerateFileFromTemplate : Task, IMultiThreadableTask
{
    /// <summary>Injected by MSBuild so paths resolve against the project directory in multithreaded builds.</summary>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    /// <summary>
    /// The template file using the variable syntax <c>${VarName}</c>.
    /// If your template file needs to output this format, you can escape the dollar sign with a backtick e.g. <c>`${NotReplaced}</c>.
    /// </summary>
    [Required]
    public string TemplateFile { get; set; }

    /// <summary>
    /// The destination for the generated file.
    /// </summary>
    [Required]
    public string OutputPath { get; set; }

    /// <summary>
    /// Key=Value pairs of values, separated by semicolons e.g. <c>Properties="Sum=4;OtherValue=123;"</c>.
    /// </summary>
    [Required]
    public string[] Properties { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the output file is only written if it does not already exist
    /// or if its current contents differ from what the task would write. Defaults to <see langword="false"/>.
    /// <para>
    /// Enabling this preserves the output file's timestamp when its contents are unchanged, which can
    /// significantly speed up incremental builds by avoiding unnecessary invalidation of downstream targets.
    /// </para>
    /// </summary>
    public bool SkipUnchanged { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; }

    public ITaskItem[] DeclaredOutputs { get; set; }

    /// <summary>
    /// The destination for the generated file resolved by this task.
    /// </summary>
    [Output]
    public string ResolvedOutputPath { get; set; }

    public override bool Execute()
    {
        // GetAbsolutePath deliberately does not canonicalize, but this output property was
        // produced by Path.GetFullPath, which also resolved "." and ".." and turned the forward
        // slashes substituted just above back into the platform separator.
        AbsolutePath logicalOutputPath = TaskEnvironment.GetAbsolutePath(OutputPath.Replace('\\', '/')).GetCanonicalForm();
        AbsolutePath outputPath = TaskEnvironment.GetOutputPath(OutputPath.Replace('\\', '/')).GetCanonicalForm();
        ResolvedOutputPath = logicalOutputPath;

        AbsolutePath logicalTemplateFile = TaskEnvironment.GetAbsolutePath(TemplateFile).GetCanonicalForm();
        AbsolutePath templateFile = TaskEnvironment.GetInputPath(TemplateFile);
        if (TaskEnvironment.IsSandboxed && PathComparer.Equals(logicalTemplateFile.Value, logicalOutputPath.Value))
        {
            Log.LogError("Sandboxed template generation requires distinct input and output paths.");
            return false;
        }

        if (!ValidateDeclaredFiles(logicalTemplateFile, logicalOutputPath))
        {
            return false;
        }

        if (!File.Exists(templateFile))
        {
            Log.LogError($"File {TemplateFile} does not exist");
            return false;
        }

        IDictionary<string, string> values = MSBuildListSplitter.GetNamedProperties(Properties, Log);
        string template = File.ReadAllText(templateFile);

        string result = Replace(template, values);

        // File.WriteAllText writes UTF-8 without a byte-order mark, so compare against the same encoding
        // to determine whether the on-disk bytes would actually change.
        byte[] resultBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(result);

        if (SkipUnchanged && FileContentsMatch(outputPath, resultBytes))
        {
            Log.LogMessage(MessageImportance.Low, $"Skipping unchanged file {ResolvedOutputPath}");
            return !Log.HasLoggedErrors;
        }

        string directory = Path.GetDirectoryName(logicalOutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(TaskEnvironment.GetExecutionDirectory(directory));
        }

        File.WriteAllBytes(outputPath, resultBytes);

        return !Log.HasLoggedErrors;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private bool ValidateDeclaredFiles(AbsolutePath templateFile, AbsolutePath outputPath)
    {
        if (DeclaredInputs is null && DeclaredOutputs is null)
        {
            return true;
        }

        StringComparer comparer = PathComparer;
        if (DeclaredInputs is null || DeclaredInputs.Length != 1
            || !comparer.Equals(templateFile.Value, TaskEnvironment.GetAbsolutePath(DeclaredInputs[0].ItemSpec).GetCanonicalForm().Value))
        {
            Log.LogError("DeclaredInputs must contain exactly TemplateFile.");
            return false;
        }

        if (DeclaredOutputs is null || DeclaredOutputs.Length != 1
            || !comparer.Equals(outputPath.Value, TaskEnvironment.GetAbsolutePath(DeclaredOutputs[0].ItemSpec.Replace('\\', '/')).GetCanonicalForm().Value))
        {
            Log.LogError("DeclaredOutputs must contain exactly OutputPath.");
            return false;
        }

        return true;
    }

    private static bool FileContentsMatch(AbsolutePath path, byte[] expectedBytes)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length != expectedBytes.Length)
        {
            return false;
        }

        using FileStream stream = fileInfo.OpenRead();
        byte[] buffer = new byte[4096];
        int offset = 0;
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            // Guard against the file growing after the initial length check.
            if (offset + bytesRead > expectedBytes.Length)
            {
                return false;
            }

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] != expectedBytes[offset + i])
                {
                    return false;
                }
            }

            offset += bytesRead;
        }

        // Ensure every expected byte was compared (guards against the file being truncated).
        return offset == expectedBytes.Length;
    }

    public string Replace(string template, IDictionary<string, string> values)
    {
        StringBuilder sb = new();
        StringBuilder varNameSb = new();
        int line = 1;
        for (int i = 0; i < template.Length; i++)
        {
            char templateChar = template[i];
            char nextTemplateChar = i + 1 >= template.Length
                    ? '\0'
                    : template[i + 1];

            // count lines in the template file
            if (templateChar == '\n')
            {
                line++;
            }

            if (templateChar == '`' && (nextTemplateChar == '$' || nextTemplateChar == '`'))
            {
                // skip the backtick for known escape characters
                i++;
                sb.Append(nextTemplateChar);
                continue;
            }

            if (templateChar != '$' || nextTemplateChar != '{')
            {
                // variables begin with ${. Moving on.
                sb.Append(templateChar);
                continue;
            }

            varNameSb.Clear();
            i += 2;
            for (; i < template.Length; i++)
            {
                templateChar = template[i];
                if (templateChar != '}')
                {
                    varNameSb.Append(templateChar);
                }
                else
                {
                    // Found the end of the variable substitution
                    string varName = varNameSb.ToString();
                    if (values.TryGetValue(varName, out string value))
                    {
                        sb.Append(value);
                    }
                    else
                    {
                        Log.LogWarning(null, null, null, TemplateFile,
                            line, 0, 0, 0,
                            message: $"No property value is available for '{varName}'");
                    }

                    varNameSb.Clear();
                    break;
                }
            }

            if (varNameSb.Length > 0)
            {
                Log.LogWarning(null, null, null, TemplateFile,
                            line, 0, 0, 0,
                            message: "Expected closing bracket for variable placeholder. No substitution will be made.");
                sb.Append("${").Append(varNameSb.ToString());
            }
        }

        return sb.ToString();
    }
}
