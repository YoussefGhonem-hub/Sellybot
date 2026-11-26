// The last part of the shipping work, four features, each with its own switch in «متجري» (off by default; off = nothing changes):
//   ShopShipDay.cs    «ميعاد التوصيل»: the customer picks the delivery day at checkout, from the days the owner set
//   ShopShipRate.cs   shipping by weight or city: another fee for some cities, and kilos above what the fee includes
//   ShopShipBack.cs   «المندوب يجيب المرتجع»: a return or exchange shipment with the company, from the return request
//   ShopShipCash.cs   «مراجعة فلوس التحصيل»: what the couriers collected against what the company paid, by statement or by hand
// This file: the tables, the settings (Settings.ship2, cleaned here), the owner's calls, and what the storefront is told.
// The owner's settings: ship2.dday {on, wd, govs, from, to, cut, max, parts, must}, ship2.srate {on, w, cities, inc, per, zones},
// ship2.sback {on}, ship2.scash {on, days}.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class ShopShip2
    {
        public const string Schema = @"
-- the day the customer chose (ShopOrders.Extras keeps it for showing; this row is what counts a full day)
IF OBJECT_ID('dbo.ShopOrderDays') IS NULL
BEGIN
  CREATE TABLE dbo.ShopOrderDays(OrderId BIGINT NOT NULL PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OnDay DATE NOT NULL, Part NVARCHAR(4) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopOrderDays_Day ON dbo.ShopOrderDays(StoreId, OnDay);
END
-- a size or colour's own weight in grams (the product's is ShopProducts.Weight, grams too)
IF OBJECT_ID('dbo.ShopWeights') IS NULL
BEGIN
  CREATE TABLE dbo.ShopWeights(VariantId INT NOT NULL PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Grams INT NOT NULL);
  CREATE INDEX IX_ShopWeights_Store ON dbo.ShopWeights(StoreId);
END
-- a return pickup is a row of Shipments (Source 'ret', OrderId = the order) and this one: which request, what kind, what is collected
IF OBJECT_ID('dbo.ShipBacks') IS NULL
BEGIN
  CREATE TABLE dbo.ShipBacks(ShipmentId BIGINT NOT NULL PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ReturnId INT NOT NULL, OrderId BIGINT NOT NULL, Kind NVARCHAR(8) NOT NULL, Collect DECIMAL(12,2) NOT NULL DEFAULT 0,
    MadeAt DATETIME2 NULL, TellDue DATETIME2 NULL, ToldAt DATETIME2 NULL, TellNote NVARCHAR(200) NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShipBacks_Return ON dbo.ShipBacks(StoreId, ReturnId);
END
-- a delivered cash shipment's money: collected (the company's notification or its statement) and paid to the owner (a statement or by hand)
IF OBJECT_ID('dbo.ShipCash') IS NULL
BEGIN
  CREATE TABLE dbo.ShipCash(ShipmentId BIGINT NOT NULL PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Got DECIMAL(12,2) NULL, GotSrc NVARCHAR(6) NULL, Paid DECIMAL(12,2) NULL, PaidOn DATE NULL, PaidSrc NVARCHAR(6) NULL, FileId INT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShipCash_Store ON dbo.ShipCash(StoreId);
END
IF OBJECT_ID('dbo.ShipCashFiles') IS NULL
  CREATE TABLE dbo.ShipCashFiles(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Company NVARCHAR(10) NOT NULL, Name NVARCHAR(120) NULL, Lines INT NOT NULL, Hit INT NOT NULL, Amount DECIMAL(14,2) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
";

        // ---------- small things ----------
        internal static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        internal static void Ok(HttpResponse res, params object[] kv) { var d = D(kv); d["ok"] = true; Web.Json(res, 200, d); }
        internal static string Str(Dictionary<string, object> b, string k, int max = 200) { return ShopJson.Str(ShopJson.At(b, k), max); }
        internal static int Int(Dictionary<string, object> b, string k, int d = 0) { return ShopJson.Int(ShopJson.At(b, k), d); }
        internal static int Clamp(int v, int a, int b) { return Math.Max(a, Math.Min(b, v)); }
        internal static decimal Clamp(decimal v, decimal a, decimal b) { return Math.Max(a, Math.Min(b, v)); }
        internal static List<int> Ints(object v, int a, int b, int max) { return ShopJson.List(v).Select(x => ShopJson.Int(x, -1)).Where(x => x >= a && x <= b).Distinct().Take(max).OrderBy(x => x).ToList(); }
        public static Dictionary<string, object> Of(Shop sh, string k) { return ShopJson.Sub(ShopJson.Sub(sh == null ? null : sh.Settings, "ship2"), k); }
        public static bool On(Shop sh, string k) { return sh != null && ShopJson.Bool(ShopJson.At(Of(sh, k), "on")); }
        internal static string Iso(DateTime? t) { return t.HasValue ? DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToString("o") : ""; }

        // ShopLook.CleanSettings: ship2 as the owner sent it over what was there (each part replaced whole when sent)
        public static Dictionary<string, object> Clean(Dictionary<string, object> x)
        {
            var bk = ShopJson.Sub(x, "sback"); var cs = ShopJson.Sub(x, "scash");
            return D("dday", ShopShipDay.Clean(ShopJson.Sub(x, "dday")), "srate", ShopShipRate.Clean(ShopJson.Sub(x, "srate")), "sback", D("on", ShopJson.Bool(ShopJson.At(bk, "on"))),
                "scash", D("on", ShopJson.Bool(ShopJson.At(cs, "on")), "days", Clamp(ShopJson.Int(ShopJson.At(cs, "days"), 14), 3, 90)));
        }

        // BOOT.shop.ship2 (ShopFront.ShopJsonFor): null = nothing on, the page loads nothing more
        public static object Boot(ShopFrontHandler.Ctx x)
        {
            try
            {
                var day = ShopShipDay.Boot(x.Row); var rate = ShopShipRate.Boot(x.Data);
                if (day == null && rate == null) return null;
                return D("day", day, "rate", rate, "v", ShopPages.Ver("ship2.js"));
            }
            catch (Exception ex) { Log.Error(ex); return null; }
        }

        // the carriers' parcel: the day the customer chose, and the real weight (Carriers.SendOne)
        public static void Parcel(SqlConnection c, Shop sh, ShopOrder o, CarrierParcel p)
        {
            try
            {
                string day = ShopJson.Str(ShopJson.At(o.Extras, "day"), 10), part = ShopJson.Str(ShopJson.At(o.Extras, "dpart"), 4);
                if (day.Length == 10) { p.Day = day; p.Part = part.Length == 0 ? null : part; var t = "التوصيل يوم " + ShopShipDay.Text(day, false) + (p.Part == null ? "" : " " + ShopShipDay.PartText(p.Part, false)); p.Note = p.Note.Length == 0 ? t : t + " - " + p.Note; }
                var d = ShopCache.Get(c, sh); if (d != null) p.Kg = ShopShipRate.OrderKg(c, d, o);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // ---------- the owner's calls (ShopDesk passes here what Carriers didn't take) ----------
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (a.StartsWith("sday", StringComparison.Ordinal)) return ShopShipDay.Get(c, s, sh, a, req, res);
            if (a.StartsWith("srate", StringComparison.Ordinal)) return ShopShipRate.Get(c, s, sh, a, req, res);
            if (a.StartsWith("scash", StringComparison.Ordinal)) return ShopShipCash.Get(c, s, sh, a, req, res);
            if (a.StartsWith("sback", StringComparison.Ordinal)) return ShopShipBack.Get(c, s, sh, a, req, res);
            return false;
        }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null;
            if (a.StartsWith("srate", StringComparison.Ordinal)) return ShopShipRate.Post(c, s, sh, a, b, res);
            if (a.StartsWith("scash", StringComparison.Ordinal)) return ShopShipCash.Post(c, s, sh, a, b, res);
            if (a.StartsWith("sback", StringComparison.Ordinal)) return ShopShipBack.Post(c, s, sh, a, b, res, out later);
            return false;
        }
    }
}
