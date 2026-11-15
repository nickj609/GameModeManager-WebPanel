#!/usr/bin/env bash
# A busy-looking dev panel, to see how it looks with several servers: adds demo servers to the dev data folder (once),
# then keeps a FakeAgent connected as each, every one with its own mode, map and hostname. Run it in the background next to
# the dev panel (gmm-panel-dev). Dev data only: src/GmmPanel/bin/Debug/net10.0/data.
#
#   tools/demo.sh
set -u
cd "$(dirname "$0")/.."
PANEL_URL="${PANEL_URL:-ws://127.0.0.1:5085/agent}"
DATA=src/GmmPanel/bin/Debug/net10.0/data
CLI=src/GmmPanel/bin/Debug/net10.0/gmmpanel.exe
dotnet build tools/FakeAgent/FakeAgent.csproj -c Debug -v q >/dev/null || exit 1

# name | mode | map | hostname | max players
SERVERS=(
  "Production|Casual|de_dust2|Example | Casual|32"
  "Test|Competitive|de_golden|Example | Test|10"
  "Surf|Surf|surf_kitsune|Example | Surf & Bhop|24"
  "Arena|1v1 Arena|aim_map|Example | 1v1 Arenas|16"
  "Deathmatch|Deathmatch|de_palacio|Example | FFA Deathmatch|20"
  "Retakes|Retakes|cs_alpine|Example | Retakes|10"
)

for entry in "${SERVERS[@]}"; do
  IFS='|' read -r name mode map host1 host2 max <<< "$entry"
  host="$host1|$host2"
  file="$DATA/dev-token-$name.txt"
  if [ ! -s "$file" ]; then
    (cd "$(dirname "$CLI")" && ./gmmpanel.exe token new "$name") | grep -E '^[A-Za-z0-9_-]{40,}$' | tail -1 > "$file"
  fi
  (
    while true; do
      dotnet run --project tools/FakeAgent --no-build -- "$PANEL_URL" "$(tr -d '\r\n' < "$file")" "$name" "$mode" "$map" "$host" "$max" >/dev/null 2>&1
      sleep 3
    done
  ) &
done
wait
