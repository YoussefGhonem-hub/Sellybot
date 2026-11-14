// One public address for the site (setting "CanonicalHost", e.g. sellybot.online):
//   http://sellybot.online/...      -> https://sellybot.online/...   (301)
//   http(s)://www.sellybot.online/  -> https://sellybot.online/...   (301)
//   old address (*.ftempurl.com): pages only (/, *.html) -> https://sellybot.online/... (302)
//     widget.js and api/* keep working on the old address, so stores that pasted the old code don't break.
// Let's Encrypt proof files (/.well-known/acme-challenge/) are never redirected (renewal checks them over http).
// Only GET/HEAD are redirected; an empty setting turns all of this off.
using System;
using System.Web;

namespace RiwaqChat
{
    public class CanonicalHostModule : IHttpModule
    {
        public void Init(HttpApplication app) { app.BeginRequest += (s, e) => Handle(((HttpApplication)s).Context); }
        public void Dispose() { }

        static void Handle(HttpContext ctx)
        {
            string canonical = (Web.Setting("CanonicalHost") ?? "").Trim().ToLowerInvariant();
            if (canonical.Length == 0) return;
            var req = ctx.Request;
            if (req.HttpMethod != "GET" && req.HttpMethod != "HEAD") return;
            string path = req.Url.AbsolutePath ?? "/";
            if (path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase)) return;

            string host = (req.Url.Host ?? "").ToLowerInvariant();
            bool secure = req.IsSecureConnection || string.Equals(req.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
            string target = null; bool permanent = true;

            if (host == "www." + canonical) target = "https://" + canonical + req.Url.PathAndQuery;
            else if (host == canonical) { if (!secure) target = "https://" + canonical + req.Url.PathAndQuery; }
            else if (host.EndsWith(".ftempurl.com") && IsPage(path)) { target = "https://" + canonical + req.Url.PathAndQuery; permanent = false; }

            if (target == null) return;
            ctx.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            if (permanent) ctx.Response.RedirectPermanent(target, false); else ctx.Response.Redirect(target, false);
            ctx.ApplicationInstance.CompleteRequest();
        }

        static bool IsPage(string path)
        {
            return path == "/" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
