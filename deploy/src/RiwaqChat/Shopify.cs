// Shopify sync: pulls a store's PUBLIC data (products, prices, variants, availability, shop info, policies)
// from the endpoints every Shopify store serves (/products.json, /meta.json, /policies/...), turns it into text
// and keeps it as one data part (SourceType "shopify") that refreshes itself every few hours.
// No token or app is needed, so it only sees what any visitor of the store can see.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class Shopify
    {
        public const string SourceType = "shopify";
        public const string ItemTitle = "داتا Shopify: المنتجات والأسعار والسياسات (بتتحدث تلقائيًا)";
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);
        const int MaxPages = 40;               // 40 x 250 = up to 10,000 products
        const int MaxDescriptionChars = 1200;  // per product
        const int MaxPolicyChars = 6000;       // per policy
        const long MaxResponseBytes = 12 * 1024 * 1024;

        static readonly HttpClient Http;
        static Shopify()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            {
                Timeout = TimeSpan.FromSeconds(25),
                MaxResponseContentBufferSize = MaxResponseBytes,
            };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot-Sync/1.0");
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.8");
        }

        // ---------- domain checks ----------
        static readonly Regex HostRx = new Regex(@"^(?=.{4,200}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}$", RegexOptions.Compiled);

        // "https://My-Shop.com/collections/x" -> "my-shop.com"; null if it isn't a normal public host name.
        public static string NormalizeDomain(string input)
        {
            var s = (input ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return null;
            if (!s.Contains("://")) s = "https://" + s;
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http")) return null;
            if (!u.IsDefaultPort) return null;
            var host = u.IdnHost.TrimEnd('.');
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return null;      // no raw IP addresses
            return HostRx.IsMatch(host) ? host : null;
        }

        // The server must never be pointed at itself or at a private network (SSRF): every address the name resolves to must be public.
        static async Task<bool> IsPublicHost(string host)
        {
            IPAddress[] addrs;
            try { addrs = await Dns.GetHostAddressesAsync(host); } catch { return false; }
            if (addrs.Length == 0) return false;
            foreach (var a in addrs) if (!IsPublic(a)) return false;
            return true;
        }
        static bool IsPublic(IPAddress a)
        {
            if (IPAddress.IsLoopback(a)) return false;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv4MappedToIPv6) return IsPublic(a.MapToIPv4());
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
                var b6 = a.GetAddressBytes();
                if ((b6[0] & 0xfe) == 0xfc) return false;           // fc00::/7 unique local
                if (a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None)) return false;
                return true;
            }
            var b = a.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;  // carrier-grade NAT
            if (b[0] >= 224) return false;                                 // multicast / reserved
            return true;
        }

        // GET with up to 3 redirects, each target re-checked. Returns null on 404 / non-success.
        static async Task<string> Get(string url, CancellationToken ct)
        {
            for (int hop = 0; hop < 4; hop++)
            {
                var u = new Uri(url);
                if (u.Scheme != "https" || !u.IsDefaultPort || NormalizeDomain(u.Host) == null || !await IsPublicHost(u.IdnHost))
                    throw new ShopifyException("اللينك ده مش مسموح نسحب منه.");
                for (int attempt = 0; ; attempt++)
                {
                    using (var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseContentRead, ct))
                    {
                        int code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            url = new Uri(u, resp.Headers.Location).ToString();
                            break;
                        }
                        if (code == 429 && attempt < 2) { await Task.Delay(2000 * (attempt + 1), ct); continue; }
                        if (!resp.IsSuccessStatusCode) return null;
                        return await resp.Content.ReadAsStringAsync();
                    }
                }
            }
            throw new ShopifyException("الموقع بيحوّل لأماكن كتير، مقدرناش نوصل للداتا.");
        }

        // ---------- sync ----------
        public class SyncResult { public string Domain, ShopName, Currency, Text; public int Products, Policies; }

        public static async Task<SyncResult> Fetch(string domain, CancellationToken ct)
        {
            var res = new SyncResult { Domain = domain };
            string baseUrl = "https://" + domain;

            // shop info (name, currency, countries it ships to). Optional: older or custom setups may not have it.
            string metaJson = null;
            try { metaJson = await Get(baseUrl + "/meta.json", ct); } catch (ShopifyException) { throw; } catch { }
            var shipsTo = new List<string>();
            if (metaJson != null)
            {
                try
                {
                    using (var doc = JsonDocument.Parse(metaJson))
                    {
                        var root = doc.RootElement;
                        res.ShopName = Str(root, "name");
                        res.Currency = Str(root, "currency");
                        JsonElement st;
                        if (root.TryGetProperty("ships_to_countries", out st) && st.ValueKind == JsonValueKind.Array)
                            foreach (var cc in st.EnumerateArray()) if (cc.ValueKind == JsonValueKind.String) shipsTo.Add(cc.GetString());
                    }
                }
                catch (JsonException) { }
            }

            // products, 250 per page
            var sb = new StringBuilder();
            var productsText = new StringBuilder();
            for (int page = 1; page <= MaxPages; page++)
            {
                string json = await Get(baseUrl + "/products.json?limit=250&page=" + page, ct);
                if (json == null)
                {
                    if (page == 1) throw new ShopifyException("مالقيناش منتجات على الدومين ده. اتأكد إنه دومين متجر Shopify وإن المتجر مفتوح (مش مقفول بباسورد).");
                    break;
                }
                int count = 0;
                try
                {
                    using (var doc = JsonDocument.Parse(json))
                    {
                        JsonElement arr;
                        if (!doc.RootElement.TryGetProperty("products", out arr) || arr.ValueKind != JsonValueKind.Array)
                        {
                            if (page == 1) throw new ShopifyException("الدومين ده مش شكله متجر Shopify.");
                            break;
                        }
                        foreach (var p in arr.EnumerateArray()) { AppendProduct(productsText, p, baseUrl, res.Currency); count++; }
                    }
                }
                catch (JsonException)
                {
                    if (page == 1) throw new ShopifyException("الدومين ده مش شكله متجر Shopify.");
                    break;
                }
                res.Products += count;
                if (count < 250) break;
                await Task.Delay(400, ct);   // be gentle with the store
            }

            var pt = await FetchPolicies(domain, ct);
            var policyText = new StringBuilder(pt.Item1);
            res.Policies = pt.Item2;

            sb.Append("دي داتا المتجر على Shopify (").Append(domain).Append("). اتسحبت تلقائيًا من الموقع.");
            if (!string.IsNullOrEmpty(res.ShopName)) sb.Append("\nاسم المتجر على Shopify: ").Append(res.ShopName);
            if (!string.IsNullOrEmpty(res.Currency)) sb.Append("\nالعملة: ").Append(res.Currency);
            if (shipsTo.Count > 0 && shipsTo.Count <= 30) sb.Append("\nبيشحن للدول دي: ").Append(string.Join(", ", shipsTo));
            sb.Append("\nعدد المنتجات: ").Append(res.Products);
            if (policyText.Length > 0) sb.Append("\n\n# سياسات المتجر").Append(policyText);
            if (productsText.Length > 0) sb.Append("\n\n# المنتجات").Append(productsText);
            res.Text = sb.ToString();
            if (res.Text.Length > KnowledgeHandler.MaxItemChars) res.Text = res.Text.Substring(0, KnowledgeHandler.MaxItemChars);
            return res;
        }

        // Shipping / returns / terms / privacy policies from the store's public pages: (text, how many were found).
        public static async Task<Tuple<string, int>> FetchPolicies(string domain, CancellationToken ct)
        {
            string baseUrl = "https://" + domain;
            var policies = new[] {
                new[] { "shipping-policy", "سياسة الشحن" }, new[] { "refund-policy", "سياسة الاسترجاع والاستبدال" },
                new[] { "terms-of-service", "الشروط والأحكام" }, new[] { "privacy-policy", "سياسة الخصوصية" } };
            var policyText = new StringBuilder();
            int count = 0;
            foreach (var pol in policies)
            {
                // Shopify serves each policy as JSON ({"policy":{"body":"<html>"}}); the HTML page is the fallback.
                string body = "";
                try
                {
                    var pj = await Get(baseUrl + "/policies/" + pol[0] + ".json", ct);
                    if (pj != null)
                        using (var doc = JsonDocument.Parse(pj))
                        {
                            JsonElement po;
                            if (doc.RootElement.TryGetProperty("policy", out po)) body = HtmlToText(Str(po, "body"));
                        }
                }
                catch (ShopifyException) { throw; } catch { }
                if (body.Length < 40)
                {
                    string html = null;
                    try { html = await Get(baseUrl + "/policies/" + pol[0], ct); } catch (ShopifyException) { throw; } catch { }
                    body = PolicyBody(html);
                }
                if (body.Length < 40) continue;
                if (body.Length > MaxPolicyChars) body = body.Substring(0, MaxPolicyChars) + " …";
                policyText.Append("\n\n## ").Append(pol[1]).Append("\n").Append(body);
                count++;
            }
            return Tuple.Create(policyText.ToString(), count);
        }

        static void AppendProduct(StringBuilder sb, JsonElement p, string baseUrl, string currency)
        {
            string title = Str(p, "title");
            if (string.IsNullOrWhiteSpace(title)) return;
            sb.Append("\n\n## ").Append(title.Trim());
            var meta = new List<string>();
            string type = Str(p, "product_type"), vendor = Str(p, "vendor");
            if (!string.IsNullOrWhiteSpace(type)) meta.Add("النوع: " + type.Trim());
            if (!string.IsNullOrWhiteSpace(vendor)) meta.Add("الماركة: " + vendor.Trim());
            var tags = Tags(p);
            if (tags.Count > 0) meta.Add("تصنيفات: " + string.Join("، ", tags.Take(12)));
            if (meta.Count > 0) sb.Append("\n").Append(string.Join(" | ", meta));

            // variants: price, old price, availability. Same price for all -> price once + which options are available.
            JsonElement vars;
            var vs = new List<Tuple<string, string, bool>>();   // option title, "price (was ...)", available
            if (p.TryGetProperty("variants", out vars) && vars.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vars.EnumerateArray())
                {
                    if (vs.Count >= 100) break;
                    string vt = (Str(v, "title") ?? "").Trim();
                    if (vt == "Default Title") vt = "";
                    string price = Price(Str(v, "price"), currency) ?? "مش محدد";
                    string old = Price(Str(v, "compare_at_price"), currency);
                    if (old != null && Num(Str(v, "compare_at_price")) > Num(Str(v, "price"))) price += " (بدل " + old + ")";
                    vs.Add(Tuple.Create(vt, price, Bool(v, "available")));
                }
            }
            if (vs.Count > 0)
            {
                bool anyAvailable = vs.Any(x => x.Item3);
                var prices = vs.Select(x => x.Item2).Distinct().ToList();
                if (vs.Count == 1)
                    sb.Append("\nالسعر ").Append(vs[0].Item2).Append(vs[0].Item3 ? " · متاح" : " · خلصان");
                else if (prices.Count == 1)
                {
                    sb.Append("\nالسعر ").Append(prices[0]);
                    var yes = vs.Where(x => x.Item3).Select(x => x.Item1).ToList();
                    var no = vs.Where(x => !x.Item3).Select(x => x.Item1).ToList();
                    if (yes.Count > 0) sb.Append("\nمتاح: ").Append(string.Join("، ", yes));
                    if (no.Count > 0) sb.Append("\nخلصان: ").Append(string.Join("، ", no));
                }
                else
                    sb.Append("\nالاختيارات:\n- ").Append(string.Join("\n- ", vs.Select(x => (x.Item1.Length > 0 ? x.Item1 + ": " : "") + "السعر " + x.Item2 + (x.Item3 ? " · متاح" : " · خلصان"))));
                if (!anyAvailable) sb.Append("\n(المنتج ده خلصان دلوقتي)");
            }

            string desc = HtmlToText(Str(p, "body_html"));
            if (desc.Length > MaxDescriptionChars) desc = desc.Substring(0, MaxDescriptionChars) + " …";
            if (desc.Length > 0) sb.Append("\nالوصف: ").Append(desc);
            string handle = Str(p, "handle");
            if (!string.IsNullOrWhiteSpace(handle)) sb.Append("\nاللينك: ").Append(baseUrl).Append("/products/").Append(handle);
        }

        static string Price(string raw, string currency)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var n = Num(raw);
            if (n <= 0) return null;
            var s = n.ToString(n == Math.Floor(n) ? "0" : "0.00", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(currency) ? s : s + " " + currency;
        }
        static decimal Num(string raw) { decimal d; return decimal.TryParse(raw ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0; }
        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (!e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return null;
        }
        static bool Bool(JsonElement e, string name) { JsonElement v; return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.True; }
        static List<string> Tags(JsonElement p)
        {
            var list = new List<string>();
            JsonElement t;
            if (!p.TryGetProperty("tags", out t)) return list;
            if (t.ValueKind == JsonValueKind.Array) { foreach (var x in t.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString().Trim()); }
            else if (t.ValueKind == JsonValueKind.String) list.AddRange(t.GetString().Split(',').Select(x => x.Trim()));
            // keep tags a customer would understand; drop internal codes like "app::key => value" or batch ids
            return list.Where(x => x.Length > 1 && x.Length <= 30 && !x.Contains("::") && !x.Contains("=>") && !x.Contains("@") && !Regex.IsMatch(x, @"\d{5,}|^\d+/\d+/\d+")).ToList();
        }

        // The policy text sits in <div class="shopify-policy__body">, often nested many levels deep, so everything from there
        // to the end of <main> is taken (the page header and footer are outside it).
        static string PolicyBody(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            int i = html.IndexOf("shopify-policy__body", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int start = html.IndexOf('>', i);
            if (start < 0) return "";
            int end = html.IndexOf("</main", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = Math.Min(html.Length, start + 200000);
            return HtmlToText(html.Substring(start + 1, end - start - 1));
        }

        static readonly Regex Scripts = new Regex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Breaks = new Regex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Bullets = new Regex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Tags_ = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        public static string HtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var s = Scripts.Replace(html, " ");
            s = Breaks.Replace(s, "\n");
            s = Bullets.Replace(s, "\n- ");
            s = Tags_.Replace(s, " ");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t ]+", " ");
            s = Regex.Replace(s, @" *\n[ \n]*", "\n");
            return s.Trim();
        }

        // ---------- save ----------
        // Replaces the store's Shopify part with fresh data and records the result. Returns an error message, or null.
        public static string Save(SqlConnection c, int storeId, SyncResult r)
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
            if (existing == null)
                KnowledgeHandler.Insert(c, storeId, ItemTitle, SourceType, r.Domain, r.Text);
            else
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.KnowledgeItems SET Title=@ti, FileName=@f, Content=@c, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id",
                    "@ti", ItemTitle, "@f", r.Domain, "@c", r.Text, "@id", existing.Value)) cmd.ExecuteNonQuery();
                Db.BumpVersion(c, storeId);
            }
            SetStatus(c, storeId, r.Domain, true, r.Products + " منتج" + (r.Policies > 0 ? " و" + r.Policies + " سياسات" : ""));
            return null;
        }

        public static void SetStatus(SqlConnection c, int storeId, string domain, bool synced, string status)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ShopifyDomain=@d, ShopifyStatus=@st" + (synced ? ", ShopifySyncedAt=SYSUTCDATETIME()" : "") + " WHERE Id=@s",
                "@d", (object)domain ?? DBNull.Value, "@st", (object)status ?? DBNull.Value, "@s", storeId)) cmd.ExecuteNonQuery();
        }

        public static void Disconnect(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "DELETE FROM dbo.KnowledgeItems WHERE StoreId=@s AND SourceType=@t", "@s", storeId, "@t", SourceType)) cmd.ExecuteNonQuery();
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ShopifyDomain=NULL, ShopifySyncedAt=NULL, ShopifyStatus=NULL, ShopifyShop=NULL, ShopifyClientId=NULL, ShopifySecret=NULL, ShopifyScopes=NULL, ShopifyOrders=0, ShopifyWebhooks=0 WHERE Id=@s", "@s", storeId)) cmd.ExecuteNonQuery();
            Db.BumpVersion(c, storeId);
        }

        // ---------- refresh (on a timer through chat use, or right after a Shopify webhook) ----------
        static readonly ConcurrentDictionary<int, bool> Running = new ConcurrentDictionary<int, bool>();

        // Called on chat use: if the store's Shopify data is older than RefreshEvery, refresh it in the background.
        public static void RefreshIfOld(StoreInfo s)
        {
            if (s == null || string.IsNullOrEmpty(s.ShopifyDomain)) return;
            if (s.ShopifySyncedAt.HasValue && DateTime.UtcNow - s.ShopifySyncedAt.Value < RefreshEvery) return;
            int id = s.Id;
            HostingEnvironment.QueueBackgroundWorkItem(ct => SyncNow(id, ct));
        }

        // Pulls fresh data for one store: through the Admin API when the full connection is set up, otherwise the public pages.
        public static async Task SyncNow(int storeId, CancellationToken ct)
        {
            if (!Running.TryAdd(storeId, true)) return;
            string domain = null;
            try
            {
                StoreInfo s;
                using (var c = Db.Open()) s = Db.StoreById(c, storeId);
                if (s == null || string.IsNullOrEmpty(s.ShopifyDomain)) return;
                domain = s.ShopifyDomain;
                var creds = ShopifyAdmin.For(s);
                var r = creds != null ? await ShopifyAdmin.FetchCatalog(creds, ct) : await Fetch(domain, ct);
                using (var c = Db.Open())
                {
                    var err = Save(c, storeId, r);
                    if (err != null) SetStatus(c, storeId, r.Domain, true, "⚠️ " + err);   // mark as tried, so it doesn't retry on every message
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                try { using (var c = Db.Open()) SetStatus(c, storeId, domain, true, "⚠️ آخر تحديث تلقائي فشل: " + (ex is ShopifyException ? ex.Message : "Shopify مكانتش متاحة")); } catch { }
            }
            finally { bool x; Running.TryRemove(storeId, out x); }
        }
    }

    public class ShopifyException : Exception { public ShopifyException(string m) : base(m) { } }
}
