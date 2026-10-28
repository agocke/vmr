# .NET Libraries

This folder contains the source and tests for the .NET Libraries. Different libraries are owned by different team members; refer to the [Areas](../../docs/area-owners.md#areas) list for lead and owner information.

## Contribution Bar

Some libraries are under more active development than others. Depending on the library's status, expectations for issues and pull requests can vary. Check the library's folder for a `README.md` that declares the contribution bar for that library which consists of a **Primary bar** and optional **Secondary bars**. Regardless of a library's contribution bar, refer to the [DOs and DON'Ts](../../CONTRIBUTING.md#dos-and-donts) and [Suggested Workflow](../../CONTRIBUTING.md#suggested-workflow) in our contribution guidelines before submitting a pull request.

### Assumed bars (unless a library says otherwise)
- **We consider security fixes**
- **We consider test coverage changes**
- **We don't accept style-only changes**

### Primary bar
- **We consider new features, new APIs and performance changes**
  - Both features and fixes will be considered when aligned with current efforts
    - For new APIs, please follow the [API Review Process](../../docs/project/api-review-process.md)
  - Performance gains are welcome. Update [benchmarks](https://github.com/dotnet/performance) as appropriate
  - Refactoring changes are welcome
- **We only consider fixes to maintain or improve quality**
  - New features and APIs are **not** normally accepted but there are exceptions such as when responding to runtime changes or language additions
  - The library is likely mature and feature-complete but may be extended occasionally
  - Performance gains are considered if they are lower-risk or high-impact
  - Refactoring changes are considered if there is a clear benefit
- **We only consider lower-risk or high-impact fixes to maintain or improve quality**
  - New features and APIs are **not** accepted
  - We don't accept potentially destabilizing fixes or test changes unless there is a clear need
- **We only consider fixes that unblock critical issues**
  - New features and APIs are **not** accepted
  - Infrastructure changes and other non-functional changes are considered

### Secondary bars
- **We consider PRs that target this library for new [source code analyzers](../../docs/project/analyzers.md)**
- **We don't accept refactoring changes due to new language features**

## Deployment

Some libraries are included in the .NET SDK as part of the runtime's [shared framework](https://learn.microsoft.com/dotnet/standard/glossary#shared-framework). Other libraries are deployed as out-of-band (OOB) NuGet packages and need to be installed separately.

For more information, see the [Runtime libraries overview](https://learn.microsoft.com/dotnet/standard/runtime-libraries-overview).

## Building and testing

Library tests are driven by `dotnet build /t:test <TestProject>.csproj` (not `dotnet test`), and many useful flags are not visible from `--help`. For up-front baseline build requirements (mandatory under CCA, probe-and-fall-back under CLI), see [`.github/copilot-instructions.md`](../../.github/copilot-instructions.md). See the workflow docs for the rest:

- [Building libraries](../../docs/workflow/building/libraries/README.md)
- [Testing libraries](../../docs/workflow/testing/libraries/testing.md) — includes inner-loop iteration flags such as `/p:testnobuild=true`, `/p:XunitMethodName=...`, `/p:XUnitOptions=...`, and `/p:Outerloop=true`
- [Filtering tests](../../docs/workflow/testing/libraries/filtering-tests.md)
- [Testing on WebAssembly](../../docs/workflow/testing/libraries/testing-wasm.md) · [Android](../../docs/workflow/testing/libraries/testing-android.md) · [Apple platforms](../../docs/workflow/testing/libraries/testing-apple.md)

For changes to `System.Private.CoreLib`, also rebuild the `libs.pretest` subset so the new CoreLib is copied into the testhost before running tests.

### Library products and layouts

[`eng/libraryLayout.targets`](../../eng/libraryLayout.targets) separates product
inventories from layout publication. `GetLibraryBuildProducts` returns
`LibraryBuildProduct` items from the configured outputs, without checking whether
they already exist. `GetLibraryLayout` returns `LibraryLayoutFile` items whose
identities are destination paths and whose `SourcePath` metadata identifies the
input artifact. It selects the compatible inner build, preserves custom item
lists and relative target paths, and rejects conflicting sources for a destination.

These queries do not build or copy the products. Consumers must arrange their
producer dependencies before calling `PublishLibraryLayout`. Existing `BinPlace`
entry points remain compatibility wrappers, so ordinary builds still populate
their established test and pack paths. Consumer-owned aggregate layouts can use
the queries without triggering publication.

The shared-framework source traversal, `sfx-src.proj`, supports an experimental
`UseAggregateLibraryTestHostLayout=true` mode. Pass this property consistently to
restore and build at the aggregate entry point, for example
`./build.sh libs -p:UseAggregateLibraryTestHostLayout=true`. In this mode the
traversal queries `GetLibraryTestHostLayout` on its selected library builds,
deduplicates identical mappings, rejects cross-library destination conflicts,
and publishes the combined `LibraryTestHostFile` inventory. Queries still work
without enabling publication.

Participating shared-framework libraries no longer publish or clean their
testhost mappings individually in this mode. Their other binplace destinations
remain unchanged, including reference and runtime packs. Custom item lists,
such as MsQuic's package-native files, participate in the aggregate too. Ordinary
incremental and explicit cleanup use the aggregate's file-write ledger and leave
unowned files alone; prior library ledgers cannot delete the transferred outputs.
Default builds retain per-library publication.

This owner covers library-produced testhost files, not the entire testhost:
native-library staging and external runtime/host setup remain separate.
In particular, `externals.csproj` still chooses the CoreLib runtime variant;
the aggregate does not merge or replace it.
