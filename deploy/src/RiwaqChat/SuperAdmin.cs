// Plans (with discounts), manual subscriptions (no online payment), support tickets and the super admin console API.
//   api/superadmin.ashx  the super admin console (own login: user + password, separate from store owners)
//   api/plans.ashx       public: the active plans for the pricing page and the store dashboard
//   api/support.ashx     store owners: their support tickets
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace RiwaqChat
{
    public class PlanInfo
    {
        public int Id, TokenLimit, DurationDays, SortOrder;
        public string Name, Description, Features, DiscountType, DiscountLabel;
        public decimal PriceEgp, UsdBudget;
        public decimal? DiscountValue;
        public DateTime? DiscountEndsAt;
        public bool IsActive, IsFeatured;
    }

    public static class Billing
    {
        // One cost-weighted unit = one Haiku 5.5 input token ($0.10 per million): 1$ = 10,000,000 units.
        public const decimal UsdPerMillionUnits = 0.10m;
        public static long UnitsForUsd(decimal usd) { return (long)Math.Round(usd / UsdPerMillionUnits * 1000000m); }
        public static decimal UsdForUnits(long units) { return units / 1000000m * UsdPerMillionUnits; }

        // ---------- settings kept in the database (editable from the console) ----------
        static Dictionary<string, string> cache;
        static DateTime cachedAt = DateTime.MinValue;
        static readonly object Lock = new object();
        public static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>
        {
            { "UsdRate", "50" }, { "WhatsApp", "01229391155" },
            { "UnitsSmall", "3700" }, { "UnitsMedium", "7000" }, { "UnitsLarge", "26000" },
            // discount % for paying several months at once ("months:percent")
            { "PeriodDiscounts", "4:5,6:10,12:20" },
            // shown to store owners in the plan request form
            { "PaymentInfo", "حوّل المبلغ على InstaPay أو فودافون كاش على رقم الواتساب بتاعنا، وبعدين اكتب رقم العملية (أو الرقم اللي حوّلت منه) في الطلب." },
        };

        public static string Get(SqlConnection c, string key)
        {
            lock (Lock)
            {
                if (cache == null || DateTime.UtcNow - cachedAt > TimeSpan.FromSeconds(30))
                {
                    var d = new Dictionary<string, string>();
                    using (var cmd = Db.Cmd(c, "SELECT [Key], [Value] FROM dbo.AppSettings"))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) d[(string)r[0]] = r[1] as string;
                    cache = d; cachedAt = DateTime.UtcNow;
                }
                string v;
                if (cache.TryGetValue(key, out v) && !string.IsNullOrWhiteSpace(v)) return v;
                return Defaults.TryGetValue(key, out v) ? v : null;
            }
        }

        public static void Set(SqlConnection c, string key, string value)
        {
            using (var cmd = Db.Cmd(c, @"MERGE dbo.AppSettings AS t USING (SELECT @k AS [Key]) AS s ON t.[Key] = s.[Key]
                                         WHEN MATCHED THEN UPDATE SET [Value] = @v WHEN NOT MATCHED THEN INSERT ([Key], [Value]) VALUES (@k, @v);", "@k", key, "@v", value))
                cmd.ExecuteNonQuery();
            lock (Lock) cache = null;
        }

        public static decimal Dec(string s, decimal fallback)
        {
            decimal d;
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }
        public static decimal UsdRate(SqlConnection c) { return Dec(Get(c, "UsdRate"), 50); }
        public static int Units(SqlConnection c, string key) { int v; return int.TryParse(Get(c, key), out v) && v > 0 ? v : int.Parse(Defaults[key]); }
        public static string WhatsApp(SqlConnection c) { return Get(c, "WhatsApp"); }

        // 01229391155 -> 201229391155 (wa.me needs the country code)
        public static string WaNumber(string local)
        {
            var d = new string((local ?? "").Where(char.IsDigit).ToArray());
            if (d.StartsWith("00")) d = d.Substring(2);
            if (d.Length == 11 && d.StartsWith("01")) d = "2" + d;
            return d;
        }

        // ---------- plans ----------
        public static List<PlanInfo> Plans(SqlConnection c, bool includeInactive)
        {
            var list = new List<PlanInfo>();
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.Plans WHERE IsDeleted = 0" + (includeInactive ? "" : " AND IsActive = 1") + " ORDER BY SortOrder, PriceEgp"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(ReadPlan(r));
            return list;
        }

        public static PlanInfo Plan(SqlConnection c, int id)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.Plans WHERE Id = @id AND IsDeleted = 0", "@id", id))
            using (var r = cmd.ExecuteReader()) return r.Read() ? ReadPlan(r) : null;
        }

        static PlanInfo ReadPlan(SqlDataReader r)
        {
            return new PlanInfo
            {
                Id = (int)r["Id"], Name = (string)r["Name"], Description = r["Description"] as string, Features = r["Features"] as string,
                PriceEgp = (decimal)r["PriceEgp"], UsdBudget = (decimal)r["UsdBudget"], TokenLimit = (int)r["TokenLimit"], DurationDays = (int)r["DurationDays"],
                IsActive = (bool)r["IsActive"], IsFeatured = (bool)r["IsFeatured"], SortOrder = (int)r["SortOrder"],
                DiscountType = r["DiscountType"] as string, DiscountValue = r["DiscountValue"] == DBNull.Value ? (decimal?)null : (decimal)r["DiscountValue"],
                DiscountEndsAt = r["DiscountEndsAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["DiscountEndsAt"], DateTimeKind.Utc),
                DiscountLabel = r["DiscountLabel"] as string,
            };
        }

        public static bool DiscountActive(PlanInfo p)
        {
            return p.DiscountValue.HasValue && p.DiscountValue.Value > 0 && (p.DiscountType == "percent" || p.DiscountType == "amount")
                && (!p.DiscountEndsAt.HasValue || p.DiscountEndsAt.Value > DateTime.UtcNow);
        }

        public static decimal FinalPrice(PlanInfo p)
        {
            if (!DiscountActive(p)) return p.PriceEgp;
            var v = p.DiscountValue.Value;
            var f = p.DiscountType == "percent" ? p.PriceEgp * (1 - Math.Min(100, v) / 100m) : p.PriceEgp - v;
            return Math.Max(0, Math.Round(f, 0));
        }

        public static Dictionary<string, object> PlanJson(SqlConnection c, PlanInfo p, bool forAdmin)
        {
            int small = Units(c, "UnitsSmall"), medium = Units(c, "UnitsMedium"), large = Units(c, "UnitsLarge");
            var final = FinalPrice(p);
            var d = new Dictionary<string, object>
            {
                { "id", p.Id }, { "name", p.Name }, { "description", p.Description ?? "" },
                { "features", (p.Features ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray() },
                { "priceEgp", p.PriceEgp }, { "finalPrice", final }, { "durationDays", p.DurationDays }, { "isFeatured", p.IsFeatured },
                { "discount", DiscountActive(p) ? new Dictionary<string, object> {
                    { "type", p.DiscountType }, { "value", p.DiscountValue.Value }, { "label", p.DiscountLabel ?? "" },
                    { "endsAt", p.DiscountEndsAt.HasValue ? p.DiscountEndsAt.Value.ToString("o") : "" } } : null },
                { "messages", new Dictionary<string, object> { { "small", p.TokenLimit / small }, { "medium", p.TokenLimit / medium }, { "large", p.TokenLimit / large } } },
                { "periods", QuotesJson(c, p) },
            };
            if (forAdmin)
            {
                var rate = UsdRate(c);
                var cost = Math.Round(p.UsdBudget * rate, 0);
                d["usdBudget"] = p.UsdBudget; d["tokenLimit"] = p.TokenLimit; d["isActive"] = p.IsActive; d["sortOrder"] = p.SortOrder;
                d["maxCostEgp"] = cost; d["minProfitEgp"] = final - cost;
                d["discountRaw"] = new Dictionary<string, object> {
                    { "type", p.DiscountType ?? "" }, { "value", p.DiscountValue.HasValue ? (object)p.DiscountValue.Value : "" }, { "label", p.DiscountLabel ?? "" },
                    { "endsAt", p.DiscountEndsAt.HasValue ? p.DiscountEndsAt.Value.ToString("yyyy-MM-dd") : "" } };
            }
            return d;
        }

        // ---------- paying for several months at once: 1, 4, 6 or 12 months, with a discount per period ----------
        public static readonly int[] Periods = { 1, 4, 6, 12 };

        public static Dictionary<int, decimal> PeriodDiscounts(SqlConnection c)
        {
            var d = Periods.ToDictionary(m => m, m => 0m);
            foreach (var part in (Get(c, "PeriodDiscounts") ?? "").Split(','))
            {
                var kv = part.Split(':');
                int m; decimal pct;
                if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out m) && d.ContainsKey(m) && m > 1
                    && decimal.TryParse(kv[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out pct) && pct >= 0 && pct < 90) d[m] = pct;
            }
            return d;
        }

        public class Quote { public int Months; public decimal Price, Discount, Total, Pct; }

        // Price for N months: the plan's current monthly price (after its own discount) x N, minus the period discount.
        public static Quote QuoteFor(SqlConnection c, PlanInfo p, int months)
        {
            decimal pct; PeriodDiscounts(c).TryGetValue(months, out pct);
            decimal list = p.PriceEgp * months;
            decimal total = Math.Max(0, Math.Round(FinalPrice(p) * months * (1 - pct / 100m), 0));
            return new Quote { Months = months, Price = list, Discount = Math.Max(0, list - total), Total = total, Pct = pct };
        }

        public static List<Dictionary<string, object>> QuotesJson(SqlConnection c, PlanInfo p)
        {
            return Periods.Select(m => { var q = QuoteFor(c, p, m); return new Dictionary<string, object> {
                { "months", m }, { "total", q.Total }, { "perMonth", Math.Round(q.Total / m, 0) }, { "pct", q.Pct }, { "price", q.Price }, { "save", q.Discount } }; }).ToList();
        }

        // Starts (or extends) a subscription: history row + the store's plan, end date and monthly limit. Returns the new end date.
        public static DateTime Activate(SqlConnection c, StoreInfo s, PlanInfo p, int months, decimal price, decimal paid, string method, string notes, out int subscriptionId)
        {
            var now = DateTime.UtcNow;
            // same plan still running: the new time starts after it; otherwise from today
            var start = (s.PlanId == p.Id && s.PlanEndsAt.HasValue && s.PlanEndsAt.Value > now) ? s.PlanEndsAt.Value : now;
            var end = start.AddDays(p.DurationDays * months);
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Subscriptions(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, PaidEgp, PaymentMethod, Notes, StartsAt, EndsAt)
                                             OUTPUT INSERTED.Id VALUES (@s, @p, @n, @m, @price, @disc, @paid, @method, @notes, @start, @end)",
                    "@s", s.Id, "@p", p.Id, "@n", p.Name, "@m", months, "@price", price, "@disc", Math.Max(0, price - paid), "@paid", paid,
                    "@method", (object)method ?? DBNull.Value, "@notes", (object)notes ?? DBNull.Value, "@start", start, "@end", end))
                { cmd.Transaction = tx; subscriptionId = (int)cmd.ExecuteScalar(); }
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET PlanId=@p, PlanName=@n, PlanEndsAt=@end, MonthlyTokenLimit=@l, Status='active' WHERE Id=@s",
                    "@p", p.Id, "@n", p.Name, "@end", end, "@l", p.TokenLimit, "@s", s.Id))
                { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            return end;
        }

        public static Dictionary<string, object> RequestJson(SqlDataReader r)
        {
            return new Dictionary<string, object>
            {
                { "id", r["Id"] }, { "storeId", r["StoreId"] }, { "planId", r["PlanId"] }, { "plan", r["PlanName"] }, { "months", r["Months"] },
                { "price", r["PriceEgp"] }, { "discount", r["DiscountEgp"] }, { "amount", r["AmountEgp"] },
                { "method", r["PaymentMethod"] as string ?? "" }, { "ref", r["PaymentRef"] as string ?? "" }, { "note", r["Note"] as string ?? "" },
                { "status", r["Status"] }, { "adminNote", r["AdminNote"] as string ?? "" },
                { "at", Iso(r["CreatedAt"]) }, { "decidedAt", Iso(r["DecidedAt"]) },
            };
        }

        // ---------- what a store may use ----------
        public static bool Expired(StoreInfo s) { return s.PlanId.HasValue && s.PlanEndsAt.HasValue && s.PlanEndsAt.Value < DateTime.UtcNow; }
        public static int EffectiveLimit(StoreInfo s) { return Expired(s) ? 0 : s.MonthlyTokenLimit; }

        public static Dictionary<string, object> StorePlan(StoreInfo s)
        {
            if (!s.PlanId.HasValue) return new Dictionary<string, object> { { "name", null } };
            int days = s.PlanEndsAt.HasValue ? (int)Math.Ceiling((s.PlanEndsAt.Value - DateTime.UtcNow).TotalDays) : 0;
            return new Dictionary<string, object>
            {
                { "id", s.PlanId.Value }, { "name", s.PlanName ?? "" }, { "endsAt", s.PlanEndsAt.HasValue ? s.PlanEndsAt.Value.ToString("o") : "" },
                { "daysLeft", Math.Max(0, days) }, { "expired", Expired(s) },
            };
        }

        public static string Iso(object v) { return v == null || v == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc).ToString("o"); }
    }

    // ---------- super admin login cookie (separate from store owners' cookie and signing key) ----------
    static class AdminAuth
    {
        const string Cookie = "sb_admin";
        static string Sign(string payload)
        {
            var s = Web.Setting("AuthSecret");
            if (s == null || s.Length < 32) throw new InvalidOperationException("AuthSecret is not configured");
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes("superadmin|" + s))) return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        }

        public static void SignIn(HttpContext ctx, int id)
        {
            long exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
            string payload = id + "|" + exp;
            var c = new HttpCookie(Cookie, payload + "|" + Sign(payload)) { HttpOnly = true, Secure = ctx.Request.IsSecureConnection, Path = "/", Expires = DateTime.UtcNow.AddHours(12) };
            c.SameSite = SameSiteMode.Strict;
            ctx.Response.Cookies.Add(c);
        }

        public static void SignOut(HttpContext ctx) { ctx.Response.Cookies.Add(new HttpCookie(Cookie, "") { HttpOnly = true, Path = "/", Expires = DateTime.UtcNow.AddDays(-1) }); }

        public static int? Current(HttpContext ctx)
        {
            var c = ctx.Request.Cookies[Cookie];
            if (c == null || string.IsNullOrEmpty(c.Value)) return null;
            var p = c.Value.Split('|');
            if (p.Length != 3) return null;
            var expected = Sign(p[0] + "|" + p[1]);
            if (expected.Length != p[2].Length) return null;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ p[2][i];
            long exp; int id;
            if (diff != 0 || !long.TryParse(p[1], out exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || !int.TryParse(p[0], out id)) return null;
            return id;
        }
    }

    // api/superadmin.ashx?action=...
    public class SuperAdminHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
            bool post = req.HttpMethod == "POST";
            if (post && !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var b = post ? Web.Body(req, 30000) : new Dictionary<string, object>();

                    if (action == "login" && post)
                    {
                        if (!Web.Allow("sa-login:" + Web.Ip(req), 8, TimeSpan.FromMinutes(15))) { Web.Error(res, 429, "rate_limited", "محاولات كتير، استنى ربع ساعة وجرّب تاني."); return; }
                        var user = (Web.Str(b, "username", 50) ?? "").ToLowerInvariant();
                        var pass = Web.Str(b, "password", 200) ?? "";
                        int id = 0; string hash = null;
                        using (var cmd = Db.Cmd(c, "SELECT Id, PasswordHash FROM dbo.Admins WHERE Username = @u", "@u", user))
                        using (var r = cmd.ExecuteReader()) if (r.Read()) { id = (int)r[0]; hash = (string)r[1]; }
                        if (hash == null || !Web.CheckPassword(pass, hash)) { Web.Error(res, 401, "bad_login", "اسم المستخدم أو الباسورد غلط."); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Admins SET LastLoginAt = SYSUTCDATETIME() WHERE Id = @id", "@id", id)) cmd.ExecuteNonQuery();
                        AdminAuth.SignIn(ctx, id);
                        bool isDefault;
                        using (var cmd = Db.Cmd(c, "SELECT DefaultPassword FROM dbo.Admins WHERE Id = @id", "@id", id)) isDefault = (bool)cmd.ExecuteScalar();
                        Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "username", user }, { "defaultPassword", isDefault } });
                        return;
                    }
                    if (action == "logout" && post) { AdminAuth.SignOut(ctx); Web.Json(res, 200, new { ok = true }); return; }

                    // ---- everything below needs a signed-in super admin ----
                    var adminId = AdminAuth.Current(ctx);
                    string adminName = null; bool defaultPassword = false;
                    if (adminId.HasValue)
                        using (var cmd = Db.Cmd(c, "SELECT Username, DefaultPassword FROM dbo.Admins WHERE Id = @id", "@id", adminId.Value))
                        using (var r = cmd.ExecuteReader()) if (r.Read()) { adminName = (string)r[0]; defaultPassword = (bool)r[1]; }
                    if (adminName == null) { Web.Error(res, 401, "not_logged_in"); return; }

                    switch (action)
                    {
                        case "me": Web.Json(res, 200, new Dictionary<string, object> { { "username", adminName }, { "defaultPassword", defaultPassword } }); return;
                        case "overview": Web.Json(res, 200, Overview(c)); return;
                        case "plans": Web.Json(res, 200, new Dictionary<string, object> { { "plans", Billing.Plans(c, true).Select(p => Billing.PlanJson(c, p, true)).ToList() }, { "usdRate", Billing.UsdRate(c) } }); return;
                        case "plan-save": if (!post) break; PlanSave(c, b, res); return;
                        case "plan-delete": if (!post) break; PlanDelete(c, b, res); return;
                        case "stores": Web.Json(res, 200, Stores(c)); return;
                        case "store": StoreDetail(c, req, res); return;
                        case "store-create": if (!post) break; StoreCreate(c, b, res); return;
                        case "store-update": if (!post) break; StoreUpdate(c, b, res); return;
                        case "store-password": if (!post) break; StorePassword(c, b, res); return;
                        case "store-delete": if (!post) break; StoreDelete(c, b, res); return;
                        case "store-login": if (!post) break; StoreLogin(ctx, c, b, res); return;
                        case "subscribe": if (!post) break; Subscribe(c, b, res); return;
                        case "subscription-cancel": if (!post) break; SubscriptionCancel(c, b, res); return;
                        case "subscription-delete": if (!post) break; SubscriptionDelete(c, b, res); return;
                        case "subscriptions": Web.Json(res, 200, Subscriptions(c)); return;
                        case "requests": Web.Json(res, 200, Requests(c, req.QueryString["status"])); return;
                        case "request-approve": if (!post) break; RequestDecide(c, b, res, true); return;
                        case "request-reject": if (!post) break; RequestDecide(c, b, res, false); return;
                        case "tickets": Web.Json(res, 200, Tickets(c, req.QueryString["status"])); return;
                        case "ticket": TicketDetail(c, req, res); return;
                        case "ticket-reply": if (!post) break; TicketReply(c, b, res); return;
                        case "ticket-status": if (!post) break; TicketStatus(c, b, res); return;
                        case "settings": if (post) SettingsSave(c, b, res); else Web.Json(res, 200, SettingsGet(c)); return;
                        case "password": if (!post) break; ChangePassword(c, adminId.Value, b, res); return;
                    }
                    Web.Error(res, 404, "unknown_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) { Web.Error(res, 409, "taken", "المعرّف أو الإيميل مستخدم بالفعل."); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static decimal? DecOf(Dictionary<string, object> b, string key)
        {
            object v;
            if (!b.TryGetValue(key, out v) || v == null || Convert.ToString(v).Trim() == "") return null;
            decimal d;
            return decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : (decimal?)null;
        }
        static bool BoolOf(Dictionary<string, object> b, string key) { object v; return b.TryGetValue(key, out v) && v is bool && (bool)v; }
        static int FreeLimit() { int v; return int.TryParse(Web.Setting("FreeMonthlyTokenLimit", "300000"), out v) ? v : 300000; }
        static string MonthStart() { var n = DateTime.UtcNow; return new DateTime(n.Year, n.Month, 1).ToString("yyyy-MM-dd"); }

        // ---------- overview ----------
        static Dictionary<string, object> Overview(SqlConnection c)
        {
            var rate = Billing.UsdRate(c);
            var now = DateTime.UtcNow;
            Func<string, object[], object> scalar = (sql, ps) => { using (var cmd = Db.Cmd(c, sql, ps)) return cmd.ExecuteScalar(); };
            int stores = Convert.ToInt32(scalar("SELECT COUNT(*) FROM dbo.Stores", new object[0]));
            int newStores = Convert.ToInt32(scalar("SELECT COUNT(*) FROM dbo.Stores WHERE CreatedAt >= @m", new object[] { "@m", MonthStart() }));
            int active = Convert.ToInt32(scalar("SELECT COUNT(*) FROM dbo.Stores WHERE PlanId IS NOT NULL AND PlanEndsAt >= SYSUTCDATETIME()", new object[0]));
            int expiring = Convert.ToInt32(scalar("SELECT COUNT(*) FROM dbo.Stores WHERE PlanId IS NOT NULL AND PlanEndsAt >= SYSUTCDATETIME() AND PlanEndsAt < DATEADD(day, 7, SYSUTCDATETIME())", new object[0]));
            int expired = Convert.ToInt32(scalar("SELECT COUNT(*) FROM dbo.Stores WHERE PlanId IS NOT NULL AND PlanEndsAt < SYSUTCDATETIME()", new object[0]));
            decimal revMonth = Convert.ToDecimal(scalar("SELECT ISNULL(SUM(PaidEgp),0) FROM dbo.Subscriptions WHERE CreatedAt >= @m", new object[] { "@m", MonthStart() }));
            decimal revTotal = Convert.ToDecimal(scalar("SELECT ISNULL(SUM(PaidEgp),0) FROM dbo.Subscriptions", new object[0]));
            int openTickets = Convert.ToInt32(scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE Status = 'open'", new object[0]));

            // usage per month (last 6) -> cost; revenue per month
            var months = Enumerable.Range(0, 6).Select(i => now.AddMonths(-5 + i).ToString("yyyy-MM")).ToList();
            var usage = months.ToDictionary(m => m, m => new UsageInfo());
            using (var cmd = Db.Cmd(c, @"SELECT Month, SUM(Messages), SUM(InputTokens), SUM(CacheReadTokens), SUM(CacheWriteTokens), SUM(OutputTokens)
                                         FROM dbo.UsageMonthly WHERE Month >= @from GROUP BY Month", "@from", months[0]))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var m = (string)r[0];
                    if (usage.ContainsKey(m)) usage[m] = new UsageInfo { Messages = Convert.ToInt32(r[1]), InputTokens = Convert.ToInt64(r[2]), CacheReadTokens = Convert.ToInt64(r[3]), CacheWriteTokens = Convert.ToInt64(r[4]), OutputTokens = Convert.ToInt64(r[5]) };
                }
            var revenue = months.ToDictionary(m => m, m => 0m);
            using (var cmd = Db.Cmd(c, "SELECT CONVERT(CHAR(7), CreatedAt, 126), SUM(PaidEgp) FROM dbo.Subscriptions WHERE CreatedAt >= @from GROUP BY CONVERT(CHAR(7), CreatedAt, 126)", "@from", months[0] + "-01"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) { var m = (string)r[0]; if (revenue.ContainsKey(m)) revenue[m] = Convert.ToDecimal(r[1]); }
            var series = months.Select(m => new Dictionary<string, object>
            {
                { "month", m }, { "revenue", revenue[m] }, { "messages", usage[m].Messages },
                { "costEgp", Math.Round(Billing.UsdForUnits(usage[m].Weighted) * rate, 2) },
            }).ToList();
            var thisMonth = usage[months[5]];
            var costUsd = Billing.UsdForUnits(thisMonth.Weighted);

            var top = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 6 s.Id, s.Name, s.Slug, s.PlanName, s.MonthlyTokenLimit, u.Messages,
                                           u.InputTokens + CAST(u.CacheWriteTokens * 1.25 AS BIGINT) + CAST(u.CacheReadTokens * 0.1 AS BIGINT) + u.OutputTokens * 5 AS W
                                         FROM dbo.UsageMonthly u JOIN dbo.Stores s ON s.Id = u.StoreId WHERE u.Month = @m ORDER BY W DESC", "@m", Db.Month))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    top.Add(new Dictionary<string, object> {
                        { "id", r["Id"] }, { "name", r["Name"] }, { "slug", r["Slug"] }, { "plan", r["PlanName"] as string ?? "" }, { "messages", r["Messages"] },
                        { "used", Convert.ToInt64(r["W"]) }, { "limit", r["MonthlyTokenLimit"] }, { "costEgp", Math.Round(Billing.UsdForUnits(Convert.ToInt64(r["W"])) * rate, 2) } });

            var subs = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 6 x.Id, x.PlanName, x.PaidEgp, x.CreatedAt, s.Name FROM dbo.Subscriptions x JOIN dbo.Stores s ON s.Id = x.StoreId ORDER BY x.CreatedAt DESC"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) subs.Add(new Dictionary<string, object> { { "id", r["Id"] }, { "plan", r["PlanName"] }, { "paid", r["PaidEgp"] }, { "at", Billing.Iso(r["CreatedAt"]) }, { "store", r["Name"] } });

            return new Dictionary<string, object>
            {
                { "usdRate", rate }, { "month", Db.Month },
                { "stores", stores }, { "newStores", newStores }, { "activeSubs", active }, { "expiringSoon", expiring }, { "expired", expired },
                { "revenueMonth", revMonth }, { "revenueTotal", revTotal }, { "openTickets", openTickets },
                { "messagesMonth", thisMonth.Messages }, { "costUsdMonth", Math.Round(costUsd, 4) }, { "costEgpMonth", Math.Round(costUsd * rate, 2) },
                { "profitMonth", Math.Round(revMonth - costUsd * rate, 2) },
                { "series", series }, { "topStores", top }, { "recentSubs", subs }, { "recentTickets", Tickets(c, "active", 5)["tickets"] },
            };
        }

        // ---------- plans ----------
        static void PlanSave(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id");
            string name = Web.Str(b, "name", 100), desc = Web.Str(b, "description", 400), features = Web.Str(b, "features", 2000);
            var price = DecOf(b, "priceEgp"); var usd = DecOf(b, "usdBudget");
            int days = Web.Int(b, "durationDays") ?? 30, sort = Web.Int(b, "sortOrder") ?? 0;
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) { Web.Error(res, 400, "bad_name", "اكتب اسم الباقة."); return; }
            if (price == null || price < 0 || price > 1000000) { Web.Error(res, 400, "bad_price", "اكتب سعر الباقة بالجنيه."); return; }
            if (usd == null || usd <= 0 || usd > 200) { Web.Error(res, 400, "bad_usd", "رصيد Claude لازم يكون بين 0.01 و 200 دولار."); return; }
            if (days < 1 || days > 366) { Web.Error(res, 400, "bad_days", "مدة الباقة لازم تكون من 1 لـ 366 يوم."); return; }
            features = string.Join("\n", (features ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(12));

            // discount: none, a percentage (1-100) or an amount in EGP below the price; optional end date
            string dType = (Web.Str(b, "discountType", 10) ?? "").ToLowerInvariant();
            var dVal = DecOf(b, "discountValue");
            string dLabel = Web.Str(b, "discountLabel", 60);
            DateTime? dEnds = null;
            var endsRaw = Web.Str(b, "discountEndsAt", 20);
            if (!string.IsNullOrEmpty(endsRaw))
            {
                DateTime e;
                if (!DateTime.TryParseExact(endsRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out e)) { Web.Error(res, 400, "bad_date", "تاريخ نهاية الخصم مش مظبوط."); return; }
                dEnds = e.AddDays(1).AddSeconds(-1);   // through the end of that day
            }
            if (dType != "percent" && dType != "amount") { dType = null; dVal = null; dEnds = null; dLabel = null; }
            else
            {
                if (dVal == null || dVal <= 0) { Web.Error(res, 400, "bad_discount", "اكتب قيمة الخصم."); return; }
                if (dType == "percent" && dVal > 100) { Web.Error(res, 400, "bad_discount", "نسبة الخصم لازم تكون من 1 لـ 100%."); return; }
                if (dType == "amount" && dVal >= price) { Web.Error(res, 400, "bad_discount", "قيمة الخصم لازم تكون أقل من سعر الباقة."); return; }
            }
            var limit = Billing.UnitsForUsd(usd.Value);
            var ps = new object[] {
                "@n", name.Trim(), "@d", desc, "@f", features.Length == 0 ? null : features, "@p", price.Value, "@u", usd.Value, "@l", (int)limit, "@days", days,
                "@a", BoolOf(b, "isActive"), "@feat", BoolOf(b, "isFeatured"), "@sort", sort, "@dt", dType, "@dv", dVal, "@de", dEnds, "@dl", dLabel };
            if (id.HasValue)
            {
                var all = ps.ToList(); all.Add("@id"); all.Add(id.Value);
                using (var cmd = Db.Cmd(c, @"UPDATE dbo.Plans SET Name=@n, Description=@d, Features=@f, PriceEgp=@p, UsdBudget=@u, TokenLimit=@l, DurationDays=@days, IsActive=@a,
                                             IsFeatured=@feat, SortOrder=@sort, DiscountType=@dt, DiscountValue=@dv, DiscountEndsAt=@de, DiscountLabel=@dl WHERE Id=@id AND IsDeleted=0", all.ToArray()))
                    if (cmd.ExecuteNonQuery() == 0) { Web.Error(res, 404, "not_found", "الباقة مش موجودة."); return; }
                // stores on this plan get the new allowance right away
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET MonthlyTokenLimit=@l, PlanName=@n WHERE PlanId=@id", "@l", (int)limit, "@n", name.Trim(), "@id", id.Value)) cmd.ExecuteNonQuery();
            }
            else
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Plans(Name, Description, Features, PriceEgp, UsdBudget, TokenLimit, DurationDays, IsActive, IsFeatured, SortOrder, DiscountType, DiscountValue, DiscountEndsAt, DiscountLabel)
                                             VALUES (@n, @d, @f, @p, @u, @l, @days, @a, @feat, @sort, @dt, @dv, @de, @dl)", ps)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "plans", Billing.Plans(c, true).Select(p => Billing.PlanJson(c, p, true)).ToList() } });
        }

        static void PlanDelete(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id");
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            // kept in the database (hidden) so old subscriptions keep their history; stores on it keep their allowance until their end date
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Plans SET IsDeleted=1, IsActive=0 WHERE Id=@id", "@id", id.Value)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "plans", Billing.Plans(c, true).Select(p => Billing.PlanJson(c, p, true)).ToList() } });
        }

        // ---------- stores ----------
        static Dictionary<string, object> StoreRow(SqlDataReader r, decimal rate)
        {
            var u = new UsageInfo { Messages = Convert.ToInt32(r["Messages"]), InputTokens = Convert.ToInt64(r["I"]), CacheReadTokens = Convert.ToInt64(r["CR"]), CacheWriteTokens = Convert.ToInt64(r["CW"]), OutputTokens = Convert.ToInt64(r["O"]) };
            var ends = r["PlanEndsAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["PlanEndsAt"], DateTimeKind.Utc);
            bool hasPlan = r["PlanId"] != DBNull.Value;
            return new Dictionary<string, object>
            {
                { "id", r["Id"] }, { "slug", r["Slug"] }, { "name", r["Name"] }, { "email", r["OwnerEmail"] }, { "status", r["Status"] }, { "isAdmin", r["IsAdmin"] },
                { "limit", r["MonthlyTokenLimit"] }, { "limitUsd", Billing.UsdForUnits(Convert.ToInt64(r["MonthlyTokenLimit"])) },
                { "plan", hasPlan ? r["PlanName"] as string : null }, { "planId", hasPlan ? r["PlanId"] : null }, { "planEndsAt", ends.HasValue ? ends.Value.ToString("o") : "" },
                { "planExpired", hasPlan && ends.HasValue && ends.Value < DateTime.UtcNow },
                { "messages", u.Messages }, { "used", u.Weighted }, { "costEgp", Math.Round(Billing.UsdForUnits(u.Weighted) * rate, 2) },
                { "chars", Convert.ToInt64(r["Chars"]) }, { "openTickets", r["OpenTickets"] }, { "created", Billing.Iso(r["CreatedAt"]) },
                { "shopify", !string.IsNullOrEmpty(r["ShopifyDomain"] as string) || !string.IsNullOrEmpty(r["ShopifyShop"] as string) },
            };
        }

        const string StoreSelect = @"SELECT s.Id, s.Slug, s.Name, s.OwnerEmail, s.Status, s.IsAdmin, s.MonthlyTokenLimit, s.PlanId, s.PlanName, s.PlanEndsAt, s.CreatedAt,
  s.ShopifyDomain, s.ShopifyShop, s.AllowedOrigins, s.BubbleStyle, s.BrandColor,
  ISNULL(u.Messages,0) AS Messages, ISNULL(u.InputTokens,0) AS I, ISNULL(u.CacheReadTokens,0) AS CR, ISNULL(u.CacheWriteTokens,0) AS CW, ISNULL(u.OutputTokens,0) AS O,
  (SELECT ISNULL(SUM(CAST(LEN(k.Content) AS BIGINT)),0) FROM dbo.KnowledgeItems k WHERE k.StoreId = s.Id) AS Chars,
  (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.StoreId = s.Id AND t.Status = 'open') AS OpenTickets
FROM dbo.Stores s LEFT JOIN dbo.UsageMonthly u ON u.StoreId = s.Id AND u.Month = @m ";

        static Dictionary<string, object> Stores(SqlConnection c)
        {
            var rate = Billing.UsdRate(c);
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, StoreSelect + "ORDER BY s.CreatedAt DESC", "@m", Db.Month))
            using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(StoreRow(r, rate));
            return new Dictionary<string, object> { { "stores", list }, { "plans", Billing.Plans(c, true).Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "finalPrice", Billing.FinalPrice(p) }, { "priceEgp", p.PriceEgp }, { "durationDays", p.DurationDays }, { "isActive", p.IsActive } }).ToList() } };
        }

        static void StoreDetail(SqlConnection c, HttpRequest req, HttpResponse res)
        {
            int id; if (!int.TryParse(req.QueryString["id"], out id)) { Web.Error(res, 400, "bad_request"); return; }
            var rate = Billing.UsdRate(c);
            Dictionary<string, object> store = null; string origins = null, style = null, color = null;
            using (var cmd = Db.Cmd(c, StoreSelect + "WHERE s.Id = @id", "@m", Db.Month, "@id", id))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) { store = StoreRow(r, rate); origins = r["AllowedOrigins"] as string; style = r["BubbleStyle"] as string; color = r["BrandColor"] as string; }
            if (store == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            store["origins"] = origins ?? ""; store["bubbleStyle"] = style ?? ""; store["brandColor"] = color ?? "";

            var subs = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.Subscriptions WHERE StoreId = @s ORDER BY CreatedAt DESC", "@s", id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) subs.Add(SubRow(r));
            var months = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 6 * FROM dbo.UsageMonthly WHERE StoreId = @s ORDER BY Month DESC", "@s", id))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var u = new UsageInfo { Messages = (int)r["Messages"], InputTokens = (long)r["InputTokens"], CacheReadTokens = (long)r["CacheReadTokens"], CacheWriteTokens = (long)r["CacheWriteTokens"], OutputTokens = (long)r["OutputTokens"] };
                    months.Add(new Dictionary<string, object> { { "month", r["Month"] }, { "messages", u.Messages }, { "used", u.Weighted }, { "costEgp", Math.Round(Billing.UsdForUnits(u.Weighted) * rate, 2) } });
                }
            months.Reverse();
            var tickets = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT Id, Subject, Status, UpdatedAt FROM dbo.Tickets WHERE StoreId = @s ORDER BY UpdatedAt DESC", "@s", id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) tickets.Add(new Dictionary<string, object> { { "id", r["Id"] }, { "subject", r["Subject"] }, { "status", r["Status"] }, { "updated", Billing.Iso(r["UpdatedAt"]) } });
            Web.Json(res, 200, new Dictionary<string, object> { { "store", store }, { "subscriptions", subs }, { "usage", months }, { "tickets", tickets } });
        }

        static void StoreCreate(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            string name = Web.Str(b, "name", 100), slug = (Web.Str(b, "slug", 40) ?? "").ToLowerInvariant(), email = (Web.Str(b, "email", 200) ?? "").ToLowerInvariant(), pass = Web.Str(b, "password", 200) ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) { Web.Error(res, 400, "bad_name", "اكتب اسم المتجر."); return; }
            if (!Web.SlugRx.IsMatch(slug)) { Web.Error(res, 400, "bad_slug", "المعرّف لازم يكون حروف إنجليزي صغيرة وأرقام وشرطة (-)، من 3 لـ 30 حرف."); return; }
            if (!Web.ValidEmail(email)) { Web.Error(res, 400, "bad_email", "الإيميل مش صحيح."); return; }
            if (pass.Length < 8) { Web.Error(res, 400, "weak_password", "الباسورد لازم يكون 8 حروف على الأقل."); return; }
            if (Db.StoreBySlug(c, slug) != null) { Web.Error(res, 409, "slug_taken", "المعرّف ده مستخدم."); return; }
            if (Db.StoreByEmail(c, email) != null) { Web.Error(res, 409, "email_taken", "الإيميل ده عليه حساب بالفعل."); return; }
            int id;
            using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Stores (Slug, Name, OwnerEmail, PasswordHash, IsAdmin, MonthlyTokenLimit, Welcome, BotName)
                                         OUTPUT INSERTED.Id VALUES (@slug, @name, @email, @hash, 0, @limit, @welcome, N'')",
                "@slug", slug, "@name", name.Trim(), "@email", email, "@hash", Web.HashPassword(pass), "@limit", FreeLimit(),
                "@welcome", "أهلاً بيك في **" + name.Trim() + "**! 👋\nعندك أي سؤال؟ اسألني وأنا أساعدك على طول."))
                id = (int)cmd.ExecuteScalar();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "id", id } });
        }

        static void StoreUpdate(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id");
            if (id == null || Db.StoreById(c, id.Value) == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            var sets = new List<string>(); var ps = new List<object>();
            if (b.ContainsKey("name")) { var v = Web.Str(b, "name", 100); if (string.IsNullOrWhiteSpace(v)) { Web.Error(res, 400, "bad_name", "اكتب اسم المتجر."); return; } sets.Add("Name=@name"); ps.Add("@name"); ps.Add(v); }
            if (b.ContainsKey("email"))
            {
                var v = (Web.Str(b, "email", 200) ?? "").ToLowerInvariant();
                if (!Web.ValidEmail(v)) { Web.Error(res, 400, "bad_email", "الإيميل مش صحيح."); return; }
                var other = Db.StoreByEmail(c, v);
                if (other != null && other.Id != id.Value) { Web.Error(res, 409, "email_taken", "الإيميل ده عليه حساب تاني."); return; }
                sets.Add("OwnerEmail=@email"); ps.Add("@email"); ps.Add(v);
            }
            if (b.ContainsKey("status")) { var v = Web.Str(b, "status", 20); if (v != "active" && v != "suspended") { Web.Error(res, 400, "bad_status"); return; } sets.Add("Status=@st"); ps.Add("@st"); ps.Add(v); }
            if (b.ContainsKey("limitUsd"))
            {
                var v = DecOf(b, "limitUsd");
                if (v == null || v < 0 || v > 200) { Web.Error(res, 400, "bad_limit", "الحد لازم يكون بين 0 و 200 دولار."); return; }
                sets.Add("MonthlyTokenLimit=@l"); ps.Add("@l"); ps.Add((int)Billing.UnitsForUsd(v.Value));
            }
            if (b.ContainsKey("planEndsAt"))
            {
                DateTime e; var raw = Web.Str(b, "planEndsAt", 20);
                if (!DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out e)) { Web.Error(res, 400, "bad_date", "التاريخ مش مظبوط."); return; }
                sets.Add("PlanEndsAt=@pe"); ps.Add("@pe"); ps.Add(e.AddDays(1).AddSeconds(-1));
            }
            if (sets.Count > 0)
            {
                ps.Add("@id"); ps.Add(id.Value);
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET " + string.Join(", ", sets) + " WHERE Id=@id", ps.ToArray())) cmd.ExecuteNonQuery();
            }
            Web.Json(res, 200, new { ok = true });
        }

        static void StorePassword(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id"); var pass = Web.Str(b, "password", 200) ?? "";
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            if (pass.Length < 8) { Web.Error(res, 400, "weak_password", "الباسورد لازم يكون 8 حروف على الأقل."); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET PasswordHash=@h WHERE Id=@id", "@h", Web.HashPassword(pass), "@id", id.Value)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        static void StoreDelete(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id"); var confirm = (Web.Str(b, "confirm", 40) ?? "").ToLowerInvariant();
            var s = id.HasValue ? Db.StoreById(c, id.Value) : null;
            if (s == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            if (confirm != s.Slug) { Web.Error(res, 400, "confirm", "اكتب معرّف المتجر بالظبط عشان تأكد الحذف."); return; }
            // data, usage, subscriptions and tickets go with it (ON DELETE CASCADE)
            using (var cmd = Db.Cmd(c, "DELETE FROM dbo.Stores WHERE Id=@id", "@id", s.Id)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        // Open the store owner's dashboard as that store (to help with support).
        static void StoreLogin(HttpContext ctx, SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id");
            if (id == null || Db.StoreById(c, id.Value) == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            Web.SignIn(ctx, id.Value);
            Web.Json(res, 200, new { ok = true });
        }

        // ---------- subscriptions (recorded by hand: cash, InstaPay, Vodafone Cash...) ----------
        static Dictionary<string, object> SubRow(SqlDataReader r)
        {
            return new Dictionary<string, object>
            {
                { "id", r["Id"] }, { "storeId", r["StoreId"] }, { "plan", r["PlanName"] }, { "months", r["Months"] }, { "price", r["PriceEgp"] }, { "discount", r["DiscountEgp"] },
                { "paid", r["PaidEgp"] }, { "method", r["PaymentMethod"] as string ?? "" }, { "notes", r["Notes"] as string ?? "" },
                { "startsAt", Billing.Iso(r["StartsAt"]) }, { "endsAt", Billing.Iso(r["EndsAt"]) }, { "at", Billing.Iso(r["CreatedAt"]) },
            };
        }

        static void Subscribe(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var storeId = Web.Int(b, "storeId"); var planId = Web.Int(b, "planId");
            int months = Web.Int(b, "months") ?? 1;
            var s = storeId.HasValue ? Db.StoreById(c, storeId.Value) : null;
            var p = planId.HasValue ? Billing.Plan(c, planId.Value) : null;
            if (s == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            if (p == null) { Web.Error(res, 404, "no_plan", "الباقة مش موجودة."); return; }
            if (months < 1 || months > 12) { Web.Error(res, 400, "bad_months", "المدة من شهر لـ 12 شهر."); return; }
            decimal price = p.PriceEgp * months, offer = Billing.FinalPrice(p) * months;
            var paid = DecOf(b, "paidEgp") ?? offer;
            if (paid < 0 || paid > price * 10) { Web.Error(res, 400, "bad_paid", "المبلغ المدفوع مش منطقي."); return; }
            int subId;
            var end = Billing.Activate(c, s, p, months, price, paid, Web.Str(b, "method", 40), Web.Str(b, "notes", 500), out subId);
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "endsAt", end.ToString("o") } });
        }

        // ---------- plan requests from store owners ----------
        static Dictionary<string, object> Requests(SqlConnection c, string status)
        {
            string where = status == "pending" || status == "approved" || status == "rejected" || status == "cancelled" ? "WHERE r.Status = @st" : "";
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 300 r.*, s.Name AS StoreName, s.Slug, s.OwnerEmail, s.PlanName AS CurPlan, s.PlanEndsAt AS CurEnds
                                         FROM dbo.PlanRequests r JOIN dbo.Stores s ON s.Id = r.StoreId " + where +
                                       " ORDER BY CASE r.Status WHEN 'pending' THEN 0 ELSE 1 END, r.CreatedAt DESC", "@st", status ?? ""))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var d = Billing.RequestJson(r);
                    d["store"] = r["StoreName"]; d["slug"] = r["Slug"]; d["email"] = r["OwnerEmail"];
                    d["currentPlan"] = r["CurPlan"] as string ?? ""; d["currentEnds"] = Billing.Iso(r["CurEnds"]);
                    list.Add(d);
                }
            int pending;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.PlanRequests WHERE Status = 'pending'")) pending = (int)cmd.ExecuteScalar();
            return new Dictionary<string, object> { { "requests", list }, { "pending", pending } };
        }

        static void RequestDecide(SqlConnection c, Dictionary<string, object> b, HttpResponse res, bool approve)
        {
            var id = Web.Int(b, "id");
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            int storeId = 0, planId = 0, months = 0; decimal price = 0, amount = 0; string status = null, method = null, refNo = null, note = null;
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.PlanRequests WHERE Id = @id", "@id", id.Value))
            using (var r = cmd.ExecuteReader())
                if (r.Read())
                {
                    storeId = (int)r["StoreId"]; planId = (int)r["PlanId"]; months = (int)r["Months"]; price = (decimal)r["PriceEgp"]; amount = (decimal)r["AmountEgp"];
                    status = (string)r["Status"]; method = r["PaymentMethod"] as string; refNo = r["PaymentRef"] as string; note = r["Note"] as string;
                }
            if (status == null) { Web.Error(res, 404, "not_found", "الطلب مش موجود."); return; }
            if (status != "pending") { Web.Error(res, 409, "decided", "الطلب ده اتراجع قبل كده."); return; }
            string adminNote = Web.Str(b, approve ? "note" : "reason", 500);

            if (!approve)
            {
                if (string.IsNullOrWhiteSpace(adminNote)) { Web.Error(res, 400, "need_reason", "اكتب سبب الرفض عشان يظهر لصاحب المتجر."); return; }
                using (var cmd = Db.Cmd(c, "UPDATE dbo.PlanRequests SET Status='rejected', AdminNote=@n, DecidedAt=SYSUTCDATETIME() WHERE Id=@id AND Status='pending'", "@n", adminNote, "@id", id.Value)) cmd.ExecuteNonQuery();
                Web.Json(res, 200, new { ok = true });
                return;
            }

            var s = Db.StoreById(c, storeId);
            var p = Billing.Plan(c, planId);
            if (s == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            if (p == null) { Web.Error(res, 404, "no_plan", "الباقة دي اتمسحت. ارفض الطلب واطلب من صاحب المتجر يختار باقة تانية."); return; }
            var paid = DecOf(b, "paidEgp") ?? amount;
            if (paid < 0 || paid > price * 10) { Web.Error(res, 400, "bad_paid", "المبلغ المدفوع مش منطقي."); return; }
            string notes = string.Join(" · ", new[] { string.IsNullOrWhiteSpace(refNo) ? null : "رقم العملية: " + refNo, note, adminNote }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (notes.Length > 500) notes = notes.Substring(0, 500);
            int subId;
            var end = Billing.Activate(c, s, p, months, price, paid, method, notes, out subId);
            using (var cmd = Db.Cmd(c, "UPDATE dbo.PlanRequests SET Status='approved', AdminNote=@n, SubscriptionId=@sub, DecidedAt=SYSUTCDATETIME() WHERE Id=@id",
                "@n", (object)adminNote ?? DBNull.Value, "@sub", subId, "@id", id.Value)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "endsAt", end.ToString("o") } });
        }

        static void SubscriptionCancel(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "storeId");
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET PlanId=NULL, PlanName=NULL, PlanEndsAt=NULL, MonthlyTokenLimit=@l WHERE Id=@id", "@l", FreeLimit(), "@id", id.Value)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        static void SubscriptionDelete(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id");
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            using (var cmd = Db.Cmd(c, "DELETE FROM dbo.Subscriptions WHERE Id=@id", "@id", id.Value)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        static Dictionary<string, object> Subscriptions(SqlConnection c)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 500 x.*, s.Name AS StoreName, s.Slug FROM dbo.Subscriptions x JOIN dbo.Stores s ON s.Id = x.StoreId ORDER BY x.CreatedAt DESC"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) { var d = SubRow(r); d["store"] = r["StoreName"]; d["slug"] = r["Slug"]; list.Add(d); }
            return new Dictionary<string, object> { { "subscriptions", list } };
        }

        // ---------- support tickets ----------
        public static Dictionary<string, object> Tickets(SqlConnection c, string status, int top = 300)
        {
            string where = status == "open" ? "WHERE t.Status = 'open'" : status == "answered" ? "WHERE t.Status = 'answered'" : status == "closed" ? "WHERE t.Status = 'closed'"
                         : status == "active" ? "WHERE t.Status <> 'closed'" : "";
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP (@top) t.*, s.Name AS StoreName, s.Slug,
                  (SELECT COUNT(*) FROM dbo.TicketMessages m WHERE m.TicketId = t.Id) AS Msgs,
                  (SELECT TOP 1 Body FROM dbo.TicketMessages m WHERE m.TicketId = t.Id ORDER BY m.Id DESC) AS LastBody
                FROM dbo.Tickets t JOIN dbo.Stores s ON s.Id = t.StoreId " + where + " ORDER BY CASE t.Status WHEN 'open' THEN 0 WHEN 'answered' THEN 1 ELSE 2 END, t.UpdatedAt DESC", "@top", top))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var last = r["LastBody"] as string ?? "";
                    list.Add(new Dictionary<string, object> {
                        { "id", r["Id"] }, { "storeId", r["StoreId"] }, { "store", r["StoreName"] }, { "slug", r["Slug"] }, { "subject", r["Subject"] }, { "category", r["Category"] },
                        { "status", r["Status"] }, { "messages", r["Msgs"] }, { "last", last.Length > 140 ? last.Substring(0, 140) + "…" : last },
                        { "created", Billing.Iso(r["CreatedAt"]) }, { "updated", Billing.Iso(r["UpdatedAt"]) } });
                }
            return new Dictionary<string, object> { { "tickets", list } };
        }

        public static List<Dictionary<string, object>> Messages(SqlConnection c, int ticketId)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.TicketMessages WHERE TicketId = @t ORDER BY Id", "@t", ticketId))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new Dictionary<string, object> { { "id", r["Id"] }, { "fromAdmin", r["FromAdmin"] }, { "body", r["Body"] }, { "at", Billing.Iso(r["CreatedAt"]) } });
            return list;
        }

        static void TicketDetail(SqlConnection c, HttpRequest req, HttpResponse res)
        {
            int id; if (!int.TryParse(req.QueryString["id"], out id)) { Web.Error(res, 400, "bad_request"); return; }
            Dictionary<string, object> t = null;
            using (var cmd = Db.Cmd(c, "SELECT t.*, s.Name AS StoreName, s.Slug, s.OwnerEmail, s.PlanName FROM dbo.Tickets t JOIN dbo.Stores s ON s.Id = t.StoreId WHERE t.Id = @id", "@id", id))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) t = new Dictionary<string, object> {
                    { "id", r["Id"] }, { "storeId", r["StoreId"] }, { "store", r["StoreName"] }, { "slug", r["Slug"] }, { "email", r["OwnerEmail"] }, { "plan", r["PlanName"] as string ?? "" },
                    { "subject", r["Subject"] }, { "category", r["Category"] }, { "status", r["Status"] }, { "created", Billing.Iso(r["CreatedAt"]) } };
            if (t == null) { Web.Error(res, 404, "not_found", "التذكرة مش موجودة."); return; }
            t["messages"] = Messages(c, id);
            Web.Json(res, 200, t);
        }

        static void TicketReply(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id"); var body = Web.Str(b, "body", 4000);
            if (id == null || string.IsNullOrWhiteSpace(body)) { Web.Error(res, 400, "bad_request", "اكتب الرد."); return; }
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.TicketMessages(TicketId, FromAdmin, Body) VALUES (@t, 1, @b); UPDATE dbo.Tickets SET Status='answered', UpdatedAt=SYSUTCDATETIME() WHERE Id=@t", "@t", id.Value, "@b", body)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        static void TicketStatus(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id"); var st = Web.Str(b, "status", 20);
            if (id == null || (st != "open" && st != "answered" && st != "closed")) { Web.Error(res, 400, "bad_request"); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Tickets SET Status=@s, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id", "@s", st, "@id", id.Value)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        // ---------- settings ----------
        static Dictionary<string, object> SettingsGet(SqlConnection c)
        {
            return new Dictionary<string, object>
            {
                { "usdRate", Billing.UsdRate(c) }, { "whatsapp", Billing.WhatsApp(c) },
                { "unitsSmall", Billing.Units(c, "UnitsSmall") }, { "unitsMedium", Billing.Units(c, "UnitsMedium") }, { "unitsLarge", Billing.Units(c, "UnitsLarge") },
                { "freeLimitUsd", Billing.UsdForUnits(FreeLimit()) },
                { "paymentInfo", Billing.Get(c, "PaymentInfo") ?? "" },
                { "periodDiscounts", Billing.PeriodDiscounts(c).Where(kv => kv.Key > 1).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => (object)kv.Value) },
            };
        }

        static void SettingsSave(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var rate = DecOf(b, "usdRate");
            if (rate != null) { if (rate < 1 || rate > 1000) { Web.Error(res, 400, "bad_rate", "سعر الدولار مش منطقي."); return; } Billing.Set(c, "UsdRate", rate.Value.ToString(CultureInfo.InvariantCulture)); }
            if (b.ContainsKey("whatsapp"))
            {
                var w = new string((Web.Str(b, "whatsapp", 30) ?? "").Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
                if (w.Length < 8) { Web.Error(res, 400, "bad_whatsapp", "اكتب رقم واتساب صحيح."); return; }
                Billing.Set(c, "WhatsApp", w);
            }
            foreach (var k in new[] { "unitsSmall", "unitsMedium", "unitsLarge" })
            {
                var v = Web.Int(b, k);
                if (v == null) continue;
                if (v < 100 || v > 1000000) { Web.Error(res, 400, "bad_units", "استهلاك الرسالة لازم يكون بين 100 و مليون وحدة."); return; }
                Billing.Set(c, "U" + k.Substring(1), v.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (b.ContainsKey("paymentInfo"))
            {
                var pi = (Web.Str(b, "paymentInfo", 400) ?? "").Trim();
                if (pi.Length < 5) { Web.Error(res, 400, "bad_payment_info", "اكتب طريقة الدفع اللي هتظهر لأصحاب المتاجر."); return; }
                Billing.Set(c, "PaymentInfo", pi);
            }
            object pd;
            if (b.TryGetValue("periodDiscounts", out pd) && pd is Dictionary<string, object>)
            {
                var parts = new List<string>();
                foreach (var kv in (Dictionary<string, object>)pd)
                {
                    int m; decimal pct;
                    if (!int.TryParse(kv.Key, out m) || !Billing.Periods.Contains(m) || m == 1) continue;
                    if (!decimal.TryParse(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out pct) || pct < 0 || pct >= 90)
                    { Web.Error(res, 400, "bad_discount", "خصم المدة لازم يكون من 0 لـ 89%."); return; }
                    parts.Add(m + ":" + pct.ToString(CultureInfo.InvariantCulture));
                }
                Billing.Set(c, "PeriodDiscounts", string.Join(",", parts));
            }
            Web.Json(res, 200, SettingsGet(c));
        }

        static void ChangePassword(SqlConnection c, int adminId, Dictionary<string, object> b, HttpResponse res)
        {
            var cur = Web.Str(b, "current", 200) ?? ""; var nw = Web.Str(b, "password", 200) ?? "";
            string hash;
            using (var cmd = Db.Cmd(c, "SELECT PasswordHash FROM dbo.Admins WHERE Id=@id", "@id", adminId)) hash = cmd.ExecuteScalar() as string;
            if (hash == null || !Web.CheckPassword(cur, hash)) { Web.Error(res, 400, "bad_current", "الباسورد الحالي غلط."); return; }
            if (nw.Length < 8) { Web.Error(res, 400, "weak_password", "الباسورد الجديد لازم يكون 8 حروف على الأقل."); return; }
            if (nw == cur) { Web.Error(res, 400, "same_password", "اختار باسورد مختلف عن الحالي."); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Admins SET PasswordHash=@h, DefaultPassword=0 WHERE Id=@id", "@h", Web.HashPassword(nw), "@id", adminId)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }
    }

    // api/plans.ashx  public list of plans (pricing page + store dashboard)
    //   GET ?mine=1              the signed-in store's requests + subscription history
    //   POST action=request      ask for a plan (planId, months, method, ref, note) -> waits for the super admin
    //   POST action=cancel       cancel my pending request (id)
    public class PlansHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    bool post = req.HttpMethod == "POST";
                    if (post || req.QueryString["mine"] == "1") { Mine(ctx, c, post); return; }
                    var wa = Billing.WhatsApp(c);
                    Web.Json(res, 200, new Dictionary<string, object>
                    {
                        { "plans", Billing.Plans(c, false).Select(p => Billing.PlanJson(c, p, false)).ToList() },
                        { "whatsapp", wa }, { "wa", Billing.WaNumber(wa) },
                        { "units", new Dictionary<string, object> { { "small", Billing.Units(c, "UnitsSmall") }, { "medium", Billing.Units(c, "UnitsMedium") }, { "large", Billing.Units(c, "UnitsLarge") } } },
                        { "paymentInfo", Billing.Get(c, "PaymentInfo") ?? "" },
                    });
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error"); }
        }

        static readonly string[] Methods = { "InstaPay", "فودافون كاش", "تحويل بنكي", "كاش", "تاني" };

        static void Mine(HttpContext ctx, SqlConnection c, bool post)
        {
            var req = ctx.Request; var res = ctx.Response;
            var s = Me.Require(ctx, c);
            if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
            if (!post)
            {
                var requests = new List<Dictionary<string, object>>();
                using (var cmd = Db.Cmd(c, "SELECT TOP 100 * FROM dbo.PlanRequests WHERE StoreId=@s ORDER BY CreatedAt DESC", "@s", s.Id))
                using (var r = cmd.ExecuteReader()) while (r.Read()) requests.Add(Billing.RequestJson(r));
                var subs = new List<Dictionary<string, object>>();
                using (var cmd = Db.Cmd(c, "SELECT TOP 100 * FROM dbo.Subscriptions WHERE StoreId=@s ORDER BY StartsAt DESC, Id DESC", "@s", s.Id))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        subs.Add(new Dictionary<string, object> {
                            { "id", r["Id"] }, { "plan", r["PlanName"] }, { "months", r["Months"] }, { "paid", r["PaidEgp"] },
                            { "startsAt", Billing.Iso(r["StartsAt"]) }, { "endsAt", Billing.Iso(r["EndsAt"]) } });
                Web.Json(res, 200, new Dictionary<string, object> { { "requests", requests }, { "subscriptions", subs }, { "methods", Methods } });
                return;
            }
            if (!Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
            string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
            var b = Web.Body(req, 4000);

            if (action == "cancel")
            {
                var id = Web.Int(b, "id");
                int n;
                using (var cmd = Db.Cmd(c, "UPDATE dbo.PlanRequests SET Status='cancelled', DecidedAt=SYSUTCDATETIME() WHERE Id=@id AND StoreId=@s AND Status='pending'", "@id", id ?? 0, "@s", s.Id)) n = cmd.ExecuteNonQuery();
                if (n == 0) { Web.Error(res, 404, "not_found", "مالقيناش طلب مستني مراجعة نلغيه."); return; }
                Web.Json(res, 200, new { ok = true });
                return;
            }
            if (action != "request") { Web.Error(res, 404, "unknown_action"); return; }

            if (!Web.Allow("planreq:" + s.Id, 6, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "بعت طلبات كتير، استنى شوية."); return; }
            var planId = Web.Int(b, "planId"); int months = Web.Int(b, "months") ?? 1;
            var p = planId.HasValue ? Billing.Plan(c, planId.Value) : null;
            if (p == null || !p.IsActive) { Web.Error(res, 404, "no_plan", "الباقة دي مش متاحة دلوقتي."); return; }
            if (!Billing.Periods.Contains(months)) { Web.Error(res, 400, "bad_months", "اختار مدة: شهر أو 4 أو 6 أو 12 شهر."); return; }
            string method = (Web.Str(b, "method", 40) ?? "").Trim();
            if (!Methods.Contains(method)) { Web.Error(res, 400, "bad_method", "اختار طريقة الدفع."); return; }
            string refNo = (Web.Str(b, "ref", 100) ?? "").Trim();
            if (refNo.Length < 3) { Web.Error(res, 400, "need_ref", "اكتب رقم العملية أو الرقم اللي حوّلت منه، عشان نقدر نتأكد من الدفع."); return; }
            string note = (Web.Str(b, "note", 500) ?? "").Trim();
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.PlanRequests WHERE StoreId=@s AND Status='pending'", "@s", s.Id))
                if ((int)cmd.ExecuteScalar() > 0) { Web.Error(res, 409, "has_pending", "عندك طلب لسه بيتراجع. استنى الرد عليه، أو الغيه وابعت طلب جديد."); return; }

            var q = Billing.QuoteFor(c, p, months);
            int newId;
            using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.PlanRequests(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, AmountEgp, PaymentMethod, PaymentRef, Note)
                                         OUTPUT INSERTED.Id VALUES (@s, @p, @n, @m, @price, @disc, @amt, @method, @ref, @note)",
                "@s", s.Id, "@p", p.Id, "@n", p.Name, "@m", months, "@price", q.Price, "@disc", q.Discount, "@amt", q.Total,
                "@method", method, "@ref", refNo, "@note", note.Length == 0 ? (object)DBNull.Value : note)) newId = (int)cmd.ExecuteScalar();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "id", newId }, { "amount", q.Total } });
        }
    }

    // api/support.ashx  store owner's support tickets: GET list | GET ?id= | POST action=create|reply|close
    public class SupportHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "GET")
                    {
                        int id;
                        if (int.TryParse(req.QueryString["id"], out id))
                        {
                            Dictionary<string, object> t = null;
                            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.Tickets WHERE Id=@id AND StoreId=@s", "@id", id, "@s", s.Id))
                            using (var r = cmd.ExecuteReader())
                                if (r.Read()) t = new Dictionary<string, object> { { "id", r["Id"] }, { "subject", r["Subject"] }, { "category", r["Category"] }, { "status", r["Status"] }, { "created", Billing.Iso(r["CreatedAt"]) } };
                            if (t == null) { Web.Error(res, 404, "not_found", "التذكرة مش موجودة."); return; }
                            t["messages"] = SuperAdminHandler.Messages(c, id);
                            Web.Json(res, 200, t);
                            return;
                        }
                        var list = new List<Dictionary<string, object>>();
                        using (var cmd = Db.Cmd(c, @"SELECT t.*, (SELECT COUNT(*) FROM dbo.TicketMessages m WHERE m.TicketId=t.Id) AS Msgs FROM dbo.Tickets t
                                                     WHERE t.StoreId=@s ORDER BY CASE t.Status WHEN 'answered' THEN 0 WHEN 'open' THEN 1 ELSE 2 END, t.UpdatedAt DESC", "@s", s.Id))
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) list.Add(new Dictionary<string, object> { { "id", r["Id"] }, { "subject", r["Subject"] }, { "category", r["Category"] }, { "status", r["Status"] }, { "messages", r["Msgs"] }, { "updated", Billing.Iso(r["UpdatedAt"]) } });
                        Web.Json(res, 200, new Dictionary<string, object> { { "tickets", list } });
                        return;
                    }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    var b = Web.Body(req, 20000);
                    var action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                    if (action == "create")
                    {
                        if (!Web.Allow("tk:" + s.Id, 6, TimeSpan.FromDays(1))) { Web.Error(res, 429, "rate_limited", "فتحت تذاكر كتير النهارده، كمّل في تذكرة مفتوحة أو كلّمنا واتساب."); return; }
                        var subject = Web.Str(b, "subject", 150); var body = Web.Str(b, "body", 4000);
                        var cat = Web.Str(b, "category", 30) ?? "other";
                        if (!new[] { "technical", "billing", "data", "suggestion", "other" }.Contains(cat)) cat = "other";
                        if (string.IsNullOrWhiteSpace(subject) || subject.Length < 3) { Web.Error(res, 400, "bad_subject", "اكتب عنوان للمشكلة."); return; }
                        if (string.IsNullOrWhiteSpace(body) || body.Length < 5) { Web.Error(res, 400, "bad_body", "اكتب تفاصيل المشكلة."); return; }
                        int id;
                        using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Tickets(StoreId, Subject, Category) OUTPUT INSERTED.Id VALUES (@s, @sub, @cat)", "@s", s.Id, "@sub", subject, "@cat", cat)) id = (int)cmd.ExecuteScalar();
                        using (var cmd = Db.Cmd(c, "INSERT INTO dbo.TicketMessages(TicketId, FromAdmin, Body) VALUES (@t, 0, @b)", "@t", id, "@b", body)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "id", id } });
                        return;
                    }
                    var tid = Web.Int(b, "id");
                    bool mine = false;
                    if (tid.HasValue) using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Tickets WHERE Id=@id AND StoreId=@s", "@id", tid.Value, "@s", s.Id)) mine = (int)cmd.ExecuteScalar() > 0;
                    if (!mine) { Web.Error(res, 404, "not_found", "التذكرة مش موجودة."); return; }
                    if (action == "reply")
                    {
                        if (!Web.Allow("tkr:" + s.Id, 40, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "استنى شوية وجرّب تاني."); return; }
                        var body = Web.Str(b, "body", 4000);
                        if (string.IsNullOrWhiteSpace(body)) { Web.Error(res, 400, "bad_body", "اكتب ردك."); return; }
                        using (var cmd = Db.Cmd(c, "INSERT INTO dbo.TicketMessages(TicketId, FromAdmin, Body) VALUES (@t, 0, @b); UPDATE dbo.Tickets SET Status='open', UpdatedAt=SYSUTCDATETIME() WHERE Id=@t", "@t", tid.Value, "@b", body)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, new { ok = true });
                        return;
                    }
                    if (action == "close")
                    {
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Tickets SET Status='closed', UpdatedAt=SYSUTCDATETIME() WHERE Id=@t", "@t", tid.Value)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, new { ok = true });
                        return;
                    }
                    Web.Error(res, 404, "unknown_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }
    }
}
