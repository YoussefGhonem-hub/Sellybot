/* SellyStore: paying online through the merchant's own Paymob account (card, mobile wallet, installments) and digital products, on the
   store's side. The page loads it only when the store has one of them (BOOT.shop.card). The money goes to the merchant at Paymob; an
   order is paid only when Paymob tells the server, never because of the address the customer comes back to. No monthly amount or
   interest is shown here: Paymob's own page shows the installment plans. It wraps formBits.pay / where / place and A.place, and the
   engine asks it W.CARD.has / noCod / first / only / more (the checkout), ok() (the thank-you page) and box(token) (an order's page). */
(() => {
"use strict";
const W = window, D = document, SH = W.BOOT.shop, K = SH.card;
if (!K || !W.formBits) return;
const { S, _, esc, E, m, mt, ico, A, R, au } = W, FB = W.formBits, ways = K.ways || [], X = W.CARD = {};
const INST = /^(valu|souhoola|sympl|aman|forsa|halan|contact|premium)$/, isWay = k => k === "card" || k === "wallet" || INST.test(k || "");
const way = k => ways.find(w => w.k === k), lines = () => (W.C ? W.C() : []), dg = id => { const p = W.P(id); return p && p.dg || ""; };
const hasD = () => lines().some(l => dg(l.id)), onlyD = () => { const l = lines(); return l.length > 0 && l.every(x => dg(x.id)); };
/* installments only from the owner's minimum, on the server's total */
const can = k => { const w = way(k); return !!w && (!w.min || W.bill().tot >= w.min); };
X.has = can; X.noCod = hasD; X.only = onlyD; X.first = () => (ways.find(w => can(w.k)) || {}).k || "";
X.more = () => { const e = String(S.co.email || "").trim(); return e ? { email: e } : {}; };
const ss = (k, v) => { try { if (v === undefined) return sessionStorage.getItem(k); sessionStorage.setItem(k, v); } catch (e) { } return null; };
const day = t => new Date(t * 1000).toLocaleDateString(S.lang === "en" ? "en-GB" : "ar-EG-u-nu-latn", { day: "numeric", month: "short" });
const NAME = { card: ["بالكارت", "Card"], wallet: ["محفظة موبايل (أونلاين)", "Mobile wallet (online)"] };
const wayN = k => { const w = way(k); return w ? w.n : NAME[k] || (INST.test(k) ? ["بالتقسيط", "Installments"] : [k, k]); };
const sub = k => k === "card" ? _("فيزا أو ماستركارد أو ميزة، على صفحة Paymob", "Visa, Mastercard or Meeza, on Paymob's page") : k === "wallet" ? _("فودافون كاش وباقي المحافظ، على صفحة Paymob", "Vodafone Cash and other wallets, on Paymob's page") : _("الخطط بتظهرلك في صفحة Paymob", "The plans show on Paymob's page");
const css = D.createElement("style"); css.textContent = ".dgk .dgl,.cpk .dgl{display:flex;gap:12px;align-items:center;justify-content:space-between;text-align:start;min-width:0}.dgl>span{display:grid;min-width:0;overflow-wrap:anywhere}.dgl small{color:var(--mut)}.dgl a{flex:none}.cpp{display:flex;gap:6px;align-items:center;justify-content:center}.cpp .i{width:18px;height:18px}.cpk{display:grid;gap:12px;width:100%}.cpk .optl{width:100%}.cpk .opt{display:flex;gap:11px;align-items:center;border:1px solid var(--line2,var(--line));border-radius:var(--r);padding:11px 13px;background:var(--card);min-height:52px}.cpk .opt span{flex:1;min-width:0}.cpk .opt>span:first-child{flex:none}.cpk .opt small{display:block;color:var(--mut);font-size:13.5px}.cpk .opt.on{border-color:var(--ac);box-shadow:inset 0 0 0 1px var(--ac)}.cpk .opt svg{width:24px;height:24px}.cpi{width:84px;height:84px;border-radius:50%;display:grid;place-items:center;margin-inline:auto;background:color-mix(in srgb,var(--ac) 14%,transparent);color:var(--ai,var(--ac))}.cpi .i{width:40px;height:40px}"; D.head.appendChild(css);

/* ---------- checkout ---------- */
const pay0 = FB.pay, where0 = FB.where, place0 = FB.place, who0 = FB.who;
FB.who = () => onlyD() ? who0().replace(/<label class="fl">[^<]*<input id="f-ph2"[\s\S]*?<\/label>/, "") : who0();   /* no courier for a digital order */
FB.pay = () => {
  let h = pay0(); const dig = hasD(), cur = S.co.pay, list = ways.filter(w => can(w.k)), on = isWay(cur) && can(cur);
  if (dig) h = h.replace(/<button type="button" class="opt[^"]*" data-a="payk:cod"[\s\S]*?<\/button>/, "");   // never cash for a digital line
  const btns = list.map(w => `<button type="button" class="opt ${cur === w.k ? "on" : ""}" data-a="payk:${w.k}" aria-pressed="${cur === w.k}"><span>${ico(w.k === "wallet" ? "phone" : "cash")}</span><span>${E(w.n)}<small>${sub(w.k)}</small></span></button>`).join("");
  const at = h.indexOf('<div class="optl">'); if (at < 0) return h;
  h = h.slice(0, at + 18) + btns + h.slice(at + 18); const end = h.indexOf("</div>", at + 18 + btns.length) + 6;
  const none = !/data-a="payk:/.test(h) ? `<p class="stk0 out">${_("المنتجات الرقمية لازم تتدفع أونلاين أو بتحويل، والمتجر مش مفعّلهم دلوقتي.", "Digital products are paid online or by a transfer, and this store hasn't turned them on yet.")}</p>` : "";
  const more = (on ? `<p class="mutp">${_("بعد ما تأكد الطلب، هتدفع على صفحة Paymob، والفلوس بتروح لصاحب المتجر على طول.", "After you confirm, you pay on Paymob's page; the money goes straight to the store.")}</p>${K.test ? `<p class="mutp">${_("وضع تجربة: مفيش فلوس حقيقية بتتدفع.", "Test mode: no real money is paid.")}</p>` : ""}` : "")
    + (on || dig ? FB.fld("email", "الإيميل (اختياري)", "Email (optional)", 'type="email" inputmode="email" dir="ltr" autocomplete="email" maxlength="120"') : "");
  return h.slice(0, end) + none + more + h.slice(end);
};
FB.where = () => onlyD() ? `<div class="sec" id="whr"><h2>${_("منتج رقمي", "Digital product")}</h2><p class="mutp">${_("مفيش شحن ولا عنوان: لينك التحميل بيوصلك على الواتساب وفي صفحة الطلب بعد الدفع.", "No delivery and no address: the download link reaches you on WhatsApp and on the order's page after payment.")}</p><label class="fl">${_("ملاحظة على الطلب (اختياري)", "Note (optional)")}<textarea data-i="co:nt" maxlength="400">${esc(S.co.nt || "")}</textarea></label></div>` : where0();
FB.place = () => { let h = place0(); if (onlyD()) h = h.replace(/<p class="mutp">[^<]*<\/p>/, ""); return (hasD() ? `<p class="stk0 low">${_("المنتجات الرقمية مابترجعش بعد ما تنزّلها.", "Digital products can't be returned once downloaded.")}</p>` : "") + h; };
/* the way chosen when the order was placed: the thank-you page goes on to Paymob by itself */
const place1 = A.place; A.place = (...a) => { X.last = { pay: isWay(S.co.pay) && can(S.co.pay) ? S.co.pay : "" }; return place1(...a); };

/* ---------- an order's payment and downloads: POST /_a/card {do, tok} ---------- */
const ST = {}, ING = {}, SEL = {}, BACK = {};
const post = async b => { let j; try { const r = await fetch(au("/_a/card"), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) }); j = await r.json(); } catch (e) { throw new Error(_("النت قطع. جرّب تاني.", "The connection dropped. Try again.")); } if (j.error) throw new Error(W.said ? W.said(j.message) : j.message); return j; };
const get = (tok, again) => { if (ING[tok] || (ST[tok] && !again)) return; ING[tok] = 1; post({ do: "state", tok }).then(j => { ST[tok] = j; }).catch(e => { if (!ST[tok]) ST[tok] = { err: e.message }; }).then(() => { ING[tok] = 0; R(true); after(tok); }); };
/* back from Paymob: ?pm=1&id=<transaction>&success=... The page asks the server (which may ask Paymob), then looks again a few times */
const Q = new URLSearchParams(location.search), bk = /\/thanks\/([a-f0-9]{32})/.exec(location.pathname);
if (Q.get("pm") === "1" && bk) { BACK[bk[1]] = { id: (Q.get("id") || "").replace(/\D/g, "").slice(0, 20), n: 0 }; try { history.replaceState(history.state, "", location.pathname); } catch (e) { } }
const after = tok => {
  const st = ST[tok], b = BACK[tok]; if (!st || !st.ok) return;
  if (b && st.wait) { if (!b.asked && b.id) { b.asked = 1; post({ do: "check", tok, id: b.id }).then(j => { ST[tok] = j; R(true); after(tok); }).catch(() => { }); return; } if (b.n++ < 12) setTimeout(() => get(tok, true), 2500); else { delete BACK[tok]; R(true); } return; }
  if (b) { delete BACK[tok]; if (S.thanks && S.thanks.tok === tok) S.thanks = null; R(true); }
  if (!b && st.wait && st.can && X.last && X.last.pay && S.order && S.order.token === tok && !ss("pm-go-" + tok)) A.cpGo(tok);
};
A.cpW = (tok, k) => { SEL[tok] = k; R(true); };
A.cpGo = async (tok, k) => {
  const st = ST[tok]; if (!st || X.busy) return; const w = (typeof k === "string" && k) || SEL[tok] || (isWay(st.pay) ? st.pay : (st.ways[0] || {}).k);
  X.busy = true; X.err = ""; ss("pm-go-" + tok, "1"); R(true);
  try { const j = await post({ do: "go", tok, way: w }); location.href = j.url; } catch (e) { X.busy = false; X.err = e.message; R(true); }
};
A.cpCod = async tok => { if (X.busy) return; X.busy = true; R(true); try { ST[tok] = await post({ do: "cod", tok }); if (S.thanks && S.thanks.tok === tok) S.thanks = null; W.toast(_("تمام، هتدفع كاش عند الاستلام.", "Done: you'll pay cash on delivery.")); } catch (e) { X.err = e.message; } X.busy = false; R(true); };

