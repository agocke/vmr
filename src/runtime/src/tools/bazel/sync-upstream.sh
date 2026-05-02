#!/usr/bin/env bash
# sync-upstream.sh
#
# Syncs the next upstream commit from release/10.0 into the bazel branch.
# Runs the full pipeline: check → merge → detect → push → PR.
#
# This script is the single source of truth for the sync logic.
# The GitHub Actions workflow is a thin wrapper that calls this script.
#
# Usage:
#   ./src/tools/bazel/sync-upstream.sh [options]
#
# Options:
#   --repo OWNER/REPO    GitHub repository (default: auto-detect from git remote)
#   --upstream-ref REF   Upstream branch to sync from (default: release/10.0)
#   --base-branch NAME   Local base branch (default: bazel)
#   --dry-run            Do everything locally (no push, no PR)
#   --detect-only        Only run change detection on existing HEAD vs base
#   --skip-push          Skip git push (but still create branch and merge)
#   --skip-pr            Skip PR creation (but still push)
#   --skip-fetch         Skip git fetch (use existing refs)
#   --output-file PATH   Write structured results to file (for CI consumption)
#   -v, --verbose        Print commands as they execute
#   -h, --help           Show this help
#
# Pipeline order:
#   1. Check for existing sync PRs
#   2. Fetch upstream, find next commit
#   3. Create sync branch, merge
#   4. Run change detection
#   5. Fix version bumps (deterministic — no Copilot needed)
#      - Updates paket.dependencies, defs.bzl, MODULE.bazel, BUILD.bazel files
#      - Runs paket install to regenerate paket.lock
#      - Runs sync-paket.sh to regenerate paket/paket.main.bzl
#   6. Push branch
#   7. Create PR (last step — everything is ready)
#
# Copilot auto-fix runs as a separate CI job after this script completes.
#
# Prerequisites:
#   - git with fetch access to upstream
#   - gh CLI (authenticated) for PR operations
#
# Exit codes:
#   0 = success (PR created, or up-to-date, or dry-run completed)
#   1 = failure
#   2 = usage error

set -euo pipefail

# ─── Defaults ─────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

REPO=""
UPSTREAM_REF="release/10.0"
BASE_BRANCH="bazel"
DRY_RUN=false
DETECT_ONLY=false
SKIP_PUSH=false
SKIP_PR=false
SKIP_FETCH=false
VERBOSE=false
OUTPUT_FILE=""

# ─── Parse arguments ─────────────────────────────────────────────────────────

while [[ $# -gt 0 ]]; do
    case "$1" in
        --repo)          REPO="$2"; shift 2 ;;
        --upstream-ref)  UPSTREAM_REF="$2"; shift 2 ;;
        --base-branch)   BASE_BRANCH="$2"; shift 2 ;;
        --dry-run)       DRY_RUN=true; shift ;;
        --detect-only)   DETECT_ONLY=true; shift ;;
        --skip-push)     SKIP_PUSH=true; shift ;;
        --skip-pr)       SKIP_PR=true; shift ;;
        --skip-fetch)    SKIP_FETCH=true; shift ;;
        --output-file)   OUTPUT_FILE="$2"; shift 2 ;;
        -v|--verbose)    VERBOSE=true; shift ;;
        -h|--help)
            sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Error: unknown option: $1" >&2
            echo "Run with --help for usage." >&2
            exit 2
            ;;
    esac
done

if [[ "$VERBOSE" == true ]]; then
    set -x
fi

if [[ "$DRY_RUN" == true ]]; then
    SKIP_PUSH=true
    SKIP_PR=true
fi

cd "$REPO_ROOT"

# Auto-detect repo from git remote if not specified
if [[ -z "$REPO" ]]; then
    REPO=$(git remote get-url origin 2>/dev/null \
        | sed -n 's|.*github\.com[:/]\([^/]*/[^/.]*\).*|\1|p')
    if [[ -z "$REPO" ]]; then
        echo "Error: could not detect repository. Use --repo OWNER/REPO." >&2
        exit 2
    fi
