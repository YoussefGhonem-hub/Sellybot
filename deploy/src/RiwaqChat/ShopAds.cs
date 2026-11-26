// «القياس من السيرفر»: the store's ads are measured from Sellybot's server too (Meta Conversions API, TikTok Events API), next to the browser
// pixels the store already has, so a blocked pixel doesn't lose the sale. The owner pastes an access token (the pixel's id is the one in
// «قياس الإعلانات»); off until then. The browser and the server send the same event with the same id, so each network counts it once.
//   Purchase: when a web order is placed (ShopFront's /_a/order), with the event id the page made for that press of the button.
//   InitiateCheckout, AddToCart, ViewContent: the page tells /_a/ad. The value is the server's price, never the browser's.
// Every event is a row first (one per network, unique by its id), sent later in batches by the background lane, never while the customer
// waits. user_data is SHA-256 as the networks ask (phone with the country code, names, city, governorate, country), plus the IP, the
// browser and the networks' own cookies. The store's privacy page and its assistant say so while it's on.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class ShopAds
    {
        public static readonly string[][] Nets = { new[] { "meta", "Meta" }, new[] { "tiktok", "TikTok" } };
        static Dictionary<string, object> D(params object[] kv) { return ShopApi.D(kv); }
        static void Exec(SqlConnection c, string sql, params object[] a) { ShopApi.Exec(c, null, sql, a); }
        static string Cut(string s, int n) { return ShopApi.Cut(s, n); }
        static string MetaBase { get { return (Web.Setting("MetaGraphBase") ?? "https://graph.facebook.com").TrimEnd('/') + "/" + (Web.Setting("MetaCapiVersion") ?? "v27.0"); } }
        static string TikTokBase { get { return (Web.Setting("TikTokEventsBase") ?? "https://business-api.tiktok.com/open_api/v1.3").TrimEnd('/'); } }

        public const string Schema = @"
IF OBJECT_ID('dbo.ShopAdLinks') IS NULL
  CREATE TABLE dbo.ShopAdLinks(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Net NVARCHAR(8) NOT NULL, TokenBox NVARCHAR(MAX) NOT NULL, Last4 NVARCHAR(8) NOT NULL, TestCode NVARCHAR(40) NULL,
    Active BIT NOT NULL DEFAULT 0, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), LastOkAt DATETIME2 NULL, LastError NVARCHAR(300) NULL, LastErrorAt DATETIME2 NULL, PRIMARY KEY(StoreId, Net));
-- State: wait / sending / ok / failed / off (the owner turned it off before it went)
IF OBJECT_ID('dbo.ShopAdEvents') IS NULL
BEGIN
  CREATE TABLE dbo.ShopAdEvents(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Net NVARCHAR(8) NOT NULL, Name NVARCHAR(20) NOT NULL, EventId NVARCHAR(64) NOT NULL, Body NVARCHAR(MAX) NOT NULL,
    State NVARCHAR(8) NOT NULL DEFAULT 'wait', Tries INT NOT NULL DEFAULT 0, NextAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ClaimedAt DATETIME2 NULL, OrderId BIGINT NULL, LastError NVARCHAR(300) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), SentAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopAdEvents_Id ON dbo.ShopAdEvents(StoreId, Net, Name, EventId);
  CREATE INDEX IX_ShopAdEvents_Due ON dbo.ShopAdEvents(State, NextAt);
  CREATE INDEX IX_ShopAdEvents_Store ON dbo.ShopAdEvents(StoreId, Id);
