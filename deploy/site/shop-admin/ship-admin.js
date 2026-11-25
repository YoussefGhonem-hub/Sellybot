/* The admin's card for the shipping companies (api/carriers-admin.ashx), under «المتاجر»: who uses them, the trial's merchants, J&T's certified account */
(() => {
"use strict";
const D = document, esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => "&#" + c.charCodeAt(0) + ";"), fmt = n => Number(n || 0).toLocaleString("ar-EG"), API = "api/carriers-admin.ashx";
let box = null, toast = null, d = null;
async function call(body) { const r = await fetch(API, body ? { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Content-Type": "application/json" }, body: JSON.stringify(body) } : { credentials: "same-origin", headers: { "X-Requested-With": "fetch" } }); let j = null; try { j = await r.json(); } catch (e) { } if (!r.ok) throw new Error(j && j.message || "حصلت مشكلة، جرّب تاني."); return j; }
function draw() {
  const t = d.jt;
  box.innerHTML = `<div class="card anim" style="margin:14px 0"><h3>🚚 شركات الشحن</h3><p class="sub">${d.plan ? "مفتوحة من باقة «" + esc(d.plan) + "»" : "مقفولة: اختار أقل باقة ليها من «الباقات» ثم المميزات، أو حط تجار تجربة هنا"}.</p>
    <div class="tbl-wrap"><table><thead><tr><th>الشركة</th><th>تجار مربوطين</th><th>شحنات 30 يوم</th><th>اتسلّم</th><th>رجع</th><th>مش متأكدين</th><th>مناطقها عندنا</th></tr></thead><tbody>${d.list.map(x => `<tr><td><b>${esc(x.name)}</b></td><td>${fmt(x.linked)}${x.broke ? ` <span class="pill err">${fmt(x.broke)} واقف</span>` : ""}</td><td>${fmt(x.sent)}</td><td>${fmt(x.delivered)}</td><td>${fmt(x.returned)}</td><td>${x.unsure ? `<span class="pill warn">${fmt(x.unsure)}</span>` : fmt(0)}</td><td>${fmt(d.areas[x.id])}</td></tr>`).join("")}</tbody></table></div>
    <h4 style="margin:14px 0 6px">تجار التجربة</h4><div class="sp-key"><input type="text" class="ltr" dir="ltr" id="scT" value="${esc((d.testers || "").split(",").filter(Boolean).join(", "))}" placeholder="slug1, slug2"><button class="btn ghost sm" data-sc="testers">احفظ</button></div><p class="hint">لينكات متاجرهم في Sellybot (slug). بيشوفوها حتى لو مقفولة.</p>
    <h4 style="margin:14px 0 6px">حساب Sellybot عند J&amp;T ${t.set ? '<span class="pill ok">متسجّل</span>' : '<span class="pill warn">لسه</span>'}</h4><p class="hint">بعد ما J&amp;T تعتمد الشركة: Personal center في J&amp;T Open Platform. من غيره التجار بيشوفوا J&amp;T «قريب».</p>
    <div class="sp-key"><input type="text" class="ltr" dir="ltr" id="scA" placeholder="apiAccount${t.last4 ? " ••••" + esc(t.last4) : ""}" autocomplete="off"><input type="password" class="ltr" dir="ltr" id="scK" placeholder="privateKey" autocomplete="new-password"><label class="switch"><input type="checkbox" id="scD" ${t.demo ? "checked" : ""}><span class="tg"></span><span>بيئة التجربة</span></label><button class="btn ghost sm" data-sc="jt">احفظ</button>${t.set ? '<button class="btn ghost sm" data-sc="jt-clear">امسحه</button>' : ""}</div>
    <p class="hint">عنوان الإشعارات اللي يتسجّل عند J&amp;T (Logistics track return):</p><div class="sp-key"><input type="text" class="ltr" dir="ltr" readonly value="${esc(t.hook)}" onclick="this.select()"></div></div>`;
}
window.SbCarriers = {
  async open(root, t) {
    toast = t; if (!box) { box = D.createElement("div"); box.addEventListener("click", async e => { const b = e.target.closest("[data-sc]"); if (!b) return; const a = b.dataset.sc; if (a === "jt-clear" && !confirm("التجار اللي رابطين J&T هيقفوا. تمسحه؟")) return; b.disabled = true; const v = i => D.getElementById(i).value.trim(); try { d = await call(a === "testers" ? { a, testers: v("scT") } : a === "jt" ? { a, account: v("scA"), key: v("scK"), demo: D.getElementById("scD").checked } : { a }); draw(); toast("اتحفظ ✅"); } catch (x) { b.disabled = false; toast(x.message, true); } }); }
    if (box.parentNode !== root.parentNode) root.parentNode.insertBefore(box, root.nextSibling);
    try { d = await call(); draw(); } catch (x) { box.innerHTML = `<div class="card hint">${esc(x.message)}</div>`; }
  }
};
})();
