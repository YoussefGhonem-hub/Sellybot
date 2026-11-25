/* «متجري»: the owner's side of a store on SellyStore (api/shop.ashx). The dashboard loads these files when the tab opens:
   app.js (this: the frame, home, settings, shipping, pages, the bot, the rent) · catalog.js (products, categories) ·
   sell.js (orders, offers, ads) · look.js (the store's look, with the private preview). SbShop.open(root, { toast, me, go }). */
(() => {
"use strict";
const SB = window.SbShop = window.SbShop || {}, V = SB.v = SB.v || {}, D = document, W = window;
let root = null, X = null, st = null, view = "home", arg = null, opened = false;
SB.state = () => st;

/* ---------- words, numbers, dates ---------- */
const esc = SB.esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => "&#" + c.charCodeAt(0) + ";");
const fmt = SB.fmt = n => Number(n || 0).toLocaleString("ar-EG", { maximumFractionDigits: 2 });
const mny = SB.mny = n => fmt(n) + " ج";
const day = SB.day = t => t ? new Date(t).toLocaleDateString("ar-EG", { day: "numeric", month: "long", ...(new Date(t).getFullYear() === new Date().getFullYear() ? {} : { year: "numeric" }) }) : "";
const when = SB.when = t => t ? new Date(t).toLocaleDateString("ar-EG", { day: "numeric", month: "short" }) + "، " + new Date(t).toLocaleTimeString("ar-EG", { hour: "numeric", minute: "2-digit" }) : "";
SB.ago = t => { const m = Math.round((Date.now() - new Date(t)) / 6e4); return m < 1 ? "دلوقتي" : m < 60 ? "من " + fmt(m) + " دقيقة" : m < 1440 ? "من " + fmt(Math.round(m / 60)) + " ساعة" : m < 2880 ? "امبارح" : day(t); };
const num = SB.num = s => { s = String(s == null ? "" : s).replace(/[٠-٩]/g, d => "٠١٢٣٤٥٦٧٨٩".indexOf(d)).replace(/[٫]/g, ".").replace(/[^\d.\-]/g, ""); return s === "" || isNaN(+s) ? 0 : +s; };
const plural = SB.plural = (n, one, two, few, many) => n === 1 ? one : n === 2 ? two : n >= 3 && n <= 10 ? fmt(n) + " " + few : fmt(n) + " " + many;

/* ---------- the server ---------- */
const API = "api/shop.ashx";
async function ask(url, init) {
  let r, d = null;
  try { r = await fetch(url, { credentials: "same-origin", ...init, headers: { "X-Requested-With": "fetch", ...(init && init.headers) } }); } catch (e) { const x = new Error("النت فصل. جرّب تاني."); x.code = "net"; throw x; }
  try { d = await r.json(); } catch (e) { }
  if (!r.ok) { const e = new Error(d && d.message || (r.status === 401 ? "سجّل دخولك تاني." : "حصلت مشكلة، جرّب تاني.")); e.status = r.status; e.code = d && d.error; e.data = d && d.data; throw e; }
  return d;
}
SB.get = (a, q) => ask(API + "?a=" + a + (q ? "&" + new URLSearchParams(q) : ""));
const post = SB.post = async (a, b) => { const d = await ask(API, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ a, ...b }) }); if (d && d.state) setState(d.state); if (d && d.cats && st) st.cats = d.cats; return d; };
SB.file = (f, extra) => { const fd = new FormData(); fd.append("file", f); for (const k in extra || {}) fd.append(k, extra[k]); return ask(API, { method: "POST", body: fd }); };
SB.ask = ask;
const setState = s => { st = s; head(); };
SB.reload = async () => { setState(await SB.get("state")); };
/* a button that works on something: can't be pressed twice, says what went wrong */
const run = SB.run = async (btn, fn) => { if (btn) { if (btn.disabled) return; btn.disabled = true; btn.classList.add("sm-busy"); } try { return await fn(); } catch (e) { say(e.message, true); return undefined; } finally { if (btn && btn.isConnected) { btn.disabled = false; btn.classList.remove("sm-busy"); } } };
let sayT = 0;
const say = SB.say = (t, bad) => { let e = D.getElementById("smSay"); if (!e) { e = D.createElement("div"); e.id = "smSay"; e.setAttribute("role", "status"); D.body.appendChild(e); } e.className = "sm-say on" + (bad ? " bad" : ""); e.textContent = t; clearTimeout(sayT); sayT = setTimeout(() => e.classList.remove("on"), bad ? 6500 : 2800); };

