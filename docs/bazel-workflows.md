# Bazel VMR Workflows

This document covers day-to-day workflows for working with Bazel in the VMR
(Virtual Monolithic Repository). For the overall design and integration plan,
see the runtime repo's `docs/bazel/` directory.

## Repository Layout

The VMR composes source repos under `src/`. The runtime lives at `src/runtime/`
and is wired as a Bazel module named `dotnet_runtime` via `local_path_override`
in the root `MODULE.bazel`.

Key files:
- `MODULE.bazel` — VMR root module; declares `dotnet_runtime` dependency
- `BUILD.bazel` — Root build file with `clr_config`/`libs_config` string flags
- `.bazelrc` — Imports `src/runtime/.bazelrc` for shared build configuration

## Building

```bash
# Build everything in the runtime from the VMR root
bazel build @dotnet_runtime//... --config=clr_checked

# Build a specific target
bazel build @dotnet_runtime//src/native/libs/System.Native:System.Native

# Build from the runtime directory (standalone mode)
cd src/runtime
bazel build //... --config=clr_checked
```

## Syncing Runtime Changes into the VMR

When new commits land on the runtime's `bazel` branch (on `agocke/rbz`),
use `darc vmr update` to sync them into the VMR.

### Prerequisites

- The `darc` CLI must be installed (via `dotnet tool install -g Microsoft.DotNet.Darc`)
- `src/source-mappings.json` and `src/source-manifest.json` must have their
  runtime `remoteUri`/`defaultRemote` pointing at `https://github.com/agocke/rbz`
  (not `dotnet/runtime`)

### Using a GitHub URL as the remote

```bash
# Sync to the tip of the bazel branch
darc vmr update runtime:bazel \
  --additional-remotes runtime:https://github.com/agocke/rbz \
  --vmr /path/to/vmr

# Sync to a specific commit SHA
darc vmr update runtime:<sha> \
  --additional-remotes runtime:https://github.com/agocke/rbz \
  --vmr /path/to/vmr
```

### Using a local clone as the remote

If you have a local clone of the runtime with the commits already fetched,
you can use it directly (faster, avoids re-cloning):

```bash
# Fetch latest first
cd /path/to/runtime-clone
git fetch origin bazel

# Sync into VMR
darc vmr update runtime:bazel \
  --additional-remotes runtime:/path/to/runtime-clone \
  --vmr /path/to/vmr
```

### Troubleshooting sync issues

**"Sync f8a3a45 → f8a3a45" (no-op sync):**
Darc resolves the branch name against the remote specified in
`source-mappings.json` (`defaultRemote`) and `source-manifest.json`
(`remoteUri`). If these point at `dotnet/runtime` instead of
`agocke/rbz`, the `bazel` branch won't exist there and darc falls
back to the current SHA. Fix by updating both files to point at the fork.

**"patch does not apply" errors:**
This happens when `src/runtime/` in the VMR has manual edits that conflict
with changes in the incoming patch. The cleanest fix is to reset the VMR
branch to the last clean darc sync commit and re-sync. Don't make manual
edits to `src/runtime/` — instead, commit changes to the runtime repo
and sync them in via darc.

## Include Path Architecture

The runtime's Bazel build uses the `includes` attribute on `cc_library`
targets instead of hardcoded `-Isrc/...` copts. This is required for the
runtime to work both standalone and as an external Bazel module (where
exec-root-relative paths differ).

Shared include paths are exposed via header-only `cc_library` targets:
- `//src/coreclr:coreclr_inc` — CoreCLR include directories
- `//src/coreclr/nativeaot:nativeaot_inc` — NativeAOT includes
- `//src/native:native_inc` — Native library includes

### Known pitfall: header guard conflicts

`src/coreclr/gc/env/volatile.h` and `src/coreclr/inc/volatile.h` share the
same `_VOLATILE_H_` header guard but have different contents. Do NOT add
`gc/env` to include paths for non-GC targets — the VM has wrapper headers
(e.g., `vm/gcenv.interlocked.h`) that redirect via relative paths.

Similarly, `src/native/external/llvm-libunwind/src/config.h` will shadow
NativeAOT's generated `config.h` if `llvm-libunwind/src/` is on the include
path. Only expose `llvm-libunwind/include/` via the `includes` attribute.
