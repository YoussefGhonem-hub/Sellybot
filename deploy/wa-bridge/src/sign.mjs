// Signatures and the shared secret.
// - Sellybot -> bridge: every API call carries the secret (X-Bridge-Secret, or Authorization: Bearer).
// - bridge -> Sellybot: every webhook body is signed: X-Bridge-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<body>")>.
//   Sellybot (WaBridge.Verify in WaBridge.cs) checks it and refuses one older than 5 minutes.
// - media links (a picture a customer sent) are signed URLs that expire, so Sellybot fetches them with no header.
import crypto from 'node:crypto';

export const TOLERANCE_SEC = 300;

export function hmacHex(secret, text) {
  return crypto.createHmac('sha256', secret).update(text).digest('hex');
}

export function sign(secret, body, t = Math.floor(Date.now() / 1000)) {
  return `t=${t},v1=${hmacHex(secret, `${t}.${body}`)}`;
}

// { ok: true } or { ok: false, why: 'no_secret' | 'no_signature' | 'too_old' | 'bad_signature' }
export function verify(secret, header, body, now = Date.now() / 1000, tolerance = TOLERANCE_SEC) {
  if (!secret) return { ok: false, why: 'no_secret' };
  const m = /^t=(\d{1,12}),v1=([0-9a-f]{64})$/.exec(String(header || '').trim());
  if (!m) return { ok: false, why: 'no_signature' };
  const t = Number(m[1]);
  if (Math.abs(now - t) > tolerance) return { ok: false, why: 'too_old' };
  return safeEqual(hmacHex(secret, `${t}.${body}`), m[2]) ? { ok: true } : { ok: false, why: 'bad_signature' };
}

// constant time; empty never matches
export function safeEqual(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string' || !a.length) return false;
  const x = Buffer.from(a), y = Buffer.from(b);
  return x.length === y.length && crypto.timingSafeEqual(x, y);
}

export function secretFrom(headers) {
  const h = headers || {};
  if (h['x-bridge-secret']) return String(h['x-bridge-secret']);
  const m = /^Bearer\s+(\S+)$/i.exec(String(h.authorization || ''));
  return m ? m[1] : '';
}

export function mediaSig(secret, instance, file, exp) {
  return hmacHex(secret, `media:${instance}:${file}:${exp}`).slice(0, 40);
}

export function mediaUrl(publicUrl, secret, instance, file, ttlSec = 6 * 3600, now = Math.floor(Date.now() / 1000)) {
  const exp = now + ttlSec;
  return `${publicUrl.replace(/\/+$/, '')}/media/${encodeURIComponent(instance)}/${encodeURIComponent(file)}?exp=${exp}&sig=${mediaSig(secret, instance, file, exp)}`;
}

export function checkMedia(secret, instance, file, exp, sig, now = Math.floor(Date.now() / 1000)) {
  const e = Number(exp);
  if (!Number.isFinite(e) || e < now) return false;
  return safeEqual(mediaSig(secret, instance, file, e), String(sig || ''));
}
