// Live notifications for the admin app (the user, 2026-11-28: «عايز استخدم في الاشعارات signalR»).
//   /signalr        ASP.NET SignalR 2 on OWIN. Hub "admin": only with a signed-in admin's cookie (the same sb_admin as admin.html).
//                   The app calls hello(cursor) when it connects and gets what it missed; then the server calls events(...) and badges(...).
//                   Hub "ping": anyone, ping() -> the server's time (to check the live connection, e.g. through Cloudflare).
//   the watcher     every 4 s while an app is connected (every 20 s otherwise, only for iPhone pushes): AdminApp.Since on the tables;
//                   what's new goes to the connected apps and, for each iPhone that wants it, through Apple's push service (Apns.cs).
//                   Apple's part runs in one app copy at a time (an applock) from a cursor kept in AppSettings, so a restart
//                   neither repeats nor loses a push.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using Microsoft.Owin;
using Owin;

[assembly: OwinStartup(typeof(RiwaqChat.LiveStartup))]

namespace RiwaqChat
{
    public class LiveStartup
    {
        // Runs before the site's first request: anything thrown here would stop every page, so SignalR failing only turns it off (logged).
        public void Configuration(IAppBuilder app)
        {
            try
            {
                // Cloudflare closes a connection that says nothing for 100 s: a dead connection noticed in 30 s, a keep-alive every 10 s
                // (SignalR wants DisconnectTimeout set before KeepAlive, and KeepAlive at most a third of it)
                GlobalHost.Configuration.DisconnectTimeout = TimeSpan.FromSeconds(30);
                GlobalHost.Configuration.KeepAlive = TimeSpan.FromSeconds(10);
                GlobalHost.Configuration.ConnectionTimeout = TimeSpan.FromSeconds(90);   // a long poll's wait, under Cloudflare's 100 s
                GlobalHost.Configuration.DefaultMessageBufferSize = 50;
                app.MapSignalR("/signalr", new HubConfiguration { EnableJavaScriptProxies = false, EnableDetailedErrors = false });
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    // a signed-in super admin's cookie, still valid (not ended by «اخرج من الأجهزة التانية»)
    public class AdminOnlyAttribute : AuthorizeAttribute
    {
        static bool Ok(IRequest r)
        {
            Cookie ck;
            if (r == null || !r.Cookies.TryGetValue(AdminAuth.CookieName, out ck)) return false;
            return AdminLive.AdminOf(ck.Value) != null;
        }
        public override bool AuthorizeHubConnection(HubDescriptor hubDescriptor, IRequest request) { return Ok(request); }
        public override bool AuthorizeHubMethodInvocation(IHubIncomingInvokerContext hubIncomingInvokerContext, bool appliesToMethod) { return Ok(hubIncomingInvokerContext.Hub.Context.Request); }
    }

    [HubName("admin"), AdminOnly]
    public class AdminHub : Hub
    {
        // every connection here is a signed-in admin, so the server sends to all of them (no groups: with long polling a group needs the
        // client to send a groups token back on every poll)
        public override Task OnConnected() { AdminLive.Join(Context.ConnectionId); return base.OnConnected(); }
        public override Task OnReconnected() { AdminLive.Join(Context.ConnectionId); return base.OnReconnected(); }
        public override Task OnDisconnected(bool stopCalled) { AdminLive.Leave(Context.ConnectionId); return base.OnDisconnected(stopCalled); }

        // what the app missed since its cursor, the badges, and a new cursor
        public object Hello(string cursor)
        {
            using (var c = Db.Open()) return AdminApp.Since(c, cursor).Json();
        }
    }

    [HubName("ping")]
    public class PingHub : Hub
    {
        public string Ping() { return DateTime.UtcNow.ToString("o"); }
    }

    public static class AdminLive
    {
        static readonly ConcurrentDictionary<string, byte> Live = new ConcurrentDictionary<string, byte>();
        static Timer timer;
        static int busy;
        static string cursor, badges;
        static DateTime lastQuiet = DateTime.MinValue, devicesAt = DateTime.MinValue;
        static int iphones;

        public static void Join(string id) { Live[id] = 1; Start(); }
        public static void Leave(string id) { byte b; Live.TryRemove(id, out b); }
        public static int Connected { get { return Live.Count; } }

        public static void Start()
        {
            if (timer != null) return;
            lock (Live) if (timer == null) timer = new Timer(Tick, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));
        }

        // the admin a cookie belongs to (null = not signed in, or signed out everywhere after it was given)
        public static int? AdminOf(string cookie)
        {
            try
            {
                DateTime issued; var id = AdminAuth.Check(cookie, out issued);
                if (id == null) return null;
                using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, "SELECT SignedOutAt FROM dbo.Admins WHERE Id=@id", "@id", id.Value))
                using (var r = cmd.ExecuteReader())
                    return r.Read() && !(r[0] is DateTime && issued < (DateTime)r[0]) ? id : null;
            }
            catch (Exception ex) { Log.Error(ex); return null; }
        }

