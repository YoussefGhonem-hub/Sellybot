// Every instance on this bridge: kept in <dataDir>/instances.json, each one's WhatsApp login in <dataDir>/auth/<id>/.
// On start, every instance that has a login connects again by itself (a few seconds apart), so a restart needs no new QR.
import fs from 'node:fs';
import crypto from 'node:crypto';
import { Session, BridgeError } from './session.mjs';

export class Manager {
  constructor({ cfg, log, makeSocket, media, webhook, qrToDataUrl, now = Date.now }) {
    Object.assign(this, { cfg, log, makeSocket, media, webhook, qrToDataUrl, now });
    this.file = `${cfg.dataDir}/instances.json`;
    this.sessions = new Map();
    fs.mkdirSync(`${cfg.dataDir}/auth`, { recursive: true, mode: 0o700 });
  }

  #session(row) {
    return new Session({ ...row, cfg: this.cfg, log: this.log, makeSocket: this.makeSocket, media: this.media, now: this.now,
      qrToDataUrl: this.qrToDataUrl, emit: ev => this.webhook.post(ev) });
  }

  async load() {
    let rows = [];
    try { rows = JSON.parse(fs.readFileSync(this.file, 'utf8')); } catch { }
    for (const r of Array.isArray(rows) ? rows : []) if (r && /^[a-z0-9]{3,40}$/.test(r.id)) this.sessions.set(r.id, this.#session(r));
    let i = 0;
    for (const s of this.sessions.values()) if (s.authed()) {
      const delay = (i++) * (this.cfg.staggerMs ?? 1500);
      setTimeout(() => s.start(), delay).unref?.();
    }
    this.log.info('loaded', { instances: this.sessions.size, reconnecting: i });
  }

  save() {
    const rows = [...this.sessions.values()].map(s => ({ id: s.id, name: s.name, createdAt: s.createdAt }));
    const tmp = `${this.file}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(rows, null, 1), { mode: 0o600 });
    fs.renameSync(tmp, this.file);
  }

  get(id) {
    const s = this.sessions.get(String(id || ''));
    if (!s) throw new BridgeError(404, 'INSTANCE_NOT_FOUND', 'no such instance');
    return s;
  }

  list() { return [...this.sessions.values()].map(s => s.info()); }

  create({ id, name } = {}) {
    if (this.sessions.size >= this.cfg.maxInstances) throw new BridgeError(507, 'BRIDGE_FULL', `this server holds ${this.cfg.maxInstances} numbers at most`);
    let iid = id ? String(id) : null;
    if (iid && !/^[a-z0-9]{3,40}$/.test(iid)) throw new BridgeError(400, 'BAD_ID', 'id: 3-40 lowercase letters or digits');
    if (iid && this.sessions.has(iid)) throw new BridgeError(409, 'INSTANCE_EXISTS', 'that id is taken');
    while (!iid || this.sessions.has(iid)) iid = 'wa' + crypto.randomBytes(5).toString('hex');
    const s = this.#session({ id: iid, name: String(name || iid).slice(0, 80) });
    this.sessions.set(iid, s);
    this.save();
    this.log.info('created', { instance: iid });
    return s;
  }

  async remove(id) {
    const s = this.get(id);
    await s.logout();
    this.sessions.delete(s.id);
    try { this.media.dropInstance(s.id); } catch { }
    this.save();
    this.log.info('removed', { instance: s.id });
  }

  counts() {
    const c = { total: this.sessions.size };
    for (const s of this.sessions.values()) c[s.state] = (c[s.state] || 0) + 1;
    return c;
  }

  sweep() { for (const s of this.sessions.values()) s.sweepIdle(); }

  // a restart: close every connection, keep every login
  async shutdown() { await Promise.all([...this.sessions.values()].map(s => s.stop('shutdown').catch(() => { }))); }
}
