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

This builds the selected MSBuild, SDK-task, Roslyn, SourceLink, and NuGet projects and
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

Projects may retain `TargetFrameworks` when both invocations select an inner
build with the same `-p:TargetFramework=...` and `-p:Configuration=...`.
Preparation follows normal project-reference framework/configuration negotiation
and property metadata. SDK framework, NuGet, compiler and SourceLink snapshots
are separated by `Configuration/Platform/TargetFramework` beneath their respective
intermediate directories. Other input-affecting global properties still require
distinct intermediate paths.

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
A configured fixture retains parent/child multi-targeting declarations, prepares
multiple parent frameworks/configurations, and independently builds a Debug/net11
parent with its negotiated Release/net10 child. Outer preparation and attempts
to prebuild tools/products fail explicitly.
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
legacy manifest rejection and empty-framework diagnostics are also checked.
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
disables that resolver during hardened Build; package-SDK import bindings still
need explicit capture and replay rather than package-cache searches or downloads.

Framework-reference preparation accepts one selected framework per configured
project, including package/project references, explicit assembly paths and retained
`TargetFrameworks`. Framework snapshots contain only pack-owned reference/analyzer
candidates; project-declared references stay in authoring for path-only resolution.
These are inventories, not final compiler reference sets. Final selection belongs
in graph construction: a zero-I/O adapter now reuses SDK conflict policy after
runtime filtering and path normalization. Restore captures package binary versions
and pack platform/override/preference facts, not winners. A newer package can
replace a framework member; an older package can lose without discarding unrelated
framework references. Colliding generated/explicit files need declared version
metadata or prepared external facts; missing facts and unresolved ties fail.
See the [reference-selection contract](../src/msbuild/documentation/specs/proposed/hardened-static-graph.md#framework-inventories-and-final-reference-selection).
Legacy manifest-only mode retains its narrower rejection of `Reference` items.
Outer cross-targeting and RID preparation remain unsupported.
The real `System.Collections.Immutable` trial targeting runtime's `net10.0`
minimum now gets past the multi-targeting guard. Runtime's
`FilterImplicitAssemblyReferences` now runs before final assembly-reference
resolution rather than during targeting-pack discovery. Preparation reaches
`ILLink.Tasks` without building any product binaries and accepts its explicit
package-supplied MSBuild references authored by `PackageDownloadAndReference`.
Preparation now fails with `NETSDK1247` on the empty `FrameworkReference` list in
`ILLink.CodeFixProvider` and `ILLink.RoslynAnalyzer`: their `netstandard2.0`
package-backed profile is not the supported implicit `Microsoft.NETCore.App`
profile. The producer dependencies remain intact.
Runtime library trimming compiles directly to `PreTrim/` and links to separate
final paths, without moving or rewriting compiler outputs. Generated-tool task
binding and declared-I/O adaptations, package-backed framework preparation,
package-SDK import replay and broader compiler/tool profiles remain necessary.
The trial uses ordinary
Restore (`RestoreUseStaticGraphEvaluation=false`): the static Restore launcher
still assumes an installed SDK layout. No runtime library graph, compilation
or independent execution is established.

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
Content preprocessing, additional frameworks and simple framework-assembly names
fail explicitly. Framework/package conflicts use graph-time prepared policy;
package/package compile, analyzer and runtime duplicates remain outside the
bounded package-selection profile. Construction never falls back to inspecting
assemblies.
Outer cross-targeting and RID preparation remain unsupported. Dependency/runtime-config
actions still read declared assets JSON during execution; migrating those
consumers to facts remains required by the eventual design.
See [prepared SDK package selection](../src/msbuild/documentation/specs/proposed/hardened-static-graph-implementation.md#prepared-sdk-package-selection)
for the encoding, boundaries and execution proof.

The bounded single-framework SDK C# profile emits full Build graphs for
hello-world and an executable/library closure, executes independently, and
starts on the external runtime. Other copy families and broader reference
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
