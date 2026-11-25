// Several label files in one PDF, so a merchant prints once: J&T gives one file per parcel, and a selection may be of two companies.
// Reads plain PDFs (a cross-reference table, no encryption), which is what label printers write; anything else comes out of Join as null
// and the files go down together in a zip instead.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RiwaqChat
{
    public static class CarrierPdf
    {
        static readonly Encoding L1 = Encoding.GetEncoding(28591);   // one char is one byte, both ways
        class Doc { public Dictionary<int, string> Objs = new Dictionary<int, string>(); public int Pages, Count; }

        public static byte[] Join(List<byte[]> files)
        {
            try
            {
                var docs = files.Select(Read).ToList(); if (docs.Any(d => d == null)) return null;
                var o = new StringBuilder("%PDF-1.4\n%âãÏÓ\n"); var at = new List<int> { 0, 0, 0 }; int next = 3; var kids = new List<int>(); int count = 0; var body = new StringBuilder();
                var parts = new List<KeyValuePair<int, string>>();
                foreach (var d in docs)
                {
                    var map = d.Objs.Keys.OrderBy(x => x).ToDictionary(x => x, x => next++); kids.Add(map[d.Pages]); count += d.Count;
                    foreach (var kv in d.Objs)
                    {
                        // references are renumbered in the dictionary only, never inside a stream's data
                        int cut = StreamAt(kv.Value); string head = cut < 0 ? kv.Value : kv.Value.Substring(0, cut), rest = cut < 0 ? "" : kv.Value.Substring(cut);
                        head = Regex.Replace(head, @"(?<![\d.])(\d{1,10})\s+(\d{1,5})\s+R(?![A-Za-z0-9])", m => { int n; return int.TryParse(m.Groups[1].Value, out n) && map.ContainsKey(n) ? map[n] + " 0 R" : "null"; });
                        if (kv.Key == d.Pages) { int p = head.IndexOf("<<", StringComparison.Ordinal); head = head.Substring(0, p + 2) + " /Parent 2 0 R " + head.Substring(p + 2); }
                        parts.Add(new KeyValuePair<int, string>(map[kv.Key], head + rest));
                    }
                }
                var all = new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(1, "<< /Type /Catalog /Pages 2 0 R >>"), new KeyValuePair<int, string>(2, "<< /Type /Pages /Count " + count + " /Kids [ " + string.Join(" ", kids.Select(k => k + " 0 R")) + " ] >>") }; all.AddRange(parts);
                var pos = new Dictionary<int, int>(); foreach (var kv in all) { pos[kv.Key] = o.Length; o.Append(kv.Key).Append(" 0 obj\n").Append(kv.Value.Trim('\r', '\n')).Append("\nendobj\n"); }
                int x = o.Length; o.Append("xref\n0 ").Append(next).Append("\n0000000000 65535 f \n"); for (int i = 1; i < next; i++) o.Append(pos[i].ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
                o.Append("trailer\n<< /Size ").Append(next).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n"); return L1.GetBytes(o.ToString());
            }
            catch (Exception ex) { Log.Error(ex); return null; }
        }
        static int StreamAt(string s) { var m = Regex.Match(s, @">>\s*stream\r?\n"); return m.Success ? m.Index + 2 : -1; }

        static Doc Read(byte[] file)
        {
            string s = L1.GetString(file); int sx = s.LastIndexOf("startxref", StringComparison.Ordinal); if (sx < 0) return null;
            var m = Regex.Match(s.Substring(sx), @"startxref\s+(\d+)"); if (!m.Success) return null; int at = int.Parse(m.Groups[1].Value); var off = new Dictionary<int, int>(); string root = null; var seen = new HashSet<int>();
            while (at >= 0)
            {
                if (at >= s.Length || !seen.Add(at)) return null; while (at < s.Length && char.IsWhiteSpace(s[at])) at++;
                if (string.CompareOrdinal(s, at, "xref", 0, 4) != 0) return null;   // a compressed table: not one of ours
                int tr = s.IndexOf("trailer", at, StringComparison.Ordinal); if (tr < 0) return null;
                var t = s.Substring(at + 4, tr - at - 4).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries); int i = 0;
                while (i + 1 < t.Length) { int first = int.Parse(t[i]), n = int.Parse(t[i + 1]); i += 2; for (int k = 0; k < n; k++, i += 3) if (t[i + 2] == "n" && !off.ContainsKey(first + k)) off[first + k] = int.Parse(t[i]); }
                int end = s.IndexOf("startxref", tr, StringComparison.Ordinal); string dict = s.Substring(tr, (end < 0 ? s.Length : end) - tr); if (dict.Contains("/Encrypt") || dict.Contains("/XRefStm")) return null;
                var r = Regex.Match(dict, @"/Root\s+(\d+)\s+\d+\s+R"); if (root == null && r.Success) root = r.Groups[1].Value; var pv = Regex.Match(dict, @"/Prev\s+(\d+)"); at = pv.Success ? int.Parse(pv.Groups[1].Value) : -1;
            }
            if (root == null) return null;
            var d = new Doc(); var ends = off.Values.Concat(seen).Concat(new[] { sx }).Distinct().OrderBy(x => x).ToList();
            foreach (var kv in off)
            {
                int stop = ends.FirstOrDefault(x => x > kv.Value); if (stop == 0) stop = s.Length; var h = Regex.Match(s.Substring(kv.Value, Math.Min(40, s.Length - kv.Value)), @"^\s*(\d+)\s+\d+\s+obj"); if (!h.Success || int.Parse(h.Groups[1].Value) != kv.Key) return null;
                int from = kv.Value + h.Length; string body = s.Substring(from, stop - from); int e = body.LastIndexOf("endobj", StringComparison.Ordinal); if (e < 0) return null; d.Objs[kv.Key] = body.Substring(0, e); if (body.Contains("/ObjStm")) return null;
            }
            string cat; if (!d.Objs.TryGetValue(int.Parse(root), out cat)) return null; var pg = Regex.Match(cat, @"/Pages\s+(\d+)\s+\d+\s+R"); if (!pg.Success || !d.Objs.ContainsKey(d.Pages = int.Parse(pg.Groups[1].Value))) return null;
            var ct = Regex.Match(d.Objs[d.Pages], @"/Count\s+(\d+)"); if (!ct.Success || d.Objs[d.Pages].Contains("/Parent")) return null; d.Count = int.Parse(ct.Groups[1].Value); d.Objs.Remove(int.Parse(root));
            return d;
        }

        public static byte[] Zip(List<byte[]> files)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) for (int i = 0; i < files.Count; i++) using (var e = z.CreateEntry("labels-" + (i + 1) + ".pdf").Open()) e.Write(files[i], 0, files[i].Length);
                return ms.ToArray();
            }
        }
    }
}
