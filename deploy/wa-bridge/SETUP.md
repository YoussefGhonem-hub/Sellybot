# Sellybot WhatsApp bridge — setup and operations

Sellybot's own replacement for Wapilot: the "linked device" part of WhatsApp, run by us with
[Baileys](https://github.com/WhiskeySockets/Baileys) (MIT licence, no browser). One Node.js process holds one WhatsApp
connection per number ("instance"). Sellybot talks to it over HTTPS with a shared secret, and it posts every message back
to Sellybot, signed with the same secret.

```
store owner's phone ──(WhatsApp «الأجهزة المرتبطة», QR)──┐
customers ──────────── WhatsApp servers ◀──────────────── bridge (Node, Baileys)  127.0.0.1:3100
                                                              ▲   │  signed webhook: https://sellybot.online/api/wa-webhook.ashx?src=bridge
                       Caddy (HTTPS, wa.sellybot.online) ─────┘   ▼
Sellybot (SmarterASP) ──── X-Bridge-Secret ──▶ https://wa.sellybot.online/instances/...
```

> **Risk, said plainly:** linking a number as a "linked device" through an unofficial library (Baileys, like Wapilot before it)
> is against WhatsApp's terms of service, and WhatsApp can ban a number that does it. The safe alternative is the official
> WhatsApp Business Cloud API (Meta), which needs a verified business and per-conversation fees. The bridge lowers the risk
> (one message at a time, small random gaps, "typing…" first, per-minute and per-hour caps, no sending to numbers that are
> not on WhatsApp, never marking itself "online"), but it can't remove it.

## 1. The machine

| | |
|---|---|
| Provider | Oracle Cloud, Always Free |
| Name | `wa-bridge`, region `me-dubai-1` (UAE East, Dubai), AD-1 |
| Network | VCN `vcn-20261128-0829`, public subnet |
| Shape | `VM.Standard.E2.1.Micro`: 1 OCPU, 1 GB RAM, AMD x86_64 (`VM.Standard.A1.Flex` was "Out of capacity" in Dubai) |
| OS | Canonical Ubuntu 24.04.5, user `ubuntu`, 45 GB disk |
| Public IP | `84.235.245.88` — **ephemeral**: it changes if the VM is stopped and started (see §9) |
| DNS | `A wa.sellybot.online → 84.235.245.88`, Cloudflare **DNS only** (grey cloud), so Caddy gets its own certificate |
| Firewall | Oracle Security List: ingress TCP 22, 80, 443 from 0.0.0.0/0; plus iptables on the VM (step 3 below) |

## 2. One-time setup

Everything below is in `scripts/server-setup.sh` (idempotent: safe to run again). It was run like this:

```bash
ssh -i <key> ubuntu@84.235.245.88
curl -fsSL https://raw.githubusercontent.com/YoussefGhonem-hub/Sellybot/main/deploy/wa-bridge/scripts/server-setup.sh -o /tmp/server-setup.sh
sudo bash /tmp/server-setup.sh          # REF=<branch> to set up from a branch
```

What it does, step by step (to do it by hand, copy the commands from the script):

1. **System update + unattended security updates**: `apt-get upgrade`, `unattended-upgrades` on (daily lists and upgrades,
   `/etc/apt/apt.conf.d/20auto-upgrades`), also for the NodeSource repository (`52unattended-nodesource`).
2. **2 GB swap file** `/swapfile` (in `/etc/fstab`), `vm.swappiness=10` (`/etc/sysctl.d/90-wa-bridge.conf`).
3. **iptables**: Oracle's Ubuntu image rejects everything but 22 even when the Security List allows more. The script inserts
   `ACCEPT` rules for TCP 80 and 443 before the `REJECT` rule of `INPUT`, and saves them with `netfilter-persistent save`
   (`/etc/iptables/rules.v4`), so they survive a reboot.
4. **Node.js LTS 24.x** from NodeSource's apt repository (signed key in `/etc/apt/keyrings/nodesource.gpg`).
5. **Caddy** from its official apt repository. `/etc/caddy/Caddyfile` = `server/Caddyfile`: HTTPS for `wa.sellybot.online`
   (Let's Encrypt, renewed by Caddy), HSTS, `reverse_proxy 127.0.0.1:3100`, 20 MB body limit, admin API off.
6. **A system user `wabridge`** (no login shell). Folders: `/opt/wa-bridge` (code + `.env`, 0750), `/var/lib/wa-bridge`
   (every number's login, 0700), `/var/backups/wa-bridge` (root only).
7. **The code**: a sparse, shallow clone of the public repo with only `deploy/wa-bridge`, in `/opt/wa-bridge/app`.
8. **`/opt/wa-bridge/.env`**, made once with a fresh 48-character secret (`openssl rand`), mode 600, owner `wabridge`.
   It is never printed by the script and never committed. See §4 for its keys.
9. **pm2 7.1.2** (global), started on boot by systemd (`pm2-wabridge.service`, `pm2 startup systemd -u wabridge`), with
   `pm2-logrotate` (10 MB per file, 7 kept, compressed). `ecosystem.config.cjs`: one process `wa-bridge`, restarted if it
   dies or passes 700 MB, 8 s to close cleanly on a reload.
10. **Daily backup** `/etc/cron.daily/wa-bridge-backup` (= `scripts/backup.sh`), see §6.
11. **The update command** `/usr/local/bin/wa-bridge-update` → `scripts/update.sh`, see §5.
12. **First start** with that command.

After the setup: `https://wa.sellybot.online/health` answers `{"ok":true,"service":"sellybot-wa-bridge"}` from the internet.

## 3. Sellybot's side

In the admin page, **«سيرفر الواتساب»**: the address (`https://wa.sellybot.online`) and the secret from `/opt/wa-bridge/.env`
(`BRIDGE_SECRET`). Sellybot keeps them in its database (`AppSettings`: `WaBridgeUrl`, `WaBridgeSecret` encrypted with
`Secrets.Protect`), never in the repo. **«جرّب الاتصال»** checks three things and says each in plain Arabic:
the server answers; the secret is right; the server can post to Sellybot's webhook and Sellybot accepts the signature.

`AppSettings.WaProvider` = `bridge` (default) or `wapilot` (puts the old Wapilot account back; its code is kept, unused).

To read the secret on the server (to paste it once): `sudo grep ^BRIDGE_SECRET= /opt/wa-bridge/.env`.

## 4. `.env` (on the server only: `/opt/wa-bridge/.env`)

| key | value |
|---|---|
| `BRIDGE_SECRET` | the shared secret (48 letters/digits). Change it here **and** in «سيرفر الواتساب», then `pm2 reload` |
| `WEBHOOK_URL` | `https://sellybot.online/api/wa-webhook.ashx?src=bridge` |
| `PUBLIC_URL` | `https://wa.sellybot.online` (the signed links to pictures customers send) |
| `DATA_DIR` | `/var/lib/wa-bridge` |
| `HOST` / `PORT` | `127.0.0.1` / `3100` (never open the Node port to the internet; Caddy is in front) |
| `MAX_INSTANCES` | how many numbers this server takes (15 on the 1 GB machine, see §7) |
| optional | `SEND_PER_MINUTE` (20), `SEND_PER_HOUR` (400), `SEND_GAP_MIN_MS` (1200), `SEND_GAP_MAX_MS` (3500), `SEND_MAX_QUEUE` (300), `BAILEYS_LOG` (error), `LOG_LEVEL` (info) |

## 5. Everyday commands

```bash
sudo wa-bridge-update              # pull deploy/wa-bridge from main, npm ci, pm2 reload; every number reconnects, no QR
sudo wa-bridge-update <branch>     # the same from a branch
sudo -u wabridge -H pm2 ls         # is it running, memory, restarts
sudo -u wabridge -H pm2 logs wa-bridge --lines 100
sudo -u wabridge -H pm2 reload wa-bridge
curl -s http://127.0.0.1:3100/health
sudo systemctl status caddy pm2-wabridge
```

A reboot (`sudo reboot`) brings everything back by itself: systemd starts Caddy and pm2, pm2 starts the bridge, the bridge
reconnects every linked number from its saved login (checked on 2026-11-28).

## 6. Backups and restore

Every day `/etc/cron.daily/wa-bridge-backup` writes `/var/backups/wa-bridge/wa-bridge-YYYY-MM-DD.tar.gz` (every number's
login + `instances.json`, without the cached pictures) and keeps 7 days. Restore (or move, §8):

```bash
sudo -u wabridge -H pm2 stop wa-bridge
sudo tar -xzf /var/backups/wa-bridge/wa-bridge-YYYY-MM-DD.tar.gz -C /var/lib/wa-bridge
sudo chown -R wabridge:wabridge /var/lib/wa-bridge
sudo -u wabridge -H pm2 start wa-bridge
```

A login restored from an old backup may be refused by WhatsApp if the phone changed it since; that number then shows
«اتفصل من الموبايل» and the owner scans again.

## 7. Capacity (measured on this machine, 2026-11-28)

| | RSS of the bridge |
|---|---|
| idle (Node + Baileys loaded, no number) | 101 MB |
| 1 number showing its QR | 119 MB |
| 6 numbers showing their QR | 126 MB (≈ 1–4 MB each) |

The machine had 574 MB available with 6 connections open, plus 2 GB of swap. A **linked** number keeps more (its encryption
keys and sessions with the customers it talks to); a safe budget is 25–30 MB each, so this 1 GB box holds about **15
numbers** comfortably. `MAX_INSTANCES=15` makes the server refuse a 16th (the owner is told plainly, the admin gets a
ticket). Watch `pm2 ls` and the RAM line in «سيرفر الواتساب»; on `VM.Standard.A1.Flex` (§8) raise it to 100+.

## 8. Moving to `VM.Standard.A1.Flex` (up to 4 OCPU / 24 GB, free) when Dubai has capacity

Re-trying A1 in the Oracle console is the owner's job. Once a new A1 VM (Ubuntu 24.04, **Arm**) exists:

1. Open TCP 80/443 in its Security List (like the old one).
2. On the new VM: `sudo bash server-setup.sh` (Node, Caddy and the npm packages all have arm64 builds). It makes a **new**
   secret; replace it with the old one so Sellybot keeps working: copy `BRIDGE_SECRET` from the old `/opt/wa-bridge/.env`
   into the new one (or paste the new one in «سيرفر الواتساب»).
3. Stop the old bridge so two copies never use the same logins: `sudo -u wabridge -H pm2 stop wa-bridge` (old VM).
4. Copy the logins: on the old VM `sudo tar -czf /tmp/wa.tgz -C /var/lib/wa-bridge --exclude=media .`, copy it over
   (`scp`), on the new VM stop the bridge, `sudo tar -xzf wa.tgz -C /var/lib/wa-bridge`, `sudo chown -R wabridge:wabridge /var/lib/wa-bridge`, start it.
5. Change the DNS record `wa.sellybot.online` to the new IP (Cloudflare, DNS only). Caddy gets the certificate by itself
   within a minute of the record resolving.
6. In «سيرفر الواتساب» press «جرّب الاتصال». Every number should be «مربوط ✅» without a new QR.
7. Raise `MAX_INSTANCES` in the new `.env`, `pm2 reload wa-bridge`.

## 9. If the public IP changes

The IP is ephemeral: stopping and starting the VM in the console gives a new one (a reboot from inside keeps it). Then:
update the `A` record `wa.sellybot.online` in Cloudflare (DNS only) to the new IP, wait a minute, press «جرّب الاتصال».
Nothing changes on the VM. (A reserved public IP in Oracle avoids this; it is free while attached.)

## 10. Monitoring

Sellybot checks `/health` and every number every 3 minutes (a lane of its background jobs, `WaBridge.Watch`), and the bridge
also posts `session.status` the moment a number changes:

- the bridge down → a red bar on top of every admin page and a count on «سيرفر الواتساب»; when it's back, a WhatsApp message
  to the admin saying for how long it was down (the alert itself needs the bridge, so it comes when it's back);
- a number whose phone removed the linked device → the owner sees «📵 واتساب المتجر اتفصل» on every dashboard page with one
  button that shows a new QR, and the admin gets a WhatsApp alert;
- a number WhatsApp refused (possibly banned) → the owner is told to contact support, the admin is alerted.

## 11. The API (for Sellybot only)

Every route needs `X-Bridge-Secret: <secret>` (or `Authorization: Bearer <secret>`), except `GET /health` (says only that it
is up; with the secret it adds version, memory, numbers and the webhook's state) and the signed `/media/...` links. 20 wrong
secrets from one address in 10 minutes = refused for 10 minutes.

| | |
|---|---|
| `GET /instances` | `{ instances: [{ instance_uniquename, instance_name, status, me_id, ... }] }` |
| `POST /instances` `{name}` | a new number (no phone yet) → `{ instance }` |
| `GET /instances/{id}` / `DELETE /instances/{id}` | one number / log it out and delete it |
| `GET /instances/{id}/status` | `{ status, me_id, state, reason, since }` |
| `GET /instances/{id}/qr-code` | `{ qr: "data:image/png;base64,..." | null, status }`; asking starts the connection |
| `POST /instances/{id}/start` · `/logout` | start the connection · unlink the phone (the login is deleted) |
| `POST /instances/{id}/send-message` `{chat_id, text}` | queued, paced → `{ message_id, status: "pending" }`; header `Idempotency-Key` never sends twice |
| `POST /instances/{id}/send-image` `{chat_id, base64|url, mimetype, caption}` | the same for a picture |
| `POST /instances/{id}/send-typing` `{chat_id}` | "typing…" |
| `GET /instances/{id}/messages/{message_id}` | `{ data: { status: pending|delivered|failed, error_code } }` |
| `GET /instances/{id}/lids/{lid}` | a hidden number (`@lid`) back to the phone, when WhatsApp knows it |
| `POST /webhook-test` | posts a signed `ping` to Sellybot → `{ ok, status, ms, error }` |

States (`status`): `WORKING` linked · `SCAN_QR_CODE` waiting for the scan · `STARTING` · `DISCONNECTED` (linked, reconnecting
with a growing wait) · `LOGGED_OUT` (the phone removed the link: a new QR is needed) · `FAILED` (WhatsApp refused it) ·
`STOPPED` (no connection: never linked, or nobody is looking at its QR).

**Webhook** (bridge → Sellybot), `POST WEBHOOK_URL`, header `X-Bridge-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, "<t>.<body>")>`,
refused by Sellybot when older than 5 minutes:

```json
{ "event": "message", "instance_id": "wa1a2b3c4d5e",
  "payload": { "id": "3EB0…", "chat_id": "201001234567@c.us", "from_me": false, "type": "chat|image|video|ptt|audio|document|sticker|location|vcard",
               "text": "…", "timestamp": 1764300000, "push_name": "…", "has_media": false,
               "media": { "mimetype": "image/jpeg", "url": "https://wa.sellybot.online/media/<id>/<msg>.jpg?exp=…&sig=…" } } }
{ "event": "session.status", "instance_id": "…", "payload": { "status": "WORKING|LOGGED_OUT|FAILED|DISCONNECTED", "me_id": "2010…" } }
{ "event": "ping" }
```

Groups, status updates, channels, broadcast lists, reactions, edits and deletes are never posted; our own sends are not
echoed back; a message seen twice is posted once; messages older than 15 minutes after a reconnect are not posted.
The owner's own messages typed on the phone are posted with `from_me: true`, so Sellybot keeps "the owner answered, the bot
steps aside". A failed post is tried again (2 s … 2 min).

## 12. Developing and testing (no WhatsApp needed)

```bash
cd deploy/wa-bridge && npm ci && npm test      # 39 tests: the pure parts + the whole bridge on a pretend WhatsApp against a pretend Sellybot
FAKE_WA=1 BRIDGE_SECRET=<32+ chars> DATA_DIR=./data node src/main.mjs   # a local bridge on a pretend WhatsApp (adds /_fake/* routes)
```

Sellybot's side is tested by the regression suite outside the repo (`D:\sellystore-work\reg`: `wa-unit.ps1` for the pure C#
parts, `t12.mjs` for the local site against the real bridge code on a pretend WhatsApp).

Never commit `.env`, `/var/lib/wa-bridge`, `auth/` or `data/` (all in `.gitignore`): they hold the secret and every number's
WhatsApp login.
