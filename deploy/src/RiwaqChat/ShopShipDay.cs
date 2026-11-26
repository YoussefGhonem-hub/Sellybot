// «ميعاد التوصيل»: the customer picks the delivery day at checkout. Only from what the owner set: the days of the week, the governorates,
// from how many days ahead and up to how many, the last hour to order for the nearest day, the most orders a day (a full day isn't
// offered), morning / evening if the owner wants, and whether choosing is a must. The server checks the day again when the order is placed,
// and counts the day under the store's row (ShopOrders.Place holds it), so two customers can't take the last place together.
// The day lives in ShopOrders.Extras (day, dpart) for showing, and in ShopOrderDays for counting and the owner's filter.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;

namespace RiwaqChat
{
    public static class ShopShipDay
    {
        static readonly string[] Ar = { "الأحد", "الاتنين", "التلات", "الأربع", "الخميس", "الجمعة", "السبت" }, En = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        static readonly string[] Mon = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        static Dictionary<string, object> Set(Shop sh) { return ShopShip2.Of(sh, "dday"); }
        public static bool On(Shop sh) { return ShopShip2.On(sh, "dday"); }

        public static Dictionary<string, object> Clean(Dictionary<string, object> v)
        {
            int from = ShopShip2.Clamp(ShopJson.Int(ShopJson.At(v, "from"), 1), 0, 30), to = ShopShip2.Clamp(ShopJson.Int(ShopJson.At(v, "to"), from + 6), from, from + 45);
            return ShopShip2.D("on", ShopJson.Bool(ShopJson.At(v, "on")), "wd", ShopShip2.Ints(ShopJson.At(v, "wd"), 0, 6, 7), "govs", ShopShip2.Ints(ShopJson.At(v, "govs"), 1, ShopGeo.All.Length, 30), "from", from, "to", to,
                "cut", ShopShip2.Clamp(ShopJson.Int(ShopJson.At(v, "cut"), 24), 0, 24), "max", ShopShip2.Clamp(ShopJson.Int(ShopJson.At(v, "max")), 0, 100000), "parts", ShopJson.Bool(ShopJson.At(v, "parts")), "must", ShopJson.Bool(ShopJson.At(v, "must")));
        }
        static bool Covers(Dictionary<string, object> s, int gov) { var g = ShopShip2.Ints(ShopJson.At(s, "govs"), 1, 99, 30); return gov > 0 && (g.Count == 0 || g.Contains(gov)); }

