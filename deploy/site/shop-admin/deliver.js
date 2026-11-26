/* «متجري»: «بوصّل بنفسي» (ShopSelf.cs): who delivers the orders (a name and a mobile, ten at most) and a line the customer sees; on an order,
   who delivers it. Its card opens «هتوصّل الأوردرات إزاي؟» in «الشحن» (ship.js draws the section). Off, or nobody written: nothing shows to anyone. */
(() => {
"use strict";
const SB = window.SbShop, { esc, fld, run, say, post, pane } = SB, D = document, A = SB.acts;
const S = () => (((SB.state() || {}).settings || {}).ship2 || {}).self || {}, who = () => (S().who || []).filter(w => w && w.n), on = () => !!S().on && who().length > 0;
const css = D.createElement("style"); css.textContent = ".dl-who{display:grid;gap:8px}.dl-r{display:grid;grid-template-columns:1fr 1fr auto;gap:8px;align-items:end}.dl-r .sm-f{margin:0}.dl-r+.dl-r{border-top:1px dashed var(--line);padding-top:8px}.dl-pk{display:flex;gap:6px;flex-wrap:wrap}.dl-pk button{border:1px solid var(--line);background:var(--card);color:var(--ink);border-radius:99px;padding:6px 14px;min-height:38px;cursor:pointer;font:inherit}.dl-pk button[aria-pressed=true]{border-color:var(--brand);background:color-mix(in srgb,var(--brand) 12%,var(--card));font-weight:600}@media (max-width:520px){.dl-r{grid-template-columns:1fr auto}.dl-r .sm-f:first-child{grid-column:1/-1}}"; D.head.appendChild(css);
const reTab = () => { if (SB.view() === "ship") SB.show("ship"); };
const mobile = v => { const d = String(v || "").replace(/[٠-٩]/g, c => "٠١٢٣٤٥٦٧٨٩".indexOf(c)).replace(/\D/g, ""); return /^(20)?0?1[0125]\d{8}$/.test(d); };

/* ---------- its card in «هتوصّل الأوردرات إزاي؟» ---------- */
SB.selfCard = () => { const l = who(), line = (S().line || [])[0] || ""; return `<div class="sh-c" data-c="self"><div class="row"><b>🛵 بوصّل بنفسي</b><span class="space"></span><span class="sm-pill ${on() ? "ok" : "g"}">${on() ? "شغّال" : "مقفول"}</span></div>
  <p class="sh-k">إنت أو حد من عندك بيوصّل الأوردرات. اكتب مين بيوصّل ورقمه، ولما الأوردر يخرج الزبون يعرف مين جايله ورقمه، في صفحة متابعة الطلب وعلى الواتساب.</p>
  ${on() ? `<p class="sm-hint">${l.map(w => esc(w.n)).join("، ")}${line ? " · " + esc(line) : ""}</p>` : ""}<div class="row"><button type="button" class="btn ${on() ? "ghost " : ""}sm" data-do="dlSet">${on() ? "الإعدادات" : "مين بيوصّل؟"}</button></div></div>`; };

A.dlSet = () => {
  const s = { on: S().on !== false || !who().length, who: who().map(w => ({ n: w.n, p: w.p })), line: [...(S().line || ["", ""])] }; if (!s.who.length) s.who.push({ n: "", p: "" }); let dirty = false;
  const keep = () => { const r = SB.read(p.el); s.on = !!r.on; s.who = (r.who || []).map(w => ({ n: (w.n || "").trim(), p: (w.p || "").trim() })); s.line = r.line || s.line; };
  const p = pane({
    title: "بوصّل بنفسي", cls: "mid", dirty: () => dirty, after: e => e.querySelector(".sm-p-b").addEventListener("input", () => { dirty = true; }),
    html: () => `<div class="sm-form">${SB.tog("on", s.on, "شغّال", "لما يبقى مقفول مفيش حاجة بتظهر للزبون")}
      <h4>مين بيوصّل</h4><div class="dl-who">${s.who.map((w, i) => `<div class="dl-r">${fld("who." + i + ".n", "الاسم", w.n, { max: 40 })}${fld("who." + i + ".p", "الموبايل", w.p, { mode: "tel", dir: "ltr", max: 20 })}<button type="button" class="btn ghost sm" data-do="rm:${i}" aria-label="شيل ${esc(w.n || "السطر ده")}">شيل</button></div>`).join("")}</div>
      ${s.who.length < 10 ? '<button type="button" class="btn ghost sm" data-do="add">+ ضيف حد</button>' : '<p class="sm-hint">أقصى عدد 10.</p>'}
      ${SB.two("line", "سطر يشوفه الزبون (اختياري)", s.line, { max: 120, ph: "مثلًا: التوصيل من 2 الضهر لـ 10 بالليل" })}
      <p class="sm-hint">الزبون بيشوف اسم اللي هيوصّله ورقمه والسطر ده بس، ولما الأوردر يخرج بس. أول واحد في القايمة هو اللي بيظهر لو ماخترتش في الأوردر.</p></div>`,
    foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
    acts: {
      add: () => { keep(); s.who.push({ n: "", p: "" }); dirty = true; p.draw(); }, rm: i => { keep(); s.who.splice(+i, 1); if (!s.who.length) s.who.push({ n: "", p: "" }); dirty = true; p.draw(); },
      go: b => run(b, async () => {
        keep(); const l = s.who.filter(w => w.n || w.p); const bad = l.find(w => w.n.length < 2 || !mobile(w.p)); if (bad) throw new Error(bad.n.length < 2 ? "اكتب اسم كل واحد (حرفين على الأقل)." : "رقم " + bad.n + " مش موبايل مصري صح.");
        if (s.on && !l.length) throw new Error("اكتب اسم ورقم واحد على الأقل.");
        await post("settings", { settings: { ship2: { self: { on: s.on, who: l, line: s.line } } } }); await SB.reload().catch(() => { }); dirty = false; p.close(true); say("اتحفظ ✅"); reTab();
      })
    }
  });
};

/* ---------- on an order: who delivers it ---------- */
let cur = null;
SB.selfOrder = (o, again) => {
  const by = o.ex && o.ex.by, l = who(); if (!(on() || by) || (o.ex && o.ex.branch) || o.tracking || o.carrier || /cancelled|returned/.test(o.status)) return ""; cur = { o, again };
  const done = o.status === "delivered", same = w => by && by.n === w.n && by.p === w.p;
  return `<section class="card"><div class="row"><h3>🛵 مين هيوصّله؟</h3><span class="space"></span>${by ? `<b>${esc(by.n)}</b>` : ""}</div>${done || !on() ? (by ? `<p class="sm-hint"><bdi dir="ltr">${esc(by.p)}</bdi></p>` : "") : `<div class="dl-pk">${l.map((w, i) => `<button type="button" data-do="dlPick:${i}" aria-pressed="${same(w)}">${esc(w.n)}</button>`).join("")}</div>
    <p class="sm-hint">${by ? "الزبون بيشوف اسمه ورقمه لما الأوردر يخرج." : "لو ماخترتش، أول واحد في القايمة هو اللي هيظهر للزبون لما الأوردر يخرج."}</p>`}</section>`;
};
A.dlPick = (i, b) => run(b, async () => { const c = cur; await post("self-pick", { id: c.o.id, i: +i }); await c.again(); say("اتحفظ ✅"); });
})();