fi

# ─── Helpers ──────────────────────────────────────────────────────────────────

info()  { echo "==> $*"; }
detail() { echo "    $*"; }
err()   { echo "ERROR: $*" >&2; }

# Write structured results to output file (if specified) for CI consumption.
# Format: key=value, one per line. This replaces fragile stdout grep/awk parsing.
write_output() {
    if [[ -n "$OUTPUT_FILE" ]]; then
        echo "$1=$2" >> "$OUTPUT_FILE"
    fi
}

# Initialize output file (truncate if exists)
if [[ -n "$OUTPUT_FILE" ]]; then
    : > "$OUTPUT_FILE"
fi

# ─── Detect-only mode ────────────────────────────────────────────────────────

if [[ "$DETECT_ONLY" == true ]]; then
    info "Running change detection only"
    report_file="/tmp/sync-report.md"
    detect_exit=0
    "$SCRIPT_DIR/detect-upstream-changes.sh" "origin/$BASE_BRANCH" HEAD \
        --report-file "$report_file" || detect_exit=$?

    if [[ ! -f "$report_file" ]]; then
        err "Detection script failed (exit $detect_exit) and produced no report."
        exit 1
    fi

    cat "$report_file"
    exit "$detect_exit"
fi

# ─── Step 1: Check for existing sync PR ──────────────────────────────────────

info "Checking for existing sync PRs..."

open_prs=$(gh pr list --repo "$REPO" --base "$BASE_BRANCH" \
    --label bazel-sync --state open --json number --jq 'length' 2>/dev/null || echo "0")

if [[ "$open_prs" -gt 0 ]]; then
    info "An open sync PR already exists. Close or merge it first."
    write_output "has_new_commits" "false"
    exit 0
fi

# ─── Step 2: Fetch upstream and find next commit ─────────────────────────────

if [[ "$SKIP_FETCH" == true ]]; then
    info "Fetch skipped (--skip-fetch, using existing refs)"
else
    info "Fetching upstream..."
    git remote add upstream https://github.com/dotnet/runtime.git 2>/dev/null || true
    git fetch upstream "$UPSTREAM_REF" --quiet
    git fetch origin "$BASE_BRANCH" --quiet
fi

all_commits=$(git rev-list --reverse "origin/$BASE_BRANCH..upstream/$UPSTREAM_REF")
next_commit=$(head -1 <<< "$all_commits")

if [[ -z "$next_commit" ]]; then
    info "Already up-to-date with upstream/$UPSTREAM_REF."
    write_output "has_new_commits" "false"
    exit 0
fi

remaining=$(wc -l <<< "$all_commits" | tr -d ' ')
commit_short=$(git rev-parse --short "$next_commit")
commit_subject=$(git log -1 --format='%s' "$next_commit")

info "Next commit: $commit_short — $commit_subject"
detail "($remaining commit(s) pending)"

# ─── Step 3: Create sync branch and merge ────────────────────────────────────

branch="sync/${UPSTREAM_REF//\//-}-${commit_short}"

info "Creating branch: $branch"
git checkout -B "$branch" "origin/$BASE_BRANCH" --quiet

conflict=false
if git merge "$next_commit" --no-edit 2>/dev/null; then
    detail "Merge succeeded (no conflicts)"
else
    detail "Merge conflicts detected"
    conflict=true
    git add -A
    git commit --no-edit -m "Merge $next_commit (with conflicts)" 2>/dev/null || true
fi

# ─── Step 4: Run change detection ────────────────────────────────────────────

info "Running change detection..."

report_file="/tmp/sync-report.md"
classification="unknown"

if [[ "$conflict" == true ]]; then
    classification="conflict"
    cat > "$report_file" <<CONFLICT_EOF
# Sync Report: \`$UPSTREAM_REF\` → \`$BASE_BRANCH\`

