#!/usr/bin/env bash
set -euo pipefail

if rg -n 'DateTime(\.UtcNow|\.Now)|DateTimeOffset(\.UtcNow|\.Now)' api --glob '*.cs'; then
  echo 'API code must obtain the current time through NodaTime IClock.' >&2
  exit 1
fi
