// Around an order on a store: the proof of a transfer or a deposit (the money goes to the owner's own InstaPay / wallet / bank, never through
// Sellybot), a customer's picture for a product made for them, the checkout that was left half way («السلة المتروكة», sent by Carts.cs),
// «أخبار المتجر» on WhatsApp, the message when it ships, the owner's alert when a product is about to run out, and the orders written to
// the owner's own Google Sheet (an Apps Script address they paste, no account linking).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class ShopPay
    {
        static string T(ShopFrontHandler.Ctx x, string ar, string en) { return x.Lang == "en" ? en : ar; }
        static string Str(Dictionary<string, object> b, string k, int n) { return ShopJson.Str(ShopJson.At(b, k), n); }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        static void Ok(HttpResponse res, params object[] kv) { var d = new Dictionary<string, object> { { "ok", true } }; for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; Web.Json(res, 200, d); }
        public static string WayName(string k, bool en = false) { return k == "insta" ? (en ? "InstaPay" : "إنستاباي") : k == "vf" ? (en ? "Mobile wallet" : "محفظة موبايل") : k == "bank" ? (en ? "Bank transfer" : "تحويل بنكي") : en ? "Cash on delivery" : "كاش عند الاستلام"; }

        // the customer's picture of the transfer, from the order's page (its secret token)
        public static void Proof(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (x.Preview) throw new ShopException("preview", T(x, "المعاينة مابتحفظش حاجة.", "The preview saves nothing."));
            var o = ShopOrders.ByToken(c, x.Row.StoreId, Regex.Replace(Str(b, "tok", 40), "[^a-f0-9]", ""));
            if (o == null || o.Deposit <= 0 || o.PayState == "paid" || o.Status == "cancelled" || o.Status == "returned") throw new ShopException("no_order", T(x, "الطلب ده مش محتاج تحويل.", "This order doesn't need a transfer."));
            if (!Web.Allow("shop-proof:" + x.Ip, 10, TimeSpan.FromHours(1)) || !Web.Allow("shop-prf:" + o.Id, 8, TimeSpan.FromHours(24))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            string path = ShopAccount.Upload(c, x, b); Exec(c, "UPDATE dbo.ShopImages SET Used=1 WHERE StoreId=@s AND Path=@p", "@s", x.Row.StoreId, "@p", path);
            Exec(c, "UPDATE dbo.ShopOrders SET Proof=@p, PayState='sent', Seen=0 WHERE Id=@i AND StoreId=@s AND ISNULL(PayState,'')<>'paid'", "@p", path, "@i", o.Id, "@s", x.Row.StoreId);
            if (o.Proof != null && o.Proof != path) try { ShopImages.Delete(c, x.Row.StoreId, new[] { o.Proof }); } catch (Exception ex) { Log.Error(ex); }
            ShopOrders.Log_(c, null, x.Row.StoreId, o.Id, "customer", "بعت صورة التحويل (" + ShopOrders.Money(o.Deposit) + ")");
            ShopHooks.Tell(x.Row.StoreId, "pay", "💸 *صورة تحويل وصلت* لأوردر " + o.NoText + "\n" + o.Name + " · " + ShopOrders.Local(o.Phone) + " · " + ShopOrders.Money(o.Deposit) + "\n\nراجعها من «متجري» ثم «الطلبات».", "sb-shop-pr-" + o.Id + "-" + DateTime.UtcNow.Ticks);
            Ok(res, "ps", "sent");
        }
        // a picture that goes with a product made for the customer
        public static void Pic(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            ShopProd p; if (!x.Data.ById.TryGetValue(ShopJson.Int(ShopJson.At(b, "p")), out p) || !ShopJson.Arr(p.AskJson).OfType<Dictionary<string, object>>().Any(a => ShopJson.Str(ShopJson.At(a, "k")) == "pic")) throw new ShopException("no", T(x, "المنتج ده مابيستقبلش صور.", "This product doesn't take pictures."));
            if (x.Preview) throw new ShopException("preview", T(x, "المعاينة مابتحفظش حاجة.", "The preview saves nothing."));
            if (!Web.Allow("shop-ppic:" + x.Ip, 10, TimeSpan.FromHours(1)) || !Web.Allow("shop-pics:" + x.Row.StoreId, 150, TimeSpan.FromHours(24))) throw new ShopException("too_many", T(x, "صور كتير. استنى شوية.", "Too many pictures. Please wait a while."));
            Ok(res, "path", ShopAccount.Upload(c, x, b));
        }

        // ---------- a checkout that was started with a number ----------
        static Dictionary<string, object> Abandon(Shop sh) { return ShopJson.Sub(sh.Settings, "abandon"); }
        public static bool CartOn(Shop sh) { return sh != null && ShopJson.Bool(ShopJson.At(Abandon(sh), "on"), true); }
        public static string CartCoupon(Shop sh) { return ShopJson.Str(ShopJson.At(Abandon(sh), "coupon"), 30); }
        static int Known(SqlConnection c, int storeId, string phone) { using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopCarts WHERE StoreId=@s AND Phone=@p AND At>DATEADD(DAY,-1,SYSUTCDATETIME())", "@s", storeId, "@p", phone)) return (int)cmd.ExecuteScalar(); }
        public static void Cart(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            string tok = Str(b, "tok", 40);
            if (Regex.IsMatch(tok, "^[a-f0-9]{24}$"))
            {
                // the link of the reminder: the cart again
                string lines = null; using (var cmd = Db.Cmd(c, "SELECT Lines FROM dbo.ShopCarts WHERE StoreId=@s AND Token=@t", "@s", x.Row.StoreId, "@t", tok)) lines = cmd.ExecuteScalar() as string;
                Ok(res, "l", lines == null ? new List<object>() : ShopJson.Arr(lines)); return;
            }
            var phone = ShopOrders.Mobile(Str(b, "phone", 24)); var l = ShopActions.Lines(b);
            if (phone == null || l.Count == 0 || x.Preview || !CartOn(x.Row) || !Web.Allow("shop-cart:" + x.Ip, 30, TimeSpan.FromMinutes(10))) { Ok(res); return; }
            // a reminder is a message from the merchant's own number: one place can't make it write to many strangers
            if (Known(c, x.Row.StoreId, phone) == 0 && (!Web.Allow("shop-cartip:" + x.Row.StoreId + ":" + x.Ip, 6, TimeSpan.FromHours(24)) || !Web.Allow("shop-cartd:" + x.Row.StoreId, 150, TimeSpan.FromHours(24)))) { Ok(res); return; }
            var q = ShopPrice.Quote(c, x.Data, l, null, ShopJson.Int(ShopJson.At(b, "gov")), phone, x.Now, false); if (q.Items == 0) { Ok(res); return; }
            string name = Regex.Replace(Str(b, "name", 80), @"[\u0000-\u001F]", ""), token = Shops.Token(12), json = ShopJson.Write(l.Select(y => new object[] { y.VariantId, y.Qty, y.Extra ?? "" }));
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.ShopCarts SET Lines=@l, Total=@t, Name=ISNULL(@n, Name), Lang=@g, Token=CASE WHEN SentAt IS NOT NULL OR DoneAt IS NOT NULL THEN @k ELSE Token END, SentAt=NULL, DoneAt=NULL, At=SYSUTCDATETIME() OUTPUT INSERTED.Token WHERE StoreId=@s AND Phone=@p", "@l", json, "@t", q.Total, "@n", Nz(name), "@g", x.Lang, "@k", token, "@s", x.Row.StoreId, "@p", phone))
            {
                var had = cmd.ExecuteScalar() as string;
                if (had != null) token = had;
                else try { Exec(c, "INSERT INTO dbo.ShopCarts(StoreId, Phone, Name, Lang, Lines, Total, Token) VALUES(@s, @p, @n, @g, @l, @t, @k)", "@s", x.Row.StoreId, "@p", phone, "@n", Nz(name), "@g", x.Lang, "@l", json, "@t", q.Total, "@k", token); } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
            }
            string items = string.Join("، ", q.Lines.Where(y => y.P != null).Take(3).Select(y => y.P.Name + (string.IsNullOrEmpty(y.V.Label) ? "" : " (" + y.V.Label + ")") + (y.Qty > 1 ? " × " + y.Qty : ""))) + (q.Lines.Count > 3 ? " و" + (q.Lines.Count - 3) + " كمان" : "");
            int delay = ShopJson.Int(ShopJson.At(Abandon(x.Row), "delay"), 60); if (!Carts.Delays.Contains(delay)) delay = 60;
            Carts.FromShop(c, x.Row, token, phone, name, items, ShopOrders.Money(q.Total), x.Prefix + "/cart?c=" + token, DateTime.UtcNow.AddMinutes(delay));
            Ok(res);
        }

        // ---------- «أخبار المتجر»: the number goes to the owner's own list for «ابعت عرض» ----------
        public static void News(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("shop-news:" + x.Ip, 5, TimeSpan.FromHours(1)) || !Web.Allow("shop-newsd:" + x.Row.StoreId, 200, TimeSpan.FromHours(24))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            var phone = ShopOrders.Mobile(Str(b, "phone", 24)); if (phone == null) throw new ShopException("phone", T(x, "اكتب رقم موبايل مصري صح.", "Enter a valid Egyptian mobile number."));
            if (!x.Preview) try { Exec(c, "UPDATE dbo.BcContacts SET Added=ISNULL(Added,'shop'), AddedAt=ISNULL(AddedAt, SYSUTCDATETIME()) WHERE StoreId=@s AND Phone=@p; IF @@ROWCOUNT=0 INSERT INTO dbo.BcContacts(StoreId, Phone, Added, AddedAt) VALUES(@s, @p, 'shop', SYSUTCDATETIME())", "@s", x.Row.StoreId, "@p", phone); } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
            Ok(res);
        }

        // ---------- the owner looks at the proof ----------
        public static void Review(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            var o = ShopOrders.ById(c, sh.StoreId, ShopJson.Int(ShopJson.At(b, "id"))); if (o == null) throw new ShopException("gone", "الأوردر ده مش موجود.");
            bool ok = ShopJson.Bool(ShopJson.At(b, "ok")); decimal amount = ShopJson.DecN(ShopJson.At(b, "amount")) ?? (o.Deposit > 0 ? o.Deposit : o.Total); string note = Str(b, "note", 200);
            if (ok && (amount <= 0 || amount > o.Total)) throw new ShopException("amount", "اكتب المبلغ اللي وصلك، من غير ما يزيد عن إجمالي الأوردر.");
            if (!ok && note.Length < 3) throw new ShopException("note", "اكتب للزبون المشكلة في التحويل.");
            if (ok) Exec(c, "UPDATE dbo.ShopOrders SET PayState='paid', Paid=@a, Seen=1, SheetAt=NULL WHERE Id=@i AND StoreId=@s", "@a", amount, "@i", o.Id, "@s", sh.StoreId); else Exec(c, "UPDATE dbo.ShopOrders SET PayState='bad', Paid=0, Seen=1 WHERE Id=@i AND StoreId=@s", "@i", o.Id, "@s", sh.StoreId);
            ShopOrders.Log_(c, null, sh.StoreId, o.Id, "owner", ok ? "التحويل وصل: " + ShopOrders.Money(amount) : "التحويل مش مظبوط: " + note);
            if (ok && o.Status == "new" && ShopJson.Bool(ShopJson.At(b, "confirm"), true)) ShopOrders.SetStatus(c, sh.StoreId, o.Id, "confirmed", "owner");
            string msg = o.Lang == "en" ? (ok ? "We received your transfer of EGP " + amount.ToString("#,0.##", CultureInfo.InvariantCulture) + " for order " + o.NoText + " ✅" + (o.Total > amount ? "\nLeft on delivery: EGP " + (o.Total - amount).ToString("#,0.##", CultureInfo.InvariantCulture) : "") : "About your transfer for order " + o.NoText + ": " + note + "\n" + sh.Url + "/pay/" + o.Token)
                : (ok ? "وصلنا تحويلك " + ShopOrders.Money(amount) + " لأوردر " + o.NoText + " ✅" + (o.Total > amount ? "\nالباقي عند الاستلام: " + ShopOrders.Money(o.Total - amount) : "") : "بخصوص تحويل أوردر " + o.NoText + ": " + note + "\nابعت الصورة من هنا: " + sh.Url + "/pay/" + o.Token);
            if (ShopJson.Bool(ShopJson.At(b, "tell"), true)) Customer(sh.StoreId, o.Phone, msg, "sb-shop-paid-" + o.Id + "-" + DateTime.UtcNow.Ticks);
        }
        static void Customer(int storeId, string phone, string text, string idem)
        {
            if (string.IsNullOrEmpty(phone)) return;
            HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { using (var c = Db.Open()) { var s = Db.StoreById(c, storeId); if (s != null) await WaNotify.ToCustomer(c, s, phone, text, idem); } } catch (Exception ex) { Log.Error(ex); } });
        }

        // ---------- when the order moves ----------
        // «اتشحن» with the tracking number
        public static async Task Shipped(SqlConnection c, StoreInfo s, Shop sh, ShopOrder o)
        {
            if (o.Phone.Length == 0 || !ShopJson.Bool(ShopJson.At(ShopJson.Sub(sh.Settings, "alerts"), "shipMsg"), true) || WaNotify.CantSend(c, s) != null || Billing.Expired(s)) return;
            bool en = o.Lang == "en", pick = o.Extras.ContainsKey("branch"); string name = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name; var t = new StringBuilder();
            if (pick) { var br = ShopJson.Sub(o.Extras, "branch"); t.Append(en ? "🛍️ Your order " : "🛍️ أوردرك ").Append(o.NoText).Append(en ? " from *" : " من *").Append(name).Append(en ? "* is ready to collect.\n📍 " : "* جاهز للاستلام.\n📍 ").Append(ShopJson.Str(ShopJson.At(br, "n"))).Append(" · ").Append(ShopJson.Str(ShopJson.At(br, "ad"))); }
            else
            {
                t.Append(en ? "📦 Your order " : "📦 أوردرك ").Append(o.NoText).Append(en ? " from *" : " من *").Append(name).Append(en ? "* has shipped." : "* اتشحن.");
                if (!string.IsNullOrEmpty(o.Tracking)) t.Append("\n").Append(en ? "Tracking number: *" : "رقم التتبع: *").Append(o.Tracking).Append("*").Append(string.IsNullOrEmpty(o.Carrier) ? "" : " (" + o.Carrier + ")");
                if (o.Due > 0 && o.Pay == "cod") t.Append("\n").Append(en ? "To pay the courier: *EGP " + o.Due.ToString("#,0.##", CultureInfo.InvariantCulture) + "*" : "هتدفع للمندوب: *" + ShopOrders.Money(o.Due) + "*");
                t.Append("\n").Append(en ? "Follow it: " : "تابعه من هنا: ").Append(sh.Url).Append("/track");
            }
            var why = await WaNotify.ToCustomer(c, s, o.Phone, t.ToString(), "sb-shop-ship-" + o.Id);
            if (why == null) ShopOrders.Log_(c, null, s.Id, o.Id, "system", pick ? "اتبعتله إن الأوردر جاهز للاستلام" : "رسالة «اتشحن» اتبعتت على الواتساب");
        }
        // «الكمية قربت تخلص»: once in three days for a size
        public static async Task Low(SqlConnection c, StoreInfo s, Shop sh, ShopOrder o)
        {
            var al = ShopJson.Sub(sh.Settings, "alerts"); if (!ShopJson.Bool(ShopJson.At(al, "low"), true) || string.IsNullOrEmpty(s.OwnerPhone)) return;
            int at = ShopJson.Int(ShopJson.At(al, "lowAt"), 3); var d = ShopCache.Get(c, sh); var l = new List<string>();
            foreach (var i in o.Items)
            {
                ShopVar v; if (!d.Vars.TryGetValue(i.VariantId, out v)) continue; var p = d.ById[v.ProductId];
                if (!p.Track || p.OutMode == "order" || v.Stock > at || !Web.Allow("shop-low:" + v.Id, 1, TimeSpan.FromDays(3))) continue;
                l.Add("• " + p.Name + (v.Label.Length > 0 ? " (" + v.Label + ")" : "") + ": " + (v.Stock <= 0 ? "خلص" : "باقي " + v.Stock));
            }
            if (l.Count > 0) await WaNotify.ToOwner(c, s, "📉 *منتجات قربت تخلص في متجرك*\n" + string.Join("\n", l.Take(12)) + "\n\nزوّد الكمية من «متجري» ثم «المنتجات».", "sb-shop-low-" + o.Id);
        }

        // ---------- the owner's Google Sheet ----------
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        public static bool SheetUrl(string u) { return Regex.IsMatch(u ?? "", @"^https://script\.google\.com/macros/s/[A-Za-z0-9_\-]{20,120}/exec$"); }
        static Dictionary<string, object> Row(Shop sh, ShopOrder o)
        {
            return new Dictionary<string, object> { { "order", o.No }, { "date", o.CreatedAt.AddHours(2).ToString("yyyy-MM-dd HH:mm") }, { "status", ShopOrders.StatusText(o.Status) }, { "name", o.Name }, { "phone", ShopOrders.Local(o.Phone) }, { "phone2", o.Phone2 == null ? "" : ShopOrders.Local(o.Phone2) }, { "governorate", ShopGeo.Name(o.Gov) }, { "city", o.City ?? "" },
                { "address", string.Join("، ", new[] { o.Area, o.Address, o.Landmark }.Where(y => !string.IsNullOrWhiteSpace(y))) }, { "items", string.Join(" | ", o.Items.Select(i => i.Name + (string.IsNullOrEmpty(i.Opts) ? "" : " (" + i.Opts + ")") + " × " + i.Qty)) }, { "products", o.Subtotal }, { "discount", o.Discount }, { "delivery", o.Ship }, { "fees", o.Fees }, { "total", o.Total }, { "paid", o.Paid }, { "payment", WayName(o.Pay) }, { "coupon", o.Coupon ?? "" }, { "tracking", o.Tracking ?? "" }, { "note", o.Note ?? "" }, { "source", o.Src ?? "" } };
        }
        // every order that is new or changed since it was last written, a few at a time (Jobs); the sheet's script finds the row by the order's number
        public static async Task Sheets(SqlConnection c, CancellationToken ct)
        {
            if (!Shops.Ready) return;
            var due = new List<KeyValuePair<int, long>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 60 o.StoreId, o.Id FROM dbo.ShopOrders o JOIN dbo.Shops s ON s.StoreId=o.StoreId WHERE s.Settings LIKE '%script.google.com/macros%' AND o.CreatedAt>DATEADD(DAY,-30,SYSUTCDATETIME()) AND (o.SheetAt IS NULL OR o.SheetAt<o.StatusAt) AND (o.SheetTry IS NULL OR o.SheetTry<DATEADD(MINUTE,-30,SYSUTCDATETIME())) ORDER BY o.Id")) using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new KeyValuePair<int, long>((int)r[0], Convert.ToInt64(r[1])));
            foreach (var g in due.GroupBy(k => k.Key))
            {
                var sh = Shops.ByStore(c, g.Key); string url = sh == null ? null : ShopJson.Str(ShopJson.At(ShopJson.Sub(sh.Settings, "sheet"), "url"), 200); if (!SheetUrl(url)) continue;
                var ids = g.Select(k => k.Value).ToList(); string why = await Send(url, sh, ids.Select(i => ShopOrders.ById(c, g.Key, i)).Where(o => o != null).ToList(), ct);
                Exec(c, why == null ? "UPDATE dbo.ShopOrders SET SheetAt=SYSUTCDATETIME(), SheetTry=NULL WHERE Id IN (" + string.Join(",", ids) + ")" : "UPDATE dbo.ShopOrders SET SheetTry=SYSUTCDATETIME() WHERE Id IN (" + string.Join(",", ids) + ")");
                if (why != null) ShopOrders.Fail(g.Key, "sheet", why);
                if (ct.IsCancellationRequested) return;
            }
        }
        static async Task<string> Send(string url, Shop sh, List<ShopOrder> list, CancellationToken ct)
        {
            try
            {
                var body = new StringContent(ShopJson.Write(new Dictionary<string, object> { { "store", sh.Name }, { "orders", list.Select(o => Row(sh, o)).ToArray() } }), Encoding.UTF8, "application/json");
                using (var r = await Http.PostAsync(url, body, ct)) { var t = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode && t.Contains("ok") ? null : "Google Sheets: " + (int)r.StatusCode + " " + (t.Length > 120 ? t.Substring(0, 120) : t); }
            }
            catch (Exception ex) { return "Google Sheets: " + ex.GetType().Name; }
        }
        // «جرّب»: one line to the sheet now
        public static Func<Task> Try(SqlConnection c, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            string url = Str(b, "url", 200); if (!SheetUrl(url)) throw new ShopException("url", "اللينك لازم يكون لينك Apps Script بالشكل: https://script.google.com/macros/s/.../exec");
            return async () =>
            {
                var o = new ShopOrder { No = 0, Name = "أوردر تجربة", Phone = "201000000000", Gov = 1, City = "تجربة", Address = "تجربة", Status = "new", Pay = "cod", CreatedAt = DateTime.UtcNow, StatusAt = DateTime.UtcNow };
                string why = await Send(url, sh, new List<ShopOrder> { o }, CancellationToken.None);
                Web.Json(res, 200, new Dictionary<string, object> { { "ok", why == null }, { "why", why ?? "" } });
            };
        }
    }
}
