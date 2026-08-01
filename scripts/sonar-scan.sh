#!/usr/bin/env bash
# Runs a local SonarQube analysis of the solution with test coverage imported.
#
# Usage:
#   scripts/sonar-scan.sh <sonar-token> [options]
#   SONAR_TOKEN=<token> scripts/sonar-scan.sh [options]
#
# Options:
#   --host <url>       SonarQube host (default: $SONAR_HOST_URL, else http://localhost:9000)
#   --key <key>        Project key (default: propr)
#   --name <name>      Project name (default: propr)
#   --label <version>  sonar.projectVersion (default: local)
#   --coverage-dir <d> Directory for the coverage reports (default: .ignore/coverage)
#   --skip-backend     Skip the .NET build, tests and C# coverage
#   --skip-frontend    Skip the Vitest run and the TypeScript/Vue coverage
#   --end-only         Publish an interrupted scan: run the end step alone, reusing the
#                      build output and coverage already on disk
#   -h, --help         Show this help
#
# A scan is one window, from begin to end, and the working tree has to hold still for it:
#
#   - Nothing else may build these projects, an IDE background build included. The scanner
#     captures every MSBuild build in the window, so a second build of the same project (a
#     different configuration counts as a second build) leaves two sets of highlighting
#     data for the same file, and the end step fails with "Cannot register highlighting
#     rule ... as it overlaps at least one existing rule".
#   - No editing or committing either. Roslyn reports issue positions against the code as
#     it was compiled, while the end step indexes the files as they are on disk, so an edit
#     in between fails the end step with "<n> is not a valid line offset for pointer" and
#     misplaces coverage.
#
# Both are checked before the end step, so a broken scan is reported in seconds instead of
# after the upload.
#
# Why the coverage flags are what they are:
#   - coverlet.collector writes Cobertura by default, which the C# analyzer does not
#     import. Format=opencover produces what sonar.cs.opencover.reportsPaths expects.
#   - Vitest writes no coverage report at all when a test fails, unless reportOnFailure
#     is set. A single failure would otherwise leave the frontend at 0 percent.
#   - The v8 provider roughly doubles the frontend suite's wall time, so the default 5s
#     per-test timeout starts to flake. This run raises it to 30s.
#   - Reports go under .ignore/, which sonar.exclusions already skips. The scanner does
#     not read .gitignore, so reports written elsewhere in the tree get indexed as
#     source files even though git ignores them.
#
# The token is passed on the scanner command line and is therefore visible in the local
# process list for the duration of the scan.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

TOKEN="${SONAR_TOKEN:-}"
HOST_URL="${SONAR_HOST_URL:-http://localhost:9000}"
PROJECT_KEY="propr"
PROJECT_NAME="propr"
PROJECT_VERSION="local"
COVERAGE_DIR="$REPO_ROOT/.ignore/coverage"
BUILD_CONFIG="Release"
RUN_BACKEND=1
RUN_FRONTEND=1
END_ONLY=0

# Prints the header comment above, up to the first line that is not a comment.
usage() {
  sed -n '2,${/^#/!q;s/^# \{0,1\}//p;}' "${BASH_SOURCE[0]}"
}

need_value() {
  if [ "$2" -lt 2 ]; then
    printf 'Option %s requires a value.\n' "$1" >&2
    exit 2
  fi
}

while [ $# -gt 0 ]; do
  case "$1" in
    --host) need_value "$1" $#; HOST_URL="$2"; shift 2 ;;
    --key) need_value "$1" $#; PROJECT_KEY="$2"; shift 2 ;;
    --name) need_value "$1" $#; PROJECT_NAME="$2"; shift 2 ;;
    --label) need_value "$1" $#; PROJECT_VERSION="$2"; shift 2 ;;
    --coverage-dir) need_value "$1" $#; COVERAGE_DIR="$2"; shift 2 ;;
    --skip-backend) RUN_BACKEND=0; shift ;;
    --skip-frontend) RUN_FRONTEND=0; shift ;;
    --end-only) END_ONLY=1; shift ;;
    -h|--help) usage; exit 0 ;;
    -*) printf 'Unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
    *) TOKEN="$1"; shift ;;
  esac
