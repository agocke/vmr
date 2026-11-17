// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using Microsoft.DotNet.ApiCompatibility.Logging;
using Microsoft.NET.Build.Tasks;

namespace Microsoft.DotNet.ApiCompat.Task
{
    /// <summary>
    /// ApiCompat's ValidateAssemblies MSBuild frontend.
    /// </summary>
    [MSBuildDeclaredIOTask(SandboxVersion = 1)]
    [MSBuildMultiThreadableTask]
    public class ValidateAssembliesTask : TaskBase, IMultiThreadableTask
    {
        // Important: Keep properties exposed in sync with the CLI frontend.

        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        public ITaskItem[]? DeclaredInputs { get; set; }
        public ITaskItem[]? DeclaredOutputs { get; set; }
        public ITaskItem[] DeclaredReferenceDirectories { get; set; } = [];

        /// <summary>
        /// The assemblies that represent the contract.
        /// </summary>
        [Required]
        public string[]? LeftAssemblies { get; set; }

        /// <summary>
        /// The assemblies that act as the implementation.
        /// </summary>
        [Required]
        public string[]? RightAssemblies { get; set; }

        /// <summary>
        /// The path to the roslyn assemblies that should be loaded.
        /// </summary>
        [Required]
        public string? RoslynAssembliesPath { get; set; }

        /// <summary>
        /// If true, generates a suppression file that contains the api compatibility errors.
        /// </summary>
        public bool GenerateSuppressionFile { get; set; }

        /// <summary>
        /// If true, preserves unnecessary suppressions when re-generating the suppression file.
        /// </summary>
        public bool PreserveUnnecessarySuppressions { get; set; }

        /// <summary>
        /// If true, permits unnecessary suppressions in the suppression file.
        /// </summary>
        public bool PermitUnnecessarySuppressions { get; set; }

        /// <summary>
        /// The path to suppression files. If provided, the suppressions are read and stored.
        /// </summary>
        public string[]? SuppressionFiles { get; set; }

        /// <summary>
        /// The path to the suppression output file that is written to, when <see cref="GenerateSuppressionFile"/> is true.
        /// </summary>
        public string? SuppressionOutputFile { get; set; }

        /// <summary>
        /// A NoWarn string contains the error codes that should be ignored.
        /// </summary>
        public string? NoWarn { get; set; }

        /// <summary>
        /// If true, includes both internal and public API.
        /// </summary>
        public bool RespectInternals { get; set; }

        /// <summary>
        /// Enables rule to check that attributes match.
        /// </summary>
        public bool EnableRuleAttributesMustMatch { get; set; }

        /// <summary>
        /// Set of files with types in DocId format of which attributes to exclude.
        /// </summary>
        public string[]? ExcludeAttributesFiles { get; set; }

        /// <summary>
        /// Enables rule to check that the parameter names between public methods do not change.
        /// </summary>
        public bool EnableRuleCannotChangeParameterName { get; set; }

        /// <summary>
        /// Performs api comparison checks in strict mode.
        /// </summary>
        public bool EnableStrictMode { get; set; }

        /// <summary>
        /// The left assemblies' references. The index in the array maps to the index of the passed in left assembly.
        /// </summary>
        public string[]? LeftAssembliesReferences { get; set; }

        /// <summary>
        /// The right assemblies' references. The index in the array maps to the index of the passed in right assembly.
        /// </summary>
        public string[]? RightAssembliesReferences { get; set; }

        /// <summary>
        /// The path to a semaphore file that acts as the touched output which is useful for incremental invocation of this task.
        /// </summary>
        public string? SemaphoreFile { get; set; }

        /// <summary>
        /// Create dedicated api compatibility checks for each left and right assembly tuples.
        /// </summary>
        public bool CreateWorkItemPerAssembly { get; set; }

        /// <summary>
        /// Regex transformation patterns (regex + replacement string) that transform left assemblies paths to (i.e. relative) paths that can be encoded into the suppression file.
        /// </summary>
        public ITaskItem[]? LeftAssembliesTransformationPattern { get; set; }

