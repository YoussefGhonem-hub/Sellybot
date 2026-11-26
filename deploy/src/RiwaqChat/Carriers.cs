// Shipping companies (Bosta, ShipBlu, J&T Egypt, Aramex): the merchant links their own account (prices and the cash on delivery stay between them
// and the company), sends confirmed orders with a press, prints the company's labels, asks for the courier, and the status comes back by itself.
//   CarrierBosta.cs / CarrierShipBlu.cs / CarrierJt.cs / CarrierAramex.cs   each company's own calls      CarrierAreas.cs   our cities -> theirs
//   CarrierTrack.cs   what happens to a shipment (statuses, «إنقاذ الأوردر», jobs)      CarrierPdf.cs     several labels in one file
//   CarrierHook.cs    the companies' notifications, and the admin's page              shop-admin/ship.js  the owner's side
// This file: the tables, linking, sending, labels, pickup, cancelling.
// One shipment per order, whatever happens: an order has one open row (a unique index), and a press that may have reached the company
// without an answer is looked up by our reference before anything is created again. Today the orders are the rented stores' (Source 'shop').
// Everything comes from the merchant (their keys, their account): open to every store by itself; the admin may pick a lowest plan or close it.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    // Said to the owner as it is. Unsure: the request may have reached the company (a timeout, a broken answer), so it's looked up before anything
    // is tried again. Down: it surely didn't (no connection). BadKey: the company doesn't accept the key any more.
    public class CarrierException : Exception
    {
        public readonly string Code; public readonly bool Unsure, BadKey, Down;
        public CarrierException(string message, string code = null, bool unsure = false, bool badKey = false, bool down = false) : base(message) { Code = code; Unsure = unsure; BadKey = badKey; Down = down; }
    }

    public class CarrierLink
    {
        public int StoreId; public string Company, Key, Code, Last4, AccName, HookId, HookSecret, LastError; public DateTime LinkedAt; public DateTime? LastErrorAt;
        public Dictionary<string, object> Set = new Dictionary<string, object>(); public List<object> Places = new List<object>();
        public string Account, Private; public bool Demo;   // J&T: the keys a call is signed with (the merchant's own, or the admin's fallback)
        // the merchant's other secrets (MoreBox, encrypted): J&T's apiAccount / privateKey, Aramex's API user. Own: signed with the merchant's own keys.
        // Fresh: linked to another account, so «a notification came» starts again. HeardAt: the company's first notification that checked out.
        public Dictionary<string, string> More = new Dictionary<string, string>(); public bool Own, Fresh; public DateTime? HeardAt;
        public string M(string k) { string v; return More.TryGetValue(k, out v) ? v ?? "" : ""; }
        public string S(string k, string d = "") { var v = ShopJson.Str(ShopJson.At(Set, k), 300); return v.Length == 0 ? d : v; }
        public bool B(string k, bool d) { return ShopJson.Bool(ShopJson.At(Set, k), d); }
    }
    public class CarrierParcel
    {
        public string Ref, OrderNo, Name, Phone, Phone2, Email, Address, Landmark, Note, Goods, HookUrl, HookSecret; public int Items; public decimal Cod, Value; public CarrierAreaRow Area;
        public List<string> Pics = new List<string>(); public List<ShopOrderItem> Lines = new List<ShopOrderItem>();
        public string Day, Part, NewGoods; public decimal Kg;   // ShopShip2: the delivery day the customer chose, an exchange's new piece, the real weight (0 = not known)
    }
    public class CarrierMade { public string Tracking, Id; }
    // what a company says happened; Ours: made, picked, transit, out, attempt, delivered, returning, returned, cancelled, lost, hold, "" (nothing for us).
    // Loud: the owner must hear it. Blind: it came without the shipment's type, so «on its way» may be back to the owner
    public class CarrierEvent { public string Ref, Tracking, TheirId, Code, Ours = "", Reason = "", Text; public int Attempts; public DateTime? At; public bool Loud, Blind; public decimal? Cod; }
    public class CarrierShip
    {
        public long Id; public int StoreId, Attempts, Rescued; public string Source, OrderId, OrderName, Company, Ref, Phone, Tracking, TheirId, State, Status, Ours, Reason, ReasonText, Area, Error; public decimal Cod;
        public DateTime CreatedAt, ClaimedAt; public DateTime? LastEventAt, RescueDue, RescuedAt, AnsweredAt, BackAt, GotAt, PrintedAt, CheckedAt;
        public bool Picked { get { return !(string.IsNullOrEmpty(Ours) || Ours == "made"); } }
        public bool Open { get { return State == "making" || State == "unsure" || State == "live"; } }
    }

    public abstract class Carrier
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        // asked about the open shipments this often (the companies that notify are asked rarely, for what never arrived)
        public abstract int PollMinutes { get; }
        public virtual int PollBatch { get { return 1; } }
        public virtual int MaxLabels { get { return 50; } }
        public virtual decimal MaxCod { get { return 0; } }
        public virtual bool PickupCall { get { return true; } }
        // null, or why nobody can link it yet
        public virtual string Closed(SqlConnection c) { return null; }
        public virtual void Prepare(SqlConnection c, CarrierLink k) { }
        // what the merchant pasted, before asking the company
        public abstract string BadInput(CarrierLink k);
        // asks the company whether the key is right; fills AccName and Places
        public abstract Task Check(CarrierLink k);
        public abstract Task<CarrierMade> Create(CarrierLink k, CarrierParcel p);
        // a shipment with this reference, or null when the company surely has none
        public abstract Task<CarrierMade> Find(CarrierLink k, string reference);
        public abstract Task<byte[]> Labels(CarrierLink k, List<CarrierShip> l, string size);
        public abstract Task<string> Pickup(CarrierLink k, DateTime day, int parcels, string place, string note, List<CarrierShip> waiting);
        public abstract Task Cancel(CarrierLink k, CarrierShip x);
        public abstract Task<List<CarrierEvent>> Ask(CarrierLink k, List<CarrierShip> l);
        public abstract Task<List<CarrierAreaRow>> Areas(CarrierLink k);
        // what must be in the settings before the first shipment; null = ready
        public virtual string NotReady(CarrierLink k) { return null; }
        // the fields of the link form beyond key / code (old: the link there was, so a field left empty keeps what it had)
        public virtual void Take(CarrierLink k, Dictionary<string, object> b, CarrierLink old) { }
        // the link can't be used as it is (null = fine), and the last characters shown for it
        public virtual string Broken(CarrierLink k) { return null; }
        public virtual string Tail(CarrierLink k) { return k.Key.Length > 4 ? k.Key.Substring(k.Key.Length - 4) : ""; }
        // the company itself refuses a second shipment with our reference: a press without an answer is simply made again
        public virtual bool GuardsDup { get { return false; } }
        // after its first notification the company isn't asked any more (it tells us everything)
        public virtual bool QuietAfterPush { get { return false; } }
        // what the owner's card shows beyond the common fields
        public virtual Dictionary<string, object> Card(SqlConnection c, CarrierLink k, string origin) { return null; }
    }

    public static partial class Carriers
    {
        public const string Feature = "carriers";
        public static readonly Carrier[] All = { new CarrierBosta(), new CarrierShipBlu(), new CarrierJt(), new CarrierAramex() };
        public static Carrier Of(string id) { return All.FirstOrDefault(x => x.Id == id); }

        public const string Schema = @"
IF OBJECT_ID('dbo.Carriers') IS NULL
BEGIN
  CREATE TABLE dbo.Carriers(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Company NVARCHAR(10) NOT NULL, KeyBox NVARCHAR(MAX) NOT NULL, Code NVARCHAR(40) NULL, Last4 NVARCHAR(8) NOT NULL, AccName NVARCHAR(120) NULL,
    HookId NVARCHAR(40) NOT NULL, HookBox NVARCHAR(400) NOT NULL, Places NVARCHAR(MAX) NULL, Defaults NVARCHAR(MAX) NULL, LinkedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), LastError NVARCHAR(300) NULL, LastErrorAt DATETIME2 NULL, PRIMARY KEY(StoreId, Company));
  CREATE UNIQUE INDEX UX_Carriers_Hook ON dbo.Carriers(HookId);