**Classification:** ❌ **conflict** — Merge conflicts need manual resolution

## Conflicted Files
$(git diff --name-only --diff-filter=U HEAD 2>/dev/null || true)
CONFLICT_EOF
else
    detect_exit=0
    "$SCRIPT_DIR/detect-upstream-changes.sh" "origin/$BASE_BRANCH" "$next_commit" \
        --report-file "$report_file" || detect_exit=$?

    if [[ ! -f "$report_file" ]]; then
        err "Detection script failed (exit $detect_exit) and produced no report."
        exit 1
    fi

    case "$detect_exit" in
        0) classification="clean" ;;
        1) classification="build-changes" ;;
    esac
fi

detail "Classification: $classification"
echo ""
cat "$report_file"
echo ""

# ─── Step 5: Fix version bumps (deterministic, no Copilot needed) ─────────────

if [[ "$classification" == "build-changes" || "$classification" == "conflict" ]]; then
    info "Fixing NuGet version bumps..."

    # Save MODULE.bazel before FixVersionBumps.cs modifies it.
    # We need the original version during paket regeneration because
    # sync-paket.sh calls `bazel run @rules_dotnet//tools/paket2bazel`,
    # and Bazel validates MODULE.bazel use_repo entries against repos
    # registered in paket/paket.main.bzl. If MODULE.bazel has been updated
    # to reference new repo names (e.g., v26161.102) but paket.main.bzl
    # still has old names (e.g., v26125.123), the bazel command will fail
    # with "module extension does not generate repository".
    module_bazel_pre=$(mktemp)
    module_bazel_post=$(mktemp)
    cp "$REPO_ROOT/MODULE.bazel" "$module_bazel_pre"

    cd "$SCRIPT_DIR"
    if dotnet run FixVersionBumps.cs -- "origin/$BASE_BRANCH" "$next_commit" --repo-root "$REPO_ROOT"; then
        cd "$REPO_ROOT"
        if ! git diff --quiet; then
            detail "Version bump changes detected — regenerating paket files..."

            # Save the updated MODULE.bazel, then restore the original so
            # bazel can run against the still-consistent old paket.main.bzl.
            cp "$REPO_ROOT/MODULE.bazel" "$module_bazel_post"
            cp "$module_bazel_pre" "$REPO_ROOT/MODULE.bazel"

            paket_ok=false
            bazel_ok=false

            # Regenerate paket.lock from updated paket.dependencies
            if command -v paket &>/dev/null; then
                info "Running paket install to regenerate paket.lock..."
                if paket install; then
                    detail "paket.lock regenerated successfully."
                    paket_ok=true
                else
                    err "paket install failed — paket.lock may be stale."
                fi
            elif dotnet tool list -g 2>/dev/null | grep -qi paket; then
                info "Running dotnet paket install to regenerate paket.lock..."
                if dotnet paket install; then
                    detail "paket.lock regenerated successfully."
                    paket_ok=true
                else
                    err "dotnet paket install failed — paket.lock may be stale."
                fi
            else
                err "paket not found — skipping paket.lock regeneration."
                err "Install paket: dotnet tool install -g paket"
            fi

            # Regenerate paket/paket.main.bzl from updated paket.lock
            if command -v bazel &>/dev/null || command -v bazelisk &>/dev/null; then
                info "Running sync-paket.sh to regenerate paket/paket.main.bzl..."
                if "$REPO_ROOT/sync-paket.sh"; then
                    detail "paket/paket.main.bzl regenerated successfully."
                    bazel_ok=true
                else
                    err "sync-paket.sh failed — paket/paket.main.bzl may be stale."
                fi
            else
                err "bazel not found — skipping paket regeneration."
                err "paket/paket.main.bzl must be regenerated manually via sync-paket.sh."
            fi

            # Now restore the updated MODULE.bazel with new repo names.
            # paket.main.bzl has been regenerated and defines the new repos.
            cp "$module_bazel_post" "$REPO_ROOT/MODULE.bazel"

            if [[ "$paket_ok" == false || "$bazel_ok" == false ]]; then
                err "Paket regeneration incomplete (paket=$paket_ok, bazel=$bazel_ok)."
                err "Reverting version bump changes to avoid committing inconsistent state."
                git checkout -- paket.dependencies defs.bzl MODULE.bazel paket.lock paket/
                git checkout -- src/tools/bazel/
            else
                git add -A
                git diff --cached --stat
                git commit -m "Update Bazel NuGet versions and regenerate paket

