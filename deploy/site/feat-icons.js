/* An icon for each plan feature. Features are free text the admin writes, so the icon is picked by keywords;
 * anything unknown gets a check mark. Used by pricing.html, dashboard.html (باقتي) and admin.html (plan preview).
 * sbFeatIcon(text) -> '<svg …>' (stroke = currentColor, 24x24). */
(function () {
  var P = {
    whatsapp: '<path d="M20 11.5a8.5 8.5 0 0 1-12.4 7.6L3 20.5l1.4-4.4A8.5 8.5 0 1 1 20 11.5z"/><path d="M9 9.2c.3 1.9 1.9 3.6 3.9 4.2l1-1 1.6.8-.3 1.3c-3 .3-6.7-2.8-7.1-6l1.3-.4.9 1.5z"/>',
    zap: '<path d="M13 2.5 4.5 13.5H11l-1 8 8.5-11H12z"/>',
    headset: '<path d="M4 14v-2a8 8 0 0 1 16 0v2"/><path d="M4 14h3v5H5.5A1.5 1.5 0 0 1 4 17.5zM20 14h-3v5h1.5a1.5 1.5 0 0 0 1.5-1.5z"/>',
    layers: '<path d="M12 3 2.5 8 12 13l9.5-5z"/><path d="m2.5 12.5 9.5 5 9.5-5"/><path d="m2.5 16.5 9.5 5 9.5-5"/>',
    growth: '<path d="m3 17 6-6 4 4 8-8"/><path d="M15 7h6v6"/>',
    box: '<path d="M3 7.5 12 3l9 4.5v9L12 21l-9-4.5z"/><path d="m3 7.5 9 4.5 9-4.5M12 12v9"/>',
    bag: '<path d="M5.5 8h13l-1 12.5h-11z"/><path d="M9 10V6.5a3 3 0 0 1 6 0V10"/>',
    file: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5M9 13h6M9 17h4"/>',
    palette: '<path d="M12 3a9 9 0 1 0 0 18c1.1 0 1.7-.8 1.7-1.6 0-.9-.7-1.3-.7-2.1 0-.9.8-1.6 1.7-1.6H17a4 4 0 0 0 4-4C21 6.7 17 3 12 3z"/><circle cx="7.5" cy="11.5" r="1"/><circle cx="10" cy="7.5" r="1"/><circle cx="15" cy="7.5" r="1"/>',
    spark: '<path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z"/><path d="M19 15.5l.7 1.8 1.8.7-1.8.7-.7 1.8-.7-1.8-1.8-.7 1.8-.7z"/>',
    chart: '<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>',
    lock: '<rect x="4.5" y="10.5" width="15" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.5 5.6 12 3z"/>',
    check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>'
  };
  // first match wins, so the more specific words come first ("دعم مباشر على واتساب" is WhatsApp, not support)
  var RULES = [
    [/واتس|whatsapp/i, "whatsapp"],
    [/أولوي|اولوي|سريع|فوري/, "zap"],
    [/دعم|مساعدة فني|support/i, "headset"],
    [/كل مميزات|كل المميزات|زي باقة/, "layers"],
    [/رسا?يل|رسالة|رصيد|استهلاك/, "growth"],
    [/أوردر|اوردر|طلبات|طلب|شحن|تتبع/, "box"],
    [/shopify|شوبيفاي|متجر|woo|سلة|زد/i, "bag"],
    [/ملف|pdf|word|excel|رفع/i, "file"],
    [/شكل|لون|ألوان|الوان|تصميم|ثيم/, "palette"],
    [/تقرير|تقارير|إحصائي|احصائي|تحليل/, "chart"],
    [/أمان|امان|حماية|تشفير/, "lock"],
    [/لغات|لغة|دومين|نطاق/, "globe"],
    [/مساعد|ذكي|ذكاء|24|بوت|\bai\b/i, "spark"]
  ];
  window.sbFeatIcon = function (text) {
    var t = String(text || ""), k = "check";
    for (var i = 0; i < RULES.length; i++) if (RULES[i][0].test(t)) { k = RULES[i][1]; break; }
    return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + P[k] + "</svg>";
  };
  // '<li>' with the icon and the (escaped) text, for the plan cards
  window.sbFeatItem = function (text, esc) {
    return '<li><span class="fic">' + window.sbFeatIcon(text) + "</span><span>" + esc(text) + "</span></li>";
  };
})();
