#!/usr/bin/env bash
set -euo pipefail

# send-codeberg-webhook.sh
# Sends one or more synthetic Forgejo/Codeberg pull request webhook deliveries to a ProPR listener.
# Usage: send-codeberg-webhook.sh -u URL -s SECRET -r REPO_ID -i PR_NUMBER [options]

usage() {
  cat <<EOF
Usage: $(basename "$0") -u URL -s SECRET -r REPO_ID -i PR_NUMBER [options]

Required:
  -u URL        Listener URL (e.g. http://localhost:8080/webhooks/v1/providers/forgejo/<pathKey>)
  -s SECRET     Forgejo/Codeberg webhook secret (must match the ProPR webhook secret)
  -r REPO_ID    Repository id used by Forgejo APIs. When omitted as a numeric id and provided
                as owner/repo, the value is reused as a best-effort fallback external id.
  -i PR_NUMBER  Pull request number (integer)

Options:
  -O OWNER      Repository owner/namespace (default: acme)
  -P PATH       Repository path_with_namespace (for example: local_admin/propr).
                Recommended whenever -r is a numeric repository id.
  -a ACTION     Pull request action; can be provided multiple times.
                Defaults: opened, review_requested
                Add closed explicitly when you want to test lifecycle cancellation.
  -S SOURCE     Source branch (default: feature/test-branch)
  -T TARGET     Target branch (default: main)
  -R REVIEWER   Requested reviewer login for review_requested events (default: meister-review-bot)
  -M            Mark closed events as merged (default: false)
  -n N          Repeat each action N times (default: 1)
  -h            Show this help

Example:
  $(basename "$0") -u http://localhost:8080/webhooks/v1/providers/forgejo/ddfccfe1645e40c4a8e61c4516a11a74 \
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD \
    -r 101 -P acme/propr -i 24

Path-only fallback example:
  $(basename "$0") -u http://localhost:8080/webhooks/v1/providers/forgejo/ddfccfe1645e40c4a8e61c4516a11a74 \
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD \
    -r local_admin/propr -i 24

EOF
}

REPO_ID=""
OWNER="acme"
PROJECT_PATH=""
REPO_NAME=""
PR_NUMBER=""
SOURCE_BRANCH="feature/test-branch"
TARGET_BRANCH="main"
REVIEWER_LOGIN="meister-review-bot"
MARK_MERGED=false
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

normalize_listener_url() {
  local value="$1"

  if [[ "$value" == *"/webhooks/v1/codeberg/"* && "$value" != *"/webhooks/v1/providers/"* ]]; then
    value=${value//\/webhooks\/v1\/codeberg\//\/webhooks\/v1\/providers\/forgejo\/}
  fi

  if [[ "$value" == *"/webhooks/v1/forgejo/"* && "$value" != *"/webhooks/v1/providers/"* ]]; then
    value=${value//\/webhooks\/v1\/forgejo\//\/webhooks\/v1\/providers\/forgejo\/}
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

compute_signature() {
  local secret="$1"
  local payload="$2"

  if command -v openssl >/dev/null 2>&1; then
    printf '%s' "$payload" | openssl dgst -sha256 -hmac "$secret" -binary | od -An -tx1 -v | tr -d ' \n'
    return
  fi

  echo "openssl is required to compute Forgejo webhook signatures." >&2
  exit 2
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

while getopts ":u:s:r:i:O:P:a:S:T:R:Mn:h" opt; do
  case "$opt" in
    u) URL="$OPTARG" ;;
    s) SECRET="$OPTARG" ;;
    r) REPO_ID="$OPTARG" ;;
    i) PR_NUMBER="$OPTARG" ;;
    O) OWNER="$OPTARG" ;;
    P) PROJECT_PATH="$OPTARG" ;;
    a) ACTIONS+=("$OPTARG") ;;
    S) SOURCE_BRANCH="$OPTARG" ;;
    T) TARGET_BRANCH="$OPTARG" ;;
    R) REVIEWER_LOGIN="$OPTARG" ;;
    M) MARK_MERGED=true ;;
    n) REPEAT="$OPTARG" ;;
    h) usage; exit 0 ;;
    :) echo "Missing argument for -$OPTARG" >&2; usage; exit 2 ;;
    \?) echo "Invalid option: -$OPTARG" >&2; usage; exit 2 ;;
  esac
