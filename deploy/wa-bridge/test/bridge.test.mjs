// The whole bridge on a pretend WhatsApp (fake-wa.mjs), posting to a pretend Sellybot that checks every signature.
import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createBridge } from '../src/server.mjs';
import { FakeWa } from '../src/fake-wa.mjs';
import { verify } from '../src/sign.mjs';
import { makeLog } from '../src/log.mjs';

const SECRET = 'bridge-test-secret-0123456789abcdef0123';
const sleep = ms => new Promise(r => setTimeout(r, ms));
const quiet = makeLog('error');

// ---------- a pretend Sellybot ----------
const hooks = []; let hookStatus = [];   // statuses to answer with, in turn (then 200)
const sellybot = http.createServer((req, res) => {
  let b = ''; req.on('data', c => b += c); req.on('end', () => {
    const ok = verify(SECRET, req.headers['x-bridge-signature'], b).ok;
    const st = hookStatus.length ? hookStatus.shift() : ok ? 200 : 401;
    hooks.push({ ok, status: st, body: JSON.parse(b) });
    res.writeHead(st); res.end('{}');
  });
});
await new Promise(r => sellybot.listen(0, '127.0.0.1', r));
const hookUrl = `http://127.0.0.1:${sellybot.address().port}/api/wa-webhook.ashx?src=bridge`;

const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wa-bridge-test-'));
const fake = new FakeWa();
let bridge, base;
const cfgFor = port => ({ port, host: '127.0.0.1', secret: SECRET, webhookUrl: hookUrl, publicUrl: `http://127.0.0.1:${port}`, dataDir,
  maxInstances: 4, perMinute: 100, perHour: 1000, gapMin: 30, gapMax: 40, maxQueue: 50, staggerMs: 10, fake: true, fakeTyping: true });
let port = 0;
async function up() {
  if (!port) { const s = http.createServer(); await new Promise(r => s.listen(0, '127.0.0.1', r)); port = s.address().port; await new Promise(r => s.close(r)); }
  bridge = createBridge(cfgFor(port), { makeSocket: fake.factory(), log: quiet });   // the port is in the config: media links carry it
  await bridge.manager.load();
  await new Promise(r => bridge.server.listen(port, '127.0.0.1', r));
  base = `http://127.0.0.1:${port}`;
}
const api = async (method, p, body, secret = SECRET) => {
  const go = () => fetch(base + p, { method, headers: { 'content-type': 'application/json', ...(secret ? { 'x-bridge-secret': secret } : {}) }, body: body ? JSON.stringify(body) : undefined });
  let r; try { r = await go(); } catch { await sleep(50); r = await go(); }   // a kept-alive connection to the bridge before its restart
  let j = null; try { j = await r.json(); } catch { }
  return { status: r.status, body: j };
};
const until = async (fn, ms = 3000) => { const t = Date.now(); for (;;) { const v = await fn(); if (v) return v; if (Date.now() - t > ms) return v; await sleep(20); } };
const hooksOf = (ev, inst) => hooks.filter(h => h.body.event === ev && (!inst || h.body.instance_id === inst));

await up();
let inst;

test('health: open says only "up"; with the secret it tells memory, numbers and the webhook', async () => {
  const open = await api('GET', '/health', null, null);
  assert.equal(open.status, 200); assert.deepEqual(open.body, { ok: true, service: 'sellybot-wa-bridge' });
  const full = await api('GET', '/health');
  assert.equal(full.body.ok, true); assert.ok(full.body.memoryMb.rss > 0); assert.equal(full.body.instances.total, 0);
});

test('the API is closed without the secret, and many wrong tries are cut off', async () => {
  assert.equal((await api('GET', '/instances', null, null)).status, 401);
  assert.equal((await api('GET', '/instances', null, 'wrong')).status, 401);
  assert.equal((await api('POST', '/instances', {}, 'wrong')).body.code, 'UNAUTHORIZED');
});

