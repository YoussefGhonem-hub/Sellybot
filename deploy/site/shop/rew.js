/* SellyStore: gift cards, points and «هات صاحبك» on the store's page (server: ShopRew.cs, ShopGift.cs, ShopPoints.cs). The server adds this
   file only when the owner turned one of them on, before core.js: nothing here touches the engine until the page is ready. The engine asks
   W.RW at a few places: the routes (gift, r), the sum (how / bill / tot), the menu and the footer, the account (more.js); the checkout's
   code box comes in after the coupon. Every amount comes from the server's sum (S.q.rw); nothing is worked out here. */
(() => {
"use strict";
const W = window, D = document;
const RW = W.RW = { v: {}, acc: {} };
const cfg = () => (W.BOOT && W.BOOT.shop && W.BOOT.shop.rw) || {};
const _ = (a, b) => W._(a, b), esc = s => W.esc(s), m = n => W.m(n), mt = n => W.mt(n), ico = (k, c) => W.ico(k, c), U = (id, h) => W.U(id, h), $ = id => D.getElementById(id);
const ls = { get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } }, set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) { } } };
const tk = () => ls.get("ss-tk", ""), norm = s => String(s || "").replace(/[٠-٩]/g, d => "٠١٢٣٤٥٦٧٨٩".indexOf(d)).toUpperCase().replace(/[^A-Z0-9]/g, "");
const st = { gc: "", pts: false, buy: { amount: 0, when: "now", day: "", pay: "", toName: "", toPhone: "", msg: "", name: "", phone: "", agree: false }, err: {}, busy: false, bal: null, ref: null, me: null, meAt: 0 };
const post = async (b) => { let j; try { const r = await fetch(W.au("/_a/rew"), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) }); j = await r.json(); } catch (e) { j = { error: "net", message: _("النت قطع. جرّب تاني.", "The connection dropped. Try again.") }; } return j; };
const date = t => t ? new Date(t * 1000).toLocaleDateString(W.S.lang === "en" ? "en-GB" : "ar-EG-u-nu-latn", { day: "numeric", month: "long", year: "numeric" }) : "";
const okPhone = v => /^01[0125]\d{8}$/.test(String(v || "").replace(/[٠-٩]/g, d => "٠١٢٣٤٥٦٧٨٩".indexOf(d)).replace(/\D/g, "").replace(/^(0020|20)(?=1)/, "0"));
const gcOn = () => !!cfg().gc, ptsOn = () => !!cfg().pts, refOn = () => !!cfg().ref;
// the code box takes a gift card, or the sender's reward code when the rewards are codes (points off)
const codeBox = () => gcOn() || (refOn() && !ptsOn());
const refCode = () => refOn() ? ls.get("ss-ref", "") : "";

/* ---- the sum: what goes with the cart to the server ---- */
RW.how = () => {
  const o = {}, c = cfg();
  if (st.gc && codeBox()) o.gc = st.gc;
  if (c.pts && c.pts.acct && tk()) { o.tk = tk(); if (st.pts) o.pts = 1; }
  if (refCode()) o.ref = refCode();
  return o;
};
const rw = () => W.S && W.S.q && W.S.q.rw || null;
// a gift card pays part: the engine's «deposit / left on delivery» lines are drawn here instead, with the card in them
RW.bill = b => { const r = rw(); return r && r.gc ? { ...b, dep: 0 } : b; };
const GCE = { bad: ["الكود ده مش كارت هدية صح.", "This isn't a valid gift card code."], stop: ["الكارت ده موقوف. كلّم المتجر.", "This card is stopped. Message the store."], exp: ["الكارت ده صلاحيته خلصت.", "This card has expired."], zero: ["الكارت ده رصيده خلص.", "This card has no balance left."], many: ["جرّبت أكواد كتير غلط. استنى ربع ساعة.", "Too many wrong codes. Wait a quarter of an hour."],
  used: ["كود المكافأة ده اتستخدم قبل كده.", "This reward code was already used."], exp2: ["كود المكافأة ده صلاحيته خلصت.", "This reward code has expired."], wait: ["كود المكافأة هيتحسب لما تكتب رقمك.", "The reward code counts once you type your number."], mine: ["كود المكافأة ده لصاحبه بس.", "This reward code is for its owner only."], min: ["الطلب صغير على الكود ده.", "The order is too small for this code."] };
