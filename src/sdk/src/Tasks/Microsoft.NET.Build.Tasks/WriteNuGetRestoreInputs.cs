// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

public sealed class WriteNuGetRestoreInputs : TaskBase
{
    [Required]
    public ITaskItem[] Requests { get; set; } = [];

    [Output]
    public int WrittenModels { get; private set; }

    protected override void ExecuteCore()
    {
        WrittenModels = 0;
        StringComparer comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Dictionary<string, (string Input, string Compiler, string Language)> outputs = new(comparer);
        try
        {
            foreach (ITaskItem request in Requests)
            {
                string input = request.GetMetadata("AssetsFile");
                string output = request.GetMetadata("OutputFile");
                if (!Path.IsPathRooted(input) || !Path.IsPathRooted(output))
                {
                    throw new FormatException($"NuGet input requests require absolute assets and output paths: '{request.ItemSpec}'.");
                }

                input = Path.GetFullPath(input);
                output = Path.GetFullPath(output);
                string compiler = request.GetMetadata("CompilerApiVersion");
                string language = request.GetMetadata("ProjectLanguage");
                if (comparer.Equals(input, output))
                {
                    throw new FormatException($"NuGet input facts cannot replace their assets file: '{input}'.");
                }
                if ((language is "C#" or "VB") && compiler.Length == 0)
                {
                    throw new FormatException($"Missing compiler API identity for '{request.ItemSpec}'.");
                }

                if (outputs.TryGetValue(output, out var previous)
                    && (!comparer.Equals(input, previous.Input) || compiler != previous.Compiler || language != previous.Language))
                {
                    throw new FormatException($"Conflicting NuGet input requests would write '{output}'.");
                }

                outputs[output] = (input, compiler, language);
            }

            foreach (var output in outputs)
            {
                if (outputs.ContainsKey(output.Value.Input))
                {
                    throw new FormatException($"NuGet input facts cannot replace another request's assets file: '{output.Value.Input}'.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or FormatException)
        {
            Log.LogError(Strings.ErrorReadingAssetsFile, ex.Message);
            return;
        }

        foreach (var output in outputs)
        {
            WriteNuGetInputs writer = new(Log)
            {
                BuildEngine = BuildEngine,
                InputFile = output.Value.Input,
                OutputFile = output.Key,
                CapturePackageContext = true,
                CompilerApiVersion = output.Value.Compiler,
                ProjectLanguage = output.Value.Language,
            };
            if (!writer.Execute())
            {
                return;
            }

            WrittenModels++;
        }
    }
}
