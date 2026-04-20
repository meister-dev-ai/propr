#!/usr/bin/env bash
set -euo pipefail

# send-gitlab-webhook.sh
# Sends one or more synthetic GitLab merge request webhook deliveries to a ProPR listener.
# Usage: send-gitlab-webhook.sh -u URL -s SECRET -p PROJECT_ID -i MR_IID [options]

usage() {
  cat <<EOF
Usage: $(basename "$0") -u URL -s SECRET -p PROJECT_ID -i MR_IID [options]

Required:
  -u URL        Listener URL (e.g. http://localhost:8080/webhooks/v1/providers/gitlab/<pathKey>)
  -s SECRET     GitLab secret token (must match the ProPR webhook secret)
  -p PROJECT_ID  GitLab project id (numeric or string)
  -i MR_IID     Merge request iid (integer)

Options:
  -P PATH       project path_with_namespace (default: PROJECT_ID)
  -a ACTION     Merge request action; can be provided multiple times.
                Defaults: open, update
                Add merge explicitly when you want to test lifecycle cancellation.
  -S SOURCE     Source branch (default: feature/test-branch)
  -T TARGET     Target branch (default: main)
  -U USERNAME   Merge request author username (default: propr-local)
  -D NAME       Merge request author display name (default: ProPR Local)
  -n N          Repeat each action N times (default: 1)
  -h            Show this help

Example:
  $(basename "$0") -u http://localhost:8080/webhooks/v1/providers/gitlab/ddfccfe1645e40c4a8e61c4516a11a74 \
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD \
    -p 101 -P acme/platform/propr -i 24

EOF
}

PROJECT_ID=""
PROJECT_PATH=""
MR_IID=""
SOURCE_BRANCH="feature/test-branch"
TARGET_BRANCH="main"
AUTHOR_USERNAME="propr-local"
AUTHOR_NAME="ProPR Local"
REPEAT=1
ACTIONS=()

json_escape() {
  local value="$1"
  value=${value//\\/\\\\}
  value=${value//\"/\\\"}
  value=${value//$'\n'/\\n}
  value=${value//$'\r'/\\r}
  value=${value//$'\t'/\\t}
  printf '%s' "$value"
}

json_value() {
  local value="$1"

  if [[ "$value" =~ ^[0-9]+$ ]]; then
    printf '%s' "$value"
    return
  fi

  printf '"%s"' "$(json_escape "$value")"
}

normalize_listener_url() {
  local value="$1"

  if [[ "$value" == *"/webhooks/v1/gitlab/"* && "$value" != *"/webhooks/v1/providers/"* ]]; then
    value=${value//\/webhooks\/v1\/gitlab\//\/webhooks\/v1\/providers\/gitlab\/}
  fi

  if [[ "$value" == *"/webhooks/v1/providers/gitLab/"* ]]; then
    value=${value//\/webhooks\/v1\/providers\/gitLab\//\/webhooks\/v1\/providers\/gitlab\/}
  fi

  printf '%s' "$value"
}

require_positive_integer() {
  local label="$1"
  local value="$2"

  case "$value" in
    ''|*[!0-9]*)
      echo "$label must be a positive integer." >&2
      exit 2
      ;;
  esac

  if [ "$value" -lt 1 ]; then
    echo "$label must be greater than zero." >&2
    exit 2
  fi
}

generate_delivery_uuid() {
  if command -v uuidgen >/dev/null 2>&1; then
    uuidgen
    return
  fi

  if [ -r /proc/sys/kernel/random/uuid ]; then
    cat /proc/sys/kernel/random/uuid
    return
  fi

  printf '%s-%s' "$(date +%s%N)" "$$"
}

while getopts ":u:s:p:i:P:S:T:a:U:D:n:h" opt; do
  case "$opt" in
    u) URL="$OPTARG" ;;
    s) SECRET="$OPTARG" ;;
    p) PROJECT_ID="$OPTARG" ;;
    i) MR_IID="$OPTARG" ;;
    P) PROJECT_PATH="$OPTARG" ;;
    S) SOURCE_BRANCH="$OPTARG" ;;
    T) TARGET_BRANCH="$OPTARG" ;;
    a) ACTIONS+=("$OPTARG") ;;
    U) AUTHOR_USERNAME="$OPTARG" ;;
    D) AUTHOR_NAME="$OPTARG" ;;
    n) REPEAT="$OPTARG" ;;
    h) usage; exit 0 ;;
    :) echo "Missing argument for -$OPTARG" >&2; usage; exit 2 ;;
    \?) echo "Invalid option: -$OPTARG" >&2; usage; exit 2 ;;
  esac
done

if [ -z "${URL:-}" ] || [ -z "${SECRET:-}" ] || [ -z "${PROJECT_ID:-}" ] || [ -z "${MR_IID:-}" ]; then
  echo "Missing required argument." >&2
  usage
  exit 2
fi

URL=$(normalize_listener_url "$URL")

if [ ${#ACTIONS[@]} -eq 0 ]; then
  ACTIONS=("open" "update")
fi

if [ -z "${PROJECT_PATH:-}" ]; then
  PROJECT_PATH="$PROJECT_ID"
fi

require_positive_integer "MR_IID" "$MR_IID"
require_positive_integer "REPEAT" "$REPEAT"

PROJECT_NAME="${PROJECT_PATH##*/}"
if [ -z "$PROJECT_NAME" ]; then
  PROJECT_NAME="$PROJECT_PATH"
fi

NAMESPACE_PATH=""
if [[ "$PROJECT_PATH" == *"/"* ]]; then
  NAMESPACE_PATH="${PROJECT_PATH%/*}"
fi

send_action() {
  local action="$1"
  local delivery_uuid
  delivery_uuid=$(generate_delivery_uuid)
  local timestamp
  timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)

  local payload
  payload=$(cat <<JSON
{"object_kind":"merge_request","event_type":"merge_request","user":{"id":42,"name":"$(json_escape "$AUTHOR_NAME")","username":"$(json_escape "$AUTHOR_USERNAME")"},"project":{"id":$(json_value "$PROJECT_ID"),"name":"$(json_escape "$PROJECT_NAME")","path_with_namespace":"$(json_escape "$PROJECT_PATH")","namespace":{"id":1,"name":"$(json_escape "${NAMESPACE_PATH:-$PROJECT_NAME}")","path":"$(json_escape "${NAMESPACE_PATH:-$PROJECT_NAME}")","kind":"group","full_path":"$(json_escape "${NAMESPACE_PATH:-$PROJECT_NAME}")"}},"object_attributes":{"id":$((100000 + MR_IID)),"iid":$MR_IID,"action":"$(json_escape "$action")","source_branch":"$(json_escape "$SOURCE_BRANCH")","target_branch":"$(json_escape "$TARGET_BRANCH")","last_commit":{"id":"$(json_escape "${action}-head-sha")"},"created_at":"$timestamp","updated_at":"$timestamp"}}
JSON
)

  printf "Sending merge request action '%s' -> %s\n" "$action" "$URL"

  printf '%s' "$payload" | \
    curl -sS -w '\nHTTP_STATUS:%{http_code}\n' -X POST "$URL" \
      -H 'Content-Type: application/json' \
      -H "X-Gitlab-Token: $SECRET" \
      -H 'X-Gitlab-Event: Merge Request Hook' \
      -H "X-Gitlab-Event-UUID: $delivery_uuid" \
      -H "Idempotency-Key: $delivery_uuid" \
      --data-binary @-
}

for action in "${ACTIONS[@]}"; do
  for ((i=0;i<REPEAT;i++)); do
    send_action "$action"
    echo
  done
done

exit 0
