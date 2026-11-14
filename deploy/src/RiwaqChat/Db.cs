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
        public string Slug, Name, OwnerEmail, PasswordHash, Status, BotName, Welcome, Instructions, AllowedOrigins, BubbleStyle, BrandColor, ChatTitle, Suggestions, ShopifyDomain, ShopifyStatus;
        public DateTime? ShopifySyncedAt;
        // full Shopify connection (Admin API): shop = x.myshopify.com, secret is encrypted (Secrets.Protect)
        public string ShopifyShop, ShopifyClientId, ShopifySecret, ShopifyScopes;
        public bool ShopifyOrders;
        public int ShopifyWebhooks;
        // WhatsApp through Wapilot: instance = Wapilot's instance_uniquename, token is encrypted (Secrets.Protect),
        // hook key = the secret part of the webhook URL the owner pastes into Wapilot
        public string WaInstance, WaToken, WaHookKey, WaNumber, WaStatus;
        public bool WaEnabled;
        public DateTime? WaLastAt;
        public bool CoOn;                  // ordering from the chat (Shopify draft orders)
        public string CoPay;               // "cod", "card" or "both"
        public bool OcOn, OcHooked;        // order confirmation on WhatsApp
        public string OcScope;             // "all" or "cod" (cash on delivery only)
        // EasyOrders (EasyOrders.cs): key = Public API key, encrypted (Secrets.Protect); mode "app" (install link) or "key" (pasted);
        // hook key = the secret part of our webhook URL; EoOcOn/EoOcScope = order confirmation on WhatsApp
        public string EoKey, EoMode, EoStoreId, EoUrl, EoStatus, EoOcScope, EoHookKey;
        public DateTime? EoSyncedAt, EoHookAt;
        public bool EoOrders, EoOcOn;
        public bool IsAdmin;
        public int MonthlyTokenLimit, DataVersion;
        public DateTime CreatedAt;
        // subscription (set by the super admin; no online payment): null PlanId = no plan (free / custom limit)
        public int? PlanId;
        public string PlanName;
        public DateTime? PlanEndsAt;
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
                ShopifyDomain = r["ShopifyDomain"] as string,
                ShopifyStatus = r["ShopifyStatus"] as string,
                ShopifySyncedAt = r["ShopifySyncedAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["ShopifySyncedAt"], DateTimeKind.Utc),
                ShopifyShop = r["ShopifyShop"] as string,
                ShopifyClientId = r["ShopifyClientId"] as string,
                ShopifySecret = r["ShopifySecret"] as string,
                ShopifyScopes = r["ShopifyScopes"] as string,
                ShopifyOrders = r["ShopifyOrders"] is bool && (bool)r["ShopifyOrders"],
                ShopifyWebhooks = r["ShopifyWebhooks"] is int ? (int)r["ShopifyWebhooks"] : 0,
                WaInstance = r["WaInstance"] as string,
                WaToken = r["WaToken"] as string,
                WaHookKey = r["WaHookKey"] as string,
                WaNumber = r["WaNumber"] as string,
                WaStatus = r["WaStatus"] as string,
                WaEnabled = r["WaEnabled"] is bool && (bool)r["WaEnabled"],
                WaLastAt = r["WaLastAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["WaLastAt"], DateTimeKind.Utc),
                CoOn = r["CoOn"] is bool && (bool)r["CoOn"],
                CoPay = r["CoPay"] as string ?? "cod",
                OcOn = r["OcOn"] is bool && (bool)r["OcOn"],
                OcScope = r["OcScope"] as string ?? "all",
                OcHooked = r["OcHooked"] is bool && (bool)r["OcHooked"],
                EoKey = r["EoKey"] as string,
                EoMode = r["EoMode"] as string,
                EoStoreId = r["EoStoreId"] as string,
                EoUrl = r["EoUrl"] as string,
                EoStatus = r["EoStatus"] as string,
                EoSyncedAt = r["EoSyncedAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["EoSyncedAt"], DateTimeKind.Utc),
                EoOrders = r["EoOrders"] is bool && (bool)r["EoOrders"],
                EoOcOn = r["EoOcOn"] is bool && (bool)r["EoOcOn"],
                EoOcScope = r["EoOcScope"] as string ?? "all",
                EoHookKey = r["EoHookKey"] as string,
                EoHookAt = r["EoHookAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["EoHookAt"], DateTimeKind.Utc),
                Suggestions = r["Suggestions"] as string,
                MonthlyTokenLimit = (int)r["MonthlyTokenLimit"],
                DataVersion = (int)r["DataVersion"],
                CreatedAt = (DateTime)r["CreatedAt"],
                PlanId = r["PlanId"] is int ? (int)r["PlanId"] : (int?)null,
                PlanName = r["PlanName"] as string,
                PlanEndsAt = r["PlanEndsAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["PlanEndsAt"], DateTimeKind.Utc),
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
    BotName NVARCHAR(50) NOT NULL DEFAULT N'',
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
IF COL_LENGTH('dbo.Stores', 'ChatTitle') IS NULL ALTER TABLE dbo.Stores ADD ChatTitle NVARCHAR(80) NULL;
IF COL_LENGTH('dbo.Stores', 'Suggestions') IS NULL ALTER TABLE dbo.Stores ADD Suggestions NVARCHAR(400) NULL;
IF COL_LENGTH('dbo.Stores', 'ShopifyDomain') IS NULL ALTER TABLE dbo.Stores ADD ShopifyDomain NVARCHAR(200) NULL, ShopifySyncedAt DATETIME2 NULL, ShopifyStatus NVARCHAR(400) NULL;
IF COL_LENGTH('dbo.Stores', 'ShopifyShop') IS NULL ALTER TABLE dbo.Stores ADD ShopifyShop NVARCHAR(200) NULL, ShopifyClientId NVARCHAR(100) NULL, ShopifySecret NVARCHAR(1000) NULL,
  ShopifyScopes NVARCHAR(600) NULL, ShopifyOrders BIT NOT NULL CONSTRAINT DF_Stores_ShopifyOrders DEFAULT 0, ShopifyWebhooks INT NOT NULL CONSTRAINT DF_Stores_ShopifyWebhooks DEFAULT 0;
-- WhatsApp (Wapilot): the connection on the store, and the conversation per customer (the bot needs the history; WhatsApp doesn't send it)
IF COL_LENGTH('dbo.Stores', 'WaInstance') IS NULL ALTER TABLE dbo.Stores ADD WaInstance NVARCHAR(100) NULL, WaToken NVARCHAR(1000) NULL, WaHookKey NVARCHAR(64) NULL,
  WaNumber NVARCHAR(60) NULL, WaStatus NVARCHAR(400) NULL, WaEnabled BIT NOT NULL CONSTRAINT DF_Stores_WaEnabled DEFAULT 0, WaLastAt DATETIME2 NULL;
IF OBJECT_ID('dbo.WaMessages') IS NULL
BEGIN
  CREATE TABLE dbo.WaMessages(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    ChatId NVARCHAR(100) NOT NULL,
    FromCustomer BIT NOT NULL,
    Body NVARCHAR(4000) NOT NULL,
    MsgId NVARCHAR(150) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_WaMessages_Chat ON dbo.WaMessages(StoreId, ChatId, Id);
  -- Wapilot may deliver the same message twice: answer it once
  CREATE UNIQUE INDEX UX_WaMessages_MsgId ON dbo.WaMessages(StoreId, MsgId) WHERE MsgId IS NOT NULL;
END
-- Order confirmation on WhatsApp: a new Shopify order -> a message to the customer; their answer confirms it or cancels it in Shopify
IF COL_LENGTH('dbo.Stores', 'OcOn') IS NULL ALTER TABLE dbo.Stores ADD OcOn BIT NOT NULL CONSTRAINT DF_Stores_OcOn DEFAULT 0,
  OcScope NVARCHAR(10) NOT NULL CONSTRAINT DF_Stores_OcScope DEFAULT 'all', OcHooked BIT NOT NULL CONSTRAINT DF_Stores_OcHooked DEFAULT 0;
IF OBJECT_ID('dbo.OrderConfirms') IS NULL
BEGIN
  CREATE TABLE dbo.OrderConfirms(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    OrderId BIGINT NOT NULL,
    OrderName NVARCHAR(40) NOT NULL,
    Customer NVARCHAR(100) NULL,
    Phone NVARCHAR(20) NULL,
    Total NVARCHAR(40) NULL,
    Status NVARCHAR(12) NOT NULL,          -- pending / confirmed / cancelled / failed / skipped
    Note NVARCHAR(300) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    AnsweredAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_OrderConfirms_Order ON dbo.OrderConfirms(StoreId, OrderId);   -- Shopify may deliver a webhook twice
  CREATE INDEX IX_OrderConfirms_Pending ON dbo.OrderConfirms(StoreId, Status, Id);
END
IF OBJECT_ID('dbo.Migrations') IS NULL CREATE TABLE dbo.Migrations(Name NVARCHAR(100) NOT NULL PRIMARY KEY, RanAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'drop-default-bot-name')
BEGIN
  -- The chat used to call every assistant N'دبدوب' by default; stores now speak with their own name.
  UPDATE dbo.Stores SET BotName = N'' WHERE BotName = N'دبدوب';
  UPDATE dbo.Stores SET Welcome = REPLACE(Welcome, N'أنا **دبدوب** 🐻، ', N'') WHERE CHARINDEX(N'أنا **دبدوب** 🐻، ', Welcome) > 0;
  UPDATE dbo.Stores SET DataVersion = DataVersion + 1;
  INSERT INTO dbo.Migrations(Name) VALUES ('drop-default-bot-name');
END
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'riwaq-suggestions')
BEGIN
  -- Suggested questions used to be built into the chat; Riwaq keeps the ones it had.
  -- Run as its own statement: the column is added in this same batch, so a direct UPDATE would not compile.
  DECLARE @riwaqQ NVARCHAR(400) = N'عندي بقع عرق في القميص، أستخدم إيه؟ 👕' + NCHAR(10) + N'بكام ملمع الأخشاب؟ 🪵' + NCHAR(10) + N'الشحن بكام؟ 🚚';
  EXEC sp_executesql N'UPDATE dbo.Stores SET Suggestions = @q WHERE Slug = ''riwaq'' AND Suggestions IS NULL;', N'@q NVARCHAR(400)', @q = @riwaqQ;
  INSERT INTO dbo.Migrations(Name) VALUES ('riwaq-suggestions');
END
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'neutral-welcome')
BEGIN
  -- The old default welcome talked about products, which doesn't fit apps, platforms or services. Only untouched defaults change.
  UPDATE dbo.Stores SET Welcome = REPLACE(REPLACE(Welcome, N'اسألني عن أي منتج وأنا أساعدك تختار الأنسب.', N'عندك أي سؤال؟ اسألني وأنا أساعدك على طول.'), N'**! 🛍️', N'**! 👋')
    WHERE CHARINDEX(N'اسألني عن أي منتج وأنا أساعدك تختار الأنسب.', Welcome) > 0;
  INSERT INTO dbo.Migrations(Name) VALUES ('neutral-welcome');
END
-- ---------- plans, subscriptions, support, super admin ----------
IF COL_LENGTH('dbo.Stores', 'PlanId') IS NULL ALTER TABLE dbo.Stores ADD PlanId INT NULL, PlanName NVARCHAR(100) NULL, PlanEndsAt DATETIME2 NULL;
IF OBJECT_ID('dbo.Admins') IS NULL
  CREATE TABLE dbo.Admins(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Username NVARCHAR(50) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(300) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    LastLoginAt DATETIME2 NULL);
IF COL_LENGTH('dbo.Admins', 'DefaultPassword') IS NULL ALTER TABLE dbo.Admins ADD DefaultPassword BIT NOT NULL CONSTRAINT DF_Admins_DefaultPassword DEFAULT 1;
IF OBJECT_ID('dbo.Plans') IS NULL
  CREATE TABLE dbo.Plans(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Description NVARCHAR(400) NULL,
    Features NVARCHAR(2000) NULL,
    PriceEgp DECIMAL(12,2) NOT NULL,
    UsdBudget DECIMAL(10,2) NOT NULL,
    TokenLimit INT NOT NULL,
    DurationDays INT NOT NULL DEFAULT 30,
    IsActive BIT NOT NULL DEFAULT 1,
    IsFeatured BIT NOT NULL DEFAULT 0,
    IsDeleted BIT NOT NULL DEFAULT 0,
    SortOrder INT NOT NULL DEFAULT 0,
    DiscountType NVARCHAR(10) NULL,
    DiscountValue DECIMAL(12,2) NULL,
    DiscountEndsAt DATETIME2 NULL,
    DiscountLabel NVARCHAR(60) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.Subscriptions') IS NULL
BEGIN
  CREATE TABLE dbo.Subscriptions(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    PlanId INT NULL,
    PlanName NVARCHAR(100) NOT NULL,
    Months INT NOT NULL DEFAULT 1,
    PriceEgp DECIMAL(12,2) NOT NULL,
    DiscountEgp DECIMAL(12,2) NOT NULL DEFAULT 0,
    PaidEgp DECIMAL(12,2) NOT NULL,
    PaymentMethod NVARCHAR(40) NULL,
    Notes NVARCHAR(500) NULL,
    StartsAt DATETIME2 NOT NULL,
    EndsAt DATETIME2 NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_Subscriptions_Store ON dbo.Subscriptions(StoreId);
END
-- a store owner asks for a plan (after paying); the super admin approves it (-> Subscriptions) or rejects it
IF OBJECT_ID('dbo.PlanRequests') IS NULL
BEGIN
  CREATE TABLE dbo.PlanRequests(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    PlanId INT NOT NULL,
    PlanName NVARCHAR(100) NOT NULL,
    Months INT NOT NULL,
    PriceEgp DECIMAL(12,2) NOT NULL,
    DiscountEgp DECIMAL(12,2) NOT NULL DEFAULT 0,
    AmountEgp DECIMAL(12,2) NOT NULL,
    PaymentMethod NVARCHAR(40) NULL,
    PaymentRef NVARCHAR(100) NULL,
    Note NVARCHAR(500) NULL,
    Status NVARCHAR(12) NOT NULL DEFAULT 'pending',
    AdminNote NVARCHAR(500) NULL,
    SubscriptionId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    DecidedAt DATETIME2 NULL);
  CREATE INDEX IX_PlanRequests_Store ON dbo.PlanRequests(StoreId);
END
-- a request is either a plan (Kind 'plan') or extra quota for the current month (Kind 'topup', Units added to Month when approved)
IF COL_LENGTH('dbo.PlanRequests', 'Kind') IS NULL
  ALTER TABLE dbo.PlanRequests ADD Kind NVARCHAR(10) NOT NULL CONSTRAINT DF_PlanRequests_Kind DEFAULT 'plan', Units BIGINT NULL, Month CHAR(7) NULL;
IF OBJECT_ID('dbo.Tickets') IS NULL
BEGIN
  CREATE TABLE dbo.Tickets(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Subject NVARCHAR(150) NOT NULL,
    Category NVARCHAR(30) NOT NULL DEFAULT 'other',
    Status NVARCHAR(20) NOT NULL DEFAULT 'open',
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_Tickets_Store ON dbo.Tickets(StoreId);
END
IF OBJECT_ID('dbo.TicketMessages') IS NULL
  CREATE TABLE dbo.TicketMessages(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    TicketId INT NOT NULL REFERENCES dbo.Tickets(Id) ON DELETE CASCADE,
    FromAdmin BIT NOT NULL DEFAULT 0,
    Body NVARCHAR(4000) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.AppSettings') IS NULL
  CREATE TABLE dbo.AppSettings([Key] NVARCHAR(60) NOT NULL PRIMARY KEY, [Value] NVARCHAR(400) NULL);
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'seed-super-admin')
BEGIN
  -- The first super admin: user ""admin"". Only a PBKDF2 hash is stored; change the password from the console's settings.
  IF NOT EXISTS (SELECT 1 FROM dbo.Admins WHERE Username = N'admin')
    INSERT INTO dbo.Admins(Username, PasswordHash) VALUES (N'admin', N'pbkdf2$100000$3iQZqYUUaiU1pvtuEHhFKw==$YwyN/etSIi3/KvXpkgKsBVJlZR3AL8mKvtZvegtOuMA=');
  INSERT INTO dbo.Migrations(Name) VALUES ('seed-super-admin');
END
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'seed-plans')
BEGIN
  -- Starting plans: price in EGP, a monthly Claude budget in USD (1$ = 10,000,000 cost-weighted units at Haiku 5.5 prices).
  IF NOT EXISTS (SELECT 1 FROM dbo.Plans)
    INSERT INTO dbo.Plans(Name, Description, Features, PriceEgp, UsdBudget, TokenLimit, DurationDays, IsFeatured, SortOrder) VALUES
    (N'الأساسية', N'بداية مثالية لمتجر صغير', N'مساعد ذكي بيرد 24 ساعة' + NCHAR(10) + N'رفع ملفات PDF و Word و Excel' + NCHAR(10) + N'20 شكل للشات وأي لون' + NCHAR(10) + N'دعم فني', 1000, 2, 20000000, 30, 0, 1),
    (N'المتقدمة', N'الأنسب لأغلب المتاجر', N'كل مميزات الأساسية' + NCHAR(10) + N'رسايل أكتر بـ 50%' + NCHAR(10) + N'ربط متجر Shopify أو EasyOrders' + NCHAR(10) + N'أولوية في الدعم', 1300, 3, 30000000, 30, 1, 2),
    (N'الاحترافية', N'للمتاجر الكبيرة والزحمة', N'كل مميزات المتقدمة' + NCHAR(10) + N'أكبر عدد رسايل' + NCHAR(10) + N'تتبع الأوردرات في الشات' + NCHAR(10) + N'ربط Shopify و EasyOrders مع بعض' + NCHAR(10) + N'دعم مباشر على واتساب', 1600, 5, 50000000, 30, 0, 3);
  INSERT INTO dbo.Migrations(Name) VALUES ('seed-plans');
END
-- The starting plans' lines learn about EasyOrders (once). A plan whose text the admin already changed is left alone.
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'plans-easyorders')
BEGIN
  UPDATE dbo.Plans SET Features = REPLACE(Features, N'ربط متجر Shopify', N'ربط متجر Shopify أو EasyOrders')
    WHERE Features LIKE N'%ربط متجر Shopify%' AND Features NOT LIKE N'%EasyOrders%';
  UPDATE dbo.Plans SET Features = REPLACE(Features, N'تتبع الأوردرات في الشات', N'تتبع الأوردرات في الشات' + NCHAR(10) + N'ربط Shopify و EasyOrders مع بعض')
    WHERE Name = N'الاحترافية' AND Features LIKE N'%تتبع الأوردرات في الشات%' AND Features NOT LIKE N'%EasyOrders%';
  INSERT INTO dbo.Migrations(Name) VALUES ('plans-easyorders');
END
-- ---------- ordering from the chat (Shopify draft orders), one row per order being built in a chat ----------
IF COL_LENGTH('dbo.Stores', 'CoOn') IS NULL ALTER TABLE dbo.Stores ADD CoOn BIT NOT NULL CONSTRAINT DF_Stores_CoOn DEFAULT 0,
  CoPay NVARCHAR(10) NOT NULL CONSTRAINT DF_Stores_CoPay DEFAULT 'cod';
IF OBJECT_ID('dbo.ChatOrders') IS NULL
BEGIN
  CREATE TABLE dbo.ChatOrders(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Sid NVARCHAR(110) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'draft',
    Items NVARCHAR(MAX) NULL,
    Name NVARCHAR(120) NULL, Phone NVARCHAR(20) NULL, Province NVARCHAR(60) NULL, ProvinceCode NVARCHAR(5) NULL,
    City NVARCHAR(80) NULL, Address NVARCHAR(300) NULL,
    Rates NVARCHAR(MAX) NULL, ShipTitle NVARCHAR(150) NULL, ShipPrice DECIMAL(12,2) NULL, Payment NVARCHAR(10) NULL, Currency NVARCHAR(5) NULL,
    Subtotal DECIMAL(12,2) NULL, Shipping DECIMAL(12,2) NULL, Discounts DECIMAL(12,2) NULL, Tax DECIMAL(12,2) NULL, Total DECIMAL(12,2) NULL,
    CodeHash NVARCHAR(100) NULL, CodeExpires DATETIME2 NULL, CodeTries INT NOT NULL DEFAULT 0, CodeSends INT NOT NULL DEFAULT 0,
    DraftGid NVARCHAR(100) NULL, OrderName NVARCHAR(40) NULL, InvoiceUrl NVARCHAR(500) NULL, Note NVARCHAR(300) NULL, Ip NVARCHAR(60) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_ChatOrders_Sid ON dbo.ChatOrders(StoreId, Sid, Id);
END
-- ---------- EasyOrders: connection, data sync, order tracking, order confirmation on WhatsApp (EasyOrders.cs) ----------
IF COL_LENGTH('dbo.Stores', 'EoKey') IS NULL ALTER TABLE dbo.Stores ADD EoKey NVARCHAR(1000) NULL, EoMode NVARCHAR(10) NULL, EoStoreId NVARCHAR(64) NULL,
  EoUrl NVARCHAR(200) NULL, EoStatus NVARCHAR(400) NULL, EoSyncedAt DATETIME2 NULL,
  EoOrders BIT NOT NULL CONSTRAINT DF_Stores_EoOrders DEFAULT 0, EoOcOn BIT NOT NULL CONSTRAINT DF_Stores_EoOcOn DEFAULT 0,
  EoOcScope NVARCHAR(10) NOT NULL CONSTRAINT DF_Stores_EoOcScope DEFAULT 'all', EoHookKey NVARCHAR(40) NULL, EoHookAt DATETIME2 NULL;
IF OBJECT_ID('dbo.EoConfirms') IS NULL
BEGIN
  CREATE TABLE dbo.EoConfirms(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    OrderId NVARCHAR(64) NOT NULL,
    OrderName NVARCHAR(40) NULL, Customer NVARCHAR(100) NULL, Phone NVARCHAR(20) NULL, Total NVARCHAR(40) NULL,
    Status NVARCHAR(20) NOT NULL, Note NVARCHAR(300) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), AnsweredAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_EoConfirms_Order ON dbo.EoConfirms(StoreId, OrderId);   -- EasyOrders may deliver a webhook twice
  CREATE INDEX IX_EoConfirms_Pending ON dbo.EoConfirms(StoreId, Status, Id);
END";
    }
}