        /// <summary>
        /// Regex transformation patterns (regex + replacement string) that transform right assemblies paths to (i.e. relative) paths that can be encoded into the suppression file.
        /// </summary>
        public ITaskItem[]? RightAssembliesTransformationPattern { get; set; }

        public override bool Execute()
        {
            ValidateDeclarations();
            RoslynResolver roslynResolver = RoslynResolver.Register(TaskEnvironment.GetExecutionDirectory(RoslynAssembliesPath!).Value);
            try
            {
                return base.Execute();
            }
            finally
            {
                roslynResolver.Unregister();
            }
        }

        protected override void ExecuteCore()
        {
            if (LeftAssemblies is null) throw new ArgumentNullException(nameof(LeftAssemblies));
            if (RightAssemblies is null) throw new ArgumentNullException(nameof(RightAssemblies));

            HashSet<string> referenceDirectories = new(
                DeclaredReferenceDirectories.Select(item => FullPath(item.ItemSpec)),
                ApiCompatInputPaths.Comparer);
            SuppressibleMSBuildLog logFactory(ISuppressionEngine suppressionEngine) => new(Log, suppressionEngine, NoWarn);
            ValidateAssemblies.Run(logFactory,
                new ValidateAssembliesOptions(MapInputs(LeftAssemblies!), MapInputs(RightAssemblies!))
                {
                    GenerateSuppressionFile = GenerateSuppressionFile,
                    PreserveUnnecessarySuppressions = PreserveUnnecessarySuppressions,
                    PermitUnnecessarySuppressions = PermitUnnecessarySuppressions,
                    SuppressionFiles = SuppressionFiles is null ? null : MapInputs(SuppressionFiles),
                    SuppressionOutputFile = SuppressionOutputFile is null ? null : TaskEnvironment.GetOutputPath(SuppressionOutputFile).Value,
                    NoWarn = NoWarn,
                    RespectInternals = RespectInternals,
                    EnableRuleAttributesMustMatch = EnableRuleAttributesMustMatch,
                    ExcludeAttributesFiles = ExcludeAttributesFiles is null ? null : MapInputs(ExcludeAttributesFiles),
                    EnableRuleCannotChangeParameterName = EnableRuleCannotChangeParameterName,
                    EnableStrictMode = EnableStrictMode,
                    LeftAssembliesReferences = MapAssembliesReferences(LeftAssembliesReferences, referenceDirectories),
                    RightAssembliesReferences = MapAssembliesReferences(RightAssembliesReferences, referenceDirectories),
                    ReferenceFilePaths = DeclaredInputs is null ? null : new HashSet<string>(
                        DeclaredInputs.Select(item => TaskEnvironment.GetInputPath(item.ItemSpec).Value), ApiCompatInputPaths.Comparer),
                    CreateWorkItemPerAssembly = CreateWorkItemPerAssembly,
                    LeftAssembliesTransformationPatterns = ParseTransformationPattern(LeftAssembliesTransformationPattern),
                    RightAssembliesTransformationPatterns = ParseTransformationPattern(RightAssembliesTransformationPattern),
                });

            if (SemaphoreFile is not null)
            {
                string semaphoreFile = TaskEnvironment.GetOutputPath(SemaphoreFile).Value;
                if (Log.HasLoggedErrors)
                {
                    // To force incremental builds to show failures again, delete the passed in semaphore file.
                    if (DeclaredInputs is null && File.Exists(semaphoreFile))
                        File.Delete(semaphoreFile);
                }
                else
                {
                    // If ValidateAssemblies was successful, create/update the semaphore file.
                    File.Create(semaphoreFile).Dispose();
                    if (DeclaredInputs is null)
                        File.SetLastWriteTimeUtc(semaphoreFile, DateTime.UtcNow);
                }
            }
        }

