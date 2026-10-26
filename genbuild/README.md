# genbuild

Experimental MSBuild action-graph compiler. Restore prepares dependencies and
captures discovery inputs; a separate hardened Build emits Ninja actions for
independent task hosts. MSBuild does not execute those actions.

The output is a framework-dependent app, not an SDK distribution. It includes
its own targets, tasks, compiler, and action host, but no .NET runtime.
For now it is launched through `dotnet genbuild.dll`, without a native apphost.

## Build

Use the .NET 11 SDK selected by the VMR's `global.json`. Package restore requires
access to the component repositories' configured NuGet feeds.
Run these commands from the **VMR root**:

```sh
dotnet msbuild genbuild/genbuild.proj -m:1 -nr:false -bl:{}
```

This builds the selected MSBuild, SDK-task, Roslyn, SourceLink, NuGet, and Arcade
target-framework task sources and
assembles the app in `artifacts/bin/genbuild/Debug/`. It does not build the full
SDK, templates, workloads, or a runtime.

`genbuild.proj` is the complete build entry point. `genbuild.csproj` only compiles
the CLI; building that project alone does not assemble its toolset.

After the initial component build, reassemble without rebuilding components:

```sh
dotnet msbuild genbuild/genbuild.proj -p:GenBuildSkipComponentBuild=true -bl:{}
```

Use `-p:Configuration=Release` to change configuration. If necessary,
`GenBuildDotNetHost` and `GenBuildBootstrapSdk` select explicit build-time
paths to `dotnet` and its SDK directory, respectively.

## Run

Execution requires a compatible .NET 11 runtime, but no installed SDK.
Keep the entire app directory together when copying it elsewhere.
From the VMR root:

```sh
dotnet artifacts/bin/genbuild/Debug/genbuild.dll restore path/Project.csproj -bl:{}
dotnet artifacts/bin/genbuild/Debug/genbuild.dll build path/Project.csproj -bl:{}
ninja -C path -f obj/genbuild/build.ninja
```

Restore and Build must remain separate invocations. `build` generates
`obj/genbuild/build.ninja` under the project's directory without running actions;
Ninja executes them. Additional arguments are forwarded to MSBuild.
Use `-p:MSBuildEmitActionGraph=/absolute/path/build.ninja` to change the graph path.

