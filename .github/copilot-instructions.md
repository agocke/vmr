# Copilot Instructions for the VMR

## Bazel Branch

When working on the `bazel` branch, read `docs/bazel-workflows.md` first.
It covers building, syncing runtime changes, include path architecture,
and known pitfalls (header guard conflicts, darc sync troubleshooting).

Key points:
- The runtime is wired as Bazel module `dotnet_runtime` via `local_path_override` in `MODULE.bazel`
- Do NOT make manual edits to `src/runtime/` — commit to the runtime repo and sync via `darc vmr update`
- Use `includes` attributes (not `-Isrc/...` copts) for include paths in BUILD files
- The runtime fork is at `agocke/runtime` (branch `bazel`), not `dotnet/runtime`