const PTE = { signin: ["ادخل على حسابك الأول عشان تستخدم نقطك.", "Sign in first to use your points."], phone: ["النقط على رقم حسابك. اكتب نفس الرقم في الطلب.", "Your points belong to your account's number. Use the same number."], none: ["لسه ماعندكش نقط.", "You have no points yet."], least: ["نقطك لسه أقل من أقل عدد يتستخدم.", "You don't have the least number of points to use yet."], zero: ["مش هينفع تستخدم نقط في الطلب ده.", "Points can't be used on this order."] };
const RFE = { coupon: ["هدية صاحبك مابتتجمعش مع كود خصم. شيل الكود لو عايزها.", "Your friend's gift doesn't add up with a discount code. Remove the code to get it."], self: ["ده لينكك إنت. ابعته لصحابك.", "That's your own link. Send it to your friends."], old: ["هدية صاحبك لأول طلب من المتجر بس.", "Your friend's gift is for a first order only."], addr: ["هدية صاحبك مش على نفس عنوانه.", "The gift can't go to your friend's own address."], min: null, bad: ["لينك صاحبك مش شغال.", "Your friend's link doesn't work."] };
const giftTxt = () => { const r = cfg().ref || {}; return r.kind === "pct" ? _(`خصم ${r.value}%`, `${r.value}% off`) : _(`خصم ${mt(r.value)}`, `${mt(r.value)} off`); };
RW.tot = b => {
  const r = rw(); if (!r) return ""; let h = "";
  if (r.gc) {
    const left = r.left, dep = r.dep || 0, pay = b.pay || "cod";
    h += `<div class="tot dsc rwt"><span>${ico("gift")} ${_("كارت هدية", "Gift card")} <bdi dir="ltr">${esc(r.gc.code)}</bdi></span><span>−${m(r.gc.use)}</span></div><div class="tot b rwt"><span>${_("الباقي عليك", "Left to pay")}</span><span>${m(left)}</span></div>`;
    if (r.gc.bal > 0) h += `<p class="mutp">${_(`يفضل في الكارت ${mt(r.gc.bal)}`, `${mt(r.gc.bal)} stays on the card`)}</p>`;
    if (dep > 0 && pay !== "cod") h += `<p class="mutp">${_(`هتحوّل ${mt(dep)} بعد ما تأكد، والكارت هيدفع الباقي.`, `You'll send ${mt(dep)} after you confirm; the card pays the rest.`)}</p>`;
    else if (dep > 0) h += `<div class="tot"><span>${_("عربون قبل التجهيز", "Deposit first")}</span><span>${m(dep)}</span></div><div class="tot"><span>${_("الباقي عند الاستلام", "Left on delivery")}</span><span>${m(Math.max(0, left - dep))}</span></div>`;
  }
  const e = r.gcErr && st.gc ? GCE[r.gcErr] : null; if (e) h += `<p class="err" role="alert">${esc(_(e[0], e[1]))}</p>`;
  if (st.pts && r.pts && r.pts.err && PTE[r.pts.err]) h += `<p class="err" role="alert">${esc(_(PTE[r.pts.err][0], PTE[r.pts.err][1]))}</p>`;
  const f = r.ref || {};
  if (refCode()) {
    if (f.wait) h += `<p class="mutp rwn">${ico("gift")} ${_(`هدية صاحبك (${giftTxt()}) هتتحسب لما تكتب رقمك.`, `Your friend's gift (${giftTxt()}) counts once you type your number.`)}</p>`;
    else if (f.err === "min") h += `<p class="mutp rwn">${ico("gift")} ${_(`هدية صاحبك للطلبات من ${mt(f.min)}.`, `Your friend's gift is for orders from ${mt(f.min)}.`)}</p>`;
    else if (f.err && RFE[f.err]) h += `<p class="mutp rwn">${esc(_(RFE[f.err][0], RFE[f.err][1]))}</p>`;
    if (f.dead) ls.set("ss-ref", "");
  }
  return h;
};

