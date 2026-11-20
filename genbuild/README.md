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
target-framework task sources, including the source-built C# and VB compilers, and
assembles the app in `artifacts/bin/genbuild/Release/`. It does not build the full
SDK, templates, workloads, or a runtime.

`genbuild.proj` is the complete build entry point. `genbuild.csproj` only compiles
the CLI; building that project alone does not assemble its toolset.
The compiler tasks reference source-built MSBuild Framework and Utilities for
the task environment and `SandboxedToolTask`. The component pipeline builds
their `netstandard2.0` compile-time contracts first and ships the modern
implementations. Building the Roslyn task project separately requires those
components first, or explicit `GenBuildMSBuildFrameworkAssembly` and
`GenBuildMSBuildUtilitiesAssembly` paths; cached packages are insufficient.

After the initial component build, reassemble without rebuilding components:

```sh
dotnet msbuild genbuild/genbuild.proj -p:GenBuildSkipComponentBuild=true -bl:{}
```

Release is the default for the app, source-built components, and smoke-test
toolset selection. Use `-p:Configuration=Debug` for debugging. Smoke fixtures
still exercise their intentional Debug/Release configuration cases. If necessary,
`GenBuildDotNetHost` and `GenBuildBootstrapSdk` select explicit build-time
paths to `dotnet` and its SDK directory, respectively.
After Utilities changes, rebuild both MSBuild and the action-host component
before skipping component builds: each ships its own Utilities copy.

For isolated builds, `GenBuildOutputDirectory` selects the assembled app;
`GenBuildAppArtifactsDirectory` routes CLI/toolset intermediates and their NuGet
project outputs, and `GenBuildNativeHostArtifactsDirectory` routes native-host
intermediates. The `GenBuildMSBuildArtifactsDirectory`,
`GenBuildRoslynArtifactsDirectory`, `GenBuildSdkArtifactsDirectory`,
`GenBuildSourceLinkArtifactsDirectory` and `GenBuildArcadeArtifactsDirectory`
properties select component build and assembly roots. Standalone Arcade's
NuGet project references use `GenBuildArcadeNuGetArtifactsDirectory`.
Changing only the final app directory does not isolate component builds.
The selected roots also supply analyzer files and the Framework/Utilities
compile-time contracts shipped under `toolset/ref/`; no manual DLL overlays
are needed.

To build the app and pack its runtime task integrations together, use a unique
package version:

```sh
dotnet msbuild genbuild/genbuild.proj -t:PackTaskPackages \
  -p:GenBuildTaskPackageVersion=12.0.0-genbuild.1 -m:1 -nr:false -bl:{}
```

This uses normal NuGet packing for the source-built Arcade SDK, target-framework,
XLIFF, GenFacades, templating, ApiCompat and two Roslyn analyzer packages. The
analyzer packages regenerate their graph-compatible integration targets from
source rather than reusing the authoring in downloaded packages.
The response file opts runtime's Roslyn consumers into a direct reference to
that local analyzer package, including consumers that otherwise acquire its old
authoring transitively. Without this explicit opt-in, ordinary runtime package
selection is unchanged.
The app includes the Arcade SDK authoring and generated dependency versions,
not just its task DLL.
Packages go under the app's `packages/` directory. The generated
`task-packages.rsp` binds that feed, package versions, compiler targets, Arcade
authoring and compile-time contracts. It contains absolute paths: regenerate it
through this target after changing the app location. Use new versions when task
package contents change; never edit shared package caches or repack cached
archives by hand.

The standalone pipeline has been validated for runtime's managed-libraries
Release/net11.0/linux-x64 profile after deleting normal runtime artifacts, using
fresh component outputs and an empty private package cache. The runtime-only
host completed separate Restore, graph generation and direct Ninja execution:
5,320 actions, 500 compiler actions, 7,208 file outputs and nine generated-tree
members. Launcher read enforcement was enabled. Both the immediate repeat and
a fresh-process graph regeneration reported no Ninja work; regenerated graph
and product bytes were unchanged. This is not a full native-runtime build,
a new syscall audit or a performance comparison.

For performance measurements, use Release for both the toolchain and the target
projects, with matching `-p:Configuration=Release` in Restore and Build. Prepare
fresh intermediate/output directories and repack locally built task packages
from Release outputs with new versions; do not reuse Debug task packages.

## Run

Execution requires a compatible .NET 11 runtime, but no installed SDK.
Keep the entire app directory together when copying it elsewhere.
From the VMR root:

```sh
dotnet artifacts/bin/genbuild/Release/genbuild.dll restore path/Project.csproj -p:Configuration=Release -bl:{}
dotnet artifacts/bin/genbuild/Release/genbuild.dll build path/Project.csproj -p:Configuration=Release -bl:{}
ninja -C path -f obj/genbuild/build.ninja
```

Restore and Build must remain separate invocations. `build` generates
`obj/genbuild/build.ninja` under the project's directory without running actions;
Ninja executes them. Additional arguments are forwarded to MSBuild.
Use `-p:MSBuildEmitActionGraph=/absolute/path/build.ninja` to change the graph path.
Restore accepts an explicit `-m:N` for parallel acquisition. Assembly composes
the source-built NuGet.Frameworks dependency into its packaged MSBuild manifest,
so worker nodes can use framework property functions without a manual manifest
overlay or an inherited `DOTNET_ADDITIONAL_DEPS` that could contaminate child
tools. This does not change hardened graph construction's current single-node
limit.

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

