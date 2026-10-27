#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
backend="$project_root/ASTRA-BACKEND/Astra_backend"
log_dir="$project_root/.run"
mkdir -p "$log_dir"

if [[ -f "$backend/runtime.env" ]]; then
    # runtime.env is a private, locally maintained shell configuration file.
    source "$backend/runtime.env"
fi
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export APP_BASE_URL="${APP_BASE_URL:-http://localhost:5174}"

pids=()
cleanup() {
    for pid in "${pids[@]}"; do
        kill "$pid" 2>/dev/null || true
    done
    wait 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

while IFS=$'\t' read -r module port; do
    assembly="$backend/$module.API/bin/Debug/net10.0/$module.API.dll"
    if [[ ! -f "$assembly" ]]; then
        printf 'Build the solution first: dotnet build %s/Astra.slnx\n' "$backend" >&2
        exit 1
    fi
    (
        cd "$backend/$module.API"
        export ASPNETCORE_URLS="http://127.0.0.1:$port"
        exec dotnet "$assembly"
    ) > "$log_dir/$module.log" 2>&1 &
    pids+=("$!")
    printf '%-28s http://127.0.0.1:%s/swagger\n' "$module" "$port"
done < <(node - "$backend/moduleManifest.json" <<'JS'
const fs = require('node:fs');
for (const module of JSON.parse(fs.readFileSync(process.argv[2], 'utf8'))) {
    console.log(`${module.name}\t${module.port}`);
}
JS
)

printf '\nLogs: %s\nKeep this terminal open. Ctrl+C stops the processes started here.\n' "$log_dir"
wait -n
