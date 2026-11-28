import test from 'node:test';
import assert from 'node:assert/strict';
import { Seen } from '../src/dedupe.mjs';
import { nextDelay, Pacer, typingMs } from '../src/pace.mjs';

test('Seen drops a message id it already passed on', () => {
  let t = 0; const s = new Seen(3, 1000, () => t);
  assert.equal(s.add('a'), true);
  assert.equal(s.add('a'), false);
  assert.equal(s.has('a'), true);
  s.add('b'); s.add('c'); s.add('d');           // the oldest goes once it's full
  assert.equal(s.has('a'), false);
  assert.equal(s.has('d'), true);
  t = 2000;                                      // and after its time
  assert.equal(s.has('d'), false);
  assert.equal(s.add('d'), true);
});

test('Seen keeps values too (idempotency keys -> message ids)', () => {
  const s = new Seen(10, 60000);
  s.set('sb-1-0', 'm1');
  assert.equal(s.get('sb-1-0'), 'm1');
  assert.equal(s.get('nope'), undefined);
});

const opts = { gapMin: 1000, gapMax: 1000, perMinute: 3, perHour: 5 };
test('a small gap between two messages from the same number', () => {
  assert.equal(nextDelay([], 10000, opts, () => 0), 0);
  assert.equal(nextDelay([9500], 10000, opts, () => 0), 500);
  assert.equal(nextDelay([8000], 10000, opts, () => 0), 0);
  // the gap is random between min and max
  assert.equal(nextDelay([10000], 10000, { ...opts, gapMin: 1000, gapMax: 3000 }, () => 0.5), 2000);
});

test('the per-minute and per-hour limits make the next message wait', () => {
  // 3 in the last minute: wait until the oldest of them is a minute old
  assert.equal(nextDelay([1000, 20000, 30000], 40000, opts, () => 0), 1000 + 60000 - 40000);
  // 5 in the last hour (spread over more than a minute): wait until the oldest is an hour old
  const h = [0, 100000, 200000, 300000, 400000];
  assert.equal(nextDelay(h, 500000, opts, () => 0), 3600000 - 500000);
  // old sends don't count
  assert.equal(nextDelay([0, 1, 2], 3600 * 1000 + 10, opts, () => 0), 0);
});

test('typing time grows with the text and stays short', () => {
  assert.equal(typingMs('', () => 0), 600);
  assert.ok(typingMs('x'.repeat(40), () => 0) > 600);
  assert.equal(typingMs('x'.repeat(5000), () => 0), 3500);
});

test('the Pacer sends one at a time, in order, with the waits in between', async () => {
  let now = 0; const waits = [], done = [];
  const p = new Pacer({ ...opts, maxQueue: 10, now: () => now, sleep: async ms => { waits.push(ms); now += ms; }, random: () => 0 });
  const a = p.push(async () => { done.push('a'); }), b = p.push(async () => { done.push('b'); }), c = p.push(async () => { done.push('c'); });
  await Promise.all([a, b, c]);
  assert.deepEqual(done, ['a', 'b', 'c']);
  assert.deepEqual(waits, [1000, 1000]);   // no wait before the first, the gap before each next one
});

test('a full queue says so instead of piling up', async () => {
  const p = new Pacer({ ...opts, maxQueue: 1, sleep: () => new Promise(() => {}) });
  p.push(async () => {}); p.push(async () => {});
  await assert.rejects(p.push(async () => {}), e => e.code === 'QUEUE_FULL');
});

test('one failed send does not stop the next', async () => {
  const p = new Pacer({ ...opts, gapMin: 0, gapMax: 0, maxQueue: 5, sleep: async () => {} });
  const bad = p.push(async () => { throw new Error('boom'); });
  const good = p.push(async () => 'ok');
  await assert.rejects(bad, /boom/);
  assert.equal(await good, 'ok');
});
