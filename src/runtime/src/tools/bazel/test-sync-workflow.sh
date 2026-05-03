#!/usr/bin/env bash
# test-sync-workflow.sh
#
# Simulates the GitHub Actions workflow locally to validate:
# - sync-upstream.sh runs without errors
# - Output parsing produces valid GITHUB_OUTPUT lines
# - Classification is a known value
#
# Usage:
#   ./src/tools/bazel/test-sync-workflow.sh
#
# This catches issues that only surface in CI, like:
# - SIGPIPE under set -euo pipefail
# - Multiple grep matches producing multi-line values
# - Hidden characters (\r) corrupting GITHUB_OUTPUT

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO_ROOT"

REPO=$(git remote get-url origin 2>/dev/null \
    | sed -n 's|.*github\.com[:/]\([^/]*/[^/.]*\).*|\1|p')

pass=0
fail=0

check() {
    local desc="$1" result="$2"
    if [[ "$result" == "ok" ]]; then
        echo "  ✅ $desc"
        pass=$((pass + 1))
    else
        echo "  ❌ $desc — $result"
        fail=$((fail + 1))
    fi
}

echo "=== Test: sync-upstream.sh output parsing ==="
echo ""

# Run the script the same way the YAML does (2>&1 capture, set +e)
set +e
output=$(src/tools/bazel/sync-upstream.sh \
    --repo "$REPO" --skip-push --skip-pr 2>&1)
exit_code=$?
set -e

check "Script exits cleanly (got $exit_code)" \
    "$( [[ $exit_code -eq 0 ]] && echo ok || echo "exit code $exit_code" )"

# Parse outputs the same way the YAML does
sync_branch=$(echo "$output" | grep '^ *Branch:' | tail -1 | awk '{print $2}' || true)
classification=$(echo "$output" | grep '^ *Classification:' | tail -1 | awk '{print $2}' || true)
pr_url=$(echo "$output" | grep '^ *PR:' | tail -1 | awk '{print $2}' || true)
remaining=$(echo "$output" | grep '^ *Remaining:' | tail -1 | awk '{print $2}' || true)

# Check if we got "already up-to-date" (no new commits)
if echo "$output" | grep -q "Already up-to-date\|already exists" 2>/dev/null; then
    echo ""
    echo "  ℹ️  No new commits or existing PR — skipping parse validation"
    echo ""
    echo "Results: $pass passed, $fail failed"
    exit $fail
fi

# Validate parsed values are single-line, non-empty, no hidden chars
check "sync_branch is non-empty" \
    "$( [[ -n "$sync_branch" ]] && echo ok || echo "empty" )"

check "sync_branch is single-line" \
    "$( [[ $(echo "$sync_branch" | wc -l) -eq 1 ]] && echo ok || echo "$(echo "$sync_branch" | wc -l) lines" )"

check "classification is non-empty" \
    "$( [[ -n "$classification" ]] && echo ok || echo "empty" )"

check "classification is single-line" \
    "$( [[ $(echo "$classification" | wc -l) -eq 1 ]] && echo ok || echo "$(echo "$classification" | wc -l) lines" )"

check "classification is a known value" \
    "$( [[ "$classification" =~ ^(clean|build-changes|conflict|unknown)$ ]] && echo ok || echo "got '$classification'" )"

check "no carriage returns in classification" \
    "$( echo -n "$classification" | grep -qP '\r' && echo "contains \\r" || echo ok )"

check "no carriage returns in sync_branch" \
    "$( echo -n "$sync_branch" | grep -qP '\r' && echo "contains \\r" || echo ok )"

# Simulate GITHUB_OUTPUT writes and validate format
echo ""
echo "=== Test: GITHUB_OUTPUT format ==="
echo ""

github_output=$(mktemp)
echo "has_new_commits=true" >> "$github_output"
echo "sync_branch=$sync_branch" >> "$github_output"
echo "classification=$classification" >> "$github_output"
if [[ -n "$pr_url" ]]; then
    pr_number=$(echo "$pr_url" | grep -oP '/pull/\K[0-9]+' || true)
    echo "pr_number=$pr_number" >> "$github_output"
fi

while IFS= read -r line; do
    if [[ "$line" =~ ^[a-zA-Z_][a-zA-Z0-9_]*=.+$ ]]; then
        check "valid output: $line" "ok"
    else
        check "valid output format" "invalid line: [$line]"
    fi
done < "$github_output"
rm -f "$github_output"

# Cleanup
git checkout - --quiet 2>/dev/null || true
git branch -D "sync/release-10.0-"* 2>/dev/null || true

echo ""
echo "Results: $pass passed, $fail failed"
exit $fail