Assembly also publishes a relocatable `toolset/Roslyn/CompilerInputs.props`
catalog from the files it copies. Compiler preparation does not resolve NuGet or
targeting-pack assets: graph construction selects analyzer companions from the
acquired package file lists and packaged SDK analyzer files, after the final
`Analyzer` items are available. Only hardened Build imports the tool catalog;
Restore does not copy its static file lists into per-project snapshots.
Declare companions for loose or generated analyzers with `AnalyzerDependency`;
files added beside an extracted package analyzer are not implicitly acquired
inputs. Framework-pack analyzer companions still use the existing framework
preparation snapshot.

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
runtime-graph file. Existing acquisition visits register graph identities, including
explicit `RuntimeGraphResource` items. After successful acquisition, the root
translator compiles native RID/import/dependency/profile records into shared
resources. Unconfigured `runtimegraphinputs/Project.g.props` manifests under each
project's extensions directory import those resources. Hardened selection consumes
the native items with zero-I/O tasks, using the same NuGet matching policy over the current
framework/platform and referenced-project metadata. Restore does not capture
the winning framework. Changed runtime-graph inputs require a fresh Restore.

SDK acquisition authoring registers requirements even when Arcade's package
targets are not yet imported. Fresh Build imports the package and resource facts;
it does not replay package-only capture hooks. A different external graph needs
an acquisition-visible declaration, not another configured project traversal.

A repository consuming a pinned Arcade target-framework package must explicitly
select the local package; `ArcadeSdkDir` alone does not replace NuGet imports.
For runtime, use the app and bindings produced by `PackTaskPackages` in both
phases:

```sh
dotnet /absolute/path/to/app/genbuild.dll restore path/Project.csproj \
  @/absolute/path/to/app/task-packages.rsp -p:Configuration=Release -bl:{}
dotnet /absolute/path/to/app/genbuild.dll build path/Project.csproj \
  @/absolute/path/to/app/task-packages.rsp -p:Configuration=Release -bl:{}
```

The response file selects integration authoring; it does not prebuild runtime
products or replace the separate direct Ninja execution step. Target/profile
properties and private runtime artifact/publication paths remain explicit.

## Action-host staged file access

The action host stages every admitted action by default, using the same
declared-file staging helper as `SandboxedToolTask`. Tasks retain their logical
parameters, item identities, metadata and `TaskEnvironment.GetAbsolutePath`
results. At actual file-access boundaries, task implementations use
`TaskEnvironment.GetInputPath` or `GetOutputPath`; the host checks the existing
declarations and supplies physical execution-root paths. Ordinary MSBuild
execution resolves these APIs to the original absolute paths.

The common contract requires
`MSBuildDeclaredIOTask(SandboxVersion = 1)` and `IMultiThreadableTask`; the managed
host rejects unmigrated contracts before constructing tasks, without an
unstaged fallback. Zero-I/O construction selectors still need their explicit
DeclaredIO markers; they are not execution-host actions and do not require the
staging version. Staged `WriteLinesToFile` requires `Overwrite=true` and
`FailIfNotIncremental=false`: an old output is not an implicit input. Path-shaped
line contents remain unchanged. The host owns private cwd/temp, validates
outputs before publication, and cleans staging on success and failure.

Compiler tools reuse this host context rather than nesting another sandbox.
On the supported Linux execution platform, formatters emit typed argument
bindings even without `MSBuildSandboxToolTasks=true`. Structural directory
operands use `TaskEnvironment.GetExecutionDirectory`; this does not stage a
directory tree or authorize member discovery. The host finalizes compiler
archive/dyndep outputs inside staging before publishing any declared file.
Generated-source materialization remains separately ownership checked.

**The full staged successful-content-read audit passes.** A fresh cold
managed-libraries build completes all 5,321 execution steps at eight jobs across
24 task types, including 500 compilers without compiler staging opt-in. The
complete trace has zero undeclared successful content reads, unresolved events,
incomplete records, directory-catalog mismatches or staging violations; all
614 response-file reads have lifecycle proofs. Outputs and tree ownership
validate, the no-work check passes, and frozen inputs/products remain unchanged.
The auditor uses duration-backed shared-file-descriptor lifetimes; the earlier
capture lacking that evidence remains rejected, not waived. Metadata and
negative probes remain reported but are not strict admission failures. Runtime
and platform access uses the explicitly reviewed execution-image contract.
The earlier measurements below use the frozen compiler-only toolset, not
universal host staging.
Symlink staging is not filesystem/network confinement or read-only input
enforcement.

### Experimental launcher read enforcement

The Linux app build also compiles `toolset/read-sandbox/genbuild-landlock`
using `GenBuildCCompiler` (default `cc`). The experimental
`genbuild build <project> --enforce-reads` option wraps every managed/native task
command with this launcher. It requires explicit global construction inputs
`MSBuildActionSandboxReadFiles`, `MSBuildActionSandboxExecutableFiles` and,
where enumeration is needed, `MSBuildActionSandboxReadDirectories`. These are
absolute, semicolon-separated execution-image paths, not task-directory
discovery; escape semicolons as `%3B` in command-line property values.
`MSBuildActionEnvironment` must capture an absolute `TMPDIR`, `TMP` or `TEMP`.
Direct MSBuild callers additionally supply `MSBuildActionSandboxLauncher`.
`MSBuildActionSandboxProcessFiles` names explicitly approved process-runtime
files; currently only `/proc/self/maps` and `/proc/self/mountinfo` are accepted.
These are per-process policy records, not stable physical Ninja file inputs.

The launcher fails closed if Landlock is unavailable. It restricts file reads
and executable launches before host startup, closes inherited input descriptors,
and creates an owned private staging root. Directory-listing grants do not
grant file-content reads. Startup temporary files also use the private root;
the host adopts it without changing captured logical parameters. Policies,
launcher and static execution-image files are Ninja inputs. Ownership-checked generated
tree materialization and cleanup remain trusted operations outside the wrapper.
Writes, networking and metadata access are not confined.

