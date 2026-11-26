// «نقل منتجات من متجر قديم»: the products of a store the owner had elsewhere become this store's products.
//   Shopify   the products CSV it exports (Products > Export). Both header styles: the old one (Handle, Body (HTML), Variant Price,
//             Image Src...) and the one Shopify writes now (URL handle, Description, Price, Product image URL...). Rows with the same
//             handle are one product: its sizes, then rows with only a picture.
//   WooCommerce  its products CSV (a «variable» product and its «variation» rows).
//   EasyOrders   read from its API with the key the store already linked (or one the owner pastes, used for this read only).
// First a preview (what comes in, what is skipped and why), nothing is written; then the owner presses «انقل» and it runs in the
// background (ShopGoods' lane picks up one that stopped half way): products first, then their pictures a few at a time through
// ShopFetch (the address checked, the bytes checked, shrunk like an upload). A report at the end says what came and what didn't.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class MoveVar { public List<string> O { get; set; } = new List<string>(); public decimal Price { get; set; } public decimal Old { get; set; } public decimal Cost { get; set; } public string Sku { get; set; } public string Img { get; set; } public int? Qty { get; set; } public int Grams { get; set; } }
    public class MovePic { public string Url { get; set; } public string Alt { get; set; } }
    public class MoveProd
    {
        public string Ref { get; set; } public string Name { get; set; } public string Descr { get; set; } public string Vendor { get; set; } public string Type { get; set; } public string Cat { get; set; } public string Coll { get; set; } public string Skip { get; set; }
        public List<string> Tags { get; set; } = new List<string>(); public bool Hidden { get; set; } public bool Track { get; set; } public bool Cont { get; set; }
        public List<string> Opts { get; set; } = new List<string>(); public List<MoveVar> Vars { get; set; } = new List<MoveVar>(); public List<MovePic> Pics { get; set; } = new List<MovePic>(); public List<string> Notes { get; set; } = new List<string>();
    }

    public static class ShopMove
    {
        static readonly JsonSerializerOptions Js = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        static string Cut(string s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s.Substring(0, n) : s; }
        static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        static decimal Money(string v) { return ShopJson.Dec(Regex.Replace(ShopJson.Digits(v ?? ""), @"[^\d.\-]", "")); }

        // ---------- HTML -> the store's text (lines, «- » lists, «## » headings; nothing else of the markup survives) ----------
        public static string Text(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var s = Regex.Replace(html, @"<(script|style|template|iframe|object|svg)[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline);
            s = Regex.Replace(s, @"<\s*li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*h[1-3][^>]*>", "\n## ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*br\s*/?\s*>|<\s*/\s*(p|div|h[1-6]|tr|ul|ol|table|section|article|blockquote)\s*>|<\s*(p|div|tr|table)[^>]*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]*>", "");
            s = HttpUtility.HtmlDecode(HttpUtility.HtmlDecode(s)).Replace(' ', ' ').Replace("<", "‹").Replace(">", "›");
            var lines = s.Replace("\r", "").Split('\n').Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim()).ToList();
            var sb = new StringBuilder(); int blank = 0;
            foreach (var l in lines)
            {
                if (l.Length == 0 || l == "-" || l == "##") { if (sb.Length > 0) blank++; continue; }
                if (sb.Length > 0) sb.Append(blank > 0 && !l.StartsWith("- ") ? "\n\n" : "\n"); sb.Append(l); blank = 0;
            }
            return Cut(sb.ToString(), 8000);
        }

        // ---------- reading the files ----------
        static readonly Dictionary<string, string[]> Cols = new Dictionary<string, string[]> {
            { "handle", new[] { "handle", "url handle" } }, { "title", new[] { "title" } }, { "body", new[] { "body (html)", "description", "body html" } }, { "vendor", new[] { "vendor" } },
            { "pcat", new[] { "product category", "standard product type", "category" } }, { "type", new[] { "type", "custom product type" } }, { "tags", new[] { "tags" } }, { "coll", new[] { "collection" } },
            { "pub", new[] { "published", "published on online store" } }, { "status", new[] { "status" } }, { "o1n", new[] { "option1 name" } }, { "o1v", new[] { "option1 value" } }, { "o2n", new[] { "option2 name" } }, { "o2v", new[] { "option2 value" } },
            { "o3n", new[] { "option3 name" } }, { "o3v", new[] { "option3 value" } }, { "sku", new[] { "variant sku", "sku" } }, { "grams", new[] { "variant grams", "weight value (grams)" } },
            { "tracker", new[] { "variant inventory tracker", "inventory tracker" } }, { "qty", new[] { "variant inventory qty", "inventory quantity" } }, { "policy", new[] { "variant inventory policy", "continue selling when out of stock" } },
            { "price", new[] { "variant price", "price" } }, { "compare", new[] { "variant compare at price", "compare-at price" } }, { "cost", new[] { "cost per item" } },
            { "img", new[] { "image src", "product image url" } }, { "alt", new[] { "image alt text" } }, { "vimg", new[] { "variant image", "variant image url" } } };

        static Dictionary<string, int> Heads(string[] h, Dictionary<string, string[]> cols)
        {
            var m = new Dictionary<string, int>();
            for (int j = 0; j < h.Length; j++) { var t = (h[j] ?? "").Trim().ToLowerInvariant(); foreach (var kv in cols) if (!m.ContainsKey(kv.Key) && kv.Value.Contains(t)) m[kv.Key] = j; }
            return m;
        }

        public static List<MoveProd> Shopify(List<string[]> rows)
        {
            var col = Heads(rows[0], Cols); Func<string[], string, string> cell = (r, k) => { int j; return col.TryGetValue(k, out j) && j < r.Length ? (r[j] ?? "").Trim() : ""; };
            var list = new List<MoveProd>(); var by = new Dictionary<string, MoveProd>(StringComparer.OrdinalIgnoreCase); var imgs = new Dictionary<MoveProd, HashSet<string>>();
            foreach (var r in rows.Skip(1))
            {
                if (r.All(string.IsNullOrWhiteSpace)) continue;
                string h = cell(r, "handle"); if (h.Length == 0) h = Shops.Slugify(cell(r, "title")); if (h.Length == 0) continue;
                MoveProd p; if (!by.TryGetValue(h, out p))
                {
                    by[h] = p = new MoveProd { Ref = Cut(h, 150), Name = Cut(cell(r, "title"), 150), Descr = Text(cell(r, "body")), Vendor = Cut(cell(r, "vendor"), 60), Type = Cut(cell(r, "type"), 80), Cat = Cut(cell(r, "pcat"), 300), Coll = Cut(cell(r, "coll"), 80) };
                    p.Tags = cell(r, "tags").Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries).Select(t => Cut(t, 40)).Where(t => t.Length > 0).Distinct().Take(30).ToList();
                    string st = cell(r, "status").ToLowerInvariant(), pub = cell(r, "pub").ToLowerInvariant(); p.Hidden = st == "draft" || st == "archived" || pub == "false" || pub == "0" || pub == "no";
                    for (int i = 1; i <= 3; i++) { var n = cell(r, "o" + i + "n"); if (n.Length > 0) p.Opts.Add(Cut(n, 30)); }
                    if (p.Opts.Count == 1 && p.Opts[0].Equals("Title", StringComparison.OrdinalIgnoreCase) && cell(r, "o1v").Equals("Default Title", StringComparison.OrdinalIgnoreCase)) p.Opts.Clear();
                    list.Add(p); imgs[p] = new HashSet<string>();
                }
                else if (p.Name.Length == 0 && cell(r, "title").Length > 0) p.Name = Cut(cell(r, "title"), 150);
                // a size: the row has a price or an option value
                var vals = Enumerable.Range(1, p.Opts.Count).Select(i => Cut(cell(r, "o" + i + "v"), 40).Replace("|", "/")).ToList();
                if (cell(r, "price").Length > 0 && (p.Opts.Count == 0 ? p.Vars.Count == 0 : vals.All(v => v.Length > 0)))
                {
                    int q; var v = new MoveVar { O = vals, Price = Money(cell(r, "price")), Old = Money(cell(r, "compare")), Cost = Money(cell(r, "cost")), Sku = Cut(cell(r, "sku"), 60), Img = cell(r, "vimg"), Grams = (int)Math.Max(0, Math.Min(1000000, Money(cell(r, "grams")))) };
                    if (int.TryParse(ShopJson.Digits(cell(r, "qty")), NumberStyles.Integer, CultureInfo.InvariantCulture, out q)) v.Qty = Math.Max(0, q);
                    string tr = cell(r, "tracker").ToLowerInvariant(); if (col.ContainsKey("tracker") ? tr.Length > 0 : v.Qty.HasValue) p.Track = true;
                    if (cell(r, "policy").ToLowerInvariant() == "continue") p.Cont = true;
                    if (!p.Vars.Any(x => x.O.SequenceEqual(v.O))) p.Vars.Add(v);
                }
                var src = cell(r, "img"); if (src.Length > 0 && imgs[p].Add(src)) p.Pics.Add(new MovePic { Url = src, Alt = Cut(cell(r, "alt"), 160) });
            }
            return list;
        }

        static readonly Dictionary<string, string[]> Woo = new Dictionary<string, string[]> {
            { "id", new[] { "id" } }, { "type", new[] { "type" } }, { "sku", new[] { "sku" } }, { "name", new[] { "name" } }, { "pub", new[] { "published" } }, { "short", new[] { "short description" } }, { "body", new[] { "description" } },
            { "regular", new[] { "regular price" } }, { "sale", new[] { "sale price" } }, { "stock", new[] { "stock" } }, { "back", new[] { "backorders allowed?" } }, { "kg", new[] { "weight (kg)" } }, { "cats", new[] { "categories" } }, { "tags", new[] { "tags" } },
            { "imgs", new[] { "images" } }, { "parent", new[] { "parent" } }, { "a1n", new[] { "attribute 1 name" } }, { "a1v", new[] { "attribute 1 value(s)" } }, { "a2n", new[] { "attribute 2 name" } }, { "a2v", new[] { "attribute 2 value(s)" } }, { "a3n", new[] { "attribute 3 name" } }, { "a3v", new[] { "attribute 3 value(s)" } } };

        public static List<MoveProd> WooCommerce(List<string[]> rows)
        {
            var col = Heads(rows[0], Woo); Func<string[], string, string> cell = (r, k) => { int j; return col.TryGetValue(k, out j) && j < r.Length ? (r[j] ?? "").Trim() : ""; };
            var list = new List<MoveProd>(); var byId = new Dictionary<string, MoveProd>(StringComparer.OrdinalIgnoreCase);
            Func<string[], MoveVar> price = r => { decimal reg = Money(cell(r, "regular")), sale = Money(cell(r, "sale")); int q; var v = new MoveVar { Price = sale > 0 && (reg <= 0 || sale < reg) ? sale : reg, Old = sale > 0 && sale < reg ? reg : 0, Sku = Cut(cell(r, "sku"), 60), Grams = (int)Math.Max(0, Math.Min(1000000, Money(cell(r, "kg")) * 1000)) }; if (int.TryParse(ShopJson.Digits(cell(r, "stock")), NumberStyles.Integer, CultureInfo.InvariantCulture, out q)) v.Qty = Math.Max(0, q); return v; };
            Func<string, List<MovePic>> pics = s => s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().Select(x => new MovePic { Url = x }).ToList();
            var vars = new List<string[]>();
            foreach (var r in rows.Skip(1))
            {
                if (r.All(string.IsNullOrWhiteSpace)) continue;
                string type = cell(r, "type").ToLowerInvariant();
                if (type.Contains("variation")) { vars.Add(r); continue; }
                var p = new MoveProd { Ref = Cut(cell(r, "id").Length > 0 ? "id:" + cell(r, "id") : cell(r, "sku").Length > 0 ? cell(r, "sku") : Shops.Slugify(cell(r, "name")), 150), Name = Cut(cell(r, "name"), 150), Descr = Text(cell(r, "body").Length > 0 ? cell(r, "body") : cell(r, "short")), Pics = pics(cell(r, "imgs")) };
                var cats = cell(r, "cats").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); if (cats.Count > 0) p.Cat = Cut(cats[0], 300);
                p.Tags = cell(r, "tags").Split(',').Select(t => Cut(t, 40)).Where(t => t.Length > 0).Distinct().Take(30).ToList();
                string pub = cell(r, "pub"); p.Hidden = pub == "0" || pub == "-1"; p.Cont = cell(r, "back") == "1" || cell(r, "back").ToLowerInvariant() == "notify";
                if (type.Contains("variable")) { for (int i = 1; i <= 3; i++) if (cell(r, "a" + i + "n").Length > 0) p.Opts.Add(Cut(cell(r, "a" + i + "n"), 30)); }
                else { var v = price(r); p.Vars.Add(v); p.Track = v.Qty.HasValue; }
                list.Add(p); if (cell(r, "id").Length > 0) byId["id:" + cell(r, "id")] = p; if (cell(r, "sku").Length > 0) byId[cell(r, "sku")] = p;
            }
            foreach (var r in vars)
            {
                MoveProd p; if (!byId.TryGetValue(cell(r, "parent"), out p) || p.Opts.Count == 0) continue;
                var v = price(r); var named = Enumerable.Range(1, 3).Select(i => new { n = cell(r, "a" + i + "n"), v = cell(r, "a" + i + "v") }).Where(x => x.n.Length > 0).ToList();
                v.O = p.Opts.Select(o => Cut((named.FirstOrDefault(x => x.n.Equals(o, StringComparison.OrdinalIgnoreCase)) ?? new { n = "", v = "" }).v, 40).Replace("|", "/")).ToList();
                if (v.O.Any(x => x.Length == 0)) continue;
                var ims = pics(cell(r, "imgs")); if (ims.Count > 0) v.Img = ims[0].Url;
                if (v.Qty.HasValue) p.Track = true;
                if (!p.Vars.Any(x => x.O.SequenceEqual(v.O))) p.Vars.Add(v);
            }
            return list;
        }

        // one EasyOrders product (fields as their API gives them: name, price / sale_price, description, sku, thumb, images, quantity,
        // track_stock, disable_orders_for_no_stock, categories, variations{props}, variants{variation_props})
        public static MoveProd FromEo(JsonElement e)
        {
            Func<JsonElement, string, string> s = (x, k) => EasyOrders.Str(x, k); Func<JsonElement, string, decimal> n = (x, k) => EasyOrders.Num(x, k) ?? 0;
            var p = new MoveProd { Ref = Cut(s(e, "id") ?? s(e, "slug") ?? s(e, "name"), 150), Name = Cut(s(e, "name"), 150), Descr = Text(s(e, "description")), Hidden = EasyOrders.Bool(e, "hidden"), Track = EasyOrders.Bool(e, "track_stock"), Cont = !EasyOrders.Bool(e, "disable_orders_for_no_stock") && EasyOrders.Bool(e, "track_stock") };
            var cats = EasyOrders.Arr(e, "categories").Select(x => s(x, "name")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(); if (cats.Count > 0) p.Cat = Cut(cats[0], 300);
            var seen = new HashSet<string>(); Action<string> pic = u => { if (!string.IsNullOrWhiteSpace(u) && seen.Add(u.Trim())) p.Pics.Add(new MovePic { Url = u.Trim() }); };
            pic(s(e, "thumb")); JsonElement im; if (e.TryGetProperty("images", out im) && im.ValueKind == JsonValueKind.Array) foreach (var x in im.EnumerateArray()) pic(x.ValueKind == JsonValueKind.String ? x.GetString() : s(x, "url") ?? s(x, "src"));
            Func<JsonElement, JsonElement?, MoveVar> price = (v, parent) => { decimal pr = n(v, "price"), sale = n(v, "sale_price"); if (pr <= 0 && parent.HasValue) { pr = n(parent.Value, "price"); if (sale <= 0) sale = n(parent.Value, "sale_price"); } var q = EasyOrders.Num(v, "quantity"); return new MoveVar { Price = sale > 0 && (pr <= 0 || sale < pr) ? sale : pr, Old = sale > 0 && sale < pr ? pr : 0, Sku = Cut(s(v, "sku"), 60), Qty = q.HasValue ? (int?)Math.Max(0, (int)q.Value) : null }; };
            var labels = new Dictionary<string, string>();
            foreach (var v in EasyOrders.Arr(e, "variations"))
            {
                string vn = Cut(s(v, "name"), 30); if (vn.Length == 0) continue; p.Opts.Add(vn);
                foreach (var pr in EasyOrders.Arr(v, "props")) { string lbl = s(pr, "name"), val = s(pr, "value"); if (!string.IsNullOrWhiteSpace(val)) labels[vn + "|" + val.Trim()] = !string.IsNullOrWhiteSpace(lbl) ? lbl.Trim() : val.Trim(); }
            }
            var variants = EasyOrders.Arr(e, "variants");
            if (variants.Count == 0 || p.Opts.Count == 0) { p.Opts.Clear(); p.Vars.Add(price(e, null)); }
            else foreach (var v in variants)
                {
                    var mv = price(v, e); var by = EasyOrders.Arr(v, "variation_props").ToDictionary(x => (s(x, "variation") ?? "").Trim(), x => (s(x, "variation_prop") ?? "").Trim());
                    mv.O = p.Opts.Select(o => { string val; if (!by.TryGetValue(o, out val)) return ""; string lbl; return Cut(labels.TryGetValue(o + "|" + val, out lbl) ? lbl : val, 40).Replace("|", "/"); }).ToList();
                    if (mv.O.All(x => x.Length > 0) && !p.Vars.Any(x => x.O.SequenceEqual(mv.O))) p.Vars.Add(mv);
                }
            if (p.Opts.Count > 3) { p.Notes.Add("فيه أكتر من 3 اختيارات، اتنقل أول 3"); p.Opts = p.Opts.Take(3).ToList(); foreach (var v in p.Vars) v.O = v.O.Take(3).ToList(); p.Vars = p.Vars.GroupBy(v => string.Join("|", v.O)).Select(g => g.First()).ToList(); }
            return p;
        }

        // ---------- the preview ----------
        static void Check(List<MoveProd> l)
        {
            foreach (var p in l)
            {
                if (p.Name.Length < 2) { p.Skip = "مفيش اسم"; continue; }
                p.Vars = p.Vars.Where(v => v.Price >= 1).ToList();
                if (p.Vars.Count == 0) { p.Skip = "مفيش سعر"; continue; }
                if (p.Opts.Count > 3) { p.Notes.Add("فيه أكتر من 3 اختيارات، اتنقل أول 3"); p.Opts = p.Opts.Take(3).ToList(); }
                if (p.Opts.Count > 0 && p.Opts.Select(o => o.ToLowerInvariant()).Distinct().Count() != p.Opts.Count) { p.Skip = "اسمين اختيارات متكررين"; continue; }
                if (p.Vars.Count > Shops.MaxVariants) { p.Notes.Add("اتنقل أول " + Shops.MaxVariants + " مقاس ولون من " + p.Vars.Count); p.Vars = p.Vars.Take(Shops.MaxVariants).ToList(); }
                var vi = p.Vars.Select(v => v.Img).Where(u => !string.IsNullOrWhiteSpace(u) && !p.Pics.Any(x => x.Url == u)).Distinct().ToList(); foreach (var u in vi) p.Pics.Add(new MovePic { Url = u });
                if (p.Pics.Count > Shops.MaxImages) { p.Notes.Add("اتنقل أول " + Shops.MaxImages + " صور من " + p.Pics.Count); p.Pics = p.Pics.Take(Shops.MaxImages).ToList(); }
            }
        }
        static List<string> Paths(MoveProd p, string cats)
        {
            if (cats == "none") return new List<string>();
            if (cats == "tags") return p.Tags.Take(3).ToList();
            var l = new List<string>();
            if (!string.IsNullOrWhiteSpace(p.Type)) l.Add(p.Type);
            else if (!string.IsNullOrWhiteSpace(p.Cat)) { var parts = p.Cat.Split(new[] { '>', '›', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 1).ToList(); if (parts.Count > 0) l.Add(string.Join(" > ", parts.Skip(Math.Max(0, parts.Count - 2)))); }
            if (!string.IsNullOrWhiteSpace(p.Coll) && !l.Contains(p.Coll)) l.Add(p.Coll);
            return l;
        }
        static Dictionary<string, object> Preview(SqlConnection c, Shop sh, string src, List<MoveProd> l)
        {
            var d = ShopCache.Get(c, sh); var map = Map(c, sh.StoreId, src); var sku = Skus(d);
            int dup = l.Count(p => p.Skip == null && Find(d, map, sku, p) != null), ok = l.Count(p => p.Skip == null), room = Math.Max(0, Shops.MaxProducts - d.Products.Count);
            var cats = l.Where(p => p.Skip == null).SelectMany(p => Paths(p, "type")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var known = new HashSet<string>(d.Cats.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, object>
            {
                { "products", l.Count }, { "ok", ok }, { "vars", l.Where(p => p.Skip == null).Sum(p => p.Opts.Count == 0 ? 0 : p.Vars.Count) }, { "pics", l.Where(p => p.Skip == null).Sum(p => p.Pics.Count) }, { "hidden", l.Count(p => p.Skip == null && p.Hidden) },
                { "dup", dup }, { "room", room }, { "over", Math.Max(0, ok - dup - room) }, { "cats", cats.Count }, { "newCats", cats.Count(k => !known.Contains(k.Split('>').Last().Trim())) }, { "tags", l.Any(p => p.Tags.Count > 0) },
                { "skip", l.Where(p => p.Skip != null).Take(60).Select(p => new object[] { p.Name.Length > 0 ? p.Name : p.Ref, p.Skip }).ToArray() }, { "skipN", l.Count(p => p.Skip != null) },
                { "notes", l.Where(p => p.Skip == null && p.Notes.Count > 0).Take(60).Select(p => new object[] { p.Name, string.Join("، ", p.Notes) }).ToArray() },
                { "sample", l.Where(p => p.Skip == null).Take(12).Select(p => new Dictionary<string, object> { { "n", p.Name }, { "pr", p.Vars.Min(v => v.Price) }, { "v", p.Opts.Count == 0 ? 0 : p.Vars.Count }, { "im", p.Pics.Count > 0 ? p.Pics[0].Url : "" }, { "k", string.Join("، ", Paths(p, "type")) }, { "h", p.Hidden }, { "d", Find(d, map, sku, p) != null } }).ToArray() }
            };
        }

        static Dictionary<string, int> Map(SqlConnection c, int storeId, string src)
        {
            var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = Db.Cmd(c, "SELECT Ref, ProductId FROM dbo.ShopImportMap WHERE StoreId=@s AND Src=@k", "@s", storeId, "@k", src)) using (var r = cmd.ExecuteReader()) while (r.Read()) m[(string)r[0]] = (int)r[1];
            return m;
        }
        static Dictionary<string, int> Skus(ShopData d)
        {
            var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in d.Products) { if (!string.IsNullOrEmpty(p.Sku)) m[p.Sku] = p.Id; foreach (var v in p.Vars) if (!string.IsNullOrEmpty(v.Sku)) m[v.Sku] = p.Id; }
            return m;
        }
        // the same product already in the store: by what it was the last time it moved, its link, or a code
        static ShopProd Find(ShopData d, Dictionary<string, int> map, Dictionary<string, int> sku, MoveProd p)
        {
            int id; ShopProd x;
            if (map.TryGetValue(p.Ref, out id) && d.ById.TryGetValue(id, out x)) return x;
            var slug = Shops.Slugify(p.Ref.StartsWith("id:") ? p.Name : p.Ref, 74); if (slug.Length > 0 && d.BySlug.TryGetValue(slug, out x)) return x;
            foreach (var v in p.Vars) if (!string.IsNullOrEmpty(v.Sku) && sku.TryGetValue(v.Sku, out id) && d.ById.TryGetValue(id, out x)) return x;
            return null;
        }

        // ---------- the owner's file (api/shop.ashx with kind=move) ----------
        public static void Upload(SqlConnection c, StoreInfo s, Shop sh, HttpRequest req, HttpResponse res)
        {
            if (!Web.Allow("shop-move:" + s.Id, 20, TimeSpan.FromHours(1))) throw new ShopException("many", "رفعت ملفات كتير في ساعة. استنى شوية.");
            Busy(c, sh.StoreId);
            var f = req.Files[0]; string ext = Path.GetExtension(f.FileName ?? "").ToLowerInvariant();
            if (ext != ".csv") throw new ShopException("type", "ارفع ملف CSV اللي نزّلته من متجرك القديم.");
            if (f.ContentLength > 8 * 1024 * 1024) throw new ShopException("big", "الملف أكبر من 8 ميجا. نزّل المنتجات على كذا ملف.");
            byte[] data; using (var ms = new MemoryStream()) { f.InputStream.CopyTo(ms); data = ms.ToArray(); }
            List<string[]> rows; try { rows = Crm.Csv(Extract.PlainText(data)); } catch { throw new ShopException("bad", "الملف مش بيتقري. نزّله CSV من متجرك القديم تاني."); }
            if (rows.Count < 2) throw new ShopException("empty", "الملف فاضي.");
            var h = rows[0].Select(x => (x ?? "").Trim().ToLowerInvariant()).ToList(); string src; List<MoveProd> l;
            if ((h.Contains("handle") || h.Contains("url handle")) && h.Contains("title")) { src = "shopify"; l = Shopify(rows); }
            else if (h.Contains("type") && h.Contains("name") && h.Contains("regular price")) { src = "woo"; l = WooCommerce(rows); }
            else throw new ShopException("kind", "الملف ده مش ملف منتجات من Shopify أو WooCommerce. نزّل ملف المنتجات (Export) من هناك وارفعه زي ما هو.");
            if (l.Count == 0) throw new ShopException("empty", "مالقيناش منتجات في الملف.");
            Check(l); var pv = Preview(c, sh, src, l);
            int id; using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopImports(StoreId, Src, FileName, State, PlanJson, Preview, Total) OUTPUT INSERTED.Id VALUES(@s, @k, @f, 'preview', @p, @v, @t)", "@s", sh.StoreId, "@k", src, "@f", Cut(Path.GetFileName(f.FileName ?? ""), 120), "@p", JsonSerializer.Serialize(l, Js), "@v", ShopJson.Write(pv), "@t", l.Count)) id = (int)cmd.ExecuteScalar();
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "imp", Status(c, sh, id) } });
        }
        static void Busy(SqlConnection c, int storeId)
        {
            Exec(c, "UPDATE dbo.ShopImports SET State='stopped' WHERE StoreId=@s AND State='preview' AND CreatedAt<DATEADD(DAY,-2,SYSUTCDATETIME())", "@s", storeId);
            // a read of EasyOrders that the site's restart cut (its key lived in that task only): it can be started again
            Exec(c, "UPDATE dbo.ShopImports SET State='failed', Error=N'القراية من EasyOrders وقفت. ابدأها تاني.' WHERE StoreId=@s AND State='fetch' AND ISNULL(BeatAt, CreatedAt)<DATEADD(MINUTE,-10,SYSUTCDATETIME())", "@s", storeId);
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopImports WHERE StoreId=@s AND State IN ('fetch','queued','run')", "@s", storeId)) if ((int)cmd.ExecuteScalar() > 0) throw new ShopException("busy", "فيه نقل شغال دلوقتي. استنى لما يخلص.");
        }

        // EasyOrders: read in the background (their API answers 36 times a minute), then the same preview
        public static int StartEo(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            if (!Web.Allow("shop-move:" + s.Id, 20, TimeSpan.FromHours(1))) throw new ShopException("many", "حاولت كتير في ساعة. استنى شوية.");
            Busy(c, sh.StoreId);
            var k = EasyOrders.For(s); var typed = ShopJson.Str(ShopJson.At(b, "key"), 200).Trim();
            if (typed.Length > 0) { if (!EasyOrders.ValidKey(typed)) throw new ShopException("key", "الـ API Key ده مش كامل. انسخه تاني من EasyOrders (Settings ← Public API)."); k = new EasyOrders.Creds { StoreId = s.Id, Key = typed }; }
            if (k == null) throw new ShopException("key", "اكتب الـ API Key بتاع EasyOrders (Settings ← Public API).");
            int id; using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopImports(StoreId, Src, FileName, State) OUTPUT INSERTED.Id VALUES(@s, 'eo', N'EasyOrders', 'fetch')", "@s", sh.StoreId)) id = (int)cmd.ExecuteScalar();
            HostingEnvironment.QueueBackgroundWorkItem(ct => ReadEo(id, sh.StoreId, k, ct));   // the key lives in this task only, never in a table
            return id;
        }
        static async Task ReadEo(int id, int storeId, EasyOrders.Creds k, CancellationToken ct)
        {
            try
            {
                var l = new List<MoveProd>(); var seen = new HashSet<string>();
                for (int page = 1; page <= 60; page++)
                {
                    var r = await EasyOrders.ProductsPage(k, page); int fresh = 0;
                    JsonElement a = r; if (r.ValueKind == JsonValueKind.Object) foreach (var nm in new[] { "data", "items", "products", "results" }) { JsonElement x; if (r.TryGetProperty(nm, out x) && x.ValueKind == JsonValueKind.Array) { a = x; break; } }
                    if (a.ValueKind == JsonValueKind.Array) foreach (var e in a.EnumerateArray()) { if (e.ValueKind != JsonValueKind.Object) continue; var p = FromEo(e); if (seen.Add(p.Ref)) { l.Add(p); fresh++; } }
                    using (var c = Db.Open()) Exec(c, "UPDATE dbo.ShopImports SET Total=@n, BeatAt=SYSUTCDATETIME() WHERE Id=@i AND State='fetch'", "@n", l.Count, "@i", id);
                    JsonElement next; bool last = r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("next_page", out next) || next.ValueKind == JsonValueKind.Null;
                    if (last || fresh == 0 || ct.IsCancellationRequested) break;
                }
                using (var c = Db.Open())
                {
                    var sh = Shops.ByStore(c, storeId); if (sh == null) return;
                    if (l.Count == 0) { Exec(c, "UPDATE dbo.ShopImports SET State='failed', Error=N'مالقيناش منتجات في EasyOrders.' WHERE Id=@i", "@i", id); return; }
                    Check(l); Exec(c, "UPDATE dbo.ShopImports SET State='preview', PlanJson=@p, Preview=@v, Total=@t WHERE Id=@i AND State='fetch'", "@p", JsonSerializer.Serialize(l, Js), "@v", ShopJson.Write(Preview(c, sh, "eo", l)), "@t", l.Count, "@i", id);
                }
            }
            catch (Exception ex)
            {
                if (!(ex is EoException)) Log.Error(ex);
                try { using (var c = Db.Open()) Exec(c, "UPDATE dbo.ShopImports SET State='failed', Error=@e WHERE Id=@i", "@e", Cut(ex is EoException ? ex.Message : "مقدرناش نقرا منتجات EasyOrders دلوقتي. جرّب تاني كمان شوية.", 400), "@i", id); } catch { }
            }
        }

        public static Dictionary<string, object> Status(SqlConnection c, Shop sh, int id)
        {
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id, Src, FileName, State, Preview, Opts, Total, Done, Pics, PicsDone, Added, Updated, Skipped, Report, Error, CreatedAt, EndedAt FROM dbo.ShopImports WHERE StoreId=@s AND (@i=0 OR Id=@i) ORDER BY Id DESC", "@s", sh.StoreId, "@i", id))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null; string st = (string)r["State"];
                return new Dictionary<string, object> { { "id", (int)r["Id"] }, { "src", (string)r["Src"] }, { "file", r["FileName"] as string ?? "" }, { "state", st }, { "preview", st == "preview" ? ShopJson.Obj(r["Preview"] as string) : null }, { "opts", ShopJson.Obj(r["Opts"] as string) },
                    { "total", (int)r["Total"] }, { "done", (int)r["Done"] }, { "pics", (int)r["Pics"] }, { "picsDone", (int)r["PicsDone"] }, { "added", (int)r["Added"] }, { "updated", (int)r["Updated"] }, { "skipped", (int)r["Skipped"] },
                    { "report", st == "done" || st == "stopped" || st == "failed" ? ShopJson.Obj(r["Report"] as string) : null }, { "error", r["Error"] as string ?? "" } };
            }
        }

        public static void Go(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            int id = ShopJson.Int(ShopJson.At(b, "id")); string dup = ShopJson.Str(ShopJson.At(b, "dup"), 8) == "skip" ? "skip" : "update", cats = ShopJson.Str(ShopJson.At(b, "cats"), 6); if (cats != "tags" && cats != "none") cats = "type";
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopImports SET State='queued', Opts=@o WHERE Id=@i AND StoreId=@s AND State='preview'", "@o", ShopJson.Write(new Dictionary<string, object> { { "dup", dup }, { "cats", cats } }), "@i", id, "@s", sh.StoreId))
                if (cmd.ExecuteNonQuery() == 0) throw new ShopException("state", "النقل ده اتبدأ أو اتلغى. افتح الصفحة تاني.");
            HostingEnvironment.QueueBackgroundWorkItem(ct => Run(id, ct));
        }
        public static void Stop(SqlConnection c, Shop sh, int id) { Exec(c, "UPDATE dbo.ShopImports SET State='stopped', EndedAt=SYSUTCDATETIME() WHERE Id=@i AND StoreId=@s AND State IN ('fetch','preview','queued','run')", "@i", id, "@s", sh.StoreId); }
        // one that waited or stopped beating (the site restarted): carried on from where it was
        public static void Resume()
        {
            var ids = new List<int>(); using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "SELECT TOP 5 Id FROM dbo.ShopImports WHERE State='queued' OR (State='run' AND BeatAt<DATEADD(MINUTE,-3,SYSUTCDATETIME())) ORDER BY Id")) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((int)r[0]);
            foreach (var id in ids) { int i = id; HostingEnvironment.QueueBackgroundWorkItem(ct => Run(i, ct)); }
        }

        // ---------- the run ----------
        static async Task Run(int id, CancellationToken ct)
        {
            try
            {
                int storeId; string src; List<MoveProd> plan; Dictionary<string, object> opts, report; int done;
                using (var c = Db.Open())
                {
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopImports SET State='run', BeatAt=SYSUTCDATETIME(), StartedAt=ISNULL(StartedAt, SYSUTCDATETIME()) OUTPUT INSERTED.StoreId, INSERTED.Src, INSERTED.PlanJson, INSERTED.Opts, INSERTED.Done, INSERTED.Report WHERE Id=@i AND (State='queued' OR (State='run' AND BeatAt<DATEADD(MINUTE,-3,SYSUTCDATETIME())))", "@i", id))
                    using (var r = cmd.ExecuteReader()) { if (!r.Read()) return; storeId = (int)r[0]; src = (string)r[1]; plan = JsonSerializer.Deserialize<List<MoveProd>>((string)r[2]); opts = ShopJson.Obj(r[3] as string); done = (int)r[4]; report = ShopJson.Obj(r[5] as string); }
                }
                if (!report.ContainsKey("skip")) report["skip"] = new List<object>(); if (!report.ContainsKey("bad")) report["bad"] = new List<object>();
                var skips = ShopJson.List(report["skip"]); var bad = ShopJson.List(report["bad"]);
                if (await Products(id, storeId, src, plan, opts, done, skips, ct)) await Pictures(id, storeId, bad, ct);
                using (var c = Db.Open())
                {
                    report["skip"] = skips.Take(400).ToList(); report["bad"] = bad.Take(400).ToList();
                    Exec(c, "UPDATE dbo.ShopImports SET State=CASE WHEN State='run' THEN 'done' ELSE State END, EndedAt=SYSUTCDATETIME(), Report=@r, PlanJson=CASE WHEN State='run' THEN NULL ELSE PlanJson END WHERE Id=@i", "@r", ShopJson.Write(report), "@i", id);
                    Shops.Bump(c, storeId);
                }
            }
            catch (Exception ex) { Log.Error(ex); try { using (var c = Db.Open()) Exec(c, "UPDATE dbo.ShopImports SET State='failed', Error=N'حصلت مشكلة في النقل. اللي اتنقل فضل، وتقدر تكمّل بنفس الملف (اختار «حدّث الموجود»).', EndedAt=SYSUTCDATETIME() WHERE Id=@i", "@i", id); } catch { } }
        }
        static bool Running(SqlConnection c, int id) { using (var cmd = Db.Cmd(c, "SELECT State FROM dbo.ShopImports WHERE Id=@i", "@i", id)) return (cmd.ExecuteScalar() as string) == "run"; }

        // the products: false = the owner stopped it
        static Task<bool> Products(int id, int storeId, string src, List<MoveProd> plan, Dictionary<string, object> opts, int from, List<object> skips, CancellationToken ct)
        {
            using (var c = Db.Open())
            {
                var sh = Shops.ByStore(c, storeId); if (sh == null) return Task.FromResult(false);
                string dup = ShopJson.Str(ShopJson.At(opts, "dup")), cats = ShopJson.Str(ShopJson.At(opts, "cats"));
                // the categories first, so every product is saved against a copy that has them
                var made = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); int catN = 0;
                foreach (var path in plan.Where(p => p.Skip == null).SelectMany(p => Paths(p, cats)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                    try { Cat(c, sh, path, made, ref catN); } catch (ShopException) { }
                ShopCache.Forget(storeId); var d = ShopCache.Get(c, sh); ShopCache.Forget(storeId);
                var map = Map(c, storeId, src); var sku = Skus(d); var bumped = DateTime.UtcNow; int count = d.Products.Count, added = 0, updated = 0, skipped = 0; int pics = 0;
                using (var cmd = Db.Cmd(c, "SELECT Added, Updated, Skipped, Pics FROM dbo.ShopImports WHERE Id=@i", "@i", id)) using (var r = cmd.ExecuteReader()) if (r.Read()) { added = (int)r[0]; updated = (int)r[1]; skipped = (int)r[2]; pics = (int)r[3]; }
                for (int i = from; i < plan.Count; i++)
                {
                    var p = plan[i];
                    try
                    {
                        if (p.Skip != null) { skipped++; skips.Add(new object[] { p.Name.Length > 0 ? p.Name : p.Ref, p.Skip }); continue; }
                        var old = Find(d, map, sku, p);
                        if (old != null && dup == "skip") { skipped++; skips.Add(new object[] { p.Name, "موجود في متجرك قبل كده" }); continue; }
                        if (old == null && count >= Shops.MaxProducts) { skipped++; skips.Add(new object[] { p.Name, "وصلت لأقصى عدد منتجات (" + Shops.MaxProducts + ")" }); continue; }
                        var body = Body(p, old, Paths(p, cats).Select(x => made.ContainsKey(x) ? made[x] : 0).Where(x => x > 0).Distinct().ToList());
                        int pid = ShopOwner.SaveProductFrom(c, sh, body, d);
                        Exec(c, "MERGE dbo.ShopImportMap AS t USING (SELECT @s AS StoreId, @k AS Src, @r AS Ref) AS x ON t.StoreId=x.StoreId AND t.Src=x.Src AND t.Ref=x.Ref WHEN MATCHED THEN UPDATE SET ProductId=@p WHEN NOT MATCHED THEN INSERT (StoreId, Src, Ref, ProductId) VALUES (@s, @k, @r, @p);", "@s", storeId, "@k", src, "@r", p.Ref, "@p", pid);
                        if (old == null) { added++; count++; } else updated++;
                        // pictures for a new product, or one that has none yet (a second import doesn't copy them twice)
                        if (old == null || old.Pics.Count == 0)
                            for (int k = 0; k < p.Pics.Count; k++)
                            {
                                var u = p.Pics[k].Url; var keys = p.Vars.Where(v => v.Img == u).Select(v => string.Join("|", v.O)).ToList();
                                Exec(c, "INSERT INTO dbo.ShopImportPics(ImportId, StoreId, ProductId, Url, Sort, Alt, Vars) VALUES(@i, @s, @p, @u, @n, @a, @v)", "@i", id, "@s", storeId, "@p", pid, "@u", Cut(u, 900), "@n", k, "@a", string.IsNullOrEmpty(p.Pics[k].Alt) ? null : p.Pics[k].Alt, "@v", keys.Count == 0 ? null : ShopJson.Write(keys)); pics++;
                            }
                    }
                    catch (ShopException ex) { skipped++; skips.Add(new object[] { p.Name, ex.Message }); }
                    if ((i + 1) % 25 == 0 || i == plan.Count - 1)
                    {
                        Exec(c, "UPDATE dbo.ShopImports SET Done=@d, Added=@a, Updated=@u, Skipped=@k, Pics=@p, Report=@r, BeatAt=SYSUTCDATETIME() WHERE Id=@i", "@d", i + 1, "@a", added, "@u", updated, "@k", skipped, "@p", pics, "@r", ShopJson.Write(new Dictionary<string, object> { { "skip", skips.Take(400).ToList() } }), "@i", id);
                        if (DateTime.UtcNow - bumped > TimeSpan.FromSeconds(20)) { Shops.Bump(c, storeId); bumped = DateTime.UtcNow; }   // a big catalog is read again after each: not more than every 20 seconds
                        if (!Running(c, id) || ct.IsCancellationRequested) { Shops.Bump(c, storeId); return Task.FromResult(false); }
                    }
                }
                Exec(c, "UPDATE dbo.ShopImports SET Done=@d, Added=@a, Updated=@u, Skipped=@k, Pics=@p, BeatAt=SYSUTCDATETIME() WHERE Id=@i", "@d", plan.Count, "@a", added, "@u", updated, "@k", skipped, "@p", pics, "@i", id);
                Shops.Bump(c, storeId);
                return Task.FromResult(true);
            }
        }

        // "ملابس > فساتين": two levels at most, made when they aren't there (the store's own limit stays)
        static int Cat(SqlConnection c, Shop sh, string path, Dictionary<string, int> made, ref int count)
        {
            int had; if (made.TryGetValue(path, out had)) return had;
            var parts = path.Split(new[] { '>', '›' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Cut(x, 80)).Where(x => x.Length > 1).ToList(); parts = parts.Skip(Math.Max(0, parts.Count - 2)).ToList(); int parent = 0, id = 0;
            foreach (var p in parts)
            {
                var d = ShopCache.Get(c, sh); var k = d.Cats.FirstOrDefault(x => string.Equals(x.Name, p, StringComparison.OrdinalIgnoreCase) && (x.ParentId ?? 0) == parent) ?? (parent == 0 && parts.Count == 1 ? d.Cats.FirstOrDefault(x => string.Equals(x.Name, p, StringComparison.OrdinalIgnoreCase)) : null);
                if (k != null) id = k.Id; else { if (d.Cats.Count >= Shops.MaxCategories) return made[path] = 0; id = ShopOwner.SaveCatFrom(c, sh, new Dictionary<string, object> { { "name", p }, { "parent", parent } }); count++; }
                parent = id;
            }
            return made[path] = id;
        }

        // the product as the owner's editor would save it; an update keeps what the old store doesn't have (English, specs, the guide...)
        static Dictionary<string, object> Body(MoveProd p, ShopProd old, List<int> cats)
        {
            decimal price = p.Vars.Min(v => v.Price); var low = p.Vars.First(v => v.Price == price);
            var b = new Dictionary<string, object> { { "name", p.Name }, { "descr", p.Descr ?? "" }, { "price", price }, { "old", low.Old > price ? low.Old : 0 }, { "cost", low.Cost > 0 ? low.Cost : 0 }, { "brand", Cut(p.Vendor, 60) }, { "tags", Cut(string.Join("، ", p.Tags), 300) },
                { "track", p.Track }, { "out", p.Cont ? "order" : "show" }, { "status", p.Hidden ? "hidden" : "active" }, { "weight", p.Vars.Select(v => v.Grams).FirstOrDefault(g => g > 0) }, { "slug", Regex.IsMatch(p.Ref, "^[a-z0-9][a-z0-9-]{1,78}$") && !Regex.IsMatch(p.Ref, "^[0-9-]+$") ? p.Ref : "" } };
            if (old != null)
            {
                foreach (var kv in new Dictionary<string, object> { { "id", old.Id }, { "slug", old.Slug }, { "nameEn", old.NameEn }, { "descrEn", old.DescrEn }, { "unit", old.Unit }, { "min", old.MinQty }, { "max", old.MaxQty ?? 0 }, { "feat", old.Feat }, { "specs", ShopJson.Arr(old.SpecsJson) }, { "guide", string.IsNullOrEmpty(old.SizeGuideJson) ? null : ShopJson.Obj(old.SizeGuideJson) }, { "extra", ShopJson.Obj(old.ExtraJson) }, { "metaTitle", old.MetaTitle }, { "metaDesc", old.MetaDesc } }) b[kv.Key] = kv.Value;
                if (string.IsNullOrWhiteSpace(p.Descr)) b["descr"] = old.Descr; if (string.IsNullOrWhiteSpace(p.Vendor)) b["brand"] = old.Brand; if (p.Tags.Count == 0) b["tags"] = old.Tags;
                if (old.Cost.HasValue && low.Cost <= 0) b["cost"] = old.Cost.Value;
                cats = old.Cats.Concat(cats).Distinct().Take(10).ToList(); b["main"] = old.MainCat;
            }
            else if (cats.Count > 0) b["main"] = cats[0];
            b["cats"] = cats.Cast<object>().ToArray();
            if (p.Opts.Count == 0)
            {
                var v = p.Vars[0]; b["sku"] = Cut(v.Sku, 60);
                if (p.Track) { b["stock"] = v.Qty ?? 0; if (old != null && old.Vars.Count == 1 && old.Options.Count == 0) b["stock0"] = old.Vars[0].Stock; }
                else b["stock"] = 0;
            }
            else
            {
                b["options"] = p.Opts.Select((o, i) => (object)new Dictionary<string, object> { { "n", new object[] { o, "" } }, { "v", p.Vars.Select(v => v.O[i]).Distinct().Take(40).Select(x => (object)new object[] { x, "", "" }).ToArray() } }).ToArray();
                b["vars"] = p.Vars.Select(v =>
                {
                    var key = string.Join("|", v.O); var was = old == null ? null : old.Vars.FirstOrDefault(x => x.Opts == key);
                    var x2 = new Dictionary<string, object> { { "o", v.O.Cast<object>().ToArray() }, { "price", v.Price != price ? (object)v.Price : null }, { "old", v.Price != price && v.Old > v.Price ? (object)v.Old : null }, { "cost", v.Cost > 0 ? (object)v.Cost : null }, { "sku", Cut(v.Sku, 60) }, { "stock", p.Track ? v.Qty ?? 0 : 0 }, { "active", true } };
                    if (was != null) { x2["stock0"] = was.Stock; x2["image"] = was.Image; if (!p.Track) x2["stock"] = was.Stock; }
                    return (object)x2;
                }).ToArray();
            }
            return b;
        }

        // the pictures, four at a time; nothing is held open while a picture is on its way
        static async Task Pictures(int id, int storeId, List<object> bad, CancellationToken ct)
        {
            for (var bumped = DateTime.UtcNow; ; )
            {
                if (ct.IsCancellationRequested) return;
                var batch = new List<object[]>();
                using (var c = Db.Open())
                {
                    if (!Running(c, id)) return;
                    using (var cmd = Db.Cmd(c, "SELECT TOP 8 i.Id, i.ProductId, i.Url, i.Alt, i.Vars, p.Name FROM dbo.ShopImportPics i LEFT JOIN dbo.ShopProducts p ON p.Id=i.ProductId WHERE i.ImportId=@i AND i.State='wait' ORDER BY i.ProductId, i.Sort, i.Id", "@i", id))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) batch.Add(new object[] { Convert.ToInt64(r[0]), (int)r[1], (string)r[2], r[3] as string, r[4] as string, r[5] as string });
                }
                if (batch.Count == 0) return;
                var got = new ShopFetch.Got[batch.Count];
                for (int k = 0; k < batch.Count; k += 4) { var part = batch.Skip(k).Take(4).Select(x => ShopFetch.Get((string)x[2], ShopFetch.MaxBytes, null, ct)).ToArray(); var res = await Task.WhenAll(part); for (int j = 0; j < res.Length; j++) got[k + j] = res[j]; }
                using (var c = Db.Open())
                {
                    for (int k = 0; k < batch.Count; k++)
                    {
                        var x = batch[k]; long pid = (long)x[0]; int prod = (int)x[1]; string why = got[k].Why, path = null;
                        if (x[5] == null) why = "المنتج اتمسح";
                        if (why == null) { var pic = ShopFetch.Make(got[k].Bytes); if (pic == null) why = "الملف مش صورة JPG أو PNG أو WebP، أو كبيرة أوي"; else try { path = (string)ShopFetch.Keep(c, storeId, prod, pic, (string)x[3])["path"]; } catch (ShopException ex) { why = ex.Message; } }
                        if (path != null)
                        {
                            foreach (var key in ShopJson.Arr((string)x[4]).Select(o => ShopJson.Str(o, 300)).Where(o => o.Length > 0)) Exec(c, "UPDATE dbo.ShopVariants SET Image=@im WHERE StoreId=@s AND ProductId=@p AND Opts=@o", "@im", path, "@s", storeId, "@p", prod, "@o", key);
                            Exec(c, "UPDATE dbo.ShopImportPics SET State='ok', Path=@p WHERE Id=@i", "@p", path, "@i", pid);
                        }
                        else { Exec(c, "UPDATE dbo.ShopImportPics SET State='bad', Why=@w WHERE Id=@i", "@w", Cut(why, 120), "@i", pid); bad.Add(new object[] { (string)x[5] ?? "", Cut((string)x[2], 160), why }); }
                    }
                    Exec(c, "UPDATE dbo.ShopImports SET PicsDone=(SELECT COUNT(*) FROM dbo.ShopImportPics WHERE ImportId=@i AND State<>'wait'), BeatAt=SYSUTCDATETIME() WHERE Id=@i", "@i", id);
                    if (DateTime.UtcNow - bumped > TimeSpan.FromSeconds(20)) { Shops.Bump(c, storeId); bumped = DateTime.UtcNow; }   // the store's pages see the new pictures every 20 seconds
                }
            }
        }
    }
}
