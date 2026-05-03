#!/usr/bin/env bash

# --- begin runfiles.bash initialization v3 ---
set -uo pipefail; set +e; f=bazel_tools/tools/bash/runfiles/runfiles.bash
source "${RUNFILES_DIR:-/dev/null}/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "${RUNFILES_MANIFEST_FILE:-/dev/null}" | cut -f2- -d' ')" 2>/dev/null || \
  source "$0.runfiles/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.exe.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  { echo>&2 "ERROR: cannot find $f"; exit 1; }; f=; set -e
# --- end runfiles.bash initialization v3 ---

DOTNET="$(rlocation TEMPLATED_dotnet)"
XUNIT_CONSOLE="$(rlocation TEMPLATED_xunit_console)"
ENTRY_DLL="$(rlocation TEMPLATED_entry_dll)"

# Set DOTNET_ROOT so the host can find shared frameworks
DOTNET_DIR="$(dirname "$(readlink -f "$DOTNET")")"
export DOTNET_ROOT="$DOTNET_DIR"

# Run from the directory containing the test DLL so deps can be found
RESOLVED_DIR="$(dirname "$(readlink -f "$ENTRY_DLL")")"
cd "$RESOLVED_DIR"

# Constrain the OS thread stack to ~1.5 MB to match Windows defaults.
# Roslyn tests assume a Windows-sized stack (~1 MB reserve); on Linux/macOS the
# OS default is 8 MB, which lets the parser recurse deep enough to defeat the
# StackGuard tests (TooDeepObjectInitializer*) when Roslyn is built optimized.
ulimit -s 1536 2>/dev/null || true

"$DOTNET" exec "$XUNIT_CONSOLE" "$ENTRY_DLL" -nologo "$@"
