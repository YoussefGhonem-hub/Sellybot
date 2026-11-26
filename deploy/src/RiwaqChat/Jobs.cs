// Work that happens later, not in answer to a request: abandoned-cart reminders once they're due, the weekly report, the customers'
// orders and returns alerts («زبايني», Crm.cs), and the store's offers («ابعت عرض», Offers.cs) a number at a time.
// What starts a run (any one of them is enough):
//   - a timer inside the site, every 5 minutes while the site is awake. The site keeps itself awake: it calls its own address every
//     2 minutes, and once more when the server stops it, so it starts again (shared hosting puts an idle site to sleep);
//   - api/health.ashx, which the uptime monitor (UptimeRobot) calls every 5 minutes: when the timer missed its turn, or the site has
//     just woken up, it starts a run;
//   - api/cron.ashx?key=..., called by the "Background jobs" GitHub Actions schedule. GitHub starts it when it has room, a few times
//     a day in practice, whatever the schedule says (seen 2026-11-25): the last resort, not the clock.
// The work goes in three lanes that don't wait for each other: a slow Shopify or an offer going out doesn't hold a reminder back.
// A lease row in AppSettings stops two runs of the first lane overlapping (two worker processes, or two of the above at once);
// the other two lanes claim what they do in the database. Every run is a row in JobRuns: admin.html shows when it ran and the longest gap.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class Jobs
    {
        static Timer timer, pinger;
        static long lastKick;   // ticks
        static readonly object Gate = new object();
        static readonly DateTime Up = DateTime.UtcNow;
        static readonly TimeSpan LeaseFor = TimeSpan.FromMinutes(4);
        // every call inside a lane times out long before this: a lane still going after it is stuck, and the next run goes on without it
        static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(40);
        static readonly TimeSpan Every = Sec("JobsEverySec", 300), PingEvery = Sec("KeepAliveEverySec", 120);   // settings only for tests

        static TimeSpan Sec(string setting, int fallback)
        {
            int v;
            return TimeSpan.FromSeconds(int.TryParse(Web.Setting(setting), out v) && v > 0 ? v : fallback);
        }

        class Lane { public string Name; public long Since; public Func<string, CancellationToken, Task> Work; }
        static readonly Lane[] Lanes =
        {
            new Lane { Name = "due", Work = Due },
            // «زبايني»: the returns alerts that waited for the morning, and what changed in the stores' orders. Outside the lease (each
            // alert and each store's pull is claimed in the database) and without holding a connection while it waits for Shopify.
            new Lane { Name = "crm", Work = (src, ct) => Crm.Run(ct) },
            // «ابعت عرض»: offers go out slowly, a message every half a minute or so, for up to ~4 minutes a run. Outside the lease (it
            // claims each slot and each number in the database itself) and without holding a connection while it waits.
            new Lane { Name = "offers", Work = (src, ct) => Offers.SendDue(ct) },
            // shipping companies: the parcels that don't tell by themselves, their areas, the rescue messages that waited for the morning (claimed in the database)
            new Lane { Name = "ship", Work = (src, ct) => Carriers.Run(ct) },
            // SellyChat: a copy of whatever didn't get there right after it was saved (each store's own round, claimed in the database)
            new Lane { Name = "sellychat", Work = (src, ct) => SellyChat.Run(ct) },
        };

        public static void Start()
        {
            if (timer != null) return;
            lock (Gate)
            {
                if (timer != null) return;
                var first = Every < TimeSpan.FromMinutes(1) ? Every : TimeSpan.FromMinutes(1);
                timer = new Timer(_ => Tick(), null, first, Every);
                if (Address() == null) return;
                pinger = new Timer(_ => { var t = Ping(); }, null, PingEvery, PingEvery);
                try { HostingEnvironment.RegisterObject(new Waker()); } catch (Exception ex) { Log.Error(ex); }
            }
        }

        static void Tick()
        {
            if (SinceKick() > TimeSpan.FromTicks(Every.Ticks / 2)) Kick("timer");   // not right after the request that woke the site
        }

        // From api/health.ashx: a run when the timer missed its turn. Costs nothing otherwise, so it may be called as often as anyone likes.
        public static void Nudge()
        {
            long was = Interlocked.Read(ref lastKick), now = DateTime.UtcNow.Ticks;
            if (now - was > Every.Ticks + Every.Ticks / 3 && Interlocked.CompareExchange(ref lastKick, now, was) == was) Kick("health");
        }

        static TimeSpan SinceKick() { return DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastKick), DateTimeKind.Utc); }

        // src: timer / health / cron / now (an offer starts, a product is back: something asked for it)
        public static void Kick(string src = "now")
        {
            Interlocked.Exchange(ref lastKick, DateTime.UtcNow.Ticks);
            foreach (var l in Lanes)
            {
                var lane = l;
                try { HostingEnvironment.QueueBackgroundWorkItem(ct => Run(lane, src, ct)); }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        static async Task Run(Lane lane, string src, CancellationToken ct)
        {
            long me = DateTime.UtcNow.Ticks, was = Interlocked.Read(ref lane.Since);
            if (was != 0 && me - was < StuckAfter.Ticks) return;   // still at it
            if (Interlocked.CompareExchange(ref lane.Since, me, was) != was) return;
            if (was != 0) Log.Error(new Exception("Background jobs: «" + lane.Name + "» was still running after " + StuckAfter.TotalMinutes + " minutes; a new run goes on without it."));
            try { await lane.Work(src, ct); }
            catch (Exception ex) { Log.Error(ex); }
            finally { Interlocked.CompareExchange(ref lane.Since, 0, me); }
        }

        static async Task Due(string src, CancellationToken ct)
        {
            using (var c = Db.Open())
            {
                if (!Lease(c)) return;
                long row = 0; var took = Stopwatch.StartNew();
                try
                {
                    row = Began(c, src);
                    try { await Carts.SendDue(c, ct); } catch (Exception ex) { Log.Error(ex); }
                    try { await Restock.Check(c, ct); } catch (Exception ex) { Log.Error(ex); }
                    try { Restock.Expire(c); } catch (Exception ex) { Log.Error(ex); }
                    try { await WeeklyReport.SendDue(c, ct); } catch (Exception ex) { Log.Error(ex); }
                    try { await WeeklyReport.SendDailyDue(c, ct); } catch (Exception ex) { Log.Error(ex); }   // the morning summary
                    try { await Followup.SendDue(c, ct); } catch (Exception ex) { Log.Error(ex); }            // unanswered confirmations
                    try { await ShopReviews.Asks(c, ct); } catch (Exception ex) { Log.Error(ex); }           // SellyStore: «إيه رأيك في أوردرك؟»
                    try { await ShopPay.Sheets(c, ct); } catch (Exception ex) { Log.Error(ex); }             // SellyStore: orders to the owner's Google Sheet
                    try { if (Web.Allow("shop-tidy", 1, TimeSpan.FromHours(20))) ShopAccount.Tidy(c); } catch (Exception ex) { Log.Error(ex); }
                    try { await ShopRent.Run(c, ct); } catch (Exception ex) { Log.Error(ex); }               // SellyStore: the rent's reminders, pictures to R2
                }
                finally { Ended(c, row, took.ElapsedMilliseconds); Release(c); }   // the next run may start at once; the 4 minutes only cover a run that died half way
            }
        }

        // ---------- the record: a row per run, kept 8 days ----------
        static long Began(SqlConnection c, string src)
        {
            try
            {
                using (var cmd = Db.Cmd(c, "DELETE FROM dbo.JobRuns WHERE At < DATEADD(DAY,-8,SYSUTCDATETIME()); INSERT INTO dbo.JobRuns(Src) OUTPUT INSERTED.Id VALUES(@s);", "@s", src))
                    return Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch (Exception ex) { Log.Error(ex); return 0; }   // the work goes on without it
        }

        static void Ended(SqlConnection c, long row, long ms)
        {
            if (row == 0) return;
            try { using (var cmd = Db.Cmd(c, "UPDATE dbo.JobRuns SET Ms=@ms WHERE Id=@id", "@ms", (int)Math.Min(ms, int.MaxValue), "@id", row)) cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Log.Error(ex); }
        }

        // When it last ran, how often, and the longest it stood still (admin.html; the cron call's answer). Nothing about any store in it.
        public static Dictionary<string, object> Stats(SqlConnection c)
        {
            DateTime now; var runs = new List<DateTime>(); var by = new Dictionary<string, object>(); int runs24 = 0, slow = 0;
            using (var cmd = Db.Cmd(c, "SELECT SYSUTCDATETIME()")) now = (DateTime)cmd.ExecuteScalar();
            using (var cmd = Db.Cmd(c, "SELECT At, Src, ISNULL(Ms,0) FROM dbo.JobRuns WHERE At > DATEADD(DAY,-7,SYSUTCDATETIME()) ORDER BY At"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var at = (DateTime)r[0]; runs.Add(at);
                    if (now - at > TimeSpan.FromHours(24)) continue;
                    runs24++; slow = Math.Max(slow, (int)r[2]);
                    object n; var src = (string)r[1];
                    by[src] = by.TryGetValue(src, out n) ? (int)n + 1 : 1;
                }
            double gap24 = 0, gap7 = 0; DateTime at24 = now, at7 = now;
            for (int i = 0; i < runs.Count; i++)
            {
                var end = i + 1 < runs.Count ? runs[i + 1] : now;
                double min = (end - runs[i]).TotalMinutes;
                if (min > gap7) { gap7 = min; at7 = runs[i]; }
                if (now - end < TimeSpan.FromHours(24) && min > gap24) { gap24 = min; at24 = runs[i]; }
            }
            return new Dictionary<string, object> {
                { "every", Every.TotalMinutes }, { "last", runs.Count == 0 ? null : Billing.Iso(runs[runs.Count - 1]) }, { "since", runs.Count == 0 ? null : Billing.Iso(runs[0]) },
                { "lastMin", runs.Count == 0 ? (object)null : Math.Round((now - runs[runs.Count - 1]).TotalMinutes, 1) }, { "runs24", runs24 }, { "by", by }, { "slowMs", slow },
                { "gap24", Math.Round(gap24, 1) }, { "gap24At", gap24 > 0 ? Billing.Iso(at24) : null }, { "gap7", Math.Round(gap7, 1) }, { "gap7At", gap7 > 0 ? Billing.Iso(at7) : null },
                { "upMin", Math.Round((DateTime.UtcNow - Up).TotalMinutes) }, { "ping", Interlocked.Read(ref pingAt) == 0 ? "none" : pingOk ? "ok" : "failed" } };
        }

        // ---------- keeping the site awake ----------
        static readonly HttpClient Http = MakeHttp();
        static int pinging; static long pingAt; static bool pingOk;

        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Sellybot-KeepAlive");
            return h;
        }

        // the site's own address; null (a copy on someone's machine) = it doesn't call itself
        static string Address()
        {
            var u = (Web.Setting("KeepAliveUrl") ?? "").Trim();
            if (u.Length > 0) return u;
            var host = (Web.Setting("CanonicalHost") ?? "").Trim();
            return host.Length == 0 ? null : "https://" + host + "/api/health.ashx";
        }

        static async Task Ping()
        {
            if (Interlocked.Exchange(ref pinging, 1) == 1) return;
            try { using (var r = await Http.GetAsync(Address())) pingOk = r.IsSuccessStatusCode; }
            catch (Exception) { pingOk = false; }   // not logged: it's tried again in two minutes, and admin.html says it doesn't get through
            finally { Interlocked.Exchange(ref pingAt, DateTime.UtcNow.Ticks); Interlocked.Exchange(ref pinging, 0); }
        }

        // The server is stopping the site (idle, recycled, an upload): one request now and it starts again. Not when it had only just
        // started, so a site that can't stay up doesn't go round and round.
        class Waker : IRegisteredObject
        {
            public void Stop(bool immediate)
            {
                if (immediate || DateTime.UtcNow - Up < TimeSpan.FromMinutes(2)) { HostingEnvironment.UnregisterObject(this); return; }
                Task.Run(async () =>
                {
                    try { using (await Http.GetAsync(Address(), HttpCompletionOption.ResponseHeadersRead)) { } }
                    catch (Exception) { }
                    finally { HostingEnvironment.UnregisterObject(this); }
                });
            }
        }

        // ---------- the lease ----------
        // Claims the database-wide lease for LeaseFor; false when another run holds it.
        static bool Lease(SqlConnection c)
        {
            string now = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string cut = DateTime.UtcNow.Subtract(LeaseFor).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key]='JobsLease') INSERT INTO dbo.AppSettings([Key],[Value]) VALUES('JobsLease','0');
                UPDATE dbo.AppSettings SET [Value]=@now WHERE [Key]='JobsLease' AND ([Value] IS NULL OR [Value] < @cut);
                SELECT @@ROWCOUNT;", "@now", now, "@cut", cut))
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        static void Release(SqlConnection c)
        {
            try { using (var cmd = Db.Cmd(c, "UPDATE dbo.AppSettings SET [Value]='0' WHERE [Key]='JobsLease'")) cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Log.Error(ex); }
        }

        // The key in the cron URL: derived from AuthSecret, so the GitHub workflow computes it from the same secret (no new one to keep)
        public static string CronKey()
        {
            var master = Web.Setting("AuthSecret");
            if (string.IsNullOrEmpty(master) || master.Length < 32) return null;
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(master)))
                return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes("sellybot-cron")).Select(b => b.ToString("x2"))).Substring(0, 32);
        }
    }

    // api/cron.ashx?key=<Jobs.CronKey> — wakes the site, starts a run of the background jobs, and says how they've been doing
    public class CronHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            var want = Jobs.CronKey();
            var got = ctx.Request.QueryString["key"] ?? "";
            if (want == null || !Secrets.Same(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(got))) { res.StatusCode = 404; return; }
            if (!Db.Configured) { res.StatusCode = 503; return; }
            Dictionary<string, object> st;
            using (var c = Db.Open())   // runs the schema and starts the timer on a fresh (just woken) site
                st = Jobs.Stats(c);     // before this call's own run
            Jobs.Kick("cron");
            st["ok"] = true;
            Web.Json(res, 200, st);
        }
    }
}
