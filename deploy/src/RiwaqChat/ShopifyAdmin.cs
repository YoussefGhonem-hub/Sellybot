// Full Shopify connection through the official Admin GraphQL API.
// The store owner creates an app for their own store in the Shopify Dev Dashboard (read-only scopes), installs it,
// and gives us its Client ID + Client secret. With those we ask Shopify for an access token (client credentials grant,
// valid 24 hours, renewed automatically) and read: products with exact stock, orders (for "where is my order?"),
// and we subscribe to product/inventory webhooks so changes reach the assistant within seconds.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
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
    // ---------- secrets at rest: AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC), keys derived from AuthSecret ----------
    public static class Secrets
    {
        public static string Protect(string plain) { return Protect(plain, Web.Setting("AuthSecret")); }
        public static string Unprotect(string box) { return Unprotect(box, Web.Setting("AuthSecret")); }

        public static string Protect(string plain, string master)
        {
            if (plain == null) return null;
            byte[] encKey = Derive(master, "enc"), macKey = Derive(master, "mac");
            using (var aes = Aes.Create())
            {
                aes.Key = encKey; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.GenerateIV();
                byte[] data = Encoding.UTF8.GetBytes(plain), cipher;
                using (var enc = aes.CreateEncryptor()) cipher = enc.TransformFinalBlock(data, 0, data.Length);
                var body = aes.IV.Concat(cipher).ToArray();
                byte[] mac; using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(body);
                return "v1:" + Convert.ToBase64String(body.Concat(mac).ToArray());
            }
        }

        public static string Unprotect(string box, string master)
        {
            try
            {
                if (string.IsNullOrEmpty(box) || !box.StartsWith("v1:")) return null;
                var all = Convert.FromBase64String(box.Substring(3));
                if (all.Length < 16 + 16 + 32) return null;
                var body = all.Take(all.Length - 32).ToArray();
                var mac = all.Skip(all.Length - 32).ToArray();
                byte[] expect; using (var h = new HMACSHA256(Derive(master, "mac"))) expect = h.ComputeHash(body);
                if (!Same(mac, expect)) return null;
                using (var aes = Aes.Create())
                {
                    aes.Key = Derive(master, "enc"); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    aes.IV = body.Take(16).ToArray();
                    using (var dec = aes.CreateDecryptor()) { var c = body.Skip(16).ToArray(); return Encoding.UTF8.GetString(dec.TransformFinalBlock(c, 0, c.Length)); }
                }
            }
            catch { return null; }
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        static byte[] Derive(string master, string purpose)
        {
            if (string.IsNullOrEmpty(master) || master.Length < 32) throw new InvalidOperationException("AuthSecret is not configured");
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(master))) return h.ComputeHash(Encoding.UTF8.GetBytes("sellybot-shopify-" + purpose));
        }
    }

    public static class ShopifyAdmin
    {
        public static string ApiVersion { get { return Web.Setting("ShopifyApiVersion", "2026-10"); } }
        // Shown to the store owner; read-only on purpose.
        public static readonly string[] Wanted = { "read_products", "read_inventory", "read_orders" };

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        static ShopifyAdmin() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0"); }

        public class Creds { public int StoreId; public string Shop, ClientId, Secret, Slug; }

        // "riwaq", "riwaq.myshopify.com", "https://admin.shopify.com/store/riwaq/..." -> "riwaq.myshopify.com"
        static readonly Regex ShopRx = new Regex(@"^[a-z0-9][a-z0-9-]{0,60}\.myshopify\.com$", RegexOptions.Compiled);
        public static string NormalizeShop(string input)
        {
            var s = (input ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return null;
            var m = Regex.Match(s, @"admin\.shopify\.com/store/([a-z0-9][a-z0-9-]{0,60})");
            if (m.Success) return m.Groups[1].Value + ".myshopify.com";
            s = Regex.Replace(s, @"^https?://", "");
            s = s.Split('/', '?', '#')[0].TrimEnd('.');
            if (Regex.IsMatch(s, @"^[a-z0-9][a-z0-9-]{0,60}$")) s += ".myshopify.com";
            return ShopRx.IsMatch(s) ? s : null;
        }

        public static Creds For(StoreInfo s)
        {
            if (s == null || string.IsNullOrEmpty(s.ShopifyClientId) || string.IsNullOrEmpty(s.ShopifyShop)) return null;
            var secret = Secrets.Unprotect(s.ShopifySecret);
            if (string.IsNullOrEmpty(secret)) return null;
            return new Creds { StoreId = s.Id, Shop = s.ShopifyShop, ClientId = s.ShopifyClientId, Secret = secret, Slug = s.Slug };
        }

        public static bool Has(string scopes, string scope)
        {
            return (scopes ?? "").Split(',').Select(x => x.Trim()).Any(x => x == scope || (scope.StartsWith("read_") && x == "write_" + scope.Substring(5)));
        }

        // ---------- access token (24 h), cached in memory ----------
        public class TokenInfo { public string Token, Scopes; public DateTime Expires; }
        static readonly ConcurrentDictionary<string, TokenInfo> Tokens = new ConcurrentDictionary<string, TokenInfo>();

        public static void Forget(Creds c) { TokenInfo x; if (c != null) Tokens.TryRemove(c.Shop + "|" + c.ClientId, out x); }

        public static async Task<TokenInfo> Token(Creds c, bool fresh = false)
        {
            string key = c.Shop + "|" + c.ClientId;
            TokenInfo t;
            if (!fresh && Tokens.TryGetValue(key, out t) && t.Expires > DateTime.UtcNow.AddMinutes(5)) return t;

            var form = new FormUrlEncodedContent(new Dictionary<string, string> {
                { "grant_type", "client_credentials" }, { "client_id", c.ClientId }, { "client_secret", c.Secret } });
            HttpResponseMessage resp;
            try { resp = await Http.PostAsync("https://" + c.Shop + "/admin/oauth/access_token", form); }
            catch (Exception ex) { Log.Error(ex); throw new ShopifyException("مقدرناش نوصل لـ Shopify. اتأكد من دومين المتجر (اللي آخره .myshopify.com) وجرّب تاني."); }
            using (resp)
            {
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    string err = body ?? "";
                    if (err.Contains("shop_not_permitted"))
                        throw new ShopifyException("Shopify رفضت الربط: التطبيق لازم يكون معمول من نفس حساب المتجر في Dev Dashboard ومتركّب عليه. راجع الخطوات 1 و4.");
                    if (err.Contains("application_cannot_be_found"))
                        throw new ShopifyException("Shopify مالقتش تطبيق بالـ Client ID ده. انسخه تاني من Settings بتاع التطبيق في Dev Dashboard (ومن غير مسافات).");
                    if (err.Contains("invalid_client") || (int)resp.StatusCode == 401)
                        throw new ShopifyException("الـ Client ID أو الـ Client secret مش صح. انسخهم تاني من إعدادات التطبيق في Dev Dashboard.");
                    if ((int)resp.StatusCode == 404)
                        throw new ShopifyException("مالقيناش متجر بالدومين ده. اكتب الدومين اللي آخره .myshopify.com (تلاقيه في Shopify: Settings ← Domains).");
                    if (err.Contains("not_installed") || err.Contains("installation"))
                        throw new ShopifyException("التطبيق لسه مش متركّب على متجرك. دوس Install عليه من Dev Dashboard (الخطوة 4).");
                    var plain = Regex.Replace(Regex.Replace(Regex.Replace(err, @"<(style|script)[\s\S]*?</\1>", " "), "<[^>]+>", " "), @"\s+", " ").Trim();
                    Log.Error(new Exception("Shopify token " + (int)resp.StatusCode + ": " + (plain.Length > 200 ? plain.Substring(0, 200) : plain)));
                    throw new ShopifyException("Shopify رفضت الربط (" + (int)resp.StatusCode + "). اتأكد إن التطبيق متركّب على متجرك وإن المفاتيح صح.");
                }
                using (var doc = JsonDocument.Parse(body))
                {
                    var r = doc.RootElement;
                    t = new TokenInfo { Token = Str(r, "access_token"), Scopes = Str(r, "scope") ?? "" };
                    JsonElement ex;
                    int secs = r.TryGetProperty("expires_in", out ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt32() : 86399;
                    t.Expires = DateTime.UtcNow.AddSeconds(secs);
                }
                if (string.IsNullOrEmpty(t.Token)) throw new ShopifyException("Shopify مارجّعتش مفتاح دخول. جرّب تاني.");
                Tokens[key] = t;
                return t;
            }
        }

        // ---------- GraphQL ----------
        public class Gql { public JsonElement Data; public List<string> Errors = new List<string>(); public int Available = 1000; }

        public static async Task<Gql> Query(Creds c, string query, object variables, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var tok = await Token(c);   // after a 401 the cached token is dropped, so this fetches a new one
                var payload = JsonSerializer.Serialize(new { query, variables });
                var req = new HttpRequestMessage(HttpMethod.Post, "https://" + c.Shop + "/admin/api/" + ApiVersion + "/graphql.json")
                { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                req.Headers.Add("X-Shopify-Access-Token", tok.Token);
                using (var resp = await Http.SendAsync(req, ct))
                {
                    if ((int)resp.StatusCode == 401) { Forget(c); if (attempt < 1) continue; throw new ShopifyException("Shopify رفضت المفتاح. اربط تاني من الداشبورد."); }
                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500) { await Task.Delay(1500 * (attempt + 1), ct); continue; }
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) throw new ShopifyException("Shopify رجّعت خطأ (" + (int)resp.StatusCode + ").");
                    var g = new Gql();
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        JsonElement errs, data, ext;
                        bool throttled = false;
                        if (root.TryGetProperty("errors", out errs) && errs.ValueKind == JsonValueKind.Array)
                            foreach (var e in errs.EnumerateArray())
                            {
                                var msg = Str(e, "message") ?? "error";
                                JsonElement exts, code;
                                if (e.TryGetProperty("extensions", out exts) && exts.TryGetProperty("code", out code) && code.GetString() == "THROTTLED") throttled = true;
                                g.Errors.Add(msg);
                            }
                        if (throttled) { await Task.Delay(2000 * (attempt + 1), ct); continue; }
                        if (root.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object) g.Data = data.Clone();
                        if (root.TryGetProperty("extensions", out ext) && ext.TryGetProperty("cost", out var cost) && cost.TryGetProperty("throttleStatus", out var ts)
                            && ts.TryGetProperty("currentlyAvailable", out var ca) && ca.ValueKind == JsonValueKind.Number) g.Available = (int)ca.GetDouble();
                    }
                    if (g.Data.ValueKind != JsonValueKind.Object) throw new ShopifyException("Shopify: " + (g.Errors.Count > 0 ? g.Errors[0] : "مافيش داتا."));
                    return g;
                }
            }
            throw new ShopifyException("Shopify مشغولة دلوقتي، جرّب كمان شوية.");
        }

        // ---------- catalog: products with exact stock ----------
        public static async Task<Shopify.SyncResult> FetchCatalog(Creds c, CancellationToken ct)
        {
            var tok = await Token(c);
            bool inv = Has(tok.Scopes, "read_inventory");
            if (!Has(tok.Scopes, "read_products")) throw new ShopifyException("التطبيق ناقصه صلاحية read_products، فمش هنقدر نقرا المنتجات. ضيفها في Scopes واعمل Release للنسخة ووافق عليها في متجرك.");

            string variantFields = "title price compareAtPrice availableForSale" + (inv ? " inventoryQuantity" : "");
            string q = "query($cursor:String){ shop { name currencyCode primaryDomain { host } } " +
                       "products(first:20, after:$cursor, query:\"status:active\") { pageInfo { hasNextPage endCursor } nodes { title handle productType vendor tags descriptionHtml onlineStoreUrl" +
                       (inv ? " tracksInventory" : "") + " variants(first:30) { nodes { " + variantFields + " } } } } }";

            var res = new Shopify.SyncResult { Domain = c.Shop };
            var products = new StringBuilder();
            string cursor = null, host = null;
            for (int page = 0; page < 500; page++)
            {
                var g = await Query(c, q, new { cursor }, ct);
                if (page == 0)
                {
                    JsonElement shop;
                    if (g.Data.TryGetProperty("shop", out shop))
                    {
                        res.ShopName = Str(shop, "name"); res.Currency = Str(shop, "currencyCode");
                        JsonElement pd; if (shop.TryGetProperty("primaryDomain", out pd) && pd.ValueKind == JsonValueKind.Object) host = Str(pd, "host");
                    }
                }
                JsonElement pr = g.Data.GetProperty("products");
                foreach (var p in pr.GetProperty("nodes").EnumerateArray()) { AppendProduct(products, p, host ?? c.Shop, res.Currency, inv); res.Products++; }
                var pi = pr.GetProperty("pageInfo");
                if (pi.GetProperty("hasNextPage").ValueKind != JsonValueKind.True) break;
                cursor = Str(pi, "endCursor");
                if (g.Available < 700) await Task.Delay(Math.Min(10000, (700 - g.Available) * 12), ct);   // let the cost bucket refill
            }

            // policies (shipping, returns...) come from the store's public pages, when the store is open
            string policies = ""; int nPol = 0;
            try { var pt = await Shopify.FetchPolicies(host ?? c.Shop, ct); policies = pt.Item1; nPol = pt.Item2; } catch (Exception ex) { if (!(ex is ShopifyException)) Log.Error(ex); }
            res.Policies = nPol;

            var sb = new StringBuilder();
            sb.Append("دي داتا المتجر على Shopify (").Append(host ?? c.Shop).Append("). بتتحدث تلقائيًا من Shopify نفسه، والكميات اللي فيها هي المتاح فعلًا في المخزن.");
            if (!string.IsNullOrEmpty(res.ShopName)) sb.Append("\nاسم المتجر على Shopify: ").Append(res.ShopName);
            if (!string.IsNullOrEmpty(res.Currency)) sb.Append("\nالعملة: ").Append(res.Currency);
            sb.Append("\nعدد المنتجات: ").Append(res.Products);
            if (policies.Length > 0) sb.Append("\n\n# سياسات المتجر").Append(policies);
            if (products.Length > 0) sb.Append("\n\n# المنتجات").Append(products);
            res.Text = sb.ToString();
            if (res.Text.Length > KnowledgeHandler.MaxItemChars) res.Text = res.Text.Substring(0, KnowledgeHandler.MaxItemChars);
            if (host != null) res.Domain = host;
            return res;
        }

        static void AppendProduct(StringBuilder sb, JsonElement p, string host, string currency, bool inv)
        {
            string title = Str(p, "title");
            if (string.IsNullOrWhiteSpace(title)) return;
            sb.Append("\n\n## ").Append(title.Trim());
            var meta = new List<string>();
            string type = Str(p, "productType"), vendor = Str(p, "vendor");
            if (!string.IsNullOrWhiteSpace(type)) meta.Add("النوع: " + type.Trim());
            if (!string.IsNullOrWhiteSpace(vendor)) meta.Add("الماركة: " + vendor.Trim());
            JsonElement tags;
            if (p.TryGetProperty("tags", out tags) && tags.ValueKind == JsonValueKind.Array)
            {
                var t = tags.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString().Trim())
                    .Where(x => x.Length > 1 && x.Length <= 30 && !x.Contains("::") && !x.Contains("=>") && !Regex.IsMatch(x, @"\d{5,}")).Take(12).ToList();
                if (t.Count > 0) meta.Add("تصنيفات: " + string.Join("، ", t));
            }
            if (meta.Count > 0) sb.Append("\n").Append(string.Join(" | ", meta));

            bool tracks = inv && p.TryGetProperty("tracksInventory", out var tr) && tr.ValueKind == JsonValueKind.True;
            var vs = new List<Tuple<string, string, bool, int?>>();
            foreach (var v in p.GetProperty("variants").GetProperty("nodes").EnumerateArray())
            {
                string vt = (Str(v, "title") ?? "").Trim(); if (vt == "Default Title") vt = "";
                string price = Money(Str(v, "price"), currency) ?? "مش محدد";
                string old = Money(Str(v, "compareAtPrice"), currency);
                if (old != null && Num(Str(v, "compareAtPrice")) > Num(Str(v, "price"))) price += " (بدل " + old + ")";
                int? qty = null;
                if (tracks && v.TryGetProperty("inventoryQuantity", out var q) && q.ValueKind == JsonValueKind.Number) qty = q.GetInt32();
                bool avail = v.TryGetProperty("availableForSale", out var a) && a.ValueKind == JsonValueKind.True;
                vs.Add(Tuple.Create(vt, price, avail, qty));
            }
            Func<Tuple<string, string, bool, int?>, string> stock = x =>
                !x.Item3 ? "خلصان" : (x.Item4.HasValue && x.Item4.Value > 0 ? "متاح، فاضل " + x.Item4.Value : "متاح");
            if (vs.Count == 1) sb.Append("\nالسعر ").Append(vs[0].Item2).Append(" · ").Append(stock(vs[0]));
            else if (vs.Count > 1)
            {
                var prices = vs.Select(x => x.Item2).Distinct().ToList();
                if (prices.Count == 1)
                {
                    sb.Append("\nالسعر ").Append(prices[0]);
                    var yes = vs.Where(x => x.Item3).Select(x => x.Item1 + (x.Item4.HasValue && x.Item4.Value > 0 ? " (فاضل " + x.Item4.Value + ")" : "")).ToList();
                    var no = vs.Where(x => !x.Item3).Select(x => x.Item1).ToList();
                    if (yes.Count > 0) sb.Append("\nمتاح: ").Append(string.Join("، ", yes));
                    if (no.Count > 0) sb.Append("\nخلصان: ").Append(string.Join("، ", no));
                }
                else sb.Append("\nالاختيارات:\n- ").Append(string.Join("\n- ", vs.Select(x => (x.Item1.Length > 0 ? x.Item1 + ": " : "") + "السعر " + x.Item2 + " · " + stock(x))));
            }
            if (vs.Count > 0 && !vs.Any(x => x.Item3)) sb.Append("\n(المنتج ده خلصان دلوقتي)");
            string desc = Shopify.HtmlToText(Str(p, "descriptionHtml"));
            if (desc.Length > 1200) desc = desc.Substring(0, 1200) + " …";
            if (desc.Length > 0) sb.Append("\nالوصف: ").Append(desc);
            string url = Str(p, "onlineStoreUrl");
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(Str(p, "handle"))) url = "https://" + host + "/products/" + Str(p, "handle");
            if (!string.IsNullOrEmpty(url)) sb.Append("\nاللينك: ").Append(url);
        }

        // ---------- orders: "where is my order?" ----------
        public const string OrderToolName = "lookup_order";

        // Rate limits so nobody can guess other people's orders: per visitor and per store.
        public static string LookupBlocked(int storeId, string ip)
        {
            if (!Web.Allow("ord-ip:" + storeId + ":" + ip, 6, TimeSpan.FromMinutes(15)) || !Web.Allow("ord-ip-h:" + storeId + ":" + ip, 15, TimeSpan.FromHours(1)))
                return "الزبون جرّب كذا مرة ورا بعض. قوله بلطف يستنى شوية ويجرّب تاني، أو يكلّم المتجر مباشرة.";
            if (!Web.Allow("ord-store:" + storeId, 300, TimeSpan.FromHours(1)))
                return "تتبع الأوردرات مش متاح دلوقتي. انصح الزبون يكلّم المتجر مباشرة.";
            return null;
        }

        // Returns text for Claude. Order details are only given when the order number AND the email/phone match.
        public static async Task<string> LookupOrder(StoreInfo s, string ip, string orderNumber, string contact)
        {
            const string NoMatch = "البيانات دي مش مطابقة لأي أوردر. ماتقولش أي تفاصيل عن أي أوردر. اطلب من الزبون بلطف يتأكد من رقم الأوردر والإيميل أو الموبايل اللي طلب بيه، ولو المشكلة فضلت يكلّم المتجر.";
            var c = For(s);
            if (c == null || !s.ShopifyOrders) return "تتبع الأوردرات مش متاح في المتجر ده. انصح الزبون يكلّم المتجر مباشرة.";
            var blocked = LookupBlocked(s.Id, ip);
            if (blocked != null) return blocked;

            string num = Regex.Replace(orderNumber ?? "", @"[^\p{L}\p{N}\-]", "");
            num = ToLatinDigits(num);
            string want = (contact ?? "").Trim();
            if (num.Length == 0 || num.Length > 30 || want.Length < 5) return "محتاج رقم الأوردر والإيميل أو الموبايل اللي الطلب اتعمل بيه. اطلبهم من الزبون.";

            const string q = "query($q:String!){ orders(first:5, query:$q, sortKey:CREATED_AT, reverse:true) { nodes { name createdAt cancelledAt displayFinancialStatus displayFulfillmentStatus email phone " +
                             "shippingAddress { phone city } billingAddress { phone } lineItems(first:15) { nodes { name quantity } } " +
                             "fulfillments(first:5) { displayStatus estimatedDeliveryAt deliveredAt inTransitAt trackingInfo(first:3) { company number url } } } } }";
            Gql g;
            try { g = await Query(c, q, new { q = "name:" + num }, CancellationToken.None); }
            catch (ShopifyException ex) { Log.Error(ex); return "مقدرناش نوصل لبيانات الأوردرات دلوقتي. اعتذر للزبون وانصحه يجرّب كمان شوية أو يكلّم المتجر."; }

            foreach (var o in g.Data.GetProperty("orders").GetProperty("nodes").EnumerateArray())
            {
                string name = Str(o, "name") ?? "";
                if (!string.Equals(name.TrimStart('#'), num.TrimStart('#'), StringComparison.OrdinalIgnoreCase)) continue;
                var phones = new List<string> { Str(o, "phone") };
                JsonElement a;
                if (o.TryGetProperty("shippingAddress", out a) && a.ValueKind == JsonValueKind.Object) phones.Add(Str(a, "phone"));
                if (o.TryGetProperty("billingAddress", out a) && a.ValueKind == JsonValueKind.Object) phones.Add(Str(a, "phone"));
                if (!ContactMatches(want, Str(o, "email"), phones))
                {
                    if (string.IsNullOrEmpty(Str(o, "email")) && phones.All(string.IsNullOrEmpty))
                        Log.Error(new Exception("Shopify order lookup: no email/phone returned (protected customer data access?) for store " + s.Id));
                    return NoMatch;
                }
                return Describe(o);
            }
            return NoMatch;
        }

        // Email: exact (case-insensitive). Phone: the last 9 digits must match (works for 010..., +2010..., 002010...).
        public static bool ContactMatches(string given, string email, IEnumerable<string> phones)
        {
            given = ToLatinDigits((given ?? "").Trim());
            if (given.Contains("@"))
                return !string.IsNullOrEmpty(email) && string.Equals(given, email.Trim(), StringComparison.OrdinalIgnoreCase);
            string gd = Regex.Replace(given, @"\D", "");
            if (gd.Length < 8) return false;
            string tail = gd.Substring(gd.Length - Math.Min(9, gd.Length));
            foreach (var p in phones)
            {
                string pd = Regex.Replace(ToLatinDigits(p ?? ""), @"\D", "");
                if (pd.Length >= 8 && pd.EndsWith(tail)) return true;
            }
            return false;
        }

        static string ToLatinDigits(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
                else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        static string Describe(JsonElement o)
        {
            var sb = new StringBuilder();
            sb.Append("البيانات مطابقة. ده الأوردر ").Append(Str(o, "name"));
            var created = Date(Str(o, "createdAt")); if (created != null) sb.Append(" (اتعمل يوم ").Append(created).Append(")");
            sb.Append(":");
            if (!string.IsNullOrEmpty(Str(o, "cancelledAt"))) sb.Append("\n- الأوردر ده اتلغى يوم ").Append(Date(Str(o, "cancelledAt")));
            sb.Append("\n- الدفع: ").Append(Financial(Str(o, "displayFinancialStatus")));
            sb.Append("\n- التوصيل: ").Append(Fulfillment(Str(o, "displayFulfillmentStatus")));
            JsonElement items;
            if (o.TryGetProperty("lineItems", out items))
            {
                var li = items.GetProperty("nodes").EnumerateArray().Select(x => (Str(x, "name") ?? "") + (x.TryGetProperty("quantity", out var qn) && qn.ValueKind == JsonValueKind.Number && qn.GetInt32() > 1 ? " ×" + qn.GetInt32() : "")).ToList();
                if (li.Count > 0) sb.Append("\n- المنتجات: ").Append(string.Join("، ", li));
            }
            JsonElement fs;
            if (o.TryGetProperty("fulfillments", out fs) && fs.ValueKind == JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray())
                {
                    sb.Append("\n- شحنة: ").Append(Shipment(Str(f, "displayStatus")));
                    var eta = Date(Str(f, "estimatedDeliveryAt")); if (eta != null) sb.Append("، متوقع توصل يوم ").Append(eta);
                    var del = Date(Str(f, "deliveredAt")); if (del != null) sb.Append("، اتسلمت يوم ").Append(del);
                    JsonElement ti;
                    if (f.TryGetProperty("trackingInfo", out ti) && ti.ValueKind == JsonValueKind.Array)
                        foreach (var t in ti.EnumerateArray())
                        {
                            if (!string.IsNullOrEmpty(Str(t, "company"))) sb.Append("، شركة الشحن: ").Append(Str(t, "company"));
                            if (!string.IsNullOrEmpty(Str(t, "number"))) sb.Append("، رقم التتبع: ").Append(Str(t, "number"));
                            if (!string.IsNullOrEmpty(Str(t, "url"))) sb.Append("، لينك التتبع: ").Append(Str(t, "url"));
                        }
                }
            sb.Append("\nقول للزبون الحالة دي بكلام بسيط. ماتذكرش أي عنوان أو بيانات دفع أو أي حاجة مش مكتوبة هنا.");
            return sb.ToString();
        }

        static string Financial(string v)
        {
            switch (v)
            {
                case "PAID": return "مدفوع"; case "PENDING": return "لسه مستني الدفع (أو الدفع عند الاستلام)"; case "AUTHORIZED": return "الدفع متأكد ولسه هيتسحب";
                case "PARTIALLY_PAID": return "مدفوع جزء منه"; case "REFUNDED": return "الفلوس اترجعت"; case "PARTIALLY_REFUNDED": return "جزء من الفلوس اترجع";
                case "VOIDED": return "الدفع اتلغى"; case "EXPIRED": return "الدفع انتهت صلاحيته";
                default: return v ?? "مش معروف";
            }
        }
        static string Fulfillment(string v)
        {
            switch (v)
            {
                case "UNFULFILLED": return "لسه بيتجهز ومااتشحنش"; case "IN_PROGRESS": return "بيتجهز دلوقتي"; case "PARTIALLY_FULFILLED": return "جزء منه اتشحن";
                case "FULFILLED": return "اتشحن"; case "ON_HOLD": return "متوقف مؤقتًا"; case "SCHEDULED": return "متجدول يتشحن"; case "PENDING_FULFILLMENT": return "مستني التجهيز";
                case "RESTOCKED": return "اترجع للمخزن"; case "OPEN": return "مفتوح ولسه بيتجهز";
                default: return v ?? "مش معروف";
            }
        }
        static string Shipment(string v)
        {
            switch (v)
            {
                case "DELIVERED": return "اتسلمت"; case "IN_TRANSIT": return "في الطريق"; case "OUT_FOR_DELIVERY": return "خرجت مع المندوب للتوصيل";
                case "ATTEMPTED_DELIVERY": return "المندوب حاول يوصلها ومالقاش حد"; case "READY_FOR_PICKUP": return "جاهزة للاستلام"; case "CONFIRMED": return "اتأكدت";
                case "LABEL_PRINTED": case "LABEL_PURCHASED": return "اتجهزت للشحن"; case "FULFILLED": return "اتشحنت"; case "FAILURE": return "حصلت مشكلة في التوصيل";
                case "CANCELED": return "اتلغت";
                default: return v ?? "اتشحنت";
            }
        }
        static string Date(string iso)
        {
            DateTime d;
            return !string.IsNullOrEmpty(iso) && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out d) ? d.ToString("yyyy-MM-dd") : null;
        }

        // ---------- instant updates (webhooks) ----------
        public static readonly string[] Topics = { "PRODUCTS_CREATE", "PRODUCTS_UPDATE", "PRODUCTS_DELETE", "INVENTORY_LEVELS_UPDATE" };

        // Returns how many topics are subscribed. Skipped (0) when our site isn't reachable from the internet (local runs).
        public static async Task<int> RegisterWebhooks(Creds c, string siteBase, string scopes)
        {
            Uri u;
            if (!Uri.TryCreate(siteBase, UriKind.Absolute, out u) || u.Scheme != "https" || u.IsLoopback || u.Host.EndsWith(".localhost") || u.Host == "localhost") return 0;
            string uri = siteBase.TrimEnd('/') + "/api/shopify-webhook.ashx?store=" + Uri.EscapeDataString(c.Slug);
            const string m = "mutation($topic: WebhookSubscriptionTopic!, $sub: WebhookSubscriptionInput!) { webhookSubscriptionCreate(topic: $topic, webhookSubscription: $sub) { userErrors { field message } webhookSubscription { id } } }";
            int ok = 0;
            foreach (var topic in Topics)
            {
                if (topic.StartsWith("INVENTORY") && !Has(scopes, "read_inventory")) continue;
                try
                {
                    var g = await Query(c, m, new { topic, sub = new { uri } }, CancellationToken.None);
                    var r = g.Data.GetProperty("webhookSubscriptionCreate");
                    var errs = r.GetProperty("userErrors").EnumerateArray().Select(e => Str(e, "message") ?? "").ToList();
                    if (errs.Count == 0 || errs.Any(e => e.Contains("already been taken") || e.Contains("already exists"))) ok++;
                    else Log.Error(new Exception("Shopify webhook " + topic + ": " + string.Join("; ", errs)));
                }
                catch (Exception ex) { Log.Error(ex); }
            }
            return ok;
        }

        // Called by the webhook endpoint: refresh the store's data a little later, once per burst of changes.
        static readonly ConcurrentDictionary<int, bool> Pending = new ConcurrentDictionary<int, bool>();
        public static void QueueResync(int storeId)
        {
            if (!Pending.TryAdd(storeId, true)) return;
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch { }
                finally { bool x; Pending.TryRemove(storeId, out x); }
                await Shopify.SyncNow(storeId, ct);
            });
        }

        // ---------- helpers ----------
        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return null;
        }
        static decimal Num(string raw) { decimal d; return decimal.TryParse(raw ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0; }
        static string Money(string raw, string currency)
        {
            var n = Num(raw);
            if (n <= 0) return null;
            var s = n.ToString(n == Math.Floor(n) ? "0" : "0.00", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(currency) ? s : s + " " + currency;
        }
    }

    // api/shopify-webhook.ashx?store=<slug> — Shopify calls this when a product or its stock changes.
    public class ShopifyWebhookHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }
            byte[] body;
            using (var ms = new MemoryStream()) { req.InputStream.CopyTo(ms); body = ms.ToArray(); }   // raw body, before any parsing
            if (body.Length > 2 * 1024 * 1024) { res.StatusCode = 413; return; }
            string slug = (req.QueryString["store"] ?? "").ToLowerInvariant();
            string header = req.Headers["X-Shopify-Hmac-SHA256"] ?? "";
            try
            {
                using (var c = Db.Open())
                {
                    var s = Web.SlugRx.IsMatch(slug) ? Db.StoreBySlug(c, slug) : null;
                    var creds = ShopifyAdmin.For(s);
                    if (creds == null) { res.StatusCode = 401; return; }   // not connected (anymore): Shopify drops the subscription after repeated failures
                    byte[] expect; using (var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(creds.Secret))) expect = h.ComputeHash(body);
                    byte[] got; try { got = Convert.FromBase64String(header); } catch { got = null; }
                    if (!Secrets.Same(expect, got)) { res.StatusCode = 401; return; }
                    ShopifyAdmin.QueueResync(s.Id);
                    res.StatusCode = 200;
                }
            }
            catch (Exception ex) { Log.Error(ex); res.StatusCode = 500; }
        }
    }
}
