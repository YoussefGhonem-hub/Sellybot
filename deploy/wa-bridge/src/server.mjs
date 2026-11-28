// Sellybot's WhatsApp bridge: one linked-device connection per number, a small HTTP API for Sellybot only, and a signed
// webhook back to Sellybot for every customer message, every message the owner types on the phone, and connection changes.
// Listens on 127.0.0.1 only; Caddy serves it as https://wa.sellybot.online. Every route but /health and the signed /media
// links needs the shared secret (X-Bridge-Secret). See SETUP.md.
import http from 'node:http';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import QRCode from 'qrcode';
import { loadConfig } from './config.mjs';
import { makeLog } from './log.mjs';
import { safeEqual, secretFrom, checkMedia } from './sign.mjs';
import { Webhook } from './webhook.mjs';
import { MediaStore } from './media.mjs';
import { Manager } from './manager.mjs';
import { BridgeError } from './session.mjs';

export const VERSION = '1.0.0';
const started = Date.now();

function json(res, status, body) {
  const b = JSON.stringify(body);
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'content-length': Buffer.byteLength(b) });
  res.end(b);
}

function readBody(req, limit) {
  return new Promise((resolve, reject) => {
    let size = 0; const parts = [];
    req.on('data', c => { size += c.length; if (size > limit) { reject(new BridgeError(413, 'TOO_LARGE', 'body too large')); req.destroy(); } else parts.push(c); });
    req.on('end', () => {
      const text = Buffer.concat(parts).toString('utf8');
      if (!text.trim()) return resolve({});
      try { resolve(JSON.parse(text)); } catch { reject(new BridgeError(400, 'BAD_JSON', 'body is not JSON')); }
    });
    req.on('error', reject);
  });
}

const clientIp = req => String(req.headers['x-forwarded-for'] || '').split(',').map(s => s.trim()).filter(Boolean).pop() || req.socket.remoteAddress || '';

