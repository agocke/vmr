// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;

[Generator]
public sealed class ConflictGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
#if WINNER
        const string message = "high";
#else
        const string message = "low";
#endif
        context.AddSource("Generated.cs", $"public static class Generated {{ public const string Message = \"{message}\"; }}");
    }
}