done

if [ -z "$TOKEN" ]; then
  printf 'No SonarQube token. Pass it as the first argument or set SONAR_TOKEN.\n\n' >&2
  usage >&2
  exit 2
fi

if [ "$RUN_BACKEND" -eq 0 ] && [ "$RUN_FRONTEND" -eq 0 ] && [ "$END_ONLY" -eq 0 ]; then
  printf 'Nothing to do: both --skip-backend and --skip-frontend were given.\n' >&2
  exit 2
fi

if ! command -v dotnet-sonarscanner > /dev/null 2>&1; then
  printf 'dotnet-sonarscanner not found. Install it with:\n' >&2
  printf '  dotnet tool install --global dotnet-sonarscanner\n' >&2
  exit 1
fi

SETTINGS="$REPO_ROOT/SonarQube.Analysis.xml"
FRONTEND_ROOT="$REPO_ROOT/frontend"
SCANNER_OUT="$REPO_ROOT/.sonarqube/out"
SOURCE_STATE="$REPO_ROOT/.sonarqube/propr-source-state"

if [ "$END_ONLY" -eq 1 ]; then
  if [ ! -f "$REPO_ROOT/.sonarqube/conf/SonarQubeAnalysisConfig.xml" ]; then
    printf 'No scan to finish: %s/.sonarqube holds no begin-step output.\n' "$REPO_ROOT" >&2
    printf 'Run the script without --end-only to scan from the start.\n' >&2
    exit 1
  fi
else
  if [ ! -f "$SETTINGS" ]; then
    printf 'Missing %s.\n' "$SETTINGS" >&2
    printf 'It carries sonar.exclusions. Without it the scan indexes nested worktrees and\n' >&2
    printf 'local .env files, and the server keeps a copy of everything it indexes.\n' >&2
    exit 1
  fi

  if [ "$RUN_FRONTEND" -eq 1 ] && [ ! -d "$FRONTEND_ROOT/node_modules" ]; then
    printf 'frontend/node_modules is missing. Run "npm ci --prefix frontend" first, or\n' >&2
    printf 'pass --skip-frontend to scan the backend only.\n' >&2
    exit 1
  fi
fi

BACKEND_COVERAGE="$COVERAGE_DIR/backend"
FRONTEND_COVERAGE="$COVERAGE_DIR/frontend"

step() {
  printf '\n=== %s ===\n' "$1"
}

# Identifies the committed and uncommitted state of the tracked sources. Untracked files
# are left out, so a scratch file appearing mid-scan does not read as a source change.
source_fingerprint() {
  {
    git -C "$REPO_ROOT" rev-parse HEAD
    git -C "$REPO_ROOT" status --porcelain --untracked-files=no
    git -C "$REPO_ROOT" diff HEAD
  } 2> /dev/null | sha256sum | cut -d ' ' -f 1
}

# Roslyn reported its issue positions against the code as it was compiled. If the sources
# moved on since, the end step reads positions that no longer exist.
assert_source_unchanged() {
  if ! git -C "$REPO_ROOT" rev-parse --git-dir > /dev/null 2>&1; then
    return 0
  fi

  if [ ! -f "$SOURCE_STATE" ]; then
    printf 'No source state was recorded at begin, so the sources cannot be compared.\n'
    return 0
  fi

  # The PowerShell twin hashes the same facts a different way, so its fingerprint is not
  # comparable with this one.
  if [ "$(sed -n '3p' "$SOURCE_STATE")" != "sh" ]; then
    printf 'Source state was recorded by sonar-scan.ps1; skipping the comparison.\n'
    return 0
  fi

  local expected_hash expected_head actual_hash actual_head
  expected_hash="$(sed -n '1p' "$SOURCE_STATE")"
  expected_head="$(sed -n '2p' "$SOURCE_STATE")"
  actual_hash="$(source_fingerprint)"
  actual_head="$(git -C "$REPO_ROOT" rev-parse HEAD 2> /dev/null)"
  if [ "$expected_hash" = "$actual_hash" ]; then
    printf 'Sources unchanged since the build.\n'
    return 0
  fi

  printf 'The sources changed after the build.\n\n' >&2
  if [ "$expected_head" != "$actual_head" ]; then
    printf '  HEAD at begin: %s\n' "$expected_head" >&2
    printf '  HEAD now:      %s\n' "$actual_head" >&2
  fi
  git -C "$REPO_ROOT" status --short --untracked-files=no >&2
  printf '\nRoslyn reported issue positions against the code it compiled, and the end step\n' >&2
  printf 'indexes the files as they are now, so it would fail with "is not a valid line\n' >&2
  printf 'offset for pointer" or place issues and coverage on the wrong lines.\n' >&2
  printf '\nRe-run the whole scan and leave the tree alone until it finishes. --end-only\n' >&2
  printf 'cannot help here: the Roslyn reports have to be regenerated.\n' >&2
  exit 1
}