CoreCLR needs process-local procfs reads, which cannot be covered by pinning
the supervisor's proc inodes. The trusted per-process supervisor/broker launches
the host and brokered tools with fresh copies of the same immutable action policy
and staging root, binding approved self-files in each child before exec.
No recursive `/proc` grant or staging-only fallback is used. The host captures
broker controls outside the logical task
environment; common `ToolTask` execution wraps the final process start info,
after task-specific environment changes. In broker mode, the trusted supervisor
owns cleanup until workers are reaped. Ordinary execution is unchanged.
Raw child processes inherit restrictions; managed subprocesses need the broker
launch path. Only explicit ordinary read-file grants for the reviewed global
`/proc/meminfo` and `/proc/sys/vm/overcommit_memory` resources are additionally
accepted; no other generic procfs files or directory grants are accepted.
These two global resources remain exact policy grants, not timestamped Ninja
file inputs: procfs timestamps reflect inode lifetime rather than content changes.

Validation passes 29 native launcher/broker and 88 host/graph/Utilities cases,
plus nine actual action pilots covering native/managed parity, constructor and
child read denials, brokered managed children and timeout cleanup. A fresh
managed-libraries graph preserves all 5,320 invocation payloads when enforcement
is enabled. Both cold executions complete at `-j8` with 500 compiler actions;
all 7,208 file outputs and nine generated-tree members are byte-identical at the
same logical paths, and repeats report no work. Frozen toolsets/products remain
unchanged. This is experimental Linux read/execute enforcement, not write,
network, metadata or adversarial confinement, and not a new complete syscall
audit or performance comparison.

## Opt-in staged compiler execution

In the earlier compiler-only toolset, Linux with modern .NET supports
`-p:MSBuildSandboxToolTasks=true` on `genbuild build` to stage C#/VB compilation.
That option defaults off; ordinary `Csc`/`Vbc` tasks remain unchanged. The newer
action-host migration above is separate. Neither adds Restore discovery.

`Microsoft.Build.Utilities.SandboxedToolTask` creates a private execution root,
symlinks only declared input files while preserving directory layout, and
provides `MapInputPath`, `MapOutputPath` and `MapDirectory` to tool authors.
`CompileManaged` uses a response template regenerated from typed formatter
properties and explicit path bindings, not replacements of original path
strings in opaque arguments or file contents. It maps the host/compiler,
working directory and deterministic path maps, and uses private response-file
and temporary-file scratch. All tool-produced declared outputs must validate as
regular files before publication. Each file is published atomically; the output
set is not a transaction if publication itself fails.

This is **staging, not OS confinement**. Absolute-path escapes, writes through
input symlinks, network access and ambient runtime/loader reads remain possible.
Do not treat input symlinks as read-only, infer hermeticity, or use this as a
security boundary. Analyzer configs are staged as ordinary declared input files:
their command-line paths are mapped, but their contents are neither inspected nor
rewritten. Relative configs retain the staged directory hierarchy. Config
producers that depend on raw absolute paths must adapt their authoring; staging
does not repair per-file selectors or path-valued settings. C# generated-source
trees use the existing declared-root archive/materializer protocol: stage the
physical archive, retain logical generated-source paths, and let the action host
write dyndep before materialization. No tree members are discovered during graph
construction. VB generated-source archives remain unsupported. Other
platforms/runtimes reject opt-in staging rather than falling back silently.

`Smoke.proj -t:TestSandboxedCompilers` exercises staged C#, public signing, VB,
explicit VB runtime inputs, DLL/PDB/reference/XML byte parity, consumer execution,
no-work reuse and failed-compilation output preservation. The Utilities tests
cover staging, cancellation and publication failures.
`TestSandboxedCompilerGeneratedFiles` runs the generated-source encoding,
logical-path, recovery, ownership, empty-tree and failure checks in staged mode.
`TestSandboxedSatelliteSource` covers SDK satellite formatting and execution with
the same staging option and typed argument bindings, and rejects silent fallback.
Private full managed-libraries validation completes all 5,321 Ninja steps at
`-j4`, with all 500 compilers staged (including 143 satellites and CoreLib's
generated-source tree), followed by a no-work repeat. Isolated Linux compiler
traces verify mapped executable/source paths; the complete runtime-libraries
read audit below predates staging and does not validate this new execution mode.

An eight-job comparison with the frozen compiler-only toolset produced:

| Mode | Restore | Graph generation | Build/execution | Total |
|---|---:|---:|---:|---:|
| Ordinary MSBuild `-m:8` | 64.1s | Included in Build | 806.0s | 870.1s |
| Prototype, Ninja `-j8` | 77.9s | 591.1s | 325.8s | 994.8s |

These are single sequential shared-host, cold-product/warm-package-cache
observations, not a statistical benchmark or developer-default compiler-server
comparison. Both modes use matching compiler binaries, no compiler server and
no node reuse. Ordinary Build uses all eight node IDs; the prototype reaches
eight concurrent actions/compilers, but hardened graph generation still forces
one construction node. All 500 compiler output identities match. Published
inventories match at 3,229 paths, with every non-DLL/PDB file byte-identical;
1,761 DLL/PDB paths differ. The execution-only number excludes the prototype's
dominant graph-generation cost.

## Linux read audit

`audit.py` observes action-host families with `strace` and checks successful
file opens, executable/readlink accesses and directory enumeration against
each action's content inputs. Order-only dependencies are not read permissions.
Run it on fresh isolated outputs; an up-to-date Ninja run does not exercise the
actions:

```sh
python3 -B genbuild/audit.py trace --strace /absolute/path/to/strace \
  --graph /absolute/path/to/build.ninja --cwd /absolute/path/to/project \
  --output /absolute/path/to/new-capture -- \
  ninja -f /absolute/path/to/build.ninja -j4
python3 -B genbuild/audit.py analyze --capture /absolute/path/to/new-capture \
  --runtime-catalog /absolute/path/to/runtime-files.json \
  --platform-catalog /absolute/path/to/platform-files.json \
  --report /absolute/path/to/report.json
```