export function createBridge(cfg, { makeSocket, fetchFn, log = makeLog('info') } = {}) {
  const webhook = new Webhook({ url: cfg.webhookUrl, secret: cfg.secret, log, fetchFn });
  const media = new MediaStore(`${cfg.dataDir}/media`);
  const qrToDataUrl = q => QRCode.toDataURL(q, { margin: 2, width: 320, errorCorrectionLevel: 'M' });
  const manager = new Manager({ cfg, log, makeSocket, media, webhook, qrToDataUrl });
  const fails = new Map();   // ip -> { n, at }: wrong secrets

  const authOk = req => safeEqual(secretFrom(req.headers), cfg.secret);
  const tooManyFails = ip => { const f = fails.get(ip); return f && f.n >= 20 && Date.now() - f.at < 600000; };
  const failed = ip => { const f = fails.get(ip); fails.set(ip, f && Date.now() - f.at < 600000 ? { n: f.n + 1, at: f.at } : { n: 1, at: Date.now() }); };

  const health = req => {
    const base = { ok: true, service: 'sellybot-wa-bridge' };
    if (!authOk(req)) return base;   // the open check says only "up"
    const m = process.memoryUsage();
    return { ...base, version: VERSION, uptimeSec: Math.round((Date.now() - started) / 1000), node: process.version,
      memoryMb: { rss: Math.round(m.rss / 1048576), heapUsed: Math.round(m.heapUsed / 1048576) },
      instances: manager.counts(), maxInstances: cfg.maxInstances,
      webhook: { ...webhook.stats, pending: webhook.pending } };
  };

  const routes = [
    ['GET', /^\/health$/, async (req, res) => json(res, 200, health(req)), true],
    ['GET', /^\/media\/([A-Za-z0-9_-]+)\/([A-Za-z0-9_.-]+)$/, async (req, res, [inst, file], url) => {
      if (!checkMedia(cfg.secret, inst, file, url.searchParams.get('exp'), url.searchParams.get('sig'))) return json(res, 403, { code: 'BAD_LINK', message: 'link expired or wrong' });
      const f = media.get(inst, file);
      if (!f) return json(res, 404, { code: 'GONE', message: 'not here any more' });
      res.writeHead(200, { 'content-type': f.mime, 'cache-control': 'private, max-age=600' });
      fs.createReadStream(f.file).pipe(res);
    }, true],
    ['GET', /^\/instances$/, async (req, res) => json(res, 200, { instances: manager.list() })],
    ['POST', /^\/instances$/, async (req, res, m, url, body) => json(res, 201, { instance: manager.create({ id: body.id, name: body.name }).info() })],
    ['GET', /^\/instances\/([a-z0-9]+)$/, async (req, res, [id]) => json(res, 200, { instance: manager.get(id).info() })],
    ['DELETE', /^\/instances\/([a-z0-9]+)$/, async (req, res, [id]) => { await manager.remove(id); json(res, 200, { ok: true }); }],
    ['GET', /^\/instances\/([a-z0-9]+)\/status$/, async (req, res, [id]) => { const s = manager.get(id); json(res, 200, { status: s.status, state: s.state, me_id: s.number || '', reason: s.reason || '', since: new Date(s.since).toISOString() }); }],
    ['GET', /^\/instances\/([a-z0-9]+)\/qr-code$/, async (req, res, [id]) => json(res, 200, manager.get(id).askQr())],
    ['POST', /^\/instances\/([a-z0-9]+)\/start$/, async (req, res, [id]) => { const s = manager.get(id); s.askQr(); json(res, 200, { ok: true, status: s.status }); }],
    ['POST', /^\/instances\/([a-z0-9]+)\/logout$/, async (req, res, [id]) => { await manager.get(id).logout(); json(res, 200, { ok: true }); }],
    ['POST', /^\/instances\/([a-z0-9]+)\/send-message$/, async (req, res, [id], url, body) =>
      json(res, 200, manager.get(id).sendText(body.chat_id, body.text, req.headers['idempotency-key'] || body.idempotency_key))],
    ['POST', /^\/instances\/([a-z0-9]+)\/send-image$/, async (req, res, [id], url, body) => {
      let buffer = null;
      if (body.base64) { buffer = Buffer.from(String(body.base64), 'base64'); if (!buffer.length) throw new BridgeError(400, 'NO_IMAGE', 'base64 is empty'); }
      const link = body.url && /^https:\/\//i.test(String(body.url)) ? String(body.url) : null;
      json(res, 200, manager.get(id).sendImage(body.chat_id, { buffer, url: link, mimetype: body.mimetype, caption: body.caption }, req.headers['idempotency-key'] || body.idempotency_key));
    }, false, 16 * 1024 * 1024],
    ['POST', /^\/instances\/([a-z0-9]+)\/send-typing$/, async (req, res, [id], url, body) => { await manager.get(id).typing(body.chat_id, body.state); json(res, 200, { ok: true }); }],
    ['GET', /^\/instances\/([a-z0-9]+)\/messages\/([A-Za-z0-9]+)$/, async (req, res, [id, mid]) => {
      const st = manager.get(id).messageStatus(mid);
      if (!st) return json(res, 404, { code: 'MESSAGE_NOT_FOUND', message: 'unknown message id' });
      json(res, 200, { data: { id: mid, status: st.status, error_code: st.error || null } });
    }],
    ['GET', /^\/instances\/([a-z0-9]+)\/lids\/([0-9]+(?:@lid)?)$/, async (req, res, [id, lid]) => json(res, 200, { lid, phone: await manager.get(id).lidPhone(lid) })],
    ['POST', /^\/webhook-test$/, async (req, res) => json(res, 200, await webhook.ping())],
  ];
  if (cfg.fake && cfg.fakeWa) {
    const f = cfg.fakeWa;
    routes.push(
      ['POST', /^\/_fake\/([a-z0-9]+)\/scan$/, async (req, res, [id], url, b) => { f.scan(id, b.number || '201000000099'); json(res, 200, { ok: true }); }],
      ['POST', /^\/_fake\/([a-z0-9]+)\/incoming$/, async (req, res, [id], url, b) => json(res, 200, { id: f.incoming(id, b) })],
      ['POST', /^\/_fake\/([a-z0-9]+)\/phone-logout$/, async (req, res, [id]) => { f.phoneLogout(id); json(res, 200, { ok: true }); }],
      ['POST', /^\/_fake\/([a-z0-9]+)\/drop$/, async (req, res, [id]) => { f.drop(id); json(res, 200, { ok: true }); }],
      ['POST', /^\/_fake\/not-on-wa$/, async (req, res, m, url, b) => { f.notOnWa.add(String(b.number)); json(res, 200, { ok: true }); }],
      ['GET', /^\/_fake\/sent$/, async (req, res) => json(res, 200, f.sent)],
      ['POST', /^\/_fake\/reset$/, async (req, res) => { f.sent.length = 0; f.notOnWa.clear(); json(res, 200, { ok: true }); }],
    );
  }

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://bridge');
    const ip = clientIp(req);
    try {
      for (const [method, rx, fn, open, limit] of routes) {
        const m = rx.exec(url.pathname);
        if (!m) continue;
        if (req.method !== method) continue;
        if (!open) {
          if (tooManyFails(ip)) return json(res, 429, { code: 'TOO_MANY_TRIES', message: 'too many wrong secrets, wait 10 minutes' });
          if (!authOk(req)) { failed(ip); log.warn('wrong secret', { ip, path: url.pathname.split('/').slice(0, 2).join('/') }); return json(res, 401, { code: 'UNAUTHORIZED', message: 'wrong or missing secret' }); }
        }
        const body = method === 'POST' ? await readBody(req, limit || 64 * 1024) : {};
        return await fn(req, res, m.slice(1), url, body);
      }
      json(res, 404, { code: 'NOT_FOUND', message: 'no such route' });
    } catch (e) {
      if (e instanceof BridgeError) return json(res, e.status, { code: e.code, message: e.message });
      log.error('request failed', { path: url.pathname, error: e.message });
      if (!res.headersSent) json(res, 500, { code: 'BRIDGE_ERROR', message: 'something went wrong on the bridge' });
    }
  });

  const sweeper = setInterval(() => { manager.sweep(); try { media.sweep(); } catch { } }, 30000);
  sweeper.unref();
  const close = async () => {
    clearInterval(sweeper);
    const closed = new Promise(r => server.close(() => r()));
    server.closeAllConnections?.();   // idle keep-alive connections would hold the close (and the port) open
    await manager.shutdown();
    await closed;
  };
  return { server, manager, webhook, media, close };
}

