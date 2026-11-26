// SellyStore: three ways to bring customers back, each with its own switch in «متجري» («الهدايا والنقط»), all off by default:
//   ShopGift.cs    gift cards: bought on the store's /gift page (an order paid by transfer before the card exists), sent on WhatsApp, a
//                  payment at checkout (it lowers what is left to pay, never the price)
//   ShopPoints.cs  points on what was paid for the products, earned on delivery, used by a signed-in customer as a discount;
//                  «هات صاحبك» (ShopRefer): a friend's first order gets a gift, the sender a reward when that order is delivered
// This file: the switches, what the page sends with the cart (ShopRewIn), what the sum found (ShopRewQ), and the hooks the store calls:
// ShopPrice.Quote (Off before the total, Pay after it), ShopOrders.Place and SetStatus (inside their transactions), ShopPay.Review, a part
// return, the job, the bot, the owner's api/shop.ashx (a=rew...) and the customer's /_a/rew. Each hook returns at once when all are off.
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

namespace RiwaqChat
{
    // what the page sent with the cart: the code in «كارت هدية», the friend's link, the account (points)
    public class ShopRewIn
    {
        public string Card = "", Ref = "", Tk = "", Address = "", Ip; public bool Pts; public int Gov;
        public static ShopRewIn Of(Dictionary<string, object> b)
        {
            var w = new ShopRewIn { Card = ShopRew.Code(ShopJson.Str(ShopJson.At(b, "gc"), 40)), Ref = ShopRew.Code(ShopJson.Str(ShopJson.At(b, "ref"), 20)), Tk = Regex.IsMatch(ShopJson.Str(ShopJson.At(b, "tk"), 80), "^[a-f0-9]{64}$") ? ShopJson.Str(ShopJson.At(b, "tk"), 80) : "", Pts = ShopJson.Bool(ShopJson.At(b, "pts")) };
            if (w.Card.Length == 0 && w.Ref.Length == 0 && w.Tk.Length == 0) return null;
            w.Address = ShopJson.Str(ShopJson.At(b, "address"), 300); w.Gov = ShopJson.Int(ShopJson.At(b, "gov"));
            try { var x = HttpContext.Current; if (x != null) w.Ip = Web.Ip(x.Request); } catch { }
            return w;
        }
    }

    // what the sum worked out for these features (ShopQuote.Rw); the order takes exactly this inside its transaction
    public class ShopRewQ
    {
        public string Phone;                                                                              // signed in on this phone (points)
        public bool CardAsked; public int CardId; public string CardCode; public decimal CardUse, CardLeft; public DateTime? CardUntil; public string CardErr;
        public int CodeId; public decimal CodeOff; public string CodeErr;                                // a reward code of «هات صاحبك»
        public string RefCode, RefSender; public decimal RefOff, RefMin; public string RefErr; public bool RefWait, RefDead;
        public int PtsHave, PtsUse; public decimal PtsOff; public string PtsErr;
    }

    public static class ShopRew
    {
        public const int FriendId = -1, CodeOfferId = -2, PointsId = -3;   // their lines among the order's offers (real offers have ids > 0)

        // ---------- the switches ----------
        public static bool AnyOn(Shop sh) { return sh != null && (ShopGift.On(sh) || ShopPoints.On(sh) || ShopRefer.On(sh)); }
        // the owner's settings arrive with the rest ("settings"): kept, or cleaned when sent (ShopOwner.SaveSettings)
        public static void Clean(Dictionary<string, object> x, Dictionary<string, object> old, Dictionary<string, object> set)
        {
            foreach (var k in new[] { "gcard", "points", "refer" })
            {
                var had = ShopJson.Sub(old, k); var now = ShopJson.At(x, k) as Dictionary<string, object>;
                if (now == null && !old.ContainsKey(k)) continue;
                var all = new Dictionary<string, object>(had); if (now != null) foreach (var kv in now) all[kv.Key] = kv.Value;
                set[k] = k == "gcard" ? ShopGift.Clean(all) : k == "points" ? ShopPoints.Clean(all) : ShopRefer.Clean(all);
            }
        }
        // what the store's page may know (BOOT.shop.rw): nothing when all three are off
        public static object Public(Shop sh)
        {
            if (!AnyOn(sh)) return null;
            var d = new Dictionary<string, object>();
            if (ShopGift.On(sh)) d["gc"] = ShopGift.PublicSet(sh);
            if (ShopPoints.On(sh)) d["pts"] = ShopPoints.PublicSet(sh);
            if (ShopRefer.On(sh)) d["ref"] = ShopRefer.PublicSet(sh);
            return d;
        }
        // the page's own files, only when one of them is on (otherwise nothing changes for customers)
        public static string Tags(Shop sh)
        {
            if (!AnyOn(sh)) return "";
            return "<link rel=\"stylesheet\" href=\"/_s/rew.css?v=" + ShopPages.Ver("rew.css") + "\"><script src=\"/_s/rew.js?v=" + ShopPages.Ver("rew.js") + "\" defer></script>";
        }

