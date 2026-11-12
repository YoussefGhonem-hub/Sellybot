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

        public static string Ip(HttpRequest req) { return req.UserHostAddress ?? "unknown"; }

        // ---------- rate limits ----------
        static readonly ConcurrentDictionary<string, Queue<DateTime>> Hits = new ConcurrentDictionary<string, Queue<DateTime>>();
        public static bool Allow(string key, int perWindow, TimeSpan window)
        {
            var q = Hits.GetOrAdd(key, k => new Queue<DateTime>());
            lock (q)
            {
                var now = DateTime.UtcNow;
                while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
                if (q.Count >= perWindow) return false;
                q.Enqueue(now);
                return true;
            }
        }

        // ---------- validation ----------
        public static readonly Regex SlugRx = new Regex("^[a-z0-9](?:[a-z0-9-]{1,28})[a-z0-9]$");
        public static bool ValidEmail(string e) { return e != null && e.Length <= 200 && Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"); }
        public static bool Reserved(string slug)
        {
            var list = (Setting("ReservedSlugs", "riwaq,admin,api,dashboard,www,app")).Split(',').Select(s => s.Trim().ToLowerInvariant());
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
