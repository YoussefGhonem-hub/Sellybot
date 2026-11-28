// One WhatsApp number = one "instance" = one linked-device connection.
// States (what Sellybot sees, Wapilot-style, in brackets):
//   idle (STOPPED)          no connection: never linked, a QR nobody looked at, or logged out on purpose
//   connecting (STARTING)   opening the connection
//   qr (SCAN_QR_CODE)       waiting for the store's phone to scan the QR
//   open (WORKING)          linked and connected
//   disconnected (DISCONNECTED) linked, the connection dropped, trying again with a growing wait
//   logged_out (LOGGED_OUT) the phone removed the linked device (or WhatsApp did): a new QR is needed
//   failed (FAILED)         WhatsApp refused the number (403)
import fs from 'node:fs';
import crypto from 'node:crypto';
import { Seen } from './dedupe.mjs';
import { Pacer, typingMs } from './pace.mjs';
import { toEvent, MAX_DOWNLOAD } from './parse.mjs';
import { toJid, digits, isLid } from './jid.mjs';
import { mediaUrl } from './sign.mjs';

export const STATUS = { idle: 'STOPPED', connecting: 'STARTING', qr: 'SCAN_QR_CODE', open: 'WORKING', disconnected: 'DISCONNECTED', logged_out: 'LOGGED_OUT', failed: 'FAILED' };
const QR_IDLE_MS = 120000;      // a QR on nobody's screen for this long: the connection is closed (it's opened again when asked)
const QR_WATCHED_MS = 45000;    // a QR asked for this recently is renewed when WhatsApp lets it expire
const DOWN_NOTICE_MS = 90000;   // a dropped connection is reported only if it isn't back by then

export class BridgeError extends Error {
  constructor(status, code, message) { super(message || code); this.status = status; this.code = code; }
}

export class Session {
  constructor({ id, name, createdAt, cfg, log, makeSocket, media, emit, qrToDataUrl, now = Date.now }) {
    Object.assign(this, { id, name: name || id, createdAt: createdAt || new Date(now()).toISOString(), cfg, log, makeSocket, media, emit, qrToDataUrl, now });
    this.dir = `${cfg.dataDir}/auth/${id}`;
    this.state = 'idle'; this.reason = null; this.since = now(); this.number = null; this.qr = null; this.qrAt = 0; this.lastQrAsk = 0;
    this.sock = null; this.starting = null; this.attempts = 0; this.retryTimer = null; this.downTimer = null; this.stopping = false;
    this.seen = new Seen(4000, 12 * 3600e3, now);        // message ids already passed on to Sellybot
    this.sentIds = new Seen(3000, 6 * 3600e3, now);      // what we sent (never passed back as the owner typing)
    this.idem = new Seen(5000, 24 * 3600e3, now);        // Sellybot's idempotency keys -> our message id
    this.statuses = new Seen(8000, 24 * 3600e3, now);    // our message id -> { status, error }
    this.recent = new Seen(500, 3600e3, now);            // what we sent, for WhatsApp's retry requests (getMessage)
    this.onWa = new Seen(5000, 24 * 3600e3, now);        // "is this number on WhatsApp?" answers
    this.talked = new Seen(20000, 7 * 24 * 3600e3, now); // customers who wrote to this number (surely on WhatsApp)
    this.pacer = new Pacer({ gapMin: cfg.gapMin, gapMax: cfg.gapMax, perMinute: cfg.perMinute, perHour: cfg.perHour, maxQueue: cfg.maxQueue, now });
  }

  get status() { return STATUS[this.state]; }
  authed() {
    try { const c = JSON.parse(fs.readFileSync(`${this.dir}/creds.json`, 'utf8')); return !!(c && c.me && c.me.id); } catch { return false; }
  }
  info() {
    return { instance_uniquename: this.id, instance_name: this.name, status: this.status, state: this.state, me_id: this.number || '',
      is_api: true, since: new Date(this.since).toISOString(), reason: this.reason || '', created_at: this.createdAt, queue: this.pacer.pending };
  }

