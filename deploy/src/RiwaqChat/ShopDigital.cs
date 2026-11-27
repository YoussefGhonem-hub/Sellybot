// SellyStore: «منتج رقمي». A product can be a file the owner uploads (PDF, ZIP, EPUB, MP3, MP4, pictures; up to 50 MB; kept under App_Data,
// never public), a link (a course, a drive folder), or a list of codes (one code per piece sold; no codes left = sold out, the codes are the
// stock). A digital line is paid before it is delivered (by card, or a transfer the owner approved: ShopCard.AfterPaid), never cash on
// delivery; a cart of digital lines only has no shipping and no address. After payment each line gets a private link
// (<store>/_dl/<64 hex>, valid DigiDays days and DigiMax downloads, both the owner's) by WhatsApp, on the order's page and in the account;
// an all-digital order is delivered by itself. Every download is logged. A cancelled order closes its links, and codes nobody opened go
// back to the list. On/off: ShopCardSet.DigiOn (off = digital products aren't sold, nothing else changes).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class DigiRow { public int ProductId; public string Kind, FileKey, FileName, FileType, Link; public long FileBytes; public bool Multi; }

    public static class ShopDigital
    {
        public const long MaxFile = 50L * 1024 * 1024; public const int Chunk = 1500000, MaxCodes = 5000;
        public static long MaxStoreBytes { get { long mb; return long.TryParse(Web.Setting("DigiStoreMb"), out mb) && mb > 0 ? mb * 1048576 : 300L * 1048576; } }
        internal static string Dir { get { return Web.Setting("ShopFileDir") ?? HostingEnvironment.MapPath("~/App_Data/shop-files"); } }
        static string PathOf(int storeId, string key) { return Path.Combine(Dir, storeId.ToString(CultureInfo.InvariantCulture), key); }
        static string Sha(string s) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2"))); }
        static void Exec(SqlConnection c, string sql, params object[] a) { ShopCard.Exec(c, sql, a); }
        static int Num(SqlConnection c, string sql, params object[] a) { return ShopCard.Num(c, sql, a); }
        static Dictionary<string, object> D(params object[] kv) { return ShopCard.D(kv); }
        static string T(ShopFrontHandler.Ctx x, string ar, string en) { return x.Lang == "en" ? en : ar; }

        public const string Schema = @"
IF OBJECT_ID('dbo.ShopDigital') IS NULL
  CREATE TABLE dbo.ShopDigital(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, Kind NVARCHAR(6) NOT NULL, FileKey NVARCHAR(80) NULL, FileName NVARCHAR(160) NULL,
    FileType NVARCHAR(60) NULL, FileBytes BIGINT NOT NULL DEFAULT 0, Link NVARCHAR(600) NULL, Multi BIT NOT NULL DEFAULT 0, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), PRIMARY KEY(StoreId, ProductId));
