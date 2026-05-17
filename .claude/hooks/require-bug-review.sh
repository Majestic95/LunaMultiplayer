#!/bin/bash
# Pre-commit bug-review gate for Luna Multiplayer.
#
# Fires on PreToolUse for Bash when the command looks like `git commit`.
# Reads the staged diff, filters to production source paths, and BLOCKS the
# commit (exit 2) unless a matching review receipt exists in
# .claude/review-receipts/. The receipt is a per-session marker Claude writes
# after running the appropriate review-agent on the staged content; its name
# is sha1(staged diff) so any subsequent edit invalidates the marker and
# forces a fresh review pass.
#
# Production code that requires a review = any .cs file NOT under */Test/,
# */ServerTest/, */LmpCommonTest/, */MockClientTest/, */LmpClientTest/,
# **/Properties/AssemblyInfo.cs, or **/Test*.cs.
# Pure-doc / config / scaffolding paths are exempt (CLAUDE.md, .claude/,
# docs/, Documentation/, .gitignore, .editorconfig, *.csproj, *.sln).
#
# Bootstrap: when ONLY exempt paths are staged (e.g. when committing this
# hook itself or a CLAUDE.md update), no receipt is required.

set -u

INPUT=$(cat)
COMMAND=$(printf '%s' "$INPUT" | python -c "import json,sys; d=json.load(sys.stdin); print(d.get('tool_input',{}).get('command',''))")

# Only act on `git commit` invocations. Be permissive about flags and quoting —
# the command shows up roughly verbatim as Claude wrote it. We don't try to
# block `git commit --amend` separately; same rule applies.
if [[ ! "$COMMAND" =~ git[[:space:]]+commit ]]; then
  exit 0
fi

# In case Claude is committing inside a subshell with a heredoc-style message,
# the command may be very long. We still only care that "git commit" is in
# there.

# Cache directory for receipts. Gitignored — see .gitignore.
RECEIPT_DIR=".claude/review-receipts"
mkdir -p "$RECEIPT_DIR"

# Enumerate staged files (relative paths from repo root).
STAGED=$(git diff --cached --name-only --diff-filter=ACMRT 2>/dev/null)
if [[ -z "$STAGED" ]]; then
  # Nothing staged — `git commit` will fail on its own, no point gating.
  exit 0
fi

# Filter to production code. Exemption rules below stay close to
# check-file-size.sh / classify-change.sh so the three hooks agree on what
# counts as production.
PRODUCTION_FILES=()
while IFS= read -r FILE; do
  # Normalize Windows backslashes (git on Windows often emits forward slashes
  # already, but be defensive).
  FILE_NORM=${FILE//\\//}

  # Exempt: documentation, config, .claude/, project files, generated, tests.
  if [[ "$FILE_NORM" =~ ^docs/ ]] \
     || [[ "$FILE_NORM" =~ ^Documentation/ ]] \
     || [[ "$FILE_NORM" =~ ^\.claude/ ]] \
     || [[ "$FILE_NORM" =~ ^CLAUDE\.md$ ]] \
     || [[ "$FILE_NORM" =~ ^README\.md$ ]] \
     || [[ "$FILE_NORM" =~ ^\.gitignore$ ]] \
     || [[ "$FILE_NORM" =~ ^\.editorconfig$ ]] \
     || [[ "$FILE_NORM" =~ \.csproj$ ]] \
     || [[ "$FILE_NORM" =~ \.sln$ ]] \
     || [[ "$FILE_NORM" =~ \.props$ ]] \
     || [[ "$FILE_NORM" =~ \.targets$ ]] \
     || [[ "$FILE_NORM" =~ /Test/ ]] \
     || [[ "$FILE_NORM" =~ ^ServerTest/ ]] \
     || [[ "$FILE_NORM" =~ ^LmpCommonTest/ ]] \
     || [[ "$FILE_NORM" =~ ^MockClientTest/ ]] \
     || [[ "$FILE_NORM" =~ ^LmpClientTest/ ]] \
     || [[ "$FILE_NORM" =~ /Properties/AssemblyInfo\.cs$ ]] \
     || [[ "$FILE_NORM" =~ \.Designer\.cs$ ]] \
     || [[ "$FILE_NORM" =~ \.g\.cs$ ]] \
     || [[ "$FILE_NORM" =~ Test\.cs$ ]]; then
    continue
  fi

  PRODUCTION_FILES+=("$FILE_NORM")
done <<< "$STAGED"

if [[ ${#PRODUCTION_FILES[@]} -eq 0 ]]; then
  # All staged files are exempt (docs, tests, scaffolding). No review needed.
  exit 0
fi

# Compute the receipt key: sha1 of the entire staged diff. Stable across
# re-runs of the same staged set; invalidated by any change.
DIFF_HASH=$(git diff --cached | sha1sum | awk '{print $1}')
RECEIPT_FILE="$RECEIPT_DIR/$DIFF_HASH.txt"

if [[ -f "$RECEIPT_FILE" ]]; then
  # Receipt is present — review has been done for this exact staged content.
  echo "[bug-review-gate] OK — receipt $RECEIPT_FILE found for staged production code." >&2
  exit 0
fi

# Block. Emit a structured message that Claude will see and act on.
{
  echo "=================================================================="
  echo " BUG-REVIEW REQUIRED — commit blocked by .claude/hooks/require-bug-review.sh"
  echo "=================================================================="
  echo
  echo "The following production files are staged but have no matching review receipt:"
  for f in "${PRODUCTION_FILES[@]}"; do
    echo "  - $f"
  done
  echo
  echo "Required: run the appropriate review-agent for each file (see"
  echo ".claude/review-agents/ and .claude/hooks/classify-change.sh for routing),"
  echo "address [MUST FIX] findings, then write a receipt before re-attempting:"
  echo
  echo "  echo \"reviewed by <agent-name> on \$(date -u +%FT%TZ); see commit message for findings\" > $RECEIPT_FILE"
  echo
  echo "The receipt key is sha1(staged diff) — any further edit to the staged set"
  echo "invalidates the receipt and forces a fresh review."
  echo
} >&2

exit 2
