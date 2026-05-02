#!/usr/bin/env bash
# detect-upstream-changes.sh
#
# Analyzes a merge diff between the bazel branch and upstream/release/10.0 to
# identify changes that may require Bazel BUILD file updates.
#
# Usage:
#   ./eng/bazel/detect-upstream-changes.sh <base-ref> <head-ref> [--report-file <path>]
#
# Arguments:
#   base-ref    The base commit (e.g., origin/bazel)
#   head-ref    The head commit (e.g., upstream/release/10.0 or HEAD after merge)
#
# Options:
#   --report-file <path>   Write the Markdown report to a file (default: stdout)
#
# Exit codes:
#   0 = clean        (no Bazel-relevant changes)
#   1 = build-changes (build-relevant changes detected)
#   2 = conflict     (reserved for caller to set when merge had conflicts)

set -euo pipefail

BASE_REF=""
HEAD_REF=""
REPORT_FILE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --report-file)
            REPORT_FILE="$2"
            shift 2
            ;;
        *)
            if [[ -z "$BASE_REF" ]]; then
                BASE_REF="$1"
            elif [[ -z "$HEAD_REF" ]]; then
                HEAD_REF="$1"
            else
                echo "Error: unexpected argument: $1" >&2
                exit 2
            fi
            shift
            ;;
    esac
done

if [[ -z "$BASE_REF" || -z "$HEAD_REF" ]]; then
    echo "Usage: $0 <base-ref> <head-ref> [--report-file <path>]" >&2
    exit 2
fi

# Use three-dot diff to get only upstream-side changes (not bazel-only changes)
DIFF_RANGE="${BASE_REF}...${HEAD_REF}"

has_build_changes=false
report=""

append() {
    report+="$1"$'\n'
}

# ─── Section: New .cs files ───────────────────────────────────────────────────

new_cs_files=$(git diff --diff-filter=A --name-only "$DIFF_RANGE" -- '*.cs' 2>/dev/null || true)

