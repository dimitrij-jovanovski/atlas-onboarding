#!/usr/bin/env bash
# Builds and starts all Atlas services on the host. Ctrl+C stops them all.
#
#   ./scripts/run.sh            SQL Server + Seq from /platform (docker compose up -d first)
#   ./scripts/run.sh --sqlite   no Docker needed: one SQLite file per market under .data/
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [[ "${1:-}" == "--sqlite" ]]; then
  export Database__Provider=Sqlite
  export Database__ConnectionStringTemplate="Data Source=$ROOT/.data/atlas_{market}.db"
  echo "Using SQLite in $ROOT/.data"
fi

dotnet build "$ROOT/Atlas.sln" -v q -nologo

pids=()
cleanup() { kill "${pids[@]}" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

start() { dotnet run --no-build --no-launch-profile --project "$ROOT/src/$1" & pids+=($!); }

start Atlas.FakeProviders
start Atlas.Onboarding.Api   # owns the schema: creates the per-market databases on startup

echo "Waiting for the onboarding API..."
for _ in $(seq 1 120); do
  curl -sf http://localhost:5100/health >/dev/null && break
  sleep 1
done

start Atlas.Verification.Worker
start Atlas.Backoffice.Api

cat <<EOF

  Onboarding API (mobile)   http://localhost:5100
  Back office (staff)       http://localhost:5200
  Fake IDNow / World-Check  http://localhost:5300
  Seq (logs)                http://localhost:5341   (only with docker compose)

  Try:  ./scripts/demo.sh      Ctrl+C to stop everything.

EOF
wait
