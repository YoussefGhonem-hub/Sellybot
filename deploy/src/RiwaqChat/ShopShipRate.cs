// Shipping by weight or city: the governorate's fee (ShopPrice.ShipFee) stays the base. The owner may give some cities of a governorate
// another fee, and say the fee includes up to X kg and each kilo more costs Y (one rule for all, or per governorate). A piece weighs its
// size's weight, else its product's, else the store's default; nothing known = no extra. «شحن مجاني فوق» still makes all of it free.
// Every sum passes ShopPrice.Quote, which calls Fee with the governorate, the city and the lines; the shipping companies get the real weight.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web;

namespace RiwaqChat
{
    public static class ShopShipRate
    {
        static Dictionary<string, object> Set(ShopData d) { return ShopShip2.Of(d.Row, "srate"); }
        public static bool On(ShopData d) { return d != null && ShopShip2.On(d.Row, "srate"); }

        public static Dictionary<string, object> Clean(Dictionary<string, object> v)
        {
            var cities = new List<object>(); var seen = new HashSet<string>();
            foreach (var x in ShopJson.List(ShopJson.At(v, "cities")).OfType<Dictionary<string, object>>())
            {
                int g = ShopJson.Int(ShopJson.At(x, "g")); string c = ShopJson.Str(ShopJson.At(x, "c"), 60).Trim(); var gov = ShopGeo.Get(g);
                if (gov == null || c.Length < 2 || !seen.Add(g + "|" + ShopGeo.Fold(c)) || cities.Count >= 300) continue;
                cities.Add(ShopShip2.D("g", g, "c", c, "f", ShopShip2.Clamp(ShopJson.Dec(ShopJson.At(x, "f")), 0, 5000)));
            }
            var zones = new List<object>(); var gs = new HashSet<int>();
            foreach (var x in ShopJson.List(ShopJson.At(v, "zones")).OfType<Dictionary<string, object>>())
            {
                int g = ShopJson.Int(ShopJson.At(x, "g")); if (ShopGeo.Get(g) == null || !gs.Add(g)) continue;
                zones.Add(ShopShip2.D("g", g, "inc", Kg(ShopJson.At(x, "inc")), "per", ShopShip2.Clamp(ShopJson.Dec(ShopJson.At(x, "per")), 0, 1000)));
            }
            return ShopShip2.D("on", ShopJson.Bool(ShopJson.At(v, "on")), "w", Kg(ShopJson.At(v, "w")), "cities", cities, "inc", Kg(ShopJson.At(v, "inc")), "per", ShopShip2.Clamp(ShopJson.Dec(ShopJson.At(v, "per")), 0, 1000), "zones", zones);
        }
        static decimal Kg(object v) { decimal n; return decimal.TryParse(ShopJson.Digits(ShopJson.Str(v)), NumberStyles.Number, CultureInfo.InvariantCulture, out n) ? Math.Round(ShopShip2.Clamp(n, 0, 100), 3) : 0; }
        static decimal KgOf(Dictionary<string, object> s, string k) { return Kg(ShopJson.At(s, k)); }

        // ---------- the fee ----------
        static decimal? CityFee(Dictionary<string, object> s, int gov, string city)
        {
            var f = ShopGeo.Fold(city); if (gov <= 0 || f.Length < 2) return null;
            var x = ShopJson.List(ShopJson.At(s, "cities")).OfType<Dictionary<string, object>>().FirstOrDefault(y => ShopJson.Int(ShopJson.At(y, "g")) == gov && ShopGeo.Fold(ShopJson.Str(ShopJson.At(y, "c"), 60)) == f);
            return x == null ? (decimal?)null : ShopJson.Dec(ShopJson.At(x, "f"));
        }
        // the weight rule for a governorate: its own, else the one for all; inc = kilos the fee includes, per = each kilo more
        static void Rule(Dictionary<string, object> s, int gov, out decimal inc, out decimal per)
        {
            var z = ShopJson.List(ShopJson.At(s, "zones")).OfType<Dictionary<string, object>>().FirstOrDefault(y => ShopJson.Int(ShopJson.At(y, "g")) == gov);
            var r = z ?? s; inc = KgOf(r, "inc"); per = ShopJson.Dec(ShopJson.At(r, "per"));
        }
        public static decimal Fee(SqlConnection c, ShopData d, int gov, string city, IEnumerable<ShopLine> lines, decimal fee)
        {
            if (!On(d) || gov <= 0) return fee;
            try
            {
                var s = Set(d); var cf = CityFee(s, gov, city); if (cf.HasValue) fee = cf.Value;
                decimal inc, per; Rule(s, gov, out inc, out per); if (per <= 0 || lines == null) return fee;
                var kg = Kg(c, d, lines); if (kg > inc) fee += Math.Ceiling(kg - inc) * per;
                return fee;
            }
            catch (Exception ex) { Log.Error(ex); return fee; }
        }
        // the customer's new address moves the fee (ShopAccount, ShopOrders.Edit): another city with its own fee
        public static bool CityMoves(ShopData d, int gov, string from, string to)
        {
            if (!On(d)) return false; var s = Set(d); return CityFee(s, gov, from) != CityFee(s, gov, to);
        }
        // the lines changed and the weight counts
        public static bool Weighs(ShopData d, int gov) { if (!On(d)) return false; decimal inc, per; Rule(Set(d), gov, out inc, out per); return per > 0; }