        static void Tick(object _)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                bool live = Connected > 0;
                if (!live && DateTime.UtcNow - lastQuiet < TimeSpan.FromSeconds(20)) return;
                lastQuiet = DateTime.UtcNow;
                using (var c = Db.Open())
                {
                    if (live)
                    {
                        var r = AdminApp.Since(c, cursor);
                        cursor = r.Cursor.ToString();
                        var hub = GlobalHost.ConnectionManager.GetHubContext<AdminHub>();
                        var b = Web.Js.Serialize(r.Badges);
                        if (r.Events.Count > 0) hub.Clients.All.events(r.Json());
                        else if (b != badges) hub.Clients.All.badges(r.Badges);
                        badges = b;
                    }
                    else { cursor = null; badges = null; }
                    Push(c);
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        // Apple's push service for closed iPhone apps: one app copy at a time, from the cursor in AppSettings
        static void Push(SqlConnection c)
        {
            if (DateTime.UtcNow - devicesAt > TimeSpan.FromMinutes(1))
            {
                using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.AdminDevices WHERE PushToken IS NOT NULL AND RevokedAt IS NULL AND Platform='ios'")) iphones = (int)cmd.ExecuteScalar();
                devicesAt = DateTime.UtcNow;
            }
            if (iphones == 0) return;
            var k = Apns.Load(c);
            if (k == null) return;
            using (var lk = Db.Cmd(c, "DECLARE @r INT; EXEC @r = sp_getapplock @Resource='adminpush', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @r;"))
                if (Convert.ToInt32(lk.ExecuteScalar()) < 0) return;
            try
            {
                string was;
                // straight from the table: Billing.Get keeps settings 30 s, and an old cursor would send a push twice
                using (var cmd = Db.Cmd(c, "SELECT [Value] FROM dbo.AppSettings WHERE [Key]='AdminPushCursor'")) was = cmd.ExecuteScalar() as string;
                var r = AdminApp.Since(c, was);
                if (r.Cursor.ToString() == was) return;
                Billing.Set(c, "AdminPushCursor", r.Cursor.ToString());
                if (r.Events.Count == 0) return;
                var devs = Apns.Devices(c, null);
                int badge = Convert.ToInt32(r.Badges["total"]);
                var sends = new List<Task<string>>();
                foreach (var e in r.Events.Skip(Math.Max(0, r.Events.Count - 5)))   // a burst sends its last 5
                    foreach (var d in devs)
                    {
                        var type = (string)e["type"];
                        if (!string.IsNullOrEmpty(d.Prefs) && !d.Prefs.Split(',').Contains(type)) continue;
                        sends.Add(Apns.Send(k, d, (string)e["title"], (string)e["body"], (string)e["route"], badge, type));
                    }
                if (sends.Count == 0) return;
                System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    string worst = null;
                    foreach (var t in sends) { try { var why = await t.ConfigureAwait(false); if (why != null) worst = why; } catch (Exception ex) { worst = ex.GetBaseException().Message; Log.Error(ex); } }
                    try { using (var c2 = Db.Open()) Apns.Remember(c2, worst == null ? "ok" : "error: " + worst); } catch (Exception ex) { Log.Error(ex); }
                });
            }
            finally
            {
                using (var un = Db.Cmd(c, "EXEC sp_releaseapplock @Resource='adminpush', @LockOwner='Session';")) un.ExecuteNonQuery();
            }
        }
    }
}
