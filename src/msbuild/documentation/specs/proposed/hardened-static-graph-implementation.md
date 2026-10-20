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
lives in `src/nuget-client`: the standalone app builds the NuGet task/dependency
closure and stages the matching targets from that source tree.
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
NuGet's managed dependency closure is source-built using its existing .NET 10
target; the compiler and other external tool dependencies remain pinned by
`src/sdk/eng/Version.Details.props`. The additional dependency manifest makes
NuGet available without the SDK driver's runtime manifest.

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

The Unix slice supports intrinsic cross-project `MSBuild` routing through the
ordinary engine with static parameters. Configured projects share one graph,
published only after the root succeeds. Static target returns use the ordinary
return-gathering implementation, and configured-target state survives metadata
queries followed by Build requests. Construction currently requires one node
and no multi-threaded mode. Forward producer resolution, Windows commands,
full tool closures and graph-input inventories remain work rather than
supported fallbacks. Provably empty project lists remain a no-action base case.

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
unmanaged-registration-cache presence, framework
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
discovery, `TargetDir` computation, the unmanaged-cache import, and assembly-folder
initialization. The SDK omits optional target-framework telemetry and the
preview-SDK banner during hardened graph construction because neither
produces build artifacts; ordinary Restore and Build retain their behavior.
The coordinated SDK's ordinary Restore prepares the required imports/marker
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
the invocation and before executing the task. Common's participating directory
preparation sites accumulate zero-I/O requirements instead of running
`MakeDir` during construction. Each requested directory must be covered by a
declared file output beneath it before the graph can be published. Unrelated
empty-directory effects remain unsupported, even if the directory already
exists. There are no directory actions or stamps; ordinary `MakeDir` behavior
is unchanged.

`MSBuild` calls with statically known parameters now use normal binding,
configuration requests and output mappings during construction. Empty project
lists remain no-ops. Nonempty lists expand child targets into the shared graph,
not independent nested-build actions. Tests cover distinct configurations,
additional/removed global properties, caller/source metadata, rebased paths,
`Returns`/legacy `Outputs` and duplicate rules, diamond producer reuse, skipped
target reexecution, and failure without partial publication. Root and child
requests must retain the same destination, hosts, host OS and selected action
environment. Unavailable parameters remain rejected, as do unaudited custom
tasks named `MSBuild`, even with explicit empty declarations.

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

The assembly-info, compile-input, and runtime-config wrappers now omit their
`Hash` computations, timestamp-cache writes, cache-only input registrations,
and cleanup bookkeeping during hardened construction. Their semantic
prerequisites still run; ordinary builds retain their existing caches.
`Hash` itself is not admitted merely to preserve this incremental bookkeeping.
Packaged smoke verifies cache-free construction from clean and stale outputs,
including obstructing cache directories, retained resource/reference/platform
preparation and caller inputs, independent assembly-info publication with
ordinary-identical bytes, and ordinary cache creation.

Roslyn's `MapSourceRoots` and the packaged GitHub, GitLab, Azure Repos, and
Bitbucket SourceLink URL tasks now run as audited zero-I/O construction tasks.
They derive mapped paths and URLs from prepared metadata and captured project
context, without requiring source directories or querying source control.
Hardened SourceLink generation shares the ordinary JSON serializer through
`FormatSourceLink` and publishes its exact text through overwrite-only
`WriteSourceLink`. Compiler and cleanup paths are static. Empty mappings clear
the compiler property and emit no file action, leaving stale JSON unreferenced.
Ordinary builds retain their comparison and deletion behavior.
Packaged smoke checks all four providers, explicit URL preservation, relative
and nested roots, duplicate merging, deterministic/non-deterministic paths,
legacy host versions, exact ordinary-compatible bytes, empty/stale outputs,
invalid metadata rejection without partial graphs, and fatal publication errors.

