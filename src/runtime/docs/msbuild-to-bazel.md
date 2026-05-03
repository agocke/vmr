# MSBuild to Bazel Translation Guide

This document describes how common MSBuild (.csproj) patterns map to Bazel
BUILD files in the dotnet/runtime repo. See also [docs/bazel.md](bazel.md)
for overall Bazel build status and architecture.

## Quick Reference

| MSBuild (.csproj) | Bazel (BUILD.bazel) |
|--------------------|---------------------|
| `<Compile Include="path">` | `srcs = ["src/path"]` |
| `$(CoreLibSharedDir)path` | `"//src/libraries/System.Private.CoreLib:src/path"` |
| `$(CommonPath)path` | `"//src/libraries/Common:src/path"` |
| `<ProjectReference Include="...">` | `deps = ["//src/libraries:ref_AssemblyName"]` |
| `<DefineConstants>FOO</DefineConstants>` | `defines = ["FOO"]` |
| `<ProjectReference Condition="'$(Configuration)' == 'Debug'" ...>` | `deps = [...] + select({"//:opt_mode": [], "//conditions:default": [...]})` |

## Debug-Conditional Dependencies

MSBuild projects sometimes reference assemblies only in Debug configuration,
typically because the consuming code sits inside `#if DEBUG` blocks. In Bazel,
`rules_dotnet` defines `DEBUG` for both `fastbuild` and `dbg` compilation
modes, but **not** for `-c opt`. Use `select()` on `//:opt_mode` to match
MSBuild's `Condition="'$(Configuration)' == 'Debug'"`:

```python
netcoreapp_impl_assembly(
    name = "impl_System.Security.Cryptography",
    deps = [
        # ... always-needed deps ...
    ] + select({
        # Console.WriteLine in #if DEBUG (SafeX509Handles.Unix.cs)
        "//:opt_mode": [],
        "//conditions:default": [
            "//src/libraries/System.Console:ref_System.Console",
        ],
    }),
)
```

### How it works internally

The `//:opt_mode` config_setting in the root `BUILD.bazel` matches
`-c opt` (`--compilation_mode=opt`). In all other modes (`fastbuild`,
`dbg`), `rules_dotnet` defines `DEBUG` and the conditional deps are
included.

## Adding a NuGet Package

1. Add `nuget <name> <version>` to `paket.dependencies`
2. Run `paket install` to update `paket.lock`
3. Run `bash sync-paket.sh` to regenerate `paket/paket.main.bzl`
4. If you reference files from the package directly (not just as a `deps` entry),
   add the repo name to `use_repo()` in `MODULE.bazel` and add a constant to
   `defs.bzl` following the existing pattern