END
-- State: making (being sent), unsure (no answer: looked up before another try), live, done, failed (refused), off (cancelled before the courier took it)
IF OBJECT_ID('dbo.Shipments') IS NULL
BEGIN
  CREATE TABLE dbo.Shipments(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Source NVARCHAR(10) NOT NULL DEFAULT 'shop', OrderId NVARCHAR(64) NOT NULL, OrderName NVARCHAR(40) NOT NULL,
    Company NVARCHAR(10) NOT NULL, Ref NVARCHAR(50) NOT NULL, Phone NVARCHAR(20) NULL, Tracking NVARCHAR(60) NULL, TheirId NVARCHAR(60) NULL, State NVARCHAR(8) NOT NULL, Status NVARCHAR(40) NULL, Ours NVARCHAR(10) NULL, Reason NVARCHAR(12) NULL, ReasonText NVARCHAR(200) NULL,
    Attempts INT NOT NULL DEFAULT 0, Cod DECIMAL(12,2) NOT NULL DEFAULT 0, Area NVARCHAR(200) NULL, Label NVARCHAR(300) NULL, Error NVARCHAR(300) NULL, Rescued INT NOT NULL DEFAULT 0, RescueDue DATETIME2 NULL, RescuedAt DATETIME2 NULL, AnsweredAt DATETIME2 NULL,
    BackAt DATETIME2 NULL, GotAt DATETIME2 NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ClaimedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), PrintedAt DATETIME2 NULL, LastEventAt DATETIME2 NULL, CheckedAt DATETIME2 NULL, DoneAt DATETIME2 NULL);
  CREATE INDEX IX_Shipments_Order ON dbo.Shipments(StoreId, Source, OrderId, Id);
  CREATE INDEX IX_Shipments_Tracking ON dbo.Shipments(Company, Tracking);
  CREATE INDEX IX_Shipments_Poll ON dbo.Shipments(State, Company, CheckedAt);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_Shipments_Open ON dbo.Shipments(StoreId, Source, OrderId) WHERE State IN (''making'', ''unsure'', ''live'');';
  EXEC sp_executesql N'CREATE INDEX IX_Shipments_Rescue ON dbo.Shipments(StoreId, RescuedAt) WHERE RescuedAt IS NOT NULL;';
