// Store owner accounts (sign up / log in), store settings, and the admin overview.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Web;

namespace RiwaqChat
{
    static class Me
    {
        public static Dictionary<string, object> Describe(SqlConnection c, StoreInfo s)
        {
            var u = Db.Usage(c, s.Id, Db.Month);
            int pct = Storage.Percent(c);
            var storage = new Dictionary<string, object> { { "percent", pct }, { "nearlyFull", pct >= 90 }, { "full", pct >= 98 } };
            if (s.IsAdmin) { storage["usedMB"] = Math.Round(Storage.UsedMB(c), 1); storage["capacityMB"] = Storage.CapacityMB; }
            int items, chars;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*), ISNULL(SUM(CAST(LEN(Content) AS BIGINT)),0) FROM dbo.KnowledgeItems WHERE StoreId=@s", "@s", s.Id))
            using (var r = cmd.ExecuteReader()) { r.Read(); items = Convert.ToInt32(r[0]); chars = Convert.ToInt32(r[1]); }
            return new Dictionary<string, object>
            {
                { "id", s.Id }, { "slug", s.Slug }, { "name", s.Name }, { "email", s.OwnerEmail }, { "isAdmin", s.IsAdmin },
                { "status", s.Status }, { "botName", s.BotName }, { "welcome", s.Welcome ?? "" }, { "instructions", s.Instructions ?? "" },
                { "allowedOrigins", s.AllowedOrigins ?? "" }, { "bubbleStyle", s.BubbleStyle }, { "brandColor", s.BrandColor }, { "chatTitle", s.ChatTitle ?? "" }, { "suggestions", s.Suggestions ?? "" },
                { "usage", new Dictionary<string, object> { { "month", Db.Month }, { "messages", u.Messages }, { "used", u.Weighted }, { "limit", s.MonthlyTokenLimit } } },
                { "storage", storage },
                { "knowledge", new Dictionary<string, object> { { "items", items }, { "chars", chars }, { "maxChars", KnowledgeHandler.MaxStoreChars },
                                                                { "fullModeMaxChars", Claude.FullModeMaxChars }, { "mode", chars > Claude.FullModeMaxChars ? "search" : "full" } } },
            };
        }

