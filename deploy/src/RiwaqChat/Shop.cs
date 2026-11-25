// SellyStore: a merchant with no website rents a ready store from Sellybot (the plan, 2026-11-25). Its address is
// <name>.sellystore.online: a Cloudflare Worker (deploy/store-worker) takes the name from the address and asks this site for the
// page (ShopFront.cs). Products, orders, shipping and offers are here, so the store, the dashboard and the assistant read the same rows.
// This file: the tables, the store's row (Shops), its name in the link, who may have one, and its settings.
//   ShopData.cs   the catalog in memory (per store, per version)      ShopPrice.cs  offers, coupons, the cart's sum
//   ShopOrders.cs orders, stock, statuses                             ShopFront.cs  what the Worker asks for
//   ShopOwner.cs  the owner's API (api/shop.ashx)                     ShopImages.cs pictures
// Nothing shows to merchants until the admin opens it (AppSettings ShopOn) or adds a store to the testers (ShopTesters).
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RiwaqChat
{
    // A message for the owner or the customer: shown as it is (400).
    public class ShopException : Exception
    {
        public readonly string Code; public readonly object Extra;
        public ShopException(string code, string message, object extra = null) : base(message) { Code = code; Extra = extra; }
    }

    public class Shop
    {
        public int StoreId, Ver, OrderNo;
        public string Slug, Name, NameEn, Biz, Status, ReviewNote, CloseNote, PendSlug, PendName;
        public DateTime? RentUntil, SubmittedAt, ApprovedAt, PublishedAt, ClosedAt, DraftAt;
        public DateTime CreatedAt;
        public string DraftJson, LiveJson, SettingsJson;
        public bool ReviewChange;   // a live store whose name / link / logo change waits for the admin

        Dictionary<string, object> set;
        public Dictionary<string, object> Settings { get { return set ?? (set = ShopJson.Obj(SettingsJson)); } }
        public Dictionary<string, object> Look(bool draft) { return ShopJson.Obj(draft ? (DraftJson ?? LiveJson) : LiveJson); }
        public string Url { get { return Shops.Address(Slug); } }
    }

    // Reading the JSON kept in a column (JavaScriptSerializer gives Dictionary / ArrayList), never throwing on a missing key.
    public static class ShopJson
    {
        public static Dictionary<string, object> Obj(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
            try { return Web.Js.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }
        public static List<object> Arr(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<object>();
            try { return Web.Js.Deserialize<List<object>>(json) ?? new List<object>(); }
            catch { return new List<object>(); }
        }
        public static object At(Dictionary<string, object> d, params string[] path)
        {
            object v = d;
            foreach (var p in path)
            {
                var o = v as Dictionary<string, object>;
                if (o == null || !o.TryGetValue(p, out v)) return null;
            }
            return v;
        }
        public static Dictionary<string, object> Sub(Dictionary<string, object> d, string key) { return At(d, key) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static List<object> List(object v)
        {
            if (v is ArrayList) return ((ArrayList)v).Cast<object>().ToList();
            if (v is object[]) return ((object[])v).ToList();
            return v as List<object> ?? new List<object>();
        }
        public static string Str(object v, int max = 400) { var s = v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture).Trim(); return s.Length > max ? s.Substring(0, max) : s; }
        public static bool Bool(object v, bool dflt = false) { return v is bool ? (bool)v : v == null ? dflt : Str(v) == "true" || Str(v) == "1"; }
        public static int Int(object v, int dflt = 0) { decimal n; return decimal.TryParse(Str(v), NumberStyles.Number, CultureInfo.InvariantCulture, out n) && Math.Abs(n) < 2000000000 ? (int)n : dflt; }
        public static decimal Dec(object v, decimal dflt = 0) { decimal n; return decimal.TryParse(Digits(Str(v)), NumberStyles.Number, CultureInfo.InvariantCulture, out n) && Math.Abs(n) < 100000000 ? Math.Round(n, 2) : dflt; }
        public static decimal? DecN(object v) { decimal n; return v != null && decimal.TryParse(Digits(Str(v)), NumberStyles.Number, CultureInfo.InvariantCulture, out n) && Math.Abs(n) < 100000000 ? Math.Round(n, 2) : (decimal?)null; }
        // ١٢٣ -> 123, and the Arabic decimal and thousands marks
        public static string Digits(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
                if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
                else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
                else if (ch == '٫') sb.Append('.');
                else if (ch == '٬' || ch == '،') sb.Append(',');
                else sb.Append(ch);
            return sb.ToString();
        }
        public static string Write(object o) { return Web.Js.Serialize(o); }
    }

    public static class Shops
    {
        public const string Feature = "shop";
        public const int GraceDays = 3, KeepDays = 90, RentDays = 30;
        public const string ServiceName = "متجر SellyStore";

        public static bool Ready;   // the tables are there (a failure here never stops the rest of the site)
        public static void Ensure(SqlConnection c)
        {
            try { using (var cmd = new SqlCommand(Schema, c)) { cmd.CommandTimeout = 120; cmd.ExecuteNonQuery(); } Ready = true; }
            catch (Exception ex) { Log.Error(ex); Ready = false; }
        }

        // ---------- addresses ----------
        // Settings ShopDomain / ShopScheme are for the local tests; live it's https://<name>.sellystore.online
        public static string Domain { get { return Web.Setting("ShopDomain", "sellystore.online"); } }
        public static string Scheme { get { return Web.Setting("ShopScheme", "https"); } }
        public static string Address(string slug) { return Scheme + "://" + slug + "." + Domain; }
        public static string ImgBase(SqlConnection c) { return Scheme + "://img." + Domain; }

        // ---------- who may have a store ----------
        // ShopOn = open to every store · ShopTesters = slugs that see it while it's closed (the trial's merchants)
        public static bool On(SqlConnection c) { return Ready && Billing.Get(c, "ShopOn") == "1"; }
        public static bool Tester(SqlConnection c, StoreInfo s)
        {
            return s != null && (Billing.Get(c, "ShopTesters") ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries).Contains(s.Slug, StringComparer.OrdinalIgnoreCase);
        }
        // for the dashboard: null = not shown to this store; n = orders waiting for the owner
        public static object MeJson(SqlConnection c, StoreInfo s)
        {
            if (!Visible(c, s)) return null;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Status IN ('new','noreply')", "@s", s.Id)) return new Dictionary<string, object> { { "n", (int)cmd.ExecuteScalar() } };
        }
        public static bool Visible(SqlConnection c, StoreInfo s) { return Ready && s != null && (On(c) || Tester(c, s) || (s.IsAdmin && Billing.Get(c, "ShopAdmin") == "1")); }

        // The rent: its own monthly subscription on any plan, like WhatsApp's, never part of the free trial (it has its own end date).
        // A plan that includes the store (Plans.IncludesShop) turns it on while that plan runs.
        public static bool InPlan(SqlConnection c, StoreInfo s)
        {
            if (s == null || !s.PlanId.HasValue || Billing.Expired(s)) return false;
            return PlanIncludes(c, s.PlanId.Value);
        }
        static HashSet<int> planSet; static DateTime planAt;
        public static bool PlanIncludes(SqlConnection c, int planId)
        {
            if (!Ready) return false;
            var set = planSet;
            if (set == null || DateTime.UtcNow - planAt > TimeSpan.FromMinutes(1))
            {
                set = new HashSet<int>();
                using (var cmd = Db.Cmd(c, "SELECT Id FROM dbo.Plans WHERE IncludesShop = 1")) using (var r = cmd.ExecuteReader()) while (r.Read()) set.Add((int)r[0]);
                planSet = set; planAt = DateTime.UtcNow;
            }
            return set.Contains(planId);
        }
        public static void ForgetPlans() { planSet = null; }

        public static DateTime? Until(SqlConnection c, StoreInfo s, Shop sh)
        {
            DateTime? u = sh == null ? null : sh.RentUntil;
            if (InPlan(c, s) && s.PlanEndsAt.HasValue && (!u.HasValue || s.PlanEndsAt.Value > u.Value)) u = s.PlanEndsAt;
            return u;
        }
        public static bool HasService(SqlConnection c, StoreInfo s, Shop sh)
        {
            if (s == null) return false;
            if (s.IsAdmin) return true;
            var u = Until(c, s, sh);
            return u.HasValue && u.Value > DateTime.UtcNow;
        }
        // the rent ended, but the store stays open a few more days
        public static bool InGrace(SqlConnection c, StoreInfo s, Shop sh)
        {
            var u = Until(c, s, sh); var now = DateTime.UtcNow;
            return u.HasValue && u.Value <= now && u.Value.AddDays(GraceDays) > now;
        }
        // customers can open it
        public static bool Open(SqlConnection c, StoreInfo s, Shop sh)
        {
            return sh != null && s != null && sh.Status == "live" && s.Status == "active" && (HasService(c, s, sh) || InGrace(c, s, sh));
        }

        // ---------- the Worker's secret ----------
        // Made here, kept encrypted, shown to the admin to paste into the Worker's settings (SHOP_KEY). A page is never given without it.
        static string keyCache; static DateTime keyAt;
        public static string Key(SqlConnection c, bool create = false)
        {
            if (keyCache != null && DateTime.UtcNow - keyAt < TimeSpan.FromMinutes(1)) return keyCache;
            var box = Billing.Get(c, "ShopKey");
            var k = string.IsNullOrEmpty(box) ? null : Secrets.Unprotect(box);
            if (k == null && create) { k = Token(32); Billing.Set(c, "ShopKey", Secrets.Protect(k)); }
            keyCache = k; keyAt = DateTime.UtcNow;
            return k;
        }
        public static string NewKey(SqlConnection c) { var k = Token(32); Billing.Set(c, "ShopKey", Secrets.Protect(k)); keyCache = k; keyAt = DateTime.UtcNow; return k; }
        public static string KnownKey() { return keyCache; }
        public static bool SameKey(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
        public static string Token(int bytes)
        {
            var b = new byte[bytes]; using (var g = RandomNumberGenerator.Create()) g.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")));
        }

        // ---------- the name in the link ----------
        static readonly HashSet<string> Names = new HashSet<string>(("www admin api app dashboard pricing support img cdn static assets mail email smtp ftp ns shop store stores pay payment payments checkout login signin signup " +
            "account accounts help status blog docs dev test demo staging uat beta sellybot sellystore selly seller root official secure security verify verification bank banks gov egypt police ministry").Split(' '));
        // whole words of the name that are a bank, a payment company, a known brand or a public body
        static readonly HashSet<string> Brands = new HashSet<string>(("cib nbe qnb hsbc aaib adib fab ebank we visa mada meeza bm aib nbk bnp citi citibank alexbank banquemisr banque misr nike adidas puma zara gucci prada chanel dior hermes rolex " +
            "omega apple iphone samsung huawei oppo xiaomi realme sony lg dell hp lenovo toshiba amazon noon jumia souq shein temu aliexpress google microsoft meta facebook instagram whatsapp tiktok snapchat twitter telegram youtube netflix " +
            "paypal fawry instapay vodafone orange etisalat telecom shopify easyorders wuilt bosta aramex dhl fedex mylerz shipblu ikea carrefour lego disney barbie hm pandora cartier fendi versace balenciaga converse vans rayban loreal maybelline nivea " +
            "pfizer bayer moi mof moh passport tax customs nid").Split(' '));
        // long names that are caught anywhere inside the name
        static readonly string[] BrandParts = { "vodafone", "instapay", "facebook", "instagram", "whatsapp", "paypal", "alexbank", "banquemisr", "sellybot", "sellystore", "easyorders", "shopify", "mastercard", "microsoft", "aliexpress" };

        // null = good; otherwise why not (for the owner)
        public static string BadSlug(string slug, bool admin = false)
        {
            if (string.IsNullOrEmpty(slug) || !Web.SlugRx.IsMatch(slug)) return "الاسم لازم يكون من 3 لـ 30 حرف إنجليزي صغير أو رقم أو شرطة، ومايبدأش أو يخلص بشرطة.";
            if (slug.StartsWith("xn--", StringComparison.Ordinal) || slug.Contains("--")) return "الاسم ده مش مسموح. ماتكررش الشرطة.";
            if (admin) return null;
            if (Names.Contains(slug) || Web.Reserved(slug)) return "الاسم ده محجوز للموقع. اختار اسم تاني.";
            if (slug.Split('-').Any(w => Brands.Contains(w)) || BrandParts.Any(slug.Contains)) return "الاسم ده فيه اسم بنك أو ماركة أو جهة رسمية، فمش هينفع. اختار اسم متجرك انت.";
            return null;
        }

        public static string Slugify(string text, int max = 60)
        {
            // Arabic names give a readable Latin link
            var map = new Dictionary<char, string> { { 'ا', "a" }, { 'أ', "a" }, { 'إ', "e" }, { 'آ', "a" }, { 'ب', "b" }, { 'ت', "t" }, { 'ث', "th" }, { 'ج', "g" }, { 'ح', "h" }, { 'خ', "kh" }, { 'د', "d" }, { 'ذ', "z" }, { 'ر', "r" }, { 'ز', "z" },
                { 'س', "s" }, { 'ش', "sh" }, { 'ص', "s" }, { 'ض', "d" }, { 'ط', "t" }, { 'ظ', "z" }, { 'ع', "a" }, { 'غ', "gh" }, { 'ف', "f" }, { 'ق', "q" }, { 'ك', "k" }, { 'ل', "l" }, { 'م', "m" }, { 'ن', "n" }, { 'ه', "h" }, { 'ة', "a" },
                { 'و', "o" }, { 'ي', "y" }, { 'ى', "a" }, { 'ئ', "e" }, { 'ؤ', "o" }, { 'ء', "" }, { 'پ', "p" }, { 'چ', "ch" }, { 'ڤ', "v" }, { 'گ', "g" } };
            var sb = new StringBuilder();
            foreach (var ch in ShopJson.Digits((text ?? "").ToLowerInvariant()))
            {
                string m;
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else if (map.TryGetValue(ch, out m)) sb.Append(m);
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_' || ch == '/' || ch == '.' || ch == '+' || ch == '&') sb.Append('-');
            }
            var s = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (s.Length > max) s = s.Substring(0, max).Trim('-');
            return s;
        }

        // ---------- the store's row ----------
        public static Shop ByStore(SqlConnection c, int storeId) { return Ready ? One(c, "WHERE StoreId=@v", storeId) : null; }
        public static Shop BySlug(SqlConnection c, string slug) { return Ready ? One(c, "WHERE Slug=@v", slug) : null; }

        static Shop One(SqlConnection c, string where, object v)
        {
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 * FROM dbo.Shops " + where, "@v", v))
            using (var r = cmd.ExecuteReader()) return r.Read() ? Read(r) : null;
        }
        public static List<Shop> All(SqlConnection c)
        {
            var list = new List<Shop>(); if (!Ready) return list;
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.Shops ORDER BY CreatedAt DESC")) using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(Read(r));
            return list;
        }
        static DateTime? Dt(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }
        static Shop Read(IDataRecord r)
        {
            return new Shop
            {
                StoreId = (int)r["StoreId"], Slug = (string)r["Slug"], Name = (string)r["Name"], NameEn = r["NameEn"] as string, Biz = r["Biz"] as string, Status = (string)r["Status"],
                Ver = (int)r["Ver"], OrderNo = (int)r["OrderNo"], RentUntil = Dt(r["RentUntil"]), DraftJson = r["Draft"] as string, LiveJson = r["Live"] as string, SettingsJson = r["Settings"] as string,
                ReviewNote = r["ReviewNote"] as string, CloseNote = r["CloseNote"] as string, PendSlug = r["PendSlug"] as string, PendName = r["PendName"] as string,
                ReviewChange = (bool)r["ReviewChange"], DraftAt = Dt(r["DraftAt"]),
                CreatedAt = DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc), SubmittedAt = Dt(r["SubmittedAt"]), ApprovedAt = Dt(r["ApprovedAt"]), PublishedAt = Dt(r["PublishedAt"]), ClosedAt = Dt(r["ClosedAt"]),
            };
        }

        // a store's first visit to «متجري»: the row, the pages with ready text, and the first look (a draft)
        public static Shop Create(SqlConnection c, StoreInfo s)
        {
            var sh = ByStore(c, s.Id);
            if (sh != null) return sh;
            string slug = FreeSlug(c, s.Slug, s.Id, s.IsAdmin);
            try
            {
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Shops(StoreId, Slug, Name, Draft, Settings) VALUES(@s, @slug, @n, @d, @set)", "@s", s.Id, "@slug", slug,
                    "@n", s.Name, "@d", ShopJson.Write(ShopLook.Start(s)), "@set", ShopJson.Write(ShopLook.StartSettings(s)))) cmd.ExecuteNonQuery();
                ShopLook.StartPages(c, s);
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }   // two tabs opened it at once
            return ByStore(c, s.Id);
        }

        public static string FreeSlug(SqlConnection c, string wanted, int storeId, bool admin)
        {
            var b = Slugify(wanted, 26);
            if (BadSlug(b, admin) != null) b = "store-" + storeId;
            for (int i = 0; i < 30; i++)
            {
                var t = i == 0 ? b : b + "-" + (i + 1);
                if (BadSlug(t, admin) == null && SlugFree(c, t, storeId)) return t;
            }
            return "store-" + storeId + "-" + Token(2);
        }
        public static bool SlugFree(SqlConnection c, string slug, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Shops WHERE (Slug=@v OR PendSlug=@v) AND StoreId<>@s", "@v", slug, "@s", storeId)) return (int)cmd.ExecuteScalar() == 0;
        }

        // Every change customers or the assistant can see: a new version, so the copy in memory and the catalog's address change.
        public static void Bump(SqlConnection c, int storeId, SqlTransaction tx = null)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET Ver=Ver+1 WHERE StoreId=@s", "@s", storeId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
            ShopCache.Touch(storeId);
        }

        public static void SaveSettings(SqlConnection c, Shop sh, Dictionary<string, object> set)
        {
            sh.SettingsJson = ShopJson.Write(set);
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET Settings=@v WHERE StoreId=@s", "@v", sh.SettingsJson, "@s", sh.StoreId)) cmd.ExecuteNonQuery();
            Bump(c, sh.StoreId);
        }

        // Limits per store (the plan: limits with a warning before they're reached)
        public const int MaxProducts = 1000, MaxVariants = 100, MaxImages = 8, MaxCategories = 120, MaxOffers = 60, MaxCoupons = 200, MaxPages = 30;

        const string Schema = @"
