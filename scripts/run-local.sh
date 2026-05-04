#!/usr/bin/env bash
set -euo pipefail

# Determine script and repo root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Defaults and arg parsing
DB_CONNECTION_STRING="${DB_CONNECTION_STRING:-}"
BACKEND_PORT="${BACKEND_PORT:-8080}"
SKIP_UI_INSTALL=false
POSITIONAL_ARGS=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --skip-ui-install|-s)
      SKIP_UI_INSTALL=true
      ;;
    --)
      shift
      while [ "$#" -gt 0 ]; do
        POSITIONAL_ARGS+=("$1")
        shift
      done
      break
      ;;
    -*)
      echo "Unknown option: $1"
      echo "Usage: $0 '<DB_CONNECTION_STRING>' [backend-port] [--skip-ui-install]"
      exit 1
      ;;
    *)
      POSITIONAL_ARGS+=("$1")
      ;;
  esac
  shift
done

if [ "${#POSITIONAL_ARGS[@]}" -gt 2 ]; then
  echo "Usage: $0 '<DB_CONNECTION_STRING>' [backend-port] [--skip-ui-install]"
  exit 1
fi

if [ "${#POSITIONAL_ARGS[@]}" -ge 1 ]; then
  DB_CONNECTION_STRING="${POSITIONAL_ARGS[0]}"
fi

if [ "${#POSITIONAL_ARGS[@]}" -ge 2 ]; then
  BACKEND_PORT="${POSITIONAL_ARGS[1]}"
fi

ApiProject="$REPO_ROOT/src/MeisterProPR.Api/MeisterProPR.Api.csproj"
ApiFolder="$REPO_ROOT/src/MeisterProPR.Api"
UiFolder="$REPO_ROOT/admin-ui"
EnvFile="$REPO_ROOT/.env"
LogDir="${RUN_LOCAL_LOG_DIR:-$REPO_ROOT/logs/local}"
LogFile="${RUN_LOCAL_LOG_FILE:-$LogDir/run-local-$(date +%Y%m%d-%H%M%S).log}"

mkdir -p "$(dirname "$LogFile")"
: > "$LogFile"

write_status() {
  local message="$1"
  local timestamp
  timestamp="$(date +"%Y-%m-%d %H:%M:%S")"
  printf '%s %s\n' "$timestamp" "$message" | tee -a "$LogFile"
}

pipe_stream() {
  local label="$1"
  local pipe_path="$2"
  local output_target="$3"
  local timestamp
  local formatted
  local in_db_command_block=false

  while IFS= read -r line || [ -n "$line" ]; do
    if [[ "$line" =~ ^\[[0-9]{2}:[0-9]{2}:[0-9]{2}\ [A-Z]{3}\] ]]; then
      timestamp="$(date +"%Y-%m-%d %H:%M:%S")"
      formatted="$timestamp [$label] $line"
      if [[ "$line" == *"DbCommand ("* ]]; then
        in_db_command_block=true
      else
        in_db_command_block=false
      fi
    elif [ "$in_db_command_block" = true ]; then
      formatted="                    [$label] │ $line"
    else
      timestamp="$(date +"%Y-%m-%d %H:%M:%S")"
      formatted="$timestamp [$label] $line"
    fi

    printf '%s\n' "$formatted" >> "$LogFile"

    if [ "$output_target" = "stderr" ]; then
      printf '%s\n' "$formatted" >&2
    else
      printf '%s\n' "$formatted"
    fi
  done < "$pipe_path"
}

