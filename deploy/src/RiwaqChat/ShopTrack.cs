// «بيتجهز» and the customer's «طلبي فين؟». The owner marks a confirmed order as being prepared: a mark in ShopOrders.Extras (prep = when,
// UTC), not a status, so every filter, count, report, export and shipping company keeps reading "confirmed". A cancelled, new or
// no-answer order loses the mark (ShopHooks.OnStatus), so it never shows past what the order really is.
// The customer's pages (the track page, the thank-you page, the account's order) get the steps' times only as they were recorded: the
// order's own columns, the mark, and the log for «خرج للتوصيل»; a step without a recorded time shows none. Never who did what.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public static class ShopTrack
    {
        public static DateTime? PrepAt(ShopOrder o)
        {
            var s = ShopJson.Str(ShopJson.At(o.Extras, "prep"), 40); DateTime d;
            return s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null;
        }
        public static bool Prepping(ShopOrder o) { return o.Status == "confirmed" && PrepAt(o).HasValue; }

        // written over what the order's extras are now, the read checked again (as ShopSelf.Write): null takes the mark off
        static bool Write(SqlConnection c, ShopOrder o, DateTime? at)
        {
            for (int i = 0; i < 3; i++)
            {
                var ex = new Dictionary<string, object>(o.Extras); if (at.HasValue) ex["prep"] = at.Value.ToString("o", CultureInfo.InvariantCulture); else ex.Remove("prep");
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Extras=@e WHERE Id=@i AND StoreId=@s AND ISNULL(Extras,'')=@old", "@e", ShopJson.Write(ex), "@i", o.Id, "@s", o.StoreId, "@old", o.ExtrasJson ?? ""))
                    if (cmd.ExecuteNonQuery() == 1) { o.ExtrasJson = ShopJson.Write(ex); o.Extras.Clear(); foreach (var kv in ex) o.Extras[kv.Key] = kv.Value; return true; }
                var fresh = ShopOrders.ById(c, o.StoreId, o.Id); if (fresh == null) return false; o.Status = fresh.Status; o.ExtrasJson = fresh.ExtrasJson; o.Extras.Clear(); foreach (var kv in fresh.Extras) o.Extras[kv.Key] = kv.Value;
                if (at.HasValue && o.Status != "confirmed") return false;
            }
            return false;
        }

        // the owner (and staff with «الأوردرات»): {ids, on}
        public static bool Post(SqlConnection c, Shop sh, string a, Dictionary<string, object> b, HttpResponse res)
        {
            if (a != "order-prep") return false;
            bool on = ShopJson.Bool(ShopJson.At(b, "on")); var ids = ShopJson.List(ShopJson.At(b, "ids")).Select(x => (long)ShopJson.Int(x)).Where(x => x > 0).Distinct().Take(300).ToList();
            if (ids.Count == 0) throw new ShopException("ids", "اختار أوردر الأول.");
            var errors = new List<string>(); int done = 0;
            foreach (var id in ids)
            {
                var o = ShopOrders.ById(c, sh.StoreId, id); if (o == null) { errors.Add("الأوردر ده مش موجود."); continue; }
                if (o.Status != "confirmed") { errors.Add(o.NoText + ": " + (on ? "«بيتجهز» بيتعلّم على الأوردر المتأكد بس، وده «" + ShopOrders.StatusText(o.Status) + "»." : "الأوردر ده «" + ShopOrders.StatusText(o.Status) + "» خلاص.")); continue; }
                if (on == PrepAt(o).HasValue) { done++; continue; }
                if (!Write(c, o, on ? DateTime.UtcNow : (DateTime?)null)) { errors.Add(o.NoText + ": الأوردر اتغيّر حالًا. حدّث الصفحة وجرّب تاني."); continue; }
                ShopOrders.Log_(c, null, sh.StoreId, o.Id, "owner", on ? "بيتجهز" : "اتشال «بيتجهز»"); done++;
            }
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "done", done }, { "errors", errors } }); return true;
        }

        // ShopHooks.OnStatus: back to new / no answer / cancelled = the mark goes (a returned order keeps its history)
        public static void Changed(SqlConnection c, int storeId, long id, string to)
        {
            if (to != "new" && to != "noreply" && to != "cancelled") return;
            try { var o = ShopOrders.ById(c, storeId, id); if (o != null && PrepAt(o).HasValue) Write(c, o, null); } catch (Exception ex) { Log.Error(ex); }
        }

        // the assistant, about one order
        public static string BotLine(ShopOrder o) { var at = PrepAt(o); return o.Status == "confirmed" && at.HasValue ? "المتجر بيجهّز الطلب دلوقتي (من " + ShopPromo.Local(at.Value).Replace("T", " ") + ")، ولسه ماخرجش للتوصيل.\n" : ""; }

        // what the customer is told when the company has news, in their words (owner-side words like «راجعة ليك» never)
        static readonly Dictionary<string, string[]> Said = new Dictionary<string, string[]> {
            { "made", new[] { "الشحنة اتسجّلت عند شركة الشحن", "Booked with the shipping company" } }, { "picked", new[] { "شركة الشحن استلمت الطلب", "The shipping company picked it up" } },
            { "transit", new[] { "الطلب في مخازن شركة الشحن", "At the shipping company's hub" } }, { "out", new[] { "المندوب خرج بيه للتوصيل", "Out for delivery" } },
            { "attempt", new[] { "المندوب حاول يوصّله ومعرفش", "A delivery attempt didn't go through" } } };
        static readonly string[] Left = { "shipped", "out", "delivered" };

        // the steps' times for the customer's pages: {k: co / self / pick / "", t: {at, confirmed, prep, shipped, out, delivered}, via, eta, last}
        public static Dictionary<string, object> Of(SqlConnection c, Shop sh, ShopOrder o)
        {
            string st = o.Status; bool left = Left.Contains(st), on = left || st == "confirmed";
            DateTime? outAt = null; bool viaShipped = st == "shipped";
            if (left)
                using (var cmd = Db.Cmd(c, "SELECT What, At FROM dbo.ShopOrderLog WHERE OrderId=@o AND StoreId=@s AND (What LIKE @a OR What LIKE @b) ORDER BY Id", "@o", o.Id, "@s", o.StoreId, "@a", "%← " + ShopOrders.StatusText("out") + "%", "@b", "%← " + ShopOrders.StatusText("shipped") + "%"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) { var w = (string)r[0]; if (w.Contains("← " + ShopOrders.StatusText("out"))) outAt = DateTime.SpecifyKind((DateTime)r[1], DateTimeKind.Utc); else viaShipped = true; }
            if (st == "out") outAt = o.StatusAt; else if (st != "delivered" || (outAt.HasValue && o.DeliveredAt.HasValue && outAt > o.DeliveredAt)) outAt = null;   // «out» now: when it became so
            Func<DateTime?, long> u = t => t.HasValue ? ShopView.Unix(t.Value) : 0;
            var prep = on ? PrepAt(o) : null;
            var t_ = new Dictionary<string, object> { { "at", u(o.CreatedAt) }, { "confirmed", on ? u(o.ConfirmedAt) : 0 }, { "prep", u(prep) }, { "shipped", left ? u(o.ShippedAt) : 0 }, { "out", u(outAt) }, { "delivered", st == "delivered" ? u(o.DeliveredAt) : 0 } };
            string k = o.Extras.ContainsKey("branch") ? "pick" : !string.IsNullOrEmpty(o.Carrier) || !string.IsNullOrEmpty(o.Tracking) ? "co" : o.Extras.ContainsKey("by") || ShopSelf.On(sh) ? "self" : "";
            var d = new Dictionary<string, object> { { "k", k }, { "t", t_ }, { "via", viaShipped } };
            // the company's latest news, while it's on its way
            if (k == "co" && (st == "shipped" || st == "out"))
                using (var cmd = Db.Cmd(c, "SELECT TOP 1 Ours, LastEventAt FROM dbo.Shipments WHERE StoreId=@s AND Source='shop' AND OrderId=@o AND State='live' ORDER BY Id DESC", "@s", o.StoreId, "@o", o.Id.ToString(CultureInfo.InvariantCulture)))
                using (var r = cmd.ExecuteReader())
                    if (r.Read()) { string[] w; var ours = r[0] as string ?? "made"; if (Said.TryGetValue(ours, out w)) d["last"] = new Dictionary<string, object> { { "t", w }, { "at", r[1] is DateTime ? u(DateTime.SpecifyKind((DateTime)r[1], DateTimeKind.Utc)) : 0 } }; }
            // «بوصّل بنفسي», once it left: the day the customer chose, or the store's days counted from the order
            if (k == "self" && (st == "shipped" || st == "out")) { var eta = Eta(c, sh, o); if (eta != null) d["eta"] = eta; }
            return d;
        }
        static Dictionary<string, object> Eta(SqlConnection c, Shop sh, ShopOrder o)
        {
            var day = ShopJson.Str(ShopJson.At(o.Extras, "day"), 10); DateTime dd; var today = Carriers.Cairo(DateTime.UtcNow).Date;
            if (DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dd)) return dd < today ? null : new Dictionary<string, object> { { "d", day }, { "p", ShopJson.Str(ShopJson.At(o.Extras, "dpart"), 4) } };
            try
            {
                var data = ShopCache.Get(c, sh); if (data == null) return null; string days; bool off; decimal min; ShopPrice.ShipFee(data, o.Gov, out days, out off, out min);
                var m = Regex.Match(ShopJson.Digits(days ?? ""), @"^\s*(\d{1,2})(?:\s*[-–]\s*(\d{1,2}))?\s*$"); if (!m.Success) return null;
                int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : a; if (b < a) { var x = a; a = b; b = x; }
                var from = Carriers.Cairo(o.CreatedAt).Date; if (from.AddDays(b) < today) return null;
                return new Dictionary<string, object> { { "a", from.AddDays(Math.Max(a, 0)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "b", from.AddDays(b).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } };
            }
            catch (Exception ex) { Log.Error(ex); return null; }
        }
    }
}