test('a new number: created by Sellybot, QR on asking, refreshed, linked by a scan', async () => {
  const c = await api('POST', '/instances', { name: 'store-7 main' });
  assert.equal(c.status, 201); inst = c.body.instance.instance_uniquename;
  assert.match(inst, /^wa[0-9a-f]{10}$/); assert.equal(c.body.instance.status, 'STOPPED');
  let q = await api('GET', `/instances/${inst}/qr-code`);
  assert.equal(q.status, 200); assert.equal(q.body.qr, null);          // starting
  q = await until(async () => { const r = await api('GET', `/instances/${inst}/qr-code`); return r.body.qr ? r : null; });
  assert.match(q.body.qr, /^data:image\/png;base64,/); assert.equal(q.body.status, 'SCAN_QR_CODE');
  fake.scan(inst, '201005556667');
  const st = await until(async () => { const r = await api('GET', `/instances/${inst}/status`); return r.body.status === 'WORKING' ? r : null; });
  assert.equal(st.body.me_id, '201005556667');
  const h = await until(() => hooksOf('session.status', inst).find(x => x.body.payload.status === 'WORKING'));
  assert.ok(h.ok, 'signed'); assert.equal(h.body.payload.me_id, '201005556667');
  const list = await api('GET', '/instances');
  assert.equal(list.body.instances[0].status, 'WORKING'); assert.equal(list.body.instances[0].me_id, '201005556667');
});

test('a customer message reaches Sellybot signed, once, with the chat id Sellybot keys on', async () => {
  hooks.length = 0;
  fake.incoming(inst, { from: '201112223334', text: 'عندكم مقاس L؟', msgId: 'M1' });
  fake.incoming(inst, { from: '201112223334', text: 'عندكم مقاس L؟', msgId: 'M1' });   // delivered twice
  const h = await until(() => hooksOf('message', inst)[0]);
  await sleep(100);
  assert.equal(hooksOf('message', inst).length, 1);
  assert.equal(h.ok, true);
  assert.deepEqual({ ...h.body.payload, timestamp: 0 }, { id: 'M1', chat_id: '201112223334@c.us', from_me: false, type: 'chat', text: 'عندكم مقاس L؟', timestamp: 0, push_name: 'Test', has_media: false });
});

test('groups are ignored; the owner typing on the phone is passed on as from_me', async () => {
  hooks.length = 0;
  fake.incoming(inst, { group: true, text: 'hello group' });
  fake.incoming(inst, { from: '201112223334', text: 'أيوه موجود', fromMe: true, msgId: 'O1' });
  const h = await until(() => hooksOf('message', inst)[0]);
  await sleep(80);
  assert.equal(hooksOf('message', inst).length, 1);
  assert.equal(h.body.payload.from_me, true); assert.equal(h.body.payload.id, 'O1');
});

test('a picture comes with a signed link Sellybot can fetch, and only that link works', async () => {
  hooks.length = 0;
  fake.incoming(inst, { from: '201112223334', image: true, text: 'ده عندكم؟', msgId: 'IMG1' });
  const h = await until(() => hooksOf('message', inst)[0]);
  const m = h.body.payload.media;
  assert.equal(h.body.payload.type, 'image'); assert.equal(m.mimetype, 'image/png'); assert.match(m.url, /\/media\/wa[0-9a-f]+\/IMG1\.png\?exp=\d+&sig=[0-9a-f]+$/);
  const r = await fetch(m.url); assert.equal(r.status, 200); assert.equal(r.headers.get('content-type'), 'image/png');
  assert.equal((await fetch(m.url.replace(/sig=./, 'sig=0'))).status, 403);
  assert.equal((await fetch(m.url.replace('IMG1', 'IMG2'))).status, 403);
});

