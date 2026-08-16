#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet format "$script_dir/../PatientDataPortal.Api.csproj" --verify-no-changes --no-restore
