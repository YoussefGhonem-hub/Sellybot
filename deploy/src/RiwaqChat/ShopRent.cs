// The store's rent (its own monthly subscription, like WhatsApp's), the review before a store opens, and the background work.
// The rent: asked for from «باقتي» (PlanRequests.Kind = 'shop', the same request and sum as everything else), approved by the admin, 30
// days from then or after the end of the month still running. A reminder two days before the end, three days' grace with the store open,
// then customers see «المتجر مقفول مؤقتًا» while the owner keeps everything. After 83 days the owner is told to download a copy; nothing
// is ever wiped by itself: the admin sees the stores that passed 90 days and decides.
// The free days: a store that hasn't paid can still be sent for review; the admin's first approval opens it free for Shops.FreeDays (2)
// days (Shops.FreeUntil, once per store). A reminder a day before they end; then it closes to customers at once (no grace) until a paid
// month is approved, and that month starts after the free days, so paying early loses nothing.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public static class ShopRent
    {
        // 0 = no price yet: stores can't ask for it
        public static decimal Price(SqlConnection c) { return Math.Max(0, Billing.Dec(Billing.Get(c, "ShopPrice"), 0)); }

        public static Dictionary<string, object> Json(SqlConnection c, StoreInfo s, Shop sh)
        {
            var until = Shops.Until(c, s, sh); bool on = Shops.HasService(c, s, sh);
            return new Dictionary<string, object> { { "on", on }, { "grace", Shops.InGrace(c, s, sh) }, { "inPlan", Shops.InPlan(c, s) }, { "until", until.HasValue ? until.Value.ToString("o") : "" }, { "days", until.HasValue ? (int)Math.Ceiling((until.Value - DateTime.UtcNow).TotalDays) : 0 },
                { "price", Price(c) }, { "pending", Pending(c, s.Id) }, { "graceDays", Shops.GraceDays }, { "keepDays", Shops.KeepDays }, { "admin", s.IsAdmin },
                // free = when the free days end ("" = not used yet) · inFree = they're running now · hoursLeft = until they end
                { "freeDays", Shops.FreeDays }, { "free", sh != null && sh.FreeUntil.HasValue ? sh.FreeUntil.Value.ToString("o") : "" }, { "inFree", Shops.InFree(sh) },
                { "hoursLeft", Shops.InFree(sh) ? (int)Math.Ceiling((sh.FreeUntil.Value - DateTime.UtcNow).TotalHours) : 0 } };
        }
        // a rent request the admin hasn't answered yet
        static bool Pending(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.PlanRequests WHERE StoreId=@s AND Kind='shop' AND Status='pending'", "@s", storeId)) return (int)cmd.ExecuteScalar() > 0;
        }

        // where a new paid month (or the admin's days) starts: after the rent still running, or after the free days still running
        static DateTime Start(Shop sh)
        {
            var now = DateTime.UtcNow; var start = sh.RentUntil.HasValue && sh.RentUntil.Value > now ? sh.RentUntil.Value : now;
            if (sh.FreeUntil.HasValue && sh.FreeUntil.Value > start) start = sh.FreeUntil.Value;
            return start;
        }

        // The admin's first approval of a store that hasn't paid: the free days start now. Returns their end, or null when the store can't
        // open (they were used before and no rent runs). A store that already paid gets the same days added to its rent.
        public static DateTime? OpenFree(SqlConnection c, StoreInfo s, Shop sh)
        {
            if (Shops.InFree(sh)) return sh.FreeUntil;
            if (sh.FreeUntil.HasValue) return Shops.HasService(c, s, sh) ? (DateTime?)DateTime.UtcNow : null;
            var end = DateTime.UtcNow.AddDays(Shops.FreeDays);
            if (Shops.HasService(c, s, sh))
            {
                // paid before opening: the free days go on the end of the paid month (not for a plan that includes the store, or Sellybot's own)
                if (s.IsAdmin || Shops.InPlan(c, s) || !sh.RentUntil.HasValue) return DateTime.UtcNow;
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET FreeUntil=SYSUTCDATETIME(), RentUntil=DATEADD(DAY, @d, RentUntil) WHERE StoreId=@s AND FreeUntil IS NULL", "@d", Shops.FreeDays, "@s", s.Id)) cmd.ExecuteNonQuery();
                return DateTime.UtcNow;
            }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET FreeUntil=@e WHERE StoreId=@s AND FreeUntil IS NULL", "@e", end, "@s", s.Id)) if (cmd.ExecuteNonQuery() == 0) return null;
            return end;
        }

        // A paid month (or more): its own payment row (Subscriptions.Kind 'shop') and the store's new end. Returns the new end.
        public static DateTime Add(SqlConnection c, StoreInfo s, int months, decimal price, decimal paid, string method, string notes, out int subscriptionId)
        {
            var sh = Shops.Create(c, s); var start = Start(sh); var end = start.AddDays(Shops.RentDays * Math.Max(1, months));
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Subscriptions(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, PaidEgp, PaymentMethod, Notes, StartsAt, EndsAt, Kind) OUTPUT INSERTED.Id
                    VALUES (@s, @p, @n, @m, @price, @disc, @paid, @method, @notes, @start, @end, 'shop')", "@s", s.Id, "@p", s.PlanId.HasValue ? (object)s.PlanId.Value : DBNull.Value, "@n", Shops.ServiceName, "@m", Math.Max(1, months), "@price", price, "@disc", Math.Max(0, price - paid), "@paid", paid, "@method", (object)method ?? DBNull.Value, "@notes", (object)notes ?? DBNull.Value, "@start", start, "@end", end))
                { cmd.Transaction = tx; subscriptionId = (int)cmd.ExecuteScalar(); }
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET RentUntil=@e, RemindAt=NULL, LapseAt=NULL, WipeWarnAt=NULL WHERE StoreId=@s", "@e", end, "@s", s.Id)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            return end;
        }
        // days the admin gives for free (the trial's merchants), with no payment
        public static DateTime Grant(SqlConnection c, StoreInfo s, int days)
        {
            var sh = Shops.Create(c, s); var now = DateTime.UtcNow; var start = days > 0 ? Start(sh) : sh.RentUntil.HasValue && sh.RentUntil.Value > now ? sh.RentUntil.Value : now; var end = start.AddDays(Math.Max(-400, Math.Min(400, days)));
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET RentUntil=@e, RemindAt=NULL, LapseAt=NULL, WipeWarnAt=NULL WHERE StoreId=@s", "@e", end, "@s", s.Id)) cmd.ExecuteNonQuery();
            return end;
        }

        // ---------- every few minutes (Jobs.cs, lane "due") ----------
        public static async Task Run(SqlConnection c, CancellationToken ct)
        {
            if (!Shops.Ready) return;
            using (var cmd = Db.Cmd(c, "DELETE FROM dbo.ShopPreviews WHERE ExpiresAt < DATEADD(DAY, -1, SYSUTCDATETIME()); DELETE FROM dbo.ShopFails WHERE At < DATEADD(DAY, -30, SYSUTCDATETIME())")) cmd.ExecuteNonQuery();
            await ShopImages.Tidy(c, ct);
            if (WaNotify.QuietUntil() != null) return;
            var now = DateTime.UtcNow;
            foreach (var sh in Shops.All(c).Where(x => x.Status == "live" || x.Status == "review"))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var s = Db.StoreById(c, sh.StoreId); if (s == null || s.IsAdmin) continue;
                    var until = Shops.Until(c, s, sh); string text = null, mark = null;
                    if (sh.FreeUntil.HasValue && !(until.HasValue && until.Value > sh.FreeUntil.Value))
                    {
                        // the free days are the last thing this store had: no grace after them
                        double free = (sh.FreeUntil.Value - now).TotalDays; var price = Price(c);
                        if (free > 0 && free <= 1) { mark = "RemindAt"; text = "⏰ *متجرك «" + sh.Name + "» مفتوح ببلاش لحد " + When(sh.FreeUntil.Value) + "*\nبعدها هيتقفل على الزباين لحد ما تدفع الإيجار" + (price > 0 ? " (" + price.ToString("0.##") + " ج في الشهر)" : "") + ". ادفع من «متجري» ← «إيجار المتجر»، والشهر بيبدأ بعد الأيام المجانية فمش هتخسر ولا ساعة.\n" + sh.Url; }
                        else if (free <= 0 && free > -2) { mark = "LapseAt"; text = "🔒 *الأيام المجانية لمتجرك «" + sh.Name + "» خلصت*\nالمتجر اتقفل على الزباين دلوقتي، ومنتجاتك وأوردراتك محفوظة. ادفع الإيجار من «متجري» ← «إيجار المتجر»، ويرجع يفتح أول ما نتأكد من التحويل."; }
                        else if (free < -(Shops.KeepDays - 7)) { mark = "WipeWarnAt"; text = "📦 *متجرك «" + sh.Name + "» مقفول من فترة*\nنزّل نسخة من منتجاتك وأوردراتك من «متجري» خلال أسبوع، أو ادفع الإيجار عشان يرجع زي ما كان."; }
                        if (mark == null) continue;
                        if (mark == "RemindAt" && Pending(c, s.Id)) continue;   // already paid, waiting for the admin
                        until = sh.FreeUntil;
                    }
                    else
                    {
                        if (!until.HasValue) continue;
                        double left = (until.Value - now).TotalDays;
                        if (left > 0 && left <= 2) { mark = "RemindAt"; text = "⏰ *إيجار متجرك «" + sh.Name + "» بيخلص يوم " + ShopPromo.Local(until).Substring(0, 10) + "*\nجدّده من «باقتي» عشان المتجر يفضل مفتوح لزباينك.\n" + sh.Url; }
                        else if (left <= 0 && left > -Shops.GraceDays) { mark = "LapseAt"; text = "⚠️ *إيجار متجرك «" + sh.Name + "» خلص*\nالمتجر هيفضل مفتوح " + Shops.GraceDays + " أيام سماح، وبعدها الزباين هتشوف «المتجر مقفول مؤقتًا». منتجاتك وأوردراتك محفوظة. جدّد من «باقتي»."; }
                        else if (left < -(Shops.KeepDays - 7)) { mark = "WipeWarnAt"; text = "📦 *متجرك «" + sh.Name + "» مقفول من فترة*\nنزّل نسخة من منتجاتك وأوردراتك من «متجري» خلال أسبوع، أو جدّد الإيجار عشان يرجع زي ما كان."; }
                    }
                    if (mark == null) continue;
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET " + mark + "=SYSUTCDATETIME() WHERE StoreId=@s AND (" + mark + " IS NULL OR " + mark + " < DATEADD(DAY, -20, SYSUTCDATETIME()))", "@s", s.Id)) if (cmd.ExecuteNonQuery() == 0) continue;
                    await WaNotify.ToOwner(c, s, text, "sb-shop-" + mark + "-" + s.Id + "-" + until.Value.ToString("yyyyMMdd"));
                }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        // «الخميس 28/11 الساعة 3:30 م» in Cairo's time
        static readonly string[] Days = { "الحد", "الاتنين", "التلات", "الأربع", "الخميس", "الجمعة", "السبت" };
        public static string When(DateTime utc)
        {
            var t = DateTime.ParseExact(ShopPromo.Local(utc), "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
            int h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
            return Days[(int)t.DayOfWeek] + " " + t.Day + "/" + t.Month + " الساعة " + h + ":" + t.Minute.ToString("00") + (t.Hour < 12 ? " ص" : " م");
        }
    }

    // What the admin looks at before a store opens: names and products that look like fraud or aren't allowed.
    public static class ShopReview
    {
        // words that don't belong to a small shop's name (a bank, a government body, a payment company, a known brand)
        static readonly string[] NameWords = { "بنك", "مصرف", "البريد المصري", "فوري", "انستاباي", "إنستاباي", "فودافون", "اورنج", "أورنج", "اتصالات", "وزارة", "الداخلية", "الضرائب", "الجوازات", "سفارة", "فيزا", "ماستركارد", "باي بال", "فيسبوك", "انستجرام", "واتساب", "تيك توك", "جوجل", "امازون", "أمازون", "نون", "جوميا", "نايك", "اديداس", "أديداس", "زارا", "جوتشي", "شانيل", "ديور", "رولكس", "ابل ستور", "سامسونج", "هواوي", "بوسطة", "ارامكس", "أرامكس" };
        // products that aren't allowed, or that the admin should look at: medicine, tobacco, weapons, copies, promises to cure, money
        static readonly string[][] Flags = {
            new[] { "دواء أو علاج", "دواء|ادوية|أدوية|برشام|حبوب تخسيس|كبسولات|منشط|فياجرا|سياليس|ترامادول|مورفين|انسولين|مضاد حيوي|يعالج|يشفي|علاج نهائي|بيعالج|للسكر والضغط" },
            new[] { "سجاير أو مخدرات", "سجاير|سجائر|شيشة|فيب|vape|تبغ|معسل|حشيش|بانجو|خمر|بيرة|ويسكي|فودكا" },
            new[] { "سلاح", "سلاح|مسدس|خرطوش|طبنجة|رشاش|صاعق|كهربا دفاع|مطوة|ذخيرة" },
            new[] { "تقليد ماركات", "هاي كوبي|هاى كوبى|high copy|كوبي اوريجينال|ميرور|mirror|first copy|فرست كوبي|درجة اولى من الاصل|درجة أولى من الأصل|replica|تقليد" },
            new[] { "فلوس أو استثمار", "استثمار مضمون|ربح مضمون|ارباح يومية|أرباح يومية|قرض|تمويل فوري|عملات رقمية|بيتكوين|bitcoin|كارت فيزا|حسابات بنكية|شحن رصيد مجاني|اربح ايفون|اربح آيفون" },
            new[] { "حيوانات أو أعضاء أو آثار", "اثار|آثار|عملة اثرية|سلحفاة|قرد|نسر|صقر حر" },
            new[] { "كلام جنسي", "جنسي|اباحي|إباحي|تأخير القذف|تكبير" } };

        static string Fold(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), "[ً-ْـ]", "").Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي'); }
        public static string BadName(string name, bool admin)
        {
            if (admin) return null; var f = " " + Fold(name) + " ";
            foreach (var w in NameWords) if (f.Contains(" " + Fold(w) + " ") || f.Contains(" ال" + Fold(w) + " ")) return "اسم المتجر فيه «" + w + "»، وده اسم بنك أو ماركة أو جهة معروفة. اختار اسم متجرك انت.";
            return null;
        }
        public static List<string> Check(Shop sh, ShopData d)
        {
            var l = new List<string>();
            foreach (var p in d.Products)
            {
                var t = Fold(p.Name + " " + p.NameEn + " " + p.Descr + " " + p.Tags + " " + p.Brand);
                foreach (var f in Flags) { var m = Regex.Match(t, Fold(f[1])); if (m.Success) { l.Add(p.Name + ": " + f[0] + " («" + m.Value + "»)"); break; } }
                if (l.Count >= 30) break;
            }
            var bad = BadName(sh.Name, false); if (bad != null) l.Insert(0, bad);
            return l;
        }
    }
}
