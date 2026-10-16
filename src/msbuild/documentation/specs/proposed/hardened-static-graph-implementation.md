# Standalone action-graph prototype roadmap

Implement the [action-graph contract](hardened-static-graph.md) in small,
verifiable slices. MSBuild emits concrete work; Ninja schedules independent
hosts. This roadmap describes planned work, not implemented behavior.
Use the [conversion cookbook](hardened-static-graph-migration.md) for practical
patterns when adapting existing targets and tasks.

## Current foundation

- `HardenedTargetValidator` walks target bodies, tracks batching and
  static/deferred values, and constructs DeclaredIO invocations through a callback.
- `HardenedTaskClassificationResolver` resolves implementation annotations.
  `HardenedDeclaredIOFootprint` records paths, not executable actions.
- In hardened mode, `RequestBuilder` requires graph/host configuration,
  publishes the constructed bundle, and returns without ordinary `BuildTargets`.
  Restore is a separate ordinary invocation, not a hardened-mode exception.
- `TaskExecutionHost` depends on project, batching, and engine logging state.
- The VMR-level `genbuild` app assembles the graph compiler's toolset without
  producing an SDK redist or bundling a .NET runtime.
- `MSBuild.ActionTaskHost` executes a hand-authored JSON invocation in a fresh
  process without the engine. It binds scalars, arrays, and items with custom
  metadata; applies explicit context; and checks required declared files.
- The construction callback now uses normal parameter binding, records nonempty
  DeclaredIO invocations, and executes zero-I/O invocations with output mappings.
  Process tests prove a generated Ninja producer/consumer chain, batching,
  unchanged regeneration, invalidation, and failure propagation.

Reuse these pieces without introducing another target interpreter. Keep
ordinary builds unchanged outside the opt-in hardened graph-generation path.

## VMR integration

Cross-component prototype work lives together under the VMR's `src/msbuild`,
`src/sdk`, `src/sourcelink`, and `src/roslyn` directories. NuGet integration
lives in `src/nuget-client`: the standalone app stages the local NuGet targets
while retaining the pinned NuGet managed dependency closure.
The integration baseline is VMR commit
`baa2aa16da04556ac274562798d2fbd7cfe7bc9a`.

Only the following prototype deltas were imported, not the intervening
upstream history or dependency updates:

| Component | Excluded source base | Included source tip |
| --- | --- | --- |
| MSBuild | `1eecb3d3833b5db76c03e1407fa057d497206821` | `99f6af456bb04a5fb166d09ce19b2602fbdeec25` |
| SDK | `fcd632a06320edcb05ac62f20e150da48b00b6a8` | `03a7cdcd0a1bbc4b9b9c0753ef64e1cb7aca9b3e` |
| SourceLink | `6ec19dda0e195435884deb056354390cfa0c0cf7` | `d9f4f778c5e0acf2c634ea1c5c8e2fe1729bb2a5` |
| Roslyn | `660df145e35572c84bef325bf7d846aefef0500b` | `873218be8df748732cd64f7eeeafd836c93e8586` |

The SDK delta includes explicit project-relative apphost normalization.
Roslyn's analyzer-config generator retains this baseline's style
selection rules while adding declared package assets for hardened construction.
The source manifest remains the baseline provenance; it is not rewritten to
claim that these local changes have flowed from the component repositories.

### Standalone genbuild app

The VMR is the coordinated source workspace, not the product's distribution
format. `genbuild/genbuild.proj` at the VMR root builds just the required
component projects and assembles a framework-dependent app in
`artifacts/bin/genbuild/Debug`. It does not build the SDK redist, templates,
workloads, CLI driver, or a runtime. The old composite-SDK script is no longer
the integration entry point.

With the MSBuild component's pinned .NET 11 SDK on `PATH`, build from
`src/msbuild` so the CLI uses that component's SDK selection:

```sh
dotnet msbuild ../../genbuild/genbuild.proj -m:1 -nr:false -bl:{}
```

