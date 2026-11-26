/* «متجري»: «الموظفين», people who help in the store with what the owner lets them do (api/team.ashx, ShopStaff.cs).
   They sign in at the link below with a code on WhatsApp and see only their part; the owner stops them at once and sees who did what. */
(() => {
"use strict";
const SB = window.SbShop, V = SB.v, { esc, fmt, ico, fld, run, say, pane, sure, plural } = SB, D = document;
if (SB.tabs && !SB.tabs.some(t => t[0] === "team")) SB.tabs.push(["team", "🧑‍💼", "الموظفين"]);
const API = "api/team.ashx", call = b => SB.ask(API, b ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) } : undefined);
const P = { orders: ["🧾", "الأوردرات بس", "يشوف الأوردرات ويغيّر حالتها ويطبع البوليصة والفاتورة. من غير إجماليات الفلوس ولا تنزيل بيانات الزباين."],
  products: ["📦", "المنتجات والمخزون", "يضيف ويعدّل المنتجات والصور والأقسام والكميات."],
  returns: ["↩️", "المرتجعات والتقييمات", "يرد على طلبات الاسترجاع والاستبدال والتقييمات."],
  money: ["📊", "التقارير والفلوس", "يشوف أرقام المتجر والتقارير ويراجع صور التحويلات."],
  ship: ["🚚", "الشحن", "يبعت الأوردرات لشركة الشحن ويطبع البوليصات. من غير مفاتيح الربط."] };
const ROLES = [["الأوردرات بس", ["orders"]], ["المخزن", ["products"]], ["خدمة العملا", ["orders", "returns"]], ["الشحن", ["orders", "ship"]], ["المحاسب", ["money"]]];
const ACT = { "order-status": "غيّر حالة أوردر", "order-note": "كتب ملاحظة على أوردر", "order-ship": "سجّل رقم بوليصة", "prod-save": "حفظ منتج", "prod-del": "مسح منتجات", "prod-dup": "كرّر منتج", "prod-bulk": "عدّل منتجات مرة واحدة", "prod-sort": "رتّب المنتجات", stock: "عدّل الكمية", image: "رفع صورة", "image-del": "مسح صورة", "image-sort": "رتّب الصور", "image-alt": "عدّل وصف صورة", "cat-save": "حفظ قسم", "cat-del": "مسح قسم", "cat-sort": "رتّب الأقسام", "cat-products": "حط منتجات في قسم", "return-do": "رد على مرتجع", "review-do": "رد على تقييم", "pay-review": "راجع صورة تحويل", "ship-send": "بعت لشركة الشحن", "ship-check": "راجع قبل الشحن", "ship-labels": "طبع بوليصات الشركة", "ship-pickup": "طلب المندوب", "ship-cancel": "لغى شحنة", "ship-ask": "سأل الشركة عن شحنة", "ship-drop": "شال شحنة من القايمة", "ship-got": "المرتجع وصل", import: "رفع شيت منتجات", video: "رفع فيديو منتج" };
const ST = { "new": "جديد", noreply: "مردّش", confirmed: "متأكد", shipped: "اتشحن", out: "خرج للتوصيل", delivered: "اتسلم", cancelled: "ملغي", returned: "مرتجع" };
let T = null;
const css = D.createElement("style"); css.textContent = ".sm-row.tm{flex-wrap:wrap;cursor:default}.sm-row.tm:hover{background:none}.sm-row.tm .grow{min-width:180px}.sm-row.tm .grow small{display:block;color:var(--muted);font-size:12.5px}.sm-row.tm .sm-av{width:40px;height:40px;font-size:17px}.tm-h{margin:14px 0 2px;font-size:14px}.tm-p .toggle{margin-top:10px}.tm-link{flex-wrap:nowrap}.tm-link input{flex:1;min-width:0;width:auto}"; D.head.appendChild(css);
const ago = t => t ? SB.ago(t) : "";
const refText = r => esc(String(r || "").split(" · ").map(x => ST[x] ? "← " + ST[x] : /^[\d,]+$/.test(x) ? "#" + x.split(",").join("، #") : x).join(" · "));