# Two analyses of one project leave two sets of highlighting data for the same source
# file, and the end step throws in the protobuf importer when it imports the second.
# Catching it here costs seconds; letting the end step run costs the whole upload.
assert_single_analysis_per_project() {
  if [ ! -d "$SCANNER_OUT" ]; then
    return 0
  fi

  local duplicates
  duplicates="$(grep -ho '<ProjectName>[^<]*' "$SCANNER_OUT"/*/ProjectInfo.xml 2> /dev/null \
    | sed 's|<ProjectName>||' | sort | uniq -d)"
  if [ -z "$duplicates" ]; then
    return 0
  fi

  printf 'Some projects were analysed more than once in this scan:\n\n' >&2

  local stray=()
  local name info info_name info_config
  while IFS= read -r name; do
    for info in "$SCANNER_OUT"/*/ProjectInfo.xml; do
      [ -f "$info" ] || continue
      info_name="$(grep -o '<ProjectName>[^<]*' "$info" | sed 's|<ProjectName>||')"
      [ "$info_name" = "$name" ] || continue
      info_config="$(grep -o '<Configuration>[^<]*' "$info" | sed 's|<Configuration>||')"
      printf '  %-46s %-8s %s\n' "$info_name" "$info_config" "$(dirname "$info")" >&2
      if [ "$info_config" != "$BUILD_CONFIG" ]; then
        stray+=("$(dirname "$info")")
      fi
    done
  done <<< "$duplicates"

  printf '\nThe end step would fail while importing highlighting for a file that appears in\n' >&2
  printf 'two of these. Something built those projects while the scan was running, an IDE\n' >&2
  printf 'background build included.\n' >&2

  if [ "${#stray[@]}" -gt 0 ]; then
    printf '\nThe ones above that are not %s were not built by this script. Remove them:\n' "$BUILD_CONFIG" >&2
    printf '  rm -rf' >&2
    printf ' %s' "${stray[@]}" >&2
    printf '\n' >&2
  else
    printf '\nRemove the duplicate directories, keeping one analysis per project.\n' >&2
  fi

  printf 'Then publish without rebuilding:\n' >&2
  printf '  %s <token> --end-only\n' "$0" >&2
  exit 1
}

if [ "$END_ONLY" -eq 0 ]; then
  # Stale reports from an earlier run would be imported as if they were current.
  step "Clearing previous coverage reports"
  if [ "$RUN_BACKEND" -eq 1 ]; then
    rm -rf "$BACKEND_COVERAGE"
  fi
  if [ "$RUN_FRONTEND" -eq 1 ]; then
    rm -rf "$FRONTEND_COVERAGE"
  fi
  mkdir -p "$COVERAGE_DIR"
  printf 'Coverage directory: %s\n' "$COVERAGE_DIR"

  BEGIN_ARGS=(
    /k:"$PROJECT_KEY"
    /n:"$PROJECT_NAME"
    /v:"$PROJECT_VERSION"
    /s:"$SETTINGS"
    /d:sonar.host.url="$HOST_URL"
    /d:sonar.token="$TOKEN"
  )
  if [ "$RUN_BACKEND" -eq 1 ]; then
    BEGIN_ARGS+=(/d:sonar.cs.opencover.reportsPaths="$BACKEND_COVERAGE/**/coverage.opencover.xml")
  fi
  if [ "$RUN_FRONTEND" -eq 1 ]; then
    BEGIN_ARGS+=(/d:sonar.javascript.lcov.reportPaths="$FRONTEND_COVERAGE/lcov.info")
  fi

  step "Scanner begin"
  printf 'Do not build these projects until the scan finishes, in an IDE or elsewhere.\n\n'
  if ! dotnet sonarscanner begin "${BEGIN_ARGS[@]}"; then
    printf '\nScanner begin failed. Nothing was analysed.\n' >&2
    exit 1
  fi

  # Recorded now so the check before the end step can tell whether the sources moved on.
  if git -C "$REPO_ROOT" rev-parse --git-dir > /dev/null 2>&1; then
    {
      source_fingerprint
      git -C "$REPO_ROOT" rev-parse HEAD 2> /dev/null
      printf 'sh\n'
    } > "$SOURCE_STATE"
  fi
fi

TESTS_FAILED=0

if [ "$END_ONLY" -eq 0 ] && [ "$RUN_BACKEND" -eq 1 ]; then
  # The build has to happen between begin and end, so the scanner's targets can collect
  # the Roslyn analyzer output for every project.
  step "dotnet build"
  if ! dotnet build "$REPO_ROOT/MeisterDev.ProPR.slnx" --configuration "$BUILD_CONFIG" --nologo; then
    printf '\nBuild failed. Skipping the rest of the scan: an analysis without analyzer\n' >&2
    printf 'output for the failed projects would report misleading results.\n' >&2
    exit 1
  fi

  step "dotnet test with coverage"
  if ! dotnet test "$REPO_ROOT/MeisterDev.ProPR.slnx" --configuration "$BUILD_CONFIG" --no-build --nologo \
    --collect:"XPlat Code Coverage;Format=opencover" \
    --results-directory "$BACKEND_COVERAGE"; then
    printf '\nBackend tests failed. Continuing so the analysis still gets published.\n' >&2
    TESTS_FAILED=1
  fi
fi

if [ "$END_ONLY" -eq 0 ] && [ "$RUN_FRONTEND" -eq 1 ]; then
  step "Vitest with coverage"
  if ! (cd "$FRONTEND_ROOT" && npx vitest run --coverage \
    --coverage.reporter=lcov \
    --coverage.reporter=text-summary \
    --coverage.reportOnFailure \
    --coverage.reportsDirectory="$FRONTEND_COVERAGE" \
    --testTimeout=30000); then
    printf '\nFrontend tests failed. Continuing so the analysis still gets published.\n' >&2
    TESTS_FAILED=1
  fi
fi

step "Checking the scanner output"
assert_single_analysis_per_project
printf 'One analysis per project.\n'
assert_source_unchanged

if [ "$END_ONLY" -eq 1 ]; then
  printf 'Coverage comes from the paths the earlier begin step recorded.\n'
else
  if [ "$RUN_BACKEND" -eq 1 ]; then
    BACKEND_REPORTS=$(find "$BACKEND_COVERAGE" -name 'coverage.opencover.xml' 2> /dev/null | wc -l)
    printf 'C# OpenCover reports: %s\n' "$BACKEND_REPORTS"
    if [ "$BACKEND_REPORTS" -eq 0 ]; then
      printf 'No C# coverage was produced, so C# coverage will be reported as 0 percent.\n' >&2
    fi
  fi
  if [ "$RUN_FRONTEND" -eq 1 ]; then
    if [ -f "$FRONTEND_COVERAGE/lcov.info" ]; then
      printf 'TypeScript lcov report: %s\n' "$FRONTEND_COVERAGE/lcov.info"
    else
      printf 'No lcov.info was produced, so frontend coverage will be reported as 0 percent.\n' >&2
    fi
  fi
fi

# The end step must be given the token and nothing else.
step "Scanner end"
if ! dotnet sonarscanner end /d:sonar.token="$TOKEN"; then
  printf '\nScanner end failed. Nothing was published.\n' >&2
  exit 1
fi

printf '\nAnalysis published: %s/dashboard?id=%s\n' "${HOST_URL%/}" "$PROJECT_KEY"

if [ "$TESTS_FAILED" -ne 0 ]; then
  printf 'One or more test suites failed. Coverage above is from an incomplete run.\n' >&2
  exit 1
fi
