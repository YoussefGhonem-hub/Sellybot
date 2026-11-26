/* «متجري»: paying by card, mobile wallet and installments from the owner's own Paymob account, and digital products. The money goes from
   the customer to the owner at Paymob; Sellybot takes nothing. Three places: «الدفع والفروع» (SB.cardTab: linking, the ways, digital
   products on/off), an order's page (SB.cardUI: the payment, refunds, the download links), the product editor (SB.digiUI / SB.digiSave). */
(() => {
"use strict";
const SB = window.SbShop, { esc, fmt, mny, num, ico, fld, run, say, post, get, pane, sure, when, plural } = SB, D = document, A = SB.acts;
const css = D.createElement("style"); css.textContent = ".cd-g{display:grid;gap:10px;grid-template-columns:repeat(auto-fill,minmax(210px,1fr))}.cd-i{display:flex;gap:8px;align-items:center;border:1px solid var(--line);border-radius:12px;padding:8px 10px;min-width:0}.cd-i input[type=text]{min-width:0;flex:1;max-width:130px}.cd-i b{flex:1;min-width:0}.cd-t{display:grid;gap:6px}.cd-t>div{display:flex;gap:8px;align-items:center;flex-wrap:wrap;border-bottom:1px solid var(--line);padding:7px 0;font-size:14px}.cd-t>div:last-child{border:0}.cd-t small{color:var(--muted)}.cd-w{color:#a86a00;font-size:13.5px}[data-theme=dark] .cd-w{color:#f0c36a}.cd-bar{height:8px;border-radius:9px;background:var(--soft);overflow:hidden}.cd-bar i{display:block;height:100%;background:var(--brand);width:0}.cd-steps{margin:0 0 10px;padding-inline-start:20px;line-height:1.9;font-size:14px}"; D.head.appendChild(css);
const isWay = k => /^(card|wallet|valu|souhoola|sympl|aman|forsa|halan|contact|premium)$/.test(k || "");
SB.isCard = o => isWay(o.pay) || o.ps === "card";

/* ================= «الدفع والفروع»: Paymob and digital products ================= */
let T = null;
SB.cardTab = body => {
  const e = D.createElement("div"); e.className = "cd-tab"; body.appendChild(e); e.innerHTML = '<section class="card"><p class="sm-hint">ثانية واحدة...</p></section>';
  get("card").then(t => { T = t; if (e.isConnected) draw(e); }).catch(x => { if (e.isConnected) e.innerHTML = `<section class="card"><p class="sm-hint">${esc(x.message)}</p></section>`; });
};
const tabEl = () => D.querySelector(".cd-tab");
function draw(e) {
  e = e || tabEl(); if (!e || !T) return; const l = T.last4 || {}, dg = T.digi;
  const pill = !T.linked ? '<span class="sm-pill g">مش مربوط</span>' : T.err ? '<span class="sm-pill bad">وقف</span>' : T.worked ? '<span class="sm-pill ok">شغال</span>' : '<span class="sm-pill y">لسه ماتجربش</span>';
  e.innerHTML = `<section class="card"><div class="row"><h3>الدفع بالكارت والتقسيط (Paymob)</h3><span class="space"></span>${pill}</div>
    <p class="sub">الزبون يدفع بالكارت أو المحفظة أو التقسيط على صفحة Paymob، والفلوس بتروح لحسابك إنت عند Paymob على طول. Sellybot مابياخدش منها حاجة، ورسوم Paymob بينك وبينهم.</p>
    ${T.linked && !T.live ? '<p class="sm-risk">مفاتيح تجربة: الزباين مش هيدفعوا فلوس حقيقية. حط مفاتيح Live قبل ما تفتح الدفع للزباين.</p>' : ""}${T.err ? `<p class="sm-risk">${esc(T.err)}</p>` : ""}
    ${T.linked && !T.worked && !T.err ? '<p class="sm-hint">المفاتيح اتحفظت. بيبان إنها شغالة مع أول دفع ناجح (جرّب بنفسك بأوردر صغير).</p>' : T.worked ? `<p class="sm-hint">أول دفع نجح ${esc(SB.day(T.worked))}.</p>` : ""}
    ${SB.tog("cdOn", T.on, "الدفع أونلاين في المتجر", T.linked ? "الزبون يختار الكارت أو المحفظة أو التقسيط وهو بيأكد الطلب" : "اربط حسابك الأول", `data-ch="cdOn"${T.linked ? "" : " disabled"}`)}
    ${T.waiting ? `<p class="sm-hint">${plural(T.waiting, "أوردر واحد مستني الدفع دلوقتي", "أوردرين مستنيين الدفع دلوقتي", "أوردرات مستنية الدفع دلوقتي", "أوردر مستني الدفع دلوقتي")}. اللي مايتدفعش خلال 30 دقيقة بيتلغى لوحده والكمية بترجع.</p>` : ""}
    <details class="sm-kn"${T.linked ? "" : " open"}><summary>${T.linked ? "بيانات الربط" : "اربط حسابك على Paymob"}</summary>
      <ol class="cd-steps"><li>من حسابك على Paymob: <b>Settings</b> ثم <b>API Keys</b>: انسخ <bdi dir="ltr">Secret Key</bdi> و<bdi dir="ltr">Public Key</bdi> (والـ <bdi dir="ltr">API Key</bdi> لو حابب).</li><li>من <b>Settings</b> ثم <b>Account info</b>: انسخ الـ <bdi dir="ltr">HMAC</bdi>.</li><li>من <b>Developers</b> ثم <b>Payment Integrations</b>: انسخ رقم الـ <bdi dir="ltr">Integration ID</bdi> لكل طريقة دفع عندك.</li></ol>
      <div class="sm-form cd-link"><div class="sm-2">${fld("secret", "Secret Key", "", { dir: "ltr", max: 300, type: "password", x: 'autocomplete="new-password"', ph: l.s ? "••••" + l.s : "", hint: T.linked ? "سيبه فاضي لو مش هتغيّره" : "بيتحفظ متشفّر، ومابيظهرش تاني لحد." })}${fld("public", "Public Key", "", { dir: "ltr", max: 300, x: 'autocomplete="off"', ph: l.p ? "••••" + l.p : "" })}</div>
        <div class="sm-2">${fld("hmac", "HMAC", "", { dir: "ltr", max: 200, type: "password", x: 'autocomplete="new-password"', ph: l.h ? "••••" + l.h : "" })}${fld("api", "API Key (اختياري)", "", { dir: "ltr", max: 1000, type: "password", x: 'autocomplete="new-password"', ph: l.a ? "••••" + l.a : "", hint: "بيخلّينا نسأل Paymob بنفسنا لو إشعار الدفع اتأخر" })}</div>
        ${T.api ? SB.tog("apiOff", false, "امسح الـ API Key") : ""}
        <div class="sm-2">${fld("card", "رقم Integration الكارت", T.card || "", { dir: "ltr", mode: "numeric", max: 10, x: "data-n" })}${fld("wallet", "رقم Integration المحافظ (اختياري)", T.wallet || "", { dir: "ltr", mode: "numeric", max: 10, x: "data-n" })}</div>
        <h4>شركات التقسيط اللي على حسابك (اختياري)</h4><p class="sm-hint">اختار الشركة واكتب رقم الـ Integration بتاعها. الزبون بيشوف «بالتقسيط مع ...» بس، والخطط بتظهرله في صفحة Paymob.</p>
        <div class="cd-g">${T.inst.map(x => `<label class="cd-i"><input type="checkbox" data-k="inst.${x.k}.on" ${x.on ? "checked" : ""}><b>${esc(x.n)}</b><input type="text" inputmode="numeric" dir="ltr" data-k="inst.${x.k}.id" value="${x.id || ""}" placeholder="Integration ID" aria-label="رقم ${esc(x.n)}" maxlength="10"></label>`).join("")}</div>
        ${fld("instMin", "التقسيط للطلبات من", T.instMin || "", { mode: "decimal", suf: "ج", hint: "فاضي = أي طلب", x: "data-n" })}
        <div class="row"><button type="button" class="btn" data-do="cdLink">${T.linked ? "احفظ التعديل" : "اربط"}</button><span class="space"></span>${T.linked ? '<button type="button" class="btn danger sm" data-do="cdUnlink">افصل الحساب</button>' : ""}</div></div></details></section>
    <section class="card"><div class="row"><h3>المنتجات الرقمية</h3><span class="space"></span>${dg.on ? '<span class="sm-pill ok">شغالة</span>' : '<span class="sm-pill g">مقفولة</span>'}</div>
      <p class="sub">ملف (PDF، ZIP، كتاب، صوت، فيديو، صور لحد 50 ميجا) أو لينك (كورس، فولدر) أو أكواد. مفيش شحن: الزبون بيدفع الأول (كارت أو تحويل) وبيوصله لينك خاص على الواتساب وفي صفحة الطلب.</p>
      ${SB.tog("cdDigi", dg.on, "بيع منتجات رقمية", "بتختار من المنتج نفسه إنه رقمي", 'data-ch="cdDigi"')}
      ${dg.on && !(T.on && T.linked) && !T.transfer ? '<p class="sm-risk">مفيش طريقة دفع مقدّم في متجرك (كارت أو تحويل)، فالمنتجات الرقمية مش هتتباع. فعّل واحدة منهم.</p>' : ""}
      <div class="sm-2 cd-dg">${fld("days", "اللينك شغال كام يوم", dg.days, { mode: "numeric", max: 3, suf: "يوم", x: "data-n" })}${fld("max", "كام مرة تحميل", dg.max, { mode: "numeric", max: 3, suf: "مرة", x: "data-n" })}</div>
      <div class="row"><button type="button" class="btn ghost sm" data-do="cdDg">احفظ</button><span class="space"></span><span class="sm-hint">${dg.count ? plural(dg.count, "منتج رقمي واحد", "منتجين رقميين", "منتجات رقمية", "منتج رقمي") + " · " : ""}الملفات: ${fmt(dg.mb)} من ${fmt(dg.maxMb)} ميجا</span></div></section>`;
}
const fresh = t => { T = t; draw(); };
const CH = { cdOn: v => run(null, async () => fresh(await post("card-set", { on: v }))), cdDigi: v => run(null, async () => fresh(await post("card-set", { digi: v }))) };
D.addEventListener("change", ev => { const b = ev.target.closest && ev.target.closest("[data-ch]"); if (!b || !b.closest(".cd-tab")) return; const f = CH[b.dataset.ch]; if (f) { ev.stopPropagation(); f(b.checked); } }, true);
A.cdLink = b => run(b, async () => {
  const d = SB.read(tabEl().querySelector(".cd-link")), inst = Object.entries(d.inst || {}).map(([k, v]) => ({ k, id: num(v.id), on: !!v.on })).filter(x => x.id > 0);
  fresh(await post("card-link", { secret: d.secret, public: d.public, hmac: d.hmac, api: d.api, apiOff: !!d.apiOff, card: d.card, wallet: d.wallet, inst, instMin: d.instMin, ...(T.linked ? {} : { on: true }) })); say(T.linked ? "اتحفظ ✅" : "اتربط ✅");
});
A.cdUnlink = async b => { if (await sure("الدفع أونلاين هيقف، ومش هتقدر ترجّع فلوس كارت من هنا لحد ما تربط تاني. تفصل الحساب؟", "افصله")) run(b, async () => { fresh(await post("card-unlink", {})); say("اتفصل."); }); };
A.cdDg = b => run(b, async () => { const d = SB.read(tabEl().querySelector(".cd-dg")); fresh(await post("card-set", { days: d.days, max: d.max })); say("اتحفظ ✅"); });

/* ================= an order: the payment at Paymob, refunds, the download links ================= */
const OC = {};
SB.cardUI = (o, again) => { const id = o.id; load(id, again); return `<div class="cd-o" data-cdo="${id}">${OC[id] && OC[id].d ? view(id) : ""}</div>`; };
const load = (id, again) => { OC[id] = { ...(OC[id] || {}), stale: false, again: again || (OC[id] || {}).again }; get("card-order", { id }).then(d => { OC[id].d = d; paint(id); }).catch(() => { }); };
const paint = id => { const e = D.querySelector(`[data-cdo="${id}"]`); if (e) e.innerHTML = view(id); };
const ST = { new: ["بتتجهز", "g"], made: ["فتح صفحة الدفع", "g"], pending: ["مستنيين Paymob", "y"], failed: ["مانجحتش", "bad"], paid: ["اتدفع", "ok"] }, RS = { making: ["بتترجع دلوقتي", "y"], done: ["رجعت", "ok"], failed: ["مارجعتش", "bad"], unsure: ["مش متأكدين", "y"] };
const pl = (m, s) => `<span class="sm-pill ${(m[s] || ["", "g"])[1]}">${(m[s] || [s])[0]}</span>`;
function view(id) {
  const x = OC[id], d = x.d; if (!d || (!d.card && !d.dg)) return ""; const ck = x.ck || (x.ck = "rf-" + Date.now().toString(36) + Math.random().toString(36).slice(2, 10));
  let h = "";
  if (d.card) h += `<section class="card"><div class="row"><h3>الدفع أونلاين: ${esc(d.way || "Paymob")}</h3><span class="space"></span>${d.wait ? `<span class="sm-pill y">مستني الدفع · ${plural(d.mins, "دقيقة", "دقيقتين", "دقايق", "دقيقة")}</span>` : d.pays.some(p => p.state === "paid" && !p.extra) ? '<span class="sm-pill ok">اتدفع</span>' : ""}</div>
    ${d.test ? '<p class="sm-risk">وضع تجربة: الفلوس دي مش حقيقية.</p>' : ""}${d.wait ? '<p class="sm-hint">الزبون لسه بيدفع. لو مادفعش خلال 30 دقيقة من الطلب، بيتلغى لوحده والكمية بترجع، ومفيش تنبيه بيوصلك غير لما يدفع.</p>' : ""}
    ${d.pays.length ? `<div class="cd-t">${d.pays.map(p => `<div><b>محاولة ${fmt(p.try)}</b> ${pl(ST, p.state)}<span>${esc(p.way)} · ${mny(p.amount)}</span>${p.txn ? `<small>عملية <bdi dir="ltr">${esc(p.txn)}</bdi>${p.pan ? ` · كارت آخره <bdi dir="ltr">${esc(p.pan)}</bdi>` : ""}</small>` : ""}${p.err ? `<small>${esc(p.err)}</small>` : ""}${p.extra ? "<small>دفع زيادة: بيرجع للزبون لوحده</small>" : ""}<span class="space"></span><small>${when(p.at)}</small></div>`).join("")}</div>` : '<p class="sm-hint">الزبون لسه مافتحش صفحة الدفع.</p>'}
    ${d.refunds.length ? `<h4>الفلوس اللي رجعت</h4><div class="cd-t">${d.refunds.map(r => `<div><b>${mny(r.amount)}</b> ${pl(RS, r.state)}<span>${r.kind === "void" ? "إلغاء العملية" : "استرجاع"}${r.auto ? " · دفع زيادة" : r.ret ? " · مرتجع" : ""}</span>${r.err ? `<small>${esc(r.err)}</small>` : ""}<span class="space"></span><small>${when(r.at)}</small>${r.state === "unsure" ? `<span class="row"><button type="button" class="btn ghost sm" data-do="cdMark:${id}:${r.id}:1">رجعت فعلًا</button><button type="button" class="btn ghost sm" data-do="cdMark:${id}:${r.id}:0">مارجعتش</button></span>` : ""}</div>`).join("")}</div>${d.refunds.some(r => r.state === "unsure") ? '<p class="sm-hint">Paymob مارّدتش. افتح العملية في حسابك على Paymob وقولنا رجعت ولا لأ.</p>' : ""}` : ""}
    ${d.left > 0 ? `<h4>رجّع فلوس الكارت</h4><div class="row cd-rf">${fld("amount", "المبلغ", d.left, { mode: "decimal", suf: "ج", x: "data-n" })}<button type="button" class="btn ghost" data-do="cdRefund:${id}">رجّع</button></div><p class="sm-hint">لحد ${mny(d.left)}. ${d.canVoid ? "لو رجّعت المبلغ كله النهارده، العملية بتتلغي من غير رسوم." : "بيرجع على نفس الكارت أو المحفظة، والبنك بياخد كام يوم."}</p>${d.rets.map(r => `<button type="button" class="btn ghost sm" data-do="cdRet:${id}:${r.id}">رجّع فلوس المرتجع على الكارت (${mny(r.amount)})</button>`).join("")}` : ""}</section>`;
  if (d.dg) h += `<section class="card"><h3>المنتجات الرقمية</h3>${d.dg.links.filter(z => !z.off).length ? `<div class="cd-t">${d.dg.links.filter(z => !z.off).map(z => `<div><b>${esc(z.n)}</b><span>${z.k === "codes" ? `${fmt(z.codes)} من ${fmt(z.q)} كود · ` : ""}اتفتح ${fmt(z.count)} من ${fmt(z.max)}</span><small>لحد ${esc(SB.day(z.until))}${z.sent ? " · اتبعت على الواتساب" : ""}${z.refused ? " · اترفض " + fmt(z.refused) : ""}</small><span class="space"></span><button type="button" class="btn ghost sm" data-do="cdNew:${id}:${z.id}">لينك جديد</button></div>`).join("")}</div><div class="row"><button type="button" class="btn ghost sm" data-do="cdSend:${id}:0">ابعت اللينكات تاني</button></div>` : d.dg.paid ? `<p class="sm-hint">اللينكات بتتعمل لوحدها بعد الدفع.</p><button type="button" class="btn ghost sm" data-do="cdSend:${id}:0">اعملها وابعتها</button>` : `<p class="sm-hint">لينك التحميل بيتعمل لوحده لما الأوردر يتدفع كله (كارت، أو تحويل توافق عليه).</p><button type="button" class="btn ghost sm" data-do="cdSend:${id}:1">ابعته برضه</button>`}</section>`;
  return h;
}
const redo = (id, d) => { OC[id].d = d; paint(id); if (OC[id].again) OC[id].again(); };
A.cdRefund = async (id, b) => { const e = D.querySelector(`[data-cdo="${id}"] .cd-rf`), v = num(e.querySelector("[data-k=amount]").value); if (!(v > 0)) return say("اكتب المبلغ.", true); if (!await sure(`هيرجع ${mny(v)} للزبون على كارته من حسابك على Paymob. متأكد؟`, "رجّعه", { danger: false })) return; run(b, async () => { const d = await post("card-refund", { id: +id, amount: v, ck: OC[id].ck }); OC[id].ck = null; redo(id, d); const r = d.refunds[d.refunds.length - 1]; say(r && r.state === "done" ? "رجعت ✅" : r && r.state === "unsure" ? "Paymob مارّدتش. راجعها في حسابك." : "مارجعتش: " + (r && r.err || ""), !(r && r.state === "done")); }); };
A.cdRet = async (id, ret, b) => { if (!await sure("هيرجع مبلغ المرتجع للزبون على كارته. متأكد؟", "رجّعه", { danger: false })) return; run(b, async () => { const d = await post("card-refund", { id: +id, ret: +ret, ck: "ret-" + ret }); redo(id, d); }); };
A.cdMark = (id, rid, yes, b) => run(b, async () => redo(id, await post("card-mark", { rid: +rid, done: yes === "1" })));
A.cdSend = (id, force, b) => run(b, async () => { redo(id, await post("digi-send", { id: +id, force: force === "1" })); say("اتبعت ✅"); });
A.cdNew = async (id, lid, b) => { if (!await sure("اللينك القديم هيقف، وهيتعمل لينك جديد بمدة وعدد مرات جديدين ويتبعت للزبون.", "اعمله", { danger: false })) return; run(b, async () => { redo(id, await post("digi-new", { lid: +lid })); say("اتعمل واتبعت ✅"); }); };

/* ================= the product editor: «منتج رقمي» ================= */
let G = null;
SB.digiUI = p => { G = { pid: p.id || 0, kind: "", link: "", multi: false, file: null, pend: null, free: 0, used: 0, on: null, codes: "", pct: -1 }; info(p.id); return '<section class="card cd-dgp" hidden></section>'; };
const info = async id => { try { const d = id ? await get("digi", { id }) : await get("card"); if (!G || G.pid !== (id || 0)) return; if (id) Object.assign(G, { on: d.on, kind: d.kind, was: d.kind, link: d.link, multi: d.multi, file: d.file, free: d.free, used: d.used, days: d.days, max: d.max, pay: d.pay, chunk: d.chunk, maxFile: d.maxFile }); else Object.assign(G, { on: d.digi.on, days: d.digi.days, max: d.digi.max, pay: (d.on && d.linked) || d.transfer > 0, chunk: 1500000, maxFile: 52428800 }); dgDraw(); } catch (e) { } };
const dgEl = () => D.querySelector(".cd-dgp");
function dgDraw() {
  const e = dgEl(); if (!e || !G) return; if (!G.on && !G.kind) { e.hidden = true; return; } e.hidden = false;
  e.innerHTML = `<h3>منتج رقمي</h3><p class="sub">مفيش شحن: الزبون بيدفع الأول وبيوصله لينك خاص على الواتساب وفي صفحة الطلب، شغال ${fmt(G.days)} يوم ولحد ${fmt(G.max)} مرات.</p>${G.on ? "" : '<p class="sm-risk">«المنتجات الرقمية» مقفولة من «الدفع والفروع»، فالمنتج ده مش بيتباع دلوقتي.</p>'}${G.pay === false ? '<p class="sm-risk">فعّل الدفع بالكارت أو تحويل من «الدفع والفروع»، المنتجات الرقمية مابتتباعش كاش.</p>' : ""}
    ${SB.seg("dgK", [["", "منتج بيتشحن"], ["file", "ملف"], ["link", "لينك"], ["codes", "أكواد"]], G.kind)}
    ${G.kind === "file" ? `<div class="sm-box">${G.file ? `<p><b>${esc(G.file.name)}</b> <small class="sm-hint">${size(G.file.size)}</small></p>` : G.pend ? `<p><b>${esc(G.pend.name)}</b> <small class="sm-hint">${size(G.pend.size)} · بيترفع بعد ما تحفظ المنتج</small></p>` : '<p class="sm-hint">لسه مفيش ملف. المنتج مش بيتباع من غيره.</p>'}${G.pct >= 0 ? `<div class="cd-bar" role="progressbar" aria-valuenow="${G.pct}" aria-valuemin="0" aria-valuemax="100"><i style="width:${G.pct}%"></i></div>` : ""}<div class="row"><button type="button" class="btn ghost sm" data-do="dgPick"${G.pct >= 0 ? " disabled" : ""}>${G.file || G.pend ? "غيّر الملف" : "اختار ملف"}</button><span class="sm-hint">PDF و ZIP و EPUB و MP3 و MP4 وصور، لحد 50 ميجا</span></div></div>` : ""}
    ${G.kind === "link" ? fld("dgLink", "اللينك اللي الزبون هيوصله", G.link, { dir: "ltr", max: 600, ph: "https://", hint: "بيظهر للزبون بس بعد الدفع، من لينك خاص بيتعدّ" }) : ""}
    ${G.kind === "codes" ? `<div class="sm-box"><p>${fmt(G.free)} كود فاضل · ${fmt(G.used)} اتباع</p>${fld("dgCodes", "ضيف أكواد (كود في كل سطر)", G.codes, { area: 4, max: 400000, dir: "ltr" })}<p class="sm-hint">كل قطعة بتتباع بكود واحد، ولما الأكواد تخلص المنتج بيخلص. المنتج ده مايبقاش ليه مقاسات أو ألوان.</p>${G.free ? '<button type="button" class="btn ghost sm" data-do="dgClear">امسح الأكواد اللي لسه ماتباعتش</button>' : ""}</div>` : ""}
    ${G.kind ? SB.tog("dgMulti", G.multi, "الزبون ياخد أكتر من واحد في الطلب", "من غيرها: واحد بس في كل طلب", 'data-ch="dgM"') : ""}`;
}
const size = b => b >= 1048576 ? fmt(Math.round(b / 104857.6) / 10) + " ميجا" : fmt(Math.max(1, Math.round(b / 1024))) + " كيلو";
const keepG = () => { const e = dgEl(); if (!e || !G) return; const l = e.querySelector('[data-k="dgLink"]'), c = e.querySelector('[data-k="dgCodes"]'), mm = e.querySelector('[data-k="dgMulti"]'); if (l) G.link = l.value.trim(); if (c) G.codes = c.value; if (mm) G.multi = mm.checked; };
A.dgK = k => { keepG(); G.kind = typeof k === "string" ? k : ""; dgDraw(); };
A.dgPick = async () => { const [f] = await SB.pick(false, ".pdf,.zip,.epub,.mp3,.mp4,image/*,application/pdf,application/zip,audio/mpeg,video/mp4"); if (!f) return; if (f.size > G.maxFile) return say("الملف لازم يكون لحد 50 ميجا.", true); keepG(); G.pend = f; G.file = null; dgDraw(); if (G.pid) run(null, () => upload(G.pid)); };
A.dgClear = async b => { if (await sure("تمسح الأكواد اللي لسه ماتباعتش؟", "امسحها")) run(b, async () => { const d = await post("digi-codes-del", { id: G.pid }); Object.assign(G, { free: d.free, used: d.used }); dgDraw(); }); };
const b64 = blob => new Promise((ok, no) => { const r = new FileReader(); r.onload = () => ok(String(r.result).split(",")[1] || ""); r.onerror = () => no(new Error("الملف ماتقراش.")); r.readAsDataURL(blob); });
async function upload(pid) {
  const f = G.pend; if (!f) return; const n = Math.max(1, Math.ceil(f.size / G.chunk)), up = (Date.now().toString(36) + Math.random().toString(36).slice(2) + "0000000000000000").slice(0, 24); let d = null;
  try { for (let i = 0; i < n; i++) { G.pct = Math.round(i / n * 100); dgDraw(); d = await post("digi-up", { id: pid, up, i, n, size: f.size, name: f.name, data: await b64(f.slice(i * G.chunk, Math.min(f.size, (i + 1) * G.chunk))) }); } }
  finally { G.pct = -1; }
  if (d && d.file) { G.file = d.file; G.pend = null; G.kind = "file"; } dgDraw(); say("الملف اترفع ✅");
}
/* after «احفظ المنتج»: what kind of digital product it is (and the file picked before it had an id) */
SB.digiSave = async pid => {
  if (!G || (G.on === null && !G.kind)) return; keepG(); if (!G.kind && !G.was) return; G.pid = pid; G.was = G.kind;
  const d = await post("digi-save", { id: pid, kind: G.kind, link: G.link, multi: G.multi, codes: G.kind === "codes" ? G.codes : "" }); Object.assign(G, { free: d.free, used: d.used, codes: "" });
  if (G.kind === "file" && G.pend) await upload(pid);
};
/* the switch inside the editor's panel: the panel's own change handler doesn't know it */
D.addEventListener("change", ev => { const b = ev.target.closest && ev.target.closest('[data-ch="dgM"]'); if (b && G) G.multi = b.checked; }, true);
})();
