#!/usr/bin/env bash
# compare-bazel.sh — Compare Bazel and CMake/MSBuild build inputs
#
# Verifies that the Bazel build compiles the same source files with the same
# compiler options as the CMake/MSBuild build.  Compares both native C/C++
# (via compile_commands.json vs bazel aquery) and managed C# (via .binlog vs
# bazel aquery).
#
# Usage:
#   ./compare-bazel.sh                    # Debug config (default)
#   ./compare-bazel.sh --config release   # Release config
#   ./compare-bazel.sh --config both      # Both configs
#   ./compare-bazel.sh --skip-build       # Use existing build artifacts
#   ./compare-bazel.sh --verbose          # Show all differences
#   ./compare-bazel.sh --json-output report.json

set -euo pipefail

scriptroot="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ----- Defaults -----
config="release"
skip_build=false
verbose=false
json_output=""
msbuild_json=""

# ----- Parse arguments -----
while [[ $# -gt 0 ]]; do
    case "$1" in
        --config)
            config="${2,,}"
            shift 2
            ;;
        --skip-build)
            skip_build=true
            shift
            ;;
        --verbose|-v)
            verbose=true
            shift
            ;;
        --json-output)
            json_output="$2"
            shift 2
            ;;
        --msbuild-json)
            msbuild_json="$2"
            shift 2
            ;;
        -h|--help)
            head -16 "${BASH_SOURCE[0]}" | tail -15
            exit 0
            ;;
        *)
            echo "Unknown argument: $1"
            exit 1
            ;;
    esac
done

# ----- Color helpers -----
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[1;36m'
NC='\033[0m'

log()         { echo -e "${CYAN}==>${NC} $*"; }
log_success() { echo -e "${GREEN}==>${NC} $*"; }
log_error()   { echo -e "${RED}==>${NC} $*" >&2; }

# ----- Ensure dotnet is on PATH -----
export PATH="$scriptroot/.dotnet:$PATH"

# ----- Determine configs to compare -----
configs=()
case "$config" in
    debug)   configs=(debug) ;;
    release) configs=(release) ;;
    both)    configs=(debug release) ;;
    *)
        echo "Invalid config: $config (must be debug, release, or both)"
        exit 1
        ;;
esac

overall_exit=0

for cfg in "${configs[@]}"; do
    log "════════════════════════════════════════════════════"
    log "  Configuration: $cfg"
    log "════════════════════════════════════════════════════"

    # ----- Map config to build system flags -----
    # Always include --ci for MSBuild and --config=ci for Bazel so the
    # comparison reflects CI-mode deterministic source paths.
    if [[ "$cfg" == "debug" ]]; then
        msbuild_rc="Debug"
        bazel_aquery_args=(--config=clr_debug --config=ci)
    else
        msbuild_rc="Release"
        bazel_aquery_args=(--config=release --config=ci)
    fi

    # ----- CMake compile_commands.json paths -----
    cmake_coreclr_cc="$scriptroot/artifacts/obj/coreclr/linux.x64.${msbuild_rc}/compile_commands.json"
    cmake_corehost_cc="$scriptroot/artifacts/obj/linux-x64.${msbuild_rc}/compile_commands.json"
    cmake_nativelibs_cc="$scriptroot/artifacts/obj/native/net10.0-linux-${msbuild_rc}-x64/compile_commands.json"

    # ----- MSBuild binlog path -----
    binlog_dir="$scriptroot/artifacts/log/${msbuild_rc}"

    # ----- Bazel aquery output paths -----
    aquery_dir="$scriptroot/artifacts/obj/bazel-aquery"
    mkdir -p "$aquery_dir"
    bazel_native_aquery="$aquery_dir/${cfg}-native.json"
    bazel_managed_aquery="$aquery_dir/${cfg}-managed.json"

    # ----- Step 1: Build with CMake/MSBuild -----
    if [[ -z "$msbuild_json" && "$skip_build" != "true" ]]; then
        log "Building with CMake/MSBuild (./build.sh clr+libs+libs.tests --ci -c $cfg -rc $cfg -lc $cfg -bl --rebuild)..."
        "$scriptroot/build.sh" clr+libs+libs.tests --ci -c "$cfg" -rc "$cfg" -lc "$cfg" -bl --rebuild
    fi

    # ----- Step 2: Build with Bazel + extract aquery -----
    # Build first so that generated source files (AssemblyInfo.cs, System.SR.cs)
    # are materialized on disk with the correct CI-mode content.  The aquery
    # alone only performs analysis and does not write generated files.
    if [[ "$skip_build" != "true" ]]; then
        log "Building with Bazel (bazel build ${bazel_aquery_args[*]} //...)..."
        bazel --nohome_rc build "${bazel_aquery_args[@]}" //...
    fi

    log "Extracting Bazel aquery (native)..."
    bazel --nohome_rc aquery \
        "${bazel_aquery_args[@]}" \
        --output=jsonproto \
        'mnemonic("CppCompile", //...)' \
        > "$bazel_native_aquery" 2>/dev/null

    log "Extracting Bazel aquery (managed)..."
    bazel --nohome_rc aquery \
        "${bazel_aquery_args[@]}" \
        --output=jsonproto \
        'mnemonic("CSharpCompile", //...)' \
        > "$bazel_managed_aquery" 2>/dev/null

    # ----- Step 3: Find binlog files -----
    binlog_args=()
    if [[ -z "$msbuild_json" && -d "$binlog_dir" ]]; then
        while IFS= read -r -d '' f; do
            binlog_args+=(--binlog "$f")
        done < <(find "$binlog_dir" -name "*.binlog" -print0)
    fi

    # ----- Step 4: Build the analysis tool -----
    log "Building analysis tool..."
    "$scriptroot/dotnet.sh" build "$scriptroot/src/tools/bazel/BuildEquivalenceCheck/BuildEquivalenceCheck.csproj" \
        --nologo -v quiet 2>&1

    # ----- Step 5: Run comparison -----
    log "Running equivalence check..."
    tool_args=(
        --repo-root "$scriptroot"
        --bazel-native-aquery "$bazel_native_aquery"
        --bazel-managed-aquery "$bazel_managed_aquery"
        --managed-manifest "$scriptroot/src/tools/bazel/BuildEquivalenceCheck/managed-assembly-manifest.txt"
    )

    # Add compile_commands.json files that exist
    for cc in "$cmake_coreclr_cc" "$cmake_corehost_cc" "$cmake_nativelibs_cc"; do
        if [[ -f "$cc" ]]; then
            tool_args+=(--cmake-compile-commands "$cc")
        else
            log "  (skipping missing compile_commands: $cc)"
        fi
    done

    # Use pre-extracted MSBuild JSON if provided, otherwise use binlog files
    if [[ -n "$msbuild_json" ]]; then
        tool_args+=(--msbuild-json "$msbuild_json")
    else
        tool_args+=("${binlog_args[@]}")
    fi

    if [[ "$verbose" == "true" ]]; then
        tool_args+=(--verbose)
    fi

    if [[ -n "$json_output" ]]; then
        local_json="${json_output%.json}-${cfg}.json"
        tool_args+=(--json-output "$local_json")
    fi

    "$scriptroot/dotnet.sh" run --project "$scriptroot/src/tools/bazel/BuildEquivalenceCheck/BuildEquivalenceCheck.csproj" \
        --no-build -- "${tool_args[@]}" || overall_exit=1

    echo ""
done

if [[ "$overall_exit" -eq 0 ]]; then
    log_success "All equivalence checks passed."
else
    log_error "Some equivalence checks found differences."
fi

exit "$overall_exit"
