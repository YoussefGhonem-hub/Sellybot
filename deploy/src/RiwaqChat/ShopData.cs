// A store's catalog in memory: products, sizes and colors, categories, shipping, offers, pages. The store pages, the cart's sum and
// the assistant all read this one copy. It's read again when the store's version (Shops.Ver) changes: every change the owner makes
// bumps it, so a new price is seen by the next request. Stock moves with every order, so it has its own version (StockVer) and only the
// quantities are read again.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RiwaqChat
{
    public class ShopPic { public string Path, Alt, Focus; public int W, H; }
    public class ShopOptVal { public string Ar, En, Hex; }
    public class ShopOpt { public string Ar, En; public List<ShopOptVal> Vals = new List<ShopOptVal>(); }

    public class ShopVar
    {
        public int Id, ProductId, Sort; public int Stock; public string Opts, Sku, Image; public decimal? Price, OldPrice, Cost; public bool Active;
        public int PreLeft;   // «حجز مسبق»: how many more can be booked beyond the shelf (ShopPre.cs)
        public string[] Vals { get { return string.IsNullOrEmpty(Opts) ? new string[0] : Opts.Split('|'); } }
        public string Label { get { return string.IsNullOrEmpty(Opts) ? "" : Opts.Replace("|", " / "); } }
    }

    public class ShopProd
    {
        public int Id, MinQty, Sold, Sort; public int? MaxQty, Weight;
        public string Slug, Name, NameEn, Descr, DescrEn, Sku, Unit, Brand, Tags, OutMode, Status, SpecsJson, SizeGuideJson, ExtraJson, MetaTitle, MetaDesc;
        public decimal Price; public decimal? OldPrice, Cost; public bool Track, Feat;
        public DateTime CreatedAt, UpdatedAt;
        public List<ShopOpt> Options = new List<ShopOpt>(); public List<ShopVar> Vars = new List<ShopVar>(); public List<ShopPic> Pics = new List<ShopPic>();
        public List<int> Cats = new List<int>(); public int MainCat;
        public int[] Stars;   // how many reviews of one star .. five stars (null = none)
        public int Reviews { get { return Stars == null ? 0 : Stars.Sum(); } }
        public double Rating { get { int n = Reviews; return n == 0 ? 0 : Math.Round((Stars[0] + 2.0 * Stars[1] + 3 * Stars[2] + 4 * Stars[3] + 5 * Stars[4]) / n, 1); } }
        public string Video;
        public string Goes;   // ids of what goes with it, chosen by the owner
        public string AskJson;   // what the customer writes or sends with it (a name to engrave, a picture)
        public Dictionary<string, int> Words;

        public decimal PriceOf(ShopVar v) { return v != null && v.Price.HasValue ? v.Price.Value : Price; }
        public decimal? OldOf(ShopVar v) { return v != null && v.Price.HasValue ? v.OldPrice : OldPrice; }
        public decimal? CostOf(ShopVar v) { return v != null && v.Cost.HasValue ? v.Cost : Cost; }
        // can one more of it be bought now
        public bool Can(ShopVar v) { return v != null && v.Active && Status == "active" && (!Track || OutMode == "order" || v.Stock > 0 || v.PreLeft > 0); }
        public bool Available { get { return Vars.Any(Can); } }
        public int Total { get { return Vars.Where(v => v.Active).Sum(v => Math.Max(0, v.Stock)); } }
        // what customers see in the store (an empty product with «يختفي» isn't there at all)
        public bool Shown { get { return Status == "active" && (Available || OutMode != "hide"); } }
    }

    public class ShopCat
    {
        public int Id, Sort; public int? ParentId; public string Slug, Name, NameEn, Descr, DescrEn, Image, Cover, MetaTitle, MetaDesc, SizeGuideJson; public bool Hidden, InMenu, InHome, LinkOnly; public DateTime? From, To;
        public bool InTime(DateTime now) { return !Hidden && (!From.HasValue || From.Value <= now) && (!To.HasValue || To.Value > now); }
        public List<ShopCat> Kids = new List<ShopCat>();
        public int Count;   // shown products in it and under it
    }

    public class ShopOffer
    {
        public int Id, Sort; public string Name, NameEn, Type, RuleJson, Campaign, LookJson; public DateTime? StartsAt, EndsAt; public bool Active, Countdown;
        Dictionary<string, object> rule; HashSet<int> cats, prods, not; bool all;
        public Dictionary<string, object> Rule { get { if (rule == null) Prepare(); return rule; } }
        void Prepare()
        {
            var r = ShopJson.Obj(RuleJson); var sc = ShopJson.Sub(r, "scope");
            all = ShopJson.Bool(ShopJson.At(sc, "all")); cats = new HashSet<int>(ShopJson.List(ShopJson.At(sc, "cats")).Select(x => ShopJson.Int(x))); prods = new HashSet<int>(ShopJson.List(ShopJson.At(sc, "prods")).Select(x => ShopJson.Int(x)));
            not = new HashSet<int>(ShopJson.List(ShopJson.At(sc, "not")).Select(x => ShopJson.Int(x)));
            rule = r;
        }
        public bool Running(DateTime now) { return Active && (!StartsAt.HasValue || StartsAt.Value <= now) && (!EndsAt.HasValue || EndsAt.Value > now); }
        // a category in the scope covers its sub-categories
        public bool Covers(ShopProd p, ShopData d)
        {
            if (rule == null) Prepare();
            if (not.Contains(p.Id)) return false;
            return all || prods.Contains(p.Id) || (cats.Count > 0 && p.Cats.Any(x => { if (cats.Contains(x)) return true; ShopCat k; return d.CatById.TryGetValue(x, out k) && k.ParentId.HasValue && cats.Contains(k.ParentId.Value); }));
        }
    }

    public class ShopZone { public int Gov; public decimal? Fee, MinSub; public string Days; public bool Off; }
    public class ShopPost { public int Id; public string Slug, Title, TitleEn, Short, ShortEn, Image; public bool Hidden; public DateTime At; }
    public class ShopPage { public int Id, Sort; public string Slug, Kind, Title, TitleEn, Body, BodyEn; public bool Hidden, InMenu, InFooter; }

    public class ShopData
    {
        public Shop Row; public int Ver, StockVer; public DateTime At = DateTime.UtcNow, UsedAt = DateTime.UtcNow;
        public List<ShopProd> Products = new List<ShopProd>(); public Dictionary<int, ShopProd> ById = new Dictionary<int, ShopProd>(); public Dictionary<string, ShopProd> BySlug = new Dictionary<string, ShopProd>();
        public Dictionary<int, ShopVar> Vars = new Dictionary<int, ShopVar>();
        public List<ShopCat> Cats = new List<ShopCat>(); public Dictionary<int, ShopCat> CatById = new Dictionary<int, ShopCat>(); public Dictionary<string, ShopCat> CatBySlug = new Dictionary<string, ShopCat>();
        public Dictionary<int, ShopZone> Zones = new Dictionary<int, ShopZone>(); public List<ShopOffer> Offers = new List<ShopOffer>(); public List<ShopPage> Pages = new List<ShopPage>(); public List<ShopPost> Posts = new List<ShopPost>(); public List<object> Revs = new List<object>(), Ugc = new List<object>();
        public Dictionary<string, int> Redirects = new Dictionary<string, int>();   // "p:old-slug" -> product id
        public Dictionary<int, List<int>> CatOrder = new Dictionary<int, List<int>>(); // category -> its products in the owner's order (pinned first)
        public Dictionary<string, int> Df; public double AvgLen = 1;

        public IEnumerable<ShopProd> Shown { get { return Products.Where(p => p.Shown); } }
        public bool CatShown(ShopCat k) { return !k.LinkOnly && CatOpen(k); }
        // also the ones reached by their link only (for ads)
        public bool CatOpen(ShopCat k) { var now = DateTime.UtcNow; if (!k.InTime(now) || k.Count == 0) return false; ShopCat p; return !k.ParentId.HasValue || (CatById.TryGetValue(k.ParentId.Value, out p) && p.InTime(now)); }
        public string ImgUrl(string path, bool small = false) { return string.IsNullOrEmpty(path) ? "" : ShopImages.Url(path, small); }

        // The catalog's version for browsers and Cloudflare: changes with every edit, every stock move, and when an offer starts or ends by itself.
        public string CatalogVer(DateTime now)
        {
            int h = 17;
            foreach (var o in Offers) if (o.Running(now)) h = unchecked(h * 31 + o.Id);
            foreach (var k in Cats) if ((k.From.HasValue || k.To.HasValue) && k.InTime(now)) h = unchecked(h * 37 + k.Id);
            return Ver.ToString(CultureInfo.InvariantCulture) + "-" + StockVer.ToString(CultureInfo.InvariantCulture) + "-" + (h & 0xFFFFFF).ToString("x");
        }
        // when the next offer starts or ends by itself
        public DateTime? NextChange(DateTime now)
        {
            DateTime? n = null;
            foreach (var k in Cats) foreach (var t in new[] { k.From, k.To }) if (t.HasValue && t.Value > now && (!n.HasValue || t.Value < n.Value)) n = t;
            foreach (var o in Offers)
            {
                if (!o.Active) continue;
                foreach (var t in new[] { o.StartsAt, o.EndsAt }) if (t.HasValue && t.Value > now && (!n.HasValue || t.Value < n.Value)) n = t;
            }
            return n;
        }
        // a category with everything under it
        public HashSet<int> Under(int catId)
        {
            var set = new HashSet<int> { catId };
            foreach (var k in Cats) if (k.ParentId == catId) set.Add(k.Id);
            return set;
        }

        // ---------- search (the same words as the assistant's search: «عبايه» finds «عباية») ----------
        public List<ShopProd> Find(string query, int max, bool all = false, HashSet<int> cats = null)
        {
            var q = Search.Words(query).Distinct().ToList();
            var pool = (all ? Products : Shown.ToList()).Where(p => cats == null || p.Cats.Any(cats.Contains)).ToList();
            if (q.Count == 0) return cats != null ? pool.Take(max).ToList() : new List<ShopProd>();
            int n = Math.Max(1, Products.Count);
            var scored = new List<KeyValuePair<double, ShopProd>>();
            foreach (var p in pool)
            {
                double score = 0;
                foreach (var w in q)
                {
                    int tf, df;
                    if (!p.Words.TryGetValue(w, out tf))
                    {
                        // a part of a longer word ("كات" while typing "كاتل")
                        var part = w.Length >= 3 ? p.Words.Keys.FirstOrDefault(k => k.StartsWith(w, StringComparison.Ordinal)) : null;
                        if (part == null) continue;
                        score += 0.6; continue;
                    }
                    Df.TryGetValue(w, out df);
                    score += Math.Log(1 + (n - df + 0.5) / (df + 0.5)) * (tf * 2.2) / (tf + 1.2);
                }
                if (score > 0) scored.Add(new KeyValuePair<double, ShopProd>(score + (p.Available ? 0.05 : 0), p));
            }
            return scored.OrderByDescending(x => x.Key).ThenByDescending(x => x.Value.Sold).Take(max).Select(x => x.Value).ToList();
        }
    }

    public static class ShopCache
    {
        static readonly ConcurrentDictionary<int, ShopData> Cache = new ConcurrentDictionary<int, ShopData>();
        static readonly ConcurrentDictionary<int, object> Locks = new ConcurrentDictionary<int, object>();
        const int MaxStores = 60;   // the least used are dropped from memory

        public static void Touch(int storeId) { }   // the version in the database decides; nothing to do here
        public static void Forget(int storeId) { ShopData x; Cache.TryRemove(storeId, out x); }

        // always the current version: one short query for the version, and the catalog itself only when it changed
        public static ShopData Get(SqlConnection c, Shop row)
        {
            if (row == null) return null;
            int ver, stock;
            using (var cmd = Db.Cmd(c, "SELECT Ver, StockVer FROM dbo.Shops WHERE StoreId=@s", "@s", row.StoreId))
            using (var r = cmd.ExecuteReader()) { if (!r.Read()) return null; ver = (int)r[0]; stock = (int)r[1]; }
            ShopData d;
            if (Cache.TryGetValue(row.StoreId, out d) && d.Ver == ver)
            {
                d.UsedAt = DateTime.UtcNow;
                if (d.StockVer != stock) RefreshStock(c, d, stock);
                return d;
            }
            lock (Locks.GetOrAdd(row.StoreId, k => new object()))
            {
                if (Cache.TryGetValue(row.StoreId, out d) && d.Ver == ver) { if (d.StockVer != stock) RefreshStock(c, d, stock); return d; }
                d = Load(c, Shops.ByStore(c, row.StoreId) ?? row, ver, stock);
                Cache[row.StoreId] = d;
                if (Cache.Count > MaxStores) foreach (var kv in Cache.OrderBy(x => x.Value.UsedAt).Take(Cache.Count - MaxStores).ToList()) Forget(kv.Key);
                return d;
            }
        }
        public static ShopData Get(SqlConnection c, int storeId) { return Get(c, Shops.ByStore(c, storeId)); }

        static void RefreshStock(SqlConnection c, ShopData d, int stock)
        {
            using (var cmd = Db.Cmd(c, "SELECT Id, Stock FROM dbo.ShopVariants WHERE StoreId=@s; SELECT Id, Sold FROM dbo.ShopProducts WHERE StoreId=@s AND Sold>0", "@s", d.Row.StoreId))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) { ShopVar v; if (d.Vars.TryGetValue((int)r[0], out v)) v.Stock = (int)r[1]; }
                r.NextResult();
                while (r.Read()) { ShopProd p; if (d.ById.TryGetValue((int)r[0], out p)) p.Sold = (int)r[1]; }
            }
            ShopGoods.Load(c, d);
            Count(d);
            d.StockVer = stock;
        }

        static string S(object v) { return v as string; }
        static decimal? Dn(object v) { return v is decimal ? (decimal)v : (decimal?)null; }
        static DateTime? Dt(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }

        public static List<ShopOpt> ReadOptions(string json)
        {
            var list = new List<ShopOpt>();
            foreach (var o in ShopJson.Arr(json).OfType<Dictionary<string, object>>())
            {
                var n = ShopJson.List(ShopJson.At(o, "n")); var opt = new ShopOpt { Ar = ShopJson.Str(n.Count > 0 ? n[0] : null, 30), En = ShopJson.Str(n.Count > 1 ? n[1] : null, 30) };
                foreach (var v in ShopJson.List(ShopJson.At(o, "v")))
                {
                    var l = ShopJson.List(v);
                    opt.Vals.Add(new ShopOptVal { Ar = ShopJson.Str(l.Count > 0 ? l[0] : v, 40), En = ShopJson.Str(l.Count > 1 ? l[1] : null, 40), Hex = ShopJson.Str(l.Count > 2 ? l[2] : null, 9) });
                }
                if (opt.Ar.Length > 0 && opt.Vals.Count > 0) list.Add(opt);
            }
            return list;
        }

        static ShopData Load(SqlConnection c, Shop row, int ver, int stock)
        {
            var d = new ShopData { Row = row, Ver = ver, StockVer = stock };
            int s = row.StoreId;
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopCategories WHERE StoreId=@s ORDER BY Sort, Id", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var k = new ShopCat { Id = (int)r["Id"], ParentId = r["ParentId"] as int?, Slug = S(r["Slug"]), Name = S(r["Name"]), NameEn = S(r["NameEn"]), Descr = S(r["Descr"]), DescrEn = S(r["DescrEn"]), Image = S(r["Image"]), Cover = S(r["Cover"]), Sort = (int)r["Sort"],
                        Hidden = (bool)r["Hidden"], InMenu = (bool)r["InMenu"], InHome = (bool)r["InHome"], LinkOnly = (bool)r["LinkOnly"], From = Dt(r["ShowFrom"]), To = Dt(r["ShowTo"]), MetaTitle = S(r["MetaTitle"]), MetaDesc = S(r["MetaDesc"]), SizeGuideJson = S(r["SizeGuide"]) };
                    d.Cats.Add(k); d.CatById[k.Id] = k; d.CatBySlug[k.Slug] = k;
                }
            foreach (var k in d.Cats) { ShopCat p; if (k.ParentId.HasValue && d.CatById.TryGetValue(k.ParentId.Value, out p)) p.Kids.Add(k); }

            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopProducts WHERE StoreId=@s ORDER BY Sort, Id DESC", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var p = new ShopProd { Id = (int)r["Id"], Slug = S(r["Slug"]), Name = S(r["Name"]), NameEn = S(r["NameEn"]), Descr = S(r["Descr"]), DescrEn = S(r["DescrEn"]), Price = (decimal)r["Price"], OldPrice = Dn(r["OldPrice"]), Cost = Dn(r["Cost"]), Sku = S(r["Sku"]),
                        Unit = S(r["Unit"]), Brand = S(r["Brand"]), Tags = S(r["Tags"]), MinQty = Math.Max(1, (int)r["MinQty"]), MaxQty = r["MaxQty"] as int?, Track = (bool)r["Track"], OutMode = S(r["OutMode"]), Status = S(r["Status"]), SpecsJson = S(r["Specs"]),
                        SizeGuideJson = S(r["SizeGuide"]), ExtraJson = S(r["Extra"]), AskJson = S(r["Ask"]), Goes = S(r["Goes"]), Video = S(r["Video"]), MetaTitle = S(r["MetaTitle"]), MetaDesc = S(r["MetaDesc"]), Sold = (int)r["Sold"], Sort = (int)r["Sort"], Feat = (bool)r["Feat"], Weight = r["Weight"] as int?,
                        CreatedAt = Dt(r["CreatedAt"]).Value, UpdatedAt = Dt(r["UpdatedAt"]).Value, Options = ReadOptions(S(r["Options"])) };
                    d.Products.Add(p); d.ById[p.Id] = p; d.BySlug[p.Slug] = p;
                }
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopVariants WHERE StoreId=@s ORDER BY ProductId, Sort, Id", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    ShopProd p; if (!d.ById.TryGetValue((int)r["ProductId"], out p)) continue;
                    var v = new ShopVar { Id = (int)r["Id"], ProductId = p.Id, Opts = S(r["Opts"]), Price = Dn(r["Price"]), OldPrice = Dn(r["OldPrice"]), Cost = Dn(r["Cost"]), Stock = (int)r["Stock"], Sku = S(r["Sku"]), Image = S(r["Image"]), Active = (bool)r["Active"], Sort = (int)r["Sort"] };
                    p.Vars.Add(v); d.Vars[v.Id] = v;
                }
            using (var cmd = Db.Cmd(c, "SELECT ProductId, Path, W, H, Alt, Focus FROM dbo.ShopImages WHERE StoreId=@s AND ProductId IS NOT NULL ORDER BY ProductId, Sort, Id", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) { ShopProd p; if (d.ById.TryGetValue((int)r[0], out p)) p.Pics.Add(new ShopPic { Path = S(r[1]), W = (int)r[2], H = (int)r[3], Alt = S(r[4]), Focus = S(r[5]) }); }
            using (var cmd = Db.Cmd(c, "SELECT ProductId, CategoryId, IsMain FROM dbo.ShopProductCategories WHERE StoreId=@s ORDER BY CategoryId, Pinned DESC, Sort, ProductId DESC", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    ShopProd p; int pid = (int)r[0], cid = (int)r[1];
                    if (!d.ById.TryGetValue(pid, out p) || !d.CatById.ContainsKey(cid)) continue;
                    p.Cats.Add(cid); if ((bool)r[2]) p.MainCat = cid;
                    List<int> l; if (!d.CatOrder.TryGetValue(cid, out l)) d.CatOrder[cid] = l = new List<int>();
                    l.Add(pid);
                }
            foreach (var p in d.Products) if (p.MainCat == 0 && p.Cats.Count > 0) p.MainCat = p.Cats[0];
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopZones WHERE StoreId=@s", "@s", s))
            using (var r = cmd.ExecuteReader()) while (r.Read()) { var z = new ShopZone { Gov = (int)r["Gov"], Fee = Dn(r["Fee"]), Days = S(r["Days"]), MinSub = Dn(r["MinSub"]), Off = (bool)r["Closed"] }; d.Zones[z.Gov] = z; }
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopOffers WHERE StoreId=@s AND (EndsAt IS NULL OR EndsAt > DATEADD(DAY, -1, SYSUTCDATETIME())) ORDER BY Sort, Id", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) d.Offers.Add(new ShopOffer { Id = (int)r["Id"], Name = S(r["Name"]), NameEn = S(r["NameEn"]), Type = S(r["Type"]), RuleJson = S(r["RuleJson"]), Campaign = S(r["Campaign"]), LookJson = S(r["Look"]), StartsAt = Dt(r["StartsAt"]), EndsAt = Dt(r["EndsAt"]), Active = (bool)r["Active"], Countdown = (bool)r["Countdown"], Sort = (int)r["Sort"] });
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopPages WHERE StoreId=@s ORDER BY Sort, Id", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) d.Pages.Add(new ShopPage { Id = (int)r["Id"], Slug = S(r["Slug"]), Kind = S(r["Kind"]), Title = S(r["Title"]), TitleEn = S(r["TitleEn"]), Body = S(r["Body"]), BodyEn = S(r["BodyEn"]), Hidden = (bool)r["Hidden"], InMenu = (bool)r["InMenu"], InFooter = (bool)r["InFooter"], Sort = (int)r["Sort"] });
            using (var cmd = Db.Cmd(c, "SELECT Kind, OldSlug, TargetId FROM dbo.ShopRedirects WHERE StoreId=@s", "@s", s))
            using (var r = cmd.ExecuteReader()) while (r.Read()) d.Redirects[S(r[0]) + ":" + S(r[1])] = (int)r[2];

            using (var cmd = Db.Cmd(c, "SELECT TOP 100 Id, Slug, Title, TitleEn, LEFT(Body, 400) AS B, LEFT(BodyEn, 400) AS BE, Image, Hidden, At FROM dbo.ShopPosts WHERE StoreId=@s ORDER BY At DESC, Id DESC", "@s", s))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) d.Posts.Add(new ShopPost { Id = (int)r["Id"], Slug = S(r["Slug"]), Title = S(r["Title"]), TitleEn = S(r["TitleEn"]), Short = ShopPages.Plain(S(r["B"]), 160), ShortEn = ShopPages.Plain(S(r["BE"]), 160), Image = S(r["Image"]), Hidden = (bool)r["Hidden"], At = Dt(r["At"]).Value });
            ShopReviews.Load(c, d); ShopReviews.LoadPics(c, d);
            ShopGoods.Load(c, d);

            Count(d);
            Index(d);
            return d;
        }

        static void Count(ShopData d)
        {
            var by = new Dictionary<int, HashSet<int>>();
            foreach (var p in d.Products)
            {
                if (!p.Shown) continue;
                foreach (var cid in p.Cats)
                {
                    HashSet<int> h; if (!by.TryGetValue(cid, out h)) by[cid] = h = new HashSet<int>(); h.Add(p.Id);
                    ShopCat k; if (d.CatById.TryGetValue(cid, out k) && k.ParentId.HasValue) { if (!by.TryGetValue(k.ParentId.Value, out h)) by[k.ParentId.Value] = h = new HashSet<int>(); h.Add(p.Id); }
                }
            }
            foreach (var k in d.Cats) { HashSet<int> h; k.Count = by.TryGetValue(k.Id, out h) ? h.Count : 0; }
        }

        static void Index(ShopData d)
        {
            d.Df = new Dictionary<string, int>(); long total = 0;
            foreach (var p in d.Products)
            {
                p.Words = new Dictionary<string, int>();
                Action<string, int> add = (t, weight) => { foreach (var w in Search.Words(t)) { int n; p.Words[w] = (p.Words.TryGetValue(w, out n) ? n : 0) + weight; } };
                add(p.Name, 4); add(p.NameEn, 4); add(p.Brand, 2); add(p.Tags, 3); add(p.Sku, 2);
                foreach (var cid in p.Cats) { ShopCat k; if (d.CatById.TryGetValue(cid, out k)) { add(k.Name, 2); add(k.NameEn, 2); if (k.ParentId.HasValue && d.CatById.TryGetValue(k.ParentId.Value, out k)) { add(k.Name, 1); add(k.NameEn, 1); } } }
                foreach (var o in p.Options) foreach (var v in o.Vals) { add(v.Ar, 1); add(v.En, 1); }
                foreach (var sp in ShopJson.Arr(p.SpecsJson)) { var l = ShopJson.List(sp); foreach (var x in l) add(ShopJson.Str(x, 100), 1); }
                add(p.Descr != null && p.Descr.Length > 1500 ? p.Descr.Substring(0, 1500) : p.Descr, 1);
                foreach (var w in p.Words.Keys) { int n; d.Df[w] = d.Df.TryGetValue(w, out n) ? n + 1 : 1; }
                total += p.Words.Count;
            }
            d.AvgLen = d.Products.Count == 0 ? 1 : (double)total / d.Products.Count;
        }
    }

    // ---------- the catalog as the store's pages read it ----------
    public static class ShopView
    {
        public static string[] Pair(string ar, string en) { return new[] { ar ?? "", en ?? "" }; }
        public static long Unix(DateTime t) { return (long)(t - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
        static object[] Pic(ShopPic x) { return string.IsNullOrEmpty(x.Focus) ? new object[] { x.Path, x.W, x.H, x.Alt ?? "" } : new object[] { x.Path, x.W, x.H, x.Alt ?? "", x.Focus }; }

        public static Dictionary<string, object> Summary(ShopData d, ShopProd p, DateTime now, Dictionary<string, object> badges, HashSet<int> best)
        {
            ShopOffer of; decimal lo = decimal.MaxValue, hi = 0, pr = 0, old = 0; bool first = true;
            ShopOffer used = null;
            foreach (var v in p.Vars.Where(x => x.Active).DefaultIfEmpty(null))
            {
                decimal? o; var x = ShopPrice.Unit(d, p, v, now, out o, out of);
                if (x < lo) lo = x;
                if (x > hi) hi = x;
                if (first || x < pr || (x == pr && (o ?? 0) > old)) { pr = x; old = o ?? 0; used = of; first = false; }
            }
            var b = new List<string>();
            if (old > pr && ShopJson.Bool(ShopJson.At(badges, "sale"), true)) b.Add("sale");
            if (ShopJson.Bool(ShopJson.At(badges, "new"), true) && (now - p.CreatedAt).TotalDays < ShopJson.Int(ShopJson.At(badges, "newDays"), 14)) b.Add("new");
            if (best.Contains(p.Id) && ShopJson.Bool(ShopJson.At(badges, "best"), true)) b.Add("best");
            if (p.Track && p.OutMode != "order" && p.Available && p.Total <= ShopJson.Int(ShopJson.At(badges, "lowAt"), 3) && ShopJson.Bool(ShopJson.At(badges, "low"), true)) b.Add("low");
            var r = new Dictionary<string, object>
            {
                { "i", p.Id }, { "s", p.Slug }, { "n", Pair(p.Name, p.NameEn) }, { "pr", pr }, { "c", p.Cats }, { "mc", p.MainCat }, { "im", p.Pics.Take(2).Select(Pic).ToArray() }, { "av", p.Available }, { "t", Unix(p.CreatedAt) }, { "sd", p.Sold }
            };
            if (old > pr) r["old"] = old;
            if (hi > lo) { r["lo"] = lo; r["hi"] = hi; }
            if (p.Options.Count > 0)
            {
                r["o"] = p.Options.Select(o => new Dictionary<string, object> { { "n", Pair(o.Ar, o.En) }, { "v", o.Vals.Select(v => new[] { v.Ar, v.En ?? "", v.Hex ?? "" }).ToArray() } }).ToArray();
            }
            // every size and colour: its id, values, can it be bought, price, old price, picture, quantity (null = not counted)
            bool count = p.Track && p.OutMode != "order";
            r["vs"] = p.Vars.Where(v => v.Active).Select(v => { decimal? o; ShopOffer x; var u = ShopPrice.Unit(d, p, v, now, out o, out x); return new object[] { v.Id, v.Opts, p.Can(v) ? 1 : 0, u, o ?? 0, v.Image ?? "", count ? (object)Math.Max(0, v.Stock) : null }; }).ToArray();
            if (count) r["st"] = p.Total;
            if (p.OutMode == "order") r["om"] = "order";
            if (b.Count > 0) r["b"] = b;
            if (p.MinQty > 1) r["mn"] = p.MinQty;
            if (p.MaxQty.HasValue) r["mx"] = p.MaxQty.Value;
            if (!string.IsNullOrEmpty(p.Unit)) r["un"] = p.Unit;
            if (!string.IsNullOrEmpty(p.Brand)) r["br"] = p.Brand;
            if (!string.IsNullOrEmpty(p.Tags)) r["tg"] = p.Tags;
            if (p.Feat) r["ft"] = true;
            if (p.Stars != null) { r["rt"] = p.Rating; r["rv"] = p.Reviews; }
            if (!string.IsNullOrEmpty(p.Goes)) r["gw"] = GoesIds(p);
            if (!string.IsNullOrEmpty(p.AskJson)) r["ask"] = ShopJson.Arr(p.AskJson);
            if (used != null) { r["of"] = used.Id; if (used.EndsAt.HasValue && used.Countdown) r["u"] = Unix(used.EndsAt.Value); }
            if (!string.IsNullOrEmpty(p.SpecsJson)) r["sp"] = ShopJson.Arr(p.SpecsJson).Take(14);
            if (!string.IsNullOrEmpty(p.ExtraJson)) r["x"] = ShopJson.Obj(p.ExtraJson);
            ShopGoods.Card(d, p, r);
            return r;
        }

        public static Dictionary<string, object> Detail(ShopData d, ShopProd p, DateTime now)
        {
            ShopOffer of;
            var r = new Dictionary<string, object>
            {
                { "i", p.Id }, { "d", Pair(p.Descr, p.DescrEn) }, { "im", p.Pics.Select(Pic).ToArray() }, { "sp", ShopJson.Arr(p.SpecsJson) }, { "sku", p.Sku ?? "" },
                { "vars", p.Vars.Where(v => v.Active).Select(v => { decimal? old; var u = ShopPrice.Unit(d, p, v, now, out old, out of);
                    return new Dictionary<string, object> { { "i", v.Id }, { "o", v.Vals }, { "pr", u }, { "old", old ?? 0 }, { "st", p.Track && p.OutMode != "order" ? Math.Max(0, v.Stock) : -1 }, { "can", p.Can(v) }, { "im", v.Image ?? "" }, { "sku", v.Sku ?? "" } }; }).ToArray() },
                { "rel", Related(d, p).Select(x => x.Id).ToArray() }
            };
            if (p.Stars != null) r["rs"] = p.Stars;
            if (!string.IsNullOrEmpty(p.Video)) r["vd"] = p.Video;
            var guide = SizeGuide(d, p);
            if (guide != null) r["sg"] = guide;
            ShopGoods.Detail(d, p, r);
            return r;
        }
        // the product's own size guide, or its category's (or the parent category's)
        static object SizeGuide(ShopData d, ShopProd p)
        {
            if (!string.IsNullOrEmpty(p.SizeGuideJson)) return ShopJson.Obj(p.SizeGuideJson);
            foreach (var cid in new[] { p.MainCat }.Concat(p.Cats))
            {
                ShopCat k; if (!d.CatById.TryGetValue(cid, out k)) continue;
                if (!string.IsNullOrEmpty(k.SizeGuideJson)) return ShopJson.Obj(k.SizeGuideJson);
                if (k.ParentId.HasValue && d.CatById.TryGetValue(k.ParentId.Value, out k) && !string.IsNullOrEmpty(k.SizeGuideJson)) return ShopJson.Obj(k.SizeGuideJson);
            }
            return null;
        }
        public static int[] GoesIds(ShopProd p) { return (p.Goes ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => ShopJson.Int(x)).Where(x => x > 0).ToArray(); }
        // what goes with it: the owner's choice, without what is sold out
        public static IEnumerable<ShopProd> With(ShopData d, ShopProd p) { return GoesIds(p).Select(i => { ShopProd x; return d.ById.TryGetValue(i, out x) ? x : null; }).Where(x => x != null && x.Shown && x.Available); }
        // the same category first, then the closest price
        public static IEnumerable<ShopProd> Related(ShopData d, ShopProd p, int n = 8)
        {
            return d.Shown.Where(x => x.Id != p.Id && x.Available).Select(x => new { x, k = (x.Cats.Intersect(p.Cats).Count() * 3) + (x.MainCat == p.MainCat && p.MainCat != 0 ? 2 : 0) - (double)(Math.Abs(x.Price - p.Price) / (p.Price + 1)) })
                .OrderByDescending(a => a.k).ThenByDescending(a => a.x.Sold).Take(n).Select(a => a.x);
        }
        public static HashSet<int> Best(ShopData d) { return new HashSet<int>(d.Shown.Where(p => p.Sold > 0).OrderByDescending(p => p.Sold).Take(Math.Max(4, d.Products.Count / 10)).Select(p => p.Id)); }

        public static Dictionary<string, object> Category(ShopData d, ShopCat k)
        {
            return new Dictionary<string, object> { { "i", k.Id }, { "s", k.Slug }, { "n", Pair(k.Name, k.NameEn) }, { "p", k.ParentId ?? 0 }, { "im", k.Image ?? "" }, { "cv", k.Cover ?? "" }, { "d", Pair(k.Descr, k.DescrEn) }, { "k", k.Count }, { "h", k.InHome && !k.LinkOnly }, { "m", k.InMenu && !k.LinkOnly }, { "lo", k.LinkOnly } };
        }

        // everything the pages list: categories and products (immutable for its version, so browsers keep it)
        public static Dictionary<string, object> Catalog(ShopData d, DateTime now)
        {
            var badges = ShopJson.Sub(d.Row.Look(false), "badges"); var best = Best(d);
            var order = new Dictionary<string, object>();
            foreach (var kv in d.CatOrder) order[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            return new Dictionary<string, object>
            {
                { "v", d.CatalogVer(now) }, { "c", d.Cats.Where(d.CatOpen).Select(k => Category(d, k)).ToArray() },
                { "p", d.Shown.Select(p => Summary(d, p, now, badges, best)).ToArray() }, { "co", order },
                { "of", d.Offers.Where(o => o.Running(now)).Select(o => OfferJson(o)).ToArray() }
            };
        }

        public static Dictionary<string, object> OfferJson(ShopOffer o)
        {
            return new Dictionary<string, object> { { "i", o.Id }, { "n", Pair(o.Name, o.NameEn) }, { "t", o.Type }, { "r", o.Rule }, { "cp", o.Campaign ?? "" }, { "lk", ShopJson.Obj(o.LookJson) }, { "u", o.EndsAt.HasValue ? Unix(o.EndsAt.Value) : 0 }, { "cd", o.Countdown } };
        }
    }
}