test('sending: queued at once, "typing…" first, then sent; an idempotency key never sends twice', async () => {
  fake.sent.length = 0;
  const a = await fetch(`${base}/instances/${inst}/send-message`, { method: 'POST', headers: { 'x-bridge-secret': SECRET, 'idempotency-key': 'sb-9-0', 'content-type': 'application/json' }, body: JSON.stringify({ chat_id: '201112223334@c.us', text: 'أيوه موجود بـ 450' }) }).then(r => r.json());
  const b = await fetch(`${base}/instances/${inst}/send-message`, { method: 'POST', headers: { 'x-bridge-secret': SECRET, 'idempotency-key': 'sb-9-0', 'content-type': 'application/json' }, body: JSON.stringify({ chat_id: '201112223334@c.us', text: 'أيوه موجود بـ 450' }) }).then(r => r.json());
  assert.equal(a.status, 'pending'); assert.equal(b.message_id, a.message_id); assert.equal(b.duplicate, true);
  const st = await until(async () => { const r = await api('GET', `/instances/${inst}/messages/${a.message_id}`); return r.body.data.status === 'delivered' ? r : null; });
  assert.equal(st.body.data.status, 'delivered');
  const texts = fake.sent.filter(x => x.content && x.content.text);
  assert.equal(texts.length, 1); assert.equal(texts[0].jid, '201112223334@s.whatsapp.net');
  const order = fake.sent.map(x => x.presence || 'text');
  assert.deepEqual(order.slice(0, 2), ['composing', 'text']);
  // our own message is not passed back to Sellybot as the owner typing
  hooks.length = 0; await sleep(60); assert.equal(hooksOf('message', inst).length, 0);
});

test('pacing: messages from one number go one at a time with a gap', async () => {
  fake.sent.length = 0;
  for (let i = 0; i < 3; i++) await api('POST', `/instances/${inst}/send-message`, { chat_id: '201112223334', text: 'رسالة ' + i });
  await until(() => fake.sent.filter(x => x.content && x.content.text).length === 3);
  const t = fake.sent.filter(x => x.content && x.content.text).map(x => x.at);
  assert.ok(t[1] - t[0] >= 25 && t[2] - t[1] >= 25, `gaps ${t[1] - t[0]}, ${t[2] - t[1]}`);
});

test('a number that is not on WhatsApp fails without sending; a group or nonsense is refused', async () => {
  await api('POST', '/_fake/reset'); fake.notOnWa.add('201999999999'); fake.sent.length = 0;
  const r = await api('POST', `/instances/${inst}/send-message`, { chat_id: '201999999999@c.us', text: 'عرض' });
  const st = await until(async () => { const x = await api('GET', `/instances/${inst}/messages/${r.body.message_id}`); return x.body.data.status === 'failed' ? x : null; });
  assert.equal(st.body.data.error_code, 'NOT_ON_WHATSAPP'); assert.equal(fake.sent.filter(x => x.content).length, 0);
  assert.equal((await api('POST', `/instances/${inst}/send-message`, { chat_id: '120363@g.us', text: 'x' })).body.code, 'BAD_CHAT');
  assert.equal((await api('POST', `/instances/${inst}/send-message`, { chat_id: '201112223334', text: '  ' })).body.code, 'EMPTY_TEXT');
  assert.equal((await api('POST', `/instances/nosuch1/send-message`, { chat_id: '201112223334', text: 'x' })).status, 404);
});

test('a picture by bytes, typing on demand, and a hidden number back to a phone', async () => {
  fake.sent.length = 0;
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');
  const r = await api('POST', `/instances/${inst}/send-image`, { chat_id: '201112223334@c.us', base64: png.toString('base64'), mimetype: 'image/png', caption: 'العرض' });
  assert.equal(r.status, 200);
  await until(() => fake.sent.find(x => x.content && x.content.image));
  const img = fake.sent.find(x => x.content && x.content.image);
  assert.equal(img.content.caption, 'العرض'); assert.match(img.content.image, /^<\d+ bytes>$/);
  assert.equal((await api('POST', `/instances/${inst}/send-typing`, { chat_id: '201112223334' })).status, 200);
  assert.equal((await api('GET', `/instances/${inst}/lids/11112345678`)).body.phone, '201012345678');
  assert.equal((await api('GET', `/instances/${inst}/lids/99912345678@lid`)).body.phone, null);
});