const panel = (tok, st, full) => {
  if (st.timeout) return `<h1>${_("الطلب اتلغى", "The order was cancelled")}</h1><p>${_(`الدفع ماكملش خلال ${K.mins} دقيقة، فالطلب اتلغى لوحده. مفيش أي فلوس اتخصمت منك.`, `The payment wasn't completed within ${K.mins} minutes, so the order was cancelled by itself. Nothing was charged.`)}</p><button class="cta" data-a="go:home">${_("كمّل تسوّق", "Keep shopping")}</button>`;
  const sel = SEL[tok] || (isWay(st.pay) ? st.pay : (st.ways[0] || {}).k), back = BACK[tok];
  return `${full ? `<div class="cpi">${ico("clock")}</div><h1>${_("باقي تدفع", "Now pay for it")}</h1><p>${_(`طلب رقم <b>${st.no}</b> بإجمالي ${m(st.total)}`, `Order <b>${st.no}</b>, total ${m(st.total)}`)}</p>` : `<h2>${st.wait ? _("باقي تدفع", "Now pay for it") : _("تقدر تدفع أونلاين", "You can pay online")} ${m(st.total)}</h2>`}
    ${st.wait ? `<p class="mutp">${_(`الطلب محجوزلك ${st.mins} دقيقة. لو مادفعتش، بيتلغى لوحده ومفيش فلوس بتتخصم.`, `Your order is held for ${st.mins} minutes. If it isn't paid, it is cancelled by itself and nothing is charged.`)}</p>` : ""}
    ${back && st.wait ? `<p class="stk0">${_("بنتأكد من الدفع مع Paymob...", "Checking the payment with Paymob...")}</p>` : st.last && st.last.state === "failed" ? `<p class="stk0 out">${_("الدفع اللي فات مانجحش. جرّب تاني أو اختار طريقة تانية.", "The last payment didn't go through. Try again or choose another way.")}</p>` : ""}
    ${st.can ? `${st.ways.length > 1 ? `<div class="optl">${st.ways.map(w => `<button type="button" class="opt ${sel === w.k ? "on" : ""}" data-a="cpW:${tok}:${w.k}" aria-pressed="${sel === w.k}"><span>${ico(w.k === "wallet" ? "phone" : "cash")}</span><span>${E(w.n)}<small>${sub(w.k)}</small></span></button>`).join("")}</div>` : ""}<button class="cta w" data-a="cpGo:${tok}"${X.busy ? " disabled" : ""}>${X.busy ? _("لحظة...", "One moment...") : _(`ادفع ${mt(st.total)}`, `Pay ${mt(st.total)}`)}</button>` : st.wait ? `<p class="stk0 out">${_("وقت بدء الدفع للطلب ده خلص.", "The time to start paying this order has ended.")}</p>` : ""}
    ${st.cod ? `<button class="gho w" data-a="cpCod:${tok}"${X.busy ? " disabled" : ""}>${_("ادفع كاش عند الاستلام بدل كده", "Pay cash on delivery instead")}${st.codFee > 0 ? ` (+${mt(st.codFee)})` : ""}</button>` : ""}
    ${X.err ? `<p class="err" role="alert">${esc(X.err)}</p>` : ""}${st.test ? `<p class="mutp">${_("وضع تجربة: مفيش فلوس حقيقية بتتدفع.", "Test mode: no real money is paid.")}</p>` : ""}
    <p class="mutp">${_("الدفع على صفحة Paymob الآمنة، والفلوس بتروح لصاحب المتجر على طول.", "You pay on Paymob's secure page; the money goes straight to the store.")}</p>`;
};
const digi = g => `<div class="pyb dgk"><h2>${_("التحميل", "Your downloads")}</h2>${g.closed ? `<p class="mutp">${_("الطلب ده اتلغى، فاللينكات اتقفلت.", "This order was cancelled, so its links are closed.")}</p>` : !g.paid ? `<p class="mutp">${_("لينك التحميل بيظهر هنا وبيوصلك على الواتساب بعد الدفع.", "The download link shows here and reaches you on WhatsApp after payment.")}</p>` : ""}${g.l.map(x => `<div class="dgl"><span><b>${W.P(x.p) ? W.pn(W.P(x.p)) : esc(x.n)}</b><small>${x.url ? (x.open ? _(`باقي ${x.left} مرات · لحد ${day(x.until)}`, `${x.left} left · until ${day(x.until)}`) : _("اللينك خلص. اطلب من المتجر لينك جديد.", "This link has ended. Ask the store for a new one.")) : g.paid && !g.closed ? _("بيتجهّز...", "Being prepared...") : ""}${x.owed ? " · " + _("فيه كود لسه جاي", "a code is still on its way") : ""}</small></span>${x.url && x.open ? `<a class="gho sm" href="${esc(x.url)}" target="_blank" rel="noopener">${x.k === "codes" ? _("الكود", "The code") : x.k === "link" ? _("افتح", "Open") : _("نزّل", "Download")}</a>` : ""}</div>`).join("")}<p class="mutp">${_("المنتجات الرقمية مابترجعش بعد ما تنزّلها. اللينك ليك إنت بس.", "Digital products can't be returned once downloaded. The link is yours only.")}</p></div>`;
/* the thank-you page itself while a card order waits for its payment (or was cancelled for it); "" = the engine's page */
X.ok = () => {
  const tok = S.arg; if (!/^[a-f0-9]{32}$/.test(tok || "")) return "";
  const f = S.thanks && S.thanks.tok === tok ? S.thanks : null, st = ST[tok] && ST[tok].ok ? ST[tok] : null;
  const mine = st ? st.wait || st.timeout : f ? isWay(f.pay) && f.ps === "card" : !!(X.last && X.last.pay && S.order && S.order.token === tok);
  if (!mine) return "";
  if (!st) { get(tok); return `<div class="okp"><p class="mutp">${_("لحظة...", "One moment...")}</p></div>`; }
  return `<div class="okp"><div class="cpk">${panel(tok, st, true)}</div></div>`;
};
/* inside an order's page (thank-you, account): paid by card, pay online now, the downloads */
X.box = tok => {
  if (!/^[a-f0-9]{32}$/.test(tok || "")) return ""; const st = ST[tok]; if (!st) { get(tok); return ""; } if (!st.ok) return "";
  return (st.ps === "paid" && isWay(st.pay) ? `<p class="stk0 cpp">${ico("check")} ${_("اتدفع", "Paid")}: ${E(wayN(st.pay))}</p>` : "") + (st.wait || (st.pre && st.can) ? `<div class="pyb cpk">${panel(tok, st, false)}</div>` : "") + (st.dg ? digi(st.dg) : "");
};
})();
