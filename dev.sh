#!/usr/bin/env bash
# Build the logic assembly under a unique assembly name (MelonLoader rejects reloading the
# same identity), copy it to the fixed path the dev host reads, and ask the host to reload.
set -u
GAME="${SCHEDULE1_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Schedule I}"
# the dev host only answers requests that carry its per-session secret
tok() { cat "$GAME/UserData/SupplyRunDev.token" 2>/dev/null; }
dcurl() { curl -H "X-Dev-Token: $(tok)" "$@"; }
cd "$(dirname "$0")/Logic" || exit 1
stamp=$(date +%H%M%S)
rm -f bin/SupplyRunLogic_*.dll
extra=""
# "tools" builds only the inspection/test commands, for use next to the release SupplyRun.dll
if [ "${1:-}" = "tools" ] || [ "${2:-}" = "tools" ]; then extra="-p:ToolsOnly=true"; fi
out=$(dotnet build -c Release --no-incremental -p:AssemblyName=SupplyRunLogic_$stamp $extra 2>&1)
if ! echo "$out" | grep -q " 0 Error(s)"; then
  echo "$out" | grep -E " error " | sed -E 's/\[D:.*//' | sort -u | head -40
  echo "BUILD FAILED"
  exit 1
fi
cp "bin/SupplyRunLogic_$stamp.dll" bin/SupplyRunLogic.dll
echo "built SupplyRunLogic_$stamp"
if [ "${1:-}" != "noreload" ] && [ "${2:-}" != "noreload" ]; then
  dcurl -s -m 40 "http://localhost:7901/reload"
  echo
fi
