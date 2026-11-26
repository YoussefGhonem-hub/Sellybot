/* «متجري»: «ربط ببرامج تانية» (keys for the merchant's accounting or warehouse program, the addresses that get events, what the programs
   changed) and «القياس من السيرفر» (Meta Conversions API, TikTok Events API), a card inside «المميزات». Server: ShopApi*.cs, ShopAds.cs. */
(() => {
"use strict";
const SB = window.SbShop, V = SB.v, { esc, fmt, ico, fld, run, say, post, get, pane, sure } = SB, D = document, A = SB.acts;
const css = D.createElement("style"); css.textContent = ".ap-k{display:block;direction:ltr;text-align:left;font:600 14px/1.6 ui-monospace,Consolas,monospace;background:var(--soft);border:1px dashed var(--line);border-radius:10px;padding:12px;word-break:break-all;user-select:all;margin:8px 0}.ap-w{color:#a86a00;font-size:13px;margin:6px 0}[data-theme=dark] .ap-w{color:#f0c36a}.ap-r{border:1px solid var(--line);border-radius:12px;padding:12px;margin-bottom:10px;display:grid;gap:6px}.ap-r p{margin:0;font-size:13px;color:var(--muted)}.ap-r.bad{border-color:color-mix(in srgb,var(--err) 45%,var(--line))}.ap-r.off{opacity:.7}.ap-u{direction:ltr;unicode-bidi:isolate;font:600 14px ui-monospace,Consolas,monospace;word-break:break-all}.ap-s{display:flex;flex-wrap:wrap;gap:4px}.ap-s span{font-size:12px;border:1px solid var(--line);border-radius:99px;padding:1px 9px;color:var(--muted)}.ap-e{color:var(--err);font-size:13px;margin:0}.ap-ol{margin:4px 0 8px;padding-inline-start:20px;font-size:13.5px;line-height:1.9}.sm-ads .ap-r .sm-2{display:grid;gap:0 12px}"; D.head.appendChild(css);
const when = t => t ? SB.when(t) : "", ago = t => t ? SB.ago(t) : "";
const host = u => { try { const x = new URL(u); return x.host + (x.pathname.length > 1 ? x.pathname : ""); } catch (e) { return u; } };
const ST = { ok: ["وصل", "ok"], wait: ["هيتبعت تاني", "y"], sending: ["بيتبعت", "b"], failed: ["ماوصلش", "bad"], off: ["اتلغى", "g"] };
const pill = s => { const x = ST[s] || [s, "g"]; return `<span class="sm-pill ${x[1]}">${x[0]}</span>`; };
let T = null;
const tx = (list, k) => (T && T[list] || []).find(x => x.k === k), kind = k => k === "ping" ? "تجربة" : (tx("events", k) || { t: k }).t;

/* ================= «ربط ببرامج تانية» ================= */
V.api = {
  async open(body) { T = await get("api"); if (SB.view() === "api") draw(body); },
  ch: { apiOn: (v, b) => run(null, async () => { if (!v && !await sure("تقفل الربط؟ البرامج المربوطة هتقف ومفيش حاجة هتتبعتلها لحد ما تفتحه تاني.", "اقفله")) { b.checked = true; return; } T = (await post("api-on", { on: v })).tab; draw(SB.body()); say(v ? "الربط اتفتح ✅" : "الربط اتقفل"); }) },
  acts: {
    apiKey: () => newKey(), apiHook: id => hookPane(+id || 0),
    apiStop: id => run(null, async () => { if (!await sure("توقّف المفتاح ده؟ البرنامج اللي بيستخدمه هيقف على طول، ومش هينفع يرجع يشتغل.", "وقّفه")) return; T = (await post("api-key-stop", { id: +id })).tab; draw(SB.body()); say("المفتاح اتوقف"); }),
    apiDel: id => run(null, async () => { T = (await post("api-key-del", { id: +id })).tab; draw(SB.body()); }),
    apiHOn: (id, on) => run(null, async () => { T = (await post("api-hook-on", { id: +id, on: on === "1" })).tab; draw(SB.body()); }),
    apiHDel: id => run(null, async () => { if (!await sure("تمسح العنوان ده؟ اللي مستني يتبعتله هيتلغى.", "امسحه")) return; T = (await post("api-hook-del", { id: +id })).tab; draw(SB.body()); }),
    apiSec: id => run(null, async () => { if (!await sure("تعمل سر جديد؟ القديم هيقف، ولازم المبرمج يحط الجديد في برنامجك.", "اعمل سر جديد", { danger: false })) return; const r = await post("api-hook-secret", { id: +id }); T = r.tab; draw(SB.body()); once("السر الجديد", r.secret, "المبرمج بيستخدمه عشان يتأكد إن الرسالة جاية من متجرك فعلًا."); }),
    apiTry: (id, b) => run(b, async () => { const r = await post("api-hook-try", { id: +id }); T = r.tab; draw(SB.body()); say(r.sent ? "برنامجك ردّ ✅" : "ماوصلش: " + r.why, !r.sent); }),
    apiAgain: (id, b) => run(b, async () => { T = (await post("api-send-again", { id: +id })).tab; draw(SB.body()); say("هيتبعت تاني دلوقتي"); })
  }
};
function draw(body) {
  if (!body || SB.view() !== "api") return; const act = T.keys.filter(k => !k.stopped).length;
  body.innerHTML = `<section class="card"><h3>ربط ببرامج تانية</h3><p class="sub">لو عندك برنامج حسابات أو مخزن، المبرمج بتاعك يربطه بمتجرك: يقرا المنتجات والأوردرات والزباين، ويغيّر الكمية والأسعار وحالة الأوردر بنفسه، ويوصله كل أوردر جديد أول ما يتعمل.</p>
    ${SB.tog("on", T.on, "الربط ببرامج تانية", T.on ? "شغال: البرامج اللي معاها مفتاح تقدر تدخل" : "مقفول: مفيش برنامج يقدر يدخل، ومفيش حاجة بتتبعت", 'data-ch="apiOn"')}
    ${T.on ? `<div class="row" style="margin-top:8px"><a class="btn ghost sm" href="${esc(T.docs)}" target="_blank" rel="noopener">${ico("out")} صفحة الشرح للمبرمج</a><button type="button" class="btn ghost sm" data-do="copy:${encodeURIComponent(T.docs)}">انسخ لينك الشرح</button></div><p class="sm-hint">ابعت للمبرمج لينك الشرح والمفتاح، وهو يكمّل.</p>` : ""}</section>
  ${T.on ? `<section class="card"><div class="row"><h3>المفاتيح</h3><span class="space"></span><button type="button" class="btn sm" data-do="apiKey" ${act >= T.max.keys ? "disabled" : ""}>${ico("plus")} مفتاح جديد</button></div><p class="sub">كل برنامج بمفتاحه، وانت بتختار يقدر يعمل إيه. ${act >= T.max.keys ? "وصلت لأقصى " + fmt(T.max.keys) + " مفاتيح شغالة." : ""}</p>
    ${T.keys.length ? T.keys.map(k => `<div class="ap-r ${k.stopped ? "off" : ""}"><div class="row"><b>${esc(k.name)}</b><span class="space"></span><span class="sm-pill ${k.stopped ? "g" : "ok"}">${k.stopped ? "متوقف" : "شغال"}</span></div><div class="ap-s">${k.scopes.map(s => `<span>${esc((tx("scopes", s) || { t: s }).t)}</span>`).join("")}</div><p>المفتاح <bdi dir="ltr">ssk_…${esc(k.last4)}</bdi> · اتعمل ${when(k.at)}${k.used ? " · آخر استخدام " + ago(k.used) : " · لسه مااستخدمش"}</p><div class="row">${k.stopped ? `<button type="button" class="btn ghost sm" data-do="apiDel:${k.id}">امسحه من القايمة</button>` : `<button type="button" class="btn ghost sm" data-do="apiStop:${k.id}">وقّفه</button>`}</div></div>`).join("") : `<div class="sm-empty"><span aria-hidden="true">🔑</span><p>مفيش مفاتيح لسه.</p></div>`}</section>
  <section class="card"><div class="row"><h3>عناوين الأحداث</h3><span class="space"></span><button type="button" class="btn sm" data-do="apiHook" ${T.hooks.length >= T.max.hooks ? "disabled" : ""}>${ico("plus")} ضيف عنوان</button></div><p class="sub">برنامجك يوصله اللي بيحصل أول ما يحصل. لو مارّدش، بنبعت تاني لحد يوم.</p>
    ${T.hooks.length ? T.hooks.map(h => `<div class="ap-r ${h.err && (!h.ok || h.fail > h.ok) ? "bad" : ""} ${h.active ? "" : "off"}"><div class="row"><span class="ap-u grow">${esc(host(h.url))}</span><span class="sm-pill ${!h.active ? "g" : h.err && (!h.ok || h.fail > h.ok) ? "bad" : h.ok ? "ok" : "g"}">${!h.active ? "متوقف" : h.err && (!h.ok || h.fail > h.ok) ? "مش بيوصل" : h.ok ? "بيوصل" : "لسه مااتبعتلوش"}</span></div><div class="ap-s">${h.events.map(e => `<span>${esc(kind(e))}</span>`).join("")}</div>
      ${h.ok ? `<p>آخر مرة وصل: ${ago(h.ok)}</p>` : ""}${h.err && (!h.ok || h.fail > h.ok) ? `<p class="ap-e">${esc(h.err)}</p>` : ""}<p>السر <bdi dir="ltr">whsec_…${esc(h.last4)}</bdi></p>
      <div class="row"><button type="button" class="btn sm" data-do="apiTry:${h.id}">جرّب</button><button type="button" class="btn ghost sm" data-do="apiHook:${h.id}">عدّل</button><button type="button" class="btn ghost sm" data-do="apiSec:${h.id}">سر جديد</button><button type="button" class="btn ghost sm" data-do="apiHOn:${h.id}:${h.active ? 0 : 1}">${h.active ? "وقّفه" : "شغّله"}</button><button type="button" class="btn ghost sm" data-do="apiHDel:${h.id}">امسحه</button></div></div>`).join("") : `<div class="sm-empty"><span aria-hidden="true">📨</span><p>مفيش عناوين. المبرمج بيقولك العنوان.</p></div>`}</section>
  <section class="card"><h3>آخر الإرسال</h3>${T.sends.length ? `<div class="sm-list">${T.sends.map(x => { const h = T.hooks.find(y => y.id === x.hook); return `<div class="sm-row"><span class="grow"><b>${esc(kind(x.kind))} <i>${h ? `· <bdi dir="ltr">${esc(host(h.url))}</bdi>` : ""}</i></b><small>${when(x.at)}${x.tries > 1 ? " · " + fmt(x.tries) + " محاولات" : ""}${x.code ? " · ردّ " + x.code : ""}${x.err && x.state !== "ok" ? " · " + esc(x.err) : ""}${x.next ? " · المحاولة الجاية " + when(x.next) : ""}</small></span>${pill(x.state)}${x.state === "failed" && x.kind !== "ping" ? `<button type="button" class="btn ghost sm" data-do="apiAgain:${x.id}">ابعته تاني</button>` : ""}</div>`; }).join("")}</div>` : `<p class="sm-hint">لسه مااتبعتش حاجة.</p>`}</section>
  <section class="card"><h3>اللي البرامج غيّرته</h3>${T.log.length ? `<div class="sm-list">${T.log.map(l => `<div class="sm-row"><span class="grow"><b>${esc(l.what)}</b><small>🔌 ${esc(l.key)} · ${when(l.at)}</small></span></div>`).join("")}</div>` : `<p class="sm-hint">لسه مفيش برنامج غيّر حاجة.</p>`}</section>` : ""}`;
}
// a key or a secret, shown once
function once(title, value, why) {
  pane({ title, cls: "small", html: () => `<p class="sub">${why}</p><code class="ap-k">${esc(value)}</code><p class="ap-w">⚠️ انسخه دلوقتي: مش هيظهر تاني لأي حد، ولا حتى ليك. لو ضاع اعمل غيره.</p>`,
    foot: () => `<button type="button" class="btn" data-do="cp">انسخه</button><button type="button" class="btn ghost" data-do="paneClose">خلاص، نسخته</button>`, acts: { cp: () => SB.copy(value, "اتنسخ") } });
}
function newKey() {
  const p = pane({ title: "مفتاح جديد", cls: "small", html: () => `<div class="sm-form">${fld("name", "اسم المفتاح", "", { max: 60, ph: "زي: برنامج الحسابات", must: true })}<div class="sm-f"><label>البرنامج يقدر</label><div class="sm-cks">${T.scopes.map(s => `<label><input type="checkbox" data-k="sc.${s.k.replace(":", "_")}" ${/read/.test(s.k) && !/customers/.test(s.k) ? "checked" : ""}><span>${esc(s.t)}</span></label>`).join("")}</div><small class="sm-hint">اختار اللي البرنامج محتاجه بس.</small></div></div>`,
    foot: () => '<button type="button" class="btn" data-do="go">اعمل المفتاح</button>',
    acts: { go: b => run(b, async () => { const d = SB.read(p.el), sc = T.scopes.map(s => s.k).filter(k => d.sc && d.sc[k.replace(":", "_")]); const r = await post("api-key", { name: d.name, scopes: sc }); T = r.tab; p.close(true); draw(SB.body()); once("المفتاح بتاع «" + d.name + "»", r.key, "ابعته للمبرمج هو ولينك صفحة الشرح. اللي معاه المفتاح يقدر يعمل اللي اخترته بس."); }) } });
}
function hookPane(id) {
  const h = T.hooks.find(x => x.id === id) || { url: "", events: ["order.created", "order.status_changed"] };
  const p = pane({ title: id ? "عدّل العنوان" : "عنوان جديد", cls: "small", html: () => `<div class="sm-form">${fld("url", "العنوان (من المبرمج)", h.url, { dir: "ltr", max: 500, ph: "https://", must: true, hint: "لازم يبدأ بـ https://، ومايكونش على شبكة داخلية." })}<div class="sm-f"><label>يوصله</label><div class="sm-cks">${T.events.map(e => `<label><input type="checkbox" data-k="ev.${e.k.replace(".", "_")}" ${h.events.includes(e.k) ? "checked" : ""}><span>${esc(e.t)}</span></label>`).join("")}</div></div></div>`,
    foot: () => '<button type="button" class="btn" data-do="go">احفظ</button>',
    acts: { go: b => run(b, async () => { const d = SB.read(p.el), ev = T.events.map(e => e.k).filter(k => d.ev && d.ev[k.replace(".", "_")]); const r = await post("api-hook", { id, url: d.url, events: ev }); T = r.tab; p.close(true); draw(SB.body()); if (r.secret) once("السر بتاع العنوان ده", r.secret, "المبرمج بيستخدمه عشان يتأكد إن الرسالة جاية من متجرك فعلًا (التوقيع)."); else say("اتحفظ ✅"); }) } });
}

/* ================= «القياس من السيرفر»: a card in «المميزات», next to «قياس الإعلانات» ================= */
const EV = { Purchase: "شراء", CompletePayment: "شراء", InitiateCheckout: "فتح صفحة الطلب", AddToCart: "حط في السلة", ViewContent: "شاف منتج", test: "تجربة" };
const HOW = {
  meta: ["افتح Events Manager على فيسبوك، واختار الـ Pixel بتاعك.", "ادخل Settings، وانزل لـ Conversions API، ودوس Generate access token.", "انسخ التوكن والصقه هنا. ولو عايز تجرّب: من تبويب Test events انسخ الكود اللي زي TEST12345."],
  tiktok: ["افتح TikTok Ads Manager، ثم Tools ثم Events، واختار الـ Pixel بتاعك.", "ادخل Settings، وفي Events API دوس Generate Access Token.", "انسخ التوكن والصقه هنا. ولو عايز تجرّب: من Test events انسخ الكود."]
};
let AD = null;
SB.capiCard = body => {
  const form = body.querySelector("[data-form]"); if (!form) return;
  const near = [...form.querySelectorAll("section.card")].find(s => /قياس الإعلانات/.test((s.querySelector("h3") || {}).textContent || ""));
  const sec = D.createElement("section"); sec.className = "card sm-ads"; sec.innerHTML = '<h3>القياس من السيرفر</h3><div class="sm-load" role="status"><i></i> ثانية واحدة...</div>';
  // its fields are its own (data-skip on each box): not the form's «احفظ», not «فيه تعديلات ماتحفظتش»
  sec.addEventListener("input", e => e.stopPropagation()); sec.addEventListener("keydown", e => { if (e.key === "Enter" && e.target.tagName === "INPUT") e.preventDefault(); });
  if (near) near.after(sec); else form.prepend(sec);
  (async () => { try { AD = await get("ads"); if (sec.isConnected) adDraw(sec); } catch (x) { if (sec.isConnected) sec.innerHTML = `<h3>القياس من السيرفر</h3><p class="sm-hint">${esc(x.message)}</p>`; } })();
};
function adDraw(sec) {
  sec = sec || D.querySelector(".sm-ads"); if (!sec) return; const d = AD.day || {};
  sec.innerHTML = `<h3>القياس من السيرفر</h3><p class="sub">لما المتصفح يمنع الـ Pixel (برامج منع الإعلانات، أو الآيفون)، الشراء بيتحسب برضه: السيرفر بيبعت نفس الحدث بنفس رقمه، فبيتحسب مرة واحدة. بيبعت رقم الزبون واسمه ومدينته متشفّرين، وصفحة الخصوصية في متجرك بتقول كده وهو شغال.</p>
    ${AD.nets.map(n => `<div class="ap-r ${n.on && n.err ? "bad" : ""}" data-net="${n.net}" data-skip><div class="row"><b>${n.name}</b><span class="space"></span><span class="sm-pill ${!n.on ? "g" : n.err ? "bad" : n.ok ? "ok" : "b"}">${!n.on ? (n.linked ? "مقفول" : "مش مربوط") : n.err ? "فيه مشكلة" : n.ok ? "شغال" : "شغال، لسه مابعتش"}</span></div>
      ${n.pixel ? `<p>الـ Pixel: <bdi dir="ltr">${esc(n.pixel)}</bdi></p>` : `<p class="ap-w">اكتب رقم ${n.name} Pixel في «قياس الإعلانات» فوق واحفظ الأول.</p>`}
      <details class="sm-kn"><summary>إزاي تجيب التوكن</summary><ol class="ap-ol">${HOW[n.net].map(x => `<li>${x}</li>`).join("")}</ol></details>
      <div class="sm-2">${fld("token", "التوكن (Access token)", "", { dir: "ltr", max: 800, type: "password", x: 'autocomplete="new-password"', ph: n.linked ? "••••" + n.last4 + " (سيبه فاضي عشان يفضل)" : "الصقه هنا", hint: "بيتحفظ متشفّر، ومابيظهرش تاني." })}${fld("test", "كود التجربة (اختياري)", n.test, { dir: "ltr", max: 40, ph: "TEST12345", hint: "لزرار «جرّب» بس، عشان التجربة ماتتحسبش زيارة حقيقية." })}</div>
      ${SB.tog("on", n.on, "ابعت من السيرفر لـ " + n.name)}
      ${n.err && n.on ? `<p class="ap-e">${esc(n.err)}${n.errAt ? " (" + ago(n.errAt) + ")" : ""}</p>` : n.ok ? `<p>آخر مرة اتبعت: ${ago(n.ok)}</p>` : ""}
      <div class="row"><button type="button" class="btn sm" data-do="adSave:${n.net}">احفظ</button>${n.linked ? `<button type="button" class="btn ghost sm" data-do="adTry:${n.net}">جرّب</button><button type="button" class="btn ghost sm" data-do="adDel:${n.net}">افصله</button>` : ""}</div></div>`).join("")}
    <h4>آخر 24 ساعة</h4><p class="sm-hint">اتبعت ${fmt(d.ok || 0)} · مستني ${fmt((d.wait || 0) + (d.sending || 0))} · ماوصلش ${fmt(d.failed || 0)}</p>
    ${AD.sends.length ? `<div class="sm-list">${AD.sends.slice(0, 12).map(x => `<div class="sm-row"><span class="grow"><b>${esc(EV[x.name] || x.name)} <i>· ${x.net === "meta" ? "Meta" : "TikTok"}</i></b><small>${when(x.at)}${x.err && x.state !== "ok" ? " · " + esc(x.err) : ""}</small></span>${pill(x.state)}</div>`).join("")}</div>` : `<p class="sm-hint">لسه مااتبعتش حاجة.</p>`}`;
}
const adBox = net => D.querySelector(`.sm-ads [data-net="${net}"]`);
A.adSave = (net, b) => run(b, async () => { const d = SB.read(adBox(net)); AD = await post("ads-save", { net, token: d.token || "", test: d.test || "", on: !!d.on }); adDraw(); say("اتحفظ ✅"); });
A.adTry = (net, b) => run(b, async () => { const r = await post("ads-test", { net }); AD = r; adDraw(); say(r.sent ? "وصل ✅ شوفه في Test events" : "ماوصلش: " + r.why, !r.sent); });
A.adDel = net => run(null, async () => { if (!await sure("تفصل القياس من السيرفر لـ " + (net === "meta" ? "Meta" : "TikTok") + "؟ التوكن هيتمسح.", "افصله")) return; AD = await post("ads-del", { net }); adDraw(); });
})();
