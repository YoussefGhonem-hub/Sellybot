// The other half of «ربط ببرامج تانية»: webhooks (we POST to the merchant's program when something happens), what changed in the products
// (the watcher behind updated_since, «منتج اتغيّر» and «قرب يخلص»), and the owner's tab in «متجري» (shop-admin/api.js).
// An event is a row per address before anything is sent (ShopHookSends, unique per event), then sent at once and again with backoff for
// about a day by the background lane. Each POST is signed: HMAC-SHA256 of "<timestamp>.<body>" with the address's own secret.
// The address must be https and every IP its name has must be public: checked when it's saved and again before every send.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class ShopApiHooks
    {
        public const int MaxHooks = 5;
        public static readonly string[][] Events = {
            new[] { "order.created", "أوردر جديد", "A new order" }, new[] { "order.status_changed", "حالة أوردر اتغيّرت", "An order's status changed" },
            new[] { "stock.low", "مقاس قرب يخلص", "A size is running low" }, new[] { "product.changed", "منتج اتغيّر (السعر، الكمية، الظهور، البيانات، أو اتمسح)", "A product changed (price, stock, shown, details, or deleted)" } };
        static string Iso(DateTime? t) { return ShopApi.Iso(t); }
        static Dictionary<string, object> D(params object[] kv) { return ShopApi.D(kv); }
        static void Exec(SqlConnection c, string sql, params object[] a) { ShopApi.Exec(c, null, sql, a); }

        // ---------- the addresses ----------
        class Hook { public int Id, StoreId; public string Url, Events, Box, Last4; public bool Active; public bool Has(string k) { return (Events ?? "").Split(',').Contains(k); } }
        static readonly ConcurrentDictionary<int, Tuple<DateTime, List<Hook>>> live = new ConcurrentDictionary<int, Tuple<DateTime, List<Hook>>>();
        public static void Forget(int storeId) { Tuple<DateTime, List<Hook>> x; live.TryRemove(storeId, out x); }
        static List<Hook> Read(SqlConnection c, string where, params object[] a)
        {
            var l = new List<Hook>();
            using (var cmd = Db.Cmd(c, "SELECT Id, StoreId, Url, Events, SecretBox, Last4, Active FROM dbo.ShopWebhooks WHERE " + where + " ORDER BY Id", a)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Hook { Id = (int)r[0], StoreId = (int)r[1], Url = (string)r[2], Events = (string)r[3], Box = (string)r[4], Last4 = (string)r[5], Active = (bool)r[6] });
            return l;
        }
        // the addresses that get events now: switched on, and the feature on (half a minute in memory)
        static List<Hook> Active(SqlConnection c, int storeId)
        {
            Tuple<DateTime, List<Hook>> x;
            if (live.TryGetValue(storeId, out x) && DateTime.UtcNow - x.Item1 < TimeSpan.FromSeconds(30)) return x.Item2;
            var l = Shops.Ready && ShopApi.IsOn(c, storeId) ? Read(c, "StoreId=@s AND Active=1", "@s", storeId) : new List<Hook>();
            live[storeId] = Tuple.Create(DateTime.UtcNow, l); return l;
        }
        public static bool Wants(SqlConnection c, int storeId, string kind) { return Active(c, storeId).Any(h => h.Has(kind)); }

        // ---------- the address check (save and send) ----------
        static bool TestHost(Uri u) { return (Web.Setting("ApiHookTestHosts") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Contains(u.Authority, StringComparer.OrdinalIgnoreCase); }
        // null = it may be called; otherwise why not, for the owner
        public static async Task<string> BadUrl(string url)
        {
            Uri u; if (string.IsNullOrWhiteSpace(url) || url.Length > 500 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return "العنوان مش مظبوط. اكتبه كامل زي https://example.com/webhook";
            bool test = TestHost(u);   // the local tests' fake receiver only (a setting no live site has)
            if (u.Scheme != "https" && !test) return "العنوان لازم يبدأ بـ https://";
            if (u.UserInfo.Length > 0) return "العنوان مايكونش فيه اسم مستخدم أو باسورد.";
            if (test) return null;
            IPAddress[] all; IPAddress one;
            if (IPAddress.TryParse(u.DnsSafeHost, out one)) all = new[] { one };
            else { try { all = await Dns.GetHostAddressesAsync(u.DnsSafeHost); } catch (Exception) { return "مش لاقيين العنوان ده على النت. اتأكد منه."; } }
            if (all.Length == 0) return "مش لاقيين العنوان ده على النت. اتأكد منه.";
            if (all.Any(a => !Public(a))) return "العنوان ده بيشاور على شبكة داخلية أو على السيرفر نفسه، ومش مسموح.";
            return null;
        }
        public static bool Public(IPAddress a)
        {
            if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None)) return false;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv4MappedToIPv6) return Public(a.MapToIPv4());
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
                var b6 = a.GetAddressBytes();
                if ((b6[0] & 0xfe) == 0xfc) return false;                                                     // fc00::/7 unique local
                if (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x0d && b6[3] == 0xb8) return false;           // 2001:db8::/32 examples
                if (b6[0] == 0 && b6[1] == 0x64 && b6[2] == 0xff && b6[3] == 0x9b) return Public(new IPAddress(b6.Skip(12).ToArray()));   // 64:ff9b::/96 carries an IPv4 one
                if (b6.Take(10).All(x => x == 0) && b6[10] == 0 && b6[11] == 0) return false;                  // ::a.b.c.d (old IPv4-compatible)
                return true;
            }
            var b = a.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224) return false;
            if (b[0] == 169 && b[1] == 254) return false;                       // link-local (the cloud's metadata address is here)
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;         // carrier-grade NAT
            if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return false;
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;
            if ((b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113)) return false;
            return true;
        }

        // ---------- events ----------
        public static int Emit(SqlConnection c, int storeId, string kind, string dedup, Func<object> data)
        {
            var hooks = Active(c, storeId).Where(h => h.Has(kind)).ToList(); if (hooks.Count == 0) return 0;
            var sh = Shops.ByStore(c, storeId); if (sh == null) return 0;
            string id = "evt_" + Shops.Token(16), body = ShopJson.Write(D("id", id, "type", kind, "created_at", Iso(DateTime.UtcNow), "store", D("slug", sh.Slug, "name", sh.Name), "data", data()));
            int n = 0;
            foreach (var h in hooks)
                try { Exec(c, "INSERT INTO dbo.ShopHookSends(StoreId, HookId, EventId, Kind, Dedup, Body) VALUES(@s, @h, @e, @k, @d, @b)", "@s", storeId, "@h", h.Id, "@e", id, "@k", kind, "@d", ShopApi.Cut(dedup, 80), "@b", body); n++; }
                catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }   // the same event, noticed twice
            if (n > 0) Soon(storeId);
            return n;
        }
        // an order was placed (from = null) or moved: called once per change, right after it's saved
        public static void OnOrder(int storeId, long orderId, string from, string to)
        {
            string mark = from == null ? "o" + orderId : "o" + orderId + "-" + to + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            HostingEnvironment.QueueBackgroundWorkItem(ct =>
            {
                try
                {
                    using (var c = Db.Open())
                    {
                        if (Active(c, storeId).Count == 0) return;
                        var o = ShopOrders.ById(c, storeId, orderId); if (o == null) return;
                        if (from == null) Emit(c, storeId, "order.created", mark, () => D("order", ShopApi.OrderJson(o)));
                        else Emit(c, storeId, "order.status_changed", mark, () => D("from", from, "to", to, "order", ShopApi.OrderJson(o)));
                        // the stock moved with it
                        if (Wants(c, storeId, "stock.low") || Wants(c, storeId, "product.changed")) Watch(c, Shops.ByStore(c, storeId));
                    }
                }
                catch (Exception ex) { Log.Error(ex); }
            });
        }

        // ---------- what changed in the products ----------
        // Every product's fingerprint (its words, prices, sizes and their stock) against the last one seen: a new one is a change. The
        // catalog in memory is read only when the store's version moved. Two at once: the conditional UPDATE lets one of them tell.
        static readonly ConcurrentDictionary<int, string> marks = new ConcurrentDictionary<int, string>();
        static readonly ConcurrentDictionary<int, object> locks = new ConcurrentDictionary<int, object>();
        static string Sig(ShopProd p)
        {
            var s = new StringBuilder().Append(p.Name).Append('|').Append(p.NameEn).Append('|').Append(p.Slug).Append('|').Append(p.Sku).Append('|').Append(p.Status).Append('|').Append(p.Price).Append('|').Append(p.OldPrice).Append('|').Append(p.Cost).Append('|')
                .Append(p.Track).Append('|').Append(p.OutMode).Append('|').Append(p.Unit).Append('|').Append(p.Brand).Append('|').Append(Fnv(p.Descr)).Append('|').Append(Fnv(p.DescrEn)).Append('|')
                .Append(string.Join(",", p.Pics.Select(x => x.Path))).Append('|').Append(string.Join(",", p.Cats)).Append('|').Append(p.MainCat);
            foreach (var v in p.Vars) s.Append('#').Append(v.Id).Append(':').Append(v.Opts).Append(':').Append(v.Sku).Append(':').Append(v.Price).Append(':').Append(v.OldPrice).Append(':').Append(v.Cost).Append(':').Append(v.Stock).Append(':').Append(v.Active);
            using (var h = SHA256.Create()) return ShopApi.Hex(h.ComputeHash(Encoding.UTF8.GetBytes(s.ToString()))).Substring(0, 16);
        }
        static uint Fnv(string s) { uint h = 2166136261; foreach (var ch in s ?? "") { h ^= ch; h *= 16777619; } return h; }   // a stable hash of a long text (not string.GetHashCode)
        static int LowAt(Shop sh) { return Math.Max(0, Math.Min(100, ShopJson.Int(ShopJson.At(ShopJson.Sub(sh.Settings, "alerts"), "lowAt"), 3))); }
        static string Lows(ShopProd p, int at) { return p.Track && p.OutMode != "order" ? string.Join(",", p.Vars.Where(v => v.Active && v.Stock <= at).Select(v => v.Id).OrderBy(i => i)) : ""; }
        public static void Watch(SqlConnection c, Shop sh, ShopData d = null)
        {
            if (sh == null) return; d = d ?? ShopCache.Get(c, sh); if (d == null) return;
            int at = LowAt(sh); string mark = d.Ver + "-" + d.StockVer + "-" + at, had;
            if (marks.TryGetValue(sh.StoreId, out had) && had == mark) return;
            lock (locks.GetOrAdd(sh.StoreId, k => new object()))
            {
                if (marks.TryGetValue(sh.StoreId, out had) && had == mark) return;
                var rows = new Dictionary<int, string[]>();
                using (var cmd = Db.Cmd(c, "SELECT ProductId, Sig, Lows, Gone FROM dbo.ShopApiSeen WHERE StoreId=@s", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) rows[(int)r[0]] = new[] { (string)r[1], (string)r[2], (bool)r[3] ? "1" : "0" };
                bool first = rows.Count == 0, wantP = Wants(c, sh.StoreId, "product.changed"), wantL = Wants(c, sh.StoreId, "stock.low");
                foreach (var p in d.Products)
                {
                    string sig = Sig(p), lows = Lows(p, at); string[] o; var pp = p;
                    if (!rows.TryGetValue(p.Id, out o))
                    {
                        try { Exec(c, "INSERT INTO dbo.ShopApiSeen(StoreId, ProductId, Sig, Lows, ChangedAt) VALUES(@s, @p, @g, @l, @t)", "@s", sh.StoreId, "@p", p.Id, "@g", sig, "@l", lows, "@t", first ? p.UpdatedAt : DateTime.UtcNow); }
                        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { continue; }
                        if (first) continue;
                        if (wantP) Emit(c, sh.StoreId, "product.changed", "p" + p.Id + "-" + sig, () => D("product", ShopApi.ProdJson(d, pp, DateTime.UtcNow), "created", true));
                        if (wantL) LowEvents(c, sh, d, p, "", lows, sig, at);
                        continue;
                    }
                    if (o[0] == sig && o[1] == lows && o[2] == "0") continue;
                    int n; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopApiSeen SET Sig=@g, Lows=@l, Gone=0, ChangedAt=CASE WHEN Sig<>@g OR Gone=1 THEN SYSUTCDATETIME() ELSE ChangedAt END WHERE StoreId=@s AND ProductId=@p AND Sig=@og AND Lows=@ol AND Gone=@go",
                        "@g", sig, "@l", lows, "@s", sh.StoreId, "@p", p.Id, "@og", o[0], "@ol", o[1], "@go", o[2] == "1")) n = cmd.ExecuteNonQuery();
                    if (n != 1) continue;
                    if (wantP && (o[0] != sig || o[2] == "1")) Emit(c, sh.StoreId, "product.changed", "p" + p.Id + "-" + sig, () => D("product", ShopApi.ProdJson(d, pp, DateTime.UtcNow)));
                    if (wantL) LowEvents(c, sh, d, p, o[1], lows, sig, at);
                }
                var ids = new HashSet<int>(d.Products.Select(p => p.Id));
                foreach (var kv in rows.Where(k => !ids.Contains(k.Key) && k.Value[2] == "0"))
                {
                    int n, pid = kv.Key; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopApiSeen SET Gone=1, ChangedAt=SYSUTCDATETIME() WHERE StoreId=@s AND ProductId=@p AND Gone=0", "@s", sh.StoreId, "@p", pid)) n = cmd.ExecuteNonQuery();
                    if (n == 1 && wantP) Emit(c, sh.StoreId, "product.changed", "p" + pid + "-gone", () => D("product", D("id", pid), "deleted", true));
                }
                marks[sh.StoreId] = mark;
            }
        }
        // a size that wasn't low and is now: once, until it goes up again
        static void LowEvents(SqlConnection c, Shop sh, ShopData d, ShopProd p, string before, string now, string sig, int at)
        {
            var was = new HashSet<string>(before.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (var vid in now.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !was.Contains(x)))
            {
                ShopVar v; if (!d.Vars.TryGetValue(int.Parse(vid), out v)) continue; var vv = v;
                Emit(c, sh.StoreId, "stock.low", "v" + vid + "-" + sig, () => D("product_id", p.Id, "variant_id", vv.Id, "sku", vv.Sku ?? p.Sku ?? "", "name", p.Name, "options", vv.Label, "stock", vv.Stock, "low_at", at, "url", d.Row.Url + "/p/" + p.Slug));
            }
        }

        // ---------- sending ----------
        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; int sec; if (!int.TryParse(Web.Setting("ApiHookTimeoutSec"), out sec) || sec < 1) sec = 10;
            var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(sec) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("SellyStore-Webhooks/1.0"); return h;
        }
        // after a failure: in a minute, 5, 15, an hour, 3, 6, 12 (about a day in all), then it stops (a setting for the tests)
        static int[] Retry { get { var s = Web.Setting("ApiHookRetrySec"); if (s != null) { var l = s.Split(',').Select(x => { int n; return int.TryParse(x.Trim(), out n) ? n : 0; }).Where(n => n > 0).ToArray(); if (l.Length > 0) return l; } return new[] { 60, 300, 900, 3600, 10800, 21600, 43200 }; } }
        public static string Sign(string secret, long ts, string body) { using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret))) return "sha256=" + ShopApi.Hex(h.ComputeHash(Encoding.UTF8.GetBytes(ts.ToString(CultureInfo.InvariantCulture) + "." + body))); }
        // one POST: null = the program took it (2xx), otherwise what went wrong; code = its HTTP answer (0 = none)
        static async Task<string> Post(string url, string secret, string kind, string eventId, int attempt, string body, Action<int> code)
        {
            code(0);
            var why = await BadUrl(url); if (why != null) return why;
            long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var q = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            q.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            q.Headers.TryAddWithoutValidation("X-SellyStore-Event", kind); q.Headers.TryAddWithoutValidation("X-SellyStore-Event-Id", eventId); q.Headers.TryAddWithoutValidation("X-SellyStore-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
            q.Headers.TryAddWithoutValidation("X-SellyStore-Signature", Sign(secret, ts, body)); q.Headers.TryAddWithoutValidation("X-SellyStore-Attempt", attempt.ToString(CultureInfo.InvariantCulture));
            try
            {
                using (var r = await Http.SendAsync(q, HttpCompletionOption.ResponseHeadersRead))
                {
                    code((int)r.StatusCode);
                    if ((int)r.StatusCode >= 200 && (int)r.StatusCode < 300) return null;
                    return (int)r.StatusCode >= 300 && (int)r.StatusCode < 400 ? "برنامجك ردّ بتحويل لعنوان تاني (" + (int)r.StatusCode + ")، واحنا مابنتحوّلش. حط العنوان النهائي." : "برنامجك ردّ بـ " + (int)r.StatusCode + " بدل 200.";
                }
            }
            catch (TaskCanceledException) { return "برنامجك مارّدش في " + Http.Timeout.TotalSeconds + " ثواني."; }
            catch (Exception ex) { var w = (ex as HttpRequestException)?.InnerException as WebException; return "مقدرناش نوصل لبرنامجك" + (w == null ? "." : " (" + w.Status + ")."); }
        }

        static readonly ConcurrentDictionary<int, int> busy = new ConcurrentDictionary<int, int>();
        static void Soon(int storeId)
        {
            if (!busy.TryAdd(storeId, 1)) return;
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try { await SendDue(ct, storeId); }
                catch (Exception ex) { Log.Error(ex); }
                finally { int x; busy.TryRemove(storeId, out x); }
            });
        }
        class Send { public long Id; public int StoreId, HookId, Tries; public string EventId, Kind, Body; public DateTime CreatedAt; }
        public static async Task SendDue(CancellationToken ct, int storeId)
        {
            var retry = Retry;
            for (int round = 0; round < 40 && !ct.IsCancellationRequested; round++)
            {
                var l = new List<Send>();
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, @"UPDATE TOP (10) dbo.ShopHookSends SET State='sending', ClaimedAt=SYSUTCDATETIME(), Tries=Tries+1
                    OUTPUT INSERTED.Id, INSERTED.StoreId, INSERTED.HookId, INSERTED.EventId, INSERTED.Kind, INSERTED.Body, INSERTED.Tries, INSERTED.CreatedAt
                    WHERE ((State='wait' AND NextAt<=SYSUTCDATETIME()) OR (State='sending' AND ClaimedAt<DATEADD(MINUTE,-5,SYSUTCDATETIME()))) AND (@s=0 OR StoreId=@s)", "@s", storeId))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) l.Add(new Send { Id = (long)r[0], StoreId = (int)r[1], HookId = (int)r[2], EventId = ((string)r[3]).Trim(), Kind = (string)r[4], Body = (string)r[5], Tries = (int)r[6], CreatedAt = ShopApi.Dt(r[7]).Value });
                if (l.Count == 0) return;
                foreach (var x in l)
                {
                    Hook h; string secret = null; bool on;
                    using (var c = Db.Open()) { h = Read(c, "Id=@i AND StoreId=@s", "@i", x.HookId, "@s", x.StoreId).FirstOrDefault(); on = h != null && h.Active && ShopApi.IsOn(c, x.StoreId); if (on) secret = Secrets.Unprotect(h.Box); }
                    if (!on || secret == null) { using (var c = Db.Open()) Exec(c, "UPDATE dbo.ShopHookSends SET State='failed', DoneAt=SYSUTCDATETIME(), LastError=@e WHERE Id=@i", "@e", "العنوان اتوقف قبل ما يتبعتله.", "@i", x.Id); continue; }
                    int code = 0; var why = await Post(h.Url, secret, x.Kind, x.EventId, x.Tries, x.Body, n => code = n);
                    using (var c = Db.Open())
                    {
                        if (why == null)
                        {
                            Exec(c, "UPDATE dbo.ShopHookSends SET State='ok', DoneAt=SYSUTCDATETIME(), LastCode=@c, LastError=NULL WHERE Id=@i", "@c", code, "@i", x.Id);
                            Exec(c, "UPDATE dbo.ShopWebhooks SET LastOkAt=SYSUTCDATETIME() WHERE Id=@h", "@h", h.Id);
                            continue;
                        }
                        bool last = x.Tries > retry.Length || x.CreatedAt < DateTime.UtcNow.AddHours(-24);
                        Exec(c, "UPDATE dbo.ShopHookSends SET State=@st, NextAt=@n, DoneAt=CASE WHEN @st='failed' THEN SYSUTCDATETIME() ELSE NULL END, LastCode=@c, LastError=@e WHERE Id=@i",
                            "@st", last ? "failed" : "wait", "@n", DateTime.UtcNow.AddSeconds(last ? 0 : retry[Math.Min(x.Tries, retry.Length) - 1]), "@c", code == 0 ? (object)null : code, "@e", ShopApi.Cut(why, 300), "@i", x.Id);
                        Exec(c, "UPDATE dbo.ShopWebhooks SET LastFailAt=SYSUTCDATETIME(), LastError=@e WHERE Id=@h", "@e", ShopApi.Cut(why, 300), "@h", h.Id);
                        // it stopped trying: the owner hears it on WhatsApp (once a day at most, by day: Alerts)
                        if (last) Exec(c, "UPDATE dbo.ShopWebhooks SET AlertDue=ISNULL(AlertDue, SYSUTCDATETIME()) WHERE Id=@h AND (AlertedAt IS NULL OR AlertedAt<DATEADD(HOUR,-24,SYSUTCDATETIME()))", "@h", h.Id);
                    }
                }
            }
        }

        static async Task Alerts(CancellationToken ct)
        {
            if (WaNotify.QuietUntil() != null) return;
            var due = new List<int[]>(); using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "SELECT TOP 20 Id, StoreId FROM dbo.ShopWebhooks WHERE AlertDue IS NOT NULL")) using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new[] { (int)r[0], (int)r[1] });
            foreach (var x in due)
            {
                using (var c = Db.Open())
                {
                    string url; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopWebhooks SET AlertDue=NULL, AlertedAt=SYSUTCDATETIME() OUTPUT INSERTED.Url WHERE Id=@i AND AlertDue IS NOT NULL", "@i", x[0])) url = cmd.ExecuteScalar() as string;
                    var s = url == null ? null : Db.StoreById(c, x[1]); if (s == null) continue;
                    Uri u; Uri.TryCreate(url, UriKind.Absolute, out u);
                    await WaNotify.ToOwner(c, s, "⚠️ *برنامجك مش بيستلم من متجرك*\nبعتنا لـ " + (u == null ? url : u.Host) + " يوم كامل ومارّدش صح، فوقفنا المحاولة للأحداث دي.\n\nافتح «متجري» ثم «ربط ببرامج تانية» وشوف «آخر الإرسال»، وتقدر تبعتها تاني من هناك.", "sb-api-hook-" + x[0] + "-" + DateTime.UtcNow.ToString("yyyyMMdd"));
                }
                if (ct.IsCancellationRequested) return;
            }
        }

        // ---------- the background lane (Jobs.cs) ----------
        public static async Task Run(CancellationToken ct)
        {
            if (!Shops.Ready) return;
            try { await SendDue(ct, 0); } catch (Exception ex) { Log.Error(ex); }
            try
            {
                using (var c = Db.Open())
                {
                    var ids = new List<int>(); using (var cmd = Db.Cmd(c, "SELECT DISTINCT h.StoreId FROM dbo.ShopWebhooks h JOIN dbo.ShopApiConf f ON f.StoreId=h.StoreId AND f.ApiOn=1 WHERE h.Active=1 AND (h.Events LIKE '%product.changed%' OR h.Events LIKE '%stock.low%')")) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((int)r[0]);
                    foreach (var id in ids) { try { Watch(c, Shops.ByStore(c, id)); } catch (Exception ex) { Log.Error(ex); } }
                }
                await SendDue(ct, 0);
            }
            catch (Exception ex) { Log.Error(ex); }
            try { await Alerts(ct); } catch (Exception ex) { Log.Error(ex); }
            try { await ShopAds.SendDue(ct, 0); } catch (Exception ex) { Log.Error(ex); }
            try
            {
                if (Web.Allow("api-tidy", 1, TimeSpan.FromHours(20)))
                    using (var c = Db.Open()) Exec(c, "DELETE FROM dbo.ShopHookSends WHERE State IN ('ok','failed') AND CreatedAt<DATEADD(DAY,-30,SYSUTCDATETIME()); DELETE FROM dbo.ShopApiLog WHERE At<DATEADD(DAY,-180,SYSUTCDATETIME()); DELETE FROM dbo.ShopApiSeen WHERE Gone=1 AND ChangedAt<DATEADD(DAY,-30,SYSUTCDATETIME()); DELETE FROM dbo.ShopAdEvents WHERE CreatedAt<DATEADD(DAY,-8,SYSUTCDATETIME())");
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // ================= the owner's tab (api/shop.ashx a=api..., ads...) =================
        static string Str(Dictionary<string, object> b, string k, int n) { return ShopJson.Str(ShopJson.At(b, k), n); }
        static int Int(Dictionary<string, object> b, string k) { return ShopJson.Int(ShopJson.At(b, k)); }
        static void Ok(HttpResponse res, params object[] kv) { var d = D(kv); d["ok"] = true; Web.Json(res, 200, d); }
        static string Base(HttpRequest req) { return ShopApi.Base(req); }
        static Dictionary<string, object> Tab(SqlConnection c, int storeId, HttpRequest req)
        {
            var hooks = Read(c, "StoreId=@s", "@s", storeId);
            var sends = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 40 Id, HookId, Kind, State, Tries, LastCode, LastError, CreatedAt, NextAt, DoneAt FROM dbo.ShopHookSends WHERE StoreId=@s ORDER BY Id DESC", "@s", storeId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) sends.Add(D("id", (long)r[0], "hook", (int)r[1], "kind", (string)r[2], "state", (string)r[3], "tries", (int)r[4], "code", r[5] is int ? (object)(int)r[5] : null, "err", r[6] as string ?? "", "at", Iso(ShopApi.Dt(r[7])), "next", (string)r[3] == "wait" ? Iso(ShopApi.Dt(r[8])) : null, "done", Iso(ShopApi.Dt(r[9]))));
            var log = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT TOP 40 KeyName, What, At FROM dbo.ShopApiLog WHERE StoreId=@s ORDER BY Id DESC", "@s", storeId)) using (var r = cmd.ExecuteReader()) while (r.Read()) log.Add(D("key", (string)r[0], "what", (string)r[1], "at", Iso(ShopApi.Dt(r[2]))));
            var hx = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT Id, LastOkAt, LastFailAt, LastError, CreatedAt FROM dbo.ShopWebhooks WHERE StoreId=@s", "@s", storeId)) using (var r = cmd.ExecuteReader()) while (r.Read()) hx.Add(new object[] { (int)r[0], Iso(ShopApi.Dt(r[1])), Iso(ShopApi.Dt(r[2])), r[3] as string ?? "", Iso(ShopApi.Dt(r[4])) });
            Func<int, object[]> extra = id => (object[])hx.FirstOrDefault(z => (int)((object[])z)[0] == id) ?? new object[5];
            return D("on", ShopApi.IsOn(c, storeId), "base", Base(req), "docs", Base(req) + "/docs", "keys", ShopApi.Keys(c, storeId), "max", D("keys", ShopApi.MaxKeys, "hooks", MaxHooks),
                "hooks", hooks.Select(h => { var e = extra(h.Id); return D("id", h.Id, "url", h.Url, "events", h.Events.Split(','), "active", h.Active, "last4", h.Last4, "ok", e[1], "fail", e[2], "err", e[3], "at", e[4]); }).ToArray(),
                "sends", sends, "log", log, "scopes", ShopApi.Scopes.Select(x => D("k", x[0], "t", x[1])).ToArray(), "events", Events.Select(x => D("k", x[0], "t", x[1])).ToArray());
        }

        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (a == "api") { Web.Json(res, 200, Tab(c, s.Id, req)); return true; }
            return ShopAds.Get(c, s, sh, a, req, res);
        }

        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null; int id = Int(b, "id"); var req = HttpContext.Current.Request;
            switch (a)
            {
                case "api-on": ShopApi.SetOn(c, s.Id, ShopJson.Bool(ShopJson.At(b, "on"))); Ok(res, "tab", Tab(c, s.Id, req)); return true;
                case "api-key":
                    {
                        if (!Web.Allow("api-newkey:" + s.Id, 20, TimeSpan.FromHours(1))) throw new ShopException("too_many", "عملت مفاتيح كتير. استنى شوية.");
                        int kid; var key = ShopApi.NewKey(c, s.Id, Str(b, "name", 60), ShopJson.List(ShopJson.At(b, "scopes")).Select(x => ShopJson.Str(x, 30)), out kid);
                        Ok(res, "key", key, "id", kid, "tab", Tab(c, s.Id, req)); return true;
                    }
                case "api-key-stop": Exec(c, "UPDATE dbo.ShopApiKeys SET StoppedAt=SYSUTCDATETIME() WHERE Id=@i AND StoreId=@s AND StoppedAt IS NULL", "@i", id, "@s", s.Id); Ok(res, "tab", Tab(c, s.Id, req)); return true;
                case "api-key-del": Exec(c, "DELETE FROM dbo.ShopApiKeys WHERE Id=@i AND StoreId=@s AND StoppedAt IS NOT NULL", "@i", id, "@s", s.Id); Ok(res, "tab", Tab(c, s.Id, req)); return true;
                case "api-hook": later = SaveHook(c, s, b, res, req); return true;
                case "api-hook-on": Exec(c, "UPDATE dbo.ShopWebhooks SET Active=@a WHERE Id=@i AND StoreId=@s", "@a", ShopJson.Bool(ShopJson.At(b, "on")), "@i", id, "@s", s.Id); Forget(s.Id); Ok(res, "tab", Tab(c, s.Id, req)); return true;
                case "api-hook-del": Exec(c, "DELETE FROM dbo.ShopHookSends WHERE HookId=@i AND StoreId=@s AND State IN ('wait','sending'); DELETE FROM dbo.ShopWebhooks WHERE Id=@i AND StoreId=@s", "@i", id, "@s", s.Id); Forget(s.Id); Ok(res, "tab", Tab(c, s.Id, req)); return true;
                case "api-hook-secret":
                    {
                        var sec = "whsec_" + Shops.Token(24); int n; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopWebhooks SET SecretBox=@b, Last4=@l WHERE Id=@i AND StoreId=@s", "@b", Secrets.Protect(sec), "@l", sec.Substring(sec.Length - 4), "@i", id, "@s", s.Id)) n = cmd.ExecuteNonQuery();
                        if (n == 0) throw new ShopException("gone", "العنوان ده مش موجود."); Forget(s.Id); Ok(res, "secret", sec, "tab", Tab(c, s.Id, req)); return true;
                    }
                case "api-hook-try": later = TryHook(c, s, id, res, req); return true;
                case "api-send-again":
                    {
                        int n; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopHookSends SET State='wait', NextAt=SYSUTCDATETIME(), Tries=0, CreatedAt=SYSUTCDATETIME(), DoneAt=NULL WHERE Id=@i AND StoreId=@s AND State='failed' AND Kind<>'ping'", "@i", ShopJson.Int(ShopJson.At(b, "id")), "@s", s.Id)) n = cmd.ExecuteNonQuery();
                        if (n == 0) throw new ShopException("gone", "ده اتبعت أو مش موجود."); Soon(s.Id); Ok(res, "tab", Tab(c, s.Id, req)); return true;
                    }
            }
            return ShopAds.Post(c, s, sh, a, b, res, out later);
        }

        static Func<Task> SaveHook(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res, HttpRequest req)
        {
            int id = Int(b, "id"); string url = Str(b, "url", 500).Trim();
            var ev = ShopJson.List(ShopJson.At(b, "events")).Select(x => ShopJson.Str(x, 30)).Where(x => Events.Any(e => e[0] == x)).Distinct().ToList();
            if (ev.Count == 0) throw new ShopException("events", "اختار برنامجك يوصله إيه.");
            if (id == 0 && ShopApi.Num(c, "SELECT COUNT(*) FROM dbo.ShopWebhooks WHERE StoreId=@s", "@s", s.Id) >= MaxHooks) throw new ShopException("limit", "أقصى عدد " + MaxHooks + " عناوين.");
            if (id > 0 && ShopApi.Num(c, "SELECT COUNT(*) FROM dbo.ShopWebhooks WHERE StoreId=@s AND Id=@i", "@s", s.Id, "@i", id) == 0) throw new ShopException("gone", "العنوان ده مش موجود.");
            if (!Web.Allow("api-hooksave:" + s.Id, 30, TimeSpan.FromHours(1))) throw new ShopException("too_many", "محاولات كتير. استنى شوية.");
            int sid = s.Id;
            return async () =>
            {
                var why = await BadUrl(url); if (why != null) throw new ShopException("url", why);
                using (var c2 = Db.Open())
                {
                    string secret = null;
                    if (id > 0) Exec(c2, "UPDATE dbo.ShopWebhooks SET Url=@u, Events=@e WHERE Id=@i AND StoreId=@s", "@u", url, "@e", string.Join(",", ev), "@i", id, "@s", sid);
                    else { secret = "whsec_" + Shops.Token(24); Exec(c2, "INSERT INTO dbo.ShopWebhooks(StoreId, Url, Events, SecretBox, Last4) VALUES(@s, @u, @e, @b, @l)", "@s", sid, "@u", url, "@e", string.Join(",", ev), "@b", Secrets.Protect(secret), "@l", secret.Substring(secret.Length - 4)); }
                    Forget(sid); Ok(res, "secret", secret, "tab", Tab(c2, sid, req));
                }
            };
        }

        // «جرّب»: a «ping» now, and what the program answered (kept in the list, never sent again)
        static Func<Task> TryHook(SqlConnection c, StoreInfo s, int id, HttpResponse res, HttpRequest req)
        {
            var h = Read(c, "Id=@i AND StoreId=@s", "@i", id, "@s", s.Id).FirstOrDefault(); if (h == null) throw new ShopException("gone", "العنوان ده مش موجود.");
            if (!Web.Allow("api-ping:" + s.Id, 30, TimeSpan.FromHours(1))) throw new ShopException("too_many", "جرّبت كتير. استنى شوية.");
            var sh = Shops.ByStore(c, s.Id); string eid = "evt_" + Shops.Token(16), body = ShopJson.Write(D("id", eid, "type", "ping", "created_at", Iso(DateTime.UtcNow), "store", D("slug", sh.Slug, "name", sh.Name), "data", D("message", "A test from SellyStore. Answer 200 and it's linked.")));
            string secret = Secrets.Unprotect(h.Box); int sid = s.Id;
            return async () =>
            {
                int code = 0; var why = secret == null ? "السر ماتقراش. اعمل سر جديد." : await Post(h.Url, secret, "ping", eid, 1, body, n => code = n);
                using (var c2 = Db.Open())
                {
                    Exec(c2, "INSERT INTO dbo.ShopHookSends(StoreId, HookId, EventId, Kind, Dedup, Body, State, Tries, LastCode, LastError, DoneAt) VALUES(@s, @h, @e, 'ping', @d, @b, @st, 1, @c, @er, SYSUTCDATETIME())",
                        "@s", sid, "@h", h.Id, "@e", eid, "@d", "ping-" + eid, "@b", body, "@st", why == null ? "ok" : "failed", "@c", code == 0 ? (object)null : code, "@er", ShopApi.Cut(why, 300));
                    if (why == null) Exec(c2, "UPDATE dbo.ShopWebhooks SET LastOkAt=SYSUTCDATETIME(), LastError=NULL WHERE Id=@h", "@h", h.Id); else Exec(c2, "UPDATE dbo.ShopWebhooks SET LastFailAt=SYSUTCDATETIME(), LastError=@e WHERE Id=@h", "@e", ShopApi.Cut(why, 300), "@h", h.Id);
                    Ok(res, "sent", why == null, "why", why ?? "", "code", code, "tab", Tab(c2, sid, req));
                }
            };
        }
    }
}
