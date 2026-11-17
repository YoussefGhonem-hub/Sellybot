// api/logo.ashx: the store's own logo as its chat bubble (bubble style "logo", «شكل الشات»).
//   GET  ?store=<id>&v=<LogoVer>  the picture. With the current v, browsers keep it for a year (a new upload changes v),
//                                 and it's kept in memory too, so busy store pages don't each read it from the database.
//   POST (signed in) {data: "data:image/...;base64,..."}  saves a new logo (the dashboard has already shrunk it to 400px or less)
//   POST {remove: true}  deletes it; a store that was showing it goes back to the bear
// PNG, JPEG or WebP only (never SVG, which could carry script), up to 300 KB. Plan gate "styles", like the other special looks.
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Web;

namespace RiwaqChat
{
    public class LogoHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        const int MaxBytes = 300 * 1024;
        const long MaxCached = 16L * 1024 * 1024;

        class Pic { public int Ver; public string Type; public byte[] Data; }
        static readonly ConcurrentDictionary<string, Pic> Cache = new ConcurrentDictionary<string, Pic>();
        static long cachedBytes;

        // what the chat and the dashboard use: the logo's address (relative to the site), or null when the store has none
        public static string Url(StoreInfo s) { return s != null && s.LogoVer > 0 ? "api/logo.ashx?store=" + s.Slug + "&v=" + s.LogoVer : null; }
        public static string Fit(StoreInfo s) { return s != null && s.LogoFit == "cover" ? "cover" : "contain"; }

        static void Forget(string slug)
        {
            Pic p;
            if (slug != null && Cache.TryRemove(slug, out p)) Interlocked.Add(ref cachedBytes, -p.Data.Length);
            ChatLook.Forget(slug);   // the chat's look (with the logo's address) shows the change right away
        }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                if (req.HttpMethod == "GET" || req.HttpMethod == "HEAD") { Serve(req, res); return; }
                if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (!Web.Allow("logo:" + s.Id, 20, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "غيّرت اللوجو كتير، استنى شوية وجرّب تاني."); return; }
                    var b = Web.Body(req, 450000);

                    object rm;
                    if (b.TryGetValue("remove", out rm) && rm is bool && (bool)rm)
                    {
                        using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.StoreLogos WHERE StoreId=@s;
                            UPDATE dbo.Stores SET LogoVer=0, BubbleStyle=CASE WHEN BubbleStyle='logo' THEN 'bear' ELSE BubbleStyle END WHERE Id=@s", "@s", s.Id))
                            cmd.ExecuteNonQuery();
                        Forget(s.Slug);
                        Web.Json(res, 200, Me.Describe(c, Db.StoreById(c, s.Id)));
                        return;
                    }

                    if (!PlanGate.Allows(c, s, "styles")) { PlanGate.Deny(res, c, s, "styles"); return; }
                    byte[] data; string type;
                    if (!Decode(Web.Str(b, "data", 450000), out data, out type))
                    { Web.Error(res, 400, "bad_image", "الصورة دي مش مدعومة. ارفع اللوجو PNG أو JPG أو WebP."); return; }
                    if (data.Length > MaxBytes) { Web.Error(res, 400, "too_big", "اللوجو كبير، لازم يكون أقل من 300 كيلوبايت."); return; }

                    // the upload time is the version: the address changes with every upload, so browsers never show an old one
                    int ver = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (ver <= s.LogoVer) ver = s.LogoVer + 1;
                    using (var cmd = Db.Cmd(c, @"UPDATE dbo.StoreLogos SET ContentType=@t, Data=@d, UpdatedAt=SYSUTCDATETIME() WHERE StoreId=@s;
                        IF @@ROWCOUNT = 0 INSERT INTO dbo.StoreLogos(StoreId, ContentType, Data) VALUES(@s, @t, @d);
                        UPDATE dbo.Stores SET LogoVer=@v WHERE Id=@s;", "@s", s.Id, "@t", type, "@d", data, "@v", ver))
                        cmd.ExecuteNonQuery();
                    Forget(s.Slug);
                    Web.Json(res, 200, Me.Describe(c, Db.StoreById(c, s.Id)));
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static void Serve(HttpRequest req, HttpResponse res)
        {
            string slug = (req.QueryString["store"] ?? "").Trim().ToLowerInvariant();
            int want; int.TryParse(req.QueryString["v"], out want);
            if (slug.Length == 0 || slug.Length > 40) { NotFound(res); return; }

            Pic p;
            if (!Cache.TryGetValue(slug, out p) || (want != 0 && want != p.Ver))
            {
                if (!Web.Allow("logo-get:" + Web.Ip(req), 300, TimeSpan.FromMinutes(1))) { res.StatusCode = 429; return; }
                p = Load(slug);
                if (p == null) { Forget(slug); NotFound(res); return; }
                Remember(slug, p);
            }
            res.ContentType = p.Type;
            res.AddHeader("Content-Security-Policy", "default-src 'none'");
            res.Cache.SetCacheability(HttpCacheability.Public);
            if (want == p.Ver) { res.Cache.SetMaxAge(TimeSpan.FromDays(365)); res.Cache.AppendCacheExtension("immutable"); }
            else res.Cache.SetMaxAge(TimeSpan.FromMinutes(5));   // an old or missing v: the newest logo, briefly
            if (req.HttpMethod == "GET") res.BinaryWrite(p.Data);
        }

        static void NotFound(HttpResponse res)
        {
            res.StatusCode = 404;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
        }

        static Pic Load(string slug)
        {
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, @"SELECT s.LogoVer, l.ContentType, l.Data FROM dbo.Stores s JOIN dbo.StoreLogos l ON l.StoreId = s.Id
                WHERE s.Slug=@slug AND s.LogoVer > 0", "@slug", slug))
            using (var r = cmd.ExecuteReader())
                return r.Read() ? new Pic { Ver = (int)r[0], Type = (string)r[1], Data = (byte[])r[2] } : null;
        }

        static void Remember(string slug, Pic p)
        {
            if (Interlocked.Add(ref cachedBytes, p.Data.Length) > MaxCached) { Cache.Clear(); Interlocked.Exchange(ref cachedBytes, p.Data.Length); }
            Pic old;
            if (Cache.TryGetValue(slug, out old)) Interlocked.Add(ref cachedBytes, -old.Data.Length);
            Cache[slug] = p;
        }

        // "data:image/png;base64,..." (or bare base64) -> bytes, with the type read from the bytes themselves (never trusted from the name)
        static bool Decode(string dataUrl, out byte[] data, out string type)
        {
            data = null; type = null;
            if (string.IsNullOrEmpty(dataUrl)) return false;
            int comma = dataUrl.IndexOf(',');
            string b64 = dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 ? dataUrl.Substring(comma + 1) : dataUrl;
            try { data = Convert.FromBase64String(b64); } catch (FormatException) { return false; }
            type = Sniff(data);
            return type != null;
        }

        static string Sniff(byte[] d)
        {
            if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
            if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
            if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "image/webp";
            return null;
        }
    }
}
