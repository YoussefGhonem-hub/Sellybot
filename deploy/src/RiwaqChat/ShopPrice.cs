// Prices: offers, coupons, shipping, and the cart's sum. Worked out here only, from the rows in the database; never by the browser or
// the assistant. Offer types (ShopOffers.Type, Rule):
//   auto  {kind: pct|amt, value, scope}                  the product's own price goes down while the offer runs
//   bxgy  {buy, get, pct (100 = free), max, scope}       buy 2 and the third at half price: the cheapest pieces get it
//   qty   {tiers: [{q, pct}], scope}                     the more of one product, the cheaper the piece
//   ship  {over, govs}                                   free shipping above an amount and/or to these governorates
// scope: {all} or {cats: [..]} (a category covers its sub-categories) or {prods: [..]}, and {not: [..]} products left out.
// Offers never add up unless the owner allows it (Settings.stack): a product whose price is already down takes no second offer, each
// line takes the one offer that saves most, and a coupon either covers the products without an offer or replaces the offers,
// whichever is better for the customer. Free shipping is not counted as a discount here.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;

namespace RiwaqChat
{
    public class ShopLine
    {
        public int VariantId, Qty; public string Extra;
        public ShopProd P; public ShopVar V;
        public decimal Full, Unit, Disc;            // list price, price after the product's own offer, discount on the whole line from cart offers and the coupon
        public decimal? Old; public ShopOffer Auto; public string Err; public int Max;
        public decimal Sum { get { return Unit * Qty; } }
        public decimal Net { get { return Sum - Disc; } }
    }

    public class ShopApplied { public int Id; public string Name, NameEn; public decimal Amount; }

    public class ShopQuote
    {
        public List<ShopLine> Lines = new List<ShopLine>(); public decimal Items, Sub, Discount, CouponOff, Ship, ShipFull, Total, FreeLeft, FreeOver, Min; public bool FreeShip, Deliver = true, CouponUsed;
        public string Coupon, CouponErr, Days, Stop; public int CouponId; public List<ShopApplied> Offers = new List<ShopApplied>(); public bool Bad { get { return Lines.Any(l => l.Err != null) || Stop != null; } }
    }

    public class ShopCoupon { public int Id; public string Code, Type, ScopeJson; public decimal Value; public decimal? MaxOff, MinSub; public DateTime? StartsAt, EndsAt; public int? MaxUses, PerPhone; public bool FirstOnly, PopupPhone, Active, Popup; public int Uses; }

    public static class ShopPrice
    {
        static decimal Round(decimal v) { return Math.Max(0, Math.Round(v, 0, MidpointRounding.AwayFromZero)); }
        static string Money(decimal v) { return v.ToString(v == Math.Floor(v) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture) + " ج.م"; }

        // one piece's price now; old = the price it's struck from (null = none); of = the offer that made it
        public static decimal Unit(ShopData d, ShopProd p, ShopVar v, DateTime now, out decimal? old, out ShopOffer of)
        {
            decimal list = p.PriceOf(v); of = null; old = p.OldOf(v);
            decimal best = list;
            foreach (var o in d.Offers)
            {
                if (o.Type != "auto" || !o.Running(now) || !o.Covers(p, d)) continue;
                var val = ShopJson.Dec(ShopJson.At(o.Rule, "value"));
                if (val <= 0) continue;
                decimal x = ShopJson.Str(ShopJson.At(o.Rule, "kind")) == "amt" ? list - val : list * (1 - Math.Min(95, val) / 100m);
                x = x < list ? Round(x) : list;
                if (x < best && x > 0) { best = x; of = o; }
            }
            if (of != null && (!old.HasValue || old.Value < list)) old = list;
            if (old.HasValue && old.Value <= best) old = null;
            return best;
        }

