// The weekly report on WhatsApp: every Saturday from 10:00 Cairo, from the store's number to the owner's own number
// (Stores.OwnerPhone). What the assistant did in the last 7 days, the growth features' results, and, from that week's
// WhatsApp chats, what customers asked about most and the questions the assistant had no answer for (one short Claude call,
// counted in the store's usage), with a nudge to add those answers to «داتا المتجر». Then the month's quota and the plan's end.
// Jobs runs SendDue; a store that slept through Saturday gets it at the next run. "ابعتلي تقرير دلوقتي" sends one at once.
// Plan gate "report" (open until the super admin picks a minimum) on top of the WhatsApp service.
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

        // ---------- what ----------
        public static async Task<string> Build(SqlConnection c, StoreInfo s)
        {
            var to = DateTime.UtcNow; var from = to.AddDays(-7);
            var zone = Zone();
            string Range() => TimeZoneInfo.ConvertTimeFromUtc(from, zone).ToString("d MMMM", Ar) + " لـ " + TimeZoneInfo.ConvertTimeFromUtc(to, zone).ToString("d MMMM", Ar);
            int answers = Scalar(c, "SELECT ISNULL(SUM(Messages),0) FROM dbo.UsageDaily WHERE StoreId=@s AND Day >= CAST(@f AS DATE)", s.Id, from);
            int waChats = Scalar(c, "SELECT COUNT(DISTINCT ChatId) FROM dbo.WaMessages WHERE StoreId=@s AND FromCustomer=1 AND CreatedAt >= @f", s.Id, from);

            var sb = new StringBuilder();
            sb.Append("📊 *تقرير الأسبوع* لـ *").Append(s.Name).Append("*\n").Append(Range()).Append("\n\n");
            if (answers == 0 && waChats == 0)
                sb.Append("😴 المساعد مارَدّش على حد الأسبوع ده. اتأكد إن الشات ظاهر على موقعك، وإن واتساب المتجر مربوط.\n");
            else
            {
                sb.Append("💬 المساعد رد على *").Append(N(answers)).Append("* رسالة (الموقع والواتساب)\n");
                if (waChats > 0) sb.Append("📱 *").Append(N(waChats)).Append("* زبون كلّمك على الواتساب\n");
            }
            // the features' results (only what this store uses)
            int chatOrders = Scalar(c, "SELECT COUNT(*) FROM dbo.ChatOrders WHERE StoreId=@s AND Status IN ('placed','awaiting_payment') AND CreatedAt >= @f", s.Id, from);
            if (chatOrders > 0) sb.Append("🛒 *").Append(N(chatOrders)).Append("* طلب اتعمل من الشات\n");
            int ocYes = Scalar(c, "SELECT (SELECT COUNT(*) FROM dbo.OrderConfirms WHERE StoreId=@s AND Status='confirmed' AND CreatedAt >= @f) + (SELECT COUNT(*) FROM dbo.EoConfirms WHERE StoreId=@s AND Status='confirmed' AND CreatedAt >= @f)", s.Id, from);
            int ocNo = Scalar(c, "SELECT (SELECT COUNT(*) FROM dbo.OrderConfirms WHERE StoreId=@s AND Status='cancelled' AND CreatedAt >= @f) + (SELECT COUNT(*) FROM dbo.EoConfirms WHERE StoreId=@s AND Status='cancelled' AND CreatedAt >= @f)", s.Id, from);
            if (ocYes + ocNo > 0) sb.Append("📲 تأكيد الطلبات: *").Append(N(ocYes)).Append("* اتأكد · *").Append(N(ocNo)).Append("* الزبون لغاه\n");
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
            if (hand > 0) sb.Append("🙋 *").Append(N(hand)).Append("* زبون طلب يكلّمك بنفسك\n");

            // what customers asked about, and what the assistant couldn't answer (from the week's WhatsApp chats)
            var ins = await Insights(c, s, from);
            if (ins != null)
            {
                if (ins.Topics.Count > 0) sb.Append("\n🔥 *أكتر حاجات الزباين سألوا عنها:*\n").Append(string.Concat(ins.Topics.Select(t => "• " + t + "\n")));
                if (ins.Gaps.Count > 0)
                    sb.Append("\n❓ *أسئلة المساعد ماكانش عنده إجابتها:*\n").Append(string.Concat(ins.Gaps.Select(t => "• " + t + "\n")))
                      .Append("👈 ضيف إجاباتها في «داتا المتجر»، والمساعد هيرد عليها من دلوقتي.\n");
            }

            // the month and the plan
            var u = Db.Usage(c, s.Id, Db.Month);
            long chars = Scalar(c, "SELECT ISNULL(SUM(CAST(LEN(Content) AS BIGINT)),0) FROM dbo.KnowledgeItems WHERE StoreId=@s", s.Id, from);
            var uj = Billing.UsageJson(c, s, u, chars);
            sb.Append("\n📈 رصيد الشهر: استهلكت *").Append(N(Convert.ToInt32(Math.Round(Convert.ToDouble(uj["pct"]))))).Append("%*، وفاضل حوالي *").Append(N(Convert.ToInt32(uj["msgLeft"]))).Append("* رسالة\n");
            if (s.PlanEndsAt.HasValue && !string.IsNullOrEmpty(s.PlanName))
                sb.Append(Billing.Expired(s) ? "⛔ باقتك خلصت يوم " : "📅 باقتك بتخلص يوم ").Append(TimeZoneInfo.ConvertTimeFromUtc(s.PlanEndsAt.Value, zone).ToString("d MMMM", Ar)).Append('\n');
            if (s.WaUntil.HasValue && !s.IsAdmin)
                sb.Append("📱 خدمة الواتساب بتتجدد يوم ").Append(TimeZoneInfo.ConvertTimeFromUtc(s.WaUntil.Value, zone).ToString("d MMMM", Ar)).Append('\n');
            sb.Append("\n— Sellybot 🤖");
            return sb.ToString();
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
