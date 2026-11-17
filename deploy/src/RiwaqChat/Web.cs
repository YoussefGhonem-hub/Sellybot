// Small helpers shared by all handlers: JSON in/out, settings, rate limits, CSRF check, password hashing, login cookie.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

namespace RiwaqChat
{
    // A bad request from the browser (unreadable or too large body): answered with 400, not logged as a server error.
    public class BadRequestException : Exception { public BadRequestException(string m) : base(m) { } }

    public static class Web
    {
        public static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 50000000 };

        public static string Setting(string key, string fallback = null)
        {
            var v = ConfigurationManager.AppSettings[key];
            return string.IsNullOrEmpty(v) || v.StartsWith("PUT-") ? fallback : v;
        }

        public static void Json(HttpResponse res, int status, object obj)
        {
            res.StatusCode = status;
            res.TrySkipIisCustomErrors = true;
            res.ContentType = "application/json; charset=utf-8";
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            res.Write(Js.Serialize(obj));
        }

        public static void Error(HttpResponse res, int status, string code, string message = null)
        {
            Json(res, status, new Dictionary<string, object> { { "error", code }, { "message", message ?? "" } });
        }

        public static Dictionary<string, object> Body(HttpRequest req, int maxChars)
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            if (body.Length > maxChars) throw new BadRequestException("المحتوى كبير جدًا.");
            try { return Js.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>(); }
            catch (Exception) { throw new BadRequestException("الطلب مش مفهوم."); }
        }

        public static string Str(Dictionary<string, object> d, string key, int maxLen)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            var s = Convert.ToString(v).Trim();
            return s.Length > maxLen ? s.Substring(0, maxLen) : s;
        }

        public static int? Int(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            int i;
            return int.TryParse(Convert.ToString(v), out i) ? i : (int?)null;
        }

        // State-changing calls must come from our own pages (a plain cross-site form can't add this header).
        public static bool CsrfOk(HttpRequest req) { return req.Headers["X-Requested-With"] == "fetch"; }

        // The visitor's address (rate limits, logs). Behind Cloudflare every request reaches us from a Cloudflare server and the
        // visitor's own address is in the CF-Connecting-IP header. The header is believed only when the request really comes
        // from Cloudflare (its published ranges, https://www.cloudflare.com/ips): anyone else could write any address in it
        // and get a fresh rate-limit bucket on every request. Without Cloudflare in front it's simply the connecting address.
        public static string Ip(HttpRequest req) { return ClientIp(req.UserHostAddress, req.Headers["CF-Connecting-IP"]); }

        public static string ClientIp(string peer, string cfConnectingIp)
        {
            if (string.IsNullOrEmpty(peer)) return "unknown";
            System.Net.IPAddress real;
            if (!string.IsNullOrEmpty(cfConnectingIp) && FromCloudflare(peer) && System.Net.IPAddress.TryParse(cfConnectingIp.Trim(), out real))
                return real.ToString();
            return peer;
        }

        // Checked 2026-11-17. Cloudflare changes these rarely and announces it in advance; a missing range only means the
        // visitors it carries share that Cloudflare server's address for rate limits until it's added here.
        static readonly string[] CloudflareRanges = {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
            "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
            "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32" };
        static readonly KeyValuePair<byte[], int>[] CloudflareNets = CloudflareRanges.Select(r =>
        {
            var p = r.Split('/');
            return new KeyValuePair<byte[], int>(System.Net.IPAddress.Parse(p[0]).GetAddressBytes(), int.Parse(p[1]));
        }).ToArray();

        public static bool FromCloudflare(string address)
        {
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(address, out ip)) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var b = ip.GetAddressBytes();
            foreach (var net in CloudflareNets)
            {
                var n = net.Key; int bits = net.Value;
                if (n.Length != b.Length) continue;
                int whole = bits / 8, rest = bits % 8, i = 0;
                for (; i < whole && n[i] == b[i]; i++) { }
                if (i < whole) continue;
                if (rest == 0 || ((n[whole] ^ b[whole]) & (0xFF << (8 - rest))) == 0) return true;
            }
            return false;
        }

        // ---------- rate limits ----------
        // One bucket per key (e.g. "login:<ip>") with the times of its recent hits. Every few minutes the buckets whose hits
        // are all older than their window are dropped, so each visitor who came once doesn't stay in memory until the site
        // restarts (a flood from many addresses used to grow it without end).
        class Bucket { public readonly Queue<DateTime> Hits = new Queue<DateTime>(); public TimeSpan Window; public bool Gone; }
        static readonly ConcurrentDictionary<string, Bucket> Buckets = new ConcurrentDictionary<string, Bucket>();
        static long nextSweep;

        public static bool Allow(string key, int perWindow, TimeSpan window)
        {
            var now = DateTime.UtcNow;
            Sweep(now);
            while (true)
            {
                var b = Buckets.GetOrAdd(key, k => new Bucket());
                lock (b)
                {
                    if (b.Gone) continue;   // the sweep dropped it just now: take the new one
                    b.Window = window;
                    while (b.Hits.Count > 0 && now - b.Hits.Peek() >= window) b.Hits.Dequeue();
                    if (b.Hits.Count >= perWindow) return false;
                    b.Hits.Enqueue(now);
                    return true;
                }
            }
        }

        static void Sweep(DateTime now)
        {
            long due = System.Threading.Interlocked.Read(ref nextSweep);
            if (now.Ticks < due) return;
            if (System.Threading.Interlocked.CompareExchange(ref nextSweep, now.AddMinutes(5).Ticks, due) != due) return;   // another request is on it
            foreach (var kv in Buckets)
            {
                var b = kv.Value;
                lock (b)
                {
                    while (b.Hits.Count > 0 && now - b.Hits.Peek() >= b.Window) b.Hits.Dequeue();
                    if (b.Hits.Count > 0) continue;
                    b.Gone = true;
                    Bucket x; Buckets.TryRemove(kv.Key, out x);
                }
            }
        }

        // ---------- validation ----------
        public static readonly Regex SlugRx = new Regex("^[a-z0-9](?:[a-z0-9-]{1,28})[a-z0-9]$");
        public static bool ValidEmail(string e) { return e != null && e.Length <= 200 && Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"); }
        public static bool Reserved(string slug)
        {
            var list = (Setting("ReservedSlugs", "admin,api,dashboard,www,app,pricing,support")).Split(',').Select(s => s.Trim().ToLowerInvariant());
            return list.Contains(slug);
        }

        // ---------- passwords (PBKDF2-SHA256) ----------
        public static string HashPassword(string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            const int iter = 100000;
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iter, HashAlgorithmName.SHA256))
                return "pbkdf2$" + iter + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(kdf.GetBytes(32));
        }

        public static bool CheckPassword(string password, string stored)
        {
            try
            {
                var p = stored.Split('$');
                if (p.Length != 4 || p[0] != "pbkdf2") return false;
                var salt = Convert.FromBase64String(p[2]);
                var expected = Convert.FromBase64String(p[3]);
                using (var kdf = new Rfc2898DeriveBytes(password, salt, int.Parse(p[1]), HashAlgorithmName.SHA256))
                {
                    var actual = kdf.GetBytes(expected.Length);
                    int diff = 0;
                    for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
                    return diff == 0;
                }
            }
            catch { return false; }
        }

        // ---------- login cookie: storeId|expiry|HMAC ----------
        const string Cookie = "rc_auth";
        static byte[] Secret()
        {
            var s = Setting("AuthSecret");
            if (s == null || s.Length < 32) throw new InvalidOperationException("AuthSecret is not configured");
            return Encoding.UTF8.GetBytes(s);
        }

        static string Sign(string payload)
        {
            using (var h = new HMACSHA256(Secret())) return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        }

        public static void SignIn(HttpContext ctx, int storeId)
        {
            long exp = DateTimeOffset.UtcNow.AddDays(14).ToUnixTimeSeconds();
            string payload = storeId + "|" + exp;
            var c = new HttpCookie(Cookie, payload + "|" + Sign(payload))
            {
                HttpOnly = true,
                Secure = ctx.Request.IsSecureConnection,
                Path = "/",
                Expires = DateTime.UtcNow.AddDays(14),
            };
            c.SameSite = SameSiteMode.Lax;
            ctx.Response.Cookies.Add(c);
        }

        public static void SignOut(HttpContext ctx)
        {
            var c = new HttpCookie(Cookie, "") { HttpOnly = true, Path = "/", Expires = DateTime.UtcNow.AddDays(-1) };
            ctx.Response.Cookies.Add(c);
        }

        public static int? CurrentStoreId(HttpContext ctx)
        {
            var c = ctx.Request.Cookies[Cookie];
            if (c == null || string.IsNullOrEmpty(c.Value)) return null;
            var p = c.Value.Split('|');
            if (p.Length != 3) return null;
            var payload = p[0] + "|" + p[1];
            var expected = Sign(payload);
            if (expected.Length != p[2].Length) return null;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ p[2][i];
            if (diff != 0) return null;
            long exp;
            if (!long.TryParse(p[1], out exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            int id;
            return int.TryParse(p[0], out id) ? id : (int?)null;
        }
    }
}
