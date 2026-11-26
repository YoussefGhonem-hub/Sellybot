// The delivery-day message on WhatsApp. When an order goes out for delivery, the customer gets a message from the store's
// number: your order is on its way, have the cash ready, keep your phone close and answer the courier. Most cash-on-delivery
// returns are a customer who wasn't expecting the courier or didn't pick up, and this one message is aimed at exactly that.
//   EasyOrders: the "order-status-update" webhook with new_status "in_delivery" (the owner or their shipping integration set it).
//   Shopify:    the FULFILLMENT_EVENTS_CREATE webhook with status "out_for_delivery". The shipping company's app sets that
//               status; a store that marks orders shipped by hand never gets it. Needs the read_fulfillments scope.
// The order is always read back from the store system (a webhook body is never trusted for what we send), and each order
// gets one message at most. Plan gate "delivery" (open to every plan until the super admin picks a minimum).
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
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class Delivery
    {
        public const string Feature = "delivery";
        public const string Scope = "read_fulfillments";   // Shopify: FULFILLMENT_EVENTS_CREATE
        const int ReplyDays = 3;                           // the assistant knows about the message for this long

        // ---------- EasyOrders ----------
        public static void QueueEo(int storeId, string orderId)
        {
            if (string.IsNullOrEmpty(orderId) || !Regex.IsMatch(orderId, "^[A-Za-z0-9-]{6,64}$")) return;
            HostingEnvironment.QueueBackgroundWorkItem(ct => OnEo(storeId, orderId));
        }

        static async Task OnEo(int storeId, string orderId)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (s == null || !s.EoDnOn || s.Status != "active") return;
                    var k = EasyOrders.For(s);
                    if (k == null || Exists(c, s.Id, "eo", orderId)) return;
                    JsonElement o;
                    try { o = await EasyOrders.GetOrder(k, orderId); }
                    catch (EoException ex)
                    {
                        if (!ex.NotFound) Record(c, s.Id, "eo", orderId, null, null, null, null, "failed", "مقدرناش نقرا الطلب من EasyOrders: " + ex.Message);
                        return;
                    }
                    if (o.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(EasyOrders.Str(o, "id"))) return;

                    string shortId = EasyOrders.Str(o, "short_id");
                    string name = "#" + (shortId ?? orderId.Substring(0, Math.Min(8, orderId.Length)));
                    string status = EasyOrders.Str(o, "status");
                    bool cod = EasyOrders.IsCod(EasyOrders.Str(o, "payment_method"));
                    string due = cod ? EasyOrders.Money(EasyOrders.Num(o, "total_cost"), null) : null;
                    // the status moved on again before we got here (delivered, back to the store...): nothing to say any more
                    string skip = status != "in_delivery" ? "حالته في EasyOrders بقت «" + EasyOrders.StatusText(status) + "»" : null;
                    var d = new Notice { Source = "eo", OrderId = orderId, Name = name, First = FirstName(EasyOrders.Str(o, "full_name")),
                        Phone = OrderConfirm.Phone(EasyOrders.Str(o, "phone"), null), Due = string.IsNullOrEmpty(due) ? null : due, Paid = !cod, Today = false };
                    await Send(c, s, d, skip, EasyOrders.Feature);
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // ---------- Shopify ----------
        // The fulfillment_events/create webhook (called from ShopifyWebhookHandler, the HMAC already checked)
        public static void OnShopifyEvent(StoreInfo s, byte[] body)
        {
            if (s == null || !s.DnOn || s.Status != "active") return;
            JsonElement e;
            try { using (var doc = JsonDocument.Parse(body)) e = doc.RootElement.Clone(); }
            catch { return; }
            if (!string.Equals(Str(e, "status"), "out_for_delivery", StringComparison.OrdinalIgnoreCase)) return;
            long orderId;
            if (!long.TryParse(Str(e, "order_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out orderId) || orderId <= 0) return;
            int storeId = s.Id;
            HostingEnvironment.QueueBackgroundWorkItem(ct => OnShopify(storeId, orderId));
        }

        static async Task OnShopify(int storeId, long orderId)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (s == null || !s.DnOn || s.Status != "active") return;
                    var creds = ShopifyAdmin.For(s);
                    string id = orderId.ToString(CultureInfo.InvariantCulture);
                    if (creds == null || Exists(c, s.Id, "shopify", id)) return;

                    const string q = "query($id: ID!) { order(id: $id) { name cancelledAt phone " +
                                     "totalOutstandingSet { shopMoney { amount currencyCode } } " +
                                     "shippingAddress { firstName phone countryCodeV2 } billingAddress { firstName phone countryCodeV2 } } }";
                    ShopifyAdmin.Gql g;
                    try { g = await ShopifyAdmin.Query(creds, q, new { id = "gid://shopify/Order/" + id }, CancellationToken.None); }
                    catch (ShopifyException ex) { Record(c, s.Id, "shopify", id, null, null, null, null, "failed", "مقدرناش نقرا الطلب من Shopify: " + ex.Message); return; }
                    JsonElement o;
                    if (!g.Data.TryGetProperty("order", out o) || o.ValueKind != JsonValueKind.Object) return;

                    var ship = Obj(o, "shippingAddress"); var bill = Obj(o, "billingAddress");
                    string country = Str(ship, "countryCodeV2") ?? Str(bill, "countryCodeV2");
                    string phone = new[] { Str(ship, "phone"), Str(o, "phone"), Str(bill, "phone") }.Select(p => OrderConfirm.Phone(p, country)).FirstOrDefault(p => p != null);
                    var money = Obj(Obj(o, "totalOutstandingSet"), "shopMoney");
                    decimal owed; decimal.TryParse(Str(money, "amount") ?? "0", NumberStyles.Any, CultureInfo.InvariantCulture, out owed);
                    string due = owed > 0 ? EasyOrders.Money(owed, CurrencyText(Str(money, "currencyCode"))) : null;
                    var d = new Notice { Source = "shopify", OrderId = id, Name = Str(o, "name") ?? ("#" + id), First = FirstName(Str(ship, "firstName") ?? Str(bill, "firstName")),
                        Phone = phone, Due = due, Paid = owed <= 0, Today = true };
                    await Send(c, s, d, Str(o, "cancelledAt") != null ? "الطلب ملغي" : null, "shopify");
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // ---------- the store's own page (SellyStore): the owner, or the shipping company, moved the order to «خرج للتوصيل» ----------
        public static async Task OnShop(SqlConnection c, StoreInfo s, ShopOrder o)
        {
            if (s == null || s.Status != "active") return;
            string id = o.Id.ToString(CultureInfo.InvariantCulture);
            if (Exists(c, s.Id, "shop", id)) return;
            await Send(c, s, new Notice { Source = "shop", OrderId = id, Name = o.NoText, First = FirstName(o.Name), Phone = o.Phone, Due = o.Due <= 0 ? null : o.Lang == "en" ? "EGP " + o.Due.ToString("#,0.##", CultureInfo.InvariantCulture) : ShopOrders.Money(o.Due), Paid = o.Due <= 0, Today = true, En = o.Lang == "en", Part = ShopShipDay.Period(o), Who = ShopSelf.Line(o, o.Lang == "en") }, null, Feature);
        }

        // ---------- the message ----------
        class Notice { public string Source, OrderId, Name, First, Phone, Due, Part, Who; public bool Paid, Today, En; }   // Part: the period the customer chose (ShopShipDay); Who: who delivers (ShopSelf)

        static async Task Send(SqlConnection c, StoreInfo s, Notice d, string skip, string storeFeature)
        {
            string status = "sent", note = null;
            if (skip != null) { status = "skipped"; note = skip; }
            else if (d.Phone == null) { status = "skipped"; note = "الأوردر مفيهوش رقم موبايل"; }
            else if (Billing.Expired(s)) { status = "skipped"; note = "الباقة خلصت"; }
            else if (!PlanGate.Allows(c, s, storeFeature) || !PlanGate.Allows(c, s, Feature) || !WaAccount.PlanAllows(c, s, out _)) { status = "skipped"; note = "باقتك الحالية مش شاملة رسالة يوم التوصيل"; }
            else if (!Web.Allow("dn:" + s.Id, 300, TimeSpan.FromHours(1))) { status = "skipped"; note = "أوردرات كتير في ساعة واحدة"; }

            long rowId = Record(c, s.Id, d.Source, d.OrderId, d.Name, d.First, d.Phone, d.Due, status, note);
            if (rowId <= 0 || status != "sent") return;   // 0 = this order already had its message

            string token = WaAccount.Token(c, s);
            if (string.IsNullOrEmpty(s.WaInstance) || token == null || string.IsNullOrEmpty(s.WaNumber)) { Mark(c, rowId, "failed", "واتساب المتجر مش مربوط، فالرسالة ماتبعتتش"); return; }
            string text = Message(s, d);
            long? mine = WhatsAppBot.Save(c, s.Id, d.Phone, false, text, null);   // part of the chat history, and recognised when Wapilot echoes it
            try { await Wapilot.Send(token, s.WaInstance, d.Phone + "@c.us", text, "sb-dn-" + rowId); }
            catch (WaException ex)
            {
                if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                Mark(c, rowId, "failed", ex.Message);
            }
        }

        static string Message(StoreInfo s, Notice d)
        {
            // the customer ordered from the store's English pages
            if (d.En) return "Hello" + (string.IsNullOrWhiteSpace(d.First) ? "" : " " + d.First) + " 👋\nYour order *" + d.Name + "* from *" + s.Name + "* is out for delivery and arrives today" + (d.Part == "am" ? " in the morning, as you chose" : d.Part == "pm" ? " in the evening, as you chose" : "") + " 🚚\n\n" + (string.IsNullOrEmpty(d.Who) ? "" : d.Who + "\n\n") + (d.Due != null ? "💵 Please have *" + d.Due + "* in cash for the courier\n" : "") + "📱 Keep your phone near you and answer the courier's call\n\nIf you won't be there or need to change the address or the time, just reply here 🙏";
            var sb = new StringBuilder();
            sb.Append("أهلًا").Append(string.IsNullOrWhiteSpace(d.First) ? "" : " " + d.First).Append(" 👋\n");
            // Shopify's "out for delivery" is today; EasyOrders' "in delivery" only says the shipping company has it
            if (d.Today) sb.Append("طلبك *").Append(d.Name).Append("* من *").Append(s.Name).Append("* خرج مع المندوب، وهيوصلك النهارده").Append(d.Part == "am" ? " الصبح زي ما اخترت" : d.Part == "pm" ? " بالليل زي ما اخترت" : "").Append(" 🚚\n\n");
            else sb.Append("طلبك *").Append(d.Name).Append("* من *").Append(s.Name).Append("* خرج مع شركة الشحن وفي الطريق ليك 🚚\n\n");
            if (!string.IsNullOrEmpty(d.Who)) sb.Append(d.Who).Append("\n\n");   // «بوصّل بنفسي»: who it is, and the store's line
            if (d.Due != null) sb.Append("💵 جهّز *").Append(d.Due).Append("* كاش للمندوب\n");
            else if (d.Paid) sb.Append("💳 طلبك مدفوع، مش هتدفع حاجة للمندوب\n");
            sb.Append("📱 خلّي موبايلك جنبك ورد على المندوب لما يكلّمك\n\n");
            sb.Append("لو مش هتكون موجود أو محتاج تغيّر العنوان أو الميعاد، ابعتلنا هنا 🙏");
            return sb.ToString();
        }

        // ---------- the customer answers ----------
        // A line for the assistant when the customer writes after the message ("I'm not home today", "change the address")
        public static string Note(SqlConnection c, int storeId, string chatKey)
        {
            var phone = WaChat.Digits(chatKey);
            if (phone == null) return "";
            string tail = phone.Length > 9 ? phone.Substring(phone.Length - 9) : phone;
            using (var cmd = Db.Cmd(c, @"SELECT TOP 1 OrderName FROM dbo.DeliveryNotices WHERE StoreId=@s AND Status='sent' AND Phone IS NOT NULL
                AND RIGHT(Phone, 9) = @t AND CreatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", storeId, "@t", tail, "@d", ReplyDays))
            {
                var name = cmd.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(name)) return "";
                return "\n- الزبون ده طلبه " + name + " خرج للتوصيل، واتبعتله رسالة إن المندوب جايله. لو قال إنه مش هيكون موجود أو عايز يغيّر العنوان أو الميعاد أو يلغي، " +
                       "اعتذر بلطف وقوله إن المتجر هيشوف رسالته ويتواصل معاه في أقرب وقت. ماتوعدش بميعاد ولا بتغيير، وماتقولش إن الطلب اتلغى أو اتأجل.";
            }
        }

        // ---------- dashboard ----------
        public static List<Dictionary<string, object>> Recent(SqlConnection c, int storeId, string source, int n = 15)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP (@n) OrderName, Customer, Due, Status, Note, CreatedAt FROM dbo.DeliveryNotices WHERE StoreId=@s AND Source=@src ORDER BY Id DESC",
                "@n", n, "@s", storeId, "@src", source))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> {
                        { "order", r["OrderName"] as string ?? "" }, { "customer", r["Customer"] as string ?? "" }, { "total", r["Due"] as string ?? "" },
                        { "status", r["Status"] as string }, { "note", r["Note"] as string ?? "" },
                        { "at", DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc).ToString("o") } });
            return list;
        }

        // ---------- helpers ----------
        static bool Exists(SqlConnection c, int storeId, string source, string orderId)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.DeliveryNotices WHERE StoreId=@s AND Source=@src AND OrderId=@o", "@s", storeId, "@src", source, "@o", orderId))
                return (int)cmd.ExecuteScalar() > 0;
        }

        // The row's id, or 0 when the order already has one (a webhook delivered twice, or two events for one order)
        static long Record(SqlConnection c, int storeId, string source, string orderId, string name, string first, string phone, string due, string status, string note)
        {
            try
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.DeliveryNotices(StoreId, Source, OrderId, OrderName, Customer, Phone, Due, Status, Note)
                    OUTPUT INSERTED.Id VALUES(@s, @src, @o, @n, @cu, @p, @d, @st, @no)",
                    "@s", storeId, "@src", source, "@o", Cut(orderId, 64), "@n", (object)Cut(name, 40) ?? DBNull.Value, "@cu", (object)Cut(first, 100) ?? DBNull.Value,
                    "@p", (object)phone ?? DBNull.Value, "@d", (object)Cut(due, 40) ?? DBNull.Value, "@st", status, "@no", (object)Cut(note, 300) ?? DBNull.Value))
                    return Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return 0; }
        }

        static void Mark(SqlConnection c, long id, string status, string note)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.DeliveryNotices SET Status=@st, Note=@n WHERE Id=@id", "@st", status, "@n", Cut(note, 300), "@id", id)) cmd.ExecuteNonQuery();
        }

        static string FirstName(string full) { var f = (full ?? "").Trim().Split(' ')[0]; return f.Length > 0 ? f : null; }
        static string CurrencyText(string code) { return code == "EGP" ? "ج.م" : code; }
        static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }
        static JsonElement? Obj(JsonElement? e, string name) { JsonElement v; return e.HasValue && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Object ? v : (JsonElement?)null; }
        static JsonElement? Obj(JsonElement e, string name) { return Obj((JsonElement?)e, name); }
        static string Str(JsonElement? e, string name) { return e.HasValue ? Str(e.Value, name) : null; }
        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return null;
        }
    }
}
