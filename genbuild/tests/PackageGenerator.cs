using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

[Generator(LanguageNames.CSharp)]
public sealed class PackageGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context) =>
        context.AddSource("PackageGenerated.g.cs", SourceText.From(
            "public static class PackageGenerated { public const string Text = \"package generator ran\"; }",
            Encoding.UTF8));
}
