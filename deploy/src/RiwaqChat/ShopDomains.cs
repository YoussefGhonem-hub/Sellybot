// «دومين خاص بالتاجر»: the merchant's own domain (nour.com) through Cloudflare for SaaS custom hostnames on the sellystore.online zone.
// The owner types the domain in «متجري»; we register it (and its www when it's a bare domain) as custom hostnames, show the DNS records
// Cloudflare needs (a CNAME to our target, and the TXT Cloudflare gives to check the domain is theirs), and check every few minutes
// (Jobs, lane "due") until Cloudflare says the hostname and its certificate are active. Then the store answers there with HTTPS: the
// Worker asks us which store a host is (Lookup, kept a few minutes), the *.sellystore.online link moves to it (Moved, 301), and every
// link the store makes uses it (Shop.Url -> Url). Removing it goes back to the sellystore.online link. One domain (and its www) per
// store; a domain on another store is refused. The token and the zone id are typed by the super admin (encrypted, never sent back);
// until they and the CNAME target are set, owners don't see any of it. Base address: web.config CloudflareApi (a fake in the tests).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class ShopDomains
    {
        public const string Schema = @"
-- Host: what the owner typed, without www (nour.com, or a subdomain: shop.nour.com). Apex: a bare domain, so its www is added too.
-- Status: adding / pending (waiting for the records or the certificate) / live. Main: where the store answers once live.
IF OBJECT_ID('dbo.ShopDomains') IS NULL
BEGIN
  CREATE TABLE dbo.ShopDomains(StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Host NVARCHAR(253) NOT NULL, Apex BIT NOT NULL, Main NVARCHAR(253) NULL, Status NVARCHAR(8) NOT NULL DEFAULT 'adding',
    CfId NVARCHAR(40) NULL, CfWww NVARCHAR(40) NULL, Info NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), CheckedAt DATETIME2 NULL, LiveAt DATETIME2 NULL, ToldAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopDomains_Host ON dbo.ShopDomains(Host);
END
-- a custom hostname to take off Cloudflare that didn't go the first time
IF OBJECT_ID('dbo.ShopDomainDrops') IS NULL
  CREATE TABLE dbo.ShopDomainDrops(CfId NVARCHAR(40) NOT NULL PRIMARY KEY, Tries INT NOT NULL DEFAULT 0, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
";

        // ---------- the admin's settings ----------
        class Cf { public string Token, Zone, Target; }
        static string Open_(SqlConnection c, string k) { var v = Billing.Get(c, k); return string.IsNullOrEmpty(v) ? null : Secrets.Unprotect(v); }
        static Cf Conf(SqlConnection c)
        {
            if (!Shops.Ready) return null;
            var k = new Cf { Token = Open_(c, "ShopCfToken"), Zone = Open_(c, "ShopCfZone"), Target = Billing.Get(c, "ShopCfTarget") };
            return string.IsNullOrEmpty(k.Token) || string.IsNullOrEmpty(k.Zone) || string.IsNullOrEmpty(k.Target) ? null : k;
        }
        public static bool On(SqlConnection c) { return Conf(c) != null; }
        static string Api { get { return Web.Setting("CloudflareApi", "https://api.cloudflare.com/client/v4").TrimEnd('/'); } }
        // the tests' addresses carry a port (ShopDomain = localhost:5855); live there is none
        static string Port { get { var d = Shops.Domain; int i = d.IndexOf(':'); return i < 0 ? "" : d.Substring(i); } }
        static string Root { get { var d = Shops.Domain; int i = d.IndexOf(':'); return (i < 0 ? d : d.Substring(0, i)).ToLowerInvariant(); } }

        // ---------- the domains, in memory (the Worker's questions and every link the store makes read them) ----------
        class Dom { public int StoreId; public string Slug, Host, Main, Status; public bool Apex; }
        class Snap { public Dictionary<string, Dom> Hosts = new Dictionary<string, Dom>(); public Dictionary<int, Dom> Stores = new Dictionary<int, Dom>(); public DateTime At; }
        static volatile Snap snap; static int loading;
        public static void Touch() { var s = snap; if (s != null) s.At = DateTime.MinValue; }
        static Snap Now()
        {
            var s = snap; if (s != null && DateTime.UtcNow - s.At < TimeSpan.FromSeconds(30)) return s;
            if (!Db.Configured || !Shops.Ready) return s ?? new Snap { At = DateTime.UtcNow };
            if (s != null && Interlocked.Exchange(ref loading, 1) == 1) return s;   // another request is reading them
            try
            {
                var n = new Snap { At = DateTime.UtcNow };
                using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "SELECT d.StoreId, d.Host, d.Apex, d.Main, d.Status, s.Slug FROM dbo.ShopDomains d JOIN dbo.Shops s ON s.StoreId=d.StoreId")) using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var d = new Dom { StoreId = (int)r[0], Host = (string)r[1], Apex = (bool)r[2], Main = r[3] as string, Status = (string)r[4], Slug = (string)r[5] };
                        n.Stores[d.StoreId] = d; n.Hosts[d.Host] = d; if (d.Apex) n.Hosts["www." + d.Host] = d;
                    }
                return snap = n;
            }
            catch (Exception ex) { Log.Error(ex); return s ?? new Snap { At = DateTime.UtcNow }; }
            finally { Interlocked.Exchange(ref loading, 0); }
        }
        static string Url(Dom d) { return Shops.Scheme + "://" + d.Main + Port; }
        // Shop.Url: the merchant's domain once it's live, otherwise null (the sellystore.online link)
        public static string Url(Shop sh)
        {
            if (sh == null) return null; Dom d;
            return Now().Stores.TryGetValue(sh.StoreId, out d) && d.Status == "live" && !string.IsNullOrEmpty(d.Main) ? Url(d) : null;
        }

        // the Worker: which store is this host? {s: slug, m: where it answers once live ("" while waiting)}; 404 = none
        static readonly Regex HostRx = new Regex(@"^(?=.{4,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$");
        public static void Lookup(string path, HttpResponse res)
        {
            res.Cache.SetCacheability(HttpCacheability.NoCache); res.Cache.SetNoStore();
            if (!Web.Allow("shop-host", 3000, TimeSpan.FromMinutes(1))) { res.StatusCode = 429; return; }
            var host = (path ?? "").TrimStart('/').ToLowerInvariant(); Dom d;
            if (!HostRx.IsMatch(host) || !Now().Hosts.TryGetValue(host, out d) || d.Status == "adding") { Web.Json(res, 404, new Dictionary<string, object> { { "s", "" } }); return; }
            Web.Json(res, 200, new Dictionary<string, object> { { "s", d.Slug }, { "m", d.Status == "live" ? d.Main ?? "" : "" } });
        }
        // A page asked on the merchant's host (the Worker says which, X-Shop-Host) must be this store's; one asked on the old link of a
        // store whose domain is live moves there (the store's own files, its actions and the preview stay where they are).
        public static bool Moved(ShopFrontHandler.Ctx x, HttpRequest req, HttpResponse res)
        {
            var via = (req.Headers["X-Shop-Host"] ?? "").ToLowerInvariant(); Dom d; Now().Stores.TryGetValue(x.Row.StoreId, out d);
            if (via.Length > 0)
            {
                if (d != null && (via == d.Host || (d.Apex && via == "www." + d.Host))) return false;
                res.StatusCode = 404; ShopPages.Message(res, "مفيش متجر على العنوان ده", "اتأكد من اللينك واكتبه تاني."); return true;
            }
            if (d == null || d.Status != "live" || string.IsNullOrEmpty(d.Main) || (req.HttpMethod != "GET" && req.HttpMethod != "HEAD")) return false;
            var p = x.Path ?? "/"; if (p.StartsWith("/_", StringComparison.Ordinal) || p.StartsWith("/api/", StringComparison.Ordinal) || p == "/widget.js") return false;
            var q = req.Headers["X-Shop-Query"] ?? ""; if (q.Length > 600 || !q.StartsWith("?") || q.Any(char.IsControl)) q = "";
            res.StatusCode = 301; res.AddHeader("Location", Url(d) + HttpUtility.UrlPathEncode(p) + q); res.Cache.SetCacheability(HttpCacheability.NoCache); return true;
        }

        // ---------- what the owner types ----------
        // the region's two-part endings: nour.com.eg is a bare domain, shop.nour.com is not
        static readonly HashSet<string> Two = new HashSet<string>("com.eg net.eg org.eg edu.eg sci.eg info.eg name.eg tv.eg eun.eg com.sa net.sa org.sa com.kw com.qa com.bh com.om com.jo com.lb com.ly com.iq co.ae com.tr co.uk org.uk co.za com.au".Split(' '));
        static string Reg(string host) { var l = host.Split('.'); int n = l.Length >= 3 && Two.Contains(l[l.Length - 2] + "." + l[l.Length - 1]) ? 3 : 2; return string.Join(".", l.Skip(Math.Max(0, l.Length - n))); }
        public static string Norm(string typed, out bool apex)
        {
            apex = false; var s = (typed ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"^[a-z]+://", ""); s = Regex.Split(s, @"[/?#\\\s]")[0]; s = Regex.Replace(s, @":\d*$", "").TrimEnd('.');
            if (s.Length == 0) throw new ShopException("host", "اكتب الدومين، زي nour.com");
            if (s.Any(ch => ch > 127)) try { s = new IdnMapping().GetAscii(s); } catch { throw new ShopException("host", "الدومين ده مش مكتوب صح."); }
            if (s.StartsWith("www.", StringComparison.Ordinal)) s = s.Substring(4);
            if (!HostRx.IsMatch(s) || s.Split('.').Length < 2) throw new ShopException("host", "الدومين ده مش مكتوب صح. اكتبه زي nour.com");
            foreach (var mine in new[] { Root, "sellystore.online", "sellybot.online", (Web.Setting("CanonicalHost", "") ?? "").ToLowerInvariant() }.Where(m => m.Length > 0))
                if (s == mine || s.EndsWith("." + mine, StringComparison.Ordinal)) throw new ShopException("host", "ده دومين SellyStore نفسه. اكتب الدومين اللي انت اشتريته.");
            apex = Reg(s) == s; return s;
        }

        // ---------- Cloudflare ----------
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        public class CfError : Exception { public int Status; public string Raw; public CfError(int status, string message, string raw) : base(message) { Status = status; Raw = raw; } }
        static async Task<Dictionary<string, object>> Call(Cf k, HttpMethod m, string path, object body)
        {
            using (var q = new HttpRequestMessage(m, Api + "/zones/" + Uri.EscapeDataString(k.Zone) + path))
            {
                q.Headers.TryAddWithoutValidation("Authorization", "Bearer " + k.Token);
                if (body != null) q.Content = new StringContent(ShopJson.Write(body), Encoding.UTF8, "application/json");
                HttpResponseMessage r;
                try { r = await Http.SendAsync(q); } catch (Exception ex) { throw new CfError(0, "Cloudflare مارّدتش. جرّب تاني بعد شوية.", "0 " + ex.GetType().Name + ": " + ex.Message); }
                using (r)
                {
                    var t = await r.Content.ReadAsStringAsync(); var d = ShopJson.Obj(t);
                    if ((int)r.StatusCode >= 400 || !ShopJson.Bool(ShopJson.At(d, "success")))
                    {
                        var e = ShopJson.List(ShopJson.At(d, "errors")).OfType<Dictionary<string, object>>().FirstOrDefault();
                        throw new CfError((int)r.StatusCode, "Cloudflare رفضت الطلب.", e == null ? ((int)r.StatusCode + " " + (t.Length > 200 ? t.Substring(0, 200) : t)) : ShopJson.Str(ShopJson.At(e, "code")) + " " + ShopJson.Str(ShopJson.At(e, "message"), 300));
                    }
                    return d;
                }
            }
        }
        // a new custom hostname (or ours already there, from a removal that didn't go): its id
        static async Task<string> Create(Cf k, string host)
        {
            try
            {
                var d = await Call(k, HttpMethod.Post, "/custom_hostnames", new Dictionary<string, object> { { "hostname", host }, { "ssl", new Dictionary<string, object> { { "method", "http" }, { "type", "dv" }, { "settings", new Dictionary<string, object> { { "min_tls_version", "1.2" } } } } } });
                return ShopJson.Str(ShopJson.At(d, "result", "id"), 40);
            }
            catch (CfError ex) when (ex.Status == 409 || Regex.IsMatch(ex.Raw ?? "", "duplicate|already exists|1406", RegexOptions.IgnoreCase))
            {
                var l = await Call(k, HttpMethod.Get, "/custom_hostnames?hostname=" + Uri.EscapeDataString(host), null);
                var f = ShopJson.List(ShopJson.At(l, "result")).OfType<Dictionary<string, object>>().FirstOrDefault(x => ShopJson.Str(ShopJson.At(x, "hostname")) == host);
                if (f == null) throw; return ShopJson.Str(ShopJson.At(f, "id"), 40);
            }
        }
        static async Task Remove(SqlConnection c, Cf k, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try { await Call(k, HttpMethod.Delete, "/custom_hostnames/" + Uri.EscapeDataString(id), null); }
            catch (CfError ex) when (ex.Status == 404) { }
            catch (Exception ex) { Log.Error(ex); using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopDomainDrops WHERE CfId=@i) INSERT INTO dbo.ShopDomainDrops(CfId) VALUES(@i)", "@i", id)) cmd.ExecuteNonQuery(); }
        }
        // what Cloudflare says about one hostname, kept as it is: {n, s, ssl, own: [name, value], txt: [[name, value]], err: [...]}
        static Dictionary<string, object> Read(Dictionary<string, object> r)
        {
            var ov = ShopJson.Sub(r, "ownership_verification"); var ssl = ShopJson.Sub(r, "ssl");
            var txt = ShopJson.List(ShopJson.At(ssl, "validation_records")).OfType<Dictionary<string, object>>().Where(v => ShopJson.Str(ShopJson.At(v, "txt_name")).Length > 0).Select(v => (object)new[] { ShopJson.Str(ShopJson.At(v, "txt_name"), 253), ShopJson.Str(ShopJson.At(v, "txt_value"), 300) }).Take(4).ToArray();
            var err = ShopJson.List(ShopJson.At(r, "verification_errors")).Concat(ShopJson.List(ShopJson.At(ssl, "validation_errors")).OfType<Dictionary<string, object>>().Select(e => ShopJson.At(e, "message"))).Select(e => ShopJson.Str(e, 300)).Where(e => e.Length > 0).Take(4).ToArray();
            return new Dictionary<string, object> { { "n", ShopJson.Str(ShopJson.At(r, "hostname"), 253) }, { "s", ShopJson.Str(ShopJson.At(r, "status"), 30) }, { "ssl", ShopJson.Str(ShopJson.At(ssl, "status"), 40) },
                { "own", ShopJson.Str(ShopJson.At(ov, "name")).Length > 0 ? new[] { ShopJson.Str(ShopJson.At(ov, "name"), 253), ShopJson.Str(ShopJson.At(ov, "value"), 300) } : null }, { "txt", txt }, { "err", err } };
        }
        static bool Live(Dictionary<string, object> h) { return h != null && ShopJson.Str(ShopJson.At(h, "s")) == "active" && ShopJson.Str(ShopJson.At(h, "ssl")) == "active"; }

        class Row { public int StoreId; public string Host, Main, Status, CfId, CfWww, Info; public bool Apex; public DateTime CreatedAt; public DateTime? LiveAt, CheckedAt; }
        static Row Get(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopDomains WHERE StoreId=@s", "@s", storeId)) using (var r = cmd.ExecuteReader())
                return !r.Read() ? null : new Row { StoreId = storeId, Host = (string)r["Host"], Apex = (bool)r["Apex"], Main = r["Main"] as string, Status = (string)r["Status"], CfId = r["CfId"] as string, CfWww = r["CfWww"] as string, Info = r["Info"] as string, CreatedAt = (DateTime)r["CreatedAt"], LiveAt = r["LiveAt"] as DateTime?, CheckedAt = r["CheckedAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["CheckedAt"], DateTimeKind.Utc) : (DateTime?)null };
        }
        // ask Cloudflare (creating what's missing), then write down what it said; no connection is held while it answers
        static async Task Sync(Cf k, int storeId)
        {
            Row d; using (var c = Db.Open()) d = Get(c, storeId); if (d == null) return;
            var hosts = new List<string> { d.Host }; if (d.Apex) hosts.Add("www." + d.Host);
            var ids = new[] { d.CfId, d.Apex ? d.CfWww : null }; var info = new Dictionary<string, object>[hosts.Count];
            for (int i = 0; i < hosts.Count; i++)
            {
                try
                {
                    if (string.IsNullOrEmpty(ids[i])) ids[i] = await Create(k, hosts[i]);
                    info[i] = Read(ShopJson.Sub(await Call(k, HttpMethod.Get, "/custom_hostnames/" + Uri.EscapeDataString(ids[i]), null), "result"));
                }
                catch (CfError ex) when (ex.Status == 404) { ids[i] = null; }   // gone on Cloudflare's side: made again next time
                catch (CfError ex) { info[i] = new Dictionary<string, object> { { "n", hosts[i] }, { "s", "" }, { "ssl", "" }, { "own", null }, { "txt", new object[0] }, { "err", new[] { ex.Raw ?? ex.Message } } }; }
            }
            string main = d.Apex ? (Live(info[0]) ? d.Host : Live(info[1]) ? "www." + d.Host : null) : (Live(info[0]) ? d.Host : null);
            var json = ShopJson.Write(new Dictionary<string, object> { { "h", info.Where(x => x != null).ToArray() } });
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopDomains SET CfId=@a, CfWww=@w, Info=@i, Main=@m, Status=@st, CheckedAt=SYSUTCDATETIME(), LiveAt=CASE WHEN @st='live' THEN ISNULL(LiveAt, SYSUTCDATETIME()) ELSE NULL END, ToldAt=CASE WHEN @st='live' THEN ToldAt ELSE NULL END WHERE StoreId=@s AND Host=@h",
                "@a", (object)ids[0] ?? DBNull.Value, "@w", (object)ids[1] ?? DBNull.Value, "@i", json, "@m", (object)main ?? DBNull.Value, "@st", main != null ? "live" : "pending", "@s", storeId, "@h", d.Host)) cmd.ExecuteNonQuery();
            Touch();
        }

        // ---------- «متجري» ----------
        public static Dictionary<string, object> OwnerJson(SqlConnection c, Shop sh)
        {
            var k = Conf(c); if (k == null) return new Dictionary<string, object> { { "on", false } };
            var d = Get(c, sh.StoreId); var o = new Dictionary<string, object> { { "on", true }, { "target", k.Target }, { "old", Shops.Address(sh.Slug) } };
            if (d == null) return o;
            var info = ShopJson.List(ShopJson.At(ShopJson.Obj(d.Info), "h")).OfType<Dictionary<string, object>>().ToList();
            Func<string, Dictionary<string, object>> of = n => info.FirstOrDefault(x => ShopJson.Str(ShopJson.At(x, "n")) == n);
            string reg = d.Apex ? d.Host : Reg(d.Host), www = "www." + d.Host;
            Func<string, string> rel = n => n == reg ? "@" : n.EndsWith("." + reg) ? n.Substring(0, n.Length - reg.Length - 1) : n;
            var recs = new List<object>();
            Action<string, string, string, bool, string> add = (type, full, value, need, note) => recs.Add(new Dictionary<string, object> { { "type", type }, { "name", rel(full) }, { "full", full }, { "value", value }, { "need", need }, { "note", note } });
            if (d.Apex)
            {
                add("CNAME", www, k.Target, true, "ده الأساسي: متجرك هيفتح عليه.");
                add("CNAME", d.Host, k.Target, false, "لو شركة الدومين بتسمح بـ CNAME (أو ALIAS أو ANAME) على الاسم من غير www. لو مش بتسمح، اعمل عندهم «تحويل» (forwarding) من " + d.Host + " على https://" + www + " وكفاية.");
            }
            else add("CNAME", d.Host, k.Target, true, "متجرك هيفتح عليه.");
            foreach (var h in info)
            {
                var own = ShopJson.List(ShopJson.At(h, "own")); if (own.Count == 2) add("TXT", ShopJson.Str(own[0], 253), ShopJson.Str(own[1], 300), ShopJson.Str(ShopJson.At(h, "n")) != d.Host || !d.Apex, "عشان Cloudflare تتأكد إن الدومين ده بتاعك. تقدر تشيله بعد ما يشتغل.");
                foreach (var t in ShopJson.List(ShopJson.At(h, "txt")).Select(ShopJson.List).Where(t => t.Count == 2)) add("TXT", ShopJson.Str(t[0], 253), ShopJson.Str(t[1], 300), true, "عشان شهادة الأمان (https).");
            }
            var main = d.Apex ? of(www) : of(d.Host); var bare = d.Apex ? of(d.Host) : null;
            Func<Dictionary<string, object>, string> st = h => h == null ? "" : ShopJson.Str(ShopJson.At(h, "s"));
            string words;
            if (d.Status == "adding") words = "بنسجّل الدومين عند Cloudflare...";
            else if (d.Status == "live") words = "شغال ✅ متجرك بيفتح على " + d.Main + " بشهادة أمان (https)، واللينك القديم بيحوّل عليه لوحده." + (d.Apex && d.Main == www && !Live(bare) ? " الاسم من غير www لسه مش شغال: لو شركة الدومين مابتسمحش بسجله، اعمل تحويل منه على www." : "");
            else if (info.Any(h => Regex.IsMatch(st(h), "blocked"))) words = "Cloudflare وقفت الدومين ده. كلّمنا ونشوفه سوا.";
            else if (info.Any(h => st(h) == "active" && !Live(h))) words = "السجلات وصلت ✅ بنجهّز شهادة الأمان (https)، ودي بتاخد من دقايق لساعة.";
            else if (info.Any(h => ShopJson.List(ShopJson.At(h, "err")).Count > 0)) words = "لسه مش شايفين السجلات صح. راجع إنها مكتوبة زي اللي تحت بالظبط. التغيير عند شركة الدومين بياخد من دقايق لكام ساعة.";
            else words = "مستنيين السجلات. ضيفها عند الشركة اللي اشتريت منها الدومين، وإحنا بنشيّك لوحدنا كل كام دقيقة.";
            o["d"] = new Dictionary<string, object> { { "host", d.Host }, { "apex", d.Apex }, { "status", d.Status }, { "main", d.Main ?? "" }, { "url", d.Status == "live" ? Shops.Scheme + "://" + d.Main + Port : "" }, { "words", words }, { "records", recs.OfType<Dictionary<string, object>>().OrderBy(x => (bool)x["need"] ? 0 : 1).ToList() }, { "checked", Iso(d.CheckedAt) } };
            return o;
        }
        static string Iso(DateTime? t) { return t.HasValue ? t.Value.ToString("o") : ""; }

        static void Answer(HttpResponse res, Shop sh)
        {
            using (var c = Db.Open()) Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "dom", OwnerJson(c, sh) } });
        }
        public static Func<Task> Add(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var k = Conf(c); if (k == null) throw new ShopException("off", "الخدمة دي لسه مش متاحة.");
            bool apex; var host = Norm(ShopJson.Str(ShopJson.At(b, "host"), 300), out apex);
            var had = Get(c, sh.StoreId); if (had != null) throw new ShopException("one", had.Host == host ? "الدومين ده متضاف بالفعل." : "متجرك عليه دومين بالفعل (" + had.Host + "). شيله الأول لو عايز تغيّره.");
            if (!Web.Allow("shop-dom-add:" + sh.StoreId, 10, TimeSpan.FromDays(1))) throw new ShopException("many", "جرّبت دومينات كتير النهارده. جرّب بكرة.");
            // the same domain, or a part of one, on another store (whoever owns nour.com owns shop.nour.com too)
            var others = new List<string>(); using (var cmd = Db.Cmd(c, "SELECT Host FROM dbo.ShopDomains WHERE StoreId<>@s", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) others.Add((string)r[0]);
            if (others.Any(o => o == host || Reg(o) == Reg(host))) throw new ShopException("taken", "الدومين ده مربوط بمتجر تاني. لو هو بتاعك، كلّمنا.");
            try { using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopDomains(StoreId, Host, Apex) VALUES(@s, @h, @a)", "@s", sh.StoreId, "@h", host, "@a", apex)) cmd.ExecuteNonQuery(); }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { throw new ShopException("taken", "الدومين ده مربوط بمتجر تاني، أو بيتضاف دلوقتي. استنى ثواني وحدّث الصفحة."); }
            Touch(); int sid = sh.StoreId;
            return async () =>
            {
                try { await Sync(k, sid); }
                catch (Exception ex) { Log.Error(ex); }
                // nothing at all reached Cloudflare: the row goes, and the owner is told
                using (var c2 = Db.Open())
                {
                    var d = Get(c2, sid);
                    if (d != null && d.CfId == null && d.CfWww == null)
                    {
                        var why = ShopJson.List(ShopJson.At(ShopJson.Obj(d.Info), "h")).OfType<Dictionary<string, object>>().SelectMany(h => ShopJson.List(ShopJson.At(h, "err"))).Select(e => ShopJson.Str(e)).FirstOrDefault() ?? "";
                        using (var cmd = Db.Cmd(c2, "DELETE FROM dbo.ShopDomains WHERE StoreId=@s AND CfId IS NULL AND CfWww IS NULL", "@s", sid)) cmd.ExecuteNonQuery(); Touch();
                        if (why.Length > 0) Log.Error(new Exception("Cloudflare custom hostname for " + host + ": " + why));
                        throw new ShopException("cf", Regex.IsMatch(why, "^0 |Cloudflare مارّدتش") ? "Cloudflare مارّدتش. جرّب تاني بعد شوية." : "Cloudflare مارضيتش تضيف الدومين ده. اتأكد إنه مكتوب صح، ولو المشكلة فضلت كلّمنا.");
                    }
                }
                Answer(res, sh);
            };
        }
        public static Func<Task> Check(SqlConnection c, Shop sh, HttpResponse res)
        {
            var k = Conf(c); if (k == null) throw new ShopException("off", "الخدمة دي لسه مش متاحة.");
            if (Get(c, sh.StoreId) == null) throw new ShopException("none", "مفيش دومين متضاف.");
            if (!Web.Allow("shop-dom-check:" + sh.StoreId, 1, TimeSpan.FromSeconds(15))) { Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "dom", OwnerJson(c, sh) } }); return null; }
            int sid = sh.StoreId; return async () => { await Sync(k, sid); Answer(res, sh); };
        }
        public static Func<Task> Drop(SqlConnection c, Shop sh, HttpResponse res)
        {
            var d = Get(c, sh.StoreId); var k = Conf(c);
            if (d != null) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.ShopDomains WHERE StoreId=@s", "@s", sh.StoreId)) cmd.ExecuteNonQuery();
            Touch(); Shops.Bump(c, sh.StoreId);
            if (d != null && k == null) foreach (var id in new[] { d.CfId, d.CfWww }.Where(i => !string.IsNullOrEmpty(i))) using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopDomainDrops WHERE CfId=@i) INSERT INTO dbo.ShopDomainDrops(CfId) VALUES(@i)", "@i", id)) cmd.ExecuteNonQuery();
            return async () => { if (d != null && k != null) using (var c2 = Db.Open()) { await Remove(c2, k, d.CfId); await Remove(c2, k, d.CfWww); } Answer(res, sh); };
        }

        // ---------- every few minutes (Jobs, lane "due") ----------
        public static async Task Run(SqlConnection c, CancellationToken ct)
        {
            if (!Shops.Ready) return; var k = Conf(c); if (k == null) return;
            var drops = new List<string>(); using (var cmd = Db.Cmd(c, "SELECT TOP 10 CfId FROM dbo.ShopDomainDrops WHERE Tries < 30 ORDER BY At")) using (var r = cmd.ExecuteReader()) while (r.Read()) drops.Add((string)r[0]);
            foreach (var id in drops)
            {
                bool gone; try { await Call(k, HttpMethod.Delete, "/custom_hostnames/" + Uri.EscapeDataString(id), null); gone = true; } catch (CfError ex) { gone = ex.Status == 404; }
                using (var cmd = Db.Cmd(c, gone ? "DELETE FROM dbo.ShopDomainDrops WHERE CfId=@i" : "UPDATE dbo.ShopDomainDrops SET Tries=Tries+1 WHERE CfId=@i", "@i", id)) cmd.ExecuteNonQuery();
            }
            // waiting ones every couple of minutes (not one the owner is adding right now), live ones a few times a day
            var due = new List<int>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 20 StoreId FROM dbo.ShopDomains WHERE (Status IN ('adding','pending') AND CreatedAt < DATEADD(SECOND,-90,SYSUTCDATETIME()) AND (CheckedAt IS NULL OR CheckedAt < DATEADD(SECOND,-100,SYSUTCDATETIME())))
                OR (Status='live' AND (CheckedAt IS NULL OR CheckedAt < DATEADD(HOUR,-6,SYSUTCDATETIME()))) ORDER BY CheckedAt")) using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add((int)r[0]);
            foreach (var id in due) { if (ct.IsCancellationRequested) return; try { await Sync(k, id); } catch (Exception ex) { Log.Error(ex); } }
            // the owner hears once that it's live (never at night: it waits for the morning)
            if (WaNotify.QuietUntil() != null) return;
            var told = new List<int>(); using (var cmd = Db.Cmd(c, "SELECT TOP 20 StoreId FROM dbo.ShopDomains WHERE Status='live' AND ToldAt IS NULL")) using (var r = cmd.ExecuteReader()) while (r.Read()) told.Add((int)r[0]);
            foreach (var id in told)
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopDomains SET ToldAt=SYSUTCDATETIME() WHERE StoreId=@s AND Status='live' AND ToldAt IS NULL", "@s", id)) if (cmd.ExecuteNonQuery() == 0) continue;
                var s = Db.StoreById(c, id); var sh = Shops.ByStore(c, id); if (s == null || sh == null) continue; var url = Url(sh); if (url == null) continue;
                var why = await WaNotify.ToOwner(c, s, "🌐 *دومين متجرك شغال* ✅\nمتجر «" + sh.Name + "» بقى بيفتح على " + url + "\nواللينك القديم بيحوّل عليه لوحده، وكل الرسايل واللينكات بقت بالدومين الجديد.", "sb-shop-dom-" + id + "-" + url.GetHashCode().ToString("x"));
                if (why != null && why != WaNotify.NotLinked) Log.Error(new Exception("domain live message for store " + id + ": " + why));
            }
        }

        // ---------- the super admin (api/shop-domains-admin.ashx) ----------
        static string Last4(string v) { return string.IsNullOrEmpty(v) ? "" : v.Length <= 4 ? v : v.Substring(v.Length - 4); }
        public static Dictionary<string, object> AdminJson(SqlConnection c)
        {
            var tok = Open_(c, "ShopCfToken"); var zone = Open_(c, "ShopCfZone"); var list = new List<object>();
            if (Shops.Ready) using (var cmd = Db.Cmd(c, "SELECT d.Host, d.Apex, d.Main, d.Status, d.CheckedAt, d.Info, s.Slug, s.Name FROM dbo.ShopDomains d JOIN dbo.Shops s ON s.StoreId=d.StoreId ORDER BY d.CreatedAt DESC")) using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new Dictionary<string, object> { { "host", r[0] }, { "apex", r[1] }, { "main", r[2] as string ?? "" }, { "status", r[3] }, { "checked", r[4] is DateTime ? DateTime.SpecifyKind((DateTime)r[4], DateTimeKind.Utc).ToString("o") : "" }, { "err", ShopJson.List(ShopJson.At(ShopJson.Obj(r[5] as string), "h")).OfType<Dictionary<string, object>>().SelectMany(h => ShopJson.List(ShopJson.At(h, "err"))).Select(e => ShopJson.Str(e)).FirstOrDefault() ?? "" }, { "slug", r[6] }, { "name", r[7] } });
            return new Dictionary<string, object> { { "set", tok != null && zone != null && !string.IsNullOrEmpty(Billing.Get(c, "ShopCfTarget")) }, { "token", Last4(tok) }, { "zone", Last4(zone) }, { "target", Billing.Get(c, "ShopCfTarget") ?? "" }, { "suggest", "stores." + Root }, { "list", list } };
        }
        public static async Task<Dictionary<string, object>> AdminPost(SqlConnection c, Dictionary<string, object> b)
        {
            var a = ShopJson.Str(ShopJson.At(b, "a"), 10);
            if (a == "save")
            {
                string tok = ShopJson.Str(ShopJson.At(b, "token"), 200), zone = ShopJson.Str(ShopJson.At(b, "zone"), 64).ToLowerInvariant(), target = ShopJson.Str(ShopJson.At(b, "target"), 200).ToLowerInvariant().TrimEnd('.');
                if (tok.Length > 0) { if (!Regex.IsMatch(tok, @"^[A-Za-z0-9_\-]{30,200}$")) throw new ShopException("token", "الـ token مش كامل."); Billing.Set(c, "ShopCfToken", Secrets.Protect(tok)); }
                if (zone.Length > 0) { if (!Regex.IsMatch(zone, "^[a-f0-9]{32}$")) throw new ShopException("zone", "الـ Zone ID بيبقى 32 حرف ورقم."); Billing.Set(c, "ShopCfZone", Secrets.Protect(zone)); }
                if (b.ContainsKey("target")) { if (target.Length > 0 && (!HostRx.IsMatch(target) || !(target == Root || target.EndsWith("." + Root)))) throw new ShopException("target", "الـ CNAME لازم يكون اسم على " + Root + "، زي stores." + Root); if (target.Length > 0 && target != Root && !target.Substring(0, target.Length - Root.Length - 1).Contains(".") && !Shops.SlugFree(c, target.Substring(0, target.Length - Root.Length - 1), 0)) throw new ShopException("target", "الاسم ده لينك متجر. اختار اسم محجوز زي stores." + Root); Billing.Set(c, "ShopCfTarget", target); }
                return new Dictionary<string, object> { { "ok", true } };
            }
            if (a == "clear") { Billing.Set(c, "ShopCfToken", ""); Billing.Set(c, "ShopCfZone", ""); return new Dictionary<string, object> { { "ok", true } }; }
            if (a == "test")
            {
                var k = Conf(c); if (k == null) throw new ShopException("off", "اكتب الـ token والـ Zone ID والـ CNAME الأول.");
                try
                {
                    await Call(k, HttpMethod.Get, "/custom_hostnames?per_page=5", null);
                    string fo = "", fs = ""; try { var f = await Call(k, HttpMethod.Get, "/custom_hostnames/fallback_origin", null); fo = ShopJson.Str(ShopJson.At(f, "result", "origin"), 253); fs = ShopJson.Str(ShopJson.At(f, "result", "status"), 40); } catch (CfError) { }
                    return new Dictionary<string, object> { { "ok", true }, { "fallback", fo }, { "fallbackStatus", fs } };
                }
                catch (CfError ex) { return new Dictionary<string, object> { { "ok", false }, { "why", ex.Raw ?? ex.Message } }; }
            }
            throw new ShopException("bad", "مش معروف.");
        }
    }

    public class ShopDomainAdminHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }
        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response; res.Cache.SetCacheability(HttpCacheability.NoCache); if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                Dictionary<string, object> extra = null;
                using (var c = Db.Open())
                {
                    if (!MetaAdminHandler.SignedIn(ctx, c)) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (!Shops.Ready) { Web.Error(res, 503, "not_ready"); return; }
                    if (req.HttpMethod == "POST") { if (!Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; } extra = await ShopDomains.AdminPost(c, Web.Body(req, 5000)); }
                    var d = ShopDomains.AdminJson(c); if (extra != null) d["result"] = extra; Web.Json(res, 200, d);
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { Web.Error(res, 400, ex.Code, ex.Message); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }
    }
}
