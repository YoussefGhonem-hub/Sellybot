// Search over a store's data for big stores (search mode).
// Data parts are split into small chunks; a query is matched with BM25 on normalized Arabic/English words.
// File/part titles count three times, so clear, descriptive names make search better.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RiwaqChat
{
    public class Chunk
    {
        public string Title, Date, Text;
        public Dictionary<string, int> Tf;
        public int Length;
    }

    public class StoreIndex
    {
        public int Version;
        public long TotalChars;
        public List<Chunk> Chunks = new List<Chunk>();
        public List<string> Contents = new List<string>();      // "title (date, N chars)" per part, for the prompt's table of contents
        public Dictionary<string, int> Df = new Dictionary<string, int>();
        public double AvgLen;
        public string FullText;                                  // used in full mode
    }

    public static class Search
    {
        const int ChunkChars = 1200;
        static readonly ConcurrentDictionary<int, StoreIndex> Cache = new ConcurrentDictionary<int, StoreIndex>();

        public static StoreIndex For(SqlConnection c, StoreInfo s)
        {
            StoreIndex hit;
            if (Cache.TryGetValue(s.Id, out hit) && hit.Version == s.DataVersion) return hit;

            var idx = new StoreIndex { Version = s.DataVersion };
            var full = new StringBuilder();
            using (var cmd = Db.Cmd(c, "SELECT Title, Content, UpdatedAt FROM dbo.KnowledgeItems WHERE StoreId=@s ORDER BY Id", "@s", s.Id))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string title = (string)r["Title"], content = ((string)r["Content"]).Trim();
                    string date = ((DateTime)r["UpdatedAt"]).ToString("yyyy-MM-dd");
                    idx.TotalChars += content.Length;
                    idx.Contents.Add(title + " (آخر تحديث " + date + "، " + content.Length.ToString("N0") + " حرف)");
                    full.Append("\n\n### ").Append(title).Append(" (آخر تحديث ").Append(date).Append(")\n").Append(content);
                    foreach (var piece in Split(content))
                        idx.Chunks.Add(new Chunk { Title = title, Date = date, Text = piece });
                }
            }
            idx.FullText = full.ToString();

            long totalLen = 0;
            foreach (var ch in idx.Chunks)
            {
                var words = Words(ch.Title).Concat(Words(ch.Title)).Concat(Words(ch.Title)).Concat(Words(ch.Text)).ToList();
                ch.Tf = new Dictionary<string, int>();
                foreach (var w in words) { int n; ch.Tf[w] = ch.Tf.TryGetValue(w, out n) ? n + 1 : 1; }
                ch.Length = words.Count;
                totalLen += ch.Length;
                foreach (var w in ch.Tf.Keys) { int n; idx.Df[w] = idx.Df.TryGetValue(w, out n) ? n + 1 : 1; }
            }
            idx.AvgLen = idx.Chunks.Count == 0 ? 1 : (double)totalLen / idx.Chunks.Count;
            Cache[s.Id] = idx;
            return idx;
        }

        // Best-matching chunks for a query, as text for Claude (title + date on every result).
        public static string Run(StoreIndex idx, string query, int maxChars = 12000, int maxResults = 12)
        {
            var q = Words(query).Distinct().ToList();
            if (q.Count == 0 || idx.Chunks.Count == 0) return "مفيش نتايج.";
            const double k1 = 1.2, b = 0.75;
            int N = idx.Chunks.Count;
            var scored = new List<KeyValuePair<double, Chunk>>();
            foreach (var ch in idx.Chunks)
            {
                double score = 0;
                foreach (var w in q)
                {
                    int tf;
                    if (!ch.Tf.TryGetValue(w, out tf)) continue;
                    int df = idx.Df[w];
                    double idf = Math.Log(1 + (N - df + 0.5) / (df + 0.5));
                    score += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * ch.Length / idx.AvgLen));
                }
                if (score > 0) scored.Add(new KeyValuePair<double, Chunk>(score, ch));
            }
            if (scored.Count == 0) return "مفيش نتايج للبحث ده. جرّب كلمات تانية (مرادفات، فصحى، إنجليزي، اسم المنتج بس).";

            var sb = new StringBuilder();
            int n = 0;
            foreach (var kv in scored.OrderByDescending(x => x.Key))
            {
                var block = "### " + kv.Value.Title + " (آخر تحديث " + kv.Value.Date + ")\n" + kv.Value.Text + "\n\n";
                if (sb.Length + block.Length > maxChars && n > 0) break;
                sb.Append(block);
                if (++n >= maxResults) break;
            }
            return sb.ToString().Trim();
        }

        // ---------- chunking: keep lines together (a product row stays whole) ----------
        static IEnumerable<string> Split(string text)
        {
            var sb = new StringBuilder();
            foreach (var rawLine in text.Replace("\r", "").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.Length > ChunkChars)
                {
                    if (sb.Length > 0) { yield return sb.ToString().Trim(); sb.Clear(); }
                    for (int i = 0; i < line.Length; i += ChunkChars) yield return line.Substring(i, Math.Min(ChunkChars, line.Length - i));
                    continue;
                }
                if (sb.Length + line.Length + 1 > ChunkChars && sb.Length > 0) { yield return sb.ToString().Trim(); sb.Clear(); }
                sb.Append(line).Append('\n');
            }
            if (sb.ToString().Trim().Length > 0) yield return sb.ToString().Trim();
        }

        // ---------- normalization + light Arabic stemming ----------
        static readonly Regex Diacritics = new Regex("[ؐ-ًؚ-ٰٟۖ-ۭـ]");
        static readonly Regex NonWord = new Regex(@"[^\p{L}\p{Nd}]+");
        static readonly string[] Prefixes = { "وبال", "وكال", "وال", "بال", "كال", "فال", "لل", "ال" };
        static readonly string[] Suffixes = { "ات", "ون", "ين", "ان", "ها", "هم", "كم", "نا" };
        static readonly HashSet<string> Stop = new HashSet<string>
        {
            "في", "من", "علي", "الي", "عن", "مع", "هل", "او", "ان", "ده", "دي", "دا", "اللي", "ايه", "انا", "انت", "عايز", "عاوز",
            "هو", "هي", "كان", "بس", "عند", "عندكم", "ممكن", "لو", "يا", "the", "and", "of", "to", "is", "a", "an", "for", "in", "on"
        };

        public static IEnumerable<string> Words(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            var t = Diacritics.Replace(text, "").ToLowerInvariant();
            var sb = new StringBuilder(t.Length);
            foreach (var ch in t)
            {
                switch (ch)
                {
                    case 'أ': case 'إ': case 'آ': case 'ٱ': sb.Append('ا'); break;
                    case 'ى': case 'ئ': sb.Append('ي'); break;
                    case 'ة': sb.Append('ه'); break;
                    case 'ؤ': sb.Append('و'); break;
                    default:
                        if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
                        else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
                        else sb.Append(ch);
                        break;
                }
            }
            foreach (var raw in NonWord.Split(sb.ToString()))
            {
                if (raw.Length < 2 || Stop.Contains(raw)) continue;
                var w = raw;
                if (IsArabic(w))
                {
                    foreach (var p in Prefixes) if (w.StartsWith(p) && w.Length - p.Length >= 2) { w = w.Substring(p.Length); break; }
                    foreach (var s in Suffixes) if (w.EndsWith(s) && w.Length - s.Length >= 3) { w = w.Substring(0, w.Length - s.Length); break; }
                    if (w.Length > 3 && w.EndsWith("ه")) w = w.Substring(0, w.Length - 1);
                }
                if (w.Length >= 2 && !Stop.Contains(w)) yield return w;
            }
        }

        static bool IsArabic(string w) { foreach (var ch in w) if (ch >= '؀' && ch <= 'ۿ') return true; return false; }
    }
}
