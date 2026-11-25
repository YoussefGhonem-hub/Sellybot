// api/shop-admin.ashx: the super admin's «المتاجر»: opening the feature, the rent's price, the Worker's secret, reviewing a store before it
// opens, closing one, reports, and the numbers of every store. Signed in as the admin only.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Http;
using System.Web;

namespace RiwaqChat
{
    public class ShopAdminHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response; res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    if (!MetaAdminHandler.SignedIn(ctx, c)) { Web.Error(res, 401, "not_logged_in"); return; }   // the super admin's own sign-in, like the other admin pages
                    if (!Shops.Ready) { Web.Error(res, 503, "not_ready", "جداول المتاجر لسه مااتعملتش."); return; }
                    if (req.HttpMethod == "GET") { Get(c, req, res); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    Post(c, Web.Body(req, 60000), res);
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { Web.Error(res, 400, ex.Code, ex.Message); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static void Get(SqlConnection c, HttpRequest req, HttpResponse res)
        {
            int id; int.TryParse(req.QueryString["id"], out id);
            if (id > 0) { Web.Json(res, 200, One(c, id)); return; }
            Web.Json(res, 200, All(c));
        }
        static string Iso(DateTime? t) { return t.HasValue ? t.Value.ToString("o") : ""; }

        public static Dictionary<string, object> All(SqlConnection c)
        {
            var list = new List<object>(); var orders = new Dictionary<int, int[]>(); var visits = new Dictionary<int, int>(); var reports = new Dictionary<int, int>(); var prods = new Dictionary<int, int>();
            using (var cmd = Db.Cmd(c, @"SELECT StoreId, COUNT(*), SUM(CASE WHEN CreatedAt>DATEADD(DAY,-30,SYSUTCDATETIME()) THEN 1 ELSE 0 END) FROM dbo.ShopOrders GROUP BY StoreId;
                SELECT StoreId, SUM(Visits) FROM dbo.ShopDaily WHERE Day>DATEADD(DAY,-30,SYSUTCDATETIME()) GROUP BY StoreId; SELECT StoreId, COUNT(*) FROM dbo.ShopReports WHERE Status='new' GROUP BY StoreId; SELECT StoreId, COUNT(*) FROM dbo.ShopProducts GROUP BY StoreId"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) orders[(int)r[0]] = new[] { (int)r[1], (int)r[2] }; r.NextResult(); while (r.Read()) visits[(int)r[0]] = (int)r[1]; r.NextResult(); while (r.Read()) reports[(int)r[0]] = (int)r[1]; r.NextResult(); while (r.Read()) prods[(int)r[0]] = (int)r[1];
            }
            foreach (var sh in Shops.All(c))
            {
                var s = Db.StoreById(c, sh.StoreId); if (s == null) continue; int[] o; int v, rp, pc; orders.TryGetValue(sh.StoreId, out o); visits.TryGetValue(sh.StoreId, out v); reports.TryGetValue(sh.StoreId, out rp); prods.TryGetValue(sh.StoreId, out pc); var until = Shops.Until(c, s, sh);
                list.Add(new Dictionary<string, object> { { "id", sh.StoreId }, { "slug", sh.Slug }, { "name", sh.Name }, { "store", s.Name }, { "storeSlug", s.Slug }, { "email", s.OwnerEmail }, { "status", sh.Status }, { "url", sh.Url }, { "biz", sh.Biz ?? "" }, { "until", Iso(until) }, { "days", until.HasValue ? (int)Math.Ceiling((until.Value - DateTime.UtcNow).TotalDays) : 0 }, { "on", Shops.HasService(c, s, sh) }, { "open", Shops.Open(c, s, sh) }, { "inPlan", Shops.InPlan(c, s) },
                    { "orders", o == null ? 0 : o[0] }, { "orders30", o == null ? 0 : o[1] }, { "visits30", v }, { "reports", rp }, { "products", pc }, { "submitted", Iso(sh.SubmittedAt) }, { "change", sh.ReviewChange }, { "pendSlug", sh.PendSlug ?? "" }, { "pendName", sh.PendName ?? "" }, { "note", sh.Status == "closed" ? sh.CloseNote ?? "" : sh.ReviewNote ?? "" }, { "created", Iso(sh.CreatedAt) }, { "tester", Shops.Tester(c, s) }, { "wipe", until.HasValue && until.Value.AddDays(Shops.KeepDays) < DateTime.UtcNow } });
            }
            var rep = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT TOP 100 r.Id, r.StoreId, r.Reason, r.Text, r.Contact, r.Page, r.Status, r.At, s.Name, s.Slug FROM dbo.ShopReports r JOIN dbo.Shops s ON s.StoreId=r.StoreId ORDER BY CASE WHEN r.Status='new' THEN 0 ELSE 1 END, r.Id DESC")) using (var r = cmd.ExecuteReader())
                while (r.Read()) rep.Add(new Dictionary<string, object> { { "id", (int)r[0] }, { "store", (int)r[1] }, { "reason", (string)r[2] }, { "text", r[3] as string ?? "" }, { "contact", r[4] as string ?? "" }, { "page", r[5] as string ?? "" }, { "status", (string)r[6] }, { "at", Iso(DateTime.SpecifyKind((DateTime)r[7], DateTimeKind.Utc)) }, { "name", (string)r[8] }, { "slug", (string)r[9] } });
            var fails = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT TOP 30 f.StoreId, f.What, f.Detail, f.At, s.Name FROM dbo.ShopFails f LEFT JOIN dbo.Shops s ON s.StoreId=f.StoreId WHERE f.At>DATEADD(DAY,-7,SYSUTCDATETIME()) ORDER BY f.Id DESC")) using (var r = cmd.ExecuteReader())
                while (r.Read()) fails.Add(new Dictionary<string, object> { { "store", (int)r[0] }, { "what", (string)r[1] }, { "detail", r[2] as string ?? "" }, { "at", Iso(DateTime.SpecifyKind((DateTime)r[3], DateTimeKind.Utc)) }, { "name", r[4] as string ?? "" } });
            int disk = 0, r2 = 0; long bytes = 0; using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CASE WHEN Place='disk' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN Place='r2' THEN 1 ELSE 0 END),0), ISNULL(SUM(CAST(Bytes AS BIGINT)),0) FROM dbo.ShopImages")) using (var r = cmd.ExecuteReader()) { r.Read(); disk = (int)r[0]; r2 = (int)r[1]; bytes = Convert.ToInt64(r[2]); }
            return new Dictionary<string, object>
            {
                { "shops", list }, { "reports", rep }, { "fails", fails }, { "domain", Shops.Domain },
                { "settings", new Dictionary<string, object> { { "on", Billing.Get(c, "ShopOn") == "1" }, { "admin", Billing.Get(c, "ShopAdmin") == "1" }, { "testers", Billing.Get(c, "ShopTesters") ?? "" }, { "price", ShopRent.Price(c) }, { "key", !string.IsNullOrEmpty(Billing.Get(c, "ShopKey")) }, { "img", Billing.Get(c, "ShopImgMode") ?? "disk" }, { "tplOff", Billing.Get(c, "ShopTplOff") ?? "" }, { "templates", ShopLook.Templates.Select(t => new Dictionary<string, object> { { "id", t }, { "there", ShopPages.Has("t/" + t + ".js") }, { "open", ShopTemplates.IsOpen(c, t) } }).ToArray() } } },
                { "images", new Dictionary<string, object> { { "disk", disk }, { "r2", r2 }, { "mb", Math.Round(bytes / 1048576.0, 1) } } }
            };
        }

        static Dictionary<string, object> One(SqlConnection c, int id)
        {
            var sh = Shops.ByStore(c, id); var s = Db.StoreById(c, id); if (sh == null || s == null) throw new ShopException("not_found", "المتجر ده مش موجود.");
            var d = ShopCache.Get(c, sh); var set = sh.Settings; string test; int score; using (var cmd = Db.Cmd(c, "SELECT BotTest, ISNULL(BotScore,-1) FROM dbo.Shops WHERE StoreId=@s", "@s", id)) using (var r = cmd.ExecuteReader()) { r.Read(); test = r[0] as string; score = (int)r[1]; }
            return new Dictionary<string, object> { { "id", id }, { "name", sh.Name }, { "slug", sh.Slug }, { "status", sh.Status }, { "url", sh.Url }, { "email", s.OwnerEmail }, { "plan", s.PlanName ?? "" }, { "contact", ShopJson.Sub(set, "contact") }, { "social", ShopJson.Sub(set, "social") }, { "legal", ShopJson.Sub(set, "legal") }, { "wa", s.WaNumber ?? "" }, { "steps", ShopOwner.Steps(c, s, sh, d) }, { "flags", ShopReview.Check(sh, d) },
                { "products", d.Products.Take(60).Select(p => new Dictionary<string, object> { { "name", p.Name }, { "price", p.Price }, { "img", p.Pics.Count > 0 ? ShopImages.Url(p.Pics[0].Path, true) : "" }, { "descr", ShopPages.Plain(p.Descr, 160) }, { "hidden", p.Status != "active" } }).ToArray() }, { "count", d.Products.Count }, { "cats", d.Cats.Select(k => k.Name).ToArray() },
                { "test", ShopJson.Arr(test) }, { "score", score }, { "template", ShopJson.Str(ShopJson.At(sh.Look(true), "template")) }, { "pendSlug", sh.PendSlug ?? "" }, { "pendName", sh.PendName ?? "" }, { "change", sh.ReviewChange }, { "logoNew", sh.ReviewChange ? Logo(sh) : "" }, { "service", ShopRent.Json(c, s, sh) } };
        }
        static string Logo(Shop sh) { string a = ShopJson.Str(ShopJson.At(sh.Look(false), "logo")), b = ShopJson.Str(ShopJson.At(sh.Look(true), "logo")); return a == b || b.Length == 0 ? "" : ShopImages.Url(b); }

        static void Post(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            string a = ShopJson.Str(ShopJson.At(b, "a"), 20); int id = ShopJson.Int(ShopJson.At(b, "id")); Action<string, object[]> run = (sql, args) => { using (var cmd = Db.Cmd(c, sql, args)) cmd.ExecuteNonQuery(); };
            Shop sh = id > 0 ? Shops.ByStore(c, id) : null; StoreInfo s = id > 0 ? Db.StoreById(c, id) : null;
            if (id > 0 && (s == null || (sh == null && a != "grant"))) throw new ShopException("not_found", "المتجر ده مش موجود.");
            switch (a)
            {
                case "settings":
                    if (b.ContainsKey("on")) Billing.Set(c, "ShopOn", ShopJson.Bool(b["on"]) ? "1" : "0");
                    if (b.ContainsKey("admin")) Billing.Set(c, "ShopAdmin", ShopJson.Bool(b["admin"]) ? "1" : "0");
                    if (b.ContainsKey("testers")) Billing.Set(c, "ShopTesters", ShopJson.Str(b["testers"], 1000).ToLowerInvariant());
                    if (b.ContainsKey("price")) Billing.Set(c, "ShopPrice", Math.Max(0, Math.Min(100000, ShopJson.Dec(b["price"]))).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    if (b.ContainsKey("tplOff")) Billing.Set(c, "ShopTplOff", string.Join(",", ShopJson.Str(b["tplOff"], 400).Split(',').Where(t => ShopLook.Templates.Contains(t))));
                    Web.Json(res, 200, All(c)); return;
                case "key": { var k = ShopJson.Bool(ShopJson.At(b, "fresh")) ? Shops.NewKey(c) : Shops.Key(c, true); Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "key", k } }); return; }
                case "worker": Web.Json(res, 200, Worker(c)); return;
                case "img":
                    if (ShopJson.Str(ShopJson.At(b, "mode"), 5) == "r2")
                    {
                        // a small picture goes to R2 and back before the stores' pictures do
                        var px = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="); string path = "0/" + Shops.Token(8) + ".png"; string err = null;
                        try { if (!ShopImages.R2(c, "PUT", path, px)) err = "الـ Worker رفض الصورة. راجع ربط R2 (IMAGES) والمفتاح."; else { var back = Http.GetByteArrayAsync(ShopImages.Url(path)).GetAwaiter().GetResult(); if (back.Length != px.Length) err = "الصورة رجعت غلط."; ShopImages.R2(c, "DELETE", path, null); } } catch (Exception ex) { err = "ماوصلناش لـ R2: " + ex.Message; }
                        if (err != null) throw new ShopException("r2", err);
                        Billing.Set(c, "ShopImgMode", "r2");
                    }
                    else Billing.Set(c, "ShopImgMode", "disk");
                    Web.Json(res, 200, All(c)); return;
                case "approve":
                    if (sh.Status != "review") throw new ShopException("state", "المتجر ده مش مستني مراجعة.");
                    if (!Shops.HasService(c, s, sh)) throw new ShopException("rent", "إيجار المتجر مش شغال. وافق على طلب الإيجار أو ادّيله أيام الأول.");
                    ShopOwner.SaveVersion(c, id, sh.DraftJson, "أول نشر");
                    run("UPDATE dbo.Shops SET Status='live', Live=Draft, DraftAt=NULL, ApprovedAt=SYSUTCDATETIME(), PublishedAt=SYSUTCDATETIME(), ReviewNote=NULL, ReviewChange=0 WHERE StoreId=@s", new object[] { "@s", id }); Shops.Bump(c, id);
                    Tell(s, "🎉 *متجرك «" + sh.Name + "» اتفتح!*\nلينكه: " + sh.Url + "\nانشره لزباينك، وكل أوردر هيوصلك هنا وفي «متجري».", "sb-shop-ok-" + id);
                    Web.Json(res, 200, All(c)); return;
                case "reject":
                    { var why = ShopJson.Str(ShopJson.At(b, "why"), 400); if (why.Length < 3) throw new ShopException("why", "اكتب السبب عشان صاحب المتجر يعدّله."); run("UPDATE dbo.Shops SET Status='draft', ReviewNote=@n WHERE StoreId=@s AND Status='review'", new object[] { "@n", why, "@s", id }); Tell(s, "📝 *متجرك «" + sh.Name + "» محتاج تعديل قبل ما يتفتح*\n" + why + "\n\nعدّله من «متجري» وابعته تاني.", "sb-shop-no-" + id + "-" + DateTime.UtcNow.Ticks); Web.Json(res, 200, All(c)); return; }
                case "close":
                    { var why = ShopJson.Str(ShopJson.At(b, "why"), 400); if (why.Length < 3) throw new ShopException("why", "اكتب سبب القفل، صاحب المتجر هيشوفه."); run("UPDATE dbo.Shops SET Status='closed', CloseNote=@n, ClosedAt=SYSUTCDATETIME() WHERE StoreId=@s", new object[] { "@n", why, "@s", id }); Shops.Bump(c, id); Purge(c, sh.Slug); Tell(s, "⛔ *متجرك «" + sh.Name + "» اتقفل*\nالسبب: " + why + "\nكلّمنا لو محتاج توضيح.", "sb-shop-close-" + id + "-" + DateTime.UtcNow.Ticks); Web.Json(res, 200, All(c)); return; }
                case "reopen": run("UPDATE dbo.Shops SET Status=CASE WHEN Live IS NULL THEN 'draft' ELSE 'live' END, CloseNote=NULL, ClosedAt=NULL WHERE StoreId=@s AND Status='closed'", new object[] { "@s", id }); Shops.Bump(c, id); Web.Json(res, 200, All(c)); return;
                case "change":   // the name, the link or the logo of an open store
                    if (ShopJson.Bool(ShopJson.At(b, "ok")))
                    {
                        if (sh.PendSlug != null) { if (!Shops.SlugFree(c, sh.PendSlug, id)) throw new ShopException("slug", "اللينك ده اتاخد."); run("DELETE FROM dbo.ShopRedirects WHERE Kind='s' AND OldSlug=@n; INSERT INTO dbo.ShopRedirects(StoreId, Kind, OldSlug, TargetId) VALUES(@s, 's', @o, 0)", new object[] { "@n", sh.PendSlug, "@o", sh.Slug, "@s", id }); }
                        string live = sh.LiveJson, logo = ShopJson.Str(ShopJson.At(sh.Look(true), "logo")); if (Logo(sh).Length > 0 && live != null) { var l = sh.Look(false); l["logo"] = logo; live = ShopJson.Write(l); }
                        run("UPDATE dbo.Shops SET Slug=ISNULL(PendSlug, Slug), Name=ISNULL(PendName, Name), PendSlug=NULL, PendName=NULL, ReviewChange=0, Live=@l WHERE StoreId=@s", new object[] { "@l", live, "@s", id });
                    }
                    else run("UPDATE dbo.Shops SET PendSlug=NULL, PendName=NULL, ReviewChange=0, ReviewNote=@n WHERE StoreId=@s", new object[] { "@n", ShopJson.Str(ShopJson.At(b, "why"), 400), "@s", id });
                    Shops.Bump(c, id); Web.Json(res, 200, All(c)); return;
                case "grant": { ShopRent.Grant(c, s, ShopJson.Int(ShopJson.At(b, "days"))); Web.Json(res, 200, All(c)); return; }
                case "slug": { var slug = ShopJson.Str(ShopJson.At(b, "slug"), 30).ToLowerInvariant(); var bad = Shops.BadSlug(slug, true); if (bad != null) throw new ShopException("slug", bad); if (!Shops.SlugFree(c, slug, id)) throw new ShopException("slug", "الاسم ده متاخد."); run("UPDATE dbo.Shops SET Slug=@v WHERE StoreId=@s", new object[] { "@v", slug, "@s", id }); Shops.Bump(c, id); Web.Json(res, 200, All(c)); return; }
                case "report": run("UPDATE dbo.ShopReports SET Status='done' WHERE Id=@i", new object[] { "@i", ShopJson.Int(ShopJson.At(b, "report")) }); Web.Json(res, 200, All(c)); return;
                case "preview": { var t = Shops.Token(16); run("INSERT INTO dbo.ShopPreviews(Token, StoreId, Kind, ExpiresAt) VALUES(@t, @s, 'admin', DATEADD(HOUR, 24, SYSUTCDATETIME()))", new object[] { "@t", t, "@s", id }); Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "url", sh.Url + "/_preview/" + t } }); return; }
                default: Web.Error(res, 404, "not_found"); return;
            }
        }

        static void Tell(StoreInfo s, string text, string idem)
        {
            int sid = s.Id;
            System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { using (var c = Db.Open()) { var x = Db.StoreById(c, sid); if (x != null) await WaNotify.ToOwner(c, x, text, idem); } } catch (Exception ex) { Log.Error(ex); } });
        }

        // the pages Cloudflare keeps for a closed store go at once (the Worker forgets them when it's told)
        static void Purge(SqlConnection c, string slug)
        {
            try { var key = Shops.Key(c); if (key == null) return; using (var m = new HttpRequestMessage(HttpMethod.Post, Shops.Address(slug) + "/_purge")) { m.Headers.TryAddWithoutValidation("X-Shop-Key", key); Http.SendAsync(m); } } catch (Exception ex) { Log.Error(ex); }
        }

        // is the Worker there, with the same secret, and can it reach us?
        static Dictionary<string, object> Worker(SqlConnection c)
        {
            var d = new Dictionary<string, object>(); var key = Shops.Key(c, true);
            try
            {
                using (var m = new HttpRequestMessage(HttpMethod.Get, Shops.Address("status") + "/_status")) { m.Headers.TryAddWithoutValidation("X-Shop-Key", key); using (var r = Http.SendAsync(m).GetAwaiter().GetResult()) { var t = r.Content.ReadAsStringAsync().GetAwaiter().GetResult(); d["code"] = (int)r.StatusCode; d["answer"] = t.Length > 400 ? t.Substring(0, 400) : t; d["ok"] = r.IsSuccessStatusCode && t.Contains("\"ok\":true"); } }
            }
            catch (Exception ex) { d["ok"] = false; d["answer"] = ex.Message; }
            return d;
        }
    }
}
