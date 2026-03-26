# Configures git to use the repository's checked-in hooks from .githooks/.
# Run once after cloning:
#   pwsh scripts/setup-hooks.ps1

$ErrorActionPreference = 'Stop'

git config core.hooksPath .githooks

Write-Host "Git hooks installed. Pre-commit format, build, and test checks are now active."