Deterministic update of paket.dependencies, defs.bzl constants, and
MODULE.bazel use_repo entries. Regenerated paket/paket.main.bzl via
paket2bazel.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
                detail "Committed version bump fixes."
            fi
        else
            detail "No version bump changes needed."
        fi
    else
        err "FixVersionBumps.cs failed."
    fi
    cd "$REPO_ROOT"
    rm -f "$module_bazel_pre" "$module_bazel_post"
fi

# ─── Step 6: Push branch ─────────────────────────────────────────────────────

if [[ "$SKIP_PUSH" == true ]]; then
    info "Push skipped (--dry-run or --skip-push)"
else
    info "Pushing $branch..."
    git push --force-with-lease origin "$branch"
fi

# ─── Step 7: Create PR (last step — branch is fully ready) ───────────────────

if [[ "$SKIP_PR" == true ]]; then
    info "PR creation skipped (--dry-run or --skip-pr)"
else
    info "Creating PR..."

    case "$classification" in
        clean)
            title="✅ Sync ${commit_short}: ${commit_subject}"
            label_flags="--label bazel-sync --label bazel-sync-clean"
            ;;
        build-changes)
            title="⚠️ Sync ${commit_short}: ${commit_subject}"
            label_flags="--label bazel-sync --label bazel-sync-needs-attention"
            ;;
        conflict)
            title="❌ Sync ${commit_short}: ${commit_subject}"
            label_flags="--label bazel-sync --label bazel-sync-conflict"
            ;;
        *)
            title="Sync ${commit_short}: ${commit_subject}"
            label_flags="--label bazel-sync"
            ;;
    esac

    # Prepend commit info header
    header="**Upstream commit:** [\`${commit_short}\`](https://github.com/dotnet/runtime/commit/${next_commit}) — ${commit_subject}"
    header+="\n**Remaining after this:** $((remaining - 1)) commit(s)\n\n---\n\n"
    full_body=$(printf '%s' "$header" && cat "$report_file")
    echo "$full_body" > "$report_file"

    # Use GH_PR_TOKEN if available so the PR triggers pull_request workflows.
    # PRs created with GITHUB_TOKEN don't fire events (GitHub security feature).
    # shellcheck disable=SC2086
    pr_url=$(GH_TOKEN="${GH_PR_TOKEN:-$GH_TOKEN}" gh pr create \
        --repo "$REPO" \
        --base "$BASE_BRANCH" \
        --head "$branch" \
        --title "$title" \
        --body-file "$report_file" \
        $label_flags)

    pr_number=$(echo "$pr_url" | grep -oP '/pull/\K[0-9]+')
    if [[ -z "$pr_number" ]]; then
        err "Failed to extract PR number from: $pr_url"
        exit 1
    fi

    detail "Created: $pr_url"
fi

# ─── Done ─────────────────────────────────────────────────────────────────────

# Write structured outputs for CI
write_output "has_new_commits" "true"
write_output "sync_branch" "$branch"
write_output "classification" "$classification"
if [[ -n "${pr_number:-}" ]]; then
    write_output "pr_number" "$pr_number"
fi

info "Done."
echo ""
echo "  Branch:          $branch"
echo "  Classification:  $classification"
echo "  Remaining:       $((remaining - 1)) commit(s)"
if [[ -n "${pr_url:-}" ]]; then
    echo "  PR:              $pr_url"
fi