/* ---------- small pieces ---------- */
const ICO = { x: '<path d="m6 6 12 12M18 6 6 18"/>', plus: '<path d="M12 5v14M5 12h14"/>', grip: '<path d="M9 6h.01M15 6h.01M9 12h.01M15 12h.01M9 18h.01M15 18h.01" stroke-width="3"/>', eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z"/><circle cx="12" cy="12" r="3"/>', off: '<path d="M4 4l16 16M9.9 5.8A9.8 9.8 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a16 16 0 0 1-3 3.7M6 7.6A15.600 15.600 0 0 0 2.500 12S6 18.500 12 18.500a9.500 9.500 0 0 0 4-.9"/>', pen: '<path d="m4 20 1-4L16.500 4.500l3 3L8 19l-4 1Z"/>', bin: '<path d="M5 7h14M10 7V4.500h4V7M7 7l1 13h8l1-13"/>', copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a1 1 0 0 1 1-1h9"/>', out: '<path d="M14 4h6v6M20 4l-9 9M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5"/>', check: '<path d="m5 12.500 4.500 4.500L19 7"/>', next: '<path d="M14.500 5.500 8 12l6.500 6.500"/>', up: '<path d="m6 15 6-6 6 6"/>', down: '<path d="m6 9 6 6 6-6"/>', img: '<rect x="3.500" y="5" width="17" height="14" rx="2"/><circle cx="9" cy="10" r="1.700"/><path d="m4 17 5-4.500 3.500 3L16 12l4.500 4"/>', search: '<circle cx="11" cy="11" r="6.500"/><path d="m16 16 4.500 4.500"/>', pin: '<path d="M12 16v5M8 4h8l-1 6 3 4H6l3-4-1-6Z"/>', dots: '<path d="M6 12h.01M12 12h.01M18 12h.01" stroke-width="3"/>', phone: '<rect x="7" y="3" width="10" height="18" rx="2.500"/><path d="M11 18h2"/>', desk: '<rect x="3" y="4.500" width="18" height="12" rx="1.500"/><path d="M9 20h6M12 16.500V20"/>', moon: '<path d="M20 14.500A8.500 8.500 0 0 1 9.500 4 8.500 8.500 0 1 0 20 14.500Z"/>' };
const ico = SB.ico = k => `<svg class="sm-i" viewBox="0 0 24 24" aria-hidden="true">${ICO[k] || ""}</svg>`;
SB.tog = (k, on, t, sub, x) => `<label class="toggle"><input type="checkbox" data-k="${k}" ${on ? "checked" : ""} ${x || ""}><span class="tg" aria-hidden="true"></span><span>${t}${sub ? `<small>${sub}</small>` : ""}</span></label>`;
const fld = SB.fld = (k, t, v, o) => { o = o || {}; const id = "f_" + k.replace(/\W/g, "_"); return `<div class="sm-f ${o.cls || ""}"><label for="${id}">${t}${o.must ? ' <em>مطلوب</em>' : ""}</label>${o.area ? `<textarea id="${id}" data-k="${k}" rows="${o.area}" ${o.max ? `maxlength="${o.max}"` : ""} ${o.dir ? `dir="${o.dir}"` : ""} placeholder="${esc(o.ph || "")}">${esc(v)}</textarea>` : `${o.suf ? '<span class="sm-iw">' : ""}<input id="${id}" data-k="${k}" type="${o.type || "text"}" ${o.mode ? `inputmode="${o.mode}"` : ""} ${o.max ? `maxlength="${o.max}"` : ""} ${o.dir ? `dir="${o.dir}"` : ""} value="${esc(v)}" placeholder="${esc(o.ph || "")}" ${o.x || ""}>${o.suf ? `<span class="sm-suf">${o.suf}</span></span>` : ""}`}${o.hint ? `<small class="sm-hint">${o.hint}</small>` : ""}</div>`; };
/* Arabic and English side by side (the English only when the store has it) */
SB.two = (k, t, v, o) => { const en = st && st.draft && st.draft.langs !== "ar"; v = v || ["", ""]; return `<div class="${en ? "sm-2" : ""}">${fld(k + ".0", t, v[0], o)}${en ? fld(k + ".1", t + " (إنجليزي)", v[1], { ...o, dir: "ltr", must: false, hint: "" }) : `<input type="hidden" data-k="${k}.1" value="${esc(v[1] || "")}">`}</div>`; };
SB.sel = (k, list, v, x) => `<select data-k="${k}" ${x || ""}>${list.map(o => `<option value="${esc(o[0])}"${String(v) === String(o[0]) ? " selected" : ""}>${esc(o[1])}</option>`).join("")}</select>`;
SB.seg = (name, list, v) => `<div class="sm-seg" role="radiogroup">${list.map(o => `<button type="button" role="radio" aria-checked="${String(v) === String(o[0])}" class="${String(v) === String(o[0]) ? "on" : ""}" data-do="${name}:${esc(o[0])}">${o[1]}</button>`).join("")}</div>`;
/* what the fields inside an element say: data-k="a.b.0" -> {a:{b:["..."]}} */
SB.read = el => { const o = {}; el.querySelectorAll("[data-k]").forEach(e => { if (e.closest("[data-skip]") && e.closest("[data-skip]") !== el) return; const v = e.type === "checkbox" ? e.checked : e.dataset.n !== undefined ? num(e.value) : e.value.trim(), p = e.dataset.k.split("."); let x = o; p.forEach((k, i) => { if (i === p.length - 1) x[k] = v; else x = x[k] = x[k] || (/^\d+$/.test(p[i + 1]) ? [] : {}); }); }); return o; };
SB.copy = async (t, done) => { try { await navigator.clipboard.writeText(t); } catch (e) { const a = D.createElement("textarea"); a.value = t; D.body.appendChild(a); a.select(); try { D.execCommand("copy"); } catch (e2) { } a.remove(); } say(done || "اتنسخ"); };
/* «متأكد؟»: resolves true / false */
SB.sure = (t, yes, o) => new Promise(ok => { o = o || {}; const e = D.createElement("div"); e.className = "sm-ask"; e.innerHTML = `<div class="card" role="alertdialog" aria-modal="true"><p>${t}</p><div class="row"><button type="button" class="btn ${o.danger === false ? "" : "danger"}" data-y>${esc(yes || "أيوه")}</button><button type="button" class="btn ghost" data-n>${esc(o.no || "لأ")}</button></div></div>`; const end = v => { e.remove(); D.removeEventListener("keydown", key, true); ok(v); }; const key = ev => { if (ev.key === "Escape") { ev.stopPropagation(); end(false); } }; e.onclick = ev => { if (ev.target === e || ev.target.closest("[data-n]")) end(false); else if (ev.target.closest("[data-y]")) end(true); }; D.addEventListener("keydown", key, true); D.body.appendChild(e); e.querySelector("[data-y]").focus(); });

/* ---------- pictures: shrunk here (big ≤ 1300, small 480), so uploads are light on any phone ---------- */
const toUrl = (cv, type, q) => new Promise((ok, no) => cv.toBlob(b => { if (!b) return no(new Error("الصورة ماتقريتش.")); const r = new FileReader(); r.onload = () => ok(r.result); r.onerror = no; r.readAsDataURL(b); }, type, q));
const webp = (() => { try { const c = D.createElement("canvas"); c.width = c.height = 1; return c.toDataURL("image/webp").startsWith("data:image/webp"); } catch (e) { return false; } })();
SB.shrink = async (file, o) => {
  o = o || {}; if (!/^image\//.test(file.type) && !/\.(jpe?g|png|webp|heic|heif|gif|bmp)$/i.test(file.name || "")) throw new Error("ده مش ملف صورة.");
  let bmp; try { bmp = await createImageBitmap(file, { imageOrientation: "from-image" }); } catch (e) { bmp = await new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = () => no(new Error("الصورة دي مش بتتفتح. احفظها JPG وجرّب.")); i.src = URL.createObjectURL(file); }); }
  const w0 = bmp.width || bmp.naturalWidth, h0 = bmp.height || bmp.naturalHeight, make = async (max, q) => { const k = Math.min(1, max / Math.max(w0, h0)), c = D.createElement("canvas"); c.width = Math.max(1, Math.round(w0 * k)); c.height = Math.max(1, Math.round(h0 * k)); const g = c.getContext("2d"); g.imageSmoothingQuality = "high"; const type = webp ? "image/webp" : /png|gif/.test(file.type) ? "image/png" : "image/jpeg"; if (type === "image/jpeg") { g.fillStyle = "#fff"; g.fillRect(0, 0, c.width, c.height); } g.drawImage(bmp, 0, 0, c.width, c.height); return { url: await toUrl(c, type, q), w: c.width, h: c.height }; };
  let big = await make(o.max || 1300, .84); if (big.url.length > 1300000) big = await make(1100, .74); if (big.url.length > 1700000) big = await make(900, .68);
  const small = await make(480, .8); let dark = false; try { const c = D.createElement("canvas"); c.width = c.height = 24; const g = c.getContext("2d"); g.fillStyle = "#fff"; g.fillRect(0, 0, 24, 24); g.drawImage(bmp, 0, 0, 24, 24); const p = g.getImageData(0, 0, 24, 24).data; let s = 0; for (let i = 0; i < p.length; i += 4) s += .3 * p[i] + .59 * p[i + 1] + .11 * p[i + 2]; dark = s / 576 < 62; } catch (e) { } return { data: big.url, small: small.url, w: big.w, h: big.h, note: [Math.max(w0, h0) < 800 ? "صغيرة: هتبان مش واضحة على الشاشات الكبيرة" : "", dark ? "ضلمة: صوّرها في نور أحسن" : ""].filter(Boolean).join("، ") };
};
/* asks for pictures (one or several) and uploads them. pid: the product they belong to (0 = none) */
SB.pick = (many, accept) => new Promise(ok => { const i = D.createElement("input"); i.type = "file"; i.accept = accept || "image/*"; i.multiple = !!many; i.style.display = "none"; i.onchange = () => { ok([...i.files]); i.remove(); }; i.addEventListener("cancel", () => { ok([]); i.remove(); }); D.body.appendChild(i); i.click(); });
SB.upload = async (file, pid, alt, o) => { const { note, ...s } = await SB.shrink(file, o), im = (await post("image", { id: pid || 0, ...s, alt: alt || "" })).image; im.note = note; return im; };
/* a picture box in a form: data-img="path" holds its value */
SB.imgBox = (k, path, t, o) => { o = o || {}; return `<div class="sm-ib ${o.cls || ""}" data-ib="${k}"><span class="sm-ib-p" ${o.dark ? 'data-dark' : ""}>${path ? `<img src="${esc(SB.src(path))}" alt="">` : ico("img")}</span><span class="sm-ib-t"><b>${t}</b>${o.hint ? `<small>${o.hint}</small>` : ""}<span class="row"><button type="button" class="btn ghost sm" data-do="ibUp:${k}">${path ? "غيّرها" : "ارفع صورة"}</button>${path ? `<button type="button" class="btn ghost sm" data-do="ibDel:${k}">شيلها</button>` : ""}</span></span></div>`; };
SB.src = (p, big) => p ? st.img + (big ? p : p.replace(/(\.\w+)$/, "-s$1")) : "";

/* ---------- a panel over the page ---------- */
const panes = [];
const top = SB.top = () => panes[panes.length - 1] || null;
SB.pane = o => {
  const e = D.createElement("div"); e.className = "sm-pane " + (o.cls || ""); const p = { ...o, el: e, last: D.activeElement };
  p.draw = () => { const b = e.querySelector(".sm-p-b"), y = b ? b.scrollTop : 0; e.innerHTML = `<div class="sm-p" role="dialog" aria-modal="true" aria-label="${esc(typeof o.title === "function" ? "" : o.title || "")}"><header class="sm-p-h"><h3>${typeof o.title === "function" ? o.title() : esc(o.title || "")}</h3>${o.head ? o.head() : ""}<button type="button" class="sm-ic" data-do="paneClose" aria-label="اقفل">${ico("x")}</button></header><div class="sm-p-b">${o.html()}</div>${o.foot ? `<footer class="sm-p-f">${o.foot()}</footer>` : ""}</div>`; e.querySelector(".sm-p-b").scrollTop = y; if (o.after) o.after(e, p); };
  p.close = async force => { if (!force && o.dirty && o.dirty() && !await SB.sure("فيه تعديلات ماتحفظتش. تقفل من غير حفظ؟", "اقفل من غير حفظ")) return false; const i = panes.indexOf(p); if (i >= 0) panes.splice(i, 1); e.remove(); D.body.classList.toggle("sm-locked", panes.length > 0); if (p.last && p.last.isConnected) p.last.focus({ preventScroll: true }); if (o.closed) o.closed(); return true; };
  wire(e); e.addEventListener("pointerdown", ev => { if (ev.target === e) p.down = true; }); e.addEventListener("click", ev => { if (ev.target === e && p.down) p.close(); p.down = false; });
  panes.push(p); D.body.appendChild(e); D.body.classList.add("sm-locked"); p.draw(); const f = e.querySelector("[autofocus]") || e.querySelector(".sm-p-h .sm-ic"); if (f) f.focus({ preventScroll: true }); return p;
};
D.addEventListener("keydown", e => { if (e.key === "Escape" && panes.length && !D.querySelector(".sm-ask")) { e.stopPropagation(); top().close(); } });