if [[ -n "$new_cs_files" ]]; then
    append "## ➕ New C# Source Files"
    append ""

    needs_update_files=""
    auto_included_files=""
    no_build_files=""

    while IFS= read -r file; do
        # Determine which library this file belongs to
        lib_name=""
        build_file=""

        if [[ "$file" == src/libraries/* ]]; then
            # Extract library name: src/libraries/<LibName>/...
            lib_name=$(echo "$file" | cut -d'/' -f3)
            build_file="src/libraries/${lib_name}/BUILD.bazel"
        elif [[ "$file" == src/coreclr/* ]]; then
            # For coreclr, find the nearest BUILD.bazel
            dir=$(dirname "$file")
            while [[ "$dir" != "src/coreclr" && "$dir" != "." ]]; do
                if [[ -f "$dir/BUILD.bazel" ]]; then
                    build_file="$dir/BUILD.bazel"
                    lib_name="coreclr/$(basename "$dir")"
                    break
                fi
                dir=$(dirname "$dir")
            done
        elif [[ "$file" == src/native/* ]]; then
            dir=$(dirname "$file")
            while [[ "$dir" != "src/native" && "$dir" != "." ]]; do
                if [[ -f "$dir/BUILD.bazel" ]]; then
                    build_file="$dir/BUILD.bazel"
                    lib_name="native/$(basename "$dir")"
                    break
                fi
                dir=$(dirname "$dir")
            done
        fi

        if [[ -z "$build_file" || ! -f "$build_file" ]]; then
            no_build_files+="- \`$file\` — no BUILD.bazel found"$'\n'
        elif grep -q 'glob(' "$build_file" 2>/dev/null; then
            auto_included_files+="- \`$file\` (\`$lib_name\` — uses glob, likely auto-included)"$'\n'
        else
            needs_update_files+="- \`$file\` (\`$lib_name\` — **explicit srcs, needs BUILD.bazel update**)"$'\n'
            has_build_changes=true
        fi
    done <<< "$new_cs_files"

    if [[ -n "$needs_update_files" ]]; then
        append "### ⚠️ Needs BUILD.bazel update (explicit srcs)"
        append "$needs_update_files"
    fi
    if [[ -n "$auto_included_files" ]]; then
        append "### ✅ Auto-included (glob pattern)"
        append "$auto_included_files"
    fi
    if [[ -n "$no_build_files" ]]; then
        append "### ℹ️ No BUILD.bazel file"
        append "$no_build_files"
    fi
fi

# ─── Section: Removed .cs files ──────────────────────────────────────────────

removed_cs_files=$(git diff --diff-filter=D --name-only "$DIFF_RANGE" -- '*.cs' 2>/dev/null || true)

if [[ -n "$removed_cs_files" ]]; then
    append "## ➖ Removed C# Source Files"
    append ""

    while IFS= read -r file; do
        lib_name=""
        build_file=""

        if [[ "$file" == src/libraries/* ]]; then
            lib_name=$(echo "$file" | cut -d'/' -f3)
            build_file="src/libraries/${lib_name}/BUILD.bazel"
        fi

        if [[ -n "$build_file" && -f "$build_file" ]]; then
            if grep -q 'glob(' "$build_file" 2>/dev/null; then
                append "- \`$file\` (\`$lib_name\` — uses glob, auto-removed)"
            else
                append "- \`$file\` (\`$lib_name\` — **explicit srcs, needs BUILD.bazel update**)"
                has_build_changes=true
            fi
        else
            append "- \`$file\` — no BUILD.bazel found"
        fi
    done <<< "$removed_cs_files"
    append ""
fi

# ─── Section: Modified .csproj files ─────────────────────────────────────────

changed_csproj=$(git diff --name-only "$DIFF_RANGE" -- '*.csproj' 2>/dev/null || true)

if [[ -n "$changed_csproj" ]]; then
    # Filter to only src/ csproj files (skip eng/, tests that aren't in Bazel, etc.)
    relevant_csproj=""
    while IFS= read -r file; do
        if [[ "$file" == src/libraries/*/src/*.csproj || "$file" == src/coreclr/*.csproj ]]; then
            relevant_csproj+="$file"$'\n'
        fi
    done <<< "$changed_csproj"

    if [[ -n "$relevant_csproj" ]]; then
        append "## 📦 Modified Project Files (.csproj)"
        append ""
        append "These may affect Bazel deps, defines, or compilation settings:"
        append ""
        while IFS= read -r file; do
            [[ -z "$file" ]] && continue
            lib_name=$(echo "$file" | cut -d'/' -f3)
            # Show what changed in the csproj
            changes=$(git diff "$DIFF_RANGE" -- "$file" 2>/dev/null | grep '^[+-]' | grep -v '^[+-][+-][+-]' | grep -iE 'Compile|Reference|DefineConstants|AllowUnsafe|TargetFramework|Condition' | head -10 || true)
            append "- \`$file\` (\`$lib_name\`)"
            if [[ -n "$changes" ]]; then
                append '  ```diff'
                append "  $changes"
                append '  ```'
                has_build_changes=true
            fi
        done <<< "$relevant_csproj"
        append ""
    fi
fi

# ─── Section: Modified .props/.targets files ─────────────────────────────────

changed_props=$(git diff --name-only "$DIFF_RANGE" -- '*.props' '*.targets' 2>/dev/null || true)

if [[ -n "$changed_props" ]]; then
    # Filter to relevant build files
    relevant_props=""
    while IFS= read -r file; do
        if [[ "$file" == src/libraries/* || "$file" == src/coreclr/* || "$file" == eng/* || "$file" == Directory.Build.* ]]; then
            relevant_props+="$file"$'\n'
        fi
    done <<< "$changed_props"

    if [[ -n "$relevant_props" ]]; then
        append "## 🔧 Modified Build Properties (.props/.targets)"
        append ""
        append "These may affect compilation defines, references, or file includes:"
        append ""
        while IFS= read -r file; do
            [[ -z "$file" ]] && continue
            is_version_props=false
            if [[ "$file" == eng/Version.Details.props || "$file" == eng/Versions.props ]]; then
                is_version_props=true
            fi

            changes=$(git diff "$DIFF_RANGE" -- "$file" 2>/dev/null | grep '^[+-]' | grep -v '^[+-][+-][+-]' | grep -iE 'DefineConstants|Reference|Compile|Condition|Property|Version' | head -20 || true)

            if [[ "$is_version_props" == true ]]; then
                # Cross-reference changed package versions against Bazel NuGet deps.
                # MODULE.bazel and paket/paket.main.bzl embed package versions;
                # if any changed package is used by Bazel, this is a build change.
                bazel_affected=""
                changed_pkg_names=$(git diff "$DIFF_RANGE" -- "$file" 2>/dev/null \
                    | grep '^[-+]' | grep -v '^[+-][+-][+-]' \
                    | grep -oP '<(\w+)PackageVersion>' \
                    | sed 's/<//;s/PackageVersion>//' \
                    | sort -u || true)

                if [[ -n "$changed_pkg_names" ]]; then
                    # Build a lookup file of NuGet package names used by Bazel,
                    # with dots stripped and lowercased for matching against
                    # MSBuild property names (e.g. "microsoftdotnetarcadesdk").
                    paket_lookup_file=$(mktemp)
                    if [[ -f "paket/paket.main.bzl" ]]; then
                        grep -oP '"name":\s*"\K[^"]+' paket/paket.main.bzl 2>/dev/null \
                            | while IFS= read -r name; do
                                stripped=$(echo "$name" | tr -d '.' | tr '[:upper:]' '[:lower:]')
                                echo "$stripped $name"
                            done > "$paket_lookup_file"
                    fi

                    while IFS= read -r pkg; do
                        [[ -z "$pkg" ]] && continue
                        pkg_lower=$(echo "$pkg" | tr '[:upper:]' '[:lower:]')
                        match=$(grep "^${pkg_lower} " "$paket_lookup_file" | head -1 | cut -d' ' -f2 || true)
                        if [[ -n "$match" ]]; then
                            bazel_affected+="$match "
                        fi
                    done <<< "$changed_pkg_names"
                    rm -f "$paket_lookup_file"
                fi

                if [[ -n "$bazel_affected" ]]; then
                    append "- \`$file\` ⚠️ **version bump affects Bazel NuGet deps:** ${bazel_affected}"
                    has_build_changes=true
                else
                    append "- \`$file\` _(version bump, no Bazel NuGet deps affected)_"
                fi
            else
                append "- \`$file\`"
                if [[ -n "$changes" ]]; then
                    has_build_changes=true
                fi
            fi
            if [[ -n "$changes" ]]; then
                append '  ```diff'
                append "  $changes"
                append '  ```'
            fi
        done <<< "$relevant_props"
        append ""
    fi
fi

# ─── Section: Modified CMakeLists.txt ─────────────────────────────────────────

changed_cmake=$(git diff --name-only "$DIFF_RANGE" -- '*CMakeLists.txt' '*.cmake' 2>/dev/null || true)

if [[ -n "$changed_cmake" ]]; then
    append "## 🛠️ Modified Native Build Files (CMake)"
    append ""
    append "These may affect native cc_library targets in Bazel:"
    append ""
    while IFS= read -r file; do
        [[ -z "$file" ]] && continue
        append "- \`$file\`"
        has_build_changes=true
    done <<< "$changed_cmake"
    append ""
fi

# ─── Section: New/removed .resx files ─────────────────────────────────────────

new_resx=$(git diff --diff-filter=A --name-only "$DIFF_RANGE" -- '*.resx' 2>/dev/null || true)
removed_resx=$(git diff --diff-filter=D --name-only "$DIFF_RANGE" -- '*.resx' 2>/dev/null || true)

if [[ -n "$new_resx" || -n "$removed_resx" ]]; then
    append "## 📝 New/Removed Resource Files (.resx)"
    append ""
    if [[ -n "$new_resx" ]]; then
        while IFS= read -r file; do
            append "- ➕ \`$file\`"
            has_build_changes=true
        done <<< "$new_resx"
    fi
    if [[ -n "$removed_resx" ]]; then
        while IFS= read -r file; do
            append "- ➖ \`$file\`"
            has_build_changes=true
        done <<< "$removed_resx"
    fi
    append ""
fi

# ─── Section: New test projects ───────────────────────────────────────────────

new_test_csproj=$(git diff --diff-filter=A --name-only "$DIFF_RANGE" -- '*Tests.csproj' '*Tests/*.csproj' 2>/dev/null || true)

if [[ -n "$new_test_csproj" ]]; then
    append "## 🧪 New Test Projects"
    append ""
    while IFS= read -r file; do
        append "- \`$file\`"
        has_build_changes=true
    done <<< "$new_test_csproj"
    append ""
fi

# ─── Section: New C/C++ source files ──────────────────────────────────────────

new_native_files=$(git diff --diff-filter=A --name-only "$DIFF_RANGE" -- '*.c' '*.cpp' '*.h' '*.S' 2>/dev/null | grep -E '^src/(coreclr|native)/' || true)

if [[ -n "$new_native_files" ]]; then
    append "## 🔩 New Native Source Files"
    append ""
    while IFS= read -r file; do
        append "- \`$file\`"
        has_build_changes=true
    done <<< "$new_native_files"
    append ""
fi

removed_native_files=$(git diff --diff-filter=D --name-only "$DIFF_RANGE" -- '*.c' '*.cpp' '*.h' '*.S' 2>/dev/null | grep -E '^src/(coreclr|native)/' || true)

if [[ -n "$removed_native_files" ]]; then
    append "## 🔩 Removed Native Source Files"
    append ""
    while IFS= read -r file; do
        append "- \`$file\`"
        has_build_changes=true
    done <<< "$removed_native_files"
    append ""
fi

# ─── Summary ──────────────────────────────────────────────────────────────────

# Use two-dot for commit count (commits on HEAD_REF not on BASE_REF)
total_upstream_commits=$(git rev-list --count "$BASE_REF".."$HEAD_REF" 2>/dev/null || echo "?")
# Use three-dot for file count (same as DIFF_RANGE used for file analysis)
total_files_changed=$(git diff --name-only "$DIFF_RANGE" 2>/dev/null | wc -l | tr -d ' ')

header="# Sync Report: \`release/10.0\` → \`bazel\`"$'\n'$'\n'
header+="**Upstream commits:** ${total_upstream_commits} | **Files changed:** ${total_files_changed}"$'\n'$'\n'

if [[ "$has_build_changes" == true ]]; then
    header+="**Classification:** ⚠️ **build-changes** — Bazel-relevant changes detected, review needed"$'\n'$'\n'
else
    header+="**Classification:** ✅ **clean** — No Bazel-relevant build changes detected"$'\n'$'\n'
fi

full_report="${header}${report}"

if [[ -z "$report" ]]; then
    full_report+="No Bazel-relevant changes detected in the upstream diff."$'\n'
fi

# Output
if [[ -n "$REPORT_FILE" ]]; then
    echo "$full_report" > "$REPORT_FILE"
    echo "Report written to $REPORT_FILE" >&2
else
    echo "$full_report"
fi

# Exit code
if [[ "$has_build_changes" == true ]]; then
    exit 1
else
    exit 0
fi
