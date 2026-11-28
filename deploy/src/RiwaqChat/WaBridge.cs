// WhatsApp through Sellybot's own server (deploy/wa-bridge: Node.js + Baileys, one linked-device connection per number), the
// default since 2026-11-28 instead of Wapilot (the user's decision). AppSettings "WaProvider" = "bridge" (default) | "wapilot":
// "wapilot" brings the old Wapilot account back (its code is kept in WhatsApp.cs, unused).
//
// - The bridge's address and secret are typed once in admin «سيرفر الواتساب» (api/wa-admin.ashx ?action=bridge) and kept in
//   AppSettings WaBridgeUrl (default https://wa.sellybot.online) and WaBridgeSecret (Secrets.Protect), never in the repo.
// - Sellybot -> bridge: every call carries X-Bridge-Secret. Sellybot creates a number itself when a store links one
//   (WaAccount.AssignFree / WaLines.AssignFree -> Create) and deletes it when the store lets it go (Wapilot.Release).
// - bridge -> Sellybot: api/wa-webhook.ashx?src=bridge, every body signed: X-Bridge-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret,
//   "<t>.<body>")>, refused when older than 5 minutes (Verify). Messages are routed by instance_id, like Wapilot's.
// - The "token" the WhatsApp code passes around (WaAccount.PlatformToken) is "bridge|<url>|<secret>" in this mode: Wapilot's
//   methods see it and hand the call to the ones here, so every sender (replies, confirmations, offers, alerts, SellyChat...)
//   works the same on either provider without knowing which one it is.
// - Watch (a lane of Jobs, every 3 minutes): the bridge's /health and every number's state. A number whose phone removed the
//   linked device gets a warning on the owner's dashboard (with one button to scan again) and a WhatsApp alert to the admin;
//   the bridge down shows on top of every admin page. The bridge also posts "session.status" the moment a number changes.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class WaBridge
    {
        public const string DefaultUrl = "https://wa.sellybot.online";
        const string Prefix = "bridge|";

        // ---------- settings ----------
        public static bool On(SqlConnection c) { return !string.Equals((Billing.Get(c, "WaProvider") ?? "bridge").Trim(), "wapilot", StringComparison.OrdinalIgnoreCase); }
        public static string Url(SqlConnection c) { var u = (Billing.Get(c, "WaBridgeUrl") ?? "").Trim(); return (u.Length == 0 ? DefaultUrl : u).TrimEnd('/'); }
        public static string Secret(SqlConnection c) { var box = Billing.Get(c, "WaBridgeSecret"); return string.IsNullOrEmpty(box) ? null : Secrets.Unprotect(box); }
        // the provider token for this mode, or null while no secret is saved
        public static string Token(SqlConnection c) { var s = Secret(c); return string.IsNullOrEmpty(s) ? null : Pack(Url(c), s); }

        public static string Pack(string url, string secret) { return Prefix + (url ?? "").TrimEnd('/') + "|" + secret; }
        public static bool Is(string token) { return token != null && token.StartsWith(Prefix, StringComparison.Ordinal); }
        public static bool Unpack(string token, out string url, out string secret)
        {
            url = secret = null;
            if (!Is(token)) return false;
            var rest = token.Substring(Prefix.Length); int i = rest.LastIndexOf('|');
            if (i <= 0) return false;
            url = rest.Substring(0, i); secret = rest.Substring(i + 1);
            return url.Length > 0 && secret.Length > 0;
        }

        // an address the admin may save: https, or a local one for tests (http://127.0.0.1 / localhost)
        public static string CleanUrl(string raw)
        {
            Uri u; raw = (raw ?? "").Trim().TrimEnd('/');
            if (raw.Length == 0) return DefaultUrl;
            if (!raw.Contains("://")) raw = "https://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out u) || !string.IsNullOrEmpty(u.UserInfo) || (u.Query ?? "").Length > 1 || raw.Contains("|")) return null;
            if (u.Scheme == "https") return raw;
            return u.Scheme == "http" && u.IsLoopback ? raw : null;
        }
        // the secret: what the server's .env holds (letters, digits, - _), 32 to 200 characters
        public static bool GoodSecret(string s) { return s != null && s.Length >= 32 && s.Length <= 200 && s.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_'); }

        // ---------- calls ----------
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        static WaBridge() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0"); }
        static DateTime lastDownLog = DateTime.MinValue;

        // What owners may read when something fails (they see some of these): plain words, never the provider's name.
        public static WaException Friendly(int status, string code)
        {
            code = code ?? "";
            string text;
            if (status == 0) text = "خدمة الواتساب مش متاحة دلوقتي. جرّب تاني كمان شوية.";
            else if (status == 401) text = "إعداد خدمة الواتساب محتاج مراجعة من عندنا، ومش محتاج تعمل أي حاجة. بلّغنا فريق الدعم وهنظبطه.";
            else if (code == "INSTANCE_NOT_FOUND") text = "رقم الربط ده مش موجود. دوس «إلغاء الربط» واربط تاني، أو كلّم الدعم.";
            else if (code == "INSTANCE_NOT_READY") text = "رقم الواتساب مش متصل دلوقتي. اربطه تاني بالـ QR.";
            else if (code == "BAD_CHAT") text = "رقم الموبايل ده مش مظبوط.";
            else if (code == "BRIDGE_FULL") text = "مفيش مكان لرقم جديد دلوقتي. بعتنا للدعم وهنجهّزلك مكان في أقرب وقت.";
            else if (status == 413) text = "الصورة أو الرسالة كبيرة. ابعت صورة أصغر من 10 ميجا.";
            else if (status == 429) text = "الطلبات كتير دلوقتي، هنحاول تاني بعد شوية.";
            else text = "خدمة الواتساب مش متاحة دلوقتي (" + status + (code.Length > 0 ? " " + code : "") + "). جرّب تاني كمان شوية.";
            return new WaException(text, status, code.Length > 0 ? code : null);
        }

        static Task<JsonElement> Call(string token, HttpMethod method, string path, object body = null, string idem = null)
        {
            string url, secret;
            if (!Unpack(token, out url, out secret)) throw Friendly(401, "UNAUTHORIZED");
            return Call(url, secret, method, path, body, idem);
        }

        static async Task<JsonElement> Call(string baseUrl, string secret, HttpMethod method, string path, object body, string idem, int timeoutSec = 20)
        {
            using (var req = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + "/" + path))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)))
            {
                if (secret != null) req.Headers.TryAddWithoutValidation("X-Bridge-Secret", secret);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (idem != null) req.Headers.TryAddWithoutValidation("Idempotency-Key", idem.Length > 200 ? idem.Substring(0, 200) : idem);
                if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                HttpResponseMessage resp;
                try { resp = await Http.SendAsync(req, cts.Token); }
                catch (Exception ex)
                {
                    // the bridge down: logged once a minute, not once per message
                    if (DateTime.UtcNow - lastDownLog > TimeSpan.FromMinutes(1)) { lastDownLog = DateTime.UtcNow; Log.Error(new Exception("WhatsApp bridge unreachable: " + method + " " + path.Split('/')[0] + ": " + ex.Message)); }
                    throw Friendly(0, "BRIDGE_DOWN");
                }
                using (resp)
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    JsonElement root = default(JsonElement); bool parsed = false;
                    try { using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)) { root = doc.RootElement.Clone(); parsed = true; } } catch { }
                    int status = (int)resp.StatusCode;
                    if (status >= 200 && status < 300 && parsed) return root;
                    string code = parsed ? WaJson.S(root, "code") : null;
                    if (status >= 500 || status == 401) Log.Error(new Exception("WhatsApp bridge " + method + " " + path.Split('/')[0] + " -> " + status + " " + code));
                    throw Friendly(status, code ?? (parsed ? null : "NOT_JSON"));
                }
            }
        }

        static string Id(string instance) { return Uri.EscapeDataString(instance ?? ""); }

        // the bridge's instance JSON -> the shape the WhatsApp code already reads
        public static Wapilot.Instance ToInstance(JsonElement e)
        {
            return new Wapilot.Instance { Id = WaJson.S(e, "instance_uniquename", "id"), Name = WaJson.S(e, "instance_name", "name"), Status = WaJson.S(e, "status") ?? "",
                Number = WaChat.Digits(WaJson.S(e, "me_id")), IsApi = true };
        }

        public static async Task<List<Wapilot.Instance>> Instances(string token)
        {
            var root = await Call(token, HttpMethod.Get, "instances");
            var list = new List<Wapilot.Instance>(); JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("instances", out arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) { var i = ToInstance(e); if (!string.IsNullOrEmpty(i.Id)) list.Add(i); }
            return list;
        }

        // a new number on the bridge (no phone yet: its QR comes when asked). name: for the admin's list
        public static async Task<Wapilot.Instance> Create(string token, string name)
        {
            var r = await Call(token, HttpMethod.Post, "instances", new Dictionary<string, object> { { "name", name ?? "" } });
            var i = ToInstance(WaJson.O(r, "instance") ?? r);
            if (string.IsNullOrEmpty(i.Id)) throw Friendly(502, "BAD_ANSWER");
            return i;
        }

        public static async Task<Wapilot.Instance> Status(string token, string instance)
        {
            var r = await Call(token, HttpMethod.Get, "instances/" + Id(instance) + "/status");
            return new Wapilot.Instance { Id = instance, Status = WaJson.S(r, "status") ?? "", Number = WaChat.Digits(WaJson.S(r, "me_id")) };
        }

        // the QR as "data:image/png;base64,...", or null (linked already, or still starting: asked again in a few seconds)
        public static async Task<string> Qr(string token, string instance)
        {
            var r = await Call(token, HttpMethod.Get, "instances/" + Id(instance) + "/qr-code");
            var qr = WaJson.S(r, "qr");
            return string.IsNullOrEmpty(qr) || !qr.StartsWith("data:image/", StringComparison.Ordinal) ? null : qr;
        }

        public static Task Start(string token, string instance) { return Call(token, HttpMethod.Post, "instances/" + Id(instance) + "/start", new Dictionary<string, object>()); }
        public static Task Logout(string token, string instance) { return Call(token, HttpMethod.Post, "instances/" + Id(instance) + "/logout", new Dictionary<string, object>()); }
        public static async Task Delete(string token, string instance)
        {
            try { await Call(token, HttpMethod.Delete, "instances/" + Id(instance)); }
            catch (WaException ex) when (ex.Code == "INSTANCE_NOT_FOUND") { }   // gone already
        }

        // queued on the bridge, which sends it in its turn (paced): its message id, for MessageStatus
        public static async Task<string> Send(string token, string instance, string chatId, string text, string idem)
        {
            var r = await Call(token, HttpMethod.Post, "instances/" + Id(instance) + "/send-message", new Dictionary<string, object> { { "chat_id", chatId }, { "text", text } }, idem);
            return WaJson.S(r, "message_id");
        }

        public static async Task<string> SendImage(string token, string instance, string chatId, byte[] image, string contentType, string caption, string idem)
        {
            var body = new Dictionary<string, object> { { "chat_id", chatId }, { "base64", Convert.ToBase64String(image ?? new byte[0]) }, { "mimetype", contentType ?? "image/jpeg" } };
            if (!string.IsNullOrEmpty(caption)) body["caption"] = caption;
            var r = await Call(token, HttpMethod.Post, "instances/" + Id(instance) + "/send-image", body, idem);
            return WaJson.S(r, "message_id");
        }

        // "typing…" in the customer's chat while the assistant writes (WhatsApp clears it by itself after a few seconds)
        public static Task Typing(string token, string instance, string chatId)
        {
            return Call(token, HttpMethod.Post, "instances/" + Id(instance) + "/send-typing", new Dictionary<string, object> { { "chat_id", chatId } });
        }

        public static async Task<Wapilot.Sent> MessageStatus(string token, string instance, string messageId)
        {
            JsonElement r;
            try { r = await Call(token, HttpMethod.Get, "instances/" + Id(instance) + "/messages/" + Uri.EscapeDataString(messageId ?? "")); }
            catch (WaException ex) when (ex.Status == 404) { return null; }
            return ToSent(r);
        }
        public static Wapilot.Sent ToSent(JsonElement r)
        {
            var d = WaJson.O(r, "data") ?? r;
            string status = (WaJson.S(d, "status") ?? "").ToLowerInvariant(), err = WaJson.S(d, "error_code");
            if (status == "failed" || !string.IsNullOrEmpty(err)) return new Wapilot.Sent { Status = "failed", Error = err ?? "failed" };
            if (status == "delivered" || status == "sent" || status == "read") return new Wapilot.Sent { Status = "delivered" };
            return new Wapilot.Sent { Status = "pending" };
        }

        public static async Task<string> LidPhone(string token, string instance, string lid)
        {
            var digits = new string((lid ?? "").TakeWhile(char.IsDigit).ToArray());
            if (digits.Length < 6) return null;
            try { return WaChat.Digits(WaJson.S(await Call(token, HttpMethod.Get, "instances/" + Id(instance) + "/lids/" + digits), "phone")); }
            catch (WaException) { return null; }
        }

        // ---------- the webhook's signature ----------
        public static string Sign(string secret, long t, byte[] body)
        {
            var head = Encoding.UTF8.GetBytes(t.ToString(CultureInfo.InvariantCulture) + ".");
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                h.TransformBlock(head, 0, head.Length, null, 0);
                h.TransformFinalBlock(body, 0, body.Length);
                return "t=" + t.ToString(CultureInfo.InvariantCulture) + ",v1=" + string.Concat(h.Hash.Select(b => b.ToString("x2")));
            }
        }

        // why: null when good; "no_secret" / "no_signature" / "too_old" / "bad_signature"
        public static string Verify(string secret, string header, byte[] body, long nowUnix)
        {
            if (string.IsNullOrEmpty(secret)) return "no_secret";
            var m = System.Text.RegularExpressions.Regex.Match((header ?? "").Trim(), "^t=(\\d{1,12}),v1=([0-9a-f]{64})$");
            if (!m.Success) return "no_signature";
            long t = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (Math.Abs(nowUnix - t) > 300) return "too_old";
            var expect = Sign(secret, t, body ?? new byte[0]);
            return Secrets.Same(Encoding.ASCII.GetBytes(expect), Encoding.ASCII.GetBytes("t=" + m.Groups[1].Value + ",v1=" + m.Groups[2].Value)) ? null : "bad_signature";
        }

        public static long Unix(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        // api/wa-webhook.ashx?src=bridge: every event from the bridge (WhatsAppWebhookHandler hands it here)
        static DateTime lastBadSig = DateTime.MinValue, lastShapeLog = DateTime.MinValue;
        public static void Webhook(HttpContext ctx, SqlConnection c)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }
            byte[] body;
            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192]; int n;
                while ((n = req.InputStream.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 512 * 1024) { res.StatusCode = 413; return; } }
                body = ms.ToArray();
            }
            var why = Verify(Secret(c), req.Headers["X-Bridge-Signature"], body, Unix(DateTime.UtcNow));
            if (why != null)
            {
                if (DateTime.UtcNow - lastBadSig > TimeSpan.FromMinutes(10)) { lastBadSig = DateTime.UtcNow; Log.Error(new Exception("WhatsApp bridge webhook refused: " + why)); }
                Web.Json(res, 401, new { ok = false, error = why });
                return;
            }
            JsonElement root;
            try { using (var doc = JsonDocument.Parse(body)) root = doc.RootElement.Clone(); }
            catch { Web.Json(res, 400, new { ok = false }); return; }
            string ev = (WaJson.S(root, "event") ?? "").ToLowerInvariant(), instance = WaJson.S(root, "instance_id");
            if (ev == "ping") { Web.Json(res, 200, new { ok = true, pong = true }); return; }   // «جرّب الاتصال»
            if (ev == "session.status")
            {
                var p = WaJson.O(root, "payload") ?? root;
                Apply(c, instance, WaJson.S(p, "status") ?? "", WaChat.Digits(WaJson.S(p, "me_id")));
                Web.Json(res, 200, new { ok = true });
                return;
            }
            var m = WhatsAppBot.Parse(root);
            int line; var store = WaLines.Route(c, m.Instance, out line);   // a store's main number, or one of its extra ones
            if (store != null && !WhatsAppBot.Handle(c, store, m, line) && DateTime.UtcNow - lastShapeLog > TimeSpan.FromMinutes(10))
            {
                lastShapeLog = DateTime.UtcNow;
                Log.Error(new Exception("WhatsApp bridge webhook not understood (store " + store.Slug + "): " + WaJson.Shape(root)));
            }
            Web.Json(res, 200, new { ok = true });
        }

        // ---------- a number's state: what the owner and the admin are told ----------
        // the owner's warning on a number whose phone removed the linked device (WhatsApp ← الأجهزة المرتبطة): one button scans again
        public const string LostText = "⚠️ رقم الواتساب اتفصل عن الموبايل (اتشال من «الأجهزة المرتبطة»)، فالمساعد مش بيرد عليه. دوس «اربطه تاني» وامسح الكود من نفس الموبايل.";
        public const string DownText = "⏳ الاتصال بالواتساب فاصل دلوقتي، وبنحاول نرجّعه لوحده. مش محتاج تعمل حاجة.";
        public const string RefusedText = "⚠️ واتساب رفض الربط على الرقم ده (ممكن يكون الرقم اتحظر من واتساب). كلّم الدعم.";
        public static bool NeedsQr(string status) { return status == LostText; }
        static bool Ours(string status) { return status == LostText || status == DownText || status == RefusedText; }

        // a status the bridge posted (the webhook) or Watch found (every 3 minutes): acted on once, when it changes what the database says
        public static void Apply(SqlConnection c, string instance, string status, string number)
        {
            int line; var s = WaLines.Route(c, instance, out line);
            if (s == null) return;
            var up = (status ?? "").ToUpperInvariant();
            string had = line == 0 ? s.WaStatus : LineField(c, line, "Status"), num = line == 0 ? s.WaNumber : LineField(c, line, "Number");
            if (up == "WORKING")
            {
                if (number != null && number != num) Record(c, s, line, number);
                if (Ours(had)) ClearOurs(c, s.Id, line, had);
                return;
            }
            if (up == "LOGGED_OUT")
            {
                if (had == LostText && string.IsNullOrEmpty(num)) return;   // known already
                using (var cmd = Db.Cmd(c, line == 0 ? "UPDATE dbo.Stores SET WaNumber=NULL, WaStatus=@t WHERE Id=@s" : "UPDATE dbo.WaLines SET Number=NULL, Status=@t WHERE Id=@l AND StoreId=@s",
                    "@t", LostText, "@s", s.Id, "@l", line)) cmd.ExecuteNonQuery();
                Alert(s, line, num, "📵 *واتساب متجر " + s.Name + " اتفصل*" + (line > 0 ? " (رقم إضافي)" : "") + (num != null ? "\nالرقم: " + WaLine.Local(num) : "") +
                    "\nالموبايل شال الربط من «الأجهزة المرتبطة». صاحب المتجر شايف تنبيه في لوحته يربطه تاني بالـ QR.", "sb-wa-lost-" + instance + "-" + DateTime.UtcNow.ToString("yyyyMMddHH"));
                return;
            }
            if (up == "FAILED")
            {
                if (had == RefusedText) return;
                WaLines.SetStatus(c, s.Id, line, RefusedText);
                Alert(s, line, num, "🚫 *واتساب رفض رقم متجر " + s.Name + "*" + (num != null ? "\nالرقم: " + WaLine.Local(num) : "") + "\nممكن يكون الرقم اتحظر. افتح «سيرفر الواتساب» في الأدمن.", "sb-wa-refused-" + instance + "-" + DateTime.UtcNow.ToString("yyyyMMdd"));
                return;
            }
            // linked, the connection down for a while: the bridge keeps trying by itself; nothing for the owner to do
            if (up == "DISCONNECTED" && !string.IsNullOrEmpty(num) && (had == null || had == DownText)) { if (had == null) WaLines.SetStatus(c, s.Id, line, DownText); }
        }

        // our own warning goes once the number is fine again (only ours: a reply that failed keeps its own words)
        static void ClearOurs(SqlConnection c, int storeId, int line, string had)
        {
            using (var cmd = Db.Cmd(c, line == 0 ? "UPDATE dbo.Stores SET WaStatus=NULL WHERE Id=@s AND WaStatus=@h" : "UPDATE dbo.WaLines SET Status=NULL WHERE Id=@l AND StoreId=@s AND Status=@h",
                "@s", storeId, "@l", line, "@h", had)) cmd.ExecuteNonQuery();
        }

        static string LineField(SqlConnection c, int line, string field)
        {
            using (var cmd = Db.Cmd(c, "SELECT " + (field == "Number" ? "Number" : "Status") + " FROM dbo.WaLines WHERE Id=@l", "@l", line)) return cmd.ExecuteScalar() as string;
        }

        // the phone now on a number (linked from the bridge's side): kept, unless it is another of the store's numbers already
        static void Record(SqlConnection c, StoreInfo s, int line, string number)
        {
            bool dup;
            using (var cmd = Db.Cmd(c, line == 0 ? "SELECT COUNT(*) FROM dbo.WaLines WHERE StoreId=@s AND Number=@n"
                                                 : "SELECT COUNT(*) FROM dbo.WaLines WHERE StoreId=@s AND Number=@n AND Id<>@l", "@s", s.Id, "@n", number, "@l", line))
                dup = (int)cmd.ExecuteScalar() > 0 || (line > 0 && number == s.WaNumber);
            if (dup) return;   // the dashboard's QR check unlinks it and says why (WhatsAppHandler / WaLinesHandler "qr")
            using (var cmd = Db.Cmd(c, line == 0 ? "UPDATE dbo.Stores SET WaNumber=@n WHERE Id=@s" : "UPDATE dbo.WaLines SET Number=@n WHERE Id=@l AND StoreId=@s", "@n", number, "@s", s.Id, "@l", line))
                cmd.ExecuteNonQuery();
        }

        // ---------- the admin ----------
        // a WhatsApp alert to the admin (the admin stores' own numbers: Stores.IsAdmin with OwnerPhone), in the background
        static void Alert(StoreInfo s, int line, string number, string text, string idem)
        {
            if (!Web.Allow("wa-bridge-alert:" + idem, 1, TimeSpan.FromHours(6))) return;
            System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { await ToAdmin(text, idem); } catch (Exception ex) { Log.Error(ex); } });
        }

        public static async Task<bool> ToAdmin(string text, string idem)
        {
            var ids = new List<int>();
            using (var c = Db.Open())
            {
                using (var cmd = Db.Cmd(c, "SELECT Id FROM dbo.Stores WHERE IsAdmin=1 AND OwnerPhone IS NOT NULL AND WaInstance IS NOT NULL")) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add((int)r[0]);
                foreach (var id in ids) { var a = Db.StoreById(c, id); if (a != null && await WaNotify.ToOwner(c, a, text, idem) == null) return true; }
            }
            return false;
        }

        // what Watch last saw of the bridge (AppSettings, each value short: the column holds 400 characters)
        public class Seen { public string At, OkAt, DownSince, Error, Version; public int Rss, Numbers, Linked, Lost; public bool Alerted; }
        public static Seen LastSeen(SqlConnection c)
        {
            var v = Billing.Get(c, "WaBridgeSeen");
            if (string.IsNullOrEmpty(v)) return new Seen();
            try { return JsonSerializer.Deserialize<Seen>(v, new JsonSerializerOptions { IncludeFields = true }) ?? new Seen(); } catch { return new Seen(); }
        }
        static void Keep(SqlConnection c, Seen s)
        {
            if (s.Error != null && s.Error.Length > 80) s.Error = s.Error.Substring(0, 80);
            Billing.Set(c, "WaBridgeSeen", JsonSerializer.Serialize(s, new JsonSerializerOptions { IncludeFields = true }));
        }

        // For the admin page: the bridge's state from the last check, and how many numbers need the owner
        public static Dictionary<string, object> WatchJson(SqlConnection c)
        {
            var s = LastSeen(c);
            int lost;
            using (var cmd = Db.Cmd(c, "SELECT (SELECT COUNT(*) FROM dbo.Stores WHERE WaStatus=@t) + (SELECT COUNT(*) FROM dbo.WaLines WHERE Status=@t)", "@t", LostText)) lost = (int)cmd.ExecuteScalar();
            return new Dictionary<string, object> { { "at", s.At ?? "" }, { "okAt", s.OkAt ?? "" }, { "downSince", s.DownSince ?? "" }, { "error", s.Error ?? "" },
                { "version", s.Version ?? "" }, { "rss", s.Rss }, { "numbers", s.Numbers }, { "linked", s.Linked }, { "lost", lost } };
        }

        // a lane of Jobs: every 3 minutes, the bridge's health and each number's state
        public static async Task Watch(CancellationToken ct)
        {
            string token, url, secret;
            using (var c = Db.Open())
            {
                if (!On(c)) return;
                token = Token(c);
            }
            if (token == null || !Unpack(token, out url, out secret)) return;
            int every; if (!int.TryParse(Web.Setting("WaWatchEverySec"), out every) || every < 1) every = 170;   // the setting: tests only
            if (!Web.Allow("wa-bridge-watch", 1, TimeSpan.FromSeconds(every))) return;
            JsonElement health = default(JsonElement); List<Wapilot.Instance> list = null; string err = null;
            try
            {
                health = await Call(url, secret, HttpMethod.Get, "health", null, null, 10);
                if (WaJson.S(health, "version") == null) err = "السيرفر رد، بس المفتاح مش هو نفس المفتاح اللي عليه";
                else list = await Instances(token);
            }
            catch (WaException ex) { err = ex.Status == 0 ? "مش بيرد" : "رد بخطأ " + ex.Status; }
            using (var c = Db.Open())
            {
                var seen = LastSeen(c); var now = DateTime.UtcNow;
                seen.At = now.ToString("o");
                if (list == null)
                {
                    seen.Error = err;
                    if (string.IsNullOrEmpty(seen.DownSince)) seen.DownSince = now.ToString("o");
                    Keep(c, seen);
                    return;
                }
                // back after being down: the admin hears for how long (the alert itself goes out on WhatsApp, through the bridge)
                if (!string.IsNullOrEmpty(seen.DownSince))
                {
                    DateTime since;
                    if (DateTime.TryParse(seen.DownSince, null, DateTimeStyles.RoundtripKind, out since) && now - since > TimeSpan.FromMinutes(5))
                    {
                        var mins = (int)Math.Round((now - since).TotalMinutes);
                        System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(async t2 => { try { await ToAdmin("✅ *سيرفر الواتساب رجع يشتغل* بعد ما كان واقف حوالي " + mins + " دقيقة. الرسايل اللي وصلت وهو واقف وصلت متأخر أو ماوصلتش.", "sb-wa-back-" + now.Ticks); } catch (Exception ex) { Log.Error(ex); } });
                    }
                    seen.DownSince = null;
                }
                seen.OkAt = now.ToString("o"); seen.Error = null; seen.Version = WaJson.S(health, "version");
                var mem = WaJson.O(health, "memoryMb"); int rss; if (mem.HasValue && int.TryParse(WaJson.S(mem.Value, "rss") ?? "", out rss)) seen.Rss = rss;
                seen.Numbers = list.Count; seen.Linked = list.Count(i => WaAccount.Linked((i.Status ?? "").ToUpperInvariant()));
                foreach (var i in list) try { Apply(c, i.Id, i.Status, i.Number); } catch (Exception ex) { Log.Error(ex); }
                Keep(c, seen);
            }
        }

        // ---------- «جرّب الاتصال» (admin): three steps, each said plainly ----------
        public static async Task<Dictionary<string, object>> Test(string url, string secret)
        {
            var steps = new List<Dictionary<string, object>>();
            Action<string, bool, string> step = (name, ok, text) => steps.Add(new Dictionary<string, object> { { "step", name }, { "ok", ok }, { "text", text } });
            JsonElement h = default(JsonElement);
            bool reach = false, key = false, hook = false;
            try { await Call(url, null, HttpMethod.Get, "health", null, null, 10); reach = true; step("reach", true, "السيرفر شغال وبيرد على " + url); }
            catch (WaException ex) { step("reach", false, ex.Code == "NOT_JSON" ? "العنوان ده مش سيرفر الواتساب بتاعنا (رد بصفحة مش مفهومة). اتأكد من العنوان." : ex.Status == 0 ? "مقدرناش نوصل للسيرفر على " + url + ". اتأكد إن العنوان صح وإن السيرفر شغال." : "السيرفر رد بخطأ " + ex.Status + ". اتأكد إن العنوان صح."); }
            if (reach)
            {
                try
                {
                    h = await Call(url, secret, HttpMethod.Get, "health", null, null, 10);
                    key = WaJson.S(h, "version") != null;
                    step("key", key, key ? "المفتاح صح" : "المفتاح مش هو نفس المفتاح اللي على السيرفر (ملف .env). انسخه تاني من غير مسافات.");
                }
                catch (WaException ex) { step("key", false, "السيرفر رفض المفتاح (" + ex.Status + ")."); }
            }
            if (key)
            {
                try
                {
                    var p = await Call(url, secret, HttpMethod.Post, "webhook-test", new Dictionary<string, object>(), null, 25);
                    int st; int.TryParse(WaJson.S(p, "status") ?? "0", out st);
                    hook = WaJson.B(p, "ok") == true;
                    step("hook", hook, hook ? "الرسايل من السيرفر بتوصل لسيلّي ومتوقّعة صح"
                        : st == 401 ? "السيرفر وصل لسيلّي، بس سيلّي رفض التوقيع: المفتاح اللي على السيرفر غير المحفوظ هنا."
                        : st == 0 ? "السيرفر مش قادر يوصل لسيلّي (" + (WaJson.S(p, "error") ?? "مفيش رد") + "). اتأكد من WEBHOOK_URL في ملف .env."
                        : "السيرفر وصل لسيلّي، بس رجعله خطأ " + st + ".");
                }
                catch (WaException ex) { step("hook", false, "مقدرناش نجرّب وصول الرسايل لسيلّي (" + ex.Status + ")."); }
            }
            var d = new Dictionary<string, object> { { "ok", reach && key && hook }, { "steps", steps } };
            if (key)
            {
                var mem = WaJson.O(h, "memoryMb"); var inst = WaJson.O(h, "instances");
                d["version"] = WaJson.S(h, "version") ?? ""; d["uptimeSec"] = WaJson.S(h, "uptimeSec") ?? "";
                d["rss"] = mem.HasValue ? WaJson.S(mem.Value, "rss") ?? "" : ""; d["numbers"] = inst.HasValue ? WaJson.S(inst.Value, "total") ?? "0" : "0";
                d["max"] = WaJson.S(h, "maxInstances") ?? "";
            }
            return d;
        }
    }
}