        private void ValidateDeclarations()
        {
            if (TaskEnvironment.IsSandboxed && (DeclaredInputs is null || DeclaredOutputs is null))
                throw ApiCompatInputPaths.Invalid(SemaphoreFile ?? nameof(DeclaredInputs));

            if (DeclaredInputs is null && DeclaredOutputs is null)
                return;

            if (DeclaredInputs is null || DeclaredOutputs is not [ITaskItem output] ||
                string.IsNullOrWhiteSpace(SemaphoreFile) || GenerateSuppressionFile ||
                !ApiCompatInputPaths.Comparer.Equals(FullPath(output.ItemSpec), FullPath(SemaphoreFile)))
            {
                throw ApiCompatInputPaths.Invalid(SemaphoreFile ?? nameof(DeclaredOutputs));
            }

            HashSet<string> inputs = new(DeclaredInputs.Select(item => FullPath(item.ItemSpec)),
                ApiCompatInputPaths.Comparer);
            HashSet<string> directories = new(DeclaredReferenceDirectories.Select(item => FullPath(item.ItemSpec)),
                ApiCompatInputPaths.Comparer);
            foreach (string path in (LeftAssemblies ?? []).Concat(RightAssemblies ?? [])
                .Concat(SuppressionFiles ?? []).Concat(ExcludeAttributesFiles ?? [])
                .Concat([Path.Combine(RoslynAssembliesPath!, "Microsoft.CodeAnalysis.dll"),
                    Path.Combine(RoslynAssembliesPath!, "Microsoft.CodeAnalysis.CSharp.dll")]))
            {
                if (path.IndexOfAny(['*', '?']) >= 0 || !inputs.Contains(FullPath(path)))
                    throw ApiCompatInputPaths.Invalid(path);
            }
            foreach (string group in (LeftAssembliesReferences ?? []).Concat(RightAssembliesReferences ?? []))
            {
                foreach (string path in group.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string fullPath = FullPath(path);
                    if (!inputs.Contains(fullPath) && !directories.Contains(fullPath))
                        throw ApiCompatInputPaths.Invalid(path);
                }
            }
        }

        private string FullPath(string path) => Path.TrimEndingDirectorySeparator(TaskEnvironment.GetAbsolutePath(path).Value);

        private string[] MapInputs(string[] paths) =>
            paths.Select(path => TaskEnvironment.GetInputPath(path).Value).ToArray();

        private string[][]? MapAssembliesReferences(string[]? groups, HashSet<string> referenceDirectories)
        {
            if (groups is null || groups.Length == 0)
                return null;

            string[][] result = new string[groups.Length][];
            for (int i = 0; i < groups.Length; i++)
            {
                string[] paths = groups[i].Split(',');
                for (int j = 0; j < paths.Length; j++)
                {
                    if (paths[j].Length == 0)
                        continue;

                    paths[j] = referenceDirectories.Contains(FullPath(paths[j]))
                        ? TaskEnvironment.GetExecutionDirectory(paths[j]).Value
                        : TaskEnvironment.GetInputPath(paths[j]).Value;
                }
                result[i] = paths;
            }

            return result;
        }

        private static (string CaptureGroupPattern, string ReplacementString)[]? ParseTransformationPattern(ITaskItem[]? transformationPatterns)
        {
            const string ReplacementStringMetadataName = "ReplacementString";

            if (transformationPatterns == null)
                return null;

            var patterns = new (string CaptureGroupPattern, string ReplacementPattern)[transformationPatterns.Length];
            for (int i = 0; i < transformationPatterns.Length; i++)
            {
                string captureGroupPattern = transformationPatterns[i].ItemSpec;
                string replacementString = transformationPatterns[i].GetMetadata(ReplacementStringMetadataName);

                if (string.IsNullOrWhiteSpace(replacementString))
                {
                    throw new ArgumentException(string.Format(CommonResources.InvalidRexegStringTransformationPattern,
                        captureGroupPattern,
                        replacementString));
                }

                patterns[i] = (captureGroupPattern, replacementString);
            }

            return patterns;
        }
    }
}
