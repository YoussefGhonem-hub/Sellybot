// The admin's phone app (mobile-app/app), on the same api/superadmin.ashx as admin.html:
//   - a long sign-in per device: the app keeps a device token and trades it for a fresh 12-hour admin cookie (device-login), up to 30 days
//     after it was last used; «اخرج من الأجهزة التانية» ends the others and every older cookie (Admins.SignedOutAt)
//   - what's new for the admin (events): new plan requests, owners' tickets and replies, rented stores waiting, customers' reports,
//     «فكّر معانا» top-ups, Messenger/Instagram link requests, the WhatsApp server. Worked out from the tables themselves, so every place
//     that adds one counts without being touched. A cursor ("since") says what the app has already seen.
//   - the badge counts the web page gets from five requests, in one
// The live push (SignalR while the app is open, Apple's push service for a closed iPhone app) is AdminLive.cs and Apns.cs.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public static class AdminApp
    {
        const string Schema = @"
IF OBJECT_ID('dbo.AdminDevices') IS NULL
BEGIN
  CREATE TABLE dbo.AdminDevices(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    AdminId INT NOT NULL REFERENCES dbo.Admins(Id) ON DELETE CASCADE,
    Name NVARCHAR(80) NOT NULL, Platform NVARCHAR(12) NOT NULL DEFAULT 'android', AppVersion NVARCHAR(20) NULL,
    TokenHash CHAR(64) NOT NULL,
    PushToken NVARCHAR(200) NULL, PushEnv NVARCHAR(12) NULL,   -- Apple's device token for an iPhone, and sandbox / production
    Prefs NVARCHAR(200) NULL,                                   -- the event types it wants as notifications (null = all)
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), LastAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), RevokedAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_AdminDevices_Token ON dbo.AdminDevices(TokenHash);
END
IF COL_LENGTH('dbo.Admins', 'SignedOutAt') IS NULL ALTER TABLE dbo.Admins ADD SignedOutAt DATETIME2 NULL;";

        public static void Ensure(SqlConnection c) { using (var cmd = new SqlCommand(Schema, c)) cmd.ExecuteNonQuery(); }

        public const int DeviceDays = 30;
        public static readonly string[] Types = { "request", "ticket", "shop", "report", "council", "meta", "wa" };

        // the lowest app version the server still works with ("" = any); the app asks the admin to update below it
        public static string MinVersion(SqlConnection c) { return Billing.Get(c, "AdminAppMin") ?? ""; }

        static string Hash(string token)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(token ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        static string Platform(string p) { p = (p ?? "").ToLowerInvariant(); return p == "ios" || p == "android" || p == "windows" ? p : "android"; }

        // ---------- devices ----------
        public static string NewDevice(SqlConnection c, int adminId, string name, string platform, string version)
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.AdminDevices(AdminId, Name, Platform, AppVersion, TokenHash) VALUES(@a, @n, @p, @v, @h)",
                "@a", adminId, "@n", name.Trim(), "@p", Platform(platform), "@v", (object)version ?? DBNull.Value, "@h", Hash(token))) cmd.ExecuteNonQuery();
            return token;
        }

        // the device's row while its token still works: not ended, used in the last 30 days
        static int DeviceRow(SqlConnection c, string token, out int adminId)
        {
            adminId = 0;
            if (string.IsNullOrEmpty(token) || token.Length < 20) return 0;
            using (var cmd = Db.Cmd(c, "SELECT Id, AdminId FROM dbo.AdminDevices WHERE TokenHash=@h AND RevokedAt IS NULL AND LastAt > DATEADD(DAY, -@d, SYSUTCDATETIME())", "@h", Hash(token), "@d", DeviceDays))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { adminId = (int)r[1]; return (int)r[0]; }
            return 0;
        }

        public static void DeviceLogin(HttpContext ctx, SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("sa-device:" + Web.Ip(ctx.Request), 30, TimeSpan.FromMinutes(15))) { Web.Error(res, 429, "rate_limited", "محاولات كتير، استنى ربع ساعة وجرّب تاني."); return; }
            int adminId; var id = DeviceRow(c, Web.Str(b, "token", 100), out adminId);
            string user = null; bool isDefault = false;
            if (id > 0)
                using (var cmd = Db.Cmd(c, "SELECT Username, DefaultPassword FROM dbo.Admins WHERE Id=@id", "@id", adminId))
                using (var r = cmd.ExecuteReader()) if (r.Read()) { user = (string)r[0]; isDefault = (bool)r[1]; }
            if (user == null) { Web.Error(res, 401, "device_ended", "الدخول على الجهاز ده خلص، ادخل تاني باسم المستخدم والباسورد."); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.AdminDevices SET LastAt=SYSUTCDATETIME(), AppVersion=ISNULL(@v, AppVersion) WHERE Id=@id", "@v", (object)Web.Str(b, "version", 20) ?? DBNull.Value, "@id", id)) cmd.ExecuteNonQuery();
            AdminAuth.SignIn(ctx, adminId);
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "username", user }, { "defaultPassword", isDefault }, { "appMin", MinVersion(c) } });
        }

        public static void DeviceLogout(SqlConnection c, string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.AdminDevices SET RevokedAt=SYSUTCDATETIME(), PushToken=NULL WHERE TokenHash=@h AND RevokedAt IS NULL", "@h", Hash(token))) cmd.ExecuteNonQuery();
        }

        public static Dictionary<string, object> Devices(SqlConnection c, int adminId, string mine)
        {
            var list = new List<object>(); var me = string.IsNullOrEmpty(mine) ? "" : Hash(mine);
            using (var cmd = Db.Cmd(c, @"SELECT Id, Name, Platform, AppVersion, TokenHash, PushToken, CreatedAt, LastAt FROM dbo.AdminDevices
                                         WHERE AdminId=@a AND RevokedAt IS NULL AND LastAt > DATEADD(DAY, -@d, SYSUTCDATETIME()) ORDER BY LastAt DESC", "@a", adminId, "@d", DeviceDays))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> { { "id", r["Id"] }, { "name", r["Name"] }, { "platform", r["Platform"] }, { "version", r["AppVersion"] as string ?? "" },
                        { "push", r["PushToken"] is string }, { "me", (string)r["TokenHash"] == me }, { "created", Billing.Iso(r["CreatedAt"]) }, { "last", Billing.Iso(r["LastAt"]) } });
            return new Dictionary<string, object> { { "devices", list }, { "days", DeviceDays } };
        }

        public static void Revoke(SqlConnection c, int adminId, Dictionary<string, object> b, HttpResponse res)
        {
            var id = Web.Int(b, "id");
            if (id == null) { Web.Error(res, 400, "bad_request"); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.AdminDevices SET RevokedAt=SYSUTCDATETIME(), PushToken=NULL WHERE Id=@id AND AdminId=@a AND RevokedAt IS NULL", "@id", id.Value, "@a", adminId)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        // «اخرج من الأجهزة التانية»: every other device, and every admin cookie given before now (the web page too); this device stays in
        public static void OthersOut(HttpContext ctx, SqlConnection c, int adminId, Dictionary<string, object> b, HttpResponse res)
        {
            var mine = Hash(Web.Str(b, "device", 100) ?? "");
            // to the second, the way the cookie keeps its time, so the cookie given right below isn't older than it
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.AdminDevices SET RevokedAt=SYSUTCDATETIME(), PushToken=NULL WHERE AdminId=@a AND RevokedAt IS NULL AND TokenHash<>@h;
                                         UPDATE dbo.Admins SET SignedOutAt=DATEADD(SECOND, DATEDIFF(SECOND, '2020-01-01', SYSUTCDATETIME()), '2020-01-01') WHERE Id=@a;", "@a", adminId, "@h", mine)) cmd.ExecuteNonQuery();
            AdminAuth.SignIn(ctx, adminId);
            Web.Json(res, 200, new { ok = true });
        }

        // the device's notification choices, and on an iPhone Apple's token for it (Apns.cs sends there while the app is closed)
        public static void SetPush(SqlConnection c, int adminId, Dictionary<string, object> b, HttpResponse res)
        {
            int owner; var id = DeviceRow(c, Web.Str(b, "device", 100), out owner);
            if (id == 0 || owner != adminId) { Web.Error(res, 400, "no_device", "الجهاز ده مش متسجّل. اخرج وادخل تاني."); return; }
            var token = (Web.Str(b, "token", 200) ?? "").Trim();
            if (token.Length > 0 && !Regex.IsMatch(token, "^[0-9a-fA-F]{32,200}$")) { Web.Error(res, 400, "bad_token"); return; }
            var env = Web.Str(b, "env", 12) == "sandbox" ? "sandbox" : "production";
            object pv; string prefs = null;
            if (b.TryGetValue("prefs", out pv) && pv is object[])
                prefs = string.Join(",", ((object[])pv).Select(x => Convert.ToString(x)).Where(x => Types.Contains(x)).Distinct());
            else if (b.TryGetValue("prefs", out pv) && pv is System.Collections.ArrayList)
                prefs = string.Join(",", ((System.Collections.ArrayList)pv).Cast<object>().Select(x => Convert.ToString(x)).Where(x => Types.Contains(x)).Distinct());
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.AdminDevices SET PushToken=CASE WHEN @t='' THEN NULL ELSE LOWER(@t) END, PushEnv=@e, Prefs=ISNULL(@p, Prefs), LastAt=SYSUTCDATETIME() WHERE Id=@id",
                "@t", token, "@e", env, "@p", (object)prefs ?? DBNull.Value, "@id", id)) cmd.ExecuteNonQuery();
            Web.Json(res, 200, new { ok = true });
        }

        // an error the app hit (it has no other crash reports): into the site's error log, a few an hour per address
        public static void AppError(HttpRequest req, Dictionary<string, object> b, HttpResponse res)
        {
            if (Web.Allow("sa-apperr:" + Web.Ip(req), 20, TimeSpan.FromHours(1)))
                Log.Error(new AppReportedException("admin app " + (Web.Str(b, "version", 20) ?? "?") + " · " + (Web.Str(b, "where", 60) ?? "") + " · " + (Web.Str(b, "message", 500) ?? "")));
            Web.Json(res, 200, new { ok = true });
        }
        public class AppReportedException : Exception { public AppReportedException(string m) : base(m.Replace("\r", " ").Replace("\n", " ")) { } }

        // ---------- what's new for the admin ----------
        // How far the app has seen: the last plan request / owner ticket message / report / top-up, the shops' last submit and the
        // Messenger requests' last update (ticks), how many live stores wait with a change, and the WhatsApp server (down 0/1, numbers lost).
        public class Cursor
        {
            public int R, M, P, C, G, W, L; public long S, A;
            public override string ToString() { return string.Join(".", "1", R, M, P, C, G, S, A, W, L); }
            public static Cursor Parse(string s)
            {
                var p = (s ?? "").Split('.');
                if (p.Length != 10 || p[0] != "1") return null;
                try
                {
                    return new Cursor { R = int.Parse(p[1]), M = int.Parse(p[2]), P = int.Parse(p[3]), C = int.Parse(p[4]), G = int.Parse(p[5]),
                        S = long.Parse(p[6]), A = long.Parse(p[7]), W = int.Parse(p[8]), L = int.Parse(p[9]) };
                }
                catch { return null; }
            }
        }

        public class Result
        {
            public Cursor Cursor; public List<Dictionary<string, object>> Events = new List<Dictionary<string, object>>(); public Dictionary<string, object> Badges;
            public Dictionary<string, object> Json() { return new Dictionary<string, object> { { "cursor", Cursor.ToString() }, { "events", Events }, { "badges", Badges } }; }
        }

        static long Ticks(object v) { return v is DateTime ? ((DateTime)v).Ticks : 0; }
        // a time from the cursor as DATETIME2, to the tick (AddWithValue would send DATETIME: 3 ms steps, and nothing before 1753)
        static SqlCommand At(SqlCommand cmd, string name, long ticks)
        {
            cmd.Parameters.Add(name, System.Data.SqlDbType.DateTime2).Value = ticks > 0 ? new DateTime(ticks) : new DateTime(2000, 1, 1);
            return cmd;
        }

        // where everything is now, and the badge counts, in one round trip
        static Cursor Now(SqlConnection c, out Dictionary<string, object> badges)
        {
            var x = new Cursor();
            using (var cmd = Db.Cmd(c, @"SELECT (SELECT ISNULL(MAX(Id), 0) FROM dbo.PlanRequests), (SELECT ISNULL(MAX(Id), 0) FROM dbo.TicketMessages WHERE FromAdmin=0),
  (SELECT ISNULL(MAX(Id), 0) FROM dbo.ShopReports), (SELECT ISNULL(MAX(Id), 0) FROM dbo.CouncilTopups),
  (SELECT COUNT(*) FROM dbo.Shops WHERE Status='live' AND ReviewChange=1), (SELECT MAX(SubmittedAt) FROM dbo.Shops WHERE Status='review'),
  (SELECT MAX(UpdatedAt) FROM dbo.MetaRequests WHERE Status='pending'),
  (SELECT COUNT(*) FROM dbo.PlanRequests WHERE Status='pending'), (SELECT COUNT(*) FROM dbo.Tickets WHERE Status='open'),
  (SELECT COUNT(*) FROM dbo.Shops WHERE Status='review' OR (Status='live' AND ReviewChange=1)) + (SELECT COUNT(*) FROM dbo.ShopReports WHERE Status='new'),
  (SELECT COUNT(*) FROM dbo.MetaRequests WHERE Status='pending'), (SELECT COUNT(*) FROM dbo.CouncilTopups WHERE Status='pending')"))
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                x.R = (int)r[0]; x.M = (int)r[1]; x.P = (int)r[2]; x.C = (int)r[3]; x.G = (int)r[4]; x.S = Ticks(r[5]); x.A = Ticks(r[6]);
                badges = new Dictionary<string, object> { { "requests", (int)r[7] }, { "tickets", (int)r[8] }, { "shops", (int)r[9] }, { "meta", (int)r[10] }, { "council", (int)r[11] } };
            }
            var wa = WaBridge.WatchJson(c);
            x.W = string.IsNullOrEmpty(wa["downSince"] as string) ? 0 : 1; x.L = Convert.ToInt32(wa["lost"]);
            badges["wa"] = x.W + x.L;
            badges["waDown"] = wa["downSince"]; badges["waLost"] = x.L;
            var bd = badges; bd["total"] = new[] { "requests", "tickets", "shops", "meta", "council", "wa" }.Sum(k => (int)bd[k]);
            return x;
        }

        public static Dictionary<string, object> Badges(SqlConnection c) { Dictionary<string, object> b; var x = Now(c, out b); b["cursor"] = x.ToString(); return b; }

        static string Cut(string s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > n ? s.Substring(0, n - 1) + "…" : s; }
        static string Money(object v) { return Convert.ToDecimal(v).ToString("#,0.##", CultureInfo.InvariantCulture) + " ج"; }
        static readonly Dictionary<string, string> What = new Dictionary<string, string> {
            { "new", "اشتراك جديد" }, { "renew", "تجديد الباقة" }, { "upgrade", "ترقية لباقة أعلى" }, { "downgrade", "نزول لباقة أقل" },
            { "wa-new", "إضافة باقة الواتساب" }, { "wa-renew", "تجديد باقة الواتساب" }, { "wa-add", "أرقام واتساب زيادة" },
            { "shop-new", "إيجار متجر" }, { "shop-renew", "تجديد إيجار متجر" }, { "topup", "رسايل إضافية للشهر" } };

        static Dictionary<string, object> Ev(string key, string type, string title, string body, object at, string route)
        {
            return new Dictionary<string, object> { { "key", key }, { "type", type }, { "title", title }, { "body", body }, { "at", at is DateTime ? Billing.Iso(at) : DateTime.UtcNow.ToString("o") }, { "route", route } };
        }

        // what came since the cursor (nothing for a missing or old cursor: the app starts from now), at most 30, oldest first
        public static Result Since(SqlConnection c, string since)
        {
            var res = new Result();
            var now = Now(c, out res.Badges);
            res.Cursor = now;
            var was = Cursor.Parse(since);
            if (was == null) return res;
            var ev = res.Events;
            if (now.R > was.R)
                using (var cmd = Db.Cmd(c, @"SELECT TOP 20 r.Id, r.Kind, r.What, r.AmountEgp, r.CreatedAt, s.Name FROM dbo.PlanRequests r JOIN dbo.Stores s ON s.Id=r.StoreId
                                             WHERE r.Id > @r AND r.Status='pending' ORDER BY r.Id", "@r", was.R))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        string w; var what = r["What"] as string ?? ((r["Kind"] as string) == "topup" ? "topup" : (r["Kind"] as string) == "shop" ? "shop-new" : (r["Kind"] as string) == "wa" ? "wa-new" : "new");
                        if (!What.TryGetValue(what, out w)) w = "طلب";
                        ev.Add(Ev("r" + r["Id"], "request", "💎 طلب جديد: " + w, r["Name"] + " · " + Money(r["AmountEgp"]), r["CreatedAt"], "/requests/" + r["Id"]));
                    }
            if (now.M > was.M)
                using (var cmd = Db.Cmd(c, @"SELECT TOP 20 m.Id, m.TicketId, m.Body, m.CreatedAt, t.Subject, s.Name,
                                               CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketMessages p WHERE p.TicketId=m.TicketId AND p.Id < m.Id) THEN 0 ELSE 1 END AS First
                                             FROM dbo.TicketMessages m JOIN dbo.Tickets t ON t.Id=m.TicketId JOIN dbo.Stores s ON s.Id=t.StoreId
                                             WHERE m.Id > @m AND m.FromAdmin=0 ORDER BY m.Id", "@m", was.M))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        ev.Add(Ev("m" + r["Id"], "ticket", (int)r["First"] == 1 ? "🎧 تذكرة جديدة من " + r["Name"] : "🎧 رد جديد من " + r["Name"],
                            Cut((string)r["Subject"], 60) + ": " + Cut((string)r["Body"], 90), r["CreatedAt"], "/tickets/" + r["TicketId"]));
            if (now.S > was.S)
                using (var cmd = At(Db.Cmd(c, "SELECT TOP 20 StoreId, Name, SubmittedAt FROM dbo.Shops WHERE Status='review' AND SubmittedAt > @s ORDER BY SubmittedAt"), "@s", was.S))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        ev.Add(Ev("s" + r["StoreId"] + "-" + Ticks(r["SubmittedAt"]), "shop", "🛍️ متجر مستني مراجعتك", (string)r["Name"], r["SubmittedAt"], "/shops/" + r["StoreId"]));
            if (now.G > was.G)
                ev.Add(Ev("g" + now.G + "-" + DateTime.UtcNow.Ticks, "shop", "🛍️ تغيير مستني موافقتك", "متجر شغال عايز يغيّر اسمه أو لينكه أو اللوجو.", null, "/shops"));
            if (now.P > was.P)
                using (var cmd = Db.Cmd(c, @"SELECT TOP 20 r.Id, r.StoreId, r.Text, r.At, sh.Name FROM dbo.ShopReports r JOIN dbo.Shops sh ON sh.StoreId=r.StoreId
                                             WHERE r.Id > @p ORDER BY r.Id", "@p", was.P))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        ev.Add(Ev("p" + r["Id"], "report", "🚩 بلاغ من زبون على " + r["Name"], Cut(r["Text"] as string ?? "", 100), r["At"], "/shops/" + r["StoreId"]));
            if (now.C > was.C)
                using (var cmd = Db.Cmd(c, @"SELECT TOP 20 t.Id, t.AmountEgp, t.CreatedAt, s.Name FROM dbo.CouncilTopups t JOIN dbo.Stores s ON s.Id=t.StoreId
                                             WHERE t.Id > @t AND t.Status='pending' ORDER BY t.Id", "@t", was.C))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        ev.Add(Ev("c" + r["Id"], "council", "🧠 طلب شحن «فكّر معانا»", r["Name"] + " · " + Money(r["AmountEgp"]), r["CreatedAt"], "/council"));
            if (now.A > was.A)
                using (var cmd = At(Db.Cmd(c, @"SELECT TOP 20 m.StoreId, m.UpdatedAt, s.Name FROM dbo.MetaRequests m JOIN dbo.Stores s ON s.Id=m.StoreId
                                             WHERE m.Status='pending' AND m.UpdatedAt > @a ORDER BY m.UpdatedAt"), "@a", was.A))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        ev.Add(Ev("a" + r["StoreId"] + "-" + Ticks(r["UpdatedAt"]), "meta", "🤝 طلب ربط مسنجر وإنستجرام", (string)r["Name"], r["UpdatedAt"], "/meta"));
            if (now.W != was.W)
                ev.Add(Ev("w" + now.W + "-" + DateTime.UtcNow.Ticks, "wa", now.W == 1 ? "📵 سيرفر الواتساب مش بيرد" : "✅ سيرفر الواتساب رجع يشتغل",
                    now.W == 1 ? "الرسايل مش بتتبعت ولا بتوصل لحد ما يرجع." : "الاتصال رجع.", null, "/wa"));
            if (now.L > was.L)
                ev.Add(Ev("l" + now.L + "-" + DateTime.UtcNow.Ticks, "wa", "📵 " + (now.L - was.L) + " رقم واتساب اتفصل", "صاحب المتجر محتاج يربطه تاني.", null, "/wa"));
            ev.Sort((a, b) => string.CompareOrdinal((string)a["at"], (string)b["at"]));
            if (ev.Count > 30) ev.RemoveRange(0, ev.Count - 30);
            return res;
        }
    }
}
