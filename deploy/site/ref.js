// «دعوة تاجر»: an invite link (start.html?ref=<store id>) remembers which store sent the visitor for 30 days (cookie sb_ref),
// so signing up later (from any page, or Sellybot's chat) counts for that store. It keeps only the inviting store's id.
(function () {
  try {
    var m = /[?&]ref=([a-z0-9-]{3,40})(?:&|#|$)/i.exec(location.search);
    if (!m) return;
    document.cookie = "sb_ref=" + m[1].toLowerCase() + "; Max-Age=2592000; Path=/; SameSite=Lax" + (location.protocol === "https:" ? "; Secure" : "");
  } catch (e) {}
})();
