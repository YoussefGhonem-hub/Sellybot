// api/health.ashx: for the uptime monitor (UptimeRobot). "ok" (200) when the site and its database answer, "down" (503) when not.
// The answer is kept 15 seconds, and only one check runs at a time, so calling it often never loads the database.
using System;
using System.Threading;
using System.Web;

namespace RiwaqChat
{
    public class HealthHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        static readonly object Lock = new object();
        static DateTime until;
        static bool lastOk = true;

        public void ProcessRequest(HttpContext ctx)
        {
            var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            bool ok;
            if (Monitor.TryEnter(Lock))
            {
                try
                {
                    if (DateTime.UtcNow >= until) { lastOk = Check(); until = DateTime.UtcNow.AddSeconds(15); }
                    ok = lastOk;
                }
                finally { Monitor.Exit(Lock); }
            }
            else ok = lastOk;   // a check is running right now: the last answer
            if (ok) Jobs.Nudge();   // the monitor calls this every 5 minutes: a run of the background jobs if the timer missed its turn
            res.StatusCode = ok ? 200 : 503;
            res.ContentType = "text/plain";
            res.Write(ok ? "ok" : "down");
        }

        static bool Check()
        {
            if (!Db.Configured) return false;
            try
            {
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "SELECT 1"))
                {
                    cmd.CommandTimeout = 10;
                    return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
                }
            }
            catch (Exception ex) { Log.Error(ex); return false; }
        }
    }
}
