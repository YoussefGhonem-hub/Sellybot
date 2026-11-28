// A message from Baileys' "messages.upsert" -> the body Sellybot's webhook reads (WhatsAppBot.Parse in WhatsApp.cs reads
// Wapilot's v2 shape: { event, instance_id, payload: { id, chat_id, from_me, type, text, media: { url, mimetype } } }).
// Pure: no WhatsApp, no network. The session adds media.url after it has fetched a picture.
import { ignoredJid, toChatId, isLid, digits } from './jid.mjs';

export const MAX_DOWNLOAD = 5 * 1024 * 1024;   // the assistant's limit for one picture
const OFFLINE_MAX_AGE = 15 * 60;              // delivered after a reconnect: still answered if this recent
const LIVE_MAX_AGE = 24 * 3600;

const WRAPPERS = ['ephemeralMessage', 'viewOnceMessage', 'viewOnceMessageV2', 'viewOnceMessageV2Extension',
  'documentWithCaptionMessage', 'deviceSentMessage', 'groupMentionedMessage', 'futureProofMessage'];
const SYSTEM = new Set(['protocolMessage', 'reactionMessage', 'encReactionMessage', 'pollUpdateMessage', 'pollCreationMessage',
  'pollCreationMessageV2', 'pollCreationMessageV3', 'editedMessage', 'keepInChatMessage', 'pinInChatMessage', 'callLogMesssage',
  'bcallMessage', 'requestPhoneNumberMessage', 'encEventResponseMessage', 'eventMessage', 'albumMessage', 'associatedChildMessage']);
const NOISE = new Set(['senderKeyDistributionMessage', 'messageContextInfo']);
const TYPES = {
  conversation: 'chat', extendedTextMessage: 'chat', buttonsResponseMessage: 'chat', listResponseMessage: 'chat',
  templateButtonReplyMessage: 'chat', interactiveResponseMessage: 'chat',
  imageMessage: 'image', videoMessage: 'video', ptvMessage: 'video', audioMessage: 'audio', documentMessage: 'document', stickerMessage: 'sticker',
  locationMessage: 'location', liveLocationMessage: 'location', contactMessage: 'vcard', contactsArrayMessage: 'vcard',
};

export const num = v => (v == null ? 0 : typeof v === 'number' ? v : typeof v.toNumber === 'function' ? v.toNumber() : Number(v) || 0);

export function unwrap(content) {
  let c = content;
  for (let i = 0; i < 5 && c; i++) {
    const w = WRAPPERS.find(k => c[k] && c[k].message);
    if (!w) break;
    c = c[w].message;
  }
  return c;
}

function textOf(c, kind) {
  const x = c[kind] || {};
  switch (kind) {
    case 'conversation': return typeof c.conversation === 'string' ? c.conversation : '';
    case 'extendedTextMessage': return x.text || '';
    case 'imageMessage': case 'videoMessage': case 'documentMessage': case 'ptvMessage': return x.caption || '';
    case 'buttonsResponseMessage': return x.selectedDisplayText || x.selectedButtonId || '';
    case 'listResponseMessage': return x.title || (x.singleSelectReply && x.singleSelectReply.selectedRowId) || '';
    case 'templateButtonReplyMessage': return x.selectedDisplayText || x.selectedId || '';
    case 'interactiveResponseMessage': return (x.body && x.body.text) || '';
    default: return '';
  }
}

// -> { payload, download } or { skip: 'empty' | 'ignored_chat' | 'system' | 'old' }
// type: Baileys' upsert type ('notify' = live, 'append' = delivered after a reconnect)
export function toEvent(m, { type = 'notify', now = Math.floor(Date.now() / 1000) } = {}) {
  const key = (m && m.key) || {};
  if (!key.remoteJid || !key.id) return { skip: 'empty' };
  let jid = key.remoteJid;
  if (isLid(jid) && key.remoteJidAlt && digits(key.remoteJidAlt)) jid = key.remoteJidAlt;   // the phone number next to a hidden id
  if (ignoredJid(jid) || ignoredJid(key.remoteJid)) return { skip: 'ignored_chat' };
  const c = unwrap(m.message);
  if (!c || typeof c !== 'object') return { skip: 'empty' };
  const keys = Object.keys(c).filter(k => c[k] != null && !NOISE.has(k));
  if (!keys.length) return { skip: 'empty' };
  if (keys.some(k => SYSTEM.has(k))) return { skip: 'system' };
  const kind = keys.find(k => k === 'conversation' || TYPES[k]) || keys[0];
  const ts = num(m.messageTimestamp) || now;
  if (now - ts > (type === 'append' ? OFFLINE_MAX_AGE : LIVE_MAX_AGE)) return { skip: 'old' };

  let t = TYPES[kind] || 'unknown';
  const media = ['imageMessage', 'videoMessage', 'ptvMessage', 'audioMessage', 'documentMessage', 'stickerMessage'].includes(kind) ? c[kind] : null;
  if (kind === 'audioMessage') t = media.ptt ? 'ptt' : 'audio';
  const payload = {
    id: key.id, chat_id: toChatId(jid), from_me: !!key.fromMe, type: t, text: String(textOf(c, kind) || '').slice(0, 4000),
    timestamp: ts, push_name: m.pushName || '', has_media: !!media,
  };
  if (media) payload.media = { mimetype: media.mimetype || '' };
  // only a customer's picture is fetched (the assistant looks at it); one too big for it is only described
  const size = media ? num(media.fileLength) : 0;
  const download = !!media && !payload.from_me && (t === 'image' || t === 'sticker') && size <= MAX_DOWNLOAD;
  return { payload, download };
}
