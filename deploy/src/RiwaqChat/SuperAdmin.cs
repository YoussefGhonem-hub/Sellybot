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
            // the free trial (a store with no plan): this many chat replies a month
            { "FreeMessages", "500" },
            // ...within this many days of signing up, once (it doesn't come back every month)
            { "FreeDays", "30" },
            // discount % for paying several months at once ("months:percent")
            { "PeriodDiscounts", "4:5,6:10,12:20" },
            // «السعر القديم»: who pays for a plan by the end of this day keeps that price for its first OldPriceMonths months on it ("" = off)
            { "OldPriceUntil", "2026-12-10" }, { "OldPriceMonths", "6" },
            // the number owners send the money to (shown with InstaPay's QR in the plan request form)
            { "PayNumber", "01229391155" },
            // shown to store owners in the plan request form
            { "PaymentInfo", "حوّل المبلغ على InstaPay أو أورانج كاش على رقم الواتساب بتاعنا، وبعدين اكتب رقم العملية (أو الرقم اللي حوّلت منه) في الطلب." },
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

        // ---------- InstaPay's QR, shown to owners next to the number they pay to (a picture in dbo.AppFiles) ----------
        const string PayQrKey = "PayQr";
        // changes whenever a new picture is uploaded ("" = none), so the page can ask for the new one
        public static string PayQrVersion(SqlConnection c)
        {
            using (var cmd = Db.Cmd(c, "SELECT UpdatedAt FROM dbo.AppFiles WHERE [Key] = @k", "@k", PayQrKey))
            { var v = cmd.ExecuteScalar(); return v is DateTime ? ((DateTime)v).Ticks.ToString(CultureInfo.InvariantCulture) : ""; }
        }
        public static bool PayQrRead(SqlConnection c, out byte[] data, out string type)
        {
            data = null; type = null;
            using (var cmd = Db.Cmd(c, "SELECT ContentType, Data FROM dbo.AppFiles WHERE [Key] = @k", "@k", PayQrKey))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { type = (string)r[0]; data = (byte[])r[1]; }
            return data != null;
        }
        public static void PayQrSave(SqlConnection c, byte[] data, string type)
        {
            using (var cmd = Db.Cmd(c, @"MERGE dbo.AppFiles AS t USING (SELECT @k AS [Key]) AS s ON t.[Key] = s.[Key]
                                         WHEN MATCHED THEN UPDATE SET ContentType = @t, Data = @d, UpdatedAt = SYSUTCDATETIME()
                                         WHEN NOT MATCHED THEN INSERT ([Key], ContentType, Data) VALUES (@k, @t, @d);", "@k", PayQrKey, "@t", type, "@d", data))
                cmd.ExecuteNonQuery();
        }
        public static void PayQrDelete(SqlConnection c) { using (var cmd = Db.Cmd(c, "DELETE FROM dbo.AppFiles WHERE [Key] = @k", "@k", PayQrKey)) cmd.ExecuteNonQuery(); }
        // sends the picture as itself (its type was checked from its bytes when it was uploaded)
        public static bool PayQrServe(SqlConnection c, HttpResponse res)
        {
            byte[] data; string type;
            if (!PayQrRead(c, out data, out type)) return false;
            res.ContentType = type; res.AddHeader("X-Content-Type-Options", "nosniff"); res.BinaryWrite(data);
            return true;
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
        // Periods owners can request right now; the others show as "قريبًا" (coming soon). The super admin can still record any period by hand.
        public static readonly int[] OpenPeriods = { 1 };

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
                { "months", m }, { "total", q.Total }, { "perMonth", Math.Round(q.Total / m, 0) }, { "pct", q.Pct }, { "price", q.Price }, { "save", q.Discount },
                { "soon", !OpenPeriods.Contains(m) } }; }).ToList();
        }

        // ---------- «السعر القديم»: a store's first months at the price it paid before a cut-off date ----------
        // Settings: OldPriceUntil (yyyy-MM-dd, the last day; "off" = off) and OldPriceMonths (default 6; was 3 until the offer moved to 10 Dec).
        // A store that pays for a plan before the end of that day keeps paying that monthly price for its first OldPriceMonths
        // months on the plan (months before the cut-off count too). It still holds if the plan ran out and the store comes back
        // to the same plan before ExpiresAt (its first start + those months). Another plan after the cut-off = that plan's price.
        // One row per store (dbo.PriceLocks); each request keeps what it got (PlanRequests.OldPrice / OldLeft) for the admin.
        public class PriceLock
        {
            public int PlanId, MonthsUsed, Months; public string PlanName; public decimal Price; public DateTime StartsAt, ExpiresAt;
            public int Left { get { return Math.Max(0, Months - MonthsUsed); } }
            public bool Holds { get { return Left > 0 && DateTime.UtcNow < ExpiresAt; } }
        }
        // What a request gets at the old price: the monthly price, how many of its months, and how many are left after it
        public class OldInfo { public decimal Price; public int Months, LeftAfter; }

        // The end of the OldPriceUntil day (null = off). Like a plan discount's last day (PlanSave): through 23:59:59 UTC, so the two end together.
        public static DateTime? OldPriceCutoff(SqlConnection c)
        {
            var v = (Get(c, "OldPriceUntil") ?? "").Trim(); DateTime d;
            if (v.Length == 0 || !DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
            return DateTime.SpecifyKind(d.Date.AddDays(1), DateTimeKind.Utc);
        }
        public static int OldPriceMonths(SqlConnection c) { int m; return int.TryParse(Get(c, "OldPriceMonths"), out m) && m >= 0 && m <= 24 ? m : 6; }
        // A new number of months also reaches the stores that already have an old price: each one's window becomes its first
        // old-price month + the new count (like AfterPlanPayment), and a request still waiting keeps the right months left after it
        // (OldLeft), so approving it counts its months right (AfterPlanPayment: used = months - left after).
        public static void SetOldPriceMonths(SqlConnection c, int months)
        {
            int was = OldPriceMonths(c);
            Set(c, "OldPriceMonths", months.ToString(CultureInfo.InvariantCulture));
            if (months == was) return;
            using (var cmd = Db.Cmd(c, @"
UPDATE l SET ExpiresAt = DATEADD(DAY, @m * CASE WHEN p.DurationDays > 1 THEN p.DurationDays ELSE 1 END, l.StartsAt), UpdatedAt = SYSUTCDATETIME()
  FROM dbo.PriceLocks l JOIN dbo.Plans p ON p.Id = l.PlanId;
UPDATE dbo.PlanRequests SET OldLeft = CASE WHEN ISNULL(OldLeft, 0) + @d > 0 THEN ISNULL(OldLeft, 0) + @d ELSE 0 END
  WHERE Status = 'pending' AND OldPrice IS NOT NULL;", "@m", months, "@d", months - was)) cmd.ExecuteNonQuery();
        }
        public static bool BeforeCutoff(SqlConnection c, DateTime whenUtc) { var cut = OldPriceCutoff(c); return cut.HasValue && whenUtc < cut.Value; }

        public static PriceLock LockOf(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.PriceLocks WHERE StoreId=@s", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                if (r.Read())
                    return new PriceLock {
                        PlanId = (int)r["PlanId"], PlanName = r["PlanName"] as string ?? "", Price = (decimal)r["PriceEgp"], MonthsUsed = (int)r["MonthsUsed"], Months = OldPriceMonths(c),
                        StartsAt = DateTime.SpecifyKind((DateTime)r["StartsAt"], DateTimeKind.Utc), ExpiresAt = DateTime.SpecifyKind((DateTime)r["ExpiresAt"], DateTimeKind.Utc) };
            return null;
        }

        // The store's old price for plan p, while it holds (null = it pays the plan's price)
        public static PriceLock ActiveLock(SqlConnection c, StoreInfo s, PlanInfo p)
        {
            if (s == null || p == null) return null;
            var l = LockOf(c, s.Id);
            return l != null && l.PlanId == p.Id && l.Holds && l.Price < FinalPrice(p) ? l : null;
        }

        // What this store pays a month for plan p now: its old price while it holds, else the plan's price (after the plan's own discount)
        public static decimal PriceFor(SqlConnection c, StoreInfo s, PlanInfo p)
        {
            var l = ActiveLock(c, s, p);
            return l != null ? l.Price : FinalPrice(p);
        }

        // What a request for plan p (months; 0 = an upgrade) made now gets at the old price, or null.
        //  - the store keeps an old price for this plan: those months, at that price
        //  - before the cut-off: the plan's price now becomes its old price (a store that switches plans before the cut-off carries its months on)
        public static OldInfo OldFor(SqlConnection c, StoreInfo s, PlanInfo p, int months)
        {
            int total = OldPriceMonths(c);
            if (s == null || p == null || total <= 0) return null;
            var l = LockOf(c, s.Id);
            if (l != null && l.PlanId == p.Id)
            {
                if (!l.Holds) return null;   // its first months are over (or it came back too late)
                int use = Math.Min(months, l.Left);
                return new OldInfo { Price = Math.Min(l.Price, FinalPrice(p)), Months = use, LeftAfter = l.Left - use };
            }
            if (!BeforeCutoff(c, DateTime.UtcNow)) return null;
            int left = Math.Max(0, total - (l != null && l.Holds ? l.MonthsUsed : 0));
            if (left <= 0) return null;
            int u = Math.Min(months, left);
            return new OldInfo { Price = FinalPrice(p), Months = u, LeftAfter = left - u };
        }

        // Price for N months of plan p for this store: its old price for the months it still has, the plan's price for the rest
        public static Quote QuoteFor(SqlConnection c, PlanInfo p, int months, OldInfo old)
        {
            var q = QuoteFor(c, p, months);
            if (old == null || old.Months <= 0 || old.Price >= FinalPrice(p)) return q;
            decimal pct; PeriodDiscounts(c).TryGetValue(months, out pct);
            q.Total = Math.Max(0, Math.Round((old.Price * old.Months + FinalPrice(p) * (months - old.Months)) * (1 - pct / 100m), 0));
            q.Discount = Math.Max(0, q.Price - q.Total);
            return q;
        }

        // After a plan payment is approved: keep (or start) the store's old price, or drop it when it moved to another plan after the cut-off.
        // old: what the request got (null = nothing); start: when the new plan time begins.
        public static void AfterPlanPayment(SqlConnection c, StoreInfo s, PlanInfo p, OldInfo old, DateTime start)
        {
            int total = OldPriceMonths(c);
            var l = LockOf(c, s.Id);
            if (old == null)
            {
                if (l != null && l.PlanId != p.Id)
                    using (var cmd = Db.Cmd(c, "DELETE FROM dbo.PriceLocks WHERE StoreId=@s", "@s", s.Id)) cmd.ExecuteNonQuery();
                return;
            }
            int used = Math.Max(0, total - old.LeftAfter);
            // the old price runs from the first month paid at it; a store that switched plans before the cut-off keeps its window
            var from = l != null && l.Holds ? l.StartsAt : start;
            var until = from.AddDays(total * Math.Max(1, p.DurationDays));
            using (var cmd = Db.Cmd(c, @"
MERGE dbo.PriceLocks AS t USING (SELECT @s AS StoreId) AS x ON t.StoreId = x.StoreId
WHEN MATCHED THEN UPDATE SET PlanId=@p, PlanName=@n, PriceEgp=@price, MonthsUsed=@used, StartsAt=@from, ExpiresAt=@until, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (StoreId, PlanId, PlanName, PriceEgp, MonthsUsed, StartsAt, ExpiresAt) VALUES (@s, @p, @n, @price, @used, @from, @until);",
                "@s", s.Id, "@p", p.Id, "@n", p.Name, "@price", old.Price, "@used", used, "@from", from, "@until", until)) cmd.ExecuteNonQuery();
        }

        // Once, when this shipped: every store already on a running plan gets its old price (its first months counted from its first payment on that plan)
        public static void EnsurePriceLocks(SqlConnection c)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Migrations WHERE Name='price-locks'")) if ((int)cmd.ExecuteScalar() > 0) return;
            int total = OldPriceMonths(c);
            var rows = new List<Tuple<int, int>>();
            using (var cmd = Db.Cmd(c, "SELECT Id, PlanId FROM dbo.Stores WHERE PlanId IS NOT NULL AND PlanEndsAt > SYSUTCDATETIME() AND Id NOT IN (SELECT StoreId FROM dbo.PriceLocks)"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(Tuple.Create((int)r["Id"], (int)r["PlanId"]));
            foreach (var row in rows)
            {
                var p = Plan(c, row.Item2);
                if (p == null) continue;
                int months = 0; DateTime? first = null;
                using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(Months),0) m, MIN(StartsAt) f FROM dbo.Subscriptions WHERE StoreId=@s AND PlanId=@p AND Kind='plan'", "@s", row.Item1, "@p", p.Id))
                using (var r = cmd.ExecuteReader())
                    if (r.Read()) { months = Convert.ToInt32(r["m"]); if (r["f"] != DBNull.Value) first = DateTime.SpecifyKind((DateTime)r["f"], DateTimeKind.Utc); }
                var from = first ?? DateTime.UtcNow;
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.PriceLocks(StoreId, PlanId, PlanName, PriceEgp, MonthsUsed, StartsAt, ExpiresAt) VALUES (@s, @p, @n, @price, @used, @from, @until)",
                    "@s", row.Item1, "@p", p.Id, "@n", p.Name, "@price", FinalPrice(p), "@used", Math.Max(1, months), "@from", from, "@until", from.AddDays(total * Math.Max(1, p.DurationDays))))
                    cmd.ExecuteNonQuery();
            }
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Migrations(Name) VALUES ('price-locks')")) cmd.ExecuteNonQuery();
        }

        // For «باقتي», the admin's store page and the pricing pages
        public static Dictionary<string, object> LockJson(SqlConnection c, PriceLock l, PlanInfo p)
        {
            if (l == null) return null;
            return new Dictionary<string, object> {
                { "planId", l.PlanId }, { "plan", l.PlanName }, { "price", l.Price }, { "regular", p != null ? FinalPrice(p) : 0m },
                { "used", Math.Min(l.MonthsUsed, l.Months) }, { "months", l.Months }, { "left", l.Left }, { "holds", l.Holds && (p == null || l.Price < FinalPrice(p)) },
                { "startsAt", l.StartsAt.ToString("o") }, { "expiresAt", l.ExpiresAt.ToString("o") } };
        }
        public static Dictionary<string, object> OldOfferJson(SqlConnection c)
        {
            var cut = OldPriceCutoff(c); int m = OldPriceMonths(c);
            if (!cut.HasValue || m <= 0 || DateTime.UtcNow >= cut.Value) return null;
            return new Dictionary<string, object> { { "until", cut.Value.ToString("o") }, { "day", Get(c, "OldPriceUntil") }, { "months", m } };
        }

        // ---------- changing plans ----------
        // What picking plan p means for this store right now:
        //   new        no running plan: starts today
        //   renew      the same plan: the new time starts after the current end
        //   upgrade    a pricier plan (per day): starts now, same end date, pays the price difference for the days left
        //   downgrade  a cheaper plan: starts when the current one ends (Stores.Next*, Db.RollOverPlans)
        // Blocked: a lower plan is already waiting and this pick would clash with it (the super admin can cancel the waiting one).
        public class Change { public string Kind, Blocked, CurName; public int Days, CurDuration, NewDuration; public decimal Amount, CurPrice, NewPrice, Regular; public DateTime StartsAt; public PriceLock Old; }

        public static bool Running(StoreInfo s) { return s.PlanId.HasValue && s.PlanEndsAt.HasValue && s.PlanEndsAt.Value > DateTime.UtcNow; }

        public static Change ChangeFor(SqlConnection c, StoreInfo s, PlanInfo p)
        {
            var now = DateTime.UtcNow;
            // NewPrice: what this store pays a month for p (its old price while it holds); Regular: the plan's price for everyone
            var old = ActiveLock(c, s, p);
            var ch = new Change { NewPrice = old != null ? old.Price : FinalPrice(p), Regular = FinalPrice(p), Old = old, NewDuration = p.DurationDays, StartsAt = now, Kind = "new" };
            if (!Running(s)) return ch;
            var end = s.PlanEndsAt.Value;
            ch.CurName = s.PlanName; ch.Days = Math.Max(1, (int)Math.Ceiling((end - now).TotalDays));
            string waiting = s.NextPlanId.HasValue
                ? "باقة " + s.NextPlanName + " هتبدأ يوم " + end.ToString("yyyy-MM-dd") + " بدل الباقة الحالية. عشان تختار حاجة تانية لازم التغيير ده يتلغي الأول، كلّم الإدارة." : null;
            if (s.PlanId.Value == p.Id) { ch.Kind = "renew"; ch.StartsAt = end; ch.Blocked = waiting; return ch; }
            if (s.NextPlanId == p.Id) { ch.Kind = "downgrade"; ch.StartsAt = end.AddDays(s.NextDays); return ch; }   // more time on the plan that's already waiting
            var cur = Plan(c, s.PlanId.Value);
            if (cur == null) return ch;   // the current plan was deleted: a fresh start, as before
            ch.CurPrice = PriceFor(c, s, cur); ch.CurDuration = cur.DurationDays;
            decimal curDaily = ch.CurPrice / Math.Max(1, cur.DurationDays), newDaily = ch.NewPrice / Math.Max(1, p.DurationDays);
            ch.Blocked = waiting;
            if (newDaily >= curDaily) { ch.Kind = "upgrade"; ch.Amount = Math.Max(0, Math.Round((newDaily - curDaily) * ch.Days, 0)); }
            else { ch.Kind = "downgrade"; ch.StartsAt = end; }
            return ch;
        }

        public static Dictionary<string, object> ChangeJson(Change ch, PlanInfo p)
        {
            return new Dictionary<string, object> {
                { "planId", p.Id }, { "kind", ch.Kind }, { "blocked", ch.Blocked }, { "days", ch.Days }, { "amount", ch.Amount },
                { "curName", ch.CurName ?? "" }, { "curPrice", ch.CurPrice }, { "curDuration", ch.CurDuration }, { "newPrice", ch.NewPrice }, { "newDuration", ch.NewDuration },
                { "startsAt", ch.StartsAt.ToString("o") },
                // the store's old price for this plan (renewing it, or coming back to it): the regular price, and months left at the old one
                { "regular", ch.Regular }, { "oldLeft", ch.Old != null ? ch.Old.Left : 0 } };
        }

        // Starts, extends or schedules a period of plan p (see ChangeFor): history row + the store's plan, end date and monthly limit.
        // A lower plan only waits (Stores.Next*). Returns the new period's end date; startsAt is when it begins.
        public static DateTime Activate(SqlConnection c, StoreInfo s, PlanInfo p, int months, decimal price, decimal paid, string method, string notes, out int subscriptionId, out DateTime startsAt)
        {
            var ch = ChangeFor(c, s, p);
            if (ch.Blocked != null) throw new BadRequestException(ch.Blocked);
            // a full period of a pricier plan (requests sent before upgrades existed) still starts today, as it always did
            var start = startsAt = ch.Kind == "renew" || ch.Kind == "downgrade" ? ch.StartsAt : DateTime.UtcNow;
            int days = p.DurationDays * months;
            var end = start.AddDays(days);
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Subscriptions(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, PaidEgp, PaymentMethod, Notes, StartsAt, EndsAt, Kind)
                                             OUTPUT INSERTED.Id VALUES (@s, @p, @n, @m, @price, @disc, @paid, @method, @notes, @start, @end, 'plan')",
                    "@s", s.Id, "@p", p.Id, "@n", p.Name, "@m", months, "@price", price, "@disc", Math.Max(0, price - paid), "@paid", paid,
                    "@method", (object)method ?? DBNull.Value, "@notes", (object)notes ?? DBNull.Value, "@start", start, "@end", end))
                { cmd.Transaction = tx; subscriptionId = (int)cmd.ExecuteScalar(); }
                using (var cmd = ch.Kind == "downgrade"
                    ? Db.Cmd(c, "UPDATE dbo.Stores SET NextPlanId=@p, NextPlanName=@n, NextDays=ISNULL(NextDays,0)+@d WHERE Id=@s", "@p", p.Id, "@n", p.Name, "@d", days, "@s", s.Id)
                    : Db.Cmd(c, "UPDATE dbo.Stores SET PlanId=@p, PlanName=@n, PlanEndsAt=@end, MonthlyTokenLimit=@l, Status='active' WHERE Id=@s",
                        "@p", p.Id, "@n", p.Name, "@end", end, "@l", p.TokenLimit, "@s", s.Id))
                { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            return end;
        }

        // A pricier plan right away: same end date, the store pays the price difference for the days left (ChangeFor). Returns the end date.
        public static DateTime Upgrade(SqlConnection c, StoreInfo s, PlanInfo p, decimal amount, decimal paid, string method, string notes, out int subscriptionId)
        {
            var ch = ChangeFor(c, s, p);
            if (ch.Blocked != null) throw new BadRequestException(ch.Blocked);
            if (ch.Kind != "upgrade")
                throw new BadRequestException(ch.Kind == "new" ? "باقة المتجر خلصت، فدي مش ترقية. ارفض الطلب وخلّي صاحب المتجر يطلب الباقة كاشتراك جديد."
                                                                : "الباقة دي مش أعلى من باقة المتجر الحالية.");
            var now = DateTime.UtcNow; var end = s.PlanEndsAt.Value;
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Subscriptions(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, PaidEgp, PaymentMethod, Notes, StartsAt, EndsAt, Kind)
                                             OUTPUT INSERTED.Id VALUES (@s, @p, @n, 0, @price, @disc, @paid, @method, @notes, @start, @end, 'upgrade')",
                    "@s", s.Id, "@p", p.Id, "@n", p.Name, "@price", amount, "@disc", Math.Max(0, amount - paid), "@paid", paid,
                    "@method", (object)method ?? DBNull.Value, "@notes", (object)notes ?? DBNull.Value, "@start", now, "@end", end))
                { cmd.Transaction = tx; subscriptionId = (int)cmd.ExecuteScalar(); }
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET PlanId=@p, PlanName=@n, MonthlyTokenLimit=@l, Status='active' WHERE Id=@s",
                    "@p", p.Id, "@n", p.Name, "@l", p.TokenLimit, "@s", s.Id))
                { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            return end;
        }

        // Drops a lower plan that was waiting, and its payment rows (they never started).
        public static bool CancelNext(SqlConnection c, StoreInfo s)
        {
            if (!s.NextPlanId.HasValue || !s.PlanEndsAt.HasValue) return false;
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = Db.Cmd(c, "DELETE FROM dbo.Subscriptions WHERE StoreId=@s AND Kind='plan' AND PlanId=@p AND StartsAt >= DATEADD(SECOND, -1, @end)",
                    "@s", s.Id, "@p", s.NextPlanId.Value, "@end", s.PlanEndsAt.Value)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET NextPlanId=NULL, NextPlanName=NULL, NextDays=NULL WHERE Id=@s", "@s", s.Id)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            return true;
        }

        // ---------- extra quota for the current month (when the monthly quota runs out before the month ends) ----------
        public static readonly int[] TopupPcts = { 25, 50, 100 };

        // +X% of the plan's monthly quota for X% of the plan's current monthly price.
        public static Quote TopupQuote(PlanInfo p, int pct, out long units) { return TopupQuote(p, pct, out units, FinalPrice(p)); }
        public static Quote TopupQuote(PlanInfo p, int pct, out long units, decimal monthPrice)
        {
            units = (long)p.TokenLimit * pct / 100;
            decimal total = Math.Max(0, Math.Round(monthPrice * pct / 100m, 0));
            return new Quote { Months = 0, Price = total, Discount = 0, Total = total, Pct = pct };
        }

        // Extra units approved for this store in the current month.
        public static long TopupUnits(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(Units), 0) FROM dbo.PlanRequests WHERE StoreId=@s AND Kind='topup' AND Status='approved' AND Month=@m", "@s", storeId, "@m", Db.Month))
                return Convert.ToInt64(cmd.ExecuteScalar());
        }

        // What the store may use this month: its plan's monthly quota (0 when the plan ended) + extra quota bought for this month.
        public static long TotalLimit(SqlConnection c, StoreInfo s)
        {
            if (Expired(s)) return 0;
            return s.MonthlyTokenLimit + TopupUnits(c, s.Id);
        }

        // ---------- extra messages worked out from the store's own use: enough to reach the end of the month ----------
        // Pace = this month's messages per day (last month's when it's too early to tell and last month was busier),
        // need = pace x days left (+10% to spare) - what's left now, rounded up. Priced at the plan's own rate per unit,
        // with this store's own units per message (its real average after 10 messages, by its data size before that).
        public class TopupCalc
        {
            public long Per;                                    // units one message of this store takes
            public int Used, Left, DaysPassed, DaysLeft, Rate, Need, Suggest, Min, Max;
            public decimal PricePerMsg, SuggestPrice;
            public DateTime Until;                              // what the extra messages cover: the month's end, or the plan's end when sooner
            public bool FromLastMonth;
        }
        public const int TopupMinMsgs = 50;

        public static TopupCalc TopupNeed(SqlConnection c, StoreInfo s, PlanInfo p)
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);
            var until = s.PlanEndsAt.HasValue && s.PlanEndsAt.Value < monthEnd ? s.PlanEndsAt.Value : monthEnd;
            var u = Db.Usage(c, s.Id, Db.Month);
            long chars;
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CAST(LEN(Content) AS BIGINT)),0) FROM dbo.KnowledgeItems WHERE StoreId=@s", "@s", s.Id)) chars = Convert.ToInt64(cmd.ExecuteScalar());
            long per = Math.Max(1, u.Messages >= 10 && u.Weighted > 0 ? u.Weighted / u.Messages : Units(c, chars <= 40000 ? "UnitsSmall" : chars <= 150000 ? "UnitsMedium" : "UnitsLarge"));
            double passed = Math.Max(1, (now - monthStart).TotalDays), rate = u.Messages / passed;
            bool last = false;
            if (passed < 5)
            {
                var pm = monthStart.AddMonths(-1);
                var prev = Db.Usage(c, s.Id, pm.ToString("yyyy-MM"));
                double prevRate = prev.Messages / (double)DateTime.DaysInMonth(pm.Year, pm.Month);
                if (prevRate > rate) { rate = prevRate; last = true; }
            }
            double daysLeft = Math.Max(0, (until - now).TotalDays);
            int left = (int)Math.Max(0, (TotalLimit(c, s) - u.Weighted) / per);
            int need = (int)Math.Ceiling(rate * daysLeft * 1.1) - left;
            int suggest = RoundMsgs(Math.Max(need, TopupMinMsgs));
            decimal ppm = PriceFor(c, s, p) * per / Math.Max(1, p.TokenLimit);   // the store's own monthly price (its old price while it holds)
            return new TopupCalc {
                Per = per, Used = u.Messages, Left = left, DaysPassed = (int)Math.Floor(passed), DaysLeft = (int)Math.Ceiling(daysLeft), Rate = (int)Math.Round(rate),
                Need = Math.Max(0, need), Suggest = suggest, Min = TopupMinMsgs, Max = Math.Max(suggest * 3, (int)Math.Min(int.MaxValue / 2, p.TokenLimit * 2L / per)),
                PricePerMsg = ppm, SuggestPrice = TopupPrice(ppm, suggest), Until = until, FromLastMonth = last };
        }

        // 50s up to 500, 100s up to 3000, then 500s
        public static int RoundMsgs(int n) { int step = n <= 500 ? 50 : n <= 3000 ? 100 : 500; return (Math.Max(1, n) + step - 1) / step * step; }
        // rounded up to 5 EGP, at least 10
        public static decimal TopupPrice(decimal pricePerMsg, int msgs) { return Math.Max(10, Math.Ceiling(pricePerMsg * msgs / 5m) * 5m); }

        public static Dictionary<string, object> NeedJson(TopupCalc n)
        {
            // three ready choices around the suggestion; the owner can also type any number between Min and Max
            var opts = new[] { RoundMsgs(Math.Max(n.Min, n.Suggest / 2)), n.Suggest, RoundMsgs(n.Suggest * 3 / 2) }.Distinct().ToList();
            return new Dictionary<string, object> {
                { "per", n.Per }, { "used", n.Used }, { "left", n.Left }, { "daysPassed", n.DaysPassed }, { "daysLeft", n.DaysLeft }, { "rate", n.Rate },
                { "need", n.Need }, { "suggest", n.Suggest }, { "min", n.Min }, { "max", n.Max }, { "pricePerMsg", Math.Round(n.PricePerMsg, 10) },   // the dashboard prices a typed number the same way (TopupPrice)
                { "until", n.Until.ToString("o") }, { "fromLastMonth", n.FromLastMonth },
                { "options", opts.Select(m => new Dictionary<string, object> { { "msgs", m }, { "price", TopupPrice(n.PricePerMsg, m) }, { "suggest", m == n.Suggest } }).ToList() } };
        }

        // How the suggestion was worked out, in words, kept with the request for the admin
        public static string NeedText(TopupCalc n, int asked)
        {
            var t = "استخدم " + n.Used + " رسالة في " + n.DaysPassed + " يوم (≈ " + n.Rate + " في اليوم" + (n.FromLastMonth ? "، حسب الشهر اللي فات" : "") + ")، " +
                    "وفاضل " + n.DaysLeft + " يوم لحد " + n.Until.ToString("yyyy-MM-dd") + "، وفاضله " + n.Left + " رسالة. " +
                    "محتاج حوالي " + n.Need + " رسالة، واقترحنا " + n.Suggest + (asked != n.Suggest ? "، وهو اختار " + asked : "") + ".";
            return t.Length > 400 ? t.Substring(0, 400) : t;
        }

        // ---------- the free trial counts messages, a plan counts units ----------
        // The trial is one time only: FreeMessages replies within FreeDays of signing up. After that (or once the
        // messages are used) the assistant stops until the store subscribes; it does not come back the next month.
        public static int FreeMessages(SqlConnection c) { return Units(c, "FreeMessages"); }
        public static int FreeDays(SqlConnection c) { return Units(c, "FreeDays"); }
        // a store with no plan is on the free trial (the admin's own store keeps its unit limit)
        public static bool OnTrial(StoreInfo s) { return !s.PlanId.HasValue && !s.IsAdmin; }
        public static DateTime TrialEnds(SqlConnection c, StoreInfo s) { return DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc).AddDays(FreeDays(c)); }
        public static bool TrialEnded(SqlConnection c, StoreInfo s) { return OnTrial(s) && DateTime.UtcNow >= TrialEnds(c, s); }
        // the trial's replies so far: every month since signing up (30 days can span two calendar months)
        public static int TrialUsed(SqlConnection c, StoreInfo s)
        {
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(Messages), 0) FROM dbo.UsageMonthly WHERE StoreId=@s AND Month >= @m",
                "@s", s.Id, "@m", s.CreatedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
                return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // Nothing left? Trial: its days are over or its FreeMessages are used. Plan: this month's units + top-ups (0 once it ended).
        public static bool LimitReached(SqlConnection c, StoreInfo s)
        {
            if (OnTrial(s)) return TrialEnded(c, s) || TrialUsed(c, s) >= FreeMessages(c);
            return Db.Usage(c, s.Id, Db.Month).Weighted >= TotalLimit(c, s);
        }

        // This month for the dashboard, in messages. On a plan the units left are turned into messages at this store's
        // own cost per message: its real average after 10 messages, before that by its data size (as on the plans tab).
        public static Dictionary<string, object> UsageJson(SqlConnection c, StoreInfo s, UsageInfo u, long chars)
        {
            long limit = TotalLimit(c, s);
            bool trial = OnTrial(s);
            int msgLimit, left; double pct;
            Dictionary<string, object> trialInfo = null;
            if (trial)
            {
                int used = TrialUsed(c, s);
                var ends = TrialEnds(c, s);
                bool ended = DateTime.UtcNow >= ends;
                msgLimit = FreeMessages(c);
                left = ended ? 0 : Math.Max(0, msgLimit - used);
                pct = ended ? 100 : used * 100.0 / msgLimit;
                trialInfo = new Dictionary<string, object> {
                    { "trialUsed", used }, { "trialEnds", ends.ToString("o") }, { "trialEnded", ended },
                    { "trialDaysLeft", ended ? 0 : (int)Math.Ceiling((ends - DateTime.UtcNow).TotalDays) } };
            }
            else
            {
                double per = u.Messages >= 10 && u.Weighted > 0 ? (double)u.Weighted / u.Messages
                    : Units(c, chars <= 40000 ? "UnitsSmall" : chars <= 150000 ? "UnitsMedium" : "UnitsLarge");
                left = (int)Math.Floor(Math.Max(0, limit - u.Weighted) / per);
                msgLimit = u.Messages + left;
                pct = limit > 0 ? u.Weighted * 100.0 / limit : 100;
            }
            var d = new Dictionary<string, object> {
                { "month", Db.Month }, { "messages", u.Messages }, { "used", u.Weighted }, { "limit", limit }, { "extra", TopupUnits(c, s.Id) },
                { "trial", trial }, { "msgLimit", msgLimit }, { "msgLeft", left }, { "pct", Math.Min(100, Math.Round(pct, 1)) } };
            if (trialInfo != null) foreach (var kv in trialInfo) d[kv.Key] = kv.Value;
            return d;
        }

        public static Dictionary<string, object> RequestJson(SqlDataReader r)
        {
            return new Dictionary<string, object>
            {
                { "kind", r["Kind"] as string ?? "plan" }, { "units", r["Units"] == DBNull.Value ? 0L : Convert.ToInt64(r["Units"]) }, { "month", r["Month"] as string ?? "" },
                { "id", r["Id"] }, { "storeId", r["StoreId"] }, { "planId", r["PlanId"] }, { "plan", r["PlanName"] }, { "months", r["Months"] },
                { "price", r["PriceEgp"] }, { "discount", r["DiscountEgp"] }, { "amount", r["AmountEgp"] },
                { "method", r["PaymentMethod"] as string ?? "" }, { "ref", r["PaymentRef"] as string ?? "" }, { "refNo", r["RefNo"] as string ?? "" }, { "note", r["Note"] as string ?? "" },
                { "status", r["Status"] }, { "adminNote", r["AdminNote"] as string ?? "" },
                { "at", Iso(r["CreatedAt"]) }, { "decidedAt", Iso(r["DecidedAt"]) },
                // what an approval made (Subscriptions.Id), so «باقتي» can show its dates next to the request
                { "subscriptionId", r["SubscriptionId"] == DBNull.Value ? 0 : Convert.ToInt32(r["SubscriptionId"]) },
                // the WhatsApp service with this request (or the whole request, kind "wa"): its share of the amount and the number to link
                { "wa", r["WaAddon"] is bool && (bool)r["WaAddon"] }, { "waAmount", r["WaEgp"] == DBNull.Value ? 0m : (decimal)r["WaEgp"] }, { "waPhone", r["WaPhone"] as string ?? "" },
                // its type when it was sent (new / renew / upgrade / downgrade / wa-new / wa-renew / topup); extra messages: how many, and how we worked them out
                { "what", r["What"] as string ?? "" }, { "msgs", r["Msgs"] == DBNull.Value ? 0 : (int)r["Msgs"] }, { "calc", r["Calc"] as string ?? "" },
                // «السعر القديم»: the monthly old price this request got (0 = none), and how many months are left at it after this one
                { "oldPrice", r["OldPrice"] == DBNull.Value ? 0m : (decimal)r["OldPrice"] }, { "oldLeft", r["OldLeft"] == DBNull.Value ? 0 : (int)r["OldLeft"] },
            };
        }

        // ---------- the payment's reference number (الرقم المرجعي) ----------
        // As typed: Arabic-Indic digits become 0-9, letters go upper case, spaces/dashes/anything else drop out.
        public static string NormRef(string v)
        {
            var sb = new StringBuilder();
            foreach (var ch in v ?? "")
            {
                if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
                else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
                else if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }
        public static bool RefLooksRight(string norm) { return norm.Length >= 6 && norm.Length <= 30; }   // English letters and/or digits
        // already backing a request that is waiting or was approved (UX_PlanRequests_RefNo enforces the same)
        public static bool RefUsed(SqlConnection c, string norm)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.PlanRequests WHERE RefNo = @r AND Status IN ('pending', 'approved')", "@r", norm)) return (int)cmd.ExecuteScalar() > 0;
        }
        public const string RefUsedMessage = "الرقم المرجعي ده اتسجل قبل كده في طلب تاني، وكل دفعة ليها رقم واحد بس. اتأكد إنك كاتبه صح، ولو متأكد منه كلّمنا.";

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
                // a lower plan waiting for this one to end
                { "next", s.NextPlanId.HasValue && s.PlanEndsAt.HasValue ? new Dictionary<string, object> {
                    { "id", s.NextPlanId.Value }, { "name", s.NextPlanName ?? "" }, { "startsAt", s.PlanEndsAt.Value.ToString("o") },
                    { "endsAt", s.PlanEndsAt.Value.AddDays(s.NextDays).ToString("o") }, { "days", s.NextDays } } : null },
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
                    var b = post ? Web.Body(req, action == "pay-qr" ? 1500000 : 30000) : new Dictionary<string, object>();   // pay-qr carries a picture

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
                    Db.RollOverPlans(c);   // lower plans whose turn came show as the store's plan in every list

                    switch (action)
                    {
                        case "me": Web.Json(res, 200, new Dictionary<string, object> { { "username", adminName }, { "defaultPassword", defaultPassword } }); return;
                        case "overview": Web.Json(res, 200, Overview(c)); return;
                        case "money": Web.Json(res, 200, Finance.Report(c)); return;   // «💰 المكسب والتكلفة» (Finance.cs)
                        case "money-settings": if (!post) break; Finance.Save(c, b, res); return;
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
                        case "next-cancel": if (!post) break; NextCancel(c, b, res); return;
                        case "subscription-delete": if (!post) break; SubscriptionDelete(c, b, res); return;
                        case "subscriptions": Web.Json(res, 200, Subscriptions(c)); return;
                        case "requests": Web.Json(res, 200, Requests(c, req.QueryString["status"], req.QueryString["q"])); return;
                        case "request-approve": if (!post) break; RequestDecide(c, b, res, true); return;
                        case "request-reject": if (!post) break; RequestDecide(c, b, res, false); return;
                        case "tickets": Web.Json(res, 200, Tickets(c, req.QueryString["status"])); return;
                        case "ticket": TicketDetail(c, req, res); return;
                        case "ticket-reply": if (!post) break; TicketReply(c, b, res); return;
                        case "ticket-status": if (!post) break; TicketStatus(c, b, res); return;
                        case "settings": if (post) SettingsSave(c, b, res); else Web.Json(res, 200, SettingsGet(c)); return;
                        case "pay-qr": if (post) PayQrUpload(c, b, res); else if (!Billing.PayQrServe(c, res)) Web.Error(res, 404, "not_found"); return;
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

            var top = new List<Dictionary<string, object>>(); int free = Billing.FreeMessages(c);
            using (var cmd = Db.Cmd(c, @"SELECT TOP 6 s.Id, s.Name, s.Slug, s.PlanName, s.MonthlyTokenLimit, u.Messages,
                                           CAST(CASE WHEN s.PlanId IS NULL AND s.IsAdmin = 0 THEN 1 ELSE 0 END AS BIT) AS Trial,
                                           u.InputTokens + CAST(u.CacheWriteTokens * 1.25 AS BIGINT) + CAST(u.CacheReadTokens * 0.1 AS BIGINT) + u.OutputTokens * 5 AS W
                                         FROM dbo.UsageMonthly u JOIN dbo.Stores s ON s.Id = u.StoreId WHERE u.Month = @m ORDER BY W DESC", "@m", Db.Month))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    top.Add(new Dictionary<string, object> {
                        { "id", r["Id"] }, { "name", r["Name"] }, { "slug", r["Slug"] }, { "plan", r["PlanName"] as string ?? "" }, { "messages", r["Messages"] },
                        { "used", Convert.ToInt64(r["W"]) }, { "limit", r["MonthlyTokenLimit"] }, { "costEgp", Math.Round(Billing.UsdForUnits(Convert.ToInt64(r["W"])) * rate, 2) },
                        { "msgLimit", (bool)r["Trial"] ? free : 0 } });

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
        static Dictionary<string, object> StoreRow(SqlDataReader r, decimal rate, int freeMessages, int freeDays)
        {
            var u = new UsageInfo { Messages = Convert.ToInt32(r["Messages"]), InputTokens = Convert.ToInt64(r["I"]), CacheReadTokens = Convert.ToInt64(r["CR"]), CacheWriteTokens = Convert.ToInt64(r["CW"]), OutputTokens = Convert.ToInt64(r["O"]) };
            var ends = r["PlanEndsAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["PlanEndsAt"], DateTimeKind.Utc);
            bool hasPlan = r["PlanId"] != DBNull.Value, trial = !hasPlan && !(bool)r["IsAdmin"];
            return new Dictionary<string, object>
            {
                { "id", r["Id"] }, { "slug", r["Slug"] }, { "name", r["Name"] }, { "email", r["OwnerEmail"] }, { "phone", r["OwnerPhone"] as string ?? "" }, { "status", r["Status"] }, { "isAdmin", r["IsAdmin"] },
                { "limit", r["MonthlyTokenLimit"] }, { "limitUsd", Billing.UsdForUnits(Convert.ToInt64(r["MonthlyTokenLimit"])) },
                { "plan", hasPlan ? r["PlanName"] as string : null }, { "planId", hasPlan ? r["PlanId"] : null }, { "planEndsAt", ends.HasValue ? ends.Value.ToString("o") : "" },
                { "planExpired", hasPlan && ends.HasValue && ends.Value < DateTime.UtcNow },
                // free trial: its allowance is a number of messages, not units
                { "trial", trial }, { "msgLimit", trial ? freeMessages : 0 },
                { "trialEnds", trial ? DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc).AddDays(freeDays).ToString("o") : "" },
                { "messages", u.Messages }, { "used", u.Weighted }, { "costEgp", Math.Round(Billing.UsdForUnits(u.Weighted) * rate, 2) },
                { "chars", Convert.ToInt64(r["Chars"]) }, { "openTickets", r["OpenTickets"] }, { "created", Billing.Iso(r["CreatedAt"]) },
                { "shopify", !string.IsNullOrEmpty(r["ShopifyDomain"] as string) || !string.IsNullOrEmpty(r["ShopifyShop"] as string) },
            };
        }

        const string StoreSelect = @"SELECT s.Id, s.Slug, s.Name, s.OwnerEmail, s.Status, s.IsAdmin, s.MonthlyTokenLimit, s.PlanId, s.PlanName, s.PlanEndsAt, s.CreatedAt,
  s.ShopifyDomain, s.ShopifyShop, s.AllowedOrigins, s.BubbleStyle, s.BrandColor, s.OwnerPhone,
  ISNULL(u.Messages,0) AS Messages, ISNULL(u.InputTokens,0) AS I, ISNULL(u.CacheReadTokens,0) AS CR, ISNULL(u.CacheWriteTokens,0) AS CW, ISNULL(u.OutputTokens,0) AS O,
  (SELECT ISNULL(SUM(CAST(LEN(k.Content) AS BIGINT)),0) FROM dbo.KnowledgeItems k WHERE k.StoreId = s.Id) AS Chars,
  (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.StoreId = s.Id AND t.Status = 'open') AS OpenTickets
FROM dbo.Stores s LEFT JOIN dbo.UsageMonthly u ON u.StoreId = s.Id AND u.Month = @m ";

        static Dictionary<string, object> Stores(SqlConnection c)
        {
            var rate = Billing.UsdRate(c); int free = Billing.FreeMessages(c), freeDays = Billing.FreeDays(c);
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, StoreSelect + "ORDER BY s.CreatedAt DESC", "@m", Db.Month))
            using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(StoreRow(r, rate, free, freeDays));
            return new Dictionary<string, object> { { "stores", list }, { "plans", Billing.Plans(c, true).Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "finalPrice", Billing.FinalPrice(p) }, { "priceEgp", p.PriceEgp }, { "durationDays", p.DurationDays }, { "isActive", p.IsActive } }).ToList() } };
        }

        static void StoreDetail(SqlConnection c, HttpRequest req, HttpResponse res)
        {
            int id; if (!int.TryParse(req.QueryString["id"], out id)) { Web.Error(res, 400, "bad_request"); return; }
            var rate = Billing.UsdRate(c); int free = Billing.FreeMessages(c), freeDays = Billing.FreeDays(c);
            Dictionary<string, object> store = null; string origins = null, style = null, color = null;
            using (var cmd = Db.Cmd(c, StoreSelect + "WHERE s.Id = @id", "@m", Db.Month, "@id", id))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) { store = StoreRow(r, rate, free, freeDays); origins = r["AllowedOrigins"] as string; style = r["BubbleStyle"] as string; color = r["BrandColor"] as string; }
            if (store == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            store["origins"] = origins ?? ""; store["bubbleStyle"] = style ?? ""; store["brandColor"] = color ?? "";
            // the WhatsApp service: its own monthly renewal date (the admin's own store always has it)
            using (var cmd = Db.Cmd(c, "SELECT WaUntil, IsAdmin FROM dbo.Stores WHERE Id=@s", "@s", id))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { store["waUntil"] = Billing.Iso(r["WaUntil"]); store["waAlways"] = (bool)r["IsAdmin"]; }
            // every request the store sent (any status), for its history timeline next to the payments
            var storeReqs = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 200 * FROM dbo.PlanRequests WHERE StoreId=@s ORDER BY CreatedAt DESC", "@s", id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) storeReqs.Add(Billing.RequestJson(r));
            foreach (var x in storeReqs)
                if (string.IsNullOrEmpty(x["what"] as string)) x["what"] = (string)x["kind"] == "plan" ? "new" : (string)x["kind"] == "wa" ? "wa-new" : x["kind"];
            store["requests"] = storeReqs;

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
            // what picking each plan would do for this store (upgrade now / lower plan after the current one / renew / new), and a lower plan already waiting
            var si = Db.StoreById(c, id);
            var changes = si == null ? new List<Dictionary<string, object>>() : Billing.Plans(c, false).Select(p => Billing.ChangeJson(Billing.ChangeFor(c, si, p), p)).ToList();
            store["next"] = si == null ? null : Billing.StorePlan(si)["next"];
            var lk = si == null ? null : Billing.LockOf(c, si.Id);
            Web.Json(res, 200, new Dictionary<string, object> { { "store", store }, { "subscriptions", subs }, { "usage", months }, { "tickets", tickets }, { "changes", changes },
                { "priceLock", lk == null ? null : Billing.LockJson(c, lk, Billing.Plan(c, lk.PlanId)) } });
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
                { "id", r["Id"] }, { "storeId", r["StoreId"] }, { "plan", r["PlanName"] }, { "months", r["Months"] }, { "kind", r["Kind"] as string ?? "plan" }, { "price", r["PriceEgp"] }, { "discount", r["DiscountEgp"] },
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
            int subId;
            var ch = Billing.ChangeFor(c, s, p);
            if (ch.Kind == "upgrade")
            {
                // a pricier plan now: same end date, the price difference for the days left (the admin can change the amount)
                var paidUp = DecOf(b, "paidEgp") ?? ch.Amount;
                if (paidUp < 0 || paidUp > Math.Max(ch.Amount, Billing.FinalPrice(p)) * 10) { Web.Error(res, 400, "bad_paid", "المبلغ المدفوع مش منطقي."); return; }
                var oldUp = Billing.OldFor(c, s, p, 0);
                var endUp = Billing.Upgrade(c, s, p, ch.Amount, paidUp, Web.Str(b, "method", 40), Web.Str(b, "notes", 500), out subId);
                Billing.AfterPlanPayment(c, s, p, oldUp, DateTime.UtcNow);
                Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "kind", "upgrade" }, { "startsAt", DateTime.UtcNow.ToString("o") }, { "endsAt", endUp.ToString("o") } });
                return;
            }
            if (months < 1 || months > 12) { Web.Error(res, 400, "bad_months", "المدة من شهر لـ 12 شهر."); return; }
            var oldM = Billing.OldFor(c, s, p, months);   // the store's old price for its first months, if it has one
            decimal price = p.PriceEgp * months, offer = Billing.QuoteFor(c, p, months, oldM).Total;
            var paid = DecOf(b, "paidEgp") ?? offer;
            if (paid < 0 || paid > price * 10) { Web.Error(res, 400, "bad_paid", "المبلغ المدفوع مش منطقي."); return; }
            DateTime start;
            var end = Billing.Activate(c, s, p, months, price, paid, Web.Str(b, "method", 40), Web.Str(b, "notes", 500), out subId, out start);
            Billing.AfterPlanPayment(c, s, p, oldM, start);
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "kind", ch.Kind }, { "startsAt", start.ToString("o") }, { "endsAt", end.ToString("o") } });
        }

        static void NextCancel(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "storeId");
            var s = id.HasValue ? Db.StoreById(c, id.Value) : null;
            if (s == null) { Web.Error(res, 404, "not_found", "المتجر مش موجود."); return; }
            if (!Billing.CancelNext(c, s)) { Web.Error(res, 404, "no_next", "مفيش باقة مستنية تبدأ للمتجر ده."); return; }
            Web.Json(res, 200, new { ok = true });
        }

        // ---------- plan requests from store owners ----------
        // q: search by the reference number (any part of it, typed any way) across every status; also matches the "transferred from" number
        static Dictionary<string, object> Requests(SqlConnection c, string status, string q = null)
        {
            string where = status == "pending" || status == "approved" || status == "rejected" || status == "cancelled" ? "WHERE r.Status = @st" : "";
            string raw = (q ?? "").Trim(), norm = Billing.NormRef(raw);
            if (raw.Length > 0) where = "WHERE (r.RefNo LIKE @q OR r.PaymentRef LIKE @q2)";
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 300 r.*, s.Name AS StoreName, s.Slug, s.OwnerEmail, s.PlanName AS CurPlan, s.PlanEndsAt AS CurEnds
                                         FROM dbo.PlanRequests r JOIN dbo.Stores s ON s.Id = r.StoreId " + where +
                                       " ORDER BY CASE r.Status WHEN 'pending' THEN 0 ELSE 1 END, r.CreatedAt DESC", "@st", status ?? "",
                                       "@q", norm.Length > 0 ? "%" + norm + "%" : "", "@q2", "%" + raw.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var d = Billing.RequestJson(r);
                    d["store"] = r["StoreName"]; d["slug"] = r["Slug"]; d["email"] = r["OwnerEmail"];
                    d["currentPlan"] = r["CurPlan"] as string ?? ""; d["currentEnds"] = Billing.Iso(r["CurEnds"]);
                    list.Add(d);
                }
            // pending plan requests: what approving would do now (new / renew / downgrade later / a full period of a pricier plan)
            foreach (var d in list.Where(x => (string)x["status"] == "pending" && (string)x["kind"] == "plan"))
            {
                var st = Db.StoreById(c, Convert.ToInt32(d["storeId"])); var pl = Billing.Plan(c, Convert.ToInt32(d["planId"]));
                if (st != null && pl != null) { var ch = Billing.ChangeFor(c, st, pl); d["change"] = ch.Kind; d["changeStarts"] = ch.StartsAt.ToString("o"); d["blocked"] = ch.Blocked; }
            }
            int pending;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.PlanRequests WHERE Status = 'pending'")) pending = (int)cmd.ExecuteScalar();
            // every request: its type (kept from when it was sent; older ones worked out from kind/change), and for a pending one
            // where the store stands now and what approving does on the calendar
            var stores = new Dictionary<int, StoreInfo>();
            foreach (var d in list)
            {
                if (string.IsNullOrEmpty(d["what"] as string))
                    d["what"] = (string)d["kind"] == "plan" ? (d.ContainsKey("change") ? (string)d["change"] : "new") : (string)d["kind"] == "wa" ? "wa-new" : (string)d["kind"];
                if ((string)d["status"] != "pending") continue;
                int sid = Convert.ToInt32(d["storeId"]); StoreInfo st;
                if (!stores.TryGetValue(sid, out st)) stores[sid] = st = Db.StoreById(c, sid);
                if (st == null) continue;
                d["now"] = StoreNow(c, st);
                d["outcome"] = Outcome(c, st, d);
            }
            return new Dictionary<string, object> { { "requests", list }, { "pending", pending } };
        }

        // Where a store stands right now: its plan, the WhatsApp service, and this month's use (for the request cards)
        static Dictionary<string, object> StoreNow(SqlConnection c, StoreInfo st)
        {
            var u = Db.Usage(c, st.Id, Db.Month); long lim = Billing.TotalLimit(c, st);
            double pct = Billing.OnTrial(st) ? (Billing.TrialEnded(c, st) ? 100 : Billing.TrialUsed(c, st) * 100.0 / Math.Max(1, Billing.FreeMessages(c))) : lim > 0 ? u.Weighted * 100.0 / lim : 100;
            return new Dictionary<string, object> {
                { "plan", st.PlanName ?? "" }, { "planEnds", st.PlanEndsAt.HasValue ? st.PlanEndsAt.Value.ToString("o") : "" }, { "planExpired", Billing.Expired(st) },
                { "trial", Billing.OnTrial(st) }, { "waOn", WaAccount.HasService(st) }, { "waUntil", st.WaUntil.HasValue ? st.WaUntil.Value.ToString("o") : "" },
                { "messages", u.Messages }, { "usagePct", Math.Round(Math.Min(999, pct), 0) } };
        }

        // What approving a pending request does on the calendar (the same rules as Activate / Upgrade / AddService / the top-up)
        static Dictionary<string, object> Outcome(SqlConnection c, StoreInfo st, Dictionary<string, object> d)
        {
            var o = new Dictionary<string, object>(); var now = DateTime.UtcNow;
            string kind = (string)d["kind"]; int months = Convert.ToInt32(d["months"]);
            if (kind == "plan" || kind == "upgrade")
            {
                var p = Billing.Plan(c, Convert.ToInt32(d["planId"]));
                if (p != null)
                {
                    var ch = Billing.ChangeFor(c, st, p);
                    DateTime start = kind == "upgrade" || ch.Kind == "upgrade" || ch.Kind == "new" ? now : ch.StartsAt;
                    DateTime end = kind == "upgrade" && st.PlanEndsAt.HasValue ? st.PlanEndsAt.Value : start.AddDays(p.DurationDays * Math.Max(1, months));
                    o["planStarts"] = start.ToString("o"); o["planEnds"] = end.ToString("o");
                }
            }
            if (kind == "wa" || (d["wa"] is bool && (bool)d["wa"]))
            {
                var ws = st.WaUntil.HasValue && st.WaUntil.Value > now ? st.WaUntil.Value : now;
                o["waStarts"] = ws.ToString("o"); o["waEnds"] = ws.AddDays(WaAccount.ServiceDays).ToString("o");
            }
            if (kind == "topup") o["topupUntil"] = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddSeconds(-1).ToString("o");
            return o;
        }

        static void RequestDecide(SqlConnection c, Dictionary<string, object> b, HttpResponse res, bool approve)
        {
            var id = Web.Int(b, "id");
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            int storeId = 0, planId = 0, months = 0; decimal price = 0, amount = 0; string status = null, method = null, refNo = null, note = null, kind = "plan"; long units = 0;
            bool waAddon = false; decimal waEgp = 0;   // the WhatsApp service asked for with this request, and its share of the amount
            int reqMsgs = 0;                           // extra messages: how many were asked for (0 = an older +% request)
            Billing.OldInfo old = null;                 // «السعر القديم»: what the request got at the store's old price
            string payRef = null;                       // the payment's reference number (الرقم المرجعي), kept with the payment record
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.PlanRequests WHERE Id = @id", "@id", id.Value))
            using (var r = cmd.ExecuteReader())
                if (r.Read())
                {
                    storeId = (int)r["StoreId"]; planId = (int)r["PlanId"]; months = (int)r["Months"]; price = (decimal)r["PriceEgp"]; amount = (decimal)r["AmountEgp"];
                    status = (string)r["Status"]; method = r["PaymentMethod"] as string; refNo = r["PaymentRef"] as string; note = r["Note"] as string; payRef = r["RefNo"] as string;
                    kind = r["Kind"] as string ?? "plan"; units = r["Units"] == DBNull.Value ? 0 : Convert.ToInt64(r["Units"]);
                    waAddon = r["WaAddon"] is bool && (bool)r["WaAddon"]; waEgp = r["WaEgp"] == DBNull.Value ? 0 : (decimal)r["WaEgp"];
                    reqMsgs = r["Msgs"] == DBNull.Value ? 0 : (int)r["Msgs"];
                    if (r["OldPrice"] != DBNull.Value) old = new Billing.OldInfo { Price = (decimal)r["OldPrice"], LeftAfter = r["OldLeft"] == DBNull.Value ? 0 : (int)r["OldLeft"] };
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
            if (p == null && kind != "wa") { Web.Error(res, 404, "no_plan", "الباقة دي اتمسحت. ارفض الطلب واطلب من صاحب المتجر يختار باقة تانية."); return; }
            var paid = DecOf(b, "paidEgp") ?? amount;
            if (paid < 0 || paid > price * 10) { Web.Error(res, 400, "bad_paid", "المبلغ المدفوع مش منطقي."); return; }
            string notes = string.Join(" · ", new[] { string.IsNullOrWhiteSpace(payRef) ? null : "الرقم المرجعي: " + payRef,
                string.IsNullOrWhiteSpace(refNo) || refNo == "—" ? null : (payRef == null ? "رقم العملية: " : "من: ") + refNo, note, adminNote }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (notes.Length > 500) notes = notes.Substring(0, 500);
            int subId;
            if (kind == "wa")
            {
                // the WhatsApp service alone: its months from today, or after the end of the service that's still running
                var waEnd = WaAccount.AddService(c, s, planId, Math.Max(1, months), price, paid, method, notes, out subId);
                using (var cmd = Db.Cmd(c, "UPDATE dbo.PlanRequests SET Status='approved', AdminNote=@n, SubscriptionId=@sub, DecidedAt=SYSUTCDATETIME() WHERE Id=@id",
                    "@n", (object)adminNote ?? DBNull.Value, "@sub", subId, "@id", id.Value)) cmd.ExecuteNonQuery();
                Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "kind", "wa" }, { "endsAt", waEnd.ToString("o") } });
                return;
            }
            if (kind == "topup")
            {
                // extra quota counts for the month it is approved in; recorded with the payments (Months = 0) for the revenue page
                if (Billing.Expired(s)) { Web.Error(res, 400, "expired", "باقة المتجر ده خلصت. الرصيد الإضافي مش هيفيده، ارفض الطلب وخليه يجدد الباقة الأول."); return; }
                var now = DateTime.UtcNow;
                var monthEnd = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddSeconds(-1);
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Subscriptions(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, PaidEgp, PaymentMethod, Notes, StartsAt, EndsAt, Kind)
                                             OUTPUT INSERTED.Id VALUES (@s, @p, @n, 0, @price, @disc, @paid, @method, @notes, @start, @end, 'topup')",
                    "@s", s.Id, "@p", p.Id, "@n", "رصيد إضافي · " + (reqMsgs > 0 ? reqMsgs + " رسالة · " : "") + p.Name, "@price", price, "@disc", Math.Max(0, price - paid), "@paid", paid,
                    "@method", (object)method ?? DBNull.Value, "@notes", notes, "@start", now, "@end", monthEnd)) subId = (int)cmd.ExecuteScalar();
                using (var cmd = Db.Cmd(c, "UPDATE dbo.PlanRequests SET Status='approved', AdminNote=@n, SubscriptionId=@sub, Month=@m, DecidedAt=SYSUTCDATETIME() WHERE Id=@id",
                    "@n", (object)adminNote ?? DBNull.Value, "@sub", subId, "@m", Db.Month, "@id", id.Value)) cmd.ExecuteNonQuery();
                Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "units", units }, { "endsAt", monthEnd.ToString("o") } });
                return;
            }
            // the WhatsApp service bought with the plan: the plan's payment row gets the plan's share, the service its own row (below)
            decimal waPaid = 0;
            if (waAddon && waEgp > 0) { waPaid = Math.Min(waEgp, paid); price = Math.Max(0, price - waEgp); paid -= waPaid; }
            // upgrade: the higher plan now, same end date (the amount was the price difference when the owner asked)
            DateTime start = DateTime.UtcNow;
            var end = kind == "upgrade" ? Billing.Upgrade(c, s, p, price, paid, method, notes, out subId)
                                        : Billing.Activate(c, s, p, months, price, paid, method, notes, out subId, out start);
            Billing.AfterPlanPayment(c, s, p, old, start);   // keep, start or drop the store's old price
            // after the plan (a plan that can't start leaves the service alone): a month from today, or after the running service
            if (waAddon && waEgp > 0) { int waSub; WaAccount.AddService(c, s, p.Id, 1, waEgp, waPaid, method, notes, out waSub); }   // always one month: it renews on its own
            using (var cmd = Db.Cmd(c, "UPDATE dbo.PlanRequests SET Status='approved', AdminNote=@n, SubscriptionId=@sub, DecidedAt=SYSUTCDATETIME() WHERE Id=@id",
                "@n", (object)adminNote ?? DBNull.Value, "@sub", subId, "@id", id.Value)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "kind", kind }, { "startsAt", start.ToString("o") }, { "endsAt", end.ToString("o") } });
        }

        static void SubscriptionCancel(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "storeId");
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET PlanId=NULL, PlanName=NULL, PlanEndsAt=NULL, NextPlanId=NULL, NextPlanName=NULL, NextDays=NULL, MonthlyTokenLimit=@l WHERE Id=@id", "@l", FreeLimit(), "@id", id.Value)) cmd.ExecuteNonQuery();
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
                { "freeLimitUsd", Billing.UsdForUnits(FreeLimit()) }, { "freeMessages", Billing.FreeMessages(c) }, { "freeDays", Billing.FreeDays(c) },
                { "paymentInfo", Billing.Get(c, "PaymentInfo") ?? "" },
                { "payNumber", Billing.Get(c, "PayNumber") ?? "" }, { "payQr", Billing.PayQrVersion(c) },
                { "periodDiscounts", Billing.PeriodDiscounts(c).Where(kv => kv.Key > 1).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => (object)kv.Value) },
                // «السعر القديم»: the last day to pay at today's price ("" = off), and for how many first months
                { "oldPriceUntil", Billing.OldPriceCutoff(c).HasValue ? Billing.Get(c, "OldPriceUntil") : "" }, { "oldPriceMonths", Billing.OldPriceMonths(c) },
            };
        }

        // InstaPay's QR for the plan request form: { data: "data:image/png;base64,..." } or { remove: true }
        static void PayQrUpload(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            if (BoolOf(b, "remove")) { Billing.PayQrDelete(c); Web.Json(res, 200, SettingsGet(c)); return; }
            var s = Web.Str(b, "data", 1450000) ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^data:(image/(?:png|jpeg|webp));base64,([A-Za-z0-9+/=]+)$");
            if (!m.Success) { Web.Error(res, 400, "bad_image", "ارفع صورة PNG أو JPG."); return; }
            byte[] d;
            try { d = Convert.FromBase64String(m.Groups[2].Value); } catch (FormatException) { Web.Error(res, 400, "bad_image", "الصورة دي مش سليمة."); return; }
            if (d.Length < 100 || d.Length > 1000000) { Web.Error(res, 400, "bad_image_size", "الصورة لازم تكون أقل من 1 ميجا."); return; }
            string type = m.Groups[1].Value;
            // the bytes must really be that kind of picture
            bool real = type == "image/png" ? d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47
                      : type == "image/jpeg" ? d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF
                      : d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';
            if (!real) { Web.Error(res, 400, "bad_image", "الصورة دي مش سليمة."); return; }
            Billing.PayQrSave(c, d, type);
            Web.Json(res, 200, SettingsGet(c));
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
            // «السعر القديم»: the last day (yyyy-MM-dd, empty = off) and how many first months keep the price
            if (b.ContainsKey("oldPriceUntil"))
            {
                var day = (Web.Str(b, "oldPriceUntil", 20) ?? "").Trim(); DateTime dd;
                if (day.Length > 0 && !DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dd)) { Web.Error(res, 400, "bad_date", "آخر يوم للسعر القديم مش مظبوط."); return; }
                Billing.Set(c, "OldPriceUntil", day.Length > 0 ? day : "off");
            }
            var oldMonths = Web.Int(b, "oldPriceMonths");
            if (oldMonths != null)
            {
                if (oldMonths < 0 || oldMonths > 24) { Web.Error(res, 400, "bad_months", "عدد الشهور بالسعر القديم من 0 لـ 24."); return; }
                Billing.SetOldPriceMonths(c, oldMonths.Value);
            }
            if (b.ContainsKey("paymentInfo"))
            {
                var pi = (Web.Str(b, "paymentInfo", 400) ?? "").Trim();
                if (pi.Length < 5) { Web.Error(res, 400, "bad_payment_info", "اكتب طريقة الدفع اللي هتظهر لأصحاب المتاجر."); return; }
                Billing.Set(c, "PaymentInfo", pi);
            }
            if (b.ContainsKey("payNumber"))
            {
                var pn = (Web.Str(b, "payNumber", 40) ?? "").Trim();
                if (pn.Count(char.IsDigit) < 8) { Web.Error(res, 400, "bad_pay_number", "اكتب رقم التحويل صح، مثلًا 01001234567."); return; }
                Billing.Set(c, "PayNumber", pn);
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
                    // InstaPay's QR: only for a signed-in store owner (it's on the plan request form)
                    if (!post && req.QueryString["payqr"] == "1")
                    {
                        if (Me.Require(ctx, c) == null) { Web.Error(res, 401, "not_logged_in"); return; }
                        if (!Billing.PayQrServe(c, res)) Web.Error(res, 404, "not_found");
                        return;
                    }
                    if (post || req.QueryString["mine"] == "1") { Mine(ctx, c, post); return; }
                    var wa = Billing.WhatsApp(c);
                    Web.Json(res, 200, new Dictionary<string, object>
                    {
                        { "plans", Billing.Plans(c, false).Select(p => Billing.PlanJson(c, p, false)).ToList() },
                        { "whatsapp", wa }, { "wa", Billing.WaNumber(wa) },
                        { "units", new Dictionary<string, object> { { "small", Billing.Units(c, "UnitsSmall") }, { "medium", Billing.Units(c, "UnitsMedium") }, { "large", Billing.Units(c, "UnitsLarge") } } },
                        { "paymentInfo", Billing.Get(c, "PaymentInfo") ?? "" },
                        // where owners send the money: the number, and whether there's an InstaPay QR (its version; the picture itself is ?payqr=1)
                        { "payNumber", Billing.Get(c, "PayNumber") ?? "" }, { "payQr", Billing.PayQrVersion(c) },
                        // the lowest plan that includes each locked feature (0 = every plan), for the features list on the pricing page
                        { "gates", PlanGate.Features.ToDictionary(f => f, f => (object)PlanGate.MinPlanId(c, f)) },
                        // WhatsApp isn't in any plan: it's a service added to any plan, for this much a month
                        { "waAddon", new Dictionary<string, object> { { "price", WaAccount.ServicePrice(c) }, { "days", WaAccount.ServiceDays } } },
                        // «السعر القديم» while it's open: pay before the end of that day and the first months stay at today's price (null = off / over)
                        { "oldOffer", Billing.OldOfferJson(c) },
                    });
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error"); }
        }

        static readonly string[] Methods = { "InstaPay", "أورانج كاش", "كاش" };

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
                            { "id", r["Id"] }, { "plan", r["PlanName"] }, { "months", r["Months"] }, { "paid", r["PaidEgp"] }, { "kind", r["Kind"] as string ?? "plan" },
                            { "topup", (r["Kind"] as string) == "topup" }, { "startsAt", Billing.Iso(r["StartsAt"]) }, { "endsAt", Billing.Iso(r["EndsAt"]) } });
                // what picking each plan means for this store right now (upgrade / lower plan later / renew / new), for the plan cards and the request form
                var changes = Billing.Plans(c, false).Select(p => Billing.ChangeJson(Billing.ChangeFor(c, s, p), p)).ToList();
                // extra quota options for the running plan (none when there's no plan or it ended)
                var topups = new List<Dictionary<string, object>>();
                var cur = s.PlanId.HasValue && !Billing.Expired(s) ? Billing.Plan(c, s.PlanId.Value) : null;
                if (cur != null)
                    foreach (var pct in Billing.TopupPcts)
                    {
                        long u; var tq = Billing.TopupQuote(cur, pct, out u, Billing.PriceFor(c, s, cur));
                        topups.Add(new Dictionary<string, object> { { "pct", pct }, { "units", u }, { "total", tq.Total } });
                    }
                // extra messages worked out from this store's own pace, with a suggested number and its price
                var topupNeed = cur != null ? Billing.NeedJson(Billing.TopupNeed(c, s, cur)) : null;
                // «السعر القديم»: this store's old price (if it has one), and the offer while it's still open
                var lk = Billing.LockOf(c, s.Id);
                var priceLock = lk == null ? null : Billing.LockJson(c, lk, Billing.Plan(c, lk.PlanId));
                Web.Json(res, 200, new Dictionary<string, object> { { "requests", requests }, { "subscriptions", subs }, { "methods", Methods }, { "topups", topups }, { "changes", changes }, { "topupNeed", topupNeed },
                    { "priceLock", priceLock }, { "oldOffer", Billing.OldOfferJson(c) } });
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
            bool topup = (Web.Str(b, "kind", 10) ?? "") == "topup";
            PlanInfo p; int months; Billing.Quote q; long units = 0; string kind = topup ? "topup" : "plan";
            bool waOnly = (Web.Str(b, "kind", 10) ?? "") == "wa";
            int topupMsgs = 0; string topupCalc = null;   // extra messages: how many, and how the suggestion was worked out
            Billing.OldInfo old = null;                  // «السعر القديم»: what this plan request gets at the store's old price (kept for the admin)
            if (waOnly)
            {
                // the WhatsApp service alone, at any time: a new month, or the next one while it still runs (its own monthly cycle).
                // The request keeps the store's current plan for reference (0 = no plan).
                p = s.PlanId.HasValue ? (Billing.Plan(c, s.PlanId.Value) ?? new PlanInfo { Id = s.PlanId.Value, Name = s.PlanName ?? "" }) : new PlanInfo { Id = 0, Name = "" };
                kind = "wa"; months = 1;
                q = new Billing.Quote { Months = 1 };   // the service's price is added below
            }
            else if (topup)
            {
                // extra quota for this month: only on a running plan, priced from that plan
                if (!s.PlanId.HasValue || Billing.Expired(s)) { Web.Error(res, 400, "no_active_plan", "الرصيد الإضافي للباقات الشغالة بس. لو باقتك خلصت، جدّدها الأول."); return; }
                p = Billing.Plan(c, s.PlanId.Value);
                if (p == null) { Web.Error(res, 404, "no_plan", "باقتك مش متاحة دلوقتي، كلّمنا."); return; }
                int askMsgs = Web.Int(b, "msgs") ?? 0;
                if (askMsgs > 0)
                {
                    // a number of messages (suggested from the store's own pace, or typed), priced at the plan's rate per unit
                    var nd = Billing.TopupNeed(c, s, p);
                    if (askMsgs < nd.Min || askMsgs > nd.Max) { Web.Error(res, 400, "bad_msgs", "اختار من " + nd.Min + " لـ " + nd.Max + " رسالة."); return; }
                    units = askMsgs * nd.Per;
                    var tp = Billing.TopupPrice(nd.PricePerMsg, askMsgs);
                    q = new Billing.Quote { Months = 0, Price = tp, Discount = 0, Total = tp, Pct = 0 }; months = 0;
                    topupMsgs = askMsgs; topupCalc = Billing.NeedText(nd, askMsgs);
                }
                else
                {
                    int pct = Web.Int(b, "pct") ?? 0;   // the older choice: +25/50/100% of the plan's quota
                    if (!Billing.TopupPcts.Contains(pct)) { Web.Error(res, 400, "bad_pct", "اختار كمية الرصيد الإضافي."); return; }
                    q = Billing.TopupQuote(p, pct, out units, Billing.PriceFor(c, s, p)); months = 0;
                }
            }
            else
            {
                var planId = Web.Int(b, "planId"); months = Web.Int(b, "months") ?? 1;
                p = planId.HasValue ? Billing.Plan(c, planId.Value) : null;
                if (p == null || !p.IsActive) { Web.Error(res, 404, "no_plan", "الباقة دي مش متاحة دلوقتي."); return; }
                var ch = Billing.ChangeFor(c, s, p);
                if (ch.Blocked != null) { Web.Error(res, 409, "next_waiting", ch.Blocked); return; }
                if (ch.Kind == "upgrade")
                {
                    // a higher plan from now on: same end date, the price difference for the days left
                    kind = "upgrade"; months = 0;
                    q = new Billing.Quote { Months = 0, Price = ch.Amount, Discount = 0, Total = ch.Amount, Pct = 0 };
                    old = Billing.OldFor(c, s, p, 0);   // before the cut-off, the higher plan's price now becomes its old price
                }
                else
                {
                    if (!Billing.Periods.Contains(months)) { Web.Error(res, 400, "bad_months", "اختار مدة: شهر أو 4 أو 6 أو 12 شهر."); return; }
                    if (!Billing.OpenPeriods.Contains(months)) { Web.Error(res, 400, "period_soon", "الاشتراك لأكتر من شهر هيكون متاح قريب. اختار شهر دلوقتي."); return; }
                    old = Billing.OldFor(c, s, p, months);
                    q = Billing.QuoteFor(c, p, months, old);
                }
            }
            // the WhatsApp service: asked for with a plan (wa: true) or alone (kind "wa"). Its own monthly price, no period discount,
            // and the number the owner will link, so the admin can get a number ready on Wapilot
            object waAsk; bool wantWa = waOnly || (!topup && b.TryGetValue("wa", out waAsk) && waAsk is bool && (bool)waAsk);
            string waPhone = null; decimal waEgp = 0;
            // renewing the plan never renews the service: while it runs, it's renewed alone (kind "wa") on its own date
            if (wantWa && !waOnly && WaAccount.HasService(s) && s.WaUntil.HasValue)
            { Web.Error(res, 400, "wa_running", "خدمة الواتساب شغالة عندك لحد يوم " + s.WaUntil.Value.ToString("yyyy-MM-dd") + "، وتجديدها شهري لوحده. ابعت طلب الباقة من غيرها."); return; }
            if (wantWa)
            {
                waPhone = WaChat.Phone(Web.Str(b, "waPhone", 40));
                if (waPhone == null) { Web.Error(res, 400, "bad_wa_phone", "اكتب رقم الواتساب اللي هتربطه بالمساعد، مثلًا 01001234567."); return; }
                waEgp = WaAccount.ServicePrice(c);   // one month, whatever the plan's period: the service renews monthly on its own
                q.Price += waEgp; q.Total += waEgp;
            }
            // nothing to pay (a plan at the same price): no transfer to check
            bool free = q.Total <= 0;
            string method = (Web.Str(b, "method", 40) ?? "").Trim();
            if (!free && !Methods.Contains(method)) { Web.Error(res, 400, "bad_method", "اختار طريقة الدفع."); return; }
            string refNo = (Web.Str(b, "ref", 100) ?? "").Trim();
            if (!free && refNo.Length < 3) { Web.Error(res, 400, "need_ref", "اكتب رقم العملية أو الرقم اللي حوّلت منه، عشان نقدر نتأكد من الدفع."); return; }
            if (free) { method = Methods.Contains(method) ? method : "—"; refNo = refNo.Length > 0 ? refNo : "—"; }
            // the reference number InstaPay shows after the transfer: required, and one payment backs one request only
            string payRef = Billing.NormRef(Web.Str(b, "refNo", 80));
            if (!free && !Billing.RefLooksRight(payRef)) { Web.Error(res, 400, "need_refno", "اكتب الرقم المرجعي اللي ظهرلك في InstaPay بعد التحويل (من 6 لـ 30 حرف أو رقم)."); return; }
            if (!free && Billing.RefUsed(c, payRef)) { Web.Error(res, 409, "ref_used", Billing.RefUsedMessage); return; }
            string note = (Web.Str(b, "note", 500) ?? "").Trim();
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.PlanRequests WHERE StoreId=@s AND Status='pending'", "@s", s.Id))
                if ((int)cmd.ExecuteScalar() > 0) { Web.Error(res, 409, "has_pending", "عندك طلب لسه بيتراجع. استنى الرد عليه، أو الغيه وابعت طلب جديد."); return; }

            // what the request means right now, kept for the admin's cards and the store's history
            string what = kind == "topup" ? "topup" : kind == "upgrade" ? "upgrade" : kind == "wa" ? (s.WaUntil.HasValue ? "wa-renew" : "wa-new") : Billing.ChangeFor(c, s, p).Kind;
            int newId;
            try
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.PlanRequests(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, AmountEgp, PaymentMethod, PaymentRef, Note, Kind, Units, WaAddon, WaEgp, WaPhone, RefNo, What, Msgs, Calc, OldPrice, OldLeft)
                                             OUTPUT INSERTED.Id VALUES (@s, @p, @n, @m, @price, @disc, @amt, @method, @ref, @note, @kind, @units, @wa, @waEgp, @waPhone, @refNo, @what, @msgs, @calc, @oldP, @oldL)",
                    "@oldP", old != null ? (object)old.Price : DBNull.Value, "@oldL", old != null ? (object)old.LeftAfter : DBNull.Value,
                    "@s", s.Id, "@p", p.Id, "@n", p.Name, "@m", months, "@price", q.Price, "@disc", q.Discount, "@amt", q.Total,
                    "@wa", wantWa, "@waEgp", wantWa ? (object)waEgp : DBNull.Value, "@waPhone", (object)waPhone ?? DBNull.Value,
                    "@what", what, "@msgs", topupMsgs > 0 ? (object)topupMsgs : DBNull.Value, "@calc", (object)topupCalc ?? DBNull.Value,
                    "@method", method, "@ref", refNo, "@note", note.Length == 0 ? (object)DBNull.Value : note,
                    "@kind", kind, "@units", topup ? (object)units : DBNull.Value, "@refNo", free ? DBNull.Value : (object)payRef)) newId = (int)cmd.ExecuteScalar();
            }
            // two requests with the same reference at the same moment: the unique index lets only one in
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { Web.Error(res, 409, "ref_used", Billing.RefUsedMessage); return; }
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
