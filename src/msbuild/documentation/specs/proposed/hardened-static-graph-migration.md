# Converting MSBuild code to hardened action graphs

This cookbook describes migration patterns, not a second task contract.
The [contract](hardened-static-graph.md) defines the rules; the
[roadmap](hardened-static-graph-implementation.md) tracks implementation.
Examples below are target-body excerpts, not complete projects.

The central question is: **can this code describe its work without first
executing file-I/O actions?** Construction computes values and artifact paths;
independent actions produce file contents.

## Audit behavior before adding declarations

Trace the implementation, helpers, options, and engine services. Identify
actual reads and persistent writes, not just parameters that resemble paths.
Also check for ambient environment, installation, clock, and network access.
An annotation is a trusted assertion about behavior, not permission to do I/O.

For example, `Message.File` is diagnostic location metadata, not a file input.
After auditing the task, a call can change from:

```xml
<Message Text="Building $(MSBuildProjectName)" Importance="High" />
```

to:

```xml
<Message Text="Building $(MSBuildProjectName)" Importance="High"
         DeclaredInputs="" DeclaredOutputs="" />
```

The implementation must also have `[MSBuildDeclaredIOTask]` and public
`ITaskItem[]` declaration properties. Supplying call-site attributes alone
does not make an unaudited task eligible. Both lists must be explicitly
supplied, including when empty.

Engine services need individual audits too. `ValidateExecutableReferences`
parses supplied metadata and reads the request's global-property dictionary;
it does not open referenced projects. Preserve that provenance: a global
`SelfContained` is not interchangeable with an ordinary evaluated property.

Options can change a footprint. `WriteLinesToFile.WriteOnlyWhenDifferent`
reads an existing output; simply overwriting a file is a different operation.
Do not omit such reads, or assume that listing the same path as input and
output makes an in-place operation supported.

Real examples: [Message](../../../src/Tasks/Message.cs),
[executable-reference validation](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/ValidateExecutableReferences.cs),
and [WriteLinesToFile](../../../src/Tasks/FileIO/WriteLinesToFile.cs).

## Distinguish discovery from value derivation

A value can affect the graph without coming from Restore. Pure transformations
of supplied values belong in construction; task discovery that reads files,
tools, source-control state, or the environment belongs in preparation and must
supply explicit inputs to the fresh Build.

