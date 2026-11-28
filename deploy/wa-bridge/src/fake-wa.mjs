// A pretend WhatsApp for tests (FAKE_WA=1, never on the server): the same socket shape the session uses, driven by hand.
//   scan(id, number)      the store's phone scans the QR
//   incoming(id, msg)     a customer (or the owner's phone, fromMe) sends something
//   phoneLogout(id)       the phone removes the linked device
//   drop(id)              the connection drops (it comes back by itself)
// A "login" is a creds.json file in the instance's folder, so a restart reconnects with no QR, like the real one.
import fs from 'node:fs';

const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

class Ev {
  constructor() { this.h = {}; }
  on(e, f) { (this.h[e] = this.h[e] || []).push(f); }
  emit(e, x) { for (const f of this.h[e] || []) f(x); }
}

export class FakeWa {
  constructor() { this.socks = new Map(); this.sent = []; this.notOnWa = new Set(); this.qrN = 0; }

  factory() {
    return async ({ id, dir }) => {
      fs.mkdirSync(dir, { recursive: true });
      const creds = () => { try { return JSON.parse(fs.readFileSync(`${dir}/creds.json`, 'utf8')); } catch { return null; } };
      const ev = new Ev(), self = this;
      let open = false, n = 0;
      const close = code => { open = false; ev.emit('connection.update', { connection: 'close', lastDisconnect: { error: code ? { output: { statusCode: code } } : new Error('closed') } }); };
      const sock = {
        ev, dir,
        get user() { const c = creds(); return c ? { id: c.me.id } : undefined; },
        async sendMessage(jid, content) {
          if (!open) throw new Error('not connected');
          const key = { id: `FAKE${id}${++n}`, remoteJid: jid, fromMe: true };
          self.sent.push({ instance: id, jid, content: content.image && Buffer.isBuffer(content.image) ? { ...content, image: `<${content.image.length} bytes>` } : content, at: Date.now() });
          return { key, message: content.text ? { conversation: content.text } : { imageMessage: { caption: content.caption || '' } } };
        },
        async sendPresenceUpdate(state, jid) { self.sent.push({ instance: id, jid, presence: state, at: Date.now() }); },
        async onWhatsApp(jid) { const d = String(jid).split('@')[0]; return [{ jid, exists: !self.notOnWa.has(d) }]; },
        signalRepository: { lidMapping: { getPNForLID: async lid => (String(lid).startsWith('111') ? `2010${String(lid).slice(3, 11)}@s.whatsapp.net` : null) } },
        async logout() { try { fs.rmSync(`${dir}/creds.json`, { force: true }); } catch { } close(401); },
        async end() { close(0); },
        _close: close,
        _open() { open = true; ev.emit('connection.update', { connection: 'open' }); },
        _qr() { ev.emit('connection.update', { qr: `FAKE-QR-${id}-${++self.qrN}` }); },
      };
      this.socks.set(id, sock);
      setTimeout(() => { if (creds()) sock._open(); else sock._qr(); }, 20);
      return { sock, authed: () => !!creds(), download: async () => PNG };
    };
  }

  #sock(id) { const s = this.socks.get(id); if (!s) throw new Error(`no fake socket for ${id}`); return s; }

  scan(id, number) {
    const s = this.#sock(id);
    fs.writeFileSync(`${s.dir}/creds.json`, JSON.stringify({ me: { id: `${number}:7@s.whatsapp.net` } }));
    s._close(515);   // like WhatsApp: a fresh connection right after the scan
  }

  incoming(id, { from, text, fromMe = false, image = false, type = 'notify', msgId, lid, group = false }) {
    const s = this.#sock(id);
    const remoteJid = group ? '120363000000@g.us' : lid ? `${lid}@lid` : `${from}@s.whatsapp.net`;
    const message = image ? { imageMessage: { caption: text || '', mimetype: 'image/png', fileLength: 70 } } : { conversation: text };
    const m = { key: { remoteJid, fromMe, id: msgId || `IN${Date.now()}${Math.random().toString(36).slice(2, 7)}` }, messageTimestamp: Math.floor(Date.now() / 1000), pushName: 'Test', message };
    s.ev.emit('messages.upsert', { messages: [m], type });
    return m.key.id;
  }

  phoneLogout(id) { const s = this.#sock(id); fs.rmSync(`${s.dir}/creds.json`, { force: true }); s._close(401); }
  drop(id) { this.#sock(id)._close(428); }
}