END
IF OBJECT_ID('dbo.ShipmentEvents') IS NULL
BEGIN
  CREATE TABLE dbo.ShipmentEvents(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ShipmentId BIGINT NOT NULL, Code NVARCHAR(40) NOT NULL, Ours NVARCHAR(10) NOT NULL, Text NVARCHAR(300) NULL, At DATETIME2 NOT NULL, Src NVARCHAR(6) NOT NULL, SeenAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShipmentEvents_Key ON dbo.ShipmentEvents(ShipmentId, Code, At);
END
-- the companies' own areas, filled again every week (CarrierAreas.cs): their governorate, city / zone, and the area a shipment is addressed to
IF OBJECT_ID('dbo.CarrierAreas') IS NULL
BEGIN
  CREATE TABLE dbo.CarrierAreas(Id BIGINT IDENTITY(1,1) PRIMARY KEY, Company NVARCHAR(10) NOT NULL, GovId NVARCHAR(40) NOT NULL, Gov NVARCHAR(100) NOT NULL, GovEn NVARCHAR(100) NULL, CityId NVARCHAR(40) NOT NULL, City NVARCHAR(120) NOT NULL, CityEn NVARCHAR(120) NULL,
    AreaId NVARCHAR(40) NOT NULL, Area NVARCHAR(160) NOT NULL, AreaEn NVARCHAR(160) NULL, Closed BIT NOT NULL DEFAULT 0, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_CarrierAreas_Company ON dbo.CarrierAreas(Company);
END
-- what the owner picked by hand for a city, so it's asked once
IF OBJECT_ID('dbo.CarrierPicks') IS NULL
  CREATE TABLE dbo.CarrierPicks(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Company NVARCHAR(10) NOT NULL, Gov INT NOT NULL, Place NVARCHAR(160) NOT NULL, GovId NVARCHAR(40) NOT NULL, CityId NVARCHAR(40) NOT NULL, AreaId NVARCHAR(40) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY(StoreId, Company, Gov, Place));
-- the merchant's other secrets (encrypted), and when the company's first notification for this link checked out
IF COL_LENGTH('dbo.Carriers', 'MoreBox') IS NULL EXEC sp_executesql N'ALTER TABLE dbo.Carriers ADD MoreBox NVARCHAR(MAX) NULL, HeardAt DATETIME2 NULL;';
";

        // ---------- small things ----------
        static string Str(Dictionary<string, object> b, string k, int max = 200) { return ShopJson.Str(ShopJson.At(b, k), max); }
        static int Int(Dictionary<string, object> b, string k, int d = 0) { return ShopJson.Int(ShopJson.At(b, k), d); }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        public static string Cut(string s, int n) { return s == null ? null : s.Length > n ? s.Substring(0, n) : s; }
        internal static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        internal static int Num(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v); } }
        static string Iso(DateTime? t) { return t.HasValue ? DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToString("o") : ""; }
        static DateTime? Dt(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }
        static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        static void Ok(HttpResponse res, params object[] kv) { var d = D(kv); d["ok"] = true; Web.Json(res, 200, d); }
        static readonly TimeZoneInfo Zone = FindZone();
        static TimeZoneInfo FindZone() { foreach (var id in new[] { "Egypt Standard Time", "Africa/Cairo" }) try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { } return TimeZoneInfo.CreateCustomTimeZone("Cairo", TimeSpan.FromHours(2), "Cairo", "Cairo"); }
        public static DateTime Cairo(DateTime utc) { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone); }
        public static DateTime? FromCairo(string s)
        {
            DateTime t; if (!DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), Zone); } catch { return DateTime.SpecifyKind(t.AddHours(-2), DateTimeKind.Utc); }
        }

        // ---------- the companies' servers ----------
        public class Reply { public int Status; public byte[] Bytes; public string Text; public object Root; public bool Good { get { return Status >= 200 && Status < 300; } } public Dictionary<string, object> Body { get { return Root as Dictionary<string, object> ?? new Dictionary<string, object>(); } } }
        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; int sec; if (!int.TryParse(Web.Setting("CarrierTimeoutSec"), out sec) || sec < 1) sec = 30;   // a setting only for tests
            var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(sec), MaxResponseContentBufferSize = 40000000 };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0"); return h;
        }
        public static async Task<Reply> Send(string name, HttpRequestMessage q)
        {
            HttpResponseMessage r;
            try { r = await Http.SendAsync(q); }
            catch (Exception ex)
            {
                var w = (ex as HttpRequestException)?.InnerException as WebException; bool never = w != null && (w.Status == WebExceptionStatus.ConnectFailure || w.Status == WebExceptionStatus.NameResolutionFailure || w.Status == WebExceptionStatus.TrustFailure || w.Status == WebExceptionStatus.SecureChannelFailure || w.Status == WebExceptionStatus.ProxyNameResolutionFailure);
                Log.Error(new Exception("Carrier " + name + " " + q.Method + " " + q.RequestUri.AbsolutePath + ": " + (w == null ? ex.GetType().Name : w.Status.ToString())));
                throw new CarrierException(name + " مش بترد دلوقتي. جرّب كمان شوية.", "net", unsure: !never, down: never);
            }
            using (r)
            {
                var o = new Reply { Status = (int)r.StatusCode };
                try { o.Bytes = await r.Content.ReadAsByteArrayAsync(); } catch (Exception) { throw new CarrierException(name + " قطعت في نص الرد. جرّب كمان شوية.", "net", unsure: true); }
                var type = r.Content.Headers.ContentType == null ? "" : r.Content.Headers.ContentType.MediaType ?? "";
                if (!type.Contains("pdf") && o.Bytes.Length < 8000000) { o.Text = Encoding.UTF8.GetString(o.Bytes); if (o.Text.Length > 0 && (o.Text[0] == '{' || o.Text[0] == '[' || o.Text[0] == '"')) try { o.Root = Web.Js.DeserializeObject(o.Text); } catch { } }
                return o;
            }
        }
        // a PDF, as bytes or somewhere in the answer in base64
        public static byte[] PdfIn(Reply r)
        {
            if (IsPdf(r.Bytes)) return r.Bytes;
            Func<object, byte[]> walk = null;
            walk = v =>
            {
                var s = v as string; if (s != null) { int i = s.IndexOf("JVBERi0", StringComparison.Ordinal); if (i < 0 || i > 60) return null; try { var b = Convert.FromBase64String(Regex.Replace(s.Substring(i), @"\s", "")); return IsPdf(b) ? b : null; } catch { return null; } }
                var d = v as Dictionary<string, object>; if (d != null) { foreach (var x in d.Values) { var f = walk(x); if (f != null) return f; } return null; }
                var a = v as object[]; if (a != null) foreach (var x in a) { var f = walk(x); if (f != null) return f; }
                return null;
            };
            return walk(r.Root ?? r.Text);
        }
        static bool IsPdf(byte[] b) { return b != null && b.Length > 60 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F'; }

        // ---------- who sees it ----------
        public static bool Tester(SqlConnection c, StoreInfo s) { return (Billing.Get(c, "CarrierTesters") ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries).Contains(s.Slug, StringComparer.OrdinalIgnoreCase); }
        // open to every store by itself (all it needs is the merchant's own account); the admin may pick a lowest plan, or close it (CarriersOff)
        public static bool On(SqlConnection c, StoreInfo s) { return Shops.Ready && s != null && (Tester(c, s) || (Billing.Get(c, "CarriersOff") != "1" && (string.IsNullOrEmpty(Billing.Get(c, "MinPlan_" + Feature)) || PlanGate.Allows(c, s, Feature)))); }

        // ---------- the links ----------
        public static CarrierLink Link(SqlConnection c, int storeId, string company) { return Links(c, "StoreId=@a AND Company=@b", storeId, company).FirstOrDefault(); }
        public static CarrierLink LinkByHook(SqlConnection c, string hookId) { return Links(c, "HookId=@a", hookId, "").FirstOrDefault(); }
        public static List<CarrierLink> Links(SqlConnection c, int storeId) { return Links(c, "StoreId=@a", storeId, ""); }
        static List<CarrierLink> Links(SqlConnection c, string where, object a, object b)
        {
            var l = new List<CarrierLink>(); if (!Shops.Ready) return l;
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.Carriers WHERE " + where + " ORDER BY LinkedAt", "@a", a, "@b", b)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new CarrierLink { StoreId = (int)r["StoreId"], Company = (string)r["Company"], Key = Secrets.Unprotect((string)r["KeyBox"]), Code = r["Code"] as string, Last4 = (string)r["Last4"], AccName = r["AccName"] as string, HookId = (string)r["HookId"], HookSecret = Secrets.Unprotect((string)r["HookBox"]), LastError = r["LastError"] as string, LastErrorAt = Dt(r["LastErrorAt"]), LinkedAt = Dt(r["LinkedAt"]).Value, Set = ShopJson.Obj(r["Defaults"] as string), Places = ShopJson.Arr(r["Places"] as string), More = Box(r["MoreBox"] as string), HeardAt = Dt(r["HeardAt"]) });
            foreach (var k in l) { var co = Of(k.Company); if (co != null) co.Prepare(c, k); }
            return l.Where(k => Of(k.Company) != null).ToList();
        }
        static Dictionary<string, string> Box(string box)
        {
            var d = new Dictionary<string, string>(); if (string.IsNullOrEmpty(box)) return d;
            foreach (var kv in ShopJson.Obj(Secrets.Unprotect(box))) d[kv.Key] = ShopJson.Str(kv.Value, 300); return d;
        }
        // the key stopped working: shown in the tab, and said to the owner once a day
        internal static void Fault(SqlConnection c, CarrierLink k, string why)
        {
            Exec(c, "UPDATE dbo.Carriers SET LastError=@e, LastErrorAt=SYSUTCDATETIME() WHERE StoreId=@s AND Company=@c", "@e", Cut(why, 300), "@s", k.StoreId, "@c", k.Company);
            if (Web.Allow("ship-fault:" + k.StoreId + ":" + k.Company, 1, TimeSpan.FromHours(24))) ShopHooks.Tell(k.StoreId, "ship", "⚠️ *ربط " + Of(k.Company).Name + " وقف*\n" + why + "\n\nافتح «متجري» ثم «الشحن» واربط المفتاح تاني.", "sb-ship-key-" + k.StoreId + "-" + k.Company + "-" + DateTime.UtcNow.ToString("yyyyMMdd"));
        }
        static CarrierLink Need(SqlConnection c, int storeId, string company)
        {
            var co = Of(company); if (co == null) throw new ShopException("company", "شركة الشحن دي مش موجودة.");
            var k = Link(c, storeId, company); if (k == null) throw new ShopException("link", "اربط حسابك على " + co.Name + " الأول من تبويب «الشحن».");
            var no = co.Closed(c); if (no != null) throw new ShopException("closed", no); if (string.IsNullOrEmpty(k.Key)) throw new ShopException("link", "مفتاح " + co.Name + " ماتقراش. اربطه تاني.");
            var broke = co.Broken(k); if (broke != null) throw new ShopException("link", broke);
            return k;
        }
        static ShopException Said(CarrierException ex) { return new ShopException(ex.Code ?? "carrier", ex.Message); }

        static Func<Task> LinkUp(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var co = Of(Str(b, "c", 10)); if (co == null) throw new ShopException("company", "شركة الشحن دي مش موجودة.");
            var no = co.Closed(c); if (no != null) throw new ShopException("closed", no);
            if (!Web.Allow("ship-link:" + s.Id, 30, TimeSpan.FromHours(1))) throw new ShopException("too_many", "محاولات كتير. استنى شوية وجرّب تاني.");
            var k = new CarrierLink { StoreId = s.Id, Company = co.Id, Key = Str(b, "key", 600), Code = Str(b, "code", 40) }; co.Take(k, b, Link(c, s.Id, co.Id)); co.Prepare(c, k);
            var bad = co.BadInput(k); if (bad != null) throw new ShopException("key", bad);
            return async () =>
            {
                try { await co.Check(k); } catch (CarrierException ex) { throw new ShopException(ex.BadKey ? "key" : ex.Code ?? "carrier", ex.Message); }
                using (var c2 = Db.Open())
                {
                    // a new key keeps the settings, and the address the company already has for the shipments on their way
                    string last = co.Tail(k), places = k.Places.Count == 0 ? null : ShopJson.Write(k.Places), more = k.More.Count == 0 ? null : Secrets.Protect(ShopJson.Write(k.More.ToDictionary(x => x.Key, x => (object)x.Value)));
                    Func<bool> update = () => { using (var cmd = Db.Cmd(c2, "UPDATE dbo.Carriers SET KeyBox=@k, Code=@cd, Last4=@l, AccName=@n, Places=@p, MoreBox=@m, HeardAt=CASE WHEN @f=1 THEN NULL ELSE HeardAt END, LinkedAt=SYSUTCDATETIME(), LastError=NULL, LastErrorAt=NULL WHERE StoreId=@s AND Company=@c", "@k", Secrets.Protect(k.Key), "@cd", Nz(k.Code), "@l", last, "@n", Nz(Cut(k.AccName, 120)), "@p", places, "@m", more, "@f", k.Fresh ? 1 : 0, "@s", s.Id, "@c", co.Id)) return cmd.ExecuteNonQuery() > 0; };
                    if (!update())
                        try { Exec(c2, "INSERT INTO dbo.Carriers(StoreId, Company, KeyBox, Code, Last4, AccName, HookId, HookBox, Places, MoreBox) VALUES(@s, @c, @k, @cd, @l, @n, @h, @hb, @p, @m)", "@s", s.Id, "@c", co.Id, "@k", Secrets.Protect(k.Key), "@cd", Nz(k.Code), "@l", last, "@n", Nz(Cut(k.AccName, 120)), "@h", Shops.Token(12), "@hb", Secrets.Protect(Shops.Token(24)), "@p", places, "@m", more); }
                        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { update(); }
                    ShopShipPick.Forget(s.Id);   // the checkout's companies see the new link at once
                    if (CarrierAreas.Count(c2, co.Id) == 0) HostingEnvironment.QueueBackgroundWorkItem(ct => CarrierAreas.Refresh(co, true, ct));
                    Web.Json(res, 200, Tab(c2, s));
                }
            };
        }

        static void SaveSet(SqlConnection c, StoreInfo s, Dictionary<string, object> b)
        {
            var co = Of(Str(b, "c", 10)); var k = co == null ? null : Link(c, s.Id, co.Id); if (k == null) throw new ShopException("link", "اربط الشركة الأول.");
            var v = ShopJson.Sub(b, "set"); Func<string, int, string> t = (x, n) => Regex.Replace(ShopJson.Str(ShopJson.At(v, x), n), @"[\u0000-\u001F]", " ").Trim(); var d = k.Set;
            d["size"] = Regex.IsMatch(t("size", 20), "^[A-Za-z0-9 ]{1,20}$") ? t("size", 20) : ""; d["open"] = ShopJson.Bool(ShopJson.At(v, "open")); d["value"] = ShopJson.Bool(ShopJson.At(v, "value"), true); d["rescue"] = ShopJson.Bool(ShopJson.At(v, "rescue"), true);
            d["note"] = t("note", 150); d["label"] = t("label", 2) == "A6" ? "A6" : "A4"; d["place"] = t("place", 60); d["email"] = Web.ValidEmail(t("email", 120)) ? t("email", 120) : ""; d["goods"] = Regex.IsMatch(t("goods", 6), @"^ITN\d{1,2}$") ? t("goods", 6) : "";
            var w = ShopJson.Dec(ShopJson.At(v, "weight")); d["weight"] = w <= 0 ? 1 : Math.Max(0.01m, Math.Min(30, w));
            int from = ShopJson.Int(ShopJson.At(v, "from"), 10), to = ShopJson.Int(ShopJson.At(v, "to"), 18); if (from < 0 || from > 22 || to <= from || to > 23) { from = 10; to = 18; } d["from"] = from; d["to"] = to;
            d["sn"] = t("sn", 50); d["sp"] = ShopOrders.Mobile(t("sp", 24)) ?? ""; d["ss"] = t("ss", 150); d["pt"] = Regex.IsMatch(t("pt", 3), "^[A-Z]{3}$") ? t("pt", 3) : "";   // pt: Aramex's product
            string area = t("sa", 140); if (area.Length > 0 && CarrierAreas.ByKey(c, co.Id, area) == null) throw new ShopException("area", "اختار منطقة الاستلام من القايمة."); d["sa"] = area;
            if (t("sp", 24).Length > 0 && (string)d["sp"] == "") throw new ShopException("phone", "اكتب رقم موبايل مصري صح لعنوان الاستلام.");
            Exec(c, "UPDATE dbo.Carriers SET Defaults=@d WHERE StoreId=@s AND Company=@c", "@d", ShopJson.Write(d), "@s", s.Id, "@c", co.Id);
        }

        // ---------- reading, for the owner ----------
        public static readonly Dictionary<string, string> OursText = new Dictionary<string, string> { { "made", "اتسجّلت عند الشركة" }, { "picked", "استلمتها الشركة" }, { "transit", "في مخازن الشركة" }, { "out", "خرجت للتوصيل" }, { "attempt", "محاولة فشلت" }, { "delivered", "اتسلّمت" }, { "returning", "راجعة ليك" }, { "returned", "رجعت" }, { "cancelled", "اتلغت" }, { "lost", "ضاعت أو اتكسرت" }, { "hold", "واقفة عندهم" }, { "asked", "المندوب لسه ماراحش" }, { "going", "المندوب رايح للزبون" }, { "took", "المندوب استلم المرتجع" } };
        const string NeedsLook = "(State='unsure' OR (State='live' AND Ours IN ('attempt','returning')) OR (BackAt IS NOT NULL AND GotAt IS NULL))";

        internal static CarrierShip ReadShip(System.Data.IDataRecord r)
        {
            return new CarrierShip { Id = Convert.ToInt64(r["Id"]), StoreId = (int)r["StoreId"], Source = (string)r["Source"], OrderId = (string)r["OrderId"], OrderName = (string)r["OrderName"], Company = (string)r["Company"], Ref = (string)r["Ref"], Phone = r["Phone"] as string, Tracking = r["Tracking"] as string, TheirId = r["TheirId"] as string,
                State = (string)r["State"], Status = r["Status"] as string, Ours = r["Ours"] as string, Reason = r["Reason"] as string, ReasonText = r["ReasonText"] as string, Attempts = (int)r["Attempts"], Cod = (decimal)r["Cod"], Area = r["Area"] as string, Error = r["Error"] as string, Rescued = (int)r["Rescued"], RescueDue = Dt(r["RescueDue"]), RescuedAt = Dt(r["RescuedAt"]), AnsweredAt = Dt(r["AnsweredAt"]),
                BackAt = Dt(r["BackAt"]), GotAt = Dt(r["GotAt"]), CreatedAt = Dt(r["CreatedAt"]).Value, ClaimedAt = Dt(r["ClaimedAt"]).Value, PrintedAt = Dt(r["PrintedAt"]), LastEventAt = Dt(r["LastEventAt"]), CheckedAt = Dt(r["CheckedAt"]) };
        }
        internal static List<CarrierShip> Ships(SqlConnection c, int top, string where, params object[] a) { var l = new List<CarrierShip>(); using (var cmd = Db.Cmd(c, "SELECT " + (top > 0 ? "TOP " + top + " " : "") + "* FROM dbo.Shipments WHERE " + where, a)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(ReadShip(r)); return l; }
        internal static CarrierShip ById(SqlConnection c, int storeId, long id) { return Ships(c, 1, "StoreId=@s AND Id=@i", "@s", storeId, "@i", id).FirstOrDefault(); }
        // the order's newest shipment
        public static CarrierShip OfOrder(SqlConnection c, int storeId, long orderId) { return Ships(c, 1, "StoreId=@s AND Source='shop' AND OrderId=@o ORDER BY Id DESC", "@s", storeId, "@o", orderId.ToString(CultureInfo.InvariantCulture)).FirstOrDefault(); }

        // in the store's state: null = the store must see nothing
        public static object StateJson(SqlConnection c, StoreInfo s)
        {
            try
            {
                if (!On(c, s)) return null;
                var l = Links(c, s.Id); return D("list", All.Select(co => { var k = l.FirstOrDefault(x => x.Company == co.Id); return D("id", co.Id, "name", co.Name, "linked", k != null, "soon", co.Closed(c) != null); }).ToArray(), "look", Num(c, "SELECT COUNT(*) FROM dbo.Shipments WHERE StoreId=@s AND " + NeedsLook, "@s", s.Id));
            }
            catch (Exception ex) { Log.Error(ex); return null; }
        }
        static Dictionary<string, object> Tab(SqlConnection c, StoreInfo s)
        {
            var l = Links(c, s.Id); var n = new Dictionary<string, int[]>(); string origin = HookBase();
            using (var cmd = Db.Cmd(c, "SELECT Company, SUM(CASE WHEN State='live' THEN 1 ELSE 0 END), SUM(CASE WHEN State='live' AND ISNULL(Ours,'made')='made' THEN 1 ELSE 0 END) FROM dbo.Shipments WHERE StoreId=@s GROUP BY Company", "@s", s.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) n[(string)r[0]] = new[] { (int)r[1], (int)r[2] };
            return D("list", All.Select(co =>
            {
                var k = l.FirstOrDefault(x => x.Company == co.Id); int[] m; n.TryGetValue(co.Id, out m); var soon = co.Closed(c);
                var d = D("id", co.Id, "name", co.Name, "linked", k != null, "soon", soon ?? "", "open", m == null ? 0 : m[0], "wait", m == null ? 0 : m[1], "areas", CarrierAreas.Count(c, co.Id), "pickup", co.PickupCall, "maxCod", co.MaxCod);
                if (k != null) { d["last4"] = k.Last4; d["code"] = k.Code ?? ""; d["acc"] = k.AccName ?? ""; d["at"] = Iso(k.LinkedAt); d["err"] = co.Broken(k) ?? k.LastError ?? ""; d["set"] = k.Set; d["places"] = k.Places; d["need"] = co.NotReady(k) ?? ""; d["sender"] = AreaLabel(c, co.Id, k.S("sa")); }
                var x = co.Card(c, k, origin); if (x != null) d["x"] = x;
                return d;
            }).ToArray(), "look", Num(c, "SELECT COUNT(*) FROM dbo.Shipments WHERE StoreId=@s AND " + NeedsLook, "@s", s.Id), "ok", true);
        }
        // where the companies' notifications come (outside /api/, so the site's own limits there never hold a company back)
        internal static string HookBase() { return Web.Setting("CarrierHookBase") ?? EasyOrders.Origin(HttpContext.Current == null ? null : HttpContext.Current.Request); }
        public const string HookPath = "/ship-hook.ashx";
        static string AreaLabel(SqlConnection c, string company, string key) { if (key.Length == 0) return ""; var a = CarrierAreas.ByKey(c, company, key); return a == null ? "" : a.Label; }
        static Dictionary<string, object> List(SqlConnection c, StoreInfo s, string f, int page)
        {
            string where = "StoreId=@s AND State<>'failed'" + (f == "look" ? " AND " + NeedsLook : f == "open" ? " AND State IN ('making','unsure','live')" : "");
            return D("total", Num(c, "SELECT COUNT(*) FROM dbo.Shipments WHERE " + where, "@s", s.Id), "page", page, "size", 30, "list", Ships(c, 0, where + " ORDER BY Id DESC OFFSET " + Math.Max(0, page) * 30 + " ROWS FETCH NEXT 30 ROWS ONLY", "@s", s.Id).Select(x => ShipJson(c, x, false)).ToArray());
        }
        static Dictionary<string, object> ShipJson(SqlConnection c, CarrierShip x, bool events)
        {
            string ours = x.State == "making" ? "بتتبعت دلوقتي" : x.State == "unsure" ? "مش متأكدين إنها اتسجّلت" : x.State == "failed" ? "ماتبعتتش" : x.State == "off" ? "اتلغت" : x.Ours != null && OursText.ContainsKey(x.Ours) ? OursText[x.Ours] : "اتسجّلت عند الشركة";
            var d = D("id", x.Id, "order", x.OrderId, "name", x.OrderName, "c", x.Company, "cName", Of(x.Company) == null ? x.Company : Of(x.Company).Name, "ref", x.Ref, "tracking", x.Tracking ?? "", "state", x.State, "ours", x.Ours ?? "made", "text", ours, "status", x.Status ?? "", "why", x.ReasonText ?? "", "n", x.Attempts, "cod", x.Cod, "area", x.Area ?? "", "err", x.Error ?? "",
                "at", Iso(x.CreatedAt), "last", Iso(x.LastEventAt), "printed", x.PrintedAt.HasValue, "back", x.BackAt.HasValue, "got", x.GotAt.HasValue, "canCancel", x.State == "live" && !x.Picked, "rescued", Iso(x.RescuedAt), "answered", Iso(x.AnsweredAt), "due", Iso(x.RescueDue));
            if (!events) return d;
            var l = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT TOP 60 Code, Ours, Text, At FROM dbo.ShipmentEvents WHERE ShipmentId=@i AND StoreId=@s ORDER BY At DESC, Id DESC", "@i", x.Id, "@s", x.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) { string o; l.Add(D("t", r[2] as string ?? (OursText.TryGetValue((string)r[1], out o) ? o : (string)r[0]), "ours", (string)r[1], "at", Iso(Dt(r[3])))); }
            d["events"] = l; return d;
        }
        // an order's shipment and what can be done with it; null = the store must see nothing
        public static object OrderJson(SqlConnection c, StoreInfo s, ShopOrder o)
        {
            try
            {
                if (!On(c, s)) return null;
                var x = OfOrder(c, s.Id, o.Id); var l = Links(c, s.Id);
                if (x == null && l.Count == 0) return null;
                // the order was changed after it was sent: the courier still has the old amount
                var d = D("ship", x == null ? null : ShipJson(c, x, true), "links", l.Select(k => D("id", k.Company, "name", Of(k.Company).Name)).ToArray(), "can", CantSend(o) ?? "", "diff", x != null && x.State == "live" && x.Cod != o.Due ? (object)o.Due : null, "stock", !o.Restocked);
                return d;
            }
            catch (Exception ex) { Log.Error(ex); return null; }
        }

        // ---------- the owner's calls (ShopDesk passes here what it doesn't know) ----------
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (!a.StartsWith("ship", StringComparison.Ordinal) || !On(c, s)) return false; int n; int.TryParse(req.QueryString["page"], out n);
            switch (a)
            {
                case "ship": Web.Json(res, 200, Tab(c, s)); return true;
                case "ship-list": Web.Json(res, 200, List(c, s, req.QueryString["f"] ?? "", n)); return true;
                case "ship-areas": if (Of(req.QueryString["c"] ?? "") == null) return false; Web.Json(res, 200, D("list", CarrierAreas.Search(c, req.QueryString["c"], ShopJson.Int(req.QueryString["gov"]), req.QueryString["q"] ?? "").Select(x => D("k", x.Key, "t", x.Label, "off", x.Closed)).ToArray())); return true;
            }
            return false;
        }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null; if (!a.StartsWith("ship-", StringComparison.Ordinal) || !On(c, s)) return false;
            switch (a)
            {
                case "ship-link": ShopShipPick.Forget(s.Id); later = LinkUp(c, s, b, res); return true;
                case "ship-unlink": Exec(c, "DELETE FROM dbo.Carriers WHERE StoreId=@s AND Company=@c", "@s", s.Id, "@c", Str(b, "c", 10)); ShopShipPick.Forget(s.Id); Web.Json(res, 200, Tab(c, s)); return true;
                case "ship-set": SaveSet(c, s, b); Web.Json(res, 200, Tab(c, s)); return true;
                case "ship-check": CheckMany(c, s, sh, b, res); return true;
                case "ship-send": later = SendMany(c, s, b, res); return true;
                case "ship-labels": later = LabelsFor(c, s, b, res); return true;
                case "ship-pickup": later = AskPickup(c, s, b, res); return true;
                case "ship-cancel": later = CancelFor(c, s, b, res); return true;
                case "ship-ask": later = AskNow(c, s, b, res); return true;
                case "ship-drop": Drop(c, s, b, res); return true;
                case "ship-fix": later = Fix(c, s, b, res); return true;   // CarrierAramex.cs: the number the owner found in their account
                case "ship-got": Got(c, s, b, res); return true;
            }
            return false;
        }

        // ---------- sending ----------
        // null = can be sent, otherwise why not
        static string CantSend(ShopOrder o)
        {
            if (o == null) return "الأوردر ده مش موجود.";
            if (o.Extras.ContainsKey("branch")) return "الأوردر ده استلام من المحل.";
            switch (o.Status) { case "confirmed": return null; case "new": case "noreply": return "أكّد الأوردر الأول."; case "cancelled": case "returned": return "الأوردر ده " + ShopOrders.StatusText(o.Status) + "."; default: return "الأوردر ده خرج للشحن قبل كده."; }
        }
        static string Already(CarrierShip x)
        {
            if (x == null) return null; var n = Of(x.Company) == null ? x.Company : Of(x.Company).Name;
            if (x.State == "live") return "متبعت لـ " + n + " برقم " + x.Tracking + ".";
            if (x.State == "done" && x.Ours == "delivered") return "الشحنة دي اتسلّمت خلاص.";
            if (x.State == "making" && DateTime.UtcNow - x.ClaimedAt < TimeSpan.FromMinutes(2)) return "بيتبعت دلوقتي.";
            return null;
        }
        static string GoodsText(ShopOrder o) { return Cut(string.Join("، ", o.Items.Select(i => i.Name + (string.IsNullOrEmpty(i.Opts) ? "" : " (" + i.Opts + ")") + " × " + i.Qty)), 250); }

        static void CheckMany(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var co = Of(Str(b, "c", 10)); var k = Need(c, s.Id, Str(b, "c", 10)); var ids = ShopJson.List(ShopJson.At(b, "ids")).Select(x => (long)ShopJson.Int(x)).Distinct().Take(60).ToList();
            bool empty = CarrierAreas.Count(c, co.Id) == 0; if (empty) HostingEnvironment.QueueBackgroundWorkItem(ct => CarrierAreas.Refresh(co, true, ct));
            var rows = ids.Select(i => ShopOrders.ById(c, s.Id, i)).Where(o => o != null).Select(o =>
            {
                string err = CantSend(o) ?? Already(OfOrder(c, s.Id, o.Id)); if (err == null && co.MaxCod > 0 && o.Due > co.MaxCod) err = co.Name + " بتحصّل لحد " + ShopOrders.Money(co.MaxCod) + " بس.";
                var h = err != null || empty ? null : CarrierAreas.Match(c, s.Id, co.Id, o.Gov, o.City, o.Area); var warn = new List<string>();
                if (o.Deposit > 0 && o.PayState != "paid") warn.Add(o.PayState == "sent" ? "راجع صورة التحويل الأول" : "لسه ماحوّلش، فالمندوب هيحصّل المبلغ كله"); if (Crm.Of(c, s.Id, o.Phone).Mark == "block") warn.Add("متعلّم «ماتشحنلوش»");
                if (h != null && h.Row == null && h.Closed) warn.Add(co.Name + " مابتوصّلش للمنطقة دي");
                return D("id", o.Id, "no", o.No, "name", o.Name, "place", string.Join("، ", new[] { ShopGeo.Name(o.Gov), o.City, o.Area }.Where(x => !string.IsNullOrWhiteSpace(x))), "addr", Cut(o.Address, 120), "gov", o.Gov, "cod", o.Due, "err", err ?? "", "warn", warn, "pick", ShopShipPick.KeyOf(o), "key", h != null && h.Sure ? h.Row.Key : "", "area", h != null && h.Sure ? h.Row.Label : "", "picked", h != null && h.Picked, "more", h == null ? new object[0] : h.More.Take(12).Select(x => (object)D("k", x.Key, "t", x.Label)).ToArray());
            }).ToArray();
            Ok(res, "list", rows, "need", co.NotReady(k) ?? "", "empty", empty, "open", k.B("open", false));
        }

        static Func<Task> SendMany(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var co = Of(Str(b, "c", 10)); var k0 = Need(c, s.Id, Str(b, "c", 10)); var need = co.NotReady(k0); if (need != null) throw new ShopException("set", need);
            var items = ShopJson.List(ShopJson.At(b, "items")).OfType<Dictionary<string, object>>().Select(x => new KeyValuePair<long, string>(ShopJson.Int(ShopJson.At(x, "id")), Str(x, "area", 140))).GroupBy(x => x.Key).Select(g => g.First()).Take(20).ToList();
            if (items.Count == 0) throw new ShopException("none", "علّم على أوردر واحد على الأقل."); if (!Web.Allow("ship-send:" + s.Id, 120, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", "بعت كتير في وقت قصير. استنى شوية وكمّل.");
            string origin = Web.Setting("CarrierHookBase") ?? EasyOrders.Origin(HttpContext.Current == null ? null : HttpContext.Current.Request);
            return async () =>
            {
                var l = new List<object>(); string stop = null;
                foreach (var i in items)
                {
                    // the company isn't answering: the rest stay confirmed, and the owner has the Excel sheet as before
                    if (stop != null) { l.Add(D("id", i.Key, "ok", false, "err", stop)); continue; }
                    try { var m = await SendOne(s.Id, co, i.Key, i.Value, origin); l.Add(D("id", i.Key, "ok", true, "tracking", m.Tracking, "had", m.State == "had")); }
                    catch (ShopException ex) { l.Add(D("id", i.Key, "ok", false, "err", ex.Message, "code", ex.Code)); if (ex.Code == "net" || ex.Code == "badkey") stop = ex.Message; }
                }
                Web.Json(res, 200, D("ok", true, "list", l));
            };
        }

        static async Task<CarrierShip> SendOne(int sid, Carrier co, long oid, string areaKey, string origin)
        {
            CarrierLink k; CarrierParcel p; CarrierShip row; bool look;
            using (var c = Db.Open())
            {
                var s = Db.StoreById(c, sid); var sh = Shops.ByStore(c, sid); var o = ShopOrders.ById(c, sid, oid); var cant = CantSend(o); if (cant != null) throw new ShopException("cant", cant);
                var last = OfOrder(c, sid, oid); if (last != null && last.State == "live") return Had(last); var had = Already(last); if (had != null) throw new ShopException("already", had);
                if (last != null && (last.State == "unsure" || last.State == "making") && last.Company != co.Id) throw new ShopException("unsure", "لسه مش متأكدين من شحنة " + Of(last.Company).Name + " بتاعة الأوردر ده. اتأكد منها الأول.");
                k = Need(c, sid, co.Id); if (co.MaxCod > 0 && o.Due > co.MaxCod) throw new ShopException("cod", co.Name + " بتحصّل لحد " + ShopOrders.Money(co.MaxCod) + " بس.");
                CarrierAreaRow area;
                if (areaKey.Length > 0)
                {
                    area = CarrierAreas.ByKey(c, co.Id, areaKey); if (area == null || area.Closed) throw new ShopException("area", "اختار المنطقة من القايمة تاني.");
                    var h = CarrierAreas.Match(c, sid, co.Id, o.Gov, o.City, o.Area); if (!h.Sure || h.Row.Key != area.Key) CarrierAreas.Remember(c, sid, co.Id, o.Gov, o.City, o.Area, area);
                }
                else { var h = CarrierAreas.Match(c, sid, co.Id, o.Gov, o.City, o.Area); if (!h.Sure) throw new ShopException("area", h.Closed ? co.Name + " مابتوصّلش للمنطقة دي." : CarrierAreas.Count(c, co.Id) == 0 ? "لسه بنجيب مناطق " + co.Name + ". استنى دقيقة وجرّب تاني." : "اختار منطقة الأوردر عند " + co.Name + "."); area = h.Row; }
                if (co.Id == "bosta" && k.B("open", false) && !o.Items.Any(i => !string.IsNullOrEmpty(i.Image))) throw new ShopException("pics", "بوسطة بتطلب صورة للمنتج عشان الزبون يفتح الشحنة. ضيف صورة للمنتج، أو شيل «مسموح بفتح الشحنة» من إعدادات بوسطة.");
                row = Claim(c, o, co, area, last, out look);
                p = Parcel(c, s, sh, o, k, area, row.Ref, origin); ShopShip2.Parcel(c, sh, o, p);
            }
            // maybe: the company may have it, so nothing is created again before it says it doesn't
            CarrierException why = null; CarrierMade made = null; bool maybe = look;
            try
            {
                if (look && !co.GuardsDup) { made = await co.Find(k, row.Ref); maybe = made != null; }   // a press before this one got no answer
                if (made == null)
                    try { made = await co.Create(k, p); maybe = true; }
                    catch (CarrierException ex) when (ex.Unsure || ex.Code == "dup") { maybe = true; try { made = await co.Find(k, row.Ref); } catch (CarrierException) { } if (made == null) throw; }
                if (string.IsNullOrEmpty(made.Tracking)) { var f = await co.Find(k, row.Ref); if (f != null) made = f; }
                if (string.IsNullOrEmpty(made.Tracking)) throw new CarrierException(co.Name + " سجّلت الشحنة من غير رقم تتبع.", "notrack", unsure: true);
            }
            catch (CarrierException ex) { why = ex; }
            catch (Exception ex) { Log.Error(ex); maybe = true; why = new CarrierException("حصلت مشكلة عندنا.", "bug", unsure: true); }
            using (var c = Db.Open())
            {
                if (why == null) { Made(c, row, made); return ById(c, sid, row.Id); }
                string text = maybe && !(co.GuardsDup && why.Code == "dup") ? why.Message + " مش متأكدين إن الشحنة اتسجّلت، فهنسأل الشركة عنها قبل أي محاولة تانية." : why.Message;
                Exec(c, "UPDATE dbo.Shipments SET State=@st, Error=@e, ClaimedAt=SYSUTCDATETIME() WHERE Id=@i AND State='making'", "@st", maybe ? "unsure" : "failed", "@e", Cut(text, 300), "@i", row.Id);
                ShopOrders.Log_(c, null, sid, oid, "system", Cut("ماتبعتش لـ " + co.Name + ": " + text, 290));
                if (why.BadKey) Fault(c, k, why.Message);
                throw new ShopException(why.BadKey ? "badkey" : why.Code == "net" || why.Code == "area" ? why.Code : "carrier", text);   // area: the owner's list asks for it again
            }
        }
        static CarrierShip Had(CarrierShip x) { x.State = "had"; return x; }

        // the order's one open row: a new one, or the one a refused / unanswered press left
        static CarrierShip Claim(SqlConnection c, ShopOrder o, Carrier co, CarrierAreaRow area, CarrierShip last, out bool look)
        {
            string id = o.Id.ToString(CultureInfo.InvariantCulture); look = false;
            if (last != null && (last.State == "failed" || last.State == "unsure" || last.State == "making"))
            {
                look = last.State != "failed";
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Shipments SET State='making', ClaimedAt=SYSUTCDATETIME(), Company=@c, Cod=@cod, Area=@a, Phone=@p, Error=NULL WHERE Id=@i AND State=@st AND (State='failed' OR ClaimedAt<DATEADD(SECOND,-@w,SYSUTCDATETIME()))", "@c", co.Id, "@cod", o.Due, "@a", Cut(area.Label, 200), "@p", o.Phone, "@i", last.Id, "@st", last.State, "@w", UnsureWait))
                    if (cmd.ExecuteNonQuery() == 0) throw new ShopException("busy", look ? "لسه بنتأكد من المحاولة اللي فاتت. استنى دقيقة وجرّب تاني." : "الأوردر ده بيتبعت دلوقتي.");
                return ById(c, o.StoreId, last.Id);
            }
            // a shipment cancelled or returned before this one: its reference is taken at the company
            int n = Num(c, "SELECT COUNT(*) FROM dbo.Shipments WHERE StoreId=@s AND Source='shop' AND OrderId=@o", "@s", o.StoreId, "@o", id);
            try
            {
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Shipments(StoreId, Source, OrderId, OrderName, Company, Ref, Phone, State, Cod, Area) OUTPUT INSERTED.Id VALUES(@s, 'shop', @o, @n, @c, @r, @p, 'making', @cod, @a)", "@s", o.StoreId, "@o", id, "@n", o.NoText, "@c", co.Id, "@r", "SS" + o.StoreId + "-" + o.No + (n == 0 ? "" : "-" + (n + 1)), "@p", o.Phone, "@cod", o.Due, "@a", Cut(area.Label, 200)))
                    return ById(c, o.StoreId, Convert.ToInt64(cmd.ExecuteScalar()));
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { throw new ShopException("busy", "الأوردر ده بيتبعت دلوقتي."); }
        }
        static int UnsureWait { get { int v; return int.TryParse(Web.Setting("CarrierUnsureSec"), out v) && v >= 0 ? v : 60; } }   // a setting only for tests

        static CarrierParcel Parcel(SqlConnection c, StoreInfo s, Shop sh, ShopOrder o, CarrierLink k, CarrierAreaRow area, string reference, string origin)
        {
            // a gift goes to the person the customer named; the customer's own number is the second one
            var rc = ShopJson.Sub(o.Extras, "recv"); bool other = rc.Count > 0 && ShopJson.Str(ShopJson.At(rc, "p")).Length > 0; string to = other ? ShopJson.Str(ShopJson.At(rc, "p")) : o.Phone, second = o.Phone2 ?? (other ? o.Phone : null);
            string note = string.Join(" - ", new[] { o.Note, k.S("note") }.Where(x => !string.IsNullOrWhiteSpace(x))), addr = string.Join("، ", new[] { o.City, o.Area, o.Address }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new CarrierParcel { Ref = reference, OrderNo = o.No.ToString(CultureInfo.InvariantCulture), Name = other ? ShopJson.Str(ShopJson.At(rc, "n"), 80) : o.Name, Phone = ShopOrders.Local(to), Phone2 = second == null || second == to ? null : ShopOrders.Local(second), Email = k.S("email"), Address = addr, Landmark = o.Landmark, Note = Regex.Replace(note, @"\s+", " "), Goods = GoodsText(o),
                Items = o.Items.Sum(i => i.Qty), Cod = o.Due, Value = Math.Max(0, o.Subtotal - o.Discount), Area = area, Lines = o.Items, Pics = o.Items.Select(i => i.Image).Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(10).Select(x => ShopImages.Url(x)).ToList(),
                HookUrl = string.IsNullOrEmpty(origin) ? null : origin + HookPath + "?c=" + k.Company + "&h=" + k.HookId, HookSecret = k.HookSecret };
        }

        internal static void Made(SqlConnection c, CarrierShip x, CarrierMade m)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shipments SET State='live', Tracking=@t, TheirId=@h, Ours=ISNULL(Ours,'made'), Error=NULL, CheckedAt=SYSUTCDATETIME() WHERE Id=@i AND State IN ('making','unsure') AND Tracking IS NULL", "@t", Cut(m.Tracking, 60), "@h", Nz(Cut(m.Id, 60)), "@i", x.Id)) if (cmd.ExecuteNonQuery() == 0) return;
            Event(c, x.StoreId, x.Id, "made", "made", "اتسجّلت عند " + Of(x.Company).Name + " برقم " + m.Tracking, DateTime.UtcNow, "own");
            if (x.Source != "shop") return; long oid = long.Parse(x.OrderId, CultureInfo.InvariantCulture);
            Exec(c, "UPDATE dbo.ShopOrders SET Carrier=@c, Tracking=@t WHERE Id=@i AND StoreId=@s", "@c", Of(x.Company).Name, "@t", Cut(m.Tracking, 60), "@i", oid, "@s", x.StoreId);
            ShopOrders.Log_(c, null, x.StoreId, oid, "owner", "اتبعت لـ " + Of(x.Company).Name + "، رقم التتبع " + m.Tracking + (x.Cod > 0 ? "، والتحصيل " + ShopOrders.Money(x.Cod) : "، من غير تحصيل"));
        }
        // false = already known
        internal static bool Event(SqlConnection c, int sid, long shipId, string code, string ours, string text, DateTime at, string src)
        {
            try { Exec(c, "INSERT INTO dbo.ShipmentEvents(StoreId, ShipmentId, Code, Ours, Text, At, Src) VALUES(@s, @i, @c, @o, @t, @a, @src)", "@s", sid, "@i", shipId, "@c", Cut(code, 40), "@o", ours, "@t", Nz(Cut(text, 300)), "@a", at, "@src", src); return true; }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return false; }
        }

        // ---------- labels ----------
        static Func<Task> LabelsFor(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var ids = ShopJson.List(ShopJson.At(b, "ids")).Select(x => (long)ShopJson.Int(x)).Distinct().Take(200).ToList(); var all = ids.Select(i => OfOrder(c, s.Id, i)).Where(x => x != null && !string.IsNullOrEmpty(x.Tracking)).ToList(); var l = all.Where(x => x.State == "live").ToList();   // a finished shipment has no waybill to print (and fails a whole Bosta batch)
            if (l.Count == 0) throw new ShopException("none", all.Count > 0 ? "الأوردرات دي اتقفلت خلاص، مفيش بوليصة تتطبع." : "الأوردرات دي لسه ماتبعتتش لشركة شحن."); if (!Web.Allow("ship-label:" + s.Id, 60, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", "طلبات كتير. استنى شوية.");
            var links = l.Select(x => x.Company).Distinct().ToDictionary(x => x, x => Link(c, s.Id, x));
            return async () =>
            {
                var pdfs = new List<byte[]>(); var bad = new List<string>(); var printed = new List<long>();
                foreach (var g in l.GroupBy(x => x.Company))
                {
                    var co = Of(g.Key); var k = links[g.Key]; if (k == null || string.IsNullOrEmpty(k.Key)) { bad.Add(co.Name + ": الربط اتشال"); continue; }
                    // a company that prints one label a call: 60 at most in one go, the request has a few minutes
                    var mine = g.ToList(); if (co.MaxLabels == 1 && mine.Count > 60) { bad.Add(co.Name + ": اتطبع 60 بس، اطبع الباقي مرة تانية"); mine = mine.Take(60).ToList(); }
                    foreach (var part in mine.Select((x, i) => new { x, i }).GroupBy(y => y.i / co.MaxLabels).Select(y => y.Select(z => z.x).ToList()))
                        try { var pdf = await co.Labels(k, part, k.S("label", "A4")); pdfs.Add(pdf); printed.AddRange(part.Select(x => x.Id)); }
                        catch (CarrierException ex) { bad.Add(ex.Message); if (ex.BadKey) using (var c2 = Db.Open()) Fault(c2, k, ex.Message); if (ex.Code == "net" || ex.BadKey) break; }
                }
                if (pdfs.Count == 0) throw new ShopException("labels", string.Join(" · ", bad));
                using (var c2 = Db.Open()) Exec(c2, "UPDATE dbo.Shipments SET PrintedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Id IN (" + string.Join(",", printed) + ")", "@s", s.Id);
                string name = "labels-" + Cairo(DateTime.UtcNow).ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture); var one = pdfs.Count == 1 ? pdfs[0] : CarrierPdf.Join(pdfs);
                res.Cache.SetCacheability(HttpCacheability.NoCache); res.AddHeader("X-Content-Type-Options", "nosniff"); if (bad.Count > 0) res.AddHeader("X-Ship-Note", Uri.EscapeDataString(Cut(string.Join(" · ", bad), 400)));
                res.ContentType = one != null ? "application/pdf" : "application/zip"; res.AddHeader("Content-Disposition", (one != null ? "inline" : "attachment") + "; filename=" + name + (one != null ? ".pdf" : ".zip"));
                res.BinaryWrite(one ?? CarrierPdf.Zip(pdfs));
            };
        }

        // ---------- pickup ----------
        static Func<Task> AskPickup(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var co = Of(Str(b, "c", 10)); var k = Need(c, s.Id, Str(b, "c", 10)); if (!co.PickupCall) throw new ShopException("pickup", "مندوب " + co.Name + " بييجي مع الأوردر نفسه، في الميعاد اللي في الإعدادات.");
            DateTime day; if (!DateTime.TryParseExact(Str(b, "date", 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) || day < Cairo(DateTime.UtcNow).Date || day > Cairo(DateTime.UtcNow).Date.AddDays(30)) throw new ShopException("date", "اختار يوم من النهارده لحد شهر قدام.");
            if (day.DayOfWeek == DayOfWeek.Friday) throw new ShopException("date", "مفيش استلام يوم الجمعة. اختار يوم تاني.");
            var wait = Ships(c, 100, "StoreId=@s AND Company=@c AND State='live' AND ISNULL(Ours,'made')='made' ORDER BY Id", "@s", s.Id, "@c", co.Id); int n = Math.Max(1, Math.Min(500, Int(b, "n", Math.Max(1, wait.Count)))); string place = Str(b, "place", 60), note = Str(b, "note", 200);
            if (!Web.Allow("ship-pick:" + s.Id, 20, TimeSpan.FromHours(1))) throw new ShopException("too_many", "طلبات كتير. استنى شوية.");
            return async () =>
            {
                string no; try { no = await co.Pickup(k, day, n, place.Length == 0 ? k.S("place") : place, note, wait); } catch (CarrierException ex) { if (ex.BadKey) using (var c2 = Db.Open()) Fault(c2, k, ex.Message); throw Said(ex); }
                string warn = no != null && no.StartsWith("?", StringComparison.Ordinal) ? no.Substring(1) : ""; if (warn.Length > 0) no = "";   // the company may not have taken it (ShipBlu)
                using (var c2 = Db.Open()) { k.Set["pickup"] = D("date", day.ToString("yyyy-MM-dd"), "n", n, "no", no ?? ""); Exec(c2, "UPDATE dbo.Carriers SET Defaults=@d WHERE StoreId=@s AND Company=@c", "@d", ShopJson.Write(k.Set), "@s", s.Id, "@c", co.Id); }
                Ok(res, "no", no ?? "", "warn", warn);
            };
        }

        // ---------- cancelling before the courier takes it ----------
        static Func<Task> CancelFor(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = ById(c, s.Id, Int(b, "id")); if (x == null) throw new ShopException("gone", "الشحنة دي مش موجودة."); if (x.State != "live") throw new ShopException("state", "الشحنة دي مش مفتوحة."); if (x.Picked) throw new ShopException("picked", "الشحنة مع المندوب خلاص، فالإلغاء من شركة الشحن نفسها.");
            var k = Need(c, s.Id, x.Company);
            return async () => { try { await CancelNow(k, x, "owner"); } catch (CarrierException ex) { throw Said(ex); } Ok(res); };
        }
        internal static async Task CancelNow(CarrierLink k, CarrierShip x, string who)
        {
            await Of(x.Company).Cancel(k, x);
            using (var c = Db.Open()) Off(c, x, "الشحنة اتلغت عند " + Of(x.Company).Name, who);
        }
        // closed before it left: the order can be sent again, with this company or another
        internal static void Off(SqlConnection c, CarrierShip x, string text, string who)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shipments SET State='off', Ours='cancelled', DoneAt=SYSUTCDATETIME(), RescueDue=NULL WHERE Id=@i AND State IN ('live','unsure','making')", "@i", x.Id)) if (cmd.ExecuteNonQuery() == 0) return;
            Event(c, x.StoreId, x.Id, "off", "cancelled", text, DateTime.UtcNow, "own"); if (x.Source != "shop") return; long oid = long.Parse(x.OrderId, CultureInfo.InvariantCulture);
            Exec(c, "UPDATE dbo.ShopOrders SET Carrier=NULL, Tracking=NULL WHERE Id=@i AND StoreId=@s AND Tracking=@t", "@i", oid, "@s", x.StoreId, "@t", x.Tracking ?? ""); ShopOrders.Log_(c, null, x.StoreId, oid, who, text);
        }
        // the order was cancelled (the owner, or the customer from their account) while its shipment still waits for the courier
        public static void OnCancelled(int storeId, long orderId)
        {
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    CarrierLink k; CarrierShip x; using (var c = Db.Open()) { x = OfOrder(c, storeId, orderId); if (x == null || x.State != "live" || x.Picked) return; k = Link(c, storeId, x.Company); }
                    string why = null; try { if (k == null) why = "الربط اتشال"; else await CancelNow(k, x, "system"); } catch (CarrierException ex) { why = ex.Message; }
                    if (why != null) ShopHooks.Tell(storeId, "ship", "⚠️ *أوردر " + x.OrderName + " اتلغى وشحنته لسه عند " + Of(x.Company).Name + "*\nرقم التتبع: " + x.Tracking + "\n" + why + "\n\nالغيها من حسابك عندهم قبل ما المندوب ييجي.", "sb-ship-cx-" + x.Id);
                }
                catch (Exception ex) { Log.Error(ex); }
            });
        }
        // the owner looked at the company's own page: the shipment we weren't sure of isn't there
        static void Drop(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = ById(c, s.Id, Int(b, "id")); if (x == null || x.State != "unsure") throw new ShopException("state", "الشحنة دي مش محتاجة تأكيد.");
            Exec(c, "UPDATE dbo.Shipments SET State='failed', Error=@e WHERE Id=@i AND State='unsure'", "@e", "قلت إنها مش موجودة عند الشركة", "@i", x.Id); ShopOrders.Log_(c, null, s.Id, long.Parse(x.OrderId, CultureInfo.InvariantCulture), "owner", "أكّدت إن الشحنة مش موجودة عند " + Of(x.Company).Name); Ok(res);
        }
        // «وصلتني»: the parcel that came back is in the owner's hands; the quantity goes back now (unless it's damaged)
        static void Got(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = ById(c, s.Id, Int(b, "id")); if (x != null && x.Source == "ret") { ShopShipBack.Got(c, s, Shops.ByStore(c, s.Id), x, b, res); return; }
            if (x == null || !x.BackAt.HasValue) throw new ShopException("state", "الشحنة دي لسه مارجعتش.");
            var o = ShopOrders.ById(c, s.Id, long.Parse(x.OrderId, CultureInfo.InvariantCulture)); bool stock = ShopJson.Bool(ShopJson.At(b, "restock"), true), moved = false;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Shipments SET GotAt=SYSUTCDATETIME() WHERE Id=@i AND GotAt IS NULL", "@i", x.Id)) { cmd.Transaction = tx; if (cmd.ExecuteNonQuery() == 0) { tx.Rollback(); Ok(res, "moved", false); return; } }
                    if (stock && o != null)
                    {
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Restocked=1 WHERE Id=@i AND StoreId=@s AND Restocked=0 AND Status='returned'", "@i", o.Id, "@s", s.Id)) { cmd.Transaction = tx; moved = cmd.ExecuteNonQuery() == 1; }
                        if (moved) { ShopOrders.Return(c, tx, o, 1, "returned"); using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@s", s.Id)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } }
                    }
                    if (o != null) ShopOrders.Log_(c, tx, s.Id, o.Id, "owner", "المرتجع وصل" + (moved ? "، والكمية رجعت للمخزون" : stock ? "" : "، من غير ما الكمية ترجع"));
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            Ok(res, "moved", moved);
        }
    }
}
