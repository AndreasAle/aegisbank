#!/usr/bin/env bash
# Waits until the compose services `api` and `dashboard` report healthy.
# Usage: scripts/wait-healthy.sh [timeout-seconds]
set -euo pipefail
timeout="${1:-180}"
deadline=$((SECONDS + timeout))
while (( SECONDS < deadline )); do
  healthy=$(docker compose ps --format '{{.Service}}={{.Health}}' | grep -cE '^(api|dashboard)=healthy$' || true)
  if [[ "$healthy" == "2" ]]; then
    docker compose ps --format '{{.Service}}  {{.Status}}  {{.Ports}}'
    exit 0
  fi
  sleep 3
done
echo "services did not become healthy within ${timeout}s" >&2
docker compose ps
docker compose logs --tail 40 api dashboard
exit 1