  #set(state, reason = null) {
    if (this.state === state && this.reason === reason) return;
    const was = this.state;
    this.state = state; this.reason = reason; this.since = this.now();
    this.log.info('state', { instance: this.id, from: was, to: state, reason });
    // what Sellybot acts on: linked, unlinked from the phone, refused. A stop (a bridge restart, Sellybot's own logout) isn't news;
    // a dropped connection is told only when it stays down (#retry)
    if (state === 'open' || state === 'logged_out' || state === 'failed') this.#announce();
  }
  #announce() {
    this.emit({ event: 'session.status', instance_id: this.id, payload: { status: this.status, state: this.state, me_id: this.number || '', reason: this.reason || '' } });
  }

  // ---------- the connection ----------
  start() {
    if (this.sock) return Promise.resolve();
    if (this.starting) return this.starting;
    clearTimeout(this.retryTimer); this.retryTimer = null; this.stopping = false;
    if (this.state !== 'disconnected') this.#set('connecting');
    this.starting = (async () => {
      try {
        const { sock, authed, download } = await this.makeSocket({ id: this.id, dir: this.dir, log: this.log, getMessage: key => this.recent.get(key && key.id) });
        this.sock = sock; this.download = download; this.authedNow = authed;
        sock.ev.on('connection.update', u => this.#onConnection(sock, u));
        sock.ev.on('messages.upsert', u => this.#onUpsert(u).catch(e => this.log.warn('upsert failed', { instance: this.id, error: e.message })));
      } catch (e) {
        this.log.warn('connect failed', { instance: this.id, error: e.message });
        this.sock = null;
        this.#retry('connect_failed');
      } finally { this.starting = null; }
    })();
    return this.starting;
  }

  #onConnection(sock, u) {
    if (sock !== this.sock) return;   // an old connection's last words
    if (u.qr) {
      this.qrAt = this.now();
      this.#set('qr');
      Promise.resolve(this.qrToDataUrl(u.qr)).then(d => { if (sock === this.sock) this.qr = d; }).catch(() => { });
    }
    if (u.connection === 'open') {
      clearTimeout(this.downTimer); this.downTimer = null;
      this.attempts = 0; this.qr = null;
      this.number = digits((sock.user && sock.user.id) || '') || this.number;
      this.#set('open');
    }
    if (u.connection === 'close') {
      const err = u.lastDisconnect && u.lastDisconnect.error;
      const code = err && err.output ? err.output.statusCode : err && err.statusCode;
      this.sock = null; this.qr = null;
      this.#closed(code);
    }
  }

  #closed(code) {
    const wasLinked = this.state === 'open' || this.state === 'disconnected' || !!this.number;
    if (this.stopping) { this.stopping = false; this.#set('idle', this.reasonOnStop || null); this.reasonOnStop = null; return; }
    if (code === 401 || code === 411) {           // the phone (or WhatsApp) removed this linked device
      this.#wipe(); this.number = null;
      this.#set(wasLinked ? 'logged_out' : 'idle', wasLinked ? 'logged_out' : 'qr_refused');
      return;
    }
    if (code === 403) { this.#set('failed', 'forbidden'); return; }
    if (code === 515) { this.start(); return; }    // WhatsApp asks for a fresh connection right after the scan
    if (!this.authedNow || !this.authedNow()) {
      // the QR expired: renewed only while someone is looking at it
      if (this.now() - this.lastQrAsk < QR_WATCHED_MS) { setTimeout(() => this.start(), 500).unref?.(); this.#set('connecting'); }
      else this.#set('idle', 'qr_expired');
      return;
    }
    this.#retry(code === 440 ? 'replaced' : 'connection_lost', code === 440);
  }

  #retry(reason, slow = false) {
    if (this.state === 'open') { this.#set('disconnected', reason); }
    else if (this.state !== 'disconnected' && this.authed()) this.#set('disconnected', reason);
    else if (!this.authed()) { this.#set('idle', reason); return; }
    const n = this.attempts++;
    const wait = Math.min(slow ? 300000 : 60000, 1000 * 2 ** n) + Math.floor(Math.random() * 1000);
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => { this.retryTimer = null; this.start(); }, wait);
    this.retryTimer.unref?.();
    if (!this.downTimer) {
      this.downTimer = setTimeout(() => { this.downTimer = null; if (this.state !== 'open') this.#announce(); }, DOWN_NOTICE_MS);
      this.downTimer.unref?.();
    }
  }

  #wipe() {
    try { fs.rmSync(this.dir, { recursive: true, force: true }); } catch { }
    // a key write may land after the close: once more a moment later
    setTimeout(() => { if (!this.sock) try { fs.rmSync(this.dir, { recursive: true, force: true }); } catch { } }, 2000).unref?.();
  }

  // close the connection, keep the link (a restart of the bridge, or a QR nobody looks at)
  async stop(reason = null) {
    clearTimeout(this.retryTimer); this.retryTimer = null; clearTimeout(this.downTimer); this.downTimer = null;
    if (this.starting) await this.starting;
    const sock = this.sock;
    if (!sock) { if (this.state !== 'logged_out' && this.state !== 'failed') this.#set('idle', reason); return; }
    this.stopping = true; this.reasonOnStop = reason;
    try { await sock.end(undefined); } catch { }
    if (this.sock === sock) { this.sock = null; this.stopping = false; this.#set('idle', reason); }
  }

  // unlink the phone: WhatsApp drops the linked device, the saved login is deleted, a new QR is needed
  async logout() {
    clearTimeout(this.retryTimer); this.retryTimer = null; clearTimeout(this.downTimer); this.downTimer = null;
    if (this.starting) await this.starting;
    const sock = this.sock;
    this.stopping = true; this.reasonOnStop = 'logout';
    if (sock) {
      try { if (this.state === 'open') await sock.logout(); else await sock.end(undefined); } catch { }
    }
    this.sock = null; this.stopping = false; this.number = null; this.qr = null;
    this.#wipe();
    this.#set('idle', 'logout');
  }

  // a QR nobody has asked for in a while: close it (opened again on the next ask)
  sweepIdle() {
    if ((this.state === 'qr' || this.state === 'connecting') && this.sock && !this.authed() && this.now() - this.lastQrAsk > QR_IDLE_MS) this.stop('qr_idle');
  }

  // the QR for the dashboard: { qr, status }; asking for it starts the connection when there's none
  askQr() {
    this.lastQrAsk = this.now();
    if (this.state === 'open') return { qr: null, status: this.status, linked: true };
    if (this.state === 'failed') return { qr: null, status: this.status };
    if (!this.sock && !this.starting && this.state !== 'disconnected') this.start();
    return { qr: this.state === 'qr' ? this.qr : null, status: this.status };
  }

  // ---------- incoming ----------
  async #onUpsert({ messages, type }) {
    for (const m of messages || []) {
      const key = m && m.key;
      if (!key || !key.id) continue;
      if (key.fromMe && this.sentIds.has(key.id)) continue;      // our own message coming back
      if (!this.seen.add(key.id)) continue;                       // the same message twice
      const r = toEvent(m, { type, now: Math.floor(this.now() / 1000) });
      if (r.skip) continue;
      const p = r.payload;
      if (!p.from_me) { const d = digits(p.chat_id); if (d) this.talked.add(d); }
      if (r.download && this.download) {
        try {
          const buf = await this.download(m);
          if (buf && buf.length && buf.length <= MAX_DOWNLOAD) {
            const file = this.media.save(this.id, key.id, buf, p.media.mimetype);
            p.media.url = mediaUrl(this.cfg.publicUrl, this.cfg.secret, this.id, file);
          }
        } catch (e) { this.log.warn('media download failed', { instance: this.id, error: e.message }); }
      }
      this.emit({ event: 'message', instance_id: this.id, payload: p });
    }
  }

  // ---------- outgoing ----------
  #ready() { if (this.state !== 'open' || !this.sock) throw new BridgeError(409, 'INSTANCE_NOT_READY', 'this number is not connected'); }

  async #onWhatsApp(jid) {
    if (isLid(jid)) return true;
    const d = digits(jid);
    if (!d || this.talked.has(d)) return true;
    const hit = this.onWa.get(d);
    if (hit !== undefined) return hit;
    try {
      const r = await this.sock.onWhatsApp(jid);
      const yes = Array.isArray(r) ? r.some(x => x && x.exists) : true;
      this.onWa.set(d, yes);
      return yes;
    } catch { return true; }   // can't tell: try sending
  }

  // queued now, sent in its turn; { message_id, status: 'pending' } right away
  #queue(chatId, idem, content, textForTyping) {
    this.#ready();
    const jid = toJid(chatId);
    if (!jid) throw new BridgeError(400, 'BAD_CHAT', 'chat_id is not a person on WhatsApp');
    if (idem) { const was = this.idem.get(idem); if (was) return { message_id: was, status: (this.statuses.get(was) || {}).status || 'pending', duplicate: true }; }
    const mid = 'sb' + crypto.randomBytes(9).toString('hex');
    if (idem) this.idem.set(idem, mid);
    this.statuses.set(mid, { status: 'pending' });
    this.pacer.push(async () => {
      const sock = this.sock;
      if (this.state !== 'open' || !sock) { this.statuses.set(mid, { status: 'failed', error: 'INSTANCE_NOT_READY' }); return; }
      if (!(await this.#onWhatsApp(jid))) { this.statuses.set(mid, { status: 'failed', error: 'NOT_ON_WHATSAPP' }); return; }
      if (textForTyping != null) {
        try { await sock.sendPresenceUpdate('composing', jid); } catch { }
        await new Promise(r => setTimeout(r, this.cfg.fakeTyping ? 5 : typingMs(textForTyping)));
      }
      const sent = await sock.sendMessage(jid, content);
      if (textForTyping != null) sock.sendPresenceUpdate('paused', jid).catch(() => { });
      const waId = sent && sent.key && sent.key.id;
      if (waId) { this.sentIds.add(waId); this.recent.set(waId, sent.message); }
      this.statuses.set(mid, { status: 'delivered', wa_id: waId || '' });
    }).catch(e => {
      this.statuses.set(mid, { status: 'failed', error: e.code || 'SEND_FAILED' });
      this.log.warn('send failed', { instance: this.id, error: e.message });
    });
    return { message_id: mid, status: 'pending' };
  }

  sendText(chatId, text, idem) {
    const t = String(text || '');
    if (!t.trim()) throw new BridgeError(400, 'EMPTY_TEXT', 'text is empty');
    if (t.length > 20000) throw new BridgeError(413, 'TOO_LONG', 'text is too long');
    return this.#queue(chatId, idem, { text: t }, t);
  }

  // image: a Buffer, or a https URL WhatsApp's library downloads itself
  sendImage(chatId, { buffer, url, mimetype, caption }, idem) {
    if (!buffer && !url) throw new BridgeError(400, 'NO_IMAGE', 'send url or base64');
    if (buffer && buffer.length > 10 * 1024 * 1024) throw new BridgeError(413, 'TOO_LARGE', 'the picture is over 10 MB');
    const content = { image: buffer || { url }, ...(caption ? { caption: String(caption).slice(0, 1024) } : {}), ...(mimetype ? { mimetype } : {}) };
    return this.#queue(chatId, idem, content, null);
  }

  async typing(chatId, state = 'composing') {
    this.#ready();
    const jid = toJid(chatId);
    if (!jid) throw new BridgeError(400, 'BAD_CHAT', 'chat_id is not a person on WhatsApp');
    await this.sock.sendPresenceUpdate(state === 'paused' ? 'paused' : 'composing', jid);
  }

  messageStatus(mid) { return this.statuses.get(mid) || null; }

  async lidPhone(lid) {
    this.#ready();
    const map = this.sock.signalRepository && this.sock.signalRepository.lidMapping;
    if (!map || !map.getPNForLID) return null;
    try { return digits(await map.getPNForLID(String(lid).includes('@') ? lid : `${lid}@lid`)); } catch { return null; }
  }
}
