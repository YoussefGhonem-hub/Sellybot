// An order from the chat on a rented store (SellyStore), on the site, WhatsApp, Messenger, Instagram and Telegram. One tool in two steps:
// the first call answers with the sum from the database (the same sum as the store's checkout) and a token that stands for exactly those
// details; the second, with the token, makes the order. The token can't be used in the reply that made it, so the customer always sees the
// summary and agrees in a message of their own; any change in what was agreed on makes a new token. The order itself goes through
// ShopOrders.Place: the same price, stock, limits, marked numbers and code as an order from the store's page.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public static class ShopChatOrder
    {
        public const string ToolName = "store_make_order";
        public static bool On(SqlConnection c, StoreInfo s, Shop sh) { return s != null && sh != null && ShopJson.Bool(ShopJson.At(ShopJson.Sub(sh.Settings, "order"), "chat"), true) && PlanGate.Allows(c, s, ChatOrders.Feature) && !Billing.Expired(s) && Shops.Open(c, s, sh); }

        static JsonElement J(object o) { return JsonSerializer.SerializeToElement(o); }
        // own: the customer's number when the channel proves it (WhatsApp). ip: who is asking, for the limits. sure: false in a preview.
        public static ChatHandler.ExtraTool Tool(int storeId, string own, string ip, bool preview)
        {
            var mine = new HashSet<string>();   // the tokens of this reply
            return new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = ToolName,
                    Description = "يسجّل طلب الزبون في المتجر على مرحلتين. أول مرة من غير confirm_token: بيحسب الإجمالي بالشحن ويرجّع الملخص وكود داخلي. تاني مرة بعد ما الزبون يوافق على الملخص: نفس البيانات ومعاها confirm_token فالطلب بيتسجّل. لو ناقص بيانات بيقولك إيه.",
                    InputSchema = new InputSchema
                    {
                        Properties = new Dictionary<string, JsonElement>
                        {
                            ["items"] = J(new { type = "array", description = "المنتجات المطلوبة.", items = new { type = "object", properties = new { id = new { type = "integer", description = "رقم المنتج من أداة store_products." }, options = new { type = "string", description = "المقاس واللون اللي الزبون اختارهم زي ما هم مكتوبين في المنتج، مثلًا: أسود، M. فاضي لو المنتج مالوش اختيارات." }, qty = new { type = "integer" } }, required = new[] { "id", "qty" } } }),
                            ["name"] = J(new { type = "string", description = "اسم الزبون." }), ["phone"] = J(new { type = "string", description = "موبايل الزبون." }), ["governorate"] = J(new { type = "string", description = "المحافظة." }), ["city"] = J(new { type = "string", description = "المدينة أو الحي." }),
                            ["address"] = J(new { type = "string", description = "الشارع والعمارة والدور والشقة." }), ["note"] = J(new { type = "string" }), ["coupon"] = J(new { type = "string", description = "كود خصم لو الزبون قاله." }), ["confirm_token"] = J(new { type = "string", description = "الكود اللي رجع مع الملخص، بعد موافقة الزبون." }), ["code"] = J(new { type = "string", description = "كود الواتساب لو الأداة طلبته." }), ["delivery_day"] = J(new { type = "string", description = "يوم التوصيل yyyy-MM-dd لو المتجر بيخلّي الزبون يختاره، من الأيام اللي الأداة قالتها بس." })
                        }, Required = new List<string> { "items" }
                    }
                },
                Run = tu => Task.FromResult(Run(storeId, own, ip, preview, tu, mine)), Prompt = null
            };
        }
        public static string Prompt(string own)
        {
            return "- تقدر تسجّل الطلب من الشات بأداة " + ToolName + ": لما الزبون يقول إنه عايز يطلب، اتأكد من المنتج والمقاس واللون والكمية، وبعدين خد الاسم" + (own != null ? " (رقمه معروف من الواتساب، اسأله لو عايز رقم تاني)" : " ورقم الموبايل") + " والمحافظة والمدينة والعنوان. سؤال أو اتنين في الرسالة، وماتسألش على حاجة قالها. نادي الأداة من غير confirm_token، واعرض الملخص اللي رجع زي ما هو (المنتجات والشحن والإجمالي والعنوان) واسأله «أأكّد الطلب؟». لما يوافق في رسالته، نادي الأداة تاني بنفس البيانات ومعاها confirm_token. ماتقولش إن الطلب اتسجّل إلا لو الأداة رجّعت رقم الطلب. لو حب يكمّل من المتجر ابعتله لينك المنتج.\n";
        }

        static string S(ToolUseBlock tu, string k, int n = 300) { JsonElement v; if (tu.Input == null || !tu.Input.TryGetValue(k, out v)) return ""; var t = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : ""; t = (t ?? "").Trim(); return t.Length > n ? t.Substring(0, n) : t; }
        static string Sign(int storeId, string what) { using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(Web.Setting("AuthSecret") ?? "sellystore"))) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes("chat-order|" + storeId + "|" + what)).Take(10).Select(b => b.ToString("x2"))); }

        // the size and the colour the customer said -> the product's own; null = not clear (why says what to ask)
        static ShopVar Variant(ShopProd p, string said, out string why)
        {
            why = null; var vs = p.Vars.Where(v => v.Active).ToList();
            if (p.Options.Count == 0) return vs.FirstOrDefault();
            string t = " " + ShopGeo.Fold(said ?? "") + " "; var words = Regex.Split(t, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            var hit = vs.Where(v => v.Vals.Select((x, i) => new { x, i }).All(k =>
            {
                var o = k.i < p.Options.Count ? p.Options[k.i].Vals.FirstOrDefault(y => y.Ar == k.x) : null;
                return new[] { k.x, o == null ? "" : o.En }.Where(y => !string.IsNullOrEmpty(y)).Any(y => { var f = ShopGeo.Fold(y); return f.Length <= 4 ? words.Contains(f) : t.Contains(f); });
            })).ToList();
            if (hit.Count == 1) return hit[0];
            why = "«" + p.Name + "» ليه اختيارات، اسأل الزبون عن: " + string.Join("، ", p.Options.Select(o => o.Ar)) + ". المتاح: " + string.Join("، ", vs.Where(p.Can).Take(20).Select(v => v.Label)) + ".";
            return null;
        }

        static string Run(int storeId, string own, string ip, bool preview, ToolUseBlock tu, HashSet<string> mine)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId); var sh = ShopBot.For(c, s, preview); if (sh == null || (!preview && !On(c, s, sh))) return "الطلب من الشات مقفول دلوقتي. ابعت للزبون لينك المنتج يطلب منه.";
                    var d = ShopCache.Get(c, sh); var now = DateTime.UtcNow; var lines = new List<ShopLine>(); var bad = new List<string>(); JsonElement items;
                    if (tu.Input != null && tu.Input.TryGetValue("items", out items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var i in items.EnumerateArray().Take(20))
                        {
                            JsonElement x; int id = 0, q = 1; if (i.TryGetProperty("id", out x)) int.TryParse(x.ToString(), out id); if (i.TryGetProperty("qty", out x)) int.TryParse(x.ToString(), out q);
                            ShopProd p; if (!d.ById.TryGetValue(id, out p) || !p.Shown) { bad.Add("منتج رقم " + id + " مش موجود. دوّر عليه بأداة " + ShopBot.TProducts + "."); continue; }
                            if (ShopJson.Arr(p.AskJson).OfType<Dictionary<string, object>>().Any(a => ShopJson.Bool(ShopJson.At(a, "req")))) { bad.Add("«" + p.Name + "» محتاج الزبون يكتب بيانات عليه، فبيتطلب من صفحته: " + sh.Url + "/p/" + p.Slug); continue; }
                            string why; var v = Variant(p, i.TryGetProperty("options", out x) ? x.ToString() : "", out why); if (v == null) { bad.Add(why ?? "«" + p.Name + "» مش متاح."); continue; }
                            if (!p.Can(v)) { bad.Add("«" + p.Name + (v.Label.Length > 0 ? " (" + v.Label + ")" : "") + "» خلص. المتاح: " + string.Join("، ", p.Vars.Where(p.Can).Take(12).Select(y => y.Label)) + "."); continue; }
                            lines.Add(new ShopLine { VariantId = v.Id, Qty = Math.Max(1, Math.Min(100, q)) });
                        }
                    if (bad.Count > 0) return string.Join("\n", bad);
                    if (lines.Count == 0) return "مفيش منتجات في الطلب.";

                    string name = S(tu, "name", 80), phoneRaw = S(tu, "phone", 24), city2; var phone = phoneRaw.Length > 0 ? ShopOrders.Mobile(phoneRaw) : own != null ? ShopOrders.Mobile(own) : null; var g = ShopGeo.Find(S(tu, "governorate", 60) + " " + S(tu, "city", 60), out city2); string city = S(tu, "city", 60).Length > 1 ? S(tu, "city", 60) : city2 ?? "", address = S(tu, "address", 300), coupon = S(tu, "coupon", 30), note = S(tu, "note", 300);
                    var miss = new List<string>(); if (name.Length < 2) miss.Add("الاسم"); if (phone == null) miss.Add(phoneRaw.Length > 0 ? "رقم موبايل مصري صح (11 رقم)" : "رقم الموبايل"); if (g == null) miss.Add("المحافظة"); if (city.Length < 2) miss.Add("المدينة أو الحي"); if (address.Length < 5) miss.Add("العنوان بالتفصيل");
                    var how = new ShopHow { Pay = "cod" }; var q0 = ShopPrice.Quote(c, d, lines.Select(l => new ShopLine { VariantId = l.VariantId, Qty = l.Qty }).ToList(), coupon, g == null ? 0 : g.Id, phone, now, true, how, city);
                    if (q0.Stop == "vacation") return "المتجر واخد إجازة ومش بيستقبل طلبات دلوقتي.";
                    if (q0.Stop == "nodelivery") return "للأسف مابنوصّلش لمحافظة " + g.Ar + " دلوقتي.";
                    if (q0.Stop == "min") return "أقل قيمة للطلب " + ShopOrders.Money(q0.Min) + "، واللي في الطلب " + ShopOrders.Money(q0.Sub) + ".";
                    var er = q0.Lines.Where(l => l.Err != null).Select(l => l.P == null ? "منتج مش متاح" : "«" + l.P.Name + "»: " + (l.Err == "stock" || l.Err == "out" ? "المتاح " + Math.Max(0, l.Max) : l.Err == "max" ? "أقصى كمية " + l.Max : l.Err == "min" ? "أقل كمية " + l.P.MinQty : "مش متاح")).ToList(); if (er.Count > 0) return string.Join("، ", er) + ".";
                    if (coupon.Length > 0 && !q0.CouponUsed) return "الكود: " + (q0.CouponErr ?? "مش شغال.");
                    if (miss.Count > 0) return "ناقص من الزبون: " + string.Join("، ", miss) + ". المنتجات بـ " + ShopOrders.Money(q0.Sub - q0.Discount) + (g != null ? "، والشحن لـ" + g.Ar + " " + (q0.Ship > 0 ? ShopOrders.Money(q0.Ship) : "مجاني") : "") + ".";

                    var dday = ShopShipDay.ChatCheck(c, sh, g.Id, S(tu, "delivery_day", 12), false); if (dday != null) return dday;   // «ميعاد التوصيل»
                    string what = string.Join(";", q0.Lines.Select(l => l.VariantId + "x" + l.Qty)) + "|" + name + "|" + phone + "|" + g.Id + "|" + city + "|" + address + "|" + q0.Coupon + "|" + q0.Total.ToString(CultureInfo.InvariantCulture) + "|" + S(tu, "delivery_day", 12), given = S(tu, "confirm_token", 60).Trim();
                    // of today (Cairo); a summary from just before midnight still counts. The same order on another day is another order.
                    var day = now.AddHours(2).Date; string token = Sign(storeId, what + "|" + day.ToString("yyyyMMdd")), late = now.AddHours(2).Hour < 3 ? Sign(storeId, what + "|" + day.AddDays(-1).ToString("yyyyMMdd")) : null; if (late != null && given == late) token = late;
                    if (given != token || mine.Contains(token))
                    {
                        mine.Add(token);
                        var sb = new StringBuilder(given.Length > 0 && given != token ? "فيه حاجة اتغيّرت عن الملخص اللي فات، فاعرض ده واسأله تاني.\n" : "").Append("اعرض الملخص ده على الزبون واستنى موافقته في رسالة جديدة قبل ما تسجّل:\n");
                        foreach (var l in q0.Lines) sb.Append("• ").Append(l.P.Name).Append(l.V.Label.Length > 0 ? " (" + l.V.Label + ")" : "").Append(" × ").Append(l.Qty).Append(l.Gift > 0 ? " هدية" : " = " + ShopOrders.Money(l.Sum)).Append("\n");
                        if (q0.Discount > 0) sb.Append("الخصم: ").Append(ShopOrders.Money(q0.Discount)).Append("\n"); sb.Append("الشحن لـ").Append(g.Ar).Append(": ").Append(q0.Ship > 0 ? ShopOrders.Money(q0.Ship) : "مجاني").Append(string.IsNullOrEmpty(q0.Days) ? "" : "، خلال " + q0.Days + " يوم").Append("\n"); foreach (var f in q0.Fees) sb.Append(f.Name).Append(": ").Append(ShopOrders.Money(f.Amount)).Append("\n");
                        sb.Append("الإجمالي: ").Append(ShopOrders.Money(q0.Total)).Append("، ").Append(ShopPay.WayName(q0.Pay)).Append("\nالاسم: ").Append(name).Append("، الموبايل: ").Append(ShopOrders.Local(phone)).Append("\nالعنوان: ").Append(g.Ar).Append("، ").Append(city).Append("، ").Append(address); if (S(tu, "delivery_day", 12).Length == 10) sb.Append("\nيوم التوصيل: ").Append(ShopShipDay.Text(S(tu, "delivery_day", 12), false)); if (q0.Deposit > 0) sb.Append("\nالمتجر بيطلب تحويل ").Append(ShopOrders.Money(q0.Deposit)).Append(" بعد التأكيد، وهنبعتله بياناته.");
                        return sb.Append("\nconfirm_token: ").Append(token).Append(" (داخلي، ماتكتبهوش للزبون)").ToString();
                    }
                    var o = new ShopOrderIn { Lines = lines, Name = name, Phone = phone, Gov = g.Id, City = city, Address = address, Note = note, Coupon = q0.CouponUsed ? q0.Coupon : "", ClientKey = "chat-" + token, Lang = "ar", Ip = (ip ?? "").Length > 60 ? "" : ip, Src = "chat", Channel = own != null ? "wa" : ip != null && (ip.StartsWith("fb:") || ip.StartsWith("msgr")) ? "msgr" : ip != null && ip.StartsWith("ig:") ? "ig" : ip != null && ip.StartsWith("tg") ? "tg" : "chat", Expected = q0.Total, Agreed = true, Test = preview, How = how, Otp = S(tu, "code", 12), Day = S(tu, "delivery_day", 12) };
                    ShopOrders.Placed placed;
                    // a number WhatsApp itself proves isn't asked for a code
                    if (own != null && ShopOrders.Mobile(own) == phone) ShopAccount.Trust(c, storeId, phone);
                    try { placed = ShopOrders.Place(c, s, sh, o); }
                    catch (ShopException ex) when (ex.Code == "otp")
                    {
                        if (S(tu, "code", 12).Length > 0) return ex.Message + " اطلبه تاني من الزبون.";
                        var sent = ShopAccount.ChatCode(c, s, sh, phone, ip); return sent ?? "اتبعت كود على واتساب " + ShopOrders.Local(phone) + ". اطلبه من الزبون، ونادي الأداة تاني بنفس البيانات ومعاها code و confirm_token.";
                    }
                    if (preview) return "تجربة تمام: الطلب كان هيتسجّل بإجمالي " + ShopOrders.Money(placed.Order.Total) + ". المعاينة مابتسجّلش طلبات.";
                    bool proved = own != null && ShopOrders.Mobile(own) == phone;
                    if (placed.Again) return "الطلب ده نفسه متسجّل قبل كده النهارده برقم " + placed.Order.NoText + "، وماتسجّلش مرة تانية. قول للزبون كده. لو عايز كمية زيادة عدّل الكمية واعرض ملخص جديد. لينك المتابعة: " + sh.Url + "/thanks/" + placed.Order.Token;
                    if (!placed.Again) { if (proved && placed.Order.Status == "new" && placed.Order.Deposit <= 0) ShopOrders.SetStatus(c, storeId, placed.Order.Id, "confirmed", "bot", "أكّده في شات الواتساب"); ShopHooks.OnPlaced(s, placed.Order.Id, proved); }
                    return "الطلب اتسجّل: رقمه " + placed.Order.NoText + "، وإجماليه " + ShopOrders.Money(placed.Order.Total) + ". لينك المتابعة: " + sh.Url + "/thanks/" + placed.Order.Token + (placed.Order.Deposit > 0 ? "\nبيانات التحويل وإرسال صورته: " + sh.Url + "/pay/" + placed.Order.Token : "") + "\nقول للزبون الرقم واللينك، واشكره.";
                }
            }
            catch (ShopException ex) { return ex.Message + " قول للزبون كده بأسلوبك، ولو ينفع يتصلّح كمّل معاه."; }
            catch (Exception ex) { Log.Error(ex); return "حصلت مشكلة ومقدرناش نسجّل الطلب. اعتذر للزبون وابعتله لينك المنتج يطلب منه."; }
        }
    }
}