/* ---- the checkout's code box and points, under the coupon ---- */
const box = () => {
  const r = rw(), c = cfg(), p = r && r.pts, me = W.ME && W.ME();
  let h = "";
  if (codeBox()) h += `<div class="cpn rwc"><input id="rwgc" aria-label="${esc(gcOn() ? _("كارت هدية", "Gift card") : _("كود مكافأة", "Reward code"))}" placeholder="${esc(gcOn() ? (refOn() && !ptsOn() ? _("كارت هدية أو كود مكافأة", "Gift card or reward code") : _("كود كارت الهدية", "Gift card code")) : _("كود مكافأة «هات صاحبك»", "Referral reward code"))}" value="${esc(st.gc)}" autocomplete="off" maxlength="20" dir="ltr"><button type="button" data-a="rwgc">${st.gc ? _("شيله", "Remove") : _("استخدمه", "Use it")}</button></div>`;
  if (c.pts && c.pts.acct) {
    if (me && tk()) { const have = p ? p.have : 0; h += `<label class="agr rwp"><input type="checkbox" data-c="rwp"${st.pts ? " checked" : ""}${p && !have ? " disabled" : ""}><span>${ico("star")} ${_("استخدم نقطك", "Use your points")}${p ? ` <small class="mutp">${_(`عندك ${W.num(have)} نقطة`, `You have ${W.num(have)}`)}</small>` : ""}</span></label>`; }
    else h += `<p class="mutp rwp">${ico("star")} ${_("عندك نقط؟", "Have points?")} <a href="${W.url("account")}" data-a="go:account">${_("ادخل على حسابك", "Sign in")}</a> ${_("عشان تستخدمها.", "to use them.")}</p>`;
  }
  return h ? `<div class="rwk">${h}</div>` : "";
};

/* ---- the menu, the footer ---- */
RW.menu = () => gcOn() && cfg().gc.pay ? [{ t: "gift", url: W.url("gift"), label: ["كروت هدايا", "Gift cards"] }] : [];
RW.foot = () => gcOn() && cfg().gc.pay ? `<li><a href="${W.url("gift")}" data-a="go:gift">${_("كروت هدايا", "Gift cards")}</a></li>` : "";
RW.nav = () => (ptsOn() || refOn()) && W.BOOT.shop.acct ? [["rew", "gift", "نقطي وهداياي", "Points & rewards"]] : [];

