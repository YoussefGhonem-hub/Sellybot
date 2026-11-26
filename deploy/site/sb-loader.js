/* Sellybot's loader: its logo drawing itself, whenever a page or something on it is slow. One file for every Sellybot page,
   loaded FIRST THING in <body> and not deferred, so it can cover the page while it builds (kept a week by browsers and
   Cloudflare; ci-deploy.ps1 stamps ?v= with the file's fingerprint).
   - While the page starts: shown after 350 ms, until the page is built and the requests it sent meanwhile came back.
     <html data-sbl-wait="#a,#b">: until one of those is on screen instead (the dashboard's login or app). 15 s at most.
   - After that: a request the person asked for (a click, Enter, the back button or a #tab, up to 1.5 s before) that is
     still out after 800 ms, while reading (GET: actions already show a spinner on their button). Never the chat stream.
   - SbLoader.wrap(promise) for other slow things (scripts, several steps), SbLoader.quiet(fn) = fetches inside fn never
     show it, SbLoader.ready() ends the start by hand. It never blocks clicks after the page has started. */
(function () {
  "use strict";
  if (window.SbLoader) return;
  if (document.documentElement.hasAttribute("data-sbl-off")) {   // <html data-sbl-off>: a page that isn't Sellybot's own (a store's chat)
    window.SbLoader = { wrap: function (p) { return p; }, quiet: function (fn) { return fn(); }, ready: function () { }, start: function () { return function () { }; } }; return;
  }
  var D = document, R = D.documentElement, now = Date.now, BOOT_DELAY = 350, SLOW = 800, MIN_SHOW = 450, GESTURE = 1500, BOOT_MAX = 15000, LONG = 8000;
  var SKIP = /\/(chat|health)\.ashx$/i;

  var css = "#sbl{--sbl-bg:var(--bg,#f7f4f0);--sbl-card:var(--card,#fff);--sbl-ink:var(--ink,#221a14);--sbl-muted:var(--muted,#7f7266);--sbl-brand:var(--brand,#e8774a);" +
    "position:fixed;inset:0;z-index:2147483000;display:grid;place-items:center;pointer-events:none;opacity:0;visibility:hidden;transition:opacity .22s ease,visibility 0s linear .22s;direction:rtl}" +
    "@media (prefers-color-scheme:dark){:root:not([data-theme=light]) #sbl{--sbl-bg:var(--bg,#16110d);--sbl-card:var(--card,#211a15);--sbl-ink:var(--ink,#f4ebe2);--sbl-muted:var(--muted,#b3a496)}}" +
    ":root[data-theme=dark] #sbl{--sbl-bg:var(--bg,#16110d);--sbl-card:var(--card,#211a15);--sbl-ink:var(--ink,#f4ebe2);--sbl-muted:var(--muted,#b3a496)}" +
    "#sbl.on{opacity:1;visibility:visible;transition:opacity .22s ease}" +
    "#sbl.boot{background:var(--sbl-bg);pointer-events:auto}" +
    "#sbl.slow{background:color-mix(in srgb,var(--sbl-bg) 40%,transparent)}" +
    "#sbl .sbl-c{display:grid;justify-items:center;gap:6px;padding:18px 26px 16px;border-radius:22px;transform:scale(.94);transition:transform .25s cubic-bezier(.2,.9,.3,1.2)}" +
    "#sbl.on .sbl-c{transform:none}" +
    "#sbl.slow .sbl-c{background:var(--sbl-card);box-shadow:0 1px 2px rgba(40,25,10,.06),0 14px 40px rgba(40,25,10,.16)}" +
    "#sbl .sbl-l{position:relative;display:grid;place-items:center;width:116px;height:116px}" +
    "#sbl.slow .sbl-l{width:72px;height:72px}" +
    "#sbl .sbl-g{position:absolute;inset:0;border-radius:50%;background:radial-gradient(circle,color-mix(in srgb,var(--sbl-brand) 30%,transparent) 0,transparent 68%);animation:sbl-p 1.8s ease-in-out infinite}" +
    "#sbl svg{position:relative;width:52%;height:62%;overflow:visible;animation:sbl-b 1.8s ease-in-out infinite}" +
    "#sbl path{fill:none;stroke-linecap:round}" +
    "#sbl .a,#sbl .b{stroke-width:9;stroke-dasharray:1 1;animation:sbl-d 1.8s cubic-bezier(.65,0,.35,1) infinite}" +
    "#sbl .a,#sbl .ta{stroke:var(--sbl-brand)}#sbl .b,#sbl .tb{stroke:var(--sbl-ink)}" +
    "#sbl .ta,#sbl .tb{stroke-width:5;transform-box:fill-box;transform-origin:center;animation:sbl-t 1.8s ease infinite}" +
    "#sbl .b,#sbl .tb{animation-delay:.22s}" +
    "#sbl .sbl-w{font-weight:700;font-size:17px;line-height:1.2;color:var(--sbl-ink);letter-spacing:.2px;direction:ltr}#sbl .sbl-w span{color:var(--sbl-brand)}" +
    "#sbl.slow .sbl-w{display:none}" +
    "#sbl .sbl-s{font-size:13px;color:var(--sbl-muted);min-height:1em;text-align:center;max-width:260px}" +
    "@keyframes sbl-d{0%{stroke-dashoffset:1}45%,58%{stroke-dashoffset:0}100%{stroke-dashoffset:-1}}" +
    "@keyframes sbl-t{0%,25%{opacity:0;transform:scale(.3)}45%,62%{opacity:1;transform:none}88%,100%{opacity:0;transform:scale(.3)}}" +
    "@keyframes sbl-p{0%,100%{transform:scale(.82);opacity:.55}50%{transform:scale(1.06);opacity:1}}" +
    "@keyframes sbl-b{0%,100%{transform:none}50%{transform:translateY(-2px) rotate(-5deg)}}" +
    "@media (prefers-reduced-motion:reduce){#sbl svg,#sbl .a,#sbl .b,#sbl .ta,#sbl .tb{animation:none;stroke-dashoffset:0;opacity:1}#sbl .sbl-g{animation-duration:2.6s}}";
  var MARK = '<svg viewBox="12 2 60 72" aria-hidden="true">' +
    '<path class="a" pathLength="1" d="M58 22c0-7-7-12-16-12S24 15 24 24s8 12 18 15"/>' +
    '<path class="b" pathLength="1" d="M26 54c0 7 7 12 16 12s18-5 18-14-8-12-18-15"/>' +
    '<path class="ta" d="M58 22l8-6"/><path class="tb" d="M26 54l-8 6"/></svg>';

  var el = null, sub = null, mode = "", shownAt = 0, hideT = 0, longT = 0;
  function mount() {
    if (el || !D.body) return;
    var st = D.createElement("style"); st.textContent = css; (D.head || R).appendChild(st);
    el = D.createElement("div"); el.id = "sbl"; el.setAttribute("role", "status"); el.setAttribute("aria-live", "polite");
    el.innerHTML = '<div class="sbl-c"><div class="sbl-l"><span class="sbl-g"></span>' + MARK + '</div><b class="sbl-w">Selly<span>bot</span></b><small class="sbl-s"></small></div>';
    D.body.insertBefore(el, D.body.firstChild); sub = el.querySelector(".sbl-s"); sync();
  }

  // ---------- what should show ----------
  var boot = true, bootAt = now(), bootSeen = false, bootN = 0, slowN = 0, gestureAt = 0, quietN = 0, loaded = D.readyState !== "loading";
  function want() { return boot ? (bootSeen ? "boot" : "") : slowN > 0 ? "slow" : ""; }
  function sync() {
    if (!el) return;
    var w = want();
    if (w === mode) return;
    clearTimeout(hideT);
    if (w) { show(w); return; }
    var left = MIN_SHOW - (now() - shownAt);   // a short flash is worse than a short wait
    if (left > 0) hideT = setTimeout(function () { if (!want()) hide(); }, left); else hide();
  }
  function show(w) {
    if (!mode) shownAt = now();
    mode = w; el.className = "on " + w; el.setAttribute("aria-label", "بنحمّل");
    sub.textContent = w === "slow" ? "ثانية واحدة…" : "";
    clearTimeout(longT); longT = setTimeout(function () { if (mode) sub.textContent = "النت بطيء شوية… لسه بنحمّل"; }, LONG);
  }
  function hide() { mode = ""; el.className = ""; el.removeAttribute("aria-label"); clearTimeout(longT); }

  // ---------- while the page starts ----------
  function waitOk() {
    var sel = R.getAttribute("data-sbl-wait"); if (!sel) return loaded && bootN === 0;
    var l = D.querySelectorAll(sel); for (var i = 0; i < l.length; i++) if (!l[i].hidden && l[i].getClientRects().length) return true;
    return false;
  }
  function checkBoot() { if (boot && (waitOk() || now() - bootAt > BOOT_MAX)) endBoot(); }
  function endBoot() { if (!boot) return; boot = false; clearInterval(bootI); sync(); }
  var bootI = setInterval(checkBoot, 120);
  setTimeout(function () { if (boot) { bootSeen = true; sync(); } }, BOOT_DELAY);
  if (!loaded) D.addEventListener("DOMContentLoaded", function () { loaded = true; mount(); checkBoot(); });
  mount();

  // ---------- after: what the person asked for ----------
  function gesture() { gestureAt = now(); }
  D.addEventListener("pointerdown", gesture, true);
  D.addEventListener("keydown", function (e) { if (e.key === "Enter" && !(e.target && e.target.tagName === "TEXTAREA")) gesture(); }, true);
  window.addEventListener("hashchange", gesture); window.addEventListener("popstate", gesture);
  window.addEventListener("pageshow", function (e) { if (e.persisted) { boot = false; slowN = 0; clearInterval(bootI); if (el) hide(); } });   // back to a page kept in memory

  function slowItem() {
    var on = false, t = setTimeout(function () { on = true; slowN++; sync(); }, SLOW), done = false;
    return function () { if (done) return; done = true; clearTimeout(t); if (on) { slowN--; sync(); } };
  }
  function track(input, init) {
    if (quietN) return null;
    var url = typeof input === "string" ? input : input && input.url ? input.url : String(input), u;
    try { u = new URL(url, location.href); } catch (e) { return null; }
    if (u.origin !== location.origin || SKIP.test(u.pathname)) return null;
    if (boot) { bootN++; var d = false; return function () { if (!d) { d = true; bootN--; checkBoot(); } }; }
    var m = String((init && init.method) || (input && input.method) || "GET").toUpperCase();
    if (m !== "GET" || now() - gestureAt > GESTURE) return null;
    return slowItem();
  }
  var f0 = window.fetch;
  if (f0) window.fetch = function (input, init) {
    var p = f0.apply(this, arguments);
    try { var done = track(input, init); if (done) p.then(done, done); } catch (e) { }
    return p;
  };

  window.SbLoader = {
    wrap: function (p) { if (p && p.then) { var done = slowItem(); p.then(done, done); } return p; },
    quiet: function (fn) { quietN++; try { return fn(); } finally { quietN--; } },
    ready: endBoot,
    start: function () { return slowItem(); }   // shows after 800 ms unless the function it returns is called first
  };
})();
