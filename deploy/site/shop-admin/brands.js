/* The merchant's stores in the dashboard's account menu (api/brands.ashx, ShopBrands.cs): switching between them and «متجر جديد».
   Loaded by dashboard.html at start; it waits until the account button shows (signed in), then asks the server. Draws nothing for a
   merchant with one store while the admin hasn't turned «متجر جديد» on. */
(() => {
"use strict";
const D = document, box = D.getElementById("sbBrands"), acct = D.getElementById("acct"); if (!box || !acct) return;
const esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => "&#" + c.charCodeAt(0) + ";");
const ask = async (b) => { const r = await fetch("api/brands.ashx", { method: b ? "POST" : "GET", credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Content-Type": "application/json" }, body: b ? JSON.stringify(b) : undefined }); let d = null; try { d = await r.json(); } catch (e) { } if (!r.ok) throw new Error(d && d.message || "حصلت مشكلة، جرّب تاني."); return d; };
const css = D.createElement("style"); css.textContent = `#sbBrands:empty{display:none}#sbBrands .bh{display:block;padding:6px 12px 2px;font-size:12px;color:var(--muted)}#sbBrands button.cur{font-weight:600}#sbBrands button i{font-style:normal;color:var(--ok);margin-inline-start:6px}
.sbb-ov{position:fixed;inset:0;z-index:90;background:rgba(20,12,6,.45);display:grid;place-items:center;padding:16px}.sbb-c{background:var(--card);color:var(--ink);border:1px solid var(--line);border-radius:18px;box-shadow:var(--lift);width:min(460px,100%);max-height:calc(100vh - 32px);overflow:auto;padding:20px}
.sbb-c h2{margin:0 0 6px;font-size:18px}.sbb-c p{margin:0 0 10px;font-size:14px;color:var(--muted)}.sbb-c .warn{color:var(--ink);background:var(--soft);border:1px solid var(--line);border-radius:12px;padding:10px 12px}.sbb-c .row{margin-top:14px}
.sbb-note{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin:0 0 16px;padding:12px 14px;border-radius:14px;border:1px solid var(--line);background:var(--card);box-shadow:var(--shadow);font-size:14px}.sbb-note b{flex:1;min-width:200px;font-weight:500}`;
D.head.appendChild(css);
let S = null;
const say = t => { let e = D.getElementById("sbbToast"); if (!e) { e = D.createElement("div"); e.id = "sbbToast"; e.className = "toast"; e.setAttribute("role", "status"); D.body.appendChild(e); } e.textContent = t; e.classList.add("show"); clearTimeout(say.t); say.t = setTimeout(() => e.classList.remove("show"), 4000); };

function draw() {
  if (!S) { box.innerHTML = ""; return; } const l = S.list, many = l.length > 1;
  box.innerHTML = (many || S.on ? '<div class="sep"></div>' : "") + (many ? '<span class="bh">متاجرك</span>' + l.map(x => `<button type="button" data-sw="${x.id}" class="${x.current ? "cur" : ""}">🏪 ${esc(x.name)}${x.current ? '<i aria-label="المتجر المفتوح">✓</i>' : ""}</button>`).join("") : "")
    + (S.canAdd ? '<button type="button" data-new>➕ متجر جديد من نفس حسابك</button>' : S.on && l.length >= S.max ? `<span class="bh">عندك ${S.max} متاجر، ده الحد.</span>` : "");
  const cur = l.find(x => x.current), note = D.getElementById("sbbNote"), view = D.getElementById("appView");
  if (S.linked && cur && (!cur.plan || cur.planEnded)) {
    if (!note && view) { const n = D.createElement("div"); n.id = "sbbNote"; n.className = "sbb-note"; n.innerHTML = `<span aria-hidden="true">🏪</span><b>ده متجرك التاني «${esc(cur.name)}». المتاجر الزيادة مالهاش تجربة مجانية، فالمساعد هنا مش بيرد على حد لحد ما تشترك له في باقة.</b><button type="button" class="btn sm">اشترك من «باقتي»</button>`; n.querySelector("button").onclick = () => { const g = D.querySelector('#acctMenu [data-goto="plan"]'); if (g) g.click(); }; view.prepend(n); }
  } else if (note) note.remove();
}

box.addEventListener("click", async e => {
  const sw = e.target.closest("[data-sw]"), nw = e.target.closest("[data-new]"); if (!sw && !nw) return; e.stopPropagation();
  if (sw) { const x = S.list.find(y => y.id === +sw.dataset.sw); if (!x || x.current) return; sw.disabled = true; try { await ask({ a: "switch", id: x.id }); location.href = "dashboard.html"; } catch (err) { sw.disabled = false; say(err.message); } return; }
  D.getElementById("acctMenu").hidden = true; open();
});

function open() {
  const o = D.createElement("div"); o.className = "sbb-ov";
  o.innerHTML = `<form class="sbb-c" role="dialog" aria-modal="true" aria-labelledby="sbbT"><h2 id="sbbT">متجر جديد من نفس حسابك</h2><p>لو عندك براند تاني: متجر كامل لوحده، ليه المساعد والداتا والباقة بتوعه، ومتجر الإيجار لو حبيت. بتدخل بنفس إيميلك وباسوردك، وتنقل بين متاجرك من القايمة اللي فوق.</p>
    <label for="sbbN">اسم المتجر</label><input type="text" id="sbbN" maxlength="100" required autocomplete="off"><label for="sbbS">معرّف المتجر (إنجليزي)</label><input type="text" id="sbbS" maxlength="30" required dir="ltr" placeholder="my-brand" autocomplete="off" spellcheck="false" autocapitalize="off"><div class="hint">حروف إنجليزي صغيرة وأرقام وشرطة، من 3 لـ 30. ده اللي بيبان في لينك الشات.</div>
    <p class="warn" style="margin-top:12px">⚠️ المتجر الجديد مالوش تجربة مجانية: المساعد فيه مش هيرد على حد لحد ما تشترك له في باقة من «باقتي».</p><div class="msg err" id="sbbM" role="alert"></div>
    <div class="row"><button class="btn" type="submit">افتح المتجر</button><button class="btn ghost" type="button" data-x>إلغاء</button></div></form>`;
  const close = () => { o.remove(); D.removeEventListener("keydown", key, true); }, key = ev => { if (ev.key === "Escape") close(); };
  o.addEventListener("click", ev => { if (ev.target === o || ev.target.closest("[data-x]")) close(); }); D.addEventListener("keydown", key, true);
  const sl = o.querySelector("#sbbS"); sl.oninput = () => { sl.value = sl.value.toLowerCase().replace(/[^a-z0-9-]/g, ""); };
  o.querySelector("form").onsubmit = async ev => { ev.preventDefault(); const b = o.querySelector("[type=submit]"); b.disabled = true; o.querySelector("#sbbM").textContent = "";
    try { await ask({ a: "new", name: o.querySelector("#sbbN").value, slug: sl.value }); location.href = "dashboard.html"; } catch (err) { o.querySelector("#sbbM").textContent = err.message; b.disabled = false; } };
  D.body.appendChild(o); o.querySelector("#sbbN").focus();
}

async function load() { try { S = await ask(); } catch (e) { S = null; } draw(); }
// signed in = the account button shows; signing in or out happens without a new page, so watch it
let shown = !acct.hidden; if (shown) load();
new MutationObserver(() => { if (!acct.hidden === shown) return; shown = !acct.hidden; if (shown) load(); else { S = null; draw(); } }).observe(acct, { attributes: true, attributeFilter: ["hidden"] });
})();
