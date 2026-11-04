// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

[Generator(LanguageNames.CSharp)]
public sealed class ConfigurableGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var hints = context.AdditionalTextsProvider
            .Where(static file => Path.GetFileName(file.Path) == "generator.txt")
            .Select(static (file, token) => file.GetText(token)?.ToString().Trim() ?? "none");
        context.RegisterSourceOutput(hints, static (output, hint) =>
        {
            if (hint == "none")
            {
                return;
            }

            output.AddSource(hint, SourceText.From("""
                public static class GeneratedFixture
                {
                    public static string Text => StableFixture.Text;
                    public static string Path() => Capture();
                    private static string Capture(
                        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
                }
                """, Encoding.UTF8));
            output.AddSource("nested/Stable.g.cs", SourceText.From("""
                public static class StableFixture
                {
                    public const string Text = "generated café λ";
                }
                """, Encoding.Unicode));
        });
    }
}
