// A bounded memory of recent keys (message ids, idempotency keys), each forgotten after ttlMs. Map keeps insertion order,
// so the first entry is always the oldest.
export class Seen {
  constructor(max = 2000, ttlMs = 6 * 3600 * 1000, now = Date.now) {
    this.max = max; this.ttl = ttlMs; this.now = now; this.map = new Map();
  }
  #alive(k) {
    const e = this.map.get(k);
    if (!e) return null;
    if (this.now() - e.at > this.ttl) { this.map.delete(k); return null; }
    return e;
  }
  has(k) { return !!this.#alive(k); }
  get(k) { const e = this.#alive(k); return e ? e.v : undefined; }
  set(k, v) {
    this.map.delete(k);
    this.map.set(k, { at: this.now(), v });
    while (this.map.size > this.max) this.map.delete(this.map.keys().next().value);
  }
  // true when k is new (and now remembered), false for a duplicate
  add(k) { if (this.has(k)) return false; this.set(k, true); return true; }
  get size() { return this.map.size; }
}
