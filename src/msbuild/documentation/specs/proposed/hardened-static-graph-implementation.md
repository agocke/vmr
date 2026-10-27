# Standalone action-graph prototype roadmap

MSBuild constructs concrete work; Ninja schedules independent task hosts.
This is the implementation checklist for the
[action-graph contract](hardened-static-graph.md), not a general SDK-support claim.
Checked items describe implemented, exercised behavior. Unchecked items are
outstanding work. Use the [conversion cookbook](hardened-static-graph-migration.md)
for task/target adaptation patterns.

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

Restore is deliberately unrestricted, trusted preparation. With
`MSBuildRestoreGraphInputs=true`, it restores packages and captures discovery
results, framework references, repository metadata, and compiler/analyzer inputs.
It does not prebuild the selected product to discover its outputs. Restore and
Build must use the same configuration, framework selection, and intermediate paths.

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
provides scheduling and timestamp-based incrementality, not all of those services.

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

`genbuild/genbuild.proj` builds into `artifacts/bin/genbuild/Debug`. It does not
build SDK redist, templates, workloads, the SDK CLI driver, or a runtime. With
MSBuild's pinned .NET 11 SDK on `PATH`, build from `src/msbuild`:

```sh
dotnet msbuild ../../genbuild/genbuild.proj -m:1 -nr:false -bl:{}
```

`GenBuildBootstrapSdk` and `GenBuildDotNetHost` select explicit build-time paths.
`GenBuildSkipComponentBuild=true` reassembles already-built components; the default
builds them from this VMR. The old composite-SDK script is not the entry point.

From the VMR root, run with an external .NET 11 runtime:

```sh
dotnet artifacts/bin/genbuild/Debug/genbuild.dll restore path/Project.csproj -bl:{}
dotnet artifacts/bin/genbuild/Debug/genbuild.dll build path/Project.csproj -bl:{}
ninja -C path -f obj/genbuild/build.ninja
```

`restore` performs ordinary preparation. `build` starts a separate hardened
MSBuild process and emits the graph, not product files. Extra arguments are
forwarded to MSBuild; `MSBuildEmitActionGraph` changes the graph destination.
Explicit directory-import paths and `MSBuildGraphHostOS` must be consistent
between preparation and construction where the profile requires them.

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

- [x] Compose pure `FormatCsc` with independent `CompileCsc`, reusing option
  formatting without ordinary Csc's server, IDE host, or discovery paths.
- [x] Capture compiler/analyzer dependency closures during Restore as `cscinputs`;
  keep later tool/documentation/module inputs explicit.
- [x] Require deterministic compilation and automatic stable path maps consistent
  with SourceLink, covering project, generated-source, and output paths.
- [x] Declare analyzers/generators, dependencies, configs, and additional files;
  run real generators without an audit allowlist or opt-in gate.
- [x] Retain ToolTask diagnostics, cancellation, and private response-file scratch;
  use a controlled compiler environment and no server or task-local output cache.
- [x] Prove relocated PE/reference-assembly/portable-PDB byte equality and inspect
  PDB documents, CodeView paths, and `CallerFilePath`; cover embedded debug info.

Native PDBs, arbitrary response files, data-dependent generated-files directories,
rulesets, SARIF, app-config, machine signing, and execution-mode overrides remain
outside the initial profile. Inputs use explicit paths, not SDK/LIB/default
response-file searches. Plugin I/O enforcement is deferred like other task I/O.

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

These actions still read declared NuGet JSON at execution. Moving every consumed
field to prepared facts remains an explicit follow-up, not a completed milestone.

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

The next product milestone is `System.Collections.Immutable` with
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
- [ ] Construct a fresh real-library graph without product binaries.
- [ ] Execute independently with Ninja and verify output/ordinary compatibility.
- [ ] Add focused library behavior checks with their genuine test prerequisites.

Configured preparation is exercised by the
[packaged fixture](../../../../../genbuild/tests/ConfiguredPreparation.csproj):
a Debug/net11 parent selects its Release/net10 child while retaining both projects'
`TargetFrameworks`. Separate Debug/net10 and Release/net11 preparations preserve
framework, NuGet and compiler snapshots. The resulting parent/child graph
compiles independently and starts without prebuilt project binaries.
Outer preparation still fails, and metadata-only outer evaluations used for
framework negotiation do not import inner-build snapshots.

Each selected configuration gets an ordinary Restore without an inherited global
`TargetFramework`, followed by a distinct capture evaluation that imports the
newly restored package targets. Capture explicitly clears `MSBuildIsRestoring`;
otherwise Common suppresses NuGet-generated props/targets and their capture hooks.
The preparation marker and product/tool build guard remain enabled. Local snapshot capture precedes
`PrepareProjectReferences`; its negotiated items, including additional/removed
global-property metadata, drive recursive child preparation. This avoids treating
NuGet's path-only restore closure as the configured Build closure.
Framework metadata requests reached during that negotiation also receive ordinary
Restore and fresh local capture, without recursively selecting their compilation
references. Metadata requests drop the capture marker so later selected-framework
capture cannot reuse their pre-Restore evaluations. Capture coverage includes
discovery, not just compilation requests.
SDK snapshots distinguish `Configuration/Platform/TargetFramework`; other
input-affecting global properties require separate intermediate paths.
Framework runtime facts are captured after targeting-pack resolution filters out
unused frameworks. Standard preparation rejects `PrepareForBuild`/`CoreCompile`
instead of prebuilding products or tools.

