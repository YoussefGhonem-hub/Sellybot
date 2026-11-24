// Messages Sellybot starts itself from the store's WhatsApp number (not answers to a customer's message):
//   ToCustomer: abandoned cart, «بلّغني لما يرجع». Kept in the customer's chat history, so the assistant knows what was said
//               and Wapilot's echo of it is recognised as ours.
//   ToOwner:    the weekly report and handoff alerts, to the owner's own number (Stores.OwnerPhone). Never part of a customer chat;
//               when the owner's number IS the store's number, it goes to the store's own chat ("message yourself").
// Every send is one message to one person because of something they did; nothing here is a broadcast.
using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public static class WaNotify
    {
        public const string NotLinked = "واتساب المتجر مش مربوط";

        // Can the store send at all right now? null = yes, otherwise why not (for the dashboard's lists)
        public static string CantSend(SqlConnection c, StoreInfo s)
        {
            if (s == null || s.Status != "active") return "المتجر مش شغال";
            if (!WaAccount.HasService(s)) return "خدمة الواتساب مش مفعّلة أو خلصت";
            if (string.IsNullOrEmpty(s.WaInstance) || string.IsNullOrEmpty(s.WaNumber) || WaAccount.Token(c, s) == null) return NotLinked;
            return null;
        }

        // null = sent (queued at Wapilot), otherwise why not
        public static async Task<string> ToCustomer(SqlConnection c, StoreInfo s, string phone, string text, string idem)
        {
            var why = CantSend(c, s);
            if (why != null) return why;
            if (string.IsNullOrEmpty(phone)) return "مفيش رقم موبايل";
            if (phone == s.WaNumber) return "ده رقم المتجر نفسه";
            long? mine = WhatsAppBot.Save(c, s.Id, phone, false, text, null);
            try { await Wapilot.Send(WaAccount.Token(c, s), s.WaInstance, phone + "@c.us", text, idem); return null; }
            catch (WaException ex)
            {
                if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                return ex.Message;
            }
        }

        // from the number the owner picked for reports when the store has more than one (WaLines.ReportSender), else the main one
        public static async Task<string> ToOwner(SqlConnection c, StoreInfo s, string text, string idem)
        {
            var why = CantSend(c, s);
            if (why != null) return why;
            if (string.IsNullOrEmpty(s.OwnerPhone)) return "مفيش رقم واتساب ليك في الإعدادات";
            try
            {
                var from = WaLines.ReportSender(c, s).Instance;
                foreach (var part in Wapilot.Split(text)) { await Wapilot.Send(WaAccount.Token(c, s), from, s.OwnerPhone + "@c.us", part, idem); idem += "+"; }
                return null;
            }
            catch (WaException ex) { return ex.Message; }
        }

        // Quiet hours in Cairo (setting "WaQuietHours" = "start-end", default "23-9"; equal hours = none): the next end (UTC), or null by day
        public static DateTime? QuietUntil()
        {
            int from = 23, to = 9;
            var parts = (Web.Setting("WaQuietHours", "23-9") ?? "").Split('-');
            int a, b;
            if (parts.Length == 2 && int.TryParse(parts[0], out a) && int.TryParse(parts[1], out b) && a >= 0 && a < 24 && b >= 0 && b < 24) { from = a; to = b; }
            if (from == to) return null;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Cairo);
            bool quiet = from > to ? (local.Hour >= from || local.Hour < to) : (local.Hour >= from && local.Hour < to);
            if (!quiet) return null;
            var day = local.Hour >= to ? local.Date.AddDays(1) : local.Date;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddHours(to), DateTimeKind.Unspecified), Cairo);
        }

        static TimeZoneInfo FindZone()
        {
            foreach (var id in new[] { "Egypt Standard Time", "Africa/Cairo" })
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            return TimeZoneInfo.CreateCustomTimeZone("Cairo", TimeSpan.FromHours(2), "Cairo", "Cairo");
        }


        static readonly TimeZoneInfo Cairo = FindZone();

        // The owner's number as typed in the dashboard -> digits with the country code, or null
        public static string Phone(string input) { return WaChat.Phone(input); }
    }
}
