/* The admin's card for «متجر جديد من نفس الحساب» (api/brands.ashx?admin=1, ShopBrands.cs), under «المتاجر»: the switch and every group */
(() => {
"use strict";
const D = document, esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => "&#" + c.charCodeAt(0) + ";"), fmt = n => Number(n || 0).toLocaleString("ar-EG"), API = "api/brands.ashx?admin=1";
let box = null, toast = null, d = null;
async function call(body) { const r = await fetch(API, { method: body ? "POST" : "GET", credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined }); let j = null; try { j = await r.json(); } catch (e) { } if (!r.ok) throw new Error(j && j.message || "حصلت مشكلة، جرّب تاني."); return j; }
function draw() {
  box.innerHTML = `<div class="card anim" style="margin:14px 0"><h3>🏪 أكتر من متجر للتاجر</h3><p class="sub">التاجر اللي عنده براند تاني يفتح متجر جديد من حسابه («متجر جديد» في قايمة حسابه)، ويتنقل بينهم من غير ما يسجّل دخول تاني. المتجر التاني مالوش تجربة مجانية، وبيشترك له من «باقتي» زي أي متجر. لحد ${fmt(d.max)} متاجر للتاجر.</p>
    <label class="switch"><input type="checkbox" id="sbbOn" ${d.on ? "checked" : ""}><span class="tg"></span><span>${d.on ? "مفتوح للتجار" : "مقفول: محدش يقدر يفتح متجر تاني (اللي اتعمل بيفضل يتنقل عادي)"}</span></label>
    ${d.groups.length ? `<div class="tbl-wrap" style="margin-top:12px"><table><thead><tr><th>المتجر الأساسي</th><th>المتاجر التانية</th></tr></thead><tbody>${d.groups.map(g => { const m = g.stores.find(x => x.main) || { name: "(اتمسح)", slug: "", email: "" }, o = g.stores.filter(x => !x.main); return `<tr><td><b>${esc(m.name)}</b><div class="hint ltr" style="text-align:right">${esc(m.slug)}${m.email ? " · " + esc(m.email) : ""}</div></td><td>${o.map(x => `<div><b>${esc(x.name)}</b> <span class="hint ltr">${esc(x.slug)}</span> ${x.plan ? `<span class="pill ok">${esc(x.plan)}</span>` : '<span class="pill">من غير باقة</span>'}</div>`).join("")}</td></tr>`; }).join("")}</tbody></table></div>` : '<p class="hint" style="margin-top:10px">لسه مفيش تاجر عنده أكتر من متجر.</p>'}</div>`;
}
window.SbBrands = {
  async open(root, t) {
    toast = t; if (!box) { box = D.createElement("div"); box.addEventListener("change", async e => { if (e.target.id !== "sbbOn") return; const on = e.target.checked; e.target.disabled = true; try { d = await call({ on }); draw(); toast(on ? "اتفتح للتجار ✅" : "اتقفل"); } catch (x) { e.target.checked = !on; e.target.disabled = false; toast(x.message, true); } }); }
    if (box.parentNode !== root.parentNode) root.parentNode.insertBefore(box, root.nextSibling);
    try { d = await call(); draw(); } catch (x) { box.innerHTML = `<div class="card hint">${esc(x.message)}</div>`; }
  }
};
})();
