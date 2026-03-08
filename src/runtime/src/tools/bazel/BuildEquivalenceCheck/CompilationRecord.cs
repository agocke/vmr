// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace BuildEquivalenceCheck;

/// <summary>
/// A normalized record of a single native C/C++ file compilation.
/// </summary>
public sealed class NativeCompilationRecord
{
    public required string SourceFile { get; init; }
    public required string Target { get; init; }
    public required SortedSet<string> Defines { get; init; }
    public required SortedSet<string> Undefines { get; init; }
    public required SortedSet<string> IncludePaths { get; init; }
    public required SortedSet<string> Flags { get; init; }
    public required string LanguageStandard { get; init; }
    public required string OptimizationLevel { get; init; }
    public string BuildSystem { get; init; } = "";
}

/// <summary>
/// A normalized record of a managed C# assembly compilation.
/// </summary>
public sealed class ManagedCompilationRecord
{
    public required string AssemblyName { get; init; }
    public required SortedSet<string> SourceFiles { get; init; }
    /// <summary>
    /// Maps normalized (repo-relative) source paths to their original disk paths,
    /// used for content comparison of generated files.
    /// </summary>
    public Dictionary<string, string> SourceFileOriginalPaths { get; init; } = [];
    public required SortedSet<string> Defines { get; init; }
    public required SortedSet<string> References { get; init; }
    public required SortedSet<string> NoWarn { get; init; }
    public required SortedSet<string> Analyzers { get; init; }
    public required SortedSet<string> Flags { get; init; }
    public string TargetType { get; init; } = "library";
    public string LangVersion { get; init; } = "";
    public string BuildSystem { get; init; } = "";
    /// <summary>
    /// The output path of the assembly, used to distinguish ref vs impl builds.
    /// </summary>
    public string OutputPath { get; init; } = "";
    /// <summary>
    /// Whether this is a reference assembly build (source in ref/ directory).
    /// </summary>
    public bool IsReferenceAssembly { get; init; }
    /// <summary>
    /// The Bazel target label (e.g. "//src/libraries/System.Runtime:impl_System.Runtime").
    /// Empty for MSBuild records.
    /// </summary>
    public string TargetLabel { get; init; } = "";
}