Runtime/OS exceptions require reviewed exact-file catalogs. Optional directory
catalogs enumerate exact permitted names, not directory-prefix access.
Process-local runtime and exclusive-created/deleted case-sensitivity-probe
policies are separate opt-ins with recorded uses. Private compiler response
files have a separate same-family lifecycle policy: capture with
`--response-file-provenance`, then analyze with `--allow-response-file-scratch`.
This is not permission to read pre-existing temporary files. See
[`audit-help.txt`](audit-help.txt) for schemas, policies and limitations.
Unresolved or incomplete traces fail the audit. Metadata and failed probes are
reported separately; `--strict-probes` rejects their undeclared cases too.
This is post-execution observation, not sandbox enforcement or a complete
hermeticity proof; inherited handles, non-file inputs and filesystem races
remain limitations.

The first libraries audit is **not clean**. After a successful rebuilt-host
two-action pilot, the full run was stopped at 657 of 5,321 action families.
It found 1,427 undeclared observations, 85 unresolved events and three
incompleteness records; none were waived. Two identified defects are corrected:
graph publication binds each task assembly's adjacent `.deps.json` to its known
producer, even on a cold graph, and compiler children preserve explicitly
selected task-context `SystemRoot`, `TMPDIR`, `TMP`, `TEMP` and
`DOTNET_EnableDiagnostics` after clearing their environment. The manifest is a
content dependency with the usual forward/cycle rejection, not an ordering
chain or directory scan. XLIFF translation now creates its neighboring
serialization temporary file exclusively and write-only, rather than opening
it read/write. Fresh/replacement tests and actual-task traces preserve output
bytes and atomic publication without exempting neighboring directories.
An actual compiler pilot passes the opt-in response-file provenance check;
runtime/platform reads use exact reviewed contracts, not blanket directory
permissions.
Platform exceptions describe a particular execution runtime/OS image, not
machine-independent task inputs or a portable caching guarantee. These fixes
do not establish a clean whole-libraries audit.

Focused regressions and C#/VB smokes pass. A fresh cold 5,320-action runtime
graph adds 115 `ILLink.Tasks.deps.json` content edges and preserves the 114
already-explicit edges; all other Ninja dependency sets match after path and
invocation normalization. A fresh compiler pilot confirms the actual `csc`
child receives only the selected temp/diagnostics controls and repeats as a
Ninja no-op.

A subsequent trace completed all 5,321 execution steps successfully, but analysis
still failed closed: 716 account-lookup observations, 614 private response-file
reads and one missing Dataflow XML-documentation input. There were zero unresolved
events and four incomplete syscall records. The incomplete capture prevented
admission of all 614 response-file candidates; none were waived.

NuGet now avoids resolving the user-settings directory when experimental macros
are disabled. Actual old/new `WriteDepsFile` and `WriteRuntimeConfig` traces
remove both `/etc/passwd` and `/etc/nsswitch.conf` reads while preserving output
bytes; the native reader tests pass, including macro-enabled behavior.
Dataflow explicitly declares `XmlDocs/CommonXmlDocComments.xml` through
`CustomAdditionalCompileInputs`, also correcting ordinary incremental inputs.
A fresh cold graph preserves all 5,320 action descriptors after explicit path
normalization and that single declared-input addition, with exactly one new
Ninja content edge. The actual Dataflow compiler passes its read audit; removing
only that declaration and edge makes the negative control report exactly the
XML read. No Restore scan, account-file exemption or ordering chain was added.

Threaded-exit probes reproduce unknown syscall records with both strace 6.8 and
7.3, so upgrading the tracer alone is insufficient. The opt-in trace flag
`--seccomp-bpf` retains the same syscall selection while avoiding stops for
unselected calls. It requires a bounded live-activation probe, preserves hashed
diagnostics and rejects observed tracer errors or fallback. A 120-trial exit
probe and actual compiler pilots pass without ignoring unknown events; competing
seccomp filters and other observer limitations still apply.