/* ---------- presses: data-do / data-ch / data-in, in the top panel, then the view, then here ---------- */
const G = { acts: {}, ch: {}, inp: {} }; SB.acts = G.acts;
const find = (kind, name, el) => { const p = el.closest(".sm-pane") ? panes.find(x => x.el === el.closest(".sm-pane")) : null; return (p && p[kind] && p[kind][name]) || (V[view] && V[view][kind] && V[view][kind][name]) || G[kind][name]; };
function wire(e) {
  e.addEventListener("click", ev => { const b = ev.target.closest("[data-do]"); if (!b || b.disabled || !e.contains(b)) return; const [n, ...a] = b.dataset.do.split(":"), f = find("acts", n, b); if (f) { if (b.tagName === "A" && !b.hasAttribute("target")) ev.preventDefault(); f(...a, b, ev); } });
  e.addEventListener("change", ev => { const b = ev.target.closest("[data-ch]"); if (!b) return; const [n, ...a] = b.dataset.ch.split(":"), f = find("ch", n, b); if (f) f(b.type === "checkbox" ? b.checked : b.value, ...a, b); });
  e.addEventListener("input", ev => { const b = ev.target.closest("[data-in]"); if (!b) return; const [n, ...a] = b.dataset.in.split(":"), f = find("inp", n, b); if (f) f(b.value, ...a, b); });
  e.addEventListener("submit", ev => { const f = ev.target.closest("form[data-sub]"); if (!f) return; ev.preventDefault(); const h = find("acts", f.dataset.sub, f); if (h) h(f); });
  e.addEventListener("keydown", ev => { const b = ev.target.closest("[data-enter]"); if (b && ev.key === "Enter" && !ev.isComposing) { ev.preventDefault(); const [n, ...a] = b.dataset.enter.split(":"), f = find("acts", n, b); if (f) f(...a, b); } });
}
G.acts.paneClose = () => top() && top().close();
G.acts.tab = (v, a) => SB.show(v, typeof a === "string" ? a : null);
G.acts.copy = t => SB.copy(decodeURIComponent(t), "اتنسخ");
G.acts.ext = t => X.go(t);
G.acts.qr = (u, name) => { const t = decodeURIComponent(u), n = typeof name === "string" ? decodeURIComponent(name) : st.name, c = SB.qrCanvas(t, n, 900); if (!c) return say("اللينك طويل على الـ QR.", true); const src = c.toDataURL("image/png"); SB.pane({ title: "QR", cls: "small", html: () => `<div class="sm-qrb"><img src="${src}" alt="QR لـ ${esc(n)}"><p class="sm-hint">اطبعه على الكيس أو الكارت. الزبون يصوّره بالموبايل يفتحله ${esc(n)}.</p></div>`, foot: () => '<button type="button" class="btn" data-do="dl">نزّله صورة</button>', acts: { dl: () => { const a = D.createElement("a"); a.href = src; a.download = "qr.png"; a.click(); } } }); };
const limits = () => { const l = st.limits, c = st.counts, o = []; if (c.products >= l.products * .9) o.push(`${fmt(c.products)} منتج من ${fmt(l.products)}`); if (st.cats.length >= l.cats * .9) o.push(`${fmt(st.cats.length)} قسم من ${fmt(l.cats)}`); if (l.mb >= l.maxMb * .9) o.push(`${fmt(l.mb)} ميجا صور من ${fmt(l.maxMb)}`); return o.length ? `<div class="sm-rc y"><span>📏</span><div><b>متجرك قرب من الحد</b><small>${o.join("، ")}. امسح اللي مش محتاجه أو كلّمنا.</small></div></div>` : ""; };
/* dragging rows to order them: SB.sort(listElement, ".row", done(ids)) — works with the mouse, the finger and the arrow keys */
SB.sort = (box, sel, done) => {
  if (!box) return; const ids = () => [...box.querySelectorAll(":scope > " + sel)].map(x => x.dataset.id);
  box.querySelectorAll(":scope > " + sel).forEach(r => {
    const g = r.querySelector(".sm-grip"); if (!g) return;
    g.addEventListener("keydown", e => { if (e.key !== "ArrowUp" && e.key !== "ArrowDown") return; e.preventDefault(); const o = e.key === "ArrowUp" ? r.previousElementSibling : r.nextElementSibling; if (!o || !o.matches(sel)) return; if (e.key === "ArrowUp") box.insertBefore(r, o); else box.insertBefore(o, r); g.focus(); done(ids()); });
    g.addEventListener("pointerdown", e => {
      if (e.button > 0) return; e.preventDefault(); g.setPointerCapture(e.pointerId); r.classList.add("drag"); const before = ids().join();
      const mv = ev => { const y = ev.clientY; const l = [...box.querySelectorAll(":scope > " + sel)].filter(x => x !== r); const o = l.find(x => { const b = x.getBoundingClientRect(); return y < b.top + b.height / 2; }); if (o) { if (o.previousElementSibling !== r) box.insertBefore(r, o); } else if (l.length) { const z = l[l.length - 1]; if (z.nextElementSibling !== r) z.after(r); } const sc = box.closest(".sm-p-b"); if (sc) { const q = sc.getBoundingClientRect(); if (y < q.top + 50) sc.scrollTop -= 12; else if (y > q.bottom - 50) sc.scrollTop += 12; } };
      const up = () => { r.classList.remove("drag"); g.removeEventListener("pointermove", mv); if (ids().join() !== before) done(ids()); };
      g.addEventListener("pointermove", mv); g.addEventListener("pointerup", up, { once: true }); g.addEventListener("pointercancel", up, { once: true });
    });
  });
};

