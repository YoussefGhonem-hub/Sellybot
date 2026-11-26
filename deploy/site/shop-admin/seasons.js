/* «متجري»: «قوالب المواسم» in «شكل المتجر» (a group of the look editor, look.js) and the line in «المتجر» two weeks before a season.
   The owner picks a season and its first and last day; it goes in the draft like any change (the preview shows it, «انشر» publishes it)
   and ends by itself after its last day. Colours, drawings, the greeting and the headline are ready: nothing to type. */
(() => {
"use strict";
const SB = window.SbShop, V = SB.v, { esc, fmt, run, say, post, get } = SB, D = document;
let data = null, wait = null;
const load = (fresh) => (!fresh && data) ? Promise.resolve(data) : (wait || (wait = get("sea").then(d => { data = d; wait = null; return d; }).catch(e => { wait = null; throw e; })));
const dayN = iso => iso ? new Date(iso + "T12:00:00").toLocaleDateString("ar-EG", { day: "numeric", month: "long", ...(new Date(iso + "T12:00:00").getFullYear() === new Date().getFullYear() ? {} : { year: "numeric" }) }) : "";
const EM = { ramadan: "🌙", eidfitr: "🎈", eidadha: "🐑", blackfriday: "🏷️", valentine: "❤️", mothersday: "💐", school: "✏️", shamnessim: "🥚", summer: "☀️", newyear: "🎆" };
const plus = (iso, n) => new Date(Date.parse(iso + "T12:00:00Z") + n * 864e5).toISOString().slice(0, 10);
const span = (a, b) => Math.round((new Date(b + "T12:00:00") - new Date(a + "T12:00:00")) / 864e5);
const css = () => { if (D.getElementById("sm-sea-css")) return; const e = D.createElement("style"); e.id = "sm-sea-css"; e.textContent = `.sm-sea{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:8px;margin:8px 0}.sm-sea button{display:flex;flex-direction:column;align-items:flex-start;gap:2px;padding:10px 12px;border:1px solid var(--line);border-radius:12px;background:var(--card);text-align:start;min-height:64px}.sm-sea button b{font-size:14.5px}.sm-sea button small{color:var(--muted);font-size:12px}.sm-sea button:hover{border-color:var(--brand)}.sm-sea em{font-style:normal;font-size:18px}.sm-sea-on{display:flex;gap:10px;align-items:center;padding:10px 12px;border-radius:12px;background:var(--soft);margin-bottom:10px}.sm-sea-on b{font-size:15px}`; D.head.appendChild(e); };

/* ---------- the group in «شكل المتجر» ---------- */
const X = () => SB.lookX, lk = () => X().cur();
function body() {
  css(); if (!data) { load().then(() => X().part("season")).catch(e => say(e.message, true)); return '<div class="sm-load"><i></i></div>'; }
  const s = lk().season, l = data.list, today = data.today;
  if (!s || !s.k) return `<p class="sm-hint">شكل جاهز للموسم فوق شكل متجرك: ألوان ماشية مع لونك، رسومات بسيطة، تهنئة في الشريط اللي فوق، وعنوان أول الصفحة لو مكتبتش عنوان. بيظهر في الأيام اللي تختارها بس، وبيختفي لوحده.</p>${SB.tog("", false, "شكل موسم", "مقفول", 'data-ch="seaOn"')}`;
  const m = l.find(x => x.k === s.k) || { ar: s.k, lunar: false }, state = today > s.to ? ["خلص يوم " + dayN(s.to) + ". غيّر الأيام لو عايزه تاني.", "g"] : today < s.from ? ["هيبدأ يوم " + dayN(s.from) + "، بعد ما تنشر.", "b"] : ["شغال لحد " + dayN(s.to) + " (بعد ما تنشر).", "ok"];
  const offers = data.offers || [];
  return `<div class="sm-sea-on"><em aria-hidden="true">${EM[s.k] || "🎉"}</em><div class="grow"><b>شكل ${esc(m.ar)}</b><br><span class="sm-pill ${state[1]}">${esc(state[0])}</span></div><button type="button" class="btn ghost sm" data-do="seaOff">شيله</button></div>
    <div class="sm-2"><div class="sm-f"><label for="seaF">أول يوم</label><input type="date" id="seaF" value="${esc(s.from)}" data-ch="seaD:from"></div><div class="sm-f"><label for="seaT">آخر يوم</label><input type="date" id="seaT" value="${esc(s.to)}" data-ch="seaD:to"></div></div>
    ${m.lunar ? `<p class="sm-hint">الأيام دي متوقعة من التقويم، والرؤية ممكن تقدّمها أو تأخّرها يوم. اتأكد منها قبل الموسم.</p>` : ""}
    <div class="sm-f"><label for="seaO">عرض معاه (اختياري)</label><select id="seaO" data-ch="seaO"><option value="0">من غير عرض</option>${offers.map(o => `<option value="${o.id}"${s.offer === o.id ? " selected" : ""}>${esc(o.name)}${o.on ? "" : " (مش شغال دلوقتي)"}</option>`).join("")}</select><small class="sm-hint">منتجات العرض بتاخد علامة «${esc(m.ar)}»، والشريط بيظهر فيه «شوف العروض» طول ما العرض شغال بس.</small></div>
    <p class="sm-hint">الشكل بيظهر للزباين بين اليومين دول بتوقيت مصر، وبيختفي لوحده بعد آخر يوم. ماشي مع أي قالب، ومن غير عداد ولا وعود: العداد بيظهر بس لو عرضك نفسه عليه عداد.</p>`;
}
const choose = () => { const l = data.list, sel = (lk().season || {}).k; return `<div class="sm-sea">${l.map(x => `<button type="button" data-do="seaPick:${x.k}"${sel === x.k ? ' aria-pressed="true"' : ""}><em aria-hidden="true">${EM[x.k] || "🎉"}</em><b>${esc(x.ar)}</b><small>${x.from ? (x.lunar ? "متوقع " : "") + dayN(x.from) + " لحد " + dayN(x.to) : ""}</small></button>`).join("")}</div>`; };
/* the season's days (Cairo): to after from, and not longer than the server keeps */
const setSea = s => { if (s && (!s.from || !s.to || s.to < s.from)) return say("آخر يوم لازم يكون بعد أول يوم.", true); if (s && span(s.from, s.to) >= (data.maxDays || 75)) return say("الموسم ماينفعش يكون أطول من " + fmt((data.maxDays || 75) - 1) + " يوم.", true); X().set("season", s); X().part("season"); return true; };
function pickPane() {
  const p = SB.pane({ title: "اختار الموسم", cls: "mid", html: () => `<p class="sub">الأيام بتتحط لوحدها، وتقدر تغيّرها بعد كده.</p>${choose()}`,
    acts: { seaPick: k => { const x = data.list.find(y => y.k === k); if (!x) return; p.close(true); if (setSea({ k, from: x.from, to: x.to, offer: (lk().season || {}).offer || 0 })) post("sea-seen", { k }).catch(() => { }); } } });
}
function hook() {
  const x = X(); if (!x || x.G.season) return;
  /* after «الشريط اللي فوق», where the greeting goes */
  const G = x.G, keep = Object.keys(G), at = keep.indexOf("ann") + 1, tail = keep.slice(at).map(k => [k, G[k]]); tail.forEach(([k]) => delete G[k]);
  G.season = ["🎉", "قوالب المواسم", body]; tail.forEach(([k, v]) => { G[k] = v; });
  Object.assign(V.look.acts, { seaOff: () => setSea(null), seaPick: k => { const m = data.list.find(y => y.k === k); if (m) setSea({ k, from: m.from, to: m.to, offer: 0 }); } });
  Object.assign(V.look.ch, {
    seaOn: v => { if (v) { if (!data) return; pickPane(); X().part("season"); } else setSea(null); },
    /* a new first day moves the whole season (same length); a new last day only moves its end */
    seaD: (v, which) => { const o = lk().season, s = { ...o }; s[which] = v; if (which === "from" && v) s.to = plus(v, Math.min(span(o.from, o.to), (data.maxDays || 75) - 1)); if (!setSea(s)) X().part("season"); },
    seaO: v => { const s = { ...lk().season, offer: +v || 0 }; setSea(s); }
  });
  const o0 = V.look.open; V.look.open = async function (b, a) { data = null; load(true).catch(() => { }); return o0.call(this, b, a); };
}

/* ---------- «المتجر»: a line two weeks before a season, once ---------- */
function tipLine(body) {
  if (!data || !data.tip || body.querySelector("[data-seatip]")) return; const t = data.tip, d = D.createElement("div");
  d.className = "sm-rc g"; d.dataset.seatip = t.k; d.style.marginBottom = "12px";
  d.innerHTML = `<span aria-hidden="true">${EM[t.k] || "🎉"}</span><div><b>${t.from <= data.today ? esc(t.ar) + " بدأ" : esc(t.ar) + " قرب: " + (t.lunar ? "متوقع " : "") + dayN(t.from)}</b><small>لبّس متجرك شكل ${esc(t.ar)}: ألوان ورسومات وتهنئة جاهزين، وبيختفي لوحده بعد الموسم.</small></div><button type="button" class="btn sm" data-do="seaTipGo:${t.k}">جهّزه</button><button type="button" class="btn ghost sm" data-do="seaTipNo:${t.k}">مش دلوقتي</button>`;
  body.insertBefore(d, body.firstChild);
}
SB.acts.seaTipNo = (k, b) => run(b, async () => { await post("sea-seen", { k }); if (data) data.tip = null; const e = D.querySelector("[data-seatip]"); if (e) e.remove(); });
SB.acts.seaTipGo = k => { post("sea-seen", { k }).catch(() => { }); if (data) data.tip = null; SB.show("look", "season"); };
const h0 = V.home.draw; V.home.draw = function (b) { h0.call(this, b); load().then(() => { if (SB.view() === "home") tipLine(SB.body()); }).catch(() => { }); };

if (SB.lookX) hook();
})();
