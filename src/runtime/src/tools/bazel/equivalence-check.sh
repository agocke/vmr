#!/usr/bin/env bash
# equivalence-check.sh
#
# Runs the MSBuild vs Bazel equivalence check for an open sync PR.
# Self-discovering: finds the sync PR via gh CLI, no forwarded inputs needed.
#
# Usage:
#   ./src/tools/bazel/equivalence-check.sh [options]
#
# Options:
#   --repo OWNER/REPO          GitHub repository (default: auto-detect)
#   --branch NAME              Override: use this branch instead of discovering from PR
#   --pr-number NUMBER         Override: post comment to this PR (skip discovery)
#   --skip-pr-comment          Skip posting results to PR
#   --skip-msbuild-build       Skip MSBuild build (use existing artifacts)
#   --skip-bazel-build         Skip Bazel build (use existing artifacts)
#   --bazel-disk-cache PATH    Bazel disk cache path
#   -v, --verbose              Print commands as they execute
#   -h, --help                 Show this help
#
# How it works:
#   1. Queries GitHub for open PRs with 'bazel-sync-needs-attention' label
#   2. If none found, exits 0 (nothing to do)
#   3. Checks out the sync branch
#   4. Builds MSBuild runtime + Bazel runtime archive
#   5. Runs compare-runtime-packs.sh and compare-bazel.sh
#   6. Posts results as a PR comment
#
# Local testing:
#   ./src/tools/bazel/equivalence-check.sh --branch HEAD --skip-pr-comment
#
# Exit codes:
#   0 = success or nothing to do
#   1 = comparison failed (results posted to PR)
#   2 = usage error

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

REPO=""
BRANCH_OVERRIDE=""
PR_NUMBER_OVERRIDE=""
SKIP_PR_COMMENT=false
SKIP_MSBUILD=false
SKIP_BAZEL=false
BAZEL_DISK_CACHE=""
VERBOSE=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --repo)                REPO="$2"; shift 2 ;;
        --branch)              BRANCH_OVERRIDE="$2"; shift 2 ;;
        --pr-number)           PR_NUMBER_OVERRIDE="$2"; shift 2 ;;
        --skip-pr-comment)     SKIP_PR_COMMENT=true; shift ;;
        --skip-msbuild-build)  SKIP_MSBUILD=true; shift ;;
        --skip-bazel-build)    SKIP_BAZEL=true; shift ;;
        --bazel-disk-cache)    BAZEL_DISK_CACHE="$2"; shift 2 ;;
        -v|--verbose)          VERBOSE=true; shift ;;
        -h|--help)
            sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Error: unknown option: $1" >&2
            exit 2
            ;;
    esac
done

if [[ "$VERBOSE" == true ]]; then
    set -x
fi

cd "$REPO_ROOT"

# Auto-detect repo
if [[ -z "$REPO" ]]; then
    REPO=$(git remote get-url origin 2>/dev/null \
        | sed -n 's|.*github\.com[:/]\([^/]*/[^/.]*\).*|\1|p')
    if [[ -z "$REPO" ]]; then
        echo "Error: could not detect repository. Use --repo OWNER/REPO." >&2
        exit 2
    fi
fi

info()   { echo "==> $*"; }
detail() { echo "    $*"; }
err()    { echo "ERROR: $*" >&2; }

# ─── Step 1: Discover or use provided sync PR ────────────────────────────────

pr_number=""
sync_branch=""

if [[ -n "$BRANCH_OVERRIDE" ]]; then
    sync_branch="$BRANCH_OVERRIDE"
    pr_number="${PR_NUMBER_OVERRIDE:-}"
    info "Using override: branch=$sync_branch${pr_number:+, PR=#$pr_number}"
else
    info "Looking for open sync PRs needing attention..."

    pr_json=$(gh pr list --repo "$REPO" \
        --label bazel-sync-needs-attention \
        --state open \
        --json number,headRefName \
        --jq '.[0]' 2>/dev/null || echo "")

    if [[ -z "$pr_json" || "$pr_json" == "null" ]]; then
        info "No open sync PR with 'bazel-sync-needs-attention' label. Nothing to do."
        exit 0
    fi

    pr_number=$(echo "$pr_json" | jq -r '.number')
    sync_branch=$(echo "$pr_json" | jq -r '.headRefName')

    info "Found PR #${pr_number} on branch ${sync_branch}"
