// «موظفين بصلاحيات»: people who help the owner in «متجري». The owner adds a name, a WhatsApp number and what they may do (api/team.ashx);
// they sign in at /staff/?s=<store> with a code sent on WhatsApp, in their own cookie (sb_staff, path /staff, 12 hours at most: never rc_auth),
// and use the same shop-admin screens: the page lives under /staff/, so the screens' relative "api/shop.ashx" is /staff/api/shop.ashx, this
// file's ShopStaffHandler. Every GET and POST there is checked against the list of what their ticks allow (GetOk / PostOk); anything not on
// it is refused, so an action added to «متجري» later stays closed to staff until someone adds it here. Never on any list: the store's
// settings, keys, payment links, the plan, the rent, staff, the bot, the look, publishing, the backup, the customers' list, wiping a customer.
// What they change carries their name (the order's log through ShopOrders.Log_ -> By, and ShopStaffLog for the owner's «مين عمل إيه»).
// Off by default (ShopTeam.TeamOn): off = nobody signs in, and nothing changes for the owner, the customers or the bot.
// The employees who get the owner's WhatsApp alerts (WaLines.cs, Team) are a different thing: staff here never get alerts.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class StaffMember { public int Id, StoreId; public string Name, Phone; public HashSet<string> Perms; }

    public static class ShopStaff
    {
        public const int Max = 10, CodeMinutes = 10, MaxTries = 5, Hours = 12;
        const string Cookie = "sb_staff";
        public static readonly string[] Perms = { "orders", "products", "returns", "money", "ship" };
        // what each tick opens. GET "print" and "export" are checked with their kind: print:label, export:products...
        static readonly Dictionary<string, string[]> GetOk = new Dictionary<string, string[]> {
            { "orders", new[] { "orders", "order", "prep", "print:label", "print:invoice", "print:prep" } },
            { "products", new[] { "products", "product", "cat", "stock", "print:tags", "export:products", "export:template" } },
            { "returns", new[] { "returns", "reviews", "order" } },
            { "money", new[] { "home", "report", "report-xls", "orders", "order" } },
            { "ship", new[] { "orders", "order", "ship", "ship-list", "ship-areas", "print:label", "export:shipping" } } };
        static readonly Dictionary<string, string[]> PostOk = new Dictionary<string, string[]> {
            { "orders", new[] { "order-status", "order-note" } },
            { "products", new[] { "cat-save", "cat-del", "cat-sort", "cat-products", "prod-save", "prod-del", "prod-dup", "prod-bulk", "prod-sort", "stock", "image", "image-del", "image-sort", "image-alt", "file" } },
            { "returns", new[] { "return-do", "review-do" } },
            { "money", new[] { "pay-review" } },
            { "ship", new[] { "ship-check", "ship-send", "ship-labels", "ship-pickup", "ship-cancel", "ship-ask", "ship-drop", "ship-got", "order-ship" } } };
        static readonly string[] Always = { "state", "geo" };   // the frame (a staff copy of it, StaffState) and the governorates
        // what the order's log already uses for "by": a staff member can't be called that
        static readonly HashSet<string> Taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "owner", "customer", "bot", "system", "carrier" };

        public static bool May(StaffMember m, bool get, string key)
        {
            if (get && Always.Contains(key)) return true;
            var map = get ? GetOk : PostOk;
            return m.Perms.Any(p => map.ContainsKey(p) && map[p].Contains(key));
        }

        // ShopOrders.Log_: what a staff member does in this request is written with their name, not "owner"
        public static string By(string who)
        {
            if (who != "owner") return who; var x = HttpContext.Current;
            return (x == null ? null : x.Items["sb-staff"] as string) ?? who;
        }

        static string Sha(string s) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2"))); }
        static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        static int Num(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v); } }
        static string Iso(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc).ToString("o") : ""; }
        static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        static HashSet<string> PermSet(string s) { return new HashSet<string>((s ?? "").Split(',').Where(Perms.Contains)); }

        public static bool On(SqlConnection c, int storeId) { return Num(c, "SELECT COUNT(*) FROM dbo.ShopTeam WHERE StoreId=@s AND TeamOn=1", "@s", storeId) > 0; }

        // ---------- who is asking (the staff cookie) ----------
        public static StaffMember Current(SqlConnection c, HttpContext ctx)
        {
            var ck = ctx.Request.Cookies[Cookie]; if (ck == null || !Regex.IsMatch(ck.Value ?? "", "^[a-f0-9]{64}$")) return null;
            StaffMember m = null; DateTime? last = null;
            using (var cmd = Db.Cmd(c, @"SELECT m.Id, m.StoreId, m.Name, m.Phone, m.Perms, m.LastAt FROM dbo.ShopStaffSessions x JOIN dbo.ShopStaff m ON m.Id=x.StaffId JOIN dbo.ShopTeam t ON t.StoreId=m.StoreId
                WHERE x.Hash=@h AND x.ExpiresAt>SYSUTCDATETIME() AND x.Gen=m.Gen AND m.Active=1 AND t.TeamOn=1", "@h", Sha(ck.Value)))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { m = new StaffMember { Id = (int)r[0], StoreId = (int)r[1], Name = (string)r[2], Phone = (string)r[3], Perms = PermSet((string)r[4]) }; last = r[5] as DateTime?; }
            if (m != null && (!last.HasValue || (DateTime.UtcNow - last.Value).TotalMinutes > 1)) Exec(c, "UPDATE dbo.ShopStaff SET LastAt=SYSUTCDATETIME() WHERE Id=@i", "@i", m.Id);
            return m;
        }

        static void SetCookie(HttpContext ctx, string token, DateTime until)
        {
            var c = new HttpCookie(Cookie, token) { HttpOnly = true, Secure = ctx.Request.IsSecureConnection, Path = "/staff", Expires = until };
            c.SameSite = SameSiteMode.Strict; ctx.Response.Cookies.Add(c);
        }

        // ---------- signing in: {a:"code", s, phone} then {a:"login", s, phone, code} ----------
        // The answer to "code" is the same whether the number is on the team or not: nobody learns who works where.
        public static void SignIn(SqlConnection c, HttpContext ctx, string a, Dictionary<string, object> b)
        {
            var res = ctx.Response; string ip = Web.Ip(ctx.Request), slug = ShopJson.Str(ShopJson.At(b, "s"), 40).ToLowerInvariant(), phone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24));
            var hour = TimeSpan.FromHours(1);
            if (!Web.Allow("staff-in:" + ip, 40, hour)) { Web.Error(res, 429, "rate_limited", "محاولات كتير. استنى ساعة وجرّب تاني."); return; }
            var s = slug.Length == 0 ? null : Db.StoreBySlug(c, slug);
            if (s == null || !On(c, s.Id) || Shops.ByStore(c, s.Id) == null) { Web.Error(res, 404, "no_team", "اللينك ده مش شغال. اطلب لينك الدخول من صاحب المتجر."); return; }
            if (phone == null) { Web.Error(res, 400, "phone", "اكتب رقم الواتساب صح، مثلًا 01001234567."); return; }
            int id = 0; string name = null;
            using (var cmd = Db.Cmd(c, "SELECT Id, Name FROM dbo.ShopStaff WHERE StoreId=@s AND Phone=@p AND Active=1", "@s", s.Id, "@p", phone)) using (var r = cmd.ExecuteReader()) if (r.Read()) { id = (int)r[0]; name = (string)r[1]; }
            if (a == "code")
            {
                if (Sender(c, s) == null) { Web.Error(res, 400, "cant_send", "المتجر مش قادر يبعت أكواد على واتساب دلوقتي. كلّم صاحب المتجر."); return; }
                if (!Web.Allow("staff-code:" + s.Id + ":" + phone, 4, hour) || !Web.Allow("staff-codei:" + ip, 8, hour) || !Web.Allow("staff-codes:" + s.Id, 60, hour)) { Web.Error(res, 429, "rate_limited", "طلبت أكواد كتير. استنى ساعة وجرّب تاني."); return; }
                if (id > 0) SendCode(c, s, id);
                Web.Json(res, 200, D("ok", true, "message", "لو الرقم ده في فريق المتجر، الكود هيوصله على واتساب خلال دقيقة."));
                return;
            }
            if (id == 0 || !CheckCode(c, id, ShopJson.Str(ShopJson.At(b, "code"), 12))) { Web.Error(res, 400, "code", "الكود غلط أو وقته خلص. راجعه أو اطلب كود جديد."); return; }
            string token = Shops.Token(32); var until = DateTime.UtcNow.AddHours(Hours);
            Exec(c, @"DELETE FROM dbo.ShopStaffSessions WHERE ExpiresAt<SYSUTCDATETIME();
                INSERT INTO dbo.ShopStaffSessions(Hash, StaffId, Gen, ExpiresAt, Ip) SELECT @h, Id, Gen, @u, @ip FROM dbo.ShopStaff WHERE Id=@i AND Active=1;
                UPDATE dbo.ShopStaff SET LastIn=SYSUTCDATETIME(), LastAt=SYSUTCDATETIME() WHERE Id=@i", "@h", Sha(token), "@u", until, "@ip", ip, "@i", id);
            SetCookie(ctx, token, until);
            Web.Json(res, 200, D("ok", true, "name", name));
        }

        static void SendCode(SqlConnection c, StoreInfo s, int staffId)
        {
            var b = new byte[4]; using (var g = RandomNumberGenerator.Create()) g.GetBytes(b); string code = (BitConverter.ToUInt32(b, 0) % 1000000).ToString("000000"); int id;
            Exec(c, "UPDATE dbo.ShopStaffCodes SET UsedAt=SYSUTCDATETIME() WHERE StaffId=@i AND UsedAt IS NULL", "@i", staffId);
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopStaffCodes(StaffId, Hash, ExpiresAt) OUTPUT INSERTED.Id VALUES(@i, @h, DATEADD(MINUTE, @m, SYSUTCDATETIME()))", "@i", staffId, "@h", Sha(staffId + ":" + code), "@m", CodeMinutes)) id = (int)cmd.ExecuteScalar();
            var sh = Shops.ByStore(c, s.Id); int sid = s.Id;
            string text = "كود دخولك على «متجري» في *" + (sh != null ? sh.Name : s.Name) + "*: *" + code + "*\nصالح " + CodeMinutes + " دقايق، وماتديهوش لحد.";
            // asked for by the person themselves, like a customer's sign-in code: it goes at once, also at night
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    using (var c2 = Db.Open())
                    {
                        var from = Sender(c2, Db.StoreById(c2, sid)); string to = null, why = null;
                        using (var cmd = Db.Cmd(c2, "SELECT Phone FROM dbo.ShopStaff WHERE Id=@i", "@i", staffId)) to = cmd.ExecuteScalar() as string;
                        if (from == null || to == null) why = WaNotify.NotLinked;
                        else try { await Wapilot.Send(WaAccount.Token(c2, from), from.WaInstance, to + "@c.us", text, "sb-staff-code-" + id); } catch (WaException ex) { why = ex.Message; }
                        if (why != null) Exec(c2, "UPDATE dbo.ShopStaffCodes SET Fail=@f WHERE Id=@i", "@f", why.Length > 200 ? why.Substring(0, 200) : why, "@i", id);
                    }
                }
                catch (Exception ex) { Log.Error(ex); }
            });
        }

        // the number the codes come from: the store's own WhatsApp, else Sellybot's (setting StaffCodeFrom, the slug of the store whose
        // number sends them; "" = none), else none
        public static StoreInfo Sender(SqlConnection c, StoreInfo s)
        {
            if (s != null && WaNotify.CantSend(c, s) == null) return s;
            var slug = Web.Setting("StaffCodeFrom", "sellybot"); var x = string.IsNullOrEmpty(slug) ? null : Db.StoreBySlug(c, slug);
            return x != null && WaNotify.CantSend(c, x) == null ? x : null;
        }

        static bool CheckCode(SqlConnection c, int staffId, string code)
        {
            code = Regex.Replace(ShopJson.Digits(code ?? ""), @"\D", ""); if (code.Length != 6) return false;
            int id = 0, tries = 0; string hash = null;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id, Hash, Tries FROM dbo.ShopStaffCodes WHERE StaffId=@i AND UsedAt IS NULL AND ExpiresAt>SYSUTCDATETIME() ORDER BY Id DESC", "@i", staffId))
            using (var r = cmd.ExecuteReader()) { if (!r.Read()) return false; id = (int)r[0]; hash = (string)r[1]; tries = (int)r[2]; }
            if (tries >= MaxTries) return false;
            // the try is taken before the code is compared, in one statement: many requests at once don't get more tries
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopStaffCodes SET Tries=Tries+1 WHERE Id=@i AND Tries<@m AND UsedAt IS NULL", "@i", id, "@m", MaxTries)) if (cmd.ExecuteNonQuery() == 0) return false;
            if (!Shops.SameKey(hash, Sha(staffId + ":" + code))) return false;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopStaffCodes SET UsedAt=SYSUTCDATETIME() WHERE Id=@i AND UsedAt IS NULL", "@i", id)) return cmd.ExecuteNonQuery() == 1;
        }

        public static void SignOut(SqlConnection c, HttpContext ctx)
        {
            var ck = ctx.Request.Cookies[Cookie]; if (ck != null && Regex.IsMatch(ck.Value ?? "", "^[a-f0-9]{64}$")) Exec(c, "DELETE FROM dbo.ShopStaffSessions WHERE Hash=@h", "@h", Sha(ck.Value));
            SetCookie(ctx, "", DateTime.UtcNow.AddDays(-1));
        }

        // ---------- what staff see ----------
        // The frame's state without what isn't theirs: no settings but what a parcel's paper prints, no payment ways, no rent, no steps,
        // no pages or templates; shipping companies only to «الشحن».
        public static Dictionary<string, object> StaffState(Dictionary<string, object> d, StaffMember m)
        {
            var o = new Dictionary<string, object>();
            foreach (var k in new[] { "slug", "name", "nameEn", "biz", "status", "url", "domain", "img", "everLive", "open", "cats", "counts", "waiting", "noEn", "limits", "wa", "submittedAt", "closeNote" }) if (d.ContainsKey(k)) o[k] = d[k];
            var look = d["draft"] as Dictionary<string, object> ?? new Dictionary<string, object>(); var set = d["settings"] as Dictionary<string, object> ?? new Dictionary<string, object>(); var legal = ShopJson.Sub(set, "legal");
            o["draft"] = new[] { "langs", "first", "logo", "logoDark", "color", "template" }.Where(look.ContainsKey).ToDictionary(k => k, k => look[k]);
            o["settings"] = D("returnsDays", ShopJson.At(set, "returnsDays"), "legal", D("seller", ShopJson.Str(ShopJson.At(legal, "seller")), "phone", ShopJson.Str(ShopJson.At(legal, "phone")), "address", ShopJson.Str(ShopJson.At(legal, "address"))));
            o["service"] = D("admin", true);   // no rent card: the rent is the owner's
            o["carriers"] = m.Perms.Contains("ship") && d.ContainsKey("carriers") ? d["carriers"] : null;
            o["dirty"] = false; o["steps"] = new object[0]; o["pages"] = new object[0]; o["zones"] = new object[0]; o["templates"] = new object[0]; o["bizzes"] = new object[0];
            o["plans"] = D("carts", false, "restock", false); o["leads"] = 0;
            o["staff"] = D("name", m.Name, "perms", m.Perms.ToArray());
            return o;
        }

        // an answer of ShopOwner.Get caught before it's sent, so a part of it can be left out
        public static Dictionary<string, object> Capture(Action<HttpResponse> f, out int status)
        {
            var sw = new StringWriter(); var fake = new HttpResponse(sw); f(fake); status = fake.StatusCode;
            try { return Web.Js.Deserialize<Dictionary<string, object>>(sw.ToString()); } catch { return null; }
        }

        public static void Note(SqlConnection c, StaffMember m, string act, Dictionary<string, object> b)
        {
            try
            {
                var bits = new List<string>(); object v;
                if (b != null && b.TryGetValue("ids", out v)) bits.Add(string.Join(",", ShopJson.List(v).Take(30).Select(x => ShopJson.Str(x, 12))));
                if (b != null && b.TryGetValue("id", out v) && ShopJson.Int(v) > 0) bits.Add(ShopJson.Int(v).ToString());
                if (b != null && b.TryGetValue("to", out v)) bits.Add(ShopJson.Str(v, 20));
                if (b != null && b.TryGetValue("name", out v) && act.StartsWith("prod")) bits.Add(ShopJson.Str(v, 60));
                var rf = string.Join(" · ", bits.Where(x => x.Length > 0)); if (rf.Length > 200) rf = rf.Substring(0, 200);
                Exec(c, "INSERT INTO dbo.ShopStaffLog(StoreId, StaffId, Name, Act, Ref) VALUES(@s, @i, @n, @a, @r)", "@s", m.StoreId, "@i", m.Id, "@n", m.Name, "@a", act, "@r", rf.Length == 0 ? null : rf);
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // ---------- the owner's side (api/team.ashx) ----------
        public static Dictionary<string, object> TeamJson(SqlConnection c, StoreInfo s, HttpRequest req)
        {
            var list = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT Id, Name, Phone, Perms, Active, CreatedAt, LastIn, LastAt FROM dbo.ShopStaff WHERE StoreId=@s ORDER BY Id", "@s", s.Id)) using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(D("id", (int)r[0], "name", (string)r[1], "phone", ShopOrders.Local((string)r[2]), "perms", PermSet((string)r[3]).ToArray(), "active", (bool)r[4], "created", Iso(r[5]), "lastIn", Iso(r[6]), "lastAt", Iso(r[7])));
            var log = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 60 StaffId, Name, Act, Ref, At FROM dbo.ShopStaffLog WHERE StoreId=@s ORDER BY Id DESC", "@s", s.Id)) using (var r = cmd.ExecuteReader())
                while (r.Read()) log.Add(D("staff", (int)r[0], "name", (string)r[1], "act", (string)r[2], "ref", r[3] as string ?? "", "at", Iso(r[4])));
            string fail = null;   // the last code that couldn't be sent, so the owner knows why nobody receives one
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 k.Fail FROM dbo.ShopStaffCodes k JOIN dbo.ShopStaff m ON m.Id=k.StaffId WHERE m.StoreId=@s AND k.At>DATEADD(DAY,-2,SYSUTCDATETIME()) ORDER BY k.Id DESC", "@s", s.Id)) fail = cmd.ExecuteScalar() as string;
            var from = Sender(c, s);
            return D("on", On(c, s.Id), "max", Max, "list", list, "log", log, "from", from == null ? "" : from.Id == s.Id ? "store" : "sellybot", "fail", fail ?? "",
                "link", req.Url.GetLeftPart(UriPartial.Authority) + (req.ApplicationPath ?? "/").TrimEnd('/') + "/staff/?s=" + HttpUtility.UrlEncode(s.Slug));
        }

        public static void TeamPost(SqlConnection c, StoreInfo s, Dictionary<string, object> b)
        {
            string a = ShopJson.Str(ShopJson.At(b, "a"), 20); int id = ShopJson.Int(ShopJson.At(b, "id"));
            switch (a)
            {
                case "on":
                    {
                        bool on = ShopJson.Bool(ShopJson.At(b, "on"));
                        Exec(c, "UPDATE dbo.ShopTeam SET TeamOn=@o WHERE StoreId=@s; IF @@ROWCOUNT=0 INSERT INTO dbo.ShopTeam(StoreId, TeamOn) VALUES(@s, @o)", "@o", on, "@s", s.Id);
                        if (!on) Exec(c, "DELETE x FROM dbo.ShopStaffSessions x JOIN dbo.ShopStaff m ON m.Id=x.StaffId WHERE m.StoreId=@s", "@s", s.Id);
                        return;
                    }
                case "save":
                    {
                        string name = ShopJson.Str(ShopJson.At(b, "name"), 60), phone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24));
                        var perms = ShopJson.List(ShopJson.At(b, "perms")).Select(x => ShopJson.Str(x, 12)).Where(Perms.Contains).Distinct().ToList();
                        if (name.Length < 2 || name.Length > 20) throw new ShopException("name", "اكتب اسم الموظف (من 2 لـ 20 حرف).");
                        if (Taken.Contains(name)) throw new ShopException("name", "اختار اسم تاني للموظف.");
                        if (phone == null) throw new ShopException("phone", "اكتب رقم واتساب الموظف صح، مثلًا 01001234567.");
                        if (perms.Count == 0) throw new ShopException("perms", "اختار حاجة واحدة على الأقل الموظف يقدر يعملها.");
                        using (var tx = c.BeginTransaction())
                        {
                            Func<string, object[], int> n = (q, x) => { using (var cmd = Db.Cmd(c, q, x)) { cmd.Transaction = tx; return Convert.ToInt32(cmd.ExecuteScalar()); } };
                            // two saves at once can't pass the limit or add the same number twice
                            if (n("SELECT COUNT(*) FROM dbo.ShopStaff WITH (UPDLOCK, HOLDLOCK) WHERE StoreId=@s AND Phone=@p AND Id<>@i", new object[] { "@s", s.Id, "@p", phone, "@i", id }) > 0) throw new ShopException("phone", "الرقم ده موجود في الفريق بالفعل.");
                            if (id > 0)
                            {
                                if (n("UPDATE dbo.ShopStaff SET Name=@n, Phone=@p, Perms=@m, Gen=CASE WHEN Phone<>@p THEN Gen+1 ELSE Gen END WHERE Id=@i AND StoreId=@s; SELECT @@ROWCOUNT", new object[] { "@n", name, "@p", phone, "@m", string.Join(",", perms), "@i", id, "@s", s.Id }) == 0) throw new ShopException("not_found", "الموظف ده مش موجود.");
                            }
                            else
                            {
                                if (n("SELECT COUNT(*) FROM dbo.ShopStaff WITH (UPDLOCK, HOLDLOCK) WHERE StoreId=@s", new object[] { "@s", s.Id }) >= Max) throw new ShopException("max", "الفريق وصل " + Max + " موظفين، ده الحد. امسح حد الأول.");
                                n("INSERT INTO dbo.ShopStaff(StoreId, Name, Phone, Perms) VALUES(@s, @n, @p, @m); SELECT 1", new object[] { "@s", s.Id, "@n", name, "@p", phone, "@m", string.Join(",", perms) });
                            }
                            tx.Commit();
                        }
                        return;
                    }
                // stopped: out on the next request (the session's Gen no longer matches), and no new code
                case "stop": Exec(c, "UPDATE dbo.ShopStaff SET Active=0, Gen=Gen+1 WHERE Id=@i AND StoreId=@s; DELETE x FROM dbo.ShopStaffSessions x JOIN dbo.ShopStaff m ON m.Id=x.StaffId WHERE m.Id=@i AND m.StoreId=@s", "@i", id, "@s", s.Id); return;
                case "start": Exec(c, "UPDATE dbo.ShopStaff SET Active=1 WHERE Id=@i AND StoreId=@s", "@i", id, "@s", s.Id); return;
                case "del": Exec(c, "DELETE FROM dbo.ShopStaff WHERE Id=@i AND StoreId=@s", "@i", id, "@s", s.Id); return;
            }
            throw new ShopException("unknown", "الطلب ده مش معروف.");
        }

        public const string Schema = @"
