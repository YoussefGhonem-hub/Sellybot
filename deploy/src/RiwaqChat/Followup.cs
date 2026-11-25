// Following up an order confirmation nobody answered (the user, 2026-11-24). The confirmation message (OrderConfirm.cs for
// Shopify, EoConfirm in EasyOrders.cs) went once and that was it. Now, when the customer hasn't answered after Stores.OcFollow
// hours (1 / 3 / 6, default 3, 0 = off):
//   1. one reminder goes from the store's number, with the order and "1 / 2" again;
//   2. still nothing as long again after it: the order shows «📵 مردّش», and someone has to call the customer. The alert goes
//      to the team in turn (Team.Send: the employees' numbers, WaLines.cs), or to the owner's own number when there's no team.
// A late "1"/"2" still confirms or cancels (the order stays pending). Never at night (WaNotify.QuietUntil), never while the
// customer is in the middle of a chat with the store (wrote in the last 30 minutes), and only for orders from the last day or
// so (orders from before this existed get nothing). The rate (confirmed / cancelled / no answer over 30 days, and how many the
// reminder brought back) is shown with each confirmation list and in the reports.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public static class Followup
    {
        public static readonly int[] Hours = { 1, 3, 6 };
        const int Window = 14;        // hours past due after which an order is too old for this step
        const int BusyMinutes = 30;   // the customer wrote this recently: they're talking to the store, don't interrupt

        static readonly string[][] Sources = { new[] { "OrderConfirms", "OcOn", "Shopify" }, new[] { "EoConfirms", "EoOcOn", "EasyOrders" } };

        class Due { public string Table, Source; public long Id; public int StoreId; public string Phone, Name, Customer, Total; public bool Remind; }

        public static async Task SendDue(SqlConnection c, CancellationToken ct)
        {
            if (WaNotify.QuietUntil() != null) return;
            var due = new List<Due>();
            foreach (var src in Sources)
                using (var cmd = Db.Cmd(c, @"SELECT TOP 30 x.Id, x.StoreId, x.Phone, x.OrderName, x.Customer, x.Total, CASE WHEN x.RemindedAt IS NULL THEN 1 ELSE 0 END AS Remind
                    FROM dbo." + src[0] + @" x JOIN dbo.Stores s ON s.Id = x.StoreId
                    WHERE x.Status='pending' AND x.Phone IS NOT NULL AND x.AlertedAt IS NULL AND s.OcFollow > 0 AND s.Status='active' AND s." + src[1] + @" = 1
                      AND ((x.RemindedAt IS NULL AND x.CreatedAt <= DATEADD(HOUR, -s.OcFollow, SYSUTCDATETIME()) AND x.CreatedAt > DATEADD(HOUR, -(s.OcFollow + @w), SYSUTCDATETIME()))
                        OR (x.RemindedAt IS NOT NULL AND x.RemindedAt <= DATEADD(HOUR, -s.OcFollow, SYSUTCDATETIME()) AND x.RemindedAt > DATEADD(HOUR, -(s.OcFollow + @w), SYSUTCDATETIME())))
                    ORDER BY x.Id", "@w", Window))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        due.Add(new Due { Table = src[0], Source = src[2], Id = Convert.ToInt64(r["Id"]), StoreId = (int)r["StoreId"], Phone = r["Phone"] as string,
                            Name = r["OrderName"] as string ?? "", Customer = r["Customer"] as string ?? "", Total = r["Total"] as string ?? "", Remind = Convert.ToInt32(r["Remind"]) == 1 });

            var stores = new Dictionary<int, StoreInfo>();
            foreach (var d in due)
            {
                if (ct.IsCancellationRequested) return;
                StoreInfo s;
                if (!stores.TryGetValue(d.StoreId, out s)) stores[d.StoreId] = s = Db.StoreById(c, d.StoreId);
                if (s == null || WaNotify.CantSend(c, s) != null) continue;   // tried again next run, until it's too old
                try
                {
                    if (d.Remind) await Remind(c, s, d);
                    else await Alert(c, s, d);
                }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        static bool Busy(SqlConnection c, int storeId, string phone)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaMessages WHERE StoreId=@s AND ChatId=@c AND FromCustomer=1 AND CreatedAt > DATEADD(MINUTE,-@m,SYSUTCDATETIME())",
                "@s", storeId, "@c", phone, "@m", BusyMinutes)) return (int)cmd.ExecuteScalar() > 0;
        }

        // only one run sends it, even when two run at once
        static bool Claim(SqlConnection c, Due d, string column)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo." + d.Table + " SET " + column + "=SYSUTCDATETIME() WHERE Id=@id AND Status='pending' AND " + column + " IS NULL", "@id", d.Id))
                return cmd.ExecuteNonQuery() == 1;
        }

        static void Note(SqlConnection c, Due d, string note)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo." + d.Table + " SET Note=@n WHERE Id=@id", "@n", note.Length > 300 ? note.Substring(0, 300) : note, "@id", d.Id)) cmd.ExecuteNonQuery();
        }

        static async Task Remind(SqlConnection c, StoreInfo s, Due d)
        {
            if (Busy(c, s.Id, d.Phone)) return;
            if (!Claim(c, d, "RemindedAt")) return;
            var first = (d.Customer ?? "").Trim().Split(' ')[0];
            var text = new StringBuilder("أهلًا").Append(first.Length > 0 ? " " + first : "").Append(" 👋\n")
                .Append("طلبك *").Append(d.Name).Append("* من *").Append(s.Name).Append("* لسه مستني تأكيدك عشان نبدأ نجهّزه.")
                .Append(string.IsNullOrEmpty(d.Total) ? "" : " (الإجمالي: " + d.Total + ")").Append("\n\n")
                .Append("اكتب *1* لتأكيد الطلب ✅\n").Append("اكتب *2* لإلغاء الطلب ❌").ToString();
            var why = await WaNotify.ToCustomer(c, s, d.Phone, text, "sb-ocr-" + d.Table + "-" + d.Id);
            // a reminder that couldn't go out: the next step (someone calls the customer) still comes on time
            if (why != null) Note(c, d, "التذكير ماتبعتش: " + why);
        }

        static async Task Alert(SqlConnection c, StoreInfo s, Due d)
        {
            if (Busy(c, s.Id, d.Phone)) return;
            if (!Claim(c, d, "AlertedAt")) return;
            var risk = Crm.RiskLine(c, s, d.Phone);   // «زبايني»: they return orders
            var text = new StringBuilder("📵 *أوردر مردّش على التأكيد*\n")
                .Append("🧾 طلب ").Append(d.Name).Append(string.IsNullOrWhiteSpace(d.Customer) ? "" : " · " + d.Customer.Trim())
                .Append(string.IsNullOrEmpty(d.Total) ? "" : " · " + d.Total).Append(" (").Append(d.Source).Append(")\n")
                .Append("بعتناله رسالة التأكيد وتذكير بعدها، ولسه مارَدّش.\n").Append(risk.Length > 0 ? "↩️ وخد بالك: " + risk + ".\n" : "").Append('\n')
                .Append("📱 رقمه: +").Append(d.Phone).Append('\n')
                .Append("👈 كلّمه واتساب: https://wa.me/").Append(d.Phone).Append('\n')
                .Append("📞 أو اتصل بيه وأكّد معاه الطلب.\n\n")
                .Append("💡 لو رد على الواتساب بـ 1 أو 2 بعد كده، بيتأكد أو يتلغي لوحده.").ToString();
            var sent = await Team.Send(c, s, text, "sb-ocn-" + d.Table + "-" + d.Id);
            string to = (sent.Name ?? (string.IsNullOrEmpty(sent.Phone) ? "" : "انت")) + (string.IsNullOrEmpty(sent.Phone) ? "" : " · " + sent.Phone);
            using (var cmd = Db.Cmd(c, "UPDATE dbo." + d.Table + " SET AlertTo=@t WHERE Id=@id", "@t", to.Length > 80 ? to.Substring(0, 80) : to, "@id", d.Id)) cmd.ExecuteNonQuery();
            if (sent.Error != null) Note(c, d, "تنبيه «مردّش» ماوصلش: " + sent.Error);
        }

        // ---------- the rate ----------
        public class Rate { public int Yes, No, Silent, Waiting, Saved; public int Sent { get { return Yes + No + Silent + Waiting; } } }

        // over `days`, the orders a confirmation went to: confirmed, cancelled, no answer (told to call, or a day with nothing),
        // still waiting, and how many confirmed after the reminder. table null = Shopify and EasyOrders together
        public static Rate Stats(SqlConnection c, int storeId, string table, DateTime from)
        {
            var rate = new Rate();
            foreach (var t in table == null ? new[] { "OrderConfirms", "EoConfirms" } : new[] { table })
                using (var cmd = Db.Cmd(c, @"SELECT ISNULL(SUM(CASE WHEN Status='confirmed' THEN 1 ELSE 0 END),0) AS Yes,
                        ISNULL(SUM(CASE WHEN Status='cancelled' THEN 1 ELSE 0 END),0) AS No,
                        ISNULL(SUM(CASE WHEN Status='pending' AND (AlertedAt IS NOT NULL OR CreatedAt < DATEADD(HOUR,-24,SYSUTCDATETIME())) THEN 1 ELSE 0 END),0) AS Silent,
                        ISNULL(SUM(CASE WHEN Status='pending' AND AlertedAt IS NULL AND CreatedAt >= DATEADD(HOUR,-24,SYSUTCDATETIME()) THEN 1 ELSE 0 END),0) AS Waiting,
                        ISNULL(SUM(CASE WHEN Status='confirmed' AND RemindedAt IS NOT NULL AND AnsweredAt > RemindedAt THEN 1 ELSE 0 END),0) AS Saved
                    FROM dbo." + t + " WHERE StoreId=@s AND CreatedAt >= @f AND Status IN ('pending','confirmed','cancelled')", "@s", storeId, "@f", from))
                using (var r = cmd.ExecuteReader())
                    if (r.Read())
                    {
                        rate.Yes += Convert.ToInt32(r["Yes"]); rate.No += Convert.ToInt32(r["No"]); rate.Silent += Convert.ToInt32(r["Silent"]);
                        rate.Waiting += Convert.ToInt32(r["Waiting"]); rate.Saved += Convert.ToInt32(r["Saved"]);
                    }
            return rate;
        }

        // for a confirmation list in the dashboard: the follow-up setting and the last 30 days
        public static Dictionary<string, object> Json(SqlConnection c, StoreInfo s, string table)
        {
            var r = Stats(c, s.Id, table, DateTime.UtcNow.AddDays(-30));
            return new Dictionary<string, object> {
                { "hours", s.OcFollow }, { "choices", Hours },
                // who's told to call: the team in turn (WaLines.cs), else the owner when their number is set
                { "team", Team.Members(c, s).Select(m => m.Name).ToList() }, { "owner", !string.IsNullOrEmpty(s.OwnerPhone) },
                { "stats", new Dictionary<string, object> { { "sent", r.Sent }, { "yes", r.Yes }, { "no", r.No }, { "silent", r.Silent }, { "waiting", r.Waiting }, { "saved", r.Saved } } } };
        }

        // the confirmation list's row: "reminded" / "silent" (no answer, someone was told to call) while it's pending
        public static void Decorate(Dictionary<string, object> row, SqlDataReader r)
        {
            if ((r["Status"] as string) != "pending") return;
            if (r["AlertedAt"] != DBNull.Value) { row["status"] = "silent"; var to = r["AlertTo"] as string ?? ""; int dot = to.IndexOf(" · ", StringComparison.Ordinal); row["alertTo"] = dot > 0 ? to.Substring(0, dot) : to; }
            else if (r["RemindedAt"] != DBNull.Value) row["status"] = "reminded";
        }
    }
}
