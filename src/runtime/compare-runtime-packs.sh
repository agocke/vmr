#!/usr/bin/env bash
# compare-runtime-packs.sh — Verify that a Bazel-built runtime archive is
# bit-for-bit identical to the CMake+MSBuild-built runtime archive.
#
# Usage:
#   ./compare-runtime-packs.sh <msbuild-tarball> <bazel-tarball>
#   ./compare-runtime-packs.sh                          # auto-detect from artifacts/
#
# Exit codes:
#   0  — archives are identical
#   1  — archives differ (details printed to stderr)
#   2  — usage error / missing prerequisites

set -euo pipefail

scriptroot="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ----- Color helpers -----
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[1;36m'
YELLOW='\033[0;33m'
BOLD='\033[1m'
NC='\033[0m'

log()         { echo -e "${CYAN}==>${NC} $*"; }
log_success() { echo -e "${GREEN}==>${NC} $*"; }
log_warn()    { echo -e "${YELLOW}==>${NC} $*"; }
log_error()   { echo -e "${RED}==>${NC} $*" >&2; }
log_header()  { echo -e "\n${BOLD}── $* ──${NC}"; }

# ----- Read version info -----
major_version=$(grep '<MajorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MajorVersion>\(.*\)<\/MajorVersion>.*/\1/')
minor_version=$(grep '<MinorVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<MinorVersion>\(.*\)<\/MinorVersion>.*/\1/')
patch_version=$(grep '<PatchVersion>' "$scriptroot/eng/Versions.props" | sed 's/.*<PatchVersion>\(.*\)<\/PatchVersion>.*/\1/')
product_version="${major_version}.${minor_version}.${patch_version}"

# ----- Parse arguments or auto-detect -----
msbuild_tarball=""
bazel_tarball=""

if [[ $# -ge 2 ]]; then
    msbuild_tarball="$1"
    bazel_tarball="$2"
elif [[ $# -eq 0 ]]; then
    # Auto-detect: look for the packs tarball in artifacts/
    pattern="artifacts/packages/*/Shipping/dotnet-runtime-*-linux-x64.tar.gz"
    candidates=($scriptroot/$pattern)
    if [[ ${#candidates[@]} -eq 0 ]]; then
        log_error "No MSBuild runtime tarball found matching: $pattern"
        log_error "Run:  ./build.sh clr+libs+host+packs -rc Release -lc Release"
        exit 2
    fi
    msbuild_tarball="${candidates[0]}"
    log "Auto-detected MSBuild tarball: $msbuild_tarball"

    # Bazel tarball doesn't exist yet — that's the whole point of this script.
    # For now, allow it to be missing and report what needs to be built.
    bazel_tarball="${BAZEL_RUNTIME_TARBALL:-}"
    if [[ -z "$bazel_tarball" ]]; then
        log_error "Bazel runtime tarball not specified."
        log_error "Set BAZEL_RUNTIME_TARBALL or pass as second argument."
        log_error "Usage: $0 <msbuild-tarball> <bazel-tarball>"
        exit 2
    fi
else
    echo "Usage: $0 [<msbuild-tarball> <bazel-tarball>]"
    echo "       $0                        # auto-detect MSBuild, set BAZEL_RUNTIME_TARBALL"
    exit 2
fi

for f in "$msbuild_tarball" "$bazel_tarball"; do
    if [[ ! -f "$f" ]]; then
        log_error "File not found: $f"
        exit 2
    fi
done

# ----- Set up temp directories -----
tmpdir=$(mktemp -d)
trap "rm -rf '$tmpdir'" EXIT

msbuild_dir="$tmpdir/msbuild"
bazel_dir="$tmpdir/bazel"
mkdir -p "$msbuild_dir" "$bazel_dir"

# ----- Extract both tarballs -----
log "Extracting MSBuild tarball..."
tar xzf "$msbuild_tarball" -C "$msbuild_dir"

log "Extracting Bazel tarball..."
tar xzf "$bazel_tarball" -C "$bazel_dir"

# ----- Build sorted file lists -----
log_header "Phase 1: File inventory"

(cd "$msbuild_dir" && find . -not -type d | sort) > "$tmpdir/msbuild-files.txt"
(cd "$bazel_dir"   && find . -not -type d | sort) > "$tmpdir/bazel-files.txt"

msbuild_count=$(wc -l < "$tmpdir/msbuild-files.txt")
bazel_count=$(wc -l < "$tmpdir/bazel-files.txt")
log "MSBuild archive: $msbuild_count files"
log "Bazel archive:   $bazel_count files"

# ----- Compare file lists -----
failures=0
warnings=0

diff_result=$(diff "$tmpdir/msbuild-files.txt" "$tmpdir/bazel-files.txt" || true)
if [[ -n "$diff_result" ]]; then
    log_error "File lists differ!"

    only_msbuild=$(diff "$tmpdir/msbuild-files.txt" "$tmpdir/bazel-files.txt" | grep '^< ' | sed 's/^< //' || true)
    only_bazel=$(diff "$tmpdir/msbuild-files.txt" "$tmpdir/bazel-files.txt" | grep '^> ' | sed 's/^> //' || true)

    if [[ -n "$only_msbuild" ]]; then
        log_error "Files only in MSBuild archive:"
        echo "$only_msbuild" | while read -r f; do echo "  - $f"; done >&2
    fi

    if [[ -n "$only_bazel" ]]; then
        log_error "Files only in Bazel archive:"
        echo "$only_bazel" | while read -r f; do echo "  + $f"; done >&2
    fi
    failures=$((failures + 1))
else
    log_success "File lists match ($msbuild_count files)"
fi

# ----- Compare file contents (bit-for-bit) -----
log_header "Phase 2: Bit-for-bit content comparison"

# Only compare files present in both archives
comm -12 "$tmpdir/msbuild-files.txt" "$tmpdir/bazel-files.txt" > "$tmpdir/common-files.txt"
common_count=$(wc -l < "$tmpdir/common-files.txt")

identical=0
different=0
diff_files=()

while IFS= read -r relpath; do
    msbuild_file="$msbuild_dir/$relpath"
    bazel_file="$bazel_dir/$relpath"

    if cmp -s "$msbuild_file" "$bazel_file"; then
        identical=$((identical + 1))
    else
        different=$((different + 1))
        diff_files+=("$relpath")
    fi
done < "$tmpdir/common-files.txt"

if [[ $different -eq 0 ]]; then
    log_success "All $identical common files are bit-for-bit identical"
else
    log_error "$different of $common_count files differ:"

    for relpath in "${diff_files[@]}"; do
        msbuild_file="$msbuild_dir/$relpath"
        bazel_file="$bazel_dir/$relpath"

        msbuild_size=$(stat -c%s "$msbuild_file")
        bazel_size=$(stat -c%s "$bazel_file")
        size_info=""
        if [[ "$msbuild_size" != "$bazel_size" ]]; then
            size_info=" (MSBuild: ${msbuild_size}B, Bazel: ${bazel_size}B)"
        else
            size_info=" (same size: ${msbuild_size}B, content differs)"
        fi

        msbuild_sha=$(sha256sum "$msbuild_file" | cut -d' ' -f1)
        bazel_sha=$(sha256sum "$bazel_file" | cut -d' ' -f1)

        echo "  ✗ $relpath$size_info" >&2
        echo "      MSBuild: $msbuild_sha" >&2
        echo "      Bazel:   $bazel_sha" >&2
    done
    failures=$((failures + 1))
fi

# ----- Compare file permissions -----
log_header "Phase 3: File permissions"

perm_diffs=0
while IFS= read -r relpath; do
    msbuild_perm=$(stat -c%a "$msbuild_dir/$relpath")
    bazel_perm=$(stat -c%a "$bazel_dir/$relpath")

    if [[ "$msbuild_perm" != "$bazel_perm" ]]; then
        perm_diffs=$((perm_diffs + 1))
        echo "  ✗ $relpath: MSBuild=$msbuild_perm Bazel=$bazel_perm" >&2
    fi
done < "$tmpdir/common-files.txt"

if [[ $perm_diffs -eq 0 ]]; then
    log_success "All file permissions match"
else
    log_warn "$perm_diffs files have different permissions"
    warnings=$((warnings + 1))
fi

# ----- Summary -----
log_header "Summary"

echo ""
echo "  MSBuild tarball:  $msbuild_tarball"
echo "  Bazel tarball:    $bazel_tarball"
echo "  Total files:      MSBuild=$msbuild_count  Bazel=$bazel_count"
echo "  Common files:     $common_count"
echo "  Identical:        $identical"
echo "  Different:        $different"
echo "  Permission diffs: $perm_diffs"
echo ""

if [[ $failures -eq 0 && $warnings -eq 0 ]]; then
    log_success "PASS — archives are bit-for-bit identical"
    exit 0
elif [[ $failures -eq 0 ]]; then
    log_warn "PASS (with warnings) — content identical, minor metadata differences"
    exit 0
else
    log_error "FAIL — archives differ ($failures failure(s), $warnings warning(s))"
    exit 1
fi