# If DB connection string not provided, try dotnet user-secrets for the API project.
if [ -z "$DB_CONNECTION_STRING" ]; then
  echo "DB connection not provided; checking dotnet user-secrets..."
  if command -v dotnet >/dev/null 2>&1 && [ -f "$ApiProject" ]; then
    secrets="$(dotnet user-secrets list --project "$ApiProject" 2>/dev/null || true)"
    if [ -n "$secrets" ]; then
      while IFS= read -r line; do
        line_trim="$(echo "$line" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
        [ -z "$line_trim" ] && continue
        case "$line_trim" in
          *=*)
            key="${line_trim%%=*}"
            val="${line_trim#*=}"
            key="$(echo "$key" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
            val="$(echo "$val" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
            lower_key="$(echo "$key" | tr '[:upper:]' '[:lower:]')"
            for candidate in db_connection_string dbconnectionstring connectionstrings:defaultconnection connectionstrings:default database:connectionstring connectionstrings__defaultconnection connectionstrings__default; do
              if [ "$lower_key" = "$candidate" ]; then
                DB_CONNECTION_STRING="$val"
                break 3
              fi
            done
            ;;
        esac
      done <<< "$secrets"
    fi
  fi

  if [ -z "$DB_CONNECTION_STRING" ]; then
    echo "Usage: $0 '<DB_CONNECTION_STRING>' [backend-port] [--skip-ui-install]"
    exit 1
  fi
fi

ApiProject="$REPO_ROOT/src/MeisterProPR.Api/MeisterProPR.Api.csproj"
ApiFolder="$REPO_ROOT/src/MeisterProPR.Api"
UiFolder="$REPO_ROOT/admin-ui"
EnvFile="$REPO_ROOT/.env"

# Read .env into associative array DOTENV
declare -A DOTENV
if [ -f "$EnvFile" ]; then
  while IFS= read -r line || [ -n "$line" ]; do
    # trim leading/trailing whitespace
    line="$(echo "$line" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    # skip empty lines and comments
    case "$line" in
      ''|\#*) continue ;;
    esac
    if ! echo "$line" | grep -q '='; then
      continue
    fi
    key="${line%%=*}"
    val="${line#*=}"
    key="$(echo "$key" | sed -e 's/[[:space:]]*$//')"
    # remove surrounding quotes if present
    if [[ "${val:0:1}" = '"' && "${val: -1}" = '"' ]] || [[ "${val:0:1}" = "'" && "${val: -1}" = "'" ]]; then
      val="${val:1:${#val}-2}"
    fi
    DOTENV["$key"]="$val"
  done < "$EnvFile"
fi

# Helper to build env assignment array for env command
build_env_array_api() {
  local -n out_arr=$1
  out_arr=()
  out_arr+=("DB_CONNECTION_STRING=$DB_CONNECTION_STRING")
  out_arr+=("ASPNETCORE_URLS=http://0.0.0.0:$BACKEND_PORT")
  out_arr+=("ASPNETCORE_ENVIRONMENT=Development")
  out_arr+=("LOKI_URL=")
  for k in "${!DOTENV[@]}"; do
    # only add if not already defined by api env
    case "$k" in
      DB_CONNECTION_STRING|ASPNETCORE_URLS|ASPNETCORE_ENVIRONMENT|LOKI_URL) continue ;;
      *) out_arr+=("$k=${DOTENV[$k]}") ;;
    esac
  done
}

build_env_array_ui() {
  local -n out_arr=$1
  out_arr=()
  for k in "${!DOTENV[@]}"; do
    out_arr+=("$k=${DOTENV[$k]}")
  done
}

wait_for_backend_ready() {
  local timeout_seconds="${RUN_LOCAL_BACKEND_READY_TIMEOUT_SECONDS:-60}"
  local waited=0

  if ! command -v curl >/dev/null 2>&1; then
    write_status "curl not found; starting admin UI without backend readiness check."
    return 0
  fi

  write_status "Waiting for backend readiness at http://localhost:$BACKEND_PORT/healthz"

  while [ "$waited" -lt "$timeout_seconds" ]; do
    if curl -s --max-time 2 -o /dev/null "http://localhost:$BACKEND_PORT/healthz" 2>/dev/null; then
      write_status "Backend is ready on http://localhost:$BACKEND_PORT"
      return 0
    fi

    if [ -n "${API_PID:-}" ] && ! kill -0 "$API_PID" 2>/dev/null; then
      wait "$API_PID" 2>/dev/null || API_EXIT=$?
      write_status "API exited before becoming ready"
      return 1
    fi

    sleep 1
    waited=$((waited + 1))
  done

  write_status "Timed out waiting for backend readiness after ${timeout_seconds}s"
  return 1
}

