// Apple's push service (APNs) for the admin app on an iPhone while the app is closed (SignalR only reaches it while it's open, AdminLive.cs).
// The admin uploads the .p8 key from «الإعدادات» (Key ID, Team ID, Bundle ID with it); it is kept in dbo.AppFiles and never sent back.
// Each push is signed with a short ES256 token (JWT) and goes over HTTP/2 (WinHttpHandler) to api.push.apple.com (sandbox for test builds).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class Apns
    {
        const string KeyFile = "ApnsKey";
        public const string DefaultBundle = "online.sellybot.app";

        public class Creds { public byte[] Der; public string KeyId, TeamId, Bundle; }

        static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() => new HttpClient(new WinHttpHandler()) { Timeout = TimeSpan.FromSeconds(20) });
        static readonly object JwtLock = new object();
        static string jwt, jwtFor; static DateTime jwtAt;

        // the key as DER bytes from the .p8 text (PEM "PRIVATE KEY"), or null
        static byte[] Der(string pem)
        {
            if (string.IsNullOrWhiteSpace(pem)) return null;
            var m = Regex.Match(pem, "-----BEGIN PRIVATE KEY-----([A-Za-z0-9+/=\\s]+)-----END PRIVATE KEY-----");
            if (!m.Success) return null;
            try { return Convert.FromBase64String(Regex.Replace(m.Groups[1].Value, "\\s", "")); } catch { return null; }
        }

        public static Creds Load(SqlConnection c)
        {
            byte[] der = null;
            using (var cmd = Db.Cmd(c, "SELECT Data FROM dbo.AppFiles WHERE [Key]=@k", "@k", KeyFile)) der = cmd.ExecuteScalar() as byte[];
            var k = new Creds { Der = der, KeyId = Billing.Get(c, "ApnsKeyId") ?? "", TeamId = Billing.Get(c, "ApnsTeamId") ?? "", Bundle = Billing.Get(c, "ApnsBundle") ?? DefaultBundle };
            return der != null && k.KeyId.Length == 10 && k.TeamId.Length == 10 ? k : null;
        }

        public static Dictionary<string, object> Status(SqlConnection c)
        {
            bool has;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.AppFiles WHERE [Key]=@k", "@k", KeyFile)) has = (int)cmd.ExecuteScalar() > 0;
            int devices;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.AdminDevices WHERE PushToken IS NOT NULL AND RevokedAt IS NULL AND Platform='ios'")) devices = (int)cmd.ExecuteScalar();
            return new Dictionary<string, object> { { "configured", Load(c) != null }, { "hasKey", has }, { "keyId", Billing.Get(c, "ApnsKeyId") ?? "" },
                { "teamId", Billing.Get(c, "ApnsTeamId") ?? "" }, { "bundle", Billing.Get(c, "ApnsBundle") ?? DefaultBundle }, { "devices", devices },
                { "last", Billing.Get(c, "ApnsLast") ?? "" } };
        }

        // POST apns {key: the .p8 text, keyId, teamId, bundle} or {clear: true}
        public static void Save(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            object cl;
            if (b.TryGetValue("clear", out cl) && cl is bool && (bool)cl)
            {
                using (var cmd = Db.Cmd(c, "DELETE FROM dbo.AppFiles WHERE [Key]=@k", "@k", KeyFile)) cmd.ExecuteNonQuery();
                Billing.Set(c, "ApnsKeyId", ""); Billing.Set(c, "ApnsLast", "");
                lock (JwtLock) jwt = null;
                Web.Json(res, 200, Status(c)); return;
            }
            var keyId = (Web.Str(b, "keyId", 20) ?? "").Trim().ToUpperInvariant();
            var teamId = (Web.Str(b, "teamId", 20) ?? "").Trim().ToUpperInvariant();
            var bundle = (Web.Str(b, "bundle", 100) ?? "").Trim();
            if (bundle.Length == 0) bundle = DefaultBundle;
            if (!Regex.IsMatch(keyId, "^[A-Z0-9]{10}$")) { Web.Error(res, 400, "bad_key_id", "الـ Key ID عشر حروف وأرقام إنجليزي، وبيبان في اسم الملف AuthKey_XXXXXXXXXX.p8."); return; }
            if (!Regex.IsMatch(teamId, "^[A-Z0-9]{10}$")) { Web.Error(res, 400, "bad_team_id", "الـ Team ID عشر حروف وأرقام إنجليزي، من صفحة Membership في حساب Apple Developer."); return; }
            if (!Regex.IsMatch(bundle, "^[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$")) { Web.Error(res, 400, "bad_bundle", "الـ Bundle ID شكله كده: online.sellybot.app"); return; }
            var pem = Web.Str(b, "key", 4000);
            byte[] der = null;
            if (!string.IsNullOrWhiteSpace(pem))
            {
                der = Der(pem);
                if (der == null || !Works(der)) { Web.Error(res, 400, "bad_key", "ملف المفتاح مش مظبوط. اختار ملف الـ .p8 اللي نزّلته من Apple زي ما هو."); return; }
            }
            else
                using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.AppFiles WHERE [Key]=@k", "@k", KeyFile))
                    if ((int)cmd.ExecuteScalar() == 0) { Web.Error(res, 400, "need_key", "اختار ملف المفتاح (.p8)."); return; }
            if (der != null)
                using (var cmd = Db.Cmd(c, @"MERGE dbo.AppFiles AS t USING (SELECT @k AS [Key]) AS s ON t.[Key] = s.[Key]
                                             WHEN MATCHED THEN UPDATE SET ContentType = @t, Data = @d, UpdatedAt = SYSUTCDATETIME()
                                             WHEN NOT MATCHED THEN INSERT ([Key], ContentType, Data) VALUES (@k, @t, @d);", "@k", KeyFile, "@t", "application/pkcs8", "@d", der))
                    cmd.ExecuteNonQuery();
            Billing.Set(c, "ApnsKeyId", keyId); Billing.Set(c, "ApnsTeamId", teamId); Billing.Set(c, "ApnsBundle", bundle);
            lock (JwtLock) jwt = null;
            Web.Json(res, 200, Status(c));
        }

        static bool Works(byte[] der)
        {
            try { using (var key = CngKey.Import(der, CngKeyBlobFormat.Pkcs8PrivateBlob)) using (var ec = new ECDsaCng(key)) return ec.KeySize == 256; }
            catch { return false; }
        }

        static string B64(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        // Apple wants a new token at most once every 20 minutes and refuses one older than an hour
        static string Token(Creds k)
        {
            lock (JwtLock)
            {
                var forKey = k.KeyId + "|" + k.TeamId + "|" + Convert.ToBase64String(SHA256.Create().ComputeHash(k.Der));
                if (jwt != null && jwtFor == forKey && DateTime.UtcNow - jwtAt < TimeSpan.FromMinutes(40)) return jwt;
                var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"kid\":\"" + k.KeyId + "\"}"));
                var body = B64(Encoding.UTF8.GetBytes("{\"iss\":\"" + k.TeamId + "\",\"iat\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}"));
                using (var key = CngKey.Import(k.Der, CngKeyBlobFormat.Pkcs8PrivateBlob))
                using (var ec = new ECDsaCng(key))
                    jwt = head + "." + body + "." + B64(ec.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256));
                jwtFor = forKey; jwtAt = DateTime.UtcNow;
                return jwt;
            }
        }

        public class Device { public int Id; public string Name, Token, Env, Prefs; }

        // one push; null = Apple took it, otherwise Apple's reason (BadDeviceToken, Unregistered, InvalidProviderToken, ...)
        public static async Task<string> Send(Creds k, Device d, string title, string body, string route, int badge, string thread)
        {
            var host = d.Env == "sandbox" ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
            var payload = Web.Js.Serialize(new Dictionary<string, object> {
                { "aps", new Dictionary<string, object> { { "alert", new Dictionary<string, object> { { "title", title }, { "body", body } } }, { "sound", "default" }, { "badge", badge }, { "thread-id", thread ?? "sellybot" } } },
                { "route", route ?? "" } });
            var req = new HttpRequestMessage(HttpMethod.Post, host + "/3/device/" + d.Token) { Version = new Version(2, 0), Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("authorization", "bearer " + Token(k));
            req.Headers.TryAddWithoutValidation("apns-topic", k.Bundle);
            req.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            req.Headers.TryAddWithoutValidation("apns-priority", "10");
            using (var r = await Http.Value.SendAsync(req).ConfigureAwait(false))
            {
                if ((int)r.StatusCode == 200) return null;
                var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                var m = Regex.Match(text ?? "", "\"reason\"\\s*:\\s*\"([^\"]+)\"");
                var why = m.Success ? m.Groups[1].Value : "HTTP " + (int)r.StatusCode;
                if (why == "BadDeviceToken" || why == "Unregistered" || why == "DeviceTokenNotForTopic")   // that phone no longer takes pushes for this app
                    using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "UPDATE dbo.AdminDevices SET PushToken=NULL WHERE Id=@id", "@id", d.Id)) cmd.ExecuteNonQuery();
                return why;
            }
        }

        public static List<Device> Devices(SqlConnection c, int? adminId)
        {
            var list = new List<Device>();
            using (var cmd = Db.Cmd(c, @"SELECT Id, Name, PushToken, PushEnv, Prefs FROM dbo.AdminDevices
                                         WHERE PushToken IS NOT NULL AND RevokedAt IS NULL AND Platform='ios' AND LastAt > DATEADD(DAY, -@d, SYSUTCDATETIME())" + (adminId.HasValue ? " AND AdminId=@a" : ""),
                "@d", AdminApp.DeviceDays, "@a", adminId ?? 0))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new Device { Id = (int)r[0], Name = (string)r[1], Token = (string)r[2], Env = r[3] as string ?? "production", Prefs = r[4] as string });
            return list;
        }

        // «جرّب الإشعار»: a push to every iPhone of this admin, and what Apple said for each
        public static void Test(SqlConnection c, int adminId, HttpResponse res)
        {
            var k = Load(c);
            if (k == null) { Web.Error(res, 400, "not_configured", "ارفع ملف المفتاح واكتب الـ Key ID والـ Team ID الأول."); return; }
            var devs = Devices(c, adminId);
            if (devs.Count == 0) { Web.Error(res, 400, "no_devices", "مفيش iPhone داخل على التطبيق وموافق على الإشعارات."); return; }
            var results = new List<object>();
            foreach (var d in devs)
            {
                string why;
                try { why = Task.Run(() => Send(k, d, "✅ الإشعارات شغالة", "ده إشعار تجربة من Sellybot.", "/", 0, "test")).GetAwaiter().GetResult(); }
                catch (Exception ex) { why = ex.GetBaseException().Message; }
                results.Add(new Dictionary<string, object> { { "name", d.Name }, { "ok", why == null }, { "reason", why ?? "" } });
            }
            Remember(c, results.All(x => (bool)((Dictionary<string, object>)x)["ok"]) ? "ok" : "error");
            Web.Json(res, 200, new Dictionary<string, object> { { "results", results } });
        }

        public static void Remember(SqlConnection c, string what)
        {
            try { Billing.Set(c, "ApnsLast", what + "|" + DateTime.UtcNow.ToString("o")); } catch (Exception ex) { Log.Error(ex); }
        }
    }
}
