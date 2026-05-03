#!/usr/bin/env bash
# build-bazel-runtime.sh — Build the .NET runtime using Bazel and compare
# against the CMake+MSBuild reference build.
#
# Usage (mirrors build.sh conventions):
#   ./build-bazel-runtime.sh                              # Build everything, debug
#   ./build-bazel-runtime.sh -rc release                  # Release CLR, debug libs
#   ./build-bazel-runtime.sh -rc checked -lc release      # Checked CLR, release libs
#   ./build-bazel-runtime.sh --native-only                # Only rebuild native code with Bazel
#   ./build-bazel-runtime.sh --managed-only               # Only rebuild managed code with MSBuild
#   ./build-bazel-runtime.sh --smoke-test                 # Run smoke test after build
#   ./build-bazel-runtime.sh --compare                    # Build both + compare archives
#   ./build-bazel-runtime.sh --skip-msbuild-build         # Compare using existing MSBuild artifacts

set -euo pipefail

scriptroot="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ----- Defaults -----
build_native=true
build_managed=true
config=""
clr_config=""
libs_config=""
run_smoke_test=false
run_compare=false
skip_msbuild_build=false
bazel_config_args=()

# ----- Parse arguments -----
while [[ $# -gt 0 ]]; do
    opt="${1,,}" # lowercase for case-insensitive matching
    case "$opt" in
        --native-only)
            build_managed=false
            shift
            ;;
        --managed-only)
            build_native=false
            shift
            ;;
        # Match build.sh flags: -rc / --runtimeconfiguration
        -rc|--runtimeconfiguration)
            clr_config="${2,,}"
            shift 2
            ;;
        # Match build.sh flags: -lc / --librariesconfiguration
        -lc|--librariesconfiguration)
            libs_config="${2,,}"
            shift 2
            ;;
        # Match build.sh flags: -c / --configuration (sets both)
        -c|--configuration)
            config="${2,,}"
            shift 2
            ;;
        --smoke-test)
            run_smoke_test=true
            shift
            ;;
        --compare)
            run_compare=true
            shift
            ;;
        --skip-msbuild-build)
            skip_msbuild_build=true
            shift
            ;;
        -h|--help)
            head -10 "${BASH_SOURCE[0]}" | tail -9
            exit 0
            ;;
        *)
            echo "Unknown argument: $1"
            exit 1
            ;;
    esac
done

# ----- Derived variables -----
# Read product version from eng/Versions.props
major_version=$(grep '<MajorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MajorVersion>\(.*\)<\/MajorVersion>.*/\1/')
minor_version=$(grep '<MinorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MinorVersion>\(.*\)<\/MinorVersion>.*/\1/')
patch_version=$(grep '<PatchVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<PatchVersion>\(.*\)<\/PatchVersion>.*/\1/')
product_version="${major_version}.${minor_version}.${patch_version}"
tfm="net${major_version}.${minor_version}"

rid="linux-x64"

# Resolve per-component configs.
# -c sets both; -rc / -lc override individually.
if [[ -n "$config" ]]; then
    clr_config="${clr_config:-$config}"
    libs_config="${libs_config:-$config}"
fi
clr_config="${clr_config:-checked}"
libs_config="${libs_config:-debug}"

# Validate config values
case "$clr_config" in
    debug|checked|release) ;;
    *) echo "Invalid -rc: $clr_config (must be debug, checked, or release)"; exit 1 ;;
esac
case "$libs_config" in
    debug|release) ;;
    *) echo "Invalid -lc: $libs_config (must be debug or release)"; exit 1 ;;
esac

# Map to MSBuild config names (for managed build)
case "$clr_config" in
    debug)   msbuild_rc="Debug" ;;
    checked) msbuild_rc="Checked" ;;
    release) msbuild_rc="Release" ;;
esac
case "$libs_config" in
    debug)   msbuild_lc="Debug" ;;
    release) msbuild_lc="Release" ;;
esac

# Map to Bazel --config flags
if [[ "$clr_config" == "release" && "$libs_config" == "release" ]]; then
    bazel_config_args=(--config=release)
else
    bazel_config_args=()
    [[ "$clr_config" != "checked" ]] && bazel_config_args+=(--config=clr_${clr_config})
    [[ "$libs_config" != "debug" ]] && bazel_config_args+=(--config=libs_${libs_config})
fi

# ----- Output layout paths -----
output_dir="$scriptroot/artifacts/bazel-dotnet"
fxr_dir="$output_dir/host/fxr/$product_version"
framework_dir="$output_dir/shared/Microsoft.NETCore.App/$product_version"

# ----- MSBuild artifact source paths -----
corelib_dll="$scriptroot/artifacts/bin/coreclr/linux.x64.${msbuild_rc}/IL/System.Private.CoreLib.dll"
managed_dlls_dir="$scriptroot/artifacts/bin/microsoft.netcore.app.runtime.${rid}/${msbuild_rc}/runtimes/${rid}/lib/${tfm}"

# ----- Bazel output paths -----
bazel_bin="$scriptroot/bazel-bin"

