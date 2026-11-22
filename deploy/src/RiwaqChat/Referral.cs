using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Web;

namespace RiwaqChat
{
    // «ادعي تاجر» (2026-11-22, the user's rule): every store has an invite link (start.html?ref=<its slug>). Each store that signs up
    // from it and pays for a plan gives the inviter ONE month at ReferralPct% off (default 50). The months are used one by one:
    // 10 paying stores = 10 months at half price, never two in the same month (a plan request of N months uses up to N of them).
    // The discount is on the plan's months only: not the WhatsApp service, not extra messages, not an upgrade's price difference.
    // It's taken off what the store pays for that month (its old price too). The admin sees it on the request, with the stores behind it.
    // dbo.Referrals: one row per invited store (Db.cs).
    public static class Referral
    {
        public const string Cookie = "sb_ref";   // set by ref.js from ?ref=, read at sign-up

        // ReferralPct setting: 0..100, 0 = off
        public static int Pct(SqlConnection c)
        {
            int p; return int.TryParse(Billing.Get(c, "ReferralPct"), out p) && p >= 0 && p <= 100 ? p : 50;
        }

        public static string Link(HttpRequest req, StoreInfo s) { return WaAccount.Origin(req) + "/start.html?ref=" + s.Slug; }

        // At sign-up: the store whose link brought this one (cookie sb_ref). Not itself, not Sellybot's own store, and not a store with
        // the same email or WhatsApp number. The cookie is cleared, so a second account from the same browser doesn't count again.
        public static void Join(SqlConnection c, HttpContext ctx, int storeId, string email, string phone)
        {
            var ck = ctx.Request.Cookies[Cookie];
            string code = ck == null ? "" : (ck.Value ?? "").Trim().ToLowerInvariant();
            if (ck != null) ctx.Response.Cookies.Add(new HttpCookie(Cookie, "") { Expires = DateTime.UtcNow.AddDays(-1), Path = "/" });
            if (!Web.SlugRx.IsMatch(code) || code == SiteBot.Slug) return;
            var from = Db.StoreBySlug(c, code);
            if (from == null || from.Id == storeId) return;
            if (string.Equals(from.OwnerEmail, email, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrEmpty(phone) && from.OwnerPhone == phone) return;
            using (var cmd = Db.Cmd(c, "IF NOT EXISTS (SELECT 1 FROM dbo.Referrals WHERE StoreId=@s) INSERT INTO dbo.Referrals(ReferrerId, StoreId) VALUES (@r, @s)", "@r", from.Id, "@s", storeId))
                cmd.ExecuteNonQuery();
        }

        // A plan was paid for this store (a request approved, or recorded by the admin) with money: its inviter earns its month, once
        public static void Paid(SqlConnection c, int storeId, decimal paid)
        {
            if (paid <= 0) return;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Referrals SET PaidAt=SYSUTCDATETIME() WHERE StoreId=@s AND PaidAt IS NULL", "@s", storeId)) cmd.ExecuteNonQuery();
        }

        // Months this store can still use (earned, not held by a request, not used)
        public static int Available(SqlConnection c, int referrerId)
        {
            using (var cmd = Db.Cmd(c, @"SELECT COUNT(*) FROM dbo.Referrals f JOIN dbo.Stores s ON s.Id = f.StoreId
                                         WHERE f.ReferrerId=@r AND f.PaidAt IS NOT NULL AND f.RequestId IS NULL", "@r", referrerId))
                return (int)cmd.ExecuteScalar();
        }

        // The discount on a plan request: k of its months at pct% off what those months cost (the dashboard does the same sum)
        public static decimal Off(decimal planTotal, int months, int k, int pct)
        {
            if (k <= 0 || months <= 0 || pct <= 0 || planTotal <= 0) return 0;
            return Math.Round(planTotal * Math.Min(k, months) / months * pct / 100m, 0, MidpointRounding.AwayFromZero);
        }

        // The request takes its months (the oldest earned first); cancelling or rejecting it gives them back; approving it uses them
        public static void Hold(SqlConnection c, int referrerId, int requestId, int k)
        {
            using (var cmd = Db.Cmd(c, @"WITH x AS (SELECT TOP (@k) * FROM dbo.Referrals WHERE ReferrerId=@r AND PaidAt IS NOT NULL AND RequestId IS NULL ORDER BY PaidAt, Id)
                                         UPDATE x SET RequestId=@q", "@k", k, "@r", referrerId, "@q", requestId)) cmd.ExecuteNonQuery();
        }
        public static void Release(SqlConnection c, int requestId)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Referrals SET RequestId=NULL WHERE RequestId=@q AND UsedAt IS NULL", "@q", requestId)) cmd.ExecuteNonQuery();
        }
        public static void Use(SqlConnection c, int requestId)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Referrals SET UsedAt=SYSUTCDATETIME() WHERE RequestId=@q AND UsedAt IS NULL", "@q", requestId)) cmd.ExecuteNonQuery();
        }

        // The stores behind a request's discount (for the admin's request card)
        public static List<Dictionary<string, object>> Behind(SqlConnection c, int requestId)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT s.Name, s.Slug, f.PaidAt FROM dbo.Referrals f JOIN dbo.Stores s ON s.Id = f.StoreId WHERE f.RequestId=@q ORDER BY f.PaidAt", "@q", requestId))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new Dictionary<string, object> { { "name", r["Name"] }, { "slug", r["Slug"] }, { "paidAt", Billing.Iso(r["PaidAt"]) } });
            return list;
        }

        // Every store this one invited, newest first: signed up / paid / its month held or used
        static List<Dictionary<string, object>> Invited(SqlConnection c, int referrerId, bool forAdmin)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 200 s.Name, s.Slug, f.CreatedAt, f.PaidAt, f.RequestId, f.UsedAt FROM dbo.Referrals f JOIN dbo.Stores s ON s.Id = f.StoreId
                                         WHERE f.ReferrerId=@r ORDER BY f.CreatedAt DESC", "@r", referrerId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var d = new Dictionary<string, object> {
                        { "name", r["Name"] }, { "joinedAt", Billing.Iso(r["CreatedAt"]) }, { "paidAt", Billing.Iso(r["PaidAt"]) },
                        { "state", r["UsedAt"] != DBNull.Value ? "used" : r["RequestId"] != DBNull.Value ? "held" : r["PaidAt"] != DBNull.Value ? "ready" : "joined" } };
                    if (forAdmin) d["slug"] = r["Slug"];
                    list.Add(d);
                }
            return list;
        }

