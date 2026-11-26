// «أكتر من متجر للتاجر من نفس الحساب»: a merchant with a second brand opens another Sellybot store from their account («متجر جديد»). It's a
// whole store of its own (its bot, knowledge, plan, rented store and rent) linked to the first one by Stores.ParentId = the main store.
// Login doesn't change: OwnerEmail stays UNIQUE and only the main store has the merchant's email and password; a linked store gets an
// address that can't receive mail and a password nobody can type, so the only way in is the switcher: a new rc_auth for the other store,
// given only after the server checked from both ends (the target's ParentId is my main store, and mine is that same main store or I am it).
// Abuse: a linked store has no free trial (Billing.TrialEnds: it ends the day it's made), so it starts with nothing until the owner
// subscribes through the usual plan request; at most Max stores in a group, main included. The admin sees the groups (admin.html and the
// SellyStore page) and turns «متجر جديد» on (AppSettings BrandsOn, off by default). Off = nobody can open a new one; groups already made
// keep switching.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Web;

namespace RiwaqChat
{
    public static class ShopBrands
    {
        public const int Max = 5;
        public const string Mark = "linked$";   // PasswordHash of a linked store: never "pbkdf2$...", so no password ever matches it

        public static bool On(SqlConnection c) { return Billing.Get(c, "BrandsOn") == "1"; }

        // Db.ReadStore: the column is made by Schema at the first connection; a database without it yet reads as "not linked"
        public static int? Parent(IDataRecord r) { try { var v = r["ParentId"]; return v is int ? (int?)v : null; } catch (IndexOutOfRangeException) { return null; } }

        // the group's main store: a linked store's parent while it exists, else the store itself
        public static StoreInfo Main(SqlConnection c, StoreInfo s)
        {
            if (s == null || !s.ParentId.HasValue) return s;
            var p = Db.StoreById(c, s.ParentId.Value);
            return p != null && !p.ParentId.HasValue ? p : s;
        }

        // both ends: the two stores name the same main store, and that main store isn't linked to another
        public static bool Linked(SqlConnection c, StoreInfo me, StoreInfo to)
        {
            if (me == null || to == null || me.Id == to.Id) return false;
            var a = Main(c, me); var b = Main(c, to);
            if (a == null || b == null || a.Id != b.Id || a.ParentId.HasValue) return false;
            return (me.Id == a.Id || me.ParentId == a.Id) && (to.Id == a.Id || to.ParentId == a.Id);
        }

        static List<Dictionary<string, object>> Group(SqlConnection c, StoreInfo main, int current)
        {
            var l = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT Id, Slug, Name, PlanName, PlanEndsAt, PlanId, Status, CreatedAt FROM dbo.Stores WHERE Id=@m OR ParentId=@m ORDER BY CASE WHEN Id=@m THEN 0 ELSE 1 END, Id", "@m", main.Id))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var ends = r["PlanEndsAt"] as DateTime?;
                    l.Add(new Dictionary<string, object> { { "id", (int)r["Id"] }, { "slug", (string)r["Slug"] }, { "name", (string)r["Name"] }, { "main", (int)r["Id"] == main.Id }, { "current", (int)r["Id"] == current },
                        { "plan", r["PlanId"] is int ? r["PlanName"] as string ?? "" : "" }, { "planEnded", r["PlanId"] is int && ends.HasValue && ends.Value < DateTime.UtcNow }, { "status", (string)r["Status"] }, { "created", ((DateTime)r["CreatedAt"]).ToString("o") } });
                }
            return l;
        }

        public static Dictionary<string, object> Json(SqlConnection c, StoreInfo me)
        {
            var main = Main(c, me); var g = Group(c, main, me.Id);
            return new Dictionary<string, object> { { "on", On(c) }, { "max", Max }, { "list", g }, { "canAdd", On(c) && g.Count < Max }, { "linked", me.Id != main.Id } };
        }

        // a new store in my group. Two presses at once can't pass the limit (the main store's row is locked while counting)
        public static int Create(SqlConnection c, HttpContext ctx, StoreInfo me, Dictionary<string, object> b)
        {
            string name = (Web.Str(b, "name", 100) ?? "").Trim(), slug = (Web.Str(b, "slug", 40) ?? "").ToLowerInvariant();
            if (name.Length < 2) throw new ShopException("name", "اكتب اسم المتجر الجديد.");
            if (!Web.SlugRx.IsMatch(slug)) throw new ShopException("slug", "معرّف المتجر لازم يكون حروف إنجليزي صغيرة وأرقام وشرطة (-)، من 3 لـ 30 حرف.");
            if (Web.Reserved(slug)) throw new ShopException("slug", "المعرّف ده محجوز، اختار غيره.");
            if (Db.StoreBySlug(c, slug) != null) throw new ShopException("slug", "المعرّف ده مستخدم، اختار غيره.");
            var main = Main(c, me); int id;
            using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
            {
                Func<string, object[], object> one = (q, a) => { using (var cmd = Db.Cmd(c, q, a)) { cmd.Transaction = tx; return cmd.ExecuteScalar(); } };
                one("SELECT Id FROM dbo.Stores WITH (UPDLOCK, HOLDLOCK) WHERE Id=@m", new object[] { "@m", main.Id });
                if (Convert.ToInt32(one("SELECT COUNT(*) FROM dbo.Stores WITH (UPDLOCK, HOLDLOCK) WHERE Id=@m OR ParentId=@m", new object[] { "@m", main.Id })) >= Max)
                    throw new ShopException("max", "عندك " + Max + " متاجر، ده الحد. كلّمنا لو محتاج أكتر.");
                id = Convert.ToInt32(one(@"INSERT INTO dbo.Stores (Slug, Name, OwnerEmail, PasswordHash, IsAdmin, MonthlyTokenLimit, Welcome, BotName, OwnerPhone, ParentId)
                    OUTPUT INSERTED.Id VALUES (@slug, @name, @email, @hash, 0, 0, @welcome, N'', @phone, @main)", new object[] {
                    "@slug", slug, "@name", name, "@email", slug + "." + Shops.Token(4) + "@linked.invalid", "@hash", Mark + Shops.Token(16), "@phone", main.OwnerPhone, "@main", main.Id,
                    "@welcome", "أهلاً بيك في **" + name + "**! 👋\nعندك أي سؤال؟ اسألني وأنا أساعدك على طول." }));
                tx.Commit();
            }
            return id;
        }

        public const string Schema = @"
