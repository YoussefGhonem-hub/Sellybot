/* The admin's card for the merchants' own domains (api/shop-domains-admin.ashx), under «المتاجر»: Cloudflare for SaaS on the
   sellystore.online zone. The token and the zone id are kept encrypted and never shown again (the last 4 only). Until the token, the
   zone and the CNAME target are set, owners don't see the feature. */
(() => {
"use strict";
const D = document, esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => "&#" + c.charCodeAt(0) + ";"), API = "api/shop-domains-admin.ashx";
const ST = { adding: ["بيتضاف", "warn"], pending: ["مستني السجلات", "warn"], live: ["شغال", "ok"] };
let box = null, toast = null, d = null, test = null;
async function call(body) { const r = await fetch(API, body ? { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Content-Type": "application/json" }, body: JSON.stringify(body) } : { credentials: "same-origin", headers: { "X-Requested-With": "fetch" } }); let j = null; try { j = await r.json(); } catch (e) { } if (!r.ok) throw new Error(j && j.message || "حصلت مشكلة، جرّب تاني."); return j; }
function draw() {
  box.innerHTML = `<div class="card anim" style="margin:14px 0"><h3>🌐 دومين التاجر (Cloudflare for SaaS)</h3><p class="sub">${d.set ? "مفتوح للتجار: كل تاجر يقدر يربط دومينه من «البيانات» في «متجري»." : "مقفول: التجار مش شايفينه لحد ما تكتب الثلاثة تحت."}</p>
    <div class="sp-key"><input type="password" class="ltr" dir="ltr" id="cfT" placeholder="API token${d.token ? " ••••" + esc(d.token) : ""}" autocomplete="new-password"><input type="text" class="ltr" dir="ltr" id="cfZ" placeholder="Zone ID${d.zone ? " ••••" + esc(d.zone) : ""}" autocomplete="off"></div>
    <div class="sp-key"><input type="text" class="ltr" dir="ltr" id="cfC" value="${esc(d.target)}" placeholder="${esc(d.suggest)}"><button class="btn ghost sm" data-cf="save">احفظ</button><button class="btn ghost sm" data-cf="test">جرّب</button>${d.token ? '<button class="btn ghost sm" data-cf="clear">امسح الـ token</button>' : ""}</div>
    ${test ? `<p class="hint">${test.ok ? `✅ الـ token شغال.${test.fallback ? ` الـ fallback origin: <span dir="ltr">${esc(test.fallback)}</span> (${esc(test.fallbackStatus)})` : " لسه مفيش fallback origin على الـ zone."}` : "❌ " + esc(test.why)}</p>` : ""}
    <details style="margin-top:8px"><summary>اللي يتعمل مرة واحدة على Cloudflare</summary><ol class="hint" style="line-height:1.9"><li>في zone الـ sellystore.online: Custom Hostnames (SSL/TLS) وفعّل Cloudflare for SaaS.</li><li>سجل fallback origin من غير سيرفر: <span dir="ltr">AAAA fallback 100::</span> (Proxied)، واختاره «Fallback Origin».</li><li>سجل الـ CNAME اللي التجار بيشاوروا عليه: <span dir="ltr">CNAME ${esc(d.target || d.suggest)} → fallback.sellystore.online</span> (Proxied).</li><li>Workers Routes: ضيف <span dir="ltr">*/*</span> على الـ Worker بتاع المتاجر (جنب <span dir="ltr">*.sellystore.online/*</span>).</li><li>API token بصلاحية <span dir="ltr">Zone · SSL and Certificates · Edit</span> على الـ zone دي بس، والـ Zone ID من صفحة Overview.</li></ol></details>
    ${d.list.length ? `<div class="tbl-wrap" style="margin-top:10px"><table><thead><tr><th>المتجر</th><th>الدومين</th><th>الحالة</th><th>آخر مرة</th></tr></thead><tbody>${d.list.map(x => `<tr><td><b>${esc(x.name)}</b><br><small dir="ltr">${esc(x.slug)}</small></td><td dir="ltr">${esc(x.main || x.host)}${x.apex ? "" : " (subdomain)"}</td><td><span class="pill ${(ST[x.status] || ST.pending)[1]}">${(ST[x.status] || ST.pending)[0]}</span>${x.err ? `<br><small dir="ltr" class="hint">${esc(x.err)}</small>` : ""}</td><td>${x.checked ? new Date(x.checked).toLocaleString("ar-EG", { day: "numeric", month: "short", hour: "numeric", minute: "2-digit" }) : "—"}</td></tr>`).join("")}</tbody></table></div>` : ""}</div>`;
}
window.SbDomains = {
  async open(root, t) {
    toast = t;
    if (!box) { box = D.createElement("div"); box.addEventListener("click", async e => { const b = e.target.closest("[data-cf]"); if (!b) return; const a = b.dataset.cf; if (a === "clear" && !confirm("الدومينات المربوطة هتفضل شغالة، بس مش هنقدر نضيف أو نشيّك. تمسحه؟")) return; b.disabled = true; const v = i => D.getElementById(i).value.trim(); try { d = await call(a === "save" ? { a, token: v("cfT"), zone: v("cfZ"), target: v("cfC") } : { a }); test = a === "test" ? d.result : null; draw(); toast(a === "test" ? (test.ok ? "Cloudflare ردّت ✅" : "Cloudflare مارّدتش") : "اتحفظ ✅", a === "test" && !test.ok); } catch (x) { b.disabled = false; toast(x.message, true); } }); }
    if (box.parentNode !== root.parentNode) root.parentNode.insertBefore(box, root.nextSibling);
    try { d = await call(); draw(); } catch (x) { box.innerHTML = `<div class="card hint">${esc(x.message)}</div>`; }
  }
};
})();
