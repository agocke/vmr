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
- [ ] Capture/replay exact package-SDK import bindings for supported Build profiles.

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

`HardenedTaskClassificationResolver` checks implementation annotations;
`HardenedTargetValidator` reuses the existing construction walk and callback.
`HardenedDeclaredIOFootprint` describes paths; executable actions additionally
capture parameters and implementation/context. No second interpreter is added.

## 3. Emit a concrete Ninja graph

- [x] Capture actions per concrete task bucket, including configured-project
  identity, bound parameters, declarations, host/tool context, and source location.
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
- [x] Capture construction host OS in `MSBuildGraphHostOS`; normalize `TargetDir`
  against the explicit project directory rather than ambient CWD.
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
- [ ] Prepare selected configured inner builds despite retained `TargetFrameworks`,
  preserving child framework negotiation and per-configuration inputs.
- [ ] Capture/replay package-SDK import bindings without Build-time searches/downloads.
- [ ] Reprobe and support the actual compiler/API-tool and analyzer requirements,
  keeping generated tools connected to their producers.
- [ ] Construct a fresh real-library graph without product binaries.
- [ ] Execute independently with Ninja and verify output/ordinary compatibility.
- [ ] Add focused library behavior checks with their genuine test prerequisites.

The earlier preparation probe failed with NETSDK1247 on retained multi-targeting
and compiler/API-tool package references. Bounded package support removes the
package rejection, but the configured runtime closure has not been reprobed or
enabled. No runtime library graph, compilation, or independent execution is proven.
Do not prebuild the selected library, remove real references/analyzers, or weaken
validation to complete this milestone. Native/CoreCLR/CMake integration is later.

### NuGet fact capture

- [x] Capture the complete ordered assets JSON through
  [`WriteNuGetInputs`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteNuGetInputs.cs)
  during ordinary Restore/configured-project preparation.
- [x] Preserve unknown fields, framework/RID data, dependency/asset metadata,
  exact number text, null/empty distinctions, case, Unicode, and MSBuild escaping.
- [x] Read afresh and diagnose missing/malformed data without replacing prior facts.

The default import is `obj/nugetinputs/<project>.g.props`; `NuGetInputsPath`
overrides it and `ProjectAssetsFile` selects the source. Raw format 1 uses
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

The supported preparation profile remains single-framework `Microsoft.NETCore.App`,
without RID or retained multi-targeting. Unsupported content preprocessing,
additional frameworks, simple framework-assembly names, and conflicts requiring
assembly inspection fail with NETSDK1254. Ordinary package discovery/cache behavior
is unchanged. See the [package fixture](../../../../../genbuild/tests/PackageReferences.csproj).

- [ ] Extend preparation for the real configured runtime closure above.
- [ ] Migrate remaining NuGet JSON consumers to facts without changing output semantics.
- [ ] Broaden package policy only through explicit supported profiles.

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