/* ---------- the frame ---------- */
const TABS = [["home", "🏪", "المتجر"], ["orders", "🧾", "الأوردرات"], ["products", "📦", "المنتجات"], ["cats", "🗂️", "الأقسام"], ["offers", "🏷️", "العروض"], ["look", "🎨", "الشكل"], ["ship", "🚚", "الشحن"], ["pages", "📄", "الصفحات"], ["info", "⚙️", "البيانات"], ["bot", "🤖", "البوت عارف إيه؟"]];
const PILL = { draft: ["لسه ماتنشرش", "g"], review: ["الإدارة بتراجعه", "y"], live: ["شغال", "ok"], closed: ["مقفول من الإدارة", "bad"] };
function head() {
  if (!root || !st) return; const h = root.querySelector(".sm-head"); if (!h) return;
  const p = st.status === "live" && !st.open ? ["موقوف: الإيجار خلص", "bad"] : PILL[st.status] || PILL.draft, l = st.draft && st.draft.logo;
  h.innerHTML = `<div class="sm-id"><span class="sm-av">${l ? `<img src="${esc(SB.src(l))}" alt="">` : esc((st.name || "م").trim().charAt(0))}</span><div><h2>${esc(st.name)} <span class="sm-pill ${p[1]}">${p[0]}</span></h2><p class="sm-url"><a href="${esc(st.url)}" target="_blank" rel="noopener" dir="ltr">${esc(st.url.replace(/^https?:\/\//, ""))}</a><button type="button" class="sm-ic sm" data-do="copy:${encodeURIComponent(st.url)}" aria-label="انسخ لينك المتجر">${ico("copy")}</button><button type="button" class="sm-lk" data-do="qr:${encodeURIComponent(st.url)}">QR</button></p></div></div>
    <div class="sm-hb"><button type="button" class="btn ghost" data-do="preview">${ico("eye")} معاينة خاصة</button>${st.status === "live" ? (st.dirty ? `<button type="button" class="btn" data-do="publish">انشر التعديلات</button>` : `<span class="sm-ok">${ico("check")} كل حاجة منشورة</span>`) : st.status === "review" ? `<button type="button" class="btn ghost" data-do="unsubmit">رجّعه للتعديل</button>` : st.status === "closed" ? "" : `<button type="button" class="btn" data-do="submit">ابعته للمراجعة</button>`}</div>`;
  const n = st.counts.newOrders; root.querySelectorAll(".sm-nav [data-v]").forEach(b => { b.classList.toggle("on", b.dataset.v === view); b.setAttribute("aria-current", b.dataset.v === view ? "page" : "false"); const c = b.querySelector(".nb"); if (c) { c.hidden = !n; c.textContent = fmt(n); } });
}
SB.headDraw = head;
SB.show = async (v, a) => {
  if (!V[v]) v = "home"; if (V[view] && V[view].leave && view !== v && await V[view].leave() === false) return;
  view = v; arg = a; try { sessionStorage.setItem("sm-view", v); } catch (e) { } head(); const body = root.querySelector(".sm-body"); body.dataset.v = v;
  const nv = root.querySelector(`.sm-nav [data-v="${v}"]`); if (nv && nv.scrollIntoView) nv.scrollIntoView({ block: "nearest", inline: "center" });
  body.innerHTML = '<div class="sm-load" role="status"><i></i> ثانية واحدة...</div>';
  try { await V[v].open(body, a); } catch (e) { if (view === v) body.innerHTML = `<div class="card sm-empty"><p>${esc(e.message)}</p><button type="button" class="btn ghost" data-do="tab:${v}">جرّب تاني</button></div>`; }
};
SB.body = () => root.querySelector(".sm-body"); SB.view = () => view;
SB.open = async (el, ctx) => {
  X = ctx; root = el; SB.x = ctx;
  if (!opened) { opened = true; el.innerHTML = '<div class="sm"><header class="sm-head"></header><nav class="sm-nav" aria-label="أقسام متجري">' + TABS.map(t => `<button type="button" data-v="${t[0]}" data-do="tab:${t[0]}"><span aria-hidden="true">${t[1]}</span> ${t[2]}${t[0] === "orders" ? ' <span class="nb" hidden></span>' : ""}</button>`).join("") + '</nav><div class="sm-body"></div></div>'; wire(el); }
  try { if (!st) el.querySelector(".sm-body").innerHTML = '<div class="sm-load" role="status"><i></i> بنفتح متجرك...</div>'; setState(await SB.get("state")); }
  catch (e) { el.querySelector(".sm-body").innerHTML = `<div class="card sm-empty"><p>${esc(e.message)}</p></div>`; opened = false; return; }
  if (!st.biz) { SB.wizard(); }
  let v = "home"; try { v = sessionStorage.getItem("sm-view") || "home"; } catch (e) { } SB.show(ctx.view || v, ctx.arg);
};

/* ---------- preview, publish ---------- */
const FRAME = { el: null };
G.acts.preview = () => {
  let url = "", live = "", w = "m", pub = false;
  const p = SB.pane({ title: "معاينة خاصة", cls: "wide pv", html: () => `<div class="sm-pv"><div class="row sm-pv-t">${st.everLive ? SB.seg("pvP", [["0", "المسودة"], ["1", "المنشور"]], pub ? "1" : "0") : ""}<span class="sm-hint">${pub ? "ده اللي الزباين شايفينه دلوقتي." : "إنت بس اللي شايف النسخة دي."}</span><span class="space"></span>${SB.seg("pvW", [["m", ico("phone") + " موبايل"], ["t", "تابلت"], ["d", ico("desk") + " كمبيوتر"]], w)}<button type="button" class="btn ghost sm" data-do="pvShare">${ico("copy")} لينك لحد تاني</button><a class="btn ghost sm" ${url ? `href="${esc(url)}"` : ""} target="_blank" rel="noopener">${ico("out")} صفحة لوحدها</a></div><div class="sm-pv-f ${w}">${url ? `<iframe src="${esc(url)}?f=1${pub ? "&live=1" : ""}" title="معاينة المتجر"></iframe>` : '<div class="sm-load"><i></i></div>'}</div></div>`,
    acts: { pvP: v => { pub = v === "1"; p.draw(); }, pvW: v => { w = v; const f = p.el.querySelector(".sm-pv-f"); f.className = "sm-pv-f " + v; p.el.querySelectorAll(".sm-seg button").forEach(b => b.classList.toggle("on", b.dataset.do === "pvW:" + v)); }, pvShare: b => run(b, async () => { const r = await post("preview", { share: true }); await SB.copy(r.url, "اتنسخ. اللينك شغال 24 ساعة."); }) } });
  post("preview", {}).then(r => { url = r.url; live = r.live; p.draw(); }).catch(e => { say(e.message, true); p.close(true); });
};
G.acts.publish = async () => {
  const list = (await run(null, () => post("diff", {}))) || { list: [] }; let note = "";
  SB.pane({ title: "انشر التعديلات", cls: "small", html: () => `<p>دي الحاجات اللي هتتغيّر قدام الزباين:</p><ul class="sm-ul">${list.list.map(x => `<li>${esc(x)}</li>`).join("") || "<li>تعديلات بسيطة</li>"}</ul>${fld("note", "اسم للنسخة (اختياري)", "", { max: 60, ph: "مثلًا: شكل رمضان" })}<p class="sm-hint">المنتجات والأسعار والعروض بتنزل لوحدها أول ما تحفظها. ده نشر الشكل بس، وتقدر ترجع لأي نسخة قديمة.</p>`, foot: () => `<button type="button" class="btn" data-do="pubGo">انشر دلوقتي</button><button type="button" class="btn ghost" data-do="paneClose">لسه</button>`,
    acts: { pubGo: b => run(b, async () => { note = top().el.querySelector('[data-k="note"]').value; await post("publish", { note }); top().close(true); say("اتنشر ✅"); if (view === "look" || view === "home") SB.show(view); }) } });
};
G.acts.submit = async () => {
  const left = st.steps.filter(x => x.must && !x.done);
  if (left.length) { SB.pane({ title: "فاضل كام خطوة", cls: "small", html: () => `<p>قبل ما الإدارة تراجع متجرك، كمّل دول:</p><div class="sm-steps-l">${left.map(stepRow).join("")}</div>`, acts: { step: t => { top().close(true); goStep(t); } } }); return; }
  if (!await SB.sure("هنراجع متجرك ونفتحه للزباين في أقرب وقت. تبعته دلوقتي؟", "ابعته", { danger: false, no: "لسه" })) return;
  await run(null, async () => { await post("submit", {}); say("اتبعت ✅ هنراجعه ونرد عليك."); SB.show("home"); });
};
G.acts.unsubmit = () => run(null, async () => { await post("unsubmit", {}); say("رجع للتعديل."); SB.show("home"); });
const stepRow = x => `<button type="button" class="sm-step ${x.done ? "ok" : ""}" data-do="step:${x.tab}"><i>${x.done ? ico("check") : ""}</i><span>${esc(x.t)}</span>${x.must && !x.done ? "<em>مطلوب</em>" : ""}${x.done ? "" : ico("next")}</button>`;
const goStep = t => { if (t === "plan") return G.acts.rent(); if (t === "wa") return X.go("wa"); SB.show(t === "look" ? "look" : t); };
G.acts.step = goStep;

