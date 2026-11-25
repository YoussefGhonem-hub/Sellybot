// «زبايني» (2026-11-25, the user): a file per customer, ready-made groups, and the returns alert. Dashboard tab «👥 زبايني», api/customers.ashx.
//
//   1. The file: everything the store has about one phone number in one place. Orders live in dbo.CrmOrders (below); the rest is read
//      where it already is: carts, «بلّغني لما يرجع», handoffs, offers, the WhatsApp chat (kept 60 days), the owner's list. The owner
//      adds a note and a mark, logs an order by hand, and can erase a customer completely.
//   2. The groups (plan gate "crmgroups"): who to sell to again, each one tap from «ابعت عرض» (Offers.cs audiences "c-<group>").
//   3. Returns (plan gate "returns"): a new order from someone who returned before (or whom the owner marked «ماتشحنلوش») alerts the
//      team / the owner on WhatsApp before it ships, and shows in the dashboard and next to the order confirmations.
//
// CrmOrders: Shopify (the orders/create webhook, and a pull of what changed every few hours, the first
// one when the owner opens the page: Shopify gives an app the last 60 days),
// EasyOrders (both webhooks; it has no list of past orders, so it starts when the store is linked, and open orders are re-read in case
// a webhook was missed), an Excel / CSV of old orders the owner uploads, and orders logged by hand. A status the owner set by hand
// (ByHand) is never overwritten. Status: open / shipped / delivered / returned / cancelled; cancelled after it shipped = returned.
// Each store sees only its own customers: nothing here is shared between stores (no common blacklist).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace RiwaqChat
{
    public static class Crm
    {
        public const string Feature = "crm", GroupsFeature = "crmgroups", ReturnsFeature = "returns";
        public const int LostDays = 60, RecentDays = 30, VipOrders = 3, PageSize = 30;
        const int PullHours = 3, HistoryDays = 60;   // Shopify: how often what changed is pulled, and how far back it goes
        public static readonly string[] Groups = { "fresh", "vip", "lost", "cart", "ask", "wait", "back" };
        public static readonly string[] Statuses = { "open", "shipped", "delivered", "returned", "cancelled" };
        public static readonly string[] Marks = { "star", "follow", "whole", "block" };   // مهم · محتاج متابعة · جملة · ماتشحنلوش

        // ---------- one customer ----------
        public class Person
        {
            public string Phone, Name, Note, Mark, City, Currency, LastItems, CartItems, CartTotal, WaitFor, HandReason;
            public bool Stopped;
            public int Orders, Open, Delivered, Returned, Cancelled, Waits;
            public decimal Spent;
            public DateTime? FirstOrder, LastOrder, WaAt, CartAt, WaitAt, HandAt, AddedAt;
            public HashSet<string> Ch = new HashSet<string>();
            public int Kept { get { return Orders - Returned - Cancelled; } }
            public DateTime? LastAt
            {
                get { DateTime? m = null; foreach (var d in new[] { LastOrder, WaAt, CartAt, WaitAt, HandAt, AddedAt }) if (d.HasValue && (m == null || d.Value > m.Value)) m = d; return m; }
            }
            public bool Risky { get { return Mark == "block" || IsRisky(Returned, Delivered); } }
        }

        // Returned at least half of what reached them, or twice and more and at least a third of it
        public static bool IsRisky(int returned, int delivered)
        {
            int all = returned + delivered;
            return returned >= 1 && (returned * 2 >= all || (returned >= 2 && returned * 3 >= all));
        }

        // "رجّع أوردرين من 3"
        public static string RiskText(int returned, int delivered)
        {
            int all = returned + delivered;
            if (returned == 0) return "";
            if (all == 1) return "رجّع الأوردر الوحيد اللي اتشحنله";
            return "رجّع " + (returned == 1 ? "أوردر" : returned == 2 ? "أوردرين" : returned + (returned <= 10 ? " أوردرات" : " أوردر")) + " من " + all;
        }

        public static bool In(Person p, string g, DateTime now)
        {
            switch (g)
            {
                case "fresh": return p.Orders == 1 && p.Kept == 1 && p.LastOrder > now.AddDays(-RecentDays);
                case "vip": return p.Kept >= VipOrders && !p.Risky;
                case "lost": return p.Kept >= 1 && p.LastOrder < now.AddDays(-LostDays);
                case "cart": return p.CartAt > now.AddDays(-RecentDays) && (p.LastOrder == null || p.LastOrder < p.CartAt);
                case "ask": return p.Orders == 0 && p.WaAt > now.AddDays(-RecentDays);
                case "wait": return p.Waits > 0;
                case "back": return p.Risky;
                default: return false;
            }
        }

        // a number as any of our tables kept it -> one form (Offers.Norm); null when it isn't a phone number
        public static string Norm(string raw) { return string.IsNullOrEmpty(raw) ? null : Offers.Norm(raw); }
        static string Tail(string phone) { return phone.Length > 9 ? phone.Substring(phone.Length - 9) : phone; }

        // ---------- everyone (kept for 45 seconds per store) ----------
        static readonly ConcurrentDictionary<int, Tuple<DateTime, List<Person>>> Cache = new ConcurrentDictionary<int, Tuple<DateTime, List<Person>>>();
        public static void Forget(int storeId) { Tuple<DateTime, List<Person>> x; Cache.TryRemove(storeId, out x); }

        public static List<Person> Load(SqlConnection c, StoreInfo s, bool fresh = false)
        {
            Tuple<DateTime, List<Person>> hit;
            if (!fresh && Cache.TryGetValue(s.Id, out hit) && DateTime.UtcNow - hit.Item1 < TimeSpan.FromSeconds(45)) return hit.Item2;
            var map = new Dictionary<string, Person>();
            var own = OwnNumbers(c, s);
            Func<string, Person> at = raw =>
            {
                var ph = Norm(raw);
                if (ph == null || own.Contains(ph)) return null;
                Person p;
                if (!map.TryGetValue(ph, out p)) map[ph] = p = new Person { Phone = ph };
                return p;
            };
            Action<string, Action<SqlDataReader>> each = (sql, row) =>
            {
                using (var cmd = Db.Cmd(c, sql, "@s", s.Id)) { cmd.CommandTimeout = 60; using (var r = cmd.ExecuteReader()) while (r.Read()) row(r); }
            };
            // what the owner wrote
            var ownName = new HashSet<string>();
            each("SELECT Phone, Name, Note, Mark, UpdatedAt FROM dbo.CrmNotes WHERE StoreId=@s", r =>
            {
                var p = at(r["Phone"] as string); if (p == null) return;
                p.Note = r["Note"] as string; p.Mark = r["Mark"] as string; p.AddedAt = Utc(r["UpdatedAt"]);
                var n = r["Name"] as string; if (!string.IsNullOrWhiteSpace(n)) { p.Name = n; ownName.Add(p.Phone); }
            });
            Action<Person, string> name = (p, n) => { if (string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(n)) p.Name = n.Trim(); };
            // orders
            each(@"SELECT Phone, COUNT(*) AS N, SUM(CASE WHEN Status IN ('open','shipped') THEN 1 ELSE 0 END) AS Op, SUM(CASE WHEN Status='delivered' THEN 1 ELSE 0 END) AS De,
                     SUM(CASE WHEN Status='returned' THEN 1 ELSE 0 END) AS Re, SUM(CASE WHEN Status='cancelled' THEN 1 ELSE 0 END) AS Ca,
                     SUM(CASE WHEN Status IN ('open','shipped','delivered') THEN ISNULL(Total,0) ELSE 0 END) AS Sp, MIN(OrderedAt) AS F, MAX(OrderedAt) AS L, MAX(Currency) AS Cur
                   FROM dbo.CrmOrders WHERE StoreId=@s AND Phone IS NOT NULL GROUP BY Phone", r =>
            {
                var p = at(r["Phone"] as string); if (p == null) return;
                p.Orders += Convert.ToInt32(r["N"]); p.Open += Convert.ToInt32(r["Op"]); p.Delivered += Convert.ToInt32(r["De"]); p.Returned += Convert.ToInt32(r["Re"]); p.Cancelled += Convert.ToInt32(r["Ca"]);
                p.Spent += Convert.ToDecimal(r["Sp"]); p.Currency = r["Cur"] as string ?? p.Currency;
                DateTime? f = Utc(r["F"]), l = Utc(r["L"]);
                if (p.FirstOrder == null || f < p.FirstOrder) p.FirstOrder = f;
                if (p.LastOrder == null || l > p.LastOrder) p.LastOrder = l;
            });
            each(@"SELECT Phone, Customer, City, Items FROM (SELECT Phone, Customer, City, Items, ROW_NUMBER() OVER (PARTITION BY Phone ORDER BY OrderedAt DESC, Id DESC) AS K
                   FROM dbo.CrmOrders WHERE StoreId=@s AND Phone IS NOT NULL) x WHERE K=1", r =>
            {
                var p = at(r["Phone"] as string); if (p == null) return;
                name(p, r["Customer"] as string); p.City = r["City"] as string ?? p.City; p.LastItems = r["Items"] as string ?? p.LastItems;
            });
            // the WhatsApp chats and the owner's list
            each("SELECT Phone, Name, Added, AddedAt, WaLastAt FROM dbo.BcContacts WHERE StoreId=@s", r =>
            {
                var p = at(r["Phone"] as string); if (p == null) return;
                name(p, r["Name"] as string);
                var wa = Utc(r["WaLastAt"]); if (wa.HasValue) { p.Ch.Add("wa"); if (p.WaAt == null || wa > p.WaAt) p.WaAt = wa; }
                var add = r["Added"] as string;
                if (add == "messenger") p.Ch.Add("fb"); else if (add == "instagram") p.Ch.Add("ig");
                var ad = Utc(r["AddedAt"]); if (add != null && ad.HasValue && (p.AddedAt == null || ad > p.AddedAt)) p.AddedAt = ad;
            });
            // orders made in the chat, and the older order messages: who they are (the orders themselves are in CrmOrders)
            each(@"SELECT Phone, Name FROM dbo.ChatOrders WHERE StoreId=@s AND Phone IS NOT NULL AND Status IN ('placed','awaiting_payment')
                   UNION ALL SELECT Phone, Customer FROM dbo.DeliveryNotices WHERE StoreId=@s AND Phone IS NOT NULL", r => { var p = at(r[0] as string); if (p != null) name(p, r[1] as string); });
            // carts nobody finished
            each("SELECT Phone, Customer, Items, Total, CreatedAt, Status FROM dbo.CartReminders WHERE StoreId=@s AND Phone IS NOT NULL ORDER BY Id", r =>
            {
                var p = at(r["Phone"] as string); if (p == null) return;
                name(p, r["Customer"] as string);
                var st = r["Status"] as string;
                if (st == "ordered" || st == "recovered") { p.CartAt = null; p.CartItems = null; }   // they came back and bought
                else { p.CartAt = Utc(r["CreatedAt"]); p.CartItems = r["Items"] as string; p.CartTotal = r["Total"] as string; }
            });
            // «بلّغني لما يرجع»
            each("SELECT Phone, Product, Variant, Status, CreatedAt FROM dbo.RestockWaits WHERE StoreId=@s ORDER BY Id", r =>
            {
                var p = at(r["Phone"] as string); if (p == null) return;
                var d = Utc(r["CreatedAt"]); if (p.WaitAt == null || d > p.WaitAt) p.WaitAt = d;
                if ((r["Status"] as string) == "waiting") { p.Waits++; p.WaitFor = (r["Product"] as string) + (r["Variant"] is string ? " (" + r["Variant"] + ")" : ""); }
            });
            // asked for a person
            each("SELECT Phone, Channel, Reason, CreatedAt FROM dbo.Handoffs WHERE StoreId=@s AND Phone IS NOT NULL ORDER BY Id", r =>
            {
                var p = at(r["Phone"] as string); if (p == null) return;
                p.HandAt = Utc(r["CreatedAt"]); p.HandReason = r["Reason"] as string; p.Ch.Add(r["Channel"] as string ?? "web");
            });
            each("SELECT Phone FROM dbo.WaOptOuts WHERE StoreId=@s", r => { Person p; var ph = Norm(r[0] as string); if (ph != null && map.TryGetValue(ph, out p)) p.Stopped = true; });

            var list = map.Values.ToList();
            if (Cache.Count > 300) Cache.Clear();
            Cache[s.Id] = Tuple.Create(DateTime.UtcNow, list);
            return list;
        }

        public static bool IsOwn(SqlConnection c, StoreInfo s, string phone) { return OwnNumbers(c, s).Contains(phone); }

        // the store's own numbers (the main one, the extra lines, the owner's) are never customers
        static HashSet<string> OwnNumbers(SqlConnection c, StoreInfo s)
        {
            var own = new HashSet<string>();
            foreach (var n in new[] { s.WaNumber, s.OwnerPhone }) { var p = Norm(n); if (p != null) own.Add(p); }
            using (var cmd = Db.Cmd(c, "SELECT Number FROM dbo.WaLines WHERE StoreId=@s AND Number IS NOT NULL UNION SELECT Want FROM dbo.WaLines WHERE StoreId=@s AND Want IS NOT NULL", "@s", s.Id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) { var p = Norm(r[0] as string); if (p != null) own.Add(p); }
            return own;
        }

        // «ابعت عرض» to a group: not the ones who said «وقف» (Offers.Pick drops them), nor the ones who keep returning orders
        public static List<Offers.Who> Who(SqlConnection c, int storeId, string group)
        {
            var s = Db.StoreById(c, storeId);
            if (s == null || group == "back" || !Groups.Contains(group)) return new List<Offers.Who>();
            var now = DateTime.UtcNow;
            return Load(c, s).Where(p => In(p, group, now) && !p.Risky).Select(p => new Offers.Who { Phone = p.Phone, Name = p.Name }).ToList();
        }

        // ---------- orders in ----------
        public class OrderIn
        {
            public string Source, Id, Name, Phone, Customer, City, Currency, Items, Status; public decimal? Total; public DateTime At;
        }

        // The status an order gets when the store's system says @st: never over one the owner set by hand; cancelled (or refunded) once
        // it had shipped is a return
        const string NextStatus = @"CASE WHEN ByHand=1 THEN Status
            WHEN @st IN ('cancelled','refund') AND Status IN ('shipped','delivered','returned') THEN 'returned'
            WHEN @st='refund' THEN 'cancelled' ELSE @st END";

        // Adds the order or updates it. true = it's new.
        public static bool Upsert(SqlConnection c, int storeId, OrderIn o)
        {
            string phone = Norm(o.Phone);
            string sql = @"
UPDATE dbo.CrmOrders SET OrderName=ISNULL(@n, OrderName), Total=ISNULL(@t, Total), Currency=ISNULL(@cur, Currency), CheckedAt=SYSUTCDATETIME(),
  Phone=CASE WHEN Gone=1 THEN NULL ELSE ISNULL(@p, Phone) END, Customer=CASE WHEN Gone=1 THEN NULL ELSE ISNULL(@cu, Customer) END,
  City=CASE WHEN Gone=1 THEN NULL ELSE ISNULL(@ci, City) END, Items=CASE WHEN Gone=1 THEN NULL ELSE ISNULL(@i, Items) END,
  StatusAt=CASE WHEN Status <> " + NextStatus + @" THEN SYSUTCDATETIME() ELSE StatusAt END, Status=" + NextStatus + @"
 WHERE StoreId=@s AND Source=@src AND OrderId=@o;
IF @@ROWCOUNT = 0
BEGIN
  INSERT INTO dbo.CrmOrders(StoreId, Source, OrderId, OrderName, Phone, Customer, City, Total, Currency, Items, Status, OrderedAt, StatusAt, CheckedAt)
  VALUES(@s, @src, @o, @n, @p, @cu, @ci, @t, @cur, @i, CASE WHEN @st='refund' THEN 'returned' ELSE @st END, @at, SYSUTCDATETIME(), SYSUTCDATETIME());
  SELECT 1;
END
ELSE SELECT 0;";
            for (int attempt = 0; ; attempt++)
                try
                {
                    using (var cmd = Db.Cmd(c, sql, "@s", storeId, "@src", o.Source, "@o", Cut(o.Id, 64), "@n", Cut(Blank(o.Name), 40), "@p", phone, "@cu", Cut(Blank(o.Customer), 100),
                        "@ci", Cut(Blank(o.City), 80), "@t", (object)o.Total ?? DBNull.Value, "@cur", Cut(Blank(o.Currency), 8), "@i", Cut(Blank(o.Items), 300), "@st", o.Status, "@at", o.At))
                    {
                        bool isNew = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
                        Forget(storeId);
                        return isNew;
                    }
                }
                catch (SqlException ex) when ((ex.Number == 2601 || ex.Number == 2627) && attempt == 0) { }   // two deliveries at once: the other one added it
        }

        // rows changed (0 = we don't have this order)
        public static int SetStatus(SqlConnection c, int storeId, string source, string orderId, string st)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET StatusAt=CASE WHEN Status <> " + NextStatus + " THEN SYSUTCDATETIME() ELSE StatusAt END, Status=" + NextStatus + @",
                CheckedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Source=@src AND OrderId=@o", "@st", st, "@s", storeId, "@src", source, "@o", orderId))
            {
                int n = cmd.ExecuteNonQuery();
                if (n > 0) Forget(storeId);
                return n;
            }
        }

        // An order the store's system just told us about (a webhook, or the pull): saved, and checked for the returns alert when it's new
        static void Take(SqlConnection c, StoreInfo s, OrderIn o)
        {
            if (!Upsert(c, s.Id, o)) return;
            if (o.Status != "open" || o.At < DateTime.UtcNow.AddHours(-24)) return;
            var phone = Norm(o.Phone);
            if (phone == null || !AlertFor(c, s.Id, phone)) return;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET Alert='due' WHERE StoreId=@s AND Source=@src AND OrderId=@o AND Alert IS NULL", "@s", s.Id, "@src", o.Source, "@o", o.Id)) cmd.ExecuteNonQuery();
            int storeId = s.Id;
            HostingEnvironment.QueueBackgroundWorkItem(ct => SendAlerts(storeId, ct));
        }

        // ---------- Shopify ----------
        // orders/create (ShopifyWebhookHandler, the HMAC already checked)
        public static void OnShopifyOrder(SqlConnection c, StoreInfo s, byte[] body)
        {
            try
            {
                if (s == null || s.Status != "active") return;
                JsonElement o;
                using (var doc = JsonDocument.Parse(body)) o = doc.RootElement.Clone();
                string id = J(o, "id");
                if (string.IsNullOrEmpty(id) || B(o, "test")) return;
                using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.CrmOrders WHERE StoreId=@s AND Source='shopify' AND OrderId=@o", "@s", s.Id, "@o", id))
                    if ((int)cmd.ExecuteScalar() > 0) return;   // Shopify may repeat a webhook days later: what the pull learnt since stays
                var ship = O(o, "shipping_address"); var bill = O(o, "billing_address"); var cust = O(o, "customer");
                string country = J(ship, "country_code") ?? J(bill, "country_code");
                string phone = new[] { J(ship, "phone"), J(o, "phone"), J(bill, "phone"), J(cust, "phone") }.Select(p => OrderConfirm.Phone(p, country)).FirstOrDefault(p => p != null);
                string full = (J(ship, "name") ?? ((J(cust, "first_name") ?? "") + " " + (J(cust, "last_name") ?? ""))).Trim();
                bool shipped = (J(o, "fulfillment_status") ?? "") == "fulfilled";
                string st = !string.IsNullOrEmpty(J(o, "cancelled_at")) ? (shipped ? "returned" : "cancelled") : shipped ? "shipped" : "open";
                Take(c, s, new OrderIn { Source = "shopify", Id = id, Name = J(o, "name"), Phone = phone, Customer = full, City = J(ship, "city"), Total = D(J(o, "total_price")),
                    Currency = Cur(J(o, "currency")), Items = string.Join("، ", Arr(o, "line_items").Take(5).Select(x => (J(x, "title") ?? J(x, "name") ?? "").Trim() + Times(J(x, "quantity"))).Where(x => x.Length > 0)),
                    Status = st, At = When(J(o, "created_at")) ?? DateTime.UtcNow });
            }
            catch (JsonException) { }
            catch (Exception ex) { Log.Error(ex); }   // never lose the rest of the webhook over this
        }

        const string OrderFields = "id name createdAt updatedAt cancelledAt displayFinancialStatus displayFulfillmentStatus {RET} tags paymentGatewayNames phone test " +
            "totalPriceSet { shopMoney { amount currencyCode } } shippingAddress { name phone countryCodeV2 city } billingAddress { name phone countryCodeV2 } " +
            "lineItems(first:4) { nodes { name quantity } } fulfillments(first:3) { displayStatus deliveredAt } ";

        static readonly ConcurrentDictionary<int, bool> Pulling = new ConcurrentDictionary<int, bool>();
        public static bool IsPulling(int storeId) { return Pulling.ContainsKey(storeId); }

        // What changed in Shopify's orders since the last pull (the first time: the last 60 days), oldest change first
        public static async Task PullShopify(int storeId, int maxPages, CancellationToken ct)
        {
            if (!Pulling.TryAdd(storeId, true)) return;
            try
            {
                StoreInfo s; DateTime? last; bool hooked;
                using (var c = Db.Open())
                {
                    s = Db.StoreById(c, storeId);
                    if (s == null || s.Status != "active" || !ShopifyAdmin.Has(s.ShopifyScopes, "read_orders")) return;
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CrmTryAt=SYSUTCDATETIME() OUTPUT INSERTED.CrmShopAt, INSERTED.CrmHooked WHERE Id=@s", "@s", storeId))
                    using (var r = cmd.ExecuteReader()) { r.Read(); last = Utc(r[0]); hooked = (bool)r[1]; }
                }
                var creds = ShopifyAdmin.For(s);
                if (creds == null) return;
                if (!hooked)
                {
                    // new orders the moment they're made (OrderConfirm and Carts ignore them while they're off)
                    string origin = EasyOrders.Origin(null);
                    if ((origin.Length > 0 || !string.IsNullOrEmpty(Web.Setting("ShopifyTestBase"))) && await ShopifyAdmin.RegisterOrderWebhook(creds, origin))
                        using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CrmHooked=1 WHERE Id=@s", "@s", storeId)) cmd.ExecuteNonQuery();
                }
                var started = DateTime.UtcNow;
                var since = last ?? started.AddDays(-HistoryDays);
                string after = null; bool ret = true; bool more = true;
                for (int page = 0; page < maxPages && more; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    string q = "query($q: String!, $after: String) { orders(first: 40, after: $after, sortKey: UPDATED_AT, query: $q) { pageInfo { hasNextPage endCursor } nodes { " +
                               OrderFields.Replace("{RET}", ret ? "returnStatus" : "") + "} } }";
                    ShopifyAdmin.Gql g;
                    try { g = await ShopifyAdmin.Query(creds, q, new { q = "updated_at:>='" + since.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "'", after }, ct); }
                    catch (ShopifyException ex) when (ret && ex.Message.Contains("returnStatus")) { ret = false; page--; continue; }   // an API version without it
                    JsonElement orders;
                    if (!g.Data.TryGetProperty("orders", out orders) || orders.ValueKind != JsonValueKind.Object)
                        throw new ShopifyException("Shopify مارجّعتش الأوردرات" + (g.Errors.Count > 0 ? ": " + g.Errors[0] : ""));
                    var nodes = Arr(orders, "nodes");
                    DateTime? upTo = null;
                    using (var c = Db.Open())
                    {
                        foreach (var o in nodes)
                        {
                            var x = FromShopify(o);
                            if (x != null) Take(c, s, x);
                            upTo = When(J(o, "updatedAt")) ?? upTo;
                        }
                        var info = O(orders, "pageInfo");
                        more = info.HasValue && B(info.Value, "hasNextPage");
                        after = J(info, "endCursor");
                        if (more && upTo.HasValue)
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CrmShopAt=@t WHERE Id=@s", "@t", upTo.Value, "@s", storeId)) cmd.ExecuteNonQuery();
                    }
                    if (g.Available < 300) await Task.Delay(1500, ct);
                }
                if (!more)
                    using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CrmShopAt=@t WHERE Id=@s", "@t", started.AddMinutes(-2), "@s", storeId)) cmd.ExecuteNonQuery();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!(ex is ShopifyException)) Log.Error(ex);
                // not again for an hour: a store whose Shopify doesn't answer mustn't take the others' turn
                try { using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CrmTryAt=DATEADD(MINUTE,55,SYSUTCDATETIME()) WHERE Id=@s", "@s", storeId)) cmd.ExecuteNonQuery(); } catch { }
            }
            finally { bool x; Pulling.TryRemove(storeId, out x); Forget(storeId); }
        }

        static readonly Regex ReturnTag = new Regex(@"\b(returned|rto|refused)\b|مرتجع|راجع|اترجع", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static OrderIn FromShopify(JsonElement o)
        {
            string id = J(o, "id"); if (id == null || B(o, "test")) return null;
            id = id.Substring(id.LastIndexOf('/') + 1);
            var ship = O(o, "shippingAddress"); var bill = O(o, "billingAddress");
            string country = J(ship, "countryCodeV2") ?? J(bill, "countryCodeV2");
            string phone = new[] { J(ship, "phone"), J(o, "phone"), J(bill, "phone") }.Select(p => OrderConfirm.Phone(p, country)).FirstOrDefault(p => p != null);
            var money = O(O(o, "totalPriceSet"), "shopMoney");
            JsonElement li;
            var items = o.TryGetProperty("lineItems", out li) ? Arr(li, "nodes") : new List<JsonElement>();

            string ful = J(o, "displayFulfillmentStatus") ?? "", fin = J(o, "displayFinancialStatus") ?? "", ret = J(o, "returnStatus") ?? "";
            var ships = Arr(o, "fulfillments").Select(f => J(f, "displayStatus") ?? "").ToList();
            bool moved = ships.Any(x => x == "IN_TRANSIT" || x == "OUT_FOR_DELIVERY" || x == "ATTEMPTED_DELIVERY" || x == "DELIVERED" || x == "FAILURE");
            bool shipped = moved || ful == "FULFILLED" || ful == "PARTIALLY_FULFILLED";
            bool delivered = ships.Contains("DELIVERED") || Arr(o, "fulfillments").Any(f => !string.IsNullOrEmpty(J(f, "deliveredAt")));
            bool cod = Strs(o, "paymentGatewayNames").Any(n => Regex.IsMatch(n, @"cash|\bcod\b|on delivery|الاستلام|كاش", RegexOptions.IgnoreCase));
            string st;
            if (Strs(o, "tags").Any(t => ReturnTag.IsMatch(t)) || ret == "RETURNED" || ret == "INSPECTION_COMPLETE" || ret == "IN_PROGRESS") st = "returned";
            else if (J(o, "cancelledAt") != null) st = shipped ? "returned" : "cancelled";
            else if (fin == "REFUNDED") st = shipped || ful == "RESTOCKED" ? "returned" : "cancelled";
            else if (ful == "RESTOCKED") st = "returned";
            else if (delivered || (cod && fin == "PAID" && shipped)) st = "delivered";   // cash on delivery that got paid reached the customer
            else st = shipped ? "shipped" : "open";
            return new OrderIn { Source = "shopify", Id = id, Name = J(o, "name"), Phone = phone, Customer = J(ship, "name") ?? J(bill, "name"), City = J(ship, "city"),
                Total = D(J(money, "amount")), Currency = Cur(J(money, "currencyCode")), Items = string.Join("، ", items.Select(x => (J(x, "name") ?? "").Trim() + Times(J(x, "quantity"))).Where(x => x.Length > 0)),
                Status = st, At = When(J(o, "createdAt")) ?? DateTime.UtcNow };
        }

        // ---------- EasyOrders ----------
        public static string EoStatus(string v)
        {
            switch (v)
            {
                case "in_delivery": return "shipped";
                case "delivered": return "delivered";
                case "returning_from_delivery": return "returned";
                case "request_refund": case "refund_in_progress": case "refunded": return "refund";
                case "canceled": return "cancelled";
                default: return "open";
            }
        }

        public static void QueueEo(int storeId, string orderId)
        {
            if (string.IsNullOrEmpty(orderId) || !Regex.IsMatch(orderId, "^[A-Za-z0-9-]{6,64}$")) return;
            HostingEnvironment.QueueBackgroundWorkItem(ct => ReadEo(storeId, orderId));
        }

        static async Task ReadEo(int storeId, string orderId)
        {
            try
            {
                StoreInfo s; using (var c = Db.Open()) s = Db.StoreById(c, storeId);
                var k = s == null || s.Status != "active" ? null : EasyOrders.For(s);
                if (k == null) return;
                JsonElement o;
                try { o = await EasyOrders.GetOrder(k, orderId); }
                catch (EoException ex)
                {
                    // gone from EasyOrders (or busy): looked at, so the re-check doesn't come back to it right away
                    if (ex.NotFound) using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET CheckedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Source='eo' AND OrderId=@o", "@s", storeId, "@o", orderId)) cmd.ExecuteNonQuery();
                    return;
                }
                using (var c = Db.Open()) FromEo(c, s, o);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // an order as EasyOrders gave it (EoConfirm reads it for the confirmation message too, and passes it here)
        public static void FromEo(SqlConnection c, StoreInfo s, JsonElement o)
        {
            try
            {
                string id = EasyOrders.Str(o, "id");
                if (string.IsNullOrEmpty(id)) return;
                string shortId = EasyOrders.Str(o, "short_id");
                Take(c, s, new OrderIn { Source = "eo", Id = id, Name = "#" + (shortId ?? id.Substring(0, Math.Min(8, id.Length))), Phone = OrderConfirm.Phone(EasyOrders.Str(o, "phone"), null),
                    Customer = EasyOrders.Str(o, "full_name"), City = EasyOrders.Str(o, "government"), Total = EasyOrders.Num(o, "total_cost"), Items = string.Join("، ", EasyOrders.ItemLines(o).Take(5)),
                    Status = EoStatus(EasyOrders.Str(o, "status")), At = When(EasyOrders.Str(o, "created_at")) ?? DateTime.UtcNow });
            }
            catch (Exception ex) { Log.Error(ex); }   // never stop the confirmation message over this
        }

        // the order-status-update webhook (its URL carries the store's key)
        public static void OnEoStatus(SqlConnection c, StoreInfo s, string orderId, string newStatus)
        {
            if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(newStatus)) return;
            try { if (SetStatus(c, s.Id, "eo", orderId, EoStatus(newStatus)) == 0) QueueEo(s.Id, orderId); }
            catch (Exception ex) { Log.Error(ex); }
        }

        // ---------- in the background (Jobs) ----------
        public static async Task Run(CancellationToken ct)
        {
            await SendAlerts(null, ct);
            // Shopify stores whose last pull is old (the first one starts when the owner opens «زبايني»)
            var shops = new List<int>(); var eo = new List<Tuple<int, string>>();
            using (var c = Db.Open())
            {
                using (var cmd = Db.Cmd(c, @"SELECT TOP 3 Id FROM dbo.Stores WHERE Status='active' AND ShopifyClientId IS NOT NULL AND ShopifyScopes LIKE '%_orders%'
                    AND (CrmTryAt IS NULL OR CrmTryAt < DATEADD(MINUTE,-4,SYSUTCDATETIME())) AND CrmShopAt < DATEADD(HOUR,-@h,SYSUTCDATETIME())
                    ORDER BY ISNULL(CrmTryAt, '2000-01-01')", "@h", PullHours))
                using (var r = cmd.ExecuteReader()) while (r.Read()) shops.Add((int)r[0]);
                // EasyOrders: open orders not heard of for two days are read again, a few at a time
                using (var cmd = Db.Cmd(c, @"SELECT TOP 30 x.StoreId, x.OrderId FROM (SELECT o.StoreId, o.OrderId, o.CheckedAt, ROW_NUMBER() OVER (PARTITION BY o.StoreId ORDER BY ISNULL(o.CheckedAt,'2000-01-01'), o.Id) AS K
                      FROM dbo.CrmOrders o JOIN dbo.Stores s ON s.Id=o.StoreId WHERE o.Source='eo' AND o.ByHand=0 AND o.Gone=0 AND o.Status IN ('open','shipped') AND s.EoKey IS NOT NULL AND s.Status='active'
                        AND o.OrderedAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) AND (o.CheckedAt IS NULL OR o.CheckedAt < DATEADD(HOUR,-48,SYSUTCDATETIME()))) x
                    WHERE x.K <= 10 ORDER BY ISNULL(x.CheckedAt,'2000-01-01')", "@d", HistoryDays))
                using (var r = cmd.ExecuteReader()) while (r.Read()) eo.Add(Tuple.Create((int)r[0], (string)r[1]));
            }
            foreach (var id in shops) { if (ct.IsCancellationRequested) return; await PullShopify(id, 8, ct); }
            foreach (var x in eo)
            {
                if (ct.IsCancellationRequested) return;
                // claimed first, so an order EasyOrders can't give us isn't asked for on every run
                using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET CheckedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Source='eo' AND OrderId=@o", "@s", x.Item1, "@o", x.Item2)) cmd.ExecuteNonQuery();
                await ReadEo(x.Item1, x.Item2);
            }
        }

        // ---------- the returns alert ----------
        public class History { public int Delivered, Returned; public string Mark; public bool Risky { get { return Mark == "block" || IsRisky(Returned, Delivered); } } }

        public static History Of(SqlConnection c, int storeId, string phone)
        {
            var h = new History();
            using (var cmd = Db.Cmd(c, @"SELECT ISNULL(SUM(CASE WHEN Status='delivered' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN Status='returned' THEN 1 ELSE 0 END),0),
                (SELECT TOP 1 Mark FROM dbo.CrmNotes WHERE StoreId=@s AND Phone=@p) FROM dbo.CrmOrders WHERE StoreId=@s AND Phone=@p", "@s", storeId, "@p", phone))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) { h.Delivered = Convert.ToInt32(r[0]); h.Returned = Convert.ToInt32(r[1]); h.Mark = r[2] as string; }
            return h;
        }

        static bool AlertFor(SqlConnection c, int storeId, string phone) { return Of(c, storeId, phone).Risky; }

        // why this customer needs a look, in words ("" = nothing)
        public static string Why(History h)
        {
            var t = RiskText(h.Returned, h.Delivered);
            if (h.Mark == "block") return "إنت معلّم عليه «ماتشحنلوش»" + (t.Length > 0 ? "، و" + t + " قبل كده" : "");
            return t;
        }

        // a line for another alert about the same customer (Followup's «مردّش»); "" when there's nothing to say or the plan hasn't it
        public static string RiskLine(SqlConnection c, StoreInfo s, string phone)
        {
            try
            {
                var p = Norm(phone);
                if (p == null || !PlanGate.Allows(c, s, ReturnsFeature)) return "";
                var h = Of(c, s.Id, p);
                return h.Risky ? Why(h) : "";
            }
            catch (Exception ex) { Log.Error(ex); return ""; }
        }

        static async Task SendAlerts(int? onlyStore, CancellationToken ct)
        {
            try
            {
                var due = new List<Tuple<long, int>>();
                using (var c = Db.Open())
                {
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET Alert='late' WHERE Alert='due' AND OrderedAt < DATEADD(HOUR,-24,SYSUTCDATETIME())")) cmd.ExecuteNonQuery();
                    if (WaNotify.QuietUntil() != null) return;   // at night they wait for the morning (the dashboard shows them meanwhile)
                    using (var cmd = Db.Cmd(c, "SELECT TOP 30 Id, StoreId FROM dbo.CrmOrders WHERE Alert='due'" + (onlyStore.HasValue ? " AND StoreId=@s" : "") + " ORDER BY Id", "@s", onlyStore ?? 0))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(Tuple.Create((long)r[0], (int)r[1]));
                }
                foreach (var d in due)
                {
                    if (ct.IsCancellationRequested) return;
                    using (var c = Db.Open()) await SendAlert(c, d.Item1, d.Item2);
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        static async Task SendAlert(SqlConnection c, long id, int storeId)
        {
            // claimed: two runs never send it twice
            using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET Alert='shown' WHERE Id=@id AND Alert='due'", "@id", id)) if (cmd.ExecuteNonQuery() == 0) return;
            var s = Db.StoreById(c, storeId);
            if (s == null || s.Status != "active" || !AlertOn(c, storeId) || !PlanGate.Allows(c, s, ReturnsFeature)) return;
            string name, phone, customer, total, src, status;
            using (var cmd = Db.Cmd(c, "SELECT OrderName, Phone, Customer, Total, Currency, Source, Status FROM dbo.CrmOrders WHERE Id=@id", "@id", id))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return;
                name = r["OrderName"] as string ?? ""; phone = r["Phone"] as string; customer = (r["Customer"] as string ?? "").Trim(); src = r["Source"] as string; status = r["Status"] as string;
                total = r["Total"] == DBNull.Value ? "" : Money((decimal)r["Total"], r["Currency"] as string);
            }
            if (phone == null || status != "open") return;
            var h = Of(c, storeId, phone);
            if (!h.Risky) return;
            string origin = EasyOrders.Origin(null);
            var text = new StringBuilder("⚠️ *خد بالك من الأوردر ده قبل ما تشحنه*\n")
                .Append("🧾 طلب ").Append(name).Append(customer.Length > 0 ? " · " + customer : "").Append(total.Length > 0 ? " · " + total : "").Append(" (").Append(SourceName(src)).Append(")\n")
                .Append("↩️ الزبون ده *").Append(Why(h)).Append("*").Append(h.Mark == "block" ? "." : " عندك قبل كده.").Append("\n\n")
                .Append("📱 رقمه: +").Append(phone).Append('\n')
                .Append("👈 كلّمه واتساب: https://wa.me/").Append(phone).Append('\n')
                .Append(origin.Length > 0 ? "👤 ملفه: " + origin + "/dashboard.html#customers/" + phone + "\n" : "")
                .Append("\n💡 أكّد معاه بمكالمة، أو اطلب مصاريف الشحن مقدم قبل ما تشحن.").ToString();
            var sent = await Team.Send(c, s, text, "sb-crm-" + id);
            string to = sent.Error != null ? "" : Team.Who(sent);
            using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET Alert=@a, AlertNote=@n WHERE Id=@id AND Alert='shown'", "@a", sent.Error == null ? "sent" : "failed",
                "@n", Cut(sent.Error ?? ("اتبلّغ " + (to == "انت" ? "صاحب المتجر" : to)), 200), "@id", id)) cmd.ExecuteNonQuery();
        }

        public static bool AlertOn(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT CrmAlert FROM dbo.Stores WHERE Id=@s", "@s", storeId)) { var v = cmd.ExecuteScalar(); return v is bool && (bool)v; }
        }

        // For the order-confirmation lists: the customer's number (to open the file) and, when the plan has it, why to be careful
        public static void Decorate(SqlConnection c, StoreInfo s, List<Dictionary<string, object>> rows)
        {
            try
            {
                if (rows.Count == 0 || !PlanGate.Allows(c, s, ReturnsFeature)) return;
                Func<Dictionary<string, object>, string> phone = row => { object v; return row.TryGetValue("phone", out v) ? Norm(v as string) : null; };
                var phones = rows.Select(phone).Where(p => p != null).Distinct().Take(60).ToList();
                if (phones.Count == 0) return;
                var map = new Dictionary<string, History>();
                var ps = new List<object> { "@s", s.Id };
                for (int i = 0; i < phones.Count; i++) { ps.Add("@p" + i); ps.Add(phones[i]); }
                string list = string.Join(",", phones.Select((p, i) => "@p" + i));
                using (var cmd = Db.Cmd(c, @"SELECT x.Phone, SUM(x.De) AS De, SUM(x.Re) AS Re, MAX(x.Mark) AS Mark FROM (
                      SELECT Phone, CASE WHEN Status='delivered' THEN 1 ELSE 0 END AS De, CASE WHEN Status='returned' THEN 1 ELSE 0 END AS Re, CAST(NULL AS NVARCHAR(10)) AS Mark FROM dbo.CrmOrders WHERE StoreId=@s AND Phone IN (" + list + @")
                      UNION ALL SELECT Phone, 0, 0, Mark FROM dbo.CrmNotes WHERE StoreId=@s AND Phone IN (" + list + ")) x GROUP BY x.Phone", ps.ToArray()))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) map[(string)r["Phone"]] = new History { Delivered = Convert.ToInt32(r["De"]), Returned = Convert.ToInt32(r["Re"]), Mark = r["Mark"] as string };
                foreach (var row in rows) { History h; var p = phone(row); if (p != null && map.TryGetValue(p, out h) && h.Risky) row["risk"] = Why(h); }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // ---------- what the page shows ----------
        public static Dictionary<string, object> RowJson(Person p, DateTime now, bool groups, bool returns)
        {
            return new Dictionary<string, object> {
                { "phone", p.Phone }, { "name", p.Name ?? "" }, { "mark", p.Mark ?? "" }, { "note", !string.IsNullOrWhiteSpace(p.Note) }, { "city", p.City ?? "" },
                { "orders", p.Orders }, { "kept", p.Kept }, { "open", p.Open }, { "delivered", p.Delivered }, { "returned", p.Returned }, { "cancelled", p.Cancelled },
                { "spent", p.Spent }, { "currency", p.Currency ?? "" }, { "lastOrder", Iso(p.LastOrder) }, { "lastAt", Iso(p.LastAt) }, { "waAt", Iso(p.WaAt) }, { "ch", p.Ch.ToArray() },
                { "groups", groups ? Groups.Where(g => g != "back" && In(p, g, now)).ToArray() : new string[0] },
                { "risk", returns && p.Risky ? Why(new History { Delivered = p.Delivered, Returned = p.Returned, Mark = p.Mark }) : "" }, { "stopped", p.Stopped } };
        }

        // Arabic letters people write in different ways become one; digits become 0-9
        public static string Fold(string t)
        {
            t = ShopifyAdmin.ToLatinDigits(t ?? "").ToLowerInvariant().Replace("ـ", "");
            t = Regex.Replace(t, "[ً-ْ]", "").Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي').Replace('ؤ', 'و').Replace('ئ', 'ي');
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        public static IEnumerable<Person> Filter(SqlConnection c, StoreInfo s, List<Person> all, string group, string mark, string q, string sort)
        {
            var now = DateTime.UtcNow;
            IEnumerable<Person> list = all;
            if (!string.IsNullOrEmpty(group)) list = list.Where(p => In(p, group, now));
            if (!string.IsNullOrEmpty(mark)) list = list.Where(p => p.Mark == mark);
            var f = Fold(q);
            if (f.Length > 0)
            {
                var digits = Regex.Replace(f, @"\D", "");
                // a number as people write it: 0100…, +20100…, 100…
                var ph = digits.TrimStart('0'); if (ph.StartsWith("20")) ph = ph.Substring(2); ph = ph.TrimStart('0');
                var byOrder = new HashSet<string>();
                if (digits.Length > 0 && digits.Length <= 12)
                    using (var cmd = Db.Cmd(c, "SELECT DISTINCT TOP 200 Phone FROM dbo.CrmOrders WHERE StoreId=@s AND Phone IS NOT NULL AND REPLACE(OrderName,'#','') = @o", "@s", s.Id, "@o", digits))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) { var p = Norm(r[0] as string); if (p != null) byOrder.Add(p); }
                list = list.Where(p => Fold(p.Name).Contains(f) || (ph.Length >= 3 && p.Phone.Contains(ph)) || byOrder.Contains(p.Phone) || (p.Note != null && Fold(p.Note).Contains(f)) || (p.City != null && Fold(p.City) == f));
            }
            switch (sort)
            {
                case "spent": return list.OrderByDescending(p => p.Spent).ThenByDescending(p => p.LastAt);
                case "orders": return list.OrderByDescending(p => p.Kept).ThenByDescending(p => p.Spent);
                case "returns": return list.OrderByDescending(p => p.Returned).ThenByDescending(p => p.LastAt);
                case "old": return list.OrderBy(p => p.LastAt ?? DateTime.MaxValue);
                default: return list.OrderByDescending(p => p.LastAt ?? DateTime.MinValue);
            }
        }

        // group -> how many, and how many of them an offer reaches
        public static Dictionary<string, object> GroupJson(List<Person> all)
        {
            var now = DateTime.UtcNow; var d = new Dictionary<string, object>();
            foreach (var g in Groups)
            {
                var inG = all.Where(p => In(p, g, now)).ToList();
                d[g] = new Dictionary<string, object> { { "n", inG.Count }, { "send", g == "back" ? 0 : inG.Count(p => !p.Stopped && !p.Risky) } };
            }
            return d;
        }

        // new orders from people who return: shown until they ship or the owner says they checked
        public static List<Dictionary<string, object>> Watch(SqlConnection c, StoreInfo s, List<Person> all)
        {
            var risky = all.Where(p => p.Risky).ToDictionary(p => p.Phone);
            var list = new List<Dictionary<string, object>>();
            if (risky.Count == 0) return list;
            using (var cmd = Db.Cmd(c, @"SELECT TOP 300 Id, OrderName, Phone, Customer, Total, Currency, Items, Source, OrderedAt, Alert, AlertNote FROM dbo.CrmOrders
                WHERE StoreId=@s AND Status='open' AND Phone IS NOT NULL AND ISNULL(Alert,'') <> 'ok' AND OrderedAt > DATEADD(DAY,-7,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", s.Id))
            using (var r = cmd.ExecuteReader())
                while (r.Read() && list.Count < 20)
                {
                    Person p; if (!risky.TryGetValue(Norm(r["Phone"] as string) ?? "", out p)) continue;
                    list.Add(new Dictionary<string, object> { { "id", r["Id"] }, { "order", r["OrderName"] as string ?? "" }, { "phone", p.Phone }, { "name", p.Name ?? (r["Customer"] as string ?? "") },
                        { "total", r["Total"] == DBNull.Value ? "" : Money((decimal)r["Total"], r["Currency"] as string) }, { "items", r["Items"] as string ?? "" }, { "source", r["Source"] },
                        { "at", Iso(Utc(r["OrderedAt"])) }, { "risk", Why(new History { Delivered = p.Delivered, Returned = p.Returned, Mark = p.Mark }) },
                        { "told", (r["Alert"] as string) == "sent" ? r["AlertNote"] as string ?? "" : "" } });
                }
            return list;
        }

        public static Dictionary<string, object> State(SqlConnection c, StoreInfo s, string group, string mark, string q, string sort, int page)
        {
            bool groups = Shown(c, s, GroupsFeature), returns = Shown(c, s, ReturnsFeature);
            if (!groups && group != "back") group = ""; if (!returns && group == "back") group = "";
            var all = Load(c, s);
            var now = DateTime.UtcNow;
            var list = Filter(c, s, all, group, mark, q, sort).ToList();
            page = Math.Max(0, Math.Min(page, 5000));
            bool shop = ShopifyAdmin.For(s) != null, eo = EasyOrders.Connected(s), scope = ShopifyAdmin.Has(s.ShopifyScopes, "read_orders");
            DateTime? shopAt; int orders, noPhone, del60, ret60; decimal avg; string cur;
            using (var cmd = Db.Cmd(c, @"SELECT (SELECT CrmShopAt FROM dbo.Stores WHERE Id=@s), COUNT(*), ISNULL(SUM(CASE WHEN Phone IS NULL AND Gone=0 THEN 1 ELSE 0 END),0),
                ISNULL(SUM(CASE WHEN Status='delivered' AND OrderedAt > DATEADD(DAY,-60,SYSUTCDATETIME()) THEN 1 ELSE 0 END),0),
                ISNULL(SUM(CASE WHEN Status='returned' AND OrderedAt > DATEADD(DAY,-60,SYSUTCDATETIME()) THEN 1 ELSE 0 END),0),
                ISNULL(AVG(CASE WHEN Status IN ('open','shipped','delivered') AND Total > 0 THEN Total END),0), MIN(ISNULL(Currency,N'ج.م')), MAX(ISNULL(Currency,N'ج.م')) FROM dbo.CrmOrders WHERE StoreId=@s", "@s", s.Id))
            using (var r = cmd.ExecuteReader()) { r.Read(); shopAt = Utc(r[0]); orders = Convert.ToInt32(r[1]); noPhone = Convert.ToInt32(r[2]); del60 = Convert.ToInt32(r[3]); ret60 = Convert.ToInt32(r[4]);
                cur = r[7] as string; avg = cur != null && cur == r[6] as string ? Convert.ToDecimal(r[5]) : 0; }   // orders in two currencies have no one average
            var team = Team.Members(c, s);
            return new Dictionary<string, object> {
                { "total", list.Count }, { "page", page }, { "more", list.Count > (page + 1) * PageSize },
                { "rows", list.Skip(page * PageSize).Take(PageSize).Select(p => RowJson(p, now, groups, returns)).ToList() },
                { "all", all.Count }, { "group", group ?? "" },
                { "groups", groups || returns ? GroupJson(all) : new Dictionary<string, object>() }, { "canGroups", groups }, { "canReturns", returns },
                { "marks", Marks.ToDictionary(m => m, m => (object)all.Count(p => p.Mark == m)) },
                { "watch", returns ? (object)Watch(c, s, all) : new object[0] },
                { "returns", new Dictionary<string, object> { { "delivered", del60 }, { "returned", ret60 } } },
                { "orders", orders }, { "noPhone", noPhone }, { "avgOrder", Math.Round(avg) }, { "avgCurrency", cur ?? "" },   // the guide's example
                { "shopify", shop }, { "shopifyScope", scope }, { "shopAt", Iso(shopAt) }, { "pulling", IsPulling(s.Id) || (shop && scope && shopAt == null) }, { "eo", eo },
                { "whatsapp", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber) }, { "service", WaAccount.HasService(s) },
                { "alert", new Dictionary<string, object> { { "on", AlertOn(c, s.Id) }, { "team", team.Select(m => m.Name).ToList() }, { "owner", s.OwnerPhone ?? "" }, { "cant", WaNotify.CantSend(c, s) ?? "" } } } };
        }

        // the feature is part of what this store's dashboard draws (an ended plan keeps its pages)
        public static bool Shown(SqlConnection c, StoreInfo s, string feature) { return !PlanGate.Hidden(c, s).Contains(feature); }

        // ---------- one customer's file ----------
        public static Dictionary<string, object> File(SqlConnection c, StoreInfo s, string phone)
        {
            bool groups = Shown(c, s, GroupsFeature), returns = Shown(c, s, ReturnsFeature);
            var now = DateTime.UtcNow;
            var p = Load(c, s).FirstOrDefault(x => x.Phone == phone);
            if (p == null) return null;
            var d = RowJson(p, now, groups, returns);
            d["noteText"] = p.Note ?? ""; d["firstOrder"] = Iso(p.FirstOrder);
            d["cart"] = p.CartAt.HasValue ? new Dictionary<string, object> { { "items", p.CartItems ?? "" }, { "total", p.CartTotal ?? "" }, { "at", Iso(p.CartAt) } } : null;
            d["wait"] = p.Waits > 0 ? p.WaitFor : "";
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 ByOwner FROM dbo.WaOptOuts WHERE StoreId=@s AND Phone=@p", "@s", s.Id, "@p", phone)) { var v = cmd.ExecuteScalar(); d["stopByOwner"] = v is bool && (bool)v; }

            var orders = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 60 Id, Source, OrderName, Total, Currency, Items, City, Status, ByHand, OrderedAt, StatusAt FROM dbo.CrmOrders WHERE StoreId=@s AND Phone=@p ORDER BY OrderedAt DESC, Id DESC", "@s", s.Id, "@p", phone))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    orders.Add(new Dictionary<string, object> { { "id", r["Id"] }, { "source", r["Source"] }, { "name", r["OrderName"] as string ?? "" }, { "total", r["Total"] == DBNull.Value ? "" : Money((decimal)r["Total"], r["Currency"] as string) },
                        { "items", r["Items"] as string ?? "" }, { "city", r["City"] as string ?? "" }, { "status", r["Status"] }, { "byHand", r["ByHand"] }, { "at", Iso(Utc(r["OrderedAt"])) } });
            d["orderList"] = orders;

            // what happened, newest first
            string tail = Tail(phone);
            var ev = new List<Tuple<DateTime, Dictionary<string, object>>>();
            Action<DateTime?, string, string, string> add = (at, kind, title, text) => { if (at.HasValue) ev.Add(Tuple.Create(at.Value, new Dictionary<string, object> { { "at", Iso(at) }, { "kind", kind }, { "title", title }, { "text", text ?? "" } })); };
            Action<string, Action<SqlDataReader>> each = (sql, row) => { using (var cmd = Db.Cmd(c, sql, "@s", s.Id, "@p", phone, "@t", tail)) using (var r = cmd.ExecuteReader()) while (r.Read()) row(r); };
            each("SELECT TOP 20 Items, Total, Status, CreatedAt, SentAt FROM dbo.CartReminders WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t ORDER BY Id DESC", r =>
            {
                var st = r["Status"] as string;
                add(Utc(r["CreatedAt"]), "cart", st == "ordered" || st == "recovered" ? "ساب سلة ورجع اشتراها" : "ساب سلة" + (st == "sent" ? "، واتبعتله تذكير" : ""), (r["Items"] as string) + (r["Total"] is string ? " · " + r["Total"] : ""));
            });
            each("SELECT TOP 20 Product, Variant, Status, CreatedAt FROM dbo.RestockWaits WHERE StoreId=@s AND RIGHT(Phone,9)=@t ORDER BY Id DESC", r =>
                add(Utc(r["CreatedAt"]), "wait", (r["Status"] as string) == "sent" ? "طلب نبلّغه لما يرجع، واتبلّغ" : (r["Status"] as string) == "waiting" ? "مستني المنتج يرجع" : "طلب نبلّغه لما يرجع", (r["Product"] as string) + (r["Variant"] is string ? " (" + r["Variant"] + ")" : "")));
            each("SELECT TOP 20 Reason, Channel, CreatedAt, ToName FROM dbo.Handoffs WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t ORDER BY Id DESC", r =>
                add(Utc(r["CreatedAt"]), "hand", "طلب يكلّم حد من المتجر" + (r["ToName"] is string ? " (راح لـ " + r["ToName"] + ")" : ""), r["Reason"] as string));
            each(@"SELECT TOP 20 b.Text, x.SentAt, x.StoppedAt FROM dbo.BcSends x JOIN dbo.Broadcasts b ON b.Id=x.BroadcastId WHERE x.StoreId=@s AND x.Channel='wa' AND x.Phone=@p AND x.SentAt IS NOT NULL ORDER BY x.Id DESC", r =>
                add(Utc(r["SentAt"]), "offer", "اتبعتله عرض" + (r["StoppedAt"] != DBNull.Value ? "، وبعت «وقف»" : ""), Cut(((string)r["Text"]).Split('\n')[0], 90)));
            foreach (var t in new[] { "OrderConfirms", "EoConfirms" })
                each("SELECT TOP 20 OrderName, Status, AnsweredAt FROM dbo." + t + " WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t AND AnsweredAt IS NOT NULL ORDER BY Id DESC", r =>
                    add(Utc(r["AnsweredAt"]), "confirm", (r["Status"] as string) == "cancelled" ? "لغى طلب " + r["OrderName"] + " من رسالة التأكيد" : "أكّد طلب " + r["OrderName"] + " على الواتساب", ""));
            each("SELECT CreatedAt FROM dbo.WaOptOuts WHERE StoreId=@s AND Phone=@p", r => add(Utc(r[0]), "stop", "مابقاش يوصله رسايل ولا عروض", ""));
            d["events"] = ev.OrderByDescending(x => x.Item1).Take(40).Select(x => x.Item2).ToList();

            // the last of the WhatsApp chat (kept 60 days)
            var chat = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 14 FromCustomer, Body, MsgId, CreatedAt FROM dbo.WaMessages WHERE StoreId=@s AND ChatId=@p ORDER BY Id DESC", "@s", s.Id, "@p", phone))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    chat.Add(new Dictionary<string, object> { { "me", !(bool)r["FromCustomer"] }, { "hand", (r["MsgId"] as string ?? "").StartsWith("owner:") }, { "text", Cut((string)r["Body"], 500) }, { "at", Iso(Utc(r["CreatedAt"])) } });
            chat.Reverse();
            d["chat"] = chat;
            return d;
        }

        // ---------- the owner's own changes ----------
        public static void SaveNote(SqlConnection c, int storeId, string phone, string name, string note, string mark)
        {
            // null = leave as it is; "" = clear
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.CrmNotes SET Name=CASE WHEN @n IS NULL THEN Name ELSE NULLIF(@n,N'') END, Note=CASE WHEN @t IS NULL THEN Note ELSE NULLIF(@t,N'') END,
                  Mark=CASE WHEN @m IS NULL THEN Mark ELSE NULLIF(@m,'') END WHERE StoreId=@s AND Phone=@p;
                IF @@ROWCOUNT=0 INSERT INTO dbo.CrmNotes(StoreId, Phone, Name, Note, Mark) VALUES(@s, @p, NULLIF(@n,N''), NULLIF(@t,N''), NULLIF(@m,''));",
                "@s", storeId, "@p", phone, "@n", name, "@t", note, "@m", mark)) cmd.ExecuteNonQuery();
            Forget(storeId);
        }

        // Everything the store has about this number goes. Kept: «وقف» (so nothing is sent to them again) and the orders without who they
        // were (Gone: the store's system telling us about one again doesn't bring the customer back).
        public static void Erase(SqlConnection c, int storeId, string phone)
        {
            using (var cmd = Db.Cmd(c, @"
DELETE FROM dbo.CrmNotes WHERE StoreId=@s AND Phone=@p;
UPDATE dbo.CrmOrders SET Phone=NULL, Customer=NULL, City=NULL, Items=NULL, Gone=1, Alert=NULL, AlertNote=NULL WHERE StoreId=@s AND Phone=@p;
DELETE FROM dbo.BcContacts WHERE StoreId=@s AND RIGHT(Phone,9)=@t;
DELETE FROM dbo.WaMessages WHERE StoreId=@s AND ChatId=@p;
DELETE FROM dbo.CartReminders WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t;
DELETE FROM dbo.RestockWaits WHERE StoreId=@s AND RIGHT(Phone,9)=@t;
DELETE FROM dbo.Handoffs WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t;
UPDATE dbo.BcAsks SET Phone=NULL WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t;
UPDATE dbo.BcSends SET Status='skipped', Note=N'بيانات الزبون اتمسحت' WHERE StoreId=@s AND Channel='wa' AND Phone=@p AND Status='waiting';
UPDATE dbo.BcSends SET Name=NULL WHERE StoreId=@s AND Channel='wa' AND Phone=@p;
UPDATE dbo.OrderConfirms SET Phone=NULL, Customer=NULL WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t;
UPDATE dbo.EoConfirms SET Phone=NULL, Customer=NULL WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t;
UPDATE dbo.DeliveryNotices SET Phone=NULL, Customer=NULL WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t;
UPDATE dbo.ChatOrders SET Phone=NULL, Name=NULL, Address=NULL WHERE StoreId=@s AND Phone IS NOT NULL AND RIGHT(Phone,9)=@t AND Status NOT IN ('draft','code_sent','placing');",
                "@s", storeId, "@p", phone, "@t", Tail(phone))) { cmd.CommandTimeout = 60; cmd.ExecuteNonQuery(); }
            Forget(storeId);
        }

        // ---------- Excel out ----------
        public static byte[] Export(IEnumerable<Person> people)
        {
            var head = new[] { "الاسم", "الموبايل", "المدينة", "عدد الأوردرات", "اتسلم", "رجع", "اتلغى", "قيمة أوردراته", "آخر أوردر", "آخر نشاط", "العلامة", "ملاحظتك", "وقف الرسايل" };
            var marks = new Dictionary<string, string> { { "star", "مهم" }, { "follow", "محتاج متابعة" }, { "whole", "جملة" }, { "block", "ماتشحنلوش" } };
            using (var ms = new MemoryStream())
            {
                using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
                {
                    var wb = doc.AddWorkbookPart(); wb.Workbook = new X.Workbook();
                    var ws = wb.AddNewPart<WorksheetPart>();
                    var data = new X.SheetData();
                    ws.Worksheet = new X.Worksheet(new X.SheetViews(new X.SheetView { WorkbookViewId = 0, RightToLeft = true }), new X.SheetFormatProperties { DefaultRowHeight = 16 },
                        new X.Columns(new X.Column { Min = 1, Max = (uint)head.Length, Width = 18, CustomWidth = true }), data);
                    wb.Workbook.AppendChild(new X.Sheets(new X.Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "زبايني" }));
                    Func<string, X.Cell> text = v => new X.Cell { DataType = X.CellValues.InlineString, InlineString = new X.InlineString(new X.Text(Clean(v)) { Space = SpaceProcessingModeValues.Preserve }) };
                    Func<decimal, X.Cell> num = v => new X.Cell { DataType = X.CellValues.Number, CellValue = new X.CellValue(v.ToString("0.##", CultureInfo.InvariantCulture)) };
                    data.Append(new X.Row(head.Select(text)));
                    foreach (var p in people)
                    {
                        string m; marks.TryGetValue(p.Mark ?? "", out m);
                        data.Append(new X.Row(text(p.Name), text(Local(p.Phone)), text(p.City), num(p.Orders), num(p.Delivered), num(p.Returned), num(p.Cancelled), num(p.Spent),
                            text(Day(p.LastOrder)), text(Day(p.LastAt)), text(m), text(p.Note), text(p.Stopped ? "أيوه" : "")));
                    }
                    wb.Workbook.Save();
                }
                return ms.ToArray();
            }
        }

        // what a cell may hold (XML 1.0)
        static string Clean(string v) { return new string((v ?? "").Where(ch => ch >= 0x20 || ch == '\n' || ch == '\t').ToArray()); }
        static string Day(DateTime? d) { return d.HasValue ? d.Value.ToString("yyyy-MM-dd") : ""; }
        static string Local(string p) { return p.Length == 12 && p.StartsWith("20") ? "0" + p.Substring(2) : "+" + p; }

        // ---------- old orders in (Excel / CSV the owner exported from the store or the shipping company) ----------
        public class ImportResult { public int Rows, Added, Updated, NoPhone, NoDate; public string Error; public string[] Missing; }

        static readonly Dictionary<string, Regex[]> Heads = new Dictionary<string, Regex[]> {
            // each field: the surest names first
            { "phone", Rx(@"^(shipping |customer |receiver |consignee |client )?(phone|mobile)( ?(number|no\.?))?( ?1)?$|^phone|mobile", "^(رقم )?(الموبايل|موبايل|التليفون|تليفون|التلفون|تلفون|الهاتف|هاتف|المحمول|الجوال)( (العميل|الزبون|المستلم))?( ?1)?$", "(رقم|موبايل|تليفون|هاتف) (العميل|الزبون|المستلم)|واتس|whats") },
            { "date", Rx(@"^created( ?at| ?date)?$|^(order |creation )?date( created)?$|^date", "^(تاريخ|التاريخ)( (الطلب|الاوردر|الانشاء|الشحن))?$", "تاريخ|date|created") },
            { "name", Rx(@"^(shipping|customer|receiver|consignee|client|full)[ _]?name$|^(customer|receiver|consignee|client|full_name)$", "^(اسم )?(العميل|الزبون|المستلم)$|^الاسم( بالكامل)?$|^اسم$", "^(billing )?name$") },
            { "order", Rx(@"^(order|short)[ _]?(id|no\.?|num|number|name|code)$|^(order|id|#)$", "^(رقم|كود) (الطلب|الاوردر|الشحنه|البوليصه)$|^(الاوردر|الطلب)$", "tracking|awb|reference") },
            { "total", Rx(@"^(order |grand )?total( ?(price|cost|amount))?$|^(total_cost|cod( amount)?|amount|price)$", "^(الاجمالي|اجمالي|المبلغ|قيمه|القيمه|السعر)( (الطلب|الاوردر|التحصيل|الفاتوره|المطلوب))?$", "total|amount|اجمالي|مبلغ") },
            { "status", Rx(@"^(order |shipment |delivery |shipping )?(status|state)$", "^(حاله|الحاله)( (الطلب|الاوردر|الشحنه))?$", "status|حاله") },
            { "items", Rx(@"^(lineitem name|products?|items?|product name|cart_items)$", "^(المنتج|المنتجات|الصنف|الاصناف|اسم المنتج)$") },
            { "city", Rx(@"^(shipping )?(city|government|governorate|province)$", "^(المحافظه|المدينه|محافظه|مدينه)$") },
            { "cancelled", Rx(@"^cancell?ed( ?at)$") }, { "fulfil", Rx(@"^fulfillment status$") }, { "financial", Rx(@"^financial status$") } };
        static Regex[] Rx(params string[] p) { return p.Select(x => new Regex(x, RegexOptions.IgnoreCase)).ToArray(); }

        public static ImportResult Import(SqlConnection c, StoreInfo s, byte[] data, string ext)
        {
            var res = new ImportResult();
            var rows = ext == ".xlsx" ? Sheet(data) : Csv(Extract.PlainText(data));
            // the header row: the first one that names a phone column
            int head = -1; Dictionary<string, int> col = null;
            for (int i = 0; i < Math.Min(rows.Count, 8) && head < 0; i++) { col = Columns(rows[i]); if (col.ContainsKey("phone")) head = i; }
            if (head < 0) { res.Missing = new[] { "رقم الموبايل", "التاريخ" }; return res; }
            if (!col.ContainsKey("date")) { res.Missing = new[] { "التاريخ" }; return res; }
            Func<string[], string, string> cell = (r, f) => { int i; return col.TryGetValue(f, out i) && i < r.Length ? (r[i] ?? "").Trim() : ""; };

            // one order may take several rows (a row per product): the first value of each field wins
            var orders = new Dictionary<string, OrderIn>(); var seq = new List<OrderIn>();
            foreach (var r in rows.Skip(head + 1))
            {
                if (r.All(string.IsNullOrWhiteSpace)) continue;
                res.Rows++;
                string num = cell(r, "order").TrimStart('#').Trim();
                OrderIn same;
                string phone = Norm(cell(r, "phone"));
                bool again = num.Length > 0 && orders.TryGetValue(num, out same);
                if (again && (phone == null || phone == orders[num].Phone)) { if (string.IsNullOrEmpty(orders[num].Items)) orders[num].Items = cell(r, "items"); continue; }
                if (phone == null) { res.NoPhone++; continue; }
                var at = ReadDate(cell(r, "date"));
                if (at == null) { res.NoDate++; continue; }
                var o = new OrderIn { Source = "excel", Name = num.Length > 0 ? "#" + num : "", Phone = phone, Customer = cell(r, "name"), City = cell(r, "city"), Items = cell(r, "items"), Total = ReadMoney(cell(r, "total")), At = at.Value };
                string fin = cell(r, "financial").ToLowerInvariant();
                o.Status = cell(r, "cancelled").Length > 0 ? (cell(r, "fulfil").ToLowerInvariant() == "fulfilled" ? "returned" : "cancelled")
                    : fin == "refunded" || fin == "voided" ? (cell(r, "fulfil").ToLowerInvariant() == "fulfilled" ? "returned" : "cancelled")
                    : ReadStatus(cell(r, "status").Length > 0 ? cell(r, "status") : cell(r, "fulfil"));
                // a number that repeats for someone else isn't an order number
                o.Id = num.Length > 0 && !again ? num : Hash(num + "|" + phone + "|" + o.At.ToString("yyyyMMdd") + "|" + o.Total + "|" + o.Items);
                if (num.Length > 0 && !again) orders[num] = o;
                seq.Add(o);
            }
            if (seq.Count == 0) return res;

            // orders we already have from the store itself: only their status is learnt (an open one becomes what the file says)
            var have = new Dictionary<string, long>();
            using (var cmd = Db.Cmd(c, "SELECT Id, REPLACE(OrderName,'#','') FROM dbo.CrmOrders WHERE StoreId=@s AND Source IN ('shopify','eo') AND OrderName IS NOT NULL", "@s", s.Id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) have[r[1] as string ?? ""] = (long)r[0];
            foreach (var o in seq)
            {
                long id;
                if (o.Name.Length > 0 && have.TryGetValue(o.Name.TrimStart('#'), out id))
                {
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET Status=@st, StatusAt=SYSUTCDATETIME() WHERE Id=@id AND StoreId=@s AND Phone=@p AND ByHand=0 AND Status IN ('open','shipped') AND @st <> 'open'",
                        "@st", o.Status, "@id", id, "@s", s.Id, "@p", o.Phone)) if (cmd.ExecuteNonQuery() > 0) res.Updated++;
                    continue;
                }
                if (Upsert(c, s.Id, o)) res.Added++; else res.Updated++;
            }
            Forget(s.Id);
            return res;
        }

        static Dictionary<string, int> Columns(string[] row)
        {
            var col = new Dictionary<string, int>(); var used = new HashSet<int>();
            var heads = row.Select(h => Fold(h).Trim()).ToArray();
            // Shopify's own export calls the order number "Name"
            int nm = Array.IndexOf(heads, "name");
            if (nm >= 0 && heads.Contains("financial status")) { col["order"] = nm; used.Add(nm); }
            int deepest = Heads.Values.Max(v => v.Length);
            for (int level = 0; level < deepest; level++)
                foreach (var f in Heads)
                {
                    if (col.ContainsKey(f.Key) || level >= f.Value.Length) continue;
                    for (int i = 0; i < heads.Length; i++)
                        if (heads[i].Length > 0 && heads[i].Length < 40 && !used.Contains(i) && f.Value[level].IsMatch(heads[i])) { col[f.Key] = i; used.Add(i); break; }
                }
            return col;
        }

        // The words shipping companies and store systems use. The unhappy ones first: "undelivered" has "delivered" in it.
        public static string ReadStatus(string raw)
        {
            var t = Fold(raw).Replace('_', ' ').Replace('-', ' ');
            if (t.Length == 0) return "open";
            if (Regex.IsMatch(t, @"undeliver|not deliver|\breturn|\brto\b|refus|reject|fail|مرتجع|راجع|رجع|ارتجع|رفض|لم يتم (ال)?(تسليم|استلام)|لم يستلم|ماستلمش|مستلمش|فشل")) return "returned";
            if (Regex.IsMatch(t, @"cancel|void|ملغ|الغ|اتلغ")) return "cancelled";
            if (Regex.IsMatch(t, @"out for|in delivery|delivering|on the way|جاري التوصيل|خرج|مع المندوب|في الطريق|قيد التوصيل")) return "shipped";
            if (Regex.IsMatch(t, @"deliver|complet|success|collected|fulfilled and paid|تم التسليم|تم التوصيل|اتسلم|استلم|مسلم|تسليم|مكتمل|ناجح|تم التحصيل")) return "delivered";
            if (Regex.IsMatch(t, @"ship|transit|fulfil|picked|شحن|اتشحن")) return "shipped";
            return "open";
        }

        static readonly string[] DateForms = { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss", "d/M/yyyy", "d/M/yyyy HH:mm", "d/M/yyyy HH:mm:ss", "d/M/yyyy h:mm tt", "d/M/yyyy h:mm:ss tt", "d-M-yyyy", "d-M-yyyy HH:mm", "d-M-yyyy HH:mm:ss", "d.M.yyyy", "d/M/yy", "d-M-yy" };
        public static DateTime? ReadDate(string raw)
        {
            var t = ShopifyAdmin.ToLatinDigits(raw ?? "").Trim();
            if (t.Length == 0) return null;
            double serial; DateTime d;
            if (Regex.IsMatch(t, @"^\d{5}(\.\d+)?$") && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out serial) && serial > 36000 && serial < 60000) d = DateTime.FromOADate(serial);
            else if (!DateTime.TryParseExact(t, DateForms, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d)
                  && !DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d)) return null;
            d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            return d.Year >= 2010 && d < DateTime.UtcNow.AddDays(2) ? d : (DateTime?)null;
        }

        static decimal? ReadMoney(string raw)
        {
            var t = Regex.Replace(ShopifyAdmin.ToLatinDigits(raw ?? "").Replace("٬", "").Replace("٫", "."), @"[^\d.,]", "").Trim('.', ',');
            if (t.IndexOf(',') >= 0 && t.IndexOf('.') < 0 && Regex.IsMatch(t, @",\d{1,2}$")) t = t.Replace(',', '.');   // 350,50
            t = t.Replace(",", "");
            decimal d; return decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out d) && d >= 0 && d < 10000000 ? d : (decimal?)null;
        }

        public const int MaxImport = 20000;
        // an Excel sheet as rows of cells in their columns (a date stays Excel's number: ReadDate knows it)
        static List<string[]> Sheet(byte[] data)
        {
            var rows = new List<string[]>();
            using (var ms = new MemoryStream(data))
            using (var doc = SpreadsheetDocument.Open(ms, false))
            {
                var wb = doc.WorkbookPart;
                var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<X.SharedStringItem>().Select(x => x.InnerText).ToList() ?? new List<string>();
                var first = wb.Workbook.Sheets.Elements<X.Sheet>().FirstOrDefault();
                if (first == null) return rows;
                foreach (var row in ((WorksheetPart)wb.GetPartById(first.Id)).Worksheet.Descendants<X.Row>())
                {
                    if (rows.Count > MaxImport) break;
                    var cells = new List<string>();
                    foreach (var cell in row.Elements<X.Cell>())
                    {
                        int at = ColumnOf(cell.CellReference?.Value, cells.Count);
                        if (at > 200) break;
                        while (cells.Count <= at) cells.Add("");
                        string v;
                        if (cell.DataType != null && cell.DataType.Value == X.CellValues.SharedString) { int i; v = int.TryParse(cell.CellValue?.Text, out i) && i >= 0 && i < shared.Count ? shared[i] : ""; }
                        else if (cell.DataType != null && cell.DataType.Value == X.CellValues.InlineString) v = cell.InlineString?.InnerText;
                        else v = cell.CellValue?.Text;
                        cells[at] = v ?? "";
                    }
                    rows.Add(cells.ToArray());
                }
            }
            return rows;
        }
        static int ColumnOf(string reference, int fallback)
        {
            int n = 0; foreach (var ch in reference ?? "") { if (ch < 'A' || ch > 'Z') break; n = n * 26 + (ch - 'A' + 1); }
            return n > 0 ? n - 1 : fallback;
        }

        static List<string[]> Csv(string text)
        {
            var rows = new List<string[]>();
            var head = (text ?? "").Split('\n').FirstOrDefault() ?? "";
            char sep = new[] { ',', ';', '\t' }.OrderByDescending(ch => head.Count(x => x == ch)).First();
            var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted) { if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; } else cell.Append(ch); continue; }
                if (ch == '"' && cell.Length == 0) quoted = true;
                else if (ch == sep) { row.Add(cell.ToString()); cell.Clear(); }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear();
                    if (rows.Count > MaxImport) return rows;
                }
                else cell.Append(ch);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }

        // ---------- helpers ----------
        static string Hash(string t) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(t)).Take(12).Select(b => b.ToString("x2"))); }
        public static string SourceName(string src) { return src == "eo" ? "EasyOrders" : src == "shopify" ? "Shopify" : src == "excel" ? "Excel" : "متسجل بإيدك"; }
        public static string Money(decimal n, string cur) { return n.ToString(n == Math.Floor(n) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture) + " " + (string.IsNullOrWhiteSpace(cur) ? "ج.م" : cur); }
        static string Cur(string code) { return string.IsNullOrEmpty(code) ? null : code == "EGP" ? "ج.م" : code; }
        static string Times(string q) { return string.IsNullOrEmpty(q) || q == "1" ? "" : " × " + q; }
        static string Cut(string t, int n) { return t == null ? null : (t.Length > n ? t.Substring(0, n) : t); }
        static string Blank(string t) { return string.IsNullOrWhiteSpace(t) ? null : t.Trim(); }
        static DateTime? Utc(object v) { return v == null || v == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc); }
        static string Iso(DateTime? d) { return d.HasValue ? d.Value.ToString("o") : ""; }
        static DateTime? When(string iso)
        {
            DateTime d;
            return !string.IsNullOrEmpty(iso) && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d) && d.Year > 2000 ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null;
        }
        static decimal? D(string raw) { decimal d; return decimal.TryParse(raw ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out d) && d >= 0 && d < 100000000 ? d : (decimal?)null; }
        static JsonElement? O(JsonElement? e, string n) { JsonElement v; return e.HasValue && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(n, out v) && v.ValueKind == JsonValueKind.Object ? v : (JsonElement?)null; }
        static string J(JsonElement? e, string n)
        {
            JsonElement v;
            if (!e.HasValue || e.Value.ValueKind != JsonValueKind.Object || !e.Value.TryGetProperty(n, out v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
        }
        static bool B(JsonElement e, string n) { JsonElement v; return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out v) && v.ValueKind == JsonValueKind.True; }
        static List<JsonElement> Arr(JsonElement e, string n)
        {
            JsonElement v;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : new List<JsonElement>();
        }
        static List<string> Strs(JsonElement e, string n)
        {
            JsonElement v;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList() : new List<string>();
        }
    }

    // api/customers.ashx — the «👥 زبايني» tab (the signed-in store's own customers only).
    //   GET ?g=&m=&q=&sort=&page=    the list (group, mark, search), the groups' counts, the orders to look at, the settings
    //   GET ?phone=                  one customer's file
    //   GET ?summary=1               the counts for the home page
    //   GET ?export=1&g=&m=&q=       the same list as Excel
    //   POST ?action=note    {phone, name?, note?, mark?}     · add {phone, name, note}
    //        ?action=order   {id, status}  (set by hand)      · log {phone, total, items, status, date}   (an order logged by hand) · unlog {id}
    //        ?action=checked {id}          (the owner checked this risky order)
    //        ?action=stop    {phone, on}   (no offers / reminders to this number; off only when the owner put it)
    //        ?action=erase   {phone}       · alert {enabled}   · sync   · import (multipart: file, consent)
    public class CustomersHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }
        const int MaxFileBytes = 5 * 1024 * 1024;

        public override Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return Task.CompletedTask; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return Task.CompletedTask; }
                    if (!Crm.Shown(c, s, Crm.Feature)) { PlanGate.Deny(res, c, s, Crm.Feature); return Task.CompletedTask; }
                    if (req.HttpMethod == "GET") Get(c, s, req, res);
                    else if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) Web.Error(res, 400, "bad_request");
                    else if (!Web.Allow("crm-post:" + s.Id, 400, TimeSpan.FromHours(1))) Web.Error(res, 429, "rate_limited", "تعديلات كتير، استنى شوية وجرّب تاني.");
                    else Post(c, s, req, res);
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
            return Task.CompletedTask;
        }

        static void Get(SqlConnection c, StoreInfo s, HttpRequest req, HttpResponse res)
        {
            if (!Web.Allow("crm:" + s.Id, 2000, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "طلبات كتير، استنى شوية وجرّب تاني."); return; }
            string g = Pick(req.QueryString["g"], Crm.Groups), m = Pick(req.QueryString["m"], Crm.Marks), q = (req.QueryString["q"] ?? "").Trim(), sort = req.QueryString["sort"] ?? "";
            if (q.Length > 80) q = q.Substring(0, 80);
            if (!string.IsNullOrEmpty(req.QueryString["phone"]))
            {
                var f = Crm.File(c, s, Crm.Norm(req.QueryString["phone"]) ?? "");
                if (f == null) Web.Error(res, 404, "not_found", "مفيش زبون بالرقم ده عندك."); else Web.Json(res, 200, f);
                return;
            }
            if (req.QueryString["summary"] == "1")
            {
                var all = Crm.Load(c, s);
                Web.Json(res, 200, new Dictionary<string, object> { { "all", all.Count }, { "groups", Crm.Shown(c, s, Crm.GroupsFeature) ? Crm.GroupJson(all) : new Dictionary<string, object>() },
                    { "watch", Crm.Shown(c, s, Crm.ReturnsFeature) ? Crm.Watch(c, s, all).Count : 0 } });
                return;
            }
            if (req.QueryString["export"] == "1")
            {
                if (!Web.Allow("crm-xls:" + s.Id, 30, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "نزّلت ملفات كتير، استنى شوية."); return; }
                if (g != "back" && !Crm.Shown(c, s, Crm.GroupsFeature)) g = ""; if (g == "back" && !Crm.Shown(c, s, Crm.ReturnsFeature)) g = "";
                var file = Crm.Export(Crm.Filter(c, s, Crm.Load(c, s), g, m, q, sort));
                res.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                res.AddHeader("Content-Disposition", "attachment; filename=\"sellybot-customers" + (g.Length > 0 ? "-" + g : "") + ".xlsx\"");
                res.AddHeader("X-Content-Type-Options", "nosniff");
                res.BinaryWrite(file);
                return;
            }
            int page; int.TryParse(req.QueryString["page"] ?? "0", out page);
            // the page's first look at a Shopify store: start bringing its orders now
            if (page == 0 && ShopifyAdmin.For(s) != null && ShopifyAdmin.Has(s.ShopifyScopes, "read_orders"))
            {
                DateTime? at, tried;
                using (var cmd = Db.Cmd(c, "SELECT CrmShopAt, CrmTryAt FROM dbo.Stores WHERE Id=@s", "@s", s.Id))
                using (var r = cmd.ExecuteReader()) { r.Read(); at = r[0] == DBNull.Value ? (DateTime?)null : (DateTime)r[0]; tried = r[1] == DBNull.Value ? (DateTime?)null : (DateTime)r[1]; }
                if ((at == null || at < DateTime.UtcNow.AddHours(-1)) && (tried == null || tried < DateTime.UtcNow.AddMinutes(-2))) Pull(s.Id);
            }
            Web.Json(res, 200, Crm.State(c, s, g, m, q, sort, page));
        }

        static void Pull(int storeId) { HostingEnvironment.QueueBackgroundWorkItem(ct => Crm.PullShopify(storeId, 25, ct)); }
        static string Pick(string v, string[] allowed) { v = (v ?? "").ToLowerInvariant(); return allowed.Contains(v) ? v : ""; }

        static void Post(SqlConnection c, StoreInfo s, HttpRequest req, HttpResponse res)
        {
            string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
            if (action == "import") { Import(c, s, req, res); return; }
            var b = Web.Body(req, 8000);
            string phone = Crm.Norm(Web.Str(b, "phone", 30));
            if (phone != null && Crm.IsOwn(c, s, phone)) { Web.Error(res, 400, "own_number", "ده رقم متجرك نفسه، مش زبون."); return; }
            switch (action)
            {
                case "note":
                case "add":
                {
                    if (phone == null) { Web.Error(res, 400, "bad_phone", "اكتب رقم موبايل صح، مثلًا 01001234567."); return; }
                    string name = b.ContainsKey("name") ? (Web.Str(b, "name", 80) ?? "").Trim() : null, note = b.ContainsKey("note") ? (Web.Str(b, "note", 1000) ?? "").Trim() : null, mark = null;
                    if (b.ContainsKey("mark")) { mark = (Web.Str(b, "mark", 10) ?? "").Trim(); if (mark.Length > 0 && !Crm.Marks.Contains(mark)) { Web.Error(res, 400, "bad_request"); return; } }
                    Crm.SaveNote(c, s.Id, phone, name, note, mark);
                    Answer(c, s, res, phone);
                    return;
                }
                case "order":
                {
                    long id = Id(b); string st = (Web.Str(b, "status", 12) ?? "").ToLowerInvariant();
                    if (!Crm.Statuses.Contains(st)) { Web.Error(res, 400, "bad_request"); return; }
                    string p;
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET Status=@st, ByHand=1, StatusAt=SYSUTCDATETIME() OUTPUT INSERTED.Phone WHERE Id=@id AND StoreId=@s", "@st", st, "@id", id, "@s", s.Id)) p = cmd.ExecuteScalar() as string;
                    if (p == null) { Web.Error(res, 404, "not_found", "مالقيناش الأوردر ده."); return; }
                    Crm.Forget(s.Id);
                    Answer(c, s, res, p);
                    return;
                }
                case "log":
                {
                    if (phone == null) { Web.Error(res, 400, "bad_phone", "اكتب رقم موبايل صح، مثلًا 01001234567."); return; }
                    string st = (Web.Str(b, "status", 12) ?? "delivered").ToLowerInvariant(), items = (Web.Str(b, "items", 300) ?? "").Trim(), raw = ShopifyAdmin.ToLatinDigits(Web.Str(b, "total", 20) ?? "").Replace(",", "").Trim();
                    decimal total; bool okTotal = decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out total) && total >= 0 && total < 10000000;
                    if (!Crm.Statuses.Contains(st)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!okTotal && raw.Length > 0) { Web.Error(res, 400, "bad_total", "اكتب المبلغ أرقام بس، مثلًا 450."); return; }
                    if (!okTotal && items.Length == 0) { Web.Error(res, 400, "empty", "اكتب المبلغ أو المنتج."); return; }
                    var at = Crm.ReadDate(Web.Str(b, "date", 20)) ?? DateTime.UtcNow;
                    if (!Web.Allow("crm-log:" + s.Id, 200, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "سجّلت أوردرات كتير، استنى شوية. ولو معاك لستة، ارفعها من ملف Excel."); return; }
                    Crm.Upsert(c, s.Id, new Crm.OrderIn { Source = "hand", Id = Guid.NewGuid().ToString("N"), Phone = phone, Items = items, Total = okTotal ? total : (decimal?)null, Status = st, At = at });
                    Answer(c, s, res, phone);
                    return;
                }
                case "unlog":
                {
                    string p;
                    using (var cmd = Db.Cmd(c, "DELETE FROM dbo.CrmOrders OUTPUT DELETED.Phone WHERE Id=@id AND StoreId=@s AND Source IN ('hand','excel')", "@id", Id(b), "@s", s.Id)) p = cmd.ExecuteScalar() as string;
                    if (p == null) { Web.Error(res, 404, "not_found", "مالقيناش الأوردر ده."); return; }
                    Crm.Forget(s.Id);
                    // the customer may have been only that order: keep their (empty) file open
                    using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.CrmNotes WHERE StoreId=@s AND Phone=@p) INSERT INTO dbo.CrmNotes(StoreId, Phone) VALUES(@s, @p)", "@s", s.Id, "@p", p)) cmd.ExecuteNonQuery();
                    Answer(c, s, res, p);
                    return;
                }
                case "checked":
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.CrmOrders SET Alert='ok' WHERE Id=@id AND StoreId=@s", "@id", Id(b), "@s", s.Id)) cmd.ExecuteNonQuery();
                    Web.Json(res, 200, new { ok = true });
                    return;
                case "stop":
                {
                    if (phone == null) { Web.Error(res, 400, "bad_phone"); return; }
                    object v; bool on = b.TryGetValue("on", out v) && v is bool && (bool)v;
                    if (on)
                        using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.WaOptOuts WHERE StoreId=@s AND Phone=@p) INSERT INTO dbo.WaOptOuts(StoreId, Phone, ByOwner) VALUES(@s, @p, 1);
                            UPDATE dbo.BcSends SET Status='skipped', Note=N'صاحب المتجر وقّف رسايله' WHERE StoreId=@s AND Channel='wa' AND Phone=@p AND Status='waiting';", "@s", s.Id, "@p", phone)) cmd.ExecuteNonQuery();
                    else
                        using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaOptOuts WHERE StoreId=@s AND Phone=@p AND ByOwner=1", "@s", s.Id, "@p", phone))
                            if (cmd.ExecuteNonQuery() == 0) { Web.Error(res, 400, "customer_stopped", "الزبون هو اللي بعت «وقف»، فمينفعش نرجّع الرسايل غير لو هو طلب."); return; }
                    Crm.Forget(s.Id);
                    Answer(c, s, res, phone);
                    return;
                }
                case "erase":
                    if (phone == null) { Web.Error(res, 400, "bad_phone"); return; }
                    Crm.Erase(c, s.Id, phone);
                    Web.Json(res, 200, new { ok = true });
                    return;
                case "alert":
                {
                    object v; bool on = b.TryGetValue("enabled", out v) && v is bool && (bool)v;
                    if (on && !PlanGate.Allows(c, s, Crm.ReturnsFeature)) { PlanGate.Deny(res, c, s, Crm.ReturnsFeature); return; }
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CrmAlert=@o WHERE Id=@s", "@o", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                    Web.Json(res, 200, new { ok = true, enabled = on });
                    return;
                }
                case "sync":
                    if (ShopifyAdmin.For(s) == null) { Web.Error(res, 400, "no_shopify", "اربط متجرك على Shopify (ربط كامل) الأول من «داتا المتجر»."); return; }
                    if (!Web.Allow("crm-sync:" + s.Id, 12, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "حدّثت كتير، استنى شوية."); return; }
                    Pull(s.Id);
                    Web.Json(res, 200, new { ok = true });
                    return;
            }
            Web.Error(res, 400, "bad_action");
        }

        static long Id(Dictionary<string, object> b) { object v; long n; return b.TryGetValue("id", out v) && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out n) ? n : 0; }

        // the customer's file after the change
        static void Answer(SqlConnection c, StoreInfo s, HttpResponse res, string phone)
        {
            var f = Crm.File(c, s, phone);
            if (f == null) Web.Error(res, 404, "not_found", "مفيش زبون بالرقم ده عندك."); else Web.Json(res, 200, f);
        }

        static void Import(SqlConnection c, StoreInfo s, HttpRequest req, HttpResponse res)
        {
            if (req.Form["consent"] != "1") { Web.Error(res, 400, "no_consent", "أكّد الأول إن دي أوردرات متجرك إنت."); return; }
            var file = req.Files.Count > 0 ? req.Files[0] : null;
            if (file == null || file.ContentLength == 0) { Web.Error(res, 400, "no_file", "اختار ملف."); return; }
            if (file.ContentLength > MaxFileBytes) { Web.Error(res, 400, "file_too_big", "الملف أكبر من 5 ميجا."); return; }
            if (!Web.Allow("crm-import:" + s.Id, 15, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "رفعت ملفات كتير، استنى شوية."); return; }
            string ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
            if (ext == ".xls") { Web.Error(res, 400, "old_excel", "الملف ده Excel قديم (.xls). افتحه واحفظه .xlsx، أو CSV، وارفعه تاني."); return; }
            if (ext != ".xlsx" && ext != ".csv" && ext != ".txt") { Web.Error(res, 400, "bad_type", "ارفع ملف Excel (.xlsx) أو CSV."); return; }
            var full = Storage.CheckRoom(c, 0);
            if (full != null) { Web.Error(res, 507, "storage_full", full); return; }
            byte[] data;
            using (var ms = new MemoryStream()) { file.InputStream.CopyTo(ms); data = ms.ToArray(); }
            Crm.ImportResult r;
            try { r = Crm.Import(c, s, data, ext); }
            catch (SqlException) { throw; }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 400, "unreadable", "مقدرناش نقرا الملف ده. اتأكد إنه مش تالف أو محمي بباسورد."); return; }
            if (r.Missing != null) { Web.Error(res, 400, "no_columns", "مالقيناش في الملف عمود " + string.Join(" ولا عمود ", r.Missing) + ". لازم أول صف يبقى فيه أسامي الأعمدة."); return; }
            if (r.Added + r.Updated == 0) { Web.Error(res, 400, "nothing", r.NoPhone + r.NoDate > 0 ? "مالقيناش أوردرات برقم موبايل وتاريخ صح في الملف." : "الملف مفيهوش أوردرات جديدة."); return; }
            Web.Json(res, 200, new Dictionary<string, object> { { "rows", r.Rows }, { "added", r.Added }, { "updated", r.Updated }, { "noPhone", r.NoPhone }, { "noDate", r.NoDate } });
        }
    }
}
