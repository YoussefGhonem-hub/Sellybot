/* Sellybot's own dialogs, instead of the browser's grey confirm() / prompt() boxes. One file for every Sellybot page
   (loaded with defer; ci-deploy.ps1 stamps ?v= with the file's fingerprint, like sb-loader.js).
   Everything returns a Promise, so a call site reads  if (!(await sbConfirm(...))) return;  or  sbConfirm(...).then(...).
   - sbConfirm(text, o)        → true only when the main button was pressed (its "no" button, ×, Esc and the backdrop → false)
   - sbPrompt(text, value, o)  → the text typed (trimmed), or null.  o.multiline = a textarea, o.type = "number",
                                 o.required = the button waits for something typed
   - sbAlert(text, o)          → one button
   - sbCopy(url, o)            → the link in a box with «انسخ» (when the clipboard was refused)
   - SbDialog.ask(o)           → all of the above: { title, text, ok, no, danger, icon, input, value, copy, ... };
                                 resolves true / false (its "no" button) / null (closed), or the text for an input
   o: title (bold line on top; without it the text is the headline), ok / no (the buttons' words; no: false = one button),
      danger (red main button, and the safe button gets the focus), icon ("warn" | "ask" | "edit" | "link" | "info" | "ok").
   Only one at a time: a second call waits for the first to close. Phones get it as a sheet from the bottom.
   It sits above everything, the store chat bubble (2147483000-1) and the loader included. */
