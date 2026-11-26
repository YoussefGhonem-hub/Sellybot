// The owner's side of the second part of the plan (api/shop.ashx passes here what ShopOwner doesn't know): returns, reviews, transfers,
// an order taken by phone, merging two orders, a partial delivery, the preparation list, what is printed (the parcel's label, the invoice,
// price tags with a barcode), the blog, customers, the reports (sales, profit, where customers stop, what they searched for) and a copy of
// the store to keep. Every query has the store's id in it.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class ShopDesk
    {
        static string Str(Dictionary<string, object> b, string k, int max = 200) { return ShopJson.Str(ShopJson.At(b, k), max); }
        static int Int(Dictionary<string, object> b, string k, int d = 0) { return ShopJson.Int(ShopJson.At(b, k), d); }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        static string H(string s) { return HttpUtility.HtmlEncode(s ?? ""); }
        static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        static int Num(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v); } }
        static List<T> Rows<T>(SqlConnection c, Func<SqlDataReader, T> f, string sql, params object[] a) { var l = new List<T>(); using (var cmd = Db.Cmd(c, sql, a)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(f(r)); return l; }
        static long Unix(object v) { return v is DateTime ? ShopView.Unix(DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc)) : 0; }
        static void Ok(HttpResponse res, params object[] kv) { var d = new Dictionary<string, object> { { "ok", true } }; for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; Web.Json(res, 200, d); }
        static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }

        // what waits for the owner, for the tabs' dots
        public static Dictionary<string, object> Waiting(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT (SELECT COUNT(*) FROM dbo.ShopReturns WHERE StoreId=@s AND Status='new'), (SELECT COUNT(*) FROM dbo.ShopReviews WHERE StoreId=@s AND Status='new'), (SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND PayState='sent' AND Status NOT IN ('cancelled','returned')), (SELECT COUNT(*) FROM dbo.ShopCustomers WHERE StoreId=@s AND WipeAskedAt IS NOT NULL)", "@s", storeId)) using (var r = cmd.ExecuteReader()) { r.Read(); return D("returns", (int)r[0], "reviews", (int)r[1], "pays", (int)r[2], "wipes", (int)r[3]); }
        }

        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, int id, HttpRequest req, HttpResponse res)
        {
            switch (a)
            {
                case "returns": Web.Json(res, 200, ShopReturns.List(c, sh, req.QueryString["st"] ?? "")); return true;
                case "reviews": Web.Json(res, 200, ShopReviews.List(c, sh, req.QueryString["st"] ?? "")); return true;
                case "posts": Web.Json(res, 200, D("list", Posts(c, sh))); return true;
                case "post": { var p = Post_(c, sh, id); if (p == null) Web.Error(res, 404, "not_found", "المقال ده مش موجود."); else Web.Json(res, 200, p); return true; }
                case "report": Web.Json(res, 200, Report(c, sh, req)); return true;
                case "report-xls": ReportXls(c, sh, req, res); return true;
                case "customers": Web.Json(res, 200, Customers(c, sh, req)); return true;
                case "customers-xls": CustomersXls(c, sh, res); return true;
                case "prep": Web.Json(res, 200, Prep(c, sh)); return true;
                case "print": Print(c, s, sh, req, res); return true;
                case "backup": Backup(c, sh, res); return true;
            }
            if (ShopApiHooks.Get(c, s, sh, a, req, res)) return true;   // «ربط ببرامج تانية», «القياس من السيرفر»
            if (ShopLookMore.Get(c, s, sh, a, req, res)) return true;
            if (ShopCard.Get(c, s, sh, a, req, res)) return true;   // paying by card, digital products
            if (ShopRew.Get(c, s, sh, a, req, res)) return true;
            return Carriers.Get(c, s, sh, a, req, res) || ShopShip2.Get(c, s, sh, a, req, res) || ShopGoods.Get(c, s, sh, a, id, req, res);
        }

        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            int id = Int(b, "id"); later = null;
            switch (a)
            {
                case "return-do": ShopReturns.Do(c, s, sh, b); Web.Json(res, 200, ShopReturns.List(c, sh, Str(b, "f", 10))); return true;
                case "review-do": ShopReviews.Do(c, sh, b); Web.Json(res, 200, ShopReviews.List(c, sh, Str(b, "f", 10))); return true;
                case "pay-review": ShopPay.Review(c, s, sh, b); Ok(res); return true;
                case "post-save": Ok(res, "id", SavePost(c, sh, b), "list", Posts(c, sh)); return true;
                case "post-del": Exec(c, "DELETE FROM dbo.ShopPosts WHERE StoreId=@s AND Id=@i", "@s", s.Id, "@i", id); Shops.Bump(c, s.Id); Ok(res, "list", Posts(c, sh)); return true;
                case "order-new": OrderNew(c, s, sh, b, res); return true;
                case "order-merge": Merge(c, sh, b, res); return true;
                case "order-part": Part(c, sh, b, res); return true;
                case "wipe": { var why = ShopAccount.WipeNow(c, s.Id, ShopOrders.Mobile(Str(b, "phone", 24)) ?? ""); if (why != null) throw new ShopException("open", why); Ok(res); return true; }
                case "sheet-try": later = ShopPay.Try(c, sh, b, res); return true;
                case "ai-desc": later = ShopTranslate.Describe(c, s, sh, b, res); return true;
            }
            if (ShopApiHooks.Post(c, s, sh, a, b, res, out later)) return true;
            if (ShopLookMore.Post(c, s, sh, a, b, res, out later)) return true;
            if (ShopCard.Post(c, s, sh, a, b, res, out later)) return true;   // paying by card, digital products
            if (ShopRew.Post(c, s, sh, a, b, res)) return true;
            return Carriers.Post(c, s, sh, a, b, res, out later) || ShopShip2.Post(c, s, sh, a, b, res, out later) || ShopGoods.Post(c, s, sh, a, b, res, out later);
        }

        // ---------- an order taken by phone ----------
        static void OrderNew(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var lines = ShopJson.List(ShopJson.At(b, "items")).OfType<Dictionary<string, object>>().Select(x => new ShopLine { VariantId = ShopJson.Int(ShopJson.At(x, "v")), Qty = Math.Max(0, Math.Min(999, ShopJson.Int(ShopJson.At(x, "q")))), Extra = Str(x, "x", 600).Length == 0 ? null : Str(x, "x", 600) }).Where(l => l.Qty > 0).ToList();
            var o = new ShopOrderIn { Lines = lines, Name = Str(b, "name", 80), Phone = Str(b, "phone", 24), Phone2 = Str(b, "phone2", 24), Gov = Int(b, "gov"), City = Str(b, "city", 60), Area = Str(b, "area", 80), Address = Str(b, "address", 300), Landmark = Str(b, "landmark", 120), Note = Str(b, "note", 400), Coupon = Str(b, "cpn", 30), ClientKey = Str(b, "ck", 60), Lang = "ar", Src = Str(b, "src", 60).Length == 0 ? "تليفون" : Str(b, "src", 60), Channel = "owner", ByOwner = true, Agreed = true,
                How = new ShopHow { Pay = Str(b, "pay", 6).Length == 0 ? "cod" : Str(b, "pay", 6), Branch = Str(b, "branch", 20) } };
            var p = ShopOrders.Place(c, s, sh, o); long oid = p.Order.Id;
            if (!p.Again)
            {
                // the price the owner agreed on by phone
                if (b.ContainsKey("ship") || b.ContainsKey("off"))
                {
                    var e = new Dictionary<string, object> { { "name", p.Order.Name }, { "phone", p.Order.Phone }, { "phone2", p.Order.Phone2 ?? "" }, { "gov", p.Order.Gov }, { "city", p.Order.City ?? "" }, { "area", p.Order.Area ?? "" }, { "address", p.Order.Address }, { "landmark", p.Order.Landmark ?? "" }, { "note", p.Order.Note ?? "" }, { "items", p.Order.Items.Select(i => D("v", i.VariantId, "q", i.Qty, "x", i.Extra ?? "")).ToList<object>() } };
                    if (b.ContainsKey("ship")) e["ship"] = ShopJson.At(b, "ship"); if (b.ContainsKey("off")) e["off"] = ShopJson.At(b, "off");
                    var why = ShopOrders.Edit(c, sh, oid, e); if (why != null) throw new ShopException("edit", why);
                }
                if (ShopJson.Bool(ShopJson.At(b, "paid"))) Exec(c, "UPDATE dbo.ShopOrders SET PayState='paid', Paid=Total WHERE Id=@i AND StoreId=@s", "@i", oid, "@s", s.Id); else Exec(c, "UPDATE dbo.ShopOrders SET PayState=NULL, Deposit=0 WHERE Id=@i AND StoreId=@s", "@i", oid, "@s", s.Id);
                if (ShopJson.Bool(ShopJson.At(b, "confirmed"))) ShopOrders.SetStatus(c, s.Id, oid, "confirmed", "owner");
                ShopHooks.OnPlacedByOwner(s, oid, ShopJson.Bool(ShopJson.At(b, "tell")) && !ShopJson.Bool(ShopJson.At(b, "confirmed")));
            }
            Ok(res, "id", oid, "no", p.Order.No);
        }

        // ---------- two orders of the same person in one parcel ----------
        static void Merge(SqlConnection c, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var ids = ShopJson.List(ShopJson.At(b, "ids")).Select(x => (long)ShopJson.Int(x)).Distinct().ToList(); if (ids.Count != 2) throw new ShopException("two", "علّم على أوردرين بالظبط.");
            var l = ids.Select(i => ShopOrders.ById(c, sh.StoreId, i)).Where(o => o != null).OrderBy(o => o.Id).ToList(); if (l.Count != 2) throw new ShopException("gone", "الأوردر ده مش موجود.");
            ShopOrder x = l[0], y = l[1]; Func<ShopOrder, bool> early = o => o.Status == "new" || o.Status == "noreply" || o.Status == "confirmed";
            if (x.Phone != y.Phone) throw new ShopException("phone", "الأوردرين لازم يكونوا لنفس رقم الموبايل.");
            if (!early(x) || !early(y)) throw new ShopException("late", "ينفع ندمج الأوردرات اللي لسه ماخرجتش للشحن بس.");
            if (x.Pay != y.Pay || x.Extras.ContainsKey("branch") != y.Extras.ContainsKey("branch")) throw new ShopException("pay", "طريقة الدفع أو الاستلام مختلفة في الأوردرين.");
            // one parcel: one delivery, and each kind of fee once (the bigger one)
            var fees = ShopJson.Arr(x.FeesJson).Concat(ShopJson.Arr(y.FeesJson)).Select(ShopJson.List).Where(f => f.Count > 2).GroupBy(f => ShopJson.Str(f[0])).Select(g => g.OrderByDescending(f => ShopJson.Dec(f[2])).First()).ToList(); decimal feeSum = fees.Count > 0 ? fees.Sum(f => ShopJson.Dec(f[2])) : x.Fees + y.Fees;
            decimal ship = Math.Max(x.Ship, y.Ship), total = x.Subtotal + y.Subtotal - x.Discount - y.Discount + ship + feeSum;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    Action<string, object[]> ex = (sql, a) => { using (var cmd = Db.Cmd(c, sql, a)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } };
                    // both as they were read, or nothing moves (pressed twice, shipped a moment ago)
                    using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopOrders WITH (UPDLOCK, HOLDLOCK) WHERE StoreId=@s AND ((Id=@x AND Total=@xt) OR (Id=@y AND Total=@yt)) AND Status IN ('new','noreply','confirmed')", "@s", sh.StoreId, "@x", x.Id, "@xt", x.Total, "@y", y.Id, "@yt", y.Total)) { cmd.Transaction = tx; if ((int)cmd.ExecuteScalar() != 2) throw new ShopException("busy", "الأوردر اتغيّر حالًا. حدّث الصفحة."); }
                    ex("UPDATE dbo.ShopOrderItems SET OrderId=@x WHERE OrderId=@y AND StoreId=@s", new object[] { "@x", x.Id, "@y", y.Id, "@s", sh.StoreId });
                    ex("UPDATE dbo.ShopOrders SET Subtotal=@sub, Discount=@d, Ship=@sh, Fees=@f, FeesJson=@fj, Total=@t, Paid=Paid+@pd, Deposit=Deposit+@dp, Note=@n WHERE Id=@i AND StoreId=@s", new object[] { "@sub", x.Subtotal + y.Subtotal, "@d", x.Discount + y.Discount, "@sh", ship, "@f", feeSum, "@fj", fees.Count == 0 ? null : ShopJson.Write(fees), "@t", total, "@pd", y.Paid, "@dp", y.Deposit, "@n", Nz(string.Join(" · ", new[] { x.Note, y.Note }.Where(n => !string.IsNullOrWhiteSpace(n)))), "@i", x.Id, "@s", sh.StoreId });
                    ex("UPDATE dbo.ShopOrders SET Coupon=NULL, Subtotal=0, Discount=0, Ship=0, Fees=0, FeesJson=NULL, Total=0, Paid=0, Deposit=0 WHERE Id=@i AND StoreId=@s", new object[] { "@i", y.Id, "@s", sh.StoreId });
                    ShopOrders.Log_(c, tx, sh.StoreId, x.Id, "owner", "اتدمج معاه أوردر " + y.NoText + " (الإجمالي بقى " + ShopOrders.Money(total) + ")");
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            ShopOrders.SetStatus(c, sh.StoreId, y.Id, "cancelled", "owner", "اتدمج مع أوردر " + x.NoText, false);
            ShopHooks.OnEdit(c, sh.StoreId, x.Id); Ok(res, "id", x.Id);
        }

        // ---------- «تسليم جزئي»: the customer took some and gave the rest back to the courier ----------
        static void Part(SqlConnection c, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var o = ShopOrders.ById(c, sh.StoreId, Int(b, "id")); if (o == null) throw new ShopException("gone", "الأوردر ده مش موجود.");
            if (o.Status != "shipped" && o.Status != "out" && o.Status != "confirmed") throw new ShopException("st", "التسليم الجزئي لأوردر خرج للشحن بس.");
            // by the order's own line (the same size may be there twice with other words on it)
            var took = ShopJson.List(ShopJson.At(b, "items")).OfType<Dictionary<string, object>>().GroupBy(x => (long)ShopJson.Int(ShopJson.At(x, "i"))).ToDictionary(g => g.Key, g => ShopJson.Int(ShopJson.At(g.First(), "q")));
            var keep = new List<object>(); bool less = false;
            foreach (var i in o.Items) { int q; if (!took.TryGetValue(i.Id, out q)) q = i.Qty; q = Math.Max(0, Math.Min(i.Qty, q)); if (q < i.Qty) less = true; if (q > 0) keep.Add(D("v", i.VariantId, "q", q, "x", i.Extra ?? "")); }
            if (!less) throw new ShopException("none", "اختار القطع اللي الزبون رجّعها للمندوب."); if (keep.Count == 0) throw new ShopException("all", "لو رجّع كل حاجة، غيّر حالة الأوردر لـ«رجع».");
            var e = ShopOrders.Edit(c, sh, o.Id, new Dictionary<string, object> { { "name", o.Name }, { "phone", o.Phone }, { "phone2", o.Phone2 ?? "" }, { "gov", o.Gov }, { "city", o.City ?? "" }, { "area", o.Area ?? "" }, { "address", o.Address }, { "landmark", o.Landmark ?? "" }, { "note", o.Note ?? "" }, { "items", keep }, { "ship", o.Ship } });
            if (e != null) throw new ShopException("edit", e);
            ShopOrders.Log_(c, null, sh.StoreId, o.Id, "owner", "تسليم جزئي: الباقي رجع للمخزون");
            e = ShopOrders.SetStatus(c, sh.StoreId, o.Id, "delivered", "owner"); if (e != null) throw new ShopException("status", e);
            Ok(res);
        }

        // ---------- «قايمة التجهيز»: what is confirmed and not shipped, gathered by product ----------
        static Dictionary<string, object> Prep(SqlConnection c, Shop sh)
        {
            var ids = Rows(c, r => Convert.ToInt64(r[0]), "SELECT TOP 300 Id FROM dbo.ShopOrders WHERE StoreId=@s AND Status='confirmed' ORDER BY Id", "@s", sh.StoreId);
            var os = ids.Select(i => ShopOrders.ById(c, sh.StoreId, i)).Where(o => o != null).ToList();
            var by = os.SelectMany(o => o.Items.Select(i => new { o, i })).GroupBy(k => k.i.VariantId + "|" + (k.i.Extra ?? "")).Select(g => D("n", g.First().i.Name, "o", g.First().i.Opts ?? "", "x", g.First().i.Extra ?? "", "sku", g.First().i.Sku ?? "", "q", g.Sum(k => k.i.Qty), "im", string.IsNullOrEmpty(g.First().i.Image) ? "" : ShopImages.Url(g.First().i.Image, true), "nos", g.Select(k => k.o.No).Distinct().ToArray(), "v", g.First().i.VariantId)).OrderByDescending(d => (int)d["q"]).ToList();
            ShopGoods.Prep(c, sh, os, by);   // where to take each line from, what still waits for its pieces
            return D("list", by, "orders", os.Count, "ids", os.Select(o => o.Id).ToArray(), "days", ShopShipDay.Prep(os));
        }

        // ---------- what is printed ----------
        // Code 128 B, as bars (widths of black and white in turn)
        static readonly string[] Bars = ("212222 222122 222221 121223 121322 131222 122213 122312 132212 221213 221312 231212 112232 122132 122231 113222 123122 123221 223211 221132 221231 213212 223112 312131 311222 321122 321221 312212 322112 322211 212123 212321 232121 111323 131123 131321 112313 132113 132311 211313 231113 231311 112133 112331 132131 113123 113321 133121 313121 211331 231131 213113 213311 213131 311123 311321 331121 312113 312311 332111 314111 221411 431111 111224 111422 121124 121421 141122 141221 112214 112412 122114 122411 142112 142211 241211 221114 413111 241112 134111 111242 121142 121241 114212 124112 124211 411212 421112 421211 212141 214121 412121 111143 111341 131141 114113 114311 411113 411311 113141 114131 311141 411131 211412 211214 211232 2331112").Split(' ');
        public static string Barcode(string text, int h = 46)
        {
            text = new string((text ?? "").Where(ch => ch >= 32 && ch < 127).Take(40).ToArray()); if (text.Length == 0) return "";
            var codes = new List<int> { 104 }; int sum = 104; for (int i = 0; i < text.Length; i++) { int v = text[i] - 32; codes.Add(v); sum += v * (i + 1); } codes.Add(sum % 103); codes.Add(106);
            var sb = new StringBuilder(); int x = 10; foreach (var k in codes) { bool black = true; foreach (var w in Bars[k]) { int n = w - '0'; if (black) sb.Append("<rect x=\"").Append(x).Append("\" y=\"0\" width=\"").Append(n).Append("\" height=\"").Append(h).Append("\"/>"); x += n; black = !black; } }
            return "<svg class=\"bc\" viewBox=\"0 0 " + (x + 10) + " " + h + "\" preserveAspectRatio=\"none\" shape-rendering=\"crispEdges\">" + sb + "</svg>";
        }
        static string M(decimal v) { return ShopOrders.Money(v); }
        static void Print(SqlConnection c, StoreInfo s, Shop sh, HttpRequest req, HttpResponse res)
        {
            string kind = req.QueryString["kind"] ?? ""; var ids = (req.QueryString["ids"] ?? "").Split(',').Select(x => { long n; return long.TryParse(x, out n) ? n : 0; }).Where(n => n > 0).Distinct().Take(100).ToList();
            var h = new StringBuilder("<!doctype html><html lang=\"ar\" dir=\"rtl\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>طباعة</title><style>*{box-sizing:border-box}body{margin:0;font:14px/1.6 'Segoe UI',Tahoma,sans-serif;color:#000;background:#eee}.pg{background:#fff;margin:12px auto;padding:10mm;width:210mm;min-height:120mm;page-break-after:always}.lb{width:100mm;height:150mm;padding:5mm;overflow:hidden;display:flex;flex-direction:column;gap:2mm;font-size:13px}.lb h1{font-size:17px;margin:0}.lb .to{border:2px solid #000;border-radius:3mm;padding:2.5mm;font-size:14px}.lb .to b{font-size:16px}.due{font-size:22px;font-weight:700;border:2px solid #000;border-radius:3mm;padding:1mm 3mm;text-align:center}.it{font-size:12px;flex:1;overflow:hidden}.bc{width:100%;height:14mm;display:block}.num{direction:ltr;unicode-bidi:isolate}table{width:100%;border-collapse:collapse}th,td{border-bottom:1px solid #ccc;padding:2mm;text-align:start;vertical-align:top}th{background:#f3f3f3}.row{display:flex;justify-content:space-between;gap:6mm;flex-wrap:wrap}.tot td{border:0;padding:1mm 2mm}.tot .b td{font-weight:700;font-size:16px;border-top:2px solid #000}.tags{display:flex;flex-wrap:wrap;gap:3mm}.tag{width:50mm;height:30mm;border:1px dashed #999;padding:2mm;text-align:center;font-size:11px;overflow:hidden;display:flex;flex-direction:column;justify-content:space-between}.tag b{font-size:15px}.tag .bc{height:9mm}.bar{position:sticky;top:0;background:#111;color:#fff;padding:8px 14px;display:flex;gap:10px;align-items:center}.bar button{font:inherit;padding:6px 18px;border:0;border-radius:8px;background:#fff;cursor:pointer}small{color:#444}@media print{body{background:#fff}.bar{display:none}.pg{margin:0;box-shadow:none}@page{margin:0}}" + (kind == "label" ? "@page{size:100mm 150mm}" : "@page{size:A4}") + "</style></head><body><div class=\"bar\"><button onclick=\"print()\">اطبع</button><span>أو اختار «حفظ PDF» من شاشة الطباعة</span></div>");
            string name = H(sh.Name), phone = H(ShopJson.Str(ShopJson.At(ShopJson.Sub(sh.Settings, "contact"), "phone"))); var legal = ShopJson.Sub(sh.Settings, "legal"); var tax = ShopJson.Sub(sh.Settings, "tax");
            if (kind == "invoice") h.Append(ShopInvoice.Head(c, sh));   // the invoice's styles, logo and download buttons (ShopInvoice.cs)
            if (kind == "label" || kind == "invoice")
                foreach (var o in ids.Select(i => ShopOrders.ById(c, sh.StoreId, i)).Where(o => o != null))
                {
                    var rc = ShopJson.Sub(o.Extras, "recv"); string toName = rc.Count > 0 ? ShopJson.Str(ShopJson.At(rc, "n")) : o.Name, toPhone = rc.Count > 0 ? ShopJson.Str(ShopJson.At(rc, "p")) : o.Phone;
                    if (kind == "label")
                    {
                        h.Append("<div class=\"pg lb\"><div class=\"row\"><div><h1>").Append(name).Append("</h1><span class=\"num\">").Append(phone).Append("</span></div><div style=\"text-align:end\"><b>أوردر ").Append(o.No).Append("</b><br><small>").Append(o.CreatedAt.AddHours(2).ToString("yyyy-MM-dd")).Append("</small></div></div>").Append(Barcode(string.IsNullOrEmpty(o.Tracking) ? o.No.ToString() : o.Tracking)).Append("<div style=\"text-align:center\" class=\"num\">").Append(H(string.IsNullOrEmpty(o.Tracking) ? o.No.ToString() : o.Tracking)).Append("</div>")
                            .Append("<div class=\"to\"><b>").Append(H(toName)).Append("</b><br><span class=\"num\">").Append(H(ShopOrders.Local(toPhone))).Append(o.Phone2 == null ? "" : " · " + H(ShopOrders.Local(o.Phone2))).Append("</span><br>").Append(H(ShopOrders.AddressText(o))).Append("</div><div class=\"due\">").Append(o.Due > 0 ? "المطلوب: " + H(M(o.Due)) : "مدفوع، مفيش تحصيل").Append("</div>").Append(ShopShipDay.Label(o)).Append("<div class=\"it\">");
                        foreach (var i in o.Items) h.Append("• ").Append(H(i.Name)).Append(string.IsNullOrEmpty(i.Opts) ? "" : " (" + H(i.Opts) + ")").Append(" × ").Append(i.Qty).Append("<br>");
                        if (!string.IsNullOrEmpty(o.Note)) h.Append("<b>ملاحظة:</b> ").Append(H(o.Note)); h.Append("</div></div>");
                    }
                    else h.Append(ShopInvoice.Html(c, sh, o));   // one invoice for the printed page and its downloads
                }
            else if (kind == "tags")
            {
                var d = ShopCache.Get(c, sh); int copies = Math.Max(1, Math.Min(50, ShopJson.Int(req.QueryString["n"], 1))); h.Append("<div class=\"pg\"><div class=\"tags\">");
                foreach (var pid in ids) { ShopProd p; if (!d.ById.TryGetValue((int)pid, out p)) continue; foreach (var v in p.Vars.Where(y => y.Active)) { decimal? old; ShopOffer of; var u = ShopPrice.Unit(d, p, v, DateTime.UtcNow, out old, out of); string code = v.Sku ?? p.Sku; if (string.IsNullOrEmpty(code) || code.Any(ch => ch < 32 || ch > 126)) code = "P" + v.Id; for (int i = 0; i < copies; i++) h.Append("<div class=\"tag\"><span>").Append(H(p.Name)).Append(v.Label.Length > 0 ? " · " + H(v.Label) : "").Append("</span><b>").Append(H(M(u))).Append("</b><div>").Append(Barcode(code)).Append("<span class=\"num\">").Append(H(code)).Append("</span></div></div>"); } }
                h.Append("</div></div>");
            }
            else if (kind == "prep")
            {
                var p = Prep(c, sh); h.Append("<div class=\"pg\"><h1 style=\"margin:0\">قايمة التجهيز</h1><p>").Append(name).Append(" · ").Append(DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-dd HH:mm")).Append(" · ").Append(p["orders"]).Append(" أوردر متأكد</p><table><thead><tr><th>المنتج</th><th>الكود</th><th>الكمية</th><th>أوردرات</th></tr></thead><tbody>");
                foreach (var i in (List<Dictionary<string, object>>)p["list"]) h.Append("<tr><td>").Append(H((string)i["n"])).Append(((string)i["o"]).Length > 0 ? "<br><small>" + H((string)i["o"]) + "</small>" : "").Append(((string)i["x"]).Length > 0 ? "<br><small>" + H((string)i["x"]) + "</small>" : "").Append(i.ContainsKey("at") ? "<br><small>" + H((string)i["at"]) + "</small>" : "").Append(i.ContainsKey("pre") ? "<br><small>مستني " + i["pre"] + " حجز مسبق</small>" : "").Append("</td><td class=\"num\">").Append(H((string)i["sku"])).Append("</td><td><b>").Append(i["q"]).Append("</b></td><td>").Append(string.Join("، ", (int[])i["nos"])).Append("</td></tr>");
                h.Append("</tbody></table></div>");
            }
            else { Web.Error(res, 404, "not_found"); return; }
            res.ContentType = "text/html; charset=utf-8"; res.AddHeader("X-Robots-Tag", "noindex"); res.AddHeader("X-Frame-Options", "DENY"); res.Write(h.Append("</body></html>").ToString());
        }

        // ---------- the blog ----------
        static List<Dictionary<string, object>> Posts(SqlConnection c, Shop sh) { return Rows(c, r => D("id", (int)r["Id"], "slug", (string)r["Slug"], "title", (string)r["Title"], "titleEn", r["TitleEn"] as string ?? "", "im", r["Image"] as string ?? "", "hidden", (bool)r["Hidden"], "at", Unix(r["At"])), "SELECT Id, Slug, Title, TitleEn, Image, Hidden, At FROM dbo.ShopPosts WHERE StoreId=@s ORDER BY At DESC, Id DESC", "@s", sh.StoreId); }
        static Dictionary<string, object> Post_(SqlConnection c, Shop sh, int id) { return Rows(c, r => D("id", (int)r["Id"], "slug", (string)r["Slug"], "title", (string)r["Title"], "titleEn", r["TitleEn"] as string ?? "", "body", r["Body"] as string ?? "", "bodyEn", r["BodyEn"] as string ?? "", "im", r["Image"] as string ?? "", "hidden", (bool)r["Hidden"], "ps", (r["Products"] as string ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), "at", Unix(r["At"])), "SELECT * FROM dbo.ShopPosts WHERE StoreId=@s AND Id=@i", "@s", sh.StoreId, "@i", id).FirstOrDefault(); }
        static int SavePost(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int id = Int(b, "id"); string t = Str(b, "title", 140); if (t.Length < 3) throw new ShopException("title", "اكتب عنوان المقال."); if (Str(b, "body", 30000).Length < 20) throw new ShopException("body", "اكتب المقال.");
            if (id == 0 && Num(c, "SELECT COUNT(*) FROM dbo.ShopPosts WHERE StoreId=@s", "@s", sh.StoreId) >= 100) throw new ShopException("limit", "أقصى عدد مقالات 100.");
            string slug = Shops.Slugify(Str(b, "slug", 80).Length > 0 ? Str(b, "slug", 80) : t, 70); if (slug.Length < 2) slug = "post"; string b0 = slug;
            for (int i = 2; Num(c, "SELECT COUNT(*) FROM dbo.ShopPosts WHERE StoreId=@s AND Slug=@k AND Id<>@i", "@s", sh.StoreId, "@k", slug, "@i", id) > 0; i++) slug = b0 + "-" + i;
            string im = Str(b, "im", 160); if (!im.StartsWith(sh.StoreId + "/", StringComparison.Ordinal)) im = "";
            string ps = string.Join(",", ShopJson.List(ShopJson.At(b, "ps")).Select(x => ShopJson.Int(x)).Where(x => x > 0).Distinct().Take(12)); object[] a = { "@s", sh.StoreId, "@i", id, "@k", slug, "@t", t, "@te", Nz(Str(b, "titleEn", 140)), "@b", Str(b, "body", 30000), "@be", Nz(Str(b, "bodyEn", 30000)), "@im", Nz(im), "@p", Nz(ps), "@h", ShopJson.Bool(ShopJson.At(b, "hidden")) };
            if (id > 0) Exec(c, "UPDATE dbo.ShopPosts SET Slug=@k, Title=@t, TitleEn=@te, Body=@b, BodyEn=@be, Image=@im, Products=@p, Hidden=@h WHERE StoreId=@s AND Id=@i", a);
            else using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopPosts(StoreId, Slug, Title, TitleEn, Body, BodyEn, Image, Products, Hidden) OUTPUT INSERTED.Id VALUES(@s, @k, @t, @te, @b, @be, @im, @p, @h)", a)) id = (int)cmd.ExecuteScalar();
            if (im.Length > 0) Exec(c, "UPDATE dbo.ShopImages SET Used=1 WHERE StoreId=@s AND Path=@p", "@s", sh.StoreId, "@p", im);
            Shops.Bump(c, sh.StoreId); return id;
        }

        // ---------- customers ----------
        static Dictionary<string, object> Customers(SqlConnection c, Shop sh, HttpRequest req)
        {
            string q = (req.QueryString["q"] ?? "").Trim(), ph = Regex.Replace(ShopJson.Digits(q), @"\D", "").TrimStart('0'); int page = Math.Max(1, ShopJson.Int(req.QueryString["page"], 1));
            var l = Rows(c, r => D("phone", ShopOrders.Local((string)r["Phone"]), "name", r["Name"] as string ?? "", "n", (int)r["N"], "got", (int)r["Got"], "back", (int)r["Back"], "total", r["Total"] is decimal ? (decimal)r["Total"] : 0, "last", Unix(r["LastAt"]), "acct", r["Proved"] is DateTime, "wipe", r["Wipe"] is DateTime),
                @"SELECT o.Phone, MAX(o.Name) AS Name, COUNT(*) AS N, SUM(CASE WHEN o.Status='delivered' THEN 1 ELSE 0 END) AS Got, SUM(CASE WHEN o.Status='returned' THEN 1 ELSE 0 END) AS Back, SUM(CASE WHEN o.Status='delivered' THEN o.Total-o.Refund ELSE 0 END) AS Total, MAX(o.CreatedAt) AS LastAt, MAX(k.ProvedAt) AS Proved, MAX(k.WipeAskedAt) AS Wipe
                  FROM dbo.ShopOrders o LEFT JOIN dbo.ShopCustomers k ON k.StoreId=o.StoreId AND k.Phone=o.Phone WHERE o.StoreId=@s AND o.Phone<>'' AND (@q='' OR (@p<>'' AND o.Phone LIKE '%'+@p+'%') OR o.Name LIKE '%'+@l+'%') GROUP BY o.Phone ORDER BY MAX(CASE WHEN k.WipeAskedAt IS NULL THEN 0 ELSE 1 END) DESC, MAX(o.CreatedAt) DESC OFFSET @o ROWS FETCH NEXT 51 ROWS ONLY",
                "@s", sh.StoreId, "@q", q, "@p", ph.Length >= 3 ? ph : "", "@l", q.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]"), "@o", (page - 1) * 50);
            bool more = l.Count > 50; if (more) l.RemoveAt(50);
            return D("list", l, "more", more, "all", Num(c, "SELECT COUNT(DISTINCT Phone) FROM dbo.ShopOrders WHERE StoreId=@s AND Phone<>''", "@s", sh.StoreId), "accts", Num(c, "SELECT COUNT(*) FROM dbo.ShopCustomers WHERE StoreId=@s AND ProvedAt IS NOT NULL", "@s", sh.StoreId));
        }
        static void CustomersXls(SqlConnection c, Shop sh, HttpResponse res)
        {
            var l = Rows(c, r => new object[] { r["Name"] as string ?? "", ShopOrders.Local((string)r["Phone"]), ShopGeo.Name((int)r["Gov"]), (int)r["N"], (int)r["Got"], (int)r["Back"], (decimal)r["Total"], ((DateTime)r["F"]).AddHours(2).ToString("yyyy-MM-dd"), ((DateTime)r["L"]).AddHours(2).ToString("yyyy-MM-dd"), r["Items"] as string ?? "" },
                @"SELECT TOP 20000 o.Phone, MAX(o.Name) AS Name, MAX(o.Gov) AS Gov, COUNT(*) AS N, SUM(CASE WHEN o.Status='delivered' THEN 1 ELSE 0 END) AS Got, SUM(CASE WHEN o.Status='returned' THEN 1 ELSE 0 END) AS Back, SUM(CASE WHEN o.Status='delivered' THEN o.Total-o.Refund ELSE 0 END) AS Total, MIN(o.CreatedAt) AS F, MAX(o.CreatedAt) AS L,
                  (SELECT TOP 1 STUFF((SELECT DISTINCT N'، '+i.Name FROM dbo.ShopOrderItems i JOIN dbo.ShopOrders z ON z.Id=i.OrderId WHERE z.StoreId=@s AND z.Phone=o.Phone AND z.Status='delivered' FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N'')) AS Items FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.Phone<>'' GROUP BY o.Phone ORDER BY MAX(o.CreatedAt) DESC", "@s", sh.StoreId);
            Xls(res, "customers.xlsx", ShopExcel.Book("الزباين", new[] { "الاسم", "الموبايل", "المحافظة", "أوردرات", "استلم", "رجّع", "اشترى بـ", "أول أوردر", "آخر أوردر", "اشترى إيه" }, l));
        }
        static void Xls(HttpResponse res, string name, byte[] data) { res.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; res.AddHeader("Content-Disposition", "attachment; filename=" + name); res.Cache.SetCacheability(HttpCacheability.NoCache); res.BinaryWrite(data); }

        // ---------- reports ----------
        // from / to = the owner's days (Cairo); 400 days at most
        static void Span(HttpRequest req, out DateTime from, out DateTime to)
        {
            var today = DateTime.UtcNow.AddHours(2).Date; DateTime a, z;
            if (!DateTime.TryParseExact(req.QueryString["from"] ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out a)) a = today.AddDays(-29);
            if (!DateTime.TryParseExact(req.QueryString["to"] ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out z) || z > today) z = today;
            if (a > z) a = z; if ((z - a).TotalDays > 400) a = z.AddDays(-400); from = a; to = z;
        }
        const string Good = "o.Status NOT IN ('cancelled','returned') AND o.Channel<>'gift'";   // a gift card's money is counted apart, when it's sold (ShopRew.Report)
        public static string Source(string src)
        {
            var s = (src ?? "").ToLowerInvariant(); if (s.Length == 0) return "مباشر أو مش معروف";
            if (s.Contains("تليفون")) return "تليفون"; if (s.Contains("instagram") || s.StartsWith("ig")) return "إنستجرام"; if (s.Contains("facebook") || s.StartsWith("fb") || s.Contains("fbclid")) return "فيسبوك"; if (s.Contains("tiktok")) return "تيك توك"; if (s.Contains("whatsapp") || s.Contains("wa.me")) return "واتساب"; if (s.Contains("google")) return "جوجل"; if (s.Contains("snap")) return "سناب شات"; if (s.Contains("youtube")) return "يوتيوب"; if (s.Contains("t.me") || s.Contains("telegram")) return "تيليجرام"; if (s.Contains("chat")) return "الشات";
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }
        static Dictionary<string, object> Report(SqlConnection c, Shop sh, HttpRequest req)
        {
            DateTime from, to; Span(req, out from, out to); DateTime f = from.AddHours(-2), t = to.AddDays(1).AddHours(-2); object[] a = { "@s", sh.StoreId, "@f", f, "@t", t }; var d = ShopCache.Get(c, sh);
            var sum = Rows(c, r => D("orders", (int)r[0], "sales", (decimal)r[1], "got", (int)r[2], "gotEgp", (decimal)r[3], "cancelled", (int)r[4], "returned", (int)r[5], "disc", (decimal)r[6], "ship", (decimal)r[7], "refund", (decimal)r[8], "open", (int)r[9]),
                "SELECT COUNT(*), ISNULL(SUM(CASE WHEN " + Good + " THEN o.Total ELSE 0 END),0), ISNULL(SUM(CASE WHEN o.Status='delivered' AND o.Channel<>'gift' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN o.Status='delivered' AND o.Channel<>'gift' THEN o.Total-o.Refund ELSE 0 END),0), ISNULL(SUM(CASE WHEN o.Status='cancelled' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN o.Status='returned' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN " + Good + " THEN o.Discount ELSE 0 END),0), ISNULL(SUM(CASE WHEN o.Status='delivered' AND o.Channel<>'gift' THEN o.Ship ELSE 0 END),0), ISNULL(SUM(o.Refund),0), ISNULL(SUM(CASE WHEN o.Status IN ('new','noreply','confirmed','shipped','out') THEN 1 ELSE 0 END),0) FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.CreatedAt>=@f AND o.CreatedAt<@t", a)[0];
            // profit of what was delivered: what was paid for the pieces less what they cost the owner (a piece with no cost is told apart)
            var pf = Rows(c, r => new[] { (decimal)r[0], (decimal)r[1], (decimal)r[2] }, "SELECT ISNULL(SUM(CASE WHEN i.Cost IS NOT NULL THEN (i.Price*i.Qty-i.Discount)*(i.Qty-i.Returned)/i.Qty ELSE 0 END),0), ISNULL(SUM(CASE WHEN i.Cost IS NOT NULL THEN i.Cost*(i.Qty-i.Returned) ELSE 0 END),0), ISNULL(SUM(CASE WHEN i.Cost IS NULL THEN (i.Price*i.Qty-i.Discount)*(i.Qty-i.Returned)/i.Qty ELSE 0 END),0) FROM dbo.ShopOrderItems i JOIN dbo.ShopOrders o ON o.Id=i.OrderId WHERE o.StoreId=@s AND o.Status='delivered' AND o.Channel<>'gift' AND i.Qty>0 AND o.CreatedAt>=@f AND o.CreatedAt<@t", a)[0];
            sum["profit"] = Math.Round(pf[0] - pf[1], 2); sum["cost"] = Math.Round(pf[1], 2); sum["noCost"] = Math.Round(pf[2], 2);
            sum["rew"] = ShopRew.Report(c, sh.StoreId, req);   // gift cards sold and used, points, friends: apart
            var days = Rows(c, r => new object[] { ((DateTime)r[0]).ToString("yyyy-MM-dd"), (int)r[1], (decimal)r[2] }, "SELECT CAST(DATEADD(HOUR,2,o.CreatedAt) AS DATE), COUNT(*), ISNULL(SUM(CASE WHEN " + Good + " THEN o.Total ELSE 0 END),0) FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.CreatedAt>=@f AND o.CreatedAt<@t GROUP BY CAST(DATEADD(HOUR,2,o.CreatedAt) AS DATE) ORDER BY 1", a);
            var seen = Rows(c, r => new[] { (int)r[0], (int)r[1], (int)r[2] }, "SELECT ProductId, SUM(Views), SUM(Carts) FROM dbo.ShopProdDaily WHERE StoreId=@s AND Day>=@a AND Day<=@z GROUP BY ProductId", "@s", sh.StoreId, "@a", from, "@z", to).ToDictionary(x => x[0]);
            var sold = Rows(c, r => D("id", (int)r[0], "n", (string)r[1], "q", (int)r[2], "egp", (decimal)r[3], "profit", r[4] is decimal ? (object)(decimal)r[4] : null, "back", (int)r[5]),
                "SELECT i.ProductId, MAX(i.Name), SUM(CASE WHEN " + Good + " THEN i.Qty ELSE 0 END), SUM(CASE WHEN " + Good + " THEN i.Price*i.Qty-i.Discount ELSE 0 END), SUM(CASE WHEN o.Status='delivered' AND i.Cost IS NOT NULL AND i.Qty>0 THEN ((i.Price*i.Qty-i.Discount)/i.Qty-i.Cost)*(i.Qty-i.Returned) END), SUM(CASE WHEN o.Status='returned' THEN i.Qty ELSE i.Returned END) FROM dbo.ShopOrderItems i JOIN dbo.ShopOrders o ON o.Id=i.OrderId WHERE o.StoreId=@s AND o.CreatedAt>=@f AND o.CreatedAt<@t GROUP BY i.ProductId", a).ToDictionary(x => (int)x["id"]);
            var prods = new List<Dictionary<string, object>>();
            foreach (var pid in sold.Keys.Union(seen.Keys)) { ShopProd p; d.ById.TryGetValue(pid, out p); Dictionary<string, object> x; if (!sold.TryGetValue(pid, out x)) { if (p == null) continue; x = D("id", pid, "n", p.Name, "q", 0, "egp", 0m, "profit", null, "back", 0); } int[] v; seen.TryGetValue(pid, out v); x["views"] = v == null ? 0 : v[1]; x["carts"] = v == null ? 0 : v[2]; x["im"] = p == null || p.Pics.Count == 0 ? "" : p.Pics[0].Path; prods.Add(x); }
            var cats = new Dictionary<string, decimal[]>();
            foreach (var x in prods) { ShopProd p; string k = "من غير قسم"; if (d.ById.TryGetValue((int)x["id"], out p)) { ShopCat g; if (d.CatById.TryGetValue(p.MainCat, out g)) { ShopCat up; k = g.ParentId.HasValue && d.CatById.TryGetValue(g.ParentId.Value, out up) ? up.Name : g.Name; } } decimal[] m; if (!cats.TryGetValue(k, out m)) cats[k] = m = new decimal[4]; m[0] += (int)x["views"]; m[1] += (int)x["q"]; m[2] += (decimal)x["egp"]; m[3] += (int)x["back"]; }
            var govs = Rows(c, r => new object[] { ShopGeo.Name((int)r[0]), (int)r[1], (decimal)r[2], (int)r[3] }, "SELECT TOP 27 o.Gov, COUNT(*), ISNULL(SUM(CASE WHEN " + Good + " THEN o.Total ELSE 0 END),0), SUM(CASE WHEN o.Status='returned' THEN 1 ELSE 0 END) FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.CreatedAt>=@f AND o.CreatedAt<@t GROUP BY o.Gov ORDER BY COUNT(*) DESC", a);
            var srcs = Rows(c, r => new { k = Source(r[0] as string), n = (int)r[1], e = (decimal)r[2], g = (int)r[3] }, "SELECT o.Src, COUNT(*), ISNULL(SUM(CASE WHEN " + Good + " THEN o.Total ELSE 0 END),0), SUM(CASE WHEN o.Status='delivered' THEN 1 ELSE 0 END) FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.CreatedAt>=@f AND o.CreatedAt<@t GROUP BY o.Src", a).GroupBy(x => x.k).Select(g => new object[] { g.Key, g.Sum(x => x.n), g.Sum(x => x.e), g.Sum(x => x.g) }).OrderByDescending(x => (int)x[1]).Take(12).ToArray();
            var fn = Rows(c, r => new[] { (int)r[0], (int)r[1], (int)r[2], (int)r[3], (int)r[4], (int)r[5] }, "SELECT ISNULL(SUM(Visitors),0), ISNULL(SUM(Visits),0), ISNULL(SUM(Views),0), ISNULL(SUM(Carts),0), ISNULL(SUM(Checkouts),0), ISNULL(SUM(Orders),0) FROM dbo.ShopDaily WHERE StoreId=@s AND Day>=@a AND Day<=@z", "@s", sh.StoreId, "@a", from, "@z", to)[0];
            Func<string, object> find = w => Rows(c, r => new object[] { (string)r[0], (int)r[1], (int)r[2] }, "SELECT TOP 20 Q, SUM(N), MAX(Found) FROM dbo.ShopSearches WHERE StoreId=@s AND Day>=@a AND Day<=@z GROUP BY Q " + w + " ORDER BY SUM(N) DESC", "@s", sh.StoreId, "@a", from, "@z", to).ToArray();
            return D("from", from.ToString("yyyy-MM-dd"), "to", to.ToString("yyyy-MM-dd"), "sum", sum, "days", days, "prods", prods.OrderByDescending(x => (decimal)x["egp"]).ThenByDescending(x => (int)x["views"]).Take(100).ToArray(), "cats", cats.OrderByDescending(k => k.Value[2]).Select(k => new object[] { k.Key, k.Value[0], k.Value[1], k.Value[2], k.Value[3] }).ToArray(),
                "govs", govs, "srcs", srcs, "funnel", fn, "find", find(""), "miss", find("HAVING MAX(Found)=0"), "carts", Carts.Stats(c, sh.StoreId));
        }
        static void ReportXls(SqlConnection c, Shop sh, HttpRequest req, HttpResponse res)
        {
            var r = Report(c, sh, req); string k = req.QueryString["k"] ?? "prods";
            if (k == "govs") Xls(res, "governorates.xlsx", ShopExcel.Book("المحافظات", new[] { "المحافظة", "أوردرات", "مبيعات", "رجع" }, ((List<object[]>)r["govs"])));
            else if (k == "days") Xls(res, "days.xlsx", ShopExcel.Book("الأيام", new[] { "اليوم", "أوردرات", "مبيعات" }, (List<object[]>)r["days"]));
            else if (k == "cats") Xls(res, "categories.xlsx", ShopExcel.Book("الأقسام", new[] { "القسم", "مشاهدات", "قطع", "مبيعات", "رجع" }, (object[][])r["cats"]));
            else if (k == "srcs") Xls(res, "sources.xlsx", ShopExcel.Book("المصادر", new[] { "المصدر", "أوردرات", "مبيعات", "اتسلّم" }, (object[][])r["srcs"]));
            else Xls(res, "products-report.xlsx", ShopExcel.Book("المنتجات", new[] { "المنتج", "مشاهدات", "اتحط في السلة", "قطع", "مبيعات", "مكسب", "رجع" }, ((Dictionary<string, object>[])r["prods"]).Select(x => new object[] { x["n"], x["views"], x["carts"], x["q"], x["egp"], x["profit"] ?? "", x["back"] })));
        }

        // ---------- «نسخة من المتجر»: everything that is the owner's, in one file ----------
        static void Backup(SqlConnection c, Shop sh, HttpResponse res)
        {
            if (!Web.Allow("shop-bak:" + sh.StoreId, 4, TimeSpan.FromHours(6))) { Web.Error(res, 429, "rate_limited", "نزّلت نسخ كتير النهارده. جرّب بعد كام ساعة."); return; }
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Action<string, byte[]> put = (n, data) => { using (var f = zip.CreateEntry(n, CompressionLevel.Fastest).Open()) f.Write(data, 0, data.Length); };
                    Func<string, object> table = t => Rows(c, r => { var x = new Dictionary<string, object>(); for (int i = 0; i < r.FieldCount; i++) { var v = r.GetValue(i); x[r.GetName(i)] = v == DBNull.Value ? null : v is DateTime ? ((DateTime)v).ToString("o") : v; } return x; }, "SELECT * FROM dbo." + t + " WHERE StoreId=@s", "@s", sh.StoreId);
                    put("products.xlsx", ShopExcel.Products(c, sh)); put("orders.xlsx", ShopExcel.AllOrders(c, sh));
                    foreach (var t in new[] { "ShopCategories", "ShopProducts", "ShopVariants", "ShopProductCategories", "ShopImages", "ShopPages", "ShopPosts", "ShopOffers", "ShopCoupons", "ShopZones", "ShopOrders", "ShopOrderItems", "ShopReturns", "ShopReviews", "ShopCustomers", "ShopLeads" }) put("data/" + t + ".json", Encoding.UTF8.GetBytes(new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(table(t))));
                    put("data/look.json", Encoding.UTF8.GetBytes(sh.LiveJson ?? sh.DraftJson ?? "{}")); put("data/settings.json", Encoding.UTF8.GetBytes(sh.SettingsJson ?? "{}"));
                    put("pictures.txt", Encoding.UTF8.GetBytes(string.Join("\r\n", Rows(c, r => ShopImages.Url((string)r[0]), "SELECT Path FROM dbo.ShopImages WHERE StoreId=@s", "@s", sh.StoreId))));
                }
                res.ContentType = "application/zip"; res.AddHeader("Content-Disposition", "attachment; filename=" + sh.Slug + "-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".zip"); res.Cache.SetCacheability(HttpCacheability.NoCache); res.BinaryWrite(ms.ToArray());
            }
        }
    }
}
