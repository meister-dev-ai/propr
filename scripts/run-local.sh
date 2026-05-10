#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

ApiProject="$REPO_ROOT/src/MeisterProPR.Api/MeisterProPR.Api.csproj"
ProCursorProject="$REPO_ROOT/src/MeisterProPR.ProCursor.Service/MeisterProPR.ProCursor.Service.csproj"
UiFolder="$REPO_ROOT/admin-ui"
EnvFile="$REPO_ROOT/.env"

DB_CONNECTION_STRING="${DB_CONNECTION_STRING:-}"
PROCURSOR_DB_CONNECTION_STRING="${PROCURSOR_DB_CONNECTION_STRING:-}"
BACKEND_PORT="${BACKEND_PORT:-8080}"
PROCURSOR_PORT="${PROCURSOR_PORT:-8081}"
SKIP_UI_INSTALL=false
POSITIONAL_ARGS=()

LogDir="${RUN_LOCAL_LOG_DIR:-$REPO_ROOT/logs/local}"
LogFile="${RUN_LOCAL_LOG_FILE:-$LogDir/run-local-$(date +%Y%m%d-%H%M%S).log}"
DataProtectionKeysDir="${RUN_LOCAL_KEYS_DIR:-$HOME/.aspnet/DataProtection-Keys}"

usage() {
  echo "Usage: $0 '<DB_CONNECTION_STRING>' [backend-port] [procursor-port] [--skip-ui-install]"
}

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
      usage
      exit 1
      ;;
    *)
      POSITIONAL_ARGS+=("$1")
      ;;
  esac
  shift
done

if [ "${#POSITIONAL_ARGS[@]}" -gt 3 ]; then
  usage
  exit 1
fi

if [ "${#POSITIONAL_ARGS[@]}" -ge 1 ]; then
  DB_CONNECTION_STRING="${POSITIONAL_ARGS[0]}"
fi

if [ "${#POSITIONAL_ARGS[@]}" -ge 2 ]; then
  BACKEND_PORT="${POSITIONAL_ARGS[1]}"
fi

if [ "${#POSITIONAL_ARGS[@]}" -ge 3 ]; then
  PROCURSOR_PORT="${POSITIONAL_ARGS[2]}"
fi

mkdir -p "$LogDir" "$DataProtectionKeysDir"
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
      formatted="                    [$label] | $line"
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

trim() {
  printf '%s' "$1" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//'
}

describe_connection_target() {
  local connection_string="$1"
  local host=""
  local port=""
  local database=""
  local segment
  local key
  local value

  IFS=';' read -r -a segments <<< "$connection_string"
  for segment in "${segments[@]}"; do
    segment="$(trim "$segment")"
    [ -n "$segment" ] || continue

    case "$segment" in
      *=*)
        key="${segment%%=*}"
        value="${segment#*=}"
        key="$(printf '%s' "$key" | tr '[:upper:]' '[:lower:]')"
        value="$(trim "$value")"

        case "$key" in
          host|server|data\ source)
            host="$value"
            ;;
          port)
            port="$value"
            ;;
          database|initial\ catalog)
            database="$value"
            ;;
        esac
        ;;
    esac
  done

  [ -n "$host" ] || host="<unspecified-host>"
  [ -n "$port" ] || port="<default-port>"
  [ -n "$database" ] || database="<unspecified-db>"
  printf 'host=%s port=%s database=%s' "$host" "$port" "$database"
}

read_db_connection_from_local_podman() {
  if ! command -v podman >/dev/null 2>&1; then
    return 1
  fi

  local candidates=(
    "meisterpropr-pgvector-db"
    "docker-compose-postgres-1"
  )
  local container_name
  local status
  local port
  local env_line
  local username
  local password
  local database

  for container_name in "${candidates[@]}"; do
    status="$(podman inspect --format '{{.State.Status}}' "$container_name" 2>/dev/null || true)"
    [ "$status" = "running" ] || continue

    port="$(podman inspect --format '{{range $p, $bindings := .NetworkSettings.Ports}}{{if eq $p "5432/tcp"}}{{if gt (len $bindings) 0}}{{(index $bindings 0).HostPort}}{{end}}{{end}}{{end}}' "$container_name" 2>/dev/null || true)"
    [ -n "$port" ] || continue

    username="postgres"
    password=""
    database="postgres"

    while IFS= read -r env_line; do
      case "$env_line" in
        POSTGRES_USER=*)
          username="${env_line#POSTGRES_USER=}"
          ;;
        POSTGRES_PASSWORD=*)
          password="${env_line#POSTGRES_PASSWORD=}"
          ;;
        POSTGRES_DB=*)
          database="${env_line#POSTGRES_DB=}"
          ;;
      esac
    done < <(podman inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$container_name" 2>/dev/null || true)

    [ -n "$password" ] || continue

    DB_CONNECTION_STRING="Host=localhost;Port=$port;Database=$database;Username=$username;Password=$password"
    write_status "DB connection not provided; using running local Podman PostgreSQL container '$container_name' ($(describe_connection_target "$DB_CONNECTION_STRING"))"
    return 0
  done

  return 1
}

