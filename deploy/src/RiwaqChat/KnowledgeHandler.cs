// api/knowledge.ashx — a store owner's data: add text, upload files (txt/md/csv/pdf/docx/xlsx), edit, delete.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public class KnowledgeHandler : HttpTaskAsyncHandler
    {
        // One part (a file or a text) and the whole store. Above Claude.FullModeMaxChars the store uses search mode,
        // so big catalogs (thousands of products) are fine; the cap only protects the server's memory.
        public const int MaxItemChars = 3000000;
        public static int MaxStoreChars
        {
            get { int v; return int.TryParse(Web.Setting("MaxStoreChars", "5000000"), out v) ? v : 5000000; }
        }
        const int MaxFileBytes = 10 * 1024 * 1024;

        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }

                    if (req.HttpMethod == "GET")
                    {
                        int id;
                        if (int.TryParse(req.QueryString["id"], out id)) { var one = Item(c, s.Id, id, true); if (one == null) Web.Error(res, 404, "not_found"); else Web.Json(res, 200, one); }
                        else Web.Json(res, 200, new Dictionary<string, object> { { "items", List(c, s.Id) } });
                        return;
                    }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!Web.Allow("kn:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "تعديلات كتير، استنى شوية."); return; }

                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                    if ((action == "shopify-connect" || action == "shopify-sync" || action == "shopify-admin-connect") && !PlanGate.Allows(c, s, "shopify")) { PlanGate.Deny(res, c, s, "shopify"); return; }
                    if ((action == "shopify-connect" || action == "shopify-admin-connect") && PlanGate.BothBlocked(c, s, "shopify")) { PlanGate.DenyBoth(res, c, s, "shopify"); return; }   // EasyOrders already linked

                    // ---- Shopify: connect (and sync now), sync again, disconnect ----
                    if (action == "shopify-connect" || action == "shopify-sync")
                    {
                        if (!Web.Allow("shop:" + s.Id, 12, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "حدّثت كتير، استنى شوية وجرّب تاني."); return; }
                        string domain;
                        if (action == "shopify-connect")
                        {
                            var body = Web.Body(req, 2000);
                            domain = Shopify.NormalizeDomain(Web.Str(body, "domain", 300));
                            if (domain == null) { Web.Error(res, 400, "bad_domain", "اكتب دومين متجرك صح، مثلًا my-shop.myshopify.com أو www.mystore.com"); return; }
                        }
                        else
                        {
                            domain = s.ShopifyDomain;
                            if (string.IsNullOrEmpty(domain)) { Web.Error(res, 400, "not_connected", "اربط متجرك على Shopify الأول."); return; }
                        }
                        var early = Storage.CheckRoom(c, 0);
                        if (early != null) { Web.Error(res, 507, "storage_full", early); return; }
                        Shopify.SyncResult r;
                        var adm = action == "shopify-sync" ? ShopifyAdmin.For(s) : null;   // full connection -> Admin API
                        try { using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(4))) r = adm != null ? await ShopifyAdmin.FetchCatalog(adm, cts.Token) : await Shopify.Fetch(domain, cts.Token); }
                        catch (ShopifyException ex) { Web.Error(res, 400, "shopify_failed", ex.Message); return; }
                        catch (Exception ex) { Log.Error(ex); Web.Error(res, 502, "shopify_failed", "مقدرناش نوصل لمتجرك على Shopify دلوقتي. اتأكد من الدومين وجرّب تاني."); return; }
                        var saveErr = Shopify.Save(c, s.Id, r);
                        if (saveErr != null) { Web.Error(res, 400, "too_much", saveErr); return; }
                        Web.Json(res, 200, new Dictionary<string, object> { { "domain", r.Domain }, { "products", r.Products }, { "policies", r.Policies }, { "shopName", r.ShopName ?? "" }, { "chars", r.Text.Length } });
                        return;
                    }
                    // Full connection: the owner's own Shopify app (Client ID + secret) -> Admin API.
                    if (action == "shopify-admin-connect")
                    {
                        if (!Web.Allow("shopadm:" + s.Id, 10, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                        var body = Web.Body(req, 4000);
                        string shop = ShopifyAdmin.NormalizeShop(Web.Str(body, "shop", 300));
                        string clientId = (Web.Str(body, "clientId", 200) ?? "").Trim();
                        string secret = (Web.Str(body, "secret", 300) ?? "").Trim();
                        if (shop == null) { Web.Error(res, 400, "bad_shop", "اكتب دومين متجرك اللي آخره .myshopify.com (تلاقيه في Shopify: Settings ← Domains)."); return; }
                        if (!System.Text.RegularExpressions.Regex.IsMatch(clientId, "^[A-Za-z0-9_-]{8,100}$")) { Web.Error(res, 400, "bad_client", "الـ Client ID مش مظبوط. انسخه زي ما هو من إعدادات التطبيق."); return; }
                        if (secret.Length < 10 || secret.Length > 300 || secret.Contains(" ")) { Web.Error(res, 400, "bad_secret", "الـ Client secret مش مظبوط. انسخه زي ما هو من إعدادات التطبيق."); return; }
                        var early = Storage.CheckRoom(c, 0);
                        if (early != null) { Web.Error(res, 507, "storage_full", early); return; }

                        var creds = new ShopifyAdmin.Creds { StoreId = s.Id, Shop = shop, ClientId = clientId, Secret = secret, Slug = s.Slug };
                        ShopifyAdmin.Forget(creds);
                        ShopifyAdmin.TokenInfo tok;
                        Shopify.SyncResult r;
                        try
                        {
                            tok = await ShopifyAdmin.Token(creds, true);
                            using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(4))) r = await ShopifyAdmin.FetchCatalog(creds, cts.Token);
                        }
                        catch (ShopifyException ex) { Web.Error(res, 400, "shopify_failed", ex.Message); return; }
                        catch (Exception ex) { Log.Error(ex); Web.Error(res, 502, "shopify_failed", "مقدرناش نوصل لـ Shopify دلوقتي. جرّب تاني كمان شوية."); return; }

                        bool orders = ShopifyAdmin.Has(tok.Scopes, "read_orders");
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ShopifyShop=@sh, ShopifyClientId=@ci, ShopifySecret=@se, ShopifyScopes=@sc, ShopifyOrders=@o, ShopifyWebhooks=0 WHERE Id=@s",
                            "@sh", shop, "@ci", clientId, "@se", Secrets.Protect(secret), "@sc", tok.Scopes, "@o", orders, "@s", s.Id)) cmd.ExecuteNonQuery();
                        var saveErr = Shopify.Save(c, s.Id, r);
                        if (saveErr != null) { Web.Error(res, 400, "too_much", saveErr); return; }

                        // instant updates: Shopify calls us when a product or its stock changes (needs our site to be reachable over https)
                        string siteBase = "https://" + req.Url.Host + req.ApplicationPath;
                        int hooks = await ShopifyAdmin.RegisterWebhooks(creds, siteBase, tok.Scopes);
                        bool ocHooked = s.OcOn && ShopifyAdmin.Has(tok.Scopes, "read_orders") && await ShopifyAdmin.RegisterOrderWebhook(creds, siteBase);   // re-connecting keeps order confirmation going
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ShopifyWebhooks=@w, OcHooked=@oh WHERE Id=@s", "@w", hooks, "@oh", ocHooked, "@s", s.Id)) cmd.ExecuteNonQuery();

                        var missing = new List<string>();
                        foreach (var w in ShopifyAdmin.Wanted) if (!ShopifyAdmin.Has(tok.Scopes, w)) missing.Add(w);
                        Web.Json(res, 200, new Dictionary<string, object> {
                            { "shop", shop }, { "products", r.Products }, { "policies", r.Policies }, { "shopName", r.ShopName ?? "" },
                            { "orders", orders }, { "webhooks", hooks }, { "missing", missing } });
                        return;
                    }
                    if (action == "shopify-orders")
                    {
                        var body = Web.Body(req, 500);
                        object en; bool on = body.TryGetValue("enabled", out en) && en is bool && (bool)en;
                        if (on && !ShopifyAdmin.Has(s.ShopifyScopes, "read_orders")) { Web.Error(res, 400, "no_scope", "التطبيق ناقصه صلاحية read_orders. ضيفها في Scopes واعمل Release للنسخة ووافق عليها، وبعدين اربط تاني."); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ShopifyOrders=@o WHERE Id=@s", "@o", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Db.BumpVersion(c, s.Id);   // the assistant's instructions change with it
                        Web.Json(res, 200, new { ok = true, orders = on });
                        return;
                    }
                    // Order confirmation on WhatsApp: on/off and which orders (all, or cash on delivery only)
                    if (action == "order-confirm")
                    {
                        var body = Web.Body(req, 500);
                        object en; bool on = body.TryGetValue("enabled", out en) && en is bool && (bool)en;
                        string scope = Web.Str(body, "scope", 10) == "cod" ? "cod" : "all";
                        var creds = ShopifyAdmin.For(s);
                        bool hooked = s.OcHooked;
                        if (on)
                        {
                            if (!PlanGate.Allows(c, s, "shopify")) { PlanGate.Deny(res, c, s, "shopify"); return; }
                            if (creds == null) { Web.Error(res, 400, "no_shopify", "اعمل ربط كامل لمتجرك على Shopify الأول (بالـ Client ID والـ secret)."); return; }
                            if (!ShopifyAdmin.Has(s.ShopifyScopes, "read_orders")) { Web.Error(res, 400, "no_scope", "التطبيق ناقصه صلاحية read_orders. ضيفها في Scopes واعمل Release للنسخة ووافق عليها، وبعدين اربط تاني."); return; }
                            if (string.IsNullOrEmpty(s.WaInstance) || string.IsNullOrEmpty(s.WaNumber)) { Web.Error(res, 400, "no_whatsapp", "اربط واتساب المتجر الأول من صفحة الواتساب، عشان الرسايل تتبعت من رقمك."); return; }
                            if (!hooked)
                            {
                                if (!Web.Allow("oc-hook:" + s.Id, 10, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                                hooked = await ShopifyAdmin.RegisterOrderWebhook(creds, "https://" + req.Url.Host + req.ApplicationPath);
                                if (!hooked) { Web.Error(res, 502, "hook_failed", "Shopify مارضيتش تبعتلنا الأوردرات الجديدة. اتأكد إن التطبيق فيه صلاحية read_orders واربط تاني، ولو فضلت كلّم الدعم."); return; }
                            }
                        }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET OcOn=@o, OcScope=@sc, OcHooked=@h WHERE Id=@s", "@o", on, "@sc", scope, "@h", hooked, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, new { ok = true, enabled = on, scope, canCancel = ShopifyAdmin.Has(s.ShopifyScopes, OrderConfirm.Scope) });
                        return;
                    }
                    if (action == "shopify-disconnect")
                    {
                        ShopifyAdmin.Forget(ShopifyAdmin.For(s));
                        Shopify.Disconnect(c, s.Id);
                        Web.Json(res, 200, new { ok = true });
                        return;
                    }

                    // Suggested questions written by Claude from the store's data (not saved: the owner reviews them, then saves).
                    if (action == "suggest")
                    {
                        if (!Web.Allow("sg:" + s.Id, 15, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                        var idx = Search.For(c, s);
                        if (idx.TotalChars < 40) { Web.Error(res, 400, "no_data", "ضيف داتا المتجر الأول من تاب \"داتا المتجر\"، وبعدين نقترح أسئلة منها."); return; }
                        var sr = await Claude.SuggestQuestions(s, idx);
                        Db.AddUsage(c, s.Id, 0, sr.In, sr.CacheRead, sr.CacheWrite, sr.Out);
                        if (sr.Items.Count == 0) { Web.Error(res, 502, "no_suggestions", "مقدرناش نطلع أسئلة دلوقتي، جرّب تاني."); return; }
                        Web.Json(res, 200, new Dictionary<string, object> { { "suggestions", sr.Items } });
                        return;
                    }

                    if (action == "upload")
                    {
                        var file = req.Files.Count > 0 ? req.Files[0] : null;
                        if (file == null || file.ContentLength == 0) { Web.Error(res, 400, "no_file", "اختار ملف."); return; }
                        if (file.ContentLength > MaxFileBytes) { Web.Error(res, 400, "file_too_big", "الملف أكبر من 10 ميجا."); return; }
                        string fileName = Path.GetFileName(file.FileName);
                        string type = Extract.TypeOf(fileName);
                        if (type == null) { Web.Error(res, 400, "bad_type", "نوع الملف مش مدعوم. المسموح: txt, md, csv, pdf, docx, xlsx"); return; }
                        if (PlanGate.LockedFileTypes.Contains(type) && !PlanGate.Allows(c, s, "files")) { PlanGate.Deny(res, c, s, "files"); return; }
                        byte[] data;
                        using (var ms = new MemoryStream()) { file.InputStream.CopyTo(ms); data = ms.ToArray(); }
                        // stop early if the database is already full (before spending time or Claude tokens reading the file)
                        var early = Storage.CheckRoom(c, 0);
                        if (early != null) { Web.Error(res, 507, "storage_full", early); return; }

                        string text;
                        try
                        {
                            if (type == "txt") text = Extract.PlainText(data);
                            else if (type == "docx") text = Extract.Word(data);
                            else if (type == "xlsx") text = Extract.Excel(data);
                            else
                            {
                                var r = await Claude.PdfToText(data);
                                text = r.Text;
                                Db.AddUsage(c, s.Id, 0, r.In, r.CacheRead, r.CacheWrite, r.Out);
                            }
                        }
                        catch (Exception ex) { Log.Error(ex); Web.Error(res, 400, "unreadable", "مقدرناش نقرا الملف ده. اتأكد إنه مش تالف أو محمي بباسورد."); return; }

                        if (string.IsNullOrWhiteSpace(text)) { Web.Error(res, 400, "empty", "الملف مافيهوش نص نقدر نقراه."); return; }
                        string title = Web.Str(new Dictionary<string, object> { { "t", req.Form["title"] } }, "t", 200);
                        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(fileName);
                        var err = CheckSize(c, s.Id, 0, text);
                        if (err != null) { Web.Error(res, 400, "too_much", err); return; }
                        var room = Storage.CheckRoom(c, text.Length);
                        if (room != null) { Web.Error(res, 507, "storage_full", room); return; }
                        int id = Insert(c, s.Id, title, type, fileName, text);
                        Web.Json(res, 200, Item(c, s.Id, id, true));
                        return;
                    }

                    var b = Web.Body(req, MaxItemChars * 3 + 5000);
                    if (action == "add")
                    {
                        string title = Web.Str(b, "title", 200);
                        string content = Web.Str(b, "content", MaxItemChars);
                        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) { Web.Error(res, 400, "empty", "اكتب عنوان ومحتوى."); return; }
                        var err = CheckSize(c, s.Id, 0, content);
                        if (err != null) { Web.Error(res, 400, "too_much", err); return; }
                        var room = Storage.CheckRoom(c, content.Length);
                        if (room != null) { Web.Error(res, 507, "storage_full", room); return; }
                        int id = Insert(c, s.Id, title, "text", null, content);
                        Web.Json(res, 200, Item(c, s.Id, id, true));
                        return;
                    }
                    if (action == "update")
                    {
                        var id = Web.Int(b, "id");
                        string title = Web.Str(b, "title", 200);
                        string content = Web.Str(b, "content", MaxItemChars);
                        if (id == null || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) { Web.Error(res, 400, "empty", "اكتب عنوان ومحتوى."); return; }
                        if (IsShopifyItem(c, s.Id, id.Value)) { Web.Error(res, 400, "shopify_item", ShopifyItemMessage); return; }
                        var err = CheckSize(c, s.Id, id.Value, content);
                        if (err != null) { Web.Error(res, 400, "too_much", err); return; }
                        var room = Storage.CheckRoom(c, content.Length);
                        if (room != null) { Web.Error(res, 507, "storage_full", room); return; }
                        int n;
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.KnowledgeItems SET Title=@t, Content=@c, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id AND StoreId=@s",
                            "@t", title, "@c", content, "@id", id.Value, "@s", s.Id)) n = cmd.ExecuteNonQuery();
                        if (n == 0) { Web.Error(res, 404, "not_found"); return; }
                        Db.BumpVersion(c, s.Id);
                        Web.Json(res, 200, Item(c, s.Id, id.Value, true));
                        return;
                    }
                    if (action == "delete")
                    {
                        var id = Web.Int(b, "id");
                        if (id == null) { Web.Error(res, 400, "bad_request"); return; }
                        if (IsShopifyItem(c, s.Id, id.Value)) { Web.Error(res, 400, "shopify_item", ShopifyItemMessage); return; }
                        using (var cmd = Db.Cmd(c, "DELETE FROM dbo.KnowledgeItems WHERE Id=@id AND StoreId=@s", "@id", id.Value, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Db.BumpVersion(c, s.Id);
                        Web.Json(res, 200, new { ok = true });
                        return;
                    }
                    Web.Error(res, 404, "unknown_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        const string ShopifyItemMessage = "الجزء ده بيتحدث تلقائيًا من متجرك (Shopify أو EasyOrders)، فمش بيتعدّل من هنا. عدّل المنتج من هناك ودوس \"حدّث دلوقتي\"، ولو مش عايزه دوس \"إلغاء الربط\".";
        static bool IsShopifyItem(SqlConnection c, int storeId, int id)
        {
            // synced parts (Shopify, EasyOrders) are rewritten by the next sync, so they aren't edited or deleted by hand
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.KnowledgeItems WHERE Id=@id AND StoreId=@s AND SourceType IN (@t, @t2)", "@id", id, "@s", storeId, "@t", Shopify.SourceType, "@t2", EasyOrders.SourceType))
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        internal static string CheckSize(SqlConnection c, int storeId, int exceptId, string content)
        {
            if (content.Length > MaxItemChars) return "الجزء ده أكبر من " + MaxItemChars.ToString("N0") + " حرف. قسّمه لأكتر من جزء.";
            long others;
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CAST(LEN(Content) AS BIGINT)),0) FROM dbo.KnowledgeItems WHERE StoreId=@s AND Id<>@id", "@s", storeId, "@id", exceptId))
                others = Convert.ToInt64(cmd.ExecuteScalar());
            if (others + content.Length > MaxStoreChars)
                return "كده الداتا هتعدّي الحد (" + MaxStoreChars.ToString("N0") + " حرف للمتجر). امسح أو اختصر حاجات قديمة الأول.";
            return null;
        }

        internal static int Insert(SqlConnection c, int storeId, string title, string type, string fileName, string content)
        {
            int id;
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.KnowledgeItems (StoreId, Title, SourceType, FileName, Content) OUTPUT INSERTED.Id VALUES (@s,@t,@ty,@f,@c)",
                "@s", storeId, "@t", title, "@ty", type, "@f", fileName, "@c", content))
                id = (int)cmd.ExecuteScalar();
            Db.BumpVersion(c, storeId);
            return id;
        }

        static List<Dictionary<string, object>> List(SqlConnection c, int storeId)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT Id, Title, SourceType, FileName, LEN(Content) AS Chars, UpdatedAt FROM dbo.KnowledgeItems WHERE StoreId=@s ORDER BY Id DESC", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object>
                    {
                        { "id", r["Id"] }, { "title", r["Title"] }, { "type", r["SourceType"] }, { "fileName", r["FileName"] as string },
                        { "chars", r["Chars"] }, { "updated", ((DateTime)r["UpdatedAt"]).ToString("yyyy-MM-dd HH:mm") },
                    });
            return list;
        }

        static Dictionary<string, object> Item(SqlConnection c, int storeId, int id, bool withContent)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.KnowledgeItems WHERE Id=@id AND StoreId=@s", "@id", id, "@s", storeId))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                var d = new Dictionary<string, object>
                {
                    { "id", r["Id"] }, { "title", r["Title"] }, { "type", r["SourceType"] }, { "fileName", r["FileName"] as string },
                    { "chars", ((string)r["Content"]).Length }, { "updated", ((DateTime)r["UpdatedAt"]).ToString("yyyy-MM-dd HH:mm") },
                };
                if (withContent) d["content"] = r["Content"];
                return d;
            }
        }
    }

    public static class Log
    {
        public static void Error(Exception ex)
        {
            try
            {
                var path = System.Web.Hosting.HostingEnvironment.MapPath("~/App_Data/errors.log");
                lock (typeof(Log)) File.AppendAllText(path, DateTime.UtcNow.ToString("o") + "\t" + ex.GetType().Name + "\t" + ex.Message + Environment.NewLine);
            }
            catch { }
        }
    }
}
