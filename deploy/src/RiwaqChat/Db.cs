// Database access (SQL Server). Tables are created automatically on first use.
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;

namespace RiwaqChat
{
    public class StoreInfo
    {
        public int Id;
        public string Slug, Name, OwnerEmail, PasswordHash, Status, BotName, Welcome, Instructions, AllowedOrigins, BubbleStyle, BrandColor, ChatTitle;
        public bool IsAdmin;
        public int MonthlyTokenLimit, DataVersion;
        public DateTime CreatedAt;
    }

    public class UsageInfo
    {
        public int Messages;
        public long InputTokens, CacheReadTokens, CacheWriteTokens, OutputTokens;
        // Cost-weighted tokens (Haiku price ratios): cache writes cost 1.25x, cache reads 0.1x, output 5x an input token.
        public long Weighted { get { return InputTokens + (long)(CacheWriteTokens * 1.25) + (long)(CacheReadTokens * 0.1) + OutputTokens * 5; } }
    }

    // Database space: a configured limit (DbCapacityMB, from the hosting control panel) plus detection of "database full" errors.
    public static class Storage
    {
        public const string FullMessage = "⚠️ مساحة التخزين على السيرفر خلصت، فمش هنقدر نحفظ داتا جديدة دلوقتي. امسح ملفات قديمة مش محتاجها، أو كلّم إدارة الموقع عشان يزوّدوا المساحة.";
        public const string NearlyFullMessage = "⚠️ مساحة التخزين على السيرفر مش هتكفي الملف ده. جرّب ملف أصغر، أو امسح ملفات قديمة، أو كلّم إدارة الموقع.";
        static double cachedUsed = -1;
        static DateTime cachedAt = DateTime.MinValue;

        public static long CapacityMB { get { long v; return long.TryParse(Web.Setting("DbCapacityMB", "0"), out v) ? v : 0; } }

