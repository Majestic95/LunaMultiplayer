#!/bin/bash
# Classifies edited files by Luna subsystem and suggests which review agent to invoke.
# Outputs a recommendation to stderr so it ends up in Claude's context window.
#
# Uses python for JSON parsing because jq is not available on Windows.

INPUT=$(cat)
FILE_PATH=$(printf '%s' "$INPUT" | python -c "import json,sys; d=json.load(sys.stdin); print(d.get('tool_input',{}).get('file_path',''))")

# Normalize Windows backslashes to forward slashes so regexes are portable.
FILE_PATH=${FILE_PATH//\\//}

# Only classify C# source files and project-level Markdown/docs
if [[ ! "$FILE_PATH" =~ \.(cs|md)$ ]]; then
  exit 0
fi

# Skip tests and build artifacts
if [[ "$FILE_PATH" =~ Test\.cs$ ]] \
   || [[ "$FILE_PATH" =~ /Test/ ]] \
   || [[ "$FILE_PATH" =~ /bin/ ]] \
   || [[ "$FILE_PATH" =~ /obj/ ]] \
   || [[ "$FILE_PATH" =~ \.Designer\.cs$ ]] \
   || [[ "$FILE_PATH" =~ \.g\.cs$ ]]; then
  exit 0
fi

DOMAIN=""

# Architecture / docs first (CLAUDE.md and docs/ override path-based heuristics)
if [[ "$FILE_PATH" =~ CLAUDE\.md$ ]] || [[ "$FILE_PATH" =~ /docs/ ]] || [[ "$FILE_PATH" =~ /Documentation/ ]]; then
  DOMAIN="ARCHITECTURE"
# Server-side subsystems
elif [[ "$FILE_PATH" =~ /Server/Message/ ]] || [[ "$FILE_PATH" =~ /Lidgren/ ]] || [[ "$FILE_PATH" =~ /LmpCommon/Message/ ]]; then
  DOMAIN="NETWORK"
elif [[ "$FILE_PATH" =~ /Server/System/.*Backup ]] || [[ "$FILE_PATH" =~ /Server/System/FileHandler\.cs$ ]] || [[ "$FILE_PATH" =~ /Server/Context/Universe\.cs$ ]]; then
  DOMAIN="PERSISTENCE"
elif [[ "$FILE_PATH" =~ /Server/System/ ]] || [[ "$FILE_PATH" =~ /Server/Command/ ]]; then
  DOMAIN="SERVER_SYSTEM"
elif [[ "$FILE_PATH" =~ /Server/Web/ ]]; then
  DOMAIN="WEB_DASHBOARD"
elif [[ "$FILE_PATH" =~ /Server/ ]]; then
  DOMAIN="SERVER_GENERAL"
# Client-side (KSP plugin)
elif [[ "$FILE_PATH" =~ /LmpClient/Harmony/ ]]; then
  DOMAIN="CLIENT_HARMONY"
elif [[ "$FILE_PATH" =~ /LmpClient/Systems/ ]] || [[ "$FILE_PATH" =~ /LmpClient/ ]]; then
  DOMAIN="CLIENT"
# Shared protocol / common
elif [[ "$FILE_PATH" =~ /LmpCommon/ ]]; then
  DOMAIN="COMMON"
else
  DOMAIN="GENERAL"
fi

# Emit a suggestion to stderr; format matches CE's REVIEW_SUGGESTED: convention
case "$DOMAIN" in
  NETWORK)
    echo "REVIEW_SUGGESTED: [Network Agent] — $FILE_PATH touches Lidgren / message handling. Run: review-agents/network-review.md" >&2
    ;;
  PERSISTENCE)
    echo "REVIEW_SUGGESTED: [Persistence Agent] — $FILE_PATH touches backup / Universe / FileHandler. Run: review-agents/persistence-review.md" >&2
    ;;
  SERVER_SYSTEM)
    echo "REVIEW_SUGGESTED: [Server Systems Agent] — $FILE_PATH is a Server/System or Server/Command file. Run: review-agents/server-systems-review.md" >&2
    ;;
  WEB_DASHBOARD)
    echo "REVIEW_SUGGESTED: [Server Systems Agent] — $FILE_PATH is in Server/Web (admin dashboard surface). Run: review-agents/server-systems-review.md" >&2
    ;;
  SERVER_GENERAL)
    echo "REVIEW_SUGGESTED: [Server Systems Agent] — $FILE_PATH is general server code. Run: review-agents/server-systems-review.md" >&2
    ;;
  CLIENT_HARMONY)
    echo "REVIEW_SUGGESTED: [Client/Harmony Agent] — $FILE_PATH is a Harmony patch on KSP internals. Run: review-agents/client-harmony-review.md" >&2
    ;;
  CLIENT)
    echo "REVIEW_SUGGESTED: [Client/Harmony Agent] — $FILE_PATH is in LmpClient (mod side, .NET Fx 4.7.2). Run: review-agents/client-harmony-review.md" >&2
    ;;
  COMMON)
    echo "REVIEW_SUGGESTED: [Network Agent] — $FILE_PATH is shared protocol (LmpCommon). Run: review-agents/network-review.md" >&2
    ;;
  ARCHITECTURE)
    echo "REVIEW_SUGGESTED: [Architecture Agent] — $FILE_PATH is a design doc / project config. Run: review-agents/architecture-review.md" >&2
    ;;
  *)
    echo "REVIEW_SUGGESTED: [General review] — $FILE_PATH. Run: review-prompt.md" >&2
    ;;
esac

exit 0
