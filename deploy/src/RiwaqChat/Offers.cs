// «ابعت عرض» (2026-11-23, the user, every plan): the store owner writes an offer (text, and a picture if they want) and it goes from
// the store's WhatsApp number to its customers, one number at a time. Dashboard tab «📣 ابعت عرض», api/offers.ashx.
//
// Who it can go to (the owner ticks any of them; a number is sent to once even when it's in several):
//   wa       everyone who has written to the store's WhatsApp (BcContacts.WaLastAt, kept from their first message on)
//   wa30     the same, in the last 30 days only
//   restock  customers who asked «بلّغني لما يرجع» (RestockWaits)
//   orders   customers whose orders went through Sellybot: order confirmation (Shopify / EasyOrders), the delivery-day message,
//            orders made in the chat, abandoned carts that were bought in the end
//   list     the owner's own list (BcContacts.Added): numbers typed or uploaded from Excel / CSV (the owner confirms these people
//            agreed to hear from the store), and Messenger / Instagram customers who gave their number when the assistant asked
//            (Stores.BcAsk: once per customer, «تحب يوصلك جديدنا وعروضنا على الواتساب؟» with Meta's phone-number button)
// Never to a number that sent «وقف» (WaOptOuts) or to the store's own number.
//
// WhatsApp bans numbers that blast messages, and the store's number is its business. So: one offer at a time per store, a random
// 20-40 seconds between messages, at most BcDailyCap (250) a day per store, nothing at night (WaNotify.QuietUntil), the customer's
// first name when it's known ({الاسم}), and every message ends with how to stop them («وقف»), which Egypt's data protection law
// (151/2020) asks of direct marketing too. Each message is kept in the customer's WhatsApp chat, so the assistant knows the offer
// when they answer it, and Wapilot's echo of it is known as ours.
// The sending runs in the background jobs (Jobs.cs → SendDue), outside their lease: every slot and every number is claimed in the
// database, so two runs never send the same message, and the idempotency key per number makes a resend after a crash harmless.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;
using DocumentFormat.OpenXml.Packaging;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace RiwaqChat
{
    public static class Offers
    {
        public const string Feature = "broadcast";
        public const int MaxText = 900;            // + the «وقف» line stays under WhatsApp's 1024-character picture caption
        public const int MaxImage = 1500 * 1024;   // the dashboard shrinks the picture to 1600px first
        public const int MaxPerOffer = 20000, MaxList = 50000, MaxImportRows = 20000;
        public static readonly string[] Audiences = { "wa", "wa30", "restock", "orders", "list" };
        public const string Footer = "لو مش عايز توصلك رسايلنا، ابعت «وقف».";

        public static int DailyCap(SqlConnection c) { int v; return int.TryParse(Billing.Get(c, "BcDailyCap"), out v) && v > 0 ? v : 250; }
        public static int GapMin(SqlConnection c) { int v; return int.TryParse(Billing.Get(c, "BcGapMin"), out v) && v >= 5 ? v : 20; }
        public static int GapMax(SqlConnection c) { int v, min = GapMin(c); return int.TryParse(Billing.Get(c, "BcGapMax"), out v) && v >= min ? v : Math.Max(min, 40); }

        // ---------- numbers ----------
        // A number as typed or as Excel kept it -> international digits ("01001234567", "1001234567" (Excel drops the 0),
        // "+20 100 123 4567", "00201001234567", "2.01001234567E+11") -> "201001234567"; null when it isn't a phone number
        public static string Norm(string raw)
        {
            var t = ShopifyAdmin.ToLatinDigits(raw ?? "").Trim();
            decimal sci;
            if (Regex.IsMatch(t, @"^\d(\.\d+)?[eE]\+?\d+$") && decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out sci)) t = sci.ToString("0", CultureInfo.InvariantCulture);
            var d = Regex.Replace(t, @"\D", "");
            if (d.StartsWith("00")) d = d.Substring(2);
            if (d.Length == 10 && d[0] == '1' && "0125".IndexOf(d[1]) >= 0) d = "20" + d;   // an Egyptian mobile without its 0
            if (d.Length == 11 && d.StartsWith("01")) d = "2" + d;                          // 01001234567
            if (d.Length == 13 && d.StartsWith("2001")) d = "20" + d.Substring(3);          // +20 01001234567
            return d.Length >= 10 && d.Length <= 15 ? d : null;
        }

        static string Cut(string s, int n) { s = (s ?? "").Trim(); return s.Length == 0 ? null : (s.Length > n ? s.Substring(0, n) : s); }
        static string FirstName(string full)
        {
            var f = (full ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return f != null && f.Length >= 2 && f.Length <= 20 && f.Any(char.IsLetter) && !f.Any(char.IsDigit) ? f : null;
        }

        public class Who { public string Phone, Name; }

        // Everyone a store's WhatsApp customer wrote: kept for the «wa» audiences (WaMessages themselves are cleared after 60 days)
        public static void Seen(SqlConnection c, int storeId, string chatKey)
        {
            if (string.IsNullOrEmpty(chatKey) || chatKey.Length < 8 || chatKey.Length > 15 || !chatKey.All(char.IsDigit)) return;
            try
            {
                using (var cmd = Db.Cmd(c, @"UPDATE dbo.BcContacts SET WaLastAt=SYSUTCDATETIME() WHERE StoreId=@s AND Phone=@p;
                    IF @@ROWCOUNT = 0 INSERT INTO dbo.BcContacts(StoreId, Phone, WaLastAt) VALUES(@s, @p, SYSUTCDATETIME());", "@s", storeId, "@p", chatKey))
                    cmd.ExecuteNonQuery();
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { }   // two messages at once: the other one saved it
            catch (Exception ex) { Log.Error(ex); }   // never lose a customer's message over this
        }

        static List<Who> Read(SqlConnection c, string sql, int storeId)
        {
            var list = new List<Who>();
            using (var cmd = Db.Cmd(c, sql, "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new Who { Phone = r[0] as string, Name = r.FieldCount > 1 ? r[1] as string : null });
            return list;
        }

        static List<Who> Audience(SqlConnection c, int storeId, string a)
        {
            switch (a)
            {
                case "wa": return Read(c, "SELECT Phone, Name FROM dbo.BcContacts WHERE StoreId=@s AND WaLastAt IS NOT NULL", storeId);
                case "wa30": return Read(c, "SELECT Phone, Name FROM dbo.BcContacts WHERE StoreId=@s AND WaLastAt > DATEADD(DAY,-30,SYSUTCDATETIME())", storeId);
                case "restock": return Read(c, "SELECT DISTINCT Phone, CAST(NULL AS NVARCHAR(100)) FROM dbo.RestockWaits WHERE StoreId=@s", storeId);
                case "list": return Read(c, "SELECT Phone, Name FROM dbo.BcContacts WHERE StoreId=@s AND Added IS NOT NULL", storeId);
                case "orders": return Read(c, @"
SELECT Phone, Customer FROM dbo.OrderConfirms WHERE StoreId=@s AND Phone IS NOT NULL
UNION ALL SELECT Phone, Customer FROM dbo.EoConfirms WHERE StoreId=@s AND Phone IS NOT NULL
UNION ALL SELECT Phone, Customer FROM dbo.DeliveryNotices WHERE StoreId=@s AND Phone IS NOT NULL
UNION ALL SELECT Phone, Name FROM dbo.ChatOrders WHERE StoreId=@s AND Status='placed' AND Phone IS NOT NULL
UNION ALL SELECT Phone, Customer FROM dbo.CartReminders WHERE StoreId=@s AND Status IN ('ordered','recovered') AND Phone IS NOT NULL", storeId);
                default: return new List<Who>();
            }
        }

        static HashSet<string> OptOuts(SqlConnection c, int storeId)
        {
            var set = new HashSet<string>();
            using (var cmd = Db.Cmd(c, "SELECT Phone FROM dbo.WaOptOuts WHERE StoreId=@s", "@s", storeId))
            using (var r = cmd.ExecuteReader()) while (r.Read()) set.Add((string)r[0]);
            return set;
        }

        // The numbers an offer to these audiences goes to: normalised, once each (with a name when any source has one), without
        // the ones who said «وقف» and the store's own number
        public static List<Who> Pick(SqlConnection c, StoreInfo s, IEnumerable<string> audiences)
        {
            var stop = OptOuts(c, s.Id);
            var map = new Dictionary<string, Who>();
            foreach (var a in audiences.Distinct())
                foreach (var w in Audience(c, s.Id, a))
                {
                    var p = Norm(w.Phone);
                    if (p == null || stop.Contains(p) || p == s.WaNumber) continue;
                    Who have;
                    if (!map.TryGetValue(p, out have)) map[p] = new Who { Phone = p, Name = Cut(w.Name, 100) };
                    else if (have.Name == null) have.Name = Cut(w.Name, 100);
                }
            return map.Values.ToList();
        }

        static Dictionary<string, object> Counts(SqlConnection c, StoreInfo s)
        {
            var d = new Dictionary<string, object>();
            foreach (var a in Audiences) d[a] = Pick(c, s, new[] { a }).Count;
            return d;
        }

        // ---------- the owner's own list ----------
        static readonly Regex PhoneRx = new Regex(@"\+?\d[\d \-().]{6,22}\d|\d(\.\d+)?[eE]\+?\d+", RegexOptions.Compiled);

        // Lines of "number" or "name - number" (any order, any separator); bad = lines with digits but no number we can use
        public static List<Who> FromText(string text, out int bad)
        {
            bad = 0;
            var list = new List<Who>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = ShopifyAdmin.ToLatinDigits(raw).Trim();
                if (line.Length == 0) continue;
                bool any = false;
                var ms = PhoneRx.Matches(line);
                foreach (Match m in ms)
                {
                    var p = Norm(m.Value);
                    if (p == null) continue;
                    any = true;
                    string rest = PhoneRx.Replace(line, " ");
                    rest = Regex.Replace(rest, @"[|,;:\t\-_/\\""'()\[\]]+", " ");
                    rest = Regex.Replace(rest, @"\s+", " ").Trim();
                    list.Add(new Who { Phone = p, Name = rest.Length >= 2 && rest.Length <= 60 && rest.Any(char.IsLetter) ? rest : null });
                }
                if (!any && line.Any(char.IsDigit)) bad++;
            }
            return list;
        }

        // An Excel sheet: every row is read like a line of text (its cells side by side), numbers stored as numbers included
        public static string ExcelText(byte[] data)
        {
            var sb = new StringBuilder();
            using (var ms = new MemoryStream(data))
            using (var doc = SpreadsheetDocument.Open(ms, false))
            {
                var wb = doc.WorkbookPart;
                var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<X.SharedStringItem>().Select(x => x.InnerText).ToList() ?? new List<string>();
                int rows = 0;
                foreach (var sheet in wb.Workbook.Sheets.Elements<X.Sheet>())
                {
                    var part = (WorksheetPart)wb.GetPartById(sheet.Id);
                    foreach (var row in part.Worksheet.Descendants<X.Row>())
                    {
                        if (++rows > MaxImportRows) return sb.ToString();
                        var cells = new List<string>();
                        foreach (var cell in row.Elements<X.Cell>())
                        {
                            string v;
                            if (cell.DataType != null && cell.DataType.Value == X.CellValues.SharedString)
                            {
                                int i; v = int.TryParse(cell.CellValue?.Text, out i) && i >= 0 && i < shared.Count ? shared[i] : null;
                            }
                            else if (cell.DataType != null && cell.DataType.Value == X.CellValues.InlineString) v = cell.InlineString?.InnerText;
                            else
                            {
                                v = cell.CellValue?.Text;
                                decimal num;   // a number cell: 201001234567 may be kept as 2.01001234567E+11
                                if (v != null && (cell.DataType == null || cell.DataType.Value == X.CellValues.Number)
                                    && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out num) && num == Math.Floor(num) && num > 0)
                                    v = num.ToString("0", CultureInfo.InvariantCulture);
                            }
                            if (!string.IsNullOrWhiteSpace(v)) cells.Add(v.Replace('\n', ' ').Trim());
                        }
                        if (cells.Count > 0) sb.AppendLine(string.Join(" | ", cells));
                    }
                }
            }
            return sb.ToString();
        }

        public class AddResult { public int Found, Added, Already, Stopped, Bad; }

        public static AddResult AddToList(SqlConnection c, int storeId, List<Who> found, string source, int bad)
        {
            var res = new AddResult { Bad = bad };
            var uniq = new Dictionary<string, Who>();
            foreach (var w in found) { Who h; if (!uniq.TryGetValue(w.Phone, out h)) uniq[w.Phone] = w; else if (h.Name == null) h.Name = w.Name; }
            res.Found = uniq.Count;
            if (uniq.Count == 0) return res;
            var t = new DataTable();
            t.Columns.Add("Phone", typeof(string)); t.Columns.Add("Name", typeof(string));
            foreach (var w in uniq.Values) t.Rows.Add(w.Phone, (object)Cut(w.Name, 100) ?? DBNull.Value);
            using (var cmd = Db.Cmd(c, "CREATE TABLE #in(Phone NVARCHAR(20) NOT NULL PRIMARY KEY, Name NVARCHAR(100) NULL)")) cmd.ExecuteNonQuery();
            try
            {
                using (var bulk = new SqlBulkCopy(c) { DestinationTableName = "#in" }) bulk.WriteToServer(t);
                using (var cmd = Db.Cmd(c, @"
DECLARE @already INT = (SELECT COUNT(*) FROM #in i JOIN dbo.BcContacts b ON b.StoreId=@s AND b.Phone=i.Phone AND b.Added IS NOT NULL);
DECLARE @stopped INT = (SELECT COUNT(*) FROM #in i JOIN dbo.WaOptOuts o ON o.StoreId=@s AND o.Phone=i.Phone);
UPDATE b SET Name = ISNULL(b.Name, i.Name), Added = ISNULL(b.Added, @src), AddedAt = ISNULL(b.AddedAt, SYSUTCDATETIME())
  FROM dbo.BcContacts b JOIN #in i ON b.StoreId=@s AND b.Phone=i.Phone;
INSERT INTO dbo.BcContacts(StoreId, Phone, Name, Added, AddedAt)
  SELECT @s, i.Phone, i.Name, @src, SYSUTCDATETIME() FROM #in i WHERE NOT EXISTS (SELECT 1 FROM dbo.BcContacts b WHERE b.StoreId=@s AND b.Phone=i.Phone);
SELECT @already, @stopped;", "@s", storeId, "@src", source))
                using (var r = cmd.ExecuteReader())
                    if (r.Read()) { res.Already = r.GetInt32(0); res.Stopped = r.GetInt32(1); }
            }
            finally { using (var cmd = Db.Cmd(c, "DROP TABLE #in")) cmd.ExecuteNonQuery(); }
            res.Added = res.Found - res.Already;
            return res;
        }

        public static int ListCount(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.BcContacts WHERE StoreId=@s AND Added IS NOT NULL", "@s", storeId)) return (int)cmd.ExecuteScalar();
        }

        // ---------- the message ----------
        // {الاسم} -> the customer's first name, or nothing ("أهلًا {الاسم}،" -> "أهلًا،"); then the «وقف» line
        public static string Compose(string text, string name)
        {
            var first = FirstName(name);
            var t = (text ?? "").Replace("{الاسم}", first ?? "").Replace("{name}", first ?? "");
            if (first == null) { t = Regex.Replace(t, @"[ \t]+([،,.!؟?:])", "$1"); t = Regex.Replace(t, @"[ \t]{2,}", " "); }
            return t.Trim() + "\n\n" + Footer;
        }

        static async Task<string> SendTo(string token, string instance, string phone, string body, byte[] image, string type, string idem)
        {
            if (image != null) return await Wapilot.SendImage(token, instance, phone + "@c.us", image, type, body, idem);
            return await Wapilot.Send(token, instance, phone + "@c.us", body, idem);
        }

        // what the customer's chat keeps (the assistant sees it; Wapilot's echo of the caption is recognised as ours)
        static string Kept(string body, bool image) { return image ? "📷 [صورة العرض]\n" + body : body; }

        // A test from the dashboard to one number (the owner's): sent now, not queued. null = sent, otherwise why not.
        public static async Task<string> Test(SqlConnection c, StoreInfo s, string phone, string text, byte[] image, string type)
        {
            var why = WaNotify.CantSend(c, s);
            if (why != null) return why;
            if (phone == s.WaNumber) return "ده رقم المتجر نفسه، اكتب رقم تاني (رقمك انت مثلًا).";
            var body = Compose(text, null);
            long? mine = WhatsAppBot.Save(c, s.Id, phone, false, Kept(body, image != null), null);
            try { await SendTo(WaAccount.Token(c, s), s.WaInstance, phone, body, image, type, "sb-bctest-" + s.Id + "-" + DateTime.UtcNow.Ticks); return null; }
            catch (WaException ex)
            {
                if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                return ex.Message;
            }
        }

        // ---------- an offer ----------
        public static int? Active(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id FROM dbo.Broadcasts WHERE StoreId=@s AND Status IN ('sending','paused') ORDER BY Id DESC", "@s", storeId))
            { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? (int?)null : (int)v; }
        }

        public static int Create(SqlConnection c, StoreInfo s, string text, byte[] image, string type, List<string> audiences, List<Who> to)
        {
            int id;
            using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Broadcasts(StoreId, Text, Image, ImageType, Audience, Total, Status) OUTPUT INSERTED.Id
                VALUES(@s, @t, @img, @it, @a, @n, 'sending')", "@s", s.Id, "@t", text, "@img", image != null ? (object)image : System.Data.SqlTypes.SqlBinary.Null, "@it", (object)type ?? DBNull.Value,
                "@a", string.Join(",", audiences), "@n", to.Count))
                id = (int)cmd.ExecuteScalar();
            var t = new DataTable();
            foreach (var col in new[] { "BroadcastId", "StoreId", "Phone", "Name", "Status" }) t.Columns.Add(col, col.EndsWith("Id") ? typeof(int) : typeof(string));
            foreach (var w in to) t.Rows.Add(id, s.Id, w.Phone, (object)w.Name ?? DBNull.Value, "waiting");
            using (var bulk = new SqlBulkCopy(c) { DestinationTableName = "dbo.BcSends", BulkCopyTimeout = 120 })
            {
                foreach (DataColumn col in t.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                bulk.WriteToServer(t);
            }
            return id;
        }

        // ---------- sending (background jobs) ----------
        class Step
        {
            public TimeSpan Wait; public bool Go;
            public int StoreId, Bid; public long RowId; public long? Mine;
            public string Token, Instance, Phone, Body, Type; public byte[] Image;
        }

        static readonly Dictionary<int, Tuple<byte[], string>> Pictures = new Dictionary<int, Tuple<byte[], string>>();

        public static async Task SendDue(CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddMinutes(4);
            using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "UPDATE dbo.BcSends SET Status='waiting', ClaimedAt=NULL WHERE Status='sending' AND ClaimedAt < DATEADD(MINUTE,-10,SYSUTCDATETIME())"))
                    cmd.ExecuteNonQuery();   // a run that died mid-send: the same idempotency key makes sending it again safe
            try { await CheckSent(30); } catch (Exception ex) { Log.Error(ex); }
            var rnd = new Random();
            while (!ct.IsCancellationRequested && DateTime.UtcNow < until)
            {
                Step st;
                using (var c = Db.Open()) st = Next(c);
                if (st == null) return;
                if (!st.Go)
                {
                    if (st.Wait <= TimeSpan.Zero) continue;
                    if (DateTime.UtcNow + st.Wait >= until) return;
                    await Task.Delay(st.Wait, ct);
                    continue;
                }
                string msgId = null; WaException fail = null;
                try { msgId = await SendTo(st.Token, st.Instance, st.Phone, st.Body, st.Image, st.Type, "sb-bc-" + st.Bid + "-" + st.RowId); }
                catch (WaException ex) { fail = ex; }
                catch (Exception ex) { Log.Error(ex); fail = new WaException("حصلت مشكلة في الإرسال."); }
                using (var c = Db.Open()) Done(c, st, msgId, fail, rnd);
            }
        }

        static void Set(SqlConnection c, int bid, string sql, params object[] args)
        {
            var all = new List<object> { "@b", bid }; all.AddRange(args);
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Broadcasts SET " + sql + " WHERE Id=@b", all.ToArray())) cmd.ExecuteNonQuery();
        }

        // The next thing to do: null = nothing to send; Wait = nothing due yet; Go = send Step to Phone
        static Step Next(SqlConnection c)
        {
            int bid, storeId; DateTime next; string type;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id, StoreId, NextAt, ImageType FROM dbo.Broadcasts WHERE Status='sending' ORDER BY NextAt, Id"))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                bid = (int)r["Id"]; storeId = (int)r["StoreId"]; next = DateTime.SpecifyKind((DateTime)r["NextAt"], DateTimeKind.Utc); type = r["ImageType"] as string;
            }
            var now = DateTime.UtcNow;
            if (next > now) return new Step { Wait = next - now };
            // this slot is ours (another run moving the same offer finds NextAt changed); Done() sets the real next time
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Broadcasts SET NextAt=DATEADD(MINUTE,3,SYSUTCDATETIME()) WHERE Id=@b AND Status='sending' AND NextAt <= SYSUTCDATETIME()", "@b", bid))
                if (cmd.ExecuteNonQuery() == 0) return new Step();

            var s = Db.StoreById(c, storeId);
            string why = s == null ? "المتجر مش موجود" : WaNotify.CantSend(c, s);
            if (why == null && !PlanGate.Allows(c, s, Feature)) why = "باقتك مش فيها «ابعت عرض»";
            if (why != null)
            {
                // waits (shown on the offer) and goes on by itself once WhatsApp is linked / renewed
                Set(c, bid, "NextAt=DATEADD(MINUTE,15,SYSUTCDATETIME()), Note=@n", "@n", "⏸️ الإرسال مستني: " + why);
                return new Step();
            }
            var quiet = WaNotify.QuietUntil();
            if (quiet.HasValue)
            {
                Set(c, bid, "NextAt=@t, Note=@n", "@t", quiet.Value.AddMinutes(new Random().Next(0, 15)), "@n", "🌙 مش بنبعت بالليل، هنكمّل الصبح.");
                return new Step();
            }
            int cap = DailyCap(c), sent; DateTime? oldest;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*), MIN(SentAt) FROM dbo.BcSends WHERE StoreId=@s AND SentAt > DATEADD(HOUR,-24,SYSUTCDATETIME())", "@s", storeId))
            using (var r = cmd.ExecuteReader()) { r.Read(); sent = r.GetInt32(0); oldest = r.IsDBNull(1) ? (DateTime?)null : DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc); }
            if (sent >= cap)
            {
                Set(c, bid, "NextAt=@t, Note=@n", "@t", (oldest ?? DateTime.UtcNow).AddHours(24).AddMinutes(1),
                    "@n", "📅 بعتنا " + cap + " رسالة في آخر 24 ساعة (الحد اليومي عشان رقمك يفضل في أمان)، هنكمّل بعدها لوحدنا.");
                return new Step();
            }
            long rowId; string phone, name;
            using (var cmd = Db.Cmd(c, @";WITH n AS (SELECT TOP 1 * FROM dbo.BcSends WITH (ROWLOCK, UPDLOCK, READPAST) WHERE BroadcastId=@b AND Status='waiting' ORDER BY Id)
                UPDATE n SET Status='sending', ClaimedAt=SYSUTCDATETIME() OUTPUT INSERTED.Id, INSERTED.Phone, INSERTED.Name;", "@b", bid))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) { rowId = 0; phone = name = null; }
                else { rowId = r.GetInt64(0); phone = r.GetString(1); name = r.IsDBNull(2) ? null : r.GetString(2); }
            }
            if (rowId == 0)
            {
                // nobody left: done once nothing is in flight
                using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.BcSends WHERE BroadcastId=@b AND Status='sending'", "@b", bid))
                    if ((int)cmd.ExecuteScalar() == 0) { Set(c, bid, "Status='done', FinishedAt=SYSUTCDATETIME(), Note=NULL"); lock (Pictures) Pictures.Remove(bid); }
                    else Set(c, bid, "NextAt=DATEADD(MINUTE,1,SYSUTCDATETIME())");
                return new Step();
            }
            if (WaOptOut.Has(c, storeId, phone))
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.BcSends SET Status='skipped', Note=N'بعت «وقف»' WHERE Id=@id", "@id", rowId)) cmd.ExecuteNonQuery();
                Set(c, bid, "NextAt=SYSUTCDATETIME()");
                return new Step();
            }
            string text;
            using (var cmd = Db.Cmd(c, "SELECT Text FROM dbo.Broadcasts WHERE Id=@b", "@b", bid)) text = (string)cmd.ExecuteScalar();
            byte[] image = null;
            if (type != null)
            {
                Tuple<byte[], string> pic;
                lock (Pictures) Pictures.TryGetValue(bid, out pic);
                if (pic == null)
                {
                    using (var cmd = Db.Cmd(c, "SELECT Image FROM dbo.Broadcasts WHERE Id=@b", "@b", bid)) pic = Tuple.Create(cmd.ExecuteScalar() as byte[], type);
                    lock (Pictures) { if (Pictures.Count > 5) Pictures.Clear(); Pictures[bid] = pic; }
                }
                image = pic.Item1;
            }
            var body = Compose(text, name);
            long? mine = WhatsAppBot.Save(c, storeId, phone, false, Kept(body, image != null), null);
            return new Step { Go = true, StoreId = storeId, Bid = bid, RowId = rowId, Mine = mine, Token = WaAccount.Token(c, s), Instance = s.WaInstance,
                Phone = phone, Body = body, Image = image, Type = type };
        }

        static void Done(SqlConnection c, Step st, string msgId, WaException fail, Random rnd)
        {
            if (fail == null)
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.BcSends SET Status='sent', SentAt=SYSUTCDATETIME(), WaMsgId=@m WHERE Id=@id", "@id", st.RowId, "@m", (object)msgId ?? DBNull.Value))
                    cmd.ExecuteNonQuery();
                Set(c, st.Bid, "NextAt=DATEADD(SECOND,@g,SYSUTCDATETIME()), Note=NULL", "@g", rnd.Next(GapMin(c), GapMax(c) + 1));
                return;
            }
            if (st.Mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", st.Mine.Value)) cmd.ExecuteNonQuery();
            // the connection, the account or Wapilot itself: not this number's fault. It goes back in line and the offer waits a while.
            bool ours = fail.Status == 0 || fail.Status == 401 || fail.Status == 403 || fail.Status == 404 || fail.Status == 429 || fail.Status >= 500
                        || fail.Code == "INSTANCE_NOT_READY" || fail.Code == "TOKEN_INVALID" || fail.Code == "INSTANCE_FORBIDDEN";
            if (ours)
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.BcSends SET Status='waiting', ClaimedAt=NULL WHERE Id=@id", "@id", st.RowId)) cmd.ExecuteNonQuery();
                Set(c, st.Bid, "NextAt=DATEADD(MINUTE,@m,SYSUTCDATETIME()), Note=@n", "@m", fail.Status == 429 ? 5 : 15, "@n", "⏸️ الإرسال مستني: " + Cut(fail.Message, 250));
                return;
            }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.BcSends SET Status='failed', Note=@n WHERE Id=@id", "@id", st.RowId, "@n", Cut(fail.Message, 300))) cmd.ExecuteNonQuery();
            Set(c, st.Bid, "NextAt=DATEADD(SECOND,@g,SYSUTCDATETIME())", "@g", rnd.Next(GapMin(c), GapMax(c) + 1));
        }

        // Wapilot only queues a message: a few minutes later, ask what became of it (went out, or not, e.g. a number not on WhatsApp)
        static async Task CheckSent(int max)
        {
            var rows = new List<Tuple<long, int, string>>();
            var creds = new Dictionary<int, Tuple<string, string>>();
            using (var c = Db.Open())
            {
                using (var cmd = Db.Cmd(c, @"SELECT TOP (@n) Id, StoreId, WaMsgId FROM dbo.BcSends WHERE Status='sent' AND WaMsgId IS NOT NULL
                    AND SentAt < DATEADD(MINUTE,-2,SYSUTCDATETIME()) AND SentAt > DATEADD(DAY,-2,SYSUTCDATETIME())
                    AND (CheckedAt IS NULL OR CheckedAt < DATEADD(MINUTE,-20,SYSUTCDATETIME())) ORDER BY CheckedAt, Id", "@n", max))
                using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(Tuple.Create(r.GetInt64(0), r.GetInt32(1), r.GetString(2)));
                foreach (var sid in rows.Select(x => x.Item2).Distinct())
                {
                    var s = Db.StoreById(c, sid);
                    var token = s == null || string.IsNullOrEmpty(s.WaInstance) ? null : WaAccount.Token(c, s);
                    if (token != null) creds[sid] = Tuple.Create(token, s.WaInstance);
                }
            }
            var results = new List<Tuple<long, Wapilot.Sent>>();
            foreach (var row in rows)
            {
                Tuple<string, string> k;
                if (!creds.TryGetValue(row.Item2, out k)) { results.Add(Tuple.Create(row.Item1, (Wapilot.Sent)null)); continue; }
                Wapilot.Sent got = null;
                try { got = await Wapilot.MessageStatus(k.Item1, k.Item2, row.Item3); } catch (WaException) { }
                results.Add(Tuple.Create(row.Item1, got));
            }
            if (results.Count == 0) return;
            using (var c = Db.Open())
                foreach (var x in results)
                {
                    string sql = x.Item2 == null || x.Item2.Status == "pending" ? "UPDATE dbo.BcSends SET CheckedAt=SYSUTCDATETIME() WHERE Id=@id"
                        : x.Item2.Status == "delivered" ? "UPDATE dbo.BcSends SET Status='delivered', CheckedAt=SYSUTCDATETIME() WHERE Id=@id"
                        : "UPDATE dbo.BcSends SET Status='failed', Note=@n, CheckedAt=SYSUTCDATETIME() WHERE Id=@id";
                    using (var cmd = Db.Cmd(c, sql, "@id", x.Item1, "@n", x.Item2 == null ? "" : FailNote(x.Item2.Error))) cmd.ExecuteNonQuery();
                }
        }

        static string FailNote(string code)
        {
            var k = (code ?? "").ToUpperInvariant();
            if (k.Contains("NOT_REGISTERED") || k.Contains("NOT_ON_WHATSAPP") || k.Contains("NOT_EXIST") || k.Contains("INVALID") || k.Contains("UNSUPPORTED"))
                return "الرقم ده مش على واتساب";
            return Cut("الرسالة ماوصلتش" + (k.Length > 0 ? " (" + code + ")" : ""), 300);
        }

        // ---------- what the dashboard shows ----------
        static string Iso(object v) { return v == null || v == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc).ToString("o"); }

        public static Dictionary<string, object> OfferJson(SqlConnection c, int id, bool details)
        {
            Dictionary<string, object> d = null;
            using (var cmd = Db.Cmd(c, "SELECT Id, Text, ImageType, Audience, Total, Status, Note, NextAt, CreatedAt, FinishedAt FROM dbo.Broadcasts WHERE Id=@b", "@b", id))
            using (var r = cmd.ExecuteReader())
                if (r.Read())
                    d = new Dictionary<string, object> {
                        { "id", r["Id"] }, { "text", r["Text"] }, { "image", r["ImageType"] != DBNull.Value }, { "audience", ((string)r["Audience"]).Split(',') },
                        { "total", r["Total"] }, { "status", r["Status"] }, { "note", r["Note"] as string ?? "" }, { "nextAt", Iso(r["NextAt"]) },
                        { "createdAt", Iso(r["CreatedAt"]) }, { "finishedAt", Iso(r["FinishedAt"]) } };
            if (d == null) return null;
            var counts = new Dictionary<string, object> { { "waiting", 0 }, { "sending", 0 }, { "sent", 0 }, { "delivered", 0 }, { "failed", 0 }, { "skipped", 0 }, { "stopped", 0 } };
            using (var cmd = Db.Cmd(c, "SELECT Status, COUNT(*) FROM dbo.BcSends WHERE BroadcastId=@b GROUP BY Status", "@b", id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.BcSends WHERE BroadcastId=@b AND StoppedAt IS NOT NULL", "@b", id)) counts["stopped"] = (int)cmd.ExecuteScalar();
            d["counts"] = counts;
            if (details)
            {
                var bad = new List<Dictionary<string, object>>();
                using (var cmd = Db.Cmd(c, "SELECT TOP 30 Phone, Name, Note FROM dbo.BcSends WHERE BroadcastId=@b AND Status='failed' ORDER BY Id DESC", "@b", id))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) bad.Add(new Dictionary<string, object> { { "phone", r["Phone"] }, { "name", r["Name"] as string ?? "" }, { "note", r["Note"] as string ?? "" } });
                d["failures"] = bad;
            }
            return d;
        }

        public static Dictionary<string, object> State(SqlConnection c, StoreInfo s)
        {
            int? active = Active(c, s.Id);
            var history = new List<Dictionary<string, object>>();
            var ids = new List<int>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 10 Id FROM dbo.Broadcasts WHERE StoreId=@s AND Status IN ('done','stopped') ORDER BY Id DESC", "@s", s.Id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt32(0));
            foreach (var id in ids) history.Add(OfferJson(c, id, false));

            var bySource = new Dictionary<string, object> { { "manual", 0 }, { "excel", 0 }, { "messenger", 0 }, { "instagram", 0 } };
            using (var cmd = Db.Cmd(c, "SELECT Added, COUNT(*) FROM dbo.BcContacts WHERE StoreId=@s AND Added IS NOT NULL GROUP BY Added", "@s", s.Id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) bySource[r.GetString(0)] = r.GetInt32(1);
            var recent = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 100 Phone, Name, Added, AddedAt FROM dbo.BcContacts WHERE StoreId=@s AND Added IS NOT NULL ORDER BY AddedAt DESC, Phone", "@s", s.Id))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) recent.Add(new Dictionary<string, object> { { "phone", r["Phone"] }, { "name", r["Name"] as string ?? "" }, { "source", r["Added"] }, { "at", Iso(r["AddedAt"]) } });

            var ask = new Dictionary<string, object> { { "on", s.BcAsk }, { "asked", 0 }, { "yes", 0 }, { "no", 0 } };
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*), SUM(CASE WHEN Answer='yes' THEN 1 ELSE 0 END), SUM(CASE WHEN Answer='no' THEN 1 ELSE 0 END) FROM dbo.BcAsks WHERE StoreId=@s", "@s", s.Id))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) { ask["asked"] = r.GetInt32(0); ask["yes"] = r.IsDBNull(1) ? 0 : r.GetInt32(1); ask["no"] = r.IsDBNull(2) ? 0 : r.GetInt32(2); }
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.MetaLinks WHERE StoreId=@s", "@s", s.Id)) ask["linked"] = (int)cmd.ExecuteScalar() > 0;

            int optouts, sentDay;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaOptOuts WHERE StoreId=@s", "@s", s.Id)) optouts = (int)cmd.ExecuteScalar();
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.BcSends WHERE StoreId=@s AND SentAt > DATEADD(HOUR,-24,SYSUTCDATETIME())", "@s", s.Id)) sentDay = (int)cmd.ExecuteScalar();
            return new Dictionary<string, object> {
                { "plan", PlanGate.Allows(c, s, Feature) }, { "service", WaAccount.HasService(s) },
                { "whatsapp", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber) }, { "cant", WaNotify.CantSend(c, s) ?? "" },
                { "storeNumber", s.WaNumber ?? "" }, { "ownerPhone", s.OwnerPhone ?? "" },
                { "limits", new Dictionary<string, object> { { "daily", DailyCap(c) }, { "gapMin", GapMin(c) }, { "gapMax", GapMax(c) }, { "maxText", MaxText }, { "maxList", MaxList }, { "maxOffer", MaxPerOffer } } },
                { "sentDay", sentDay }, { "optouts", optouts }, { "audiences", Counts(c, s) },
                { "list", new Dictionary<string, object> { { "total", ListCount(c, s.Id) }, { "bySource", bySource }, { "recent", recent } } },
                { "ask", ask },
                { "current", active.HasValue ? OfferJson(c, active.Value, true) : null },
                { "history", history } };
        }

        // ---------- Messenger / Instagram: «تحب يوصلك جديدنا وعروضنا على الواتساب؟» ----------
        public const string AskText = "🎁 تحب يوصلك جديدنا وعروضنا على الواتساب؟ دوس على رقمك تحت، أو اكتبه.";
        public const string NoPayload = "SB_BC_NO";
        static readonly Regex Busy = new Regex("رقم|موبايل|تليفون|واتساب|عنوان|اوردر|أوردر|الطلب|طلبك|تأكيد", RegexOptions.Compiled);

        // After the assistant answered on Messenger / Instagram: ask once per customer, at a calm moment (their 2nd message or later,
        // and not while the answer itself is about their number, address or an order)
        public static async Task MaybeAsk(StoreInfo s, MetaLink link, string chat, string secret, int customerMessages, string answer)
        {
            if (!s.BcAsk || customerMessages < 2 || Busy.IsMatch(answer ?? "")) return;
            using (var c = Db.Open())
            {
                if (!PlanGate.Allows(c, s, Feature) || !WaAccount.HasService(s)) return;
                using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ChatOrders WHERE StoreId=@s AND Sid=@sid AND Status<>'placed' AND UpdatedAt > DATEADD(HOUR,-2,SYSUTCDATETIME())",
                    "@s", s.Id, "@sid", link.Channel + ":" + chat))
                    if ((int)cmd.ExecuteScalar() > 0) return;   // ordering in the chat right now
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.BcAsks(StoreId, Channel, ChatId) SELECT @s, @ch, @c WHERE NOT EXISTS (SELECT 1 FROM dbo.BcAsks WHERE StoreId=@s AND Channel=@ch AND ChatId=@c)",
                    "@s", s.Id, "@ch", link.Channel, "@c", chat))
                    if (cmd.ExecuteNonQuery() == 0) return;   // asked before
                MetaBot.Save(c, s.Id, link.Channel, chat, false, AskText, null);
            }
            await Task.Delay(1500);
            var quick = new object[] { new Dictionary<string, object> { { "content_type", "user_phone_number" } },
                new Dictionary<string, object> { { "content_type", "text" }, { "title", "لأ، شكرًا" }, { "payload", NoPayload } } };
            object body = link.Channel == MetaApp.Fb
                ? (object)new Dictionary<string, object> { { "recipient", new { id = chat } }, { "messaging_type", "RESPONSE" }, { "message", new Dictionary<string, object> { { "text", AskText }, { "quick_replies", quick } } } }
                : new Dictionary<string, object> { { "recipient", new { id = chat } }, { "message", new Dictionary<string, object> { { "text", AskText }, { "quick_replies", quick } } } };
            try { await MetaApp.Call(HttpMethod.Post, Uri.EscapeDataString(link.AccountId) + "/messages", link.Token, secret, body); }
            catch (MetaException ex)
            {
                Log.Error(new Exception("BcAsk " + link.Channel + ": " + ex.Message));
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "DELETE FROM dbo.BcAsks WHERE StoreId=@s AND Channel=@ch AND ChatId=@c AND Answer IS NULL", "@s", s.Id, "@ch", link.Channel, "@c", chat)) cmd.ExecuteNonQuery();
            }
        }

        // The customer's answer to the question: the phone-number button (or the number typed right after the question), or «لأ، شكرًا».
        // True = handled here (the assistant doesn't answer it).
        public static bool AskAnswer(SqlConnection c, StoreInfo s, MetaLink link, string chat, string quick, string text, string mid)
        {
            string ch = link.Channel;
            bool no = quick == NoPayload;
            string phone = null;
            if (!no)
            {
                if (!string.IsNullOrEmpty(quick)) phone = Norm(quick);
                else
                {
                    // typed: only a lone number, and only as the answer to the question (our last message in the chat)
                    var t = ShopifyAdmin.ToLatinDigits(text ?? "").Trim();
                    if (!Regex.IsMatch(t, @"^\+?[\d \-()]{8,20}$")) return false;
                    phone = Norm(t);
                    if (phone == null) return false;
                    using (var cmd = Db.Cmd(c, "SELECT TOP 1 Body FROM dbo.MetaMessages WHERE StoreId=@s AND Channel=@ch AND ChatId=@c AND FromCustomer=0 ORDER BY Id DESC", "@s", s.Id, "@ch", ch, "@c", chat))
                        if (cmd.ExecuteScalar() as string != AskText) return false;
                }
                if (phone == null) return false;
            }
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.BcAsks WHERE StoreId=@s AND Channel=@ch AND ChatId=@c AND Answer IS NULL AND AskedAt > DATEADD(DAY,-7,SYSUTCDATETIME())",
                "@s", s.Id, "@ch", ch, "@c", chat))
                if ((int)cmd.ExecuteScalar() == 0) return false;
            if (MetaBot.Save(c, s.Id, ch, chat, true, (text ?? "").Length > 0 ? text : (phone ?? "لأ، شكرًا"), mid) == null) return true;   // a repeat delivery
            string reply;
            if (no)
            {
                using (var cmd = Db.Cmd(c, "UPDATE dbo.BcAsks SET Answer='no', AnsweredAt=SYSUTCDATETIME() WHERE StoreId=@s AND Channel=@ch AND ChatId=@c", "@s", s.Id, "@ch", ch, "@c", chat)) cmd.ExecuteNonQuery();
                reply = "تمام 👍 ولو احتجت أي حاجة، أنا موجود هنا.";
            }
            else
            {
                // they asked for it themselves: on the list, and an old «وقف» from them no longer holds
                using (var cmd = Db.Cmd(c, @"UPDATE dbo.BcContacts SET Added=ISNULL(Added, @src), AddedAt=ISNULL(AddedAt, SYSUTCDATETIME()) WHERE StoreId=@s AND Phone=@p;
                    IF @@ROWCOUNT = 0 INSERT INTO dbo.BcContacts(StoreId, Phone, Added, AddedAt) VALUES(@s, @p, @src, SYSUTCDATETIME());
                    DELETE FROM dbo.WaOptOuts WHERE StoreId=@s AND Phone=@p;
                    UPDATE dbo.BcAsks SET Answer='yes', Phone=@p, AnsweredAt=SYSUTCDATETIME() WHERE StoreId=@s AND Channel=@ch AND ChatId=@c;",
                    "@s", s.Id, "@p", phone, "@src", ch == MetaApp.Fb ? "messenger" : "instagram", "@ch", ch, "@c", chat)) cmd.ExecuteNonQuery();
                reply = "تمام ✅ هيوصلك جديدنا وعروضنا على الواتساب على رقم +" + phone + ". ولو حبيت توقفها في أي وقت، ابعت «وقف» على الواتساب.";
            }
            MetaBot.Save(c, s.Id, ch, chat, false, reply, null);
            var l = link;
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try { MetaCreds k; using (var c2 = Db.Open()) k = MetaApp.ForLink(c2, l); if (k != null) await MetaBot.Send(l, chat, reply, k.Secret); }
                catch (Exception ex) { Log.Error(ex); }
            });
            return true;
        }
    }

    // api/offers.ashx — the «📣 ابعت عرض» tab.
    //   GET                    the state (audiences with their counts, the owner's list, the offer being sent, past offers)
    //   GET ?image=<offer id>  that offer's picture (its owner only)
    //   POST ?action=count     {audiences}                         -> {count}: how many numbers they make together
    //        ?action=test      {text, image, phone}                -> sends it now to that number (the owner's)
    //        ?action=create    {text, image, audiences}            -> starts sending (one offer at a time)
    //        ?action=pause|resume|cancel {id}
    //        ?action=add       {text, consent}                     -> numbers typed by the owner on their list
    //        ?action=import    (multipart: file, consent)          -> numbers from an Excel / CSV / text file
    //        ?action=remove    {phone} · ?action=clear {source}    -> off the owner's list
    //        ?action=ask       {enabled}                           -> ask Messenger / Instagram customers for their number
    public class OffersHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }
        const int MaxFileBytes = 5 * 1024 * 1024;

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "GET")
                    {
                        int img;
                        if (int.TryParse(req.QueryString["image"], out img)) { Picture(c, s, img, res); return; }
                        Web.Json(res, 200, Offers.State(c, s));
                        return;
                    }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!Web.Allow("offers:" + s.Id, 200, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();

                    if (action == "import") { Import(c, s, req, res); return; }
                    var b = Web.Body(req, 2600000);

                    if (action == "count")
                    {
                        var aud = Audiences(b);
                        Web.Json(res, 200, new Dictionary<string, object> { { "count", aud.Count == 0 ? 0 : Offers.Pick(c, s, aud).Count } });
                        return;
                    }
                    if (action == "test")
                    {
                        var stop = Needs(c, s);
                        if (stop != null) { Deny(res, c, s, stop); return; }
                        string text; byte[] image; string type;
                        if (!Message(res, b, out text, out image, out type)) return;
                        var phone = Offers.Norm(Web.Str(b, "phone", 40) ?? s.OwnerPhone ?? "");
                        if (phone == null) { Web.Error(res, 400, "bad_phone", "اكتب رقم واتساب صح للتجربة، مثلًا 01001234567."); return; }
                        if (!Web.Allow("offers-test:" + s.Id, 15, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير في الساعة دي، استنى شوية."); return; }
                        var why = await Offers.Test(c, s, phone, text, image, type);
                        if (why != null) { Web.Error(res, 502, "not_sent", "الرسالة ماتبعتتش: " + why); return; }
                        Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "phone", phone } });
                        return;
                    }
                    if (action == "create")
                    {
                        var stop = Needs(c, s);
                        if (stop != null) { Deny(res, c, s, stop); return; }
                        if (Offers.Active(c, s.Id).HasValue) { Web.Error(res, 409, "busy", "فيه عرض لسه بيتبعت. استنى لما يخلص، أو ألغي الباقي منه الأول."); return; }
                        string text; byte[] image; string type;
                        if (!Message(res, b, out text, out image, out type)) return;
                        var aud = Audiences(b);
                        if (aud.Count == 0) { Web.Error(res, 400, "no_audience", "اختار العرض يروح لمين."); return; }
                        var to = Offers.Pick(c, s, aud);
                        if (to.Count == 0) { Web.Error(res, 400, "nobody", "مفيش أرقام في اللي اخترته. اختار مجموعة تانية، أو ضيف أرقام لقايمتك."); return; }
                        if (to.Count > Offers.MaxPerOffer) { Web.Error(res, 400, "too_many", "العرض الواحد يروح لـ " + Offers.MaxPerOffer.ToString("N0") + " رقم بالكتير. اختار مجموعة أصغر."); return; }
                        if (!Web.Allow("offers-create:" + s.Id, 10, TimeSpan.FromDays(1))) { Web.Error(res, 429, "rate_limited", "بعت عروض كتير النهارده، كمّل بكرة."); return; }
                        Offers.Create(c, s, text, image, type, aud, to);
                        Jobs.Kick();   // the first messages go out now, not at the next timer
                        Web.Json(res, 200, Offers.State(c, s));
                        return;
                    }
                    if (action == "pause" || action == "resume" || action == "cancel")
                    {
                        int id = Web.Int(b, "id") ?? 0;
                        if (action == "resume")
                        {
                            var stop = Needs(c, s);
                            if (stop != null) { Deny(res, c, s, stop); return; }
                        }
                        string sql = action == "pause" ? "UPDATE dbo.Broadcasts SET Status='paused', Note=NULL WHERE Id=@b AND StoreId=@s AND Status='sending'"
                            : action == "resume" ? "UPDATE dbo.Broadcasts SET Status='sending', NextAt=SYSUTCDATETIME(), Note=NULL WHERE Id=@b AND StoreId=@s AND Status='paused'"
                            : @"UPDATE dbo.Broadcasts SET Status='stopped', FinishedAt=SYSUTCDATETIME(), Note=NULL WHERE Id=@b AND StoreId=@s AND Status IN ('sending','paused');
                                IF @@ROWCOUNT > 0 UPDATE dbo.BcSends SET Status='skipped', Note=N'العرض اتلغى قبل ما يوصله' WHERE BroadcastId=@b AND Status='waiting';";
                        using (var cmd = Db.Cmd(c, sql, "@b", id, "@s", s.Id)) cmd.ExecuteNonQuery();
                        if (action == "resume") Jobs.Kick();
                        Web.Json(res, 200, Offers.State(c, s));
                        return;
                    }
                    if (action == "add")
                    {
                        if (!Consent(res, b.ContainsKey("consent") && b["consent"] is bool && (bool)b["consent"])) return;
                        int bad;
                        var found = Offers.FromText(Web.Str(b, "text", 400000), out bad);
                        Added(c, s, res, found, "manual", bad);
                        return;
                    }
                    if (action == "remove")
                    {
                        var phone = Offers.Norm(Web.Str(b, "phone", 30));
                        // off the list; someone who also wrote on WhatsApp stays in the WhatsApp audiences
                        using (var cmd = Db.Cmd(c, @"UPDATE dbo.BcContacts SET Added=NULL, AddedAt=NULL WHERE StoreId=@s AND Phone=@p;
                            DELETE FROM dbo.BcContacts WHERE StoreId=@s AND Phone=@p AND WaLastAt IS NULL AND Added IS NULL;", "@s", s.Id, "@p", phone ?? "")) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, Offers.State(c, s));
                        return;
                    }
                    if (action == "clear")
                    {
                        var src = (Web.Str(b, "source", 12) ?? "").ToLowerInvariant();
                        if (src != "all" && src != "manual" && src != "excel" && src != "messenger" && src != "instagram") { Web.Error(res, 400, "bad_request"); return; }
                        using (var cmd = Db.Cmd(c, @"UPDATE dbo.BcContacts SET Added=NULL, AddedAt=NULL WHERE StoreId=@s AND Added IS NOT NULL AND (@src='all' OR Added=@src);
                            DELETE FROM dbo.BcContacts WHERE StoreId=@s AND WaLastAt IS NULL AND Added IS NULL;", "@s", s.Id, "@src", src)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, Offers.State(c, s));
                        return;
                    }
                    if (action == "ask")
                    {
                        object en; bool on = b.TryGetValue("enabled", out en) && en is bool && (bool)en;
                        if (on && !PlanGate.Allows(c, s, Offers.Feature)) { PlanGate.Deny(res, c, s, Offers.Feature); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET BcAsk=@o WHERE Id=@s", "@o", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, Offers.State(c, Db.StoreById(c, s.Id)));
                        return;
                    }
                    Web.Error(res, 400, "bad_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static List<string> Audiences(Dictionary<string, object> b)
        {
            object v; var list = new List<string>();
            if (b.TryGetValue("audiences", out v) && v is System.Collections.IEnumerable && !(v is string))
                foreach (var x in (System.Collections.IEnumerable)v) { var a = Convert.ToString(x); if (Offers.Audiences.Contains(a) && !list.Contains(a)) list.Add(a); }
            if (list.Contains("wa")) list.Remove("wa30");   // everyone on WhatsApp already has the last 30 days
            return list;
        }

        // the offer's text and picture from the body; false = answered with the error
        static bool Message(HttpResponse res, Dictionary<string, object> b, out string text, out byte[] image, out string type)
        {
            image = null; type = null;
            text = (Web.Str(b, "text", 5000) ?? "").Replace("\r\n", "\n").Trim();
            if (text.Length == 0) { Web.Error(res, 400, "no_text", "اكتب رسالة العرض."); return false; }
            if (text.Length > Offers.MaxText) { Web.Error(res, 400, "too_long", "الرسالة طويلة، خليها " + Offers.MaxText + " حرف بالكتير."); return false; }
            var data = Web.Str(b, "image", 2500000);
            if (string.IsNullOrEmpty(data)) return true;
            int comma = data.IndexOf(',');
            try { image = Convert.FromBase64String(data.StartsWith("data:") && comma > 0 ? data.Substring(comma + 1) : data); }
            catch (FormatException) { image = null; }
            type = image == null ? null : Sniff(image);
            if (type == null) { Web.Error(res, 400, "bad_image", "الصورة دي مش مدعومة. اختار صورة JPG أو PNG أو WebP."); image = null; return false; }
            if (image.Length > Offers.MaxImage) { Web.Error(res, 400, "too_big", "الصورة كبيرة، اختار صورة أصغر."); image = null; type = null; return false; }
            return true;
        }

        static string Sniff(byte[] d)
        {
            if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
            if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
            if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "image/webp";
            return null;
        }

        static bool Consent(HttpResponse res, bool ok)
        {
            if (!ok) Web.Error(res, 400, "no_consent", "أكّد الأول إن الأرقام دي لزباين متجرك ووافقوا يوصلهم رسايل منه.");
            return ok;
        }

        static void Import(SqlConnection c, StoreInfo s, HttpRequest req, HttpResponse res)
        {
            if (!Consent(res, req.Form["consent"] == "1")) return;
            var file = req.Files.Count > 0 ? req.Files[0] : null;
            if (file == null || file.ContentLength == 0) { Web.Error(res, 400, "no_file", "اختار ملف."); return; }
            if (file.ContentLength > MaxFileBytes) { Web.Error(res, 400, "file_too_big", "الملف أكبر من 5 ميجا."); return; }
            if (!Web.Allow("offers-import:" + s.Id, 30, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "رفعت ملفات كتير، استنى شوية."); return; }
            string ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
            if (ext == ".xls") { Web.Error(res, 400, "old_excel", "الملف ده Excel قديم (.xls). افتحه واحفظه .xlsx، أو CSV، وارفعه تاني."); return; }
            if (ext != ".xlsx" && ext != ".csv" && ext != ".txt") { Web.Error(res, 400, "bad_type", "ارفع ملف Excel (.xlsx) أو CSV أو TXT فيه الأرقام."); return; }
            byte[] data;
            using (var ms = new MemoryStream()) { file.InputStream.CopyTo(ms); data = ms.ToArray(); }
            string text;
            try { text = ext == ".xlsx" ? Offers.ExcelText(data) : Extract.PlainText(data); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 400, "unreadable", "مقدرناش نقرا الملف ده. اتأكد إنه مش تالف أو محمي بباسورد."); return; }
            int bad;
            var found = Offers.FromText(text, out bad);
            if (found.Count == 0) { Web.Error(res, 400, "no_numbers", "مالقيناش أرقام موبايل في الملف ده. خلّي الأرقام في عمود لوحدها، زي 01001234567."); return; }
            Added(c, s, res, found, ext == ".xlsx" ? "excel" : "manual", bad);
        }

        static void Added(SqlConnection c, StoreInfo s, HttpResponse res, List<Offers.Who> found, string source, int bad)
        {
            if (found.Count == 0) { Web.Error(res, 400, "no_numbers", "مالقيناش أرقام موبايل. اكتب كل رقم في سطر، زي 01001234567."); return; }
            if (Offers.ListCount(c, s.Id) + found.Count > Offers.MaxList) { Web.Error(res, 400, "list_full", "قايمتك توصل لـ " + Offers.MaxList.ToString("N0") + " رقم بالكتير."); return; }
            var r = Offers.AddToList(c, s.Id, found, source, bad);
            var state = Offers.State(c, s);
            state["added"] = new Dictionary<string, object> { { "found", r.Found }, { "added", r.Added }, { "already", r.Already }, { "stopped", r.Stopped }, { "bad", r.Bad } };
            Web.Json(res, 200, state);
        }

        static void Picture(SqlConnection c, StoreInfo s, int id, HttpResponse res)
        {
            using (var cmd = Db.Cmd(c, "SELECT Image, ImageType FROM dbo.Broadcasts WHERE Id=@b AND StoreId=@s AND Image IS NOT NULL", "@b", id, "@s", s.Id))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) { res.StatusCode = 404; return; }
                res.ContentType = (string)r["ImageType"];
                res.AddHeader("Content-Security-Policy", "default-src 'none'");
                res.Cache.SetCacheability(HttpCacheability.Private);
                res.Cache.SetMaxAge(TimeSpan.FromDays(7));
                res.BinaryWrite((byte[])r["Image"]);
            }
        }

        // What stands in the way of sending: "plan", "service", "whatsapp", or null (like GrowthHandler.Needs)
        static string Needs(SqlConnection c, StoreInfo s)
        {
            if (!PlanGate.Allows(c, s, Offers.Feature)) return "plan";
            if (!WaAccount.HasService(s)) return "service";
            if (string.IsNullOrEmpty(s.WaInstance) || string.IsNullOrEmpty(s.WaNumber)) return "whatsapp";
            return null;
        }

        static void Deny(HttpResponse res, SqlConnection c, StoreInfo s, string stop)
        {
            if (stop == "plan") { PlanGate.Deny(res, c, s, Offers.Feature); return; }
            if (stop == "service") { Web.Error(res, 400, "no_service", "العروض بتتبعت من رقم واتساب متجرك. فعّل خدمة الواتساب أو جدّدها من «باقتي» الأول."); return; }
            Web.Error(res, 400, "no_whatsapp", "اربط واتساب المتجر الأول من صفحة «واتساب»، عشان العروض تتبعت من رقمك.");
        }
    }
}