        // ---------- shipping ----------
        public static decimal? ShipFee(ShopData d, int gov, out string days, out bool off, out decimal min)
        {
            var sh = ShopJson.Sub(d.Row.Settings, "ship"); days = ShopJson.Str(ShopJson.At(sh, "days"), 30); off = false; min = ShopJson.Dec(ShopJson.At(ShopJson.Sub(d.Row.Settings, "order"), "min"));
            var fee = ShopJson.Dec(ShopJson.At(sh, "fee"));
            ShopZone z;
            if (gov > 0 && d.Zones.TryGetValue(gov, out z))
            {
                off = z.Off;
                if (!string.IsNullOrEmpty(z.Days)) days = z.Days;
                if (z.MinSub.HasValue && z.MinSub.Value > min) min = z.MinSub.Value;
                if (ShopJson.Str(ShopJson.At(sh, "mode")) == "zones" && z.Fee.HasValue) fee = z.Fee.Value;
            }
            return fee;
        }

        // ---------- the cart's sum ----------
        // gov 0 = not chosen yet (no shipping in the sum, only «فاضلك كام على الشحن المجاني»)
        public static ShopQuote Quote(SqlConnection c, ShopData d, List<ShopLine> lines, string coupon, int gov, string phone, DateTime now, bool checkStock = true)
        {
            var q = new ShopQuote();
            var set = d.Row.Settings;
            bool stack = ShopJson.Bool(ShopJson.At(set, "stack"));
            int maxQty = Math.Max(1, ShopJson.Int(ShopJson.At(ShopJson.Sub(set, "order"), "maxQty"), 20));
            foreach (var g in lines.Where(l => l.Qty > 0).GroupBy(l => l.VariantId + "|" + (l.Extra ?? "")).Take(40))
            {
                var l = g.First(); l.Qty = Math.Min(999, g.Sum(x => x.Qty)); l.Disc = 0; l.Err = null;
                ShopVar v; if (!d.Vars.TryGetValue(l.VariantId, out v)) { l.Err = "gone"; q.Lines.Add(l); continue; }
                l.V = v; l.P = d.ById[v.ProductId];
                l.Full = l.P.PriceOf(v); l.Unit = Unit(d, l.P, v, now, out l.Old, out l.Auto);
                q.Lines.Add(l);
            }
            // what can be bought: hidden, sold out, more than there is, under the least quantity
            foreach (var l in q.Lines.Where(x => x.P != null))
            {
                if (l.P.Status != "active" || !l.V.Active) { l.Err = "gone"; continue; }
                int same = q.Lines.Where(x => x.VariantId == l.VariantId).Sum(x => x.Qty), ofProduct = q.Lines.Where(x => x.P == l.P).Sum(x => x.Qty);
                int cap = l.P.MaxQty ?? maxQty;
                l.Max = l.P.Track && l.P.OutMode != "order" ? Math.Min(cap, Math.Max(0, l.V.Stock)) : cap;
                if (!checkStock) continue;
                if (l.Max == 0) l.Err = "out";
                else if (l.P.Track && l.P.OutMode != "order" && same > l.V.Stock) l.Err = "stock";
                else if (ofProduct > cap) l.Err = "max";
                else if (ofProduct < l.P.MinQty) l.Err = "min";
            }
            var ok = q.Lines.Where(l => l.P != null && l.Err != "gone" && l.Err != "out").ToList();
            q.Items = ok.Sum(l => l.Qty);

            // the offers (A), and the list prices with the coupon alone (B): the better one for the customer
            ShopCoupon cp = null;
            if (!string.IsNullOrWhiteSpace(coupon))
            {
                cp = FindCoupon(c, d.Row.StoreId, coupon);
                q.CouponErr = cp == null ? "الكود ده مش موجود." : CouponProblem(c, d, cp, phone, now);
                if (q.CouponErr != null) cp = null;
            }
            var applied = CartOffers(d, ok, stack, now);
            decimal offerOff = applied.Sum(a => a.Amount);
            decimal sub = ok.Sum(l => l.Sum);
            if (cp != null)
            {
                if (cp.Type == "ship") { if (cp.MinSub.HasValue && sub - offerOff < cp.MinSub.Value) q.CouponErr = "الكود ده للطلبات من " + Money(cp.MinSub.Value) + "."; else { q.Coupon = cp.Code; q.CouponId = cp.Id; q.CouponUsed = true; } }
                else
                {
                    decimal onFree = CouponOff(d, cp, ok, stack ? (Func<ShopLine, bool>)(l => true) : (l => l.Auto == null && l.Disc == 0), l => l.Net, sub - offerOff);
                    decimal full = ok.Sum(l => l.Full * l.Qty);
                    decimal alone = stack ? 0 : CouponOff(d, cp, ok, l => true, l => l.Full * l.Qty, full);
                    if (onFree <= 0 && alone <= 0)
                        q.CouponErr = cp.MinSub.HasValue && sub - offerOff < cp.MinSub.Value ? "الكود ده للطلبات من " + Money(cp.MinSub.Value) + "." : "الكود ده مش على المنتجات اللي في السلة، أو عليها عرض أحسن منه.";
                    else if (!stack && full - alone < sub - offerOff - onFree)
                    {
                        // the coupon replaces the offers
                        foreach (var l in ok) { l.Unit = l.Full; l.Auto = null; l.Disc = 0; l.Old = l.P.OldOf(l.V); if (l.Old.HasValue && l.Old.Value <= l.Unit) l.Old = null; }
                        applied.Clear(); offerOff = 0; sub = full;
                        Spread(ok.Where(l => InScope(d, cp, l.P)).ToList(), alone, l => l.Full * l.Qty);
                        q.CouponOff = alone; q.Coupon = cp.Code; q.CouponId = cp.Id; q.CouponUsed = true;
                    }
                    else if (onFree > 0)
                    {
                        Spread(ok.Where(l => InScope(d, cp, l.P) && (stack || (l.Auto == null && l.Disc == 0))).ToList(), onFree, l => l.Net);
                        q.CouponOff = onFree; q.Coupon = cp.Code; q.CouponId = cp.Id; q.CouponUsed = true;
                    }
                    else q.CouponErr = "عندك عرض أحسن من الكود، فحسبناه ليك.";
                }
            }
            // the products' own offers, for the order and the result of each offer
            foreach (var g in ok.Where(l => l.Auto != null).GroupBy(l => l.Auto))
                applied.Insert(0, new ShopApplied { Id = g.Key.Id, Name = g.Key.Name, NameEn = g.Key.NameEn, Amount = 0 });
            q.Offers = applied; q.Sub = sub; q.Discount = offerOff + q.CouponOff;
            decimal after = q.Sub - q.Discount;

            // shipping
            string days; bool off; decimal min;
            var fee = ShipFee(d, gov, out days, out off, out min) ?? 0;
            q.Days = days; q.Min = min; q.Deliver = !off; q.ShipFull = fee;
            decimal over = FreeOver(d, gov, now);
            q.FreeOver = over;
            q.FreeShip = (over >= 0 && after >= over && ok.Count > 0) || (cp != null && cp.Type == "ship" && q.CouponUsed);
            q.FreeLeft = over > 0 && !q.FreeShip ? over - after : 0;
            q.Ship = gov > 0 && !q.FreeShip ? fee : 0;
            q.Total = after + q.Ship;

            if (ok.Count == 0) q.Stop = "empty";
            else if (after < min) q.Stop = "min";
            else if (gov > 0 && off) q.Stop = "nodelivery";
            if (Vacation(d, now)) q.Stop = "vacation";
            return q;
        }