The driver supplies `MSBuildGraphSdkBindingsDirectory`, defaulting to
`obj/genbuild/sdk-bindings` under the entry project's directory. Restore saves
SDK resolver bindings there, including package SDKs pinned in `global.json`.
Build replays the paths and resolver-supplied values before loading SDK imports;
SDK props and targets remain live authoring, not evaluated-property snapshots.
Plugin/package SDK discovery stays disabled during Build. Only SDKs in the app's
explicit toolset can be used without a prepared binding. Supply the same absolute
`-p:MSBuildGraphSdkBindingsDirectory=...` to both commands when isolating another
preparation profile. Changed package-SDK requests or discovery configuration
require a fresh Restore. See the
[SDK-binding contract](../src/msbuild/documentation/specs/proposed/hardened-static-graph.md#prepared-sdk-imports).

For normal `Directory.Build.props`, `Directory.Build.targets`, and
`Directory.Packages.props` discovery, pass
`-p:MSBuildGraphRepositoryRoot=/absolute/path/to/repository` to both Restore and
Build. The engine selects the nearest file, checking the repository root
inclusively and returning no match rather than searching its parent. The root
must be an explicit absolute global property; it is not inferred from Git, the
environment, or imported authoring. Explicit directory-import path overrides
remain supported. This is evaluation-time source discovery, not a Restore
snapshot or permission for arbitrary ancestor searches during construction.

Build automatically supplies a fixed machine context: `MSBuildGraphHostOS`,
`MSBuildGraphHostArchitecture` and `MSBuildGraphHostRuntimeIdentifier`. Existing
`IsOSPlatform`, `RuntimeInformation.ProcessArchitecture` and
`RuntimeInformation.RuntimeIdentifier` expressions read those captured values,
including normal enum/string formatting, rather than querying the machine again.
Explicit global overrides are supported, but project authoring cannot mutate the
context. These values describe the construction process, not target architecture
or a remote executor, and all three participate in graph identity. See the
[machine-context contract](../src/msbuild/documentation/specs/proposed/hardened-static-graph.md#captured-construction-platform).

Projects may retain `TargetFrameworks` when both invocations select an inner
build with the same `-p:TargetFramework=...` and `-p:Configuration=...`.
Preparation follows normal project-reference framework/configuration negotiation
and property metadata. SDK framework, NuGet, compiler and SourceLink snapshots
are separated by `Configuration/Platform/TargetFramework` beneath their respective
intermediate directories. Other input-affecting global properties still require
distinct intermediate paths.

### Arcade framework negotiation

The app stages the selected Arcade task package at
`toolset/Arcade/TargetFramework/`. Ordinary selection still reads the configured
runtime-graph file. Restore preparation captures that file's JSON in
`targetframeworkinputs/Configuration/Platform/TargetFramework/Project.g.props`
under the project's extensions directory. Hardened selection consumes the
captured data in memory, using the same NuGet matching policy over the current
framework/platform and referenced-project metadata. Restore does not capture
the winning framework. Changed runtime-graph inputs require a fresh Restore.

Capture runs in a fresh non-restoring evaluation so NuGet-generated props and
targets participate, including their preparation hooks. The normal guard against
building products/tools during preparation remains enabled.

A repository consuming a pinned Arcade target-framework package must explicitly
select the local package; `ArcadeSdkDir` alone does not replace NuGet imports.
After building the app, create a local package, using a new version for each
revision to avoid reusing a cached package:

```sh
dotnet msbuild genbuild/arcade/TargetFramework.csproj -t:Pack \
  -p:Configuration=Debug -p:NoBuild=true \
  -p:PackageVersion=12.0.0-genbuild.1 \
  -p:PackageOutputPath=/absolute/path/to/isolated-feed
```

For runtime, pass the matching
`-p:MicrosoftDotNetBuildTasksTargetFrameworkPackageVersion=12.0.0-genbuild.1`
and `-p:RestoreAdditionalProjectSources=/absolute/path/to/isolated-feed`
to Restore and Build. Its compiler package also supplies integration targets;
select the prototype integration explicitly in both commands with
`-p:CSharpCoreTargetsPath=/absolute/path/to/app/toolset/Roslyn/Microsoft.CSharp.Core.targets`.
This changes the integration authoring, not a permission to prebuild compiler
or runtime products. Do not patch shared cached package contents.

## Smoke checks

Install Ninja and build the app first. Then run from the VMR root, replacing
the host path with an absolute path to `dotnet` (`dotnet.exe` on Windows):

```sh
dotnet msbuild genbuild/tests/Smoke.proj -p:GenBuildTestHost=/absolute/path/to/dotnet -bl:{}
```

The checks cover CLI failures, paths with spaces, graph-only construction,
independent Ninja execution, explicit NuGet project-style classification, SDK
duplicate-item, executable-reference and filename-only WinMD validation, exact
editorconfig and framework attribute generation, assembly version derivation,
cache-free assembly generation, source-root mapping, exact SourceLink publication
with all four packaged providers, SDK Restore, standalone C# compilation,
dependency/runtime-configuration publication and final assembly/reference/PDB
copying. They also check cleanup-free SDK Build graph construction and independent
execution through final artifact publication and program startup on the external
runtime. A clean SDK executable/library closure also constructs and executes
without prebuilding references, including renamed library outputs, copy-local
symbols and ordinary-equivalent dependency manifests.
Arcade checks compare ordinary and prepared P2P selection, platform fallback,
RID-agnostic metadata, explicit framework overrides and incompatible references.
They also exercise real NuGet-imported capture hooks, literal JSON transport,
replay without the live runtime-graph file, and fail-closed prepared inputs.
Repository-info checks preserve provider URL replay, Azure DevOps mirror
remapping, the translation opt-out and unrelated source-root metadata, then
independently publish metadata matching ordinary builds.
Roslyn analyzer-config checks run the source generator, verify its declared
inventory against every emitted file, and select configs with those files absent
during construction. Version, mode, category, warnings-as-errors, empty and
unsupported selections match ordinary execution. Ordinary custom directories
remain supported and fail closed in hardened construction.
Skipped-analyzer checks request the marker output alone to prove its compilation
dependency, recreate a missing marker, and preserve its timestamp on compilation
failure and analyzer-enabled builds. Construction leaves existing marker bytes
and timestamps unchanged; ordinary builds retain `Touch` behavior.
Resource/signing checks compare ordinary and graph-time metadata with resource
and key files absent. Source-dependent resource naming and certificate/PFX/
key-container discovery fail without publishing a partial graph.
The compiler independently consumes an execution-produced signing key and emits
the expected public-key token.
Resource compilation likewise consumes generated `.resx` files, produces
ordinary-equivalent main/culture resource bytes without a state cache, and
embeds values retrieved by the independently compiled program. Unsupported
host/tool/source-generation modes fail construction; linked and typed resource
payloads fail execution before resource output creation.
Arcade resource-source generation independently consumes a generated `.resx`,
produces accessors used by the compiler, and preserves ordinary source bytes,
resource lookup, default-value arguments, and named/numbered format methods.
Unrecognized source languages fail without graph publication.
A configured fixture retains parent/child multi-targeting declarations, prepares
multiple parent frameworks/configurations, and independently builds a Debug/net11
parent with its negotiated Release/net10 child. Outer preparation and attempts
to prebuild tools/products fail explicitly.
A framework-basics fixture builds a package-backed `netstandard2.0` library for a
net11 executable and a net10 library using both Core and ASP.NET targeting packs.
It preserves ordinary package-authored references, checks stale framework inputs,
and compiles independently without prebuilt project binaries.
A runtime-authoring fixture captures unfiltered targeting-pack references and
analyzers without building dependencies, then filters them against project
replacements during construction. Its graph retains a build-order-only tool
producer and executes independently. A separate ordinary-build fixture uses a
recording linker to check runtime's distinct pre-trim/final assembly and symbol
paths, unchanged-build reuse, and recovery of missing final symbols. It tests
target authoring, not real linker semantics or generated-task binding.
An explicit-reference fixture keeps a future tool assembly out of framework
facts, preserves aliases and copy-local symbols, and compiles/starts independently.
Assembly-name/HintPath fallback and missing paths fail without graph publication;
legacy manifest rejection and valid empty-framework inventories are also checked.
An offline package fixture also selects distinct reference/runtime assemblies
and a compatible package generator, then independently compiles and starts the
consumer. Package selection runs with assets JSON and package directories
removed; full graph publication retains external-input validation.
Framework/package fixtures compare ordinary SDK selection in both directions,
then construct from facts with packages/JSON absent and compile/start through
Ninja. Pure policy checks cover file versions, package preferences/overrides,
aliases, future producers, symbol copies and invalid/ambiguous facts.
Compiler actions run analyzers and the Regex generator, consume generated
sources, and produce byte-identical assemblies, reference assemblies and
portable PDBs across relocated checkouts.
Select a runtime-only host to check that the app does not require an installed
SDK.

The runtime WebAssembly-cache authoring has a separate evaluation-only regression:

```sh
dotnet msbuild src/runtime/eng/wasm/tests/WasmToolCache.proj -bl:{}
```

It checks native-library isolation, browser/WASI inputs and explicit host-side
tool consumers without provisioning tools or compiling runtime products.

## Current limitations

Hardened NuGet classification supports the `PackageReference` style when it is
explicitly selected or implied by `PackageReference` items. It never searches for
`packages.config`; other styles or inputs requiring discovery fail explicitly.
Ordinary Restore retains NuGet's existing classification and discovery.
The app builds NuGet tasks and their managed dependency closure from this VMR's
sources, using NuGet's existing .NET 10 target inside the .NET 11 app. Its targets
come from the same source tree; no pinned NuGet task package or nested SDK build
is used. Compiler and other external tool dependencies remain pinned.
The source-built NuGet SDK resolver supports package-SDK discovery during
ordinary Restore, including versions pinned in `global.json`. The driver
disables that resolver during hardened Build, which now replays captured SDK
bindings rather than searching package caches or downloading SDKs.

Framework-reference preparation accepts a selected target framework per configured
project, including zero, one or multiple `FrameworkReference` items, package/project
references, explicit assembly paths and retained `TargetFrameworks`.
Normal SDK authoring decomposes selected targeting packs into reference/analyzer
items and runtime/conflict facts. Package-backed frameworks such as `netstandard2.0`
keep their explicit references in package authoring, without synthetic framework
references. Framework snapshots contain only pack-owned reference/analyzer
candidates; project/package-declared references stay in authoring for path-only resolution.
These are inventories, not final compiler reference sets. Final selection belongs
in graph construction: a zero-I/O adapter now reuses SDK conflict policy after
runtime filtering and path normalization. Restore captures package binary versions
and pack platform/override/preference facts, not winners. A newer package can
replace a framework member; an older package can lose without discarding unrelated
framework references. Colliding generated/explicit files need declared version
metadata or prepared external facts; missing facts and unresolved ties fail.
See the [reference-selection contract](../src/msbuild/documentation/specs/proposed/hardened-static-graph.md#framework-inventories-and-final-reference-selection).
Legacy manifest-only mode retains its narrower rejection of `Reference` items.
Changing the framework-reference identity set requires a fresh Restore.
Outer cross-targeting and RID preparation remain unsupported.
The real `System.Collections.Immutable` trial targeting runtime's `net10.0`
minimum now gets past the multi-targeting guard. Runtime's
`FilterImplicitAssemblyReferences` now runs before final assembly-reference
resolution rather than during targeting-pack discovery. Preparation reaches
`ILLink.Tasks` without building any product binaries and accepts its explicit
package-supplied MSBuild references authored by `PackageDownloadAndReference`.
Preparation now completes, including the ordinary package-backed `netstandard2.0`
references of `ILLink.CodeFixProvider` and `ILLink.RoslynAnalyzer`, without producing
DLLs. The producer dependencies remain intact.
Runtime library trimming compiles directly to `PreTrim/` and links to separate
final paths, without moving or rewriting compiler outputs. Runtime task
declared-I/O adaptations, explicit version inputs, and broader
compiler/tool profiles remain necessary.
The trial uses ordinary
Restore (`RestoreUseStaticGraphEvaluation=false`): the static Restore launcher
still assumes an installed SDK layout. With the VMR root explicitly supplied to
both invocations, a fresh hardened graph passes Common's directory-props lookup
and passes `eng/OSArch.props`' existing process-architecture query using the
predefined context. It now replays the `Microsoft.DotNet.Arcade.Sdk` binding and
loads its imports with package-SDK discovery disabled. Runtime's root
`Directory.Build.props` declares its local `RepoRoot` before importing Arcade,
so `tools/RepoLayout.props` does not search ancestors for `global.json`. This
layout root is distinct from the VMR-wide directory-import boundary.
Arcade's source `tools/ProjectLayout.props` now uses `MSBuild.NormalizePath` for
its output and intermediate base paths. The
[layout fixture](tests/ArcadeLayout.proj) imports that actual authoring and compares
ordinary execution with hardened construction and independent Ninja execution.
For the real probe, an isolated copy of the pinned Arcade SDK carries the changed
props, selected by the same explicit `ArcadeSdkDir` in Restore and Build; the shared
NuGet cache is unchanged.
[dotnet/runtime#135658](https://github.com/dotnet/runtime/pull/135658) is applied
locally to defer workload SDK-version calculations until their consumers run,
removing the unconditional `global.json` read from `eng/Versions.props`.
This unblocks ordinary library evaluation, not hardened workload construction;
the workload calculation still needs explicit prepared version inputs eventually.
Runtime now imports `eng/wasm/WasmToolCache.props` early only for WebAssembly
targets. Explicit host-side acquisition/provisioning consumers import those
inputs at their use sites; ordinary libraries no longer probe `.git`, read
WebAssembly tool versions or enable wasmtime provisioning.
`eng/RuntimeIdentifier.props` now evaluates unchanged against captured host-RID
context. Runtime's generator-directory classification now uses `NormalizePath`
anchored at `MSBuildProjectDirectory`, preserving ordinary classifications.
Restore-time capture includes the configured framework-metadata requests reached
by ordinary project-reference negotiation, not just the configurations selected
for compilation. Each reached configuration receives ordinary Restore followed
by fresh local capture, without recursively requesting its compilation references.
Framework, NuGet, compiler, and enabled source-control imports
remain required during hardened evaluation; incomplete capture fails rather than
being hidden by optional imports. There is no metadata-only mode or discovery
fallback. The [packaged regression](tests/DiscoveryPreparation.csproj) requires
facts for all four inspected frameworks, checks fresh Restore-generated imports
and ordinary/hardened metadata parity,
and independently compiles only the negotiated dependency. Missing facts for an
inspected but uncompiled framework fail its required imports.
The legacy framework-installation lookup is disabled by default in hardened SDK
evaluation, including .NET Framework metadata probes.
An isolated hardened metadata probe of runtime's actual reference project
successfully returns `net11.0`, `net10.0`, `netstandard2.0`, and `net462` without
producing DLLs.
Nonempty-footprint tasks are now opaque during construction: the graph captures
their authored registration, expanded scalar/item arguments, and declared paths
without loading their assemblies. The executor audits and binds after producers
run. A generated-task regression captures an absent DLL, gives it a producer
dependency, and independently executes it with ordinary-equivalent arguments.
Zero-I/O tasks still require an available audited implementation during construction.
No new `UsingTask` syntax or tool prebuild is needed.
The real net10.0 Restore succeeds with discovery-inclusive capture. Hardened
Build no longer attempts metadata loading of the future `ILLink.Tasks.dll`.
It now passes Arcade framework negotiation and resource/signing metadata,
resource compilation, and Arcade resource-source generation authoring.
`GenerateResxSource` reads one RESX and writes one known source path as an
execution action; it needs neither a Restore snapshot nor a graph-time task
output. The app stages the source-built SDK task assembly and its
`BuildTasks.props`, `GenerateResxSource.targets`, and `RepositoryInfo.targets`
at `toolset/Arcade/Sdk/tools/`.
The real probe regenerates its isolated Arcade SDK overlay with these files and
the `net/` payload, selecting the same `ArcadeSdkDir` for Restore and Build.
The staged task subset is not a complete Arcade SDK installation.
Ordinary task hosting is unchanged; hardened source generation uses the current
action host rather than the ordinary specialized `Runtime="NET"` registration.
Arcade now routes provider URL translation through SourceLink's replay-aware
target, so prepared URLs are not translated again. Its Azure DevOps mirror
remapping still runs, using the audited zero-I/O `RewriteRepositoryUrls` task
for the existing regex rewrite during construction. Ordinary expressions and
SourceLink enablement policy are unchanged; no new snapshot is needed.
Roslyn's analyzer-package generator now emits an explicit config inventory,
matching the existing SDK generator pattern. Hardened targets select from these
items without `Exists`; ordinary policy and custom-directory behavior remain
unchanged. This needs no new Restore snapshot. The real probe selects
source-generated target authoring in an isolated, normally restored analyzer
package root; its original binaries and config payload are preserved. Both
`RestorePackagesPath` and Arcade's early `NuGetPackageRoot` are bound to this root,
with the shared package cache retained as a read-only fallback.
Roslyn's skipped-analyzer marker uses the existing overwrite-only file writer
in hardened mode, declaring the intermediate assembly as its input. Independent
execution publishes an empty marker after successful compilation; ordinary builds
retain `Touch`. Neither the clock nor the marker's prior contents become action
inputs, and graph construction does not update the marker.
Construction advances through the ILLink analyzer and code-fix projects to the
linker reference project. The first remaining error is `GenerateResxSource`
being invoked without its required `ResourceFile` in `linker/ref/Mono.Linker.csproj`.
Preparation and the failed graph attempt produce zero DLLs, binary resources,
or generated source files, and publish no library graph.
No runtime library graph, compilation or independent execution is established.

Ordinary graph-input preparation now captures the complete assets JSON as
item/property facts under `obj/nugetinputs/`, including framework/RID data,
ordered package folders, dependencies, asset metadata and unknown fields.
This preserves underlying data rather than freezing selected package-task
outputs. A separate zero-I/O `ReadNuGetAssets` task now exposes an explicitly
named target's libraries, asset groups, dependencies and file inventory directly
from these facts. Hardened SDK Build now imports the facts automatically and
selects a separate zero-I/O `ResolveNuGetAssets` task. It reuses SDK package
policy over a direct model projection and direct item sink, without NuGet's
ambient JSON reader, package probing or binary asset cache. Restore additionally
records selected package locations, related-file presence, locale normalization
and compiler API identity, plus version facts for package assets; package generators
join the prepared compiler closure.
Standard reference, analyzer and copy-local item composition is retained.
Content preprocessing and simple framework-assembly names
fail explicitly. Framework/package conflicts use graph-time prepared policy;
package/package compile, analyzer and runtime duplicates remain outside the
bounded package-selection profile. Construction never falls back to inspecting
assemblies.
Outer cross-targeting and RID preparation remain unsupported. Dependency/runtime-config
actions still read declared assets JSON during execution; migrating those
consumers to facts remains required by the eventual design.
See [prepared SDK package selection](../src/msbuild/documentation/specs/proposed/hardened-static-graph-implementation.md#prepared-sdk-package-selection)
for the encoding, boundaries and execution proof.

The bounded configured SDK C# build emits full Build graphs for
hello-world, executable/library closures, package-backed .NET Standard libraries
and multiple targeting packs, executes independently, and starts executables on
the external runtime. Other copy families and broader reference
profiles still need adaptations.
SDK duplicate-item and executable-reference validation run as zero-I/O
construction tasks. `GetAssemblyVersion` also runs during construction: it
parses the supplied version string without inspecting an assembly or discovering
a version. Hardened SDK builds select a separate filename-only
`ValidateNoWinMDReferences` task, reporting `NETSDK1130` without inspecting
assemblies. Ordinary builds retain `CheckForUnsupportedWinMDReferences` and
its refined diagnostics.
Hardened editorconfig generation uses a zero-I/O `FormatAnalyzerConfig` task
and an overwrite-only `WriteAnalyzerConfig` action. It preserves the ordinary
serializer's bytes without reading a previous output; filesystem timestamp
metadata is rejected. Ordinary builds keep their existing comparison behavior.
Explicit fail-fast `ContinueOnError` values are supported. Continuing after
errors and active `OnError` handlers remain unsupported. In hardened mode,
framework attribute publication fails fast through its declared-output action;
ordinary builds retain their write-race warning behavior.
Hardened builds omit the assembly-info, compile-input, and runtime-config
timestamp caches, retaining their semantic prerequisites and caller-supplied
inputs. Ordinary builds keep those caches; execution-system reuse is separate
from target composition.
`MapSourceRoots` and the packaged GitHub, GitLab, Azure Repos, and Bitbucket URL
tasks derive mappings during construction from prepared metadata and project
context. They do not discover repositories or require source directories.
SourceLink generation composes zero-I/O `FormatSourceLink` with overwrite-only
`WriteSourceLink`, sharing the ordinary JSON serializer without comparing old
output. Empty hardened mappings clear the compiler's SourceLink argument and
emit no file action; stale JSON remains unreferenced rather than being deleted.
Ordinary builds retain their comparison and deletion behavior.
Hardened C# compilation composes zero-I/O `FormatCsc` with `CompileCsc`, leaving
ordinary `Csc` unchanged. Restore captures compiler/analyzer dependency files
in `obj/cscinputs/`; Build requires that prepared manifest rather than discovering
tool files. Determinism and deterministic source paths are mandatory. Automatic
path maps preserve SourceLink repository mappings and cover the project,
generated-source and output paths, including the PE's CodeView PDB path.
Analyzer/generator assemblies and dependencies, configuration and additional
files are declared inputs. Their nominal compiler-input-only contract is trusted;
enforcement is deferred, without per-plugin gates. The action runs an explicit
compiler on the external runtime, without compiler servers, inherited compiler
environment, default response files, installation searches or output caches.
Portable and embedded PDBs, reference assemblies, resources and explicit
documentation inputs are supported. User response files, generated-files
directories, native PDBs, rulesets, SARIF output, app-config and machine-key-container
modes remain explicitly unsupported.
Hardened SDK Build uses `WriteDepsFile` to write the dependency manifest from
explicit resolved metadata, assets JSON and declared runtime binaries. Generated
binary versions are read after their producers execute, not captured during
Restore. The writer shares the ordinary dependency model/serializer, without
build-engine or timestamp caches, filesystem fallbacks, ambient reference roots
or comparisons with old output. Logical manifest paths must be relative.
Version reads use a portable content policy: managed assembly metadata, native
PE fixed-version resources (which must agree across languages), and `0.0.0.0`
for other native formats or absent version information. Malformed or conflicting
version information fails explicitly. This deliberately avoids OS-specific
`FileVersionInfo` and installation-side resource lookup; ordinary
`GenerateDepsFile`, Publish and designer builds keep their existing behavior.
The SDK-driver dependency closure is also staged beside the SDK tasks so
independent actions require no driver process or ambient assembly searches.
Hardened Build selects `WriteRuntimeConfig` for the main runtime configuration
and the optional dev file, including Debug's Hot Reload defaults. It shares
ordinary framework selection, template merging, typed host-option precedence
and serialization, but reads declared assets/template JSON without a build-engine
cache or old-output comparison. A missing default template is omitted during
engine evaluation; explicit templates are required inputs, including paths set by
earlier targets and templates generated by preceding actions. Execution never
falls back to a template existence check. Output is UTF-8 without a BOM with LF
newlines on every host. Published probing paths must be nonempty relative paths;
backslashes and trailing separators are normalized. Requesting automatic probes
from absolute NuGet-cache roots fails rather than embedding machine locations.
Ordinary Build, Publish and designer behavior remains unchanged.
Common's hardened main assembly, reference-assembly and PDB publication uses
content-only `CopyFiles` actions with explicit source/destination vectors.
`MainAssembly`, `ReferenceAssembly` and `FileWrites` are computed during
construction, preserving source item metadata and copy opt-outs. The task
does not propagate source timestamps/attributes, compare old destinations,
use MVID/size/timestamp skips, retry, or create links. Explicit link/read-only
override modes are rejected. The copy-up-to-date marker and its bookkeeping
are omitted in hardened mode; ordinary copying and marker behavior are unchanged.
Copy-local implementation assemblies and explicitly supplied symbols use the
same content-only task, honoring `Private`, `CopyLocal`, destination-subdirectory
metadata and common-output-directory opt-outs. Documentation, discovered
satellites and other copy families remain unsupported.
Automatic `IncrementalClean`, prior/current clean-ledger collection and
`_CleanRecordFileWrites` are omitted in hardened mode. Restore no longer
captures a clean ledger, and Build does not import old clean snapshots.
Cleaning current graph outputs and safely pruning obsolete owned outputs belong
to the executor, not build actions; that functionality is not implemented yet.
Stale/unowned files are left untouched. Ordinary cleanup remains unchanged;
explicit MSBuild Clean/Rebuild and arbitrary Delete actions remain unsupported.
With explicit directory imports and construction host OS, the fresh SDK
hello-world Build graph now emits ten actions without creating product files.
Target-time property assignment preserves MSBuild escaping, so generated framework
attributes retain their semicolons and match ordinary source bytes. Independent
execution completes all ten actions, including compilation and final publication.
Action capture leaves empty scalar parameters unset, preserving ordinary task
defaults instead of explicitly passing empty strings. Default runtime configuration
matches ordinary generation, and the resulting SDK-built program runs on the
external runtime.
Common's hardened `CoreBuild` does not run failure handlers or record partial
writes for later Clean. Nonempty post-build events using `Always` or `OnOutputUpdated`
are rejected; use `OnBuildSuccess` or no event. Ordinary MSBuild retains its
failure handlers. Unsupported graph construction fails explicitly; there is
no fallback to ordinary Build.
The action host prepares declared output-file parents. Common directory
preparation is accepted only when every requested directory contains a
declared file output; unrelated empty-directory effects remain unsupported.
Intrinsic `MSBuild` calls with static project lists and parameters use the
ordinary engine's configured-project requests during construction. Children
contribute to one root graph; they never become nested-build actions or publish
partial bundles. The bound engine task is specially handled as a construction
intrinsic, not admitted as an ordinary non-pure task. Every child's evaluation
and target construction obey the same hardened restrictions as the parent.
Its `TargetOutputs` mappings return construction-time values, not results of
file actions; see [intrinsic project requests](../src/msbuild/documentation/specs/proposed/hardened-static-graph.md#intrinsic-project-requests).
Static target returns preserve ordinary metadata, global
property propagation, `Returns`/legacy `Outputs`, and duplicate-output rules.
Repeated and diamond references reuse completed configured targets; skipped
targets can run on later requests. Each action retains its own project directory.
Intrinsic project requests bypass file-action parameter capture, preserving
ordinary default-target outputs when `Targets` is omitted or expands to empty.
Construction is currently single-node and single-threaded, even when an
`MSBuild` call requests `BuildInParallel`. Unavailable project lists and custom
tasks merely named `MSBuild` remain unsupported.

SDK Restore prepares framework and compiler inputs for the project-reference
closure without compiling referenced assemblies. `AssignProjectConfiguration`
and the target-framework XML formatters run as audited zero-I/O construction
tasks over supplied metadata. Child `GetTargetFrameworks` returns match ordinary
execution, including sequential metadata assignments and item-definition defaults.
Hardened project-reference splitting leaves project-file validation to intrinsic
engine requests: missing requested projects fail with `MSB3202` rather than being
skipped by the ordinary existence split.

NuGet's `GetReferenceNearestTargetFrameworkTask` is also an audited zero-I/O
construction task. It preserves ordinary nearest-framework, alias, platform,
fallback and invalid-input behavior without opening the referenced project.
Only hardened Common targets supply the new declaration parameters; ordinary
calls remain compatible with older NuGet task assemblies.

Hardened `GetNativeManifest` does not inspect the default manifest placeholder
when no manifest generation is requested, even if stale output exists.
Ordinary existence-based queries remain unchanged; actual manifest-generation
requests still fail at unsupported discovery rather than being ignored.

Hardened `ResolveAssemblyReferences` selects zero-I/O `ResolveReferencePaths`,
not stock RAR. Inputs must be explicit file paths, relative to the captured
project directory or absolute, even when a `HintPath` is available. Simple/display
assembly names, search tokens and wildcards fail with MSB3671. The task normalizes
paths and preserves metadata without opening assemblies, checking existence,
discovering dependencies or unifying versions. Callers must supply the complete
reference closure; generated outputs remain producer edges, not Restore snapshots.
Explicit `ReferenceAssembly` metadata selects the compile contract while the
implementation path and optional `DebugSymbolsFile` select runtime copies.
Permitted `AssemblySearchPaths` settings do not enable lookup in this profile.
Ordinary builds retain stock RAR and Copy.

The packaged smoke executes a clean SDK executable/library graph after MSBuild
exits, starts the executable with its renamed library, and compares dependency
manifest bytes with ordinary Build. It also covers closure preparation, child
framework metadata/selection, a non-SDK cross-project graph, copy-local metadata
and opt-outs, stale replacement, rejected names/options and independent failures.
Empty/stale native-manifest queries and rejection of requested manifest discovery
remain covered.
Workload resolution is disabled in this prototype.

For adapting targets and tasks, see the
[conversion cookbook](../src/msbuild/documentation/specs/proposed/hardened-static-graph-migration.md).
See the [prototype roadmap](../src/msbuild/documentation/specs/proposed/hardened-static-graph-implementation.md)
for the architecture and remaining work.
