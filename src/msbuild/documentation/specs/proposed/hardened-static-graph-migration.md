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
This pattern does not imply that the full SDK compiler closure is supported.

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

Real example: Roslyn's [editorconfig target](../../../../roslyn/src/Compilers/Core/MSBuildTask/Microsoft.Managed.Core.targets).

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