        public static bool Vacation(ShopData d, DateTime now)
        {
            var va = ShopJson.Sub(d.Row.Settings, "vacation");
            if (!ShopJson.Bool(ShopJson.At(va, "on"))) return false;
            DateTime until; var t = ShopJson.Str(ShopJson.At(va, "until"), 10);
            return t.Length == 0 || !DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out until) || now.AddHours(2).Date <= until;
        }

        // free shipping above this much (0 = always, -1 = never): the store's own rule, or the best running offer for the governorate
        public static decimal FreeOver(ShopData d, int gov, DateTime now)
        {
            var own = ShopJson.Dec(ShopJson.At(ShopJson.Sub(d.Row.Settings, "ship"), "freeOver")); decimal best = own > 0 ? own : -1;
            foreach (var o in d.Offers.Where(x => x.Type == "ship" && x.Running(now)))
            {
                var govs = ShopJson.List(ShopJson.At(o.Rule, "govs")).Select(x => ShopJson.Int(x)).ToList();
                if (govs.Count > 0 && !govs.Contains(gov)) continue;
                var over = Math.Max(0, ShopJson.Dec(ShopJson.At(o.Rule, "over")));
                if (best < 0 || over < best) best = over;
            }
            return best;
        }

        // ---------- cart offers (bxgy, qty): each line takes the offer that saves most ----------
        class Try { public ShopOffer O; public decimal Amount; public Dictionary<ShopLine, decimal> By = new Dictionary<ShopLine, decimal>(); }

