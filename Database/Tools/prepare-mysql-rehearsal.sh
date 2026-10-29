#!/usr/bin/env bash
set -euo pipefail
astra_backend_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
astra_settings_root="${1:-$astra_backend_root}"
if [[ $# -eq 0 && -f "$astra_backend_root/../../.env" ]]; then
  astra_settings_root="$(cd -- "$astra_backend_root/../.." && pwd)"
fi
astra_dotnet="${ASTRA_DOTNET:-dotnet}"
if ! command -v "$astra_dotnet" >/dev/null 2>&1; then
  echo 'Install the .NET 10 SDK or set ASTRA_DOTNET to its executable path.' >&2
  exit 1
fi
# Connect to the configured test server; never install, start, or stop database services.
exec "$astra_dotnet" run --project "$astra_backend_root/Database/Tools/SchemaRehearsal/SchemaRehearsal.csproj" -- "$astra_settings_root"