/* ================= the gift card page (/gift) ================= */
const card = (amount, to) => { const L = W.BOOT.look || {}, lg = W.theme && W.theme() === "dark" && L.logoDark ? L.logoDark : L.logo; return `<div class="rw-card" aria-hidden="true"><div class="rw-ct">${lg ? `<img src="${esc(W.src(lg, true))}" alt="">` : `<b>${esc(_(W.BOOT.shop.name))}</b>`}<span>${_("كارت هدية", "Gift card")}</span></div><div class="rw-cb"><b>${amount ? m(amount) : "&nbsp;"}</b><small>${to ? _("لـ ", "For ") + esc(to) : ""}</small></div></div>`; };
const PAYN = { insta: ["phone", "إنستاباي", "InstaPay"], vf: ["phone", "محفظة موبايل", "Mobile wallet"], bank: ["shield", "تحويل بنكي", "Bank transfer"] };
const fld = (k, la, le, x) => `<label class="fl">${_(la, le)}<input id="rwf-${k}" data-i="rwi:${k}" value="${esc(st.buy[k] || "")}" ${x || ""}><span class="err" id="rwe-${k}" role="alert">${esc(st.err[k] || "")}</span></label>`;
const today = () => { const d = new Date(Date.now() + 2 * 36e5); return d.toISOString().slice(0, 10); };
const plus = n => new Date(Date.now() + 2 * 36e5 + n * 864e5).toISOString().slice(0, 10);
RW.v.gift = () => {
  D.title = _("كروت هدايا", "Gift cards") + " | " + _(W.BOOT.shop.name);
  const c = cfg().gc; if (!c) return `<div class="okp"><h1>${_("الصفحة دي مش موجودة", "This page isn't here")}</h1></div>`;
  if (!c.pay) return `<div class="okp"><div class="tick">${ico("gift")}</div><h1>${_("كروت الهدايا", "Gift cards")}</h1><p class="mutp">${_("مش متاحة دلوقتي. كلّم المتجر.", "Not available right now. Message the store.")}</p></div>`;
  const b = st.buy, ways = ((W.BOOT.shop.pay || {}).ways || []).filter(w => PAYN[w.k]); if (!b.pay || !ways.some(w => w.k === b.pay)) b.pay = ways[0] ? ways[0].k : "";
  if (!b.name && W.ME && W.ME()) { b.name = W.ME().name || ""; b.phone = W.ME().phone || ""; }
  const free = c.free, amounts = c.amounts || [], other = free && b.amount && !amounts.includes(b.amount);
  return `<div class="co rwg"><h1>${_("كروت هدايا", "Gift cards")}</h1><div style="display:grid;gap:26px;min-width:0">
    <div class="sec"><h2>${_("المبلغ", "Amount")}</h2>${amounts.length ? `<div class="chips rwam">${amounts.map(a => `<button type="button" class="gho sm${b.amount === a ? " on" : ""}" aria-pressed="${b.amount === a}" data-a="rwa:${a}">${m(a)}</button>`).join("")}${free ? `<button type="button" class="gho sm${other ? " on" : ""}" aria-pressed="${!!other}" data-a="rwa:x">${_("مبلغ تاني", "Another amount")}</button>` : ""}</div>` : ""}${free && (other || !amounts.length || st.other) ? `<label class="fl">${_(`اكتب المبلغ (من ${mt(c.min)} لـ ${mt(c.max)})`, `Type the amount (${mt(c.min)} to ${mt(c.max)})`)}<input id="rwf-amount" data-i="rwamt" inputmode="numeric" dir="ltr" value="${other ? b.amount : ""}" maxlength="7"></label>` : ""}<span class="err" id="rwe-amount" role="alert">${esc(st.err.amount || "")}</span></div>
    <div class="sec"><h2>${_("الكارت لمين؟", "Who is it for?")}</h2><div class="f2">${fld("toName", "اسمه", "Their name", 'maxlength="80" autocomplete="off"')}${fld("toPhone", "رقم الواتساب بتاعه", "Their WhatsApp number", 'type="tel" inputmode="tel" dir="ltr" placeholder="01xxxxxxxxx" maxlength="20"')}</div><label class="fl">${_("رسالة معاه (اختياري)", "A message with it (optional)")}<textarea id="rwf-msg" data-i="rwi:msg" maxlength="300">${esc(b.msg)}</textarea></label><p class="mutp">${_("الكارت بيوصله على الواتساب بالكود ورسالتك.", "They get it on WhatsApp with the code and your message.")}</p></div>
    <div class="sec"><h2>${_("يتبعت إمتى؟", "When should it arrive?")}</h2><div class="optl"><button type="button" class="opt ${b.when === "now" ? "on" : ""}" aria-pressed="${b.when === "now"}" data-a="rww:now"><span>${ico("send")}</span><span>${_("أول ما التحويل يوصل", "As soon as the transfer arrives")}<small>${_("بعد ما المتجر يتأكد منه", "Once the store checks it")}</small></span></button><button type="button" class="opt ${b.when === "day" ? "on" : ""}" aria-pressed="${b.when === "day"}" data-a="rww:day"><span>${ico("clock")}</span><span>${_("في يوم أختاره", "On a day I choose")}<small>${_("الساعة 10 الصبح", "At 10 in the morning")}</small></span></button></div>${b.when === "day" ? `<label class="fl">${_("اليوم", "The day")}<input type="date" id="rwf-day" data-c="rwday" min="${today()}" max="${plus(90)}" value="${esc(b.day)}"></label>` : ""}<span class="err" id="rwe-when" role="alert">${esc(st.err.when || "")}</span></div>
    <div class="sec"><h2>${_("بياناتك", "Your details")}</h2><div class="f2">${fld("name", "اسمك", "Your name", 'maxlength="80" autocomplete="name"')}${fld("phone", "رقم موبايلك", "Your mobile number", 'type="tel" inputmode="tel" dir="ltr" autocomplete="tel" placeholder="01xxxxxxxxx" maxlength="20"')}</div></div>
    <div class="sec"><h2>${_("الدفع", "Payment")}</h2><div class="optl">${ways.map(w => `<button type="button" class="opt ${b.pay === w.k ? "on" : ""}" aria-pressed="${b.pay === w.k}" data-a="rwpay:${w.k}"><span>${ico(PAYN[w.k][0])}</span><span>${_(PAYN[w.k][1], PAYN[w.k][2])}<small>${_("بعد ما تأكد هتظهر بيانات التحويل وتبعت صورته", "After you confirm you'll see where to send it")}</small></span></button>`).join("")}</div><p class="mutp">${_("كارت الهدية مابيتدفعش كاش. بيتعمل ويتبعت بعد ما المتجر يتأكد من التحويل.", "A gift card isn't paid in cash. It's made and sent once the store checks the transfer.")}</p></div>
  </div><aside class="sum">${card(b.amount, b.toName)}<div class="tot b"><span>${_("الإجمالي", "Total")}</span><span>${b.amount ? m(b.amount) : "—"}</span></div>${c.months ? `<p class="mutp">${_(`الكارت صالح ${c.months} شهر من يوم ما يتعمل، وبيتدفع بيه في الطلب.`, `Valid for ${c.months} months from the day it's made; it pays at checkout.`)}</p>` : `<p class="mutp">${_("الكارت مالوش تاريخ انتهاء، وبيتدفع بيه في الطلب.", "The card doesn't expire; it pays at checkout.")}</p>`}
    <label class="agr"><input type="checkbox" data-c="rwag"${b.agree ? " checked" : ""}><span>${_("موافق على ", "I agree to the ")}<a href="${W.url("page", "terms")}" data-a="go:page:terms">${_("الشروط", "terms")}</a></span></label><span class="err" id="rwe-agree" role="alert">${esc(st.err.agree || "")}</span>
    <button type="button" class="cta w" data-a="rwbuy"${st.busy ? " disabled" : ""}>${st.busy ? _("لحظة...", "One moment...") : _("اطلب الكارت", "Order the card") + (b.amount ? " · " + m(b.amount) : "")}</button><p class="err" id="rwe-all" role="alert">${esc(st.err.all || "")}</p></aside>
    <section class="sec rwbal" style="grid-column:1/-1"><h2>${_("اعرف رصيد كارتك", "Check a card's balance")}</h2><div class="cpn"><input id="rwbc" aria-label="${_("كود الكارت", "The card's code")}" placeholder="ABCD-EFGH-JKMN" dir="ltr" maxlength="20" autocomplete="off"><button type="button" data-a="rwbal">${_("اعرف", "Check")}</button></div><p id="rwbo" role="status">${st.bal ? balTxt(st.bal) : ""}</p></section></div>`;
};
const balTxt = j => j.error ? `<span class="err">${esc(W.said(j.message))}</span>` : j.st === "stop" ? `<span class="err">${_("الكارت ده موقوف. كلّم المتجر.", "This card is stopped. Message the store.")}</span>` : j.st === "exp" ? `<span class="err">${_("الكارت ده صلاحيته خلصت.", "This card has expired.")}</span>` : `<b>${_(`رصيد الكارت ${esc(j.code)}: ${mt(j.bal)} من ${mt(j.amount)}`, `Card ${esc(j.code)}: ${mt(j.bal)} of ${mt(j.amount)}`)}</b>${j.until ? `<br><small class="mutp">${_("صالح لحد", "Valid until")} ${date(j.until)}</small>` : ""}`;
const redraw = () => { if (W.S.view === "gift") W.R(true); };

