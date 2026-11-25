// shopfront.ashx: what the Cloudflare Worker asks for when a customer opens <name>.sellystore.online (deploy/store-worker/worker.js).
// The Worker sends the secret (X-Shop-Key), the store's name (X-Shop-Name), the path (X-Shop-Path) and the visitor's address (X-Shop-Ip);
// without the secret there is nothing here. It's not under /api/ on purpose: every store's requests reach us from Cloudflare's own address,
// and the limits are per visitor. A page is ready HTML (title, text, prices, for Google, links shared on WhatsApp and slow phones) with
// the data the template draws from; prices are always worked out again by the server when the cart is summed and when the order is made.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class ShopFrontHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        // the store a request is for (the chat inside the store reads it)
        public class Ctx { public StoreInfo Store; public Shop Row; public ShopData Data; public string Ip, Path, Lang, Prefix, Pv; public bool Preview; public DateTime Now = DateTime.UtcNow; public Dictionary<string, object> Look; }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.TrySkipIisCustomErrors = true;
            res.Cache.SetNoServerCaching();   // a public answer must never be kept by IIS: it would be given to the next request, whatever its store or secret
            if (!Db.Configured) { res.StatusCode = 503; return; }
            bool known = Shops.SameKey(req.Headers["X-Shop-Key"], Shops.KnownKey());
            if (!known)
            {
                // the key isn't in memory yet (just started): read it, a few times a minute at most
                if (string.IsNullOrEmpty(req.Headers["X-Shop-Key"]) || !Web.Allow("shop-key", 60, TimeSpan.FromMinutes(1))) { res.StatusCode = 404; return; }
                using (var c = Db.Open()) if (!Shops.Ready || !Shops.SameKey(req.Headers["X-Shop-Key"], Shops.Key(c))) { res.StatusCode = 404; return; }
            }
            string path = HttpUtility.UrlDecode(req.Headers["X-Shop-Path"] ?? "/") ?? "/";
            string name = (req.Headers["X-Shop-Name"] ?? "").ToLowerInvariant();
            if (path.Length > 300 || path.Contains("..") || name.Length > 40) { res.StatusCode = 404; return; }
            try
            {
                if (name == "img") { ShopImages.Serve(path.TrimStart('/'), res, req.HttpMethod == "HEAD"); return; }
                // the same files for every store (and the pictures of the domain's own page)
                if (path.StartsWith("/_s/", StringComparison.Ordinal)) { ShopPages.Asset(path.Substring(4), req, res); return; }
                if (name.Length == 0) { ShopPages.Root(path, res); return; }
                if (path == "/widget.js") { ShopPages.SiteFile("~/widget.js", "text/javascript; charset=utf-8", req, res); return; }
                if (path == "/api/logo.ashx") { new LogoHandler().ProcessRequest(ctx); return; }
                await Store(ctx, name, path);
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { res.StatusCode = 200; Web.Json(res, 200, new Dictionary<string, object> { { "error", ex.Code }, { "message", ex.Message }, { "data", ex.Extra } }); }
            catch (Exception ex)
            {
                Log.Error(ex);
                if (path.StartsWith("/_a/")) { if (path == "/_a/order") ShopOrders.FailAt(name, "order", ex.GetType().Name + ": " + ex.Message); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني بعد شوية."); }
                else { res.StatusCode = 500; ShopPages.Message(res, "حصلت مشكلة بسيطة", "جرّب تفتح الصفحة تاني بعد شوية."); }
            }
        }

        static async Task Store(HttpContext ctx, string name, string path)
        {
            var req = ctx.Request; var res = ctx.Response;
            var x = new Ctx { Ip = Web.Ip(req), Path = path };
            bool open, chat = path == "/api/chat.ashx";
            using (var c = Db.Open())
            {
                if (!Shops.Ready) { res.StatusCode = 503; return; }
                x.Row = Shops.BySlug(c, name);
                if (x.Row == null)
                {
                    // the store changed its name: the old link goes to the new one
                    string to = null; using (var cmd = Db.Cmd(c, "SELECT TOP 1 s.Slug FROM dbo.ShopRedirects r JOIN dbo.Shops s ON s.StoreId=r.StoreId WHERE r.Kind='s' AND r.OldSlug=@n", "@n", name)) to = cmd.ExecuteScalar() as string;
                    if (to != null && req.HttpMethod == "GET") { res.StatusCode = 301; res.AddHeader("Location", Shops.Address(to) + path); return; }
                    res.StatusCode = 404; ShopPages.Message(res, "مفيش متجر بالاسم ده", "اتأكد من اللينك واكتبه تاني."); return;
                }
                x.Store = Db.StoreById(c, x.Row.StoreId);
                // the owner's preview: a cookie, or (inside the dashboard, where some browsers keep no cookie for a frame) the token in the address
                var pv = Regex.Match(req.Headers["X-Shop-Query"] ?? "", "[?&]_pv=([a-f0-9]{32})(&|$)"); x.Pv = pv.Success ? pv.Groups[1].Value : null;
                string token = x.Pv ?? (req.Cookies["sbpv"] == null ? null : req.Cookies["sbpv"].Value);
                if (path.StartsWith("/_preview/", StringComparison.Ordinal)) { ShopPages.StartPreview(c, x, path.Substring(10), res, Regex.IsMatch(req.Headers["X-Shop-Query"] ?? "", "[?&]f=1(&|$)"), Regex.IsMatch(req.Headers["X-Shop-Query"] ?? "", "[?&]live=1(&|$)")); return; }
                if (path == "/_preview-off") { ShopPages.Cookie(res, "", DateTime.UtcNow.AddDays(-1)); res.Redirect("/", false); return; }
                x.Preview = token != null && ShopPages.PreviewOk(c, x.Row.StoreId, token);
                open = Shops.Open(c, x.Store, x.Row);
                if (!open && !x.Preview)
                {
                    if (chat || path.StartsWith("/_")) { Web.Error(res, 403, "closed", "المتجر مقفول دلوقتي."); return; }
                    ShopPages.Closed(c, x, res); return;
                }
                // «المنشور» inside the preview, to compare the two
                x.Look = x.Row.Look(x.Preview && !(x.Row.LiveJson != null && Regex.IsMatch(req.Headers["X-Shop-Query"] ?? "", "[?&]_lv=1(&|$)")));
                if (!chat)
                {
                    x.Data = ShopCache.Get(c, x.Row);
                    if (x.Data == null) { res.StatusCode = 404; return; }
                    if (req.HttpMethod == "POST") ShopActions.Run(c, x, req, res); else ShopPages.Get(c, x, req, res);
                    return;
                }
            }
            // the assistant, in the store's own chat: the connection was given back first (a reply takes seconds)
            if (req.HttpMethod != "GET" && req.HttpMethod != "POST") { Web.Error(res, 405, "method"); return; }
            if (!string.Equals((req.QueryString["store"] ?? "").Trim(), x.Store.Slug, StringComparison.OrdinalIgnoreCase)) { Web.Error(res, 404, "no_store"); return; }
            ctx.Items["shop"] = x;
            await new ChatHandler().ProcessRequestAsync(ctx);
        }
    }

    // What the store's page sends: the cart's sum, the order, «طلبي فين؟», a report, the numbers of visits.
    public static class ShopActions
    {
        public static void Run(SqlConnection c, ShopFrontHandler.Ctx x, HttpRequest req, HttpResponse res)
        {
            var min = TimeSpan.FromMinutes(1);
            string a = x.Path.StartsWith("/_a/", StringComparison.Ordinal) ? x.Path.Substring(4) : "";
            // the language the customer is reading: their messages and their order's WhatsApp follow it
            string langs = ShopJson.Str(ShopJson.At(x.Look, "langs")); x.Lang = langs == "en" || (langs == "both" && Regex.IsMatch(req.Headers["X-Shop-Query"] ?? "", "[?&]l=en(&|$)")) ? "en" : "ar";
            if (a == "hit") { Hit(c, x, req); res.StatusCode = 204; return; }
            if (!Web.Allow("shop-a:" + x.Ip, 120, min)) { Web.Error(res, 429, "rate_limited", "استنى ثواني وجرّب تاني."); return; }
            if (a.Length == 0) { Web.Error(res, 404, "not_found"); return; }
            var b = Web.Body(req, 40000);
            switch (a)
            {
                case "quote":
                    Web.Json(res, 200, ShopPrice.Json(ShopPrice.Quote(c, x.Data, Lines(b), ShopJson.Str(ShopJson.At(b, "cpn"), 30), ShopJson.Int(ShopJson.At(b, "gov")), ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24)), x.Now)));
                    return;
                case "order": Order(c, x, b, res); return;
                case "track": Track(c, x, b, res); return;
                case "thanks":
                    {
                        var o = ShopOrders.ByToken(c, x.Row.StoreId, Regex.Replace(ShopJson.Str(ShopJson.At(b, "t"), 40), "[^a-f0-9]", ""));
                        if (o == null || (DateTime.UtcNow - o.CreatedAt).TotalDays > 60) { Web.Error(res, 404, "not_found"); return; }
                        Web.Json(res, 200, ShopOrders.CustomerJson(o, true)); return;
                    }
                case "report": Report(c, x, b, res); return;
                case "lead": Lead(c, x, b, res); return;
                default: Web.Error(res, 404, "not_found"); return;
            }
        }

        public static List<ShopLine> Lines(Dictionary<string, object> b)
        {
            var list = new List<ShopLine>();
            foreach (var i in ShopJson.List(ShopJson.At(b, "l")).Take(Shops.MaxLines))
            {
                var d = i as Dictionary<string, object>; var l = d == null ? ShopJson.List(i) : null;
                int v = d != null ? ShopJson.Int(ShopJson.At(d, "v")) : l.Count > 0 ? ShopJson.Int(l[0]) : 0, q = d != null ? ShopJson.Int(ShopJson.At(d, "q")) : l.Count > 1 ? ShopJson.Int(l[1]) : 0;
                string ex = d != null ? ShopJson.Str(ShopJson.At(d, "x"), 600) : l.Count > 2 ? ShopJson.Str(l[2], 600) : "";
                if (v > 0 && q > 0) list.Add(new ShopLine { VariantId = v, Qty = Math.Min(999, q), Extra = ex.Length == 0 ? null : ex });
            }
            return list;
        }

        static void Order(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("shop-try:" + x.Ip, 40, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", "حاولت كتير. استنى شوية وجرّب تاني.");
            Func<string, int, string> s = (k, n) => ShopJson.Str(ShopJson.At(b, k), n);
            var o = new ShopOrderIn
            {
                Lines = Lines(b), Name = s("name", 80), Phone = s("phone", 24), Phone2 = s("phone2", 24), Gov = ShopJson.Int(ShopJson.At(b, "gov")), City = s("city", 60), Area = s("area", 80), Address = s("address", 300), Landmark = s("landmark", 120), Note = s("note", 400),
                Coupon = s("cpn", 30), ClientKey = s("ck", 60), Lang = x.Lang, Device = Regex.Replace(s("dev", 40), "[^A-Za-z0-9-]", ""), Ip = x.Ip, Src = s("src", 200), Expected = ShopJson.DecN(ShopJson.At(b, "total")), Agreed = ShopJson.Bool(ShopJson.At(b, "agree")), Test = x.Preview
            };
            // a filled field people don't see: a program, not a person. It's told everything went well.
            if (s("website", 50).Length > 0) { Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "no", 0 }, { "token", "" } }); return; }
            ShopOrders.Placed p;
            try { p = ShopOrders.Place(c, x.Store, x.Row, o); }
            catch (ShopException) { throw; }
            catch (Exception ex) { ShopOrders.Fail(x.Row.StoreId, "order", ex.GetType().Name + ": " + ex.Message); throw; }
            if (!p.Again && !p.Order.Test) ShopHooks.OnPlaced(x.Store, p.Order.Id);
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "no", p.Order.No }, { "token", p.Order.Token }, { "total", p.Order.Total }, { "test", p.Order.Test }, { "again", p.Again } });
        }

        static void Track(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            // guessing numbers: a few tries from one place
            if (!Web.Allow("shop-track:" + x.Row.StoreId + ":" + x.Ip, 10, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", "حاولت كتير. استنى شوية وجرّب تاني.");
            int no = ShopJson.Int(Regex.Replace(ShopJson.Digits(ShopJson.Str(ShopJson.At(b, "no"), 20)), @"\D", "")); var phone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24));
            var o = no > 0 && phone != null ? ShopOrders.ByNo(c, x.Row.StoreId, no) : null;
            if (o == null || (o.Phone != phone && o.Phone2 != phone)) throw new ShopException("nomatch", x.Lang == "en" ? "No order matches this number and phone." : "مفيش طلب بالرقم والموبايل دول. راجعهم وجرّب تاني.");
            Web.Json(res, 200, ShopOrders.CustomerJson(o, false));
        }

        // «نافذة عرض»: the visitor's mobile for the code
        static void Lead(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("shop-lead:" + x.Ip, 8, TimeSpan.FromHours(1))) throw new ShopException("too_many", x.Lang == "en" ? "Too many tries. Please wait a while." : "محاولات كتير. استنى شوية.");
            var phone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24)); if (phone == null) throw new ShopException("phone", x.Lang == "en" ? "Enter a valid Egyptian mobile number." : "اكتب رقم موبايل مصري صح.");
            string code; using (var cmd = Db.Cmd(c, "SELECT TOP 1 Code FROM dbo.ShopCoupons WHERE StoreId=@s AND Popup=1 AND PopupPhone=1 AND Active=1 AND (EndsAt IS NULL OR EndsAt>SYSUTCDATETIME()) AND (StartsAt IS NULL OR StartsAt<=SYSUTCDATETIME()) AND (MaxUses IS NULL OR Uses<MaxUses) ORDER BY Id DESC", "@s", x.Row.StoreId)) code = cmd.ExecuteScalar() as string;
            if (code == null) throw new ShopException("ended", x.Lang == "en" ? "This offer has ended." : "العرض ده خلص.");
            if (!x.Preview) using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopLeads WHERE StoreId=@s AND Phone=@p) INSERT INTO dbo.ShopLeads(StoreId, Phone, Coupon) VALUES(@s, @p, @k)", "@s", x.Row.StoreId, "@p", phone, "@k", code)) try { cmd.ExecuteNonQuery(); } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "code", code } });
        }

        static void Report(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("shop-rep:" + x.Ip, 3, TimeSpan.FromHours(1))) throw new ShopException("too_many", "وصلنا بلاغك. شكرًا.");
            var reason = ShopJson.Str(ShopJson.At(b, "reason"), 30); if (!Regex.IsMatch(reason, "^[a-z]{2,20}$")) reason = "other";
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopReports(StoreId, Reason, Text, Contact, Page, Ip) VALUES(@s, @r, @t, @c, @p, @ip)", "@s", x.Row.StoreId, "@r", reason, "@t", ShopJson.Str(ShopJson.At(b, "text"), 1000), "@c", ShopJson.Str(ShopJson.At(b, "contact"), 100), "@p", ShopJson.Str(ShopJson.At(b, "page"), 200), "@ip", x.Ip)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true } });
            int sid = x.Row.StoreId; HostingEnvironment.QueueBackgroundWorkItem(ct => ShopHooks.Reported(sid));
        }

        // visits: v = a new visit, d = the phone's first today, p = a product page, c = first add to the cart, k = checkout opened
        static void Hit(SqlConnection c, ShopFrontHandler.Ctx x, HttpRequest req)
        {
            if (x.Preview || !Web.Allow("shop-hit:" + x.Ip, 120, TimeSpan.FromMinutes(1))) return;
            string t; using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) t = sr.ReadToEnd();
            if (t.Length > 20) return;
            var cols = new Dictionary<char, string> { { 'v', "Visits" }, { 'd', "Visitors" }, { 'p', "Views" }, { 'c', "Carts" }, { 'k', "Checkouts" } };
            foreach (var ch in t.Distinct()) { string col; if (cols.TryGetValue(ch, out col)) ShopOrders.Daily(c, null, x.Row.StoreId, col, 1); }
        }
    }

    public static class ShopPages
    {
        static string H(string s) { return HttpUtility.HtmlEncode(s ?? ""); }
        static string Pick(string ar, string en, string lang) { return lang == "en" && !string.IsNullOrWhiteSpace(en) ? en : ar; }

        // ---------- files every store shares (site/shop): templates, the engine, styles ----------
        class FileCopy { public byte[] Data; public DateTime At; public string Tag; }
        static readonly ConcurrentDictionary<string, FileCopy> Files = new ConcurrentDictionary<string, FileCopy>();
        static FileCopy Load(string virt)
        {
            var phys = HostingEnvironment.MapPath(virt);
            if (phys == null || !File.Exists(phys)) return null;
            var t = File.GetLastWriteTimeUtc(phys); FileCopy f;
            if (Files.TryGetValue(virt, out f) && f.At == t) return f;
            var data = File.ReadAllBytes(phys); int h = 17; foreach (var b in data) h = unchecked(h * 31 + b);
            f = new FileCopy { Data = data, At = t, Tag = (h & 0x7FFFFFFF).ToString("x") };
            Files[virt] = f; return f;
        }
        public static string Ver(string file) { var f = Load("~/shop/" + file); return f == null ? "0" : f.Tag; }
        public static bool Has(string file) { return Load("~/shop/" + file) != null; }

        public static void Asset(string file, HttpRequest req, HttpResponse res)
        {
            if (!Regex.IsMatch(file, @"^(t/)?[a-z0-9.\-]{1,40}\.(js|css|svg|woff2|webp|png)$") || file.Contains("..")) { res.StatusCode = 404; return; }
            var type = file.EndsWith(".js") ? "text/javascript; charset=utf-8" : file.EndsWith(".css") ? "text/css; charset=utf-8" : file.EndsWith(".svg") ? "image/svg+xml" : file.EndsWith(".woff2") ? "font/woff2" : file.EndsWith(".png") ? "image/png" : "image/webp";
            SiteFile("~/shop/" + file, type, req, res);
        }
        public static void SiteFile(string virt, string type, HttpRequest req, HttpResponse res)
        {
            var f = Load(virt);
            if (f == null) { res.StatusCode = 404; res.Cache.SetCacheability(HttpCacheability.NoCache); return; }
            res.ContentType = type;
            res.Cache.SetCacheability(HttpCacheability.Public);
            if (req.QueryString["v"] == f.Tag) { res.Cache.SetMaxAge(TimeSpan.FromDays(365)); res.Cache.AppendCacheExtension("immutable"); } else res.Cache.SetMaxAge(TimeSpan.FromMinutes(10));
            if (req.HttpMethod != "HEAD") res.BinaryWrite(f.Data);
        }

        // ---------- preview: the draft, seen by the owner or by whoever has the link, for a day ----------
        public static bool PreviewOk(SqlConnection c, int storeId, string token)
        {
            if (!Regex.IsMatch(token ?? "", "^[a-f0-9]{32}$")) return false;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopPreviews WHERE Token=@t AND StoreId=@s AND ExpiresAt>SYSUTCDATETIME()", "@t", token, "@s", storeId)) return (int)cmd.ExecuteScalar() > 0;
        }
        public static void Cookie(HttpResponse res, string v, DateTime until)
        {
            // inside the dashboard the preview is a frame of another site: the cookie has to be allowed there (None needs https)
            bool https = Shops.Scheme == "https";
            res.AddHeader("Set-Cookie", "sbpv=" + v + "; Path=/; HttpOnly; Expires=" + until.ToString("R") + (https ? "; Secure; SameSite=None; Partitioned" : "; SameSite=Lax"));
        }
        public static void StartPreview(SqlConnection c, ShopFrontHandler.Ctx x, string token, HttpResponse res, bool frame = false, bool live = false)
        {
            string to = "/"; int q = token.IndexOf('/'); if (q > 0) { to = token.Substring(q); token = token.Substring(0, q); }
            if (!PreviewOk(c, x.Row.StoreId, token)) { res.StatusCode = 410; Message(res, "لينك المعاينة خلص", "اطلب من صاحب المتجر لينك جديد."); return; }
            Cookie(res, token, DateTime.UtcNow.AddHours(24));
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            res.StatusCode = 302; res.AddHeader("Location", (to.StartsWith("/") && !to.StartsWith("//") ? to : "/") + (frame ? "?_pv=" + token + (live ? "&_lv=1" : "") : ""));
        }

        // ---------- pages ----------
        public static void Get(SqlConnection c, ShopFrontHandler.Ctx x, HttpRequest req, HttpResponse res)
        {
            string path = x.Path;
            string langs = ShopJson.Str(ShopJson.At(x.Look, "langs")), first = ShopJson.Str(ShopJson.At(x.Look, "first")) == "en" ? "en" : "ar";
            x.Lang = langs == "en" ? "en" : langs == "both" ? first : "ar"; x.Prefix = "";
            var m = Regex.Match(path, "^/(ar|en)(/|$)");
            if (m.Success)
            {
                string want = m.Groups[1].Value;
                if (langs != "both" || want == first) { res.StatusCode = 301; res.AddHeader("Location", "/" + path.Substring(m.Length) + Query(req)); return; }
                x.Lang = want; x.Prefix = "/" + want; path = "/" + path.Substring(m.Length);
            }
            if (path.Length > 1 && path.EndsWith("/")) path = path.TrimEnd('/');

            if (path.StartsWith("/_d/", StringComparison.Ordinal)) { Data(x, path.Substring(4), res); return; }
            if (path == "/robots.txt") { Robots(x, res); return; }
            if (path == "/sitemap.xml") { Sitemap(x, res); return; }
            if (path == "/favicon.ico" || path == "/apple-touch-icon.png")
            {
                var icon = ShopJson.Str(ShopJson.At(x.Look, "icon")); if (icon.Length == 0) icon = ShopJson.Str(ShopJson.At(x.Look, "logo"));
                if (icon.Length == 0) { SiteFile("~/sellybot-icon.svg", "image/svg+xml", req, res); return; }
                res.StatusCode = 302; res.AddHeader("Location", ShopImages.Url(icon, true)); res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.FromHours(1)); return;
            }

            var p = new PageInfo { T = "home", Canonical = path };
            var d = x.Data;
            var seg = path.Trim('/').Split('/');
            if (path == "/") { }
            else if (seg[0] == "c" && seg.Length == 2)
            {
                if (seg[1] == "all") p.T = "all";
                else
                {
                    ShopCat k; int id;
                    if (!d.CatBySlug.TryGetValue(seg[1], out k) && d.Redirects.TryGetValue("c:" + seg[1], out id) && d.CatById.TryGetValue(id, out k)) { Moved(x, res, "/c/" + k.Slug + Query(req)); return; }
                    if (k == null || k.Hidden) { p.T = "404"; } else { p.T = "cat"; p.Cat = k; }
                }
            }
            else if (seg[0] == "p" && seg.Length == 2)
            {
                ShopProd pr; int id;
                if (!d.BySlug.TryGetValue(seg[1], out pr) && d.Redirects.TryGetValue("p:" + seg[1], out id) && d.ById.TryGetValue(id, out pr)) { Moved(x, res, "/p/" + pr.Slug + Query(req)); return; }
                if (pr == null || pr.Status != "active") p.T = "404"; else { p.T = "p"; p.Prod = pr; }
            }
            else if (seg[0] == "page" && seg.Length == 2) { var pg = d.Pages.FirstOrDefault(y => y.Slug == seg[1] && !y.Hidden); if (pg == null) p.T = "404"; else { p.T = "page"; p.Page = pg; } }
            else if (seg[0] == "thanks" && seg.Length == 2) { p.T = "thanks"; p.Token = Regex.Replace(seg[1], "[^a-f0-9]", ""); p.NoIndex = true; }
            else if (seg.Length == 1 && (seg[0] == "cart" || seg[0] == "checkout" || seg[0] == "track" || seg[0] == "search" || seg[0] == "offers" || seg[0] == "fav")) { p.T = seg[0]; p.NoIndex = seg[0] != "offers"; }
            else p.T = "404";

            if (p.T == "404") { res.StatusCode = 404; p.NoIndex = true; }
            Html(c, x, p, res);
        }
        // the visitor's own query (the address we get carries the Worker's as well)
        static string Query(HttpRequest req) { var q = req.Headers["X-Shop-Query"]; return string.IsNullOrEmpty(q) || q.Length > 600 || !q.StartsWith("?") || q.Any(char.IsControl) ? "" : q; }
        static void Moved(ShopFrontHandler.Ctx x, HttpResponse res, string to) { res.StatusCode = 301; res.AddHeader("Location", x.Prefix + to); res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.FromMinutes(10)); }

        class PageInfo { public string T, Canonical, Token; public ShopCat Cat; public ShopProd Prod; public ShopPage Page; public bool NoIndex; }

        // the data files: immutable for their version
        static void Data(ShopFrontHandler.Ctx x, string file, HttpResponse res)
        {
            object o = null; bool fixedFile = true; var m = Regex.Match(file, @"^p-(\d+)-[\w-]+\.json$");
            if (Regex.IsMatch(file, @"^c-[\w-]+\.json$")) { o = ShopView.Catalog(x.Data, x.Now); fixedFile = file == "c-" + x.Data.CatalogVer(x.Now) + ".json"; }
            else if (m.Success) { ShopProd p; int id = int.Parse(m.Groups[1].Value); if (x.Data.ById.TryGetValue(id, out p) && p.Status == "active") { o = ShopView.Detail(x.Data, p, x.Now); fixedFile = file == "p-" + id + "-" + x.Data.CatalogVer(x.Now) + ".json"; } }
            else if (file == "geo.json") o = ShopGeo.Json();
            if (o == null) { Web.Error(res, 404, "not_found"); return; }
            res.ContentType = "application/json; charset=utf-8";
            if (x.Preview) res.Cache.SetCacheability(HttpCacheability.NoCache);
            else { res.Cache.SetCacheability(HttpCacheability.Public); if (file == "geo.json") res.Cache.SetMaxAge(TimeSpan.FromDays(7)); else if (fixedFile) { res.Cache.SetMaxAge(TimeSpan.FromDays(30)); res.Cache.AppendCacheExtension("immutable"); } else res.Cache.SetMaxAge(TimeSpan.FromSeconds(30)); }
            res.Write(Web.Js.Serialize(o));
        }

        static void Robots(ShopFrontHandler.Ctx x, HttpResponse res)
        {
            res.ContentType = "text/plain; charset=utf-8"; res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.FromHours(1));
            res.Write("User-agent: *\nDisallow: /cart\nDisallow: /checkout\nDisallow: /thanks/\nDisallow: /track\nDisallow: /search\nDisallow: /_a/\n\nSitemap: " + x.Row.Url + "/sitemap.xml\n");
        }
        static void Sitemap(ShopFrontHandler.Ctx x, HttpResponse res)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            Action<string, DateTime?> add = (p, t) => sb.Append("<url><loc>").Append(H(x.Row.Url + p)).Append("</loc>").Append(t.HasValue ? "<lastmod>" + t.Value.ToString("yyyy-MM-dd") + "</lastmod>" : "").Append("</url>\n");
            add("/", null); if (x.Data.Offers.Any(o => o.Running(x.Now))) add("/offers", null);
            foreach (var k in x.Data.Cats.Where(x.Data.CatShown)) add("/c/" + k.Slug, null);
            foreach (var p in x.Data.Shown) add("/p/" + p.Slug, p.UpdatedAt);
            foreach (var p in x.Data.Pages.Where(y => !y.Hidden)) add("/page/" + p.Slug, null);
            sb.Append("</urlset>");
            res.ContentType = "application/xml; charset=utf-8"; res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.FromHours(1)); res.Write(sb.ToString());
        }

        // what the page's script may know about the store (nothing the owner keeps to themselves)
        public static Dictionary<string, object> ShopJsonFor(SqlConnection c, ShopFrontHandler.Ctx x)
        {
            var set = x.Row.Settings; var d = x.Data; var order = ShopJson.Sub(set, "order"); var ship = ShopJson.Sub(set, "ship");
            string tpl = ShopJson.Str(ShopJson.At(x.Look, "template")); if (!Has("t/" + tpl + ".js")) tpl = ShopTemplates.First(c);   // its template is gone: the first one that is there
            return new Dictionary<string, object>
            {
                { "name", ShopView.Pair(x.Row.Name, x.Row.NameEn) }, { "slug", x.Row.Slug }, { "url", x.Row.Url }, { "img", Img() }, { "tpl", tpl }, { "biz", x.Row.Biz ?? "" },
                { "contact", ShopJson.Sub(set, "contact") }, { "social", ShopJson.Sub(set, "social") }, { "legal", ShopJson.Sub(set, "legal") },
                { "ship", new Dictionary<string, object> { { "fee", ShopJson.Dec(ShopJson.At(ship, "fee")) }, { "over", ShopPrice.FreeOver(d, 0, x.Now) }, { "days", ShopJson.Str(ShopJson.At(ship, "days")) }, { "note", ShopJson.List(ShopJson.At(ship, "note")) },
                    { "zones", d.Zones.Values.Select(z => new Dictionary<string, object> { { "g", z.Gov }, { "f", z.Fee ?? ShopJson.Dec(ShopJson.At(ship, "fee")) }, { "d", z.Days ?? "" }, { "off", z.Off } }).ToArray() }, { "mode", ShopJson.Str(ShopJson.At(ship, "mode")) } } },
                { "order", new Dictionary<string, object> { { "maxQty", ShopJson.Int(ShopJson.At(order, "maxQty"), 20) }, { "min", ShopJson.Dec(ShopJson.At(order, "min")) }, { "phone2", ShopJson.Bool(ShopJson.At(order, "phone2"), true) }, { "area", ShopJson.Bool(ShopJson.At(order, "area"), true) }, { "waBtn", ShopJson.Bool(ShopJson.At(order, "waBtn"), true) } } },
                { "returns", ShopJson.Int(ShopJson.At(set, "returnsDays"), 14) }, { "vacation", ShopPrice.Vacation(d, x.Now) ? ShopJson.Sub(set, "vacation") : null },
                { "pixel", x.Preview ? "" : ShopJson.Str(ShopJson.At(ShopJson.Sub(set, "pixel"), "meta")) },
                { "pages", d.Pages.Where(p => !p.Hidden).Select(p => new Dictionary<string, object> { { "s", p.Slug }, { "k", p.Kind }, { "t", ShopView.Pair(p.Title, p.TitleEn) }, { "m", p.InMenu }, { "f", p.InFooter } }).ToArray() },
                { "popup", Popup(c, x) },
                { "chat", new Dictionary<string, object> { { "on", x.Store.Status == "active" }, { "store", x.Store.Slug }, { "v", (Load("~/widget.js") ?? new FileCopy { Tag = "1" }).Tag }, { "bot", x.Store.BotName ?? "" } } }
            };
        }
        static string Img() { var u = ShopImages.Url("x"); return u.Substring(0, u.Length - 1); }
        // «خصم على أول طلب»: the coupon the owner put in the window
        static object Popup(SqlConnection c, ShopFrontHandler.Ctx x)
        {
            var pop = ShopJson.Sub(x.Look, "popup");
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 * FROM dbo.ShopCoupons WHERE StoreId=@s AND Popup=1 AND Active=1 AND (EndsAt IS NULL OR EndsAt>SYSUTCDATETIME()) AND (StartsAt IS NULL OR StartsAt<=SYSUTCDATETIME()) AND (MaxUses IS NULL OR Uses<MaxUses) ORDER BY Id DESC", "@s", x.Row.StoreId))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                var k = ShopPrice.ReadCoupon(r);
                return new Dictionary<string, object> { { "code", k.PopupPhone ? "" : k.Code }, { "ask", k.PopupPhone }, { "type", k.Type }, { "value", k.Value }, { "min", k.MinSub ?? 0 }, { "first", k.FirstOnly }, { "until", k.EndsAt.HasValue ? ShopView.Unix(k.EndsAt.Value) : 0 } };
            }
        }

        static void Html(SqlConnection c, ShopFrontHandler.Ctx x, PageInfo p, HttpResponse res)
        {
            var d = x.Data; bool en = x.Lang == "en"; string lang = x.Lang;
            string shopName = Pick(x.Row.Name, x.Row.NameEn, lang), title = shopName, desc = null, image = null;
            var set = x.Row.Settings; var hero = ShopJson.Sub(x.Look, "hero");
            var about = ShopJson.List(ShopJson.At(ShopJson.Sub(x.Look, "footer"), "about")); string aboutText = about.Count > 0 ? Pick(ShopJson.Str(about[0]), about.Count > 1 ? ShopJson.Str(about[1]) : "", lang) : "";
            image = ShopJson.Str(ShopJson.At(x.Look, "share")); if (image.Length == 0) image = ShopJson.Str(ShopJson.At(hero, "img")); if (image.Length == 0) image = d.Shown.SelectMany(y => y.Pics).Select(y => y.Path).FirstOrDefault() ?? ShopJson.Str(ShopJson.At(x.Look, "logo"));
            desc = aboutText.Length > 0 ? aboutText : shopName;

            if (p.T == "cat") { title = (string.IsNullOrEmpty(p.Cat.MetaTitle) ? Pick(p.Cat.Name, p.Cat.NameEn, lang) : p.Cat.MetaTitle) + " | " + shopName; desc = !string.IsNullOrEmpty(p.Cat.MetaDesc) ? p.Cat.MetaDesc : Pick(p.Cat.Descr, p.Cat.DescrEn, lang) ?? desc; if (!string.IsNullOrEmpty(p.Cat.Cover ?? p.Cat.Image)) image = p.Cat.Cover ?? p.Cat.Image; }
            else if (p.T == "p") { title = (string.IsNullOrEmpty(p.Prod.MetaTitle) ? Pick(p.Prod.Name, p.Prod.NameEn, lang) : p.Prod.MetaTitle) + " | " + shopName; desc = !string.IsNullOrEmpty(p.Prod.MetaDesc) ? p.Prod.MetaDesc : Plain(Pick(p.Prod.Descr, p.Prod.DescrEn, lang), 200); if (p.Prod.Pics.Count > 0) image = p.Prod.Pics[0].Path; }
            else if (p.T == "page") { title = Pick(p.Page.Title, p.Page.TitleEn, lang) + " | " + shopName; desc = Plain(Pick(p.Page.Body, p.Page.BodyEn, lang), 200); }
            else if (p.T == "all") title = (en ? "All products" : "كل المنتجات") + " | " + shopName;
            else if (p.T == "offers") title = (en ? "Offers" : "العروض") + " | " + shopName;
            else if (p.T == "cart") title = (en ? "Cart" : "السلة") + " | " + shopName;
            else if (p.T == "checkout") title = (en ? "Checkout" : "تأكيد الطلب") + " | " + shopName;
            else if (p.T == "track") title = (en ? "Track my order" : "طلبي فين؟") + " | " + shopName;
            else if (p.T == "404") title = (en ? "Not found" : "الصفحة مش موجودة") + " | " + shopName;

            var now = x.Now; var shop = ShopJsonFor(c, x); string tpl = (string)shop["tpl"], ver = d.CatalogVer(now);
            string color = ShopJson.Str(ShopJson.At(x.Look, "color")), canonical = x.Row.Url + x.Prefix + (p.Canonical == "/" ? "/" : p.Canonical);
            var page = new Dictionary<string, object> { { "t", p.T } };
            if (p.Cat != null) page["id"] = p.Cat.Id;
            if (p.Prod != null) { page["id"] = p.Prod.Id; page["d"] = ShopView.Detail(d, p.Prod, now); }
            if (p.Page != null) page["pg"] = new Dictionary<string, object> { { "s", p.Page.Slug }, { "k", p.Page.Kind }, { "t", ShopView.Pair(p.Page.Title, p.Page.TitleEn) }, { "b", ShopView.Pair(p.Page.Body, p.Page.BodyEn) } };
            if (p.Token != null) page["token"] = p.Token;
            var boot = new Dictionary<string, object> { { "shop", shop }, { "look", x.Look }, { "lang", lang }, { "pre", x.Prefix }, { "ver", ver }, { "page", page }, { "now", ShopView.Unix(now) }, { "preview", x.Preview }, { "dash", x.Preview ? Dash() : "" }, { "pv", x.Preview ? x.Pv ?? "" : "" },
                { "a", new Dictionary<string, object> { { "core", Ver("core.js") }, { "geo", "1" } } } };

            var h = new StringBuilder(16000);
            h.Append("<!doctype html><html lang=\"").Append(lang).Append("\" dir=\"").Append(en ? "ltr" : "rtl").Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">");
            h.Append("<title>").Append(H(title)).Append("</title><meta name=\"description\" content=\"").Append(H(Plain(desc, 200))).Append("\">");
            if (p.NoIndex || x.Preview) h.Append("<meta name=\"robots\" content=\"noindex\">"); else h.Append("<link rel=\"canonical\" href=\"").Append(H(canonical)).Append("\">");
            if (ShopJson.Str(ShopJson.At(x.Look, "langs")) == "both" && !p.NoIndex)
            {
                string f = ShopJson.Str(ShopJson.At(x.Look, "first")) == "en" ? "en" : "ar", o = f == "en" ? "ar" : "en";
                h.Append("<link rel=\"alternate\" hreflang=\"").Append(f).Append("\" href=\"").Append(H(x.Row.Url + p.Canonical)).Append("\"><link rel=\"alternate\" hreflang=\"").Append(o).Append("\" href=\"").Append(H(x.Row.Url + "/" + o + (p.Canonical == "/" ? "" : p.Canonical))).Append("\">");
            }
            string ogImg = string.IsNullOrEmpty(image) ? "" : ShopImages.Url(image);
            h.Append("<meta property=\"og:type\" content=\"").Append(p.T == "p" ? "product" : "website").Append("\"><meta property=\"og:title\" content=\"").Append(H(title)).Append("\"><meta property=\"og:description\" content=\"").Append(H(OgDesc(d, p.Prod, desc, now))).Append("\"><meta property=\"og:url\" content=\"").Append(H(canonical)).Append("\"><meta property=\"og:site_name\" content=\"").Append(H(shopName)).Append("\">");
            if (ogImg.Length > 0) h.Append("<meta property=\"og:image\" content=\"").Append(H(ogImg)).Append("\"><meta name=\"twitter:card\" content=\"summary_large_image\">");
            if (color.Length > 0) h.Append("<meta name=\"theme-color\" content=\"").Append(H(color)).Append("\">");
            h.Append("<link rel=\"icon\" href=\"/favicon.ico\"><link rel=\"apple-touch-icon\" href=\"/apple-touch-icon.png\">");
            h.Append("<link rel=\"preconnect\" href=\"").Append(H(Img().TrimEnd('/'))).Append("\">"); if (x.Pv == null) h.Append("<link rel=\"preload\" as=\"fetch\" crossorigin=\"anonymous\" href=\"/_d/c-").Append(ver).Append(".json\">");
            h.Append("<link rel=\"stylesheet\" href=\"/_s/core.css?v=").Append(Ver("core.css")).Append("\"><link rel=\"stylesheet\" href=\"/_s/t/").Append(tpl).Append(".css?v=").Append(Ver("t/" + tpl + ".css")).Append("\">");
            h.Append("<style>.ssr{max-width:1080px;margin:0 auto;padding:24px 16px;font:16px/1.7 system-ui,Tahoma,sans-serif;opacity:0;animation:ssr 0s 3s forwards}.ssr img{max-width:100%;height:auto}@keyframes ssr{to{opacity:1}}</style><noscript><style>.ssr{opacity:1}</style></noscript>");
            JsonLd(h, x, p, canonical, shopName, ogImg);
            h.Append("</head><body><div id=\"app\"><div class=\"ssr\">");
            Ssr(h, x, p, shopName);
            h.Append("</div></div><script type=\"application/json\" id=\"boot\">").Append(Safe(Web.Js.Serialize(boot))).Append("</script>");
            h.Append("<script src=\"/_s/core.js?v=").Append(Ver("core.js")).Append("\" defer></script><script src=\"/_s/t/").Append(tpl).Append(".js?v=").Append(Ver("t/" + tpl + ".js")).Append("\" defer></script><script src=\"/_s/run.js?v=").Append(Ver("run.js")).Append("\" defer></script></body></html>");

            res.ContentType = "text/html; charset=utf-8";
            Secure(res, x.Preview);
            // the Worker may keep the page a minute (never a preview, nor the customer's own pages)
            bool keep = !x.Preview && res.StatusCode == 200 && (p.T == "home" || p.T == "cat" || p.T == "p" || p.T == "page" || p.T == "all" || p.T == "offers" || p.T == "cart" || p.T == "checkout" || p.T == "track" || p.T == "search" || p.T == "fav");
            if (keep)
            {
                int sec = 60; var next = d.NextChange(now); if (next.HasValue) sec = Math.Max(5, Math.Min(60, (int)(next.Value - now).TotalSeconds));
                Keep(res, sec);
            }
            else Keep(res, 0);
            res.Write(h.ToString());
        }

        // how many seconds the Worker may keep the page (0 = never)
        static void Keep(HttpResponse res, int sec)
        {
            if (sec <= 0) { res.Cache.SetCacheability(HttpCacheability.NoCache); res.Cache.SetNoStore(); return; }
            res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.Zero); res.AddHeader("X-Shop-Keep", sec.ToString(CultureInfo.InvariantCulture));
        }

        // the dashboard, the only place a preview may be framed in and take messages from
        static string Dash() { return "https://" + (Web.Setting("CanonicalHost", "").Length > 0 ? Web.Setting("CanonicalHost") : "sellybot.online"); }
        static void Secure(HttpResponse res, bool preview)
        {
            string img = Img().TrimEnd('/');
            if (preview) res.AddHeader("X-Robots-Tag", "noindex");
            res.AddHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob: " + img + " https://www.facebook.com; script-src 'self' https://connect.facebook.net; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' data: https://fonts.gstatic.com; connect-src 'self' https://www.facebook.com https://connect.facebook.net; media-src 'self' " + img
                + "; object-src 'none'; base-uri 'none'; form-action 'self' https://wa.me; frame-ancestors " + (preview ? Dash() + (Shops.Scheme == "http" ? " http://localhost:*" : "") : "'none'"));
        }
        // JSON inside a script tag: nothing in a product's text can close it
        static string Safe(string json) { return json.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029"); }
        public static string Plain(string s, int max)
        {
            s = Regex.Replace(s ?? "", @"[#*\r\n]+", " "); s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > max ? s.Substring(0, max - 1).TrimEnd() + "…" : s;
        }
        static string OgDesc(ShopData d, ShopProd p, string desc, DateTime now)
        {
            if (p == null) return Plain(desc, 200);
            decimal? old; ShopOffer of; var v = p.Vars.FirstOrDefault(p.Can) ?? p.Vars.FirstOrDefault();
            var pr = ShopPrice.Unit(d, p, v, now, out old, out of);
            return ShopOrders.Money(pr) + (old.HasValue ? " بدل " + ShopOrders.Money(old.Value) : "") + (p.Available ? "" : " (خلص)") + (string.IsNullOrWhiteSpace(desc) ? "" : " · " + Plain(desc, 140));
        }

        static void JsonLd(StringBuilder h, ShopFrontHandler.Ctx x, PageInfo p, string url, string shopName, string img)
        {
            var list = new List<object>();
            if (p.T == "home") list.Add(new Dictionary<string, object> { { "@context", "https://schema.org" }, { "@type", "Store" }, { "name", shopName }, { "url", x.Row.Url }, { "image", img }, { "telephone", ShopJson.Str(ShopJson.At(ShopJson.Sub(x.Row.Settings, "contact"), "phone")) } });
            if (p.T == "p")
            {
                decimal lo = decimal.MaxValue, hi = 0; decimal? old; ShopOffer of;
                foreach (var v in p.Prod.Vars.Where(y => y.Active)) { var u = ShopPrice.Unit(x.Data, p.Prod, v, x.Now, out old, out of); lo = Math.Min(lo, u); hi = Math.Max(hi, u); }
                var offer = new Dictionary<string, object> { { "@type", hi > lo ? "AggregateOffer" : "Offer" }, { "priceCurrency", "EGP" }, { "availability", "https://schema.org/" + (p.Prod.Available ? "InStock" : "OutOfStock") }, { "url", url }, { "itemCondition", "https://schema.org/NewCondition" } };
                if (hi > lo) { offer["lowPrice"] = lo; offer["highPrice"] = hi; offer["offerCount"] = p.Prod.Vars.Count(y => y.Active); } else offer["price"] = lo == decimal.MaxValue ? p.Prod.Price : lo;
                var prod = new Dictionary<string, object> { { "@context", "https://schema.org" }, { "@type", "Product" }, { "name", Pick(p.Prod.Name, p.Prod.NameEn, x.Lang) }, { "description", Plain(Pick(p.Prod.Descr, p.Prod.DescrEn, x.Lang), 500) }, { "image", p.Prod.Pics.Select(y => ShopImages.Url(y.Path)).ToArray() }, { "offers", offer } };
                if (!string.IsNullOrEmpty(p.Prod.Sku)) prod["sku"] = p.Prod.Sku;
                if (!string.IsNullOrEmpty(p.Prod.Brand)) prod["brand"] = new Dictionary<string, object> { { "@type", "Brand" }, { "name", p.Prod.Brand } };
                list.Add(prod);
            }
            if (p.T == "p" || p.T == "cat")
            {
                var items = new List<object>(); int n = 1; Action<string, string> add = (name, u) => items.Add(new Dictionary<string, object> { { "@type", "ListItem" }, { "position", n++ }, { "name", name }, { "item", u } });
                add(shopName, x.Row.Url); ShopCat k = p.Cat, up = null; if (k == null && p.Prod != null) x.Data.CatById.TryGetValue(p.Prod.MainCat, out k);
                if (k != null && k.ParentId.HasValue) x.Data.CatById.TryGetValue(k.ParentId.Value, out up);
                if (up != null) add(Pick(up.Name, up.NameEn, x.Lang), x.Row.Url + "/c/" + up.Slug); if (k != null) add(Pick(k.Name, k.NameEn, x.Lang), x.Row.Url + "/c/" + k.Slug);
                if (p.Prod != null) add(Pick(p.Prod.Name, p.Prod.NameEn, x.Lang), url);
                list.Add(new Dictionary<string, object> { { "@context", "https://schema.org" }, { "@type", "BreadcrumbList" }, { "itemListElement", items } });
            }
            foreach (var o in list) h.Append("<script type=\"application/ld+json\">").Append(Safe(Web.Js.Serialize(o))).Append("</script>");
        }

        // the page's text without any script: for Google, for links shared on WhatsApp, and when the script doesn't load
        static void Ssr(StringBuilder h, ShopFrontHandler.Ctx x, PageInfo p, string shopName)
        {
            var d = x.Data; string lang = x.Lang, pre = x.Prefix; bool en = lang == "en";
            h.Append("<p><a href=\"").Append(pre).Append("/\"><b>").Append(H(shopName)).Append("</b></a></p><nav>");
            foreach (var k in d.Cats.Where(y => !y.ParentId.HasValue && d.CatShown(y)).Take(20)) h.Append("<a href=\"").Append(pre).Append("/c/").Append(k.Slug).Append("\">").Append(H(Pick(k.Name, k.NameEn, lang))).Append("</a> · ");
            h.Append("</nav>");
            Action<IEnumerable<ShopProd>> list = ps => { h.Append("<ul>"); foreach (var y in ps) { decimal? old; ShopOffer of; var u = ShopPrice.Unit(d, y, y.Vars.FirstOrDefault(y.Can) ?? y.Vars.FirstOrDefault(), x.Now, out old, out of); h.Append("<li><a href=\"").Append(pre).Append("/p/").Append(y.Slug).Append("\">").Append(H(Pick(y.Name, y.NameEn, lang))).Append("</a> ").Append(H(ShopOrders.Money(u))).Append(y.Available ? "" : en ? " (sold out)" : " (خلص)").Append("</li>"); } h.Append("</ul>"); };
            if (p.T == "p")
            {
                var y = p.Prod; decimal? old; ShopOffer of; var u = ShopPrice.Unit(d, y, y.Vars.FirstOrDefault(y.Can) ?? y.Vars.FirstOrDefault(), x.Now, out old, out of);
                h.Append("<h1>").Append(H(Pick(y.Name, y.NameEn, lang))).Append("</h1>"); if (y.Pics.Count > 0) h.Append("<img src=\"").Append(H(ShopImages.Url(y.Pics[0].Path, true))).Append("\" width=\"").Append(y.Pics[0].W).Append("\" height=\"").Append(y.Pics[0].H).Append("\" alt=\"").Append(H(y.Pics[0].Alt ?? y.Name)).Append("\" style=\"max-width:420px\">");
                h.Append("<p><b>").Append(H(ShopOrders.Money(u))).Append("</b>").Append(old.HasValue ? " <s>" + H(ShopOrders.Money(old.Value)) + "</s>" : "").Append("</p><div style=\"white-space:pre-line\">").Append(H(Pick(y.Descr, y.DescrEn, lang))).Append("</div>");
                list(ShopView.Related(d, y, 6));
            }
            else if (p.T == "cat") { h.Append("<h1>").Append(H(Pick(p.Cat.Name, p.Cat.NameEn, lang))).Append("</h1><p>").Append(H(Pick(p.Cat.Descr, p.Cat.DescrEn, lang))).Append("</p>"); foreach (var k in p.Cat.Kids.Where(d.CatShown)) h.Append("<p><a href=\"").Append(pre).Append("/c/").Append(k.Slug).Append("\">").Append(H(Pick(k.Name, k.NameEn, lang))).Append("</a></p>"); var under = d.Under(p.Cat.Id); list(d.Shown.Where(y => y.Cats.Any(under.Contains)).Take(60)); }
            else if (p.T == "page") { h.Append("<h1>").Append(H(Pick(p.Page.Title, p.Page.TitleEn, lang))).Append("</h1><div style=\"white-space:pre-line\">").Append(H(Pick(p.Page.Body, p.Page.BodyEn, lang))).Append("</div>"); }
            else if (p.T == "404") { h.Append("<h1>").Append(en ? "This page isn't here" : "الصفحة دي مش موجودة").Append("</h1>"); list(d.Shown.Take(8)); }
            else { h.Append("<h1>").Append(H(shopName)).Append("</h1>"); list(p.T == "offers" ? d.Shown.Where(y => { decimal? old; ShopOffer of; ShopPrice.Unit(d, y, y.Vars.FirstOrDefault(), x.Now, out old, out of); return old.HasValue; }).Take(60) : d.Shown.Take(p.T == "all" ? 100 : 24)); }
            h.Append("<p>"); foreach (var g in d.Pages.Where(y => !y.Hidden && y.InFooter)) h.Append("<a href=\"").Append(pre).Append("/page/").Append(g.Slug).Append("\">").Append(H(Pick(g.Title, g.TitleEn, lang))).Append("</a> · "); h.Append("</p>");
        }

        // ---------- a store that isn't open ----------
        public static void Closed(SqlConnection c, ShopFrontHandler.Ctx x, HttpResponse res)
        {
            bool lapsed = x.Row.Status == "live";
            res.StatusCode = x.Row.Status == "live" ? 503 : 404;
            if (lapsed) res.AddHeader("Retry-After", "86400");
            Message(res, lapsed ? "المتجر مقفول مؤقتًا" : x.Row.Status == "closed" ? "المتجر ده مقفول" : "المتجر ده لسه مافتحش", lapsed ? "المتجر هيرجع يشتغل قريب. لو ليك طلب عنده، كلّم صاحب المتجر." : x.Row.Status == "closed" ? "لو ليك طلب عنده، كلّم صاحب المتجر." : "استنّانا قريب.");
        }

        public static void Message(HttpResponse res, string title, string text)
        {
            res.ContentType = "text/html; charset=utf-8"; Keep(res, 0); res.AddHeader("X-Robots-Tag", "noindex");
            res.Write("<!doctype html><html lang=\"ar\" dir=\"rtl\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\"><title>" + H(title) + "</title><style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f3f5f4;color:#17211d;font:17px/1.8 system-ui,'Segoe UI',Tahoma,sans-serif;padding:20px}main{text-align:center;max-width:420px}h1{font-size:26px;margin:0 0 8px}p{margin:0;color:#55635d}@media(prefers-color-scheme:dark){body{background:#121815;color:#e7eeea}p{color:#a1b0a9}}</style></head><body><main><h1>" + H(title) + "</h1><p>" + H(text) + "</p></main></body></html>");
        }

        // sellystore.online itself
        public static void Root(string path, HttpResponse res)
        {
            if (path == "/robots.txt") { res.ContentType = "text/plain"; res.Write("User-agent: *\nAllow: /\n"); return; }
            if (path != "/") { res.StatusCode = 404; Message(res, "الصفحة دي مش موجودة", "ارجع لـ sellystore.online"); return; }
            // the page about the stores shows once they are open to every merchant
            string on = null; try { using (var c = Db.Open()) on = Billing.Get(c, "ShopOn"); } catch (Exception ex) { Log.Error(ex); }
            var f = on == "1" ? Load("~/shop/root.html") : null;
            if (f == null) { Message(res, "SellyStore", "متاجر جاهزة باسمك من Sellybot. قريب."); return; }
            string tpls; using (var c = Db.Open()) { var open = new HashSet<string>(ShopTemplates.List(c)); tpls = Web.Js.Serialize(ShopTemplates.Cards().Where(x => open.Contains(x.Id)).Select(x => new[] { x.Id, x.Name[0], x.For }).ToArray()); }
            res.ContentType = "text/html; charset=utf-8"; Keep(res, 60);
            res.Write(Encoding.UTF8.GetString(f.Data).TrimStart('\uFEFF').Replace("/*TPL*/[]", Safe(tpls)));
        }
    }
}
