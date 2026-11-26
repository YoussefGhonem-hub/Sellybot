// Telegram: the store's own Telegram bot answers customers with the same assistant as the site chat, WhatsApp and Messenger
// (same data, rules, tools and monthly quota). The user asked for it on 2026-11-23, with a link that makes the bot by itself.
//
// Two ways for an owner to link a bot:
// - Made for them (Telegram "managed bots", Bot API 9.6, April 2026). Sellybot has one manager bot: the super admin pastes its
//   token in admin.html ← «تيليجرام», and we switch its bot management on with setMySettings(can_manage_bots).
//   1. The owner presses «اعمل بوت متجرك» in the dashboard and gets t.me/<manager>?start=<code>.
//   2. They press Start. The code (dbo.TgStarts) says which store; the Telegram account that pressed Start is the one
//      whose new bot counts.
//   3. The manager answers with a request_managed_bot button, with the store's name and a username filled in.
//   4. The owner presses it and «Create». Telegram tells the manager (update managed_bot, and the service message
//      managed_bot_created).
//   5. getManagedBotToken(bot id) gives us the token, and the bot is linked.
// - Their own bot: made with @BotFather (/newbot), its token pasted in the dashboard.
// Either way the bot belongs to the owner's Telegram account: they can rename it, change its picture or delete it in @BotFather.
//
// Flow:
// 1. Telegram posts to api/tg-webhook.ashx?b=<bot id>, with the header X-Telegram-Bot-Api-Secret-Token set to that bot's own
//    secret.
// 2. The message is saved in dbo.MetaMessages (Channel 'tg', ChatId = the customer's Telegram id), and we answer 200 right away.
// 3. In the background: wait a few seconds, answer with the recent conversation, and send it with sendMessage.
// Unlike Messenger there's no 24-hour window, but a bot can't write first: the customer opens the bot and presses Start.
// An owner can't answer as the bot from Telegram itself, so «الزبون محتاجك» asks the customer for their number, as on Messenger.
// Plan gate "telegram" (open to every plan until the super admin picks a minimum).
// Built from the Bot API docs (10.5, 2026-11-22) and tested with a fake Bot API (setting TgApiBase).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    public class TgException : Exception
    {
        public int Code, RetryAfter;   // RetryAfter: seconds Telegram asked to wait (429)
        public TgException(string message, int code = 0) : base(message) { Code = code; }
        public bool TokenDead { get { return Code == 401 || Code == 404; } }   // the token was revoked in @BotFather, or the bot deleted
        public bool Blocked { get { return Code == 403; } }                   // the customer blocked the bot
    }

    public class TgLink
    {
        public int StoreId; public long BotId; public long? OwnerTgId;
        public string Username, Name, Token, Secret, Status;
        public bool Managed, Enabled; public DateTime? LastAt;
        public string Url { get { return string.IsNullOrEmpty(Username) ? null : "https://t.me/" + Username; } }
    }

    // ---------- the Bot API, the links, Sellybot's manager bot ----------
    public static class Tg
    {
        public const string Ch = "tg", Feature = "telegram";
        static string ApiBase { get { return (Web.Setting("TgApiBase", "https://api.telegram.org/") ?? "").TrimEnd('/') + "/"; } }   // a setting only for local tests
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        static readonly Regex TokenRx = new Regex(@"^[0-9]{5,15}:[A-Za-z0-9_-]{30,60}$");

        public static bool TokenShape(string token) { return token != null && TokenRx.IsMatch(token); }

        // POST {base}bot<token>/<method> with a JSON body; the result, or a TgException with an owner-friendly message.
        // The token is part of the URL, so nothing here ever logs the URL or a raw network exception.
        public static Task<JsonElement> Call(string token, string method, object body = null)
        {
            return Post(token, method, new StringContent(body == null ? "{}" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        }

        // sendPhoto with the picture's own bytes (multipart; «ابعت عرض»). The sent message's photo[] has Telegram's file_id, which
        // the next sends of the same picture use instead of uploading it again.
        public static Task<JsonElement> SendPhoto(string token, string chat, byte[] image, string type, string caption)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(chat), "chat_id");
            if (!string.IsNullOrEmpty(caption)) form.Add(new StringContent(caption, Encoding.UTF8), "caption");
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrEmpty(type) ? "image/jpeg" : type);
            form.Add(file, "photo", type == "image/png" ? "offer.png" : type == "image/webp" ? "offer.webp" : "offer.jpg");
            return Post(token, "sendPhoto", form);
        }

        static async Task<JsonElement> Post(string token, string method, HttpContent content)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + "bot" + token + "/" + method);
            req.Content = content;
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req); }
            catch (Exception ex)
            {
                Log.Error(new Exception("Telegram " + method + ": " + ex.GetType().Name + " " + (ex.Message ?? "").Replace(token ?? "\u0000", "***")));
                throw new TgException("مقدرناش نوصل لتيليجرام دلوقتي. جرّب تاني كمان شوية.");
            }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync();
                JsonElement root = default(JsonElement); bool parsed = false;
                try { using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)) { root = doc.RootElement.Clone(); parsed = true; } } catch { }
                JsonElement result;
                if (parsed && (WaJson.B(root, "ok") ?? false))
                    return root.TryGetProperty("result", out result) ? result : default(JsonElement);
                int code = (int)resp.StatusCode, c2;
                if (parsed && int.TryParse(WaJson.S(root, "error_code") ?? "", out c2) && c2 > 0) code = c2;
                string desc = parsed ? (WaJson.S(root, "description") ?? "") : "";
                Log.Error(new Exception("Telegram " + method + " -> " + code + " " + desc));
                var fail = Friendly(code, desc);
                var pars = parsed ? WaJson.O(root, "parameters") : null;   // 429: how many seconds to wait
                int ra; if (pars.HasValue && int.TryParse(WaJson.S(pars.Value, "retry_after") ?? "", out ra)) fail.RetryAfter = ra;
                throw fail;
            }
        }

        static TgException Friendly(int code, string desc)
        {
            string why = (desc ?? "").Trim(); if (why.Length > 160) why = why.Substring(0, 160);
            string text;
            if (code == 401 || code == 404) text = "توكن البوت مش شغال: يا اتغيّر من \u2068@BotFather\u2069، يا البوت اتمسح. اربطه تاني بالتوكن الجديد.";
            else if (code == 403) text = "الزبون قفل البوت، فمش هتوصله رسايل.";
            else if (code == 429) text = "تيليجرام بيقول الطلبات كتير دلوقتي، هنحاول تاني بعد شوية.";
            else text = "تيليجرام رفض الطلب" + (why.Length > 0 ? ": " + why : "") + " (" + code + ").";
            return new TgException(text, code);
        }

        static string Str(JsonElement e, string name) { return e.ValueKind == JsonValueKind.Object ? WaJson.S(e, name) : null; }
        static long Id(JsonElement e) { long v; return long.TryParse(Str(e, "id") ?? "", out v) ? v : 0; }

        public static string Cut(string s, int n) { return MetaApp.Cut(s, n); }
        public static string HookUrl(string origin, long botId) { return origin + "/api/tg-webhook.ashx?b=" + botId; }

        // ---- Sellybot's manager bot (AppSettings; the token encrypted) ----
        public static string ManagerToken(SqlConnection c) { var box = Billing.Get(c, "TgManagerToken"); return string.IsNullOrEmpty(box) ? null : Secrets.Unprotect(box); }
        public static long ManagerId(SqlConnection c) { long v; return long.TryParse(Billing.Get(c, "TgManagerId") ?? "", out v) ? v : 0; }
        public static string ManagerUser(SqlConnection c) { return Billing.Get(c, "TgManagerUser"); }
        // Owners can have a bot made for them: the manager bot is set up and Telegram let it manage bots
        public static bool Auto(SqlConnection c) { return ManagerId(c) > 0 && !string.IsNullOrEmpty(ManagerUser(c)) && Billing.Get(c, "TgManagerManage") == "1"; }

        // ---- the links (one bot per store) ----
        public static TgLink Link(SqlConnection c, int storeId) { return One(c, "StoreId=@v", storeId); }
        public static TgLink LinkByBot(SqlConnection c, long botId) { return One(c, "BotId=@v", botId); }

        static TgLink One(SqlConnection c, string where, object v)
        {
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 * FROM dbo.TgLinks WHERE " + where, "@v", v))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                return new TgLink
                {
                    StoreId = (int)r["StoreId"], BotId = (long)r["BotId"], Username = r["Username"] as string, Name = r["Name"] as string,
                    Token = Secrets.Unprotect((string)r["Token"]), Secret = (string)r["Secret"], Managed = (bool)r["Managed"],
                    OwnerTgId = r["OwnerTgId"] is long ? (long?)(long)r["OwnerTgId"] : null, Enabled = (bool)r["Enabled"], Status = r["Status"] as string,
                    LastAt = r["LastAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["LastAt"], DateTimeKind.Utc) : (DateTime?)null,
                };
            }
        }

        // null = all fine (and LastAt = now); otherwise what the owner sees on the card
        public static void SetStatus(SqlConnection c, int storeId, string status)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.TgLinks SET Status=@st" + (status == null ? ", LastAt=SYSUTCDATETIME()" : "") + " WHERE StoreId=@s",
                "@st", (object)Cut(status, 400) ?? DBNull.Value, "@s", storeId)) cmd.ExecuteNonQuery();
        }

        // Points the bot's updates at us (with a new secret); null = done, otherwise why not
        public static async Task<string> Hook(string token, string url, string secret, string[] updates)
        {
            try
            {
                await Call(token, "setWebhook", new { url = url, secret_token = secret, allowed_updates = updates, max_connections = 20 });
                return null;
            }
            catch (TgException ex) { return ex.Message; }
        }

        // Links this bot (its token) to the store, replacing the store's old bot. null = done, otherwise the error to show.
        // managed = made for them through the manager bot (owner = the Telegram account that made it).
        public static async Task<string> Connect(int storeId, string token, bool managed, long? owner, string origin)
        {
            token = (token ?? "").Trim();
            if (!TokenShape(token)) return "التوكن شكله مش مظبوط. انسخه زي ما هو من رسالة \u2068@BotFather\u2069: أرقام، وبعدها «:» وحروف (زي 123456789:AAH…).";
            JsonElement me;
            try { me = await Call(token, "getMe"); }
            catch (TgException ex) { return ex.TokenDead ? "التوكن ده مش شغال. انسخه تاني زي ما هو من \u2068@BotFather\u2069، أو ابعتله \u2068/token\u2069 واختار البوت عشان ياخد توكن جديد." : ex.Message; }
            long botId = Id(me);
            string username = Str(me, "username"), name = Str(me, "first_name");
            if (botId <= 0 || string.IsNullOrEmpty(username)) return "تيليجرام مارجّعش بيانات البوت. جرّب تاني.";
            TgLink old;
            using (var c = Db.Open())
            {
                if (botId == ManagerId(c)) return "ده بوت Sellybot نفسه. اعمل بوت لمتجرك وحط التوكن بتاعه هو.";
                var other = LinkByBot(c, botId);
                if (other != null && other.StoreId != storeId) return "البوت ده مربوط بمتجر تاني على Sellybot. لو ده متجرك القديم، الغِ الربط منه الأول.";
                old = Link(c, storeId);
            }
            string secret = WhatsAppBot.NewKey().Substring(0, 32);
            var why = await Hook(token, HookUrl(origin, botId), secret, new[] { "message" });
            if (why != null) return "تيليجرام ماوافقش يبعتلنا رسايل البوت: " + why;
            if (old != null && old.BotId != botId && old.Token != null)
                try { await Call(old.Token, "deleteWebhook"); } catch (TgException) { }   // the old bot stops coming to us
            if (managed) await Dress(token, storeId);
            try
            {
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.TgLinks WHERE StoreId=@s;
                    INSERT INTO dbo.TgLinks(StoreId, BotId, Username, Name, Token, Secret, Managed, OwnerTgId, Enabled) VALUES(@s, @b, @u, @n, @t, @k, @m, @o, 1);
                    DELETE FROM dbo.TgStarts WHERE StoreId=@s AND UsedAt IS NULL;",
                    "@s", storeId, "@b", botId, "@u", Cut(username, 64), "@n", (object)Cut(name, 200) ?? DBNull.Value, "@t", Secrets.Protect(token), "@k", secret,
                    "@m", managed, "@o", owner.HasValue ? (object)owner.Value : DBNull.Value))
                    cmd.ExecuteNonQuery();
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return "البوت ده مربوط بمتجر تاني على Sellybot."; }
            return null;
        }

        // A bot we just made for the owner starts blank: what it says before Start, and it stays out of groups (each group
        // message would cost the store's quota). Best effort; the owner can change all of it in @BotFather.
        static async Task Dress(string token, int storeId)
        {
            string name;
            using (var c = Db.Open()) { var s = Db.StoreById(c, storeId); name = s != null ? s.Name : null; }
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Cut(name.Trim(), 80);
            try { await Call(token, "setMyDescription", new { description = "أهلًا بيك في " + name + " 👋\nدوس «Start» واسأل عن أي منتج أو سعر أو الشحن، والمساعد يرد عليك على طول." }); } catch (TgException) { }
            try { await Call(token, "setMyShortDescription", new { short_description = Cut("المساعد الذكي لـ " + name + ": اسأل عن أي منتج.", 120) }); } catch (TgException) { }
            try { await Call(token, "setMySettings", new { can_join_groups = false }); } catch (TgException) { }
        }

        // The bot stops coming to us; its token and every message we kept from it go (privacy.html)
        public static async Task Unlink(int storeId)
        {
            TgLink l; using (var c = Db.Open()) l = Link(c, storeId);
            if (l != null && l.Token != null)
                try { await Call(l.Token, "deleteWebhook"); } catch (TgException) { }
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.TgLinks WHERE StoreId=@s; DELETE FROM dbo.TgStarts WHERE StoreId=@s;
                DELETE FROM dbo.MetaMessages WHERE StoreId=@s AND Channel=@ch;", "@s", storeId, "@ch", Ch))
                cmd.ExecuteNonQuery();
        }

        // A picture the customer sent (its file_id): its bytes through getFile, or null (gone, or over 5 MB)
        public static async Task<byte[]> Download(string token, string fileId)
        {
            try
            {
                var f = await Call(token, "getFile", new { file_id = fileId });
                string path = Str(f, "file_path"); long size; long.TryParse(Str(f, "file_size") ?? "0", out size);
                if (string.IsNullOrEmpty(path) || size > 5 * 1024 * 1024) return null;
                using (var res = await Http.GetAsync(ApiBase + "file/bot" + token + "/" + path, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!res.IsSuccessStatusCode) return null;
                    var bytes = await res.Content.ReadAsByteArrayAsync();
                    return bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024 ? null : bytes;
                }
            }
            catch (TgException) { return null; }
            catch (Exception ex) { Log.Error(new Exception("Telegram file: " + ex.GetType().Name)); return null; }
        }

        // A username to suggest for the store's bot: 5-32 letters, digits and _, starting with a letter, ending in "bot"
        public static string SuggestUsername(string slug)
        {
            var u = Regex.Replace((slug ?? "").ToLowerInvariant().Replace('-', '_'), "[^a-z0-9_]", "").Trim('_');
            if (u.Length == 0 || !char.IsLetter(u[0])) u = "shop_" + u;
            u = u.TrimEnd('_');
            if (u.Length > 22) u = u.Substring(0, 22).TrimEnd('_');
            return u + (u.EndsWith("store") || u.EndsWith("shop") ? "_bot" : "_store_bot");
        }

        public static object LinkJson(TgLink l)
        {
            if (l == null) return null;
            return new Dictionary<string, object> {
                { "id", l.BotId.ToString() }, { "username", l.Username ?? "" }, { "name", l.Name ?? "" }, { "url", l.Url }, { "enabled", l.Enabled },
                { "managed", l.Managed }, { "status", l.Status ?? "" }, { "lastAt", l.LastAt.HasValue ? l.LastAt.Value.ToString("o") : null }, { "tokenOk", l.Token != null } };
        }
    }

    // ---------- the assistant on the store's bot ----------
    public static class TgBot
    {
        static readonly TimeSpan Debounce = TimeSpan.FromSeconds(4);
        const string Fallback = "مقدرش أساعد في ده 🙏 بس اسألني عن أي حاجة تخص المتجر وأنا أساعدك.";
        const int MaxImage = 5 * 1024 * 1024;

        // One update's message (private chats only: a bot added to a group stays quiet there)
        public static void Handle(SqlConnection c, StoreInfo s, TgLink link, JsonElement m)
        {
            var chatEl = WaJson.O(m, "chat");
            if (!chatEl.HasValue || WaJson.S(chatEl.Value, "type") != "private") return;
            var from = WaJson.O(m, "from");
            if (from.HasValue && (WaJson.B(from.Value, "is_bot") ?? false)) return;
            string chat = WaJson.S(chatEl.Value, "id");
            if (string.IsNullOrEmpty(chat) || chat.Length > 30) return;
            string mid = chat + ":" + (WaJson.S(m, "message_id") ?? Guid.NewGuid().ToString("N"));
            string text = (WaJson.S(m, "text") ?? WaJson.S(m, "caption") ?? "").Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);
            int storeId = s.Id; var l = link;

            // «ابعت عرض»: everyone who wrote or pressed Start can get the store's offers here; «وقف» / /stop after one ends them
            Offers.TgSeen(c, s.Id, chat, from.HasValue ? WaJson.S(from.Value, "first_name") : null);
            if (Offers.TgStop(c, s, link, chat, text)) return;

            // Start: the store's welcome, no assistant call (nothing from the quota)
            if (text == "/start" || text.StartsWith("/start "))
            {
                if (!link.Enabled || !Web.Allow("tg-start:" + s.Id + ":" + chat, 3, TimeSpan.FromHours(1))) return;
                HostingEnvironment.QueueBackgroundWorkItem(ct => Welcome(storeId, chat));
                return;
            }

            // what they sent besides words: a picture the assistant looks at, a number they shared, or something it can't take
            string image = null, media = null;
            JsonElement photos;
            if (m.TryGetProperty("photo", out photos) && photos.ValueKind == JsonValueKind.Array) image = BestPhoto(photos);
            var doc = WaJson.O(m, "document");
            if (image == null && doc.HasValue)
            {
                string mime = (WaJson.S(doc.Value, "mime_type") ?? "").ToLowerInvariant(); long size; long.TryParse(WaJson.S(doc.Value, "file_size") ?? "0", out size);
                if (WaImages.IsImage(null, mime) && size <= MaxImage) image = WaJson.S(doc.Value, "file_id");
                else media = "document";
            }
            var contact = WaJson.O(m, "contact");
            if (text.Length == 0 && contact.HasValue)
            {
                string phone = WaJson.S(contact.Value, "phone_number"), who = WaJson.S(contact.Value, "user_id");
                string me = from.HasValue ? WaJson.S(from.Value, "id") : null;
                if (!string.IsNullOrEmpty(phone)) text = (who != null && who == me ? "ده رقمي: " : "رقم: ") + phone;
            }
            if (m.TryGetProperty("sticker", out photos)) return;   // a sticker is a reaction, like 👍
            if (media == null)
                foreach (var k in new[] { "voice", "audio", "video", "video_note", "animation", "location", "venue", "poll" })
                    if (m.TryGetProperty(k, out photos)) { media = k == "voice" || k == "audio" ? "audio" : k.StartsWith("video") || k == "animation" ? "video" : ""; break; }

            if (text.Length == 0 && image == null)
            {
                if (media == null || !link.Enabled || !Web.Allow("tg-media:" + s.Id + ":" + chat, 1, TimeSpan.FromHours(1))) return;
                string say = WaImages.MediaReply(media, null);
                HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    try { await Send(l.Token, chat, say); using (var c2 = Db.Open()) MetaBot.Save(c2, storeId, Tg.Ch, chat, false, say, null); }
                    catch (TgException) { }
                    catch (Exception ex) { Log.Error(ex); }
                });
                return;
            }

            long? row = MetaBot.Save(c, s.Id, Tg.Ch, chat, true, text.Length > 0 ? text : WaImages.Placeholder, mid, image != null ? "tg:" + image : null);
            if (row == null || !link.Enabled) return;   // a repeat delivery / the assistant is off here (the message stays as context)
            long rowId = row.Value;
            HostingEnvironment.QueueBackgroundWorkItem(ct => Reply(storeId, chat, rowId, ct));
        }

        // The largest size of a photo that's still small enough to send on (Claude scales anything above ~1600px down anyway)
        static string BestPhoto(JsonElement sizes)
        {
            string best = null, smallest = null; int bestSide = -1;
            foreach (var p in sizes.EnumerateArray())
            {
                string id = WaJson.S(p, "file_id"); int w, h; long size;
                if (string.IsNullOrEmpty(id)) continue;
                int.TryParse(WaJson.S(p, "width") ?? "0", out w); int.TryParse(WaJson.S(p, "height") ?? "0", out h); long.TryParse(WaJson.S(p, "file_size") ?? "0", out size);
                if (smallest == null) smallest = id;
                int side = Math.Max(w, h);
                if (side <= 1600 && size <= MaxImage && side > bestSide) { best = id; bestSide = side; }
            }
            return best ?? smallest;
        }

        static async Task Welcome(int storeId, string chat)
        {
            try
            {
                StoreInfo s; TgLink link;
                using (var c = Db.Open())
                {
                    s = Db.StoreById(c, storeId); link = Tg.Link(c, storeId);
                    if (s == null || s.Status != "active" || link == null || !link.Enabled || link.Token == null) return;
                    if (!PlanGate.Allows(c, s, Tg.Feature) || Billing.Expired(s)) return;
                }
                string hello = MetaBot.Plain(string.IsNullOrWhiteSpace(s.Welcome) ? "أهلًا بيك في " + s.Name + " 👋 اسألني عن أي حاجة وأنا أساعدك على طول." : s.Welcome);
                await Send(link.Token, chat, hello);
                using (var c = Db.Open()) MetaBot.Save(c, storeId, Tg.Ch, chat, false, hello, null);
            }
            catch (TgException) { }
            catch (Exception ex) { Log.Error(ex); }
        }

        internal static async Task Reply(int storeId, string chat, long rowId, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Debounce, ct);
                // the database in short steps, never while waiting on the network (see WhatsAppBot.Reply)
                List<MetaBot.Row> rows; StoreInfo s; TgLink link; List<ChatHandler.ExtraTool> extra;
                using (var c = Db.Open())
                {
                    rows = MetaBot.Recent(c, storeId, Tg.Ch, chat);
                    if (rows.Count == 0 || rows[0].Id != rowId) return;   // a newer message answers everything
                    if (rows.Any(x => x.Owner && x.At > DateTime.UtcNow.AddHours(-WhatsAppBot.PauseHours))) return;   // someone answered from SellyChat
                    s = Db.StoreById(c, storeId);
                    link = Tg.Link(c, storeId);
                    if (s == null || s.Status != "active" || link == null || !link.Enabled || link.Token == null) return;
                    if (!PlanGate.Allows(c, s, Tg.Feature)) { Tg.SetStatus(c, s.Id, "⚠️ باقتك مش فيها المساعد على تيليجرام، فهو مش بيرد هناك لحد ما ترقّي من «باقتي»."); return; }
                    if (Billing.Expired(s)) { Tg.SetStatus(c, s.Id, "⚠️ الباقة خلصت، فالمساعد مش بيرد على تيليجرام لحد ما تجدّدها."); return; }
                    if (Billing.LimitReached(c, s)) { Tg.SetStatus(c, s.Id, "⚠️ رصيد الشهر خلص، فالمساعد مش بيرد على تيليجرام لحد ما تزوّد الرصيد أو يبدأ شهر جديد."); return; }
                    if (!Web.Allow("tg-chat:" + s.Id + ":" + chat, 30, TimeSpan.FromHours(1))) return;   // one customer can't burn the quota
                    PlanGate.Apply(c, s);
                    try { Shopify.RefreshIfOld(s); } catch (Exception ex) { Log.Error(ex); }
                    try { EasyOrders.RefreshIfOld(s); } catch (Exception ex) { Log.Error(ex); }
                    extra = new List<ChatHandler.ExtraTool>();
                    if (Handoff.On(c, s)) extra.Add(Handoff.Tool(s, Tg.Ch, chat));   // the customer's number isn't known here: the assistant asks for it
                }
                try { await Tg.Call(link.Token, "sendChatAction", new { chat_id = chat, action = "typing" }); } catch (TgException) { }

                var imgs = new Dictionary<long, ImageBlockParam>();
                foreach (var x in rows.TakeWhile(x => x.Customer).Where(x => x.Media != null && x.Media.StartsWith("tg:")).Take(WaImages.MaxPerTurn))
                {
                    var img = WaImages.FromBytes(await Tg.Download(link.Token, x.Media.Substring(3)), null);
                    if (img != null) imgs[x.Id] = img;
                }

                ChatHandler.LoopResult res;
                try
                {
                    ChatHandler.Prepared prep;
                    using (var c = Db.Open())
                        prep = ChatHandler.Prepare(c, s, MetaBot.History(rows, imgs), Tg.Ch + ":" + chat, ChannelNote + (imgs.Count > 0 ? WaImages.Note : ""), extra);
                    res = await ChatHandler.Run(prep);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    using (var c = Db.Open()) Tg.SetStatus(c, s.Id, "⚠️ حصلت مشكلة وإحنا بنجهّز رد على آخر رسالة. لو اتكررت كلّم الدعم.");
                    return;
                }

                string answer = MetaBot.Plain(string.IsNullOrWhiteSpace(res.Text) ? Fallback : res.Text);
                long? mine;
                using (var c = Db.Open())
                {
                    Db.AddUsage(c, s.Id, 1, res.In, res.CacheRead, res.CacheWrite, res.Out);
                    mine = MetaBot.Save(c, s.Id, Tg.Ch, chat, false, answer, null);
                }
                string failed = null; bool blocked = false;
                try { await Send(link.Token, chat, answer); }
                catch (TgException ex) { blocked = ex.Blocked; failed = "⚠️ " + ex.Message; }
                using (var c = Db.Open())
                {
                    if (failed != null && mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.MetaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                    if (!blocked) Tg.SetStatus(c, s.Id, failed);   // a customer who blocked the bot isn't the owner's problem
                    if (new Random().Next(200) == 0) using (var cmd = Db.Cmd(c, "DELETE TOP (5000) FROM dbo.MetaMessages WHERE CreatedAt < DATEADD(DAY,-60,SYSUTCDATETIME())")) cmd.ExecuteNonQuery();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error(ex); }
        }

        // Plain text (no parse_mode: nothing the assistant writes can break it), in parts of up to 1900 characters (Telegram takes 4096)
        public static async Task Send(string token, string chat, string text)
        {
            foreach (var part in MetaBot.Split(text, MetaApp.Fb))
                await Tg.Call(token, "sendMessage", new { chat_id = chat, text = part });
        }

        const string ChannelNote = "المحادثة دي على تيليجرام (بوت المتجر)، مش على الموقع:\n" +
            "- خلي ردودك قصيرة زي رسايل الشات العادية.\n" +
            "- اكتب كلام عادي من غير أي تنسيق: من غير نجوم ولا # ولا جداول. للقوائم استخدم • أو أرقام.\n" +
            "- اكتب اللينكات كاملة (https://...) مش [نص](لينك).\n" +
            "- مش معاك رقم الزبون. لو احتجته (تتبع أوردر، أو صاحب المتجر يكلّمه) اطلبه منه.";
    }

    // ---------- Sellybot's manager bot: makes a bot for an owner ----------
    public static class TgManager
    {
        static bool Valid(string code) { return code != null && Regex.IsMatch(code, "^[A-Za-z0-9_-]{8,64}$"); }

        public static void Handle(JsonElement update, string origin)
        {
            var mb = WaJson.O(update, "managed_bot");
            if (mb.HasValue)
            {
                var u = WaJson.O(mb.Value, "user"); var b = WaJson.O(mb.Value, "bot");
                if (u.HasValue && b.HasValue) Later(() => Created(Num(u.Value), b.Value, origin));
                return;
            }
            var mo = WaJson.O(update, "message");
            if (!mo.HasValue) return;
            var m = mo.Value;
            var chat = WaJson.O(m, "chat"); var from = WaJson.O(m, "from");
            if (!chat.HasValue || !from.HasValue || WaJson.S(chat.Value, "type") != "private") return;
            long user = Num(from.Value);
            if (user <= 0) return;
            var made = WaJson.O(m, "managed_bot_created");
            if (made.HasValue) { var b = WaJson.O(made.Value, "bot"); if (b.HasValue) Later(() => Created(user, b.Value, origin)); return; }
            string text = (WaJson.S(m, "text") ?? "").Trim();
            if (text.StartsWith("/start ") && Valid(text.Substring(7).Trim())) { string code = text.Substring(7).Trim(); Later(() => Start(user, code)); return; }
            if (Web.Allow("tg-mgr-help:" + user, 2, TimeSpan.FromMinutes(10))) Later(() => Say(user, Help(origin)));
        }

        static long Num(JsonElement e) { long v; return long.TryParse(WaJson.S(e, "id") ?? "", out v) ? v : 0; }
        static void Later(Func<Task> work) { HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { await work(); } catch (Exception ex) { Log.Error(ex); } }); }

        static string Help(string origin)
        {
            return "أهلًا 👋 البوت ده بيعمل بوت تيليجرام لمتجرك على Sellybot، والمساعد الذكي يرد فيه على زباينك.\n\n"
                + "ابدأ من لوحة التحكم ← «🔗 ربط بحساباتك» ← «تيليجرام» ← «اعمل بوت متجرك»:\n" + origin + "/dashboard.html#telegram";
        }

        static async Task Say(long user, string text, object keyboard = null)
        {
            string token; using (var c = Db.Open()) token = Tg.ManagerToken(c);
            if (token == null) return;
            var body = new Dictionary<string, object> { { "chat_id", user }, { "text", text } };
            if (keyboard != null) body["reply_markup"] = keyboard;
            try { await Tg.Call(token, "sendMessage", body); } catch (TgException) { }
        }

        // /start <code>: this Telegram account makes the bot for that store
        static async Task Start(long user, string code)
        {
            StoreInfo s = null; string problem = null;
            using (var c = Db.Open())
            {
                int n;
                using (var cmd = Db.Cmd(c, @"UPDATE dbo.TgStarts SET TgUserId=@u, StartedAt=SYSUTCDATETIME()
                    WHERE Code=@c AND UsedAt IS NULL AND ExpiresAt > SYSUTCDATETIME() AND (TgUserId IS NULL OR TgUserId=@u)", "@u", user, "@c", code))
                    n = cmd.ExecuteNonQuery();
                if (n == 0)
                    using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.TgStarts WHERE Code=@c AND UsedAt IS NULL AND ExpiresAt > SYSUTCDATETIME()", "@c", code))
                        problem = (int)cmd.ExecuteScalar() > 0
                            ? "اللينك ده اتفتح من حساب تيليجرام تاني. ارجع للوحة التحكم ودوس «اعمل بوت متجرك» تاني، وافتحه من حسابك انت."
                            : "اللينك ده قديم أو اتستخدم خلاص. ارجع للوحة التحكم ← «تيليجرام» ودوس «اعمل بوت متجرك» تاني.";
                else
                    using (var cmd = Db.Cmd(c, "SELECT StoreId FROM dbo.TgStarts WHERE Code=@c", "@c", code))
                        s = Db.StoreById(c, (int)cmd.ExecuteScalar());
            }
            if (problem != null || s == null) { await Say(user, problem ?? "حصلت مشكلة. ابدأ تاني من لوحة التحكم."); return; }
            string name = Tg.Cut((s.Name ?? "").Trim(), 64);
            var button = new Dictionary<string, object> { { "request_id", 1 }, { "suggested_username", Tg.SuggestUsername(s.Slug) } };
            if (name.Length > 0) button["suggested_name"] = name;
            var keyboard = new
            {
                keyboard = new[] { new[] { new Dictionary<string, object> { { "text", "🤖 اعمل بوت متجرك" }, { "request_managed_bot", button } } } },
                resize_keyboard = true, one_time_keyboard = true, input_field_placeholder = "دوس «🤖 اعمل بوت متجرك» تحت",
            };
            await Say(user, "أهلًا 👋 هنعمل بوت تيليجرام لمتجر «" + (name.Length > 0 ? name : s.Slug) + "» على Sellybot.\n\n"
                + "1️⃣ دوس الزرار اللي تحت «🤖 اعمل بوت متجرك».\n"
                + "2️⃣ هتلاقي اسم البوت واليوزرنيم بتاعه جاهزين. غيّرهم لو حبيت (اليوزرنيم لازم يخلص بـ bot).\n"
                + "3️⃣ دوس «Create».\n\n"
                + "وبس! هنربطه بمتجرك لوحدنا، والمساعد يبدأ يرد فيه على طول.", keyboard);
        }

        // Telegram made the bot (or its token / owner changed): link it to the store this account pressed Start for
        static async Task Created(long user, JsonElement bot, string origin)
        {
            long botId = Num(bot);
            if (user <= 0 || botId <= 0) return;
            string managerToken, code = null; int storeId = 0; TgLink existing;
            using (var c = Db.Open())
            {
                managerToken = Tg.ManagerToken(c);
                if (managerToken == null) return;
                existing = Tg.LinkByBot(c, botId);
                if (existing == null)
                    using (var cmd = Db.Cmd(c, @";WITH x AS (SELECT TOP 1 * FROM dbo.TgStarts WITH (ROWLOCK, UPDLOCK, READPAST)
                          WHERE TgUserId=@u AND UsedAt IS NULL AND ExpiresAt > SYSUTCDATETIME() ORDER BY StartedAt DESC)
                        UPDATE x SET UsedAt=SYSUTCDATETIME() OUTPUT INSERTED.Code, INSERTED.StoreId;", "@u", user))
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) { code = (string)r["Code"]; storeId = (int)r["StoreId"]; }
                if (existing == null && code == null)
                {
                    // the second of the two notices Telegram sends for one bot: the first one is linking it right now
                    using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.TgStarts WHERE TgUserId=@u AND UsedAt > DATEADD(MINUTE,-3,SYSUTCDATETIME())", "@u", user))
                        if ((int)cmd.ExecuteScalar() > 0) return;
                }
            }
            if (existing != null)
            {
                // a bot we already answer for: its token changed (the owner asked @BotFather for a new one): take the new one
                if (!existing.Managed) return;
                string fresh;
                try { fresh = (await Tg.Call(managerToken, "getManagedBotToken", new { user_id = botId })).GetString(); }
                catch (Exception) { return; }
                if (string.IsNullOrEmpty(fresh) || fresh == existing.Token) return;
                string secret = WhatsAppBot.NewKey().Substring(0, 32);
                if (await Tg.Hook(fresh, Tg.HookUrl(origin, botId), secret, new[] { "message" }) != null) return;
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "UPDATE dbo.TgLinks SET Token=@t, Secret=@k, Status=NULL WHERE BotId=@b", "@t", Secrets.Protect(fresh), "@k", secret, "@b", botId))
                    cmd.ExecuteNonQuery();
                return;
            }
            if (code == null)
            {
                await Say(user, "✅ البوت اتعمل، بس مش لاقيين طلب ربط مفتوح من حسابك. ارجع للوحة التحكم ← «تيليجرام» ودوس «اعمل بوت متجرك»، "
                    + "أو اربطه بالتوكن بتاعه (من \u2068@BotFather\u2069 ← \u2068/mybots\u2069).");
                return;
            }
            string err = null, username = WaJson.S(bot, "username");
            using (var c = Db.Open())
            {
                var s = Db.StoreById(c, storeId);
                if (s == null) err = "المتجر ده مش موجود.";
                else if (!PlanGate.Allows(c, s, Tg.Feature)) err = "باقة متجرك مش فيها تيليجرام. رقّي باقتك من «باقتي» وجرّب تاني.";
            }
            if (err == null)
            {
                string token = null;
                try { token = (await Tg.Call(managerToken, "getManagedBotToken", new { user_id = botId })).GetString(); }
                catch (TgException ex) { err = "مقدرناش ناخد توكن البوت من تيليجرام: " + ex.Message; }
                catch (InvalidOperationException) { err = "تيليجرام مارجّعش توكن البوت. جرّب تاني."; }
                if (err == null) err = await Tg.Connect(storeId, token, true, user, origin);
            }
            if (err != null)
            {
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "UPDATE dbo.TgStarts SET Error=@e WHERE Code=@c", "@e", Tg.Cut(err, 400), "@c", code)) cmd.ExecuteNonQuery();
                await Say(user, "⚠️ البوت اتعمل بس مااتربطش: " + err + "\nتقدر تربطه من لوحة التحكم ← «تيليجرام» بالتوكن بتاعه (من \u2068@BotFather\u2069 ← \u2068/mybots\u2069).", new { remove_keyboard = true });
                return;
            }
            await Say(user, "✅ تمام! البوت @" + username + " اتربط بمتجرك على Sellybot، والمساعد بيرد فيه من دلوقتي.\n\n"
                + "🔗 لينك البوت لزباينك: https://t.me/" + username + "\n"
                + "حطه على صفحتك أو في البايو. وتقدر تغيّر صورته ووصفه من \u2068@BotFather\u2069 ← \u2068/mybots\u2069.\n\n"
                + "ارجع للوحة التحكم تلاقيه مربوط: " + origin + "/dashboard.html#telegram", new { remove_keyboard = true });
        }
    }

    // api/tg-webhook.ashx?b=<bot id> — Telegram posts every update of a store's bot (and of Sellybot's manager bot) here.
    // Each bot has its own secret, sent back in X-Telegram-Bot-Api-Secret-Token.
    public class TgWebhookHandler : IHttpHandler
    {
        static DateTime lastBadSecret = DateTime.MinValue;
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { res.StatusCode = 503; return; }
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }
            if (!Web.Allow("tg-hook:" + Web.Ip(req), 1200, TimeSpan.FromMinutes(1))) { res.StatusCode = 429; return; }
            try
            {
                long botId;
                if (!long.TryParse(req.QueryString["b"] ?? "", out botId) || botId <= 0) { res.StatusCode = 404; return; }
                byte[] raw;
                using (var ms = new MemoryStream()) { req.InputStream.CopyTo(ms); raw = ms.ToArray(); }
                if (raw.Length > 512 * 1024) { res.StatusCode = 413; return; }
                string got = req.Headers["X-Telegram-Bot-Api-Secret-Token"] ?? "";
                using (var c = Db.Open())
                {
                    bool manager = botId == Tg.ManagerId(c);
                    TgLink link = manager ? null : Tg.LinkByBot(c, botId);
                    string want = manager ? Billing.Get(c, "TgManagerSecret") : link != null ? link.Secret : null;
                    if (string.IsNullOrEmpty(want)) { Web.Json(res, 200, new { ok = true }); return; }   // a bot nobody links (any more): nothing to retry
                    if (!Secrets.Same(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(got)))
                    {
                        if (DateTime.UtcNow - lastBadSecret > TimeSpan.FromMinutes(10)) { lastBadSecret = DateTime.UtcNow; Log.Error(new Exception("Telegram webhook: bad or missing secret")); }
                        res.StatusCode = 401; return;
                    }
                    JsonElement root;
                    try { using (var doc = JsonDocument.Parse(raw)) root = doc.RootElement.Clone(); }
                    catch { res.StatusCode = 400; return; }
                    if (manager) TgManager.Handle(root, WaAccount.Origin(req));
                    else
                    {
                        var s = Db.StoreById(c, link.StoreId);
                        var m = WaJson.O(root, "message");
                        if (s != null && m.HasValue)
                            try { TgBot.Handle(c, s, link, m.Value); } catch (Exception ex) { Log.Error(ex); }
                    }
                    Web.Json(res, 200, new { ok = true });
                }
            }
            catch (Exception ex) { Log.Error(ex); res.StatusCode = 500; }
        }
    }

    // api/telegram.ashx — the owner's «تيليجرام» tab: GET = state; POST ?action=start (a link to make a bot through the manager)
    // | token (link their own bot) | enable | unlink | refresh
    public class TelegramHandler : HttpTaskAsyncHandler
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
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, State(c, s)); return; }
                }
                if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                if (!Web.Allow("tg:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                var b = Web.Body(req, 2000);
                if (action == "start" || action == "token" || (action == "enable" && b.ContainsKey("enabled") && b["enabled"] is bool && (bool)b["enabled"]))
                    using (var c = Db.Open())
                        if (!PlanGate.Allows(c, s, Tg.Feature)) { PlanGate.Deny(res, c, s, Tg.Feature); return; }

                if (action == "start")
                {
                    using (var c = Db.Open())
                    {
                        if (!Tg.Auto(c)) { Web.Error(res, 400, "no_auto", "عمل البوت بضغطة مش متاح دلوقتي. اربط بوتك بالتوكن من \u2068@BotFather\u2069."); return; }
                        using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.TgStarts WHERE (StoreId=@s AND UsedAt IS NULL) OR ExpiresAt < DATEADD(DAY,-1,SYSUTCDATETIME());
                            INSERT INTO dbo.TgStarts(Code, StoreId, ExpiresAt) VALUES(@c, @s, DATEADD(MINUTE,30,SYSUTCDATETIME()));",
                            "@s", s.Id, "@c", "sb" + WhatsAppBot.NewKey().Substring(0, 30))) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, State(c, s));
                    }
                    return;
                }
                if (action == "token")
                {
                    var err = await Tg.Connect(s.Id, Web.Str(b, "token", 120), false, null, WaAccount.Origin(req));
                    if (err != null) { Web.Error(res, 400, "telegram", err); return; }
                }
                else if (action == "enable")
                {
                    object en; bool on = b.TryGetValue("enabled", out en) && en is bool && (bool)en;
                    using (var c = Db.Open())
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.TgLinks SET Enabled=@e, Status=NULL WHERE StoreId=@s", "@e", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                }
                else if (action == "unlink") await Tg.Unlink(s.Id);
                else if (action == "refresh")
                {
                    TgLink l; using (var c = Db.Open()) l = Tg.Link(c, s.Id);
                    if (l == null || l.Token == null) { Web.Error(res, 400, "not_linked", "مفيش بوت مربوط. اربط الأول."); return; }
                    string secret = WhatsAppBot.NewKey().Substring(0, 32);
                    var why = await Tg.Hook(l.Token, Tg.HookUrl(WaAccount.Origin(req), l.BotId), secret, new[] { "message" });
                    if (why != null) { using (var c = Db.Open()) Tg.SetStatus(c, s.Id, "⚠️ " + why); Web.Error(res, 400, "telegram", why); return; }
                    using (var c = Db.Open())
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.TgLinks SET Secret=@k, Status=NULL WHERE StoreId=@s", "@k", secret, "@s", s.Id)) cmd.ExecuteNonQuery();
                }
                else { Web.Error(res, 400, "bad_request"); return; }
                using (var c = Db.Open()) Web.Json(res, 200, State(c, s));
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }

        static Dictionary<string, object> State(SqlConnection c, StoreInfo s)
        {
            var d = new Dictionary<string, object> { { "allowed", PlanGate.Allows(c, s, Tg.Feature) }, { "auto", Tg.Auto(c) } };
            var l = Tg.Link(c, s.Id);
            d["link"] = Tg.LinkJson(l);
            // the newest make-a-bot link: waiting for the owner in Telegram, or what went wrong there
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Code, TgUserId, UsedAt, Error, ExpiresAt FROM dbo.TgStarts WHERE StoreId=@s AND CreatedAt > DATEADD(HOUR,-1,SYSUTCDATETIME()) ORDER BY CreatedAt DESC", "@s", s.Id))
            using (var r = cmd.ExecuteReader())
                if (r.Read())
                {
                    bool open = r["UsedAt"] == DBNull.Value && (DateTime)r["ExpiresAt"] > DateTime.UtcNow;
                    var mgr = Tg.ManagerUser(c);
                    d["start"] = new Dictionary<string, object> {
                        { "open", open }, { "started", r["TgUserId"] != DBNull.Value }, { "error", r["Error"] as string },
                        { "url", open && !string.IsNullOrEmpty(mgr) ? "https://t.me/" + mgr + "?start=" + (string)r["Code"] : null } };
                }
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(DISTINCT ChatId) FROM dbo.MetaMessages WHERE StoreId=@s AND Channel=@ch AND FromCustomer=1
                AND CreatedAt > DATEADD(DAY,-7,SYSUTCDATETIME())", "@s", s.Id, "@ch", Tg.Ch))
                d["chats7d"] = (int)cmd.ExecuteScalar();
            return d;
        }
    }

    // api/tg-admin.ashx — the super admin's «تيليجرام»: Sellybot's manager bot (POST ?action=manager {token} | clear) and every linked bot
    public class TgAdminHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

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
                var b = Web.Body(req, 2000);
                string oldToken; using (var c = Db.Open()) oldToken = Tg.ManagerToken(c);

                if (action == "clear")
                {
                    if (oldToken != null) try { await Tg.Call(oldToken, "deleteWebhook"); } catch (TgException) { }
                    using (var c = Db.Open())
                        foreach (var k in new[] { "TgManagerToken", "TgManagerId", "TgManagerUser", "TgManagerName", "TgManagerSecret", "TgManagerManage" }) Billing.Set(c, k, "");
                    Web.Json(res, 200, await State());
                    return;
                }
                if (action != "manager") { Web.Error(res, 400, "bad_request"); return; }

                string token = (Web.Str(b, "token", 120) ?? "").Trim();
                if (token.Length == 0) token = oldToken;   // «حفظ والتأكد منه» with the box empty: check the saved bot again
                if (!Tg.TokenShape(token)) { Web.Error(res, 400, "bad_token", "التوكن شكله مش مظبوط. انسخه زي ما هو من رسالة \u2068@BotFather\u2069."); return; }
                JsonElement me;
                try { me = await Tg.Call(token, "getMe"); }
                catch (TgException ex) { Web.Error(res, 400, "telegram", ex.TokenDead ? "التوكن ده مش شغال. خد توكن جديد من \u2068@BotFather\u2069 ← \u2068/token\u2069." : ex.Message); return; }
                long id; long.TryParse(WaJson.S(me, "id") ?? "", out id);
                string user = WaJson.S(me, "username"), name = WaJson.S(me, "first_name");
                if (id <= 0 || string.IsNullOrEmpty(user)) { Web.Error(res, 400, "telegram", "تيليجرام مارجّعش بيانات البوت."); return; }
                using (var c = Db.Open())
                    if (Tg.LinkByBot(c, id) != null) { Web.Error(res, 400, "taken", "البوت ده مربوط بمتجر. اعمل بوت تاني مخصوص لـ Sellybot."); return; }
                // bot management: on through the API when Telegram allows it, otherwise from @BotFather's Mini App (the hint says how)
                try { await Tg.Call(token, "setMySettings", new { can_manage_bots = true }); } catch (TgException) { }
                bool manage = false;
                try { manage = WaJson.B(await Tg.Call(token, "getMe"), "can_manage_bots") ?? false; } catch (TgException) { }
                string secret = WhatsAppBot.NewKey().Substring(0, 32);
                var why = await Tg.Hook(token, Tg.HookUrl(WaAccount.Origin(req), id), secret, new[] { "message", "managed_bot" });
                if (why != null) { Web.Error(res, 400, "telegram", "تيليجرام ماوافقش يبعتلنا رسايل البوت: " + why); return; }
                if (oldToken != null && oldToken != token) try { await Tg.Call(oldToken, "deleteWebhook"); } catch (TgException) { }
                try { await Tg.Call(token, "setMyDescription", new { description = "البوت ده بيعمل بوت تيليجرام لمتجرك على Sellybot، والمساعد الذكي يرد فيه على زباينك. ابدأ من لوحة التحكم ← تيليجرام." }); } catch (TgException) { }
                using (var c = Db.Open())
                {
                    Billing.Set(c, "TgManagerToken", Secrets.Protect(token));
                    Billing.Set(c, "TgManagerId", id.ToString());
                    Billing.Set(c, "TgManagerUser", user);
                    Billing.Set(c, "TgManagerName", Tg.Cut(name ?? "", 100));
                    Billing.Set(c, "TgManagerSecret", secret);
                    Billing.Set(c, "TgManagerManage", manage ? "1" : "0");
                }
                Web.Json(res, 200, await State());
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة. جرّب تاني."); }
        }

        static async Task<Dictionary<string, object>> State()
        {
            var d = new Dictionary<string, object>();
            string token;
            var links = new List<Dictionary<string, object>>();
            using (var c = Db.Open())
            {
                token = Tg.ManagerToken(c);
                d["manager"] = token == null ? null : new Dictionary<string, object> {
                    { "id", Billing.Get(c, "TgManagerId") ?? "" }, { "username", Tg.ManagerUser(c) ?? "" }, { "name", Billing.Get(c, "TgManagerName") ?? "" },
                    { "manage", Billing.Get(c, "TgManagerManage") == "1" } };
                using (var cmd = Db.Cmd(c, @"SELECT TOP 300 l.BotId, l.Username, l.Name, l.Managed, l.Enabled, l.Status, l.LastAt, l.CreatedAt, s.Slug, s.Name AS StoreName
                    FROM dbo.TgLinks l JOIN dbo.Stores s ON s.Id = l.StoreId ORDER BY l.CreatedAt DESC"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        links.Add(new Dictionary<string, object> {
                            { "username", r["Username"] as string ?? "" }, { "name", r["Name"] as string ?? "" }, { "managed", r["Managed"] }, { "enabled", r["Enabled"] },
                            { "status", r["Status"] as string ?? "" }, { "lastAt", r["LastAt"] is DateTime ? ((DateTime)r["LastAt"]).ToString("o") + "Z" : null },
                            { "createdAt", ((DateTime)r["CreatedAt"]).ToString("o") + "Z" }, { "store", r["StoreName"] }, { "slug", r["Slug"] } });
            }
            d["links"] = links;
            if (token != null)
                try
                {
                    var w = await Tg.Call(token, "getWebhookInfo");
                    d["hook"] = new Dictionary<string, object> { { "set", !string.IsNullOrEmpty(WaJson.S(w, "url")) }, { "pending", WaJson.S(w, "pending_update_count") ?? "0" }, { "error", WaJson.S(w, "last_error_message") } };
                }
                catch (TgException ex) { d["hook"] = new Dictionary<string, object> { { "set", false }, { "error", ex.Message } }; }
            return d;
        }
    }
}
