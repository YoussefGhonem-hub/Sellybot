// Posts events to Sellybot's webhook (api/wa-webhook.ashx?src=bridge), each body signed with the shared secret.
// A failed post is tried again later (2 s, 5 s, 15 s, 30 s, 1 min, 2 min); the others go on meanwhile. The queue is bounded:
// when Sellybot is down for long, the oldest events are dropped first (an old customer message is not worth answering late).
import { sign } from './sign.mjs';

const RETRY = [2000, 5000, 15000, 30000, 60000, 120000];

export class Webhook {
  constructor({ url, secret, log, fetchFn = globalThis.fetch, now = Date.now, maxQueue = 2000, timeoutMs = 15000 }) {
    this.url = url; this.secret = secret; this.log = log; this.fetch = fetchFn; this.now = now;
    this.maxQueue = maxQueue; this.timeoutMs = timeoutMs;
    this.queue = []; this.busy = false; this.timer = null;
    this.stats = { sent: 0, failed: 0, dropped: 0, lastOkAt: null, lastError: null, lastErrorAt: null, lastStatus: null };
  }

  get pending() { return this.queue.length; }

  post(event) {
    if (!this.url) return;
    this.queue.push({ event, tries: 0, due: 0 });
    while (this.queue.length > this.maxQueue) { this.queue.shift(); this.stats.dropped++; }
    this.#kick();
  }

  // one signed POST; { ok, status, ms, error }
  async send(event) {
    const body = JSON.stringify(event), t0 = this.now();
    const ctl = new AbortController(), timer = setTimeout(() => ctl.abort(), this.timeoutMs);
    try {
      const r = await this.fetch(this.url, {
        method: 'POST', signal: ctl.signal,
        headers: { 'content-type': 'application/json; charset=utf-8', 'x-bridge-signature': sign(this.secret, body), 'user-agent': 'sellybot-wa-bridge/1' },
        body,
      });
      try { await r.text(); } catch { }
      return { ok: r.status >= 200 && r.status < 300, status: r.status, ms: this.now() - t0 };
    } catch (e) {
      return { ok: false, status: 0, ms: this.now() - t0, error: e.name === 'AbortError' ? 'timeout' : (e.cause && e.cause.code) || e.message };
    } finally { clearTimeout(timer); }
  }

  // «جرّب الاتصال» on the admin page: can the bridge reach Sellybot, and does Sellybot accept our signature?
  ping() { return this.send({ event: 'ping', at: new Date(this.now()).toISOString() }); }

  #kick(delay = 0) {
    if (this.timer) { if (delay > 0) return; clearTimeout(this.timer); this.timer = null; }
    if (delay > 0) { this.timer = setTimeout(() => { this.timer = null; this.#drain(); }, delay); this.timer.unref?.(); return; }
    this.#drain();
  }

  async #drain() {
    if (this.busy) return;
    this.busy = true;
    try {
      for (;;) {
        const now = this.now();
        const i = this.queue.findIndex(x => x.due <= now);
        if (i < 0) break;
        const item = this.queue[i];
        const r = await this.send(item.event);
        if (r.ok) {
          this.queue.splice(this.queue.indexOf(item), 1);
          this.stats.sent++; this.stats.lastOkAt = new Date(this.now()).toISOString(); this.stats.lastStatus = r.status;
          continue;
        }
        this.stats.lastError = r.error || `HTTP ${r.status}`; this.stats.lastErrorAt = new Date(this.now()).toISOString(); this.stats.lastStatus = r.status;
        // 4xx other than 408/429: Sellybot refused this body (bad signature, unknown shape): trying again won't help
        const final = (r.status >= 400 && r.status < 500 && r.status !== 408 && r.status !== 429) || item.tries >= RETRY.length;
        if (final) {
          this.queue.splice(this.queue.indexOf(item), 1); this.stats.failed++;
          this.log?.warn('webhook gave up', { event: item.event.event, instance: item.event.instance_id, status: r.status, error: r.error });
        } else { item.due = this.now() + RETRY[item.tries++]; }
      }
    } finally { this.busy = false; }
    if (this.queue.length) {
      const next = Math.min(...this.queue.map(x => x.due)) - this.now();
      this.#kick(Math.max(50, next));
    }
  }
}
