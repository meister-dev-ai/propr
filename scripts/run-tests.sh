#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

FAILED=0

run_quiet() {
  local label="$1"
  shift
  local tmp exit_code
  tmp="$(mktemp)"
  exit_code=0

  printf 'Running %s...\n' "$label"
  "$@" > "$tmp" 2>&1 || exit_code=$?

  if [ "$exit_code" -ne 0 ]; then
    printf '\n[%s] FAILED (exit code %d):\n' "$label" "$exit_code" >&2
    cat "$tmp" >&2
    FAILED=1
  fi

  rm -f "$tmp"
}

run_quiet "dotnet test" dotnet test "$REPO_ROOT/MeisterProPR.slnx" --verbosity quiet
run_quiet "frontend type-check" npm run type-check --prefix "$REPO_ROOT/frontend"
run_quiet "frontend lint:css" npm run lint:css --prefix "$REPO_ROOT/frontend"
run_quiet "npm test" npm test --prefix "$REPO_ROOT/frontend"

if [ "$FAILED" -ne 0 ]; then
  printf '\nOne or more test suites failed.\n' >&2
  exit 1
fi

printf 'All tests passed.\n'