        static List<ShopApplied> CartOffers(ShopData d, List<ShopLine> lines, bool stack, DateTime now)
        {
            var res = new List<ShopApplied>();
            var offers = d.Offers.Where(o => (o.Type == "bxgy" || o.Type == "qty") && o.Running(now)).ToList();
            var free = new HashSet<ShopLine>(lines.Where(l => stack || l.Auto == null));
            while (offers.Count > 0 && free.Count > 0)
            {
                Try best = null;
                foreach (var o in offers)
                {
                    var t = Work(d, o, lines.Where(l => free.Contains(l) && o.Covers(l.P, d)).ToList());
                    if (t != null && t.Amount > 0 && (best == null || t.Amount > best.Amount)) best = t;
                }
                if (best == null) break;
                foreach (var kv in best.By) { kv.Key.Disc += kv.Value; free.Remove(kv.Key); }
                res.Add(new ShopApplied { Id = best.O.Id, Name = best.O.Name, NameEn = best.O.NameEn, Amount = best.Amount });
                offers.Remove(best.O);
            }
            return res;
        }

        static Try Work(ShopData d, ShopOffer o, List<ShopLine> lines)
        {
            var t = new Try { O = o };
            if (lines.Count == 0) return null;
            if (o.Type == "bxgy")
            {
                int buy = Math.Max(1, ShopJson.Int(ShopJson.At(o.Rule, "buy"), 2)), get = Math.Max(1, ShopJson.Int(ShopJson.At(o.Rule, "get"), 1)), max = ShopJson.Int(ShopJson.At(o.Rule, "max"));
                var pct = Math.Max(1, Math.Min(100, ShopJson.Dec(ShopJson.At(o.Rule, "pct"), 100)));
                var units = new List<ShopLine>();
                foreach (var l in lines) for (int i = 0; i < l.Qty && units.Count < 2000; i++) units.Add(l);
                int sets = units.Count / (buy + get);
                if (max > 0) sets = Math.Min(sets, max);
                if (sets == 0) return null;
                foreach (var l in units.OrderBy(x => x.Unit).Take(sets * get))
                {
                    decimal off = pct >= 100 ? l.Unit : Round(l.Unit * pct / 100m), had;
                    t.By[l] = (t.By.TryGetValue(l, out had) ? had : 0) + off; t.Amount += off;
                }
                // every line that was part of the set is taken, not only the cheapest ones
                foreach (var l in lines) if (!t.By.ContainsKey(l)) t.By[l] = 0;
            }
            else
            {
                var tiers = ShopJson.List(ShopJson.At(o.Rule, "tiers")).OfType<Dictionary<string, object>>().Select(x => new { q = ShopJson.Int(ShopJson.At(x, "q")), pct = ShopJson.Dec(ShopJson.At(x, "pct")) }).Where(x => x.q > 1 && x.pct > 0 && x.pct < 100).OrderByDescending(x => x.q).ToList();
                foreach (var g in lines.GroupBy(l => l.P))
                {
                    int n = g.Sum(l => l.Qty); var tier = tiers.FirstOrDefault(x => n >= x.q);
                    if (tier == null) continue;
                    foreach (var l in g) { var off = Round(l.Unit * tier.pct / 100m) * l.Qty; t.By[l] = off; t.Amount += off; }
                }
            }
            return t;
        }

