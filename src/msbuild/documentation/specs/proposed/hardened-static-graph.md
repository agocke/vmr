# Hardened action graph

## Purpose and status

MSBuild becomes a graph compiler: it evaluates projects and target bodies,
writes `build.ninja` and concrete task-invocation manifests, and exits. Ninja
then launches independent task-host console processes. Execution requires
neither the MSBuild engine nor another target interpreter or custom scheduler.

This is experimental, opt-in behavior. Hardened mode requires graph emission
and never falls back to ordinary target execution. A first graph-only path
emits single-project Ninja bundles for the supported subset. Existing
builds remain unchanged outside the opt-in graph/Restore workflow. The
[roadmap](hardened-static-graph-implementation.md) describes the implementation.
For practical migration patterns, see the
[conversion cookbook](hardened-static-graph-migration.md).

## Pipeline

1. Run ordinary Restore with `MSBuildRestoreGraphInputs=true` to complete
   restore/fetch and capture the known discovery and cache inputs.
2. In a fresh evaluation, construct the complete requested target closure,
   executing only zero-I/O task invocations.
3. Validate and publish the Ninja graph and invocation manifests, then exit
   without ordinary target execution.
4. Run Ninja separately to execute the actions.

The current prototype uses global properties; all three paths are absolute.
Restore must have completed before this invocation:

```sh
msbuild Demo.proj -target:Restore -p:MSBuildRestoreGraphInputs=true
msbuild Demo.proj --hardened-graph -target:Build \
  -p:MSBuildEmitActionGraph=/workspace/out/build.ninja \
  -p:MSBuildActionTaskHost=/tools/MSBuild.ActionTaskHost.dll \
  -p:MSBuildActionDotNetHost=/tools/dotnet
ninja -f /workspace/out/build.ninja
```

Graph generation must work from clean task outputs. Recording an ordinary
build after executing its tasks does not satisfy this contract.

For this prototype, Restore is deliberately unrestricted, trusted
preparation. It runs without `--hardened-graph`; only the subsequent Build
evaluation constructs an action graph. Use the same configuration,
framework/platform selection, and intermediate paths for both invocations.
Making Restore itself a standalone action graph is deferred.

### Entry requests and expansion

Given explicit entry requests (project, global properties, and targets),
Restore-time input capture must cover the transitive closure of configured
requests needed to construct them, including requests used to discover that
closure. Unsupported requests must be rejected explicitly, not silently omitted.

An outer multi-target build dispatches a separate graph construction for each
selected target framework. For example, `net8.0;net10.0;net11.0` means three
selected inner-build requests and three graphs, not one combined graph. Each
graph contains its own negotiated project-reference closure, which may include
dependencies targeting other compatible frameworks. An explicitly supplied
`TargetFramework` selects one such inner build. The prototype currently requires
selected inner builds; automatic outer-build graph dispatch remains a follow-up.

`Configuration`, `Platform`, and runtime identifiers use their supplied values or
authored defaults. Enumerating possible values does not itself request a
cross-product of builds. Further expansion requires explicit authored dispatch.
Project-reference property overrides and metadata requests remain part of the
reachable request closure. Inspecting another framework's metadata does not
implicitly request its compilation.

