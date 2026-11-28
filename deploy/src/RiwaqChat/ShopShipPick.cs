// «الزبون يختار شركة الشحن»: when the owner turns it on and at least two ways are on (the store's own delivery «self» and the companies
// linked in «الشحن»), the checkout shows each way with its own fee and days for the customer's governorate, city and cart. The customer
// picks one; ShopPrice.Quote works out its fee from the governorate's (ShipFee + ShopShipRate.Fee) and ShopOrders.Place checks the choice
// again against what is on now. Off, or fewer than two ways on: nothing changes anywhere (the quote, the order, the owner's numbers, the bot).
// The owner's settings: ship2.pick {on, opts: [{k, on, n: [ar, en], pm: gov|flat, pa, pf, dm: gov|own, d1, d2, free}]}
//   pm gov = the governorate's fee + pa (pa may be below zero), flat = pf everywhere; dm gov = the governorate's days, own = d1 to d2;
//   free = «شحن مجاني فوق» (the store's rule or a free-shipping offer) covers this way too. A free-shipping code covers every way.
// The order keeps what was chosen in ShopOrders.Extras.car {k, n, ne, d}; «ابعت للشحن» starts on that company.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RiwaqChat
{
    public static class ShopShipPick
    {
        public class Opt { public string K, N, Ne, Pm, Dm; public decimal Pa, Pf; public int D1, D2; public bool Free; }
        // one way for this cart and place: what it costs before and after free shipping, and its days
        public class Way { public Opt O; public decimal Full, Fee; public bool Free; public string Days; }

        public static readonly string[] Keys = { "self", "bosta", "shipblu", "jt", "aramex" };
        public static string ArName(string k) { if (k == "self") return "توصيل المتجر"; var co = Carriers.Of(k); return co == null ? k : co.Name; }
        public static string EnName(string k) { return k == "self" ? "Store delivery" : k == "bosta" ? "Bosta" : k == "shipblu" ? "ShipBlu" : k == "jt" ? "J&T Express" : k == "aramex" ? "Aramex" : k; }
        static string Txt(object v, int max) { return Regex.Replace(ShopJson.Str(v, max), @"[\u0000-\u001F<>]", " ").Trim(); }

        // ShopShip2.Clean: what the owner sent, kept to what makes sense
        public static Dictionary<string, object> Clean(Dictionary<string, object> x)
        {
            var opts = new List<object>(); var seen = new HashSet<string>();
            foreach (var o in ShopJson.List(ShopJson.At(x, "opts")).OfType<Dictionary<string, object>>())
            {
                var k = ShopJson.Str(ShopJson.At(o, "k"), 10); if (!Keys.Contains(k) || !seen.Add(k)) continue;
                var n = ShopJson.List(ShopJson.At(o, "n")); int d1 = ShopShip2.Clamp(ShopJson.Int(ShopJson.At(o, "d1"), 1), 0, 60), d2 = ShopShip2.Clamp(ShopJson.Int(ShopJson.At(o, "d2"), d1), d1, 60);
                opts.Add(ShopShip2.D("k", k, "on", ShopJson.Bool(ShopJson.At(o, "on")), "n", new object[] { Txt(n.Count > 0 ? n[0] : null, 40), Txt(n.Count > 1 ? n[1] : null, 40) },
                    "pm", ShopJson.Str(ShopJson.At(o, "pm")) == "flat" ? "flat" : "gov", "pa", ShopShip2.Clamp(ShopJson.Dec(ShopJson.At(o, "pa")), -5000, 5000), "pf", ShopShip2.Clamp(ShopJson.Dec(ShopJson.At(o, "pf")), 0, 5000),
                    "dm", ShopJson.Str(ShopJson.At(o, "dm")) == "own" ? "own" : "gov", "d1", d1, "d2", d2, "free", ShopJson.Bool(ShopJson.At(o, "free"), true)));
            }
            return ShopShip2.D("on", ShopJson.Bool(ShopJson.At(x, "on")), "opts", opts);
        }

        // the companies this store has linked: a minute in memory, forgotten when the owner links or unlinks one
        static readonly ConcurrentDictionary<int, Tuple<DateTime, HashSet<string>>> Linked = new ConcurrentDictionary<int, Tuple<DateTime, HashSet<string>>>();
        public static void Forget(int storeId) { Tuple<DateTime, HashSet<string>> t; Linked.TryRemove(storeId, out t); }
        static HashSet<string> LinkedOf(SqlConnection c, int storeId)
        {
            Tuple<DateTime, HashSet<string>> t;
            if (Linked.TryGetValue(storeId, out t) && t.Item1 > DateTime.UtcNow) return t.Item2;
            var l = new HashSet<string>();
            if (Shops.Ready && c != null) using (var cmd = Db.Cmd(c, "SELECT Company FROM dbo.Carriers WHERE StoreId=@s", "@s", storeId)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add((string)r[0]);
            Linked[storeId] = Tuple.Create(DateTime.UtcNow.AddMinutes(1), l); return l;
        }

        static Opt Parse(Dictionary<string, object> o)
        {
            var k = ShopJson.Str(ShopJson.At(o, "k"), 10); var n = ShopJson.List(ShopJson.At(o, "n"));
            string ar = Txt(n.Count > 0 ? n[0] : null, 40), en = Txt(n.Count > 1 ? n[1] : null, 40);
            return new Opt { K = k, N = ar.Length > 0 ? ar : ArName(k), Ne = en.Length > 0 ? en : ar.Length > 0 && !Regex.IsMatch(ar, @"\p{IsArabic}") ? ar : EnName(k),
                Pm = ShopJson.Str(ShopJson.At(o, "pm")) == "flat" ? "flat" : "gov", Pa = ShopJson.Dec(ShopJson.At(o, "pa")), Pf = Math.Max(0, ShopJson.Dec(ShopJson.At(o, "pf"))),
                Dm = ShopJson.Str(ShopJson.At(o, "dm")) == "own" ? "own" : "gov", D1 = Math.Max(0, ShopJson.Int(ShopJson.At(o, "d1"), 1)), D2 = Math.Max(0, ShopJson.Int(ShopJson.At(o, "d2"), 1)), Free = ShopJson.Bool(ShopJson.At(o, "free"), true) };
        }
        // the ways on now, in the owner's order; null = the choice is off, or fewer than two ways are on
        public static List<Opt> Active(SqlConnection c, Shop sh)
        {
            var s = ShopShip2.Of(sh, "pick"); if (sh == null || !ShopJson.Bool(ShopJson.At(s, "on"))) return null;
            var raw = ShopJson.List(ShopJson.At(s, "opts")).OfType<Dictionary<string, object>>().Where(o => ShopJson.Bool(ShopJson.At(o, "on")) && Keys.Contains(ShopJson.Str(ShopJson.At(o, "k"), 10))).ToList();
            if (raw.Count < 2) return null;
            HashSet<string> linked = null; var l = new List<Opt>();
            foreach (var o in raw)
            {
                var k = ShopJson.Str(ShopJson.At(o, "k"), 10);
                if (k != "self") { if (linked == null) linked = LinkedOf(c, sh.StoreId); if (!linked.Contains(k)) continue; }
                l.Add(Parse(o));
            }
            return l.Count >= 2 ? l : null;
        }
        // BOOT.shop.ship2.pick: the page loads ship2.js; which ways are really on comes with each sum
        public static bool Boot(Shop sh)
        {
            var s = ShopShip2.Of(sh, "pick");
            return ShopJson.Bool(ShopJson.At(s, "on")) && ShopJson.List(ShopJson.At(s, "opts")).OfType<Dictionary<string, object>>().Count(o => ShopJson.Bool(ShopJson.At(o, "on"))) >= 2;
        }

        public static decimal Fee(Opt o, decimal govFee) { return o.Pm == "flat" ? o.Pf : Math.Max(0, govFee + o.Pa); }
        public static string Days(Opt o, string govDays) { return o.Dm == "own" ? (o.D1 >= o.D2 ? o.D1.ToString() : o.D1 + "-" + o.D2) : govDays ?? ""; }

        // ShopPrice.Quote, after the governorate's fee: every way for this place and cart, and the one that counts (the customer's,
        // else the first in the owner's order). gov 0 or no way: the sum stays exactly as it was.
        // A company whose own list of areas says it doesn't deliver to this governorate or city isn't offered there (CarrierAreas.Delivers).
        // If that leaves a single way, it is the one: its fee and days, no cards, nothing to pick; none left: the sum stays as it was.
        public static void Quote(SqlConnection c, ShopData d, ShopQuote q, ShopHow how, int gov, string city, decimal govFee, string govDays, decimal over, decimal after, bool any, bool codeFree)
        {
            if (gov <= 0 || !q.Deliver) return; var l = Active(c, d.Row); if (l == null) return;
            l = l.Where(o => o.K == "self" || CarrierAreas.Delivers(c, d.Row.StoreId, o.K, gov, city)).ToList(); if (l.Count == 0) return;
            q.Ways = l.Select(o => { var full = Fee(o, govFee); bool free = codeFree || (o.Free && over >= 0 && after >= over && any); return new Way { O = o, Full = full, Fee = free ? 0 : full, Free = free, Days = Days(o, govDays) }; }).ToList();
            var want = how == null ? "" : how.Carrier ?? ""; var w = q.Ways.FirstOrDefault(x => x.O.K == want); q.CarOk = w != null || q.Ways.Count == 1; if (w == null) w = q.Ways[0];
            q.Car = w.O.K; q.ShipFull = w.Full; q.FreeShip = w.Free; q.FreeLeft = w.O.Free && over > 0 && !w.Free ? over - after : 0; q.Ship = w.Fee; q.Days = w.Days;
            // a way «شحن مجاني فوق» doesn't cover has no free shipping to reach: -1 («never», as FreeOver says it), so no bar says «الشحن مجاني
            // ليك» while it is charged (every bar shows only above 0; 0 would read «always free» in some templates)
            if (!w.O.Free && (how == null || string.IsNullOrEmpty(how.Branch) || ShopPrice.Branch(d.Row, how.Branch) == null)) q.FreeOver = -1;   // taken from a branch: as it was
        }
        static Way Chosen(ShopQuote q) { return q.Ways == null || q.Branch != null ? null : q.Ways.FirstOrDefault(x => x.O.K == q.Car); }

        // ShopPrice.Json: the ways only when there is a choice
        public static void Json(ShopQuote q, Dictionary<string, object> j)
        {
            if (q.Ways == null || q.Branch != null) return;
            j["copts"] = q.Ways.Select(w => (object)new Dictionary<string, object> { { "k", w.O.K }, { "n", ShopView.Pair(w.O.N, w.O.Ne) }, { "fee", w.Fee }, { "full", w.Full }, { "free", w.Free }, { "d", w.Days } }).ToArray();
            j["car"] = q.Car;
        }

        // ShopOrders.Place: a customer on the store's page must have picked one of the ways on now; the assistant's and the owner's orders
        // take the first when none was said
        public static void Check(ShopOrderIn o, ShopQuote q)
        {
            if (q.Ways == null || q.Branch != null || o.ByOwner || o.Channel != "web" || q.CarOk) return;
            bool en = o.Lang == "en", none = o.How == null || string.IsNullOrEmpty(o.How.Carrier);
            throw new ShopException("carrier", none ? (en ? "Choose who delivers your order." : "اختار هتستلم طلبك مع مين.") : (en ? "The delivery company you chose isn't available now. Choose again." : "شركة الشحن اللي اخترتها مش متاحة دلوقتي. اختار تاني."), ShopPrice.Json(q));
        }
        public static void Extra(ShopQuote q, Dictionary<string, object> ex)
        {
            var w = Chosen(q); if (w == null) return;
            ex["car"] = new Dictionary<string, object> { { "k", w.O.K }, { "n", w.O.N }, { "ne", w.O.Ne }, { "d", w.Days } };
        }

        // what the order says about it
        public static string KeyOf(ShopOrder o) { return o == null ? "" : ShopJson.Str(ShopJson.At(ShopJson.Sub(o.Extras, "car"), "k"), 10); }
        public static string NameOf(ShopOrder o, bool en)
        {
            var x = ShopJson.Sub(o == null ? null : o.Extras, "car"); if (x.Count == 0) return null;
            string n = ShopJson.Str(ShopJson.At(x, en ? "ne" : "n"), 40); return n.Length > 0 ? n : ShopJson.Str(ShopJson.At(x, "n"), 40);
        }
        public static string Line(ShopOrder o, bool en)
        {
            var n = NameOf(o, en); if (n == null || o.Extras.ContainsKey("branch")) return ""; var d = ShopJson.Str(ShopJson.At(ShopJson.Sub(o.Extras, "car"), "d"), 12);
            return (en ? "🚚 Delivered by: " : "🚚 هيوصلك مع: ") + n + (d.Length > 0 ? (en ? " (" + d + " days)" : " (" + d + " يوم)") : "") + "\n";
        }
        // the sum of an order whose place changed (the customer's account, the owner's edit): the same way, at the new place's fee
        public static ShopHow How(ShopOrder o) { var k = KeyOf(o); return k.Length == 0 ? null : new ShopHow { Carrier = k }; }
        public static decimal Refee(SqlConnection c, ShopData d, ShopOrder o, decimal govFee)
        {
            var k = KeyOf(o); if (k.Length == 0) return govFee; var l = Active(c, d.Row); var w = l == null ? null : l.FirstOrDefault(x => x.K == k);
            return w == null ? govFee : Fee(w, govFee);
        }

        // the store's assistant (ShopBot «shipping»)
        public static string BotText(SqlConnection c, ShopData d, int gov, string govDays, decimal govFee)
        {
            var l = Active(c, d.Row); if (l == null) return "";
            var sb = new StringBuilder(" الزبون بيختار هيستلم مع مين في صفحة الطلب: ");
            sb.Append(string.Join("، ", l.Select(o =>
            {
                string fee = gov > 0 ? ShopOrders.Money(Fee(o, govFee)) : o.Pm == "flat" ? ShopOrders.Money(o.Pf) : o.Pa == 0 ? "نفس سعر المحافظة" : o.Pa > 0 ? "سعر المحافظة + " + ShopOrders.Money(o.Pa) : "سعر المحافظة - " + ShopOrders.Money(-o.Pa);
                var days = Days(o, govDays); return o.N + " " + fee + (days.Length > 0 ? " (" + days + " يوم)" : "") + (o.Free ? "" : "، ومش داخل في الشحن المجاني");
            }))).Append(".");
            return sb.ToString();
        }
    }
}
