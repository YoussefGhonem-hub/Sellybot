// «بوصّل بنفسي»: the owner, or people of theirs, deliver the orders. In «الشحن» the owner writes who delivers (a name and a mobile, ten at
// most) and a line the customer sees («التوصيل من 2 الضهر لـ 10 بالليل»): Settings.ship2.self {on, who: [{n, p}], line: [ar, en]}, cleaned
// here (ShopShip2.Clean). On an order the owner picks who delivers it (ShopOrders.Extras.by, a copy: changing the list later never changes an
// order that left); an order that leaves (shipped / out) without a pick gets the first one. The customer sees it then, and only then: the
// tracking page and the order page, the «اتشحن» and «خرج للتوصيل» WhatsApp messages, the assistant's answer about the order.
// Never for an order with a shipping company or a pickup from the shop. Off, or nobody written: nothing changes anywhere.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public static class ShopSelf
    {
        static string Txt(object v, int max) { return Regex.Replace(ShopJson.Str(v, max), @"[\u0000-\u001F]", " ").Trim(); }
        static Dictionary<string, object> Set(Shop sh) { return ShopShip2.Of(sh, "self"); }
        public static List<Dictionary<string, object>> Who(Shop sh) { return ShopJson.List(ShopJson.At(Set(sh), "who")).OfType<Dictionary<string, object>>().Where(w => ShopJson.Str(ShopJson.At(w, "n")).Length > 0).ToList(); }
        public static bool On(Shop sh) { return sh != null && ShopJson.Bool(ShopJson.At(Set(sh), "on")) && Who(sh).Count > 0; }

        // ShopShip2.Clean: as the owner sent it (a name of 2 letters or more and an Egyptian mobile, or the row is dropped)
        public static Dictionary<string, object> Clean(Dictionary<string, object> x)
        {
            var who = ShopJson.List(ShopJson.At(x, "who")).OfType<Dictionary<string, object>>().Select(w => ShopShip2.D("n", Txt(ShopJson.At(w, "n"), 40), "p", ShopOrders.Local(ShopOrders.Mobile(ShopJson.Str(ShopJson.At(w, "p"), 24))) ?? ""))
                .Where(w => ((string)w["n"]).Length >= 2 && ((string)w["p"]).Length > 0).Take(10).ToArray();
            var l = ShopJson.List(ShopJson.At(x, "line"));
            return ShopShip2.D("on", ShopJson.Bool(ShopJson.At(x, "on")), "who", who, "line", new object[] { Txt(l.Count > 0 ? l[0] : null, 120), Txt(l.Count > 1 ? l[1] : null, 120) });
        }

        // the order is carried by us: no company's number on it, not a pickup from the shop
        static bool Ours(ShopOrder o) { return !o.Extras.ContainsKey("branch") && string.IsNullOrEmpty(o.Tracking) && string.IsNullOrEmpty(o.Carrier); }
        static Dictionary<string, object> Copy(Shop sh, Dictionary<string, object> w) { var l = ShopJson.List(ShopJson.At(Set(sh), "line")); return ShopShip2.D("n", ShopJson.Str(ShopJson.At(w, "n"), 40), "p", ShopJson.Str(ShopJson.At(w, "p"), 20), "t", new object[] { ShopJson.Str(l.Count > 0 ? l[0] : null, 120), ShopJson.Str(l.Count > 1 ? l[1] : null, 120) }); }
        // written over what the order's extras are now (the read is checked again, so nothing another writer put there is lost)
        static bool Write(SqlConnection c, ShopOrder o, Dictionary<string, object> by)
        {
            for (int i = 0; i < 3; i++)
            {
                var ex = new Dictionary<string, object>(o.Extras); ex["by"] = by;
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Extras=@e WHERE Id=@i AND StoreId=@s AND ISNULL(Extras,'')=@old", "@e", ShopJson.Write(ex), "@i", o.Id, "@s", o.StoreId, "@old", o.ExtrasJson ?? ""))
                    if (cmd.ExecuteNonQuery() == 1) { o.Extras["by"] = by; return true; }
                var fresh = ShopOrders.ById(c, o.StoreId, o.Id); if (fresh == null) return false; o.ExtrasJson = fresh.ExtrasJson; o.Extras.Clear(); foreach (var kv in fresh.Extras) o.Extras[kv.Key] = kv.Value;
            }
            return false;
        }

        // ShopHooks (ShopBot.Changed): the order left; nobody picked: the first one
        public static void Stamp(SqlConnection c, int storeId, ShopOrder o)
        {
            try { var sh = Shops.ByStore(c, storeId); if (!On(sh) || !Ours(o) || o.Extras.ContainsKey("by")) return; if (Write(c, o, Copy(sh, Who(sh)[0]))) ShopOrders.Log_(c, null, storeId, o.Id, "system", Carriers.Cut("هيوصّله " + ShopJson.Str(ShopJson.At(ShopJson.Sub(o.Extras, "by"), "n")), 290)); }
            catch (Exception ex) { Log.Error(ex); }
        }

        // the owner's pick {id, i}
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res)
        {
            if (a != "self-pick") return false;
            var o = ShopOrders.ById(c, sh.StoreId, ShopShip2.Int(b, "id")); if (o == null) throw new ShopException("gone", "الأوردر ده مش موجود.");
            if (!On(sh)) throw new ShopException("off", "شغّل «بوصّل بنفسي» واكتب مين بيوصّل من «الشحن» الأول.");
            var who = Who(sh); int i = ShopShip2.Int(b, "i", -1); if (i < 0 || i >= who.Count) throw new ShopException("who", "اختار من القايمة.");
            if (!Ours(o)) throw new ShopException("carrier", o.Extras.ContainsKey("branch") ? "الأوردر ده استلام من المحل." : "الأوردر ده مع شركة شحن.");
            if (o.Status == "delivered" || o.Status == "cancelled" || o.Status == "returned") throw new ShopException("state", "الأوردر ده «" + ShopOrders.StatusText(o.Status) + "» خلاص.");
            var by = Copy(sh, who[i]); if (!Write(c, o, by)) throw new ShopException("busy", "الأوردر اتغيّر حالًا. جرّب تاني.");
            ShopOrders.Log_(c, null, sh.StoreId, o.Id, "owner", Carriers.Cut("هيوصّله " + (string)by["n"], 290)); ShopShip2.Ok(res, "by", by); return true;
        }

        // what the customer is told, once the order left (the WhatsApp messages; an empty string otherwise)
        public static string Line(ShopOrder o, bool en)
        {
            if (o == null || (o.Status != "shipped" && o.Status != "out")) return ""; var by = ShopJson.Sub(o.Extras, "by"); string n = ShopJson.Str(ShopJson.At(by, "n"), 40), p = ShopJson.Str(ShopJson.At(by, "p"), 20); if (n.Length == 0) return "";
            var t = ShopJson.List(ShopJson.At(by, "t")); string ar = ShopJson.Str(t.Count > 0 ? t[0] : null, 120), eng = ShopJson.Str(t.Count > 1 ? t[1] : null, 120), line = en && eng.Length > 0 ? eng : ar;
            return (en ? "🛵 " + n + " will deliver it" + (p.Length > 0 ? ", mobile " + p : "") : "🛵 هيوصلك " + n + (p.Length > 0 ? "، رقمه " + p : "")) + (line.Length > 0 ? "\n🕒 " + line : "");
        }
        // the assistant: about one order (ShopBot.Text), and what the store says in general
        public static string BotLine(ShopOrder o) { var t = Line(o, false); return t.Length == 0 ? "" : "اللي هيوصّل الطلب (المتجر بيوصّل بنفسه): " + t.Replace("🛵 هيوصلك ", "").Replace("\n🕒 ", ". ") + "\n"; }
        public static string BotText(Shop sh)
        {
            if (!On(sh)) return ""; var l = ShopJson.List(ShopJson.At(Set(sh), "line")); var line = ShopJson.Str(l.Count > 0 ? l[0] : null, 120);
            return "المتجر بيوصّل الأوردرات بنفسه، مش مع شركة شحن." + (line.Length > 0 ? " " + line + "." : "") + " لما الطلب يخرج، الزبون بيوصله اسم ورقم اللي هيوصّله. ماتدّيش أرقام حد من غير طلب الزبون نفسه. ";
        }
    }
}