function draw() {
  const b = SB.body(); if (!b || SB.view() !== "team") return; const on = T.on, full = T.list.length >= T.max;
  b.innerHTML = `<section class="card"><div class="row"><h3>فريق المتجر</h3><span class="space"></span>${SB.tog("on", on, on ? "شغال" : "مقفول", "", 'data-ch="tOn"')}</div>
      <p class="sub">ضيف اللي بيساعدوك في المتجر. كل واحد يدخل برقم الواتساب بتاعه وكود بيوصله، ويشوف الحاجات اللي إنت تختارها بس. عمره ما يشوف الإعدادات ولا المفاتيح ولا لينكات الدفع ولا الباقة ولا الفلوس إلا لو اخترتله «التقارير والفلوس». الدخول بيخلص لوحده بعد ${fmt(12)} ساعة.</p>
      ${on ? `<div class="sm-f"><label for="tLink">لينك دخول الموظفين</label><div class="row tm-link"><input id="tLink" type="text" readonly dir="ltr" value="${esc(T.link)}"><button type="button" class="btn ghost sm" data-do="copy:${encodeURIComponent(T.link)}">${ico("copy")} انسخ</button></div>
        <small class="sm-hint">${T.from === "store" ? "الكود بيوصلهم من رقم واتساب متجرك." : T.from === "sellybot" ? "الكود بيوصلهم من رقم Sellybot، لأن واتساب متجرك مش مربوط." : "⚠️ الأكواد مش هتوصل: اربط واتساب متجرك الأول."}${T.fail ? ` آخر كود ماوصلش: ${esc(T.fail)}` : ""}</small></div>` : `<p class="sm-hint">وهو مقفول محدش من الفريق يقدر يدخل، واللي داخل بيخرج على طول.</p>`}</section>
    <section class="card"><div class="row"><h3>الموظفين</h3><span class="space"></span><small class="sm-hint">${fmt(T.list.length)} من ${fmt(T.max)}</small><button type="button" class="btn sm" data-do="tAdd" ${full ? "disabled" : ""}>${ico("plus")} ضيف موظف</button></div>
      ${T.list.length ? `<div class="sm-list">${T.list.map(m => `<div class="sm-row tm ${m.active ? "" : "dim"}"><span class="sm-av" aria-hidden="true">${esc(m.name.trim().charAt(0))}</span><div class="grow"><b>${esc(m.name)} ${m.active ? "" : '<span class="sm-pill g">موقوف</span>'}</b><small><bdi dir="ltr">${esc(m.phone)}</bdi> · ${m.perms.map(p => P[p][1]).join("، ")}</small><small>${m.lastIn ? "آخر دخول " + ago(m.lastIn) + (m.lastAt && m.lastAt !== m.lastIn ? " · آخر حركة " + ago(m.lastAt) : "") : "لسه مادخلش"}</small></div>
          <span class="row"><button type="button" class="btn ghost sm" data-do="tEdit:${m.id}">${ico("pen")} عدّل</button>${m.active ? `<button type="button" class="btn ghost sm" data-do="tStop:${m.id}">وقّفه</button>` : `<button type="button" class="btn ghost sm" data-do="tStart:${m.id}">رجّعه</button>`}<button type="button" class="sm-ic" data-do="tDel:${m.id}" aria-label="امسح ${esc(m.name)}">${ico("bin")}</button></span></div>`).join("")}</div>` : `<div class="sm-empty"><span aria-hidden="true">🧑‍💼</span><p>لسه مفيش حد في الفريق.</p></div>`}</section>
    <section class="card"><h3>مين عمل إيه</h3>${T.log.length ? `<ol class="sm-log">${T.log.map(x => `<li><b>${esc(x.name)}: ${esc(ACT[x.act] || x.act)}</b><small>${x.ref ? refText(x.ref) + " · " : ""}${SB.when(x.at)}</small></li>`).join("")}</ol>` : `<p class="sm-hint">كل حاجة الفريق يعملها هتظهر هنا، وفي سجل كل أوردر باسم اللي عملها.</p>`}</section>`;
}

