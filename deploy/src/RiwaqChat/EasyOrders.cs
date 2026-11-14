// EasyOrders (easyorders.eg), the same idea as the Shopify connection:
//  * Connect: one click on EasyOrders' "install app" link (EasyOrders then POSTs us an API key and creates the order webhooks
//    itself), or the owner makes a Public API key (EasyOrders: Settings > Public API), pastes it here, and adds our webhook URL there.
//  * Data: products, prices, options (variants) and stock become one data part (SourceType "easyorders"). It refreshes every few
//    hours while the chat is used, and a little after every new order (the stock changed).
//  * Order tracking in the chat (the lookup_order tool): the order number is EasyOrders' short id; details only when the phone matches.
//  * Order confirmation on WhatsApp (EoConfirm below): a new order -> a message from the store's number; 1 sets the order to
//    "confirmed", 2 to "canceled" (only while it is still pending or confirmed).
// EasyOrders has no API to create orders, so there is no ordering from the chat here.
// API: https://api.easy-orders.net/api/v1/external-apps/... with the header "Api-Key", 40 requests a minute per key.
// Docs: https://public-api-docs.easy-orders.net/ . The "EasyOrdersTestBase" setting points every call at a local fake (local tests only).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class EoException : Exception
    {
        public int Status; public string Raw;
        public EoException(string message, int status = 0, string raw = null) : base(message) { Status = status; Raw = raw; }
        // "Store not active or has over due": the EasyOrders store itself is stopped (unpaid wallet / subscription)
        public bool NotActive { get { var r = Raw ?? ""; return r.IndexOf("not active", StringComparison.OrdinalIgnoreCase) >= 0 || r.IndexOf("over due", StringComparison.OrdinalIgnoreCase) >= 0; } }
        // a missing order comes back as 400 {"message":"record not found"} (not 404)
        public bool NotFound { get { return Status == 404 || (Raw ?? "").IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0; } }
    }

    public static class EasyOrders
    {
        public const string SourceType = "easyorders";
        public const string Feature = "easyorders";   // PlanGate feature
        public const string ItemTitle = "داتا EasyOrders: المنتجات والأسعار والمخزون (بتتحدث تلقائيًا)";
        public const string Permissions = "products:read,orders:read,orders:update";   // read-only, plus changing an order's status (confirm / cancel)
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);
        const int PageSize = 100, MaxPages = 60, PerMinute = 36, MaxDescriptionChars = 1200;

        static string ApiBase { get { var t = Web.Setting("EasyOrdersTestBase"); return (string.IsNullOrEmpty(t) ? "https://api.easy-orders.net" : t.TrimEnd('/')) + "/api/v1/external-apps"; } }
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        static EasyOrders()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0");
        }

        public class Creds { public int StoreId; public string Key; }

        public static bool Connected(StoreInfo s) { return s != null && !string.IsNullOrEmpty(s.EoKey); }
        public static bool TrackingOn(StoreInfo s) { return Connected(s) && s.EoOrders; }

        public static Creds For(StoreInfo s)
        {
            if (!Connected(s)) return null;
            var key = Secrets.Unprotect(s.EoKey);
            return string.IsNullOrEmpty(key) ? null : new Creds { StoreId = s.Id, Key = key };
        }

        public static bool ValidKey(string key) { return key != null && Regex.IsMatch(key, @"^[A-Za-z0-9_.\-]{16,200}$"); }

        // ---------- calls (36 a minute per key, under EasyOrders' 40) ----------
        static readonly ConcurrentDictionary<int, Queue<DateTime>> Calls = new ConcurrentDictionary<int, Queue<DateTime>>();

        static async Task Throttle(int storeId)
        {
            var q = Calls.GetOrAdd(storeId, _ => new Queue<DateTime>());
            for (int i = 0; i < 90; i++)
            {
                TimeSpan wait;
                lock (q)
                {
                    var now = DateTime.UtcNow;
                    while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
                    if (q.Count < PerMinute) { q.Enqueue(now); return; }
                    wait = q.Peek().AddMinutes(1) - now;
                }
                await Task.Delay(wait < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait);
            }
            throw new EoException("EasyOrders مشغولة دلوقتي، جرّب كمان دقيقة.", 429);
        }

        static async Task<JsonElement> Call(Creds k, HttpMethod method, string path, object body = null, bool bearer = false)
        {
            for (int attempt = 0; ; attempt++)
            {
                await Throttle(k.StoreId);
                var req = new HttpRequestMessage(method, ApiBase + path);
                req.Headers.TryAddWithoutValidation("Api-Key", k.Key);
                if (bearer) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + k.Key);   // the delete-webhook page shows this header
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                HttpResponseMessage resp;
                try { resp = await Http.SendAsync(req); }
                catch (Exception ex) { Log.Error(ex); throw new EoException("مقدرناش نوصل لـ EasyOrders دلوقتي. جرّب تاني كمان شوية."); }
                using (resp)
                {
                    int code = (int)resp.StatusCode;
                    string text = await resp.Content.ReadAsStringAsync();
                    if (code == 429 && attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(30)); continue; }   // over 40 a minute: wait for the next minute
                    if (code >= 500 && attempt < 1 && method == HttpMethod.Get) { await Task.Delay(TimeSpan.FromSeconds(2)); continue; }
                    JsonElement root = default(JsonElement);
                    try { using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text)) root = doc.RootElement.Clone(); }
                    catch (JsonException) { }
                    if (code >= 200 && code < 300) return root;
                    string raw = ErrorText(root) ?? Cut(Regex.Replace(text ?? "", @"\s+", " ").Trim(), 200);
                    if (code != 404 && raw.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0) Log.Error(new Exception("EasyOrders " + method + " " + Regex.Replace(path, @"\?.*$", "") + " -> " + code + ": " + raw));
                    throw new EoException(Friendly(code, raw), code, raw);
                }
            }
        }

        static string ErrorText(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "message", "error" })
            {
                JsonElement m;
                if (!root.TryGetProperty(name, out m)) continue;
                if (m.ValueKind == JsonValueKind.String) return m.GetString();
                if (m.ValueKind == JsonValueKind.Array) return string.Join("; ", m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
            }
            return null;
        }

        static string Friendly(int code, string raw)
        {
            var r = raw ?? "";
            Func<string, bool> has = w => r.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
            if (has("not active") || has("over due"))
                return "EasyOrders بتقول إن متجرك مش مفعّل أو عليه مستحقات (Store not active or has over due). فعّل المتجر أو اشحن المحفظة من لوحة EasyOrders، وبعدين جرّب تاني.";
            if (code == 401 || has("api-key") || has("api key") || has("unauthorized"))
                return "الـ API Key ده مش شغال. اتأكد إنك نسخته كامل وإنه Active في EasyOrders (Settings ← Public API).";
            if (code == 403) return "الـ API Key ده ناقصه صلاحية. اعمل مفتاح جديد في EasyOrders وعلّم على صلاحيات قراءة المنتجات، وقراءة وتعديل الأوردرات.";
            if (code == 404 || has("not found")) return "مالقيناش ده في EasyOrders.";
            if (code == 429) return "EasyOrders مشغولة دلوقتي (أكتر من 40 طلب في الدقيقة). جرّب كمان دقيقة.";
            return "EasyOrders رجّعت خطأ (" + code + ").";
        }

        // a list answer: a plain array, or an object holding the array
        static List<JsonElement> Items(JsonElement r)
        {
            var list = new List<JsonElement>();
            if (r.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "data", "items", "products", "results" })
                {
                    JsonElement a;
                    if (r.TryGetProperty(name, out a) && a.ValueKind == JsonValueKind.Array) { r = a; break; }
                }
            if (r.ValueKind == JsonValueKind.Array) foreach (var x in r.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object) list.Add(x);
            return list;
        }

        public static Task<JsonElement> GetOrder(Creds k, string orderId) { return Call(k, HttpMethod.Get, "/orders/" + Uri.EscapeDataString(orderId)); }
        static readonly HttpMethod Patch = new HttpMethod("PATCH");   // no HttpMethod.Patch on .NET Framework
        public static Task<JsonElement> SetOrderStatus(Creds k, string orderId, string status) { return Call(k, Patch, "/orders/" + Uri.EscapeDataString(orderId) + "/status", new { status }); }

        // a private note on the order in the owner's EasyOrders dashboard (best effort)
        public static async Task AddNote(Creds k, string orderId, string eoStoreId, string note)
        {
            if (string.IsNullOrEmpty(eoStoreId)) return;
            try { await Call(k, HttpMethod.Post, "/order-notes", new { order_id = orderId, note, store_id = eoStoreId, type = "private" }); }
            catch (Exception ex) { if (!(ex is EoException)) Log.Error(ex); }
        }

        // ---------- catalog ----------
        public class Sync { public string Text, EoStoreId; public int Products; }

        // Every product with its options and stock, 100 a page. Checked against the live API (2026-11-14):
        //  * the plain list is an array with few fields (no description, sale_price, categories); "fields=" brings them back
        //  * with limit/page the answer is {data:[...], next_page (null on the last page), page, total, totalPages}
        //  * join=categories works; unknown joins are ignored. The variant joins couldn't be checked yet (no product had options).
        const string Fields = "id,name,price,sale_price,description,slug,quantity,track_stock,hidden,is_quantity_hidden,disable_orders_for_no_stock,is_free_shipping,custom_currency,store_id";
        const string Joins = "join=categories&join=variations&join=variations.props&join=variants&join=variants.variation_props";

        public static async Task<Sync> FetchCatalog(Creds k, string storeUrl, CancellationToken ct)
        {
            var seen = new HashSet<string>();
            var products = new List<JsonElement>();
            bool plain = false;
            for (int page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                JsonElement r;
                try { r = await Call(k, HttpMethod.Get, plain ? "/products" : "/products?fields=" + Fields + "&" + Joins + "&limit=" + PageSize + "&page=" + page); }
                catch (EoException ex) when (page == 1 && ex.Status == 400 && !ex.NotActive)
                {
                    plain = true;   // the paging / fields / join params weren't accepted: the plain list
                    r = await Call(k, HttpMethod.Get, "/products");
                }
                int fresh = 0;
                foreach (var p in Items(r))
                {
                    string id = Str(p, "id") ?? Str(p, "slug") ?? Str(p, "name") ?? ("n" + products.Count);
                    if (seen.Add(id)) { products.Add(p); fresh++; }
                }
                JsonElement next;
                bool last = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("next_page", out next) && next.ValueKind == JsonValueKind.Null;
                if (plain || last || fresh == 0) break;
            }

            var res = new Sync();
            var sb = new StringBuilder();
            foreach (var p in products)
            {
                if (res.EoStoreId == null) res.EoStoreId = Str(p, "store_id");
                if (Bool(p, "hidden")) continue;
                if (AppendProduct(sb, p, storeUrl)) res.Products++;
            }
            var all = new StringBuilder("دي داتا المتجر على EasyOrders. بتتحدث تلقائيًا من EasyOrders نفسه، والكميات اللي فيها هي المتاح في المخزن.");
            if (!string.IsNullOrEmpty(storeUrl)) all.Append("\nموقع المتجر: ").Append(storeUrl);
            all.Append("\nعدد المنتجات: ").Append(res.Products);
            if (sb.Length > 0) all.Append("\n\n# المنتجات").Append(sb);
            res.Text = all.ToString();
            if (res.Text.Length > KnowledgeHandler.MaxItemChars) res.Text = res.Text.Substring(0, KnowledgeHandler.MaxItemChars);
            return res;
        }

        static bool AppendProduct(StringBuilder sb, JsonElement p, string storeUrl)
        {
            string name = Str(p, "name");
            if (string.IsNullOrWhiteSpace(name)) return false;
            string cur = Str(p, "custom_currency");
            sb.Append("\n\n## ").Append(name.Trim());
            var cats = Arr(p, "categories").Select(x => Str(x, "name")).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(8).ToList();
            if (cats.Count > 0) sb.Append("\nالتصنيف: ").Append(string.Join("، ", cats));
            bool track = Bool(p, "track_stock"), hideQty = Bool(p, "is_quantity_hidden"), stopAtZero = Bool(p, "disable_orders_for_no_stock");

            // options (size, color...): a prop's value can be a code (a color's #hex), so remember value -> label
            var labels = new Dictionary<string, string>();
            var opts = new List<string>();
            foreach (var v in Arr(p, "variations"))
            {
                string vn = (Str(v, "name") ?? "").Trim();
                var vals = new List<string>();
                foreach (var pr in Arr(v, "props"))
                {
                    string label = Str(pr, "name"), value = Str(pr, "value");
                    string shown = !string.IsNullOrWhiteSpace(label) ? label.Trim() : (value ?? "").Trim();
                    if (shown.Length == 0) continue;
                    vals.Add(shown);
                    if (!string.IsNullOrWhiteSpace(value)) labels[vn + "|" + value.Trim()] = shown;
                }
                if (vn.Length > 0 && vals.Count > 0) opts.Add(vn + ": " + string.Join(" / ", vals));
            }

            var variants = Arr(p, "variants");
            if (variants.Count == 0)
                sb.Append("\nالسعر ").Append(PriceText(p, null, cur)).Append(" · ").Append(Stock(track, Num(p, "quantity"), hideQty, stopAtZero));
            else
            {
                if (opts.Count > 0) sb.Append("\nالاختيارات: ").Append(string.Join(" · ", opts));
                var lines = new List<string>();
                foreach (var v in variants.Take(100))
                {
                    var parts = new List<string>();
                    foreach (var vp in Arr(v, "variation_props"))
                    {
                        string vn = (Str(vp, "variation") ?? "").Trim(), val = (Str(vp, "variation_prop") ?? "").Trim(), lbl;
                        if (labels.TryGetValue(vn + "|" + val, out lbl)) val = lbl;
                        if (val.Length > 0) parts.Add((vn.Length > 0 ? vn + " " : "") + val);
                    }
                    lines.Add((parts.Count > 0 ? string.Join("، ", parts) + ": " : "") + "السعر " + PriceText(v, p, cur) + " · " + Stock(track, Num(v, "quantity"), hideQty, stopAtZero));
                }
                sb.Append("\n- ").Append(string.Join("\n- ", lines));
            }
            if (Bool(p, "is_free_shipping")) sb.Append("\nالشحن مجاني للمنتج ده.");
            string desc = Shopify.HtmlToText(Str(p, "description"));
            if (desc.Length > MaxDescriptionChars) desc = desc.Substring(0, MaxDescriptionChars) + " …";
            if (desc.Length > 0) sb.Append("\nالوصف: ").Append(desc);
            string slug = Str(p, "slug");
            if (!string.IsNullOrEmpty(storeUrl) && !string.IsNullOrWhiteSpace(slug)) sb.Append("\nاللينك: ").Append(storeUrl).Append("/products/").Append(Uri.EscapeDataString(slug.Trim()));
            return true;
        }

        // "price" is the original price, "sale_price" the price it sells at (when lower)
        static string PriceText(JsonElement v, JsonElement? product, string cur)
        {
            decimal? price = Num(v, "price"), sale = Num(v, "sale_price");
            if (price == null && product.HasValue) { price = Num(product.Value, "price"); if (sale == null) sale = Num(product.Value, "sale_price"); }
            if (price == null || price <= 0) return sale > 0 ? Money(sale, cur) : "مش محدد";
            if (sale > 0 && sale < price) return Money(sale, cur) + " (بدل " + Money(price, cur) + ")";
            return Money(price, cur);
        }

        static string Stock(bool track, decimal? qty, bool hideQty, bool stopAtZero)
        {
            if (!track || qty == null) return "متاح";
            if (qty.Value <= 0) return stopAtZero ? "خلصان" : "متاح بالطلب";
            return hideQty ? "متاح" : "متاح (فاضل " + qty.Value.ToString("0", CultureInfo.InvariantCulture) + ")";
        }

        // ---------- save ----------
        public static string Save(SqlConnection c, int storeId, Sync r, string fileName)
        {
            int? existing = null;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id FROM dbo.KnowledgeItems WHERE StoreId=@s AND SourceType=@t", "@s", storeId, "@t", SourceType))
            {
                var v = cmd.ExecuteScalar();
                if (v != null && v != DBNull.Value) existing = (int)v;
            }
            var err = KnowledgeHandler.CheckSize(c, storeId, existing ?? 0, r.Text);
            if (err != null) return err;
            var room = Storage.CheckRoom(c, r.Text.Length);
            if (room != null) return room;
            if (existing == null) KnowledgeHandler.Insert(c, storeId, ItemTitle, SourceType, fileName, r.Text);
            else
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.KnowledgeItems SET Title=@ti, FileName=@f, Content=@c, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id",
                    "@ti", ItemTitle, "@f", fileName, "@c", r.Text, "@id", existing.Value)) cmd.ExecuteNonQuery();
                Db.BumpVersion(c, storeId);
            }
            SetStatus(c, storeId, true, r.Products + " منتج");
            if (!string.IsNullOrEmpty(r.EoStoreId))
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoStoreId=@e WHERE Id=@s", "@e", Cut(r.EoStoreId, 64), "@s", storeId)) cmd.ExecuteNonQuery();
            return null;
        }

        public static string FileName(string storeUrl) { return string.IsNullOrEmpty(storeUrl) ? "EasyOrders" : storeUrl.Replace("https://", ""); }

        public static void SetStatus(SqlConnection c, int storeId, bool synced, string status)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoStatus=@st" + (synced ? ", EoSyncedAt=SYSUTCDATETIME()" : "") + " WHERE Id=@s",
                "@st", (object)Cut(status, 400) ?? DBNull.Value, "@s", storeId)) cmd.ExecuteNonQuery();
        }

        public static async Task Disconnect(SqlConnection c, StoreInfo s, string origin)
        {
            var k = For(s);
            if (k != null && !string.IsNullOrEmpty(s.EoHookKey))
            {
                // best effort: the old link stops working anyway, because the key in it is cleared below
                try { await Call(k, HttpMethod.Delete, "/webhooks/delete-by-url?url=" + Uri.EscapeDataString(HookUrl(origin, s, s.EoHookKey)), null, true); }
                catch (Exception ex) { if (!(ex is EoException)) Log.Error(ex); }
            }
            using (var cmd = Db.Cmd(c, "DELETE FROM dbo.KnowledgeItems WHERE StoreId=@s AND SourceType=@t", "@s", s.Id, "@t", SourceType)) cmd.ExecuteNonQuery();
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.Stores SET EoKey=NULL, EoMode=NULL, EoStoreId=NULL, EoUrl=NULL, EoStatus=NULL, EoSyncedAt=NULL,
                EoOrders=0, EoOcOn=0, EoHookKey=NULL, EoHookAt=NULL WHERE Id=@s", "@s", s.Id)) cmd.ExecuteNonQuery();
            Db.BumpVersion(c, s.Id);
        }

        // ---------- refresh (through chat use, or a little after a new order) ----------
        static readonly ConcurrentDictionary<int, bool> Running = new ConcurrentDictionary<int, bool>();
        static readonly ConcurrentDictionary<int, bool> Pending = new ConcurrentDictionary<int, bool>();

        public static void RefreshIfOld(StoreInfo s)
        {
            if (!Connected(s)) return;
            if (s.EoSyncedAt.HasValue && DateTime.UtcNow - s.EoSyncedAt.Value < RefreshEvery) return;
            int id = s.Id;
            HostingEnvironment.QueueBackgroundWorkItem(ct => SyncNow(id, ct));
        }

        public static void QueueResync(int storeId)
        {
            if (!Pending.TryAdd(storeId, true)) return;
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(90), ct); }
                catch { }
                finally { bool x; Pending.TryRemove(storeId, out x); }
                await SyncNow(storeId, ct);
            });
        }

        public static async Task SyncNow(int storeId, CancellationToken ct)
        {
            if (!Running.TryAdd(storeId, true)) return;
            try
            {
                StoreInfo s;
                using (var c = Db.Open()) s = Db.StoreById(c, storeId);
                var k = For(s);
                if (k == null) return;
                Sync r;
                try { r = await FetchCatalog(k, s.EoUrl, ct); }
                catch (EoException ex) { using (var c = Db.Open()) SetStatus(c, storeId, true, "⚠️ آخر تحديث تلقائي فشل: " + ex.Message); return; }   // marked as tried, so it doesn't retry on every message
                using (var c = Db.Open())
                {
                    var now = Db.StoreById(c, storeId);
                    if (!Connected(now)) return;   // disconnected while it was syncing
                    var err = Save(c, storeId, r, FileName(now.EoUrl));
                    if (err != null) SetStatus(c, storeId, true, "⚠️ " + err);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                try { using (var c = Db.Open()) SetStatus(c, storeId, true, "⚠️ آخر تحديث تلقائي فشل: EasyOrders مكانتش متاحة"); } catch { }
            }
            finally { bool x; Running.TryRemove(storeId, out x); }
        }

        // ---------- order tracking ("where is my order?") ----------
        public static async Task<string> LookupOrder(StoreInfo s, string ip, string orderNumber, string contact, bool countAttempt = true)
        {
            const string NoMatch = "البيانات دي مش مطابقة لأي أوردر. ماتقولش أي تفاصيل عن أي أوردر. اطلب من الزبون بلطف يتأكد من رقم الأوردر ورقم الموبايل اللي طلب بيه، ولو المشكلة فضلت يكلّم المتجر.";
            var k = For(s);
            if (k == null || !s.EoOrders) return "تتبع الأوردرات مش متاح في المتجر ده. انصح الزبون يكلّم المتجر مباشرة.";
            var blocked = countAttempt ? ShopifyAdmin.LookupBlocked(s.Id, ip) : null;   // false: Shopify's lookup already counted this question
            if (blocked != null) return blocked;

            string num = Regex.Replace(ShopifyAdmin.ToLatinDigits(orderNumber ?? ""), @"\D", "");
            string want = (contact ?? "").Trim();
            if (num.Length == 0 || num.Length > 12 || want.Length < 5) return "محتاج رقم الأوردر (الرقم اللي ظهر للزبون لما طلب) ورقم الموبايل اللي الطلب اتعمل بيه. اطلبهم من الزبون.";

            JsonElement o;
            try { o = await Call(k, HttpMethod.Get, "/orders/short/" + num); }
            catch (EoException ex)
            {
                if (ex.NotFound || (ex.Status == 400 && !ex.NotActive)) return NoMatch;
                return "مقدرناش نوصل لبيانات الأوردرات دلوقتي. اعتذر للزبون وانصحه يجرّب كمان شوية أو يكلّم المتجر.";
            }
            if (o.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(Str(o, "id"))) return NoMatch;
            if (!ShopifyAdmin.ContactMatches(want, Str(o, "email"), new[] { Str(o, "phone") })) return NoMatch;

            var sb = new StringBuilder("البيانات مطابقة. ده الأوردر #").Append(Str(o, "short_id") ?? num);
            var created = Date(Str(o, "created_at")); if (created != null) sb.Append(" (اتعمل يوم ").Append(created).Append(")");
            sb.Append(":\n- الحالة: ").Append(StatusText(Str(o, "status")));
            sb.Append("\n- الدفع: ").Append(PayText(Str(o, "payment_method")));
            var items = ItemLines(o);
            if (items.Count > 0) sb.Append("\n- المنتجات: ").Append(string.Join("، ", items));
            var total = Num(o, "total_cost");
            if (total != null) sb.Append("\n- الإجمالي: ").Append(Money(total, null)).Append(Num(o, "shipping_cost") > 0 ? " (منه " + Money(Num(o, "shipping_cost"), null) + " شحن)" : "");
            sb.Append("\nقول للزبون الحالة دي بكلام بسيط. ماتذكرش العنوان ولا أي حاجة مش مكتوبة هنا.");
            return sb.ToString();
        }

        public static string StatusText(string v)
        {
            switch (v)
            {
                case "pending": return "جديد ولسه مستني التأكيد";
                case "confirmed": return "اتأكد وهيتجهز";
                case "pending_payment": return "مستني الدفع";
                case "paid": return "اتدفع";
                case "paid_failed": return "الدفع مانجحش";
                case "processing": return "بيتجهز";
                case "waiting_for_pickup": return "جاهز ومستني شركة الشحن تستلمه";
                case "in_delivery": return "خرج مع شركة الشحن للتوصيل";
                case "delivered": return "اتسلم";
                case "canceled": return "اتلغى";
                case "returning_from_delivery": return "راجع من التوصيل";
                case "request_refund": return "فيه طلب استرجاع";
                case "refund_in_progress": return "الفلوس بتترجع";
                case "refunded": return "الفلوس اترجعت";
                default: return string.IsNullOrEmpty(v) ? "مش معروفة" : v;
            }
        }
        static string PayText(string v) { return string.IsNullOrEmpty(v) ? "مش محدد" : IsCod(v) ? "كاش عند الاستلام" : v; }
        public static bool IsCod(string paymentMethod) { return paymentMethod != null && Regex.IsMatch(paymentMethod, @"^cod$|cash|on.?delivery|الاستلام|كاش", RegexOptions.IgnoreCase); }

        // "تيشيرت (المقاس L) × 2"
        public static List<string> ItemLines(JsonElement o)
        {
            var list = new List<string>();
            foreach (var li in Arr(o, "cart_items").Take(15))
            {
                JsonElement pr, va;
                string title = li.TryGetProperty("product", out pr) && pr.ValueKind == JsonValueKind.Object ? Str(pr, "name") : null;
                if (string.IsNullOrWhiteSpace(title)) title = "منتج";
                var props = li.TryGetProperty("variant", out va) && va.ValueKind == JsonValueKind.Object
                    ? Arr(va, "variation_props").Select(x => ((Str(x, "variation") ?? "") + " " + (Str(x, "variation_prop") ?? "")).Trim()).Where(x => x.Length > 0).ToList()
                    : new List<string>();
                var q = Num(li, "quantity") ?? 1;
                list.Add(title.Trim() + (props.Count > 0 ? " (" + string.Join("، ", props) + ")" : "") + (q > 1 ? " × " + q.ToString("0", CultureInfo.InvariantCulture) : ""));
            }
            return list;
        }

        // ---------- links ----------
        // The webhook URL carries a random per-store key (EasyOrders shows no signing secret we can check);
        // the install callback carries a second key derived from it, so the webhook URL alone can't link a store.
        public static string HookKey(SqlConnection c, StoreInfo s)
        {
            if (!string.IsNullOrEmpty(s.EoHookKey)) return s.EoHookKey;
            var b = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            string k = B64Url(b);
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoHookKey=@k WHERE Id=@s AND EoHookKey IS NULL", "@k", k, "@s", s.Id))
                if (cmd.ExecuteNonQuery() == 0) k = Db.StoreById(c, s.Id).EoHookKey ?? k;   // another request made it first
            s.EoHookKey = k;
            return k;
        }

        static string CallbackKey(string hookKey)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes("easyorders-callback|" + (Web.Setting("AuthSecret") ?? ""))))
                return B64Url(h.ComputeHash(Encoding.UTF8.GetBytes(hookKey ?? ""))).Substring(0, 24);
        }

        public static bool HookOk(StoreInfo s, string key) { return s != null && !string.IsNullOrEmpty(s.EoHookKey) && SameText(s.EoHookKey, key); }
        public static bool CallbackOk(StoreInfo s, string key) { return s != null && !string.IsNullOrEmpty(s.EoHookKey) && SameText(CallbackKey(s.EoHookKey), key); }

        // https://sellybot.online (the canonical host when set), from the current request
        public static string Origin(HttpRequest req)
        {
            if (req != null) return WaAccount.Origin(req);
            var host = Web.Setting("CanonicalHost");
            return string.IsNullOrWhiteSpace(host) ? "" : "https://" + host.Trim();
        }

        public static string HookUrl(string origin, StoreInfo s, string key) { return origin + "/api/easyorders-webhook.ashx?store=" + s.Slug + "&key=" + key; }
        static string CallbackUrl(string origin, StoreInfo s, string key) { return origin + "/api/easyorders.ashx?action=callback&store=" + s.Slug + "&key=" + CallbackKey(key); }

        // EasyOrders' "install app" page: the owner approves, EasyOrders posts the API key to callback_url and creates both webhooks.
        public static string InstallUrl(string origin, StoreInfo s, string key)
        {
            Func<string, string> E = Uri.EscapeDataString;
            string hook = HookUrl(origin, s, key);
            return "https://app.easy-orders.net/#/install-app?app_name=" + E("Sellybot") +
                   "&app_description=" + E("مساعد ذكي بيرد على زباين متجرك من منتجاتك، ويتابع الأوردرات ويأكدها على واتساب.") +
                   "&app_icon=" + E(origin + "/sellybot-icon.svg") +
                   "&permissions=" + Permissions +
                   "&callback_url=" + E(CallbackUrl(origin, s, key)) +
                   "&orders_webhook=" + E(hook) + "&order_status_webhook=" + E(hook) +
                   "&redirect_url=" + E(origin + "/dashboard.html?eo=1");
        }

        // ---------- dashboard ----------
        public static Dictionary<string, object> Describe(SqlConnection c, StoreInfo s)
        {
            var ctx = HttpContext.Current;
            string origin = Origin(ctx != null ? ctx.Request : null), key = HookKey(c, s);
            bool on = Connected(s);
            return new Dictionary<string, object> {
                { "connected", on }, { "mode", s.EoMode ?? "" }, { "url", s.EoUrl ?? "" }, { "status", s.EoStatus ?? "" },
                { "syncedAt", Iso(s.EoSyncedAt) }, { "hookAt", Iso(s.EoHookAt) }, { "orders", s.EoOrders },
                { "hookUrl", HookUrl(origin, s, key) }, { "installUrl", on ? "" : InstallUrl(origin, s, key) },
                { "confirm", new Dictionary<string, object> {
                    { "on", s.EoOcOn }, { "scope", s.EoOcScope ?? "all" },
                    { "whatsapp", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber) },
                    { "recent", on ? (object)EoConfirm.Recent(c, s.Id) : new object[0] } } } };
        }

        // ---------- helpers ----------
        public static string NormalizeUrl(string input, out bool bad)
        {
            bad = false;
            if (string.IsNullOrWhiteSpace(input)) return null;
            var host = Shopify.NormalizeDomain(input);
            if (host == null) { bad = true; return null; }
            return "https://" + host;
        }

        internal static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return null;
        }
        internal static decimal? Num(JsonElement e, string name)
        {
            JsonElement v; decimal d;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out d)) return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }
        internal static bool Bool(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return false;
            return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetRawText() == "1") || (v.ValueKind == JsonValueKind.String && v.GetString() == "true");
        }
        internal static List<JsonElement> Arr(JsonElement e, string name)
        {
            JsonElement v;
            var list = new List<JsonElement>();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Array)
                foreach (var x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object) list.Add(x);
            return list;
        }
        internal static string Money(decimal? n, string cur)
        {
            if (n == null) return "";
            var s = n.Value.ToString(n.Value == Math.Floor(n.Value) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(cur) ? s : s + " " + cur.Trim();
        }
        internal static string Date(string iso)
        {
            DateTime d;
            return !string.IsNullOrEmpty(iso) && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out d) ? d.ToString("yyyy-MM-dd") : null;
        }
        static string Iso(DateTime? d) { return d.HasValue ? d.Value.ToString("o") : ""; }
        internal static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }
        static string B64Url(byte[] b) { return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('='); }
        static bool SameText(string a, string b) { return a != null && b != null && Secrets.Same(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b)); }
    }

    // "Where is my order?" for whichever store system is connected (Shopify first, when its tracking is on).
    public static class OrderTracking
    {
        static bool ShopifyOn(StoreInfo s) { return s != null && s.ShopifyOrders && ShopifyAdmin.For(s) != null; }
        public static bool On(StoreInfo s) { return ShopifyOn(s) || EasyOrders.TrackingOn(s); }
        // Both linked (the "bothstores" plan): Shopify first; when it has no such order, EasyOrders. The second look doesn't
        // count against the customer's attempts again, so one question is one attempt.
        public static async Task<string> Lookup(StoreInfo s, string ip, string orderNumber, string contact)
        {
            if (!ShopifyOn(s)) return await EasyOrders.LookupOrder(s, ip, orderNumber, contact);
            var r = await ShopifyAdmin.LookupOrder(s, ip, orderNumber, contact);
            if (!EasyOrders.TrackingOn(s) || r == null || !r.StartsWith(NoMatchStart, StringComparison.Ordinal)) return r;
            return await EasyOrders.LookupOrder(s, ip, orderNumber, contact, false);
        }
        const string NoMatchStart = "البيانات دي مش مطابقة";   // how both lookups begin their "no such order" answer
    }

    // ---------- order confirmation on WhatsApp (EasyOrders) ----------
    // A new order (webhook) -> the order is read from EasyOrders itself (the webhook body is never trusted for what we send)
    // -> a WhatsApp message from the store's number. The answer "1"/"2" (OrderConfirm.Decide) sets the order's status.
    public static class EoConfirm
    {
        const int AnswerDays = 7;

        public static void Queue(int storeId, string orderId, string shortHint)
        {
            HostingEnvironment.QueueBackgroundWorkItem(ct => OnOrder(storeId, orderId, shortHint));
        }

        static async Task OnOrder(int storeId, string orderId, string shortHint)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (s == null || !s.EoOcOn || s.Status != "active") return;
                    var k = EasyOrders.For(s);
                    if (k == null) return;
                    using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.EoConfirms WHERE StoreId=@s AND OrderId=@o", "@s", storeId, "@o", orderId))
                        if ((int)cmd.ExecuteScalar() > 0) return;   // the same order delivered twice

                    JsonElement o = default(JsonElement); string readErr = null;
                    try { o = await EasyOrders.GetOrder(k, orderId); }
                    catch (EoException ex) { if (ex.NotFound) return; readErr = ex.Message; }   // not an order of this store
                    if (readErr == null && (o.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(EasyOrders.Str(o, "id")))) return;

                    string shortId = readErr == null ? EasyOrders.Str(o, "short_id") : null;
                    if (string.IsNullOrEmpty(shortId) && shortHint != null && Regex.IsMatch(shortHint, @"^\d{1,12}$")) shortId = shortHint;
                    string name = "#" + (shortId ?? orderId.Substring(0, Math.Min(8, orderId.Length)));
                    string first = readErr == null ? (EasyOrders.Str(o, "full_name") ?? "").Trim().Split(' ')[0] : "";
                    string phone = readErr == null ? OrderConfirm.Phone(EasyOrders.Str(o, "phone"), null) : null;
                    string total = readErr == null ? EasyOrders.Money(EasyOrders.Num(o, "total_cost"), null) : "";
                    bool cod = readErr == null && EasyOrders.IsCod(EasyOrders.Str(o, "payment_method"));
                    string current = readErr == null ? EasyOrders.Str(o, "status") : null;

                    string status = "pending", note = null;
                    if (readErr != null) { status = "failed"; note = "مقدرناش نقرا الطلب من EasyOrders: " + readErr; }
                    else if (!string.IsNullOrEmpty(current) && current != "pending") { status = "skipped"; note = "حالته في EasyOrders: " + EasyOrders.StatusText(current); }
                    else if (s.EoOcScope == "cod" && !cod) { status = "skipped"; note = "مش دفع عند الاستلام (التأكيد شغال على الدفع عند الاستلام بس)"; }
                    else if (phone == null) { status = "skipped"; note = "الأوردر مفيهوش رقم موبايل"; }
                    else if (Billing.Expired(s)) { status = "skipped"; note = "الباقة خلصت"; }
                    else if (!PlanGate.Allows(c, s, EasyOrders.Feature) || !WaAccount.PlanAllows(c, s, out _)) { status = "skipped"; note = "باقتك الحالية مش شاملة ربط EasyOrders والواتساب"; }
                    else if (!Web.Allow("eo-oc:" + s.Id, 300, TimeSpan.FromHours(1))) { status = "skipped"; note = "أوردرات كتير في ساعة واحدة"; }

                    long rowId;
                    try
                    {
                        using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.EoConfirms(StoreId, OrderId, OrderName, Customer, Phone, Total, Status, Note)
                            OUTPUT INSERTED.Id VALUES(@s, @o, @n, @cu, @p, @t, @st, @no)",
                            "@s", s.Id, "@o", orderId, "@n", EasyOrders.Cut(name, 40), "@cu", EasyOrders.Cut(first, 100), "@p", phone, "@t", EasyOrders.Cut(total, 40),
                            "@st", status, "@no", EasyOrders.Cut(note, 300)))
                            rowId = Convert.ToInt64(cmd.ExecuteScalar());
                    }
                    catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return; }
                    if (status != "pending") return;

                    string token = WaAccount.Token(c, s);
                    if (string.IsNullOrEmpty(s.WaInstance) || token == null || string.IsNullOrEmpty(s.WaNumber)) { Mark(c, rowId, "failed", "واتساب المتجر مش مربوط، فالرسالة ماتبعتتش"); return; }
                    string text = Message(s, o, name, first, total, cod);
                    long? mine = WhatsAppBot.Save(c, s.Id, phone, false, text, null);   // part of the chat history, and recognised when Wapilot echoes it
                    try { await Wapilot.Send(token, s.WaInstance, phone + "@c.us", text, "sb-eo-" + rowId); }
                    catch (WaException ex)
                    {
                        if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                        Mark(c, rowId, "failed", ex.Message);
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        static string Message(StoreInfo s, JsonElement o, string name, string first, string total, bool cod)
        {
            var sb = new StringBuilder();
            sb.Append("أهلًا").Append(string.IsNullOrWhiteSpace(first) ? "" : " " + first).Append(" 👋\n");
            sb.Append("شكرًا إنك طلبت من *").Append(s.Name).Append("*!\n\n");
            sb.Append("🧾 *طلب رقم ").Append(name).Append("*\n");
            foreach (var line in EasyOrders.ItemLines(o)) sb.Append("• ").Append(line).Append("\n");
            if (!string.IsNullOrEmpty(total)) sb.Append("💰 الإجمالي: *").Append(total).Append("*\n");
            if (cod) sb.Append("💵 الدفع: كاش عند الاستلام\n");
            string addr = string.Join("، ", new[] { EasyOrders.Str(o, "address"), EasyOrders.Str(o, "government") }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            if (addr.Length > 0) sb.Append("📍 التوصيل: ").Append(addr.Length > 200 ? addr.Substring(0, 200) : addr).Append("\n");
            sb.Append("\nمن فضلك أكّد طلبك عشان نبدأ نجهّزه:\n");
            sb.Append("اكتب *1* لتأكيد الطلب ✅\n");
            sb.Append("اكتب *2* لإلغاء الطلب ❌");
            return sb.ToString();
        }

        // The owner (or our own answer) moved the order on in EasyOrders: stop waiting for the customer.
        public static void OnStatus(SqlConnection c, StoreInfo s, string orderId, string newStatus)
        {
            if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(newStatus) || newStatus == "pending") return;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.EoConfirms SET Status='skipped', Note=@n WHERE StoreId=@s AND OrderId=@o AND Status='pending'",
                "@n", EasyOrders.Cut("اتغيّرت حالته في EasyOrders لـ «" + EasyOrders.StatusText(newStatus) + "» قبل ما الزبون يرد", 300), "@s", s.Id, "@o", orderId))
                cmd.ExecuteNonQuery();
        }

        // ---------- the customer's answer ----------
        // Called for every text a customer sends to the store's number, before the assistant. True = handled here.
        public static bool Intercept(SqlConnection c, StoreInfo s, string chatKey, string chat, string text, long rowId)
        {
            bool? yes = OrderConfirm.Decide(text);
            if (yes == null) return false;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.EoConfirms WHERE StoreId=@s AND Status='pending' AND CreatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME())", "@s", s.Id, "@d", AnswerDays))
                if ((int)cmd.ExecuteScalar() == 0) return false;

            int storeId = s.Id;
            string phone = WaChat.Digits(chatKey);
            if (phone != null)
            {
                long? oc = Pending(c, storeId, phone);
                if (oc == null || !Claim(c, oc.Value, yes.Value)) return false;
                HostingEnvironment.QueueBackgroundWorkItem(ct => Act(storeId, oc.Value, yes.Value, chat, phone));
                return true;
            }
            // "...@lid": ask Wapilot for the number behind it, then either answer the order or let the assistant reply
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    long? oc = null; string num = null;
                    using (var c2 = Db.Open())
                    {
                        var s2 = Db.StoreById(c2, storeId);
                        var token = s2 == null ? null : WaAccount.Token(c2, s2);
                        if (token != null && !string.IsNullOrEmpty(s2.WaInstance)) num = await Wapilot.LidPhone(token, s2.WaInstance, chat);
                        if (num != null) { oc = Pending(c2, storeId, num); if (oc != null && !Claim(c2, oc.Value, yes.Value)) oc = null; }
                    }
                    if (oc != null) await Act(storeId, oc.Value, yes.Value, chat, chatKey);
                    else await WhatsAppBot.Reply(storeId, chatKey, chat, rowId, ct);
                }
                catch (Exception ex) { Log.Error(ex); }
            });
            return true;
        }

        static long? Pending(SqlConnection c, int storeId, string phone)
        {
            string tail = phone.Length > 9 ? phone.Substring(phone.Length - 9) : phone;
            using (var cmd = Db.Cmd(c, @"SELECT TOP 1 Id FROM dbo.EoConfirms WHERE StoreId=@s AND Status='pending' AND Phone IS NOT NULL
                AND RIGHT(Phone, 9) = @t AND CreatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", storeId, "@t", tail, "@d", AnswerDays))
            {
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? (long?)null : Convert.ToInt64(v);
            }
        }

        // only one answer wins, even when the customer sends "1" twice
        static bool Claim(SqlConnection c, long id, bool yes)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.EoConfirms SET Status=@st, AnsweredAt=SYSUTCDATETIME() WHERE Id=@id AND Status='pending'",
                "@st", yes ? "confirmed" : "cancelled", "@id", id))
                return cmd.ExecuteNonQuery() == 1;
        }

        static async Task Act(int storeId, long id, bool yes, string chat, string chatKey)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (s == null) return;
                    string orderId, name;
                    using (var cmd = Db.Cmd(c, "SELECT OrderId, OrderName FROM dbo.EoConfirms WHERE Id=@id", "@id", id))
                    using (var r = cmd.ExecuteReader()) { if (!r.Read()) return; orderId = (string)r["OrderId"]; name = r["OrderName"] as string ?? ""; }
                    var k = EasyOrders.For(s);

                    // the order as it is now: only a pending order gets confirmed, and only a pending/confirmed one gets canceled
                    string current = null, err = null, eoStore = s.EoStoreId;
                    if (k == null) err = "EasyOrders مش مربوط";
                    else
                    {
                        try { var o = await EasyOrders.GetOrder(k, orderId); current = EasyOrders.Str(o, "status"); eoStore = EasyOrders.Str(o, "store_id") ?? eoStore; }
                        catch (EoException ex) { err = ex.Message; }
                    }

                    string reply;
                    if (yes)
                    {
                        if (err == null && current == "pending")
                        {
                            try { await EasyOrders.SetOrderStatus(k, orderId, "confirmed"); } catch (EoException ex) { err = ex.Message; }
                        }
                        if (err != null) Mark(c, id, "failed", "الزبون أكد الطلب، بس الحالة في EasyOrders ماتغيّرتش: " + err + ". غيّرها لـ confirmed بإيدك.");
                        else await EasyOrders.AddNote(k, orderId, eoStore, "✅ الزبون أكد الطلب على واتساب (Sellybot)");
                        reply = "تمام ✅ طلبك " + name + " اتأكد، وهنبدأ نجهّزه على طول. شكرًا لثقتك في *" + s.Name + "* 🙏";
                    }
                    else
                    {
                        if (err == null && current != "pending" && current != "confirmed")
                            err = "الطلب حالته «" + EasyOrders.StatusText(current) + "»، فمالغيناهوش لوحدنا";
                        if (err == null)
                        {
                            try { await EasyOrders.SetOrderStatus(k, orderId, "canceled"); } catch (EoException ex) { err = ex.Message; }
                        }
                        if (err == null)
                        {
                            await EasyOrders.AddNote(k, orderId, eoStore, "❌ الزبون لغى الطلب من رسالة التأكيد على واتساب (Sellybot)");
                            reply = "تم إلغاء طلبك " + name + " ❌\nلو غيّرت رأيك أو محتاج أي حاجة، ابعتلنا هنا في أي وقت.";
                        }
                        else
                        {
                            Mark(c, id, "failed", "الزبون رفض الطلب، بس الإلغاء في EasyOrders مانجحش: " + err + ". الغيه بإيدك.");
                            reply = "وصلنا إنك عايز تلغي طلبك " + name + "، وهنتواصل معاك نأكد الإلغاء 🙏";
                        }
                    }

                    var token = WaAccount.Token(c, s);
                    if (token == null || string.IsNullOrEmpty(s.WaInstance)) return;
                    long? mine = WhatsAppBot.Save(c, s.Id, chatKey, false, reply, null);
                    try { await Wapilot.Send(token, s.WaInstance, chat, reply, "sb-eo-ans-" + id); }
                    catch (WaException ex)
                    {
                        Log.Error(ex);
                        if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // a line for the assistant when the customer writes something else while an order waits for their answer
        public static string PendingNote(SqlConnection c, int storeId, string chatKey)
        {
            var phone = WaChat.Digits(chatKey);
            if (phone == null) return "";
            var id = Pending(c, storeId, phone);
            if (id == null) return "";
            using (var cmd = Db.Cmd(c, "SELECT OrderName FROM dbo.EoConfirms WHERE Id=@id", "@id", id.Value))
            {
                var name = cmd.ExecuteScalar() as string;
                return "\n- الزبون ده عنده طلب " + name + " مستني تأكيده (اتبعتله رسالة بتفاصيله). لو كلامه معناه إنه موافق أو عايز يلغي، قوله يكتب 1 للتأكيد أو 2 للإلغاء. ماتقولش إن الطلب اتأكد أو اتلغى بنفسك.";
            }
        }

        public static List<Dictionary<string, object>> Recent(SqlConnection c, int storeId, int n = 15)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP (@n) OrderName, Customer, Total, Status, Note, CreatedAt, AnsweredAt FROM dbo.EoConfirms WHERE StoreId=@s ORDER BY Id DESC", "@n", n, "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> {
                        { "order", r["OrderName"] as string ?? "" }, { "customer", r["Customer"] as string ?? "" }, { "total", r["Total"] as string ?? "" },
                        { "status", r["Status"] as string }, { "note", r["Note"] as string ?? "" },
                        { "at", DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc).ToString("o") },
                        { "answeredAt", r["AnsweredAt"] == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)r["AnsweredAt"], DateTimeKind.Utc).ToString("o") } });
            return list;
        }

        static void Mark(SqlConnection c, long id, string status, string note)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.EoConfirms SET Status=@st, Note=@n WHERE Id=@id", "@st", status, "@n", EasyOrders.Cut(note, 300), "@id", id)) cmd.ExecuteNonQuery();
        }
    }

    // api/easyorders.ashx
    //   POST ?action=callback&store=&key=      EasyOrders' install callback {api_key, store_id} (no login: the key in the URL proves the store)
    //   GET                                     the store's EasyOrders state (the same as "easyorders" in auth.ashx?action=me)
    //   POST ?action=connect {key, url}         connect with a pasted Public API key and pull the products
    //   POST ?action=sync {url?}                pull again (with a new store link when given)
    //   POST ?action=orders {enabled}           order tracking in the chat on/off
    //   POST ?action=confirm {enabled, scope}   order confirmation on WhatsApp on/off; scope "all" or "cod"
    //   POST ?action=disconnect
    public class EasyOrdersHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
            string origin = EasyOrders.Origin(req);
            try
            {
                using (var c = Db.Open())
                {
                    if (action == "callback") { Callback(req, res, c); return; }

                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, EasyOrders.Describe(c, s)); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!Web.Allow("eo:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "تعديلات كتير، استنى شوية."); return; }
                    var body = Web.Body(req, 4000);
                    object en; bool enabled = body.TryGetValue("enabled", out en) && en is bool && (bool)en;
                    bool gated = action == "connect" || action == "sync" || ((action == "orders" || action == "confirm") && enabled);
                    if (gated && !PlanGate.Allows(c, s, EasyOrders.Feature)) { PlanGate.Deny(res, c, s, EasyOrders.Feature); return; }
                    if (action == "connect" && PlanGate.BothBlocked(c, s, "easyorders")) { PlanGate.DenyBoth(res, c, s, "easyorders"); return; }   // Shopify already linked
                    if (action != "connect" && action != "disconnect" && !EasyOrders.Connected(s)) { Web.Error(res, 400, "not_connected", "اربط متجرك على EasyOrders الأول."); return; }

                    switch (action)
                    {
                        case "connect":
                        case "sync":
                        {
                            if (!Web.Allow("eo-sync:" + s.Id, 12, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                            EasyOrders.Creds k;
                            if (action == "connect")
                            {
                                string key = (Web.Str(body, "key", 300) ?? "").Trim();
                                if (!EasyOrders.ValidKey(key)) { Web.Error(res, 400, "bad_key", "الـ API Key مش مظبوط. انسخه زي ما هو من EasyOrders (Settings ← Public API)."); return; }
                                k = new EasyOrders.Creds { StoreId = s.Id, Key = key };
                            }
                            else k = EasyOrders.For(s);
                            if (k == null) { Web.Error(res, 400, "not_connected", "اربط متجرك على EasyOrders الأول."); return; }
                            string url = s.EoUrl;
                            if (body.ContainsKey("url"))
                            {
                                bool bad; url = EasyOrders.NormalizeUrl(Web.Str(body, "url", 300), out bad);
                                if (bad) { Web.Error(res, 400, "bad_url", "لينك المتجر مش مظبوط. اكتبه زي ما زباينك بيفتحوه، مثلًا mystore.myeasyorders.com"); return; }
                            }
                            var early = Storage.CheckRoom(c, 0);
                            if (early != null) { Web.Error(res, 507, "storage_full", early); return; }

                            EasyOrders.Sync r;
                            try { using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150))) r = await EasyOrders.FetchCatalog(k, url, cts.Token); }
                            catch (EoException ex) { Web.Error(res, 400, "eo_failed", ex.Message); return; }
                            catch (OperationCanceledException) { Web.Error(res, 504, "eo_slow", "سحب المنتجات أخد وقت طويل. جرّب تاني كمان شوية."); return; }
                            if (action == "connect")
                                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoKey=@k, EoMode='key', EoUrl=@u WHERE Id=@s", "@k", Secrets.Protect(k.Key), "@u", (object)url ?? DBNull.Value, "@s", s.Id)) cmd.ExecuteNonQuery();
                            else if (url != s.EoUrl)
                                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoUrl=@u WHERE Id=@s", "@u", (object)url ?? DBNull.Value, "@s", s.Id)) cmd.ExecuteNonQuery();
                            var saveErr = EasyOrders.Save(c, s.Id, r, EasyOrders.FileName(url));
                            if (saveErr != null) { Web.Error(res, 400, "too_much", saveErr); return; }
                            Web.Json(res, 200, new Dictionary<string, object> { { "products", r.Products }, { "chars", r.Text.Length } });
                            return;
                        }
                        case "orders":
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoOrders=@o WHERE Id=@s", "@o", enabled, "@s", s.Id)) cmd.ExecuteNonQuery();
                            Db.BumpVersion(c, s.Id);   // the assistant's instructions change with it
                            Web.Json(res, 200, new { ok = true, orders = enabled });
                            return;
                        case "confirm":
                        {
                            string scope = Web.Str(body, "scope", 10) == "cod" ? "cod" : "all";
                            if (enabled && (string.IsNullOrEmpty(s.WaInstance) || string.IsNullOrEmpty(s.WaNumber)))
                            { Web.Error(res, 400, "no_whatsapp", "اربط واتساب المتجر الأول من صفحة الواتساب، عشان الرسايل تتبعت من رقمك."); return; }
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoOcOn=@o, EoOcScope=@sc WHERE Id=@s", "@o", enabled, "@sc", scope, "@s", s.Id)) cmd.ExecuteNonQuery();
                            Web.Json(res, 200, new { ok = true, enabled, scope });
                            return;
                        }
                        case "disconnect":
                            await EasyOrders.Disconnect(c, s, origin);
                            Web.Json(res, 200, new { ok = true });
                            return;
                    }
                    Web.Error(res, 400, "bad_request");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        // EasyOrders posts {api_key, store_id} after the owner approves the app; the catalog is pulled in the background.
        static void Callback(HttpRequest req, HttpResponse res, SqlConnection c)
        {
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }
            if (!Web.Allow("eo-cb:" + Web.Ip(req), 30, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited"); return; }
            string slug = (req.QueryString["store"] ?? "").ToLowerInvariant();
            var s = Web.SlugRx.IsMatch(slug) ? Db.StoreBySlug(c, slug) : null;
            if (!EasyOrders.CallbackOk(s, req.QueryString["key"])) { Web.Error(res, 401, "bad_key"); return; }

            string apiKey = null, eoStore = null;
            if ((req.ContentType ?? "").IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 || string.IsNullOrEmpty(req.ContentType))
            {
                var b = Web.Body(req, 4000);
                apiKey = Web.Str(b, "api_key", 300); eoStore = Web.Str(b, "store_id", 100);
            }
            else { apiKey = req.Form["api_key"]; eoStore = req.Form["store_id"]; }
            apiKey = (apiKey ?? req.QueryString["api_key"] ?? "").Trim();
            eoStore = (eoStore ?? req.QueryString["store_id"] ?? "").Trim();
            if (!EasyOrders.ValidKey(apiKey)) { Web.Error(res, 400, "bad_key"); return; }
            if (!PlanGate.Allows(c, s, EasyOrders.Feature)) { PlanGate.Deny(res, c, s, EasyOrders.Feature); return; }
            if (PlanGate.BothBlocked(c, s, "easyorders")) { PlanGate.DenyBoth(res, c, s, "easyorders"); return; }

            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoKey=@k, EoMode='app', EoStoreId=@e, EoStatus=@st, EoSyncedAt=NULL WHERE Id=@s",
                "@k", Secrets.Protect(apiKey), "@e", eoStore.Length > 0 ? (object)EasyOrders.Cut(eoStore, 64) : DBNull.Value, "@st", "⏳ بنسحب المنتجات…", "@s", s.Id))
                cmd.ExecuteNonQuery();
            int storeId = s.Id;
            HostingEnvironment.QueueBackgroundWorkItem(ct => EasyOrders.SyncNow(storeId, ct));
            Web.Json(res, 200, new { ok = true });
        }
    }

    // api/easyorders-webhook.ashx?store=<slug>&key=<key> — EasyOrders calls this for a new order and when an order's status changes.
    public class EasyOrdersWebhookHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }
            byte[] body;
            using (var ms = new MemoryStream()) { req.InputStream.CopyTo(ms); body = ms.ToArray(); }
            if (body.Length > 1024 * 1024) { res.StatusCode = 413; return; }
            string slug = (req.QueryString["store"] ?? "").ToLowerInvariant();
            try
            {
                using (var c = Db.Open())
                {
                    var s = Web.SlugRx.IsMatch(slug) ? Db.StoreBySlug(c, slug) : null;
                    if (!EasyOrders.Connected(s) || !EasyOrders.HookOk(s, req.QueryString["key"])) { res.StatusCode = 401; return; }
                    if (!Web.Allow("eo-hook:" + s.Id, 600, TimeSpan.FromHours(1))) { res.StatusCode = 429; return; }
                    JsonElement o;
                    try { using (var doc = JsonDocument.Parse(body)) o = doc.RootElement.Clone(); }
                    catch (JsonException) { res.StatusCode = 400; return; }
                    if (o.ValueKind == JsonValueKind.Object && !o.TryGetProperty("id", out _) && o.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object) o = inner;
                    if (o.ValueKind != JsonValueKind.Object) { res.StatusCode = 400; return; }
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET EoHookAt=SYSUTCDATETIME() WHERE Id=@s", "@s", s.Id)) cmd.ExecuteNonQuery();

                    if (EasyOrders.Str(o, "event_type") == "order-status-update")
                        EoConfirm.OnStatus(c, s, EasyOrders.Str(o, "order_id"), EasyOrders.Str(o, "new_status"));
                    else
                    {
                        string id = EasyOrders.Str(o, "id");
                        if (id != null && Regex.IsMatch(id, "^[A-Za-z0-9-]{6,64}$"))
                        {
                            if (s.EoOcOn) EoConfirm.Queue(s.Id, id, EasyOrders.Str(o, "short_id"));
                            EasyOrders.QueueResync(s.Id);   // the stock changed
                        }
                    }
                    res.StatusCode = 200;
                }
            }
            catch (Exception ex) { Log.Error(ex); res.StatusCode = 500; }
        }
    }
}
