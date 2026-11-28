#!/bin/bash
# One-time setup of the bridge's machine (Ubuntu 24.04, run as root: sudo bash server-setup.sh). Safe to run again.
# Every step is explained in ../SETUP.md. It never prints the secret.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a

DOMAIN="${DOMAIN:-wa.sellybot.online}"
REPO="${REPO:-https://github.com/YoussefGhonem-hub/Sellybot.git}"
REF="${REF:-main}"
USER_NAME=wabridge
APP_ROOT=/opt/wa-bridge
APP="$APP_ROOT/app"
DATA=/var/lib/wa-bridge
PM2_VERSION=7.1.2
say() { printf '\n== %s\n' "$*"; }

say "1. system update + unattended security updates"
apt-get update -q
apt-get -y -q -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold upgrade
apt-get -y -q install unattended-upgrades apt-listchanges iptables-persistent git curl ca-certificates gnupg debian-keyring debian-archive-keyring apt-transport-https
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF
systemctl enable --now unattended-upgrades >/dev/null

say "2. a 2 GB swap file (1 GB of RAM is tight)"
if ! swapon --show | grep -q /swapfile; then
  [ -f /swapfile ] || fallocate -l 2G /swapfile
  chmod 600 /swapfile; mkswap /swapfile >/dev/null; swapon /swapfile
fi
grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
echo 'vm.swappiness=10' > /etc/sysctl.d/90-wa-bridge.conf; sysctl -q -p /etc/sysctl.d/90-wa-bridge.conf

say "3. open 80 and 443 in iptables (Oracle's image rejects everything but 22), kept across reboots"
for p in 80 443; do
  if ! iptables -C INPUT -p tcp -m state --state NEW -m tcp --dport $p -j ACCEPT 2>/dev/null; then
    pos=$(iptables -L INPUT --line-numbers -n | awk '$2=="REJECT"{print $1; exit}')
    if [ -n "$pos" ]; then iptables -I INPUT "$pos" -p tcp -m state --state NEW -m tcp --dport $p -j ACCEPT
    else iptables -A INPUT -p tcp -m state --state NEW -m tcp --dport $p -j ACCEPT; fi
  fi
done
netfilter-persistent save >/dev/null

say "4. Node.js LTS (24.x, NodeSource apt repository)"
install -d -m 0755 /etc/apt/keyrings
if [ ! -f /etc/apt/keyrings/nodesource.gpg ]; then
  curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg
fi
echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_24.x nodistro main" > /etc/apt/sources.list.d/nodesource.list
# NodeSource's security fixes come with the unattended updates too
cat > /etc/apt/apt.conf.d/52unattended-nodesource <<'EOF'
Unattended-Upgrade::Origins-Pattern { "origin=Node Source"; };
EOF

say "5. Caddy (HTTPS with its own certificate for $DOMAIN)"
if [ ! -f /usr/share/keyrings/caddy-stable-archive-keyring.gpg ]; then
  curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/gpg.key | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
fi
curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt -o /etc/apt/sources.list.d/caddy-stable.list
apt-get update -q
apt-get -y -q install nodejs caddy
node -v

say "6. the bridge's own user and folders"
id -u $USER_NAME >/dev/null 2>&1 || useradd --system --create-home --home-dir /home/$USER_NAME --shell /usr/sbin/nologin $USER_NAME
install -d -o $USER_NAME -g $USER_NAME -m 0750 $APP_ROOT
install -d -o $USER_NAME -g $USER_NAME -m 0700 $DATA
install -d -o root -g root -m 0700 /var/backups/wa-bridge

say "7. the code: only deploy/wa-bridge of the repo (sparse checkout, public repo, no credentials)"
if [ ! -d "$APP/.git" ]; then
  sudo -u $USER_NAME git clone -q --depth 1 --filter=blob:none --sparse --branch "$REF" "$REPO" "$APP"
  sudo -u $USER_NAME git -C "$APP" sparse-checkout set deploy/wa-bridge
fi

say "8. the .env with the secret: made once, readable only by $USER_NAME"
if [ ! -f $APP_ROOT/.env ]; then
  umask 077
  SECRET=$(openssl rand -base64 64 | tr -dc 'A-Za-z0-9' | cut -c1-48)
  cat > $APP_ROOT/.env <<EOF
BRIDGE_SECRET=$SECRET
WEBHOOK_URL=https://sellybot.online/api/wa-webhook.ashx?src=bridge
PUBLIC_URL=https://$DOMAIN
DATA_DIR=$DATA
HOST=127.0.0.1
PORT=3100
MAX_INSTANCES=25
EOF
  unset SECRET
fi
chown $USER_NAME:$USER_NAME $APP_ROOT/.env; chmod 600 $APP_ROOT/.env

say "9. pm2 (one process, starts on boot) + log rotation"
npm install -g --no-fund --no-audit "pm2@$PM2_VERSION" >/dev/null
env PATH="$PATH" pm2 startup systemd -u $USER_NAME --hp /home/$USER_NAME >/dev/null
sudo -u $USER_NAME -H pm2 install pm2-logrotate >/dev/null 2>&1 || true
sudo -u $USER_NAME -H pm2 set pm2-logrotate:max_size 10M >/dev/null
sudo -u $USER_NAME -H pm2 set pm2-logrotate:retain 7 >/dev/null
sudo -u $USER_NAME -H pm2 set pm2-logrotate:compress true >/dev/null

say "10. Caddyfile"
cp "$APP/deploy/wa-bridge/server/Caddyfile" /etc/caddy/Caddyfile
sed -i "s/wa.sellybot.online/$DOMAIN/" /etc/caddy/Caddyfile
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
systemctl enable caddy >/dev/null; systemctl reload caddy || systemctl restart caddy

say "11. daily backup of the logins (keep 7) + the update command"
install -m 0755 "$APP/deploy/wa-bridge/scripts/backup.sh" /etc/cron.daily/wa-bridge-backup
ln -sf "$APP/deploy/wa-bridge/scripts/update.sh" /usr/local/bin/wa-bridge-update
chmod 0755 "$APP/deploy/wa-bridge/scripts/update.sh"

say "12. first start"
/usr/local/bin/wa-bridge-update "$REF"
say "done"