# Create temp dir and pipes
TMPDIR="$(mktemp -d)"
API_OUT="$TMPDIR/api.out"
API_ERR="$TMPDIR/api.err"
UI_OUT="$TMPDIR/ui.out"
UI_ERR="$TMPDIR/ui.err"
mkfifo "$API_OUT" "$API_ERR" "$UI_OUT" "$UI_ERR"

# Start readers
pipe_stream "API" "$API_OUT" "stdout" &
API_OUT_READER=$!
pipe_stream "API" "$API_ERR" "stderr" &
API_ERR_READER=$!

pipe_stream "UI" "$UI_OUT" "stdout" &
UI_OUT_READER=$!
pipe_stream "UI" "$UI_ERR" "stderr" &
UI_ERR_READER=$!

# Function to cleanup pipes and readers
cleanup() {
  echo
  write_status "Shutting down child processes..."
  set +e
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  [ -n "${UI_PID:-}" ] && kill "$UI_PID" 2>/dev/null || true
  wait 2>/dev/null || true
  rm -f "$API_OUT" "$API_ERR" "$UI_OUT" "$UI_ERR"
  rmdir "$TMPDIR" 2>/dev/null || true
  exit 0
}
trap cleanup INT TERM

# Optionally install admin-ui deps (npm ci)
build_env_array_ui ui_env_assign
if [ "$SKIP_UI_INSTALL" = false ]; then
  if [ ! -d "$UiFolder/node_modules" ]; then
    write_status "Installing admin-ui dependencies (npm ci)..."
    env "${ui_env_assign[@]}" npm ci --prefix "$UiFolder"
    if [ $? -ne 0 ]; then
      write_status "npm ci failed"
      cleanup
    fi
  fi
fi

# Prepare child envs
build_env_array_api api_env_assign
build_env_array_ui ui_env_assign

write_status "Starting backend and admin UI in this terminal. Press Ctrl+C to stop both."
write_status "Local run log: $LogFile"

# Start API
( env "${api_env_assign[@]}" dotnet run --project "$ApiProject" --no-launch-profile ) > "$API_OUT" 2> "$API_ERR" &
API_PID=$!

if ! wait_for_backend_ready; then
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  wait "$API_PID" 2>/dev/null || true
  exit 1
fi

# Start UI
( env "${ui_env_assign[@]}" npm run dev --prefix "$UiFolder" ) > "$UI_OUT" 2> "$UI_ERR" &
UI_PID=$!

write_status "API PID: $API_PID"
write_status "UI  PID: $UI_PID"
write_status "API -> http://localhost:$BACKEND_PORT  Admin UI -> http://localhost:5173"

# Monitor children
API_EXIT=0
UI_EXIT=0

while true; do
  if [ -n "${API_PID:-}" ] && ! kill -0 "$API_PID" 2>/dev/null; then
    wait "$API_PID" 2>/dev/null || API_EXIT=$?
    break
  fi
  if [ -n "${UI_PID:-}" ] && ! kill -0 "$UI_PID" 2>/dev/null; then
    wait "$UI_PID" 2>/dev/null || UI_EXIT=$?
    break
  fi
  sleep 0.1
done

# If one exited with non-zero, kill the other and exit with its code
if [ "$API_EXIT" -ne 0 ]; then
  write_status "API exited with code $API_EXIT"
  [ -n "${UI_PID:-}" ] && kill "$UI_PID" 2>/dev/null || true
  wait "$UI_PID" 2>/dev/null || true
  exit "$API_EXIT"
fi

if [ "$UI_EXIT" -ne 0 ]; then
  write_status "UI exited with code $UI_EXIT"
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  wait "$API_PID" 2>/dev/null || true
  exit "$UI_EXIT"
fi

# Normal exit: wait for both to finish
wait "$API_PID" "$UI_PID" 2>/dev/null || true
cleanup
