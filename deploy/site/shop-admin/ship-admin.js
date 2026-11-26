/* The admin's card for the shipping companies (api/carriers-admin.ashx), under «المتاجر»: who uses them, the trial's merchants, closing it.
   Nothing here is needed for it to work: every merchant links their own account. J&T's fallback keys stay folded away, optional. */
(() => {
"use strict";
const D = document, esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => "&#" + c.charCodeAt(0) + ";"), fmt = n => Number(n || 0).toLocaleString("ar-EG"), API = "api/carriers-admin.ashx";
let box = null, toast = null, d = null;
async function call(body) { const r = await fetch(API, body ? { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Content-Type": "application/json" }, body: JSON.stringify(body) } : { credentials: "same-origin", headers: { "X-Requested-With": "fetch" } }); let j = null; try { j = await r.json(); } catch (e) { } if (!r.ok) throw new Error(j && j.message || "حصلت مشكلة، جرّب تاني."); return j; }
function draw() {
  const t = d.jt;
  box.innerHTML = `<div class="card anim" style="margin:14px 0"><h3>🚚 شركات الشحن</h3><p class="sub">${d.off ? "مقفولة لكل المتاجر (غير تجار التجربة)" : d.plan ? "مفتوحة من باقة «" + esc(d.plan) + "»" : "مفتوحة لكل متجر لوحدها: كل تاجر بيربط حسابه هو"}.</p>
    <label class="switch"><input type="checkbox" id="scO" ${d.off ? "checked" : ""}><span class="tg"></span><span>اقفلها لكل المتاجر</span></label>
    <div class="tbl-wrap"><table><thead><tr><th>الشركة</th><th>تجار مربوطين</th><th>شحنات 30 يوم</th><th>اتسلّم</th><th>رجع</th><th>مش متأكدين</th><th>مناطقها عندنا</th></tr></thead><tbody>${d.list.map(x => `<tr><td><b>${esc(x.name)}</b></td><td>${fmt(x.linked)}${x.broke ? ` <span class="pill err">${fmt(x.broke)} واقف</span>` : ""}</td><td>${fmt(x.sent)}</td><td>${fmt(x.delivered)}</td><td>${fmt(x.returned)}</td><td>${x.unsure ? `<span class="pill warn">${fmt(x.unsure)}</span>` : fmt(0)}</td><td>${fmt(d.areas[x.id])}</td></tr>`).join("")}</tbody></table></div>
    <h4 style="margin:14px 0 6px">تجار التجربة</h4><div class="sp-key"><input type="text" class="ltr" dir="ltr" id="scT" value="${esc((d.testers || "").split(",").filter(Boolean).join(", "))}" placeholder="slug1, slug2"><button class="btn ghost sm" data-sc="testers">احفظ</button></div><p class="hint">لينكات متاجرهم في Sellybot (slug). بيشوفوها حتى لو مقفولة.</p>
    <details style="margin-top:12px"><summary class="hint" style="cursor:pointer">J&amp;T: مفاتيح احتياطية (اختياري، مش مطلوب) ${t.set ? '<span class="pill ok">متسجّلة</span>' : ""}</summary>
      <p class="hint">كل تاجر بيربط J&amp;T بمفاتيحه هو. لو حطيت هنا apiAccount و privateKey لحساب معتمد عند J&amp;T، التاجر اللي ماعندوش مفاتيح يقدر يربط بكود العميل والباسورد بس.</p>
      <div class="sp-key"><input type="text" class="ltr" dir="ltr" id="scA" placeholder="apiAccount${t.last4 ? " ••••" + esc(t.last4) : ""}" autocomplete="off"><input type="password" class="ltr" dir="ltr" id="scK" placeholder="privateKey" autocomplete="new-password"><button class="btn ghost sm" data-sc="jt">احفظ</button>${t.set ? '<button class="btn ghost sm" data-sc="jt-clear">امسحها</button>' : ""}</div>
      ${t.hook ? `<p class="hint">عنوان إشعاراتها (للحساب ده بس):</p><div class="sp-key"><input type="text" class="ltr" dir="ltr" readonly value="${esc(t.hook)}" onclick="this.select()"></div>` : ""}</details></div>`;
}
window.SbCarriers = {
  async open(root, t) {
    toast = t;
    if (!box) {
      box = D.createElement("div");
      box.addEventListener("change", async e => { if (e.target.id !== "scO") return; try { d = await call({ a: "off", off: e.target.checked }); draw(); toast("اتحفظ ✅"); } catch (x) { toast(x.message, true); } });
      box.addEventListener("click", async e => { const b = e.target.closest("[data-sc]"); if (!b) return; const a = b.dataset.sc; if (a === "jt-clear" && !confirm("التجار اللي رابطين J&T بالمفاتيح دي من غير مفاتيحهم هيقفوا. تمسحها؟")) return; b.disabled = true; const v = i => D.getElementById(i).value.trim(); try { d = await call(a === "testers" ? { a, testers: v("scT") } : a === "jt" ? { a, account: v("scA"), key: v("scK") } : { a }); draw(); toast("اتحفظ ✅"); } catch (x) { b.disabled = false; toast(x.message, true); } });
    }
    if (box.parentNode !== root.parentNode) root.parentNode.insertBefore(box, root.nextSibling);
    try { d = await call(); draw(); } catch (x) { box.innerHTML = `<div class="card hint">${esc(x.message)}</div>`; }
  }
};
})();