read_db_connection_from_user_secrets() {
  if ! command -v dotnet >/dev/null 2>&1 || [ ! -f "$ApiProject" ]; then
    return 1
  fi

  local secrets
  secrets="$(dotnet user-secrets list --project "$ApiProject" 2>/dev/null || true)"
  [ -n "$secrets" ] || return 1

  while IFS= read -r line; do
    line="$(trim "$line")"
    [ -n "$line" ] || continue

    case "$line" in
      *=*)
        local key="${line%%=*}"
        local value="${line#*=}"
        key="$(trim "$key")"
        value="$(trim "$value")"
        local lower_key
        lower_key="$(printf '%s' "$key" | tr '[:upper:]' '[:lower:]')"

        case "$lower_key" in
          db_connection_string|dbconnectionstring|connectionstrings:defaultconnection|connectionstrings:default|database:connectionstring|connectionstrings__defaultconnection|connectionstrings__default)
            DB_CONNECTION_STRING="$value"
            return 0
            ;;
        esac
        ;;
    esac
  done <<< "$secrets"

  return 1
}

read_secret_from_user_secrets() {
  local wanted_key="$1"

  if ! command -v dotnet >/dev/null 2>&1 || [ ! -f "$ApiProject" ]; then
    return 1
  fi

  local secrets
  secrets="$(dotnet user-secrets list --project "$ApiProject" 2>/dev/null || true)"
  [ -n "$secrets" ] || return 1

  while IFS= read -r line; do
    line="$(trim "$line")"
    [ -n "$line" ] || continue

    case "$line" in
      *=*)
        local key="${line%%=*}"
        local value="${line#*=}"
        key="$(trim "$key")"
        value="$(trim "$value")"
        if [ "$key" = "$wanted_key" ]; then
          printf '%s' "$value"
          return 0
        fi
        ;;
    esac
  done <<< "$secrets"

  return 1
}

if [ -z "$DB_CONNECTION_STRING" ]; then
  read_db_connection_from_local_podman || true
fi

if [ -z "$DB_CONNECTION_STRING" ]; then
  write_status "DB connection not provided; checking dotnet user-secrets"
  read_db_connection_from_user_secrets || true
fi

if [ -z "$DB_CONNECTION_STRING" ]; then
  usage
  exit 1
fi

if [ -z "$PROCURSOR_DB_CONNECTION_STRING" ]; then
  PROCURSOR_DB_CONNECTION_STRING="$DB_CONNECTION_STRING"
fi

ApiDbTarget="$(describe_connection_target "$DB_CONNECTION_STRING")"
ProCursorDbTarget="$(describe_connection_target "$PROCURSOR_DB_CONNECTION_STRING")"
if [ "$DB_CONNECTION_STRING" = "$PROCURSOR_DB_CONNECTION_STRING" ]; then
  write_status "Using shared local PostgreSQL target for ProPR and ProCursor ($ApiDbTarget)"
else
  write_status "Using ProPR PostgreSQL target ($ApiDbTarget)"
  write_status "Using ProCursor PostgreSQL target ($ProCursorDbTarget)"
fi

declare -A DOTENV
if [ -f "$EnvFile" ]; then
  while IFS= read -r line || [ -n "$line" ]; do
    line="$(trim "$line")"
    case "$line" in
      ''|\#*)
        continue
        ;;
    esac

    case "$line" in
      *=*)
        key="${line%%=*}"
        value="${line#*=}"
        key="$(trim "$key")"
        value="$(trim "$value")"
        if [[ "${value:0:1}" = '"' && "${value: -1}" = '"' ]] || [[ "${value:0:1}" = "'" && "${value: -1}" = "'" ]]; then
          value="${value:1:${#value}-2}"
        fi
        DOTENV["$key"]="$value"
        ;;
    esac
  done < "$EnvFile"
