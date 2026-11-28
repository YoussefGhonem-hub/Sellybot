// The real WhatsApp connection (Baileys, MIT licence: https://github.com/WhiskeySockets/Baileys). No browser.
// Kept light for a 1 GB machine: no history sync, no link previews, "online" never shown (so the store's phone keeps
// getting its notifications), and our own sends are not echoed back.
import fs from 'node:fs';
import makeWASocket, { useMultiFileAuthState, makeCacheableSignalKeyStore, fetchLatestWaWebVersion, downloadMediaMessage } from '@whiskeysockets/baileys';
import pino from 'pino';

let version = null, versionAt = 0;
async function waVersion(log) {
  if (version && Date.now() - versionAt < 12 * 3600e3) return version;
  try {
    const r = await fetchLatestWaWebVersion({ signal: AbortSignal.timeout(8000) });
    if (r && Array.isArray(r.version)) { version = r.version; versionAt = Date.now(); }
  } catch (e) { log.warn('WhatsApp Web version check failed, using the built-in one', { error: e.message }); }
  return version;
}

export function baileysFactory({ level = 'error' } = {}) {
  const logger = pino({ level });
  return async ({ dir, log, getMessage }) => {
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
    const { state, saveCreds } = await useMultiFileAuthState(dir);
    const v = await waVersion(log);
    const sock = makeWASocket({
      auth: { creds: state.creds, keys: makeCacheableSignalKeyStore(state.keys, logger) },
      ...(v ? { version: v } : {}),
      browser: ['Sellybot', 'Chrome', '1.0.0'],   // the name the phone shows under «الأجهزة المرتبطة»
      logger,
      markOnlineOnConnect: false,
      syncFullHistory: false,
      shouldSyncHistoryMessage: () => false,
      emitOwnEvents: false,
      generateHighQualityLinkPreview: false,
      getMessage: async key => getMessage(key),
    });
    sock.ev.on('creds.update', saveCreds);
    return {
      sock,
      authed: () => !!(state.creds && state.creds.me),
      download: m => downloadMediaMessage(m, 'buffer', {}, { logger, reuploadRequest: sock.updateMediaMessage }),
    };
  };
}