        public static StoreInfo Require(HttpContext ctx, SqlConnection c)
        {
            var id = Web.CurrentStoreId(ctx);
            return id == null ? null : Db.StoreById(c, id.Value);
        }
    }

    // api/auth.ashx?action=signup|login|logout|me
    public class AuthHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
            try
            {
                using (var c = Db.Open())
                {
                    if (action == "me")
                    {
                        var s = Me.Require(ctx, c);
                        if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                        Web.Json(res, 200, Me.Describe(c, s));
                        return;
                    }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }

                    if (action == "logout") { Web.SignOut(ctx); Web.Json(res, 200, new { ok = true }); return; }

                    var b = Web.Body(req, 5000);
                    string email = (Web.Str(b, "email", 200) ?? "").ToLowerInvariant();
                    string password = Web.Str(b, "password", 200) ?? "";

                    if (action == "login")
                    {
                        if (!Web.Allow("login:" + Web.Ip(req), 10, TimeSpan.FromMinutes(15))) { Web.Error(res, 429, "rate_limited", "محاولات كتير، استنى شوية وجرّب تاني."); return; }
                        // log in with the email or with the store id (e.g. "riwaq")
                        var s = email.Contains("@") ? Db.StoreByEmail(c, email) : Db.StoreBySlug(c, email);
                        if (s == null || !Web.CheckPassword(password, s.PasswordHash)) { Web.Error(res, 401, "bad_login", "بيانات الدخول أو الباسورد غلط."); return; }
                        Web.SignIn(ctx, s.Id);
                        Web.Json(res, 200, Me.Describe(c, s));
                        return;
                    }

                    if (action == "signup")
                    {
                        if (!Web.Allow("signup:" + Web.Ip(req), 5, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "عملت حسابات كتير، جرّب بعد ساعة."); return; }
                        string name = Web.Str(b, "name", 100);
                        string slug = (Web.Str(b, "slug", 40) ?? "").ToLowerInvariant();
                        string code = Web.Str(b, "adminCode", 200);
                        bool admin = !string.IsNullOrEmpty(code) && code == Web.Setting("AdminSignupCode");
                        if (!string.IsNullOrEmpty(code) && !admin) { Web.Error(res, 400, "bad_code", "كود الأدمن غلط."); return; }
                        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) { Web.Error(res, 400, "bad_name", "اكتب اسم المتجر."); return; }
                        if (!Web.SlugRx.IsMatch(slug)) { Web.Error(res, 400, "bad_slug", "معرّف المتجر لازم يكون حروف إنجليزي صغيرة وأرقام وشرطة (-)، من 3 لـ 30 حرف."); return; }
                        if (Web.Reserved(slug) && !admin) { Web.Error(res, 400, "reserved_slug", "المعرّف ده محجوز، اختار غيره."); return; }
                        if (!Web.ValidEmail(email)) { Web.Error(res, 400, "bad_email", "الإيميل مش صحيح."); return; }
                        if (password.Length < 8) { Web.Error(res, 400, "weak_password", "الباسورد لازم يكون 8 حروف على الأقل."); return; }
                        if (Db.StoreBySlug(c, slug) != null) { Web.Error(res, 409, "slug_taken", "المعرّف ده مستخدم، اختار غيره."); return; }
                        if (Db.StoreByEmail(c, email) != null) { Web.Error(res, 409, "email_taken", "الإيميل ده عنده حساب بالفعل، سجّل دخول."); return; }

                        int freeLimit;
                        if (!int.TryParse(Web.Setting("FreeMonthlyTokenLimit", "300000"), out freeLimit)) freeLimit = 300000;
                        int id;
                        using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Stores (Slug, Name, OwnerEmail, PasswordHash, IsAdmin, MonthlyTokenLimit, Welcome, BotName)
                                                     OUTPUT INSERTED.Id VALUES (@slug, @name, @email, @hash, @admin, @limit, @welcome, N'')",
                            "@slug", slug, "@name", name, "@email", email, "@hash", Web.HashPassword(password), "@admin", admin,
                            "@limit", admin ? 20000000 : freeLimit,
                            "@welcome", "أهلاً بيك في **" + name + "**! 👋\nعندك أي سؤال؟ اسألني وأنا أساعدك على طول."))
                            id = (int)cmd.ExecuteScalar();
                        if (slug == Web.Setting("LegacyStore", "riwaq")) ImportLegacy(c, id);
                        Web.SignIn(ctx, id);
                        Web.Json(res, 200, Me.Describe(c, Db.StoreById(c, id)));
                        return;
                    }
                    Web.Error(res, 404, "unknown_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) { Web.Error(res, 409, "taken", "المعرّف أو الإيميل مستخدم بالفعل."); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", "⚠️ مساحة التخزين على السيرفر خلصت، فمش هنقدر نعمل حسابات جديدة دلوقتي. كلّم إدارة الموقع."); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        // When the original store (riwaq) gets its account, bring over the data it was using from App_Data/products.txt.
        static void ImportLegacy(SqlConnection c, int storeId)
        {
            try
            {
                var path = System.Web.Hosting.HostingEnvironment.MapPath("~/App_Data/products.txt");
                if (!System.IO.File.Exists(path)) return;
                var text = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8).Trim();
                if (text.Length == 0) return;
                if (text.Length > KnowledgeHandler.MaxItemChars) text = text.Substring(0, KnowledgeHandler.MaxItemChars);
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.KnowledgeItems (StoreId, Title, SourceType, FileName, Content) VALUES (@s, @t, 'txt', 'products.txt', @c)",
                    "@s", storeId, "@t", "بيانات المتجر (منقولة من الملف القديم)", "@c", text))
                    cmd.ExecuteNonQuery();
                Db.BumpVersion(c, storeId);
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    // api/store.ashx  GET = my settings, POST = update settings
    public class StoreHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, Me.Describe(c, s)); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }

                    // Only the fields that were sent are changed (each dashboard tab saves its own part).
                    var b = Web.Body(req, 60000);
                    var sets = new List<string>();
                    var ps = new List<object>();
                    if (b.ContainsKey("name")) { var v = Web.Str(b, "name", 100); if (!string.IsNullOrWhiteSpace(v)) { sets.Add("Name=@name"); ps.Add("@name"); ps.Add(v); } }
                    if (b.ContainsKey("botName")) { sets.Add("BotName=@bot"); ps.Add("@bot"); ps.Add((Web.Str(b, "botName", 50) ?? "").Trim()); }
                    if (b.ContainsKey("welcome")) { sets.Add("Welcome=@welcome"); ps.Add("@welcome"); ps.Add(NullIfEmpty(Web.Str(b, "welcome", 600))); }
                    if (b.ContainsKey("instructions")) { sets.Add("Instructions=@ins"); ps.Add("@ins"); ps.Add(NullIfEmpty(Web.Str(b, "instructions", 20000))); }
                    if (b.ContainsKey("allowedOrigins")) { sets.Add("AllowedOrigins=@origins"); ps.Add("@origins"); ps.Add(NormalizeOrigins(Web.Str(b, "allowedOrigins", 1000))); }
                    if (b.ContainsKey("suggestions")) { var list = Claude.CleanSuggestions(Web.Str(b, "suggestions", 2000)); sets.Add("Suggestions=@sugg"); ps.Add("@sugg"); ps.Add(list.Count == 0 ? null : string.Join("\n", list)); }
                    if (b.ContainsKey("chatTitle")) { sets.Add("ChatTitle=@title"); ps.Add("@title"); ps.Add(NullIfEmpty(Web.Str(b, "chatTitle", 80))); }
                    if (b.ContainsKey("bubbleStyle"))
                    {
                        var v = Web.Str(b, "bubbleStyle", 20);
                        if (!BubbleStyles.Contains(v)) { Web.Error(res, 400, "bad_style", "شكل الفقاعة مش معروف."); return; }
                        sets.Add("BubbleStyle=@style"); ps.Add("@style"); ps.Add(v);
                    }
                    if (b.ContainsKey("brandColor"))
                    {
                        var v = (Web.Str(b, "brandColor", 9) ?? "").ToLowerInvariant();
                        if (!System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9a-f]{6}$")) { Web.Error(res, 400, "bad_color", "كود اللون لازم يكون زي #e8774a"); return; }
                        sets.Add("BrandColor=@color"); ps.Add("@color"); ps.Add(v);
                    }
                    if (sets.Count > 0)
                    {
                        ps.Add("@id"); ps.Add(s.Id);
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET " + string.Join(", ", sets) + ", DataVersion=DataVersion+1 WHERE Id=@id", ps.ToArray()))
                            cmd.ExecuteNonQuery();
                    }
                    Web.Json(res, 200, Me.Describe(c, Db.StoreById(c, s.Id)));
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        public static readonly HashSet<string> BubbleStyles = new HashSet<string>
        {
            "classic", "pill", "agent", "minimal", "bear", "cat", "blob", "bag", "gift", "star",
            "orb", "duo", "wave", "robot", "owl", "ghost", "bunny", "heart", "cup", "cart"
        };

        static string NullIfEmpty(string v) { return string.IsNullOrWhiteSpace(v) ? null : v; }

        // "mystore.com, https://shop.myshopify.com" -> "https://mystore.com,https://shop.myshopify.com"
        static string NormalizeOrigins(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var list = raw.Split(new[] { ',', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(o => o.Trim().TrimEnd('/'))
                .Select(o => o.StartsWith("http://") || o.StartsWith("https://") ? o : "https://" + o)
                .Select(o => { Uri u; return Uri.TryCreate(o, UriKind.Absolute, out u) ? u.Scheme + "://" + u.Authority : null; })
                .Where(o => o != null).Distinct().Take(20);
            var joined = string.Join(",", list);
            return joined.Length == 0 ? null : joined;
        }
    }

    // api/admin.ashx  GET = all stores + usage, POST {id, monthlyTokenLimit, status}
    public class AdminHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var me = Me.Require(ctx, c);
                    if (me == null || !me.IsAdmin) { Web.Error(res, 403, "forbidden"); return; }
                    if (req.HttpMethod == "POST")
                    {
                        if (!Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                        var b = Web.Body(req, 5000);
                        var id = Web.Int(b, "id");
                        var limit = Web.Int(b, "monthlyTokenLimit");
                        var status = Web.Str(b, "status", 20);
                        if (id == null || (status != null && status != "active" && status != "suspended")) { Web.Error(res, 400, "bad_request"); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET MonthlyTokenLimit=ISNULL(@l,MonthlyTokenLimit), Status=ISNULL(@st,Status) WHERE Id=@id",
                            "@l", limit.HasValue ? (object)Math.Max(0, limit.Value) : null, "@st", status, "@id", id.Value))
                            cmd.ExecuteNonQuery();
                    }
                    var stores = Db.AllStores(c).Select(s =>
                    {
                        var u = Db.Usage(c, s.Id, Db.Month);
                        return new Dictionary<string, object>
                        {
                            { "id", s.Id }, { "slug", s.Slug }, { "name", s.Name }, { "email", s.OwnerEmail }, { "isAdmin", s.IsAdmin },
                            { "status", s.Status }, { "limit", s.MonthlyTokenLimit }, { "used", u.Weighted }, { "messages", u.Messages },
                            { "created", s.CreatedAt.ToString("yyyy-MM-dd") },
                        };
                    }).ToList();
                    Web.Json(res, 200, new Dictionary<string, object> { { "month", Db.Month }, { "stores", stores } });
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error"); }
        }
    }
}
