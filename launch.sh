#!/usr/bin/env bash
# Dev helper: start Schedule I (if not running), wait for the dev host, load the test save slot.
SLOT="${1:-5}"
X="http://localhost:7901"
if ! curl -s -m 3 "$X/ping" >/dev/null 2>&1; then
  powershell.exe -NoProfile -Command "Start-Process 'steam://rungameid/3164500'" >/dev/null 2>&1
  for i in $(seq 1 60); do
    r=$(curl -s -m 3 "$X/ping" 2>/dev/null)
    case "$r" in *"scene='Menu'"*) break;; esac
    sleep 3
  done
fi
echo "ping: $(curl -s -m 3 "$X/ping")"
s=$(curl -s -m 10 "$X/x?cmd=status")
case "$s" in
  *"loaded=True"*) echo "already in game: $s"; exit 0;;
esac
sleep 2
echo "load: $(curl -s -m 35 "$X/x?cmd=loadsave&slot=$SLOT")"
for i in $(seq 1 60); do
  s=$(curl -s -m 5 "$X/x?cmd=status&timeout=4000")
  case "$s" in *"loaded=True"*) echo "in game: $s"; exit 0;; esac
  sleep 3
done
echo "did not finish loading: $s"
exit 1
