#!/usr/bin/env bash
# After a Bash call that published a release or pushed tags, remind the session that it can propose a Braggarr campaign.
# Reads the hook's JSON on stdin; prints nothing (and so adds nothing) for any other command, or when Braggarr is not set up.
set -u
input=$(cat)
[ -n "${BRAGGARR_URL:-}" ] || exit 0
if command -v jq >/dev/null 2>&1; then
  command=$(printf '%s' "$input" | jq -r '.tool_input.command // empty')
else
  command=$(printf '%s' "$input" | sed -n 's/.*"command"[[:space:]]*:[[:space:]]*"\(.*\)".*/\1/p' | head -n 1)
fi
case "$command" in
  *"gh release create"*|*"git push"*"--tags"*|*"git push"*" v"[0-9]*|*"git push"*" refs/tags/"*) ;;
  *) exit 0 ;;
esac
cat <<'JSON'
{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"That published a release or pushed a tag. If it holds something users would notice, offer the owner to propose a Braggarr campaign about it (the braggarr skill, or /brag). Do not propose without asking; nothing is posted without the owner's approval anyway."}}
JSON
