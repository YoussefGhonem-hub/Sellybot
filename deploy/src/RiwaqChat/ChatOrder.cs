// Ordering from the chat (Shopify, full connection + write_draft_orders).
// The assistant finds the product, asks the product's own options in order and the quantity, then the customer's name, phone,
// governorate, city and address and the shipping option; Shopify prices it all (draftOrderCalculate: shipping, discounts, tax),
// the customer confirms the summary, the phone is verified (a code on WhatsApp; inside a WhatsApp chat the chat itself proves
// the number), and then a draft order is created and completed as cash on delivery, or its checkout link is sent for card payment.
// Prices, stock and totals always come from Shopify, never from the model: the model only picks variants and passes the
// customer's answers. State lives in dbo.ChatOrders per chat session (web: a random id kept by the widget; WhatsApp: the number).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    // Who is ordering: the store, the chat session and (on WhatsApp) the number the chat comes from.
    public class OrderCtx { public StoreInfo Store; public string Sid, VerifiedPhone, Ip; }

    public static class ChatOrders
    {
        public const string Scope = "write_draft_orders";
        public const string TagChat = "Sellybot-chat";
        public const string Feature = "chatorders";
        const int CodeMinutes = 10, CodeTries = 5, KeepDays = 2;

        public static readonly string[] ToolNames = { "shop_find_product", "shop_set_cart", "shop_set_customer", "shop_quote", "shop_confirm", "shop_verify_code", "shop_cancel_order" };

        // ---------- is it on for this store? ----------
        public static bool Ready(StoreInfo s)
        {
            return s != null && s.CoOn && ShopifyAdmin.For(s) != null && ShopifyAdmin.Has(s.ShopifyScopes, Scope) && ShopifyAdmin.Has(s.ShopifyScopes, "read_products");
        }
        // runtime check (the plan may have changed since it was switched on): needs both this feature and Shopify in the store's plan
        public static bool Enabled(SqlConnection c, StoreInfo s) { return Ready(s) && PlanGate.Allows(c, s, Feature) && PlanGate.Allows(c, s, "shopify") && !Billing.Expired(s); }

        // the widget's random session id -> "w:<id>"; anything else -> no ordering for that request
        public static string WebSid(string raw) { return raw != null && Regex.IsMatch(raw, "^[A-Za-z0-9_-]{16,64}$") ? "w:" + raw : null; }

        public static OrderCtx ForChannel(SqlConnection c, StoreInfo s, string sid, string ip)
        {
            if (sid == null || !Enabled(c, s)) return null;
            string phone = sid.StartsWith("wa:") ? WaChat.Digits(sid.Substring(3)) : null;
            return new OrderCtx { Store = s, Sid = sid, VerifiedPhone = phone, Ip = ip };
        }

        // ---------- the order being built in this chat ----------
        class Row
        {
            public long Id; public string Status = "draft", Items, Name, Phone, Province, ProvinceCode, City, Address, Rates, ShipTitle, Payment, Currency, DraftGid, OrderName, InvoiceUrl, Note, CodeHash;
            public decimal? ShipPrice, Subtotal, Shipping, Discounts, Tax, Total;
            public DateTime? CodeExpires; public int CodeTries, CodeSends;
        }
        class Item { public string V { get; set; } public string T { get; set; } public string VT { get; set; } public int Q { get; set; } public decimal P { get; set; } }
        class Rate { public string T { get; set; } public decimal P { get; set; } }

        static Row Current(SqlConnection c, OrderCtx x)
        {
            using (var cmd = Db.Cmd(c, @"SELECT TOP 1 * FROM dbo.ChatOrders WHERE StoreId=@s AND Sid=@sid AND Status IN ('draft','code_sent')
                AND UpdatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", x.Store.Id, "@sid", x.Sid, "@d", KeepDays))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                return new Row
                {
                    Id = (long)r["Id"], Status = (string)r["Status"], Items = r["Items"] as string, Name = r["Name"] as string, Phone = r["Phone"] as string,
                    Province = r["Province"] as string, ProvinceCode = r["ProvinceCode"] as string, City = r["City"] as string, Address = r["Address"] as string,
                    Rates = r["Rates"] as string, ShipTitle = r["ShipTitle"] as string, Payment = r["Payment"] as string, Currency = r["Currency"] as string,
                    DraftGid = r["DraftGid"] as string, OrderName = r["OrderName"] as string, InvoiceUrl = r["InvoiceUrl"] as string, Note = r["Note"] as string,
                    CodeHash = r["CodeHash"] as string, ShipPrice = r["ShipPrice"] as decimal?, Subtotal = r["Subtotal"] as decimal?, Shipping = r["Shipping"] as decimal?,
                    Discounts = r["Discounts"] as decimal?, Tax = r["Tax"] as decimal?, Total = r["Total"] as decimal?,
                    CodeExpires = r["CodeExpires"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["CodeExpires"], DateTimeKind.Utc),
                    CodeTries = (int)r["CodeTries"], CodeSends = (int)r["CodeSends"],
                };
            }
        }

        static Row Ensure(SqlConnection c, OrderCtx x)
        {
            var row = Current(c, x);
            if (row != null) return row;
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ChatOrders(StoreId, Sid, Status, Ip) OUTPUT INSERTED.Id VALUES(@s, @sid, 'draft', @ip)",
                "@s", x.Store.Id, "@sid", x.Sid, "@ip", Cut(x.Ip, 60)))
                return new Row { Id = Convert.ToInt64(cmd.ExecuteScalar()) };
        }

        static void Save(SqlConnection c, Row r)
        {
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.ChatOrders SET Status=@st, Items=@it, Name=@n, Phone=@ph, Province=@pr, ProvinceCode=@pc, City=@ci, Address=@ad,
                Rates=@ra, ShipTitle=@sht, ShipPrice=@shp, Payment=@pay, Currency=@cur, Subtotal=@sub, Shipping=@shi, Discounts=@dis, Tax=@tax, Total=@tot,
                DraftGid=@dg, OrderName=@on, InvoiceUrl=@iu, Note=@no, CodeHash=@ch, CodeExpires=@ce, CodeTries=@ct, CodeSends=@cs, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id",
                "@st", r.Status, "@it", r.Items, "@n", Cut(r.Name, 120), "@ph", r.Phone, "@pr", Cut(r.Province, 60), "@pc", r.ProvinceCode, "@ci", Cut(r.City, 80),
                "@ad", Cut(r.Address, 300), "@ra", r.Rates, "@sht", Cut(r.ShipTitle, 150), "@shp", r.ShipPrice, "@pay", r.Payment, "@cur", r.Currency,
                "@sub", r.Subtotal, "@shi", r.Shipping, "@dis", r.Discounts, "@tax", r.Tax, "@tot", r.Total, "@dg", r.DraftGid, "@on", r.OrderName, "@iu", r.InvoiceUrl,
                "@no", Cut(r.Note, 300), "@ch", r.CodeHash, "@ce", r.CodeExpires, "@ct", r.CodeTries, "@cs", r.CodeSends, "@id", r.Id))
                cmd.ExecuteNonQuery();
        }

        static List<Item> ItemsOf(Row r) { try { return string.IsNullOrEmpty(r.Items) ? new List<Item>() : JsonSerializer.Deserialize<List<Item>>(r.Items); } catch { return new List<Item>(); } }
        static List<Rate> RatesOf(Row r) { try { return string.IsNullOrEmpty(r.Rates) ? new List<Rate>() : JsonSerializer.Deserialize<List<Rate>>(r.Rates); } catch { return new List<Rate>(); } }
        // anything changed before the confirmation: the old price/summary is no longer valid
        static void Unquote(Row r) { r.Total = null; r.Subtotal = null; r.Shipping = null; r.Discounts = null; r.Tax = null; if (r.Status == "code_sent") { r.Status = "draft"; r.CodeHash = null; } }

        // ---------- tools for Claude ----------
        public static List<Tool> Tools(OrderCtx x)
        {
            Func<object, JsonElement> J = o => JsonSerializer.SerializeToElement(o);
            var pay = Pays(x.Store);
            return new List<Tool>
            {
                new Tool { Name = "shop_find_product",
                    Description = "يدوّر على منتج في متجر Shopify باسمه أو بلينكه، ويرجّع اختياراته (زي المقاس واللون) بالترتيب، وكل نسخة (variant) برقمها وسعرها ومتاحة ولا لأ والكمية. استخدمه أول ما الزبون يقول إنه عايز يطلب منتج.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> {
                        ["query"] = J(new { type = "string", description = "اسم المنتج زي ما الزبون كتبه، أو لينك المنتج كامل." }) }, Required = new List<string> { "query" } } },
                new Tool { Name = "shop_set_cart",
                    Description = "يحط المنتجات اللي الزبون اختارها في طلبه (بيستبدل اللي قبلها). بعد ما الزبون يختار كل اختيارات المنتج والكمية. بيتأكد من السعر والمخزون من Shopify.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> {
                        ["items"] = J(new { type = "array", description = "كل منتج: رقم النسخة (variant_id) من shop_find_product والكمية.",
                            items = new { type = "object", properties = new { variant_id = new { type = "string" }, quantity = new { type = "integer", minimum = 1, maximum = 50 } }, required = new[] { "variant_id", "quantity" } } }) },
                        Required = new List<string> { "items" } } },
                new Tool { Name = "shop_set_customer",
                    Description = "يحفظ بيانات التوصيل اللي الزبون قالها، ويرجّع طرق الشحن المتاحة من Shopify. ابعت بس اللي الزبون قاله فعلًا.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> {
                        ["name"] = J(new { type = "string", description = "الاسم بالكامل." }),
                        ["phone"] = J(new { type = "string", description = "رقم الموبايل زي ما الزبون كتبه." }),
                        ["governorate"] = J(new { type = "string", description = "المحافظة." }),
                        ["city"] = J(new { type = "string", description = "المدينة أو المنطقة." }),
                        ["address"] = J(new { type = "string", description = "العنوان بالتفصيل: الشارع ورقم العمارة والدور والشقة وعلامة مميزة." }) } } },
                new Tool { Name = "shop_quote",
                    Description = "يختار طريقة الشحن وطريقة الدفع، ويحسب من Shopify الإجمالي بالشحن والخصومات. لازم تعرض الملخص اللي بيرجع للزبون بالظبط وتسأله يأكد.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> {
                        ["shipping_option"] = J(new { type = "integer", description = "رقم طريقة الشحن من القايمة اللي رجعت من shop_set_customer (1 = الأولى)." }),
                        ["payment"] = J(new { type = "string", @enum = pay, description = pay.Length == 1 ? "طريقة الدفع الوحيدة المتاحة." : "cod = كاش عند الاستلام، card = دفع أونلاين بلينك." }) },
                        Required = new List<string> { "shipping_option", "payment" } } },
                new Tool { Name = "shop_confirm",
                    Description = "الزبون وافق على الملخص صراحةً (قال تمام/أكد). بيبدأ تنفيذ الطلب: على الموقع بيبعت كود تأكيد على واتساب الزبون، وعلى واتساب بيعمل الطلب على طول.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement>() } },
                new Tool { Name = "shop_verify_code",
                    Description = "الزبون كتب كود التأكيد اللي وصله على واتساب. لو صح الطلب بيتعمل في Shopify.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> {
                        ["code"] = J(new { type = "string", description = "الكود زي ما الزبون كتبه." }) }, Required = new List<string> { "code" } } },
                new Tool { Name = "shop_cancel_order",
                    Description = "الزبون مش عايز يكمّل الطلب اللي لسه بيتجهز (قبل ما يتعمل). بيمسح الطلب المفتوح.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement>() } },
            };
        }

        static string[] Pays(StoreInfo s) { return s.CoPay == "card" ? new[] { "card" } : s.CoPay == "both" ? new[] { "cod", "card" } : new[] { "cod" }; }

        public static async Task<string> Run(string tool, IReadOnlyDictionary<string, JsonElement> input, OrderCtx x)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, x.Store.Id);
                    if (s == null || !Enabled(c, s)) return "الطلب من الشات مش متاح دلوقتي. اعتذر للزبون وابعتله لينك المنتج يطلب من الموقع.";
                    x.Store = s;
                    var creds = ShopifyAdmin.For(s);
                    switch (tool)
                    {
                        case "shop_find_product": return await Find(creds, s, S(input, "query"));
                        case "shop_set_cart": return await SetCart(c, creds, x, input);
                        case "shop_set_customer": return await SetCustomer(c, creds, x, input);
                        case "shop_quote": return await Quote(c, creds, x, I(input, "shipping_option"), S(input, "payment"));
                        case "shop_confirm": return await Confirm(c, creds, x);
                        case "shop_verify_code": return await Verify(c, creds, x, S(input, "code"));
                        case "shop_cancel_order": return CancelOpen(c, x);
                    }
                    return "أداة غير معروفة.";
                }
            }
            catch (ShopifyException ex) { Log.Error(ex); return "Shopify مش بترد دلوقتي (" + ex.Message + "). اعتذر للزبون وقوله يجرّب كمان شوية أو يطلب من الموقع."; }
            catch (Exception ex) { Log.Error(ex); return "حصلت مشكلة في تجهيز الطلب. اعتذر للزبون وقوله يجرّب كمان شوية أو يطلب من الموقع."; }
        }

        // ---------- 1. the product ----------
        static async Task<string> Find(ShopifyAdmin.Creds creds, StoreInfo s, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length < 2) return "محتاج اسم المنتج أو لينكه.";
            bool inv = ShopifyAdmin.Has(s.ShopifyScopes, "read_inventory");
            string fields = "id title handle status onlineStoreUrl options { name values } variants(first:60) { nodes { id title price availableForSale inventoryPolicy" +
                            (inv ? " inventoryQuantity" : "") + " selectedOptions { name value } } }" + (inv ? " tracksInventory" : "");
            var nodes = new List<JsonElement>();
            var m = Regex.Match(query, @"/products/([^/?#\s]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var g = await ShopifyAdmin.Query(creds, "query($h:String!){ productByIdentifier(identifier:{handle:$h}) { " + fields + " } }", new { h = Uri.UnescapeDataString(m.Groups[1].Value) }, CancellationToken.None);
                JsonElement p; if (g.Data.TryGetProperty("productByIdentifier", out p) && p.ValueKind == JsonValueKind.Object) nodes.Add(p);
            }
            if (nodes.Count == 0)
            {
                string q = Regex.Replace(m.Success ? m.Groups[1].Value.Replace('-', ' ') : query, "[\"\\\\():*]", " ").Trim();
                var g = await ShopifyAdmin.Query(creds, "query($q:String!){ products(first:5, query:$q) { nodes { " + fields + " } } }", new { q = "status:active " + q }, CancellationToken.None);
                foreach (var p in g.Data.GetProperty("products").GetProperty("nodes").EnumerateArray()) nodes.Add(p);
            }
            nodes = nodes.Where(p => (Str(p, "status") ?? "ACTIVE") == "ACTIVE").ToList();
            if (nodes.Count == 0) return "مالقيتش منتج بالاسم ده في المتجر. اسأل الزبون عن اسم تاني أو اطلب منه لينك المنتج.";

            var sb = new StringBuilder("المنتجات اللي لقيتها (الأسعار والكميات من Shopify دلوقتي):");
            foreach (var p in nodes.Take(5))
            {
                sb.Append("\n\n## ").Append(Str(p, "title"));
                var opts = new List<string>();
                JsonElement os;
                if (p.TryGetProperty("options", out os) && os.ValueKind == JsonValueKind.Array)
                    foreach (var o in os.EnumerateArray())
                    {
                        string on = Str(o, "name"); var vals = Arr(o, "values");
                        if (on == "Title" && vals.Count == 1 && vals[0] == "Default Title") continue;
                        opts.Add(on + ": " + string.Join(" / ", vals));
                    }
                sb.Append(opts.Count == 0 ? "\nالاختيارات: مالوش اختيارات (نسخة واحدة)." : "\nالاختيارات بالترتيب (اسأل عنهم بالترتيب ده):\n- " + string.Join("\n- ", opts));
                bool tracks = inv && p.TryGetProperty("tracksInventory", out var tr) && tr.ValueKind == JsonValueKind.True;
                sb.Append("\nالنسخ:");
                foreach (var v in p.GetProperty("variants").GetProperty("nodes").EnumerateArray())
                {
                    string vt = Str(v, "title"); if (vt == "Default Title") vt = "النسخة الوحيدة";
                    bool avail = v.TryGetProperty("availableForSale", out var a) && a.ValueKind == JsonValueKind.True;
                    int? qty = tracks && v.TryGetProperty("inventoryQuantity", out var qv) && qv.ValueKind == JsonValueKind.Number ? qv.GetInt32() : (int?)null;
                    bool oversell = Str(v, "inventoryPolicy") == "CONTINUE";
                    sb.Append("\n- variant_id ").Append(ShortId(Str(v, "id"))).Append(": ").Append(vt).Append(" · السعر ").Append(Str(v, "price"))
                      .Append(" · ").Append(!avail ? "خلصان" : qty.HasValue && !oversell ? "متاح (فاضل " + Math.Max(0, qty.Value) + ")" : "متاح");
                }
                string url = Str(p, "onlineStoreUrl"); if (!string.IsNullOrEmpty(url)) sb.Append("\nاللينك: ").Append(url);
            }
            sb.Append("\n\nاسأل الزبون عن كل اختيار لوحده بالترتيب، وبعدين الكمية، وماتختارش نسخة خلصانة. لو أكتر من منتج طلع، اتأكد الأول من المنتج المقصود.");
            return sb.ToString();
        }

        // ---------- 2. the cart ----------
        static async Task<string> SetCart(SqlConnection c, ShopifyAdmin.Creds creds, OrderCtx x, IReadOnlyDictionary<string, JsonElement> input)
        {
            JsonElement arr;
            if (!input.TryGetValue("items", out arr) || arr.ValueKind != JsonValueKind.Array) return "ابعت items.";
            var want = new List<Tuple<string, int>>();
            foreach (var it in arr.EnumerateArray())
            {
                string vid = it.TryGetProperty("variant_id", out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()) : null;
                int q = it.TryGetProperty("quantity", out var qq) && qq.ValueKind == JsonValueKind.Number ? qq.GetInt32() : 1;
                string gid = Gid(vid);
                if (gid == null) return "رقم النسخة \"" + vid + "\" مش صحيح. استخدم variant_id زي ما رجع من shop_find_product.";
                if (q < 1 || q > 50) return "الكمية لازم تكون من 1 لـ 50.";
                want.Add(Tuple.Create(gid, q));
            }
            if (want.Count == 0) return "مفيش منتجات.";
            if (want.Count > 15) return "أكتر من 15 منتج في طلب واحد كتير. قسّمه.";
            var items = await Check(creds, x.Store, want);
            if (items.Item2 != null) return items.Item2;
            var row = Ensure(c, x);
            if (row.Status != "draft" && row.Status != "code_sent") return "الطلب ده اتنفذ خلاص.";
            row.Items = JsonSerializer.Serialize(items.Item1);
            Unquote(row);
            Save(c, row);
            var sb = new StringBuilder("✅ اتحطوا في الطلب:");
            foreach (var i in items.Item1) sb.Append("\n- ").Append(i.T).Append(i.VT != null ? " (" + i.VT + ")" : "").Append(" × ").Append(i.Q).Append(" · ").Append(Money(i.P * i.Q, null)).Append(" للكمية");
            sb.Append("\nالمطلوب بعد كده: ").Append(Missing(row, false));
            return sb.ToString();
        }

        // prices and stock right now, from Shopify
        static async Task<Tuple<List<Item>, string>> Check(ShopifyAdmin.Creds creds, StoreInfo s, List<Tuple<string, int>> want)
        {
            bool inv = ShopifyAdmin.Has(s.ShopifyScopes, "read_inventory");
            var g = await ShopifyAdmin.Query(creds, "query($ids:[ID!]!){ nodes(ids:$ids) { ... on ProductVariant { id title price availableForSale inventoryPolicy" + (inv ? " inventoryQuantity" : "") +
                " product { title status" + (inv ? " tracksInventory" : "") + " } } } }", new { ids = want.Select(w => w.Item1).Distinct().ToArray() }, CancellationToken.None);
            var byId = new Dictionary<string, JsonElement>();
            foreach (var n in g.Data.GetProperty("nodes").EnumerateArray()) if (n.ValueKind == JsonValueKind.Object && Str(n, "id") != null) byId[Str(n, "id")] = n;
            var list = new List<Item>();
            foreach (var w in want)
            {
                JsonElement v;
                if (!byId.TryGetValue(w.Item1, out v)) return Tuple.Create<List<Item>, string>(null, "النسخة " + ShortId(w.Item1) + " مش موجودة. دوّر على المنتج تاني بـ shop_find_product.");
                var p = v.GetProperty("product");
                string title = Str(p, "title"), vt = Str(v, "title");
                if (vt == "Default Title") vt = null;
                if (Str(p, "status") != "ACTIVE" || !(v.TryGetProperty("availableForSale", out var a) && a.ValueKind == JsonValueKind.True))
                    return Tuple.Create<List<Item>, string>(null, title + (vt != null ? " (" + vt + ")" : "") + " خلصان دلوقتي. قول للزبون واقترح نسخة تانية متاحة.");
                bool tracks = inv && p.TryGetProperty("tracksInventory", out var tr) && tr.ValueKind == JsonValueKind.True;
                if (tracks && Str(v, "inventoryPolicy") != "CONTINUE" && v.TryGetProperty("inventoryQuantity", out var qv) && qv.ValueKind == JsonValueKind.Number && qv.GetInt32() < w.Item2)
                    return Tuple.Create<List<Item>, string>(null, "المتاح من " + title + (vt != null ? " (" + vt + ")" : "") + " " + Math.Max(0, qv.GetInt32()) + " بس. قول للزبون واسأله يقلل الكمية.");
                list.Add(new Item { V = w.Item1, T = title, VT = vt, Q = w.Item2, P = Dec(Str(v, "price")) });
            }
            return Tuple.Create(list, (string)null);
        }

        // ---------- 3. delivery details + shipping options ----------
        static async Task<string> SetCustomer(SqlConnection c, ShopifyAdmin.Creds creds, OrderCtx x, IReadOnlyDictionary<string, JsonElement> input)
        {
            var row = Ensure(c, x);
            string name = S(input, "name"), phone = S(input, "phone"), gov = S(input, "governorate"), city = S(input, "city"), addr = S(input, "address");
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(name)) row.Name = name.Trim();
            if (!string.IsNullOrWhiteSpace(phone))
            {
                var p = WaChat.Phone(phone);
                if (p == null) notes.Add("رقم الموبايل \"" + phone + "\" مش صحيح، اطلبه تاني.");
                else if (x.VerifiedPhone == null && !Web.Allow("co-phone:" + x.Store.Id + ":" + p, 6, TimeSpan.FromHours(6))) notes.Add("الرقم ده اتستخدم في طلبات كتير. اطلب رقم تاني أو انصحه يطلب من الموقع.");
                else row.Phone = p;
            }
            else if (row.Phone == null && x.VerifiedPhone != null) row.Phone = x.VerifiedPhone;
            if (!string.IsNullOrWhiteSpace(gov))
            {
                var code = Province(gov);
                if (code == null) notes.Add("مش عارف المحافظة \"" + gov + "\". اسأله عن المحافظة تاني (زي القاهرة، الجيزة، الإسكندرية...).");
                else { row.ProvinceCode = code; row.Province = gov.Trim(); }
            }
            if (!string.IsNullOrWhiteSpace(city)) row.City = city.Trim();
            if (!string.IsNullOrWhiteSpace(addr))
            {
                if (addr.Trim().Length < 8) notes.Add("العنوان قصير أوي، اطلب تفاصيل أكتر (الشارع، رقم العمارة، الدور، الشقة).");
                else row.Address = addr.Trim();
            }
            Unquote(row); row.Rates = null; row.ShipTitle = null; row.ShipPrice = null;

            string missing = Missing(row, false);
            var sb = new StringBuilder();
            if (notes.Count > 0) sb.Append(string.Join("\n", notes)).Append("\n");
            if (missing != "مفيش، كمّل بـ shop_quote")
            {
                Save(c, row);
                sb.Append("اتحفظ اللي اتقال. لسه ناقص: ").Append(missing).Append(". اسأل عنهم واحدة واحدة.");
                return sb.ToString();
            }
            // everything needed: ask Shopify for the shipping options to this address
            var calc = await Calculate(creds, row, null);
            if (calc.Item2 != null) { Save(c, row); return calc.Item2; }
            var rates = new List<Rate>();
            JsonElement ar;
            if (calc.Item1.TryGetProperty("availableShippingRates", out ar) && ar.ValueKind == JsonValueKind.Array)
                foreach (var r in ar.EnumerateArray()) rates.Add(new Rate { T = Str(r, "title"), P = Dec(Str(r.GetProperty("price"), "amount")) });
            row.Currency = Str(calc.Item1, "currencyCode");
            if (rates.Count == 0) rates.Add(new Rate { T = "توصيل", P = 0 });   // store without shipping rates for this zone: delivery cost agreed later
            row.Rates = JsonSerializer.Serialize(rates);
            Save(c, row);
            sb.Append("✅ بيانات التوصيل اتحفظت.\nطرق الشحن المتاحة (من Shopify):");
            for (int i = 0; i < rates.Count; i++) sb.Append("\n").Append(i + 1).Append(") ").Append(rates[i].T).Append(" · ").Append(rates[i].P == 0 ? "مجانًا" : Money(rates[i].P, row.Currency));
            if (rates.Count == 1 && rates[0].T == "توصيل") sb.Append(" (المتجر ماحددش سعر شحن للمنطقة دي في Shopify، المتجر هيأكده مع الزبون)");
            var pays = Pays(x.Store);
            sb.Append("\nطرق الدفع: ").Append(string.Join(" أو ", pays.Select(p => p == "cod" ? "كاش عند الاستلام (cod)" : "أونلاين بلينك دفع (card)")));
            sb.Append("\nاسأل الزبون يختار طريقة الشحن").Append(pays.Length > 1 ? " والدفع" : "").Append("، وبعدين shop_quote.");
            return sb.ToString();
        }

        // ---------- 4. the total, from Shopify ----------
        static async Task<string> Quote(SqlConnection c, ShopifyAdmin.Creds creds, OrderCtx x, int option, string payment)
        {
            var row = Current(c, x);
            if (row == null) return "مفيش طلب مفتوح. ابدأ بـ shop_set_cart.";
            string missing = Missing(row, false);
            if (missing != "مفيش، كمّل بـ shop_quote") return "لسه ناقص: " + missing + ".";
            var rates = RatesOf(row);
            if (rates.Count == 0) return "مفيش طرق شحن محفوظة. ابعت بيانات التوصيل تاني بـ shop_set_customer.";
            if (option < 1 || option > rates.Count) return "اختار رقم طريقة شحن من 1 لـ " + rates.Count + ".";
            var pays = Pays(x.Store);
            if (!pays.Contains(payment ?? "")) payment = pays[0];
            // stock may have changed while chatting
            var items = ItemsOf(row);
            var fresh = await Check(creds, x.Store, items.Select(i => Tuple.Create(i.V, i.Q)).ToList());
            if (fresh.Item2 != null) return fresh.Item2;
            row.Items = JsonSerializer.Serialize(fresh.Item1);
            var rate = rates[option - 1];
            row.ShipTitle = rate.T; row.ShipPrice = rate.P; row.Payment = payment;
            var calc = await Calculate(creds, row, rate);
            if (calc.Item2 != null) return calc.Item2;
            var d = calc.Item1;
            row.Currency = Str(d, "currencyCode") ?? row.Currency;
            row.Subtotal = Shop(d, "subtotalPriceSet"); row.Shipping = Shop(d, "totalShippingPriceSet"); row.Discounts = Shop(d, "totalDiscountsSet");
            row.Tax = Shop(d, "totalTaxSet"); row.Total = Shop(d, "totalPriceSet");
            if (row.Status == "code_sent") { row.Status = "draft"; row.CodeHash = null; }
            Save(c, row);
            return "اعرض الملخص ده للزبون زي ما هو، واسأله يأكد (مثلًا \"أأكد الطلب؟\"). ماتغيّرش أي رقم:\n\n" + Summary(row, fresh.Item1) +
                   "\n\nلما يوافق صراحةً استخدم shop_confirm. لو عايز يغيّر حاجة، غيّرها بالأداة المناسبة واحسب تاني.";
        }

        static string Summary(Row r, List<Item> items)
        {
            var sb = new StringBuilder("🧾 *ملخص طلبك*");
            foreach (var i in items) sb.Append("\n• ").Append(i.T).Append(i.VT != null ? " (" + i.VT + ")" : "").Append(" × ").Append(i.Q).Append(" — ").Append(Money(i.P * i.Q, r.Currency));
            sb.Append("\n\n👤 ").Append(r.Name).Append(" · 📱 +").Append(r.Phone);
            sb.Append("\n📍 ").Append(r.Address).Append("، ").Append(r.City).Append("، ").Append(r.Province);
            sb.Append("\n🚚 ").Append(r.ShipTitle).Append(" — ").Append(r.Shipping.GetValueOrDefault() == 0 ? "مجانًا" : Money(r.Shipping.Value, r.Currency));
            if (r.Discounts.GetValueOrDefault() > 0) sb.Append("\n🏷️ خصم: −").Append(Money(r.Discounts.Value, r.Currency));
            if (r.Tax.GetValueOrDefault() > 0) sb.Append("\nضريبة: ").Append(Money(r.Tax.Value, r.Currency));
            sb.Append("\n💰 *الإجمالي: ").Append(Money(r.Total.GetValueOrDefault(), r.Currency)).Append("*");
            sb.Append("\n💳 الدفع: ").Append(r.Payment == "card" ? "أونلاين (هيوصلك لينك دفع آمن من المتجر)" : "كاش عند الاستلام");
            return sb.ToString();
        }

        // ---------- 5/6. confirm -> verify the phone -> place ----------
        static async Task<string> Confirm(SqlConnection c, ShopifyAdmin.Creds creds, OrderCtx x)
        {
            var row = Current(c, x);
            if (row == null || row.Total == null) return "لسه مفيش ملخص متحسب. استخدم shop_quote الأول واعرض الملخص على الزبون.";
            if (!Web.Allow("co-place:" + x.Store.Id + ":" + x.Sid, 3, TimeSpan.FromDays(1)) || !Web.Allow("co-place-ip:" + x.Store.Id + ":" + x.Ip, 6, TimeSpan.FromDays(1)))
                return "اتعملت طلبات كتير من المحادثة دي النهارده. انصح الزبون يكلّم المتجر مباشرة.";

            // inside WhatsApp the chat comes from the number itself
            if (x.VerifiedPhone != null && Tail(x.VerifiedPhone) == Tail(row.Phone)) return await Place(c, creds, x, row, true);

            var wa = WaReady(c, x.Store);
            if (wa == null) return await Place(c, creds, x, row, false);   // no WhatsApp linked: the order goes through, marked as unverified for the store

            if (row.CodeSends >= 3) return "اتبعت كود كذا مرة. اطلب من الزبون يتأكد من رقمه أو يكلّم المتجر.";
            if (!Web.Allow("co-code:" + row.Phone, 4, TimeSpan.FromHours(1))) return "اتبعت أكواد كتير للرقم ده. اطلب منه يستنى شوية.";
            var rnd = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
            string code = (1000 + BitConverter.ToUInt32(rnd, 0) % 9000).ToString(CultureInfo.InvariantCulture);
            row.CodeHash = Hash(row.Id, code); row.CodeExpires = DateTime.UtcNow.AddMinutes(CodeMinutes); row.CodeTries = 0; row.CodeSends++; row.Status = "code_sent";
            Save(c, row);
            string text = "كود تأكيد طلبك من *" + x.Store.Name + "* هو: *" + code + "*\nاكتبه في الشات عشان نأكد الطلب. الكود صالح " + CodeMinutes + " دقايق.\nلو مش انت اللي طلبت، تجاهل الرسالة دي.";
            try { await Wapilot.Send(wa, x.Store.WaInstance, row.Phone + "@c.us", text, "sb-co-" + row.Id + "-" + row.CodeSends); }
            catch (Exception ex) { Log.Error(ex); row.Status = "draft"; row.CodeHash = null; Save(c, row); return "مقدرناش نبعت كود التأكيد على واتساب الرقم +" + row.Phone + ". اتأكد من الزبون إن الرقم عليه واتساب، أو اطلب رقم تاني."; }
            return "اتبعت كود من 4 أرقام على واتساب الزبون (+" + row.Phone + "). قوله يكتب الكود هنا عشان نأكد الطلب، واستخدم shop_verify_code بالكود اللي يكتبه.";
        }

        static async Task<string> Verify(SqlConnection c, ShopifyAdmin.Creds creds, OrderCtx x, string code)
        {
            var row = Current(c, x);
            if (row == null || row.Status != "code_sent" || row.CodeHash == null) return "مفيش كود مستني. لو الزبون أكد الملخص استخدم shop_confirm.";
            if (row.CodeExpires < DateTime.UtcNow) return "الكود خلص وقته. اسأل الزبون لو يحب نبعتله كود جديد (shop_confirm).";
            if (row.CodeTries >= CodeTries) return "الكود اتكتب غلط كذا مرة. لازم كود جديد (shop_confirm).";
            code = Regex.Replace(ShopifyAdmin.ToLatinDigits(code ?? ""), @"\D", "");
            if (!Secrets.Same(Encoding.UTF8.GetBytes(Hash(row.Id, code)), Encoding.UTF8.GetBytes(row.CodeHash)))
            {
                row.CodeTries++; Save(c, row);
                return "الكود غلط (فاضل " + (CodeTries - row.CodeTries) + " محاولات). اطلب منه يتأكد من الرسالة اللي وصلته على واتساب.";
            }
            return await Place(c, creds, x, row, true);
        }

        static async Task<string> Place(SqlConnection c, ShopifyAdmin.Creds creds, OrderCtx x, Row row, bool verified)
        {
            // claim it so a double "yes" can't create two orders
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ChatOrders SET Status='placing', UpdatedAt=SYSUTCDATETIME() WHERE Id=@id AND Status IN ('draft','code_sent')", "@id", row.Id))
                if (cmd.ExecuteNonQuery() != 1) return "الطلب ده بيتنفذ بالفعل.";
            row.Status = "placing";
            var items = ItemsOf(row);
            var fresh = await Check(creds, x.Store, items.Select(i => Tuple.Create(i.V, i.Q)).ToList());
            if (fresh.Item2 != null) { row.Status = "draft"; row.Total = null; Save(c, row); return fresh.Item2 + " (الطلب ماتعملش)"; }

            var input = DraftInput(row, new Rate { T = row.ShipTitle, P = row.ShipPrice.GetValueOrDefault() });
            input["note"] = "طلب من شات Sellybot" + (verified ? "" : " · ⚠️ رقم الموبايل ماتأكدش (واتساب المتجر مش مربوط)");
            input["tags"] = verified ? new[] { TagChat } : new[] { TagChat, "Sellybot: رقم غير مؤكد" };
            var g = await ShopifyAdmin.Query(creds, "mutation($input: DraftOrderInput!){ draftOrderCreate(input:$input){ draftOrder { id name invoiceUrl totalPriceSet { shopMoney { amount currencyCode } } } userErrors { field message } } }",
                new { input }, CancellationToken.None);
            var res = g.Data.GetProperty("draftOrderCreate");
            string err = Errors(res);
            JsonElement draft;
            if (err != null || !res.TryGetProperty("draftOrder", out draft) || draft.ValueKind != JsonValueKind.Object)
            {
                row.Status = "failed"; row.Note = err ?? "draftOrderCreate"; Save(c, row);
                return "Shopify رفضت الطلب (" + (err ?? "خطأ") + "). اعتذر للزبون وانصحه يطلب من لينك المنتج أو يكلّم المتجر.";
            }
            row.DraftGid = Str(draft, "id"); row.InvoiceUrl = Str(draft, "invoiceUrl"); row.OrderName = Str(draft, "name");
            row.Total = Shop(draft, "totalPriceSet") ?? row.Total;

            if (row.Payment == "card")
            {
                row.Status = "awaiting_payment"; Save(c, row);
                return "✅ الطلب اتجهز. ابعت للزبون لينك الدفع ده بالظبط عشان يكمّل الدفع، والطلب هيتأكد أول ما يدفع:\n" + row.InvoiceUrl +
                       "\nالإجمالي " + Money(row.Total.GetValueOrDefault(), row.Currency) + ". ماتقولش إن الطلب اتأكد قبل الدفع.";
            }
            // cash on delivery: complete the draft with the payment still pending (paid by the customer on delivery)
            var done = await ShopifyAdmin.Query(creds, "mutation($id: ID!){ draftOrderComplete(id:$id, paymentPending:true){ draftOrder { id order { id name } } userErrors { field message } } }",
                new { id = row.DraftGid }, CancellationToken.None);
            var dres = done.Data.GetProperty("draftOrderComplete");
            string derr = Errors(dres);
            JsonElement order;
            if (derr == null && dres.TryGetProperty("draftOrder", out var dd) && dd.ValueKind == JsonValueKind.Object && dd.TryGetProperty("order", out order) && order.ValueKind == JsonValueKind.Object)
            {
                row.OrderName = Str(order, "name") ?? row.OrderName; row.Status = "placed"; Save(c, row);
                return "✅ الطلب اتعمل في المتجر برقم " + row.OrderName + ". قول للزبون إن طلبه اتأكد، والإجمالي " + Money(row.Total.GetValueOrDefault(), row.Currency) +
                       " كاش عند الاستلام، وإن المتجر هيتواصل معاه لو احتاج حاجة. اشكره.";
            }
            // the draft exists in Shopify (Orders > Drafts): the store completes it by hand
            row.Status = "placed"; row.Note = "اتعمل كطلب مبدئي (Draft) بس، كمّله من Shopify: " + (derr ?? ""); Save(c, row);
            return "✅ الطلب اتسجل عند المتجر برقم " + row.OrderName + " وهيتأكد معاه قريب. قول للزبون إن طلبه وصل للمتجر، والإجمالي " + Money(row.Total.GetValueOrDefault(), row.Currency) + " كاش عند الاستلام.";
        }

        static string CancelOpen(SqlConnection c, OrderCtx x)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ChatOrders SET Status='cancelled', UpdatedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Sid=@sid AND Status IN ('draft','code_sent')", "@s", x.Store.Id, "@sid", x.Sid))
                return cmd.ExecuteNonQuery() > 0 ? "اتلغى الطلب المفتوح. قول للزبون إنه يقدر يطلب تاني في أي وقت." : "مفيش طلب مفتوح يتلغي. (الطلبات اللي اتعملت بتتلغي من المتجر)";
        }

        // ---------- Shopify draft order input + calculate ----------
        static Dictionary<string, object> DraftInput(Row r, Rate rate)
        {
            string first = r.Name ?? "", last = "";
            int sp = first.Trim().IndexOf(' ');
            if (sp > 0) { last = first.Trim().Substring(sp + 1).Trim(); first = first.Trim().Substring(0, sp); }
            var addr = new Dictionary<string, object> {
                { "firstName", first }, { "lastName", last.Length > 0 ? last : "-" }, { "address1", r.Address }, { "city", r.City ?? r.Province },
                { "provinceCode", r.ProvinceCode }, { "countryCode", "EG" }, { "phone", "+" + r.Phone } };
            var d = new Dictionary<string, object> {
                { "lineItems", ItemsOf(r).Select(i => new Dictionary<string, object> { { "variantId", i.V }, { "quantity", i.Q } }).ToArray() },
                { "shippingAddress", addr }, { "billingAddress", addr }, { "phone", "+" + r.Phone }, { "acceptAutomaticDiscounts", true } };
            if (rate != null)
                d["shippingLine"] = new Dictionary<string, object> { { "title", rate.T }, { "priceWithCurrency", new Dictionary<string, object> {
                    { "amount", rate.P.ToString("0.##", CultureInfo.InvariantCulture) }, { "currencyCode", string.IsNullOrEmpty(r.Currency) ? "EGP" : r.Currency } } } };
            return d;
        }

        static async Task<Tuple<JsonElement, string>> Calculate(ShopifyAdmin.Creds creds, Row r, Rate rate)
        {
            var g = await ShopifyAdmin.Query(creds, "mutation($input: DraftOrderInput!){ draftOrderCalculate(input:$input){ calculatedDraftOrder { currencyCode " +
                "subtotalPriceSet { shopMoney { amount } } totalShippingPriceSet { shopMoney { amount } } totalDiscountsSet { shopMoney { amount } } totalTaxSet { shopMoney { amount } } " +
                "totalPriceSet { shopMoney { amount } } availableShippingRates { title price { amount currencyCode } } } userErrors { field message } } }",
                new { input = DraftInput(r, rate) }, CancellationToken.None);
            var res = g.Data.GetProperty("draftOrderCalculate");
            string err = Errors(res);
            JsonElement cd;
            if (err != null || !res.TryGetProperty("calculatedDraftOrder", out cd) || cd.ValueKind != JsonValueKind.Object)
                return Tuple.Create(default(JsonElement), "Shopify ماقدرتش تحسب الطلب (" + (err ?? "خطأ") + "). اتأكد من بيانات التوصيل مع الزبون، ولو المشكلة فضلت انصحه يطلب من الموقع.");
            return Tuple.Create(cd.Clone(), (string)null);
        }

        // ---------- what the assistant should know each turn ----------
        public static string StateNote(OrderCtx x)
        {
            if (x == null) return "";
            try
            {
                using (var c = Db.Open())
                {
                    var r = Current(c, x);
                    var sb = new StringBuilder();
                    if (r != null)
                    {
                        var items = ItemsOf(r);
                        sb.Append("حالة الطلب المفتوح في المحادثة دي (من السيستم، مش من الزبون):");
                        sb.Append("\n- المنتجات: ").Append(items.Count == 0 ? "لسه مفيش" : string.Join("، ", items.Select(i => i.T + (i.VT != null ? " (" + i.VT + ")" : "") + " × " + i.Q)));
                        sb.Append("\n- الاسم: ").Append(r.Name ?? "—").Append(" · الموبايل: ").Append(r.Phone != null ? "+" + r.Phone : "—")
                          .Append(" · المحافظة: ").Append(r.Province ?? "—").Append(" · المدينة: ").Append(r.City ?? "—").Append(" · العنوان: ").Append(r.Address ?? "—");
                        if (r.Total != null) sb.Append("\n- اتحسب الإجمالي ").Append(Money(r.Total.Value, r.Currency)).Append(" (").Append(r.ShipTitle).Append("، ").Append(r.Payment == "card" ? "دفع أونلاين" : "كاش").Append(") ومستني موافقة الزبون");
                        if (r.Status == "code_sent") sb.Append("\n- اتبعت كود تأكيد على واتساب الزبون ومستنيه يكتبه (shop_verify_code).");
                        sb.Append("\n- الناقص: ").Append(Missing(r, r.Total != null));
                    }
                    using (var cmd = Db.Cmd(c, @"SELECT TOP 1 OrderName, Status, InvoiceUrl, Total, Currency FROM dbo.ChatOrders WHERE StoreId=@s AND Sid=@sid AND Status IN ('placed','awaiting_payment')
                        AND UpdatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", x.Store.Id, "@sid", x.Sid, "@d", KeepDays))
                    using (var rd = cmd.ExecuteReader())
                        if (rd.Read())
                            sb.Append(sb.Length > 0 ? "\n" : "").Append("- الزبون عمل قبل كده من المحادثة دي طلب ").Append(rd["OrderName"] as string)
                              .Append((string)rd["Status"] == "awaiting_payment" ? " (مستني الدفع على اللينك: " + (rd["InvoiceUrl"] as string) + ")" : " (اتأكد)").Append(".");
                    return sb.Length == 0 ? "" : sb.ToString();
                }
            }
            catch (Exception ex) { Log.Error(ex); return ""; }
        }

        static string Missing(Row r, bool quoted)
        {
            var m = new List<string>();
            if (ItemsOf(r).Count == 0) m.Add("المنتجات (shop_set_cart)");
            if (string.IsNullOrWhiteSpace(r.Name)) m.Add("الاسم");
            if (r.Phone == null) m.Add("رقم الموبايل");
            if (r.ProvinceCode == null) m.Add("المحافظة");
            if (string.IsNullOrWhiteSpace(r.City)) m.Add("المدينة/المنطقة");
            if (string.IsNullOrWhiteSpace(r.Address)) m.Add("العنوان بالتفصيل");
            if (m.Count > 0) return string.Join("، ", m);
            return quoted ? "موافقة الزبون على الملخص" : "مفيش، كمّل بـ shop_quote";
        }

        // ---------- the prompt part (cached with the store's prompt) ----------
        public static string PromptPart(StoreInfo s)
        {
            var pays = Pays(s);
            return "\n\nالطلب من الشات: الزبون يقدر يطلب من غير ما يفتح الموقع. لو قال إنه عايز يطلب أو يشتري منتج (أو بعت اسمه أو لينكه وقال عايزه):\n" +
                "1. دوّر عليه بـ shop_find_product.\n" +
                "2. اسأل عن اختيارات المنتج واحد واحد بالترتيب اللي رجع (زي المقاس وبعده اللون)، وبعدين الكمية. ماتسألش عن اختيار المنتج مالوش. ماتقترحش نسخة خلصانة.\n" +
                "3. حط اختياره بـ shop_set_cart.\n" +
                "4. اسأل واحدة واحدة: الاسم، رقم الموبايل، المحافظة، المدينة أو المنطقة، العنوان بالتفصيل. ابعت كل اللي اتقال بـ shop_set_customer (ممكن تبعته على مراحل).\n" +
                "5. اعرض طرق الشحن اللي رجعت" + (pays.Length > 1 ? " واسأله يدفع كاش عند الاستلام ولا أونلاين" : pays[0] == "card" ? " (الدفع أونلاين بلينك بس)" : " (الدفع كاش عند الاستلام)") + "، وبعدين shop_quote.\n" +
                "6. اعرض الملخص اللي رجع من shop_quote زي ما هو واسأله يأكد. ماتكتبش أسعار أو إجمالي من عندك أبدًا، كل الأرقام من الأدوات.\n" +
                "7. لما يقول صراحةً إنه موافق: shop_confirm. لو اتبعتله كود على واتساب، اطلب منه يكتبه وبعدين shop_verify_code.\n" +
                "- اسأل سؤال واحد في كل رسالة، وخلي الرسالة قصيرة. لو الزبون قال كذا معلومة مرة واحدة، خدهم كلهم.\n" +
                "- ماتقولش إن الطلب اتعمل أو اتأكد إلا لو الأداة قالت كده. لو أداة رجعت مشكلة، اشرحها للزبون ببساطة.\n" +
                "- لو عايز يغيّر حاجة قبل التأكيد، غيّرها بنفس الأداة واحسب تاني. لو مش عايز يكمّل: shop_cancel_order.\n" +
                "- الطلبات اللي اتعملت خلاص مش بتتعدل من الشات: انصحه يكلّم المتجر.";
        }

        // ---------- dashboard ----------
        public static List<Dictionary<string, object>> Recent(SqlConnection c, int storeId, int n = 15)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP (@n) Id, Status, OrderName, Name, Total, Currency, Payment, Note, Sid, UpdatedAt FROM dbo.ChatOrders
                WHERE StoreId=@s AND Status IN ('placed','awaiting_payment','failed') ORDER BY Id DESC", "@n", n, "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> {
                        { "order", r["OrderName"] as string ?? "" }, { "customer", r["Name"] as string ?? "" }, { "status", r["Status"] as string },
                        { "total", r["Total"] is decimal ? Money((decimal)r["Total"], r["Currency"] as string) : "" }, { "payment", r["Payment"] as string ?? "" },
                        { "channel", (r["Sid"] as string ?? "").StartsWith("wa:") ? "whatsapp" : "web" }, { "note", r["Note"] as string ?? "" },
                        { "at", DateTime.SpecifyKind((DateTime)r["UpdatedAt"], DateTimeKind.Utc).ToString("o") } });
            return list;
        }

        // ---------- helpers ----------
        static string WaReady(SqlConnection c, StoreInfo s)
        {
            if (string.IsNullOrEmpty(s.WaInstance) || string.IsNullOrEmpty(s.WaNumber) || !WaAccount.PlanAllows(c, s, out _)) return null;
            try { return WaAccount.Token(c, s); } catch { return null; }
        }

        static string Hash(long id, string code)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes("chat-order|" + (Web.Setting("AuthSecret") ?? ""))))
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(id.ToString(CultureInfo.InvariantCulture) + ":" + code)));
        }

        static string Gid(string v)
        {
            v = (v ?? "").Trim();
            if (Regex.IsMatch(v, @"^gid://shopify/ProductVariant/\d+$")) return v;
            v = ShopifyAdmin.ToLatinDigits(v);
            return Regex.IsMatch(v, @"^\d{1,20}$") ? "gid://shopify/ProductVariant/" + v : null;
        }
        static string ShortId(string gid) { var i = (gid ?? "").LastIndexOf('/'); return i >= 0 ? gid.Substring(i + 1) : gid; }
        static string Tail(string p) { p = p ?? ""; return p.Length > 9 ? p.Substring(p.Length - 9) : p; }

        static string Errors(JsonElement payload)
        {
            JsonElement ue;
            if (!payload.TryGetProperty("userErrors", out ue) || ue.ValueKind != JsonValueKind.Array) return null;
            var msgs = ue.EnumerateArray().Select(e => Str(e, "message")).Where(m => !string.IsNullOrEmpty(m)).ToList();
            return msgs.Count == 0 ? null : string.Join(" · ", msgs);
        }

        static decimal? Shop(JsonElement o, string setName)
        {
            JsonElement set, sm;
            if (o.TryGetProperty(setName, out set) && set.ValueKind == JsonValueKind.Object && set.TryGetProperty("shopMoney", out sm)) return Dec(Str(sm, "amount"));
            return null;
        }

        static string S(IReadOnlyDictionary<string, JsonElement> d, string k) { JsonElement v; return d != null && d.TryGetValue(k, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
        static int I(IReadOnlyDictionary<string, JsonElement> d, string k)
        {
            JsonElement v; int n;
            if (d == null || !d.TryGetValue(k, out v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n)) return n;
            return v.ValueKind == JsonValueKind.String && int.TryParse(ShopifyAdmin.ToLatinDigits(v.GetString() ?? ""), out n) ? n : 0;
        }
        static List<string> Arr(JsonElement e, string name)
        {
            JsonElement a;
            return e.TryGetProperty(name, out a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList() : new List<string>();
        }
        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return null;
        }
        static decimal Dec(string s) { decimal d; return decimal.TryParse(s ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0; }
        static string Money(decimal n, string currency)
        {
            var s = n.ToString(n == Math.Floor(n) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
            return s + " " + (string.IsNullOrEmpty(currency) || currency == "EGP" ? "ج.م" : currency);
        }
        static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }

        // ---------- Egyptian governorates -> Shopify province codes (ISO 3166-2:EG) ----------
        static readonly Dictionary<string, string> Gov = BuildGov();
        static Dictionary<string, string> BuildGov()
        {
            var raw = new Dictionary<string, string[]> {
                { "C", new[] { "القاهره", "cairo", "حلوان", "مدينه نصر", "التجمع", "التجمع الخامس", "القاهره الجديده", "المعادي", "مصر الجديده", "الشروق", "بدر", "العبور", "شبرا" } },
                { "GZ", new[] { "الجيزه", "giza", "6 اكتوبر", "اكتوبر", "السادس من اكتوبر", "الشيخ زايد", "زايد", "الهرم", "فيصل", "الدقي", "المهندسين" } },
                { "ALX", new[] { "الاسكندريه", "اسكندريه", "alexandria", "alex" } },
                { "ASN", new[] { "اسوان", "aswan" } }, { "AST", new[] { "اسيوط", "asyut", "assiut" } },
                { "BA", new[] { "البحر الاحمر", "red sea", "الغردقه", "hurghada" } }, { "BH", new[] { "البحيره", "beheira", "دمنهور" } },
                { "BNS", new[] { "بني سويف", "beni suef" } }, { "DK", new[] { "الدقهليه", "dakahlia", "المنصوره", "mansoura" } },
                { "DT", new[] { "دمياط", "damietta" } }, { "FYM", new[] { "الفيوم", "faiyum", "fayoum" } },
                { "GH", new[] { "الغربيه", "gharbia", "طنطا", "tanta", "المحله", "المحله الكبري" } }, { "IS", new[] { "الاسماعيليه", "ismailia" } },
                { "JS", new[] { "جنوب سيناء", "south sinai", "شرم الشيخ", "sharm el sheikh", "دهب" } },
                { "KB", new[] { "القليوبيه", "qalyubia", "بنها", "شبرا الخيمه" } }, { "KFS", new[] { "كفر الشيخ", "kafr el sheikh" } },
                { "KN", new[] { "قنا", "qena" } }, { "LX", new[] { "الاقصر", "luxor" } }, { "MN", new[] { "المنيا", "minya" } },
                { "MNF", new[] { "المنوفيه", "monufia", "menofia", "شبين الكوم" } }, { "MT", new[] { "مطروح", "مرسي مطروح", "matrouh", "العلمين" } },
                { "PTS", new[] { "بورسعيد", "بور سعيد", "port said" } }, { "SHG", new[] { "سوهاج", "sohag" } },
                { "SHR", new[] { "الشرقيه", "sharqia", "sharkia", "الزقازيق", "zagazig", "العاشر من رمضان" } },
                { "SIN", new[] { "شمال سيناء", "north sinai", "العريش" } }, { "SUZ", new[] { "السويس", "suez" } },
                { "WAD", new[] { "الوادي الجديد", "new valley", "الخارجه" } } };
            var d = new Dictionary<string, string>();
            foreach (var kv in raw) foreach (var n in kv.Value) d[Norm(n)] = kv.Key;
            return d;
        }
        static string Norm(string s)
        {
            s = ShopifyAdmin.ToLatinDigits(s ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, "[ً-ْـ]", "");
            s = s.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي');
            s = Regex.Replace(s, @"^(محافظه|محافظة)\s+", "");
            s = Regex.Replace(s, @"\s+(governorate|محافظه)$", "");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }
        public static string Province(string name)
        {
            var n = Norm(name);
            if (n.Length == 0) return null;
            string code;
            if (Gov.TryGetValue(n, out code)) return code;
            if (n.StartsWith("ال") && Gov.TryGetValue(n.Substring(2), out code)) return code;
            if (Gov.TryGetValue("ال" + n, out code)) return code;
            if (Regex.IsMatch(n, "^[a-z]{1,3}$") && Gov.Values.Contains(n.ToUpperInvariant())) return n.ToUpperInvariant();
            return null;
        }
    }

    // api/chatorders.ashx  store owner: GET -> settings + recent orders; POST {enabled, pay}
    public class ChatOrdersHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "POST")
                    {
                        if (!Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                        var b = Web.Body(req, 500);
                        object en; bool on = b.TryGetValue("enabled", out en) && en is bool && (bool)en;
                        string pay = Web.Str(b, "pay", 10); if (pay != "card" && pay != "both") pay = "cod";
                        if (on)
                        {
                            if (!PlanGate.Allows(c, s, ChatOrders.Feature)) { PlanGate.Deny(res, c, s, ChatOrders.Feature); return; }
                            if (ShopifyAdmin.For(s) == null) { Web.Error(res, 400, "no_shopify", "اعمل ربط كامل لمتجرك على Shopify الأول (بالـ Client ID والـ secret)."); return; }
                            var miss = new[] { "read_products", ChatOrders.Scope }.Where(x => !ShopifyAdmin.Has(s.ShopifyScopes, x)).ToList();
                            if (miss.Count > 0) { Web.Error(res, 400, "no_scope", "التطبيق ناقصه صلاحية " + string.Join(" و", miss) + ". ضيفها في Scopes واعمل Release للنسخة ووافق عليها، وبعدين اربط تاني."); return; }
                        }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CoOn=@o, CoPay=@p WHERE Id=@s", "@o", on, "@p", pay, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Db.BumpVersion(c, s.Id);   // the assistant's instructions change with it
                        s = Db.StoreById(c, s.Id);
                    }
                    PlanInfo offer;
                    bool allowed = PlanGate.Allows(c, s, ChatOrders.Feature, out offer);
                    Web.Json(res, 200, new Dictionary<string, object> {
                        { "enabled", s.CoOn }, { "pay", s.CoPay ?? "cod" }, { "full", ShopifyAdmin.For(s) != null },
                        { "missing", new[] { "read_products", ChatOrders.Scope }.Where(x => !ShopifyAdmin.Has(s.ShopifyScopes, x)).ToArray() },
                        { "whatsapp", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber) },
                        { "locked", allowed ? null : (offer != null ? PlanGate.OfferJson(c, s, offer) : new Dictionary<string, object> { { "id", 0 }, { "name", "" } }) },
                        { "recent", ChatOrders.Recent(c, s.Id) } });
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }
    }
}
