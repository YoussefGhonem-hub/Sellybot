// Pacing what a number sends, to look like a person and lower the chance WhatsApp bans it:
// one message at a time per number, a small random gap between two, a short "typing…" before a text, and a cap per
// minute and per hour. Over the cap, messages wait their turn (the API answered "queued" already); a long queue is refused.

// ms to wait before the next send, given when the last ones went (ms, oldest first)
export function nextDelay(history, now, { gapMin, gapMax, perMinute, perHour }, random = Math.random) {
  let wait = 0;
  const last = history.length ? history[history.length - 1] : null;
  if (last != null) wait = Math.max(wait, last + gapMin + Math.round((gapMax - gapMin) * random()) - now);
  const minute = history.filter(t => t > now - 60000);
  if (perMinute > 0 && minute.length >= perMinute) wait = Math.max(wait, minute[minute.length - perMinute] + 60000 - now);
  const hour = history.filter(t => t > now - 3600000);
  if (perHour > 0 && hour.length >= perHour) wait = Math.max(wait, hour[hour.length - perHour] + 3600000 - now);
  return Math.max(0, wait);
}

// how long "typing…" shows before a text: longer for a longer text, between 0.6 and 3.5 seconds
export function typingMs(text, random = Math.random) {
  const ms = 600 + String(text || '').length * 25;
  return Math.max(600, Math.min(3500, Math.round(ms * (0.85 + 0.3 * random()))));
}

export class Pacer {
  constructor({ gapMin = 1200, gapMax = 3500, perMinute = 20, perHour = 400, maxQueue = 300,
    now = Date.now, sleep = ms => new Promise(r => setTimeout(r, ms)), random = Math.random } = {}) {
    this.o = { gapMin, gapMax, perMinute, perHour };
    this.maxQueue = maxQueue; this.now = now; this.sleep = sleep; this.random = random;
    this.history = []; this.queue = []; this.running = false;
  }
  get pending() { return this.queue.length; }
  // job: async () => result. Resolves with the job's result, rejects with its error (or QUEUE_FULL).
  push(job) {
    if (this.queue.length >= this.maxQueue) return Promise.reject(Object.assign(new Error('the send queue is full'), { code: 'QUEUE_FULL' }));
    return new Promise((resolve, reject) => {
      this.queue.push({ job, resolve, reject });
      if (!this.running) this.#run();
    });
  }
  async #run() {
    this.running = true;
    try {
      while (this.queue.length) {
        const wait = nextDelay(this.history, this.now(), this.o, this.random);
        if (wait > 0) await this.sleep(wait);
        const item = this.queue.shift();
        this.history.push(this.now());
        const cut = this.now() - 3600000;
        while (this.history.length && this.history[0] <= cut) this.history.shift();
        try { item.resolve(await item.job()); } catch (e) { item.reject(e); }
      }
    } finally { this.running = false; }
  }
}