/* ================= the friend's link (/r/<code>) ================= */
RW.v.r = code => {
  const c = cfg().ref; code = norm(code);
  if (!c) return `<div class="okp"><h1>${_("اللينك ده مش شغال", "This link doesn't work")}</h1><a class="cta" href="${W.url("home")}" data-a="go:home">${_("ادخل المتجر", "Visit the store")}</a></div>`;
  if (!st.ref || st.ref.code !== code) { st.ref = { code, wait: true }; post({ do: "ref", code }).then(j => { st.ref = { code, ok: !!(j && j.valid) }; if (st.ref.ok) ls.set("ss-ref", j.code); if (W.S.view === "r") W.R(true); }); }
  if (st.ref.wait) return `<div class="okp"><p class="mutp">${_("لحظة...", "One moment...")}</p></div>`;
  if (!st.ref.ok) return `<div class="okp rwr"><h1>${_("اللينك ده مش شغال", "This link doesn't work")}</h1><p class="mutp">${_("اتأكد منه من صاحبك. وتقدر تتفرّج على المتجر عادي.", "Check it with your friend. You can still shop as usual.")}</p><a class="cta" href="${W.url("list", "all")}" data-a="go:list:all">${_("اتفرّج على المنتجات", "Shop now")}</a></div>`;
  return `<div class="okp rwr"><div class="tick">${ico("gift")}</div><h1>${_("صاحبك بعتلك هدية", "A friend sent you a gift")}</h1><p>${_(`${giftTxt()} على أول طلب ليك من ${esc(_(W.BOOT.shop.name))}`, `${giftTxt()} on your first order from ${esc(_(W.BOOT.shop.name))}`)}${c.least > 0 ? _(` للطلبات من ${mt(c.least)}`, ` for orders from ${mt(c.least)}`) : ""}.</p><p class="mutp">${_("الهدية محفوظة على موبايلك، وبتتحسب في صفحة الطلب بعد ما تكتب رقمك.", "The gift is kept on this phone and counts on the order page once you type your number.")}</p><a class="cta" href="${W.url("list", "all")}" data-a="go:list:all">${_("اتفرّج على المنتجات", "Shop now")}</a></div>`;
};