/* ---------- the rent: a month at a time, paid apart from the plan ---------- */
G.acts.rent = async () => {
  const s = st.service; if (s.inPlan || s.admin) return say("المتجر جوه باقتك.");
  if (s.pending) return say("طلبك مستني موافقة الإدارة."); if (!(s.price > 0)) return say("سعر المتجر لسه ماتحددش. كلّمنا.", true);
  let pay; try { pay = await ask("api/plans.ashx"); } catch (e) { return say(e.message, true); }
  const p = SB.pane({ title: "إيجار المتجر", cls: "small", html: () => `<div class="sm-rent"><p>${s.on ? `متجرك شغال لحد <b>${day(s.until)}</b>. الشهر الجديد بيبدأ بعده، ومش هتخسر ولا يوم.` : "شهر كامل من يوم الموافقة: متجرك باسمه ولينكه، وأوردراته في لوحتك."}</p><div class="sm-amt"><span>المطلوب</span><b>${mny(s.price)}</b><small>في الشهر</small></div>${pay.payNumber ? `<div class="sm-payto"><span>حوّل على الرقم</span><b dir="ltr">${esc(pay.payNumber)}</b><button type="button" class="btn ghost sm" data-do="copy:${encodeURIComponent(pay.payNumber)}">نسخ</button></div>` : ""}${pay.paymentInfo ? `<p class="sm-hint">${esc(pay.paymentInfo)}</p>` : ""}${pay.payQr ? `<figure class="sm-qr"><img src="api/plans.ashx?payqr=1&v=${encodeURIComponent(pay.payQr)}" alt="QR InstaPay"><figcaption>امسحه من تطبيق InstaPay</figcaption></figure>` : ""}<div class="sm-f"><label for="rMeth">دفعت بإيه؟</label><select id="rMeth"><option>InstaPay</option><option>أورانج كاش</option><option>كاش</option></select></div><div class="sm-f"><label for="rNo">الرقم المرجعي اللي ظهرلك بعد التحويل</label><input id="rNo" type="text" maxlength="40" dir="ltr"></div><div class="sm-f"><label for="rFrom">الرقم أو الحساب اللي حوّلت منه</label><input id="rFrom" type="text" maxlength="60" dir="ltr"></div></div>`, foot: () => `<button type="button" class="btn" data-do="rentGo">ابعت الطلب</button>`,
    acts: { rentGo: b => run(b, async () => { const q = k => p.el.querySelector(k).value.trim(), no = q("#rNo").replace(/[\s-]/g, ""); if (no.length < 6) throw new Error("اكتب الرقم المرجعي (من 6 لـ 30 حرف أو رقم)."); if (q("#rFrom").length < 3) throw new Error("اكتب الرقم أو الحساب اللي حوّلت منه."); await ask("api/plans.ashx?action=request", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kind: "shop", method: q("#rMeth"), ref: q("#rFrom"), refNo: no }) }); p.close(true); say("اتبعت ✅ هنفعّل متجرك أول ما نتأكد من الدفع."); await SB.reload(); if (view === "home") SB.show("home"); }) } });
};
const rentCard = () => {
  const s = st.service; if (s.admin) return ""; const b = t => `<button type="button" class="btn sm" data-do="rent">${t}</button>`;
  if (s.inPlan) return `<div class="sm-rc ok"><span>${ico("check")}</span><div><b>المتجر جوه باقتك</b><small>شغال طول ما باقتك شغالة.</small></div></div>`;
  if (s.pending) return `<div class="sm-rc y"><span>⏳</span><div><b>طلب الإيجار مستني موافقة الإدارة</b><small>بنراجع التحويل ونفعّله.</small></div></div>`;
  if (s.on && s.grace) return `<div class="sm-rc bad"><span>⚠️</span><div><b>إيجار المتجر خلص</b><small>المتجر لسه فاتح ${plural(Math.max(1, s.graceDays + s.days), "يوم واحد", "يومين", "أيام", "يوم")}، وبعدها بيقفل قدام الزباين لحد ما تجدّد.</small></div>${b("جدّد دلوقتي")}</div>`;
  if (s.on) return `<div class="sm-rc ${s.days <= 7 ? "y" : "ok"}"><span>${s.days <= 7 ? "⏰" : ico("check")}</span><div><b>إيجار المتجر شغال لحد ${day(s.until)}</b><small>باقي ${plural(Math.max(0, s.days), "يوم واحد", "يومين", "أيام", "يوم")}.</small></div>${s.days <= 7 ? b("جدّد") : ""}</div>`;
  if (s.until) return `<div class="sm-rc bad"><span>🔒</span><div><b>المتجر مقفول قدام الزباين</b><small>الإيجار خلص ${day(s.until)}. كل منتجاتك وأوردراتك محفوظة ${fmt(s.keepDays)} يوم من يوم ما خلص.</small></div>${b("رجّعه شغال")}</div>`;
  return `<div class="sm-rc g"><span>🔑</span><div><b>${s.price > 0 ? "فعّل إيجار المتجر: " + mny(s.price) + " في الشهر" : "إيجار المتجر"}</b><small>${s.price > 0 ? "تقدر تجهّز متجرك براحتك الأول، وتدفع لما تبقى جاهز تفتحه." : "السعر لسه ماتحددش. كلّمنا."}</small></div>${s.price > 0 ? b("فعّله") : ""}</div>`;
};

/* ---------- the first time: what the store sells, its name and link ---------- */
const BIZ = { clothes: ["👗", "لبس وشنط"], home: ["🍳", "أدوات منزلية"], beauty: ["🧴", "بشرة وتجميل"], spices: ["🌿", "عطارة"], electronics: ["🎧", "إلكترونيات"], toys: ["🧸", "ألعاب أطفال"], sweets: ["🍫", "حلويات"], plants: ["🪴", "نباتات"], furniture: ["🛋️", "أثاث وديكور"], jewelry: ["💍", "ساعات ومجوهرات"], grocery: ["🥬", "بقالة وخضار"], other: ["🏪", "حاجة تانية"] };
SB.BIZ = BIZ;
SB.wizard = () => {
  let biz = "", name = st.name, slug = st.slug, err = "";
  const p = SB.pane({ title: "أهلًا بيك في متجرك الجديد", cls: "wide wz", dirty: () => false, html: () => `<div class="sm-wz"><h4>بتبيع إيه؟</h4><div class="sm-bz">${st.bizzes.map(k => `<button type="button" class="${biz === k ? "on" : ""}" data-do="wzB:${k}"><span aria-hidden="true">${BIZ[k][0]}</span>${BIZ[k][1]}</button>`).join("")}</div><h4>اسم المتجر ولينكه</h4><div class="sm-2">${fld("name", "اسم المتجر", name, { max: 60, must: true, x: 'data-in="wzN"' })}<div class="sm-f"><label for="wzS">لينك المتجر</label><div class="sm-slug" dir="ltr"><input id="wzS" type="text" value="${esc(slug)}" maxlength="30" spellcheck="false" autocapitalize="off" data-in="wzS"><span>.${esc(st.domain)}</span></div><small class="sm-hint">حروف إنجليزي وأرقام وشرطة، من 3 لـ 30.</small></div></div><p class="msg err">${esc(err)}</p><p class="sm-hint">هنعملك أقسام جاهزة تقدر تعدّلها، وبعدها تختار شكل متجرك من القوالب. كله بيتغيّر بعدين.</p></div>`, foot: () => `<button type="button" class="btn" data-do="wzGo" ${biz ? "" : "disabled"}>يلا نبدأ</button>`,
    acts: { wzB: k => { biz = k; p.draw(); }, wzGo: b => run(b, async () => { try { await post("info", { name, slug, biz }); } catch (e) { err = e.message; p.draw(); throw e; } await post("template", { t: st.templates.includes(TPL[biz]) ? TPL[biz] : st.templates[0], biz }); if (!st.cats.length) try { await post("cat-ready", { biz }); } catch (e) { } await SB.reload(); p.close(true); SB.show("home"); say("متجرك اتعمل ✅"); if (st.templates.length > 1) SB.tplPick(true); }) },
    inp: { wzN: v => { name = v; }, wzS: v => { slug = v.toLowerCase().replace(/[^a-z0-9-]/g, ""); } } });
};
const TPL = { clothes: "atelier", electronics: "volt", home: "volt", grocery: "taza", spices: "taza", beauty: "nada", furniture: "rokn", plants: "shatla", toys: "farfoush", sweets: "elba", jewelry: "bareeq" };