# Bazel target for native build — produces a pre-assembled runtime layout
# at bazel-bin/runtime_native/ matching the standard .NET hosting structure.
bazel_targets=(
    "//:runtime_native"
)

# Bazel target for the full runtime archive (tar.gz)
bazel_archive_target="//:runtime_archive"

# ----- Helper functions -----
log() { echo -e "\033[1;36m==>\033[0m $*"; }
log_error() { echo -e "\033[1;31m==> ERROR:\033[0m $*" >&2; }
log_success() { echo -e "\033[1;32m==>\033[0m $*"; }

# ----- Step 1: MSBuild managed build -----
build_managed_libs() {
    if [[ "$build_managed" != "true" ]]; then
        return
    fi

    log "Building managed libraries with MSBuild (clr.corelib+libs, ${msbuild_rc})..."
    log "This may take 15-30 minutes on first run."

    "$scriptroot/build.sh" clr.corelib+libs -rc "$msbuild_rc" -lc "$msbuild_lc"

    if [[ ! -f "$corelib_dll" ]]; then
        log_error "System.Private.CoreLib.dll not found at: $corelib_dll"
        log_error "MSBuild build may have failed."
        exit 1
    fi

    log_success "Managed build complete."
}

# ----- Step 2: Bazel native build -----
build_native_libs() {
    if [[ "$build_native" != "true" ]]; then
        return
    fi

    log "Building native components with Bazel..."

    local targets=("${bazel_targets[@]}")
    if [[ "$run_compare" == "true" ]]; then
        targets+=("$bazel_archive_target")
    fi

    bazel --nohome_rc build "${bazel_config_args[@]}" "${targets[@]}"

    log_success "Native build complete."
}

