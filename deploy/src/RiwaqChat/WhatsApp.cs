// WhatsApp through Wapilot (https://wapilot.net, API v2): the store's own WhatsApp number answers customers
// with the same assistant as the site chat (same data, rules, Shopify tools and monthly quota).
//
// Setup (store owner): Wapilot account + API plan, link the store's number (QR), create an API token,
// paste the token in the dashboard, then paste our webhook URL into Wapilot (events "message", and "message.any"
// if they want the bot to step aside when they answer a customer themselves).
//
// Flow: Wapilot -> api/wa-webhook.ashx?store=<slug>&key=<secret>  (Wapilot signs nothing, so the URL carries a secret)
//       -> message saved (duplicates dropped) -> 200 right away
//       -> background: wait a few seconds (customers often send 2-3 short messages), answer the latest one with the
//          whole recent conversation, send it with Wapilot's send-message.
using System;
using System.Collections.Concurrent;
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
    public class WaException : Exception
    {
        public int Status; public string Code;
        public WaException(string message, int status = 0, string code = null) : base(message) { Status = status; Code = code; }
    }

    // ---------- Wapilot API client ----------
    public static class Wapilot
    {
        static string Base { get { return Web.Setting("WapilotBase", "https://api.wapilot.net/api/v2/").TrimEnd('/') + "/"; } }   // setting only for local tests
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        static Wapilot() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0"); }

        public class Instance { public string Id, Name, Status, Number; public bool IsApi; }

        static async Task<JsonElement> Call(HttpMethod method, string path, string token, object body = null, string idem = null)
        {
            var req = new HttpRequestMessage(method, Base + path);
            req.Headers.TryAddWithoutValidation("token", token);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (idem != null) req.Headers.TryAddWithoutValidation("Idempotency-Key", idem);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req); }
            catch (Exception ex) { Log.Error(ex); throw new WaException("مقدرناش نوصل لـ Wapilot دلوقتي. جرّب تاني كمان شوية."); }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync();
                JsonElement root = default(JsonElement);
                bool parsed = false;
                try { using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)) { root = doc.RootElement.Clone(); parsed = true; } } catch { }
                int status = (int)resp.StatusCode;
                if (status >= 200 && status < 300 && parsed) return root;
                string code = parsed ? WaJson.S(root, "code") : null, msg = parsed ? WaJson.S(root, "message") : null;
                throw Friendly(status, code, msg);
            }
        }

        static WaException Friendly(int status, string code, string msg)
        {
            code = code ?? "";
            string text;
            if (status == 401 || code == "TOKEN_INVALID") text = "الـ API token مش صح أو انتهى. اعمل token جديد من Wapilot (Create token) والصقه هنا.";
            else if (code == "INSTANCE_FORBIDDEN") text = "الرقم ده مش تبع الـ token بتاعك على Wapilot.";
            else if (code == "INSTANCE_NOT_READY") text = "رقم الواتساب مش متصل على Wapilot دلوقتي. افتح Wapilot واتأكد إن الرقم متصل (WORKING).";
            else if (status == 404) text = "Wapilot مالقاش الرقم ده. اتأكد إنه لسه موجود في حسابك.";
            else if (status == 429) text = "Wapilot بيقول إن الطلبات كتير، هنحاول تاني بعد شوية.";
            else
            {
                var m = (msg ?? "").Trim(); if (m.Length > 150) m = m.Substring(0, 150);
                text = "Wapilot رفض الطلب (" + status + (code.Length > 0 ? " " + code : "") + ")" + (m.Length > 0 ? ": " + m : ".");
            }
            return new WaException(text, status, code);
        }

        public static async Task<List<Instance>> Instances(string token)
        {
            var root = await Call(HttpMethod.Get, "instances", token);
            var list = new List<Instance>();
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("instances", out arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    var id = WaJson.S(e, "instance_uniquename");
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add(new Instance
                    {
                        Id = id, Name = WaJson.S(e, "instance_name") ?? id, Status = WaJson.S(e, "status", "session_status") ?? "",
                        Number = WaChat.Digits(WaJson.S(e, "me_id")), IsApi = WaJson.B(e, "is_api") ?? true,
                    });
                }
            return list;
        }

        // Live connection state: QR instances have /status; Official ones only the instance details.
        public static async Task<Instance> Status(string token, string instance)
        {
            string id = Uri.EscapeDataString(instance);
            try
            {
                var r = await Call(HttpMethod.Get, "instances/" + id + "/status", token);
                return new Instance { Id = instance, Status = WaJson.S(r, "status") ?? "", Number = WaChat.Digits(WaJson.S(r, "me_id")) };
            }
            catch (WaException ex) when (ex.Status != 401 && ex.Code != "TOKEN_INVALID")
            {
                var r = await Call(HttpMethod.Get, "instances/" + id, token);
                JsonElement inst; if (!r.TryGetProperty("instance", out inst)) inst = r;
                return new Instance { Id = instance, Name = WaJson.S(inst, "instance_name"), Status = WaJson.S(inst, "status", "session_status") ?? "", Number = WaChat.Digits(WaJson.S(inst, "me_id")) };
            }
        }

        // Which send route works for an instance (QR "/{id}/send-message" or Official "/official/{id}/send-message"), learned on first use.
        static readonly ConcurrentDictionary<string, bool> Official = new ConcurrentDictionary<string, bool>();

        public static async Task Send(string token, string instance, string chatId, string text, string idem)
        {
            string id = Uri.EscapeDataString(instance);
            var body = new Dictionary<string, object> { { "chat_id", chatId }, { "text", text } };
            bool official;
            if (Official.TryGetValue(instance, out official) && official) { await Call(HttpMethod.Post, "official/" + id + "/send-message", token, body, idem); return; }
            try { await Call(HttpMethod.Post, id + "/send-message", token, body, idem); Official[instance] = false; }
            catch (WaException ex) when (ex.Status == 404 || ex.Code == "MESSAGE_TYPE_UNSUPPORTED" || (ex.Code ?? "").Contains("OFFICIAL"))
            {
                await Call(HttpMethod.Post, "official/" + id + "/send-message", token, body, idem);
                Official[instance] = true;
            }
        }

        // The assistant writes light Markdown for the site; WhatsApp has its own: *bold*, _italic_, ~strike~.
        public static string Format(string md)
        {
            var s = (md ?? "").Replace("\r\n", "\n");
            s = Regex.Replace(s, @"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", m => m.Groups[1].Value.Trim() == m.Groups[2].Value ? m.Groups[2].Value : m.Groups[1].Value + ": " + m.Groups[2].Value);
            s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*(.+?)\s*#*\s*$", "*$1*", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\*\*(.+?)\*\*", "*$1*");
            s = Regex.Replace(s, @"__(.+?)__", "_$1_");
            s = Regex.Replace(s, @"~~(.+?)~~", "~$1~");
            s = Regex.Replace(s, @"^(\s*)[-*+]\s+", "$1• ", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s*([-*_]\s*){3,}$", "", RegexOptions.Multiline);   // horizontal rules
            s = Regex.Replace(s, @"\*\*+", "*");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        // WhatsApp allows ~4096 characters per message; long answers go out in parts, split between paragraphs.
        public static List<string> Split(string text, int max = 3500)
        {
            var parts = new List<string>();
            var rest = text ?? "";
            while (rest.Length > max)
            {
                int cut = rest.LastIndexOf("\n\n", max, StringComparison.Ordinal);
                if (cut < max / 2) cut = rest.LastIndexOf('\n', max);
                if (cut < max / 2) cut = rest.LastIndexOf(' ', max);
                if (cut < max / 2) cut = max;
                parts.Add(rest.Substring(0, cut).Trim());
                rest = rest.Substring(cut).Trim();
            }
            if (rest.Length > 0) parts.Add(rest);
            return parts;
        }
    }

    // ---------- tolerant JSON reading (Wapilot's webhook body is only loosely documented) ----------
    static class WaJson
    {
        public static string S(JsonElement e, params string[] names)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var n in names)
            {
                JsonElement v;
                if (!e.TryGetProperty(n, out v)) continue;
                if (v.ValueKind == JsonValueKind.String) { var s = v.GetString(); if (!string.IsNullOrEmpty(s)) return s; }
                else if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
                else if (v.ValueKind == JsonValueKind.Object) { var s = S(v, "_serialized", "id", "body", "text"); if (!string.IsNullOrEmpty(s)) return s; }
            }
            return null;
        }
        public static bool? B(JsonElement e, params string[] names)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var n in names)
            {
                JsonElement v;
                if (!e.TryGetProperty(n, out v)) continue;
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText() != "0";
                if (v.ValueKind == JsonValueKind.String) { var s = v.GetString().ToLowerInvariant(); if (s == "true" || s == "1") return true; if (s == "false" || s == "0") return false; }
            }
            return null;
        }
        public static JsonElement? O(JsonElement e, params string[] names)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var n in names) { JsonElement v; if (e.TryGetProperty(n, out v) && v.ValueKind == JsonValueKind.Object) return v; }
            return null;
        }
        // "message{chat_id,text,id}" with values masked: logged when a webhook can't be understood
        public static string Shape(JsonElement e, int depth = 0)
        {
            if (e.ValueKind != JsonValueKind.Object || depth > 2) return e.ValueKind.ToString().ToLowerInvariant();
            return "{" + string.Join(",", e.EnumerateObject().Take(25).Select(p => p.Name + (p.Value.ValueKind == JsonValueKind.Object ? Shape(p.Value, depth + 1) : ""))) + "}";
        }
    }

    // ---------- chat ids ----------
    public static class WaChat
    {
        // "+20 100 123 4567", "201001234567@c.us" -> "201001234567"
        public static string Digits(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var m = Regex.Match(id.Trim(), @"^\+?(\d{6,20})(@c\.us|@s\.whatsapp\.net)?$");
            return m.Success ? m.Groups[1].Value : null;
        }
        // one key per customer: plain digits for phone numbers, the raw id otherwise (e.g. "...@lid")
        public static string Key(string id) { return Digits(id) ?? (id ?? "").Trim().ToLowerInvariant(); }
        public static bool Ignored(string id)
        {
            id = (id ?? "").ToLowerInvariant();
            return id.Length == 0 || id.EndsWith("@g.us") || id.Contains("broadcast") || id.EndsWith("@newsletter");
        }
        // the number typed by the owner (01001234567 / +201001234567 / 201001234567) -> international digits
        public static string Phone(string input)
        {
            var d = Regex.Replace(ShopifyAdmin.ToLatinDigits(input ?? ""), @"\D", "");
            if (d.StartsWith("00")) d = d.Substring(2);
            if (d.Length == 11 && d.StartsWith("01")) d = "2" + d;   // Egyptian mobile without the country code
            return d.Length >= 8 && d.Length <= 15 ? d : null;
        }
    }

    // ---------- the bot ----------
    public static class WhatsAppBot
    {
        public const int PauseHours = 2;            // after the owner answers a customer themselves
        static readonly TimeSpan Debounce = TimeSpan.FromSeconds(4);
        const string Fallback = "مقدرش أساعد في ده 🙏 بس اسألني عن أي حاجة تخص المتجر وأنا أساعدك.";
        const string MediaReply = "معلش، أنا بقرأ الرسايل المكتوبة بس 🙏 اكتبلي سؤالك وأنا أرد عليك على طول.";

        public class Incoming { public string Event, Instance, Chat, Text, Id; public bool FromMe, HasMedia; }

        public static Incoming Parse(JsonElement root)
        {
            var x = new Incoming { Event = (WaJson.S(root, "event", "type") ?? "").ToLowerInvariant(), Instance = WaJson.S(root, "instance_id", "instance", "session") };
            var msg = WaJson.O(root, "message", "payload", "data") ?? root;
            var inner = WaJson.O(msg, "message");   // some senders nest it once more
            if (inner.HasValue && WaJson.S(msg, "chat_id", "from", "chatId") == null) msg = inner.Value;
            x.FromMe = WaJson.B(msg, "from_me", "fromMe", "is_from_me") ?? WaJson.B(root, "from_me", "fromMe") ?? false;
            x.Chat = x.FromMe ? WaJson.S(msg, "chat_id", "chatId", "to", "remoteJid") : WaJson.S(msg, "chat_id", "chatId", "from", "remoteJid", "sender");
            x.Text = WaJson.S(msg, "text", "body", "caption", "content");
            x.Id = WaJson.S(msg, "id", "message_id", "messageId");
            string type = (WaJson.S(msg, "type", "message_type") ?? "").ToLowerInvariant();
            x.HasMedia = (WaJson.B(msg, "hasMedia", "has_media") ?? false) || new[] { "image", "video", "audio", "ptt", "voice", "document", "sticker" }.Contains(type);
            return x;
        }

        // Returns false when the body isn't a message we understand (logged by the caller).
        public static bool Handle(SqlConnection c, StoreInfo s, Incoming m)
        {
            if (m.Event.Length > 0 && !m.Event.StartsWith("message")) return true;   // other events (acks, status…): nothing to do
            if (string.IsNullOrEmpty(m.Chat)) return false;
            if (WaChat.Ignored(m.Chat)) return true;                                    // groups, status updates, channels
            if (!string.IsNullOrEmpty(m.Instance) && !string.Equals(m.Instance, s.WaInstance, StringComparison.OrdinalIgnoreCase)) return true;
            string key = WaChat.Key(m.Chat);
            if (!string.IsNullOrEmpty(s.WaNumber) && key == s.WaNumber) return true;     // our own number
            string text = (m.Text ?? "").Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);

            if (m.FromMe)
            {
                // our own reply echoed back (message.any) -> ignore; anything else is the owner answering by hand -> the bot steps aside for a while
                if (text.Length > 0 && IsOurs(c, s.Id, key, text)) return true;
                Save(c, s.Id, key, false, text.Length > 0 ? text : "[ملف/صورة من المتجر]", "owner:" + (m.Id ?? Guid.NewGuid().ToString("N")));
                return true;
            }

            if (text.Length == 0)
            {
                if (!m.HasMedia) return true;   // reactions, receipts…
                if (!s.WaEnabled || !Web.Allow("wa-media:" + s.Id + ":" + key, 1, TimeSpan.FromHours(1))) return true;
                var token = Secrets.Unprotect(s.WaToken);
                if (token != null) HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    try { await Wapilot.Send(token, s.WaInstance, m.Chat, MediaReply, "sb-media-" + (m.Id ?? Guid.NewGuid().ToString("N"))); }
                    catch (Exception ex) { Log.Error(ex); }
                });
                return true;
            }

            long? row = Save(c, s.Id, key, true, text, m.Id);
            if (row == null || !s.WaEnabled) return true;   // duplicate delivery, or the bot is switched off (the message is still kept as context)
            long rowId = row.Value; int storeId = s.Id; string chat = m.Chat;
            HostingEnvironment.QueueBackgroundWorkItem(ct => Reply(storeId, key, chat, rowId, ct));
            return true;
        }

        // null = already have it (same message id, or the same text from the same customer in the last minute)
        public static long? Save(SqlConnection c, int storeId, string chatKey, bool fromCustomer, string body, string msgId)
        {
            const string sql = @"
IF @fc = 1 AND EXISTS (SELECT 1 FROM dbo.WaMessages WHERE StoreId=@s AND ChatId=@c AND FromCustomer=1 AND Body=@b AND CreatedAt > DATEADD(SECOND,-60,SYSUTCDATETIME()))
  SELECT CAST(NULL AS BIGINT);
ELSE
  INSERT INTO dbo.WaMessages(StoreId, ChatId, FromCustomer, Body, MsgId) OUTPUT INSERTED.Id VALUES(@s, @c, @fc, @b, @m);";
            try
            {
                using (var cmd = Db.Cmd(c, sql, "@s", storeId, "@c", chatKey.Length > 100 ? chatKey.Substring(0, 100) : chatKey, "@fc", fromCustomer,
                    "@b", body.Length > 4000 ? body.Substring(0, 4000) : body, "@m", msgId == null ? null : (msgId.Length > 150 ? msgId.Substring(0, 150) : msgId)))
                {
                    var v = cmd.ExecuteScalar();
                    return v == null || v == DBNull.Value ? (long?)null : Convert.ToInt64(v);
                }
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return null; }
        }

        static bool IsOurs(SqlConnection c, int storeId, string chatKey, string text)
        {
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(*) FROM dbo.WaMessages WHERE StoreId=@s AND ChatId=@c AND FromCustomer=0 AND MsgId IS NULL
                AND CreatedAt > DATEADD(MINUTE,-30,SYSUTCDATETIME()) AND CHARINDEX(@t, Body) > 0", "@s", storeId, "@c", chatKey, "@t", text))
                return (int)cmd.ExecuteScalar() > 0;
        }

        class Row { public long Id; public bool Customer, Owner; public string Body; public DateTime At; }

        static async Task Reply(int storeId, string chatKey, string chat, long rowId, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Debounce, ct);
                using (var c = Db.Open())
                {
                    var rows = new List<Row>();
                    using (var cmd = Db.Cmd(c, @"SELECT TOP 40 Id, FromCustomer, Body, MsgId, CreatedAt FROM dbo.WaMessages
                        WHERE StoreId=@s AND ChatId=@c AND CreatedAt > DATEADD(HOUR,-24,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", storeId, "@c", chatKey))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) rows.Add(new Row { Id = (long)r["Id"], Customer = (bool)r["FromCustomer"], Body = (string)r["Body"],
                            Owner = (r["MsgId"] as string ?? "").StartsWith("owner:"), At = DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc) });
                    // a newer message came in (its own run answers everything), or something was already said after this one
                    if (rows.Count == 0 || rows[0].Id != rowId) return;
                    // the owner is talking to this customer themselves
                    if (rows.Any(x => x.Owner && x.At > DateTime.UtcNow.AddHours(-PauseHours))) return;

                    var s = Db.StoreById(c, storeId);
                    if (s == null || s.Status != "active" || !s.WaEnabled || string.IsNullOrEmpty(s.WaInstance)) return;
                    var token = Secrets.Unprotect(s.WaToken);
                    if (token == null) return;
                    if (Billing.Expired(s)) { SetStatus(c, s.Id, "⚠️ الباقة خلصت، فالمساعد مش بيرد على الواتساب لحد ما تجدّدها."); return; }
                    if (Db.Usage(c, s.Id, Db.Month).Weighted >= Billing.TotalLimit(c, s)) { SetStatus(c, s.Id, "⚠️ رصيد الشهر خلص، فالمساعد مش بيرد على الواتساب لحد ما تزوّد الرصيد أو يبدأ شهر جديد."); return; }
                    if (!Web.Allow("wa-chat:" + s.Id + ":" + chatKey, 30, TimeSpan.FromHours(1))) return;   // one customer can't burn the quota
                    try { Shopify.RefreshIfOld(s); } catch (Exception ex) { Log.Error(ex); }

                    ChatHandler.LoopResult res;
                    try { res = await ChatHandler.Answer(c, s, History(rows), "wa:" + chatKey, ChannelNote(s, chatKey)); }
                    catch (Exception ex) { Log.Error(ex); SetStatus(c, s.Id, "⚠️ حصلت مشكلة وإحنا بنجهّز رد على آخر رسالة. لو اتكررت كلّم الدعم."); return; }
                    Db.AddUsage(c, s.Id, 1, res.In, res.CacheRead, res.CacheWrite, res.Out);

                    string answer = Wapilot.Format(string.IsNullOrWhiteSpace(res.Text) ? Fallback : res.Text);
                    // saved before sending: Wapilot may echo it back (message.any) before Send returns
                    long? mine = Save(c, s.Id, chatKey, false, answer, null);
                    try
                    {
                        int i = 0;
                        foreach (var part in Wapilot.Split(answer)) await Wapilot.Send(token, s.WaInstance, chat, part, "sb-" + rowId + "-" + (i++));
                        SetStatus(c, s.Id, null);
                    }
                    catch (WaException ex)
                    {
                        if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                        SetStatus(c, s.Id, "⚠️ " + ex.Message);
                    }
                    if (new Random().Next(200) == 0) Cleanup(c);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error(ex); }
        }

        // oldest first; messages in a row from the same side become one turn; starts and ends with the customer
        static List<MessageParam> History(List<Row> newestFirst)
        {
            var turns = new List<KeyValuePair<bool, string>>();
            foreach (var r in Enumerable.Reverse(newestFirst))
            {
                if (turns.Count > 0 && turns[turns.Count - 1].Key == r.Customer)
                    turns[turns.Count - 1] = new KeyValuePair<bool, string>(r.Customer, turns[turns.Count - 1].Value + "\n" + r.Body);
                else turns.Add(new KeyValuePair<bool, string>(r.Customer, r.Body));
            }
            if (turns.Count > 12) turns = turns.Skip(turns.Count - 12).ToList();
            while (turns.Count > 0 && !turns[0].Key) turns.RemoveAt(0);
            var list = new List<MessageParam>();
            foreach (var t in turns)
            {
                var body = t.Value.Length > 4000 ? t.Value.Substring(t.Value.Length - 4000) : t.Value;
                if (t.Key) list.Add(new UserMessageParam { Content = body }); else list.Add(new AssistantMessageParam { Content = body });
            }
            return list;
        }

        static string ChannelNote(StoreInfo s, string chatKey)
        {
            var sb = new StringBuilder("المحادثة دي على واتساب (رقم المتجر)، مش على الموقع:\n")
                .Append("- خلي ردودك قصيرة زي رسايل الواتساب العادية.\n")
                .Append("- ماتستخدمش جداول ولا عناوين. للخط العريض استخدم نجمة واحدة *كده*، وللقوائم • أو أرقام.\n")
                .Append("- اكتب اللينكات كاملة (https://...) مش [نص](لينك).\n")
                .Append("- لو فيه رسايل في المحادثة من المتجر نفسه (صاحب المتجر رد بنفسه)، كمّل على كلامه وماتناقضهوش.");
            var phone = WaChat.Digits(chatKey);
            if (phone != null && s.ShopifyOrders && !string.IsNullOrEmpty(s.ShopifyClientId))
                sb.Append("\n- رقم واتساب الزبون: +").Append(phone).Append(". لو سأل عن أوردر وقال إنه طلب بنفس الرقم ده، استخدمه كرقم الموبايل في أداة الأوردرات من غير ما تطلبه منه تاني.");
            return sb.ToString();
        }

        public static void SetStatus(SqlConnection c, int storeId, string status)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaStatus=@st" + (status == null ? ", WaLastAt=SYSUTCDATETIME()" : "") + " WHERE Id=@s", "@st", status, "@s", storeId)) cmd.ExecuteNonQuery();
        }

        static void Cleanup(SqlConnection c)
        {
            using (var cmd = Db.Cmd(c, "DELETE TOP (5000) FROM dbo.WaMessages WHERE CreatedAt < DATEADD(DAY,-60,SYSUTCDATETIME())")) cmd.ExecuteNonQuery();
        }

        public static string NewKey()
        {
            var b = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")));
        }
    }

    // api/wa-webhook.ashx?store=<slug>&key=<secret> — Wapilot calls this for every WhatsApp message.
    public class WhatsAppWebhookHandler : IHttpHandler
    {
        static DateTime lastShapeLog = DateTime.MinValue;
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            string slug = (req.QueryString["store"] ?? "").ToLowerInvariant(), key = req.QueryString["key"] ?? "";
            if (!Web.Allow("wa-hook:" + Web.Ip(req), 600, TimeSpan.FromMinutes(1))) { res.StatusCode = 429; return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Web.SlugRx.IsMatch(slug) ? Db.StoreBySlug(c, slug) : null;
                    if (s == null || string.IsNullOrEmpty(s.WaHookKey) || !Secrets.Same(Encoding.UTF8.GetBytes(s.WaHookKey), Encoding.UTF8.GetBytes(key)))
                    { res.StatusCode = 401; return; }
                    if (req.HttpMethod == "GET" || req.HttpMethod == "HEAD") { Web.Json(res, 200, new { ok = true }); return; }   // "test URL" buttons
                    if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }

                    string body;
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                    if (body.Length > 512 * 1024) { res.StatusCode = 413; return; }
                    JsonElement root;
                    try { using (var doc = JsonDocument.Parse(body)) root = doc.RootElement.Clone(); }
                    catch { Web.Json(res, 400, new { ok = false }); return; }

                    // one body may carry one message or a list of them
                    var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().Take(20).ToList() : new List<JsonElement> { root };
                    foreach (var item in items)
                    {
                        var m = WhatsAppBot.Parse(item);
                        if (!WhatsAppBot.Handle(c, s, m) && DateTime.UtcNow - lastShapeLog > TimeSpan.FromMinutes(10))
                        {
                            lastShapeLog = DateTime.UtcNow;
                            Log.Error(new Exception("WhatsApp webhook not understood (store " + s.Slug + "): " + WaJson.Shape(item)));
                        }
                    }
                    Web.Json(res, 200, new { ok = true });
                }
            }
            catch (Exception ex) { Log.Error(ex); res.StatusCode = 500; }
        }
    }

    // api/whatsapp.ashx — the store owner connects the store's WhatsApp (Wapilot), switches the bot on/off, tests it.
    public class WhatsAppHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        static string HookUrl(HttpRequest req, StoreInfo s)
        {
            string host = Web.Setting("CanonicalHost");
            string origin = string.IsNullOrWhiteSpace(host) ? req.Url.Scheme + "://" + req.Url.Authority : "https://" + host.Trim();
            return origin + req.ApplicationPath.TrimEnd('/') + "/api/wa-webhook.ashx?store=" + s.Slug + "&key=" + s.WaHookKey;
        }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }

                    if (req.HttpMethod == "GET") { Web.Json(res, 200, await State(c, req, s)); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!Web.Allow("wa-own:" + s.Id, 40, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                    var body = Web.Body(req, 4000);

                    if (action == "connect")
                    {
                        string token = (Web.Str(body, "token", 600) ?? "").Trim(), pick = (Web.Str(body, "instance", 150) ?? "").Trim();
                        if (token.Length == 0 && !string.IsNullOrEmpty(s.WaToken)) token = Secrets.Unprotect(s.WaToken) ?? "";   // choosing another number later
                        if (token.Length < 10 || token.Length > 600 || token.Contains(" ")) { Web.Error(res, 400, "bad_token", "الـ API token مش مظبوط. انسخه زي ما هو من Wapilot."); return; }
                        List<Wapilot.Instance> list;
                        try { list = await Wapilot.Instances(token); }
                        catch (WaException ex) { Web.Error(res, 400, "wapilot", ex.Message); return; }
                        var usable = list.Where(i => i.IsApi).ToList();
                        if (usable.Count == 0) usable = list;
                        if (usable.Count == 0) { Web.Error(res, 400, "no_instances", "مفيش أي رقم على حساب Wapilot ده. اربط رقم واتساب المتجر على Wapilot الأول (بالـ QR)، وبعدين جرّب تاني."); return; }
                        Wapilot.Instance chosen = pick.Length > 0 ? usable.FirstOrDefault(i => string.Equals(i.Id, pick, StringComparison.OrdinalIgnoreCase)) : (usable.Count == 1 ? usable[0] : null);
                        if (chosen == null)
                        {
                            Web.Json(res, 200, new Dictionary<string, object> { { "choose", usable.Select(i => new Dictionary<string, object> {
                                { "id", i.Id }, { "name", i.Name }, { "number", i.Number ?? "" }, { "status", i.Status } }).ToList() } });
                            return;
                        }
                        string hookKey = string.IsNullOrEmpty(s.WaHookKey) ? WhatsAppBot.NewKey() : s.WaHookKey;   // same URL on reconnect
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaInstance=@i, WaToken=@t, WaHookKey=@k, WaNumber=@n, WaEnabled=1, WaStatus=NULL WHERE Id=@s",
                            "@i", chosen.Id, "@t", Secrets.Protect(token), "@k", hookKey, "@n", chosen.Number, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, req, Db.StoreById(c, s.Id)));
                        return;
                    }
                    if (string.IsNullOrEmpty(s.WaInstance)) { Web.Error(res, 400, "not_connected", "اربط الواتساب الأول."); return; }

                    if (action == "enable")
                    {
                        object en; bool on = body.TryGetValue("enabled", out en) && en is bool && (bool)en;
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaEnabled=@e WHERE Id=@s", "@e", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, new { ok = true, enabled = on });
                        return;
                    }
                    if (action == "test")
                    {
                        if (!Web.Allow("wa-test:" + s.Id, 6, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "بعت رسايل تجربة كتير، استنى شوية."); return; }
                        string phone = WaChat.Phone(Web.Str(body, "phone", 40));
                        if (phone == null) { Web.Error(res, 400, "bad_phone", "اكتب رقم موبايل صح بكود الدولة، مثلًا 201001234567."); return; }
                        string text = "✅ رسالة تجربة من " + s.Name + ": الواتساب مربوط بـ Sellybot. ابعت أي سؤال من رقم تاني والمساعد هيرد عليه.";
                        var mine = WhatsAppBot.Save(c, s.Id, phone, false, text, null);   // so the echo (message.any) isn't taken for the owner typing
                        try { await Wapilot.Send(Secrets.Unprotect(s.WaToken), s.WaInstance, phone, text, null); }
                        catch (WaException ex)
                        {
                            if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                            Web.Error(res, 400, "wapilot", ex.Message); return;
                        }
                        Web.Json(res, 200, new { ok = true });
                        return;
                    }
                    if (action == "new-key")
                    {
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaHookKey=@k WHERE Id=@s", "@k", WhatsAppBot.NewKey(), "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, req, Db.StoreById(c, s.Id)));
                        return;
                    }
                    if (action == "disconnect")
                    {
                        using (var cmd = Db.Cmd(c, @"UPDATE dbo.Stores SET WaInstance=NULL, WaToken=NULL, WaHookKey=NULL, WaNumber=NULL, WaStatus=NULL, WaEnabled=0, WaLastAt=NULL WHERE Id=@s;
                            DELETE FROM dbo.WaMessages WHERE StoreId=@s;", "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, new { ok = true });
                        return;
                    }
                    Web.Error(res, 400, "bad_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static async Task<Dictionary<string, object>> State(SqlConnection c, HttpRequest req, StoreInfo s)
        {
            bool on = !string.IsNullOrEmpty(s.WaInstance);
            var d = new Dictionary<string, object> { { "connected", on } };
            if (!on) return d;
            d["instance"] = s.WaInstance; d["number"] = s.WaNumber ?? ""; d["enabled"] = s.WaEnabled;
            d["status"] = s.WaStatus ?? ""; d["lastAt"] = s.WaLastAt.HasValue ? s.WaLastAt.Value.ToString("o") : "";
            d["webhookUrl"] = HookUrl(req, s); d["pauseHours"] = WhatsAppBot.PauseHours;
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(DISTINCT ChatId), ISNULL(SUM(CASE WHEN FromCustomer=0 AND MsgId IS NULL THEN 1 ELSE 0 END),0),
                    (SELECT MAX(CreatedAt) FROM dbo.WaMessages WHERE StoreId=@s AND FromCustomer=1)
                FROM dbo.WaMessages WHERE StoreId=@s AND CreatedAt > DATEADD(DAY,-30,SYSUTCDATETIME())", "@s", s.Id))
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                d["chats30"] = Convert.ToInt32(r[0]); d["replies30"] = Convert.ToInt32(r[1]);
                d["lastIncoming"] = r[2] == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)r[2], DateTimeKind.Utc).ToString("o");
            }
            // live state from Wapilot (is the phone still linked?)
            try
            {
                var token = Secrets.Unprotect(s.WaToken);
                var st = token == null ? null : await Wapilot.Status(token, s.WaInstance);
                d["live"] = st == null ? "" : st.Status; if (st != null && !string.IsNullOrEmpty(st.Number)) d["number"] = st.Number;
            }
            catch (WaException ex) { d["live"] = ""; d["liveError"] = ex.Message; }
            return d;
        }
    }
}
