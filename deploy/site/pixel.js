// Meta Pixel (Facebook / Instagram ads) for Sellybot's OWN public pages: start, pricing, about, download.
// Never in widget.js (it runs on store owners' websites) or index.html with ?store= (their customers' chat page),
// and the dashboard only loads it to report a new account (data-quiet: no PageView). The privacy page says the same.
//   <script src="pixel.js"></script>              -> PageView now
//   <script src="pixel.js" data-quiet></script>   -> nothing until sbTrack(...) is called
//   window.sbTrack("CompleteRegistration")         -> a conversion (e.g. a new account, from the form or from the chat)
(function () {
  var ID = "2169409460277527";
  var me = document.currentScript;
  var quiet = !!(me && me.hasAttribute("data-quiet"));
  var started = false;

  function start() {
    if (started) return;
    started = true;
    // Meta's standard loader (unchanged)
    !function (f, b, e, v, n, t, s) {
      if (f.fbq) return; n = f.fbq = function () { n.callMethod ? n.callMethod.apply(n, arguments) : n.queue.push(arguments); };
      if (!f._fbq) f._fbq = n; n.push = n; n.loaded = !0; n.version = '2.0';
      n.queue = []; t = b.createElement(e); t.async = !0;
      t.src = v; s = b.getElementsByTagName(e)[0];
      s.parentNode.insertBefore(t, s);
    }(window, document, 'script', 'https://connect.facebook.net/en_US/fbevents.js');
    window.fbq('init', ID);
  }

  window.sbTrack = function (event, data) {
    try { start(); window.fbq('track', event, data || {}); } catch (e) { }
  };

  if (!quiet) window.sbTrack("PageView");
})();