NuGet JSON data that influences graph construction must be prepared as MSBuild
item/property facts during Restore. This is the eventual representation for
**all consumed NuGet JSON data**, not just a snapshot of a task's selected
outputs. Preserve the underlying framework/RID, dependency, asset and metadata
facts so configuration-dependent selection remains a pure construction-time
operation. Files produced by project references remain future artifact paths
with producer edges; preparation must not require those binaries to exist.
The [initial fact capture](hardened-static-graph-implementation.md#nuget-fact-capture)
does not yet replace every existing JSON consumer.

### Configured framework discovery

The SDK profile captures inputs for the existing
`GetTargetFrameworksWithPlatformForSingleTargetFramework` requests reached during
Restore-time project-reference discovery, including frameworks not selected for
compilation. Those requests preserve ordinary property overrides and removals;
they do not enumerate additional configurations or recursively request compilation
of their project references. Selected compilation references retain their
separate recursive capture path.

Each reached configuration receives ordinary Restore and a fresh capture
evaluation with restored imports. Metadata requests do not inherit the capture
evaluation marker, so a subsequent selected-framework capture cannot reuse an
evaluation created before that project's Restore.

Hardened evaluation requires the explicitly named framework, NuGet, compiler,
and enabled source-control imports for every reached inner build. Missing
discovered inputs fail their imports without publishing a partial graph.
Do not make those imports optional to conceal an incomplete capture closure,
or add a metadata-only evaluation mode. Capturing a framework's facts does not
request its compilation.

Hardened SDK evaluation also defaults `EnableFrameworkPathOverride=false`,
including for .NET Framework metadata probes. The legacy framework-installation
lookup is not an alternative to prepared build inputs.

## One task contract

An audited task uses `[MSBuildDeclaredIOTask]` and explicitly supplies both
`DeclaredInputs` and `DeclaredOutputs`: finite, exact filesystem path lists,
separate from its operational parameters. An empty value means an empty list;
an omitted declaration is an error.

Removing the nonempty-footprint invocations leaves the pure construction
language: property/item computations and audited zero-I/O tasks assemble
concrete actions. No file action runs to discover its own parameters or later
work. Apart from writing the graph bundle, construction has no output effects
under this contract. A future sandbox can verify the contract; dynamic
side-effect tracing is not required for graph emission. Engine-managed reads
of projects, imports, and source globs remain graph-input discovery.

The contract asserts deterministic results from explicit parameters, declared
input contents, and fixed implementation/host context. It excludes undeclared
filesystem, network, clock, environment, or other ambient observations,
including observations made by loaded analyzers, generators, or tools.
Annotations are trusted assertions, not a sandbox or an implementation proof.

Treatment is per invocation:

| Declared filesystem footprint | Treatment with known parameters |
| --- | --- |
| Both lists empty | Audit the implementation, bind, and execute during construction |
| Either list nonempty | Capture an opaque action; audit and bind at execution |
| Missing or invalid declarations | Reject graph generation |

Construction does not load, instantiate, or reflect over a nonempty-footprint
task. Its existing `UsingTask` registration supplies the assembly artifact and
task name; no additional registration annotation is needed. The standalone
executor checks the implementation's declared-I/O contract and parameter types
before `Execute`. Invalid implementation contracts and type-dependent binding
errors therefore fail execution, not construction. Zero-I/O invocations still
need an available, audited implementation before they can run during construction.

Pure is the zero-I/O case, not a separate classification or annotation.
Zero-I/O tasks may still accept values and return properties, items, and
metadata through ordinary `<Output>` mappings. Their results are available
to subsequent construction. Fixed host context includes the configured
project directory and path semantics, not an ambient current directory.

Explicit fail-fast `ContinueOnError` values, such as `false` and `ErrorAndStop`,
are accepted after static expansion. Values that continue after failure,
invalid values, and unavailable control expressions are rejected. An explicitly
empty value is not equivalent to omitting the attribute. Active `OnError`
handlers and task-host overrides remain unsupported. An `OnError` handler may
be skipped only when its condition is provably false across the target's
possible failure states.

Common's `CoreBuild` uses an explicit fail-fast profile in hardened mode:
failure does not run post-build events or update the clean-file ledger with
partial writes. Automatic incremental cleanup and clean-ledger bookkeeping are
also omitted on success; ordinary MSBuild retains its cleanup and failure
handlers. A nonempty `PostBuildEvent` with
`RunPostBuildEvent=Always` or `OnOutputUpdated` fails with MSB3670 rather than
silently losing failure-time behavior. Use `OnBuildSuccess` or no event.
This profile does not implement general failure-path execution.

Cleaning is execution-system functionality, not a file-producing build action.
Hardened Build omits `IncrementalClean`, `_CleanGetCurrentAndPriorFileWrites`
and `_CleanRecordFileWrites`, without reading, replaying or updating the legacy
clean ledger. Product `FileWrites` and semantic producer prerequisites remain
available independently of that bookkeeping.

A future executor can clean the current graph's declared outputs; safely
pruning outputs absent from a later graph requires persistent output ownership.
It must not infer ownership from arbitrary directory contents or delete
unowned files. This functionality is deferred: current execution leaves stale
outputs untouched. Explicit MSBuild Clean/Rebuild and arbitrary semantic
Delete operations are not supported by omitting automatic cleanup.

File-I/O actions cannot return MSBuild values in the prototype. A generator's
output path, for example, must be established through static items or
properties and passed to both generator and compiler. Construction knows the
path; execution supplies the contents. Only zero-I/O invocations may use
ordinary task `<Output>` mappings. Engine construction intrinsics return
construction-time target values instead, as described below.

### Intrinsic project requests

`MSBuild` is represented as a task in project XML, but the bound engine
intrinsic is special-cased as a graph-construction operation, like `CallTarget`.
It does not require an ordinary task's DeclaredIO annotation and is not emitted
as a file-I/O action. This exception does not apply to custom tasks merely named
`MSBuild`.

With statically known projects and request parameters, the intrinsic constructs
the referenced configured-project subgraphs as part of the same root graph.
Each child's evaluation and target construction must obey the same hardened
restrictions as the parent: engine-managed discovery stays constrained,
construction values must be available, ordinary tasks need audited DeclaredIO,
only zero-I/O task invocations execute, and file-I/O actions cannot use
`<Output>` mappings. Recursing through `MSBuild` does not permit an unrestricted
nested build or a fallback to ordinary task execution. A violation in any child
fails the root construction without publishing a partial graph.

The intrinsic's `<Output TaskParameter="TargetOutputs" ...>` mappings return
the child's construction-time `Returns` values, including supported legacy
`Outputs` returns. These may describe paths whose producer actions have been
recorded but not executed; they cannot depend on file-action results or reads
of not-yet-produced files. Normal parameter binding, metadata propagation and
configured-project identity are preserved.

An empty `Projects` list is a construction-time no-op: `TargetOutputs` is
empty, item outputs append nothing, property outputs become empty, and
`MSBuildLastTaskResult` becomes `true`. Unavailable project lists or request
parameters remain unsupported.

### Output parent directories

The standalone host validates the complete invocation, then ensures the parent
directories of its declared output files exist before executing the task.
Parent directories are execution scaffolding, not owned directory-tree
artifacts. No directory action, timestamp dependency, or success stamp is
emitted. File/directory collisions fail without deleting existing paths.

Common's participating `MakeDir` call sites retain their ordinary behavior outside
hardened mode. During construction they instead use the audited zero-I/O
`RemoveDuplicates` task to accumulate `_MSBuildRequiredOutputDirectory` items.
The emitter captures these requirements as tasks return, including from
`CallTarget` scopes, and checks the complete action closure before publication:
each directory must be an ancestor of a declared file output. Repeated and
nested requirements are allowed; preexisting directories do not bypass the
check. Uncovered requests, including arbitrary `CreateDirectory` entries, fail
rather than disappearing or being created during construction.

This is a structural-directory profile, not support for general `MakeDir`
actions, `DirectoriesCreated` mappings, empty-directory products, or tree
artifacts. Directory-creation errors in hardened execution are fail-fast;
ordinary `PrepareForBuild` retains `ContinueOnError=true`.

### Repository-bounded directory imports

Standard directory imports are engine-managed source discovery, not external
facts to snapshot during Restore. During project evaluation, the engine supports
`GetDirectoryNameOfFileAbove` and `GetPathOfFileAbove` for exactly
`Directory.Build.props`, `Directory.Build.targets`, and `Directory.Packages.props`.
The existing Common and NuGet authoring therefore retains its nearest-file
selection, import order, enable switches, and explicit path overrides.

Supply `MSBuildGraphRepositoryRoot` as an absolute global property to both
preparation and hardened Build. The evaluator captures this global input before
project authoring; an environment value, an authored property, or an authored
replacement through `TreatAsLocalProperty` cannot supply or change the boundary.
It is not inferred from the current directory, Git, or an imported props file.
The search start must also be absolute and inside the root. Search includes the
root itself and returns an empty result if no file exists through that directory,
without probing any parent above it. Lexical paths and resolved symlinks must stay
inside the boundary. This constrains automatic discovery, not every explicit
import path.

The same opt-in boundary applies during ordinary preparation so it evaluates the
same directory authoring. Ordinary evaluation without the global input is
unchanged. Other ancestor-search filenames retain ordinary behavior outside
graph construction but remain prohibited in hardened Build. Target construction
does not receive this evaluation-only capability; general filesystem property
functions are not newly allowed.

Imports remain live source inputs to fresh evaluation, not captured evaluated
properties or task outputs. This feature does not implement persistent tracking
of negative lookup probes or automatic graph regeneration. Callers reusing an
evaluation context must discard its filesystem cache after source changes.

### Prepared SDK imports

SDK package selection and location discovery belong to ordinary preparation.
With `MSBuildRestoreGraphInputs=true` and an absolute global
`MSBuildGraphSdkBindingsDirectory`, evaluation saves successful resolver results
as versioned XML binding facts. The standalone driver supplies this directory
automatically; programmatic callers must supply it to both preparation and the
fresh graph evaluation. The directory is inherited by project requests.

Bindings are keyed by project, solution, expanded SDK name/version/minimum
version, interactive mode, and Visual Studio context. They preserve the original
result reference, resolved version, ordered absolute SDK paths, resolver-supplied
properties, items and metadata, environment values, and warnings. Literal values
are XML-encoded without evaluating or expanding MSBuild expressions. Atomic
replacement avoids publishing a partially written binding.

Hardened evaluation reads the matching binding before loading SDK imports,
without invoking registered SDK resolver plugins. The existing import machinery
still evaluates the SDK's actual props and targets and applies resolver-provided
values in their normal order. It does not flatten evaluated SDK authoring into
Restore snapshots. A missing binding permits only the default resolver's direct
lookup in the configured toolset's `Sdks` directory; the standalone app supplies
that root explicitly. A package merely existing in a NuGet cache is not enough.
Missing package bindings, malformed data, unsupported formats, mismatched
contexts, and nonabsolute paths fail without plugin discovery or graph publication.

Changing a package-SDK request requires a new matching Restore. Changes to
`global.json`, NuGet configuration, or resolver implementations also require
preparation again; this feature does not implement general content-based
invalidation of those external discovery inputs. Use a separate binding directory
for independent preparation profiles. Ordinary evaluation without the opt-in
capture inputs is unchanged.

### Captured construction platform

Machine information is predefined construction context, like predefined
variables, rather than a requirement to thread machine properties through
project authoring. The command-line launcher captures its host OS, process
architecture and runtime identifier once as the global properties
`MSBuildGraphHostOS`, `MSBuildGraphHostArchitecture` and
`MSBuildGraphHostRuntimeIdentifier`. Defaults come from the launcher automatically;
explicit overrides such as `-p:MSBuildGraphHostOS=linux`,
`-p:MSBuildGraphHostArchitecture=arm64` or
`-p:MSBuildGraphHostRuntimeIdentifier=linux-musl-arm64` select a different fixed context.
OS values are `windows`, `linux`, `osx`, and `freebsd`; architecture values must
name a member of `System.Runtime.InteropServices.Architecture`, such as `x64`
or `arm64`. Names are case-insensitive; numeric and compound values are rejected.
The RID is a nonempty, case-preserving token containing ASCII letters, digits,
periods, hyphens or underscores; custom RID names do not require a registry.
Programmatic callers supply all three global inputs before evaluating a
graph-emission project. Environment and project properties do not replace them.
`TreatAsLocalProperty`, target assignments, and task output mappings cannot
make them mutable, and child projects cannot change the context.

During hardened evaluation and target construction, `[MSBuild]::IsOSPlatform`
compares its argument with the captured OS.
`[System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture`
returns the captured `Architecture` enum, preserving ordinary enum formatting
and chains such as `.ToString().ToLowerInvariant()`.
`[System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier`
returns the captured RID string, including ordinary string chains. None reads live
machine state during construction. Other `RuntimeInformation` members are not newly
allowed; ordinary builds retain their existing runtime queries.
The emitter records `msbuild_construction_host_os`,
`msbuild_construction_host_architecture` and
`msbuild_construction_host_runtime_identifier` values in the Ninja file, so changing
any of them changes graph identity even when no action parameter depends on it.
Future construction-cache keys must include this context; no such cache is
implemented yet.

This describes the construction process, not the underlying OS architecture,
action execution platform, or artifact's target platform. Target architecture
remains an independent project input. An override does not virtualize
filesystem/path semantics, change executors, or supply a different toolchain.
Remote execution and cross-platform toolchain selection remain separate
configuration work.

### Restore-time platform SDK discovery

Platform SDK machine searches belong to Restore, not graph construction:

```sh
msbuild Demo.csproj -target:Restore -p:MSBuildRestoreGraphInputs=true
```

That ordinary Restore invocation records SDK location/registry lookups,
display-name discovery, and metadata-path existence checks in a generated
import. It records empty results too: no installed SDK is a discovery result,
not permission to search again during graph construction.

The import contains discovery facts, not final build properties. In the fresh
hardened evaluation, the existing defaulting, path selection, moniker
construction, and explicit overrides execute normally using those facts.
For example, changing a display-name or WinMD override does not require
Restore when it does not affect discovery.

The default import path is
`$(BaseIntermediateOutputPath)/hardened/$(Configuration)/$(Platform)/$(TargetFramework)/$(MSBuildProjectFile).platform-sdk.props`
(`obj` is used when the base path is empty). `MSBuildPlatformSDKRestoreFile`
can select a different file. The Restore hook runs for each framework's
restore-graph traversal, as well as direct `Restore` targets.

Hardened evaluation requires this import; a missing file fails rather than
falling back to machine discovery. Before constructing actions, an initial
target checks the discovery inputs, including platform identifier/version,
SDK path/metadata overrides, tools/runtime context, and captured host OS.
A mismatch reports MSB3668 and requires Restore again. Repeating Restore with
unchanged discovery preserves the file's timestamp.

In this prototype, the queries run during ordinary Restore's evaluation and
the Restore target hook persists their results. Ordinary non-hardened builds
retain their existing discovery behavior.

The same flag also snapshots the presence of the unmanaged-registration cache into
`$(BaseIntermediateOutputPath)/hardened/$(Configuration)/$(Platform)/$(TargetFramework)/$(MSBuildProjectFile).UnmanagedRegistration.cache.props`.
Absent caches still produce valid imports. The hook participates in each
framework's Restore traversal, and unchanged snapshots are not rewritten.
The per-configuration/framework paths prevent concurrent captures from
overwriting one another.
The prior clean-file ledger is not a Restore discovery input: it is neither
captured nor replayed, and obsolete `$(CleanFile).hardened.props` files are ignored.

With the coordinated SDK, SourceLink, and Roslyn overlays, the same Restore
flag also prepares framework-reference and repository snapshots and initializes
the analyzer semaphore used as a stable compilation input. The selected target
framework can have zero, one or multiple `FrameworkReference` items; ordinary SDK
authoring decomposes their targeting packs into item/property facts. Package-backed
frameworks such as `netstandard2.0` retain normal package-authored explicit reference
items and need no synthetic framework reference or special preparation path.
Package/project references and explicit assembly paths are supported, but runtime
identifiers are not. The
[SDK preparation targets](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/sdk/Sdk.targets)
leaves project-declared references in authoring; hardened resolution rejects
assembly names even with `HintPath`. Legacy manifest-only mode still rejects
`Reference` items. Retained `TargetFrameworks` is supported when an inner
`TargetFramework` is selected; outer preparation is not.

Framework snapshots live in
`$(MSBuildProjectExtensionsPath)frameworkreferences/$(MSBuildProjectFile).g.props`;
repository snapshots live in
`$(MSBuildProjectExtensionsPath)sourcelink/$(MSBuildProjectFile).g.targets`.
For SDK graph-input preparation and hardened replay, both defaults insert
`$(Configuration)/$(Platform)/$(TargetFramework)/` beneath the snapshot directory;
explicit legacy manifest switches retain the unqualified defaults. NuGet and
compiler inputs use the same configuration-qualified layout. Other input-affecting
global properties require distinct intermediate paths.
Dedicated directories prevent automatic Common project-extension imports from
replaying stale snapshots during Restore. Capture ignores existing snapshots
and refreshes them from current inputs, preserving timestamps when unchanged.
Hardened evaluation requires the snapshots and never regenerates them.
SourceLink capture still respects `EnableSourceControlManagerQueries=false`.
The analyzer marker is prepared during Restore, not written as an in-place
action during graph construction.

Without the flag, ordinary Restore writes none of these additional imports.
The earlier `MSBuildRestorePlatformSDK=true` flag remains supported for
SDK-only capture; prefer `MSBuildRestoreGraphInputs=true` to prepare all known
graph inputs. Explicit legacy framework-reference and SourceLink manifest
switches remain supported; they are not required for umbrella-flag capture.
Configured preparation restores each selected project without forcing its global
framework onto dependencies, then captures with a fresh evaluation and follows
normal project-reference framework/configuration/property negotiation.
It explicitly rejects standard product/tool build targets during preparation;
generated-tool dependencies must remain connected to their graph producers.
Framework preparation captures targeting-pack reference and analyzer candidates
owned by the selected `FrameworkReferenceName`, excluding unrelated project/package
items, before project-dependent filtering. Runtime filters those candidates immediately
before final assembly-reference resolution, when project output metadata is
available; it must not pull `ResolveProjectReferences` into pack discovery.
Snapshot replay must participate in that filtering, not bypass it.

### Framework inventories and final reference selection

A framework snapshot records the selected targeting packs' candidate inventories,
not the final compiler reference set. Restore may acquire packs and capture their
membership, paths, and metadata. It must not freeze the winning references across
framework, package, project-output, and explicit-file candidates.
An empty pack inventory is valid. References introduced by package build targets
remain ordinary authoring, not pack inventory. Replay checks the current
framework-reference identity set against the prepared set and requires fresh Restore
if they differ.

Graph construction combines those candidates and applies reference-selection
policy using prepared facts and project authoring. For example, when a newer
`System.Collections.Immutable` package supplies the selected compatible compile
asset, the compiler receives the other net11 framework references plus that
package asset, not both versions of `System.Collections.Immutable.dll`.
Keeping an explicit file reference out of the framework inventory does not exempt
it from final selection. Generated candidates retain their producer dependencies.

The existing SDK splits these responsibilities:
[`ProcessFrameworkReferences` and `ResolveTargetingPackAssets`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.FrameworkReferenceResolution.targets)
select packs and obtain their inventories and conflict-policy information;
`ResolvePackageFileConflicts` performs framework/package arbitration. Migrate
that arbitration separately from its filesystem discovery, rather than running
it during Restore and snapshotting its winners.

The prototype now implements bounded framework/package selection through
[`ResolvePreparedConflicts`](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/ResolvePreparedConflicts.cs).
Restore captures package asset assembly/file versions and the targeting pack's
platform manifests, override rules and preferred-package ordering. Graph construction
invokes the shared SDK conflict policy after runtime filtering and explicit path
normalization, updating compile/analyzer/copy-local items and dependency conflict
records. It does not reopen manifests or binaries, probe existence, or use `HintPath`
as a second identity. Runtime-provided references can remain compile inputs without
being copied, and a losing direct reference's symbols are not published.

Noncolliding producer outputs require no version facts. Collisions can use declared
version metadata or prepared external facts; missing/invalid facts and unresolved
distinct-artifact ties fail with NETSDK1255 rather than falling back to discovery.
The ordinary SDK conflict target remains unchanged for non-hardened builds.
Package/package compile, analyzer and runtime duplicates still fail in the bounded
package-selection profile; this is not a general replacement for RAR.

### Explicit reference-resolution sources

Hardened SDK builds do not discover assemblies through installation-wide
`AssemblyFolders.config`, registry entries, legacy assembly folders, or the
GAC. They also disable automatic `ReferencePath`, output-directory, and
raw-filename searches. `Microsoft.Common.props` selects this profile before
the SDK initializes its search paths, and Common targets skip the
`GetToolsDirectory32` call and assembly-folder configuration existence probe.
Visual Studio directory-layout discovery is disabled in this profile.
Ordinary builds retain their previous behavior.

The Common reference-resolution target validates the effective
`AssemblySearchPaths` before its resolution dependencies. Permitted entries
are `{CandidateAssemblyFiles}`, `{HintPathFromItem}`,
`{TargetFrameworkDirectory}`, `{RawFileName}`, and explicitly configured fully qualified
directories. Unsupported tokens or relative search directories fail with
MSB3669 rather than being silently removed from an explicit override.
This guard does not enable searches: hardened `ResolveAssemblyReferences`
selects zero-I/O `ResolveReferencePaths`, not stock RAR. `{RawFileName}` permits
explicit path binding, not an existence-based fallback. Every reference identity
must already be a file path, relative to the captured project directory or
absolute. Simple/display assembly names, search tokens, wildcards and URIs fail;
`HintPath` does not make a non-path identity acceptable.

Reference inputs must supply the complete closure from workspace artifacts,
pinned/restored reference packs and packages, including explicit generated
project outputs. Capturing an installation path alone does not pin its contents.
Resolution normalizes paths and preserves supplied metadata without opening
assemblies, checking existence, discovering dependencies or unifying versions.
Generated references must remain execution-time artifacts, not Restore snapshots.
Explicit `ReferenceAssembly` metadata supplies the compile contract; implementation
identities and optional `DebugSymbolsFile` metadata supply copy-local artifacts.
Framework/copy-local/private metadata controls publication without inspecting
assemblies. Related-file and satellite discovery remain outside this profile.

### Resource names and signing keys

Hardened C# resource naming uses the audited zero-I/O `FormatResourceNames`
task. It shares the ordinary name formatter over resource paths, `TargetPath`,
root namespace and supplied culture metadata, using the fixed culture-name set.
It preserves resource metadata and explicit logical names. The dependent-upon
convention and `DependentUpon` metadata fail with MSB3672 rather than probing
adjacent files or parsing source. Explicit `ManifestResourceName` metadata
bypasses naming as in ordinary builds. Resource contents remain execution inputs;
neither resource files nor generated source must exist to construct their names.

Hardened `ResolveKeySource` forwards the explicit non-PFX
`AssemblyOriginatorKeyFile` to `KeyOriginatorFile` without opening it. The
compiler declares that file as an input; its bytes can come from a producer.
Manifest signing, certificate-file/store lookup and key containers fail with
MSB3673. The compiler's existing public/delay-signing restriction still applies.
Ordinary resource and signing tasks retain their existing behavior. Neither
metadata transformation requires a new Restore snapshot.

## Graph construction

Reuse MSBuild's target ordering, expression expansion, batching, and
property/item scope semantics for the supported subset.

The current path reuses the existing hardened construction walk and its
task-builder callback. File actions are captured before task loading and typed
parameter binding; zero-I/O invocations use ordinary binding, execute, and
gather outputs normally. Successful construction returns before ordinary
`BuildTargets`, so target timestamp skipping cannot erase actions or execute
construction tasks a second time. No additional target interpreter is added.

- Record graph inputs: projects/imports, global properties, declared
  environment values, pinned SDK resolution, and glob results. Imports resolve
  to exact paths; built-in globs are deterministic and workspace-bounded.
  Engine-managed reads are recorded; ambient or user-programmed I/O is not
  permitted during construction.
- Conditions, target/project routing, batch membership, all emitted parameter
  values, and item metadata must resolve during construction. Property
  functions are restricted to the hardened allowlist. Array indexing uses
  the existing read-only `Array.GetValue` binding, including computed indices
  and chained expressions; bounds/conversion errors still fail construction.
  Other array methods and arbitrary object indexers are not admitted by this
  allowance. File-I/O tasks must never run to make an unavailable construction
  value known.
- Expand supported `MSBuild` and `CallTarget` intrinsics into construction
  state and dependency edges, not host commands that invoke MSBuild.
  Configured projects retain their global-property and inner-build identity.
  [Intrinsic project requests](#intrinsic-project-requests) construct child
  subgraphs under the same restrictions and return construction-time values,
  not execution results.
- Preserve required file and control dependencies, initially conservatively.
  Associate generated inputs with their producers, reject conflicting output
  ownership and cycles, and do not mistake stale generated files for sources.

Target `Inputs` and `Outputs` retain their batching roles, and `Returns`
retains its static value role, including legacy `Outputs` returns. Target
timestamp skipping must not omit actions from the graph. Unsupported
constructs fail with source locations and useful origin chains before graph
publication; there is no partial fallback to ordinary execution.

## Persistent graph

The graph bundle consists of `build.ninja` and versioned per-action invocation
manifests. No separate whole-graph JSON format is required.

Each manifest contains:

- invocation identity, including configured project, target, and batch;
- registered task name, assembly artifact, and pinned code/tool dependencies;
- expanded scalar and item-vector parameter values, preserving order and metadata;
- working directory, explicit environment, and runtime/host context;
- canonical declared paths and original diagnostic locations.

Each action is one Ninja build edge, including all outputs of a multi-output
invocation. Declared input files, manifests, and host/tool dependencies are
normal explicit or implicit inputs. Required ordering without content
invalidation uses order-only edges, never as a substitute for consumed files.
Default roots represent the requested target closure.

Task assemblies are ordinary file inputs, including when an earlier action
produces them. Construction does not enumerate a generated assembly's future
directory. Its producer and consumers must declare any other generated tool
artifacts they need. Registration selection follows authored override and
registration order, without probing assemblies for alternative task types;
use unambiguous registrations rather than assembly-content fallback.

Host commands receive manifest paths, not MSBuild expressions or serialized
engine objects. Ninja escaping and platform command quoting are separate
concerns. Rewrite manifests only when their contents change, use consistent
path spellings, and publish the complete bundle only after successful
construction. Generation metadata is separate from task outputs.

Ninja initially provides file/timestamp and command-change incrementality,
not content-addressed caching. Timestamp-preserving content changes are not
reliably detected. `restat` is optional and does not change this limitation.
See the [Ninja manual](https://ninja-build.org/manual.html).

Graph-input changes, including source-set changes, require explicitly rerunning
MSBuild. The prototype does not install a Ninja regeneration rule that calls
MSBuild. Ninja executes from the recorded graph working directory; hosts apply
their invocation-specific execution directories.

## Standalone task hosts

The first host, `MSBuild.ActionTaskHost`, accepts `--invocation <file.json>`.
Newly emitted version 2 invocations preserve both expanded interpretations of
each authored parameter without inspecting the task's property types:

```json
{
  "version": 2,
  "taskAssembly": "/tools/Microsoft.Build.Tasks.Core.dll",
  "taskType": "Microsoft.Build.Tasks.WriteLinesToFile",
  "workingDirectory": "/workspace",
  "environment": {},
  "parameters": {
    "File": {
      "scalar": "obj/generated.txt",
      "items": [{"itemSpec": "obj/generated.txt", "metadata": {}}]
    },
    "Lines": {
      "scalar": "first line;second line",
      "items": [
        {"itemSpec": "first line", "metadata": {}},
        {"itemSpec": "second line", "metadata": {}}
      ]
    },
    "Overwrite": {
      "scalar": "true",
      "items": [{"itemSpec": "true", "metadata": {}}]
    },
    "DeclaredInputs": {"scalar": "", "items": []},
    "DeclaredOutputs": {
      "scalar": "obj/generated.txt",
      "items": [{"itemSpec": "obj/generated.txt", "metadata": {}}]
    }
  }
}
```

Assembly and working-directory paths are absolute; task paths are relative
to that directory. `scalar` is expanded and unescaped text; `items` preserves
item boundaries and custom metadata. Item text and metadata are literal, not
MSBuild-escaped or expression-expanded at execution. If an expression is valid
only as a scalar, `items` is null; this fails only when the actual parameter
needs an item/vector representation. The executor selects the representation
using the real property type and performs invariant MSBuild conversions.
An `ITaskItem` scalar needs exactly one item; arrays preserve all items.
Omitted parameters and explicitly empty optional values retain constructor
defaults, as in ordinary binding. Required empty arrays bind as empty arrays;
the two footprint declarations always bind, even when empty.

The executor also accepts version 1 pre-bound JSON values for direct callers.
That format uses scalar strings/numbers/booleans and explicit arrays; items
are literal strings or `{"itemSpec":"file.cs","metadata":{"Tag":"value"}}`.
Explicit empty values remain empty in version 1. Null parameter values are
rejected in both formats.

The environment replaces inherited task environment variables and defaults to
empty; culture is invariant. Optional `projectFile`, `lineNumber`, and
`columnNumber` supply diagnostic locations. Unknown fields, unsupported
versions/types, duplicate parameter names ignoring case, and missing required
parameters fail explicitly. This file format is experimental, not a public
serialization API.

During emission, `MSBuildActionEnvironment` optionally selects a semicolon-
separated list of environment variable names whose current values are captured.
Without it, action environments are empty; the engine's entire ambient
environment is not implicitly persisted.

Start with one reusable console application, launched in a fresh process for
each action. It loads a pinned managed task, binds supported parameter types,
reconstructs items and metadata, applies explicit execution context, and
executes one invocation.

The host may use `Microsoft.Build.Framework` and task utilities, but not the
`Microsoft.Build` engine. A small `IBuildEngine` compatibility implementation
provides supported diagnostics and services; nested builds and unsupported
services fail explicitly. The engine-dependent `TaskExecutionHost` is not
reused wholesale, although small binding helpers may be extracted.

False results, exceptions, logged errors, or missing required output files
produce nonzero exit codes and useful diagnostics. Checking required outputs
is not an audit for undeclared I/O. Ninja blocks dependents after failure;
already-running independent commands may finish.

## Sandbox enforcement (deferred)

Sandboxing is deliberately deferred, not a prerequisite for the current compiler
or analyzer/generator profile. The prototype trusts audited task contracts and
plugins' nominal compiler-input-only contract. Declaration validation, separate
processes and required-output checks do not enforce actual access or make
untrusted tasks safe to run. Deferring enforcement does not relax the contract:
DeclaredIO still promises deterministic, machine-independent results, not merely
a list of filenames.

A future general enforcement layer must:

- Limit task reads to declared inputs and explicit implementation/tool/host
  context; keep inputs read-only. Undeclared filesystem observations, including
  existence probes and directory enumeration, are not implicit discovery rights.
  Writes are limited to declared outputs and private ephemeral scratch, with no
  undeclared persistent artifacts. Path aliases and symlinks must not bypass the
  boundary.
- Apply the same boundary to child tools and loaded analyzers/generators, without
  per-plugin exemptions or admission allowlists. Zero-I/O construction tasks
  must remain zero-I/O; engine-managed project/import/glob discovery is a separate
  capability, not permission inherited by tasks.
- Deny undeclared network access and control working-directory, environment and
  toolchain context. Clock, randomness and machine-specific state must not become
  hidden semantic inputs. Report violations as failures identifying the task and
  attempted access, rather than silently widening declarations or discovering
  new graph dependencies during execution.

Filesystem/network confinement alone cannot prove determinism or relocation
independence. Deterministic compiler settings, automatic path mapping and
relocation/output comparisons remain necessary alongside future sandboxing.
Enforcement belongs in the general construction/execution infrastructure, not
task-specific sandbox implementations.

## Prototype boundary and later work

The implemented emitter supports Unix shell commands, one root project with
supported configured-project requests, conservative action ordering, and
dependencies on earlier producers. It rejects forward-produced inputs,
ordinary tasks without DeclaredIO, and unsupported hosting/error-control modes.
`DependsOnTargets`, before/after targets, batching, and supported `CallTarget`
construction use the existing hardened walk. Participating zero-I/O tasks
must use explicit DeclaredIO with empty declarations. The separate Pure
annotation, interface, and environment have been retired; participating tasks
use the existing `TaskEnvironment` for project-directory context.

`--hardened-graph` always constructs a graph. `MSBuildEmitActionGraph`,
`MSBuildActionTaskHost`, and `MSBuildActionDotNetHost` must be configured; there
is no validation-then-execution fallback. Restore must run separately without
`--hardened-graph`, before starting construction.

Manifests have content-derived names and are written before the Ninja entry
point is atomically replaced; unchanged manifests and entry points retain
their timestamps. Adjacent managed assemblies, runtime configuration files,
and native libraries in the host/task directories are conservative tool
dependencies. Tools loaded elsewhere must still appear in declared inputs.
Automatic runtime/tool-closure discovery and graph-input inventories are not
implemented. Property functions in graph mode have a narrow allowlist,
including during evaluation; unsupported SDK imports must be adapted rather
than silently allowed. This is not yet a general SDK or runtime-repository build.

Every file-I/O action must produce at least one persistent regular file, and
every declared output must exist on success. Inputs are existing external
regular files or required outputs of predecessor actions; generated inputs
need not exist during construction.

Reject unaudited ordinary tasks, unresolved parameters, task factories, specialized
runtime/architecture hosting, `OnError`, non-default `ContinueOnError`,
execution-time status control, directory outputs, optional input absences,
optional/deleted outputs, in-place updates, and outputless file-I/O actions.
Synthetic success stamps must not hide unsupported filesystem effects.

The first milestone is a small task chain, then a pinned framework-only C#
compilation and a two-project build. Start with conservative ordering and
`ninja -j1`; allow concurrency only where dependencies establish its safety.
The explicit Hello World fixture now demonstrates real compiler execution,
runtime-config generation, source invalidation, and compiler failure through
standalone hosts. It does not yet admit an ordinary SDK `Build` target chain;
see the [prototype roadmap](hardened-static-graph-implementation.md#5-compile-with-the-pinned-sdk).

Content caching, generalized fetch, deferred value flow, richer failure
handling, relocation, automatic regeneration, and persistent analysis are
later work. Action identity is not a future cache key: generated input
digests become available only during execution, and consumer keys should use
consumed artifact digests rather than producer action keys.
[Named products](hardened-named-products.md) separately describes future root
selection and artifact demand.
