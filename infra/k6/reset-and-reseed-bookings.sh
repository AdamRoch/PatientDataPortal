#!/usr/bin/env sh
set -eu

: "${DATABASE_URL:?DATABASE_URL must point to the dedicated synthetic benchmark environment}"
dotnet run --project api -- --reset-benchmark-bookings
dotnet run --project api -- --seed-benchmark
echo "Benchmark bookings reset and deterministic slots re-seeded."
