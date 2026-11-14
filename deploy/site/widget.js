/*
 * Store chat widget.
 *   On any website (e.g. Shopify):  <script src="https://YOUR-SITE/widget.js" data-store="STORE-ID" defer></script>
 *   Chat link page (index.html):    <script src="widget.js" data-mode="page"></script>   (store comes from ?store=)
 * Everything renders inside a Shadow DOM, so the store's own CSS can't break the chat and the chat can't break the store.
 */
(function () {
  var me = document.currentScript || (function () { var s = document.getElementsByTagName("script"); return s[s.length - 1]; })();
  var ART_ONLY = me.getAttribute("data-art-only") === "1";   // dashboard: only borrow the drawings, don't show a chat
  if (!ART_ONLY) { if (window.__storeChatLoaded) return; window.__storeChatLoaded = true; }

  // ---------------------------------------------------------------- setup
  var BASE = new URL(me.src, location.href).href.replace(/widget\.js(\?.*)?$/, "");
  var qs = new URLSearchParams(location.search);
  var MODE = me.getAttribute("data-mode") || "bubble";            // "bubble" = floating button on a website, "page" = full chat page
  var STORE = (me.getAttribute("data-store") || qs.get("store") || "riwaq").toLowerCase().replace(/[^a-z0-9-]/g, "");
  var PREVIEW = qs.get("preview") === "1";                          // dashboard preview: style/color come from the link
  if (PREVIEW && qs.get("view") === "bubble") MODE = "bubble";
  var API = BASE + "api/chat.ashx?store=" + encodeURIComponent(STORE);
  // this visitor's chat session (kept per store): ordering from the chat remembers the cart between messages and visits
  var SID = (function () {
    var key = "sb-sid-" + STORE, v = null;
    try { v = localStorage.getItem(key); } catch (e) {}
    if (!v || !/^[A-Za-z0-9_-]{16,64}$/.test(v)) {
      var a = new Uint8Array(16); (window.crypto || window.msCrypto).getRandomValues(a);
      v = Array.prototype.map.call(a, function (b) { return ("0" + b.toString(16)).slice(-2); }).join("");
      try { localStorage.setItem(key, v); } catch (e) {}
    }
    return v;
  })();

  var cfg = { name: "", botName: "", welcome: null, style: "bear", color: "#e8774a" };
  var history = [], busy = false, opened = false, storeMissing = false;

  // ---------------------------------------------------------------- colors
  function hexToRgb(h) { h = h.replace("#", ""); return [parseInt(h.substr(0, 2), 16), parseInt(h.substr(2, 2), 16), parseInt(h.substr(4, 2), 16)]; }
  function rgbToHex(r) { return "#" + r.map(function (v) { v = Math.max(0, Math.min(255, Math.round(v))); return (v < 16 ? "0" : "") + v.toString(16); }).join(""); }
  function mix(hex, to, t) { var a = hexToRgb(hex), b = hexToRgb(to); return rgbToHex(a.map(function (v, i) { return v + (b[i] - v) * t; })); }
  function lum(hex) { var c = hexToRgb(hex).map(function (v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }); return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]; }
  function palette(hex) {
    if (!/^#[0-9a-fA-F]{6}$/.test(hex || "")) hex = "#e8774a";
    var light = lum(hex) > 0.45;
    return { c: hex, c2: mix(hex, "#ffffff", 0.28), c3: mix(hex, "#000000", 0.2), on: light ? "#1d1a17" : "#ffffff", soft: mix(hex, "#ffffff", 0.88) };
  }

  // ---------------------------------------------------------------- drawings
  function eyes(x1, x2, y, r) {
    return '<g class="eye"><circle cx="' + x1 + '" cy="' + y + '" r="' + r + '" fill="#2a1a10"/><circle cx="' + (x1 + r * 0.35) + '" cy="' + (y - r * 0.35) + '" r="' + (r * 0.36) + '" fill="#fff"/></g>' +
           '<g class="eye r"><circle cx="' + x2 + '" cy="' + y + '" r="' + r + '" fill="#2a1a10"/><circle cx="' + (x2 + r * 0.35) + '" cy="' + (y - r * 0.35) + '" r="' + (r * 0.36) + '" fill="#fff"/></g>';
  }
  function mouth(cx, cy, w) {
    return '<g class="smile"><path d="M' + (cx - w) + ' ' + cy + ' q' + w + ' ' + (w * 0.9) + ' ' + (2 * w) + ' 0" stroke="#3b2416" stroke-width="' + Math.max(2.2, w * 0.38) + '" fill="none" stroke-linecap="round"/></g>' +
           '<g class="open"><path d="M' + (cx - w * 0.85) + ' ' + (cy - 1) + ' Q' + cx + ' ' + (cy + w * 1.7) + ' ' + (cx + w * 0.85) + ' ' + (cy - 1) + ' Z" fill="#5a1f1f"/>' +
           '<ellipse cx="' + cx + '" cy="' + (cy + w * 0.8) + '" rx="' + (w * 0.42) + '" ry="' + (w * 0.24) + '" fill="#ff6b81"/></g>';
  }
  function cheeks(x1, x2, y, rx) {
    return '<ellipse cx="' + x1 + '" cy="' + y + '" rx="' + rx + '" ry="' + (rx * 0.6) + '" fill="#ff8fa3" opacity=".5"/><ellipse cx="' + x2 + '" cy="' + y + '" rx="' + rx + '" ry="' + (rx * 0.6) + '" fill="#ff8fa3" opacity=".5"/>';
  }
  function svg(vb, inner, cls) { return '<svg class="art ' + (cls || "") + '" viewBox="' + vb + '" aria-hidden="true">' + inner + "</svg>"; }

  function bearHeadParts() {
    return '<g class="ear"><circle cx="27" cy="31" r="16" fill="#a86e40"/><circle cx="27" cy="31" r="8" fill="#f3d6b6"/></g>' +
      '<g class="ear r"><circle cx="93" cy="31" r="16" fill="#a86e40"/><circle cx="93" cy="31" r="8" fill="#f3d6b6"/></g>' +
      '<circle cx="60" cy="63" r="42" fill="#c48a5a"/>' +
      '<path d="M22 46 Q60 6 98 46 Q60 34 22 46Z" fill="var(--c)"/>' +
      '<circle cx="45" cy="30" r="2.6" fill="#fff" opacity=".9"/><circle cx="60" cy="25" r="2.6" fill="#fff" opacity=".9"/><circle cx="75" cy="30" r="2.6" fill="#fff" opacity=".9"/>' +
      '<path d="M96 44 l12 -4 -4 10z" fill="var(--c2)"/>' + cheeks(33, 87, 75, 8) + eyes(44, 76, 58, 5.6) +
      '<ellipse cx="60" cy="80" rx="20" ry="15" fill="#f3d6b6"/><ellipse cx="60" cy="73" rx="7" ry="5" fill="#3b2416"/>' +
      '<g class="smile"><path d="M60 78 q-5 8 -11 3 M60 78 q5 8 11 3" stroke="#3b2416" stroke-width="2.4" fill="none" stroke-linecap="round"/><path d="M55 86 q5 5 10 0" fill="#ff6b81"/></g>' +
      '<g class="open"><path d="M51 80 Q60 96 69 80 Z" fill="#5a1f1f"/><ellipse cx="60" cy="87" rx="4.6" ry="2.4" fill="#ff6b81"/></g>';
  }
  function bearFull() {
    return svg("0 0 140 160",
      '<ellipse cx="52" cy="150" rx="15" ry="8" fill="#a86e40"/><ellipse cx="88" cy="150" rx="15" ry="8" fill="#a86e40"/>' +
      '<ellipse cx="52" cy="151" rx="7" ry="4" fill="#f3d6b6"/><ellipse cx="88" cy="151" rx="7" ry="4" fill="#f3d6b6"/>' +
      '<g class="armL"><ellipse cx="36" cy="114" rx="10" ry="19" fill="#c48a5a"/>' +
        '<rect x="16" y="118" width="13" height="22" rx="3" fill="#6ec6ff"/><rect x="19" y="123" width="7" height="9" rx="1.5" fill="#fff" opacity=".85"/>' +
        '<rect x="17" y="110" width="11" height="9" rx="2" fill="#fff"/><rect x="10" y="111" width="8" height="4" rx="1.5" fill="#fff"/>' +
        '<path d="M26 113 q5 1 4 7" stroke="#fff" stroke-width="2.5" fill="none" stroke-linecap="round"/><circle cx="31" cy="128" r="7" fill="#c48a5a"/>' +
        '<circle class="mist" cx="7" cy="113" r="3.2" fill="#bfe6ff"/><circle class="mist m2" cx="6" cy="110" r="2.5" fill="#d6f0ff"/><circle class="mist m3" cx="7" cy="116" r="2.1" fill="#e8f7ff"/></g>' +
      '<g class="armR"><ellipse cx="104" cy="114" rx="10" ry="19" fill="#c48a5a"/><ellipse cx="105" cy="128" rx="7.5" ry="7" fill="#f3d6b6"/></g>' +
      '<ellipse cx="70" cy="118" rx="33" ry="31" fill="#c48a5a"/><ellipse cx="70" cy="123" rx="20" ry="18" fill="#f3d6b6"/>' +
      '<path d="M70 121 c-3-4-9-2-7 3 c1 3 7 6 7 6 s6-3 7-6 c2-5-4-7-7-3z" fill="var(--c)" opacity=".9"/>' +
      '<g transform="translate(10,-6)"><g class="headG">' + bearHeadParts() + "</g></g>", "full");
  }

  var STYLES = {
    classic: {
      name: "كلاسيك", art: function () {
        return svg("0 0 120 120", '<circle cx="60" cy="60" r="56" fill="var(--c)"/>' +
          '<path d="M30 38h60a12 12 0 0 1 12 12v26a12 12 0 0 1-12 12H58l-16 14v-14H30a12 12 0 0 1-12-12V50a12 12 0 0 1 12-12z" fill="var(--on)"/>' +
          '<circle class="tdot d1" cx="45" cy="63" r="5.5" fill="var(--c)"/><circle class="tdot d2" cx="60" cy="63" r="5.5" fill="var(--c)"/><circle class="tdot d3" cx="75" cy="63" r="5.5" fill="var(--c)"/>');
      }
    },
    pill: { name: "زرار اسألنا", art: function () { return STYLES.classic.art(); }, pill: true },
    agent: {
      name: "خدمة عملاء", art: function () {
        return svg("0 0 120 120", '<circle cx="60" cy="60" r="56" fill="var(--c)"/>' +
          '<path d="M26 108c4-20 18-30 34-30s30 10 34 30" fill="var(--on)" opacity=".95"/>' +
          '<circle cx="60" cy="52" r="22" fill="#ffd9b8"/>' +
          '<path d="M38 50c0-14 10-22 22-22s22 8 22 22c-6-6-14-9-22-9s-16 3-22 9z" fill="#3b2416"/>' +
          eyes(52, 68, 53, 2.8) + cheeks(47, 73, 60, 3.6) + mouth(60, 61, 5) +
          '<path d="M34 54a26 26 0 0 1 52 0" stroke="var(--c3)" stroke-width="4" fill="none"/>' +
          '<rect x="30" y="49" width="8" height="15" rx="4" fill="var(--c3)"/><rect x="82" y="49" width="8" height="15" rx="4" fill="var(--c3)"/>' +
          '<path d="M86 63q0 12-15 13" stroke="var(--c3)" stroke-width="3" fill="none"/><circle cx="70" cy="76" r="3.2" fill="var(--c3)"/>', "face");
      }
    },
    minimal: {
      name: "بسيط", art: function () {
        return svg("0 0 120 120", '<rect x="8" y="8" width="104" height="104" rx="32" fill="var(--c)"/>' +
          '<path d="M36 44h48M36 60h48M36 76h28" stroke="var(--on)" stroke-width="7" stroke-linecap="round"/>');
      }
    },
    bear: {
      name: "دب لطيف", art: function () { return svg("0 0 120 120", bearHeadParts(), "face"); }, launcher: bearFull, tall: true
    },
    cat: {
      name: "قطقوطة", art: function (big) {
        return svg("0 0 120 120",
          (big ? '<path class="tail" d="M90 100 q26 -4 22 -32" stroke="var(--c3)" stroke-width="9" fill="none" stroke-linecap="round"/>' : "") +
          '<path d="M22 50 L28 10 L54 32 Z" fill="var(--c)"/><path d="M98 50 L92 10 L66 32 Z" fill="var(--c)"/>' +
          '<path d="M30 40 L33 20 L46 32 Z" fill="#ffc2cf"/><path d="M90 40 L87 20 L74 32 Z" fill="#ffc2cf"/>' +
          '<ellipse cx="60" cy="66" rx="46" ry="40" fill="var(--c)"/>' +
          '<ellipse cx="60" cy="82" rx="19" ry="13" fill="#fff" opacity=".92"/>' +
          '<g class="eye"><ellipse cx="42" cy="60" rx="9" ry="10" fill="#fff"/><ellipse cx="43" cy="61" rx="5.5" ry="7" fill="#1f2a1f"/><circle cx="45" cy="58" r="2.2" fill="#fff"/></g>' +
          '<g class="eye r"><ellipse cx="78" cy="60" rx="9" ry="10" fill="#fff"/><ellipse cx="79" cy="61" rx="5.5" ry="7" fill="#1f2a1f"/><circle cx="81" cy="58" r="2.2" fill="#fff"/></g>' +
          cheeks(30, 90, 78, 6) + '<path d="M56 75 h8 l-4 5 z" fill="#ff7a9a"/>' +
          '<path d="M14 76h20M16 86l18-5M106 76H86M104 86l-18-5" stroke="#3b2416" stroke-width="2" opacity=".45" stroke-linecap="round"/>' +
          '<g class="smile"><path d="M53 83 q3.5 4 7 0 q3.5 4 7 0" stroke="#3b2416" stroke-width="2.4" fill="none" stroke-linecap="round"/></g>' +
          '<g class="open"><path d="M53 82 Q60 96 67 82 Z" fill="#5a1f1f"/><ellipse cx="60" cy="88" rx="3.4" ry="2" fill="#ff6b81"/></g>', "face");
      }
    },
    blob: {
      name: "بلوبي", art: function () {
        return svg("0 0 120 120",
          '<path d="M40 20 l-8 -14 l14 8z" fill="var(--c3)"/><path d="M80 20 l8 -14 l-14 8z" fill="var(--c3)"/>' +
          '<path class="body" d="M60 12c26 0 46 20 47 46 2 25-9 50-24 53-10 2-15-7-23-7s-13 9-23 7C22 108 11 83 13 58 14 32 34 12 60 12z" fill="var(--c)"/>' +
          '<g class="eye"><circle cx="60" cy="50" r="19" fill="#fff"/><circle cx="62" cy="53" r="9.5" fill="#1b1b1b"/><circle cx="65.5" cy="49" r="3.2" fill="#fff"/></g>' +
          cheeks(30, 90, 70, 7) +
          '<g class="smile"><path d="M44 80 q16 14 32 0" stroke="#3b2416" stroke-width="4" fill="none" stroke-linecap="round"/><path d="M52 85 l3 5 3 -4z" fill="#fff"/></g>' +
          '<g class="open"><path d="M46 79 Q60 104 74 79 Z" fill="#5a1f1f"/><path d="M52 80 l3 5 3 -5z M62 80 l3 5 3 -5z" fill="#fff"/><ellipse cx="60" cy="91" rx="6" ry="3" fill="#ff6b81"/></g>', "face");
      }
    },
    bag: {
      name: "شنطة تسوق", art: function () {
        return svg("0 0 120 120",
          '<path d="M42 42 v-9 a18 18 0 0 1 36 0 v9" stroke="var(--c3)" stroke-width="7" fill="none" stroke-linecap="round"/>' +
          '<path d="M20 42 h80 l-6 64 a8 8 0 0 1 -8 7 H34 a8 8 0 0 1 -8 -7 z" fill="var(--c)"/>' +
          '<path d="M20 42 h80 l-1 9 h-78z" fill="var(--c3)" opacity=".45"/>' +
          eyes(47, 73, 70, 5) + cheeks(38, 82, 82, 6) + mouth(60, 84, 8), "face");
      }
    },
    gift: {
      name: "هدية", art: function () {
        return svg("0 0 120 120",
          '<g class="pop"><path d="M30 26 l2 6 6 2 -6 2 -2 6 -2 -6 -6 -2 6 -2z" fill="var(--c2)"/><path d="M92 20 l2 5 5 2 -5 2 -2 5 -2 -5 -5 -2 5 -2z" fill="var(--c2)"/></g>' +
          '<rect x="18" y="54" width="84" height="56" rx="8" fill="var(--c)"/><rect x="54" y="54" width="12" height="56" fill="var(--on)" opacity=".9"/>' +
          '<g class="lid"><rect x="12" y="40" width="96" height="18" rx="6" fill="var(--c3)"/><rect x="54" y="40" width="12" height="18" fill="var(--on)" opacity=".9"/>' +
          '<path d="M60 40 c-10-18-30-16-26-4 4 8 18 6 26 4z M60 40 c10-18 30-16 26-4 -4 8 -18 6 -26 4z" fill="var(--on)"/></g>');
      }
    },
    star: {
      name: "نجمة", art: function () {
        return svg("0 0 120 120",
          '<path d="M104 12 l3 7 7 3 -7 3 -3 7 -3 -7 -7 -3 7 -3z" class="tw" fill="var(--c2)"/><path d="M14 84 l2 5 5 2 -5 2 -2 5 -2 -5 -5 -2 5 -2z" class="tw t2" fill="var(--c2)"/>' +
          '<path d="M60 12 L72 43 L106 45 L80 66 L89 100 L60 81 L31 100 L40 66 L14 45 L48 43 Z" fill="var(--c)" stroke="var(--c)" stroke-width="10" stroke-linejoin="round"/>' +
          eyes(51, 69, 58, 4.4) + cheeks(44, 76, 67, 4.5) + mouth(60, 68, 6), "face");
      }
    },

    // ---- 10 more (2026-11) ----
    orb: {
      name: "ذكاء اصطناعي", art: function () {
        return svg("0 0 120 120",
          '<circle class="ring" cx="60" cy="60" r="52" fill="none" stroke="var(--c)" stroke-width="3"/>' +
          '<circle cx="60" cy="60" r="48" fill="var(--c)"/><circle cx="60" cy="60" r="48" fill="none" stroke="var(--c2)" stroke-width="5" opacity=".6"/>' +
          '<ellipse cx="44" cy="38" rx="17" ry="10" fill="#fff" opacity=".22" transform="rotate(-30 44 38)"/>' +
          '<path class="spark4" d="M60 30 C62 50 70 58 90 60 C70 62 62 70 60 90 C58 70 50 62 30 60 C50 58 58 50 60 30Z" fill="var(--on)"/>' +
          '<path class="tw" d="M86 28 l2 5 5 2 -5 2 -2 5 -2 -5 -5 -2 5 -2z" fill="var(--on)" opacity=".85"/>' +
          '<path class="tw t2" d="M34 82 l1.6 4 4 1.6 -4 1.6 -1.6 4 -1.6 -4 -4 -1.6 4 -1.6z" fill="var(--on)" opacity=".8"/>');
      }
    },
    duo: {
      name: "محادثة", art: function () {
        return svg("0 0 120 120",
          '<g class="b1"><path d="M16 14 h50 a12 12 0 0 1 12 12 v22 a12 12 0 0 1 -12 12 H34 l-14 11 v-11 h-4 a12 12 0 0 1 -12 -12 V26 a12 12 0 0 1 12 -12z" fill="var(--c3)"/>' +
            '<path d="M18 31 h40 M18 44 h24" stroke="var(--on)" stroke-width="6" stroke-linecap="round" opacity=".75"/></g>' +
          '<g class="b2"><path d="M52 46 h52 a12 12 0 0 1 12 12 v24 a12 12 0 0 1 -12 12 h-4 v13 l-15 -13 H52 a12 12 0 0 1 -12 -12 V58 a12 12 0 0 1 12 -12z" fill="var(--c)"/>' +
            '<circle class="tdot d1" cx="62" cy="70" r="5" fill="var(--on)"/><circle class="tdot d2" cx="78" cy="70" r="5" fill="var(--on)"/><circle class="tdot d3" cx="94" cy="70" r="5" fill="var(--on)"/></g>');
      }
    },
    wave: {
      name: "إيد بتسلّم", art: function () {
        return svg("0 0 120 120",
          '<circle cx="60" cy="60" r="56" fill="var(--c)"/>' +
          '<g class="hand" fill="var(--on)">' +
            '<rect x="42" y="30" width="9" height="34" rx="4.5"/><rect x="52" y="22" width="9" height="40" rx="4.5"/>' +
            '<rect x="62" y="24" width="9" height="38" rx="4.5"/><rect x="72" y="32" width="8" height="32" rx="4"/>' +
            '<rect x="28" y="58" width="24" height="10" rx="5" transform="rotate(38 40 63)"/>' +
            '<path d="M42 56 h38 v14 a20 20 0 0 1 -20 20 h0 a18 18 0 0 1 -18 -18z"/>' +
            '<rect x="49" y="86" width="24" height="12" rx="4"/></g>' +
          '<path class="tw" d="M88 34 q6 4 6 12 M94 28 q9 6 9 18" stroke="var(--on)" stroke-width="3.5" fill="none" stroke-linecap="round" opacity=".8"/>');
      }
    },
    robot: {
      name: "روبوت", art: function () {
        return svg("0 0 120 120",
          '<path d="M60 22 V10" stroke="var(--c3)" stroke-width="4" stroke-linecap="round"/><circle class="bulb" cx="60" cy="9" r="6" fill="var(--c2)"/>' +
          '<rect x="7" y="52" width="12" height="26" rx="5" fill="var(--c3)"/><rect x="101" y="52" width="12" height="26" rx="5" fill="var(--c3)"/>' +
          '<rect x="16" y="22" width="88" height="84" rx="26" fill="var(--c)"/>' +
          '<circle cx="28" cy="34" r="3" fill="var(--c3)"/><circle cx="92" cy="34" r="3" fill="var(--c3)"/>' +
          '<rect x="27" y="40" width="66" height="50" rx="17" fill="#1d2433"/>' +
          '<g class="eye"><rect x="39" y="51" width="13" height="15" rx="6.5" fill="#7CFFE0"/></g>' +
          '<g class="eye r"><rect x="68" y="51" width="13" height="15" rx="6.5" fill="#7CFFE0"/></g>' +
          '<g class="smile"><path d="M48 75 q12 8 24 0" stroke="#7CFFE0" stroke-width="4" fill="none" stroke-linecap="round"/></g>' +
          '<g class="open"><rect x="49" y="72" width="22" height="10" rx="5" fill="#7CFFE0"/></g>', "face");
      }
    },
    owl: {
      name: "بومة", art: function () {
        return svg("0 0 120 120",
          '<g class="headG">' +
          '<path class="wing" d="M20 60 q-14 22 4 42 q8 -20 -4 -42z" fill="var(--c3)"/><path class="wing r" d="M100 60 q14 22 -4 42 q-8 -20 4 -42z" fill="var(--c3)"/>' +
          '<path d="M26 34 L20 8 L46 24Z" fill="var(--c3)"/><path d="M94 34 L100 8 L74 24Z" fill="var(--c3)"/>' +
          '<ellipse cx="60" cy="66" rx="42" ry="46" fill="var(--c)"/>' +
          '<ellipse cx="60" cy="88" rx="25" ry="20" fill="#fff" opacity=".85"/>' +
          '<path d="M48 84 q6 6 12 0 q6 6 12 0 M42 96 q6 6 12 0 q6 6 12 0 q6 6 12 0" stroke="var(--c2)" stroke-width="2.6" fill="none" stroke-linecap="round"/>' +
          '<circle cx="42" cy="52" r="17" fill="#fff" stroke="var(--c3)" stroke-width="3"/><circle cx="78" cy="52" r="17" fill="#fff" stroke="var(--c3)" stroke-width="3"/>' +
          '<g class="eye"><circle cx="43" cy="53" r="8.5" fill="#1b1b1b"/><circle cx="46" cy="50" r="3" fill="#fff"/></g>' +
          '<g class="eye r"><circle cx="79" cy="53" r="8.5" fill="#1b1b1b"/><circle cx="82" cy="50" r="3" fill="#fff"/></g>' +
          '<g class="smile"><path d="M53 66 h14 l-7 9z" fill="#f6b73c"/></g>' +
          '<g class="open"><path d="M53 65 h14 l-7 6z" fill="#f6b73c"/><path d="M55 73 h10 l-5 7z" fill="#e0902a"/></g></g>', "face");
      }
    },
    ghost: {
      name: "شبح لطيف", art: function () {
        return svg("0 0 120 120",
          '<g class="floaty">' +
          '<path d="M18 64 q-12 2 -12 12 q9 0 14 -5z" fill="var(--c)"/><path d="M102 64 q12 2 12 12 q-9 0 -14 -5z" fill="var(--c)"/>' +
          '<path d="M18 106 V56 C18 30 36 10 60 10 S102 30 102 56 V106 l-10.5 -8 -10.5 8 -10.5 -8 -10.5 8 -10.5 -8 -10.5 8 -10.5 -8 -10.5 8 Z" fill="var(--c)"/>' +
          '<ellipse cx="40" cy="32" rx="7" ry="11" fill="#fff" opacity=".28" transform="rotate(25 40 32)"/>' +
          eyes(46, 74, 54, 6) + cheeks(36, 84, 66, 6) + mouth(60, 68, 6) + "</g>", "face");
      }
    },
    bunny: {
      name: "أرنوب", art: function () {
        return svg("0 0 120 120",
          '<g class="ear"><ellipse cx="42" cy="28" rx="11" ry="26" fill="var(--c)"/><ellipse cx="42" cy="30" rx="5.5" ry="18" fill="#ffc2cf"/></g>' +
          '<g class="ear r"><ellipse cx="78" cy="28" rx="11" ry="26" fill="var(--c)"/><ellipse cx="78" cy="30" rx="5.5" ry="18" fill="#ffc2cf"/></g>' +
          '<ellipse cx="60" cy="74" rx="42" ry="37" fill="var(--c)"/>' +
          '<ellipse cx="60" cy="88" rx="17" ry="12" fill="#fff" opacity=".9"/>' +
          '<ellipse class="nose" cx="60" cy="81" rx="5" ry="3.6" fill="#ff7a9a"/>' +
          eyes(45, 75, 70, 5.4) + cheeks(32, 88, 84, 6.5) +
          '<g class="smile"><path d="M52 86 q4 5 8 0 q4 5 8 0" stroke="#3b2416" stroke-width="2.4" fill="none" stroke-linecap="round"/><rect x="56.5" y="89" width="7" height="7" rx="1.5" fill="#fff" stroke="#3b2416" stroke-width="1.2"/></g>' +
          '<g class="open"><path d="M53 86 Q60 101 67 86 Z" fill="#5a1f1f"/><rect x="56.5" y="86" width="7" height="5" rx="1.2" fill="#fff"/></g>', "face");
      }
    },
    heart: {
      name: "قلب", art: function () {
        return svg("0 0 120 120",
          '<g class="pop"><path d="M18 22 c-3-4-9-1-6 4 l6 6 6-6 c3-5-3-8-6-4z" fill="var(--c2)"/><path d="M100 16 c-2-3-7-1-5 3 l5 5 5-5 c2-4-3-6-5-3z" fill="var(--c2)"/></g>' +
          '<g class="beat">' +
          '<path d="M60 106 C24 82 10 62 14 42 C18 22 42 14 60 34 C78 14 102 22 106 42 C110 62 96 82 60 106Z" fill="var(--c)"/>' +
          '<ellipse cx="32" cy="40" rx="9" ry="5.5" fill="#fff" opacity=".35" transform="rotate(-35 32 40)"/>' +
          eyes(47, 73, 58, 4.8) + cheeks(37, 83, 69, 5.5) + mouth(60, 69, 6.5) + "</g>", "face");
      }
    },
    cup: {
      name: "كوباية قهوة", art: function () {
        return svg("0 0 120 120",
          '<path class="steam" d="M44 32 q-6 -7 0 -14 q6 -7 0 -14" stroke="var(--c2)" stroke-width="4.5" fill="none" stroke-linecap="round"/>' +
          '<path class="steam s2" d="M58 32 q-6 -7 0 -14 q6 -7 0 -14" stroke="var(--c2)" stroke-width="4.5" fill="none" stroke-linecap="round"/>' +
          '<path class="steam s3" d="M72 32 q-6 -7 0 -14 q6 -7 0 -14" stroke="var(--c2)" stroke-width="4.5" fill="none" stroke-linecap="round"/>' +
          '<ellipse cx="58" cy="110" rx="48" ry="7" fill="var(--c3)" opacity=".45"/>' +
          '<path d="M91 52 h6 a13 13 0 0 1 0 26 h-9" stroke="var(--c3)" stroke-width="7.5" fill="none" stroke-linecap="round"/>' +
          '<path d="M22 42 h72 v34 a30 30 0 0 1 -30 30 h-12 a30 30 0 0 1 -30 -30z" fill="var(--c)"/>' +
          '<rect x="18" y="37" width="80" height="11" rx="5.5" fill="var(--c3)"/>' +
          eyes(46, 70, 68, 4.8) + cheeks(37, 79, 79, 5.5) + mouth(58, 80, 6.5), "face");
      }
    },
    cart: {
      name: "عربية تسوق", art: function () {
        return svg("0 0 120 120",
          '<g class="cartB">' +
          '<rect x="38" y="12" width="20" height="22" rx="4" fill="var(--c2)"/><path d="M38 20 h20" stroke="var(--c3)" stroke-width="3" opacity=".5"/>' +
          '<circle cx="84" cy="22" r="11" fill="var(--c2)"/><path d="M84 11 q4 -6 9 -5" stroke="#4caf50" stroke-width="3" fill="none" stroke-linecap="round"/>' +
          '<path d="M6 20 h14 l7 12" stroke="var(--c3)" stroke-width="6" fill="none" stroke-linecap="round" stroke-linejoin="round"/>' +
          '<path d="M22 32 h88 l-10 46 a9 9 0 0 1 -9 7 H40 a9 9 0 0 1 -9 -7z" fill="var(--c)"/>' +
          eyes(55, 79, 52, 5) + cheeks(46, 88, 63, 5.5) + mouth(67, 63, 7) +
          '<path d="M36 94 h60" stroke="var(--c3)" stroke-width="5" stroke-linecap="round"/>' +
          '<g class="wheel"><circle cx="44" cy="105" r="9" fill="var(--c3)"/><path d="M44 97 v16 M36 105 h16" stroke="#fff" stroke-width="2.2" opacity=".7"/><circle cx="44" cy="105" r="3" fill="#fff"/></g>' +
          '<g class="wheel"><circle cx="88" cy="105" r="9" fill="var(--c3)"/><path d="M88 97 v16 M80 105 h16" stroke="#fff" stroke-width="2.2" opacity=".7"/><circle cx="88" cy="105" r="3" fill="#fff"/></g></g>', "face");
      }
    }
  };
  function styleOf(k) { return STYLES[k] ? k : "bear"; }

  // ---------------------------------------------------------------- styles (CSS)
  var CSS = `
:host{all:initial}
*{box-sizing:border-box}
.w{--bg:#fff7ef;--card:#fff;--ink:#2b1d14;--muted:#8a7565;--line:#efe3d6;--bot:#faf5f0;--shadow:0 22px 60px rgba(40,25,10,.22);
  font:15px/1.65 system-ui,-apple-system,"Segoe UI",Tahoma,sans-serif;color:var(--ink);direction:rtl;text-align:right;-webkit-font-smoothing:antialiased}
.w.dark{--bg:var(--dbg,#1d1611);--card:var(--dcard,#241b15);--ink:#f3f0ed;--muted:#aaa49e;--line:var(--dline,#3a2d23);--bot:var(--dbot,#30241b);--shadow:0 22px 60px rgba(0,0,0,.55)}
.w.dark .badge{box-shadow:0 0 0 3px var(--bg)}
button{font:inherit;cursor:pointer;color:inherit}

/* ----- page mode: the chat link ----- */
.w.page{position:fixed;inset:0;display:grid;place-items:center;padding:16px;
  background:radial-gradient(900px 500px at 85% 110%,var(--soft),transparent 65%),radial-gradient(700px 420px at 10% -10%,var(--soft),transparent 60%),var(--bg)}
.w.page.dark{background:radial-gradient(900px 500px at 85% 110%,var(--dbot,#33241a),transparent 65%),var(--bg)}
.w.page .chat{position:relative;inset:auto;right:auto;bottom:auto;width:min(480px,100%);height:min(760px,calc(100dvh - 32px));transform:none}
.w.page .close,.w.page .launcher{display:none}
.w.page .chat{animation:pageIn .6s cubic-bezier(.34,1.56,.64,1) both}
@keyframes pageIn{from{opacity:0;transform:translateY(24px) scale(.96)}to{opacity:1;transform:none}}

/* ----- preview of a website behind the bubble (dashboard) ----- */
.w.site{position:fixed;inset:0;background:#f4f1ee}
.w.site:before{content:"";position:absolute;inset:16px 16px auto 16px;height:46px;border-radius:12px;background:#fff;box-shadow:0 2px 8px rgba(0,0,0,.05)}
.w.site:after{content:"";position:absolute;inset:80px 16px 40% 16px;border-radius:16px;background:linear-gradient(#ebe6e0,#ebe6e0) 0 0/60% 18px no-repeat,linear-gradient(#ebe6e0,#ebe6e0) 0 34px/85% 12px no-repeat,linear-gradient(#ebe6e0,#ebe6e0) 0 58px/70% 12px no-repeat,linear-gradient(#fff,#fff) 0 100px/48% 160px no-repeat,linear-gradient(#fff,#fff) 100% 100px/48% 160px no-repeat}
.w.site.dark{background:#121315}
.w.site.dark:before{background:#1e1f23;box-shadow:none}
.w.site.dark:after{background:linear-gradient(#2a2b30,#2a2b30) 0 0/60% 18px no-repeat,linear-gradient(#2a2b30,#2a2b30) 0 34px/85% 12px no-repeat,linear-gradient(#2a2b30,#2a2b30) 0 58px/70% 12px no-repeat,linear-gradient(#1e1f23,#1e1f23) 0 100px/48% 160px no-repeat,linear-gradient(#1e1f23,#1e1f23) 100% 100px/48% 160px no-repeat}

/* ----- drawings ----- */
.art{display:block;width:100%;height:100%;overflow:visible}
.art .eye{transform-box:fill-box;transform-origin:center;animation:blink 4.2s infinite}
.art .eye.r{animation-delay:.05s}
.art .ear{transform-box:fill-box;transform-origin:50% 90%;animation:ear 6s ease-in-out infinite}
.art .ear.r{animation-delay:.3s}
@keyframes blink{0%,91%,100%{transform:scaleY(1)}94%{transform:scaleY(.08)}}
@keyframes ear{0%,80%,100%{transform:rotate(0)}85%{transform:rotate(-12deg)}90%{transform:rotate(8deg)}}
.art .smile{transition:opacity .12s}
.art .open{transform-box:fill-box;transform-origin:50% 0;transform:scaleY(.1);opacity:0;transition:opacity .12s}
.talking .art .open{opacity:1;animation:talk .8s linear infinite}
.talking .art .smile{opacity:0}
@keyframes talk{0%{transform:scaleY(.15)}10%{transform:scaleY(.95)}22%{transform:scaleY(.3)}34%{transform:scaleY(1.1)}46%{transform:scaleY(.2)}58%{transform:scaleY(.8)}70%{transform:scaleY(.35)}82%{transform:scaleY(1)}100%{transform:scaleY(.15)}}
.av.talking>.art,.hdr.talking>.art{animation:nod .7s ease-in-out infinite}
@keyframes nod{0%,100%{transform:rotate(0)}25%{transform:rotate(-6deg) translateY(-1px)}75%{transform:rotate(6deg) translateY(1px)}}
.art .eye{transition:translate .25s}
.peek .art .eye{translate:-1.5px 3px}
/* classic typing dots */
.art .tdot{transform-box:fill-box;transform-origin:center;animation:tdot 1.6s ease-in-out infinite}
.art .d2{animation-delay:.18s}.art .d3{animation-delay:.36s}
.talking .art .tdot{animation-duration:.7s}
@keyframes tdot{0%,60%,100%{transform:translateY(0)}30%{transform:translateY(-5px)}}
/* bear */
.art .headG{transform-box:fill-box;transform-origin:50% 90%;animation:tilt 7s ease-in-out infinite}
.talking .art .headG{animation:nod .6s ease-in-out infinite}
@keyframes tilt{0%,70%,100%{transform:rotate(0)}78%{transform:rotate(-7deg)}88%{transform:rotate(5deg)}}
.art .armR{transform-box:view-box;transform-origin:100px 98px;animation:waveArm 5.5s ease-in-out infinite;transition:transform .3s}
.art .armL{transform-box:view-box;transform-origin:40px 98px;animation:sprayArm 7s ease-in-out 1.2s infinite;transition:transform .3s}
@keyframes waveArm{0%,40%,100%{transform:rotate(0)}48%{transform:rotate(-150deg)}54%{transform:rotate(-118deg)}60%{transform:rotate(-155deg)}66%{transform:rotate(-118deg)}72%{transform:rotate(-150deg)}82%{transform:rotate(0)}}
@keyframes sprayArm{0%,50%,100%{transform:rotate(0)}58%{transform:rotate(62deg)}61%{transform:rotate(56deg)}64%{transform:rotate(62deg)}67%{transform:rotate(56deg)}70%{transform:rotate(62deg)}80%{transform:rotate(60deg)}90%{transform:rotate(0)}}
.art .mist{opacity:0;transform-box:fill-box;transform-origin:center;animation:mist 7s ease-out 1.2s infinite}
.art .m2{animation-delay:1.45s}.art .m3{animation-delay:1.7s}
@keyframes mist{0%,58%{opacity:0;transform:translate(0,0) scale(.3)}61%{opacity:.95;transform:translate(-3px,0) scale(.8)}72%{opacity:0;transform:translate(-18px,-3px) scale(2)}100%{opacity:0}}
.talking .art .armR{animation:gesture 1.1s ease-in-out infinite}
@keyframes gesture{0%,100%{transform:rotate(-20deg)}50%{transform:rotate(-60deg)}}
/* cat tail, blob wobble, gift lid, star twinkle */
.art .tail{transform-box:fill-box;transform-origin:0% 100%;animation:tail 2.4s ease-in-out infinite}
@keyframes tail{0%,100%{transform:rotate(0)}50%{transform:rotate(-16deg)}}
.st-blob .art .body{transform-box:fill-box;transform-origin:50% 100%;animation:wob 3s ease-in-out infinite}
@keyframes wob{0%,100%{transform:scale(1,1)}25%{transform:scale(1.05,.95)}50%{transform:scale(.97,1.04)}75%{transform:scale(1.03,.97)}}
.art .lid{transform-box:fill-box;transform-origin:50% 100%;animation:lid 4s ease-in-out infinite}
@keyframes lid{0%,60%,100%{transform:none}68%{transform:translateY(-12px) rotate(-6deg)}76%{transform:translateY(-8px) rotate(4deg)}84%{transform:none}}
.art .pop{opacity:0;animation:popS 4s ease-in-out infinite}
@keyframes popS{0%,62%,100%{opacity:0;transform:scale(.4)}70%{opacity:1;transform:scale(1.15)}82%{opacity:0;transform:scale(.6)}}
.art .tw{transform-box:fill-box;transform-origin:center;animation:twk 2.2s ease-in-out infinite}
.art .t2{animation-delay:1.1s}
@keyframes twk{0%,100%{transform:scale(.6) rotate(0);opacity:.6}50%{transform:scale(1.2) rotate(45deg);opacity:1}}
/* orb, duo, wave, robot, owl, ghost, bunny, heart, cup, cart */
.art .ring{transform-box:fill-box;transform-origin:center;animation:xRing 2.6s ease-out infinite}
@keyframes xRing{0%{transform:scale(.9);opacity:.7}100%{transform:scale(1.16);opacity:0}}
.art .spark4{transform-box:fill-box;transform-origin:center;animation:xSpk 4s ease-in-out infinite}
.talking .art .spark4{animation-duration:.9s}
@keyframes xSpk{0%,100%{transform:rotate(0) scale(1)}50%{transform:rotate(90deg) scale(.8)}}
.art .b1{animation:xBob 2.4s ease-in-out infinite}.art .b2{animation:xBob 2.4s ease-in-out 1.2s infinite}
@keyframes xBob{0%,100%{transform:translateY(0)}50%{transform:translateY(-5px)}}
.art .hand{transform-box:view-box;transform-origin:60px 98px;animation:xWave 3s ease-in-out infinite}
.talking .art .hand{animation-duration:1.1s}
@keyframes xWave{0%,55%,100%{transform:rotate(0)}10%{transform:rotate(-18deg)}20%{transform:rotate(14deg)}30%{transform:rotate(-14deg)}40%{transform:rotate(10deg)}}
.art .bulb{animation:xBulb 1.8s ease-in-out infinite}
.talking .art .bulb{animation-duration:.45s}
@keyframes xBulb{0%,100%{opacity:.4}50%{opacity:1}}
.art .wing{transform-box:fill-box;transform-origin:50% 8%;animation:xFlap 3.4s ease-in-out infinite}
.art .wing.r{animation-name:xFlapR}
.talking .art .wing{animation-duration:.8s}
@keyframes xFlap{0%,70%,100%{transform:rotate(0)}78%{transform:rotate(16deg)}86%{transform:rotate(-5deg)}}
@keyframes xFlapR{0%,70%,100%{transform:rotate(0)}78%{transform:rotate(-16deg)}86%{transform:rotate(5deg)}}
.art .floaty{animation:xFloat 3.2s ease-in-out infinite}
@keyframes xFloat{0%,100%{transform:translateY(0)}50%{transform:translateY(-6px)}}
.art .nose{transform-box:fill-box;transform-origin:center;animation:xTwitch 3s ease-in-out infinite}
@keyframes xTwitch{0%,78%,100%{transform:scale(1)}82%{transform:scale(1.35,.75)}86%{transform:scale(1)}90%{transform:scale(1.35,.75)}94%{transform:scale(1)}}
.art .beat{transform-box:fill-box;transform-origin:center;animation:xBeat 1.6s ease-in-out infinite}
.talking .art .beat{animation-duration:.8s}
@keyframes xBeat{0%,45%,100%{transform:scale(1)}12%{transform:scale(1.08)}24%{transform:scale(1)}34%{transform:scale(1.05)}}
.art .steam{opacity:0;animation:xSteam 2.7s ease-in-out infinite}
.art .s2{animation-delay:.7s}.art .s3{animation-delay:1.4s}
@keyframes xSteam{0%{opacity:0;transform:translateY(7px)}40%{opacity:.9}100%{opacity:0;transform:translateY(-8px)}}
.art .wheel{transform-box:fill-box;transform-origin:center;animation:xSpin 2.4s linear infinite}
@keyframes xSpin{to{transform:rotate(360deg)}}
.art .cartB{animation:xRoll 3s ease-in-out infinite}
.talking .art .cartB{animation-duration:.9s}
@keyframes xRoll{0%,100%{transform:translateX(0)}50%{transform:translateX(-4px)}}

/* ----- launcher (bubble mode) ----- */
.launcher{position:fixed;right:max(20px,env(safe-area-inset-right));bottom:max(20px,env(safe-area-inset-bottom));z-index:2147483000;transition:transform .35s cubic-bezier(.34,1.56,.64,1),opacity .25s}
.launcher.hide{transform:scale(0) rotate(-20deg);opacity:0;pointer-events:none}
.bubbleBtn{position:relative;display:block;width:68px;height:68px;border:0;background:none;padding:0;
  animation:popIn .9s cubic-bezier(.34,1.56,.64,1) both,float 3.2s ease-in-out 1s infinite}
.tall .bubbleBtn{width:92px;height:105px}
.bubbleBtn .art{filter:drop-shadow(0 10px 14px rgba(0,0,0,.22));transition:transform .3s cubic-bezier(.34,1.56,.64,1)}
.st-minimal .bubbleBtn:after{content:"";position:absolute;inset:4px;border-radius:30%;box-shadow:0 0 0 0 var(--c);animation:ring 2.4s ease-out infinite}
@keyframes ring{0%{box-shadow:0 0 0 0 var(--c);opacity:.6}100%{box-shadow:0 0 0 16px var(--c);opacity:0}}
.bubbleBtn.jump .art{animation:jump .45s cubic-bezier(.34,1.56,.64,1)}
.bubbleBtn.jump .armR{animation:none;transform:rotate(-165deg)}
.bubbleBtn.jump .armL{animation:none;transform:rotate(150deg)}
@media (hover:hover){
  .bubbleBtn:hover .art{transform:scale(1.1,.93) translateY(4px)}
  .bubbleBtn:hover .armR{animation:none;transform:rotate(-160deg)}
  .bubbleBtn:hover .armL{animation:none;transform:rotate(140deg)}
}
@keyframes popIn{0%{transform:translateY(140px) scale(.3) rotate(25deg);opacity:0}100%{transform:none;opacity:1}}
@keyframes float{0%,100%{transform:translateY(0)}50%{transform:translateY(-8px)}}
@keyframes jump{0%{transform:none}35%{transform:translateY(-26px) scale(.95,1.08)}70%{transform:translateY(0) scale(1.12,.88)}100%{transform:none}}
.pillBtn{position:relative;display:flex;align-items:center;gap:8px;height:52px;padding:0 18px 0 8px;border:0;border-radius:999px;background:linear-gradient(135deg,var(--c),var(--c2));color:var(--on);
  font-weight:700;font-size:16px;box-shadow:0 10px 26px rgba(0,0,0,.2);animation:popIn .9s cubic-bezier(.34,1.56,.64,1) both,float 3.2s ease-in-out 1s infinite;transition:transform .2s}
.pillBtn .ic{width:38px;height:38px;border-radius:50%;background:var(--on);padding:6px}
.pillBtn .ic .art{filter:none}
@media (hover:hover){.pillBtn:hover{transform:translateY(-2px) scale(1.04)}}
.badge{position:absolute;top:0;left:0;min-width:21px;height:21px;border-radius:11px;background:#ff3b5c;color:#fff;font-size:12px;font-weight:700;
  display:grid;place-items:center;box-shadow:0 0 0 3px #fff;animation:badge 1.6s 1.6s ease-in-out infinite both}
@keyframes badge{0%,100%{transform:scale(1)}50%{transform:scale(1.18)}}
.hello{position:absolute;bottom:calc(100% + 12px);right:0;width:max-content;max-width:min(240px,calc(100vw - 40px));background:var(--card);color:var(--ink);
  padding:10px 30px 10px 14px;border-radius:16px 16px 4px 16px;box-shadow:var(--shadow);font-size:14px;line-height:1.5;transform-origin:bottom right;
  animation:helloIn .55s 1.3s cubic-bezier(.34,1.56,.64,1) both}
.hello.swap{animation:helloSwap .45s cubic-bezier(.34,1.56,.64,1)}
.hello[hidden]{display:none}
.hello .x{position:absolute;top:4px;right:6px;border:0;background:none;color:var(--muted);font-size:16px;line-height:1;padding:2px}
/* the visitor can drag the bubble; let go and it snaps to the nearest side */
.lb{touch-action:none;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none;cursor:grab}
.launcher.moved{transition:transform .35s cubic-bezier(.34,1.56,.64,1),opacity .25s,left .4s cubic-bezier(.34,1.56,.64,1),top .4s cubic-bezier(.34,1.56,.64,1)}
.launcher.drag{transition:transform .35s cubic-bezier(.34,1.56,.64,1),opacity .25s}
.launcher.drag .lb{cursor:grabbing}
.launcher.drag .art{transform:scale(1.08) rotate(-6deg)}
.launcher.lside .hello{right:auto;left:0;border-radius:16px 16px 16px 4px;transform-origin:bottom left}
.launcher.high .hello{bottom:auto;top:calc(100% + 12px);border-radius:4px 16px 16px 16px;transform-origin:top right}
.launcher.high.lside .hello{border-radius:16px 4px 16px 16px;transform-origin:top left}
.w.lside .chat{right:auto;left:max(20px,env(safe-area-inset-left));transform-origin:bottom left}
@keyframes helloIn{0%{opacity:0;transform:scale(.3) translateY(20px)}100%{opacity:1;transform:none}}
@keyframes helloSwap{0%{transform:scale(.85)}100%{transform:none}}

/* ----- chat window ----- */
.chat{position:fixed;right:max(20px,env(safe-area-inset-right));bottom:max(20px,env(safe-area-inset-bottom));z-index:2147483001;
  width:min(400px,calc(100vw - 40px));height:min(660px,calc(100vh - 40px));height:min(660px,calc(100dvh - 40px));
  background:var(--card);border-radius:24px;box-shadow:var(--shadow);display:flex;flex-direction:column;overflow:hidden;transform-origin:bottom right;
  transition:transform .5s cubic-bezier(.34,1.56,.64,1),opacity .25s ease}
.chat.closed{transform:scale(.15) translateY(60px);opacity:0;pointer-events:none}
.ch{display:flex;align-items:center;gap:10px;padding:12px 14px;color:var(--on);background:linear-gradient(120deg,var(--c),var(--c2),var(--c));background-size:200% 100%;animation:sheen 8s linear infinite}
@keyframes sheen{to{background-position:200% 0}}
.hdr{width:46px;height:46px;flex:none;background:rgba(255,255,255,.28);border-radius:50%;padding:4px}
.hdr.wiggle{animation:wiggle .6s ease}
@keyframes wiggle{0%,100%{transform:rotate(0)}25%{transform:rotate(-14deg)}50%{transform:rotate(10deg)}75%{transform:rotate(-6deg)}}
.who{flex:1;display:flex;flex-direction:column;line-height:1.3;min-width:0}
.who b{font-size:16px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.st{font-size:12px;opacity:.95;display:flex;align-items:center;gap:6px}
.dot{width:8px;height:8px;border-radius:50%;background:#ddd;display:inline-block}
.dot.ok{background:#7CFFB2;animation:ping 2s infinite}
.dot.bad{background:#ffb4b4}
@keyframes ping{0%{box-shadow:0 0 0 0 rgba(124,255,178,.7)}70%{box-shadow:0 0 0 7px rgba(124,255,178,0)}100%{box-shadow:0 0 0 0 rgba(124,255,178,0)}}
.close{border:0;background:rgba(255,255,255,.2);color:var(--on);width:34px;height:34px;border-radius:50%;display:grid;place-items:center;transition:transform .2s}
@media (hover:hover){.close:hover{transform:rotate(90deg)}}
.thm{flex:none;border:0;background:rgba(255,255,255,.2);color:var(--on);width:34px;height:34px;border-radius:50%;display:grid;place-items:center;overflow:hidden;transition:background .2s}
@media (hover:hover){.thm:hover{background:rgba(255,255,255,.32)}}
.thm svg{width:17px;height:17px;grid-area:1/1;transition:transform .5s cubic-bezier(.34,1.56,.64,1),opacity .3s}
.thm .moon{transform:translateY(130%) rotate(-90deg);opacity:0}
.w.dark .thm .sun{transform:translateY(-130%) rotate(90deg);opacity:0}
.w.dark .thm .moon{transform:none;opacity:1}
.w.page .chat,.w.page .msgs,.w.page form,.w.page .brand,.w.page .a,.w.page .chip,.w.page .q{transition:background-color .35s,border-color .35s,color .35s}
.w.dark .err{background:#3a1d1b;color:#ffb4ab}
.w.dark .a.done::before{background:linear-gradient(105deg,transparent 35%,rgba(255,255,255,.1) 50%,transparent 65%)}
.w.dark .msgs{scrollbar-color:#4a3a2e transparent}
.msgs{flex:1;overflow-y:auto;overflow-x:hidden;overscroll-behavior:contain;padding:16px 14px;display:flex;flex-direction:column;gap:12px;scroll-behavior:smooth}
.row{display:flex;align-items:flex-end;gap:8px;position:relative}
.row.me{justify-content:flex-start;animation:inMe .45s cubic-bezier(.34,1.56,.64,1) both}
.row.bot{justify-content:flex-end;animation:inBot .5s cubic-bezier(.34,1.56,.64,1) both}
@keyframes inMe{0%{opacity:0;transform:translateX(30px) scale(.8)}100%{opacity:1;transform:none}}
@keyframes inBot{0%{opacity:0;transform:translateX(-30px) translateY(10px) scale(.8)}100%{opacity:1;transform:none}}
.av{width:34px;height:34px;flex:none}
.av.thinking{animation:think 1.2s ease-in-out infinite}
@keyframes think{0%,100%{transform:rotate(0)}50%{transform:rotate(-12deg) translateY(-2px)}}
.m{max-width:78%;padding:10px 14px;border-radius:18px;word-wrap:break-word;position:relative;overflow:hidden}
.u{background:linear-gradient(135deg,var(--c),var(--c2));color:var(--on);border-bottom-right-radius:6px;white-space:pre-wrap;box-shadow:0 6px 16px rgba(0,0,0,.12)}
.a{background:var(--bot);border:1px solid var(--line);border-bottom-left-radius:6px}
.a p{margin:0 0 6px}.a p:last-child{margin:0}
.a ul{margin:4px 0;padding-inline-start:20px}
.a li{margin:2px 0}
.a h3{font-size:15px;margin:6px 0 4px;color:var(--c3)}
.w.dark .a h3,.w.dark .a strong{color:var(--c2)}
.a strong{color:var(--c3)}
.a.streaming>:last-child::after,.a.streaming>ul:last-child>li:last-child::after{content:"";display:inline-block;width:3px;height:1.05em;margin-inline-start:3px;vertical-align:-3px;border-radius:2px;background:var(--c);animation:caret .8s steps(1) infinite}
.a.streaming>ul:last-child::after{display:none}
@keyframes caret{50%{opacity:0}}
.a.done::before{content:"";position:absolute;inset:0;background:linear-gradient(105deg,transparent 35%,rgba(255,255,255,.55) 50%,transparent 65%);transform:translateX(120%);animation:shine 1.1s .1s ease forwards;pointer-events:none}
@keyframes shine{to{transform:translateX(-120%)}}
.typing{display:inline-flex;gap:5px;padding:4px 2px}
.typing span{width:8px;height:8px;border-radius:50%;background:var(--c);animation:dot 1.1s infinite}
.typing span:nth-child(2){animation-delay:.15s;background:var(--c2)}
.typing span:nth-child(3){animation-delay:.3s}
@keyframes dot{0%,70%,100%{transform:translateY(0);opacity:.45}35%{transform:translateY(-8px);opacity:1}}
.err{align-self:center;background:#fdecea;color:#c0392b;font-size:13px;border-radius:12px;padding:8px 12px;max-width:90%;white-space:pre-wrap;animation:shake .5s}
@keyframes shake{0%,100%{transform:none}20%{transform:translateX(-8px)}40%{transform:translateX(7px)}60%{transform:translateX(-5px)}80%{transform:translateX(3px)}}
.spark{position:absolute;left:30px;bottom:20px;font-size:16px;pointer-events:none;animation:spark .95s ease-out forwards}
@keyframes spark{0%{opacity:1;transform:translate(0,0) scale(.4)}100%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(1.2) rotate(var(--rot))}}
.chips{display:flex;flex-wrap:wrap;gap:6px;padding:0 14px 10px;transition:opacity .3s,max-height .4s;max-height:140px;overflow:hidden}
.chips.gone{opacity:0;max-height:0;padding-bottom:0}
.chips[hidden]{display:none}
.chip{border:1px solid var(--line);background:var(--card);border-radius:999px;padding:5px 12px;font-size:13px;animation:chipIn .45s cubic-bezier(.34,1.56,.64,1) both;transition:transform .2s,border-color .2s}
.chip:nth-child(2){animation-delay:.12s}.chip:nth-child(3){animation-delay:.24s}
@media (hover:hover){.chip:hover{transform:translateY(-2px);border-color:var(--c)}}
@keyframes chipIn{0%{opacity:0;transform:translateY(12px) scale(.8)}100%{opacity:1;transform:none}}
form{display:flex;gap:8px;padding:10px 12px 12px;border-top:1px solid var(--line);background:var(--card)}
.q{flex:1;min-width:0;border:1.5px solid var(--line);border-radius:999px;padding:10px 16px;font:inherit;background:var(--bg);color:var(--ink);outline:none;transition:border-color .2s,box-shadow .2s}
.q:focus{border-color:var(--c);box-shadow:0 0 0 4px color-mix(in srgb,var(--c) 18%,transparent)}
.send{width:44px;height:44px;flex:none;border:0;border-radius:50%;color:var(--on);background:linear-gradient(135deg,var(--c),var(--c2));display:grid;place-items:center;box-shadow:0 6px 14px rgba(0,0,0,.18);transition:transform .2s cubic-bezier(.34,1.56,.64,1)}
@media (hover:hover){.send:hover{transform:scale(1.1) rotate(-8deg)}}
.send:disabled{opacity:.55;cursor:default}
.send svg{width:20px;height:20px;transform:scaleX(-1)}
.brand{font-size:11px;color:var(--muted);text-align:center;padding:0 0 8px;background:var(--card)}

@media (max-width:480px){
  .w.bubble .chat{right:0;left:0;bottom:0;width:100%;height:100vh;height:100dvh;border-radius:0;transform-origin:bottom center}
  .w.page{padding:0}
  .w.page .chat{width:100%;height:100vh;height:100dvh;border-radius:0}
  .ch{padding:max(10px,env(safe-area-inset-top)) 12px 10px}
  .m{max-width:86%}
  .q{font-size:16px}
  .chips{flex-wrap:nowrap;overflow-x:auto;overflow-y:hidden;scrollbar-width:none;-webkit-overflow-scrolling:touch}
  .chips::-webkit-scrollbar{display:none}
  .chip{flex:none}
  .bubbleBtn{width:60px;height:60px}
  .tall .bubbleBtn{width:78px;height:89px}
}
@media (max-height:520px){
  .w.bubble .chat{height:calc(100dvh - 20px);bottom:10px}
  .ch{padding:6px 12px}.hdr{width:36px;height:36px}
}
@media (prefers-reduced-motion:reduce){*,*::before,*::after{animation:none!important;transition:none!important}}
`;

  if (ART_ONLY) {
    window.StoreChatArt = {
      styles: Object.keys(STYLES).map(function (k) { return { id: k, name: STYLES[k].name }; }),
      // Renders one style's drawing into an element, isolated in its own Shadow DOM with the animations.
      render: function (el, id, color) {
        var r = el.shadowRoot || el.attachShadow({ mode: "open" });
        var p = palette(color), S = STYLES[styleOf(id)];
        r.innerHTML = "<style>" + CSS + ":host{display:block}.w{width:100%;height:100%}.box{width:100%;height:100%;display:flex;align-items:center;justify-content:center}.box>svg{width:auto;height:100%;max-width:100%;flex:none}</style>" +
          '<div class="w st-' + styleOf(id) + '" style="--c:' + p.c + ";--c2:" + p.c2 + ";--c3:" + p.c3 + ";--on:" + p.on + '"><div class="box">' +
          (S.pill ? '<div style="display:grid;place-items:center"><span class="pillBtn" style="animation:none;height:40px;font-size:13px;padding:0 12px 0 6px"><span class="ic" style="width:30px;height:30px;padding:4px">' + S.art() + "</span>اسألنا 👋</span></div>"
                  : (S.launcher ? S.launcher() : S.art(true))) + "</div></div>";
      },
      palette: palette
    };
    return;
  }

  // ---------------------------------------------------------------- DOM
  var host = document.createElement("div");
  host.id = "store-chat-" + STORE;
  var root = host.attachShadow ? host.attachShadow({ mode: "open" }) : host;
  var styleEl = document.createElement("style"); styleEl.textContent = CSS; root.appendChild(styleEl);
  var w = document.createElement("div");
  w.className = "w " + MODE + (PREVIEW && MODE === "bubble" ? " site" : "");
  // Light / dark:
  //  - on a website: the chat follows the website itself (its dark/light class or data-theme, otherwise how dark its
  //    background is) and switches live when the website switches. data-theme="light" or "dark" on the script tag forces one.
  //  - chat link page: the visitor's saved choice (sun/moon button), otherwise the device setting.
  //  - dashboard preview: ?theme= and live messages from the dashboard.
  var darkMq = window.matchMedia && matchMedia("(prefers-color-scheme: dark)");
  var FORCED = (me.getAttribute("data-theme") || "").toLowerCase();
  if (FORCED !== "dark" && FORCED !== "light") FORCED = "";
  function savedTheme() { if (PREVIEW) return qs.get("theme"); try { return localStorage.getItem("sb-theme"); } catch (e) { return null; } }
  function setDark(on) {
    w.classList.toggle("dark", !!on);
    if (MODE === "page") document.documentElement.setAttribute("data-theme", on ? "dark" : "light");
  }
  var cvs = null;
  function rgbaOf(css) {   // any CSS color (rgb, hsl, oklch, named...) -> [r, g, b, a] through a 1px canvas
    try {
      cvs = cvs || document.createElement("canvas"); cvs.width = cvs.height = 1;
      var x = cvs.getContext("2d", { willReadFrequently: true });
      x.clearRect(0, 0, 1, 1); x.fillStyle = "rgba(0,0,0,0)"; x.fillStyle = css; x.fillRect(0, 0, 1, 1);
      var d = x.getImageData(0, 0, 1, 1).data; return [d[0], d[1], d[2], d[3] / 255];
    } catch (e) { return null; }
  }
  var DARK_RX = /(^|\s)(dark|dark-mode|dark-theme|theme-dark|is-dark|night|night-mode)(\s|$)/;
  var LIGHT_RX = /(^|\s)(light|light-mode|light-theme|theme-light|is-light)(\s|$)/;
  function hostIsDark() {
    var els = [document.documentElement, document.body], i, el;
    // 1) what the website says about itself
    for (i = 0; i < els.length; i++) {
      el = els[i]; if (!el) continue;
      var t = ["data-theme", "data-bs-theme", "data-color-scheme", "data-mode", "data-color-mode"]
        .map(function (a) { return el.getAttribute(a) || ""; }).join(" ").toLowerCase();
      if (/\bdark\b/.test(t)) return true;
      if (/\blight\b/.test(t)) return false;
      var cls = (typeof el.className === "string" ? el.className : "").toLowerCase();
      if (DARK_RX.test(cls)) return true;
      if (LIGHT_RX.test(cls)) return false;
    }
    // 2) how dark its background really is
    for (i = 1; i >= 0; i--) {
      el = els[i]; if (!el) continue;
      var c = rgbaOf(getComputedStyle(el).backgroundColor);
      if (c && c[3] >= 0.5) return (c[0] * 299 + c[1] * 587 + c[2] * 114) / 1000 < 128;
    }
    // 3) no background at all: the browser paints it from color-scheme
    var cs = (getComputedStyle(document.documentElement).colorScheme || "").toLowerCase();
    return cs.indexOf("dark") >= 0 && (cs.indexOf("light") < 0 || !!(darkMq && darkMq.matches));
  }
  function followHost() {
    var t1, t2;
    function check() { setDark(hostIsDark()); }
    function soon() { clearTimeout(t1); clearTimeout(t2); t1 = setTimeout(check, 30); t2 = setTimeout(check, 450); }  // again after a color transition
    check();
    if (window.MutationObserver) {
      var mo = new MutationObserver(soon), opts = { attributes: true, attributeFilter: ["class", "style", "data-theme", "data-bs-theme", "data-color-scheme", "data-mode", "data-color-mode"] };
      mo.observe(document.documentElement, opts);
      if (document.body) mo.observe(document.body, opts);
      else document.addEventListener("DOMContentLoaded", function () { mo.observe(document.body, opts); soon(); });
    }
    if (darkMq && darkMq.addEventListener) darkMq.addEventListener("change", soon);
    window.addEventListener("load", soon);
  }
  if (FORCED) setDark(FORCED === "dark");
  else if (PREVIEW || MODE === "page") {
    var theme0 = savedTheme(); setDark(theme0 ? theme0 === "dark" : !!(darkMq && darkMq.matches));
    if (darkMq && darkMq.addEventListener) darkMq.addEventListener("change", function (e) { if (!savedTheme()) setDark(e.matches); });
  }
  else followHost();
  w.innerHTML =
    '<div class="launcher"><div class="hello"><span class="helloTxt"></span><button class="x" aria-label="إخفاء">×</button></div>' +
    '<button class="lb bubbleBtn" aria-label="افتح الشات"></button></div>' +
    '<section class="chat' + (MODE === "page" ? "" : " closed") + '" aria-hidden="' + (MODE === "page" ? "false" : "true") + '">' +
      '<div class="ch"><div class="hdr"></div><div class="who"><b class="title"></b><span class="st"><i class="dot"></i><span class="stxt">بيجهّز…</span></span></div>' +
      (MODE === "page" && !PREVIEW ? '<button class="thm" type="button" aria-label="الوضع الفاتح / الغامق" title="الوضع الفاتح / الغامق">' +
        '<svg class="sun" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2.5v2.2M12 19.3v2.2M2.5 12h2.2M19.3 12h2.2M5.3 5.3l1.6 1.6M17.1 17.1l1.6 1.6M5.3 18.7l1.6-1.6M17.1 6.9l1.6-1.6"/></svg>' +
        '<svg class="moon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.5 14.2A8.5 8.5 0 0 1 9.8 3.5a8.5 8.5 0 1 0 10.7 10.7z"/></svg></button>' : "") +
      '<button class="close" aria-label="إغلاق">✕</button></div>' +
      '<div class="msgs"></div><div class="chips" hidden></div>' +
      '<form><input class="q" autocomplete="off" maxlength="600" aria-label="رسالتك"><button class="send" aria-label="إرسال"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M3.4 20.4 21 12 3.4 3.6l-.01 6.53L15 12 3.39 13.87z"/></svg></button></form>' +
    "</section>";
  root.appendChild(w);
  function $(sel) { return w.querySelector(sel); }
  (document.body || document.documentElement).appendChild(host);

  // ---------------------------------------------------------------- apply look
  // Header title: the owner's own chat title if set, otherwise "assistant · store".
  function chatTitle() { return (cfg.title || "").trim() || cfg.name || cfg.botName || "الشات"; }
  function applyLook() {
    var p = palette(cfg.color);
    w.style.setProperty("--c", p.c); w.style.setProperty("--c2", p.c2); w.style.setProperty("--c3", p.c3); w.style.setProperty("--on", p.on); w.style.setProperty("--soft", p.soft);
    // dark surfaces: near-black with a hint of the brand color, so the dark chat matches the brand like the light one
    w.style.setProperty("--dbg", mix(p.c, "#121315", 0.93)); w.style.setProperty("--dcard", mix(p.c, "#1a1b1e", 0.92));
    w.style.setProperty("--dbot", mix(p.c, "#222327", 0.88)); w.style.setProperty("--dline", mix(p.c, "#2e2f34", 0.86));
    var st = styleOf(cfg.style), S = STYLES[st];
    w.className = w.className.replace(/\bst-\S+/g, "").replace(/\btall\b/g, "").trim() + " st-" + st + (S.tall ? " tall" : "");
    var btn = $(".lb");
    if (S.pill) { btn.className = "lb pillBtn"; btn.innerHTML = '<span class="ic">' + S.art() + "</span><span>اسألنا 👋</span>"; }
    else { btn.className = "lb bubbleBtn"; btn.innerHTML = (S.launcher ? S.launcher() : S.art(true)); }
    if (!opened) btn.insertAdjacentHTML("beforeend", '<span class="badge">1</span>');
    $(".hdr").innerHTML = S.art();
    w.querySelectorAll(".row.bot .av").forEach(function (a) { a.innerHTML = S.art(); });
    $(".title").textContent = chatTitle();
    $(".q").placeholder = "اكتب سؤالك هنا...";
    if (MODE === "page") document.title = chatTitle();
  }

  // ---------------------------------------------------------------- helpers
  function esc(s) { return s.replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function inline(s) { return esc(s).replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>").replace(/`([^`]+)`/g, "<code>$1</code>"); }
  function md(text) {
    var out = [], list = false;
    text.split("\n").forEach(function (line) {
      var t = line.trim(), li = t.match(/^([-*•]|\d+[.)])\s+(.*)$/);
      if (li) { if (!list) { out.push("<ul>"); list = true; } out.push("<li>" + inline(li[2]) + "</li>"); return; }
      if (list) { out.push("</ul>"); list = false; }
      var h = t.match(/^#{1,6}\s+(.*)$/);
      if (h) out.push("<h3>" + inline(h[1]) + "</h3>"); else if (t) out.push("<p>" + inline(t) + "</p>");
    });
    if (list) out.push("</ul>");
    return out.join("");
  }
  function scrollDown() { var m = $(".msgs"); m.scrollTop = m.scrollHeight; }
  function setStatus(state, txt) { $(".dot").className = "dot " + (state || ""); $(".stxt").textContent = txt; }
  function addMe(text) {
    var row = document.createElement("div"); row.className = "row me";
    var b = document.createElement("div"); b.className = "m u"; b.textContent = text;
    row.appendChild(b); $(".msgs").appendChild(row); scrollDown();
  }
  function addBot() {
    var row = document.createElement("div"); row.className = "row bot";
    var b = document.createElement("div"); b.className = "m a"; b.innerHTML = '<span class="typing"><span></span><span></span><span></span></span>';
    var av = document.createElement("div"); av.className = "av thinking"; av.innerHTML = STYLES[styleOf(cfg.style)].art();
    row.appendChild(b); row.appendChild(av); $(".msgs").appendChild(row); scrollDown();
    return { row: row, bubble: b, av: av };
  }
  function addErr(text) { var d = document.createElement("div"); d.className = "err"; d.textContent = text; $(".msgs").appendChild(d); scrollDown(); }
  function sparkle(row) {
    var em = ["✨", "💫", "⭐", "💛", "✨"];
    for (var i = 0; i < 9; i++) {
      var s = document.createElement("span"); s.className = "spark"; s.textContent = em[i % em.length];
      var ang = Math.random() * Math.PI - Math.PI, dist = 40 + Math.random() * 50;
      s.style.setProperty("--dx", Math.round(Math.cos(ang) * dist) + "px"); s.style.setProperty("--dy", Math.round(Math.sin(ang) * dist) + "px");
      s.style.setProperty("--rot", Math.round(Math.random() * 120 - 60) + "deg"); s.style.animationDelay = (i * 0.03) + "s";
      row.appendChild(s); (function (el) { setTimeout(function () { el.remove(); }, 1300); })(s);
    }
  }
  function wiggle() { var a = $(".hdr"); a.classList.remove("wiggle"); void a.offsetWidth; a.classList.add("wiggle"); }
  function talk(bot, on) { if (bot) { bot.av.classList.remove("thinking"); bot.av.classList.toggle("talking", on); } $(".hdr").classList.toggle("talking", on); }
  function say(ms) { var b = $(".launcher"); b.classList.add("talking"); clearTimeout(say.t); say.t = setTimeout(function () { b.classList.remove("talking"); }, ms || 1600); }

  function typer(bubble, onFirst) {
    var target = "", shown = 0, done = false, t = null, resolveFn, last = 0, acc = 0;
    var finished = new Promise(function (r) { resolveFn = r; });
    function tick() {
      var now = performance.now(), dt = last ? Math.min(250, now - last) : 16; last = now;
      var remain = target.length - shown;
      if (remain > 0) {
        acc += (35 + remain * 0.8) * dt / 1000;
        var step = Math.floor(acc);
        if (step > 0) { acc -= step; shown = Math.min(target.length, shown + step); bubble.innerHTML = md(target.slice(0, shown)); scrollDown(); }
      }
      if (done && shown >= target.length) { t = null; last = 0; resolveFn(); return; }
      t = setTimeout(tick, 16);
    }
    function kick() { if (!t) t = setTimeout(tick, 16); }
    return {
      push: function (s) { if (!target) { bubble.classList.add("streaming"); if (onFirst) onFirst(); } target += s; kick(); },
      end: function () { done = true; kick(); }, finished: finished, text: function () { return target; }
    };
  }
  function finishBot(bot) { bot.bubble.classList.remove("streaming"); talk(bot, false); bot.av.classList.remove("thinking"); bot.bubble.classList.add("done"); sparkle(bot.row); wiggle(); }

  // ---------------------------------------------------------------- texts
  var RIWAQ = STORE === "riwaq";
  var HELLO = RIWAQ
    ? ["أهلاً! 👋<br>محتاج مساعدة في التنظيف؟", "بقعة عنيدة؟ 😤<br>سيبهالي أنا!", "اسألني عن أي منتج 🫧<br>مش بعضّ والله 😄", "ريحة رطوبة؟ 🤢<br>عندي الحل!"]
    : ["أهلاً! 👋<br>محتاج مساعدة؟", "عندك سؤال؟ 🤔<br>اسألني!", "أنا هنا عشان أساعدك 😊", "اسألني أي حاجة<br>مش بعضّ والله 😄"];
  $(".helloTxt").innerHTML = HELLO[0];
  // Suggested questions come from the store's own settings (none saved = no buttons, instead of questions that don't fit the store).
  function renderChips() {
    var box = $(".chips"), list = (cfg.suggestions || []).filter(function (q) { return q && q.trim(); }).slice(0, 4);
    box.textContent = "";
    list.forEach(function (q) {
      var b = document.createElement("button"); b.className = "chip"; b.type = "button"; b.textContent = q;
      b.onclick = function () { ask(q.replace(/[\u{1F300}-\u{1FAFF}☀-➿]/gu, "").trim()); };
      box.appendChild(b);
    });
    box.hidden = list.length === 0;
  }

  // ---------------------------------------------------------------- config from the server
  var ready = (async function () {
    try {
      var r = await fetch(API, { cache: "no-store" });
      if (r.ok) {
        var c = await r.json();
        cfg.name = c.name || ""; cfg.botName = c.botName || ""; cfg.welcome = c.welcome; cfg.style = c.bubbleStyle || "bear"; cfg.color = c.brandColor || "#e8774a"; cfg.title = c.chatTitle || ""; cfg.suggestions = c.suggestions || [];
        if (PREVIEW) { if (qs.get("style")) cfg.style = qs.get("style"); if (qs.get("color")) cfg.color = qs.get("color"); }
        applyLook(); renderChips(); setStatus("ok", "متصل وجاهز يساعدك");
        return true;
      }
      if (MODE === "bubble" && !PREVIEW && (r.status === 403 || r.status === 404)) {
        // Not set up for this website (domain not added, store stopped or wrong id): stay hidden instead of showing a broken chat.
        host.remove();
        if (window.console) console.warn("[store chat] Hidden: " + (r.status === 404 ? "store id '" + STORE + "' was not found." : "add " + location.origin + " to the store's domains in the dashboard (Add to your store tab), or the store is paused."));
        return false;
      }
      if (PREVIEW) { if (qs.get("style")) cfg.style = qs.get("style"); if (qs.get("color")) cfg.color = qs.get("color"); }
      applyLook();
      if (r.status === 404) { storeMissing = true; setStatus("bad", "المتجر ده مش موجود"); }
      else setStatus("bad", "مش متاح دلوقتي");
    } catch (e) { applyLook(); setStatus("bad", "مش متاح دلوقتي"); }
    return false;
  })();
  applyLook();

  // ---------------------------------------------------------------- chat
  async function ask(q) {
    if (busy || !q) return;
    if (q.length > 600) { addErr("السؤال طويل شوية 😅 اختصره في أقل من 600 حرف."); return; }
    busy = true; $(".send").disabled = true; $(".chips").classList.add("gone");
    addMe(q); history.push({ role: "user", content: q });
    var bot = addBot(), tw = typer(bot.bubble, function () { talk(bot, true); });
    try {
      var r = await fetch(API, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ messages: history.slice(-12), sid: SID }) });
      if (!r.ok) { var e = new Error("http"); e.status = r.status; throw e; }
      if ((r.headers.get("content-type") || "").indexOf("text/event-stream") !== -1 && r.body) {
        var reader = r.body.getReader(), dec = new TextDecoder(), buf = "", evt = "";
        while (true) {
          var chunk = await reader.read(); if (chunk.done) break;
          buf += dec.decode(chunk.value, { stream: true });
          var lines = buf.split("\n"); buf = lines.pop();
          for (var i = 0; i < lines.length; i++) {
            var line = lines[i].replace(/\r$/, "");
            if (line === "") { evt = ""; continue; }
            if (line.charAt(0) === ":") continue;
            if (line.indexOf("event:") === 0) { evt = line.slice(6).trim(); continue; }
            if (line.indexOf("data:") !== 0 || (evt && evt !== "message")) continue;
            var data = line.slice(5).trim(); if (data === "[DONE]") continue;
            try { var j = JSON.parse(data), d = j.choices && j.choices[0] && j.choices[0].delta && j.choices[0].delta.content; if (d) tw.push(d); } catch (x) {}
          }
        }
      } else {
        var j2 = await r.json(); tw.push((j2.choices && j2.choices[0] && j2.choices[0].message && j2.choices[0].message.content) || "");
      }
      if (!tw.text()) throw new Error("empty");
      tw.end(); await tw.finished; finishBot(bot);
      history.push({ role: "assistant", content: tw.text() });
      setStatus("ok", "متصل وجاهز يساعدك");
    } catch (err) {
      bot.row.remove(); history.pop(); talk(null, false);
      var n = cfg.botName || "المساعد", msg = "حصلت مشكلة صغيرة 😕 جرّب تاني بعد شوية، أو كلّم المتجر مباشرة.";
      if (err && err.status === 429) msg = "استنى دقيقة كده 😅 بعتّ رسايل كتير ورا بعض.";
      else if (err && err.status === 402) msg = n + " واخد استراحة الشهر ده 😴 كلّم المتجر مباشرة وهيردّوا عليك.";
      else if (err && err.status === 403) msg = n + " مش متاح دلوقتي، كلّم المتجر مباشرة.";
      else if (err && err.status === 404) msg = "المتجر ده مش موجود.";
      else if (err && (err.status === 502 || err.status === 503)) msg = n + " نايم دلوقتي 😴 جرّب كمان شوية، أو كلّم المتجر مباشرة.";
      addErr(msg); setStatus("bad", "فيه مشكلة");
    } finally { busy = false; $(".send").disabled = false; if (MODE === "page" || opened) $(".q").focus(); }
  }

  async function welcome() {
    var bot = addBot(), tw = typer(bot.bubble, function () { talk(bot, true); });
    await Promise.all([ready, new Promise(function (r) { setTimeout(r, 650); })]);
    if (storeMissing) { bot.row.remove(); addErr("المتجر ده مش موجود. اتأكد من اللينك."); return; }
    var text = cfg.welcome || ("أهلاً بيك" + (cfg.name ? " في **" + cfg.name + "**" : "") + "! 👋\n" + (cfg.botName ? "أنا **" + cfg.botName + "**، " : "") + "عندك أي سؤال؟ اسألني وأنا أساعدك على طول.");
    tw.push(text); tw.end(); await tw.finished; finishBot(bot);
  }

  function openChat() {
    var b = $(".lb"); b.classList.remove("jump"); void b.offsetWidth; b.classList.add("jump");
    setTimeout(function () {
      $(".launcher").classList.add("hide");
      var c = $(".chat"); c.classList.remove("closed"); c.setAttribute("aria-hidden", "false");
      $(".hello").hidden = true;
      var badge = $(".badge"); if (badge) badge.remove();
      setTimeout(function () { $(".q").focus(); }, 350);
      if (!opened) { opened = true; welcome(); }
    }, 240);
  }
  function closeChat() {
    var c = $(".chat"); c.classList.add("closed"); c.setAttribute("aria-hidden", "true");
    setTimeout(function () { $(".launcher").classList.remove("hide"); }, 180);
  }

  var dragEnd = 0;   // a drag ends with a click; don't let that click open the chat
  $(".launcher").addEventListener("click", function (e) { if (e.target.closest(".x")) return; if (Date.now() - dragEnd < 400) return; if (e.target.closest(".lb")) openChat(); });
  $(".x").onclick = function (e) { e.stopPropagation(); $(".hello").hidden = true; };

  // ---------------------------------------------------------------- drag the bubble
  // The visitor drags the bubble where it doesn't cover the store's page; let go and it snaps to the nearest side.
  // The spot is kept for that site (side + distance from the bottom, so it fits other screen sizes). Not saved in previews.
  if (MODE !== "page") (function () {
    var L = $(".launcher"), btn = $(".lb"), M = 14, KEY = "sb-pos", pos = null, drag = null;
    function vw() { return document.documentElement.clientWidth || window.innerWidth; }
    function vh() { return window.innerHeight; }
    function clampX(x) { return Math.max(0, Math.min(vw() - L.offsetWidth, x)); }
    function clampY(y) { return Math.max(M, Math.min(vh() - L.offsetHeight - M, y)); }
    function place(side, y) {
      y = clampY(y);
      L.style.right = L.style.bottom = "auto";
      L.style.left = (side === "l" ? M : vw() - L.offsetWidth - M) + "px"; L.style.top = y + "px";
      L.classList.toggle("lside", side === "l"); w.classList.toggle("lside", side === "l");
      L.classList.toggle("high", y < 150);   // near the top: the "hello" note goes under the bubble
      pos = { s: side, b: Math.round(vh() - y - L.offsetHeight) };
    }
    try { var saved = PREVIEW ? null : JSON.parse(localStorage.getItem(KEY) || "null"); } catch (e) { saved = null; }
    if (saved && (saved.s === "l" || saved.s === "r") && typeof saved.b === "number")
      requestAnimationFrame(function () { place(saved.s, vh() - L.offsetHeight - saved.b); });
    function end(e) {
      if (!drag || e.pointerId !== drag.id) return;
      var was = drag.moved; drag = null;
      if (!was) return;
      dragEnd = Date.now();
      L.classList.remove("drag"); L.classList.add("moved");
      var r = L.getBoundingClientRect();
      place(r.left + r.width / 2 < vw() / 2 ? "l" : "r", r.top);
      if (!PREVIEW) try { localStorage.setItem(KEY, JSON.stringify(pos)); } catch (e2) {}
    }
    btn.addEventListener("pointerdown", function (e) {
      if (e.button) return;
      var r = L.getBoundingClientRect();
      drag = { id: e.pointerId, x0: e.clientX, y0: e.clientY, dx: e.clientX - r.left, dy: e.clientY - r.top, moved: false };
      try { btn.setPointerCapture(e.pointerId); } catch (e2) {}
    });
    btn.addEventListener("pointermove", function (e) {
      if (!drag || e.pointerId !== drag.id) return;
      if (!drag.moved) {
        if (Math.abs(e.clientX - drag.x0) + Math.abs(e.clientY - drag.y0) < 8) return;   // a small wobble is still a tap
        drag.moved = true; L.classList.add("drag"); $(".hello").hidden = true;
      }
      e.preventDefault();
      L.style.right = L.style.bottom = "auto";
      L.style.left = clampX(e.clientX - drag.dx) + "px"; L.style.top = clampY(e.clientY - drag.dy) + "px";
    });
    btn.addEventListener("pointerup", end);
    btn.addEventListener("pointercancel", end);
    window.addEventListener("resize", function () { if (pos) place(pos.s, vh() - L.offsetHeight - pos.b); });
  })();
  $(".close").onclick = closeChat;
  if ($(".thm")) $(".thm").onclick = function () {
    var on = !w.classList.contains("dark");
    try { localStorage.setItem("sb-theme", on ? "dark" : "light"); } catch (e) {}
    setDark(on);
  };
  $(".hdr").onclick = wiggle;
  $("form").onsubmit = function (e) { e.preventDefault(); var q = $(".q").value.trim(); $(".q").value = ""; ask(q); };
  var peekT; $(".q").addEventListener("input", function () { $(".hdr").classList.add("peek"); clearTimeout(peekT); peekT = setTimeout(function () { $(".hdr").classList.remove("peek"); }, 900); });

  if (MODE === "page") { opened = true; welcome(); }
  else {
    var fi = 0;
    setTimeout(function () { say(1800); }, 1400);
    setInterval(function () {
      var h = $(".hello"); if (h.hidden || opened) return;
      fi = (fi + 1) % HELLO.length; $(".helloTxt").innerHTML = HELLO[fi];
      h.classList.remove("swap"); void h.offsetWidth; h.classList.add("swap"); say();
    }, 5000);
  }

  // dashboard: after the owner saves, the store's own floating chat on the same page updates right away
  document.addEventListener("sellybot:look", function (e) {
    var d = e.detail || {};
    if (d.store !== STORE) return;
    if (d.style) cfg.style = d.style; if (d.color) cfg.color = d.color;
    if (typeof d.title === "string") cfg.title = d.title;
    if (d.name) cfg.name = d.name; if (typeof d.botName === "string") cfg.botName = d.botName;
    if (d.suggestions) { cfg.suggestions = d.suggestions; renderChips(); }
    applyLook();
  });

  // dashboard preview: change style/color live without reloading
  if (PREVIEW) window.addEventListener("message", function (e) {
    if (e.origin !== location.origin || !e.data || e.data.type !== "chat-look") return;
    if (e.data.theme) setDark(e.data.theme === "dark");
    if (typeof e.data.title === "string") cfg.title = e.data.title;
    if (e.data.style) cfg.style = e.data.style; if (e.data.color) cfg.color = e.data.color;
    applyLook();
  });
})();