The complete follow-up audit now passes all 5,321 execution steps at `-j4`:
zero undeclared successful content reads, unresolved events, incomplete records
or directory-catalog mismatches. All 614 private response-file reads satisfy the
opt-in provenance policy. The frozen app, protected originals and input canonical
paths remain unchanged; no automatic allowances were added. Traced execution
takes 15 minutes 9 seconds and analysis 28 minutes 43 seconds, not an untraced
build-performance measurement. Metadata and failed probes remain reported but
are not rejected in this non-strict profile; this is not sandbox enforcement
or universal hermeticity.

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
Analyzer project-reference checks build competing source generators with repeated
references and short-form assembly/file versions. Construction selects using
producer-owned, normalized versions without opening future DLLs; disabled generated
version attributes fail closed. Ninja-generated consumer code and actual binary
versions agree with ordinary SDK selection, and a second run is a no-op.
Source-content copies cover `PreserveNewest`, `Always` and `IfDifferent` with
existing and execution-produced inputs, stale destinations, static `FileWrites`,
ordinary byte parity and empty no-ops. Unsupported link/read-only overrides fail
construction without publishing a partial graph.
ILLink package-runtime-config checks import the actual project targets, generate
the input through the SDK action, and request only the rewritten package config.
Independent bytes match ordinary generation, including escaped content and
multiple framework versions. Construction preserves stale output; inactive
frameworks remain no-ops and write failures fail execution.
ILLink resource-substitution checks import the actual runtime authoring and
compare independent replacement bytes with stock SDK output, including literal
and empty values, sequential replacements and generated templates. The XML
combiner is itself built by Ninja from runtime's task source; selected
descriptor/substitution/attribute outputs match ordinary bytes and static
`FileWrites`. Non-resx naming ignores the unused source convention, while
source-dependent resx naming remains rejected. Disabled or ineligible NGen
optimization avoids unused profile discovery.
ILLink compile-output checks import runtime's pre-trim authoring and use recording
producers to compare ordinary and independent assembly/PDB paths and restored
metadata. Construction creates no product directories; inactive trimming remains
a no-op, and a directory requirement without a future output fails publication.
Invocation checks build the real runtime linker task and a recording linker through
Ninja, with both tools and compiler inputs absent during construction. They verify
declared root/PDB/XML, reference/search-candidate, tool/runfile and external-host
inputs, refreshed discovery after a new search candidate, ordinary output parity
and re-execution after changing a reference sidecar. This tests the invocation
contract, not the recording linker's trimming semantics.
Diagnostic checks cover ordinary warnings, global and project-local promotion,
warning exclusions and message demotion, including child projects. Failed
construction preserves existing graph bytes and sidecars; publication I/O errors
fail completion and clean up new metadata. Successful graphs remain independently
executable.
Arcade checks compare ordinary and prepared P2P selection, platform fallback,
RID-agnostic metadata, explicit framework overrides and incompatible references.
They also exercise real NuGet-imported capture hooks, literal JSON transport,
replay without the live runtime-graph file, and fail-closed prepared inputs.
Runtime library-layout checks import the shared runtime authoring and compare
legacy binplace, ordinary publication and independent execution. They cover
future products, custom item lists and target paths, platform fallback, inactive
inner frameworks, configured symbols/docs, duplicate placements and collisions.
Explicit publication also works with automatic binplacing disabled.
Query-only requests do not build or copy products; construction preserves stale
destinations and execution consumes the declared producer outputs.
Aggregate testhost fixtures import runtime's library producer and consumer
authoring with two configured projects. They compare stock SDK publication and
ordinary cleanup with independent Ninja execution, including custom native
items, cross-project deduplication/conflicts, property negotiation, transferred
clean ownership and preservation of foreign runtime files. A selected output
exercises its future producer edge; the current emitter's conservative ordering
can also execute preceding actions, so this is not a minimal-closure test.
Repository-info checks preserve provider URL replay, Azure DevOps mirror
remapping, the translation opt-out and unrelated source-root metadata, then
independently publish metadata matching ordinary builds.
Roslyn analyzer-config checks run the source generator for both the core and
Banned API analyzer packages, verify declared inventories against every emitted
file, and select configs with those files absent during construction. Banned API
uses its actual release-tracking opt-out policy and emits an empty inventory.
Version, mode, category, warnings-as-errors, empty and unsupported selections match
ordinary execution for both packages. Ordinary custom directories remain supported
even with an empty package inventory, and fail closed in hardened construction.
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
All compiler fixtures import Arcade's resource-source targets. Empty resource
lists and resources without source generation are explicit no-ops, while the
nonempty accessor cases still generate and compile source. This does not rely on
incremental target skipping.
A configured fixture retains parent/child multi-targeting declarations, prepares
multiple parent frameworks/configurations, and independently builds a Debug/net11
parent with its negotiated Release/net10 child. Outer Restore acquires resources
without configured selection; attempts to prebuild tools/products during
acquisition fail explicitly.
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

