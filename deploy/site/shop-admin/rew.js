/* «متجري» «الهدايا والنقط»: gift cards, customers' points and «هات صاحبك» (ShopRew.cs). Each has its own switch, off by default; the
   settings go with the store's others ("settings"). Also: the points in a customer's file (الزباين) and the cards' numbers in the reports. */
(() => {
"use strict";
const SB = window.SbShop, V = SB.v, { esc, fmt, mny, num, ico, fld, run, say, post, get, pane, sure, plural } = SB, D = document;
const st = () => SB.state(), day = t => t ? SB.day(t) : "", when = t => t ? SB.when(t) : "";
const css = D.createElement("style"); css.textContent = ".rw-ex{background:var(--soft);border:1px dashed var(--line);border-radius:12px;padding:10px 12px;font-size:13.5px;margin:4px 0 0}.rw-c{display:flex;align-items:center;gap:10px}.rw-code{font-family:ui-monospace,Consolas,monospace;letter-spacing:.5px;direction:ltr;unicode-bidi:isolate}.rw-mv{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:10px;align-items:center;padding:8px 2px;border-bottom:1px solid var(--line);font-size:14px}.rw-mv:last-child{border-bottom:0}.rw-mv b.p{color:var(--ok)}.rw-mv small{display:block;color:var(--muted);font-size:12.5px}.rw-warn{margin:8px 0}"; D.head.appendChild(css);
let R = null, Q = "", dirty = false, qT = 0;
const MO = [[0, "من غير تاريخ انتهاء"], [3, "3 شهور"], [6, "6 شهور"], [12, "سنة"], [24, "سنتين"]];
const mark = () => { dirty = true; const s = SB.body().querySelector(".sm-save"); if (s) s.classList.add("on"); ex(); };
const leave = async () => !dirty || (await sure("فيه تعديلات ماتحفظتش. تسيبها؟", "سيبها") ? (dirty = false, true) : false);
const GST = { active: ["شغال", "ok"], stopped: ["موقوف", "g"] }, RST = { pending: ["مستني يتسلّم", "y"], rewarded: ["خد مكافأته", "ok"], capped: ["الشهر خلّص مكافآته", "g"], void: ["اتلغى", "g"] };
const KT = { issue: "اتعمل", use: "اتدفع بيه", back: "رجع للكارت", stop: "اتوقّف", start: "اشتغل تاني", hand: "عملته بإيدك" };

/* the tab, after «المميزات» (the frame draws its list once) */
const open0 = SB.open; SB.open = (el, ctx) => { const p = open0(el, ctx); const nav = el.querySelector(".sm-nav"); if (nav && !nav.querySelector('[data-v="rew"]')) { const after = nav.querySelector('[data-v="feat"]'), b = D.createElement("button"); b.type = "button"; b.dataset.v = "rew"; b.dataset.do = "tab:rew"; b.innerHTML = '<span aria-hidden="true">🎁</span> الهدايا والنقط'; if (after) after.after(b); else nav.appendChild(b); } return p; };

V.rew = {
  async open(body) { R = await get("rew", Q ? { q: Q } : null); if (SB.view() !== "rew") return; dirty = false; V.rew.draw(body); },
  draw(body) {
    const s = R.set, g = s.gcard, p = s.points, f = s.refer, c = R.cards, P = R.pts, F = R.ref;
    const warn = t => `<div class="sm-rc y rw-warn"><span>⚠️</span><div><small>${t}</small></div></div>`;
    body.innerHTML = `<form class="sm-form" data-sub="rwSave" data-form>
      <section class="card"><h3>🎁 كروت هدايا</h3><p class="sub">الزبون يشتري كارت بمبلغ ويبعته هدية لحد على الواتساب. بيدفعه تحويل وانت توافق على صورته، وبعدها الكارت يطلع ويتبعت. اللي معاه الكارت يدفع بيه جزء من أي طلب أو كله، والباقي يفضل عليه.</p>
        ${SB.tog("gcard.on", g.on, "كروت هدايا في متجري", "صفحة «كروت هدايا» في القايمة وآخر الصفحة، وخانة «كارت هدية» في صفحة الطلب")}
        ${g.on && !R.ways ? warn("الزبون بيدفع تمن الكارت تحويل: فعّل طريقة تحويل واحدة على الأقل من «الدفع والفروع»، وإلا الصفحة هتقول إن الكروت مش متاحة.") : ""}${g.on && !R.wa ? warn("واتساب المتجر مش مربوط: الكارت مش هيتبعت لوحده. هتلاقي زرار تبعته بيه من موبايلك.") : ""}
        ${fld("gcard.amounts", "المبالغ اللي الزبون يختار منها", (g.amounts || []).join("، "), { mode: "numeric", hint: "افصل بينهم بفاصلة، لحد 8 مبالغ. فاضي = الزبون يكتب المبلغ" })}
        ${SB.tog("gcard.free", g.free, "الزبون يكتب أي مبلغ", "في الحدود دي")}<div class="sm-3">${fld("gcard.min", "من", g.min, { mode: "numeric", suf: "ج", x: "data-n" })}${fld("gcard.max", "لحد", g.max, { mode: "numeric", suf: "ج", x: "data-n" })}<div class="sm-f"><label>الكارت صالح</label>${SB.sel("gcard.months", MO, g.months)}</div></div>
        <div class="sm-nums"><div><small>كروت اتباعت</small><b>${fmt(c.sold)}</b><i>${mny(c.soldEgp)}</i></div><div><small>اتدفع بيها في طلبات</small><b>${mny(c.used)}</b></div><div><small>رصيد لسه في الكروت</small><b>${mny(c.out)}</b><i>من غير الموقوف والمنتهي</i></div><div><small>كروت عملتها بإيدك</small><b>${mny(c.hand)}</b></div></div>
        ${c.waiting ? `<button type="button" class="sm-rc y sm-go" data-do="tab:orders:pay" style="margin-top:10px"><span>💸</span><div><b>${plural(c.waiting, "كارت واحد مستني", "كارتين مستنيين", "كروت مستنية", "كارت مستني")} التحويل</b><small>الكارت بيطلع لما توافق على صورة التحويل من الأوردر.</small></div></button>` : ""}
        <div class="sm-tools" style="margin-top:14px"><div class="sm-q">${ico("search")}<input type="search" placeholder="كود أو اسم أو رقم" value="${esc(Q)}" data-in="rwq" aria-label="دوّر على كارت"></div><button type="button" class="btn ghost sm" data-do="rwNew">${ico("plus")} كارت بإيدك</button></div>
        <div class="sm-list rw-cards">${cards(c.list)}</div></section>
      <section class="card"><h3>⭐ نقط الزباين</h3><p class="sub">الزبون بياخد نقط على اللي دفعه في المنتجات (من غير الشحن والرسوم) لما الأوردر يتسلّم، وبيشوفها «جاية» قبلها. لو الأوردر رجع النقط بتتشال. بيستخدمها وهو داخل حسابه كخصم في صفحة الطلب.</p>
        ${SB.tog("points.on", p.on, "نقط في متجري")}${p.on && !R.acct ? warn("النقط محتاجة «حسابات الزباين» مفتوحة من «المميزات»، عشان الزبون يدخل ويستخدمها.") : ""}
        <div class="sm-3">${fld("points.per", "كل كام جنيه = نقطة", p.per, { mode: "decimal", suf: "ج", x: "data-n" })}${fld("points.pts", "كام نقطة", p.pts, { mode: "numeric", suf: "نقطة", x: "data-n" })}${fld("points.egp", "يساووا كام جنيه", p.egp, { mode: "decimal", suf: "ج", x: "data-n" })}</div>
        <div class="sm-3">${fld("points.least", "أقل نقط تتستخدم", p.least, { mode: "numeric", suf: "نقطة", x: "data-n" })}${fld("points.cap", "أقصى جزء من الطلب", p.cap, { mode: "numeric", suf: "%", x: "data-n" })}<div class="sm-f"><label>النقط صالحة</label>${SB.sel("points.months", MO.map(x => x[0] ? x : [0, "على طول"]), p.months)}</div></div>
        <p class="rw-ex" id="rwExP"></p>
        <div class="sm-nums"><div><small>نقط اتدّت</small><b>${fmt(P.given)}</b></div><div><small>اتستخدمت</small><b>${fmt(P.used)}</b></div><div><small>مع الزباين دلوقتي</small><b>${fmt(Math.max(0, P.left))}</b><i>تساوي ${mny(P.worth)}</i></div><div><small>زباين معاهم نقط</small><b>${fmt(P.holders)}</b></div></div>
        <div class="sm-tools" style="margin-top:14px"><div class="sm-q">${ico("search")}<input type="tel" inputmode="tel" dir="ltr" placeholder="01xxxxxxxxx" data-enter="rwPts" id="rwPh" aria-label="رقم الزبون"></div><button type="button" class="btn ghost sm" data-do="rwPts">نقط زبون</button></div>
        ${P.top.length ? `<div class="sm-list">${P.top.map(x => `<div class="sm-row"><span class="sm-em" aria-hidden="true">⭐</span><button type="button" class="grow" data-do="rwPts:${esc(x.phone)}"><b>${esc(x.name || x.phone)}</b><small><bdi dir="ltr">${esc(x.phone)}</bdi></small></button><span class="sm-pr"><b>${fmt(x.bal)} نقطة</b><small>${mny(x.worth)}</small></span></div>`).join("")}</div>` : ""}</section>
      <section class="card"><h3>👥 هات صاحبك</h3><p class="sub">الزبون اللي داخل حسابه ياخد لينك خاص بيه يبعته لصحابه. صاحبه ياخد هدية على أول طلب (رقم عمره ماطلب منك، مش رقم اللي بعت ولا عنوانه)، واللي بعت ياخد مكافأة بعد ما الطلب يتسلّم بس.</p>
        ${SB.tog("refer.on", f.on, "«هات صاحبك» في متجري")}${f.on && !R.acct ? warn("محتاجة «حسابات الزباين» مفتوحة من «المميزات»، عشان الزبون ياخد لينكه.") : ""}
        <div class="sm-3"><div class="sm-f"><label>هدية الصاحب</label>${SB.sel("refer.kind", [["amt", "خصم بمبلغ"], ["pct", "خصم بنسبة %"]], f.kind)}</div>${fld("refer.value", f.kind === "pct" ? "النسبة" : "المبلغ", f.value, { mode: "numeric", suf: f.kind === "pct" ? "%" : "ج", x: "data-n" })}${fld("refer.least", "للطلبات من", f.least || "", { mode: "numeric", suf: "ج", hint: "فاضي = أي طلب", x: "data-n" })}</div>
        <div class="sm-2">${fld("refer.reward", "مكافأة اللي بعت", f.reward, { mode: "numeric", suf: "ج", x: "data-n", hint: p.on ? "بتنزله نقط بنفس القيمة" : "كود خصم بالمبلغ ده، صالح 90 يوم، لطلب واحد" })}${fld("refer.month", "أقصى مكافآت لزبون في الشهر", f.month, { mode: "numeric", x: "data-n" })}</div>
        <p class="rw-ex" id="rwExR"></p>
        <div class="sm-nums"><div><small>أصحاب طلبوا</small><b>${fmt(F.friends)}</b><i>من ${plural(F.senders, "زبون واحد", "زبونين", "زباين", "زبون")} معاهم لينك</i></div><div><small>مكافآت اتدّت</small><b>${fmt(F.rewarded)}</b><i>${mny(F.rewards)}</i></div><div><small>خصم لأصحاب</small><b>${mny(F.off)}</b></div><div><small>من غير مكافأة (حد الشهر)</small><b>${fmt(F.capped)}</b></div></div>
        ${F.list.length ? `<div class="sm-list" style="margin-top:10px">${F.list.map(x => `<div class="sm-row"><span class="sm-em" aria-hidden="true">👥</span><button type="button" class="grow" data-do="rwOrd:${x.order}"><b>أوردر #${x.no} · <bdi dir="ltr">${esc(x.friend)}</bdi></b><small>من <bdi dir="ltr">${esc(x.sender)}</bdi> · خصم ${mny(x.off)}${x.st === "rewarded" ? " · " + (x.pts ? fmt(x.pts) + " نقطة" : "كود " + esc(x.code) + (x.used ? " (اتستخدم)" : "")) : ""} · ${day(x.at)}</small></button><span class="sm-pill ${(RST[x.st] || RST.void)[1]}">${(RST[x.st] || RST.void)[0]}</span></div>`).join("")}</div>` : ""}</section>
      <div class="sm-save"><span>فيه تعديلات ماتحفظتش</span><button type="button" class="btn" data-do="rwSave">احفظ</button></div></form>`;
    body.querySelector("[data-form]").addEventListener("input", mark); body.querySelector("[data-form]").addEventListener("change", mark); ex();
  },
  leave,
  inp: { rwq: v => { clearTimeout(qT); qT = setTimeout(async () => { Q = v.trim(); const r = await run(null, () => get("rew", Q ? { q: Q } : null)); if (!r || SB.view() !== "rew") return; R.cards = r.cards; const l = SB.body().querySelector(".rw-cards"); if (l) l.innerHTML = cards(R.cards.list); }, 300); } },
  acts: {
    rwSave: b => run(b.tagName === "FORM" ? b.querySelector(".sm-save .btn") : b, async () => {
      const d = SB.read(SB.body().querySelector("[data-form]")); d.gcard.amounts = String(d.gcard.amounts || "").split(/[^\d٠-٩]+/).map(num).filter(x => x > 0);
      if (d.gcard.on && !d.gcard.amounts.length && !d.gcard.free) throw new Error("اكتب مبلغ واحد على الأقل، أو خلّي الزبون يكتب المبلغ.");
      d.gcard.months = +d.gcard.months; d.points.months = +d.points.months; d.refer.least = d.refer.least || 0;
      await post("settings", { settings: { gcard: d.gcard, points: d.points, refer: d.refer } }); dirty = false; say("اتحفظ ✅"); SB.show("rew");
    }),
    rwNew: () => issue(), rwCard: id => cardPane(+id), rwOrd: id => SB.openOrder(+id),
    rwPts: ph => { const v = typeof ph === "string" ? ph : (D.getElementById("rwPh") || {}).value; ptsPane(v); }
  }
};
const cards = l => l.length ? l.map(x => `<div class="sm-row ${x.st !== "active" || x.exp ? "dim" : ""}"><span class="sm-em" aria-hidden="true">${x.kind === "hand" ? "🎀" : "🎁"}</span><button type="button" class="grow" data-do="rwCard:${x.id}"><b><span class="rw-code">${esc(x.code)}</span> · ${esc(x.to || "من غير اسم")}</b><small>${x.kind === "hand" ? "بإيدك: " + esc(x.note) : "من " + esc(x.buyer) + (x.no ? " · أوردر #" + x.no : "")} · ${x.sentAt ? "اتبعت " + when(x.sentAt) : x.err ? "مابعتش: " + esc(x.err) : x.toPhone ? (new Date(x.sendAt) > new Date() ? "هيتبعت " + when(x.sendAt) : "بيتبعت دلوقتي") : "مالوش رقم"}</small></button><span class="sm-pr"><b>${mny(x.bal)}</b><small>من ${mny(x.amount)}</small></span><span class="sm-pill ${x.exp ? "g" : GST[x.st][1]}">${x.exp ? "خلص" : GST[x.st][0]}</span></div>`).join("") : `<div class="sm-empty"><span aria-hidden="true">🎁</span><p>${Q ? "مفيش كارت بالكلام ده." : "لسه مفيش كروت."}</p></div>`;
// the examples, from the owner's own numbers
function ex() {
  const f = SB.body().querySelector("[data-form]"); if (!f) return; const d = SB.read(f), p = d.points || {}, r = d.refer || {};
  const per = num(p.per) || 10, pts = num(p.pts) || 100, egp = num(p.egp) || 10, cap = num(p.cap) || 30, least = num(p.least) || 0;
  const n = Math.floor(1000 / per), w = Math.floor(n * egp / pts * 100) / 100, e = D.getElementById("rwExP");
  if (e) e.textContent = `مثال: أوردر منتجاته بـ 1,000 ج يجيب ${fmt(n)} نقطة، تساوي ${fmt(w)} ج. وفي أوردر بـ 1,000 ج يقدر يستخدم نقط لحد ${fmt(Math.floor(1000 * cap / 100))} ج${least ? `، ومن ${fmt(least)} نقطة على الأقل` : ""}.`;
  const e2 = D.getElementById("rwExR"), gift = r.kind === "pct" ? fmt(num(r.value)) + "%" : fmt(num(r.value)) + " ج", rw = num(r.reward);
  if (e2) e2.textContent = `مثال: صاحب طلب بـ 600 ج${num(r.least) ? ` (والحد ${fmt(num(r.least))} ج)` : ""} ياخد خصم ${r.kind === "pct" ? fmt(Math.round(600 * num(r.value) / 100)) + " ج (" + gift + ")" : gift}، ولما يستلم، اللي بعتله ياخد ${d.points && d.points.on ? fmt(Math.floor(rw * pts / egp)) + " نقطة (= " + fmt(rw) + " ج)" : "كود بـ " + fmt(rw) + " ج"}، لحد ${fmt(num(r.month) || 5)} مكافآت في الشهر.`;
}

/* a card: its movements, stop it, send it again, or send it from the owner's own phone */
async function cardPane(id) {
  const g = await run(null, () => get("rew-card", { id })); if (!g) return;
  const wa = g.toPhone ? "https://wa.me/2" + g.toPhone.replace(/\D/g, "") + "?text=" + encodeURIComponent(g.text) : "";
  const p = pane({ title: "كارت " + g.code, cls: "small", html: () => `<div class="sm-form"><div class="sm-nums"><div><small>الرصيد</small><b>${mny(g.bal)}</b><i>من ${mny(g.amount)}</i></div><div><small>صالح لحد</small><b style="font-size:16px">${g.until ? day(g.until) : "على طول"}</b>${g.exp ? "<i>خلص</i>" : ""}</div></div>
    <p class="sm-hint">${g.kind === "hand" ? "عملته بإيدك: " + esc(g.note) : "اشتراه " + esc(g.buyer) + " <bdi dir=ltr>" + esc(g.buyerPhone) + "</bdi>" + (g.no ? " · أوردر #" + g.no : "")}<br>لـ ${esc(g.to || "—")} <bdi dir="ltr">${esc(g.toPhone)}</bdi>${g.msg ? "<br>«" + esc(g.msg) + "»" : ""}<br>${g.sentAt ? "اتبعت على الواتساب " + when(g.sentAt) : g.err ? "مابعتش: " + esc(g.err) : g.toPhone ? "هيتبعت " + when(g.sendAt) : ""}</p>
    <div class="row">${g.toPhone && g.st === "active" ? `<button type="button" class="btn ghost sm" data-do="re">ابعته تاني</button><a class="btn ghost sm" href="${esc(wa)}" target="_blank" rel="noopener">ابعته من موبايلك</a>` : ""}<button type="button" class="btn ${g.st === "active" ? "danger" : ""} sm" data-do="stop">${g.st === "active" ? "وقّفه" : "شغّله تاني"}</button></div>
    <h4>حركة الرصيد</h4>${g.moves.map(m => `<div class="rw-mv"><span>${m.order ? `<button type="button" class="sm-lnk" data-do="ord:${m.order}">#${m.no}</button>` : "—"}</span><span>${KT[m.k] || esc(m.k)}${m.note ? `<small>${esc(m.note)}</small>` : ""}<small>${when(m.at)}</small></span><b class="${m.d > 0 ? "p" : ""}" dir="ltr">${m.d ? (m.d > 0 ? "+" : "") + fmt(m.d) : ""}</b></div>`).join("")}</div>`,
    acts: {
      re: b => run(b, async () => { await post("rew-card-send", { id }); p.close(true); say("هيتبعت خلال دقيقة ✅"); SB.show("rew"); }),
      stop: async b => { if (g.st === "active" && !await sure("توقّف الكارت ده؟ مش هينفع يتدفع بيه لحد ما تشغّله تاني.", "وقّفه")) return; run(b, async () => { await post("rew-card-stop", { id, stop: g.st === "active" }); p.close(true); SB.show("rew"); }); },
      ord: oid => { p.close(true); SB.openOrder(+oid); }
    } });
}
function issue() {
  const p = pane({ title: "كارت بإيدك", cls: "small", html: () => `<div class="sm-form"><p class="sub">تعويض عن مشكلة أو هدية لزبون. الكارت بيتعمل على طول، ولو كتبت رقم بيتبعتله على الواتساب.</p>${fld("amount", "المبلغ", "", { mode: "numeric", suf: "ج", x: "data-n", must: true })}${fld("note", "السبب", "", { max: 200, must: true, ph: "مثلًا: تعويض عن أوردر اتأخر" })}<div class="sm-2">${fld("toName", "اسمه", "", { max: 80 })}${fld("toPhone", "رقم الواتساب", "", { mode: "tel", dir: "ltr", ph: "01xxxxxxxxx" })}</div>${fld("msg", "رسالة معاه (اختياري)", "", { area: 2, max: 300 })}<div class="sm-f"><label>صالح</label>${SB.sel("months", MO, (R && R.set.gcard.months) || 12)}</div></div>`,
    foot: () => '<button type="button" class="btn" data-do="go">اعمل الكارت</button>',
    acts: { go: b => run(b, async () => { const d = SB.read(p.el); d.months = +d.months; const r = await post("rew-card-new", d); p.close(true); say("الكارت اتعمل ✅"); await SB.show("rew"); cardPane(r.id); }) } });
}
/* a customer's points: the balance, every movement, add or take away with a reason */
async function ptsPane(ph) {
  ph = String(ph || "").trim(); if (!/^0?1[0125]\d{8}$/.test(ph.replace(/\D/g, "").replace(/^(0020|20)(?=1)/, "0"))) return say("اكتب رقم موبايل الزبون صح.", true);
  let x = await run(null, () => get("rew-pts", { phone: ph })); if (!x) return;
  const p = pane({ title: "نقط " + (x.name || x.phone), cls: "small", html: () => `<div class="sm-form"><div class="sm-nums"><div><small>رصيده</small><b>${fmt(x.bal)} نقطة</b><i>تساوي ${mny(x.worth)}</i></div></div>
    <div class="sm-2">${fld("n", "زوّد أو شيل", "", { mode: "numeric", dir: "ltr", hint: "بالسالب عشان تشيل، زي -50", x: "data-n" })}${fld("why", "السبب (الزبون بيشوفه)", "", { max: 200 })}</div><button type="button" class="btn sm" data-do="go">سجّل</button>
    <h4>حركة النقط</h4>${x.list.length ? x.list.map(m => `<div class="rw-mv"><span>${m.no ? "#" + m.no : "—"}</span><span>${esc(m.t)}${m.note ? `<small>${esc(m.note)}</small>` : ""}<small>${when(m.at)}${m.exp && m.n > 0 ? " · صالحة لحد " + day(m.exp) : ""}</small></span><b class="${m.n > 0 ? "p" : ""}" dir="ltr">${m.n > 0 ? "+" : ""}${fmt(m.n)}</b></div>`).join("") : '<p class="sm-hint">مفيش حركة.</p>'}</div>`,
    acts: { go: b => run(b, async () => { const d = SB.read(p.el); if (!d.n) throw new Error("اكتب عدد النقط."); x = await post("rew-pts-add", { phone: x.phone, n: d.n, why: d.why }); p.draw(); say("اتسجّل ✅"); }) } });
}

/* «الزباين»: a customer's points from their menu */
if (V.custs && V.custs.acts && V.custs.acts.cMore) { const m0 = V.custs.acts.cMore; V.custs.acts.cMore = ph => { m0(ph); const s = st(), p = SB.top(); if (!p || !s || !(s.settings.points || {}).on) return; p.acts = p.acts || {}; p.acts.rwPts = () => { p.close(true); ptsPane(ph); }; const f = p.el.querySelector(".sm-form"), h = f && f.querySelector("hr"); if (h) h.insertAdjacentHTML("beforebegin", '<button type="button" class="btn ghost" data-do="rwPts">⭐ نقطه</button>'); }; }

/* «التقارير»: cards sold and used, points and friends, apart from the sales */
const iso = d => { const z = new Date(d.getTime() - d.getTimezoneOffset() * 6e4); return z.toISOString().slice(0, 10); };
if (V.report && V.report.draw) { const d0 = V.report.draw; V.report.draw = body => { d0(body); const s = st(); if (!s || !body.querySelector(".sm-nums") || !body.querySelector('[data-do="rt:sum"][aria-checked="true"]')) return; const on = ["gcard", "points", "refer"].some(k => (s.settings[k] || {}).on);
  const k = ((body.querySelector('.sm-chips [data-do^="rk:"].on') || {}).dataset || {}).do || "rk:30", n = k.slice(3); let q;
  if (n === "x") q = { from: (body.querySelector('[data-k="from"]') || {}).value || "", to: (body.querySelector('[data-k="to"]') || {}).value || "" }; else { const t = new Date(), f = new Date(); f.setDate(f.getDate() - (+n - 1)); q = { from: iso(f), to: iso(t) }; }
  get("rew-report", q).then(r => { if (SB.view() !== "report" || !body.isConnected || body.querySelector(".rw-rep")) return; const c = r.cards, p = r.pts, f = r.ref; if (!on && !c.sold && !c.used && !p.given && !f.friends) return;
    const sec = D.createElement("section"); sec.className = "card rw-rep"; sec.innerHTML = `<h3>🎁 الكروت والنقط</h3><p class="sub">برّه المبيعات: فلوس الكارت بتتحسب مرة واحدة لما يتباع، واللي اتدفع بيه في الطلبات مش فلوس جديدة.</p><div class="sm-nums"><div><small>كروت اتباعت</small><b>${fmt(c.sold)}</b><i>${mny(c.soldEgp)}</i></div><div><small>اتدفع بالكروت في طلبات</small><b>${mny(c.used)}</b></div>${c.hand ? `<div><small>كروت بإيدك</small><b>${mny(c.hand)}</b></div>` : ""}<div><small>نقط اتدّت</small><b>${fmt(p.given)}</b></div><div><small>خصم بالنقط</small><b>${mny(p.off)}</b><i>${fmt(p.used)} نقطة</i></div><div><small>أصحاب طلبوا</small><b>${fmt(f.friends)}</b><i>خصم ${mny(f.off)}</i></div></div>`;
    const after = body.querySelectorAll("section.card")[1]; if (after) after.after(sec); else body.appendChild(sec); }).catch(() => { }); }; }
})();