IF OBJECT_ID('dbo.Shops') IS NULL
BEGIN
  CREATE TABLE dbo.Shops(
    StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Slug NVARCHAR(30) NOT NULL, Name NVARCHAR(80) NOT NULL, NameEn NVARCHAR(80) NULL, Biz NVARCHAR(20) NULL,
    Status NVARCHAR(10) NOT NULL DEFAULT 'draft',            -- draft / review / live / closed
    RentUntil DATETIME2 NULL,
    Draft NVARCHAR(MAX) NULL, Live NVARCHAR(MAX) NULL,       -- the look: template, colors, sections, footer (ShopLook.cs)
    DraftAt DATETIME2 NULL,                                  -- the draft has changes that aren't published
    Settings NVARCHAR(MAX) NULL,                             -- contact, legal details, shipping, order rules, pixels
    Ver INT NOT NULL DEFAULT 1, StockVer INT NOT NULL DEFAULT 1, OrderNo INT NOT NULL DEFAULT 1000,
    ReviewNote NVARCHAR(400) NULL, CloseNote NVARCHAR(400) NULL, PendSlug NVARCHAR(30) NULL, PendName NVARCHAR(80) NULL, ReviewChange BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), SubmittedAt DATETIME2 NULL, ApprovedAt DATETIME2 NULL, PublishedAt DATETIME2 NULL, ClosedAt DATETIME2 NULL,
    RemindAt DATETIME2 NULL, LapseAt DATETIME2 NULL, WipeWarnAt DATETIME2 NULL,
    BotTest NVARCHAR(MAX) NULL, BotTestAt DATETIME2 NULL, BotScore INT NULL);
  CREATE UNIQUE INDEX UX_Shops_Slug ON dbo.Shops(Slug);
