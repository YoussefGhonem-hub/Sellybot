/* The invoice page (ShopDesk.Print "invoice", written by ShopInvoice.cs): «نزّلها صورة» and «نزّلها PDF» next to «اطبع». The browser draws the
   invoice itself (html2canvas reads the page as it shows, so Arabic joins and runs right to left exactly as on screen), drawn at an A4 width
   whatever the screen; the PDF is that picture cut into A4 pages between rows, never through a line. One file per invoice for the picture, one
   PDF for all of them. html2canvas 1.4.1 and jsPDF 2.5.1 (MIT, in vendor/, never from a CDN) load only when a button is pressed. */
(() => {
"use strict";
const D = document, bar = D.querySelector(".bar"), me = D.currentScript, base = me && me.src ? me.src.replace(/invoice\.js[^/]*$/, "") : "../shop-admin/";
if (!bar || !D.querySelector(".inv")) return;
const load = src => new Promise((ok, no) => { const s = D.createElement("script"); s.src = base + src; s.onload = ok; s.onerror = () => no(new Error("ماقدرناش نجهّز التحميل. اتأكد من النت وجرّب تاني.")); D.head.appendChild(s); });
let libs = null; const need = () => libs || (libs = Promise.all([window.html2canvas ? 0 : load("vendor/html2canvas-1.4.1.min.js"), window.jspdf ? 0 : load("vendor/jspdf-2.5.1.umd.min.js")]).catch(e => { libs = null; throw e; }));
const save = (blob, name) => { const a = D.createElement("a"), u = URL.createObjectURL(blob); a.href = u; a.download = name; D.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(u), 60000); };
const day = () => new Date().toISOString().slice(0, 10);
// one invoice drawn at A4 width; cuts: where a page may end (the bottom of each row and block), in the drawing's pixels
async function draw(el) {
  let cuts = [], h = 1;
  const c = await window.html2canvas(el, { scale: 2, backgroundColor: "#ffffff", logging: false, windowWidth: 1100,
    onclone: (doc, x) => { x = x || doc.querySelector('.inv[data-no="' + el.dataset.no + '"]'); x.style.width = "210mm"; x.style.margin = "0"; x.style.boxShadow = "none"; const top = x.getBoundingClientRect().top; h = x.getBoundingClientRect().height || 1; cuts = [...x.querySelectorAll(".top,.pp,tr,.notes p,.foot")].map(e => e.getBoundingClientRect().bottom - top); } });
  const k = c.height / h; return { c, cuts: cuts.map(y => Math.round(y * k)).sort((a, b) => a - b) };
}
const status = D.createElement("span"); status.setAttribute("role", "status"); status.style.cssText = "font-size:13px;opacity:.85";
const btn = (t, f) => { const b = D.createElement("button"); b.type = "button"; b.textContent = t; b.onclick = async () => { const all = bar.querySelectorAll("button"); all.forEach(x => x.disabled = true); status.textContent = "بنجهّزها..."; try { await need(); await f(); status.textContent = "اتنزّلت ✅"; } catch (e) { status.textContent = e && e.message || "حصلت مشكلة، جرّب تاني."; } finally { all.forEach(x => x.disabled = false); } }; return b; };
bar.append(btn("نزّلها صورة", async () => {
  for (const el of D.querySelectorAll(".inv")) { const { c } = await draw(el); const b = await new Promise(r => c.toBlob(r, "image/png")); save(b, "invoice-" + el.dataset.no + ".png"); await new Promise(r => setTimeout(r, 400)); }
}), btn("نزّلها PDF", async () => {
  const pdf = new window.jspdf.jsPDF({ unit: "mm", format: "a4", orientation: "portrait", compress: true }); let first = true; const els = [...D.querySelectorAll(".inv")];
  for (const el of els) {
    const { c, cuts } = await draw(el), mm = c.width / 210, page = Math.floor(297 * mm);
    for (let y = 0; y < c.height - 2;) {
      let end = Math.min(c.height, y + page); if (end < c.height) { const ok = cuts.filter(z => z > y + page * 0.3 && z <= end); if (ok.length) end = ok[ok.length - 1]; }
      const part = D.createElement("canvas"); part.width = c.width; part.height = end - y; const g = part.getContext("2d"); g.fillStyle = "#fff"; g.fillRect(0, 0, part.width, part.height); g.drawImage(c, 0, y, c.width, end - y, 0, 0, c.width, end - y);
      if (!first) pdf.addPage("a4", "portrait"); first = false; pdf.addImage(part.toDataURL("image/jpeg", 0.92), "JPEG", 0, 0, 210, (end - y) / mm); y = end;
    }
  }
  save(pdf.output("blob"), els.length === 1 ? "invoice-" + els[0].dataset.no + ".pdf" : "invoices-" + day() + ".pdf");
}), status);
})();