IF OBJECT_ID('dbo.ShopTeam') IS NULL
  CREATE TABLE dbo.ShopTeam(StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE, TeamOn BIT NOT NULL DEFAULT 0);
IF OBJECT_ID('dbo.ShopStaff') IS NULL
BEGIN
  CREATE TABLE dbo.ShopStaff(Id INT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Name NVARCHAR(20) NOT NULL, Phone NVARCHAR(20) NOT NULL,
    Perms NVARCHAR(100) NOT NULL, Active BIT NOT NULL DEFAULT 1, Gen INT NOT NULL DEFAULT 0, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), LastIn DATETIME2 NULL, LastAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopStaff_Phone ON dbo.ShopStaff(StoreId, Phone);
END
IF OBJECT_ID('dbo.ShopStaffCodes') IS NULL
BEGIN
  CREATE TABLE dbo.ShopStaffCodes(Id INT IDENTITY(1,1) PRIMARY KEY, StaffId INT NOT NULL REFERENCES dbo.ShopStaff(Id) ON DELETE CASCADE, Hash NVARCHAR(64) NOT NULL,
    Tries INT NOT NULL DEFAULT 0, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ExpiresAt DATETIME2 NOT NULL, UsedAt DATETIME2 NULL, Fail NVARCHAR(200) NULL);
  CREATE INDEX IX_ShopStaffCodes ON dbo.ShopStaffCodes(StaffId, Id);