fi

if [ -z "${DOTENV[MEISTER_JWT_SECRET]:-}" ] && [ -z "${MEISTER_JWT_SECRET:-}" ]; then
  user_secret_jwt="$(read_secret_from_user_secrets "MEISTER_JWT_SECRET" || true)"
  if [ -n "$user_secret_jwt" ]; then
    DOTENV["MEISTER_JWT_SECRET"]="$user_secret_jwt"
  fi
fi

generate_shared_key() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32
    return 0
  fi

  printf '%s%s%s%s' "$RANDOM" "$RANDOM" "$(date +%s%N)" "$RANDOM"
}

ProCursorSharedKey="${PROCURSOR_SHARED_KEY:-$(generate_shared_key)}"

build_env_array_api() {
  local -n out_arr=$1
  out_arr=()
  out_arr+=("DB_CONNECTION_STRING=$DB_CONNECTION_STRING")
  out_arr+=("ASPNETCORE_URLS=http://0.0.0.0:$BACKEND_PORT")
  out_arr+=("ASPNETCORE_ENVIRONMENT=Development")
  out_arr+=("LOKI_URL=")
  out_arr+=("PROCURSOR_REMOTE_MODE=proprManagedRemote")
  out_arr+=("PROCURSOR_SERVICE_BASE_URL=http://127.0.0.1:$PROCURSOR_PORT")
  out_arr+=("PROCURSOR_SHARED_KEY=$ProCursorSharedKey")
  out_arr+=("MEISTER_DATA_PROTECTION_KEYS_PATH=$DataProtectionKeysDir")

  for key in "${!DOTENV[@]}"; do
    case "$key" in
      DB_CONNECTION_STRING|PROCURSOR_DB_CONNECTION_STRING|ASPNETCORE_URLS|ASPNETCORE_ENVIRONMENT|LOKI_URL|PROCURSOR_REMOTE_MODE|PROCURSOR_SERVICE_BASE_URL|PROCURSOR_SHARED_KEY|MEISTER_DATA_PROTECTION_KEYS_PATH)
        continue
        ;;
      *)
        out_arr+=("$key=${DOTENV[$key]}")
        ;;
    esac
  done
}

build_env_array_procursor() {
  local -n out_arr=$1
  out_arr=()
  out_arr+=("ASPNETCORE_URLS=http://0.0.0.0:$PROCURSOR_PORT")
  out_arr+=("ASPNETCORE_ENVIRONMENT=Development")
  out_arr+=("LOKI_URL=")
  out_arr+=("PROCURSOR_PROPR_BASE_URL=http://127.0.0.1:$BACKEND_PORT")
  out_arr+=("PROCURSOR_DB_CONNECTION_STRING=$PROCURSOR_DB_CONNECTION_STRING")
  out_arr+=("PROCURSOR_SHARED_KEY=$ProCursorSharedKey")
  out_arr+=("MEISTER_DATA_PROTECTION_KEYS_PATH=$DataProtectionKeysDir")

  for key in "${!DOTENV[@]}"; do
    case "$key" in
      DB_CONNECTION_STRING|PROCURSOR_DB_CONNECTION_STRING|ASPNETCORE_URLS|ASPNETCORE_ENVIRONMENT|LOKI_URL|PROCURSOR_PROPR_BASE_URL|PROCURSOR_SHARED_KEY|MEISTER_DATA_PROTECTION_KEYS_PATH)
        continue
        ;;
      *)
        out_arr+=("$key=${DOTENV[$key]}")
        ;;
    esac
  done
}

build_env_array_ui() {
  local -n out_arr=$1
  out_arr=()
  out_arr+=("VITE_API_BASE_URL=/api")
  for key in "${!DOTENV[@]}"; do
    case "$key" in
      VITE_API_BASE_URL)
        continue
        ;;
      *)
        out_arr+=("$key=${DOTENV[$key]}")
        ;;
    esac
  done
}

