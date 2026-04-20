#!/usr/bin/env bash
set -euo pipefail

# send-ado-webhook.sh
# Sends one or more synthetic Azure DevOps webhook deliveries to a ProPR listener.
# Usage: send-ado-webhook.sh -u URL -s SECRET -r REPO_ID -i PR_ID [options]

usage() {
  cat <<EOF
Usage: $(basename "$0") -u URL -s SECRET -r REPO_ID -i PR_ID [options]

Required:
  -u URL        Listener URL (e.g. http://localhost:8080/webhooks/v1/providers/ado/<pathKey>)
  -s SECRET     Webhook secret (shared secret stored in ProPR)
  -r REPO_ID    Repository id or name (string)
  -i PR_ID      Pull request id (integer)

Options:
  -S SOURCE     Source ref (default: refs/heads/feature/test-branch)
  -T TARGET     Target ref (default: refs/heads/main)
  -E EVENT      Event type; can be provided multiple times.
                Defaults: git.pullrequest.created, git.pullrequest.updated, git.pullrequest.commented
  -U USERNAME   Basic auth username (default: propr)
  -k STATUS     Pull request status (default: active)
  -n N          Repeat each event N times (default: 1)
  -h            Show this help

Example:
  $(basename "$0") -u http://localhost:8080/webhooks/v1/providers/ado/ddfccfe1645e40c4a8e61c4516a11a74 \
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD -r meister-propr -i 24

EOF
}

SOURCE="refs/heads/feature/test-branch"
TARGET="refs/heads/main"
USERNAME="propr"
PR_STATUS="active"
REPEAT=1
EVENTS=()

json_escape() {
  local value="$1"
  value=${value//\\/\\\\}
  value=${value//\"/\\\"}
  value=${value//$'\n'/\\n}
  value=${value//$'\r'/\\r}
  value=${value//$'\t'/\\t}
  printf '%s' "$value"
}

normalize_listener_url() {
  local value="$1"

  if [[ "$value" == *"/webhooks/v1/ado/"* && "$value" != *"/webhooks/v1/providers/"* ]]; then
    value=${value//\/webhooks\/v1\/ado\//\/webhooks\/v1\/providers\/ado\/}
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

while getopts ":u:s:r:i:S:T:E:U:k:n:h" opt; do
  case "$opt" in
    u) URL="$OPTARG" ;; 
    s) SECRET="$OPTARG" ;; 
    r) REPO_ID="$OPTARG" ;; 
    i) PR_ID="$OPTARG" ;; 
    S) SOURCE="$OPTARG" ;; 
    T) TARGET="$OPTARG" ;; 
    E) EVENTS+=("$OPTARG") ;; 
    U) USERNAME="$OPTARG" ;; 
    k) PR_STATUS="$OPTARG" ;; 
    n) REPEAT="$OPTARG" ;; 
    h) usage; exit 0 ;; 
    :) echo "Missing argument for -$OPTARG" >&2; usage; exit 2 ;; 
    \?) echo "Invalid option: -$OPTARG" >&2; usage; exit 2 ;;
  esac
done

if [ -z "${URL:-}" ] || [ -z "${SECRET:-}" ] || [ -z "${REPO_ID:-}" ] || [ -z "${PR_ID:-}" ]; then
  echo "Missing required argument." >&2
  usage
  exit 2
fi

URL=$(normalize_listener_url "$URL")

if [ ${#EVENTS[@]} -eq 0 ]; then
  EVENTS=("git.pullrequest.created" "git.pullrequest.updated" "git.pullrequest.commented")
fi

require_positive_integer "PR_ID" "$PR_ID"
require_positive_integer "REPEAT" "$REPEAT"

AUTH_B64=$(printf '%s' "$USERNAME:$SECRET" | base64 | tr -d '\n')

send_event() {
  local event="$1"

  local payload
  payload=$(cat <<JSON
{"eventType":"$(json_escape "$event")","resource":{"repository":{"id":"$(json_escape "$REPO_ID")"},"pullRequestId":$PR_ID,"sourceRefName":"$(json_escape "$SOURCE")","targetRefName":"$(json_escape "$TARGET")","status":"$(json_escape "$PR_STATUS")","reviewers":[]}}
JSON
)

  printf "Sending event '%s' -> %s\n" "$event" "$URL"

  # Send payload and print response with HTTP status
  printf '%s' "$payload" | \
    curl -sS -w '\nHTTP_STATUS:%{http_code}\n' -X POST "$URL" \
      -H 'Content-Type: application/json' \
      -H "Authorization: Basic $AUTH_B64" \
      --data-binary @-
}

for event in "${EVENTS[@]}"; do
  for ((i=0;i<REPEAT;i++)); do
    send_event "$event"
    echo
  done
done

exit 0
