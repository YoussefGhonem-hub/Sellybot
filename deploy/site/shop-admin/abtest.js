/* «متجري»: «تجربة شكلين» in «شكل المتجر». The owner makes look B from the published look (another hero picture and headline, another
   colour, another order of the home sections, the other template), sees A and B side by side, and starts: half the visitors see B.
   The numbers come side by side with plain words; «لسه بدري» until each look has enough visitors and orders (ShopAb.cs). */
(() => {
"use strict";
const SB = window.SbShop, V = SB.v, { esc, fmt, mny, ico, run, say, post, get, pane, sure } = SB, D = document;
let ab = null, wait = null;
const load = () => wait || (wait = get("ab").then(d => { ab = d; wait = null; return d; }).catch(e => { wait = null; throw e; }));
const X = () => SB.lookX;
const dayN = t => t ? SB.day(t) : "";
const css = () => { if (D.getElementById("sm-ab-css")) return; const e = D.createElement("style"); e.id = "sm-ab-css"; e.textContent = `.sm-abt{width:100%;border-collapse:collapse;font-size:14px;margin:8px 0}.sm-abt th,.sm-abt td{padding:7px 8px;border-top:1px solid var(--line);text-align:start}.sm-abt th{font-size:12.5px;color:var(--muted);font-weight:600}.sm-abt td:not(:first-child),.sm-abt th:not(:first-child){text-align:center;font-variant-numeric:tabular-nums}.sm-abt tr.k td{font-weight:700}.sm-abv{padding:10px 12px;border-radius:12px;background:var(--soft);margin:8px 0;line-height:1.7}.sm-abv.ok{background:color-mix(in srgb,var(--ok) 12%,var(--card))}.sm-ab2{display:grid;grid-template-columns:1fr 1fr;gap:10px}.sm-ab2 figure{margin:0;display:flex;flex-direction:column;gap:6px}.sm-ab2 figcaption{font-weight:700;text-align:center}.sm-ab2 .fw{position:relative;width:100%;max-width:390px;margin-inline:auto;height:min(70vh,640px);border:1px solid var(--line);border-radius:12px;overflow:hidden;background:var(--soft)}.sm-ab2 iframe{position:absolute;inset-block-start:0;inset-inline-start:0;width:390px;height:844px;border:0;transform-origin:0 0}[dir=rtl] .sm-ab2 iframe{transform-origin:100% 0}.sm-abo{display:flex;flex-direction:column;gap:6px}.sm-abo .sm-row{cursor:grab}.sm-abi{display:flex;gap:10px;align-items:center}.sm-abi img{width:88px;height:56px;object-fit:cover;border-radius:8px;border:1px solid var(--line)}@media(max-width:600px){.sm-ab2{grid-template-columns:1fr}}`; D.head.appendChild(e); };
const SECN = { slider: "سلايدر صور", cats: "الأقسام", "new": "وصل حديثًا", best: "الأكتر طلبًا", sale: "عليها خصم", pick: "منتجات قسم", banner: "بانر", look: "طقم أو مجموعة", hot: "صورة عليها منتجات", ba: "قبل وبعد", budget: "معاك كام؟", text: "فقرة", marquee: "شريط كلام متحرك", faq: "أسئلة متكررة", revs: "آراء الزباين", ugc: "صور زباينا", video: "فيديو", posts: "من المدونة", trust: "شريط الثقة" };
const secName = s => (s.title && s.title[0]) || SECN[s.t] || s.t;

/* what B changes, in words */
const what = b => { const l = [], h = b.hero || {}; if (h.img !== undefined || h.imgM !== undefined) l.push("صورة أول الصفحة"); if (h.title !== undefined) l.push("العنوان"); if (h.text !== undefined) l.push("السطر اللي تحته"); if (h.btn !== undefined) l.push("الزرار"); if (b.color !== undefined) l.push("اللون الأساسي"); if (b.order) l.push("ترتيب الأجزاء"); if (b.template) l.push("القالب"); return l; };
const row = (t, a, b, f) => `<tr><td>${t}</td><td>${f ? f(a) : fmt(a)}</td><td>${f ? f(b) : fmt(b)}</td></tr>`;
const table = t => `<table class="sm-abt"><thead><tr><th></th><th>A (الحالي)</th><th>B</th></tr></thead><tbody>${row("الزوار", t.a.visitors, t.bb.visitors)}${row("شافوا منتج", t.a.views, t.bb.views)}${row("حطّوا في السلة", t.a.carts, t.bb.carts)}${row("بدأوا الطلب", t.a.checkouts, t.bb.checkouts)}<tr class="k"><td>طلبوا</td><td>${fmt(t.a.orders)}</td><td>${fmt(t.bb.orders)}</td></tr>${row("المبيعات", t.a.sales, t.bb.sales, mny)}${row("طلبات لكل 100 زائر", t.a.per100, t.bb.per100)}</tbody></table><p class="sm-hint">الطلبات من غير الملغية. اللي جه من محركات البحث ومعاينات اللينكات مش متحسوب، وبيشوف A.</p>`;
const verdict = t => `<div class="sm-abv ${t.verdict.better ? "ok" : ""}" role="status">${esc(t.verdict.text)}</div>`;
function body() {
  css(); if (!ab) { load().then(() => X().part("abtest")).catch(e => say(e.message, true)); return '<div class="sm-load"><i></i></div>'; }
  const t = ab.test, last = ab.last;
  const intro = `<p class="sm-hint">اعمل شكل تاني (B) من الشكل المنشور: صورة أو عنوان أو لون أو ترتيب أجزاء أو قالب تاني. نص الزوار بيشوفوا B، وكل زائر بيشوف نفس الشكل كل مرة. بنقارن الطلبات لكل زائر، ومش هنقول مين أحسن غير بعد ${fmt(ab.min[0])} زائر و${fmt(ab.min[1])} طلب لكل شكل. التجربة بتقف لوحدها بعد ${fmt(ab.days)} يوم.</p>`;
  if (!ab.can && !t && !last) return intro + '<p class="sm-hint"><b>التجربة بتشتغل بعد ما متجرك يتنشر ويكون مفتوح للزباين.</b></p>';
  if (t && t.state === "draft") return `${intro}<div class="sm-rc g"><span aria-hidden="true">🧪</span><div><b>شكل B جاهز</b><small>بيغيّر: ${esc(what(t.b).join("، "))}. لسه مابدأش.</small></div></div><div class="row" style="flex-wrap:wrap;gap:8px;margin-top:10px"><button type="button" class="btn ghost sm" data-do="abSide">شوف A وB جنب بعض</button><button type="button" class="btn ghost sm" data-do="abEdit">عدّل B</button><button type="button" class="btn sm" data-do="abStart">ابدأ التجربة</button><button type="button" class="btn ghost sm" data-do="abDrop">امسحه</button></div>`;
  if (t && t.state === "on") return `<div class="sm-rc ok"><span aria-hidden="true">🧪</span><div><b>التجربة شغالة: نص الزوار بيشوفوا B</b><small>B بيغيّر: ${esc(what(t.b).join("، "))}. بدأت ${dayN(t.started)}، وباقي ${fmt(t.daysLeft)} يوم وتقف لوحدها.</small></div></div>${verdict(t)}${table(t)}<div class="row" style="flex-wrap:wrap;gap:8px"><button type="button" class="btn ghost sm" data-do="abSide">شوف A وB</button><button type="button" class="btn ghost sm" data-do="abKeep:${t.id}:a">وقّف وخلّي A</button><button type="button" class="btn sm" data-do="abKeep:${t.id}:b">وقّف وخلّي B</button></div>`;
  if (last) return `<div class="sm-rc y"><span aria-hidden="true">⏱️</span><div><b>التجربة خلصت${last.byTime ? " لوحدها بعد " + fmt(ab.days) + " يوم" : ""}</b><small>كل الزوار بيشوفوا A دلوقتي. اختار الشكل اللي يفضل.</small></div></div>${verdict(last)}${table(last)}<div class="row" style="flex-wrap:wrap;gap:8px"><button type="button" class="btn ghost sm" data-do="abKeep:${last.id}:a">خلّي A</button><button type="button" class="btn sm" data-do="abKeep:${last.id}:b">خلّي B</button></div>`;
  return intro + `<button type="button" class="btn ghost sm" data-do="abEdit">${ico("plus")} جهّز شكل B</button>`;
}
const redraw = d => { if (d && d.ab) ab = d.ab; if (X()) X().part("abtest"); };

/* ---------- making B ---------- */
function edit() {
  const L = ab.live, t = ab.test && ab.test.state === "draft" ? ab.test.b : {}, h0 = L.hero || {}, h = { ...h0, ...(t.hero || {}) }, two = (st().draft || {}).langs !== "ar";
  const b = { img: h.img || "", imgM: h.imgM || "", title: [...(h.title || ["", ""])], text: [...(h.text || ["", ""])], btn: [...(h.btn || ["", ""])], color: t.color !== undefined ? t.color : L.color || "", tpl: t.template || L.template, order: t.order && t.order.length ? [...t.order] : L.sections.map((x, i) => i) };
  let dirty = false;
  const pic = (k, label) => `<div class="sm-f"><label>${label}</label><div class="sm-abi">${b[k] ? `<img src="${esc(SB.src(b[k]))}" alt="">` : `<span class="sm-hint">من غير صورة</span>`}<button type="button" class="btn ghost sm" data-do="abPic:${k}">${b[k] ? "غيّرها" : "ارفع صورة"}</button>${b[k] ? `<button type="button" class="btn ghost sm" data-do="abPicX:${k}">شيلها</button>` : ""}</div></div>`;
  const txt = (k, label, max) => `<div class="sm-f"><label>${label}</label>${two ? `<div class="sm-2"><input type="text" maxlength="${max}" value="${esc(b[k][0])}" data-in="abT:${k}:0" aria-label="${label} عربي"><input type="text" dir="ltr" maxlength="${max}" value="${esc(b[k][1])}" data-in="abT:${k}:1" aria-label="${label} إنجليزي"></div>` : `<input type="text" maxlength="${max}" value="${esc(b[k][0])}" data-in="abT:${k}:0">`}</div>`;
  const p = pane({ title: "شكل B", cls: "mid", dirty: () => dirty,
    html: () => `<div class="sm-form"><p class="sub">غيّر حاجة واحدة أو أكتر. اللي ماتغيّرهوش بيفضل زي A، ولو عدّلت المتجر ونشرت وقت التجربة، التعديل بيوصل للشكلين.</p>
      <section class="card in"><h4>أول الصفحة</h4>${pic("img", "الصورة")}${pic("imgM", "صورة الموبايل (اختياري)")}${txt("title", "العنوان", 100)}${txt("text", "السطر اللي تحته", 240)}${txt("btn", "الزرار", 40)}</section>
      <section class="card in"><h4>اللون الأساسي</h4><div class="sm-cl"><input type="color" value="${esc(b.color || "#222222")}" data-in="abC" aria-label="لون B">${b.color ? `<button type="button" class="btn ghost sm" data-do="abCx">لون القالب</button>` : ""}</div></section>
      <section class="card in"><h4>ترتيب أجزاء الرئيسية</h4>${L.sections.length > 1 ? `<div class="sm-list" data-abo>${b.order.filter(i => L.sections[i]).map(i => `<div class="sm-row" data-id="${i}"><button type="button" class="sm-grip" aria-label="اسحب للترتيب">${ico("grip")}</button><span class="grow"><b>${esc(secName(L.sections[i]))}</b>${L.sections[i].on === false ? "<small>مخفي</small>" : ""}</span></div>`).join("")}</div>` : '<p class="sm-hint">محتاج جزأين على الأقل.</p>'}</section>
      ${ab.templates.length > 1 ? `<section class="card in"><h4>القالب</h4>${SB.sel("abtpl", ab.templates.map(x => [x, x === L.template ? x + " (الحالي)" : x]), b.tpl, 'data-ch="abTpl"')}</section>` : ""}</div>`,
    foot: () => '<button type="button" class="btn" data-do="abSave">احفظ B</button>',
    after: e => { const l = e.querySelector("[data-abo]"); if (l) SB.sort(l, ".sm-row", ids => { b.order = ids.map(Number); dirty = true; }); },
    inp: { abT: (v, k, i) => { b[k][+i] = v; dirty = true; }, abC: v => { b.color = v; dirty = true; } },
    ch: { abTpl: v => { b.tpl = v; dirty = true; } },
    acts: {
      abPic: k => SB.pick().then(([f]) => { if (f) run(null, async () => { const x = await SB.upload(f, 0, "", { max: 1600 }); b[k] = x.path; dirty = true; p.draw(); }); }),
      abPicX: k => { b[k] = ""; dirty = true; p.draw(); }, abCx: () => { b.color = ""; dirty = true; p.draw(); },
      abSave: btn => run(btn, async () => {
        const hero = {}; for (const k of ["img", "imgM"]) if ((b[k] || "") !== (h0[k] || "")) hero[k] = b[k]; for (const k of ["title", "text", "btn"]) if (JSON.stringify(b[k]) !== JSON.stringify(h0[k] || ["", ""])) hero[k] = b[k];
        const o = { hero, order: b.order }; if ((b.color || "") !== (L.color || "")) o.color = b.color; if (b.tpl && b.tpl !== L.template) o.template = b.tpl;
        redraw(await post("ab-save", { b: o })); dirty = false; p.close(true); say("شكل B اتحفظ ✅ شوفه جنب A قبل ما تبدأ.");
      })
    } });
}
const st = () => SB.state();
/* A and B as a customer sees them, on a phone */
async function side() {
  const r = await run(null, () => post("preview", {})); if (!r) return; const tok = (/\/_preview\/([a-f0-9]{32})/.exec(r.url) || [])[1]; if (!tok) return;
  const u = r.live.replace(/\/$/, "") + "/?_pv=" + tok + "&_lv=1";
  const p = pane({ title: "A وB زي ما الزباين بيشوفوهم", cls: "wide", html: () => `<div class="sm-ab2"><figure><figcaption>A (الحالي)</figcaption><div class="fw"><iframe src="${esc(u)}" title="شكل A"></iframe></div></figure><figure><figcaption>B</figcaption><div class="fw"><iframe src="${esc(u + "&_ab=b")}" title="شكل B"></iframe></div></figure></div>`,
    after: e => { const fit = () => e.querySelectorAll(".fw").forEach(w => { const f = w.querySelector("iframe"), k = Math.min(1, w.clientWidth / 390); f.style.transform = `scale(${k})`; f.style.height = Math.round(w.clientHeight / k) + "px"; }); fit(); setTimeout(fit, 60); } });
  return p;
}

function hook() {
  const x = X(); if (!x || x.G.abtest) return;
  const G = x.G, keep = Object.keys(G), at = keep.indexOf("secs") + 1, tail = keep.slice(at).map(k => [k, G[k]]); tail.forEach(([k]) => delete G[k]);
  G.abtest = ["🧪", "تجربة شكلين", body]; tail.forEach(([k, v]) => { G[k] = v; });
  Object.assign(V.look.acts, {
    abEdit: () => edit(), abSide: () => side(),
    abStart: async b => { if (await sure("نص الزوار هيشوفوا شكل B من دلوقتي، لحد ما توقّفها أو تعدّي " + fmt(ab.days) + " يوم. تبدأ؟", "ابدأ", { danger: false })) run(b, async () => { redraw(await post("ab-start", {})); say("التجربة بدأت ✅"); }); },
    abDrop: async b => { if (await sure("تمسح شكل B؟", "امسحه")) run(b, async () => { redraw(await post("ab-drop", {})); }); },
    abKeep: async (id, k, b) => { if (await sure(k === "b" ? "شكل B هيبقى شكل متجرك لكل الزوار، ونسخة من الشكل القديم بتتحفظ في «نسخ قديمة». تكمّل؟" : "التجربة هتقف وكل الزوار هيشوفوا A.", k === "b" ? "خلّي B" : "خلّي A", { danger: false })) run(b, async () => { redraw(await post("ab-stop", { id: +id, keep: k })); if (k === "b") { await SB.reload(); say("شكل B بقى شكل متجرك ✅"); SB.show("look", "abtest"); } else say("اتوقفت ✅"); }); }
  });
  const o0 = V.look.open; V.look.open = async function (b, a) { ab = null; load().catch(() => { }); return o0.call(this, b, a); };
}
if (SB.lookX) hook();
})();