Framework-reference selection runs in fresh Build for each configured
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
Direct acquisition writes an unconfigured
`obj/frameworkpackinputs/$(MSBuildProjectFile).g.props` locator to shared pack facts,
not a per-configuration framework snapshot. A shared native catalog captures installed
SDK display-name overrides, including platform aliases. A zero-I/O task selects the
current platform's display name before framework metadata queries/validation, with
the ordinary identifier/version fallback when no override exists. This late SDK
fixup is target-time in hardened mode; ordinary evaluation is unchanged. The separate
Common platform-SDK location/presence facts are published during existing
per-framework acquisition visits, separately from this display-name catalog.
Changing the framework-reference identity set requires a fresh Restore.
Outer cross-targeting Restore is acquisition-only; this does not broaden the
bounded hardened Build's RID support.
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
Restore publishes resource facts from the per-framework visits NuGet already
makes for acquisition. There is no post-Restore configured preparation closure,
nested child Restore/fresh capture, recursive `PrepareProjectReferences` walk,
or framework-discovery capture. Fresh Build imports acquired facts and newly
restored package authoring, then negotiates project-reference frameworks and
selects the configured closure. Acquisition requirements must be visible before
package-only Build authoring is imported; no replacement traversal is added.
Acquired framework, NuGet, Common cache/platform, and enabled source-control imports
remain required during hardened evaluation; missing facts fail rather than
being hidden by optional imports. There is no metadata-only mode or discovery
fallback. The [packaged regression](tests/DiscoveryPreparation.csproj) requires
facts for all four inspected frameworks using only ordinary NuGet acquisition
visits from a [plain root](tests/DiscoveryAcquisition.proj). It rejects child
Restore, capture and configured reference preparation. Cold acquisition fetches
a local package whose Build authoring adds framework metadata; fresh ordinary
and hardened Build return identical metadata, including the package's values
for every framework. Up-to-date acquisition republishes deleted native facts.
Missing NuGet, framework, source-control, unmanaged-cache or .NET Framework
platform-SDK facts fail their required imports without publishing a partial graph.
Independent execution compiles only the negotiated dependency. A separate
configured fixture preserves additional/removed reference properties and
configuration-specific compiler inputs while accepting outer acquisition.
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
package root; original binaries and config payloads are preserved. The pinned
Banned API package intentionally ships no global configs, so its declared
inventory is empty; analyzer DLLs and ordinary custom-directory behavior remain
unchanged. The existing generic generator handles this without further changes.
`RestorePackagesPath` and Arcade's early `NuGetPackageRoot` are both bound to
this root, with the shared package cache retained as a read-only fallback.
Roslyn's skipped-analyzer marker uses the existing overwrite-only file writer
in hardened mode, declaring the intermediate assembly as its input. Independent
execution publishes an empty marker after successful compilation; ordinary builds
retain `Touch`. Neither the clock nor the marker's prior contents become action
inputs, and graph construction does not update the marker.
Arcade's resource-source batch target explicitly requires a nonempty selected
resource list, preserving ordinary no-op behavior without relying on target
incrementality.
Runtime's GenAPI implementation-assembly discovery runs only before the
on-demand `GenerateReferenceAssemblySource` inner-build target, after that
target's reference-resolution dependencies. Normal compilation does not need
GenAPI's CoreLib probes or a snapshot of them. Ordinary explicit generation
retains the existing path policy; hardened GenAPI execution is not implemented.
ILLink now declares the linker and shared-resource manifest names directly in
its project authoring. These match ordinary MSBuild's names and remove the need
for source-dependent naming without disabling the convention globally.
The linker also disables preserialized-resource support: both resource files
contain only inline strings. The focused regression uses the actual resources
and SDK default, verifying byte-identical ordinary and independent compilation,
including deferred RESX producers. Unsupported serialization modes and non-string
payloads remain rejected.
Arcade's `InternalsVisibleTo` generation reuses `WriteCodeFragment` with an
explicit output and no file inputs in hardened mode. Compile and clean items
are published from that known path, including F#'s `CompileBefore` ordering.
Ordinary task invocation and friend-assembly key policy are unchanged.
ILLink's package-runtime-config rewrite now uses Arcade's `RegexReplaceFile`
worker in hardened mode. It consumes the execution-produced runtime config and
writes the known package-config path with the same regex and version properties.
Ordinary builds retain the original expressions and writer. The worker preserves
UTF-8/no-BOM output and the writer's appended newline, without reading prior output.
Common's source-content publication uses the existing `CopyFiles` worker with
explicit inputs/outputs and static `FileWrites` for all three copy modes.
Ordinary copy options and incrementality remain unchanged; hardened publication
does not depend on prior destination state and rejects link/read-only overrides.
Construction advances through the ILLink analyzer, code-fix and linker reference
projects, resource actions, IVT generation, Banned API config selection,
package-runtime-config generation, source-content copies and library binplace.
Runtime's shared `eng/libraryLayout.targets` exposes configured products and
destination/source mappings without probing product existence. Existing
`BinPlace` entry points delegate to these queries and publication, preserving
ordinary paths while enabling future consumer-owned aggregate layouts.
The first consumer is `sfx-src.proj`, behind the ordinary
`UseAggregateLibraryTestHostLayout=true` opt-in. It owns library-produced
testhost mappings, while native-library and external runtime/host staging remain
separate. Participating producers retain their other binplace destinations.
Ordinary cleanup uses the aggregate's existing file-write machinery; hardened
construction neither reads nor emits a clean ledger.
Hardened framework selection uses prepared runtime-graph JSON with the same
NuGet policy; placement uses explicit `CopyFiles` actions and static `FileWrites`.
Ordinary publication retains its copy/link options.
The guarded ordinary query returns Immutable's future assembly/PDB/XML paths
without producing them. For the selected `net10.0` inner build, all three layout
framework requests choose `net11.0`, so that build correctly has no placements;
the isolated regressions exercise positive layout publication.
An additional guarded query of the actual `sfx-src.proj` consumer, restricted to
Immutable through its existing custom Traversal import hook, selects `net11.0`
and returns three future testhost mappings (assembly/PDB/XML), all with absent
sources and destinations. This verifies positive configured composition, not a
full-framework build or runtime execution.
ILLink's resource-substitution template transformation now uses an execution-time
`ReplaceTextFile` worker with distinct declared input/output paths and the
legacy writer's text-item conversion. XML combination declares its source files
and known outputs, and the future combiner task carries the same I/O contract.
Non-resx manifest naming no longer rejects the unused `UseDependentUponConvention`
flag. Immutable's inline-string resources explicitly disable object serialization;
their ordinary and independent bytes match the SDK-default baseline.
Runtime's missing-documentation warning now runs as an explicit zero-I/O
construction task. Graph validation and formatting produce an in-memory
publication plan; only successful root-submission completion, after logging and
warning promotion finish, publishes Ninja and its sidecars. Failed reconstruction
leaves any previous graph untouched.
ILLink's pre-compile target now records its trim-input directory as a structural
requirement in hardened construction; future compiler execution creates the
parent. Ordinary directory creation and assembly/PDB remapping are unchanged.
Declared-I/O ILLink receives explicit assembly/reference paths, XML and tool
files; external symbols and companions are authored as `ILLinkAdditionalInput`.
Known producer outputs conservatively cover future companions and tool runfiles.
There is no per-project linker directory/PE inventory during Restore.
The task rejects `-d`, indirect file lists and unsupported discovery arguments.
During execution, the linker resolves only its explicit references, reads symbols
and copies sidecars/satellites only from declared files, and checks output paths.
An undeclared neighboring file cannot affect the action; unresolved identities
follow the existing explicit linker policy without directory probing.
The linker uses the explicit external host and owns the final DLL/conditional PDB.
Custom steps, dependency dumps, multi-module assemblies and linked resources
currently fail closed in this profile. Ordinary ILLink retains its existing
discovery and behavior.
Common's XML-documentation publication now uses declared `CopyFiles` and static
`FileWrites`; runtime's documentation override staging uses the same worker,
without hardened timestamp touching. Ordinary copy/touch behavior is retained.
Focused regressions cover future compiler documentation, renamed destinations,
disabled publication, stale replacement, ordinary bytes and override staging.
The isolated runtime profile must restore packages without fallback folders:
runtime's `PackageDownloadAndReference` authoring addresses the explicit primary
package root, not a shared fallback location.
Hardened `CallTarget.TargetOutputs` now materializes returned items and properties,
preserving escaped identities, metadata, duplicate policy and cached returns.
Caller-scope completion carries concrete mapped items rather than availability
alone; empty/skipped returns retain existing items and clear property outputs.
The SDK's existing transitive-copy hooks now publish all four linker runtime
manifests to ILLink.Tasks' live tool directory, with declared copy producers and
ILLink input dependencies. No additional runtime copy authoring is needed.
The Immutable closure's 82-action graph now executes successfully, including
real trimming and final DLL/PDB publication. Published bytes match ILLink outputs,
and a second Ninja invocation reports no work. Reconstruction leaves existing
product files unchanged; shared cache hashes are unchanged. This validates the
managed Immutable closure, not a complete native runtime build.
An additional fresh-product probe confirms the same 82-action result from an
empty tree: preparation/construction produce no tool or library binaries,
independent execution publishes the final DLL/PDB, and repeated Ninja has no work.
The current `net11.0` profile adds in-tree framework-library references and reaches
CoreLib and interop source generators. A pure CoreCLR symbol-folder expression
removes its first evaluation blocker, with ordinary/hardened path parity verified.
XLIFF localization now uses audited metadata-only item derivation, a read-only
freshness action and independent translation actions with declared source/XLF
inputs and translated-file outputs. The app builds and stages the selected tasks
and targets at `toolset/Arcade/XliffTasks/`. Pinned package consumers must pack
`genbuild/arcade/xliff/XliffTasks.csproj` with a local version and select it through
`MicrosoftDotNetXliffTasksPackageVersion` and `RestoreAdditionalProjectSources`;
staging the toolset alone does not replace restored package imports.
Use `UpdateXlfOnBuild=false;ErrorOnOutOfDateXlf=true` for the runtime profile:
Arcade otherwise enables developer source updates outside CI. Explicit hardened
`UpdateXlf` requests fail rather than modifying checked-in localization sources.
`TestXlfLocalization` verifies ordinary/action byte and metadata parity for resx
and JSON, both filename modes, compiled resources, neutral-XLF validation,
incremental translation edits and missing/stale/malformed/schema-invalid XLFs.
An isolated invocation of the real `Microsoft.Interop.SourceGeneration`
localization target executes one validation and all 13 translation actions;
repeated Ninja has no work and source/cache hashes remain unchanged.

