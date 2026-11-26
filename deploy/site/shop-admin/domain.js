/* «متجري»: «دومين خاص بيك» in «البيانات», under the store's name and link. The owner types the domain they bought; we say exactly which
   records to add at the company they bought it from, check by ourselves every few minutes, and say where it stands in plain words.
   Shown only once the admin set Cloudflare up (ShopDomains.cs). */
(() => {
"use strict";
const SB = window.SbShop, V = SB.v, { esc, run, say, post, get, sure } = SB, D = document;
let dom = null, timer = 0;
const css = () => { if (D.getElementById("sm-dom-css")) return; const e = D.createElement("style"); e.id = "sm-dom-css"; e.textContent = `.sm-dom .sm-t td{vertical-align:top}.sm-dom code{direction:ltr;unicode-bidi:isolate;font-size:13px;word-break:break-all;background:var(--soft);padding:2px 6px;border-radius:6px;display:inline-block;max-width:100%}.sm-dom .need{font-size:11.5px;font-weight:700;color:var(--muted)}.sm-dom .row2{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-top:10px}.sm-dom .add{display:flex;gap:8px}.sm-dom .add input{flex:1;min-width:0}.sm-dom .st{padding:10px 12px;border-radius:12px;background:var(--soft);margin:8px 0;line-height:1.7}.sm-dom .st.ok{background:color-mix(in srgb,var(--ok) 12%,var(--card))}@media(max-width:600px){.sm-dom .sm-t thead{display:none}.sm-dom .sm-t tr{display:grid;grid-template-columns:auto 1fr;gap:2px 10px;padding:8px 0;border-top:1px solid var(--line)}.sm-dom .sm-t td{border:0;padding:2px 0}.sm-dom .sm-t td:nth-child(3),.sm-dom .sm-t td:nth-child(4){grid-column:1/-1}}`; D.head.appendChild(e); };
const rec = r => `<tr><td><b dir="ltr">${esc(r.type)}</b><br><span class="need">${r.need ? "مطلوب" : "اختياري"}</span></td><td><code>${esc(r.name)}</code></td><td><code>${esc(r.value)}</code> <button type="button" class="btn ghost sm" data-do="copy:${encodeURIComponent(r.value)}" aria-label="انسخ">انسخ</button></td><td><small class="sm-hint">${esc(r.note)}</small></td></tr>`;
function card() {
  const d = dom.d;
  if (!d) return `<h3>دومين خاص بيك</h3><p class="sub">لو اشتريت دومين زي nour.com، اربطه ومتجرك يفتح عليه بشهادة أمان (https). لينك ${esc(dom.old.replace(/^https?:\/\//, ""))} بيفضل شغال وبيحوّل عليه.</p><div class="add"><input type="text" dir="ltr" id="domIn" maxlength="253" placeholder="nour.com" autocomplete="off" spellcheck="false" data-enter="domAdd" aria-label="الدومين"><button type="button" class="btn" data-do="domAdd">اربطه</button></div>`;
  return `<div class="row"><h3>دومين خاص بيك</h3><span class="space"></span><span class="sm-pill ${d.status === "live" ? "ok" : "y"}">${d.status === "live" ? "شغال" : "لسه بيتجهز"}</span></div><p><b dir="ltr">${esc(d.host)}</b>${d.url ? ` · <a href="${esc(d.url)}" target="_blank" rel="noopener" dir="ltr">${esc(d.url.replace(/^https?:\/\//, ""))}</a>` : ""}</p><div class="st ${d.status === "live" ? "ok" : ""}" role="status">${esc(d.words)}</div>
    ${d.status !== "live" ? `<p class="sub">ادخل على الشركة اللي اشتريت منها الدومين (زي GoDaddy أو Namecheap)، وفي إعدادات الـ DNS ضيف السجلات دي زي ما هي:</p>` : `<details><summary>السجلات</summary>`}
    <div class="sm-tw"><table class="sm-t"><thead><tr><th>النوع</th><th>الاسم (Host)</th><th>القيمة</th><th></th></tr></thead><tbody>${d.records.map(rec).join("")}</tbody></table></div>${d.status === "live" ? "</details>" : ""}
    <div class="row2">${d.status !== "live" ? `<button type="button" class="btn ghost sm" data-do="domCheck">اتأكد دلوقتي</button>` : ""}<button type="button" class="btn ghost sm" data-do="domDel">شيل الدومين</button>${d.checked ? `<small class="sm-hint">آخر مرة شيّكنا: ${SB.ago(d.checked)}</small>` : ""}</div>`;
}
function draw() {
  const body = SB.body(); if (!body || SB.view() !== "info" || !dom || !dom.on) return; css();
  let el = body.querySelector("[data-dom]");
  if (!el) {
    el = D.createElement("section"); el.className = "card sm-dom"; el.dataset.dom = "1";
    el.addEventListener("input", e => e.stopPropagation()); el.addEventListener("change", e => e.stopPropagation());   // not part of the page's own form
    const f = body.querySelector("[data-form] > section"); if (f) f.after(el); else body.insertBefore(el, body.firstChild);
  }
  el.innerHTML = card();
  clearTimeout(timer); if (dom.d && dom.d.status !== "live") timer = setTimeout(() => { if (SB.view() === "info") get("dom").then(x => { dom = x; draw(); }).catch(() => { }); }, 60000);
}
const set = r => { if (r && r.dom) dom = r.dom; draw(); };
SB.acts.domAdd = b => run(b && b.tagName === "BUTTON" ? b : null, async () => { const i = D.getElementById("domIn"), v = i ? i.value.trim() : ""; if (!v) throw new Error("اكتب الدومين، زي nour.com"); set(await post("dom-add", { host: v })); say("اتضاف ✅ ضيف السجلات اللي تحت."); });
SB.acts.domCheck = b => run(b, async () => { set(await post("dom-check", {})); });
SB.acts.domDel = async b => { if (await sure("متجرك هيرجع يفتح على " + esc(dom.old.replace(/^https?:\/\//, "")) + " بس. تشيل الدومين؟", "شيله")) run(b, async () => { set(await post("dom-del", {})); await SB.reload(); say("اتشال."); }); };
const o0 = V.info.open; V.info.open = async function (body) { const r = await o0.call(this, body); get("dom").then(x => { dom = x; draw(); }).catch(() => { }); return r; };
})();
