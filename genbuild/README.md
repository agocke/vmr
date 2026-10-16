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

This builds the selected MSBuild, SDK-task, Roslyn, and SourceLink projects and
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

## Smoke checks

Install Ninja and build the app first. Then run from the VMR root, replacing
the host path with an absolute path to `dotnet` (`dotnet.exe` on Windows):

```sh
dotnet msbuild genbuild/tests/Smoke.proj -p:GenBuildTestHost=/absolute/path/to/dotnet -bl:{}
```

The checks cover CLI failures, paths with spaces, graph-only construction,
independent Ninja execution, explicit NuGet project-style classification, SDK
duplicate-item, executable-reference and filename-only WinMD validation, SDK
Restore, and direct use of the packaged compiler to compile and run Hello World.
Select a runtime-only host to check that the app does not require an installed
SDK.

## Current limitations

Hardened NuGet classification supports the `PackageReference` style when it is
explicitly selected or implied by `PackageReference` items. It never searches for
`packages.config`; other styles or inputs requiring discovery fail explicitly.
Ordinary Restore retains NuGet's existing classification and discovery.
The app uses this VMR's NuGet targets with the pinned NuGet managed dependencies.

Full SDK Build graph support is unfinished: preparation, resource, compiler, copy,
and cleanup operations still need adaptations, including RAR and Roslyn's `Csc`.
SDK duplicate-item and executable-reference validation run as zero-I/O
construction tasks. Hardened SDK builds select a separate filename-only
`ValidateNoWinMDReferences` task, reporting `NETSDK1130` without inspecting
assemblies. Ordinary builds retain `CheckForUnsupportedWinMDReferences` and
its refined diagnostics.
Explicit fail-fast `ContinueOnError` values are supported. Continuing after
errors and active `OnError` handlers remain unsupported. In hardened mode,
Common's `CoreBuild` does not run failure handlers or record partial writes for
later Clean. Nonempty post-build events using `Always` or `OnOutputUpdated`
are rejected; use `OnBuildSuccess` or no event. Ordinary MSBuild retains its
failure handlers. Unsupported graph construction fails explicitly; there is
no fallback to ordinary Build.
The action host prepares declared output-file parents. Common directory
preparation is accepted only when every requested directory contains a
declared file output; unrelated empty-directory effects remain unsupported.
Leaf-project `MSBuild` calls with provably empty project lists are accepted as
construction-time no-ops with normal parameter and output validation.
Nonempty or unavailable project lists still require unsupported cross-project
expansion.
The compiler smoke check invokes the compiler directly, not through a `Csc`
graph action. Workload resolution is disabled in this prototype.

For adapting targets and tasks, see the
[conversion cookbook](../src/msbuild/documentation/specs/proposed/hardened-static-graph-migration.md).
See the [prototype roadmap](../src/msbuild/documentation/specs/proposed/hardened-static-graph-implementation.md)
for the architecture and remaining work.
