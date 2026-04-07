#!/usr/bin/env bash
# Configures git to use the repository's checked-in hooks from .githooks/.
# Run once after cloning:
#   bash scripts/setup-hooks.sh
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"

git config core.hooksPath .githooks
chmod +x "$REPO_ROOT/.githooks/pre-commit"

echo "Git hooks installed. Pre-commit format, build, and test checks are now active."