IF OBJECT_ID('dbo.ShopDigiCodes') IS NULL
BEGIN
  CREATE TABLE dbo.ShopDigiCodes(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, CodeBox NVARCHAR(MAX) NOT NULL, Hash NVARCHAR(64) NOT NULL,
    OrderId BIGINT NULL, ItemId BIGINT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UsedAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopDigiCodes_Hash ON dbo.ShopDigiCodes(StoreId, ProductId, Hash);
  CREATE INDEX IX_ShopDigiCodes_Free ON dbo.ShopDigiCodes(StoreId, ProductId, OrderId);
  CREATE INDEX IX_ShopDigiCodes_Item ON dbo.ShopDigiCodes(ItemId);
END
IF OBJECT_ID('dbo.ShopDigiLinks') IS NULL
BEGIN
  CREATE TABLE dbo.ShopDigiLinks(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, ItemId BIGINT NOT NULL, ProductId INT NOT NULL,
    Hash NVARCHAR(64) NOT NULL, TokenBox NVARCHAR(MAX) NOT NULL, ExpiresAt DATETIME2 NOT NULL, MaxN INT NOT NULL, N INT NOT NULL DEFAULT 0, Revoked BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), SendAt DATETIME2 NULL, SentAt DATETIME2 NULL, LastAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopDigiLinks_Hash ON dbo.ShopDigiLinks(Hash);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopDigiLinks_Item ON dbo.ShopDigiLinks(ItemId) WHERE Revoked=0;';
  CREATE INDEX IX_ShopDigiLinks_Order ON dbo.ShopDigiLinks(OrderId);
  CREATE INDEX IX_ShopDigiLinks_Send ON dbo.ShopDigiLinks(SentAt, SendAt);
END
IF OBJECT_ID('dbo.ShopDigiLog') IS NULL
BEGIN
  CREATE TABLE dbo.ShopDigiLog(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, LinkId BIGINT NOT NULL, Ok BIT NOT NULL, Why NVARCHAR(20) NULL, Ip NVARCHAR(64) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopDigiLog_Link ON dbo.ShopDigiLog(LinkId, Id);
END
";

        // ---------- the catalog (ShopCache.Load -> ShopCard.Load -> here) ----------
        static readonly ConcurrentDictionary<int, Dictionary<int, DigiRow>> All = new ConcurrentDictionary<int, Dictionary<int, DigiRow>>();
        static Dictionary<int, DigiRow> Read(SqlConnection c, int storeId)
        {
            var l = new Dictionary<int, DigiRow>(); using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopDigital WHERE StoreId=@s", "@s", storeId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) { var x = new DigiRow { ProductId = (int)r["ProductId"], Kind = (string)r["Kind"], FileKey = r["FileKey"] as string, FileName = r["FileName"] as string, FileType = r["FileType"] as string, FileBytes = Convert.ToInt64(r["FileBytes"]), Link = r["Link"] as string, Multi = (bool)r["Multi"] }; l[x.ProductId] = x; }
            return l;
        }
        public static void Load(SqlConnection c, ShopData d, CardSet k)
        {
            var rows = Read(c, d.Row.StoreId); All[d.Row.StoreId] = rows;
            foreach (var x in rows.Values)
            {
                ShopProd p; if (!d.ById.TryGetValue(x.ProductId, out p)) continue;
                p.Digi = x.Kind; if (!x.Multi) p.MaxQty = 1;
                if (x.Kind == "codes") { p.Track = true; p.OutMode = "show"; } else p.Track = false;   // the codes are the stock; a file or a link never runs out
            }
        }
        static Dictionary<int, DigiRow> Rows(SqlConnection c, int storeId) { Dictionary<int, DigiRow> l; if (All.TryGetValue(storeId, out l)) return l; l = Read(c, storeId); All[storeId] = l; return l; }
        // can it be sold now: the feature is on and the product has what it delivers
        static bool Sellable(int storeId, ShopProd p) { if (p.Digi == null) return true; Dictionary<int, DigiRow> l; DigiRow x; if (!ShopCard.Of(storeId).DigiOn || !All.TryGetValue(storeId, out l) || !l.TryGetValue(p.Id, out x)) return false; return x.Kind == "file" ? !string.IsNullOrEmpty(x.FileKey) : x.Kind == "link" ? !string.IsNullOrEmpty(x.Link) : true; }
        // the catalog's product (ShopView.Summary / Detail): what it is, and not on sale while it can't be delivered
        public static void Mark(ShopData d, ShopProd p, Dictionary<string, object> r)
        {
            if (p.Digi == null) return; r["dg"] = p.Digi; if (Sellable(d.Row.StoreId, p)) return;
            r["av"] = false; var vs = r.ContainsKey("vs") ? r["vs"] as object[] : null; if (vs != null) foreach (var v in vs.OfType<object[]>()) if (v.Length > 2 && v[2] is int) v[2] = 0;
            var vars = r.ContainsKey("vars") ? r["vars"] as Dictionary<string, object>[] : null; if (vars != null) foreach (var v in vars) v["can"] = false;
        }
        // the cart's sum (ShopPrice.Quote, before the total): only digital lines = no delivery, no cash fee, no physical gift
        public static void Quote(ShopData d, ShopQuote q, List<ShopLine> ok)
        {
            if (ok.Count == 0 || !ok.Any(l => l.P.Digi != null)) return;
            foreach (var l in q.Lines.Where(x => x.P != null && x.P.Digi != null && x.Err == null)) if (!Sellable(d.Row.StoreId, l.P)) l.Err = "gone";
            if (!ok.All(l => l.P.Digi != null)) return;
            q.Lines.RemoveAll(l => l.Gift > 0 && (l.P == null || l.P.Digi == null)); q.Ship = 0; q.ShipFull = 0; q.FreeLeft = 0; q.FreeShip = true; q.Days = ""; q.Deliver = true; q.Branch = null; q.Fees.RemoveAll(f => f.Kind == "cod");
        }
        // an order of digital lines only (ShopOrders.Place, before its checks): no governorate, no address
        public static bool Fill(SqlConnection c, Shop row, ShopOrderIn o)
        {
            if (o.Lines.Count == 0) return false; var d = ShopCache.Get(c, row); if (d == null) return false;
            foreach (var l in o.Lines) { ShopVar v; if (!d.Vars.TryGetValue(l.VariantId, out v) || d.ById[v.ProductId].Digi == null) return false; }
            o.Gov = 0; o.City = "منتج رقمي"; o.Area = null; o.Landmark = null; o.Address = "منتج رقمي: مفيش شحن"; o.How.Branch = null; o.RecvName = null; o.RecvPhone = null;
            return true;
        }

        // ---------- delivering: after the payment (ShopCard.AfterPaid), or when the owner says so ----------
        // force = the owner gives it whatever was paid. Returns how many links were made.
        public static int Deliver(SqlConnection c, int storeId, long orderId, bool force)
        {
            var o = ShopOrders.ById(c, storeId, orderId); if (o == null || o.Status == "cancelled" || o.Status == "returned") return 0;
            if (!force && !(o.PayState == "paid" && o.Paid >= o.Total - 0.5m)) return 0;
            var rows = Rows(c, storeId); var items = o.Items.Where(i => rows.ContainsKey(i.ProductId)).ToList(); if (items.Count == 0) return 0;
            var k = ShopCard.Of(storeId); int made = 0; var short_ = new List<string>();
            foreach (var i in items)
            {
                if (rows[i.ProductId].Kind == "codes" && Assign(c, storeId, o, i) < i.Qty) short_.Add(i.Name);
                if (Num(c, "SELECT COUNT(*) FROM dbo.ShopDigiLinks WHERE ItemId=@i AND Revoked=0", "@i", i.Id) > 0) continue;
                string tok = Shops.Token(32);
                try { Exec(c, "INSERT INTO dbo.ShopDigiLinks(StoreId, OrderId, ItemId, ProductId, Hash, TokenBox, ExpiresAt, MaxN) VALUES(@s, @o, @i, @p, @h, @t, DATEADD(DAY, @d, SYSUTCDATETIME()), @m)", "@s", storeId, "@o", o.Id, "@i", i.Id, "@p", i.ProductId, "@h", Sha(tok), "@t", Secrets.Protect(tok), "@d", k.DigiDays, "@m", k.DigiMax); made++; }
                catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }   // made a moment ago by another request
            }
            foreach (var pid in items.Where(i => rows[i.ProductId].Kind == "codes").Select(i => i.ProductId).Distinct()) Sync(c, storeId, pid);
            if (made > 0) { ShopOrders.Log_(c, null, storeId, o.Id, "system", "لينكات المنتجات الرقمية اتعملت (" + made + ")"); Queue(c, storeId, o.Id); }
            if (short_.Count > 0 && Web.Allow("digi-short:" + o.Id, 1, TimeSpan.FromHours(6))) ShopHooks.Tell(storeId, "low", "🔑 *الأكواد خلصت*\nأوردر " + o.NoText + " مستني كود لـ: " + string.Join("، ", short_.Distinct()) + "\n\nضيف أكواد من «متجري» ثم المنتج، والكود بيوصل للزبون لوحده.", "sb-digi-short-" + o.Id);
            if (o.Items.All(i => rows.ContainsKey(i.ProductId)) && o.Status != "delivered" && short_.Count == 0) ShopOrders.SetStatus(c, storeId, o.Id, "delivered", "system", null);
            return made;
        }
        // the codes a line still lacks, from the oldest; the line's row is held so two requests can't both give it codes
        static int Assign(SqlConnection c, int storeId, ShopOrder o, ShopOrderItem i)
        {
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    Func<string, object[], object> one = (sql, a) => { using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; return cmd.ExecuteScalar(); } };
                    one("SELECT Id FROM dbo.ShopOrderItems WITH (UPDLOCK, ROWLOCK) WHERE Id=@i", new object[] { "@i", i.Id });
                    int had = Convert.ToInt32(one("SELECT COUNT(*) FROM dbo.ShopDigiCodes WHERE ItemId=@i", new object[] { "@i", i.Id }));
                    if (had < i.Qty) one(";WITH x AS (SELECT TOP (@n) * FROM dbo.ShopDigiCodes WITH (UPDLOCK, READPAST, ROWLOCK) WHERE StoreId=@s AND ProductId=@p AND OrderId IS NULL ORDER BY Id) UPDATE x SET OrderId=@o, ItemId=@i, UsedAt=SYSUTCDATETIME()", new object[] { "@n", i.Qty - had, "@s", storeId, "@p", i.ProductId, "@o", o.Id, "@i", i.Id });
                    int now = Convert.ToInt32(one("SELECT COUNT(*) FROM dbo.ShopDigiCodes WHERE ItemId=@i", new object[] { "@i", i.Id }));
                    tx.Commit(); return now;
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
        }
        // a code product's stock = the codes left less what open orders hold and haven't got yet. The store's row is held like an order does.
        public static void Sync(SqlConnection c, int storeId, int productId)
        {
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    Func<string, object> one = sql => { using (var cmd = Db.Cmd(c, sql, "@s", storeId, "@p", productId)) { cmd.Transaction = tx; return cmd.ExecuteScalar(); } };
                    one("UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s");
                    int free = Convert.ToInt32(one("SELECT COUNT(*) FROM dbo.ShopDigiCodes WHERE StoreId=@s AND ProductId=@p AND OrderId IS NULL"));
                    int held = Convert.ToInt32(one(@"SELECT ISNULL(SUM(CASE WHEN i.Qty>ISNULL(g.n,0) THEN i.Qty-ISNULL(g.n,0) ELSE 0 END),0) FROM dbo.ShopOrderItems i JOIN dbo.ShopOrders o ON o.Id=i.OrderId
                        LEFT JOIN (SELECT ItemId, COUNT(*) AS n FROM dbo.ShopDigiCodes WHERE StoreId=@s AND ProductId=@p AND ItemId IS NOT NULL GROUP BY ItemId) g ON g.ItemId=i.Id WHERE i.StoreId=@s AND i.ProductId=@p AND o.Status NOT IN ('cancelled','returned')"));
                    var moved = new List<int[]>();
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopVariants SET Stock=@x OUTPUT INSERTED.Id, DELETED.Stock WHERE StoreId=@s AND ProductId=@p AND Stock<>@x", "@x", Math.Max(0, free - held), "@s", storeId, "@p", productId)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) while (r.Read()) moved.Add(new[] { (int)r[0], (int)r[1] }); }
                    foreach (var m in moved) ShopOrders.Stock(c, tx, storeId, productId, m[0], Math.Max(0, free - held) - m[1], Math.Max(0, free - held), "codes", null, null);
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
        }
        // the links of an order go out on WhatsApp together, never at night
        static void Queue(SqlConnection c, int storeId, long orderId)
        {
            var quiet = WaNotify.QuietUntil(); Exec(c, "UPDATE dbo.ShopDigiLinks SET SendAt=ISNULL(@t, SYSUTCDATETIME()), SentAt=NULL WHERE StoreId=@s AND OrderId=@o AND Revoked=0", "@t", quiet.HasValue ? (object)quiet.Value : null, "@s", storeId, "@o", orderId);
            if (quiet == null) HostingEnvironment.QueueBackgroundWorkItem(ct => SendDue(ct));
        }
        public static async Task SendDue(CancellationToken ct)
        {
            if (WaNotify.QuietUntil() != null || !Shops.Ready) return;
            var l = new List<KeyValuePair<int, long>>(); using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "SELECT DISTINCT TOP 30 StoreId, OrderId FROM dbo.ShopDigiLinks WHERE SentAt IS NULL AND SendAt<=SYSUTCDATETIME() AND SendAt>DATEADD(DAY,-3,SYSUTCDATETIME()) AND Revoked=0")) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(new KeyValuePair<int, long>((int)r[0], Convert.ToInt64(r[1])));
            foreach (var x in l)
            {
                if (ct.IsCancellationRequested) return;
                using (var c = Db.Open())
                {
                    if (Num(c, "UPDATE dbo.ShopDigiLinks SET SentAt=SYSUTCDATETIME() WHERE StoreId=@s AND OrderId=@o AND SentAt IS NULL AND Revoked=0 AND SendAt<=SYSUTCDATETIME(); SELECT @@ROWCOUNT", "@s", x.Key, "@o", x.Value) == 0) continue;   // once
                    var o = ShopOrders.ById(c, x.Key, x.Value); var s = Db.StoreById(c, x.Key); var sh = Shops.ByStore(c, x.Key); if (o == null || s == null || sh == null || o.Phone.Length == 0) continue;
                    bool en = o.Lang == "en"; var links = Links(c, x.Key, o.Id).Where(z => !z.Revoked).ToList(); if (links.Count == 0) continue;
                    var t = new StringBuilder(en ? "📥 *" + (string.IsNullOrEmpty(sh.NameEn) ? sh.Name : sh.NameEn) + "*\nYour order " + o.NoText + " is ready:\n" : "📥 *" + sh.Name + "*\nطلبك " + o.NoText + " جاهز:\n");
                    foreach (var z in links) t.Append("• ").Append(z.Name).Append(z.Kind == "codes" ? (en ? " (your code): " : " (الكود): ") : ": ").Append(sh.Url).Append("/_dl/").Append(z.Token).Append("\n");
                    var k = ShopCard.Of(x.Key); t.Append(en ? "\nEach link works for " + k.DigiDays + " days and " + k.DigiMax + " downloads. It's yours only: don't share it." : "\nكل لينك شغال " + k.DigiDays + " يوم ولحد " + k.DigiMax + " مرات. اللينك ليك إنت بس، ماتبعتهوش لحد.");
                    var why = await WaNotify.ToCustomer(c, s, o.Phone, t.ToString(), "sb-digi-" + o.Id + "-" + DateTime.UtcNow.Ticks).ConfigureAwait(false);
                    ShopOrders.Log_(c, null, x.Key, o.Id, "system", why == null ? "لينكات التحميل اتبعتت على الواتساب" : "لينكات التحميل ماتبعتتش على الواتساب: " + why + " (موجودة في صفحة الطلب)");
                }
            }
        }
        // a cancel or a return closes the order's links; codes nobody opened go back to the list
        public static void OnStatus(SqlConnection c, int storeId, long orderId, string to)
        {
            if (!Shops.Ready || Num(c, "SELECT COUNT(*) FROM dbo.ShopOrderItems i JOIN dbo.ShopDigital d ON d.StoreId=i.StoreId AND d.ProductId=i.ProductId WHERE i.OrderId=@o AND i.StoreId=@s", "@o", orderId, "@s", storeId) == 0) return;
            if (to == "cancelled" || to == "returned")
            {
                Exec(c, @"UPDATE c SET OrderId=NULL, ItemId=NULL, UsedAt=NULL FROM dbo.ShopDigiCodes c WHERE c.StoreId=@s AND c.OrderId=@o AND NOT EXISTS (SELECT 1 FROM dbo.ShopDigiLinks l WHERE l.ItemId=c.ItemId AND l.N>0);
                    UPDATE dbo.ShopDigiLinks SET Revoked=1 WHERE StoreId=@s AND OrderId=@o AND Revoked=0", "@s", storeId, "@o", orderId);
            }
            var pids = new List<int>(); using (var cmd = Db.Cmd(c, "SELECT DISTINCT i.ProductId FROM dbo.ShopOrderItems i JOIN dbo.ShopDigital d ON d.StoreId=i.StoreId AND d.ProductId=i.ProductId AND d.Kind='codes' WHERE i.OrderId=@o AND i.StoreId=@s", "@o", orderId, "@s", storeId)) using (var r = cmd.ExecuteReader()) while (r.Read()) pids.Add((int)r[0]);
            foreach (var p in pids) Sync(c, storeId, p);
        }

        // ---------- the links of an order ----------
        class L { public long Id, ItemId; public int ProductId, N, MaxN, Qty, Codes; public string Name, Kind, Token; public bool Revoked; public DateTime Until, At; public DateTime? SentAt, LastAt; }
        static List<L> Links(SqlConnection c, int storeId, long orderId)
        {
            var l = new List<L>(); using (var cmd = Db.Cmd(c, @"SELECT l.*, i.Name, i.Qty, ISNULL(d.Kind,'file') AS Kind, (SELECT COUNT(*) FROM dbo.ShopDigiCodes x WHERE x.ItemId=l.ItemId) AS Codes FROM dbo.ShopDigiLinks l JOIN dbo.ShopOrderItems i ON i.Id=l.ItemId
                LEFT JOIN dbo.ShopDigital d ON d.StoreId=l.StoreId AND d.ProductId=l.ProductId WHERE l.StoreId=@s AND l.OrderId=@o ORDER BY l.Revoked, l.Id", "@s", storeId, "@o", orderId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new L { Id = Convert.ToInt64(r["Id"]), ItemId = Convert.ToInt64(r["ItemId"]), ProductId = (int)r["ProductId"], N = (int)r["N"], MaxN = (int)r["MaxN"], Qty = (int)r["Qty"], Codes = (int)r["Codes"], Name = (string)r["Name"], Kind = (string)r["Kind"], Token = Secrets.Unprotect((string)r["TokenBox"]), Revoked = (bool)r["Revoked"],
                    Until = DateTime.SpecifyKind((DateTime)r["ExpiresAt"], DateTimeKind.Utc), At = DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc), SentAt = r["SentAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["SentAt"], DateTimeKind.Utc) : (DateTime?)null, LastAt = r["LastAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["LastAt"], DateTimeKind.Utc) : (DateTime?)null });
            return l;
        }
        // for the customer who holds the order's token; null = nothing digital in the order
        public static object CustomerLinks(SqlConnection c, ShopFrontHandler.Ctx x, ShopOrder o)
        {
            var rows = Rows(c, x.Row.StoreId); var items = o.Items.Where(i => rows.ContainsKey(i.ProductId)).ToList(); if (items.Count == 0) return null;
            var links = Links(c, x.Row.StoreId, o.Id).Where(z => !z.Revoked).ToList(); var now = DateTime.UtcNow;
            return D("paid", o.PayState == "paid", "closed", o.Status == "cancelled" || o.Status == "returned", "l", items.Select(i =>
            {
                var z = links.FirstOrDefault(y => y.ItemId == i.Id); var kind = rows[i.ProductId].Kind;
                return D("n", i.Name, "p", i.ProductId, "k", kind, "url", z == null ? "" : x.Row.Url + "/_dl/" + z.Token, "left", z == null ? 0 : Math.Max(0, z.MaxN - z.N), "until", z == null ? 0 : ShopView.Unix(z.Until), "open", z != null && z.N < z.MaxN && z.Until > now, "owed", kind == "codes" && z != null ? Math.Max(0, i.Qty - z.Codes) : 0);
            }).ToArray());
        }
        // for the owner's order page
        public static object OwnerLinks(SqlConnection c, int storeId, ShopOrder o)
        {
            var rows = Rows(c, storeId); if (!o.Items.Any(i => rows.ContainsKey(i.ProductId))) return null; var links = Links(c, storeId, o.Id);
            var log = new Dictionary<long, int>(); using (var cmd = Db.Cmd(c, "SELECT LinkId, COUNT(*) FROM dbo.ShopDigiLog WHERE StoreId=@s AND Ok=0 AND LinkId IN (SELECT Id FROM dbo.ShopDigiLinks WHERE OrderId=@o) GROUP BY LinkId", "@s", storeId, "@o", o.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) log[Convert.ToInt64(r[0])] = (int)r[1];
            return D("paid", o.PayState == "paid" && o.Paid >= o.Total - 0.5m, "items", o.Items.Where(i => rows.ContainsKey(i.ProductId)).Select(i => D("i", i.Id, "n", i.Name, "k", rows[i.ProductId].Kind, "q", i.Qty)).ToArray(),
                "links", links.Select(z => { int bad; log.TryGetValue(z.Id, out bad); return D("id", z.Id, "item", z.ItemId, "n", z.Name, "k", z.Kind, "count", z.N, "max", z.MaxN, "until", z.Until.ToString("o"), "sent", z.SentAt.HasValue ? z.SentAt.Value.ToString("o") : "", "last", z.LastAt.HasValue ? z.LastAt.Value.ToString("o") : "", "off", z.Revoked, "codes", z.Codes, "q", z.Qty, "refused", bad); }).ToArray());
        }

        // ---------- the private link: GET /_dl/<64 hex> (a page), ?go=1 (the download itself, counted) ----------
        public static void Serve(SqlConnection c, ShopFrontHandler.Ctx x, string tok, HttpRequest req, HttpResponse res)
        {
            res.Cache.SetCacheability(HttpCacheability.NoCache); res.Cache.SetNoStore(); res.AddHeader("X-Robots-Tag", "noindex");
            bool en = x.Lang == "en", go = Regex.IsMatch(req.Headers["X-Shop-Query"] ?? "", "[?&]go=1(&|$)"), head = req.HttpMethod == "HEAD";
            if (!Web.Allow("dl:" + x.Ip, 120, TimeSpan.FromMinutes(10))) { Page(res, 429, en, en ? "Too many tries" : "محاولات كتير", en ? "Please wait a few minutes." : "استنى كام دقيقة وجرّب تاني.", null); return; }
            long id = 0; int max = 0, n = 0; long orderId = 0, itemId = 0; int pid = 0; bool off = false; DateTime until = DateTime.MinValue;
            if (Regex.IsMatch(tok ?? "", "^[a-f0-9]{64}$"))
                using (var cmd = Db.Cmd(c, "SELECT Id, OrderId, ItemId, ProductId, ExpiresAt, MaxN, N, Revoked FROM dbo.ShopDigiLinks WHERE Hash=@h AND StoreId=@s", "@h", Sha(tok), "@s", x.Row.StoreId)) using (var r = cmd.ExecuteReader())
                    if (r.Read()) { id = Convert.ToInt64(r[0]); orderId = Convert.ToInt64(r[1]); itemId = Convert.ToInt64(r[2]); pid = (int)r[3]; until = DateTime.SpecifyKind((DateTime)r[4], DateTimeKind.Utc); max = (int)r[5]; n = (int)r[6]; off = (bool)r[7]; }
            // a guessed address: the same answer as a wrong one, and only a few tries from one place
            if (id == 0) { res.StatusCode = Web.Allow("dl-bad:" + x.Ip, 20, TimeSpan.FromMinutes(10)) ? 404 : 429; Page(res, res.StatusCode, en, en ? "This link doesn't work" : "اللينك ده مش شغال", en ? "Check it, or ask the store for a new one." : "اتأكد منه، أو اطلب من المتجر لينك جديد.", null); return; }
            var o = ShopOrders.ById(c, x.Row.StoreId, orderId); var item = o == null ? null : o.Items.FirstOrDefault(i => i.Id == itemId); DigiRow d; Rows(c, x.Row.StoreId).TryGetValue(pid, out d);
            string name = item == null ? "" : item.Name, why = off || o == null || o.Status == "cancelled" || o.Status == "returned" ? "off" : until <= DateTime.UtcNow ? "late" : n >= max ? "max" : d == null ? "gone" : null;
            Func<string, string> say = w => w == "off" ? (en ? "This link was closed. Ask the store for a new one." : "اللينك ده اتقفل. اطلب من المتجر لينك جديد.") : w == "late" ? (en ? "This link has expired. Ask the store for a new one." : "وقت اللينك ده خلص. اطلب من المتجر لينك جديد.") : w == "max" ? (en ? "This link was used the most times allowed. Ask the store for a new one." : "اللينك ده اتستخدم أقصى عدد مرات. اطلب من المتجر لينك جديد.") : (en ? "This product isn't here any more. Message the store." : "المنتج ده مش موجود دلوقتي. كلّم المتجر.");
            if (why != null) { if (go && !head) Exec(c, "INSERT INTO dbo.ShopDigiLog(StoreId, LinkId, Ok, Why, Ip) VALUES(@s, @l, 0, @w, @ip)", "@s", x.Row.StoreId, "@l", id, "@w", why, "@ip", x.Ip); Page(res, 410, en, name, say(why), null); return; }
            if (!go || head)
            {
                string what = d.Kind == "file" ? (en ? "File" : "ملف") + ": " + d.FileName + " · " + Size(d.FileBytes, en) : d.Kind == "link" ? (en ? "A link to the content" : "لينك المحتوى") : (en ? "Your code" : "الكود بتاعك");
                Page(res, 200, en, name, what + "<br>" + (en ? "Left: " + (max - n) + " of " + max + " · until " + until.AddHours(2).ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "باقي " + (max - n) + " من " + max + " مرات · لحد " + until.AddHours(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "?go=1", d.Kind == "file" ? (en ? "Download" : "نزّل") : d.Kind == "link" ? (en ? "Open" : "افتح") : (en ? "Show the code" : "اظهر الكود"), true);
                return;
            }
            // counted before it's given: two presses at once can't pass the limit
            if (Num(c, "UPDATE dbo.ShopDigiLinks SET N=N+1, LastAt=SYSUTCDATETIME() WHERE Id=@i AND N<MaxN AND Revoked=0 AND ExpiresAt>SYSUTCDATETIME(); SELECT @@ROWCOUNT", "@i", id) == 0) { Exec(c, "INSERT INTO dbo.ShopDigiLog(StoreId, LinkId, Ok, Why, Ip) VALUES(@s, @l, 0, 'max', @ip)", "@s", x.Row.StoreId, "@l", id, "@ip", x.Ip); Page(res, 410, en, name, say("max"), null); return; }
            Exec(c, "INSERT INTO dbo.ShopDigiLog(StoreId, LinkId, Ok, Ip) VALUES(@s, @l, 1, @ip)", "@s", x.Row.StoreId, "@l", id, "@ip", x.Ip);
            if (d.Kind == "link") { res.StatusCode = 302; res.AddHeader("Location", d.Link); return; }
            if (d.Kind == "codes")
            {
                var codes = new List<string>(); using (var cmd = Db.Cmd(c, "SELECT CodeBox FROM dbo.ShopDigiCodes WHERE ItemId=@i ORDER BY Id", "@i", itemId)) using (var r = cmd.ExecuteReader()) while (r.Read()) codes.Add(Secrets.Unprotect((string)r[0]) ?? "");
                Page(res, 200, en, name, codes.Count == 0 ? (en ? "Your code isn't ready yet. The store will send it." : "الكود لسه ماجهزش، والمتجر هيبعتهولك.") : string.Join("", codes.Select(z => "<code>" + HttpUtility.HtmlEncode(z) + "</code>")) + (item != null && codes.Count < item.Qty ? "<br>" + (en ? "The rest is on its way." : "الباقي جاي في الطريق.") : ""), null);
                return;
            }
            var f = PathOf(x.Row.StoreId, d.FileKey); if (!File.Exists(f)) { Page(res, 410, en, name, en ? "The file isn't here. Message the store." : "الملف مش موجود. كلّم المتجر.", null); return; }
            string fn = Regex.Replace(d.FileName ?? "file", @"[\u0000-\u001F""\\/]", "_"), ascii = Regex.Replace(fn, @"[^\x20-\x7E]", "_");
            res.ContentType = d.FileType ?? "application/octet-stream"; res.AddHeader("Content-Disposition", "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + Uri.EscapeDataString(fn)); res.AddHeader("Content-Length", new FileInfo(f).Length.ToString(CultureInfo.InvariantCulture));
            res.AddHeader("Content-Security-Policy", "default-src 'none'; sandbox");
            res.TransmitFile(f);
        }
        static string Size(long b, bool en) { return b >= 1048576 ? (b / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + (en ? " MB" : " ميجا") : Math.Max(1, b / 1024) + (en ? " KB" : " كيلو"); }
        // a small page in the store's language; html = already safe
        static void Page(HttpResponse res, int status, bool en, string title, string html, string href, string btn = null, bool note = false)
        {
            res.StatusCode = status; res.ContentType = "text/html; charset=utf-8"; res.AddHeader("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");
            string t = HttpUtility.HtmlEncode(string.IsNullOrEmpty(title) ? (en ? "Your download" : "التحميل بتاعك") : title);
            res.Write("<!doctype html><html lang=\"" + (en ? "en" : "ar") + "\" dir=\"" + (en ? "ltr" : "rtl") + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\"><meta name=\"referrer\" content=\"no-referrer\"><title>" + t + "</title><style>*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f3f5f4;color:#17211d;font:17px/1.8 system-ui,'Segoe UI',Tahoma,sans-serif;padding:16px}main{text-align:center;max-width:460px;width:100%;background:#fff;border-radius:18px;padding:26px 20px;box-shadow:0 2px 14px #0001}h1{font-size:22px;margin:0 0 8px;overflow-wrap:anywhere}p{margin:0 0 14px;color:#4b5852}a.b{display:inline-flex;align-items:center;justify-content:center;min-height:48px;padding:0 28px;border-radius:12px;background:#17734f;color:#fff;text-decoration:none;font-weight:700}code{display:block;direction:ltr;font:600 20px/1.5 ui-monospace,Consolas,monospace;background:#eef3f0;border:1px dashed #8aa597;border-radius:10px;padding:10px;margin:8px 0;user-select:all;overflow-wrap:anywhere}small{display:block;margin-top:14px;color:#5b6862;font-size:14px}@media(prefers-color-scheme:dark){body{background:#121815;color:#e7eeea}main{background:#1b231f;box-shadow:none}p,small{color:#a9b8b1}code{background:#24302a;border-color:#4f6b5d}a.b{background:#2f9a6d;color:#06130d}}</style></head><body><main><h1>" + t + "</h1><p>" + html + "</p>"
                + (href == null ? "" : "<a class=\"b\" href=\"" + HttpUtility.HtmlAttributeEncode(href) + "\" rel=\"nofollow\">" + HttpUtility.HtmlEncode(btn) + "</a>") + (note ? "<small>" + (en ? "Digital products can't be returned once downloaded. This link is yours only: don't share it." : "المنتجات الرقمية مابترجعش بعد ما تنزّلها. اللينك ليك إنت بس، ماتبعتهوش لحد.") + "</small>" : "") + "</main></body></html>");
        }

        // ================= the owner =================
        public static long UsedBytes(SqlConnection c, int storeId) { using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(FileBytes),0) FROM dbo.ShopDigital WHERE StoreId=@s AND Kind='file'", "@s", storeId)) return Convert.ToInt64(cmd.ExecuteScalar()); }
        static Dictionary<string, object> Info(SqlConnection c, Shop sh, int pid)
        {
            DigiRow d; var rows = Read(c, sh.StoreId); All[sh.StoreId] = rows; rows.TryGetValue(pid, out d); int free = 0, used = 0;
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CASE WHEN OrderId IS NULL THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN OrderId IS NULL THEN 0 ELSE 1 END),0) FROM dbo.ShopDigiCodes WHERE StoreId=@s AND ProductId=@p", "@s", sh.StoreId, "@p", pid)) using (var r = cmd.ExecuteReader()) { r.Read(); free = (int)r[0]; used = (int)r[1]; }
            var k = ShopCard.Of(sh.StoreId);
            return D("ok", true, "on", k.DigiOn, "kind", d == null ? "" : d.Kind, "file", d == null || d.FileKey == null ? null : D("name", d.FileName, "size", d.FileBytes, "type", d.FileType), "link", d == null ? "" : d.Link ?? "", "multi", d != null && d.Multi, "free", free, "used", used,
                "days", k.DigiDays, "max", k.DigiMax, "pay", ShopCard.Has(sh, null) || ShopPrice.PayWays(sh).Count > 0, "chunk", Chunk, "maxFile", MaxFile);
        }
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (a != "digi") return false; Web.Json(res, 200, Info(c, sh, Product(c, sh, ShopJson.Int(req.QueryString["id"])).Id)); return true;
        }
        static ShopProd Product(SqlConnection c, Shop sh, int id) { ShopProd p; if (!ShopCache.Get(c, sh).ById.TryGetValue(id, out p)) throw new ShopException("not_found", "المنتج ده مش موجود. احفظه الأول."); return p; }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null; if (!a.StartsWith("digi-", StringComparison.Ordinal)) return false; int sid = sh.StoreId;
            switch (a)
            {
                case "digi-save": { var p = Product(c, sh, ShopJson.Int(ShopJson.At(b, "id"))); Save(c, sh, p, b); Web.Json(res, 200, Info(c, sh, p.Id)); return true; }
                case "digi-up": { var p = Product(c, sh, ShopJson.Int(ShopJson.At(b, "id"))); var r = Up(c, sh, p, b); if (r != null) { Web.Json(res, 200, Info(c, sh, p.Id)); } else Web.Json(res, 200, D("ok", true, "next", ShopJson.Int(ShopJson.At(b, "i")) + 1)); return true; }
                case "digi-codes-del": { var p = Product(c, sh, ShopJson.Int(ShopJson.At(b, "id"))); Exec(c, "DELETE FROM dbo.ShopDigiCodes WHERE StoreId=@s AND ProductId=@p AND OrderId IS NULL", "@s", sid, "@p", p.Id); Sync(c, sid, p.Id); Web.Json(res, 200, Info(c, sh, p.Id)); return true; }
                case "digi-send":
                    {
                        // «ابعت تاني»: the links (made now when they weren't), again on WhatsApp. force = give it although it isn't paid in full
                        long oid = ShopJson.Int(ShopJson.At(b, "id")); var o = ShopOrders.ById(c, sid, oid); if (o == null) throw new ShopException("gone", "الأوردر ده مش موجود.");
                        if (o.Status == "cancelled" || o.Status == "returned") throw new ShopException("closed", "الأوردر ده " + ShopOrders.StatusText(o.Status) + ".");
                        bool force = ShopJson.Bool(ShopJson.At(b, "force")); if (!force && !(o.PayState == "paid" && o.Paid >= o.Total - 0.5m)) throw new ShopException("unpaid", "الأوردر لسه مااتدفعش كله. لو عايز تبعت اللينك برضه، اختار «ابعته برضه».");
                        if (!Web.Allow("digi-send:" + oid, 6, TimeSpan.FromHours(1))) throw new ShopException("too_many", "بعته كتير. استنى شوية.");
                        Deliver(c, sid, oid, true); Queue(c, sid, oid); ShopOrders.Log_(c, null, sid, oid, "owner", "طلب يتبعت لينك التحميل للزبون" + (force ? " (قبل الدفع كله)" : ""));
                        Web.Json(res, 200, ShopCard.OrderCard(c, sid, oid)); return true;
                    }
                case "digi-new":
                    {
                        // a new link for a line: fresh time and count; the old one stops (the codes stay the customer's)
                        long lid = ShopJson.Int(ShopJson.At(b, "lid")); long oid = 0, item = 0; int pid = 0; using (var cmd = Db.Cmd(c, "SELECT OrderId, ItemId, ProductId FROM dbo.ShopDigiLinks WHERE Id=@i AND StoreId=@s", "@i", lid, "@s", sid)) using (var r = cmd.ExecuteReader()) if (r.Read()) { oid = Convert.ToInt64(r[0]); item = Convert.ToInt64(r[1]); pid = (int)r[2]; }
                        var o = oid == 0 ? null : ShopOrders.ById(c, sid, oid); if (o == null) throw new ShopException("gone", "اللينك ده مش موجود.");
                        if (o.Status == "cancelled" || o.Status == "returned") throw new ShopException("closed", "الأوردر ده " + ShopOrders.StatusText(o.Status) + ".");
                        if (!Web.Allow("digi-new:" + sid, 60, TimeSpan.FromHours(1))) throw new ShopException("too_many", "لينكات كتير. استنى شوية.");
                        var k = ShopCard.Of(sid); string tok = Shops.Token(32);
                        using (var tx = c.BeginTransaction())
                        {
                            try
                            {
                                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopDigiLinks SET Revoked=1 WHERE ItemId=@i AND StoreId=@s AND Revoked=0", "@i", item, "@s", sid)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopDigiLinks(StoreId, OrderId, ItemId, ProductId, Hash, TokenBox, ExpiresAt, MaxN) VALUES(@s, @o, @i, @p, @h, @t, DATEADD(DAY, @d, SYSUTCDATETIME()), @m)", "@s", sid, "@o", oid, "@i", item, "@p", pid, "@h", Sha(tok), "@t", Secrets.Protect(tok), "@d", k.DigiDays, "@m", k.DigiMax)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                                tx.Commit();
                            }
                            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { try { tx.Rollback(); } catch { } throw new ShopException("busy", "اتعمل لينك جديد حالًا. حدّث الصفحة."); }
                            catch { try { tx.Rollback(); } catch { } throw; }
                        }
                        ShopOrders.Log_(c, null, sid, oid, "owner", "لينك تحميل جديد (القديم اتقفل)");
                        if (ShopJson.Bool(ShopJson.At(b, "send"), true)) Queue(c, sid, oid);
                        Web.Json(res, 200, ShopCard.OrderCard(c, sid, oid)); return true;
                    }
            }
            return false;
        }
        // file / link / codes ("" = an ordinary product again); codes = new ones to add, one a line
        static void Save(SqlConnection c, Shop sh, ShopProd p, Dictionary<string, object> b)
        {
            int sid = sh.StoreId; string kind = ShopJson.Str(ShopJson.At(b, "kind"), 6); var k = ShopCard.Of(sid); var rows = Read(c, sid); DigiRow old; rows.TryGetValue(p.Id, out old);
            if (kind == "") { if (old != null) { Exec(c, "DELETE FROM dbo.ShopDigital WHERE StoreId=@s AND ProductId=@p", "@s", sid, "@p", p.Id); Drop(sid, old.FileKey); Exec(c, "UPDATE dbo.ShopProducts SET Track=1, UpdatedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Id=@p", "@s", sid, "@p", p.Id); Shops.Bump(c, sid); } return; }
            if (kind != "file" && kind != "link" && kind != "codes") throw new ShopException("kind", "اختار نوع المنتج الرقمي.");
            if (!k.DigiOn) throw new ShopException("off", "فعّل «المنتجات الرقمية» الأول من «الدفع والفروع».");
            if (kind == "codes" && p.Options.Count > 0) throw new ShopException("opts", "المنتج اللي بأكواد مايبقاش ليه مقاسات أو ألوان. شيلها الأول.");
            string link = ShopJson.Str(ShopJson.At(b, "link"), 600); Uri u;
            if (kind == "link" && !(Uri.TryCreate(link, UriKind.Absolute, out u) && (u.Scheme == "https" || u.Scheme == "http") && link.Length > 10 && !link.Any(char.IsWhiteSpace))) throw new ShopException("link", "اكتب لينك كامل يبدأ بـ https://");
            bool multi = ShopJson.Bool(ShopJson.At(b, "multi"));
            Exec(c, @"MERGE dbo.ShopDigital AS t USING (SELECT @s AS StoreId, @p AS ProductId) AS x ON t.StoreId=x.StoreId AND t.ProductId=x.ProductId
                WHEN MATCHED THEN UPDATE SET Kind=@k, Link=@l, Multi=@m, FileKey=CASE WHEN @k='file' THEN FileKey ELSE NULL END, FileName=CASE WHEN @k='file' THEN FileName ELSE NULL END, FileBytes=CASE WHEN @k='file' THEN FileBytes ELSE 0 END, At=SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (StoreId, ProductId, Kind, Link, Multi) VALUES (@s, @p, @k, @l, @m);", "@s", sid, "@p", p.Id, "@k", kind, "@l", kind == "link" ? link : null, "@m", multi);
            if (old != null && old.Kind == "file" && kind != "file") Drop(sid, old.FileKey);
            // the product as it sells: a file or a link never runs out; codes are counted
            Exec(c, "UPDATE dbo.ShopProducts SET Track=@t, OutMode=CASE WHEN @t=1 THEN 'show' ELSE OutMode END, MaxQty=CASE WHEN @m=1 AND MaxQty=1 THEN NULL ELSE MaxQty END, UpdatedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Id=@p", "@t", kind == "codes", "@m", multi, "@s", sid, "@p", p.Id);
            int added = 0;
            if (kind == "codes")
            {
                var lines = (ShopJson.Str(ShopJson.At(b, "codes"), 400000) ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                if (lines.Any(x => x.Length > 200)) throw new ShopException("codes", "فيه كود أطول من 200 حرف.");
                if (Num(c, "SELECT COUNT(*) FROM dbo.ShopDigiCodes WHERE StoreId=@s AND ProductId=@p", "@s", sid, "@p", p.Id) + lines.Count > MaxCodes) throw new ShopException("codes", "أقصى عدد " + MaxCodes + " كود للمنتج.");
                foreach (var x in lines) try { Exec(c, "INSERT INTO dbo.ShopDigiCodes(StoreId, ProductId, CodeBox, Hash) VALUES(@s, @p, @b, @h)", "@s", sid, "@p", p.Id, "@b", Secrets.Protect(x), "@h", Sha(sid + ":" + p.Id + ":" + x)); added++; } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }   // the same code twice is kept once
            }
            Shops.Bump(c, sid);
            if (kind == "codes")
            {
                Sync(c, sid, p.Id);
                // orders paid while the codes had run out get theirs now
                if (added > 0) { var owed = new List<long>(); using (var cmd = Db.Cmd(c, "SELECT DISTINCT i.OrderId FROM dbo.ShopOrderItems i JOIN dbo.ShopOrders o ON o.Id=i.OrderId WHERE i.StoreId=@s AND i.ProductId=@p AND o.PayState='paid' AND o.Status NOT IN ('cancelled','returned') AND i.Qty>(SELECT COUNT(*) FROM dbo.ShopDigiCodes x WHERE x.ItemId=i.Id)", "@s", sid, "@p", p.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) owed.Add(Convert.ToInt64(r[0])); foreach (var oid in owed) { Deliver(c, sid, oid, false); Queue(c, sid, oid); } }
            }
        }
        static void Drop(int storeId, string key) { if (string.IsNullOrEmpty(key)) return; try { var f = PathOf(storeId, key); if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { Log.Error(ex); } }
        // a part of the file (base64, Chunk bytes each, in order); the last one checks the whole file by its bytes. null = more to come
        static object Up(SqlConnection c, Shop sh, ShopProd p, Dictionary<string, object> b)
        {
            int sid = sh.StoreId, i = ShopJson.Int(ShopJson.At(b, "i")), n = ShopJson.Int(ShopJson.At(b, "n")); long size; long.TryParse(ShopJson.Str(ShopJson.At(b, "size"), 12), out size); string up = ShopJson.Str(ShopJson.At(b, "up"), 40), name = Regex.Replace(ShopJson.Str(ShopJson.At(b, "name"), 160), @"[\u0000-\u001F""\\/:*?<>|]", "_").Trim();
            if (!ShopCard.Of(sid).DigiOn) throw new ShopException("off", "فعّل «المنتجات الرقمية» الأول من «الدفع والفروع».");
            if (!Regex.IsMatch(up, "^[a-z0-9]{16,40}$") || n < 1 || n > (int)(MaxFile / Chunk) + 1 || i < 0 || i >= n) throw new ShopException("up", "الرفع اتلخبط. ارفع الملف تاني.");
            if (size <= 0 || size > MaxFile) throw new ShopException("size", "الملف لازم يكون لحد 50 ميجا.");
            if ((long)(n - 1) * Chunk >= size || (long)n * Chunk < size) throw new ShopException("up", "الرفع اتلخبط. ارفع الملف تاني.");
            if (!Web.Allow("digi-up:" + sid, 400, TimeSpan.FromHours(1))) throw new ShopException("too_many", "رفعت كتير في ساعة. استنى شوية.");
            byte[] data; try { data = Convert.FromBase64String(ShopJson.Str(ShopJson.At(b, "data"), Chunk * 2)); } catch (FormatException) { throw new ShopException("up", "الرفع اتلخبط. ارفع الملف تاني."); }
            if (data.Length != (i < n - 1 ? Chunk : size - (long)(n - 1) * Chunk)) throw new ShopException("up", "الرفع اتلخبط. ارفع الملف تاني.");
            var dir = Path.Combine(Dir, sid.ToString(CultureInfo.InvariantCulture)); Directory.CreateDirectory(dir); var tmp = Path.Combine(dir, up + ".part");
            if (i == 0)
            {
                foreach (var f in Directory.GetFiles(dir, "*.part")) try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6) || f == tmp) File.Delete(f); } catch { }
                var oldRow = Read(c, sid); DigiRow mine; oldRow.TryGetValue(p.Id, out mine);
                if (UsedBytes(c, sid) - (mine == null ? 0 : mine.FileBytes) + size > MaxStoreBytes) throw new ShopException("full", "مساحة ملفات متجرك (" + MaxStoreBytes / 1048576 + " ميجا) مش هتكفي الملف ده. امسح ملف مش محتاجه.");
            }
            long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0; if (have != (long)i * Chunk) throw new ShopException("up", "الرفع اتلخبط. ارفع الملف تاني.");
            using (var fs = new FileStream(tmp, FileMode.Append, FileAccess.Write, FileShare.None)) fs.Write(data, 0, data.Length);
            if (i < n - 1) return null;
            if (new FileInfo(tmp).Length != size) { File.Delete(tmp); throw new ShopException("up", "الرفع اتلخبط. ارفع الملف تاني."); }
            byte[] head = new byte[64]; using (var fs = File.OpenRead(tmp)) fs.Read(head, 0, head.Length);
            var kind = Sniff(head); if (kind == null) { File.Delete(tmp); throw new ShopException("type", "نوع الملف ده مش مسموح. المسموح: PDF و ZIP و EPUB و MP3 و MP4 والصور (JPG و PNG و WEBP و GIF)."); }
            if (name.Length == 0) name = "file"; if (!name.EndsWith(kind[1], StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name) + kind[1];
            string key = Shops.Token(16) + ".bin"; File.Move(tmp, PathOf(sid, key));
            var rows = Read(c, sid); DigiRow old; rows.TryGetValue(p.Id, out old);
            Exec(c, @"MERGE dbo.ShopDigital AS t USING (SELECT @s AS StoreId, @p AS ProductId) AS x ON t.StoreId=x.StoreId AND t.ProductId=x.ProductId
                WHEN MATCHED THEN UPDATE SET Kind='file', FileKey=@f, FileName=@n, FileType=@t, FileBytes=@b, Link=NULL, At=SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (StoreId, ProductId, Kind, FileKey, FileName, FileType, FileBytes) VALUES (@s, @p, 'file', @f, @n, @t, @b);", "@s", sid, "@p", p.Id, "@f", key, "@n", name, "@t", kind[0], "@b", size);
            Exec(c, "UPDATE dbo.ShopProducts SET Track=0, UpdatedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Id=@p", "@s", sid, "@p", p.Id);
            if (old != null && old.FileKey != null && old.FileKey != key) Drop(sid, old.FileKey);
            Shops.Bump(c, sid); return true;
        }
        // what the file is by its first bytes: [type, extension]; null = not allowed
        public static string[] Sniff(byte[] h)
        {
            Func<int, string, bool> at = (o, s) => h.Length >= o + s.Length && Enumerable.Range(0, s.Length).All(j => h[o + j] == s[j]);
            if (at(0, "%PDF-")) return new[] { "application/pdf", ".pdf" };
            if (h.Length > 4 && h[0] == 'P' && h[1] == 'K' && h[2] == 3 && h[3] == 4) return at(30, "mimetypeapplication/epub+zip") ? new[] { "application/epub+zip", ".epub" } : new[] { "application/zip", ".zip" };
            if (at(0, "ID3") || (h.Length > 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0 && (h[1] & 0x06) != 0)) return new[] { "audio/mpeg", ".mp3" };
            if (at(4, "ftyp")) return new[] { "video/mp4", ".mp4" };
            if (h.Length > 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return new[] { "image/jpeg", ".jpg" };
            if (h.Length > 8 && h[0] == 0x89 && at(1, "PNG\r\n")) return new[] { "image/png", ".png" };
            if (at(0, "RIFF") && at(8, "WEBP")) return new[] { "image/webp", ".webp" };
            if (at(0, "GIF87a") || at(0, "GIF89a")) return new[] { "image/gif", ".gif" };
            return null;
        }
    }
}
