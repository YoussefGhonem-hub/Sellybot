/* «متجري»: the last shipping features, each with its own switch (off until the owner turns it on):
   «ميعاد التوصيل» (the customer picks the day), the fee by city or weight, «المندوب يجيب المرتجع» and «مراجعة فلوس التحصيل»
   (these two need a linked shipping company: state.carriers). Their part in «الشحن», in a return, in an order and in the orders' list. */
(() => {
"use strict";
const SB = window.SbShop, { esc, fmt, mny, num, ico, fld, run, say, post, get, pane, sure, plural } = SB, D = document, A = SB.acts;
const st = () => SB.state() || {}, S2 = k => ((st().settings || {}).ship2 || {})[k] || {}, car = () => st().carriers || null;
const css = D.createElement("style"); css.textContent = ".s2-wd{display:flex;flex-wrap:wrap;gap:6px;margin:4px 0 12px}.s2-wd label{display:flex;align-items:center;gap:6px;border:1px solid var(--line);border-radius:99px;padding:6px 12px;cursor:pointer;min-height:38px}.s2-wd label:has(input:checked){border-color:var(--brand);background:color-mix(in srgb,var(--brand) 10%,var(--card))}.s2-row{display:grid;grid-template-columns:minmax(0,1.3fr) minmax(0,1.3fr) minmax(70px,.7fr) auto;gap:8px;align-items:end;margin-bottom:8px}.s2-row.z{grid-template-columns:minmax(0,1.6fr) minmax(70px,.7fr) minmax(70px,.7fr) auto}.s2-row select,.s2-row input{width:100%;min-width:0}.s2-row .sm-f{margin:0}@media(max-width:560px){.s2-row,.s2-row.z{grid-template-columns:1fr 1fr}.s2-row>:last-child{justify-self:end}}.s2-rb{border:1px solid var(--line);border-radius:12px;padding:10px 12px;margin:8px 0;display:grid;gap:4px}.s2-rb p{margin:0}.s2-w{display:grid;grid-template-columns:minmax(0,1fr) 110px;gap:6px 10px;align-items:center}.s2-w .v{padding-inline-start:18px;color:var(--muted)}.s2-w input{width:100%}.s2-sum{display:grid;gap:10px;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));margin:10px 0}.s2-sum div{border:1px solid var(--line);border-radius:12px;padding:10px}.s2-sum b{display:block;font-size:18px}.s2-sum small{color:var(--muted)}.s2-sum .bad b{color:var(--err)}.s2-cl .sm-row input{flex:none}";
D.head.appendChild(css);
const WD = ["الأحد", "الاتنين", "التلات", "الأربع", "الخميس", "الجمعة", "السبت"];
const dayT = iso => { if (!iso) return ""; const d = new Date(iso + "T12:00:00"); return WD[d.getDay()] + " " + d.getDate() + "/" + (d.getMonth() + 1); };
const partT = p => p === "am" ? " (الصبح)" : p === "pm" ? " (بالليل)" : "";
const hours = v => SB.sel("cut", [[24, "مفيش، أي وقت"]].concat(Array.from({ length: 24 }, (_, i) => [i, "قبل " + ((i % 12) || 12) + (i < 12 ? " الصبح" : " بالليل")])), v);
const saveSet = (k, v) => post("settings", { settings: { ship2: { [k]: v } } });
const reTab = () => { if (SB.view() === "ship") SB.show("ship"); };

/* ================= the part in «الشحن» ================= */
SB.ship2Tab = body => {
  const d = S2("dday"), r = S2("srate"), b = S2("sback"), c = S2("scash"), k = car(), e = D.createElement("section"); e.className = "card s2-tab";
  const card = (em, t, on, sub, btns) => `<div class="sh-c"><div class="row"><b>${em} ${t}</b><span class="space"></span><span class="sm-pill ${on ? "ok" : "g"}">${on ? "شغّال" : "مقفول"}</span></div><p class="sm-hint">${sub}</p><div class="row">${btns}</div></div>`;
  e.innerHTML = `<h3>مواعيد التوصيل وحساب الشحن</h3><p class="sub">كل حاجة هنا مقفولة لحد ما تفتحها، ولما تبقى مقفولة مفيش حاجة بتتغيّر عند الزبون.</p><div class="sh-g">
    ${card("📅", "ميعاد التوصيل", d.on, d.on ? `الزبون بيختار يوم من: ${(d.wd || []).map(i => WD[i]).join("، ") || "مفيش أيام"}` : "الزبون يختار يوم التوصيل وهو بيطلب، من الأيام اللي تحددها.", `<button type="button" class="btn sm" data-do="s2Day">الإعدادات</button>${d.on ? '<button type="button" class="btn ghost sm" data-do="s2Next">الأيام الجاية</button>' : ""}`)}
    ${card("⚖️", "الشحن بالوزن أو المدينة", r.on, "سعر تاني لمدن معيّنة، وزيادة على كل كيلو فوق اللي السعر شامله.", '<button type="button" class="btn sm" data-do="s2Rate">الإعدادات</button><button type="button" class="btn ghost sm" data-do="s2W">أوزان المنتجات</button>')}
    ${k ? card("↩️", "المندوب يجيب المرتجع", b.on, "من طلب الاسترجاع أو الاستبدال، دوسة واحدة وشركة الشحن تبعت مندوب للزبون.", `<button type="button" class="btn ${b.on ? "ghost " : ""}sm" data-do="s2Back">${b.on ? "اقفلها" : "شغّلها"}</button>`) : ""}
    ${k ? card("💵", "مراجعة فلوس التحصيل", c.on, "اللي المندوب حصّله قصاد اللي الشركة حوّلتهولك، وإيه اللي ناقص.", `${c.on ? '<button type="button" class="btn sm" data-do="s2Cash">افتح المراجعة</button>' : ""}<button type="button" class="btn ${c.on ? "ghost " : ""}sm" data-do="s2CashSet">${c.on ? "الإعدادات" : "شغّلها"}</button>`) : ""}</div>`;
  body.appendChild(e);
};

/* ---------- «ميعاد التوصيل» ---------- */
A.s2Day = async () => {
  const geo = await SB.geo(), s = { on: false, wd: [6, 0, 1, 2, 3, 4], govs: [], from: 1, to: 7, cut: 24, max: 0, parts: false, must: false, ...S2("dday") }; let dirty = false, all = !(s.govs || []).length;
  const p = pane({
    title: "ميعاد التوصيل", cls: "mid", dirty: () => dirty, after: e => e.querySelector(".sm-p-b").addEventListener("change", () => { dirty = true; }),
    html: () => `<div class="sm-form">${SB.tog("on", s.on, "الزبون يختار يوم التوصيل", "بيظهرله في صفحة الطلب، من الأيام اللي هنا بس")}
      <h4>بتوصّل أيام إيه؟</h4><div class="s2-wd">${WD.map((n, i) => `<label><input type="checkbox" data-wd="${i}" ${(s.wd || []).includes(i) ? "checked" : ""}>${n}</label>`).join("")}</div>
      <div class="sm-2">${fld("from", "من بعد كام يوم", s.from, { mode: "numeric", x: "data-n", hint: "0 = النهارده، 1 = بكرة" })}${fld("to", "لحد كام يوم قدام", s.to, { mode: "numeric", x: "data-n" })}</div>
      <div class="sm-2"><div class="sm-f"><label>آخر ميعاد للطلب عشان أقرب يوم</label>${hours(s.cut)}</div>${fld("max", "أكتر عدد أوردرات في اليوم", s.max, { mode: "numeric", x: "data-n", hint: "0 = من غير حد. اليوم اللي يتملى مابيظهرش" })}</div>
      ${SB.tog("parts", s.parts, "الزبون يختار الصبح ولا بالليل")}${SB.tog("must", s.must, "لازم يختار يوم", "لو مقفولة يقدر يسيبها والمتجر يحدد")}
      <h4>لأنهي محافظات؟</h4><label class="toggle"><input type="checkbox" data-ch="s2All" ${all ? "checked" : ""}><span class="tg" aria-hidden="true"></span><span>كل المحافظات</span></label>${all ? "" : `<div class="s2-wd">${geo.map(g => `<label><input type="checkbox" data-gv="${g.id}" ${(s.govs || []).includes(g.id) ? "checked" : ""}>${esc(g.ar)}</label>`).join("")}</div>`}</div>`,
    foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
    ch: { s2All: on => { keep(); all = on; p.draw(); } },
    acts: { go: b => run(b, async () => { keep(); if (s.on && !s.wd.length) throw new Error("اختار يوم واحد على الأقل."); if (+s.to < +s.from) throw new Error("«لحد» لازم يكون بعد «من»."); await saveSet("dday", { ...s, govs: all ? [] : s.govs }); dirty = false; p.close(true); say("اتحفظ ✅"); reTab(); }) }
  });
  function keep() { const v = SB.read(p.el); Object.assign(s, { on: v.on, from: +v.from || 0, to: +v.to || 0, cut: +v.cut, max: +v.max || 0, parts: v.parts, must: v.must }); s.wd = [...p.el.querySelectorAll("[data-wd]")].filter(x => x.checked).map(x => +x.dataset.wd); if (!all) s.govs = [...p.el.querySelectorAll("[data-gv]")].filter(x => x.checked).map(x => +x.dataset.gv); }
};
A.s2Next = async b => {
  const r = await run(b, () => get("sday")); if (!r) return;
  const p = pane({ title: "الأيام الجاية", cls: "small", acts: { on: d => { p.close(true); SB.ordersOn(d); } }, html: () => `<p class="sm-hint">اللي الزبون شايفه دلوقتي: ${r.seen.length ? esc(r.seen.join("، ")) : "مفيش أيام متاحة"}</p>${r.next.length ? `<div class="sm-list">${r.next.map(x => `<button type="button" class="sm-row" data-do="on:${x.d}"><span class="grow"><b>${esc(x.t)}</b></span><span class="sm-pr"><b>${plural(x.n, "أوردر واحد", "أوردرين", "أوردرات", "أوردر")}</b></span></button>`).join("")}</div>` : '<div class="sm-empty"><span aria-hidden="true">📅</span><p>لسه مفيش أوردرات ليها يوم.</p></div>'}` });
};

/* in the orders' list, the prep list and an order */
SB.dayTag = o => o.dday ? " · 📅 " + esc(dayT(o.dday) + partT(o.dpart)) : "";
SB.dayPick = F => { if (SB.v.orders && SB.v.orders.ch) SB.v.orders.ch.dday = v => SB.ordersOn(v); return S2("dday").on || F.day ? `<label class="sm-dt">يوم التوصيل <input type="date" value="${esc(F.day || "")}" data-ch="dday"></label>` : ""; };
SB.prepDays = r => r.days && r.days.length ? `<p class="sm-hint">📅 ${r.days.map(x => esc(x.t) + ": " + plural(x.n, "أوردر واحد", "أوردرين", "أوردرات", "أوردر")).join(" · ")}</p>` : "";
SB.dayCard = o => o.ex && o.ex.day ? `<section class="card"><div class="row"><h3>📅 يوم التوصيل</h3><span class="space"></span><b>${esc(dayT(o.ex.day) + partT(o.ex.dpart))}</b></div><p class="sm-hint">الزبون اختاره وهو بيطلب، وبيظهر على البوليصة ورسالة التأكيد.</p></section>` : "";

/* ---------- the fee by city or weight ---------- */
A.s2Rate = async () => {
  const geo = await SB.geo(), s = { on: false, w: 0, inc: 0, per: 0, cities: [], zones: [], ...S2("srate") }; s.cities = (s.cities || []).map(x => ({ ...x })); s.zones = (s.zones || []).map(x => ({ ...x })); let dirty = false;
  const gsel = (k, v) => SB.sel(k, [["", "المحافظة"]].concat(geo.map(g => [g.id, g.ar])), v || "");
  const csel = (k, g, v) => { const x = geo.find(y => y.id === +g); const l = x ? x.c : []; return `<input list="s2c-${esc(k)}" data-k="${k}" value="${esc(v || "")}" placeholder="المدينة" maxlength="60"><datalist id="s2c-${esc(k)}">${l.map(c => `<option value="${esc(c)}">`).join("")}</datalist>`; };
  const p = pane({
    title: "الشحن بالوزن أو المدينة", cls: "mid", dirty: () => dirty, after: e => e.querySelector(".sm-p-b").addEventListener("input", () => { dirty = true; }),
    html: () => `<div class="sm-form">${SB.tog("on", s.on, "شغّل الحساب ده", "مقفول = سعر المحافظة زي ما هو. «شحن مجاني فوق» شغّال في الحالتين")}
      <h4>الوزن</h4><div class="sm-3">${fld("inc", "السعر شامل لحد", s.inc, { mode: "decimal", suf: "كجم", x: "data-n" })}${fld("per", "كل كيلو زيادة بـ", s.per, { mode: "decimal", suf: "ج", x: "data-n", hint: "0 = الوزن مابيفرقش" })}${fld("w", "وزن القطعة لو ماكتبتوش", s.w, { mode: "decimal", suf: "كجم", x: "data-n" })}</div>
      <p class="sm-hint">مثال: شامل لحد 2 كجم وكل كيلو زيادة بـ 10: طلب 3.4 كجم بياخد سعر المحافظة + 20.</p>
      <h4>وزن مختلف لمحافظات</h4>${s.zones.map((z, i) => `<div class="s2-row z"><div class="sm-f"><label>المحافظة</label>${gsel("zg" + i, z.g)}</div>${fld("zi" + i, "شامل لحد", z.inc, { mode: "decimal", suf: "كجم", x: "data-n" })}${fld("zp" + i, "الكيلو الزيادة", z.per, { mode: "decimal", suf: "ج", x: "data-n" })}<button type="button" class="sm-ic" data-do="zx:${i}" aria-label="شيل">${ico("x")}</button></div>`).join("")}<button type="button" class="btn ghost sm" data-do="za">${ico("plus")} محافظة</button>
      <h4>مدن ليها سعر لوحدها</h4>${s.cities.map((x, i) => `<div class="s2-row"><div class="sm-f"><label>المحافظة</label>${gsel("cg" + i, x.g)}</div><div class="sm-f"><label>المدينة</label>${csel("cc" + i, x.g, x.c)}</div>${fld("cf" + i, "السعر", x.f, { mode: "decimal", suf: "ج", x: "data-n" })}<button type="button" class="sm-ic" data-do="cx:${i}" aria-label="شيل">${ico("x")}</button></div>`).join("")}<button type="button" class="btn ghost sm" data-do="ca">${ico("plus")} مدينة</button></div>`,
    foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
    acts: {
      za: () => { keep(); s.zones.push({ g: "", inc: s.inc, per: s.per }); p.draw(); }, zx: i => { keep(); s.zones.splice(+i, 1); dirty = true; p.draw(); },
      ca: () => { keep(); s.cities.push({ g: "", c: "", f: "" }); p.draw(); }, cx: i => { keep(); s.cities.splice(+i, 1); dirty = true; p.draw(); },
      go: b => run(b, async () => { keep(); await saveSet("srate", { on: s.on, w: s.w, inc: s.inc, per: s.per, zones: s.zones.filter(z => z.g), cities: s.cities.filter(x => x.g && (x.c || "").trim()) }); dirty = false; p.close(true); say("اتحفظ ✅"); reTab(); })
    }
  });
  p.el.addEventListener("change", e => { if (/^cg\d+$/.test(e.target.dataset.k || "")) { keep(); p.draw(); } });
  function keep() { const v = SB.read(p.el); Object.assign(s, { on: v.on, w: +v.w || 0, inc: +v.inc || 0, per: +v.per || 0 }); s.zones.forEach((z, i) => { z.g = +v["zg" + i] || ""; z.inc = +v["zi" + i] || 0; z.per = +v["zp" + i] || 0; }); s.cities.forEach((x, i) => { x.g = +v["cg" + i] || ""; x.c = v["cc" + i] || ""; x.f = +v["cf" + i] || 0; }); }
};
A.s2W = async b => {
  const r = await run(b, () => get("srate-w")); if (!r) return; let dirty = false;
  const p = pane({
    title: "أوزان المنتجات", cls: "mid", dirty: () => dirty, after: e => e.querySelector(".sm-p-b").addEventListener("input", () => { dirty = true; }),
    html: () => `<p class="sm-hint">بالكيلو، للقطعة الواحدة. المقاس أو اللون اللي وزنه مختلف اكتبله وزنه، والفاضي بياخد وزن المنتج.</p>${r.list.length ? `<div class="s2-w">${r.list.map(x => `<span><b>${esc(x.n)}</b></span><input type="text" inputmode="decimal" dir="ltr" data-wp="${x.p}" value="${x.w || ""}" placeholder="0" aria-label="وزن ${esc(x.n)}">${x.vars.map(v => `<span class="v">${esc(v.l)}</span><input type="text" inputmode="decimal" dir="ltr" data-wv="${v.v}" value="${v.w || ""}" placeholder="—" aria-label="وزن ${esc(x.n)} ${esc(v.l)}">`).join("")}`).join("")}</div>` : '<div class="sm-empty"><span aria-hidden="true">📦</span><p>لسه مفيش منتجات.</p></div>'}`,
    foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
    acts: { go: b2 => run(b2, async () => { const items = [...p.el.querySelectorAll("[data-wp]")].map(e => ({ p: +e.dataset.wp, w: num(e.value) || 0 })).concat([...p.el.querySelectorAll("[data-wv]")].map(e => ({ v: +e.dataset.wv, w: num(e.value) || 0 }))); await post("srate-w", { items }); dirty = false; p.close(true); say("اتحفظ ✅"); }) }
  });
};

/* ---------- the two that need a shipping company ---------- */
A.s2Back = b => run(b, async () => { const on = !S2("sback").on; await saveSet("sback", { on }); say(on ? "اتشغّلت. هتلاقي الزرار في «المرتجعات» ✅" : "اتقفلت."); reTab(); });
A.s2CashSet = async () => {
  const s = { on: false, days: 14, ...S2("scash") };
  const p = pane({ title: "مراجعة فلوس التحصيل", cls: "small", html: () => `<div class="sm-form">${SB.tog("on", s.on, "شغّل المراجعة")}${fld("days", "الشحنة تبقى متأخرة لو مااتدفعتش بعد", s.days, { mode: "numeric", x: "data-n", suf: "يوم" })}</div>`, foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
    acts: { go: b => run(b, async () => { const v = SB.read(p.el); await saveSet("scash", { on: v.on, days: +v.days || 14 }); p.close(true); say("اتحفظ ✅"); reTab(); }) } });
};

/* ---------- a return request: «المندوب يجيب المرتجع» ---------- */
const TONE = { asked: "b", made: "b", picked: "b", going: "b", took: "b", returning: "y", returned: "ok", attempt: "bad", cancelled: "g", lost: "bad", hold: "y" };
SB.retShip = r => { const k = r.pk; if (!k) return ""; const x = k.ship; if (x && (x.state === "live" || x.state === "making" || (x.state === "done" && x.back))) return ""; if (x && x.state === "unsure") return `<button type="button" class="btn sm" data-do="rbAsk:${x.id}">اسأل ${esc(x.cName)}</button>`; return k.can && k.cos.length ? `<button type="button" class="btn sm" data-do="rbGo:${r.id}">🚚 المندوب يجيب ${r.kind === "exchange" ? "الاستبدال" : "المرتجع"}</button>` : ""; };
SB.retCard = r => {
  const k = r.pk, x = k && k.ship; if (!x || x.state === "failed") return x && x.err ? `<p class="sm-risk">${esc(x.err)}</p>` : "";
  return `<div class="s2-rb"><div class="row"><b>🚚 ${esc(x.cName)}${x.tracking ? ` · <bdi dir="ltr">${esc(x.tracking)}</bdi>` : ""}</b><span class="space"></span><span class="sm-pill ${x.state === "unsure" ? "bad" : x.state === "off" ? "g" : TONE[x.ours] || "b"}">${esc(x.text)}</span></div>
    ${x.collect > 0 ? `<p class="sm-hint">المندوب بيحصّل الفرق: ${mny(x.collect)}</p>` : ""}${x.why ? `<p class="sm-hint">${esc(x.why)}</p>` : ""}${x.state === "unsure" ? `<p class="sm-risk">${esc(x.err)}</p>` : ""}
    ${x.told ? '<p class="sm-hint">الزبون عرف على الواتساب إن المندوب جاي.</p>' : x.tellDue ? `<p class="sm-hint">رسالة الزبون هتتبعت ${SB.when(x.tellDue)}.</p>` : x.tellNote ? `<p class="sh-w">${esc(x.tellNote)}</p>` : ""}
    <div class="row">${x.back && !x.got ? `<button type="button" class="btn sm" data-do="rbGot:${x.id}:${r.id}">وصلتني</button>` : ""}${x.tracking && x.state !== "off" && !(x.c === "bosta" && x.kind !== "exchange") ? `<button type="button" class="btn ghost sm" data-do="rbLabel:${x.id}">البوليصة</button>` : x.tracking && x.c === "bosta" && x.state === "live" ? '<span class="sm-hint">مندوب بوسطة بيجيب بوليصة المرتجع معاه.</span>' : ""}${x.url ? `<a class="btn ghost sm" href="${esc(x.url)}" target="_blank" rel="noopener">صفحة التتبع</a>` : ""}${x.state === "live" ? `<button type="button" class="btn ghost sm" data-do="rbAsk:${x.id}">حدّث الحالة</button>` : ""}${x.canCancel ? `<button type="button" class="btn danger sm" data-do="rbOff:${x.id}">الغيه</button>` : x.c === "shipblu" && x.state === "live" && (x.ours === "asked" || x.ours === "made") ? '<span class="sm-hint">الإلغاء من حسابك على ShipBlu، وهنعرف لوحدنا خلال ساعة.</span>' : ""}</div>
    ${x.events.length ? `<details><summary>حركة الشحنة</summary><ol class="sm-log">${x.events.map(e => `<li><b>${esc(e.t)}</b><small>${SB.when(e.at)}</small></li>`).join("")}</ol></details>` : ""}</div>`;
};
const retFresh = () => SB.show("returns");
A.rbGo = async id => {
  const R = await get("returns", { st: "" }), r = R.list.find(x => x.id === +id); if (!r || !r.pk) return; const ok = r.pk.cos.filter(c => !c.no);
  let c = ok.some(x => x.id === r.pk.def) ? r.pk.def : ok.length ? ok[0].id : "", more = null, area = "", busy = false;
  const p = pane({
    title: r.kind === "exchange" ? "المندوب يجيب الاستبدال" : "المندوب يجيب المرتجع", cls: "small", dirty: () => busy,
    html: () => `<div class="sm-form">${r.pk.cos.length > 1 ? `<div class="sm-f"><label>شركة الشحن</label>${SB.seg("co", r.pk.cos.map(x => [x.id, esc(x.name)]), c)}</div>` : ""}${r.pk.cos.filter(x => x.no).map(x => `<p class="sh-w">${esc(x.name)}: ${esc(x.no)}</p>`).join("")}
      <p class="sm-hint">${r.kind === "exchange" ? "المندوب هيستلم منك البديل، ويسلّمه للزبون وياخد القديم." : "المندوب هيروح للزبون ياخد المنتج ويرجّعهولك، من غير ما يحصّل حاجة."} والزبون هيعرف على الواتساب ومعاه رقم التتبع.</p>
      ${r.kind === "exchange" ? fld("collect", "فرق يحصّله من الزبون (اختياري)", "", { mode: "decimal", suf: "ج", x: "data-n", hint: "سيبه فاضي لو مفيش فرق" }) : ""}${fld("note", "ملاحظة للمندوب", "", { max: 150 })}
      ${more ? `<div class="sm-f"><label>منطقة الزبون عند الشركة</label><div class="sh-a">${more.map((m, i) => `<button type="button" data-do="ar:${i}" class="${area === m.k ? "on" : ""}">${esc(m.t)}</button>`).join("")}</div></div>` : ""}</div>`,
    foot: () => `<button type="button" class="btn" data-do="go" ${c && (!more || area) ? "" : "disabled"}>اطلب المندوب</button>`,
    acts: {
      co: v => { c = v; more = null; area = ""; p.draw(); }, ar: i => { area = more[+i].k; p.draw(); },
      go: async b => { if (busy) return; busy = true; const v = SB.read(p.el); try { await run(b, async () => { try { const x = await post("sback-go", { id: +id, c, collect: +v.collect || 0, note: v.note || "", area }); p.close(true); say(x.had ? "المندوب متطلب قبل كده." : "المندوب اتطلب ✅"); retFresh(); } catch (e) { if (e.code === "area" && e.data && e.data.more && e.data.more.length) { more = e.data.more; area = ""; p.draw(); say(e.message, true); return; } throw e; } }); } finally { busy = false; } }
    }
  });
};
A.rbAsk = (id, b) => run(b, async () => { await post("sback-ask", { id: +id }); say("اتحدّثت ✅"); retFresh(); });
A.rbOff = async (id, b) => { if (await sure("تلغي طلب المندوب؟ الطلب نفسه هيفضل زي ما هو، وتقدر تطلبه تاني.", "الغيه")) run(b, async () => { await post("sback-cancel", { id: +id }); say("اتلغى."); retFresh(); }); };
A.rbLabel = async (id, b) => {
  const w = open("", "_blank"); await run(b, async () => { const r = await fetch("api/shop.ashx", { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Content-Type": "application/json" }, body: JSON.stringify({ a: "sback-label", id: +id }) }); if (!r.ok) { let d = null; try { d = await r.json(); } catch (e) { } if (w) w.close(); throw new Error(d && d.message || "حصلت مشكلة، جرّب تاني."); } const u = URL.createObjectURL(await r.blob()); if (w) w.location = u; setTimeout(() => URL.revokeObjectURL(u), 12e4); });
};
A.rbGot = async (id, rid) => {
  const R = await get("returns", { st: "" }), r = R.list.find(x => x.id === +rid), sum = r ? r.items.reduce((a, i) => a + num(i.t), 0) : 0;
  const p = pane({ title: "المرتجع وصلك", cls: "small", html: () => `<div class="sm-form">${SB.tog("restock", true, "رجّع الكمية للمخزون", "شيلها لو المنتج رجع تالف")}${r && r.kind === "return" ? fld("refund", "المبلغ اللي هترجعه للزبون", sum, { mode: "decimal", suf: "ج", x: "data-n" }) : ""}</div>`, foot: () => '<button type="button" class="btn" data-do="go">وصلتني</button>',
    acts: { go: b => run(b, async () => { const v = SB.read(p.el); await post("sback-got", { id: +id, restock: v.restock !== false, ...(v.refund !== undefined ? { refund: v.refund } : {}) }); p.close(true); say("اتسجّل ✅"); retFresh(); }) } });
};

/* ---------- «مراجعة فلوس التحصيل» ---------- */
const CS = { paid: ["اتدفعت", "ok"], part: ["اتدفع أقل", "bad"], more: ["اتدفع أكتر", "y"], wait: ["لسه", "b"], late: ["متأخرة", "bad"] };
A.s2Cash = async b => {
  let C = "", F = "owed", P = 0, L = await run(b, () => get("scash", { c: C, f: F, page: P })); if (!L) return; const pick = new Set();
  const load = async () => { L = await get("scash", { c: C, f: F, page: P }); pick.clear(); p.draw(); };
  const p = pane({
    title: "مراجعة فلوس التحصيل", cls: "mid",
    head: () => '<a class="btn ghost sm" href="api/shop.ashx?a=scash-xls">Excel</a>',
    html: () => `<p class="sub">كل شحنة اتسلّمت بتحصيل: المفروض يتحصّل، اللي الشركة قالت إنه اتحصّل، واللي اتحوّلك. الشركات مابتدّيش تفاصيل التحويل، فارفع كشفها أو علّم اللي وصلك بإيدك.</p>
      ${L.sums.length ? L.sums.map(s => `<h4>${esc(s.name)}</h4><div class="s2-sum"><div><small>المفروض يتحصّل</small><b>${mny(s.due)}</b><small>${plural(s.n, "شحنة واحدة", "شحنتين", "شحنات", "شحنة")}</small></div><div><small>اتحصّل (من الشركة)</small><b>${s.gotN ? mny(s.got) : "—"}</b><small>${s.gotN ? "في " + plural(s.gotN, "شحنة واحدة", "شحنتين", "شحنات", "شحنة") : "الشركة ماقالتش"}</small></div><div><small>اتحوّلك</small><b>${mny(s.paid)}</b></div><div class="${s.missing > 0 ? "bad" : ""}"><small>لسه ماجاش</small><b>${mny(s.missing)}</b></div><div class="${s.late ? "bad" : ""}"><small>متأخرة أكتر من ${fmt(L.days)} يوم</small><b>${fmt(s.late)}</b><small>${s.late ? mny(s.lateSum) : ""}</small></div><div class="${s.diff ? "bad" : ""}"><small>فيها فرق</small><b>${fmt(s.diff)}</b></div></div>`).join("") : '<div class="sm-empty"><span aria-hidden="true">💵</span><p>لسه مفيش شحنات اتسلّمت بتحصيل.</p></div>'}
      ${L.sums.length > 1 ? SB.seg("cc", [["", "كل الشركات"]].concat(L.sums.map(s => [s.c, esc(s.name)])), C) : ""}<div class="sm-chips">${[["owed", "لسه ماجاش"], ["late", "متأخرة"], ["diff", "فيها فرق"], ["paid", "اتدفعت"], ["", "الكل"]].map(x => `<button type="button" class="${F === x[0] ? "on" : ""}" data-do="cf:${x[0]}">${x[1]}</button>`).join("")}</div>
      <div class="row"><button type="button" class="btn sm" data-do="up">ارفع كشف الشركة</button>${pick.size ? `<button type="button" class="btn ghost sm" data-do="pay">علّم ${fmt(pick.size)} اتدفعت</button>` : ""}</div>
      ${L.list.length ? `<div class="sm-list s2-cl">${L.list.map(x => `<div class="sm-row"><input type="checkbox" data-ch="pk:${x.id}" ${pick.has(x.id) ? "checked" : ""} aria-label="علّم ${esc(x.name)}"><button type="button" class="grow" data-do="ord:${x.order}"><b>${esc(x.name)} <i>${esc(x.cName)} · <bdi dir="ltr">${esc(x.tracking)}</bdi></i></b><small>${[x.at ? "اتسلّمت " + SB.day(x.at) : "", x.got != null ? "اتحصّل " + mny(x.got) : "", x.paid != null ? "اتحوّل " + mny(x.paid) + (x.on ? " يوم " + SB.day(x.on + "T12:00:00") : "") + (x.src === "file" ? " (من الكشف)" : x.src === "hand" ? " (بإيدك)" : "") : "", x.diff ? "⚠️ فيه فرق" : ""].filter(Boolean).join(" · ")}</small></button><span class="sm-pr"><b>${mny(x.due)}</b></span><span class="sm-pill ${CS[x.st][1]}">${CS[x.st][0]}</span>${x.paid != null ? `<button type="button" class="sm-ic" data-do="un:${x.id}" aria-label="شيل الدفع">${ico("x")}</button>` : ""}</div>`).join("")}</div>${L.total > L.size ? `<div class="sm-pg"><button type="button" class="btn ghost sm" data-do="pg:-1" ${P ? "" : "disabled"}>السابق</button><span>${fmt(P + 1)} من ${fmt(Math.ceil(L.total / L.size))}</span><button type="button" class="btn ghost sm" data-do="pg:1" ${P < Math.ceil(L.total / L.size) - 1 ? "" : "disabled"}>التالي</button></div>` : ""}` : `<p class="sm-hint">${F === "owed" ? "مفيش فلوس ناقصة. كله وصل." : "مفيش شحنات هنا."}</p>`}
      ${L.files.length ? `<details><summary>الكشوف اللي اترفعت</summary><ol class="sm-log">${L.files.map(f => `<li><b>${esc(f.name || "كشف")} · ${esc((L.sums.find(s => s.c === f.c) || {}).name || f.c)}</b><small>${plural(f.hit, "شحنة واحدة", "شحنتين", "شحنات", "شحنة")} من ${fmt(f.lines)} سطر · ${mny(f.amount)} · ${SB.when(f.at)}</small></li>`).join("")}</ol></details>` : ""}`,
    ch: { pk: (on, id) => { if (on) pick.add(+id); else pick.delete(+id); p.draw(); } },
    acts: {
      cc: v => { C = v; P = 0; run(null, load); }, cf: v => { F = typeof v === "string" ? v : ""; P = 0; run(null, load); }, pg: d => { P = Math.max(0, P + +d); run(null, load); }, ord: id => SB.openOrder(+id),
      un: (id, b2) => run(b2, async () => { L = await post("scash-unpay", { id: +id, c: C, f: F }); p.draw(); }),
      pay: () => { const l = L.list.filter(x => pick.has(x.id)), q = pane({ title: "اتدفعت", cls: "small", html: () => `<div class="sm-form">${fld("day", "يوم التحويل", new Date().toISOString().slice(0, 10), { type: "date" })}${l.map(x => fld("a" + x.id, esc(x.name) + " · " + esc(x.tracking), x.due, { mode: "decimal", suf: "ج", x: "data-n" })).join("")}</div>`, foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
        acts: { go: b2 => run(b2, async () => { const v = SB.read(q.el); L = await post("scash-pay", { day: v.day, items: l.map(x => ({ id: x.id, amount: +v["a" + x.id] })), c: C, f: F }); pick.clear(); q.close(true); p.draw(); say("اتحفظ ✅"); }) } }); },
      up: () => { const cos = (car() || { list: [] }).list.filter(x => x.linked); let co = C || (cos[0] || {}).id || "", file = null, cols = null, amt = -1, res = null;
        const q = pane({ title: "كشف الشركة", cls: "small", html: () => `<div class="sm-form">${cos.length > 1 ? `<div class="sm-f"><label>الشركة</label>${SB.seg("co", cos.map(x => [x.id, esc(x.name)]), co)}</div>` : ""}<div class="sm-f"><label>الملف (Excel أو CSV)</label><input type="file" accept=".xlsx,.csv,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" data-ch="f"></div>${fld("day", "يوم التحويل (لو الكشف مفيهوش تاريخ)", new Date().toISOString().slice(0, 10), { type: "date" })}
          ${cols ? `<div class="sm-f"><label>أنهي عمود فيه المبلغ اللي اتحوّلك؟</label>${SB.sel("amt", [[-1, "اختار"]].concat(cols.map(x => [x.i, x.t + (x.ex ? " (مثلًا " + x.ex + ")" : "")])), amt)}</div>` : ""}${res ? `<p class="sm-hint">✅ اتطابق ${plural(res.hit, "شحنة واحدة", "شحنتين", "شحنات", "شحنة")} من ${fmt(res.lines)} سطر بـ ${mny(res.amount)} (رقم التتبع: «${esc(res.track)}»، المبلغ: «${esc(res.amt)}»${res.date ? "، التاريخ: «" + esc(res.date) + "»" : ""}).${res.other ? ` ${plural(res.other, "سطر واحد", "سطرين", "سطور", "سطر")} مش من شحناتك.` : ""}${res.noAmt ? ` ${fmt(res.noAmt)} من غير مبلغ.` : ""}</p>` : ""}</div>`,
          foot: () => res ? '<button type="button" class="btn" data-do="paneClose">تمام</button>' : '<button type="button" class="btn" data-do="go">ارفع</button>',
          ch: { f: (v, b2) => { file = b2.files[0] || null; } },
          acts: { co: v => { co = v; q.draw(); }, go: b2 => run(b2, async () => { if (!file) throw new Error("اختار الملف."); if (file.size > 2.5e6) throw new Error("الملف أكبر من 2.5 ميجا."); const v = SB.read(q.el); if (cols) amt = +v.amt; if (cols && amt < 0) throw new Error("اختار عمود المبلغ.");
            const data = await new Promise((ok, no) => { const fr = new FileReader(); fr.onload = () => ok(fr.result); fr.onerror = () => no(new Error("الملف ماتقراش.")); fr.readAsDataURL(file); });
            try { const r = await post("scash-file", { c: co, name: file.name, data, day: v.day, amt }); res = r.file; L = r; C = co; F = ""; p.draw(); q.draw(); } catch (e) { if (e.code === "column" && e.data) { cols = e.data.cols; q.draw(); say(e.message, true); return; } throw e; } }) } }); }
    }
  });
};
})();