        // ---------- coupons ----------
        public static string CodeNorm(string code) { return (ShopJson.Digits(code ?? "")).Trim().ToUpperInvariant().Replace(" ", ""); }
        public static ShopCoupon FindCoupon(SqlConnection c, int storeId, string code)
        {
            code = CodeNorm(code); if (code.Length == 0 || code.Length > 30) return null;
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopCoupons WHERE StoreId=@s AND Code=@c", "@s", storeId, "@c", code))
            using (var r = cmd.ExecuteReader()) return r.Read() ? ReadCoupon(r) : null;
        }
        public static ShopCoupon ReadCoupon(System.Data.IDataRecord r)
        {
            return new ShopCoupon { Id = (int)r["Id"], Code = (string)r["Code"], Type = (string)r["Type"], Value = (decimal)r["Value"], MaxOff = r["MaxOff"] as decimal?, MinSub = r["MinSub"] as decimal?, ScopeJson = r["Scope"] as string,
                StartsAt = r["StartsAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["StartsAt"], DateTimeKind.Utc) : (DateTime?)null, EndsAt = r["EndsAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["EndsAt"], DateTimeKind.Utc) : (DateTime?)null,
                MaxUses = r["MaxUses"] as int?, PerPhone = r["PerPhone"] as int?, FirstOnly = (bool)r["FirstOnly"], Active = (bool)r["Active"], Popup = (bool)r["Popup"], PopupPhone = (bool)r["PopupPhone"], Uses = (int)r["Uses"] };
        }
        static string CouponProblem(SqlConnection c, ShopData d, ShopCoupon cp, string phone, DateTime now)
        {
            if (!cp.Active || (cp.StartsAt.HasValue && cp.StartsAt.Value > now)) return "الكود ده مش شغال دلوقتي.";
            if (cp.EndsAt.HasValue && cp.EndsAt.Value <= now) return "الكود ده خلص.";
            if (cp.MaxUses.HasValue && cp.Uses >= cp.MaxUses.Value) return "الكود ده خلص، اتستخدم العدد المسموح.";
            if (!string.IsNullOrEmpty(phone) && (cp.PerPhone.HasValue || cp.FirstOnly))
            {
                int used, before;
                using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CASE WHEN Coupon=@k THEN 1 ELSE 0 END),0), COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p AND Status<>'cancelled' AND Test=0", "@k", cp.Code, "@s", d.Row.StoreId, "@p", phone))
                using (var r = cmd.ExecuteReader()) { r.Read(); used = (int)r[0]; before = (int)r[1]; }
                if (cp.FirstOnly && before > 0) return "الكود ده لأول طلب بس.";
                if (cp.PerPhone.HasValue && used >= cp.PerPhone.Value) return "استخدمت الكود ده قبل كده.";
            }
            return null;
        }
        static bool InScope(ShopData d, ShopCoupon cp, ShopProd p)
        {
            if (string.IsNullOrEmpty(cp.ScopeJson)) return true;
            return new ShopOffer { RuleJson = "{\"scope\":" + cp.ScopeJson + "}" }.Covers(p, d);
        }
        static decimal CouponOff(ShopData d, ShopCoupon cp, List<ShopLine> lines, Func<ShopLine, bool> may, Func<ShopLine, decimal> worth, decimal sub)
        {
            if (cp.MinSub.HasValue && sub < cp.MinSub.Value) return 0;
            decimal b = lines.Where(l => may(l) && InScope(d, cp, l.P)).Sum(worth);
            if (b <= 0) return 0;
            decimal off = cp.Type == "amt" ? cp.Value : Round(b * Math.Min(95, cp.Value) / 100m);
            if (cp.MaxOff.HasValue && cp.MaxOff.Value > 0) off = Math.Min(off, cp.MaxOff.Value);
            return Math.Min(off, b);
        }
        // a discount on the whole cart, shared over its lines (so each product's real price is kept with the order)
        static void Spread(List<ShopLine> lines, decimal off, Func<ShopLine, decimal> worth)
        {
            decimal b = lines.Sum(worth), left = off; if (b <= 0) return;
            for (int i = 0; i < lines.Count; i++)
            {
                var x = i == lines.Count - 1 ? left : Math.Round(off * worth(lines[i]) / b, 2);
                x = Math.Min(x, left); lines[i].Disc += x; left -= x;
            }
        }

        // ---------- the sum for the browser ----------
        public static Dictionary<string, object> Json(ShopQuote q)
        {
            return new Dictionary<string, object>
            {
                { "l", q.Lines.Select(l => new Dictionary<string, object> { { "v", l.VariantId }, { "x", l.Extra ?? "" }, { "q", l.Qty }, { "u", l.Unit }, { "old", l.Old ?? 0 }, { "d", l.Disc }, { "t", l.Net }, { "e", l.Err ?? "" }, { "mx", l.Max },
                    { "p", l.P == null ? 0 : l.P.Id }, { "o", l.V == null ? "" : l.V.Label }, { "st", l.V == null ? 0 : Math.Max(0, l.V.Stock) } }).ToArray() },
                { "n", q.Items }, { "sub", q.Sub }, { "off", q.Discount }, { "coff", q.CouponOff }, { "ship", q.Ship }, { "shipFull", q.ShipFull }, { "free", q.FreeShip }, { "left", q.FreeLeft }, { "over", q.FreeOver }, { "total", q.Total }, { "days", q.Days ?? "" },
                { "min", q.Min }, { "stop", q.Stop ?? "" }, { "deliver", q.Deliver }, { "cpn", q.Coupon ?? "" }, { "cpnErr", q.CouponErr ?? "" },
                { "of", q.Offers.Select(o => new Dictionary<string, object> { { "i", o.Id }, { "n", ShopView.Pair(o.Name, o.NameEn) }, { "a", o.Amount } }).ToArray() }
            };
        }

        // «حماية من الخسارة»: products whose price with this offer is under what they cost the owner
        public static List<string> Losses(ShopData d, ShopOffer offer, DateTime now, int max = 8)
        {
            var res = new List<string>();
            foreach (var p in d.Products.Where(x => x.Status == "active" && offer.Covers(x, d)))
                foreach (var v in p.Vars.Where(x => x.Active))
                {
                    var cost = p.CostOf(v); if (!cost.HasValue || cost.Value <= 0) continue;
                    decimal list = p.PriceOf(v), low = list, val = ShopJson.Dec(ShopJson.At(offer.Rule, "value"));
                    if (offer.Type == "auto") low = ShopJson.Str(ShopJson.At(offer.Rule, "kind")) == "amt" ? list - val : list * (1 - val / 100m);
                    else if (offer.Type == "bxgy") { int b = Math.Max(1, ShopJson.Int(ShopJson.At(offer.Rule, "buy"), 2)), g = Math.Max(1, ShopJson.Int(ShopJson.At(offer.Rule, "get"), 1)); low = list * (b + g * (1 - ShopJson.Dec(ShopJson.At(offer.Rule, "pct"), 100) / 100m)) / (b + g); }
                    else if (offer.Type == "qty") low = list * (1 - ShopJson.List(ShopJson.At(offer.Rule, "tiers")).OfType<Dictionary<string, object>>().Select(x => ShopJson.Dec(ShopJson.At(x, "pct"))).DefaultIfEmpty(0).Max() / 100m);
                    if (low >= cost.Value) continue;
                    res.Add(p.Name + (v.Label.Length > 0 ? " (" + v.Label + ")" : "") + ": " + Money(Round(low)) + " وسعر الشرا " + Money(cost.Value));
                    if (res.Count >= max) return res;
                    break;
                }
            return res;
        }
    }
}
