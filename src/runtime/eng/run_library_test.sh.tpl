#!/usr/bin/env bash

# --- begin runfiles.bash initialization v3 ---
# Copy-pasted from the Bazel Bash runfiles library v3.
set -uo pipefail; set +e; f=bazel_tools/tools/bash/runfiles/runfiles.bash
source "${RUNFILES_DIR:-/dev/null}/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "${RUNFILES_MANIFEST_FILE:-/dev/null}" | cut -f2- -d' ')" 2>/dev/null || \
  source "$0.runfiles/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.exe.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  { echo>&2 "ERROR: cannot find $f"; exit 1; }; f=; set -e
# --- end runfiles.bash initialization v3 ---

# Run the dotnet binary from the testhost directory. The testhost contains a
# shared framework built from Bazel-built (live) assemblies, matching how
# MSBuild's testhost uses live-built bits. Because dotnet resolves frameworks
# relative to its own location, using the testhost's copy ensures our
# DEBUG-built assemblies are loaded instead of the SDK's RELEASE versions.
TESTHOST=$(rlocation TEMPLATED_testhost)
XUNIT_CONSOLE="$(rlocation TEMPLATED_xunit_console)"
ENTRY_DLL="$(rlocation TEMPLATED_entry_dll)"
DEPSFILE="$(rlocation TEMPLATED_depsfile)"
RUNTIMECONFIG="$(rlocation TEMPLATED_runtimeconfig)"

RESOLVED_DIR="$(dirname "$(readlink -f "$XUNIT_CONSOLE")")"

if [ "TEMPLATED_writable_test_dir" = "true" ]; then
    # Copy all runtime files to a writable directory.  Bazel's linux-sandbox
    # bind-mounts build outputs read-only, which breaks tests that call
    # File.Move on files next to the assembly (e.g. PDB rename).
    WORK_DIR="${TEST_TMPDIR:-/tmp}/testdir"
    mkdir -p "$WORK_DIR"
    # Copy everything including subdirectories (test data), then make writable.
    cp -a "$RESOLVED_DIR"/. "$WORK_DIR/"
    chmod -R u+w "$WORK_DIR"
    cd "$WORK_DIR"
    XUNIT_CONSOLE="$WORK_DIR/$(basename "$XUNIT_CONSOLE")"
    ENTRY_DLL="$WORK_DIR/$(basename "$ENTRY_DLL")"
    DEPSFILE="$WORK_DIR/$(basename "$DEPSFILE")"
    RUNTIMECONFIG="$WORK_DIR/$(basename "$RUNTIMECONFIG")"
else
    # Run directly from the build output directory.
    cd "$RESOLVED_DIR"
fi

# On macOS, tests requiring keychain access fail in Bazel's test environment because
# test-setup.sh creates an isolated environment without access to the user's login
# session. These tests pass with 'bazel run' but fail with 'bazel test'.
# See docs/workflow/building/bazel/README.md for details.
"$TESTHOST/dotnet" exec --runtimeconfig "$RUNTIMECONFIG" --depsfile "$DEPSFILE" "$XUNIT_CONSOLE" "$ENTRY_DLL" -nologo -notrait "category=failing" -notrait "category=OuterLoop" -notrait "category=RequiresKeychain" "$@"
