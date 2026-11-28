// The assistant understands pictures customers send on WhatsApp (plan gate "waimages", on top of the WhatsApp service).
// Wapilot's webhook carries the picture as payload.media { url, mimetype }. The picture is downloaded only when the assistant
// answers, and only for the customer's newest turn (up to 3 pictures); older pictures stay in the history as words.
// Voice notes, videos and files still get a short "please type" reply: voice notes need a speech-to-text service the owner
// hasn't chosen yet (the user's call, 2026-11-17).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public static class WaImages
    {
        public const string Feature = "waimages";
        public const string Placeholder = "[الزبون بعت صورة]";
        public const int MaxPerTurn = 3;
        const int MaxBytes = 5 * 1024 * 1024;   // Claude's limit per image
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        static readonly Dictionary<string, string> Types = new Dictionary<string, string> {
            { "image/jpeg", "image/jpeg" }, { "image/jpg", "image/jpeg" }, { "image/png", "image/png" }, { "image/webp", "image/webp" }, { "image/gif", "image/gif" } };

        public static bool IsImage(string type, string mime)
        {
            mime = (mime ?? "").ToLowerInvariant();
            if (mime.StartsWith("image/")) return Types.ContainsKey(mime.Split(';')[0].Trim());   // stickers (webp) count; svg/heic don't
            return type == "image";
        }

        // what to say when the bot can't take a message with media in it
        public static string MediaReply(string type, string mime)
        {
            type = type ?? ""; mime = (mime ?? "").ToLowerInvariant();
            if (type == "ptt" || type == "voice" || type == "audio" || mime.StartsWith("audio/"))
                return "معلش، مش بقدر أسمع الفويس نوت 🙏 اكتبلي سؤالك وأنا أرد عليك على طول.";
            if (type == "video" || mime.StartsWith("video/") || type == "document" || mime.StartsWith("application/"))
                return "معلش، مش بقدر أفتح الفيديوهات والملفات 🙏 اكتبلي سؤالك، أو ابعت صورة، وأنا أرد عليك على طول.";
            return "معلش، أنا بقرأ الرسايل المكتوبة بس 🙏 اكتبلي سؤالك وأنا أرد عليك على طول.";
        }

        // A media URL we're willing to fetch: https (or the local fake Wapilot in tests), never an address inside our own network
        public static bool SafeUrl(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || url.Length > 800 || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            Uri testBase;
            if (Uri.TryCreate(Web.Setting("WapilotBase", "") ?? "", UriKind.Absolute, out testBase) && testBase.IsLoopback && u.IsLoopback && u.Port == testBase.Port) return true;
            // tests: our WhatsApp server running on this machine (web.config WaBridgeLocal = its http://127.0.0.1:<port>)
            if (Uri.TryCreate(Web.Setting("WaBridgeLocal", "") ?? "", UriKind.Absolute, out testBase) && testBase.IsLoopback && u.IsLoopback && u.Port == testBase.Port) return true;
            if (u.Scheme != "https" || u.IsLoopback || u.HostNameType == UriHostNameType.IPv6) return false;
            IPAddress ip;
            if (IPAddress.TryParse(u.Host, out ip))
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)) return false;
            }
            return !u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        // The picture as an image block for Claude, or null (gone, too big, not a picture). Wapilot's media links may want its token.
        public static async Task<ImageBlockParam> Load(string url, string token)
        {
            if (!SafeUrl(url)) return null;
            if (WaBridge.Is(token)) token = null;   // our server's links are signed and expire: they need no header (and its secret stays home)
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (attempt == 1 && !string.IsNullOrEmpty(token)) { req.Headers.TryAddWithoutValidation("token", token); req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token); }
                        using (var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                        {
                            if ((res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden) && attempt == 0) continue;
                            if (!res.IsSuccessStatusCode) return null;
                            var len = res.Content.Headers.ContentLength;
                            if (len.HasValue && len.Value > MaxBytes) return null;
                            string mime = (res.Content.Headers.ContentType != null ? res.Content.Headers.ContentType.MediaType : "") ?? "";
                            return FromBytes(await res.Content.ReadAsByteArrayAsync(), mime);
                        }
                    }
                }
                catch (Exception ex) { Log.Error(ex); return null; }
            }
            return null;
        }

        // A picture already downloaded (Telegram's file links carry the bot's token, so Telegram.cs fetches them itself):
        // an image block, or null (empty, too big, not a picture). mime may be null: the first bytes tell the format.
        public static ImageBlockParam FromBytes(byte[] bytes, string mime)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) return null;
            string type;
            if (!Types.TryGetValue((mime ?? "").ToLowerInvariant(), out type)) type = Sniff(bytes);
            if (type == null) return null;
            return new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(bytes), MediaType = Media(type) } };
        }

        static MediaType Media(string type)
        {
            switch (type) { case "image/png": return MediaType.ImagePng; case "image/webp": return MediaType.ImageWebP; case "image/gif": return MediaType.ImageGif; default: return MediaType.ImageJpeg; }
        }

        // a media server that says application/octet-stream: tell the format from the first bytes
        static string Sniff(byte[] b)
        {
            if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
            if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
            return null;
        }

        // For the assistant, when the customer's newest turn has pictures in it
        public const string Note = "\n- الزبون بعت صورة (موجودة في رسالته). لو صورة منتج: دوّر في منتجات المتجر على الأقرب ليها ورشّحه بالاسم والسعر واللينك، " +
            "ولو مش متأكد إنه نفس المنتج قول كده بصراحة واسأله سؤال يوضّح (اللون، المقاس، الخامة). ماتخترعش منتج مش في بيانات المتجر. " +
            "لو الصورة لمشكلة (منتج تالف، أوردر غلط، إيصال تحويل): اعتذر بلطف ووضّح إن المتجر هيشوفها ويتابع معاه، وماتوعدش بحاجة.";
    }
}
