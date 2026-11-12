# Tasks Agent Instructions

Guidance for changes under `src/Tasks` (MSBuild tasks, targets, and SDK build messages).

## Where things live

`Microsoft.NET.Build.Tasks` and `Microsoft.NET.Build.Extensions.Tasks` build for
both `net472` and current .NET, so code added to them must stay
`net472`-compatible (no .NET-only APIs). `Common` is linked shared source
compiled into each, so it inherits that constraint; `sdk-tasks` is
current-.NET-only:

| Project | Role |
|---------|------|
| `Microsoft.NET.Build.Tasks` | The core `Microsoft.NET.Sdk` task assembly **and** the targets that compose `dotnet build`. Ships in the SDK. |
| `Common` | Common shared source **linked into** both task assemblies — not its own package. |
| `Microsoft.NET.Build.Extensions.Tasks` | Tasks/targets for desktop/.NET Framework projects built **outside** the SDK. |
| `sdk-tasks` | Build-time-only tasks for **this repo's own** build/packaging — never shipped to users. |

### Inside `Microsoft.NET.Build.Tasks`

- Root `*.cs` — the MSBuild `Task` classes.
- `targets/` — the shipping `.targets`/`.props` that drive a build;
  `Microsoft.NET.Sdk.props` / `Microsoft.NET.Sdk.targets` are the top-level entry
  points, and `Microsoft.NET.Sdk.Common.targets` registers the diagnostic tasks.
- `sdk/` — `Sdk.props` / `Sdk.targets`, the entry points when the SDK is referenced
  via the `<Sdk>` attribute.
- `FrameworkPackages/` — per-TFM runtime framework version data.

## Declared compiler package inputs

Hardened `ResolveNuGetAssets` publishes `AnalyzerDependencyCandidates` from the
package file inventories in its already-reconstructed assets model. These are
candidates, not selected analyzer dependencies: Roslyn selects companions after
the final `Analyzer` items are available, including package-target additions.
Do not restore configured analyzer resolution or directory scans in compiler
preparation. Explicit `AnalyzerDependency` authoring covers loose/generated files
and package-adjacent files absent from the acquired inventory.
Both primary and satellite `CompileManaged` calls consume `_CompilerToolInput`
directly from the packaged Build catalog, not Restore snapshots.

Existing per-framework acquisition visits declare finite pack requests and lookup,
version, member-catalog and resource-output bindings through
[`CollectFrameworkPackInputs`](Microsoft.NET.Build.Tasks/CollectFrameworkPackInputs.cs).
After successful root acquisition,
[`WriteFrameworkRestoreInputs`](Microsoft.NET.Build.Tasks/WriteFrameworkRestoreInputs.cs)
validates compatible resource scopes and invokes
[`WriteFrameworkPackInputs`](Microsoft.NET.Build.Tasks/WriteFrameworkPackInputs.cs)
once per shared output, not once per configured preparation request.
These are raw acquired resources, not configured references, analyzers,
runtime frameworks or apphost paths.
[`PreparedFrameworkTasks`](Microsoft.NET.Build.Tasks/PreparedFrameworkTasks.cs)
reuses SDK selection with those resources during graph construction, without
filesystem/workload discovery fallback. Archive member inventories preserve
analyzer companions absent from FrameworkList; loose packs need explicit catalogs
when FrameworkList is incomplete. `_CompilerFrameworkFile` supplies these candidates
to final analyzer dependency selection. Platform display-name observations and the
shared pack-manifest locator remain in the narrow configured
[`WriteFrameworkPlatformInputs`](Microsoft.NET.Build.Tasks/WriteFrameworkPlatformInputs.cs)
producer. Import that locator before the shared facts so alternate Build entry
points find the original acquisition scope. The configured preparation walk remains;
do not add reference traversal or new selection passes just to collect resource facts.
Native pack format 2 compiles FrameworkList attributes, platform rows, package override
relationships and SDK runtime-graph records into item metadata during acquisition.
Do not embed original resource text in `Content` or reparse it during construction;
preserve record order and empty/absent distinctions and leave selection in Build.

## Declared ApiCompat reference inputs

ApiCompat Restore capture records only the default suppression-file presence fact
and `_ApiCompatInputsVersion=2`. Do not add configured package/targeting-pack
resolution or reference-directory inventories to `_CaptureApiCompatInputs`.
[`FormatApiCompatInputs`](../Compatibility/ApiCompat/Microsoft.DotNet.ApiCompat.Task/FormatApiCompatInputs.cs)
selects file inputs during graph construction from existing package inventories,
prepared framework catalogs, current references and explicit `ApiCompatReferenceFile`
items. Loose external directories need an explicit file catalog; do not discover
ambient DLLs. Produced directories use `ApiCompatProducedReferenceFile` members
with required producer ownership, never stale acquired/loose files.
Preserve reference-group ordering and explicit-file precedence in the loader.

## Build diagnostics (NETSDK errors / warnings / info)

These are conventions that aren't visible from the code alone — getting them wrong
breaks localization or silently collides with another PR.

- **Diagnostics are raised from targets, never as literal text.** Use
  `<NETSdkError/>`, `<NETSdkWarning/>`, or `<NETSdkInformation/>` (registered in
  `Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.Common.targets`) and reference
  the message **by `ResourceName`** from the shared `Common/Resources/Strings.resx`:

  ```xml
  <NETSdkError Condition="'$(TargetFramework)' == ''"
               ResourceName="TargetFrameworkEmpty"
               FormatArguments="$([MSBuild]::Escape('$(SomeValue)'))" />
  ```

- **The NETSDK code lives in the value, not in metadata.** In `Strings.resx` the
  `<value>` begins with the code (`NETSDK1238: The property '{0}' ...`), and the
  `<comment>` carries the localization directives — `{StrBegins="NETSDK1238: "}` plus
  `{Locked="{0}"}` per placeholder (or `{Locked="--option"}` for literal flags).
- **Message entries are append-only.** Add new strings at the **end** of the resx with
  the **next available** NETSDK number, and update the trailing
  `<!-- The latest message added is <Name>. -->` guard comment — it exists so two PRs
  adding a message conflict in git instead of silently reusing a code. (The root
  instructions carry the one-line version of this rule.)
- **`.xlf` files are generated**, never hand-edited — regenerate via `/t:UpdateXlf`.

Diagnostics are covered by tests under `test/Microsoft.NET.Build.Tests/`.
