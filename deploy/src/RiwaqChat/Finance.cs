// The super admin's «💰 المكسب والتكلفة» page (admin.html#money, api/superadmin.ashx?action=money): what Sellybot earns and
// spends, how much to charge the Claude API with, what each plan and each store brings in, and the numbers a calculator for
// any number of stores needs (it runs in the page).
// Claude's cost comes from the tokens every reply used (UsageMonthly / UsageDaily), in the cost-weighted units the plans'
// budgets are counted in: Claude Haiku 5.5 prices, $0.10 per million input tokens (Billing.UsdForUnits).
// Settings (AppSettings): FinClaudeBalanceUsd + FinClaudeBalanceAt + FinClaudeBalanceUnits (the API credit the admin typed, when,
// and every store's units at that moment, so what's left = typed − what was used since), FinBufferPct (spare % on top of the
// expected spend), FinWaCostEgp (what one linked WhatsApp number costs a month at the provider), FinFixedEgp (hosting etc. a month).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Web;

namespace RiwaqChat
{
    public static class Finance
    {
        // cost-weighted units of a UsageMonthly row (alias a), same ratios as UsageInfo.Weighted
        static string W(string a) { return "(" + a + ".InputTokens + CAST(" + a + ".CacheWriteTokens * 1.25 AS BIGINT) + CAST(" + a + ".CacheReadTokens * 0.1 AS BIGINT) + " + a + ".OutputTokens * 5)"; }
        static decimal Num(SqlConnection c, string key, decimal fallback) { return Billing.Dec(Billing.Get(c, key), fallback); }
        static decimal R(decimal v, int d = 2) { return Math.Round(v, d); }

        class Row
        {
            public int Id; public string Name, Slug, Plan; public int? PlanId; public bool Admin, Own, Active, Trial, TrialLive, Wa, WaLinked, Ending7, Ending30, Churned;
            public DateTime Created; public DateTime? Ends;
            public int Msgs, LastMsgs; public long Units, LastUnits, Limit, TopUnits;
            public decimal PerMonth, PaidMonth;
        }