SDK satellite-source generation now defers parent-DLL attribute extraction and
source writing together through
[`GenerateSatelliteAssemblySource`](../src/sdk/src/Tasks/Microsoft.NET.Build.Tasks/GenerateSatelliteAssemblySource.cs).
It composes the existing `GetAssemblyAttributes` and `WriteCodeFragment` tasks,
preserving authored attributes, parent-derived values, culture and escaping.
Hardened satellite compilation and publication use `FormatCsc`/`CompileCsc` and
`CopyFiles`; ordinary extraction, compilation and copy behavior remain unchanged.
The app stages `Microsoft.Build.Tasks.Core` and `System.CodeDom` beside the SDK
tasks; the existing tool-directory input inventory tracks both for Ninja
invalidation. No runtime is bundled.
`TestSatelliteSource` covers two cultures, ordinary generated-source byte parity,
all inherited/authored attributes, parent assembly versions that differ from
MSBuild properties, selected-output producer ordering and incremental parent-DLL
changes. Both ordinary and action-built executables resolve the translations.
The complete real `Microsoft.Interop.SourceGeneration` project now executes
67 actions, including one XLF validation, 13 translations, 13 satellite-source
generations and 14 compilations. All 13 satellites are published; repeated Ninja
has no work, with localization sources and shared-cache hashes unchanged.

CoreLib now names its string resource explicitly with its existing manifest name
and selects inline-string resource compilation without object serialization.
Its descriptor-combination and runtime-root-generation tasks declare their exact
inputs and outputs. A resource/descriptor-only CoreLib profile independently
executes 221 actions, including its tool producers; descriptor bytes match
ordinary task execution and string-resource bytes match ordinary compilation.
The profile excludes CoreLib compilation, and repeated Ninja has no work.

`EmitCompilerGeneratedFiles` now uses a compiler-owned output tree. The source-built
C# compiler retains the configured logical generated-source paths, including
analyzer-config matching, `CallerFilePath` and embedded PDB documents, while
persisting a deterministic ZIP instead of writing the tree during compilation.
After successful compilation, the action host writes Ninja dyndep metadata and a
separate materializer publishes the tree and its ownership receipt. Missing files
are recovered from the archive without recompilation; changed hints prune only
previously owned files, and zero generated sources publish a real empty directory.
Unowned files, symlinks and invalid archive/receipt paths are rejected. Ordinary
Csc still writes generated sources normally, without sidecars. Tree contents are
not construction-time item discovery or supported directory inputs.

`ClearAssemblyReferenceVersions` now supports a distinct declared output.
Hardened builds retain the raw compiler assembly and publish a rewritten
`ZeroVersionSeeds` intermediate, preserving item metadata, debug backups and
clean bookkeeping without relocating symbols or documentation. Ordinary builds
retain their in-place rewrite. `TestClearAssemblyReferenceVersions` verifies
producer ownership, zeroed reference versions, untouched inputs, ordinary
DLL/PDB/XML byte parity, no-work and rejection of incorrect footprints or
malformed assemblies.

Forwarded-type source generation in `shims/stubs/Directory.Build.targets` now
declares no file inputs and exactly one generated-source output. Its static item
metadata supplies the content. `TestRuntimeForwardedTypes` verifies ordinary
source-byte parity, actual plain/nested-type compilation, producer ownership,
future compiler inputs, clean bookkeeping, metadata-change invalidation, no-work,
and the original empty-item error without construction-time products.

Hardened ASN.1 generation now transforms XML into private `obj/asnxml` sources and
remaps only the corresponding `Compile` items, preserving metadata and recording
clean ownership. It never rewrites checked-in generated files or feeds a
file-dependent comparison result back into construction. Ordinary builds retain
their regeneration, comparison, timestamp and warning behavior. Declared
`XslTransformation` invocations validate exact file footprints, reject overlapping
or duplicate outputs, and prohibit trusted settings, DTDs and external stylesheet
resolution. Ordinary transformations retain their existing modes and resolver.
The focused runtime smoke checks real stylesheet byte parity, compile metadata,
unrelated sources, no-work, XML-edit invalidation and execution-time schema errors.