`GenBuildBootstrapSdk` and `GenBuildDotNetHost` can specify explicit build-time
SDK and host paths. `GenBuildSkipComponentBuild=true` reassembles already-built
components during iteration; the default builds them from this VMR.

Run the resulting app with an external .NET 11 runtime:

```sh
dotnet artifacts/bin/genbuild/Debug/genbuild.dll restore path/Project.csproj -bl:{}
dotnet artifacts/bin/genbuild/Debug/genbuild.dll build path/Project.csproj -bl:{}
ninja -C path -f obj/genbuild/build.ninja
```

`restore` is unrestricted ordinary Restore with
`MSBuildRestoreGraphInputs=true`. `build` starts a separate hardened MSBuild
process and emits `obj/genbuild/build.ninja`; it does not execute the actions.
Additional arguments are forwarded to MSBuild, including an explicit
`MSBuildEmitActionGraph` property to change the output location.

The app carries locally-built engine/common targets, SDK targets/tasks,
SourceLink, Roslyn tasks, analyzer manifests, and the independent task host.
NuGet's managed dependency closure and the compiler are restored at versions
from `src/sdk/eng/Version.Details.props`. The additional dependency manifest
makes NuGet available without the SDK driver's runtime manifest.

The current .NET 11 framework definitions and RID/pruning metadata are
build-time inputs from the selected bootstrap SDK. Pruning data for its current
framework comes from the matching reference-pack package; only metadata, not
the pack's assemblies, is included in the app. Project reference packs are
obtained during Restore, not rediscovered from an installed SDK at runtime.
Workload resolution is explicitly disabled for this prototype profile.

`genbuild/tests/Smoke.proj` exercises CLI error propagation, paths containing
spaces, graph-only construction, independent Ninja execution, and SDK Restore
capture. It also invokes the packaged compiler directly with the captured
reference assemblies and runs the resulting Hello World program. This compiler
check does not depend on graph support for Roslyn's `Csc` task.
Set `GenBuildTestHost` to a runtime-only installation to verify there is no SDK
dependency at runtime.

The app layout does not make all SDK targets graph-compatible. NuGet's
`_GetRestoreProjectStyle` target now classifies the supported `PackageReference`
profile from explicit project inputs without executing its discovery task.
Other styles or inputs requiring `packages.config` discovery fail during
construction; ordinary Restore retains its existing behavior. SDK duplicate-item
validation is an audited zero-I/O task. The smoke fixture imports the shipped
NuGet and SDK validation targets, covers explicit and item-implied styles in the
presence of both packages.config layouts, and rejects unsupported styles and
duplicate items without publishing a graph or executing actions.

Full SDK Build still needs further task adaptations, including `Csc`;
unsupported construction fails rather than falling back to ordinary execution.

## 1. Prove an engine-free task host

Start with a hand-authored invocation manifest and `WriteLinesToFile`.
Implement a reusable console host that loads an exact managed task, binds
supported parameters, reconstructs item metadata, and sets explicit working
directory/environment. Supply a small `IBuildEngine` compatibility layer for
supported diagnostics and services; reject nested builds and unsupported
hosting. Reuse small binding helpers, not the whole `TaskExecutionHost`.

**Done when:** a fresh process produces the expected file without loading
`Microsoft.Build`. False results, exceptions, logged errors, and missing
required outputs fail with useful diagnostics. Parameter checks cover item
order, metadata, escaping, and unset versus empty values.

This first slice is implemented with process-level tests.

## 2. Unify construction-time tasks under Declared-IO

Migrate participating tasks and target invocations from the separate Pure
contract to explicit `DeclaredInputs` and `DeclaredOutputs`. Empty lists permit
construction-time execution with known parameters and ordinary `<Output>`
mappings. Nonempty footprints remain unexecuted actions and cannot publish
MSBuild return values. Retire the branch-only Pure attribute, interface, and
environment; reuse `TaskEnvironment` rather than a second context abstraction.