        // ---------- small things ----------
        public static string Code(string s) { var t = Regex.Replace(ShopJson.Digits(s ?? "").ToUpperInvariant(), "[^A-Z0-9]", ""); return t.Length > 16 ? t.Substring(0, 16) : t; }
        public static string Sha(string s) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2"))); }
        static readonly TimeZoneInfo Cairo = Zone();
        static TimeZoneInfo Zone() { foreach (var id in new[] { "Egypt Standard Time", "Africa/Cairo" }) try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { } return TimeZoneInfo.CreateCustomTimeZone("Cairo", TimeSpan.FromHours(2), "Cairo", "Cairo"); }
        public static DateTime Local(DateTime utc) { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Cairo); }
        public static DateTime Utc(DateTime local) { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Cairo); }
        public static DateTime MonthStart() { var l = Local(DateTime.UtcNow); return Utc(new DateTime(l.Year, l.Month, 1)); }
        static readonly string[] Months = { "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر" };
        public static string Day(DateTime utc, bool en = false) { var l = Local(utc); return en ? l.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : l.Day + " " + Months[l.Month - 1] + " " + l.Year; }
        internal static SqlCommand Cmd(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { var cmd = Db.Cmd(c, sql, a); cmd.Transaction = tx; return cmd; }
        internal static int Exec(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { using (var cmd = Cmd(c, tx, sql, a)) return cmd.ExecuteNonQuery(); }
        internal static object One(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { using (var cmd = Cmd(c, tx, sql, a)) { var v = cmd.ExecuteScalar(); return v == DBNull.Value ? null : v; } }
        internal static int Num(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { var v = One(c, tx, sql, a); return v == null ? 0 : Convert.ToInt32(v); }
        internal static decimal Dec(SqlConnection c, SqlTransaction tx, string sql, params object[] a) { var v = One(c, tx, sql, a); return v == null ? 0 : Convert.ToDecimal(v); }
        internal static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        internal static void Ok(HttpResponse res, params object[] kv) { var d = D(kv); d["ok"] = true; Web.Json(res, 200, d); }
        internal static string T(ShopFrontHandler.Ctx x, string ar, string en) { return x.Lang == "en" ? en : ar; }
        internal static string Clip(string s, int n) { s = Regex.Replace((s ?? "").Trim(), @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", ""); return s.Length > n ? s.Substring(0, n) : s; }

        // who is signed in on this phone (the account's session, as ShopAccount keeps it)
        public static string WhoIs(SqlConnection c, SqlTransaction tx, int storeId, string tk)
        {
            if (!Regex.IsMatch(tk ?? "", "^[a-f0-9]{64}$")) return null;
            return One(c, tx, "SELECT Phone FROM dbo.ShopSessions WHERE Hash=@h AND StoreId=@s AND ExpiresAt>SYSUTCDATETIME()", "@h", Sha(tk), "@s", storeId) as string;
        }

        // a discount spread over the products (so each one keeps its real price with the order, for the profit) and a line among the offers
        internal static decimal Apply(ShopQuote q, decimal amt, int id, string name, string nameEn)
        {
            var lines = q.Lines.Where(l => l.P != null && l.Gift == 0 && l.Err != "gone" && l.Err != "out" && l.Net > 0).ToList();
            decimal b = lines.Sum(l => l.Net); amt = Math.Round(Math.Min(amt, b), 2); if (amt <= 0) return 0;
            decimal left = amt;
            for (int i = 0; i < lines.Count && left > 0; i++) { var x = i == lines.Count - 1 ? left : Math.Round(amt * lines[i].Net / b, 2); x = Math.Min(Math.Min(x, left), lines[i].Net); lines[i].Disc += x; left -= x; }
            foreach (var l in lines) { if (left <= 0) break; var x = Math.Min(left, l.Net); l.Disc += x; left -= x; }
            amt -= left; if (amt <= 0) return 0;
            q.Discount += amt; q.Offers.Add(new ShopApplied { Id = id, Name = name, NameEn = nameEn, Amount = amt });
            return amt;
        }

        // ---------- the cart's sum (ShopPrice.Quote) ----------
        // before the total: the friend's gift, a reward code, the points (discounts, never under the store's least order)
        public static decimal Off(SqlConnection c, ShopData d, ShopQuote q, ShopHow how, string phone, DateTime now, decimal after)
        {
            var w = how == null ? null : how.Rw; if (w == null || q.Items == 0 || !AnyOn(d.Row)) return 0;
            var r = q.Rw = new ShopRewQ { CardAsked = w.Card.Length > 0 && (ShopGift.On(d.Row) || ShopRefer.On(d.Row)) }; decimal off = 0;
            off += ShopRefer.Friend(c, d, q, w, r, phone, after);
            off += ShopRefer.Code(c, d, q, w, r, phone, after - off);
            off += ShopPoints.Use(c, d, q, w, r, phone, after - off);
            return off;
        }
        // after the total: a gift card pays what it can of what is left; what must be sent first shrinks with it
        public static void Pay(SqlConnection c, ShopData d, ShopQuote q, ShopHow how, DateTime now)
        {
            var w = how == null ? null : how.Rw; if (w == null || w.Card.Length == 0 || q.Items == 0 || q.Total <= 0 || !ShopGift.On(d.Row)) return;
            var r = q.Rw ?? (q.Rw = new ShopRewQ()); r.CardAsked = true; if (r.CodeId > 0 || r.CodeErr != null) return;
            ShopGift.Quote(c, d, q, w, r, now);
            if (r.CardUse > 0) q.Deposit = Math.Max(0, Math.Min(q.Deposit, q.Total - r.CardUse));
        }
        // the page's part of the sum
        public static object Json(ShopQuote q)
        {
            var r = q.Rw; if (r == null) return null;
            return D("gc", r.CardUse > 0 ? D("code", ShopGift.Mask(r.CardCode), "use", r.CardUse, "bal", r.CardLeft, "until", r.CardUntil.HasValue ? ShopView.Unix(r.CardUntil.Value) : 0) : null, "gcErr", r.CardErr ?? r.CodeErr ?? "",
                "rc", r.CodeOff > 0 ? (object)r.CodeOff : null, "ref", D("off", r.RefOff, "wait", r.RefWait, "min", r.RefMin, "err", r.RefErr ?? "", "dead", r.RefDead),
                "pts", D("have", r.PtsHave, "use", r.PtsUse, "off", r.PtsOff, "err", r.PtsErr ?? "", "in", r.Phone == null ? "" : ShopOrders.Local(r.Phone)),
                "left", Math.Max(0, q.Total - r.CardUse), "dep", q.Deposit);
        }

        static readonly Dictionary<string, string[]> CodeErrs = new Dictionary<string, string[]> {
            { "bad", new[] { "الكود اللي في خانة «كارت هدية» مش صح. شيله أو صحّحه.", "The code in “Gift card” isn't valid. Remove or fix it." } }, { "stop", new[] { "كارت الهدية ده موقوف. شيله عشان تكمّل.", "This gift card is stopped. Remove it to go on." } },
            { "exp", new[] { "كارت الهدية ده صلاحيته خلصت. شيله عشان تكمّل.", "This gift card has expired. Remove it to go on." } }, { "zero", new[] { "كارت الهدية ده رصيده خلص. شيله عشان تكمّل.", "This gift card has no balance left. Remove it to go on." } },
            { "many", new[] { "جرّبت أكواد كتير غلط. استنى ربع ساعة.", "Too many wrong codes. Wait a quarter of an hour." } }, { "used", new[] { "كود المكافأة ده اتستخدم قبل كده.", "This reward code was already used." } },
            { "exp2", new[] { "كود المكافأة ده صلاحيته خلصت.", "This reward code has expired." } }, { "mine", new[] { "كود المكافأة ده لصاحبه بس، بنفس رقمه.", "This reward code is for its owner's number only." } }, { "min", new[] { "الطلب صغير على الكود ده.", "The order is too small for this code." } } };

        // ---------- placing the order: inside its transaction (the store's row is held, so orders of one store go one after the other) ----------
        public static void Take(SqlConnection c, SqlTransaction tx, Shop row, long orderId, ShopQuote q, string phone, string lang, string ip)
        {
            var r = q.Rw; if (r == null) return;
            var ex = new Dictionary<string, object>(); int sid = row.StoreId; bool en = lang == "en";
            // a code the customer typed that can't pay now: the order isn't made with a total they didn't mean
            if (r.CardAsked && r.CardUse <= 0 && r.CodeOff <= 0) { string[] t; if (!CodeErrs.TryGetValue(r.CardErr ?? r.CodeErr ?? "bad", out t)) t = CodeErrs["bad"]; throw new ShopException("card", en ? t[1] : t[0], ShopPrice.Json(q)); }
            if (r.RefOff > 0) ex["ref"] = ShopRefer.TakeFriend(c, tx, sid, orderId, r, phone, q, en, ip);
            if (r.CodeOff > 0) ex["rc"] = ShopRefer.TakeCode(c, tx, sid, orderId, r, en);
            if (r.PtsUse > 0) ex["pts"] = ShopPoints.TakeUse(c, tx, sid, phone, orderId, r, en);
            if (r.CardUse > 0) ex["gc"] = ShopGift.TakeUse(c, tx, sid, orderId, r, en);
            if (ex.Count == 0) return;
            var all = ShopJson.Obj(One(c, tx, "SELECT Extras FROM dbo.ShopOrders WHERE Id=@o AND StoreId=@s", "@o", orderId, "@s", sid) as string); all["rw"] = ex;
            Exec(c, tx, "UPDATE dbo.ShopOrders SET Extras=@x WHERE Id=@o AND StoreId=@s", "@x", ShopJson.Write(all), "@o", orderId, "@s", sid);
        }

        // ---------- an order's status changed: inside SetStatus's transaction. null = fine, otherwise why it can't move ----------
        // Everything here is worked out again from what the order should hold in its new status, so it can't be given or taken twice.
        public static string OnStatus(SqlConnection c, SqlTransaction tx, ShopOrder o, string to)
        {
            if (!Shops.Ready || o == null) return null;
            if (o.Items.Count == 0 && o.Total == 0) return null;   // merged into another order: what it used went with it
            bool live = to != "cancelled" && to != "returned"; var rw = ShopJson.Sub(o.Extras, "rw"); string err;
            if (rw.ContainsKey("gc") && (err = ShopGift.Settle(c, tx, o, live, ShopJson.Sub(rw, "gc"))) != null) return err;
            if (rw.ContainsKey("pts") && (err = ShopPoints.SettleUse(c, tx, o, live, ShopJson.Sub(rw, "pts"))) != null) return err;
            if (rw.ContainsKey("rc") && (err = ShopRefer.SettleCode(c, tx, o, live, ShopJson.Sub(rw, "rc"))) != null) return err;
            var set = ShopJson.Obj(One(c, tx, "SELECT Settings FROM dbo.Shops WITH (NOLOCK) WHERE StoreId=@s", "@s", o.StoreId) as string);
            ShopPoints.SettleEarn(c, tx, o, to, set);
            ShopRefer.Settle(c, tx, o, to, set);
            if (o.Channel == ShopGift.Channel) ShopGift.SettleOrder(c, tx, o, live);
            return null;
        }
        // a part of a delivered order came back (ShopReturns, after its own transaction): the points of what came back go
        public static void Part(SqlConnection c, int storeId, long orderId)
        {
            if (!Shops.Ready) return;
            var o = ShopOrders.ById(c, storeId, orderId); if (o == null || o.Status != "delivered" || o.Phone.Length == 0) return;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    if (One(c, tx, "SELECT Status FROM dbo.ShopOrders WITH (UPDLOCK, ROWLOCK) WHERE Id=@o AND StoreId=@s", "@o", orderId, "@s", storeId) as string != "delivered") { tx.Rollback(); return; }
                    ShopPoints.SettleEarn(c, tx, o, o.Status, ShopJson.Obj(One(c, tx, "SELECT Settings FROM dbo.Shops WITH (NOLOCK) WHERE StoreId=@s", "@s", storeId) as string));
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
        }

        // ---------- the owner looked at a transfer (ShopPay.Review) ----------
        // The review writes what arrived as the order's Paid; a gift card's part is a payment too, so it's added back. Then a gift card
        // order that is paid becomes its card (ShopGift.OnPaid: the one function anything that marks an order paid calls).
        public static void Reviewed(SqlConnection c, StoreInfo s, Shop sh, long orderId)
        {
            if (!Shops.Ready) return;
            var o = ShopOrders.ById(c, sh.StoreId, orderId); if (o == null) return;
            var gc = ShopJson.Sub(ShopJson.Sub(o.Extras, "rw"), "gc"); decimal part = ShopJson.Dec(ShopJson.At(gc, "a"));
            if (part > 0 && o.Status != "cancelled" && o.Status != "returned") Exec(c, null, "UPDATE dbo.ShopOrders SET Paid=CASE WHEN Paid+@a>Total THEN Total ELSE Paid+@a END WHERE Id=@o AND StoreId=@s", "@a", part, "@o", orderId, "@s", sh.StoreId);
            ShopGift.OnPaid(c, sh.StoreId, orderId);
        }

        // ---------- what the messages say ----------
        // the owner's alert and the customer's confirmation: what a card paid and what the points or a friend took off
        public static string PayNote(ShopOrder o)
        {
            var rw = ShopJson.Sub(o.Extras, "rw"); if (rw.Count == 0) return ""; var t = new StringBuilder();
            var gc = ShopJson.Sub(rw, "gc"); if (gc.Count > 0) t.Append("🎁 كارت هدية دفع ").Append(ShopOrders.Money(ShopJson.Dec(ShopJson.At(gc, "a")))).Append(" (").Append(ShopJson.Str(ShopJson.At(gc, "c"))).Append(")\n");
            var p = ShopJson.Sub(rw, "pts"); if (p.Count > 0) t.Append("⭐ نقط: ").Append(ShopJson.Int(ShopJson.At(p, "n"))).Append(" نقطة بـ ").Append(ShopOrders.Money(ShopJson.Dec(ShopJson.At(p, "off")))).Append("\n");
            var f = ShopJson.Sub(rw, "ref"); if (f.Count > 0) t.Append("👥 هدية «هات صاحبك»: ").Append(ShopOrders.Money(ShopJson.Dec(ShopJson.At(f, "off")))).Append("\n");
            return t.ToString();
        }
        public static string ConfirmNote(ShopOrder o, bool en)
        {
            var gc = ShopJson.Sub(ShopJson.Sub(o.Extras, "rw"), "gc"); decimal a = ShopJson.Dec(ShopJson.At(gc, "a")); if (a <= 0) return "";
            Func<decimal, string> m = v => en ? "EGP " + v.ToString("#,0.##", CultureInfo.InvariantCulture) : ShopOrders.Money(v);
            return en ? "🎁 Paid by gift card: " + m(a) + "\n💵 Left to pay: *" + m(Math.Max(0, o.Total - a)) + "*\n" : "🎁 اتدفع بكارت هدية: " + m(a) + "\n💵 الباقي عليك: *" + m(Math.Max(0, o.Total - a)) + "*\n";
        }

        // ---------- the customer: /_a/rew {do} ----------
        public static void Front(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("shop-rw:" + x.Ip, 120, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            switch (ShopJson.Str(ShopJson.At(b, "do"), 10))
            {
                case "buy": ShopGift.Buy(c, x, b, res); return;
                case "bal": ShopGift.Balance(c, x, b, res); return;
                case "ref": ShopRefer.Look(c, x, b, res); return;
                case "me": Me(c, x, b, res); return;
                default: Web.Error(res, 404, "not_found"); return;
            }
        }
        // the account's part: points, what is coming, the history, the personal link and what it brought
        static void Me(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (x.Preview) { Ok(res, "me", D("pts", ShopPoints.On(x.Row) ? D("have", 0, "worth", 0, "coming", 0, "list", new object[0]) : null, "ref", ShopRefer.On(x.Row) ? D("link", x.Row.Url + "/r/TEST", "friends", 0, "list", new object[0]) : null, "test", true)); return; }
            var phone = WhoIs(c, null, x.Row.StoreId, ShopJson.Str(ShopJson.At(b, "tk"), 80)); if (phone == null) throw new ShopException("signin", T(x, "سجّل دخول الأول.", "Please sign in first."));
            Ok(res, "me", D("pts", ShopPoints.On(x.Row) ? ShopPoints.Mine(c, x, phone) : null, "ref", ShopRefer.On(x.Row) ? ShopRefer.Mine(c, x, phone) : null));
        }

        // ---------- the owner: api/shop.ashx a=rew... ----------
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (!a.StartsWith("rew", StringComparison.Ordinal)) return false;
            switch (a)
            {
                case "rew": Web.Json(res, 200, D("set", D("gcard", ShopGift.Clean(ShopJson.Sub(sh.Settings, "gcard")), "points", ShopPoints.Clean(ShopJson.Sub(sh.Settings, "points")), "refer", ShopRefer.Clean(ShopJson.Sub(sh.Settings, "refer"))),
                    "ways", ShopPrice.PayWays(sh).Count, "acct", ShopAccount.On(sh), "wa", WaNotify.CantSend(c, s) == null, "cards", ShopGift.Owner(c, sh, req.QueryString["q"] ?? ""), "pts", ShopPoints.Owner(c, sh), "ref", ShopRefer.Owner(c, sh))); return true;
                case "rew-card": { var d = ShopGift.OwnerCard(c, sh, ShopJson.Int(req.QueryString["id"])); if (d == null) Web.Error(res, 404, "not_found", "الكارت ده مش موجود."); else Web.Json(res, 200, d); return true; }
                case "rew-pts": Web.Json(res, 200, ShopPoints.OwnerOne(c, sh, ShopOrders.Mobile(req.QueryString["phone"] ?? "") ?? "")); return true;
                case "rew-report": Web.Json(res, 200, Report(c, sh.StoreId, req)); return true;
            }
            return false;
        }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res)
        {
            if (!a.StartsWith("rew-", StringComparison.Ordinal)) return false;
            switch (a)
            {
                case "rew-card-new": Ok(res, "id", ShopGift.Issue(c, s, sh, b)); return true;
                case "rew-card-stop": ShopGift.Stop(c, sh, ShopJson.Int(ShopJson.At(b, "id")), ShopJson.Bool(ShopJson.At(b, "stop"))); Ok(res); return true;
                case "rew-card-send": ShopGift.Resend(c, s, sh, ShopJson.Int(ShopJson.At(b, "id"))); Ok(res); return true;
                case "rew-pts-add": ShopPoints.Hand(c, sh, b); Web.Json(res, 200, ShopPoints.OwnerOne(c, sh, ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24)) ?? "")); return true;
            }
            return false;
        }
        // «التقارير»: cards sold and cards used apart from the sales (a card's money is counted once: when it's sold)
        public static Dictionary<string, object> Report(SqlConnection c, int storeId, HttpRequest req)
        {
            var today = Local(DateTime.UtcNow).Date; DateTime a, z;
            if (!DateTime.TryParseExact(req.QueryString["from"] ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out a)) a = today.AddDays(-29);
            if (!DateTime.TryParseExact(req.QueryString["to"] ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out z) || z > today) z = today;
            if (a > z) a = z; DateTime f = Utc(a), t = Utc(z.AddDays(1));
            object[] p = { "@s", storeId, "@f", f, "@t", t };
            int sold = Num(c, null, "SELECT COUNT(*) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Kind='sold' AND CreatedAt>=@f AND CreatedAt<@t", p);
            decimal soldEgp = Dec(c, null, "SELECT ISNULL(SUM(Amount),0) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Kind='sold' AND CreatedAt>=@f AND CreatedAt<@t", p);
            decimal hand = Dec(c, null, "SELECT ISNULL(SUM(Amount),0) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Kind='hand' AND CreatedAt>=@f AND CreatedAt<@t", p);
            decimal used = -Dec(c, null, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopGiftMoves WHERE StoreId=@s AND Kind IN ('use','back') AND At>=@f AND At<@t", p);
            int ptsGiven = Num(c, null, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopPoints WHERE StoreId=@s AND Kind IN ('earn','back','refer','unref') AND At>=@f AND At<@t", p);
            int ptsUsed = -Num(c, null, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopPoints WHERE StoreId=@s AND Kind IN ('use','undo') AND At>=@f AND At<@t", p);
            decimal ptsOff = 0; using (var cmd = Db.Cmd(c, "SELECT Extras FROM dbo.ShopOrders WHERE StoreId=@s AND CreatedAt>=@f AND CreatedAt<@t AND Status NOT IN ('cancelled','returned') AND Extras LIKE '%\"rw\"%'", p)) using (var r = cmd.ExecuteReader()) while (r.Read()) ptsOff += ShopJson.Dec(ShopJson.At(ShopJson.Obj(r[0] as string), "rw", "pts", "off"));
            int friends = Num(c, null, "SELECT COUNT(*) FROM dbo.ShopReferrals WHERE StoreId=@s AND Status<>'void' AND CreatedAt>=@f AND CreatedAt<@t", p);
            decimal refOff = Dec(c, null, "SELECT ISNULL(SUM(Gift),0) FROM dbo.ShopReferrals WHERE StoreId=@s AND Status<>'void' AND CreatedAt>=@f AND CreatedAt<@t", p);
            return D("from", a.ToString("yyyy-MM-dd"), "to", z.ToString("yyyy-MM-dd"), "cards", D("sold", sold, "soldEgp", soldEgp, "hand", hand, "used", used), "pts", D("given", ptsGiven, "used", ptsUsed, "off", ptsOff), "ref", D("friends", friends, "off", refOff));
        }

        // ---------- the assistant ----------
        public static string BotPrompt(Shop sh)
        {
            if (!AnyOn(sh)) return ""; var sb = new StringBuilder();
            if (ShopGift.On(sh)) sb.Append(ShopGift.BotText(sh)).Append("\n");
            if (ShopPoints.On(sh)) sb.Append(ShopPoints.BotText(sh)).Append("\n");
            if (ShopRefer.On(sh)) sb.Append(ShopRefer.BotText(sh)).Append("\n");
            return sb.ToString();
        }
        public static void BotTool(List<ChatHandler.ExtraTool> list, int storeId, string ip, bool preview) { ShopGift.Tool(list, storeId, ip, preview); }

        // ---------- the job (Jobs, the «due» lane): gift cards at their time, friends' rewards; never at night ----------
        public static async Task Due(SqlConnection c, CancellationToken ct)
        {
            if (!Shops.Ready || WaNotify.QuietUntil().HasValue) return;
            await ShopGift.SendDue(c, ct);
            await ShopRefer.TellDue(c, ct);
        }

        // «امسح بياناتي»: the person's points, link and name on cards go; a card's money stays (its code works for whoever holds it)
        public static void Wipe(SqlConnection c, int storeId, string phone)
        {
            if (!Shops.Ready || string.IsNullOrEmpty(phone)) return;
            Exec(c, null, @"DELETE FROM dbo.ShopPoints WHERE StoreId=@s AND Phone=@p; DELETE FROM dbo.ShopReferrers WHERE StoreId=@s AND Phone=@p;
                UPDATE dbo.ShopReferrals SET Sender='' WHERE StoreId=@s AND Sender=@p; UPDATE dbo.ShopReferrals SET Friend='' , Ip=NULL WHERE StoreId=@s AND Friend=@p;
                UPDATE dbo.ShopGiftCards SET BuyerName=NULL, BuyerPhone=NULL WHERE StoreId=@s AND BuyerPhone=@p; UPDATE dbo.ShopGiftCards SET ToName=NULL, ToPhone=NULL, Msg=NULL WHERE StoreId=@s AND ToPhone=@p", "@s", storeId, "@p", phone);
        }

        public const string Schema = @"
IF OBJECT_ID('dbo.ShopGiftCards') IS NULL
BEGIN
  CREATE TABLE dbo.ShopGiftCards(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Code NVARCHAR(16) NOT NULL, Amount DECIMAL(12,2) NOT NULL, Balance DECIMAL(12,2) NOT NULL, ExpiresAt DATETIME2 NULL,
    Status NVARCHAR(8) NOT NULL DEFAULT 'active',             -- active / stopped
    Kind NVARCHAR(6) NOT NULL DEFAULT 'sold',                 -- sold (an order paid for it) / hand (the owner gave it)
    OrderId BIGINT NULL, BuyerName NVARCHAR(80) NULL, BuyerPhone NVARCHAR(20) NULL, ToName NVARCHAR(80) NULL, ToPhone NVARCHAR(20) NULL, Msg NVARCHAR(300) NULL, Lang CHAR(2) NOT NULL DEFAULT 'ar',
    SendAt DATETIME2 NULL, SentAt DATETIME2 NULL, SendTry DATETIME2 NULL, Sends INT NOT NULL DEFAULT 0, SendErr NVARCHAR(200) NULL, ToldAt DATETIME2 NULL, Note NVARCHAR(200) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_ShopGiftCards_Code ON dbo.ShopGiftCards(StoreId, Code);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopGiftCards_Order ON dbo.ShopGiftCards(OrderId) WHERE OrderId IS NOT NULL;';
  CREATE INDEX IX_ShopGiftCards_Send ON dbo.ShopGiftCards(SentAt, SendAt);
END
-- every movement of a card's balance: issue / use / back (a cancelled order) / stop / start / hand
IF OBJECT_ID('dbo.ShopGiftMoves') IS NULL
BEGIN
  CREATE TABLE dbo.ShopGiftMoves(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, CardId INT NOT NULL, OrderId BIGINT NULL,
    Kind NVARCHAR(6) NOT NULL, Delta DECIMAL(12,2) NOT NULL, After_ DECIMAL(12,2) NOT NULL, Who NVARCHAR(20) NOT NULL, Note NVARCHAR(200) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopGiftMoves_Card ON dbo.ShopGiftMoves(CardId, Id);
  CREATE INDEX IX_ShopGiftMoves_Order ON dbo.ShopGiftMoves(OrderId);
END
-- points: one row per movement. A row that adds keeps what is left of it (Rest) until it's used or its date passes; a take that found
-- too little leaves a debt (Rest below 0) that the next points pay. earn / back (a return) / use / undo (a cancelled order) / hand / refer / unref
IF OBJECT_ID('dbo.ShopPoints') IS NULL
BEGIN
  CREATE TABLE dbo.ShopPoints(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL,
    Kind NVARCHAR(6) NOT NULL, Delta INT NOT NULL, Rest INT NOT NULL DEFAULT 0, ExpiresAt DATETIME2 NULL, OrderId BIGINT NULL, Note NVARCHAR(200) NULL, Who NVARCHAR(20) NOT NULL DEFAULT 'system', At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopPoints_Phone ON dbo.ShopPoints(StoreId, Phone, Id);
  CREATE INDEX IX_ShopPoints_Order ON dbo.ShopPoints(OrderId);
END
-- «هات صاحبك»: a customer's own link, and each friend's first order (pending / rewarded / capped = over the month's most / void)
IF OBJECT_ID('dbo.ShopReferrers') IS NULL
BEGIN
  CREATE TABLE dbo.ShopReferrers(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL, Code NVARCHAR(12) NOT NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), PRIMARY KEY(StoreId, Phone));
  CREATE UNIQUE INDEX UX_ShopReferrers_Code ON dbo.ShopReferrers(StoreId, Code);
END
IF OBJECT_ID('dbo.ShopReferrals') IS NULL
BEGIN
  CREATE TABLE dbo.ShopReferrals(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Code NVARCHAR(12) NOT NULL, Sender NVARCHAR(20) NOT NULL, Friend NVARCHAR(20) NOT NULL,
    OrderId BIGINT NOT NULL, OrderNo INT NOT NULL, Status NVARCHAR(8) NOT NULL DEFAULT 'pending', Gift DECIMAL(12,2) NOT NULL DEFAULT 0, Ip NVARCHAR(64) NULL,
    RewardPts INT NULL, RewardAmt DECIMAL(12,2) NULL, RewardCode NVARCHAR(12) NULL, CodeUntil DATETIME2 NULL, CodeOrder BIGINT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), RewardAt DATETIME2 NULL, ToldAt DATETIME2 NULL);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopReferrals_Friend ON dbo.ShopReferrals(StoreId, Friend) WHERE Status<>''void'' AND Friend<>'''';';
  CREATE UNIQUE INDEX UX_ShopReferrals_Order ON dbo.ShopReferrals(OrderId);
  CREATE INDEX IX_ShopReferrals_Sender ON dbo.ShopReferrals(StoreId, Sender, Id);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopReferrals_Code ON dbo.ShopReferrals(StoreId, RewardCode) WHERE RewardCode IS NOT NULL;';
END
";
    }
}