        // Space used by the data in this database, in MB (cached for a minute).
        public static double UsedMB(SqlConnection c, bool fresh = false)
        {
            if (!fresh && cachedUsed >= 0 && DateTime.UtcNow - cachedAt < TimeSpan.FromMinutes(1)) return cachedUsed;
            double mb;
            try
            {
                using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CAST(FILEPROPERTY(name,'SpaceUsed') AS BIGINT)),0) * 8.0 / 1024 FROM sys.database_files WHERE type = 0"))
                    mb = Convert.ToDouble(cmd.ExecuteScalar());
            }
            catch
            {
                using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CAST(DATALENGTH(Content) AS BIGINT)),0) / 1048576.0 FROM dbo.KnowledgeItems"))
                    mb = Convert.ToDouble(cmd.ExecuteScalar());
            }
            cachedUsed = mb; cachedAt = DateTime.UtcNow;
            return mb;
        }

        public static int Percent(SqlConnection c)
        {
            var cap = CapacityMB;
            return cap <= 0 ? -1 : (int)Math.Min(100, Math.Round(UsedMB(c) * 100 / cap));
        }

        // null = there is room; otherwise a message for the user. Arabic text is stored as 2 bytes per character (+ index overhead).
        public static string CheckRoom(SqlConnection c, long newChars)
        {
            var cap = CapacityMB;
            if (cap <= 0) return null;
            double used = UsedMB(c, fresh: true);
            double adding = newChars * 2.2 / 1048576.0;
            if (used >= cap * 0.98) return FullMessage;
            if (used + adding > cap * 0.98) return NearlyFullMessage;
            return null;
        }

        // SQL Server errors that mean "no more space": filegroup full, log full, size quota, disk full.
        public static bool IsFull(Exception ex)
        {
            var sql = ex as SqlException;
            if (sql == null) return false;
            foreach (SqlError e in sql.Errors)
            {
                if (e.Number == 1101 || e.Number == 1105 || e.Number == 9002 || e.Number == 1827 || e.Number == 5149 || e.Number == 1133) return true;
                var m = (e.Message ?? "").ToLowerInvariant();
                if (m.Contains("quota") || m.Contains("disk is full") || m.Contains("not enough space")) return true;
            }
            return false;
        }
    }

    public static class Db
    {
        static readonly object Lock = new object();
        static bool ensured;

        static string Cs
        {
            get { var c = ConfigurationManager.ConnectionStrings["ChatDb"]; return c == null ? null : c.ConnectionString; }
        }

        public static bool Configured { get { var cs = Cs; return !string.IsNullOrEmpty(cs) && !cs.Contains("PUT-"); } }

        public static SqlConnection Open()
        {
            var c = new SqlConnection(Cs);
            c.Open();
            if (!ensured)
            {
                lock (Lock)
                {
                    if (!ensured) { using (var cmd = new SqlCommand(Schema, c)) cmd.ExecuteNonQuery(); ensured = true; }
                }
            }
            return c;
        }

        public static SqlCommand Cmd(SqlConnection c, string sql, params object[] nameValuePairs)
        {
            var cmd = new SqlCommand(sql, c);
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
                cmd.Parameters.AddWithValue((string)nameValuePairs[i], nameValuePairs[i + 1] ?? DBNull.Value);
            return cmd;
        }

        public static string Month { get { return DateTime.UtcNow.ToString("yyyy-MM"); } }

        public static StoreInfo StoreBySlug(SqlConnection c, string slug) { return OneStore(c, "WHERE Slug=@v", slug); }
        public static StoreInfo StoreById(SqlConnection c, int id) { return OneStore(c, "WHERE Id=@v", id); }
        public static StoreInfo StoreByEmail(SqlConnection c, string email) { return OneStore(c, "WHERE OwnerEmail=@v", email); }

        static StoreInfo OneStore(SqlConnection c, string where, object v)
        {
            using (var cmd = Cmd(c, "SELECT TOP 1 * FROM dbo.Stores " + where, "@v", v))
            using (var r = cmd.ExecuteReader())
                return r.Read() ? ReadStore(r) : null;
        }

        public static List<StoreInfo> AllStores(SqlConnection c)
        {
            var list = new List<StoreInfo>();
            using (var cmd = Cmd(c, "SELECT * FROM dbo.Stores ORDER BY CreatedAt"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(ReadStore(r));
            return list;
        }

        static StoreInfo ReadStore(IDataRecord r)
        {
            return new StoreInfo
            {
                Id = (int)r["Id"],
                Slug = (string)r["Slug"],
                Name = (string)r["Name"],
                OwnerEmail = (string)r["OwnerEmail"],
                PasswordHash = (string)r["PasswordHash"],
                IsAdmin = (bool)r["IsAdmin"],
                Status = (string)r["Status"],
                BotName = (string)r["BotName"],
                Welcome = r["Welcome"] as string,
                Instructions = r["Instructions"] as string,
                AllowedOrigins = r["AllowedOrigins"] as string,
                BubbleStyle = r["BubbleStyle"] as string ?? "bear",
                BrandColor = r["BrandColor"] as string ?? "#e8774a",
                ChatTitle = r["ChatTitle"] as string,
                MonthlyTokenLimit = (int)r["MonthlyTokenLimit"],
                DataVersion = (int)r["DataVersion"],
                CreatedAt = (DateTime)r["CreatedAt"],
            };
        }

        public static UsageInfo Usage(SqlConnection c, int storeId, string month)
        {
            using (var cmd = Cmd(c, "SELECT * FROM dbo.UsageMonthly WHERE StoreId=@s AND Month=@m", "@s", storeId, "@m", month))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return new UsageInfo();
                return new UsageInfo
                {
                    Messages = (int)r["Messages"],
                    InputTokens = (long)r["InputTokens"],
                    CacheReadTokens = (long)r["CacheReadTokens"],
                    CacheWriteTokens = (long)r["CacheWriteTokens"],
                    OutputTokens = (long)r["OutputTokens"],
                };
            }
        }

        public static void AddUsage(SqlConnection c, int storeId, int messages, long input, long cacheRead, long cacheWrite, long output)
        {
            const string sql = @"
MERGE dbo.UsageMonthly AS t
USING (SELECT @s AS StoreId, @m AS Month) AS src ON t.StoreId = src.StoreId AND t.Month = src.Month
WHEN MATCHED THEN UPDATE SET Messages = Messages + @n, InputTokens = InputTokens + @i, CacheReadTokens = CacheReadTokens + @cr,
     CacheWriteTokens = CacheWriteTokens + @cw, OutputTokens = OutputTokens + @o
WHEN NOT MATCHED THEN INSERT (StoreId, Month, Messages, InputTokens, CacheReadTokens, CacheWriteTokens, OutputTokens)
     VALUES (@s, @m, @n, @i, @cr, @cw, @o);";
            using (var cmd = Cmd(c, sql, "@s", storeId, "@m", Month, "@n", messages, "@i", input, "@cr", cacheRead, "@cw", cacheWrite, "@o", output))
                cmd.ExecuteNonQuery();
        }

        public static void BumpVersion(SqlConnection c, int storeId)
        {
            using (var cmd = Cmd(c, "UPDATE dbo.Stores SET DataVersion = DataVersion + 1 WHERE Id=@s", "@s", storeId)) cmd.ExecuteNonQuery();
        }

        const string Schema = @"
IF OBJECT_ID('dbo.Stores') IS NULL
BEGIN
  CREATE TABLE dbo.Stores(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Slug NVARCHAR(40) NOT NULL UNIQUE,
    Name NVARCHAR(100) NOT NULL,
    OwnerEmail NVARCHAR(200) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(300) NOT NULL,
    IsAdmin BIT NOT NULL DEFAULT 0,
    Status NVARCHAR(20) NOT NULL DEFAULT 'active',
    BotName NVARCHAR(50) NOT NULL DEFAULT N'دبدوب',
    Welcome NVARCHAR(600) NULL,
    Instructions NVARCHAR(MAX) NULL,
    AllowedOrigins NVARCHAR(1000) NULL,
    MonthlyTokenLimit INT NOT NULL DEFAULT 300000,
    DataVersion INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
END
IF OBJECT_ID('dbo.KnowledgeItems') IS NULL
BEGIN
  CREATE TABLE dbo.KnowledgeItems(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Title NVARCHAR(200) NOT NULL,
    SourceType NVARCHAR(10) NOT NULL,
    FileName NVARCHAR(260) NULL,
    Content NVARCHAR(MAX) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_KnowledgeItems_Store ON dbo.KnowledgeItems(StoreId);
END
IF OBJECT_ID('dbo.UsageMonthly') IS NULL
BEGIN
  CREATE TABLE dbo.UsageMonthly(
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Month CHAR(7) NOT NULL,
    Messages INT NOT NULL DEFAULT 0,
    InputTokens BIGINT NOT NULL DEFAULT 0,
    CacheReadTokens BIGINT NOT NULL DEFAULT 0,
    CacheWriteTokens BIGINT NOT NULL DEFAULT 0,
    OutputTokens BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY(StoreId, Month));
END
IF COL_LENGTH('dbo.Stores', 'BubbleStyle') IS NULL ALTER TABLE dbo.Stores ADD BubbleStyle NVARCHAR(20) NOT NULL CONSTRAINT DF_Stores_BubbleStyle DEFAULT 'bear';
IF COL_LENGTH('dbo.Stores', 'BrandColor') IS NULL ALTER TABLE dbo.Stores ADD BrandColor NVARCHAR(9) NOT NULL CONSTRAINT DF_Stores_BrandColor DEFAULT '#e8774a';
IF COL_LENGTH('dbo.Stores', 'ChatTitle') IS NULL ALTER TABLE dbo.Stores ADD ChatTitle NVARCHAR(80) NULL;";
    }
}
