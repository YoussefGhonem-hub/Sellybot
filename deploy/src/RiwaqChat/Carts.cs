// Abandoned-cart reminder on WhatsApp (Shopify). A customer reaches checkout, types their phone and leaves without
// ordering: after the owner's delay (CartDelay minutes) they get ONE message from the store's number with what they
// picked and Shopify's link to finish the order (plus the owner's coupon, if any).
//   - Shopify's CHECKOUTS_CREATE / CHECKOUTS_UPDATE webhooks (read_orders) feed dbo.CartReminders; every update while the
//     customer is still typing pushes the message later.
//   - Jobs runs SendDue every few minutes. Right before sending, the store's orders since the checkout are read back from
//     Shopify (Order.checkoutToken): a customer who ordered meanwhile gets nothing.
//   - Never at night (23:00–09:00 Cairo: it waits for the morning), one reminder per phone per 3 days, and «وقف» stops them.
//   - EasyOrders has no abandoned-cart API (no endpoint or webhook), so it's Shopify only; the dashboard says so.
// Plan gate "carts" (open until the super admin picks a minimum) on top of the Shopify link and the WhatsApp service.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public static class Carts
    {
        public const string Feature = "carts";
        public static readonly int[] Delays = { 30, 60, 180 };   // minutes the owner can pick
        const int GapDays = 3;                                  // one reminder per phone in this many days
        const int NoteDays = 3;                                 // the assistant knows about the reminder for this long

        // ---------- 1. Shopify tells us about a checkout ----------
        public static void OnShopifyCheckout(SqlConnection c, StoreInfo s, byte[] body)
        {
            if (s == null || !s.CartOn || s.Status != "active") return;
            JsonElement o;
            try { using (var doc = JsonDocument.Parse(body)) o = doc.RootElement.Clone(); }
            catch { return; }
            string token = Str(o, "token");
            if (string.IsNullOrEmpty(token) || token.Length > 64) return;
            if (!string.IsNullOrEmpty(Str(o, "completed_at"))) { MarkOrdered(c, s.Id, token, null); return; }

            var ship = Obj(o, "shipping_address"); var bill = Obj(o, "billing_address"); var cust = Obj(o, "customer");
            string country = First(Str(ship, "country_code"), Str(bill, "country_code"));
            string phone = new[] { Str(ship, "phone"), Str(o, "phone"), Str(bill, "phone"), Str(cust, "phone"), Str(o, "sms_marketing_phone") }
                .Select(p => OrderConfirm.Phone(p, country)).FirstOrDefault(p => p != null);
            string first = FirstName(First(Str(ship, "first_name"), Str(cust, "first_name"), Str(bill, "first_name")));
            string url = Str(o, "abandoned_checkout_url");
            string total = EasyOrders.Money(Num(Str(o, "total_price")), CurrencyText(First(Str(o, "presentment_currency"), Str(o, "currency"))));
            string items = Items(o);
            if (string.IsNullOrEmpty(items)) return;   // an empty checkout: nothing to remind about

            var due = DateTime.UtcNow.AddMinutes(Delays.Contains(s.CartDelay) ? s.CartDelay : 60);
            // a new checkout, or the same one still being filled in: (re)schedule it while it's waiting
            using (var cmd = Db.Cmd(c, @"
MERGE dbo.CartReminders AS t
USING (SELECT @s AS StoreId, 'shopify' AS Source, @k AS CheckoutId) AS x ON t.StoreId = x.StoreId AND t.Source = x.Source AND t.CheckoutId = x.CheckoutId
WHEN MATCHED AND t.Status = 'waiting' THEN UPDATE SET Phone = ISNULL(@p, t.Phone), Customer = ISNULL(@n, t.Customer), Items = @i, Total = @t,
  Url = ISNULL(@u, t.Url), DueAt = @d, UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (StoreId, Source, CheckoutId, Phone, Customer, Items, Total, Url, Status, DueAt)
  VALUES (@s, 'shopify', @k, @p, @n, @i, @t, @u, 'waiting', @d);",
                "@s", s.Id, "@k", token, "@p", (object)phone ?? DBNull.Value, "@n", (object)Cut(first, 100) ?? DBNull.Value, "@i", Cut(items, 500),
                "@t", (object)Cut(total, 40) ?? DBNull.Value, "@u", (object)Cut(url, 800) ?? DBNull.Value, "@d", due))
                cmd.ExecuteNonQuery();
        }

        // orders/create: the checkout became an order (before or after our message)
        public static void OnShopifyOrder(SqlConnection c, StoreInfo s, byte[] body)
        {
            if (s == null) return;
            JsonElement o;
            try { using (var doc = JsonDocument.Parse(body)) o = doc.RootElement.Clone(); }
            catch { return; }
            string token = Str(o, "checkout_token");
            if (!string.IsNullOrEmpty(token)) MarkOrdered(c, s.Id, token, Str(o, "name"));
        }

        // waiting -> ordered (no message needed); sent -> ordered after the reminder (a recovered sale)
        static void MarkOrdered(SqlConnection c, int storeId, string token, string orderName)
        {
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.CartReminders SET Status = CASE WHEN Status='sent' THEN 'recovered' ELSE 'ordered' END,
                OrderedAt = SYSUTCDATETIME(), Note = ISNULL(@n, Note), UpdatedAt = SYSUTCDATETIME()
                WHERE StoreId=@s AND Source='shopify' AND CheckoutId=@k AND Status IN ('waiting','sent','skipped','failed')",
                "@s", storeId, "@k", token, "@n", string.IsNullOrEmpty(orderName) ? (object)DBNull.Value : "اتعمل أوردر " + orderName))
                cmd.ExecuteNonQuery();
        }

        // ---------- 2. the reminders that are due (Jobs) ----------
        class Due { public long Id; public int StoreId; public string Token, Phone, Customer, Items, Total, Url; public DateTime CreatedAt; }

        public static async Task SendDue(SqlConnection c, CancellationToken ct)
        {
            var list = new List<Due>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 40 Id, StoreId, CheckoutId, Phone, Customer, Items, Total, Url, CreatedAt FROM dbo.CartReminders WHERE Status='waiting' AND DueAt <= SYSUTCDATETIME() ORDER BY DueAt"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Due { Id = (long)r["Id"], StoreId = (int)r["StoreId"], Token = (string)r["CheckoutId"], Phone = r["Phone"] as string, Customer = r["Customer"] as string,
                        Items = r["Items"] as string, Total = r["Total"] as string, Url = r["Url"] as string, CreatedAt = DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc) });
            if (list.Count == 0) return;

            // night in Cairo: everything waits for the morning
            var morning = WaNotify.QuietUntil();
            if (morning.HasValue)
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.CartReminders SET DueAt=@d WHERE Status='waiting' AND DueAt <= SYSUTCDATETIME()", "@d", morning.Value)) cmd.ExecuteNonQuery();
                return;
            }

            var stores = new Dictionary<int, StoreInfo>();
            foreach (var d in list)
            {
                if (ct.IsCancellationRequested) return;
                StoreInfo s;
                if (!stores.TryGetValue(d.StoreId, out s)) stores[d.StoreId] = s = Db.StoreById(c, d.StoreId);
                try { await SendOne(c, s, d); }
                catch (Exception ex) { Log.Error(ex); Mark(c, d.Id, "failed", "حصلت مشكلة وإحنا بنبعت"); }
            }
        }

        static async Task SendOne(SqlConnection c, StoreInfo s, Due d)
        {
            if (s == null || !s.CartOn) { Mark(c, d.Id, "skipped", "استرجاع السلة اتقفل"); return; }
            if (string.IsNullOrEmpty(d.Phone)) { Mark(c, d.Id, "skipped", "الزبون ماكتبش رقم موبايل (أو Shopify مخبّيه)"); return; }
            if (string.IsNullOrEmpty(d.Url)) { Mark(c, d.Id, "skipped", "Shopify مابعتتش لينك إكمال الطلب"); return; }
            if (Billing.Expired(s)) { Mark(c, d.Id, "skipped", "الباقة خلصت"); return; }
            if (!PlanGate.Allows(c, s, Feature) || !PlanGate.Allows(c, s, "shopify")) { Mark(c, d.Id, "skipped", "باقتك الحالية مش شاملة استرجاع السلة"); return; }
            var cant = WaNotify.CantSend(c, s);
            if (cant != null) { Mark(c, d.Id, "skipped", cant); return; }
            if (WaOptOut.Has(c, s.Id, d.Phone)) { Mark(c, d.Id, "skipped", "الزبون طلب مايوصلهوش رسايل زي دي"); return; }
            if (RecentlyReminded(c, s.Id, d.Phone, d.Id)) { Mark(c, d.Id, "skipped", "اتبعتله تذكير في آخر " + GapDays + " أيام"); return; }

            // did they order meanwhile? (the order webhook isn't always subscribed, so ask Shopify)
            var creds = ShopifyAdmin.For(s);
            if (creds == null) { Mark(c, d.Id, "skipped", "ربط Shopify الكامل اتشال"); return; }
            string ordered;
            try { ordered = await OrderedSince(creds, d.Token, d.CreatedAt); }
            catch (Exception ex) { Log.Error(ex); Mark(c, d.Id, "failed", "مقدرناش نتأكد من Shopify إن الزبون ماطلبش"); return; }
            if (ordered != null) { MarkOrdered(c, s.Id, d.Token, ordered); return; }

            if (!Web.Allow("cart:" + s.Id, 60, TimeSpan.FromHours(1))) return;   // stays waiting: the next run sends it
            var why = await WaNotify.ToCustomer(c, s, d.Phone, Message(s, d), "sb-cart-" + d.Id);
            if (why == null)
                using (var cmd = Db.Cmd(c, "UPDATE dbo.CartReminders SET Status='sent', SentAt=SYSUTCDATETIME(), Note=NULL, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id", "@id", d.Id)) cmd.ExecuteNonQuery();
            else Mark(c, d.Id, "failed", why);
        }

        // The order made from this checkout since it started, or null
        static async Task<string> OrderedSince(ShopifyAdmin.Creds creds, string token, DateTime since)
        {
            const string q = "query($q: String!) { orders(first: 50, sortKey: CREATED_AT, reverse: true, query: $q) { nodes { name checkoutToken } } }";
            var g = await ShopifyAdmin.Query(creds, q, new { q = "created_at:>='" + since.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "'" }, CancellationToken.None);
            JsonElement orders, nodes;
            if (!g.Data.TryGetProperty("orders", out orders) || !orders.TryGetProperty("nodes", out nodes) || nodes.ValueKind != JsonValueKind.Array) return null;
            foreach (var n in nodes.EnumerateArray())
                if (string.Equals(Str(n, "checkoutToken"), token, StringComparison.Ordinal)) return Str(n, "name") ?? "";
            return null;
        }

        static bool RecentlyReminded(SqlConnection c, int storeId, string phone, long notId)
        {
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(*) FROM dbo.CartReminders WHERE StoreId=@s AND Phone=@p AND Id<>@id AND SentAt > DATEADD(DAY,-@g,SYSUTCDATETIME())",
                "@s", storeId, "@p", phone, "@id", notId, "@g", GapDays))
                return (int)cmd.ExecuteScalar() > 0;
        }

        static string Message(StoreInfo s, Due d)
        {
            var sb = new StringBuilder();
            sb.Append("أهلًا").Append(string.IsNullOrWhiteSpace(d.Customer) ? "" : " " + d.Customer).Append(" 👋\n");
            sb.Append("شفنا إنك كنت بتطلب من *").Append(s.Name).Append("* ولسه ماكمّلتش:\n");
            sb.Append("🛒 ").Append(d.Items).Append('\n');
            if (!string.IsNullOrEmpty(d.Total)) sb.Append("💰 الإجمالي: *").Append(d.Total).Append("*\n");
            sb.Append("\nطلبك محفوظ، وتقدر تكمّله من هنا في دقيقة 👇\n").Append(d.Url).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.CartCoupon)) sb.Append("\n🎁 ولو كمّلته دلوقتي، استخدم كود *").Append(s.CartCoupon.Trim()).Append("*\n");
            sb.Append("\nلو عندك أي سؤال عن المقاس أو الشحن، ابعتلنا هنا وهنرد عليك على طول.\n");
            sb.Append("_لو مش عايز رسايل زي دي، ابعت «وقف»._");
            return sb.ToString();
        }

        // ---------- 3. the customer answers ----------
        // A line for the assistant: what was in the cart and the link to finish it
        public static string Note(SqlConnection c, int storeId, string chatKey)
        {
            var phone = WaChat.Digits(chatKey);
            if (phone == null) return "";
            using (var cmd = Db.Cmd(c, @"SELECT TOP 1 Items, Total, Url FROM dbo.CartReminders WHERE StoreId=@s AND Status='sent' AND Phone=@p
                AND SentAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) ORDER BY SentAt DESC", "@s", storeId, "@p", phone, "@d", NoteDays))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return "";
                return "\n- الزبون ده اتبعتله تذكير إنه ساب طلب ماكمّلهوش فيه: " + (r["Items"] as string) + (r["Total"] is string ? " (الإجمالي " + r["Total"] + ")" : "") +
                       ". لينك إكمال الطلب: " + (r["Url"] as string) + " . لو سأل عن حاجة فيه ساعده بالمعلومة، ولو جاهز يطلب ابعتله اللينك ده. ماتضغطش عليه، ولو قال مش عايز خلاص اشكره بلطف.";
            }
        }

        // ---------- dashboard ----------
        public static List<Dictionary<string, object>> Recent(SqlConnection c, int storeId, int n = 15)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP (@n) Customer, Items, Total, Status, Note, CreatedAt, SentAt, OrderedAt FROM dbo.CartReminders
                WHERE StoreId=@s AND Phone IS NOT NULL ORDER BY Id DESC", "@n", n, "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> {
                        { "customer", r["Customer"] as string ?? "" }, { "items", r["Items"] as string ?? "" }, { "total", r["Total"] as string ?? "" },
                        { "status", r["Status"] as string }, { "note", r["Note"] as string ?? "" }, { "at", Iso(r["CreatedAt"]) },
                        { "sentAt", Iso(r["SentAt"]) }, { "orderedAt", Iso(r["OrderedAt"]) } });
            return list;
        }

        // Checkouts without a phone (left before typing it, or Shopify hides it) only count: they'd flood the list
        // last 30 days: reminders sent and orders that came after them
        public static Dictionary<string, object> Stats(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, @"SELECT ISNULL(SUM(CASE WHEN SentAt IS NOT NULL THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN Status='recovered' THEN 1 ELSE 0 END),0),
                ISNULL(SUM(CASE WHEN Status='waiting' AND Phone IS NOT NULL THEN 1 ELSE 0 END),0),
                ISNULL(SUM(CASE WHEN Status='skipped' AND Phone IS NULL THEN 1 ELSE 0 END),0)
                FROM dbo.CartReminders WHERE StoreId=@s AND CreatedAt > DATEADD(DAY,-30,SYSUTCDATETIME())", "@s", storeId))
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                return new Dictionary<string, object> { { "sent", Convert.ToInt32(r[0]) }, { "recovered", Convert.ToInt32(r[1]) }, { "waiting", Convert.ToInt32(r[2]) }, { "nophone", Convert.ToInt32(r[3]) } };
            }
        }

        // ---------- helpers ----------
        static string Items(JsonElement o)
        {
            JsonElement arr;
            if (!o.TryGetProperty("line_items", out arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var parts = new List<string>();
            int total = 0;
            foreach (var li in arr.EnumerateArray())
            {
                total++;
                if (parts.Count >= 3) continue;
                string title = Str(li, "title"), variant = Str(li, "variant_title");
                if (string.IsNullOrEmpty(title)) continue;
                int qty; int.TryParse(Str(li, "quantity") ?? "1", out qty);
                parts.Add(title + (string.IsNullOrEmpty(variant) || variant == "Default Title" ? "" : " (" + variant + ")") + (qty > 1 ? " × " + qty : ""));
            }
            if (parts.Count == 0) return null;
            return string.Join("، ", parts) + (total > parts.Count ? " و" + (total - parts.Count) + " كمان" : "");
        }

        static void Mark(SqlConnection c, long id, string status, string note)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.CartReminders SET Status=@st, Note=@n, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id", "@st", status, "@n", (object)Cut(note, 300) ?? DBNull.Value, "@id", id))
                cmd.ExecuteNonQuery();
        }

        static string Iso(object v) { return v == null || v == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc).ToString("o"); }
        static decimal Num(string raw) { decimal d; return decimal.TryParse(raw ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0; }
        static string FirstName(string full) { var f = (full ?? "").Trim().Split(' ')[0]; return f.Length > 0 ? f : null; }
        static string CurrencyText(string code) { return code == "EGP" ? "ج.م" : code; }
        static string First(params string[] v) { return v.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); }
        static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }
        static JsonElement? Obj(JsonElement e, string name) { JsonElement v; return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Object ? v : (JsonElement?)null; }
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

    // «وقف»: a customer who doesn't want Sellybot's own messages (abandoned cart, back in stock) from this store any more.
    // Answers to their own questions and order messages they asked for still work.
    public static class WaOptOut
    {
        static readonly string[] Words = { "وقف", "stop", "الغاء", "إلغاء", "ستوب", "unsubscribe" };

        public static bool Has(SqlConnection c, int storeId, string phone)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaOptOuts WHERE StoreId=@s AND Phone=@p", "@s", storeId, "@p", phone)) return (int)cmd.ExecuteScalar() > 0;
        }

        // The customer wrote «وقف» after one of our reminders: remember it, say so, and the bot doesn't answer this message
        public static bool Intercept(SqlConnection c, StoreInfo s, string chatKey, string chat, string text)
        {
            var t = (text ?? "").Trim().Trim('.', '!', '«', '»', '"').ToLowerInvariant();
            if (!Words.Contains(t)) return false;
            var phone = WaChat.Digits(chatKey);
            if (phone == null) return false;
            using (var cmd = Db.Cmd(c, @"SELECT (SELECT COUNT(*) FROM dbo.CartReminders WHERE StoreId=@s AND Phone=@p AND SentAt > DATEADD(DAY,-14,SYSUTCDATETIME()))
                + (SELECT COUNT(*) FROM dbo.RestockWaits WHERE StoreId=@s AND Phone=@p AND (Status='waiting' OR SentAt > DATEADD(DAY,-14,SYSUTCDATETIME())))", "@s", s.Id, "@p", phone))
                if ((int)cmd.ExecuteScalar() == 0) return false;   // «وقف» out of the blue is just a message for the assistant
            using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.WaOptOuts WHERE StoreId=@s AND Phone=@p) INSERT INTO dbo.WaOptOuts(StoreId, Phone) VALUES(@s, @p);
                UPDATE dbo.RestockWaits SET Status='expired', Note=N'الزبون بعت «وقف»' WHERE StoreId=@s AND Phone=@p AND Status='waiting';", "@s", s.Id, "@p", phone))
                cmd.ExecuteNonQuery();
            const string ok = "تمام 🙏 مش هنبعتلك رسايل زي دي تاني. ولو احتجت أي حاجة، ابعتلنا هنا في أي وقت.";
            var token = WaAccount.Token(c, s);
            if (token != null && WaAccount.HasService(s))
            {
                WhatsAppBot.Save(c, s.Id, chatKey, false, ok, null);
                int storeId = s.Id; string instance = s.WaInstance;
                System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    try { await Wapilot.Send(token, instance, chat, ok, "sb-stop-" + storeId + "-" + phone + "-" + DateTime.UtcNow.Ticks); }
                    catch (Exception ex) { Log.Error(ex); }
                });
            }
            return true;
        }
    }
}
