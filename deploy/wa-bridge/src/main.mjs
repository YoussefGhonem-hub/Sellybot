// The bridge's entry point (pm2 runs this file: ecosystem.config.cjs; by hand: node src/main.mjs). See server.mjs and SETUP.md.
import { loadConfig } from './config.mjs';
import { makeLog } from './log.mjs';
import { createBridge, VERSION } from './server.mjs';

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
