// «مراجعة فلوس التحصيل»: per company, every delivered cash shipment: what the courier should collect (the shipment's amount), what was
// collected (Bosta says it in its «delivered» notification: the webhook's cod), and what the company paid the owner. None of the three
// APIs gives a payout per shipment (Bosta only a total not yet paid, ShipBlu's statements are on its dashboard, J&T none), so the owner
// uploads the company's statement (Excel or CSV, matched by tracking number), or ticks shipments as paid with an amount and a day.
// Shows what is owed, what came, what is missing, the shipments not paid after N days, and every difference. Excel out.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public static class ShopShipCash
    {
        public static bool On(SqlConnection c, StoreInfo s, Shop sh) { return s != null && Carriers.On(c, s) && ShopShip2.On(sh, "scash"); }
        static int Days(Shop sh) { return ShopShip2.Clamp(ShopJson.Int(ShopJson.At(ShopShip2.Of(sh, "scash"), "days"), 14), 3, 90); }
        static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        const string Base = "FROM dbo.Shipments x LEFT JOIN dbo.ShipCash m ON m.ShipmentId=x.Id WHERE x.StoreId=@s AND x.Source='shop' AND x.State='done' AND x.Ours='delivered' AND x.Cod>0";

        // the courier collected (Carriers.Happened, when the company says it with «delivered»)
        public static void Collected(SqlConnection c, CarrierShip x, decimal amount)
        {
            try { Exec(c, "UPDATE dbo.ShipCash SET Got=@g, GotSrc='hook' WHERE ShipmentId=@i; IF @@ROWCOUNT=0 INSERT INTO dbo.ShipCash(ShipmentId, StoreId, Got, GotSrc) VALUES(@i, @s, @g, 'hook')", "@g", Math.Max(0, amount), "@i", x.Id, "@s", x.StoreId); }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
        }

        class Row { public long Id; public string Company, Order, Tracking; public decimal Due; public decimal? Got, Paid; public DateTime? PaidOn, At; public string PaidSrc; public long OrderId; }
        static List<Row> Rows(SqlConnection c, int sid, string more, params object[] a)
        {
            var l = new List<Row>(); var args = new List<object> { "@s", sid }; args.AddRange(a);
            using (var cmd = Db.Cmd(c, "SELECT x.Id, x.Company, x.OrderName, x.Tracking, x.Cod, m.Got, m.Paid, m.PaidOn, m.PaidSrc, x.DoneAt, x.OrderId " + Base + more, args.ToArray())) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Row { Id = Convert.ToInt64(r[0]), Company = (string)r[1], Order = (string)r[2], Tracking = r[3] as string ?? "", Due = (decimal)r[4], Got = r[5] as decimal?, Paid = r[6] as decimal?, PaidOn = r[7] as DateTime?, PaidSrc = r[8] as string, At = r[9] is DateTime ? DateTime.SpecifyKind((DateTime)r[9], DateTimeKind.Utc) : (DateTime?)null, OrderId = long.Parse((string)r[10], CultureInfo.InvariantCulture) });
            return l;
        }
        // paid: all of it; part: less; more: more than it should; wait: not yet; late: not yet after N days. diff: the collected or paid amount isn't the shipment's
        static string State(Row x, int days)
        {
            if (x.Paid.HasValue) return x.Paid.Value == x.Due ? "paid" : x.Paid.Value < x.Due ? "part" : "more";
            return x.At.HasValue && x.At.Value < DateTime.UtcNow.AddDays(-days) ? "late" : "wait";
        }
        static bool Diff(Row x) { return (x.Paid.HasValue && x.Paid.Value != x.Due) || (x.Got.HasValue && x.Got.Value != x.Due); }
        static Dictionary<string, object> RowJson(Row x, int days)
        {
            return ShopShip2.D("id", x.Id, "c", x.Company, "cName", Carriers.Of(x.Company) == null ? x.Company : Carriers.Of(x.Company).Name, "order", x.OrderId, "name", x.Order, "tracking", x.Tracking, "due", x.Due, "got", x.Got, "paid", x.Paid, "on", x.PaidOn.HasValue ? x.PaidOn.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                "src", x.PaidSrc ?? "", "at", ShopShip2.Iso(x.At), "st", State(x, days), "diff", Diff(x));
        }

        static Dictionary<string, object> Page(SqlConnection c, Shop sh, string company, string f, int page)
        {
            int days = Days(sh); var all = Rows(c, sh.StoreId, " ORDER BY x.DoneAt DESC");
            var sums = Carriers.All.Select(co =>
            {
                var l = all.Where(x => x.Company == co.Id).ToList(); if (l.Count == 0) return null;
                return (object)ShopShip2.D("c", co.Id, "name", co.Name, "n", l.Count, "due", l.Sum(x => x.Due), "got", l.Where(x => x.Got.HasValue).Sum(x => x.Got.Value), "gotN", l.Count(x => x.Got.HasValue), "paid", l.Where(x => x.Paid.HasValue).Sum(x => x.Paid.Value),
                    "missing", l.Sum(x => Math.Max(0, x.Due - (x.Paid ?? 0))), "late", l.Count(x => State(x, days) == "late"), "lateSum", l.Where(x => State(x, days) == "late").Sum(x => x.Due), "diff", l.Count(Diff), "wait", l.Count(x => !x.Paid.HasValue));
            }).Where(x => x != null).ToArray();
            var pick = all.Where(x => company.Length == 0 || x.Company == company).Where(x => f == "owed" ? !x.Paid.HasValue || x.Paid.Value < x.Due : f == "late" ? State(x, days) == "late" : f == "diff" ? Diff(x) : f == "paid" ? x.Paid.HasValue : true).ToList();
            var files = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT TOP 10 Company, Name, Lines, Hit, Amount, At FROM dbo.ShipCashFiles WHERE StoreId=@s ORDER BY Id DESC", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) files.Add(ShopShip2.D("c", (string)r[0], "name", r[1] as string ?? "", "lines", (int)r[2], "hit", (int)r[3], "amount", (decimal)r[4], "at", ShopShip2.Iso(DateTime.SpecifyKind((DateTime)r[5], DateTimeKind.Utc))));
            return ShopShip2.D("ok", true, "days", days, "sums", sums, "total", pick.Count, "page", page, "size", 40, "list", pick.Skip(Math.Max(0, page) * 40).Take(40).Select(x => RowJson(x, days)).ToArray(), "files", files);
        }

        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (!On(c, s, sh)) return false; int page; int.TryParse(req.QueryString["page"], out page);
            switch (a)
            {
                case "scash": Web.Json(res, 200, Page(c, sh, req.QueryString["c"] ?? "", req.QueryString["f"] ?? "", page)); return true;
                case "scash-xls":
                    {
                        int days = Days(sh); var st = new Dictionary<string, string> { { "paid", "اتدفع" }, { "part", "اتدفع أقل" }, { "more", "اتدفع أكتر" }, { "wait", "لسه" }, { "late", "متأخر" } };
                        var rows = Rows(c, sh.StoreId, " ORDER BY x.DoneAt DESC").Select(x => new object[] { Carriers.Of(x.Company) == null ? x.Company : Carriers.Of(x.Company).Name, x.Order, x.Tracking, x.At.HasValue ? Carriers.Cairo(x.At.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "", x.Due, x.Got.HasValue ? (object)x.Got.Value : "", x.Paid.HasValue ? (object)x.Paid.Value : "", x.PaidOn.HasValue ? x.PaidOn.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "", Math.Max(0, x.Due - (x.Paid ?? 0)), st[State(x, days)], Diff(x) ? "فيه فرق" : "" });
                        var xls = ShopExcel.Book("التحصيل", new[] { "الشركة", "الأوردر", "رقم التتبع", "اتسلّم يوم", "المفروض يتحصّل", "اتحصّل", "اتدفع لك", "يوم الدفع", "الباقي", "الحالة", "ملاحظة" }, rows);
                        res.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; res.AddHeader("Content-Disposition", "attachment; filename=cash-" + Carriers.Cairo(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".xlsx"); res.Cache.SetCacheability(HttpCacheability.NoCache); res.BinaryWrite(xls); return true;
                    }
            }
            return false;
        }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res)
        {
            if (!On(c, s, sh)) return false;
            switch (a)
            {
                case "scash-pay": Pay(c, sh, b); Web.Json(res, 200, Page(c, sh, ShopShip2.Str(b, "c", 10), ShopShip2.Str(b, "f", 8), 0)); return true;
                case "scash-unpay": Exec(c, "UPDATE dbo.ShipCash SET Paid=NULL, PaidOn=NULL, PaidSrc=NULL, FileId=NULL WHERE ShipmentId=@i AND StoreId=@s", "@i", ShopShip2.Int(b, "id"), "@s", sh.StoreId); Web.Json(res, 200, Page(c, sh, ShopShip2.Str(b, "c", 10), ShopShip2.Str(b, "f", 8), 0)); return true;
                case "scash-file": File(c, sh, b, res); return true;
            }
            return false;
        }

        static DateTime? Day(string s) { DateTime d; return DateTime.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && d.Year > 2000 ? d : (DateTime?)null; }
        static void SetPaid(SqlConnection c, int sid, long id, decimal amount, DateTime day, string src, int? file)
        {
            try { Exec(c, "UPDATE dbo.ShipCash SET Paid=@p, PaidOn=@d, PaidSrc=@src, FileId=@f, At=SYSUTCDATETIME() WHERE ShipmentId=@i AND StoreId=@s; IF @@ROWCOUNT=0 INSERT INTO dbo.ShipCash(ShipmentId, StoreId, Paid, PaidOn, PaidSrc, FileId) VALUES(@i, @s, @p, @d, @src, @f)", "@p", amount, "@d", day, "@src", src, "@f", (object)file, "@i", id, "@s", sid); }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { SetPaid(c, sid, id, amount, day, src, file); }
        }
        // ticked by hand: {items: [{id, amount}], day}; an amount left out is the shipment's own
        static void Pay(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            var day = Day(ShopShip2.Str(b, "day", 10)) ?? Carriers.Cairo(DateTime.UtcNow).Date; if (day > Carriers.Cairo(DateTime.UtcNow).Date.AddDays(1)) throw new ShopException("day", "اليوم ده لسه ماجاش.");
            var items = ShopJson.List(ShopJson.At(b, "items")).OfType<Dictionary<string, object>>().Take(500).ToList(); if (items.Count == 0) throw new ShopException("none", "علّم على شحنة واحدة على الأقل.");
            var mine = Rows(c, sh.StoreId, " AND x.Id IN (" + string.Join(",", items.Select(x => (long)ShopShip2.Int(x, "id")).Where(x => x > 0).Distinct().DefaultIfEmpty(0)) + ")").ToDictionary(x => x.Id);
            foreach (var x in items) { Row r; if (!mine.TryGetValue(ShopShip2.Int(x, "id"), out r)) continue; var amt = ShopJson.DecN(ShopJson.At(x, "amount")) ?? r.Due; SetPaid(c, sh.StoreId, r.Id, ShopShip2.Clamp(amt, 0, 1000000), day, "hand", null); }
        }

        // ---------- the company's statement ----------
        static readonly Regex Amt = new Regex(@"cod|collect|cash|amount|paid|payout|transfer|net|مبلغ|المبلغ|تحصيل|التحصيل|المحصل|محصل|مدفوع|قيمه|القيمه|صافي", RegexOptions.IgnoreCase), NotAmt = new Regex(@"fee|fees|shipping|charge|vat|tax|date|رسوم|شحن|مصاريف|ضريب|عموله|تاريخ", RegexOptions.IgnoreCase), DateH = new Regex(@"date|day|paid|payout|transfer|تاريخ|يوم", RegexOptions.IgnoreCase);
        static decimal? Money(string raw)
        {
            var t = Regex.Replace(ShopJson.Digits(raw ?? ""), @"[^\d.,\-]", "").Trim('.', ','); if (t.IndexOf(',') >= 0 && t.IndexOf('.') < 0 && Regex.IsMatch(t, @",\d{1,2}$")) t = t.Replace(',', '.'); t = t.Replace(",", "");
            decimal d; return t.Length > 0 && decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out d) && d >= 0 && d < 10000000 ? Math.Round(d, 2) : (decimal?)null;
        }
        static string Clean(string s) { return Regex.Replace((s ?? "").Trim().TrimStart('\''), @"\s", ""); }
        static void File(SqlConnection c, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var co = Carriers.Of(ShopShip2.Str(b, "c", 10)); if (co == null) throw new ShopException("company", "اختار شركة الشحن اللي الكشف بتاعها.");
            if (!Web.Allow("scash-file:" + sh.StoreId, 40, TimeSpan.FromHours(1))) throw new ShopException("too_many", "رفعت ملفات كتير. استنى شوية.");
            var raw = ShopShip2.Str(b, "data", 3500000); int comma = raw.IndexOf(','); if (raw.StartsWith("data:", StringComparison.Ordinal) && comma > 0) raw = raw.Substring(comma + 1);
            byte[] data; try { data = Convert.FromBase64String(raw); } catch { throw new ShopException("file", "الملف ماتقراش. ارفعه تاني."); }
            if (data.Length < 10 || data.Length > 2500000) throw new ShopException("file", "الملف لازم يكون Excel أو CSV وأصغر من 2.5 ميجا.");
            bool xlsx = data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4; if (!xlsx && data.Take(4000).Any(x => x == 0)) throw new ShopException("file", "الملف ده مش Excel ولا CSV.");
            List<string[]> rows; try { rows = xlsx ? Crm.Sheet(data) : Crm.Csv(Extract.PlainText(data)); } catch (Exception) { throw new ShopException("file", "الملف ماتقراش. احفظه من Excel تاني (xlsx أو CSV) وارفعه."); }
            // the store's shipments with this company, by number
            var ours = Rows(c, sh.StoreId, " AND x.Company=@c", "@c", co.Id).Where(x => x.Tracking.Length > 0).GroupBy(x => Clean(x.Tracking)).ToDictionary(g => g.Key, g => g.First());
            // the number's column: the one where most cells are our shipments; the header is the last row above the first one
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Length), tc = -1, best = 0;
            for (int i = 0; i < cols; i++) { int n = rows.Count(r => i < r.Length && ours.ContainsKey(Clean(r[i]))); if (n > best) { best = n; tc = i; } }
            if (tc < 0) throw new ShopException("match", "مالقيناش في الملف أي رقم تتبع من شحناتك مع " + co.Name + ". اتأكد إنه كشف " + co.Name + ".");
            int first = rows.FindIndex(r => tc < r.Length && ours.ContainsKey(Clean(r[tc]))), hr = -1; for (int i = first - 1; i >= 0; i--) if (rows[i].Count(x => !string.IsNullOrWhiteSpace(x)) >= 2) { hr = i; break; }
            var head = hr < 0 ? new string[0] : rows[hr]; Func<int, string> name = i => i < head.Length && !string.IsNullOrWhiteSpace(head[i]) ? head[i].Trim() : "عمود " + (i + 1);
            int ac = ShopShip2.Int(b, "amt", -1), dc = ShopShip2.Int(b, "date", -1); var data0 = rows.Skip(first).ToList();
            if (ac < 0 || ac >= cols)
            {
                // a money column by its name (not the fees), else none: the owner chooses
                var cand = Enumerable.Range(0, cols).Where(i => i != tc && Amt.IsMatch(ShopGeo.Fold(name(i))) && !NotAmt.IsMatch(ShopGeo.Fold(name(i))) && data0.Count(r => i < r.Length && Money(r[i]).HasValue) >= Math.Max(1, data0.Count / 2)).ToList();
                var named = cand.Where(i => Regex.IsMatch(ShopGeo.Fold(name(i)), "collect|cod|محصل|التحصيل|تحصيل", RegexOptions.IgnoreCase)).ToList();
                ac = cand.Count == 1 ? cand[0] : named.Count == 1 ? named[0] : -1;
            }
            if (ac < 0) { Web.Json(res, 400, ShopShip2.D("error", "column", "message", "مش عارفين أنهي عمود فيه المبلغ. اختاره.", "data", ShopShip2.D("cols", Enumerable.Range(0, cols).Where(i => i != tc).Select(i => ShopShip2.D("i", i, "t", name(i), "ex", data0.Count > 0 && i < data0[0].Length ? data0[0][i] : "")).ToArray(), "track", name(tc)))); return; }
            if (dc < 0 || dc >= cols) dc = Enumerable.Range(0, cols).Where(i => i != tc && i != ac && DateH.IsMatch(ShopGeo.Fold(name(i))) && data0.Any(r => i < r.Length && Crm.ReadDate(r[i]).HasValue)).DefaultIfEmpty(-1).First();
            var fallback = Day(ShopShip2.Str(b, "day", 10)) ?? Carriers.Cairo(DateTime.UtcNow).Date; int lines = 0, hit = 0, other = 0, noAmt = 0; decimal sum = 0; var seen = new HashSet<long>();
            int fid; using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShipCashFiles(StoreId, Company, Name, Lines, Hit, Amount) OUTPUT INSERTED.Id VALUES(@s, @c, @n, 0, 0, 0)", "@s", sh.StoreId, "@c", co.Id, "@n", Carriers.Cut(ShopShip2.Str(b, "name", 120), 120))) fid = (int)cmd.ExecuteScalar();
            foreach (var r in data0)
            {
                if (tc >= r.Length || Clean(r[tc]).Length < 4) continue; lines++; Row x; if (!ours.TryGetValue(Clean(r[tc]), out x)) { other++; continue; }
                var amt = ac < r.Length ? Money(r[ac]) : null; if (!amt.HasValue) { noAmt++; continue; }
                var day = dc >= 0 && dc < r.Length ? Crm.ReadDate(r[dc]) : null; var on = day.HasValue ? Carriers.Cairo(day.Value).Date : fallback;
                // the same shipment twice in one statement: its lines add up
                decimal one = amt.Value; if (!seen.Add(x.Id)) amt = (Rows(c, sh.StoreId, " AND x.Id=@i", "@i", x.Id).First().Paid ?? 0) + one; else hit++;
                SetPaid(c, sh.StoreId, x.Id, amt.Value, on, "file", fid); sum += one;
            }
            Exec(c, "UPDATE dbo.ShipCashFiles SET Lines=@l, Hit=@h, Amount=@a WHERE Id=@i", "@l", lines, "@h", hit, "@a", sum, "@i", fid);
            var page = Page(c, sh, co.Id, "", 0); page["file"] = ShopShip2.D("lines", lines, "hit", hit, "other", other, "noAmt", noAmt, "amount", sum, "track", name(tc), "amt", name(ac), "date", dc < 0 ? "" : name(dc));
            Web.Json(res, 200, page);
        }
    }
}