        public static Dictionary<string, object> Report(SqlConnection c)
        {
            var now = DateTime.UtcNow; var rate = Billing.UsdRate(c);
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            int daysIn = DateTime.DaysInMonth(now.Year, now.Month);
            double elapsed = Math.Max(0.5, (now - monthStart).TotalDays), left = Math.Max(0, daysIn - elapsed);
            string month = Db.Month, lastMonth = monthStart.AddMonths(-1).ToString("yyyy-MM");
            int free = Billing.FreeMessages(c), freeDays = Billing.FreeDays(c);
            decimal waPrice = WaAccount.ServicePrice(c);
            decimal buffer = Num(c, "FinBufferPct", 20), waCost = Num(c, "FinWaCostEgp", 0), fixedCost = Num(c, "FinFixedEgp", 0);
            var plans = Billing.Plans(c, true).ToDictionary(p => p.Id);

            // ---------- every store: this month and last month, what it pays a month ----------
            var rows = new List<Row>();
            using (var cmd = Db.Cmd(c, @"SELECT s.Id, s.Name, s.Slug, s.IsAdmin, s.PlanId, s.PlanName, s.PlanEndsAt, s.NextPlanId, s.CreatedAt, s.WaUntil, s.WaInstance, s.MonthlyTokenLimit,
                  ISNULL(u.Messages,0) AS M, ISNULL(" + W("u") + @",0) AS U, ISNULL(l.Messages,0) AS LM, ISNULL(" + W("l") + @",0) AS LU,
                  (SELECT TOP 1 x.PaidEgp / x.Months FROM dbo.Subscriptions x WHERE x.StoreId = s.Id AND x.Kind = 'plan' AND x.Months > 0 ORDER BY x.StartsAt DESC) AS PerMonth,
                  (SELECT ISNULL(SUM(x.PaidEgp),0) FROM dbo.Subscriptions x WHERE x.StoreId = s.Id AND x.CreatedAt >= @ms) AS PaidMonth,
                  (SELECT ISNULL(SUM(r.Units),0) FROM dbo.PlanRequests r WHERE r.StoreId = s.Id AND r.Kind = 'topup' AND r.Status = 'approved' AND r.Month = @m) AS TopUnits
                FROM dbo.Stores s LEFT JOIN dbo.UsageMonthly u ON u.StoreId = s.Id AND u.Month = @m LEFT JOIN dbo.UsageMonthly l ON l.StoreId = s.Id AND l.Month = @lm",
                "@m", month, "@lm", lastMonth, "@ms", monthStart))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var x = new Row
                    {
                        Id = (int)r["Id"], Name = (string)r["Name"], Slug = (string)r["Slug"], Admin = (bool)r["IsAdmin"],
                        PlanId = r["PlanId"] as int?, Plan = r["PlanName"] as string,
                        Ends = r["PlanEndsAt"] is DateTime ? DateTime.SpecifyKind((DateTime)r["PlanEndsAt"], DateTimeKind.Utc) : (DateTime?)null,
                        Created = DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc),
                        Msgs = Convert.ToInt32(r["M"]), Units = Convert.ToInt64(r["U"]), LastMsgs = Convert.ToInt32(r["LM"]), LastUnits = Convert.ToInt64(r["LU"]),
                        Limit = Convert.ToInt64(r["MonthlyTokenLimit"]), TopUnits = Convert.ToInt64(r["TopUnits"]),
                        PerMonth = r["PerMonth"] == DBNull.Value ? 0 : Convert.ToDecimal(r["PerMonth"]), PaidMonth = Convert.ToDecimal(r["PaidMonth"]),
                        WaLinked = !string.IsNullOrEmpty(r["WaInstance"] as string),
                    };
                    x.Own = x.Admin || x.Slug == SiteBot.Slug;   // the platform's own stores (and سيلّي): a cost, never revenue
                    x.Active = x.PlanId.HasValue && x.Ends.HasValue && x.Ends.Value >= now && !x.Own;
                    x.Trial = !x.PlanId.HasValue && !x.Own;
                    x.TrialLive = x.Trial && x.Created.AddDays(freeDays) > now && x.Msgs < free;
                    x.Wa = !x.Own && r["WaUntil"] is DateTime && DateTime.SpecifyKind((DateTime)r["WaUntil"], DateTimeKind.Utc) > now;
                    bool waiting = r["NextPlanId"] != DBNull.Value;   // a lower plan already paid for: it renews itself
                    x.Ending7 = x.Active && !waiting && x.Ends.Value < now.AddDays(7);
                    x.Ending30 = x.Active && !waiting && x.Ends.Value < now.AddDays(30);
                    x.Churned = x.PlanId.HasValue && !x.Own && x.Ends.HasValue && x.Ends.Value < now && x.Ends.Value >= now.AddDays(-30);
                    // a store that never paid through a request (set by hand) is priced at its plan's list price
                    if (x.PlanId.HasValue && x.PerMonth <= 0 && plans.ContainsKey(x.PlanId.Value))
                    {
                        var p = plans[x.PlanId.Value];
                        x.PerMonth = Billing.FinalPrice(p) * 30m / Math.Max(1, p.DurationDays);
                    }
                    rows.Add(x);
                }