fi

# ─── Step 2: Checkout the sync branch ────────────────────────────────────────

current_branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")

if [[ "$sync_branch" == "HEAD" ]]; then
    info "Using current HEAD"
elif [[ "$current_branch" != "$sync_branch" ]]; then
    info "Checking out $sync_branch..."
    git fetch origin "$sync_branch" --quiet
    git checkout "$sync_branch" --quiet
fi

# ─── Step 3: Build MSBuild runtime ──────────────────────────────────────────

if [[ "$SKIP_MSBUILD" == true ]]; then
    info "MSBuild build skipped (--skip-msbuild-build)"
else
    info "Building MSBuild runtime..."
    ./build.sh clr+libs+host+packs -bl
fi

# ─── Step 4: Build Bazel runtime archive ─────────────────────────────────────

if [[ "$SKIP_BAZEL" == true ]]; then
    info "Bazel build skipped (--skip-bazel-build)"
else
    info "Building Bazel runtime archive..."
    bazel_args=(build //:runtime_archive --config=clr_release)
    if [[ -n "$BAZEL_DISK_CACHE" ]]; then
        bazel_args+=(--disk_cache="$BAZEL_DISK_CACHE")
    fi
    bazel "${bazel_args[@]}"
fi

# ─── Step 5: Run comparisons ─────────────────────────────────────────────────

info "Running comparisons..."

packs_status="success"
build_status="success"
packs_output=""
build_output=""

if packs_output=$(./compare-runtime-packs.sh --skip-build 2>&1); then
    detail "Runtime packs: ✅ Pass"
else
    detail "Runtime packs: ❌ Fail"
    packs_status="failure"
fi

if build_output=$(./compare-bazel.sh --skip-build 2>&1); then
    detail "Build inputs: ✅ Pass"
else
    detail "Build inputs: ❌ Fail"
    build_status="failure"
fi

# ─── Step 6: Post results to PR ──────────────────────────────────────────────

if [[ "$SKIP_PR_COMMENT" == true || -z "$pr_number" ]]; then
    if [[ "$SKIP_PR_COMMENT" == true ]]; then
        info "PR comment skipped (--skip-pr-comment)"
    else
        info "No PR number — skipping comment"
    fi
else
    info "Posting results to PR #${pr_number}..."

    comment="## Equivalence Check Results

| Check | Status |
|-------|--------|
| Runtime Packs | $([ "$packs_status" = "success" ] && echo "✅ Pass" || echo "❌ Fail") |
| Build Inputs | $([ "$build_status" = "success" ] && echo "✅ Pass" || echo "❌ Fail") |
"

    if [[ "$packs_status" != "success" ]]; then
        packs_tail=$(echo "$packs_output" | tail -50)
        comment+="
<details><summary>Runtime Packs Details</summary>

\`\`\`
${packs_tail}
\`\`\`
</details>
"
    fi

    if [[ "$build_status" != "success" ]]; then
        build_tail=$(echo "$build_output" | tail -50)
        comment+="
<details><summary>Build Inputs Details</summary>

\`\`\`
${build_tail}
\`\`\`
</details>
"
    fi

    gh pr comment "$pr_number" --repo "$REPO" --body "$comment"
fi

# ─── Done ────────────────────────────────────────────────────────────────────

echo ""
echo "  Runtime Packs: $([ "$packs_status" = "success" ] && echo "PASS" || echo "FAIL")"
echo "  Build Inputs:  $([ "$build_status" = "success" ] && echo "PASS" || echo "FAIL")"

if [[ "$packs_status" == "success" && "$build_status" == "success" ]]; then
    info "Done. All checks passed."
    exit 0
else
    info "Done. One or more checks failed."
    exit 1
fi