**Done when:** a zero-I/O task's returned items and metadata can construct a
later invocation, while missing declarations, unavailable parameters, and
file-I/O output mappings fail before action side effects.

The existing construction-time task catalog now uses DeclaredIO and ordinary
task context. Hardened mode has no validator-only execution path. The broader
SDK overlays remain experimental and require graph-compatible adaptation.

## 3. Emit a concrete Ninja graph

Extend the existing partial-evaluation walk to collect actions per concrete
task bucket. Capture bound parameters, configured-project identity, declared
paths, implementation/host context, and diagnostic provenance. Associate
generated inputs with producers and preserve required file/control ordering.
Expand supported `MSBuild` and `CallTarget` intrinsics during construction.

Write versioned invocation manifests and one Ninja edge per action. Include
manifests and the host/tool dependency closure as normal inputs; use order-only
edges only for control precedence. Handle Ninja escaping separately from shell
quoting, write manifests only on content changes, and emit explicit roots.
Record graph inputs, but require explicit regeneration after they change.

Add a graph-only path in `RequestBuilder` that publishes the complete bundle
and returns without ordinary `BuildTargets`. Enforce the spec's required-file,
static-value, and fail-fast subset; do not emulate unsupported effects with
stamps or fall back to ordinary execution.

**Done when:** clean-output generation emits all actions without running any
file-I/O task. Invalid construction publishes no executable partial graph.
Focused cases cover buckets, generated inputs, conflicting writes, cycles,
paths with spaces/dollars/colons, and supported cross-project routing.

The first single-project Unix slice is implemented. Cross-project `MSBuild`
routing, forward producer resolution, Windows commands, full tool closures,
and graph-input inventories remain work rather than supported fallbacks.
Provably empty `MSBuild` project lists are supported as the leaf-project base
case; they do not require cross-project routing or emit an action.

## 4. Execute a small build with Ninja

Use a repository `.proj` fixture containing a zero-I/O value transformation,
a file producer, and a consuming file-transform action. Begin with conservative
ordering and `ninja -j1`.

**Done when:**

- After MSBuild exits, Ninja alone produces the expected files, with one host
  process per action and no engine loading, reevaluation, or nested builds.
- Parameters, metadata, and outputs match ordinary execution of the fixture.
- An unchanged second run is a no-op. Input, manifest, and host/tool changes
  invalidate the correct actions under Ninja's timestamp rules.
- Task failure blocks dependents; unsupported constructs fail during generation.

Then test parallel execution where modeled dependencies permit it.

The process fixture covers these behaviors with conservative action ordering.

## 5. Compile with the pinned SDK

The explicit compiler fixture is implemented in
`src/Samples/ActionGraphHelloWorld/HelloWorld.proj`. It uses the test assembly's
audited `GraphCSharpCompileTask` to invoke the real `csc.dll` with explicit
sources, references, compiler/runtime files, and one assembly output. A second
standalone action writes the runtime configuration. MSBuild emits both actions
without compiling; Ninja produces a runnable `HelloWorld.dll`.

Process tests cover `Hello, World!`, a no-op second Ninja run, recompilation
after a source edit without regenerating the graph, and a genuine compiler
error blocking the runtime-config action. The compiler adapter rejects missing
declarations for its explicit inputs/output and refuses execution with the
MSBuild engine loaded. It is a fixture, not a general Roslyn task replacement.

After a Release repository build, supply the installed .NET 11 runtime and
reference-pack versions explicitly:

