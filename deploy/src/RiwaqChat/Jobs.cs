// Work that happens later, not in answer to a request: abandoned-cart reminders once they're due, and the weekly report.
// Two things start a run:
//   - a timer inside the site (every 5 minutes, started on the first database use), while the site is awake;
//   - api/cron.ashx?key=..., which the "Background jobs" GitHub Actions schedule calls every 15 minutes, so the site
//     wakes up and runs even when nobody visits it (shared hosting puts an idle site to sleep).
// A lease row in AppSettings stops two runs overlapping (two worker processes, or the timer and the cron call at once).
using System;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
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
        static Timer timer;
        static int running;
        static readonly object Gate = new object();
        static readonly TimeSpan LeaseFor = TimeSpan.FromMinutes(4);

        public static void Start()
        {
            if (timer != null) return;
            lock (Gate) if (timer == null) timer = new Timer(_ => Kick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        }

        public static void Kick()
        {
            try { HostingEnvironment.QueueBackgroundWorkItem(ct => Run(ct)); }
            catch (Exception ex) { Log.Error(ex); }
        }

        static async Task Run(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            try
            {
                using (var c = Db.Open())
                {
                    if (!Lease(c)) return;
                    try
                    {
                        try { await Carts.SendDue(c, ct); } catch (Exception ex) { Log.Error(ex); }
                        try { await Restock.Check(c, ct); } catch (Exception ex) { Log.Error(ex); }
                        try { Restock.Expire(c); } catch (Exception ex) { Log.Error(ex); }
                        try { await WeeklyReport.SendDue(c, ct); } catch (Exception ex) { Log.Error(ex); }
                    }
                    finally { Release(c); }   // the next run may start at once; the 4 minutes only cover a run that died half way
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            finally { Interlocked.Exchange(ref running, 0); }
        }

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

    // api/cron.ashx?key=<Jobs.CronKey> — wakes the site and starts a run of the background jobs
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
            using (Db.Open()) { }   // runs the schema and starts the timer on a fresh (just woken) site
            Jobs.Kick();
            Web.Json(res, 200, new { ok = true });
        }
    }
}
