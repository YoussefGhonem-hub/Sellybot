// The store and the rest of Sellybot. The assistant reads the store's own rows at the moment of the question (price, sizes, quantity,
// shipping, offers, the customer's order), so there is nothing to update: a small fixed summary in the prompt, and three tools. The same
// on the store's page, WhatsApp, Messenger, Instagram and Telegram. Each tool is bound to one store: the store's id is never the model's.
// Then what happens after an order: the confirmation on WhatsApp («1» / «2», the follow-up in Followup.cs), the owner's alert, «زبايني»,
// the delivery-day message; and what the admin is told.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public static class ShopBot
    {
        public const string TProducts = "store_products", TInfo = "store_info", TOrder = "store_order";

        // the store this assistant may sell from: open to customers (or the owner's own preview and tests)
        public static Shop For(SqlConnection c, StoreInfo s, bool preview = false)
        {
            if (s == null || !Shops.Ready) return null;
            var sh = Shops.ByStore(c, s.Id);
            if (sh == null) return null;
            return preview || Shops.Open(c, s, sh) ? sh : null;
        }

        // ---------- the prompt's part: short, the same for every question ----------
        // the questions offered in the store's own chat when the owner wrote none: its first categories, then what people ask most
        public static Dictionary<string, object> Look(HttpContext ctx, Dictionary<string, object> json)
        {
            object o; var mine = json.TryGetValue("suggestions", out o) ? o as string[] : null; if (mine != null && mine.Length > 0) return json;
            return new Dictionary<string, object>(json) { ["suggestions"] = Suggest(ctx) };   // a copy: the kept one is shared with the same store's chat elsewhere
        }
        static string[] Suggest(HttpContext ctx)
        {
            var x = ctx.Items["shop"] as ShopFrontHandler.Ctx; if (x == null) return new string[0];
            try
            {
                ShopData d; using (var c = Db.Open()) d = x.Data ?? ShopCache.Get(c, x.Row); if (d == null) return new string[0];
                return d.Cats.Where(k => !k.ParentId.HasValue && d.CatShown(k)).Take(3).Select(k => "عندكم إيه في " + k.Name + "؟").Concat(new[] { "فيه عروض دلوقتي؟", "طلبي فين؟" }).Take(5).ToArray();
            }
            catch (Exception ex) { Log.Error(ex); return new string[0]; }
        }
        public static string Prompt(SqlConnection c, StoreInfo s, Shop sh, ShopData d, string phone)
        {
            var now = DateTime.UtcNow; var set = sh.Settings; var sb = new StringBuilder();
            sb.Append("\n\nالمتجر الإلكتروني: ").Append(sh.Name).Append("، ولينكه ").Append(sh.Url).Append("\n");
            var main = d.Cats.Where(k => !k.ParentId.HasValue && d.CatShown(k)).Take(25).ToList();
            if (main.Count > 0) sb.Append("الأقسام: ").Append(string.Join("، ", main.Select(k => k.Name + (k.Kids.Any(d.CatShown) ? " (" + string.Join("، ", k.Kids.Where(d.CatShown).Take(8).Select(x => x.Name)) + ")" : "")))).Append("\n");
            sb.Append("عدد المنتجات المعروضة: ").Append(d.Shown.Count()).Append("\n");
            var offers = d.Offers.Where(o => o.Running(now)).Take(8).ToList();
            if (offers.Count > 0) sb.Append("العروض الشغالة دلوقتي: ").Append(string.Join("، ", offers.Select(o => o.Name + (o.EndsAt.HasValue ? " (لحد " + ShopPromo.Local(o.EndsAt).Replace("T", " ") + ")" : "")))).Append("\n");
            var ways = ShopPrice.PayWays(sh).Select(w => ShopPay.WayName(ShopJson.Str(ShopJson.At(w, "k")))).ToList(); if (ShopPrice.Cod(sh)) ways.Insert(0, "كاش عند الاستلام"); var branches = ShopPrice.Branches(sh);
            sb.Append("الدفع: ").Append(string.Join("، ", ways)).Append(ways.Count == 1 ? " بس" : "").Append(". ").Append(branches.Count > 0 ? "الفروع: " + string.Join("، ", branches.Take(8).Select(b => ShopJson.Str(ShopJson.At(b, "n")) + " (" + ShopJson.Str(ShopJson.At(b, "ad")) + (ShopJson.Str(ShopJson.At(b, "hours")).Length > 0 ? "، " + ShopJson.Str(ShopJson.At(b, "hours")) : "") + (ShopJson.Bool(ShopJson.At(b, "pickup")) ? "، ينفع الاستلام منه" : "") + ")")) + ". " : "").Append("الاستبدال والاسترجاع: خلال ").Append(ShopJson.Int(ShopJson.At(set, "returnsDays"), 14)).Append(" يوم من الاستلام.\n");
            sb.Append(ShopCard.BotNote(sh, d));   // paying by card / installments, digital products
            if (ShopPrice.Vacation(d, now)) sb.Append("المتجر واخد إجازة ومش بيستقبل طلبات دلوقتي.\n");
            sb.Append(ShopRew.BotPrompt(sh));   // gift cards, points, «هات صاحبك» when on
            ShopSeasons.Bot(sh, sb);
            sb.Append("إزاي تستخدمه:\n")
              .Append("- السعر والمقاسات والألوان والكمية والعروض من أداة ").Append(TProducts).Append(" بس. استخدمها قبل ما ترد على أي سؤال عن منتج، حتى لو رديت عليه قبل كده، لأن السعر والكمية بيتغيّروا.\n")
              .Append("- سعر الشحن ومدة التوصيل والعروض والأكواد والسياسات والتواصل من أداة ").Append(TInfo).Append(".\n")
              .Append("- حالة الطلب من أداة ").Append(TOrder).Append(phone != null ? "، ورقم الزبون معروف من الواتساب." : "، ومحتاجة رقم الطلب ورقم الموبايل اللي طلب بيه.").Append(" ماتخمّنش حالة طلب أبدًا، وماتقولش بيانات حد تاني.\n")
              .Append("- لما ترشّح منتج ابعت لينكه زي ما الأداة رجّعته.").Append(ShopChatOrder.On(c, s, sh) ? "\n" + ShopChatOrder.Prompt(phone) : " والزبون يدوس عليه ويطلب. الطلب بيتعمل من صفحة المنتج في المتجر، مش من الشات: ماتاخدش اسم أو عنوان كأنك بتسجّل طلب.\n")
              .Append("- لو الزبون اختار منتج والأداة رجّعت «بيكمّله»، اعرض عليه منتج واحد منها مرة واحدة بس، من غير إلحاح.\n")
              .Append("- لو سأل على مقاسه: اقرا دليل المقاسات من الأداة برقم المنتج، واسأله عن المقاسات المكتوبة فيه (زي الطول والوزن)، ورشّح من الدليل بس. لو مفيش دليل قول إنك مش متأكد.\n")
              .Append("- لو المقاس أو اللون خلص قول إنه خلص واعرض المتاح. لو المنتج مش موجود قول إنه مش عندنا واعرض الأقرب من الأداة، وماتألّفش منتج أو سعر أو عرض.\n")
              .Append("- لو مالقيتش الإجابة قول إنك مش متأكد وانصحه يكلّم المتجر. الأدوات حاجة داخلية، ماتتكلمش عنها.");
            return sb.ToString();
        }

        // what the customer is looking at in the store (ids only come from the page; everything said about them is read here)
        public static string PageNote(ShopData d, Dictionary<string, object> page)
        {
            if (page == null) return "";
            var sb = new StringBuilder(); var now = DateTime.UtcNow; ShopProd p;
            if (d.ById.TryGetValue(ShopJson.Int(ShopJson.At(page, "p")), out p) && p.Shown) sb.Append("\n- الزبون فاتح دلوقتي صفحة منتج «").Append(p.Name).Append("» (رقمه في الأداة ").Append(p.Id).Append("). لو قال «ده» أو «دي» أو سأل عن السعر أو المقاس من غير ما يسمّي منتج، يبقى قصده المنتج ده.");
            ShopCat k; if (d.CatById.TryGetValue(ShopJson.Int(ShopJson.At(page, "c")), out k)) sb.Append("\n- الزبون فاتح قسم «").Append(k.Name).Append("».");
            var lines = ShopActions.Lines(page).Take(12).ToList();
            if (lines.Count > 0)
            {
                var items = new List<string>(); ShopVar v;
                foreach (var l in lines) if (d.Vars.TryGetValue(l.VariantId, out v)) { decimal? old; ShopOffer of; var pr = d.ById[v.ProductId]; items.Add(pr.Name + (v.Label.Length > 0 ? " (" + v.Label + ")" : "") + " × " + l.Qty + " بـ " + ShopOrders.Money(ShopPrice.Unit(d, pr, v, now, out old, out of) * l.Qty)); }
                if (items.Count > 0) sb.Append("\n- في سلة الزبون دلوقتي: ").Append(string.Join("، ", items)).Append(".");
            }
            return sb.ToString();
        }

        // ---------- the tools ----------
        static JsonElement J(object o) { return JsonSerializer.SerializeToElement(o); }

        // phone: the customer's own number when the channel proves it (WhatsApp), otherwise null
        public static List<ChatHandler.ExtraTool> Tools(int storeId, string phone, string ip, bool preview = false)
        {
            Func<Func<SqlConnection, StoreInfo, Shop, ShopData, string>, string> with = f =>
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId); var sh = For(c, s, preview);
                    if (sh == null) return "المتجر مقفول دلوقتي.";
                    var d = ShopCache.Get(c, sh);
                    return d == null ? "المتجر مقفول دلوقتي." : f(c, s, sh, d);
                }
            };
            var list = new List<ChatHandler.ExtraTool>();
            list.Add(new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = TProducts,
                    Description = "منتجات المتجر من الداتابيز دلوقتي: يدوّر بالاسم أو الوصف أو القسم أو السعر، ويرجّع السعر الحالي والسعر قبل الخصم والمقاسات والألوان المتاحة واللي خلصت والكمية القليلة واللينك. وبرقم منتج يرجّع كل تفاصيله ومواصفاته ودليل مقاساته.",
                    InputSchema = new InputSchema
                    {
                        Properties = new Dictionary<string, JsonElement>
                        {
                            ["query"] = J(new { type = "string", description = "كلمات البحث: اسم المنتج أو نوعه أو لونه. سيبها فاضية لو بتعرض قسم كامل." }),
                            ["id"] = J(new { type = "integer", description = "رقم المنتج، لو معروف، عشان كل تفاصيله." }),
                            ["category"] = J(new { type = "string", description = "اسم القسم لو الزبون سأل عن قسم." }),
                            ["max_price"] = J(new { type = "number", description = "أعلى سعر لو الزبون حدد ميزانية." }),
                            ["on_sale"] = J(new { type = "boolean", description = "true لو عايز اللي عليه خصم بس." }),
                            ["sort"] = J(new { type = "string", description = "cheap الأرخص، best الأكتر مبيعًا، new الأحدث." }),
                        }, Required = new List<string>()
                    }
                },
                Run = tu => Task.FromResult(with((c, s, sh, d) => Products(d, Arg(tu, "query"), (int)Num(tu, "id"), Arg(tu, "category"), Num(tu, "max_price"), Flag(tu, "on_sale"), Arg(tu, "sort")))),
                Prompt = null
            });
            list.Add(new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = TInfo,
                    Description = "معلومات المتجر من الداتابيز دلوقتي: الشحن (سعره ومدته لمحافظة أو مدينة)، العروض والأكواد الشغالة، الأقسام، الاسترجاع والصفحات، وطرق التواصل.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> { ["topic"] = J(new { type = "string", @enum = new[] { "shipping", "offers", "categories", "returns", "pages", "contact" } }), ["place"] = J(new { type = "string", description = "المحافظة أو المدينة لو السؤال عن الشحن." }) }, Required = new List<string> { "topic" } }
                },
                Run = tu => Task.FromResult(with((c, s, sh, d) => Info(c, sh, d, Arg(tu, "topic"), Arg(tu, "place")))),
                Prompt = null
            });
            list.Add(new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = TOrder,
                    Description = "حالة طلب الزبون من المتجر." + (phone != null ? " من غير رقم طلب بيرجّع آخر طلبات الزبون ده." : " محتاج رقم الطلب ورقم الموبايل اللي الطلب اتعمل بيه، وبيرجّع التفاصيل لو الاتنين مطابقين بس."),
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> { ["order_number"] = J(new { type = "string", description = "رقم الطلب زي 1042." }), ["phone"] = J(new { type = "string", description = "رقم الموبايل اللي الزبون قال إنه طلب بيه." }) }, Required = phone != null ? new List<string>() : new List<string> { "order_number", "phone" } }
                },
                Run = tu => Task.FromResult(with((c, s, sh, d) => OrderOf(c, sh, Arg(tu, "order_number"), phone, Arg(tu, "phone"), ip))),
                Prompt = null
            });
            using (var c = Db.Open()) { var s = Db.StoreById(c, storeId); var sh = For(c, s, preview); if (sh != null && (preview || ShopChatOrder.On(c, s, sh))) list.Add(ShopChatOrder.Tool(storeId, phone, ip, preview)); }
            ShopRew.BotTool(list, storeId, ip, preview);   // a gift card's balance, with its code
            return list;
        }
        static string Arg(ToolUseBlock tu, string n)
        {
            JsonElement v;
            if (tu.Input == null || !tu.Input.TryGetValue(n, out v)) return "";
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";
        }
        static decimal Num(ToolUseBlock tu, string n) { return ShopJson.Dec(Arg(tu, n)); }
        static bool Flag(ToolUseBlock tu, string n) { JsonElement v; return tu.Input != null && tu.Input.TryGetValue(n, out v) && v.ValueKind == JsonValueKind.True; }

        static string Line(ShopData d, ShopProd p, DateTime now)
        {
            decimal? old; ShopOffer of; var sb = new StringBuilder(); decimal lo = decimal.MaxValue, hi = 0, strike = 0; string offer = null;
            foreach (var v in p.Vars.Where(x => x.Active)) { var u = ShopPrice.Unit(d, p, v, now, out old, out of); if (u < lo) { lo = u; strike = old ?? 0; offer = of == null ? null : of.Name; } if (u > hi) hi = u; }
            if (lo == decimal.MaxValue) lo = p.Price;
            sb.Append("- [").Append(p.Id).Append("] ").Append(p.Name).Append(": ").Append(hi > lo ? "من " + ShopOrders.Money(lo) + " لـ " + ShopOrders.Money(hi) : ShopOrders.Money(lo));
            if (strike > lo) sb.Append(" بدل ").Append(ShopOrders.Money(strike)); if (offer != null) sb.Append(" (عرض ").Append(offer).Append(")");
            if (!p.Available) sb.Append("، خلصان دلوقتي");
            else if (p.Options.Count > 0) { var inn = p.Vars.Where(p.Can).Select(v => v.Label).Take(14).ToList(); var outt = p.Vars.Where(v => v.Active && !p.Can(v)).Select(v => v.Label).Take(8).ToList(); sb.Append("، المتاح: ").Append(string.Join("، ", inn)); if (outt.Count > 0) sb.Append("، خلص: ").Append(string.Join("، ", outt)); }
            else if (p.Track && p.OutMode != "order" && p.Total <= 5) sb.Append("، باقي ").Append(p.Total);
            ShopGoods.BotLine(d, p, sb);   // «حجز مسبق، متوقع يوصل يوم ...»
            return sb.Append("، اللينك: ").Append(d.Row.Url).Append("/p/").Append(p.Slug).ToString();
        }

        public static string Products(ShopData d, string query, int id, string category, decimal max, bool sale, string sort)
        {
            var now = DateTime.UtcNow; ShopProd one;
            var m = Regex.Match(query ?? "", @"/p/([a-z0-9-]+)"); if (m.Success && d.BySlug.TryGetValue(m.Groups[1].Value, out one)) id = one.Id;
            if (id > 0) return d.ById.TryGetValue(id, out one) && one.Shown ? Detail(d, one, now) : "مفيش منتج بالرقم ده.";
            HashSet<int> cats = null; string catName = null;
            if (!string.IsNullOrWhiteSpace(category))
            {
                var q = Search.Words(category).ToList(); var k = d.Cats.Where(d.CatShown).Select(x => new { x, n = Search.Words(x.Name + " " + x.NameEn).Intersect(q).Count() }).Where(a => a.n > 0).OrderByDescending(a => a.n).Select(a => a.x).FirstOrDefault();
                if (k != null) { cats = d.Under(k.Id); catName = k.Name; } else if (string.IsNullOrWhiteSpace(query)) query = category;
            }
            List<ShopProd> list = string.IsNullOrWhiteSpace(query) ? d.Shown.Where(p => cats == null || p.Cats.Any(cats.Contains)).ToList() : d.Find(query, 40, false, cats);
            if (list.Count == 0 && cats != null && !string.IsNullOrWhiteSpace(query)) list = d.Find(query, 40);
            bool near = false;
            if (max > 0) { var under = list.Where(p => p.Vars.Any(v => { decimal? o; ShopOffer of; return v.Active && ShopPrice.Unit(d, p, v, now, out o, out of) <= max; })).ToList(); if (under.Count > 0) list = under; else near = list.Count > 0; }
            if (sale) list = list.Where(p => p.Vars.Any(v => { decimal? o; ShopOffer of; ShopPrice.Unit(d, p, v, now, out o, out of); return o.HasValue; })).ToList();
            if (sort == "cheap") list = list.OrderBy(p => p.Price).ToList(); else if (sort == "best") list = list.OrderByDescending(p => p.Sold).ToList(); else if (sort == "new") list = list.OrderByDescending(p => p.CreatedAt).ToList();
            if (list.Count == 0) return "مفيش منتجات مطابقة في المتجر." + (d.Cats.Any(d.CatShown) ? " الأقسام الموجودة: " + string.Join("، ", d.Cats.Where(k => !k.ParentId.HasValue && d.CatShown(k)).Take(15).Select(k => k.Name)) + "." : "");
            var sb = new StringBuilder(); if (catName != null) sb.Append("قسم ").Append(catName).Append(" (").Append(list.Count).Append(" منتج):\n"); if (near) sb.Append("مفيش حاجة في حدود ").Append(ShopOrders.Money(max)).Append("، ودي الأقرب:\n");
            foreach (var p in list.Take(8)) sb.Append(Line(d, p, now)).Append("\n");
            if (list.Count > 8) sb.Append("وفيه ").Append(list.Count - 8).Append(" كمان في المتجر.");
            return sb.ToString().Trim();
        }

        static string Detail(ShopData d, ShopProd p, DateTime now)
        {
            var sb = new StringBuilder(Line(d, p, now)).Append("\n"); decimal? old; ShopOffer of;
            if (!string.IsNullOrWhiteSpace(p.Descr)) sb.Append("الوصف: ").Append(ShopPages.Plain(p.Descr, 900)).Append("\n");
            var specs = ShopJson.Arr(p.SpecsJson).Select(x => ShopJson.List(x)).Where(x => x.Count > 1).Select(x => ShopJson.Str(x[0]) + ": " + ShopJson.Str(x[1])).ToList(); if (specs.Count > 0) sb.Append("المواصفات: ").Append(string.Join("، ", specs)).Append("\n");
            if (p.Options.Count > 0 && p.Vars.Select(v => p.PriceOf(v)).Distinct().Count() > 1) sb.Append("الأسعار: ").Append(string.Join("، ", p.Vars.Where(v => v.Active).Take(20).Select(v => v.Label + " " + ShopOrders.Money(ShopPrice.Unit(d, p, v, now, out old, out of)) + (p.Can(v) ? "" : " (خلص)")))).Append("\n");
            var low = p.Vars.Where(v => v.Active && p.Track && p.OutMode != "order" && v.Stock > 0 && v.Stock <= 3).Take(6).Select(v => (v.Label.Length > 0 ? v.Label + " " : "") + "باقي " + v.Stock).ToList(); if (low.Count > 0) sb.Append("قرب يخلص: ").Append(string.Join("، ", low)).Append("\n");
            var goes = ShopView.With(d, p).Take(3).ToList(); if (goes.Count > 0) sb.Append("بيكمّله: ").Append(string.Join("، ", goes.Select(x => x.Name + " بـ " + ShopOrders.Money(ShopPrice.Unit(d, x, x.Vars.FirstOrDefault(x.Can), now, out old, out of)) + " (رقمه " + x.Id + ")"))).Append("\n");
            if (p.Reviews > 0) sb.Append("تقييمات الزباين: ").Append(p.Rating.ToString("0.#", CultureInfo.InvariantCulture)).Append(" من 5 (").Append(p.Reviews).Append(" تقييم)\n");
            if (!p.Available && p.OutMode != "order") { var rel = ShopView.Related(d, p, 3).ToList(); if (rel.Count > 0) sb.Append("بدائل متاحة:\n").Append(string.Join("\n", rel.Select(x => Line(d, x, now)))).Append("\n"); }
            var det = ShopView.Detail(d, p, now); object guide; if (det.TryGetValue("sg", out guide)) { var g = guide as Dictionary<string, object>; sb.Append("دليل المقاسات: ").Append(string.Join(" | ", ShopJson.List(ShopJson.At(g, "head")).Select(x => ShopJson.Str(x)))).Append(" ؛ ").Append(string.Join(" ؛ ", ShopJson.List(ShopJson.At(g, "rows")).Select(r => string.Join(" | ", ShopJson.List(r).Select(x => ShopJson.Str(x)))))).Append("\n"); }
            if (p.MinQty > 1) sb.Append("أقل كمية في الطلب: ").Append(p.MinQty).Append("\n");
            return sb.ToString().Trim();
        }

        public static string Info(SqlConnection c, Shop sh, ShopData d, string topic, string place)
        {
            var now = DateTime.UtcNow; var set = sh.Settings;
            switch (topic)
            {
                case "shipping":
                    {
                        string city; var g = ShopGeo.Find(place, out city); var over = ShopPrice.FreeOver(d, g == null ? 0 : g.Id, now); string days; bool off; decimal min; var fee = ShopPrice.ShipFee(d, g == null ? 0 : g.Id, out days, out off, out min) ?? 0; fee = ShopShipRate.Fee(c, d, g == null ? 0 : g.Id, city, null, fee); var sb = new StringBuilder();
                        if (g == null && !string.IsNullOrWhiteSpace(place)) sb.Append("مش عارف المكان «").Append(place).Append("» في أنهي محافظة، اسأل الزبون عن المحافظة. ");
                        if (g != null && off) return "مابنوصّلش لمحافظة " + g.Ar + " دلوقتي.";
                        if (g != null) sb.Append("الشحن لـ ").Append(city ?? g.Ar).Append(city != null ? " (محافظة " + g.Ar + ")" : "").Append(": ").Append(over == 0 ? "مجاني" : ShopOrders.Money(fee)).Append(string.IsNullOrEmpty(days) ? "" : "، وبياخد " + days + " يوم").Append(". ");
                        else if (ShopJson.Str(ShopJson.At(ShopJson.Sub(set, "ship"), "mode")) == "zones" && d.Zones.Count > 0) sb.Append("سعر الشحن حسب المحافظة: ").Append(string.Join("، ", d.Zones.Values.Where(z => !z.Off).Select(z => ShopGeo.Name(z.Gov) + " " + ShopOrders.Money(z.Fee ?? fee)))).Append(". باقي المحافظات ").Append(ShopOrders.Money(fee)).Append(". ");
                        else sb.Append("الشحن ").Append(over == 0 ? "مجاني" : ShopOrders.Money(fee)).Append(" لكل المحافظات").Append(string.IsNullOrEmpty(days) ? "" : "، وبياخد " + days + " يوم").Append(". ");
                        if (over > 0) sb.Append("الشحن مجاني للطلبات من ").Append(ShopOrders.Money(over)).Append(". ");
                        if (min > 0) sb.Append("أقل طلب ").Append(ShopOrders.Money(min)).Append(". ");
                        var blocked = d.Zones.Values.Where(z => z.Off).Select(z => ShopGeo.Name(z.Gov)).ToList(); if (blocked.Count > 0) sb.Append("مابنوصّلش لـ: ").Append(string.Join("، ", blocked)).Append(".");
                        sb.Append(ShopShipRate.BotText(d, g == null ? 0 : g.Id)).Append(ShopShipDay.BotText(c, sh, g == null ? 0 : g.Id)).Append(ShopSelf.BotText(sh));
                        return sb.ToString().Trim();
                    }
                case "offers":
                    {
                        var sb = new StringBuilder();
                        foreach (var o in d.Offers.Where(x => x.Running(now))) sb.Append("- ").Append(o.Name).Append(": ").Append(OfferText(d, o)).Append(o.EndsAt.HasValue ? "، لحد " + ShopPromo.Local(o.EndsAt).Replace("T", " ") : "").Append("\n");
                        // only the code the owner shows to everyone; the others are for the people they were given to
                        using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopCoupons WHERE StoreId=@s AND Popup=1 AND Active=1 AND (EndsAt IS NULL OR EndsAt>SYSUTCDATETIME()) AND (StartsAt IS NULL OR StartsAt<=SYSUTCDATETIME()) AND (MaxUses IS NULL OR Uses<MaxUses)", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader())
                            while (r.Read()) { var k = ShopPrice.ReadCoupon(r); sb.Append("- كود ").Append(k.Code).Append(": ").Append(k.Type == "ship" ? "شحن مجاني" : k.Type == "pct" ? "خصم " + k.Value.ToString("0.##") + "%" : "خصم " + ShopOrders.Money(k.Value)).Append(k.FirstOnly ? " على أول طلب" : "").Append(k.MinSub.HasValue ? " للطلبات من " + ShopOrders.Money(k.MinSub.Value) : "").Append("\n"); }
                        var sale = d.Shown.Where(p => p.Vars.Any(v => { decimal? o; ShopOffer of; ShopPrice.Unit(d, p, v, now, out o, out of); return o.HasValue; })).Take(6).ToList();
                        if (sale.Count > 0) sb.Append("منتجات عليها خصم:\n").Append(string.Join("\n", sale.Select(p => Line(d, p, now)))).Append("\nكلها: ").Append(sh.Url).Append("/offers");
                        return sb.Length == 0 ? "مفيش عروض شغالة دلوقتي." : sb.ToString().Trim();
                    }
                case "categories": { var l = d.Cats.Where(k => !k.ParentId.HasValue && d.CatShown(k)).Select(k => "- " + k.Name + " (" + k.Count + "): " + sh.Url + "/c/" + k.Slug + (k.Kids.Any(d.CatShown) ? "، وجواه: " + string.Join("، ", k.Kids.Where(d.CatShown).Select(x => x.Name)) : "")).ToList(); return l.Count == 0 ? "المتجر مفيهوش أقسام، كل المنتجات: " + sh.Url + "/c/all" : string.Join("\n", l); }
                case "returns": { var p = d.Pages.FirstOrDefault(x => x.Kind == "returns"); return "الاسترجاع والاستبدال خلال " + ShopJson.Int(ShopJson.At(set, "returnsDays"), 14) + " يوم من الاستلام. " + ShopShipBack.BotText(c, sh) + (p == null ? "" : ShopPages.Plain(p.Body, 1500) + " التفاصيل: " + sh.Url + "/page/" + p.Slug); }
                case "pages": return string.Join("\n", d.Pages.Where(p => !p.Hidden).Select(p => "- " + p.Title + " (" + sh.Url + "/page/" + p.Slug + "): " + ShopPages.Plain(p.Body, 500)));
                default:
                    {
                        var ct = ShopJson.Sub(set, "contact"); var so = ShopJson.Sub(set, "social"); var sb = new StringBuilder(); Action<string, string> add = (n, v) => { if (!string.IsNullOrEmpty(v)) sb.Append(n).Append(": ").Append(v).Append("\n"); };
                        add("تليفون", ShopJson.Str(ShopJson.At(ct, "phone"))); var wa = ShopJson.Str(ShopJson.At(ct, "wa")); if (wa.Length > 0) add("واتساب", "https://wa.me/" + wa); add("إيميل", ShopJson.Str(ShopJson.At(ct, "email"))); add("العنوان", ShopJson.Str(ShopJson.At(ct, "address"))); add("المواعيد", ShopJson.Str(ShopJson.At(ct, "hours"))); add("فيسبوك", ShopJson.Str(ShopJson.At(so, "fb"))); add("إنستجرام", ShopJson.Str(ShopJson.At(so, "ig")));
                        return sb.Length == 0 ? "صاحب المتجر ماكتبش طريقة تواصل." : sb.ToString().Trim();
                    }
            }
        }

        public static string OfferText(ShopData d, ShopOffer o)
        {
            var r = o.Rule; var sc = ShopJson.Sub(r, "scope"); string on = ShopJson.Bool(ShopJson.At(sc, "all")) ? "على المتجر كله" : "";
            if (on.Length == 0) { var names = ShopJson.List(ShopJson.At(sc, "cats")).Select(x => { ShopCat k; return d.CatById.TryGetValue(ShopJson.Int(x), out k) ? "قسم " + k.Name : null; }).Concat(ShopJson.List(ShopJson.At(sc, "prods")).Select(x => { ShopProd p; return d.ById.TryGetValue(ShopJson.Int(x), out p) ? p.Name : null; })).Where(x => x != null).Take(6).ToList(); on = names.Count > 0 ? "على " + string.Join("، ", names) : ""; }
            switch (o.Type)
            {
                case "auto": return "خصم " + (ShopJson.Str(ShopJson.At(r, "kind")) == "amt" ? ShopOrders.Money(ShopJson.Dec(ShopJson.At(r, "value"))) : ShopJson.Dec(ShopJson.At(r, "value")).ToString("0.##") + "%") + " " + on;
                case "bxgy": { var pct = ShopJson.Dec(ShopJson.At(r, "pct"), 100); return "اشتري " + ShopJson.Int(ShopJson.At(r, "buy"), 2) + " وخد " + ShopJson.Int(ShopJson.At(r, "get"), 1) + (pct >= 100 ? " هدية" : " بخصم " + pct.ToString("0.##") + "%") + " (على الأرخص) " + on; }
                case "qty": return string.Join("، ", ShopJson.List(ShopJson.At(r, "tiers")).OfType<Dictionary<string, object>>().Select(t => ShopJson.Int(ShopJson.At(t, "q")) + " قطع أو أكتر: خصم " + ShopJson.Dec(ShopJson.At(t, "pct")).ToString("0.##") + "%")) + " " + on;
                case "pack": return "بكدج بـ " + ShopOrders.Money(ShopJson.Dec(ShopJson.At(r, "price"))) + " لما تاخدهم مع بعض " + on;
                case "gift": { ShopProd gp; return "هدية " + (d.ById.TryGetValue(ShopJson.Int(ShopJson.At(r, "gift")), out gp) ? "«" + gp.Name + "» " : "") + "مع الطلب من " + ShopOrders.Money(ShopJson.Dec(ShopJson.At(r, "over"))); }
                case "up": return "قطعة واحدة بخصم " + (ShopJson.Str(ShopJson.At(r, "kind")) == "amt" ? ShopOrders.Money(ShopJson.Dec(ShopJson.At(r, "value"))) : ShopJson.Dec(ShopJson.At(r, "value")).ToString("0.##") + "%") + " مع أي طلب" + (ShopJson.Dec(ShopJson.At(r, "over")) > 0 ? " من " + ShopOrders.Money(ShopJson.Dec(ShopJson.At(r, "over"))) : "") + " " + on;
                default: { var over = ShopJson.Dec(ShopJson.At(r, "over")); var govs = ShopJson.List(ShopJson.At(r, "govs")).Select(x => ShopGeo.Name(ShopJson.Int(x))).Where(x => x.Length > 0).ToList(); return "شحن مجاني" + (over > 0 ? " للطلبات من " + ShopOrders.Money(over) : "") + (govs.Count > 0 ? " لـ " + string.Join("، ", govs) : ""); }
            }
        }

        static string OrderOf(SqlConnection c, Shop sh, string number, string own, string said, string ip)
        {
            int no; int.TryParse(Regex.Replace(ShopJson.Digits(number ?? ""), @"\D", ""), out no);
            if (own != null)
            {
                var ids = new List<long>(); using (var cmd = Db.Cmd(c, "SELECT TOP 3 Id FROM dbo.ShopOrders WHERE StoreId=@s AND (Phone=@p OR Phone2=@p) AND (@n=0 OR No=@n) ORDER BY Id DESC", "@s", sh.StoreId, "@p", Offers.Norm(own) ?? own, "@n", no)) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Convert.ToInt64(r[0]));
                if (ids.Count > 0) return string.Join("\n\n", ids.Select(i => Text(ShopOrders.ById(c, sh.StoreId, i), sh)));
                if (no == 0) return "الزبون ده ماعندوش طلبات بالرقم ده. لو طلب برقم تاني، اسأله عن رقم الطلب والرقم اللي طلب بيه.";
            }
            if (!Web.Allow("shop-bot-track:" + sh.StoreId + ":" + (ip ?? ""), 8, TimeSpan.FromMinutes(30))) return "الزبون حاول كتير. اعتذر وقوله يستخدم صفحة «طلبي فين؟»: " + sh.Url + "/track";
            var phone = ShopOrders.Mobile(said); var o = no > 0 && phone != null ? ShopOrders.ByNo(c, sh.StoreId, no) : null;
            if (o == null || (o.Phone != phone && o.Phone2 != phone)) return "البيانات دي مش مطابقة لأي طلب. ماتقولش أي تفاصيل، واطلب منه يتأكد من رقم الطلب ورقم الموبايل اللي طلب بيه.";
            return Text(o, sh);
        }
        static string Text(ShopOrder o, Shop sh)
        {
            var sb = new StringBuilder("طلب ").Append(o.NoText).Append(": حالته «").Append(ShopOrders.StatusText(o.Status)).Append("»، من ").Append(ShopPromo.Local(o.CreatedAt).Replace("T", " ")).Append("، الإجمالي ").Append(ShopOrders.Money(o.Total)).Append("\n").Append(ShopOrders.ItemsText(o, 8));
            if (!string.IsNullOrEmpty(o.Tracking)) sb.Append("رقم التتبع: ").Append(o.Tracking).Append(string.IsNullOrEmpty(o.Carrier) ? "" : " (" + o.Carrier + ")").Append("\n");
            sb.Append(ShopSelf.BotLine(o));
            if (o.Status == "new" || o.Status == "noreply") sb.Append("مستني تأكيد الزبون: يرد 1 على رسالة الواتساب عشان يتأكد.\n");
            if (o.Status == "cancelled") sb.Append("الطلب ملغي.\n");
            return sb.ToString().Trim();
        }

        // ---------- «البوت عارف إيه؟» ----------
        public static Dictionary<string, object> Knows(SqlConnection c, StoreInfo s, Shop sh)
        {
            var d = ShopCache.Get(c, sh); string test, at; using (var cmd = Db.Cmd(c, "SELECT BotTest, BotTestAt FROM dbo.Shops WHERE StoreId=@s", "@s", s.Id)) using (var r = cmd.ExecuteReader()) { r.Read(); test = r[0] as string; at = r[1] is DateTime ? DateTime.SpecifyKind((DateTime)r[1], DateTimeKind.Utc).ToString("o") : ""; }
            var extra = new List<string>(); using (var cmd = Db.Cmd(c, "SELECT Title FROM dbo.KnowledgeItems WHERE StoreId=@s ORDER BY Id", "@s", s.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) extra.Add((string)r[0]);
            return new Dictionary<string, object>
            {
                { "summary", Prompt(c, s, sh, d, null).Trim() }, { "shipping", Info(c, sh, d, "shipping", "") }, { "offers", Info(c, sh, d, "offers", "") }, { "returns", Info(c, sh, d, "returns", "") }, { "contact", Info(c, sh, d, "contact", "") }, { "products", Products(d, "", 0, "", 0, false, "best") }, { "count", d.Shown.Count() }, { "files", extra },
                { "test", ShopJson.Arr(test) }, { "testAt", at }, { "bot", s.BotName ?? "" }, { "channels", new Dictionary<string, object> { { "wa", !string.IsNullOrEmpty(s.WaInstance) && s.WaEnabled } } }
            };
        }

        // ---------- ten questions from the store's own rows, asked and checked ----------
        class Q { public string Text, Kind; public Func<string, bool> Ok; public string Want; }
        static List<Q> Questions(SqlConnection c, Shop sh, ShopData d)
        {
            var now = DateTime.UtcNow; var l = new List<Q>(); decimal? old; ShopOffer of;
            Func<string, string> norm = t => ShopJson.Digits(t ?? "").Replace(",", "");
            Func<decimal, Func<string, bool>> money = v => t => norm(t).Contains(v.ToString("0.##", CultureInfo.InvariantCulture));
            Func<string, bool> no = t => Regex.IsMatch(t ?? "", "مش |مفيش|ماعندناش|معندناش|للأسف|خلص|غير متاح|مش متاح|مش متأكد|ليس|لا يوجد|لا تتوفر|not ");
            var shown = d.Shown.Where(p => p.Available).OrderByDescending(p => p.Sold).ToList();
            foreach (var p in shown.Take(2)) { var v = p.Vars.FirstOrDefault(p.Can); var u = ShopPrice.Unit(d, p, v, now, out old, out of); l.Add(new Q { Text = "بكام " + p.Name + "؟", Kind = "السعر", Ok = money(u), Want = ShopOrders.Money(u) }); }
            var sized = shown.FirstOrDefault(p => p.Options.Count > 0 && p.Vars.Any(v => v.Active && !p.Can(v)));
            if (sized != null) { var v = sized.Vars.First(x => x.Active && !sized.Can(x)); l.Add(new Q { Text = "فيه " + sized.Name + " " + v.Label + "؟", Kind = "مقاس خلص", Ok = no, Want = "يقول خلص" }); }
            else if (shown.Any(p => p.Options.Count > 0)) { var p = shown.First(x => x.Options.Count > 0); var any = p.Vars.First(p.Can).Vals[0]; l.Add(new Q { Text = "إيه المتاح من " + p.Name + "؟", Kind = "المتاح", Ok = t => (t ?? "").Contains(any), Want = any }); }
            string days; bool off; decimal min; var over = ShopPrice.FreeOver(d, 5, now); var fee = ShopPrice.ShipFee(d, 5, out days, out off, out min) ?? 0; fee = ShopShipRate.Fee(c, d, 5, "المنصورة", null, fee);
            l.Add(new Q { Text = "الشحن للمنصورة بكام؟", Kind = "الشحن", Ok = off ? no : over == 0 ? (Func<string, bool>)(t => (t ?? "").Contains("مجان")) : money(fee), Want = off ? "مابنوصّلش" : over == 0 ? "مجاني" : ShopOrders.Money(fee) });
            int rd = ShopJson.Int(ShopJson.At(sh.Settings, "returnsDays"), 14); l.Add(new Q { Text = "ينفع أرجّع المنتج لو ماعجبنيش؟", Kind = "الاسترجاع", Ok = t => norm(t).Contains(rd.ToString()), Want = rd + " يوم" });
            l.Add(new Q { Text = "عندكم طيارة هليكوبتر حقيقية للبيع؟", Kind = "منتج مش موجود", Ok = no, Want = "يقول مش عندنا" });
            var run = d.Offers.Where(o => o.Running(now)).ToList(); bool anySale = run.Count > 0 || d.Shown.Any(p => p.Vars.Any(v => { decimal? o; ShopOffer f; ShopPrice.Unit(d, p, v, now, out o, out f); return o.HasValue; }));
            l.Add(new Q { Text = "فيه عروض إيه دلوقتي؟", Kind = "العروض", Ok = anySale ? (Func<string, bool>)(t => run.Count == 0 ? !Regex.IsMatch(t ?? "", "مفيش عروض") : run.Any(o => Search.Words(o.Name).Any(w => Search.Words(t).Contains(w))) || Regex.IsMatch(norm(t), "خصم|مجان|\\d")) : no, Want = anySale ? "يذكر العرض" : "يقول مفيش" });
            var cat = d.Cats.Where(k => !k.ParentId.HasValue && d.CatShown(k)).Select(k => k.Name).ToList(); if (cat.Count > 0) l.Add(new Q { Text = "بتبيعوا إيه؟", Kind = "الأقسام", Ok = t => cat.Any(n => Search.Words(n).Any(w => Search.Words(t).Contains(w))), Want = cat[0] });
            l.Add(new Q { Text = "بدفع إزاي؟", Kind = "الدفع", Ok = t => (t ?? "").Contains("استلام"), Want = "عند الاستلام" });
            l.Add(new Q { Text = "طلبي رقم 999999 ورقمي 01000000000 وصل لفين؟", Kind = "طلب مش بتاعه", Ok = t => Regex.IsMatch(t ?? "", "مش |اتأكد|تأكد|مطابق|للأسف|راجع"), Want = "يرفض" });
            return l.Take(10).ToList();
        }

        public static Func<Task> Test(SqlConnection c, StoreInfo s, Shop sh, HttpResponse res)
        {
            if (Claude.ApiKey == null) throw new ShopException("off", "المساعد مش متاح دلوقتي.");
            if (Billing.Expired(s) || Billing.TrialEnded(c, s) || Billing.LimitReached(c, s)) throw new ShopException("limit", "رصيد باقتك مش كفاية للاختبار.");
            if (!Web.Allow("shop-bt:" + s.Id, 4, TimeSpan.FromHours(1))) throw new ShopException("many", "الاختبار اتعمل كتير في ساعة. استنى شوية.");
            var d = ShopCache.Get(c, sh); if (!d.Shown.Any()) throw new ShopException("empty", "ضيف منتجات الأول.");
            int sid = s.Id;
            return async () => { var list = await Run(sid); Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "test", list } }); };
        }

        // also when a store is sent for review, so the admin sees it with the store
        public static async Task<List<object>> Run(int storeId)
        {
            StoreInfo s; Shop sh; ShopData d; List<Q> qs; var preps = new List<ChatHandler.Prepared>();
            using (var c = Db.Open())
            {
                s = Db.StoreById(c, storeId); sh = Shops.ByStore(c, storeId); d = ShopCache.Get(c, sh); qs = Questions(c, sh, d);
                foreach (var q in qs) preps.Add(ChatHandler.Prepare(c, s, new List<MessageParam> { new UserMessageParam { Content = q.Text } }, "test:" + storeId, "المحادثة دي على موقع المتجر.", null, true));
            }
            var res = new List<object>(); long i = 0, o = 0, cr = 0, cw = 0; int good = 0;
            for (int n = 0; n < qs.Count; n += 3)
            {
                var part = qs.Skip(n).Take(3).ToList(); var answers = await Task.WhenAll(preps.Skip(n).Take(3).Select(async p => { try { return await ChatHandler.Run(p); } catch (Exception ex) { Log.Error(ex); return null; } }));
                for (int k = 0; k < part.Count; k++)
                {
                    var a = answers[k]; string t = a == null ? "" : a.Text ?? ""; if (a != null) { i += a.In; o += a.Out; cr += a.CacheRead; cw += a.CacheWrite; }
                    bool ok = a != null && part[k].Ok(t); if (ok) good++;
                    res.Add(new Dictionary<string, object> { { "q", part[k].Text }, { "k", part[k].Kind }, { "a", t.Length > 700 ? t.Substring(0, 700) : t }, { "ok", ok }, { "want", part[k].Want } });
                }
            }
            using (var c = Db.Open())
            {
                Db.AddUsage(c, storeId, qs.Count, i, cr, cw, o);
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET BotTest=@t, BotTestAt=SYSUTCDATETIME(), BotScore=@g WHERE StoreId=@s", "@t", ShopJson.Write(res), "@g", good * 100 / Math.Max(1, qs.Count), "@s", storeId)) cmd.ExecuteNonQuery();
            }
            return res;
        }
    }

    // ---------- after an order ----------
    public static partial class ShopHooks
    {
        const int AnswerDays = 7;
        public static bool ConfirmOn(SqlConnection c, StoreInfo s) { var sh = Shops.ByStore(c, s.Id); return sh != null && ShopJson.Bool(ShopJson.At(ShopJson.Sub(sh.Settings, "order"), "confirm"), true) && WaNotify.CantSend(c, s) == null; }
        static string Cut(string s, int n) { return s == null ? null : s.Length > n ? s.Substring(0, n) : s; }
        static string First(string name) { return (name ?? "").Trim().Split(' ')[0]; }
        static string CrmStatus(string st) { return st == "shipped" || st == "out" ? "shipped" : st == "delivered" ? "delivered" : st == "cancelled" ? "cancelled" : st == "returned" ? "returned" : "open"; }

        // quiet: the customer confirmed it in the chat already, so no confirmation message
        static async Task Placed(int storeId, long orderId, bool quiet = false)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId); var sh = Shops.ByStore(c, storeId); var o = ShopOrders.ById(c, storeId, orderId); if (s == null || sh == null || o == null) return;
                    try { Crm.FromShop(c, s, new Crm.OrderIn { Source = "shop", Id = o.Id.ToString(CultureInfo.InvariantCulture), Name = o.NoText, Phone = o.Phone, Customer = o.Name, City = ShopGeo.Name(o.Gov), Currency = "ج.م", Items = Cut(string.Join("، ", o.Items.Select(i => i.Name + (i.Qty > 1 ? " × " + i.Qty : ""))), 300), Status = CrmStatus(o.Status), Total = o.Total, At = o.CreatedAt }); } catch (Exception ex) { Log.Error(ex); }
                    if (ShopJson.Bool(ShopJson.At(ShopJson.Sub(sh.Settings, "alerts"), "wa"), true) && !string.IsNullOrEmpty(s.OwnerPhone))
                    {
                        var t = new StringBuilder("🛒 *طلب جديد من متجرك* ").Append(o.NoText).Append("\n").Append(o.Name).Append(" · ").Append(ShopOrders.Local(o.Phone)).Append(" · ").Append(ShopGeo.Name(o.Gov)).Append("\n\n").Append(ShopOrders.ItemsText(o, 8)).Append("💰 الإجمالي: *").Append(ShopOrders.Money(o.Total)).Append("*\n");
                        t.Append(PayNote(o)); if (o.Dup) t.Append("⚠️ نفس الرقم طلب نفس المنتج النهارده، ممكن يكون مكرر.\n"); if (!string.IsNullOrEmpty(o.Note)) t.Append("📝 ").Append(o.Note).Append("\n");
                        var why = await WaNotify.ToOwner(c, s, t.ToString(), "sb-shop-new-" + o.Id); if (why != null && why != WaNotify.NotLinked) ShopOrders.Log_(c, null, storeId, orderId, "system", "تنبيه الواتساب ليك ماوصلش: " + why);
                    }
                    try { Carts.ShopOrdered(c, storeId, o.Phone, o.NoText); } catch (Exception ex) { Log.Error(ex); }
                    if (!quiet) await SendConfirm(c, s, sh, o);
                    try { await ShopPay.Low(c, s, sh, o); } catch (Exception ex) { Log.Error(ex); }
                }
            }
            catch (Exception ex) { Log.Error(ex); ShopOrders.Fail(storeId, "after-order", ex.Message); }
        }

        // an order the owner wrote down: it enters «زبايني»; the customer gets the confirmation only when the owner asked for it
        public static void OnPlacedByOwner(StoreInfo s0, long orderId, bool tell)
        {
            int storeId = s0.Id;
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    using (var c = Db.Open())
                    {
                        var s = Db.StoreById(c, storeId); var sh = Shops.ByStore(c, storeId); var o = ShopOrders.ById(c, storeId, orderId); if (s == null || sh == null || o == null) return;
                        Crm.FromShop(c, s, new Crm.OrderIn { Source = "shop", Id = o.Id.ToString(CultureInfo.InvariantCulture), Name = o.NoText, Phone = o.Phone, Customer = o.Name, City = ShopGeo.Name(o.Gov), Currency = "ج.م", Items = Cut(string.Join("، ", o.Items.Select(i => i.Name + (i.Qty > 1 ? " × " + i.Qty : ""))), 300), Status = CrmStatus(o.Status), Total = o.Total, At = o.CreatedAt });
                        Carts.ShopOrdered(c, storeId, o.Phone, o.NoText); if (tell) await SendConfirm(c, s, sh, o);
                    }
                }
                catch (Exception ex) { Log.Error(ex); }
            });
        }
        static async Task SendConfirm(SqlConnection c, StoreInfo s, Shop sh, ShopOrder o)
        {
            string status = "pending", note = null;
            if (!ShopJson.Bool(ShopJson.At(ShopJson.Sub(sh.Settings, "order"), "confirm"), true)) return;
            var why = WaNotify.CantSend(c, s);
            if (why != null) { status = "skipped"; note = why; }
            else if (Billing.Expired(s)) { status = "skipped"; note = "الباقة خلصت"; }
            else if (!Web.Allow("shop-oc:" + s.Id, 300, TimeSpan.FromHours(1))) { status = "skipped"; note = "طلبات كتير في ساعة واحدة"; }
            long id;
            try { using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopConfirms(StoreId, OrderId, OrderName, Customer, Phone, Total, Status, Note) OUTPUT INSERTED.Id VALUES(@s, @o, @n, @cu, @p, @t, @st, @no)", "@s", s.Id, "@o", o.Id, "@n", o.NoText, "@cu", Cut(o.Name, 100), "@p", o.Phone, "@t", ShopOrders.Money(o.Total), "@st", status, "@no", note)) id = Convert.ToInt64(cmd.ExecuteScalar()); }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return; }
            if (status != "pending") return;
            string text = ConfirmText(sh, o), chat = o.Phone + "@c.us", token = WaAccount.Token(c, s);
            long? mine = WhatsAppBot.Save(c, s.Id, o.Phone, false, text, null);
            try
            {
                int cutAt = text.IndexOf(o.Lang == "en" ? "\nReply *1*" : "\nاكتب *1*", StringComparison.Ordinal);
                bool buttons = await Wapilot.TrySendButtons(token, s.WaInstance, chat, cutAt > 0 ? text.Substring(0, cutAt).TrimEnd() : text, new[] { new[] { "sb_oc_yes", En("✅ تأكيد الطلب", "✅ Confirm", o) }, new[] { "sb_oc_no", En("❌ إلغاء الطلب", "❌ Cancel", o) } }, "sb-shop-oc-" + id);
                if (!buttons) await Wapilot.Send(token, s.WaInstance, chat, text, "sb-shop-oc-" + id);
                ShopOrders.Log_(c, null, s.Id, o.Id, "system", "رسالة التأكيد اتبعتت على الواتساب");
            }
            catch (WaException ex)
            {
                if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopConfirms SET Status='failed', Note=@n WHERE Id=@i", "@n", Cut(ex.Message, 300), "@i", id)) cmd.ExecuteNonQuery();
            }
        }

        // also the written notice the law asks for after the sale: what was ordered, the total, the way back, where to follow it
        static string En(string ar, string en, ShopOrder o) { return o.Lang == "en" ? en : ar; }
        static string MoneyEn(decimal v) { return "EGP " + v.ToString(v == Math.Floor(v) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture); }
        static string Pickup(ShopOrder o) { var b = ShopJson.Sub(o.Extras, "branch"); return b.Count == 0 ? null : string.Join("، ", new[] { ShopJson.Str(ShopJson.At(b, "n")), ShopJson.Str(ShopJson.At(b, "ad")) }.Where(y => y.Length > 0)); }
        // the parts of an order the owner mustn't miss: how it's paid, pickup, gift, another recipient
        static string PayNote(ShopOrder o)
        {
            var t = new StringBuilder(); if (o.Pay != "cod" || o.Deposit > 0) t.Append("💳 ").Append(ShopPay.WayName(o.Pay)).Append(o.Deposit > 0 && o.Deposit < o.Total ? "، عربون " + ShopOrders.Money(o.Deposit) : "").Append(o.PayState == "paid" ? " (اتدفع ✅)" : o.PayState == "sent" ? " (صورة التحويل وصلت)" : o.Deposit > 0 ? " (لسه مابعتش صورة التحويل)" : "").Append("\n");
            if (Pickup(o) != null) t.Append("🛍️ استلام من: ").Append(Pickup(o)).Append("\n"); if (o.Extras.ContainsKey("gift")) t.Append("🎁 رسالة الهدية: ").Append(ShopJson.Str(o.Extras["gift"], 200)).Append("\n"); else if ((o.FeesJson ?? "").Contains("\"gift\"")) t.Append("🎁 تغليف هدية\n");
            t.Append(ShopShipDay.Line(o, false)); var rc = ShopJson.Sub(o.Extras, "recv"); if (rc.Count > 0) t.Append("👤 المستلم: ").Append(ShopJson.Str(ShopJson.At(rc, "n"))).Append(" · ").Append(ShopOrders.Local(ShopJson.Str(ShopJson.At(rc, "p")))).Append("\n");
            t.Append(ShopRew.PayNote(o));
            return t.ToString();
        }
        public static string ConfirmText(Shop sh, ShopOrder o)
        {
            if (o.Lang == "en")
            {
                var e = new StringBuilder("Hello").Append(First(o.Name).Length > 0 ? " " + First(o.Name) : "").Append(" 👋\nThank you for ordering from *").Append(string.IsNullOrEmpty(sh.NameEn) ? sh.Name : sh.NameEn).Append("*!\n\n🧾 *Order ").Append(o.NoText).Append("*\n").Append(ShopOrders.ItemsText(o));
                if (o.Discount > 0) e.Append("🏷️ Discount: ").Append(MoneyEn(o.Discount)).Append("\n");
                foreach (var f in ShopJson.Arr(o.FeesJson).Select(ShopJson.List).Where(f => f.Count > 2)) e.Append("➕ ").Append(ShopJson.Str(ShopJson.List(f[1]).ElementAtOrDefault(1), 40)).Append(": ").Append(MoneyEn(ShopJson.Dec(f[2]))).Append("\n");
                if (Pickup(o) != null) e.Append("🛍️ Pick up from: ").Append(Pickup(o)); else e.Append("🚚 Delivery: ").Append(o.Ship > 0 ? MoneyEn(o.Ship) : "free");
                e.Append("\n💰 Total: *").Append(MoneyEn(o.Total)).Append("*\n💵 Payment: ").Append(ShopPay.WayName(o.Pay, true)).Append("\n"); if (Pickup(o) == null) e.Append("📍 To: ").Append(Cut(ShopOrders.AddressText(o), 200)).Append("\n"); e.Append(ShopShipDay.Line(o, true));
                e.Append(ShopRew.ConfirmNote(o, true));
                if (o.Deposit > 0 && o.PayState != "paid" && o.PayState != "sent") e.Append("\n💳 Please send *").Append(MoneyEn(o.Deposit)).Append("* and its picture from here: ").Append(sh.Url).Append("/pay/").Append(o.Token).Append("\n");
                e.Append("\n");
                e.Append("📦 Track it: ").Append(sh.Url).Append(sh.Url.EndsWith("/en") ? "" : "/en").Append("/track\n↩️ Returns within ").Append(ShopJson.Int(ShopJson.At(sh.Settings, "returnsDays"), 14)).Append(" days: ").Append(sh.Url).Append("/page/returns\n\nPlease confirm so we can start preparing it:\nReply *1* to confirm ✅\nReply *2* to cancel ❌");
                return e.ToString();
            }
            var sb = new StringBuilder("أهلًا").Append(First(o.Name).Length > 0 ? " " + First(o.Name) : "").Append(" 👋\nشكرًا إنك طلبت من *").Append(sh.Name).Append("*!\n\n🧾 *طلب رقم ").Append(o.NoText).Append("*\n").Append(ShopOrders.ItemsText(o));
            if (o.Discount > 0) sb.Append("🏷️ الخصم: ").Append(ShopOrders.Money(o.Discount)).Append("\n");
            foreach (var f in ShopJson.Arr(o.FeesJson).Select(ShopJson.List).Where(f => f.Count > 2)) sb.Append("➕ ").Append(ShopJson.Str(ShopJson.List(f[1]).ElementAtOrDefault(0), 40)).Append(": ").Append(ShopOrders.Money(ShopJson.Dec(f[2]))).Append("\n");
            if (Pickup(o) != null) sb.Append("🛍️ الاستلام من: ").Append(Pickup(o)); else sb.Append("🚚 الشحن: ").Append(o.Ship > 0 ? ShopOrders.Money(o.Ship) : "مجاني");
            sb.Append("\n💰 الإجمالي: *").Append(ShopOrders.Money(o.Total)).Append("*\n💵 الدفع: ").Append(ShopPay.WayName(o.Pay)).Append("\n"); if (Pickup(o) == null) sb.Append("📍 التوصيل: ").Append(Cut(ShopOrders.AddressText(o), 200)).Append("\n"); sb.Append(ShopShipDay.Line(o, false));
            sb.Append(ShopRew.ConfirmNote(o, false));
            if (o.Deposit > 0 && o.PayState != "paid" && o.PayState != "sent") sb.Append("\n💳 محتاجين تحويل *").Append(ShopOrders.Money(o.Deposit)).Append("*، وابعت صورته من هنا: ").Append(sh.Url).Append("/pay/").Append(o.Token).Append("\n");
            sb.Append("\n");
            sb.Append("📦 تابع طلبك: ").Append(sh.Url).Append("/track\n↩️ الاسترجاع خلال ").Append(ShopJson.Int(ShopJson.At(sh.Settings, "returnsDays"), 14)).Append(" يوم: ").Append(sh.Url).Append("/page/returns\n\nمن فضلك أكّد طلبك عشان نبدأ نجهّزه:\nاكتب *1* لتأكيد الطلب ✅\nاكتب *2* لإلغاء الطلب ❌");
            return sb.ToString();
        }

        static async Task Changed(int storeId, long id, string from, string to, string who)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId); var o = ShopOrders.ById(c, storeId, id); if (s == null || o == null) return;
                    Crm.SetStatus(c, storeId, "shop", id.ToString(CultureInfo.InvariantCulture), CrmStatus(to));
                    // the owner moved it on: stop waiting for the customer's answer
                    if (who != "customer") using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopConfirms SET Status=@st, Note=@n WHERE StoreId=@s AND OrderId=@o AND Status='pending'", "@st", to == "confirmed" ? "confirmed" : to == "cancelled" ? "cancelled" : "skipped", "@n", "غيّرت حالته لـ «" + ShopOrders.StatusText(to) + "»", "@s", storeId, "@o", id)) if (to != "noreply") cmd.ExecuteNonQuery();
                    if (to == "cancelled" && who != "carrier") Carriers.OnCancelled(storeId, id);
                    try { ShopDigital.OnStatus(c, storeId, id, to); } catch (Exception ex) { Log.Error(ex); }   // a cancel closes the digital links
                    if (to == "shipped" || to == "out") ShopSelf.Stamp(c, storeId, o);   // «بوصّل بنفسي»: who delivers it, before the messages say so
                    if (to == "out") await Delivery.OnShop(c, s, o);
                    if (to == "shipped") { var sh = Shops.ByStore(c, storeId); if (sh != null) await ShopPay.Shipped(c, s, sh, o); }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        static Task Edited(int storeId, long id)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId); var o = ShopOrders.ById(c, storeId, id); if (s == null || o == null) return Task.CompletedTask;
                    Crm.FromShop(c, s, new Crm.OrderIn { Source = "shop", Id = o.Id.ToString(CultureInfo.InvariantCulture), Name = o.NoText, Phone = o.Phone, Customer = o.Name, City = ShopGeo.Name(o.Gov), Currency = "ج.م", Items = Cut(string.Join("، ", o.Items.Select(i => i.Name + (i.Qty > 1 ? " × " + i.Qty : ""))), 300), Status = CrmStatus(o.Status), Total = o.Total, At = o.CreatedAt });
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET SheetAt=NULL WHERE StoreId=@s AND Id=@o; UPDATE dbo.ShopConfirms SET Phone=@p, Customer=@cu, Total=@t WHERE StoreId=@s AND OrderId=@o", "@p", o.Phone, "@cu", Cut(o.Name, 100), "@t", ShopOrders.Money(o.Total), "@s", storeId, "@o", id)) cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            return Task.CompletedTask;
        }

        // «مردّش» (Followup.cs told someone to call): the order says so
        public static void NoReply(SqlConnection c, int storeId, long confirmId)
        {
            using (var cmd = Db.Cmd(c, "SELECT OrderId FROM dbo.ShopConfirms WHERE Id=@i AND StoreId=@s", "@i", confirmId, "@s", storeId)) { var v = cmd.ExecuteScalar(); if (v == null) return; var o = ShopOrders.ById(c, storeId, Convert.ToInt64(v)); if (o != null && o.Status == "new") ShopOrders.SetStatus(c, storeId, o.Id, "noreply", "system"); }
        }

        public static object ConfirmOf(SqlConnection c, int storeId, long orderId)
        {
            using (var cmd = Db.Cmd(c, "SELECT Status, Note, CreatedAt, AnsweredAt, RemindedAt, AlertedAt, AlertTo FROM dbo.ShopConfirms WHERE StoreId=@s AND OrderId=@o", "@s", storeId, "@o", orderId)) using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null; Func<int, string> t = i => r[i] is DateTime ? DateTime.SpecifyKind((DateTime)r[i], DateTimeKind.Utc).ToString("o") : "";
                return new Dictionary<string, object> { { "status", (string)r[0] }, { "note", r[1] as string ?? "" }, { "at", t(2) }, { "answeredAt", t(3) }, { "remindedAt", t(4) }, { "alertedAt", t(5) }, { "alertTo", r[6] as string ?? "" } };
            }
        }

        // ---------- the customer's answer on WhatsApp («1» / «2»): before the assistant, like the other stores' orders ----------
        public static bool Intercept(SqlConnection c, StoreInfo s, string chatKey, string chat, string text, long rowId)
        {
            bool? yes = OrderConfirm.Decide(text);
            if (yes == null || !Shops.Ready) return false;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopConfirms WHERE StoreId=@s AND Status='pending' AND CreatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME())", "@s", s.Id, "@d", AnswerDays)) if ((int)cmd.ExecuteScalar() == 0) return false;
            int sid = s.Id; string phone = WaChat.Digits(chatKey);
            if (phone != null)
            {
                long? id = Pending(c, sid, phone); if (id == null || !Claim(c, id.Value, yes.Value)) return false;
                HostingEnvironment.QueueBackgroundWorkItem(ct => Act(sid, id.Value, yes.Value, chat, phone)); return true;
            }
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    long? id = null; string num = null;
                    using (var c2 = Db.Open()) { var s2 = Db.StoreById(c2, sid); var token = s2 == null ? null : WaAccount.Token(c2, s2); if (token != null && !string.IsNullOrEmpty(s2.WaInstance)) num = await Wapilot.LidPhone(token, s2.WaInstance, chat); if (num != null) { id = Pending(c2, sid, num); if (id != null && !Claim(c2, id.Value, yes.Value)) id = null; } }
                    if (id != null) await Act(sid, id.Value, yes.Value, chat, chatKey); else await WhatsAppBot.Reply(sid, chatKey, chat, rowId, ct);
                }
                catch (Exception ex) { Log.Error(ex); }
            });
            return true;
        }
        static long? Pending(SqlConnection c, int storeId, string phone)
        {
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id FROM dbo.ShopConfirms WHERE StoreId=@s AND Status='pending' AND Phone IS NOT NULL AND RIGHT(Phone, 9) = @t AND CreatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", storeId, "@t", phone.Length > 9 ? phone.Substring(phone.Length - 9) : phone, "@d", AnswerDays)) { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? (long?)null : Convert.ToInt64(v); }
        }
        static bool Claim(SqlConnection c, long id, bool yes) { using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopConfirms SET Status=@st, AnsweredAt=SYSUTCDATETIME() WHERE Id=@i AND Status='pending'", "@st", yes ? "confirmed" : "cancelled", "@i", id)) return cmd.ExecuteNonQuery() == 1; }
        static async Task Act(int storeId, long id, bool yes, string chat, string chatKey)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId); if (s == null) return; long orderId; using (var cmd = Db.Cmd(c, "SELECT OrderId FROM dbo.ShopConfirms WHERE Id=@i", "@i", id)) orderId = Convert.ToInt64(cmd.ExecuteScalar());
                    var o = ShopOrders.ById(c, storeId, orderId); if (o == null) return; var sh = Shops.ByStore(c, storeId); string reply;
                    if (yes)
                    {
                        if (o.Status == "new" || o.Status == "noreply") ShopOrders.SetStatus(c, storeId, orderId, "confirmed", "customer");
                        reply = En("تمام ✅ طلبك " + o.NoText + " اتأكد، وهنبدأ نجهّزه على طول. شكرًا لثقتك في *" + sh.Name + "* 🙏", "Done ✅ Order " + o.NoText + " is confirmed and we are preparing it. Thank you for choosing *" + sh.Name + "* 🙏", o);
                    }
                    else if (o.Status == "new" || o.Status == "noreply" || o.Status == "confirmed")
                    {
                        var err = ShopOrders.SetStatus(c, storeId, orderId, "cancelled", "customer", "الزبون لغى من رسالة التأكيد");
                        reply = err == null ? En("تم إلغاء طلبك " + o.NoText + " ❌\nلو غيّرت رأيك: " + sh.Url, "Order " + o.NoText + " is cancelled ❌\nIf you change your mind: " + sh.Url, o) : En("وصلنا إنك عايز تلغي طلبك " + o.NoText + "، وهنتواصل معاك نأكد الإلغاء 🙏", "We got your request to cancel order " + o.NoText + " and will contact you to confirm it 🙏", o);
                    }
                    else reply = En("طلبك " + o.NoText + " حالته «" + ShopOrders.StatusText(o.Status) + "»، فهنتواصل معاك بخصوص الإلغاء 🙏", "Order " + o.NoText + " is already on its way, so we will contact you about cancelling it 🙏", o);
                    var token = WaAccount.Token(c, s); if (token == null || string.IsNullOrEmpty(s.WaInstance)) return;
                    long? mine = WhatsAppBot.Save(c, s.Id, chatKey, false, reply, null);
                    try { await Wapilot.Send(token, s.WaInstance, chat, reply, "sb-shop-ans-" + id); }
                    catch (WaException ex) { Log.Error(ex); if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery(); }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        public static string PendingNote(SqlConnection c, int storeId, string chatKey)
        {
            var phone = WaChat.Digits(chatKey); if (phone == null || !Shops.Ready) return ""; var id = Pending(c, storeId, phone); if (id == null) return "";
            using (var cmd = Db.Cmd(c, "SELECT OrderName FROM dbo.ShopConfirms WHERE Id=@i", "@i", id.Value)) return "\n- الزبون ده عنده طلب " + (cmd.ExecuteScalar() as string) + " مستني تأكيده (اتبعتله رسالة بتفاصيله). لو كلامه معناه إنه موافق أو عايز يلغي، قوله يكتب 1 للتأكيد أو 2 للإلغاء. ماتقولش إن الطلب اتأكد أو اتلغى بنفسك.";
        }

        // ---------- the admin ----------
        static async Task ToAdmin(SqlConnection c, string text, string idem)
        {
            var ids = new List<int>(); using (var cmd = Db.Cmd(c, "SELECT Id FROM dbo.Stores WHERE IsAdmin=1 AND OwnerPhone IS NOT NULL")) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((int)r[0]);
            foreach (var id in ids) { var s = Db.StoreById(c, id); if (s != null && await WaNotify.ToOwner(c, s, text, idem) == null) return; }
        }
        public static async Task Failed(int storeId, string detail)
        {
            try { using (var c = Db.Open()) { var sh = Shops.ByStore(c, storeId); if (sh != null) await ToAdmin(c, "⚠️ *طلب فشل في متجر " + sh.Name + "*\n" + Cut(detail ?? "", 200) + "\n" + sh.Url + "\n\nالتفاصيل في «المتاجر المؤجرة» في الأدمن.", "sb-shop-fail-" + storeId + "-" + DateTime.UtcNow.Ticks); } }
            catch (Exception ex) { Log.Error(ex); }
        }
        public static async Task Reported(int storeId)
        {
            try
            {
                using (var c = Db.Open())
                {
                    int people; using (var cmd = Db.Cmd(c, "SELECT COUNT(DISTINCT Ip) FROM dbo.ShopReports WHERE StoreId=@s AND At>DATEADD(DAY,-1,SYSUTCDATETIME())", "@s", storeId)) people = (int)cmd.ExecuteScalar();
                    var sh = Shops.ByStore(c, storeId); if (sh == null || people < 3) return;
                    await ToAdmin(c, "🚨 *" + people + " بلاغات على متجر " + sh.Name + " في يوم*\n" + sh.Url + "\n\nافتح «المتاجر» في الأدمن وراجعه، وتقدر تقفله بضغطة.", "sb-shop-rep-" + storeId + "-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + people);
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        public static async Task Submitted(int storeId)
        {
            try
            {
                using (var c = Db.Open()) { var sh = Shops.ByStore(c, storeId); if (sh != null) await ToAdmin(c, "🛍️ *متجر جديد مستني مراجعتك*: " + sh.Name + "\nافتح «المتاجر» في الأدمن.", "sb-shop-sub-" + storeId + "-" + DateTime.UtcNow.ToString("yyyyMMddHH")); }
                await ShopBot.Run(storeId);
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