Hardened C# compilation now composes zero-I/O `FormatCsc` and independent
`CompileCsc`, reusing Csc option formatting without its server/IDE/tool discovery.
Restore captures the compiler and analyzer dependency closure as explicit
`cscinputs` imports. Compilation requires determinism and automatic path mapping
consistent with SourceLink, covering project, generated-source and output paths.
Analyzers/generators and dependencies are declared inputs under their nominal
compiler-input-only contract, with enforcement deferred and no per-plugin gate.
Packaged smoke independently executes generated-source and Regex-generator
compilation after MSBuild exits. Relocated checkouts produce identical PE,
reference-assembly and portable-PDB bytes; regressions inspect PDB document,
CodeView and CallerFilePath values. Embedded debug information, ordinary Csc,
rejected construction modes and independent compiler failures are covered.
The action retains ToolTask diagnostics, cancellation and temporary response-file
scratch without compiler servers or task-local output caches. Native PDBs,
arbitrary response files, data-dependent generated-files directories, rulesets,
SARIF, app-config and machine signing modes remain outside this initial profile.

Hardened SDK Build now selects `WriteDepsFile`, reusing the dependency model and
JSON writer without ordinary GenerateDepsFile's object/timestamp caches,
ambient reference-root discovery or project-output fallback probes. Assets
and conditional self-contained RID JSON are declared reads; runtime binary
versions remain execution-time reads after their producers. Main identity,
satellite paths and resolved destinations are metadata, with relative logical
paths required in JSON. Explicit project-reference outputs work even when the
project and assembly names differ. The output is written without reading its
previous contents, and `FileWrites` stays statically available.

The portable version policy reads managed metadata on every OS and fixed
embedded version resources for native PE files, rejecting conflicting resources.
Missing version information and other native formats use `0.0.0.0`; missing
files and unsupported/malformed versions fail explicitly. No OS-specific
FileVersionInfo, installation resources or timestamp cache participates.
Ordinary GenerateDepsFile, Publish and designer target selection are unchanged.
The standalone package stages the pinned SDK-driver dependencies beside the
task assembly for independent loading without ambient probing. Packaged smoke
checks a compiler-produced dependency, relocation, byte-compatible ordinary
JSON, stale replacement, static bookkeeping and missing-input failure.

Hardened runtime configuration now uses `WriteRuntimeConfig`, sharing the
ordinary framework/template/host-option model and serializer. Assets and the
selected template are declared reads, without the ordinary build-engine cache
or optional-template execution probe. Main and optional dev paths are exact
outputs with static `FileWrites`; Debug's Hot Reload dev file is retained.
Both documents are prepared and validated before either is written, without
reading previous outputs. This is not atomic multi-file publication on I/O
failure. Output uses UTF-8 without BOM and fixed LF newlines.

The engine evaluates optional default-template presence afresh; it is not a
persisted Restore fact. Explicit template paths, including earlier target
assignments and generated templates, are required inputs and retain producer
ordering. The action has no missing-template fallback. Relative probing paths
are normalized to forward slashes without trailing separators, including raw
template overrides. Absolute/invalid probes fail instead of publishing
machine-specific NuGet-cache paths. Ordinary Build, Publish and designer paths
retain their prior behavior. Packaged tests cover main/dev and Release output,
framework-dependent/self-contained configuration, existing/missing/generated
templates, late selection, relocation, stale replacement and independent errors.

Common's main assembly, reference-assembly and PDB publication now selects
content-only `CopyFiles` actions. Equal-length explicit source/destination
vectors must match their declarations; outputs must be unique and disjoint
from inputs. Source fan-out is supported. Paths use captured project context,
and the host prepares output parents. The task copies bytes without source
timestamp/attribute propagation, old-output reads, size/timestamp/MVID skips,
retries, link modes or execution-time MSBuild returns. Explicit link and
read-only override requests fail rather than being silently ignored.

`MainAssembly`, `ReferenceAssembly` and `FileWrites` are static, with source
item metadata and copy opt-outs retained. Hardened mode omits the
copy-up-to-date marker, its fingerprint action, cleanup registration and
project-reference protocol metadata; it is incremental bookkeeping, not
product data. Ordinary Copy/CopyRefAssembly and marker behavior are unchanged.
Packaged tests cover compiler-to-copy ordering, relocated/ordinary byte
equivalence from identical inputs, stale replacement, renamed reference
outputs, static metadata, disabled products/symbols, rejected modes and fatal
independent publication errors. Copy-local implementation/symbol publication
uses the same task as described below; other Copy families remain unadmitted.