The whole libraries traversal now constructs a 5,321-action Ninja graph without
product assemblies or resources. Its 58 ASN sources have one transform producer
and explicit compiler dependencies. A fresh real VB-shim profile completes all 1,448 Ninja
actions, including source-built VB compilation, reference rewriting/publication
and 29 ApiCompat validations; repeated Ninja has no work. Runtime authoring supplies
the implementation-side pack fallback and transitive private references without
promoting public implementation binaries over pack references. Linking explicitly
precedes ApiCompat input collection, so validation checks the final assembly and
existing suppression identities remain valid. No validation was disabled or new
suppression added; shared-cache hashes are unchanged. The whole-partition Ninja
run generates all 58 ASN sources, matching checked-in sources modulo BOM/line
endings. A source-built Roslyn ref-safety assertion on constructor-assigned
getter-only ref-like auto-properties is temporarily avoided with equivalent
explicit readonly backing fields in `PasswordBasedEncryption` and the DNS record
types. This also avoids the problematic path with Debug compiler assertions
enabled; no escape checks were relaxed.

ApiCompat's default suppression-file presence is now observed after successful
root acquisition, from SDK-visible bindings independent of package imports and
configured validation settings. Fresh Build shares one unconfigured fact file
across frameworks and selects the references and suppression inputs normally.
Up-to-date Restore refreshes negative facts and republishes deleted files without
child Restore or capture. Configured preparation and discovery traversal have
also been removed; normal NuGet acquisition traversal remains.

The emitter no longer adds a blanket previous-action order-only dependency.
A fresh private libraries profile completes the untouched emitted graph with
`ninja -j4`, without resume or hand-edited fork/join diamonds: 5,320 task actions,
7,208 declared file outputs and one generated-source tree. Ninja's log records
four overlapping actions and four overlapping compiler invocations; all 500
compiler actions execute. Repeated Ninja reports no work. A focused regression
also checks that requesting one independent branch does not build the other,
and editing its input leaves the other branch unchanged. This validates the
observed parallel execution, not filesystem isolation, runtime tests or the
entire runtime build.

An initial matched Release comparison uses private product roots, warm package
and OS caches, the same source-built C#/VB compilers, shared compilation off,
and four execution slots in each mode. These are single shared-host runs, not
statistically controlled speed claims or a developer-default compiler-server
baseline. Seconds below exclude toolset assembly and validation bookkeeping:

| Timed work | Ordinary MSBuild | Prototype |
| --- | ---: | ---: |
| Acquisition | 57.5 | 73.4 |
| Cold Build (including graph construction) | 900.0 | 1,048.8 |
| Acquisition + cold Build | 957.6 | 1,122.3 |
| No-op Build, reconstructing the graph | 49.5 | 482.6 |
| One-file comment edit and Build | 70.8 | 501.7 |

Prototype cold construction takes 509.3 seconds and Ninja execution 539.6;
reusing an existing graph makes the Ninja-only no-op 0.3 seconds. Reconstruction
alone takes 482.3 seconds for no-op and 486.5 for the edit, followed by 15.3
seconds of edited execution. Both modes execute 500 cold compiler commands,
zero no-op compilers and five edited compilers; the edited Ninja closure has
44 actions. The source edit is restored byte-for-byte. The construction cost,
not acquisition, currently erases the parallel-execution benefit; graph reuse
must not be presented as the cost of a fresh end-to-end no-op Build.

Published path inventories match (3,229 paths each); all non-DLL/PDB files
match bytes. Four cryptography DLL copies have differing raw IL; normalizing
metadata tokens and file-local path checksums makes all 7,458 method signatures,
instructions, locals and exception regions match. This is not exhaustive
semantic or byte equivalence: debug/identity and metadata differences remain.
The timings precede the audit-driven action-host resolver correction; do not infer
final-host performance from them. Instrumented read-audit time is separate.

The isolated Release libraries profile uses `EnableNgenOptimization=false`,
matching the earlier Debug profile's lack of legacy IBC data. This does not
disable C#/VB Release optimization. Stock Release enables an external IBC-profile
glob that still needs a declared discovery model; this profile does not validate
the IBC/ReadyToRun/native-PGO pipeline.

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
Outer cross-targeting Restore does not perform configured selection; RID support
remains bounded. Dependency/runtime-config
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
Hardened C#/VB compilation composes zero-I/O `FormatCsc`/`FormatVbc` with
`CompileManaged`, leaving ordinary `Csc`/`Vbc` unchanged. Build imports the
packaged `CompilerInputs.props` tool catalog and selects analyzer companions from
acquired package/framework inventories and explicit `AnalyzerDependency` items.
No per-project compiler manifest or tool-directory discovery is needed.
Determinism and deterministic source paths are mandatory. Automatic
path maps preserve SourceLink repository mappings and cover the project,
generated-source and output paths, including the PE's CodeView PDB path.
Analyzer/generator assemblies and dependencies, configuration and additional
files are declared inputs. Their nominal compiler-input-only contract is trusted;
enforcement is deferred, without per-plugin gates. The action runs an explicit
compiler on the external runtime, without compiler servers, inherited compiler
environment, default response files, installation searches or output caches.
Portable and embedded PDBs, reference assemblies, resources and explicit
documentation inputs and compiler-owned generated-source trees are supported.
User response files, arbitrary directory outputs/inputs, native PDBs, rulesets,
SARIF output, app-config and machine-key-container
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
Both manifest writers run as independent file-producing actions, not during
graph construction. Their assets/template/runtime-graph JSON can remain declared
file inputs: selecting their output contents does not require native item facts
or another Restore pass. Native facts are required for data used to construct
the graph; execution still requires all consumed file inputs and output paths
to be known, with no ambient package/settings discovery.
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
Construction is currently in-process, single-node and single-threaded, even when an
`MSBuild` call requests `BuildInParallel`. Unavailable project lists and custom
tasks merely named `MSBuild` remain unsupported.

SDK Restore acquires framework/package resources and publishes native facts
without a separate configured project-reference walk or referenced assemblies.
`AssignProjectConfiguration`
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