            // ---------- this month's tokens, all stores ----------
            long inTok = 0, cr = 0, cw = 0, outTok = 0;
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(InputTokens),0), ISNULL(SUM(CacheReadTokens),0), ISNULL(SUM(CacheWriteTokens),0), ISNULL(SUM(OutputTokens),0) FROM dbo.UsageMonthly WHERE Month=@m", "@m", month))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { inTok = Convert.ToInt64(r[0]); cr = Convert.ToInt64(r[1]); cw = Convert.ToInt64(r[2]); outTok = Convert.ToInt64(r[3]); }
            long unitsMonth = rows.Sum(x => x.Units), unitsLast = rows.Sum(x => x.LastUnits);
            int msgsMonth = rows.Sum(x => x.Msgs), msgsLast = rows.Sum(x => x.LastMsgs);
            // units one reply costs: this month's when there's enough of it, else last month's, else the medium-data estimate
            long perMsg = msgsMonth >= 20 && unitsMonth > 0 ? unitsMonth / msgsMonth : msgsLast >= 20 && unitsLast > 0 ? unitsLast / msgsLast : Billing.Units(c, "UnitsMedium");
            perMsg = Math.Max(1, perMsg);
            decimal claudeUsd = Billing.UsdForUnits(unitsMonth);
            // what the cache saved: every cached token read would have been a full-price input token
            decimal cacheSavedUsd = Billing.UsdForUnits((long)(cr * 0.9));
            double cacheShare = inTok + cr + cw > 0 ? (double)cr / (inTok + cr + cw) : 0;

            // ---------- the burn per day (UsageDaily: units since 2026-11-21, older days estimated from their replies) ----------
            var today = now.Date;
            Func<int, decimal> burn = days =>
            {
                long units = 0;
                using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(Messages),0) AS M, ISNULL(SUM(Units),0) AS U FROM dbo.UsageDaily WHERE Day >= @f AND Day < @t GROUP BY Day",
                    "@f", today.AddDays(-days), "@t", today))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) { long u = Convert.ToInt64(r["U"]); units += u > 0 ? u : Convert.ToInt64(r["M"]) * perMsg; }
                return Billing.UsdForUnits(units) / days;
            };
            decimal burn7 = burn(7), burn30 = burn(30);
            decimal perDay = burn7 > 0 ? burn7 : burn30 > 0 ? burn30 : claudeUsd / (decimal)elapsed;
            decimal claudeProjUsd = claudeUsd + perDay * (decimal)left;

            // ---------- the API credit the admin typed, minus what was used since ----------
            decimal? balance = null, remaining = null; string balanceAt = Billing.Get(c, "FinClaudeBalanceAt") ?? "";
            decimal typed;
            if (decimal.TryParse(Billing.Get(c, "FinClaudeBalanceUsd") ?? "", NumberStyles.Number, CultureInfo.InvariantCulture, out typed))
            {
                balance = typed;
                long snap; long.TryParse(Billing.Get(c, "FinClaudeBalanceUnits") ?? "0", out snap);
                remaining = typed - Billing.UsdForUnits(Math.Max(0, TotalUnits(c) - snap));
            }
            decimal next30 = perDay * 30, next30Buf = next30 * (1 + buffer / 100m);
            // the most this month could cost: every paid store uses its whole plan (+ extra bought), every live trial its free replies
            long worstUnits = rows.Where(x => x.Active).Sum(x => x.Limit + x.TopUnits) + rows.Where(x => x.TrialLive).Sum(x => (long)free * perMsg)
                            + rows.Where(x => x.Own).Sum(x => Math.Max(x.Limit, x.Units));
            decimal worstUsd = Billing.UsdForUnits(worstUnits);
            decimal suggest = Math.Max(0, next30Buf - (remaining ?? 0));
            suggest = Math.Ceiling(suggest / 5m) * 5m;   // the console sells credit in round amounts

            // ---------- money in ----------
            var revByKind = new Dictionary<string, decimal> { { "plan", 0 }, { "upgrade", 0 }, { "topup", 0 }, { "wa", 0 } };
            using (var cmd = Db.Cmd(c, "SELECT Kind, ISNULL(SUM(PaidEgp),0) FROM dbo.Subscriptions WHERE CreatedAt >= @ms GROUP BY Kind", "@ms", monthStart))
            using (var r = cmd.ExecuteReader()) while (r.Read()) { var k = (string)r[0]; revByKind[revByKind.ContainsKey(k) ? k : "plan"] += Convert.ToDecimal(r[1]); }
            decimal revenue = revByKind.Values.Sum();
            int waNumbers = rows.Count(x => x.WaLinked);
            decimal waCostMonth = waNumbers * waCost;
            decimal claudeEgp = claudeUsd * rate, claudeProjEgp = claudeProjUsd * rate;
            decimal mrrPlans = rows.Where(x => x.Active).Sum(x => x.PerMonth), mrrWa = rows.Count(x => x.Wa) * waPrice, mrr = mrrPlans + mrrWa;
            decimal monthlyCosts = next30 * rate + waCostMonth + fixedCost;

            // ---------- each plan ----------
            var planRows = new List<Dictionary<string, object>>();
            foreach (var p in plans.Values.Where(p => p.IsActive).OrderBy(p => p.SortOrder).ThenBy(p => p.PriceEgp))
            {
                var on = rows.Where(x => x.Active && x.PlanId == p.Id).ToList();
                long lim = on.Sum(x => x.Limit), proj = (long)(on.Sum(x => x.Units) * (daysIn / elapsed));
                decimal price = Billing.FinalPrice(p) * 30m / Math.Max(1, p.DurationDays), maxCost = p.UsdBudget * rate;
                decimal avgCost = on.Count > 0 ? Billing.UsdForUnits(proj) * rate / on.Count : 0;
                planRows.Add(new Dictionary<string, object> {
                    { "id", p.Id }, { "name", p.Name }, { "price", R(price) }, { "usdBudget", p.UsdBudget }, { "maxCost", R(maxCost) },
                    { "units", p.TokenLimit }, { "msgs", p.TokenLimit / perMsg }, { "stores", on.Count },
                    { "usePct", lim > 0 ? Math.Round(proj * 100.0 / lim, 1) : (double?)null },
                    { "lastPct", lim > 0 && on.Any(x => x.LastUnits > 0) ? Math.Round(on.Sum(x => x.LastUnits) * 100.0 / lim, 1) : (double?)null },
                    { "avgCost", R(avgCost) }, { "marginMin", R(price - maxCost) }, { "marginExp", on.Count > 0 ? R(price - avgCost) : (decimal?)null } });
            }

            // ---------- each store (the ones that cost or pay something) ----------
            var storeRows = rows.Where(x => x.Units > 0 || x.Active || x.Wa || x.PaidMonth > 0).Select(x =>
            {
                long lim = x.Active ? x.Limit + x.TopUnits : x.TrialLive ? (long)free * perMsg : x.Own ? x.Limit : 0;
                decimal cost = Billing.UsdForUnits(x.Units) * rate, proj = Billing.UsdForUnits((long)(x.Units * (daysIn / elapsed))) * rate;
                decimal pays = (x.Active ? x.PerMonth : 0) + (x.Wa ? waPrice : 0);
                return new Dictionary<string, object> {
                    { "id", x.Id }, { "name", x.Name }, { "slug", x.Slug }, { "plan", x.Active ? x.Plan : null }, { "trial", x.Trial }, { "own", x.Own }, { "wa", x.Wa },
                    { "msgs", x.Msgs }, { "cost", R(cost) }, { "proj", R(proj) }, { "pays", R(pays) },
                    { "margin", x.Own ? (decimal?)null : R(pays - proj - (x.Wa && x.WaLinked ? waCost : 0)) },
                    { "usePct", lim > 0 ? Math.Round(x.Units * 100.0 / lim, 1) : (double?)null }, { "paidMonth", R(x.PaidMonth) } };
            }).OrderByDescending(d => (decimal)d["cost"]).ToList();

            // ---------- the last 6 months ----------
            var months = Enumerable.Range(0, 6).Select(i => monthStart.AddMonths(-5 + i).ToString("yyyy-MM")).ToList();
            var hist = months.ToDictionary(m => m, m => new Dictionary<string, object> { { "month", m }, { "plan", 0m }, { "topup", 0m }, { "wa", 0m }, { "msgs", 0 }, { "claude", 0m } });
            using (var cmd = Db.Cmd(c, "SELECT CONVERT(CHAR(7), CreatedAt, 126), Kind, SUM(PaidEgp) FROM dbo.Subscriptions WHERE CreatedAt >= @f GROUP BY CONVERT(CHAR(7), CreatedAt, 126), Kind", "@f", monthStart.AddMonths(-5)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var m = (string)r[0]; if (!hist.ContainsKey(m)) continue;
                    string k = (string)r[1] == "topup" ? "topup" : (string)r[1] == "wa" ? "wa" : "plan";
                    hist[m][k] = (decimal)hist[m][k] + Convert.ToDecimal(r[2]);
                }
            using (var cmd = Db.Cmd(c, "SELECT u.Month, SUM(u.Messages), SUM(" + W("u") + ") FROM dbo.UsageMonthly u WHERE u.Month >= @f GROUP BY u.Month", "@f", months[0]))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) { var m = (string)r[0]; if (hist.ContainsKey(m)) { hist[m]["msgs"] = Convert.ToInt32(r[1]); hist[m]["claude"] = R(Billing.UsdForUnits(Convert.ToInt64(r[2])) * rate); } }

            // ---------- trials, renewals, requests waiting, replies per channel ----------
            var stores90 = rows.Where(x => !x.Own && x.Created >= now.AddDays(-90)).ToList();
            int converted90 = 0;
            if (stores90.Count > 0)
                using (var cmd = Db.Cmd(c, "SELECT COUNT(DISTINCT s.Id) FROM dbo.Stores s JOIN dbo.Subscriptions x ON x.StoreId = s.Id AND x.Kind = 'plan' WHERE s.CreatedAt >= @f AND s.IsAdmin = 0 AND s.Slug <> @site",
                    "@f", now.AddDays(-90), "@site", SiteBot.Slug)) converted90 = Convert.ToInt32(cmd.ExecuteScalar());
            int pendingCount = 0; decimal pendingEgp = 0;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*), ISNULL(SUM(AmountEgp),0) FROM dbo.PlanRequests WHERE Status = 'pending'"))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { pendingCount = Convert.ToInt32(r[0]); pendingEgp = Convert.ToDecimal(r[1]); }
            int waReplies = 0, fbReplies = 0, igReplies = 0;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaMessages WHERE FromCustomer = 0 AND CreatedAt >= @ms", "@ms", monthStart)) waReplies = Convert.ToInt32(cmd.ExecuteScalar());
            using (var cmd = Db.Cmd(c, "SELECT Channel, COUNT(*) FROM dbo.MetaMessages WHERE FromCustomer = 0 AND MsgId IS NULL AND CreatedAt >= @ms GROUP BY Channel", "@ms", monthStart))
            using (var r = cmd.ExecuteReader()) while (r.Read()) { if ((string)r[0] == MetaApp.Fb) fbReplies = Convert.ToInt32(r[1]); else igReplies = Convert.ToInt32(r[1]); }
            var trialRows = rows.Where(x => x.Trial && x.Units > 0).ToList();

            return new Dictionary<string, object>
            {
                { "model", Claude.Model }, { "usdRate", rate }, { "month", month }, { "daysIn", daysIn }, { "elapsed", Math.Round(elapsed, 1) },
                { "perMsgUnits", perMsg }, { "msgCost", R(Billing.UsdForUnits(perMsg) * rate, 4) }, { "freeMessages", free }, { "waPrice", waPrice },
                { "settings", new Dictionary<string, object> { { "balanceUsd", balance }, { "balanceAt", balanceAt }, { "bufferPct", buffer }, { "waCostEgp", waCost }, { "fixedEgp", fixedCost } } },
                { "now", new Dictionary<string, object> {
                    { "revenue", R(revenue) }, { "byKind", revByKind.ToDictionary(k => k.Key, k => (object)R(k.Value)) },
                    { "claudeUsd", R(claudeUsd, 4) }, { "claudeEgp", R(claudeEgp) }, { "claudeProjUsd", R(claudeProjUsd, 4) }, { "claudeProjEgp", R(claudeProjEgp) },
                    { "waNumbers", waNumbers }, { "waCost", R(waCostMonth) }, { "fixed", R(fixedCost) },
                    { "profit", R(revenue - claudeEgp - waCostMonth - fixedCost) }, { "messages", msgsMonth },
                    { "cacheSavedUsd", R(cacheSavedUsd, 4) }, { "cacheShare", Math.Round(cacheShare * 100, 1) },
                    { "costPaid", R(Billing.UsdForUnits(rows.Where(x => x.Active).Sum(x => x.Units)) * rate) },
                    { "costTrial", R(Billing.UsdForUnits(rows.Where(x => x.Trial).Sum(x => x.Units)) * rate) },
                    { "costOwn", R(Billing.UsdForUnits(rows.Where(x => x.Own).Sum(x => x.Units)) * rate) },
                    { "channels", new Dictionary<string, object> { { "site", Math.Max(0, msgsMonth - waReplies - fbReplies - igReplies) }, { "wa", waReplies }, { "fb", fbReplies }, { "ig", igReplies } } } } },
                { "mrr", new Dictionary<string, object> { { "plans", R(mrrPlans) }, { "wa", R(mrrWa) }, { "total", R(mrr) }, { "stores", rows.Count(x => x.Active) }, { "waStores", rows.Count(x => x.Wa) },
                    { "costs", R(monthlyCosts) }, { "profit", R(mrr - monthlyCosts) } } },
                { "claude", new Dictionary<string, object> {
                    { "perDay", R(perDay, 4) }, { "burn7", R(burn7, 4) }, { "burn30", R(burn30, 4) }, { "remaining", remaining.HasValue ? R(remaining.Value, 2) : (decimal?)null },
                    { "runway", remaining.HasValue && perDay > 0 ? Math.Round((double)(remaining.Value / perDay), 1) : (double?)null },
                    { "next30", R(next30, 2) }, { "next30Buf", R(next30Buf, 2) }, { "worst", R(worstUsd, 2) }, { "suggest", suggest } } },
                { "plans", planRows }, { "stores", storeRows }, { "history", months.Select(m => hist[m]).ToList() },
                { "trial", new Dictionary<string, object> { { "live", rows.Count(x => x.TrialLive) }, { "cost", R(Billing.UsdForUnits(trialRows.Sum(x => x.Units)) * rate) },
                    { "avgMsgs", trialRows.Count > 0 ? (int)trialRows.Average(x => x.Msgs) : 0 }, { "signups90", stores90.Count }, { "converted90", converted90 } } },
                { "renewals", new Dictionary<string, object> {
                    { "in7", rows.Count(x => x.Ending7) }, { "in7Egp", R(rows.Where(x => x.Ending7).Sum(x => x.PerMonth)) },
                    { "in30", rows.Count(x => x.Ending30) }, { "in30Egp", R(rows.Where(x => x.Ending30).Sum(x => x.PerMonth)) },
                    { "churned", rows.Count(x => x.Churned) }, { "churnedEgp", R(rows.Where(x => x.Churned).Sum(x => x.PerMonth)) } } },
                { "pending", new Dictionary<string, object> { { "count", pendingCount }, { "egp", R(pendingEgp) } } },
            };
        }

        // every store's units, all months: the base the typed credit is counted down from
        static long TotalUnits(SqlConnection c)
        {
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(" + W("u") + "),0) FROM dbo.UsageMonthly u")) return Convert.ToInt64(cmd.ExecuteScalar());
        }

        // POST money-settings: the Claude credit now ("" = forget it), the spare %, a WhatsApp number's monthly cost, fixed costs
        public static void Save(SqlConnection c, Dictionary<string, object> b, HttpResponse res)
        {
            Func<string, decimal, decimal, decimal?> num = (key, min, max) =>
            {
                object v; if (!b.TryGetValue(key, out v) || v == null) return null;
                var s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim(); if (s.Length == 0) return null;
                decimal d; if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d) || d < min || d > max) throw new BadRequestException("رقم مش مظبوط: " + s);
                return d;
            };
            object raw;
            if (b.TryGetValue("balanceUsd", out raw))
            {
                var bal = num("balanceUsd", 0, 1000000);
                Billing.Set(c, "FinClaudeBalanceUsd", bal.HasValue ? bal.Value.ToString(CultureInfo.InvariantCulture) : "");
                Billing.Set(c, "FinClaudeBalanceAt", bal.HasValue ? DateTime.UtcNow.ToString("o") : "");
                Billing.Set(c, "FinClaudeBalanceUnits", bal.HasValue ? TotalUnits(c).ToString(CultureInfo.InvariantCulture) : "");
            }
            var buf = num("bufferPct", 0, 300); if (buf.HasValue) Billing.Set(c, "FinBufferPct", buf.Value.ToString(CultureInfo.InvariantCulture));
            var wa = num("waCostEgp", 0, 100000); if (wa.HasValue) Billing.Set(c, "FinWaCostEgp", wa.Value.ToString(CultureInfo.InvariantCulture));
            var fx = num("fixedEgp", 0, 100000000); if (fx.HasValue) Billing.Set(c, "FinFixedEgp", fx.Value.ToString(CultureInfo.InvariantCulture));
            Web.Json(res, 200, Report(c));
        }
    }
}
