#!/usr/bin/env sh
set -eu

output=${1:-infra/k6/artifacts/benchmark-k6-fixture.json}
mkdir -p "$(dirname "$output")"
dotnet run --project api -- --write-benchmark-k6-fixture "$output"
echo "Wrote $output. It contains deterministic synthetic provider/service/slot IDs only."
