#!/bin/bash
# The one command that updates the bridge on its machine:  sudo wa-bridge-update [git ref, default main]
# Pulls deploy/wa-bridge from the repo, installs exactly what package-lock.json says, and reloads the one pm2 process.
# Every number reconnects by itself with its saved login (no new QR).
set -euo pipefail
REF="${1:-main}"
APP=/opt/wa-bridge/app
U=wabridge
as() { sudo -u "$U" -H "$@"; }

as git -C "$APP" fetch -q --depth 1 origin "$REF"
as git -C "$APP" checkout -q --force FETCH_HEAD
echo "code: $(as git -C "$APP" log -1 --format='%h %s')"
cd "$APP/deploy/wa-bridge"
as npm ci --omit=dev --no-audit --no-fund --loglevel=error
as pm2 startOrReload ecosystem.config.cjs --update-env >/dev/null
as pm2 save >/dev/null
for i in $(seq 1 20); do
  if curl -fsS http://127.0.0.1:3100/health >/dev/null 2>&1; then echo "bridge up: $(curl -fsS http://127.0.0.1:3100/health)"; exit 0; fi
  sleep 1
done
echo "the bridge did not answer on 127.0.0.1:3100 — look at: sudo -u $U -H pm2 logs wa-bridge --lines 50" >&2
exit 1