/* ================= home ================= */
let hSpan = "week", hData = null, hTable = false;
V.home = {
  async open(body) { hData = await SB.get("home"); if (view !== "home") return; V.home.draw(body); },
  draw(body) {
    const done = st.steps.filter(x => x.done).length, all = st.steps.length, must = st.steps.filter(x => x.must && !x.done), s = hData[hSpan], conv = s.visitors ? s.webOrders / s.visitors * 100 : 0;
    const showSteps = st.status !== "live" || must.length;
    body.innerHTML = `${st.reviewNote && st.status === "draft" ? `<div class="sm-rc bad"><span>📝</span><div><b>ملاحظات الإدارة على متجرك</b><small style="white-space:pre-line">${esc(st.reviewNote)}</small></div></div>` : ""}${st.status === "closed" ? `<div class="sm-rc bad"><span>🔒</span><div><b>متجرك مقفول من الإدارة</b><small>${esc(st.closeNote)}</small></div></div>` : ""}${st.status === "review" ? `<div class="sm-rc y"><span>⏳</span><div><b>الإدارة بتراجع متجرك</b><small>اتبعت ${SB.ago(st.submittedAt)}. تقدر تكمّل تعديل المنتجات عادي.</small></div></div>` : ""}${st.pendName || st.pendSlug ? `<div class="sm-rc y"><span>⏳</span><div><b>تغيير الاسم أو اللينك مستني موافقة الإدارة</b><small>${esc([st.pendName, st.pendSlug && st.pendSlug + "." + st.domain].filter(Boolean).join("، "))}</small></div></div>` : ""}
      ${rentCard()}${limits()}
      ${showSteps ? `<section class="card sm-steps"><div class="row"><h3>خطوات تجهيز المتجر</h3><span class="space"></span><b>${fmt(done)} من ${fmt(all)}</b></div><div class="sm-bar" role="progressbar" aria-valuenow="${done}" aria-valuemin="0" aria-valuemax="${all}"><i style="width:${Math.round(done / all * 100)}%"></i></div><div class="sm-steps-l">${st.steps.map(stepRow).join("")}</div>${!must.length && st.status === "draft" ? `<button type="button" class="btn" data-do="submit">خلصت المطلوب: ابعته للمراجعة</button>` : ""}</section>` : ""}
      ${st.counts.newOrders ? `<button type="button" class="sm-rc y sm-go" data-do="tab:orders:todo"><span>🧾</span><div><b>${plural(st.counts.newOrders, "أوردر واحد مستنيك", "أوردرين مستنيينك", "أوردرات مستنياك", "أوردر مستنيك")}</b><small>راجعهم وأكّدهم.</small></div>${ico("next")}</button>` : ""}
      <section class="card"><div class="row"><h3>أرقام المتجر</h3><span class="space"></span>${SB.seg("hSpan", [["today", "النهارده"], ["week", "7 أيام"], ["month", "30 يوم"]], hSpan)}</div>
        <div class="sm-nums"><div><small>الأوردرات</small><b>${fmt(s.orders)}</b>${s.cancelled || s.returned ? `<i>${[s.cancelled && fmt(s.cancelled) + " ملغي", s.returned && fmt(s.returned) + " مرتجع"].filter(Boolean).join("، ")}</i>` : ""}</div><div><small>المبيعات</small><b>${mny(s.sales)}</b><i>من غير الملغي والمرتجع</i></div><div><small>الزوار</small><b>${fmt(s.visitors)}</b><i>${fmt(s.visits)} زيارة</i></div><div><small>من كل 100 زائر</small><b>${conv ? conv.toLocaleString("ar-EG", { maximumFractionDigits: 1 }) : fmt(0)}</b><i>طلبوا</i></div></div>
        ${s.visitors ? `<ol class="sm-fun">${[["زاروا المتجر", s.visitors], ["شافوا منتج", s.views], ["حطّوا في السلة", s.carts], ["بدأوا الطلب", s.checkouts], ["طلبوا", s.webOrders]].map(x => `<li><span>${x[0]}</span><i><u style="width:${Math.max(1, Math.round(Math.min(1, x[1] / s.visitors) * 100))}%"></u></i><b>${fmt(x[1])}</b></li>`).join("")}</ol>` : `<p class="sm-hint">الأرقام بتبدأ من أول زيارة. ابعت لينك متجرك لحد وشوف.</p>`}</section>
      ${hData.days.length > 1 ? `<section class="card"><div class="row"><h3>آخر 30 يوم</h3><span class="space"></span><button type="button" class="btn ghost sm" data-do="hTable">${hTable ? "رسم" : "جدول"}</button></div>${hTable ? table() : `<div class="sm-2 sm-ch2">${chart("الزيارات في اليوم", 1)}${chart("الأوردرات في اليوم", 2)}</div>`}</section>` : ""}
      <div class="sm-2">${hData.top.length ? `<section class="card"><h3>الأكتر مبيعًا في 30 يوم</h3><ol class="sm-rank">${hData.top.map(x => `<li><button type="button" data-do="openProd:${x.id}"><span>${esc(x.name)}</span><b>${fmt(x.n)} قطعة</b><i>${mny(x.sum)}</i></button></li>`).join("")}</ol></section>` : ""}${hData.low.length ? `<section class="card"><h3>قرّب يخلص</h3><ol class="sm-rank">${hData.low.map(x => `<li><button type="button" data-do="openProd:${x.id}"><span>${esc(x.name)}</span><b class="${x.stock ? "" : "bad"}">${x.stock ? "باقي " + fmt(x.stock) : "خلص"}</b></button></li>`).join("")}</ol></section>` : ""}</div>`;
    const tip = body.querySelectorAll(".sm-ch"); tip.forEach(hover);
  },
  acts: { hSpan: v => { hSpan = v; V.home.draw(SB.body()); }, hTable: () => { hTable = !hTable; V.home.draw(SB.body()); }, openProd: id => { SB.show("products").then(() => SB.editProduct(+id)); } }
};
/* every day of the last 30, also the ones with nothing */
const days30 = () => { const m = new Map(hData.days.map(x => [x[0], x])); const out = [], now = new Date(Date.now() + 2 * 36e5); for (let i = 29; i >= 0; i--) { const d = new Date(now - i * 864e5).toISOString().slice(0, 10); out.push(m.get(d) || [d, 0, 0]); } return out; };
const nice = m => { if (m <= 4) return 4; const p = Math.pow(10, Math.floor(Math.log10(m))); for (const k of [1, 2, 2.5, 5, 10]) if (k * p >= m && (k * p) % 2 === 0) return k * p; return Math.ceil(m / p) * p; };
const chart = (t, ix) => {
  const l = days30(), max = nice(Math.max(...l.map(x => x[ix]))), w = 420, h = 150, L = 34, B = 22, bw = (w - L) / l.length, y = v => 8 + (h - B - 8) * (1 - v / max);
  return `<figure class="sm-ch" data-ix="${ix}"><figcaption>${t}</figcaption><svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${t}، 30 يوم" direction="ltr">${[0, .5, 1].map(k => `<line x1="${L}" x2="${w}" y1="${y(max * k)}" y2="${y(max * k)}" class="gr"/><text x="${L - 6}" y="${y(max * k) + 4}" text-anchor="end">${fmt(max * k)}</text>`).join("")}${l.map((x, i) => { const v = x[ix], X0 = L + i * bw + 1, yy = y(v), hh = h - B - yy, r = Math.min(3, hh); return v ? `<path class="br" data-i="${i}" d="M${X0} ${h - B}V${yy + r}q0-${r} ${r}-${r}H${X0 + bw - 2 - r}q${r} 0 ${r} ${r}V${h - B}Z"/>` : ""; }).join("")}${[0, 10, 20, 29].map(i => `<text x="${L + i * bw + bw / 2}" y="${h - 6}" text-anchor="${i === 29 ? "end" : "middle"}">${new Date(l[i][0] + "T12:00").toLocaleDateString("ar-EG", { day: "numeric", month: "short" })}</text>`).join("")}<line class="cur" x1="0" x2="0" y1="8" y2="${h - B}" visibility="hidden"/></svg><div class="sm-tip" hidden></div></figure>`;
};
const hover = f => {
  const svg = f.querySelector("svg"), tip = f.querySelector(".sm-tip"), cur = f.querySelector(".cur"), l = days30(), ix = +f.dataset.ix, bw = (420 - 34) / l.length; svg.tabIndex = 0; let at = -1;
  const show = i => { at = i; const x = l[i], px = 34 + i * bw + bw / 2, k = svg.getBoundingClientRect().width / 420; cur.setAttribute("x1", px); cur.setAttribute("x2", px); cur.setAttribute("visibility", "visible"); tip.hidden = false; tip.innerHTML = `<b>${fmt(x[ix])}</b> ${day(x[0] + "T12:00")}`; tip.style.left = Math.max(46, Math.min(svg.getBoundingClientRect().width - 46, px * k)) + "px"; f.querySelectorAll(".br").forEach(b => b.classList.toggle("hi", +b.dataset.i === i)); };
  const hide = () => { tip.hidden = true; cur.setAttribute("visibility", "hidden"); f.querySelectorAll(".br.hi").forEach(b => b.classList.remove("hi")); };
  svg.addEventListener("pointermove", e => { const r = svg.getBoundingClientRect(), i = Math.floor(((e.clientX - r.left) / r.width * 420 - 34) / bw); if (i >= 0 && i < l.length) show(i); else hide(); }); svg.addEventListener("pointerleave", hide); svg.addEventListener("blur", hide);
  svg.addEventListener("keydown", e => { if (e.key === "ArrowRight" || e.key === "ArrowLeft") { e.preventDefault(); show(Math.max(0, Math.min(l.length - 1, (at < 0 ? l.length - 1 : at) + (e.key === "ArrowRight" ? 1 : -1)))); } });
};
const table = () => `<div class="sm-tw"><table class="sm-t"><thead><tr><th>اليوم</th><th>الزيارات</th><th>الأوردرات</th></tr></thead><tbody>${days30().reverse().map(x => `<tr><td>${day(x[0] + "T12:00")}</td><td>${fmt(x[1])}</td><td>${fmt(x[2])}</td></tr>`).join("")}</tbody></table></div>`;

