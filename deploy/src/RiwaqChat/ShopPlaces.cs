// «أكتر من مخزن»: the owner's branches (settings "branches") and warehouses (a name, not a pickup point) each hold their own stock.
// ShopVariants.Stock stays the total that all the existing code reads: every change of it reaches ShopOrders.Stock, and from there
// Moved here, which moves the places by the same amount, so the sum of the places is always the total. An order takes from a place:
// pickup at a branch = that branch; delivery = the first place in the owner's order that has all of it, otherwise a part from each in
// that order. What an order took is kept (ShopOrderAt), so a cancel or a return puts each piece back where it came from.
// With one place nothing shows anywhere: it simply holds everything.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public static class ShopPlaces
    {
        public const int Max = 20;
        public class Place { public int Id, Sort; public string Kind, Ref, Name; public bool Gone; }

        static List<T> Rows<T>(SqlConnection c, SqlTransaction tx, Func<SqlDataReader, T> f, string sql, params object[] a) { var l = new List<T>(); using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(f(r)); } return l; }
        static void Exec(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } }
        static Place Read(SqlDataReader r) { return new Place { Id = (int)r["Id"], Kind = ((string)r["Kind"]).Trim(), Ref = r["Ref"] as string, Name = (string)r["Name"], Sort = (int)r["Sort"], Gone = (bool)r["Gone"] }; }
        public static List<Place> All(SqlConnection c, int storeId, SqlTransaction tx = null) { return Rows(c, tx, Read, "SELECT * FROM dbo.ShopPlaces WHERE StoreId=@s ORDER BY Gone, Sort, Id", "@s", storeId); }
        public static int Count(SqlConnection c, int storeId) { using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopPlaces WHERE StoreId=@s AND Gone=0", "@s", storeId)) return (int)cmd.ExecuteScalar(); }
        static string Name(Place p) { return p.Gone ? p.Name + " (الفرع اتشال)" : p.Name; }

        public static List<object> List(SqlConnection c, int storeId)
        {
            var sums = new Dictionary<int, int>(); foreach (var x in Rows(c, null, r => new[] { (int)r[0], (int)r[1] }, "SELECT a.PlaceId, SUM(a.Qty) FROM dbo.ShopStockAt a JOIN dbo.ShopVariants v ON v.Id=a.VariantId WHERE a.StoreId=@s GROUP BY a.PlaceId", "@s", storeId)) sums[x[0]] = x[1];
            return All(c, storeId).Select(p => (object)new Dictionary<string, object> { { "id", p.Id }, { "kind", p.Kind }, { "name", Name(p) }, { "gone", p.Gone }, { "qty", sums.ContainsKey(p.Id) ? sums[p.Id] : 0 } }).ToList();
        }

        // the branches in the settings are places; a store with none gets «المخزن الرئيسي». fill: whatever stock isn't in a place goes to the first
        public static void Sync(SqlConnection c, Shop sh, bool fill = false)
        {
            int s = sh.StoreId; var have = All(c, s); var brs = ShopPrice.Branches(Shops.ByStore(c, s) ?? sh);
            foreach (var b in brs)
            {
                string id = ShopJson.Str(ShopJson.At(b, "id"), 20), n = ShopJson.Str(ShopJson.At(b, "n"), 60); if (id.Length == 0) continue;
                var p = have.FirstOrDefault(x => x.Kind == "b" && x.Ref == id);
                if (p == null) { Exec(c, null, "INSERT INTO dbo.ShopPlaces(StoreId, Kind, Ref, Name, Sort) VALUES(@s, 'b', @r, @n, (SELECT ISNULL(MAX(Sort),0)+1 FROM dbo.ShopPlaces WHERE StoreId=@s))", "@s", s, "@r", id, "@n", n.Length > 1 ? n : "فرع"); fill = true; }
                else if (p.Gone || (n.Length > 1 && p.Name != n)) Exec(c, null, "UPDATE dbo.ShopPlaces SET Gone=0, Name=@n WHERE Id=@i", "@i", p.Id, "@n", n.Length > 1 ? n : p.Name);
            }
            foreach (var p in have.Where(x => x.Kind == "b" && !x.Gone && !brs.Any(b => ShopJson.Str(ShopJson.At(b, "id"), 20) == x.Ref))) Exec(c, null, "UPDATE dbo.ShopPlaces SET Gone=1 WHERE Id=@i", "@i", p.Id);
            if (!All(c, s).Any(x => !x.Gone)) { Exec(c, null, "INSERT INTO dbo.ShopPlaces(StoreId, Kind, Name, Sort) VALUES(@s, 'w', N'المخزن الرئيسي', 0)", "@s", s); fill = true; }
            if (!fill) return;
            // one statement: what each size has beyond what its places hold goes to the first place
            Exec(c, null, @"DECLARE @f INT = (SELECT TOP 1 Id FROM dbo.ShopPlaces WHERE StoreId=@s AND Gone=0 ORDER BY Sort, Id);
                MERGE dbo.ShopStockAt AS t USING (SELECT v.Id AS VariantId, v.Stock - ISNULL((SELECT SUM(a.Qty) FROM dbo.ShopStockAt a WHERE a.VariantId=v.Id), 0) AS Diff FROM dbo.ShopVariants v WHERE v.StoreId=@s) AS x
                ON t.VariantId=x.VariantId AND t.PlaceId=@f
                WHEN MATCHED AND x.Diff<>0 THEN UPDATE SET Qty=t.Qty+x.Diff
                WHEN NOT MATCHED AND x.Diff<>0 THEN INSERT (StoreId, VariantId, PlaceId, Qty) VALUES (@s, x.VariantId, @f, x.Diff);
                DELETE a FROM dbo.ShopStockAt a WHERE a.StoreId=@s AND NOT EXISTS (SELECT 1 FROM dbo.ShopVariants v WHERE v.Id=a.VariantId);", "@s", s);
        }

        // ---------- the one place every stock change goes through ----------
        [ThreadStatic] public static bool Exact;   // the owner set each place by hand: the places are already right
        [ThreadStatic] public static int Prefer;   // where new pieces arrived (pre-orders take them from there first)

        public static void Moved(SqlConnection c, SqlTransaction tx, int storeId, int variantId, int delta, int after, string reason, long? orderId)
        {
            if (Exact || delta == 0) return;
            var places = All(c, storeId, tx).Where(p => !p.Gone).ToList(); if (places.Count == 0) return;
            var at = Hold(c, tx, variantId); int before = after - delta, sum = at.Values.Sum();
            if (sum != before) Add(at, places[0].Id, before - sum);   // stock that moved while the places were off
            bool forOrder = orderId.HasValue && (reason == "order" || reason == "edit" || reason == "reopen" || reason == "pre");
            if (delta < 0) TakeIn(c, tx, storeId, variantId, -delta, forOrder ? orderId : null, places, at, Prefer, true);
            else GiveIn(c, tx, storeId, variantId, delta, orderId.HasValue && (reason == "cancelled" || reason == "returned" || reason == "return" || reason == "edit") ? orderId : null, places, at, Prefer);
            Write(c, tx, storeId, variantId, at);
        }

        // pieces leave for an order (or the owner takes some without saying from where)
        public static void Take(SqlConnection c, SqlTransaction tx, int storeId, int variantId, int n, long? orderId, int prefer = 0)
        {
            var places = All(c, storeId, tx).Where(p => !p.Gone).ToList(); if (places.Count == 0 || n <= 0) return;
            var at = Hold(c, tx, variantId); TakeIn(c, tx, storeId, variantId, n, orderId, places, at, prefer, false); Write(c, tx, storeId, variantId, at);
        }

        static Dictionary<int, int> Hold(SqlConnection c, SqlTransaction tx, int variantId)
        {
            var d = new Dictionary<int, int>(); foreach (var x in Rows(c, tx, r => new[] { (int)r[0], (int)r[1] }, "SELECT PlaceId, Qty FROM dbo.ShopStockAt WITH (UPDLOCK, HOLDLOCK) WHERE VariantId=@v", "@v", variantId)) d[x[0]] = x[1];
            return d;
        }
        static void Add(Dictionary<int, int> at, int place, int n) { int q; at.TryGetValue(place, out q); at[place] = q + n; }
        static void Write(SqlConnection c, SqlTransaction tx, int storeId, int variantId, Dictionary<int, int> at)
        {
            foreach (var kv in at)
                Exec(c, tx, "MERGE dbo.ShopStockAt AS t USING (SELECT @v AS VariantId, @p AS PlaceId) AS k ON t.VariantId=k.VariantId AND t.PlaceId=k.PlaceId WHEN MATCHED THEN UPDATE SET Qty=@q WHEN NOT MATCHED THEN INSERT (StoreId, VariantId, PlaceId, Qty) VALUES (@s, @v, @p, @q);", "@v", variantId, "@p", kv.Key, "@q", kv.Value, "@s", storeId);
        }

        // strict: an order being placed must find its branch's pieces; pieces kept for a waiting pre-order come from wherever they arrived
        static void TakeIn(SqlConnection c, SqlTransaction tx, int storeId, int variantId, int n, long? orderId, List<Place> places, Dictionary<int, int> at, int prefer, bool strict)
        {
            Func<int, int> q = id => { int x; return at.TryGetValue(id, out x) ? x : 0; };
            var took = new Dictionary<int, int>();
            Place branch = null;
            if (orderId.HasValue)
            {
                string ex; using (var cmd = Db.Cmd(c, "SELECT Extras FROM dbo.ShopOrders WHERE Id=@o AND StoreId=@s", "@o", orderId.Value, "@s", storeId)) { cmd.Transaction = tx; ex = cmd.ExecuteScalar() as string; }
                var bid = ShopJson.Str(ShopJson.At(ShopJson.Sub(ShopJson.Obj(ex), "branch"), "id"), 20); if (bid.Length > 0) branch = places.FirstOrDefault(p => p.Kind == "b" && p.Ref == bid);
            }
            if (branch != null && !strict && q(branch.Id) < n) branch = null;
            if (branch != null)
            {
                // picked up at a branch: from that branch only (the cart's sum already said what it has)
                if (q(branch.Id) < n && !Free(c, tx, variantId)) throw new ShopException("branch", "مش موجود كفاية في " + branch.Name + ". اختار التوصيل أو فرع تاني.");
                took[branch.Id] = n;
            }
            else
            {
                var order = places.OrderBy(p => p.Id == prefer ? 0 : 1).ThenBy(p => places.IndexOf(p)).ToList();
                var whole = order.FirstOrDefault(p => q(p.Id) >= n);
                if (whole != null) took[whole.Id] = n;
                else { int left = n; foreach (var p in order) { int k = Math.Min(left, Math.Max(0, q(p.Id))); if (k > 0) { took[p.Id] = k; left -= k; } if (left == 0) break; } if (left > 0) took[order[0].Id] = (took.ContainsKey(order[0].Id) ? took[order[0].Id] : 0) + left; }
            }
            foreach (var kv in took)
            {
                Add(at, kv.Key, -kv.Value);
                if (orderId.HasValue) Exec(c, tx, "MERGE dbo.ShopOrderAt AS t USING (SELECT @o AS OrderId, @v AS VariantId, @p AS PlaceId) AS k ON t.OrderId=k.OrderId AND t.VariantId=k.VariantId AND t.PlaceId=k.PlaceId WHEN MATCHED THEN UPDATE SET Qty=t.Qty+@q WHEN NOT MATCHED THEN INSERT (StoreId, OrderId, VariantId, PlaceId, Qty) VALUES (@s, @o, @v, @p, @q);", "@o", orderId.Value, "@v", variantId, "@p", kv.Key, "@q", kv.Value, "@s", storeId);
            }
        }
        static bool Free(SqlConnection c, SqlTransaction tx, int variantId) { using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopVariants v JOIN dbo.ShopProducts p ON p.Id=v.ProductId WHERE v.Id=@v AND (p.OutMode='order' OR p.Track=0)", "@v", variantId)) { cmd.Transaction = tx; return (int)cmd.ExecuteScalar() > 0; } }

        static void GiveIn(SqlConnection c, SqlTransaction tx, int storeId, int variantId, int n, long? orderId, List<Place> places, Dictionary<int, int> at, int prefer)
        {
            if (orderId.HasValue)
            {
                // back where it came from, the last place first
                foreach (var x in Rows(c, tx, r => new[] { (int)r[0], (int)r[1] }, "SELECT PlaceId, Qty FROM dbo.ShopOrderAt WITH (UPDLOCK) WHERE OrderId=@o AND VariantId=@v AND Qty>0 ORDER BY PlaceId DESC", "@o", orderId.Value, "@v", variantId))
                {
                    if (n == 0) break; int k = Math.Min(n, x[1]); Add(at, x[0], k); n -= k;
                    Exec(c, tx, "UPDATE dbo.ShopOrderAt SET Qty=Qty-@k WHERE OrderId=@o AND VariantId=@v AND PlaceId=@p", "@k", k, "@o", orderId.Value, "@v", variantId, "@p", x[0]);
                }
            }
            if (n > 0) Add(at, places.Any(p => p.Id == prefer) ? prefer : places[0].Id, n);
        }

        // ---------- the owner ----------
        public static int Save(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int id = ShopJson.Int(ShopJson.At(b, "id")); string n = Regex.Replace(ShopJson.Str(ShopJson.At(b, "name"), 60), @"\s+", " ").Trim();
            if (n.Length < 2) throw new ShopException("name", "اكتب اسم المخزن.");
            var all = All(c, sh.StoreId);
            if (all.Any(p => p.Id != id && !p.Gone && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) throw new ShopException("name", "فيه مكان بنفس الاسم.");
            if (id > 0)
            {
                var p = all.FirstOrDefault(x => x.Id == id); if (p == null) throw new ShopException("not_found", "المكان ده مش موجود.");
                if (p.Kind == "b") throw new ShopException("branch", "اسم الفرع بيتغيّر من «الدفع والفروع».");
                Exec(c, null, "UPDATE dbo.ShopPlaces SET Name=@n WHERE Id=@i AND StoreId=@s", "@n", n, "@i", id, "@s", sh.StoreId); return id;
            }
            if (all.Count(p => !p.Gone) >= Max) throw new ShopException("max", "أقصى عدد " + Max + " مكان.");
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopPlaces(StoreId, Kind, Name, Sort) OUTPUT INSERTED.Id VALUES(@s, 'w', @n, (SELECT ISNULL(MAX(Sort),0)+1 FROM dbo.ShopPlaces WHERE StoreId=@s))", "@s", sh.StoreId, "@n", n)) id = (int)cmd.ExecuteScalar();
            Shops.Bump(c, sh.StoreId); return id;
        }
        public static void Delete(SqlConnection c, Shop sh, int id)
        {
            var p = All(c, sh.StoreId).FirstOrDefault(x => x.Id == id); if (p == null) throw new ShopException("not_found", "المكان ده مش موجود.");
            if (p.Kind == "b" && !p.Gone) throw new ShopException("branch", "الفرع بيتشال من «الدفع والفروع».");
            int left; using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(a.Qty),0) FROM dbo.ShopStockAt a JOIN dbo.ShopVariants v ON v.Id=a.VariantId WHERE a.PlaceId=@i AND a.Qty<>0", "@i", id)) left = (int)cmd.ExecuteScalar();
            if (left != 0) throw new ShopException("stock", "فيه " + left + " قطعة في «" + p.Name + "». انقلهم لمكان تاني الأول.");
            if (All(c, sh.StoreId).Count(x => !x.Gone && x.Id != id) == 0) throw new ShopException("last", "لازم يفضل مكان واحد على الأقل.");
            Exec(c, null, "DELETE FROM dbo.ShopStockAt WHERE PlaceId=@i AND StoreId=@s; DELETE FROM dbo.ShopPlaces WHERE Id=@i AND StoreId=@s", "@i", id, "@s", sh.StoreId); Shops.Bump(c, sh.StoreId);
        }
        public static void Sort(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int n = 0; foreach (var x in ShopJson.List(ShopJson.At(b, "ids")).Take(Max * 2)) Exec(c, null, "UPDATE dbo.ShopPlaces SET Sort=@n WHERE StoreId=@s AND Id=@i", "@n", n++, "@s", sh.StoreId, "@i", ShopJson.Int(x));
        }

        // every place of each size of a product, as the owner typed them (after a count): the total follows
        public static void SetMany(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int pid = ShopJson.Int(ShopJson.At(b, "product")); var d = ShopCache.Get(c, sh); ShopProd p; if (!d.ById.TryGetValue(pid, out p)) throw new ShopException("not_found", "المنتج ده مش موجود.");
            var places = All(c, sh.StoreId).ToDictionary(x => x.Id); string note = ShopJson.Str(ShopJson.At(b, "note"), 120);
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    foreach (var x in ShopJson.List(ShopJson.At(b, "vars")).OfType<Dictionary<string, object>>().Take(Shops.MaxVariants))
                    {
                        int vid = ShopJson.Int(ShopJson.At(x, "v")); if (!p.Vars.Any(v => v.Id == vid)) continue;
                        int before; using (var cmd = Db.Cmd(c, "SELECT Stock FROM dbo.ShopVariants WITH (UPDLOCK) WHERE Id=@v AND StoreId=@s", "@v", vid, "@s", sh.StoreId)) { cmd.Transaction = tx; before = (int)cmd.ExecuteScalar(); }
                        var at = Hold(c, tx, vid); int sum = at.Values.Sum(); if (sum != before && places.Count > 0) Add(at, places.Values.Where(z => !z.Gone).OrderBy(z => z.Sort).ThenBy(z => z.Id).First().Id, before - sum);
                        int up = 0, best = 0;
                        foreach (var kv in ShopJson.Sub(x, "at"))
                        {
                            int place; if (!int.TryParse(kv.Key, out place) || !places.ContainsKey(place)) continue;
                            int q = Math.Max(0, Math.Min(1000000, ShopJson.Int(kv.Value))), had; at.TryGetValue(place, out had);
                            if (q - had > up) { up = q - had; best = place; }
                            at[place] = q;
                        }
                        int total = at.Values.Sum(); Write(c, tx, sh.StoreId, vid, at);
                        if (total == before) continue;
                        Exec(c, tx, "UPDATE dbo.ShopVariants SET Stock=@q WHERE Id=@v; UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@q", total, "@v", vid, "@s", sh.StoreId);
                        Exact = true; Prefer = best;
                        try { ShopOrders.Stock(c, tx, sh.StoreId, pid, vid, total - before, total, "count", null, note.Length > 0 ? note : "الكمية في الأماكن"); }
                        finally { Exact = false; Prefer = 0; }
                    }
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
        }

        // pieces from one place to another: the total doesn't move, so it is its own record (and a line in the stock's history)
        public static void Move(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int pid = ShopJson.Int(ShopJson.At(b, "product")), vid = ShopJson.Int(ShopJson.At(b, "variant")), from = ShopJson.Int(ShopJson.At(b, "from")), to = ShopJson.Int(ShopJson.At(b, "to")), n = ShopJson.Int(ShopJson.At(b, "qty"));
            string note = ShopJson.Str(ShopJson.At(b, "note"), 120);
            var places = All(c, sh.StoreId).ToDictionary(x => x.Id);
            if (!places.ContainsKey(from) || !places.ContainsKey(to) || from == to) throw new ShopException("place", "اختار المكانين.");
            if (places[to].Gone) throw new ShopException("place", "الفرع ده اتشال، انقل لمكان تاني.");
            if (n < 1 || n > 1000000) throw new ShopException("qty", "اكتب الكمية.");
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    int stock; using (var cmd = Db.Cmd(c, "SELECT v.Stock FROM dbo.ShopVariants v WITH (UPDLOCK) WHERE v.Id=@v AND v.ProductId=@p AND v.StoreId=@s", "@v", vid, "@p", pid, "@s", sh.StoreId)) { cmd.Transaction = tx; var o = cmd.ExecuteScalar(); if (o == null) throw new ShopException("not_found", "المقاس ده مش موجود."); stock = (int)o; }
                    var at = Hold(c, tx, vid); int sum = at.Values.Sum(); if (sum != stock) Add(at, places.Values.Where(z => !z.Gone).OrderBy(z => z.Sort).ThenBy(z => z.Id).First().Id, stock - sum);
                    int have; at.TryGetValue(from, out have); if (have < n) throw new ShopException("qty", "في «" + places[from].Name + "» " + Math.Max(0, have) + " بس.");
                    Add(at, from, -n); Add(at, to, n); Write(c, tx, sh.StoreId, vid, at);
                    Exec(c, tx, "INSERT INTO dbo.ShopStockMoves(StoreId, ProductId, VariantId, FromId, ToId, Qty, Note) VALUES(@s, @p, @v, @f, @t, @n, @no)", "@s", sh.StoreId, "@p", pid, "@v", vid, "@f", from, "@t", to, "@n", n, "@no", note.Length == 0 ? null : note);
                    Exec(c, tx, "INSERT INTO dbo.ShopStock(StoreId, ProductId, VariantId, Delta, After_, Reason, Note) VALUES(@s, @p, @v, 0, @a, 'move', @n); UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@s", sh.StoreId, "@p", pid, "@v", vid, "@a", stock, "@n", Cut("نقل " + n + " من " + places[from].Name + " لـ " + places[to].Name, 120));
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
        }
        static string Cut(string s, int n) { return s.Length > n ? s.Substring(0, n) : s; }

        public static object Moves(SqlConnection c, Shop sh)
        {
            var places = All(c, sh.StoreId).ToDictionary(x => x.Id, x => Name(x)); Func<int, string> pn = i => places.ContainsKey(i) ? places[i] : "مكان اتمسح";
            return new Dictionary<string, object> { { "list", Rows(c, null, r => (object)new Dictionary<string, object> { { "name", r["Name"] as string ?? "" }, { "o", ((r["Opts"] as string) ?? "").Replace("|", " / ") }, { "from", pn((int)r["FromId"]) }, { "to", pn((int)r["ToId"]) }, { "q", (int)r["Qty"] }, { "note", r["Note"] as string ?? "" }, { "at", DateTime.SpecifyKind((DateTime)r["At"], DateTimeKind.Utc).ToString("o") } },
                "SELECT TOP 100 m.*, p.Name, v.Opts FROM dbo.ShopStockMoves m LEFT JOIN dbo.ShopProducts p ON p.Id=m.ProductId LEFT JOIN dbo.ShopVariants v ON v.Id=m.VariantId WHERE m.StoreId=@s ORDER BY m.Id DESC", "@s", sh.StoreId) } };
        }

        static Dictionary<int, Dictionary<int, int>> AtOf(SqlConnection c, int storeId, IEnumerable<int> vids)
        {
            var ids = vids.Distinct().ToList(); var d = new Dictionary<int, Dictionary<int, int>>(); if (ids.Count == 0) return d;
            foreach (var x in Rows(c, null, r => new[] { (int)r[0], (int)r[1], (int)r[2] }, "SELECT VariantId, PlaceId, Qty FROM dbo.ShopStockAt WHERE StoreId=@s AND VariantId IN (" + string.Join(",", ids) + ")", "@s", storeId))
            { Dictionary<int, int> m; if (!d.TryGetValue(x[0], out m)) d[x[0]] = m = new Dictionary<int, int>(); m[x[1]] = x[2]; }
            return d;
        }
        static object PlacesJson(List<Place> l) { return l.Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", Name(p) }, { "kind", p.Kind }, { "gone", p.Gone } }).ToArray(); }
        static object VarsJson(ShopProd p, Dictionary<int, Dictionary<int, int>> at) { return p.Vars.Select(v => { Dictionary<int, int> m; at.TryGetValue(v.Id, out m); return new Dictionary<string, object> { { "id", v.Id }, { "o", v.Label }, { "stock", v.Stock }, { "active", v.Active }, { "at", (m ?? new Dictionary<int, int>()).ToDictionary(k => k.Key.ToString(), k => (object)k.Value) } }; }).ToArray(); }

        public static object Of(SqlConnection c, Shop sh, ShopProd p)
        {
            var l = All(c, sh.StoreId); return new Dictionary<string, object> { { "places", PlacesJson(l) }, { "vars", VarsJson(p, AtOf(c, sh.StoreId, p.Vars.Select(v => v.Id))) } };
        }

        // the table: 40 products a page, each size with what every place has
        public static object Table(SqlConnection c, Shop sh, HttpRequest req)
        {
            Sync(c, sh, true);
            var d = ShopCache.Get(c, sh); string q = (req.QueryString["q"] ?? "").Trim(); int page; int.TryParse(req.QueryString["page"], out page); page = Math.Max(0, page);
            IEnumerable<ShopProd> l = d.Products.Where(p => p.Track);
            if (q.Length > 0) { var f = new HashSet<int>(d.Find(q.Length > 80 ? q.Substring(0, 80) : q, 300, true).Select(p => p.Id)); l = l.Where(p => f.Contains(p.Id) || string.Equals(p.Sku, q, StringComparison.OrdinalIgnoreCase) || p.Vars.Any(v => string.Equals(v.Sku, q, StringComparison.OrdinalIgnoreCase))); }
            var all = l.ToList(); var pageL = all.Skip(page * 40).Take(40).ToList(); var at = AtOf(c, sh.StoreId, pageL.SelectMany(p => p.Vars.Select(v => v.Id)));
            return new Dictionary<string, object> { { "places", PlacesJson(All(c, sh.StoreId)) }, { "total", all.Count }, { "page", page }, { "size", 40 }, { "list", pageL.Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "img", p.Pics.Count > 0 ? ShopImages.Url(p.Pics[0].Path, true) : "" }, { "vars", VarsJson(p, at) } }).ToArray() } };
        }

        // the cart's sum: a branch the customer picks up from has to hold what the order takes from the shelf
        public static void Quote(SqlConnection c, ShopData d, ShopQuote q, ShopHow how, bool checkStock)
        {
            var places = All(c, d.Row.StoreId).Where(p => !p.Gone).ToList(); if (places.Count < 2) return;
            var pick = places.Where(p => p.Kind == "b" && ShopPrice.Branch(d.Row, p.Ref) != null).ToList(); if (pick.Count == 0) return;
            var need = q.Lines.Where(l => l.P != null && l.V != null && l.P.Track && l.P.OutMode != "order").GroupBy(l => l.VariantId).ToDictionary(g => g.Key, g => Math.Min(g.Sum(l => l.Qty), Math.Max(0, g.First().V.Stock)));
            var at = AtOf(c, d.Row.StoreId, need.Keys); Func<int, int, int> has = (v, p) => { Dictionary<int, int> m; int x; return at.TryGetValue(v, out m) && m.TryGetValue(p, out x) ? x : 0; };
            var nb = pick.Where(p => need.Any(kv => has(kv.Key, p.Id) < kv.Value)).Select(p => p.Ref).ToList();
            if (nb.Count > 0) (q.Cat ?? (q.Cat = new Dictionary<string, object>()))["nb"] = nb;
            var mine = how == null || string.IsNullOrEmpty(how.Branch) ? null : pick.FirstOrDefault(p => p.Ref == how.Branch);
            if (mine == null || !checkStock) return;
            foreach (var l in q.Lines) { int n; if (l.Err == null && need.TryGetValue(l.VariantId, out n) && has(l.VariantId, mine.Id) < n) l.Err = "branch"; }
        }

        // the owner's order: each line's places
        public static object OfOrder(SqlConnection c, int storeId, long orderId)
        {
            var names = All(c, storeId).ToDictionary(p => p.Id, p => Name(p)); var d = new Dictionary<string, object>();
            foreach (var g in Rows(c, null, r => new[] { (int)r[0], (int)r[1], (int)r[2] }, "SELECT VariantId, PlaceId, Qty FROM dbo.ShopOrderAt WHERE OrderId=@o AND StoreId=@s AND Qty>0", "@o", orderId, "@s", storeId).GroupBy(x => x[0]))
                d[g.Key.ToString()] = g.Select(x => new object[] { names.ContainsKey(x[1]) ? names[x[1]] : "مكان اتمسح", x[2] }).ToArray();
            return d;
        }
        // «قايمة التجهيز»: «خد 2 من المخزن الرئيسي، 1 من فرع مدينة نصر»
        public static void Prep(SqlConnection c, int storeId, List<ShopOrder> os, List<Dictionary<string, object>> rows)
        {
            var places = All(c, storeId); if (places.Count(p => !p.Gone) < 2) return; var names = places.ToDictionary(p => p.Id, p => Name(p));
            var took = Rows(c, null, r => new[] { Convert.ToInt64(r[0]), (int)r[1], (int)r[2], (int)r[3] }, "SELECT OrderId, VariantId, PlaceId, Qty FROM dbo.ShopOrderAt WHERE StoreId=@s AND Qty>0 AND OrderId IN (" + string.Join(",", os.Select(o => o.Id)) + ")", "@s", storeId);
            foreach (var row in rows)
            {
                int v = ShopJson.Int(ShopJson.At(row, "v")); var nos = new HashSet<int>((int[])row["nos"]); var ids = new HashSet<long>(os.Where(o => nos.Contains(o.No)).Select(o => o.Id));
                var by = took.Where(t => t[1] == v && ids.Contains(t[0])).GroupBy(t => (int)t[2]).Select(g => "خد " + g.Sum(t => t[3]) + " من " + (names.ContainsKey(g.Key) ? names[g.Key] : "مكان اتمسح")).ToList();
                if (by.Count > 0) row["at"] = string.Join("، ", by);
            }
        }

        // ---------- Excel: a column per place ----------
        public static string[] XlsHead(SqlConnection c, Shop sh, string[] head)
        {
            if (!Shops.Ready || !ShopGoods.Set(c, sh.StoreId).Places) return head; var l = All(c, sh.StoreId).Where(p => !p.Gone).ToList(); if (l.Count < 2) return head;
            return head.Concat(l.Select(p => "كمية: " + p.Name)).ToArray();
        }
        // the same rows, in the same order ShopExcel writes them (a row per active size, or one for a product without any)
        public static IEnumerable<object[]> XlsRows(SqlConnection c, Shop sh, ShopData d, IEnumerable<object[]> rows)
        {
            if (!Shops.Ready || !ShopGoods.Set(c, sh.StoreId).Places) return rows; var l = All(c, sh.StoreId).Where(p => !p.Gone).ToList(); if (l.Count < 2) return rows;
            var at = AtOf(c, sh.StoreId, d.Products.SelectMany(p => p.Vars.Select(v => v.Id)));
            var vs = d.Products.SelectMany(p => p.Vars.Where(x => x.Active).DefaultIfEmpty(null).Select(v => new { p, v })).ToList();
            return rows.Select((r, i) => { var x = i < vs.Count ? vs[i] : null; Dictionary<int, int> m = null; if (x != null && x.v != null) at.TryGetValue(x.v.Id, out m); return r.Concat(l.Select(p => x == null || x.v == null || !x.p.Track ? (object)"" : (object)(m != null && m.ContainsKey(p.Id) ? m[p.Id] : 0))).ToArray(); });
        }
        // after an Excel file came in: a product's size with a column per place gets exactly those (the total follows them)
        public static int FromXls(SqlConnection c, Shop sh, List<string[]> rows, int head, Func<string[], string, string> cell)
        {
            if (!Shops.Ready || !ShopGoods.Set(c, sh.StoreId).Places || head < 0) return 0; var l = All(c, sh.StoreId).Where(p => !p.Gone).ToList(); if (l.Count < 2) return 0;
            var cols = new Dictionary<int, int>(); var h = rows[head];
            for (int j = 0; j < h.Length; j++) { var t = (h[j] ?? "").Trim(); var p = l.FirstOrDefault(x => t == "كمية: " + x.Name || t == x.Name); if (p != null) cols[j] = p.Id; }
            if (cols.Count == 0) return 0;
            ShopCache.Forget(sh.StoreId); var d = ShopCache.Get(c, sh); int n = 0; string last = null;
            var by = new Dictionary<int, Dictionary<string, object>>();
            foreach (var r in rows.Skip(head + 1))
            {
                if (r.All(string.IsNullOrWhiteSpace)) continue;
                string name = cell(r, "name"); if (name.Length == 0) name = last ?? ""; last = name; if (name.Length < 2) continue;
                var p = d.Products.FirstOrDefault(x => string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); if (p == null || !p.Track) continue;
                var vals = new[] { cell(r, "color").Replace("|", "/"), cell(r, "size").Replace("|", "/") }.Where(x => x.Length > 0).ToList();
                var v = p.Vars.FirstOrDefault(x => x.Vals.Length == vals.Count && vals.All(x.Vals.Contains)) ?? (p.Vars.Count == 1 ? p.Vars[0] : null); if (v == null) continue;
                var at = new Dictionary<string, object>(); foreach (var kv in cols) { int q; if (kv.Key < r.Length && int.TryParse(ShopJson.Digits((r[kv.Key] ?? "").Trim()), out q)) at[kv.Value.ToString()] = Math.Max(0, q); }
                if (at.Count == 0) continue;
                Dictionary<string, object> one; if (!by.TryGetValue(p.Id, out one)) by[p.Id] = one = new Dictionary<string, object> { { "product", p.Id }, { "vars", new List<object>() }, { "note", "Excel" } };
                ((List<object>)one["vars"]).Add(new Dictionary<string, object> { { "v", v.Id }, { "at", at } }); n++;
            }
            foreach (var x in by.Values) SetMany(c, sh, x);
            return n;
        }
    }
}