function edit(m) {
  m = m || { id: 0, name: "", phone: "", perms: ["orders"] }; let perms = new Set(m.perms);
  const p = pane({ title: m.id ? "عدّل " + m.name : "موظف جديد", cls: "small", html: () => `<div class="sm-form">${fld("name", "الاسم", m.name, { max: 20, must: true, x: "autofocus" })}${fld("phone", "رقم الواتساب", m.phone, { mode: "tel", dir: "ltr", must: true, ph: "01001234567", hint: "الكود بيوصله على الرقم ده." })}
      <div class="sm-f"><label>دور جاهز</label><div class="sm-chips">${ROLES.map(r => `<button type="button" class="${r[1].length === perms.size && r[1].every(x => perms.has(x)) ? "on" : ""}" data-do="role:${r[1].join(",")}">${r[0]}</button>`).join("")}</div></div>
      <h4 class="tm-h">يقدر يعمل إيه</h4><div class="tm-p">${Object.keys(P).map(k => SB.tog("p." + k, perms.has(k), P[k][0] + " " + P[k][1], P[k][2], `data-ch="perm:${k}"`)).join("")}</div>
      <p class="sm-hint">مش هيقدر أبدًا يغيّر إعدادات المتجر أو الشكل أو المفاتيح أو لينكات الدفع، ولا الباقة والإيجار، ولا الفريق، ولا ياخد نسخة من المتجر، ولا يمسح بيانات زبون.</p></div>`,
    foot: () => `<button type="button" class="btn" data-do="go">${m.id ? "احفظ" : "ضيفه"}</button><button type="button" class="btn ghost" data-do="paneClose">إلغاء</button>`,
    acts: { role: l => { const keep = SB.read(p.el); m.name = keep.name; m.phone = keep.phone; perms = new Set(l.split(",")); p.draw(); },
      go: b => run(b, async () => { const d = SB.read(p.el); T = await call({ a: "save", id: m.id, name: d.name, phone: d.phone, perms: [...perms] }); p.close(true); draw(); say(m.id ? "اتحفظ ✅" : "اتضاف ✅ ابعتله لينك الدخول."); }) },
    ch: { perm: (on, k) => { if (on) perms.add(k); else perms.delete(k); const keep = SB.read(p.el); m.name = keep.name; m.phone = keep.phone; p.draw(); } } });
}

V.team = {
  async open(body) { T = await call(); draw(); },
  ch: { tOn: (on, el) => run(null, async () => { if (!on && T.list.some(m => m.active) && !await SB.sure("تقفل الفريق؟ اللي داخل هيخرج على طول، ومحدش هيقدر يدخل لحد ما تفتحه تاني.", "اقفله")) { el.checked = true; return; } T = await call({ a: "on", on }); draw(); say(on ? "الفريق اتفتح ✅" : "الفريق اتقفل"); }) },
  acts: {
    tAdd: () => edit(), tEdit: id => edit({ ...T.list.find(m => m.id === +id) }),
    tStop: async (id, b) => { const m = T.list.find(x => x.id === +id); if (await sure(`توقّف ${esc(m.name)}؟ هيخرج على طول ومش هيقدر يدخل لحد ما ترجّعه.`, "وقّفه")) run(b, async () => { T = await call({ a: "stop", id: +id }); draw(); say("اتوقف"); }); },
    tStart: (id, b) => run(b, async () => { T = await call({ a: "start", id: +id }); draw(); say("رجع ✅"); }),
    tDel: async (id, b) => { const m = T.list.find(x => x.id === +id); if (await sure(`تمسح ${esc(m.name)} من الفريق؟ اللي عمله هيفضل في السجل باسمه.`, "امسحه")) run(b, async () => { T = await call({ a: "del", id: +id }); draw(); say("اتمسح"); }); }
  }
};
})();