END
";

        // ---------- the links ----------
        class Link { public int StoreId; public string Net, Box, Last4, Test, Pixel, Err; public bool Active; public DateTime? OkAt, ErrAt; public string Token { get { return Secrets.Unprotect(Box); } } }
        static string Pixel(Shop sh, string net) { return ShopJson.Str(ShopJson.At(ShopJson.Sub(sh.Settings, "pixel"), net == "meta" ? "meta" : "tiktok"), 40); }
        static List<Link> Links(SqlConnection c, Shop sh)
        {
            var l = new List<Link>();
            using (var cmd = Db.Cmd(c, "SELECT Net, TokenBox, Last4, TestCode, Active, LastOkAt, LastError, LastErrorAt FROM dbo.ShopAdLinks WHERE StoreId=@s", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Link { StoreId = sh.StoreId, Net = (string)r[0], Box = (string)r[1], Last4 = (string)r[2], Test = r[3] as string ?? "", Active = (bool)r[4], OkAt = ShopApi.Dt(r[5]), Err = r[6] as string ?? "", ErrAt = ShopApi.Dt(r[7]), Pixel = Pixel(sh, (string)r[0]) });
            return l;
        }
        // the ones that send now: switched on, with the pixel's id (half a minute in memory: every page asks)
        static readonly ConcurrentDictionary<int, Tuple<DateTime, string, List<Link>>> live = new ConcurrentDictionary<int, Tuple<DateTime, string, List<Link>>>();
        static void Forget(int storeId) { Tuple<DateTime, string, List<Link>> x; live.TryRemove(storeId, out x); }
        static List<Link> Live(SqlConnection c, Shop sh)
        {
            if (sh == null || !Shops.Ready) return new List<Link>();
            Tuple<DateTime, string, List<Link>> x; string px = Pixel(sh, "meta") + "|" + Pixel(sh, "tiktok");
            if (live.TryGetValue(sh.StoreId, out x) && x.Item2 == px && DateTime.UtcNow - x.Item1 < TimeSpan.FromSeconds(30)) return x.Item3;
            var l = Links(c, sh).Where(k => k.Active && k.Pixel.Length > 0).ToList(); live[sh.StoreId] = Tuple.Create(DateTime.UtcNow, px, l); return l;
        }
        public static bool On(SqlConnection c, Shop sh) { try { return Live(c, sh).Count > 0; } catch (Exception ex) { Log.Error(ex); return false; } }

        // ---------- the person, as the networks want it ----------
        class Who { public string Phone, First, Last, City, Gov, Ip, Ua, Fbp, Fbc, Ttp, Ttc, Url; }
        static string H(string s) { using (var h = SHA256.Create()) return ShopApi.Hex(h.ComputeHash(Encoding.UTF8.GetBytes(s))); }
        // lowercase, letters only (Meta: «no punctuation»; Arabic letters stay, as UTF-8)
        static string Letters(string s, bool spaces = false) { var t = new string((s ?? "").ToLowerInvariant().Where(ch => char.IsLetter(ch) || (spaces && ch == ' ')).ToArray()); return t.Trim(); }
        static Who From(ShopFrontHandler.Ctx x, Dictionary<string, object> b)
        {
            var req = HttpContext.Current == null ? null : HttpContext.Current.Request; Func<string, string, int, string> ck = (k, rx, n) => { var v = ShopJson.Str(ShopJson.At(b, k), n); return Regex.IsMatch(v, rx) ? v : null; };
            // the page we tell them about: the store's own address and a plain path (never a token: /thanks/<token>, /pay/<token>)
            string path = ShopJson.Str(ShopJson.At(b, "u"), 200); if (!Regex.IsMatch(path, @"^/[A-Za-z0-9/_\-]{0,199}$")) path = "/";
            var seg = path.Trim('/').Split('/'); if (seg.Length > 0 && (seg[0] == "thanks" || seg[0] == "pay" || seg[0] == "rate" || seg[0] == "account" || seg[0] == "cart")) path = "/" + seg[0];
            return new Who { Ip = x.Ip, Ua = req == null ? null : Cut(req.UserAgent, 500), Url = x.Row.Url + path,
                Fbp = ck("fbp", @"^fb\.\d\.\d{10,14}\.\d{1,25}$", 80), Fbc = ck("fbc", @"^fb\.\d\.\d{10,14}\.[A-Za-z0-9_\-]{8,500}$", 600), Ttp = ck("ttp", @"^[A-Za-z0-9_\-.]{8,120}$", 120), Ttc = ck("ttc", @"^[A-Za-z0-9_\-.]{8,300}$", 300) };
        }
        static Dictionary<string, object> MetaUser(Who u)
        {
            var d = D("country", new[] { H("eg") });
            if (u.Phone != null) d["ph"] = new[] { H(u.Phone) }; if (!string.IsNullOrEmpty(u.First)) d["fn"] = new[] { H(u.First) }; if (!string.IsNullOrEmpty(u.Last)) d["ln"] = new[] { H(u.Last) };
            if (!string.IsNullOrEmpty(u.City)) d["ct"] = new[] { H(u.City) }; if (!string.IsNullOrEmpty(u.Gov)) d["st"] = new[] { H(u.Gov) };
            if (!string.IsNullOrEmpty(u.Ip) && u.Ip != "unknown") d["client_ip_address"] = u.Ip; if (!string.IsNullOrEmpty(u.Ua)) d["client_user_agent"] = u.Ua;
            if (u.Fbp != null) d["fbp"] = u.Fbp; if (u.Fbc != null) d["fbc"] = u.Fbc;
            return d;
        }
        static Dictionary<string, object> TikTokUser(Who u)
        {
            var d = D(); if (u.Phone != null) d["phone"] = H("+" + u.Phone);   // E.164, then SHA-256
            if (!string.IsNullOrEmpty(u.Ip) && u.Ip != "unknown") d["ip"] = u.Ip; if (!string.IsNullOrEmpty(u.Ua)) d["user_agent"] = u.Ua; if (u.Ttp != null) d["ttp"] = u.Ttp; if (u.Ttc != null) d["ttclid"] = u.Ttc;
            return d;
        }
        class Item { public int Id, Qty; public decimal Price; }
        static string Body(string net, string name, string eid, Who u, decimal? value, List<Item> items, int? orderNo)
        {
            long t = ShopView.Unix(DateTime.UtcNow);
            if (net == "meta")
            {
                var cd = D("currency", "EGP"); if (value.HasValue) cd["value"] = value.Value;
                if (items.Count > 0) { cd["content_type"] = "product"; cd["content_ids"] = items.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray(); cd["contents"] = items.Select(i => D("id", i.Id.ToString(CultureInfo.InvariantCulture), "quantity", i.Qty, "item_price", i.Price)).ToArray(); cd["num_items"] = items.Sum(i => i.Qty); }
                if (orderNo.HasValue) cd["order_id"] = orderNo.Value.ToString(CultureInfo.InvariantCulture);
                return ShopJson.Write(D("event_name", name, "event_time", t, "event_id", eid, "action_source", "website", "event_source_url", u.Url, "user_data", MetaUser(u), "custom_data", cd));
            }
            var pr = D("currency", "EGP"); if (value.HasValue) pr["value"] = value.Value;
            if (items.Count > 0) { pr["content_type"] = "product"; pr["contents"] = items.Select(i => D("content_id", i.Id.ToString(CultureInfo.InvariantCulture), "quantity", i.Qty, "price", i.Price)).ToArray(); }
            if (orderNo.HasValue) pr["order_id"] = orderNo.Value.ToString(CultureInfo.InvariantCulture);
            return ShopJson.Write(D("event", name, "event_time", t, "event_id", eid, "user", TikTokUser(u), "properties", pr, "page", D("url", u.Url)));
        }
        // the browser pixel's names for the same thing
        static string NameFor(string net, string ev) { return net == "tiktok" && ev == "Purchase" ? "CompletePayment" : ev; }
        static string EventId(Dictionary<string, object> b, string k) { var v = ShopJson.Str(ShopJson.At(b, k), 64); return Regex.IsMatch(v, "^[A-Za-z0-9-]{8,64}$") ? v : null; }
        static int Put(SqlConnection c, int storeId, string net, string name, string eid, string body, long? orderId)
        {
            try { Exec(c, "INSERT INTO dbo.ShopAdEvents(StoreId, Net, Name, EventId, Body, OrderId) VALUES(@s, @n, @nm, @e, @b, @o)", "@s", storeId, "@n", net, "@nm", name, "@e", eid, "@b", body, "@o", orderId.HasValue ? (object)orderId.Value : null); return 1; }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return 0; }   // the same event twice (a second press, the page sent it again)
        }

        // ---------- what happens on the store ----------
        // a web order was placed (ShopFront.cs): the Purchase, with the page's own event id for this press of the button
        public static void Purchase(SqlConnection c, ShopFrontHandler.Ctx x, ShopOrder o, Dictionary<string, object> b)
        {
            try
            {
                if (x.Preview || o == null || o.Test) return; var links = Live(c, x.Row); if (links.Count == 0) return;
                var u = From(x, b); u.Phone = o.Phone; var parts = Letters(o.Name, true).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) u.First = parts[0]; if (parts.Length > 1) u.Last = parts[parts.Length - 1];
                u.City = Letters(o.City); u.Gov = Letters(ShopGeo.Name(o.Gov, true)); u.Url = x.Row.Url + "/checkout";
                string eid = EventId(b, "eid") ?? "sbo-" + o.Id; var items = o.Items.Select(i => new Item { Id = i.ProductId, Qty = i.Qty, Price = i.Price }).ToList(); int n = 0;
                foreach (var k in links) n += Put(c, x.Row.StoreId, k.Net, NameFor(k.Net, "Purchase"), eid, Body(k.Net, NameFor(k.Net, "Purchase"), eid, u, o.Total, items, o.No), o.Id);
                if (n > 0) Soon(x.Row.StoreId);
            }
            catch (Exception ex) { Log.Error(ex); }   // never the customer's order
        }

        // /_a/ad: the page saw something (it answers nothing: the page doesn't wait for it)
        static readonly string[] Browser = { "ViewContent", "AddToCart", "InitiateCheckout" };
        public static void Event(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            res.StatusCode = 204;
            if (x.Preview) return; var links = Live(c, x.Row); if (links.Count == 0) return;
            string ev = ShopJson.Str(ShopJson.At(b, "e"), 20), eid = EventId(b, "id"); if (!Browser.Contains(ev) || eid == null) return;
            if (!Web.Allow("shop-ad:" + x.Ip, 60, TimeSpan.FromMinutes(1)) || !Web.Allow("shop-ads:" + x.Row.StoreId, 20000, TimeSpan.FromHours(1))) return;
            var d = x.Data; var now = DateTime.UtcNow; var items = new List<Item>(); decimal? old; ShopOffer of;
            if (ev == "InitiateCheckout")
            {
                foreach (var l in ShopJson.List(ShopJson.At(b, "l")).Take(Shops.MaxLines).Select(ShopJson.List).Where(l => l.Count > 1))
                { ShopVar v; if (!d.Vars.TryGetValue(ShopJson.Int(l[0]), out v)) continue; var p = d.ById[v.ProductId]; if (!p.Shown) continue; items.Add(new Item { Id = p.Id, Qty = Math.Max(1, Math.Min(999, ShopJson.Int(l[1]))), Price = ShopPrice.Unit(d, p, v, now, out old, out of) }); }
                if (items.Count == 0) return;
            }
            else
            {
                ShopProd p; if (!d.ById.TryGetValue(ShopJson.Int(ShopJson.At(b, "p")), out p) || !p.Shown) return;
                var v = p.Vars.FirstOrDefault(p.Can) ?? p.Vars.FirstOrDefault(); items.Add(new Item { Id = p.Id, Qty = 1, Price = v == null ? p.Price : ShopPrice.Unit(d, p, v, now, out old, out of) });
            }
            var u = From(x, b); decimal value = items.Sum(i => i.Price * i.Qty);
            foreach (var k in links) Put(c, x.Row.StoreId, k.Net, NameFor(k.Net, ev), eid, Body(k.Net, NameFor(k.Net, ev), eid, u, value, items, null), null);
        }

        // the privacy page says it while it's on (a copy: the page in memory is shared)
        public static ShopPage Page(SqlConnection c, Shop sh, ShopPage pg)
        {
            if (pg == null || pg.Kind != "privacy") return pg; var l = Live(c, sh); if (l.Count == 0) return pg;
            string names = string.Join(" و", l.Select(k => Nets.First(n => n[0] == k.Net)[1])), en = string.Join(" and ", l.Select(k => Nets.First(n => n[0] == k.Net)[1]));
            return new ShopPage { Id = pg.Id, Sort = pg.Sort, Slug = pg.Slug, Kind = pg.Kind, Title = pg.Title, TitleEn = pg.TitleEn, Hidden = pg.Hidden, InMenu = pg.InMenu, InFooter = pg.InFooter,
                Body = (pg.Body ?? "").TrimEnd() + "\n\nالمتجر بيقيس إعلاناته من السيرفر كمان: لما تزوره أو تطلب، بيبعت لـ " + names + " اللي حصل (منتج اتشاف، سلة، طلب وقيمته)، ومعاه رقمك واسمك ومدينتك متشفّرين، وعنوان الـ IP ونوع جهازك، عشان يعرف الإعلان جاب نتيجة.",
                BodyEn = string.IsNullOrWhiteSpace(pg.BodyEn) ? pg.BodyEn : pg.BodyEn.TrimEnd() + "\n\nThe store also measures its ads from its server: when you visit or order, it tells " + en + " what happened (a product viewed, a bag, an order and its value), with your phone, name and city hashed, and your IP address and device type, to see whether an ad worked." };
        }
        // one line in the assistant's store information
        public static string BotLine(SqlConnection c, Shop sh) { return On(c, sh) ? "قياس الإعلانات: المتجر بيبعت لـ " + string.Join(" و", Live(c, sh).Select(k => Nets.First(n => n[0] == k.Net)[1])) + " الزيارات والطلبات من السيرفر، ومعاها رقم الزبون واسمه متشفّرين (التفاصيل في صفحة الخصوصية).\n" : ""; }

        // ---------- sending ----------
        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) }; h.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0"); return h; }
        // after a failure: in a minute, 5, 30, 2 hours, 6, then it stops (Meta takes events up to 7 days old; a day is plenty). A setting for the tests
        static int[] Again { get { var s = Web.Setting("AdsRetrySec"); if (s != null) { var l = s.Split(',').Select(x => { int n; return int.TryParse(x.Trim(), out n) ? n : 0; }).Where(n => n > 0).ToArray(); if (l.Length > 0) return l; } return new[] { 60, 300, 1800, 7200, 21600 }; } }
        // one batch to one network: null = taken; bad = the token itself is refused
        static async Task<string> Post(Link k, List<string> bodies, string test, Action<bool> bad)
        {
            bad(false); string token = k.Token; if (token == null) { bad(true); return "التوكن ماتقراش. حطه تاني."; }
            HttpRequestMessage q; string data = "[" + string.Join(",", bodies) + "]";
            if (k.Net == "meta")
            {
                q = new HttpRequestMessage(HttpMethod.Post, MetaBase + "/" + Uri.EscapeDataString(k.Pixel) + "/events?access_token=" + Uri.EscapeDataString(token));
                q.Content = new StringContent("{\"data\":" + data + (string.IsNullOrEmpty(test) ? "" : ",\"test_event_code\":" + ShopJson.Write(test)) + "}", Encoding.UTF8, "application/json");
            }
            else
            {
                q = new HttpRequestMessage(HttpMethod.Post, TikTokBase + "/event/track/"); q.Headers.TryAddWithoutValidation("Access-Token", token);
                q.Content = new StringContent("{\"event_source\":\"web\",\"event_source_id\":" + ShopJson.Write(k.Pixel) + (string.IsNullOrEmpty(test) ? "" : ",\"test_event_code\":" + ShopJson.Write(test)) + ",\"data\":" + data + "}", Encoding.UTF8, "application/json");
            }
            string name = k.Net == "meta" ? "Meta" : "TikTok";
            try
            {
                using (var r = await Http.SendAsync(q))
                {
                    var text = await r.Content.ReadAsStringAsync(); Dictionary<string, object> j = null; try { j = Web.Js.Deserialize<Dictionary<string, object>>(text); } catch { }
                    if (k.Net == "meta")
                    {
                        if (r.IsSuccessStatusCode) return null;
                        var e = ShopJson.Sub(j, "error"); int code = ShopJson.Int(ShopJson.At(e, "code")); string msg = Cut(ShopJson.Str(ShopJson.At(e, "message"), 300), 200);
                        if (code == 190 || code == 102 || (int)r.StatusCode == 401) { bad(true); return "Meta مش قابلة التوكن: " + msg + ". اعمل توكن جديد من Events Manager وحطه."; }
                        if (code == 100 && msg.IndexOf("pixel", StringComparison.OrdinalIgnoreCase) >= 0) { bad(true); return "Meta مش لاقية الـ Pixel ده بالتوكن ده: " + msg; }
                        return "Meta ردّت بـ " + (int)r.StatusCode + (msg.Length > 0 ? ": " + msg : "");
                    }
                    int tc = j == null ? -1 : ShopJson.Int(ShopJson.At(j, "code"), -1); string tm = Cut(j == null ? text : ShopJson.Str(ShopJson.At(j, "message"), 300), 200);
                    if (r.IsSuccessStatusCode && tc == 0) return null;
                    if (tc == 40001 || tc == 40100 || tc == 40104 || tc == 40105 || (int)r.StatusCode == 401) { bad(true); return "TikTok مش قابلة التوكن: " + tm + ". اعمل توكن جديد من Events Manager وحطه."; }
                    return "TikTok ردّت" + (tc > 0 ? " بـ " + tc : " بـ " + (int)r.StatusCode) + (tm.Length > 0 ? ": " + tm : "");
                }
            }
            catch (TaskCanceledException) { return name + " مارّدتش في الوقت."; }
            catch (Exception ex) { var w = (ex as HttpRequestException)?.InnerException as WebException; return "مقدرناش نوصل لـ " + name + (w == null ? "." : " (" + w.Status + ")."); }
        }

        static readonly ConcurrentDictionary<int, int> busy = new ConcurrentDictionary<int, int>();
        static void Soon(int storeId)
        {
            if (!busy.TryAdd(storeId, 1)) return;
            HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { await SendDue(ct, storeId); } catch (Exception ex) { Log.Error(ex); } finally { int x; busy.TryRemove(storeId, out x); } });
        }
        public static async Task SendDue(CancellationToken ct, int storeId)
        {
            if (!Shops.Ready) return;
            for (int round = 0; round < 40 && !ct.IsCancellationRequested; round++)
            {
                var pairs = new List<Tuple<int, string>>();
                using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "SELECT DISTINCT TOP 30 StoreId, Net FROM dbo.ShopAdEvents WHERE ((State='wait' AND NextAt<=SYSUTCDATETIME()) OR (State='sending' AND ClaimedAt<DATEADD(MINUTE,-5,SYSUTCDATETIME()))) AND (@s=0 OR StoreId=@s)", "@s", storeId)) using (var r = cmd.ExecuteReader()) while (r.Read()) pairs.Add(Tuple.Create((int)r[0], (string)r[1]));
                if (pairs.Count == 0) return;
                foreach (var pr in pairs)
                {
                    var rows = new List<object[]>(); Link k;
                    using (var c = Db.Open())
                    {
                        using (var cmd = Db.Cmd(c, @"UPDATE TOP (500) dbo.ShopAdEvents SET State='sending', ClaimedAt=SYSUTCDATETIME(), Tries=Tries+1 OUTPUT INSERTED.Id, INSERTED.Body, INSERTED.Tries, INSERTED.CreatedAt
                            WHERE StoreId=@s AND Net=@n AND ((State='wait' AND NextAt<=SYSUTCDATETIME()) OR (State='sending' AND ClaimedAt<DATEADD(MINUTE,-5,SYSUTCDATETIME())))", "@s", pr.Item1, "@n", pr.Item2))
                        using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(new object[] { (long)r[0], (string)r[1], (int)r[2], ShopApi.Dt(r[3]).Value });
                        if (rows.Count == 0) continue;
                        var sh = Shops.ByStore(c, pr.Item1); k = sh == null ? null : Links(c, sh).FirstOrDefault(x => x.Net == pr.Item2);
                        string ids = string.Join(",", rows.Select(x => (long)x[0]));
                        if (k == null || !k.Active || k.Pixel.Length == 0) { Exec(c, "UPDATE dbo.ShopAdEvents SET State='off', LastError=N'القياس من السيرفر اتقفل قبل ما يتبعت' WHERE Id IN (" + ids + ")"); continue; }
                    }
                    bool bad = false; var why = await Post(k, rows.Select(x => (string)x[1]).ToList(), null, b => bad = b);
                    using (var c = Db.Open())
                    {
                        string ids = string.Join(",", rows.Select(x => (long)x[0]));
                        if (why == null) { Exec(c, "UPDATE dbo.ShopAdEvents SET State='ok', SentAt=SYSUTCDATETIME(), LastError=NULL WHERE Id IN (" + ids + ")"); Exec(c, "UPDATE dbo.ShopAdLinks SET LastOkAt=SYSUTCDATETIME(), LastError=NULL WHERE StoreId=@s AND Net=@n", "@s", k.StoreId, "@n", k.Net); continue; }
                        Exec(c, "UPDATE dbo.ShopAdLinks SET LastError=@e, LastErrorAt=SYSUTCDATETIME() WHERE StoreId=@s AND Net=@n", "@e", Cut(why, 300), "@s", k.StoreId, "@n", k.Net);
                        foreach (var g in rows.GroupBy(x => (int)x[2] > Again.Length || (DateTime)x[3] < DateTime.UtcNow.AddHours(-24) ? "failed" : "wait"))
                            Exec(c, "UPDATE dbo.ShopAdEvents SET State=@st, NextAt=@n, LastError=@e WHERE Id IN (" + string.Join(",", g.Select(x => (long)x[0])) + ")", "@st", g.Key, "@n", DateTime.UtcNow.AddSeconds(Again[Math.Min(Again.Length, Math.Max(1, (int)g.First()[2])) - 1] * (bad ? 4 : 1)), "@e", Cut(why, 300));
                    }
                }
            }
        }

        // ================= the owner's card in «المميزات» (api/shop.ashx a=ads, ads-...) =================
        static Dictionary<string, object> Card(SqlConnection c, Shop sh)
        {
            var links = Links(c, sh); var l = new List<object>();
            foreach (var n in Nets) { var k = links.FirstOrDefault(x => x.Net == n[0]); l.Add(D("net", n[0], "name", n[1], "pixel", Pixel(sh, n[0]), "linked", k != null, "on", k != null && k.Active, "last4", k == null ? "" : k.Last4, "test", k == null ? "" : k.Test, "ok", k == null ? null : ShopApi.Iso(k.OkAt), "err", k == null ? "" : k.Err, "errAt", k == null ? null : ShopApi.Iso(k.ErrAt))); }
            var sends = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT TOP 25 Net, Name, State, Tries, LastError, CreatedAt, SentAt, OrderId FROM dbo.ShopAdEvents WHERE StoreId=@s ORDER BY Id DESC", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) sends.Add(D("net", (string)r[0], "name", (string)r[1], "state", (string)r[2], "tries", (int)r[3], "err", r[4] as string ?? "", "at", ShopApi.Iso(ShopApi.Dt(r[5])), "sent", ShopApi.Iso(ShopApi.Dt(r[6])), "order", r[7] is long ? (object)(long)r[7] : null));
            var day = new Dictionary<string, object>(); using (var cmd = Db.Cmd(c, "SELECT State, COUNT(*) FROM dbo.ShopAdEvents WHERE StoreId=@s AND CreatedAt>DATEADD(HOUR,-24,SYSUTCDATETIME()) AND Name<>'test' GROUP BY State", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) day[(string)r[0]] = (int)r[1];
            return D("nets", l, "sends", sends, "day", day);
        }
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (a != "ads") return false; Web.Json(res, 200, Card(c, sh)); return true;
        }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null; string net = ShopJson.Str(ShopJson.At(b, "net"), 8);
            if (!a.StartsWith("ads-", StringComparison.Ordinal)) return false;
            if (!Nets.Any(n => n[0] == net)) throw new ShopException("net", "اختار Meta أو TikTok.");
            string name = Nets.First(n => n[0] == net)[1];
            switch (a)
            {
                case "ads-save":
                    {
                        string token = Regex.Replace(ShopJson.Str(ShopJson.At(b, "token"), 800), @"\s", ""), test = Regex.Replace(ShopJson.Str(ShopJson.At(b, "test"), 40), @"[^A-Za-z0-9_\-]", ""); bool on = ShopJson.Bool(ShopJson.At(b, "on"));
                        if (token.Length > 0 && (token.Length < 20 || !Regex.IsMatch(token, @"^[A-Za-z0-9_\-.|]+$"))) throw new ShopException("token", "التوكن ده مش كامل. انسخه تاني من Events Manager.");
                        bool had = ShopApi.Num(c, "SELECT COUNT(*) FROM dbo.ShopAdLinks WHERE StoreId=@s AND Net=@n", "@s", s.Id, "@n", net) > 0;
                        if (!had && token.Length == 0) throw new ShopException("token", "حط التوكن الأول.");
                        if (on && Pixel(sh, net).Length == 0) throw new ShopException("pixel", "اكتب رقم " + name + " Pixel في «قياس الإعلانات» فوق واحفظ الأول.");
                        if (token.Length > 0) Exec(c, "UPDATE dbo.ShopAdLinks SET TokenBox=@b, Last4=@l, TestCode=@t, Active=@a, LastError=NULL, LastErrorAt=NULL WHERE StoreId=@s AND Net=@n; IF @@ROWCOUNT=0 INSERT INTO dbo.ShopAdLinks(StoreId, Net, TokenBox, Last4, TestCode, Active) VALUES(@s, @n, @b, @l, @t, @a)", "@b", Secrets.Protect(token), "@l", token.Substring(token.Length - 4), "@t", ShopApi.Nz(test), "@a", on, "@s", s.Id, "@n", net);
                        else Exec(c, "UPDATE dbo.ShopAdLinks SET TestCode=@t, Active=@a WHERE StoreId=@s AND Net=@n", "@t", ShopApi.Nz(test), "@a", on, "@s", s.Id, "@n", net);
                        if (!on) Exec(c, "UPDATE dbo.ShopAdEvents SET State='off', LastError=N'القياس من السيرفر اتقفل قبل ما يتبعت' WHERE StoreId=@s AND Net=@n AND State='wait'", "@s", s.Id, "@n", net);
                        Forget(s.Id); Web.Json(res, 200, Card(c, sh)); return true;
                    }
                case "ads-del":
                    Exec(c, "UPDATE dbo.ShopAdEvents SET State='off', LastError=N'القياس من السيرفر اتقفل قبل ما يتبعت' WHERE StoreId=@s AND Net=@n AND State='wait'; DELETE FROM dbo.ShopAdLinks WHERE StoreId=@s AND Net=@n", "@s", s.Id, "@n", net);
                    Forget(s.Id); Web.Json(res, 200, Card(c, sh)); return true;
                case "ads-test":
                    {
                        var k = Links(c, sh).FirstOrDefault(x => x.Net == net); if (k == null) throw new ShopException("token", "حط التوكن واحفظ الأول.");
                        if (k.Pixel.Length == 0) throw new ShopException("pixel", "اكتب رقم " + name + " Pixel في «قياس الإعلانات» واحفظ الأول.");
                        if (k.Test.Length == 0) throw new ShopException("test", "اكتب «Test event code» من Events Manager (تبويب Test events) واحفظ، عشان التجربة ماتتحسبش زيارة حقيقية.");
                        if (!Web.Allow("ads-test:" + s.Id, 20, TimeSpan.FromHours(1))) throw new ShopException("too_many", "جرّبت كتير. استنى شوية.");
                        string eid = "sbtest-" + Shops.Token(8), ev = net == "meta" ? "PageView" : "ViewContent";
                        var body = Body(net, ev, eid, new Who { Ip = Web.Ip(HttpContext.Current.Request), Ua = Cut(HttpContext.Current.Request.UserAgent, 500), Url = sh.Url + "/" }, null, new List<Item>(), null); int sid = s.Id;
                        later = async () =>
                        {
                            bool bad = false; var why = await Post(k, new List<string> { body }, k.Test, x => bad = x);
                            using (var c2 = Db.Open())
                            {
                                Exec(c2, "INSERT INTO dbo.ShopAdEvents(StoreId, Net, Name, EventId, Body, State, Tries, SentAt, LastError) VALUES(@s, @n, 'test', @e, @b, @st, 1, SYSUTCDATETIME(), @er)", "@s", sid, "@n", net, "@e", eid, "@b", body, "@st", why == null ? "ok" : "failed", "@er", Cut(why, 300));
                                if (why == null) Exec(c2, "UPDATE dbo.ShopAdLinks SET LastOkAt=SYSUTCDATETIME(), LastError=NULL WHERE StoreId=@s AND Net=@n", "@s", sid, "@n", net); else Exec(c2, "UPDATE dbo.ShopAdLinks SET LastError=@e, LastErrorAt=SYSUTCDATETIME() WHERE StoreId=@s AND Net=@n", "@e", Cut(why, 300), "@s", sid, "@n", net);
                                var d = Card(c2, Shops.ByStore(c2, sid)); d["sent"] = why == null; d["why"] = why ?? ""; Web.Json(res, 200, d);
                            }
                        };
                        return true;
                    }
            }
            return false;
        }
    }
}
