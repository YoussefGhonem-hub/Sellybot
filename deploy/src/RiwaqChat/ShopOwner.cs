// api/shop.ashx: the owner's side of the store («متجري» in the dashboard, site/shop-admin.js). GET ?a=... reads, POST {a: ...} writes.
// The owner's own store only (Me.Require); nothing here exists for a store that may not have one (Shops.Visible).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public class ShopOwnerHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                StoreInfo s; Func<Task> later = null;
                using (var c = Db.Open())
                {
                    s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (!Shops.Visible(c, s)) { Web.Error(res, 403, "not_open", "المتاجر لسه مافتحتش."); return; }
                    var sh = Shops.Create(c, s);
                    if (req.HttpMethod == "GET")
                    {
                        if (!Web.Allow("shop-get:" + s.Id, 3000, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "طلبات كتير، استنى شوية."); return; }
                        ShopOwner.Get(c, s, sh, req, res);
                        return;
                    }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!Web.Allow("shop-post:" + s.Id, 1500, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "تعديلات كتير، استنى شوية وجرّب تاني."); return; }
                    if (req.Files.Count > 0) { ShopExcel.Import(c, s, sh, req, res); return; }
                    var b = Web.Body(req, 3600000);
                    later = ShopOwner.Post(c, s, sh, b, res);
                }
                if (later != null) await later();   // work that takes seconds (Claude), with no connection held
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { Web.Json(res, 400, new Dictionary<string, object> { { "error", ex.Code }, { "message", ex.Message }, { "data", ex.Extra } }); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }
    }

    public static class ShopOwner
    {
        static string Str(Dictionary<string, object> b, string k, int max = 200) { return ShopJson.Str(ShopJson.At(b, k), max); }
        static int Int(Dictionary<string, object> b, string k, int d = 0) { return ShopJson.Int(ShopJson.At(b, k), d); }
        static bool Bool(Dictionary<string, object> b, string k, bool d = false) { return ShopJson.Bool(ShopJson.At(b, k), d); }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        static string Iso(DateTime? t) { return t.HasValue ? t.Value.ToString("o") : ""; }
        static void Ok(HttpResponse res, params object[] kv)
        {
            var d = new Dictionary<string, object> { { "ok", true } };
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            Web.Json(res, 200, d);
        }
        static void Exec(SqlConnection c, SqlTransaction tx, string sql, params object[] args) { using (var cmd = Db.Cmd(c, sql, args)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } }
        static int Scalar(SqlConnection c, string sql, params object[] args) { using (var cmd = Db.Cmd(c, sql, args)) return Convert.ToInt32(cmd.ExecuteScalar()); }

        // ================= reading =================
        public static void Get(SqlConnection c, StoreInfo s, Shop sh, HttpRequest req, HttpResponse res)
        {
            string a = req.QueryString["a"] ?? "state"; int id; int.TryParse(req.QueryString["id"], out id);
            switch (a)
            {
                case "state": Web.Json(res, 200, State(c, s, sh)); return;
                case "products": Web.Json(res, 200, Products(c, sh, req)); return;
                case "product": { var p = Product(c, sh, id); if (p == null) Web.Error(res, 404, "not_found", "المنتج ده مش موجود."); else Web.Json(res, 200, p); return; }
                case "cat": { var k = CatRows(c, sh, id); if (k == null) Web.Error(res, 404, "not_found", "القسم ده مش موجود."); else Web.Json(res, 200, k); return; }
                case "orders": Web.Json(res, 200, Orders(c, sh, req)); return;
                case "order": { var o = Order(c, s, sh, id); if (o == null) Web.Error(res, 404, "not_found", "الأوردر ده مش موجود."); else Web.Json(res, 200, o); return; }
                case "home": Web.Json(res, 200, Home(c, s, sh)); return;
                case "stock": Web.Json(res, 200, StockLog(c, sh, id)); return;
                case "offers": Web.Json(res, 200, ShopPromo.List(c, sh)); return;
                case "offer-result": Web.Json(res, 200, ShopPromo.Result(c, sh, id, req.QueryString["kind"])); return;
                case "versions": Web.Json(res, 200, Versions(c, sh)); return;
                case "bot": Web.Json(res, 200, ShopBot.Knows(c, s, sh)); return;
                case "export": ShopExcel.Export(c, sh, req, res); return;
                case "geo": Web.Json(res, 200, ShopGeo.Json()); return;
                default: if (!ShopDesk.Get(c, s, sh, a, id, req, res)) Web.Error(res, 404, "not_found"); return;
            }
        }

        public static Dictionary<string, object> State(SqlConnection c, StoreInfo s, Shop sh)
        {
            var d = ShopCache.Get(c, sh); var until = Shops.Until(c, s, sh);
            int newOrders, orders; using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CASE WHEN Status IN ('new','noreply') THEN 1 ELSE 0 END),0), COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) { r.Read(); newOrders = (int)r[0]; orders = (int)r[1]; }
            return new Dictionary<string, object>
            {
                { "slug", sh.Slug }, { "name", sh.Name }, { "nameEn", sh.NameEn ?? "" }, { "biz", sh.Biz ?? "" }, { "status", sh.Status }, { "url", sh.Url }, { "domain", Shops.Domain }, { "img", ShopImages.Url("x").TrimEnd('x') },
                { "reviewNote", sh.ReviewNote ?? "" }, { "closeNote", sh.CloseNote ?? "" }, { "pendSlug", sh.PendSlug ?? "" }, { "pendName", sh.PendName ?? "" }, { "reviewChange", sh.ReviewChange },
                { "submittedAt", Iso(sh.SubmittedAt) }, { "publishedAt", Iso(sh.PublishedAt) }, { "dirty", sh.DraftAt.HasValue }, { "everLive", sh.LiveJson != null },
                { "service", ShopRent.Json(c, s, sh) }, { "open", Shops.Open(c, s, sh) }, { "until", Iso(until) },
                { "draft", sh.Look(true) }, { "settings", sh.Settings }, { "templates", ShopTemplates.List(c) }, { "bizzes", ShopLook.Bizzes },
                { "cats", d.Cats.Select(k => CatJson(k)).ToArray() }, { "pages", d.Pages.Select(PageJson).ToArray() }, { "zones", d.Zones.Values.Select(z => new Dictionary<string, object> { { "g", z.Gov }, { "f", z.Fee.HasValue ? (object)z.Fee.Value : null }, { "d", z.Days ?? "" }, { "m", z.MinSub.HasValue ? (object)z.MinSub.Value : null }, { "off", z.Off } }).ToArray() },
                { "counts", new Dictionary<string, object> { { "products", d.Products.Count }, { "shown", d.Shown.Count() }, { "out", d.Products.Count(p => p.Status == "active" && !p.Available) }, { "orders", orders }, { "newOrders", newOrders }, { "offers", d.Offers.Count(o => o.Running(DateTime.UtcNow)) } } },
                { "waiting", ShopDesk.Waiting(c, sh.StoreId) }, { "carriers", Carriers.StateJson(c, s) }, { "plans", new Dictionary<string, object> { { "carts", PlanGate.Allows(c, s, Carts.Feature) }, { "restock", PlanGate.Allows(c, s, Restock.Feature) } } },
                { "noEn", ShopTranslate.Missing(d) }, { "leads", Scalar(c, "SELECT COUNT(*) FROM dbo.ShopLeads WHERE StoreId=@s", "@s", sh.StoreId) },
                { "limits", new Dictionary<string, object> { { "mb", Scalar(c, "SELECT ISNULL(SUM(CAST(Bytes AS BIGINT)),0)/1048576 FROM dbo.ShopImages WHERE StoreId=@s", "@s", sh.StoreId) }, { "maxMb", ShopImages.MaxStoreBytes / 1048576 }, { "products", Shops.MaxProducts }, { "images", Shops.MaxImages }, { "cats", Shops.MaxCategories }, { "variants", Shops.MaxVariants } } },
                { "steps", Steps(c, s, sh, d) }, { "wa", new Dictionary<string, object> { { "linked", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber) }, { "service", WaAccount.HasService(s) }, { "confirm", ShopHooks.ConfirmOn(c, s) } } }
            };
        }

        // «خطوات تجهيز المتجر»: what is left until it can be sent for review. must = the store can't be sent without it.
        public static List<Dictionary<string, object>> Steps(SqlConnection c, StoreInfo s, Shop sh, ShopData d)
        {
            var look = sh.Look(true); var set = sh.Settings; var contact = ShopJson.Sub(set, "contact"); var legal = ShopJson.Sub(set, "legal");
            var l = new List<Dictionary<string, object>>();
            Action<string, string, bool, bool, string> add = (k, t, done, must, tab) => l.Add(new Dictionary<string, object> { { "k", k }, { "t", t }, { "done", done }, { "must", must }, { "tab", tab } });
            add("biz", "اختار نشاط متجرك وقالبه", !string.IsNullOrEmpty(sh.Biz), true, "look");
            add("logo", "ارفع لوجو المتجر", ShopJson.Str(ShopJson.At(look, "logo")).Length > 0, false, "look");
            add("cats", "اعمل قسم واحد على الأقل", d.Cats.Count > 0, false, "cats");
            add("products", "ضيف 3 منتجات بصورهم وأسعارهم", d.Products.Count(p => p.Status == "active" && p.Pics.Count > 0) >= (ShopJson.Str(ShopJson.At(look, "template")) == "hammal" ? 1 : 3), true, "products");
            add("ship", "راجع سعر الشحن", ShopJson.Bool(ShopJson.At(set, "shipSeen")) || d.Zones.Count > 0, true, "ship");
            add("contact", "اكتب رقم موبايل أو واتساب للتواصل", ShopJson.Str(ShopJson.At(contact, "phone")).Length > 0 || ShopJson.Str(ShopJson.At(contact, "wa")).Length > 0, true, "info");
            add("legal", "اكتب بيانات البايع (القانون بيطلبها)", ShopJson.Str(ShopJson.At(legal, "seller")).Length > 1 && ShopJson.Str(ShopJson.At(legal, "address")).Length > 4 && ShopJson.Str(ShopJson.At(legal, "phone")).Length > 4, true, "info");
            add("social", "ضيف صفحتك على فيسبوك أو إنستجرام", ShopJson.Str(ShopJson.At(ShopJson.Sub(set, "social"), "fb")).Length > 0 || ShopJson.Str(ShopJson.At(ShopJson.Sub(set, "social"), "ig")).Length > 0, true, "info");
            add("returns", "راجع صفحة الاستبدال والاسترجاع", ShopJson.Bool(ShopJson.At(set, "returnsSeen")), true, "pages");
            add("wa", "اربط رقم واتساب المتجر (تأكيد الطلبات)", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber), false, "wa");
            add("rent", "فعّل إيجار المتجر من «باقتي»", Shops.HasService(c, s, sh), true, "plan");
            return l;
        }

        static Dictionary<string, object> CatJson(ShopCat k)
        {
            return new Dictionary<string, object> { { "id", k.Id }, { "parent", k.ParentId ?? 0 }, { "slug", k.Slug }, { "name", k.Name }, { "nameEn", k.NameEn ?? "" }, { "descr", k.Descr ?? "" }, { "descrEn", k.DescrEn ?? "" }, { "image", k.Image ?? "" }, { "cover", k.Cover ?? "" }, { "linkOnly", k.LinkOnly }, { "from", ShopPromo.Local(k.From) }, { "to", ShopPromo.Local(k.To) }, { "hidden", k.Hidden }, { "inMenu", k.InMenu }, { "inHome", k.InHome }, { "metaTitle", k.MetaTitle ?? "" }, { "metaDesc", k.MetaDesc ?? "" }, { "guide", string.IsNullOrEmpty(k.SizeGuideJson) ? null : ShopJson.Obj(k.SizeGuideJson) }, { "count", k.Count } };
        }
        static Dictionary<string, object> PageJson(ShopPage p) { return new Dictionary<string, object> { { "id", p.Id }, { "slug", p.Slug }, { "kind", p.Kind }, { "title", p.Title }, { "titleEn", p.TitleEn ?? "" }, { "body", p.Body ?? "" }, { "bodyEn", p.BodyEn ?? "" }, { "hidden", p.Hidden }, { "inMenu", p.InMenu }, { "inFooter", p.InFooter } }; }

        static Dictionary<string, object> Products(SqlConnection c, Shop sh, HttpRequest req)
        {
            var d = ShopCache.Get(c, sh); var now = DateTime.UtcNow;
            string q = (req.QueryString["q"] ?? "").Trim(), st = req.QueryString["st"] ?? "", sort = req.QueryString["sort"] ?? ""; int cat; int.TryParse(req.QueryString["cat"], out cat); int page; int.TryParse(req.QueryString["page"], out page); page = Math.Max(0, page);
            IEnumerable<ShopProd> l = d.Products;
            // the names and pictures of these products (an offer's, a section's)
            string ids = req.QueryString["ids"] ?? "";
            if (ids.Length > 0) { var want = new HashSet<int>(ids.Split(',').Take(500).Select(x => ShopJson.Int(x))); return new Dictionary<string, object> { { "list", l.Where(p => want.Contains(p.Id)).Select(p => ProdRow(d, p, now)).ToArray() } }; }
            if (q.Length > 0) { var f = d.Find(q.Length > 80 ? q.Substring(0, 80) : q, 300, true); var sku = d.Products.Where(p => string.Equals(p.Sku, q, StringComparison.OrdinalIgnoreCase) || p.Vars.Any(v => string.Equals(v.Sku, q, StringComparison.OrdinalIgnoreCase))); l = sku.Concat(f).Distinct(); }
            if (cat > 0) { var under = d.Under(cat); l = l.Where(p => p.Cats.Any(under.Contains)); } else if (cat == -1) l = l.Where(p => p.Cats.Count == 0);
            if (st == "hidden") l = l.Where(p => p.Status != "active"); else if (st == "out") l = l.Where(p => p.Status == "active" && !p.Available); else if (st == "low") l = l.Where(p => p.Track && p.Available && p.Total <= 3); else if (st == "nopic") l = l.Where(p => p.Pics.Count == 0); else if (st == "active") l = l.Where(p => p.Status == "active");
            if (sort == "price") l = l.OrderBy(p => p.Price); else if (sort == "-price") l = l.OrderByDescending(p => p.Price); else if (sort == "sold") l = l.OrderByDescending(p => p.Sold); else if (sort == "stock") l = l.OrderBy(p => p.Total); else if (sort == "name") l = l.OrderBy(p => p.Name);
            var all = l.ToList();
            return new Dictionary<string, object> { { "total", all.Count }, { "page", page }, { "size", 50 }, { "list", all.Skip(page * 50).Take(50).Select(p => ProdRow(d, p, now)).ToArray() } };
        }
        // a category's products in the owner's order, and which are pinned first
        static Dictionary<string, object> CatRows(SqlConnection c, Shop sh, int id)
        {
            var d = ShopCache.Get(c, sh); if (!d.CatById.ContainsKey(id)) return null;
            var pins = new HashSet<int>(); using (var cmd = Db.Cmd(c, "SELECT ProductId FROM dbo.ShopProductCategories WHERE StoreId=@s AND CategoryId=@i AND Pinned=1", "@s", sh.StoreId, "@i", id)) using (var r = cmd.ExecuteReader()) while (r.Read()) pins.Add((int)r[0]);
            List<int> order; if (!d.CatOrder.TryGetValue(id, out order)) order = new List<int>();
            return new Dictionary<string, object> { { "list", order.Where(x => d.ById.ContainsKey(x)).Select(x => d.ById[x]).Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "img", p.Pics.Count > 0 ? ShopImages.Url(p.Pics[0].Path, true) : "" }, { "pin", pins.Contains(p.Id) }, { "status", p.Status } }).ToArray() } };
        }
        static Dictionary<string, object> ProdRow(ShopData d, ShopProd p, DateTime now)
        {
            decimal? old; ShopOffer of; var u = ShopPrice.Unit(d, p, p.Vars.FirstOrDefault(v => v.Active), now, out old, out of);
            return new Dictionary<string, object> { { "id", p.Id }, { "slug", p.Slug }, { "name", p.Name }, { "price", p.Price }, { "now", u }, { "old", old ?? 0 }, { "offer", of == null ? "" : of.Name }, { "cost", p.Cost.HasValue ? (object)p.Cost.Value : null }, { "stock", p.Track ? (object)p.Total : null }, { "status", p.Status }, { "av", p.Available }, { "vars", p.Vars.Count(v => v.Active) }, { "img", p.Pics.Count > 0 ? ShopImages.Url(p.Pics[0].Path, true) : "" }, { "cats", p.Cats }, { "sold", p.Sold }, { "sku", p.Sku ?? "" }, { "out", p.OutMode }, { "feat", p.Feat } };
        }
        static Dictionary<string, object> Product(SqlConnection c, Shop sh, int id)
        {
            var d = ShopCache.Get(c, sh); ShopProd p; if (!d.ById.TryGetValue(id, out p)) return null;
            return new Dictionary<string, object>
            {
                { "id", p.Id }, { "slug", p.Slug }, { "name", p.Name }, { "nameEn", p.NameEn ?? "" }, { "descr", p.Descr ?? "" }, { "descrEn", p.DescrEn ?? "" }, { "price", p.Price }, { "old", p.OldPrice ?? 0 }, { "cost", p.Cost ?? 0 }, { "sku", p.Sku ?? "" }, { "unit", p.Unit ?? "" }, { "brand", p.Brand ?? "" }, { "tags", p.Tags ?? "" },
                { "min", p.MinQty }, { "max", p.MaxQty ?? 0 }, { "track", p.Track }, { "out", p.OutMode }, { "status", p.Status }, { "feat", p.Feat }, { "weight", p.Weight ?? 0 }, { "specs", ShopJson.Arr(p.SpecsJson) }, { "guide", string.IsNullOrEmpty(p.SizeGuideJson) ? null : ShopJson.Obj(p.SizeGuideJson) }, { "extra", ShopJson.Obj(p.ExtraJson) },
                { "metaTitle", p.MetaTitle ?? "" }, { "metaDesc", p.MetaDesc ?? "" }, { "cats", p.Cats }, { "main", p.MainCat }, { "sold", p.Sold }, { "url", sh.Url + "/p/" + p.Slug },
                { "options", p.Options.Select(o => new Dictionary<string, object> { { "n", new[] { o.Ar, o.En } }, { "v", o.Vals.Select(v => new[] { v.Ar, v.En, v.Hex }).ToArray() } }).ToArray() },
                { "vars", p.Vars.Select(v => new Dictionary<string, object> { { "id", v.Id }, { "o", v.Vals }, { "price", v.Price.HasValue ? (object)v.Price.Value : null }, { "old", v.OldPrice.HasValue ? (object)v.OldPrice.Value : null }, { "cost", v.Cost.HasValue ? (object)v.Cost.Value : null }, { "stock", v.Stock }, { "sku", v.Sku ?? "" }, { "image", v.Image ?? "" }, { "active", v.Active } }).ToArray() },
                { "images", Images(c, sh.StoreId, p.Id) }, { "ask", ShopJson.Arr(p.AskJson) }, { "goes", ShopView.GoesIds(p) }, { "video", p.Video ?? "" }, { "rating", p.Rating }, { "reviews", p.Reviews }
            };
        }
        static object[] Images(SqlConnection c, int storeId, int productId)
        {
            var l = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT Id, Path, W, H, Alt, Focus FROM dbo.ShopImages WHERE StoreId=@s AND ProductId=@p ORDER BY Sort, Id", "@s", storeId, "@p", productId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Dictionary<string, object> { { "id", (int)r[0] }, { "path", (string)r[1] }, { "url", ShopImages.Url((string)r[1]) }, { "small", ShopImages.Url((string)r[1], true) }, { "w", (int)r[2] }, { "h", (int)r[3] }, { "alt", r[4] as string ?? "" }, { "focus", r[5] as string ?? "" } });
            return l.ToArray();
        }

        // ---------- orders ----------
        static Dictionary<string, object> Orders(SqlConnection c, Shop sh, HttpRequest req)
        {
            string st = req.QueryString["st"] ?? "", q = (req.QueryString["q"] ?? "").Trim(), from = req.QueryString["from"] ?? "", to = req.QueryString["to"] ?? ""; int page; int.TryParse(req.QueryString["page"], out page); page = Math.Max(0, page); if (q.Length > 80) q = q.Substring(0, 80);
            string where = "StoreId=@s"; var args = new List<object> { "@s", sh.StoreId };
            if (ShopOrders.Statuses.Contains(st)) { where += " AND Status=@st"; args.Add("@st"); args.Add(st); } else if (st == "todo") where += " AND Status IN ('new','noreply')"; else if (st == "ship") where += " AND Status='confirmed'"; else if (st == "pay") where += " AND PayState='sent' AND Status NOT IN ('cancelled','returned')";
            DateTime dt;
            if (DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) { where += " AND CreatedAt>=@f"; args.Add("@f"); args.Add(dt.AddHours(-2)); }
            if (DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) { where += " AND CreatedAt<@t"; args.Add("@t"); args.Add(dt.AddDays(1).AddHours(-2)); }
            if (q.Length > 0)
            {
                var digits = Regex.Replace(ShopJson.Digits(q), @"\D", ""); int no; int.TryParse(digits.Length < 9 ? digits : "", out no);
                where += " AND (No=@no OR Name LIKE @q OR (@d<>'' AND Phone LIKE @dq) OR EXISTS (SELECT 1 FROM dbo.ShopOrderItems i WHERE i.OrderId=dbo.ShopOrders.Id AND i.Name LIKE @q))";
                args.AddRange(new object[] { "@no", no, "@q", "%" + q.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%", "@d", digits.Length >= 4 ? digits : "", "@dq", "%" + digits.TrimStart('0') + "%" });
            }
            int total; decimal sum; using (var cmd = Db.Cmd(c, "SELECT COUNT(*), ISNULL(SUM(Total),0) FROM dbo.ShopOrders WHERE " + where, args.ToArray())) using (var r = cmd.ExecuteReader()) { r.Read(); total = (int)r[0]; sum = (decimal)r[1]; }
            var list = new List<long>(); var rows = new List<ShopOrder>();
            args.Add("@o"); args.Add(page * 30);
            using (var cmd = Db.Cmd(c, "SELECT Id FROM dbo.ShopOrders WHERE " + where + " ORDER BY Id DESC OFFSET @o ROWS FETCH NEXT 30 ROWS ONLY", args.ToArray())) using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(Convert.ToInt64(r[0]));
            foreach (var id in list) rows.Add(ShopOrders.ById(c, sh.StoreId, id));
            var counts = new Dictionary<string, object>();
            using (var cmd = Db.Cmd(c, "SELECT Status, COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s GROUP BY Status", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) counts[(string)r[0]] = (int)r[1];
            return new Dictionary<string, object> { { "total", total }, { "sum", sum }, { "page", page }, { "size", 30 }, { "counts", counts }, { "list", rows.Select(o => OrderRow(c, o)).ToArray() } };
        }
        static Dictionary<string, object> OrderRow(SqlConnection c, ShopOrder o)
        {
            return new Dictionary<string, object> { { "id", o.Id }, { "no", o.No }, { "status", o.Status }, { "name", o.Name }, { "phone", ShopOrders.Local(o.Phone) }, { "gov", o.Gov }, { "govName", ShopGeo.Name(o.Gov) }, { "city", o.City ?? "" }, { "total", o.Total }, { "items", o.Items.Sum(i => i.Qty) }, { "first", o.Items.Count > 0 ? o.Items[0].Name + (string.IsNullOrEmpty(o.Items[0].Opts) ? "" : " (" + o.Items[0].Opts + ")") : "" }, { "img", o.Items.Count > 0 && !string.IsNullOrEmpty(o.Items[0].Image) ? ShopImages.Url(o.Items[0].Image, true) : "" },
                { "at", Iso(o.CreatedAt) }, { "statusAt", Iso(o.StatusAt) }, { "dup", o.Dup }, { "seen", o.Seen }, { "channel", o.Channel }, { "note", o.Note ?? "" }, { "staff", o.StaffNote ?? "" }, { "coupon", o.Coupon ?? "" }, { "tracking", o.Tracking ?? "" }, { "pay", o.Pay }, { "ps", o.PayState ?? "" }, { "pick", o.Extras.ContainsKey("branch") }, { "gift", (o.FeesJson ?? "").Contains("\"gift\"") } };
        }
        static Dictionary<string, object> Order(SqlConnection c, StoreInfo s, Shop sh, long id)
        {
            var o = ShopOrders.ById(c, sh.StoreId, id); if (o == null) return null;
            if (!o.Seen) Exec(c, null, "UPDATE dbo.ShopOrders SET Seen=1 WHERE Id=@i", "@i", id);
            var d = OrderRow(c, o);
            foreach (var kv in new Dictionary<string, object> { { "phone2", ShopOrders.Local(o.Phone2) ?? "" }, { "area", o.Area ?? "" }, { "address", o.Address }, { "landmark", o.Landmark ?? "" }, { "sub", o.Subtotal }, { "off", o.Discount }, { "ship", o.Ship }, { "offers", ShopJson.Arr(o.OffersJson) }, { "reason", o.CancelReason ?? "" }, { "carrier", o.Carrier ?? "" }, { "restocked", o.Restocked }, { "src", o.Src ?? "" }, { "from", ShopDesk.Source(o.Src) }, { "lang", o.Lang }, { "addressText", ShopOrders.AddressText(o) }, { "fees", ShopJson.Arr(o.FeesJson) }, { "pay", o.Pay }, { "payName", ShopPay.WayName(o.Pay) }, { "ps", o.PayState ?? "" }, { "dep", o.Deposit }, { "paid", o.Paid }, { "due", o.Due }, { "proof", string.IsNullOrEmpty(o.Proof) ? "" : ShopImages.Url(o.Proof) }, { "ex", o.Extras }, { "refund", o.Refund }, { "returns", ShopReturns.OfOrder(c, o.Id) }, { "token", o.Token },
                { "lines", o.Items.Select(i => new Dictionary<string, object> { { "i", i.Id }, { "back", i.Returned }, { "v", i.VariantId }, { "p", i.ProductId }, { "name", i.Name }, { "opts", i.Opts ?? "" }, { "sku", i.Sku ?? "" }, { "q", i.Qty }, { "price", i.Price }, { "full", i.Full }, { "off", i.Discount }, { "sum", i.Sum }, { "x", i.Extra ?? "" }, { "img", string.IsNullOrEmpty(i.Image) ? "" : ShopImages.Url(i.Image, true) } }).ToArray() } }) d[kv.Key] = kv.Value;
            var log = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT Who, What, At FROM dbo.ShopOrderLog WHERE OrderId=@o AND StoreId=@s ORDER BY Id DESC", "@o", id, "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) log.Add(new Dictionary<string, object> { { "who", (string)r[0] }, { "what", (string)r[1] }, { "at", Iso(DateTime.SpecifyKind((DateTime)r[2], DateTimeKind.Utc)) } });
            d["log"] = log;
            var h = Crm.Of(c, sh.StoreId, o.Phone); d["risk"] = PlanGate.Allows(c, s, "returns") && h != null ? Crm.RiskLine(c, s, o.Phone) ?? "" : "";
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Phone=@p AND Id<>@i", "@s", sh.StoreId, "@p", o.Phone, "@i", id)) d["before"] = (int)cmd.ExecuteScalar();
            d["confirm"] = ShopHooks.ConfirmOf(c, sh.StoreId, id); d["parcel"] = Carriers.OrderJson(c, s, o);
            return d;
        }

        // ---------- the store's home: today, this week, what needs the owner ----------
        static Dictionary<string, object> Home(SqlConnection c, StoreInfo s, Shop sh)
        {
            Func<int, Dictionary<string, object>> span = days =>
            {
                var d = new Dictionary<string, object>();
                using (var cmd = Db.Cmd(c, @"DECLARE @today DATE = CAST(DATEADD(HOUR,2,SYSUTCDATETIME()) AS DATE); DECLARE @from DATETIME2 = DATEADD(HOUR,-2,CAST(DATEADD(DAY,-@d+1,@today) AS DATETIME2));
                    SELECT COUNT(*), ISNULL(SUM(CASE WHEN Status NOT IN ('cancelled','returned') THEN Total ELSE 0 END),0), ISNULL(SUM(CASE WHEN Status='delivered' THEN Total ELSE 0 END),0), ISNULL(SUM(CASE WHEN Status='cancelled' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN Status='returned' THEN 1 ELSE 0 END),0) FROM dbo.ShopOrders WHERE StoreId=@s AND CreatedAt>=@from;
                    SELECT ISNULL(SUM(Visits),0), ISNULL(SUM(Visitors),0), ISNULL(SUM(Views),0), ISNULL(SUM(Carts),0), ISNULL(SUM(Checkouts),0), ISNULL(SUM(Orders),0) FROM dbo.ShopDaily WHERE StoreId=@s AND Day>DATEADD(DAY,-@d,@today)", "@s", sh.StoreId, "@d", days))
                using (var r = cmd.ExecuteReader())
                {
                    r.Read(); d["orders"] = (int)r[0]; d["sales"] = (decimal)r[1]; d["delivered"] = (decimal)r[2]; d["cancelled"] = (int)r[3]; d["returned"] = (int)r[4];
                    r.NextResult(); r.Read(); d["visits"] = (int)r[0]; d["visitors"] = (int)r[1]; d["views"] = (int)r[2]; d["carts"] = (int)r[3]; d["checkouts"] = (int)r[4]; d["webOrders"] = (int)r[5];
                }
                return d;
            };
            var days30 = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT Day, Visits, Orders FROM dbo.ShopDaily WHERE StoreId=@s AND Day>DATEADD(DAY,-30,CAST(DATEADD(HOUR,2,SYSUTCDATETIME()) AS DATE)) ORDER BY Day", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) days30.Add(new object[] { ((DateTime)r[0]).ToString("yyyy-MM-dd"), (int)r[1], (int)r[2] });
            var top = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 5 i.ProductId, MAX(i.Name), SUM(i.Qty), SUM(i.Price*i.Qty-i.Discount) FROM dbo.ShopOrderItems i JOIN dbo.ShopOrders o ON o.Id=i.OrderId WHERE i.StoreId=@s AND o.CreatedAt>DATEADD(DAY,-30,SYSUTCDATETIME()) AND o.Status NOT IN ('cancelled','returned') GROUP BY i.ProductId ORDER BY SUM(i.Qty) DESC", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) top.Add(new Dictionary<string, object> { { "id", (int)r[0] }, { "name", (string)r[1] }, { "n", (int)r[2] }, { "sum", (decimal)r[3] } });
            var data = ShopCache.Get(c, sh);
            return new Dictionary<string, object> { { "today", span(1) }, { "week", span(7) }, { "month", span(30) }, { "days", days30 }, { "top", top }, { "low", data.Products.Where(p => p.Status == "active" && p.Track && p.OutMode != "order" && p.Total <= 3).OrderBy(p => p.Total).Take(8).Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "stock", p.Total } }).ToArray() } };
        }

        static List<object> StockLog(SqlConnection c, Shop sh, int productId)
        {
            var l = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 100 k.Delta, k.After_, k.Reason, k.Note, k.At, v.Opts, o.No FROM dbo.ShopStock k LEFT JOIN dbo.ShopVariants v ON v.Id=k.VariantId LEFT JOIN dbo.ShopOrders o ON o.Id=k.OrderId WHERE k.StoreId=@s AND k.ProductId=@p ORDER BY k.Id DESC", "@s", sh.StoreId, "@p", productId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Dictionary<string, object> { { "d", (int)r[0] }, { "a", (int)r[1] }, { "r", (string)r[2] }, { "n", r[3] as string ?? "" }, { "at", Iso(DateTime.SpecifyKind((DateTime)r[4], DateTimeKind.Utc)) }, { "o", (r[5] as string ?? "").Replace("|", " / ") }, { "no", r[6] is int ? (int)r[6] : 0 } });
            return l;
        }
        static List<object> Versions(SqlConnection c, Shop sh)
        {
            var l = new List<object>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 10 Id, Note, CreatedAt, Look FROM dbo.ShopVersions WHERE StoreId=@s ORDER BY Id DESC", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Dictionary<string, object> { { "id", (int)r[0] }, { "note", r[1] as string ?? "" }, { "at", Iso(DateTime.SpecifyKind((DateTime)r[2], DateTimeKind.Utc)) }, { "tpl", ShopJson.Str(ShopJson.At(ShopJson.Obj(r[3] as string), "template")) } });
            return l;
        }

        // ================= writing =================
        public static Func<Task> Post(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            string a = Str(b, "a", 30); int id = Int(b, "id");
            switch (a)
            {
                case "info": SaveInfo(c, s, sh, b); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;
                case "settings": SaveSettings(c, sh, b); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;
                case "seen": { var set = sh.Settings; var k = Str(b, "k", 20); if (k == "shipSeen" || k == "returnsSeen") { set[k] = true; Shops.SaveSettings(c, sh, set); } Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null; }
                case "look": SaveLook(c, s, sh, b); Ok(res, "dirty", true); return null;
                case "look-reset": Exec(c, null, "UPDATE dbo.Shops SET Draft=ISNULL(Live, Draft), DraftAt=NULL WHERE StoreId=@s", "@s", s.Id); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;
                case "diff": Ok(res, "list", Diff(sh)); return null;
                case "publish": Publish(c, s, sh, Str(b, "note", 200)); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;
                case "submit": Submit(c, s, sh, res); return null;
                case "unsubmit": if (sh.Status == "review") Exec(c, null, "UPDATE dbo.Shops SET Status='draft' WHERE StoreId=@s", "@s", s.Id); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;
                case "restore": Restore(c, sh, id); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;
                case "preview": Preview(c, sh, Bool(b, "share"), res); return null;
                case "template": Template(c, s, sh, b); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;

                case "cat-save": { int nid = SaveCat(c, sh, b); Ok(res, "id", nid, "cats", Cats(c, sh)); return null; }
                case "cat-del": DelCat(c, sh, id); Ok(res, "cats", Cats(c, sh)); return null;
                case "cat-sort": SortCats(c, sh, b); Ok(res, "cats", Cats(c, sh)); return null;
                case "cat-ready": ReadyCats(c, sh, Str(b, "biz", 20)); Ok(res, "cats", Cats(c, sh)); return null;
                case "cat-products": CatProducts(c, sh, b); Ok(res, "cats", Cats(c, sh)); return null;

                case "prod-save": { int nid = SaveProduct(c, sh, b); Ok(res, "id", nid, "product", Product(c, sh, nid)); return null; }
                case "prod-del": DelProducts(c, sh, ShopJson.List(ShopJson.At(b, "ids")).Select(x => ShopJson.Int(x)).ToList()); Ok(res); return null;
                case "prod-dup": { int nid = Duplicate(c, sh, id); Ok(res, "id", nid); return null; }
                case "prod-bulk": Bulk(c, sh, b); Ok(res); return null;
                case "prod-sort": SortProducts(c, sh, b); Ok(res); return null;
                case "stock": SetStock(c, sh, b); Ok(res, "product", Product(c, sh, Int(b, "product"))); return null;

                case "image": Ok(res, "image", ShopImages.Save(c, s.Id, id > 0 ? CheckProduct(c, sh, id) : (int?)null, Str(b, "data", 1800000), Str(b, "small", 400000), Int(b, "w", 1), Int(b, "h", 1), Str(b, "alt", 160))); if (id > 0) Shops.Bump(c, s.Id); return null;
                case "image-del": ShopImages.Delete(c, s.Id, new[] { Str(b, "path", 160) }); Exec(c, null, "UPDATE dbo.ShopVariants SET Image=NULL WHERE StoreId=@s AND Image=@p", "@s", s.Id, "@p", Str(b, "path", 160)); Shops.Bump(c, s.Id); Ok(res); return null;
                case "image-sort": { int n = 0; foreach (var x in ShopJson.List(ShopJson.At(b, "paths")).Take(Shops.MaxImages)) Exec(c, null, "UPDATE dbo.ShopImages SET Sort=@n WHERE StoreId=@s AND ProductId=@p AND Path=@path", "@n", n++, "@s", s.Id, "@p", id, "@path", ShopJson.Str(x, 160)); Shops.Bump(c, s.Id); Ok(res); return null; }
                case "image-alt": Exec(c, null, "UPDATE dbo.ShopImages SET Alt=@a, Focus=@f WHERE StoreId=@s AND Path=@p", "@a", Nz(Str(b, "alt", 160)), "@f", Regex.IsMatch(Str(b, "focus", 9), @"^\d{1,3},\d{1,3}$") ? Str(b, "focus", 9) : null, "@s", s.Id, "@p", Str(b, "path", 160)); Shops.Bump(c, s.Id); Ok(res); return null;

                case "zones": SaveZones(c, sh, b); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;
                case "page-save": { int nid = SavePage(c, sh, b); Ok(res, "id", nid, "state", State(c, s, Shops.ByStore(c, s.Id))); return null; }
                case "page-del": Exec(c, null, "DELETE FROM dbo.ShopPages WHERE StoreId=@s AND Id=@i AND Kind='custom'", "@s", s.Id, "@i", id); Shops.Bump(c, s.Id); Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id))); return null;

                case "order-status":
                    {
                        var errors = new List<string>(); int done = 0;
                        foreach (var x in ShopJson.List(ShopJson.At(b, "ids")).Take(100)) { long oid = ShopJson.Int(x); var e = ShopOrders.SetStatus(c, s.Id, oid, Str(b, "to", 12), "owner", Str(b, "reason", 200), Bool(b, "restock", true)); if (e == null) done++; else { var o = ShopOrders.ById(c, s.Id, oid); errors.Add((o == null ? "" : o.NoText + ": ") + e); } }
                        Ok(res, "done", done, "errors", errors); return null;
                    }
                case "order-edit": { var e = ShopOrders.Edit(c, sh, id, b); if (e != null) throw new ShopException("edit", e); Ok(res, "order", Order(c, s, sh, id)); return null; }
                case "order-note": Exec(c, null, "UPDATE dbo.ShopOrders SET StaffNote=@n WHERE Id=@i AND StoreId=@s", "@n", Nz(Str(b, "note", 1000)), "@i", id, "@s", s.Id); Ok(res); return null;
                case "order-ship": Exec(c, null, "UPDATE dbo.ShopOrders SET Carrier=@c, Tracking=@t WHERE Id=@i AND StoreId=@s", "@c", Nz(Str(b, "carrier", 30)), "@t", Nz(Str(b, "tracking", 60)), "@i", id, "@s", s.Id); Ok(res, "order", Order(c, s, sh, id)); return null;

                case "offer-save": Ok(res, "id", ShopPromo.Save(c, sh, b), "offers", ShopPromo.List(c, sh)); return null;
                case "offer-check": Ok(res, "losses", ShopPromo.Check(c, sh, b)); return null;
                case "offer-del": Exec(c, null, "DELETE FROM dbo.ShopOffers WHERE StoreId=@s AND Id=@i", "@s", s.Id, "@i", id); Shops.Bump(c, s.Id); Ok(res, "offers", ShopPromo.List(c, sh)); return null;
                case "offer-on": Exec(c, null, "UPDATE dbo.ShopOffers SET Active=@a WHERE StoreId=@s AND Id=@i", "@a", Bool(b, "on"), "@s", s.Id, "@i", id); Shops.Bump(c, s.Id); Ok(res, "offers", ShopPromo.List(c, sh)); return null;
                case "coupon-save": Ok(res, "id", ShopPromo.SaveCoupon(c, sh, b), "offers", ShopPromo.List(c, sh)); return null;
                case "coupon-del": Exec(c, null, "DELETE FROM dbo.ShopCoupons WHERE StoreId=@s AND Id=@i", "@s", s.Id, "@i", id); Shops.Bump(c, s.Id); Ok(res, "offers", ShopPromo.List(c, sh)); return null;
                case "ad-copy": return ShopPromo.AdCopy(c, s, sh, b, res);
                case "bot-test": return ShopBot.Test(c, s, sh, res);
                case "translate": return ShopTranslate.Run(c, s, sh, b, res);
                case "translate-save": Ok(res, "done", ShopTranslate.Save(c, sh, b)); return null;
                default: { Func<Task> later; if (!ShopDesk.Post(c, s, sh, a, b, res, out later)) Web.Error(res, 404, "not_found"); return later; }
            }
        }

        static object[] Cats(SqlConnection c, Shop sh) { return ShopCache.Get(c, sh).Cats.Select(k => (object)CatJson(k)).ToArray(); }
        static int CheckProduct(SqlConnection c, Shop sh, int id) { if (Scalar(c, "SELECT COUNT(*) FROM dbo.ShopProducts WHERE StoreId=@s AND Id=@i", "@s", sh.StoreId, "@i", id) == 0) throw new ShopException("not_found", "المنتج ده مش موجود."); return id; }

        // ---------- the store's name, link, activity ----------
        static void SaveInfo(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            string name = b.ContainsKey("name") ? Str(b, "name", 80) : sh.Name, slug = b.ContainsKey("slug") ? Str(b, "slug", 30).ToLowerInvariant() : sh.Slug, en = b.ContainsKey("nameEn") ? Str(b, "nameEn", 80) : sh.NameEn;
            string biz = b.ContainsKey("biz") && ShopLook.Bizzes.Contains(Str(b, "biz", 20)) ? Str(b, "biz", 20) : sh.Biz;
            if (name.Length < 2) throw new ShopException("name", "اكتب اسم المتجر.");
            var word = ShopReview.BadName(name, s.IsAdmin); if (word != null) throw new ShopException("name", word);
            if (slug != sh.Slug && slug != (sh.PendSlug ?? ""))
            {
                var why = Shops.BadSlug(slug, s.IsAdmin); if (why != null) throw new ShopException("slug", why);
                if (!Shops.SlugFree(c, slug, s.Id)) throw new ShopException("slug", "الاسم ده متاخد. جرّب اسم تاني زي " + slug + "-store.");
            }
            bool live = sh.Status == "live";
            // a store customers already know: its name and link change after the admin agrees
            string pendSlug = live && slug != sh.Slug ? slug : null, pendName = live && name != sh.Name ? name : null;
            if (live) Exec(c, null, "UPDATE dbo.Shops SET NameEn=@en, Biz=@b, PendSlug=@ps, PendName=@pn, ReviewChange=CASE WHEN @ps IS NOT NULL OR @pn IS NOT NULL THEN 1 ELSE ReviewChange END WHERE StoreId=@s", "@en", Nz(en), "@b", Nz(biz), "@ps", pendSlug, "@pn", pendName, "@s", s.Id);
            else Exec(c, null, "UPDATE dbo.Shops SET Name=@n, NameEn=@en, Slug=@sl, Biz=@b WHERE StoreId=@s", "@n", name, "@en", Nz(en), "@sl", slug, "@b", Nz(biz), "@s", s.Id);
            Shops.Bump(c, s.Id);
        }

        static void SaveSettings(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            var x = ShopJson.Sub(b, "settings"); var old = sh.Settings; var set = ShopLook.CleanSettings(x, old);
            foreach (var k in new[] { "shipSeen", "returnsSeen" }) if (ShopJson.Bool(ShopJson.At(old, k))) set[k] = true;
            if (x.ContainsKey("ship")) set["shipSeen"] = true;
            Shops.SaveSettings(c, sh, set);
        }

        static void SaveLook(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            var look = ShopLook.Clean(ShopJson.Sub(b, "look"), s.Id); var json = ShopJson.Write(look);
            if (json.Length > 200000) throw new ShopException("too_big", "الشكل فيه كلام كتير. قلّل شوية.");
            Exec(c, null, "UPDATE dbo.Shops SET Draft=@d, DraftAt=SYSUTCDATETIME() WHERE StoreId=@s", "@d", json, "@s", s.Id);
        }

        static void Template(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            string t = Str(b, "t", 20), biz = Str(b, "biz", 20);
            var look = sh.Look(true); if (ShopLook.Templates.Contains(t) && ShopTemplates.IsOpen(c, t)) look["template"] = t;
            Exec(c, null, "UPDATE dbo.Shops SET Draft=@d, DraftAt=SYSUTCDATETIME(), Biz=ISNULL(@b, Biz) WHERE StoreId=@s", "@d", ShopJson.Write(ShopLook.Clean(look, s.Id)), "@b", ShopLook.Bizzes.Contains(biz) ? biz : null, "@s", s.Id);
            Shops.Bump(c, s.Id);
        }

        // what «انشر» will change, in the owner's words
        static List<string> Diff(Shop sh)
        {
            var a = sh.Look(false); var b = sh.Look(true); var l = new List<string>();
            var names = new Dictionary<string, string> { { "template", "القالب" }, { "langs", "لغة المتجر" }, { "first", "اللغة الأساسية" }, { "mode", "الوضع الفاتح والغامق" }, { "modeBtn", "زرار الوضع الغامق" }, { "color", "اللون الأساسي" }, { "font", "الخط" }, { "motion", "درجة الحركة" }, { "logo", "اللوجو" }, { "logoDark", "لوجو الوضع الغامق" }, { "logoH", "حجم اللوجو" }, { "icon", "أيقونة المتجر" }, { "share", "صورة المشاركة" }, { "ann", "شريط الإعلان" }, { "hero", "الغلاف" }, { "sections", "أقسام الرئيسية" }, { "menu", "القايمة" }, { "footer", "الفوتر" }, { "badges", "شارات المنتجات" }, { "trust", "شارات الثقة" }, { "opts", "اختيارات القالب" } };
            foreach (var kv in names)
            {
                object x, y; a.TryGetValue(kv.Key, out x); b.TryGetValue(kv.Key, out y);
                if (ShopJson.Write(x) != ShopJson.Write(y)) l.Add(kv.Value);
            }
            ShopSeasons.Diff(a, b, l);
            return l;
        }

        static void Publish(SqlConnection c, StoreInfo s, Shop sh, string note)
        {
            if (sh.Status != "live") throw new ShopException("not_live", "أول نشر بيبدأ بطلب مراجعة.");
            var live = sh.Look(false); var draft = sh.Look(true);
            // a new logo on an open store goes to the admin's list (the look itself is published: the plan holds only the name, link and logo)
            bool logo = ShopJson.Str(ShopJson.At(live, "logo")) != ShopJson.Str(ShopJson.At(draft, "logo"));
            if (logo && !s.IsAdmin) { draft["logo"] = ShopJson.At(live, "logo"); }
            var json = ShopJson.Write(draft);
            SaveVersion(c, s.Id, json, note);
            Exec(c, null, "UPDATE dbo.Shops SET Live=@l, DraftAt=CASE WHEN @logo=1 THEN DraftAt ELSE NULL END, PublishedAt=SYSUTCDATETIME(), ReviewChange=CASE WHEN @logo=1 THEN 1 ELSE ReviewChange END WHERE StoreId=@s", "@l", json, "@logo", logo && !s.IsAdmin, "@s", s.Id);
            Shops.Bump(c, s.Id);
        }
        public static void SaveVersion(SqlConnection c, int storeId, string look, string note)
        {
            Exec(c, null, "INSERT INTO dbo.ShopVersions(StoreId, Look, Note) VALUES(@s, @l, @n); DELETE FROM dbo.ShopVersions WHERE StoreId=@s AND Id NOT IN (SELECT TOP 10 Id FROM dbo.ShopVersions WHERE StoreId=@s ORDER BY Id DESC)", "@s", storeId, "@l", look, "@n", Nz(note));
        }
        static void Restore(SqlConnection c, Shop sh, int id)
        {
            string look; using (var cmd = Db.Cmd(c, "SELECT Look FROM dbo.ShopVersions WHERE StoreId=@s AND Id=@i", "@s", sh.StoreId, "@i", id)) look = cmd.ExecuteScalar() as string;
            if (look == null) throw new ShopException("not_found", "النسخة دي مش موجودة.");
            Exec(c, null, "UPDATE dbo.Shops SET Draft=@l, DraftAt=SYSUTCDATETIME() WHERE StoreId=@s", "@l", look, "@s", sh.StoreId);
        }

        // the first publish: the admin looks at the store before customers do
        static void Submit(SqlConnection c, StoreInfo s, Shop sh, HttpResponse res)
        {
            if (sh.Status == "live") throw new ShopException("live", "متجرك شغال بالفعل. دوس «انشر» عشان التعديلات تنزل.");
            if (sh.Status == "closed") throw new ShopException("closed", "متجرك مقفول من الإدارة: " + (sh.CloseNote ?? "") + " كلّمنا الأول.");
            var left = Steps(c, s, sh, ShopCache.Get(c, sh)).Where(x => (bool)x["must"] && !(bool)x["done"]).Select(x => (string)x["t"]).ToList();
            if (left.Count > 0) throw new ShopException("steps", "فاضل قبل النشر: " + string.Join("، ", left) + ".");
            Exec(c, null, "UPDATE dbo.Shops SET Status='review', SubmittedAt=SYSUTCDATETIME(), ReviewNote=NULL WHERE StoreId=@s", "@s", s.Id);
            int sid = s.Id; System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(ct => ShopHooks.Submitted(sid));
            Ok(res, "state", State(c, s, Shops.ByStore(c, s.Id)));
        }

        static void Preview(SqlConnection c, Shop sh, bool share, HttpResponse res)
        {
            Exec(c, null, "DELETE FROM dbo.ShopPreviews WHERE ExpiresAt<SYSUTCDATETIME()");
            if (share && Scalar(c, "SELECT COUNT(*) FROM dbo.ShopPreviews WHERE StoreId=@s AND Kind='share'", "@s", sh.StoreId) >= 10) throw new ShopException("many", "عملت لينكات معاينة كتير النهارده. استخدم واحد منهم.");
            var t = Shops.Token(16);
            Exec(c, null, "INSERT INTO dbo.ShopPreviews(Token, StoreId, Kind, ExpiresAt) VALUES(@t, @s, @k, DATEADD(HOUR, 24, SYSUTCDATETIME()))", "@t", t, "@s", sh.StoreId, "@k", share ? "share" : "own");
            Ok(res, "url", sh.Url + "/_preview/" + t, "live", sh.Url + "/");
        }

        // ---------- categories ----------
        static string FreeSlug(SqlConnection c, string table, int storeId, string wanted, string name, int id, int max)
        {
            var b = Shops.Slugify(string.IsNullOrWhiteSpace(wanted) ? name : wanted, max - 6); if (b.Length == 0) b = table == "ShopProducts" ? "product" : "category";
            if (table == "ShopCategories" && b == "all") b = "all-products";
            for (int i = 1; i < 200; i++)
            {
                var t = i == 1 ? b : b + "-" + i;
                if (Scalar(c, "SELECT COUNT(*) FROM dbo." + table + " WHERE StoreId=@s AND Slug=@v AND Id<>@i", "@s", storeId, "@v", t, "@i", id) == 0) return t;
            }
            return b + "-" + Shops.Token(3);
        }
        static string Img(Dictionary<string, object> b, string k, int storeId) { return ShopLook.Img(ShopJson.At(b, k), storeId); }

        static int SaveCat(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int id = Int(b, "id"), parent = Int(b, "parent"), s = sh.StoreId; string name = Str(b, "name", 80);
            if (name.Length < 2) throw new ShopException("name", "اكتب اسم القسم.");
            var d = ShopCache.Get(c, sh); ShopCat up = null, me = null;
            if (id > 0 && !d.CatById.TryGetValue(id, out me)) throw new ShopException("not_found", "القسم ده مش موجود.");
            if (parent > 0)
            {
                if (!d.CatById.TryGetValue(parent, out up)) throw new ShopException("parent", "القسم الرئيسي مش موجود.");
                if (up.ParentId.HasValue || parent == id) throw new ShopException("parent", "الأقسام مستويين بس: رئيسي وفرعي.");
                if (me != null && me.Kids.Count > 0) throw new ShopException("parent", "القسم ده جواه أقسام فرعية، فلازم يفضل رئيسي.");
            }
            if (id == 0 && d.Cats.Count >= Shops.MaxCategories) throw new ShopException("limit", "وصلت لأقصى عدد أقسام (" + Shops.MaxCategories + ").");
            string slug = FreeSlug(c, "ShopCategories", s, Str(b, "slug", 60), name, id, 60);
            var guide = ShopJson.At(b, "guide") as Dictionary<string, object>;
            object[] args = { "@s", s, "@i", id, "@p", parent > 0 ? (object)parent : null, "@slug", slug, "@n", name, "@ne", Nz(Str(b, "nameEn", 80)), "@d", Nz(Str(b, "descr", 1000)), "@de", Nz(Str(b, "descrEn", 1000)), "@im", Nz(Img(b, "image", s)), "@cv", Nz(Img(b, "cover", s)), "@h", Bool(b, "hidden"), "@m", Bool(b, "inMenu", true), "@ho", Bool(b, "inHome", true), "@mt", Nz(Str(b, "metaTitle", 120)), "@md", Nz(Str(b, "metaDesc", 300)), "@g", guide == null ? null : ShopJson.Write(Guide(guide)), "@lo", Bool(b, "linkOnly"), "@sf", ShopPromo.Time(ShopJson.At(b, "from")), "@st", ShopPromo.Time(ShopJson.At(b, "to")) };
            if (id == 0)
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopCategories(StoreId, ParentId, Slug, Name, NameEn, Descr, DescrEn, Image, Cover, Hidden, InMenu, InHome, MetaTitle, MetaDesc, SizeGuide, LinkOnly, ShowFrom, ShowTo, Sort) OUTPUT INSERTED.Id VALUES(@s, @p, @slug, @n, @ne, @d, @de, @im, @cv, @h, @m, @ho, @mt, @md, @g, @lo, @sf, @st, (SELECT ISNULL(MAX(Sort),0)+1 FROM dbo.ShopCategories WHERE StoreId=@s))", args)) id = (int)cmd.ExecuteScalar();
            else
            {
                Exec(c, null, "UPDATE dbo.ShopCategories SET ParentId=@p, Slug=@slug, Name=@n, NameEn=@ne, Descr=@d, DescrEn=@de, Image=@im, Cover=@cv, Hidden=@h, InMenu=@m, InHome=@ho, MetaTitle=@mt, MetaDesc=@md, SizeGuide=@g, LinkOnly=@lo, ShowFrom=@sf, ShowTo=@st WHERE StoreId=@s AND Id=@i", args);
                if (me.Slug != slug) Redirect(c, s, "c", me.Slug, id);
            }
            Exec(c, null, "DELETE FROM dbo.ShopRedirects WHERE StoreId=@s AND Kind='c' AND OldSlug=@o", "@s", s, "@o", slug);
            Shops.Bump(c, s);
            return id;
        }
        static void Redirect(SqlConnection c, int storeId, string kind, string old, int id)
        {
            Exec(c, null, "MERGE dbo.ShopRedirects AS t USING (SELECT @s AS StoreId, @k AS Kind, @o AS OldSlug) AS x ON t.StoreId=x.StoreId AND t.Kind=x.Kind AND t.OldSlug=x.OldSlug WHEN MATCHED THEN UPDATE SET TargetId=@i WHEN NOT MATCHED THEN INSERT (StoreId, Kind, OldSlug, TargetId) VALUES (@s, @k, @o, @i);", "@s", storeId, "@k", kind, "@o", old, "@i", id);
        }
        // a size guide: {head: [..], rows: [[..]], note}
        public static Dictionary<string, object> Guide(Dictionary<string, object> g)
        {
            return new Dictionary<string, object> { { "head", ShopJson.List(ShopJson.At(g, "head")).Take(8).Select(x => ShopJson.Str(x, 40)).ToArray() }, { "rows", ShopJson.List(ShopJson.At(g, "rows")).Take(20).Select(r => ShopJson.List(r).Take(8).Select(x => ShopJson.Str(x, 40)).ToArray()).ToArray() }, { "note", ShopJson.Str(ShopJson.At(g, "note"), 300) } };
        }
        // deleting a category keeps its products: they stay without it. Its sub-categories become main ones.
        static void DelCat(SqlConnection c, Shop sh, int id)
        {
            Exec(c, null, "UPDATE dbo.ShopCategories SET ParentId=NULL WHERE StoreId=@s AND ParentId=@i; DELETE FROM dbo.ShopProductCategories WHERE StoreId=@s AND CategoryId=@i; DELETE FROM dbo.ShopCategories WHERE StoreId=@s AND Id=@i; DELETE FROM dbo.ShopRedirects WHERE StoreId=@s AND Kind='c' AND TargetId=@i", "@s", sh.StoreId, "@i", id);
            Shops.Bump(c, sh.StoreId);
        }
        static void SortCats(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int n = 0; foreach (var x in ShopJson.List(ShopJson.At(b, "ids")).Take(Shops.MaxCategories)) Exec(c, null, "UPDATE dbo.ShopCategories SET Sort=@n WHERE StoreId=@s AND Id=@i", "@n", n++, "@s", sh.StoreId, "@i", ShopJson.Int(x));
            Shops.Bump(c, sh.StoreId);
        }
        static readonly Dictionary<string, string[][]> Ready = new Dictionary<string, string[][]> {
            { "clothes", new[] { new[] { "فساتين", "Dresses" }, new[] { "عبايات", "Abayas", "خروج|Going out", "يومي|Everyday", "استقبال|Occasion" }, new[] { "بلوزات", "Tops" }, new[] { "بناطيل", "Trousers" }, new[] { "طرح", "Scarves" }, new[] { "شنط", "Bags" }, new[] { "أحذية", "Shoes" } } },
            { "home", new[] { new[] { "مطبخ", "Kitchen", "طاسات وحلل|Cookware", "كاسات|Glassware", "أجهزة صغيرة|Small appliances" }, new[] { "كهربا وإضاءة", "Electrics and lighting" }, new[] { "تنظيم وتخزين", "Storage" }, new[] { "حمّام", "Bathroom" } } },
            { "beauty", new[] { new[] { "بشرة", "Skincare", "غسول|Cleansers", "كريمات|Creams", "واقي شمس|Sunscreen" }, new[] { "شعر", "Hair" }, new[] { "مكياج", "Makeup" }, new[] { "عطور", "Fragrance" } } },
            { "spices", new[] { new[] { "بهارات", "Spices" }, new[] { "أعشاب", "Herbs" }, new[] { "مكسرات", "Nuts" }, new[] { "زيوت", "Oils" } } },
            { "electronics", new[] { new[] { "سماعات", "Headphones", "سلكي|Wired", "بلوتوث|Bluetooth" }, new[] { "شواحن وكابلات", "Chargers and cables" }, new[] { "ساعات ذكية", "Smart watches" }, new[] { "إكسسوار موبايل", "Phone accessories" } } },
            { "toys", new[] { new[] { "من سنة لـ 3", "1 to 3 years" }, new[] { "من 3 لـ 6", "3 to 6 years" }, new[] { "من 6 وفوق", "6 and up" }, new[] { "ألعاب تعليمية", "Educational" } } },
            { "sweets", new[] { new[] { "شوكولاتة", "Chocolate" }, new[] { "كوكيز", "Cookies" }, new[] { "علب هدايا", "Gift boxes" } } },
            { "plants", new[] { new[] { "نباتات داخلية", "Indoor plants" }, new[] { "نباتات خارجية", "Outdoor plants" }, new[] { "قصاري", "Pots" }, new[] { "ورد", "Flowers" } } },
            { "furniture", new[] { new[] { "أنتريه", "Living room" }, new[] { "غرف نوم", "Bedroom" }, new[] { "سفرة", "Dining" }, new[] { "مكتب", "Office" }, new[] { "ديكور", "Decor" } } },
            { "jewelry", new[] { new[] { "خواتم", "Rings" }, new[] { "سلاسل", "Necklaces" }, new[] { "حلقان", "Earrings" }, new[] { "ساعات", "Watches" } } },
            { "grocery", new[] { new[] { "خضار وفاكهة", "Fruit and veg" }, new[] { "ألبان وبيض", "Dairy and eggs" }, new[] { "مخبوزات", "Bakery" }, new[] { "بقالة", "Pantry" } } } };
        static void ReadyCats(SqlConnection c, Shop sh, string biz)
        {
            string[][] list; if (!Ready.TryGetValue(biz, out list)) throw new ShopException("biz", "مفيش أقسام جاهزة للنشاط ده.");
            var d = ShopCache.Get(c, sh); var have = new HashSet<string>(d.Cats.Select(k => k.Name));
            foreach (var x in list)
            {
                if (have.Contains(x[0])) continue;
                int pid = SaveCat(c, sh, new Dictionary<string, object> { { "name", x[0] }, { "nameEn", x[1] }, { "slug", x[1] } });
                foreach (var k in x.Skip(2)) { var p = k.Split('|'); if (!have.Contains(p[0])) SaveCat(c, sh, new Dictionary<string, object> { { "name", p[0] }, { "nameEn", p[1] }, { "slug", p[1] }, { "parent", pid } }); }
            }
        }
        // which products are in a category, in what order, and which are pinned first
        static void CatProducts(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int id = Int(b, "id"); if (Scalar(c, "SELECT COUNT(*) FROM dbo.ShopCategories WHERE StoreId=@s AND Id=@i", "@s", sh.StoreId, "@i", id) == 0) throw new ShopException("not_found", "القسم ده مش موجود.");
            var ids = ShopJson.List(ShopJson.At(b, "ids")).Select(x => ShopJson.Int(x)).Where(x => x > 0).Distinct().Take(Shops.MaxProducts).ToList(); var pins = new HashSet<int>(ShopJson.List(ShopJson.At(b, "pins")).Select(x => ShopJson.Int(x)));
            var d = ShopCache.Get(c, sh);
            using (var tx = c.BeginTransaction())
            {
                var main = new HashSet<int>(); using (var cmd = Db.Cmd(c, "SELECT ProductId FROM dbo.ShopProductCategories WHERE CategoryId=@i AND IsMain=1", "@i", id)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) while (r.Read()) main.Add((int)r[0]); }
                Exec(c, tx, "DELETE FROM dbo.ShopProductCategories WHERE StoreId=@s AND CategoryId=@i", "@s", sh.StoreId, "@i", id); int n = 0;
                foreach (var pid in ids)
                {
                    ShopProd p; if (!d.ById.TryGetValue(pid, out p) || (p.Cats.Count(x => x != id) >= 10)) continue;
                    Exec(c, tx, "INSERT INTO dbo.ShopProductCategories(StoreId, ProductId, CategoryId, IsMain, Sort, Pinned) VALUES(@s, @p, @i, @m, @n, @pin)", "@s", sh.StoreId, "@p", pid, "@i", id, "@m", main.Contains(pid) || p.Cats.All(x => x == id), "@n", n++, "@pin", pins.Contains(pid));
                }
                tx.Commit();
            }
            Shops.Bump(c, sh.StoreId);
        }

        // ---------- products ----------
        // given: a file of products saves them against the copy it read at the start, and moves the version once at its end
        static int SaveProduct(SqlConnection c, Shop sh, Dictionary<string, object> b, ShopData given = null)
        {
            int id = Int(b, "id"), s = sh.StoreId; string name = Str(b, "name", 150);
            if (name.Length < 2) throw new ShopException("name", "اكتب اسم المنتج.");
            decimal price = ShopJson.Dec(ShopJson.At(b, "price")), old = ShopJson.Dec(ShopJson.At(b, "old")), cost = ShopJson.Dec(ShopJson.At(b, "cost"));
            if (price < 1 || price > 5000000) throw new ShopException("price", "اكتب سعر المنتج.");
            if (old > 0 && old <= price) throw new ShopException("old", "السعر قبل الخصم لازم يكون أكبر من السعر الحالي.");
            var d = given ?? ShopCache.Get(c, sh); ShopProd me = null;
            if (id > 0 && !d.ById.TryGetValue(id, out me)) throw new ShopException("not_found", "المنتج ده مش موجود.");
            if (id == 0 && d.Products.Count >= Shops.MaxProducts) throw new ShopException("limit", "وصلت لأقصى عدد منتجات (" + Shops.MaxProducts + ").");

            // options (up to 3) and the sizes / colors made from them
            var opts = new List<object>(); var names = new List<List<string>>();
            foreach (var o in ShopJson.List(ShopJson.At(b, "options")).OfType<Dictionary<string, object>>().Take(3))
            {
                var n = ShopJson.List(ShopJson.At(o, "n")); string ar = ShopJson.Str(n.Count > 0 ? n[0] : null, 30); var vals = new List<object>(); var seen = new List<string>();
                foreach (var v in ShopJson.List(ShopJson.At(o, "v")).Take(40))
                {
                    var l = ShopJson.List(v); string t = ShopJson.Str(l.Count > 0 ? l[0] : v, 40).Replace("|", "/"); if (t.Length == 0 || seen.Contains(t)) continue;
                    string hex = ShopJson.Str(l.Count > 2 ? l[2] : null, 9); seen.Add(t); vals.Add(new[] { t, ShopJson.Str(l.Count > 1 ? l[1] : null, 40), Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$") ? hex : "" });
                }
                if (ar.Length == 0 || vals.Count == 0) continue;
                opts.Add(new Dictionary<string, object> { { "n", new[] { ar, ShopJson.Str(n.Count > 1 ? n[1] : null, 30) } }, { "v", vals } }); names.Add(seen);
            }
            var rows = new Dictionary<string, Dictionary<string, object>>();
            foreach (var v in ShopJson.List(ShopJson.At(b, "vars")).OfType<Dictionary<string, object>>().Take(Shops.MaxVariants + 1))
            {
                var o = ShopJson.List(ShopJson.At(v, "o")).Select(x => ShopJson.Str(x, 40).Replace("|", "/")).ToList();
                if (o.Count != names.Count || o.Where((x, i) => !names[i].Contains(x)).Any()) continue;
                rows[string.Join("|", o)] = v;
            }
            if (names.Count > 0 && rows.Count == 0) throw new ShopException("vars", "اختار مقاس أو لون واحد على الأقل للبيع.");
            if (rows.Count > Shops.MaxVariants) throw new ShopException("vars", "أقصى عدد للمقاسات والألوان " + Shops.MaxVariants + ".");
            if (names.Count == 0) rows = new Dictionary<string, Dictionary<string, object>> { { "", rows.ContainsKey("") ? rows[""] : new Dictionary<string, object> { { "stock", ShopJson.At(b, "stock") }, { "stock0", ShopJson.At(b, "stock0") }, { "active", true } } } };

            var cats = ShopJson.List(ShopJson.At(b, "cats")).Select(x => ShopJson.Int(x)).Where(x => d.CatById.ContainsKey(x)).Distinct().Take(10).ToList(); int main = Int(b, "main"); if (!cats.Contains(main)) main = cats.Count > 0 ? cats[0] : 0;
            var specs = ShopJson.List(ShopJson.At(b, "specs")).Select(x => ShopJson.List(x)).Where(x => x.Count >= 2 && ShopJson.Str(x[0]).Length > 0 && ShopJson.Str(x[1]).Length > 0).Take(30).Select(x => x.Take(4).Select(y => ShopJson.Str(y, 120)).ToArray()).ToArray();
            var guide = ShopJson.At(b, "guide") as Dictionary<string, object>; var extra = Extra(ShopJson.Sub(b, "extra"), s);
            string outMode = new[] { "hide", "show", "order" }.Contains(Str(b, "out", 6)) ? Str(b, "out", 6) : "show", status = Str(b, "status", 8) == "hidden" ? "hidden" : "active";
            int min = Math.Max(1, Math.Min(500, Int(b, "min", 1))), max = Math.Max(0, Math.Min(999, Int(b, "max"))); if (max > 0 && max < min) max = min;
            string slug = FreeSlug(c, "ShopProducts", s, Str(b, "slug", 80), name, id, 80);
            object[] args = { "@s", s, "@i", id, "@slug", slug, "@n", name, "@ne", Nz(Str(b, "nameEn", 150)), "@d", Nz(Str(b, "descr", 8000)), "@de", Nz(Str(b, "descrEn", 8000)), "@p", price, "@o", old > 0 ? (object)old : null, "@c", cost > 0 ? (object)cost : null, "@sku", Nz(Str(b, "sku", 60)), "@u", Nz(Str(b, "unit", 20)), "@br", Nz(Str(b, "brand", 60)), "@tg", Nz(Str(b, "tags", 300)), "@min", min, "@max", max > 0 ? (object)max : null,
                "@t", Bool(b, "track", true), "@om", outMode, "@st", status, "@op", opts.Count == 0 ? null : ShopJson.Write(opts), "@sp", specs.Length == 0 ? null : ShopJson.Write(specs), "@g", guide == null ? null : ShopJson.Write(Guide(guide)), "@x", extra.Count == 0 ? null : ShopJson.Write(extra), "@mt", Nz(Str(b, "metaTitle", 120)), "@md", Nz(Str(b, "metaDesc", 300)), "@f", Bool(b, "feat"), "@w", Int(b, "weight") > 0 ? (object)Int(b, "weight") : null, "@ask", Ask(ShopJson.At(b, "ask")), "@hasAsk", b.ContainsKey("ask"), "@vd", Nz(Vid(b, s)), "@goes", Nz(string.Join(",", ShopJson.List(ShopJson.At(b, "goes")).Select(x => ShopJson.Int(x)).Where(x => x > 0 && x != id && d.ById.ContainsKey(x)).Distinct().Take(8))) };
            using (var tx = c.BeginTransaction())
            {
                if (id == 0)
                    using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.ShopProducts(StoreId, Slug, Name, NameEn, Descr, DescrEn, Price, OldPrice, Cost, Sku, Unit, Brand, Tags, MinQty, MaxQty, Track, OutMode, Status, Options, Specs, SizeGuide, Extra, MetaTitle, MetaDesc, Feat, Weight, Ask, Goes, Video)
                        OUTPUT INSERTED.Id VALUES(@s, @slug, @n, @ne, @d, @de, @p, @o, @c, @sku, @u, @br, @tg, @min, @max, @t, @om, @st, @op, @sp, @g, @x, @mt, @md, @f, @w, @ask, @goes, @vd)", args)) { cmd.Transaction = tx; id = (int)cmd.ExecuteScalar(); }
                else
                    Exec(c, tx, @"UPDATE dbo.ShopProducts SET Slug=@slug, Name=@n, NameEn=@ne, Descr=@d, DescrEn=@de, Price=@p, OldPrice=@o, Cost=@c, Sku=@sku, Unit=@u, Brand=@br, Tags=@tg, MinQty=@min, MaxQty=@max, Track=@t, OutMode=@om, Status=@st, Options=@op, Specs=@sp, SizeGuide=@g, Extra=@x, MetaTitle=@mt, MetaDesc=@md, Feat=@f, Weight=@w, Ask=CASE WHEN @hasAsk=1 THEN @ask ELSE Ask END, Goes=CASE WHEN @hasAsk=1 THEN @goes ELSE Goes END, Video=CASE WHEN @hasAsk=1 THEN @vd ELSE Video END, UpdatedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Id=@i", args);

                // the sizes: one kept by its values, the quantity moved by the difference (an order may have taken some since the page was opened)
                var have = new Dictionary<string, int>(); using (var cmd = Db.Cmd(c, "SELECT Id, Opts FROM dbo.ShopVariants WHERE StoreId=@s AND ProductId=@p", "@s", s, "@p", id)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) while (r.Read()) have[(string)r[1]] = (int)r[0]; }
                int sort = 0;
                foreach (var kv in rows)
                {
                    var v = kv.Value; decimal? vp = ShopJson.DecN(ShopJson.At(v, "price")), vo = ShopJson.DecN(ShopJson.At(v, "old")), vc = ShopJson.DecN(ShopJson.At(v, "cost")); if (vp.HasValue && vp.Value < 1) vp = null; if (vo.HasValue && (!vp.HasValue || vo.Value <= vp.Value)) vo = null;
                    int stock = Math.Max(0, Math.Min(1000000, ShopJson.Int(ShopJson.At(v, "stock")))); int? from = ShopJson.At(v, "stock0") == null ? (int?)null : ShopJson.Int(ShopJson.At(v, "stock0")); int vid;
                    object[] va = { "@s", s, "@p", id, "@k", kv.Key, "@pr", vp.HasValue ? (object)vp.Value : null, "@o", vo.HasValue ? (object)vo.Value : null, "@c", vc.HasValue && vc.Value > 0 ? (object)vc.Value : null, "@sku", Nz(Str(v, "sku", 60)), "@im", Nz(Img(v, "image", s)), "@a", Bool(v, "active", true), "@so", sort++, "@st", stock };
                    if (have.TryGetValue(kv.Key, out vid))
                    {
                        int delta = from.HasValue ? stock - from.Value : 0; int before; using (var cmd = Db.Cmd(c, "SELECT Stock FROM dbo.ShopVariants WITH (UPDLOCK) WHERE Id=@i", "@i", vid)) { cmd.Transaction = tx; before = (int)cmd.ExecuteScalar(); }
                        int after = from.HasValue ? Math.Max(0, before + delta) : stock;
                        Exec(c, tx, "UPDATE dbo.ShopVariants SET Price=@pr, OldPrice=@o, Cost=@c, Sku=@sku, Image=@im, Active=@a, Sort=@so, Stock=@after WHERE Id=@vid", va.Concat(new object[] { "@vid", vid, "@after", after }).ToArray());
                        if (after != before) ShopOrders.Stock(c, tx, s, id, vid, after - before, after, "set", null, null);
                        have.Remove(kv.Key);
                    }
                    else
                    {
                        using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopVariants(StoreId, ProductId, Opts, Price, OldPrice, Cost, Sku, Image, Active, Sort, Stock) OUTPUT INSERTED.Id VALUES(@s, @p, @k, @pr, @o, @c, @sku, @im, @a, @so, @st)", va)) { cmd.Transaction = tx; vid = (int)cmd.ExecuteScalar(); }
                        if (stock > 0) ShopOrders.Stock(c, tx, s, id, vid, stock, stock, "first", null, null);
                    }
                }
                foreach (var gone in have.Values) Exec(c, tx, "DELETE FROM dbo.ShopVariants WHERE Id=@i", "@i", gone);

                var pins = new Dictionary<int, object[]>(); using (var cmd = Db.Cmd(c, "SELECT CategoryId, Sort, Pinned FROM dbo.ShopProductCategories WHERE StoreId=@s AND ProductId=@p", "@s", s, "@p", id)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) while (r.Read()) pins[(int)r[0]] = new object[] { (int)r[1], (bool)r[2] }; }
                Exec(c, tx, "DELETE FROM dbo.ShopProductCategories WHERE StoreId=@s AND ProductId=@p", "@s", s, "@p", id);
                foreach (var k in cats) { object[] x; pins.TryGetValue(k, out x); Exec(c, tx, "INSERT INTO dbo.ShopProductCategories(StoreId, ProductId, CategoryId, IsMain, Sort, Pinned) VALUES(@s, @p, @k, @m, @so, @pin)", "@s", s, "@p", id, "@k", k, "@m", k == main, "@so", x == null ? 0 : x[0], "@pin", x != null && (bool)x[1]); }
                // pictures uploaded while the product was new
                int n = 0; foreach (var x in ShopJson.List(ShopJson.At(b, "images")).Take(Shops.MaxImages)) Exec(c, tx, "UPDATE dbo.ShopImages SET ProductId=@p, Sort=@n, Used=1 WHERE StoreId=@s AND Path=@path AND (ProductId IS NULL OR ProductId=@p)", "@p", id, "@n", n++, "@s", s, "@path", ShopJson.Str(x, 160));
                Exec(c, tx, "DELETE FROM dbo.ShopRedirects WHERE StoreId=@s AND Kind='p' AND OldSlug=@o", "@s", s, "@o", slug);
                if (given == null) Exec(c, tx, "UPDATE dbo.Shops SET Ver=Ver+1 WHERE StoreId=@s", "@s", s);
                tx.Commit();
            }
            if (me != null && me.Slug != slug) Redirect(c, s, "p", me.Slug, id);
            return id;
        }
        static string Vid(Dictionary<string, object> b, int storeId) { var p = Str(b, "video", 160); return Regex.IsMatch(p, "^" + storeId + @"/[a-z0-9]{8,40}\.(mp4|webm)$") ? p : ""; }
        // «خيارات يكتبها الزبون»: a name to engrave, a message, a picture (up to 4)
        static object Ask(object v)
        {
            var l = new List<object>();
            foreach (var a in ShopJson.List(v).OfType<Dictionary<string, object>>().Take(4))
            {
                var nm = ShopJson.List(ShopJson.At(a, "n")); string ar = ShopJson.Str(nm.Count > 0 ? nm[0] : null, 60), k = ShopJson.Str(ShopJson.At(a, "k"), 4); if (ar.Length < 2) continue;
                l.Add(new Dictionary<string, object> { { "k", k == "pic" || k == "long" ? k : "text" }, { "n", new[] { ar, ShopJson.Str(nm.Count > 1 ? nm[1] : null, 60) } }, { "req", ShopJson.Bool(ShopJson.At(a, "req")) }, { "max", Math.Max(1, Math.Min(200, ShopJson.Int(ShopJson.At(a, "max"), 40))) } });
            }
            return l.Count == 0 ? null : ShopJson.Write(l);
        }
        public static int SaveProductFrom(SqlConnection c, Shop sh, Dictionary<string, object> b, ShopData given) { return SaveProduct(c, sh, b, given); }
        public static int SaveCatFrom(SqlConnection c, Shop sh, Dictionary<string, object> b) { return SaveCat(c, sh, b); }
        // what a template asks for about a product (room size, sun and water, ingredients...): short texts, numbers, lists
        static Dictionary<string, object> Extra(Dictionary<string, object> x, int storeId)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in x.Take(40))
            {
                if (!Regex.IsMatch(kv.Key, "^[A-Za-z][A-Za-z0-9_]{0,24}$")) continue;
                var l = ShopJson.List(kv.Value);
                d[kv.Key] = kv.Value is bool || kv.Value is int || kv.Value is decimal || kv.Value is double ? kv.Value : l.Count > 0 ? (object)l.Take(30).Select(i => i is Dictionary<string, object> ? (object)((Dictionary<string, object>)i).Take(8).ToDictionary(q => q.Key, q => (object)ShopJson.Str(q.Value, 200)) : ShopJson.List(i).Count > 0 ? (object)ShopJson.List(i).Take(6).Select(z => ShopJson.Str(z, 200)).ToArray() : ShopJson.Str(i, 200)).ToArray() : ShopJson.Str(kv.Value, 300);
            }
            return d;
        }

        static void DelProducts(SqlConnection c, Shop sh, List<int> ids)
        {
            foreach (var id in ids.Take(200))
            {
                var paths = new List<string>(); using (var cmd = Db.Cmd(c, "SELECT Path FROM dbo.ShopImages WHERE StoreId=@s AND ProductId=@p", "@s", sh.StoreId, "@p", id)) using (var r = cmd.ExecuteReader()) while (r.Read()) paths.Add((string)r[0]);
                // pictures an old order shows stay
                paths = paths.Where(p => Scalar(c, "SELECT COUNT(*) FROM dbo.ShopOrderItems WHERE StoreId=@s AND Image=@p", "@s", sh.StoreId, "@p", p) == 0).ToList();
                Exec(c, null, "DELETE FROM dbo.ShopVariants WHERE StoreId=@s AND ProductId=@p; DELETE FROM dbo.ShopProductCategories WHERE StoreId=@s AND ProductId=@p; DELETE FROM dbo.ShopRedirects WHERE StoreId=@s AND Kind='p' AND TargetId=@p; UPDATE dbo.ShopImages SET ProductId=NULL WHERE StoreId=@s AND ProductId=@p; DELETE FROM dbo.ShopProducts WHERE StoreId=@s AND Id=@p", "@s", sh.StoreId, "@p", id);
                ShopImages.Delete(c, sh.StoreId, paths);
            }
            Shops.Bump(c, sh.StoreId);
        }
        static int Duplicate(SqlConnection c, Shop sh, int id)
        {
            var p = Product(c, sh, id); if (p == null) throw new ShopException("not_found", "المنتج ده مش موجود.");
            p["id"] = 0; p["name"] = Cut("نسخة من " + p["name"], 150); p["slug"] = ""; p["status"] = "hidden"; p["images"] = new object[0];
            var json = ShopJson.Obj(ShopJson.Write(p));
            foreach (var v in ShopJson.List(ShopJson.At(json, "vars")).OfType<Dictionary<string, object>>()) { v["image"] = ""; v["stock"] = 0; }
            json["stock"] = 0;
            return SaveProduct(c, sh, json);
        }
        static string Cut(string s, int n) { return s.Length > n ? s.Substring(0, n) : s; }

        // the same change on several products: hide / show / category / price
        static void Bulk(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            var ids = ShopJson.List(ShopJson.At(b, "ids")).Select(x => ShopJson.Int(x)).Where(x => x > 0).Distinct().Take(500).ToList(); if (ids.Count == 0) throw new ShopException("none", "علّم على منتج واحد على الأقل.");
            string what = Str(b, "what", 12), inIds = string.Join(",", ids);
            if (what == "hide" || what == "show") Exec(c, null, "UPDATE dbo.ShopProducts SET Status=@st WHERE StoreId=@s AND Id IN (" + inIds + ")", "@st", what == "hide" ? "hidden" : "active", "@s", sh.StoreId);
            else if (what == "cat" || what == "uncat")
            {
                int cat = Int(b, "cat"); if (Scalar(c, "SELECT COUNT(*) FROM dbo.ShopCategories WHERE StoreId=@s AND Id=@i", "@s", sh.StoreId, "@i", cat) == 0) throw new ShopException("cat", "اختار القسم.");
                if (what == "uncat") Exec(c, null, "DELETE FROM dbo.ShopProductCategories WHERE StoreId=@s AND CategoryId=@k AND ProductId IN (" + inIds + ")", "@s", sh.StoreId, "@k", cat);
                else
                {
                    if (Bool(b, "move")) Exec(c, null, "DELETE FROM dbo.ShopProductCategories WHERE StoreId=@s AND ProductId IN (" + inIds + ")", "@s", sh.StoreId);
                    Exec(c, null, @"INSERT INTO dbo.ShopProductCategories(StoreId, ProductId, CategoryId, IsMain) SELECT @s, p.Id, @k, CASE WHEN EXISTS (SELECT 1 FROM dbo.ShopProductCategories x WHERE x.ProductId=p.Id) THEN 0 ELSE 1 END FROM dbo.ShopProducts p
                        WHERE p.StoreId=@s AND p.Id IN (" + inIds + ") AND NOT EXISTS (SELECT 1 FROM dbo.ShopProductCategories x WHERE x.ProductId=p.Id AND x.CategoryId=@k) AND (SELECT COUNT(*) FROM dbo.ShopProductCategories x WHERE x.ProductId=p.Id) < 10", "@s", sh.StoreId, "@k", cat);
                }
            }
            else if (what == "price")
            {
                var v = ShopJson.Dec(ShopJson.At(b, "value")); string kind = Str(b, "kind", 5);
                if (kind == "pct" && v > -90 && v < 500) Exec(c, null, "UPDATE dbo.ShopProducts SET Price=CASE WHEN ROUND(Price*(1+@v/100.0),0)>=1 THEN ROUND(Price*(1+@v/100.0),0) ELSE 1 END WHERE StoreId=@s AND Id IN (" + inIds + "); UPDATE dbo.ShopVariants SET Price=CASE WHEN ROUND(Price*(1+@v/100.0),0)>=1 THEN ROUND(Price*(1+@v/100.0),0) ELSE 1 END WHERE StoreId=@s AND Price IS NOT NULL AND ProductId IN (" + inIds + ")", "@v", v, "@s", sh.StoreId);
                else if (kind == "amt") Exec(c, null, "UPDATE dbo.ShopProducts SET Price=CASE WHEN Price+@v>=1 THEN Price+@v ELSE 1 END WHERE StoreId=@s AND Id IN (" + inIds + "); UPDATE dbo.ShopVariants SET Price=CASE WHEN Price+@v>=1 THEN Price+@v ELSE 1 END WHERE StoreId=@s AND Price IS NOT NULL AND ProductId IN (" + inIds + ")", "@v", v, "@s", sh.StoreId);
                else throw new ShopException("value", "اكتب التغيير.");
                Exec(c, null, "UPDATE dbo.ShopProducts SET OldPrice=NULL WHERE StoreId=@s AND OldPrice<=Price AND Id IN (" + inIds + ")", "@s", sh.StoreId);
            }
            else if (what == "stock")
            {
                // the same quantity for every size, or so many more / fewer; each change is in the stock's record
                int v = Int(b, "value"); bool set = Str(b, "kind", 5) == "set"; if (set && (v < 0 || v > 100000) || !set && (v == 0 || Math.Abs(v) > 100000)) throw new ShopException("value", "اكتب الكمية.");
                var rows = new List<int[]>(); using (var cmd = Db.Cmd(c, "SELECT v.Id, v.ProductId, v.Stock FROM dbo.ShopVariants v JOIN dbo.ShopProducts p ON p.Id=v.ProductId WHERE v.StoreId=@s AND p.Track=1 AND v.ProductId IN (" + inIds + ")", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(new[] { (int)r[0], (int)r[1], (int)r[2] });
                using (var tx = c.BeginTransaction())
                {
                    foreach (var r in rows)
                        using (var cmd = Db.Cmd(c, set ? "UPDATE dbo.ShopVariants SET Stock=@v OUTPUT INSERTED.Stock - DELETED.Stock, INSERTED.Stock WHERE Id=@i AND StoreId=@s" : "UPDATE dbo.ShopVariants SET Stock=CASE WHEN Stock+@v>0 THEN Stock+@v ELSE 0 END OUTPUT INSERTED.Stock - DELETED.Stock, INSERTED.Stock WHERE Id=@i AND StoreId=@s", "@v", v, "@i", r[0], "@s", sh.StoreId))
                        { cmd.Transaction = tx; int delta = 0, after = 0; using (var x = cmd.ExecuteReader()) if (x.Read()) { delta = (int)x[0]; after = (int)x[1]; } if (delta != 0) ShopOrders.Stock(c, tx, sh.StoreId, r[1], r[0], delta, after, "set", null, "تعديل جماعي"); }
                    tx.Commit();
                }
            }
            else throw new ShopException("what", "اختار التعديل.");
            Shops.Bump(c, sh.StoreId);
        }
        static void SortProducts(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int n = 0; foreach (var x in ShopJson.List(ShopJson.At(b, "ids")).Take(Shops.MaxProducts)) Exec(c, null, "UPDATE dbo.ShopProducts SET Sort=@n WHERE StoreId=@s AND Id=@i", "@n", n++, "@s", sh.StoreId, "@i", ShopJson.Int(x));
            Shops.Bump(c, sh.StoreId);
        }
        // after a count of the shelf: the quantity as it is, with the reason
        static void SetStock(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int v = Int(b, "variant"), p = Int(b, "product"), q = Math.Max(0, Math.Min(1000000, Int(b, "q")));
            using (var tx = c.BeginTransaction())
            {
                object had; using (var cmd = Db.Cmd(c, "SELECT Stock FROM dbo.ShopVariants WITH (UPDLOCK) WHERE Id=@v AND ProductId=@p AND StoreId=@s", "@v", v, "@p", p, "@s", sh.StoreId)) { cmd.Transaction = tx; had = cmd.ExecuteScalar(); }
                if (had == null) { tx.Rollback(); throw new ShopException("not_found", "المقاس ده مش موجود."); }
                Exec(c, tx, "UPDATE dbo.ShopVariants SET Stock=@q WHERE Id=@v; UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@q", q, "@v", v, "@s", sh.StoreId);
                if (q != (int)had) ShopOrders.Stock(c, tx, sh.StoreId, p, v, q - (int)had, q, "count", null, Nz(Str(b, "note", 120)) as string);
                tx.Commit();
            }
        }

        // ---------- shipping prices, pages ----------
        static void SaveZones(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            using (var tx = c.BeginTransaction())
            {
                Exec(c, tx, "DELETE FROM dbo.ShopZones WHERE StoreId=@s", "@s", sh.StoreId); var seen = new HashSet<int>();
                foreach (var z in ShopJson.List(ShopJson.At(b, "zones")).OfType<Dictionary<string, object>>().Take(30))
                {
                    int g = Int(z, "g"); if (ShopGeo.Get(g) == null || !seen.Add(g)) continue;
                    var f = ShopJson.DecN(ShopJson.At(z, "f")); var m = ShopJson.DecN(ShopJson.At(z, "m")); var days = Str(z, "d", 30); bool off = Bool(z, "off");
                    if (!f.HasValue && !m.HasValue && days.Length == 0 && !off) continue;
                    Exec(c, tx, "INSERT INTO dbo.ShopZones(StoreId, Gov, Fee, Days, MinSub, Closed) VALUES(@s, @g, @f, @d, @m, @o)", "@s", sh.StoreId, "@g", g, "@f", f.HasValue ? (object)Math.Max(0, Math.Min(5000, f.Value)) : null, "@d", Nz(days), "@m", m.HasValue && m.Value > 0 ? (object)m.Value : null, "@o", off);
                }
                tx.Commit();
            }
            if (b.ContainsKey("ship")) SaveSettings(c, sh, new Dictionary<string, object> { { "settings", new Dictionary<string, object> { { "ship", ShopJson.Sub(b, "ship") } } } });
            else Shops.Bump(c, sh.StoreId);
        }
        static int SavePage(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            int id = Int(b, "id"); string title = Str(b, "title", 100); if (title.Length < 2) throw new ShopException("title", "اكتب عنوان الصفحة.");
            object[] args = { "@s", sh.StoreId, "@i", id, "@t", title, "@te", Nz(Str(b, "titleEn", 100)), "@b", Nz(Str(b, "body", 20000)), "@be", Nz(Str(b, "bodyEn", 20000)), "@h", Bool(b, "hidden"), "@m", Bool(b, "inMenu"), "@f", Bool(b, "inFooter", true) };
            if (id > 0)
            {
                string kind; using (var cmd = Db.Cmd(c, "SELECT Kind FROM dbo.ShopPages WHERE StoreId=@s AND Id=@i", "@s", sh.StoreId, "@i", id)) kind = cmd.ExecuteScalar() as string;
                if (kind == null) throw new ShopException("not_found", "الصفحة دي مش موجودة.");
                // the pages the law asks for can't be hidden
                bool must = kind == "returns" || kind == "contact" || kind == "terms" || kind == "privacy";
                Exec(c, null, "UPDATE dbo.ShopPages SET Title=@t, TitleEn=@te, Body=@b, BodyEn=@be, Hidden=CASE WHEN @must=1 THEN 0 ELSE @h END, InMenu=@m, InFooter=CASE WHEN @must=1 THEN 1 ELSE @f END, UpdatedAt=SYSUTCDATETIME() WHERE StoreId=@s AND Id=@i", args.Concat(new object[] { "@must", must }).ToArray());
                if (kind == "returns") { var set = sh.Settings; set["returnsSeen"] = true; Shops.SaveSettings(c, sh, set); return id; }
            }
            else
            {
                if (Scalar(c, "SELECT COUNT(*) FROM dbo.ShopPages WHERE StoreId=@s", "@s", sh.StoreId) >= Shops.MaxPages) throw new ShopException("limit", "وصلت لأقصى عدد صفحات.");
                string slug = FreeSlug(c, "ShopPages", sh.StoreId, Str(b, "slug", 60), title, 0, 60);
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopPages(StoreId, Slug, Title, TitleEn, Body, BodyEn, Hidden, InMenu, InFooter, Sort) OUTPUT INSERTED.Id VALUES(@s, @slug, @t, @te, @b, @be, @h, @m, @f, 50)", args.Concat(new object[] { "@slug", slug }).ToArray())) id = (int)cmd.ExecuteScalar();
            }
            Shops.Bump(c, sh.StoreId);
            return id;
        }
    }

    // which templates a store may choose: the ones whose files are on the site (site/shop/t/<id>.js, .css, .meta.json) and the admin hasn't closed.
    // A new template is only its files: nothing here names them.
    public static class ShopTemplates
    {
        public class Card { public string Id; public string[] Name; public string For; public int Sort; public bool Hold; }   // Hold: waits until the admin ticks it
        static Card[] cards; static DateTime until;
        public static Card[] Cards()
        {
            var now = DateTime.UtcNow; var c = cards; if (c != null && until > now) return c;
            var l = new List<Card>();
            try
            {
                var dir = System.Web.Hosting.HostingEnvironment.MapPath("~/shop/t");
                if (dir != null && System.IO.Directory.Exists(dir))
                    foreach (var f in System.IO.Directory.GetFiles(dir, "*.js"))
                    {
                        var id = System.IO.Path.GetFileNameWithoutExtension(f); if (!Regex.IsMatch(id, "^[a-z]{3,16}$") || !System.IO.File.Exists(System.IO.Path.ChangeExtension(f, ".css"))) continue;
                        var meta = System.IO.Path.Combine(dir, id + ".meta.json"); var m = System.IO.File.Exists(meta) ? ShopJson.Obj(System.IO.File.ReadAllText(meta, System.Text.Encoding.UTF8)) : new Dictionary<string, object>(); var n = ShopJson.List(ShopJson.At(m, "n"));
                        l.Add(new Card { Id = id, Name = new[] { n.Count > 0 ? ShopJson.Str(n[0], 40) : id, n.Count > 1 ? ShopJson.Str(n[1], 40) : id }, For = ShopJson.Str(ShopJson.At(m, "for"), 200), Sort = ShopJson.Int(ShopJson.At(m, "sort"), 100), Hold = ShopJson.Bool(ShopJson.At(m, "hold")) });
                    }
            }
            catch (Exception ex) { Log.Error(ex); if (c != null) return c; }
            c = l.OrderBy(x => x.Sort).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(); cards = c; until = now.AddSeconds(60); return c;
        }
        public static string[] All() { var l = Cards().Select(x => x.Id).ToArray(); return l.Length == 0 ? new[] { "atelier" } : l; }
        public static string First(SqlConnection c) { return List(c).FirstOrDefault() ?? All()[0]; }
        public static bool IsOpen(SqlConnection c, string t)
        {
            var k = Cards().FirstOrDefault(x => x.Id == t); if (k == null) return false;
            return k.Hold ? (Billing.Get(c, "ShopTplOn") ?? "").Split(',').Contains(t) : !(Billing.Get(c, "ShopTplOff") ?? "").Split(',').Contains(t);
        }
        public static string[] List(SqlConnection c) { return ShopLook.Templates.Where(t => IsOpen(c, t)).ToArray(); }
    }
}
