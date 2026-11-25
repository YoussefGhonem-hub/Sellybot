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
        public DateTime? WaUntil;          // the WhatsApp service (an add-on on any plan) runs until then (WaAccount.HasService)
        public bool CoOn;                  // ordering from the chat (Shopify draft orders)
        public string CoPay;               // "cod", "card" or "both"
        public bool OcOn, OcHooked;        // order confirmation on WhatsApp
        public string OcScope;             // "all" or "cod" (cash on delivery only)
        // EasyOrders (EasyOrders.cs): key = Public API key, encrypted (Secrets.Protect); mode "app" (install link) or "key" (pasted);
        // hook key = the secret part of our webhook URL; EoOcOn/EoOcScope = order confirmation on WhatsApp
        public string EoKey, EoMode, EoStoreId, EoUrl, EoStatus, EoOcScope, EoHookKey;
        public DateTime? EoSyncedAt, EoHookAt;
        public bool EoOrders, EoOcOn;
        // the delivery-day message on WhatsApp (Delivery.cs): DnOn/DnHooked for Shopify, EoDnOn for EasyOrders
        public bool DnOn, DnHooked, EoDnOn;
        // WhatsApp growth features: the owner's own number (report + handoff alerts), abandoned carts (Carts.cs),
        // «بلّغني لما يرجع» (Restock.cs), the weekly report (WeeklyReport.cs), handoff to the owner (Handoff.cs)
        public string OwnerPhone, CartCoupon;
        public bool CartOn, CartHooked, RestockOn, ReportOn, HandoffOn;
        public bool BcAsk;   // «ابعت عرض»: ask Messenger / Instagram customers for their WhatsApp number (Offers.cs)
        public int CartDelay;
        public DateTime? ReportAt;
        // more than one WhatsApp number (WaLines.cs): who holds the main number, how many numbers are paid for, the line the
        // owner's reports come from (0 = main); following up an unanswered confirmation (Followup.cs, hours, 0 = off);
        // the morning summary (WeeklyReport.cs)
        public string WaStaff;
        public int WaCount = 1, ReportLine, OcFollow = 3;
        public bool ReportDaily;
        public DateTime? ReportDayAt;
        // the store's own logo as its chat bubble (style "logo", StoreLogo.cs): when it was uploaded (0 = none), contain / cover
        public int LogoVer; public string LogoFit;
        public bool IsAdmin;
        public int MonthlyTokenLimit, DataVersion;
        public DateTime CreatedAt;
        // subscription (set by the super admin; no online payment): null PlanId = no plan (free / custom limit)
        public int? PlanId;
        public string PlanName;
        public DateTime? PlanEndsAt;
        // a move to a lower plan waits for the current one to end: then NextPlan runs for NextDays (Db.RollOverPlans)
        public int? NextPlanId;
        public string NextPlanName;
        public int NextDays;
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
                    if (!ensured) { using (var cmd = new SqlCommand(Schema, c)) cmd.ExecuteNonQuery(); ensured = true; try { PlanGate.Seed(c); Billing.EnsurePriceLocks(c); SiteBot.Ensure(c); } finally { Jobs.Start(); } }
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

        static StoreInfo OneStore(SqlConnection c, string where, object v, bool rolled = false)
        {
            StoreInfo s;
            using (var cmd = Cmd(c, "SELECT TOP 1 * FROM dbo.Stores " + where, "@v", v))
            using (var r = cmd.ExecuteReader())
                s = r.Read() ? ReadStore(r) : null;
            // a scheduled lower plan whose turn has come: switch first, so every check sees the plan that runs now
            if (!rolled && s != null && s.NextPlanId.HasValue && s.PlanEndsAt.HasValue && s.PlanEndsAt.Value <= DateTime.UtcNow)
            {
                RollOverPlans(c);
                return OneStore(c, where, v, true);
            }
            return s;
        }

        // Starts every scheduled plan (Stores.Next*) whose current plan has ended. The new period starts where the old one ended.
        public static void RollOverPlans(SqlConnection c)
        {
            using (var cmd = Cmd(c, @"UPDATE s SET PlanId = s.NextPlanId, PlanName = s.NextPlanName, PlanEndsAt = DATEADD(DAY, s.NextDays, s.PlanEndsAt),
                                             MonthlyTokenLimit = ISNULL(p.TokenLimit, s.MonthlyTokenLimit),
                                             NextPlanId = NULL, NextPlanName = NULL, NextDays = NULL
                                      FROM dbo.Stores s LEFT JOIN dbo.Plans p ON p.Id = s.NextPlanId
                                      WHERE s.NextPlanId IS NOT NULL AND s.PlanEndsAt <= SYSUTCDATETIME()")) cmd.ExecuteNonQuery();
        }

        public static List<StoreInfo> AllStores(SqlConnection c)
        {
            RollOverPlans(c);
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
                WaUntil = r["WaUntil"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["WaUntil"], DateTimeKind.Utc),
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
                DnOn = r["DnOn"] is bool && (bool)r["DnOn"],
                DnHooked = r["DnHooked"] is bool && (bool)r["DnHooked"],
                EoDnOn = r["EoDnOn"] is bool && (bool)r["EoDnOn"],
                OwnerPhone = r["OwnerPhone"] as string,
                CartOn = r["CartOn"] is bool && (bool)r["CartOn"],
                CartDelay = r["CartDelay"] is int ? (int)r["CartDelay"] : 60,
                CartCoupon = r["CartCoupon"] as string,
                CartHooked = r["CartHooked"] is bool && (bool)r["CartHooked"],
                RestockOn = r["RestockOn"] is bool && (bool)r["RestockOn"],
                ReportOn = r["ReportOn"] is bool && (bool)r["ReportOn"],
                ReportAt = r["ReportAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["ReportAt"], DateTimeKind.Utc),
                HandoffOn = r["HandoffOn"] is bool && (bool)r["HandoffOn"],
                WaStaff = r["WaStaff"] as string,
                WaCount = r["WaCount"] is int ? Math.Max(1, (int)r["WaCount"]) : 1,
                ReportLine = r["ReportLine"] is int ? (int)r["ReportLine"] : 0,
                OcFollow = r["OcFollow"] is int ? (int)r["OcFollow"] : 3,
                ReportDaily = r["ReportDaily"] is bool && (bool)r["ReportDaily"],
                ReportDayAt = r["ReportDayAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["ReportDayAt"], DateTimeKind.Utc) : (DateTime?)null,
                BcAsk = r["BcAsk"] is bool && (bool)r["BcAsk"],
                LogoVer = r["LogoVer"] is int ? (int)r["LogoVer"] : 0,
                LogoFit = r["LogoFit"] as string,
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
                NextPlanId = r["NextPlanId"] is int ? (int)r["NextPlanId"] : (int?)null,
                NextPlanName = r["NextPlanName"] as string,
                NextDays = r["NextDays"] is int ? (int)r["NextDays"] : 0,
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
     VALUES (@s, @m, @n, @i, @cr, @cw, @o);
-- answers and cost-weighted units per day (UTC): the weekly report counts the answers, «المكسب والتكلفة» the daily spend
MERGE dbo.UsageDaily AS d USING (SELECT @s AS StoreId, CAST(SYSUTCDATETIME() AS DATE) AS Day) AS x ON d.StoreId = x.StoreId AND d.Day = x.Day
WHEN MATCHED THEN UPDATE SET Messages = Messages + @n, Units = Units + @w
WHEN NOT MATCHED THEN INSERT (StoreId, Day, Messages, Units) VALUES (@s, x.Day, @n, @w);";
            long weighted = new UsageInfo { InputTokens = input, CacheReadTokens = cacheRead, CacheWriteTokens = cacheWrite, OutputTokens = output }.Weighted;
            using (var cmd = Cmd(c, sql, "@s", storeId, "@m", Month, "@n", messages, "@i", input, "@cr", cacheRead, "@cw", cacheWrite, "@o", output, "@w", weighted))
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
-- the WhatsApp service (WaAccount.HasService): an add-on on any plan, paid by the month; WhatsApp works while WaUntil is ahead
IF COL_LENGTH('dbo.Stores', 'WaUntil') IS NULL ALTER TABLE dbo.Stores ADD WaUntil DATETIME2 NULL;
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
-- Delivery-day message on WhatsApp (Delivery.cs): an order goes out for delivery -> the courier is coming, have the cash ready
IF COL_LENGTH('dbo.Stores', 'DnOn') IS NULL ALTER TABLE dbo.Stores ADD DnOn BIT NOT NULL CONSTRAINT DF_Stores_DnOn DEFAULT 0,
  DnHooked BIT NOT NULL CONSTRAINT DF_Stores_DnHooked DEFAULT 0, EoDnOn BIT NOT NULL CONSTRAINT DF_Stores_EoDnOn DEFAULT 0;
IF OBJECT_ID('dbo.DeliveryNotices') IS NULL
BEGIN
  CREATE TABLE dbo.DeliveryNotices(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Source NVARCHAR(10) NOT NULL,          -- shopify / eo
    OrderId NVARCHAR(64) NOT NULL,
    OrderName NVARCHAR(40) NULL, Customer NVARCHAR(100) NULL, Phone NVARCHAR(20) NULL, Due NVARCHAR(40) NULL,
    Status NVARCHAR(12) NOT NULL,          -- sent / failed / skipped
    Note NVARCHAR(300) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_DeliveryNotices_Order ON dbo.DeliveryNotices(StoreId, Source, OrderId);   -- one message per order
  CREATE INDEX IX_DeliveryNotices_Store ON dbo.DeliveryNotices(StoreId, Source, Id);
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
-- the WhatsApp service asked for with a plan (WaAddon = 1), or alone (Kind 'wa'): its share of AmountEgp and the number the owner will link
IF COL_LENGTH('dbo.PlanRequests', 'WaAddon') IS NULL
  ALTER TABLE dbo.PlanRequests ADD WaAddon BIT NOT NULL CONSTRAINT DF_PlanRequests_WaAddon DEFAULT 0, WaEgp DECIMAL(12,2) NULL, WaPhone NVARCHAR(20) NULL;
-- what the request meant when it was sent (new / renew / upgrade / downgrade / wa-new / wa-renew / topup), and for extra messages
-- how many were asked for and how the suggestion was worked out (the admin sees both)
IF COL_LENGTH('dbo.PlanRequests', 'What') IS NULL
  ALTER TABLE dbo.PlanRequests ADD What NVARCHAR(20) NULL, Msgs INT NULL, Calc NVARCHAR(400) NULL;
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
-- the delivery-day message in the plan lines (next to the store link), and the 30 bubble styles
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'plans-delivery')
BEGIN
  UPDATE dbo.Plans SET Features = REPLACE(Features, N'ربط متجر Shopify أو EasyOrders', N'ربط متجر Shopify أو EasyOrders' + NCHAR(10) + N'رسالة يوم التوصيل على واتساب')
    WHERE Features LIKE N'%ربط متجر Shopify أو EasyOrders%' AND Features NOT LIKE N'%يوم التوصيل%';
  UPDATE dbo.Plans SET Features = REPLACE(Features, N'20 شكل للشات', N'30 شكل للشات') WHERE Features LIKE N'%20 شكل للشات%';
  INSERT INTO dbo.Migrations(Name) VALUES ('plans-delivery');
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
END
-- ---------- changing plans: a lower plan waits for the current one to end (Stores.Next*), a payment row says what it was for ----------
IF COL_LENGTH('dbo.Stores', 'NextPlanId') IS NULL ALTER TABLE dbo.Stores ADD NextPlanId INT NULL, NextPlanName NVARCHAR(100) NULL, NextDays INT NULL;
-- Subscriptions.Kind: 'plan' (a period), 'topup' (extra quota for one month), 'upgrade' (price difference for a higher plan, same end date)
IF COL_LENGTH('dbo.Subscriptions', 'Kind') IS NULL
BEGIN
  ALTER TABLE dbo.Subscriptions ADD Kind NVARCHAR(10) NOT NULL CONSTRAINT DF_Subscriptions_Kind DEFAULT 'plan';
  -- own statement: the column is added in this same batch
  EXEC sp_executesql N'UPDATE dbo.Subscriptions SET Kind = ''topup'' WHERE Months = 0;';
END
-- the reference number InstaPay shows after a transfer (normalized: A-Z 0-9). One payment backs one request:
-- no two pending or approved requests may share it (a cancelled or rejected one frees it to be sent again)
IF COL_LENGTH('dbo.PlanRequests', 'RefNo') IS NULL ALTER TABLE dbo.PlanRequests ADD RefNo NVARCHAR(40) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PlanRequests_RefNo' AND object_id = OBJECT_ID('dbo.PlanRequests'))
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_PlanRequests_RefNo ON dbo.PlanRequests(RefNo) WHERE RefNo IS NOT NULL AND Status IN (''pending'', ''approved'');';
-- pictures the super admin uploads (InstaPay's QR for owners who pay): kept here, never in the (public) code
IF OBJECT_ID('dbo.AppFiles') IS NULL
  CREATE TABLE dbo.AppFiles([Key] NVARCHAR(60) NOT NULL PRIMARY KEY, ContentType NVARCHAR(40) NOT NULL, Data VARBINARY(MAX) NOT NULL,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
-- ---- the store's own logo as its chat bubble (style logo, StoreLogo.cs, 2026-11-17) ----
-- LogoVer: when it was uploaded, in Unix seconds, so its address changes with every upload (0 = no logo) · LogoFit: contain / cover
-- The picture itself is in StoreLogos, so reading a store (SELECT *) never pulls it.
IF COL_LENGTH('dbo.Stores', 'LogoVer') IS NULL ALTER TABLE dbo.Stores ADD LogoVer INT NOT NULL CONSTRAINT DF_Stores_LogoVer DEFAULT 0, LogoFit NVARCHAR(10) NULL;
IF OBJECT_ID('dbo.StoreLogos') IS NULL
  CREATE TABLE dbo.StoreLogos(StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    ContentType NVARCHAR(20) NOT NULL, Data VARBINARY(MAX) NOT NULL, UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
-- ---- WhatsApp growth features (2026-11-17) ----
-- OwnerPhone: the owner's own WhatsApp (the weekly report and handoff alerts go there from the store's number)
-- Cart*: abandoned-cart reminder (Carts.cs) on/off, minutes to wait, an optional coupon, Shopify's checkout webhooks registered
-- RestockOn: «بلّغني لما يرجع» (Restock.cs) · ReportOn/ReportAt: the weekly report and when it last went out · HandoffOn: handoff alerts
IF COL_LENGTH('dbo.Stores', 'OwnerPhone') IS NULL ALTER TABLE dbo.Stores ADD OwnerPhone NVARCHAR(20) NULL,
  CartOn BIT NOT NULL CONSTRAINT DF_Stores_CartOn DEFAULT 0, CartDelay INT NOT NULL CONSTRAINT DF_Stores_CartDelay DEFAULT 60,
  CartCoupon NVARCHAR(40) NULL, CartHooked BIT NOT NULL CONSTRAINT DF_Stores_CartHooked DEFAULT 0,
  RestockOn BIT NOT NULL CONSTRAINT DF_Stores_RestockOn DEFAULT 0,
  ReportOn BIT NOT NULL CONSTRAINT DF_Stores_ReportOn DEFAULT 0, ReportAt DATETIME2 NULL,
  HandoffOn BIT NOT NULL CONSTRAINT DF_Stores_HandoffOn DEFAULT 0;
IF OBJECT_ID('dbo.CartReminders') IS NULL
BEGIN
  CREATE TABLE dbo.CartReminders(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Source NVARCHAR(10) NOT NULL,          -- shopify / eo
    CheckoutId NVARCHAR(64) NOT NULL,
    Phone NVARCHAR(20) NULL, Customer NVARCHAR(100) NULL, Items NVARCHAR(500) NULL, Total NVARCHAR(40) NULL, Url NVARCHAR(800) NULL,
    Status NVARCHAR(12) NOT NULL,          -- waiting / sent / ordered (bought before or after the message) / skipped / failed
    Note NVARCHAR(300) NULL,
    DueAt DATETIME2 NOT NULL, SentAt DATETIME2 NULL, OrderedAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_CartReminders_Checkout ON dbo.CartReminders(StoreId, Source, CheckoutId);   -- one reminder per checkout
  CREATE INDEX IX_CartReminders_Due ON dbo.CartReminders(Status, DueAt);
  CREATE INDEX IX_CartReminders_Store ON dbo.CartReminders(StoreId, Id);
END
IF OBJECT_ID('dbo.RestockWaits') IS NULL
BEGIN
  CREATE TABLE dbo.RestockWaits(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Source NVARCHAR(10) NOT NULL,          -- shopify / eo / site (a product typed in the store's own data)
    ProductKey NVARCHAR(100) NOT NULL,     -- the product id in its store system
    Product NVARCHAR(200) NOT NULL, Variant NVARCHAR(100) NULL, Url NVARCHAR(600) NULL,
    Phone NVARCHAR(20) NOT NULL,
    Status NVARCHAR(12) NOT NULL,          -- waiting / sent / expired / failed
    Note NVARCHAR(300) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), SentAt DATETIME2 NULL, CheckedAt DATETIME2 NULL);
  CREATE INDEX IX_RestockWaits_Product ON dbo.RestockWaits(StoreId, ProductKey, Status);
  CREATE INDEX IX_RestockWaits_Store ON dbo.RestockWaits(StoreId, Id);
END
IF OBJECT_ID('dbo.Handoffs') IS NULL
BEGIN
  CREATE TABLE dbo.Handoffs(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Channel NVARCHAR(8) NOT NULL,          -- wa / web
    ChatKey NVARCHAR(100) NOT NULL,        -- the customer's number (wa) or the site chat's session (web)
    Phone NVARCHAR(20) NULL, Reason NVARCHAR(400) NOT NULL,
    Status NVARCHAR(12) NOT NULL,          -- sent (the owner got the alert) / failed / noalert (no owner number)
    Note NVARCHAR(300) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_Handoffs_Store ON dbo.Handoffs(StoreId, Id);
END
IF COL_LENGTH('dbo.RestockWaits', 'CheckedAt') IS NULL ALTER TABLE dbo.RestockWaits ADD CheckedAt DATETIME2 NULL;   -- when the stock was last looked at
-- answers per day (site + WhatsApp), so the weekly report can say how many this week (UsageMonthly is per month)
IF OBJECT_ID('dbo.UsageDaily') IS NULL
  CREATE TABLE dbo.UsageDaily(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Day DATE NOT NULL, Messages INT NOT NULL DEFAULT 0,
    PRIMARY KEY (StoreId, Day));
-- the day's cost-weighted units too (2026-11-21, «المكسب والتكلفة»: the Claude spend per day). Days before it show 0 and are estimated.
IF COL_LENGTH('dbo.UsageDaily', 'Units') IS NULL ALTER TABLE dbo.UsageDaily ADD Units BIGINT NOT NULL CONSTRAINT DF_UsageDaily_Units DEFAULT 0;
-- a picture the customer sent on WhatsApp (its URL at Wapilot), for the assistant to look at (WaImages)
IF COL_LENGTH('dbo.WaMessages', 'Media') IS NULL ALTER TABLE dbo.WaMessages ADD Media NVARCHAR(800) NULL;
-- customers who sent «وقف» after one of Sellybot's own messages (abandoned cart, back in stock): no more of those from this store
IF OBJECT_ID('dbo.WaOptOuts') IS NULL
  CREATE TABLE dbo.WaOptOuts(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), PRIMARY KEY (StoreId, Phone));
-- «السعر القديم» (2026-11-18): a store that paid for a plan before the cut-off day (setting OldPriceUntil) keeps that monthly price
-- for its first OldPriceMonths months on the plan (Billing.OldFor). One row per store; each request keeps what it got.
IF OBJECT_ID('dbo.PriceLocks') IS NULL
  CREATE TABLE dbo.PriceLocks(StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    PlanId INT NOT NULL, PlanName NVARCHAR(100) NOT NULL, PriceEgp DECIMAL(12,2) NOT NULL,
    MonthsUsed INT NOT NULL DEFAULT 0,      -- months paid at the old price so far (the first ones on this plan)
    StartsAt DATETIME2 NOT NULL, ExpiresAt DATETIME2 NOT NULL,   -- the first old-price month, and the last moment to come back to it
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF COL_LENGTH('dbo.PlanRequests', 'OldPrice') IS NULL ALTER TABLE dbo.PlanRequests ADD OldPrice DECIMAL(12,2) NULL, OldLeft INT NULL;
-- «دعوة تاجر» (Referral.cs, 2026-11-22): one row per store that signed up from another store's invite link. PaidAt = its first paid
-- plan, which earns the inviter one month at ReferralPct% off; RequestId = the inviter's plan request holding (pending) or using
-- (approved, UsedAt) that month. No foreign key on ReferrerId (a second cascade path to Stores); deleting a store clears its rows.
IF OBJECT_ID('dbo.Referrals') IS NULL
BEGIN
  CREATE TABLE dbo.Referrals(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    ReferrerId INT NOT NULL,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    PaidAt DATETIME2 NULL, RequestId INT NULL, UsedAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_Referrals_Store ON dbo.Referrals(StoreId);
  CREATE INDEX IX_Referrals_Referrer ON dbo.Referrals(ReferrerId, PaidAt);
END
-- a plan request's invite discount: how many of its months are at ReferralPct% off, and how much that took off
IF COL_LENGTH('dbo.PlanRequests', 'RefMonths') IS NULL ALTER TABLE dbo.PlanRequests ADD RefMonths INT NULL, RefEgp DECIMAL(12,2) NULL;
-- ---- Messenger and Instagram (Meta.cs, 2026-11-19) ----
-- MetaLinks: one Facebook Page (fb) and one Instagram professional account (ig) per store. AccountId = the Page ID /
-- the Instagram account ID (entry.id in the webhooks); Token = the Page access token / the business's system-user token,
-- encrypted (Secrets.Protect). Subscribed = the account posts its messages to our webhook.
IF OBJECT_ID('dbo.MetaLinks') IS NULL
BEGIN
  CREATE TABLE dbo.MetaLinks(
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Channel NVARCHAR(4) NOT NULL,
    AccountId NVARCHAR(40) NOT NULL, Name NVARCHAR(200) NULL,
    Token NVARCHAR(2000) NOT NULL, BusinessId NVARCHAR(40) NULL,
    Enabled BIT NOT NULL DEFAULT 1, Subscribed BIT NOT NULL DEFAULT 0,
    Status NVARCHAR(400) NULL, LastAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (StoreId, Channel));
  CREATE UNIQUE INDEX UX_MetaLinks_Account ON dbo.MetaLinks(Channel, AccountId);   -- a Page / account answers for one store only
END
-- the chats on Messenger / Instagram (ChatId = the customer's page-scoped / Instagram-scoped id), like WaMessages
IF OBJECT_ID('dbo.MetaMessages') IS NULL
BEGIN
  CREATE TABLE dbo.MetaMessages(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Channel NVARCHAR(4) NOT NULL, ChatId NVARCHAR(64) NOT NULL,
    FromCustomer BIT NOT NULL, Body NVARCHAR(4000) NOT NULL,
    MsgId NVARCHAR(150) NULL,     -- Meta's mid; owner:<mid> when the owner answered by hand; NULL for the bot's own replies
    Media NVARCHAR(800) NULL,     -- a picture the customer sent (its URL at Meta), for the assistant to look at
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_MetaMessages_Chat ON dbo.MetaMessages(StoreId, Channel, ChatId, Id);
  CREATE UNIQUE INDEX UX_MetaMessages_MsgId ON dbo.MetaMessages(StoreId, MsgId) WHERE MsgId IS NOT NULL;   -- Meta may deliver twice
END
-- an owner who manages several Pages / Instagram accounts picks one in the dashboard; the choices (with tokens) wait here, encrypted
IF OBJECT_ID('dbo.MetaConnects') IS NULL
  CREATE TABLE dbo.MetaConnects(StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Channel NVARCHAR(4) NOT NULL,
    Choices NVARCHAR(MAX) NOT NULL, ExpiresAt DATETIME2 NOT NULL, PRIMARY KEY (StoreId, Channel));
-- ---- each store's own Meta app (2026-11-20) ----
-- The owner makes an app on developers.facebook.com with the same steps as Sellybot's and pastes its numbers in the
-- dashboard. Being an admin of their own app, their Page answers everyone without App Review. Secret encrypted
-- (Secrets.Protect). VerifyToken = the handshake for this store's webhook URL (api/meta-webhook.ashx?s=<StoreId>); the
-- row is made with it the first time the owner opens the tab, AppId comes when they save.
IF OBJECT_ID('dbo.MetaApps') IS NULL
BEGIN
  CREATE TABLE dbo.MetaApps(
    StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    AppId NVARCHAR(40) NULL, Secret NVARCHAR(1000) NULL,
    FbConfig NVARCHAR(40) NULL, IgConfig NVARCHAR(40) NULL,
    VerifyToken NVARCHAR(64) NOT NULL,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
END
-- one app answers for one store only. The App ID can be saved before its secret (right after Create App), but until Meta
-- has checked the two together it's only a claim, so it doesn't keep the app's real owner out: the index covers checked
-- apps only (2026-11-20; it was every saved App ID).
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_MetaApps_App' AND object_id=OBJECT_ID('dbo.MetaApps')) DROP INDEX UX_MetaApps_App ON dbo.MetaApps;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_MetaApps_Checked' AND object_id=OBJECT_ID('dbo.MetaApps'))
  CREATE UNIQUE INDEX UX_MetaApps_Checked ON dbo.MetaApps(AppId) WHERE AppId IS NOT NULL AND Secret IS NOT NULL;
-- the app a link's token came from (NULL = Sellybot's, links made before 2026-11-20)
IF COL_LENGTH('dbo.MetaLinks', 'AppId') IS NULL ALTER TABLE dbo.MetaLinks ADD AppId NVARCHAR(40) NULL;
-- «ضيفنا على صفحتك» (2026-11-23): the owner adds Sellybot's Facebook account to their Page (messages only) and asks here;
-- the super admin links that Page through Sellybot's app. One row per store, its latest ask.
-- Status: pending (waiting for the admin) | problem (Note says what the owner should fix, then they send again) | done.
IF OBJECT_ID('dbo.MetaRequests') IS NULL
  CREATE TABLE dbo.MetaRequests(
    StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    PageUrl NVARCHAR(300) NOT NULL, IgUser NVARCHAR(60) NULL,
    Status NVARCHAR(12) NOT NULL DEFAULT 'pending', Note NVARCHAR(400) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
-- ---------- «الاقتصادية» (2026-11-23, the user): the first plan. WhatsApp in its price (no separate monthly service while it runs),
-- Messenger, Instagram, the chat link and the store's own site; 3000 messages a month for a small store (11,100,000 units = $1.11);
-- 850 EGP a month with no discount of any kind (its own offer, longer periods, «السعر القديم», «دعوة تاجر»).
-- «الأساسية» gets the Shopify / EasyOrders link (it was from «المتقدمة»); every other plan stays as it was, WhatsApp still its own service.
IF COL_LENGTH('dbo.Plans', 'IncludesWa') IS NULL ALTER TABLE dbo.Plans ADD IncludesWa BIT NOT NULL CONSTRAINT DF_Plans_IncludesWa DEFAULT 0,
  NoDiscounts BIT NOT NULL CONSTRAINT DF_Plans_NoDiscounts DEFAULT 0;
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'plan-economy')
BEGIN
  -- through sp_executesql: the two columns are added in this same batch
  EXEC sp_executesql N'
    IF NOT EXISTS (SELECT 1 FROM dbo.Plans WHERE Name = N''الاقتصادية'' AND IsDeleted = 0)
      INSERT INTO dbo.Plans(Name, Description, Features, PriceEgp, UsdBudget, TokenLimit, DurationDays, IsActive, IsFeatured, SortOrder, IncludesWa, NoDiscounts)
      SELECT N''الاقتصادية'', N''كل قنواتك في مكان واحد بسعر ثابت'',
             N''مساعد ذكي بيرد 24 ساعة'' + NCHAR(10) + N''الواتساب جوه الباقة من غير اشتراك زيادة'' + NCHAR(10) + N''المسنجر والإنستجرام'' + NCHAR(10) + N''لينك الشات وتركيبه على موقعك'',
             850, 1.11, 11100000, 30, 1, 0, ISNULL(MIN(SortOrder), 1) - 1, 1, 1
        FROM dbo.Plans WHERE IsDeleted = 0;';
  DECLARE @basicPlan INT = (SELECT TOP 1 Id FROM dbo.Plans WHERE Name = N'الأساسية' AND IsDeleted = 0 ORDER BY Id);
  IF @basicPlan IS NOT NULL
  BEGIN
    UPDATE dbo.Plans SET Features = REPLACE(Features, N'رفع ملفات PDF و Word و Excel', N'رفع ملفات PDF و Word و Excel' + NCHAR(10) + N'ربط متجر Shopify أو EasyOrders')
      WHERE Id = @basicPlan AND Features LIKE N'%رفع ملفات PDF و Word و Excel%' AND Features NOT LIKE N'%Shopify%';
    UPDATE dbo.Plans SET Features = ISNULL(Features + NCHAR(10), N'') + N'ربط متجر Shopify أو EasyOrders'
      WHERE Id = @basicPlan AND (Features IS NULL OR Features NOT LIKE N'%Shopify%');
    MERGE dbo.AppSettings AS t USING (VALUES (N'MinPlan_shopify'), (N'MinPlan_easyorders')) AS x([Key]) ON t.[Key] = x.[Key]
      WHEN MATCHED THEN UPDATE SET [Value] = CAST(@basicPlan AS NVARCHAR(20))
      WHEN NOT MATCHED THEN INSERT ([Key], [Value]) VALUES (x.[Key], CAST(@basicPlan AS NVARCHAR(20)));
  END
  INSERT INTO dbo.Migrations(Name) VALUES ('plan-economy');
END
-- ---------- «ابعت عرض» (Offers.cs, 2026-11-23): the store sends an offer (text, and a picture if it wants) from its WhatsApp number ----------
-- BcContacts: the numbers an offer can go to. One row per number: Added = the owner put it on their own list (manual / excel), or the
-- customer gave it on Messenger / Instagram (messenger / instagram); WaLastAt = the last time it wrote to the store's WhatsApp.
IF OBJECT_ID('dbo.BcContacts') IS NULL
BEGIN
  CREATE TABLE dbo.BcContacts(
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Phone NVARCHAR(20) NOT NULL, Name NVARCHAR(100) NULL,
    Added NVARCHAR(12) NULL, AddedAt DATETIME2 NULL,
    WaLastAt DATETIME2 NULL,
    PRIMARY KEY (StoreId, Phone));
  CREATE INDEX IX_BcContacts_Added ON dbo.BcContacts(StoreId, AddedAt) WHERE Added IS NOT NULL;
END
-- an offer: sent one number at a time, slowly (NextAt = when the next one may go), only while Status = 'sending'.
-- Status: sending / paused / stopped (the owner cancelled the rest) / done
IF OBJECT_ID('dbo.Broadcasts') IS NULL
BEGIN
  CREATE TABLE dbo.Broadcasts(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Text NVARCHAR(1200) NOT NULL, Image VARBINARY(MAX) NULL, ImageType NVARCHAR(20) NULL,
    Audience NVARCHAR(100) NOT NULL, Total INT NOT NULL DEFAULT 0,
    Status NVARCHAR(10) NOT NULL, Note NVARCHAR(300) NULL,
    NextAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), FinishedAt DATETIME2 NULL);
  CREATE INDEX IX_Broadcasts_Store ON dbo.Broadcasts(StoreId, Id);
  CREATE INDEX IX_Broadcasts_Due ON dbo.Broadcasts(Status, NextAt);
END
-- one row per number an offer goes to. Status: waiting / sending (being sent now) / sent (queued at Wapilot) / delivered / failed /
-- skipped (not sent: said «وقف», the offer was cancelled…). WaMsgId = Wapilot's message_id, to learn if it went out.
-- StoppedAt = the customer sent «وقف» after this message.
IF OBJECT_ID('dbo.BcSends') IS NULL
BEGIN
  CREATE TABLE dbo.BcSends(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    BroadcastId INT NOT NULL REFERENCES dbo.Broadcasts(Id) ON DELETE CASCADE,
    StoreId INT NOT NULL,
    Phone NVARCHAR(20) NOT NULL, Name NVARCHAR(100) NULL,
    Status NVARCHAR(10) NOT NULL, Note NVARCHAR(300) NULL, WaMsgId NVARCHAR(100) NULL,
    ClaimedAt DATETIME2 NULL, SentAt DATETIME2 NULL, CheckedAt DATETIME2 NULL, StoppedAt DATETIME2 NULL);
  CREATE INDEX IX_BcSends_Queue ON dbo.BcSends(BroadcastId, Status, Id);
  CREATE INDEX IX_BcSends_Phone ON dbo.BcSends(StoreId, Phone, SentAt);
  CREATE INDEX IX_BcSends_Sent ON dbo.BcSends(StoreId, SentAt) WHERE SentAt IS NOT NULL;
END
-- Messenger / Instagram: «تحب توصلك عروضنا على الواتساب؟» asked once per customer (Stores.BcAsk on). Answer: yes (Phone) / no
IF COL_LENGTH('dbo.Stores', 'BcAsk') IS NULL ALTER TABLE dbo.Stores ADD BcAsk BIT NOT NULL CONSTRAINT DF_Stores_BcAsk DEFAULT 0;
IF OBJECT_ID('dbo.BcAsks') IS NULL
  CREATE TABLE dbo.BcAsks(
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Channel NVARCHAR(4) NOT NULL, ChatId NVARCHAR(64) NOT NULL,
    AskedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), Answer NVARCHAR(4) NULL, Phone NVARCHAR(20) NULL, AnsweredAt DATETIME2 NULL,
    PRIMARY KEY (StoreId, Channel, ChatId));
-- once: everyone who wrote to a store's WhatsApp in the chats kept so far (60 days) goes on its list of WhatsApp customers,
-- and every plan's card says it has «ابعت عرض» (the user: every plan, «الاقتصادية» too)
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'offers')
BEGIN
  INSERT INTO dbo.BcContacts(StoreId, Phone, WaLastAt)
    SELECT m.StoreId, m.ChatId, MAX(m.CreatedAt) FROM dbo.WaMessages m
    WHERE m.FromCustomer = 1 AND LEN(m.ChatId) BETWEEN 8 AND 15 AND m.ChatId NOT LIKE '%[^0-9]%'
      AND NOT EXISTS (SELECT 1 FROM dbo.BcContacts b WHERE b.StoreId = m.StoreId AND b.Phone = m.ChatId)
    GROUP BY m.StoreId, m.ChatId;
  UPDATE dbo.Plans SET Features = ISNULL(Features + NCHAR(10), N'') + N'ابعت عروضك لزباينك على الواتساب'
    WHERE IsDeleted = 0 AND (Features IS NULL OR Features NOT LIKE N'%ابعت عروضك%');
  INSERT INTO dbo.Migrations(Name) VALUES ('offers');
END
-- ---------- Telegram (Telegram.cs, 2026-11-23): the store's own Telegram bot answers its customers ----------
-- TgLinks: one bot per store. Token encrypted (Secrets.Protect); Secret = what Telegram sends back in X-Telegram-Bot-Api-Secret-Token
-- on this bot's webhook (api/tg-webhook.ashx?b=<BotId>). Managed = made through Sellybot's manager bot (OwnerTgId = the Telegram
-- account that made it), else the owner's own bot from @BotFather. Its chats go in dbo.MetaMessages with Channel 'tg'.
IF OBJECT_ID('dbo.TgLinks') IS NULL
BEGIN
  CREATE TABLE dbo.TgLinks(
    StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    BotId BIGINT NOT NULL, Username NVARCHAR(64) NULL, Name NVARCHAR(200) NULL,
    Token NVARCHAR(1000) NOT NULL, Secret NVARCHAR(64) NOT NULL,
    Managed BIT NOT NULL DEFAULT 0, OwnerTgId BIGINT NULL,
    Enabled BIT NOT NULL DEFAULT 1, Status NVARCHAR(400) NULL, LastAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_TgLinks_Bot ON dbo.TgLinks(BotId);   -- a bot answers for one store only
END
-- «اعمل بوت متجرك»: the link t.me/<manager>?start=<Code>, good for 30 minutes. TgUserId = the Telegram account that pressed
-- Start with it (only that account's new bot counts); UsedAt = its bot came; Error = why that bot wasn't linked.
IF OBJECT_ID('dbo.TgStarts') IS NULL
BEGIN
  CREATE TABLE dbo.TgStarts(
    Code NVARCHAR(64) NOT NULL PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    TgUserId BIGINT NULL, StartedAt DATETIME2 NULL, UsedAt DATETIME2 NULL, Error NVARCHAR(400) NULL,
    ExpiresAt DATETIME2 NOT NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_TgStarts_User ON dbo.TgStarts(TgUserId, UsedAt);
  CREATE INDEX IX_TgStarts_Store ON dbo.TgStarts(StoreId, CreatedAt);
END
-- once: every plan's card says Telegram too (open to every plan, like Messenger and Instagram)
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'telegram')
BEGIN
  UPDATE dbo.Plans SET Features = REPLACE(Features, N'المسنجر والإنستجرام', N'المسنجر والإنستجرام وتيليجرام')
    WHERE IsDeleted = 0 AND Features LIKE N'%المسنجر والإنستجرام%' AND Features NOT LIKE N'%تيليجرام%';
  UPDATE dbo.Plans SET Features = ISNULL(Features + NCHAR(10), N'') + N'المساعد على تيليجرام'
    WHERE IsDeleted = 0 AND (Features IS NULL OR Features NOT LIKE N'%تيليجرام%');
  INSERT INTO dbo.Migrations(Name) VALUES ('telegram');
END
-- ---------- «ابعت عرض» on Telegram (2026-11-23, the user): the offer goes to the store's bot's chats too ----------
-- TgContacts: everyone who wrote to the store's bot or pressed Start (kept, like BcContacts.WaLastAt; MetaMessages go after
-- 60 days). StoppedAt + StopWhy: 'stop' = sent «وقف» / /stop after an offer; 'blocked' = blocked the bot (cleared when they write again).
IF OBJECT_ID('dbo.TgContacts') IS NULL
  CREATE TABLE dbo.TgContacts(
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    ChatId NVARCHAR(20) NOT NULL, Name NVARCHAR(100) NULL,
    LastAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), StoppedAt DATETIME2 NULL, StopWhy NVARCHAR(8) NULL,
    PRIMARY KEY (StoreId, ChatId));
-- an offer's row says where it goes: 'wa' (Phone = the number) or 'tg' (Phone = the Telegram chat id)
IF COL_LENGTH('dbo.BcSends', 'Channel') IS NULL ALTER TABLE dbo.BcSends ADD Channel NVARCHAR(4) NOT NULL CONSTRAINT DF_BcSends_Channel DEFAULT 'wa';
-- the offer's picture as Telegram keeps it after the first send (file_id), so it's uploaded once per offer
IF COL_LENGTH('dbo.Broadcasts', 'TgPhoto') IS NULL ALTER TABLE dbo.Broadcasts ADD TgPhoto NVARCHAR(300) NULL;
-- once: the chats the bots had before this (customers' messages still kept)
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'tg-offers')
BEGIN
  INSERT INTO dbo.TgContacts(StoreId, ChatId, LastAt)
    SELECT StoreId, ChatId, MAX(CreatedAt) FROM dbo.MetaMessages WHERE Channel = 'tg' AND FromCustomer = 1 AND LEN(ChatId) <= 20 GROUP BY StoreId, ChatId;
  INSERT INTO dbo.Migrations(Name) VALUES ('tg-offers');
END
-- ---------- more than one WhatsApp number + the store's team (WaLines.cs, 2026-11-24, the user) ----------
-- WaLines: the store's extra WhatsApp numbers (the main one stays in Stores.WaInstance / WaNumber, line 0). Each is a number on
-- Sellybot's Wapilot account linked by QR. Want = the number the owner said they'll link; Number = the phone actually linked;
-- Staff = who holds it (optional). The bot answers on every line the subscription covers (WaLines.Allowed).
IF OBJECT_ID('dbo.WaLines') IS NULL
BEGIN
  CREATE TABLE dbo.WaLines(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Instance NVARCHAR(150) NULL, Number NVARCHAR(20) NULL, Want NVARCHAR(20) NULL, Staff NVARCHAR(60) NULL,
    Status NVARCHAR(400) NULL, LastAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_WaLines_Store ON dbo.WaLines(StoreId, Id);
  CREATE UNIQUE INDEX UX_WaLines_Instance ON dbo.WaLines(Instance) WHERE Instance IS NOT NULL;
END
-- WaStaff: who holds the main number · WaCount: how many numbers the WhatsApp service is paid for (300 for one, 260 each for more)
-- ReportLine: the line the reports and the owner's alerts go out from (0 = the main number) · TeamTurn: whose turn the next alert is
IF COL_LENGTH('dbo.Stores', 'WaStaff') IS NULL ALTER TABLE dbo.Stores ADD WaStaff NVARCHAR(60) NULL,
  WaCount INT NOT NULL CONSTRAINT DF_Stores_WaCount DEFAULT 1, ReportLine INT NOT NULL CONSTRAINT DF_Stores_ReportLine DEFAULT 0,
  TeamTurn INT NOT NULL CONSTRAINT DF_Stores_TeamTurn DEFAULT 0;
-- which number a message came to / went out from (0 = the main number)
IF COL_LENGTH('dbo.WaMessages', 'Line') IS NULL ALTER TABLE dbo.WaMessages ADD Line INT NOT NULL CONSTRAINT DF_WaMessages_Line DEFAULT 0;
-- a handoff: the number the customer wrote to, and who on the team the alert went to
IF COL_LENGTH('dbo.Handoffs', 'Line') IS NULL ALTER TABLE dbo.Handoffs ADD Line INT NULL, ToName NVARCHAR(60) NULL, ToPhone NVARCHAR(20) NULL;
-- how many numbers a WhatsApp request / payment is for
IF COL_LENGTH('dbo.PlanRequests', 'WaCount') IS NULL ALTER TABLE dbo.PlanRequests ADD WaCount INT NULL;
IF COL_LENGTH('dbo.Subscriptions', 'WaCount') IS NULL ALTER TABLE dbo.Subscriptions ADD WaCount INT NULL;
-- ---------- following up an order confirmation nobody answered (Followup.cs, 2026-11-24) ----------
-- Stores.OcFollow: hours before the reminder, and again before the team is told to call (0 = off). RemindedAt: the reminder went;
-- AlertedAt / AlertTo: still no answer, and who was told to call the customer
IF COL_LENGTH('dbo.Stores', 'OcFollow') IS NULL ALTER TABLE dbo.Stores ADD OcFollow INT NOT NULL CONSTRAINT DF_Stores_OcFollow DEFAULT 3;
IF COL_LENGTH('dbo.OrderConfirms', 'RemindedAt') IS NULL ALTER TABLE dbo.OrderConfirms ADD RemindedAt DATETIME2 NULL, AlertedAt DATETIME2 NULL, AlertTo NVARCHAR(80) NULL;
IF COL_LENGTH('dbo.EoConfirms', 'RemindedAt') IS NULL ALTER TABLE dbo.EoConfirms ADD RemindedAt DATETIME2 NULL, AlertedAt DATETIME2 NULL, AlertTo NVARCHAR(80) NULL;
-- ---------- the morning summary on WhatsApp (WeeklyReport.cs): every day at 9, next to the weekly report ----------
IF COL_LENGTH('dbo.Stores', 'ReportDaily') IS NULL ALTER TABLE dbo.Stores ADD ReportDaily BIT NOT NULL CONSTRAINT DF_Stores_ReportDaily DEFAULT 0, ReportDayAt DATETIME2 NULL;
-- ---------- «فكّر معانا» (Council.cs, 2026-11-24, the user): its own prepaid tokens, apart from the plan ----------
-- CouncilWallets: what's left (Balance, never below 0, no expiry) and the free tokens it started with · CouncilLedger: every change
-- (free / topup / session / follow / gift) · CouncilSessions: an idea and the council's answer, or (ParentId) a follow-up on one and
-- its reply; Tokens = what Claude used, Charged = what the wallet paid (0 without an answer), Units = Claude's cost the way Finance counts it · CouncilTopups: paid by transfer, approved by the
-- super admin (-> Subscriptions, Kind 'council'). One payment backs one request, here and in PlanRequests (Billing.RefUsed).
IF OBJECT_ID('dbo.CouncilWallets') IS NULL
  CREATE TABLE dbo.CouncilWallets(
    StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Balance BIGINT NOT NULL DEFAULT 0, Free BIGINT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.CouncilLedger') IS NULL
BEGIN
  CREATE TABLE dbo.CouncilLedger(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Kind NVARCHAR(10) NOT NULL, Tokens BIGINT NOT NULL, RefId INT NULL, Note NVARCHAR(200) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE INDEX IX_CouncilLedger_Store ON dbo.CouncilLedger(StoreId, Id);
END
IF OBJECT_ID('dbo.CouncilSessions') IS NULL
BEGIN
  CREATE TABLE dbo.CouncilSessions(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Idea NVARCHAR(3000) NOT NULL, UseData BIT NOT NULL DEFAULT 1, ParentId INT NULL,   -- ParentId: the idea this message follows up on
    Status NVARCHAR(10) NOT NULL DEFAULT 'running',     -- running / done / failed / no (not a decision, not about this idea)
    Answer NVARCHAR(MAX) NULL,
    InTok INT NOT NULL DEFAULT 0, CacheWrite INT NOT NULL DEFAULT 0, CacheRead INT NOT NULL DEFAULT 0, OutTok INT NOT NULL DEFAULT 0,
    Tokens INT NOT NULL DEFAULT 0, Charged INT NOT NULL DEFAULT 0, Units BIGINT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), DoneAt DATETIME2 NULL);
  CREATE INDEX IX_CouncilSessions_Store ON dbo.CouncilSessions(StoreId, Id);
END
IF COL_LENGTH('dbo.CouncilSessions', 'ParentId') IS NULL ALTER TABLE dbo.CouncilSessions ADD ParentId INT NULL;
IF OBJECT_ID('dbo.CouncilTopups') IS NULL
BEGIN
  CREATE TABLE dbo.CouncilTopups(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    AmountEgp DECIMAL(12,2) NOT NULL, Tokens BIGINT NOT NULL, Per1k DECIMAL(12,4) NOT NULL,   -- the price per 1000 tokens the owner saw
    PaymentMethod NVARCHAR(40) NULL, PaymentRef NVARCHAR(100) NULL, RefNo NVARCHAR(40) NOT NULL, Note NVARCHAR(500) NULL,
    Status NVARCHAR(12) NOT NULL DEFAULT 'pending', AdminNote NVARCHAR(400) NULL, SubscriptionId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), DecidedAt DATETIME2 NULL);
  CREATE INDEX IX_CouncilTopups_Store ON dbo.CouncilTopups(StoreId, Id);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_CouncilTopups_RefNo ON dbo.CouncilTopups(RefNo) WHERE Status IN (''pending'', ''approved'');';
END
-- Every run of the background jobs (Jobs.cs), kept 8 days: when it started, what started it (timer / health / cron / now), how long it took
IF OBJECT_ID('dbo.JobRuns') IS NULL
BEGIN
  CREATE TABLE dbo.JobRuns(Id BIGINT IDENTITY(1,1) PRIMARY KEY, At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), Src NVARCHAR(10) NOT NULL, Ms INT NULL);
  CREATE INDEX IX_JobRuns_At ON dbo.JobRuns(At) INCLUDE (Src, Ms);
END
-- ---------- «زبايني» (Crm.cs, 2026-11-25, the user): a file per customer, ready-made groups, the returns alert ----------
-- CrmOrders: the store's orders (shopify / eo / excel / hand), one row each. Status: open / shipped / delivered / returned / cancelled.
-- ByHand = the owner set the status (the store's system doesn't change it any more) · Gone = the customer was erased (no name or
-- number is kept, nor brought back) · Alert: due / shown / sent / failed / late (the returns alert), ok = the owner checked it.
IF OBJECT_ID('dbo.CrmOrders') IS NULL
BEGIN
  CREATE TABLE dbo.CrmOrders(
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE,
    Source NVARCHAR(10) NOT NULL, OrderId NVARCHAR(64) NOT NULL,
    OrderName NVARCHAR(40) NULL, Phone NVARCHAR(20) NULL, Customer NVARCHAR(100) NULL, City NVARCHAR(80) NULL,
    Total DECIMAL(12,2) NULL, Currency NVARCHAR(8) NULL, Items NVARCHAR(300) NULL,
    Status NVARCHAR(10) NOT NULL DEFAULT 'open', ByHand BIT NOT NULL DEFAULT 0, Gone BIT NOT NULL DEFAULT 0,
    Alert NVARCHAR(10) NULL, AlertNote NVARCHAR(200) NULL,
    OrderedAt DATETIME2 NOT NULL, StatusAt DATETIME2 NULL, CheckedAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
  CREATE UNIQUE INDEX UX_CrmOrders_Order ON dbo.CrmOrders(StoreId, Source, OrderId);   -- a webhook may come twice, and the pull sees it again
  CREATE INDEX IX_CrmOrders_Phone ON dbo.CrmOrders(StoreId, Phone) INCLUDE (Status, Total, OrderedAt);
  CREATE INDEX IX_CrmOrders_Alert ON dbo.CrmOrders(Alert) WHERE Alert IS NOT NULL;
END
-- what the owner wrote about a customer. Mark: star (مهم) / follow (محتاج متابعة) / whole (جملة) / block (ماتشحنلوش)
IF OBJECT_ID('dbo.CrmNotes') IS NULL
  CREATE TABLE dbo.CrmNotes(
    StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, Phone NVARCHAR(20) NOT NULL,
    Name NVARCHAR(100) NULL, Note NVARCHAR(1000) NULL, Mark NVARCHAR(10) NULL,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), PRIMARY KEY (StoreId, Phone));
-- CrmAlert: the returns alert on WhatsApp · CrmShopAt: Shopify's orders are known up to here · CrmTryAt: the last pull started ·
-- CrmHooked: new Shopify orders come by webhook. WaOptOuts.ByOwner: the owner stopped this number's messages, not the customer («وقف»)
IF COL_LENGTH('dbo.Stores', 'CrmAlert') IS NULL ALTER TABLE dbo.Stores ADD CrmAlert BIT NOT NULL CONSTRAINT DF_Stores_CrmAlert DEFAULT 1,
  CrmShopAt DATETIME2 NULL, CrmTryAt DATETIME2 NULL, CrmHooked BIT NOT NULL CONSTRAINT DF_Stores_CrmHooked DEFAULT 0;
IF COL_LENGTH('dbo.WaOptOuts', 'ByOwner') IS NULL ALTER TABLE dbo.WaOptOuts ADD ByOwner BIT NOT NULL CONSTRAINT DF_WaOptOuts_ByOwner DEFAULT 0;
-- once: the orders the confirmation messages know already, and the plans (the file: every plan; the groups and the returns alert: from «المتقدمة»)
IF NOT EXISTS (SELECT 1 FROM dbo.Migrations WHERE Name = 'crm')
BEGIN
  BEGIN TRY   -- best effort: the site starts either way
  INSERT INTO dbo.CrmOrders(StoreId, Source, OrderId, OrderName, Phone, Customer, Total, Currency, Status, OrderedAt, StatusAt)
    SELECT o.StoreId, 'shopify', CAST(o.OrderId AS NVARCHAR(64)), o.OrderName, CASE WHEN LEN(o.Phone)=10 AND o.Phone LIKE '1%' THEN '20' + o.Phone ELSE o.Phone END, o.Customer,
      CASE WHEN v.N LIKE '[0-9]%' AND v.N NOT LIKE '%[^0-9.]%' AND LEN(v.N) < 11 THEN CAST(v.N AS DECIMAL(12,2)) END, CASE WHEN o.Total LIKE N'% EGP' THEN N'ج.م' ELSE NULLIF(LEFT(LTRIM(SUBSTRING(o.Total, CHARINDEX(' ', o.Total + ' '), 20)), 8), '') END, CASE WHEN o.Status='cancelled' THEN 'cancelled' ELSE 'open' END, o.CreatedAt, o.AnsweredAt
    FROM dbo.OrderConfirms o CROSS APPLY (SELECT REPLACE(LEFT(o.Total, CHARINDEX(' ', o.Total + ' ') - 1), ',', '') AS N) v
    WHERE NOT EXISTS (SELECT 1 FROM dbo.CrmOrders x WHERE x.StoreId=o.StoreId AND x.Source='shopify' AND x.OrderId=CAST(o.OrderId AS NVARCHAR(64)));
  INSERT INTO dbo.CrmOrders(StoreId, Source, OrderId, OrderName, Phone, Customer, Total, Status, OrderedAt, StatusAt)
    SELECT o.StoreId, 'eo', o.OrderId, o.OrderName, CASE WHEN LEN(o.Phone)=10 AND o.Phone LIKE '1%' THEN '20' + o.Phone ELSE o.Phone END, o.Customer,
      CASE WHEN v.N LIKE '[0-9]%' AND v.N NOT LIKE '%[^0-9.]%' AND LEN(v.N) < 11 THEN CAST(v.N AS DECIMAL(12,2)) END, CASE WHEN o.Status='cancelled' THEN 'cancelled' ELSE 'open' END, o.CreatedAt, o.AnsweredAt
    FROM dbo.EoConfirms o CROSS APPLY (SELECT REPLACE(LEFT(o.Total, CHARINDEX(' ', o.Total + ' ') - 1), ',', '') AS N) v
    WHERE NOT EXISTS (SELECT 1 FROM dbo.CrmOrders x WHERE x.StoreId=o.StoreId AND x.Source='eo' AND x.OrderId=o.OrderId);
  END TRY BEGIN CATCH END CATCH
  DECLARE @advPlan INT = (SELECT TOP 1 Id FROM dbo.Plans WHERE Name = N'المتقدمة' AND IsDeleted = 0 ORDER BY Id);
  IF @advPlan IS NOT NULL
    MERGE dbo.AppSettings AS t USING (VALUES (N'MinPlan_crmgroups'), (N'MinPlan_returns')) AS x([Key]) ON t.[Key] = x.[Key]
      WHEN NOT MATCHED THEN INSERT ([Key], [Value]) VALUES (x.[Key], CAST(@advPlan AS NVARCHAR(20)));
  UPDATE dbo.Plans SET Features = ISNULL(Features + NCHAR(10), N'') + N'«زبايني»: ملف لكل زبون بأوردراته'
    WHERE IsDeleted = 0 AND (Features IS NULL OR (Features NOT LIKE N'%«زبايني»%' AND Features NOT LIKE N'%كل مميزات%'));
  UPDATE p SET Features = p.Features + NCHAR(10) + N'مجموعات زباين جاهزة وتنبيه المرتجع'
    FROM dbo.Plans p WHERE p.IsDeleted = 0 AND p.IncludesWa = 0 AND p.Features NOT LIKE N'%تنبيه المرتجع%' AND p.Features NOT LIKE N'%كل مميزات المتقدمة%'
      AND (@advPlan IS NULL OR p.SortOrder >= (SELECT SortOrder FROM dbo.Plans WHERE Id = @advPlan));
  INSERT INTO dbo.Migrations(Name) VALUES ('crm');
END";
    }
}