wait_for_http_ready() {
  local name="$1"
  local port="$2"
  local pid="$3"
  local timeout_seconds="$4"
  local waited=0

  if ! command -v curl >/dev/null 2>&1; then
    write_status "curl not found; skipping $name readiness check"
    return 0
  fi

  write_status "Waiting for $name readiness at http://localhost:$port/healthz"

  while [ "$waited" -lt "$timeout_seconds" ]; do
    if curl -s --max-time 2 -o /dev/null "http://localhost:$port/healthz" 2>/dev/null; then
      write_status "$name is ready on http://localhost:$port"
      return 0
    fi

    if ! kill -0 "$pid" 2>/dev/null; then
      write_status "$name exited before becoming ready"
      return 1
    fi

    sleep 1
    waited=$((waited + 1))
  done

  write_status "Timed out waiting for $name readiness after ${timeout_seconds}s"
  return 1
}

TMPDIR="$(mktemp -d)"
API_OUT="$TMPDIR/api.out"
API_ERR="$TMPDIR/api.err"
PROCURSOR_OUT="$TMPDIR/procursor.out"
PROCURSOR_ERR="$TMPDIR/procursor.err"
UI_OUT="$TMPDIR/ui.out"
UI_ERR="$TMPDIR/ui.err"
mkfifo "$API_OUT" "$API_ERR" "$PROCURSOR_OUT" "$PROCURSOR_ERR" "$UI_OUT" "$UI_ERR"

pipe_stream "API" "$API_OUT" "stdout" &
API_OUT_READER=$!
pipe_stream "API" "$API_ERR" "stderr" &
API_ERR_READER=$!
pipe_stream "PROCURSOR" "$PROCURSOR_OUT" "stdout" &
PROCURSOR_OUT_READER=$!
pipe_stream "PROCURSOR" "$PROCURSOR_ERR" "stderr" &
PROCURSOR_ERR_READER=$!
pipe_stream "UI" "$UI_OUT" "stdout" &
UI_OUT_READER=$!
pipe_stream "UI" "$UI_ERR" "stderr" &
UI_ERR_READER=$!

cleanup() {
  echo
  write_status "Shutting down child processes"
  set +e
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  [ -n "${PROCURSOR_PID:-}" ] && kill "$PROCURSOR_PID" 2>/dev/null || true
  [ -n "${UI_PID:-}" ] && kill "$UI_PID" 2>/dev/null || true
  wait 2>/dev/null || true
  rm -f "$API_OUT" "$API_ERR" "$PROCURSOR_OUT" "$PROCURSOR_ERR" "$UI_OUT" "$UI_ERR"
  rmdir "$TMPDIR" 2>/dev/null || true
  exit 0
}
trap cleanup INT TERM

wait_for_log_readers() {
  for reader_pid in "$@"; do
    [ -n "${reader_pid:-}" ] || continue
    wait "$reader_pid" 2>/dev/null || true
  done
}

build_env_array_ui ui_env_assign
if [ "$SKIP_UI_INSTALL" = false ] && [ ! -d "$UiFolder/node_modules" ]; then
  write_status "Installing admin-ui dependencies (npm ci)"
  env "${ui_env_assign[@]}" npm ci --prefix "$UiFolder"
fi

build_env_array_api api_env_assign
build_env_array_procursor procursor_env_assign
build_env_array_ui ui_env_assign

write_status "Building backend and ProCursor host"
dotnet build "$ApiProject"
dotnet build "$ProCursorProject"

write_status "Starting backend, ProCursor, and admin UI in this terminal. Press Ctrl+C to stop all processes."
write_status "Local run log: $LogFile"
write_status "Shared ProCursor key generated for this run"

( env "${api_env_assign[@]}" dotnet run --project "$ApiProject" --no-build --no-launch-profile ) > "$API_OUT" 2> "$API_ERR" &
API_PID=$!

if ! wait_for_http_ready "API" "$BACKEND_PORT" "$API_PID" "${RUN_LOCAL_BACKEND_READY_TIMEOUT_SECONDS:-60}"; then
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  wait "$API_PID" 2>/dev/null || true
  wait_for_log_readers "${API_OUT_READER:-}" "${API_ERR_READER:-}"
  write_status "API startup logs were flushed to $LogFile"
  exit 1
fi

( env "${procursor_env_assign[@]}" dotnet run --project "$ProCursorProject" --no-build --no-launch-profile ) > "$PROCURSOR_OUT" 2> "$PROCURSOR_ERR" &
PROCURSOR_PID=$!

