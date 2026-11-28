import test from 'node:test';
import assert from 'node:assert/strict';
import { toJid, toChatId, digits, ignoredJid } from '../src/jid.mjs';
import { toEvent } from '../src/parse.mjs';

const NOW = 1764300000;
const msg = (over = {}) => ({
  key: { remoteJid: '201001234567@s.whatsapp.net', fromMe: false, id: 'ABC123', ...(over.key || {}) },
  messageTimestamp: over.ts ?? NOW - 5,
  pushName: 'Mona',
  message: over.message === undefined ? { conversation: 'عندكم المقاس ده؟' } : over.message,
});
const ev = (m, type = 'notify') => toEvent(m, { type, now: NOW });

test('chat ids: Sellybot sends digits / @c.us / @s.whatsapp.net / @lid, WhatsApp wants a jid', () => {
  assert.equal(toJid('201001234567'), '201001234567@s.whatsapp.net');
  assert.equal(toJid('+20 100 123 4567'), '201001234567@s.whatsapp.net');
  assert.equal(toJid('201001234567@c.us'), '201001234567@s.whatsapp.net');
  assert.equal(toJid('201001234567@s.whatsapp.net'), '201001234567@s.whatsapp.net');
  assert.equal(toJid('123456789012345@lid'), '123456789012345@lid');
  assert.equal(toJid('1203630@g.us'), null);           // never to a group
  assert.equal(toJid('status@broadcast'), null);
  assert.equal(toJid('12'), null);
  assert.equal(toJid(''), null);
  assert.equal(toChatId('201001234567:12@s.whatsapp.net'), '201001234567@c.us');   // a device suffix goes
  assert.equal(toChatId('99887766@lid'), '99887766@lid');
  assert.equal(digits('201001234567:3@s.whatsapp.net'), '201001234567');
  assert.equal(digits('x@lid'), null);
});

test('groups, status updates, channels and broadcast lists are ignored', () => {
  for (const j of ['1203630@g.us', 'status@broadcast', '123@newsletter', '1234@broadcast']) assert.equal(ignoredJid(j), true, j);
  assert.equal(ignoredJid('201001234567@s.whatsapp.net'), false);
  assert.equal(ev(msg({ key: { remoteJid: '1203630@g.us' } })).skip, 'ignored_chat');
  assert.equal(ev(msg({ key: { remoteJid: 'status@broadcast' } })).skip, 'ignored_chat');
});

test('a customer text becomes Wapilot-shaped payload (what WhatsAppBot.Parse reads)', () => {
  const r = ev(msg());
  assert.deepEqual(r.payload, {
    id: 'ABC123', chat_id: '201001234567@c.us', from_me: false, type: 'chat', text: 'عندكم المقاس ده؟',
    timestamp: NOW - 5, push_name: 'Mona', has_media: false,
  });
  assert.equal(r.download, false);
  assert.equal(ev(msg({ message: { extendedTextMessage: { text: 'لينك https://x' } } })).payload.text, 'لينك https://x');
});

test('a hidden number (@lid) uses the phone number WhatsApp gives next to it', () => {
  const r = ev(msg({ key: { remoteJid: '55443322110@lid', remoteJidAlt: '201009998887@s.whatsapp.net' } }));
  assert.equal(r.payload.chat_id, '201009998887@c.us');
  assert.equal(ev(msg({ key: { remoteJid: '55443322110@lid' } })).payload.chat_id, '55443322110@lid');
});

test('the owner typing on the phone is passed on as from_me (the bot steps aside)', () => {
  const r = ev(msg({ key: { fromMe: true }, message: { conversation: 'تمام هبعتهولك' } }));
  assert.equal(r.payload.from_me, true);
  assert.equal(r.payload.chat_id, '201001234567@c.us');
});

test('pictures are fetched for the assistant; voice notes, videos and files are only described', () => {
  const img = ev(msg({ message: { imageMessage: { caption: 'ده موجود؟', mimetype: 'image/jpeg', fileLength: 120000 } } }));
  assert.equal(img.payload.type, 'image'); assert.equal(img.payload.text, 'ده موجود؟'); assert.equal(img.payload.has_media, true);
  assert.equal(img.payload.media.mimetype, 'image/jpeg'); assert.equal(img.download, true);
  const big = ev(msg({ message: { imageMessage: { mimetype: 'image/jpeg', fileLength: 9 * 1024 * 1024 } } }));
  assert.equal(big.download, false);   // too big for the assistant: described, not fetched
  const ptt = ev(msg({ message: { audioMessage: { ptt: true, mimetype: 'audio/ogg; codecs=opus' } } }));
  assert.equal(ptt.payload.type, 'ptt'); assert.equal(ptt.download, false); assert.equal(ptt.payload.media.mimetype, 'audio/ogg; codecs=opus');
  assert.equal(ev(msg({ message: { videoMessage: { mimetype: 'video/mp4' } } })).payload.type, 'video');
  assert.equal(ev(msg({ message: { documentMessage: { mimetype: 'application/pdf' } } })).payload.type, 'document');
  assert.equal(ev(msg({ message: { stickerMessage: { mimetype: 'image/webp' } } })).payload.type, 'sticker');
  // the owner's own picture from the phone is not fetched (Sellybot only notes it)
  assert.equal(ev(msg({ key: { fromMe: true }, message: { imageMessage: { mimetype: 'image/jpeg' } } })).download, false);
});

test('wrappers (disappearing, view once) are opened', () => {
  assert.equal(ev(msg({ message: { ephemeralMessage: { message: { conversation: 'سلام' } } } })).payload.text, 'سلام');
  assert.equal(ev(msg({ message: { viewOnceMessageV2: { message: { imageMessage: { mimetype: 'image/png' } } } } })).payload.type, 'image');
});

test('reactions, edits, deletes, polls and empty messages are skipped', () => {
  assert.equal(ev(msg({ message: { reactionMessage: { text: '👍' } } })).skip, 'system');
  assert.equal(ev(msg({ message: { protocolMessage: { type: 0 } } })).skip, 'system');
  assert.equal(ev(msg({ message: { editedMessage: { message: { conversation: 'x' } } } })).skip, 'system');
  assert.equal(ev(msg({ message: { pollUpdateMessage: {} } })).skip, 'system');
  assert.equal(ev(msg({ message: null })).skip, 'empty');
  assert.equal(ev({ key: {} }).skip, 'empty');
});

test('old messages are not answered: offline ones up to 15 minutes, live ones up to a day', () => {
  assert.equal(ev(msg({ ts: NOW - 14 * 60 }), 'append').payload.id, 'ABC123');
  assert.equal(ev(msg({ ts: NOW - 16 * 60 }), 'append').skip, 'old');
  assert.equal(ev(msg({ ts: NOW - 23 * 3600 }), 'notify').payload.id, 'ABC123');
  assert.equal(ev(msg({ ts: NOW - 25 * 3600 }), 'notify').skip, 'old');
  // a Long-like timestamp (protobuf) works too
  assert.equal(ev(msg({ ts: { toNumber: () => NOW - 1 } })).payload.timestamp, NOW - 1);
});