The real `System.Collections.Immutable` probe now passes the retained-multi-targeting
guard and the live-tool prebuild dependency, but its closure is not enabled. Runtime's
[`FilterImplicitAssemblyReferences`](../../../../runtime/Directory.Build.targets)
runs before `ResolveAssemblyReferences`, after pack candidates and project-output
metadata are available. Framework capture retains unfiltered reference/analyzer
candidates; replayed `ReferencePath` items participate in the same runtime filter.
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
Framework capture selects pack-owned references/analyzers by `FrameworkReferenceName`;
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
contribute items scoped to the configured target framework. Changed framework-reference
identity sets fail with NETSDK1255 and require a fresh Restore.
Windows SDK normalization is imported only for Windows platform authoring, preserving
ordinary normalization without validating unused Windows-only expressions elsewhere.

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
Framework, NuGet, compiler, and enabled source-control imports remain required.
Restore captures the existing inner-framework metadata requests reached during
reference discovery, including inspected frameworks not selected for compilation.
This replaces the optional-import workaround rather than relying on incomplete
capture. Hardened SDK evaluation defaults `EnableFrameworkPathOverride=false`
so .NET Framework metadata probes do not discover framework installations.
The [packaged fixture](../../../../../genbuild/tests/DiscoveryPreparation.csproj)
requires inputs for all four inspected frameworks, compares ordinary and hardened
metadata, and independently compiles and starts only the negotiated closure.
It also requires capture to import a property file generated by the child's
Restore, covering both discovery capture and subsequent selected-framework capture.
Removing each kind of discovered net10 input fails its required import without
partial graph publication or action execution.
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
`ChooseBestP2PTargetFrameworkFromInputs`. Configured preparation captures the
selected runtime-graph JSON, not project-reference winners. Required
`targetframeworkinputs` imports supply that data during construction; the existing
NuGet matching algorithm still selects among current framework/platform and
reference metadata. `SetRidAgnosticValueForProjects` already has an audited
empty-footprint implementation; its hardened call now supplies both declarations.
The [packaged fixtures](../../../../../genbuild/tests/ArcadeTargetFrameworkInputs.proj)
compare ordinary and hardened metadata, runtime/platform fallback, omission,
explicit framework overrides, configured queries and literal JSON. They also
cover missing/incompatible inputs, replay with the original JSON absent, and
[automatic capture through real NuGet package imports](../../../../../genbuild/tests/ArcadeTargetFrameworkPackage.csproj).
Both the ordinary Arcade task project and selected-source component build, and
the complete SDK-free packaged smoke passes.

The real probe explicitly selects an isolated local task package and the
prototype's packaged `CSharpCoreTargetsPath`. Without that compiler-target
binding, runtime's compiler package replaces the prototype integration authoring.
Restore now captures five Arcade input files and produces zero DLLs. Build runs
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
The standalone build includes the original Roslyn generator and a source-built
analyzer payload for its regression. A controlled release fixture exercises the
full generator, checks every emitted config against the declared inventory,
constructs with config files absent, and independently publishes metadata
matching ordinary execution. Empty/unsupported versions and opt-out behavior are
preserved; custom-directory overrides fail without a partial graph.
The real probe uses source-generated authoring with all 600 original analyzer
package config assets declared. A normally restored isolated package preserves
the original binary/config payload; the shared cache is unchanged.
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
The real probe gets past analyzer-config selection, provider translation,
mirror remapping, the marker hook, the empty resource-source batch and unrequested
GenAPI discovery, then passes ILLink's resource naming, resource-action construction
and IVT action construction.
Construction advances through the ILLink analyzer, code-fix and linker reference
projects. Its first remaining error is the Banned API analyzer package's
`AddGlobalAnalyzerConfigForPackage_MicrosoftCodeAnalysisBannedApiAnalyzers` target:
global-config selection uses `Exists` during construction (`MSB4287`).
Later target-validation diagnostics are not yet isolated as independent blockers.
The runtime product directory contains zero DLLs, `.resources`, or generated sources.
Preparation and the failed graph attempt publish no library graph or action directory.
No runtime library graph, compilation, or independent execution is proven.
Native/CoreCLR/CMake integration is later.

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

These are underlying data, not cached outputs of one policy task. All consumed
NuGet JSON must eventually use this representation; deps/runtime-config actions
still reading JSON are an acknowledged intermediate state.

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
- [ ] Migrate remaining NuGet JSON consumers to facts without changing output semantics.
- [ ] Broaden package policy only through explicit supported profiles.

### Prepared reference conflicts

- [x] Capture external version and pack conflict facts without selecting winners.
- [x] Reuse ordinary SDK conflict policy in zero-I/O graph construction.
- [x] Verify newer-package and older-package cases against ordinary SDK selection,
  then compile and start independently through Ninja.
- [x] Reject missing, malformed and unresolved facts without publishing a graph.

`_FrameworkReferenceConflictFactsVersion=1` adds parsed platform items, pack-owned
override rules and ordered preferred packages to framework snapshots.
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

## After the prototype

- [ ] Add general sandbox enforcement for task/tool/plugin reads, output/private-
  scratch writes, aliases/symlinks, network, and ambient context. Violations must
  fail, not expand declarations. The action host is not a security sandbox.
- [ ] Add executor-owned cleaning and persistent ownership for safe obsolete-
  output pruning; never infer ownership from arbitrary directory contents.
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
