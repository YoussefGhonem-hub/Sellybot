// Messenger and Instagram (Meta): the store's Facebook Page and Instagram professional account answer customers'
// messages with the same assistant as the site chat and WhatsApp (same data, rules, tools and monthly quota).
//
// One Meta app for the whole platform. The super admin saves its App ID, App Secret and the Facebook Login for Business
// configuration IDs in admin.html → «مسنجر وإنستجرام» (AppSettings; the secret encrypted), and copies from there the
// webhook URL + verify token and the redirect URL into the Meta app.
// - Messenger: the owner presses «اربط صفحتك» → Facebook login (configuration MetaFbConfig, or plain scopes when there's
//   none) → the Pages they manage (me/accounts) → we keep the chosen Page's access token and subscribe the Page to the
//   app's webhooks (messages, postbacks, echoes).
// - Instagram: "Instagram API with Unified Login", the only setup Meta gives new apps (2026). The owner logs in with
//   Instagram or Facebook (configuration MetaIgConfig) → a Business Integration System User token for their business
//   portfolio → their Instagram account (me/assigned_instagram_accounts) → subscribed to the webhooks. graph.facebook.com.
// Flow: Meta → api/meta-webhook.ashx (signed with the App Secret: X-Hub-Signature-256) → message saved (MetaMessages,
//       duplicates dropped) → 200 right away → background: wait a few seconds (customers send 2-3 short messages),
//       answer with the recent conversation, send with the Send API.
// Meta lets a business answer only within 24 hours of the customer's last message, and that's all the bot does.
// When the owner answers a customer by hand (an echo that isn't ours), the bot steps aside in that chat for a while.
// Plan gates "messenger" and "instagram" (open to every plan until the super admin picks a minimum).
// Built from Meta's docs (2026-11-19) and tested with a fake Graph API (settings MetaGraphBase / MetaDialogBase).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
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
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public class MetaException : Exception
    {
        public int Status, Code, Subcode;
        public MetaException(string message, int status = 0, int code = 0, int subcode = 0) : base(message) { Status = status; Code = code; Subcode = subcode; }
        public bool TokenDead { get { return Code == 190 || Code == 102; } }   // the owner removed the app, changed their password, ...
    }

    public class MetaLink
    {
        public int StoreId; public string Channel, AccountId, Name, Token, BusinessId, Status;
        public bool Enabled, Subscribed; public DateTime? LastAt;
    }

    // ---------- the platform's Meta app and the Graph API ----------
    public static class MetaApp
    {
        public const string Fb = "fb", Ig = "ig";
        public const string FbFeature = "messenger", IgFeature = "instagram";
        public static string Feature(string ch) { return ch == Fb ? FbFeature : IgFeature; }
        public static string Label(string ch) { return ch == Fb ? "المسنجر" : "الإنستجرام"; }
        public static bool Valid(string ch) { return ch == Fb || ch == Ig; }

        public static string Version { get { var v = (Web.Setting("MetaApiVersion", "v27.0") ?? "").Trim(); return v.Length == 0 ? "v27.0" : v; } }
        static string GraphBase { get { return (Web.Setting("MetaGraphBase", "https://graph.facebook.com/") ?? "").TrimEnd('/') + "/"; } }   // settings only for local tests
        static string DialogBase { get { return (Web.Setting("MetaDialogBase", "https://www.facebook.com/") ?? "").TrimEnd('/') + "/"; } }
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        static MetaApp() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0"); }

        public static string AppId(SqlConnection c) { return Billing.Get(c, "MetaAppId"); }
        public static string Secret(SqlConnection c) { var box = Billing.Get(c, "MetaAppSecret"); return string.IsNullOrEmpty(box) ? null : Secrets.Unprotect(box); }
        public static string Config(SqlConnection c, string ch) { return Billing.Get(c, ch == Fb ? "MetaFbConfig" : "MetaIgConfig"); }
        // Messenger works with plain scopes too; Instagram (Unified Login) needs its configuration
        public static bool Ready(SqlConnection c, string ch) { return !string.IsNullOrEmpty(AppId(c)) && Secret(c) != null && (ch == Fb || !string.IsNullOrEmpty(Config(c, Ig))); }

        // Until Meta approves the app (App Review), only people with a role on the app can log in. So linking stays closed to
        // owners ("قريب") until the super admin ticks MetaLive, except for the platform's own stores and the testers' slugs.
        public static bool Live(SqlConnection c) { return Billing.Get(c, "MetaLive") == "1"; }
        public static string Testers(SqlConnection c) { return Billing.Get(c, "MetaTesters") ?? ""; }
        static bool Tester(SqlConnection c, StoreInfo s)
        {
            return Testers(c).Split(new[] { ',', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(t => string.Equals(t.Trim(), s.Slug, StringComparison.OrdinalIgnoreCase));
        }
        public static bool OpenFor(SqlConnection c, StoreInfo s, string ch)
        {
            if (s == null || !Ready(c, ch)) return false;
            return Live(c) || s.IsAdmin || Tester(c, s);
        }
        // Whether the owner sees the «مسنجر وإنستجرام» tab at all. Until MetaLive it's hidden from every other owner, so a feature
        // that isn't open to them doesn't confuse anyone: only the platform's own stores, the testers' slugs, and a store that
        // already linked one (so it can still unlink after it's taken off the testers list) see it.
        public static bool Visible(SqlConnection c, StoreInfo s)
        {
            if (s == null) return false;
            if (Live(c) || s.IsAdmin || Tester(c, s)) return true;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.MetaLinks WHERE StoreId=@s", "@s", s.Id)) return (int)cmd.ExecuteScalar() > 0;
        }

        public static string VerifyToken(SqlConnection c, bool create)
        {
            var v = Billing.Get(c, "MetaVerifyToken");
            if (string.IsNullOrEmpty(v) && create) { v = WhatsAppBot.NewKey().Substring(0, 32); Billing.Set(c, "MetaVerifyToken", v); }
            return v;
        }

        public static string RedirectUri(HttpRequest req) { return WaAccount.Origin(req) + "/api/meta-connect.ashx"; }
        public static string HookUrl(HttpRequest req) { return WaAccount.Origin(req) + "/api/meta-webhook.ashx"; }

        public static string DialogUrl(SqlConnection c, HttpRequest req, string ch, string state)
        {
            var q = new StringBuilder(DialogBase).Append(Version).Append("/dialog/oauth?client_id=").Append(Uri.EscapeDataString(AppId(c)))
                .Append("&redirect_uri=").Append(Uri.EscapeDataString(RedirectUri(req))).Append("&state=").Append(Uri.EscapeDataString(state))
                .Append("&response_type=code");
            var config = Config(c, ch);
            if (!string.IsNullOrEmpty(config)) q.Append("&config_id=").Append(Uri.EscapeDataString(config)).Append("&override_default_response_type=true");
            else q.Append("&scope=").Append(Uri.EscapeDataString("pages_show_list,pages_messaging,pages_manage_metadata,business_management"));
            return q.ToString();
        }

        // appsecret_proof: Meta accepts it always and requires it when "Require App Secret" is on in the app's settings
        static string Proof(string secret, string token)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(token)).Select(b => b.ToString("x2")));
        }

        // path is relative to the version ("me/accounts?fields=..."); token goes as access_token (+ appsecret_proof)
        public static async Task<JsonElement> Call(HttpMethod method, string path, string token, string secret, object body = null)
        {
            var url = new StringBuilder(GraphBase).Append(Version).Append('/').Append(path);
            if (token != null)
            {
                url.Append(path.Contains("?") ? "&" : "?").Append("access_token=").Append(Uri.EscapeDataString(token));
                if (!string.IsNullOrEmpty(secret) && !token.Contains("|")) url.Append("&appsecret_proof=").Append(Proof(secret, token));
            }
            var req = new HttpRequestMessage(method, url.ToString());
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req); }
            catch (Exception ex) { Log.Error(ex); throw new MetaException("مقدرناش نوصل لفيسبوك دلوقتي. جرّب تاني كمان شوية."); }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync();
                JsonElement root = default(JsonElement); bool parsed = false;
                try { using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)) { root = doc.RootElement.Clone(); parsed = true; } } catch { }
                int status = (int)resp.StatusCode;
                if (status >= 200 && status < 300 && parsed) return root;
                int code = 0, sub = 0; string msg = null;
                JsonElement err;
                if (parsed && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out err) && err.ValueKind == JsonValueKind.Object)
                {
                    msg = WaJson.S(err, "error_user_msg", "message");
                    int.TryParse(WaJson.S(err, "code") ?? "", out code); int.TryParse(WaJson.S(err, "error_subcode") ?? "", out sub);
                }
                Log.Error(new Exception("Meta " + method + " " + path.Split('?')[0] + " -> " + status + " #" + code + "/" + sub + " " + (msg ?? "")));
                throw Friendly(status, code, sub, msg);
            }
        }

        static MetaException Friendly(int status, int code, int sub, string msg)
        {
            string why = (msg ?? "").Trim(); if (why.Length > 160) why = why.Substring(0, 160);
            string text;
            if (code == 190 || code == 102) text = "الربط بفيسبوك وقف (الصلاحية اتشالت، أو الباسورد اتغيّر). اربط تاني من «مسنجر وإنستجرام».";
            else if (code == 10 && sub == 2018278) text = "عدّى أكتر من 24 ساعة على آخر رسالة من الزبون، وفيسبوك مش بيسمح بالرد بعدها.";
            else if (code == 551 || sub == 1545041) text = "الشخص ده مش متاح يستقبل رسايل دلوقتي.";
            else if (code == 10 || code == 200 || (code >= 200 && code < 300)) text = "ناقص صلاحية من فيسبوك. اربط تاني ووافق على كل الصلاحيات اللي بتتطلب.";
            else if (code == 4 || code == 17 || code == 32 || code == 613 || status == 429) text = "فيسبوك بيقول الطلبات كتير دلوقتي، هنحاول تاني بعد شوية.";
            else text = "فيسبوك رفض الطلب" + (why.Length > 0 ? ": " + why : "") + " (" + status + (code > 0 ? " #" + code : "") + ").";
            return new MetaException(text, status, code, sub);
        }

        // ---- the OAuth state: which store and which channel, who started it (o = the owner's dashboard, a = the super admin
        // linking Sellybot's own Page from admin.html), signed, good for 20 minutes ----
        public static string State(int storeId, string ch, bool admin = false)
        {
            string payload = storeId + "." + ch + "." + DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds() + "." + WhatsAppBot.NewKey().Substring(0, 12) + "." + (admin ? "a" : "o");
            return payload + "." + StateSig(payload);
        }

        public static bool ReadState(string state, out int storeId, out string ch, out bool admin)
        {
            storeId = 0; ch = null; admin = false;
            var p = (state ?? "").Split('.');
            if (p.Length != 6) return false;
            string payload = string.Join(".", p.Take(5));
            if (!Secrets.Same(Encoding.UTF8.GetBytes(StateSig(payload)), Encoding.UTF8.GetBytes(p[5]))) return false;
            long exp;
            if (!long.TryParse(p[2], out exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
            if (!int.TryParse(p[0], out storeId) || !Valid(p[1]) || (p[4] != "a" && p[4] != "o")) return false;
            ch = p[1]; admin = p[4] == "a";
            return true;
        }

        static string StateSig(string payload)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes((Web.Setting("AuthSecret") ?? "") + "|meta-state")))
                return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(payload)).Select(b => b.ToString("x2"))).Substring(0, 32);
        }

        // Webhook bodies are signed with the App Secret: X-Hub-Signature-256: sha256=<hex of HMAC-SHA256(secret, raw body)>
        public static bool SignatureOk(string secret, byte[] body, string header)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header) || !header.StartsWith("sha256=")) return false;
            byte[] mac; using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret))) mac = h.ComputeHash(body);
            string want = string.Concat(mac.Select(b => b.ToString("x2")));
            return Secrets.Same(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(header.Substring(7).Trim().ToLowerInvariant()));
        }

        // ---- the links (one Page / one Instagram account per store) ----
        public static MetaLink Link(SqlConnection c, int storeId, string ch) { return One(c, "StoreId=@a AND Channel=@b", storeId, ch); }
        public static MetaLink LinkFor(SqlConnection c, string ch, string accountId) { return One(c, "Channel=@b AND AccountId=@a", accountId, ch); }

        static MetaLink One(SqlConnection c, string where, object a, string ch)
        {
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 * FROM dbo.MetaLinks WHERE " + where, "@a", a, "@b", ch))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                return new MetaLink
                {
                    StoreId = (int)r["StoreId"], Channel = (string)r["Channel"], AccountId = (string)r["AccountId"], Name = r["Name"] as string,
                    Token = Secrets.Unprotect((string)r["Token"]), BusinessId = r["BusinessId"] as string, Status = r["Status"] as string,
                    Enabled = (bool)r["Enabled"], Subscribed = (bool)r["Subscribed"],
                    LastAt = r["LastAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["LastAt"], DateTimeKind.Utc) : (DateTime?)null,
                };
            }
        }

        public static void SetStatus(SqlConnection c, int storeId, string ch, string status)
        {
            if (status != null && status.Length > 400) status = status.Substring(0, 400);
            using (var cmd = Db.Cmd(c, "UPDATE dbo.MetaLinks SET Status=@st" + (status == null ? ", LastAt=SYSUTCDATETIME()" : "") + " WHERE StoreId=@s AND Channel=@ch",
                "@st", (object)status ?? DBNull.Value, "@s", storeId, "@ch", ch)) cmd.ExecuteNonQuery();
        }

        // Subscribes the account to the app's webhooks; null = done, otherwise why not
        public static async Task<string> Subscribe(string ch, string accountId, string token, string secret)
        {
            try
            {
                string fields = ch == Fb ? "messages,messaging_postbacks,message_echoes" : "messages";
                await Call(HttpMethod.Post, Uri.EscapeDataString(accountId) + "/subscribed_apps?subscribed_fields=" + fields, token, secret);
                return null;
            }
            catch (MetaException ex) { return ex.Message; }
        }

        // Saves the link (replacing this store's old one on that channel) and subscribes it. Returns an error, or null.
        public static async Task<string> Save(int storeId, string ch, string accountId, string name, string token, string businessId)
        {
            string secret;
            using (var c = Db.Open())
            {
                var other = LinkFor(c, ch, accountId);
                if (other != null && other.StoreId != storeId)
                    return ch == Fb ? "الصفحة دي مربوطة بمتجر تاني على Sellybot. لو ده متجرك القديم، شيل الربط منه الأول." : "حساب الإنستجرام ده مربوط بمتجر تاني على Sellybot. لو ده متجرك القديم، شيل الربط منه الأول.";
                secret = Secret(c);
            }
            var why = await Subscribe(ch, accountId, token, secret);
            using (var c = Db.Open())
            {
                using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.MetaLinks WHERE StoreId=@s AND Channel=@ch;
                    INSERT INTO dbo.MetaLinks(StoreId, Channel, AccountId, Name, Token, BusinessId, Enabled, Subscribed, Status) VALUES(@s, @ch, @a, @n, @t, @b, 1, @sub, @st);
                    DELETE FROM dbo.MetaConnects WHERE StoreId=@s AND Channel=@ch;",
                    "@s", storeId, "@ch", ch, "@a", accountId, "@n", (object)Cut(name, 200) ?? DBNull.Value, "@t", Secrets.Protect(token),
                    "@b", (object)businessId ?? DBNull.Value, "@sub", why == null,
                    "@st", why == null ? (object)DBNull.Value : Cut("⚠️ الربط تم، بس فيسبوك ماوافقش يبعتلنا الرسايل: " + why, 400)))
                    cmd.ExecuteNonQuery();
            }
            return null;
        }

        public static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }

        // Where a customer opens a chat with this account
        public static string ChatUrl(MetaLink l)
        {
            if (l == null) return null;
            if (l.Channel == Fb) return "https://m.me/" + l.AccountId;
            return string.IsNullOrEmpty(l.Name) ? null : "https://ig.me/m/" + l.Name.TrimStart('@');
        }
    }

    // ---------- the assistant on Messenger / Instagram ----------
    public static class MetaBot
    {
        static readonly TimeSpan Debounce = TimeSpan.FromSeconds(4);
        const string Fallback = "مقدرش أساعد في ده 🙏 بس اسألني عن أي حاجة تخص المتجر وأنا أساعدك.";

        public class Incoming { public string Customer, Text, Mid, AppId, ImageUrl, MediaType; public bool Echo, Deleted, HasMedia; }

        // one entry of entry[].messaging[]
        public static Incoming Parse(JsonElement ev)
        {
            var x = new Incoming();
            var msg = WaJson.O(ev, "message");
            var post = WaJson.O(ev, "postback");
            string sender = WaJson.O(ev, "sender").HasValue ? WaJson.S(WaJson.O(ev, "sender").Value, "id") : null;
            string recipient = WaJson.O(ev, "recipient").HasValue ? WaJson.S(WaJson.O(ev, "recipient").Value, "id") : null;
            if (msg.HasValue)
            {
                var m = msg.Value;
                x.Echo = WaJson.B(m, "is_echo") ?? false;
                x.Deleted = WaJson.B(m, "is_deleted") ?? false;
                x.Mid = WaJson.S(m, "mid");
                x.AppId = WaJson.S(m, "app_id");
                x.Text = WaJson.S(m, "text");
                JsonElement atts;
                if (m.TryGetProperty("attachments", out atts) && atts.ValueKind == JsonValueKind.Array)
                    foreach (var a in atts.EnumerateArray())
                    {
                        x.HasMedia = true;
                        string type = (WaJson.S(a, "type") ?? "").ToLowerInvariant();
                        var payload = WaJson.O(a, "payload");
                        string url = payload.HasValue ? WaJson.S(payload.Value, "url") : null;
                        if (type == "image" && x.ImageUrl == null && !string.IsNullOrEmpty(url)) x.ImageUrl = url;
                        else if (x.MediaType == null && type != "image") x.MediaType = type;
                        // a shared post / story mention arrives with a link and no text: its title, if any, is enough to talk about
                        if (x.Text == null && (type == "share" || type == "story_mention" || type == "ig_reel" || type == "reel" || type == "fallback"))
                            x.Text = payload.HasValue ? WaJson.S(payload.Value, "title") : null;
                    }
            }
            else if (post.HasValue)
            {
                x.Mid = WaJson.S(post.Value, "mid");
                x.Text = WaJson.S(post.Value, "title", "payload");   // a button or ice-breaker the customer pressed
            }
            x.Customer = x.Echo ? recipient : sender;
            return x;
        }

        public static void Handle(SqlConnection c, StoreInfo s, MetaLink link, Incoming m, string appId)
        {
            if (string.IsNullOrEmpty(m.Customer) || m.Customer == link.AccountId || m.Deleted) return;
            string ch = link.Channel, chat = m.Customer;
            string text = (m.Text ?? "").Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);

            if (m.Echo)
            {
                // what we sent ourselves comes back as an echo: ignore it. Anything else is the owner answering by hand
                // (from the Page inbox / the Instagram app): the bot steps aside in this chat for a while.
                if (!string.IsNullOrEmpty(m.AppId) && m.AppId == appId) return;
                if (text.Length > 0 && IsOurs(c, s.Id, ch, chat, text)) return;
                Save(c, s.Id, ch, chat, false, text.Length > 0 ? text : "[ملف/صورة من المتجر]", "owner:" + (m.Mid ?? Guid.NewGuid().ToString("N")));
                return;
            }

            bool image = !string.IsNullOrEmpty(m.ImageUrl) && WaImages.SafeUrl(m.ImageUrl);
            if (text.Length == 0 && !image)
            {
                if (!m.HasMedia) return;   // reactions, reads...
                if (!link.Enabled || !Web.Allow("meta-media:" + s.Id + ":" + ch + ":" + chat, 1, TimeSpan.FromHours(1))) return;
                string say = WaImages.MediaReply(m.MediaType == "audio" ? "audio" : m.MediaType == "video" ? "video" : m.MediaType == "file" ? "document" : "", null);
                int storeId = s.Id; var l = link;
                HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    try { string secret; using (var c2 = Db.Open()) secret = MetaApp.Secret(c2); await Send(l, chat, say, secret); }
                    catch (Exception ex) { Log.Error(ex); }
                });
                return;
            }

            long? row = Save(c, s.Id, ch, chat, true, text.Length > 0 ? text : WaImages.Placeholder, m.Mid, image ? m.ImageUrl : null);
            if (row == null || !link.Enabled) return;   // a duplicate delivery / the bot is off here (the message stays as context)
            long rowId = row.Value; int sid = s.Id;
            HostingEnvironment.QueueBackgroundWorkItem(ct => Reply(sid, ch, chat, rowId, ct));
        }

        // null = already have it (same message id, or the same text from the same customer in the last minute)
        public static long? Save(SqlConnection c, int storeId, string ch, string chat, bool fromCustomer, string body, string msgId, string media = null)
        {
            const string sql = @"
IF @fc = 1 AND @md IS NULL AND EXISTS (SELECT 1 FROM dbo.MetaMessages WHERE StoreId=@s AND Channel=@ch AND ChatId=@c AND FromCustomer=1 AND Body=@b AND CreatedAt > DATEADD(SECOND,-60,SYSUTCDATETIME()))
  SELECT CAST(NULL AS BIGINT);
ELSE
  INSERT INTO dbo.MetaMessages(StoreId, Channel, ChatId, FromCustomer, Body, MsgId, Media) OUTPUT INSERTED.Id VALUES(@s, @ch, @c, @fc, @b, @m, @md);";
            try
            {
                using (var cmd = Db.Cmd(c, sql, "@s", storeId, "@ch", ch, "@c", MetaApp.Cut(chat, 64), "@fc", fromCustomer, "@b", MetaApp.Cut(body, 4000),
                    "@m", (object)MetaApp.Cut(msgId, 150) ?? DBNull.Value, "@md", (object)MetaApp.Cut(media, 800) ?? DBNull.Value))
                {
                    var v = cmd.ExecuteScalar();
                    return v == null || v == DBNull.Value ? (long?)null : Convert.ToInt64(v);
                }
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return null; }
        }

        static bool IsOurs(SqlConnection c, int storeId, string ch, string chat, string text)
        {
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(*) FROM dbo.MetaMessages WHERE StoreId=@s AND Channel=@ch AND ChatId=@c AND FromCustomer=0 AND MsgId IS NULL
                AND CreatedAt > DATEADD(MINUTE,-30,SYSUTCDATETIME()) AND CHARINDEX(@t, Body) > 0", "@s", storeId, "@ch", ch, "@c", chat, "@t", text))
                return (int)cmd.ExecuteScalar() > 0;
        }

        class Row { public long Id; public bool Customer, Owner; public string Body, Media; public DateTime At; }

        internal static async Task Reply(int storeId, string ch, string chat, long rowId, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Debounce, ct);
                // the database in short steps, never while waiting on the network (see WhatsAppBot.Reply)
                List<Row> rows; StoreInfo s; MetaLink link; string secret; List<ChatHandler.ExtraTool> extra;
                using (var c = Db.Open())
                {
                    rows = new List<Row>();
                    using (var cmd = Db.Cmd(c, @"SELECT TOP 40 Id, FromCustomer, Body, MsgId, Media, CreatedAt FROM dbo.MetaMessages
                        WHERE StoreId=@s AND Channel=@ch AND ChatId=@c AND CreatedAt > DATEADD(HOUR,-24,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", storeId, "@ch", ch, "@c", chat))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) rows.Add(new Row { Id = (long)r["Id"], Customer = (bool)r["FromCustomer"], Body = (string)r["Body"], Media = r["Media"] as string,
                            Owner = (r["MsgId"] as string ?? "").StartsWith("owner:"), At = DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc) });
                    if (rows.Count == 0 || rows[0].Id != rowId) return;                                           // a newer message answers everything
                    if (rows.Any(x => x.Owner && x.At > DateTime.UtcNow.AddHours(-WhatsAppBot.PauseHours))) return;   // the owner is talking to them

                    s = Db.StoreById(c, storeId);
                    link = MetaApp.Link(c, storeId, ch);
                    if (s == null || s.Status != "active" || link == null || !link.Enabled || link.Token == null) return;
                    secret = MetaApp.Secret(c);
                    string where = MetaApp.Label(ch);
                    if (!PlanGate.Allows(c, s, MetaApp.Feature(ch))) { MetaApp.SetStatus(c, s.Id, ch, "⚠️ باقتك مش فيها المساعد على " + where + "، فهو مش بيرد هناك لحد ما ترقّي من «باقتي»."); return; }
                    if (Billing.Expired(s)) { MetaApp.SetStatus(c, s.Id, ch, "⚠️ الباقة خلصت، فالمساعد مش بيرد على " + where + " لحد ما تجدّدها."); return; }
                    if (Billing.LimitReached(c, s)) { MetaApp.SetStatus(c, s.Id, ch, "⚠️ رصيد الشهر خلص، فالمساعد مش بيرد على " + where + " لحد ما تزوّد الرصيد أو يبدأ شهر جديد."); return; }
                    if (!Web.Allow("meta-chat:" + s.Id + ":" + ch + ":" + chat, 30, TimeSpan.FromHours(1))) return;   // one customer can't burn the quota
                    PlanGate.Apply(c, s);
                    try { Shopify.RefreshIfOld(s); } catch (Exception ex) { Log.Error(ex); }
                    try { EasyOrders.RefreshIfOld(s); } catch (Exception ex) { Log.Error(ex); }
                    extra = new List<ChatHandler.ExtraTool>();
                    if (Handoff.On(c, s)) extra.Add(Handoff.Tool(s, ch, chat));   // the customer's number isn't known here: the assistant asks for it
                }

                var imgs = new Dictionary<long, ImageBlockParam>();
                foreach (var x in rows.TakeWhile(x => x.Customer).Where(x => x.Media != null).Take(WaImages.MaxPerTurn))
                {
                    var img = await WaImages.Load(x.Media, null);
                    if (img != null) imgs[x.Id] = img;
                }

                ChatHandler.LoopResult res;
                try
                {
                    ChatHandler.Prepared prep;
                    using (var c = Db.Open())
                        prep = ChatHandler.Prepare(c, s, History(rows, imgs), ch + ":" + chat, ChannelNote(ch, s) + (imgs.Count > 0 ? WaImages.Note : ""), extra);
                    res = await ChatHandler.Run(prep);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    using (var c = Db.Open()) MetaApp.SetStatus(c, s.Id, ch, "⚠️ حصلت مشكلة وإحنا بنجهّز رد على آخر رسالة. لو اتكررت كلّم الدعم.");
                    return;
                }

                string answer = Plain(string.IsNullOrWhiteSpace(res.Text) ? Fallback : res.Text);
                long? mine;
                using (var c = Db.Open())
                {
                    Db.AddUsage(c, s.Id, 1, res.In, res.CacheRead, res.CacheWrite, res.Out);
                    mine = Save(c, s.Id, ch, chat, false, answer, null);   // saved before sending: the echo may come back before Send returns
                }
                string failed = null;
                try { await Send(link, chat, answer, secret); }
                catch (MetaException ex) { failed = "⚠️ " + ex.Message; }
                using (var c = Db.Open())
                {
                    if (failed != null && mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.MetaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                    MetaApp.SetStatus(c, s.Id, ch, failed);
                    if (new Random().Next(200) == 0) using (var cmd = Db.Cmd(c, "DELETE TOP (5000) FROM dbo.MetaMessages WHERE CreatedAt < DATEADD(DAY,-60,SYSUTCDATETIME())")) cmd.ExecuteNonQuery();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error(ex); }
        }

        // Messenger: /<page>/messages with the Page token; Instagram: /<ig account>/messages with the business's token
        public static async Task Send(MetaLink link, string chat, string text, string secret)
        {
            foreach (var part in Split(text, link.Channel))
            {
                object body = link.Channel == MetaApp.Fb
                    ? (object)new { recipient = new { id = chat }, messaging_type = "RESPONSE", message = new { text = part } }
                    : new { recipient = new { id = chat }, message = new { text = part } };
                await MetaApp.Call(HttpMethod.Post, Uri.EscapeDataString(link.AccountId) + "/messages", link.Token, secret, body);
            }
        }

        // Messenger takes up to 2000 characters a message; Instagram up to 1000 bytes (UTF-8: an Arabic letter is 2)
        public static List<string> Split(string text, string ch)
        {
            Func<string, bool> fits = ch == MetaApp.Fb ? (Func<string, bool>)(t => t.Length <= 1900) : (t => Encoding.UTF8.GetByteCount(t) <= 950);
            var parts = new List<string>();
            var cur = new StringBuilder();
            foreach (var para in text.Replace("\r", "").Split('\n'))
            {
                var line = para;
                while (!fits(line))
                {
                    int cut = line.Length;
                    while (cut > 1 && !fits(line.Substring(0, cut))) cut = (int)(cut * 0.9);
                    int sp = line.LastIndexOf(' ', Math.Max(0, cut - 1));
                    if (sp > cut / 2) cut = sp;
                    if (cur.Length > 0) { parts.Add(cur.ToString().Trim()); cur.Clear(); }
                    parts.Add(line.Substring(0, cut).Trim());
                    line = line.Substring(cut);
                }
                string next = cur.Length == 0 ? line : cur + "\n" + line;
                if (fits(next)) { cur.Clear(); cur.Append(next); }
                else { parts.Add(cur.ToString().Trim()); cur.Clear(); cur.Append(line); }
            }
            if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString().Trim());
            return parts.Where(p => p.Length > 0).ToList();
        }

        // Messenger and Instagram show text as is: no Markdown
        public static string Plain(string md)
        {
            var t = (md ?? "").Replace("\r", "");
            t = Regex.Replace(t, @"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", m => m.Groups[1].Value.Trim() == m.Groups[2].Value ? m.Groups[2].Value : m.Groups[1].Value + ": " + m.Groups[2].Value);
            t = Regex.Replace(t, @"\*\*(.+?)\*\*|__(.+?)__", m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            t = Regex.Replace(t, @"(?<![\w*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\w*])", "$1");   // *emphasis* (a lone * in "5 * 2" stays)
            t = Regex.Replace(t, @"(?m)^#{1,6}\s*", "");
            t = Regex.Replace(t, @"(?m)^(\s*)[-*]\s+", "$1• ");
            t = Regex.Replace(t, @"`([^`\n]+)`", "$1");
            t = Regex.Replace(t, @"\n{3,}", "\n\n");
            return t.Trim();
        }

        class Turn { public bool Customer; public string Text; public List<ImageBlockParam> Images = new List<ImageBlockParam>(); }

        static List<MessageParam> History(List<Row> newestFirst, Dictionary<long, ImageBlockParam> imgs)
        {
            var turns = new List<Turn>();
            foreach (var r in Enumerable.Reverse(newestFirst))
            {
                Turn t;
                if (turns.Count > 0 && turns[turns.Count - 1].Customer == r.Customer) { t = turns[turns.Count - 1]; t.Text += "\n" + r.Body; }
                else turns.Add(t = new Turn { Customer = r.Customer, Text = r.Body });
                ImageBlockParam img;
                if (r.Customer && imgs.TryGetValue(r.Id, out img)) t.Images.Add(img);
            }
            if (turns.Count > 12) turns = turns.Skip(turns.Count - 12).ToList();
            while (turns.Count > 0 && !turns[0].Customer) turns.RemoveAt(0);
            var list = new List<MessageParam>();
            foreach (var t in turns)
            {
                var body = t.Text.Length > 4000 ? t.Text.Substring(t.Text.Length - 4000) : t.Text;
                if (!t.Customer) list.Add(new AssistantMessageParam { Content = body });
                else if (t.Images.Count == 0) list.Add(new UserMessageParam { Content = body });
                else
                {
                    var blocks = new List<ContentBlockParam>();
                    foreach (var img in t.Images) blocks.Add(img);
                    blocks.Add(new TextBlockParam { Text = body });
                    list.Add(new UserMessageParam { Content = blocks });
                }
            }
            return list;
        }

        static string ChannelNote(string ch, StoreInfo s)
        {
            var sb = new StringBuilder("المحادثة دي على ").Append(ch == MetaApp.Fb ? "ماسنجر (صفحة المتجر على فيسبوك)" : "إنستجرام (رسايل حساب المتجر)").Append("، مش على الموقع:\n")
                .Append("- خلي ردودك قصيرة زي رسايل الشات العادية.\n")
                .Append("- اكتب كلام عادي من غير أي تنسيق: من غير نجوم ولا # ولا جداول. للقوائم استخدم • أو أرقام.\n")
                .Append("- اكتب اللينكات كاملة (https://...) مش [نص](لينك).\n")
                .Append("- مش معاك رقم الزبون. لو احتجته (تتبع أوردر، أو صاحب المتجر يكلّمه) اطلبه منه.\n")
                .Append("- لو فيه رسايل في المحادثة من المتجر نفسه (صاحب المتجر رد بنفسه)، كمّل على كلامه وماتناقضهوش.");
            // Sellybot's own Page: its instructions talk about forms inside the site chat (sign up, plans, tickets) and the visitor's
            // own account, and neither exists here. Links instead.
            if (s != null && s.Slug == SiteBot.Slug)
                sb.Append("\n- مافيش هنا فورمز جوه الشات، ومش بتشوف حساب حد. بدل الفورم ابعت اللينك:\n")
                  .Append("  • حساب جديد وتجربة مجانية: https://sellybot.online/dashboard.html?signup=1\n")
                  .Append("  • الأسعار والباقات: https://sellybot.online/pricing.html (والاشتراك من «باقتي» في لوحة التحكم بعد تسجيل الدخول)\n")
                  .Append("  • مشكلة في حسابه أو دفع أو أي حاجة محتاجة متابعة: واتساب الدعم https://wa.me/201229391155");
            return sb.ToString();
        }
    }

    // api/meta-webhook.ashx — Meta posts every Messenger / Instagram message here (GET = the verification handshake)
    public class MetaWebhookHandler : IHttpHandler
    {
        static DateTime lastBadSig = DateTime.MinValue;
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { res.StatusCode = 503; return; }
            if (!Web.Allow("meta-hook:" + Web.Ip(req), 1200, TimeSpan.FromMinutes(1))) { res.StatusCode = 429; return; }
            try
            {
                using (var c = Db.Open())
                {
                    if (req.HttpMethod == "GET")
                    {
                        string want = MetaApp.VerifyToken(c, false), got = req.QueryString["hub.verify_token"] ?? "";
                        if (req.QueryString["hub.mode"] == "subscribe" && !string.IsNullOrEmpty(want) && Secrets.Same(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(got)))
                        {
                            res.ContentType = "text/plain";
                            res.Write(req.QueryString["hub.challenge"] ?? "");
                            return;
                        }
                        res.StatusCode = 403; return;
                    }
                    if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }

                    byte[] raw;
                    using (var ms = new MemoryStream()) { req.InputStream.CopyTo(ms); raw = ms.ToArray(); }
                    if (raw.Length > 1024 * 1024) { res.StatusCode = 413; return; }
                    if (!MetaApp.SignatureOk(MetaApp.Secret(c), raw, req.Headers["X-Hub-Signature-256"]))
                    {
                        if (DateTime.UtcNow - lastBadSig > TimeSpan.FromMinutes(10)) { lastBadSig = DateTime.UtcNow; Log.Error(new Exception("Meta webhook: bad or missing signature")); }
                        res.StatusCode = 401; return;
                    }
                    JsonElement root;
                    try { using (var doc = JsonDocument.Parse(raw)) root = doc.RootElement.Clone(); }
                    catch { res.StatusCode = 400; return; }

                    string obj = WaJson.S(root, "object") ?? "";
                    string ch = obj == "page" ? MetaApp.Fb : obj == "instagram" ? MetaApp.Ig : null;
                    JsonElement entries;
                    if (ch != null && root.TryGetProperty("entry", out entries) && entries.ValueKind == JsonValueKind.Array)
                    {
                        string appId = MetaApp.AppId(c);
                        foreach (var entry in entries.EnumerateArray().Take(50))
                        {
                            var link = MetaApp.LinkFor(c, ch, WaJson.S(entry, "id") ?? "");
                            if (link == null) continue;                 // an account no store has linked (any more)
                            var s = Db.StoreById(c, link.StoreId);
                            if (s == null) continue;
                            JsonElement events;
                            if (!entry.TryGetProperty("messaging", out events) || events.ValueKind != JsonValueKind.Array) continue;
                            foreach (var ev in events.EnumerateArray().Take(50))
                                try { MetaBot.Handle(c, s, link, MetaBot.Parse(ev), appId); } catch (Exception ex) { Log.Error(ex); }
                        }
                    }
                    Web.Json(res, 200, new { ok = true });
                }
            }
            catch (Exception ex) { Log.Error(ex); res.StatusCode = 500; }
        }
    }

    // api/meta-connect.ashx — ?start=fb|ig sends the signed-in owner to Meta's login; Meta sends them back here with a code
    public class MetaConnectHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        // back to the owner's «مسنجر وإنستجرام» tab, or to admin.html when the super admin linked Sellybot's own Page
        static void Back(HttpResponse res, bool admin, string key, string message = null)
        {
            res.Redirect((admin ? "../admin.html" : "../dashboard.html") + "?meta=" + Uri.EscapeDataString(key)
                + (message != null ? "&metaMsg=" + Uri.EscapeDataString(MetaApp.Cut(message, 500)) : "") + (admin ? "#meta" : "#social"), false);
        }

        // ?start=fb|ig — from the owner's dashboard; ?start=fb|ig&site=1 — the super admin links Sellybot's own store (SiteBot.cs),
        // which nobody signs in to. Meta comes back here with ?code&state, and the state says which of the two started it.
        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { res.StatusCode = 503; return; }
            bool admin = false;
            try
            {
                string start = (req.QueryString["start"] ?? "").ToLowerInvariant();
                int stateStore = 0; string ch = null;
                if (start.Length > 0) admin = req.QueryString["site"] == "1";
                else if (!MetaApp.ReadState(req.QueryString["state"], out stateStore, out ch, out admin)) { Back(res, false, "error", "الرابط ده قديم أو مش سليم. ابدأ الربط تاني من «مسنجر وإنستجرام»."); return; }

                StoreInfo s; string appId, secret;
                using (var c = Db.Open())
                {
                    if (admin)
                    {
                        if (!MetaAdminHandler.SignedIn(ctx, c))
                        {
                            // the admin cookie is SameSite=Strict, so it stays behind when Facebook sends the browser back: go once more from our own page
                            if (start.Length == 0 && req.QueryString["again"] != "1") { Again(req, res); return; }
                            res.Redirect("../admin.html", false); return;
                        }
                        s = Db.StoreBySlug(c, SiteBot.Slug);
                        if (s == null) { Back(res, true, "error", "متجر Sellybot لسه مااتعملش. افتح الموقع مرة وجرّب تاني."); return; }
                    }
                    else
                    {
                        s = Me.Require(ctx, c);
                        if (s == null) { res.Redirect("../dashboard.html", false); return; }
                    }
                    appId = MetaApp.AppId(c); secret = MetaApp.Secret(c);

                    if (start.Length > 0)
                    {
                        if (!MetaApp.Valid(start)) { Back(res, admin, "error", "طلب مش مفهوم."); return; }
                        if (!MetaApp.OpenFor(c, s, start)) { Back(res, admin, "error", admin ? "كمّل إعدادات تطبيق Meta الأول (App ID و Secret و Configuration ID)." : "الربط بـ" + MetaApp.Label(start) + " لسه مش متاح. هيتفتح قريب."); return; }
                        if (!PlanGate.Allows(c, s, MetaApp.Feature(start))) { Back(res, admin, "locked"); return; }
                        if (!Web.Allow("meta-start:" + s.Id, 20, TimeSpan.FromHours(1))) { Back(res, admin, "error", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                        res.Redirect(MetaApp.DialogUrl(c, req, start, MetaApp.State(s.Id, start, admin)), false);
                        return;
                    }
                }

                if (stateStore != s.Id) { Back(res, admin, "error", "انت داخل بحساب متجر غير اللي بدأ الربط. ادخل بالمتجر الصح وابدأ تاني."); return; }
                if (!string.IsNullOrEmpty(req.QueryString["error"])) { Back(res, admin, "cancel"); return; }
                string code = (req.QueryString["code"] ?? "").Trim();
                if (code.EndsWith("#_")) code = code.Substring(0, code.Length - 2);
                if (code.Length == 0) { Back(res, admin, "cancel"); return; }

                try
                {
                    var tok = await MetaApp.Call(HttpMethod.Get, "oauth/access_token?client_id=" + Uri.EscapeDataString(appId) + "&client_secret=" + Uri.EscapeDataString(secret)
                        + "&redirect_uri=" + Uri.EscapeDataString(MetaApp.RedirectUri(req)) + "&code=" + Uri.EscapeDataString(code), null, null);
                    string token = WaJson.S(tok, "access_token");
                    if (string.IsNullOrEmpty(token)) { Back(res, admin, "error", "فيسبوك مارجّعش صلاحية. جرّب تاني."); return; }
                    var choices = ch == MetaApp.Fb ? await Pages(token, appId, secret) : await IgAccounts(token, appId, secret);
                    if (choices.Count == 0)
                    {
                        Back(res, admin, "error", ch == MetaApp.Fb
                            ? "مالقيناش صفحة فيسبوك انت أدمن عليها. لو عندك صفحة، ابدأ تاني واختارها لما فيسبوك يسألك."
                            : "مالقيناش حساب إنستجرام. لازم يكون حساب Professional (Business أو Creator)، واختاره لما فيسبوك يسألك.");
                        return;
                    }
                    if (choices.Count == 1)
                    {
                        var one = choices[0];
                        var err = await MetaApp.Save(s.Id, ch, one.Id, one.Name, one.Token, one.Business);
                        if (err != null) { Back(res, admin, "error", err); return; }
                        Back(res, admin, "ok_" + ch);
                        return;
                    }
                    // several Pages / accounts: the owner picks one in the dashboard (the tokens wait here, encrypted, for 30 minutes)
                    using (var c = Db.Open())
                    using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.MetaConnects WHERE StoreId=@s AND Channel=@ch;
                        INSERT INTO dbo.MetaConnects(StoreId, Channel, Choices, ExpiresAt) VALUES(@s, @ch, @j, DATEADD(MINUTE,30,SYSUTCDATETIME()));",
                        "@s", s.Id, "@ch", ch, "@j", Secrets.Protect(JsonSerializer.Serialize(choices.Take(50).Select(x => new { id = x.Id, name = x.Name, token = x.Token, business = x.Business })))))
                        cmd.ExecuteNonQuery();
                    Back(res, admin, "pick_" + ch);
                }
                catch (MetaException ex) { Back(res, admin, "error", ex.Message); }
            }
            catch (Exception ex) { Log.Error(ex); Back(res, admin, "error", "حصلت مشكلة وإحنا بنربط. جرّب تاني."); }
        }

        // A page of ours that opens the same callback again: a navigation that starts on our own site carries the strict admin cookie
        static void Again(HttpRequest req, HttpResponse res)
        {
            string a = HttpUtility.HtmlAttributeEncode(req.Url.AbsolutePath + req.Url.Query + (req.Url.Query.Length > 0 ? "&" : "?") + "again=1");
            res.ContentType = "text/html"; res.Charset = "utf-8";
            res.AddHeader("Referrer-Policy", "no-referrer");
            res.Write("<!doctype html><html lang=\"ar\" dir=\"rtl\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">"
                + "<meta http-equiv=\"refresh\" content=\"0;url=" + a + "\"><title>Sellybot</title></head>"
                + "<body style=\"font-family:system-ui,sans-serif;text-align:center;margin-top:3em\"><a href=\"" + a + "\">لحظة… لو الصفحة ماتحركتش دوس هنا</a></body></html>");
        }

        public class Choice { public string Id, Name, Token, Business; }

        // The Pages this person manages, each with its own token (from a long-lived user token, so the Page token doesn't expire)
        static async Task<List<Choice>> Pages(string userToken, string appId, string secret)
        {
            string token = userToken;
            try
            {
                var ll = await MetaApp.Call(HttpMethod.Get, "oauth/access_token?grant_type=fb_exchange_token&client_id=" + Uri.EscapeDataString(appId)
                    + "&client_secret=" + Uri.EscapeDataString(secret) + "&fb_exchange_token=" + Uri.EscapeDataString(userToken), null, null);
                token = WaJson.S(ll, "access_token") ?? userToken;
            }
            catch (MetaException) { }   // a system-user token (Login for Business) doesn't exchange: it's long-lived already
            var list = new List<Choice>();
            JsonElement data;
            var r = await MetaApp.Call(HttpMethod.Get, "me/accounts?fields=id,name,access_token,tasks&limit=100", token, secret);
            if (r.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array)
                foreach (var p in data.EnumerateArray())
                {
                    string id = WaJson.S(p, "id"), name = WaJson.S(p, "name"), pt = WaJson.S(p, "access_token");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(pt)) list.Add(new Choice { Id = id, Name = name ?? id, Token = pt });
                }
            return list;
        }

        // Unified Login: the business's system-user token reaches the Instagram accounts the owner picked in Facebook's dialog.
        // Those accounts are the target_ids of the token's instagram_* grants (debug_token), which needs no extra permission.
        // me/assigned_instagram_accounts is only the fallback: on the real app it answered "(#100) Requires instagram_basic
        // permission to manage the object" (it wants business_management on the configuration).
        static async Task<List<Choice>> IgAccounts(string token, string appId, string secret)
        {
            string business = null;
            try { business = WaJson.S(await MetaApp.Call(HttpMethod.Get, "me?fields=client_business_id", token, secret), "client_business_id"); }
            catch (MetaException) { }
            var ids = new List<string>(); var scopes = new List<string>();
            try
            {
                JsonElement d, arr, t;
                var dbg = await MetaApp.Call(HttpMethod.Get, "debug_token?input_token=" + Uri.EscapeDataString(token), appId + "|" + secret, null);
                if (dbg.TryGetProperty("data", out d) && d.ValueKind == JsonValueKind.Object)
                {
                    if (d.TryGetProperty("scopes", out arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var x in arr.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) scopes.Add(x.GetString());
                    if (d.TryGetProperty("granular_scopes", out arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var g in arr.EnumerateArray())
                        {
                            string scope = WaJson.S(g, "scope");
                            if ((scope == "instagram_basic" || scope == "instagram_manage_messages") && g.TryGetProperty("target_ids", out t) && t.ValueKind == JsonValueKind.Array)
                                foreach (var x in t.EnumerateArray())
                                {
                                    string id = x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText();
                                    if (!string.IsNullOrEmpty(id) && id.All(char.IsDigit) && !ids.Contains(id)) ids.Add(id);
                                }
                        }
                }
            }
            catch (MetaException) { }

            var list = new List<Choice>();
            foreach (var id in ids.Take(50))
            {
                string user = null;
                try { user = WaJson.S(await MetaApp.Call(HttpMethod.Get, id + "?fields=id,username", token, secret), "username"); }
                catch (MetaException) { }
                list.Add(new Choice { Id = id, Name = string.IsNullOrEmpty(user) ? id : "@" + user, Token = token, Business = business });
            }
            if (list.Count > 0) return list;

            string why = null;
            try
            {
                JsonElement data;
                var r = await MetaApp.Call(HttpMethod.Get, "me/assigned_instagram_accounts?fields=id,username&limit=100", token, secret);
                if (r.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var a in data.EnumerateArray())
                    {
                        string id = WaJson.S(a, "id"), user = WaJson.S(a, "username");
                        if (!string.IsNullOrEmpty(id)) list.Add(new Choice { Id = id, Name = string.IsNullOrEmpty(user) ? id : "@" + user, Token = token, Business = business });
                    }
            }
            catch (MetaException ex) { why = ex.Message; }
            if (list.Count > 0) return list;
            // nothing: say what Facebook did give us, so the fix (the configuration's permissions, or picking the account) is clear
            throw new MetaException("فيسبوك مابعتش حساب إنستجرام. لازم تختار حساب الإنستجرام في شاشة فيسبوك، والحساب يكون Professional."
                + " الصلاحيات اللي وصلت: " + (scopes.Count > 0 ? string.Join("، ", scopes) : "مش معروفة") + "."
                + (scopes.Contains("instagram_basic") && scopes.Contains("instagram_manage_messages") ? "" : " ناقص instagram_basic و instagram_manage_messages في الـ Configuration بتاع الإنستجرام.")
                + (why != null ? " (" + MetaApp.Cut(why, 160) + ")" : ""));
        }
    }

    // api/meta.ashx — the owner's «مسنجر وإنستجرام» tab: GET = state; POST ?action=pick|enable|unlink|resubscribe
    public class MetaHandler : HttpTaskAsyncHandler
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
                    if (!MetaApp.Visible(c, s)) { Web.Error(res, 404, "not_found"); return; }   // the tab is hidden from this owner
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, State(c, s)); return; }
                }
                if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                if (!Web.Allow("meta:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                if (!await Act(res, s, (req.QueryString["action"] ?? "").ToLowerInvariant(), Web.Body(req, 2000))) return;
                using (var c = Db.Open()) Web.Json(res, 200, State(c, Db.StoreById(c, s.Id)));
            }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }

        // pick | enable | unlink | resubscribe for one channel of a store: the owner's tab, and admin.html for Sellybot's own store.
        // false = an error was already written.
        internal static async Task<bool> Act(HttpResponse res, StoreInfo s, string action, Dictionary<string, object> b)
        {
            string ch = (Web.Str(b, "channel", 4) ?? "").ToLowerInvariant();
            if (!MetaApp.Valid(ch)) { Web.Error(res, 400, "bad_request"); return false; }

            if (action == "pick")
            {
                string id = Web.Str(b, "id", 40) ?? "";
                MetaConnectHandler.Choice pick = null;
                using (var c = Db.Open())
                {
                    if (!PlanGate.Allows(c, s, MetaApp.Feature(ch))) { PlanGate.Deny(res, c, s, MetaApp.Feature(ch)); return false; }
                    using (var cmd = Db.Cmd(c, "SELECT Choices FROM dbo.MetaConnects WHERE StoreId=@s AND Channel=@ch AND ExpiresAt > SYSUTCDATETIME()", "@s", s.Id, "@ch", ch))
                    {
                        var box = cmd.ExecuteScalar() as string;
                        var json = box == null ? null : Secrets.Unprotect(box);
                        if (json != null)
                            pick = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json)
                                .Where(x => x.ContainsKey("id") && x["id"] == id)
                                .Select(x => new MetaConnectHandler.Choice { Id = x["id"], Name = x.ContainsKey("name") ? x["name"] : null, Token = x.ContainsKey("token") ? x["token"] : null, Business = x.ContainsKey("business") ? x["business"] : null })
                                .FirstOrDefault();
                    }
                }
                if (pick == null || string.IsNullOrEmpty(pick.Token)) { Web.Error(res, 400, "expired", "الاختيار ده انتهى. ابدأ الربط تاني."); return false; }
                var err = await MetaApp.Save(s.Id, ch, pick.Id, pick.Name, pick.Token, pick.Business);
                if (err != null) { Web.Error(res, 400, "taken", err); return false; }
            }
            else if (action == "enable")
            {
                object en; bool on = b.TryGetValue("enabled", out en) && en is bool && (bool)en;
                using (var c = Db.Open())
                {
                    if (on && !PlanGate.Allows(c, s, MetaApp.Feature(ch))) { PlanGate.Deny(res, c, s, MetaApp.Feature(ch)); return false; }
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.MetaLinks SET Enabled=@e, Status=NULL WHERE StoreId=@s AND Channel=@ch", "@e", on, "@s", s.Id, "@ch", ch)) cmd.ExecuteNonQuery();
                }
            }
            else if (action == "unlink")
            {
                MetaLink link; string secret;
                using (var c = Db.Open()) { link = MetaApp.Link(c, s.Id, ch); secret = MetaApp.Secret(c); }
                if (link != null && link.Token != null)
                    try { await MetaApp.Call(HttpMethod.Delete, Uri.EscapeDataString(link.AccountId) + "/subscribed_apps", link.Token, secret); }
                    catch (MetaException) { }   // the owner may have removed the app on Facebook already
                using (var c = Db.Open())
                // the token and every message we kept from this account go (privacy.html#meta-delete)
                using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.MetaLinks WHERE StoreId=@s AND Channel=@ch; DELETE FROM dbo.MetaConnects WHERE StoreId=@s AND Channel=@ch;
                    DELETE FROM dbo.MetaMessages WHERE StoreId=@s AND Channel=@ch;", "@s", s.Id, "@ch", ch))
                    cmd.ExecuteNonQuery();
            }
            else if (action == "resubscribe")
            {
                MetaLink link; string secret;
                using (var c = Db.Open()) { link = MetaApp.Link(c, s.Id, ch); secret = MetaApp.Secret(c); }
                if (link == null || link.Token == null) { Web.Error(res, 400, "not_linked", "مفيش ربط. اربط الأول."); return false; }
                var why = await MetaApp.Subscribe(ch, link.AccountId, link.Token, secret);
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "UPDATE dbo.MetaLinks SET Subscribed=@sub, Status=@st WHERE StoreId=@s AND Channel=@ch",
                    "@sub", why == null, "@st", why == null ? (object)DBNull.Value : MetaApp.Cut("⚠️ " + why, 400), "@s", s.Id, "@ch", ch))
                    cmd.ExecuteNonQuery();
                if (why != null) { Web.Error(res, 400, "meta", why); return false; }
            }
            else { Web.Error(res, 400, "bad_request"); return false; }
            return true;
        }

        internal static Dictionary<string, object> State(SqlConnection c, StoreInfo s)
        {
            var d = new Dictionary<string, object>();
            foreach (var ch in new[] { MetaApp.Fb, MetaApp.Ig })
            {
                var l = MetaApp.Link(c, s.Id, ch);
                var one = new Dictionary<string, object> { { "ready", MetaApp.OpenFor(c, s, ch) }, { "allowed", PlanGate.Allows(c, s, MetaApp.Feature(ch)) } };
                if (l != null)
                    one["link"] = new Dictionary<string, object> {
                        { "id", l.AccountId }, { "name", l.Name ?? "" }, { "enabled", l.Enabled }, { "subscribed", l.Subscribed }, { "status", l.Status ?? "" },
                        { "lastAt", l.LastAt.HasValue ? l.LastAt.Value.ToString("o") : null }, { "chatUrl", MetaApp.ChatUrl(l) }, { "tokenOk", l.Token != null } };
                using (var cmd = Db.Cmd(c, "SELECT Choices FROM dbo.MetaConnects WHERE StoreId=@s AND Channel=@ch AND ExpiresAt > SYSUTCDATETIME()", "@s", s.Id, "@ch", ch))
                {
                    var box = cmd.ExecuteScalar() as string;
                    var json = box == null ? null : Secrets.Unprotect(box);
                    if (json != null)
                        one["choices"] = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json)
                            .Select(x => new Dictionary<string, object> { { "id", x.ContainsKey("id") ? x["id"] : "" }, { "name", x.ContainsKey("name") ? x["name"] : "" } }).ToList();
                }
                using (var cmd = Db.Cmd(c, @"SELECT COUNT(DISTINCT ChatId) FROM dbo.MetaMessages WHERE StoreId=@s AND Channel=@ch AND FromCustomer=1
                    AND CreatedAt > DATEADD(DAY,-7,SYSUTCDATETIME())", "@s", s.Id, "@ch", ch))
                    one["chats7d"] = (int)cmd.ExecuteScalar();
                d[ch] = one;
            }
            return d;
        }
    }

    // api/meta-admin.ashx — the super admin's Meta app settings, every linked Page / Instagram account, and Sellybot's own
    // Page / account (the store SiteBot.cs runs, which nobody signs in to): POST ?action=site with {do: pick|enable|unlink|resubscribe}
    public class MetaAdminHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        internal static bool SignedIn(HttpContext ctx, SqlConnection c)
        {
            var adminId = AdminAuth.Current(ctx);
            if (!adminId.HasValue) return false;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Admins WHERE Id=@id", "@id", adminId.Value)) return (int)cmd.ExecuteScalar() > 0;
        }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    if (!SignedIn(ctx, c)) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, State(c, req)); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                    var b = Web.Body(req, 4000);

                    if (action == "settings")
                    {
                        string appId = (Web.Str(b, "appId", 40) ?? "").Trim(), secret = (Web.Str(b, "secret", 200) ?? "").Trim();
                        string fbConfig = (Web.Str(b, "fbConfig", 40) ?? "").Trim(), igConfig = (Web.Str(b, "igConfig", 40) ?? "").Trim();
                        Func<string, bool> digits = v => v.Length == 0 || Regex.IsMatch(v, "^[0-9]{5,30}$");
                        if (appId.Length == 0 || !digits(appId)) { Web.Error(res, 400, "bad_app", "الـ App ID أرقام بس. هتلاقيه في App settings ← Basic."); return; }
                        if (!digits(fbConfig) || !digits(igConfig)) { Web.Error(res, 400, "bad_config", "الـ Configuration ID أرقام بس. هتلاقيه في Facebook Login for Business ← Configurations."); return; }
                        if (secret.Length > 0 && !Regex.IsMatch(secret, "^[0-9a-fA-F]{20,64}$")) { Web.Error(res, 400, "bad_secret", "الـ App Secret مش مظبوط. انسخه زي ما هو من App settings ← Basic (Show)."); return; }
                        string useSecret = secret.Length > 0 ? secret : MetaApp.Secret(c);
                        if (useSecret != null)
                        {
                            // the App ID and secret go together: an app token (id|secret) can read the app itself
                            try { await MetaApp.Call(HttpMethod.Get, Uri.EscapeDataString(appId) + "?fields=id,name", appId + "|" + useSecret, null); }
                            catch (MetaException ex) { Web.Error(res, 400, "meta", "فيسبوك ماقبلش الـ App ID والـ Secret مع بعض: " + ex.Message); return; }
                        }
                        Billing.Set(c, "MetaAppId", appId);
                        if (secret.Length > 0) Billing.Set(c, "MetaAppSecret", Secrets.Protect(secret));
                        Billing.Set(c, "MetaFbConfig", fbConfig);
                        Billing.Set(c, "MetaIgConfig", igConfig);
                        object lv; bool live = b.TryGetValue("live", out lv) && lv is bool && (bool)lv;
                        Billing.Set(c, "MetaLive", live ? "1" : "0");
                        string testers = string.Join(",", (Web.Str(b, "testers", 1000) ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => t.Trim().ToLowerInvariant()).Where(t => Web.SlugRx.IsMatch(t)).Distinct().Take(50));
                        Billing.Set(c, "MetaTesters", testers);
                        MetaApp.VerifyToken(c, true);
                        Web.Json(res, 200, State(c, req));
                        return;
                    }
                    if (action == "site")
                    {
                        var site = Db.StoreBySlug(c, SiteBot.Slug);
                        if (site == null) { Web.Error(res, 400, "no_site", "متجر Sellybot لسه مااتعملش."); return; }
                        if (!await MetaHandler.Act(res, site, (Web.Str(b, "do", 20) ?? "").ToLowerInvariant(), b)) return;
                        Web.Json(res, 200, State(c, req));
                        return;
                    }
                    Web.Error(res, 400, "bad_request");
                }
            }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }

        static Dictionary<string, object> State(SqlConnection c, HttpRequest req)
        {
            var site = Db.StoreBySlug(c, SiteBot.Slug);   // Sellybot's own Page / account, linked from here
            var links = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 300 l.Channel, l.AccountId, l.Name, l.Enabled, l.Subscribed, l.Status, l.LastAt, l.CreatedAt, s.Slug, s.Name AS StoreName
                FROM dbo.MetaLinks l JOIN dbo.Stores s ON s.Id = l.StoreId ORDER BY l.CreatedAt DESC"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    links.Add(new Dictionary<string, object> {
                        { "channel", r["Channel"] }, { "account", r["AccountId"] }, { "name", r["Name"] as string ?? "" }, { "enabled", r["Enabled"] }, { "subscribed", r["Subscribed"] },
                        { "status", r["Status"] as string ?? "" }, { "lastAt", r["LastAt"] is DateTime ? ((DateTime)r["LastAt"]).ToString("o") + "Z" : null },
                        { "store", r["StoreName"] }, { "slug", r["Slug"] } });
            return new Dictionary<string, object> {
                { "appId", MetaApp.AppId(c) ?? "" }, { "hasSecret", MetaApp.Secret(c) != null },
                { "fbConfig", MetaApp.Config(c, MetaApp.Fb) ?? "" }, { "igConfig", MetaApp.Config(c, MetaApp.Ig) ?? "" },
                { "verifyToken", MetaApp.VerifyToken(c, true) }, { "hookUrl", MetaApp.HookUrl(req) }, { "redirectUri", MetaApp.RedirectUri(req) },
                { "live", MetaApp.Live(c) }, { "testers", MetaApp.Testers(c) },
                { "version", MetaApp.Version }, { "links", links }, { "site", site != null ? MetaHandler.State(c, site) : null } };
        }
    }
}
