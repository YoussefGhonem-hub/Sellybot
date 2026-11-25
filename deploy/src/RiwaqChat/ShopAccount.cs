// A customer's account on a store (<name>.sellystore.online/account): their mobile and a code on WhatsApp from the store's own number, no
// password. Their orders, cancelling or changing the address before it ships, asking for a return or an exchange, rating what they
// received, addresses, codes. A store that can't send WhatsApp has no sign-in: the phone that placed an order proves it with the order's
// secret token (the one in its thank-you link), so «طلباتي» works there too. Everything is POST /_a/me {do, tk, toks}.
// This file also keeps the code a store may ask for before an order (order.otp), «بلّغني لما يرجع», the numbers of what was searched and
// seen, and the tables of the features that came after the first version (ShopMore.Schema).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
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
    public static class ShopAccount
    {
        const int CodeMinutes = 10, SessionDays = 120, MaxAddr = 5, MaxTries = 5;

        static Dictionary<string, object> Set(Shop sh, string key) { return ShopJson.Sub(sh.Settings, key); }
        public static bool On(Shop sh) { return ShopJson.Bool(ShopJson.At(Set(sh, "acct"), "on"), true); }
        public static bool ReturnsOn(Shop sh) { return ShopJson.Bool(ShopJson.At(Set(sh, "returns"), "on"), true); }
        public static bool ReviewsOn(Shop sh) { return ShopJson.Bool(ShopJson.At(Set(sh, "reviews"), "on"), true); }
        public static bool CancelOn(Shop sh) { return ShopJson.Bool(ShopJson.At(Set(sh, "order"), "selfCancel"), true); }
        public static int ReturnDays(Shop sh) { return Math.Max(14, ShopJson.Int(ShopJson.At(sh.Settings, "returnsDays"), 14)); }

        static string T(ShopFrontHandler.Ctx x, string ar, string en) { return x.Lang == "en" ? en : ar; }
        static string Str(Dictionary<string, object> b, string k, int n) { return ShopJson.Str(ShopJson.At(b, k), n); }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        static string Cut(string s, int n) { return s == null ? null : s.Length > n ? s.Substring(0, n) : s; }
        static string Sha(string s) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2"))); }
        static string Digits6() { var b = new byte[4]; using (var g = RandomNumberGenerator.Create()) g.GetBytes(b); return (BitConverter.ToUInt32(b, 0) % 1000000).ToString("000000"); }
        static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        static int Num(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v); } }

        // ---------- codes on WhatsApp ----------
        // kind: login / order. null = on its way, otherwise why not (for the customer)
        static string SendCode(SqlConnection c, ShopFrontHandler.Ctx x, string phone, string kind)
        {
            var hour = TimeSpan.FromHours(1);
            if (!Web.Allow("shop-code:" + x.Row.StoreId + ":" + phone, 4, hour) || !Web.Allow("shop-codei:" + x.Ip, 12, hour) || !Web.Allow("shop-codes:" + x.Row.StoreId, 200, hour)) return T(x, "طلبت أكواد كتير. استنى ساعة وجرّب تاني.", "Too many codes were asked for. Please try again in an hour.");
            string code = Digits6(); int id;
            Exec(c, "UPDATE dbo.ShopCodes SET UsedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Phone=@p AND Kind=@k AND UsedAt IS NULL", "@s", x.Row.StoreId, "@p", phone, "@k", kind);
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopCodes(StoreId, Phone, Kind, Hash, ExpiresAt) OUTPUT INSERTED.Id VALUES(@s, @p, @k, @h, DATEADD(MINUTE, @m, SYSUTCDATETIME()))", "@s", x.Row.StoreId, "@p", phone, "@k", kind, "@h", Sha(x.Row.StoreId + ":" + phone + ":" + code), "@m", CodeMinutes)) id = (int)cmd.ExecuteScalar();
            string name = x.Lang == "en" && !string.IsNullOrEmpty(x.Row.NameEn) ? x.Row.NameEn : x.Row.Name;
            string text = x.Lang == "en" ? "Your code for *" + name + "*: *" + code + "*\nIt works for " + CodeMinutes + " minutes. Don't share it with anyone." : "كودك في *" + name + "*: *" + code + "*\nصالح " + CodeMinutes + " دقايق، وماتديهوش لحد.";
            int sid = x.Row.StoreId;
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    using (var c2 = Db.Open())
                    {
                        var s = Db.StoreById(c2, sid); var why = WaNotify.CantSend(c2, s);
                        if (why == null) try { await Wapilot.Send(WaAccount.Token(c2, s), s.WaInstance, phone + "@c.us", text, "sb-shop-code-" + id); } catch (WaException ex) { why = ex.Message; }
                        if (why != null) Exec(c2, "UPDATE dbo.ShopCodes SET Fail=@f WHERE Id=@i", "@f", Cut(why, 200), "@i", id);
                    }
                }
                catch (Exception ex) { Log.Error(ex); }
            });
            return null;
        }
        static bool CheckCode(SqlConnection c, int storeId, string phone, string kind, string code)
        {
            code = Regex.Replace(ShopJson.Digits(code ?? ""), @"\D", ""); if (code.Length != 6) return false;
            int id = 0, tries = 0; string hash = null;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id, Hash, Tries FROM dbo.ShopCodes WHERE StoreId=@s AND Phone=@p AND Kind=@k AND UsedAt IS NULL AND ExpiresAt>SYSUTCDATETIME() ORDER BY Id DESC", "@s", storeId, "@p", phone, "@k", kind))
            using (var r = cmd.ExecuteReader()) { if (!r.Read()) return false; id = (int)r[0]; hash = (string)r[1]; tries = (int)r[2]; }
            if (tries >= MaxTries) return false;
            bool ok = Shops.SameKey(hash, Sha(storeId + ":" + phone + ":" + code));
            // one use, and a few tries: counted in the same statement, so two requests at once don't get one more try
            using (var cmd = Db.Cmd(c, ok ? "UPDATE dbo.ShopCodes SET UsedAt=SYSUTCDATETIME() WHERE Id=@i AND UsedAt IS NULL" : "UPDATE dbo.ShopCodes SET Tries=Tries+1 WHERE Id=@i", "@i", id)) { int n = cmd.ExecuteNonQuery(); return ok && n == 1; }
        }

        // ---------- the code before an order (the owner turns it on) ----------
        public static bool OtpNeeded(SqlConnection c, StoreInfo s, Shop sh, string phone)
        {
            if (!ShopJson.Bool(ShopJson.At(Set(sh, "order"), "otp"))) return false;
            if (WaNotify.CantSend(c, s) != null) return false;   // the number isn't linked any more: never stop the store's orders
            // a number that proved itself here in the last 60 days isn't asked again
            return Num(c, "SELECT COUNT(*) FROM dbo.ShopCustomers WHERE StoreId=@s AND Phone=@p AND ProvedAt>DATEADD(DAY,-60,SYSUTCDATETIME())", "@s", sh.StoreId, "@p", phone) == 0;
        }
        static void Prove(SqlConnection c, int storeId, string phone)
        {
            Exec(c, @"UPDATE dbo.ShopCustomers SET ProvedAt=SYSUTCDATETIME(), LastAt=SYSUTCDATETIME() WHERE StoreId=@s AND Phone=@p;
                IF @@ROWCOUNT=0 INSERT INTO dbo.ShopCustomers(StoreId, Phone, Name, ProvedAt) VALUES(@s, @p, (SELECT TOP 1 Name FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p ORDER BY Id DESC), SYSUTCDATETIME())", "@s", storeId, "@p", phone);
        }
        // the checkout: {do:"otp", phone} sends it; the order carries the code
        public static bool CheckOtp(SqlConnection c, int storeId, string phone, string code) { if (!CheckCode(c, storeId, phone, "order", code)) return false; Prove(c, storeId, phone); return true; }

        // ---------- who is asking ----------
        static string Who(SqlConnection c, int storeId, string token)
        {
            if (!Regex.IsMatch(token ?? "", "^[a-f0-9]{64}$")) return null;
            string h = Sha(token), phone = null; DateTime last = DateTime.MinValue;
            using (var cmd = Db.Cmd(c, "SELECT Phone, LastAt FROM dbo.ShopSessions WHERE Hash=@h AND StoreId=@s AND ExpiresAt>SYSUTCDATETIME()", "@h", h, "@s", storeId)) using (var r = cmd.ExecuteReader()) if (r.Read()) { phone = (string)r[0]; last = (DateTime)r[1]; }
            if (phone != null && (DateTime.UtcNow - last).TotalHours > 12) Exec(c, "UPDATE dbo.ShopSessions SET LastAt=SYSUTCDATETIME(), ExpiresAt=DATEADD(DAY, @d, SYSUTCDATETIME()) WHERE Hash=@h; UPDATE dbo.ShopCustomers SET LastAt=SYSUTCDATETIME() WHERE StoreId=@s AND Phone=@p", "@d", SessionDays, "@h", h, "@s", storeId, "@p", phone);
            return phone;
        }
        static List<string> Tokens(Dictionary<string, object> b) { return ShopJson.List(ShopJson.At(b, "toks")).Select(t => ShopJson.Str(t, 40)).Where(t => Regex.IsMatch(t, "^[a-f0-9]{32}$")).Distinct().Take(20).ToList(); }
        // an order this person may see: theirs by the number they proved, or by its secret token
        static ShopOrder Mine(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, string phone)
        {
            int no = ShopJson.Int(ShopJson.At(b, "no")); string tok = Str(b, "tok", 40); ShopOrder o = null;
            if (Regex.IsMatch(tok, "^[a-f0-9]{32}$")) o = ShopOrders.ByToken(c, x.Row.StoreId, tok);
            else if (no > 0) { o = ShopOrders.ByNo(c, x.Row.StoreId, no); if (o != null && !(phone != null && o.Phone == phone) && !Tokens(b).Contains(o.Token)) o = null; }
            if (o == null || o.Test) throw new ShopException("no_order", T(x, "الطلب ده مش موجود.", "This order isn't here."));
            return o;
        }

        public static void Run(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            string a = Str(b, "do", 20); int sid = x.Row.StoreId;
            if (!On(x.Row) && a != "otp" && a != "order" && a != "review" && a != "pic") throw new ShopException("off", T(x, "الحسابات مقفولة في المتجر ده.", "Accounts are off in this store."));
            if (x.Preview) { Preview(a, x, res); return; }   // the preview never reads a real customer
            string phone = Who(c, sid, Str(b, "tk", 80));
            switch (a)
            {
                case "start":
                    {
                        var p = ShopOrders.Mobile(Str(b, "phone", 24)); if (p == null) throw new ShopException("phone", T(x, "اكتب رقم موبايل مصري صح.", "Enter a valid Egyptian mobile number."));
                        if (WaNotify.CantSend(c, x.Store) != null) throw new ShopException("no_wa", T(x, "الدخول بالكود مش متاح في المتجر ده دلوقتي. تابع طلبك برقمه من «طلبي فين؟».", "Signing in with a code isn't available here right now. Use “Track my order” with your order number."));
                        var why = SendCode(c, x, p, "login"); if (why != null) throw new ShopException("too_many", why);
                        Ok(res, "sent", true); return;
                    }
                case "otp":
                    {
                        var p = ShopOrders.Mobile(Str(b, "phone", 24)); if (p == null) throw new ShopException("phone", T(x, "اكتب رقم موبايل مصري صح.", "Enter a valid Egyptian mobile number."));
                        if (!OtpNeeded(c, x.Store, x.Row, p)) { Ok(res, "need", false); return; }
                        var why = SendCode(c, x, p, "order"); if (why != null) throw new ShopException("too_many", why);
                        Ok(res, "need", true, "sent", true); return;
                    }
                case "verify":
                    {
                        var p = ShopOrders.Mobile(Str(b, "phone", 24));
                        if (!Web.Allow("shop-ver:" + sid + ":" + x.Ip, 20, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
                        if (p == null || !CheckCode(c, sid, p, "login", Str(b, "code", 12))) throw new ShopException("code", T(x, "الكود غلط أو وقته خلص. راجعه أو اطلب كود جديد.", "The code is wrong or has expired. Check it or ask for a new one."));
                        Prove(c, sid, p);
                        string tk = Shops.Token(32);
                        Exec(c, "INSERT INTO dbo.ShopSessions(Hash, StoreId, Phone, ExpiresAt) VALUES(@h, @s, @p, DATEADD(DAY, @d, SYSUTCDATETIME())); DELETE FROM dbo.ShopSessions WHERE StoreId=@s AND Phone=@p AND Hash NOT IN (SELECT TOP 10 Hash FROM dbo.ShopSessions WHERE StoreId=@s AND Phone=@p ORDER BY LastAt DESC)", "@h", Sha(tk), "@s", sid, "@p", p, "@d", SessionDays);
                        Ok(res, "tk", tk, "me", Me_(c, x, p)); return;
                    }
                case "out": if (phone != null) Exec(c, "DELETE FROM dbo.ShopSessions WHERE Hash=@h AND StoreId=@s", "@h", Sha(Str(b, "tk", 80)), "@s", sid); Ok(res); return;
                case "me": if (phone == null) { Ok(res, "me", null, "wa", WaNotify.CantSend(c, x.Store) == null); return; } Ok(res, "me", Me_(c, x, phone)); return;
                case "orders": Ok(res, "orders", Orders(c, x, b, phone)); return;
                case "order": Ok(res, "order", OrderJson(c, x, Mine(c, x, b, phone))); return;
                case "cancel": Cancel(c, x, b, phone, res); return;
                case "edit": Edit(c, x, b, phone, res); return;
                case "pic": Pic(c, x, b, phone, res); return;
                case "return": ShopReturns.Ask(c, x, b, Mine(c, x, b, phone), res); return;
                case "return-off": ShopReturns.CustomerOff(c, x, b, Mine(c, x, b, phone), res); return;
                case "review": ShopReviews.Add(c, x, b, Mine(c, x, b, phone), res); return;
            }
            if (phone == null) throw new ShopException("signin", T(x, "سجّل دخول الأول.", "Please sign in first."));
            switch (a)
            {
                case "save":
                    {
                        var n = Regex.Replace(Str(b, "name", 80), @"[\u0000-\u001F]", ""); if (n.Length < 2) throw new ShopException("name", T(x, "اكتب اسمك.", "Enter your name."));
                        Exec(c, "UPDATE dbo.ShopCustomers SET Name=@n, News=@w WHERE StoreId=@s AND Phone=@p", "@n", n, "@w", ShopJson.Bool(ShopJson.At(b, "news")), "@s", sid, "@p", phone);
                        Ok(res, "me", Me_(c, x, phone)); return;
                    }
                case "addr": SaveAddr(c, x, b, phone); Ok(res, "me", Me_(c, x, phone)); return;
                case "codes": Ok(res, "codes", Codes(c, x, phone)); return;
                case "returns": Ok(res, "returns", ShopReturns.OfCustomer(c, x, phone)); return;
                case "wipe": Wipe(c, x, phone, res); return;
                default: Web.Error(res, 404, "not_found"); return;
            }
        }
        static void Ok(HttpResponse res, params object[] kv) { var d = new Dictionary<string, object> { { "ok", true } }; for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; Web.Json(res, 200, d); }

        static void Preview(string a, ShopFrontHandler.Ctx x, HttpResponse res)
        {
            var me = new Dictionary<string, object> { { "name", T(x, "زبون للتجربة", "A test customer") }, { "phone", "01000000000" }, { "addr", new object[0] }, { "news", false }, { "orders", 0 }, { "open", 0 }, { "test", true } };
            if (a == "start") Ok(res, "sent", true, "test", true);
            else if (a == "verify") Ok(res, "tk", new string('0', 64), "me", me);
            else if (a == "me") Ok(res, "me", null, "wa", true);
            else if (a == "otp") Ok(res, "need", false);
            else if (a == "orders") Ok(res, "orders", new object[0]);
            else if (a == "returns") Ok(res, "returns", new object[0]);
            else if (a == "codes") Ok(res, "codes", new object[0]);
            else throw new ShopException("preview", T(x, "المعاينة مابتحفظش حاجة.", "The preview saves nothing."));
        }

        // ---------- the person ----------
        static Dictionary<string, object> Me_(SqlConnection c, ShopFrontHandler.Ctx x, string phone)
        {
            string name = "", addr = null; bool news = false;
            using (var cmd = Db.Cmd(c, "SELECT Name, Addr, News FROM dbo.ShopCustomers WHERE StoreId=@s AND Phone=@p", "@s", x.Row.StoreId, "@p", phone)) using (var r = cmd.ExecuteReader()) if (r.Read()) { name = r[0] as string ?? ""; addr = r[1] as string; news = (bool)r[2]; }
            int n = 0, open = 0; using (var cmd = Db.Cmd(c, "SELECT COUNT(*), ISNULL(SUM(CASE WHEN Status IN ('new','noreply','confirmed','shipped','out') THEN 1 ELSE 0 END),0) FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p", "@s", x.Row.StoreId, "@p", phone)) using (var r = cmd.ExecuteReader()) { r.Read(); n = (int)r[0]; open = (int)r[1]; }
            return new Dictionary<string, object> { { "name", name }, { "phone", ShopOrders.Local(phone) }, { "addr", addr == null ? FirstAddr(c, x.Row.StoreId, phone) : ShopJson.Arr(addr) }, { "news", news }, { "orders", n }, { "open", open }, { "wa", true } };
        }
        // nothing saved yet: the address of their last order is offered
        static List<object> FirstAddr(SqlConnection c, int storeId, string phone)
        {
            var l = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Gov, City, Area, Address, Landmark FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p ORDER BY Id DESC", "@s", storeId, "@p", phone)) using (var r = cmd.ExecuteReader())
                if (r.Read()) l.Add(new Dictionary<string, object> { { "id", 1 }, { "t", "" }, { "gov", (int)r[0] }, { "city", r[1] as string ?? "" }, { "area", r[2] as string ?? "" }, { "ad", (string)r[3] }, { "lm", r[4] as string ?? "" }, { "def", true } });
            return l;
        }
        static void SaveAddr(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, string phone)
        {
            var l = new List<object>(); int id = 1; bool def = false;
            foreach (var a in ShopJson.List(ShopJson.At(b, "list")).OfType<Dictionary<string, object>>().Take(MaxAddr))
            {
                int g = ShopJson.Int(ShopJson.At(a, "gov")); string city = Str(a, "city", 60), ad = Str(a, "ad", 300);
                if (ShopGeo.Get(g) == null || city.Length < 2 || ad.Length < 5) throw new ShopException("addr", T(x, "كمّل العنوان: المحافظة والمدينة والشارع.", "Complete the address: governorate, city and street."));
                bool d = ShopJson.Bool(ShopJson.At(a, "def")) && !def; def |= d;
                l.Add(new Dictionary<string, object> { { "id", id++ }, { "t", Str(a, "t", 30) }, { "gov", g }, { "city", city }, { "area", Str(a, "area", 80) }, { "ad", ad }, { "lm", Str(a, "lm", 120) }, { "def", d } });
            }
            if (!def && l.Count > 0) ((Dictionary<string, object>)l[0])["def"] = true;
            Exec(c, "UPDATE dbo.ShopCustomers SET Addr=@a WHERE StoreId=@s AND Phone=@p", "@a", ShopJson.Write(l), "@s", x.Row.StoreId, "@p", phone);
        }

        // the codes the owner chose to show to customers, without the ones this number finished
        public static object[] Codes(SqlConnection c, ShopFrontHandler.Ctx x, string phone)
        {
            var l = new List<object>();
            using (var cmd = Db.Cmd(c, @"SELECT k.*, (SELECT COUNT(*) FROM dbo.ShopOrders o WHERE o.StoreId=k.StoreId AND o.Coupon=k.Code AND o.Phone=@p AND o.Status<>'cancelled') AS Mine, (SELECT COUNT(*) FROM dbo.ShopOrders o WHERE o.StoreId=k.StoreId AND o.Phone=@p AND o.Status<>'cancelled') AS Had
                FROM dbo.ShopCoupons k WHERE k.StoreId=@s AND k.Shown=1 AND k.Active=1 AND (k.EndsAt IS NULL OR k.EndsAt>SYSUTCDATETIME()) AND (k.StartsAt IS NULL OR k.StartsAt<=SYSUTCDATETIME()) AND (k.MaxUses IS NULL OR k.Uses<k.MaxUses) ORDER BY k.Id DESC", "@s", x.Row.StoreId, "@p", phone ?? ""))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    int mine = (int)r["Mine"], had = (int)r["Had"]; var k = ShopPrice.ReadCoupon(r);
                    bool used = (k.PerPhone.HasValue && mine >= k.PerPhone.Value) || (k.FirstOnly && had > 0);
                    l.Add(new Dictionary<string, object> { { "code", k.Code }, { "type", k.Type }, { "value", k.Value }, { "max", k.MaxOff ?? 0 }, { "min", k.MinSub ?? 0 }, { "first", k.FirstOnly }, { "until", k.EndsAt.HasValue ? ShopView.Unix(k.EndsAt.Value) : 0 }, { "used", used } });
                }
            return l.ToArray();
        }

        // ---------- orders ----------
        static object[] Orders(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, string phone)
        {
            var toks = Tokens(b); var list = new List<ShopOrder>(); var ids = new List<long>();
            if (phone != null) using (var cmd = Db.Cmd(c, "SELECT TOP 60 Id FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p ORDER BY Id DESC", "@s", x.Row.StoreId, "@p", phone)) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Convert.ToInt64(r[0]));
            foreach (var id in ids) { var o = ShopOrders.ById(c, x.Row.StoreId, id); if (o != null) list.Add(o); }
            foreach (var t in toks) { var o = ShopOrders.ByToken(c, x.Row.StoreId, t); if (o != null && !list.Any(y => y.Id == o.Id)) list.Add(o); }
            return list.OrderByDescending(o => o.Id).Select(o => (object)ShopOrders.CustomerJson(o, false)).ToArray();
        }
        static bool Early(string st) { return st == "new" || st == "noreply" || st == "confirmed"; }
        public static bool CanReturn(Shop sh, ShopOrder o) { return ReturnsOn(sh) && o.Status == "delivered" && o.DeliveredAt.HasValue && (DateTime.UtcNow - o.DeliveredAt.Value).TotalDays <= ReturnDays(sh); }
        static Dictionary<string, object> OrderJson(SqlConnection c, ShopFrontHandler.Ctx x, ShopOrder o)
        {
            var d = ShopOrders.CustomerJson(o, true);
            d["tok"] = o.Token; d["area"] = o.Area ?? ""; d["lm"] = o.Landmark ?? ""; d["note"] = o.Note ?? ""; d["phone2"] = o.Phone2 == null ? "" : ShopOrders.Local(o.Phone2); d["fullName"] = o.Name; d["coupon"] = o.Coupon ?? ""; d["reason"] = o.CancelReason ?? "";
            d["log"] = new Dictionary<string, object> { { "confirmed", o.ConfirmedAt.HasValue ? ShopView.Unix(o.ConfirmedAt.Value) : 0 }, { "shipped", o.ShippedAt.HasValue ? ShopView.Unix(o.ShippedAt.Value) : 0 }, { "delivered", o.DeliveredAt.HasValue ? ShopView.Unix(o.DeliveredAt.Value) : 0 } };
            d["can"] = new Dictionary<string, object> { { "cancel", CancelOn(x.Row) && Early(o.Status) }, { "edit", CancelOn(x.Row) && Early(o.Status) }, { "ret", CanReturn(x.Row, o) && ShopReturns.Left(c, o).Count > 0 }, { "review", ReviewsOn(x.Row) && o.Status == "delivered" } };
            d["rids"] = o.Items.Select(i => i.Id).ToArray(); d["vids"] = o.Items.Select(i => i.VariantId).ToArray();
            d["rated"] = ShopReviews.Rated(c, o.Id); d["returns"] = ShopReturns.OfOrder(c, o.Id); d["left"] = ShopReturns.Left(c, o);
            return d;
        }
        static void Cancel(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, string phone, HttpResponse res)
        {
            var o = Mine(c, x, b, phone);
            if (!CancelOn(x.Row) || !Early(o.Status)) throw new ShopException("late", T(x, "الطلب خرج للشحن، كلّم المتجر عشان الإلغاء.", "It has already shipped. Message the store to cancel it."));
            if (!Web.Allow("shop-cxl:" + x.Ip, 20, TimeSpan.FromHours(1))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            var why = Regex.Replace(Str(b, "reason", 120), @"[\u0000-\u001F]", "");
            var e = ShopOrders.SetStatus(c, x.Row.StoreId, o.Id, "cancelled", "customer", "الزبون لغاه من حسابه" + (why.Length > 0 ? ": " + why : "")); if (e != null) throw new ShopException("status", T(x, e, "The order changed a moment ago. Reload and try again."));
            ShopHooks.Tell(x.Row.StoreId, "cancel", "❌ *زبون لغى طلبه* " + o.NoText + "\n" + o.Name + " · " + ShopOrders.Local(o.Phone) + " · " + ShopOrders.Money(o.Total) + (why.Length > 0 ? "\nالسبب: " + why : ""), "sb-shop-cxl-" + o.Id);
            Ok(res, "order", OrderJson(c, x, ShopOrders.ById(c, x.Row.StoreId, o.Id)));
        }
        // the address, the second number and the note; never what was bought (its price was agreed on)
        static void Edit(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, string phone, HttpResponse res)
        {
            var o = Mine(c, x, b, phone);
            if (!CancelOn(x.Row) || !Early(o.Status)) throw new ShopException("late", T(x, "الطلب خرج للشحن، كلّم المتجر عشان التعديل.", "It has already shipped. Message the store to change it."));
            if (!Web.Allow("shop-edit:" + x.Ip, 20, TimeSpan.FromHours(1))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            int gov = ShopJson.Int(ShopJson.At(b, "gov"), o.Gov); string city = Str(b, "city", 60), area = Str(b, "area", 80), ad = Str(b, "ad", 300), lm = Str(b, "lm", 120), note = Str(b, "note", 400), p2raw = Str(b, "phone2", 24); var p2 = p2raw.Length == 0 ? null : ShopOrders.Mobile(p2raw);
            if (ShopGeo.Get(gov) == null || city.Length < 2 || ad.Length < 5) throw new ShopException("addr", T(x, "كمّل العنوان: المحافظة والمدينة والشارع.", "Complete the address: governorate, city and street."));
            if (p2raw.Length > 0 && p2 == null) throw new ShopException("phone2", T(x, "الرقم التاني مش صح", "The second number isn't valid"));
            decimal ship = o.Ship;
            if (gov != o.Gov)
            {
                // the delivery of the new place, worked out on the same items; what was free by a code or an offer stays free
                var q = ShopPrice.Quote(c, x.Data, o.Items.Select(i => new ShopLine { VariantId = i.VariantId, Qty = i.Qty }).ToList(), null, gov, o.Phone, x.Now, false);
                if (q.Stop == "nodelivery") throw new ShopException("nodelivery", T(x, "للأسف مابنوصّلش لمحافظة " + ShopGeo.Name(gov) + " دلوقتي.", "Sorry, we don't deliver there right now."));
                if (o.Ship > 0) ship = q.Ship;
            }
            decimal total = o.Subtotal - o.Discount + ship + o.Fees; bool more = Math.Abs(total - o.Total) >= 1;
            if (more && !ShopJson.Bool(ShopJson.At(b, "ok"))) { Ok(res, "confirm", true, "ship", ship, "total", total); return; }   // the new total is shown first
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Gov=@g, City=@ci, Area=@ar, Address=@ad, Landmark=@lm, Note=@no, Phone2=@p2, Ship=@sh, Total=@t, Seen=0 WHERE Id=@i AND StoreId=@s AND Status IN ('new','noreply','confirmed')",
                "@g", gov, "@ci", city, "@ar", Nz(area), "@ad", ad, "@lm", Nz(lm), "@no", Nz(note), "@p2", p2, "@sh", ship, "@t", total, "@i", o.Id, "@s", x.Row.StoreId)) if (cmd.ExecuteNonQuery() == 0) throw new ShopException("late", T(x, "الطلب اتحرّك حالًا. حدّث الصفحة.", "The order has just moved on. Reload the page."));
            ShopOrders.Log_(c, null, x.Row.StoreId, o.Id, "customer", "الزبون عدّل العنوان" + (more ? " (الإجمالي بقى " + ShopOrders.Money(total) + ")" : ""));
            ShopHooks.OnEdit(c, x.Row.StoreId, o.Id);
            ShopHooks.Tell(x.Row.StoreId, "edit", "✏️ *زبون عدّل طلبه* " + o.NoText + "\nالعنوان الجديد: " + Cut(ShopGeo.Name(gov) + "، " + city + "، " + ad, 200) + (more ? "\nالإجمالي: " + ShopOrders.Money(total) : ""), "sb-shop-ed-" + o.Id + "-" + DateTime.UtcNow.Ticks);
            Ok(res, "order", OrderJson(c, x, ShopOrders.ById(c, x.Row.StoreId, o.Id)));
        }

        // a customer's picture (a return, a rating, a transfer): shrunk on their phone, and only from someone with an order or a session
        static void Pic(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, string phone, HttpResponse res)
        {
            if (phone == null) Mine(c, x, b, null);
            if (!Web.Allow("shop-pic:" + x.Ip, 20, TimeSpan.FromHours(1)) || !Web.Allow("shop-pics:" + x.Row.StoreId, 300, TimeSpan.FromHours(24))) throw new ShopException("too_many", T(x, "صور كتير. استنى شوية.", "Too many pictures. Please wait a while."));
            Ok(res, "path", Upload(c, x, b));
        }
        public static string Upload(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b)
        {
            try { return (string)ShopImages.Save(c, x.Row.StoreId, null, Str(b, "d", 560000), Str(b, "s", 160000), ShopJson.Int(ShopJson.At(b, "w")), ShopJson.Int(ShopJson.At(b, "h")), "customer")["path"]; }
            catch (ShopException ex) when (ex.Code == "full") { throw new ShopException("full", T(x, "مش قادرين نستقبل صور دلوقتي. ابعتها للمتجر على واتساب.", "Pictures can't be received right now. Please send it on WhatsApp.")); }
            catch (ShopException) { throw new ShopException("bad_image", T(x, "الصورة دي مش مدعومة.", "This picture isn't supported.")); }
        }
        // one picture a customer sent, inside the order's own transaction
        public static string PicIn(SqlConnection c, SqlTransaction tx, int storeId, string path)
        {
            path = ShopJson.Str(path, 160); if (!path.StartsWith(storeId + "/", StringComparison.Ordinal)) return null;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopImages SET Used=1 WHERE StoreId=@s AND Path=@p AND Alt='customer'", "@s", storeId, "@p", path)) { cmd.Transaction = tx; return cmd.ExecuteNonQuery() == 1 ? path : null; }
        }
        // only pictures this store's customers sent
        public static List<string> Pics(SqlConnection c, int storeId, object list, int max)
        {
            var l = new List<string>();
            foreach (var p in ShopJson.List(list).Select(y => ShopJson.Str(y, 160)).Where(y => y.StartsWith(storeId + "/", StringComparison.Ordinal)).Distinct().Take(max))
                if (Num(c, "SELECT COUNT(*) FROM dbo.ShopImages WHERE StoreId=@s AND Path=@p AND Alt='customer'", "@s", storeId, "@p", p) > 0) { l.Add(p); Exec(c, "UPDATE dbo.ShopImages SET Used=1 WHERE StoreId=@s AND Path=@p", "@s", storeId, "@p", p); }
            return l;
        }

        // «امسح بياناتي»: the owner is told and does it (the law: within a reasonable time; the numbers of the accounts stay without a name)
        static void Wipe(SqlConnection c, ShopFrontHandler.Ctx x, string phone, HttpResponse res)
        {
            if (Num(c, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p AND Status IN ('new','noreply','confirmed','shipped','out')", "@s", x.Row.StoreId, "@p", phone) > 0) throw new ShopException("open", T(x, "عندك طلب لسه ماخلصش. بعد ما يخلص اطلب المسح.", "You have an order in progress. Ask again when it's done."));
            Exec(c, "UPDATE dbo.ShopCustomers SET WipeAskedAt=ISNULL(WipeAskedAt, SYSUTCDATETIME()) WHERE StoreId=@s AND Phone=@p", "@s", x.Row.StoreId, "@p", phone);
            ShopHooks.Tell(x.Row.StoreId, "wipe", "🗑️ *زبون طلب مسح بياناته*\n" + ShopOrders.Local(phone) + "\nامسحها من «متجري» ثم «الزباين».", "sb-shop-wipe-" + x.Row.StoreId + "-" + phone);
            Ok(res, "asked", true);
        }
        // the owner's press: the person goes, the orders' money stays
        public static string WipeNow(SqlConnection c, int storeId, string phone)
        {
            if (Num(c, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p AND Status IN ('new','noreply','confirmed','shipped','out')", "@s", storeId, "@p", phone) > 0) return "الزبون ده عنده طلب لسه ماخلصش.";
            Exec(c, @"UPDATE dbo.ShopOrders SET Name=N'زبون اتمسحت بياناته', Phone='', Phone2=NULL, City=NULL, Area=NULL, Address=N'—', Landmark=NULL, Note=NULL, StaffNote=NULL, Ip=NULL, Device=NULL WHERE StoreId=@s AND Phone=@p;
                UPDATE dbo.ShopReviews SET Name=N'زبون', Phone='' WHERE StoreId=@s AND Phone=@p;
                DELETE FROM dbo.ShopReturns WHERE StoreId=@s AND Phone=@p; DELETE FROM dbo.ShopSessions WHERE StoreId=@s AND Phone=@p; DELETE FROM dbo.ShopCodes WHERE StoreId=@s AND Phone=@p; DELETE FROM dbo.ShopConfirms WHERE StoreId=@s AND Phone=@p;
                DELETE FROM dbo.ShopLeads WHERE StoreId=@s AND Phone=@p; DELETE FROM dbo.ShopCustomers WHERE StoreId=@s AND Phone=@p; DELETE FROM dbo.ShopCarts WHERE StoreId=@s AND Phone=@p", "@s", storeId, "@p", phone);
            try { Crm.Erase(c, storeId, phone); } catch (Exception ex) { Log.Error(ex); }
            return null;
        }

        // ---------- «بلّغني لما يرجع» from the product's page ----------
        public static void Watch(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("shop-watch:" + x.Ip, 12, TimeSpan.FromHours(1))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            var phone = ShopOrders.Mobile(Str(b, "phone", 24)); if (phone == null) throw new ShopException("phone", T(x, "اكتب رقم موبايل مصري صح.", "Enter a valid Egyptian mobile number."));
            ShopProd p; ShopVar v; int pid = ShopJson.Int(ShopJson.At(b, "p")), vid = ShopJson.Int(ShopJson.At(b, "v"));
            if (!x.Data.ById.TryGetValue(pid, out p) || p.Status != "active") throw new ShopException("gone", T(x, "المنتج ده مش موجود.", "This product isn't here."));
            x.Data.Vars.TryGetValue(vid, out v); if (v != null && v.ProductId != pid) v = null;
            if (v != null ? p.Can(v) : p.Available) throw new ShopException("there", T(x, "المنتج متاح دلوقتي، تقدر تطلبه.", "It's available now, you can order it."));
            if (!x.Preview) Restock.FromShop(c, x.Store, x.Row, p, v, phone, x.Prefix);
            Ok(res);
        }

        // ---------- what was searched, what was seen ----------
        public static void Find(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            var q = Regex.Replace(Str(b, "q", 60).ToLowerInvariant(), @"[\u0000-\u001F]|\s+", " ").Trim(); int n = Math.Max(0, ShopJson.Int(ShopJson.At(b, "n")));
            // a phone number or an address typed by mistake isn't kept
            if (!x.Preview && q.Length >= 2 && !Regex.IsMatch(q, @"\d{7,}|@") && Web.Allow("shop-find:" + x.Ip, 30, TimeSpan.FromMinutes(10)) && Num(c, "SELECT COUNT(*) FROM dbo.ShopSearches WHERE StoreId=@s AND Day=CAST(DATEADD(HOUR,2,SYSUTCDATETIME()) AS DATE)", "@s", x.Row.StoreId) < 2000)
                Exec(c, "MERGE dbo.ShopSearches AS d USING (SELECT @s AS StoreId, CAST(DATEADD(HOUR,2,SYSUTCDATETIME()) AS DATE) AS Day, @q AS Q) AS x ON d.StoreId=x.StoreId AND d.Day=x.Day AND d.Q=x.Q WHEN MATCHED THEN UPDATE SET N=d.N+1, Found=@n WHEN NOT MATCHED THEN INSERT(StoreId, Day, Q, N, Found) VALUES(x.StoreId, x.Day, x.Q, 1, @n);", "@s", x.Row.StoreId, "@q", q, "@n", n);
            Ok(res);
        }
        public static void Seen(SqlConnection c, int storeId, int productId, string col)
        {
            Exec(c, "MERGE dbo.ShopProdDaily AS d USING (SELECT @s AS StoreId, CAST(DATEADD(HOUR,2,SYSUTCDATETIME()) AS DATE) AS Day, @p AS ProductId) AS x ON d.StoreId=x.StoreId AND d.Day=x.Day AND d.ProductId=x.ProductId WHEN MATCHED THEN UPDATE SET " + col + "=d." + col + "+1 WHEN NOT MATCHED THEN INSERT(StoreId, Day, ProductId, " + col + ") VALUES(x.StoreId, x.Day, x.ProductId, 1);", "@s", storeId, "@p", productId);
        }

        // old codes, sessions and numbers (Jobs, once a day)
        public static void Tidy(SqlConnection c)
        {
            if (!Shops.Ready) return;
            Exec(c, @"DELETE FROM dbo.ShopCodes WHERE At<DATEADD(DAY,-2,SYSUTCDATETIME()); DELETE FROM dbo.ShopSessions WHERE ExpiresAt<SYSUTCDATETIME();
                DELETE FROM dbo.ShopSearches WHERE Day<DATEADD(DAY,-120,SYSUTCDATETIME()); DELETE FROM dbo.ShopProdDaily WHERE Day<DATEADD(DAY,-400,SYSUTCDATETIME()); DELETE FROM dbo.ShopCarts WHERE At<DATEADD(DAY,-30,SYSUTCDATETIME())");
        }
    }

    // ---------- tables of the second part of the plan ----------
    public static class ShopMore
    {
        public const string Schema = @"
IF OBJECT_ID('dbo.ShopCustomers') IS NULL
  CREATE TABLE dbo.ShopCustomers(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL, Name NVARCHAR(80) NULL, Addr NVARCHAR(MAX) NULL, News BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), LastAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ProvedAt DATETIME2 NULL, WipeAskedAt DATETIME2 NULL, PRIMARY KEY(StoreId, Phone));
IF OBJECT_ID('dbo.ShopCodes') IS NULL
BEGIN
  CREATE TABLE dbo.ShopCodes(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL, Kind NVARCHAR(8) NOT NULL, Hash NVARCHAR(64) NOT NULL,
    Tries INT NOT NULL DEFAULT 0, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ExpiresAt DATETIME2 NOT NULL, UsedAt DATETIME2 NULL, Fail NVARCHAR(200) NULL);
  CREATE INDEX IX_ShopCodes_Phone ON dbo.ShopCodes(StoreId, Phone, Kind, Id);
END
IF OBJECT_ID('dbo.ShopSessions') IS NULL
BEGIN
  CREATE TABLE dbo.ShopSessions(Hash NVARCHAR(64) NOT NULL PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), LastAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ExpiresAt DATETIME2 NOT NULL);
  CREATE INDEX IX_ShopSessions_Phone ON dbo.ShopSessions(StoreId, Phone);
END
-- new / approved / rejected / received / done / cancelled
IF OBJECT_ID('dbo.ShopReturns') IS NULL
BEGIN
  CREATE TABLE dbo.ShopReturns(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, OrderNo INT NOT NULL, Phone NVARCHAR(20) NOT NULL, Kind NVARCHAR(8) NOT NULL DEFAULT 'return',
    Status NVARCHAR(10) NOT NULL DEFAULT 'new', Reason NVARCHAR(12) NOT NULL, Note NVARCHAR(500) NULL, Items NVARCHAR(MAX) NOT NULL, Pics NVARCHAR(700) NULL, OwnerNote NVARCHAR(300) NULL, Refund DECIMAL(12,2) NULL, Restocked BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopReturns_Store ON dbo.ShopReturns(StoreId, Status, Id);
  CREATE INDEX IX_ShopReturns_Order ON dbo.ShopReturns(OrderId);
END
-- new = waits for the owner, shown, hidden
IF OBJECT_ID('dbo.ShopReviews') IS NULL
BEGIN
  CREATE TABLE dbo.ShopReviews(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, ProductId INT NOT NULL, OrderId BIGINT NOT NULL, Phone NVARCHAR(20) NOT NULL, Name NVARCHAR(60) NOT NULL, Opts NVARCHAR(300) NULL,
    Stars INT NOT NULL, Text NVARCHAR(1000) NULL, Pics NVARCHAR(700) NULL, Status NVARCHAR(8) NOT NULL DEFAULT 'new', Reply NVARCHAR(500) NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ShownAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopReviews_Order ON dbo.ShopReviews(OrderId, ProductId);
  CREATE INDEX IX_ShopReviews_Product ON dbo.ShopReviews(StoreId, ProductId, Status, Id);
END
IF OBJECT_ID('dbo.ShopPosts') IS NULL
BEGIN
  CREATE TABLE dbo.ShopPosts(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Slug NVARCHAR(80) NOT NULL, Title NVARCHAR(140) NOT NULL, TitleEn NVARCHAR(140) NULL, Body NVARCHAR(MAX) NULL, BodyEn NVARCHAR(MAX) NULL,
    Image NVARCHAR(160) NULL, Products NVARCHAR(300) NULL, Hidden BIT NOT NULL DEFAULT 0, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShopPosts_Slug ON dbo.ShopPosts(StoreId, Slug);
END
IF OBJECT_ID('dbo.ShopSearches') IS NULL
  CREATE TABLE dbo.ShopSearches(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Day DATE NOT NULL, Q NVARCHAR(60) NOT NULL, N INT NOT NULL DEFAULT 0, Found INT NOT NULL DEFAULT 0, PRIMARY KEY(StoreId, Day, Q));
IF OBJECT_ID('dbo.ShopProdDaily') IS NULL
  CREATE TABLE dbo.ShopProdDaily(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Day DATE NOT NULL, ProductId INT NOT NULL, Views INT NOT NULL DEFAULT 0, Carts INT NOT NULL DEFAULT 0, PRIMARY KEY(StoreId, Day, ProductId));
-- a checkout that was started with a number and not finished («السلة المتروكة»)
IF OBJECT_ID('dbo.ShopCarts') IS NULL
  CREATE TABLE dbo.ShopCarts(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL, Name NVARCHAR(80) NULL, Lang CHAR(2) NOT NULL DEFAULT 'ar', Lines NVARCHAR(MAX) NOT NULL, Total DECIMAL(12,2) NOT NULL DEFAULT 0, Token NVARCHAR(40) NOT NULL,
    At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), SentAt DATETIME2 NULL, DoneAt DATETIME2 NULL, Note NVARCHAR(200) NULL, PRIMARY KEY(StoreId, Phone));
IF COL_LENGTH('dbo.ShopCoupons', 'Shown') IS NULL ALTER TABLE dbo.ShopCoupons ADD Shown BIT NOT NULL CONSTRAINT DF_ShopCoupons_Shown DEFAULT 0;
-- what is added on the order's total apart from delivery (cash handling, gift wrap...) and what came back of it
IF COL_LENGTH('dbo.ShopOrders', 'Fees') IS NULL ALTER TABLE dbo.ShopOrders ADD Fees DECIMAL(12,2) NOT NULL CONSTRAINT DF_ShopOrders_Fees DEFAULT 0;
IF COL_LENGTH('dbo.ShopOrders', 'FeesJson') IS NULL ALTER TABLE dbo.ShopOrders ADD FeesJson NVARCHAR(600) NULL;
IF COL_LENGTH('dbo.ShopOrders', 'Extras') IS NULL ALTER TABLE dbo.ShopOrders ADD Extras NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.ShopOrders', 'Refund') IS NULL ALTER TABLE dbo.ShopOrders ADD Refund DECIMAL(12,2) NOT NULL CONSTRAINT DF_ShopOrders_Refund DEFAULT 0;
IF COL_LENGTH('dbo.ShopOrders', 'RateAskedAt') IS NULL ALTER TABLE dbo.ShopOrders ADD RateAskedAt DATETIME2 NULL;
-- cod, or how the money was sent before it (insta / vf / bank); PayState: wait (nothing sent yet), sent (the proof came), paid, bad
IF COL_LENGTH('dbo.ShopOrders', 'PayState') IS NULL ALTER TABLE dbo.ShopOrders ADD PayState NVARCHAR(6) NULL;
IF COL_LENGTH('dbo.ShopOrders', 'Deposit') IS NULL ALTER TABLE dbo.ShopOrders ADD Deposit DECIMAL(12,2) NOT NULL CONSTRAINT DF_ShopOrders_Deposit DEFAULT 0;
IF COL_LENGTH('dbo.ShopOrders', 'Paid') IS NULL ALTER TABLE dbo.ShopOrders ADD Paid DECIMAL(12,2) NOT NULL CONSTRAINT DF_ShopOrders_Paid DEFAULT 0;
IF COL_LENGTH('dbo.ShopOrders', 'Proof') IS NULL ALTER TABLE dbo.ShopOrders ADD Proof NVARCHAR(160) NULL;
IF COL_LENGTH('dbo.ShopOrders', 'SheetAt') IS NULL ALTER TABLE dbo.ShopOrders ADD SheetAt DATETIME2 NULL, SheetTry DATETIME2 NULL;
IF COL_LENGTH('dbo.ShopProducts', 'Ask') IS NULL ALTER TABLE dbo.ShopProducts ADD Ask NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.ShopOrderItems', 'Returned') IS NULL ALTER TABLE dbo.ShopOrderItems ADD Returned INT NOT NULL CONSTRAINT DF_ShopOrderItems_Returned DEFAULT 0;
";
    }
}