if ! wait_for_http_ready "ProCursor" "$PROCURSOR_PORT" "$PROCURSOR_PID" "${RUN_LOCAL_PROCURSOR_READY_TIMEOUT_SECONDS:-60}"; then
  [ -n "${PROCURSOR_PID:-}" ] && kill "$PROCURSOR_PID" 2>/dev/null || true
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  wait "$PROCURSOR_PID" 2>/dev/null || true
  wait "$API_PID" 2>/dev/null || true
  wait_for_log_readers "${PROCURSOR_OUT_READER:-}" "${PROCURSOR_ERR_READER:-}" "${API_OUT_READER:-}" "${API_ERR_READER:-}"
  write_status "Startup logs were flushed to $LogFile"
  exit 1
fi

( env "${ui_env_assign[@]}" npm run dev --prefix "$UiFolder" ) > "$UI_OUT" 2> "$UI_ERR" &
UI_PID=$!

write_status "API PID: $API_PID"
write_status "ProCursor PID: $PROCURSOR_PID"
write_status "UI PID: $UI_PID"
write_status "API -> http://localhost:$BACKEND_PORT  ProCursor -> http://localhost:$PROCURSOR_PORT  Admin UI -> http://localhost:5173"

API_EXIT=0
PROCURSOR_EXIT=0
UI_EXIT=0

while true; do
  if [ -n "${API_PID:-}" ] && ! kill -0 "$API_PID" 2>/dev/null; then
    wait "$API_PID" 2>/dev/null || API_EXIT=$?
    break
  fi

  if [ -n "${PROCURSOR_PID:-}" ] && ! kill -0 "$PROCURSOR_PID" 2>/dev/null; then
    wait "$PROCURSOR_PID" 2>/dev/null || PROCURSOR_EXIT=$?
    break
  fi

  if [ -n "${UI_PID:-}" ] && ! kill -0 "$UI_PID" 2>/dev/null; then
    wait "$UI_PID" 2>/dev/null || UI_EXIT=$?
    break
  fi

  sleep 0.1
done

if [ "$API_EXIT" -ne 0 ]; then
  write_status "API exited with code $API_EXIT"
  [ -n "${PROCURSOR_PID:-}" ] && kill "$PROCURSOR_PID" 2>/dev/null || true
  [ -n "${UI_PID:-}" ] && kill "$UI_PID" 2>/dev/null || true
  wait "$PROCURSOR_PID" 2>/dev/null || true
  wait "$UI_PID" 2>/dev/null || true
  wait_for_log_readers "${API_OUT_READER:-}" "${API_ERR_READER:-}" "${PROCURSOR_OUT_READER:-}" "${PROCURSOR_ERR_READER:-}" "${UI_OUT_READER:-}" "${UI_ERR_READER:-}"
  exit "$API_EXIT"
fi

if [ "$PROCURSOR_EXIT" -ne 0 ]; then
  write_status "ProCursor exited with code $PROCURSOR_EXIT"
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  [ -n "${UI_PID:-}" ] && kill "$UI_PID" 2>/dev/null || true
  wait "$API_PID" 2>/dev/null || true
  wait "$UI_PID" 2>/dev/null || true
  wait_for_log_readers "${API_OUT_READER:-}" "${API_ERR_READER:-}" "${PROCURSOR_OUT_READER:-}" "${PROCURSOR_ERR_READER:-}" "${UI_OUT_READER:-}" "${UI_ERR_READER:-}"
  exit "$PROCURSOR_EXIT"
fi

if [ "$UI_EXIT" -ne 0 ]; then
  write_status "UI exited with code $UI_EXIT"
  [ -n "${API_PID:-}" ] && kill "$API_PID" 2>/dev/null || true
  [ -n "${PROCURSOR_PID:-}" ] && kill "$PROCURSOR_PID" 2>/dev/null || true
  wait "$API_PID" 2>/dev/null || true
  wait "$PROCURSOR_PID" 2>/dev/null || true
  wait_for_log_readers "${API_OUT_READER:-}" "${API_ERR_READER:-}" "${PROCURSOR_OUT_READER:-}" "${PROCURSOR_ERR_READER:-}" "${UI_OUT_READER:-}" "${UI_ERR_READER:-}"
  exit "$UI_EXIT"
fi

wait "$API_PID" "$PROCURSOR_PID" "$UI_PID" 2>/dev/null || true
cleanup