Automatic cleanup is now ordinary-only: hardened Build omits IncrementalClean,
prior/current clean-ledger collection and `_CleanRecordFileWrites`, including
their cleanup-only dependencies. Restore no longer snapshots the ledger and
Build does not import obsolete clean snapshots. Product FileWrites and
semantic producers remain intact. Packaged cases verify missing/stale/obstructing
ledgers, independent product publication without cleanup actions, preserved
stale/unowned files and ordinary deletion/ledger reconciliation. Restore
process tests cover single/multiple frameworks without a clean snapshot.

Cleaning current graph outputs and safely pruning obsolete owned outputs belong
to the executor. Neither is implemented here; pruning needs persistent ownership
records rather than arbitrary directory deletion. Explicit MSBuild Clean/Rebuild
and arbitrary semantic Delete actions remain unsupported.

With directory import paths and construction host OS supplied explicitly to both Restore and Build,
the fresh SDK full-`Build` probe now gets past `GetTargetFrameworks`,
executable-reference validation, WinMD validation, editorconfig generation,
framework attribute generation, assembly version derivation, source-root mapping,
SourceLink generation and compiler preparation. There are no remaining hash,
deferred-hash, source-root mapping, SourceLink, compiler, deps, runtime-config,
assembly/reference/PDB copy or deferred `MainAssembly` diagnostics in this probe.
The bounded SDK hello-world Build now emits ten independent actions without
writing product payloads. Packaged smoke verifies construction even with an
obstructing ledger directory and a malformed obsolete clean snapshot.
Target-time property assignment now preserves escaped values, matching ordinary
MSBuild until item/task binding. Regressions cover literal semicolons, literal
percent escapes, actual list separators and chained property assignments.
The framework-attribute fixture uses the shipped C# text-producing target and
compares independent publication with ordinary source bytes.
Independent SDK execution now completes all ten actions, including compilation
and final assembly/reference/PDB and runtime-manifest publication. Packaged smoke
executes that graph, checks its final outputs and runs the resulting program on
the external runtime; the direct packaged-compiler smoke remains separate.
Action capture preserves ordinary empty-scalar omission rather than assigning
empty strings. Independent tasks retain null and constructor defaults, nonempty
escaped values still bind, and empty required scalars fail during construction
just as in ordinary builds. Process regressions compare ordinary and independent
output bytes. Default runtime configuration now matches ordinary generation
without an invalid empty `rollForward` setting; main/dev JSON equality and SDK
program startup are passing smoke cases.
Other diagnostic invocations need explicit declarations as their target
closures are admitted. The SDK's satellite-assembly target also retains an
unadapted `MakeDir`; the structural-directory profile currently covers
Common's preparation sites, not every SDK or custom directory operation.
Other copy families remain outside the admitted profile. Earlier task-rejection
chains should not be treated as independently reachable blockers now that this
bounded graph constructs successfully.
Broader reference and compiler option profiles still need audited
action-graph adaptation. Broader SDK `Build` profiles remain unsupported; ambient
discovery has not been allowed as a fallback. The bounded project-reference
closure below completes this execution milestone; broadening the `Build`
lifecycle remains separate work.

Use existing SDK overlays for a single-framework C# hello-world profile:

```text
construction -> generated source -> compiler -> final output copy
```

Complete restore/fetch before fresh graph generation. Supply exact compiler,
reference, analyzer, generator, and other tool inputs. Start with a supported
compile closure, then extend toward `Build` without weakening the contract.
Use a two-project fixture with static child returns and configured-project
identity; child artifacts flow through file edges, not nested engine calls.

The bounded single-project and SDK executable/library Build graphs and startup
are implemented. The packaged cross-project action fixture independently
executes child and root producers, and the real SDK fixture verifies Restore
preparation and construction across the closure without prebuilding binaries.
Broader SDK project-reference profiles remain unsupported. Project configuration
and child framework XML formatting
are now audited zero-I/O construction tasks. Child `GetTargetFrameworks` metadata
matches ordinary execution, including item-definition defaults and sequential
metadata assignments on new items; updates of existing items retain their
ordinary pre-update metadata view. Missing requested project files fail through
intrinsic engine requests (`MSB3202`) instead of ordinary existence-based skipping.

