// SellyChat («💬 محادثاتي»): one place where a store's owner and team read and answer their customers from WhatsApp (every number),
// Messenger, Instagram and Telegram, inside Sellybot itself (inbox.html). The user asked for a Chatwoot-like inbox on 2026-11-26 and
// named it SellyChat, then chose to build it here instead of running Chatwoot on a server of its own (Sellybot's shared Windows
// hosting can't run Chatwoot; the first try, Chatwoot on its own server, was removed the same day).
// - Every chat with a customer message has a row in dbo.InboxChats, kept up to date by Touch() from WhatsAppBot.Save / MetaBot.Save:
//   last message, unread count, open / done, who it's assigned to. A row starts with the customer's first message, never with an
//   offer or a reminder sent to them.
// - A chat's thread is the channel's own messages (dbo.WaMessages / MetaMessages) and the team's private notes (dbo.InboxNotes).
// - An answer from here goes out the way the chat came (the same WhatsApp number through Wapilot, the Page / the Instagram account,
//   the store's bot) and is kept as the owner's message, MsgId "owner:in-<staff>-<random>" (staff 0 = the owner). So the assistant
//   steps aside for WhatsAppBot.PauseHours, as when the owner answers from the phone, and the channel's echo of it is known as ours
//   (IsOurs). «وقّف المساعد هنا» (InboxChats.BotOff) keeps it quiet in that chat until it's switched back.
// - Employees (dbo.InboxStaff): the owner adds them in the dashboard. They sign in on inbox.html with their email and a password we
//   make, into this page only: their own cookie (rc_staff), which nothing else in Sellybot reads.
// - The page asks for news every few seconds (shared hosting: no sockets).
// Plan gate "inbox": closed until the admin picks the lowest plan or names tester stores (InboxTesters); Sellybot's own store has it.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class Inbox
    {
        public const string Feature = "inbox", Brand = "SellyChat";
        const string TeamPrefix = "owner:in-";   // a message written here: owner:in-<staff id>-<random>
        const int MaxImage = 5 * 1024 * 1024;
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // ---------- who has it ----------
        public static bool Tester(SqlConnection c, StoreInfo s) { return s != null && (Billing.Get(c, "InboxTesters") ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries).Contains(s.Slug, StringComparer.OrdinalIgnoreCase); }
        // like Carriers.On, «no minimum» means closed: nothing shows until the admin picks the lowest plan (or names testers)
        public static bool On(SqlConnection c, StoreInfo s)
        {
            return s != null && (s.IsAdmin || Tester(c, s) || (!string.IsNullOrEmpty(Billing.Get(c, "MinPlan_" + Feature)) && PlanGate.Allows(c, s, Feature)));
        }
        public static List<string> Hide(SqlConnection c, StoreInfo s, List<string> hidden) { if (!On(c, s) && !hidden.Contains(Feature)) hidden.Add(Feature); return hidden; }
        public static int Seats(SqlConnection c) { int n; return int.TryParse(Billing.Get(c, "InboxSeats") ?? "", out n) && n > 0 ? n : 5; }

        // ---------- channels: "wa<line>" (wa0 = the main number), "fb", "ig", "tg" ----------
        public static string WaChan(int line) { return "wa" + line; }
        public static bool IsWa(string chan, out int line) { line = -1; return chan != null && chan.StartsWith("wa") && int.TryParse(chan.Substring(2), out line) && line >= 0; }
        public static bool Valid(string chan) { int l; return IsWa(chan, out l) || chan == MetaApp.Fb || chan == MetaApp.Ig || chan == Tg.Ch; }
        static string Cut(string s, int n) { return MetaApp.Cut(s, n); }

        static string Preview(string body)
        {
            var t = Regex.Replace(Regex.Replace(body ?? "", @"\*([^*\n]+)\*", "$1"), @"\s+", " ").Trim();   // WhatsApp's *bold* without its stars
            if (t == WaImages.Placeholder || t == "[صورة من المتجر]") return "📷 صورة";
            return Cut(t, 140);
        }

        // ---------- kept up to date as messages are saved (WhatsAppBot.Save, MetaBot.Save) ----------
        // kind: 1 the customer, 2 the assistant / an automatic message, 3 someone from the store (here, the phone, the Page)
        public static void Touch(SqlConnection c, int storeId, string chan, string chat, bool fromCustomer, string body, string msgId)
        {
            try
            {
                int kind = fromCustomer ? 1 : (msgId ?? "").StartsWith("owner:") ? 3 : 2;
                using (var cmd = Db.Cmd(c, @"
MERGE dbo.InboxChats WITH (HOLDLOCK) AS t USING (SELECT @s AS S, @ch AS Ch, @c AS C) AS x ON t.StoreId = x.S AND t.Chan = x.Ch AND t.ChatId = x.C
WHEN MATCHED THEN UPDATE SET LastAt = SYSUTCDATETIME(), LastText = @t, LastFrom = @k,
  Unread = CASE @k WHEN 1 THEN t.Unread + 1 WHEN 3 THEN 0 ELSE t.Unread END,
  LastCustomerAt = CASE WHEN @k = 1 THEN SYSUTCDATETIME() ELSE t.LastCustomerAt END,
  Status = CASE WHEN @k = 1 THEN 'open' ELSE t.Status END,
  NeedsHuman = CASE WHEN @k = 3 THEN 0 ELSE t.NeedsHuman END
WHEN NOT MATCHED AND @k = 1 THEN INSERT (StoreId, Chan, ChatId, LastAt, LastText, LastFrom, LastCustomerAt, Unread)
  VALUES (@s, @ch, @c, SYSUTCDATETIME(), @t, 1, SYSUTCDATETIME(), 1);",
                    "@s", storeId, "@ch", chan, "@c", Cut(chat, 100), "@t", Preview(body), "@k", kind)) cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Error(ex); }   // never in the way of the message itself
        }

        // «الزبون محتاجك» (Handoff.cs): the chat shows as waiting for someone
        public static void Flag(SqlConnection c, int storeId, string chan, string chat)
        {
            try { using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxChats SET NeedsHuman=1, Status='open' WHERE StoreId=@s AND Chan=@ch AND ChatId=@c", "@s", storeId, "@ch", chan, "@c", chat)) cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Log.Error(ex); }
        }

        // «وقّف المساعد هنا»: the assistant doesn't answer in this chat (WhatsAppBot / MetaBot / TgBot.Reply ask)
        public static bool Quiet(SqlConnection c, int storeId, string chan, string chat)
        {
            using (var cmd = Db.Cmd(c, "SELECT BotOff FROM dbo.InboxChats WHERE StoreId=@s AND Chan=@ch AND ChatId=@c", "@s", storeId, "@ch", chan, "@c", chat))
            { var v = cmd.ExecuteScalar(); return v is bool && (bool)v; }
        }

        // The channel's echo of a picture sent from here comes back without words: ours if one went to this chat in the last 2 minutes
        public static bool SentPicture(SqlConnection c, int storeId, string chan, string chat)
        {
            int line; bool wa = IsWa(chan, out line);
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo." + (wa ? "WaMessages" : "MetaMessages") + " WHERE StoreId=@s AND ChatId=@c AND " + (wa ? "Line=@l" : "Channel=@ch") +
                " AND FromCustomer=0 AND MsgId LIKE 'owner:in-%' AND Media LIKE 'inbox:%' AND CreatedAt > DATEADD(MINUTE,-2,SYSUTCDATETIME())", "@s", storeId, "@c", chat, "@l", line, "@ch", chan))
                return (int)cmd.ExecuteScalar() > 0;
        }

        // ---------- the team ----------
        public class Staff { public int Id, StoreId, Ver; public string Name, Email, Hash; public DateTime CreatedAt; public DateTime? LastAt; }

        static Staff ReadStaff(SqlDataReader r)
        {
            return new Staff { Id = (int)r["Id"], StoreId = (int)r["StoreId"], Ver = (int)r["Ver"], Name = (string)r["Name"], Email = (string)r["Email"], Hash = (string)r["PasswordHash"],
                CreatedAt = (DateTime)r["CreatedAt"], LastAt = r["LastAt"] as DateTime? };
        }
        public static List<Staff> Team(SqlConnection c, int storeId)
        {
            var l = new List<Staff>();
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.InboxStaff WHERE StoreId=@s ORDER BY CreatedAt", "@s", storeId)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(ReadStaff(r));
            return l;
        }
        static Staff StaffWhere(SqlConnection c, string where, object v)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.InboxStaff WHERE " + where, "@v", v)) using (var r = cmd.ExecuteReader()) return r.Read() ? ReadStaff(r) : null;
        }

        // A password that's easy to read out and type: no 0/O, 1/l/I
        public static string NewPassword()
        {
            const string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", low = "abcdefghjkmnpqrstuvwxyz", dig = "23456789";
            var b = new byte[11];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder();
            for (int i = 0; i < 3; i++) sb.Append(up[b[i] % up.Length]);
            for (int i = 3; i < 7; i++) sb.Append(low[b[i] % low.Length]);
            sb.Append('-');
            for (int i = 7; i < 11; i++) sb.Append(dig[b[i] % dig.Length]);
            return sb.ToString();
        }

        // the new employee's password (shown to the owner once)
        public static string AddStaff(SqlConnection c, StoreInfo s, string name, string email)
        {
            if (Team(c, s.Id).Count >= Seats(c)) throw new BadRequestException("وصلت لأكبر عدد موظفين (" + Seats(c) + "). امسح موظف الأول، أو كلّم الدعم لو محتاج أكتر.");
            using (var cmd = Db.Cmd(c, "SELECT (SELECT COUNT(*) FROM dbo.InboxStaff WHERE Email=@e) + (SELECT COUNT(*) FROM dbo.Stores WHERE OwnerEmail=@e)", "@e", email))
                if ((int)cmd.ExecuteScalar() > 0) throw new BadRequestException("الإيميل ده مستخدم قبل كده في Sellybot. استخدم إيميل تاني للموظف ده.");
            string pw = NewPassword();
            try
            {
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.InboxStaff(StoreId, Name, Email, PasswordHash) VALUES(@s, @n, @e, @h)", "@s", s.Id, "@n", name, "@e", email, "@h", Web.HashPassword(pw))) cmd.ExecuteNonQuery();
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { throw new BadRequestException("الإيميل ده مستخدم قبل كده في Sellybot. استخدم إيميل تاني للموظف ده."); }
            return pw;
        }

        // a new password: the old one and every place they're signed in stop working
        public static string ResetStaff(SqlConnection c, int storeId, int id)
        {
            string pw = NewPassword();
            using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxStaff SET PasswordHash=@h, Ver=Ver+1 WHERE Id=@id AND StoreId=@s", "@h", Web.HashPassword(pw), "@id", id, "@s", storeId))
                if (cmd.ExecuteNonQuery() == 0) throw new BadRequestException("الموظف ده مش موجود.");
            return pw;
        }

        public static void RemoveStaff(SqlConnection c, int storeId, int id)
        {
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.InboxChats SET AssignedTo=NULL WHERE StoreId=@s AND AssignedTo=@id;
                DELETE FROM dbo.InboxStaff WHERE Id=@id AND StoreId=@s;", "@s", storeId, "@id", id)) cmd.ExecuteNonQuery();
        }

        // ---------- the employee's sign-in: rc_staff = id|version|expiry|HMAC (its own key prefix: never mistaken for the owner's cookie) ----------
        const string StaffCookie = "rc_staff";
        static string Sign(string payload)
        {
            var key = Web.Setting("AuthSecret");
            if (key == null || key.Length < 32) throw new InvalidOperationException("AuthSecret is not configured");
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(key))) return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes("inbox-staff|" + payload)));
        }
        public static void StaffSignIn(HttpContext ctx, Staff st)
        {
            long exp = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            string payload = st.Id + "|" + st.Ver + "|" + exp;
            var ck = new HttpCookie(StaffCookie, payload + "|" + Sign(payload)) { HttpOnly = true, Secure = ctx.Request.IsSecureConnection, Path = "/", Expires = DateTime.UtcNow.AddDays(30) };
            ck.SameSite = SameSiteMode.Lax;
            ctx.Response.Cookies.Add(ck);
        }
        public static void StaffSignOut(HttpContext ctx) { ctx.Response.Cookies.Add(new HttpCookie(StaffCookie, "") { HttpOnly = true, Path = "/", Expires = DateTime.UtcNow.AddDays(-1) }); }
        static Staff FromCookie(HttpContext ctx, SqlConnection c)
        {
            var ck = ctx.Request.Cookies[StaffCookie];
            if (ck == null || string.IsNullOrEmpty(ck.Value)) return null;
            var p = ck.Value.Split('|');
            if (p.Length != 4) return null;
            string payload = p[0] + "|" + p[1] + "|" + p[2];
            if (!Secrets.Same(Encoding.UTF8.GetBytes(Sign(payload)), Encoding.UTF8.GetBytes(p[3]))) return null;
            long exp; int id, ver;
            if (!long.TryParse(p[2], out exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || !int.TryParse(p[0], out id) || !int.TryParse(p[1], out ver)) return null;
            var st = StaffWhere(c, "Id=@v", id);
            return st != null && st.Ver == ver ? st : null;
        }

        public class Who
        {
            public StoreInfo Store; public Staff Staff;
            public int Id { get { return Staff == null ? 0 : Staff.Id; } }
            public bool Owner { get { return Staff == null; } }
        }

        // the owner (their dashboard sign-in wins) or an employee
        public static Who Identify(HttpContext ctx, SqlConnection c)
        {
            var s = Me.Require(ctx, c);
            if (s != null) return new Who { Store = s };
            var st = FromCookie(ctx, c);
            if (st == null) return null;
            s = Db.StoreById(c, st.StoreId);
            if (s == null) return null;
            if (!st.LastAt.HasValue || DateTime.UtcNow - st.LastAt.Value > TimeSpan.FromMinutes(5))
                using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxStaff SET LastAt=SYSUTCDATETIME() WHERE Id=@id", "@id", st.Id)) cmd.ExecuteNonQuery();
            return new Who { Store = s, Staff = st };
        }

        public static Staff Login(SqlConnection c, string email, string password)
        {
            var st = StaffWhere(c, "Email=@v", email);
            return st != null && Web.CheckPassword(password, st.Hash) ? st : null;
        }

        // ---------- the list ----------
        public class Chat
        {
            public string Chan, ChatId, Name, LastText; public int LastFrom, Unread; public int? Assigned; public bool BotOff, Needs; public string Status;
            public DateTime LastAt; public DateTime? LastCustomerAt;
        }

        static Chat ReadChat(SqlDataReader r)
        {
            return new Chat { Chan = (string)r["Chan"], ChatId = (string)r["ChatId"], Name = r["Name"] as string, LastText = r["LastText"] as string, LastFrom = Convert.ToInt32(r["LastFrom"]),
                Unread = (int)r["Unread"], Assigned = r["AssignedTo"] as int?, BotOff = (bool)r["BotOff"], Needs = (bool)r["NeedsHuman"], Status = (string)r["Status"],
                LastAt = (DateTime)r["LastAt"], LastCustomerAt = r["LastCustomerAt"] as DateTime? };
        }

        public static Chat One(SqlConnection c, int storeId, string chan, string chat)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.InboxChats WHERE StoreId=@s AND Chan=@ch AND ChatId=@c", "@s", storeId, "@ch", chan, "@c", chat))
            using (var r = cmd.ExecuteReader()) return r.Read() ? ReadChat(r) : null;
        }

        // view: waiting (the customer wrote last, or asked for someone) · mine · all (open) · done
        public static List<Chat> List(SqlConnection c, Who w, string view, string chan, string q, DateTime? before, int n = 40)
        {
            var where = new StringBuilder("StoreId=@s");
            if (view == "done") where.Append(" AND Status='done'"); else where.Append(" AND Status='open'");
            if (view == "waiting") where.Append(" AND (LastFrom=1 OR NeedsHuman=1)");
            if (view == "mine") where.Append(" AND AssignedTo=@me");
            if (chan == "wa") where.Append(" AND Chan LIKE 'wa%'"); else if (Valid(chan)) where.Append(" AND Chan=@ch");
            string digits = Regex.Replace(ShopifyAdmin.ToLatinDigits(q ?? ""), @"\D", "");
            if (!string.IsNullOrWhiteSpace(q)) where.Append(digits.Length >= 3 ? " AND (ChatId LIKE @dq OR Name LIKE @q)" : " AND Name LIKE @q");
            if (before.HasValue) where.Append(" AND LastAt < @b");
            var list = new List<Chat>();
            using (var cmd = Db.Cmd(c, "SELECT TOP (@n) * FROM dbo.InboxChats WHERE " + where + " ORDER BY LastAt DESC",
                "@n", n, "@s", w.Store.Id, "@me", w.Id, "@ch", chan ?? "", "@q", "%" + (q ?? "").Trim() + "%", "@dq", "%" + (digits.StartsWith("0") ? digits.Substring(1) : digits) + "%",
                "@b", (object)before ?? DBNull.Value))
            using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(ReadChat(r));
            foreach (var x in list.Where(x => x.Name == null)) x.Name = Name(c, w.Store, x.Chan, x.ChatId);
            return list;
        }

        public static Dictionary<string, int> Counts(SqlConnection c, Who w)
        {
            using (var cmd = Db.Cmd(c, @"SELECT ISNULL(SUM(CASE WHEN LastFrom=1 OR NeedsHuman=1 THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN AssignedTo=@me THEN 1 ELSE 0 END),0), COUNT(*),
                ISNULL(SUM(CASE WHEN Unread > 0 THEN 1 ELSE 0 END),0) FROM dbo.InboxChats WHERE StoreId=@s AND Status='open'", "@s", w.Store.Id, "@me", w.Id))
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                return new Dictionary<string, int> { { "waiting", (int)r[0] }, { "mine", (int)r[1] }, { "all", (int)r[2] }, { "unread", (int)r[3] } };
            }
        }

        // A name for the chat, kept once found: the name from their orders on WhatsApp, the first name they gave Telegram,
        // the profile Meta lets the Page read (asked in the background); otherwise the number / a plain label
        static string Name(SqlConnection c, StoreInfo s, string chan, string chat)
        {
            string name = null; int line;
            if (IsWa(chan, out line))
            {
                var d = WaChat.Digits(chat);
                if (d != null)
                {
                    using (var cmd = Db.Cmd(c, @"SELECT TOP 1 Customer FROM dbo.CrmOrders WHERE StoreId=@s AND Phone=@p AND Customer IS NOT NULL AND Customer<>'' ORDER BY OrderedAt DESC", "@s", s.Id, "@p", d))
                        name = cmd.ExecuteScalar() as string;
                    if (string.IsNullOrWhiteSpace(name))
                        using (var cmd = Db.Cmd(c, "SELECT Name FROM dbo.BcContacts WHERE StoreId=@s AND Phone=@p AND Name IS NOT NULL AND Name<>''", "@s", s.Id, "@p", d))
                            name = cmd.ExecuteScalar() as string;
                }
            }
            else if (chan == Tg.Ch)
                using (var cmd = Db.Cmd(c, "SELECT Name FROM dbo.TgContacts WHERE StoreId=@s AND ChatId=@c", "@s", s.Id, "@c", chat)) name = cmd.ExecuteScalar() as string;
            else if (chan == MetaApp.Fb || chan == MetaApp.Ig)
            {
                int sid = s.Id;
                if (Web.Allow("inbox-name:" + sid + ":" + chan + ":" + chat, 1, TimeSpan.FromHours(6)))
                    HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                    {
                        var got = await MetaName(sid, chan, chat);
                        if (got != null) using (var c2 = Db.Open()) using (var cmd = Db.Cmd(c2, "UPDATE dbo.InboxChats SET Name=@n WHERE StoreId=@s AND Chan=@ch AND ChatId=@c AND Name IS NULL", "@n", got, "@s", sid, "@ch", chan, "@c", chat)) cmd.ExecuteNonQuery();
                    });
                return null;   // shown as the plain label until Meta answers
            }
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = Cut(name.Trim(), 100);
            using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxChats SET Name=@n WHERE StoreId=@s AND Chan=@ch AND ChatId=@c", "@n", name, "@s", s.Id, "@ch", chan, "@c", chat)) cmd.ExecuteNonQuery();
            return name;
        }

        static async Task<string> MetaName(int storeId, string ch, string chat)
        {
            try
            {
                MetaLink link; MetaCreds k;
                using (var c = Db.Open()) { link = MetaApp.Link(c, storeId, ch); k = link == null ? null : MetaApp.ForLink(c, link); }
                if (link == null || k == null || link.Token == null) return null;
                var p = await MetaApp.Call(HttpMethod.Get, Uri.EscapeDataString(chat) + (ch == MetaApp.Fb ? "?fields=first_name,last_name" : "?fields=name,username"), link.Token, k.Secret);
                string n = ch == MetaApp.Fb ? ((WaJson.S(p, "first_name") ?? "") + " " + (WaJson.S(p, "last_name") ?? "")).Trim() : (WaJson.S(p, "name") ?? WaJson.S(p, "username"));
                return string.IsNullOrWhiteSpace(n) ? null : Cut(n.Trim(), 100);
            }
            catch (MetaException) { return null; }
            catch (Exception ex) { Log.Error(ex); return null; }
        }

        // What the list and the thread's header show for a chat
        public static Dictionary<string, object> ChatJson(SqlConnection c, StoreInfo s, Chat x, Dictionary<int, string> names)
        {
            int line; bool wa = IsWa(x.Chan, out line);
            string digits = wa ? WaChat.Digits(x.ChatId) : null;
            string sub = digits != null ? WaLine.Local(digits) : x.Chan == Tg.Ch ? "تيليجرام" : x.Chan == MetaApp.Fb ? "مسنجر" : x.Chan == MetaApp.Ig ? "إنستجرام" : "واتساب";
            string label = x.Name ?? (digits != null ? WaLine.Local(digits) : x.Chan == MetaApp.Fb ? "زبون على المسنجر" : x.Chan == MetaApp.Ig ? "زبون على الإنستجرام" : x.Chan == Tg.Ch ? "زبون على تيليجرام" : "زبون على الواتساب");
            var d = new Dictionary<string, object> {
                { "chan", x.Chan }, { "kind", wa ? "wa" : x.Chan }, { "chat", x.ChatId }, { "name", label }, { "sub", sub }, { "phone", digits },
                { "last", x.LastText ?? "" }, { "lastFrom", x.LastFrom }, { "at", Iso(x.LastAt) }, { "unread", x.Unread }, { "status", x.Status },
                { "assigned", x.Assigned }, { "assignedName", x.Assigned.HasValue && names.ContainsKey(x.Assigned.Value) ? names[x.Assigned.Value] : null },
                { "botOff", x.BotOff }, { "needs", x.Needs } };
            // Messenger and Instagram take answers for 24 hours after the customer's last message
            if (x.Chan == MetaApp.Fb || x.Chan == MetaApp.Ig) d["window"] = x.LastCustomerAt.HasValue ? (object)Iso(x.LastCustomerAt.Value.AddHours(24)) : null;
            return d;
        }
        public static string Iso(DateTime t) { return DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("o"); }

        // the store's people by id, for "assigned to" and who wrote what (0 = the owner)
        public static Dictionary<int, string> Names(SqlConnection c, int storeId)
        {
            var d = new Dictionary<int, string> { { 0, "صاحب المتجر" } };
            foreach (var st in Team(c, storeId)) d[st.Id] = st.Name;
            return d;
        }

        // ---------- the thread ----------
        // The messages after `after` (new ones), or the last 60 before `before` (scrolling up), oldest first; the notes in the same span
        public static List<Dictionary<string, object>> Thread(SqlConnection c, StoreInfo s, string chan, string chat, long after, long before, Dictionary<int, string> names, out bool more)
        {
            int line; bool wa = IsWa(chan, out line);
            string where = wa ? "StoreId=@s AND ChatId=@c AND Line=@l" : "StoreId=@s AND Channel=@ch AND ChatId=@c";
            string sql = "SELECT TOP 61 Id, FromCustomer, Body, MsgId, Media, CreatedAt FROM dbo." + (wa ? "WaMessages" : "MetaMessages") + " WHERE " + where +
                (after > 0 ? " AND Id > @a" : before > 0 ? " AND Id < @b" : "") + " ORDER BY Id " + (after > 0 ? "ASC" : "DESC");
            var rows = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, sql, "@s", s.Id, "@c", chat, "@l", line, "@ch", chan, "@a", after, "@b", before))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    long id = (long)r["Id"]; bool customer = (bool)r["FromCustomer"];
                    string body = (string)r["Body"], msgId = r["MsgId"] as string ?? "", media = r["Media"] as string;
                    string who = customer ? "customer" : msgId.StartsWith(TeamPrefix) ? "team" : msgId.StartsWith("owner:") ? "phone" : "bot";
                    string by = null;
                    if (who == "team")
                    {
                        var m = Regex.Match(msgId, @"^owner:in-(\d+)-");
                        int sid; by = m.Success && int.TryParse(m.Groups[1].Value, out sid) && names.ContainsKey(sid) ? names[sid] : "حد من الفريق";
                    }
                    string img = null;
                    if (!string.IsNullOrEmpty(media))
                        img = media.StartsWith("inbox:") ? "api/inbox-file.ashx?k=" + Uri.EscapeDataString(media.Substring(6)) : "api/inbox.ashx?action=media&chan=" + Uri.EscapeDataString(chan) + "&id=" + id;
                    if (img != null && (body == WaImages.Placeholder || body == "[صورة من المتجر]")) body = "";
                    rows.Add(new Dictionary<string, object> { { "id", id }, { "at", Iso((DateTime)r["CreatedAt"]) }, { "who", who }, { "by", by }, { "text", body }, { "image", img } });
                }
            more = after <= 0 && rows.Count > 60;
            if (rows.Count > 60) rows.RemoveAt(rows.Count - 1);
            if (after <= 0) rows.Reverse();
            return rows;
        }

        public static List<Dictionary<string, object>> Notes(SqlConnection c, int storeId, string chan, string chat, long afterNote, Dictionary<int, string> names)
        {
            var l = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 200 Id, StaffId, Text, CreatedAt FROM dbo.InboxNotes WHERE StoreId=@s AND Chan=@ch AND ChatId=@c AND Id > @a ORDER BY Id",
                "@s", storeId, "@ch", chan, "@c", chat, "@a", afterNote))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    int sid = (int)r["StaffId"];
                    l.Add(new Dictionary<string, object> { { "id", (long)r["Id"] }, { "at", Iso((DateTime)r["CreatedAt"]) }, { "by", names.ContainsKey(sid) ? names[sid] : "حد من الفريق" }, { "byId", sid }, { "text", (string)r["Text"] } });
                }
            return l;
        }

        // ---------- answering ----------
        // null when it went; otherwise why it didn't (and it isn't kept)
        public static async Task<string> Send(Who w, string chan, string chat, string text, byte[] image, string mime, string origin)
        {
            var s = w.Store; Chat x; int line; bool wa = IsWa(chan, out line);
            text = (text ?? "").Trim();
            if (text.Length == 0 && image == null) return "اكتب رسالة الأول.";
            if (text.Length > 3000) return "الرسالة طويلة أوي (أكتر من 3000 حرف).";
            string key = null, saved = text.Length == 0 ? "[صورة من المتجر]" : wa ? Wapilot.Format(text) : MetaBot.Plain(text);
            string msgId = TeamPrefix + w.Id + "-" + WhatsAppBot.NewKey().Substring(0, 10);
            long? row;
            using (var c = Db.Open())
            {
                x = One(c, s.Id, chan, chat);
                if (x == null) return "المحادثة دي مش موجودة.";
                if ((chan == MetaApp.Fb || chan == MetaApp.Ig) && (!x.LastCustomerAt.HasValue || DateTime.UtcNow - x.LastCustomerAt.Value > TimeSpan.FromHours(24)))
                    return "فات أكتر من 24 ساعة من آخر رسالة من الزبون، وفيسبوك مش بيسمح بالرد بعدها. الزبون لازم يكتب الأول.";
                if (image != null) key = PutFile(c, s.Id, image, mime);
                // kept first: the channel may echo it back before the send returns, and the assistant steps aside at once
                row = wa ? WhatsAppBot.Save(c, s.Id, chat, false, saved, msgId, key == null ? null : "inbox:" + key, line)
                         : MetaBot.Save(c, s.Id, chan, chat, false, saved, msgId, key == null ? null : "inbox:" + key);
                if (row == null) return "الرسالة دي اتبعتت قبل كده.";
                // an employee who answers an unassigned chat takes it
                if (!w.Owner && !x.Assigned.HasValue)
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxChats SET AssignedTo=@me WHERE StoreId=@s AND Chan=@ch AND ChatId=@c AND AssignedTo IS NULL", "@me", w.Id, "@s", s.Id, "@ch", chan, "@c", chat)) cmd.ExecuteNonQuery();
            }
            string why;
            try
            {
                if (wa) why = await ToWhatsApp(s, line, chat, text, image, mime, msgId);
                else if (chan == Tg.Ch) why = await ToTelegram(s, chat, text, image, mime);
                else why = await ToMeta(s, chan, chat, text, key == null ? null : (origin ?? "").TrimEnd('/') + "/api/inbox-file.ashx?k=" + key);
            }
            catch (Exception ex) { Log.Error(ex); why = "حصلت مشكلة وإحنا بنبعتها."; }
            if (why != null)
                using (var c = Db.Open())
                {
                    // as if it was never written: the message goes, and the chat shows what it showed before
                    using (var cmd = Db.Cmd(c, "DELETE FROM dbo." + (wa ? "WaMessages" : "MetaMessages") + " WHERE Id=@id", "@id", row.Value)) cmd.ExecuteNonQuery();
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxChats SET LastText=@t, LastFrom=@f, LastAt=@at, Unread=@u, NeedsHuman=@n WHERE StoreId=@s AND Chan=@ch AND ChatId=@c",
                        "@t", (object)x.LastText ?? DBNull.Value, "@f", x.LastFrom, "@at", x.LastAt, "@u", x.Unread, "@n", x.Needs, "@s", s.Id, "@ch", chan, "@c", chat)) cmd.ExecuteNonQuery();
                }
            return why;
        }

        static async Task<string> ToWhatsApp(StoreInfo s, int line, string chatKey, string text, byte[] image, string mime, string idem)
        {
            string token, from;
            using (var c = Db.Open())
            {
                if (!WaLines.Works(c, s, line)) return "رقم الواتساب ده مش شغال في اشتراك الواتساب دلوقتي.";
                token = WaAccount.Token(c, s); from = WaLines.InstanceOf(c, s, line);
            }
            if (token == null || string.IsNullOrEmpty(from)) return "رقم الواتساب مش مربوط دلوقتي.";
            string chat = WaChat.Digits(chatKey) != null ? WaChat.Digits(chatKey) + "@c.us" : chatKey;
            try
            {
                if (image != null) { await Wapilot.SendImage(token, from, chat, image, mime, text.Length > 0 ? Wapilot.Format(text) : null, "sb-in-" + idem); return null; }
                int i = 0;
                foreach (var part in Wapilot.Split(Wapilot.Format(text))) await Wapilot.Send(token, from, chat, part, "sb-in-" + idem + "-" + (i++));
                return null;
            }
            catch (WaException ex) { return ex.Message; }
        }

        static async Task<string> ToTelegram(StoreInfo s, string chat, string text, byte[] image, string mime)
        {
            TgLink t; using (var c = Db.Open()) t = Tg.Link(c, s.Id);
            if (t == null || t.Token == null) return "بوت التيليجرام مش مربوط دلوقتي.";
            try
            {
                string plain = MetaBot.Plain(text);
                if (image != null) await Tg.SendPhoto(t.Token, chat, image, mime, plain.Length > 0 && plain.Length <= 1000 ? plain : null);
                if (plain.Length > 0 && (image == null || plain.Length > 1000)) await TgBot.Send(t.Token, chat, plain);
                return null;
            }
            catch (TgException ex) { return ex.Message; }
        }

        // a picture goes by its address here (Meta fetches it from us)
        static async Task<string> ToMeta(StoreInfo s, string ch, string chat, string text, string imageUrl)
        {
            MetaLink link; MetaCreds k;
            using (var c = Db.Open()) { link = MetaApp.Link(c, s.Id, ch); k = link == null ? null : MetaApp.ForLink(c, link); }
            if (link == null || link.Token == null || k == null) return MetaApp.Label(ch) + " مش مربوط دلوقتي.";
            try
            {
                if (imageUrl != null)
                {
                    object msg = new { attachment = new { type = "image", payload = new { url = imageUrl, is_reusable = false } } };
                    object body = ch == MetaApp.Fb ? (object)new { recipient = new { id = chat }, messaging_type = "RESPONSE", message = msg } : new { recipient = new { id = chat }, message = msg };
                    await MetaApp.Call(HttpMethod.Post, Uri.EscapeDataString(link.AccountId) + "/messages", link.Token, k.Secret, body);
                }
                if (text.Length > 0) await MetaBot.Send(link, chat, MetaBot.Plain(text), k.Secret);
                return null;
            }
            catch (MetaException ex) { return ex.Message; }
        }

        // ---------- pictures ----------
        // what the team sends: kept 30 days, readable by its random key (Meta fetches it by that address)
        static string PutFile(SqlConnection c, int storeId, byte[] data, string mime)
        {
            string key = WhatsAppBot.NewKey().Substring(0, 32);
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.InboxFiles(K, StoreId, Mime, Data) VALUES(@k, @s, @m, @d)", "@k", key, "@s", storeId, "@m", mime, "@d", data)) cmd.ExecuteNonQuery();
            if (new Random().Next(50) == 0) using (var cmd = Db.Cmd(c, "DELETE TOP (200) FROM dbo.InboxFiles WHERE CreatedAt < DATEADD(DAY,-30,SYSUTCDATETIME())")) cmd.ExecuteNonQuery();
            return key;
        }
        public static Tuple<byte[], string> GetFile(SqlConnection c, string key)
        {
            using (var cmd = Db.Cmd(c, "SELECT Data, Mime FROM dbo.InboxFiles WHERE K=@k", "@k", key))
            using (var r = cmd.ExecuteReader()) return r.Read() ? Tuple.Create((byte[])r[0], (string)r[1]) : null;
        }

        public static string Sniff(byte[] b)
        {
            if (b == null) return null;
            if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
            if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
            if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
            return null;
        }

        // A customer's picture, fetched for the team: from Telegram (tg:<file_id>), Wapilot (may want its token) or Meta's CDN
        public static async Task<Tuple<byte[], string>> Picture(StoreInfo s, string chan, long rowId)
        {
            string media; int line; bool wa = IsWa(chan, out line);
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, wa ? "SELECT Media FROM dbo.WaMessages WHERE Id=@id AND StoreId=@s" : "SELECT Media FROM dbo.MetaMessages WHERE Id=@id AND StoreId=@s AND Channel=@ch",
                "@id", rowId, "@s", s.Id, "@ch", chan)) media = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(media)) return null;
            try
            {
                byte[] bytes = null; string mime = null;
                if (media.StartsWith("tg:"))
                {
                    TgLink t; using (var c = Db.Open()) t = Tg.Link(c, s.Id);
                    if (t == null || t.Token == null) return null;
                    bytes = await Tg.Download(t.Token, media.Substring(3));
                }
                else
                {
                    if (!WaImages.SafeUrl(media)) return null;
                    string token = null;
                    if (wa) using (var c = Db.Open()) token = WaAccount.Token(c, s);
                    for (int attempt = 0; attempt < 2 && bytes == null; attempt++)
                        using (var req = new HttpRequestMessage(HttpMethod.Get, media))
                        {
                            if (attempt == 1) { if (string.IsNullOrEmpty(token)) break; req.Headers.TryAddWithoutValidation("token", token); req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token); }
                            using (var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                            {
                                if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxImage) continue;
                                mime = res.Content.Headers.ContentType != null ? res.Content.Headers.ContentType.MediaType : null;
                                bytes = await res.Content.ReadAsByteArrayAsync();
                            }
                        }
                }
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImage) return null;
                mime = Sniff(bytes) ?? mime;
                return mime != null && mime.StartsWith("image/") ? Tuple.Create(bytes, mime) : null;
            }
            catch (Exception ex) { Log.Error(new Exception("Inbox picture: " + ex.GetType().Name)); return null; }
        }
    }

    // api/inbox.ashx — SellyChat (inbox.html) and the dashboard's «💬 محادثاتي». The owner (dashboard sign-in) or an employee (rc_staff).
    //   GET  ?action=me | list (view, chan, q, before) | chat (chan, chat, after, before, note) | media (chan, id) | snippets | team (owner)
    //   POST ?action=login {email, password} | logout | send {chan, chat, text} or multipart (+ image) | note | assign {to} | status {status}
    //        | bot {off} | read | snippet-save {id, title, body} | snippet-delete {id} | staff-add {name, email} | staff-reset {id} | staff-remove {id}
    public class InboxHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
            try
            {
                if (action == "login" || action == "logout")
                {
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (action == "logout") { Inbox.StaffSignOut(ctx); Web.Json(res, 200, new { ok = true }); return; }
                    var lb = Web.Body(req, 1000);
                    string email = (Web.Str(lb, "email", 150) ?? "").Trim().ToLowerInvariant(), pw = Web.Str(lb, "password", 200) ?? "";
                    if (!Web.Allow("inbox-login:" + Web.Ip(req), 10, TimeSpan.FromMinutes(15)) || !Web.Allow("inbox-login:" + email, 10, TimeSpan.FromMinutes(15)))
                    { Web.Error(res, 429, "rate_limited", "محاولات كتير، استنى ربع ساعة وجرّب تاني."); return; }
                    using (var c = Db.Open())
                    {
                        var st = Inbox.Login(c, email, pw);
                        if (st == null) { Web.Error(res, 401, "bad_login", "الإيميل أو الباسورد غلط."); return; }
                        var s = Db.StoreById(c, st.StoreId);
                        if (s == null || !Inbox.On(c, s)) { Web.Error(res, 403, "closed", Inbox.Brand + " مش متاح لمتجرك دلوقتي. كلّم صاحب المتجر."); return; }
                        Inbox.StaffSignIn(ctx, st);
                        Web.Json(res, 200, MeJson(c, new Inbox.Who { Store = s, Staff = st }));
                    }
                    return;
                }

                Inbox.Who w;
                using (var c = Db.Open())
                {
                    w = Inbox.Identify(ctx, c);
                    if (w == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (!Inbox.On(c, w.Store))
                    {
                        if (w.Owner && !string.IsNullOrEmpty(Billing.Get(c, "MinPlan_" + Inbox.Feature))) { PlanGate.Deny(res, c, w.Store, Inbox.Feature); return; }
                        Web.Error(res, 403, "closed", Inbox.Brand + " مش متاح لمتجرك دلوقتي."); return;
                    }
                }
                if (req.HttpMethod == "GET") { await Get(ctx, w, action); return; }
                if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                await Post(ctx, w, action);
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }

        static Dictionary<string, object> MeJson(System.Data.SqlClient.SqlConnection c, Inbox.Who w)
        {
            var team = Inbox.Team(c, w.Store.Id);
            return new Dictionary<string, object> {
                { "id", w.Id }, { "name", w.Owner ? "صاحب المتجر" : w.Staff.Name }, { "owner", w.Owner }, { "store", w.Store.Name }, { "slug", w.Store.Slug },
                { "people", new[] { new Dictionary<string, object> { { "id", 0 }, { "name", "صاحب المتجر" } } }.Concat(team.Select(t => new Dictionary<string, object> { { "id", t.Id }, { "name", t.Name } })).ToArray() } };
        }

        static string Chan(HttpRequest req, Dictionary<string, object> b = null)
        {
            var v = (b != null ? Web.Str(b, "chan", 10) : null) ?? req.QueryString["chan"] ?? req.Form["chan"] ?? "";
            if (!Inbox.Valid(v)) throw new BadRequestException("القناة مش مفهومة.");
            return v;
        }
        static string ChatId(HttpRequest req, Dictionary<string, object> b = null)
        {
            var v = (b != null ? Web.Str(b, "chat", 100) : null) ?? req.QueryString["chat"] ?? req.Form["chat"] ?? "";
            if (v.Length == 0 || v.Length > 100) throw new BadRequestException("المحادثة مش مفهومة.");
            return v;
        }

        static async Task Get(HttpContext ctx, Inbox.Who w, string action)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (action == "media")
            {
                long id; if (!long.TryParse(req.QueryString["id"] ?? "", out id)) { res.StatusCode = 404; return; }
                var pic = await Inbox.Picture(w.Store, Chan(req), id);
                if (pic == null) { res.StatusCode = 404; return; }
                res.Cache.SetCacheability(HttpCacheability.Private); res.Cache.SetMaxAge(TimeSpan.FromDays(1));
                res.ContentType = pic.Item2; res.BinaryWrite(pic.Item1);
                return;
            }
            using (var c = Db.Open())
            {
                var names = Inbox.Names(c, w.Store.Id);
                if (action == "me" || action == "") { Web.Json(res, 200, MeJson(c, w)); return; }
                if (action == "list")
                {
                    DateTime b; DateTime? before = DateTime.TryParse(req.QueryString["before"] ?? "", null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out b) ? b : (DateTime?)null;
                    var list = Inbox.List(c, w, (req.QueryString["view"] ?? "waiting").ToLowerInvariant(), req.QueryString["chan"], req.QueryString["q"], before);
                    Web.Json(res, 200, new Dictionary<string, object> { { "chats", list.Select(x => Inbox.ChatJson(c, w.Store, x, names)).ToArray() }, { "counts", Inbox.Counts(c, w) }, { "more", list.Count == 40 } });
                    return;
                }
                if (action == "chat")
                {
                    string chan = Chan(req), chat = ChatId(req);
                    var x = Inbox.One(c, w.Store.Id, chan, chat);
                    if (x == null) { Web.Error(res, 404, "not_found", "المحادثة دي مش موجودة."); return; }
                    long after, before, note; long.TryParse(req.QueryString["after"] ?? "0", out after); long.TryParse(req.QueryString["before"] ?? "0", out before); long.TryParse(req.QueryString["note"] ?? "0", out note);
                    bool more;
                    var msgs = Inbox.Thread(c, w.Store, chan, chat, after, before, names, out more);
                    // the team has seen it (the page says so only while it's open in front of someone)
                    if (req.QueryString["seen"] == "1" && x.Unread > 0)
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxChats SET Unread=0 WHERE StoreId=@s AND Chan=@ch AND ChatId=@c", "@s", w.Store.Id, "@ch", chan, "@c", chat)) cmd.ExecuteNonQuery();
                    var d = Inbox.ChatJson(c, w.Store, x, names);
                    Web.Json(res, 200, new Dictionary<string, object> { { "chat", d }, { "messages", msgs }, { "notes", before > 0 ? new List<Dictionary<string, object>>() : Inbox.Notes(c, w.Store.Id, chan, chat, note, names) }, { "more", more } });
                    return;
                }
                if (action == "snippets")
                {
                    var l = new List<Dictionary<string, object>>();
                    using (var cmd = Db.Cmd(c, "SELECT Id, Title, Body FROM dbo.InboxSnippets WHERE StoreId=@s ORDER BY Title", "@s", w.Store.Id))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(new Dictionary<string, object> { { "id", r[0] }, { "title", r[1] }, { "body", r[2] } });
                    Web.Json(res, 200, new { snippets = l });
                    return;
                }
                if (action == "team" && w.Owner) { Web.Json(res, 200, Team(c, w.Store)); return; }
            }
            Web.Error(res, 400, "bad_request");
        }

        public static Dictionary<string, object> Team(System.Data.SqlClient.SqlConnection c, StoreInfo s)
        {
            var w = new Inbox.Who { Store = s };
            return new Dictionary<string, object> {
                { "on", Inbox.On(c, s) }, { "seats", Inbox.Seats(c) }, { "counts", Inbox.Counts(c, w) },
                { "staff", Inbox.Team(c, s.Id).Select(t => new Dictionary<string, object> { { "id", t.Id }, { "name", t.Name }, { "email", t.Email }, { "createdAt", Inbox.Iso(t.CreatedAt) }, { "lastAt", t.LastAt.HasValue ? Inbox.Iso(t.LastAt.Value) : null } }).ToArray() } };
        }

        static async Task Post(HttpContext ctx, Inbox.Who w, string action)
        {
            var req = ctx.Request; var res = ctx.Response;
            var s = w.Store;
            if (action == "send")
            {
                if (!Web.Allow("inbox-send:" + s.Id, 600, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "بعتّوا رسايل كتير أوي في ساعة. استنوا شوية."); return; }
                string chan, chat, text; byte[] image = null; string mime = null;
                if ((req.ContentType ?? "").StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                {
                    chan = Chan(req); chat = ChatId(req); text = req.Form["text"] ?? "";
                    var f = req.Files["image"];
                    if (f != null && f.ContentLength > 0)
                    {
                        if (f.ContentLength > 5 * 1024 * 1024) { Web.Error(res, 400, "too_big", "الصورة أكبر من 5 ميجا."); return; }
                        using (var ms = new System.IO.MemoryStream()) { f.InputStream.CopyTo(ms); image = ms.ToArray(); }
                        mime = Inbox.Sniff(image);
                        if (mime == null) { Web.Error(res, 400, "not_image", "ده مش صورة. ابعت JPG أو PNG أو WEBP."); return; }
                    }
                }
                else { var b = Web.Body(req, 20000); chan = Chan(req, b); chat = ChatId(req, b); text = Web.Str(b, "text", 3500) ?? ""; }
                var why = await Inbox.Send(w, chan, chat, text, image, mime, WaAccount.Origin(req));
                if (why != null) { Web.Error(res, 400, "not_sent", why); return; }
                Web.Json(res, 200, new { ok = true });
                return;
            }
            var body = Web.Body(req, 6000);
            using (var c = Db.Open())
            {
                if (action == "note" || action == "assign" || action == "status" || action == "bot" || action == "read")
                {
                    string chan = Chan(req, body), chat = ChatId(req, body);
                    if (Inbox.One(c, s.Id, chan, chat) == null) { Web.Error(res, 404, "not_found", "المحادثة دي مش موجودة."); return; }
                    string sql = null; object v = null;
                    if (action == "note")
                    {
                        string text = (Web.Str(body, "text", 1000) ?? "").Trim();
                        if (text.Length == 0) { Web.Error(res, 400, "empty", "اكتب الملاحظة الأول."); return; }
                        using (var cmd = Db.Cmd(c, "INSERT INTO dbo.InboxNotes(StoreId, Chan, ChatId, StaffId, Text) VALUES(@s, @ch, @c, @me, @t)", "@s", s.Id, "@ch", chan, "@c", chat, "@me", w.Id, "@t", text)) cmd.ExecuteNonQuery();
                    }
                    else if (action == "assign")
                    {
                        int to = Web.Int(body, "to") ?? -1;
                        if (to >= 0 && !Inbox.Names(c, s.Id).ContainsKey(to)) { Web.Error(res, 400, "bad_request", "الشخص ده مش في الفريق."); return; }
                        sql = "AssignedTo=@v"; v = to >= 0 ? (object)to : DBNull.Value;
                    }
                    else if (action == "status") { sql = "Status=@v, NeedsHuman=CASE WHEN @v='done' THEN 0 ELSE NeedsHuman END, Unread=CASE WHEN @v='done' THEN 0 ELSE Unread END"; v = Web.Str(body, "status", 10) == "done" ? "done" : "open"; }
                    else if (action == "bot") { object off; sql = "BotOff=@v"; v = body.TryGetValue("off", out off) && off is bool && (bool)off; }
                    else { sql = "Unread=0"; }
                    if (sql != null)
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxChats SET " + sql + " WHERE StoreId=@s AND Chan=@ch AND ChatId=@c", "@v", v ?? DBNull.Value, "@s", s.Id, "@ch", chan, "@c", chat)) cmd.ExecuteNonQuery();
                    var names = Inbox.Names(c, s.Id);
                    Web.Json(res, 200, new { chat = Inbox.ChatJson(c, s, Inbox.One(c, s.Id, chan, chat), names) });
                    return;
                }
                if (action == "snippet-save")
                {
                    string title = (Web.Str(body, "title", 40) ?? "").Trim(), text = (Web.Str(body, "body", 1000) ?? "").Trim();
                    int id = Web.Int(body, "id") ?? 0;
                    if (title.Length == 0 || text.Length == 0) { Web.Error(res, 400, "empty", "اكتب اسم للرد والكلام بتاعه."); return; }
                    if (id > 0) { using (var cmd = Db.Cmd(c, "UPDATE dbo.InboxSnippets SET Title=@t, Body=@b WHERE Id=@id AND StoreId=@s", "@t", title, "@b", text, "@id", id, "@s", s.Id)) cmd.ExecuteNonQuery(); }
                    else
                    {
                        using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.InboxSnippets WHERE StoreId=@s", "@s", s.Id)) if ((int)cmd.ExecuteScalar() >= 100) { Web.Error(res, 400, "full", "وصلت لـ 100 رد جاهز. امسح حاجة الأول."); return; }
                        using (var cmd = Db.Cmd(c, "INSERT INTO dbo.InboxSnippets(StoreId, Title, Body) VALUES(@s, @t, @b)", "@s", s.Id, "@t", title, "@b", text)) cmd.ExecuteNonQuery();
                    }
                    Web.Json(res, 200, new { ok = true });
                    return;
                }
                if (action == "snippet-delete")
                {
                    using (var cmd = Db.Cmd(c, "DELETE FROM dbo.InboxSnippets WHERE Id=@id AND StoreId=@s", "@id", Web.Int(body, "id") ?? 0, "@s", s.Id)) cmd.ExecuteNonQuery();
                    Web.Json(res, 200, new { ok = true });
                    return;
                }
                // the team: the owner only
                if (action.StartsWith("staff-"))
                {
                    if (!w.Owner) { Web.Error(res, 403, "owner_only", "صاحب المتجر بس اللي يقدر يغيّر الفريق."); return; }
                    if (!Web.Allow("inbox-team:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية."); return; }
                    var d = new Dictionary<string, object>();
                    if (action == "staff-add")
                    {
                        string name = (Web.Str(body, "name", 60) ?? "").Trim(), email = (Web.Str(body, "email", 150) ?? "").Trim().ToLowerInvariant();
                        if (name.Length < 2) { Web.Error(res, 400, "bad_name", "اكتب اسم الموظف."); return; }
                        if (!Web.ValidEmail(email)) { Web.Error(res, 400, "bad_email", "الإيميل شكله مش مظبوط."); return; }
                        d["password"] = Inbox.AddStaff(c, s, name, email); d["for"] = email;
                    }
                    else if (action == "staff-reset")
                    {
                        int id = Web.Int(body, "id") ?? 0;
                        d["password"] = Inbox.ResetStaff(c, s.Id, id);
                        d["for"] = Inbox.Team(c, s.Id).Where(t => t.Id == id).Select(t => t.Email).FirstOrDefault();
                    }
                    else if (action == "staff-remove") Inbox.RemoveStaff(c, s.Id, Web.Int(body, "id") ?? 0);
                    else { Web.Error(res, 400, "bad_request"); return; }
                    foreach (var kv in Team(c, s)) d[kv.Key] = kv.Value;
                    Web.Json(res, 200, d);
                    return;
                }
            }
            Web.Error(res, 400, "bad_request");
        }
    }

    // api/inbox-file.ashx?k=<key> — a picture the team sent from SellyChat (Messenger / Instagram fetch it from here; the thread shows it)
    public class InboxFileHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var res = ctx.Response; string k = ctx.Request.QueryString["k"] ?? "";
            if (!Db.Configured || !Regex.IsMatch(k, "^[a-f0-9]{32}$")) { res.StatusCode = 404; return; }
            try
            {
                Tuple<byte[], string> f;
                using (var c = Db.Open()) f = Inbox.GetFile(c, k);
                if (f == null) { res.StatusCode = 404; return; }
                res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.FromDays(7));
                res.ContentType = f.Item2; res.BinaryWrite(f.Item1);
            }
            catch (Exception ex) { Log.Error(ex); res.StatusCode = 500; }
        }
    }

    // api/inbox-admin.ashx — the super admin's «💬 محادثاتي»: tester stores, employees per store, and who uses it.
    // GET = state; POST ?action=save {testers, seats}
    public class InboxAdminHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    if (!MetaAdminHandler.SignedIn(ctx, c)) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "POST")
                    {
                        if (!Web.CsrfOk(req) || (req.QueryString["action"] ?? "") != "save") { Web.Error(res, 400, "bad_request"); return; }
                        var b = Web.Body(req, 4000);
                        Billing.Set(c, "InboxTesters", string.Join(", ", (Web.Str(b, "testers", 1000) ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Distinct()));
                        Billing.Set(c, "InboxSeats", Math.Max(1, Math.Min(50, Web.Int(b, "seats") ?? 5)).ToString());
                    }
                    var stores = new List<Dictionary<string, object>>();
                    using (var cmd = Db.Cmd(c, @"SELECT TOP 300 s.Name, s.Slug, x.Chats, x.Waiting, x.LastAt, (SELECT COUNT(*) FROM dbo.InboxStaff t WHERE t.StoreId=s.Id) AS Staff
                        FROM (SELECT StoreId, COUNT(*) AS Chats, SUM(CASE WHEN Status='open' AND (LastFrom=1 OR NeedsHuman=1) THEN 1 ELSE 0 END) AS Waiting, MAX(LastAt) AS LastAt
                              FROM dbo.InboxChats GROUP BY StoreId) x JOIN dbo.Stores s ON s.Id = x.StoreId
                        WHERE EXISTS (SELECT 1 FROM dbo.InboxStaff t WHERE t.StoreId=s.Id) OR s.IsAdmin=1 OR @t LIKE '%' + s.Slug + '%'
                        ORDER BY x.LastAt DESC", "@t", "," + (Billing.Get(c, "InboxTesters") ?? "").Replace(" ", "") + ","))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            stores.Add(new Dictionary<string, object> { { "store", r["Name"] }, { "slug", r["Slug"] }, { "chats", r["Chats"] }, { "waiting", r["Waiting"] }, { "staff", r["Staff"] },
                                { "lastAt", r["LastAt"] is DateTime ? Inbox.Iso((DateTime)r["LastAt"]) : null } });
                    Web.Json(res, 200, new Dictionary<string, object> {
                        { "testers", Billing.Get(c, "InboxTesters") ?? "" }, { "seats", Inbox.Seats(c) }, { "minPlan", PlanGate.MinPlanId(c, Inbox.Feature) }, { "stores", stores } });
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }
    }
}
