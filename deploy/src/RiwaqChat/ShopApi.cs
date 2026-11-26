// «ربط ببرامج تانية (API)»: the merchant's accounting or warehouse program reads the store and changes it, with a key the owner makes in
// «متجري». Everything is under /api/v1/ (web.config sends that folder here; /api/v1.ashx/... reaches the same handler where the mapping
// isn't there). The routes are one list (Routes): the handler dispatches from it and /api/v1/docs is written from it, so the page can't
// say what the handler doesn't do. A key is shown once and kept as an HMAC; it has what it may do (scopes) and its own rate limit, and
// every change it makes is logged with its name. Changes take the store's own paths: an order's status goes through ShopOrders.SetStatus
// (the customer's WhatsApp, the stock and «زبايني» follow), a quantity is written in ShopStock like a count of the shelf.
// ShopApiHooks.cs: the webhooks, what changed in the products, the owner's tab. Off until the owner turns it on (ShopApiConf.ApiOn).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    // An answer with an error: the status, a stable code, the words in English and Arabic
    public class ApiError : Exception
    {
        public readonly int Status; public readonly string Code, Ar; public readonly object Details;
        public ApiError(int status, string code, string en, string ar, object details = null) : base(en) { Status = status; Code = code; Ar = ar; Details = details; }
    }

    public class ApiKey { public int Id, StoreId; public string Name, Scopes, Last4; public DateTime? StoppedAt; public bool Can(string scope) { return (Scopes ?? "").Split(',').Contains(scope); } }

    public class ShopApiHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx) { ShopApi.Serve(ctx); }
    }

    public static partial class ShopApi
    {
        public const string Prefix = "ssk_";
        public const int MaxKeys = 5, MaxPage = 200, MaxBulk = 200;
        // what a key may do, as the owner reads it
        public static readonly string[][] Scopes = {
            new[] { "products:read", "يقرا المنتجات والكميات", "Read products and stock" }, new[] { "products:write", "يغيّر الكمية والأسعار والظهور", "Change stock, prices and what is shown" },
            new[] { "orders:read", "يقرا الأوردرات", "Read orders" }, new[] { "orders:write", "يغيّر حالة الأوردر", "Change an order's status" }, new[] { "customers:read", "يقرا الزباين", "Read customers" } };
        static int PerMinute { get { int n; return int.TryParse(Web.Setting("ApiPerMinute"), out n) && n > 0 ? n : 120; } }

        public const string Schema = @"
IF OBJECT_ID('dbo.ShopApiConf') IS NULL
  CREATE TABLE dbo.ShopApiConf(StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ApiOn BIT NOT NULL DEFAULT 0, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.ShopApiKeys') IS NULL
BEGIN
  CREATE TABLE dbo.ShopApiKeys(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Name NVARCHAR(60) NOT NULL, Lookup CHAR(12) NOT NULL, Hash CHAR(64) NOT NULL,
    Scopes NVARCHAR(200) NOT NULL, Last4 NVARCHAR(8) NOT NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UsedAt DATETIME2 NULL, UsedIp NVARCHAR(64) NULL, StoppedAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopApiKeys_Lookup ON dbo.ShopApiKeys(Lookup);
  CREATE INDEX IX_ShopApiKeys_Store ON dbo.ShopApiKeys(StoreId);
END
IF OBJECT_ID('dbo.ShopApiLog') IS NULL
BEGIN
  CREATE TABLE dbo.ShopApiLog(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, KeyId INT NULL, KeyName NVARCHAR(60) NOT NULL, What NVARCHAR(400) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopApiLog_Store ON dbo.ShopApiLog(StoreId, Id);
END
-- the owner's addresses for webhooks; the secret signs, so it's kept encrypted (not as a hash)
IF OBJECT_ID('dbo.ShopWebhooks') IS NULL
BEGIN
  CREATE TABLE dbo.ShopWebhooks(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Url NVARCHAR(500) NOT NULL, Events NVARCHAR(200) NOT NULL, SecretBox NVARCHAR(400) NOT NULL, Last4 NVARCHAR(8) NOT NULL,
    Active BIT NOT NULL DEFAULT 1, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), LastOkAt DATETIME2 NULL, LastFailAt DATETIME2 NULL, LastError NVARCHAR(300) NULL, AlertDue DATETIME2 NULL, AlertedAt DATETIME2 NULL);
  CREATE INDEX IX_ShopWebhooks_Store ON dbo.ShopWebhooks(StoreId);
END
-- one row per event per address. State: wait / sending / ok / failed. Dedup: one row for an event however many times it is noticed
IF OBJECT_ID('dbo.ShopHookSends') IS NULL
BEGIN
  CREATE TABLE dbo.ShopHookSends(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, HookId INT NOT NULL, EventId CHAR(36) NOT NULL, Kind NVARCHAR(30) NOT NULL, Dedup NVARCHAR(80) NOT NULL,
    Body NVARCHAR(MAX) NOT NULL, State NVARCHAR(8) NOT NULL DEFAULT 'wait', Tries INT NOT NULL DEFAULT 0, NextAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ClaimedAt DATETIME2 NULL, LastCode INT NULL, LastError NVARCHAR(300) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), DoneAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopHookSends_Dedup ON dbo.ShopHookSends(HookId, Dedup);
  CREATE INDEX IX_ShopHookSends_Due ON dbo.ShopHookSends(State, NextAt);
  CREATE INDEX IX_ShopHookSends_Store ON dbo.ShopHookSends(StoreId, Id);
END
-- what the API last saw of each product: its fingerprint, its sizes that are low, and when it last changed (updated_since, the webhooks)
IF OBJECT_ID('dbo.ShopApiSeen') IS NULL
  CREATE TABLE dbo.ShopApiSeen(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, Sig CHAR(16) NOT NULL, Lows NVARCHAR(400) NOT NULL DEFAULT N'', ChangedAt DATETIME2 NOT NULL, Gone BIT NOT NULL DEFAULT 0,
    PRIMARY KEY(StoreId, ProductId));
";

        // ---------- small things ----------
        internal static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        internal static string Cut(string s, int n) { return s == null ? null : s.Length > n ? s.Substring(0, n) : s; }
        // to the database's own tick: server_time comes back as updated_since, compared with >= (see Clock)
        internal static string Iso(DateTime? t) { return t.HasValue ? DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture) : null; }
        internal static DateTime? Dt(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }
        internal static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        internal static string Hex(byte[] b) { return string.Concat(b.Select(x => x.ToString("x2"))); }
        internal static void Exec(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } }
        internal static int Num(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v); } }

        // ---------- the switch ----------
        public static bool IsOn(SqlConnection c, int storeId) { return Num(c, "SELECT COUNT(*) FROM dbo.ShopApiConf WHERE StoreId=@s AND ApiOn=1", "@s", storeId) > 0; }
        public static void SetOn(SqlConnection c, int storeId, bool on)
        {
            Exec(c, null, "UPDATE dbo.ShopApiConf SET ApiOn=@o, At=SYSUTCDATETIME() WHERE StoreId=@s; IF @@ROWCOUNT=0 INSERT INTO dbo.ShopApiConf(StoreId, ApiOn) VALUES(@s, @o)", "@s", storeId, "@o", on);
            ShopApiHooks.Forget(storeId);
        }

        // ---------- keys ----------
        // kept as an HMAC with a key of the site's own (a copy of the database alone can't test guesses)
        static byte[] pepper;
        static byte[] Pepper()
        {
            if (pepper != null) return pepper;
            var m = Web.Setting("AuthSecret"); if (string.IsNullOrEmpty(m) || m.Length < 32) throw new InvalidOperationException("AuthSecret is not configured");
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(m))) return pepper = h.ComputeHash(Encoding.UTF8.GetBytes("sellystore-api-key"));
        }
        static string KeyHash(string key) { using (var h = new HMACSHA256(Pepper())) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(key))); }
        static readonly Regex KeyRx = new Regex("^" + Prefix + "[a-f0-9]{52}$", RegexOptions.Compiled);

        // the whole key, to show once: ssk_ + 12 to find it + 40 more (208 random bits in all)
        public static string NewKey(SqlConnection c, int storeId, string name, IEnumerable<string> scopes, out int id)
        {
            name = Regex.Replace(Cut((name ?? "").Trim(), 60), @"[\u0000-\u001F]", "");
            if (name.Length < 2) throw new ShopException("name", "اكتب اسم للمفتاح، زي «برنامج الحسابات».");
            var sc = scopes.Where(x => Scopes.Any(y => y[0] == x)).Distinct().ToList(); if (sc.Count == 0) throw new ShopException("scopes", "اختار البرنامج يقدر يعمل إيه.");
            if (Num(c, "SELECT COUNT(*) FROM dbo.ShopApiKeys WHERE StoreId=@s AND StoppedAt IS NULL", "@s", storeId) >= MaxKeys) throw new ShopException("limit", "أقصى عدد " + MaxKeys + " مفاتيح شغالة. وقّف واحد الأول.");
            string look = Shops.Token(6), key = Prefix + look + Shops.Token(20);
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopApiKeys(StoreId, Name, Lookup, Hash, Scopes, Last4) OUTPUT INSERTED.Id VALUES(@s, @n, @l, @h, @sc, @l4)", "@s", storeId, "@n", name, "@l", look, "@h", KeyHash(key), "@sc", string.Join(",", sc), "@l4", key.Substring(key.Length - 4))) id = (int)cmd.ExecuteScalar();
            return key;
        }
        public static List<Dictionary<string, object>> Keys(SqlConnection c, int storeId)
        {
            var l = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT Id, Name, Scopes, Last4, CreatedAt, UsedAt, StoppedAt FROM dbo.ShopApiKeys WHERE StoreId=@s ORDER BY CASE WHEN StoppedAt IS NULL THEN 0 ELSE 1 END, Id DESC", "@s", storeId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(D("id", (int)r[0], "name", (string)r[1], "scopes", ((string)r[2]).Split(','), "last4", (string)r[3], "at", Iso(Dt(r[4])), "used", Iso(Dt(r[5])), "stopped", Iso(Dt(r[6]))));
            return l;
        }

        static readonly Dictionary<int, DateTime> usedMark = new Dictionary<int, DateTime>();
        static ApiKey Auth(SqlConnection c, HttpRequest req, string ip)
        {
            string tok = req.Headers["Authorization"];
            tok = tok != null && tok.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? tok.Substring(7).Trim() : (req.Headers["X-Api-Key"] ?? "").Trim();
            if (tok.Length == 0) throw new ApiError(401, "no_key", "Send your key in the Authorization header: Bearer ssk_...", "ابعت المفتاح في الـ Authorization header بالشكل: Bearer ssk_...");
            Func<ApiError> bad = () => new ApiError(401, "bad_key", "This key isn't right.", "المفتاح ده مش صح.");
            ApiKey k = null; string hash = null;
            if (KeyRx.IsMatch(tok))
                using (var cmd = Db.Cmd(c, "SELECT Id, StoreId, Name, Scopes, Last4, StoppedAt, Hash FROM dbo.ShopApiKeys WHERE Lookup=@l", "@l", tok.Substring(Prefix.Length, 12))) using (var r = cmd.ExecuteReader())
                    if (r.Read()) { k = new ApiKey { Id = (int)r[0], StoreId = (int)r[1], Name = (string)r[2], Scopes = (string)r[3], Last4 = (string)r[4], StoppedAt = Dt(r[5]) }; hash = (string)r[6]; }
            if (k == null || !Secrets.Same(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(KeyHash(tok)))) { Strike(ip); throw bad(); }
            if (k.StoppedAt.HasValue) throw new ApiError(401, "key_stopped", "This key was stopped by the store's owner.", "صاحب المتجر وقّف المفتاح ده.");
            return k;
        }
        // guessing keys from one place: 20 wrong ones in 10 minutes, then nothing from there for 10 minutes
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> blocked = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
        static void Strike(string ip) { if (!Web.Allow("api-bad:" + ip, 20, TimeSpan.FromMinutes(10))) { if (blocked.Count > 20000) blocked.Clear(); blocked[ip] = DateTime.UtcNow.AddMinutes(10); } }
        static bool Struck(string ip) { DateTime t; return blocked.TryGetValue(ip, out t) && t > DateTime.UtcNow; }

        // ---------- the requests ----------
        public class Call
        {
            public SqlConnection C; public HttpRequest Req; public HttpResponse Res; public ApiKey Key; public StoreInfo S; public Shop Sh; public string Ip;
            public Dictionary<string, string> Arg = new Dictionary<string, string>(); public Dictionary<string, object> Body;
            public string Q(string k) { return (Req.QueryString[k] ?? "").Trim(); }
            public string Who { get { return Cut("🔌 " + Key.Name, 20); } }
        }
        public class Route
        {
            public string Method, Path, Scope, Ar, En, Sample; public string[][] Params; public Action<Call> Run; Regex rx;
            public Regex Rx { get { return rx ?? (rx = new Regex("^" + Regex.Replace(Path, @"\{(\w+)\}", m => "(?<" + m.Groups[1].Value + ">" + (m.Groups[1].Value == "sku" ? "[^/]{1,80}" : m.Groups[1].Value == "phone" ? @"\+?[0-9]{8,16}" : "[0-9]{1,12}") + ")") + "$", RegexOptions.IgnoreCase)); } }
        }

        public static void Serve(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.TrySkipIisCustomErrors = true; res.Cache.SetCacheability(HttpCacheability.NoCache); res.Cache.SetNoStore(); res.AddHeader("X-Content-Type-Options", "nosniff");
            string path = req.Path ?? ""; int at = path.IndexOf("/api/v1", StringComparison.OrdinalIgnoreCase); string rest = at < 0 ? "" : path.Substring(at + 7);
            if (rest.StartsWith(".ashx", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(5);
            rest = rest.TrimEnd('/');
            string ip = Web.Ip(req);
            try
            {
                if (!Db.Configured) throw new ApiError(503, "unavailable", "The service isn't available right now.", "الخدمة مش متاحة دلوقتي.");
                if (rest.Length == 0 || rest.Equals("/docs", StringComparison.OrdinalIgnoreCase)) { if (!Web.Allow("api-docs:" + ip, 120, TimeSpan.FromMinutes(1))) throw Many(); Docs(req, res); return; }
                // the key would travel in the clear: only https on the real site (the same test as CanonicalHost's redirect)
                if ((Web.Setting("CanonicalHost") ?? "").Trim().Length > 0 && !req.IsSecureConnection && !string.Equals(req.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase))
                    throw new ApiError(403, "https_required", "Use https.", "استخدم https.");
                if (!Web.Allow("api-ip:" + ip, 1200, TimeSpan.FromMinutes(1))) throw Many();
                string method = req.HttpMethod == "PATCH" || req.HttpMethod == "PUT" ? "PATCH" : req.HttpMethod;
                var hits = Routes.Where(r => r.Rx.IsMatch(rest)).ToList();
                if (hits.Count == 0) throw new ApiError(404, "not_found", "There is no such address in the API. See /api/v1/docs", "العنوان ده مش موجود. شوف /api/v1/docs");
                // POST does what PATCH does, for programs that can't send PATCH
                var route = hits.FirstOrDefault(r => r.Method == method) ?? (method == "POST" ? hits.FirstOrDefault(r => r.Method == "PATCH") : null);
                if (route == null) { res.AddHeader("Allow", string.Join(", ", hits.Select(r => r.Method).Distinct())); throw new ApiError(405, "method_not_allowed", "Use " + string.Join(" or ", hits.Select(r => r.Method).Distinct()) + " here.", "استخدم " + string.Join(" أو ", hits.Select(r => r.Method).Distinct()) + " هنا."); }
                if (Struck(ip)) throw new ApiError(429, "too_many_bad_keys", "Too many wrong keys from this address. Wait 10 minutes.", "مفاتيح غلط كتير من نفس المكان. استنى 10 دقايق.");
                using (var c = Db.Open())
                {
                    if (!Shops.Ready) throw new ApiError(503, "unavailable", "The service isn't available right now.", "الخدمة مش متاحة دلوقتي.");
                    var call = new Call { C = c, Req = req, Res = res, Ip = ip, Key = Auth(c, req, ip) };
                    call.S = Db.StoreById(c, call.Key.StoreId); call.Sh = Shops.ByStore(c, call.Key.StoreId);
                    if (call.S == null || call.Sh == null || !Shops.Visible(c, call.S)) throw new ApiError(403, "not_open", "This store has no SellyStore.", "المتجر ده ماعندوش متجر SellyStore.");
                    if (call.S.Status != "active") throw new ApiError(403, "store_inactive", "This store's account isn't active.", "حساب المتجر مش شغال.");
                    if (!IsOn(c, call.S.Id)) throw new ApiError(403, "api_off", "The store's owner turned the API off.", "صاحب المتجر قافل الربط ببرامج تانية.");
                    if (!Web.Allow("api-key:" + call.Key.Id, PerMinute, TimeSpan.FromMinutes(1))) { res.AddHeader("Retry-After", "60"); throw new ApiError(429, "rate_limited", "At most " + PerMinute + " requests a minute for one key. Wait a minute.", "أقصى " + PerMinute + " طلب في الدقيقة للمفتاح الواحد. استنى دقيقة."); }
                    if (route.Scope != null && !call.Key.Can(route.Scope)) throw new ApiError(403, "forbidden", "This key may not " + Scopes.First(x => x[0] == route.Scope)[2].ToLowerInvariant() + " (" + route.Scope + ").", "المفتاح ده مش مسموحله: " + Scopes.First(x => x[0] == route.Scope)[1] + ".");
                    Used(c, call.Key, ip);
                    var m = route.Rx.Match(rest); foreach (var g in route.Rx.GetGroupNames().Where(g => !Regex.IsMatch(g, @"^\d+$"))) call.Arg[g] = HttpUtility.UrlDecode(m.Groups[g].Value);
                    if (method != "GET") { try { call.Body = Web.Body(req, 400000); } catch (BadRequestException) { throw new ApiError(400, "bad_json", "The body isn't JSON, or it's too big.", "المحتوى مش JSON أو كبير جدًا."); } }
                    route.Run(call);
                }
            }
            catch (ApiError ex) { Fail(res, ex); }
            catch (ShopException ex) { Fail(res, new ApiError(422, ex.Code, ex.Message, ex.Message)); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Fail(res, new ApiError(500, "server_error", "Something went wrong on our side. Try again.", "حصلت مشكلة عندنا. جرّب تاني.")); }
        }
        static ApiError Many() { return new ApiError(429, "rate_limited", "Too many requests. Wait a minute.", "طلبات كتير. استنى دقيقة."); }
        static void Fail(HttpResponse res, ApiError ex)
        {
            var e = D("code", ex.Code, "message", ex.Message, "message_ar", ex.Ar); if (ex.Details != null) e["details"] = ex.Details;
            Web.Json(res, ex.Status, D("error", e));
        }
        static void Used(SqlConnection c, ApiKey k, string ip)
        {
            lock (usedMark) { DateTime t; if (usedMark.TryGetValue(k.Id, out t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(1)) return; usedMark[k.Id] = DateTime.UtcNow; }
            Exec(c, null, "UPDATE dbo.ShopApiKeys SET UsedAt=SYSUTCDATETIME(), UsedIp=@ip WHERE Id=@i", "@ip", Cut(ip, 64), "@i", k.Id);
        }
        public static void Log_(SqlConnection c, int storeId, ApiKey k, string what) { Exec(c, null, "INSERT INTO dbo.ShopApiLog(StoreId, KeyId, KeyName, What) VALUES(@s, @k, @n, @w)", "@s", storeId, "@k", k == null ? (object)null : k.Id, "@n", k == null ? "" : k.Name, "@w", Cut(what, 400)); }
        static void Ok(Call x, object o) { Web.Json(x.Res, 200, o); }

        // ---------- paging and dates ----------
        static void Page(Call x, out int page, out int per)
        {
            page = Math.Max(1, ShopJson.Int(x.Q("page"), 1)); per = ShopJson.Int(x.Q("per_page"), 50);
            if (per < 1 || per > MaxPage) throw new ApiError(400, "bad_per_page", "per_page is 1 to " + MaxPage + ".", "per_page من 1 لـ " + MaxPage + ".");
            if (page > 100000) throw new ApiError(400, "bad_page", "page is too big.", "page كبير جدًا.");
        }
        // an ISO date and time (UTC unless it says otherwise), or a day (the store's day, Cairo)
        static DateTime? When(Call x, string k, bool end = false)
        {
            var s = x.Q(k); if (s.Length == 0) return null; DateTime t;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return Carriers.FromCairo(t.ToString("yyyy-MM-dd") + " 00:00:00").Value.AddDays(end ? 1 : 0);
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            throw new ApiError(400, "bad_date", k + " must be like 2026-11-26T09:30:00Z or 2026-11-26.", k + " لازم يكون بالشكل 2026-11-26T09:30:00Z أو 2026-11-26.");
        }
        static DateTime Now(SqlConnection c) { using (var cmd = Db.Cmd(c, "SELECT SYSUTCDATETIME()")) return DateTime.SpecifyKind((DateTime)cmd.ExecuteScalar(), DateTimeKind.Utc); }
        // server_time for the products: after every change already marked (the clock's tick is coarse, so it waits for the next one), and
        // updated_since takes >=: what was seen before is never sent again, what comes after is never missed
        static DateTime Clock(SqlConnection c, int storeId)
        {
            DateTime last; using (var cmd = Db.Cmd(c, "SELECT ISNULL(MAX(ChangedAt), '2000-01-01') FROM dbo.ShopApiSeen WHERE StoreId=@s", "@s", storeId)) last = (DateTime)cmd.ExecuteScalar();
            var now = Now(c); for (int i = 0; i < 40 && now <= last; i++) { System.Threading.Thread.Sleep(2); now = Now(c); }
            return now;
        }
        static Dictionary<string, object> List(object data, int page, int per, int total, DateTime now, object extra = null)
        {
            var d = D("data", data, "page", page, "per_page", per, "total", total, "has_more", page * per < total, "server_time", Iso(now));
            if (extra != null) foreach (var kv in (Dictionary<string, object>)extra) d[kv.Key] = kv.Value;
            return d;
        }

        // ---------- products ----------
        public static Dictionary<string, object> ProdJson(ShopData d, ShopProd p, DateTime? changed)
        {
            bool single = p.Options.Count == 0;
            return D("id", p.Id, "slug", p.Slug, "name", p.Name, "name_en", p.NameEn ?? "", "sku", p.Sku ?? "", "status", p.Status, "price", p.Price, "old_price", p.OldPrice, "cost", p.Cost, "unit", p.Unit ?? "", "brand", p.Brand ?? "",
                "track_stock", p.Track, "when_out", p.OutMode, "stock_total", p.Track ? (object)p.Total : null, "available", p.Available, "url", d.Row.Url + "/p/" + p.Slug,
                "images", p.Pics.Select(x => ShopImages.Url(x.Path)).ToArray(), "categories", p.Cats.Select(k => { ShopCat g; return d.CatById.TryGetValue(k, out g) ? (object)D("id", g.Id, "name", g.Name, "main", g.Id == p.MainCat) : null; }).Where(z => z != null).ToArray(),
                "options", p.Options.Select(o => D("name", o.Ar, "name_en", o.En ?? "", "values", o.Vals.Select(v => v.Ar).ToArray())).ToArray(),
                "variants", p.Vars.Select(v => D("id", v.Id, "sku", v.Sku ?? p.Sku ?? "", "options", v.Vals, "label", v.Label, "price", p.PriceOf(v), "old_price", p.OldOf(v), "cost", p.CostOf(v), "stock", p.Track ? (object)v.Stock : null, "active", single || v.Active)).ToArray(),
                "created_at", Iso(p.CreatedAt), "updated_at", Iso(changed ?? p.UpdatedAt));
        }
        // when each product last changed as the watcher saw it (ShopApiHooks.Watch); Gone = deleted
        class Seen { public DateTime At; public bool Gone; }
        static Dictionary<int, Seen> Changed(SqlConnection c, int storeId)
        {
            var l = new Dictionary<int, Seen>();
            using (var cmd = Db.Cmd(c, "SELECT ProductId, ChangedAt, Gone FROM dbo.ShopApiSeen WHERE StoreId=@s", "@s", storeId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l[(int)r[0]] = new Seen { At = Dt(r[1]).Value, Gone = (bool)r[2] };
            return l;
        }
        static DateTime? At(Dictionary<int, Seen> ch, int id) { Seen s; return ch.TryGetValue(id, out s) ? s.At : (DateTime?)null; }
        static void Products(Call x)
        {
            int page, per; Page(x, out page, out per); var since = When(x, "updated_since"); string sku = x.Q("sku"), st = x.Q("status");
            if (st.Length > 0 && st != "active" && st != "hidden") throw new ApiError(400, "bad_status", "status is active or hidden.", "status يا active يا hidden.");
            var d = ShopCache.Get(x.C, x.Sh); ShopApiHooks.Watch(x.C, x.Sh, d); var now = Clock(x.C, x.S.Id); var ch = Changed(x.C, x.S.Id);
            var all = d.Products.Where(p => (!since.HasValue || (At(ch, p.Id) ?? p.UpdatedAt) >= since.Value) && (st.Length == 0 || p.Status == st) && (sku.Length == 0 || string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase) || p.Vars.Any(v => string.Equals(v.Sku, sku, StringComparison.OrdinalIgnoreCase)))).OrderBy(p => p.Id).ToList();
            var gone = since.HasValue ? ch.Where(k => k.Value.Gone && k.Value.At >= since.Value).Select(k => k.Key).OrderBy(k => k).ToArray() : null;
            Ok(x, List(all.Skip((page - 1) * per).Take(per).Select(p => ProdJson(d, p, At(ch, p.Id))).ToArray(), page, per, all.Count, now, gone == null ? null : D("deleted", gone)));
        }
        static void Product(Call x)
        {
            var d = ShopCache.Get(x.C, x.Sh); ShopProd p; if (!d.ById.TryGetValue(ShopJson.Int(x.Arg["id"]), out p)) throw NoProduct();
            ShopApiHooks.Watch(x.C, x.Sh, d); Ok(x, D("data", ProdJson(d, p, At(Changed(x.C, x.S.Id), p.Id))));
        }
        static ApiError NoProduct() { return new ApiError(404, "product_not_found", "There is no product with this id in the store.", "مفيش منتج بالرقم ده في المتجر."); }

        // one change: what it's on (a product, a size, a SKU) and what changes
        class Change { public int Index, ProductId, VariantId; public string Sku; public int? Stock, Delta; public decimal? Price, Old; public bool OldGiven; public bool? Shown; }
        static Change ReadChange(Dictionary<string, object> b, int index)
        {
            var ch = new Change { Index = index, ProductId = ShopJson.Int(ShopJson.At(b, "product_id")), VariantId = ShopJson.Int(ShopJson.At(b, "variant_id")), Sku = ShopJson.Str(ShopJson.At(b, "sku"), 80) };
            int ids = (ch.ProductId > 0 ? 1 : 0) + (ch.VariantId > 0 ? 1 : 0) + (ch.Sku.Length > 0 ? 1 : 0);
            if (ids != 1) throw new ApiError(422, "which", "Say which one with exactly one of product_id, variant_id or sku.", "حدّد المنتج بواحد بس من product_id أو variant_id أو sku.");
            Func<string, bool> has = k => b.ContainsKey(k);
            if (has("stock") && has("stock_change")) throw new ApiError(422, "stock_both", "Send stock or stock_change, not both.", "ابعت stock أو stock_change، مش الاتنين.");
            if (has("stock")) { int n; if (!int.TryParse(ShopJson.Str(b["stock"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0 || n > 1000000) throw new ApiError(422, "bad_stock", "stock is a whole number from 0 to 1000000.", "stock رقم صحيح من 0 لـ 1000000."); ch.Stock = n; }
            if (has("stock_change")) { int n; if (!int.TryParse(ShopJson.Str(b["stock_change"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n == 0 || Math.Abs(n) > 1000000) throw new ApiError(422, "bad_stock_change", "stock_change is a whole number, not 0 (for example -2 or 5).", "stock_change رقم صحيح غير الصفر (زي -2 أو 5)."); ch.Delta = n; }
            if (has("price")) { var p = ShopJson.DecN(b["price"]); if (!p.HasValue || p.Value < 1 || p.Value > 5000000) throw new ApiError(422, "bad_price", "price is from 1 to 5000000 (EGP).", "price من 1 لـ 5000000 جنيه."); ch.Price = p; }
            if (has("old_price")) { ch.OldGiven = true; var o = b["old_price"] == null ? 0 : ShopJson.DecN(b["old_price"]) ?? -1; if (o < 0 || o > 5000000) throw new ApiError(422, "bad_old_price", "old_price is a number, or 0 / null to remove it.", "old_price رقم، أو 0 / null عشان يتشال."); ch.Old = o == 0 ? (decimal?)null : o; }
            if (has("shown")) { if (!(b["shown"] is bool)) throw new ApiError(422, "bad_shown", "shown is true or false.", "shown يا true يا false."); ch.Shown = (bool)b["shown"]; }
            if (!ch.Stock.HasValue && !ch.Delta.HasValue && !ch.Price.HasValue && !ch.OldGiven && !ch.Shown.HasValue) throw new ApiError(422, "nothing", "Nothing to change: send stock, stock_change, price, old_price or shown.", "مفيش حاجة تتغيّر: ابعت stock أو stock_change أو price أو old_price أو shown.");
            return ch;
        }

        // applies one change; the values are read again under a lock, the ids from the catalog in memory
        static Dictionary<string, object> Apply(Call x, ShopData d, Change ch, ref bool look)
        {
            ShopProd p = null; ShopVar v = null;
            if (ch.VariantId > 0) { if (!d.Vars.TryGetValue(ch.VariantId, out v)) throw new ApiError(404, "variant_not_found", "There is no size / variant with this id in the store.", "مفيش مقاس بالرقم ده في المتجر."); p = d.ById[v.ProductId]; }
            else if (ch.ProductId > 0) { if (!d.ById.TryGetValue(ch.ProductId, out p)) throw NoProduct(); }
            else
            {
                var hits = d.Products.SelectMany(q => q.Vars.Where(w => string.Equals((w.Sku ?? q.Sku ?? "").Trim(), ch.Sku.Trim(), StringComparison.OrdinalIgnoreCase)).Select(w => new { q, w })).ToList();
                if (hits.Count == 0) throw new ApiError(404, "sku_not_found", "No product or size has this SKU.", "مفيش منتج أو مقاس بالـ SKU ده.");
                if (hits.Count > 1 && !(hits.All(h => h.q.Id == hits[0].q.Id && h.w.Sku == null))) throw new ApiError(409, "sku_not_unique", "More than one product or size has this SKU: use variant_id.", "أكتر من منتج أو مقاس عليهم نفس الـ SKU: استخدم variant_id.", D("variant_ids", hits.Select(h => h.w.Id).ToArray()));
                p = hits[0].q; if (hits.Count == 1) v = hits[0].w;
            }
            bool single = p.Options.Count == 0; if (v == null && single && p.Vars.Count > 0) v = p.Vars[0];
            if ((ch.Stock.HasValue || ch.Delta.HasValue) && v == null) throw new ApiError(422, "has_sizes", "This product has sizes: change the stock of each one (variant_id or its SKU).", "المنتج ده ليه مقاسات: غيّر كمية كل مقاس لوحده (variant_id أو الـ SKU بتاعه).");
            if ((ch.Stock.HasValue || ch.Delta.HasValue) && !p.Track) throw new ApiError(422, "stock_not_tracked", "This product's stock isn't counted (the owner turned counting off).", "كمية المنتج ده مش متعدّة (صاحب المتجر قافل العدّ).");
            bool onVar = v != null && !single;   // a size of a product with sizes: its own price and its own «shown»
            var did = new List<string>(); string label = p.Name + (v != null && v.Label.Length > 0 ? " (" + v.Label + ")" : ""); int? stockNow = null;
            using (var tx = x.C.BeginTransaction())
            {
                try
                {
                    decimal price, pPrice; decimal? old, vPrice, vOld, pOld; string status; bool active = true; int stock = 0;
                    using (var cmd = Db.Cmd(x.C, "SELECT p.Price, p.OldPrice, p.Status, v.Price, v.OldPrice, v.Active, v.Stock FROM dbo.ShopProducts p WITH (UPDLOCK) LEFT JOIN dbo.ShopVariants v WITH (UPDLOCK) ON v.Id=@v AND v.ProductId=p.Id WHERE p.Id=@p AND p.StoreId=@s", "@p", p.Id, "@v", v == null ? 0 : v.Id, "@s", x.S.Id))
                    {
                        cmd.Transaction = tx; using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read() || (v != null && r[6] == DBNull.Value)) throw NoProduct();
                            pPrice = (decimal)r[0]; pOld = r[1] as decimal?; status = (string)r[2]; vPrice = r[3] as decimal?; vOld = r[4] as decimal?; if (v != null) { active = (bool)r[5]; stock = (int)r[6]; }
                        }
                    }
                    price = onVar && vPrice.HasValue ? vPrice.Value : pPrice; old = onVar ? (vPrice.HasValue ? vOld : pOld) : pOld;
                    if (ch.Price.HasValue || ch.OldGiven)
                    {
                        decimal np = ch.Price ?? price; decimal? no = ch.OldGiven ? ch.Old : old;
                        if (no.HasValue && no.Value <= np) { if (ch.OldGiven) throw new ApiError(422, "old_price_low", "old_price must be more than the price.", "old_price لازم يكون أكبر من السعر."); no = null; }
                        if (onVar) Exec(x.C, tx, "UPDATE dbo.ShopVariants SET Price=@p, OldPrice=@o WHERE Id=@v AND StoreId=@s", "@p", np, "@o", no.HasValue ? (object)no.Value : null, "@v", v.Id, "@s", x.S.Id);
                        else Exec(x.C, tx, "UPDATE dbo.ShopProducts SET Price=@p, OldPrice=@o, UpdatedAt=SYSUTCDATETIME() WHERE Id=@i AND StoreId=@s", "@p", np, "@o", no.HasValue ? (object)no.Value : null, "@i", p.Id, "@s", x.S.Id);
                        if (np != price) did.Add("السعر من " + ShopOrders.Money(price) + " لـ " + ShopOrders.Money(np)); if (no != old) did.Add(no.HasValue ? "السعر قبل الخصم " + ShopOrders.Money(no.Value) : "شال السعر قبل الخصم");
                        look = true;
                    }
                    if (ch.Shown.HasValue)
                    {
                        if (onVar) { if (active != ch.Shown.Value) { Exec(x.C, tx, "UPDATE dbo.ShopVariants SET Active=@a WHERE Id=@v AND StoreId=@s", "@a", ch.Shown.Value, "@v", v.Id, "@s", x.S.Id); did.Add(ch.Shown.Value ? "المقاس بقى ظاهر" : "المقاس اتخفى"); } }
                        else if ((status == "active") != ch.Shown.Value) { Exec(x.C, tx, "UPDATE dbo.ShopProducts SET Status=@st, UpdatedAt=SYSUTCDATETIME() WHERE Id=@i AND StoreId=@s", "@st", ch.Shown.Value ? "active" : "hidden", "@i", p.Id, "@s", x.S.Id); did.Add(ch.Shown.Value ? "بقى ظاهر" : "اتخفى"); }
                        look = true;
                    }
                    if (ch.Stock.HasValue || ch.Delta.HasValue)
                    {
                        int after = ch.Stock.HasValue ? ch.Stock.Value : Math.Max(0, stock + ch.Delta.Value);
                        if (after != stock)
                        {
                            Exec(x.C, tx, "UPDATE dbo.ShopVariants SET Stock=@q WHERE Id=@v AND StoreId=@s; UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@q", after, "@v", v.Id, "@s", x.S.Id);
                            ShopOrders.Stock(x.C, tx, x.S.Id, p.Id, v.Id, after - stock, after, "api", null, Cut(x.Key.Name, 120));
                            did.Add("الكمية من " + stock + " لـ " + after);
                        }
                        stockNow = after;
                    }
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            if (did.Count > 0) Log_(x.C, x.S.Id, x.Key, label + ": " + string.Join("، ", did));
            var o = D("ok", true, "index", ch.Index, "product_id", p.Id, "variant_id", v == null ? (object)null : v.Id, "changed", did.Count > 0);
            if (stockNow.HasValue) o["stock"] = stockNow.Value;
            return o;
        }
        static void Patch(Call x, Dictionary<string, object> b)
        {
            var d = ShopCache.Get(x.C, x.Sh); bool look = false; var ch = ReadChange(b, 0);
            var r = Apply(x, d, ch, ref look); After(x, look);
            d = ShopCache.Get(x.C, x.Sh); ShopProd p; d.ById.TryGetValue((int)r["product_id"], out p);
            Ok(x, D("data", p == null ? null : ProdJson(d, p, At(Changed(x.C, x.S.Id), p.Id)), "changed", r["changed"]));
        }
        static void After(Call x, bool look) { if (look) Shops.Bump(x.C, x.S.Id); try { ShopApiHooks.Watch(x.C, x.Sh); } catch (Exception ex) { Log.Error(ex); } }
        static Dictionary<string, object> With(Call x, string k, object v) { var b = new Dictionary<string, object>(x.Body ?? new Dictionary<string, object>()); b.Remove("product_id"); b.Remove("variant_id"); b.Remove("sku"); b[k] = v; return b; }
        static void Bulk(Call x)
        {
            var items = ShopJson.List(ShopJson.At(x.Body, "items"));
            if (items.Count == 0 || items.Count > MaxBulk) throw new ApiError(422, "bad_items", "Send items: a list of 1 to " + MaxBulk + " changes.", "ابعت items: من 1 لـ " + MaxBulk + " تغيير.");
            var d = ShopCache.Get(x.C, x.Sh); bool look = false; var res = new List<object>(); int good = 0;
            for (int i = 0; i < items.Count; i++)
            {
                try { var b = items[i] as Dictionary<string, object>; if (b == null) throw new ApiError(422, "bad_item", "Each item is an object.", "كل item لازم يكون object."); res.Add(Apply(x, d, ReadChange(b, i), ref look)); good++; }
                catch (ApiError ex) { res.Add(D("ok", false, "index", i, "error", D("code", ex.Code, "message", ex.Message, "message_ar", ex.Ar))); }
            }
            After(x, look);
            Ok(x, D("results", res, "done", good, "failed", items.Count - good));
        }

        // ---------- orders ----------
        public static Dictionary<string, object> OrderJson(ShopOrder o)
        {
            var rc = ShopJson.Sub(o.Extras, "recv"); var br = ShopJson.Sub(o.Extras, "branch");
            return D("id", o.Id, "number", o.No, "status", o.Status, "status_text", ShopOrders.StatusText(o.Status), "status_text_en", ShopOrders.StatusText(o.Status, true), "channel", o.Channel, "lang", o.Lang,
                "created_at", Iso(o.CreatedAt), "status_at", Iso(o.StatusAt), "confirmed_at", Iso(o.ConfirmedAt), "shipped_at", Iso(o.ShippedAt), "delivered_at", Iso(o.DeliveredAt),
                "customer", D("name", o.Name, "phone", ShopOrders.Local(o.Phone), "phone_intl", "+" + o.Phone, "phone2", o.Phone2 == null ? null : ShopOrders.Local(o.Phone2)),
                "shipping_address", D("governorate_id", o.Gov, "governorate", ShopGeo.Name(o.Gov), "governorate_en", ShopGeo.Name(o.Gov, true), "city", o.City ?? "", "area", o.Area ?? "", "address", o.Address, "landmark", o.Landmark ?? ""),
                "recipient", rc.Count == 0 ? null : D("name", ShopJson.Str(ShopJson.At(rc, "n")), "phone", ShopOrders.Local(ShopJson.Str(ShopJson.At(rc, "p")))), "pickup_branch", br.Count == 0 ? null : D("name", ShopJson.Str(ShopJson.At(br, "n")), "address", ShopJson.Str(ShopJson.At(br, "ad"))),
                "items", o.Items.Select(i => D("id", i.Id, "product_id", i.ProductId, "variant_id", i.VariantId, "sku", i.Sku ?? "", "name", i.Name, "options", i.Opts ?? "", "note", i.Extra ?? "", "quantity", i.Qty, "returned", i.Returned, "unit_price", i.Price, "full_price", i.Full, "discount", i.Discount, "total", i.Sum, "cost", i.Cost)).ToArray(),
                "currency", "EGP", "subtotal", o.Subtotal, "discount", o.Discount, "shipping", o.Ship, "fees", o.Fees, "total", o.Total, "paid", o.Paid, "due", o.Due, "refund", o.Refund,
                "payment", D("method", o.Pay, "state", o.PayState ?? "", "deposit", o.Deposit), "coupon", o.Coupon ?? "", "note", o.Note ?? "", "staff_note", o.StaffNote ?? "", "cancel_reason", o.CancelReason ?? "",
                "carrier", o.Carrier ?? "", "tracking", o.Tracking ?? "", "restocked", o.Restocked, "possible_duplicate", o.Dup);
        }
        static void Orders(Call x)
        {
            int page, per; Page(x, out page, out per); var since = When(x, "updated_since"); var from = When(x, "created_from"); var to = When(x, "created_to", true);
            var st = x.Q("status").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            var badSt = st.FirstOrDefault(s => !ShopOrders.Statuses.Contains(s)); if (badSt != null) throw new ApiError(400, "bad_status", "Unknown status «" + badSt + "». Statuses: " + string.Join(", ", ShopOrders.Statuses), "الحالة «" + badSt + "» مش معروفة.");
            string sort = x.Q("sort"); if (sort.Length > 0 && sort != "newest" && sort != "oldest") throw new ApiError(400, "bad_sort", "sort is newest or oldest.", "sort يا newest يا oldest.");
            string where = "o.StoreId=@s" + (st.Count > 0 ? " AND o.Status IN (" + string.Join(",", st.Select(s => "'" + s + "'")) + ")" : "") + (from.HasValue ? " AND o.CreatedAt>=@f" : "") + (to.HasValue ? " AND o.CreatedAt<@t" : "")
                + (since.HasValue ? " AND (o.CreatedAt>=@u OR o.StatusAt>=@u OR EXISTS (SELECT 1 FROM dbo.ShopOrderLog l WHERE l.OrderId=o.Id AND l.At>=@u))" : "");
            object[] a = { "@s", x.S.Id, "@f", from.HasValue ? (object)from.Value : DBNull.Value, "@t", to.HasValue ? (object)to.Value : DBNull.Value, "@u", since.HasValue ? (object)since.Value : DBNull.Value, "@o", (page - 1) * per, "@n", per };
            var now = Now(x.C); int total = Num(x.C, "SELECT COUNT(*) FROM dbo.ShopOrders o WHERE " + where, a); var ids = new List<long>();
            using (var cmd = Db.Cmd(x.C, "SELECT o.Id FROM dbo.ShopOrders o WHERE " + where + " ORDER BY o.Id " + (sort == "oldest" ? "ASC" : "DESC") + " OFFSET @o ROWS FETCH NEXT @n ROWS ONLY", a)) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Convert.ToInt64(r[0]));
            Ok(x, List(ids.Select(i => ShopOrders.ById(x.C, x.S.Id, i)).Where(o => o != null).Select(OrderJson).ToArray(), page, per, total, now));
        }
        static ShopOrder TheOrder(Call x) { var o = ShopOrders.ByNo(x.C, x.S.Id, ShopJson.Int(x.Arg["number"])); if (o == null) throw new ApiError(404, "order_not_found", "There is no order with this number in the store.", "مفيش أوردر بالرقم ده في المتجر."); return o; }
        static void Status(Call x)
        {
            var o = TheOrder(x); string to = ShopJson.Str(ShopJson.At(x.Body, "status"), 12), reason = ShopJson.Str(ShopJson.At(x.Body, "reason"), 200);
            if (!ShopOrders.Statuses.Contains(to)) throw new ApiError(422, "bad_status", "status is one of: " + string.Join(", ", ShopOrders.Statuses), "status لازم يكون واحد من: " + string.Join("، ", ShopOrders.Statuses), D("statuses", ShopOrders.Statuses));
            bool restock = ShopJson.Bool(ShopJson.At(x.Body, "restock"), true);
            // the tracking number goes first, so the customer's «اتشحن» message carries it
            if (x.Body.ContainsKey("tracking") || x.Body.ContainsKey("carrier"))
            {
                string tr = ShopJson.Str(ShopJson.At(x.Body, "tracking"), 60), ca = ShopJson.Str(ShopJson.At(x.Body, "carrier"), 30);
                Exec(x.C, null, "UPDATE dbo.ShopOrders SET Tracking=CASE WHEN @ht=1 THEN @t ELSE Tracking END, Carrier=CASE WHEN @hc=1 THEN @c ELSE Carrier END WHERE Id=@i AND StoreId=@s", "@ht", x.Body.ContainsKey("tracking"), "@t", Nz(tr), "@hc", x.Body.ContainsKey("carrier"), "@c", Nz(ca), "@i", o.Id, "@s", x.S.Id);
                if (tr.Length > 0 && tr != (o.Tracking ?? "")) ShopOrders.Log_(x.C, null, x.S.Id, o.Id, x.Who, "رقم التتبع: " + tr + (ca.Length > 0 ? " (" + ca + ")" : ""));
            }
            string from = o.Status, e = ShopOrders.SetStatus(x.C, x.S.Id, o.Id, to, x.Who, reason, restock);
            if (e != null) throw new ApiError(409, "status_refused", "The status didn't change: see message_ar.", e);
            if (from != to) Log_(x.C, x.S.Id, x.Key, "حالة أوردر " + o.NoText + ": " + ShopOrders.StatusText(from) + " ← " + ShopOrders.StatusText(to));
            Ok(x, D("data", OrderJson(ShopOrders.ById(x.C, x.S.Id, o.Id)), "changed", from != to));
        }

        // ---------- customers: the people who ordered, by their number ----------
        static Dictionary<string, object> CustJson(SqlDataReader r)
        {
            var ph = (string)r["Phone"];
            return D("phone", ShopOrders.Local(ph), "phone_intl", "+" + ph, "name", r["Name"] as string ?? "", "governorate_id", (int)r["Gov"], "governorate", ShopGeo.Name((int)r["Gov"]), "orders", (int)r["N"], "delivered", (int)r["Got"], "returned", (int)r["Back"], "cancelled", (int)r["Gone"],
                "spent", r["Spent"] is decimal ? (decimal)r["Spent"] : 0m, "first_order_at", Iso(Dt(r["F"])), "last_order_at", Iso(Dt(r["L"])), "updated_at", Iso(Dt(r["U"])), "has_account", r["Proved"] is DateTime);
        }
        const string CustSql = @"SELECT o.Phone, MAX(o.Name) AS Name, MAX(o.Gov) AS Gov, COUNT(*) AS N, SUM(CASE WHEN o.Status='delivered' THEN 1 ELSE 0 END) AS Got, SUM(CASE WHEN o.Status='returned' THEN 1 ELSE 0 END) AS Back, SUM(CASE WHEN o.Status='cancelled' THEN 1 ELSE 0 END) AS Gone,
            SUM(CASE WHEN o.Status='delivered' THEN o.Total-o.Refund ELSE 0 END) AS Spent, MIN(o.CreatedAt) AS F, MAX(o.CreatedAt) AS L, MAX(o.StatusAt) AS U, MAX(k.ProvedAt) AS Proved
            FROM dbo.ShopOrders o LEFT JOIN dbo.ShopCustomers k ON k.StoreId=o.StoreId AND k.Phone=o.Phone WHERE o.StoreId=@s AND o.Phone<>'' ";
        static void Customers(Call x)
        {
            int page, per; Page(x, out page, out per); var since = When(x, "updated_since"); var now = Now(x.C);
            string having = since.HasValue ? " HAVING MAX(o.StatusAt)>=@u OR MAX(o.CreatedAt)>=@u" : ""; object[] a = { "@s", x.S.Id, "@u", since.HasValue ? (object)since.Value : DBNull.Value, "@o", (page - 1) * per, "@n", per };
            int total = Num(x.C, "SELECT COUNT(*) FROM (SELECT o.Phone FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.Phone<>'' GROUP BY o.Phone" + having + ") z", a);
            var l = new List<object>(); using (var cmd = Db.Cmd(x.C, CustSql + "GROUP BY o.Phone" + having + " ORDER BY MIN(o.Id) OFFSET @o ROWS FETCH NEXT @n ROWS ONLY", a)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(CustJson(r));
            Ok(x, List(l, page, per, total, now));
        }
        static void Customer(Call x)
        {
            var ph = ShopOrders.Mobile(x.Arg["phone"]); if (ph == null) throw new ApiError(400, "bad_phone", "Write an Egyptian mobile number, like 01012345678.", "اكتب رقم موبايل مصري زي 01012345678.");
            Dictionary<string, object> one = null; using (var cmd = Db.Cmd(x.C, CustSql + "AND o.Phone=@p GROUP BY o.Phone", "@s", x.S.Id, "@p", ph)) using (var r = cmd.ExecuteReader()) if (r.Read()) one = CustJson(r);
            if (one == null) throw new ApiError(404, "customer_not_found", "No order has this number.", "مفيش أوردر بالرقم ده.");
            var nos = new List<object>(); using (var cmd = Db.Cmd(x.C, "SELECT TOP 100 No, Status, Total, CreatedAt FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p ORDER BY Id DESC", "@s", x.S.Id, "@p", ph)) using (var r = cmd.ExecuteReader()) while (r.Read()) nos.Add(D("number", (int)r[0], "status", (string)r[1], "total", (decimal)r[2], "created_at", Iso(Dt(r[3]))));
            one["recent_orders"] = nos; Ok(x, D("data", one));
        }

        // ---------- the list: the handler and the documentation page read it ----------
        static readonly string[] PageP = { "page", "رقم الصفحة، من 1", "Page number, from 1" }, PerP = { "per_page", "كام واحد في الصفحة، من 1 لـ 200 (50 لو ماكتبتش)", "How many per page, 1 to 200 (50 if not given)" };
        static readonly string[] SinceP = { "updated_since", "اللي اتغيّر بعد الوقت ده بس. ابعت server_time من آخر رد", "Only what changed after this time. Send server_time from your last answer" };
        static readonly string ChangeDoc = "stock | stock_change | price | old_price | shown";
        public static readonly Route[] Routes = {
            new Route { Method = "GET", Path = "/me", Scope = null, Ar = "اتأكد إن المفتاح شغال: اسم المتجر واسم المفتاح ويقدر يعمل إيه.", En = "Check the key: the store, the key's name and what it may do.", Params = new string[0][],
                Run = x => Ok(x, D("store", D("name", x.Sh.Name, "slug", x.Sh.Slug, "url", x.Sh.Url), "key", D("name", x.Key.Name, "last4", x.Key.Last4, "scopes", x.Key.Scopes.Split(',')), "server_time", Iso(Now(x.C)))) },
            new Route { Method = "GET", Path = "/products", Scope = "products:read", Ar = "المنتجات بمقاساتها وكمياتها وأسعارها، صفحة صفحة.", En = "Products with their sizes, stock and prices, page by page.",
                Params = new[] { PageP, PerP, SinceP, new[] { "sku", "منتج أو مقاس بالـ SKU ده", "A product or size with this SKU" }, new[] { "status", "active (ظاهر) أو hidden (مخفي)", "active (shown) or hidden" } }, Run = Products,
                Sample = "GET /api/v1/products?updated_since=2026-11-26T09:30:00Z" },
            new Route { Method = "GET", Path = "/products/{id}", Scope = "products:read", Ar = "منتج واحد برقمه.", En = "One product by its id.", Params = new string[0][], Run = Product },
            new Route { Method = "PATCH", Path = "/products/{id}", Scope = "products:write", Ar = "غيّر منتج: السعر أو السعر قبل الخصم أو يظهر ولا لأ، والكمية لو المنتج من غير مقاسات.", En = "Change a product: price, old price, shown, and stock when it has no sizes.",
                Params = new[] { new[] { ChangeDoc, "اللي عايز تغيّره بس", "Only what you want to change" } }, Run = x => Patch(x, With(x, "product_id", ShopJson.Int(x.Arg["id"]))), Sample = "{ \"price\": 450, \"old_price\": 600, \"shown\": true }" },
            new Route { Method = "PATCH", Path = "/variants/{id}", Scope = "products:write", Ar = "غيّر مقاس (لون أو حجم): الكمية أو السعر أو يتباع ولا لأ.", En = "Change a size / variant: stock, price, or whether it's sold.",
                Params = new[] { new[] { ChangeDoc, "stock = الكمية زي ما هي على الرف، stock_change = زوّد أو قلّل (زي -2)", "stock = the count on the shelf, stock_change = add or take (like -2)" } }, Run = x => Patch(x, With(x, "variant_id", ShopJson.Int(x.Arg["id"]))), Sample = "{ \"stock\": 12 }" },
            new Route { Method = "PATCH", Path = "/variants/sku/{sku}", Scope = "products:write", Ar = "نفس اللي فوق بالـ SKU.", En = "The same, by SKU.", Params = new[] { new[] { ChangeDoc, "اللي عايز تغيّره بس", "Only what you want to change" } }, Run = x => Patch(x, With(x, "sku", x.Arg["sku"])), Sample = "{ \"stock_change\": -2 }" },
            new Route { Method = "POST", Path = "/products/bulk", Scope = "products:write", Ar = "تغييرات كتير مرة واحدة (لحد 200). كل واحد بـ product_id أو variant_id أو sku، والرد فيه نتيجة كل واحد.", En = "Many changes at once (up to 200). Each by product_id, variant_id or sku; the answer has each one's result.",
                Params = new[] { new[] { "items", "قايمة التغييرات", "The list of changes" } }, Run = Bulk, Sample = "{ \"items\": [ { \"sku\": \"ABY-M\", \"stock\": 7 }, { \"variant_id\": 55, \"stock_change\": -1 }, { \"product_id\": 9, \"price\": 380 } ] }" },
            new Route { Method = "GET", Path = "/orders", Scope = "orders:read", Ar = "الأوردرات، الأحدث الأول.", En = "Orders, newest first.",
                Params = new[] { PageP, PerP, SinceP, new[] { "status", "حالة أو أكتر بفاصلة: " + string.Join(", ", ShopOrders.Statuses), "One status or more, with commas: " + string.Join(", ", ShopOrders.Statuses) }, new[] { "created_from", "من يوم أو وقت", "From a day or a time" }, new[] { "created_to", "لحد يوم أو وقت", "Up to a day or a time" }, new[] { "sort", "newest أو oldest (للمزامنة استخدم oldest)", "newest or oldest (use oldest to sync)" } }, Run = Orders,
                Sample = "GET /api/v1/orders?status=confirmed&created_from=2026-11-01" },
            new Route { Method = "GET", Path = "/orders/{number}", Scope = "orders:read", Ar = "أوردر واحد برقمه اللي الزبون شايفه.", En = "One order by the number the customer sees.", Params = new string[0][], Run = x => Ok(x, D("data", OrderJson(TheOrder(x)))) },
            new Route { Method = "POST", Path = "/orders/{number}/status", Scope = "orders:write", Ar = "غيّر حالة الأوردر زي ما بتغيّرها من «متجري»: الزبون بتوصله رسالته على الواتساب، والكمية بترجع لو اتلغى أو رجع.", En = "Change an order's status as «متجري» does: the customer gets their WhatsApp message, and the stock comes back when it's cancelled or returned.",
                Params = new[] { new[] { "status", string.Join(", ", ShopOrders.Statuses), string.Join(", ", ShopOrders.Statuses) }, new[] { "reason", "السبب (للإلغاء والمرتجع)", "The reason (for cancelled and returned)" }, new[] { "restock", "true (الكمية ترجع، لو ماكتبتش) أو false", "true (stock comes back, the default) or false" }, new[] { "tracking, carrier", "رقم التتبع وشركة الشحن، قبل «shipped»", "The tracking number and carrier, before «shipped»" } }, Run = Status,
                Sample = "{ \"status\": \"shipped\", \"tracking\": \"7788123\", \"carrier\": \"Bosta\" }" },
            new Route { Method = "GET", Path = "/customers", Scope = "customers:read", Ar = "الزباين اللي طلبوا، برقمهم: عدد أوردراتهم واشتروا بكام.", En = "The customers who ordered, by their number: how many orders and how much they bought.", Params = new[] { PageP, PerP, SinceP }, Run = Customers },
            new Route { Method = "GET", Path = "/customers/{phone}", Scope = "customers:read", Ar = "زبون واحد برقم موبايله، وآخر أوردراته.", En = "One customer by their mobile number, with their latest orders.", Params = new string[0][], Run = Customer },
        };

        // the errors the API gives (the docs page lists them)
        public static readonly string[][] Errors = {
            new[] { "400", "bad_json, bad_date, bad_page, bad_per_page, bad_status, bad_sort, bad_phone", "الطلب نفسه مش مظبوط", "The request itself isn't right" },
            new[] { "401", "no_key, bad_key, key_stopped", "مفيش مفتاح، أو غلط، أو اتوقّف", "No key, a wrong one, or a stopped one" },
            new[] { "403", "forbidden, api_off, not_open, store_inactive, https_required", "المفتاح مش مسموحله، أو الربط مقفول", "The key may not do this, or the API is off" },
            new[] { "404", "not_found, product_not_found, variant_not_found, sku_not_found, order_not_found, customer_not_found", "مش موجود", "Not there" },
            new[] { "405", "method_not_allowed", "استخدم طريقة تانية (GET / POST / PATCH)", "Use another method" },
            new[] { "409", "sku_not_unique, status_refused", "مش هينفع دلوقتي (السبب في message_ar)", "Can't be done now (the reason is in message_ar)" },
            new[] { "422", "which, nothing, bad_stock, bad_stock_change, stock_both, bad_price, bad_old_price, old_price_low, bad_shown, has_sizes, stock_not_tracked, bad_items", "القيم اللي اتبعتت مش مظبوطة", "The values sent aren't right" },
            new[] { "429", "rate_limited, too_many_bad_keys", "طلبات كتير، استنى دقيقة", "Too many requests, wait a minute" },
            new[] { "500 / 503", "server_error, unavailable", "مشكلة عندنا، جرّب تاني", "A problem on our side, try again" } };
    }
}
