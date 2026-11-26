// A store's look (template, colors, sections, footer, menu) and settings: what a new store starts with, and what is kept from what
// the dashboard sends. Nothing the owner writes is ever code: every value is a known key, a short text, a color, or a picture that
// was uploaded for this store. The look has a draft (what the owner edits) and the published copy (what customers see).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;

namespace RiwaqChat
{
    public static class ShopLook
    {
        // ids of the templates (site/shop/t/<id>.js); the admin can close any of them (AppSettings ShopTplOff)
        public static string[] Templates { get { return ShopTemplates.All(); } }   // the templates are files: site/shop/t
        public static readonly string[] Bizzes = { "clothes", "home", "beauty", "spices", "electronics", "toys", "sweets", "plants", "furniture", "jewelry", "grocery", "other" };
        static readonly string[] Sections = { "slider", "cats", "new", "best", "sale", "pick", "text", "marquee", "hot", "look", "ba", "budget", "faq", "trust", "banner", "video", "ugc", "revs", "posts" };
        static readonly Regex Hex = new Regex("^#[0-9a-fA-F]{6}$");

        public static string TemplateFor(string biz)
        {
            switch (biz)
            {
                case "clothes": return "atelier"; case "electronics": case "home": return "volt"; case "grocery": case "spices": return "taza"; case "beauty": return "nada"; case "furniture": return "rokn";
                case "plants": return "shatla"; case "toys": return "farfoush"; case "sweets": return "elba"; case "jewelry": return "bareeq"; default: return "atelier";
            }
        }

        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        static string[] P(string ar, string en) { return new[] { ar, en }; }

        // ---------- what a new store starts with ----------
        public static Dictionary<string, object> Start(StoreInfo s)
        {
            string color = s != null && Hex.IsMatch(s.BrandColor ?? "") && !string.Equals(s.BrandColor, "#e8774a", StringComparison.OrdinalIgnoreCase) ? s.BrandColor : "";
            return D("template", Templates[0], "langs", "ar", "first", "ar", "mode", "auto", "modeBtn", true, "color", color, "font", "", "motion", "mid",
                "ann", D("on", true, "text", P("الدفع عند الاستلام لكل المحافظات", "Cash on delivery all over Egypt"), "link", ""),
                "hero", D("img", "", "imgM", "", "title", P("", ""), "text", P("", ""), "btn", P("اتفرّج على المنتجات", "Shop now"), "link", ""),
                "sections", new object[] { D("t", "cats", "on", true), D("t", "new", "on", true), D("t", "best", "on", true), D("t", "sale", "on", true), D("t", "trust", "on", true) },
                "menu", new object[0],
                "footer", D("layout", "cols", "bg", "dark", "parts", D("about", true, "links", true, "contact", true, "social", true, "pay", true, "news", false), "about", P("", ""), "links", new object[0]),
                "badges", D("sale", true, "new", true, "best", true, "low", true, "newDays", 14, "lowAt", 3),
                "trust", new object[] { D("i", "cash", "t", P("الدفع عند الاستلام", "Cash on delivery")), D("i", "back", "t", P("استبدال واسترجاع خلال 14 يوم", "14-day returns")), D("i", "truck", "t", P("توصيل لباب البيت", "Delivered to your door")) },
                "opts", new Dictionary<string, object>());
        }

        public static Dictionary<string, object> StartSettings(StoreInfo s)
        {
            string wa = s == null ? "" : (s.WaNumber ?? "");
            return D("contact", D("phone", "", "wa", Regex.IsMatch(wa, @"^\+?\d{8,15}$") ? wa.TrimStart('+') : "", "email", "", "address", "", "hours", "", "waBtn", true),
                "social", D("fb", "", "ig", "", "tt", "", "yt", ""),
                "legal", D("seller", "", "address", "", "phone", "", "reg", "", "tax", ""),
                "ship", D("mode", "flat", "fee", 60, "freeOver", 0, "days", "2-4", "note", P("", "")),
                "order", D("min", 0, "phone2", true, "area", true, "waBtn", true, "maxQty", 20, "confirm", true),
                "pixel", D("meta", ""), "stack", false, "returnsDays", 14, "vacation", D("on", false, "until", "", "msg", P("", "")), "alerts", D("wa", true), "social_proof", true);
        }