END
IF OBJECT_ID('dbo.ShopStaffSessions') IS NULL
  CREATE TABLE dbo.ShopStaffSessions(Hash NVARCHAR(64) NOT NULL PRIMARY KEY, StaffId INT NOT NULL REFERENCES dbo.ShopStaff(Id) ON DELETE CASCADE, Gen INT NOT NULL,
    At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), ExpiresAt DATETIME2 NOT NULL, Ip NVARCHAR(64) NULL);
IF OBJECT_ID('dbo.ShopStaffLog') IS NULL
BEGIN
  CREATE TABLE dbo.ShopStaffLog(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, StaffId INT NOT NULL, Name NVARCHAR(20) NOT NULL,
    Act NVARCHAR(30) NOT NULL, Ref NVARCHAR(200) NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ShopStaffLog ON dbo.ShopStaffLog(StoreId, Id);
END";
    }

    // /staff/api/shop.ashx: the staff member's own door to «متجري». Only the sb_staff cookie counts here, never rc_auth.
    public class ShopStaffHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                Func<Task> later = null;
                using (var c = Db.Open())
                {
                    bool get = req.HttpMethod == "GET";
                    if (!get && (req.HttpMethod != "POST" || !Web.CsrfOk(req))) { Web.Error(res, 400, "bad_request"); return; }
                    bool file = !get && req.Files.Count > 0;
                    Dictionary<string, object> b = get || file ? null : Web.Body(req, 3600000);
                    string a = get ? req.QueryString["a"] ?? "state" : file ? "file" : ShopJson.Str(ShopJson.At(b, "a"), 30);
                    if (!get && (a == "code" || a == "login")) { ShopStaff.SignIn(c, ctx, a, b); return; }
                    var m = ShopStaff.Current(c, ctx);
                    if (!get && a == "logout") { ShopStaff.SignOut(c, ctx); Web.Json(res, 200, new Dictionary<string, object> { { "ok", true } }); return; }
                    if (m == null) { Web.Error(res, 401, "not_logged_in", "سجّل دخولك تاني."); return; }
                    var s = Db.StoreById(c, m.StoreId); var sh = s == null ? null : Shops.ByStore(c, s.Id);
                    if (s == null || sh == null || !Shops.Visible(c, s)) { Web.Error(res, 403, "not_open", "المتجر مش مفتوح دلوقتي."); return; }
                    if (get && a == "me") { Web.Json(res, 200, new Dictionary<string, object> { { "name", m.Name }, { "perms", m.Perms.ToArray() }, { "store", sh.Name }, { "slug", s.Slug } }); return; }
                    string key = !get ? a : a == "print" ? "print:" + (req.QueryString["kind"] ?? "") : a == "export" ? "export:" + (req.QueryString["what"] ?? "") : a;
                    if (!ShopStaff.May(m, get, key)) { Web.Error(res, 403, "not_allowed", "الحاجة دي مش من صلاحياتك. اطلبها من صاحب المتجر."); return; }
                    if (!Web.Allow((get ? "staff-get:" : "staff-post:") + m.Id, get ? 3000 : 1500, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "طلبات كتير، استنى شوية."); return; }
                    ctx.Items["sb-staff"] = m.Name;   // ShopOrders.Log_ writes this name where the owner's actions say "owner"
                    if (get)
                    {
                        if (a == "state") { Web.Json(res, 200, ShopStaff.StaffState(ShopOwner.State(c, s, sh), m)); return; }
                        if (a == "orders" || a == "order" || a == "ship")
                        {
                            int st; var d = ShopStaff.Capture(r => ShopOwner.Get(c, s, sh, req, r), out st);
                            if (d == null) { Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); return; }
                            if (a == "orders" && !m.Perms.Contains("money")) d.Remove("sum");   // «الأوردرات بس»: no money totals
                            if (a == "order") { d.Remove("token"); if (!m.Perms.Contains("money")) d.Remove("proof"); }
                            if (a == "ship") foreach (var x in ShopJson.List(ShopJson.At(d, "list")).OfType<Dictionary<string, object>>()) { x.Remove("last4"); x.Remove("code"); }
                            Web.Json(res, st, d); return;
                        }
                        ShopOwner.Get(c, s, sh, req, res); return;
                    }
                    if (file) { ShopExcel.Import(c, s, sh, req, res); ShopStaff.Note(c, m, req.Form["kind"] == "video" ? "video" : "import", null); return; }
                    later = ShopOwner.Post(c, s, sh, b, res);
                    if (later != null || res.StatusCode == 200) ShopStaff.Note(c, m, a, b);
                }
                if (later != null) await later();
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { Web.Json(res, 400, new Dictionary<string, object> { { "error", ex.Code }, { "message", ex.Message }, { "data", ex.Extra } }); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }
    }

    // api/team.ashx: the owner's list of staff. GET = the team, POST {a: on | save | stop | start | del}. The owner's rc_auth only.
    public class ShopTeamHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (!Shops.Visible(c, s)) { Web.Error(res, 403, "not_open", "المتاجر لسه مافتحتش."); return; }
                    if (req.HttpMethod != "GET")
                    {
                        if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                        if (!Web.Allow("team-post:" + s.Id, 300, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "تعديلات كتير، استنى شوية."); return; }
                        Shops.Create(c, s); ShopStaff.TeamPost(c, s, Web.Body(req, 4000));
                    }
                    Web.Json(res, 200, ShopStaff.TeamJson(c, s, req));
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { Web.Json(res, 400, new Dictionary<string, object> { { "error", ex.Code }, { "message", ex.Message } }); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }
    }
}
