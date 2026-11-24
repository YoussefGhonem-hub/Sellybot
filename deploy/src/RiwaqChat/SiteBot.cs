// Sellybot's own chat on sellybot.online (store "sellybot"): a built-in store whose persona and data live in the repo,
// under site/App_Data/sitebot/ — settings.json (name, bot name, welcome, chat title, suggested questions, instructions)
// and one .md file per data part (its first line "# ..." is the part's title).
// At app start the store is created if it is missing, and whenever those files changed since the last sync the persona
// and the parts are rewritten. Parts added by hand (the super admin can sign in as the store) are left alone, and
// settings changed by hand last until the files change again.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class SiteBot
    {
        public const string Slug = "sellybot";
        public const string Style = "sellybot";        // the logo bubble in widget.js, only for this store
        const string Email = "sitebot@sellybot.online";  // nobody signs in with it (random password)
        const string Marker = "sitebot/";                // FileName prefix of the parts this sync owns
        const string HashKey = "SiteBotHash";
        const int MonthlyLimit = 100000000;              // weighted tokens a month; the super admin can change it

        public static void Ensure(SqlConnection c)
        {
            try { Sync(c, HostingEnvironment.MapPath("~/App_Data/sitebot")); }
            catch (Exception ex) { Log.Error(ex); }   // never stop the site over this
        }

        class Part { public string FileName, Title, Content; }

        public static void Sync(SqlConnection c, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string settingsPath = Path.Combine(dir, "settings.json");
            if (!File.Exists(settingsPath)) return;
            string settingsRaw = File.ReadAllText(settingsPath, Encoding.UTF8);

            var parts = new List<Part>();
            foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(x => x, StringComparer.Ordinal))
            {
                if (Path.GetFileName(f) == "20-council.md" && !Council.On(c)) continue;   // «مجلس المستشارين»: known once the admin opens it
                string text = File.ReadAllText(f, Encoding.UTF8).Replace("\r\n", "\n").Trim();
                if (text.Length == 0) continue;
                string first = text.Split('\n')[0].Trim().TrimStart('#').Trim();
                string title = first.Length > 0 ? first : Path.GetFileNameWithoutExtension(f);
                parts.Add(new Part { FileName = Marker + Path.GetFileName(f), Title = title.Length > 200 ? title.Substring(0, 200) : title, Content = text });
            }

            string hash;
            using (var sha = SHA256.Create())
            {
                var all = new StringBuilder(settingsRaw);
                foreach (var p in parts) all.Append('\0').Append(p.FileName).Append('\0').Append(p.Content);
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(all.ToString()))).Replace("-", "").ToLowerInvariant();
            }

            using (var tx = c.BeginTransaction())
            {
                // one sync at a time, also across the two app copies that briefly overlap after a deploy
                using (var cmd = new SqlCommand("EXEC sp_getapplock @Resource='sitebot', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000", c, tx)) cmd.ExecuteNonQuery();

                int id = 0; string owner = null;
                using (var cmd = new SqlCommand("SELECT Id, OwnerEmail FROM dbo.Stores WHERE Slug=@s", c, tx))
                {
                    cmd.Parameters.AddWithValue("@s", Slug);
                    using (var r = cmd.ExecuteReader()) if (r.Read()) { id = (int)r["Id"]; owner = r["OwnerEmail"] as string; }
                }
                if (id != 0 && !string.Equals(owner, Email, StringComparison.OrdinalIgnoreCase)) { tx.Commit(); return; }   // a real account owns the name: never touch it

                string last = null;
                using (var cmd = new SqlCommand("SELECT [Value] FROM dbo.AppSettings WHERE [Key]=@k", c, tx))
                {
                    cmd.Parameters.AddWithValue("@k", HashKey);
                    last = cmd.ExecuteScalar() as string;
                }
                if (id != 0 && last == hash) { tx.Commit(); return; }   // nothing changed since the last sync

                var st = JsonDocument.Parse(settingsRaw).RootElement;
                string name = Cut(Str(st, "name") ?? "Sellybot", 100), botName = Cut(Str(st, "botName") ?? "", 50);
                string welcome = Cut(Str(st, "welcome"), 600), title2 = Cut(Str(st, "chatTitle"), 80), instructions = Str(st, "instructions");
                var raw = new List<string>();
                JsonElement arr;
                if (st.TryGetProperty("suggestions", out arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var q in arr.EnumerateArray()) if (q.ValueKind == JsonValueKind.String) raw.Add(q.GetString());
                string suggestions = Cut(string.Join("\n", Claude.CleanSuggestions(string.Join("\n", raw))), 400);

                if (id == 0)
                {
                    byte[] rnd = new byte[32];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
                    using (var cmd = new SqlCommand(@"INSERT INTO dbo.Stores (Slug, Name, OwnerEmail, PasswordHash, IsAdmin, MonthlyTokenLimit, Welcome, BotName)
                                                      OUTPUT INSERTED.Id VALUES (@slug, @name, @email, @hash, 1, @limit, @welcome, @bot)", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@slug", Slug); cmd.Parameters.AddWithValue("@name", name); cmd.Parameters.AddWithValue("@email", Email);
                        cmd.Parameters.AddWithValue("@hash", Web.HashPassword(Convert.ToBase64String(rnd))); cmd.Parameters.AddWithValue("@limit", MonthlyLimit);
                        cmd.Parameters.AddWithValue("@welcome", (object)welcome ?? DBNull.Value); cmd.Parameters.AddWithValue("@bot", botName);
                        id = (int)cmd.ExecuteScalar();
                    }
                }

                using (var cmd = new SqlCommand(@"UPDATE dbo.Stores SET Name=@name, BotName=@bot, Welcome=@welcome, Instructions=@ins, ChatTitle=@title, Suggestions=@sg,
                                                    BubbleStyle=@style, BrandColor='#e8774a', Status='active', IsAdmin=1 WHERE Id=@id", c, tx))
                {
                    cmd.Parameters.AddWithValue("@name", name); cmd.Parameters.AddWithValue("@bot", botName);
                    cmd.Parameters.AddWithValue("@welcome", (object)welcome ?? DBNull.Value); cmd.Parameters.AddWithValue("@ins", (object)instructions ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@title", (object)title2 ?? DBNull.Value); cmd.Parameters.AddWithValue("@sg", suggestions.Length > 0 ? (object)suggestions : DBNull.Value);
                    cmd.Parameters.AddWithValue("@style", Style); cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }

                // the parts this sync owns: update the changed ones, add the new ones, remove the ones whose file is gone
                var have = new Dictionary<string, Tuple<int, string, string>>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqlCommand("SELECT Id, FileName, Title, Content FROM dbo.KnowledgeItems WHERE StoreId=@s AND FileName LIKE 'sitebot/%'", c, tx))
                {
                    cmd.Parameters.AddWithValue("@s", id);
                    using (var r = cmd.ExecuteReader()) while (r.Read()) have[(string)r["FileName"]] = Tuple.Create((int)r["Id"], (string)r["Title"], (string)r["Content"]);
                }
                foreach (var p in parts)
                {
                    Tuple<int, string, string> old;
                    if (have.TryGetValue(p.FileName, out old))
                    {
                        have.Remove(p.FileName);
                        if (old.Item2 == p.Title && old.Item3 == p.Content) continue;
                        using (var cmd = new SqlCommand("UPDATE dbo.KnowledgeItems SET Title=@t, Content=@c, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id", c, tx))
                        { cmd.Parameters.AddWithValue("@t", p.Title); cmd.Parameters.AddWithValue("@c", p.Content); cmd.Parameters.AddWithValue("@id", old.Item1); cmd.ExecuteNonQuery(); }
                    }
                    else
                        using (var cmd = new SqlCommand("INSERT INTO dbo.KnowledgeItems (StoreId, Title, SourceType, FileName, Content) VALUES (@s,@t,'md',@f,@c)", c, tx))
                        { cmd.Parameters.AddWithValue("@s", id); cmd.Parameters.AddWithValue("@t", p.Title); cmd.Parameters.AddWithValue("@f", p.FileName); cmd.Parameters.AddWithValue("@c", p.Content); cmd.ExecuteNonQuery(); }
                }
                foreach (var gone in have.Values)
                    using (var cmd = new SqlCommand("DELETE FROM dbo.KnowledgeItems WHERE Id=@id", c, tx)) { cmd.Parameters.AddWithValue("@id", gone.Item1); cmd.ExecuteNonQuery(); }

                using (var cmd = new SqlCommand("UPDATE dbo.Stores SET DataVersion = DataVersion + 1 WHERE Id=@s", c, tx)) { cmd.Parameters.AddWithValue("@s", id); cmd.ExecuteNonQuery(); }
                using (var cmd = new SqlCommand(@"MERGE dbo.AppSettings AS t USING (SELECT @k AS [Key]) AS x ON t.[Key] = x.[Key]
                                                  WHEN MATCHED THEN UPDATE SET [Value] = @v WHEN NOT MATCHED THEN INSERT ([Key], [Value]) VALUES (@k, @v);", c, tx))
                { cmd.Parameters.AddWithValue("@k", HashKey); cmd.Parameters.AddWithValue("@v", hash); cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
        }

        static string Str(JsonElement o, string k)
        {
            JsonElement v;
            if (!o.TryGetProperty(k, out v) || v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString().Replace("\r\n", "\n").Trim();
            return s.Length == 0 ? null : s;
        }
        static string Cut(string s, int max) { return s == null ? null : (s.Length > max ? s.Substring(0, max) : s); }
    }
}