/* ================= the account: points and the personal link ================= */
const KIND = { earn: "box", back: "back", use: "tag", undo: "back", hand: "gift", refer: "gift", unref: "back" };
RW.acc.rew = () => {
  if (!st.me || Date.now() - st.meAt > 60000) { if (!st.meL) { st.meL = true; post({ do: "me", tk: tk() }).then(j => { st.meL = false; st.meAt = Date.now(); st.me = j && j.ok ? j.me : { err: j && j.message }; if (W.S.view === "account") W.R(true); }); } if (!st.me) return `<h1>${_("نقطي وهداياي", "Points & rewards")}</h1><p class="mutp">${_("لحظة...", "One moment...")}</p>`; }
  const me = st.me; if (me.err) return `<h1>${_("نقطي وهداياي", "Points & rewards")}</h1><p class="err">${esc(W.said(me.err))}</p>`;
  const p = me.pts, f = me.ref, rp = cfg().pts || {}, rf = cfg().ref || {}; let h = `<h1>${_("نقطي وهداياي", "Points & rewards")}</h1>`;
  if (p) h += `<section class="rwa"><div class="tls"><a><b>${W.num(p.have)}</b><span>${_("نقطة معاك", "points now")}</span></a><a><b>${m(p.worth)}</b><span>${_("قيمتها", "worth")}</span></a><a><b>${W.num(p.coming)}</b><span>${_("جاية لما طلباتك توصل", "coming with your orders")}</span></a></div>
      <p class="mutp">${_(`كل ${mt(rp.per)} في المنتجات = نقطة، وكل ${W.num(rp.pts)} نقطة بـ ${mt(rp.egp)}. بتستخدمها في صفحة الطلب لحد ${rp.cap}% من الطلب${rp.least ? ` ومن ${W.num(rp.least)} نقطة` : ""}${rp.months ? `، وصالحة ${rp.months} شهر من يوم ما اتكسبت` : ""}.`, `Every ${mt(rp.per)} on products = 1 point; ${W.num(rp.pts)} points are worth ${mt(rp.egp)}. Use them at checkout for up to ${rp.cap}% of an order${rp.least ? `, from ${W.num(rp.least)} points` : ""}${rp.months ? `; they last ${rp.months} months` : ""}.`)}</p>
      ${p.soon ? `<p class="stk0 low">${_(`${W.num(p.soon)} نقطة صلاحيتها بتخلص يوم ${date(p.soonAt)}`, `${W.num(p.soon)} points end on ${date(p.soonAt)}`)}</p>` : ""}
      ${p.list.length ? `<div class="sech"><h2>${_("حركة النقط", "Points history")}</h2></div><div class="rwl">${p.list.map(x => `<div class="rwi"><span class="rwic">${ico(KIND[x.k] || "star")}</span><span><b>${esc(_(x.t))}</b>${x.note ? `<small>${esc(x.note)}</small>` : ""}<small>${date(x.at)}${x.no ? ` · ${_("طلب", "order")} ${x.no}` : ""}${x.exp && x.n > 0 ? ` · ${x.gone ? _("خلصت صلاحيتها", "expired") : _("لحد", "until") + " " + date(x.exp)}` : ""}</small></span><b class="${x.n < 0 ? "neg" : "pos"}" dir="ltr">${x.n > 0 ? "+" : ""}${W.num(x.n)}</b></div>`).join("")}</div>` : `<p class="mutp">${_("لسه مفيش نقط. أول طلب يوصلك بتنزل نقطه هنا.", "No points yet. Your first delivered order adds them here.")}</p>`}</section>`;
  if (f) {
    const txt = _(`جرّب ${_(W.BOOT.shop.name)}! خد ${giftTxt()} على أول طلب ليك من اللينك ده: ${f.link}`, `Try ${_(W.BOOT.shop.name)}! Get ${giftTxt()} on your first order with this link: ${f.link}`);
    h += `<section class="rwa"><div class="sech"><h2>${_("هات صاحبك", "Bring a friend")}</h2></div><p>${_(`ابعت اللينك ده لصحابك: ياخدوا ${giftTxt()} على أول طلب${rf.least ? ` من ${mt(rf.least)}` : ""}، وانت تاخد ${rf.pts ? `${W.num(rf.rewardPts || 0)} نقطة` : `كود خصم بـ ${mt(rf.reward)}`} لما طلبهم يوصلهم.`, `Send this link to your friends: they get ${giftTxt()} on a first order${rf.least ? ` from ${mt(rf.least)}` : ""}, and you get ${rf.pts ? `${W.num(rf.rewardPts || 0)} points` : `a code worth ${mt(rf.reward)}`} when their order is delivered.`)}</p>
      <div class="cpn"><input readonly value="${esc(f.link)}" dir="ltr" aria-label="${_("لينكك", "Your link")}"><button type="button" data-a="rwcp:${encodeURIComponent(f.link)}">${_("انسخ", "Copy")}</button></div>
      <a class="cta w" href="https://wa.me/?text=${encodeURIComponent(txt)}" target="_blank" rel="noopener">${ico("wa")} ${_("ابعته على واتساب", "Share on WhatsApp")}</a>
      <div class="tls"><a><b>${W.num(f.friends)}</b><span>${_("صاحب طلب", "friends ordered")}</span></a><a><b>${W.num(f.got)}</b><span>${_("مكافأة خدتها", "rewards")}</span></a><a><b>${W.num(f.month)} / ${W.num(f.max)}</b><span>${_("مكافآت الشهر ده", "this month")}</span></a></div>
      ${f.list.length ? `<div class="rwl">${f.list.map(x => `<div class="rwi"><span class="rwic">${ico(x.st === "rewarded" ? "gift" : "clock")}</span><span><b>${x.st === "rewarded" ? (x.pts ? _(`${W.num(x.pts)} نقطة`, `${W.num(x.pts)} points`) : _(`كود بـ ${mt(x.amt)}`, `A code worth ${mt(x.amt)}`)) : x.st === "capped" ? _("طلب صاحبك وصل (الشهر ده خلّص مكافآته)", "Delivered (this month's rewards were used up)") : _("صاحبك طلب، ومستنيين الطلب يوصله", "Your friend ordered; waiting for delivery")}</b><small>${date(x.at)}${x.code ? ` · <bdi dir="ltr">${esc(x.code)}</bdi> · ${x.used ? _("اتستخدم", "used") : _("لحد", "until") + " " + date(x.until)}` : ""}</small></span></div>`).join("")}</div>` : ""}</section>`;
  }
  return h;
};