        static Dictionary<string, object> Counts(List<Dictionary<string, object>> list)
        {
            int paid = 0, ready = 0, held = 0, used = 0;
            foreach (var d in list)
            {
                var st = (string)d["state"];
                if (st != "joined") paid++;
                if (st == "ready") ready++; else if (st == "held") held++; else if (st == "used") used++;
            }
            return new Dictionary<string, object> { { "joined", list.Count }, { "paid", paid }, { "available", ready }, { "held", held }, { "used", used } };
        }

        // For «باقتي»: the link, the rule, and how it's going
        public static Dictionary<string, object> OwnerJson(SqlConnection c, HttpRequest req, StoreInfo s)
        {
            var list = Invited(c, s.Id, false);
            var d = Counts(list);
            d["link"] = Link(req, s); d["pct"] = Pct(c); d["list"] = list;
            return d;
        }

        // For the admin's store page: who invited it, and whom it invited
        public static Dictionary<string, object> AdminJson(SqlConnection c, int storeId)
        {
            Dictionary<string, object> by = null;
            using (var cmd = Db.Cmd(c, @"SELECT s.Id, s.Name, s.Slug, f.PaidAt FROM dbo.Referrals f JOIN dbo.Stores s ON s.Id = f.ReferrerId WHERE f.StoreId=@s", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                if (r.Read()) by = new Dictionary<string, object> { { "id", r["Id"] }, { "name", r["Name"] }, { "slug", r["Slug"] }, { "paidAt", Billing.Iso(r["PaidAt"]) } };
            var list = Invited(c, storeId, true);
            var d = Counts(list);
            d["by"] = by; d["list"] = list; d["pct"] = Pct(c);
            return d;
        }
    }
}
