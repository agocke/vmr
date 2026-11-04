# Microsoft.DotNet.ApiCompat.Task

MSBuild tasks and targets to perform API compatibility checks on assemblies and packages.

## Getting started

Add a `PackageReference` to this package and the `EnablePackageValidation` and
`ApiCompatValidateAssemblies` features become available on your project. See
[the package validation docs](https://learn.microsoft.com/dotnet/fundamentals/apicompat/package-validation/overview)
for the full list of options.

```xml
<PackageReference Include="Microsoft.DotNet.ApiCompat.Task" Version="..." PrivateAssets="all" />
```

## Prototype action-graph builds

Ordinary Restore with `MSBuildRestoreGraphInputs=true` captures existing reference
directory members and default-suppression existence after compiler-input preparation.
Hardened assembly validation imports those facts, formats its file footprint without
I/O, and schedules the existing comparison with declared assembly, reference,
suppression, exclusion and Roslyn inputs and one success-semaphore output.
Reference resolution preserves explicit-file precedence and cannot discover new
undeclared siblings. Failed validation preserves the prior semaphore.

For source-built reference directories, author `ApiCompatProducedReferenceDirectory`
and explicit `ApiCompatProducedReferenceFile` members from their producers. Members
must be immediate files in a declared directory and have graph output owners.
Preparation never inventories these directories, even if stale files exist.
Selected members carry `RequireProducer=true` in `DeclaredInputs`; graph
publication rejects missing producers even when a stale file exists.
The comparison retains the directory request and its search order, but can read
only the declared members. An unused directory may have no members; a required
undeclared reference fails rather than falling back to an ambient sibling.

This opt-in profile supports exact primary files, prepared external reference
directories and producer-owned reference files, not future output-directory
discovery, primary globs/directories, or suppression generation. Ordinary
assembly/package validation and suppression generation retain their behavior.

## Requirements

This package ships only a .NET (Core) implementation of the MSBuild task. When
the task is invoked from desktop `MSBuild.exe` (i.e. inside Visual Studio), the
task is loaded into a .NET task host using MSBuild's `Runtime="NET"` support.

That support requires:

- **MSBuild 18.0 or later** (Visual Studio 2026 / `MSBuild.exe` 18.0+).
- Equivalently, **.NET SDK 10.0.100 or later**.

If the package is consumed under an older MSBuild / SDK, the task will fail to
load with `MSB4175` ("The task factory ... could not be loaded") or with a
`NETHostTaskLoad_Failed` error from the .NET task host. Upgrade Visual Studio
or the .NET SDK to a supported version to resolve.
