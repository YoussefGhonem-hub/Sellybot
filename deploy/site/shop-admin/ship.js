/* «متجري»: shipping companies (Bosta, ShipBlu, J&T, Aramex): linking the merchant's account, sending orders, labels, the courier, what the company says.
   The tab starts with «هتوصّل الأوردرات إزاي؟»: «بوصّل بنفسي» (deliver.js) and a card per company. The companies show only when the server sends
   state.carriers. */
(() => {
"use strict";
const SB = window.SbShop, { esc, fmt, mny, ico, fld, run, say, post, get, pane, sure, plural } = SB, D = document, A = SB.acts;
const car = () => (SB.state() || {}).carriers || null, linked = () => (car() ? car().list.filter(x => x.linked && !x.soon) : []), API = "api/shop.ashx";
const css = D.createElement("style"); css.textContent = ".sh-c{border:1px solid var(--line);border-radius:14px;padding:14px;display:grid;gap:8px;align-content:start}.sh-g{display:grid;gap:12px;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));margin-top:10px}.sh-c p{margin:0}.sh-c .row{gap:8px}.sh-r{border:1px solid var(--line);border-radius:12px;padding:12px;margin-bottom:10px;display:grid;gap:6px}.sh-r.bad{border-color:color-mix(in srgb,var(--err) 45%,var(--line))}.sh-r.ok{border-color:color-mix(in srgb,var(--ok) 45%,var(--line))}.sh-r.need{border-color:#d8930f88}.sh-r p{margin:0;font-size:13px;color:var(--muted)}.sh-a{display:flex;gap:6px;flex-wrap:wrap;align-items:center}.sh-a button{border:1px solid var(--line);background:var(--card);border-radius:99px;padding:5px 12px;font-size:13px;cursor:pointer;min-height:34px}.sh-a button:hover{border-color:var(--brand)}.sh-tr{display:flex;align-items:center;gap:6px;font-size:16px;font-weight:600}.sh-w{color:#a86a00;font-size:13px}[data-theme=dark] .sh-w{color:#f0c36a}.sh-ol{margin:0 0 12px;padding-inline-start:20px;line-height:1.9}.sh-c .sm-pill{justify-self:start}.sh-c .sh-k{font-size:13px;color:var(--muted);line-height:1.7}.sh-c .sh-k b{color:var(--ink)}.sh-ad{display:flex;gap:6px;align-items:center;min-width:0}.sh-ad input{flex:1;min-width:0;direction:ltr;font-size:12px;padding:6px 8px;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--ink)}.sh-c details summary{cursor:pointer;font-size:13px;color:var(--brand)}.sh-c details ol{margin:6px 0 0;padding-inline-start:18px;font-size:13px;line-height:1.8}.sh-fx{display:flex;gap:6px;flex-wrap:wrap;align-items:center}.sh-fx input{flex:1;min-width:140px;direction:ltr;padding:8px;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--ink)}"; D.head.appendChild(css);
const EM = { bosta: "🔴", shipblu: "🔵", jt: "🟥", aramex: "🟧" }, TONE = { made: "b", picked: "b", transit: "b", out: "b", attempt: "bad", delivered: "ok", returning: "y", returned: "y", cancelled: "g", lost: "bad", hold: "y" };
const pillOf = x => `<span class="sm-pill ${x.state === "unsure" || x.state === "failed" ? "bad" : x.state === "off" ? "g" : x.state === "making" ? "y" : TONE[x.ours] || "b"}">${esc(x.text)}</span>`;
const orders = n => plural(n, "أوردر واحد", "أوردرين", "أوردرات", "أوردر"), parcels = n => plural(n, "شحنة واحدة", "شحنتين", "شحنات", "شحنة");
let T = null, LF = "look", LP = 0, LL = null;

/* ================= the part in «الشحن» ================= */
SB.shipTab = body => {
  // «هتوصّل الأوردرات إزاي؟» first: delivering yourself, and the companies when the store has them
  const top = D.createElement("div"); top.className = "sm-how"; body.insertBefore(top, body.firstChild);
  if (!car()) { top.innerHTML = SB.selfCard ? how("") : ""; return; }
  const e = D.createElement("div"); e.className = "sm-ship"; body.appendChild(e); top.innerHTML = how('<div class="sm-load" role="status"><i></i> ثانية واحدة...</div>');
  (async () => { try { T = await get("ship"); if (!e.isConnected) return; LF = T.look ? "look" : "open"; LP = 0; draw(); await list(); } catch (x) { if (e.isConnected) e.innerHTML = `<section class="card"><p class="sm-hint">${esc(x.message)}</p></section>`; } })();
};
const how = cards => `<section class="card"><h3>هتوصّل الأوردرات إزاي؟</h3><p class="sub">اختار اللي يناسبك، وينفع أكتر من طريقة: كل أوردر تبعته بالطريقة اللي تحبها.</p><div class="sh-g">${SB.selfCard ? SB.selfCard() : ""}${cards}</div></section>`;
const box = () => D.querySelector(".sm-ship");
function draw() {
  const e = box(), t = D.querySelector(".sm-how"); if (!e) return; if (t) t.innerHTML = how(T.list.map(card).join(""));
  e.innerHTML = `<section class="card"><div class="row"><h3>الشحنات</h3><span class="space"></span><div class="sm-chips" style="margin:0">${[["look", "محتاجة تبص عليها", T.look], ["open", "في الطريق", 0], ["", "الكل", 0]].map(x => `<button type="button" class="${LF === x[0] ? "on" : ""}" data-do="shF:${x[0]}">${x[1]}${x[2] ? ` <b>${fmt(x[2])}</b>` : ""}</button>`).join("")}</div></div><div class="sh-list"></div></section>`;
}
/* what linking does, what it needs and where it's found (only what the companies' own pages say; prices and coverage are the merchant's deal) */
const ABOUT = {
  bosta: ["بتبعت الأوردر من هنا، وتطبع بوليصة بوسطة، وتطلب مندوب ييجي ياخد الشحنات، والحالة بتتحدّث لوحدها.", "<b>محتاج:</b> مفتاح API من حسابك على بوسطة (Settings ثم API Integration)."],
  shipblu: ["بتبعت الأوردر من هنا، وتطبع البوليصة، وتطلب مندوب الاستلام، والحالة بتتحدّث لوحدها كل ساعة.", "<b>محتاج:</b> مفتاح من حسابك على ShipBlu (Integrations)."],
  jt: ["بتبعت الأوردر من هنا وتطبع البوليصة، ومندوب J&T بييجي في الميعاد اللي تحدده، والحالة بتتحدّث لوحدها.", "<b>محتاج:</b> apiAccount و privateKey من J&T Open Platform بعد اعتمادهم بسجلك التجاري، وكود العميل والباسورد من فرع J&T."],
  aramex: ["بتبعت الأوردر من هنا، وتطبع البوليصة، وتطلب مندوب الاستلام، والحالة بتتحدّث لوحدها كل ساعة.", "<b>محتاج:</b> يوزر API من مندوب أرامكس: اسم المستخدم والباسورد ورقم الحساب والـ PIN وكود الفرع وكود الدولة."]
};
const WHY = "🔒 بنطلبها عشان نبعت الأوردرات لحسابك إنت. الشركة بتحصّل من الزبون وتحوّلك فلوسك على طول، ومش بتعدّي على Sellybot، والأسعار والتحصيل حسب اتفاقك معاهم. بتتحفظ متشفّرة ومابتظهرش تاني لحد ولا لينا، ولما تفصل الشركة بتتمسح.";
const keyName = k => k.id === "jt" ? "الباسورد" : k.id === "aramex" ? "الحساب" : "المفتاح";
const JTSTEPS = "<ol><li>افتح J&T Open Platform بحسابك، وادخل على إعدادات الربط (API).</li><li>في مكان عنوان إشعارات التتبع (Logistics track return)، الصق العنوان ده واحفظ.</li><li>أول ما J&T تبعت أول تحديث، هتلاقي هنا «التحديثات بتوصل لحظتها». لحد ساعتها بنسألهم إحنا كل 4 ساعات.</li></ol><p class=\"sm-hint\" style=\"margin:6px 0 0\">لو مش لاقي المكان، ابعت العنوان لمسؤول الربط في J&T.</p>";
const jtPush = k => { const x = k.x || {}; if (!k.linked || !x.own || !x.addr) return ""; const ad = `<div class="sh-ad"><input type="text" readonly value="${esc(x.addr)}" aria-label="عنوان الإشعارات" onclick="this.select()"><button type="button" class="sm-ic sm" data-do="copy:${encodeURIComponent(x.addr)}" aria-label="انسخ العنوان">${ico("copy")}</button></div>`;
  return x.heard ? `<p class="sm-hint" style="color:var(--ok)">✅ التحديثات بتوصل لحظتها</p><details><summary>عنوان الإشعارات</summary>${ad}</details>` : `<p class="sh-w">⏳ لسه بنسأل كل 4 ساعات. سجّل عنوان الإشعارات ده في حسابك على J&T عشان التحديثات توصل لحظتها:</p>${ad}<details><summary>إزاي أسجّله؟</summary>${JTSTEPS}</details>`; };
const card = k => `<div class="sh-c" data-c="${k.id}"><div class="row"><b>${EM[k.id] || "🚚"} ${esc(k.name)}</b><span class="space"></span>${k.linked ? `<span class="sm-pill ${k.err ? "bad" : "ok"}">${k.err ? "الربط وقف" : "مربوط"}</span>` : '<span class="sm-pill g">مش مربوط</span>'}</div>
  ${k.linked ? `<p class="sm-hint">${k.acc ? esc(k.acc) + " · " : ""}${k.last4 ? `${keyName(k)} <bdi dir="ltr">••••${esc(k.last4)}</bdi>` : ""}${k.x && k.x.keys ? ` · مفاتيحك <bdi dir="ltr">••••${esc(k.x.keys)}</bdi>` : ""}${k.open ? " · " + parcels(k.open) + " في الطريق" : ""}</p>${k.err ? `<p class="sm-risk" style="margin:0">${esc(k.err)}</p>` : ""}${k.need ? `<p class="sh-w">${esc(k.need)}</p>` : ""}${!k.areas ? `<p class="sh-w">بنجيب ${k.id === "aramex" ? "مدن" : "مناطق"} ${esc(k.name)}، بياخد دقيقة أو اتنين.</p>` : ""}${k.id === "jt" ? jtPush(k) : ""}${k.set.pickup ? `<p class="sm-hint">آخر مندوب اتطلب: ${SB.day(k.set.pickup.date)} (${parcels(k.set.pickup.n)})${k.set.pickup.no ? " · رقم <bdi>" + esc(k.set.pickup.no) + "</bdi>" : ""}</p>` : ""}<div class="row">${k.pickup ? `<button type="button" class="btn sm" data-do="shPick:${k.id}">اطلب مندوب${k.wait ? " (" + fmt(k.wait) + ")" : ""}</button>` : ""}<button type="button" class="btn ghost sm" data-do="shSet:${k.id}">الإعدادات</button><button type="button" class="btn ghost sm" data-do="shLink:${k.id}">${k.id === "jt" || k.id === "aramex" ? "غيّر البيانات" : "غيّر المفتاح"}</button><button type="button" class="btn ghost sm" data-do="shOff:${k.id}">افصله</button></div>`
    : `${(ABOUT[k.id] || []).map(t => `<p class="sh-k">${t}</p>`).join("")}<p class="sh-k">${WHY}</p><div class="row"><button type="button" class="btn sm" data-do="shLink:${k.id}">اربطها</button></div>`}</div>`;
async function list() { const e = box(), n = LF; LL = await get("ship-list", { f: n, page: LP }); const b = e && e.querySelector(".sh-list"); if (!b || n !== LF) return; const pages = Math.ceil(LL.total / LL.size); b.innerHTML = LL.list.length ? `<div class="sm-list">${LL.list.map(x => `<div class="sm-row"><button type="button" class="grow" data-do="shOpen:${esc(x.order)}"><b>أوردر <bdi dir="ltr">${esc(x.name)}</bdi> <i>${esc(x.cName)}${x.tracking ? " · " : ""}<bdi dir="ltr">${esc(x.tracking)}</bdi></i></b><small>${[x.why ? esc(x.why) : "", x.n ? plural(x.n, "محاولة واحدة", "محاولتين", "محاولات", "محاولة") : "", x.back && !x.got ? "مستنية «وصلتني»" : "", x.answered ? "الزبون ردّ" : x.rescued ? "اتبعتله رسالة" : "", x.state === "unsure" ? esc(x.err) : "", SB.ago(x.last || x.at)].filter(Boolean).join(" · ")}</small></button><span class="sm-pr"><b>${mny(x.cod)}</b></span>${pillOf(x)}</div>`).join("")}</div>${pages > 1 ? `<div class="sm-pg"><button type="button" class="btn ghost sm" data-do="shP:-1" ${LP ? "" : "disabled"}>السابق</button><span>${fmt(LP + 1)} من ${fmt(pages)}</span><button type="button" class="btn ghost sm" data-do="shP:1" ${LP < pages - 1 ? "" : "disabled"}>التالي</button></div>` : ""}` : `<div class="sm-empty"><span aria-hidden="true">📦</span><p>${LF === "look" ? "مفيش شحنة محتاجاك. كله ماشي." : "مفيش شحنات هنا."}</p></div>`; }
A.shF = f => { LF = typeof f === "string" ? f : ""; LP = 0; draw(); run(null, list); }; A.shP = d => { LP = Math.max(0, LP + +d); run(null, list); }; A.shOpen = id => SB.openOrder(+id);
const fresh = async t => { T = t; draw(); await SB.reload().catch(() => { }); await list().catch(() => { }); };

/* ---------- linking ---------- */
const STEPS = {
  bosta: ["افتح حسابك على بوسطة: Settings ثم API Integration، ودوس Request OTP واكتب الكود اللي جالك على موبايلك.", "دوس Create API key. اختار <b dir=\"ltr\">Full access</b> لو عايز تلغي الشحنات من هنا، أو <b dir=\"ltr\">Read/Write</b> وتلغيها من حسابك.", "المفتاح بيظهر مرة واحدة بس. انسخه والصقه هنا."],
  shipblu: ["افتح حسابك على ShipBlu، وادخل Integrations.", "دوس علامة + فوق على اليمين، هيطلع المفتاح.", "انسخه والصقه هنا."],
  jt: ["سجّل على J&T Open Platform (<bdi dir=\"ltr\">open.jtjms-eg.com</bdi>) واختار English من فوق.", "قدّم الاعتماد (Certification) بسجلك التجاري إنت. <b>من غير سجل تجاري J&T مابتدّيش المفاتيح</b>: اربط شركة تانية، أو وصّل بنفسك.", "بعد الاعتماد افتح Personal center وانسخ <b dir=\"ltr\">apiAccount</b> و<b dir=\"ltr\">privateKey</b>.", "من فرع J&T بتاعك خد <b>كود العميل</b> و<b>الباسورد</b>، واتأكد إن التحصيل عند الاستلام متفعّل على حسابك.", "اكتبهم هنا. بعد الربط هيظهرلك عنوان إشعارات تسجّله في حسابك عندهم."],
  aramex: ["كلّم مندوب أرامكس واطلب <b>يوزر API</b> للربط، منفصل عن يوزر دخولك العادي.", "هيدّيك 6 حاجات: اسم المستخدم، الباسورد، رقم الحساب، الـ PIN، كود الفرع (زي <bdi dir=\"ltr\">CAI</bdi>)، كود الدولة (<bdi dir=\"ltr\">EG</bdi>).", "اتأكد منه إن التحصيل عند الاستلام متفعّل على حسابك.", "اكتبهم هنا."]
};
const of = id => T.list.find(x => x.id === id), tab = async () => { if (!T) T = await get("ship"); };
const SECRET = { type: "password", x: 'autocomplete="new-password"' }, LTR = { dir: "ltr", x: 'autocomplete="off"' };
const linkForm = k => {
  if (k.id === "jt") { const x = k.x || {}, keep = k.linked && x.own, opt = !keep && x.opt; return `${fld("acc", "apiAccount" + (opt ? " (اختياري)" : ""), "", { ...LTR, max: 40, mode: "numeric", ph: keep ? "محفوظ ••••" + x.keys : "", hint: keep ? "سيبه فاضي لو مش هتغيّره." : "" })}${fld("pk", "privateKey" + (opt ? " (اختياري)" : ""), "", { ...SECRET, dir: "ltr", max: 200, ph: keep ? "محفوظ" : "" })}${fld("code", "كود العميل", k.code || "", { ...LTR, max: 30 })}${fld("key", "الباسورد", "", { ...SECRET, dir: "ltr", max: 100 })}`; }
  if (k.id === "aramex") return `${fld("u", "اسم المستخدم (UserName)", "", { ...LTR, max: 80 })}${fld("key", "الباسورد (Password)", "", { ...SECRET, dir: "ltr", max: 60 })}<div class="sm-2">${fld("acc", "رقم الحساب (AccountNumber)", "", { ...LTR, max: 20, mode: "numeric" })}${fld("pin", "الـ PIN (AccountPin)", "", { ...SECRET, dir: "ltr", max: 12 })}</div><div class="sm-2">${fld("ent", "كود الفرع (AccountEntity)", "", { ...LTR, max: 3, ph: "CAI" })}${fld("cc", "كود الدولة (AccountCountryCode)", "EG", { ...LTR, max: 2 })}</div>`;
  return fld("key", "المفتاح", "", { ...SECRET, dir: "ltr", max: 600 });
};
A.shLink = async id => {
  await tab(); const k = of(id), p = pane({ title: "ربط " + k.name, cls: "small", html: () => `<ol class="sh-ol">${STEPS[id].map(x => `<li>${x}</li>`).join("")}</ol><div class="sm-form">${linkForm(k)}<p class="sm-hint">🔒 بيتحفظوا متشفّرين، ومابيظهروش تاني لأي حد ولا لينا، ولما تفصل الشركة بيتمسحوا.</p></div>`, foot: () => '<button type="button" class="btn" data-do="go">اربط</button>',
    acts: { go: b => run(b, async () => { const d = SB.read(p.el), t = await post("ship-link", { c: id, ...d }); p.close(true); say("اتربطت ✅"); await fresh(t); const n = of(id); if (n.need) A.shSet(id); }) } });
};
A.shOff = async (id, b) => { const k = of(id); if (await sure(`تفصل ${esc(k.name)}؟${k.open ? `<br>عندك ${parcels(k.open)} في الطريق، ومش هنعرف حالتهم بعد كده.` : ""}`, "افصلها")) run(b, async () => { const t = await post("ship-unlink", { c: id }); say("اتفصلت."); await fresh(t); }); };

/* ---------- settings ---------- */
const SIZE = { bosta: [["SMALL", "صغير"], ["MEDIUM", "وسط"], ["LARGE", "كبير"], ["Light Bulky", "Light Bulky (حجم كبير خفيف)"], ["Heavy Bulky", "Heavy Bulky (حجم كبير تقيل)"]], shipblu: [["1", "صغير"], ["2", "وسط"], ["3", "كبير"], ["4", "كبير جدًا"]] };
const GOODS = [["ITN16", "حاجات تانية"], ["ITN1", "هدوم"], ["ITN6", "استخدام يومي"], ["ITN5", "أجهزة وإلكترونيات"], ["ITN3", "أكل"], ["ITN7", "قابل للكسر"], ["ITN8", "أدوات وعدد"], ["ITN9", "أدوات مكتبية"], ["ITN10", "أثاث"], ["ITN13", "هاند ميد"], ["ITN15", "كتب"], ["ITN2", "أوراق"]];
const PTYPE = [["ONP", "طرود، اليوم اللي بعده (ONP)"], ["CDS", "تجارة إلكترونية (CDS)"], ["SMP", "طرود في نفس اليوم (SMP)"]];   // Aramex's own plugin's names for them
const hours = (k, v) =>SB.sel(k, Array.from({ length: 24 }, (_, i) => [i, (i % 12 || 12) + (i < 12 ? " ص" : " م")]), v);
A.shSet = async id => {
  await tab(); const k = of(id), s = { ...k.set }; let sa = s.sa || "", sl = k.sender || "", dirty = false;
  const keep = () => Object.assign(s, SB.read(p.el));
  const p = pane({
    title: "إعدادات " + k.name, cls: "mid", dirty: () => dirty, after: e => e.querySelector(".sm-p-b").addEventListener("input", () => { dirty = true; }),
    html: () => `<div class="sm-form">${id === "jt" || id === "aramex" ? `<h4>عنوان الاستلام منك</h4><div class="sm-2">${fld("sn", "الاسم", s.sn, { max: 50, must: true })}${fld("sp", "الموبايل", (s.sp || "").replace(/^20/, "0"), { mode: "tel", dir: "ltr", must: true })}</div><div class="sm-f"><label>${id === "aramex" ? "المدينة" : "المنطقة"} <em>مطلوب</em></label><div class="sh-a"><b>${esc(sl || "لسه ماخترتش")}</b><button type="button" data-do="area">${sl ? "غيّرها" : "اختارها"}</button></div></div>${fld("ss", "الشارع والعمارة", s.ss, { max: 150, must: true })}<div class="sm-2"><div class="sm-f"><label>المندوب ييجي من</label>${hours("from", s.from == null ? 10 : s.from)}</div><div class="sm-f"><label>لحد</label>${hours("to", s.to == null ? 18 : s.to)}</div></div><div class="sm-2">${id === "jt" ? `<div class="sm-f"><label>نوع المنتجات</label>${SB.sel("goods", GOODS, s.goods || "ITN16")}</div>` : `<div class="sm-f"><label>الخدمة على حسابك</label>${SB.sel("pt", PTYPE, s.pt || "ONP")}<small class="sm-hint">اسأل مندوب أرامكس لو مش متأكد.</small></div>`}${fld("weight", "وزن الشحنة", s.weight || 1, { mode: "decimal", suf: "كجم", x: "data-n", hint: "لو المنتج مالوش وزن في الكتالوج" })}</div>` : ""}
      ${id === "bosta" && k.places.length ? `<div class="sm-f"><label>عنوان الاستلام منك</label>${SB.sel("place", k.places.map(x => [x.id, (x.n || "") + (x.ad ? " · " + x.ad : "")]), s.place || (k.places.find(x => x.def) || k.places[0]).id)}</div>` : id === "bosta" ? '<p class="sh-w">مالقيناش عنوان استلام في حسابك على بوسطة. ضيفه عندهم، وبعدين «غيّر المفتاح» عشان نقراه.</p>' : ""}
      ${SIZE[id] ? `<div class="sm-f"><label>مقاس الشحنة</label>${SB.sel("size", SIZE[id], s.size || SIZE[id][0][0])}</div>` : ""}
      ${id === "shipblu" ? fld("email", "إيميل يتكتب على الشحنات", s.email, { dir: "ltr", max: 120, hint: "اكتبه بس لو ShipBlu طلبته." }) : id === "aramex" ? fld("email", "إيميل يتكتب على الشحنات", s.email, { dir: "ltr", max: 120, must: true, hint: "أرامكس بتطلب إيميل للراسل والمستلم في كل شحنة، فبنكتب إيميلك." }) : ""}
      ${fld("note", "ملاحظة للمندوب في كل شحنة", s.note, { max: 150, ph: "مثلًا: اتصل قبلها بنص ساعة" })}
      ${id === "jt" || id === "aramex" ? "" : SB.tog("open", s.open, "مسموح للزبون يفتح الشحنة قبل ما يستلم", id === "bosta" ? "بوسطة بتطلب صور المنتج، وبنبعتلها صور متجرك" : "")}
      ${id === "aramex" ? "" : SB.tog("value", s.value !== false, id === "jt" ? "ابعت قيمة المنتجات (تأمين)" : "ابعت قيمة المنتجات للشركة", id === "bosta" ? "من غيرها بوسطة مابتحسبش التأمين" : "")}
      ${SB.tog("rescue", s.rescue !== false, "إنقاذ الأوردر", "لو محاولة فشلت، الزبون يوصله رسالة واحدة على واتساب المتجر تناسب السبب، ومش بالليل")}
      ${id === "aramex" ? '<p class="sm-hint">البوليصة A4 زي ما أرامكس بتطلعها.</p>' : `<div class="sm-f"><label>البوليصة</label>${SB.seg("lab", [["A4", id === "shipblu" ? "A4 (اتنين في الصفحة)" : "A4"], ["A6", "A6 (طابعة بوليصات)"]], s.label || "A4")}</div>`}</div>`,
    foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
    acts: {
      lab: v => { keep(); s.label = v; dirty = true; p.draw(); }, area: async () => { keep(); const x = await pickArea(id, 0, "منطقة الاستلام منك"); if (x) { sa = x.k; sl = x.t; dirty = true; } p.draw(); },
      go: b => run(b, async () => { keep(); const t = await post("ship-set", { c: id, set: { ...s, sa, from: +s.from, to: +s.to } }); dirty = false; p.close(true); say("اتحفظ ✅"); await fresh(t); })
    }
  });
};
/* one of the company's areas, by typing */
function pickArea(c, gov, title, first) {
  return new Promise(ok => {
    let l = first || [], q = "", t = 0, n = 0, sent = false, g = gov;
    const rows = () => l.length ? l.map((x, i) => `<button type="button" class="sm-row" data-do="pk:${i}" ${x.off ? "disabled" : ""}><span class="grow"><b>${esc(x.t)}</b>${x.off ? "<small>مابيوصّلوش هنا</small>" : ""}</span></button>`).join("") : '<p class="sm-hint">مالقيناش. جرّب كلمة تانية.</p>';
    const load = async () => { const k = ++n; try { const r = await get("ship-areas", { c, gov: g, q }); if (k !== n || !p.el.isConnected) return; l = r.list; p.el.querySelector(".sh-l").innerHTML = rows(); } catch (e) { say(e.message, true); } };
    const p = pane({ title: title || "اختار المنطقة", cls: "small", html: () => `<div class="sm-q" style="margin-bottom:10px">${ico("search")}<input type="search" placeholder="المدينة أو الحي" data-in="q" autofocus aria-label="ابحث عن المنطقة"></div>${gov ? `<label class="toggle"><input type="checkbox" data-ch="all"><span class="tg" aria-hidden="true"></span><span>دوّر في كل المحافظات</span></label>` : ""}<div class="sm-list sh-l">${rows()}</div>`, closed: () => { if (!sent) ok(null); },
      acts: { pk: i => { sent = true; const x = l[+i]; p.close(true); ok(x); } }, inp: { q: v => { q = v.trim(); clearTimeout(t); t = setTimeout(load, 220); } }, ch: { all: on => { g = on ? 0 : gov; load(); } } });
    if (!first || !first.length) load();
  });
}

/* ---------- the courier ---------- */
const iso = d => { const z = new Date(d.getTime() - d.getTimezoneOffset() * 6e4); return z.toISOString().slice(0, 10); };
A.shPick = async id => {
  await tab(); const k = of(id), d = new Date(); if (d.getHours() >= 13) d.setDate(d.getDate() + 1); if (d.getDay() === 5) d.setDate(d.getDate() + 1);
  const p = pane({ title: "اطلب مندوب " + k.name, cls: "small", html: () => `<div class="sm-form">${id === "bosta" && k.places.length > 1 ? `<div class="sm-f"><label>ييجي فين</label>${SB.sel("place", k.places.map(x => [x.id, (x.n || "") + (x.ad ? " · " + x.ad : "")]), k.set.place || (k.places.find(x => x.def) || k.places[0]).id)}</div>` : ""}<div class="sm-2">${id === "shipblu" ? "" : fld("date", "اليوم", iso(d), { type: "date" })}${id === "shipblu" ? "" : fld("n", "عدد الشحنات", k.wait || 1, { mode: "numeric", x: "data-n" })}</div>${fld("note", "ملاحظة", "", { max: 200 })}<p class="sm-hint">${id === "shipblu" ? `هيستلم ${parcels(k.wait)} مستنية عندك.` : id === "aramex" ? "من النهارده لحد 7 أيام، ومفيش يوم الجمعة. المندوب ييجي في المواعيد اللي في إعدادات أرامكس." : "مفيش يوم الجمعة، ومرة واحدة في اليوم لكل عنوان."}</p></div>`, foot: () => '<button type="button" class="btn" data-do="go">اطلبه</button>',
    acts: { go: b => run(b, async () => { const v = SB.read(p.el), r = await post("ship-pickup", { c: id, n: k.wait || 1, ...v, date: v.date || iso(d) }); p.close(true); say(r.warn ? r.warn : "المندوب اتطلب ✅" + (r.no ? " رقم " + r.no : "")); T = await get("ship"); draw(); list().catch(() => { }); }) } });
};

/* ---------- labels: the company's own, in one file ---------- */
async function labels(ids, btn) {
  const w = open("", "_blank"); if (w) w.document.write('<p dir="rtl" style="font-family:Tahoma;padding:30px">بنجيب البوليصات من شركة الشحن...</p>');
  const got = await run(btn, async () => {
    let r; try { r = await fetch(API, { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Content-Type": "application/json" }, body: JSON.stringify({ a: "ship-labels", ids }) }); } catch (e) { throw new Error("النت فصل. جرّب تاني."); }
    if (!r.ok) { let d = null; try { d = await r.json(); } catch (e) { } throw new Error(d && d.message || "حصلت مشكلة، جرّب تاني."); }
    const type = r.headers.get("Content-Type") || "", note = r.headers.get("X-Ship-Note"), url = URL.createObjectURL(await r.blob()); if (note) say(decodeURIComponent(note), true);
    if (w && /pdf/.test(type)) w.location = url; else { if (w) w.close(); const a = D.createElement("a"); a.href = url; a.download = /pdf/.test(type) ? "labels.pdf" : "labels.zip"; a.click(); } setTimeout(() => URL.revokeObjectURL(url), 12e4); return true;
  });
  if (!got && w) w.close();
}

/* ================= sending ================= */
let picked = [];
SB.shipBar = ids => { picked = ids; return linked().length ? '<button type="button" class="btn sm" data-do="shMany">ابعت للشحن</button><button type="button" class="btn ghost sm" data-do="shLabels">بوليصات الشركة</button>' : ""; };
A.shLabels = b => labels(picked, b); A.shMany = () => send(picked, () => SB.show("orders"));
function send(ids, after) {
  const ks = linked(); if (!ks.length) return; let c = ks[0].id, L = null, busy = false, done = 0, sent = []; try { const x = localStorage.getItem("sm-ship"); if (ks.some(k => k.id === x)) c = x; } catch (e) { }
  const name = () => ks.find(k => k.id === c).name, ready = () => L ? L.list.filter(o => !o.err && !o.tracking && o.key) : [], wants = () => L ? L.list.filter(o => !o.err && !o.tracking && !o.key) : [];
  const row = o => `<div class="sh-r ${o.tracking ? "ok" : o.err || o.fail ? "bad" : o.key ? "" : "need"}"><div class="row"><b>#${o.no} ${esc(o.name)}</b><span class="space"></span><b>${o.cod > 0 ? mny(o.cod) : "مدفوع"}</b></div><p>${esc(o.place)} · ${esc(o.addr)}</p>${o.tracking ? `<p style="color:var(--ok)">✅ ${o.had ? "كان متبعت" : "اتبعت"} · <bdi dir="ltr">${esc(o.tracking)}</bdi></p>` : o.err ? `<p class="bad">${esc(o.err)}</p>` : `${o.fail ? `<p class="bad">${esc(o.fail)}</p>` : ""}${o.warn.map(x => `<span class="sh-w">⚠️ ${esc(x)}</span>`).join("")}<div class="sh-a">${o.key ? `<span class="sm-pill b">${esc(o.area)}</span><button type="button" data-do="ar:${o.id}">غيّرها</button>` : `<span class="sh-w">اختار منطقته عند ${esc(name())}:</span>${o.more.slice(0, 5).map((x, i) => `<button type="button" data-do="mo:${o.id}:${i}">${esc(x.t.split(" › ").slice(-2).join(" › "))}</button>`).join("")}<button type="button" data-do="ar:${o.id}">دوّر</button>`}</div>`}</div>`;
  const load = async () => { L = null; p.draw(); try { L = await post("ship-check", { c, ids }); } catch (e) { say(e.message, true); p.close(true); return; } p.draw(); if (L.empty) setTimeout(() => { if (p.el.isConnected && L && L.empty) load(); }, 6000); };
  const p = pane({
    title: "ابعت للشحن", cls: "mid", dirty: () => busy, closed: () => { if (sent.length && after) after(); },
    html: () => `${ks.length > 1 && !sent.length ? SB.seg("co", ks.map(k => [k.id, esc(k.name)]), c) : ""}${!L ? '<div class="sm-load" role="status"><i></i> ثانية واحدة...</div>' : `${L.need ? `<p class="sm-risk">${esc(L.need)}</p>` : ""}${L.empty ? `<p class="sh-w">لسه بنجيب مناطق ${esc(name())}. استنى شوية وهتظهر لوحدها.</p>` : ""}${L.list.map(row).join("")}`}`,
    foot: () => !L ? "" : sent.length && !ready().length ? `<button type="button" class="btn" data-do="lb">اطبع ${fmt(sent.length)} ${sent.length > 2 && sent.length < 11 ? "بوليصات" : "بوليصة"}</button><button type="button" class="btn ghost" data-do="paneClose">تمام</button>` : `<button type="button" class="btn" data-do="go" ${ready().length && !L.need && !L.empty ? "" : "disabled"}>ابعت ${ready().length ? orders(ready().length) : ""} لـ ${esc(name())}</button>${wants().length ? `<span class="sm-hint">${fmt(wants().length)} محتاج منطقة</span>` : ""}${done ? `<span class="sm-hint">${fmt(done)} خلصوا</span>` : ""}`,
    acts: {
      co: v => { if (busy) return; c = v; try { localStorage.setItem("sm-ship", c); } catch (e) { } load(); },
      mo: (id, i) => { const o = L.list.find(x => x.id === +id), m = o.more[+i]; set(o, m); }, ar: async id => { const o = L.list.find(x => x.id === +id), m = await pickArea(c, o.gov, "منطقة أوردر #" + o.no, o.more); if (m) set(o, m); },
      lb: b => labels(sent, b),
      go: async b => {
        if (busy) return; busy = true; b.disabled = true; b.classList.add("sm-busy"); const l = ready(); done = 0;
        try { for (let i = 0; i < l.length; i += 3) { const part = l.slice(i, i + 3), r = await post("ship-send", { c, items: part.map(o => ({ id: o.id, area: o.key })) }); r.list.forEach(x => { const o = L.list.find(y => y.id === x.id); if (x.ok) { o.tracking = x.tracking; o.had = x.had; o.fail = ""; sent.push(o.id); } else { o.fail = x.err; if (x.code === "area") { o.key = ""; } } done++; }); p.draw(); if (r.list.some(x => x.code === "net" || x.code === "badkey")) break; } }
        catch (e) { say(e.message, true); } finally { busy = false; p.draw(); SB.reload().catch(() => { }); }
        if (sent.length && L.list.every(o => o.tracking || o.err)) say(`${orders(sent.length)} اتبعت لـ ${name()} ✅`);
      }
    }
  });
  /* the same city in the other rows takes the same area, until the owner says otherwise */
  const set = (o, m) => { o.key = m.k; o.area = m.t; o.fail = ""; L.list.forEach(x => { if (x !== o && !x.key && !x.err && x.place === o.place) { x.key = m.k; x.area = m.t; } }); p.draw(); };
  load();
}

/* ================= in an order ================= */
let cur = null;
SB.shipCard = (o, again) => {
  const q = o.parcel; if (!q) return ""; cur = { o, again }; const x = q.ship, live = x && (x.state === "live" || x.state === "done"), no = !x || x.state === "off" || x.state === "failed";
  const manual = `<details ${!q.links.length ? "open" : ""}><summary>اكتب شركة ورقم بإيدك</summary><div class="sm-form">${fld("carrier", "شركة الشحن", o.carrier, { max: 30, ph: "بوسطة، J&T..." })}${fld("tracking", "رقم البوليصة", o.tracking, { max: 60, dir: "ltr" })}<button type="button" class="btn ghost sm" data-do="ship">احفظ</button></div></details>`;
  return `<section class="card sh-o"><div class="row"><h3>الشحن</h3><span class="space"></span>${x && x.state !== "failed" ? pillOf(x) : ""}</div>${live || (x && (x.state === "unsure" || x.state === "making")) ? `<p class="sm-hint">${esc(x.cName)}${x.area ? " · " + esc(x.area) : ""}</p>${x.tracking ? `<p class="sh-tr"><bdi dir="ltr">${esc(x.tracking)}</bdi><button type="button" class="sm-ic sm" data-do="copy:${encodeURIComponent(x.tracking)}" aria-label="انسخ رقم التتبع">${ico("copy")}</button></p>` : ""}<p class="sm-hint">${x.cod > 0 ? "بتحصّل " + mny(x.cod) : "من غير تحصيل"}${x.n ? " · " + plural(x.n, "محاولة واحدة", "محاولتين", "محاولات", "محاولة") : ""}${x.why ? " · " + esc(x.why) : ""}</p>${q.diff != null ? `<p class="sm-risk">المبلغ اتغيّر بعد ما بعتّه: عند ${esc(x.cName)} ${mny(x.cod)} والأوردر بقى ${mny(q.diff)}. ${x.canCancel ? "الغي الشحنة وابعتها تاني، أو عدّلها من حسابك عندهم." : "عدّله من حسابك عندهم."}</p>` : ""}${x.answered ? '<p class="sh-w">💬 الزبون ردّ على رسالة الإنقاذ، شوف ردّه تحت.</p>' : x.rescued ? `<p class="sm-hint">اتبعتله رسالة إنقاذ ${SB.ago(x.rescued)}.</p>` : x.due ? `<p class="sm-hint">رسالة الإنقاذ هتتبعت ${SB.when(x.due)}.</p>` : ""}${o.status === "cancelled" && x.state === "live" ? `<p class="sm-risk">الأوردر ملغي والشحنة لسه عند ${esc(x.cName)}.</p>` : ""}${x.state === "unsure" ? `<p class="sm-risk">${esc(x.err)}</p>` : ""}${x.state === "unsure" && x.c === "aramex" ? `<div class="sh-fx"><input type="text" inputmode="numeric" dir="ltr" maxlength="20" placeholder="رقم الشحنة عند أرامكس" aria-label="رقم الشحنة عند أرامكس" data-fx><button type="button" class="btn sm" data-do="shFix">لقيتها</button></div><p class="sm-hint">دوّر في حسابك على أرامكس بالمرجع <bdi dir="ltr">${esc(x.ref)}</bdi></p>` : ""}
      <div class="sm-nx">${x.back && !x.got ? '<button type="button" class="btn sm" data-do="shGot">وصلتني</button>' : ""}${x.tracking ? '<button type="button" class="btn ghost sm" data-do="shLabel">البوليصة</button>' : ""}${x.state === "live" || x.state === "unsure" ? `<button type="button" class="btn ghost sm" data-do="shAsk">${x.state === "unsure" ? "اسأل الشركة" : "حدّث الحالة"}</button>` : ""}${x.state === "unsure" ? '<button type="button" class="btn ghost sm" data-do="shDrop">اتأكدت إنها مش عندهم</button>' : ""}${x.state === "unsure" && x.c === "shipblu" ? '<p class="sm-hint" style="flex-basis:100%">ShipBlu مابتقولش لوحدها إن الشحنة اتسجّلت. افتح حسابك عليها ودوّر على رقم الأوردر: لو لقيتها دوس «اسأل الشركة»، ولو مش موجودة دوس «اتأكدت إنها مش عندهم» وابعتها تاني.</p>' : ""}${x.canCancel ? '<button type="button" class="btn danger sm" data-do="shCancel">الغي الشحنة</button>' : ""}</div>
      ${x.events.length ? `<details ${x.n || x.answered ? "open" : ""}><summary>حركة الشحنة</summary><ol class="sm-log">${x.events.map(e => `<li><b>${esc(e.t)}</b><small>${SB.when(e.at)}</small></li>`).join("")}</ol></details>` : ""}` : ""}
    ${no ? `${x && x.state === "failed" ? `<p class="sm-risk">${esc(x.err)}</p>` : x && x.state === "off" ? `<p class="sm-hint">شحنة ${esc(x.cName)} اتلغت.</p>` : ""}${q.links.length ? (q.can ? (["confirmed", "new", "noreply"].includes(o.status) ? `<p class="sm-hint">${esc(q.can)}</p>` : "") : `<button type="button" class="btn" data-do="shSend">ابعت لـ ${q.links.length === 1 ? esc(q.links[0].name) : "شركة الشحن"}</button>`) : ""}${manual}` : ""}</section>`;
};
const redo = async () => { const c = cur; await c.again(); SB.reload().catch(() => { }); };
A.shSend = () => send([cur.o.id], redo); A.shLabel = b => labels([cur.o.id], b);
A.shFix = b => run(b, async () => { const v = (D.querySelector(".sh-o [data-fx]") || {}).value || ""; await post("ship-fix", { id: cur.o.parcel.ship.id, tracking: v.trim() }); await redo(); say("اتسجّلت برقمها ✅"); });
A.shAsk = b => run(b, async () => { await post("ship-ask", { id: cur.o.parcel.ship.id }); await redo(); say("اتحدّثت ✅"); });
A.shCancel = async b => { if (await sure("تلغي الشحنة عند " + esc(cur.o.parcel.ship.cName) + "؟ الأوردر هيفضل متأكد وتقدر تبعته تاني.", "الغيها")) run(b, async () => { await post("ship-cancel", { id: cur.o.parcel.ship.id }); await redo(); say("اتلغت."); }); };
A.shDrop = async b => { if (await sure("متأكد إن الشحنة مش موجودة في حسابك عند " + esc(cur.o.parcel.ship.cName) + "؟ لو موجودة هيبقى عندك شحنتين.", "أيوه، مش موجودة")) run(b, async () => { await post("ship-drop", { id: cur.o.parcel.ship.id }); await redo(); }); };
A.shGot = () => { const c = cur, p = pane({ title: "المرتجع وصل", cls: "small", html: () => `<div class="sm-form">${c.o.parcel.stock ? SB.tog("restock", true, "رجّع الكمية للمخزون", "شيلها لو المنتج راجع تالف") : '<p class="sm-hint">الكمية رجعت للمخزون قبل كده.</p>'}</div>`, foot: () => '<button type="button" class="btn" data-do="go">وصلتني</button>', acts: { go: b => run(b, async () => { const d = SB.read(p.el); await post("ship-got", { id: c.o.parcel.ship.id, restock: d.restock !== false }); p.close(true); await redo(); say("اتسجّل ✅"); }) } }); };
})();
