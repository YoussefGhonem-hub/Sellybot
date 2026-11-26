// «تجربة شكلين»: the owner makes look B from the published look (another hero, headline, colour, order of the home sections, or the
// other template) and half the visitors see it. B keeps only what differs from A, so a change the owner publishes meanwhile reaches both.
// Who sees what: the Worker gives each visitor a random first-party cookie sbab=<test>-<a|b>-<16 hex> (worker.js), sends it here and
// keeps each half's page apart; the same visitor always gets the same look. Search engines and link previews always see A and aren't
// counted. Counted per look: visitors (once each), product views, the first add to the cart, checkouts opened, orders that weren't
// cancelled and their sales. «لسه بدري» until each look has 300 visitors and 20 orders; then a two-proportion test says how sure we are.
// One test at a time; it stops by itself after 30 days (everyone sees A again) and the owner keeps A or B.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public static class ShopAb
    {
        public const int Days = 30, MinVisitors = 300, MinOrders = 20;
        public const string Schema = @"
-- State: draft (being made) / on (half the visitors see B) / done (stopped by the owner or after 30 days). Kept = the look the owner kept.
IF OBJECT_ID('dbo.ShopTests') IS NULL
BEGIN
  CREATE TABLE dbo.ShopTests(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    B NVARCHAR(MAX) NOT NULL, State NVARCHAR(6) NOT NULL DEFAULT 'draft', StartedAt DATETIME2 NULL, EndsAt DATETIME2 NULL, StoppedAt DATETIME2 NULL, Kept CHAR(1) NULL,
    AViews INT NOT NULL DEFAULT 0, BViews INT NOT NULL DEFAULT 0, ACarts INT NOT NULL DEFAULT 0, BCarts INT NOT NULL DEFAULT 0, AChecks INT NOT NULL DEFAULT 0, BChecks INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopTests_Store ON dbo.ShopTests(StoreId, Id);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopTests_Open ON dbo.ShopTests(StoreId) WHERE State<>''done'';';
END
-- each visitor once per test (the random part of their cookie), and each order once
IF OBJECT_ID('dbo.ShopTestSeen') IS NULL
  CREATE TABLE dbo.ShopTestSeen(TestId INT NOT NULL REFERENCES dbo.ShopTests(Id) ON DELETE CASCADE, V CHAR(16) NOT NULL, Arm CHAR(1) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), PRIMARY KEY(TestId, V));
IF OBJECT_ID('dbo.ShopTestOrders') IS NULL
BEGIN
  CREATE TABLE dbo.ShopTestOrders(OrderId BIGINT NOT NULL PRIMARY KEY, TestId INT NOT NULL REFERENCES dbo.ShopTests(Id) ON DELETE CASCADE, Arm CHAR(1) NOT NULL, Sales DECIMAL(14,2) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopTestOrders_Test ON dbo.ShopTestOrders(TestId);
END
";
        // the same list as the Worker's: these never get a cookie, always see A, and aren't counted
        public static readonly Regex Bots = new Regex(@"bot|crawl|spider|slurp|facebookexternalhit|facebookcatalog|meta-externalagent|whatsapp|telegram|preview|lighthouse|headless|embedly|pinterest|skypeuri|discord|slack|bytespider|petalbot|yandex|baidu|semrush|ahrefs|mj12|python|curl|wget|go-http|java/|okhttp", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Cookie = new Regex(@"^(\d{1,9})-([ab])-([a-f0-9]{16})$");

        public class Test { public int Id, StoreId; public string State; public Dictionary<string, object> B; public DateTime? StartedAt, EndsAt, StoppedAt; public string Kept; public bool On { get { return State == "on"; } } }
        class Kept_ { public Test T; public DateTime At; }
        static readonly ConcurrentDictionary<int, Kept_> Cache = new ConcurrentDictionary<int, Kept_>();
        public static void Forget(int storeId) { Kept_ k; Cache.TryRemove(storeId, out k); }

        static Test Read(SqlDataReader r)
        {
            Func<string, DateTime?> dt = k => r[k] is DateTime ? DateTime.SpecifyKind((DateTime)r[k], DateTimeKind.Utc) : (DateTime?)null;
            return new Test { Id = (int)r["Id"], StoreId = (int)r["StoreId"], State = (string)r["State"], B = ShopJson.Obj((string)r["B"]), StartedAt = dt("StartedAt"), EndsAt = dt("EndsAt"), StoppedAt = dt("StoppedAt"), Kept = r["Kept"] as string };
        }
        // the store's test that isn't over (a draft, or on), a few seconds old at most; a test whose 30 days passed is closed here
        public static Test Open(SqlConnection c, int storeId)
        {
            Kept_ k; if (Cache.TryGetValue(storeId, out k) && DateTime.UtcNow - k.At < TimeSpan.FromSeconds(15)) return k.T;
            Test t = null;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 * FROM dbo.ShopTests WHERE StoreId=@s AND State<>'done' ORDER BY Id DESC", "@s", storeId)) using (var r = cmd.ExecuteReader()) if (r.Read()) t = Read(r);
            if (t != null && t.On && t.EndsAt.HasValue && t.EndsAt.Value <= DateTime.UtcNow)
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopTests SET State='done', StoppedAt=EndsAt WHERE Id=@i AND State='on'", "@i", t.Id)) cmd.ExecuteNonQuery();
                t = null;
            }
            Cache[storeId] = new Kept_ { T = t, At = DateTime.UtcNow };
            return t;
        }

        // ---------- look B ----------
        static readonly string[] HeroKeys = { "img", "imgM", "title", "text", "btn" };
        public static Dictionary<string, object> Apply(Dictionary<string, object> look, Dictionary<string, object> b)
        {
            var x = ShopJson.Obj(ShopJson.Write(look)); var bh = ShopJson.Sub(b, "hero");
            if (bh.Count > 0) { var hero = ShopJson.Sub(x, "hero"); foreach (var k in HeroKeys) if (bh.ContainsKey(k)) hero[k] = bh[k]; x["hero"] = hero; }
            if (b.ContainsKey("color")) x["color"] = b["color"];
            var tpl = ShopJson.Str(ShopJson.At(b, "template"), 20); if (tpl.Length > 0) x["template"] = tpl;
            var order = ShopJson.List(ShopJson.At(b, "order")).Select(o => ShopJson.Int(o, -1)).ToList();
            if (order.Count > 0)
            {
                var secs = ShopJson.List(ShopJson.At(x, "sections")); var used = new HashSet<int>(); var l = new List<object>();
                foreach (var i in order) if (i >= 0 && i < secs.Count && used.Add(i)) l.Add(secs[i]);
                for (int i = 0; i < secs.Count; i++) if (!used.Contains(i)) l.Add(secs[i]);   // a section added to A meanwhile: at the end
                x["sections"] = l;
            }
            return x;
        }
        // what the owner sends -> only what differs from the published look, every value checked by the look's own rules
        static Dictionary<string, object> CleanB(SqlConnection c, Shop sh, Dictionary<string, object> raw)
        {
            var live = sh.Look(false); var b = new Dictionary<string, object>();
            var rh = ShopJson.Sub(raw, "hero"); var h = new Dictionary<string, object>(); foreach (var k in HeroKeys) if (rh.ContainsKey(k)) h[k] = rh[k];
            var order = ShopJson.List(ShopJson.At(raw, "order")).Select(o => ShopJson.Int(o, -1)).Where(i => i >= 0 && i < 40).Distinct().ToList();
            var tpl = ShopJson.Str(ShopJson.At(raw, "template"), 20);
            var want = new Dictionary<string, object> { { "hero", h }, { "order", order.Cast<object>().ToList() } }; if (raw.ContainsKey("color")) want["color"] = ShopJson.Str(raw["color"], 9); if (tpl.Length > 0) want["template"] = tpl;
            var m = ShopLook.Clean(Apply(live, want), sh.StoreId); var lh = ShopJson.Sub(live, "hero"); var mh = ShopJson.Sub(m, "hero"); var oh = new Dictionary<string, object>();
            foreach (var k in HeroKeys) if (h.ContainsKey(k) && ShopJson.Write(ShopJson.At(mh, k)) != ShopJson.Write(ShopJson.At(lh, k))) oh[k] = mh[k];
            if (oh.Count > 0) b["hero"] = oh;
            if (raw.ContainsKey("color") && ShopJson.Str(m["color"]) != ShopJson.Str(ShopJson.At(live, "color"))) b["color"] = m["color"];
            if (tpl.Length > 0 && tpl != ShopJson.Str(ShopJson.At(live, "template"))) { if (!ShopTemplates.IsOpen(c, tpl) || (string)m["template"] != tpl) throw new ShopException("tpl", "القالب ده مش متاح."); b["template"] = tpl; }
            if (order.Count > 0 && ShopJson.Write(ShopJson.At(m, "sections")) != ShopJson.Write(ShopJson.At(ShopLook.Clean(live, sh.StoreId), "sections"))) b["order"] = order.Cast<object>().ToList();
            if (b.Count == 0) throw new ShopException("same", "شكل B لسه زي A بالظبط. غيّر حاجة واحدة على الأقل: الصورة أو العنوان أو اللون أو ترتيب الأجزاء أو القالب.");
            return b;
        }

        // ---------- the store's pages ----------
        static string Pick() { var x = new byte[1]; using (var g = RandomNumberGenerator.Create()) g.GetBytes(x); return (x[0] & 1) == 0 ? "a" : "b"; }
        static Match Mine(HttpRequest req) { var k = req.Cookies["sbab"]; return Cookie.Match(k == null ? "" : k.Value ?? ""); }
        // which half this page is for: X-Shop-Ab = "<test>-<a|b>" (the Worker keeps it with that half and gives the cookie), "0" = a test is
        // on but this visitor isn't in it (a bot); none = no test. The owner's preview shows B with ?_ab=b.
        public static void Pick(SqlConnection c, ShopFrontHandler.Ctx x, HttpRequest req, HttpResponse res)
        {
            if ((req.HttpMethod != "GET" && req.HttpMethod != "HEAD") || x.Path.StartsWith("/_", StringComparison.Ordinal) || x.Path.StartsWith("/api/", StringComparison.Ordinal)) return;
            var t = Open(c, x.Row.StoreId); if (t == null) return;
            if (x.Preview) { if (Regex.IsMatch(req.Headers["X-Shop-Query"] ?? "", "[?&]_ab=b(&|$)")) x.Look = Apply(x.Look, t.B); return; }
            if (!t.On) return;
            if (Bots.IsMatch(req.UserAgent ?? "") || string.IsNullOrWhiteSpace(req.UserAgent)) { res.AddHeader("X-Shop-Ab", "0"); return; }
            var m = Mine(req); string arm = m.Success && m.Groups[1].Value == t.Id.ToString(CultureInfo.InvariantCulture) ? m.Groups[2].Value : null;
            if (arm == null) { var p = req.Headers["X-Shop-Pick"]; arm = p == "a" || p == "b" ? p : Pick(); }
            res.AddHeader("X-Shop-Ab", t.Id.ToString(CultureInfo.InvariantCulture) + "-" + arm);
            if (arm == "b") x.Look = Apply(x.Look, t.B);
        }
        // the visitor's half of the running test, or null
        static string[] Half(SqlConnection c, ShopFrontHandler.Ctx x, HttpRequest req)
        {
            var m = Mine(req); if (!m.Success || x.Preview) return null;
            var t = Open(c, x.Row.StoreId); if (t == null || !t.On || m.Groups[1].Value != t.Id.ToString(CultureInfo.InvariantCulture)) return null;
            return new[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value };
        }
        // /_a/hit (flags: v a visit began, p a product page, c the first add to the cart, k the checkout opened)
        public static void Hit(SqlConnection c, ShopFrontHandler.Ctx x, HttpRequest req, string t)
        {
            var h = Half(c, x, req); if (h == null) return; string A = h[1] == "b" ? "B" : "A";
            if (t.Contains('v')) using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopTestSeen WHERE TestId=@t AND V=@v) INSERT INTO dbo.ShopTestSeen(TestId, V, Arm) VALUES(@t, @v, @a)", "@t", int.Parse(h[0]), "@v", h[2], "@a", h[1]))
                try { cmd.ExecuteNonQuery(); } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
            var cols = new List<string>(); if (t.Contains('p')) cols.Add(A + "Views=" + A + "Views+1"); if (t.Contains('c')) cols.Add(A + "Carts=" + A + "Carts+1"); if (t.Contains('k')) cols.Add(A + "Checks=" + A + "Checks+1");
            if (cols.Count > 0) using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopTests SET " + string.Join(", ", cols) + " WHERE Id=@t AND State='on'", "@t", int.Parse(h[0]))) cmd.ExecuteNonQuery();
        }
        // an order from the store's page: once, whatever happens (the order's id is the key)
        public static void Placed(SqlConnection c, ShopFrontHandler.Ctx x, HttpRequest req, ShopOrders.Placed p)
        {
            if (p == null || p.Again || p.Order == null || p.Order.Test) return;
            var h = Half(c, x, req); if (h == null) return;
            using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopTestOrders WHERE OrderId=@o) INSERT INTO dbo.ShopTestOrders(OrderId, TestId, Arm, Sales) VALUES(@o, @t, @a, @m)", "@o", p.Order.Id, "@t", int.Parse(h[0]), "@a", h[1], "@m", Math.Max(0, p.Order.Subtotal - p.Order.Discount)))
                try { cmd.ExecuteNonQuery(); } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
        }

        // ---------- the numbers, and what they say ----------
        static double Phi(double z)
        {
            // Abramowitz and Stegun 7.1.26
            double t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2)), y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z / 2);
            return z >= 0 ? (1 + y) / 2 : (1 - y) / 2;
        }
        // how sure we are that the two halves really order at different rates (1 - the two-sided p-value)
        public static double Sure(int va, int oa, int vb, int ob)
        {
            if (va <= 0 || vb <= 0) return 0; double pa = (double)oa / va, pb = (double)ob / vb, p = (double)(oa + ob) / (va + vb), se = Math.Sqrt(p * (1 - p) * (1.0 / va + 1.0 / vb));
            if (se <= 0) return 0; return 1 - 2 * (1 - Phi(Math.Abs(pb - pa) / se));
        }
        static string N1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
        public static Dictionary<string, object> Verdict(int va, int oa, int vb, int ob)
        {
            var d = new Dictionary<string, object> { { "ready", false }, { "better", "" }, { "sure", 0 } };
            if (va < MinVisitors || vb < MinVisitors || oa < MinOrders || ob < MinOrders)
            {
                d["text"] = "لسه بدري: محتاجين " + MinVisitors + " زائر و" + MinOrders + " طلب على الأقل لكل شكل قبل ما نقول مين باع أكتر.";
                return d;
            }
            double ra = 100.0 * oa / va, rb = 100.0 * ob / vb, s = Sure(va, oa, vb, ob); int pct = Math.Min(99, (int)Math.Floor(s * 100));
            d["ready"] = true; d["sure"] = pct;
            string hi = rb > ra ? "B" : "A", lo = hi == "B" ? "A" : "B", rh = N1(Math.Max(ra, rb)), rl = N1(Math.Min(ra, rb));
            if (Math.Abs(ra - rb) < 1e-9 || pct < 80) d["text"] = "مفيش فرق واضح بين الشكلين: " + rh + " و" + rl + " طلب لكل 100 زائر، والفرق ده ممكن يكون صدفة.";
            else if (pct < 95) d["text"] = "شكل " + hi + " باع أكتر لكل زائر لحد دلوقتي (" + rh + " طلب لكل 100 زائر، و" + lo + " " + rl + ")، بس متأكدين بنسبة " + pct + "% بس، فممكن يكون صدفة. استنى شوية كمان.";
            else { d["better"] = hi.ToLowerInvariant(); d["text"] = "شكل " + hi + " باع أكتر لكل زائر: " + rh + " طلب لكل 100 زائر، و" + lo + " " + rl + ". متأكدين بنسبة " + pct + "%."; }
            return d;
        }
        static Dictionary<string, object> Json(SqlConnection c, Test t)
        {
            if (t == null) return null;
            var v = new Dictionary<string, int> { { "a", 0 }, { "b", 0 } }; var o = new Dictionary<string, int> { { "a", 0 }, { "b", 0 } }; var m = new Dictionary<string, decimal> { { "a", 0 }, { "b", 0 } };
            using (var cmd = Db.Cmd(c, "SELECT Arm, COUNT(*) FROM dbo.ShopTestSeen WHERE TestId=@t GROUP BY Arm", "@t", t.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) v[((string)r[0]).Trim()] = (int)r[1];
            using (var cmd = Db.Cmd(c, "SELECT x.Arm, COUNT(*), ISNULL(SUM(x.Sales),0) FROM dbo.ShopTestOrders x JOIN dbo.ShopOrders s ON s.Id=x.OrderId WHERE x.TestId=@t AND s.Status<>'cancelled' GROUP BY x.Arm", "@t", t.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) { o[((string)r[0]).Trim()] = (int)r[1]; m[((string)r[0]).Trim()] = (decimal)r[2]; }
            int[] n = new int[6]; using (var cmd = Db.Cmd(c, "SELECT AViews, BViews, ACarts, BCarts, AChecks, BChecks FROM dbo.ShopTests WHERE Id=@t", "@t", t.Id)) using (var r = cmd.ExecuteReader()) if (r.Read()) for (int i = 0; i < 6; i++) n[i] = (int)r[i];
            Func<string, int, Dictionary<string, object>> arm = (k, i) => new Dictionary<string, object> { { "visitors", v[k] }, { "views", n[i] }, { "carts", n[2 + i] }, { "checkouts", n[4 + i] }, { "orders", o[k] }, { "sales", m[k] }, { "per100", v[k] > 0 ? Math.Round(100.0 * o[k] / v[k], 2) : 0 } };
            var now = DateTime.UtcNow; bool ended = t.State == "done";
            return new Dictionary<string, object> { { "id", t.Id }, { "state", t.State }, { "b", t.B }, { "started", Iso(t.StartedAt) }, { "ends", Iso(t.EndsAt) }, { "stopped", Iso(t.StoppedAt) }, { "kept", t.Kept ?? "" },
                { "byTime", ended && t.EndsAt.HasValue && t.StoppedAt.HasValue && Math.Abs((t.StoppedAt.Value - t.EndsAt.Value).TotalSeconds) < 1 },
                { "daysLeft", t.On && t.EndsAt.HasValue ? Math.Max(0, (int)Math.Ceiling((t.EndsAt.Value - now).TotalDays)) : 0 },
                { "a", arm("a", 0) }, { "bb", arm("b", 1) }, { "verdict", Verdict(v["a"], o["a"], v["b"], o["b"]) }, { "min", new[] { MinVisitors, MinOrders } } };
        }
        static string Iso(DateTime? t) { return t.HasValue ? t.Value.ToString("o") : ""; }

        // ---------- «متجري» ----------
        public static Dictionary<string, object> OwnerJson(SqlConnection c, StoreInfo s, Shop sh)
        {
            Forget(sh.StoreId); var t = Open(c, sh.StoreId); Test last = null;
            if (t == null) using (var cmd = Db.Cmd(c, "SELECT TOP 1 * FROM dbo.ShopTests WHERE StoreId=@s AND State='done' ORDER BY Id DESC", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) if (r.Read()) last = Read(r);
            var live = sh.LiveJson == null ? null : sh.Look(false);
            return new Dictionary<string, object> { { "can", sh.Status == "live" && sh.LiveJson != null && Shops.Open(c, s, sh) }, { "test", Json(c, t) }, { "last", last != null && last.Kept == null ? Json(c, last) : null },
                { "live", live == null ? null : new Dictionary<string, object> { { "hero", ShopJson.Sub(live, "hero") }, { "color", ShopJson.Str(ShopJson.At(live, "color")) }, { "template", ShopJson.Str(ShopJson.At(live, "template")) },
                    { "sections", ShopJson.List(ShopJson.At(live, "sections")).OfType<Dictionary<string, object>>().Select(x => new Dictionary<string, object> { { "t", ShopJson.Str(ShopJson.At(x, "t")) }, { "title", ShopJson.At(x, "title") }, { "on", ShopJson.Bool(ShopJson.At(x, "on"), true) } }).ToArray() } } },
                { "templates", ShopTemplates.List(c) }, { "days", Days }, { "min", new[] { MinVisitors, MinOrders } } };
        }
        public static void Save(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            if (sh.LiveJson == null) throw new ShopException("not_live", "التجربة بتبدأ بعد ما متجرك يتنشر.");
            var t = Open(c, sh.StoreId); if (t != null && t.On) throw new ShopException("on", "فيه تجربة شغالة. وقّفها الأول.");
            var json = ShopJson.Write(CleanB(c, sh, ShopJson.Sub(b, "b")));
            if (t != null) using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopTests SET B=@b WHERE Id=@i AND StoreId=@s AND State='draft'", "@b", json, "@i", t.Id, "@s", sh.StoreId)) cmd.ExecuteNonQuery();
            else using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopTests(StoreId, B) VALUES(@s, @b)", "@s", sh.StoreId, "@b", json))
                try { cmd.ExecuteNonQuery(); } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { throw new ShopException("busy", "فيه تجربة متجهزة بالفعل. حدّث الصفحة."); }
            Forget(sh.StoreId);
        }
        public static void Start(SqlConnection c, StoreInfo s, Shop sh)
        {
            if (sh.Status != "live" || sh.LiveJson == null || !Shops.Open(c, s, sh)) throw new ShopException("not_live", "التجربة بتشتغل على متجر مفتوح للزباين بس.");
            object last; using (var cmd = Db.Cmd(c, "SELECT MAX(StoppedAt) FROM dbo.ShopTests WHERE StoreId=@s", "@s", sh.StoreId)) last = cmd.ExecuteScalar();
            // the pages kept for the last test's halves have to be gone first (the Worker keeps a page a minute)
            if (last is DateTime && DateTime.UtcNow - DateTime.SpecifyKind((DateTime)last, DateTimeKind.Utc) < TimeSpan.FromMinutes(2)) throw new ShopException("soon", "استنى دقيقتين بعد آخر تجربة وابدأ.");
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopTests SET State='on', StartedAt=SYSUTCDATETIME(), EndsAt=DATEADD(DAY, @d, SYSUTCDATETIME()) WHERE StoreId=@s AND State='draft'", "@d", Days, "@s", sh.StoreId))
                if (cmd.ExecuteNonQuery() == 0) throw new ShopException("none", "جهّز شكل B الأول.");
            Forget(sh.StoreId);
        }
        // the owner keeps A or B; B becomes the published look (and the draft's) once, however many times it's pressed
        public static void Stop(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            string keep = ShopJson.Str(ShopJson.At(b, "keep"), 1); int id = ShopJson.Int(ShopJson.At(b, "id")); if (keep != "a" && keep != "b") throw new ShopException("keep", "اختار A أو B.");
            Test t = null; using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopTests WHERE Id=@i AND StoreId=@s", "@i", id, "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) if (r.Read()) t = Read(r);
            if (t == null || t.State == "draft") throw new ShopException("not_found", "التجربة دي مش موجودة.");
            string nl = null;
            using (var tx = c.BeginTransaction())
            {
                int n; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopTests SET State='done', StoppedAt=ISNULL(StoppedAt, SYSUTCDATETIME()), Kept=@k WHERE Id=@i AND StoreId=@s AND Kept IS NULL", "@k", keep, "@i", id, "@s", sh.StoreId)) { cmd.Transaction = tx; n = cmd.ExecuteNonQuery(); }
                if (n == 1 && keep == "b")
                {
                    string live, draft;
                    using (var cmd = Db.Cmd(c, "SELECT Live, Draft, DraftAt FROM dbo.Shops WITH (UPDLOCK) WHERE StoreId=@s", "@s", sh.StoreId)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) { r.Read(); live = r[0] as string; draft = r[2] is DateTime ? r[1] as string : null; } }
                    // a draft with changes not published yet gets B too; otherwise the draft is the new look
                    nl = ShopJson.Write(ShopLook.Clean(Apply(ShopJson.Obj(live), t.B), sh.StoreId)); var nd = draft == null ? nl : ShopJson.Write(ShopLook.Clean(Apply(ShopJson.Obj(draft), t.B), sh.StoreId));
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET Live=@l, Draft=@d, PublishedAt=SYSUTCDATETIME() WHERE StoreId=@s", "@l", nl, "@d", nd, "@s", sh.StoreId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                }
                tx.Commit();
            }
            if (nl != null) ShopOwner.SaveVersion(c, sh.StoreId, nl, "شكل B من «تجربة شكلين»");
            Forget(sh.StoreId); Shops.Bump(c, sh.StoreId);
        }
        public static void Drop(SqlConnection c, Shop sh)
        {
            using (var cmd = Db.Cmd(c, "DELETE FROM dbo.ShopTests WHERE StoreId=@s AND State='draft'", "@s", sh.StoreId)) cmd.ExecuteNonQuery();
            Forget(sh.StoreId);
        }
    }
}