/* ================= info: name, link, contact, the seller, orders ================= */
const saveBar = (dirty, act) => `<div class="sm-save ${dirty ? "on" : ""}"><span>فيه تعديلات ماتحفظتش</span><button type="button" class="btn" data-do="${act}">احفظ</button></div>`;
let infoDirty = false;
const mark = () => { infoDirty = true; const s = SB.body().querySelector(".sm-save"); if (s) s.classList.add("on"); };
const leave = async () => { if (!infoDirty) return true; if (!await SB.sure("فيه تعديلات ماتحفظتش. تسيبها؟", "سيبها")) return false; infoDirty = false; return true; };
V.info = {
  async open(body) {
    infoDirty = false; const s = st.settings, c = s.contact || {}, so = s.social || {}, lg = s.legal || {}, o = s.order || {}, va = s.vacation || {};
    body.innerHTML = `<form class="sm-form" data-sub="infoSave" data-form>
      <section class="card"><h3>اسم المتجر ولينكه</h3><p class="sub">${st.status === "live" ? "متجرك شغال، فتغيير الاسم أو اللينك بيتراجع من الإدارة الأول. اللينك القديم بيفضل يوصّل للجديد." : "اختار اسم سهل يتكتب ويتقال."}</p><div class="sm-2">${fld("i.name", "اسم المتجر", st.pendName || st.name, { max: 60, must: true })}${fld("i.nameEn", "الاسم بالإنجليزي", st.nameEn, { max: 60, dir: "ltr" })}</div><div class="sm-2"><div class="sm-f"><label for="iSlug">لينك المتجر</label><div class="sm-slug" dir="ltr"><input id="iSlug" type="text" data-k="i.slug" value="${esc(st.pendSlug || st.slug)}" maxlength="30" spellcheck="false" autocapitalize="off"><span>.${esc(st.domain)}</span></div></div><div class="sm-f"><label>النشاط</label>${SB.sel("i.biz", st.bizzes.map(k => [k, BIZ[k][1]]), st.biz)}</div></div></section>
      <section class="card"><h3>التواصل</h3><div class="sm-2">${fld("contact.phone", "رقم الموبايل", c.phone, { mode: "tel", dir: "ltr", ph: "01xxxxxxxxx" })}${fld("contact.wa", "رقم الواتساب", c.wa, { mode: "tel", dir: "ltr", ph: "01xxxxxxxxx" })}</div><div class="sm-2">${fld("contact.email", "الإيميل", c.email, { dir: "ltr", max: 100 })}${fld("contact.hours", "مواعيد الشغل", c.hours, { max: 100, ph: "من 10 الصبح لـ 10 بالليل" })}</div>${fld("contact.address", "العنوان", c.address, { max: 200 })}<h4>صفحات السوشيال</h4><div class="sm-2">${fld("social.fb", "فيسبوك", so.fb, { dir: "ltr", ph: "facebook.com/..." })}${fld("social.ig", "إنستجرام", so.ig, { dir: "ltr", ph: "@..." })}</div><div class="sm-2">${fld("social.tt", "تيك توك", so.tt, { dir: "ltr", ph: "@..." })}${fld("social.yt", "يوتيوب", so.yt, { dir: "ltr" })}</div></section>
      <section class="card"><h3>بيانات البايع</h3><p class="sub">القانون بيطلب إن الزبون يعرف بيشتري من مين. بتظهر في آخر صفحة المتجر.</p><div class="sm-2">${fld("legal.seller", "اسم البايع أو الشركة", lg.seller, { max: 120, must: true })}${fld("legal.phone", "رقم البايع", lg.phone, { mode: "tel", dir: "ltr", must: true })}</div>${fld("legal.address", "عنوان البايع", lg.address, { max: 200, must: true })}<div class="sm-2">${fld("legal.reg", "رقم السجل التجاري", lg.reg, { max: 40, dir: "ltr", hint: "لو عندك" })}${fld("legal.tax", "الرقم الضريبي", lg.tax, { max: 40, dir: "ltr", hint: "لو عندك" })}</div></section>
      <section class="card"><h3>الطلبات</h3><div class="sm-2">${fld("order.min", "أقل قيمة للطلب", o.min || "", { mode: "numeric", suf: "ج", hint: "فاضي = مفيش حد أدنى", x: "data-n" })}${fld("order.maxQty", "أقصى كمية من المنتج في الطلب", o.maxQty || 20, { mode: "numeric", x: "data-n" })}</div>${SB.tog("order.phone2", o.phone2 !== false, "اسأل عن رقم تاني", "رقم احتياطي للمندوب، مش إجباري")}${SB.tog("order.area", o.area !== false, "اسأل عن المنطقة والعلامة المميزة")}${SB.tog("order.waBtn", o.waBtn !== false, "زرار «كمّل على واتساب»", "للزبون اللي بيحب يطلب على واتساب")}${SB.tog("order.confirm", o.confirm !== false, "رسالة تأكيد على واتساب", st.wa.linked ? "الزبون بيرد 1 يأكد أو 2 يلغي، والأوردر بيتغيّر لوحده" : "محتاجة رقم واتساب مربوط من «ربط بحساباتك»")}${SB.tog("alerts.wa", (s.alerts || {}).wa !== false, "ابعتلي رسالة واتساب مع كل أوردر")}</section>
      <section class="card"><h3>الاسترجاع</h3><div class="sm-2">${fld("returnsDays", "الزبون يقدر يرجّع خلال كام يوم", s.returnsDays || 14, { mode: "numeric", x: "data-n", hint: "القانون: 14 يوم على الأقل" })}</div></section>
      <section class="card"><h3>إجازة</h3>${SB.tog("vacation.on", va.on, "المتجر في إجازة", "الزبون بيشوف المنتجات ومايقدرش يطلب")}<div class="sm-2">${fld("vacation.until", "لحد يوم", va.until, { type: "date" })}</div>${SB.two("vacation.msg", "رسالة للزباين", va.msg, { max: 200, ph: "راجعين يوم السبت" })}</section>
      <section class="card"><h3>قياس إعلانات فيسبوك</h3>${fld("pixel.meta", "رقم الـ Pixel", (s.pixel || {}).meta, { dir: "ltr", mode: "numeric", max: 20, hint: "من مدير الأحداث في فيسبوك. بيقيس المشاهدة والسلة والشراء، وبيظهر في صفحة الخصوصية." })}</section>
      ${saveBar(false, "infoSave")}</form>`;
    body.querySelector("[data-form]").addEventListener("input", mark);
  },
  leave, acts: { infoSave: b => run(b.tagName === "FORM" ? b.querySelector(".sm-save .btn") : b, async () => { const d = SB.read(SB.body().querySelector("[data-form]")), i = d.i; delete d.i; await post("info", i); await post("settings", { settings: d }); infoDirty = false; say("اتحفظ ✅"); SB.show("info"); }) }
};

