// Pictures customers send, kept on disk for a few hours so Sellybot can fetch them by a signed link (sign.mjs mediaUrl).
import fs from 'node:fs';
import path from 'node:path';

const EXT = { 'image/jpeg': 'jpg', 'image/png': 'png', 'image/webp': 'webp', 'image/gif': 'gif' };
const MIME = Object.fromEntries(Object.entries(EXT).map(([m, e]) => [e, m]));

export class MediaStore {
  constructor(dir, ttlMs = 6 * 3600 * 1000, now = Date.now) {
    this.dir = dir; this.ttl = ttlMs; this.now = now;
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  }
  static safe(s) { return String(s || '').replace(/[^A-Za-z0-9_-]/g, '').slice(0, 80); }
  // -> the file name ("<id>.jpg") under the instance's folder
  save(instance, id, buf, mime) {
    const inst = MediaStore.safe(instance), file = `${MediaStore.safe(id) || 'm' + this.now()}.${EXT[String(mime || '').split(';')[0].trim()] || 'jpg'}`;
    const d = path.join(this.dir, inst);
    fs.mkdirSync(d, { recursive: true, mode: 0o700 });
    fs.writeFileSync(path.join(d, file), buf, { mode: 0o600 });
    return file;
  }
  // { file, mime } or null
  get(instance, file) {
    const inst = MediaStore.safe(instance), m = /^([A-Za-z0-9_-]{1,80})\.(jpg|png|webp|gif)$/.exec(String(file || ''));
    if (!inst || !m) return null;
    const p = path.join(this.dir, inst, file);
    try { const st = fs.statSync(p); if (this.now() - st.mtimeMs > this.ttl) return null; } catch { return null; }
    return { file: p, mime: MIME[m[2]] };
  }
  dropInstance(instance) { fs.rmSync(path.join(this.dir, MediaStore.safe(instance)), { recursive: true, force: true }); }
  sweep() {
    let n = 0;
    for (const inst of safeList(this.dir)) for (const f of safeList(path.join(this.dir, inst))) {
      const p = path.join(this.dir, inst, f);
      try { if (this.now() - fs.statSync(p).mtimeMs > this.ttl) { fs.rmSync(p, { force: true }); n++; } } catch { }
    }
    return n;
  }
}

function safeList(d) { try { return fs.readdirSync(d); } catch { return []; } }
