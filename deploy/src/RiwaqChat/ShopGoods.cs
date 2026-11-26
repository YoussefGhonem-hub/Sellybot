// Four catalog features of SellyStore, each with its own switch in «متجري» (off for every store until the owner turns it on):
//   ShopMove.cs    «نقل منتجات من متجر قديم»: Shopify / WooCommerce CSV, EasyOrders' API; a preview, then the import in the background
//   ShopWaAdd.cs   «إضافة منتج من واتساب»: the owner sends a picture with words to the store's number, a hidden draft is made
//   ShopPre.cs     «حجز مسبق»: a product bought at stock 0, with the day it is expected
//   ShopPlaces.cs  «أكتر من مخزن»: stock per branch / warehouse; ShopVariants.Stock stays the total
// This file: their tables, the switches (their own row, so a settings save elsewhere never drops them), the owner's actions
// (api/shop.ashx a=cat-...), the background lane, and the hooks the rest of the store calls (stock moves, the cart's sum, the catalog).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public class GoodsSet { public bool Wa, Pre, Places; public List<string> Phones = new List<string>(); public DateTime At = DateTime.UtcNow; }

    public static class ShopGoods
    {
        // ---------- the switches ----------
        static readonly ConcurrentDictionary<int, GoodsSet> Sets = new ConcurrentDictionary<int, GoodsSet>();
        public static GoodsSet Set(SqlConnection c, int storeId, SqlTransaction tx = null)
        {
            GoodsSet x; if (tx == null && Sets.TryGetValue(storeId, out x) && DateTime.UtcNow - x.At < TimeSpan.FromSeconds(30)) return x;
            x = new GoodsSet();
            if (!Shops.Ready) return x;
            using (var cmd = Db.Cmd(c, "SELECT WaOn, PreOn, PlacesOn, Phones FROM dbo.ShopCatSet WHERE StoreId=@s", "@s", storeId))
            {
                cmd.Transaction = tx;
                using (var r = cmd.ExecuteReader()) if (r.Read()) { x.Wa = (bool)r[0]; x.Pre = (bool)r[1]; x.Places = (bool)r[2]; x.Phones = ((r[3] as string) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(); }
            }
            Sets[storeId] = x; return x;
        }
        static void SaveSet(SqlConnection c, int storeId, GoodsSet x)
        {
            using (var cmd = Db.Cmd(c, "MERGE dbo.ShopCatSet AS t USING (SELECT @s AS StoreId) AS k ON t.StoreId=k.StoreId WHEN MATCHED THEN UPDATE SET WaOn=@w, PreOn=@p, PlacesOn=@l, Phones=@ph, At=SYSUTCDATETIME() WHEN NOT MATCHED THEN INSERT (StoreId, WaOn, PreOn, PlacesOn, Phones) VALUES (@s, @w, @p, @l, @ph);",
                "@s", storeId, "@w", x.Wa, "@p", x.Pre, "@l", x.Places, "@ph", string.Join(",", x.Phones))) cmd.ExecuteNonQuery();
            Sets.TryRemove(storeId, out x);
        }

        // what «متجري» shows of these four
        public static Dictionary<string, object> StateJson(SqlConnection c, StoreInfo s, Shop sh)
        {
            if (!Shops.Ready) return null;
            var x = Set(c, s.Id); if (x.Places) ShopPlaces.Sync(c, sh);
            return new Dictionary<string, object> { { "wa", x.Wa }, { "pre", x.Pre }, { "places", x.Places }, { "phones", x.Phones.Select(ShopOrders.Local).ToArray() }, { "owner", ShopOrders.Local(s.OwnerPhone) ?? "" },
                { "waLinked", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber) }, { "waService", WaAccount.HasService(s) }, { "nPlaces", x.Places ? ShopPlaces.Count(c, sh.StoreId) : 0 }, { "eo", EasyOrders.Connected(s) } };
        }

        // ---------- the owner's actions (api/shop.ashx, after the others) ----------
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, int id, HttpRequest req, HttpResponse res)
        {
            if (!a.StartsWith("cat-", StringComparison.Ordinal) || !Shops.Ready) return false;
            if (a == "cat-places" || a == "cat-moves") Need(c, sh, "places");
            switch (a)
            {
                case "cat-places": Web.Json(res, 200, ShopPlaces.Table(c, sh, req)); return true;
                case "cat-moves": Web.Json(res, 200, ShopPlaces.Moves(c, sh)); return true;
                case "cat-prod": Web.Json(res, 200, Prod(c, sh, id)); return true;
                case "cat-pre": Web.Json(res, 200, ShopPre.List(c, sh)); return true;
                case "cat-imp": Web.Json(res, 200, ShopMove.Status(c, sh, id)); return true;
            }
            return false;
        }

        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null; if (!a.StartsWith("cat-", StringComparison.Ordinal) || !Shops.Ready) return false;
            int id = ShopJson.Int(ShopJson.At(b, "id"));
            if (a.StartsWith("cat-place", StringComparison.Ordinal) || a == "cat-stock" || a == "cat-move") Need(c, sh, "places");
            if (a.StartsWith("cat-pre", StringComparison.Ordinal)) Need(c, sh, "pre");
            switch (a)
            {
                case "cat-set": Switch(c, s, sh, b); Ok(res, "cat", StateJson(c, s, sh)); return true;
                case "cat-place-save": Ok(res, "id", ShopPlaces.Save(c, sh, b), "places", ShopPlaces.List(c, sh.StoreId)); return true;
                case "cat-place-del": ShopPlaces.Delete(c, sh, id); Ok(res, "places", ShopPlaces.List(c, sh.StoreId)); return true;
                case "cat-place-sort": ShopPlaces.Sort(c, sh, b); Ok(res, "places", ShopPlaces.List(c, sh.StoreId)); return true;
                case "cat-stock": ShopPlaces.SetMany(c, sh, b); Ok(res, "prod", Prod(c, sh, ShopJson.Int(ShopJson.At(b, "product")))); return true;
                case "cat-move": ShopPlaces.Move(c, sh, b); Ok(res, "prod", Prod(c, sh, ShopJson.Int(ShopJson.At(b, "product")))); return true;
                case "cat-pre-save": ShopPre.Save(c, s, sh, b); Ok(res, "prod", Prod(c, sh, ShopJson.Int(ShopJson.At(b, "product")))); return true;
                case "cat-pre-in": ShopPre.Arrived(c, s, sh, b); Ok(res, "prod", Prod(c, sh, ShopJson.Int(ShopJson.At(b, "product")))); return true;
                case "cat-imp-eo": Ok(res, "id", ShopMove.StartEo(c, s, sh, b)); return true;
                case "cat-imp-go": ShopMove.Go(c, s, sh, b); Ok(res, "imp", ShopMove.Status(c, sh, id)); return true;
                case "cat-imp-stop": ShopMove.Stop(c, sh, id); Ok(res, "imp", ShopMove.Status(c, sh, id)); return true;
            }
            return false;
        }
        static void Need(SqlConnection c, Shop sh, string what)
        {
            var x = Set(c, sh.StoreId);
            if (what == "places" && !x.Places) throw new ShopException("off", "شغّل «أكتر من مخزن» من «المميزات» الأول.");
            if (what == "pre" && !x.Pre) throw new ShopException("off", "شغّل «حجز مسبق» من «المميزات» الأول.");
        }
        static void Ok(HttpResponse res, params object[] kv) { var d = new Dictionary<string, object> { { "ok", true } }; for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; Web.Json(res, 200, d); }

        static void Switch(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            var x = Set(c, s.Id); var y = new GoodsSet { Wa = x.Wa, Pre = x.Pre, Places = x.Places, Phones = x.Phones };
            if (b.ContainsKey("wa")) y.Wa = ShopJson.Bool(b["wa"]);
            if (b.ContainsKey("pre")) y.Pre = ShopJson.Bool(b["pre"]);
            if (b.ContainsKey("places")) y.Places = ShopJson.Bool(b["places"]);
            if (b.ContainsKey("phones"))
            {
                var l = new List<string>();
                foreach (var p in ShopJson.List(b["phones"]).Take(8))
                {
                    var t = ShopJson.Str(p, 24); if (t.Length == 0) continue; var m = ShopOrders.Mobile(t);
                    if (m == null) throw new ShopException("phone", "الرقم «" + t + "» مش رقم موبايل مصري صح.");
                    if (m == s.WaNumber) throw new ShopException("phone", "ده رقم واتساب المتجر نفسه. اكتب رقمك إنت أو رقم حد معاك.");
                    if (!l.Contains(m) && m != s.OwnerPhone) l.Add(m);
                }
                if (l.Count > 5) throw new ShopException("phone", "أقصى عدد 5 أرقام غير رقمك.");
                y.Phones = l;
            }
            if (y.Wa && !x.Wa && string.IsNullOrEmpty(s.OwnerPhone) && y.Phones.Count == 0) throw new ShopException("owner", "اكتب رقم واتساب ليك الأول (من «الإعدادات») أو رقم مسموح هنا، عشان نعرف إن الرسالة منك.");
            SaveSet(c, s.Id, y);
            if (y.Places && !x.Places) ShopPlaces.Sync(c, sh, true);
            if (y.Pre != x.Pre || y.Places != x.Places) Shops.Bump(c, s.Id);
        }

        // the product editor's card: pre-order, and the stock in each place
        static Dictionary<string, object> Prod(SqlConnection c, Shop sh, int pid)
        {
            var d = ShopCache.Get(c, sh); ShopProd p; if (!d.ById.TryGetValue(pid, out p)) throw new ShopException("not_found", "المنتج ده مش موجود.");
            var x = Set(c, sh.StoreId);
            return new Dictionary<string, object> { { "id", p.Id }, { "pre", x.Pre ? ShopPre.Of(c, sh, p) : null }, { "at", x.Places ? ShopPlaces.Of(c, sh, p) : null },
                { "vars", p.Vars.Select(v => new Dictionary<string, object> { { "id", v.Id }, { "o", v.Label }, { "stock", v.Stock }, { "active", v.Active } }).ToArray() }, { "track", p.Track } };
        }

        // ---------- hooks ----------
        // ShopOrders.Stock, before the movement is written: the part of an order that is a pre-order never leaves the shelf.
        // true = nothing is left to write (all of it was a pre-order). The flags are kept for Moved, on the same thread.
        [ThreadStatic] static int[] flags;
        public static bool Moving(SqlConnection c, SqlTransaction tx, int storeId, int productId, int variantId, ref int delta, ref int after, string reason, long? orderId)
        {
            flags = null; if (!Shops.Ready) return false;
            using (var cmd = Db.Cmd(c, @"SELECT s.PreOn, s.PlacesOn, (SELECT COUNT(*) FROM dbo.ShopPre p WHERE p.StoreId=@s AND p.ProductId=@p AND p.Active=1),
                (SELECT COUNT(*) FROM dbo.ShopPreOrders o WHERE o.StoreId=@s AND o.VariantId=@v AND o.State IN ('wait','gone')), (SELECT COUNT(*) FROM dbo.ShopPlaces l WHERE l.StoreId=@s AND l.Gone=0) FROM dbo.ShopCatSet s WHERE s.StoreId=@s", "@s", storeId, "@p", productId, "@v", variantId))
            {
                cmd.Transaction = tx;
                using (var r = cmd.ExecuteReader()) { if (!r.Read()) return false; flags = new[] { (bool)r[0] ? 1 : 0, (bool)r[1] ? 1 : 0, (int)r[2], (int)r[3], (int)r[4] }; }
            }
            if ((flags[0] == 1 && flags[2] > 0) || flags[3] > 0) ShopPre.Moving(c, tx, storeId, productId, variantId, ref delta, ref after, reason, orderId, flags[0] == 1 && flags[2] > 0);
            return delta == 0;
        }
        // after it is written: the places follow the total, and new pieces go to the pre-orders that wait for them first
        public static void Moved(SqlConnection c, SqlTransaction tx, int storeId, int productId, int variantId, int delta, int after, string reason, long? orderId)
        {
            var f = flags; flags = null; if (f == null) return;
            if (f[1] == 1 && f[4] > 0) ShopPlaces.Moved(c, tx, storeId, variantId, delta, after, reason, orderId);
            if (delta > 0 && reason != "pre" && f[3] > 0) ShopPre.Cover(c, tx, storeId, productId, variantId, f[1] == 1 && f[4] > 0);
        }

        // ShopPrice.Quote, before the lines that can be bought are counted: pre-orders and what a branch has
        public static void Quote(SqlConnection c, ShopData d, ShopQuote q, ShopHow how, bool checkStock)
        {
            if (!Shops.Ready || q.Lines.Count == 0) return;
            var x = Set(c, d.Row.StoreId);
            if (x.Pre) ShopPre.Quote(d, q, checkStock);
            if (x.Places) ShopPlaces.Quote(c, d, q, how, checkStock);
        }
        // ...and after the deposit is worked out: what a pre-order asks to be paid first
        public static void Paid(SqlConnection c, ShopData d, ShopQuote q, ShopHow how) { if (q.Cat != null && how != null) ShopPre.Paid(d, q); }

        // the catalog the store's pages read
        public static void Card(ShopData d, ShopProd p, Dictionary<string, object> r) { ShopPre.Card(d, p, r); }
        public static void Detail(ShopData d, ShopProd p, Dictionary<string, object> r) { ShopPre.Detail(d, p, r); }
        public static void Load(SqlConnection c, ShopData d) { ShopPre.Load(c, d); }
        // what the assistant says about a product
        public static void BotLine(ShopData d, ShopProd p, System.Text.StringBuilder sb)
        {
            var t = ShopPre.BotLine(d, p); if (t.Length == 0) return;
            var s = sb.ToString(); if (s.EndsWith("، باقي 0")) sb.Length -= "، باقي 0".Length;   // nothing on the shelf is what a pre-order is
            sb.Append(t);
        }
        // the owner's order: its pre-order lines, and where each line is taken from
        public static object OrderJson(SqlConnection c, Shop sh, ShopOrder o)
        {
            if (!Shops.Ready) return null; var x = Set(c, sh.StoreId);
            var pre = ShopPre.OfOrder(c, sh.StoreId, o.Id); var at = x.Places && ShopPlaces.Count(c, sh.StoreId) >= 2 ? ShopPlaces.OfOrder(c, sh.StoreId, o.Id) : null;
            return pre.Count == 0 && at == null ? null : new Dictionary<string, object> { { "pre", pre }, { "at", at } };
        }
        // «قايمة التجهيز»: where to take each line from, and what still waits for its pieces
        public static void Prep(SqlConnection c, Shop sh, List<ShopOrder> os, List<Dictionary<string, object>> rows)
        {
            if (!Shops.Ready || os.Count == 0) return; var x = Set(c, sh.StoreId);
            if (x.Places) ShopPlaces.Prep(c, sh.StoreId, os, rows);
            ShopPre.Prep(c, sh.StoreId, os, rows);
        }

        // ---------- the background lane (Jobs.cs): imports that stopped half way, the pre-orders' messages, replies held for the morning ----------
        public static async Task Run(CancellationToken ct)
        {
            if (!Shops.Ready) return;
            try { ShopMove.Resume(); } catch (Exception ex) { Log.Error(ex); }
            try { await ShopPre.SendDue(ct); } catch (Exception ex) { Log.Error(ex); }
            try { await ShopWaAdd.SendDue(ct); } catch (Exception ex) { Log.Error(ex); }
        }

        public const string Schema = @"
-- the owner's switches for the four features, and the numbers allowed to add products on WhatsApp
IF OBJECT_ID('dbo.ShopCatSet') IS NULL
  CREATE TABLE dbo.ShopCatSet(StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE, WaOn BIT NOT NULL DEFAULT 0, PreOn BIT NOT NULL DEFAULT 0, PlacesOn BIT NOT NULL DEFAULT 0,
    Phones NVARCHAR(200) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
-- «نقل منتجات»: fetch (EasyOrders being read) / preview / run / done / failed / stopped; the plan is what was read, cleaned
IF OBJECT_ID('dbo.ShopImports') IS NULL
BEGIN
  CREATE TABLE dbo.ShopImports(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Src NVARCHAR(10) NOT NULL, FileName NVARCHAR(120) NULL,
    State NVARCHAR(8) NOT NULL DEFAULT 'preview', PlanJson NVARCHAR(MAX) NULL, Preview NVARCHAR(MAX) NULL, Opts NVARCHAR(400) NULL,
    Total INT NOT NULL DEFAULT 0, Done INT NOT NULL DEFAULT 0, Pics INT NOT NULL DEFAULT 0, PicsDone INT NOT NULL DEFAULT 0, Added INT NOT NULL DEFAULT 0, Updated INT NOT NULL DEFAULT 0, Skipped INT NOT NULL DEFAULT 0,
    Report NVARCHAR(MAX) NULL, Error NVARCHAR(400) NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), StartedAt DATETIME2 NULL, BeatAt DATETIME2 NULL, EndedAt DATETIME2 NULL);
  CREATE INDEX IX_ShopImports_Store ON dbo.ShopImports(StoreId, Id);
END
-- which product an old store's product became (so a second import updates instead of copying)
IF OBJECT_ID('dbo.ShopImportMap') IS NULL
  CREATE TABLE dbo.ShopImportMap(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Src NVARCHAR(10) NOT NULL, Ref NVARCHAR(150) NOT NULL, ProductId INT NOT NULL, PRIMARY KEY(StoreId, Src, Ref));
-- an import's pictures, fetched a few at a time: wait / ok / bad
IF OBJECT_ID('dbo.ShopImportPics') IS NULL
BEGIN
  CREATE TABLE dbo.ShopImportPics(Id BIGINT IDENTITY(1,1) PRIMARY KEY, ImportId INT NOT NULL, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, Url NVARCHAR(900) NOT NULL,
    Sort INT NOT NULL DEFAULT 0, Alt NVARCHAR(160) NULL, Vars NVARCHAR(MAX) NULL, State NVARCHAR(5) NOT NULL DEFAULT 'wait', Why NVARCHAR(120) NULL, Path NVARCHAR(160) NULL);
  CREATE INDEX IX_ShopImportPics_Imp ON dbo.ShopImportPics(ImportId, State, Id);
END
-- «إضافة منتج من واتساب»: each message from an allowed number (once, by its id), the picture it brought, the draft it made, the reply
IF OBJECT_ID('dbo.ShopWaAdds') IS NULL
BEGIN
  CREATE TABLE dbo.ShopWaAdds(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL, MsgId NVARCHAR(150) NULL,
    Kind NVARCHAR(5) NOT NULL, Text NVARCHAR(1000) NULL, Path NVARCHAR(160) NULL, ProductId INT NULL, Reply NVARCHAR(2000) NULL, RepliedAt DATETIME2 NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopWaAdds_Msg ON dbo.ShopWaAdds(StoreId, MsgId) WHERE MsgId IS NOT NULL;';
  CREATE INDEX IX_ShopWaAdds_Phone ON dbo.ShopWaAdds(StoreId, Phone, Id);
END
-- «حجز مسبق»: a product (VariantId 0 = all its sizes) or one size; Pay: cod / deposit / full
IF OBJECT_ID('dbo.ShopPre') IS NULL
  CREATE TABLE dbo.ShopPre(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, VariantId INT NOT NULL DEFAULT 0, Day DATE NOT NULL, MaxQty INT NULL,
    Pay NVARCHAR(8) NOT NULL DEFAULT 'cod', Active BIT NOT NULL DEFAULT 1, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ArrivedAt DATETIME2 NULL, PRIMARY KEY(StoreId, ProductId, VariantId));
-- the pieces of an order that wait for the goods: wait / in (the pieces came and are kept for it) / gone (cancelled); Told = the day the customer last heard
IF OBJECT_ID('dbo.ShopPreOrders') IS NULL
BEGIN
  CREATE TABLE dbo.ShopPreOrders(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, ProductId INT NOT NULL, VariantId INT NOT NULL,
    Qty INT NOT NULL, State NVARCHAR(5) NOT NULL DEFAULT 'wait', Told DATE NOT NULL, InAt DATETIME2 NULL, InToldAt DATETIME2 NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopPreOrders_Var ON dbo.ShopPreOrders(StoreId, VariantId, State, Id);
  CREATE INDEX IX_ShopPreOrders_Order ON dbo.ShopPreOrders(OrderId);
END
-- «أكتر من مخزن»: a branch of the owner's (Kind b, Ref = the branch's id in the settings) or a warehouse (w); Gone = the branch was removed
IF OBJECT_ID('dbo.ShopPlaces') IS NULL
BEGIN
  CREATE TABLE dbo.ShopPlaces(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Kind CHAR(1) NOT NULL, Ref NVARCHAR(20) NULL, Name NVARCHAR(60) NOT NULL,
    Sort INT NOT NULL DEFAULT 0, Gone BIT NOT NULL DEFAULT 0, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopPlaces_Store ON dbo.ShopPlaces(StoreId, Sort, Id);
END
IF OBJECT_ID('dbo.ShopStockAt') IS NULL
BEGIN
  CREATE TABLE dbo.ShopStockAt(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, VariantId INT NOT NULL, PlaceId INT NOT NULL, Qty INT NOT NULL DEFAULT 0, PRIMARY KEY(VariantId, PlaceId));
  CREATE INDEX IX_ShopStockAt_Store ON dbo.ShopStockAt(StoreId, PlaceId);
END
-- what an order took from each place (a cancel or a return puts it back where it came from)
IF OBJECT_ID('dbo.ShopOrderAt') IS NULL
  CREATE TABLE dbo.ShopOrderAt(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, VariantId INT NOT NULL, PlaceId INT NOT NULL, Qty INT NOT NULL, PRIMARY KEY(OrderId, VariantId, PlaceId));
IF OBJECT_ID('dbo.ShopStockMoves') IS NULL
BEGIN
  CREATE TABLE dbo.ShopStockMoves(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, VariantId INT NOT NULL, FromId INT NOT NULL, ToId INT NOT NULL,
    Qty INT NOT NULL, Note NVARCHAR(120) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopStockMoves_Store ON dbo.ShopStockMoves(StoreId, Id);
END
";
    }
}
