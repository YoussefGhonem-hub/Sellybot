// The companies' own areas (dbo.CarrierAreas, filled again every week) and finding the one an order goes to: our governorate and city (ShopGeo)
// and the customer's area, in Arabic written many ways. Sure only when one area fits; otherwise the owner picks, and the pick is kept for that
// city (dbo.CarrierPicks, each store its own). A company's area is named by its three ids together: they don't change between refreshes.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public class CarrierAreaRow
    {
        public string GovId, Gov, GovEn, CityId, City, CityEn, AreaId, Area, AreaEn; public bool Closed;
        public string FG, FC, FA;   // folded (CarrierAreas.Fold)
        public string Key { get { return GovId + "~" + CityId + "~" + AreaId; } }
        public string Label { get { return string.Join(" › ", new[] { Gov, City == Gov || City == Area ? null : City, Area }.Where(x => !string.IsNullOrEmpty(x))); } }
    }
    // Row with Sure = send it; Row without Sure never happens: More is what the owner picks from. Closed: the place is known and the company doesn't deliver there.
    public class CarrierHit { public CarrierAreaRow Row; public bool Sure, Picked, Closed; public List<CarrierAreaRow> More = new List<CarrierAreaRow>(); }

    public static class CarrierAreas
    {
        const double Near = .86, Like = .8;
        static readonly TimeSpan Fresh = TimeSpan.FromDays(7), Memory = TimeSpan.FromMinutes(10);
        class Copy { public DateTime At; public List<CarrierAreaRow> Rows; public Dictionary<string, CarrierAreaRow> ByKey; }
        static readonly ConcurrentDictionary<string, Copy> Copies = new ConcurrentDictionary<string, Copy>();

        // ---------- words ----------
        static readonly HashSet<string> Plain = new HashSet<string> { "مدينه", "مركز", "حي", "قسم", "محافظه", "منطقه", "بندر", "قريه", "الحي", "من" };
        // «الحي السابع» and «الحي 7»
        static readonly Dictionary<string, string> Nth = new Dictionary<string, string> { { "اول", "1" }, { "اولي", "1" }, { "ثاني", "2" }, { "تاني", "2" }, { "ثالث", "3" }, { "تالت", "3" }, { "رابع", "4" }, { "خامس", "5" }, { "سادس", "6" }, { "سابع", "7" }, { "ثامن", "8" }, { "تامن", "8" }, { "تاسع", "9" }, { "عاشر", "10" } };
        public static string Fold(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(); string n;
            foreach (var ch in ShopJson.Digits(s).ToLowerInvariant())
            {
                if ((ch >= 'ً' && ch <= 'ٟ') || ch == 'ـ' || ch == 'ٰ') continue;   // the vowel marks and the stretch
                char x = ch == 'أ' || ch == 'إ' || ch == 'آ' || ch == 'ٱ' ? 'ا' : ch == 'ة' ? 'ه' : ch == 'ى' || ch == 'ئ' ? 'ي' : ch == 'ؤ' ? 'و' : ch == 'گ' ? 'ك' : ch;
                sb.Append(char.IsLetterOrDigit(x) ? x : ' ');
            }
            var t = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            t = Regex.Replace(t, "(ال)?خامس عشر", "15"); t = Regex.Replace(t, @"(\d)(st|nd|rd|th)\b", "$1");
            var w = t.Split(' ').Where(x => x.Length > 0).ToList(); var keep = w.Where(x => !Plain.Contains(x)).ToList(); if (keep.Count == 0) keep = w;
            return string.Join(" ", keep.Select(x => x.StartsWith("ال", StringComparison.Ordinal) && x.Length > 4 ? x.Substring(2) : x).Select(x => Nth.TryGetValue(x, out n) ? n : x));
        }
        // 1 = the same; a letter or two off in a long name still passes Near; one name inside the other is only Like
        public static double Sim(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0; if (a == b) return 1;
            string x = a.Replace(" ", ""), y = b.Replace(" ", ""); if (x == y) return .99; double best = 0; int max = Math.Max(x.Length, y.Length);
            // another number is another place («الحي 7» and «الحي 17»)
            string nx = Regex.Replace(a, @"\D+", " ").Trim(), ny = Regex.Replace(b, @"\D+", " ").Trim();
            if (nx == ny && Math.Abs(x.Length - y.Length) <= max * .3) { int d = Lev(x, y); if (max >= 4) best = 1 - (double)d / max; if (max < 6 && d > 0) best = Math.Min(best, .79); }
            if (best < .81) { var ta = a.Split(' '); var tb = b.Split(' '); var s = ta.Length <= tb.Length ? ta : tb; var l = s == ta ? tb : ta; if (s.All(l.Contains) && s.Sum(z => z.Length) >= 4) best = .81; }
            return best;
        }
        static int Lev(string a, string b)
        {
            var p = new int[b.Length + 1]; for (int j = 0; j <= b.Length; j++) p[j] = j;
            for (int i = 1; i <= a.Length; i++) { int last = p[0]; p[0] = i; for (int j = 1; j <= b.Length; j++) { int up = p[j]; p[j] = Math.Min(Math.Min(p[j] + 1, p[j - 1] + 1), last + (a[i - 1] == b[j - 1] ? 0 : 1)); last = up; } }
            return p[b.Length];
        }

        // ---------- the rows ----------
        public static int Count(SqlConnection c, string company) { return Load(c, company).Rows.Count; }
        static Copy Load(SqlConnection c, string company)
        {
            Copy m; if (Copies.TryGetValue(company, out m) && DateTime.UtcNow - m.At < (m.Rows.Count == 0 ? TimeSpan.FromSeconds(3) : Memory)) return m;
            m = new Copy { At = DateTime.UtcNow, Rows = new List<CarrierAreaRow>(), ByKey = new Dictionary<string, CarrierAreaRow>() };
            using (var cmd = Db.Cmd(c, "SELECT GovId, Gov, GovEn, CityId, City, CityEn, AreaId, Area, AreaEn, Closed FROM dbo.CarrierAreas WHERE Company=@c ORDER BY Id", "@c", company)) using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var a = new CarrierAreaRow { GovId = (string)r[0], Gov = (string)r[1], GovEn = r[2] as string, CityId = (string)r[3], City = (string)r[4], CityEn = r[5] as string, AreaId = (string)r[6], Area = (string)r[7], AreaEn = r[8] as string, Closed = (bool)r[9] };
                    a.FG = Fold(a.Gov); a.FC = Fold(a.City); a.FA = Fold(a.Area); m.Rows.Add(a); m.ByKey[a.Key] = a;
                }
            Copies[company] = m; return m;
        }
        public static CarrierAreaRow ByKey(SqlConnection c, string company, string key) { CarrierAreaRow a; return Load(c, company).ByKey.TryGetValue(key ?? "", out a) ? a : null; }
        // their names for a governorate of ours
        static List<CarrierAreaRow> InGov(Copy m, int gov)
        {
            var g = ShopGeo.Get(gov); if (g == null) return new List<CarrierAreaRow>(); string f = Fold(g.Ar);
            var ids = new HashSet<string>(m.Rows.GroupBy(x => x.GovId).Select(x => x.First()).Where(x => Sim(x.FG, f) >= Like || string.Equals(x.GovEn, g.En, StringComparison.OrdinalIgnoreCase)).Select(x => x.GovId));
            return m.Rows.Where(x => ids.Contains(x.GovId)).ToList();
        }
        static string Place(string city, string area) { var t = Fold(city) + "|" + Fold(area); return t.Length > 160 ? t.Substring(0, 160) : t; }
        static CarrierAreaRow Pick(SqlConnection c, Copy m, int storeId, string company, int gov, string place)
        {
            using (var cmd = Db.Cmd(c, "SELECT GovId+N'~'+CityId+N'~'+AreaId FROM dbo.CarrierPicks WHERE StoreId=@s AND Company=@c AND Gov=@g AND Place=@p", "@s", storeId, "@c", company, "@g", gov, "@p", place))
            { CarrierAreaRow a; var k = cmd.ExecuteScalar() as string; return k != null && m.ByKey.TryGetValue(k, out a) && !a.Closed ? a : null; }
        }
        // the owner's pick: for this city and area, and for the city alone when it has none yet
        public static void Remember(SqlConnection c, int storeId, string company, int gov, string city, string area, CarrierAreaRow a)
        {
            foreach (var p in new[] { Place(city, area), Place(city, "") }.Distinct())
            {
                bool alone = p == Place(city, "") && Place(city, area) != p;
                try { Carriers.Exec(c, (alone ? "" : "UPDATE dbo.CarrierPicks SET GovId=@x, CityId=@y, AreaId=@z, At=SYSUTCDATETIME() WHERE StoreId=@s AND Company=@c AND Gov=@g AND Place=@p; IF @@ROWCOUNT=0 ") + (alone ? "IF NOT EXISTS (SELECT 1 FROM dbo.CarrierPicks WHERE StoreId=@s AND Company=@c AND Gov=@g AND Place=@p) " : "") + "INSERT INTO dbo.CarrierPicks(StoreId, Company, Gov, Place, GovId, CityId, AreaId) VALUES(@s, @c, @g, @p, @x, @y, @z)", "@s", storeId, "@c", company, "@g", gov, "@p", p, "@x", a.GovId, "@y", a.CityId, "@z", a.AreaId); }
                catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
            }
        }

        // The checkout's «هتستلم مع مين؟» (ShopShipPick.Quote): false only when the company's own list is sure it doesn't deliver there.
        // That is: every area it has in the governorate is closed, or the customer's city is one closed area with no open one beside it.
        // Unsure (no list yet, our governorate's name not found in theirs, a city of many areas): it delivers, as before.
        public static bool Delivers(SqlConnection c, int storeId, string company, int gov, string city)
        {
            var m = Load(c, company); if (m.Rows.Count == 0 || gov <= 0) return true;
            var pool = InGov(m, gov); if (pool.Count == 0 || !pool.Any(r => r.Closed)) return true;
            if (pool.All(r => r.Closed)) return false;
            if (string.IsNullOrWhiteSpace(city)) return true;
            var h = Match(c, storeId, company, gov, city, ""); return !(h.Closed && h.More.Count == 0);
        }

        public static CarrierHit Match(SqlConnection c, int storeId, string company, int gov, string city, string area)
        {
            var m = Load(c, company); var h = new CarrierHit(); if (m.Rows.Count == 0) return h; string fc = Fold(city), fa = Fold(area);
            var pick = Pick(c, m, storeId, company, gov, Place(city, area)); if (pick != null) { h.Row = pick; h.Sure = h.Picked = true; return h; }
            var pool = InGov(m, gov); var g = ShopGeo.Get(gov); string fg = g == null ? "" : Fold(g.Ar);
            Func<List<CarrierAreaRow>, string, List<CarrierAreaRow>> near = (l, t) => { if (t.Length == 0) return new List<CarrierAreaRow>(); var x = l.Where(r => r.FA == t).ToList(); return x.Count > 0 ? x : l.Where(r => Sim(t, r.FA) >= Near).ToList(); };
            // the customer's own area, inside their city («الحي السابع» is in many cities)
            var inCity = pool.Where(r => Sim(fc, r.FC) >= Like || Sim(fc, r.FA) >= Near || fc.Length == 0 || fc == fg).ToList(); var hit = near(inCity, fa); if (hit.Count > 1) { var same = hit.Where(r => Sim(fc, r.FC) >= Near).ToList(); if (same.Count > 0) hit = same; }
            if (hit.Count != 1 && fc.Length > 1)
            {
                // the city is one of their areas («الزمالك»), or a city of theirs with a single area
                var byArea = near(pool, fc); var byCity = pool.Where(r => Sim(fc, r.FC) >= Near).ToList(); if (byCity.Any(r => r.FC == fc)) byCity = byCity.Where(r => r.FC == fc).ToList();
                if (byArea.Count > 1 && byCity.Count > 0) { var both = byArea.Where(byCity.Contains).ToList(); if (both.Count > 0) byArea = both; }
                hit = byArea.Count == 1 ? byArea : byCity.Count == 1 ? byCity : new List<CarrierAreaRow>();
                h.More = (hit.Count == 1 ? hit : byArea.Concat(byCity.OrderByDescending(r => Sim(fa, r.FA)))).Distinct().ToList();
            }
            if (hit.Count == 1) { if (hit[0].Closed) { h.Closed = true; h.More = pool.Where(r => r.CityId == hit[0].CityId && !r.Closed).Take(30).ToList(); } else { h.Row = hit[0]; h.Sure = true; return h; } }
            pick = Pick(c, m, storeId, company, gov, Place(city, "")); if (pick != null) { h.Row = pick; h.Sure = h.Picked = true; h.Closed = false; return h; }
            // what it may be: the same name elsewhere (they count «العبور» under another governorate), then the nearest names
            var more = h.More.Concat(near(inCity, fa)).Concat(fc.Length > 2 ? m.Rows.Where(r => r.FC == fc || r.FA == fc) : new CarrierAreaRow[0]).Concat(pool.Select(r => new { r, n = Math.Max(Math.Max(Sim(fa, r.FA), Sim(fc, r.FA)), Math.Max(Sim(fa, r.FC), Sim(fc, r.FC))) }).Where(x => x.n >= .55).OrderByDescending(x => x.n).Select(x => x.r));
            h.More = more.Where(r => !r.Closed).Distinct().Take(30).ToList(); if (h.Closed || (h.More.Count == 0 && pool.Count > 0 && pool.All(r => r.Closed))) h.Closed = true;
            return h;
        }
        // the owner's search box
        public static List<CarrierAreaRow> Search(SqlConnection c, string company, int gov, string q)
        {
            var m = Load(c, company); string f = Fold(q), en = (q ?? "").Trim().ToLowerInvariant(); var pool = gov > 0 ? InGov(m, gov) : m.Rows; if (pool.Count == 0) pool = m.Rows;
            if (f.Length == 0) return pool.Take(40).ToList();
            Func<List<CarrierAreaRow>, List<CarrierAreaRow>> find = l => l.Select(r => new { r, n = r.FA == f ? 3 : r.FC == f ? 2.5 : r.FA.Contains(f) ? 2 : r.FC.Contains(f) ? 1.6 : en.Length > 2 && ((r.AreaEn ?? "").ToLowerInvariant().Contains(en) || (r.CityEn ?? "").ToLowerInvariant().Contains(en)) ? 1.5 : Math.Max(Sim(f, r.FA), Sim(f, r.FC)) >= .7 ? 1 : 0 }).Where(x => x.n > 0).OrderByDescending(x => x.n).Select(x => x.r).Take(40).ToList();
            var got = find(pool); return got.Count > 0 || pool.Count == m.Rows.Count ? got : find(m.Rows);
        }

        // ---------- filling them again ----------
        static readonly ConcurrentDictionary<string, int> Busy = new ConcurrentDictionary<string, int>();
        // the rows that fit dbo.CarrierAreas: an id longer than its column (40) would stop the whole load, so that one row is left out
        internal static List<CarrierAreaRow> Usable(List<CarrierAreaRow> l)
        {
            return l == null ? null : l.Where(x => !string.IsNullOrEmpty(x.AreaId) && !string.IsNullOrEmpty(x.Area) && x.Key.Length <= 122 && (x.GovId ?? "").Length <= 40 && (x.CityId ?? "").Length <= 40 && x.AreaId.Length <= 40).GroupBy(x => x.Key).Select(x => x.First()).ToList();
        }
        public static async Task Refresh(Carrier co, bool now, CancellationToken ct)
        {
            if (!Busy.TryAdd(co.Id, 1)) return;
            try
            {
                string key = "CarrierAreasAt_" + co.Id, stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), cut = DateTime.UtcNow.Subtract(Fresh).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture); CarrierLink k;
                using (var c = Db.Open())
                {
                    int sid = Carriers.Num(c, "SELECT TOP 1 StoreId FROM dbo.Carriers WHERE Company=@c ORDER BY CASE WHEN LastErrorAt IS NULL THEN 0 ELSE 1 END, LinkedAt DESC", "@c", co.Id); if (sid == 0) return;
                    k = Carriers.Link(c, sid, co.Id); if (k == null || string.IsNullOrEmpty(k.Key) || co.Closed(c) != null) return;
                    // one worker process of the two, and one try an hour when it fails
                    using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key]=@k) INSERT INTO dbo.AppSettings([Key],[Value]) VALUES(@k,'0'); UPDATE dbo.AppSettings SET [Value]=@n WHERE [Key]=@k AND ([Value]<@cut OR (@now=1 AND [Value]<@soon)); SELECT @@ROWCOUNT", "@k", key, "@n", stamp, "@cut", cut, "@now", now && Count(c, co.Id) == 0 ? 1 : 0, "@soon", DateTime.UtcNow.AddSeconds(-20).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture))) if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) return;
                }
                List<CarrierAreaRow> l = null; string why = null;
                try { l = await co.Areas(k); } catch (CarrierException ex) { why = ex.Message; }
                using (var c = Db.Open())
                {
                    int had = Carriers.Num(c, "SELECT COUNT(*) FROM dbo.CarrierAreas WHERE Company=@c", "@c", co.Id); l = Usable(l);
                    // nothing, or a good part of them missing: the company's answer is broken, the old ones stay
                    if (l == null || l.Count == 0 || l.Count < had * .7)
                    {
                        Log.Error(new Exception("Carrier areas " + co.Id + ": " + (why ?? (l == null ? 0 : l.Count) + " of " + had)));
                        Billing.Set(c, key, DateTime.UtcNow.Subtract(Fresh).AddHours(1).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)); return;
                    }
                    var t = new DataTable(); foreach (var n in new[] { "Company", "GovId", "Gov", "GovEn", "CityId", "City", "CityEn", "AreaId", "Area", "AreaEn" }) t.Columns.Add(n, typeof(string)); t.Columns.Add("Closed", typeof(bool));
                    foreach (var x in l) t.Rows.Add(co.Id, x.GovId, Carriers.Cut(x.Gov, 100), Carriers.Cut(x.GovEn, 100), x.CityId, Carriers.Cut(x.City, 120), Carriers.Cut(x.CityEn, 120), x.AreaId, Carriers.Cut(x.Area, 160), Carriers.Cut(x.AreaEn, 160), x.Closed);
                    using (var tx = c.BeginTransaction())
                    {
                        using (var cmd = Db.Cmd(c, "DELETE FROM dbo.CarrierAreas WHERE Company=@c", "@c", co.Id)) { cmd.Transaction = tx; cmd.CommandTimeout = 120; cmd.ExecuteNonQuery(); }
                        using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "dbo.CarrierAreas", BulkCopyTimeout = 120 }) { foreach (DataColumn n in t.Columns) bulk.ColumnMappings.Add(n.ColumnName, n.ColumnName); bulk.WriteToServer(t); }
                        tx.Commit();
                    }
                }
                Copy old; Copies.TryRemove(co.Id, out old);
            }
            catch (Exception ex) { Log.Error(ex); }
            finally { int x; Busy.TryRemove(co.Id, out x); }
        }
    }
}
