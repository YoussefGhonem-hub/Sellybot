// «نقط» and «هات صاحبك» (ShopRew.cs has the hooks).
// Points: earned on what was paid for the products (no delivery, no fees) when the order is delivered, «جاية» before that, taken back when
// it comes back (a part return takes back a part). Used by a signed-in customer at checkout as a discount, up to the owner's share of the
// order. Each row that adds keeps what is left of it (Rest) until it's used or its date passes; the oldest go first. Every number for an
// order is worked out again from what it should hold in its status, so a press twice or a status that comes back gives nothing twice.
// «هات صاحبك» (customers bring friends; not Sellybot's «دعوة تاجر» in Referral.cs): a signed-in customer's link /r/<code>; the friend's first
// order (a number that never ordered here, not the sender's, not at the sender's address) gets the owner's gift; the sender gets a reward
// (points, or a one-time code when points are off) only once that order is delivered, and at most the owner's number of them in a month.
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

namespace RiwaqChat
{
    public static class ShopPoints
    {
        static Dictionary<string, object> Set(Shop sh) { return ShopJson.Sub(sh.Settings, "points"); }
        public static bool On(Shop sh) { return sh != null && ShopJson.Bool(ShopJson.At(Set(sh), "on")); }
        static decimal Clamp(decimal v, decimal a, decimal b) { return Math.Max(a, Math.Min(b, v)); }

