#!/usr/bin/env bash
set -euo pipefail

DB_CONNECTION_STRING="${1:-${DB_CONNECTION_STRING:-}}"
BACKEND_PORT="${2:-8080}"

if [ -z "$DB_CONNECTION_STRING" ]; then
  echo "Usage: $0 '<DB_CONNECTION_STRING>' [backend-port]"
  exit 1
fi

export DB_CONNECTION_STRING
export ASPNETCORE_URLS="http://localhost:${BACKEND_PORT}"
export ASPNETCORE_ENVIRONMENT="Development"
export LOKI_URL=""

# Start API (background)
cd src/MeisterProPR.Api
dotnet run --no-launch-profile &
API_PID=$!
cd - >/dev/null

# Start admin UI (install deps if missing) and run (background)
cd admin-ui
if [ ! -d node_modules ]; then
  npm ci
fi
npm run dev &
UI_PID=$!
cd - >/dev/null

echo "API PID: $API_PID  Admin UI PID: $UI_PID"
echo "API -> http://localhost:${BACKEND_PORT}  Admin UI -> http://localhost:5173"
wait