END
IF OBJECT_ID('dbo.ShopVersions') IS NULL
BEGIN
  CREATE TABLE dbo.ShopVersions(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Look NVARCHAR(MAX) NOT NULL, Note NVARCHAR(200) NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopVersions_Store ON dbo.ShopVersions(StoreId, Id);
END
IF OBJECT_ID('dbo.ShopPreviews') IS NULL
  CREATE TABLE dbo.ShopPreviews(Token NVARCHAR(64) NOT NULL PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Kind NVARCHAR(10) NOT NULL DEFAULT 'own', ExpiresAt DATETIME2 NOT NULL);
IF OBJECT_ID('dbo.ShopCategories') IS NULL
BEGIN
  CREATE TABLE dbo.ShopCategories(
    Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    ParentId INT NULL, Slug NVARCHAR(60) NOT NULL, Name NVARCHAR(80) NOT NULL, NameEn NVARCHAR(80) NULL, Descr NVARCHAR(1000) NULL, DescrEn NVARCHAR(1000) NULL,
    Image NVARCHAR(160) NULL, Cover NVARCHAR(160) NULL, Sort INT NOT NULL DEFAULT 0, Hidden BIT NOT NULL DEFAULT 0, InMenu BIT NOT NULL DEFAULT 1, InHome BIT NOT NULL DEFAULT 1,
    MetaTitle NVARCHAR(120) NULL, MetaDesc NVARCHAR(300) NULL, SizeGuide NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShopCategories_Slug ON dbo.ShopCategories(StoreId, Slug);
END
IF OBJECT_ID('dbo.ShopProducts') IS NULL
BEGIN
  CREATE TABLE dbo.ShopProducts(
    Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Slug NVARCHAR(80) NOT NULL, Name NVARCHAR(150) NOT NULL, NameEn NVARCHAR(150) NULL, Descr NVARCHAR(MAX) NULL, DescrEn NVARCHAR(MAX) NULL,
    Price DECIMAL(12,2) NOT NULL, OldPrice DECIMAL(12,2) NULL, Cost DECIMAL(12,2) NULL, Sku NVARCHAR(60) NULL, Unit NVARCHAR(20) NULL, Brand NVARCHAR(60) NULL, Tags NVARCHAR(300) NULL,
    MinQty INT NOT NULL DEFAULT 1, MaxQty INT NULL, Track BIT NOT NULL DEFAULT 1, OutMode NVARCHAR(6) NOT NULL DEFAULT 'show',   -- hide / show / order
    Status NVARCHAR(8) NOT NULL DEFAULT 'active',            -- active / hidden
    Options NVARCHAR(MAX) NULL, Specs NVARCHAR(MAX) NULL, SizeGuide NVARCHAR(MAX) NULL, Extra NVARCHAR(MAX) NULL,
    MetaTitle NVARCHAR(120) NULL, MetaDesc NVARCHAR(300) NULL, Sold INT NOT NULL DEFAULT 0, Sort INT NOT NULL DEFAULT 0, Feat BIT NOT NULL DEFAULT 0, Weight INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShopProducts_Slug ON dbo.ShopProducts(StoreId, Slug);
END
-- one row per size / color; a product without options has one row with no option values
IF OBJECT_ID('dbo.ShopVariants') IS NULL
BEGIN
  CREATE TABLE dbo.ShopVariants(
    Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL,
    Opts NVARCHAR(300) NOT NULL DEFAULT N'', Price DECIMAL(12,2) NULL, OldPrice DECIMAL(12,2) NULL, Cost DECIMAL(12,2) NULL, Stock INT NOT NULL DEFAULT 0,
    Sku NVARCHAR(60) NULL, Image NVARCHAR(160) NULL, Active BIT NOT NULL DEFAULT 1, Sort INT NOT NULL DEFAULT 0);
  CREATE UNIQUE INDEX UX_ShopVariants_Opts ON dbo.ShopVariants(ProductId, Opts);
  CREATE INDEX IX_ShopVariants_Store ON dbo.ShopVariants(StoreId, ProductId);
END
IF OBJECT_ID('dbo.ShopImages') IS NULL
BEGIN
  CREATE TABLE dbo.ShopImages(
    Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NULL,
    Path NVARCHAR(160) NOT NULL, W INT NOT NULL, H INT NOT NULL, Bytes INT NOT NULL, Alt NVARCHAR(160) NULL, Sort INT NOT NULL DEFAULT 0, Used BIT NOT NULL DEFAULT 0,
    Place NVARCHAR(6) NOT NULL DEFAULT 'disk', CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopImages_Product ON dbo.ShopImages(StoreId, ProductId, Sort);
  CREATE UNIQUE INDEX UX_ShopImages_Path ON dbo.ShopImages(Path);
END
IF OBJECT_ID('dbo.ShopProductCategories') IS NULL
BEGIN
  CREATE TABLE dbo.ShopProductCategories(
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, CategoryId INT NOT NULL,
    IsMain BIT NOT NULL DEFAULT 0, Sort INT NOT NULL DEFAULT 0, Pinned BIT NOT NULL DEFAULT 0, PRIMARY KEY(CategoryId, ProductId));
  CREATE INDEX IX_ShopProductCategories_Product ON dbo.ShopProductCategories(StoreId, ProductId);
END
-- an old link of a product (p) or a category (c) goes to the new one
IF OBJECT_ID('dbo.ShopRedirects') IS NULL
  CREATE TABLE dbo.ShopRedirects(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Kind CHAR(1) NOT NULL, OldSlug NVARCHAR(80) NOT NULL, TargetId INT NOT NULL,
    PRIMARY KEY(StoreId, Kind, OldSlug));
IF OBJECT_ID('dbo.ShopZones') IS NULL
  CREATE TABLE dbo.ShopZones(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Gov INT NOT NULL, Fee DECIMAL(10,2) NULL, Days NVARCHAR(30) NULL, MinSub DECIMAL(10,2) NULL,
    Closed BIT NOT NULL DEFAULT 0, PRIMARY KEY(StoreId, Gov));
IF OBJECT_ID('dbo.ShopPages') IS NULL
BEGIN
  CREATE TABLE dbo.ShopPages(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Slug NVARCHAR(60) NOT NULL, Kind NVARCHAR(10) NOT NULL DEFAULT 'custom', Title NVARCHAR(100) NOT NULL, TitleEn NVARCHAR(100) NULL, Body NVARCHAR(MAX) NULL, BodyEn NVARCHAR(MAX) NULL,
    Hidden BIT NOT NULL DEFAULT 0, InMenu BIT NOT NULL DEFAULT 0, InFooter BIT NOT NULL DEFAULT 1, Sort INT NOT NULL DEFAULT 0, UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShopPages_Slug ON dbo.ShopPages(StoreId, Slug);
END
IF OBJECT_ID('dbo.ShopOffers') IS NULL
BEGIN
  CREATE TABLE dbo.ShopOffers(
    Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Name NVARCHAR(80) NOT NULL, NameEn NVARCHAR(80) NULL, Type NVARCHAR(8) NOT NULL,      -- auto / bxgy / qty / ship
    RuleJson NVARCHAR(MAX) NOT NULL, Campaign NVARCHAR(20) NULL, Look NVARCHAR(MAX) NULL,
    StartsAt DATETIME2 NULL, EndsAt DATETIME2 NULL, Active BIT NOT NULL DEFAULT 1, Countdown BIT NOT NULL DEFAULT 0, Sort INT NOT NULL DEFAULT 0,
    Uses INT NOT NULL DEFAULT 0, SoldEgp DECIMAL(14,2) NOT NULL DEFAULT 0, CostEgp DECIMAL(14,2) NOT NULL DEFAULT 0, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopOffers_Store ON dbo.ShopOffers(StoreId, Active);
END
IF OBJECT_ID('dbo.ShopCoupons') IS NULL
BEGIN
  CREATE TABLE dbo.ShopCoupons(
    Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Code NVARCHAR(30) NOT NULL, Type NVARCHAR(6) NOT NULL, Value DECIMAL(12,2) NOT NULL DEFAULT 0, MaxOff DECIMAL(12,2) NULL, MinSub DECIMAL(12,2) NULL, Scope NVARCHAR(MAX) NULL,   -- pct / amt / ship
    StartsAt DATETIME2 NULL, EndsAt DATETIME2 NULL, MaxUses INT NULL, PerPhone INT NULL, FirstOnly BIT NOT NULL DEFAULT 0, Active BIT NOT NULL DEFAULT 1, Popup BIT NOT NULL DEFAULT 0,
    Uses INT NOT NULL DEFAULT 0, SoldEgp DECIMAL(14,2) NOT NULL DEFAULT 0, CostEgp DECIMAL(14,2) NOT NULL DEFAULT 0, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShopCoupons_Code ON dbo.ShopCoupons(StoreId, Code);
END
-- new / noreply / confirmed / shipped / out / delivered / cancelled / returned
IF OBJECT_ID('dbo.ShopOrders') IS NULL
BEGIN
  CREATE TABLE dbo.ShopOrders(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    No INT NOT NULL, Token NVARCHAR(40) NOT NULL, ClientKey NVARCHAR(40) NULL, Status NVARCHAR(10) NOT NULL DEFAULT 'new', Channel NVARCHAR(8) NOT NULL DEFAULT 'web', Lang CHAR(2) NOT NULL DEFAULT 'ar',
    Name NVARCHAR(80) NOT NULL, Phone NVARCHAR(20) NOT NULL, Phone2 NVARCHAR(20) NULL, Gov INT NOT NULL, City NVARCHAR(60) NULL, Area NVARCHAR(80) NULL, Address NVARCHAR(300) NOT NULL, Landmark NVARCHAR(120) NULL, Note NVARCHAR(400) NULL,
    Subtotal DECIMAL(12,2) NOT NULL, Discount DECIMAL(12,2) NOT NULL DEFAULT 0, Ship DECIMAL(12,2) NOT NULL DEFAULT 0, Total DECIMAL(12,2) NOT NULL, Coupon NVARCHAR(30) NULL, Offers NVARCHAR(MAX) NULL,
    Pay NVARCHAR(10) NOT NULL DEFAULT 'cod', Dup BIT NOT NULL DEFAULT 0, Test BIT NOT NULL DEFAULT 0, Ip NVARCHAR(64) NULL, Device NVARCHAR(40) NULL, Src NVARCHAR(200) NULL,
    StaffNote NVARCHAR(1000) NULL, CancelReason NVARCHAR(200) NULL, Carrier NVARCHAR(30) NULL, Tracking NVARCHAR(60) NULL, Restocked BIT NOT NULL DEFAULT 0, Seen BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), StatusAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ConfirmedAt DATETIME2 NULL, ShippedAt DATETIME2 NULL, DeliveredAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopOrders_No ON dbo.ShopOrders(StoreId, No);
  CREATE UNIQUE INDEX UX_ShopOrders_Token ON dbo.ShopOrders(Token);
  CREATE INDEX IX_ShopOrders_Status ON dbo.ShopOrders(StoreId, Status, Id);
  CREATE INDEX IX_ShopOrders_Phone ON dbo.ShopOrders(StoreId, Phone, Id);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopOrders_Key ON dbo.ShopOrders(StoreId, ClientKey) WHERE ClientKey IS NOT NULL;';
END
IF OBJECT_ID('dbo.ShopOrderItems') IS NULL
BEGIN
  CREATE TABLE dbo.ShopOrderItems(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, ProductId INT NOT NULL, VariantId INT NOT NULL,
    Name NVARCHAR(150) NOT NULL, Opts NVARCHAR(300) NULL, Sku NVARCHAR(60) NULL, Image NVARCHAR(160) NULL, Qty INT NOT NULL, Price DECIMAL(12,2) NOT NULL, Full_ DECIMAL(12,2) NOT NULL, Discount DECIMAL(12,2) NOT NULL DEFAULT 0, Cost DECIMAL(12,2) NULL, Extra NVARCHAR(600) NULL);
  CREATE INDEX IX_ShopOrderItems_Order ON dbo.ShopOrderItems(OrderId);
  CREATE INDEX IX_ShopOrderItems_Product ON dbo.ShopOrderItems(StoreId, ProductId);
END
IF OBJECT_ID('dbo.ShopOrderLog') IS NULL
BEGIN
  CREATE TABLE dbo.ShopOrderLog(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL,
    Who NVARCHAR(20) NOT NULL, What NVARCHAR(300) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopOrderLog_Order ON dbo.ShopOrderLog(OrderId, Id);
END
IF OBJECT_ID('dbo.ShopStock') IS NULL
BEGIN
  CREATE TABLE dbo.ShopStock(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, VariantId INT NOT NULL,
    Delta INT NOT NULL, After_ INT NOT NULL, Reason NVARCHAR(20) NOT NULL, OrderId BIGINT NULL, Note NVARCHAR(120) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopStock_Product ON dbo.ShopStock(StoreId, ProductId, Id);
END
IF OBJECT_ID('dbo.ShopReports') IS NULL
BEGIN
  CREATE TABLE dbo.ShopReports(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Reason NVARCHAR(30) NOT NULL, Text NVARCHAR(1000) NULL,
    Contact NVARCHAR(100) NULL, Page NVARCHAR(200) NULL, Ip NVARCHAR(64) NULL, Status NVARCHAR(8) NOT NULL DEFAULT 'new', At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopReports_Store ON dbo.ShopReports(StoreId, At);
END
IF OBJECT_ID('dbo.ShopDaily') IS NULL
  CREATE TABLE dbo.ShopDaily(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Day DATE NOT NULL, Visits INT NOT NULL DEFAULT 0, Visitors INT NOT NULL DEFAULT 0, Views INT NOT NULL DEFAULT 0,
    Carts INT NOT NULL DEFAULT 0, Checkouts INT NOT NULL DEFAULT 0, Orders INT NOT NULL DEFAULT 0, PRIMARY KEY(StoreId, Day));
IF OBJECT_ID('dbo.ShopFails') IS NULL
BEGIN
  CREATE TABLE dbo.ShopFails(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, What NVARCHAR(20) NOT NULL, Detail NVARCHAR(600) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopFails_At ON dbo.ShopFails(At);
END
-- an order's confirmation on WhatsApp (the same columns as OrderConfirms / EoConfirms, so the follow-up reads the three)
IF OBJECT_ID('dbo.ShopConfirms') IS NULL
BEGIN
  CREATE TABLE dbo.ShopConfirms(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, OrderName NVARCHAR(40) NULL, Customer NVARCHAR(100) NULL, Phone NVARCHAR(20) NULL, Total NVARCHAR(40) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'pending', Note NVARCHAR(300) NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), AnsweredAt DATETIME2 NULL, RemindedAt DATETIME2 NULL, AlertedAt DATETIME2 NULL, AlertTo NVARCHAR(80) NULL);
  CREATE UNIQUE INDEX UX_ShopConfirms_Order ON dbo.ShopConfirms(StoreId, OrderId);
  CREATE INDEX IX_ShopConfirms_Phone ON dbo.ShopConfirms(StoreId, Phone, Id);
END
-- the point of a picture that must stay in view when a template crops it (50,35 = percent across, percent down)
IF COL_LENGTH('dbo.ShopImages', 'Focus') IS NULL ALTER TABLE dbo.ShopImages ADD Focus NVARCHAR(9) NULL;
-- «نافذة عرض»: the code is shown for a mobile number; the numbers the store was given
IF COL_LENGTH('dbo.ShopCoupons', 'PopupPhone') IS NULL ALTER TABLE dbo.ShopCoupons ADD PopupPhone BIT NOT NULL CONSTRAINT DF_ShopCoupons_PopupPhone DEFAULT 0;
IF OBJECT_ID('dbo.ShopLeads') IS NULL
BEGIN
  CREATE TABLE dbo.ShopLeads(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL, Coupon NVARCHAR(30) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShopLeads_Phone ON dbo.ShopLeads(StoreId, Phone);
END
IF COL_LENGTH('dbo.Plans', 'IncludesShop') IS NULL ALTER TABLE dbo.Plans ADD IncludesShop BIT NOT NULL CONSTRAINT DF_Plans_IncludesShop DEFAULT 0;
";
    }
}