        // per = pounds for one point; pts points are worth egp pounds; least = the fewest that can be used; cap = the most of an order (%)
        public class Rules { public decimal Per, Pts, Egp, Least, Cap; public int Months; }
        public static Dictionary<string, object> Clean(Dictionary<string, object> v)
        {
            return ShopRew.D("on", ShopJson.Bool(ShopJson.At(v, "on")), "per", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "per"), 10), 2), 1, 100000), "pts", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "pts"), 100), 0), 1, 1000000),
                "egp", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "egp"), 10), 2), 1, 100000), "least", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "least"), 100), 0), 0, 1000000), "cap", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "cap"), 30), 0), 1, 100),
                "months", Math.Max(0, Math.Min(60, ShopJson.Int(ShopJson.At(v, "months"), 12))));
        }
        public static Rules Of(Dictionary<string, object> settings) { var s = Clean(ShopJson.Sub(settings, "points")); return new Rules { Per = (decimal)s["per"], Pts = (decimal)s["pts"], Egp = (decimal)s["egp"], Least = (decimal)s["least"], Cap = (decimal)s["cap"], Months = (int)s["months"] }; }
        public static object PublicSet(Shop sh) { var s = Clean(Set(sh)); s.Remove("on"); s["acct"] = ShopAccount.On(sh); return s; }
        public static int For(decimal paid, Rules r) { return paid <= 0 ? 0 : (int)Math.Floor(paid / r.Per); }
        public static decimal Worth(int n, Rules r) { return n <= 0 ? 0 : Math.Floor(n * r.Egp / r.Pts * 100) / 100; }
        static DateTime? Until(Rules r) { return r.Months > 0 ? DateTime.UtcNow.AddMonths(r.Months) : (DateTime?)null; }
        // what was paid for the products of an order, less what came back (its gift card line isn't a product)
        public static decimal Paid(ShopOrder o) { return Math.Max(0, o.Items.Where(i => i.ProductId > 0 && i.Qty > 0).Sum(i => (i.Price * i.Qty - i.Discount) * (i.Qty - i.Returned) / i.Qty)); }

        // ---------- the ledger ----------
        public static int Balance(SqlConnection c, SqlTransaction tx, int storeId, string phone) { return ShopRew.Num(c, tx, "SELECT ISNULL(SUM(Rest),0) FROM dbo.ShopPoints WHERE StoreId=@s AND Phone=@p AND (ExpiresAt IS NULL OR ExpiresAt>SYSUTCDATETIME())", "@s", storeId, "@p", phone); }
        // adds n: what the person owes is paid first
        public static void Add(SqlConnection c, SqlTransaction tx, int storeId, string phone, string kind, int n, DateTime? until, long? orderId, string note, string who)
        {
            if (n <= 0 || string.IsNullOrEmpty(phone)) return; int left = n; var debts = new List<long[]>();
            using (var cmd = ShopRew.Cmd(c, tx, "SELECT Id, Rest FROM dbo.ShopPoints WITH (UPDLOCK, ROWLOCK) WHERE StoreId=@s AND Phone=@p AND Rest<0 ORDER BY Id", "@s", storeId, "@p", phone)) using (var r = cmd.ExecuteReader()) while (r.Read()) debts.Add(new[] { (long)r[0], (int)r[1] });
            foreach (var dt in debts) { if (left == 0) break; int k = (int)Math.Min(left, -dt[1]); ShopRew.Exec(c, tx, "UPDATE dbo.ShopPoints SET Rest=Rest+@k WHERE Id=@i", "@k", k, "@i", dt[0]); left -= k; }
            ShopRew.Exec(c, tx, "INSERT INTO dbo.ShopPoints(StoreId, Phone, Kind, Delta, Rest, ExpiresAt, OrderId, Note, Who) VALUES(@s, @p, @k, @n, @r, @e, @o, @t, @w)", "@s", storeId, "@p", phone, "@k", kind, "@n", n, "@r", left, "@e", until.HasValue ? (object)until.Value : null, "@o", orderId.HasValue ? (object)orderId.Value : null, "@t", note, "@w", who);
        }
        // takes n from the oldest (this order's own first); with too little: false, or a debt when it's points taken back
        public static bool Take(SqlConnection c, SqlTransaction tx, int storeId, string phone, string kind, int n, long? orderId, string note, string who, bool debt, long prefer = 0)
        {
            if (n <= 0 || string.IsNullOrEmpty(phone)) return true; var lots = new List<long[]>();
            using (var cmd = ShopRew.Cmd(c, tx, "SELECT Id, Rest FROM dbo.ShopPoints WITH (UPDLOCK, ROWLOCK) WHERE StoreId=@s AND Phone=@p AND Rest>0 AND (ExpiresAt IS NULL OR ExpiresAt>SYSUTCDATETIME()) ORDER BY CASE WHEN OrderId=@o THEN 0 ELSE 1 END, CASE WHEN ExpiresAt IS NULL THEN 1 ELSE 0 END, ExpiresAt, Id", "@s", storeId, "@p", phone, "@o", prefer))
            using (var r = cmd.ExecuteReader()) while (r.Read()) lots.Add(new[] { (long)r[0], (int)r[1] });
            if (lots.Sum(l => l[1]) < n && !debt) return false;
            int left = n;
            foreach (var l in lots) { if (left == 0) break; int k = (int)Math.Min(left, l[1]); if (ShopRew.Exec(c, tx, "UPDATE dbo.ShopPoints SET Rest=Rest-@k WHERE Id=@i AND Rest>=@k", "@k", k, "@i", l[0]) == 1) left -= k; }
            if (left > 0 && !debt) throw new Exception("points moved while held");
            ShopRew.Exec(c, tx, "INSERT INTO dbo.ShopPoints(StoreId, Phone, Kind, Delta, Rest, OrderId, Note, Who) VALUES(@s, @p, @k, @n, @r, @o, @t, @w)", "@s", storeId, "@p", phone, "@k", kind, "@n", -n, "@r", -left, "@o", orderId.HasValue ? (object)orderId.Value : null, "@t", note, "@w", who);
            return true;
        }

        // ---------- the cart's sum: «استخدم نقطك», a discount ----------
        public static decimal Use(SqlConnection c, ShopData d, ShopQuote q, ShopRewIn w, ShopRewQ r, string phone, decimal after)
        {
            var sh = d.Row; if (!On(sh) || w.Tk.Length == 0 || !ShopAccount.On(sh)) return 0;
            var who = ShopRew.WhoIs(c, null, sh.StoreId, w.Tk); if (who == null) { if (w.Pts) r.PtsErr = "signin"; return 0; }
            var R = Of(sh.Settings); r.Phone = who; r.PtsHave = Math.Max(0, Balance(c, null, sh.StoreId, who));
            if (!w.Pts) return 0;
            if (phone != null && phone != who) { r.PtsErr = "phone"; return 0; }
            if (r.PtsHave <= 0 || r.PtsHave < R.Least) { r.PtsErr = r.PtsHave <= 0 ? "none" : "least"; return 0; }
            decimal off = Math.Min(Math.Floor(r.PtsHave * R.Egp / R.Pts), Math.Min(Math.Floor(after * R.Cap / 100m), Math.Floor(Math.Max(0, after - q.Min))));
            if (off <= 0) { r.PtsErr = "zero"; return 0; }
            int use = (int)Math.Ceiling(off * R.Pts / R.Egp); if (use > r.PtsHave) { off -= 1; use = (int)Math.Ceiling(off * R.Pts / R.Egp); if (off <= 0 || use > r.PtsHave) { r.PtsErr = "zero"; return 0; } }
            off = ShopRew.Apply(q, off, ShopRew.PointsId, "نقطك (" + use + " نقطة)", "Your points (" + use + ")"); if (off <= 0) return 0;
            r.PtsUse = use; r.PtsOff = off; return off;
        }
        public static object TakeUse(SqlConnection c, SqlTransaction tx, int storeId, string phone, long orderId, ShopRewQ r, bool en)
        {
            if (phone != r.Phone || !Take(c, tx, storeId, phone, "use", r.PtsUse, orderId, null, "customer", false))
                throw new ShopException("points", en ? "Your points changed a moment ago. Check the total and confirm again." : "نقطك اتغيّرت حالًا. راجع الإجمالي وأكّد تاني.");
            ShopOrders.Log_(c, tx, storeId, orderId, "customer", "استخدم " + r.PtsUse + " نقطة بـ " + ShopOrders.Money(r.PtsOff));
            return ShopRew.D("n", r.PtsUse, "off", r.PtsOff);
        }
        // the points an order used: held while it's alive, back to the customer once it's cancelled or returned
        public static string SettleUse(SqlConnection c, SqlTransaction tx, ShopOrder o, bool live, Dictionary<string, object> p)
        {
            int n = ShopJson.Int(ShopJson.At(p, "n")); if (n <= 0 || o.Phone.Length == 0) return null;
            int now = ShopRew.Num(c, tx, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopPoints WHERE StoreId=@s AND OrderId=@o AND Phone=@p AND Kind IN ('use','undo')", "@s", o.StoreId, "@o", o.Id, "@p", o.Phone), diff = (live ? -n : 0) - now;
            var R = Of(ShopJson.Obj(ShopRew.One(c, tx, "SELECT Settings FROM dbo.Shops WITH (NOLOCK) WHERE StoreId=@s", "@s", o.StoreId) as string));
            if (diff > 0) { Add(c, tx, o.StoreId, o.Phone, "undo", diff, Until(R), o.Id, "أوردر " + o.NoText, "system"); ShopOrders.Log_(c, tx, o.StoreId, o.Id, "system", "رجعت للزبون " + diff + " نقطة"); }
            else if (diff < 0 && !Take(c, tx, o.StoreId, o.Phone, "use", -diff, o.Id, "أوردر " + o.NoText + " رجع يشتغل", "system", false)) return "نقط الزبون اللي اتخصمت في الأوردر ده اتصرفت في حاجة تانية، فماينفعش يرجع يشتغل.";
            return null;
        }
        // the points an order earns: what was paid for its products once it's delivered, nothing in any other status
        public static void SettleEarn(SqlConnection c, SqlTransaction tx, ShopOrder o, string to, Dictionary<string, object> set)
        {
            if (o.Phone.Length == 0) return;
            int had = ShopRew.Num(c, tx, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopPoints WHERE StoreId=@s AND OrderId=@o AND Phone=@p AND Kind IN ('earn','back')", "@s", o.StoreId, "@o", o.Id, "@p", o.Phone);
            var R = Of(set); int want = 0;
            if (to == "delivered" && o.Channel != ShopGift.Channel) { if (!ShopJson.Bool(ShopJson.At(ShopJson.Sub(set, "points"), "on"))) return; want = For(Paid(o), R); }
            if (want > had) Add(c, tx, o.StoreId, o.Phone, "earn", want - had, Until(R), o.Id, "أوردر " + o.NoText, "system");
            else if (want < had) Take(c, tx, o.StoreId, o.Phone, "back", had - want, o.Id, "أوردر " + o.NoText + (to == "returned" ? " رجع" : to == "cancelled" ? " اتلغى" : to == "delivered" ? ": جزء رجع" : ""), "system", true, o.Id);
        }

        // ---------- the customer's account ----------
        static readonly Dictionary<string, string[]> Kinds = new Dictionary<string, string[]> { { "earn", new[] { "كسبتها من طلب", "Earned on an order" } }, { "back", new[] { "اتشالت عشان الطلب رجع", "Taken back: the order came back" } }, { "use", new[] { "استخدمتها في طلب", "Used on an order" } },
            { "undo", new[] { "رجعتلك عشان الطلب اتلغى", "Back: the order was cancelled" } }, { "hand", new[] { "من المتجر", "From the store" } }, { "refer", new[] { "مكافأة صاحبك", "A friend's reward" } }, { "unref", new[] { "اتشالت: طلب صاحبك رجع", "Taken back: your friend's order came back" } } };
        public static object Mine(SqlConnection c, ShopFrontHandler.Ctx x, string phone)
        {
            var R = Of(x.Row.Settings); int have = Math.Max(0, Balance(c, null, x.Row.StoreId, phone)); bool en = x.Lang == "en";
            // «جاية»: the orders on their way, at today's rules
            int coming = 0;
            using (var cmd = Db.Cmd(c, @"SELECT SUM((i.Price*i.Qty-i.Discount)*(i.Qty-i.Returned)/i.Qty) FROM dbo.ShopOrders o JOIN dbo.ShopOrderItems i ON i.OrderId=o.Id WHERE o.StoreId=@s AND o.Phone=@p AND o.Status IN ('new','noreply','confirmed','shipped','out') AND o.Channel<>'gift' AND o.Test=0 AND i.ProductId>0 AND i.Qty>0 GROUP BY o.Id", "@s", x.Row.StoreId, "@p", phone))
            using (var r = cmd.ExecuteReader()) while (r.Read()) if (r[0] is decimal) coming += For((decimal)r[0], R);
            // what ends in the next 30 days
            int soon = ShopRew.Num(c, null, "SELECT ISNULL(SUM(Rest),0) FROM dbo.ShopPoints WHERE StoreId=@s AND Phone=@p AND Rest>0 AND ExpiresAt>SYSUTCDATETIME() AND ExpiresAt<DATEADD(DAY,30,SYSUTCDATETIME())", "@s", x.Row.StoreId, "@p", phone);
            var soonAt = ShopRew.One(c, null, "SELECT MIN(ExpiresAt) FROM dbo.ShopPoints WHERE StoreId=@s AND Phone=@p AND Rest>0 AND ExpiresAt>SYSUTCDATETIME()", "@s", x.Row.StoreId, "@p", phone);
            var list = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 60 p.Kind, p.Delta, p.ExpiresAt, p.At, p.Rest, o.No, p.Note FROM dbo.ShopPoints p LEFT JOIN dbo.ShopOrders o ON o.Id=p.OrderId AND o.Phone=p.Phone WHERE p.StoreId=@s AND p.Phone=@p ORDER BY p.Id DESC", "@s", x.Row.StoreId, "@p", phone))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    string k = (string)r[0]; string[] t; Kinds.TryGetValue(k, out t); var exp = r[2] is DateTime ? DateTime.SpecifyKind((DateTime)r[2], DateTimeKind.Utc) : (DateTime?)null;
                    list.Add(ShopRew.D("k", k, "t", t == null ? new[] { k, k } : t, "n", (int)r[1], "at", ShopView.Unix(DateTime.SpecifyKind((DateTime)r[3], DateTimeKind.Utc)), "no", r[5] is int ? r[5] : 0, "exp", exp.HasValue ? ShopView.Unix(exp.Value) : 0, "gone", exp.HasValue && exp.Value <= DateTime.UtcNow && (int)r[4] > 0 ? (int)r[4] : 0, "note", k == "hand" ? r[6] as string ?? "" : ""));   // the owner's reason is written for the customer
                }
            return ShopRew.D("have", have, "worth", Worth(have, R), "coming", coming, "soon", soon, "soonAt", soonAt is DateTime ? ShopView.Unix(DateTime.SpecifyKind((DateTime)soonAt, DateTimeKind.Utc)) : 0, "list", list, "acct", ShopAccount.On(x.Row));
        }

        // ---------- the owner ----------
        public static object Owner(SqlConnection c, Shop sh)
        {
            var R = Of(sh.Settings); object[] a = { "@s", sh.StoreId };
            int given = ShopRew.Num(c, null, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopPoints WHERE StoreId=@s AND Kind IN ('earn','back','refer','unref','hand')", a), used = -ShopRew.Num(c, null, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopPoints WHERE StoreId=@s AND Kind IN ('use','undo')", a);
            int left = ShopRew.Num(c, null, "SELECT ISNULL(SUM(Rest),0) FROM dbo.ShopPoints WHERE StoreId=@s AND (ExpiresAt IS NULL OR ExpiresAt>SYSUTCDATETIME())", a);
            var top = new List<object>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 30 p.Phone, SUM(p.Rest) AS B, (SELECT TOP 1 Name FROM dbo.ShopOrders o WHERE o.StoreId=p.StoreId AND o.Phone=p.Phone ORDER BY o.Id DESC) AS Name FROM dbo.ShopPoints p WHERE p.StoreId=@s AND (p.ExpiresAt IS NULL OR p.ExpiresAt>SYSUTCDATETIME()) GROUP BY p.StoreId, p.Phone HAVING SUM(p.Rest)>0 ORDER BY SUM(p.Rest) DESC", a))
            using (var r = cmd.ExecuteReader()) while (r.Read()) top.Add(ShopRew.D("phone", ShopOrders.Local((string)r[0]), "bal", (int)r[1], "worth", Worth((int)r[1], R), "name", r[2] as string ?? ""));
            return ShopRew.D("given", given, "used", used, "left", left, "worth", Worth(Math.Max(0, left), R), "holders", ShopRew.Num(c, null, "SELECT COUNT(*) FROM (SELECT Phone FROM dbo.ShopPoints WHERE StoreId=@s AND (ExpiresAt IS NULL OR ExpiresAt>SYSUTCDATETIME()) GROUP BY Phone HAVING SUM(Rest)>0) x", a), "top", top);
        }
        public static object OwnerOne(SqlConnection c, Shop sh, string phone)
        {
            if (phone.Length == 0) throw new ShopException("phone", "اكتب رقم موبايل الزبون صح.");
            var R = Of(sh.Settings); int bal = Balance(c, null, sh.StoreId, phone); var list = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 200 p.Kind, p.Delta, p.Rest, p.ExpiresAt, p.Note, p.Who, p.At, o.No FROM dbo.ShopPoints p LEFT JOIN dbo.ShopOrders o ON o.Id=p.OrderId WHERE p.StoreId=@s AND p.Phone=@p ORDER BY p.Id DESC", "@s", sh.StoreId, "@p", phone))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) { string[] t; Kinds.TryGetValue((string)r[0], out t); list.Add(ShopRew.D("k", (string)r[0], "t", t == null ? (string)r[0] : t[0], "n", (int)r[1], "rest", (int)r[2], "exp", r[3] is DateTime ? DateTime.SpecifyKind((DateTime)r[3], DateTimeKind.Utc).ToString("o") : "", "note", r[4] as string ?? "", "who", (string)r[5], "at", DateTime.SpecifyKind((DateTime)r[6], DateTimeKind.Utc).ToString("o"), "no", r[7] is int ? r[7] : 0)); }
            return ShopRew.D("phone", ShopOrders.Local(phone), "name", ShopRew.One(c, null, "SELECT TOP 1 Name FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p ORDER BY Id DESC", "@s", sh.StoreId, "@p", phone) as string ?? "", "bal", bal, "worth", Worth(Math.Max(0, bal), R), "list", list);
        }
        // by hand, with a reason; taking away never goes under nothing
        public static void Hand(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            var phone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24)); int n = ShopJson.Int(ShopJson.At(b, "n")); string why = ShopRew.Clip(ShopJson.Str(ShopJson.At(b, "why"), 250), 200);
            if (phone == null) throw new ShopException("phone", "اكتب رقم موبايل الزبون صح."); if (n == 0 || Math.Abs(n) > 100000) throw new ShopException("n", "اكتب عدد النقط (بالسالب لو هتشيل)."); if (why.Length < 3) throw new ShopException("why", "اكتب السبب، الزبون هيشوفه في حسابه.");
            if (!Web.Allow("shop-ptsh:" + sh.StoreId, 300, TimeSpan.FromHours(24))) throw new ShopException("many", "عدّلت كتير النهارده.");
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    if (n > 0) Add(c, tx, sh.StoreId, phone, "hand", n, Until(Of(sh.Settings)), null, why, "owner");
                    else if (!Take(c, tx, sh.StoreId, phone, "hand", -n, null, why, "owner", false)) { tx.Rollback(); throw new ShopException("low", "رصيده " + Balance(c, null, sh.StoreId, phone) + " نقطة بس."); }
                    tx.Commit();
                }
                catch (ShopException) { throw; }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
        }
        public static string BotText(Shop sh)
        {
            var R = Of(sh.Settings);
            return "نقط: كل " + ShopOrders.Money(R.Per) + " بيدفعهم الزبون في المنتجات (من غير الشحن) = نقطة، بتنزل في حسابه لما الطلب يتسلّم، وكل " + R.Pts.ToString("#,0", CultureInfo.InvariantCulture) + " نقطة بـ " + ShopOrders.Money(R.Egp) + ". بيستخدمها وهو داخل حسابه في صفحة الطلب، لحد " + R.Cap.ToString("0", CultureInfo.InvariantCulture) + "% من الطلب" + (R.Least > 0 ? " ومن " + R.Least.ToString("#,0", CultureInfo.InvariantCulture) + " نقطة" : "") + (R.Months > 0 ? "، وصالحة " + R.Months + " شهر من يوم ما اتكسبت" : "") + ". رصيده وتاريخها في " + sh.Url + "/account/rew (إنت ماتعرفش رصيد حد).";
        }
    }

    public static class ShopRefer
    {
        const string Abc = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        static Dictionary<string, object> Set(Shop sh) { return ShopJson.Sub(sh.Settings, "refer"); }
        public static bool On(Shop sh) { return sh != null && ShopJson.Bool(ShopJson.At(Set(sh), "on")); }
        static decimal Clamp(decimal v, decimal a, decimal b) { return Math.Max(a, Math.Min(b, v)); }
        public const int CodeDays = 90;

        // kind: amt / pct of the friend's gift; least = the friend's least order; reward = the sender's (in pounds: as points when points are on); month = most rewards a month
        public static Dictionary<string, object> Clean(Dictionary<string, object> v)
        {
            string kind = ShopJson.Str(ShopJson.At(v, "kind")) == "pct" ? "pct" : "amt";
            return ShopRew.D("on", ShopJson.Bool(ShopJson.At(v, "on")), "kind", kind, "value", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "value"), 50), 0), 1, kind == "pct" ? 90 : 100000), "least", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "least"), 0), 0), 0, 1000000),
                "reward", Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "reward"), 50), 0), 0, 100000), "month", Math.Max(1, Math.Min(100, ShopJson.Int(ShopJson.At(v, "month"), 5))));
        }
        public static object PublicSet(Shop sh) { var s = Clean(Set(sh)); s.Remove("on"); s.Remove("month"); s["pts"] = ShopPoints.On(sh); if (ShopPoints.On(sh)) { var R = ShopPoints.Of(sh.Settings); s["rewardPts"] = (int)Math.Floor((decimal)s["reward"] * R.Pts / R.Egp); } s["acct"] = ShopAccount.On(sh); return s; }
        static string N(string s) { return Regex.Replace(ShopJson.Digits(s ?? "").ToLowerInvariant().Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي'), @"[^\p{L}\p{N}]", ""); }
        static bool SameAddress(SqlConnection c, int storeId, string sender, int gov, string address)
        {
            var a = N(address); if (a.Length < 6) return false;
            using (var cmd = Db.Cmd(c, "SELECT TOP 20 Gov, Address FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p ORDER BY Id DESC", "@s", storeId, "@p", sender)) using (var r = cmd.ExecuteReader()) while (r.Read()) if (N((string)r[1]) == a && (gov <= 0 || (int)r[0] == gov)) return true;
            foreach (var x in ShopJson.Arr(ShopRew.One(c, null, "SELECT Addr FROM dbo.ShopCustomers WHERE StoreId=@s AND Phone=@p", "@s", storeId, "@p", sender) as string).OfType<Dictionary<string, object>>()) if (N(ShopJson.Str(ShopJson.At(x, "ad"))) == a) return true;
            return false;
        }

        // ---------- the friend's gift at checkout: a discount on a first order ----------
        public static decimal Friend(SqlConnection c, ShopData d, ShopQuote q, ShopRewIn w, ShopRewQ r, string phone, decimal after)
        {
            var sh = d.Row; if (!On(sh) || w.Ref.Length == 0) return 0;
            var s = Clean(Set(sh)); r.RefCode = w.Ref; r.RefMin = (decimal)s["least"];
            var sender = ShopRew.One(c, null, "SELECT Phone FROM dbo.ShopReferrers WHERE StoreId=@s AND Code=@c", "@s", sh.StoreId, "@c", w.Ref) as string;
            if (sender == null) { r.RefErr = "bad"; r.RefDead = true; return 0; }
            r.RefSender = sender;
            if (q.CouponUsed) { r.RefErr = "coupon"; return 0; }
            if (phone == null) { r.RefWait = true; return 0; }
            if (phone == sender) { r.RefErr = "self"; r.RefDead = true; return 0; }
            if (ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p AND Status<>'cancelled' AND Test=0", "@s", sh.StoreId, "@p", phone) > 0 || ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Friend=@p AND Status<>'void'", "@s", sh.StoreId, "@p", phone) > 0) { r.RefErr = "old"; r.RefDead = true; return 0; }
            if (w.Address.Length > 0 && SameAddress(c, sh.StoreId, sender, w.Gov, w.Address)) { r.RefErr = "addr"; return 0; }
            if (after < r.RefMin) { r.RefErr = "min"; return 0; }
            decimal v = (decimal)s["value"], amt = (string)s["kind"] == "pct" ? Math.Round(after * v / 100m, 0, MidpointRounding.AwayFromZero) : v;
            amt = Math.Min(amt, Math.Floor(Math.Max(0, after - q.Min)));
            r.RefOff = ShopRew.Apply(q, amt, ShopRew.FriendId, "هدية صاحبك", "Friend's gift"); return r.RefOff;
        }
        // the sender's reward code in «كارت هدية»: a discount, for the sender's own number, once
        public static decimal Code(SqlConnection c, ShopData d, ShopQuote q, ShopRewIn w, ShopRewQ r, string phone, decimal after)
        {
            if (w.Card.Length == 0 || w.Card.Length == 12) return 0;   // 12 = a gift card (after the total)
            var sh = d.Row; string key = sh.StoreId + ":" + (w.Ip ?? "");
            if (w.Card.Length != 10 || w.Card[0] != 'R') { r.CodeErr = "bad"; return 0; }
            var row = new List<object[]>(); using (var cmd = Db.Cmd(c, "SELECT Id, Sender, RewardAmt, CodeUntil, CodeOrder, Status FROM dbo.ShopReferrals WHERE StoreId=@s AND RewardCode=@c", "@s", sh.StoreId, "@c", w.Card)) using (var rd = cmd.ExecuteReader()) while (rd.Read()) { var a = new object[6]; rd.GetValues(a); row.Add(a); }
            if (row.Count == 0 || ((string)row[0][5] != "rewarded" && (string)row[0][5] != "void")) { r.CodeErr = "bad"; return 0; } if ((string)row[0][5] == "void") { r.CodeErr = "exp2"; return 0; }
            var x = row[0];
            if (x[4] is long) { r.CodeErr = "used"; return 0; }
            if (!(x[3] is DateTime) || DateTime.SpecifyKind((DateTime)x[3], DateTimeKind.Utc) <= DateTime.UtcNow) { r.CodeErr = "exp2"; return 0; }
            if (phone == null) { r.CodeErr = "wait"; return 0; }
            if (phone != (string)x[1]) { r.CodeErr = "mine"; return 0; }
            decimal amt = Math.Min(x[2] is decimal ? (decimal)x[2] : 0, Math.Floor(Math.Max(0, after - q.Min)));
            r.CodeOff = ShopRew.Apply(q, amt, ShopRew.CodeOfferId, "مكافأة «هات صاحبك»", "Referral reward"); if (r.CodeOff > 0) r.CodeId = (int)x[0]; else r.CodeErr = "min";
            return r.CodeOff;
        }

        // ---------- the order takes them (inside its transaction) ----------
        public static object TakeFriend(SqlConnection c, SqlTransaction tx, int storeId, long orderId, ShopRewQ r, string phone, ShopQuote q, bool en, string ip)
        {
            // a few gifts a day from one place, and from one sender's link
            if (!string.IsNullOrEmpty(ip) && ShopRew.Num(c, tx, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Ip=@ip AND Status<>'void' AND CreatedAt>DATEADD(DAY,-1,SYSUTCDATETIME())", "@s", storeId, "@ip", ip) >= 3
                || ShopRew.Num(c, tx, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Sender=@p AND Status<>'void' AND CreatedAt>DATEADD(DAY,-1,SYSUTCDATETIME())", "@s", storeId, "@p", r.RefSender) >= 10)
                throw new ShopException("ref", en ? "This friend's gift was used too many times today. Remove it or try tomorrow." : "هدية «هات صاحبك» اتستخدمت كتير النهارده من نفس المكان أو نفس اللينك. جرّب بكرة.");
            int no = ShopRew.Num(c, tx, "SELECT No FROM dbo.ShopOrders WHERE Id=@o", "@o", orderId);
            try { ShopRew.Exec(c, tx, "INSERT INTO dbo.ShopReferrals(StoreId, Code, Sender, Friend, OrderId, OrderNo, Gift, Ip) VALUES(@s, @c, @snd, @f, @o, @no, @off, @ip)", "@s", storeId, "@c", r.RefCode, "@snd", r.RefSender, "@f", phone, "@o", orderId, "@no", no, "@off", r.RefOff, "@ip", ip); }
            catch (SqlException e) when (e.Number == 2601 || e.Number == 2627) { throw new ShopException("ref", en ? "The friend's gift is for a first order only." : "هدية صاحبك لأول طلب بس."); }
            ShopOrders.Log_(c, tx, storeId, orderId, "customer", "جه من لينك «هات صاحبك» (" + ShopOrders.Local(r.RefSender) + ")، وخد خصم " + ShopOrders.Money(r.RefOff));
            return ShopRew.D("code", r.RefCode, "off", r.RefOff);
        }
        public static object TakeCode(SqlConnection c, SqlTransaction tx, int storeId, long orderId, ShopRewQ r, bool en)
        {
            if (ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET CodeOrder=@o WHERE Id=@i AND StoreId=@s AND CodeOrder IS NULL AND Status='rewarded' AND CodeUntil>SYSUTCDATETIME()", "@o", orderId, "@i", r.CodeId, "@s", storeId) == 0)
                throw new ShopException("code", en ? "This reward code was just used. Check the total and confirm again." : "كود المكافأة ده اتستخدم حالًا. راجع الإجمالي وأكّد تاني.");
            ShopOrders.Log_(c, tx, storeId, orderId, "customer", "استخدم كود مكافأة «هات صاحبك» بـ " + ShopOrders.Money(r.CodeOff));
            return ShopRew.D("id", r.CodeId, "off", r.CodeOff);
        }
        public static string SettleCode(SqlConnection c, SqlTransaction tx, ShopOrder o, bool live, Dictionary<string, object> rc)
        {
            int id = ShopJson.Int(ShopJson.At(rc, "id")); if (id <= 0) return null;
            if (!live) ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET CodeOrder=NULL WHERE Id=@i AND StoreId=@s AND CodeOrder=@o", "@i", id, "@s", o.StoreId, "@o", o.Id);
            else if (ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET CodeOrder=@o WHERE Id=@i AND StoreId=@s AND (CodeOrder IS NULL OR CodeOrder=@o) AND Status='rewarded'", "@i", id, "@s", o.StoreId, "@o", o.Id) == 0) return "كود المكافأة اللي اتستخدم في الأوردر ده اتستخدم في أوردر تاني، فماينفعش يرجع يشتغل.";
            return null;
        }

        // ---------- the friend's order moved: the sender's reward comes on delivery only, and leaves when it comes back ----------
        public static void Settle(SqlConnection c, SqlTransaction tx, ShopOrder o, string to, Dictionary<string, object> set)
        {
            var rows = new List<object[]>(); using (var cmd = ShopRew.Cmd(c, tx, "SELECT Id, Status, Sender, RewardPts, RewardCode, CodeOrder FROM dbo.ShopReferrals WITH (UPDLOCK, ROWLOCK) WHERE OrderId=@o AND StoreId=@s", "@o", o.Id, "@s", o.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) { var a = new object[6]; r.GetValues(a); rows.Add(a); }
            if (rows.Count == 0) return;
            int id = (int)rows[0][0]; string st = (string)rows[0][1], sender = (string)rows[0][2]; int pts = rows[0][3] is int ? (int)rows[0][3] : 0; string code = rows[0][4] as string;
            Action takeBack = () =>
            {
                if (pts > 0) ShopPoints.Take(c, tx, o.StoreId, sender, "unref", pts, o.Id, "طلب صاحبك " + o.NoText + " رجع", "system", true, o.Id);
                if (code != null && !(rows[0][5] is long)) ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET CodeUntil=SYSUTCDATETIME() WHERE Id=@i", "@i", id);
            };
            if (to == "cancelled" || to == "returned")
            {
                if (st == "rewarded") takeBack();
                if (st != "void") ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET Status='void' WHERE Id=@i", "@i", id);
                return;
            }
            // brought back: the friend's gift counts again, unless that number came by another link since
            if (st == "void") { try { if (ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET Status='pending', RewardAt=NULL, RewardPts=NULL, RewardCode=NULL, CodeUntil=NULL, CodeOrder=NULL, ToldAt=NULL WHERE Id=@i AND Status='void' AND Friend<>''", "@i", id) == 1) st = "pending"; } catch (SqlException e) when (e.Number == 2601 || e.Number == 2627) { return; } }
            if (to != "delivered" || st != "pending" || sender.Length == 0) return;
            var s = Clean(ShopJson.Sub(set, "refer")); decimal amount = (decimal)s["reward"];
            if (ShopRew.Num(c, tx, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Sender=@p AND Status='rewarded' AND RewardAt>=@m", "@s", o.StoreId, "@p", sender, "@m", ShopRew.MonthStart()) >= (int)s["month"] || amount <= 0)
            { ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET Status='capped', RewardAt=SYSUTCDATETIME() WHERE Id=@i", "@i", id); return; }
            if (ShopJson.Bool(ShopJson.At(ShopJson.Sub(set, "points"), "on")))
            {
                var R = ShopPoints.Of(set); int n = (int)Math.Floor(amount * R.Pts / R.Egp);
                ShopPoints.Add(c, tx, o.StoreId, sender, "refer", n, R.Months > 0 ? DateTime.UtcNow.AddMonths(R.Months) : (DateTime?)null, o.Id, "صاحبك استلم طلبه " + o.NoText, "system");
                ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET Status='rewarded', RewardAt=SYSUTCDATETIME(), RewardPts=@n, RewardAmt=@a, ToldAt=NULL WHERE Id=@i", "@n", n, "@a", amount, "@i", id);
            }
            else
            {
                string rc = null;
                for (int i = 0; i < 5 && rc == null; i++) { var k = "R" + ShopGift.NewCode(9); if (ShopRew.Num(c, tx, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND RewardCode=@c", "@s", o.StoreId, "@c", k) == 0) rc = k; }
                ShopRew.Exec(c, tx, "UPDATE dbo.ShopReferrals SET Status='rewarded', RewardAt=SYSUTCDATETIME(), RewardAmt=@a, RewardCode=@c, CodeUntil=DATEADD(DAY,@d,SYSUTCDATETIME()), ToldAt=NULL WHERE Id=@i", "@a", amount, "@c", rc, "@d", CodeDays, "@i", id);
            }
            ShopOrders.Log_(c, tx, o.StoreId, o.Id, "system", "اللي بعت لينك «هات صاحبك» خد مكافأته");
        }

        // ---------- the friend's page /r/<code> ----------
        public static void Look(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!On(x.Row)) { ShopRew.Ok(res, "on", false); return; }
            if (!Web.Allow("shop-rlook:" + x.Ip, 30, TimeSpan.FromHours(1))) throw new ShopException("too_many", ShopRew.T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            var code = ShopRew.Code(ShopJson.Str(ShopJson.At(b, "code"), 20));
            bool ok = code.Length >= 6 && ShopRew.One(c, null, "SELECT Phone FROM dbo.ShopReferrers WHERE StoreId=@s AND Code=@c", "@s", x.Row.StoreId, "@c", code) != null;
            ShopRew.Ok(res, "on", true, "valid", ok, "code", ok ? code : "");
        }
        // the sender's own link, made the first time it's asked for
        static string MyCode(SqlConnection c, int storeId, string phone)
        {
            var had = ShopRew.One(c, null, "SELECT Code FROM dbo.ShopReferrers WHERE StoreId=@s AND Phone=@p", "@s", storeId, "@p", phone) as string; if (had != null) return had;
            for (int i = 0; i < 6; i++)
            {
                var k = ShopGift.NewCode(8);
                try { ShopRew.Exec(c, null, "INSERT INTO dbo.ShopReferrers(StoreId, Phone, Code) VALUES(@s, @p, @c)", "@s", storeId, "@p", phone, "@c", k); return k; }
                catch (SqlException e) when (e.Number == 2601 || e.Number == 2627) { had = ShopRew.One(c, null, "SELECT Code FROM dbo.ShopReferrers WHERE StoreId=@s AND Phone=@p", "@s", storeId, "@p", phone) as string; if (had != null) return had; }
            }
            throw new Exception("referral: no free code");
        }
        public static object Mine(SqlConnection c, ShopFrontHandler.Ctx x, string phone)
        {
            var s = Clean(Set(x.Row)); string code = MyCode(c, x.Row.StoreId, phone); var list = new List<object>(); int friends = 0, got = 0;
            using (var cmd = Db.Cmd(c, "SELECT TOP 40 Status, RewardPts, RewardAmt, RewardCode, CodeUntil, CodeOrder, CreatedAt, RewardAt FROM dbo.ShopReferrals WHERE StoreId=@s AND Sender=@p ORDER BY Id DESC", "@s", x.Row.StoreId, "@p", phone))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    string st = (string)r[0]; if (st != "void") friends++; if (st == "rewarded") got++;
                    list.Add(ShopRew.D("st", st, "pts", r[1] is int ? r[1] : 0, "amt", r[2] is decimal ? r[2] : 0, "code", st == "rewarded" && r[3] is string ? r[3] : "", "until", r[4] is DateTime ? ShopView.Unix(DateTime.SpecifyKind((DateTime)r[4], DateTimeKind.Utc)) : 0, "used", r[5] is long, "at", ShopView.Unix(DateTime.SpecifyKind((DateTime)r[6], DateTimeKind.Utc))));
                }
            int month = ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Sender=@p AND Status='rewarded' AND RewardAt>=@m", "@s", x.Row.StoreId, "@p", phone, "@m", ShopRew.MonthStart());
            return ShopRew.D("code", code, "link", x.Row.Url + "/r/" + code, "friends", friends, "got", got, "month", month, "max", s["month"], "list", list.Where(y => (string)((Dictionary<string, object>)y)["st"] != "void").ToList());
        }

        // ---------- the owner ----------
        public static object Owner(SqlConnection c, Shop sh)
        {
            object[] a = { "@s", sh.StoreId }; var list = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 50 r.Sender, r.Friend, r.OrderId, r.OrderNo, r.Status, r.Gift, r.RewardPts, r.RewardAmt, r.RewardCode, r.CodeOrder, r.CreatedAt, o.Status AS OSt FROM dbo.ShopReferrals r LEFT JOIN dbo.ShopOrders o ON o.Id=r.OrderId WHERE r.StoreId=@s ORDER BY r.Id DESC", a))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(ShopRew.D("sender", ShopOrders.Local(r[0] as string ?? ""), "friend", ShopOrders.Local(r[1] as string ?? ""), "order", Convert.ToInt64(r[2]), "no", (int)r[3], "st", (string)r[4], "off", (decimal)r[5], "pts", r[6] is int ? r[6] : 0, "amt", r[7] is decimal ? r[7] : 0, "code", r[8] as string ?? "", "used", r[9] is long, "at", DateTime.SpecifyKind((DateTime)r[10], DateTimeKind.Utc).ToString("o"), "ost", r[11] as string ?? ""));
            return ShopRew.D("friends", ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Status<>'void'", a), "rewarded", ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Status='rewarded'", a),
                "capped", ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Status='capped'", a), "off", ShopRew.Dec(c, null, "SELECT ISNULL(SUM(Gift),0) FROM dbo.ShopReferrals WHERE StoreId=@s AND Status<>'void'", a),
                "rewards", ShopRew.Dec(c, null, "SELECT ISNULL(SUM(RewardAmt),0) FROM dbo.ShopReferrals WHERE StoreId=@s AND Status='rewarded'", a), "senders", ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopReferrers WHERE StoreId=@s", a), "list", list);
        }
        public static string BotText(Shop sh)
        {
            var s = Clean(Set(sh)); string gift = (string)s["kind"] == "pct" ? "خصم " + ((decimal)s["value"]).ToString("0", CultureInfo.InvariantCulture) + "%" : "خصم " + ShopOrders.Money((decimal)s["value"]);
            return "«هات صاحبك»: الزبون اللي داخل حسابه ياخد لينك خاص بيه من " + sh.Url + "/account/rew ويبعته لصحابه، وصاحبه ياخد " + gift + " على أول طلب ليه من المتجر" + ((decimal)s["least"] > 0 ? " للطلبات من " + ShopOrders.Money((decimal)s["least"]) : "") + "، واللي بعت اللينك ياخد مكافأة " + ShopOrders.Money((decimal)s["reward"]) + (ShopPoints.On(sh) ? " نقط" : " كود خصم") + " بعد ما طلب صاحبه يتسلّم.";
        }

        // ---------- the job: the sender hears of the reward, once ----------
        public static async Task TellDue(SqlConnection c, CancellationToken ct)
        {
            var due = new List<int[]>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 30 Id, StoreId FROM dbo.ShopReferrals WHERE Status='rewarded' AND ToldAt IS NULL AND Sender<>'' AND RewardAt>DATEADD(DAY,-7,SYSUTCDATETIME()) AND (RewardPts>0 OR RewardCode IS NOT NULL) ORDER BY Id")) using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new[] { (int)r[0], (int)r[1] });
            foreach (var g in due.GroupBy(x => x[1]))
            {
                var s = Db.StoreById(c, g.Key); var sh = Shops.ByStore(c, g.Key); if (s == null || sh == null || WaNotify.CantSend(c, s) != null || Billing.Expired(s)) continue;
                bool en = ShopJson.Str(ShopJson.At(sh.Look(false), "langs")) == "en"; string store = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name;
                foreach (var k in g)
                {
                    if (ct.IsCancellationRequested) return;
                    if (ShopRew.Exec(c, null, "UPDATE dbo.ShopReferrals SET ToldAt=SYSUTCDATETIME() WHERE Id=@i AND ToldAt IS NULL", "@i", k[0]) == 0) continue;
                    string sender = null, code = null; int pts = 0; decimal amt = 0; DateTime? until = null;
                    using (var cmd = Db.Cmd(c, "SELECT Sender, RewardPts, RewardAmt, RewardCode, CodeUntil FROM dbo.ShopReferrals WHERE Id=@i", "@i", k[0])) using (var r = cmd.ExecuteReader()) if (r.Read()) { sender = (string)r[0]; pts = r[1] is int ? (int)r[1] : 0; amt = r[2] is decimal ? (decimal)r[2] : 0; code = r[3] as string; until = r[4] is DateTime ? DateTime.SpecifyKind((DateTime)r[4], DateTimeKind.Utc) : (DateTime?)null; }
                    if (string.IsNullOrEmpty(sender)) continue;
                    string text = en ? "🎉 A friend you invited received their first order from *" + store + "*, so you got " + (pts > 0 ? "*" + pts + " points* for your next order: " + sh.Url + "/account/rew" : "a code worth *EGP " + amt.ToString("#,0", CultureInfo.InvariantCulture) + "* for your next order: *" + code + "*" + (until.HasValue ? " (until " + ShopRew.Day(until.Value, true) + ")" : "")) + "\nThank you 🙏"
                        : "🎉 صاحبك اللي بعتله لينكك استلم أول طلب من *" + store + "*، وكسبت " + (pts > 0 ? "*" + pts + " نقطة* تستخدمهم في طلبك الجاي: " + sh.Url + "/account/rew" : "كود خصم بـ *" + ShopOrders.Money(amt) + "* لطلبك الجاي: *" + code + "*" + (until.HasValue ? " (لحد " + ShopRew.Day(until.Value) + ")" : "") + "\nاكتبه في خانة «كارت هدية» وانت بتأكد الطلب") + "\nشكرًا ليك 🙏";
                    await WaNotify.ToCustomer(c, s, sender, text, "sb-shop-ref-" + k[0]);
                }
            }
        }
    }
}