/* ================= shipping ================= */
let geo = null, shipZ = {}, shipMode = "flat";
SB.geo = async () => geo || (geo = (await SB.get("geo")).map(g => ({ ...g, ar: g.n[0] })));
V.ship = {
  async open(body) {
    infoDirty = false; await SB.geo(); if (view !== "ship") return; const s = st.settings.ship || {}; shipMode = s.mode || "flat"; shipZ = {}; st.zones.forEach(z => { shipZ[z.g] = z; }); V.ship.draw(body);
  },
  draw(body) {
    const s = st.settings.ship || {};
    body.innerHTML = `<form class="sm-form" data-sub="shipSave" data-form>
      <section class="card"><h3>سعر الشحن</h3><p class="sub">الزبون بيشوف السعر والمدة لمحافظته قبل ما يأكد، والبوت بيقولهم كمان.</p>${SB.seg("shipMode", [["flat", "سعر واحد لكل مصر"], ["zones", "سعر لكل محافظة"]], shipMode)}<div class="sm-3">${fld("ship.fee", shipMode === "flat" ? "سعر الشحن" : "السعر العادي", s.fee, { mode: "decimal", suf: "ج", x: "data-n", hint: shipMode === "zones" ? "للمحافظات اللي تسيبها فاضية تحت" : "" })}${fld("ship.freeOver", "شحن مجاني فوق", s.freeOver || "", { mode: "decimal", suf: "ج", x: "data-n", hint: "فاضي = مفيش" })}${fld("ship.days", "مدة التوصيل", s.days, { max: 30, ph: "2-4", suf: "يوم" })}</div>${SB.two("ship.note", "ملاحظة عن التوصيل", s.note, { max: 200, ph: "مثلًا: بنوصّل من السبت للخميس" })}</section>
      ${shipMode === "zones" ? `<section class="card"><h3>المحافظات</h3><div class="sm-tw"><table class="sm-t sm-zones"><thead><tr><th>المحافظة</th><th>السعر</th><th>المدة (يوم)</th><th>أقل طلب</th><th>بنوصّلها</th></tr></thead><tbody data-skip>${geo.map(g => { const z = shipZ[g.id] || {}; return `<tr data-g="${g.id}" class="${z.off ? "off" : ""}"><td>${esc(g.ar)}</td><td><input type="text" inputmode="decimal" data-z="f" value="${z.f == null ? "" : z.f}" placeholder="${esc(s.fee)}" aria-label="سعر ${esc(g.ar)}"></td><td><input type="text" data-z="d" maxlength="20" value="${esc(z.d || "")}" placeholder="${esc(s.days || "")}" aria-label="مدة ${esc(g.ar)}"></td><td><input type="text" inputmode="decimal" data-z="m" value="${z.m == null ? "" : z.m}" aria-label="أقل طلب ${esc(g.ar)}"></td><td><input type="checkbox" data-z="on" ${z.off ? "" : "checked"} aria-label="بنوصّل ${esc(g.ar)}"></td></tr>`; }).join("")}</tbody></table></div></section>` : ""}
      ${saveBar(infoDirty, "shipSave")}</form>`;
    const f = body.querySelector("[data-form]"); f.addEventListener("input", e => { const r = e.target.closest("[data-g]"); if (r) { const k = r.dataset.g, z = shipZ[k] = shipZ[k] || { g: +k }; r.querySelectorAll("[data-z]").forEach(i => { if (i.dataset.z === "on") { z.off = !i.checked; r.classList.toggle("off", z.off); } else z[i.dataset.z] = i.dataset.z === "d" ? i.value.trim() : i.value.trim() === "" ? null : num(i.value); }); } mark(); });
  },
  leave, acts: {
    shipMode: v => { const d = SB.read(SB.body().querySelector("[data-form]")); st.settings.ship = { ...st.settings.ship, ...d.ship }; shipMode = v; infoDirty = true; V.ship.draw(SB.body()); },
    shipSave: b => run(b.tagName === "FORM" ? b.querySelector(".sm-save .btn") : b, async () => { const d = SB.read(SB.body().querySelector("[data-form]")); await post("zones", { zones: shipMode === "zones" ? Object.values(shipZ) : [], ship: { ...d.ship, mode: shipMode } }); infoDirty = false; say("اتحفظ ✅"); SB.show("ship"); })
  }
};

/* ================= pages ================= */
V.pages = {
  async open(body) { body.innerHTML = `<section class="card"><div class="row"><div><h3>صفحات المتجر</h3><p class="sub">القانون بيطلب صفحات الاستبدال والخصوصية والشروط، فجهّزناهالك. راجعها وعدّلها على شغلك.</p></div><span class="space"></span><button type="button" class="btn sm" data-do="pgEdit:0">${ico("plus")} صفحة جديدة</button></div><div class="sm-list">${st.pages.map(p => `<div class="sm-row ${p.hidden ? "dim" : ""}"><button type="button" class="grow" data-do="pgEdit:${p.id}"><b>${esc(p.title)}</b><small dir="ltr">/page/${esc(p.slug)}</small></button>${p.kind === "returns" && !st.settings.returnsSeen ? `<span class="sm-pill y">راجعها</span>` : ""}${p.hidden ? '<span class="sm-pill g">مخفية</span>' : ""}<a class="sm-ic" href="${esc(st.url)}/page/${esc(p.slug)}" target="_blank" rel="noopener" aria-label="افتحها">${ico("out")}</a></div>`).join("")}</div></section>`; },
  acts: {
    pgEdit: id => {
      const p = st.pages.find(x => x.id === +id) || { id: 0, title: "", titleEn: "", body: "", bodyEn: "", kind: "custom", inFooter: true }, en = st.draft.langs !== "ar", must = ["returns", "contact", "terms", "privacy"].includes(p.kind); let dirty = false;
      const o = SB.pane({ title: p.id ? p.title : "صفحة جديدة", cls: "wide", dirty: () => dirty, html: () => `<div class="sm-form">${fld("title", "العنوان", p.title, { max: 100, must: true })}${en ? fld("titleEn", "العنوان بالإنجليزي", p.titleEn, { max: 100, dir: "ltr" }) : ""}${fld("body", "الكلام", p.body, { area: 14, max: 20000, hint: "سطر يبدأ بـ ## بيبقى عنوان، وسطر يبدأ بـ - بيبقى نقطة في قايمة." })}${en ? fld("bodyEn", "الكلام بالإنجليزي", p.bodyEn, { area: 12, max: 20000, dir: "ltr" }) : ""}${must ? '<p class="sm-hint">الصفحة دي مطلوبة قانونًا، فبتفضل ظاهرة في آخر المتجر.</p>' : SB.tog("inFooter", p.inFooter !== false, "تظهر في آخر المتجر") + SB.tog("hidden", p.hidden, "مخفية عن الزباين")}</div>`, foot: () => `<button type="button" class="btn" data-do="pgSave">احفظ</button>${p.id && p.kind === "custom" ? '<span class="space"></span><button type="button" class="btn danger sm" data-do="pgDel">امسحها</button>' : ""}`, after: e => e.querySelector(".sm-p-b").addEventListener("input", () => { dirty = true; }),
        acts: { pgSave: b => run(b, async () => { await post("page-save", { ...p, ...SB.read(o.el) }); dirty = false; o.close(true); say("اتحفظ ✅"); SB.show("pages"); }), pgDel: async b => { if (await SB.sure("تمسح الصفحة دي؟", "امسحها")) run(b, async () => { await post("page-del", { id: p.id }); dirty = false; o.close(true); SB.show("pages"); }); } } });
    }
  }
};

/* ================= what the bot knows ================= */
V.bot = {
  async open(body) {
    const b = await SB.get("bot"); if (view !== "bot") return; const good = b.test.filter(x => x.ok).length;
    body.innerHTML = `<section class="card"><h3>البوت بيعرف متجرك لوحده</h3><p class="sub">أول ما تحفظ منتج أو سعر أو عرض أو شحن، البوت بيقوله صح في نفس اللحظة${b.channels.wa ? "، على المتجر وعلى واتساب" : " على المتجر"}. مش محتاج ترفعله ملفات.</p><div class="sm-nums"><div><small>منتجات عارفها</small><b>${fmt(b.count)}</b></div><div><small>ملفات إضافية</small><b>${fmt(b.files.length)}</b><i>من «داتا المتجر»</i></div>${b.test.length ? `<div><small>آخر اختبار</small><b>${fmt(good)} من ${fmt(b.test.length)}</b><i>${SB.ago(b.testAt)}</i></div>` : ""}</div></section>
      <section class="card"><div class="row"><div><h3>جرّبه: 10 أسئلة من متجرك</h3><p class="sub">بنسأله أسئلة زي الزباين (سعر، مقاس خلص، شحن، عرض، منتج مش عندك) ونقارن رده بالبيانات الحقيقية.</p></div><span class="space"></span><button type="button" class="btn sm" data-do="botTest">${b.test.length ? "اختبره تاني" : "ابدأ الاختبار"}</button></div><div class="sm-bt">${b.test.map(x => `<details class="${x.ok ? "ok" : "bad"}"><summary><i>${x.ok ? ico("check") : ico("x")}</i><span>${esc(x.q)}</span><small>${esc(x.k)}</small></summary><p>${esc(x.a)}</p>${x.ok ? "" : `<p class="sm-hint">كان المفروض: ${esc(x.want)}</p>`}</details>`).join("")}</div></section>
      <section class="card"><h3>اللي هو عارفه دلوقتي</h3>${[["عن المتجر", b.summary], ["الشحن", b.shipping], ["العروض", b.offers], ["الاستبدال والاسترجاع", b.returns], ["التواصل", b.contact], ["أول المنتجات", b.products]].map(x => `<details class="sm-kn"><summary>${x[0]}</summary><pre dir="rtl">${esc(x[1])}</pre></details>`).join("")}</section>`;
  },
  acts: { botTest: b => run(b, async () => { say("بنسأله 10 أسئلة... بياخد نص دقيقة."); await post("bot-test", {}); if (view === "bot") SB.show("bot"); }) }
};
})();