// ---------- run ----------
if (process.argv[1] && fileURLToPath(import.meta.url) === fs.realpathSync(process.argv[1])) {
  const cfg = loadConfig();
  const log = makeLog(process.env.LOG_LEVEL || 'info');
  let makeSocket;
  if (cfg.fake) {
    const { FakeWa } = await import('./fake-wa.mjs');
    cfg.fakeWa = new FakeWa(); makeSocket = cfg.fakeWa.factory();
    log.warn('FAKE_WA=1: a pretend WhatsApp, for tests only');
  } else {
    const { baileysFactory } = await import('./baileys.mjs');
    makeSocket = baileysFactory({ level: cfg.baileysLog });
  }
  const b = createBridge(cfg, { makeSocket, log });
  await b.manager.load();
  b.server.listen(cfg.port, cfg.host, () => log.info('listening', { host: cfg.host, port: cfg.port, version: VERSION }));
  let stopping = false;
  const stop = async sig => {
    if (stopping) return; stopping = true;
    log.info('stopping', { signal: sig });
    const t = setTimeout(() => process.exit(0), 7000); t.unref();
    try { await b.close(); } catch { }
    process.exit(0);
  };
  process.on('SIGTERM', () => stop('SIGTERM'));
  process.on('SIGINT', () => stop('SIGINT'));
  process.on('message', m => { if (m === 'shutdown') stop('pm2'); });   // pm2 on Windows
  process.on('unhandledRejection', e => log.error('unhandled rejection', { error: e && e.message }));
  process.on('uncaughtException', e => log.error('uncaught exception', { error: e && e.message }));
}
