// «قوالب مواسم»: a ready season look laid over the store's template (Ramadan, Eid, White Friday...). The look keeps
// season = {k, from, to, offer}: the first and the last day (Cairo) and, if the owner likes, one of their offers whose products get the
// season's badge. The engine draws it (site/shop/season.js) only between those days, so it ends by itself on its last day; the private
// preview shows it before it starts. The owner types nothing but the days: the greeting, the headline and the colours are ready.
// Lunar seasons come from the Umm al-Qura calendar and Sham El-Nessim from the Coptic Easter: the owner is told they are expected days.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RiwaqChat
{
    public static class ShopSeasons
    {
        public class Season { public string K, Ar, En; public bool Lunar; }
        public static readonly Season[] All =
        {
            new Season { K = "ramadan", Ar = "رمضان", En = "Ramadan", Lunar = true },
            new Season { K = "eidfitr", Ar = "عيد الفطر", En = "Eid al-Fitr", Lunar = true },
            new Season { K = "eidadha", Ar = "عيد الأضحى", En = "Eid al-Adha", Lunar = true },
            new Season { K = "blackfriday", Ar = "الجمعة البيضاء", En = "White Friday" },
            new Season { K = "valentine", Ar = "عيد الحب", En = "Valentine's Day" },
            new Season { K = "mothersday", Ar = "عيد الأم", En = "Mother's Day" },
            new Season { K = "school", Ar = "المدارس", En = "Back to school" },
            new Season { K = "shamnessim", Ar = "شم النسيم", En = "Sham El-Nessim" },
            new Season { K = "summer", Ar = "الصيف", En = "Summer" },
            new Season { K = "newyear", Ar = "رأس السنة", En = "New Year" },
        };
        public static Season Of(string k) { return All.FirstOrDefault(s => s.K == k); }

        public const string Schema = @"
-- what the owner already saw once (the season's suggestion line in «متجري»)
IF OBJECT_ID('dbo.ShopLookMarks') IS NULL
  CREATE TABLE dbo.ShopLookMarks(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Mark NVARCHAR(40) NOT NULL, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), PRIMARY KEY(StoreId, Mark));
";

        public static DateTime Today() { return Carriers.Cairo(DateTime.UtcNow).Date; }

        // ---------- the days ----------
        static readonly UmAlQuraCalendar Hijri = new UmAlQuraCalendar();
        static DateTime Hd(int y, int m, int d) { return Hijri.ToDateTime(y, m, d, 0, 0, 0, 0); }
        // the Coptic (Orthodox) Easter: the Julian computus, 13 days later in the Gregorian calendar (1900 to 2099)
        static DateTime Easter(int y)
        {
            int a = y % 4, b = y % 7, c = y % 19, d = (19 * c + 15) % 30, e = (2 * a + 4 * b - d + 34) % 7, n = d + e + 114;
            return new DateTime(y, n / 31, n % 31 + 1).AddDays(13);
        }
        static DateTime LastFriday(int y) { var t = new DateTime(y, 11, 30); while (t.DayOfWeek != DayOfWeek.Friday) t = t.AddDays(-1); return t; }
        static IEnumerable<DateTime[]> Ranges(string k, DateTime today)
        {
            int h = Hijri.GetYear(today);
            for (int i = -1; i <= 1; i++)
            {
                int y = today.Year + i, hy = h + i; DateTime a;
                switch (k)
                {
                    case "ramadan": a = Hd(hy, 9, 1); yield return new[] { a, a.AddDays(Hijri.GetDaysInMonth(hy, 9) - 1) }; break;
                    case "eidfitr": a = Hd(hy, 10, 1); yield return new[] { a, a.AddDays(2) }; break;
                    case "eidadha": a = Hd(hy, 12, 10); yield return new[] { a.AddDays(-7), a.AddDays(3) }; break;   // the week before is when people buy
                    case "blackfriday": a = LastFriday(y); yield return new[] { a.AddDays(-6), a.AddDays(3) }; break;
                    case "valentine": yield return new[] { new DateTime(y, 2, 7), new DateTime(y, 2, 14) }; break;
                    case "mothersday": yield return new[] { new DateTime(y, 3, 14), new DateTime(y, 3, 21) }; break;   // 21 March in Egypt
                    case "school": yield return new[] { new DateTime(y, 9, 1), new DateTime(y, 9, 30) }; break;
                    case "shamnessim": a = Easter(y).AddDays(1); yield return new[] { a.AddDays(-6), a }; break;
                    case "summer": yield return new[] { new DateTime(y, 7, 1), new DateTime(y, 8, 31) }; break;
                    case "newyear": yield return new[] { new DateTime(y, 12, 24), new DateTime(y + 1, 1, 2) }; break;
                }
            }
        }
        // the next time a season comes (or the one going on now)
        public static DateTime[] Next(string k, DateTime today) { return Ranges(k, today).Where(r => r[1] >= today).OrderBy(r => r[0]).FirstOrDefault(); }
        static string Iso(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        static bool Day(object v, out DateTime d) { return DateTime.TryParseExact(ShopJson.Str(v, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d); }

        // ---------- what the look keeps (ShopLook.Clean) ----------
        public const int MaxDays = 75;
        public static object Clean(object v)
        {
            var d = v as Dictionary<string, object>; if (d == null) return null;
            var k = ShopJson.Str(ShopJson.At(d, "k"), 20); DateTime f, t;
            if (Of(k) == null || !Day(ShopJson.At(d, "from"), out f) || !Day(ShopJson.At(d, "to"), out t) || t < f || (t - f).TotalDays >= MaxDays) return null;
            return new Dictionary<string, object> { { "k", k }, { "from", Iso(f) }, { "to", Iso(t) }, { "offer", Math.Max(0, ShopJson.Int(ShopJson.At(d, "offer"))) } };
        }
        // null = none; otherwise the season and its days
        public static Dictionary<string, object> Get(Dictionary<string, object> look) { return Clean(ShopJson.At(look, "season")) as Dictionary<string, object>; }
        public static bool Running(Dictionary<string, object> look, DateTime today)
        {
            var s = Get(look); DateTime f, t; return s != null && Day(s["from"], out f) && Day(s["to"], out t) && f <= today && today <= t;
        }

        // ---------- the page ----------
        // the engine's file, before run.js: when the season is on, or in the preview until it ends (the editor may add one later: core.js loads it then)
        public static string Script(ShopFrontHandler.Ctx x)
        {
            var s = Get(x.Look); DateTime t, today = Today();
            if (s == null || !Day(s["to"], out t) || t < today || (!x.Preview && !Running(x.Look, today))) return "";
            return "<script src=\"/_s/season.js?v=" + ShopPages.Ver("season.js") + "\" defer></script>";
        }
        // the store's day (the engine compares the season's days with it, not with the phone's clock) and the file's version
        public static void Boot(Dictionary<string, object> boot, ShopFrontHandler.Ctx x)
        {
            boot["cday"] = Iso(Today());
            var a = boot["a"] as Dictionary<string, object>; if (a != null) a["sea"] = ShopPages.Ver("season.js");
        }

        // the assistant: the look is only a look, the offers are what is real
        public static void Bot(Shop sh, StringBuilder sb)
        {
            var look = sh.Look(false); var s = Get(look); if (s == null || !Running(look, Today())) return;
            sb.Append("المتجر لابس شكل موسم ").Append(Of((string)s["k"]).Ar).Append(" لحد ").Append(s["to"]).Append(". ده شكل بس: ماتوعدش بخصم غير العروض الشغالة اللي فوق.\n");
        }
        // «انشر»: what changes, in words
        public static void Diff(Dictionary<string, object> live, Dictionary<string, object> draft, List<string> l)
        {
            if (ShopJson.Write(Get(live)) != ShopJson.Write(Get(draft))) l.Add("شكل الموسم");
        }

        // ---------- «متجري» ----------
        public static Dictionary<string, object> OwnerJson(SqlConnection c, Shop sh)
        {
            var today = Today(); var d = ShopCache.Get(c, sh); var now = DateTime.UtcNow;
            var seen = new HashSet<string>(); using (var cmd = Db.Cmd(c, "SELECT Mark FROM dbo.ShopLookMarks WHERE StoreId=@s", "@s", sh.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) seen.Add((string)r[0]);
            var list = All.Select(s => { var n = Next(s.K, today); return new Dictionary<string, object> { { "k", s.K }, { "ar", s.Ar }, { "en", s.En }, { "lunar", s.Lunar }, { "from", n == null ? "" : Iso(n[0]) }, { "to", n == null ? "" : Iso(n[1]) } }; }).ToList();
            // the line in «متجري»: two weeks before a season (and while it goes on), once, unless the look already has it
            var mine = Get(sh.Look(true)); Dictionary<string, object> tip = null;
            foreach (var x in list.Where(x => (string)x["from"] != "").OrderBy(x => (string)x["from"]))
            {
                DateTime f, t; Day(x["from"], out f); Day(x["to"], out t);
                if (today < f.AddDays(-14) || today > t.AddDays(-2) || seen.Contains(Mark((string)x["k"], f))) continue;
                if (mine != null && (string)mine["k"] == (string)x["k"]) continue;
                tip = x; break;
            }
            return new Dictionary<string, object> { { "today", Iso(today) }, { "list", list }, { "tip", tip }, { "maxDays", MaxDays },
                { "offers", d.Offers.Where(o => o.Active && (!o.EndsAt.HasValue || o.EndsAt.Value > now)).OrderBy(o => o.Sort).Take(60).Select(o => new Dictionary<string, object> { { "id", o.Id }, { "name", o.Name }, { "on", o.Running(now) }, { "from", ShopPromo.Local(o.StartsAt) }, { "to", ShopPromo.Local(o.EndsAt) } }).ToArray() } };
        }
        static string Mark(string k, DateTime from) { return "tip:" + k + ":" + from.Year; }
        public static void Seen(SqlConnection c, Shop sh, string k)
        {
            var n = Of(k) == null ? null : Next(k, Today()); if (n == null) return;
            using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopLookMarks WHERE StoreId=@s AND Mark=@m) INSERT INTO dbo.ShopLookMarks(StoreId, Mark) VALUES(@s, @m)", "@s", sh.StoreId, "@m", Mark(k, n[0])))
                try { cmd.ExecuteNonQuery(); } catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }
        }
    }
}
