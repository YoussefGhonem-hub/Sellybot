// The weekly report on WhatsApp: every Saturday from 10:00 Cairo, from the store's number to the owner's own number
// (Stores.OwnerPhone). What the assistant did in the last 7 days, the growth features' results, and, from that week's
// WhatsApp chats, what customers asked about most and the questions the assistant had no answer for (one short Claude call,
// counted in the store's usage), with a nudge to add those answers to «داتا المتجر». Then the month's quota and the plan's end.
// Jobs runs SendDue; a store that slept through Saturday gets it at the next run. "ابعتلي تقرير دلوقتي" sends one at once.
// Plan gate "report" (open until the super admin picks a minimum) on top of the WhatsApp service.
// The morning summary (the user, 2026-11-24): the same report for the last 24 hours, every day from 9:00 Cairo (Stores.ReportDaily),
// shorter, with the orders nobody confirmed and who was told to call them (Followup.cs). With more than one WhatsApp number
// (WaLines.cs) both say which number they come from (the one the owner picked, Stores.ReportLine) and what each number did.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public static class WeeklyReport
    {
        public const string Feature = "report";
        const DayOfWeek Day = DayOfWeek.Saturday;
        const int Hour = 10;
        static readonly CultureInfo Ar = new CultureInfo("ar-EG");

        // ---------- when ----------
        // This week's slot (Saturday 10:00 Cairo) in UTC, or null when it hasn't come yet this week
        static DateTime? SlotUtc()
        {
            var zone = Zone();
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            int back = ((int)local.DayOfWeek - (int)Day + 7) % 7;
            var slot = local.Date.AddDays(-back).AddHours(Hour);
            if (slot > local) slot = slot.AddDays(-7);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(slot, DateTimeKind.Unspecified), zone);
        }

        public static async Task SendDue(SqlConnection c, CancellationToken ct)
        {
            var slot = SlotUtc();
            if (!slot.HasValue) return;
            var ids = new List<int>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 10 Id FROM dbo.Stores WHERE ReportOn=1 AND OwnerPhone IS NOT NULL AND Status='active'
                AND (ReportAt IS NULL OR ReportAt < @slot) ORDER BY Id", "@slot", slot.Value))
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((int)r[0]);
            foreach (var id in ids)
            {
                if (ct.IsCancellationRequested) return;
                var s = Db.StoreById(c, id);
                // marked first: a store whose report can't go out (WhatsApp unlinked...) isn't retried every few minutes all week
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ReportAt=SYSUTCDATETIME() WHERE Id=@s", "@s", id)) cmd.ExecuteNonQuery();
                if (s == null || !PlanGate.Allows(c, s, Feature)) continue;
                try { var text = await Build(c, s); await WaNotify.ToOwner(c, s, text, "sb-report-" + id + "-" + slot.Value.ToString("yyyyMMdd")); }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        // ---------- the morning summary: every day from 9:00 Cairo ----------
        const int DailyHour = 9;
        static DateTime DailySlotUtc()
        {
            var zone = Zone();
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            var slot = local.Date.AddHours(DailyHour);
            if (slot > local) slot = slot.AddDays(-1);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(slot, DateTimeKind.Unspecified), zone);
        }

        public static async Task SendDailyDue(SqlConnection c, CancellationToken ct)
        {
            var slot = DailySlotUtc();
            // a site asleep all morning sends it when it wakes, but not in the evening: yesterday's news by then
            if (DateTime.UtcNow - slot > TimeSpan.FromHours(6)) return;
            var ids = new List<int>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 10 Id FROM dbo.Stores WHERE ReportDaily=1 AND OwnerPhone IS NOT NULL AND Status='active'
                AND (ReportDayAt IS NULL OR ReportDayAt < @slot) ORDER BY Id", "@slot", slot))
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((int)r[0]);
            foreach (var id in ids)
            {
                if (ct.IsCancellationRequested) return;
                var s = Db.StoreById(c, id);
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ReportDayAt=SYSUTCDATETIME() WHERE Id=@s", "@s", id)) cmd.ExecuteNonQuery();
                if (s == null || !PlanGate.Allows(c, s, Feature)) continue;
                try { var text = await Build(c, s, true); await WaNotify.ToOwner(c, s, text, "sb-daily-" + id + "-" + slot.ToString("yyyyMMdd")); }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        // ---------- what ----------
        // daily: the morning summary (the last 24 hours, shorter); otherwise the week
        public static async Task<string> Build(SqlConnection c, StoreInfo s, bool daily = false)
        {
            var to = DateTime.UtcNow; var from = to.AddDays(daily ? -1 : -7);
            var zone = Zone();
            string Range() => TimeZoneInfo.ConvertTimeFromUtc(from, zone).ToString("d MMMM", Ar) + " لـ " + TimeZoneInfo.ConvertTimeFromUtc(to, zone).ToString("d MMMM", Ar);
            int answers = Scalar(c, "SELECT ISNULL(SUM(Messages),0) FROM dbo.UsageDaily WHERE StoreId=@s AND Day >= CAST(@f AS DATE)", s.Id, from);
            int waChats = Scalar(c, "SELECT COUNT(DISTINCT ChatId) FROM dbo.WaMessages WHERE StoreId=@s AND FromCustomer=1 AND CreatedAt >= @f", s.Id, from);
            var lines = WaLines.All(c, s);

            var sb = new StringBuilder();
            if (daily) sb.Append("☀️ *صباح الخير!* ملخص امبارح لـ *").Append(s.Name).Append("*\n").Append(TimeZoneInfo.ConvertTimeFromUtc(from, zone).ToString("dddd d MMMM", Ar)).Append("\n");
            else sb.Append("📊 *تقرير الأسبوع* لـ *").Append(s.Name).Append("*\n").Append(Range()).Append("\n");
            // more than one number: which one this comes from (the owner picks it in «أرقام الواتساب والفريق»)
            if (lines.Count > 1)
            {
                var sender = WaLines.ReportSender(c, s);
                int at = lines.FindIndex(l => l.Id == sender.Id);
                sb.Append("📤 جايلك من رقم: ").Append(sender.Label(Math.Max(0, at))).Append('\n');
            }
            sb.Append('\n');
            if (answers == 0 && waChats == 0)
                sb.Append(daily ? "😴 المساعد مارَدّش على حد امبارح.\n" : "😴 المساعد مارَدّش على حد الأسبوع ده. اتأكد إن الشات ظاهر على موقعك، وإن واتساب المتجر مربوط.\n");
            else
            {
                sb.Append("💬 المساعد رد على *").Append(N(answers)).Append("* رسالة (الموقع والواتساب)\n");
                if (waChats > 0) sb.Append("📱 *").Append(N(waChats)).Append("* زبون كلّمك على الواتساب\n");
            }
            // the features' results (only what this store uses)
            int chatOrders = Scalar(c, "SELECT COUNT(*) FROM dbo.ChatOrders WHERE StoreId=@s AND Status IN ('placed','awaiting_payment') AND CreatedAt >= @f", s.Id, from);
            if (chatOrders > 0) sb.Append("🛒 *").Append(N(chatOrders)).Append("* طلب اتعمل من الشات\n");
            // order confirmation on WhatsApp: confirmed / cancelled / no answer (Followup.cs), and what the reminder brought back
            var oc = Followup.Stats(c, s.Id, null, from);
            if (oc.Yes + oc.No + oc.Silent > 0)
            {
                sb.Append("📲 تأكيد الطلبات: *").Append(N(oc.Yes)).Append("* اتأكد · *").Append(N(oc.No)).Append("* الزبون لغاه");
                if (oc.Silent > 0) sb.Append(" · *").Append(N(oc.Silent)).Append("* مردّش");
                sb.Append('\n');
                if (oc.Saved > 0) sb.Append("   🔔 التذكير رجّع *").Append(N(oc.Saved)).Append("* أوردر اتأكد بعده\n");
            }
            if (daily) Silent(c, s, from, sb);
            int dn = Scalar(c, "SELECT COUNT(*) FROM dbo.DeliveryNotices WHERE StoreId=@s AND Status='sent' AND CreatedAt >= @f", s.Id, from);
            if (dn > 0) sb.Append("🚚 *").Append(N(dn)).Append("* رسالة يوم توصيل\n");
            int cartSent = Scalar(c, "SELECT COUNT(*) FROM dbo.CartReminders WHERE StoreId=@s AND SentAt >= @f", s.Id, from);
            int cartBack = Scalar(c, "SELECT COUNT(*) FROM dbo.CartReminders WHERE StoreId=@s AND Status='recovered' AND OrderedAt >= @f", s.Id, from);
            if (cartSent + cartBack > 0) sb.Append("🛒 السلة المتروكة: *").Append(N(cartSent)).Append("* تذكير · *").Append(N(cartBack)).Append("* رجعوا اشتروا\n");
            int rsSent = Scalar(c, "SELECT COUNT(*) FROM dbo.RestockWaits WHERE StoreId=@s AND Status='sent' AND SentAt >= @f", s.Id, from);
            int rsWait = Scalar(c, "SELECT COUNT(*) FROM dbo.RestockWaits WHERE StoreId=@s AND Status='waiting'", s.Id, from);
            if (rsSent + rsWait > 0) sb.Append("🔔 بلّغني لما يرجع: *").Append(N(rsSent)).Append("* اتبلّغوا · *").Append(N(rsWait)).Append("* لسه مستنيين\n");
            if (rsWait > 0)
            {
                var top = TopWaited(c, s.Id);
                if (top != null) sb.Append("   أكتر منتج مستنيينه: ").Append(top).Append('\n');
            }
            int hand = Scalar(c, "SELECT COUNT(*) FROM dbo.Handoffs WHERE StoreId=@s AND CreatedAt >= @f", s.Id, from);
            if (hand > 0)
            {
                sb.Append("🙋 *").Append(N(hand)).Append("* زبون طلب يكلّم حد من المتجر");
                // with a team: whom the alerts went to
                var who = new List<string>();
                using (var cmd = Db.Cmd(c, "SELECT ToName, COUNT(*) AS N FROM dbo.Handoffs WHERE StoreId=@s AND CreatedAt >= @f AND ToName IS NOT NULL GROUP BY ToName ORDER BY COUNT(*) DESC", "@s", s.Id, "@f", from))
                using (var r = cmd.ExecuteReader()) while (r.Read()) who.Add((string)r["ToName"] + " " + N((int)r["N"]));
                sb.Append(who.Count > 0 ? " (" + string.Join(" · ", who) + ")" : "").Append('\n');
            }

            // more than one number: what each did, and who answered most by hand
            if (lines.Count > 1) Numbers(c, s, lines, from, sb);

            // what customers asked about, and what the assistant couldn't answer (from the period's WhatsApp chats)
            var ins = await Insights(c, s, from);
            if (ins != null)
            {
                if (ins.Topics.Count > 0) sb.Append("\n🔥 *أكتر حاجات الزباين سألوا عنها:*\n").Append(string.Concat(ins.Topics.Select(t => "• " + t + "\n")));
                if (ins.Gaps.Count > 0)
                    sb.Append("\n❓ *أسئلة المساعد ماكانش عنده إجابتها:*\n").Append(string.Concat(ins.Gaps.Select(t => "• " + t + "\n")))
                      .Append("👈 ضيف إجاباتها في «داتا المتجر»، والمساعد هيرد عليها من دلوقتي.\n");
            }

            // the month and the plan (the morning summary only when something needs doing soon)
            var u = Db.Usage(c, s.Id, Db.Month);
            long chars = Scalar(c, "SELECT ISNULL(SUM(CAST(LEN(Content) AS BIGINT)),0) FROM dbo.KnowledgeItems WHERE StoreId=@s", s.Id, from);
            var uj = Billing.UsageJson(c, s, u, chars);
            double pct = Convert.ToDouble(uj["pct"]);
            bool planSoon = s.PlanEndsAt.HasValue && !string.IsNullOrEmpty(s.PlanName) && s.PlanEndsAt.Value < DateTime.UtcNow.AddDays(3);
            bool waSoon = s.WaUntil.HasValue && !s.IsAdmin && s.WaUntil.Value < DateTime.UtcNow.AddDays(3);
            if (!daily || pct >= 80) sb.Append("\n📈 رصيد الشهر: استهلكت *").Append(N(Convert.ToInt32(Math.Round(pct)))).Append("%*، وفاضل حوالي *").Append(N(Convert.ToInt32(uj["msgLeft"]))).Append("* رسالة\n");
            if (s.PlanEndsAt.HasValue && !string.IsNullOrEmpty(s.PlanName) && (!daily || planSoon))
                sb.Append(Billing.Expired(s) ? "⛔ باقتك خلصت يوم " : "📅 باقتك بتخلص يوم ").Append(TimeZoneInfo.ConvertTimeFromUtc(s.PlanEndsAt.Value, zone).ToString("d MMMM", Ar)).Append('\n');
            if (s.WaUntil.HasValue && !s.IsAdmin && (!daily || waSoon))
                sb.Append("📱 خدمة الواتساب بتتجدد يوم ").Append(TimeZoneInfo.ConvertTimeFromUtc(s.WaUntil.Value, zone).ToString("d MMMM", Ar)).Append('\n');
            sb.Append("\n— Sellybot 🤖");
            return sb.ToString();
        }

        // the morning summary: yesterday's orders nobody confirmed, and who was told to call each (up to 5)
        static void Silent(SqlConnection c, StoreInfo s, DateTime from, StringBuilder sb)
        {
            var rows = new List<string>();
            foreach (var t in new[] { "OrderConfirms", "EoConfirms" })
                using (var cmd = Db.Cmd(c, "SELECT TOP 5 OrderName, Customer, AlertTo FROM dbo." + t + " WHERE StoreId=@s AND Status='pending' AND AlertedAt >= @f ORDER BY Id DESC", "@s", s.Id, "@f", from))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        string to = r["AlertTo"] as string ?? ""; int dot = to.IndexOf(" · ", StringComparison.Ordinal); if (dot >= 0) to = to.Substring(0, dot);
                        rows.Add("• " + (r["OrderName"] as string ?? "") + (r["Customer"] is string && ((string)r["Customer"]).Trim().Length > 0 ? " · " + ((string)r["Customer"]).Trim() : "")
                            + (to == "انت" ? " (وصلك تنبيه)" : to.Length > 0 ? " (اتبلّغ " + to + " يكلّمه)" : ""));
                    }
            if (rows.Count == 0) return;
            sb.Append("📵 *أوردرات مردّوش على التأكيد:*\n").Append(string.Join("\n", rows.Take(5))).Append('\n');
        }

        // more than one WhatsApp number: per number, the customers who wrote and the replies typed on the phone; 🏆 = most by hand
        static void Numbers(SqlConnection c, StoreInfo s, List<WaLine> lines, DateTime from, StringBuilder sb)
        {
            var stats = WaLines.Stats(c, s, lines, from);
            var top = WaLines.Top(stats);
            sb.Append("\n📱 *أرقام الواتساب:*\n");
            for (int i = 0; i < lines.Count; i++)
            {
                var st = stats[lines[i].Id];
                sb.Append("• ").Append(lines[i].Label(i)).Append(": *").Append(N(st.Chats)).Append("* زبون");
                if (st.Staff > 0) sb.Append(" · *").Append(N(st.Staff)).Append("* رد من الموبايل");
                if (st.Alerts > 0) sb.Append(" · ").Append(N(st.Alerts)).Append(" تنبيه");
                if (top.HasValue && top.Value == lines[i].Id) sb.Append(" 🏆");
                sb.Append('\n');
            }
            if (top.HasValue) sb.Append("🏆 = أكتر رقم رد على الزباين بنفسه\n");
        }

        class Insight { public List<string> Topics = new List<string>(), Gaps = new List<string>(); }

        // A short Claude call over the week's WhatsApp chats (customer questions + the assistant's answers). null = nothing to say.
        static async Task<Insight> Insights(SqlConnection c, StoreInfo s, DateTime from)
        {
            var lines = new List<string>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 400 ChatId, FromCustomer, Body FROM dbo.WaMessages WHERE StoreId=@s AND CreatedAt >= @f
                AND (FromCustomer=1 OR MsgId IS NULL) ORDER BY ChatId, Id", "@s", s.Id, "@f", from))
            using (var r = cmd.ExecuteReader())
            {
                string chat = null;
                while (r.Read())
                {
                    string id = (string)r["ChatId"], body = ((string)r["Body"]).Replace("\n", " ");
                    if (id != chat) { lines.Add("---"); chat = id; }
                    lines.Add(((bool)r["FromCustomer"] ? "زبون: " : "المساعد: ") + (body.Length > 220 ? body.Substring(0, 220) + "…" : body));
                }
            }
            if (lines.Count(l => l.StartsWith("زبون: ")) < 3) return null;
            var text = string.Join("\n", lines);
            if (text.Length > 14000) text = text.Substring(text.Length - 14000);
            try
            {
                var p = new MessageCreateParams
                {
                    Model = Claude.Model, MaxTokens = 700,
                    System = new List<TextBlockParam> { new TextBlockParam { Text =
                        "إنت بتساعد صاحب متجر يفهم زباينه. قدامك محادثات الأسبوع على الواتساب بين الزباين والمساعد الآلي بتاع المتجر.\n" +
                        "رجّع JSON بس، من غير أي كلام قبله أو بعده، بالشكل ده: {\"topics\":[\"...\"],\"gaps\":[\"...\"]}\n" +
                        "- topics: أكتر 3 لـ 5 مواضيع الزباين سألوا عنها، كل واحد في 3 لـ 7 كلمات بالعامية المصرية (مثلًا: مقاسات الجاكيت الجلد).\n" +
                        "- gaps: أسئلة حقيقية الزباين سألوها والمساعد قال إنه مش عارف أو مفيش معلومة أو حوّلهم للمتجر، بحد أقصى 5، كل واحد سؤال قصير. لو مفيش، رجّع [].\n" +
                        "ماتكتبش أسماء ولا أرقام موبايلات ولا أي بيانات شخصية." } },
                    Messages = new List<MessageParam> { new UserMessageParam { Content = text } },
                };
                Message resp = await Claude.Client().Messages.Create(p);
                Db.AddUsage(c, s.Id, 0, resp.Usage.InputTokens, resp.Usage.CacheReadInputTokens ?? 0, resp.Usage.CacheCreationInputTokens ?? 0, resp.Usage.OutputTokens);
                var sb = new StringBuilder();
                foreach (ContentBlock block in resp.Content) if (block.TryPickText(out TextBlock t)) sb.Append(t.Text);
                var raw = sb.ToString();
                int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
                if (a < 0 || b <= a) return null;
                using (var doc = JsonDocument.Parse(raw.Substring(a, b - a + 1)))
                {
                    var ins = new Insight();
                    JsonElement arr;
                    if (doc.RootElement.TryGetProperty("topics", out arr) && arr.ValueKind == JsonValueKind.Array)
                        ins.Topics = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clean(x.GetString())).Where(x => x.Length > 0).Take(5).ToList();
                    if (doc.RootElement.TryGetProperty("gaps", out arr) && arr.ValueKind == JsonValueKind.Array)
                        ins.Gaps = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clean(x.GetString())).Where(x => x.Length > 0).Take(5).ToList();
                    return ins.Topics.Count + ins.Gaps.Count > 0 ? ins : null;
                }
            }
            catch (Exception ex) { Log.Error(ex); return null; }
        }

        static string Clean(string s) { s = (s ?? "").Replace("*", "").Replace("\n", " ").Trim(); return s.Length > 120 ? s.Substring(0, 120) : s; }

        static string TopWaited(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, @"SELECT TOP 1 Product, Variant, COUNT(*) AS N FROM dbo.RestockWaits WHERE StoreId=@s AND Status='waiting'
                GROUP BY Product, Variant ORDER BY COUNT(*) DESC", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                return r.Read() ? (string)r["Product"] + (r["Variant"] is string ? " (" + r["Variant"] + ")" : "") + " · " + N((int)r["N"]) + " زبون" : null;
        }

        static int Scalar(SqlConnection c, string sql, int storeId, DateTime from)
        {
            using (var cmd = Db.Cmd(c, sql, "@s", storeId, "@f", from)) return Convert.ToInt32(cmd.ExecuteScalar());
        }

        static string N(int n) { return n.ToString("#,0", CultureInfo.InvariantCulture); }

        static TimeZoneInfo zone;
        static TimeZoneInfo Zone()
        {
            if (zone != null) return zone;
            foreach (var id in new[] { "Egypt Standard Time", "Africa/Cairo" })
                try { return zone = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            return zone = TimeZoneInfo.CreateCustomTimeZone("Cairo", TimeSpan.FromHours(2), "Cairo", "Cairo");
        }
    }
}
