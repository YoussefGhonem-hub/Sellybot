// After delivery: returns and exchanges (a customer asks from their account or their order's link, the owner decides, the quantity comes
// back when the owner says the pieces arrived) and reviews (only from someone who received the product; the owner may hide or answer one,
// never write or change it). One message asks for a review two days after delivery.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class ShopReturns
    {
        public static readonly string[] Statuses = { "new", "approved", "rejected", "received", "done", "cancelled" };
        static readonly Dictionary<string, string[]> Reasons = new Dictionary<string, string[]> { { "size", new[] { "المقاس مش مظبوط", "Wrong size" } }, { "damaged", new[] { "وصل فيه عيب", "Arrived damaged" } }, { "wrong", new[] { "وصل منتج غير اللي طلبته", "Wrong item" } }, { "notlike", new[] { "مش زي الصورة أو الوصف", "Not as described" } }, { "late", new[] { "اتأخر", "Too late" } }, { "mind", new[] { "غيّرت رأيي", "Changed my mind" } }, { "other", new[] { "سبب تاني", "Another reason" } } };
        public static string ReasonText(string k, bool en = false) { string[] t; return Reasons.TryGetValue(k ?? "", out t) ? t[en ? 1 : 0] : k; }
        public static string StatusText(string s, bool en = false)
        {
            switch (s) { case "new": return en ? "Waiting for the store" : "مستني رد المتجر"; case "approved": return en ? "Approved" : "اتوافق عليه"; case "rejected": return en ? "Declined" : "اترفض"; case "received": return en ? "Received by the store" : "وصل المتجر"; case "done": return en ? "Done" : "خلص"; default: return en ? "Cancelled" : "اتلغى"; }
        }
        static string T(ShopFrontHandler.Ctx x, string ar, string en) { return x.Lang == "en" ? en : ar; }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        static void Exec(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } }

        // how many of each line may still be asked back: bought, less what came back and what an open request holds
        public static Dictionary<string, object> Left(SqlConnection c, ShopOrder o)
        {
            var held = new Dictionary<long, int>();
            using (var cmd = Db.Cmd(c, "SELECT Items FROM dbo.ShopReturns WHERE OrderId=@o AND Status IN ('new','approved')", "@o", o.Id)) using (var r = cmd.ExecuteReader())
                while (r.Read()) foreach (var i in ShopJson.Arr((string)r[0]).OfType<Dictionary<string, object>>()) { long id = ShopJson.Int(ShopJson.At(i, "i")); int n; held.TryGetValue(id, out n); held[id] = n + ShopJson.Int(ShopJson.At(i, "q")); }
            var d = new Dictionary<string, object>();
            foreach (var i in o.Items) { int h; held.TryGetValue(i.Id, out h); int n = i.Qty - i.Returned - h; if (n > 0) d[i.Id.ToString(CultureInfo.InvariantCulture)] = n; }
            return d;
        }

        public static void Ask(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, ShopOrder o, HttpResponse res)
        {
            if (!ShopAccount.CanReturn(x.Row, o)) throw new ShopException("late", T(x, "مدة الاسترجاع عدّت أو الطلب لسه ماوصلش. كلّم المتجر.", "The return period has passed or it hasn't been delivered yet. Message the store."));
            if (!Web.Allow("shop-ret:" + x.Ip, 10, TimeSpan.FromHours(1))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            string kind = ShopJson.Str(ShopJson.At(b, "kind"), 8) == "exchange" ? "exchange" : "return", reason = ShopJson.Str(ShopJson.At(b, "reason"), 12), note = Regex.Replace(ShopJson.Str(ShopJson.At(b, "note"), 500), @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", "");
            if (!Reasons.ContainsKey(reason)) throw new ShopException("reason", T(x, "اختار السبب.", "Choose the reason."));
            if (reason == "other" && note.Length < 3) throw new ShopException("note", T(x, "اكتب السبب.", "Write the reason."));
            var left = Left(c, o); var items = new List<object>(); var names = new List<string>();
            foreach (var i in ShopJson.List(ShopJson.At(b, "items")).OfType<Dictionary<string, object>>())
            {
                long id = ShopJson.Int(ShopJson.At(i, "i")); int q = ShopJson.Int(ShopJson.At(i, "q")); var it = o.Items.FirstOrDefault(y => y.Id == id); object n;
                if (it == null || q <= 0 || !left.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out n) || items.Any(y => (long)((Dictionary<string, object>)y)["i"] == id)) continue;
                q = Math.Min(q, (int)n); var one = new Dictionary<string, object> { { "i", id }, { "q", q }, { "n", it.Name }, { "o", it.Opts ?? "" }, { "v", it.VariantId }, { "p", it.ProductId }, { "im", it.Image ?? "" }, { "t", Math.Round(it.Sum / it.Qty * q, 2) } };
                if (kind == "exchange")
                {
                    ShopVar v; int to = ShopJson.Int(ShopJson.At(i, "to"));
                    if (to > 0 && to != it.VariantId && x.Data.Vars.TryGetValue(to, out v) && v.ProductId == it.ProductId && x.Data.ById[v.ProductId].Can(v)) { one["to"] = to; one["toText"] = v.Label; }
                }
                items.Add(one); names.Add(it.Name + (string.IsNullOrEmpty(it.Opts) ? "" : " (" + it.Opts + ")") + " × " + q);
            }
            if (items.Count == 0) throw new ShopException("items", T(x, "اختار المنتج اللي عايز ترجّعه.", "Choose what you want to send back."));
            var pics = ShopAccount.Pics(c, x.Row.StoreId, ShopJson.At(b, "pics"), 4); int rid;
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopReturns(StoreId, OrderId, OrderNo, Phone, Kind, Reason, Note, Items, Pics) OUTPUT INSERTED.Id VALUES(@s, @o, @no, @p, @k, @r, @n, @i, @pics)", "@s", x.Row.StoreId, "@o", o.Id, "@no", o.No, "@p", o.Phone, "@k", kind, "@r", reason, "@n", Nz(note), "@i", ShopJson.Write(items), "@pics", pics.Count == 0 ? null : string.Join(",", pics))) rid = (int)cmd.ExecuteScalar();
            ShopOrders.Log_(c, null, x.Row.StoreId, o.Id, "customer", (kind == "exchange" ? "طلب استبدال: " : "طلب استرجاع: ") + string.Join("، ", names) + "، السبب: " + ReasonText(reason));
            ShopHooks.Tell(x.Row.StoreId, "return", (kind == "exchange" ? "🔁 *طلب استبدال* من أوردر " : "↩️ *طلب استرجاع* من أوردر ") + o.NoText + "\n" + o.Name + " · " + ShopOrders.Local(o.Phone) + "\n" + string.Join("\n", names.Select(n => "• " + n)) + "\nالسبب: " + ReasonText(reason) + (note.Length > 0 ? "\n" + note : "") + "\n\nرد عليه من «متجري» ثم «الطلبات» ثم «المرتجعات».", "sb-shop-ret-" + rid);
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "id", rid }, { "returns", OfOrder(c, o.Id) } });
        }
        public static void CustomerOff(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, ShopOrder o, HttpResponse res)
        {
            Exec(c, null, "UPDATE dbo.ShopReturns SET Status='cancelled', At=SYSUTCDATETIME() WHERE Id=@i AND OrderId=@o AND StoreId=@s AND Status='new'", "@i", ShopJson.Int(ShopJson.At(b, "id")), "@o", o.Id, "@s", x.Row.StoreId);
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "returns", OfOrder(c, o.Id) } });
        }

        static Dictionary<string, object> Row(System.Data.IDataRecord r, bool owner)
        {
            var d = new Dictionary<string, object> { { "id", (int)r["Id"] }, { "no", (int)r["OrderNo"] }, { "kind", (string)r["Kind"] }, { "st", (string)r["Status"] }, { "reason", (string)r["Reason"] }, { "note", r["Note"] as string ?? "" }, { "items", ShopJson.Arr((string)r["Items"]) }, { "pics", (r["Pics"] as string ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) },
                { "reply", r["OwnerNote"] as string ?? "" }, { "refund", r["Refund"] is decimal ? r["Refund"] : null }, { "at", ShopView.Unix(DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc)) }, { "stAt", ShopView.Unix(DateTime.SpecifyKind((DateTime)r["At"], DateTimeKind.Utc)) } };
            if (owner) { d["order"] = Convert.ToInt64(r["OrderId"]); d["phone"] = ShopOrders.Local((string)r["Phone"]); d["restocked"] = (bool)r["Restocked"]; d["why"] = ReasonText((string)r["Reason"]); }
            return d;
        }
        static List<Dictionary<string, object>> Rows(SqlConnection c, bool owner, string where, params object[] a) { var l = new List<Dictionary<string, object>>(); using (var cmd = Db.Cmd(c, "SELECT TOP 200 * FROM dbo.ShopReturns WHERE " + where + " ORDER BY Id DESC", a)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(Row(r, owner)); return l; }
        public static object OfOrder(SqlConnection c, long orderId) { return Rows(c, false, "OrderId=@o", "@o", orderId); }
        public static object OfCustomer(SqlConnection c, ShopFrontHandler.Ctx x, string phone) { return Rows(c, false, "StoreId=@s AND Phone=@p", "@s", x.Row.StoreId, "@p", phone); }
        public static int Waiting(SqlConnection c, int storeId) { using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopReturns WHERE StoreId=@s AND Status='new'", "@s", storeId)) return (int)cmd.ExecuteScalar(); }

        // ---------- the owner ----------
        public static object List(SqlConnection c, Shop sh, string st)
        {
            var l = Statuses.Contains(st) ? Rows(c, true, "StoreId=@s AND Status=@st", "@s", sh.StoreId, "@st", st) : Rows(c, true, "StoreId=@s", "@s", sh.StoreId);
            foreach (var d in l) { var o = ShopOrders.ById(c, sh.StoreId, (long)d["order"]); d["name"] = o == null ? "" : o.Name; d["total"] = o == null ? 0 : o.Total; d["paid"] = o == null ? "" : o.Pay; }
            return new Dictionary<string, object> { { "list", l }, { "waiting", Waiting(c, sh.StoreId) }, { "reasons", Stats(c, sh.StoreId) } };
        }
        // «أسباب المرتجع»: the last 90 days, by reason and by product
        static object Stats(SqlConnection c, int storeId)
        {
            var by = new Dictionary<string, int>(); var prod = new Dictionary<string, int>();
            using (var cmd = Db.Cmd(c, "SELECT Reason, Items FROM dbo.ShopReturns WHERE StoreId=@s AND Status NOT IN ('cancelled','rejected') AND CreatedAt>DATEADD(DAY,-90,SYSUTCDATETIME())", "@s", storeId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) { int n; var k = ReasonText((string)r[0]); by.TryGetValue(k, out n); by[k] = n + 1; foreach (var i in ShopJson.Arr((string)r[1]).OfType<Dictionary<string, object>>()) { var p = ShopJson.Str(ShopJson.At(i, "n"), 150); prod.TryGetValue(p, out n); prod[p] = n + ShopJson.Int(ShopJson.At(i, "q")); } }
            return new Dictionary<string, object> { { "by", by.OrderByDescending(k => k.Value).Select(k => new object[] { k.Key, k.Value }).ToArray() }, { "prod", prod.OrderByDescending(k => k.Value).Take(8).Select(k => new object[] { k.Key, k.Value }).ToArray() } };
        }
        // approve / reject / received (the pieces are back: quantity and money) / done
        public static void Do(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            int id = ShopJson.Int(ShopJson.At(b, "id")); string to = ShopJson.Str(ShopJson.At(b, "st"), 10), note = ShopJson.Str(ShopJson.At(b, "note"), 300);
            if (!Statuses.Contains(to) || to == "new" || to == "cancelled") throw new ShopException("st", "الحالة دي مش معروفة.");
            var l = Rows(c, true, "StoreId=@s AND Id=@i", "@s", sh.StoreId, "@i", id); if (l.Count == 0) throw new ShopException("gone", "الطلب ده مش موجود.");
            var r = l[0]; string from = (string)r["st"]; long oid = (long)r["order"]; var o = ShopOrders.ById(c, sh.StoreId, oid); if (o == null) throw new ShopException("gone", "الأوردر ده مش موجود.");
            bool okMove = (from == "new" && (to == "approved" || to == "rejected" || to == "received")) || (from == "approved" && (to == "received" || to == "rejected")) || (from == "received" && to == "done") || (from == "rejected" && to == "approved");
            if (!okMove) throw new ShopException("move", "الطلب ده «" + StatusText(from) + "»، ماينفعش يتحوّل لـ«" + StatusText(to) + "».");
            if (to == "rejected" && note.Length < 3) throw new ShopException("note", "اكتب سبب الرفض، الزبون هيشوفه.");
            var items = ShopJson.List(r["items"]).OfType<Dictionary<string, object>>().ToList(); bool restock = ShopJson.Bool(ShopJson.At(b, "restock"), true); decimal refund = Math.Max(0, Math.Min(Math.Max(0, o.Total - o.Refund), ShopJson.DecN(ShopJson.At(b, "refund")) ?? items.Sum(i => ShopJson.Dec(ShopJson.At(i, "t")))));
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopReturns SET Status=@t, OwnerNote=ISNULL(@n, OwnerNote), Refund=CASE WHEN @t='received' THEN @rf ELSE Refund END, Restocked=CASE WHEN @t='received' THEN @rs ELSE Restocked END, At=SYSUTCDATETIME() WHERE Id=@i AND StoreId=@s AND Status=@f", "@t", to, "@n", Nz(note), "@rf", (string)r["kind"] == "return" ? (object)refund : DBNull.Value, "@rs", restock, "@i", id, "@s", sh.StoreId, "@f", from))
                    { cmd.Transaction = tx; if (cmd.ExecuteNonQuery() == 0) { tx.Rollback(); throw new ShopException("busy", "الطلب اتغيّر حالًا. حدّث الصفحة."); } }
                    if (to == "received")
                    {
                        foreach (var i in items)
                        {
                            int q = ShopJson.Int(ShopJson.At(i, "q")), vid = ShopJson.Int(ShopJson.At(i, "v")), pid = ShopJson.Int(ShopJson.At(i, "p"));
                            // a piece comes back once, however many requests hold it
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrderItems SET Returned=CASE WHEN Returned+@q>Qty THEN Qty ELSE Returned+@q END OUTPUT INSERTED.Returned-DELETED.Returned WHERE Id=@i AND OrderId=@o", "@q", q, "@i", ShopJson.Int(ShopJson.At(i, "i")), "@o", oid)) { cmd.Transaction = tx; var got = cmd.ExecuteScalar(); q = got == null ? 0 : (int)got; }
                            if (!restock || q <= 0) continue;
                            using (var cmd = Db.Cmd(c, "UPDATE v SET Stock=v.Stock+@q OUTPUT INSERTED.Stock FROM dbo.ShopVariants v JOIN dbo.ShopProducts p ON p.Id=v.ProductId WHERE v.Id=@v AND v.StoreId=@s AND p.Track=1", "@q", q, "@v", vid, "@s", sh.StoreId)) { cmd.Transaction = tx; var left = cmd.ExecuteScalar(); if (left != null) ShopOrders.Stock(c, tx, sh.StoreId, pid, vid, q, (int)left, "return", oid, "مرتجع"); }
                            Exec(c, tx, "UPDATE dbo.ShopProducts SET Sold=CASE WHEN Sold-@q>0 THEN Sold-@q ELSE 0 END WHERE Id=@p AND StoreId=@s", "@q", q, "@p", pid, "@s", sh.StoreId);
                        }
                        if ((string)r["kind"] == "return") Exec(c, tx, "UPDATE dbo.ShopOrders SET Refund=Refund+@m WHERE Id=@o AND StoreId=@s", "@m", refund, "@o", oid, "@s", sh.StoreId);
                        Exec(c, tx, "UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@s", sh.StoreId);
                    }
                    ShopOrders.Log_(c, tx, sh.StoreId, oid, "owner", ((string)r["kind"] == "exchange" ? "الاستبدال: " : "الاسترجاع: ") + StatusText(to) + (to == "received" ? (restock ? "، والكمية رجعت" : "، من غير ما الكمية ترجع") : "") + (note.Length > 0 ? "، " + note : ""));
                    tx.Commit();
                }
                catch (ShopException) { throw; }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            // everything came back and nothing replaces it: the order itself is returned (its quantity is already back)
            if (to == "received" && (string)r["kind"] == "return") { var o2 = ShopOrders.ById(c, sh.StoreId, oid); if (o2 != null && o2.Status == "delivered" && o2.Items.All(i => i.Returned >= i.Qty)) ShopOrders.SetStatus(c, sh.StoreId, oid, "returned", "owner", ReasonText((string)r["reason"]), false); }
            if (ShopJson.Bool(ShopJson.At(b, "tell"), true) && (to == "approved" || to == "rejected" || to == "done")) Notify(sh.StoreId, id, oid, (string)r["kind"], to, note);
        }
        static void Notify(int storeId, int id, long orderId, string kind, string to, string note)
        {
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    using (var c = Db.Open())
                    {
                        var s = Db.StoreById(c, storeId); var sh = Shops.ByStore(c, storeId); var o = ShopOrders.ById(c, storeId, orderId); if (s == null || sh == null || o == null || o.Phone.Length == 0) return;
                        bool en = o.Lang == "en", ex = kind == "exchange"; string name = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name;
                        string t = en ? "*" + name + "*\nYour " + (ex ? "exchange" : "return") + " request for order " + o.NoText + ": *" + StatusText(to, true).ToLowerInvariant() + "*." : "*" + name + "*\nطلب " + (ex ? "الاستبدال" : "الاسترجاع") + " لأوردر " + o.NoText + ": *" + StatusText(to) + "*.";
                        if (note.Length > 0) t += "\n" + note;
                        await WaNotify.ToCustomer(c, s, o.Phone, t, "sb-shop-rts-" + id + "-" + to);
                    }
                }
                catch (Exception ex2) { Log.Error(ex2); }
            });
        }
    }

    public static class ShopReviews
    {
        static string T(ShopFrontHandler.Ctx x, string ar, string en) { return x.Lang == "en" ? en : ar; }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        // a link, a mobile or an email in a review waits for the owner
        static bool Odd(string t) { return Regex.IsMatch(ShopJson.Digits(t ?? ""), @"https?:|www\.|\.com|@|\d{8,}", RegexOptions.IgnoreCase); }

        public static object Rated(SqlConnection c, long orderId)
        {
            var d = new Dictionary<string, object>();
            using (var cmd = Db.Cmd(c, "SELECT ProductId, Stars, Text, Status FROM dbo.ShopReviews WHERE OrderId=@o", "@o", orderId)) using (var r = cmd.ExecuteReader()) while (r.Read()) d[((int)r[0]).ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, object> { { "r", (int)r[1] }, { "t", r[2] as string ?? "" }, { "st", (string)r[3] } };
            return d;
        }
        public static void Add(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, ShopOrder o, HttpResponse res)
        {
            if (!ShopAccount.ReviewsOn(x.Row)) throw new ShopException("off", T(x, "التقييمات مقفولة.", "Reviews are off."));
            if (o.Status != "delivered" && !(o.Status == "returned" && o.DeliveredAt.HasValue)) throw new ShopException("early", T(x, "تقدر تقيّم بعد ما الطلب يوصلك.", "You can rate it once it's delivered."));
            if (!Web.Allow("shop-rev:" + x.Ip, 30, TimeSpan.FromHours(1))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            int pid = ShopJson.Int(ShopJson.At(b, "p")), stars = ShopJson.Int(ShopJson.At(b, "r")); var it = o.Items.FirstOrDefault(i => i.ProductId == pid);
            if (it == null) throw new ShopException("not_yours", T(x, "المنتج ده مش في الطلب.", "This product isn't in the order."));
            if (stars < 1 || stars > 5) throw new ShopException("stars", T(x, "اختار عدد النجوم.", "Choose how many stars."));
            string text = Regex.Replace(ShopJson.Str(ShopJson.At(b, "t"), 1000), @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", ""), name = Regex.Replace(ShopJson.Str(ShopJson.At(b, "name"), 40), @"[\u0000-\u001F<>]", "").Trim();
            if (name.Length < 2) name = o.Name.Trim().Split(' ')[0]; if (name.Length > 60) name = name.Substring(0, 60);
            var pics = ShopJson.Bool(ShopJson.At(b, "agree")) ? ShopAccount.Pics(c, x.Row.StoreId, ShopJson.At(b, "pics"), 4) : new List<string>();
            bool auto = ShopJson.Bool(ShopJson.At(ShopJson.Sub(x.Row.Settings, "reviews"), "auto"), true) && pics.Count == 0 && !Odd(text) && !Odd(name);
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.ShopReviews SET Stars=@r, Text=@t, Name=@n, Pics=ISNULL(@pics, Pics), Status=CASE WHEN Status='hidden' THEN 'hidden' ELSE @st END, ShownAt=CASE WHEN @st='shown' THEN SYSUTCDATETIME() ELSE ShownAt END WHERE OrderId=@o AND ProductId=@p AND StoreId=@s;
                IF @@ROWCOUNT=0 INSERT INTO dbo.ShopReviews(StoreId, ProductId, OrderId, Phone, Name, Opts, Stars, Text, Pics, Status, ShownAt) VALUES(@s, @p, @o, @ph, @n, @op, @r, @t, @pics, @st, CASE WHEN @st='shown' THEN SYSUTCDATETIME() END)",
                "@s", x.Row.StoreId, "@p", pid, "@o", o.Id, "@ph", o.Phone, "@n", name, "@op", Nz(it.Opts), "@r", stars, "@t", Nz(text), "@pics", pics.Count == 0 ? null : string.Join(",", pics), "@st", auto ? "shown" : "new")) cmd.ExecuteNonQuery();
            if (auto) Shops.Bump(c, x.Row.StoreId);
            if (stars <= 2) ShopHooks.Tell(x.Row.StoreId, "review", "⭐ *تقييم " + stars + " من 5* على «" + it.Name + "»\n" + o.Name + " · " + ShopOrders.Local(o.Phone) + " · أوردر " + o.NoText + (text.Length > 0 ? "\n«" + (text.Length > 300 ? text.Substring(0, 300) + "…" : text) + "»" : "") + "\n\nكلّم الزبون وحل المشكلة.", "sb-shop-rev-" + o.Id + "-" + pid);
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "shown", auto }, { "rated", Rated(c, o.Id) } });
        }

        // the catalog's numbers: average, how many, and how many of each star
        public static void Load(SqlConnection c, ShopData d)
        {
            using (var cmd = Db.Cmd(c, "SELECT ProductId, Stars, COUNT(*) FROM dbo.ShopReviews WHERE StoreId=@s AND Status='shown' GROUP BY ProductId, Stars", "@s", d.Row.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) { ShopProd p; if (!d.ById.TryGetValue((int)r[0], out p)) continue; if (p.Stars == null) p.Stars = new int[5]; int s = (int)r[1]; if (s >= 1 && s <= 5) p.Stars[s - 1] = (int)r[2]; }
            // the home page's row: the latest with something written, from four stars (shown as «من آراء زباينا»)
            using (var cmd = Db.Cmd(c, "SELECT TOP 12 ProductId, Name, Stars, Text, CreatedAt FROM dbo.ShopReviews WHERE StoreId=@s AND Status='shown' AND Stars>=4 AND LEN(Text)>=8 ORDER BY Id DESC", "@s", d.Row.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) if (d.ById.ContainsKey((int)r[0])) d.Revs.Add(new Dictionary<string, object> { { "p", (int)r[0] }, { "n", (string)r[1] }, { "r", (int)r[2] }, { "t", (string)r[3] }, { "at", ShopView.Unix(DateTime.SpecifyKind((DateTime)r[4], DateTimeKind.Utc)) } });
        }
        // «صور زباينا»: the pictures of shown reviews (the customer agreed to it when they sent them)
        public static void LoadPics(SqlConnection c, ShopData d)
        {
            using (var cmd = Db.Cmd(c, "SELECT TOP 16 ProductId, Name, Pics FROM dbo.ShopReviews WHERE StoreId=@s AND Status='shown' AND Stars>=4 AND Pics IS NOT NULL ORDER BY Id DESC", "@s", d.Row.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read() && d.Ugc.Count < 16) foreach (var p in ((string)r[2]).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Take(2)) if (d.ById.ContainsKey((int)r[0])) d.Ugc.Add(new Dictionary<string, object> { { "im", p }, { "p", (int)r[0] }, { "n", (string)r[1] } });
        }
        // /_d/rv-<product>-<page>-<version>.json
        public static Dictionary<string, object> Page(SqlConnection c, int storeId, int productId, int page)
        {
            var l = new List<object>(); int n = 0;
            using (var cmd = Db.Cmd(c, "SELECT Name, Opts, Stars, Text, Pics, Reply, CreatedAt FROM dbo.ShopReviews WHERE StoreId=@s AND ProductId=@p AND Status='shown' ORDER BY Id DESC OFFSET @o ROWS FETCH NEXT 11 ROWS ONLY", "@s", storeId, "@p", productId, "@o", Math.Max(0, page - 1) * 10)) using (var r = cmd.ExecuteReader())
                while (r.Read()) { if (++n > 10) break; l.Add(new Dictionary<string, object> { { "n", (string)r[0] }, { "o", r[1] as string ?? "" }, { "r", (int)r[2] }, { "t", r[3] as string ?? "" }, { "im", (r[4] as string ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) }, { "re", r[5] as string ?? "" }, { "at", ShopView.Unix(DateTime.SpecifyKind((DateTime)r[6], DateTimeKind.Utc)) } }); }
            return new Dictionary<string, object> { { "l", l }, { "more", n > 10 } };
        }

        // ---------- the owner ----------
        public static int Waiting(SqlConnection c, int storeId) { using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopReviews WHERE StoreId=@s AND Status='new'", "@s", storeId)) return (int)cmd.ExecuteScalar(); }
        public static object List(SqlConnection c, Shop sh, string st)
        {
            var l = new List<object>(); var d = ShopCache.Get(c, sh);
            using (var cmd = Db.Cmd(c, "SELECT TOP 200 r.*, o.No AS No FROM dbo.ShopReviews r LEFT JOIN dbo.ShopOrders o ON o.Id=r.OrderId WHERE r.StoreId=@s AND (@st='' OR r.Status=@st) ORDER BY CASE WHEN r.Status='new' THEN 0 ELSE 1 END, r.Id DESC", "@s", sh.StoreId, "@st", st == "new" || st == "shown" || st == "hidden" ? st : "")) using (var r = cmd.ExecuteReader())
                while (r.Read()) { ShopProd p; d.ById.TryGetValue((int)r["ProductId"], out p); l.Add(new Dictionary<string, object> { { "id", (int)r["Id"] }, { "product", (int)r["ProductId"] }, { "pn", p == null ? "منتج اتمسح" : p.Name }, { "im", p == null || p.Pics.Count == 0 ? "" : p.Pics[0].Path }, { "name", (string)r["Name"] }, { "opts", r["Opts"] as string ?? "" }, { "r", (int)r["Stars"] }, { "t", r["Text"] as string ?? "" },
                    { "pics", (r["Pics"] as string ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) }, { "st", (string)r["Status"] }, { "reply", r["Reply"] as string ?? "" }, { "no", r["No"] is int ? r["No"] : 0 }, { "order", Convert.ToInt64(r["OrderId"]) }, { "at", ShopView.Unix(DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc)) } }); }
            double avg = 0; int n = 0; using (var cmd = Db.Cmd(c, "SELECT COUNT(*), AVG(CAST(Stars AS FLOAT)) FROM dbo.ShopReviews WHERE StoreId=@s AND Status='shown'", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) { r.Read(); n = (int)r[0]; avg = r[1] is double ? (double)r[1] : 0; }
            return new Dictionary<string, object> { { "list", l }, { "n", n }, { "avg", Math.Round(avg, 1) }, { "waiting", Waiting(c, sh.StoreId) } };
        }
        public static void Do(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int id = ShopJson.Int(ShopJson.At(b, "id")); string st = ShopJson.Str(ShopJson.At(b, "st"), 8);
            if (st == "shown" || st == "hidden") using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopReviews SET Status=@t, ShownAt=CASE WHEN @t='shown' THEN SYSUTCDATETIME() ELSE ShownAt END WHERE Id=@i AND StoreId=@s", "@t", st, "@i", id, "@s", sh.StoreId)) cmd.ExecuteNonQuery();
            if (b.ContainsKey("reply")) using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopReviews SET Reply=@r WHERE Id=@i AND StoreId=@s", "@r", Nz(ShopJson.Str(ShopJson.At(b, "reply"), 500)), "@i", id, "@s", sh.StoreId)) cmd.ExecuteNonQuery();
            Shops.Bump(c, sh.StoreId);
        }

        // ---------- «طلب تقييم بعد الاستلام»: once, two days after delivery, by day (Jobs) ----------
        public static async Task Asks(SqlConnection c, CancellationToken ct)
        {
            if (!Shops.Ready || WaNotify.QuietUntil().HasValue) return;
            var due = new List<KeyValuePair<int, long>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 40 StoreId, Id FROM dbo.ShopOrders WHERE Status='delivered' AND RateAskedAt IS NULL AND Phone<>'' AND DeliveredAt<DATEADD(DAY,-2,SYSUTCDATETIME()) AND DeliveredAt>DATEADD(DAY,-9,SYSUTCDATETIME()) ORDER BY DeliveredAt")) using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new KeyValuePair<int, long>((int)r[0], Convert.ToInt64(r[1])));
            foreach (var g in due.GroupBy(k => k.Key))
            {
                var s = Db.StoreById(c, g.Key); var sh = Shops.ByStore(c, g.Key); if (s == null || sh == null) continue;
                bool on = Shops.Open(c, s, sh) && ShopAccount.ReviewsOn(sh) && ShopJson.Bool(ShopJson.At(ShopJson.Sub(sh.Settings, "reviews"), "ask"), true) && WaNotify.CantSend(c, s) == null && !Billing.Expired(s);
                foreach (var k in g)
                {
                    if (ct.IsCancellationRequested) return;
                    // written first: a message is never sent twice, whatever happens after
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET RateAskedAt=SYSUTCDATETIME() WHERE Id=@i AND RateAskedAt IS NULL", "@i", k.Value)) if (cmd.ExecuteNonQuery() == 0) continue;
                    if (!on) continue;
                    var o = ShopOrders.ById(c, g.Key, k.Value); if (o == null || WaOptOut.Has(c, s.Id, o.Phone) || !Web.Allow("shop-ask:" + s.Id, 60, TimeSpan.FromHours(1))) continue;
                    bool en = o.Lang == "en"; string name = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name, first = o.Name.Trim().Split(' ')[0], link = sh.Url + (en && ShopJson.Str(ShopJson.At(sh.Look(false), "langs")) == "both" && ShopJson.Str(ShopJson.At(sh.Look(false), "first")) != "en" ? "/en" : "") + "/rate/" + o.Token;
                    var why = await WaNotify.ToCustomer(c, s, o.Phone, en ? "Hi " + first + " 👋\nHow was your order " + o.NoText + " from *" + name + "*? Your rating takes half a minute and helps other customers:\n" + link + "\n\n_Reply “stop” if you don't want such messages._" : "أهلًا " + first + " 👋\nإيه رأيك في أوردرك " + o.NoText + " من *" + name + "*؟ تقييمك بياخد نص دقيقة وبيساعد غيرك يختار:\n" + link + "\n\n_لو مش عايز رسايل زي دي، ابعت «وقف»._", "sb-shop-rate-" + o.Id);
                    if (why == null) ShopOrders.Log_(c, null, s.Id, o.Id, "system", "اتبعتله طلب تقييم على الواتساب");
                }
            }
        }
    }

    public static partial class ShopHooks
    {
        // a short WhatsApp to the owner about something a customer did (kind = its switch in the settings: alerts.<kind>, on by default)
        public static void Tell(int storeId, string kind, string text, string idem)
        {
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    using (var c = Db.Open())
                    {
                        var s = Db.StoreById(c, storeId); var sh = Shops.ByStore(c, storeId); if (s == null || sh == null || string.IsNullOrEmpty(s.OwnerPhone)) return;
                        var al = ShopJson.Sub(sh.Settings, "alerts"); if (!ShopJson.Bool(ShopJson.At(al, "wa"), true) || !ShopJson.Bool(ShopJson.At(al, kind), true)) return;
                        if (Web.Allow("shop-tell:" + storeId, 60, TimeSpan.FromHours(1))) await WaNotify.ToOwner(c, s, text, idem);
                    }
                }
                catch (Exception ex) { Log.Error(ex); }
            });
        }
    }
}
