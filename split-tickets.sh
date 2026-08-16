#!/usr/bin/env bash
# Regenerates tickets/*.md from TICKETS.md (the source of truth). Never hand-edit tickets/.
set -euo pipefail
cd "$(dirname "$0")"
rm -f tickets/*.md
mkdir -p tickets
awk '
  /^### (E[0-9]+-T[0-9]+) / { id = $2; out = "tickets/" id ".md"; print > out; next }
  /^(##[^#]|---)/ { out = "" }
  out != "" { print > out }
' TICKETS.md
echo "Wrote $(ls tickets | wc -l | tr -d ' ') ticket files."
