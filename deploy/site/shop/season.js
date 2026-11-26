/* SellyStore: a season look laid over any template. The owner picks it in «شكل المتجر» (BOOT.look.season = {k, from, to, offer}); it is
   drawn between its two days (the store's own day, BOOT.cday, not the phone's clock) and in the owner's private preview before it starts.
   It adds, through the engine only: colours that go with the owner's colour (--sea-b band, --sea-f its text, --sea-d drawings), a greeting
   in the announcement bar with a few drawings in inline SVG (still when the visitor or the owner turned motion off), the hero's headline
   when the owner typed none, and a badge on the products of the offer the owner picked. No countdown, no promise of its own: the link to
   the offers shows only while that offer really runs. A template may restyle .seab and .bdg.sea (TEMPLATES.md). */
(() => {
"use strict";
const W = window, D = document, B = W.BOOT;
/* n name · g greeting · h headline · b badge · c [band, drawings] · d drawings at the start and the end */
const SEAS = {
  ramadan: { n: ["رمضان", "Ramadan"], g: ["رمضان كريم", "Ramadan Kareem"], h: ["رمضان كريم", "Ramadan Kareem"], b: ["عرض رمضان", "Ramadan offer"], c: ["#1f2d6b", "#f2b631"], d: ["lantern", "moon"] },
  eidfitr: { n: ["عيد الفطر", "Eid al-Fitr"], g: ["عيد فطر سعيد، كل سنة وانت طيب", "Eid Mubarak"], h: ["عيد سعيد", "Eid Mubarak"], b: ["عرض العيد", "Eid offer"], c: ["#0f6b4f", "#f6c445"], d: ["moon", "balloon"] },
  eidadha: { n: ["عيد الأضحى", "Eid al-Adha"], g: ["عيد أضحى مبارك، كل سنة وانت طيب", "Eid al-Adha Mubarak"], h: ["عيد أضحى مبارك", "Eid Mubarak"], b: ["عرض العيد", "Eid offer"], c: ["#0b5f6b", "#f0c24b"], d: ["lantern", "moon"] },
  blackfriday: { n: ["الجمعة البيضاء", "White Friday"], g: ["موسم الجمعة البيضاء", "White Friday season"], h: ["الجمعة البيضاء", "White Friday"], b: ["الجمعة البيضاء", "White Friday"], c: ["#141414", "#ffd60a"], d: ["tag", "spark"] },
  valentine: { n: ["عيد الحب", "Valentine's Day"], g: ["عيد حب سعيد", "Happy Valentine's Day"], h: ["هدية لحد بتحبه", "A gift for someone you love"], b: ["هدية عيد الحب", "Valentine's pick"], c: ["#a8124a", "#ffc2d6"], d: ["heart", "heart"] },
  mothersday: { n: ["عيد الأم", "Mother's Day"], g: ["كل سنة وكل أم طيبة", "Happy Mother's Day"], h: ["هدية لست الحبايب", "A gift for Mum"], b: ["هدية عيد الأم", "Mother's Day pick"], c: ["#9c355d", "#ffd3e0"], d: ["flower", "heart"] },
  school: { n: ["المدارس", "Back to school"], g: ["سنة دراسية سعيدة", "Happy new school year"], h: ["جاهزين للمدارس", "Ready for school"], b: ["للمدارس", "Back to school"], c: ["#1d4fa3", "#ffcb3d"], d: ["pencil", "star"] },
  shamnessim: { n: ["شم النسيم", "Sham El-Nessim"], g: ["شم نسيم سعيد", "Happy Sham El-Nessim"], h: ["أهلًا بالربيع", "Hello spring"], b: ["شم النسيم", "Spring pick"], c: ["#2b733a", "#ffd447"], d: ["egg", "flower"] },
  summer: { n: ["الصيف", "Summer"], g: ["صيف حلو", "Have a lovely summer"], h: ["أهلًا بالصيف", "Hello summer"], b: ["عرض الصيف", "Summer offer"], c: ["#0a6496", "#ffcc33"], d: ["sun", "wave"] },
  newyear: { n: ["رأس السنة", "New Year"], g: ["سنة جديدة سعيدة", "Happy New Year"], h: ["سنة جديدة سعيدة", "Happy New Year"], b: ["عرض رأس السنة", "New Year offer"], c: ["#1b1f4b", "#e9c46a"], d: ["confetti", "star"] }
};
/* the drawings: d = the drawings' colour, f = the band's text colour (faint), w = a light part */
const ART = {
  lantern: '<svg viewBox="0 0 32 44" class="sw"><path class="ln" d="M16 0v5"/><path class="d" d="M11 5h10l2 4H9z"/><path class="d" d="M9 9h14l-2 18H11z"/><path class="w" d="M13.4 12.5h5.2l-1 11h-3.2z"/><path class="d" d="M10 27h12l-2 4h-8zM14 31h4l-1 5h-2z"/></svg>',
  moon: '<svg viewBox="0 0 40 40"><path class="d" d="M23 5a15 15 0 1 0 12 24A12 12 0 1 1 23 5z"/><path class="d tw" d="m31 8 1.4 2.9 3.2.5-2.3 2.2.5 3.2-2.8-1.5-2.9 1.5.6-3.2-2.3-2.2 3.2-.5z"/><circle class="f tw2" cx="9" cy="9" r="1.4"/></svg>',
  balloon: '<svg viewBox="0 0 32 44" class="fl"><path class="ln" d="M16 30c-2 4 2 6 0 10"/><ellipse class="d" cx="16" cy="15" rx="10" ry="12.5"/><path class="w" d="M11 9a6 6 0 0 1 4-3.4"/><path class="d" d="M14 27h4l-2 3z"/></svg>',
  tag: '<svg viewBox="0 0 40 40"><path class="d" d="M5 7v12l16 16 14-14L19 5H7a2 2 0 0 0-2 2z"/><circle class="b" cx="12" cy="12" r="2.8"/><path class="b" d="m17 22 8-8M18.5 16.5h.01M23.5 21.5h.01" stroke-width="2.6"/></svg>',
  spark: '<svg viewBox="0 0 40 40"><path class="d tw" d="M20 3c1.5 8 4 10.5 12 12-8 1.5-10.5 4-12 12-1.5-8-4-10.5-12-12 8-1.5 10.5-4 12-12z"/><path class="f tw2" d="M33 26c.6 3 1.6 4 4.6 4.6-3 .6-4 1.6-4.6 4.6-.6-3-1.6-4-4.6-4.6 3-.6 4-1.6 4.6-4.6z"/></svg>',
  heart: '<svg viewBox="0 0 40 40" class="fl"><path class="d" d="M20 34S7 26 7 16.5A6.8 6.8 0 0 1 20 13a6.8 6.8 0 0 1 13 3.5C33 26 20 34 20 34z"/><path class="w" d="M12.5 15.5a3.4 3.4 0 0 1 3-3"/></svg>',
  flower: '<svg viewBox="0 0 40 40"><g class="d"><circle cx="20" cy="11" r="6"/><circle cx="28.6" cy="17.2" r="6"/><circle cx="25.3" cy="27.3" r="6"/><circle cx="14.7" cy="27.3" r="6"/><circle cx="11.4" cy="17.2" r="6"/></g><circle class="b" cx="20" cy="20" r="4.4"/></svg>',
  pencil: '<svg viewBox="0 0 40 40"><path class="d" d="m9 31 2.5-8.5L27 7l6 6-15.5 15.5z"/><path class="w" d="m9 31 2.5-8.5 6 6z"/><path class="b" d="m24 10 6 6"/></svg>',
  star: '<svg viewBox="0 0 40 40"><path class="d tw" d="m20 5 4.3 8.8 9.7 1.4-7 6.8 1.7 9.6-8.7-4.6-8.7 4.6 1.7-9.6-7-6.8 9.7-1.4z"/></svg>',
  egg: '<svg viewBox="0 0 40 40"><path class="d" d="M20 4c7 0 12 11 12 19a12 12 0 0 1-24 0C8 15 13 4 20 4z"/><path class="b" d="m9 21 4-3 4 3 3-3 3 3 4-3 4 3" fill="none" stroke-width="2.2"/><circle class="w" cx="15" cy="13" r="1.6"/><circle class="w" cx="25" cy="28" r="1.6"/></svg>',
  sun: '<svg viewBox="0 0 40 40"><g class="sp"><circle class="d" cx="20" cy="20" r="8"/><path class="ln2" d="M20 3v5M20 32v5M3 20h5M32 20h5M8 8l3.5 3.5M28.5 28.5 32 32M32 8l-3.5 3.5M11.5 28.5 8 32"/></g></svg>',
  wave: '<svg viewBox="0 0 44 40"><path class="ln2" d="M2 17c5 0 5-4 10-4s5 4 10 4 5-4 10-4 5 4 10 4M2 27c5 0 5-4 10-4s5 4 10 4 5-4 10-4 5 4 10 4"/></svg>',
  confetti: '<svg viewBox="0 0 40 40"><rect class="d tw" x="6" y="8" width="5" height="8" rx="1" transform="rotate(-20 8 12)"/><circle class="w tw2" cx="27" cy="9" r="2.6"/><rect class="d" x="24" y="24" width="5" height="8" rx="1" transform="rotate(25 26 28)"/><circle class="d tw2" cx="12" cy="29" r="2.2"/><path class="d tw" d="m33 17 1.2 2.4 2.6.4-1.9 1.8.5 2.6-2.4-1.2-2.4 1.2.5-2.6-1.9-1.8 2.6-.4z"/></svg>'
};
/* ---- colours (the same rule as the engine: text at 4.5 at least, in light and dark) ---- */
const rgb = h => [1, 3, 5].map(i => parseInt(h.substr(i, 2), 16));
const hx = a => "#" + a.map(x => ("0" + Math.max(0, Math.min(255, Math.round(x))).toString(16)).slice(-2)).join("");
const lum = h => { const a = rgb(h).map(v => { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); }); return .2126 * a[0] + .7152 * a[1] + .0722 * a[2]; };
const con = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };
const mix = (a, b, f) => { const x = rgb(a), y = rgb(b); return hx(x.map((v, i) => v + (y[i] - v) * f)); };
const ok = h => /^#[0-9a-f]{6}$/i.test(h || "");
/* the band takes a little of the owner's colour, so the season goes with the store; then its text is made readable */
const paint = (s, brand, th) => {
  let b = ok(brand) ? mix(s.c[0], brand, .22) : s.c[0]; if (th === "dark") b = mix(b, "#000000", .22);
  let f = con(b, "#ffffff") >= con(b, "#141414") ? "#ffffff" : "#141414", i = 0;
  while (con(b, f) < 4.6 && i++ < 40) b = mix(b, f === "#ffffff" ? "#000000" : "#ffffff", .06);
  let d = s.c[1]; i = 0; while (con(b, d) < 2.6 && i++ < 30) d = mix(d, f, .1);
  return { b, f, d };
};
const day = () => B.cday || new Date().toISOString().slice(0, 10);
const cur = () => { const s = (W.BOOT.look || {}).season; return s && SEAS[s.k] && s.from && s.to ? s : null; };
/* on for customers between its days; in the preview from now until it ends */
const on = () => { const s = cur(); if (!s) return false; const t = day(); return t <= s.to && (s.from <= t || !!B.preview); };
const fmtD = iso => { try { return new Date(iso + "T12:00:00").toLocaleDateString(W.S && W.S.lang === "en" ? "en-GB" : "ar-EG", { day: "numeric", month: "long" }); } catch (e) { return iso; } };
let css = false;
const style = () => { if (css) return; css = true; const e = D.createElement("style"); e.id = "sea-css"; e.textContent = CSS; D.head.appendChild(e); };
const CSS = `
#st .annb.seab{position:relative;display:flex;align-items:center;justify-content:center;gap:10px;min-height:46px;padding:6px 14px 9px;background:var(--sea-b);color:var(--sea-f);font-size:14px;font-weight:600;line-height:1.45;text-align:center}
#st .annb.seab::before{display:none}
#st .annb.seab::after{content:"";position:absolute;inset-inline:0;bottom:2px;height:4px;background:radial-gradient(circle,var(--sea-d) 1.5px,transparent 2px) center/16px 4px repeat-x;opacity:.75;pointer-events:none}
#st .seab .sea-t{display:flex;flex-wrap:wrap;align-items:baseline;justify-content:center;gap:2px 8px;min-width:0}
#st .seab .sea-t b{font-weight:800}
#st .seab .sea-t i{font-style:normal;font-weight:500;opacity:.92}
#st .seab .sea-t i::before{content:"·";margin-inline-end:8px}
#st .seab a{color:inherit;text-decoration:underline;text-underline-offset:3px}
#st .seab .sea-pv{font-weight:500;font-size:12px;opacity:.9}
#st .seab .sea-d{flex:none;display:inline-flex;width:30px;height:34px;pointer-events:none}
#st .seab .sea-d svg{width:100%;height:100%;overflow:visible}
#st .seab .sea-x{display:none}
@media (min-width:700px){#st .seab .sea-x{display:inline-flex}}
@media (max-width:699px){#st .seab .sea-t i{flex-basis:100%}#st .seab .sea-t i::before{display:none}}
@media (max-width:380px){#st .annb.seab{font-size:13px;gap:6px;padding-inline:8px}#st .seab .sea-d{width:24px;height:28px}}
#st .seab .d{fill:var(--sea-d)}#st .seab .f{fill:var(--sea-f);opacity:.55}#st .seab .w{fill:var(--sea-f);opacity:.35}#st .seab .b{fill:var(--sea-b);stroke:var(--sea-b);stroke-linecap:round}
#st .seab .ln{stroke:var(--sea-d);stroke-width:1.6;fill:none}#st .seab .ln2{stroke:var(--sea-d);stroke-width:2.6;stroke-linecap:round;fill:none}
#st .bdg.sea{background:var(--sea-b);color:var(--sea-f);border-color:transparent}
@media (prefers-reduced-motion:no-preference){
#st:not([data-mo=none]) .seab .sw{transform-origin:50% 0;animation:sea-sw 4.2s ease-in-out infinite}
#st:not([data-mo=none]) .seab .fl{animation:sea-fl 3.6s ease-in-out infinite}
#st:not([data-mo=none]) .seab .tw{animation:sea-tw 2.8s ease-in-out infinite}
#st:not([data-mo=none]) .seab .tw2{animation:sea-tw 2.8s ease-in-out -1.4s infinite}
#st:not([data-mo=none]) .seab .sp{transform-origin:50% 50%;animation:sea-sp 18s linear infinite}
#st[data-mo=calm] .seab *{animation-duration:7s!important}
}
@keyframes sea-sw{0%,100%{transform:rotate(-5deg)}50%{transform:rotate(5deg)}}
@keyframes sea-fl{0%,100%{transform:translateY(0)}50%{transform:translateY(-3px)}}
@keyframes sea-tw{0%,100%{opacity:1}50%{opacity:.45}}
@keyframes sea-sp{to{transform:rotate(360deg)}}
`;
const art = (k, x) => `<span class="sea-d${x ? " sea-x" : ""}" aria-hidden="true">${ART[k] || ""}</span>`;
W.SEA = {
  list: SEAS, on,
  /* from the engine's tokens(): the colours on the store, the headline when the owner typed none */
  tk(st, k, th) {
    const LK = W.BOOT.look, h = LK.hero || (LK.hero = {}), t = h.title || ["", ""], s = cur(), a = on();
    if (a) { const sp = SEAS[s.k], c = paint(sp, LK.color || k.ac, th); style(); st.style.setProperty("--sea-b", c.b); st.style.setProperty("--sea-f", c.f); st.style.setProperty("--sea-d", c.d); st.dataset.sea = s.k; if ((!t[0] && !t[1]) || h._sea) { h.title = sp.h.slice(); h._sea = 1; } }
    else { delete st.dataset.sea; if (h._sea) { h.title = ["", ""]; delete h._sea; } }
  },
  /* the announcement bar: the greeting, the offers while the owner's offer runs, then the owner's own line */
  ann(t, a) {
    if (!on()) return ""; const s = cur(), sp = SEAS[s.k], _ = W._, esc = W.esc;
    const of = s.offer && (W.OFFERS || []).some(o => o.i === s.offer) ? `<a href="${W.url("list", "sale")}" data-a="go:list:sale">${_("شوف العروض", "See the offers")}</a>` : "";
    const own = t ? (a && a.link ? `<i><a ${W.link(a.link)}>${esc(t)}</a></i>` : `<i>${esc(t)}</i>`) : "";
    const pv = B.preview && day() < s.from ? `<span class="sea-pv">${_("(معاينة: الزباين هيشوفوه من " + fmtD(s.from) + ")", "(Preview: customers see it from " + fmtD(s.from) + ")")}</span>` : "";
    return `<div class="annb seab" data-sea="${s.k}">${art(sp.d[0])}${art(sp.d[1], 1)}<span class="sea-t"><b>${esc(_(sp.g))}</b>${of}${own}${pv}</span>${art(sp.d[1], 1)}${art(sp.d[0])}</div>`;
  },
  /* the products of the owner's offer, while it runs */
  badge(p) { if (!on()) return ""; const s = cur(); return s.offer && p.of === s.offer && (W.OFFERS || []).some(o => o.i === s.offer) ? `<em class="bdg sea">${W.esc(W._(SEAS[s.k].b))}</em>` : ""; }
};
if (W.R && D.getElementById("sc")) W.R(true);
})();