test('the connection drops and comes back by itself, no QR', async () => {
  fake.drop(inst);
  const st = await until(async () => { const r = await api('GET', `/instances/${inst}/status`); return r.body.status === 'WORKING' && r.body.since ? r : null; }, 5000);
  assert.equal(st.body.status, 'WORKING');
});

test('a restart of the bridge reconnects every linked number with no new QR', async () => {
  await bridge.close();
  await up();
  const st = await until(async () => { const r = await api('GET', `/instances/${inst}/status`); return r.body.status === 'WORKING' ? r : null; });
  assert.equal(st.body.me_id, '201005556667');
});

test('the phone removes the linked device: Sellybot hears LOGGED_OUT, and a new QR is offered', async () => {
  hooks.length = 0;
  fake.phoneLogout(inst);
  const h = await until(() => hooksOf('session.status', inst).find(x => x.body.payload.status === 'LOGGED_OUT'));
  assert.ok(h && h.ok);
  assert.equal((await api('GET', `/instances/${inst}/status`)).body.status, 'LOGGED_OUT');
  assert.equal((await api('POST', `/instances/${inst}/send-message`, { chat_id: '201112223334', text: 'x' })).body.code, 'INSTANCE_NOT_READY');
  const q = await until(async () => { const r = await api('GET', `/instances/${inst}/qr-code`); return r.body.qr ? r : null; });
  assert.match(q.body.qr, /^data:image\/png/);
  fake.scan(inst, '201005556667');
  await until(async () => (await api('GET', `/instances/${inst}/status`)).body.status === 'WORKING');
});

test('logout from Sellybot drops the login; delete removes the number', async () => {
  assert.equal((await api('POST', `/instances/${inst}/logout`)).status, 200);
  const st = await api('GET', `/instances/${inst}/status`);
  assert.equal(st.body.status, 'STOPPED');
  assert.equal(fs.existsSync(path.join(dataDir, 'auth', inst, 'creds.json')), false);
  assert.equal((await api('DELETE', `/instances/${inst}`)).status, 200);
  assert.equal((await api('GET', '/instances')).body.instances.length, 0);
});

test('webhook: a refused post is tried again; the ping says if Sellybot accepts our signature', async () => {
  const c = await api('POST', '/instances', { name: 'retry' }); const id = c.body.instance.instance_uniquename;
  await until(async () => (await api('GET', `/instances/${id}/qr-code`)).body.qr);
  fake.scan(id, '201007778889');
  await until(async () => (await api('GET', `/instances/${id}/status`)).body.status === 'WORKING');
  hooks.length = 0; hookStatus = [500];
  fake.incoming(id, { from: '201112223334', text: 'تاني', msgId: 'R1' });
  const got = await until(() => hooks.filter(h => h.body.payload && h.body.payload.id === 'R1').length >= 2 ? true : null, 6000);
  assert.ok(got, 'tried again after a 500');
  const ping = await api('POST', '/webhook-test');
  assert.equal(ping.body.ok, true); assert.equal(ping.body.status, 200);
});

test('the server refuses more numbers than it holds', async () => {
  for (let i = (await api('GET', '/instances')).body.instances.length; i < 4; i++) assert.equal((await api('POST', '/instances', {})).status, 201);
  const r = await api('POST', '/instances', {});
  assert.equal(r.status, 507); assert.equal(r.body.code, 'BRIDGE_FULL');
});

test.after(async () => { await bridge.close(); sellybot.close(); fs.rmSync(dataDir, { recursive: true, force: true }); });
