#!/usr/bin/env bash
# Runs a command inside the toolchain container (docker/dev.Dockerfile).
# For hosts that have Docker but no .NET SDK or C++ compiler.
#
#   scripts/dev-exec.sh up                 start container + PostgreSQL
#   scripts/dev-exec.sh "<shell command>"  sync sources into the container and run the command in /work
#   scripts/dev-exec.sh pull <path>        copy <path> from the container back to the host tree
#   scripts/dev-exec.sh down               remove the containers
#
# Sources are copied into a container volume before each command because
# building on a bind-mounted Windows path is very slow.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
net=aegis-dev-net
dev=aegis-dev
pg=aegis-dev-pg

case "${1:-}" in
  up)
    docker network inspect "$net" >/dev/null 2>&1 || docker network create "$net" >/dev/null
    docker image inspect aegis-dev:latest >/dev/null 2>&1 || docker build -t aegis-dev:latest -f "$root/docker/dev.Dockerfile" "$root/docker"
    docker container inspect "$pg" >/dev/null 2>&1 || docker run -d --name "$pg" --network "$net" \
      -e POSTGRES_USER=aegis -e POSTGRES_PASSWORD=aegis-dev-only -e POSTGRES_DB=aegisbank \
      postgres:17-alpine -c max_connections=300 >/dev/null
    docker container inspect "$dev" >/dev/null 2>&1 || docker run -d --name "$dev" --network "$net" \
      -v "$root":/src -v aegis-work:/work -v aegis-nuget:/root/.nuget \
      -e AEGIS_TEST_DB="Host=$pg;Username=aegis;Password=aegis-dev-only" \
      -e AEGIS_DESIGN_DB="Host=$pg;Database=aegisbank;Username=aegis;Password=aegis-dev-only" \
      aegis-dev:latest sleep infinity >/dev/null
    echo "toolchain container '$dev' and database '$pg' are running"
    ;;
  down)
    docker rm -f "$dev" "$pg" >/dev/null 2>&1 || true
    ;;
  pull)
    docker exec "$dev" bash -c "mkdir -p \"/src/$(dirname "$2")\" && rsync -a \"/work/$2\" \"/src/$(dirname "$2")/\""
    ;;
  "")
    echo "usage: $0 up | down | pull <path> | \"<command>\"" >&2; exit 2
    ;;
  *)
    docker exec "$dev" bash -c "rsync -a --delete \
        --exclude bin/ --exclude obj/ --exclude build/ --exclude artifacts/ --exclude .git/ \
        --exclude BenchmarkDotNet.Artifacts/ --exclude TestResults/ /src/ /work/ \
      && find /work/scripts -name '*.sh' -exec sed -i 's/\r\$//' {} + \
      && cd /work && $1"
    ;;
esac
