// The stores' pictures. The dashboard shrinks a picture before it is sent (a big one and a small one); PNG, JPEG or WebP only, read from
// the bytes themselves, never SVG. A picture's path is <store id>/<random>.webp (the small one: <random>-s.webp) and never changes, so
// browsers and Cloudflare keep it for a year. A short video (MP4 or WebM, 6 MB) is kept the same way, in one size, and is given in parts
// (a phone asks for a piece of it at a time). Kept on Cloudflare R2 through the Worker (AppSettings ShopImgMode = r2), and on this
// server's disk (App_Data/shopimg) until R2 is on, or when it doesn't answer; pictures left on the disk move to R2 by themselves.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class ShopImages
    {
        public const int MaxBig = 1200 * 1024, MaxSmall = 260 * 1024, MaxVideo = 6 * 1024 * 1024;
        public const long MaxStoreBytes = 250L * 1024 * 1024;
        static readonly Regex PathRx = new Regex(@"^\d+/[a-z0-9]{8,40}(-s)?\.(webp|jpg|png|mp4|webm)$");
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        public static string Url(string path, bool small = false)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var b = Web.Setting("ShopImgUrl") ?? (Shops.Scheme + "://img." + Shops.Domain);
            return b + "/" + (small ? Small(path) : path);
        }
        public static string Small(string path) { int i = path.LastIndexOf('.'); return i < 0 ? path : path.Substring(0, i) + "-s" + path.Substring(i); }
        static string Dir { get { return Web.Setting("ShopImgDir") ?? HostingEnvironment.MapPath("~/App_Data/shopimg"); } }
        static string File_(string path) { return Path.Combine(Dir, path.Replace('/', Path.DirectorySeparatorChar)); }

        public static string Sniff(byte[] d)
        {
            if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "png";
            if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpg";
            if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "webp";
            return null;
        }
        public static bool IsVideo(string path) { return path != null && (path.EndsWith(".mp4") || path.EndsWith(".webm")); }
        public static string Type(string path) { return path.EndsWith(".mp4") ? "video/mp4" : path.EndsWith(".webm") ? "video/webm" : path.EndsWith(".png") ? "image/png" : path.EndsWith(".jpg") ? "image/jpeg" : "image/webp"; }
        static byte[] Decode(string dataUrl, int max)
        {
            if (string.IsNullOrEmpty(dataUrl)) return null;
            int comma = dataUrl.IndexOf(',');
            var b64 = dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 ? dataUrl.Substring(comma + 1) : dataUrl;
            if (b64.Length > max * 4L / 3 + 16) throw new ShopException("too_big", "الصورة كبيرة. جرّب صورة أصغر.");
            try { return Convert.FromBase64String(b64); } catch (FormatException) { return null; }
        }

        // saves the two sizes and the row; productId null = a picture of the store itself (logo, cover, category, banner)
        public static Dictionary<string, object> Save(SqlConnection c, int storeId, int? productId, string big, string small, int w, int h, string alt)
        {
            var b = Decode(big, MaxBig); var s = Decode(small, MaxSmall);
            string ext = b == null ? null : Sniff(b), ext2 = s == null ? null : Sniff(s);
            if (ext == null || (s != null && ext2 != ext)) throw new ShopException("bad_image", "الصورة دي مش مدعومة. ارفعها JPG أو PNG أو WebP.");
            long used; int n;
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CAST(Bytes AS BIGINT)),0), COUNT(*) FROM dbo.ShopImages WHERE StoreId=@s", "@s", storeId))
            using (var r = cmd.ExecuteReader()) { r.Read(); used = Convert.ToInt64(r[0]); n = (int)r[1]; }
            if (used + b.Length + (s == null ? 0 : s.Length) > MaxStoreBytes) throw new ShopException("full", "مساحة صور متجرك خلصت. امسح صور مش محتاجها، أو كلّم الإدارة.");
            if (productId.HasValue)
                using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopImages WHERE StoreId=@s AND ProductId=@p", "@s", storeId, "@p", productId.Value))
                    if ((int)cmd.ExecuteScalar() >= Shops.MaxImages) throw new ShopException("max_images", "أقصى عدد للمنتج " + Shops.MaxImages + " صور.");
            string path = storeId + "/" + Shops.Token(10) + "." + ext;
            string place = Put(c, path, b, s ?? b);
            using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.ShopImages(StoreId, ProductId, Path, W, H, Bytes, Alt, Sort, Place) OUTPUT INSERTED.Id VALUES(@s, @p, @path, @w, @h, @b, @alt,
                    (SELECT ISNULL(MAX(Sort),0)+1 FROM dbo.ShopImages WHERE StoreId=@s AND ProductId=@p), @pl)", "@s", storeId, "@p", productId.HasValue ? (object)productId.Value : DBNull.Value, "@path", path,
                "@w", Math.Max(1, Math.Min(5000, w)), "@h", Math.Max(1, Math.Min(5000, h)), "@b", b.Length + (s == null ? 0 : s.Length), "@alt", string.IsNullOrWhiteSpace(alt) ? null : alt, "@pl", place))
            {
                int id = (int)cmd.ExecuteScalar();
                return new Dictionary<string, object> { { "id", id }, { "path", path }, { "url", Url(path) }, { "small", Url(path, true) }, { "w", w }, { "h", h }, { "alt", alt ?? "" } };
            }
        }

        // the owner's short video: read from the bytes themselves (MP4: 'ftyp', WebM: EBML)
        public static string SaveVideo(SqlConnection c, int storeId, byte[] b)
        {
            if (b == null || b.Length < 2000 || b.Length > MaxVideo) throw new ShopException("big", "الفيديو لازم يكون لحد 6 ميجا. قصّره أو صغّره وارفعه تاني.");
            string ext = b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p' ? "mp4" : b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3 ? "webm" : null; if (ext == null) throw new ShopException("type", "ارفع فيديو MP4 أو WebM.");
            long used; using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CAST(Bytes AS BIGINT)),0) FROM dbo.ShopImages WHERE StoreId=@s", "@s", storeId)) used = Convert.ToInt64(cmd.ExecuteScalar()); if (used + b.Length > MaxStoreBytes) throw new ShopException("full", "مساحة متجرك خلصت. امسح صور أو فيديوهات مش محتاجها.");
            string path = storeId + "/" + Shops.Token(10) + "." + ext, place = Put(c, path, b, null);
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopImages(StoreId, Path, W, H, Bytes, Alt, Used, Place) VALUES(@s, @p, 16, 9, @b, 'video', 1, @pl)", "@s", storeId, "@p", path, "@b", b.Length, "@pl", place)) cmd.ExecuteNonQuery();
            return path;
        }

        static string Put(SqlConnection c, string path, byte[] big, byte[] small)
        {
            if (Billing.Get(c, "ShopImgMode") == "r2")
            {
                try { if (R2(c, "PUT", path, big) && (small == null || R2(c, "PUT", Small(path), small))) return "r2"; }
                catch (Exception ex) { Log.Error(ex); }
            }
            Write(path, big); if (small != null) Write(Small(path), small);
            return "disk";
        }
        static void Write(string path, byte[] data)
        {
            var f = File_(path); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllBytes(f, data);
        }

        // PUT / DELETE img.<domain>/_put/<path> on the Worker, with the secret; it writes to the R2 bucket bound to it
        public static bool R2(SqlConnection c, string method, string path, byte[] data)
        {
            var key = Shops.Key(c); if (key == null) return false;
            var baseUrl = Web.Setting("ShopImgPut") ?? Url("x").Substring(0, Url("x").Length - 1);
            using (var m = new HttpRequestMessage(new HttpMethod(method), baseUrl + "_put/" + path))
            {
                m.Headers.TryAddWithoutValidation("X-Shop-Key", key);
                if (data != null) { m.Content = new ByteArrayContent(data); m.Content.Headers.TryAddWithoutValidation("Content-Type", Type(path)); }
                using (var r = Http.SendAsync(m).GetAwaiter().GetResult()) return r.IsSuccessStatusCode;
            }
        }

        public static byte[] Read(string path, bool small) { if (path == null || !PathRx.IsMatch(path)) return null; var f = File_(small ? Small(path) : path); return File.Exists(f) ? File.ReadAllBytes(f) : null; }
        public static async Task<byte[]> Fetch(string url) { using (var r = await Http.GetAsync(url)) return r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync() : null; }
        // the site's own copy (the Worker asks for it when R2 doesn't have the picture)
        public static void Serve(string path, HttpResponse res, bool head)
        {
            if (path == null || !PathRx.IsMatch(path)) { res.StatusCode = 404; return; }
            var f = File_(path);
            if (!File.Exists(f)) { res.StatusCode = 404; res.Cache.SetCacheability(HttpCacheability.NoCache); return; }
            res.ContentType = Type(path);
            res.AddHeader("Content-Security-Policy", "default-src 'none'");
            res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.FromDays(365)); res.Cache.AppendCacheExtension("immutable");
            if (IsVideo(path))
            {
                // one piece of the file, when that is what is asked for
                res.AddHeader("Accept-Ranges", "bytes"); long size = new FileInfo(f).Length, a = 0, z = size - 1; var rg = Regex.Match(HttpContext.Current.Request.Headers["Range"] ?? "", @"^bytes=(\d*)-(\d*)$");
                if (rg.Success && (rg.Groups[1].Length > 0 || rg.Groups[2].Length > 0))
                {
                    if (rg.Groups[1].Length == 0) { long last; long.TryParse(rg.Groups[2].Value, out last); a = Math.Max(0, size - last); }
                    else { long.TryParse(rg.Groups[1].Value, out a); if (rg.Groups[2].Length > 0) long.TryParse(rg.Groups[2].Value, out z); z = Math.Min(z, size - 1); }
                    if (a > z || a >= size) { res.StatusCode = 416; res.AddHeader("Content-Range", "bytes */" + size); return; }
                    res.StatusCode = 206; res.AddHeader("Content-Range", "bytes " + a + "-" + z + "/" + size);
                }
                res.AddHeader("Content-Length", (z - a + 1).ToString()); if (!head) res.TransmitFile(f, a, z - a + 1); return;
            }
            if (!head) res.TransmitFile(f);
        }

        public static void Delete(SqlConnection c, int storeId, IEnumerable<string> paths)
        {
            foreach (var p in paths.Where(x => !string.IsNullOrEmpty(x) && x.StartsWith(storeId + "/") && PathRx.IsMatch(x)).Distinct().ToList())
            {
                string place = null;
                using (var cmd = Db.Cmd(c, "DELETE FROM dbo.ShopImages OUTPUT DELETED.Place WHERE StoreId=@s AND Path=@p", "@s", storeId, "@p", p)) place = cmd.ExecuteScalar() as string;
                if (place == null) continue;
                try
                {
                    foreach (var x in new[] { p, Small(p) })
                        if (x != p && IsVideo(p)) continue; else if (place == "r2") R2(c, "DELETE", x, null); else { var f = File_(x); if (File.Exists(f)) File.Delete(f); }
                }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        // pictures on the disk move to R2 (a few per run), and uploads that were never used are removed after two days
        public static async Task Tidy(SqlConnection c, CancellationToken ct)
        {
            if (Billing.Get(c, "ShopImgMode") == "r2")
            {
                var list = new List<string>();
                using (var cmd = Db.Cmd(c, "SELECT TOP 30 Path FROM dbo.ShopImages WHERE Place='disk' ORDER BY Id")) using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add((string)r[0]);
                foreach (var p in list)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var f = File_(p); var s = File_(Small(p));
                        if (!File.Exists(f)) { using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopImages SET Place='lost' WHERE Path=@p", "@p", p)) cmd.ExecuteNonQuery(); continue; }
                        if (!R2(c, "PUT", p, File.ReadAllBytes(f)) || (!IsVideo(p) && !R2(c, "PUT", Small(p), File.ReadAllBytes(File.Exists(s) ? s : f)))) return;
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopImages SET Place='r2' WHERE Path=@p", "@p", p)) cmd.ExecuteNonQuery();
                        File.Delete(f); if (File.Exists(s)) File.Delete(s);
                    }
                    catch (Exception ex) { Log.Error(ex); return; }
                }
            }
            await Task.CompletedTask;
        }
    }
}
