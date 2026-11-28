// WhatsApp ids. Sellybot keeps a customer as digits ("201001234567") and sends to "<digits>@c.us" (Wapilot's style);
// WhatsApp (Baileys) wants "<digits>@s.whatsapp.net", or "<id>@lid" when WhatsApp hides the number.

// groups, status updates, channels and broadcast lists: never a customer, never answered
export function ignoredJid(jid) {
  const j = String(jid || '').toLowerCase();
  return !j || j.endsWith('@g.us') || j.endsWith('@broadcast') || j.endsWith('@newsletter') || j.endsWith('@call');
}

// "201001234567:12@s.whatsapp.net" -> "201001234567"; null when it isn't a phone number
export function digits(jid) {
  const m = /^\+?(\d{6,20})(?::\d+)?(?:@(?:c\.us|s\.whatsapp\.net))?$/.exec(String(jid || '').trim().replace(/[\s-]/g, ''));
  return m ? m[1] : null;
}

// what Sellybot sends -> the jid to send to; null for anything we don't send to (groups, nonsense)
export function toJid(chatId) {
  const s = String(chatId || '').trim();
  if (!s || ignoredJid(s)) return null;
  const lid = /^(\d{6,25})(?::\d+)?@lid$/.exec(s);
  if (lid) return `${lid[1]}@lid`;
  const d = digits(s);
  return d ? `${d}@s.whatsapp.net` : null;
}

// a jid WhatsApp gave us -> the chat_id Sellybot keys on
export function toChatId(jid) {
  const s = String(jid || '');
  const lid = /^(\d{6,25})(?::\d+)?@lid$/.exec(s);
  if (lid) return `${lid[1]}@lid`;
  const d = digits(s);
  return d ? `${d}@c.us` : s;
}

export const isLid = jid => /@lid$/.test(String(jid || ''));