/* ---- start: after the engine and the template are there ---- */
const init = () => {
  const A = W.A, IN = W.IN, CH = W.CH, fb = W.formBits; if (!A || !fb) return;
  const cp0 = fb.coupon; fb.coupon = () => cp0() + box();
  A.rwgc = () => { const e = $("rwgc"), v = norm(e ? e.value : ""); st.gc = st.gc ? "" : v; W.requote(true).then(() => { W.R(true); const r = rw(); if (st.gc && r && (r.gc || r.rc)) W.toast(_("الكود اتطبّق", "Code applied")); }); };
  CH.rwp = v => { st.pts = !!v; W.requote(true).then(() => W.R(true)); };
  // the gift card page
  A.rwa = a => { st.err.amount = ""; if (a === "x") { st.other = true; st.buy.amount = 0; } else { st.other = false; st.buy.amount = +a; } redraw(); };
  IN.rwamt = v => { st.buy.amount = Math.round(+String(v).replace(/[٠-٩]/g, d => "٠١٢٣٤٥٦٧٨٩".indexOf(d)).replace(/\D/g, "")) || 0; const t = D.querySelector("#st .rwg .sum .tot.b span:last-child"); if (t) t.innerHTML = st.buy.amount ? m(st.buy.amount) : "—"; const cb = D.querySelector("#st .rw-cb b"); if (cb) cb.innerHTML = st.buy.amount ? m(st.buy.amount) : "&nbsp;"; };
  IN.rwi = (v, k) => { st.buy[k] = v; st.err[k] = ""; const e = $("rwe-" + k); if (e) e.textContent = ""; if (k === "toName") { const s = D.querySelector("#st .rw-cb small"); if (s) s.textContent = v ? _("لـ ", "For ") + v : ""; } };
  A.rww = w => { st.buy.when = w; if (w === "day" && !st.buy.day) st.buy.day = plus(1); redraw(); };
  CH.rwday = v => { st.buy.day = v; st.err.when = ""; };
  A.rwpay = k => { st.buy.pay = k; redraw(); };
  CH.rwag = v => { st.buy.agree = !!v; st.err.agree = ""; const e = $("rwe-agree"); if (e) e.textContent = ""; };
  A.rwbuy = async () => {
    if (st.busy) return; const b = st.buy, c = cfg().gc || {}, e = {};
    if (!b.amount || (!(c.amounts || []).includes(b.amount) && !(c.free && b.amount >= c.min && b.amount <= c.max))) e.amount = c.free ? _(`اكتب مبلغ من ${mt(c.min)} لـ ${mt(c.max)}`, `Type an amount from ${mt(c.min)} to ${mt(c.max)}`) : _("اختار مبلغ الكارت", "Choose the card's amount");
    if ((b.toName || "").trim().length < 2) e.toName = _("اكتب اسمه", "Enter their name"); if (!okPhone(b.toPhone)) e.toPhone = _("رقم موبايل مصري من 11 رقم", "An 11-digit Egyptian mobile number");
    if ((b.name || "").trim().length < 2) e.name = _("اكتب اسمك", "Enter your name"); if (!okPhone(b.phone)) e.phone = _("رقم موبايل مصري من 11 رقم", "An 11-digit Egyptian mobile number");
    if (b.when === "day" && !b.day) e.when = _("اختار اليوم", "Choose the day"); if (!b.agree) e.agree = _("وافق على الشروط الأول", "Please agree first");
    st.err = e; if (Object.keys(e).length) { redraw(); const f = D.querySelector("#st .rwg .err:not(:empty)"); if (f) f.scrollIntoView({ block: "center" }); return; }
    if (!st.ck || st.ck.s !== JSON.stringify(b)) st.ck = { s: JSON.stringify(b), k: (W.crypto && crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2)) };
    st.busy = true; redraw();
    const j = await post({ do: "buy", amount: b.amount, toName: b.toName, toPhone: b.toPhone, msg: b.msg, when: b.when === "day" ? b.day : "now", name: b.name, phone: b.phone, pay: b.pay, agree: true, ck: st.ck.k, dev: ls.get("ss-dev", ""), src: ls.get("ss-src", "") });
    st.busy = false;
    if (j && j.ok) { if (j.token) ls.set("ss-ord", [j.token, ...ls.get("ss-ord", []).filter(x => x !== j.token)].slice(0, 20)); st.buy = { amount: 0, when: "now", day: "", pay: b.pay, toName: "", toPhone: "", msg: "", name: b.name, phone: b.phone, agree: false }; st.ck = null; W.A.go("pay", j.token); return; }
    if (j && j.error === "fields" && j.data) { st.err = { ...j.data }; for (const k in st.err) st.err[k] = W.said(st.err[k]); } else st.err = { all: W.said(j && j.message) || _("حصلت مشكلة. جرّب تاني.", "Something went wrong. Try again.") };
    redraw();
  };
  A.rwbal = async () => { const v = norm(($("rwbc") || {}).value); if (v.length !== 12) { st.bal = { error: "x", message: _("الكود 12 حرف ورقم.", "The code has 12 letters and digits.") }; return U("rwbo", balTxt(st.bal)); } U("rwbo", _("لحظة...", "One moment...")); st.bal = await post({ do: "bal", code: v }); U("rwbo", balTxt(st.bal)); };
  A.rwcp = t => { const v = decodeURIComponent(t), done = () => W.toast(_("اللينك اتنسخ", "Link copied")); if (navigator.clipboard) navigator.clipboard.writeText(v).then(done, done); else done(); };
  // the order went through: the code and the points are spent, the friend's gift used
  const pl0 = A.place; A.place = async (...a) => { const r0 = rw(); await pl0(...a); if (W.S.order && W.S.order.no) { st.gc = ""; st.pts = false; st.me = null; if (r0 && r0.ref && r0.ref.off > 0) ls.set("ss-ref", ""); } };
  // the number decides the friend's gift and a reward code: asked again when it's typed
  let pT = 0; D.addEventListener("input", e => { if (e.target && e.target.id === "f-ph" && (refCode() || st.gc)) { clearTimeout(pT); pT = setTimeout(() => { if (okPhone(e.target.value)) W.requote(true).then(() => W.retot && W.retot()); }, 500); } });
  // drawn before this was ready: again
  if (W.S && (W.S.view === "co" || W.S.view === "gift" || W.S.view === "r" || W.S.view === "account")) W.R(true);
  if (W.C && W.C().length && (refCode() || ptsOn())) W.requote(true).then(() => { if (W.S.view === "co" && W.retot) W.retot(); });
};
// this file runs before core.js (deferred, in order): DOMContentLoaded comes after both; the engine is waited for anyway, a few seconds at most
let tries = 0; const boot = () => { if (W.A && W.formBits && W.IN && W.CH) init(); else if (tries++ < 100) setTimeout(boot, 50); };
if (D.readyState === "complete") boot(); else D.addEventListener("DOMContentLoaded", boot);
})();