        // Ready text for the pages the law and any customer expect; the owner edits them. The admin's lawyer checks them before the trial.
        public static void StartPages(SqlConnection c, StoreInfo s)
        {
            int sort = 0;
            Action<string, string, string, string, string, string> add = (slug, kind, t, te, b, be) =>
            {
                using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopPages WHERE StoreId=@s AND Slug=@slug) INSERT INTO dbo.ShopPages(StoreId, Slug, Kind, Title, TitleEn, Body, BodyEn, Sort) VALUES(@s, @slug, @k, @t, @te, @b, @be, @so)",
                    "@s", s.Id, "@slug", slug, "@k", kind, "@t", t, "@te", te, "@b", b, "@be", be, "@so", sort++)) cmd.ExecuteNonQuery();
            };
            add("about", "about", "من نحن", "About us", "اكتب هنا حكاية متجرك: بتبيع إيه، ومن إمتى، وليه الزبون يشتري منك.", "Tell your store's story here: what you sell, since when, and why customers buy from you.");
            add("shipping", "shipping", "الشحن والتوصيل", "Shipping and delivery",
                "بنوصّل لكل محافظات مصر، والدفع عند الاستلام.\n\nسعر الشحن ومدة التوصيل لمحافظتك بيظهروا في صفحة الطلب قبل ما تأكد.\n\nقبل التوصيل هتوصلك رسالة على واتساب. لو مالقيناكش، المندوب بيحاول تاني.",
                "We deliver all over Egypt, cash on delivery.\n\nThe shipping price and delivery time for your governorate are shown on the order page before you confirm.\n\nYou get a WhatsApp message before delivery. If we miss you, the courier tries again.");
            add("returns", "returns", "الاستبدال والاسترجاع", "Returns and exchanges",
                "من حقك ترجّع أو تستبدل المنتج خلال 14 يوم من يوم الاستلام، لو كان بحالته اللي استلمته بيها.\n\nلو المنتج فيه عيب أو غير اللي طلبته، الاستبدال أو الاسترجاع علينا.\n\nبعد ما المنتج يرجعلنا، فلوسك بترجعلك خلال 7 أيام.\n\nعشان تبدأ: كلّمنا على واتساب برقم طلبك.\n\nمنتجات القانون بيستثنيها، زي المعمول مخصوص ليك أو اللي بيتلف بسرعة أو المنتجات الشخصية بعد فتحها، مابترجعش إلا لو فيها عيب.",
                "You can return or exchange a product within 14 days of receiving it, in the condition you received it.\n\nIf it's faulty or not what you ordered, the return or exchange is on us.\n\nOnce the product is back with us, your money is refunded within 7 days.\n\nTo start: message us on WhatsApp with your order number.\n\nProducts the law excludes, such as made-to-order items, perishables and personal products once opened, can be returned only if faulty.");
            add("contact", "contact", "كلّمنا", "Contact us", "كلّمنا في أي وقت، وهنرد عليك في أسرع وقت.", "Contact us any time and we'll get back to you as soon as we can.");
            add("faq", "faq", "أسئلة متكررة", "FAQ",
                "## بدفع إزاي؟\nكاش للمندوب لما تستلم.\n\n## التوصيل بياخد قد إيه؟\nحسب محافظتك، والمدة بتظهر في صفحة الطلب.\n\n## أعرف طلبي فين إزاي؟\nمن صفحة «طلبي فين؟» برقم الطلب ورقم موبايلك، أو اسأل مساعد المتجر.\n\n## ينفع أرجّع المنتج؟\nأيوه، خلال 14 يوم من الاستلام. التفاصيل في صفحة الاستبدال والاسترجاع.",
                "## How do I pay?\nCash to the courier when you receive your order.\n\n## How long does delivery take?\nIt depends on your governorate. The time is shown on the order page.\n\n## Where is my order?\nOpen the \"Track my order\" page with your order number and phone, or ask the store's assistant.\n\n## Can I return a product?\nYes, within 14 days of receiving it. See the returns page.");
            add("terms", "terms", "الشروط والأحكام", "Terms and conditions",
                "باستخدامك للمتجر وتأكيدك لأي طلب، فإنت موافق على الشروط دي.\n\n## الطلب\nالطلب بيتأكد بعد ما نتواصل معاك. من حقنا نعتذر عن طلب لو المنتج خلص أو البيانات ناقصة.\n\n## الأسعار\nالسعر اللي بتشوفه بالجنيه المصري، والإجمالي بالشحن بيظهر قبل ما تأكد.\n\n## المسؤولية\nصاحب المتجر هو المسؤول عن المنتج والتوصيل والضمان والاسترجاع والفواتير. منصة SellyStore بتوفّر المتجر نفسه بس.\n\n## الشكاوى\nكلّمنا الأول من صفحة «كلّمنا». ولو شايف مخالفة، دوس «بلّغ عن المتجر» تحت في أي صفحة.",
                "By using this store and placing an order you agree to these terms.\n\n## Orders\nAn order is confirmed after we contact you. We may decline an order if a product is sold out or details are missing.\n\n## Prices\nPrices are in Egyptian pounds. The total including shipping is shown before you confirm.\n\n## Responsibility\nThe store's owner is responsible for products, delivery, warranty, returns and invoices. SellyStore provides the store itself only.\n\n## Complaints\nContact us first from the Contact page. To report a violation, use \"Report this store\" at the bottom of any page.");
            add("privacy", "privacy", "الخصوصية", "Privacy",
                "بنستخدم اسمك ورقمك وعنوانك عشان نوصّلك طلبك ونتواصل معاك بخصوصه بس.\n\nبياناتك بيشوفها صاحب المتجر، وشركة الشحن بتاخد اللي محتاجاه عشان التوصيل.\n\nمابنبيعش بياناتك لحد.\n\nالمتجر بيحفظ سلتك وبياناتك على موبايلك عشان المرة الجاية تبقى أسرع، وممكن يستخدم أدوات قياس الإعلانات.\n\nلو عايز بياناتك تتمسح، كلّمنا من صفحة «كلّمنا».",
                "We use your name, phone and address only to deliver your order and contact you about it.\n\nThe store's owner sees your details, and the shipping company gets what it needs to deliver.\n\nWe don't sell your data.\n\nThe store keeps your cart and details on your phone to make next time faster, and may use ad measurement tools.\n\nTo have your data deleted, contact us from the Contact page.");
        }

        // ---------- what is kept from what the dashboard sends ----------
        static string Txt(object v, int max) { return Regex.Replace(ShopJson.Str(v, max), @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", ""); }
        static object[] Pair(object v, int max) { var l = ShopJson.List(v); return new object[] { Txt(l.Count > 0 ? l[0] : null, max), Txt(l.Count > 1 ? l[1] : null, max) }; }
        static string One(object v, string dflt, params string[] allowed) { var s = ShopJson.Str(v, 20); return allowed.Contains(s) ? s : dflt; }
        static string Color(object v) { var s = ShopJson.Str(v, 9); return Hex.IsMatch(s) ? s.ToLowerInvariant() : ""; }
        public static string Img(object v, int storeId)
        {
            var s = ShopJson.Str(v, 160);
            return s.StartsWith(storeId + "/", StringComparison.Ordinal) && Regex.IsMatch(s, @"^\d+/[a-z0-9]{8,40}\.(webp|jpg|png)$") ? s : "";
        }
        // inside the store (/c/abayas, /p/.., /page/..) or a full https address
        public static string Link(object v)
        {
            var s = ShopJson.Str(v, 300);
            if (s.Length == 0) return "";
            if (Regex.IsMatch(s, @"^/(?!/)[A-Za-z0-9\-_/?=&%#.]*$")) return s;
            Uri u;
            return Uri.TryCreate(s, UriKind.Absolute, out u) && u.Scheme == "https" ? u.AbsoluteUri : "";
        }
        static object[] Links(object v, int max)
        {
            return ShopJson.List(v).OfType<Dictionary<string, object>>().Take(max).Select(x =>
            {
                var t = One(ShopJson.At(x, "t"), "", "cat", "page", "link", "home", "offers", "track");
                var o = D("t", t, "id", ShopJson.Int(ShopJson.At(x, "id")), "slug", Regex.Replace(ShopJson.Str(ShopJson.At(x, "slug"), 60), "[^a-z0-9-]", ""), "url", Link(ShopJson.At(x, "url")), "label", Pair(ShopJson.At(x, "label"), 60), "on", ShopJson.Bool(ShopJson.At(x, "on"), true));
                return t.Length == 0 || (t == "link" && (string)o["url"] == "") ? null : (object)o;
            }).Where(x => x != null).ToArray();
        }

        public static Dictionary<string, object> Clean(Dictionary<string, object> x, int storeId, ICollection<string> closedTemplates = null)
        {
            Func<string, object> g = k => ShopJson.At(x, k);
            var tpl = One(g("template"), Templates[0], Templates);
            var footer = ShopJson.Sub(x, "footer"); var parts = ShopJson.Sub(footer, "parts"); var hero = ShopJson.Sub(x, "hero"); var ann = ShopJson.Sub(x, "ann"); var bad = ShopJson.Sub(x, "badges");
            var opts = new Dictionary<string, object>();
            foreach (var kv in ShopJson.Sub(x, "opts").Take(60))
                if (Regex.IsMatch(kv.Key, "^[A-Za-z][A-Za-z0-9_.]{0,29}$"))
                    opts[kv.Key] = kv.Value is bool ? kv.Value : kv.Value is int || kv.Value is decimal || kv.Value is double ? kv.Value : ShopJson.List(kv.Value).Count > 0 ? (object)Pair(kv.Value, 300) : Txt(kv.Value, 300);
            return D("template", tpl, "langs", One(g("langs"), "ar", "ar", "en", "both"), "first", One(g("first"), "ar", "ar", "en"), "mode", One(g("mode"), "auto", "auto", "light", "dark"), "modeBtn", ShopJson.Bool(g("modeBtn"), true),
                "color", Color(g("color")), "font", One(g("font"), "", "", "a", "b", "c"), "motion", One(g("motion"), "mid", "none", "calm", "mid", "strong"),
                "logo", Img(g("logo"), storeId), "logoDark", Img(g("logoDark"), storeId), "icon", Img(g("icon"), storeId), "share", Img(g("share"), storeId), "logoH", Math.Max(20, Math.Min(90, ShopJson.Int(g("logoH"), 40))),
                "ann", D("on", ShopJson.Bool(ShopJson.At(ann, "on")), "text", Pair(ShopJson.At(ann, "text"), 160), "link", Link(ShopJson.At(ann, "link"))),
                "hero", D("img", Img(ShopJson.At(hero, "img"), storeId), "imgM", Img(ShopJson.At(hero, "imgM"), storeId), "title", Pair(ShopJson.At(hero, "title"), 100), "text", Pair(ShopJson.At(hero, "text"), 240), "btn", Pair(ShopJson.At(hero, "btn"), 40), "link", Link(ShopJson.At(hero, "link"))),
                "sections", ShopJson.List(g("sections")).OfType<Dictionary<string, object>>().Where(s => Sections.Contains(ShopJson.Str(ShopJson.At(s, "t"), 12))).Take(20).Select(s => (object)Section(s, storeId)).ToArray(),
                "menu", Links(g("menu"), 14),
                "footer", D("layout", One(ShopJson.At(footer, "layout"), "cols", "cols", "center", "big"), "bg", One(ShopJson.At(footer, "bg"), "dark", "dark", "light", "color"),
                    "parts", D("about", ShopJson.Bool(ShopJson.At(parts, "about"), true), "links", ShopJson.Bool(ShopJson.At(parts, "links"), true), "contact", ShopJson.Bool(ShopJson.At(parts, "contact"), true),
                        "social", ShopJson.Bool(ShopJson.At(parts, "social"), true), "pay", ShopJson.Bool(ShopJson.At(parts, "pay"), true), "news", ShopJson.Bool(ShopJson.At(parts, "news"))),
                    "about", Pair(ShopJson.At(footer, "about"), 400), "links", Links(ShopJson.At(footer, "links"), 16)),
                "badges", D("sale", ShopJson.Bool(ShopJson.At(bad, "sale"), true), "new", ShopJson.Bool(ShopJson.At(bad, "new"), true), "best", ShopJson.Bool(ShopJson.At(bad, "best"), true), "low", ShopJson.Bool(ShopJson.At(bad, "low"), true),
                    "newDays", Math.Max(1, Math.Min(90, ShopJson.Int(ShopJson.At(bad, "newDays"), 14))), "lowAt", Math.Max(1, Math.Min(50, ShopJson.Int(ShopJson.At(bad, "lowAt"), 3)))),
                "trust", ShopJson.List(g("trust")).OfType<Dictionary<string, object>>().Take(6).Select(t => (object)D("i", One(ShopJson.At(t, "i"), "check", "cash", "back", "truck", "shield", "gift", "star", "phone", "check"), "t", Pair(ShopJson.At(t, "t"), 60))).ToArray(),
                "opts", opts, "season", ShopSeasons.Clean(g("season")));
        }

        static Dictionary<string, object> Section(Dictionary<string, object> s, int storeId)
        {
            return D("t", ShopJson.Str(ShopJson.At(s, "t"), 12), "on", ShopJson.Bool(ShopJson.At(s, "on"), true), "title", Pair(ShopJson.At(s, "title"), 80), "text", Pair(ShopJson.At(s, "text"), 600), "cat", ShopJson.Int(ShopJson.At(s, "cat")),
                "n", Math.Max(2, Math.Min(24, ShopJson.Int(ShopJson.At(s, "n"), 8))), "img", Img(ShopJson.At(s, "img"), storeId), "img2", Img(ShopJson.At(s, "img2"), storeId), "vid", Regex.IsMatch(ShopJson.Str(ShopJson.At(s, "vid"), 160), "^" + storeId + @"/[a-z0-9]{8,40}\.(mp4|webm)$") ? ShopJson.Str(ShopJson.At(s, "vid"), 160) : "", "link", Link(ShopJson.At(s, "link")), "btn", Pair(ShopJson.At(s, "btn"), 40),
                "items", ShopJson.List(ShopJson.At(s, "items")).OfType<Dictionary<string, object>>().Take(12).Select(i => (object)D("img", Img(ShopJson.At(i, "img"), storeId), "imgM", Img(ShopJson.At(i, "imgM"), storeId),
                    "title", Pair(ShopJson.At(i, "title"), 120), "text", Pair(ShopJson.At(i, "text"), 600), "btn", Pair(ShopJson.At(i, "btn"), 40), "link", Link(ShopJson.At(i, "link")), "p", ShopJson.Int(ShopJson.At(i, "p")),
                    "x", Math.Max(0, Math.Min(100, ShopJson.Int(ShopJson.At(i, "x")))), "y", Math.Max(0, Math.Min(100, ShopJson.Int(ShopJson.At(i, "y")))))).ToArray());
        }

        // every picture a look uses (so the rest can be cleaned)
        public static IEnumerable<string> Images(object v)
        {
            var d = v as Dictionary<string, object>;
            if (d != null) { foreach (var kv in d) foreach (var x in Images(kv.Value)) yield return x; yield break; }
            var s = v as string;
            if (s != null) { if (Regex.IsMatch(s, @"^\d+/[a-z0-9]{8,40}\.(webp|jpg|png)$")) yield return s; yield break; }
            if (v is System.Collections.IEnumerable) foreach (var i in (System.Collections.IEnumerable)v) foreach (var x in Images(i)) yield return x;
        }

        // ---------- settings ----------
        static string Phone(object v) { var t = Regex.Replace(ShopJson.Digits(ShopJson.Str(v, 24)), @"[^\d+]", ""); return t.Length >= 5 && t.Length <= 16 ? t : ""; }
        static string Page(object v, string host)
        {
            var s = ShopJson.Str(v, 200);
            if (s.Length == 0) return "";
            if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) s = "https://" + (s.Contains(".") ? s : host + "/" + s.TrimStart('@'));
            Uri u;
            return Uri.TryCreate(s, UriKind.Absolute, out u) && (u.Scheme == "https" || u.Scheme == "http") && (u.Host == host || u.Host.EndsWith("." + host) || (host == "facebook.com" && (u.Host == "fb.com" || u.Host == "www.fb.com" || u.Host == "fb.me"))) ? "https://" + u.Host + u.PathAndQuery : "";
        }

        // «عربون أو تحويل»: where the owner receives the money
        static Dictionary<string, object> Pay(Dictionary<string, object> p)
        {
            var ways = new List<object>();
            foreach (var w in ShopJson.List(ShopJson.At(p, "ways")).OfType<Dictionary<string, object>>())
            {
                string k = ShopJson.Str(ShopJson.At(w, "k"), 6); if (!ShopPrice.Ways.Contains(k) || ways.Any(y => (string)((Dictionary<string, object>)y)["k"] == k)) continue;
                ways.Add(D("k", k, "on", ShopJson.Bool(ShopJson.At(w, "on")), "to", Txt(ShopJson.At(w, "to"), 80), "name", Txt(ShopJson.At(w, "name"), 60)));
            }
            var dp = ShopJson.Sub(p, "deposit");
            return D("cod", ShopJson.Bool(ShopJson.At(p, "cod"), true), "ways", ways, "deposit", D("on", ShopJson.Bool(ShopJson.At(dp, "on")), "kind", One(ShopJson.At(dp, "kind"), "pct", "pct", "amt"), "value", Money(ShopJson.At(dp, "value"), 100000), "who", One(ShopJson.At(dp, "who"), "all", "all", "new"), "over", Money(ShopJson.At(dp, "over"), 1000000)));
        }
        // «فروع ومواعيد وخريطة»: where the shop is, and which branch a customer may collect from
        static List<object> Branches(object v)
        {
            var l = new List<object>();
            foreach (var b in ShopJson.List(v).OfType<Dictionary<string, object>>().Take(20))
            {
                string n = Txt(ShopJson.At(b, "n"), 60), id = ShopJson.Str(ShopJson.At(b, "id"), 12), map = ShopJson.Str(ShopJson.At(b, "map"), 300); int g = ShopJson.Int(ShopJson.At(b, "gov")); if (n.Length < 2 || ShopGeo.Get(g) == null) continue;
                if (!Regex.IsMatch(id, "^[a-z0-9]{3,12}$")) id = Shops.Token(4);
                if (!Regex.IsMatch(map, @"^https://(www\.)?(google\.[a-z.]{2,6}/maps|maps\.google\.[a-z.]{2,6}|maps\.app\.goo\.gl|goo\.gl/maps)[/?].{1,260}$")) map = "";
                l.Add(D("id", id, "n", n, "gov", g, "ad", Txt(ShopJson.At(b, "ad"), 200), "ph", Phone(ShopJson.At(b, "ph")), "hours", Txt(ShopJson.At(b, "hours"), 100), "map", map, "pickup", ShopJson.Bool(ShopJson.At(b, "pickup"), true)));
            }
            return l;
        }
        public static Dictionary<string, object> CleanSettings(Dictionary<string, object> x, Dictionary<string, object> old)
        {
            Func<string, Dictionary<string, object>> sub = k => { var had = ShopJson.Sub(old, k); var now = ShopJson.At(x, k) as Dictionary<string, object>; if (now == null) return had; var all = new Dictionary<string, object>(had); foreach (var kv in now) all[kv.Key] = kv.Value; return all; };
            var ct = sub("contact"); var so = sub("social"); var lg = sub("legal"); var sh = sub("ship"); var or = sub("order"); var px = sub("pixel"); var va = sub("vacation"); var al = sub("alerts");
            Func<string, object> top = k => x.ContainsKey(k) ? x[k] : ShopJson.At(old, k);
            return D(
                "contact", D("phone", Phone(ShopJson.At(ct, "phone")), "wa", Wa(ShopJson.At(ct, "wa")), "email", Txt(ShopJson.At(ct, "email"), 100), "address", Txt(ShopJson.At(ct, "address"), 200), "hours", Txt(ShopJson.At(ct, "hours"), 100), "waBtn", ShopJson.Bool(ShopJson.At(ct, "waBtn"), true)),
                "social", D("fb", Page(ShopJson.At(so, "fb"), "facebook.com"), "ig", Page(ShopJson.At(so, "ig"), "instagram.com"), "tt", Page(ShopJson.At(so, "tt"), "tiktok.com"), "yt", Page(ShopJson.At(so, "yt"), "youtube.com")),
                "legal", D("seller", Txt(ShopJson.At(lg, "seller"), 120), "address", Txt(ShopJson.At(lg, "address"), 200), "phone", Phone(ShopJson.At(lg, "phone")), "reg", Txt(ShopJson.At(lg, "reg"), 40), "tax", Txt(ShopJson.At(lg, "tax"), 40)),
                "ship", D("mode", One(ShopJson.At(sh, "mode"), "flat", "flat", "zones"), "fee", Money(ShopJson.At(sh, "fee"), 5000), "freeOver", Money(ShopJson.At(sh, "freeOver"), 1000000), "days", Txt(ShopJson.At(sh, "days"), 30), "note", Pair(ShopJson.At(sh, "note"), 200)),
                "order", D("min", Money(ShopJson.At(or, "min"), 100000), "phone2", ShopJson.Bool(ShopJson.At(or, "phone2"), true), "area", ShopJson.Bool(ShopJson.At(or, "area"), true), "waBtn", ShopJson.Bool(ShopJson.At(or, "waBtn"), true), "maxQty", Math.Max(1, Math.Min(500, ShopJson.Int(ShopJson.At(or, "maxQty"), 20))), "confirm", ShopJson.Bool(ShopJson.At(or, "confirm"), true),
                    "chat", ShopJson.Bool(ShopJson.At(or, "chat"), true), "otp", ShopJson.Bool(ShopJson.At(or, "otp")), "selfCancel", ShopJson.Bool(ShopJson.At(or, "selfCancel"), true), "block", ShopJson.Bool(ShopJson.At(or, "block"), true), "recv", ShopJson.Bool(ShopJson.At(or, "recv")), "codFee", Money(ShopJson.At(or, "codFee"), 1000),
                    "gift", D("on", ShopJson.Bool(ShopJson.At(ShopJson.Sub(or, "gift"), "on")), "price", Money(ShopJson.At(ShopJson.Sub(or, "gift"), "price"), 2000))),
                "acct", D("on", ShopJson.Bool(ShopJson.At(sub("acct"), "on"), true)), "returns", D("on", ShopJson.Bool(ShopJson.At(sub("returns"), "on"), true)), "restock", D("on", ShopJson.Bool(ShopJson.At(sub("restock"), "on"), true)), "news", D("on", ShopJson.Bool(ShopJson.At(sub("news"), "on"), true)),
                "reviews", D("on", ShopJson.Bool(ShopJson.At(sub("reviews"), "on"), true), "auto", ShopJson.Bool(ShopJson.At(sub("reviews"), "auto"), true), "ask", ShopJson.Bool(ShopJson.At(sub("reviews"), "ask"), true)),
                "abandon", D("on", ShopJson.Bool(ShopJson.At(sub("abandon"), "on"), true), "delay", Carts.Delays.Contains(ShopJson.Int(ShopJson.At(sub("abandon"), "delay"), 60)) ? ShopJson.Int(ShopJson.At(sub("abandon"), "delay"), 60) : 60, "coupon", ShopPrice.CodeNorm(Txt(ShopJson.At(sub("abandon"), "coupon"), 30))),
                "pay", Pay(sub("pay")), "branches", Branches(x.ContainsKey("branches") ? x["branches"] : ShopJson.At(old, "branches")),
                "tax", D("on", ShopJson.Bool(ShopJson.At(sub("tax"), "on")), "pct", Math.Min(40, Money(ShopJson.At(sub("tax"), "pct"), 40))),
                "sheet", D("url", ShopPay.SheetUrl(ShopJson.Str(ShopJson.At(sub("sheet"), "url"), 200)) ? ShopJson.Str(ShopJson.At(sub("sheet"), "url"), 200) : ""),
                "pixel", D("meta", Regex.IsMatch(ShopJson.Str(ShopJson.At(px, "meta"), 24), @"^\d{8,20}$") ? ShopJson.Str(ShopJson.At(px, "meta"), 24) : "", "tiktok", Regex.IsMatch(ShopJson.Str(ShopJson.At(px, "tiktok"), 30), @"^[A-Z0-9]{16,24}$") ? ShopJson.Str(ShopJson.At(px, "tiktok"), 30) : "",
                    "snap", Regex.IsMatch(ShopJson.Str(ShopJson.At(px, "snap"), 40), @"^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$") ? ShopJson.Str(ShopJson.At(px, "snap"), 40) : "", "ga", Regex.IsMatch(ShopJson.Str(ShopJson.At(px, "ga"), 20), @"^G-[A-Z0-9]{6,14}$") ? ShopJson.Str(ShopJson.At(px, "ga"), 20) : ""),
                "stack", ShopJson.Bool(top("stack")), "returnsDays", Math.Max(14, Math.Min(90, ShopJson.Int(top("returnsDays"), 14))),
                "vacation", D("on", ShopJson.Bool(ShopJson.At(va, "on")), "until", Regex.IsMatch(ShopJson.Str(ShopJson.At(va, "until"), 10), @"^\d{4}-\d\d-\d\d$") ? ShopJson.Str(ShopJson.At(va, "until"), 10) : "", "msg", Pair(ShopJson.At(va, "msg"), 200)),
                "alerts", D("wa", ShopJson.Bool(ShopJson.At(al, "wa"), true), "cancel", ShopJson.Bool(ShopJson.At(al, "cancel"), true), "edit", ShopJson.Bool(ShopJson.At(al, "edit"), true), "return", ShopJson.Bool(ShopJson.At(al, "return"), true), "review", ShopJson.Bool(ShopJson.At(al, "review"), true), "pay", ShopJson.Bool(ShopJson.At(al, "pay"), true),
                    "low", ShopJson.Bool(ShopJson.At(al, "low"), true), "lowAt", Math.Max(0, Math.Min(100, ShopJson.Int(ShopJson.At(al, "lowAt"), 3))), "shipMsg", ShopJson.Bool(ShopJson.At(al, "shipMsg"), true)));
        }
        static decimal Money(object v, decimal max) { return Math.Max(0, Math.Min(max, ShopJson.Dec(v))); }
        // a WhatsApp number as wa.me needs it: 2010...
        public static string Wa(object v) { var p = Phone(v); if (p.Length == 0) return ""; return Offers.Norm(p) ?? p.TrimStart('+'); }
    }
}
