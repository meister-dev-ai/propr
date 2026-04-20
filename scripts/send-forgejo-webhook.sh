#!/usr/bin/env bash
set -euo pipefail

# send-forgejo-webhook.sh
# Thin alias for the shared Forgejo/Codeberg webhook helper.

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
TARGET_SCRIPT="$SCRIPT_DIR/send-codeberg-webhook.sh"

if [ ! -f "$TARGET_SCRIPT" ]; then
  echo "Missing helper script: $TARGET_SCRIPT" >&2
  exit 1
fi

FORWARDED_ARGS=("$@")
if [ "${1:-}" = "--help" ]; then
  FORWARDED_ARGS=(-h)
fi

if [ "${FORWARDED_ARGS[0]:-}" = "-h" ]; then
  cat <<EOF
Usage: $(basename "$0") -u URL -s SECRET -r REPO_ID -i PR_NUMBER [options]

This Forgejo-named wrapper delegates to $(basename "$TARGET_SCRIPT").
Forgejo and Codeberg use the same webhook payload and signature format in ProPR,
so all options are forwarded to the shared implementation.

EOF
fi

exec bash "$TARGET_SCRIPT" "${FORWARDED_ARGS[@]}"
