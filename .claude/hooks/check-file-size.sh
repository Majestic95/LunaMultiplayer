#!/bin/bash
# Post-edit file-size check for Luna Multiplayer.
# Warns at 600 lines (soft cap), errors at 900 lines (hard cap).
# C# is more verbose than TypeScript, so caps are higher than CE's 400/500.
# Uses python for JSON parsing because jq is not available on Windows
# (silently no-oping was a known bug in the CE hook set — see user memory
# feedback_jq_not_available.md).

INPUT=$(cat)
FILE_PATH=$(printf '%s' "$INPUT" | python -c "import json,sys; d=json.load(sys.stdin); print(d.get('tool_input',{}).get('file_path',''))")

# Normalize Windows backslashes to forward slashes so regexes are portable.
FILE_PATH=${FILE_PATH//\\//}

# Only review C# source files
if [[ ! "$FILE_PATH" =~ \.cs$ ]]; then
  exit 0
fi

# Skip test files and projects, generated code, and auto-generated assembly info
if [[ "$FILE_PATH" =~ Test\.cs$ ]] \
   || [[ "$FILE_PATH" =~ /Test/ ]] \
   || [[ "$FILE_PATH" =~ /ServerTest/ ]] \
   || [[ "$FILE_PATH" =~ /LmpCommonTest/ ]] \
   || [[ "$FILE_PATH" =~ /LmpMasterServerTest/ ]] \
   || [[ "$FILE_PATH" =~ /Properties/AssemblyInfo\.cs$ ]] \
   || [[ "$FILE_PATH" =~ \.Designer\.cs$ ]] \
   || [[ "$FILE_PATH" =~ \.g\.cs$ ]] \
   || [[ "$FILE_PATH" =~ /bin/ ]] \
   || [[ "$FILE_PATH" =~ /obj/ ]]; then
  exit 0
fi

# Check file line count against caps
if [[ -f "$FILE_PATH" ]]; then
  LINE_COUNT=$(wc -l < "$FILE_PATH")
  if [[ $LINE_COUNT -gt 900 ]]; then
    echo "WARNING: $FILE_PATH is $LINE_COUNT lines (hard cap: 900). Must be split before merge." >&2
  elif [[ $LINE_COUNT -gt 600 ]]; then
    echo "NOTE: $FILE_PATH is $LINE_COUNT lines (soft cap: 600). Consider splitting." >&2
  fi
fi

exit 0