The standalone toolset now builds NuGet tasks and their managed dependency closure
from the VMR's NuGet projects, constrained to their existing .NET 10 target.
Targets and managed binaries come from the same source tree, and the same
dependency files are staged beside independently hosted SDK tasks. NuGet's
nearest-framework task is admitted with explicit zero-I/O declarations, retaining
ordinary alias, platform and fallback selection and diagnostics. Its compatibility
marker is recognized by namespace/name as specified by the task contract; no
engine admission allowlist or duplicate selection algorithm is used. Only the
hardened Common invocation passes declaration parameters, so ordinary calls still
work with older NuGet task assemblies.

Hardened `GetNativeManifest` skips the ordinary output existence query when no
manifest generation is requested, rather than adopting a stale placeholder.
Requested ClickOnce, Appx, native-reference and isolated-COM discovery remains
unsupported; ordinary behavior is unchanged.

Intrinsic project requests use ordinary parameter binding, without file-action
capture. In particular, empty `Targets` leaves the parameter unset, preserving
default-target outputs. Capturing it as an empty array previously constructed
child actions but silently dropped their returned artifacts.

Hardened `ResolveAssemblyReferences` now selects zero-I/O `ResolveReferencePaths`
over prepared references and explicit project outputs. It normalizes paths
against captured project context and preserves metadata without opening
assemblies, checking existence or discovering dependencies. Simple/display
assembly names fail even with `HintPath`; search tokens, wildcards and invalid
paths also fail before graph publication. The existing `AssemblySearchPaths`
guard remains, but its permitted settings do not enable lookup. Stock RAR
remains ordinary-only, with no ambient discovery or name-resolution fallback.
Callers must supply the complete reference closure; dependency discovery,
version unification and related-file/satellite discovery are not implemented.

Child protocol metadata supplies an absolute compile `ReferenceAssembly` and,
when symbols are published, `DebugSymbolsFile`. Compilation uses the reference
assembly while runtime publication consumes the implementation DLL and symbols.
Copy-local uses explicit content-only `CopyFiles` actions, preserves destination
subdirectories and static `FileWritesShareable`, honors private/framework/copy
metadata and the common-output opt-out, and rejects link/read-only modes.
Ordinary RAR, copying and project metadata remain unchanged.

Packaged smoke constructs the SDK executable/library graph from absent binaries,
executes all twenty actions after MSBuild exits, and starts the executable with
its renamed library. Copied DLL/PDB bytes match their producers; the dependency
manifest matches ordinary Build. Shipped-target cases cover generated paths,
compile/runtime separation, metadata and opt-outs, stale replacement, invalid
names/options without partial publication, and fatal independent copy failure.
The ordinary comparison uses a fresh compiler process, not a potentially
incompatible shared server.

**Bounded execution milestone complete:** Ninja builds the two-project closure
from clean outputs after MSBuild exits. Broader dependency, platform and
parallelism profiles remain separate work.

## 6. Exercise dotnet/runtime

After SDK compilation works, select one bounded managed library under
`~/code/runtime/src/libraries` and construct its compile closure. Complete
restore and prerequisite tool/reference builds beforehand. Use real source
generation and compiler actions, then expand to managed project-reference
closures. Treat native runtime/CMake integration as a separate later boundary,
not a prerequisite for the first managed-library result.

## After the prototype

General sandbox enforcement is deliberately deferred. Current admission trusts
task contracts and analyzers'/generators' nominal compiler-input-only contract;
it does not require a per-plugin audit/allowlist, and the action host is not a
security sandbox. This delay does not weaken DeclaredIO's determinism and
machine-independence requirements. Future enforcement must cover declared-only
reads, output/private-scratch writes, transitive tool/plugin access, network and
ambient context, and fail explicitly on violations. It must preserve the
distinction between engine graph-input discovery and zero-I/O task execution.
See [sandbox requirements](hardened-static-graph.md#sandbox-enforcement-deferred);
confinement complements, rather than replaces, determinism and relocation checks.

[Named products](hardened-named-products.md) can add catalog validation,
producer-closure selection, and Ninja roots. Content caching and lazy artifact
materialization need separate standalone integration; Ninja timestamps are
not that cache. Generalized fetch, richer file effects and failure control,
deferred values, relocation, and persistent analysis remain separate work.
