/* SellyStore: «ميعاد التوصيل» (the customer picks the delivery day at checkout) and shipping by city or weight (the sum is asked again
   as soon as the city is known). Loaded only when BOOT.shop.ship2 is there. Every day shown comes from the server (/_a/days), which
   checks it again when the order is placed; the fee always comes from /_a/quote. Works inside any template's checkout (formBits.where). */
(() => {
"use strict";
const W = window, D = document, $ = i => D.getElementById(i), { S, _, esc, A, CH } = W, X = W.BOOT.shop.ship2 || {}, DY = X.day, RT = X.rate;
const css = D.createElement("style"); css.textContent = ".ddg{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(100%,108px),1fr));gap:8px}.ddg button{font:inherit;color:inherit;background:var(--card);border:1.5px solid var(--line2,var(--line));border-radius:var(--r,12px);min-height:48px;padding:6px 8px;cursor:pointer;text-align:center;line-height:1.3}.ddg button[aria-pressed=true]{border-color:var(--ac,var(--fg));box-shadow:inset 0 0 0 1px var(--ac,var(--fg))}.ddp{margin-top:8px}.ddg button b{display:block;font-size:15px}.ddg button small{color:var(--mut);font-size:12.5px}.ddw .mutp{margin:6px 0 0}"; D.head.appendChild(css);
let L = null, LG = 0, N = 0;   /* the days for governorate LG */
const gov = () => +S.co.gov || 0, covered = () => DY && !S.co.branch && gov() > 0 && (!DY.govs.length || DY.govs.includes(gov()));
const cityOf = () => { const c = S.co, g = W.gov && W.gov(); return c.city && g && !g.c.includes(c.city) ? (c.cityT || "").trim() : c.city || ""; };
/* what the sum and the order carry (core.js how()) */
W.how2 = () => { const o = {}; if (RT) o.cq = cityOf(); if (covered() && S.co.day) { o.day = S.co.day; if (DY.parts && S.co.dpart) o.dpart = S.co.dpart; } return o; };

async function load(force) {
  const g = gov(); if (!covered() || (!force && L && LG === g)) return; const n = ++N;
  let j = null; try { const r = await fetch(W.au("/_a/days"), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ gov: g }) }); j = await r.json(); } catch (e) { }
  if (n !== N || !j || j.error) return; L = j.on ? j.days : []; LG = g; if (S.co.day && !L.some(d => d.d === S.co.day)) { S.co.day = ""; S.co.dpart = ""; } draw();
}
const box = () => {
  if (!covered()) return ""; if (!L || LG !== gov()) { setTimeout(() => load(), 0); return `<div class="sec ddw" id="ddw"><h2>${_("ميعاد التوصيل", "Delivery day")}</h2><p class="mutp">${_("لحظة...", "One moment...")}</p></div>`; }
  if (!L.length) return `<div class="sec ddw" id="ddw"><h2>${_("ميعاد التوصيل", "Delivery day")}</h2><p class="mutp">${DY.must ? _("مفيش يوم توصيل فاضي دلوقتي. جرّب بعدين أو كلّم المتجر.", "No delivery day is free right now. Try later or message the store.") : _("مفيش أيام متاحة للاختيار دلوقتي، والمتجر هيحدد الميعاد.", "No days to choose right now; the store will set the time.")}</p></div>`;
  return `<div class="sec ddw" id="ddw"><h2>${_("ميعاد التوصيل", "Delivery day")}${DY.must ? "" : ` <small class="mutp">${_("(اختياري)", "(optional)")}</small>`}</h2><div class="ddg" role="group" aria-label="${_("يوم التوصيل", "Delivery day")}">${L.map(d => { const t = _(d.t.ar, d.t.en).split(" "); return `<button type="button" data-a="dday:${d.d}" aria-pressed="${S.co.day === d.d}"><b>${esc(t[0])}</b><small dir="ltr">${esc(t.slice(1).join(" "))}</small></button>`; }).join("")}</div>${DY.parts ? `<div class="ddg ddp" role="group" aria-label="${_("الفترة", "Time of day")}">${[["am", "الصبح", "Morning"], ["pm", "بالليل", "Evening"]].map(p => `<button type="button" data-a="dpart:${p[0]}" aria-pressed="${S.co.dpart === p[0]}"><b>${_(p[1], p[2])}</b></button>`).join("")}</div>` : ""}<span class="err" id="e-day" role="alert"></span></div>`;
};
const draw = () => { const e = $("ddw"); if (e) e.outerHTML = box(); else if (covered() && S.view === "co") W.R(true); };
/* inside every checkout: after «where to» */
const fb = W.formBits, w0 = fb.where; fb.where = () => w0() + box();
A.dday = v => { S.co.day = S.co.day === v && !DY.must ? "" : v; draw(); };
A.dpart = v => { S.co.dpart = S.co.dpart === v && !DY.must ? "" : v; draw(); };
if (CH) { const g0 = CH.gov, c0 = CH.city; CH.gov = v => { S.co.day = ""; S.co.dpart = ""; L = null; g0(v); }; CH.city = v => { c0(v); if (RT) W.requote(true); }; }
/* the day first, when the store asks for one; after a refusal the days are asked again (one may have filled up) */
if (DY && A.place) { const p0 = A.place; A.place = async () => {
  if (covered() && L && L.length && DY.must && (!S.co.day || (DY.parts && !S.co.dpart))) { const e = $("e-day"); if (e) { e.textContent = !S.co.day ? _("اختار يوم التوصيل", "Choose the delivery day") : _("اختار الصبح ولا بالليل", "Choose morning or evening"); $("ddw").scrollIntoView({ block: "center" }); } return; }
  await p0(); if ($("ddw") && $("e-all") && $("e-all").textContent) load(true); }; }
/* the store's promise under the products: the fee of a governorate isn't every city's or every weight's */
if (RT && W.promise && W.BOOT.shop.ship.over !== 0) { const pr = W.promise; W.promise = () => pr().replace("</span></li>", _(" (بيختلف حسب المدينة والوزن، وبيظهر مظبوط في السلة)", " (it depends on the city and the weight; the bag shows the exact fee)") + "</span></li>"); }
if (S.view === "co") W.R(true); else if (W.retot && S.q) W.requote(true);
})();
