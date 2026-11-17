# Standalone action-graph prototype roadmap

MSBuild constructs concrete work; Ninja schedules independent task hosts.
This is the implementation checklist for the
[action-graph contract](hardened-static-graph.md), not a general SDK-support claim.
Checked items describe implemented, exercised behavior. Unchecked items are
outstanding work. Use the [conversion cookbook](hardened-static-graph-migration.md)
for task/target adaptation patterns.

Per-migration validation paragraphs retain their historical preparation counts
and limitations. The configured traversal is now removed as described under
[acquisition-only traversal cutover](#acquisition-only-traversal-cutover); those earlier
counts do not describe the current acquisition path.

## Current foundation

- [x] Emit a concrete Ninja graph without executing its file-producing tasks.
- [x] Run those tasks in engine-free processes after MSBuild exits.
- [x] Build and start a bounded SDK C# executable on an external, SDK-free runtime.
- [x] Build an SDK executable/library closure with generated project references.
- [x] Select prepared package assets, run a package generator, and publish the
  runtime implementation separately from the compile-time reference assembly.
- [x] Evaluate standard directory imports with nearest-file lookup bounded by
  an explicit repository-root global input.
- [ ] Prepare and build a real `dotnet/runtime` managed-library closure.

The packaged smoke fixtures independently execute ten actions for the basic SDK
executable, twenty for the executable/library closure, and eleven for the package
fixture. They check outputs and program startup, not just successful graph emission.
These are bounded profiles, not arbitrary SDK, runtime, platform, or workload builds.

## The model so far

### Restore discovers; construction computes; execution produces

```text
ordinary Restore -> prepared inputs and facts
fresh hardened Build -> build.ninja + concrete invocation manifests
Ninja -> standalone task hosts -> product files
```

Restore is deliberately unrestricted, trusted acquisition. With
`MSBuildRestoreGraphInputs=true`, normal NuGet per-framework visits declare
resource bindings and collect repository and Common cache/platform observations.
After successful acquisition, shared translators publish native resource facts.
Restore does not select a separate configured Build closure, invoke child
Restore/fresh capture, or prebuild products. Fresh Build imports those facts and
restored package authoring, negotiates references and selects configured inputs.
Acquisition must cover the required resource and machine-observation contexts;
it need not repeat the configured graph's project requests.

Fresh hardened Build evaluates projects and constructs the requested target
closure. Engine-managed project/import reads and permitted source globs remain
graph-input discovery; task execution during construction must be zero-I/O.
Externally discovered data becomes explicit items/properties. Pure derivation
over those values stays in Build rather than being frozen as selected task results.

Standard directory-import discovery now uses an evaluator-only capability:
`MSBuildGraphRepositoryRoot` supplies the absolute global boundary for the three
normal directory filenames. Both Restore and fresh Build preserve nearest-file
selection, root-inclusive lookup and no-match semantics without looking outside
that root. The engine does not snapshot evaluated source authoring or broaden
the target-time function allowlist. Unit cases and packaged SDK fixtures cover
layered imports, absent files despite poisoned external parents, boundary/link
rejection, and ordinary evaluation compatibility. Persistent negative-probe
tracking and automatic graph regeneration remain deferred. See the
[contract](hardened-static-graph.md#repository-bounded-directory-imports).

Construction binds concrete parameters, records file actions, validates the
closure, and publishes the bundle without ordinary `BuildTargets` execution.
Ninja later supplies file contents by running the recorded actions. A task must
not execute during construction to discover later parameters, branches, or files.
Unsupported behavior fails explicitly; there is no ordinary-build fallback.

### Values now, file contents later

Both empty `DeclaredInputs` and empty `DeclaredOutputs` permit an audited task
to execute during construction and return ordinary properties/items/metadata.
A nonempty footprint makes the invocation a file action: its parameters and
paths must already be known, and it cannot return execution-time MSBuild values.
Missing declarations are errors, not implicit empty lists.

A generated source path therefore exists as an item before its contents exist.
The producer declares that path as an output; the compiler declares it as an
input. Static `FileWrites` and publication items describe the same known paths
without depending on task execution or old outputs.

The bound `MSBuild` task is a special construction intrinsic, as is `CallTarget`.
It constructs child configured-project subgraphs under the parent's restrictions,
not unrestricted nested builds. `TargetOutputs` carries construction-time target
`Returns` values, including supported legacy `Outputs` returns.

For a project reference, child target construction establishes the implementation,
reference-assembly, and symbol paths. The parent receives those paths and metadata,
feeds the reference assembly into compilation, and publishes the implementation
and symbols through file actions. Ninja executes the dependency's producers first;
Restore and construction do not need the referenced binaries to exist.

### Ownership and determinism

Semantic transformations belong to declared actions, not timestamp-cache wrappers.
Hardened targets retain semantic prerequisites but omit hash stamps, copy markers,
and automatic clean-ledger machinery. Scheduling, content caching, cleaning, and
safe obsolete-output pruning belong to the execution system; Ninja currently
provides scheduling and timestamp-based incrementality and a generated `clean`
target, not all of those services.

Run `ninja -f <graph> clean` separately from builds to remove current declared
outputs. Its host preflights all generated-source trees before any deletion,
using the same receipt validation as materialization without loading compiler
tasks or requiring their inputs/products to exist. It removes receipt-owned
files and empty directory scaffolding bottom-up, then invokes Ninja's ordinary
file cleaner. Unexpected files, links or malformed receipts stop cleaning before
outputs change. Repeated cleaning, missing owned files, empty trees and empty
scaffolding left by raw Ninja cleaning are supported. Graph bundles, prepared
inputs, shared caches and inactive invocation sidecars remain intact. Raw
`ninja -t clean` bypasses this hook and is not the supported tree cleaner.

DeclaredIO asserts deterministic results from explicit data and fixed host context,
not merely a list of paths. C# compilation mandates determinism and stable path
mapping. Relocation checks compare product bytes; concrete graph paths do not make
the graph bundle itself a portable content cache.

Annotations and compiler-plugin contracts are trusted assertions today. Analyzers
and generators are declared tool inputs under their nominal compiler-input-only
contract, without a per-plugin gate. General sandbox enforcement remains deferred.

## VMR integration

Coordinated changes live in `src/msbuild`, `src/sdk`, `src/sourcelink`,
`src/roslyn`, and `src/nuget-client`. The VMR is the source workspace, not the
product distribution. The integration baseline is VMR commit
`baa2aa16da04556ac274562798d2fbd7cfe7bc9a`.

The initial import included only these prototype deltas, not intervening upstream
history or dependency updates:

| Component | Excluded source base | Included source tip |
| --- | --- | --- |
| MSBuild | `1eecb3d3833b5db76c03e1407fa057d497206821` | `99f6af456bb04a5fb166d09ce19b2602fbdeec25` |
| SDK | `fcd632a06320edcb05ac62f20e150da48b00b6a8` | `03a7cdcd0a1bbc4b9b9c0753ef64e1cb7aca9b3e` |
| SourceLink | `6ec19dda0e195435884deb056354390cfa0c0cf7` | `d9f4f778c5e0acf2c634ea1c5c8e2fe1729bb2a5` |
| Roslyn | `660df145e35572c84bef325bf7d846aefef0500b` | `873218be8df748732cd64f7eeeafd836c93e8586` |

The source manifest retains baseline provenance; local prototype changes are not
represented as having flowed from the component repositories.

### Standalone genbuild app

- [x] Assemble a framework-dependent app from selected VMR component projects.
- [x] Carry locally built MSBuild, Common/SDK targets and tasks, Roslyn tasks,
  SourceLink providers, analyzer manifests, and the independent action host.
- [x] Source-build NuGet tasks, SDK resolver, and managed dependencies using
  NuGet's existing .NET 10 target inside the .NET 11 app.
- [x] Exercise the packaged app with a runtime-only host, without an installed SDK.

`genbuild/genbuild.proj` defaults to Release and builds into
`artifacts/bin/genbuild/Release`. This includes the source-built compiler, tasks,
and action host; use `-p:Configuration=Debug` explicitly for debugging. It does not
build SDK redist, templates, workloads, the SDK CLI driver, or a runtime. With
MSBuild's pinned .NET 11 SDK on `PATH`, build from `src/msbuild`:

```sh
dotnet msbuild ../../genbuild/genbuild.proj -m:1 -nr:false -bl:{}
```

`GenBuildBootstrapSdk` and `GenBuildDotNetHost` select explicit build-time paths.
`GenBuildSkipComponentBuild=true` reassembles already-built components; the default
builds them from this VMR. The old composite-SDK script is not the entry point.
Roslyn's compiler tasks reference source-built Utilities, not the cached package:
the component pipeline builds its `netstandard2.0` assembly first and ships the
modern implementation containing `SandboxedToolTask`. Separate compiler-task
builds can override `GenBuildMSBuildUtilitiesAssembly` explicitly.

An optional NativeAOT action host accelerates `CopyFiles` and `WriteLinesToFile`:

```sh
dotnet msbuild ../../genbuild/genbuild.proj -p:GenBuildNativeTaskHost=true -m:1 -nr:false -bl:{}
```

Publishing it requires the platform's NativeAOT compiler/linker prerequisites.
The app and general action host remain framework-dependent; only the specialty
host includes the NativeAOT runtime. The driver discovers the staged native host
and supplies `MSBuildNativeActionTaskHost`; pass that property empty to select
managed-only execution. Direct MSBuild callers can supply its absolute path.

The native executable has a versioned catalog pinned to its binary and the exact
source-built task, Framework, Utilities, and StringTools assemblies. Construction
selects it only for supported task types with matching assembly bytes and no
graph-produced replacement dependencies. Other tasks go directly to the managed
host, without launching a native dispatcher. Native invocations recheck their
assembly identities and share invocation decoding, parameter interpretation,
footprint validation, environment replay, and diagnostics with the managed host.
Identity drift, invalid declarations, and task failures fail without retrying
through another host. Both the native executable and its catalog are Ninja inputs.
Tree materialization and ownership-aware cleaning remain on the managed path.

From the VMR root, run with an external .NET 11 runtime:

```sh
dotnet artifacts/bin/genbuild/Release/genbuild.dll restore path/Project.csproj -p:Configuration=Release -bl:{}
dotnet artifacts/bin/genbuild/Release/genbuild.dll build path/Project.csproj -p:Configuration=Release -bl:{}
ninja -C path -f obj/genbuild/build.ninja
```

`restore` performs ordinary preparation. `build` starts a separate hardened
MSBuild process and emits the graph, not product files. Extra arguments are
forwarded to MSBuild; `MSBuildEmitActionGraph` changes the graph destination.
Explicit directory-import paths and `MSBuildGraphHostOS` must be consistent
between preparation and construction where the profile requires them.
Performance profiles also pass `Configuration=Release` to both invocations, use
fresh intermediate/output directories, and repack local task packages from
Release outputs under new versions.

Compiler/external tool versions remain pinned by `src/sdk/eng/Version.Details.props`.
Framework definitions and RID/pruning metadata come from the selected bootstrap
SDK/reference-pack package at build time. Project reference-pack assemblies are
restored separately, not found through an installed SDK during construction.
Workload resolution is disabled.

`genbuild/tests/Smoke.proj` checks CLI errors, paths containing spaces, Restore
capture, graph-only construction, independent execution, and program startup.
`GenBuildTestHost` selects an SDK-free runtime installation. Its direct packaged
compiler check is separate from the full SDK action-graph execution checks.

- [x] Resolve package SDKs pinned in `global.json` during ordinary Restore through
  the source-built resolver, including an offline local-feed fixture.
- [x] Disable that discovery resolver during hardened Build and fail closed.
- [x] Capture/replay exact package-SDK import bindings before SDK evaluation,
  preserving live props/targets and resolver-supplied values.

The driver supplies an absolute global `MSBuildGraphSdkBindingsDirectory`,
defaulting to the entry project's `obj/genbuild/sdk-bindings`. Preparation saves
versioned XML results per project/solution/request/resolver context; child requests
inherit the same store. Fresh hardened evaluation never invokes SDK plugins:
matching bindings are replayed, with only direct explicit-toolset lookup available
for uncaptured SDKs. Malformed or mismatched facts fail closed.

Focused cases cover ordered paths, result metadata, literal values, replacement,
context separation, invalid input and resolver-free fresh evaluation. The packaged
SDK fixture removes the feed, changes NuGet's package directory and edits the
restored SDK props after preparation. Build emits actions from live SDK authoring;
Ninja executes them separately. Missing bindings and changed SDK requests fail
without publishing a graph. See the
[contract](hardened-static-graph.md#prepared-sdk-imports).

## 1. Prove an engine-free task host

- [x] Load an exact task implementation in `MSBuild.ActionTaskHost` without
  loading the MSBuild engine or another target interpreter.
- [x] Bind scalars, arrays, and items with order, custom metadata, escaping,
  required-parameter checks, and ordinary unset/default semantics.
- [x] Apply explicit project directory, environment, runtime, and host context.
- [x] Support diagnostics through a small `IBuildEngine` compatibility layer;
  reject nested builds and unsupported hosting/services.
- [x] Fail on false results, logged errors, exceptions, missing declared inputs,
  and missing required outputs.

The host receives a concrete versioned invocation, not MSBuild expressions or
serialized engine state. It validates the invocation, then creates declared
output-file parents before executing. Parent creation is scaffolding, not an
owned directory artifact or a success stamp. Collisions fail without deletion.

## 2. Unify construction-time tasks under Declared-IO

- [x] Use `[MSBuildDeclaredIOTask]` and explicit finite `DeclaredInputs` and
  `DeclaredOutputs` at participating call sites.
- [x] Execute the zero-I/O case with ordinary output mappings and task context;
  emit nonempty-footprint invocations without calling `Execute`.
- [x] Retire the separate construction-time Pure contract and validator-only
  execution path; reuse `TaskEnvironment`.
- [x] Reject file-action `<Output>` mappings and unavailable control/parameter
  values before file-action side effects.
- [x] Preserve ordinary task/target selection outside the opt-in hardened path.

`HardenedTargetValidator` schedules ordinary tasks from their declared footprint.
Only zero-I/O invocations use `HardenedTaskClassificationResolver` to check
implementation annotations during construction; nonempty-footprint invocations
are captured before task loading, construction, or typed binding. The standalone
executor audits their implementations and binds their actual parameter types.
The existing `UsingTask` registration is sufficient, including for a future DLL.
The validator reuses the existing construction walk and callback.
`HardenedDeclaredIOFootprint` describes paths; executable actions additionally
capture parameters and implementation/context. No second interpreter is added.

## 3. Emit a concrete Ninja graph

- [x] Capture actions per concrete task bucket, including configured-project
  identity, expanded scalar/item parameters, declarations, host/tool context, and source location.
- [x] Expand supported `MSBuild`/`CallTarget` requests into a shared root graph.
- [x] Preserve configured-target state across metadata queries and Build requests,
  static target returns, property scopes, and repeated/diamond project requests.
- [x] Associate generated inputs with earlier producers; reject cycles,
  conflicting output ownership, and stale generated files treated as sources.
- [x] Publish versioned invocation manifests and a Ninja entry point only after
  successful root construction, without ordinary `BuildTargets`.
- [x] Keep unchanged manifests/entry points unchanged on disk; separate Ninja
  escaping from command quoting.

Each action is one edge containing all its outputs. Declared input contents,
manifests, and host/tool dependencies invalidate work; order-only edges carry
control precedence, not consumed data. External input files must exist at
publication; predecessor-produced inputs need not exist yet.
Task assemblies participate in the same producer validation and Ninja file
dependencies. Generated task directories are not enumerated during construction.
Version 2 invocation manifests preserve both scalar and item interpretations;
version 1 direct-host invocations remain supported.

The implemented emitter uses Unix commands, one construction node, and conservative
ordering. Adjacent host/task files are conservative tool dependencies; tools
elsewhere still need explicit declarations. Graph-input changes require rerunning
MSBuild explicitly; Ninja does not invoke an automatic regeneration rule.

- [ ] Support forward-produced inputs beyond the earlier-producer subset.
- [ ] Add Windows commands and broader construction/parallelism profiles.
- [ ] Complete graph-input inventories and automatic runtime/tool closure support.
- [ ] Add automatic regeneration separately from action execution.

## 4. Execute a small build with Ninja

- [x] Construct a fixture with a zero-I/O value transformation, file producer,
  and consuming transformation, then execute it after MSBuild exits.
- [x] Match ordinary parameters, metadata, and product contents.
- [x] Make an unchanged second run a no-op; invalidate actions after input,
  manifest, or host/tool changes.
- [x] Propagate action failures to dependents and reject unsupported constructs
  during generation.
- [x] Compile/run the explicit
  `src/Samples/ActionGraphHelloWorld/HelloWorld.proj` fixture with the real compiler.

That initial fixture uses audited `GraphCSharpCompileTask`, explicit compiler/
runtime/reference inputs, and a separate runtime-config action. It proves source
invalidation and real compiler errors without SDK Build. The SDK milestones below
now exercise shipped targets and the production compiler adapter instead.

Ninja's incrementality is timestamp/command based, not content-addressed caching.
Timestamp-preserving content changes are not reliably detected by Ninja alone.

## 5. Compile with the pinned SDK

The bounded single-project and executable/library SDK Build profiles are complete.
Restore prepares inputs; fresh construction emits actions from clean outputs;
Ninja independently compiles and publishes runnable programs. The following
checklist replaces the earlier sequence of individual task-rejection probes.

### Preparation and constrained evaluation

- [x] Capture platform SDK discovery, including absence, without freezing later
  pure property overrides; replay only during hardened Build.
- [x] Prepare framework references, unmanaged-registration-cache presence,
  SourceLink repository metadata, and the analyzer semaphore during Restore.
- [x] Participate in configured-project/framework Restore traversal; keep saved
  imports outside Common's automatic project-extension import wildcards.
- [x] Capture construction host OS, process architecture and runtime identifier in
  immutable globals `MSBuildGraphHostOS`, `MSBuildGraphHostArchitecture` and
  `MSBuildGraphHostRuntimeIdentifier`; expose existing OS, architecture and RID
  expressions as reads of that predefined context and include all three
  in graph identity. Normalize `TargetDir` against the explicit project directory
  rather than ambient CWD.
- [x] Support read-only array indexing needed by SDK/Roslyn expressions while
  retaining the property-function allowlist.
- [x] Classify supported NuGet `PackageReference` style from explicit inputs,
  without `packages.config` discovery.

Preparation opt-out, missing/stale inputs, literal escaping, unchanged snapshot
timestamps, fresh recapture, and replay after discovery directories disappear
are covered. Restore does not replay old snapshots as its own discovery results.
Unsupported SDK/import expressions still require adaptation, not allowlisting
ambient I/O. See [platform discovery](hardened-static-graph.md#restore-time-platform-sdk-discovery).

### Static policy and target control

- [x] Admit audited zero-I/O duplicate-item, executable-reference, project-
  configuration, framework-XML, and nearest-framework policy tasks.
- [x] Preserve supplied/global-property executable compatibility and NuGet's
  alias, platform, fallback, and diagnostic semantics.
- [x] Reject `.winmd` paths through hardened `ValidateNoWinMDReferences` without
  opening assemblies; retain ordinary WinMD inspection outside hardened mode.
- [x] Run `Message` during construction with empty declarations, not as an action.
- [x] Accept explicit fail-fast `ContinueOnError` values and skip only provably
  disabled `OnError` handlers; reject active failure-recovery paths.
- [x] Omit optional telemetry/preview banners and select fail-fast `CoreBuild`,
  without changing ordinary cleanup or post-build failure behavior.
- [x] Represent Common's structural directory requirements without `MakeDir`
  actions; require coverage by declared output files.

Nonempty failure-time post-build events (`Always`/`OnOutputUpdated`) fail with
MSB3670. General recovery, unrelated empty directories, and specialized hosting
are not silently dropped. Requested ClickOnce/Appx/native-reference/isolated-COM
discovery remains unsupported; inactive native-manifest placeholders are not probed.

### Formatting and generated sources

- [x] Split editorconfig into pure `FormatAnalyzerConfig` and overwrite-only
  `WriteAnalyzerConfig`, sharing ordinary serialization and rejecting timestamps.
- [x] Derive `AssemblyVersion` from the supplied NuGet version through zero-I/O
  `GetAssemblyVersion`, preserving overrides and invalid-version diagnostics.
- [x] Publish framework/assembly attributes as declared file actions with static
  compiler-input bookkeeping; hardened framework publication is fail-fast.
- [x] Compute source-root mappings and GitHub/GitLab/Azure Repos/Bitbucket URLs
  from prepared metadata, without source-control or source-directory queries.
- [x] Share SourceLink serialization through pure `FormatSourceLink` and
  overwrite-only `WriteSourceLink`.

Generated text matches ordinary bytes where policy is unchanged. Empty SourceLink
mappings clear the compiler property and emit no action; stale JSON remains
unreferenced, not inspected/deleted. Source-generation checks cover escaping,
provider versions, metadata validation, stale replacement, and independent errors.

### Deterministic compiler actions

- [x] Compose pure `FormatCsc` with independent `CompileManaged`, reusing option
  formatting without ordinary Csc's server, IDE host, or discovery paths.
- [x] Compose pure `FormatVbc` with the same executor and shared footprint/path-map
  helper; retain VB options, explicit runtime inputs and VB resource naming.
- [x] Import the packaged compiler/tool catalog directly and select analyzer
  companions from acquired package/framework inventories during graph construction,
  without per-project C#/VB compiler snapshots; keep loose analyzer,
  documentation and module inputs explicit.
- [x] Require deterministic compilation and automatic stable path maps consistent
  with SourceLink, covering project, generated-source, and output paths.
- [x] Declare analyzers/generators, dependencies, configs, and additional files;
  run real generators without an audit allowlist or opt-in gate.
- [x] Retain ToolTask diagnostics, cancellation, and private response-file scratch;
  use a controlled compiler environment and no server or task-local output cache.
- [x] Prove relocated PE/reference-assembly/portable-PDB byte equality and inspect
  PDB documents, CodeView paths, and `CallerFilePath`; cover embedded debug info.

Native PDBs, arbitrary response files, general directory inputs/outputs,
rulesets, SARIF, app-config, machine signing, and execution-mode overrides remain
outside the initial profile. Inputs use explicit paths, not SDK/LIB/default
response-file searches. Plugin I/O enforcement is deferred like other task I/O.

### Opt-in staged tool execution

- [x] Add `SandboxedToolTask : ToolTask`, default off, with exact declared-input
  symlinks, private cwd/scratch, typed path mappers and validated file publication.
- [x] Integrate C#/VB formatters and `CompileManaged` through
  `MSBuildSandboxToolTasks=true`, preserving ordinary arguments and deterministic
  logical paths without rewriting opaque option values or configuration contents.

The initial backend supports Linux on modern .NET. It preserves absolute
directory hierarchy and filenames beneath the execution root, not directory
symlinks or Restore snapshots. All tool-produced outputs validate before publication;
individual files publish atomically, not as one transactional output set. Cancellation,
compiler errors and validation failures do not publish partial tool output.
Analyzer configs are opaque declared file inputs: map their command-line paths,
preserve the directory hierarchy, and do not parse or rewrite their contents.
Config producers must adapt any raw absolute-path selectors or settings to their
execution context. Staged C# compiler-owned trees reuse the declared-root
archive/materializer protocol: map archive writes into staging, retain logical
generated-source paths and their stable aliases, and leave dyndep generation to
the host after successful tool publication. `GetToolOutputs` selects tool-owned
files without removing host-finalized outputs from the declared graph footprint.
VB generated-source archives remain unsupported. Ordinary compiler execution
remains the default.

Staging does not enforce filesystem/network isolation or protect input symlink
targets from writes. Absolute-path escapes and runtime/loader reads remain
possible; OS enforcement is still deferred. Utilities lifecycle tests and
`TestSandboxedCompilers` cover the reusable base and real C#/VB publication,
signing, resource/runtime inputs, output bytes, consumers and failure retention.
`TestSandboxedCompilerGeneratedFiles` applies the existing generated-source
encoding, logical-path, recovery, ownership, empty-tree and failure checks to
staged execution. `TestSandboxedSatelliteSource` checks primary and satellite
staging bindings, producer ordering, ordinary source-byte parity and incremental
resource consumers, including rejection of silent unstaged fallback.
A private managed-libraries rebuild completes all 5,321 Ninja steps at `-j4`,
with all 500 compilers staged, including 143 satellites and CoreLib's generated
tree; a repeat reports no work. Bounded compiler traces confirm mapped paths,
not a complete staged-mode read audit or OS confinement. See
[staged execution usage](../../../../../genbuild/README.md#opt-in-staged-compiler-execution).

### Action-host staging migration

The compiler-only results above use a frozen toolset predating this cutover.
The newer host stages all admitted actions through a shared `FileStagingContext`,
including the native fast path; it does not infer path semantics from task
strings or metadata. `TaskEnvironment.GetAbsolutePath` remains logical.
`GetInputPath`/`GetOutputPath` resolve physical paths at I/O boundaries and check
the existing declared footprint. Ordinary execution retains identity resolution.

- [x] Share staging/publication/cleanup with the opt-in tool adapter.
- [x] Add explicit task-environment input/output path resolution.
- [x] Migrate `CopyFiles` and overwrite-only staged `WriteLinesToFile`.
- [x] Reject unmigrated contracts at host admission rather than execute unstaged.
- [ ] Migrate compiler typed bindings and host-owned generated-tree finalization.
- [ ] Migrate the remaining runtime task implementations and repeat full-profile
  execution/read validation with a new toolset.

The initial host contract requires `SandboxVersion=1` on the declared-I/O marker
and `IMultiThreadableTask`, not `ToolTask` inheritance. Staged line writing rejects
append and prior-output incremental checking; old output bytes are not inputs.
Opaque contents and logical identities are not rewritten. Unsupported contracts
and generated trees currently fail explicitly, so the newer host is not yet a
replacement for the frozen full-libraries toolset. This remains Linux symlink
staging, not OS confinement or a portable-cache proof.

### Compiler-owned generated-source trees

- [x] Preserve logical generator paths and ordinary source encodings while the
  source-built C# compiler persists an ordinal-sorted, fixed-timestamp ZIP.
- [x] Have `FormatCsc` declare a single private tree with archive, dyndep and
  receipt paths, and validate static tree metadata and exclusive ownership.
- [x] Write dyndep only after successful compilation; lower an independent
  materializer edge whose dynamic outputs include all generated files and the
  root directory, including empty trees.
- [x] Recover deleted members without recompilation and prune only prior
  receipt-owned files, including file/directory hint transitions.
- [x] Reject traversal, duplicate/conflicting entries, symlinks, malformed
  receipts and unowned contents without replacing the published tree.
- [x] Verify ordinary source-byte/encoding, mapped `CallerFilePath` and PDB
  document parity, deterministic archive bytes, no-work rebuilds and failed
  compilation preserving published source bytes/timestamps.

This is the single-tree `CompileManaged` profile, not general directory support,
construction-time enumeration or generated-item discovery. Publication updates
individual files atomically, not the whole tree in one transaction. The receipt
is required to authorize replacement/deletion of existing files. Ninja-created
empty parent directories are scaffolding, not ownership of arbitrary files.

### Dependency and runtime manifests

- [x] Use hardened `WriteDepsFile`, sharing the ordinary dependency model/writer
  without object/timestamp caches, ambient roots, or project-output fallback probes.
- [x] Read runtime binary versions after their producers; use managed metadata
  and native PE version resources portably, not OS-specific `FileVersionInfo`.
- [x] Preserve renamed project outputs, satellites, resolved destinations, and
  relative logical manifest paths with static `FileWrites`.
- [x] Use `WriteRuntimeConfig` with the shared framework/template/host-option model,
  declared assets/template reads, and exact main/optional dev outputs.
- [x] Preserve Debug Hot Reload dev configuration; validate both documents before
  writing either, with UTF-8/no-BOM and LF output.

Versionless/other native formats use `0.0.0.0`; missing files and malformed or
conflicting versions fail. Optional default-template presence is selected by fresh
engine evaluation, not frozen by Restore. Explicit/generated templates are required
inputs with producer edges; no task-side missing-file fallback exists.
Relative probing paths are normalized; absolute/invalid probes fail rather than
embedding machine-specific package roots. Multi-file writes are not transactional.
Ordinary Build/Publish/designer behavior stays on its original path.

These actions intentionally read declared NuGet JSON at execution. Their
framework/dependency-model selection affects output contents, not graph shape,
and does not need to move into construction or Restore. The complete assets,
optional template/runtime-graph JSON and versioned binary inputs must suffice;
package/settings discovery is not an implicit part of execution.

### Project references and publication

- [x] Return child paths/metadata through intrinsic requests with ordinary binding;
  absent/empty `Targets` preserves default-target returns.
- [x] Preserve configured identity, framework selection, metadata defaults,
  sequential new-item assignments, and ordinary existing-item update semantics.
- [x] Resolve explicit paths with pure `ResolveReferencePaths`, without assembly
  reads, existence probes, name lookup, version unification, or dependency discovery.
- [x] Supply absolute child `ReferenceAssembly`/`DebugSymbolsFile` metadata;
  compile against references and publish implementations/symbols.
- [x] Publish main/reference/PDB and copy-local files with content-only `CopyFiles`;
  derive `MainAssembly`, `ReferenceAssembly`, `FileWrites`, and shareable paths statically.
- [x] Preserve private/framework/copy-local metadata, destination subdirectories,
  source metadata, and product/symbol/common-output opt-outs.

Simple/display names fail even with `HintPath`; permitted search settings do not
enable lookup. Callers supply the complete reference closure. Missing requested
projects fail through intrinsic requests, not existence-based skipping.
Copy requires exact vectors/declarations, unique outputs disjoint from inputs,
and no old-output, timestamp/MVID, link, retry, or attribute-propagation behavior.
Explicit unsupported copy modes fail. Literal percent escapes survive path and
metadata forwarding. DLL/PDB producer bytes and ordinary deps JSON are checked.

### Incrementality, cleanup, and semantic fidelity

- [x] Omit assembly-info/compile/runtime-config hash caches and copy-up-to-date
  markers while retaining their semantic prerequisites and caller inputs.
- [x] Omit automatic incremental cleanup and clean-ledger capture/replay/updates;
  leave stale/unowned files untouched and keep product bookkeeping available.
- [x] Preserve target-time property escaping until normal binding, including
  literal semicolons/percent text and chained assignments.
- [x] Preserve omitted/empty scalar constructor defaults during action capture;
  reject empty required scalars as ordinary MSBuild does.
- [x] Compare ordinary/independent generated source and default main/dev JSON,
  including absence of an invalid empty `rollForward`, and start the SDK program.

Stale/malformed/obstructing caches and ledgers do not become construction inputs.
Cleaning and safe obsolete-output pruning are not implemented. Explicit
Clean/Rebuild and arbitrary semantic Delete remain unsupported.

- [ ] Broaden compiler, reference, dependency, platform, and SDK Build profiles.
- [ ] Adapt other Copy families and satellite-target directory preparation.
- [ ] Admit other diagnostic call sites only with explicit audited declarations.

## 6. Exercise dotnet/runtime

The initial product milestone is `System.Collections.Immutable` with
`TargetFramework=net10.0` and `BuildTargetFramework=net10.0`. This is runtime's
`NetCoreAppMinimum`; `NetCoreAppPrevious` is empty in this checkout.
An isolated `ArtifactsDir` separates the experiment from existing runtime products.
The real closure includes its matching reference project and ILLink tool/analyzer
projects; choosing the older framework does not make those dependencies optional.

- [x] Select the bounded managed-library profile and isolate its artifacts.
- [x] Get ordinary Restore through pinned Arcade package-SDK resolution.
- [x] Implement raw NuGet facts, pure views, and bounded SDK package selection.
- [x] Prepare selected configured inner builds despite retained `TargetFrameworks`,
  preserving child framework negotiation and per-configuration inputs.
- [x] Capture/replay package-SDK import bindings without Build-time searches/downloads.
- [x] Declare runtime's local repository root before Arcade imports, avoiding layout discovery.
- [x] Normalize Arcade output/intermediate base paths through MSBuild intrinsics,
  preserving ordinary layout and explicit overrides.
- [x] Import upstream workload-version deferral to avoid unrelated JSON reads
  during ordinary library evaluation.
- [x] Scope WebAssembly cache discovery to WebAssembly targets and explicit
  host-side tool consumers, rather than every managed library.
- [x] Evaluate runtime's unchanged RID calculation against captured construction
  context instead of a live `RuntimeInformation.RuntimeIdentifier` lookup.
- [x] Normalize runtime's generator-directory parent path from the absolute
  project directory, preserving generator/source classification.
- [x] Capture the configured framework-discovery requests reached by reference
  negotiation, retaining required prepared-fact imports for those evaluations.
- [x] Separate Arcade P2P runtime-graph acquisition from construction-time
  framework selection, retaining NuGet's runtime/platform fallback policy.
- [ ] Replace workload-version file reads with explicit inputs before constructing
  workload actions.
- [x] Reprobe the real closure and identify the live-tool prebuild dependency.
- [x] Separate targeting-pack discovery from runtime reference filtering,
  retaining candidate analyzers and tool producer dependencies.
- [x] Give runtime library compilation and trimming distinct assembly/symbol
  outputs without moving or overwriting compiler outputs.
- [x] Prepare explicit restored assembly references separately from pack facts,
  leaving path-only resolution and tool producer dependencies intact.
- [x] Capture conflict-policy facts and implement bounded graph-time framework/package
  selection, including package-over-framework replacement and the reverse.
- [ ] Broaden package/package conflict support and generated-reference version contracts.
- [x] Remove framework-reference cardinality/identity restrictions; support ordinary
  package-backed `netstandard2.0` and multiple selected targeting packs.
- [ ] Support the actual compiler/API-tool and analyzer requirements,
  keeping generated tools connected to their producers.
- [x] Construct a fresh real-library graph without product binaries.
- [x] Execute independently with Ninja and verify final DLL/PDB publication.
- [ ] Verify real-library output/ordinary compatibility.
- [ ] Add focused library behavior checks with their genuine test prerequisites.

### Acquisition-only traversal cutover

Configured acquisition and Build selection are exercised by the
[packaged fixture](../../../../../genbuild/tests/ConfiguredPreparation.csproj):
a Debug/net11 parent selects its Release/net10 child while retaining both projects'
`TargetFrameworks`. Separate Debug/net10 and Release/net11 acquisitions preserve
shared framework/NuGet facts and configuration-specific authoring. The resulting parent/child graph
compiles independently and starts without prebuilt project binaries.
Outer cross-targeting Restore succeeds without configured selection or snapshots.
The acquisition guard still rejects `PrepareForBuild`/`CoreCompile`.
The fixture checks the selected child's framework, configuration and
additional/removed reference properties during fresh Build, not during capture.

The configured preparation closure, nested Restore/capture submissions,
recursive `PrepareProjectReferences` preparation and framework-discovery capture
are removed. Ordinary NuGet acquisition traversal remains, preserving effective
request properties and per-framework resource bindings. Source-control and
Common cache/platform producers keep their existing acquisition entry points.
Fresh Build performs framework metadata and reference negotiation against
required facts, without optional-import fallback or replacement preparation walk.
Common/source-control observations still distinguish
`Configuration/Platform/TargetFramework`; acquisition must cover those contexts.
Shared framework/package resources do not require per-configuration snapshots.

The isolated runtime-library acquisition now records 741 evaluations and 6,742
project requests, down from 4,898 and 69,562. There is one root `Restore`, with
zero `PrepareProjectReferences`, `GetTargetFrameworks`, configured preparation,
capture or framework-discovery capture executions during acquisition. Normal
NuGet per-framework visits decrease from 1,934 to 645; the four shared resource
processors still execute once each. These are structural counts, not matched
elapsed-time measurements.
Fresh graph construction preserves all 5,320 action/output signatures, 500 compiler
commands, 55 ApiCompat input sets, 27,257 Ninja nodes and 277,035 dependency/order-only
edges and rule commands after known revision and private-path normalization.
Both graphs are acyclic, with no missing external inputs; no runtime-library
product actions execute.

Project-reference dependency-manifest parity now passes with genuine ordinary
`GenerateBuildDependencyFile` regeneration. Previously the ordinary oracle
could skip its writer and compare Ninja's output with itself. The smoke deletes
only that generated manifest before ordinary Build, retaining the byte comparison.
The shared SDK correction identifies assets-backed projects by normalized source
path rather than project/assembly/package names and uses the explicitly bound
runtime DLL and destination for both writers. This removes spurious `.Reference`
entries and prevents ordinary version lookup from reading a `.csproj` fallback.
All 27 focused deps cases and the project-reference, framework and runtime-reference
smokes pass. Version reads remain execution-time operations on declared DLL inputs;
no discovery or replacement preparation traversal is introduced.

The real `System.Collections.Immutable` probe passes the retained-multi-targeting
guard and the live-tool prebuild dependency; its minimum-framework closure now
constructs and executes independently from an empty product tree. Runtime's
[`FilterImplicitAssemblyReferences`](../../../../runtime/Directory.Build.targets)
runs before `ResolveAssemblyReferences`, after pack candidates and project-output
metadata are available. Framework capture retains raw pack resources; graph-selected
reference/analyzer candidates participate in the same runtime filter.
Project-output and analyzer replacements are selected from metadata, without
inspecting binaries. The
[reference fixture](../../../../../genbuild/tests/RuntimeReferences.csproj)
prepares without project products, constructs a graph retaining the build-order
tool producer, and compiles/starts independently with a project replacement.

Runtime's [library trimming targets](../../../../runtime/eng/illink.targets)
now direct compilation to `PreTrim/`, link afterward to separate final paths,
and restore final publication metadata even when compilation/linking are reused.
The [authoring fixture](../../../../../genbuild/tests/RuntimeTrimming.csproj)
uses real C# compilation and a recording linker, covering portable/embedded/no
symbols, disabled symbol rewriting, untrimmed builds, unchanged-build reuse,
and missing-final-PDB recovery. This does not establish real ILLink execution
or generated-task binding in the action graph.

The real preparation probe produces no DLLs and passes `ILLink.Tasks`' explicit
MSBuild framework/utility references from
[`PackageDownloadAndReference`](../../../../runtime/eng/PackageDownloadAndReference.targets).
Graph-time SDK selection supplies pack-owned references/analyzers by `FrameworkReferenceName`;
project-declared references remain in authoring for the existing path-only resolver.
The [explicit-reference fixture](../../../../../genbuild/tests/ExplicitReferences.csproj)
prepares a missing future tool path without prebuilding, preserves aliases and
copy-local symbols, then compiles/starts independently. Assembly names fail even
with `HintPath`; missing paths fail external-input validation without publishing
a graph. Legacy manifest-only mode retains its `Reference` rejection.
Empty framework-reference lists produce valid empty pack inventories.

The [framework-basics fixture](../../../../../genbuild/tests/FrameworkBasics.csproj)
compiles a package-backed `netstandard2.0` library for a net11 executable and a
net10 library using Core and ASP.NET targeting packs. Ordinary package targets
provide explicit `Private=false` references; no framework reference is synthesized
and no package-authored references enter the pack snapshot. All selected packs
contribute items scoped to the configured target framework. A configured subset of
already-acquired packs requires no additional Restore; changed acquisition context
or missing resource observations fail before graph publication.
Windows SDK normalization is imported only for Windows platform authoring, preserving
ordinary normalization without validating unused Windows-only expressions elsewhere.

Hardened framework fetch manifests use version 2 with native resource format 2.
[`WriteFrameworkPackInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteFrameworkPackInputs.cs)
captures acquisition bindings, workload-resolved versions, pack data, presence
observations and archive/explicit member catalogs. Acquisition compiles FrameworkList
attributes, platform-manifest rows, package-override relationships and SDK runtime-graph
records into `_FrameworkPackResource` items. `Kind` identifies the record, its item
identity identifies the pack or graph, and `Attribute_*` metadata carries escaped
fields. Empty lists, absent resources and empty runtime/dependency/profile records
remain distinguishable; framework-file and RID-import order is preserved. No original
XML, JSON or manifest text is embedded in `Content` metadata. Resources are shared
within one acquisition generation, independent of project language/profile.
[`PreparedFrameworkTasks`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/PreparedFrameworkTasks.cs)
injects those resources into existing SDK policy; configured reference, analyzer,
runtime-framework and apphost selection has no filesystem/workload fallback.
The ordinary opt-in version-1 selected manifest remains supported.
Older raw-resource snapshots require a fresh Restore. Framework fact processing now
runs at the acquisition-result barrier described below. SDK and Arcade runtime graphs
now use shared native acquisition resources as well. Neither migration removes
configured preparation traversal on its own.

Framework analyzer members now feed final compiler dependency selection directly.
C#/VB preparation no longer inventories framework analyzer directories. The runtime
probe retains all 99 framework analyzer DLLs, including 91 localized companions
missing from FrameworkList, and all 56 packaged compiler files in every compiler action.
SDK task builds for current .NET and .NET Framework pass, as do focused raw-resource,
framework/package conflict, explicit-reference, compiler and acquired-apphost path
parity checks. Apphost path selection is tested separately from native apphost creation,
which is not yet a supported declared action.

A fresh Release/linux/x64 runtime-library Restore and graph reproduce all 5,320
task/output signatures and the operands of all 500 compiler commands. This is not
literal argument-string parity: 17 commands change only reference ordering after
selection moves out of evaluation-time snapshots. Thirteen resource-only satellite
compilers no longer admit unused framework analyzer DLLs; their analyzer-free command
operands are unchanged. No matched Restore-performance claim is made. Configured
preparation recursion and framework-discovery capture remain until acquisition
finalization and reference-property identity preservation are implemented.

Cold baseline/new Ninja executions subsequently completed using identical physical
execution paths and isolated publication through `LibrariesOverridePath` and
`LibrariesAllConfigurationsOverridePath`. Both repeated executions reported no work;
protected original products, caches and the default app remained unchanged.
Both runs produced the same 7,217-file inventory, but strict byte-equivalence failed:
7,203 files match and 14 DLL/PDB copies differ, belonging to `Mono.Linker`,
`ILLink.Tasks` and `System.Text.Json.SourceGeneration.Roslyn4.4`. All satellite
assemblies match. The differing assemblies have equal method-body/resource hashes
and assembly-reference sequences, but different MVID/PDB identities. Replaying the
three baseline compiler invocations with the new toolset reproduces baseline
DLL/PDB bytes exactly; replaying the new invocations restores new bytes.
Preserving compiler operands alone is therefore insufficient to match baseline
bytes: reference ordering must also be retained. That baseline ordering is not a
compatibility requirement. The new path preserves targeting-pack XML order and
normal MSBuild item insertion order, replacing case-insensitive snapshot sorting
and early framework-reference injection. A separate fresh-process graph construction
reproduces all 500 complete compiler argument strings exactly. The deterministic
new ordering is retained rather than adding baseline-specific sorting.

Bounded framework/package arbitration now runs during graph construction after
runtime filtering and path normalization. It reuses SDK conflict policy over
prepared version/platform/override/preference facts; Restore does not snapshot
the winners. An offline fixture proves newer-package replacement and retention
of the framework against an older package, including unrelated references,
runtime copies, independent compilation and startup. See
[prepared reference conflicts](#prepared-reference-conflicts) and the
[selection contract](hardened-static-graph.md#framework-inventories-and-final-reference-selection).

The real probe now completes preparation of `System.Collections.Immutable`'s
configured closure with zero DLL products, including `ILLink.CodeFixProvider`
and `ILLink.RoslynAnalyzer`'s normal package-backed `netstandard2.0` references.
References, analyzers and tool producer dependencies are not suppressed.
The probe uses `RestoreUseStaticGraphEvaluation=false` because the static Restore
launcher still assumes an installed SDK layout.
With `MSBuildGraphRepositoryRoot` set to the VMR root in both invocations, a fresh
hardened graph now passes Common's directory-props lookup and runtime's
`eng/OSArch.props`. Its unchanged `RuntimeInformation.ProcessArchitecture` query
reads the launcher's captured process-architecture context, distinct from target
architecture. Preparation now captures `Microsoft.DotNet.Arcade.Sdk` bindings
throughout the closure, and fresh hardened evaluation loads the SDK using those
bindings with package-cache SDK discovery disabled. Runtime's root
`Directory.Build.props` supplies `RepoRoot` from `MSBuildThisFileDirectory` before
the Arcade import, preserving explicit overrides. Arcade's `tools/RepoLayout.props`
therefore skips its ancestor search for `global.json`. This project-local layout
root is `src/runtime/`, not the VMR-wide `MSBuildGraphRepositoryRoot` search boundary;
no new preparation fact or ancestor-search capability is needed.
Arcade's source `tools/ProjectLayout.props` now uses `MSBuild.NormalizePath` instead
of `Path.GetFullPath` for both base paths. The
[layout fixture](../../../../../genbuild/tests/ArcadeLayout.proj) imports the actual
source props and compares all four output/intermediate paths across ordinary
execution and hardened construction plus Ninja, including AnyCPU/non-AnyCPU
layouts, nested paths, spaces, parent segments and explicit base-path overrides.
The real probe uses an isolated copy of the pinned Arcade SDK with only the changed
props overlaid, selected through the same explicit `ArcadeSdkDir` in Restore and
Build; the shared NuGet cache is not modified.
[dotnet/runtime#135658](https://github.com/dotnet/runtime/pull/135658), revision
`b4b0f6e9668046434ee1f3d0f873639fd7fa470d`, is applied unchanged under `src/runtime`.
It removes the unconditional `global.json` read from `eng/Versions.props` and
defers the existing version formulas and dependent workload-test inputs to their
consuming targets. Ordinary configured Restore succeeds; separately invoking
`CalculateWorkloadSdkVersion` still computes SDK `12.0.100-alpha.1.26463.105`,
band `12.0.100` and prerelease band `12.0.100-alpha.1`.
This is an interim unblock for library evaluation, not a hardened workload adapter:
the deferred target still performs file I/O and needs explicit version inputs
before it can participate in workload graph construction.
The root cache import is now conditional on `TargetsWasm`; host-side Emscripten,
WASI SDK and browser/wasmtime provisioning targets import the cache explicitly
when used. A shared import guard preserves single evaluation when consumers
overlap. Wasmtime defaults live with the cache inputs, so native libraries do not
discover cache anchors, read WebAssembly version files or enable provisioning.
The [evaluation fixture](../../../../runtime/eng/wasm/tests/WasmToolCache.proj)
checks native-library isolation, browser/WASI targets and individual/combined
host-side consumers without acquiring tools. Eight cases pass; the seven
WebAssembly/host-consumer profiles retain all 126 queried prior input values.
Ordinary preparation of the real library leaves the cache and wasmtime inputs
empty. The launcher now captures `MSBuildGraphHostRuntimeIdentifier` alongside
host OS and architecture; existing `RuntimeInformation.RuntimeIdentifier`
expressions read that immutable context during evaluation and construction.
The RID participates in graph identity even when unused, and child requests
cannot remove or change it. Runtime's RID authoring needs no special adapter.
All 203 action-graph cases and 51 directory-import/SDK-binding cases pass,
including captured RID overrides, string chains, invalid/mutable context and
ordinary lookup compatibility. Packaged smoke also passes with a verified
SDK-free external runtime host.
Runtime's root `Directory.Build.props` now uses
`NormalizePath('$(MSBuildProjectDirectory)', '..')` when identifying generator
projects. Ordinary evaluation of a source library, a project in `gen`, a child
of `gen`, and a reference assembly preserves all 12 queried classification/path
values. The eight-case WebAssembly-cache authoring matrix also passes.
Acquired framework, NuGet, Common cache/platform, and enabled source-control
imports remain required.
Restore captures the existing inner-framework metadata requests reached during
reference discovery, including inspected frameworks not selected for compilation.
This replaces the optional-import workaround rather than relying on incomplete
capture. Hardened SDK evaluation defaults `EnableFrameworkPathOverride=false`
so .NET Framework metadata probes do not discover framework installations.
The [packaged fixture](../../../../../genbuild/tests/DiscoveryPreparation.csproj)
requires inputs for all four inspected frameworks, compares ordinary and hardened
metadata, and independently compiles and starts only the negotiated closure.
Its [plain acquisition root](../../../../../genbuild/tests/DiscoveryAcquisition.proj)
now rejects child Restore (even with graph-input collection disabled), capture
and configured reference preparation. Normal NuGet per-framework visits publish
the required facts. A cold local package adds framework metadata through Build
authoring, which fresh Build imports without a fresh capture evaluation; both
ordinary and hardened metadata include its values for all four frameworks.
Up-to-date acquisition recreates deleted facts. Removing framework, NuGet,
source-control or Common unmanaged-cache imports for an uncompiled framework,
or the net462 Common platform-SDK import, fails without partial graph publication
or action execution.
Source-control, Common unmanaged-cache and Common platform-SDK producers already
run before `_GenerateProjectRestoreGraphPerFramework`; they need no new traversal
for this fixture. The cache and platform facts here cover absent registration
state and the current host's platform lookup, not every possible configured
property change. Package authoring that introduces new acquisition requirements
must expose them during acquisition rather than register retroactively.
The general configured preparation/discovery traversal is now removed as well;
normal NuGet acquisition remains.
An isolated hardened `GetTargetFrameworks` request against the actual runtime
reference project also returns all four advertised frameworks (`net11.0`,
`net10.0`, `netstandard2.0`, and `net462`) through an independently executed
metadata writer, without producing DLLs.
The real net10.0 Restore succeeds with discovery-inclusive capture. Opaque action
capture removes construction-time metadata loading of the future `ILLink.Tasks.dll`.
A regression captures an absent task DLL, validates its earlier producer edge,
and executes it through Ninja with literal arguments, metadata, and typed values
matching ordinary execution. Invalid implementation contracts and parameter types
fail in the executor; empty-footprint tasks remain audited during construction.
Arcade's file-backed chooser now shares selection with an audited in-memory
`ChooseBestP2PTargetFrameworkFromInputs`. Existing acquisition visits register
runtime-graph identities, not project-reference winners. Required unconfigured
`runtimegraphinputs` manifests import native resource rows during construction; the existing
NuGet matching algorithm still selects among current framework/platform and
reference metadata. `SetRidAgnosticValueForProjects` already has an audited
empty-footprint implementation; its hardened call now supplies both declarations.
The [packaged fixtures](../../../../../genbuild/tests/ArcadeTargetFrameworkInputs.proj)
compare ordinary and hardened metadata, runtime/platform fallback, omission,
explicit framework overrides and configured queries. They also
cover missing/incompatible native inputs, selection with the original JSON absent, and
[cold NuGet package imports](../../../../../genbuild/tests/ArcadeTargetFrameworkPackage.csproj)
whose resource requirements were registered before the package targets were available.
Both the ordinary Arcade task project and selected-source component build, and
the complete SDK-free packaged smoke passes.

The real probe explicitly selects an isolated local task package and the
prototype's packaged `CSharpCoreTargetsPath`. Without that compiler-target
binding, runtime's compiler package replaces the prototype integration authoring.
Restore publishes Arcade resource facts and produces zero DLLs. Build runs
the in-memory chooser and RID metadata task. Hardened reference resolution
accepts `{RawFileName}` for explicit project-relative or absolute paths without
probing file existence; generated-reference and bare-name rejection regressions
pass through the packaged app. Audited graph-time resource naming now formats
paths and metadata without opening resources or source files. Source-dependent
naming remains unsupported; ordinary builds retain their existing naming task.
Hardened signing forwards an explicit key-file path without opening it and rejects
certificate/PFX/key-container discovery. A packaged compiler regression constructs
with both the generated key and assembly absent, then independently produces the
key and compiles an assembly with the expected public-key token through Ninja.
Resource-metadata regressions compare ordinary and hardened results and reject
unsupported naming/signing policies without partial graph publication.
Hardened `CoreResGen` also assigns known resource output paths during construction
and schedules cache-free `CompileResources` actions for inline string `.resx`
files. A packaged regression constructs with generated resources absent, then
independently produces, compiles, embeds and retrieves their values. Main and
culture resource bytes match ordinary execution. Unsupported resource modes fail
construction; linked/typed payloads fail execution before output creation.
Arcade's `GenerateResxSource` now declares its single RESX input and known source
output, keeping the generator implementation and options unchanged. Its existing
targets compute source paths and compiler items without task outputs; hardened
registration selects the current action host. The standalone app stages the
source-built SDK task assembly and associated source-generation authoring, and
the real probe regenerates its isolated SDK overlay from those files.
A packaged regression constructs with both generated RESX and accessor source
absent, independently generates the source and compiles a consumer that retrieves
resources and calls named/numbered format methods. Generated source bytes match
ordinary execution with default-value arguments enabled. Unrecognized source
languages fail before publication. The complete SDK-free packaged smoke passes.
Arcade's repository-info hook now requests SourceLink's replay-aware translation
target instead of invoking providers directly. Prepared provider URLs are reused,
while Azure DevOps mirror remapping remains active. The original regex rewrite
is performed by the audited zero-I/O `RewriteRepositoryUrls` task during
construction; ordinary expressions and SourceLink enablement are unchanged.
The packaged regression first reproduced the redundant provider calls, then
verified dnceng mirror URLs, legacy DevDiv trusted URLs, the translation opt-out,
independent metadata publication and ordinary parity. Complete SDK-free smoke
passes, and no additional preparation snapshot is introduced.
Roslyn's analyzer-package config generator now follows the SDK generator's
explicit-inventory pattern. It records the actual emitted config filenames in a
deterministic set and publishes package-local items. Hardened targets apply the
existing version/mode/category/warnings-as-errors selection to those items;
ordinary expressions and custom-directory discovery are retained.
The standalone build includes the original Roslyn generator and source-built
core and Banned API analyzer payloads for its regression. A controlled release
fixture exercises the full generator, checks every emitted config against the
declared inventory, constructs with config files absent, and independently
publishes metadata matching ordinary execution. Banned API instead uses its
actual release-tracking opt-out policy, producing an empty inventory. Six
selection cases per package preserve version, mode, category, warnings-as-errors,
empty/unsupported versions and selection opt-out behavior. Ordinary custom
directories remain supported, including for the empty inventory; hardened
custom-directory overrides fail without a partial graph.
The real probe uses source-generated authoring with all 600 original core
analyzer package config assets declared. Its pinned Banned API package intentionally
ships no global configs; the existing generic generator handles its empty
inventory without further changes. Normal isolated Restore preserves both
packages' original binary/config payloads; the shared cache is unchanged.
`RestorePackagesPath` and Arcade's early `NuGetPackageRoot` select the same
isolated root. No additional preparation snapshot is introduced.
Roslyn's skipped-analyzer hook publishes an empty marker through the existing
overwrite-only `WriteLinesToFile` action in hardened mode. Its declared input is
the intermediate assembly, so execution follows successful compilation without
reading or declaring the old marker as an input. Ordinary builds retain `Touch`;
its clock access and preservation of prior contents are not annotated as
deterministic. The focused SDK-free compiler regression preserves marker bytes
and timestamps during construction, requests only the marker to prove the
compiler dependency, recreates a missing marker, and checks failed-compilation,
ordinary-build and analyzer-enabled behavior.
Arcade's resource-source batch target explicitly requires a nonempty selected
resource list. Ordinary MSBuild already skips the empty batch because it has no
outputs; hardened construction instead uses the condition, without executing an
empty zero-footprint generator or restoring incremental target skipping.
All compiler fixtures import the real Arcade targets, covering empty resources,
resources without source generation, ordinary no-ops and nonempty source
generation. The focused SDK-free compiler suite passes. Source, staged and fresh
isolated-overlay resource-source targets are byte-identical; no task rebuild,
new snapshot or shared-cache edit is needed.
Runtime's `AddImplementationAssembliesToGenAPIResolutionPath` is now scoped to
the on-demand `GenerateReferenceAssemblySource` inner-build entry point, rather
than every `ResolveAssemblyReferences`. The GenAPI target's dependencies resolve
references before its `BeforeTargets` hooks run; the cross-targeting dispatcher
does not perform the implementation-assembly probes. No new Restore snapshot or
hardened bypass is needed for normal compilation.
The focused SDK-free regression imports the actual runtime hook and observes its
metadata at a synthetic GenAPI consumer. It reproduces the old construction-time
`Exists` rejection, then independently executes normal-build publication and
checks eight ordinary cases: normal compilation, direct and alias requests,
compilation-triggered generation, missing CoreLib, an explicit libpath override,
Mono, and cross-targeting. Direct generation checks root-over-IL priority and the
alias checks IL fallback, with distinct resolved-reference directories retained.
This tests path selection and ordering, not actual GenAPI source generation;
hardened execution of the generator is still unsupported.
ILLink declares `Mono.Linker.Resources.Strings` and `ILLink.Shared.SharedStrings`
as explicit manifest names in the linker project and shared project items.
Ordinary `PrepareResourceNames` on the real net11.0 tool project produces the
same manifest/logical names before and after the change. These declarations
avoid source-dependent naming without disabling the convention globally or
expanding the graph-time formatter into a source reader.
The focused resource/signing regression imports both actual resource declarations
and checks ordinary-equivalent metadata with source/resource files absent and
the naming convention still enabled. Its hardened graph fails with `MSB3672`
before the declarations and passes afterward; independent publication matches
ordinary execution. The existing convention/dependent-source and machine-backed
signing rejection cases remain intact. No task rebuild or new snapshot is needed.
The linker project also explicitly disables preserialized-resource support.
Its two resource files contain only inline strings; ordinary SDK-default
compilation and ordinary compilation with this policy produce identical bytes.
The focused regression imports the actual project policy, packaged SDK default
and `CoreResGen` target, and compiles both actual resources. It compares both
ordinary results with independent SDK-free action execution, using RESX inputs
produced by earlier declared actions. Construction creates neither producer nor
compiled resource files, and execution creates no incremental resource cache.
The existing compiler regression still rejects preserialized mode at construction
and linked/typed payloads at execution, before resource output or compilation.
No worker changes, new snapshot or shared-cache edits are needed.
Arcade's `GenerateInternalsVisibleToFile` now invokes the existing
`WriteCodeFragment` worker with empty declared file inputs and the already named
source file as its explicit output in hardened mode. Compile and clean items
are published statically; F# retains `CompileBefore`. Ordinary invocation keeps
its task-output items and does not pass the prototype-only declaration parameters.
The focused regression imports the staged actual Arcade target. It reproduces
the previous `MSB4286`, then checks C# and VB source bytes and compile/clean items
against ordinary generation, including explicit friend keys, project-key
fallback, unsigned friends and empty no-ops. Construction preserves a stale C#
output and creates no fresh source or metadata files. F# graph construction
checks static `CompileBefore` wiring only; F# generator execution is not proven.
The resource/compiler regressions also pass. No worker rebuild, new capture or
shared-cache edit is needed; source, staged and isolated-overlay IVT targets match.
ILLink's `GeneratePackageRuntimeConfig` now delegates its file read and regex
replacement to Arcade's `RegexReplaceFile` execution worker in hardened mode.
The source runtime config is an explicit input and the package config is an
explicit output; pattern and replacement remain ordinary target properties.
Normal builds retain their original property expressions and writer. The worker
writes UTF-8 without a BOM and appends the same newline without inspecting prior
output. It introduces no new Restore snapshot or product/tool prebuild.
The focused regression imports the actual linker targets and packaged SDK
runtime-config producer. Requesting only the package config executes its template
and main-config dependencies. Main and rewritten configs remain absent during
construction, while a stale rewritten config is untouched. Independent package
config bytes match ordinary output for two frameworks and escaped content;
inactive-framework no-ops and worker write failures are checked.
Common's three source-content copy targets now select the existing `CopyFiles`
worker with declared inputs/outputs in hardened mode. `FileWrites` is published
from known destinations rather than task outputs. Normal `PreserveNewest`,
`Always` and `IfDifferent` behavior remains unchanged; hardened actions replace
stale copies without consulting destination state. Link/read-only overrides are
rejected consistently with final build-output publication.
The source-copy regression imports all three actual targets, independently copies
both source and future generated inputs, and compares all six destination bytes
and bookkeeping with ordinary execution. It covers empty no-ops, unchanged stale
outputs during construction, and fail-closed link/read-only options. The combined
source-copy and runtime-config regression passes.
The real probe gets past analyzer-config selection, provider translation,
mirror remapping, the marker hook, the empty resource-source batch and unrequested
GenAPI discovery, then passes ILLink's resource naming, resource-action construction
and IVT action construction.
Construction advances through the ILLink analyzer, code-fix and linker reference
projects, Banned API global-config selection, the package-runtime-config action
and source-content copies. Runtime library layout policy now lives in shared
`eng/libraryLayout.targets` authoring rather than Arcade's existence-driven
`GetBinPlaceItems`. Ordinary and hardened builds query the same configured product
inventory and destination mapping; the old `BinPlace` entry points delegate to
that policy. Products include declared assembly, symbol, documentation, content,
copy-local and satellite outputs. Layout queries do not build or copy them.
They retain custom item lists and target paths, deduplicate identical placements,
and reject conflicting sources for a destination.
Ordinary publication preserves existing paths and best-effort copy/link settings.
Hardened publication uses explicit byte-copy actions and static `FileWrites`,
without creating a new manifest format or adapting legacy clean ledgers.
Framework selection reuses the ordinary NuGet matching policy with prepared
native runtime-graph records, not a restored winning framework or a live construction-time
read. The layout regression passes legacy/ordinary/independent byte and placement
parity, custom and escaped paths, inactive and fallback frameworks, explicit
publication with automatic binplacing disabled, configured symbol/doc exclusion,
duplicate mappings, compatibility aliases and fail-closed conflicts.
The first consumer-owned aggregate is the library-produced part of the testhost.
With `UseAggregateLibraryTestHostLayout=true`, `sfx-src.proj` queries its selected
library builds, deduplicates complete source/destination mappings, rejects
cross-library collisions and publishes one combined inventory. CoreLib selection
remains with externals; native-library and external runtime/host staging have not
been migrated. Shared-framework library producers suppress only their testhost
publication in this mode, including custom MsQuic-style mappings, and keep their
other destinations. Ordinary default builds retain per-library publication.
Ordinary aggregate incrementality and cleanup reuse the existing file-write
ledger, while old producer ledgers cannot delete the transferred destinations.
Hardened construction skips that bookkeeping.
The two-project regression passes stock SDK versus Ninja byte/mapping/FileWrites
parity, configured framework/platform/property negotiation, pure future queries,
custom native maps, identical-map deduplication, cross-project conflicts, empty
aggregates, default/non-source compatibility and ordinary unchanged-copy,
dropped-output, stale-producer-ledger and foreign-file cleanup checks.
Windows-only case tests are authored but not executed on Linux.
A selected output verifies its future producer edge, not minimal pruning:
the prototype currently retains conservative ordering of preceding actions.
The guarded ordinary runtime query returns the future Immutable DLL/PDB/XML
inventory without building it. All three layout requests select `net11.0` from
the project's supported frameworks, so the selected `net10.0` build correctly
has no placements; positive publication is proven by the isolated fixtures.
The actual `sfx-src.proj` consumer also succeeds in a guarded query restricted
to Immutable through the existing custom Traversal import hook. It selects the
`net11.0` inner build and returns three future testhost placements with absent
sources/destinations and no products built. Full-framework composition/execution
is not established by that bounded query.
The real probe now passes resource substitution and XML combination.
`GenerateResourcesSubstitutionFile` uses a declared-input/output `ReplaceTextFile`
worker in hardened mode, while ordinary builds retain their original writer.
The template is also an ordinary incremental input. Replacement preserves
sequential literal substitutions, empty values and the legacy writer's text-item
conversion. Tests compare stock SDK bytes, preserve stale output during
construction, consume a future template producer and reject missing inputs or
failed execution writes.
All three `CombineLinkerXmlFiles` calls declare their XML inputs and output;
the runtime task source exposes that contract using the compatibility attribute
pattern for older MSBuild references. An isolated SDK project builds that actual
task source through Ninja, then selected descriptor/substitution/attribute
outputs consume future XML inputs and match ordinary bytes and static bookkeeping.
Metadata-only resource naming accepts the source-convention switch when a
resource is not resx, matching ordinary type/extension classification without
source discovery. Resx convention inference and explicit dependencies remain
unsupported. Immutable's string-only resource policy disables object
serialization; all three real ILLink/Immutable resource files match
SDK-default bytes under both ordinary and independent compilation.
Disabled or ineligible NGen optimization no longer searches unused profile
directories; enabled bounded profile selection retains ordinary metadata.
Runtime's missing-documentation warning now has an explicit zero-I/O contract.
The emitter validates and formats the complete graph into a pending in-memory
publication plan instead of writing from `RequestBuilder`. Root-submission
completion waits for results and logging, applies promoted-warning failure, then
publishes only on success. Pending publication survives in-process result copies
but is not serialized into persistent result caches. API initialization and
request execution enforce the existing single in-process construction-node
restriction as well as the CLI.
Regressions cover ordinary warnings, global/project-local promotion, warning
exclusions and message demotion, child diagnostics, preservation of a previously
executable graph, and publication I/O failure cleanup. Ordinary `Warning.Execute`
semantics are unchanged.
ILLink's pre-compile target records its trim-input directory through the existing
structural-directory contract instead of executing `MakeDir` during construction.
Recording-producer regressions verify ordinary-equivalent assembly/PDB selection,
restored metadata, inactive trimming and rejection of uncovered directories;
existing ordinary compilation/linker-ordering and incremental checks still pass.
ILLink input capture mirrors runtime main's invocation-cache contract at
[`9857961ed10086d04cf981039d12799520577055`](https://github.com/dotnet/runtime/blob/9857961ed10086d04cf981039d12799520577055/src/tools/illink/src/ILLink.Tasks/LinkTask.cs).
`WriteILLinkInputs` inventories external assembly candidates in every reference
search directory, config/MDB/PDB sidecars, immediate culture satellites, recursive
metadata modules and nonmetadata `AssemblyFile` entries during configured Restore.
`ResolveILLinkInputs` consumes those facts in memory and adds explicit XML, task,
linker and external-host paths. The evaluator imports the configured facts, not
selected linker results. Changed external directory membership requires Restore.
The existing linker search/resolution policy is unchanged.

Construction supplies `@(_MSBuildActionOutput)` lazily when directly referenced
by an audited task parameter. It describes prior recorded file outputs across the
shared configured-project graph; producing those files or opening future PE
metadata is unnecessary. The ILLink resolver conservatively includes all producer
files beneath each generated assembly/tool directory, covering generated
sidecars, modules, satellites and runfiles, including separately located tools.
Unsupported/custom discovery arguments fail closed rather than executing an
underdeclared action. The existing pre-trim compiler mapping is retained; the
declared linker action publishes its final DLL and conditional PDB without an
in-place transformation or shared-directory deletion.

Unit cases cover external inventories, recursive modules, escaped property
transport, future producer/tool closure, stale-product exclusion and invalid
inputs. A runtime-target fixture independently builds and loads the real linker
task source and launches a recording linker, with no tool/product prebuild.
It checks the actual declared input list, fresh candidate discovery, ordinary
DLL/PDB parity and reference-sidecar invalidation. Existing ordinary compiler and
linker-ordering/incremental tests still pass. This does not claim that the fixture
performs real trimming.
Common's documentation copy now uses declared `CopyFiles`, static `FileWrites`
and the existing unsupported-copy-option validation. Runtime's intellisense
override stage similarly records the copy and omits only the timestamp-only
`Touch` in hardened mode. Ordinary behavior remains unchanged.
The copy regressions exercise compiler-produced documentation, renamed paths,
disabled copies, stale destinations, override staging and ordinary byte parity.
The guarded profile restores without shared fallback folders because runtime's
explicit package-download references address the primary `NuGetPackageRoot`.

Hardened `CallTarget.TargetOutputs` now carries concrete returned items through
output mapping and legacy caller-scope completion, not just availability state.
Item metadata and escaped identities are retained, property outputs join escaped
includes, and empty/skipped results clear properties without removing prior
items. Ordinary-build parity cases cover nested calls, cached returns, duplicate
policy, conditional mappings and caller-side metadata updates.
This restores the SDK's existing transitive-copy hooks: the linker deps,
runtimeconfig, dev and pack manifests are published alongside its assembly in
ILLink.Tasks' tool directory. Their copy action is an explicit producer, and
all four paths are ILLink declared inputs.

The real Immutable closure's 82-action graph now completes independent Ninja
execution, compiling the linker, analyzer, code-fix, task and Immutable inputs,
performing real trimming and publishing the final library DLL/PDB.
Published hashes match the linker outputs; a second Ninja run has no work.
Graph reconstruction preserves existing product-file inventories/timestamps,
and shared cache hashes remain unchanged. This proves the managed closure,
not a complete runtime build.
Native/CoreCLR/CMake integration is later.

### Current-framework closure

The fresh `net10.0` profile completes ordinary preparation and hardened graph
construction without DLL/PDB/resource/generated-source products, then independently
executes all 82 actions. Final library DLL/PDB bytes match ILLink outputs; all four
tool manifests are declared inputs, and repeated Ninja execution has no work.
Shared package-cache hashes remain unchanged.

Selecting `net11.0` adds Immutable's in-tree framework-library references and
reaches CoreLib and the interop source generators. CoreCLR's symbol folder
metadata now uses pure `Path.GetDirectoryName`/`GetFileName` instead of
`Directory.GetParent`; isolated ordinary/hardened output parity covers IL, aotsdk
and paths with spaces.

XLIFF localization now retains the existing translation and freshness behavior
through explicit actions. `GatherXlf` and `GatherTranslatedSource` are audited
metadata-only construction tasks. `GatherXlf.FileInputs` includes the source and
every derived XLF, including neutral files and `SourceDocumentPath` overrides.
`ValidateXlf` reuses ordinary `UpdateXlf`'s freshness algorithm with modification
disabled and writes a declared success stamp. Translation actions consume their
source/XLF pair and, when validation is enabled, that stamp. Existing schema
validation, encoding, resource-path rewriting and output metadata are retained.
Ordinary source-updating targets remain unchanged; explicit hardened `UpdateXlf`
requests fail without changing sources. Template-localization workflows remain
unsupported rather than silently omitted.

The standalone profile builds and stages the selected XLIFF tasks/targets.
The real runtime probe consumes a source-built package from an isolated feed,
with `UpdateXlfOnBuild=false` and `ErrorOnOutOfDateXlf=true` to select the existing
CI-style read-only policy instead of Arcade's local developer auto-update.
Focused regressions compare ordinary and Ninja translated resx/JSON, compiled
resources and item metadata in both filename modes, verify incremental edits and
no-work rebuilds, and reject missing/stale/neutral-stale/malformed/schema-invalid
XLFs without source mutation. Unit coverage verifies neutral input enumeration
and source-document path overrides. An isolated real interop-generator
localization graph executes one validation and 13 translations, then has no work;
source files and shared-cache hashes remain unchanged.

SDK satellite generation now defers the future-value boundary through
[`GenerateSatelliteAssemblySource`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/GenerateSatelliteAssemblySource.cs).
The action declares the parent DLL input and generated C# output, composing the
existing `GetAssemblyAttributes` and `WriteCodeFragment` implementations without
reading the DLL during construction. Authored attributes precede parent-derived
attributes and the culture attribute, exactly as in the ordinary target.
`FormatCsc` and `CompileManaged` consume that known source and the culture's resources;
separate per-culture compiler items avoid contaminating main compilation.
Declared `CopyFiles` actions publish the satellites. Ordinary extraction,
compilation and copy paths retain their existing behavior.

The standalone SDK task directory includes the locally built
`Microsoft.Build.Tasks.Core` and `System.CodeDom` dependencies. The existing
action tool-directory inventory includes both as Ninja inputs. SDK tasks use
the centrally versioned compile reference without redistributing its runtime
assets through ordinary SDK packaging.
The two-culture regression compares ordinary/action source bytes and verifies
all inherited/authored attributes, escaping, parent versions distinct from
MSBuild properties, resource lookup, selected-output producer ordering,
incremental parent changes and no-work rebuilds.
The full real interop-generator project executes 67 actions, including one
validation, 13 translations, 13 satellite-source generations and 14 compilations.
The parent and all 13 satellites are published; repeated Ninja has no work and
source/cache hashes remain unchanged. Unlike the earlier localization-only
probe, this uses the project's complete Build target.

CoreLib's explicit `System.Private.CoreLib.Strings` manifest name preserves
ordinary resource metadata. Its 1,459 resource entries are inline strings and
now select compilation without object serialization. Descriptor combination
declares the authored XML inputs, and runtime-root generation declares the four
headers plus combined descriptor and its exact XML output. A resource/descriptor
target profile independently executes 221 actions, including all tool producers,
with no products generated during preparation/construction. Both descriptors
match ordinary task execution; resources match ordinary compilation, and a
repeated Ninja invocation has no work. This profile does not compile CoreLib.

Compiler-owned generated-source trees now preserve persistent generator output
through deterministic archives, Ninja dyndep and receipt-based materialization,
as described above. The net10 closure still independently executes 82 actions.
Completed targets are removed from the validator's active stack before their
after-targets run: an ApiCompat after-target may legitimately depend on the
completed `CoreCompile`, directly or through preparation targets.

ApiCompat assembly validation records only default-suppression existence, with
`_ApiCompatInputsVersion=2`. In direct mode the SDK registers finite source/output
bindings in the existing acquisition visits, regardless of whether ApiCompat is
already enabled: newly acquired package authoring may enable it in fresh Build.
After successful root acquisition,
[`WriteApiCompatInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteApiCompatInputs.cs)
publishes one unconfigured `apicompatinputs/$(MSBuildProjectFile).g.props` per project.
It preserves positive/negative observations and unchanged-content timestamps,
republishes missing facts on up-to-date Restore, and rejects conflicting outputs
or aliases to source inputs before writing. There is no child Restore/capture
request, configured selection or reference-directory inspection. Legacy non-direct
authoring retains its configured capture writer. Older directory-inventory
snapshots must be regenerated.
A pure
[`FormatApiCompatInputs`](../../../../sdk/src/Compatibility/ApiCompat/Microsoft.DotNet.ApiCompat.Task/FormatApiCompatInputs.cs)
selects primary/reference assemblies, suppression and attribute-exclusion files,
and both Roslyn assemblies during graph construction. Candidates reuse the
already-reconstructed NuGet package member inventory, acquired framework pack
members and FrameworkList rows (including non-default/profile-excluded members),
and current reference files. Framework reference catalogs are emitted only when
ApiCompat is enabled; no extra Restore selection or package-model parse is added.
Loose external references can supply `ApiCompatReferenceFile` items for neighboring
DLLs needed by the loader. A directory without acquired or explicitly declared
members is rejected; it is not an invitation to discover filesystem contents.
The existing comparison
runs independently with a declared success semaphore, preserving reference
precedence and restricting resolution to declared files. Failure preserves the
previous semaphore. Ordinary behavior is unchanged; hardened suppression
generation and primary globs/directories are explicitly unsupported.
`TestApiCompat` exercises ordinary/action diagnostic parity, suppression edits,
reference precedence, undeclared sibling rejection and no-work rebuilds.

The explicit-catalog migration passes 16 focused cases, current-.NET/.NET-Framework
SDK task builds, and ApiCompat, runtime reference-layout and framework smoke tests.
A fresh runtime Restore using the newly packed ApiCompat task writes all 297
suppression-only manifests. Graph construction preserves all 5,320 action/output
signatures and all 55 ApiCompat file-input/required-producer sets and reference-group
ordering. Compiler operands and ordering are unchanged apart from the isolated
validation setup's additional primary/fallback package-cache `PathMap` entry.
No runtime-library actions are executed for this migration and no matched
Restore-performance claim is made. The configured preparation walk still needs
its remaining resource producers migrated to acquisition-result processing.

Ordinary NuGet acquisition now preserves request identity when
`MSBuildRestoreGraphInputs=true`.
[`PrepareRestoreRequests`](../../../src/Tasks/PrepareRestoreRequests.cs) records
the normalized project path and effective global properties, retaining authored
reference assignments and removals without negotiating Build configurations.
Distinct property contexts for one project remain separate requests; equivalent
requests deduplicate. The ordinary, non-prototype Restore path remains path-based.
Both MSBuild task implementations replay these requests through an invocation-local,
versioned property record, preserving escaped values instead of converting the
complete global-property dictionary into a semicolon-delimited `Properties` string.
Graph entries carry the compact request identity; only request records carry the
full property state.
The fixed-toolset profile rejects authored `ToolsVersion` overrides.

NuGet still acquires one specification per project path.
`ValidateRestoreRequestsTask` reconstructs and compares specifications only for
projects reached under multiple requests. Equivalent specifications share
acquisition while retaining the originating request markers; incompatible
specifications fail before acquisition, rather than silently combining requirements
or overwriting one assets file. Automatic compatible batching and disjoint output
allocation are not implemented. Those requests require separate Restore invocations
with disjoint restore output paths.

This admission check exposed runtime analyzer/generator references inheriting
`TargetFramework=net11.0` from ILLink.Tasks despite targeting `netstandard2.0`.
Runtime now explicitly removes the consuming framework on those tool references,
including the analyzer/code-fix projects packaged by ILLink.Tasks. This is authored
acquisition policy, not Restore-side nearest-framework negotiation.
`TestRestoreRequests` covers a diamond with distinct/equivalent contexts, explicit
framework/configuration/platform assignments, removed globals, unchanged ordinary
Restore, incompatible-spec rejection without modifying assets, and replay using
the real runtime generator/live-ILLink reference metadata.
All 16 focused request/admission cases pass, including admission tests against
the packaged NuGet task. Isolated runtime Restore and graph construction preserve
all 5,320 action/output signatures, all 500 compiler argument strings and all 55
ApiCompat input/required-producer sets and reference groups. The comparison
accounts separately for the earlier baseline's Git revision in SourceLink metadata
and relocation-dependent ordering of ApiCompat's otherwise identical declared
input sets. Protected originals remain unchanged; runtime-library actions are
not executed.

Request identity associates actual acquisition evaluations, requirements and results.
It is not a resource identity or a required identity for a later configured Build
request, and the transient property record is not a persisted evaluated-property
catalog. Fresh Build evaluation still owns configured selection and graph topology.
Migration of remaining resource producers and removal of the configured
preparation recursion/framework-discovery capture remain subsequent work; this
change adds neither another traversal nor a Restore-performance claim.

NuGet fact production now consumes acquisition results directly. Existing
per-framework Restore visits emit finite assets/output/compiler/language bindings;
`ValidateRestoreRequestsTask` retains these bindings from all equivalent admitted
contexts. Immediately after successful `RestoreTask`, the packaged SDK
[`WriteNuGetRestoreInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteNuGetRestoreInputs.cs)
validates destination compatibility and serializes each distinct output once
through `WriteNuGetInputs`. A shared per-project manifest contains the entire
assets model and ordered acquired package folders. Fresh configured Build imports
it for selection; the remaining legacy resource collectors reuse its package
folders rather than rewriting the NuGet model. The explicit processor-assembly
binding lets plain NuGet/Traversal roots drive this without importing the .NET SDK.
The ordinary non-prototype capture path remains unchanged.

`TestNuGetAcquisitionInputs` uses a plain root and a two-framework child reached
under two equivalent acquisition contexts. It requires four bindings but one
written model, rejects any child Restore/capture/configured preparation, and
verifies an up-to-date Restore recreates a deleted fact manifest. This migration
adds no retained-instance registry, new project evaluation or reference walk.
Other resource producers and their preparation traversal remain; fewer NuGet
serializations alone are not a Restore-performance result.

Validation passes current-.NET/.NET-Framework task builds, 19 source-linked SDK
fact cases against the packaged task assembly, eight packaged NuGet admission
cases, and the acquisition/request/framework/discovery smokes. The full SDK test
project remains blocked by unrelated stale `ResolveNuGetAssets` test APIs; the
focused harness replaces only its directory-creation helper. Isolated runtime
Restore processes 645 bindings into 343 shared models in one root task, replacing
987 legacy `WriteNuGetInputs` task invocations. Evaluation and project-request
counts remain unchanged at 4,898 and 69,562 respectively, because the other
preparation producers have not moved. Fresh graph construction preserves all
5,320 action/output signatures, 500 compiler commands and 55 ApiCompat input sets
after separately accounting for Git revision and private-profile relocation.
No unexpected parameter differences, missing external inputs or protected-product
changes are observed. Runtime-library actions are not executed.

Framework-pack translation now uses the same successful-acquisition boundary.
[`CollectFrameworkPackInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/CollectFrameworkPackInputs.cs)
records finite pack requests, original installed-pack lookup results, resolved
versions, explicit loose-pack member catalogs, runtime-graph identities and output
bindings during the existing per-framework acquisition visits. It performs no
resource reads and adds no `ProcessFrameworkReferences` pass. Equivalent NuGet
requests retain every binding header and payload row.
[`WriteFrameworkRestoreInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteFrameworkRestoreInputs.cs)
then compiles the successful acquired resources once per shared manifest. Bindings
sharing an output must agree on machine roots, SDK identity, package-folder
precedence and any explicit member catalogs for the same pack root. Incompatible
scopes fail rather than overwrite or silently merge observations; callers may
choose separate output/resource scopes. Original installed misses remain distinct
from post-acquisition package hits. A shared manifest admits multiple explicitly
identified runtime graphs without conflating their RID relationships.

Platform display-name observations are not pack data: configured platform coverage
can exceed the acquisition visits. At the same acquisition boundary,
[`PlatformSDKInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/PlatformSDKInputs.cs)
enumerates installed SDKs and registered platform aliases once, preserving ordinary
lookup precedence. It compiles only non-null manifest `DisplayName` overrides into
native `_PlatformSDKDisplayName` items in a versioned shared catalog. Empty names
remain overrides; absent names use the ordinary identifier/version fallback.
[`GetPlatformSDKDisplayNameFromInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/GetPlatformSDKDisplayNameFromInputs.cs)
performs that selection without I/O, with case-insensitive identifiers and exact CLR
version semantics. Unsupported, duplicate or malformed facts fail without discovery.
The SDK's late display-name fixup runs before framework metadata queries and validation
in direct hardened mode, rather than at evaluation time. Ordinary evaluation and
the separate Common platform-SDK location/presence observations remain unchanged.

Acquisition publishes each unconfigured
`obj/frameworkpackinputs/$(MSBuildProjectFile).g.props` locator after successful
resource translation. Fresh Build imports it before the shared facts, preserving
alternate metadata entry points and acquisition scopes. Conflicting locators and
aliases to acquisition inputs, shared outputs or the platform catalog fail.
The configured framework/platform producer and its preparation registration are
removed in direct mode; other producers still retain the preparation traversal.
ApiCompat suppression-presence facts now have their own acquisition-visible SDK
binding and root writer; direct preparation no longer schedules its package hook.
This adds no retained instances, evaluations or reference walk.
The acquisition-only fixture rejects the old framework producer, requires all four
framework bindings to produce one shared model, and checks ordinary-mode isolation
and locator/catalog re-publication after an up-to-date Restore.

The platform/locator follow-up passes both task TFMs, 72 packaged actual-source SDK
cases and acquisition/framework/discovery/reference/compiler/ApiCompat smokes,
including offline installed-name/alias selection and C#/VB queries for an
unvisited platform version. Isolated runtime acquisition produces one catalog and
343 locators from 645 bindings; the old framework/platform producer executes zero
times. All 297 ApiCompat fact files remain byte-identical. Fresh graph audits
preserve 5,320 actions, 500 compiler commands, 55 ApiCompat input sets, 27,257 Ninja
nodes and 277,035 dependency/order-only edges and rule commands. Only known revision
and relocated declared-input ordering differences are normalized. Protected products
and 105 frozen engine/compiler files are unchanged. Evaluation/request counts remain
4,898/69,562: the preparation traversal is not removed and no timing gain is claimed.
No runtime-library actions are executed.

Earlier framework-translator validation passed current-.NET/.NET-Framework task builds, 44 focused actual-source
SDK cases against the packaged assembly, 11 packaged NuGet admission cases, and
the acquisition/framework/discovery/reference/ApiCompat smokes. An isolated runtime
Restore and fresh graph retain all 5,320 action/output signatures, 500 compiler
commands and 55 ApiCompat input sets after known Git-revision and private-profile
normalization. Ninja graph audits preserve all 27,257 nodes, 277,035 dependency
edges including order-only markers, rule commands and acyclicity. Protected original
products and frozen compiler/engine files are unchanged. No runtime-library actions
are executed and no Restore-performance improvement is claimed.

Runtime graphs now have a separate neutral resource contract.
[`Microsoft.NET.RuntimeGraphInputs.targets`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.RuntimeGraphInputs.targets)
registers the selected graph and explicit `RuntimeGraphResource` items during
existing acquisition visits, including before Arcade package targets are available.
The root [`WriteRuntimeGraphInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteRuntimeGraphInputs.cs)
translates each graph identity once, emits shared `_RuntimeGraphResource` rows and
unconfigured per-project import manifests, and republishes deleted files on
up-to-date acquisition. Native records preserve ordered imports, dependency
sets/ranges, compatibility profiles and empty records. SDK and Arcade link the
same codec source; FromInputs selectors reconstruct the typed model without
reading the JSON, querying the environment or selecting a restored winner.
Unknown graphs or incompatible facts fail closed. `_CaptureTargetFrameworkInputs`
and its configured preparation registration are removed, but the other producers
still retain the overall preparation traversal.

Validation passes both SDK task frameworks, the standalone Arcade component,
55 packaged actual-source SDK cases and 12 packaged NuGet admission cases.
Arcade/native/framework/discovery and both runtime layout smokes pass, including
offline selection from multiple explicitly declared graphs and cold package imports.
An isolated runtime Restore processes 645 bindings into 343 project manifests
and two shared graph resources in one root task; the old Arcade capture target
and task execute zero times. The 4,898 evaluations and 69,562 project requests
remain unchanged because other preparation obligations remain.
Fresh graph construction preserves all 5,320 action/output signatures, 500 compiler
commands, 55 ApiCompat input sets, 27,257 Ninja nodes and 277,035 dependency edges,
including order-only markers. Only 717 Git-revision parameter differences require
normalization; there are no unexpected differences or missing inputs.
Original products and 105 frozen engine/compiler files remain unchanged.
No runtime-library actions are executed and no timing improvement is claimed.
Old configured JSON manifests require a fresh Restore to publish native facts.

Source-built reference directories use `ApiCompatProducedReferenceDirectory`
and explicit `ApiCompatProducedReferenceFile` members. Runtime exports the
transitive `ApiCompatReferenceFiles` metadata from its reference-layout
producers through ordinary project target outputs, flattening list-valued
metadata before a pure, OS-case-aware formatter selects immediate pack DLLs
and deduplicates diamond dependencies. ApiCompat consumes both the matching
contract's and implementation references' pack closures. The implementation-side
search also receives the pack fallback, after its direct references. Separate
`ApiCompatPrivateReferenceFiles` metadata propagates private assemblies such as
System.Private.Uri that have no pack layout. These private dependencies are
appended after direct files, respecting the contract-side implementation-reference
opt-out; public transitive implementation binaries are not promoted over pack
references. These queries do not introduce implementation-build cycles or change
pack directory fallback into explicit-file precedence.
Acquired/loose candidates under produced roots are ignored even if stale files exist.
The formatter adds
their selected members as exact inputs with `RequireProducer=true` metadata;
the graph writer requires prior declared output owners before publication.
An unused directory may have no members, but unresolved required references
fail closed rather than discovering ambient siblings.

Runtime linking now precedes ApiCompat input collection explicitly, rather than
relying on the registration order of two `CoreCompile` after-targets. Validation
consumes the final linked intermediate and retains its normal framework-based
suppression identity, not `PreTrim`. The ordinary recording-linker regression
exercises this ordering across symbol modes, untrimmed builds and incremental
recovery; runtime reference fixtures cover private diamond closures, public
reference precedence and both contract-reference policies.

The actual current-framework Immutable reference closure independently
executes 84 actions, including strict ApiCompat validation with the three
producer-owned Immutable, Collections and Runtime pack members. Restore and
construction produce no assemblies; repeated Ninja has no work and shared
caches remain unchanged. Focused fixtures cover future and stale directories,
explicit-file precedence, unused empty directories, undeclared dependencies,
reference-only invalidation and atomic rejection of outside-root members or
stale files without producers.

The full net11 library closure includes string resources with the SDK's
preserialized-resource option and source-built GenFacades generation.
A whole managed CoreLib Build now
independently executes 238 actions, including compilation, ILLink and ApiCompat.
Its compiler-generated sources are materialized and its linked DLL/PDB and
compiler XML are published with byte-identical copies. Repeated Ninja has no
work and preserves the validation semaphore; shared caches remain unchanged.
CoreLib's unused targeting-pack fallback has no members, while the separate
Immutable reference probe verifies nonempty transitive membership.
Localization and validation stay enabled, failed construction publishes no
graph/products, and a full native runtime or complete net11 library build
remains unproven.

The whole shared-framework traversal (`src/runtime/src/libraries/sfx-src.proj`)
now completes isolated Restore preparation. ApiCompat fact imports are limited to
inner builds: framework-neutral `GetTargetFrameworks` evaluation needs no outer
snapshot. The unconfigured acquisition file serves all inner frameworks.
The regression queries
C# and Visual Basic projects targeting `net11.0` and `net11.0-windows` through
a traversal, verifies the configured C# validation action, and rejects a missing
acquisition file without publishing a partial graph. Both languages receive
suppression-presence facts from root acquisition without compiling products or
running their configured capture hooks.

[`WriteFrameworkReferenceFetchManifest`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteFrameworkReferenceFetchManifest.cs)
also captures platform SDK display metadata with its platform identifier/version.
Hardened evaluation replays it instead of calling ambient platform discovery;
missing facts or mismatched identities fail metadata queries with NETSDK1256.
The regression verifies byte-identical ordinary/prepared framework metadata,
including display names, and all three invalid-fact cases reject construction
without partial graphs. The existing ordinary/action ApiCompat smoke suite passes.

The whole traversal now prepares 633 framework-reference and 297 ApiCompat
snapshots, including both Visual Basic Core frameworks. The former platform
discovery and missing VB snapshot errors are gone. String-only resource actions
now accept the preserialization option: inline strings preserve ordinary
`ResourceWriter` bytes even when ordinary execution selects
`PreserializedResourceWriter`. Real Collections, Immutable and ILLink RESX files
verify this with and without a System.Resources.Extensions reference. Linked and
typed payloads remain rejected, including typed preserialized inputs.

The standalone toolset also builds GenFacades from source. Partial-facade
generation declares its contract, seeds, compile sources and both Roslyn
assemblies as individual inputs, with one generated source output; generated
compile/clean items no longer require output existence during construction.
Platform-not-supported rewriting declares its source files, optional API
exclusion file, both Roslyn assemblies and exact per-source output paths.
These tasks retain isolated managed execution, and the shared Roslyn path
selection now appends `bincore` to its own selected path. Focused independent
execution matches ordinary source bytes and compile/clean items, consumes
future producers, invalidates on source/exclusion changes, rejects invalid
declarations or missing required inputs, and preserves prior partial-facade
source on semantic failure. Unchanged facade actions have no Ninja work.

`ClearAssemblyReferenceVersions` accepts an optional separate output and validates
exact, distinct input/output declarations before touching files. Hardened targets
run after `CoreCompile`, preserve its raw assembly, and remap `IntermediateAssembly`
to a rewritten `ZeroVersionSeeds` output for downstream publication. Item metadata,
debug backups and clean bookkeeping survive; PDB and documentation paths stay
unchanged. Ordinary compilation retains its in-place rewrite. Focused seeded and
real SDK compiler fixtures verify exclusive producer ownership, zeroed references,
unchanged assembly version/MVID, ordinary DLL/PDB/XML byte parity and Ninja no-work.
Invalid footprints and malformed PE input fail without modifying prior outputs.

The standalone toolset now also builds the templating task from source.
`GenerateFileFromTemplate` declares exactly one template input and one generated
output, validating the canonical paths before reading or writing. The hardened
DiagnosticSource call supplies those declarations; ordinary builds retain their
existing call and pinned-package compatibility. Focused execution uses the actual
DiagnosticSource target and template and verifies ordinary byte/item parity,
future producers, substitution and escaping, no-work, unchanged-output timestamp
preservation, input/property invalidation and rejection of incorrect footprints.
The compiler smoke's stale generated-directory rejection is now a positive
compilation/materialization test; compiler, generated-tree, facade and
platform-stub checks all pass.

The standalone toolset builds and stages both source-built compilers. VB now uses
`FormatVbc` and the shared `CompileManaged` executor, with the same deterministic
footprint/path-map policy as C#. Preparation captures VB's compiler/analyzer
closure per configured framework. Resource formatting uses VB's root namespace
and flattened basename policy without source discovery. Ordinary Vbc and resource
naming remain on their existing paths. Focused Ninja execution checks future
source/resource and explicit-runtime producers, `None`/`Embed` runtime modes,
relocated assembly/PDB/reference/documentation bytes, ordinary resource metadata
parity, mapped caller paths, no-work and independent compiler failure. Unsupported
SDK/runtime lookup, response files, nondeterminism and source-dependent naming
reject construction. C#, generated-tree, satellite-source and ApiCompat regressions
also pass.

SDK project-output queries now carry producer-owned assembly/file versions when
the corresponding assembly-info attributes are generated. Pure version
initialization and four-part normalization avoid opening future analyzer binaries;
disabled attribute generation does not advertise unknown facts. Focused Ninja
execution builds competing source generators, checks ordinary selection parity,
short-version normalization, repeated references, actual emitted versions and
fail-closed disabled attributes.

A fresh isolated System.Runtime.InteropServices.JavaScript Restore and graph
construction produce no product DLLs. Its 502-action Ninja graph then builds the
source-generation tools, CoreLib/library dependencies and the JavaScript library;
the second execution reports no work. Forwarded-type source generation in
`shims/stubs/Directory.Build.targets` now declares no file inputs and exactly the
generated C# output: static `ForwardedType` metadata supplies the content.
`TestRuntimeForwardedTypes` checks ordinary source-byte parity, actual plain and
multiple-inner-type compilation, single producer ownership and the future compiler
dependency, `Compile`/`FileWrites` bookkeeping, no-work, metadata-change invalidation
without construction-time rewriting, and the original empty-item error without
products or a partial graph.

Hardened ASN.1 generation in
`Common/src/System/Security/Cryptography/Asn1/AsnXml.targets` now transforms into
private `obj/asnxml` files and remaps only matching compiler inputs, preserving
metadata and clean ownership. It does not write checked-in sources or consume the
ordinary file-comparison result during construction. Ordinary regeneration,
comparison, touching and warnings remain unchanged. Declared `XslTransformation`
calls require exact XML/stylesheet inputs and distinct, non-overlapping outputs;
trusted settings, DTDs, compiled/string stylesheet modes and external stylesheet
resolution are rejected. Calls without declarations retain ordinary behavior.
Unit tests exercise parity, invalid footprints and external-resolution rejection;
`TestRuntimeAsn` uses the real runtime target and stylesheet to check source-byte
parity, compile metadata, unrelated sources, no-work, XML-edit invalidation,
construction purity and execution-time schema failure without source-tree writes.

The whole libraries traversal now constructs a 5,321-action Ninja graph (5,320
task descriptors plus the generated-source tree materializer), without generating
assemblies/resources. Its 58 ASN outputs each have one producer and are explicit
inputs to the cryptography compiler action. The previously reported networking
Build cycles do not recur. Preparation and construction preserve the recorded
shared cache hashes. Ninja generates all 58 ASN sources; each matches its
checked-in counterpart modulo BOM/line endings. Constructor assignments to
getter-only ref-like auto-properties expose the source-built Roslyn assertion
`propertyAccess.AutoPropertyAccessorKind != AccessorKind.Set`. Equivalent explicit
readonly backing fields in `PasswordBasedEncryption` and the DNS record types
temporarily avoid this compiler defect without disabling assertions or escape
checks in the Debug validation. Normal Release compiler builds omit Debug.Assert
calls; the source workaround remains in place.

The blanket previous-action order-only chain has been removed. The complete
emitted libraries graph now succeeds from fresh private products with
`ninja -j4`, without resume or editing the graph: 5,320 task actions, 7,208
declared file outputs and one generated-source tree materializer. Ninja's log
accounts for all task actions, including 500 compiler invocations, with four
actions and four compilers overlapping. Repeated Ninja reports no work.
The independent-closure regression checks selective branch execution, parallel
completion and edit isolation. Real file/tool/tree dependencies remain; the
successful run alone does not establish read isolation or complete declarations.
Protected original products/caches remain unchanged. Runtime tests and the
entire runtime build are not covered by this managed-libraries profile.

An initial controlled Release comparison keeps compiler bytes and
`UseSharedCompilation=false` identical, uses four execution slots, private
products and warm package/OS caches. Ordinary acquisition/Build take
57.5/900.0 seconds; prototype acquisition/construction/Ninja take
73.4/509.3/539.6 seconds (957.6 versus 1,122.3 seconds total).
Fresh no-op Build costs 49.5 versus 482.6 seconds; the latter includes
482.3 seconds of reconstruction, not just the 0.3-second reused-graph Ninja
no-op. A restored comment-only Immutable source edit costs 70.8 versus
501.7 seconds, including 486.5 seconds of prototype reconstruction and
15.3 seconds of execution. Both modes run 500 cold compilers, zero no-op
compilers and five edited compilers; Ninja executes 44 edited actions.
Construction currently dominates the comparison, so these results do not
establish an end-to-end speed improvement.

These are single shared-machine observations, not a compiler-server/default-SDK
baseline or cold-network/cache experiment. Published inventories match all
3,229 paths and all non-DLL/PDB bytes. Four cryptography DLL copies differ in
raw IL, but comparison normalizing metadata tokens and file-local path checksums
matches all 7,458 method signatures, instructions, locals and exception regions.
Identity/debug/metadata differences remain; this is not exhaustive semantic or
byte equivalence.
Timings use the pre-audit-correction action host and
exclude tracing; the subsequent resolver fix requires separate final-host
measurement before making final-performance claims.

The Linux strace auditor now checks per-action content edges, exact runtime/OS
catalogs and optional narrowly accounted runtime/scratch policies; metadata
and failed probes remain separately visible. A rebuilt-host pilot passes after
avoiding manifestless task-directory enumeration. The real libraries run was
then stopped at 657 of 5,321 families with 1,427 undeclared observations,
85 unresolved events and three incompleteness records. This is **not** a
whole-graph audit pass. The identified generated-task manifest and compiler-child
environment defects are corrected: graph publication adds a content edge to
each task assembly's exact adjacent `.deps.json` when it has a known producer,
without requiring the file to exist, and rejects forward/cyclic producers.
Compiler children clear their environment, then retain only task-context
`SystemRoot`, `TMPDIR`, `TMP`, `TEMP` and `DOTNET_EnableDiagnostics`.
XLIFF serialization now creates its neighboring temporary file exclusively
and write-only; fresh/replacement tests and actual-task traces retain output
bytes and atomic publication without directory exemptions. An opt-in
same-family response-file provenance policy passes an actual compiler pilot;
it does not permit reading pre-existing temporary files. Exact platform files
require reviewed execution-image contracts. Such exceptions are not a
machine-independence or portable-caching proof. No blanket task/source/home/tmp
allowances or serial ordering chain were added; dependency completeness is not
yet established.

Cold selective-execution, manifest-edit and forward-producer regressions pass,
as do compiler-environment cases and C#/VB smokes. Fresh runtime construction
adds 115 missing manifest content edges, retains 114 existing ones and preserves
all other Ninja dependency sets across 5,320 actions after explicit path and
invocation normalization. A cold compiler pilot traces the selected
temp/diagnostics environment through the actual compiler launch and repeats
with no work. This is bounded validation, not a new whole-libraries audit.

A subsequent complete trace executes all 5,321 steps successfully, but analysis
fails closed with 1,331 undeclared observations: 716 account-file lookups, 614
private response-file reads and one Dataflow XML include. There are zero unresolved
events and four incomplete syscall records; the incomplete capture disables all
response-file candidate admissions.

The NuGet reader now resolves its user-settings directory only when experimental
macros are enabled. Actual manifest-task old/new traces eliminate the account
lookups without changing output bytes, and native reader tests retain enabled-macro
behavior. Dataflow declares `XmlDocs/CommonXmlDocComments.xml` as an explicit
additional compiler input rather than discovering XML includes during Restore.
Fresh construction preserves every action descriptor after explicit path
normalization and that one added input; the Ninja graph gains exactly its content
edge. An actual Dataflow compiler audit passes, while removing only that input
and edge reports exactly the XML read. Neither correction adds a new acquisition
observation or an account-file allowance.

Bounded threaded-exit probes produce unknown syscall records on both strace 6.8
and 7.3; a tracer upgrade alone does not repair the old incomplete capture.
The new opt-in `trace --seccomp-bpf` preserves the selected syscalls, requires
live startup activation evidence and rejects observed fallback/tracer errors.
It eliminates unknown records in the 120-trial same-filter probe, and actual
compiler positive/negative pilots retain response-file provenance checks.
Competing seccomp filters remain an explicit limitation, not a sandbox guarantee.
The complete follow-up audit passes all 5,321 execution steps at `-j4`, with zero
undeclared successful content reads, unresolved events, incomplete records or
directory-catalog mismatches. All 614 response-file reads meet the explicit
provenance policy; no incomplete records are waived or automatic allowances added.
Frozen app bytes, protected originals and input canonical paths remain unchanged.
Traced execution takes 908.5 seconds and analysis 1,722.9 seconds; these are not
untraced build timings. Metadata and failed probes remain separately reported,
not rejected in this non-strict profile. This validates the observed managed
libraries workload under the reviewed execution-image assumptions, not sandbox
enforcement, arbitrary inputs or portable hermeticity.

The isolated Release libraries profile sets `EnableNgenOptimization=false`
because no legacy IBC data is supplied, matching that aspect of the earlier
Debug profile. C#/VB Release optimization remains enabled. Stock Release's
external IBC-profile glob still needs a declared discovery model; the
IBC/ReadyToRun/native-PGO pipeline is not validated by this profile.

A fresh real VB shim profile constructs without products and completes all 1,448
Ninja actions, including source-built VB compilation, the shim's separate-output
rewrite/publication and 29 ApiCompat validations. The previously failing
ComponentModel.Primitives, Numerics and Reflection.Emit validations consume their
final linked producers; private System.Private.Uri is an explicit input with its
own producer. All success semaphores exist and repeated Ninja has no work. No
suppression was added or validation relaxed, and the protected cache hashes remain
unchanged. This verifies the shim dependency closure, not the whole libraries
partition.

### NuGet fact capture

- [x] Capture the complete ordered assets JSON through
  [`WriteNuGetInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteNuGetInputs.cs)
  during ordinary Restore/configured-project preparation.
- [x] Preserve unknown fields, framework/RID data, dependency/asset metadata,
  exact number text, null/empty distinctions, case, Unicode, and MSBuild escaping.
- [x] Read afresh and diagnose missing/malformed data without replacing prior facts.

SDK graph preparation defaults to
`obj/nugetinputs/<Configuration>/<Platform>/<TargetFramework>/<project>.g.props`;
explicit raw capture without the graph-input flag retains the unqualified path.
`NuGetInputsPath` overrides it and `ProjectAssetsFile` selects the source. Raw format 1 uses
`_NuGetInputsFormatVersion`, `_NuGetInputsSourceFile`, and ordered `_NuGetInput`
items. Identities are `$` plus JSON Pointer segments; `Kind`/`Value` encode nodes,
with explicit containers. Package/project binaries are not opened for raw capture.

These are underlying data, not cached outputs of one policy task. NuGet data
needed during graph construction uses this representation. Deps/runtime-config
actions may instead consume declared JSON at execution when it and their other
explicit inputs suffice; retaining those reads is not an incomplete fact migration.

### NuGet fact consumption

- [x] Validate facts and project an explicitly named assets target through pure
  [`ReadNuGetAssets`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/ReadNuGetAssets.cs).
- [x] Preserve package/project separation, future artifact paths, aliases, locale,
  RID, placeholders, inventory, and source-fact metadata.
- [x] Reject invalid facts/targets with NETSDK1253 and no partial outputs.

[`Microsoft.NET.NuGetInputs.targets`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.NuGetInputs.targets)
imports the facts in hardened inner builds. Explicit `_ReadNuGetAssets` requests
take exact `NuGetAssetsTarget` keys and return libraries, assets, dependencies,
and file inventory. The view supports assets versions 3/4 and legacy framework
keys; it does not negotiate targets, probe folders, or apply package-selection policy.

Fact projection avoids NuGet's in-memory project-spec reader because that reader
still consults ambient macro/environment and user-settings context. A packaged
fixture constructs/publishes the view with JSON and package files absent.

### Prepared SDK package selection

- [x] Select pure
  [`ResolveNuGetAssets`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/ResolveNuGetAssets.cs)
  in hardened `ResolvePackageAssets`, sharing ordinary policy via direct NuGet
  model projection and an injected resolver/item sink.
- [x] Capture selected package locations, related-file presence/absence, normalized
  locales, and compiler API identity during Restore instead of probing during Build.
- [x] Retain standard reference/analyzer/copy-local/transitive-project composition,
  package-framework replacement, and implicit-reference/version policy.
- [x] Run an offline package fixture with distinct compile/runtime assemblies and
  a genuine compatible C# generator; reject wrong-language/incompatible candidates.

Raw fact format remains 1. Separate context version `_NuGetPackageContextVersion=1`
adds `_NuGetCompilerApiVersion`, `_NuGetPackageLocation` (`PackageDirectory`,
`PackageRoot`), `_NuGetRelatedFile`, and `_NuGetLocale`. Missing/duplicate/invalid
context fails rather than falling back to discovery. Explicit raw capture can
omit discovery context with `MSBuildRestoreGraphInputs=false`.

Selection runs after deleting JSON/packages. Full graph publication restores
declared external inputs first: required-file validation is not weakened to prove
selection's purity. Eleven Ninja actions compile with the reference assembly,
run the generator, publish the implementation, and start the consumer. Runtime
implementation bytes and ordinary dependency/runtime-config bytes are checked.

Preparation selects one target framework per configured project, with zero, one
or multiple `FrameworkReference` items. Package-backed frameworks use their ordinary
build imports. Retained multi-targeting declarations are allowed, but outer
cross-targeting and RID preparation are not. Unsupported content preprocessing,
simple framework-assembly names, and package/package compile,
analyzer or runtime duplicates fail with NETSDK1254. Framework/package conflicts
are handled separately below. Ordinary package discovery/cache behavior
is unchanged. See the [package fixture](../../../../../genbuild/tests/PackageReferences.csproj).

- [ ] Extend preparation for the real configured runtime closure above.
- [ ] Migrate remaining construction-time NuGet JSON consumers to facts without changing output semantics.
- [ ] Broaden package policy only through explicit supported profiles.

### Prepared reference conflicts

- [x] Capture external version and pack conflict facts without selecting winners.
- [x] Reuse ordinary SDK conflict policy in zero-I/O graph construction.
- [x] Verify newer-package and older-package cases against ordinary SDK selection,
  then compile and start independently through Ninja.
- [x] Reject missing, malformed and unresolved facts without publishing a graph.

`_FrameworkReferenceConflictFactsVersion=1` identifies pack conflict resources.
The native framework resource path compiles platform items and package-override
relationships during acquisition; graph-time targeting selection supplies those
items and ordered preferred packages to conflict policy without reparsing pack text.
`_NuGetConflictFactsVersion=1` adds `_NuGetConflictFile` assembly/file versions for
non-placeholder package assets. Capture uses the existing portable binary-version
reader; unusual native/version-resource profiles are not established by these
managed-assembly fixtures. Older snapshots require a fresh Restore.

[`ResolvePreparedConflicts`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/ResolvePreparedConflicts.cs)
shares the ordinary `ResolvePackageFileConflicts` orchestration and winner policy
through injected, lazy prepared version providers. The hardened target runs after
`ResolveAssemblyReferences` has normalized paths, and updates references, analyzers,
copy-local files and `_ConflictPackageFiles`. It keeps direct-reference copies and
symbols aligned with selection, without treating a reference and its own RAR copy
as different competing artifacts. Explicit `HintPath` metadata round-trips but
never changes the validated reference identity.

The [offline fixture](../../../../../genbuild/tests/FrameworkPackageReferences.csproj)
selects a synthetic version-12 `System.Collections.Immutable` package over net11's
framework member, and retains the framework over a version-10 package. Selection
works with package files and assets JSON removed; full graph publication restores
and validates declared inputs before Ninja execution. Ordinary SDK reference and
copy-local selection agrees in both cases. The
[pure fixture](../../../../../genbuild/tests/PreparedConflicts.proj)
covers file versions, preferences, override thresholds, aliases, noncolliding
future producers, platform-only runtime provision and direct-reference symbols.
Missing/invalid facts and unresolved distinct-artifact ties report NETSDK1255.
Generated or explicit candidates involved in collisions need declared version
metadata or prepared external facts; no construction-time inspection is added.

For SDK project outputs,
[`GetTargetPathWithTargetPlatformMoniker`](../../../src/Tasks/Microsoft.Common.CurrentVersion.targets)
propagates versions initialized by
[`_GetHardenedProjectOutputVersions`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.GenerateAssemblyInfo.targets).
Only versions emitted through enabled SDK assembly-info attributes are inferred.
Short forms are normalized to the compiler's four-part numeric versions, without
changing the producer's properties or generated attributes. Non-SDK/custom
assembly-info producers must supply their own truthful output metadata or facts.
The [analyzer fixture](../../../../../genbuild/tests/AnalyzerProjectReference/App.csproj)
builds two future generators with equal emitted assembly versions but different
file versions, including a repeated project reference. It checks metadata and
producer identity before execution, rejects disabled generated version attributes,
then executes generated code and compares actual binary versions and ordinary
selection. Restore and construction never prebuild those tools.

## After the prototype

- [ ] Add general sandbox enforcement for task/tool/plugin reads, output/private-
  scratch writes, aliases/symlinks, network, and ambient context. Violations must
  fail, not expand declarations. The action host is not a security sandbox.
- [x] Add executor-owned current-graph cleaning with receipt-validated trees,
  nested/empty directory removal and clean/rebuild/no-work coverage.
- [ ] Add persistent cross-graph ownership for safe obsolete-output pruning;
  never infer file ownership from arbitrary directory contents.
- [ ] Integrate content caching and lazy artifact materialization independently
  of task implementations and Ninja's timestamp rules.
- [ ] Add [named products](hardened-named-products.md): catalog validation,
  producer-closure selection, and Ninja roots.
- [ ] Address generalized fetch, richer file effects/failure control, deferred
  values, graph relocation, and persistent analysis as separate work.

Deferred enforcement does not defer determinism or machine independence.
Preserve the distinction between engine graph-input discovery, zero-I/O
construction, and independently executed file transformations. See the
[sandbox requirements](hardened-static-graph.md#sandbox-enforcement-deferred).