# ----- Step 3: Assemble runtime layout -----
assemble_runtime() {
    log "Assembling runtime layout at: $output_dir"

    # Clean and create directory structure
    rm -rf "$output_dir"
    mkdir -p "$output_dir" "$fxr_dir" "$framework_dir"

    # Copy native runtime layout from Bazel
    if [[ "$build_native" == "true" ]] || [[ -d "$bazel_bin/runtime_native" ]]; then
        log "Copying native runtime layout from Bazel..."
        local layout_dir="$bazel_bin/runtime_native"
        if [[ -d "$layout_dir" ]]; then
            cp -r "$layout_dir/." "$output_dir/"
            chmod +x "$output_dir/dotnet"
        else
            log_error "Runtime layout not found at: $layout_dir"
            log_error "Run: bazel build //:runtime_native"
            exit 1
        fi
    fi

    # Copy managed DLLs from MSBuild
    if [[ "$build_managed" == "true" ]] || [[ -d "$managed_dlls_dir" ]]; then
        if [[ -d "$managed_dlls_dir" ]]; then
            log "Copying managed DLLs from MSBuild..."
            cp "$managed_dlls_dir"/*.dll "$framework_dir/"

            local dll_count
            dll_count=$(find "$framework_dir" -name "*.dll" | wc -l)
            log "Copied $dll_count managed DLLs."
        else
            log_error "Managed DLLs directory not found: $managed_dlls_dir"
            log_error "Run without --native-only first, or run: ./build.sh clr.corelib+libs -rc $msbuild_rc -lc $msbuild_lc"
            exit 1
        fi

        # Copy System.Private.CoreLib.dll (from CoreCLR IL build)
        if [[ -f "$corelib_dll" ]]; then
            cp "$corelib_dll" "$framework_dir/"
        else
            log_error "System.Private.CoreLib.dll not found at: $corelib_dll"
            exit 1
        fi
    fi

    copy_deps_json

    log_success "Runtime layout assembled at: $output_dir"
    log "  dotnet:          $output_dir/dotnet"
    log "  hostfxr:         $fxr_dir/libhostfxr.so"
    log "  shared framework: $framework_dir/"
}

# ----- Copy deps.json from MSBuild -----
copy_deps_json() {
    local deps_json="$framework_dir/Microsoft.NETCore.App.deps.json"
    local msbuild_deps="$scriptroot/artifacts/bin/microsoft.netcore.app.runtime.${rid}/${msbuild_lc}/runtimes/${rid}/lib/${tfm}/Microsoft.NETCore.App.deps.json"
    local testhost_deps="$scriptroot/artifacts/bin/testhost/${tfm}-linux-${msbuild_lc}-x64/shared/Microsoft.NETCore.App/${product_version}/Microsoft.NETCore.App.deps.json"

    if [[ -f "$msbuild_deps" ]]; then
        cp "$msbuild_deps" "$deps_json"
    elif [[ -f "$testhost_deps" ]]; then
        cp "$testhost_deps" "$deps_json"
    else
        log_error "Microsoft.NETCore.App.deps.json not found at:"
        log_error "  $msbuild_deps"
        log_error "  $testhost_deps"
        log_error "Run the managed build first: ./build.sh clr.corelib+libs -rc $msbuild_rc -lc $msbuild_lc"
        exit 1
    fi
}

# ----- Smoke test -----
smoke_test() {
    if [[ "$run_smoke_test" != "true" ]]; then
        return
    fi

    log "Running smoke test..."

    local dotnet_exe="$output_dir/dotnet"
    if [[ ! -x "$dotnet_exe" ]]; then
        log_error "dotnet executable not found or not executable: $dotnet_exe"
        exit 1
    fi

    # Set DOTNET_ROOT so the host can find the shared framework
    export DOTNET_ROOT="$output_dir"

    # Basic host check
    log "Testing: dotnet --info"
    "$dotnet_exe" --info || true

    # Create and run a minimal hello-world
    local test_dir
    test_dir=$(mktemp -d)
    trap "rm -rf '$test_dir'" EXIT

    cat > "$test_dir/hello.cs" << 'HELLO_EOF'
System.Console.WriteLine("Hello from Bazel-built .NET runtime!");
HELLO_EOF

    cat > "$test_dir/hello.csproj" << CSPROJ_EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>${tfm}</TargetFramework>
  </PropertyGroup>
</Project>
CSPROJ_EOF

    cat > "$test_dir/hello.runtimeconfig.json" << RTCFG_EOF
{
  "runtimeOptions": {
    "tfm": "${tfm}",
    "framework": {
      "name": "Microsoft.NETCore.App",
      "version": "${product_version}"
    },
    "rollForward": "LatestMajor"
  }
}
RTCFG_EOF

    # Try to compile with the repo's SDK and run with our runtime
    if command -v "$scriptroot/.dotnet/dotnet" &>/dev/null; then
        log "Compiling hello world..."
        "$scriptroot/.dotnet/dotnet" build "$test_dir/hello.csproj" -o "$test_dir/out" --nologo -v quiet 2>/dev/null || true

        if [[ -f "$test_dir/out/hello.dll" ]]; then
            log "Running hello world with Bazel-built runtime..."
            cp "$test_dir/hello.runtimeconfig.json" "$test_dir/out/"
            "$dotnet_exe" "$test_dir/out/hello.dll" && log_success "Smoke test passed!" || log_error "Smoke test failed."
        else
            log "Could not compile hello world (SDK may not be available). Skipping app test."
        fi
    else
        log "SDK not available at .dotnet/dotnet. Skipping hello world test."
    fi
}

# ----- Compare: build MSBuild packs + Bazel archive, then diff -----
compare_archives() {
    if [[ "$run_compare" != "true" ]]; then
        return
    fi

    log "Comparing Bazel and MSBuild runtime archives..."

    # Build MSBuild reference archive if needed
    local msbuild_tarball_pattern="$scriptroot/artifacts/packages/*/Shipping/dotnet-runtime-*-linux-x64.tar.gz"
    local msbuild_tarball=""

    if [[ "$skip_msbuild_build" != "true" ]]; then
        log "Building MSBuild reference (clr+libs+host+packs, -rc $msbuild_rc -lc $msbuild_lc)..."
        "$scriptroot/build.sh" clr+libs+host -rc "$msbuild_rc" -lc "$msbuild_lc"
        "$scriptroot/build.sh" packs -rc "$msbuild_rc" -lc "$msbuild_lc"
    fi

    # Find the MSBuild tarball
    local candidates=($msbuild_tarball_pattern)
    if [[ ${#candidates[@]} -eq 0 || ! -f "${candidates[0]}" ]]; then
        log_error "MSBuild runtime tarball not found matching: $msbuild_tarball_pattern"
        log_error "Run without --skip-msbuild-build, or build manually:"
        log_error "  ./build.sh clr+libs+host+packs -rc $msbuild_rc -lc $msbuild_lc"
        return 1
    fi
    msbuild_tarball="${candidates[0]}"

    # Build the Bazel archive (if not already built by build_native_libs)
    log "Building Bazel runtime archive..."
    bazel --nohome_rc build "${bazel_config_args[@]}" "$bazel_archive_target"

    # Find the Bazel tarball
    local bazel_tarball
    bazel_tarball=$(ls "$bazel_bin"/dotnet-runtime-*-linux-x64.tar.gz 2>/dev/null | head -1)
    if [[ -z "$bazel_tarball" || ! -f "$bazel_tarball" ]]; then
        log_error "Bazel runtime tarball not found in $bazel_bin"
        return 1
    fi

    log "MSBuild: $msbuild_tarball"
    log "Bazel:   $bazel_tarball"
    echo ""

    # Run comparison
    "$scriptroot/compare-runtime-packs.sh" "$msbuild_tarball" "$bazel_tarball"
}

# ----- Main -----
main() {
    log "Bazel + MSBuild hybrid runtime build"
    log "  Product version: $product_version"
    log "  CLR config:      $clr_config ($msbuild_rc)"
    log "  Libs config:     $libs_config ($msbuild_lc)"
    log "  Build native:    $build_native"
    log "  Build managed:   $build_managed"
    log "  Output:          $output_dir"
    echo ""

    build_managed_libs
    build_native_libs
    assemble_runtime
    smoke_test
    compare_archives

    echo ""
    log_success "Done! Runtime is at: $output_dir"
    log "Run an app:  DOTNET_ROOT=$output_dir $output_dir/dotnet <app.dll>"
}

main