done

if [ -z "${URL:-}" ] || [ -z "${SECRET:-}" ] || [ -z "${REPO_ID:-}" ] || [ -z "${PR_NUMBER:-}" ]; then
  echo "Missing required argument." >&2
  usage
  exit 2
fi

URL=$(normalize_listener_url "$URL")

if [ ${#ACTIONS[@]} -eq 0 ]; then
  ACTIONS=("opened" "review_requested")
fi

require_positive_integer "PR_NUMBER" "$PR_NUMBER"
require_positive_integer "REPEAT" "$REPEAT"

normalize_repository_identity() {
  if [ -n "$PROJECT_PATH" ]; then
    PROJECT_PATH=${PROJECT_PATH#/}
    PROJECT_PATH=${PROJECT_PATH%/}
  elif [[ "$REPO_ID" == */* ]]; then
    PROJECT_PATH="$REPO_ID"
  else
    PROJECT_PATH="$OWNER/$REPO_ID"
  fi

  if [[ "$PROJECT_PATH" != */* ]]; then
    echo "PROJECT_PATH must be an owner/repository path. Use -P owner/repo when -r is a numeric repository id." >&2
    exit 2
  fi

  OWNER=${PROJECT_PATH%/*}
  REPO_NAME=${PROJECT_PATH##*/}

  if [ -z "$OWNER" ] || [ -z "$REPO_NAME" ]; then
    echo "Repository path must include both owner and repository name." >&2
    exit 2
  fi
}

normalize_repository_identity

json_repository_id() {
  local value="$1"

  if [[ "$value" =~ ^[0-9]+$ ]]; then
    printf '%s' "$value"
    return
  fi

  printf '"%s"' "$(json_escape "$value")"
}

send_action() {
  local action="$1"
  local delivery_uuid
  delivery_uuid=$(generate_delivery_uuid)
  local payload
  local merged_value="false"
  if $MARK_MERGED && [ "$action" = "closed" ]; then
    merged_value="true"
  fi

  local requester=""
  if [ "$action" = "review_requested" ]; then
    requester=",\"requested_reviewer\":{\"id\":99,\"login\":\"$(json_escape "$REVIEWER_LOGIN")\",\"type\":\"Bot\"}"
  fi

  local state="open"
  if [ "$action" = "closed" ]; then
    state="closed"
  fi

  payload=$(cat <<JSON
{"action":"$(json_escape "$action")","repository":{"id":$(json_repository_id "$REPO_ID"),"full_name":"$(json_escape "$PROJECT_PATH")","owner":{"login":"$(json_escape "$OWNER")"},"name":"$(json_escape "$REPO_NAME")"},"pull_request":{"id":$((100000 + PR_NUMBER)),"number":$PR_NUMBER,"state":"$state","merged":$merged_value,"head":{"ref":"$(json_escape "$SOURCE_BRANCH")","sha":"$(json_escape "${action}-head-sha")"},"base":{"ref":"$(json_escape "$TARGET_BRANCH")","sha":"base-sha"}}$requester,"sender":{"id":7,"login":"octocat","type":"User"}}
JSON
)

  local signature
  signature=$(compute_signature "$SECRET" "$payload")

  printf "Sending Forgejo action '%s' -> %s\n" "$action" "$URL"

  printf '%s' "$payload" | \
    curl -sS -w '\nHTTP_STATUS:%{http_code}\n' -X POST "$URL" \
      -H 'Content-Type: application/json' \
      -H "X-Gitea-Signature: $signature" \
      -H 'X-Gitea-Event: pull_request' \
      -H "X-Gitea-Delivery: $delivery_uuid" \
      --data-binary @-
}

for action in "${ACTIONS[@]}"; do
  for ((i=0;i<REPEAT;i++)); do
    send_action "$action"
    echo
  done
done

exit 0