(function () {
  "use strict";
  if (window.SbDialog) return;
  var D = document, R = D.documentElement, queue = [], cur = null, uid = 0;

  var css =
    ".sbd{--sbd-card:#fff;--sbd-ink:#221a14;--sbd-muted:#7f7266;--sbd-line:#ece4db;--sbd-soft:#fbf7f3;--sbd-brand:#e8774a;--sbd-brand2:#f5a25d;--sbd-err:#c0392b;--sbd-ok:#2e9d5b;" +
    "position:fixed;inset:0;z-index:2147483500;display:grid;place-items:center;padding:20px;direction:rtl;text-align:right;font:inherit;font-size:15px;line-height:1.6;color:var(--sbd-ink);" +
    "background:rgba(25,15,8,.42);-webkit-backdrop-filter:blur(5px);backdrop-filter:blur(5px);opacity:0;transition:opacity .2s ease}" +
    "@media (prefers-color-scheme:dark){:root:not([data-theme=light]) .sbd{--sbd-card:#211a15;--sbd-ink:#f4ebe2;--sbd-muted:#b3a496;--sbd-line:#362b22;--sbd-soft:#2a211a;--sbd-err:#ff8a7a;--sbd-ok:#4cc27e}}" +
    ":root[data-theme=dark] .sbd{--sbd-card:#211a15;--sbd-ink:#f4ebe2;--sbd-muted:#b3a496;--sbd-line:#362b22;--sbd-soft:#2a211a;--sbd-err:#ff8a7a;--sbd-ok:#4cc27e}" +
    ".sbd.on{opacity:1}" +
    ".sbd *{box-sizing:border-box}" +
    ".sbd-c{position:relative;width:100%;max-width:440px;max-height:calc(100vh - 40px);overflow:auto;background:var(--sbd-card);border-radius:22px;padding:24px 22px 20px;" +
    "box-shadow:0 2px 6px rgba(40,25,10,.08),0 24px 60px rgba(40,25,10,.28);transform:translateY(8px) scale(.96);transition:transform .25s cubic-bezier(.2,.9,.3,1.15)}" +
    ".sbd.on .sbd-c{transform:none}" +
    ".sbd-x{position:absolute;top:12px;left:12px;width:34px;height:34px;border-radius:50%;border:0;background:transparent;color:var(--sbd-muted);font:inherit;font-size:22px;line-height:1;cursor:pointer;display:grid;place-items:center}" +
    ".sbd-x:hover{background:var(--sbd-soft);color:var(--sbd-ink)}" +
    ".sbd-i{width:52px;height:52px;border-radius:16px;display:grid;place-items:center;margin:0 0 14px;color:var(--sbd-brand);background:color-mix(in srgb,var(--sbd-brand) 14%,transparent)}" +
    ".sbd-i svg{width:26px;height:26px}" +
    ".sbd-i.warn{color:var(--sbd-err);background:color-mix(in srgb,var(--sbd-err) 13%,transparent)}" +
    ".sbd-i.ok{color:var(--sbd-ok);background:color-mix(in srgb,var(--sbd-ok) 14%,transparent)}" +
    ".sbd-h{margin:0 0 6px;font-size:18.5px;font-weight:800;line-height:1.45;padding-left:28px}" +
    ".sbd-p{margin:0;color:var(--sbd-muted);white-space:pre-line;overflow-wrap:anywhere}" +
    ".sbd-c.solo .sbd-p{color:var(--sbd-ink);font-size:16px;font-weight:600;padding-left:28px}" +
    ".sbd-f{margin-top:14px}" +
    ".sbd-in{display:block;width:100%;font:inherit;font-size:15px;color:var(--sbd-ink);background:var(--sbd-soft);border:1.5px solid var(--sbd-line);border-radius:12px;padding:11px 13px;outline:0;transition:border-color .15s,box-shadow .15s;resize:vertical}" +
    "textarea.sbd-in{min-height:104px;line-height:1.7}" +
    ".sbd-in:focus{border-color:var(--sbd-brand);box-shadow:0 0 0 3px color-mix(in srgb,var(--sbd-brand) 22%,transparent)}" +
    ".sbd-cp{display:flex;gap:8px}.sbd-cp .sbd-in{direction:ltr;text-align:left;flex:1;min-width:0;font-size:13.5px}" +
    ".sbd-e{margin-top:8px;color:var(--sbd-err);font-size:13.5px}.sbd-e:empty{display:none}" +
    ".sbd-b{display:flex;gap:10px;margin-top:20px;flex-wrap:wrap}" +
    ".sbd-btn{flex:1 1 0;min-width:120px;min-height:46px;border-radius:12px;padding:10px 16px;font:inherit;font-size:15px;font-weight:700;cursor:pointer;border:1.5px solid transparent;transition:transform .12s,filter .15s,background .15s}" +
    ".sbd-btn:active{transform:scale(.97)}" +
    ".sbd-btn:focus-visible,.sbd-x:focus-visible{outline:3px solid color-mix(in srgb,var(--sbd-brand) 55%,transparent);outline-offset:2px}" +
    ".sbd-go{color:#fff;background:linear-gradient(135deg,var(--sbd-brand),var(--sbd-brand2));box-shadow:0 6px 16px color-mix(in srgb,var(--sbd-brand) 35%,transparent)}" +
    ".sbd-go:hover{filter:brightness(1.05)}" +
    ".sbd-go:disabled{opacity:.45;cursor:not-allowed;box-shadow:none}" +
    ".sbd-c.danger .sbd-go{background:var(--sbd-err);box-shadow:0 6px 16px color-mix(in srgb,var(--sbd-err) 30%,transparent)}" +
    ":root[data-theme=dark] .sbd-c.danger .sbd-go{color:#2a0d08}" +
    ".sbd-no{background:transparent;color:var(--sbd-ink);border-color:var(--sbd-line)}.sbd-no:hover{background:var(--sbd-soft)}" +
    ".sbd-cp .sbd-btn{flex:0 0 auto;min-width:0;min-height:0}" +
    "@media (max-width:520px){.sbd{place-items:end stretch;padding:0}" +
    ".sbd-c{max-width:none;border-radius:24px 24px 0 0;padding:22px 18px calc(18px + env(safe-area-inset-bottom));transform:translateY(100%)}" +
    ".sbd-c::before{content:'';display:block;width:40px;height:4.5px;border-radius:9px;background:var(--sbd-line);margin:-8px auto 14px}" +
    ".sbd-b{flex-direction:column}.sbd-btn{flex:none;width:100%}}" +
    "@media (prefers-reduced-motion:reduce){.sbd,.sbd-c{transition:none}}" +
    "html.sbd-lock{overflow:hidden}";

  var I = function (d) { return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + d + '</svg>'; };
  var ICONS = {
    warn: I('<path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/>'),
    ask: I('<circle cx="12" cy="12" r="10"/><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17h.01"/>'),
    edit: I('<path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>'),
    link: I('<path d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7"/><path d="M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7"/>'),
    info: I('<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>'),
    ok: I('<circle cx="12" cy="12" r="10"/><path d="m8 12 3 3 5-6"/>')
  };
  function esc(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function addCss() { if (D.getElementById("sbd-css")) return; var s = D.createElement("style"); s.id = "sbd-css"; s.textContent = css; (D.head || R).appendChild(s); }

  function ask(o) {
    return new Promise(function (res) { queue.push({ o: o || {}, res: res }); if (!cur) next(); });
  }
  function next() { cur = queue.shift() || null; if (cur) show(cur); }

  function show(it) {
    addCss();
    var o = it.o, n = ++uid, prev = D.activeElement, done = false;
    var input = o.input || (o.multiline ? "textarea" : null), copy = o.copy;
    var one = o.no === false || copy != null;
    var icon = o.icon || (o.danger ? "warn" : input ? "edit" : copy != null ? "link" : one ? "info" : "ask");
    var field = "";
    if (input === "textarea") field = '<div class="sbd-f"><textarea class="sbd-in" dir="auto" rows="4" maxlength="' + (o.max || 2000) + '"' + (o.placeholder ? ' placeholder="' + esc(o.placeholder) + '"' : "") + '>' + esc(o.value || "") + '</textarea></div>';
    else if (input) field = '<div class="sbd-f"><input class="sbd-in" dir="auto" type="text"' + (input === "number" ? ' inputmode="decimal"' : "") +
      ' value="' + esc(o.value == null ? "" : o.value) + '" maxlength="' + (o.max || 300) + '"' + (o.placeholder ? ' placeholder="' + esc(o.placeholder) + '"' : "") + '></div>';
    else if (copy != null) field = '<div class="sbd-f sbd-cp"><input class="sbd-in" readonly value="' + esc(copy) + '" aria-label="اللينك"><button type="button" class="sbd-btn sbd-go sbd-cpy">انسخ</button></div>';

    var root = D.createElement("div");
    root.className = "sbd"; root.dir = "rtl";
    var hasT = !!o.title;
    root.innerHTML =
      '<div class="sbd-c' + (o.danger ? " danger" : "") + (hasT ? "" : " solo") + '" role="' + (o.danger ? "alertdialog" : "dialog") + '" aria-modal="true" aria-labelledby="sbd-l' + n + '"' + (hasT && o.text ? ' aria-describedby="sbd-d' + n + '"' : "") + '>' +
      '<button type="button" class="sbd-x" aria-label="اقفل">×</button>' +
      '<div class="sbd-i ' + icon + '">' + (ICONS[icon] || ICONS.info) + '</div>' +
      (hasT ? '<h2 class="sbd-h" id="sbd-l' + n + '">' + esc(o.title) + '</h2>' : "") +
      (o.text ? '<p class="sbd-p" id="sbd-' + (hasT ? "d" : "l") + n + '">' + esc(o.text) + '</p>' : "") +
      field + '<div class="sbd-e" role="alert"></div>' +
      '<div class="sbd-b">' + (copy != null ? "" : '<button type="button" class="sbd-btn sbd-go">' + esc(o.ok || (one ? "تمام" : "تمام، كمّل")) + '</button>') +
      (one && copy == null ? "" : '<button type="button" class="sbd-btn sbd-no">' + esc(copy != null ? (o.no || "اقفل") : (o.no || "لأ، رجوع")) + '</button>') + '</div></div>';
    D.body.appendChild(root);
    R.classList.add("sbd-lock");
    var card = root.firstChild, go = card.querySelector(".sbd-b .sbd-go"), no = card.querySelector(".sbd-no"), fld = card.querySelector(".sbd-in"), err = card.querySelector(".sbd-e");

    function value() { return fld ? fld.value.trim() : ""; }
    function valid() {
      if (!input) return true;
      var v = value();
      if (o.required && !v) return false;
      if (input === "number" && v && !isFinite(+v.replace(/[٠-٩]/g, function (d) { return "٠١٢٣٤٥٦٧٨٩".indexOf(d); }).replace(/,/g, ""))) return false;
      return true;
    }
    function sync() { if (go && input) go.disabled = !valid(); if (err) err.textContent = input === "number" && value() && !valid() ? "اكتب رقم بس" : ""; }
    function close(result) {
      if (done) return; done = true;
      root.classList.remove("on");
      D.removeEventListener("keydown", onKey, true);
      setTimeout(function () {
        root.remove();
        if (!queue.length) R.classList.remove("sbd-lock");
        if (prev && prev.focus && D.contains(prev)) try { prev.focus({ preventScroll: true }); } catch (e) { }
        it.res(result);
        next();
      }, matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 180);
    }
    function ok() {
      if (input) {
        if (!valid()) { sync(); fld.focus(); return; }
        var v = value();
        if (input === "number") v = v.replace(/[٠-٩]/g, function (d) { return "٠١٢٣٤٥٦٧٨٩".indexOf(d); }).replace(/,/g, "");
        close(v);
      } else close(true);
    }
    function dismiss() { close(input ? null : copy != null ? true : one ? true : null); }

    if (go) go.onclick = ok;
    if (no) no.onclick = function () { close(input ? null : copy != null ? true : false); };
    card.querySelector(".sbd-x").onclick = dismiss;
    root.addEventListener("mousedown", function (e) { if (e.target === root) root.dataset.down = "1"; });
    root.addEventListener("click", function (e) { if (e.target === root && root.dataset.down) dismiss(); delete root.dataset.down; });
    if (fld && input) fld.addEventListener("input", sync);
    var cpy = card.querySelector(".sbd-cpy");
    if (cpy) cpy.onclick = function () {
      var said = function () { cpy.textContent = "اتنسخ ✅"; setTimeout(function () { cpy.textContent = "انسخ"; }, 1800); };
      fld.focus(); fld.select();
      var legacy = function () { try { if (D.execCommand("copy")) { said(); return; } } catch (e) { } err.textContent = "علّم على اللينك وانسخه بإيدك"; };
      if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(copy).then(said, legacy); else legacy();
    };
    function onKey(e) {
      if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); dismiss(); return; }
      if (e.key === "Enter" && fld && input && e.target === fld && (input !== "textarea" || e.ctrlKey || e.metaKey)) { e.preventDefault(); ok(); return; }
      if (e.key === "Tab") {   // keep the focus inside the dialog
        var f = [].slice.call(card.querySelectorAll("button:not(:disabled),input,textarea")), i = f.indexOf(D.activeElement);
        if (!f.length) return;
        if (e.shiftKey && i <= 0) { f[f.length - 1].focus(); e.preventDefault(); }
        else if (!e.shiftKey && i === f.length - 1) { f[0].focus(); e.preventDefault(); }
        else if (i < 0) { f[0].focus(); e.preventDefault(); }
      }
    }
    D.addEventListener("keydown", onKey, true);
    sync();
    requestAnimationFrame(function () { root.classList.add("on"); });
    setTimeout(function () {
      var t = fld && input ? fld : cpy ? cpy : o.danger && no ? no : go || no;
      if (t) { t.focus({ preventScroll: true }); if (t === fld && fld.select && o.select !== false) fld.select(); }
    }, 30);
  }

  var api = {
    ask: ask,
    confirm: function (text, o) { o = o || {}; o.text = o.text || text; return ask(o).then(function (r) { return r === true; }); },
    prompt: function (text, value, o) { o = o || {}; o.text = o.text || text; o.value = value; o.input = o.input || (o.multiline ? "textarea" : o.type === "number" ? "number" : "text"); return ask(o); },
    alert: function (text, o) { o = o || {}; o.text = o.text || text; o.no = false; return ask(o).then(function () { }); },
    copy: function (url, o) { o = o || {}; o.copy = url; o.title = o.title || "انسخ اللينك"; o.text = o.text || "المتصفح ماسمحلناش ننسخه لوحدنا. دوس «انسخ» أو علّم على اللينك وانسخه."; return ask(o).then(function () { }); }
  };
  window.SbDialog = api;
  window.sbConfirm = api.confirm; window.sbPrompt = api.prompt; window.sbAlert = api.alert; window.sbCopy = api.copy;
})();