Engine-managed source discovery is different: projects, imports, and permitted
source globs remain evaluation inputs. For normal directory imports, pass an
absolute global `MSBuildGraphRepositoryRoot` to both Restore and hardened Build.
The built-in lookup selects the nearest `Directory.Build.props`,
`Directory.Build.targets`, or `Directory.Packages.props` through that root,
inclusively, and never searches its parent. Keep normal layered authoring rather
than flattening evaluated properties into Restore snapshots or forcing one root
props file on projects with different nearest imports. Missing files through the
root mean no import, not ambient ancestor discovery. Explicit path overrides remain
available. See the [directory-import contract](hardened-static-graph.md#repository-bounded-directory-imports).

Machine information also has a predefined-context path, not arbitrary ambient
discovery. The launcher captures OS and process architecture before evaluation.
Existing `IsOSPlatform` and `RuntimeInformation.ProcessArchitecture` expressions
read that immutable context during evaluation and target construction; runtime's
`.ToString().ToLowerInvariant()` architecture expression needs no authoring
rewrite. Keep target architecture separate, and do not generalize this support
to every environment or machine query. See the
[machine-context contract](hardened-static-graph.md#captured-construction-platform).

For example, `GetAssemblyVersion` does not inspect an assembly. It parses the
supplied NuGet version string into a four-part version:

```xml
<GetAssemblyVersion NuGetVersion="$(Version)"
                    DeclaredInputs="" DeclaredOutputs="">
  <Output TaskParameter="AssemblyVersion" PropertyName="AssemblyVersion" />
</GetAssemblyVersion>
```

Its result is available to subsequent conditions, metadata, and artifact paths
during construction. No Restore snapshot or file action is needed. If the
original version came from Git or another external source, prepare that input
first; parsing it does not grant permission to discover it during Build.
The legacy `[MSBuildPureTask]` annotation alone is not sufficient: the task
also needs the audited declared-I/O contract and explicit empty call-site lists.

The same distinction applies to source mapping. `MapSourceRoots` normalizes
supplied paths against captured project context and derives deterministic
`MappedPath` metadata without checking whether the directories exist.
SourceLink URL tasks turn supplied repository URLs, revisions, and host/version
metadata into content URLs; they do not contact the server or query Git.
Keep repository discovery and URL translation preparation in Restore, not in
these construction-time transformations.

Project-reference configuration and framework XML formatting follow the same
rule. `AssignProjectConfiguration` uses supplied project metadata and solution
XML; `CombineTargetFrameworkInfoProperties` and `CombineXmlElements` format
supplied values. They do not need the referenced project or assembly to exist.
Project evaluation remains an intrinsic engine request, not a file-I/O task
action or a reason to prebuild referenced binaries during Restore.
Keep those requests on ordinary parameter binding rather than action capture:
an empty optional `Targets` vector must stay unset so the default target's
outputs are returned, not discarded.
NuGet's nearest-framework task likewise selects from supplied framework, alias
and platform metadata, preserving fallback diagnostics without inspecting project
files. Ship its source-built dependency closure with the annotations; modifying
a source file cannot change a separately pinned binary. Keep new declaration
parameters on hardened-only call sites when ordinary hosts may load older task
assemblies.

NuGet assets are another discovery input, not a reason to run package resolution
with filesystem access during construction. Persist the consumed JSON data as
item/property facts in Restore, preserving underlying data rather than freezing
`ResolvePackageAssets` outputs and their configuration-dependent policy.
The [capture](hardened-static-graph-implementation.md#nuget-fact-capture)
encodes the complete JSON structure, and the
[pure target view](hardened-static-graph-implementation.md#nuget-fact-consumption)
exposes selected libraries and asset data without reopening JSON or packages.
This is not `ResolvePackageAssets` policy: placeholders, analyzer candidates and
project paths still need explicit policy and producer bindings. Avoid assuming
an in-memory NuGet model reader is pure; its project-metadata parser still
consults environment and user-settings context. Consume the facts directly,
keeping any additional discovery in Restore.
Package-folder order and package-location discovery matter:
an ordered list of candidate folders is not proof of which folder contains a
package. Project-produced libraries and analyzers must remain future producer
outputs, not existing-file snapshots.

Keep targeting-pack discovery separate from resolving project/tool producers.
Runtime's reference filter runs before final assembly-reference resolution and
handles both ordinary `Reference` candidates and replayed `ReferencePath` items.
Pack analyzers are captured and replayed too; project-produced analyzers replace
conflicting pack candidates without opening their binaries.
Capture only pack-owned references/analyzers, identified by `FrameworkReferenceName`.
Project-declared explicit assembly paths stay in authoring for `ResolveReferencePaths`;
do not mix them into framework snapshots or inspect their binaries in preparation.
Graph preparation does not add assembly-name or `HintPath` resolution, and legacy
manifest-only mode retains its existing `Reference` rejection.

Do not confuse a selected target framework with one `FrameworkReference` item.
Ordinary SDK authoring can contribute zero or multiple framework references.
Capture all selected packs as underlying items/properties, allow empty inventories,
and leave package-authored explicit references in their normal imports.
For example, `netstandard2.0` gets references from `NETStandard.Library` build
targets without a synthetic framework reference or a separate preparation path.
Reject stale framework-reference identity sets rather than silently replaying
inventory prepared for different inputs.

Framework inventories are candidates, not a final compiler reference list.
Combine them with package, project-output, and explicit-file candidates during
graph construction, and apply conflict policy there. Capture any external facts
that policy needs; do not move arbitration into Restore or snapshot its winners.
For a compatible newer package replacement, remove the framework's corresponding
assembly from the compiler set while retaining the other framework members.
Current replay uses `ResolvePreparedConflicts` after runtime filtering and explicit
path normalization, sharing SDK conflict policy without binary reads. Persist package
assembly/file versions and pack platform/override/preference data, not the resulting
winner list. Preserve aliased compile references and keep runtime copies/symbols
consistent with selection. Noncolliding generated references need no version facts;
colliding generated or explicit references need declared versions or external facts.
Missing facts and unresolved distinct-artifact ties fail, rather than probing files.
Package/package duplicates remain outside the bounded package-selection profile.
See the [selection contract](hardened-static-graph.md#framework-inventories-and-final-reference-selection).

For runtime library trimming, the compiler writes directly to `PreTrim/`, and
the linker produces distinct final paths for normal publication. Do not move
compiler outputs away or let both actions write the same path. XML-combining
helpers from `ILLink.Tasks` still run before compilation; moving every use of
that assembly after compilation would be incorrect. Generated-task binding and
the helpers'/linker's declared-I/O adaptations are still required.

The [prepared SDK selection path](hardened-static-graph-implementation.md#prepared-sdk-package-selection)
reuses ordinary policy with a direct fact-to-model projection and direct item
sink. Restore records selected package locations, related-file presence/absence,
locale normalization and compiler identity; Build consumes that context without
discovery. Keep the ordinary cache path unchanged, but do not serialize through
it merely to reuse selection logic. Feed standard reference/analyzer/copy-local
items so the existing compiler and publication composition retains its semantics.
Check no-discovery selection with JSON/package files absent, separately from
full graph publication, which still validates declared external inputs. The
dependency/runtime-config execution actions still consume declared assets JSON;
their eventual fact migration is not completed by making package selection pure.

Separate optional output placeholders from requested product effects.
Common always creates an `_ApplicationManifestFinal` path, even for a library
that requests no manifest generation. Hardened `GetNativeManifest` skips the
ordinary existence query in that case instead of adopting stale output. Actual
ClickOnce, Appx, native-reference or isolated-COM manifest requests still reach
the unsupported discovery path and fail; this is not blanket manifest support.
Ordinary queries retain their existing existence-based behavior.

Real examples: [assembly version derivation](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/GetAssemblyVersion.cs),
[source-root mapping](../../../../roslyn/src/Compilers/Core/MSBuildTask/MapSourceRoots.cs),
and [SourceLink URL derivation](../../../../sourcelink/src/Common/GitProvider/GetSourceLinkUrlGitTask.cs).

## Establish artifact paths before executing producers

When a generator has a known output, do not read a generated file merely to
discover that output's name. For example, this requires file contents before
the compiler's input list can be constructed:

```xml
<ReadLinesFromFile File="$(IntermediateOutputPath)generated-sources.txt">
  <Output TaskParameter="Lines" ItemName="Compile" />
</ReadLinesFromFile>
```

If the generator's interface can instead promise `Generated.cs`, describe
that artifact directly:

```xml
<PropertyGroup>
  <GeneratedSource>$(IntermediateOutputPath)Generated.cs</GeneratedSource>
</PropertyGroup>
<WriteLinesToFile File="$(GeneratedSource)" Lines="// generated source"
                  Overwrite="true"
                  DeclaredInputs="" DeclaredOutputs="$(GeneratedSource)" />
<ItemGroup>
  <Compile Include="$(GeneratedSource)" />
</ItemGroup>
```

The contents above are illustrative; the important change is that the path
is available without running the producer. Do not guard the `Compile` item
with `Exists`: a generated input need not exist during construction.
The consumer declares that path as an input, connecting it to its producer.
Use configuration-appropriate output paths so distinct configured projects
do not claim the same output.

If the output list genuinely depends on execution, redesign the interface
or leave the case unsupported. Inventing a static list is not a conversion.
The prototype cannot return properties/items from file-I/O actions; ordinary
`<Output>` mappings remain available on zero-I/O construction tasks.

Real example: the [standalone action fixture](../../../../../genbuild/tests/Action.proj)
captures a `WriteLinesToFile` invocation without creating its output.
The [compiler fixture](../../../../../genbuild/tests/Csc.proj) applies this
pattern to generated sources consumed by an independent compiler action.

## Make compiler determinism part of DeclaredIO

File lists alone do not establish a declared-I/O compiler contract. Compilation
must also be deterministic and independent of the physical checkout/CWD.
Hardened C# compilation uses the zero-I/O
[FormatCsc](../../../../roslyn/src/Compilers/Core/MSBuildTask/FormatCsc.cs)
and the independent
[CompileCsc](../../../../roslyn/src/Compilers/Core/MSBuildTask/CompileCsc.cs)
action rather than annotating ordinary Csc's server, IDE-host and tool-discovery
execution path.

Restore captures the pinned compiler and analyzer dependency closure in
`$(MSBuildProjectExtensionsPath)cscinputs/$(MSBuildProjectFile).g.props`.
SDK graph preparation/replay inserts
`$(Configuration)/$(Platform)/$(TargetFramework)/` beneath `cscinputs/`, matching
the framework, NuGet and SourceLink snapshot identity.
Build imports those explicit files; it does not enumerate external tool
directories. Keep this manifest outside Common's automatic project-extension
wildcards so it is imported only at its explicit preparation boundary.
Late-added analyzers need their dependencies supplied through
`@(AnalyzerDependency)`. Sources, references, analyzer configurations, additional
files, embedded sources, resources, signing inputs and SourceLink are semantic
inputs. XML documentation includes must be explicit `@(EmbeddedDocumentation)`
or `@(CustomAdditionalCompileInputs)` inputs, including transitive dependencies.
Do not substitute a target's timestamp lists for the compiler's actual footprint.

Both `Deterministic` and `DeterministicSourcePaths` must be true. Unspecified
source encoding defaults to UTF-8 rather than the machine's default code page.
Automatic path maps retain SourceLink's repository-root aliases; add a project fallback
only when no existing root covers it. A more-specific project map would win
Roslyn's longest-prefix lookup and break SourceLink. Cover generated sources and
output directories too: CodeView records the PDB path in the PE. User `PathMap`
does not replace this mandatory policy. The execution task enforces deterministic
compilation and the prepared mappings after the formatted options.

Declare analyzer/generator assemblies and their dependencies and trust their
nominal contract to use compiler-provided inputs without additional I/O.
Enforcement is deferred as for other tasks; this is not a per-plugin audit or
allowlist requirement. Do not disable generators merely to admit compilation.
This deferral applies to general task/tool sandboxing, not just compiler plugins;
separate action processes and declaration checks are not access enforcement.
The [deferred sandbox requirements](hardened-static-graph.md#sandbox-enforcement-deferred)
remain part of the design, without relaxing determinism or machine independence.
Generated syntax trees remain compiler outputs in memory/embedded PDB data;
data-dependent generated-files directories are a different, unsupported effect.

The action uses an explicit compiler/runtime host and a controlled child
environment, not server state, `LIB`, default response files or tool overrides.
ToolTask provides cancellation, output diagnostics and private temporary response
files for long argument lists. These are scratch files, not cached or declared
product artifacts; preserving tool temp files is rejected. There is no
task-local output caching.

The initial profile supports managed assemblies, reference assemblies, portable
or embedded debug information and statically named documentation/resource
artifacts. User response files, native PDBs, generated-files directories, rulesets,
SARIF output, app-config, machine key containers and non-public/non-delay signing
remain explicit errors. Ordinary Csc is unchanged. Verify relocation with
byte-identical PE/PDBs and mapped PDB documents, CodeView paths and CallerFilePath,
not merely a `/deterministic+` argument assertion.

## Reduce file tasks to their semantic transformation

Do not reproduce a legacy task's discovery, caches and fallback probes in its
declared-I/O replacement. Reuse the transformation and serialization logic,
but supply discovery results and context explicitly. For example,
[WriteDepsFile](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteDepsFile.cs)
shares GenerateDepsFile's dependency-model construction and JSON writer without
its build-engine object caches, timestamp version caches, installation-root
lookup or project-output existence/fallback search.

The deps writer reads declared assets JSON, the RID graph for self-contained
builds, and emitted runtime binaries when file versions are requested. Main
assembly identity, satellite paths, references and destinations are metadata.
Use explicit project-reference output paths even when project and assembly
names differ. Generated runtime binaries remain action inputs after their
producers; capturing their contents or versions in Restore would be incorrect.
Do not declare metadata-only assembly paths as reads when versions are disabled.
Keep `FileWrites` static rather than requesting execution-time task outputs.

Logical paths serialized into the manifest must be relative, including compiler
key paths. A physical input path is not automatically a valid product path.
An explicit reference-assembly root replaces environment/installation discovery.
Write the one declared JSON output without inspecting its previous contents;
there is no separate overwrite mode or task-local up-to-date decision.

[RuntimeFileVersions](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/RuntimeFileVersions.cs)
defines the hardened content policy independently of the host OS. Managed
assembly/file versions come from assembly metadata and
`AssemblyFileVersionAttribute`; missing file versions are `0.0.0.0`, with
two/three-part versions normalized to four parts. Native PE file versions come
from embedded `VS_FIXEDFILEINFO`; all version resources must agree. Other native
formats or PE files without version resources use `0.0.0.0` and have no assembly
version. Missing files, malformed version information, unsupported managed
version strings and conflicting native resources fail instead of invoking an
OS/installation fallback. These are intentional opt-in semantics, not a change
to ordinary GenerateDepsFile's OS-specific version policy.

Finally, independently execute the action without the SDK driver's dependency
context. A task that builds in-process can still be missing its pinned helper
assemblies in the action package. Stage the explicit dependency closure beside
the task rather than adding ambient parent-directory or SDK-installation probing.
The [shipped-target fixture](../../../../../genbuild/tests/Deps.proj) checks
producer ordering, relocation, stale output replacement and ordinary JSON
compatibility.

## Select optional inputs before file execution

[WriteRuntimeConfig](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/WriteRuntimeConfig.cs)
reuses runtime framework selection, template merging, typed host options and
serialization from the ordinary task. It reads declared assets/template JSON
without the ordinary assets cache and writes exactly the requested main/dev
files, including Debug's Hot Reload configuration. Keep `FileWrites` static
and omit inactive dev paths; do not request execution-time MSBuild outputs.

Select an optional default template in engine evaluation, not through a
`File.Exists` fallback inside the action. This is fresh evaluation, not a
Restore snapshot of presence. Explicit paths, including earlier target
assignments, are required inputs even when their content will be generated
later. Declare that identity and retain the producer edge instead of reading or
snapshotting generated content during Restore/construction. Missing inputs fail;
an explicitly requested template must not silently become "no template."

Validate the complete main/dev configuration before writing either document.
An invalid dev option must not overwrite the main file first. This does not
promise atomic multi-file publication if a subsequent I/O operation fails.
Use fixed LF UTF-8/no-BOM output rather than host-specific newlines.
Probing paths are serialized metadata, not directories the task may inspect.
Normalize portable relative paths, including raw template options, and reject
absolute/empty/invalid probes instead of embedding machine-specific NuGet-cache
roots. Do not silently disable requested probing behavior.

The [shipped-target fixture](../../../../../genbuild/tests/RuntimeConfig.proj)
checks optional and generated templates, target-time path selection, ordinary
main/dev compatibility, self-contained included frameworks, Release's main-only
output, relocation, overwrite behavior and independent failure. Ordinary
GenerateRuntimeConfigurationFiles, Publish and designer behavior is unchanged.

## Copy contents, not incremental state

[CopyFiles](../../../src/Tasks/FileIO/CopyFiles.cs) is a content transformation
between explicit source/destination vectors, not an annotation on the richer
ordinary Copy or CopyRefAssembly tasks. Derive destination identities and
`MainAssembly`, `ReferenceAssembly` and `FileWrites` during construction;
retain source item metadata and the target's product/symbol opt-outs.
Do not request execution-time output items from the action.

Require matching declarations and equally sized vectors. One source may feed
multiple destinations, but outputs must be unique and disjoint from every
input, including normalized path aliases. Validate the whole set before
writing, so an earlier copy cannot overwrite a later source. Resolve paths
against captured project context. Output-parent creation belongs to the host.
Copy bytes without propagating source timestamps or attributes, reading old
destinations, retrying, or using size/timestamp/MVID skips. Explicit link or
read-only override requests are unsupported, not silently ignored. Missing
sources and write failures fail; multi-file publication is not transactional.

Omit the copy-up-to-date marker and its fingerprint action in hardened mode,
including cleanup and project-reference bookkeeping. It is not product data,
and its physical-path hash and old-output comparison are not a portable
transformation. Keep ordinary Copy/CopyRefAssembly and marker behavior on
their existing path; do not alter unrelated fingerprint APIs.

The [shipped-target fixture](../../../../../genbuild/tests/CopyOutputs.proj)
checks independent main/reference/PDB publication after compilation, static
bookkeeping, metadata and opt-outs, renamed reference destinations, relocation,
stale replacement and failure. Compare ordinary and hardened copying from
identical binary inputs, rather than conflating copy behavior with differences
in compiler path-mapping policy. Modules, documentation,
satellites and other Copy families require their own adaptations. Explicit
copy-local implementation/symbol publication follows the pattern below.

## Resolve artifact paths, not assembly names

[ResolveReferencePaths](../../../src/Tasks/ResolveReferencePaths.cs) is a
zero-I/O transformation over the complete supplied reference closure, not an
annotation on stock RAR. Accept explicit paths relative to captured project
context or absolute paths, including generated files that do not exist yet.
Reject simple/display assembly names even with `HintPath`, search tokens,
wildcards, URIs and invalid paths. Permitted `AssemblySearchPaths` settings are
not permission to look up names or discover files.

Preserve caller metadata and normalize explicit `ReferenceAssembly` and
`DebugSymbolsFile` paths. Compilation consumes the reference assembly when
present; dependency manifests and runtime copies retain the implementation
identity. Project-reference metadata comes from child target returns, not
Restore snapshots of generated assemblies. Do not open binaries or inspect
old outputs to determine these identities.

Use explicit `Private` to override `CopyLocal`; absent either, framework
references are not copied and other references are. A supplied symbols path
produces a related artifact and follows the same copy policy. There is no
sibling-file, transitive dependency, satellite or assembly-version discovery.
Callers must supply any required closure and related artifacts explicitly.

Copy-local uses the same content-only `CopyFiles` action, with exact output
paths derived from `OutDir`, `DestinationSubDirectory` and filenames.
Keep `FileWritesShareable` static, honor the common-output-directory opt-out,
and reject link/read-only overrides. Duplicate/colliding outputs fail rather
than silently selecting a winner. Ordinary RAR and Copy keep their existing
behavior.

The [shipped-target fixture](../../../../../genbuild/tests/ProjectReference/ReferencePaths.proj)
checks generated compile/runtime paths, metadata, copy opt-outs, stale
replacement, rejected names/options and independent failure. The SDK
[executable](../../../../../genbuild/tests/ProjectReference/App/App.csproj)
and [renamed library](../../../../../genbuild/tests/ProjectReference/Library/Library.csproj)
exercise real compilation, implementation/PDB producer edges, startup and
ordinary-equivalent dependency JSON without prebuilding references.

## Leave caching to the execution system

Tasks define transformations, not caching strategies. In hardened mode, do
not read a previous output merely to decide whether to skip writing it.
Previous outputs are not semantic inputs to a transformation that can produce
its result from explicit parameters and declared inputs alone.

Keep action-cache lookup, up-to-date scheduling, and unchanged-output
publication in a separate, general execution system. Its reuse decisions must
account for declared inputs, parameters, toolchain, and execution context.
Ninja supplies up-to-date scheduling today; that is not a general action cache.

An overwrite-only action writes whenever the executor schedules it.
Preserving timestamps for identical contents can be a shared publication
optimization, not a per-task comparison against old outputs. Until such a
policy exists, a rerun may cause extra downstream work; do not underdeclare
reads or add manual caches to avoid it. Ordinary MSBuild's existing caching
and write-if-different behavior can remain on its separate target path.

Remove hash-and-stamp bookkeeping when it only represents property or item
changes for MSBuild's timestamp scheduler. Capture the real values in action
parameters and declared inputs instead; do not emit a cache action or register
its stamp as an extra compiler/runtime input or a cleanup output.
This does not apply to hashes that are actual product data.

Keep semantic prerequisites when a cache-wrapper target also prepares inputs.
For example, `CreateGeneratedAssemblyInfoInputsCacheFile` still depends on
`GetAssemblyAttributes` in hardened mode, but its hash, cache write, and
`FileWrites` registration are ordinary-only. Guarding the entire target would
also skip the attribute preparation. The compile-input and runtime-config
cache wrappers likewise retain resource/reference and platform preparation.

Real examples: [assembly-info cache](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.GenerateAssemblyInfo.targets),
[compile-input cache](../../../src/Tasks/Microsoft.Common.CurrentVersion.targets),
and [runtime-config cache](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.targets).

## Leave cleanup to the execution system

Do not convert a legacy cleanup ledger into Restore discovery inputs or
declared-I/O delete/reconciliation actions. Common's `IncrementalClean`,
`_CleanGetCurrentAndPriorFileWrites` and `_CleanRecordFileWrites` are
ordinary-only. Hardened Restore does not capture the ledger, and fresh Build
does not import old clean snapshots. Preserve product `FileWrites` and
semantic producers; only the automatic cleanup/bookkeeping closure is omitted.

An executor can clean current outputs from graph declarations. Pruning obsolete
outputs additionally needs persistent ownership records for previous graphs,
with protection for inputs, shared outputs and unowned files. That is separate
executor work, not a task-local cache or a reason to inspect/delete arbitrary
directory contents. Until implemented, stale outputs remain untouched.

This intentional opt-in behavior does not admit arbitrary semantic Delete
actions or implement explicit MSBuild Clean/Rebuild. Ordinary cleanup remains
unchanged. The [shipped-target fixture](../../../../../genbuild/tests/Cleanup.proj)
checks missing/stale/obstructing ledgers, retained product actions and
FileWrites, no legacy cleanup dependencies in the graph, preserved unowned
files, and ordinary deletion/ledger reconciliation.

## Split mixed responsibilities into stable tasks

A task that sometimes reads files is not unconditionally zero-I/O.
Prefer a separate, clearly bounded task when a useful operation can be
performed without those reads. Keep task selection in target composition,
not in a mode flag inside the task.

Before, the SDK always used its richer WinMD checker:

```xml
<CheckForUnsupportedWinMDReferences
  TargetFrameworkMoniker="$(TargetFrameworkMoniker)"
  ReferencePaths="@(ReferencePath)" />
```

After, the target chooses between two stable responsibilities:

```xml
<CheckForUnsupportedWinMDReferences
  Condition="'$(MSBuildHardenedGraph)' != 'true'"
  TargetFrameworkMoniker="$(TargetFrameworkMoniker)"
  ReferencePaths="@(ReferencePath)" />
<ValidateNoWinMDReferences
  Condition="'$(MSBuildHardenedGraph)' == 'true'"
  TargetFrameworkMoniker="$(TargetFrameworkMoniker)"
  ReferencePaths="@(ReferencePath)"
  DeclaredInputs="" DeclaredOutputs="" />
```

The original task inspects managed assemblies when WinMD references exist.
That inspection only refines diagnostics: every WinMD-containing invocation
already fails. The new validator checks filename extensions and reports
`NETSDK1130` without opening any references. Ordinary builds retain the
original checker and its more specific `NETSDK1149` diagnostic.
This preserves which inputs are accepted, not identical failure diagnostics.

Real examples: [SDK target selection](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.targets)
and the [filename-only validator](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/ValidateNoWinMDReferences.cs).

## Separate formatting from file publication

When a task computes text and then compares it with a previous output, extract
the transformation rather than declaring the old output as an input.
Roslyn's hardened editorconfig target uses this composition:

```xml
<FormatAnalyzerConfig
  PropertyItems="@(_GeneratedEditorConfigProperty)"
  MetadataItems="@(_GeneratedEditorConfigMetadata)"
  DeclaredInputs="" DeclaredOutputs="">
  <Output TaskParameter="ConfigFileContents"
          PropertyName="_GeneratedEditorConfigContents" />
</FormatAnalyzerConfig>
<WriteAnalyzerConfig
  ConfigFileContents="$(_GeneratedEditorConfigContents)"
  FileName="$(GeneratedMSBuildEditorConfigFile)"
  DeclaredInputs="" DeclaredOutputs="$(GeneratedMSBuildEditorConfigFile)" />
```

The formatter uses supplied values and project-relative path computation,
not source-file contents. It rejects `ModifiedTime`, `CreatedTime`, and
`AccessedTime`: requesting those metadata values would query the filesystem.
If such information is needed, prepare it explicitly and supply it as custom
metadata. A path in metadata is not automatically a file-content input.

Keep the result as exact text, not a semicolon-separated item list or an
implicitly newline-terminated write. The independent writer overwrites the
declared file as UTF-8 without a BOM, without reading its previous contents.
Ordinary builds retain `GenerateMSBuildEditorConfig` and its comparison
behavior; both paths share the serializer.

SourceLink uses the same split: `FormatSourceLink` shares the existing JSON
serializer, and `WriteSourceLink` publishes its exact text without comparing a
previous file. The compiler's `SourceLink` path and `FileWrites` are selected
from the formatter result during construction, not returned by the file action.
An empty mapping result clears the compiler property and emits no output action.
Do not probe/delete stale JSON to decide the compiler's inputs: in hardened mode
that stale file remains unreferenced. Ordinary generation retains its existing
empty-file deletion policy. Make such cleanup differences explicit rather than
quietly retaining a read/compare/delete task behind a declared-output annotation.

Real examples: Roslyn's [editorconfig target](../../../../roslyn/src/Compilers/Core/MSBuildTask/Microsoft.Managed.Core.targets)
and [SourceLink target](../../../../sourcelink/src/SourceLink.Common/build/Microsoft.SourceLink.Common.targets).

## Capture discovery during preparation, then replay it

Do not fix a construction failure by allowing an installation search, cache
probe, or source-control lookup during hardened Build. Discovery needed to
describe actions belongs in the preparation stage, with explicit captured
results consumed by a fresh construction invocation.

Before: Build assumes it can discover its SDK/reference inputs on demand.
After: run separate preparation and construction processes, using the same
configuration and intermediate paths:

```sh
dotnet artifacts/bin/genbuild/Debug/genbuild.dll restore app.csproj
dotnet artifacts/bin/genbuild/Debug/genbuild.dll build app.csproj
```

These commands use the assembled [genbuild app](../../../../../genbuild/README.md#run)
from the VMR root and still require a supported target closure. Restore is
ordinary, trusted preparation with `MSBuildRestoreGraphInputs=true`; Build
starts a fresh hardened evaluation. Restore is not itself an action graph.
Missing required captured inputs must fail, not silently reactivate discovery.

Real example: the SDK [captures and imports framework-reference inputs](../../../../sdk/src/Tasks/Microsoft.NET.Build.Tasks/sdk/Sdk.targets).
Do not use preparation to pre-execute ordinary Build or materialize generated
outputs merely to make graph construction succeed.

## Model output files, not incidental directory creation

When a directory exists only to contain declared output files, its creation
belongs to executor setup. Do not turn every `MakeDir` into an action or add a
stamp file just to give it an output.

Before:

```xml
<MakeDir Directories="$(IntermediateOutputPath)" />
```

Common's hardened structural-directory profile instead records a requirement:

```xml
<RemoveDuplicates Inputs="$(IntermediateOutputPath)"
                  DeclaredInputs="" DeclaredOutputs="">
  <Output TaskParameter="Filtered"
          ItemName="_MSBuildRequiredOutputDirectory" />
</RemoveDuplicates>
```

An actual declared file output underneath the requested directory is still
required. The graph compiler checks that coverage before publication; the
standalone host prepares output parents before executing the producer.
An already existing directory does not bypass coverage.

This is the prototype's Common-directory conversion, not a general directory
artifact API. If the empty directory itself is the intended product, do not
silently remove that effect.

Real example: Common's [directory preparation sites](../../../src/Tasks/Microsoft.Common.CurrentVersion.targets).
See the [output-parent contract](hardened-static-graph.md#output-parent-directories).

## Preserve ordinary builds and make profile changes explicit

Use target-level selection, as in the WinMD example, to preserve ordinary
behavior. Do not teach the engine SDK-specific task-name exceptions or add a
fallback to ordinary execution when graph construction fails.

Some migrations need an explicit profile decision, not equivalent syntax.
Before conversion, Common's `CoreBuild` runs failure handlers for cleanup
bookkeeping and certain post-build events. Its hardened fail-fast profile
does not run that failure-time work and rejects incompatible post-build-event
configurations; ordinary builds keep their existing handlers.
Do not generalize this into dropping arbitrary unsupported `OnError` handlers.

Real example: the [CoreBuild fail-fast profile](../../../src/Tasks/Microsoft.Common.CurrentVersion.targets),
described in the [task contract](hardened-static-graph.md#one-task-contract).

## Distinguish principles from prototype limits

Explicit dependencies, static artifact identities, audited task behavior, and
no ordinary-execution fallback are design principles. The following are
current implementation boundaries, not universal requirements for all future
action-graph systems:

| Current boundary | Migration consequence |
| --- | --- |
| File-I/O actions cannot return MSBuild values | Establish paths and graph-shaping metadata before execution. |
| File-I/O actions need persistent regular-file outputs | Do not hide an outputless validation action behind a synthetic stamp. |
| In-place updates and optional input absences are unsupported | Do not underdeclare reads to make an existing invocation fit. |
| General failure handlers and directory products are unsupported | Choose an explicit supported profile or reject the case. |
| Nonempty cross-project expansion is unfinished | Do not wrap child builds as opaque standalone tasks. |

`MSBuild` project requests represent graph composition, not a caller-owned
aggregate file footprint. Provably empty project lists are the supported
leaf-project base case, not permission to execute arbitrary nested builds.
See [graph construction](hardened-static-graph.md#graph-construction) and the
[current prototype boundary](hardened-static-graph.md#prototype-boundary-and-later-work)
before relying on a migration pattern.

## Demonstrate the construction/execution boundary

For each conversion, establish that construction works without generated
outputs and does not execute file actions. Run the emitted work separately
through independent hosts. Rejected construction must publish no partial
graph or action manifests.

Exercise the actual target call site, not only a task instantiated directly.
Include ordinary-mode behavior and the inputs that would expose accidental
I/O. For example, [packaged WinMD checks](../../../../../genbuild/tests/Smoke.proj)
use missing managed assemblies: hardened validation still reports WinMD
errors by name, while the ordinary checker retains its assembly-reading path.

Record any intentional behavior difference alongside the example. Extend
this cookbook when a conversion reveals a reusable pattern, rather than
adding a chronological list of blockers.