```sh
source artifacts/sdk-build-env.sh
sdk_version="$(dotnet --version)"
runtime_version="<installed .NET 11 runtime version>"
reference_version="<installed .NET 11 reference-pack version>"

dotnet msbuild src/Samples/ActionGraphHelloWorld/HelloWorld.proj --hardened-graph \
  -p:MSBuildEmitActionGraph="$PWD/artifacts/hello-world/build.ninja" \
  -p:MSBuildActionTaskHost="$PWD/artifacts/bin/MSBuild.ActionTaskHost/Release/net11.0/MSBuild.ActionTaskHost.dll" \
  -p:MSBuildActionDotNetHost="$DOTNET_ROOT/dotnet" \
  -p:CompilerDirectory="$DOTNET_ROOT/sdk/$sdk_version/Roslyn/bincore" \
  -p:ReferenceAssemblyDirectory="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Ref/$reference_version/ref/net11.0" \
  -p:RuntimeDirectory="$DOTNET_ROOT/shared/Microsoft.NETCore.App/$runtime_version" \
  -p:RuntimeVersion="$runtime_version"
ninja -C artifacts/hello-world -f build.ninja -j1
dotnet artifacts/hello-world/HelloWorld.dll
```

This bypasses SDK `Build` and reference-resolution targets. The neighboring
`HelloWorld.csproj` records the SDK integration trial: ordinary restore works,
and workload `IsOSPlatform` checks now use the captured global
`MSBuildGraphHostOS` rather than ambient OS queries. Process tests cover the
default, explicit overrides, immutable configuration, and ordinary-build
compatibility. The selected OS is recorded in the Ninja graph; see
[captured construction platform](hardened-static-graph.md#captured-construction-platform).

Read-only array indexing is now supported during evaluation and target
construction, including Roslyn's language-version expression
`$(_TargetFrameworkVersionWithoutV.Split('.')[0])`. Process cases cover
computed indices, chained string operations, character arrays, invalid
indices, and rejection of unapproved array methods.

Platform SDK discovery is now a
[Restore-stage input](hardened-static-graph.md#restore-time-platform-sdk-discovery),
not another pure-function allowance:

```sh
dotnet restore src/Samples/ActionGraphHelloWorld/HelloWorld.csproj \
  -p:MSBuildRestoreGraphInputs=true
```

`Microsoft.Common.PlatformSDK.targets` preserves the shared property logic
while substituting saved results only for machine searches and path probes.
The import records absent SDKs and validates discovery inputs; it does not
freeze final SDK properties. Process tests cover both metadata layouts,
construction after removing the discovered directories, independently
changing pure overrides, missing/stale imports, and per-framework Restore.
Writer tests cover literal escaping and unchanged-file timestamps.

The prototype deliberately uses unrestricted ordinary Restore as its trusted
preparation stage. `MSBuildRestoreGraphInputs=true` captures SDK discovery,
the prior clean ledger, unmanaged-registration-cache presence, framework
references, and SourceLink repository information, and initializes Roslyn's
analyzer semaphore. Preparation runs during ordinary Restore rather than
depending on hardened mode; cache, repository, and analyzer hooks also
participate in each framework's restore-graph traversal. SDK process
cases cover opt-in/off behavior, empty and populated caches, unchanged
snapshot timestamps, and single/multi-targeting without downloading packages.
Hardened Restore graph construction remains deferred; hardened Build evaluation
still has no ambient-discovery fallback.

Framework and repository snapshots are not replayed during ordinary
umbrella-flag Restore, even when legacy manifest switches are also enabled.
They live outside Common's automatic project-extension import wildcards.
Direct Restore does not require `MSBuildIsRestoring=true`. Focused SDK,
SourceLink, and Roslyn cases cover capture opt-in/off, fresh capture over stale
snapshots, unchanged timestamps, and replay without rediscovery or graph-time
analyzer-marker initialization.

Hardened `TargetDir` computation now uses
`MSBuild.NormalizePath(MSBuildProjectDirectory, OutDir)` with an explicit
absolute base instead of `System.IO.Path.GetFullPath`. It preserves escaping
and trailing separators; ordinary builds retain the previous expression.
Process cases use the shipped property declarations and cover relative and
absolute output directories, parent traversal, quotes, dollars, and literal
percent/semicolon characters.

The hardened SDK now uses an
[explicit reference-search profile](hardened-static-graph.md#explicit-reference-resolution-sources).
Early Common props disable installation-wide searches, automatic output and
raw-filename fallbacks, and Visual Studio directory-layout discovery. Common
targets skip `GetToolsDirectory32` and the assembly-folder configuration probe;
the intrinsic remains prohibited. A zero-I/O validation task rejects unsafe
explicit search overrides before reference-resolution dependencies run.
Process tests exercise shipped Common and SDK search declarations, present
and absent assembly-folder configuration, explicit search roots, rejection
before graph publication, and unchanged ordinary-build search sources.

A fresh ordinary Restore followed by hardened Build now passes platform
discovery, `TargetDir` computation, both cache imports, and assembly-folder
initialization. The SDK omits optional target-framework telemetry and the
preview-SDK banner during hardened graph construction because neither
produces build artifacts; ordinary Restore and Build retain their behavior.
The coordinated SDK's ordinary Restore prepares all six imports/marker
inputs, and fresh hardened Build gets past snapshot replay.
It also gets past NuGet classification and SDK duplicate-item validation.
Explicit fail-fast `ContinueOnError` values (including `false` and
`ErrorAndStop`) are accepted; continue-on-error modes and active `OnError`
handlers remain unsupported.

`Message` is now an audited zero-I/O task, with explicit empty declarations at
Common's call sites. Ordinary and critical messages are logged during graph
construction, not emitted as Ninja actions; diagnostic file paths are location
metadata, not file inputs. Invalid importance and missing declarations fail
without publishing a graph or executing file actions.

Common's `CoreBuild` now selects an explicit fail-fast graph profile. Its
ordinary failure handlers remain unchanged, but hardened mode neither records
partial writes in the clean-file ledger nor runs failure-time post-build
events. Nonempty post-build events using `Always` or `OnOutputUpdated` fail
with MSB3670. The validator skips only provably disabled `OnError` handlers;
potentially active handlers still prevent graph publication.

The action host now prepares declared file-output parents after validating
the invocation and before executing the task. Common's five directory
preparation sites accumulate zero-I/O requirements instead of running
`MakeDir` during construction. Each requested directory must be covered by a
declared file output beneath it before the graph can be published. Unrelated
empty-directory effects remain unsupported, even if the directory already
exists. There are no directory actions or stamps; ordinary `MakeDir` behavior
is unchanged.

`MSBuild` calls with statically known parameters and provably empty `Projects`
lists now complete during construction without entering a nested build.
Normal binding and output gathering validate their parameters and mappings,
append no output items, clear mapped output properties, and set
`MSBuildLastTaskResult` to `true`. Tests cover both Common
`GetTargetFrameworks` platform-negotiation variants with no references,
ordinary output semantics, and rejection of nonempty, unavailable, missing,
and invalid inputs. The bound-task guard also rejects unaudited custom tasks
named `MSBuild`, even with explicit empty declarations. Nonempty cross-project
expansion remains unsupported.

SDK `ValidateExecutableReferences` now runs as an audited zero-I/O construction
task with explicit empty declarations at its SDK call site. It parses supplied
reference metadata and reads the current request's global-property dictionary,
without opening referenced project files. Executable compatibility checks and
global `SelfContained`/`RuntimeIdentifier` propagation are unchanged. Packaged
smoke checks cover both metadata XML forms, global versus local properties,
specific `NETSDK1150`/`NETSDK1151` errors, and ordinary-build behavior.

The SDK's WinMD target selects a separate audited zero-I/O
`ValidateNoWinMDReferences` task during hardened construction. It checks
case-insensitive `.winmd` filename extensions and reports `NETSDK1130` without
opening any reference files. Ordinary builds retain
`CheckForUnsupportedWinMDReferences`, including its assembly inspection to
refine diagnostics to `NETSDK1149`. Packaged smoke imports the shipped target
to check empty and managed-only lists, lookalike names, multiple WinMD errors,
and unchanged ordinary-mode assembly inspection.

Roslyn's hardened editorconfig target now separates zero-I/O formatting from
overwrite-only file publication. `FormatAnalyzerConfig` shares the ordinary
serializer, resolves paths against the captured project directory, and rejects
filesystem timestamp metadata. `WriteAnalyzerConfig` writes exact UTF-8 text
without a BOM or a prior-output comparison. Ordinary builds retain the original
`GenerateMSBuildEditorConfig` task. Packaged smoke checks construction without
source files, exact bytes including semicolons and embedded newlines, stale
output replacement, retained compiler-input bookkeeping, and explicit metadata
rejection without partial graph publication.

The framework attribute target now selects fail-fast declared-output
publication in hardened mode, leaving ordinary write-race warnings unchanged.
Packaged smoke checks graph-only construction, independent file publication,
compiler-input bookkeeping, and the different failure policies.

SDK `GetAssemblyVersion` now runs as an audited zero-I/O construction task.
It derives a four-part assembly version from the supplied NuGet version string;
it does not inspect an assembly or discover a version. Existing version
overrides and invalid-input diagnostics are unchanged. Packaged smoke checks
prerelease/build metadata, four-part versions, override precedence, static
attribute conditions and metadata, version-dependent output paths, independent
publication, ordinary compatibility, and rejection without graph publication.

With directory import paths supplied explicitly to both Restore and Build,
the fresh SDK full-`Build` probe now gets past `GetTargetFrameworks`,
executable-reference validation, WinMD validation, editorconfig generation,
framework attribute generation, and assembly version derivation.
The first unsupported task is now `Hash` in assembly-info generation;
unavailable hash outputs also cause downstream deferred-value diagnostics.
Failed construction publishes neither a graph nor action invocations and
writes no framework attribute file.
Other diagnostic invocations need explicit declarations as their target
closures are admitted. The SDK's satellite-assembly target also retains an
unadapted `MakeDir`; the structural-directory profile currently covers
Common's preparation sites, not every SDK or custom directory operation.
The probe also reports unsupported source mapping, compilation,
dependency/runtime configuration, copying, and cleanup operations.
Some diagnostics follow unavailable earlier outputs; fix the first unsupported
operations and re-probe rather than treating
all diagnostics as independently reachable blockers.
RAR and the stock Roslyn task still need audited
action-graph adaptation. Full SDK `Build` remains unsupported; ambient
discovery has not been allowed as a fallback. Establish the bounded compile
closure below before broadening to the full `Build` lifecycle.

Use existing SDK overlays for a single-framework C# hello-world profile:

```text
construction -> generated source -> compiler -> final output copy
```

Complete restore/fetch before fresh graph generation. Supply exact compiler,
reference, analyzer, generator, and other tool inputs. Start with a supported
compile closure, then extend toward `Build` without weakening the contract.
Add a two-project fixture with static child returns and configured-project
identity; child artifacts flow through file edges, not nested engine calls.

**Done when:** Ninja builds from clean outputs after MSBuild exits, compilation
and project-reference outputs match ordinary MSBuild, and failures identify
the originating project/task. Broaden platform and parallelism coverage as
those scenarios are admitted.

## 6. Exercise dotnet/runtime

After SDK compilation works, select one bounded managed library under
`~/code/runtime/src/libraries` and construct its compile closure. Complete
restore and prerequisite tool/reference builds beforehand. Use real source
generation and compiler actions, then expand to managed project-reference
closures. Treat native runtime/CMake integration as a separate later boundary,
not a prerequisite for the first managed-library result.

## After the prototype

[Named products](hardened-named-products.md) can add catalog validation,
producer-closure selection, and Ninja roots. Content caching and lazy artifact
materialization need separate standalone integration; Ninja timestamps are
not that cache. Generalized fetch, richer file effects and failure control,
deferred values, relocation, and persistent analysis remain separate work.