        // ---------- the days on offer ----------
        // gov 0 = before the governorate is known: the days as if it were covered
        public static List<DateTime> Offered(SqlConnection c, Shop sh, int gov, DateTime utc)
        {
            var l = new List<DateTime>(); var s = Set(sh); if (!On(sh) || (gov > 0 && !Covers(s, gov))) return l;
            var wd = ShopShip2.Ints(ShopJson.At(s, "wd"), 0, 6, 7); if (wd.Count == 0) return l;
            int from = ShopJson.Int(ShopJson.At(s, "from"), 1), to = ShopJson.Int(ShopJson.At(s, "to"), from + 6), cut = ShopJson.Int(ShopJson.At(s, "cut"), 24), max = ShopJson.Int(ShopJson.At(s, "max"));
            var now = Carriers.Cairo(utc); var first = now.Date.AddDays(from);
            for (int k = from; k <= to && l.Count < 31; k++)
            {
                var day = now.Date.AddDays(k); if (!wd.Contains((int)day.DayOfWeek)) continue;
                if (day == first && cut < 24 && now.Hour >= cut) continue;   // too late today for the nearest day
                l.Add(day);
            }
            if (max > 0 && l.Count > 0) { var n = Counts(c, null, sh.StoreId, l[0], l[l.Count - 1]); l = l.Where(d => { int x; return !n.TryGetValue(d, out x) || x < max; }).ToList(); }
            return l;
        }
        static Dictionary<DateTime, int> Counts(SqlConnection c, SqlTransaction tx, int storeId, DateTime a, DateTime b)
        {
            var d = new Dictionary<DateTime, int>();
            using (var cmd = Db.Cmd(c, "SELECT d.OnDay, COUNT(*) FROM dbo.ShopOrderDays d JOIN dbo.ShopOrders o ON o.Id=d.OrderId WHERE d.StoreId=@s AND d.OnDay BETWEEN @a AND @b AND o.Status NOT IN ('cancelled','returned') GROUP BY d.OnDay", "@s", storeId, "@a", a, "@b", b))
            { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) while (r.Read()) d[((DateTime)r[0]).Date] = (int)r[1]; }
            return d;
        }
        static string Iso(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        static DateTime? Parse(string s) { DateTime d; return DateTime.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : (DateTime?)null; }

        // ---------- words ----------
        public static string Text(string iso, bool en) { var d = Parse(iso); if (!d.HasValue) return ""; var x = d.Value; return en ? En[(int)x.DayOfWeek] + " " + x.Day + " " + Mon[x.Month - 1] : Ar[(int)x.DayOfWeek] + " " + x.Day + "/" + x.Month; }
        public static string PartText(string p, bool en) { return p == "am" ? (en ? "(morning)" : "(الصبح)") : p == "pm" ? (en ? "(evening)" : "(بالليل)") : ""; }
        static string Of(ShopOrder o, bool en) { string day = ShopJson.Str(ShopJson.At(o.Extras, "day"), 10), p = ShopJson.Str(ShopJson.At(o.Extras, "dpart"), 4); return day.Length != 10 ? null : Text(day, en) + (p.Length > 0 ? " " + PartText(p, en) : ""); }
        // a line for the customer's confirmation and the owner's alert
        public static string Line(ShopOrder o, bool en) { var t = Of(o, en); return t == null ? "" : (en ? "📅 Delivery day: " : "📅 يوم التوصيل: ") + t + "\n"; }
        // the period, for the delivery-day message (Delivery.cs): null = none chosen
        public static string Period(ShopOrder o) { var p = ShopJson.Str(ShopJson.At(o.Extras, "dpart"), 4); return p.Length == 0 ? null : p; }
        // on the printed label
        public static string Label(ShopOrder o) { var t = Of(o, false); return t == null ? "" : "<div class=\"due\" style=\"font-size:16px\">التوصيل: " + HttpUtility.HtmlEncode(t) + "</div>"; }

        // ---------- the storefront ----------
        public static object Boot(Shop sh)
        {
            if (!On(sh)) return null; var s = Set(sh); if (ShopShip2.Ints(ShopJson.At(s, "wd"), 0, 6, 7).Count == 0) return null;
            return ShopShip2.D("must", ShopJson.Bool(ShopJson.At(s, "must")), "parts", ShopJson.Bool(ShopJson.At(s, "parts")), "govs", ShopShip2.Ints(ShopJson.At(s, "govs"), 1, 99, 30));
        }
        // /_a/days {gov}: the days the customer may choose now
        public static void Front(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            int gov = ShopJson.Int(ShopJson.At(b, "gov")); var s = Set(x.Row); bool covered = On(x.Row) && Covers(s, gov);
            var l = covered ? Offered(c, x.Row, gov, x.Now) : new List<DateTime>();
            Web.Json(res, 200, ShopShip2.D("on", covered, "must", covered && ShopJson.Bool(ShopJson.At(s, "must")), "parts", ShopJson.Bool(ShopJson.At(s, "parts")), "days", l.Select(d => ShopShip2.D("d", Iso(d), "t", ShopShip2.D("ar", Text(Iso(d), false), "en", Text(Iso(d), true)))).ToArray()));
        }

        // ---------- placing the order (ShopOrders.Place) ----------
        // before the sum: the day is one on offer, or none when it's allowed. A pickup from the shop has no delivery day.
        public static void Check(SqlConnection c, Shop row, ShopOrderIn o)
        {
            var day = Parse(o.Day); string part = o.DayPart == "am" || o.DayPart == "pm" ? o.DayPart : null; o.Day = null; o.DayPart = null;
            var s = Set(row); bool en = o.Lang == "en";
            if (!On(row) || !string.IsNullOrEmpty(o.How.Branch) || !Covers(s, o.Gov)) return;
            if (!ShopJson.Bool(ShopJson.At(s, "parts"))) part = null;
            if (o.ByOwner) { if (day.HasValue) { o.Day = Iso(day.Value); o.DayPart = part; } return; }
            if (!day.HasValue)
            {
                // a must with every day full: no order the owner can't deliver
                if (ShopJson.Bool(ShopJson.At(s, "must"))) throw new ShopException("day", Offered(c, row, o.Gov, DateTime.UtcNow).Count > 0 ? (en ? "Choose the delivery day." : "اختار يوم التوصيل.") : (en ? "No delivery day is free right now. Try again later or message the store." : "مفيش يوم توصيل فاضي دلوقتي. جرّب بعدين أو كلّم المتجر."));
                return;
            }
            if (!Offered(c, row, o.Gov, DateTime.UtcNow).Contains(day.Value)) throw new ShopException("day", en ? "That day isn't available any more. Choose another one." : "اليوم ده مابقاش متاح. اختار يوم تاني.");
            if (ShopJson.Bool(ShopJson.At(s, "parts")) && part == null && ShopJson.Bool(ShopJson.At(s, "must"))) throw new ShopException("day", en ? "Choose morning or evening." : "اختار الصبح ولا بالليل.");
            o.Day = Iso(day.Value); o.DayPart = part;
        }
        public static void Extra(ShopOrderIn o, Dictionary<string, object> exd) { if (o.Day == null) return; exd["day"] = o.Day; if (o.DayPart != null) exd["dpart"] = o.DayPart; }
        // in the order's transaction, after the store's row is held: the last place of a day goes to one order
        public static void Take(SqlConnection c, SqlTransaction tx, Shop row, long id, ShopOrderIn o)
        {
            if (o.Day == null) return; var day = Parse(o.Day).Value; int max = ShopJson.Int(ShopJson.At(Set(row), "max"));
            if (max > 0 && !o.ByOwner) { int n; Counts(c, tx, row.StoreId, day, day).TryGetValue(day, out n); if (n >= max) throw new ShopException("day", o.Lang == "en" ? "That day has just filled up. Choose another one." : "اليوم ده اتملى حالًا. اختار يوم تاني."); }
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopOrderDays(OrderId, StoreId, OnDay, Part) VALUES(@o, @s, @d, @p)", "@o", id, "@s", row.StoreId, "@d", day, "@p", (object)o.DayPart)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        }

        // ---------- the owner ----------
        // the list's filter (ShopOwner.Orders): ?day=yyyy-MM-dd
        public static string Filter(HttpRequest req, List<object> args)
        {
            var d = Parse(req.QueryString["day"]); if (!d.HasValue) return "";
            args.Add("@dday"); args.Add(d.Value); return " AND EXISTS (SELECT 1 FROM dbo.ShopOrderDays z WHERE z.OrderId=dbo.ShopOrders.Id AND z.OnDay=@dday)";
        }
        // the prep list: how many of the confirmed orders go each day
        public static object Prep(List<ShopOrder> os)
        {
            return os.Select(o => ShopJson.Str(ShopJson.At(o.Extras, "day"), 10)).Where(x => x.Length == 10).GroupBy(x => x).OrderBy(g => g.Key).Select(g => ShopShip2.D("d", g.Key, "t", Text(g.Key, false), "n", g.Count())).ToArray();
        }
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (a != "sday") return false;
            // the next days with how many orders each, and what a customer sees now (the Cairo governorate, or the first one covered)
            var set = Set(sh); var govs = ShopShip2.Ints(ShopJson.At(set, "govs"), 1, 99, 30); int gov = govs.Count > 0 ? govs[0] : 1; var now = Carriers.Cairo(DateTime.UtcNow).Date;
            var n = Counts(c, null, sh.StoreId, now, now.AddDays(60)); var seen = Offered(c, sh, gov, DateTime.UtcNow);
            Web.Json(res, 200, ShopShip2.D("ok", true, "set", set, "next", n.OrderBy(k => k.Key).Take(20).Select(k => ShopShip2.D("d", Iso(k.Key), "t", Text(Iso(k.Key), false), "n", k.Value)).ToArray(), "seen", seen.Select(d => Text(Iso(d), false)).ToArray()));
            return true;
        }

        // ---------- the assistant: what a customer could ask ----------
        public static string BotText(SqlConnection c, Shop sh, int gov)
        {
            try
            {
                if (!On(sh)) return ""; var s = Set(sh); var l = Offered(c, sh, gov, DateTime.UtcNow); var wd = ShopShip2.Ints(ShopJson.At(s, "wd"), 0, 6, 7); var govs = ShopShip2.Ints(ShopJson.At(s, "govs"), 1, 99, 30);
                if (wd.Count == 0) return "";
                var sb = new StringBuilder(" الزبون بيختار يوم التوصيل وهو بيطلب من المتجر: أيام التوصيل ").Append(string.Join("، ", wd.Select(i => Ar[i]))).Append(govs.Count > 0 ? " لمحافظات " + string.Join("، ", govs.Select(g => ShopGeo.Name(g))) : "").Append(".");
                if (gov > 0 && !Covers(s, gov)) sb.Append(" المحافظة دي مفيهاش اختيار يوم.");
                else sb.Append(l.Count > 0 ? " الأيام المتاحة دلوقتي: " + string.Join("، ", l.Take(10).Select(d => Text(Iso(d), false))) + "." : " مفيش أيام متاحة دلوقتي.");
                if (ShopJson.Bool(ShopJson.At(s, "parts"))) sb.Append(" وبيختار الصبح أو بالليل.");
                return sb.ToString();
            }
            catch (Exception ex) { Log.Error(ex); return ""; }
        }
        // an order from the chat: the day the customer said, if it's one on offer; null = fine
        public static string ChatCheck(SqlConnection c, Shop sh, int gov, string day, bool branch)
        {
            if (!On(sh) || branch || !Covers(Set(sh), gov)) return null; var l = Offered(c, sh, gov, DateTime.UtcNow); var d = Parse(day);
            string list = l.Count == 0 ? "" : " الأيام المتاحة (delivery_day بالشكل yyyy-MM-dd): " + string.Join("، ", l.Take(10).Select(x => Text(Iso(x), false) + " = " + Iso(x))) + ".";
            if (d.HasValue && !l.Contains(d.Value)) return "اليوم ده مش متاح للتوصيل." + list + " اسأل الزبون يختار يوم منهم.";
            if (!d.HasValue && ShopJson.Bool(ShopJson.At(Set(sh), "must"))) return l.Count > 0 ? "المتجر بيطلب يوم توصيل." + list + " اسأل الزبون يختار يوم منهم." : "مفيش يوم توصيل فاضي دلوقتي، فالطلب مايتسجّلش. قول للزبون يجرّب بعدين.";
            return null;
        }
    }
}
