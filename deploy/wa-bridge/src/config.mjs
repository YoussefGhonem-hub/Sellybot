// Settings come from the environment, or from the .env file named by ENV_FILE (on the server: /opt/wa-bridge/.env,
// readable only by the bridge's user). Never put the secret in the repo.
import fs from 'node:fs';

export function readEnvFile(file) {
  const out = {};
  let text = '';
  try { text = fs.readFileSync(file, 'utf8'); } catch { return out; }
  for (const line of text.split(/\r?\n/)) {
    const m = /^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (!m || line.trim().startsWith('#')) continue;
    out[m[1]] = m[2].replace(/^(['"])(.*)\1$/, '$2');
  }
  return out;
}

const int = (v, d) => { const n = parseInt(v, 10); return Number.isFinite(n) ? n : d; };

export function loadConfig(env = process.env) {
  const e = { ...(env.ENV_FILE ? readEnvFile(env.ENV_FILE) : {}), ...env };
  const cfg = {
    port: int(e.PORT, 3100),
    host: e.HOST || '127.0.0.1',                       // behind Caddy: never open to the internet directly
    secret: e.BRIDGE_SECRET || '',
    webhookUrl: e.WEBHOOK_URL || 'https://sellybot.online/api/wa-webhook.ashx?src=bridge',
    publicUrl: (e.PUBLIC_URL || 'https://wa.sellybot.online').replace(/\/+$/, ''),
    dataDir: e.DATA_DIR || '/var/lib/wa-bridge',
    maxInstances: int(e.MAX_INSTANCES, 15),
    perMinute: int(e.SEND_PER_MINUTE, 20),
    perHour: int(e.SEND_PER_HOUR, 400),
    gapMin: int(e.SEND_GAP_MIN_MS, 1200),
    gapMax: int(e.SEND_GAP_MAX_MS, 3500),
    maxQueue: int(e.SEND_MAX_QUEUE, 300),
    staggerMs: int(e.RECONNECT_STAGGER_MS, 1500),
    baileysLog: e.BAILEYS_LOG || 'error',
    fake: e.FAKE_WA === '1',
    fakeTyping: e.FAKE_WA === '1',
  };
  if (cfg.secret.length < 32) throw new Error('BRIDGE_SECRET is missing or shorter than 32 characters');
  return cfg;
}
