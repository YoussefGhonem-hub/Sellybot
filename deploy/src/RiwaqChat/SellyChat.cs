// SellyChat («💬 محادثاتي»): one place where a store's owner and team read and answer its customers from WhatsApp, Messenger,
// Instagram and Telegram. The user asked for it on 2026-11-26 and picked the name.
//
// It is Chatwoot's community edition (MIT, github.com/chatwoot/chatwoot; the "-ce" image has no enterprise/ folder) on its own
// Linux server, with Sellybot's name and icon (deploy/sellychat/). Sellybot keeps every channel, so linking stays as it is
// (WhatsApp by QR through Wapilot, «ضيفنا على صفحتك», the store's bot) and nothing is paid to Meta:
// - Out: every message Sellybot keeps (dbo.WaMessages, dbo.MetaMessages) is copied to the store's SellyChat account, one "API"
//   inbox per channel (and per WhatsApp number). The customer's message is incoming. The assistant's (and every automatic
//   message) is outgoing from the account's agent bot «🤖 المساعد». What the owner answered from the phone or the Page is
//   outgoing from a second bot, «📱 من موبايلك أو صفحتك».
//   Each store has a cursor per table (ScLinks.WaFrom / MetaFrom), claimed in SQL before a message goes, so two worker processes
//   never copy the same one. Kick() runs right after a message is saved; the "sellychat" lane of Jobs catches up anything left.
// - In: when someone answers in SellyChat, Chatwoot posts it to api/sellychat-hook.ashx (the inbox's webhook; key in the URL,
//   and Chatwoot's X-Chatwoot-Signature checked with the inbox's secret). Sellybot sends it the way the chat came (the same
//   WhatsApp number, the Page / the Instagram account, the store's bot) and keeps it as the owner's message ("owner:cw-<id>").
//   So the assistant steps aside in that chat for WhatsAppBot.PauseHours, as when the owner answers from the phone. What
//   couldn't go out comes back as a private note in the conversation.
// - The site chat isn't in it: its messages aren't kept per visitor.
// Per store: an account (Platform API), the owner as its administrator, signing in from the dashboard with a one-time link
// (users/<id>/login); employees added from the dashboard as agents (their email, and a password we make, shown once). Chatwoot
// answers every webhook with "failed" on the message when our answer isn't 2xx, so the hook answers 200 to everything it understands.
// Settings (admin.html ← «SellyChat»): SellyChatUrl, SellyChatToken (a Platform App's token, kept protected), SellyChatTesters,
// SellyChatSeats (employees per store). SellyChatHookBase only for local tests (Chatwoot in Docker can't reach localhost).
// Plan gate "sellychat": closed until the admin picks the lowest plan or names tester stores (like Carriers.On).
// Built against Chatwoot v4.20.1-ce running locally (2026-11-26).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class ScException : Exception
    {
        public int Status;
        public ScException(string message, int status = 0) : base(message) { Status = status; }
        public bool Down { get { return Status == 0 || Status == 429 || Status >= 500 || Status == 401; } }   // try again later, the message isn't the problem
    }

    public class ScLink
    {
        public int StoreId, AccountId, OwnerUserId, BotId, PhoneBotId;
        public string OwnerToken, BotToken, PhoneBotToken, HookKey, Status, Origin;
        public long WaFrom, MetaFrom;
        public DateTime CreatedAt; public DateTime? LastAt;
    }

    public static class SellyChat
    {
        public const string Feature = "sellychat", Brand = "SellyChat";
        const int HistoryHours = 48, Batch = 40, MaxImage = 5 * 1024 * 1024;
        const string FromScPrefix = "owner:cw-";   // a message an agent wrote in SellyChat (it's there already)
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        static readonly HttpMethod Patch = new HttpMethod("PATCH");   // .NET Framework has no HttpMethod.Patch

        // ---------- settings, and who sees it ----------
        public static string Base(SqlConnection c) { var u = (Billing.Get(c, "SellyChatUrl") ?? "").Trim().TrimEnd('/'); return u.Length == 0 ? null : u; }
        static string PlatformToken(SqlConnection c) { var b = Billing.Get(c, "SellyChatToken"); return string.IsNullOrEmpty(b) ? null : Secrets.Unprotect(b); }
        public static bool Ready(SqlConnection c) { return Base(c) != null && PlatformToken(c) != null; }
        public static int Seats(SqlConnection c) { int n; return int.TryParse(Billing.Get(c, "SellyChatSeats") ?? "", out n) && n > 0 ? n : 5; }
        public static bool Tester(SqlConnection c, StoreInfo s) { return s != null && (Billing.Get(c, "SellyChatTesters") ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries).Contains(s.Slug, StringComparer.OrdinalIgnoreCase); }
        // unlike most features «no minimum» means closed: nothing shows until the admin picks the lowest plan (or names testers)
        public static bool On(SqlConnection c, StoreInfo s)
        {
            return s != null && Ready(c) && (s.IsAdmin || Tester(c, s) || (!string.IsNullOrEmpty(Billing.Get(c, "MinPlan_" + Feature)) && PlanGate.Allows(c, s, Feature)));
        }
        public static List<string> Hide(SqlConnection c, StoreInfo s, List<string> hidden) { if (!On(c, s) && !hidden.Contains(Feature)) hidden.Add(Feature); return hidden; }

        // the address Chatwoot posts the store's answers to
        static string HookUrl(ScLink l)
        {
            string b = (Web.Setting("SellyChatHookBase") ?? l.Origin ?? "https://sellybot.online").TrimEnd('/');
            return b + "/api/sellychat-hook.ashx?s=" + l.StoreId + "&k=" + l.HookKey;
        }

        // ---------- Chatwoot's API ----------
        // {base}{path} with the token in api_access_token; the JSON answer, or a ScException with a message an owner can read.
        // Nothing here logs a token.
        static async Task<JsonElement> Call(string baseUrl, string token, HttpMethod method, string path, object body = null, HttpContent content = null)
        {
            var req = new HttpRequestMessage(method, baseUrl + path);
            req.Headers.TryAddWithoutValidation("api_access_token", token);
            if (content != null) req.Content = content;
            else if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req); }
            catch (Exception ex)
            {
                Log.Error(new Exception("SellyChat " + method + " " + Shape(path) + ": " + ex.GetType().Name));
                throw new ScException("مقدرناش نوصل لـ " + Brand + " دلوقتي. جرّب تاني كمان شوية.");
            }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync();
                JsonElement root = default(JsonElement); bool parsed = false;
                try { using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)) { root = doc.RootElement.Clone(); parsed = true; } } catch { }
                if (resp.IsSuccessStatusCode) return parsed ? root : default(JsonElement);
                int code = (int)resp.StatusCode;
                string why = parsed && root.ValueKind == JsonValueKind.Object ? (WaJson.S(root, "message", "error") ?? "") : "";
                Log.Error(new Exception("SellyChat " + method + " " + Shape(path) + " -> " + code + " " + Cut(why, 200)));
                if (code == 401) throw new ScException("مفتاح " + Brand + " مش شغال. الأدمن محتاج يحطه تاني.", code);
                if (code == 404) throw new ScException(Brand + " مالقاش اللي اتطلب (اتمسح من هناك؟).", code);
                if (code == 429) throw new ScException(Brand + " بيقول الطلبات كتير، هنحاول تاني بعد شوية.", code);
                throw new ScException(Brand + " رفض الطلب" + (why.Length > 0 ? ": " + Cut(why, 160) : "") + " (" + code + ").", code);
            }
        }
        static string Shape(string path) { var q = path.IndexOf('?'); return Regex.Replace(q < 0 ? path : path.Substring(0, q), @"/\d+", "/#"); }
        static int Id(JsonElement e) { int n; return int.TryParse(WaJson.S(e, "id") ?? "", out n) ? n : 0; }
        static string Cut(string s, int n) { return MetaApp.Cut(s, n); }

        // ---------- the store's link ----------
        public static ScLink Link(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ScLinks WHERE StoreId=@s", "@s", storeId))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                return new ScLink
                {
                    StoreId = (int)r["StoreId"], AccountId = (int)r["AccountId"], OwnerUserId = (int)r["OwnerUserId"], BotId = (int)r["BotId"], PhoneBotId = (int)r["PhoneBotId"],
                    OwnerToken = Secrets.Unprotect((string)r["OwnerBox"]), BotToken = Secrets.Unprotect((string)r["BotBox"]), PhoneBotToken = Secrets.Unprotect((string)r["PhoneBotBox"]),
                    HookKey = (string)r["HookKey"], Status = r["Status"] as string, Origin = r["Origin"] as string, WaFrom = (long)r["WaFrom"], MetaFrom = (long)r["MetaFrom"],
                    CreatedAt = (DateTime)r["CreatedAt"], LastAt = r["LastAt"] as DateTime?
                };
            }
        }

        static void SetStatus(SqlConnection c, int storeId, string status)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ScLinks SET Status=@st, StatusAt=SYSUTCDATETIME()" + (status == null ? ", LastAt=SYSUTCDATETIME()" : "") + " WHERE StoreId=@s",
                "@st", (object)Cut(status, 300) ?? DBNull.Value, "@s", storeId)) cmd.ExecuteNonQuery();
        }

        // A password Chatwoot accepts (upper, lower, digit, symbol) that's still easy to read out: no 0/O, 1/l/I
        public static string NewPassword()
        {
            const string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", low = "abcdefghjkmnpqrstuvwxyz", dig = "23456789";
            var b = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder();
            for (int i = 0; i < 4; i++) sb.Append(up[b[i] % up.Length]);
            for (int i = 4; i < 8; i++) sb.Append(low[b[i] % low.Length]);
            sb.Append('-');
            for (int i = 8; i < 12; i++) sb.Append(dig[b[i] % dig.Length]);
            return sb.ToString();
        }

        // The owner's login name in SellyChat. Never their real email: they come in from the dashboard, and an email used by an
        // employee somewhere (one installation, emails unique across it) can't stop a store from opening its account.
        static string OwnerLogin(SqlConnection c, int storeId)
        {
            var host = new Uri(Base(c)).Host;
            return "store" + storeId + "@" + (host.Contains(".") ? host : host + ".local");   // Chatwoot wants a dot after the @
        }

        // ---------- opening and closing a store's account ----------
        // null when it's ready (or was already); otherwise what to tell the owner
        public static async Task<string> Start(int storeId, string origin)
        {
            StoreInfo s; string b, pt, login;
            using (var c = Db.Open())
            {
                if (Link(c, storeId) != null) return null;
                s = Db.StoreById(c, storeId); b = Base(c); pt = PlatformToken(c);
                if (s == null || b == null || pt == null) return Brand + " مش متاح دلوقتي.";
                login = OwnerLogin(c, storeId);
            }
            int account = 0, user = 0;
            try
            {
                var acc = await Call(b, pt, HttpMethod.Post, "/platform/api/v1/accounts", new { name = Cut(s.Name, 100), locale = "ar" });
                account = Id(acc);
                // only what Sellybot's stores use: no inboxes, help center or integrations of their own (the channels are Sellybot's)
                try { await Call(b, pt, Patch, "/platform/api/v1/accounts/" + account, new { features = Features() }); } catch (ScException ex) { Log.Error(ex); }
                var u = await Call(b, pt, HttpMethod.Post, "/platform/api/v1/users", new { name = Cut(s.Name, 100), display_name = Cut(s.Name, 100), email = login, password = NewPassword(), custom_attributes = new { sellybot_store = storeId } });
                user = Id(u);
                string ownerToken = WaJson.S(u, "access_token");
                await Call(b, pt, HttpMethod.Post, "/platform/api/v1/accounts/" + account + "/account_users", new { user_id = user, role = "administrator" });
                var bot = await Call(b, ownerToken, HttpMethod.Post, "/api/v1/accounts/" + account + "/agent_bots", new { name = "🤖 المساعد", description = "مساعد " + Cut(s.Name, 80) + " على Sellybot: ردوده بتظهر هنا." });
                var phone = await Call(b, ownerToken, HttpMethod.Post, "/api/v1/accounts/" + account + "/agent_bots", new { name = "📱 من موبايلك أو صفحتك", description = "اللي اتكتب للزبون من برّه " + Brand + ": من موبايل المتجر، أو من صفحة فيسبوك وإنستجرام." });
                string botToken = Token(bot), phoneToken = Token(phone);
                if (account == 0 || user == 0 || string.IsNullOrEmpty(ownerToken) || string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(phoneToken))
                    throw new ScException(Brand + " مارجّعش بيانات الحساب كاملة.");
                using (var c = Db.Open())
                {
                    // what happened in the last two days shows up there too
                    using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.ScLinks(StoreId, AccountId, OwnerUserId, OwnerBox, BotId, BotBox, PhoneBotId, PhoneBotBox, HookKey, Origin, WaFrom, MetaFrom)
                        VALUES(@s, @a, @u, @ob, @b, @bb, @p, @pb, @k, @o,
                          ISNULL((SELECT MAX(Id) FROM dbo.WaMessages WHERE StoreId=@s AND CreatedAt < DATEADD(HOUR,-@h,SYSUTCDATETIME())), 0),
                          ISNULL((SELECT MAX(Id) FROM dbo.MetaMessages WHERE StoreId=@s AND CreatedAt < DATEADD(HOUR,-@h,SYSUTCDATETIME())), 0))",
                        "@s", storeId, "@a", account, "@u", user, "@ob", Secrets.Protect(ownerToken), "@b", Id(bot), "@bb", Secrets.Protect(botToken),
                        "@p", Id(phone), "@pb", Secrets.Protect(phoneToken), "@k", WhatsAppBot.NewKey().Substring(0, 40), "@o", (object)Cut(origin, 200) ?? DBNull.Value, "@h", HistoryHours)) cmd.ExecuteNonQuery();
                }
                Forget();
                Kick(storeId);
                return null;
            }
            catch (ScException ex)
            {
                // nothing half made stays there
                if (account > 0) try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/accounts/" + account); } catch (ScException) { }
                if (user > 0) try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/users/" + user); } catch (ScException) { }
                return ex.Message;
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                // two presses at once: the other one made it
                try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/accounts/" + account); } catch (ScException) { }
                try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/users/" + user); } catch (ScException) { }
                return null;
            }
        }

        // an account bot's token: a string, or { token } on some versions
        static string Token(JsonElement bot)
        {
            JsonElement t;
            if (bot.ValueKind != JsonValueKind.Object || !bot.TryGetProperty("access_token", out t)) return null;
            return t.ValueKind == JsonValueKind.String ? t.GetString() : t.ValueKind == JsonValueKind.Object ? WaJson.S(t, "token") : null;
        }

        static Dictionary<string, bool> Features()
        {
            var off = new[] { "inbound_emails", "channel_email", "channel_facebook", "channel_website", "channel_instagram", "channel_tiktok", "help_center",
                "integrations", "campaigns", "captain_tasks", "contact_chatwoot_support_team", "data_import", "mobile_voice_push", "inbox_management", "api_and_webhooks" };
            var on = new[] { "agent_management", "team_management", "labels", "custom_attributes", "canned_responses", "reports", "crm", "macros", "automations",
                "voice_recorder", "auto_resolve_conversations", "agent_bots", "chatwoot_v4", "assignment_v2", "companies" };
            var d = new Dictionary<string, bool>();
            foreach (var f in off) d[f] = false;
            foreach (var f in on) d[f] = true;
            return d;
        }

        // The owner closes it: the account, everyone in it and what we kept go. The messages stay in Sellybot.
        public static async Task<string> Stop(int storeId)
        {
            ScLink l; string b, pt; var users = new List<int>();
            using (var c = Db.Open())
            {
                l = Link(c, storeId); b = Base(c); pt = PlatformToken(c);
                if (l == null) return null;
                using (var cmd = Db.Cmd(c, "SELECT UserId FROM dbo.ScAgents WHERE StoreId=@s", "@s", storeId))
                using (var r = cmd.ExecuteReader()) while (r.Read()) users.Add((int)r[0]);
            }
            if (b != null && pt != null)
            {
                try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/accounts/" + l.AccountId); }
                catch (ScException ex) when (ex.Status != 404) { return ex.Message; }
                foreach (var u in users.Concat(new[] { l.OwnerUserId }))
                    try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/users/" + u); } catch (ScException) { }
            }
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.ScConvs WHERE StoreId=@s; DELETE FROM dbo.ScInboxes WHERE StoreId=@s; DELETE FROM dbo.ScAgents WHERE StoreId=@s;
                DELETE FROM dbo.ScLinks WHERE StoreId=@s;", "@s", storeId)) cmd.ExecuteNonQuery();
            Forget();
            return null;
        }

        // A one-time link that opens SellyChat signed in as the owner
        public static async Task<string> LoginUrl(int storeId)
        {
            ScLink l; string b, pt;
            using (var c = Db.Open()) { l = Link(c, storeId); b = Base(c); pt = PlatformToken(c); }
            if (l == null || b == null || pt == null) throw new ScException(Brand + " مش متاح دلوقتي.");
            var r = await Call(b, pt, HttpMethod.Get, "/platform/api/v1/users/" + l.OwnerUserId + "/login");
            var url = WaJson.S(r, "url");
            if (string.IsNullOrEmpty(url)) throw new ScException(Brand + " مارجّعش لينك الدخول.");
            return url;
        }

        // ---------- the team ----------
        public class Agent { public int UserId; public string Name, Email; public DateTime CreatedAt; }

        public static List<Agent> Agents(SqlConnection c, int storeId)
        {
            var list = new List<Agent>();
            using (var cmd = Db.Cmd(c, "SELECT UserId, Name, Email, CreatedAt FROM dbo.ScAgents WHERE StoreId=@s ORDER BY CreatedAt", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new Agent { UserId = (int)r[0], Name = (string)r[1], Email = (string)r[2], CreatedAt = (DateTime)r[3] });
            return list;
        }

        // the employee's password (shown to the owner once), or a ScException
        public static async Task<string> AddAgent(int storeId, string name, string email)
        {
            ScLink l; string b, pt;
            using (var c = Db.Open())
            {
                l = Link(c, storeId); b = Base(c); pt = PlatformToken(c);
                if (l == null || b == null || pt == null) throw new ScException(Brand + " مش متاح دلوقتي.");
                if (Agents(c, storeId).Count >= Seats(c)) throw new ScException("وصلت لأكبر عدد موظفين (" + Seats(c) + "). امسح موظف الأول، أو كلّم الدعم لو محتاج أكتر.");
            }
            // One installation, emails unique across it: Chatwoot's Platform API hands back the existing user for an email it knows
            // (ours: another store's employee) or refuses (anyone else's). Either way it's not a new employee of this store.
            var taken = new ScException("الإيميل ده مستخدم في " + Brand + " قبل كده. استخدم إيميل تاني للموظف ده.", 422);
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ScAgents WHERE Email=@e", "@e", email))
                if ((int)cmd.ExecuteScalar() > 0) throw taken;
            string pw = NewPassword();
            JsonElement u;
            try { u = await Call(b, pt, HttpMethod.Post, "/platform/api/v1/users", new { name, display_name = name, email, password = pw, custom_attributes = new { sellybot_store = storeId } }); }
            catch (ScException ex) when (ex.Status == 422 || ex.Status == 401) { throw taken; }
            int user = Id(u);
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ScAgents WHERE UserId=@u", "@u", user))
                if (user == 0 || (int)cmd.ExecuteScalar() > 0 || WaJson.S(u, "email") != email) throw taken;
            try
            {
                await Call(b, pt, HttpMethod.Post, "/platform/api/v1/accounts/" + l.AccountId + "/account_users", new { user_id = user, role = "agent" });
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ScAgents(StoreId, UserId, Name, Email) VALUES(@s, @u, @n, @e)", "@s", storeId, "@u", user, "@n", name, "@e", email)) cmd.ExecuteNonQuery();
            }
            catch (Exception) { try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/users/" + user); } catch (ScException) { } throw; }
            await Members(b, l);
            return pw;
        }

        // An agent sees only the conversations of the inboxes they're in: every employee in every inbox of the store
        static async Task Members(string b, ScLink l, int onlyInbox = 0)
        {
            List<int> users, inboxes = new List<int>();
            using (var c = Db.Open())
            {
                users = Agents(c, l.StoreId).Select(a => a.UserId).ToList();
                using (var cmd = Db.Cmd(c, "SELECT InboxId FROM dbo.ScInboxes WHERE StoreId=@s", "@s", l.StoreId))
                using (var r = cmd.ExecuteReader()) while (r.Read()) inboxes.Add((int)r[0]);
            }
            if (users.Count == 0) return;
            foreach (var i in inboxes.Where(x => onlyInbox == 0 || x == onlyInbox))
                try { await Call(b, l.OwnerToken, Patch, "/api/v1/accounts/" + l.AccountId + "/inbox_members", new { inbox_id = i, user_ids = users }); }
                catch (ScException ex) { Log.Error(ex); }
        }

        public static async Task<string> ResetAgent(int storeId, int userId)
        {
            string b, pt;
            using (var c = Db.Open())
            {
                if (!Agents(c, storeId).Any(a => a.UserId == userId)) throw new ScException("الموظف ده مش موجود.");
                b = Base(c); pt = PlatformToken(c);
            }
            string pw = NewPassword();
            await Call(b, pt, Patch, "/platform/api/v1/users/" + userId, new { password = pw });
            return pw;
        }

        public static async Task RemoveAgent(int storeId, int userId)
        {
            ScLink l; string b, pt;
            using (var c = Db.Open())
            {
                if (!Agents(c, storeId).Any(a => a.UserId == userId)) return;
                l = Link(c, storeId); b = Base(c); pt = PlatformToken(c);
            }
            if (l != null && b != null && pt != null)
            {
                try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/accounts/" + l.AccountId + "/account_users", new { user_id = userId }); } catch (ScException ex) when (ex.Status == 404) { }
                try { await Call(b, pt, HttpMethod.Delete, "/platform/api/v1/users/" + userId); } catch (ScException ex) when (ex.Status == 404) { }
            }
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, "DELETE FROM dbo.ScAgents WHERE StoreId=@s AND UserId=@u", "@s", storeId, "@u", userId)) cmd.ExecuteNonQuery();
        }

        // ---------- copying messages there ----------
        // Which stores have SellyChat: asked of the database once a minute, so Kick() costs nothing for every other store
        static HashSet<int> linked = new HashSet<int>();
        static DateTime linkedAt = DateTime.MinValue;
        static readonly object Gate = new object();
        static readonly Dictionary<int, bool> running = new Dictionary<int, bool>();   // store -> another round asked for while one runs

        static void Forget() { lock (Gate) linkedAt = DateTime.MinValue; }

        static bool Linked(int storeId)
        {
            bool stale;
            lock (Gate) stale = DateTime.UtcNow - linkedAt > TimeSpan.FromMinutes(1);
            if (stale)
                try
                {
                    var set = new HashSet<int>();
                    using (var c = Db.Open())
                    using (var cmd = Db.Cmd(c, "SELECT StoreId FROM dbo.ScLinks"))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) set.Add((int)r[0]);
                    lock (Gate) { linked = set; linkedAt = DateTime.UtcNow; }
                }
                catch (Exception ex) { Log.Error(ex); lock (Gate) linkedAt = DateTime.UtcNow; }
            lock (Gate) return linked.Contains(storeId);
        }

        // Right after a message is saved (WhatsAppBot.Save, MetaBot.Save): copy what's new, in the background
        public static void Kick(int storeId)
        {
            try
            {
                if (!Linked(storeId)) return;
                lock (Gate)
                {
                    if (running.ContainsKey(storeId)) { running[storeId] = true; return; }
                    running[storeId] = false;
                }
                HostingEnvironment.QueueBackgroundWorkItem(ct => Loop(storeId, ct));
            }
            catch (Exception ex) { Log.Error(ex); lock (Gate) running.Remove(storeId); }
        }

        static async Task Loop(int storeId, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(700, ct);   // a reply and its echo, or a few messages in a row, go together
                    await Sync(storeId, ct);
                    lock (Gate)
                    {
                        if (!running[storeId]) { running.Remove(storeId); return; }
                        running[storeId] = false;
                    }
                }
            }
            catch (OperationCanceledException) { lock (Gate) running.Remove(storeId); }
            catch (Exception ex) { Log.Error(ex); lock (Gate) running.Remove(storeId); }
        }

        // Jobs' "sellychat" lane: every store with SellyChat, in case a copy failed or the site restarted in the middle
        public static Task Run(CancellationToken ct)
        {
            var ids = new List<int>();
            using (var c = Db.Open())
            {
                if (!Ready(c)) return Task.CompletedTask;
                using (var cmd = Db.Cmd(c, @"SELECT l.StoreId FROM dbo.ScLinks l WHERE EXISTS (SELECT 1 FROM dbo.WaMessages m WHERE m.StoreId=l.StoreId AND m.Id > l.WaFrom)
                    OR EXISTS (SELECT 1 FROM dbo.MetaMessages m WHERE m.StoreId=l.StoreId AND m.Id > l.MetaFrom)"))
                using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((int)r[0]);
            }
            Forget();
            foreach (var id in ids) Kick(id);
            return Task.CompletedTask;
        }

        class Row { public long Id; public bool Wa, Customer; public int Line; public string Ch, Chat, Body, MsgId, Media; }

        static string Chan(Row r) { return r.Wa ? "wa" + r.Line : r.Ch; }

        static async Task Sync(int storeId, CancellationToken ct)
        {
            for (int round = 0; round < 25 && !ct.IsCancellationRequested; round++)
            {
                ScLink l; StoreInfo s; string b; var rows = new List<Row>();
                using (var c = Db.Open())
                {
                    l = Link(c, storeId); b = Base(c); s = Db.StoreById(c, storeId);
                    if (l == null || b == null || s == null) return;
                    if (!On(c, s)) { if (l.Status == null) SetStatus(c, storeId, "⚠️ " + Brand + " مش ضمن باقتك دلوقتي، فالرسايل الجديدة مش بتوصله."); return; }
                    using (var cmd = Db.Cmd(c, "SELECT TOP (@n) Id, ChatId, FromCustomer, Body, MsgId, Media, Line FROM dbo.WaMessages WHERE StoreId=@s AND Id > @f ORDER BY Id",
                        "@n", Batch, "@s", storeId, "@f", l.WaFrom))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) rows.Add(new Row { Id = (long)r["Id"], Wa = true, Chat = (string)r["ChatId"], Customer = (bool)r["FromCustomer"], Body = (string)r["Body"], MsgId = r["MsgId"] as string, Media = r["Media"] as string, Line = (int)r["Line"] });
                    using (var cmd = Db.Cmd(c, "SELECT TOP (@n) Id, Channel, ChatId, FromCustomer, Body, MsgId, Media FROM dbo.MetaMessages WHERE StoreId=@s AND Id > @f ORDER BY Id",
                        "@n", Batch, "@s", storeId, "@f", l.MetaFrom))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) rows.Add(new Row { Id = (long)r["Id"], Ch = (string)r["Channel"], Chat = (string)r["ChatId"], Customer = (bool)r["FromCustomer"], Body = (string)r["Body"], MsgId = r["MsgId"] as string, Media = r["Media"] as string });
                }
                if (rows.Count == 0) return;
                long wa = l.WaFrom, meta = l.MetaFrom;
                foreach (var row in rows)
                {
                    if (ct.IsCancellationRequested) return;
                    // claim it: another worker process that got here first moved the cursor, and this one stops
                    string col = row.Wa ? "WaFrom" : "MetaFrom"; long prev = row.Wa ? wa : meta;
                    using (var c = Db.Open())
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.ScLinks SET " + col + "=@id WHERE StoreId=@s AND " + col + "=@p", "@id", row.Id, "@s", storeId, "@p", prev))
                        if (cmd.ExecuteNonQuery() == 0) return;
                    if (row.Wa) wa = row.Id; else meta = row.Id;
                    if ((row.MsgId ?? "").StartsWith(FromScPrefix)) continue;   // written in SellyChat: it's there already
                    if (!row.Wa && row.Ch != MetaApp.Fb && row.Ch != MetaApp.Ig && row.Ch != Tg.Ch) continue;
                    try { await Post(b, l, s, row); }
                    catch (ScException ex) when (ex.Down)
                    {
                        // SellyChat is down or busy: give it back and try again later (the jobs lane or the next message)
                        using (var c = Db.Open())
                        {
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.ScLinks SET " + col + "=@p WHERE StoreId=@s AND " + col + "=@id", "@id", row.Id, "@s", storeId, "@p", prev)) cmd.ExecuteNonQuery();
                            SetStatus(c, storeId, "⚠️ " + ex.Message);
                        }
                        return;
                    }
                    catch (ScException ex) { Log.Error(ex); }   // this one message can't go (it stays in Sellybot); the rest do
                }
                using (var c = Db.Open()) SetStatus(c, storeId, null);   // and when it last worked
                if (rows.Count < Batch) return;
            }
        }

        // One message into its conversation (made the first time a customer shows up)
        static async Task Post(string b, ScLink l, StoreInfo s, Row row)
        {
            var conv = await Conversation(b, l, s, Chan(row), row.Chat);
            bool owner = (row.MsgId ?? "").StartsWith("owner:");
            string token = row.Customer ? l.OwnerToken : owner ? l.PhoneBotToken : l.BotToken;
            string body = row.Body ?? "";
            byte[] image = null; string mime = null;
            if (row.Customer && !string.IsNullOrEmpty(row.Media))
            {
                var got = await Picture(s, row);
                if (got != null) { image = got.Item1; mime = got.Item2; if (body == WaImages.Placeholder) body = ""; }
            }
            if (body.Length == 0 && image == null) body = "…";
            string path = "/api/v1/accounts/" + l.AccountId + "/conversations/" + conv + "/messages";
            try { await Send(b, token, path, body, row.Customer, image, mime); }
            catch (ScException ex) when (ex.Status == 404)
            {
                // someone deleted the conversation in SellyChat: a new one
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "DELETE FROM dbo.ScConvs WHERE StoreId=@s AND ConvId=@v", "@s", l.StoreId, "@v", conv)) cmd.ExecuteNonQuery();
                conv = await Conversation(b, l, s, Chan(row), row.Chat);
                await Send(b, token, "/api/v1/accounts/" + l.AccountId + "/conversations/" + conv + "/messages", body, row.Customer, image, mime);
            }
        }

        static Task<JsonElement> Send(string b, string token, string path, string body, bool incoming, byte[] image, string mime)
        {
            string type = incoming ? "incoming" : "outgoing";
            if (image == null) return Call(b, token, HttpMethod.Post, path, new { content = Cut(body, 4000), message_type = type });
            var form = new MultipartFormDataContent();
            if (body.Length > 0) form.Add(new StringContent(Cut(body, 4000), Encoding.UTF8), "content");
            form.Add(new StringContent(type), "message_type");
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime ?? "image/jpeg");
            form.Add(file, "attachments[]", "photo" + (mime == "image/png" ? ".png" : mime == "image/webp" ? ".webp" : ".jpg"));
            return Call(b, token, HttpMethod.Post, path, null, form);
        }

        // The customer's picture as bytes: from Telegram (tg:<file_id>), Wapilot (may want its token) or Meta's CDN
        static async Task<Tuple<byte[], string>> Picture(StoreInfo s, Row row)
        {
            try
            {
                byte[] bytes = null; string mime = null;
                if (row.Media.StartsWith("tg:"))
                {
                    TgLink t; using (var c = Db.Open()) t = Tg.Link(c, s.Id);
                    if (t == null || t.Token == null) return null;
                    bytes = await Tg.Download(t.Token, row.Media.Substring(3));
                }
                else
                {
                    if (!WaImages.SafeUrl(row.Media)) return null;
                    string waToken = null;
                    if (row.Wa) using (var c = Db.Open()) waToken = WaAccount.Token(c, s);
                    for (int attempt = 0; attempt < 2 && bytes == null; attempt++)
                        using (var req = new HttpRequestMessage(HttpMethod.Get, row.Media))
                        {
                            if (attempt == 1) { if (string.IsNullOrEmpty(waToken)) break; req.Headers.TryAddWithoutValidation("token", waToken); req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + waToken); }
                            using (var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                            {
                                if (!res.IsSuccessStatusCode) continue;
                                if (res.Content.Headers.ContentLength > MaxImage) return null;
                                mime = res.Content.Headers.ContentType != null ? res.Content.Headers.ContentType.MediaType : null;
                                bytes = await res.Content.ReadAsByteArrayAsync();
                            }
                        }
                }
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImage) return null;
                mime = Sniff(bytes) ?? mime;
                return mime != null && mime.StartsWith("image/") ? Tuple.Create(bytes, mime) : null;
            }
            catch (Exception ex) { Log.Error(new Exception("SellyChat picture: " + ex.GetType().Name)); return null; }
        }

        static string Sniff(byte[] b)
        {
            if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
            if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
            if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
            return null;
        }

        // The conversation of one customer on one channel: kept in dbo.ScConvs, made (with its inbox and contact) the first time
        static async Task<int> Conversation(string b, ScLink l, StoreInfo s, string chan, string chat)
        {
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, "SELECT ConvId FROM dbo.ScConvs WHERE StoreId=@s AND Chan=@ch AND ChatId=@c", "@s", l.StoreId, "@ch", chan, "@c", chat))
            { var v = cmd.ExecuteScalar(); if (v != null) return (int)v; }

            int inbox = await Inbox(b, l, s, chan);
            string name, phone; Who(s, chan, chat, out name, out phone);
            if (chan == MetaApp.Fb || chan == MetaApp.Ig) name = await MetaName(s, chan, chat) ?? name;
            string ident = "sb:" + chan + ":" + chat, source = null; int contact = 0;
            string acct = "/api/v1/accounts/" + l.AccountId;
            try
            {
                var made = await Call(b, l.OwnerToken, HttpMethod.Post, acct + "/contacts", phone != null
                    ? (object)new { inbox_id = inbox, name, identifier = ident, phone_number = phone }
                    : new { inbox_id = inbox, name, identifier = ident });
                var p = WaJson.O(made, "payload") ?? made;
                var ct = WaJson.O(p, "contact") ?? p;
                contact = Id(ct);
                var ci = WaJson.O(p, "contact_inbox");
                source = ci.HasValue ? WaJson.S(ci.Value, "source_id") : null;
                if (source == null) source = SourceIn(ct, inbox);
            }
            catch (ScException ex) when (ex.Status == 422)
            {
                // already there (our record of it got lost): find them, and their place in this inbox
                var found = await Call(b, l.OwnerToken, HttpMethod.Get, acct + "/contacts/search?q=" + Uri.EscapeDataString(ident));
                JsonElement arr; JsonElement hit = default(JsonElement);
                if (found.ValueKind == JsonValueKind.Object && found.TryGetProperty("payload", out arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var x in arr.EnumerateArray()) if (WaJson.S(x, "identifier") == ident) { hit = x; break; }
                contact = hit.ValueKind == JsonValueKind.Object ? Id(hit) : 0;
                if (contact == 0) throw;
                source = SourceIn(hit, inbox);
                if (source == null)
                {
                    var ci = await Call(b, l.OwnerToken, HttpMethod.Post, acct + "/contacts/" + contact + "/contact_inboxes", new { inbox_id = inbox });
                    source = WaJson.S(ci, "source_id");
                }
            }
            if (contact == 0 || string.IsNullOrEmpty(source)) throw new ScException(Brand + " مارجّعش بيانات الزبون.");
            var conv = await Call(b, l.OwnerToken, HttpMethod.Post, acct + "/conversations", new { source_id = source, inbox_id = inbox, contact_id = contact });
            int id = Id(conv);
            if (id == 0) throw new ScException(Brand + " مارجّعش المحادثة.");
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.ScConvs WHERE StoreId=@s AND Chan=@ch AND ChatId=@c)
                INSERT INTO dbo.ScConvs(StoreId, Chan, ChatId, ContactId, ConvId) VALUES(@s, @ch, @c, @ct, @v)", "@s", l.StoreId, "@ch", chan, "@c", chat, "@ct", contact, "@v", id))
                cmd.ExecuteNonQuery();
            return id;
        }

        static string SourceIn(JsonElement contact, int inbox)
        {
            JsonElement arr;
            if (contact.ValueKind != JsonValueKind.Object || !contact.TryGetProperty("contact_inboxes", out arr) || arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var x in arr.EnumerateArray())
            {
                var ib = WaJson.O(x, "inbox");
                if (ib.HasValue && Id(ib.Value) == inbox) return WaJson.S(x, "source_id");
            }
            return null;
        }

        // A first name for the contact: the number on WhatsApp, the name they gave on Telegram
        static void Who(StoreInfo s, string chan, string chat, out string name, out string phone)
        {
            phone = null;
            if (chan.StartsWith("wa"))
            {
                var d = WaChat.Digits(chat);
                if (d != null) { phone = "+" + d; name = WaLine.Local(d); }
                else name = "زبون على الواتساب";
                return;
            }
            if (chan == Tg.Ch)
            {
                string n = null;
                try { using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "SELECT Name FROM dbo.TgContacts WHERE StoreId=@s AND ChatId=@c", "@s", s.Id, "@c", chat)) n = cmd.ExecuteScalar() as string; }
                catch (Exception ex) { Log.Error(ex); }
                name = string.IsNullOrWhiteSpace(n) ? "زبون على تيليجرام" : n.Trim();
                return;
            }
            string tail = chat.Length > 4 ? chat.Substring(chat.Length - 4) : chat;
            name = (chan == MetaApp.Fb ? "زبون على المسنجر" : "زبون على الإنستجرام") + " " + tail;
        }

        // The customer's name from Meta, when the Page's token may read it (a Messenger customer's profile, an Instagram username)
        static async Task<string> MetaName(StoreInfo s, string ch, string chat)
        {
            try
            {
                MetaLink link; MetaCreds k;
                using (var c = Db.Open()) { link = MetaApp.Link(c, s.Id, ch); k = link == null ? null : MetaApp.ForLink(c, link); }
                if (link == null || k == null || link.Token == null) return null;
                var p = await MetaApp.Call(HttpMethod.Get, Uri.EscapeDataString(chat) + (ch == MetaApp.Fb ? "?fields=first_name,last_name" : "?fields=name,username"), link.Token, k.Secret);
                string n = ch == MetaApp.Fb ? ((WaJson.S(p, "first_name") ?? "") + " " + (WaJson.S(p, "last_name") ?? "")).Trim() : (WaJson.S(p, "name") ?? WaJson.S(p, "username"));
                return string.IsNullOrWhiteSpace(n) ? null : Cut(n.Trim(), 80);
            }
            catch (MetaException) { return null; }
            catch (Exception ex) { Log.Error(ex); return null; }
        }

        // One "API" inbox per channel (and per WhatsApp number), made the first time it's needed
        static async Task<int> Inbox(string b, ScLink l, StoreInfo s, string chan)
        {
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, "SELECT InboxId FROM dbo.ScInboxes WHERE StoreId=@s AND Chan=@ch", "@s", l.StoreId, "@ch", chan))
            { var v = cmd.ExecuteScalar(); if (v != null) return (int)v; }
            string name, hook = HookUrl(l);
            using (var c = Db.Open()) name = InboxName(c, s, chan);
            var made = await Call(b, l.OwnerToken, HttpMethod.Post, "/api/v1/accounts/" + l.AccountId + "/inboxes", new { name, channel = new { type = "api", webhook_url = hook } });
            int id = Id(made);
            if (id == 0) throw new ScException(Brand + " مارجّعش صندوق الرسايل.");
            string secret = WaJson.S(made, "secret");
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.ScInboxes WHERE StoreId=@s AND Chan=@ch)
                INSERT INTO dbo.ScInboxes(StoreId, Chan, InboxId, SecretBox) VALUES(@s, @ch, @i, @sb)", "@s", l.StoreId, "@ch", chan, "@i", id,
                "@sb", string.IsNullOrEmpty(secret) ? (object)DBNull.Value : Secrets.Protect(secret))) cmd.ExecuteNonQuery();
            await Members(b, l, id);   // the team sees the new channel too
            return id;
        }

        static string InboxName(SqlConnection c, StoreInfo s, string chan)
        {
            if (chan == MetaApp.Fb) return "💬 مسنجر";
            if (chan == MetaApp.Ig) return "📸 إنستجرام";
            if (chan == Tg.Ch) return "✈️ تيليجرام";
            int line; int.TryParse(chan.Substring(2), out line);
            var w = WaLines.Get(c, s, line);
            string num = w != null && !string.IsNullOrEmpty(w.Number) ? WaLine.Local(WaChat.Digits(w.Number) ?? w.Number) : null;
            return "🟢 واتساب" + (num != null ? " " + num : "") + (w != null && !string.IsNullOrEmpty(w.Staff) ? " (" + w.Staff + ")" : "");
        }

        // ---------- an answer written in SellyChat ----------
        public class Out { public int MsgId, ConvId, AccountId, InboxId; public string Text; public List<Tuple<string, string>> Images = new List<Tuple<string, string>>(); public bool Other; }

        // What the hook understood: an agent's answer to send, or null (anything else Chatwoot tells about)
        public static Out Read(JsonElement root)
        {
            if (WaJson.S(root, "event") != "message_created" || WaJson.S(root, "message_type") != "outgoing" || (WaJson.B(root, "private") ?? false)) return null;
            var sender = WaJson.O(root, "sender");
            if (!sender.HasValue || WaJson.S(sender.Value, "type") != "user") return null;   // our own bots' copies, the customer's messages
            var conv = WaJson.O(root, "conversation"); var acct = WaJson.O(root, "account"); var inbox = WaJson.O(root, "inbox");
            var o = new Out { MsgId = Id(root), ConvId = conv.HasValue ? Id(conv.Value) : 0, AccountId = acct.HasValue ? Id(acct.Value) : 0, InboxId = inbox.HasValue ? Id(inbox.Value) : 0, Text = (WaJson.S(root, "content") ?? "").Trim() };
            JsonElement atts;
            if (root.TryGetProperty("attachments", out atts) && atts.ValueKind == JsonValueKind.Array)
                foreach (var a in atts.EnumerateArray())
                {
                    string url = WaJson.S(a, "data_url"), type = WaJson.S(a, "file_type");
                    if (type == "image" && !string.IsNullOrEmpty(url)) o.Images.Add(Tuple.Create(url, WaJson.S(a, "content_type")));
                    else o.Other = true;
                }
            return o.MsgId > 0 && o.ConvId > 0 ? o : null;
        }

        // Is the signature Chatwoot put on the webhook the inbox's? (no secret kept for it: the key in the URL is all there is)
        public static bool Signed(SqlConnection c, int storeId, int inboxId, string ts, string sig, byte[] body)
        {
            string box;
            using (var cmd = Db.Cmd(c, "SELECT SecretBox FROM dbo.ScInboxes WHERE StoreId=@s AND InboxId=@i", "@s", storeId, "@i", inboxId)) box = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(box)) return true;
            var secret = Secrets.Unprotect(box);
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(ts)) return false;
            long t; if (!long.TryParse(ts, out t) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 600) return false;
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var mac = h.ComputeHash(Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray());
                string want = "sha256=" + string.Concat(mac.Select(x => x.ToString("x2")));
                return Secrets.Same(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(sig.Trim()));
            }
        }

        // Sends an agent's answer to the customer the way the chat came, and keeps it as the owner's message
        public static async Task Deliver(int storeId, Out o)
        {
            ScLink l; StoreInfo s; string chan = null, chat = null, b;
            using (var c = Db.Open())
            {
                l = Link(c, storeId); s = Db.StoreById(c, storeId); b = Base(c);
                if (l == null || s == null || b == null || o.AccountId != l.AccountId) return;
                using (var cmd = Db.Cmd(c, "SELECT Chan, ChatId FROM dbo.ScConvs WHERE StoreId=@s AND ConvId=@v", "@s", storeId, "@v", o.ConvId))
                using (var r = cmd.ExecuteReader()) if (r.Read()) { chan = (string)r[0]; chat = (string)r[1]; }
            }
            if (chan == null) { await Note(b, l, o.ConvId, "⚠️ المحادثة دي مش مربوطة بزبون في Sellybot، فالرسالة ماوصلتش."); return; }
            if (o.Text.Length == 0 && o.Images.Count == 0) { if (o.Other) await Note(b, l, o.ConvId, "⚠️ " + Brand + " بيبعت للزبون كلام وصور بس دلوقتي. الملف ده ماوصلش."); return; }

            string why = null;
            long? row = null; bool wa = chan.StartsWith("wa"); int line = 0;
            if (wa) int.TryParse(chan.Substring(2), out line);
            // kept as it goes out (WhatsApp's *bold*, plain text elsewhere): the echo of it is then recognised as ours
            string saved = o.Text.Length == 0 ? "[صورة من المتجر]" : wa ? Wapilot.Format(o.Text) : MetaBot.Plain(o.Text);
            using (var c = Db.Open())
            {
                if (!On(c, s)) why = Brand + " مش ضمن باقتك دلوقتي.";
                // kept first: Wapilot may echo it back before the send returns, and the assistant should step aside at once
                else row = wa ? WhatsAppBot.Save(c, storeId, chat, false, saved, FromScPrefix + o.MsgId, null, line)
                              : MetaBot.Save(c, storeId, chan, chat, false, saved, FromScPrefix + o.MsgId);
            }
            if (why == null && row == null) return;   // the same webhook twice: it went already
            if (why == null)
                try
                {
                    var pics = new List<Tuple<byte[], string, string>>();
                    foreach (var im in o.Images.Take(4))
                    {
                        var got = await Attachment(b, im.Item1);
                        if (got != null) pics.Add(Tuple.Create(got.Item1, got.Item2, im.Item1));
                    }
                    if (o.Images.Count > 0 && pics.Count == 0 && o.Text.Length == 0) why = "مقدرناش نجيب الصورة من " + Brand + ".";
                    else if (wa) why = await ToWhatsApp(s, line, chat, o, pics);
                    else if (chan == Tg.Ch) why = await ToTelegram(s, chat, o, pics);
                    else why = await ToMeta(s, chan, chat, o, pics);
                }
                catch (Exception ex) { Log.Error(ex); why = "حصلت مشكلة وإحنا بنبعتها."; }
            if (why != null)
            {
                if (row.HasValue)
                    using (var c = Db.Open())
                    using (var cmd = Db.Cmd(c, "DELETE FROM dbo." + (wa ? "WaMessages" : "MetaMessages") + " WHERE Id=@id", "@id", row.Value)) cmd.ExecuteNonQuery();
                await Note(b, l, o.ConvId, "⚠️ الرسالة دي ماوصلتش للزبون: " + why);
                return;
            }
            if (o.Other) await Note(b, l, o.ConvId, "ℹ️ الكلام والصور وصلوا. الملفات التانية " + Brand + " مش بيبعتها للزبون دلوقتي.");
        }

        static async Task<string> ToWhatsApp(StoreInfo s, int line, string chatKey, Out o, List<Tuple<byte[], string, string>> pics)
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
                int i = 0;
                foreach (var p in pics) await Wapilot.SendImage(token, from, chat, p.Item1, p.Item2, i == 0 ? Wapilot.Format(o.Text) : null, "sb-cw-" + o.MsgId + "-p" + (i++));
                if (pics.Count == 0 && o.Text.Length > 0)
                    foreach (var part in Wapilot.Split(Wapilot.Format(o.Text))) await Wapilot.Send(token, from, chat, part, "sb-cw-" + o.MsgId + "-" + (i++));
                return null;
            }
            catch (WaException ex) { return ex.Message; }
        }

        static async Task<string> ToTelegram(StoreInfo s, string chat, Out o, List<Tuple<byte[], string, string>> pics)
        {
            TgLink t; using (var c = Db.Open()) t = Tg.Link(c, s.Id);
            if (t == null || t.Token == null) return "بوت التيليجرام مش مربوط دلوقتي.";
            try
            {
                int i = 0;
                foreach (var p in pics) await Tg.SendPhoto(t.Token, chat, p.Item1, p.Item2, i++ == 0 ? Cut(MetaBot.Plain(o.Text), 1000) : null);
                if (pics.Count == 0 || MetaBot.Plain(o.Text).Length > 1000) await TgBot.Send(t.Token, chat, MetaBot.Plain(o.Text));
                return null;
            }
            catch (TgException ex) { return ex.Message; }
        }

        static async Task<string> ToMeta(StoreInfo s, string ch, string chat, Out o, List<Tuple<byte[], string, string>> pics)
        {
            MetaLink link; MetaCreds k;
            using (var c = Db.Open()) { link = MetaApp.Link(c, s.Id, ch); k = link == null ? null : MetaApp.ForLink(c, link); }
            if (link == null || link.Token == null || k == null) return MetaApp.Label(ch) + " مش مربوط دلوقتي.";
            try
            {
                // a picture goes by its address on SellyChat (Meta fetches it from there)
                foreach (var p in pics)
                {
                    object msg = new { attachment = new { type = "image", payload = new { url = p.Item3, is_reusable = false } } };
                    object body = ch == MetaApp.Fb ? (object)new { recipient = new { id = chat }, messaging_type = "RESPONSE", message = msg } : new { recipient = new { id = chat }, message = msg };
                    await MetaApp.Call(HttpMethod.Post, Uri.EscapeDataString(link.AccountId) + "/messages", link.Token, k.Secret, body);
                }
                if (o.Text.Length > 0) await MetaBot.Send(link, chat, MetaBot.Plain(o.Text), k.Secret);
                return null;
            }
            catch (MetaException ex) { return ex.Message + (ex.Code == 10 || ex.Subcode == 2018278 ? " (فيسبوك بيسمح بالرد في خلال 24 ساعة من آخر رسالة من الزبون بس)" : ""); }
        }

        // A picture an agent attached, fetched from SellyChat itself (never from anywhere else)
        static async Task<Tuple<byte[], string>> Attachment(string b, string url)
        {
            Uri u, home;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || !Uri.TryCreate(b, UriKind.Absolute, out home) || !string.Equals(u.Host, home.Host, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                using (var res = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxImage) return null;
                    var bytes = await res.Content.ReadAsByteArrayAsync();
                    if (bytes.Length == 0 || bytes.Length > MaxImage) return null;
                    var mime = Sniff(bytes);
                    return mime == null ? null : Tuple.Create(bytes, mime);
                }
            }
            catch (Exception ex) { Log.Error(new Exception("SellyChat attachment: " + ex.GetType().Name)); return null; }
        }

        // A private note in the conversation (only the team sees it), from the assistant bot
        static async Task Note(string b, ScLink l, int conv, string text)
        {
            try { await Call(b, l.BotToken, HttpMethod.Post, "/api/v1/accounts/" + l.AccountId + "/conversations/" + conv + "/messages", new { content = text, message_type = "outgoing", @private = true }); }
            catch (ScException ex) { Log.Error(ex); }
        }

        // ---------- what the dashboard shows ----------
        public static Dictionary<string, object> State(SqlConnection c, StoreInfo s)
        {
            var l = Link(c, s.Id);
            var d = new Dictionary<string, object> { { "on", On(c, s) }, { "seats", Seats(c) }, { "brand", Brand } };
            if (l == null) { d["link"] = null; return d; }
            int convs; using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ScConvs WHERE StoreId=@s", "@s", s.Id)) convs = (int)cmd.ExecuteScalar();
            var chans = new List<string>();
            using (var cmd = Db.Cmd(c, "SELECT Chan FROM dbo.ScInboxes WHERE StoreId=@s", "@s", s.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) chans.Add((string)r[0]);
            d["link"] = new Dictionary<string, object> {
                { "status", l.Status ?? "" }, { "createdAt", l.CreatedAt.ToString("o") + "Z" }, { "lastAt", l.LastAt.HasValue ? l.LastAt.Value.ToString("o") + "Z" : null },
                { "conversations", convs }, { "channels", chans.Select(x => x.StartsWith("wa") ? "wa" : x).Distinct().ToArray() }, { "address", Base(c) } };
            d["agents"] = Agents(c, s.Id).Select(a => new Dictionary<string, object> { { "id", a.UserId }, { "name", a.Name }, { "email", a.Email }, { "createdAt", a.CreatedAt.ToString("o") + "Z" } }).ToArray();
            return d;
        }
    }

    // api/sellychat.ashx — the owner's «💬 محادثاتي»: GET = state; POST ?action=start | open | stop | agent-add {name, email} |
    // agent-reset {id} | agent-remove {id}
    public class SellyChatHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                StoreInfo s;
                using (var c = Db.Open())
                {
                    s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, SellyChat.State(c, s)); return; }
                }
                if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                if (!Web.Allow("sc:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                var b = Web.Body(req, 2000);
                using (var c = Db.Open())
                    if (action != "stop" && action != "agent-remove" && !SellyChat.On(c, s))
                    {
                        if (SellyChat.Ready(c) && !string.IsNullOrEmpty(Billing.Get(c, "MinPlan_" + SellyChat.Feature))) { PlanGate.Deny(res, c, s, SellyChat.Feature); return; }
                        Web.Error(res, 403, "closed", SellyChat.Brand + " مش متاح لمتجرك دلوقتي."); return;
                    }
                try
                {
                    if (action == "start")
                    {
                        var err = await SellyChat.Start(s.Id, WaAccount.Origin(req));
                        if (err != null) { Web.Error(res, 400, "sellychat", err); return; }
                    }
                    else if (action == "open") { Web.Json(res, 200, new { url = await SellyChat.LoginUrl(s.Id) }); return; }
                    else if (action == "stop")
                    {
                        var err = await SellyChat.Stop(s.Id);
                        if (err != null) { Web.Error(res, 400, "sellychat", err); return; }
                    }
                    else if (action == "agent-add")
                    {
                        string name = (Web.Str(b, "name", 60) ?? "").Trim(), email = (Web.Str(b, "email", 150) ?? "").Trim().ToLowerInvariant();
                        if (name.Length < 2) { Web.Error(res, 400, "bad_name", "اكتب اسم الموظف."); return; }
                        if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[a-z]{2,}$")) { Web.Error(res, 400, "bad_email", "الإيميل شكله مش مظبوط."); return; }
                        using (var c = Db.Open()) if (SellyChat.Link(c, s.Id) == null) { Web.Error(res, 400, "not_started", "شغّل " + SellyChat.Brand + " الأول."); return; }
                        var pw = await SellyChat.AddAgent(s.Id, name, email);
                        using (var c = Db.Open()) { var st = SellyChat.State(c, s); st["password"] = pw; st["for"] = email; Web.Json(res, 200, st); }
                        return;
                    }
                    else if (action == "agent-reset")
                    {
                        int id = Web.Int(b, "id") ?? 0;
                        var pw = await SellyChat.ResetAgent(s.Id, id);
                        string email; using (var c = Db.Open()) email = SellyChat.Agents(c, s.Id).Where(a => a.UserId == id).Select(a => a.Email).FirstOrDefault();
                        using (var c = Db.Open()) { var st = SellyChat.State(c, s); st["password"] = pw; st["for"] = email; Web.Json(res, 200, st); }
                        return;
                    }
                    else if (action == "agent-remove") await SellyChat.RemoveAgent(s.Id, Web.Int(b, "id") ?? 0);
                    else { Web.Error(res, 400, "bad_request"); return; }
                }
                catch (ScException ex) { Web.Error(res, 400, "sellychat", ex.Message); return; }
                using (var c = Db.Open()) Web.Json(res, 200, SellyChat.State(c, s));
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }
    }

    // api/sellychat-hook.ashx?s=<store>&k=<key> — Chatwoot posts every message of the store's inboxes here (the API inbox's webhook).
    // Only an agent's answer does anything; the rest gets 200 too (anything else and Chatwoot marks the message "failed").
    public class SellyChatHookHandler : IHttpHandler
    {
        static DateTime lastBad = DateTime.MinValue;
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { res.StatusCode = 503; return; }
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }
            if (!Web.Allow("sc-hook:" + Web.Ip(req), 1200, TimeSpan.FromMinutes(1))) { res.StatusCode = 429; return; }
            try
            {
                int storeId;
                if (!int.TryParse(req.QueryString["s"] ?? "", out storeId) || storeId <= 0) { res.StatusCode = 404; return; }
                byte[] raw;
                using (var ms = new System.IO.MemoryStream()) { req.InputStream.CopyTo(ms); raw = ms.ToArray(); }
                if (raw.Length > 512 * 1024) { res.StatusCode = 413; return; }
                JsonElement root;
                try { using (var doc = JsonDocument.Parse(raw)) root = doc.RootElement.Clone(); }
                catch { res.StatusCode = 400; return; }
                SellyChat.Out o;
                using (var c = Db.Open())
                {
                    var l = SellyChat.Link(c, storeId);
                    if (l == null) { Web.Json(res, 200, new { ok = true }); return; }   // closed since: nothing to retry
                    if (!Secrets.Same(Encoding.UTF8.GetBytes(l.HookKey), Encoding.UTF8.GetBytes(req.QueryString["k"] ?? "")))
                    {
                        if (DateTime.UtcNow - lastBad > TimeSpan.FromMinutes(10)) { lastBad = DateTime.UtcNow; Log.Error(new Exception("SellyChat webhook: bad key")); }
                        res.StatusCode = 401; return;
                    }
                    o = SellyChat.Read(root);
                    if (o != null && !SellyChat.Signed(c, storeId, o.InboxId, req.Headers["X-Chatwoot-Timestamp"], req.Headers["X-Chatwoot-Signature"], raw))
                    {
                        if (DateTime.UtcNow - lastBad > TimeSpan.FromMinutes(10)) { lastBad = DateTime.UtcNow; Log.Error(new Exception("SellyChat webhook: bad signature")); }
                        res.StatusCode = 401; return;
                    }
                }
                if (o != null) HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { await SellyChat.Deliver(storeId, o); } catch (Exception ex) { Log.Error(ex); } });
                Web.Json(res, 200, new { ok = true });
            }
            catch (Exception ex) { Log.Error(ex); res.StatusCode = 500; }
        }
    }

    // api/sellychat-admin.ashx — the super admin's «SellyChat»: the server (address + Platform App token), testers, employees per
    // store, and every store's account. GET = state (and a live check of the server); POST ?action=save {url, token, testers, seats}
    public class SellyChatAdminHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                    if (!MetaAdminHandler.SignedIn(ctx, c)) { Web.Error(res, 401, "not_logged_in"); return; }
                if (req.HttpMethod == "GET") { Web.Json(res, 200, await State()); return; }
                if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                var b = Web.Body(req, 4000);
                if (action != "save") { Web.Error(res, 400, "bad_request"); return; }
                string url = (Web.Str(b, "url", 200) ?? "").Trim().TrimEnd('/'), token = (Web.Str(b, "token", 200) ?? "").Trim();
                string testers = string.Join(", ", (Web.Str(b, "testers", 1000) ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Distinct());
                int seats = Web.Int(b, "seats") ?? 5;
                if (url.Length > 0 && !Regex.IsMatch(url, @"^https?://[a-z0-9.-]+(:\d+)?$", RegexOptions.IgnoreCase)) { Web.Error(res, 400, "bad_url", "العنوان لازم يبقى زي https://chat.sellybot.online من غير أي حاجة بعده."); return; }
                if (url.Length > 0 && token.Length > 0)
                {
                    var why = await Check(url, token);
                    if (why != null) { Web.Error(res, 400, "check", why); return; }
                }
                using (var c = Db.Open())
                {
                    Billing.Set(c, "SellyChatUrl", url);
                    if (token.Length > 0) Billing.Set(c, "SellyChatToken", Secrets.Protect(token));
                    else if (url.Length == 0) Billing.Set(c, "SellyChatToken", "");
                    Billing.Set(c, "SellyChatTesters", testers);
                    Billing.Set(c, "SellyChatSeats", Math.Max(1, Math.Min(50, seats)).ToString());
                }
                Web.Json(res, 200, await State());
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }

        // null when the server answers and the token is a Platform App's
        static async Task<string> Check(string url, string token)
        {
            try
            {
                using (var r = await Http.GetAsync(url + "/api")) if (!r.IsSuccessStatusCode) return "السيرفر ده مش بيرد زي " + SellyChat.Brand + " (" + (int)r.StatusCode + ").";
                using (var req = new HttpRequestMessage(HttpMethod.Get, url + "/platform/api/v1/users/0"))
                {
                    req.Headers.TryAddWithoutValidation("api_access_token", token);
                    using (var r = await Http.SendAsync(req))
                        if ((int)r.StatusCode == 401) return "المفتاح ده مش مفتاح Platform App على السيرفر ده.";
                }
                return null;
            }
            catch (Exception ex) { return "مقدرناش نوصل للسيرفر: " + ex.GetType().Name; }
        }

        static async Task<Dictionary<string, object>> State()
        {
            var d = new Dictionary<string, object>(); string url; bool hasToken; string platformToken = null;
            var stores = new List<Dictionary<string, object>>();
            using (var c = Db.Open())
            {
                url = SellyChat.Base(c); hasToken = SellyChat.Ready(c) || !string.IsNullOrEmpty(Billing.Get(c, "SellyChatToken"));
                d["url"] = url ?? ""; d["hasToken"] = hasToken; d["testers"] = Billing.Get(c, "SellyChatTesters") ?? ""; d["seats"] = SellyChat.Seats(c);
                d["minPlan"] = PlanGate.MinPlanId(c, SellyChat.Feature);
                using (var cmd = Db.Cmd(c, @"SELECT TOP 300 l.StoreId, l.Status, l.LastAt, l.CreatedAt, s.Slug, s.Name,
                    (SELECT COUNT(*) FROM dbo.ScAgents a WHERE a.StoreId=l.StoreId) AS Agents, (SELECT COUNT(*) FROM dbo.ScConvs v WHERE v.StoreId=l.StoreId) AS Convs
                    FROM dbo.ScLinks l JOIN dbo.Stores s ON s.Id = l.StoreId ORDER BY l.CreatedAt DESC"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        stores.Add(new Dictionary<string, object> {
                            { "store", r["Name"] }, { "slug", r["Slug"] }, { "status", r["Status"] as string ?? "" }, { "agents", r["Agents"] }, { "conversations", r["Convs"] },
                            { "lastAt", r["LastAt"] is DateTime ? ((DateTime)r["LastAt"]).ToString("o") + "Z" : null }, { "createdAt", ((DateTime)r["CreatedAt"]).ToString("o") + "Z" } });
                var b = Billing.Get(c, "SellyChatToken");
                platformToken = string.IsNullOrEmpty(b) ? null : Secrets.Unprotect(b);
            }
            d["stores"] = stores;
            if (url != null && platformToken != null) d["check"] = await Check(url, platformToken);
            return d;
        }
    }
}
