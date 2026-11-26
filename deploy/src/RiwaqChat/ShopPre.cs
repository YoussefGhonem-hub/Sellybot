// «حجز مسبق»: a product (or one of its sizes) the owner is waiting for can be bought at stock 0, with the day it is expected and, if the
// owner wants, the most pieces that can be booked. The shelf never goes below zero for it: what an order takes beyond the shelf is a
// row here (ShopPreOrders, «wait»), and pieces that come in later (the owner adds stock, or presses «وصل») go to those rows first, oldest
// first, so the customers who waited are served before the shelf sells them again. A cancel frees its place. Changing the day tells each
// waiting customer once; the pieces coming in tell them once more. Payment: as on any order, or a deposit, or all of it first (the
// store's own transfer ways). Every number shown is the store's own.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class ShopPre
    {
        public const int Unlimited = 100000;
        public class Cfg { public int ProductId, VariantId; public DateTime Day; public int? Max; public string Pay; }
        class Info { public Dictionary<int, Cfg> Vars = new Dictionary<int, Cfg>(); }
        static readonly ConditionalWeakTable<ShopProd, Info> Infos = new ConditionalWeakTable<ShopProd, Info>();
        static readonly CultureInfo Ar = new CultureInfo("ar-EG"), En = new CultureInfo("en-GB");
        public static string DayText(DateTime d, bool en = false) { return d.ToString("dddd d MMMM", en ? En : Ar); }
        static string Iso(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        static void Exec(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } }
        static List<T> Rows<T>(SqlConnection c, SqlTransaction tx, Func<SqlDataReader, T> f, string sql, params object[] a) { var l = new List<T>(); using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(f(r)); } return l; }
        static Cfg ReadCfg(SqlDataReader r) { return new Cfg { ProductId = (int)r["ProductId"], VariantId = (int)r["VariantId"], Day = (DateTime)r["Day"], Max = r["MaxQty"] as int?, Pay = (string)r["Pay"] }; }
        static int Strong(string pay) { return pay == "full" ? 2 : pay == "deposit" ? 1 : 0; }

        // ---------- in memory, with the catalog (ShopCache: its version and every stock move) ----------
        public static void Load(SqlConnection c, ShopData d)
        {
            var left = new Dictionary<int, int>(); var infos = new Dictionary<ShopProd, Info>();
            var cfgs = !Shops.Ready || !ShopGoods.Set(c, d.Row.StoreId).Pre ? new List<Cfg>() : Rows(c, null, ReadCfg, "SELECT * FROM dbo.ShopPre WHERE StoreId=@s AND Active=1 AND ArrivedAt IS NULL", "@s", d.Row.StoreId);
            var wait = cfgs.Count == 0 ? new List<int[]>() : Rows(c, null, r => new[] { (int)r[0], (int)r[1], (int)r[2] }, "SELECT ProductId, VariantId, SUM(Qty) FROM dbo.ShopPreOrders WHERE StoreId=@s AND State='wait' GROUP BY ProductId, VariantId", "@s", d.Row.StoreId);
            foreach (var g in cfgs.GroupBy(x => x.ProductId))
            {
                ShopProd p; if (!d.ById.TryGetValue(g.Key, out p) || !p.Track || p.OutMode == "order") continue;
                var info = new Info(); var all = g.FirstOrDefault(x => x.VariantId == 0);
                foreach (var v in p.Vars.Where(x => x.Active))
                {
                    var cfg = g.FirstOrDefault(x => x.VariantId == v.Id) ?? all; if (cfg == null) continue;
                    int used = wait.Where(w => w[0] == p.Id && (cfg.VariantId == 0 || w[1] == v.Id)).Sum(w => w[2]);
                    left[v.Id] = cfg.Max.HasValue ? Math.Max(0, cfg.Max.Value - used) : Unlimited; info.Vars[v.Id] = cfg;
                }
                if (info.Vars.Count > 0) infos[p] = info;
            }
            lock (Infos) foreach (var kv in infos) { Infos.Remove(kv.Key); Infos.Add(kv.Key, kv.Value); }
            foreach (var v in d.Vars.Values) { int x; v.PreLeft = left.TryGetValue(v.Id, out x) ? x : 0; }
        }
        static Info Of(ShopProd p) { Info i; return p != null && Infos.TryGetValue(p, out i) && p.Vars.Any(v => v.PreLeft > 0 && i.Vars.ContainsKey(v.Id)) ? i : null; }

        // ---------- the cart's sum ----------
        public static void Quote(ShopData d, ShopQuote q, bool checkStock)
        {
            int maxQty = Math.Max(1, ShopJson.Int(ShopJson.At(ShopJson.Sub(d.Row.Settings, "order"), "maxQty"), 20)); var pre = new List<object>(); DateTime last = DateTime.MinValue; int pay = 0; bool normal = false; var seen = new HashSet<int>();
            foreach (var l in q.Lines.Where(x => x.P != null && x.V != null))
            {
                var info = Of(l.P); Cfg cfg = null;
                if (info == null || l.V.PreLeft <= 0 || !info.Vars.TryGetValue(l.VariantId, out cfg)) { if (l.Err != "gone" && l.Err != "out") normal = true; continue; }
                int same = q.Lines.Where(x => x.VariantId == l.VariantId).Sum(x => x.Qty), ofProduct = q.Lines.Where(x => x.P == l.P).Sum(x => x.Qty), real = Math.Max(0, l.V.Stock), cap = l.P.MaxQty ?? maxQty;
                l.Max = Math.Min(cap, real + Math.Min(l.V.PreLeft, Unlimited));
                if (checkStock && (l.Err == "out" || l.Err == "stock" || l.Err == "max" || l.Err == "min" || l.Err == null)) l.Err = same > real + l.V.PreLeft ? (l.Max == 0 ? "out" : "stock") : ofProduct > cap ? "max" : ofProduct < l.P.MinQty ? "min" : null;
                if (!seen.Add(l.VariantId)) continue;
                int n = Math.Max(0, same - real);   // the pieces beyond the shelf: what this order books
                if (n > 0) { pre.Add(new Dictionary<string, object> { { "v", l.VariantId }, { "n", n }, { "d", Iso(cfg.Day) } }); if (cfg.Day > last) last = cfg.Day; pay = Math.Max(pay, Strong(cfg.Pay)); }
                if (n < same) normal = true;
            }
            if (pre.Count == 0) return;
            var x2 = q.Cat ?? (q.Cat = new Dictionary<string, object>()); x2["pre"] = pre; x2["day"] = Iso(last); x2["mix"] = normal; x2["pay"] = pay == 2 ? "full" : pay == 1 ? "deposit" : "cod";
        }
        // what a pre-order asks to be paid first, through the store's own transfer ways (none = as any order)
        public static void Paid(ShopData d, ShopQuote q)
        {
            object v; if (q.Cat == null || !q.Cat.TryGetValue("pay", out v) || q.Total <= 0 || ShopPrice.PayWays(d.Row).Count == 0) return;
            var mode = (string)v;
            if (mode == "full") q.Deposit = q.Total;
            else if (mode == "deposit" && q.Deposit <= 0)
            {
                var dp = ShopJson.Sub(ShopJson.Sub(d.Row.Settings, "pay"), "deposit"); decimal x = ShopJson.Dec(ShopJson.At(dp, "value"));
                x = ShopJson.Str(ShopJson.At(dp, "kind")) == "amt" ? x : Math.Round(q.Total * Math.Min(100, x) / 100m, 0, MidpointRounding.AwayFromZero);
                q.Deposit = Math.Max(0, Math.Min(q.Total, x));
            }
        }

        // ---------- the catalog: the label, how many can still be booked ----------
        public static void Card(ShopData d, ShopProd p, Dictionary<string, object> r)
        {
            var info = Of(p); if (info == null) return;
            var real = new Dictionary<string, object>(); DateTime day = DateTime.MinValue; int pay = 0;
            foreach (var a in ShopJson.List(r.ContainsKey("vs") ? r["vs"] : null).OfType<object[]>())
            {
                ShopVar v; Cfg cfg; if (!d.Vars.TryGetValue((int)a[0], out v) || v.PreLeft <= 0 || !info.Vars.TryGetValue(v.Id, out cfg)) continue;
                if (a.Length > 6 && a[6] != null) a[6] = v.PreLeft >= Unlimited ? null : (object)(Math.Max(0, v.Stock) + v.PreLeft);
                real[v.Id.ToString(CultureInfo.InvariantCulture)] = Math.Max(0, v.Stock); if (cfg.Day > day) day = cfg.Day; pay = Math.Max(pay, Strong(cfg.Pay));
            }
            if (real.Count == 0) return;
            r["pre"] = new Dictionary<string, object> { { "d", Iso(day) }, { "v", real }, { "p", pay == 2 ? "full" : pay == 1 ? "deposit" : "cod" } };
            var b = r.ContainsKey("b") ? r["b"] as List<string> : null; if (b != null && p.Total == 0) { b.Remove("low"); if (b.Count == 0) r.Remove("b"); }
        }
        public static void Detail(ShopData d, ShopProd p, Dictionary<string, object> r)
        {
            var info = Of(p); if (info == null) return;
            foreach (var x in ShopJson.List(r.ContainsKey("vars") ? r["vars"] : null).OfType<Dictionary<string, object>>())
            {
                ShopVar v; if (!d.Vars.TryGetValue(ShopJson.Int(x["i"]), out v) || v.PreLeft <= 0 || !info.Vars.ContainsKey(v.Id)) continue;
                x["st"] = v.PreLeft >= Unlimited ? -1 : Math.Max(0, v.Stock) + v.PreLeft;
            }
        }
        public static string BotLine(ShopData d, ShopProd p)
        {
            var info = Of(p); if (info == null) return "";
            var now = p.Vars.Where(v => v.Active && v.PreLeft > 0 && v.Stock <= 0 && info.Vars.ContainsKey(v.Id)).ToList(); if (now.Count == 0) return "";
            var cfg = now.Select(v => info.Vars[v.Id]).OrderByDescending(x => x.Day).First();
            var sb = new StringBuilder("، حجز مسبق").Append(p.Options.Count > 0 && now.Count < p.Vars.Count(v => v.Active) ? " (" + string.Join("، ", now.Take(8).Select(v => v.Label)) + ")" : "").Append(": متوقع يوصل يوم ").Append(DayText(cfg.Day));
            if (cfg.Pay == "full") sb.Append("، والمبلغ كله بيتحوّل قبل التجهيز"); else if (cfg.Pay == "deposit") sb.Append("، وفيه عربون بيتحوّل الأول");
            if (now.All(v => v.PreLeft < Unlimited)) sb.Append("، فاضل ").Append(cfg.VariantId == 0 ? now[0].PreLeft : now.Sum(v => v.PreLeft)).Append(" قطعة للحجز");
            return sb.ToString();
        }

        // ---------- the stock moves (ShopGoods.Moving / Moved) ----------
        static Cfg CfgFor(SqlConnection c, SqlTransaction tx, int storeId, int pid, int vid)
        {
            return Rows(c, tx, ReadCfg, @"SELECT TOP 1 x.* FROM dbo.ShopPre x JOIN dbo.ShopProducts p ON p.Id=x.ProductId WHERE x.StoreId=@s AND x.ProductId=@p AND (x.VariantId=@v OR x.VariantId=0) AND x.Active=1 AND x.ArrivedAt IS NULL AND p.Track=1 AND p.OutMode<>'order' ORDER BY x.VariantId DESC", "@s", storeId, "@p", pid, "@v", vid).FirstOrDefault();
        }
        static string ProdName(SqlConnection c, SqlTransaction tx, int pid) { using (var cmd = Db.Cmd(c, "SELECT Name FROM dbo.ShopProducts WHERE Id=@p", "@p", pid)) { cmd.Transaction = tx; return cmd.ExecuteScalar() as string ?? ""; } }

        public static void Moving(SqlConnection c, SqlTransaction tx, int storeId, int pid, int vid, ref int delta, ref int after, string reason, long? orderId, bool active)
        {
            if (!orderId.HasValue) return;
            if (delta < 0 && after < 0 && active && (reason == "order" || reason == "edit"))
            {
                // the order took more than the shelf had: the rest waits for the goods
                var cfg = CfgFor(c, tx, storeId, pid, vid); if (cfg == null) return;
                int pre = Math.Min(-delta, -after);
                if (cfg.Max.HasValue)
                {
                    int used; using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(Qty),0) FROM dbo.ShopPreOrders WITH (UPDLOCK, HOLDLOCK) WHERE StoreId=@s AND ProductId=@p AND (@all=1 OR VariantId=@v) AND State='wait'", "@s", storeId, "@p", pid, "@v", vid, "@all", cfg.VariantId == 0 ? 1 : 0)) { cmd.Transaction = tx; used = (int)cmd.ExecuteScalar(); }
                    if (used + pre > cfg.Max.Value) throw new ShopException("cart", "الحجز المسبق لـ«" + ProdName(c, tx, pid) + "» " + (used >= cfg.Max.Value ? "اكتمل." : "فاضل فيه " + (cfg.Max.Value - used) + " قطعة بس."));
                }
                Exec(c, tx, "INSERT INTO dbo.ShopPreOrders(StoreId, OrderId, ProductId, VariantId, Qty, Told) VALUES(@s, @o, @p, @v, @q, @d); UPDATE dbo.ShopVariants SET Stock=Stock+@q WHERE Id=@v", "@s", storeId, "@o", orderId.Value, "@p", pid, "@v", vid, "@q", pre, "@d", cfg.Day);
                ShopOrders.Log_(c, tx, storeId, orderId.Value, "system", "حجز مسبق: " + pre + " من «" + ProdName(c, tx, pid) + "»، متوقع يوصل يوم " + DayText(cfg.Day));
                delta += pre; after += pre;
            }
            else if (delta > 0 && (reason == "cancelled" || reason == "returned" || reason == "edit"))
            {
                // what still waited never left the shelf, so it doesn't come back to it; its place is free again
                int x = 0;
                foreach (var r in Rows(c, tx, rd => new[] { Convert.ToInt64(rd[0]), (int)rd[1] }, "SELECT Id, Qty FROM dbo.ShopPreOrders WITH (UPDLOCK) WHERE OrderId=@o AND VariantId=@v AND State='wait' ORDER BY Id DESC", "@o", orderId.Value, "@v", vid))
                {
                    if (x == delta) break; int k = (int)Math.Min(delta - x, r[1]); x += k;
                    Exec(c, tx, k == r[1] ? "UPDATE dbo.ShopPreOrders SET State='gone' WHERE Id=@i" : "UPDATE dbo.ShopPreOrders SET Qty=Qty-@k WHERE Id=@i", "@i", r[0], "@k", k);
                }
                if (reason != "edit") Exec(c, tx, "UPDATE dbo.ShopPreOrders SET State='gone' WHERE OrderId=@o AND VariantId=@v AND State='in'", "@o", orderId.Value, "@v", vid);
                if (x > 0) { Exec(c, tx, "UPDATE dbo.ShopVariants SET Stock=Stock-@x WHERE Id=@v", "@x", x, "@v", vid); delta -= x; after -= x; }
            }
            else if (delta < 0 && reason == "reopen")
                // brought back to life with the pieces on the shelf: they are taken now, so nothing waits any more
                Exec(c, tx, "UPDATE dbo.ShopPreOrders SET State='in', InAt=SYSUTCDATETIME(), InToldAt=SYSUTCDATETIME() WHERE OrderId=@o AND VariantId=@v AND State='gone'", "@o", orderId.Value, "@v", vid);
        }

        // new pieces on the shelf: to the waiting orders first, the oldest first (a part of an order's pieces is kept for it too)
        public static void Cover(SqlConnection c, SqlTransaction tx, int storeId, int pid, int vid, bool places)
        {
            int stock; using (var cmd = Db.Cmd(c, "SELECT Stock FROM dbo.ShopVariants WITH (UPDLOCK) WHERE Id=@v", "@v", vid)) { cmd.Transaction = tx; var o = cmd.ExecuteScalar(); if (o == null) return; stock = (int)o; }
            if (stock <= 0) return;
            int avail = stock; var got = new List<long[]>();
            foreach (var r in Rows(c, tx, rd => new[] { Convert.ToInt64(rd[0]), Convert.ToInt64(rd[1]), (int)rd[2] }, "SELECT Id, OrderId, Qty FROM dbo.ShopPreOrders WITH (UPDLOCK) WHERE StoreId=@s AND VariantId=@v AND State='wait' ORDER BY Id", "@s", storeId, "@v", vid))
            {
                if (avail == 0) break; int k = (int)Math.Min(avail, r[2]); avail -= k; got.Add(new[] { r[1], k });
                if (k == r[2]) Exec(c, tx, "UPDATE dbo.ShopPreOrders SET State='in', InAt=SYSUTCDATETIME() WHERE Id=@i", "@i", r[0]);
                else Exec(c, tx, "UPDATE dbo.ShopPreOrders SET Qty=Qty-@k WHERE Id=@i; INSERT INTO dbo.ShopPreOrders(StoreId, OrderId, ProductId, VariantId, Qty, State, Told, InAt) SELECT StoreId, OrderId, ProductId, VariantId, @k, 'in', Told, SYSUTCDATETIME() FROM dbo.ShopPreOrders WHERE Id=@i", "@i", r[0], "@k", k);
            }
            if (got.Count == 0) return;
            Exec(c, tx, "UPDATE dbo.ShopVariants SET Stock=Stock-@t WHERE Id=@v; UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@t", stock - avail, "@v", vid, "@s", storeId);
            int left = stock; string name = ProdName(c, tx, pid);
            foreach (var g in got)
            {
                int k = (int)g[1]; left -= k;
                Exec(c, tx, "INSERT INTO dbo.ShopStock(StoreId, ProductId, VariantId, Delta, After_, Reason, OrderId, Note) VALUES(@s, @p, @v, @d, @a, 'pre', @o, N'لحجز مسبق')", "@s", storeId, "@p", pid, "@v", vid, "@d", -k, "@a", left, "@o", g[0]);
                if (places) ShopPlaces.Take(c, tx, storeId, vid, k, g[0], ShopPlaces.Prefer);
                ShopOrders.Log_(c, tx, storeId, g[0], "system", "البضاعة وصلت: " + k + " من «" + name + "» اتحجزوا للطلب");
            }
            HostingEnvironment.QueueBackgroundWorkItem(ct => SendDue(ct));
        }

        // ---------- the owner ----------
        public static void Save(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            int pid = ShopJson.Int(ShopJson.At(b, "product")), vid = ShopJson.Int(ShopJson.At(b, "variant")); var d = ShopCache.Get(c, sh); ShopProd p;
            if (!d.ById.TryGetValue(pid, out p)) throw new ShopException("not_found", "المنتج ده مش موجود.");
            if (vid != 0 && !p.Vars.Any(v => v.Id == vid)) throw new ShopException("not_found", "المقاس ده مش موجود.");
            if (!ShopGoods.Set(c, s.Id).Pre) throw new ShopException("off", "شغّل «حجز مسبق» من «المميزات» الأول.");
            bool on = ShopJson.Bool(ShopJson.At(b, "on"), true);
            if (!on) { Exec(c, null, "UPDATE dbo.ShopPre SET Active=0 WHERE StoreId=@s AND ProductId=@p AND VariantId=@v", "@s", s.Id, "@p", pid, "@v", vid); Shops.Bump(c, s.Id); return; }
            DateTime day; var today = DateTime.UtcNow.AddHours(2).Date;
            if (!DateTime.TryParseExact(ShopJson.Str(ShopJson.At(b, "day"), 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) throw new ShopException("day", "اختار اليوم اللي متوقع يوصل فيه.");
            if (day < today) throw new ShopException("day", "اليوم ده عدّى. اختار يوم جاي.");
            if (day > today.AddDays(365)) throw new ShopException("day", "اختار يوم في خلال سنة.");
            int max = ShopJson.Int(ShopJson.At(b, "max")); if (max < 0 || max > 100000) throw new ShopException("max", "اكتب العدد صح.");
            string pay = ShopJson.Str(ShopJson.At(b, "pay"), 8); if (pay != "full" && pay != "deposit") pay = "cod";
            if (pay != "cod" && ShopPrice.PayWays(sh).Count == 0) throw new ShopException("pay", "ضيف طريقة تحويل (إنستاباي، محفظة، بنك) من «الدفع والفروع» الأول.");
            Exec(c, null, @"MERGE dbo.ShopPre AS t USING (SELECT @s AS StoreId, @p AS ProductId, @v AS VariantId) AS k ON t.StoreId=k.StoreId AND t.ProductId=k.ProductId AND t.VariantId=k.VariantId
                WHEN MATCHED THEN UPDATE SET Day=@d, MaxQty=@m, Pay=@pay, Active=1, ArrivedAt=NULL, At=SYSUTCDATETIME() WHEN NOT MATCHED THEN INSERT (StoreId, ProductId, VariantId, Day, MaxQty, Pay) VALUES (@s, @p, @v, @d, @m, @pay);
                UPDATE dbo.ShopProducts SET Track=1, OutMode=CASE WHEN OutMode='order' THEN 'show' ELSE OutMode END WHERE StoreId=@s AND Id=@p", "@s", s.Id, "@p", pid, "@v", vid, "@d", day, "@m", max > 0 ? (object)max : null, "@pay", pay);
            Shops.Bump(c, s.Id);
            HostingEnvironment.QueueBackgroundWorkItem(ct => SendDue(ct));   // a new day: the waiting customers hear it once
        }

        // «وصل»: the pieces that came, and where they were put; the waiting orders take them first, and the pre-order ends
        public static void Arrived(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            int pid = ShopJson.Int(ShopJson.At(b, "product")), place = ShopJson.Int(ShopJson.At(b, "place")); var d = ShopCache.Get(c, sh); ShopProd p;
            if (!d.ById.TryGetValue(pid, out p)) throw new ShopException("not_found", "المنتج ده مش موجود.");
            var qty = ShopJson.Sub(b, "qty");
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    foreach (var v in p.Vars)
                    {
                        int n = Math.Max(0, Math.Min(1000000, ShopJson.Int(ShopJson.At(qty, v.Id.ToString(CultureInfo.InvariantCulture))))); if (n == 0) continue;
                        int after; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopVariants SET Stock=Stock+@n OUTPUT INSERTED.Stock WHERE Id=@v AND StoreId=@s", "@n", n, "@v", v.Id, "@s", s.Id)) { cmd.Transaction = tx; after = (int)cmd.ExecuteScalar(); }
                        ShopPlaces.Prefer = place;
                        try { ShopOrders.Stock(c, tx, s.Id, pid, v.Id, n, after, "arrive", null, "البضاعة وصلت"); } finally { ShopPlaces.Prefer = 0; }
                    }
                    Exec(c, tx, "UPDATE dbo.ShopPre SET ArrivedAt=SYSUTCDATETIME(), Active=0 WHERE StoreId=@s AND ProductId=@p; UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@s", s.Id, "@p", pid);
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            Shops.Bump(c, s.Id);
            HostingEnvironment.QueueBackgroundWorkItem(ct => SendDue(ct));
        }

        static object RowJson(SqlDataReader r) { return new Dictionary<string, object> { { "order", Convert.ToInt64(r["OrderId"]) }, { "no", r["No"] is int ? (int)r["No"] : 0 }, { "name", r["CName"] as string ?? "" }, { "v", (int)r["VariantId"] }, { "o", ((r["Opts"] as string) ?? "").Replace("|", " / ") }, { "q", (int)r["Qty"] }, { "state", (string)r["State"] }, { "status", r["Status"] as string ?? "" } }; }
        const string RowSql = "SELECT w.*, o.No, o.Name AS CName, o.Status, v.Opts FROM dbo.ShopPreOrders w LEFT JOIN dbo.ShopOrders o ON o.Id=w.OrderId LEFT JOIN dbo.ShopVariants v ON v.Id=w.VariantId ";
        public static object Of(SqlConnection c, Shop sh, ShopProd p)
        {
            var cfgs = Rows(c, null, r => new Dictionary<string, object> { { "v", (int)r["VariantId"] }, { "day", Iso((DateTime)r["Day"]) }, { "max", r["MaxQty"] as int? }, { "pay", (string)r["Pay"] }, { "on", (bool)r["Active"] && !(r["ArrivedAt"] is DateTime) }, { "arrived", r["ArrivedAt"] is DateTime } }, "SELECT * FROM dbo.ShopPre WHERE StoreId=@s AND ProductId=@p", "@s", sh.StoreId, "@p", p.Id);
            var rows = Rows(c, null, RowJson, RowSql + "WHERE w.StoreId=@s AND w.ProductId=@p AND w.State IN ('wait','in') ORDER BY w.Id DESC", "@s", sh.StoreId, "@p", p.Id);
            return new Dictionary<string, object> { { "cfg", cfgs }, { "rows", rows.Take(200) }, { "waiting", rows.Cast<Dictionary<string, object>>().Where(x => (string)x["state"] == "wait").Sum(x => (int)x["q"]) }, { "ways", ShopPrice.PayWays(sh).Count } };
        }
        // every product on pre-order, with how many pieces wait
        public static object List(SqlConnection c, Shop sh)
        {
            var d = ShopCache.Get(c, sh);
            var wait = Rows(c, null, r => new[] { (int)r[0], (int)r[1], (int)r[2] }, "SELECT ProductId, SUM(CASE WHEN State='wait' THEN Qty ELSE 0 END), COUNT(DISTINCT CASE WHEN State='wait' THEN OrderId END) FROM dbo.ShopPreOrders WHERE StoreId=@s AND State IN ('wait','in') GROUP BY ProductId", "@s", sh.StoreId).ToDictionary(x => x[0]);
            var cfgs = Rows(c, null, ReadCfgFull, "SELECT * FROM dbo.ShopPre WHERE StoreId=@s ORDER BY Day", "@s", sh.StoreId);
            return new Dictionary<string, object> { { "list", cfgs.GroupBy(x => (int)x["p"]).Where(g => d.ById.ContainsKey(g.Key)).Select(g => { var p = d.ById[g.Key]; int[] w; wait.TryGetValue(g.Key, out w); return new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "img", p.Pics.Count > 0 ? ShopImages.Url(p.Pics[0].Path, true) : "" }, { "cfg", g.ToArray() }, { "waiting", w == null ? 0 : w[1] }, { "orders", w == null ? 0 : w[2] } }; }).ToArray() } };
        }
        static Dictionary<string, object> ReadCfgFull(SqlDataReader r) { return new Dictionary<string, object> { { "p", (int)r["ProductId"] }, { "v", (int)r["VariantId"] }, { "day", Iso((DateTime)r["Day"]) }, { "max", r["MaxQty"] as int? }, { "pay", (string)r["Pay"] }, { "on", (bool)r["Active"] && !(r["ArrivedAt"] is DateTime) }, { "arrived", r["ArrivedAt"] is DateTime } }; }

        public static List<object> OfOrder(SqlConnection c, int storeId, long orderId)
        {
            return Rows(c, null, r => (object)new Dictionary<string, object> { { "v", (int)r["VariantId"] }, { "q", (int)r["Qty"] }, { "state", (string)r["State"] }, { "day", Iso(r["Day"] is DateTime ? (DateTime)r["Day"] : (DateTime)r["Told"]) } },
                "SELECT w.*, (SELECT TOP 1 x.Day FROM dbo.ShopPre x WHERE x.StoreId=w.StoreId AND x.ProductId=w.ProductId AND (x.VariantId=w.VariantId OR x.VariantId=0) ORDER BY x.VariantId DESC) AS Day FROM dbo.ShopPreOrders w WHERE w.OrderId=@o AND w.StoreId=@s AND w.State IN ('wait','in') ORDER BY w.Id", "@o", orderId, "@s", storeId);
        }
        public static void Prep(SqlConnection c, int storeId, List<ShopOrder> os, List<Dictionary<string, object>> rows)
        {
            var w = Rows(c, null, r => new[] { Convert.ToInt64(r[0]), (int)r[1], (int)r[2] }, "SELECT OrderId, VariantId, Qty FROM dbo.ShopPreOrders WHERE StoreId=@s AND State='wait' AND OrderId IN (" + string.Join(",", os.Select(o => o.Id)) + ")", "@s", storeId); if (w.Count == 0) return;
            foreach (var row in rows)
            {
                int v = ShopJson.Int(ShopJson.At(row, "v")); var nos = new HashSet<int>((int[])row["nos"]); var ids = new HashSet<long>(os.Where(o => nos.Contains(o.No)).Select(o => o.Id));
                int n = (int)w.Where(x => x[1] == v && ids.Contains(x[0])).Sum(x => x[2]); if (n > 0) row["pre"] = n;
            }
        }

        // ---------- the customers' messages: the new day, and the pieces that came ----------
        static int sending;
        public static async Task SendDue(CancellationToken ct)
        {
            if (!Shops.Ready || WaNotify.QuietUntil().HasValue || Interlocked.Exchange(ref sending, 1) == 1) return;
            try
            {
                using (var c = Db.Open())
                {
                    // a day that moved: one message per order, with the day it is now
                    var moved = Rows(c, null, r => new object[] { Convert.ToInt64(r[0]), (int)r[1], (DateTime)r[2], (DateTime)r[3] }, @"SELECT TOP 200 w.OrderId, w.StoreId, MIN(w.Told), MAX(x.Day) FROM dbo.ShopPreOrders w
                        CROSS APPLY (SELECT TOP 1 Day FROM dbo.ShopPre p WHERE p.StoreId=w.StoreId AND p.ProductId=w.ProductId AND (p.VariantId=w.VariantId OR p.VariantId=0) AND p.Active=1 AND p.ArrivedAt IS NULL ORDER BY p.VariantId DESC) x
                        WHERE w.State='wait' AND w.Told<>x.Day GROUP BY w.OrderId, w.StoreId");
                    foreach (var m in moved)
                    {
                        if (ct.IsCancellationRequested) return;
                        long oid = (long)m[0]; int sid = (int)m[1]; DateTime was = (DateTime)m[2], day = (DateTime)m[3];
                        int n; using (var cmd = Db.Cmd(c, "UPDATE w SET Told=x.Day FROM dbo.ShopPreOrders w CROSS APPLY (SELECT TOP 1 Day FROM dbo.ShopPre p WHERE p.StoreId=w.StoreId AND p.ProductId=w.ProductId AND (p.VariantId=w.VariantId OR p.VariantId=0) AND p.Active=1 AND p.ArrivedAt IS NULL ORDER BY p.VariantId DESC) x WHERE w.OrderId=@o AND w.State='wait' AND w.Told<>x.Day", "@o", oid)) n = cmd.ExecuteNonQuery();
                        if (n == 0) continue;   // another run took it
                        var o = ShopOrders.ById(c, sid, oid); var s = Db.StoreById(c, sid); var sh = Shops.ByStore(c, sid);
                        if (o == null || s == null || sh == null || o.Status == "cancelled" || o.Status == "returned") continue;
                        bool en = o.Lang == "en"; string items = Names(c, oid);
                        string text = en ? "Hello " + First(o.Name) + " 👋\nThe expected day for " + items + " in your order " + o.NoText + " from *" + (sh.NameEn ?? sh.Name) + "* has changed: it is now expected on *" + DayText(day, true) + "*.\nWe'll message you as soon as it arrives."
                                         : "أهلًا " + First(o.Name) + " 👋\nميعاد وصول " + items + " في طلبك " + o.NoText + " من *" + sh.Name + "* اتغيّر: متوقع يوصل يوم *" + DayText(day) + "*.\nهنبعتلك أول ما يوصل.";
                        var why = await WaNotify.ToCustomer(c, s, o.Phone, text, "pre-day-" + oid + "-" + Iso(day));
                        if (why == null) ShopOrders.Log_(c, null, sid, oid, "system", "اتبعت للزبون إن الميعاد بقى يوم " + DayText(day));
                        else ShopOrders.Log_(c, null, sid, oid, "system", "ماقدرناش نبلّغ الزبون بالميعاد الجديد: " + why);
                    }
                    // every piece the order waited for is in: once
                    var ready = Rows(c, null, r => new object[] { Convert.ToInt64(r[0]), (int)r[1] }, @"SELECT TOP 200 w.OrderId, w.StoreId FROM dbo.ShopPreOrders w WHERE w.State='in' AND w.InToldAt IS NULL
                        AND NOT EXISTS (SELECT 1 FROM dbo.ShopPreOrders x WHERE x.OrderId=w.OrderId AND x.State='wait') GROUP BY w.OrderId, w.StoreId");
                    foreach (var m in ready)
                    {
                        if (ct.IsCancellationRequested) return;
                        long oid = (long)m[0]; int sid = (int)m[1];
                        int n; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopPreOrders SET InToldAt=SYSUTCDATETIME() WHERE OrderId=@o AND State='in' AND InToldAt IS NULL AND NOT EXISTS (SELECT 1 FROM dbo.ShopPreOrders x WHERE x.OrderId=@o AND x.State='wait')", "@o", oid)) n = cmd.ExecuteNonQuery();
                        if (n == 0) continue;
                        var o = ShopOrders.ById(c, sid, oid); var s = Db.StoreById(c, sid); var sh = Shops.ByStore(c, sid);
                        if (o == null || s == null || sh == null || o.Status == "cancelled" || o.Status == "returned") continue;
                        bool en = o.Lang == "en";
                        string text = en ? "Good news " + First(o.Name) + " 🎉\nEverything in your order " + o.NoText + " from *" + (sh.NameEn ?? sh.Name) + "* has arrived. We're preparing it and it ships soon."
                                         : "خبر حلو يا " + First(o.Name) + " 🎉\nكل اللي في طلبك " + o.NoText + " من *" + sh.Name + "* وصل. بنجهّزه وهيتشحن قريب.";
                        var why = await WaNotify.ToCustomer(c, s, o.Phone, text, "pre-in-" + oid);
                        ShopOrders.Log_(c, null, sid, oid, "system", why == null ? "البضاعة كلها وصلت، والزبون اتبلّغ" : "البضاعة كلها وصلت، وماقدرناش نبلّغ الزبون: " + why);
                    }
                }
            }
            finally { Interlocked.Exchange(ref sending, 0); }
        }
        static string First(string n) { return (n ?? "").Trim().Split(' ')[0]; }
        static string Names(SqlConnection c, long oid)
        {
            var l = Rows(c, null, r => (string)r[0], "SELECT DISTINCT p.Name FROM dbo.ShopPreOrders w JOIN dbo.ShopProducts p ON p.Id=w.ProductId WHERE w.OrderId=@o AND w.State='wait'", "@o", oid);
            return l.Count == 0 ? "" : "«" + string.Join("» و«", l.Take(3)) + "»";
        }
    }
}
