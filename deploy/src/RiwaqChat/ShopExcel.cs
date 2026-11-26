// Excel in and out for a store: products (one row per product, or per size and color), the orders, and the shipping company's file.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace RiwaqChat
{
    public static class ShopExcel
    {
        static readonly string[] Head = { "الاسم", "القسم", "السعر", "السعر قبل الخصم", "سعر الشرا", "الكمية", "الكود", "اللون", "المقاس", "الوصف", "الاسم بالإنجليزي", "الماركة", "الوحدة", "ظاهر" };

        public static byte[] Book(string sheet, string[] head, IEnumerable<object[]> rows) { return Sheets(new Sh { Name = sheet, Head = head, Rows = rows }); }

        // one sheet of a workbook: an optional line above the column names, then the names, then the rows
        public class Sh { public string Name, Note; public string[] Head; public IEnumerable<object[]> Rows; public double[] Widths; }

        // a workbook of one sheet or more. An upload reads the first one (Crm.Sheet), so the products come first.
        public static byte[] Sheets(params Sh[] list)
        {
            using (var ms = new MemoryStream())
            {
                using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
                {
                    var wb = doc.AddWorkbookPart(); wb.Workbook = new X.Workbook(); var sheets = wb.Workbook.AppendChild(new X.Sheets()); uint n = 0;
                    Func<object, X.Cell> cell = v => v is decimal || v is int || v is long
                        ? new X.Cell { DataType = X.CellValues.Number, CellValue = new X.CellValue(Convert.ToDecimal(v).ToString("0.##", CultureInfo.InvariantCulture)) }
                        : new X.Cell { DataType = X.CellValues.InlineString, InlineString = new X.InlineString(new X.Text(new string((v as string ?? "").Where(ch => ch >= 0x20 || ch == '\n' || ch == '\t').ToArray())) { Space = SpaceProcessingModeValues.Preserve }) };
                    foreach (var s in list)
                    {
                        var ws = wb.AddNewPart<WorksheetPart>(); var data = new X.SheetData(); var cols = new X.Columns();
                        for (int i = 0; i < s.Head.Length; i++) cols.Append(new X.Column { Min = (uint)(i + 1), Max = (uint)(i + 1), Width = s.Widths != null && i < s.Widths.Length ? s.Widths[i] : 18, CustomWidth = true });
                        ws.Worksheet = new X.Worksheet(new X.SheetViews(new X.SheetView { WorkbookViewId = 0, RightToLeft = true, TabSelected = n == 0 }), new X.SheetFormatProperties { DefaultRowHeight = 16 }, cols, data);
                        if (!string.IsNullOrEmpty(s.Note)) data.Append(new X.Row(cell(s.Note)));
                        data.Append(new X.Row(s.Head.Select(h => cell(h))));
                        foreach (var r in s.Rows) data.Append(new X.Row(r.Select(cell)));
                        sheets.Append(new X.Sheet { Id = wb.GetIdOfPart(ws), SheetId = ++n, Name = s.Name });
                    }
                    wb.Workbook.Save();
                }
                return ms.ToArray();
            }
        }

        /* «ملف مثال»: what a filled file looks like, from more than one kind of shop. The ready file to fill has no rows,
           so nobody uploads the examples by mistake; an upload of the example as it is gets told so (Import). */
        const string ExampleNote = "ده ملف مثال عشان تشوف شكل الملف. اكتب منتجاتك مكان السطور دي، أو نزّل الملف الجاهز واملاه. شرح كل عمود في الصفحة التانية «اقرا الأول».";
        static readonly object[][] Example = {
            new object[] { "تيشيرت قطن بيزك", "أزياء > تيشيرتات", 350m, 450m, 180m, 6, "TS-01-WM", "أبيض", "M", "قطن 100% مريح للبس كل يوم", "Basic cotton tee", "", "قطعة", "أيوه" },
            new object[] { "تيشيرت قطن بيزك", "", 350m, 450m, "", 4, "TS-01-WL", "أبيض", "L", "", "", "", "", "" },
            new object[] { "تيشيرت قطن بيزك", "", 350m, 450m, "", 5, "TS-01-BM", "أسود", "M", "", "", "", "", "" },
            new object[] { "تيشيرت قطن بيزك", "", 370m, 470m, "", 0, "TS-01-BXL", "أسود", "XL", "", "", "", "", "" },
            new object[] { "سماعة بلوتوث لاسلكي", "إلكترونيات > سماعات", 1290m, 1590m, 900m, 12, "HP-20", "", "", "بطارية لحد 30 ساعة وشحن سريع", "Wireless headphones", "ساوند", "قطعة", "أيوه" },
            new object[] { "عسل نحل جبلي", "بقالة > عسل", 150m, "", 95m, 20, "HN-500", "", "نص كيلو", "عسل جبلي في برطمان إزاز", "Mountain honey", "", "برطمان", "أيوه" },
            new object[] { "عسل نحل جبلي", "", 280m, "", 180m, 15, "HN-1000", "", "كيلو", "", "", "", "", "" },
            new object[] { "شنطة يد جلد", "شنط", 950m, "", 600m, 3, "BG-07-BR", "بني", "", "جلد طبيعي بسوستة وجيب من جوه", "Leather handbag", "", "قطعة", "أيوه" },
            new object[] { "شنطة يد جلد", "", 950m, "", "", 2, "BG-07-BK", "أسود", "", "", "", "", "", "" },
            new object[] { "كريم مرطب للوش", "عناية > بشرة", 220m, "", 120m, 30, "CR-50", "", "", "50 مل للبشرة العادية والجافة", "Face moisturizer", "", "علبة", "أيوه" },
            new object[] { "طقم أكواب إزاز 6 قطع", "البيت", 400m, "", 250m, 8, "CU-06", "", "", "إزاز شفاف يستحمل السخونية", "Glass cup set", "", "طقم", "لأ" } };
        static readonly double[] HeadW = { 26, 22, 10, 15, 11, 9, 12, 10, 11, 34, 22, 12, 10, 8 };
        static Sh ReadMe()
        {
            Func<string, string, string, string, object[]> r = (a, b, x, e) => new object[] { a, b, x, e };
            return new Sh { Name = "اقرا الأول", Head = new[] { "العمود", "لازم؟", "تكتب فيه إيه", "مثال" }, Widths = new double[] { 18, 9, 70, 22 }, Rows = new[] {
                r("الاسم", "لازم", "اسم المنتج زي ما الزبون هيشوفه. لو ليه ألوان أو مقاسات: سطر لكل واحد، بنفس الاسم بالظبط.", "تيشيرت قطن بيزك"),
                r("القسم", "", "القسم، ولو ليه قسم فرعي اكتب: القسم > الفرعي. القسم اللي مش موجود بيتعمل لوحده.", "أزياء > تيشيرتات"),
                r("السعر", "لازم", "السعر بالجنيه، رقم بس. لو مقاس سعره مختلف اكتب سعره في سطره.", "350"),
                r("السعر قبل الخصم", "", "لو عليه خصم: السعر القديم، ولازم يكون أكبر من السعر.", "450"),
                r("سعر الشرا", "", "اللي دفعته في المنتج. ليك انت بس، الزبون مش بيشوفه.", "180"),
                r("الكمية", "", "الكمية اللي عندك، و0 يعني خلص. في الألوان والمقاسات اكتب كمية كل سطر. منتج من غير ألوان ولا مقاسات وكميته فاضية بيفضل متاح من غير عدّ.", "6"),
                r("الكود", "", "كود من عندك. لما ترفع الملف تاني، المنتج بيتعرف باسمه أو بكوده ومش بيتكرر.", "TS-01-WM"),
                r("اللون", "", "لون السطر ده.", "أبيض"),
                r("المقاس", "", "مقاس أو وزن أو حجم السطر ده.", "M، نص كيلو"),
                r("الوصف", "", "كلام عن المنتج بيظهر في صفحته.", "قطن 100% مريح للبس كل يوم"),
                r("الاسم بالإنجليزي", "", "لو متجرك بالإنجليزي كمان.", "Basic cotton tee"),
                r("الماركة", "", "اسم الماركة لو ليه.", "ساوند"),
                r("الوحدة", "", "المنتج بيتباع بإيه.", "قطعة، علبة، برطمان"),
                r("ظاهر", "", "«أيوه» أو فاضي: الزباين بيشوفوه. «لأ»: بيتحفظ مخفي لحد ما تظهره.", "أيوه"),
                r("", "", "", ""),
                r("الصور", "", "بتتضاف من صفحة كل منتج بعد ما ترفع الملف.", ""),
                r("الحجم", "", "الملف لحد 5 ميجا، ولحد 5000 سطر.", "") } };
        }

        public static void Export(SqlConnection c, Shop sh, HttpRequest req, HttpResponse res)
        {
            if (!Web.Allow("shop-xls:" + sh.StoreId, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "ملفات كتير في ساعة، استنى شوية."); return; }
            string what = req.QueryString["what"] ?? "", name; byte[] data;
            if (what == "template") { name = "sellystore-products.xlsx"; data = Sheets(new Sh { Name = "المنتجات", Head = Head, Widths = HeadW, Rows = new object[0][] }, ReadMe()); }
            else if (what == "example") { name = "sellystore-example.xlsx"; data = Sheets(new Sh { Name = "المنتجات", Note = ExampleNote, Head = Head, Widths = HeadW, Rows = Example }, ReadMe()); }
            else if (what == "products") { name = "products.xlsx"; var d = ShopCache.Get(c, sh); data = Book("المنتجات", ShopPlaces.XlsHead(c, sh, Head), ShopPlaces.XlsRows(c, sh, d, ProductRows(d))); }   // a column per place (ShopPlaces.cs)
            else if (what == "leads")
            {
                var l = new List<object[]>(); using (var cmd = Db.Cmd(c, "SELECT TOP 20000 Phone, Coupon, At FROM dbo.ShopLeads WHERE StoreId=@s ORDER BY Id DESC", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(new object[] { ShopOrders.Local((string)r[0]), r[1] as string ?? "", ((DateTime)r[2]).AddHours(2).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
                name = "numbers.xlsx"; data = Book("الأرقام", new[] { "الموبايل", "الكود", "امتى" }, l);
            }
            else if (what == "orders" || what == "shipping") { name = what + ".xlsx"; data = Orders(c, sh, req, what == "shipping"); }
            else { Web.Error(res, 404, "not_found"); return; }
            res.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; res.AddHeader("Content-Disposition", "attachment; filename=" + name); res.Cache.SetCacheability(HttpCacheability.NoCache); res.BinaryWrite(data);
        }

        public static byte[] Products(SqlConnection c, Shop sh) { return Book("المنتجات", Head, ProductRows(ShopCache.Get(c, sh))); }
        public static byte[] AllOrders(SqlConnection c, Shop sh) { return Orders(c, sh, null, false); }
        static string Path(ShopData d, ShopProd p) { ShopCat k, up; if (!d.CatById.TryGetValue(p.MainCat, out k)) return ""; return k.ParentId.HasValue && d.CatById.TryGetValue(k.ParentId.Value, out up) ? up.Name + " > " + k.Name : k.Name; }
        static IEnumerable<object[]> ProductRows(ShopData d)
        {
            foreach (var p in d.Products)
            {
                int ci = p.Options.FindIndex(o => IsColor(o.Ar)), si = p.Options.FindIndex(o => !IsColor(o.Ar)); bool first = true;
                foreach (var v in p.Vars.Where(x => x.Active).DefaultIfEmpty(null))
                {
                    var vals = v == null ? new string[0] : v.Vals; decimal? old = p.OldOf(v), cost = p.CostOf(v);
                    yield return new object[] { p.Name, Path(d, p), p.PriceOf(v), old.HasValue ? (object)old.Value : "", cost.HasValue ? (object)cost.Value : "", p.Track && v != null ? (object)v.Stock : "", (v == null ? null : v.Sku) ?? p.Sku ?? "", ci >= 0 && ci < vals.Length ? vals[ci] : "", si >= 0 && si < vals.Length ? vals[si] : "", first ? p.Descr ?? "" : "", first ? p.NameEn ?? "" : "", first ? p.Brand ?? "" : "", first ? p.Unit ?? "" : "", p.Status == "active" ? "أيوه" : "لأ" };
                    first = false;
                }
            }
        }
        static bool IsColor(string n) { return n != null && (n.Contains("لون") || n.ToLowerInvariant().Contains("colo")); }

        static byte[] Orders(SqlConnection c, Shop sh, HttpRequest req, bool ship)
        {
            var args = new List<object>(); string where = ShopOwner.OrderWhere(sh, req, args, ship ? "ship" : null);   // what the list shows under the same filter; «شيت الشحن» = its confirmed orders
            var ids = new List<long>(); using (var cmd = Db.Cmd(c, "SELECT TOP 5000 Id FROM dbo.ShopOrders WHERE " + where + " ORDER BY Id DESC", args.ToArray())) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Convert.ToInt64(r[0]));
            var rows = new List<object[]>();
            foreach (var id in ids)
            {
                var o = ShopOrders.ById(c, sh.StoreId, id); string items = string.Join(" + ", o.Items.Select(i => i.Name + (string.IsNullOrEmpty(i.Opts) ? "" : " (" + i.Opts + ")") + (i.Qty > 1 ? " × " + i.Qty : "")));
                rows.Add(ship ? new object[] { o.NoText, o.Name, ShopOrders.Local(o.Phone), ShopOrders.Local(o.Phone2) ?? "", ShopGeo.Name(o.Gov), o.City ?? "", string.Join("، ", new[] { o.Area, o.Address, o.Landmark }.Where(x => !string.IsNullOrEmpty(x))), o.Due, items, o.Items.Sum(i => i.Qty), o.Note ?? "" }
                    : new object[] { o.NoText, ShopPromo.Local(o.CreatedAt).Replace("T", " "), ShopOrders.StatusText(o.Status), o.Name, ShopOrders.Local(o.Phone), ShopOrders.Local(o.Phone2) ?? "", ShopGeo.Name(o.Gov), o.City ?? "", string.Join("، ", new[] { o.Area, o.Address, o.Landmark }.Where(x => !string.IsNullOrEmpty(x))), items, o.Items.Sum(i => i.Qty), o.Subtotal, o.Discount, o.Ship, o.Total, o.Coupon ?? "", o.Note ?? "", o.Tracking ?? "" });
            }
            return ship ? Book("للشحن", new[] { "رقم الطلب", "الاسم", "الموبايل", "موبايل تاني", "المحافظة", "المدينة", "العنوان", "المطلوب تحصيله", "المحتوى", "عدد القطع", "ملاحظات" }, rows)
                : Book("الأوردرات", new[] { "رقم الطلب", "التاريخ", "الحالة", "الاسم", "الموبايل", "موبايل تاني", "المحافظة", "المدينة", "العنوان", "المنتجات", "عدد القطع", "المنتجات بكام", "الخصم", "الشحن", "الإجمالي", "كود الخصم", "ملاحظة الزبون", "رقم التتبع" }, rows);
        }

        // ---------- products in ----------
        class Row { public int Line; public string Name, Cat, Color, Size, Sku, Descr, En, Brand, Unit, Shown; public decimal Price, Old, Cost; public int? Qty; }
        static readonly Dictionary<string, string[]> Cols = new Dictionary<string, string[]> {
            { "name", new[] { "الاسم", "اسم المنتج", "المنتج", "name", "title", "product", "product name" } }, { "cat", new[] { "القسم", "التصنيف", "الفئة", "category", "type", "product category" } }, { "price", new[] { "السعر", "سعر البيع", "price", "variant price", "sale price" } },
            { "old", new[] { "السعر قبل الخصم", "قبل الخصم", "السعر القديم", "compare at price", "variant compare at price", "old price" } }, { "cost", new[] { "سعر الشرا", "سعر الشراء", "التكلفة", "cost", "cost per item" } }, { "qty", new[] { "الكمية", "المخزون", "الكميه", "stock", "quantity", "qty", "variant inventory qty" } },
            { "sku", new[] { "الكود", "كود", "كود المنتج", "sku", "variant sku", "code" } }, { "color", new[] { "اللون", "لون", "color", "colour" } }, { "size", new[] { "المقاس", "مقاس", "size" } }, { "descr", new[] { "الوصف", "وصف", "description", "body (html)", "body" } },
            { "en", new[] { "الاسم بالإنجليزي", "الاسم بالانجليزي", "name en", "english name" } }, { "brand", new[] { "الماركة", "الماركه", "brand", "vendor" } }, { "unit", new[] { "الوحدة", "unit" } }, { "shown", new[] { "ظاهر", "status", "published" } } };

        public static void Import(SqlConnection c, StoreInfo s, Shop sh, HttpRequest req, HttpResponse res)
        {
            if (!Web.Allow("shop-imp:" + s.Id, 12, TimeSpan.FromHours(1))) throw new ShopException("many", "رفعت ملفات كتير في ساعة. استنى شوية.");
            var f = req.Files[0]; string ext = System.IO.Path.GetExtension(f.FileName ?? "").ToLowerInvariant();
            if (req.Form["kind"] == "move") { ShopMove.Upload(c, s, sh, req, res); return; }   // «نقل منتجات من متجر قديم»
            if (req.Form["kind"] == "video") { if (f.ContentLength > ShopImages.MaxVideo) throw new ShopException("big", "الفيديو لازم يكون لحد 6 ميجا. قصّره أو صغّره وارفعه تاني."); byte[] vd; using (var ms = new MemoryStream()) { f.InputStream.CopyTo(ms); vd = ms.ToArray(); } Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "path", ShopImages.SaveVideo(c, s.Id, vd) } }); return; }
            if (ext != ".xlsx" && ext != ".csv") throw new ShopException("type", "ارفع ملف Excel (xlsx) أو CSV.");
            if (f.ContentLength > 5 * 1024 * 1024) throw new ShopException("big", "الملف كبير. أقصى حجم 5 ميجا.");
            byte[] data; using (var ms = new MemoryStream()) { f.InputStream.CopyTo(ms); data = ms.ToArray(); }
            List<string[]> rows;
            try { rows = ext == ".xlsx" ? Crm.Sheet(data) : Crm.Csv(Extract.PlainText(data)); } catch (Exception) { throw new ShopException("bad", "الملف مش بيتقري. احفظه Excel (xlsx) وجرّب تاني."); }
            var col = new Dictionary<string, int>(); int head = -1;
            for (int i = 0; i < Math.Min(8, rows.Count) && head < 0; i++)
            {
                col.Clear();
                for (int j = 0; j < rows[i].Length; j++) { var h = (rows[i][j] ?? "").Trim().ToLowerInvariant(); foreach (var kv in Cols) if (!col.ContainsKey(kv.Key) && kv.Value.Any(x => x.ToLowerInvariant() == h)) col[kv.Key] = j; }
                if (col.ContainsKey("name") && col.ContainsKey("price")) head = i;
            }
            if (head < 0) throw new ShopException("head", "الملف محتاج عمود «الاسم» وعمود «السعر». نزّل الملف الجاهز واملاه.");
            Func<string[], string, string> cell = (r, k) => { int j; return col.TryGetValue(k, out j) && j < r.Length ? (r[j] ?? "").Trim() : ""; };

            var errors = new List<string>(); var list = new List<Row>(); string lastName = null; int line = head + 1;
            foreach (var r in rows.Skip(head + 1))
            {
                line++;
                if (r.All(string.IsNullOrWhiteSpace)) continue;
                var x = new Row { Line = line, Name = cell(r, "name"), Cat = cell(r, "cat"), Color = cell(r, "color").Replace("|", "/"), Size = cell(r, "size").Replace("|", "/"), Sku = cell(r, "sku"), Descr = cell(r, "descr"), En = cell(r, "en"), Brand = cell(r, "brand"), Unit = cell(r, "unit"), Shown = cell(r, "shown"), Price = ShopJson.Dec(Num(cell(r, "price"))), Old = ShopJson.Dec(Num(cell(r, "old"))), Cost = ShopJson.Dec(Num(cell(r, "cost"))) };
                int q; if (int.TryParse(Num(cell(r, "qty")), out q)) x.Qty = Math.Max(0, q);
                // a row for another size of the product above may leave the name empty
                if (x.Name.Length == 0 && lastName != null && (x.Color.Length > 0 || x.Size.Length > 0)) x.Name = lastName;
                if (x.Name.Length < 2) { errors.Add("صف " + line + ": مفيش اسم"); continue; }
                if (x.Price < 1) { var same = list.LastOrDefault(y => y.Name == x.Name); if (same != null) { x.Price = same.Price; x.Old = same.Old; } else { errors.Add("صف " + line + " (" + x.Name + "): السعر ناقص أو مش رقم"); continue; } }
                lastName = x.Name; list.Add(x);
                if (list.Count > 5000) break;
            }
            // the example file (its line above the column names says so): its own rows are examples, not the owner's products
            if (rows.Take(head).Any(r => r.Length > 0 && (r[0] ?? "").Trim().StartsWith("ده ملف مثال")))
            {
                int before = list.Count; list.RemoveAll(x => Example.Any(e => (string)e[0] == x.Name && (string)e[6] == x.Sku));
                if (list.Count == 0) throw new ShopException("example", "ده ملف المثال زي ما هو. اكتب منتجاتك مكان سطوره، أو نزّل الملف الجاهز واملاه.");
                if (list.Count < before) errors.Add("سطور المثال اتسابت ومترفعتش.");
            }
            if (list.Count == 0) throw new ShopException("empty", "الملف مفيهوش منتجات نقدر نقراها." + (errors.Count > 0 ? " " + errors[0] : ""));

            ShopCache.Forget(s.Id); var d = ShopCache.Get(c, sh); ShopCache.Forget(s.Id); int added = 0, updated = 0, cats = 0, knownCats = 0; var made = new Dictionary<string, int>();
            foreach (var g in list.GroupBy(x => x.Name))
            {
                try
                {
                    var first = g.First(); var old = d.Products.FirstOrDefault(p => string.Equals(p.Name.Trim(), first.Name, StringComparison.OrdinalIgnoreCase)) ?? (first.Sku.Length > 1 ? d.Products.FirstOrDefault(p => string.Equals(p.Sku, first.Sku, StringComparison.OrdinalIgnoreCase)) : null);
                    if (old == null && d.Products.Count + added >= Shops.MaxProducts) { errors.Add("وصلت لأقصى عدد منتجات (" + Shops.MaxProducts + ")"); break; }
                    var b = new Dictionary<string, object> { { "name", first.Name }, { "price", g.Min(x => x.Price) }, { "track", true } };
                    if (old != null)
                    {
                        foreach (var kv in new Dictionary<string, object> { { "id", old.Id }, { "slug", old.Slug }, { "nameEn", old.NameEn }, { "descr", old.Descr }, { "descrEn", old.DescrEn }, { "old", old.OldPrice ?? 0 }, { "cost", old.Cost ?? 0 }, { "sku", old.Sku }, { "unit", old.Unit }, { "brand", old.Brand }, { "tags", old.Tags }, { "min", old.MinQty }, { "max", old.MaxQty ?? 0 }, { "track", old.Track }, { "out", old.OutMode }, { "status", old.Status }, { "feat", old.Feat }, { "specs", ShopJson.Arr(old.SpecsJson) }, { "guide", string.IsNullOrEmpty(old.SizeGuideJson) ? null : ShopJson.Obj(old.SizeGuideJson) }, { "extra", ShopJson.Obj(old.ExtraJson) }, { "metaTitle", old.MetaTitle }, { "metaDesc", old.MetaDesc }, { "cats", old.Cats }, { "main", old.MainCat }, { "weight", old.Weight ?? 0 } }) b[kv.Key] = kv.Value;
                        b["price"] = g.Min(x => x.Price);
                    }
                    decimal o1 = g.Where(x => x.Price == (decimal)b["price"]).Select(x => x.Old).DefaultIfEmpty(0).Max(); if (o1 > (decimal)b["price"]) b["old"] = o1; else if (old == null || ShopJson.Dec(b["old"]) <= (decimal)b["price"]) b["old"] = 0;
                    if (g.Any(x => x.Cost > 0)) b["cost"] = g.First(x => x.Cost > 0).Cost;
                    Action<string, Func<Row, string>> txt = (k, get) => { var v = g.Select(get).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); if (v != null) b[k] = v; };
                    txt("descr", x => x.Descr); txt("nameEn", x => x.En); txt("brand", x => x.Brand); txt("unit", x => x.Unit); txt("sku", x => x.Sku);
                    var sw = g.Select(x => x.Shown.ToLowerInvariant()).FirstOrDefault(x => x.Length > 0); if (sw != null) b["status"] = sw == "لأ" || sw == "لا" || sw == "no" || sw == "false" || sw == "draft" || sw == "archived" || sw == "0" ? "hidden" : "active";
                    var cat = g.Select(x => x.Cat).FirstOrDefault(x => x.Length > 0);
                    if (cat != null) { int id = Category(c, sh, cat, made, ref cats); if (id > 0) { var cl = new List<int>(ShopJson.List(ShopJson.At(b, "cats")).Select(x => ShopJson.Int(x))); if (b.ContainsKey("cats") && b["cats"] is List<int>) cl = new List<int>((List<int>)b["cats"]); if (!cl.Contains(id)) cl.Add(id); b["cats"] = cl.Cast<object>().ToArray(); if (old == null) b["main"] = id; } }
                    if (b.ContainsKey("cats") && b["cats"] is List<int>) b["cats"] = ((List<int>)b["cats"]).Cast<object>().ToArray();

                    var colors = g.Select(x => x.Color).Where(x => x.Length > 0).Distinct().ToList(); var sizes = g.Select(x => x.Size).Where(x => x.Length > 0).Distinct().ToList();
                    if (colors.Count + sizes.Count > 0)
                    {
                        var opts = new List<object>();
                        Func<string, string, List<string>, bool> opt = (ar, en, vals) => { if (vals.Count == 0) return false; var prev = old == null ? null : old.Options.FirstOrDefault(z => IsColor(z.Ar) == IsColor(ar)); opts.Add(new Dictionary<string, object> { { "n", new object[] { prev == null ? ar : prev.Ar, prev == null ? en : prev.En } }, { "v", (prev == null ? new List<ShopOptVal>() : prev.Vals).Select(z => (object)new object[] { z.Ar, z.En, z.Hex }).Concat(vals.Where(v => prev == null || prev.Vals.All(z => z.Ar != v)).Select(v => (object)new object[] { v, "", "" })).ToArray() } }); return true; };
                        bool hc = opt("اللون", "Color", colors), hs = opt("المقاس", "Size", sizes);
                        var vars = new Dictionary<string, Dictionary<string, object>>();
                        // the sizes already in the store that the file doesn't mention stay as they are
                        if (old != null && old.Options.Count == opts.Count) foreach (var v in old.Vars) vars[v.Opts] = new Dictionary<string, object> { { "o", v.Vals.Cast<object>().ToArray() }, { "price", v.Price.HasValue ? (object)v.Price.Value : null }, { "old", v.OldPrice.HasValue ? (object)v.OldPrice.Value : null }, { "cost", v.Cost.HasValue ? (object)v.Cost.Value : null }, { "stock", v.Stock }, { "stock0", v.Stock }, { "sku", v.Sku }, { "image", v.Image }, { "active", v.Active } };
                        foreach (var x in g)
                        {
                            if ((hc && x.Color.Length == 0) || (hs && x.Size.Length == 0)) { errors.Add("صف " + x.Line + " (" + x.Name + "): اللون أو المقاس ناقص"); continue; }
                            var key = string.Join("|", new[] { hc ? x.Color : null, hs ? x.Size : null }.Where(z => z != null)); Dictionary<string, object> v; vars.TryGetValue(key, out v);
                            if (v == null) vars[key] = v = new Dictionary<string, object> { { "o", key.Split('|').Cast<object>().ToArray() }, { "stock", 0 }, { "active", true } };
                            v["price"] = x.Price != (decimal)b["price"] ? (object)x.Price : null; if (x.Price != (decimal)b["price"] && x.Old > x.Price) v["old"] = x.Old; if (x.Cost > 0) v["cost"] = x.Cost; if (x.Sku.Length > 0) v["sku"] = x.Sku; if (x.Qty.HasValue) { v["stock"] = x.Qty.Value; v.Remove("stock0"); }
                        }
                        b["options"] = opts.ToArray(); b["vars"] = vars.Values.Cast<object>().ToArray();
                    }
                    else
                    {
                        if (old != null && old.Options.Count > 0)
                        {
                            b["options"] = old.Options.Select(z => (object)new Dictionary<string, object> { { "n", new object[] { z.Ar, z.En } }, { "v", z.Vals.Select(q => (object)new object[] { q.Ar, q.En, q.Hex }).ToArray() } }).ToArray();
                            b["vars"] = old.Vars.Select(v => (object)new Dictionary<string, object> { { "o", v.Vals.Cast<object>().ToArray() }, { "price", v.Price.HasValue ? (object)v.Price.Value : null }, { "old", v.OldPrice.HasValue ? (object)v.OldPrice.Value : null }, { "cost", v.Cost.HasValue ? (object)v.Cost.Value : null }, { "stock", v.Stock }, { "stock0", v.Stock }, { "sku", v.Sku }, { "image", v.Image }, { "active", v.Active } }).ToArray();
                        }
                        else { int? q = g.Select(x => x.Qty).LastOrDefault(x => x.HasValue); if (q.HasValue) b["stock"] = q.Value; else if (old != null) { int st = old.Vars.Count > 0 ? old.Vars[0].Stock : 0; b["stock"] = st; b["stock0"] = st; } else { b["stock"] = 0; b["track"] = false; } }
                    }
                    // a category made on the way is in the copy the products are checked against
                    if (made.Count != knownCats) { var fresh = ShopCache.Get(c, sh); foreach (var k in fresh.Cats) if (!d.CatById.ContainsKey(k.Id)) { d.Cats.Add(k); d.CatById[k.Id] = k; } knownCats = made.Count; }
                    ShopOwner.SaveProductFrom(c, sh, ShopJson.Obj(ShopJson.Write(b)), d);
                    if (old == null) added++; else updated++;
                }
                catch (ShopException ex) { errors.Add("صف " + g.First().Line + " (" + g.Key + "): " + ex.Message); }
            }
            Shops.Bump(c, s.Id);
            try { ShopPlaces.FromXls(c, sh, rows, head, cell); } catch (ShopException ex) { errors.Add(ex.Message); }   // a column per place
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "added", added }, { "updated", updated }, { "cats", cats }, { "errors", errors.Take(40).ToArray() }, { "errorCount", errors.Count } });
        }
        static string Num(string v) { return ShopJson.Digits(v ?? "").Replace(",", "").Replace("ج.م", "").Replace("جنيه", "").Replace("EGP", "").Replace("LE", "").Trim(); }

        // "ملابس > عبايات": the category, made when it isn't there
        static int Category(SqlConnection c, Shop sh, string path, Dictionary<string, int> made, ref int count)
        {
            var parts = path.Split(new[] { '>', '/', '›', '<' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 1).Take(2).ToArray(); int parent = 0, id = 0;
            foreach (var p in parts)
            {
                string key = parent + "|" + p.ToLowerInvariant(); int had;
                if (made.TryGetValue(key, out had)) { id = had; parent = id; continue; }
                var d = ShopCache.Get(c, sh); var k = d.Cats.FirstOrDefault(x => string.Equals(x.Name, p, StringComparison.OrdinalIgnoreCase) && (x.ParentId ?? 0) == parent) ?? (parent == 0 && parts.Length == 1 ? d.Cats.FirstOrDefault(x => string.Equals(x.Name, p, StringComparison.OrdinalIgnoreCase)) : null);
                if (k != null) id = k.Id; else { id = ShopOwner.SaveCatFrom(c, sh, new Dictionary<string, object> { { "name", p }, { "parent", parent } }); count++; }
                made[key] = id; parent = id;
            }
            return id;
        }
    }
}
