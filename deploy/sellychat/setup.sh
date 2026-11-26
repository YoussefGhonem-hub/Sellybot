#!/bin/sh
# SellyChat on a fresh Ubuntu server, and again after every change here (it keeps what's there):
#   sudo SC_DOMAIN=chat.sellybot.online SC_ADMIN_EMAIL=you@example.com sh setup.sh
# 1. Docker, if it isn't there
# 2. .env / .env.db with new random secrets, the first time only
# 3. builds the image, prepares the database, starts everything
# 4. setup.rb: the name and links, the super admin, the Platform App; prints PLATFORM_TOKEN for admin.html ← SellyChat
set -eu
cd "$(dirname "$0")"
: "${SC_DOMAIN:?set SC_DOMAIN, e.g. chat.sellybot.online}"

if ! command -v docker >/dev/null 2>&1; then
  echo "installing Docker..."
  curl -fsSL https://get.docker.com | sh
fi

rnd() { head -c 48 /dev/urandom | od -An -tx1 | tr -d ' \n' | cut -c1-"$1"; }
if [ ! -f .env ]; then
  DBPW=$(rnd 32)
  cat > .env <<EOF
SC_DOMAIN=$SC_DOMAIN
FRONTEND_URL=https://$SC_DOMAIN
SECRET_KEY_BASE=$(rnd 96)
DEFAULT_LOCALE=ar
ENABLE_ACCOUNT_SIGNUP=false
FORCE_SSL=false
POSTGRES_HOST=postgres
POSTGRES_USERNAME=postgres
POSTGRES_PASSWORD=$DBPW
POSTGRES_DATABASE=chatwoot
REDIS_URL=redis://redis:6379
REDIS_PASSWORD=$(rnd 32)
ACTIVE_STORAGE_SERVICE=local
RAILS_LOG_TO_STDOUT=true
LOG_LEVEL=info
RAILS_MAX_THREADS=5
DISABLE_TELEMETRY=true
DISABLE_GRAVATAR=true
ENABLE_PUSH_RELAY_SERVER=false
EOF
  echo "POSTGRES_PASSWORD=$DBPW" > .env.db
  chmod 600 .env .env.db
  echo "made .env (keep it: it holds the database password and the keys)"
fi

docker compose build --pull
docker compose up -d postgres redis
sleep 5
docker compose run --rm rails bundle exec rails db:chatwoot_prepare
docker compose up -d

if [ -n "${SC_ADMIN_EMAIL:-}" ] && [ -z "${SC_ADMIN_PASSWORD:-}" ]; then SC_ADMIN_PASSWORD="Sc-$(rnd 16)!"; NEWPW=1; fi
docker compose exec -T -e SC_ADMIN_EMAIL="${SC_ADMIN_EMAIL:-}" -e SC_ADMIN_PASSWORD="${SC_ADMIN_PASSWORD:-}" -e SELLYBOT_URL="${SELLYBOT_URL:-https://sellybot.online}" \
  rails bundle exec rails runner - < setup.rb | grep -E "^(set |super admin|PLATFORM_TOKEN)" || true
[ "${NEWPW:-}" = 1 ] && echo "super admin password (shown once, for https://$SC_DOMAIN/super_admin): $SC_ADMIN_PASSWORD"
echo "done: https://$SC_DOMAIN"