IF COL_LENGTH('dbo.Stores', 'ParentId') IS NULL ALTER TABLE dbo.Stores ADD ParentId INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Stores_Parent') EXEC sp_executesql N'CREATE INDEX IX_Stores_Parent ON dbo.Stores(ParentId) WHERE ParentId IS NOT NULL';";
    }

    // api/brands.ashx  owner: GET = my group; POST {a:"new", name, slug} | {a:"switch", id}
    //                  admin (?admin=1): GET = the switch and every group; POST {on}
    public class ShopBrandsHandler : IHttpHandler
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
                    if (req.HttpMethod != "GET" && (req.HttpMethod != "POST" || !Web.CsrfOk(req))) { Web.Error(res, 400, "bad_request"); return; }
                    var b = req.HttpMethod == "POST" ? Web.Body(req, 3000) : null;
                    if (req.QueryString["admin"] == "1") { Admin(c, ctx, b); return; }
                    var me = Me.Require(ctx, c);
                    if (me == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (b != null)
                    {
                        var a = Web.Str(b, "a", 20);
                        if (!Web.Allow("brands:" + Web.Ip(req), 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "محاولات كتير، استنى شوية."); return; }
                        if (a == "new")
                        {
                            if (!ShopBrands.On(c)) { Web.Error(res, 403, "off", "فتح متجر تاني مش متاح لسه."); return; }
                            if (!Web.Allow("brands-new:" + ShopBrands.Main(c, me).Id, 20, TimeSpan.FromDays(1))) { Web.Error(res, 429, "rate_limited", "عملت متاجر كتير النهارده، جرّب بكرة."); return; }
                            int id = ShopBrands.Create(c, ctx, me, b);
                            Web.SignIn(ctx, id);   // straight into the new store: it was made in this group just now
                            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "id", id } }); return;
                        }
                        if (a == "switch")
                        {
                            var id = Web.Int(b, "id"); var to = id.HasValue ? Db.StoreById(c, id.Value) : null;
                            if (!ShopBrands.Linked(c, me, to)) { Web.Error(res, 403, "not_linked", "المتجر ده مش من متاجرك."); return; }
                            Web.SignIn(ctx, to.Id);
                            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "id", to.Id } }); return;
                        }
                        Web.Error(res, 404, "unknown_action"); return;
                    }
                    Web.Json(res, 200, ShopBrands.Json(c, me));
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { Web.Error(res, 400, ex.Code, ex.Message); }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) { Web.Error(res, 409, "taken", "المعرّف ده مستخدم، اختار غيره."); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static void Admin(SqlConnection c, HttpContext ctx, Dictionary<string, object> b)
        {
            var res = ctx.Response; var adminId = AdminAuth.Current(ctx); bool ok = false;
            if (adminId.HasValue) using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Admins WHERE Id=@id", "@id", adminId.Value)) ok = (int)cmd.ExecuteScalar() > 0;
            if (!ok) { Web.Error(res, 401, "not_logged_in"); return; }
            if (b != null) Billing.Set(c, "BrandsOn", ShopJson.Bool(ShopJson.At(b, "on")) ? "1" : "0");
            var groups = new Dictionary<int, List<Dictionary<string, object>>>();
            using (var cmd = Db.Cmd(c, @"SELECT s.Id, s.Slug, s.Name, s.OwnerEmail, s.ParentId, s.PlanName, s.PlanId FROM dbo.Stores s
                WHERE s.ParentId IS NOT NULL OR EXISTS (SELECT 1 FROM dbo.Stores k WHERE k.ParentId = s.Id) ORDER BY s.Id"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    int key = r["ParentId"] is int ? (int)r["ParentId"] : (int)r["Id"];
                    if (!groups.ContainsKey(key)) groups[key] = new List<Dictionary<string, object>>();
                    groups[key].Add(new Dictionary<string, object> { { "id", (int)r["Id"] }, { "slug", (string)r["Slug"] }, { "name", (string)r["Name"] }, { "main", !(r["ParentId"] is int) },
                        { "email", r["ParentId"] is int ? "" : (string)r["OwnerEmail"] }, { "plan", r["PlanId"] is int ? r["PlanName"] as string ?? "" : "" } });
                }
            Web.Json(res, 200, new Dictionary<string, object> { { "on", ShopBrands.On(c) }, { "max", ShopBrands.Max },
                { "groups", groups.Select(kv => new Dictionary<string, object> { { "main", kv.Key }, { "stores", kv.Value.OrderBy(x => (bool)x["main"] ? 0 : 1).ToList() } }).ToList() } });
        }
    }
}
