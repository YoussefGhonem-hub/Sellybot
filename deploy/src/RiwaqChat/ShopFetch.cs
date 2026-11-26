// Pictures fetched from somebody else's address (a product moved from Shopify / EasyOrders / WooCommerce, a picture the owner sent on
// WhatsApp) and made into the store's own, the way an upload is: shrunk to 1300 px (and 480 px for the small one) and kept by ShopImages.
// The address is never trusted: http/https only, the name is looked up and every address it gives must be a public one (no loopback,
// private, link-local, carrier-grade NAT or reserved ranges, IPv4 or IPv6), redirects are followed by hand and checked again (3 at most),
// one size cap, one timeout, and the bytes themselves must be a JPEG, PNG or WebP. Setting "ShopFetchAllow" (host:port, local tests only)
// lets one fake server on this machine through.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public static class ShopFetch
    {
        public const int MaxBytes = 15 * 1024 * 1024, MaxPixels = 40000000;
        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(25) };
        static ShopFetch() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; Http.DefaultRequestHeaders.UserAgent.ParseAdd("SellyStore/1.0 (+picture import)"); }

        public class Got { public byte[] Bytes; public string Why; }

        // null = an address we may ask; otherwise why not (in the owner's words)
        public static string Bad(Uri u)
        {
            if (u == null || (u.Scheme != "http" && u.Scheme != "https")) return "اللينك مش http أو https";
            if (!string.IsNullOrEmpty(u.UserInfo)) return "اللينك فيه اسم مستخدم";
            if (Allowed(u)) return null;
            IPAddress[] ips; IPAddress one;
            if (IPAddress.TryParse(u.Host.Trim('[', ']'), out one)) ips = new[] { one };
            else { try { ips = Dns.GetHostAddresses(u.Host); } catch { return "اسم الموقع مش موجود"; } }
            if (ips.Length == 0) return "اسم الموقع مش موجود";
            return ips.Any(Private) ? "عنوان داخلي مش مسموح" : null;
        }
        static bool Allowed(Uri u) { var a = Web.Setting("ShopFetchAllow", "") ?? ""; return a.Length > 0 && string.Equals(a, u.Host + ":" + u.Port, StringComparison.OrdinalIgnoreCase); }

        public static bool Private(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113) || b[0] >= 224;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
                    || (b.Take(10).All(x => x == 0) && b[10] == 0 && b[11] == 0);   // ::/96 (IPv4-compatible)
            return true;
        }

        // the bytes at the address, or why not. token: Wapilot's media links may want it (a second try with it)
        public static async Task<Got> Get(string url, int max = MaxBytes, string token = null, CancellationToken ct = default(CancellationToken))
        {
            Uri u; if (string.IsNullOrWhiteSpace(url) || url.Length > 2000 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return new Got { Why = "اللينك مش مظبوط" };
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                for (int hop = 0, auth = 0; hop < 4; hop++)
                {
                    var why = Bad(u); if (why != null) return new Got { Why = why };
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Get, u))
                        {
                            if (auth == 1 && token != null) { req.Headers.TryAddWithoutValidation("token", token); req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token); }
                            using (var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                            {
                                int code = (int)res.StatusCode;
                                if (code >= 300 && code < 400 && res.Headers.Location != null) { u = res.Headers.Location.IsAbsoluteUri ? res.Headers.Location : new Uri(u, res.Headers.Location); continue; }
                                if ((code == 401 || code == 403) && token != null && auth == 0) { auth = 1; hop--; continue; }
                                if (code == 404 || code == 410) return new Got { Why = "الصورة مش موجودة على اللينك" };
                                if (code < 200 || code >= 300) return new Got { Why = "الموقع رجّع خطأ " + code };
                                var len = res.Content.Headers.ContentLength; if (len.HasValue && len.Value > max) return new Got { Why = "الصورة أكبر من " + (max / 1048576) + " ميجا" };
                                using (var s = await res.Content.ReadAsStreamAsync())
                                using (var ms = new MemoryStream())
                                {
                                    var buf = new byte[81920]; int n;
                                    while ((n = await s.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) return new Got { Why = "الصورة أكبر من " + (max / 1048576) + " ميجا" }; }
                                    return new Got { Bytes = ms.ToArray() };
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { return new Got { Why = ct.IsCancellationRequested ? "اتلغى" : "الموقع اتأخر في الرد" }; }
                    catch (Exception) { return new Got { Why = "مقدرناش نوصل للموقع" }; }
                }
                return new Got { Why = "تحويلات كتير" };
            }
        }

        // ---------- the picture, the way an upload is made ----------
        public class Pic { public byte[] Big, Small; public int W, H; public string Ext; }

        // null = not a picture we take (the bytes decide, not the name or the type the server said)
        public static Pic Make(byte[] d)
        {
            var ext = ShopImages.Sniff(d); if (ext == null) return null;
            if (ext == "webp")
            {
                // GDI+ can't read WebP: kept as it came when it's already light, read for its size from the header
                int w, h; if (!WebpSize(d, out w, out h) || (long)w * h > MaxPixels || d.Length > ShopImages.MaxBig) return null;
                return new Pic { Big = d, Small = d.Length <= ShopImages.MaxSmall ? d : null, W = w, H = h, Ext = "webp" };
            }
            try
            {
                using (var ms = new MemoryStream(d))
                using (var img = Image.FromStream(ms, false, false))
                {
                    if ((long)img.Width * img.Height > MaxPixels || img.Width < 1 || img.Height < 1) return null;
                    Turn(img);
                    int w, h, w2, h2; var big = Jpeg(img, 1300, 84, out w, out h);
                    if (big.Length > 1000000) big = Jpeg(img, 1100, 74, out w, out h);
                    if (big.Length > ShopImages.MaxBig) big = Jpeg(img, 900, 68, out w, out h);
                    if (big.Length > ShopImages.MaxBig) return null;
                    return new Pic { Big = big, Small = Jpeg(img, 480, 80, out w2, out h2), W = w, H = h, Ext = "jpg" };
                }
            }
            catch (Exception) { return null; }
        }

        // a phone's picture says which way is up (EXIF 274)
        static void Turn(Image img)
        {
            if (!img.PropertyIdList.Contains(0x0112)) return;
            var p = img.GetPropertyItem(0x0112); int o = p.Value != null && p.Value.Length > 0 ? p.Value[0] : 1;
            var f = o == 2 ? RotateFlipType.RotateNoneFlipX : o == 3 ? RotateFlipType.Rotate180FlipNone : o == 4 ? RotateFlipType.Rotate180FlipX : o == 5 ? RotateFlipType.Rotate90FlipX : o == 6 ? RotateFlipType.Rotate90FlipNone : o == 7 ? RotateFlipType.Rotate270FlipX : o == 8 ? RotateFlipType.Rotate270FlipNone : RotateFlipType.RotateNoneFlipNone;
            if (f != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(f);
            try { img.RemovePropertyItem(0x0112); } catch { }
        }

        static ImageCodecInfo jpegCodec;
        static byte[] Jpeg(Image src, int max, long q, out int w, out int h)
        {
            double k = Math.Min(1.0, (double)max / Math.Max(src.Width, src.Height));
            w = Math.Max(1, (int)Math.Round(src.Width * k)); h = Math.Max(1, (int)Math.Round(src.Height * k));
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var at = new ImageAttributes())
                {
                    g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.HighQuality; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality;
                    at.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, at);
                }
                var enc = jpegCodec ?? (jpegCodec = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg"));
                using (var ps = new EncoderParameters(1))
                using (var ms = new MemoryStream())
                {
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                    bmp.Save(ms, enc, ps); return ms.ToArray();
                }
            }
        }

        static bool WebpSize(byte[] d, out int w, out int h)
        {
            w = h = 0; if (d.Length < 30) return false;
            string kind = System.Text.Encoding.ASCII.GetString(d, 12, 4);
            if (kind == "VP8X") { w = 1 + (d[24] | d[25] << 8 | d[26] << 16); h = 1 + (d[27] | d[28] << 8 | d[29] << 16); }
            else if (kind == "VP8 ") { w = (d[26] | d[27] << 8) & 0x3FFF; h = (d[28] | d[29] << 8) & 0x3FFF; }
            else if (kind == "VP8L") { w = 1 + (d[21] | (d[22] & 0x3F) << 8); h = 1 + ((d[22] >> 6) | d[23] << 2 | (d[24] & 0x0F) << 10); }
            return w > 0 && h > 0;
        }

        // the picture kept as the store's own: the row in ShopImages (a product's when productId is given)
        public static Dictionary<string, object> Keep(SqlConnection c, int storeId, int? productId, Pic p, string alt)
        {
            string mime = p.Ext == "webp" ? "image/webp" : "image/jpeg";
            var r = ShopImages.Save(c, storeId, productId, "data:" + mime + ";base64," + Convert.ToBase64String(p.Big), p.Small == null ? null : "data:" + mime + ";base64," + Convert.ToBase64String(p.Small), p.W, p.H, alt);
            if (productId.HasValue) using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopImages SET Used=1 WHERE StoreId=@s AND Path=@p", "@s", storeId, "@p", (string)r["path"])) cmd.ExecuteNonQuery();
            return r;
        }
    }
}
