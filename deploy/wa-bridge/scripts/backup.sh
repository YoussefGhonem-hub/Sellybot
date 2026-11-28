#!/bin/bash
# Daily (cron.daily): every number's WhatsApp login + the list of numbers, kept 7 days in /var/backups/wa-bridge (root only).
# Restore: stop the bridge, untar into /var/lib/wa-bridge, chown -R wabridge:wabridge, start it. See SETUP.md.
set -euo pipefail
DATA=/var/lib/wa-bridge
OUT=/var/backups/wa-bridge
[ -d "$DATA" ] || exit 0
install -d -m 0700 "$OUT"
umask 077
tar -czf "$OUT/wa-bridge-$(date +%F).tar.gz" -C "$DATA" --exclude=media .
find "$OUT" -name 'wa-bridge-*.tar.gz' -mtime +6 -delete
