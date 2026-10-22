// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using NuGet.ProjectModel;

namespace Microsoft.NET.Build.Tasks;

[MSBuildDeclaredIOTask]
public sealed class ResolveNuGetAssets : TaskBase
{
    [Required]
    public string FormatVersion { get; set; } = null!;
    [Required]
    public string PackageContextVersion { get; set; } = null!;
    public ITaskItem[] Facts { get; set; } = [];
    public ITaskItem[] PackageLocations { get; set; } = [];
    public ITaskItem[] RelatedFiles { get; set; } = [];
    public ITaskItem[] Locales { get; set; } = [];
    [Required]
    public string TargetFramework { get; set; } = null!;
    [Required]
    public string ProjectPath { get; set; } = null!;
    public string ProjectAssetsFile { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public string ProjectLanguage { get; set; } = "";
    public string CompilerApiVersion { get; set; } = "";
    public string PlatformLibraryName { get; set; } = "";
    public bool IsSelfContained { get; set; }
    public bool MarkPackageReferencesAsExternallyResolved { get; set; }
    public bool DisableFrameworkAssemblies { get; set; }
    public bool CopyLocalRuntimeTargetAssets { get; set; }
    public bool DisableTransitiveProjectReferences { get; set; }
    public bool DisableTransitiveFrameworkReferences { get; set; }
    public bool EnsureRuntimePackageDependencies { get; set; }
    public bool VerifyMatchingImplicitPackageVersion { get; set; }
    public bool EmitAssetsLogMessages { get; set; }
    public ITaskItem[] ExpectedPlatformPackages { get; set; } = [];
    public ITaskItem[] SatelliteResourceLanguages { get; set; } = [];
    public ITaskItem[] PackageReferences { get; set; } = [];
    public ITaskItem[] ExistingAnalyzers { get; set; } = [];
    public string DefaultImplicitPackages { get; set; } = "";

    [Output]
    public ITaskItem[] Analyzers { get; set; } = [];
    [Output]
    public ITaskItem[] CompileTimeAssemblies { get; set; } = [];
    [Output]
    public ITaskItem[] ContentFilesToPreprocess { get; set; } = [];
    [Output]
    public ITaskItem[] DebugSymbolsFiles { get; set; } = [];
    [Output]
    public ITaskItem[] ReferenceDocumentationFiles { get; set; } = [];
    [Output]
    public ITaskItem[] FrameworkAssemblies { get; set; } = [];
    [Output]
    public ITaskItem[] FrameworkReferences { get; set; } = [];
    [Output]
    public ITaskItem[] NativeLibraries { get; set; } = [];
    [Output]
    public ITaskItem[] ResourceAssemblies { get; set; } = [];
    [Output]
    public ITaskItem[] RuntimeAssemblies { get; set; } = [];
    [Output]
    public ITaskItem[] RuntimeTargets { get; set; } = [];
    [Output]
    public ITaskItem[] TransitiveProjectReferences { get; set; } = [];
    [Output]
    public ITaskItem[] PackageFolders { get; set; } = [];
    [Output]
    public ITaskItem[] PackageDependencies { get; set; } = [];
    [Output]
    public ITaskItem[] PackageDependenciesDesignTime { get; set; } = [];
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    protected override void ExecuteCore()
    {
        try
        {
            if (FormatVersion != "1" || PackageContextVersion != "1")
            {
                throw new FormatException("Missing or unsupported prepared NuGet facts/package context.");
            }
            if ((ProjectLanguage is "C#" or "VB") && string.IsNullOrEmpty(CompilerApiVersion))
            {
                throw new FormatException("Missing prepared compiler API version.");
            }
            LockFile model = NuGetAssets.Read(NuGetInput.Read(Facts), ProjectAssetsFile);
            PreparedPackageResolver resolver = new(PackageLocations, RelatedFiles, Locales);
            ResolvePackageAssets selection = new(Log)
            {
                BuildEngine = BuildEngine,
                TargetFramework = TargetFramework,
                RuntimeIdentifier = RuntimeIdentifier,
                ProjectPath = ProjectPath,
                ProjectLanguage = ProjectLanguage,
                CompilerApiVersion = CompilerApiVersion,
                PlatformLibraryName = PlatformLibraryName,
                IsSelfContained = IsSelfContained,
                MarkPackageReferencesAsExternallyResolved = MarkPackageReferencesAsExternallyResolved,
                DisableFrameworkAssemblies = DisableFrameworkAssemblies,
                CopyLocalRuntimeTargetAssets = CopyLocalRuntimeTargetAssets,
                DisableTransitiveProjectReferences = DisableTransitiveProjectReferences,
                DisableTransitiveFrameworkReferences = DisableTransitiveFrameworkReferences,
                EnsureRuntimePackageDependencies = EnsureRuntimePackageDependencies,
                VerifyMatchingImplicitPackageVersion = VerifyMatchingImplicitPackageVersion,
                EmitAssetsLogMessages = EmitAssetsLogMessages,
                ExpectedPlatformPackages = ExpectedPlatformPackages,
                SatelliteResourceLanguages = SatelliteResourceLanguages.Length == 0 ? null : SatelliteResourceLanguages,
                PackageReferences = PackageReferences,
                DefaultImplicitPackages = DefaultImplicitPackages,
            };
            selection.ReadPreparedAssets(model, resolver, resolver.FileExists, resolver.NormalizeLocale);
            if (Log.HasLoggedErrors)
            {
                return;
            }

            if (selection.ContentFilesToPreprocess.Length != 0 ||
                selection.FrameworkAssemblies.Length != 0)
            {
                Log.LogError(Strings.UnsupportedNuGetAssetsInput,
                    "content preprocessing or assembly-name framework references", TargetFramework);
                return;
            }
            RejectConflicts(selection.CompileTimeAssemblies, item => Path.GetFileName(item.ItemSpec));
            RejectConflicts(selection.Analyzers.Concat(ExistingAnalyzers), item => Path.GetFileName(item.ItemSpec));
            RejectConflicts(selection.RuntimeAssemblies.Concat(selection.NativeLibraries)
                .Concat(selection.ResourceAssemblies).Concat(selection.RuntimeTargets), item =>
                item.GetMetadata("DestinationSubDirectory") + Path.GetFileName(item.ItemSpec));

            Analyzers = selection.Analyzers;
            CompileTimeAssemblies = selection.CompileTimeAssemblies;
            ContentFilesToPreprocess = selection.ContentFilesToPreprocess;
            DebugSymbolsFiles = selection.DebugSymbolsFiles;
            ReferenceDocumentationFiles = selection.ReferenceDocumentationFiles;
            FrameworkAssemblies = selection.FrameworkAssemblies;
            FrameworkReferences = selection.FrameworkReferences;
            NativeLibraries = selection.NativeLibraries;
            ResourceAssemblies = selection.ResourceAssemblies;
            RuntimeAssemblies = selection.RuntimeAssemblies;
            RuntimeTargets = selection.RuntimeTargets;
            TransitiveProjectReferences = selection.TransitiveProjectReferences;
            PackageFolders = selection.PackageFolders;
            PackageDependencies = selection.PackageDependencies;
            PackageDependenciesDesignTime = selection.PackageDependenciesDesignTime;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            Log.LogError(Strings.InvalidNuGetInputFacts, ex.Message);
        }
    }

    private static void RejectConflicts(IEnumerable<ITaskItem> items, Func<ITaskItem, string> key)
    {
        Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem item in items)
        {
            string identity = key(item);
            if (paths.TryGetValue(identity, out string? previous) && previous != item.ItemSpec)
            {
                throw new BuildErrorException(Strings.UnsupportedNuGetAssetsInput,
                    "package file conflict requiring assembly inspection", identity);
            }
            paths[identity] = item.ItemSpec;
        }
    }
}
