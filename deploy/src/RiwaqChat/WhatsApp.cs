// WhatsApp: the store's own WhatsApp number answers customers with the same assistant as the site chat (same data, rules,
// Shopify tools and monthly quota).
//
// Since 2026-11-28 the linked-device part is Sellybot's own server (deploy/wa-bridge, Baileys; WaBridge.cs), AppSettings
// WaProvider = "bridge" (default). Sellybot creates every number on it by itself, and the owner only scans the QR. The class
// Wapilot below stays the one door every sender uses: with the bridge's token (WaBridge.Is) each call goes to WaBridge, otherwise
// to the Wapilot API as before (WaProvider = "wapilot", or a store still on its own Wapilot token). What follows describes Wapilot.
//
// Two ways to connect (see WaAccount):
// - Sellybot's account (default): the super admin saves ONE Wapilot token (admin.html → الواتساب) and creates numbers
//   (instances) in the Wapilot app; the API can't create them. A store owner presses "اربط واتساب المتجر", gets a free
//   number, and scans its QR (polled from Wapilot) with the store's phone. All numbers post to one webhook URL
//   (api/wa-webhook.ashx?key=<platform secret>) and messages are routed to the store by instance_id.
// - The owner's own Wapilot account: they paste their own API token, then paste our per-store webhook URL into Wapilot
//   (events "message", and "message.any" if they want the bot to step aside when they answer a customer themselves).
//
// Flow: Wapilot -> api/wa-webhook.ashx?[store=<slug>&]key=<secret>  (Wapilot signs nothing, so the URL carries a secret)
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

        static Task<JsonElement> Call(HttpMethod method, string path, string token, object body = null, string idem = null)
        {
            return Call(method, path, token, body == null ? null : (Func<HttpContent>)(() => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")), idem);
        }

        // content: made fresh for each attempt (a request's content can't be sent twice), null for none
        static async Task<JsonElement> Call(HttpMethod method, string path, string token, Func<HttpContent> content, string idem)
        {
            var req = new HttpRequestMessage(method, Base + path);
            // Wapilot reads either header (Authorization wins). Both go out, so a host that strips the non-standard "token" header still authenticates.
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("token", token);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (idem != null) req.Headers.TryAddWithoutValidation("Idempotency-Key", idem);
            if (content != null) req.Content = content();
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req); }
            catch (Exception ex) { Log.Error(ex); throw new WaException("مقدرناش نوصل لخدمة الواتساب دلوقتي. جرّب تاني كمان شوية."); }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync();
                JsonElement root = default(JsonElement);
                bool parsed = false;
                try { using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)) { root = doc.RootElement.Clone(); parsed = true; } } catch { }
                int status = (int)resp.StatusCode;
                if (status >= 200 && status < 300 && parsed) return root;
                string code = parsed ? WaJson.S(root, "code") : null, msg = parsed ? WaJson.S(root, "message") : null;
                if (!parsed) msg = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"<[^>]*>|\s+", " ").Trim();   // not Wapilot's JSON (a proxy page?)
                if (status >= 400) Log.Error(new Exception("Wapilot " + method + " " + path.Split('/')[0] + " -> " + status + " " + code + " " + (msg ?? "")));
                throw Friendly(status, code, msg);
            }
        }

        // e.g. INSTANCE_NOT_API_SUBSCRIPTION (seen 2026-11-23: a number on the account with no "API" plan paid for)
        public static bool NoSubscription(string code) { return (code ?? "").IndexOf("SUBSCRIPTION", StringComparison.OrdinalIgnoreCase) >= 0; }

        static WaException Friendly(int status, string code, string msg)
        {
            code = code ?? "";
            string text;
            if (status == 401 || code == "TOKEN_INVALID")
            {
                var why = (msg ?? "").Trim(); if (why.Length > 120) why = why.Substring(0, 120);
                // Store owners can see these: never name the provider (Sellybot keeps it private)
                text = "مفتاح خدمة الواتساب مش صح أو انتهى."
                     + " [" + status + (code.Length > 0 ? " " + code : "") + (why.Length > 0 ? ": " + why : "") + "]";
            }
            else if (code == "INSTANCE_FORBIDDEN") text = "رقم الربط ده مش تبع الحساب ده.";
            else if (code == "INSTANCE_NOT_READY") text = "رقم الواتساب مش متصل دلوقتي. اربطه تاني بالـ QR.";
            // the number has no (or an ended) subscription on Sellybot's provider account: ours to fix, nothing for the owner to do
            else if (NoSubscription(code)) text = "رقم ربط الواتساب محتاج تجديد من عندنا، ومش محتاج تعمل أي حاجة. بلّغنا فريق الدعم وهنظبطه في أقرب وقت.";
            else if (status == 404) text = "رقم الربط مش موجود. كلّم الدعم.";
            else if (status == 429) text = "الطلبات كتير دلوقتي، هنحاول تاني بعد شوية.";
            else text = "خدمة الواتساب مش متاحة دلوقتي (" + status + (code.Length > 0 ? " " + code : "") + "). جرّب تاني كمان شوية.";
            return new WaException(text, status, code);
        }

        public static async Task<List<Instance>> Instances(string token)
        {
            if (WaBridge.Is(token)) return await WaBridge.Instances(token);
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
            if (WaBridge.Is(token)) return await WaBridge.Status(token, instance);
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

        // QR to link a phone to the instance: a "data:image/png;base64,..." string, or null when none is available
        // (already linked, or the session is stopped). Needs a QR instance (not Official).
        public static async Task<string> Qr(string token, string instance)
        {
            if (WaBridge.Is(token)) return await WaBridge.Qr(token, instance);
            try
            {
                var r = await Call(HttpMethod.Get, "instances/" + Uri.EscapeDataString(instance) + "/qr-code", token);
                var qr = WaJson.S(r, "qr", "qr_code", "qrcode");
                return string.IsNullOrEmpty(qr) ? null : (qr.StartsWith("data:image/") ? qr : "data:image/png;base64," + qr);
            }
            catch (WaException ex) when (ex.Status == 404) { return null; }
            catch (WaException ex) when (ex.Status == 409 || (ex.Code ?? "").StartsWith("PASSKEY"))
            {
                throw new WaException("واتساب طالب تأكيد إضافي قبل الربط. كلّم الدعم وهنكمّله معاك.", ex.Status, ex.Code);
            }
        }

        // Session controls for QR instances: start (so a QR is produced), logout (unlinks the phone).
        public static Task Start(string token, string instance) { if (WaBridge.Is(token)) return WaBridge.Start(token, instance); return Call(HttpMethod.Post, "instances/" + Uri.EscapeDataString(instance) + "/start", token, new Dictionary<string, object>()); }
        public static Task Logout(string token, string instance) { if (WaBridge.Is(token)) return WaBridge.Logout(token, instance); return Call(HttpMethod.Post, "instances/" + Uri.EscapeDataString(instance) + "/logout", token, new Dictionary<string, object>()); }

        // A number the store no longer needs: the bridge deletes it (it made it for this store); on Wapilot the phone is logged out
        // and the number goes back to the pool.
        public static Task Release(string token, string instance) { return WaBridge.Is(token) ? WaBridge.Delete(token, instance) : Logout(token, instance); }

        // A new number for a store: only the bridge can make one (Wapilot's numbers are made in its app)
        public static Task<Instance> Create(string token, string name) { if (!WaBridge.Is(token)) throw new WaException("مفيش رقم ربط فاضي دلوقتي.", 409, "NO_CREATE"); return WaBridge.Create(token, name); }

        // "typing…" while the assistant writes: the bridge only (Wapilot has nothing like it)
        public static Task Typing(string token, string instance, string chatId) { return WaBridge.Is(token) ? WaBridge.Typing(token, instance, chatId) : Task.CompletedTask; }

        // Which send route works for an instance (QR "/{id}/send-message" or Official "/official/{id}/send-message"), learned on first use.
        static readonly ConcurrentDictionary<string, bool> Official = new ConcurrentDictionary<string, bool>();

        // Returns Wapilot's message_id (null when it gives none). The message is only queued at Wapilot: MessageStatus tells if it went out.
        public static Task<string> Send(string token, string instance, string chatId, string text, string idem)
        {
            if (WaBridge.Is(token)) return WaBridge.Send(token, instance, chatId, text, idem);
            var body = new Dictionary<string, object> { { "chat_id", chatId }, { "text", text } };
            return Queue(token, instance, "send-message", () => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), idem);
        }

        // A picture (jpg / png / webp; Wapilot takes up to 10 MB) with an optional caption (Official numbers: up to 1024 characters)
        public static Task<string> SendImage(string token, string instance, string chatId, byte[] image, string contentType, string caption, string idem)
        {
            if (WaBridge.Is(token)) return WaBridge.SendImage(token, instance, chatId, image, contentType, caption, idem);
            string ext = contentType == "image/png" ? "png" : contentType == "image/webp" ? "webp" : "jpg";
            return Queue(token, instance, "send-image", () =>
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(chatId, Encoding.UTF8), "chat_id");
                var pic = new ByteArrayContent(image);
                pic.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                form.Add(pic, "media", "offer." + ext);
                if (!string.IsNullOrEmpty(caption)) form.Add(new StringContent(caption, Encoding.UTF8), "caption");
                return form;
            }, idem);
        }

        // QR numbers: /{id}/<what>; Official ones: /official/{id}/<what>. The QR route is tried first and the one that worked is remembered.
        static async Task<string> Queue(string token, string instance, string what, Func<HttpContent> content, string idem)
        {
            string id = Uri.EscapeDataString(instance);
            bool official;
            if (Official.TryGetValue(instance, out official) && official) return WaJson.S(await Call(HttpMethod.Post, "official/" + id + "/" + what, token, content, idem), "message_id");
            try
            {
                var r = await Call(HttpMethod.Post, id + "/" + what, token, content, idem);
                Official[instance] = false;
                return WaJson.S(r, "message_id");
            }
            catch (WaException ex) when (ex.Status == 404 || ex.Code == "MESSAGE_TYPE_UNSUPPORTED" || (ex.Code ?? "").Contains("OFFICIAL"))
            {
                var r = await Call(HttpMethod.Post, "official/" + id + "/" + what, token, content, idem);
                Official[instance] = true;
                return WaJson.S(r, "message_id");
            }
        }

        // What happened to a queued message: "pending" (still in Wapilot's queue), "delivered" (it went out) or "failed" (Error = why),
        // or null when Wapilot doesn't know it. Wapilot's finished messages are "delivered", or carry an error_code / error_type.
        public class Sent { public string Status, Error; }
        public static async Task<Sent> MessageStatus(string token, string instance, string messageId)
        {
            if (WaBridge.Is(token)) return await WaBridge.MessageStatus(token, instance, messageId);
            bool official;
            string path = (Official.TryGetValue(instance, out official) && official ? "official/" : "") + Uri.EscapeDataString(instance) + "/messages/" + Uri.EscapeDataString(messageId);
            JsonElement r;
            try { r = await Call(HttpMethod.Get, path, token); }
            catch (WaException ex) when (ex.Status == 404) { return null; }
            var d = WaJson.O(r, "data") ?? r;
            string status = (WaJson.S(d, "status") ?? "").ToLowerInvariant(), err = WaJson.S(d, "error_code", "error_type");
            if (!string.IsNullOrEmpty(err) || status.Contains("fail") || status.Contains("error") || status.Contains("reject") || status == "cancelled")
                return new Sent { Status = "failed", Error = err ?? status };
            if (status == "" || status == "pending" || status == "queued" || status == "processing" || status == "sending" || status == "scheduled") return new Sent { Status = "pending" };
            return new Sent { Status = "delivered" };
        }

        // Reply buttons exist only on Official (Meta) numbers. True when sent, false when the number is a QR one (send text instead).
        public static async Task<bool> TrySendButtons(string token, string instance, string chatId, string body, string[][] buttons, string idem)
        {
            if (WaBridge.Is(token)) return false;   // a linked device has no reply buttons: the text with 1 / 2 goes instead
            bool official;
            if (!Official.TryGetValue(instance, out official) || !official) return false;
            var btns = buttons.Select(b => new Dictionary<string, object> { { "id", b[0] }, { "title", b[1] } }).ToList();
            var payload = new Dictionary<string, object> { { "chat_id", chatId }, { "interactive", new Dictionary<string, object> { { "body", body }, { "buttons", btns } } } };
            try { await Call(HttpMethod.Post, "official/" + Uri.EscapeDataString(instance) + "/send-buttons", token, payload, idem); return true; }
            catch (WaException ex) when (ex.Code == "MESSAGE_TYPE_UNSUPPORTED") { return false; }
        }

        // WhatsApp may hide a customer's number behind a "...@lid" id; Wapilot can map it back to the phone number.
        static readonly ConcurrentDictionary<string, string> Lids = new ConcurrentDictionary<string, string>();
        public static async Task<string> LidPhone(string token, string instance, string lid)
        {
            if (WaBridge.Is(token)) return await WaBridge.LidPhone(token, instance, lid);
            string hit;
            if (Lids.TryGetValue(lid, out hit)) return hit;
            try
            {
                var r = await Call(HttpMethod.Get, Uri.EscapeDataString(instance) + "/lids/" + Uri.EscapeDataString(lid), token);
                string found = null;
                Action<JsonElement> walk = null;
                walk = e =>
                {
                    if (found != null) return;
                    if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) walk(p.Value);
                    else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) walk(x);
                    else if (e.ValueKind == JsonValueKind.String) { var d = WaChat.Digits(e.GetString()); if (d != null && !e.GetString().Contains("@lid")) found = d; }
                };
                walk(r);
                if (found != null) Lids[lid] = found;
                return found;
            }
            catch (WaException) { return null; }
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

    // ---------- whose Wapilot account a store uses ----------
    // "own": the store owner pasted their own Wapilot token (Stores.WaToken).
    // "platform": Sellybot's single Wapilot account (token in AppSettings); the super admin assigned one of its numbers
    //             (Stores.WaInstance, WaToken NULL) and the owner links the store's phone by scanning that number's QR.
    public static class WaAccount
    {
        public static bool IsPlatform(StoreInfo s) { return s != null && string.IsNullOrEmpty(s.WaToken) && !string.IsNullOrEmpty(s.WaInstance); }

        // Sellybot's own account: the bridge (WaBridge.Token, the default) or the Wapilot token. null = not set up yet.
        public static string PlatformToken(SqlConnection c)
        {
            if (WaBridge.On(c)) return WaBridge.Token(c);
            var box = Billing.Get(c, "WaPlatformToken"); return string.IsNullOrEmpty(box) ? null : Secrets.Unprotect(box);
        }

        public static string PlatformHookKey(SqlConnection c, bool create)
        {
            var k = Billing.Get(c, "WaPlatformHookKey");
            if (string.IsNullOrEmpty(k) && create) { k = WhatsAppBot.NewKey(); Billing.Set(c, "WaPlatformHookKey", k); }
            return k;
        }

        public static string Token(SqlConnection c, StoreInfo s) { return IsPlatform(s) ? PlatformToken(c) : Secrets.Unprotect(s.WaToken); }

        public static string Origin(HttpRequest req)
        {
            string host = Web.Setting("CanonicalHost");
            string origin = string.IsNullOrWhiteSpace(host) ? req.Url.Scheme + "://" + req.Url.Authority : "https://" + host.Trim();
            return origin + req.ApplicationPath.TrimEnd('/');
        }

        // the one webhook URL for every number on the platform account (messages are routed by instance_id)
        public static string PlatformHookUrl(HttpRequest req, SqlConnection c) { return Origin(req) + "/api/wa-webhook.ashx?key=" + PlatformHookKey(c, true); }

        // Wapilot session states (WAHA-style): WORKING = a phone is linked; STOPPED/FAILED = needs a start to show a QR
        public static bool Linked(string upper) { return upper == "WORKING" || upper == "CONNECTED" || upper == "AUTHENTICATED" || upper == "OPEN" || upper == "READY"; }
        public static bool Stopped(string upper) { return upper == "STOPPED" || upper == "FAILED" || upper == "DISCONNECTED" || upper == "CLOSED" || upper == "LOGGED_OUT" || upper == ""; }

        // ---------- the WhatsApp service: its own monthly subscription (AppSettings "WaAddonPrice", 300 EGP by default) ----------
        // While Stores.WaUntil is in the future the store may take a number, and the bot, order confirmations, delivery messages and
        // chat-order codes go out on its WhatsApp. Each paid month adds ServiceDays: from today, or after the current end while it still runs.
        // It renews on its own cycle, never with the plan: any store can ask for it at any time (with a plan request, or alone),
        // and renewing the plan leaves it alone. The bot's replies still count from the plan's quota, the same as the site chat.
        // The admin's own store always has it. A plan that includes WhatsApp (Plans.IncludesWa, «الاقتصادية») turns it on while that
        // plan runs, with nothing to buy on its own; leaving it for another plan keeps the time already paid for (Billing.Upgrade).
        public const int ServiceDays = 30;
        public static decimal ServicePrice(SqlConnection c) { var p = Billing.Dec(Billing.Get(c, "WaAddonPrice"), 300); return p > 0 ? p : 300; }
        public static bool HasService(StoreInfo s) { return s != null && (s.IsAdmin || (s.WaUntil.HasValue && s.WaUntil.Value > DateTime.UtcNow) || Billing.WaInPlan(s)); }
        // Until when it runs: the service's own end, or the end of the plan that includes it when that's later (null = never had it)
        public static DateTime? Until(StoreInfo s)
        {
            if (s == null) return null;
            var u = s.WaUntil;
            if (Billing.WaInPlan(s) && (!u.HasValue || s.PlanEndsAt.Value > u.Value)) u = s.PlanEndsAt;
            return u;
        }

        // A paid period of the service: its own payment row (Subscriptions.Kind 'wa') and the store's new WaUntil. Returns the new end.
        public static DateTime AddService(SqlConnection c, StoreInfo s, int planId, int months, decimal price, decimal paid, string method, string notes, out int subscriptionId)
        {
            var now = DateTime.UtcNow;
            var start = s.WaUntil.HasValue && s.WaUntil.Value > now ? s.WaUntil.Value : now;
            var end = start.AddDays(ServiceDays * Math.Max(1, months));
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.Subscriptions(StoreId, PlanId, PlanName, Months, PriceEgp, DiscountEgp, PaidEgp, PaymentMethod, Notes, StartsAt, EndsAt, Kind)
                                             OUTPUT INSERTED.Id VALUES (@s, @p, @n, @m, @price, @disc, @paid, @method, @notes, @start, @end, 'wa')",
                    "@s", s.Id, "@p", planId > 0 ? (object)planId : DBNull.Value, "@n", ServiceName, "@m", Math.Max(1, months), "@price", price, "@disc", Math.Max(0, price - paid), "@paid", paid,
                    "@method", (object)method ?? DBNull.Value, "@notes", (object)notes ?? DBNull.Value, "@start", start, "@end", end))
                { cmd.Transaction = tx; subscriptionId = (int)cmd.ExecuteScalar(); }
                // the "service ended" warning the bot left goes with it
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaUntil=@end, WaStatus=CASE WHEN WaStatus LIKE @lapsed THEN NULL ELSE WaStatus END WHERE Id=@s",
                    "@end", end, "@lapsed", "%" + ServiceName + " خلصت%", "@s", s.Id)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            s.WaUntil = end;
            return end;
        }
        public const string ServiceName = "خدمة الواتساب";

        // Days before the end when the dashboard starts warning on every page (with the date and a renew button)
        public const int WarnDays = 2;

        // For the dashboard: is it on, until when, what a month costs, a renewal already asked for, and this month's WhatsApp numbers
        public static Dictionary<string, object> ServiceJson(SqlConnection c, StoreInfo s)
        {
            var now = DateTime.UtcNow;
            int replies = 0, chats = 0; bool pending;
            using (var cmd = Db.Cmd(c, @"SELECT ISNULL(SUM(CASE WHEN FromCustomer=0 AND MsgId IS NULL THEN 1 ELSE 0 END),0), COUNT(DISTINCT CASE WHEN FromCustomer=1 THEN ChatId END)
                FROM dbo.WaMessages WHERE StoreId=@s AND CreatedAt >= @m", "@s", s.Id, "@m", new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { replies = Convert.ToInt32(r[0]); chats = Convert.ToInt32(r[1]); }
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.PlanRequests WHERE StoreId=@s AND Status='pending' AND WaAddon=1", "@s", s.Id)) pending = (int)cmd.ExecuteScalar() > 0;
            var until = Until(s);
            // more than one number (WaLines.cs): how many it's paid for, each one's price, and what a month of them comes to
            bool inPlan = !s.IsAdmin && Billing.WaInPlan(s), paidOn = s.WaUntil.HasValue && s.WaUntil.Value > now;
            int count = Math.Max(1, s.WaCount), lines = string.IsNullOrEmpty(s.WaInstance) ? 0 : 1 + WaLines.Extra(c, s.Id);
            decimal unit = WaLines.UnitPrice(c, count + (inPlan ? 1 : 0));
            return new Dictionary<string, object> {
                { "count", count }, { "unit", unit }, { "monthly", count * unit }, { "multi", WaLines.MultiPrice(c) }, { "lines", lines },
                { "allowed", WaLines.Allowed(s) }, { "paidOn", paidOn }, { "paidUntil", s.WaUntil.HasValue ? s.WaUntil.Value.ToString("o") : "" },
                { "on", HasService(s) }, { "until", until.HasValue ? until.Value.ToString("o") : "" }, { "always", s.IsAdmin },
                { "daysLeft", until.HasValue ? Math.Max(0, (int)Math.Ceiling((until.Value - now).TotalDays)) : 0 }, { "warnDays", WarnDays },
                // on through a plan that includes it: it renews with the plan (no separate renewal, no reminder of its own)
                { "inPlan", !s.IsAdmin && Billing.WaInPlan(s) }, { "planName", s.PlanName ?? "" },
                { "price", ServicePrice(c) }, { "days", ServiceDays }, { "planRunning", s.PlanId.HasValue && !Billing.Expired(s) },
                { "connected", !string.IsNullOrEmpty(s.WaInstance) }, { "pending", pending }, { "number", s.WaNumber ?? "" },   // the linked phone, filled in on a renewal
                // a number whose phone removed the linked device: a banner on every dashboard page until it's scanned again (WaBridge)
                { "relink", WaBridge.NeedsQr(s.WaStatus) || RelinkLines(c, s.Id) > 0 },
                // this calendar month on WhatsApp: the assistant's replies, and the customers who wrote
                { "replies", replies }, { "chats", chats } };
        }

        static int RelinkLines(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaLines WHERE StoreId=@s AND Status=@t", "@s", storeId, "@t", WaBridge.LostText)) return (int)cmd.ExecuteScalar();
        }

        // "paid" (default): a store with the WhatsApp service takes a free number by itself; "off": the admin assigns every number.
        // ("all" from before the service existed counts as "paid": no store gets WhatsApp without the service now.)
        public static string AutoAssign(SqlConnection c) { var v = (Billing.Get(c, "WaAutoAssign") ?? "paid").Trim().ToLowerInvariant(); return v == "off" ? v : "paid"; }

        // numbers the admin keeps out of the pool (e.g. Sellybot's own support number), comma separated
        public static HashSet<string> Reserved(SqlConnection c)
        {
            return new HashSet<string>((Billing.Get(c, "WaReserved") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        // null = the owner may take a number now; otherwise why not (shown in the dashboard)
        public static string CanAutoAssign(SqlConnection c, StoreInfo s)
        {
            if (PlatformToken(c) == null) return "ربط الواتساب بالـ QR مش متاح لسه.";
            if (!HasService(s)) return "خدمة الواتساب مش مفعّلة على متجرك. ضيفها من «باقتي».";
            return null;
        }

        // "May this store use WhatsApp now?" = the WhatsApp service is on. The name and the (always null) offer are from when
        // some plans included WhatsApp; order confirmations, delivery messages and chat-order codes still call it this way.
        public static bool PlanAllows(SqlConnection c, StoreInfo s, out PlanInfo offer) { offer = null; return HasService(s); }

        // Takes the first free number of the platform account for the store. null = none free.
        public static async Task<string> AssignFree(SqlConnection c, int storeId)
        {
            var token = PlatformToken(c);
            if (token == null) return null;
            // our own server: a new number made for this store, nothing to pick from (and nobody to wait for)
            if (WaBridge.Is(token))
            {
                var made = await Wapilot.Create(token, "store " + storeId + " main");
                int got;
                using (var cmd = Db.Cmd(c, @"UPDATE dbo.Stores SET WaInstance=@i, WaToken=NULL, WaHookKey=NULL, WaNumber=NULL, WaStatus=NULL, WaEnabled=1, WaLastAt=NULL
                    WHERE Id=@s AND WaInstance IS NULL", "@i", made.Id, "@s", storeId)) got = cmd.ExecuteNonQuery();
                if (got == 0) { try { await WaBridge.Delete(token, made.Id); } catch (WaException) { } return null; }   // a second press a moment ago took one
                return made.Id;
            }
            if (AutoAssign(c) == "off") return null;
            var all = await Wapilot.Instances(token);
            var taken = WaLines.Taken(c);   // stores' main numbers and their extra ones (WaLines.cs)
            var reserved = Reserved(c);
            foreach (var i in all.Where(x => !taken.Contains(x.Id) && !reserved.Contains(x.Id) && !Linked((x.Status ?? "").ToUpperInvariant())))
            {
                int n;
                using (var cmd = Db.Cmd(c, @"UPDATE dbo.Stores SET WaInstance=@i, WaToken=NULL, WaHookKey=NULL, WaNumber=NULL, WaStatus=NULL, WaEnabled=1, WaLastAt=NULL
                    WHERE Id=@s AND WaInstance IS NULL AND NOT EXISTS (SELECT 1 FROM dbo.Stores WITH (UPDLOCK, HOLDLOCK) WHERE WaInstance=@i)
                    AND NOT EXISTS (SELECT 1 FROM dbo.WaLines WITH (UPDLOCK, HOLDLOCK) WHERE Instance=@i)", "@i", i.Id, "@s", storeId))
                    n = cmd.ExecuteNonQuery();
                if (n == 0) continue;   // someone else took it a moment ago
                if (Stopped((i.Status ?? "").ToUpperInvariant())) try { await Wapilot.Start(token, i.Id); } catch (WaException ex) { Log.Error(ex); }
                return i.Id;
            }
            return null;
        }

        // no free number: one support ticket per store so the admin creates a number in Wapilot and assigns it
        public const string AskSubject = "طلب ربط واتساب المتجر";
        public static void AskAdmin(SqlConnection c, StoreInfo s)
        {
            using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.Tickets WHERE StoreId=@s AND Subject=@sub AND Status<>'closed')
BEGIN
  DECLARE @t INT;
  INSERT INTO dbo.Tickets(StoreId, Subject, Category) VALUES (@s, @sub, 'technical'); SET @t = SCOPE_IDENTITY();
  INSERT INTO dbo.TicketMessages(TicketId, FromAdmin, Body) VALUES (@t, 0, @b);
END", "@s", s.Id, "@sub", AskSubject, "@b", "(رسالة تلقائية) عايز أربط واتساب المتجر، ومفيش رقم ربط فاضي دلوقتي."))
                cmd.ExecuteNonQuery();
        }

        // the store's number needs a subscription on Sellybot's provider account (the QR can't be fetched): one support ticket per store
        // while it's open, so the admin pays for that number. The QR polls every few seconds, hence "if not exists".
        public const string SubSubject = "رقم ربط الواتساب محتاج اشتراك";
        public static void TellAdmin(SqlConnection c, StoreInfo s, string subject, string body)
        {
            try
            {
                using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.Tickets WHERE StoreId=@s AND Subject=@sub AND Status<>'closed')
BEGIN
  DECLARE @t INT;
  INSERT INTO dbo.Tickets(StoreId, Subject, Category) VALUES (@s, @sub, 'technical'); SET @t = SCOPE_IDENTITY();
  INSERT INTO dbo.TicketMessages(TicketId, FromAdmin, Body) VALUES (@t, 0, @b);
END", "@s", s.Id, "@sub", subject, "@b", body))
                    cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Error(ex); }   // never break the QR over this
        }

        public static StoreInfo StoreForInstance(SqlConnection c, string instance)
        {
            if (string.IsNullOrEmpty(instance)) return null;
            int id = 0;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id FROM dbo.Stores WHERE WaInstance=@i AND WaToken IS NULL", "@i", instance))
            { var v = cmd.ExecuteScalar(); if (v != null && v != DBNull.Value) id = (int)v; }
            return id == 0 ? null : Db.StoreById(c, id);
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

        public class Incoming { public string Event, Instance, Chat, Text, Id, Type, MediaUrl, Mime; public bool FromMe, HasMedia; }

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
            x.Type = type;
            // a picture / voice note / file: payload.media { url, mimetype } (Wapilot's v2 contract), or flat fields from other senders
            var media = WaJson.O(msg, "media");
            if (media.HasValue) { x.MediaUrl = WaJson.S(media.Value, "url", "link"); x.Mime = WaJson.S(media.Value, "mimetype", "mime_type", "mimeType"); }
            x.MediaUrl = x.MediaUrl ?? WaJson.S(msg, "media_url", "mediaUrl");
            x.Mime = x.Mime ?? WaJson.S(msg, "mimetype", "mime_type");
            x.HasMedia = (WaJson.B(msg, "hasMedia", "has_media") ?? false) || !string.IsNullOrEmpty(x.MediaUrl) || new[] { "image", "video", "audio", "ptt", "voice", "document", "sticker" }.Contains(type);
            return x;
        }

        // Returns false when the body isn't a message we understand (logged by the caller).
        // line: which of the store's WhatsApp numbers it came to (0 = the main one; WaLines.cs). The reply goes out from the same one.
        public static bool Handle(SqlConnection c, StoreInfo s, Incoming m, int line = 0)
        {
            if (m.Event.Length > 0 && !m.Event.StartsWith("message")) return true;   // other events (acks, status…): nothing to do
            if (string.IsNullOrEmpty(m.Chat)) return false;
            if (WaChat.Ignored(m.Chat)) return true;                                    // groups, status updates, channels
            if (line == 0 && !string.IsNullOrEmpty(m.Instance) && !string.Equals(m.Instance, s.WaInstance, StringComparison.OrdinalIgnoreCase)) return true;
            string key = WaChat.Key(m.Chat);
            if (WaLines.IsOwn(c, s, key)) return true;     // our own numbers: a team alert from one of them to another, never a customer
            string text = (m.Text ?? "").Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);
            if (ShopWaAdd.Intercept(c, s, key, m, text)) return true;                   // SellyStore: the owner adding a product, never a customer

            if (m.FromMe)
            {
                // the owner's report / an alert to their own number, echoed back: not a reply to a customer
                if (!string.IsNullOrEmpty(s.OwnerPhone) && key == s.OwnerPhone) return true;
                // our own reply echoed back (message.any) -> ignore; anything else is the owner answering by hand -> the bot steps aside for a while
                if (text.Length > 0 && IsOurs(c, s.Id, key, text, line)) return true;
                if (text.Length == 0 && m.HasMedia && Inbox.SentPicture(c, s.Id, Inbox.WaChan(line), key)) return true;   // a picture sent from SellyChat
                Save(c, s.Id, key, false, text.Length > 0 ? text : "[ملف/صورة من المتجر]", "owner:" + (m.Id ?? Guid.NewGuid().ToString("N")), null, line);
                return true;
            }

            // a picture the assistant can look at (WaImages): kept with the message, fetched when it answers
            bool image = m.HasMedia && !string.IsNullOrEmpty(m.MediaUrl) && WaImages.IsImage(m.Type, m.Mime) && WaImages.SafeUrl(m.MediaUrl)
                         && s.WaEnabled && WaAccount.HasService(s) && PlanGate.Allows(c, s, WaImages.Feature);
            if (text.Length == 0 && !image)
            {
                if (!m.HasMedia) return true;   // reactions, receipts…
                if (!s.WaEnabled || !WaAccount.HasService(s) || !Web.Allow("wa-media:" + s.Id + ":" + key, 1, TimeSpan.FromHours(1))) return true;
                var token = WaAccount.Token(c, s);
                string say = WaImages.MediaReply(m.Type, m.Mime), from = WaLines.InstanceOf(c, s, line);
                if (token != null && from != null && WaLines.Works(c, s, line)) HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    try { await Wapilot.Send(token, from, m.Chat, say, "sb-media-" + (m.Id ?? Guid.NewGuid().ToString("N"))); }
                    catch (Exception ex) { Log.Error(ex); }
                });
                return true;
            }

            long? row = Save(c, s.Id, key, true, text.Length > 0 ? text : WaImages.Placeholder, m.Id, image ? m.MediaUrl : null, line);
            if (row == null) return true;                                                // duplicate delivery
            Carriers.Answered(c, s, key, m.Chat, text);                                  // the answer to «إنقاذ الأوردر»: the owner hears of it
            Offers.Seen(c, s.Id, key);                                                   // «ابعت عرض»: everyone who wrote to the store's number
            if (ConfirmAnswers.Intercept(c, s, key, m.Chat, text, row.Value)) return true; // "1"/"2" answering an order confirmation: Shopify, EasyOrders or the store's own page (works with the bot off too)
            if (WaOptOut.Intercept(c, s, key, m.Chat, text)) return true;                  // «وقف» after an abandoned-cart / back-in-stock message
            if (!s.WaEnabled) return true;                                               // the bot is switched off (the message is still kept as context)
            long rowId = row.Value; int storeId = s.Id; string chat = m.Chat;
            HostingEnvironment.QueueBackgroundWorkItem(ct => Reply(storeId, key, chat, rowId, line, ct));
            return true;
        }

        // null = already have it (same message id, or the same text from the same customer in the last minute)
        // media = a picture's URL (WaImages): two pictures in a row aren't duplicates even though their text is the same
        // line = the store's number it came to / went out from (0 = the main one, WaLines.cs)
        public static long? Save(SqlConnection c, int storeId, string chatKey, bool fromCustomer, string body, string msgId, string media = null, int line = 0)
        {
            const string sql = @"
IF @fc = 1 AND @md IS NULL AND EXISTS (SELECT 1 FROM dbo.WaMessages WHERE StoreId=@s AND ChatId=@c AND FromCustomer=1 AND Body=@b AND Line=@l AND CreatedAt > DATEADD(SECOND,-60,SYSUTCDATETIME()))
  SELECT CAST(NULL AS BIGINT);
ELSE
  INSERT INTO dbo.WaMessages(StoreId, ChatId, FromCustomer, Body, MsgId, Media, Line) OUTPUT INSERTED.Id VALUES(@s, @c, @fc, @b, @m, @md, @l);";
            try
            {
                using (var cmd = Db.Cmd(c, sql, "@s", storeId, "@c", chatKey.Length > 100 ? chatKey.Substring(0, 100) : chatKey, "@fc", fromCustomer,
                    "@b", body.Length > 4000 ? body.Substring(0, 4000) : body, "@m", msgId == null ? null : (msgId.Length > 150 ? msgId.Substring(0, 150) : msgId),
                    "@md", media == null ? (object)DBNull.Value : (media.Length > 800 ? media.Substring(0, 800) : media), "@l", line))
                {
                    var v = cmd.ExecuteScalar();
                    if (v == null || v == DBNull.Value) return null;
                    Inbox.Touch(c, storeId, Inbox.WaChan(line), chatKey, fromCustomer, body, msgId);   // SellyChat's list of chats
                    return Convert.ToInt64(v);
                }
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return null; }
        }

        // what we sent ourselves: the assistant's replies, and the answers written in SellyChat (MsgId owner:in-<staff>-<random>)
        static bool IsOurs(SqlConnection c, int storeId, string chatKey, string text, int line)
        {
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(*) FROM dbo.WaMessages WHERE StoreId=@s AND ChatId=@c AND FromCustomer=0 AND (MsgId IS NULL OR MsgId LIKE 'owner:in-%') AND Line=@l
                AND CreatedAt > DATEADD(MINUTE,-30,SYSUTCDATETIME()) AND CHARINDEX(@t, Body) > 0", "@s", storeId, "@c", chatKey, "@t", text, "@l", line))
                return (int)cmd.ExecuteScalar() > 0;
        }

        class Row { public long Id; public bool Customer, Owner; public string Body, Media; public DateTime At; }

        internal static Task Reply(int storeId, string chatKey, string chat, long rowId, CancellationToken ct) { return Reply(storeId, chatKey, chat, rowId, 0, ct); }

        // line: the store's number the customer wrote to (WaLines.cs): its own conversation, answered from the same number
        internal static async Task Reply(int storeId, string chatKey, string chat, long rowId, int line, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Debounce, ct);
                // The database is used in short steps, never while waiting on the network: read -> pictures -> the reply's data ->
                // (Claude writes) -> save -> (Wapilot sends) -> status. A reply takes seconds, and holding one connection for all of
                // it let many chats at once empty the connection pool and stop the whole site.
                List<Row> rows; StoreInfo s; string token, pending, from; List<ChatHandler.ExtraTool> extra; bool ownStore;
                using (var c = Db.Open())
                {
                    rows = new List<Row>();
                    using (var cmd = Db.Cmd(c, @"SELECT TOP 40 Id, FromCustomer, Body, MsgId, Media, CreatedAt FROM dbo.WaMessages
                        WHERE StoreId=@s AND ChatId=@c AND Line=@l AND CreatedAt > DATEADD(HOUR,-24,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", storeId, "@c", chatKey, "@l", line))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) rows.Add(new Row { Id = (long)r["Id"], Customer = (bool)r["FromCustomer"], Body = (string)r["Body"], Media = r["Media"] as string,
                            Owner = (r["MsgId"] as string ?? "").StartsWith("owner:"), At = DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc) });
                    // a newer message came in (its own run answers everything), or something was already said after this one
                    if (rows.Count == 0 || rows[0].Id != rowId) return;
                    // the owner (or whoever holds this number) is talking to this customer themselves
                    if (rows.Any(x => x.Owner && x.At > DateTime.UtcNow.AddHours(-PauseHours))) return;
                    if (Inbox.Quiet(c, storeId, Inbox.WaChan(line), chatKey)) return;   // «وقّف المساعد هنا» in SellyChat

                    s = Db.StoreById(c, storeId);
                    if (s == null || s.Status != "active" || !s.WaEnabled || string.IsNullOrEmpty(s.WaInstance)) return;
                    from = WaLines.InstanceOf(c, s, line);
                    if (string.IsNullOrEmpty(from)) return;
                    PlanGate.Apply(c, s);   // e.g. no order tracking when the plan doesn't include Shopify
                    token = WaAccount.Token(c, s);
                    if (token == null) return;
                    if (!WaAccount.HasService(s)) { SetStatus(c, s.Id, "⚠️ خدمة الواتساب خلصت، فالمساعد مش بيرد على الواتساب لحد ما تجدّدها من «باقتي»."); return; }
                    // an extra number past what the subscription covers: kept linked, but the assistant doesn't answer on it
                    if (line > 0 && !WaLines.Works(c, s, line)) { WaLines.SetStatus(c, s.Id, line, "⚠️ الرقم ده مش ضمن اشتراك الواتساب دلوقتي، فالمساعد مش بيرد عليه. ضيفه للاشتراك من «باقتي»."); return; }
                    if (Billing.Expired(s)) { SetStatus(c, s.Id, "⚠️ الباقة خلصت، فالمساعد مش بيرد على الواتساب لحد ما تجدّدها."); return; }
                    if (Billing.LimitReached(c, s)) { SetStatus(c, s.Id, "⚠️ رصيد الشهر خلص، فالمساعد مش بيرد على الواتساب لحد ما تزوّد الرصيد أو يبدأ شهر جديد."); return; }
                    if (!Web.Allow("wa-chat:" + s.Id + ":" + chatKey, 30, TimeSpan.FromHours(1))) return;   // one customer can't burn the quota
                    try { Shopify.RefreshIfOld(s); } catch (Exception ex) { Log.Error(ex); }
                    try { EasyOrders.RefreshIfOld(s); } catch (Exception ex) { Log.Error(ex); }

                    // tools the growth features add to this chat («بلّغني لما يرجع», handoff to the owner)
                    extra = new List<ChatHandler.ExtraTool>();
                    var custPhone = WaChat.Digits(chatKey);
                    // «بلّغني لما يرجع» reads Shopify / EasyOrders stock: not while the store's own page is open (it is the stock then)
                    ownStore = ShopBot.For(c, s) != null;
                    if (custPhone != null && !ownStore && Restock.On(c, s)) extra.Add(Restock.Tool(s, custPhone));
                    if (custPhone != null && Handoff.On(c, s)) extra.Add(Handoff.Tool(s, "wa", chatKey, line));
                    pending = OrderConfirm.PendingNote(c, s.Id, chatKey) + EoConfirm.PendingNote(c, s.Id, chatKey) + ShopHooks.PendingNote(c, s.Id, chatKey) + Delivery.Note(c, s.Id, chatKey) + Carriers.Note(c, s.Id, chatKey)
                        + Carts.Note(c, s.Id, chatKey) + Handoff.Note(c, s.Id, chatKey);
                }

                // "typing…" on the customer's phone while the assistant writes (our own server only)
                if (WaBridge.Is(token)) { var tk = token; var fr = from; var ch = chat; var _ = Task.Run(async () => { try { await Wapilot.Typing(tk, fr, ch); } catch (WaException) { } }); }

                // the pictures in the customer's newest turn (newest first, up to 3)
                var imgs = new Dictionary<long, ImageBlockParam>();
                foreach (var x in rows.TakeWhile(x => x.Customer).Where(x => x.Media != null).Take(WaImages.MaxPerTurn))
                {
                    var img = await WaImages.Load(x.Media, token);
                    if (img != null) imgs[x.Id] = img;
                }

                ChatHandler.LoopResult res;
                try
                {
                    ChatHandler.Prepared prep;
                    using (var c = Db.Open())
                        prep = ChatHandler.Prepare(c, s, History(rows, imgs), "wa:" + chatKey, ChannelNote(s, chatKey, ownStore) + (imgs.Count > 0 ? WaImages.Note : "") + pending, extra);
                    res = await ChatHandler.Run(prep);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    using (var c = Db.Open()) SetStatus(c, s.Id, "⚠️ حصلت مشكلة وإحنا بنجهّز رد على آخر رسالة. لو اتكررت كلّم الدعم.");
                    return;
                }

                string answer = Wapilot.Format(string.IsNullOrWhiteSpace(res.Text) ? Fallback : res.Text);
                long? mine;
                using (var c = Db.Open())
                {
                    Db.AddUsage(c, s.Id, 1, res.In, res.CacheRead, res.CacheWrite, res.Out);
                    // saved before sending: Wapilot may echo it back (message.any) before Send returns
                    mine = Save(c, s.Id, chatKey, false, answer, null, null, line);
                }
                string failed = null;
                try
                {
                    int i = 0;
                    foreach (var part in Wapilot.Split(answer)) await Wapilot.Send(token, from, chat, part, "sb-" + rowId + "-" + (i++));
                }
                catch (WaException ex) { failed = "⚠️ " + ex.Message; }
                using (var c = Db.Open())
                {
                    if (failed != null && mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                    WaLines.SetStatus(c, s.Id, line, failed);
                    if (new Random().Next(200) == 0) Cleanup(c);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error(ex); }
        }

        // oldest first; messages in a row from the same side become one turn; starts and ends with the customer
        class Turn { public bool Customer; public string Text; public List<ImageBlockParam> Images = new List<ImageBlockParam>(); }

        // imgs: pictures (by message id) that go in as real images; every other picture stays as its placeholder text
        static List<MessageParam> History(List<Row> newestFirst, Dictionary<long, ImageBlockParam> imgs = null)
        {
            var turns = new List<Turn>();
            foreach (var r in Enumerable.Reverse(newestFirst))
            {
                Turn t;
                if (turns.Count > 0 && turns[turns.Count - 1].Customer == r.Customer) { t = turns[turns.Count - 1]; t.Text += "\n" + r.Body; }
                else turns.Add(t = new Turn { Customer = r.Customer, Text = r.Body });
                ImageBlockParam img;
                if (r.Customer && imgs != null && imgs.TryGetValue(r.Id, out img)) t.Images.Add(img);
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

        static string ChannelNote(StoreInfo s, string chatKey, bool ownStore)
        {
            var sb = new StringBuilder("المحادثة دي على واتساب (رقم المتجر)، مش على الموقع:\n")
                .Append("- خلي ردودك قصيرة زي رسايل الواتساب العادية.\n")
                .Append("- ماتستخدمش جداول ولا عناوين. للخط العريض استخدم نجمة واحدة *كده*، وللقوائم • أو أرقام.\n")
                .Append("- اكتب اللينكات كاملة (https://...) مش [نص](لينك).\n")
                .Append("- لو فيه رسايل في المحادثة من المتجر نفسه (صاحب المتجر رد بنفسه)، كمّل على كلامه وماتناقضهوش.");
            var phone = WaChat.Digits(chatKey);
            if (phone != null && !ownStore && OrderTracking.On(s))   // the Shopify / EasyOrders order tool (the store's own tells ShopBot.Prompt)
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

    // api/wa-webhook.ashx?src=bridge — our own WhatsApp server posts every message and connection change here, signed (WaBridge.Webhook).
// api/wa-webhook.ashx?store=<slug>&key=<secret> / ?key=<platform secret> — the same from Wapilot (WaProvider = "wapilot").
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
                    // our own server (WaBridge.cs): a signed body, routed by instance_id
                    if (req.QueryString["src"] == "bridge" || req.Headers["X-Bridge-Signature"] != null) { WaBridge.Webhook(ctx, c); return; }
                    // two kinds of URL: ?store=<slug>&key=<store secret> (owner's own Wapilot account),
                    // or ?key=<platform secret> (Sellybot's account: every number posts here, routed by instance_id)
                    bool platform = slug.Length == 0;
                    StoreInfo s = null;
                    if (platform)
                    {
                        var pk = WaAccount.PlatformHookKey(c, false);
                        if (string.IsNullOrEmpty(pk) || !Secrets.Same(Encoding.UTF8.GetBytes(pk), Encoding.UTF8.GetBytes(key))) { res.StatusCode = 401; return; }
                    }
                    else
                    {
                        s = Web.SlugRx.IsMatch(slug) ? Db.StoreBySlug(c, slug) : null;
                        if (s == null || string.IsNullOrEmpty(s.WaHookKey) || !Secrets.Same(Encoding.UTF8.GetBytes(s.WaHookKey), Encoding.UTF8.GetBytes(key)))
                        { res.StatusCode = 401; return; }
                    }
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
                        var store = s; int line = 0;
                        if (platform)
                        {
                            store = WaLines.Route(c, m.Instance, out line);   // a store's main number, or one of its extra numbers
                            if (store == null) continue;   // a number not assigned to any store (yet)
                        }
                        if (!WhatsAppBot.Handle(c, store, m, line) && DateTime.UtcNow - lastShapeLog > TimeSpan.FromMinutes(10))
                        {
                            lastShapeLog = DateTime.UtcNow;
                            Log.Error(new Exception("WhatsApp webhook not understood (store " + store.Slug + "): " + WaJson.Shape(item)));
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
                        // the owner's own Wapilot account: only while Sellybot itself is on Wapilot (WaProvider = "wapilot")
                        if (WaBridge.On(c)) { Web.Error(res, 400, "bad_action"); return; }
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
                        if (WaLines.Extra(c, s.Id) > 0) { Web.Error(res, 400, "has_lines", "عندك أرقام واتساب إضافية مربوطة على حساب Sellybot، فمينفعش تنقل الرقم الأساسي لحساب تاني."); return; }
                        if (WaAccount.IsPlatform(s))   // moving from Sellybot's number to the owner's own account: free ours
                            try { await Wapilot.Logout(WaAccount.PlatformToken(c), s.WaInstance); } catch (WaException) { }
                        string hookKey = string.IsNullOrEmpty(s.WaHookKey) ? WhatsAppBot.NewKey() : s.WaHookKey;   // same URL on reconnect
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaInstance=@i, WaToken=@t, WaHookKey=@k, WaNumber=@n, WaEnabled=1, WaStatus=NULL WHERE Id=@s",
                            "@i", chosen.Id, "@t", Secrets.Protect(token), "@k", hookKey, "@n", chosen.Number, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, req, Db.StoreById(c, s.Id)));
                        return;
                    }
                    if (action == "link")
                    {
                        // Sellybot's account: take a free number (not assigned, no phone linked) and start it so it shows a QR
                        if (!string.IsNullOrEmpty(s.WaInstance)) { Web.Json(res, 200, await State(c, req, s)); return; }
                        var why = WaAccount.CanAutoAssign(c, s);
                        if (why != null) { Web.Error(res, 400, "not_available", why); return; }
                        string picked;
                        try { picked = await WaAccount.AssignFree(c, s.Id); }
                        catch (WaException ex)
                        {
                            // our server is full: the admin gets a ticket (like an empty pool on Wapilot), the owner the plain reason
                            if (ex.Code == "BRIDGE_FULL") { WaAccount.AskAdmin(c, s); Web.Error(res, 409, "no_free_number", ex.Message); return; }
                            Log.Error(ex); Web.Error(res, 400, "wapilot", ex.Status == 0 ? ex.Message : "مقدرناش نجهّز رقم دلوقتي، جرّب كمان شوية."); return;
                        }
                        if (picked == null)
                        {
                            WaAccount.AskAdmin(c, s);
                            Web.Error(res, 409, "no_free_number", "كل الأرقام المتاحة مستخدمة دلوقتي. بعتنا للدعم وهنجهّزلك رقم في أقرب وقت، وهيظهرلك هنا الـ QR أول ما يجهز.");
                            return;
                        }
                        Web.Json(res, 200, await State(c, req, Db.StoreById(c, s.Id)));
                        return;
                    }
                    if (string.IsNullOrEmpty(s.WaInstance)) { Web.Error(res, 400, "not_connected", "اربط الواتساب الأول."); return; }
                    bool platformMode = WaAccount.IsPlatform(s);
                    // linking a phone and test messages need the service; switching the bot off, unlinking and disconnecting never do
                    if ((action == "qr" || action == "test") && !WaAccount.HasService(s))
                    { Web.Error(res, 400, "no_service", "خدمة الواتساب مش مفعّلة على متجرك دلوقتي. ضيفها أو جدّدها من «باقتي»."); return; }

                    if (action == "qr")
                    {
                        if (!platformMode) { Web.Error(res, 400, "bad_action"); return; }
                        var token = WaAccount.PlatformToken(c);
                        if (token == null) { Web.Error(res, 400, "not_available", "الواتساب مش متاح دلوقتي، كلّم الدعم."); return; }
                        var d = new Dictionary<string, object>();
                        try
                        {
                            var st = await Wapilot.Status(token, s.WaInstance);
                            string status = (st.Status ?? "").ToUpperInvariant();
                            d["live"] = st.Status ?? "";
                            if (WaAccount.Linked(status))
                            {
                                // the phone already linked as one of the store's extra numbers would answer every customer twice
                                if (!string.IsNullOrEmpty(st.Number) && st.Number != s.WaNumber && WaLines.IsOwn(c, s, st.Number))
                                {
                                    try { await Wapilot.Logout(token, s.WaInstance); } catch (WaException) { }
                                    d["linked"] = false; d["error"] = "الموبايل ده مربوط خلاص كرقم إضافي على متجرك. امسح الكود من موبايل الرقم الأساسي.";
                                    Web.Json(res, 200, d); return;
                                }
                                d["linked"] = true;
                                if (!string.IsNullOrEmpty(st.Number) && st.Number != s.WaNumber)
                                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaNumber=@n, WaStatus=NULL WHERE Id=@s", "@n", st.Number, "@s", s.Id)) cmd.ExecuteNonQuery();
                                d["number"] = st.Number ?? s.WaNumber ?? "";
                            }
                            else
                            {
                                d["linked"] = false;
                                if (WaAccount.Stopped(status) && Web.Allow("wa-start:" + s.Id, 6, TimeSpan.FromMinutes(10))) await Wapilot.Start(token, s.WaInstance);
                                d["qr"] = await Wapilot.Qr(token, s.WaInstance) ?? "";
                            }
                        }
                        catch (WaException ex)
                        {
                            d["error"] = ex.Message;
                            if (Wapilot.NoSubscription(ex.Code)) WaAccount.TellAdmin(c, s, WaAccount.SubSubject, "(رسالة تلقائية) رقم ربط الواتساب اللي اتخصص لمتجري محتاج اشتراك من عندكم، فكود الربط مش بيظهر.");
                        }
                        Web.Json(res, 200, d);
                        return;
                    }
                    if (action == "unlink")
                    {
                        // logs the phone out of the number (to link a different phone); the number stays with the store
                        if (!platformMode) { Web.Error(res, 400, "bad_action"); return; }
                        try { await Wapilot.Logout(WaAccount.PlatformToken(c), s.WaInstance); }
                        catch (WaException ex) { Web.Error(res, 400, "wapilot", ex.Message); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaNumber=NULL, WaStatus=NULL WHERE Id=@s", "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, req, Db.StoreById(c, s.Id)));
                        return;
                    }

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
                        try { await Wapilot.Send(WaAccount.Token(c, s), s.WaInstance, phone, text, null); }
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
                        if (platformMode) { Web.Error(res, 400, "bad_action"); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET WaHookKey=@k WHERE Id=@s", "@k", WhatsAppBot.NewKey(), "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, req, Db.StoreById(c, s.Id)));
                        return;
                    }
                    if (action == "disconnect")
                    {
                        // the extra numbers (WaLines.cs) lean on the main one: they go first
                        if (WaLines.Extra(c, s.Id) > 0) { Web.Error(res, 400, "has_lines", "عندك أرقام واتساب إضافية مربوطة. شيلها الأول من «أرقام الواتساب والفريق»، وبعدين الغي ربط الرقم الأساسي."); return; }
                        // on Sellybot's account the number is let go: our server deletes it, on Wapilot the phone is logged out first
                        if (platformMode)
                            try { await Wapilot.Release(WaAccount.PlatformToken(c), s.WaInstance); }
                            catch (WaException ex) { if (ex.Status != 404) Log.Error(ex); }
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
            bool on = !string.IsNullOrEmpty(s.WaInstance), platform = WaAccount.IsPlatform(s);
            var d = new Dictionary<string, object> { { "connected", on }, { "mode", on ? (platform ? "platform" : "own") : "" } };
            // the WhatsApp service (an add-on on any plan): when it's off, the dashboard sells it instead of the link button
            var svc = WaAccount.ServiceJson(c, s);
            d["service"] = svc;
            if (!(bool)svc["on"])
            {
                // a plan with WhatsApp inside («الاقتصادية») that ended: renewing the plan brings WhatsApp back, not the 300 add-on
                var mine = s.PlanId.HasValue && Billing.Expired(s) && Billing.PlanHasWa(s.PlanId) ? Billing.Plan(c, s.PlanId.Value) : null;
                d["upsell"] = mine != null && mine.IsActive ? PlanGate.OfferJson(c, s, mine) : new Dictionary<string, object>(svc) { { "addon", true } };
            }
            if (!on)
            {
                // can the owner just scan a QR (Sellybot's account)?
                d["platformAvailable"] = WaAccount.PlatformToken(c) != null;
                d["platformBlocked"] = WaAccount.CanAutoAssign(c, s) ?? "";
                return d;
            }
            d["instance"] = s.WaInstance; d["number"] = s.WaNumber ?? ""; d["enabled"] = s.WaEnabled;
            d["relink"] = WaBridge.NeedsQr(s.WaStatus);   // the phone removed the linked device: one button shows a new QR
            d["status"] = s.WaStatus ?? ""; d["lastAt"] = s.WaLastAt.HasValue ? s.WaLastAt.Value.ToString("o") : "";
            if (!platform) d["webhookUrl"] = HookUrl(req, s);
            d["pauseHours"] = WhatsAppBot.PauseHours;
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(DISTINCT ChatId), ISNULL(SUM(CASE WHEN FromCustomer=0 AND MsgId IS NULL THEN 1 ELSE 0 END),0),
                    (SELECT MAX(CreatedAt) FROM dbo.WaMessages WHERE StoreId=@s AND FromCustomer=1)
                FROM dbo.WaMessages WHERE StoreId=@s AND CreatedAt > DATEADD(DAY,-30,SYSUTCDATETIME())", "@s", s.Id))
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                d["chats30"] = Convert.ToInt32(r[0]); d["replies30"] = Convert.ToInt32(r[1]);
                d["lastIncoming"] = r[2] == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)r[2], DateTimeKind.Utc).ToString("o");
            }
            // live state from Wapilot (is the phone still linked?), also while the service is off
            try
            {
                var token = WaAccount.Token(c, s);
                var st = token == null ? null : await Wapilot.Status(token, s.WaInstance);
                d["live"] = st == null ? "" : st.Status; if (st != null && !string.IsNullOrEmpty(st.Number)) d["number"] = st.Number;
                d["linked"] = st != null && WaAccount.Linked((st.Status ?? "").ToUpperInvariant());
            }
            catch (WaException ex) { d["live"] = ""; d["liveError"] = ex.Message; }
            return d;
        }
    }

    // api/wa-admin.ashx — super admin «سيرفر الواتساب»: our own server's address and secret (?action=bridge, «جرّب الاتصال» = bridge-test),
    // its health, and which number belongs to which store. With WaProvider = "wapilot" the old Wapilot token / webhook actions work as before.
    public class WhatsAppAdminHandler : HttpTaskAsyncHandler
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
                {
                    var adminId = AdminAuth.Current(ctx);
                    bool ok = false;
                    if (adminId.HasValue) using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Admins WHERE Id=@id", "@id", adminId.Value)) ok = (int)cmd.ExecuteScalar() > 0;
                    if (!ok) { Web.Error(res, 401, "not_logged_in"); return; }

                    if (req.HttpMethod == "GET") { Web.Json(res, 200, await State(c, req)); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                    var b = Web.Body(req, 4000);

                    // ---------- our own WhatsApp server (WaBridge.cs) ----------
                    if (action == "bridge")
                    {
                        string url = WaBridge.CleanUrl(Web.Str(b, "url", 300)), secret = (Web.Str(b, "secret", 300) ?? "").Trim();
                        if (url == null) { Web.Error(res, 400, "bad_url", "العنوان مش مظبوط. اكتبه كده: https://wa.sellybot.online"); return; }
                        if (secret.Length == 0) secret = WaBridge.Secret(c) ?? "";   // changing only the address keeps the saved secret
                        if (!WaBridge.GoodSecret(secret)) { Web.Error(res, 400, "bad_secret", "المفتاح مش مظبوط: لازم يكون من 32 حرف أو أكتر (حروف إنجليزي وأرقام و - و _)، من غير مسافات."); return; }
                        Billing.Set(c, "WaBridgeUrl", url);
                        Billing.Set(c, "WaBridgeSecret", Secrets.Protect(secret));
                        var d = await State(c, req);
                        d["test"] = await WaBridge.Test(url, secret);
                        Web.Json(res, 200, d);
                        return;
                    }
                    if (action == "bridge-test")
                    {
                        var secret = WaBridge.Secret(c);
                        if (secret == null) { Web.Error(res, 400, "no_secret", "احفظ مفتاح السيرفر الأول."); return; }
                        var d = await State(c, req);
                        d["test"] = await WaBridge.Test(WaBridge.Url(c), secret);
                        Web.Json(res, 200, d);
                        return;
                    }
                    if (action == "token")
                    {
                        string token = (Web.Str(b, "token", 600) ?? "").Trim();
                        if (token.Length < 10 || token.Contains(" ")) { Web.Error(res, 400, "bad_token", "الـ API token مش مظبوط. انسخه زي ما هو من Wapilot (API tokens)."); return; }
                        try { await Wapilot.Instances(token); }
                        catch (WaException ex) { Web.Error(res, 400, "wapilot", ex.Message); return; }
                        Billing.Set(c, "WaPlatformToken", Secrets.Protect(token));
                        WaAccount.PlatformHookKey(c, true);
                        Web.Json(res, 200, await State(c, req));
                        return;
                    }
                    if (action == "settings")
                    {
                        var mode = (Web.Str(b, "autoAssign", 10) ?? "").ToLowerInvariant();
                        var price = Web.Str(b, "price", 12);
                        var multi = Web.Str(b, "multiPrice", 12);   // each number's month when a store has more than one (WaLines.cs)
                        if (mode.Length == 0 && price == null && multi == null) { Web.Error(res, 400, "bad_request"); return; }
                        if (multi != null)
                        {
                            decimal mp = Billing.Dec(ShopifyAdmin.ToLatinDigits(multi).Trim(), -1);
                            if (mp < 1 || mp > 100000) { Web.Error(res, 400, "bad_price", "اكتب سعر الرقم في الشهر لما يبقى عند المتجر أكتر من رقم، رقم أكبر من صفر."); return; }
                            Billing.Set(c, "WaMultiPrice", Math.Round(mp, 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        if (mode.Length > 0)
                        {
                            if (mode != "paid" && mode != "off") { Web.Error(res, 400, "bad_request"); return; }
                            Billing.Set(c, "WaAutoAssign", mode);
                        }
                        if (price != null)
                        {
                            // the WhatsApp service's monthly price (new requests use it; ones already sent keep their amount)
                            decimal pr = Billing.Dec(ShopifyAdmin.ToLatinDigits(price).Trim(), -1);
                            if (pr < 1 || pr > 100000) { Web.Error(res, 400, "bad_price", "اكتب سعر الخدمة في الشهر بالجنيه، رقم أكبر من صفر."); return; }
                            Billing.Set(c, "WaAddonPrice", Math.Round(pr, 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        Web.Json(res, 200, await State(c, req));
                        return;
                    }
                    if (action == "reserve")
                    {
                        var inst = (Web.Str(b, "instance", 150) ?? "").Trim();
                        object on; bool reserve = b.TryGetValue("reserved", out on) && on is bool && (bool)on;
                        if (inst.Length == 0) { Web.Error(res, 400, "bad_request"); return; }
                        var set = WaAccount.Reserved(c);
                        if (reserve) set.Add(inst); else set.Remove(inst);
                        Billing.Set(c, "WaReserved", string.Join(",", set));
                        Web.Json(res, 200, await State(c, req));
                        return;
                    }
                    if (action == "new-key")
                    {
                        Billing.Set(c, "WaPlatformHookKey", WhatsAppBot.NewKey());
                        Web.Json(res, 200, await State(c, req));
                        return;
                    }

                    var token2 = WaAccount.PlatformToken(c);
                    if (token2 == null) { Web.Error(res, 400, "no_token", WaBridge.On(c) ? "احفظ عنوان سيرفر الواتساب ومفتاحه الأول." : "حط الـ API token بتاع حساب Wapilot الأول."); return; }

                    if (action == "delete")
                    {
                        // a number on our server that no store holds (left over after a store let it go): deleted from the server
                        var inst = (Web.Str(b, "instance", 150) ?? "").Trim();
                        if (inst.Length == 0 || !WaBridge.Is(token2)) { Web.Error(res, 400, "bad_request"); return; }
                        if (WaLines.Taken(c).Contains(inst)) { Web.Error(res, 400, "taken", "الرقم ده مع متجر. فكّه من المتجر الأول."); return; }
                        try { await WaBridge.Delete(token2, inst); } catch (WaException ex) { Web.Error(res, 400, "bridge", ex.Message); return; }
                        Web.Json(res, 200, await State(c, req));
                        return;
                    }

                    if (action == "assign")
                    {
                        var inst = (Web.Str(b, "instance", 150) ?? "").Trim();
                        var storeId = Web.Int(b, "storeId");
                        var s = storeId.HasValue ? Db.StoreById(c, storeId.Value) : null;
                        if (inst.Length == 0 || s == null) { Web.Error(res, 400, "bad_request"); return; }
                        List<Wapilot.Instance> all;
                        try { all = await Wapilot.Instances(token2); }
                        catch (WaException ex) { Web.Error(res, 400, "wapilot", ex.Message); return; }
                        var i = all.FirstOrDefault(x => string.Equals(x.Id, inst, StringComparison.OrdinalIgnoreCase));
                        if (i == null) { Web.Error(res, 400, "not_found", WaBridge.Is(token2) ? "الرقم ده مش موجود على سيرفر الواتساب." : "الرقم ده مش موجود على حساب Wapilot."); return; }
                        bool linked = WaAccount.Linked((i.Status ?? "").ToUpperInvariant());
                        // extra: to the store's oldest extra number still waiting for one (WaLines.cs), not its main number
                        object ex0; bool extra = b.TryGetValue("extra", out ex0) && ex0 is bool && (bool)ex0;
                        if (extra)
                        {
                            int n;
                            using (var cmd = Db.Cmd(c, @"UPDATE dbo.Stores SET WaInstance=NULL, WaToken=NULL, WaHookKey=NULL, WaNumber=NULL, WaStatus=NULL, WaEnabled=0, WaLastAt=NULL WHERE WaInstance=@i;
                                UPDATE dbo.WaLines SET Instance=NULL, Number=NULL, Status=NULL WHERE Instance=@i;
                                UPDATE dbo.WaLines SET Instance=@i, Number=@n, Status=NULL WHERE Id = (SELECT TOP 1 Id FROM dbo.WaLines WHERE StoreId=@s AND Instance IS NULL ORDER BY Id);",
                                "@i", i.Id, "@n", linked ? i.Number : null, "@s", s.Id)) n = cmd.ExecuteNonQuery();
                            if (WaAccount.Stopped((i.Status ?? "").ToUpperInvariant())) try { await Wapilot.Start(token2, i.Id); } catch (WaException e2) { Log.Error(e2); }
                            Web.Json(res, 200, await State(c, req));
                            return;
                        }
                        // the number leaves whichever store had it (as its main number or an extra one); the store leaves whatever it had (incl. its own Wapilot account)
                        using (var cmd = Db.Cmd(c, @"UPDATE dbo.WaLines SET Instance=NULL, Number=NULL, Status=NULL WHERE Instance=@i;
                            UPDATE dbo.Stores SET WaInstance=NULL, WaToken=NULL, WaHookKey=NULL, WaNumber=NULL, WaStatus=NULL, WaEnabled=0, WaLastAt=NULL WHERE WaInstance=@i AND Id<>@s;
                            UPDATE dbo.Stores SET WaInstance=@i, WaToken=NULL, WaHookKey=NULL, WaNumber=@n, WaStatus=NULL, WaEnabled=1 WHERE Id=@s;",
                            "@i", i.Id, "@n", linked ? i.Number : null, "@s", s.Id)) cmd.ExecuteNonQuery();
                        if (WaAccount.Stopped((i.Status ?? "").ToUpperInvariant())) try { await Wapilot.Start(token2, i.Id); } catch (WaException ex) { Log.Error(ex); }
                        Web.Json(res, 200, await State(c, req));
                        return;
                    }
                    if (action == "unassign")
                    {
                        var inst = (Web.Str(b, "instance", 150) ?? "").Trim();
                        object lo; bool logout = b.TryGetValue("logout", out lo) && lo is bool && (bool)lo;
                        if (inst.Length == 0) { Web.Error(res, 400, "bad_request"); return; }
                        using (var cmd = Db.Cmd(c, @"UPDATE dbo.Stores SET WaInstance=NULL, WaNumber=NULL, WaStatus=NULL, WaEnabled=0, WaLastAt=NULL WHERE WaInstance=@i AND WaToken IS NULL;
                            UPDATE dbo.WaLines SET Instance=NULL, Number=NULL, Status=NULL WHERE Instance=@i;", "@i", inst)) cmd.ExecuteNonQuery();
                        // logout: on our server the number is deleted (nobody else would use it), on Wapilot the phone is logged out
                        if (logout) try { await Wapilot.Release(token2, inst); } catch (WaException ex) { if (ex.Status != 404) { Web.Error(res, 400, "wapilot", ex.Message); return; } }
                        Web.Json(res, 200, await State(c, req));
                        return;
                    }
                    Web.Error(res, 400, "bad_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static async Task<Dictionary<string, object>> State(SqlConnection c, HttpRequest req)
        {
            var token = WaAccount.PlatformToken(c);
            bool bridge = WaBridge.On(c);
            var d = new Dictionary<string, object> { { "tokenSet", token != null }, { "autoAssign", WaAccount.AutoAssign(c) }, { "provider", bridge ? "bridge" : "wapilot" },
                { "price", WaAccount.ServicePrice(c) }, { "multiPrice", WaLines.MultiPrice(c) }, { "days", WaAccount.ServiceDays } };
            // every store, for the "assign to" list, with what it uses now and until when its WhatsApp service runs
            var stores = new List<Dictionary<string, object>>();
            var byInstance = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = Db.Cmd(c, "SELECT Id, Slug, Name, WaInstance, CASE WHEN WaToken IS NULL THEN 0 ELSE 1 END AS Own, PlanId, PlanEndsAt, WaUntil, IsAdmin FROM dbo.Stores ORDER BY Name"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var until = r["WaUntil"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["WaUntil"], DateTimeKind.Utc);
                    // a running plan that includes WhatsApp («الاقتصادية») has the service through the plan, until the plan ends
                    var planEnds = r["PlanEndsAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["PlanEndsAt"], DateTimeKind.Utc);
                    if (planEnds.HasValue && planEnds.Value > DateTime.UtcNow && Billing.PlanHasWa(r["PlanId"] as int?) && (!until.HasValue || planEnds.Value > until.Value)) until = planEnds;
                    var st = new Dictionary<string, object> {
                        { "id", (int)r["Id"] }, { "slug", (string)r["Slug"] }, { "name", (string)r["Name"] },
                        { "instance", r["WaInstance"] as string ?? "" }, { "own", Convert.ToInt32(r["Own"]) == 1 && r["WaInstance"] != DBNull.Value },
                        { "paid", r["PlanId"] != DBNull.Value && r["PlanEndsAt"] != DBNull.Value && (DateTime)r["PlanEndsAt"] >= DateTime.UtcNow },
                        { "service", (bool)r["IsAdmin"] || (until.HasValue && until.Value > DateTime.UtcNow) }, { "always", (bool)r["IsAdmin"] },
                        { "waUntil", until.HasValue ? until.Value.ToString("o") : "" } };
                    stores.Add(st);
                    if (r["WaInstance"] != DBNull.Value && Convert.ToInt32(r["Own"]) == 0) byInstance[(string)r["WaInstance"]] = st;
                }
            d["stores"] = stores;
            // a store's extra numbers (WaLines.cs): which instance is whose, and how many each store pays for
            var lineCount = new Dictionary<int, int>();
            using (var cmd = Db.Cmd(c, "SELECT l.Id, l.StoreId, l.Instance, s.Name FROM dbo.WaLines l JOIN dbo.Stores s ON s.Id = l.StoreId ORDER BY l.StoreId, l.Id"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    int sid = (int)r["StoreId"]; int k; lineCount.TryGetValue(sid, out k); lineCount[sid] = ++k;
                    var st = stores.FirstOrDefault(x => (int)x["id"] == sid);
                    if (st == null || r["Instance"] == DBNull.Value) continue;
                    // a copy of the store's row with this number's own name (set by index: the copy has "name" already)
                    var ln = new Dictionary<string, object>(st); ln["name"] = (string)r["Name"] + " (رقم إضافي " + (k + 1) + ")"; ln["line"] = (int)r["Id"];
                    byInstance[(string)r["Instance"]] = ln;
                }
            foreach (var st in stores) { int k; lineCount.TryGetValue((int)st["id"], out k); st["extra"] = k; }
            if (bridge) d["bridge"] = new Dictionary<string, object> { { "url", WaBridge.Url(c) }, { "secretSet", token != null }, { "watch", WaBridge.WatchJson(c) } };
            if (token == null) return d;
            if (!bridge) d["webhookUrl"] = WaAccount.PlatformHookUrl(req, c);
            // tickets from owners who found no free number (for their main number, or an extra one)
            using (var cmd = Db.Cmd(c, @"SELECT t.Id, t.StoreId, s.Name, t.CreatedAt, t.Subject FROM dbo.Tickets t JOIN dbo.Stores s ON s.Id=t.StoreId
                WHERE t.Status<>'closed' AND ((t.Subject=@sub AND s.WaInstance IS NULL)
                  OR (t.Subject=@sub2 AND EXISTS (SELECT 1 FROM dbo.WaLines l WHERE l.StoreId = s.Id AND l.Instance IS NULL))) ORDER BY t.Id",
                "@sub", WaAccount.AskSubject, "@sub2", WaLines.AskSubject))
            using (var r = cmd.ExecuteReader())
            {
                var waiting = new List<Dictionary<string, object>>();
                while (r.Read()) waiting.Add(new Dictionary<string, object> { { "ticketId", (int)r[0] }, { "storeId", (int)r[1] }, { "store", (string)r[2] },
                    { "at", DateTime.SpecifyKind((DateTime)r[3], DateTimeKind.Utc).ToString("o") }, { "extra", (string)r[4] == WaLines.AskSubject } });
                d["waiting"] = waiting;
            }
            try
            {
                var reserved = WaAccount.Reserved(c);
                d["instances"] = (await Wapilot.Instances(token)).Select(i =>
                {
                    Dictionary<string, object> st; byInstance.TryGetValue(i.Id, out st);
                    return new Dictionary<string, object> {
                        { "id", i.Id }, { "name", i.Name }, { "status", i.Status ?? "" }, { "number", i.Number ?? "" },
                        { "linked", WaAccount.Linked((i.Status ?? "").ToUpperInvariant()) }, { "reserved", reserved.Contains(i.Id) },
                        { "storeId", st == null ? (object)null : st["id"] }, { "store", st == null ? "" : st["name"] },
                        { "line", st != null && st.ContainsKey("line") ? st["line"] : null },   // one of the store's extra numbers (WaLines.cs)
                        // the store's WhatsApp service: a number whose store stopped paying can go back to the pool (or not be renewed on Wapilot)
                        { "service", st == null ? (object)null : st["service"] }, { "always", st != null && (bool)st["always"] }, { "waUntil", st == null ? "" : st["waUntil"] } };
                }).ToList();
            }
            catch (WaException ex) { d["instances"] = new List<object>(); d["error"] = ex.Message; }
            return d;
        }
    }
}