        // ---------- weights ----------
        static readonly ConditionalWeakTable<ShopData, Dictionary<int, int>> Vars = new ConditionalWeakTable<ShopData, Dictionary<int, int>>();
        static Dictionary<int, int> VarGrams(SqlConnection c, ShopData d)
        {
            return Vars.GetValue(d, k =>
            {
                var m = new Dictionary<int, int>();
                using (var cmd = Db.Cmd(c, "SELECT VariantId, Grams FROM dbo.ShopWeights WHERE StoreId=@s", "@s", d.Row.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) m[(int)r[0]] = (int)r[1];
                return m;
            });
        }
        // one piece in grams: its size's, its product's, the store's default
        static int Grams(SqlConnection c, ShopData d, int productId, int variantId)
        {
            int g; if (VarGrams(c, d).TryGetValue(variantId, out g) && g > 0) return g;
            ShopProd p; if (d.ById.TryGetValue(productId, out p) && p.Weight.HasValue && p.Weight.Value > 0) return p.Weight.Value;
            return (int)(KgOf(Set(d), "w") * 1000);
        }
        public static decimal Kg(SqlConnection c, ShopData d, IEnumerable<ShopLine> lines)
        {
            long g = 0; foreach (var l in lines.Where(x => x.P != null && x.Err != "gone" && x.Err != "out")) g += (long)Grams(c, d, l.P.Id, l.VariantId) * l.Qty;
            return Math.Round(g / 1000m, 3);
        }
        // what an order weighs, for the shipping company (0 = not known: the company's own setting stays)
        public static decimal OrderKg(SqlConnection c, ShopData d, ShopOrder o)
        {
            if (!On(d)) return 0; long g = 0; foreach (var i in o.Items) g += (long)Grams(c, d, i.ProductId, i.VariantId) * i.Qty;
            return Math.Round(g / 1000m, 3);
        }

        // ---------- the storefront and the assistant ----------
        public static object Boot(ShopData d) { if (!On(d)) return null; var s = Set(d); decimal inc, per; Rule(s, 0, out inc, out per); return ShopShip2.D("city", ShopJson.List(ShopJson.At(s, "cities")).Count > 0, "kg", per > 0 || ShopJson.List(ShopJson.At(s, "zones")).Count > 0); }
        public static string BotText(ShopData d, int gov)
        {
            if (!On(d)) return ""; var s = Set(d); var sb = new StringBuilder();
            var cities = ShopJson.List(ShopJson.At(s, "cities")).OfType<Dictionary<string, object>>().Where(x => gov <= 0 || ShopJson.Int(ShopJson.At(x, "g")) == gov).Take(20).ToList();
            if (cities.Count > 0) sb.Append(" مدن ليها سعر شحن لوحدها: ").Append(string.Join("، ", cities.Select(x => ShopJson.Str(ShopJson.At(x, "c"), 60) + " (" + ShopGeo.Name(ShopJson.Int(ShopJson.At(x, "g"))) + ") " + ShopOrders.Money(ShopJson.Dec(ShopJson.At(x, "f")))))).Append(".");
            decimal inc, per; Rule(s, gov, out inc, out per);
            if (per > 0) sb.Append(" سعر الشحن شامل لحد ").Append(inc.ToString("0.###", CultureInfo.InvariantCulture)).Append(" كيلو، وكل كيلو زيادة بـ ").Append(ShopOrders.Money(per)).Append(". الإجمالي المظبوط بيظهر في السلة بعد المدينة.");
            return sb.ToString();
        }

        // ---------- the owner: weights of the products and their sizes ----------
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (a != "srate-w") return false; var d = ShopCache.Get(c, sh); var vg = VarGrams(c, d);
            Web.Json(res, 200, ShopShip2.D("ok", true, "list", d.ById.Values.Where(p => p.Status != "deleted").OrderBy(p => p.Sort).ThenByDescending(p => p.Id).Take(600).Select(p => ShopShip2.D("p", p.Id, "n", p.Name, "w", p.Weight.HasValue ? p.Weight.Value / 1000m : 0,
                "vars", p.Options.Count == 0 ? new object[0] : p.Vars.Select(v => { int g; return (object)ShopShip2.D("v", v.Id, "l", v.Label, "w", vg.TryGetValue(v.Id, out g) ? g / 1000m : 0); }).ToArray())).ToArray()));
            return true;
        }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res)
        {
            if (a != "srate-w") return false; var d = ShopCache.Get(c, sh); int n = 0;
            foreach (var x in ShopJson.List(ShopJson.At(b, "items")).OfType<Dictionary<string, object>>().Take(2000))
            {
                int p = ShopJson.Int(ShopJson.At(x, "p")), v = ShopJson.Int(ShopJson.At(x, "v")), g = (int)Math.Round(Kg(ShopJson.At(x, "w")) * 1000);
                if (v > 0) { ShopVar sv; if (!d.Vars.TryGetValue(v, out sv)) continue; if (g > 0) ShopShip2Exec(c, "UPDATE dbo.ShopWeights SET Grams=@g WHERE VariantId=@v AND StoreId=@s; IF @@ROWCOUNT=0 INSERT INTO dbo.ShopWeights(VariantId, StoreId, Grams) VALUES(@v, @s, @g)", "@g", g, "@v", v, "@s", sh.StoreId); else ShopShip2Exec(c, "DELETE FROM dbo.ShopWeights WHERE VariantId=@v AND StoreId=@s", "@v", v, "@s", sh.StoreId); n++; }
                else if (p > 0 && d.ById.ContainsKey(p)) { ShopShip2Exec(c, "UPDATE dbo.ShopProducts SET Weight=@g WHERE Id=@p AND StoreId=@s", "@g", g > 0 ? (object)g : null, "@p", p, "@s", sh.StoreId); n++; }
            }
            if (n > 0) Shops.Bump(c, sh.StoreId);
            ShopShip2.Ok(res, "saved", n); return true;
        }
        static void ShopShip2Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
    }
}
