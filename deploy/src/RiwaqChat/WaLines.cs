// More than one WhatsApp number per store (the user, 2026-11-24). A store whose people answer customers on several WhatsApp
// numbers links every one of them, and the assistant answers on each. The main number stays where it always was
// (Stores.WaInstance / WaNumber = line 0); the others are dbo.WaLines rows (line = WaLines.Id). Each extra number is a number
// on Sellybot's Wapilot account linked by scanning its QR, so they need the main number on Sellybot's account too.
//
// Price (the user): the WhatsApp service is 300 a month for one number; with more than one, every number is 260
// (AppSettings WaAddonPrice / WaMultiPrice). Stores.WaCount = how many numbers the service is paid for. A plan with WhatsApp
// inside («الاقتصادية») covers one number, and more numbers are the service on top of it at the "more than one" price.
// A number past what's paid for stays linked, but the assistant doesn't answer on it (the main number and the oldest go first).
// Adding numbers while the service runs pays only for the days left until its renewal date (WaLines.Quote).
//
// The team (Team below): a number may carry the name of the employee who holds it (WaLines.Staff; the main one's in
// Stores.WaStaff). Alerts that need a person («الزبون محتاجك», an order nobody confirmed, Followup.cs) go to the named numbers
// in turn instead of only the owner, from another of the store's numbers so they arrive like any message. Who answers most by
// hand is counted per number: a reply typed on the phone comes back from Wapilot as an "owner:" message on that line.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public class WaLine
    {
        public int Id;                 // 0 = the store's main number
        public bool Main;
        public string Instance, Number, Want, Staff, Status;
        public DateTime? LastAt;
        public bool Linked { get { return !string.IsNullOrEmpty(Instance) && !string.IsNullOrEmpty(Number); } }
        // how the owner sees it: "منى · 01001234567", or "رقم 2" before it's linked
        public string Label(int index)
        {
            var num = Local(Number ?? Want);
            var name = string.IsNullOrWhiteSpace(Staff) ? null : Staff.Trim();
            if (name != null && num != null) return name + " · " + num;
            return name ?? num ?? (Main ? "الرقم الأساسي" : "رقم " + (index + 1));
        }
        public static string Local(string digits) { return string.IsNullOrEmpty(digits) ? null : digits.Length == 12 && digits.StartsWith("20") ? "0" + digits.Substring(2) : "+" + digits; }
    }

    public static class WaLines
    {
        public const int Max = 10;                    // numbers per store
        public const string AskSubject = "طلب رقم واتساب إضافي";

        // ---------- the price ----------
        public static decimal MultiPrice(SqlConnection c) { var p = Billing.Dec(Billing.Get(c, "WaMultiPrice"), 260); return p > 0 ? p : 260; }
        // one number's month when the store has `total` numbers: the single price alone, the "more than one" price each for two or more
        public static decimal UnitPrice(SqlConnection c, int total) { var one = WaAccount.ServicePrice(c); return total >= 2 ? Math.Min(MultiPrice(c), one) : one; }
        static bool PaidOn(StoreInfo s) { return s.WaUntil.HasValue && s.WaUntil.Value > DateTime.UtcNow; }

        // How many numbers may answer now: one with a plan that includes WhatsApp, plus the numbers the service is paid for
        public static int Allowed(StoreInfo s)
        {
            if (s == null) return 0;
            if (s.IsAdmin) return Max;
            int n = (Billing.WaInPlan(s) ? 1 : 0) + (PaidOn(s) ? Math.Max(1, s.WaCount) : 0);
            return Math.Min(Max, n);
        }

        public class WaQuote { public int Count, Total, Days; public decimal Unit, Amount; public bool Add; }
        // What `count` numbers cost. add (while the paid service runs): only the days left until its renewal date, at the price for
        // the store's numbers after adding them; otherwise a month of them. A plan with WhatsApp inside already has one number.
        public static WaQuote Quote(SqlConnection c, StoreInfo s, int count, bool add)
        {
            count = Math.Max(1, Math.Min(Max, count));
            bool inPlan = Billing.WaInPlan(s);
            var q = new WaQuote { Count = count, Add = add && PaidOn(s) };
            if (q.Add)
            {
                q.Total = (inPlan ? 1 : 0) + Math.Max(1, s.WaCount) + count;
                q.Unit = UnitPrice(c, q.Total);
                q.Days = Math.Max(1, (int)Math.Ceiling((s.WaUntil.Value - DateTime.UtcNow).TotalDays));
                q.Amount = Math.Ceiling(count * q.Unit * q.Days / WaAccount.ServiceDays);
            }
            else
            {
                q.Total = (inPlan ? 1 : 0) + count;
                q.Unit = UnitPrice(c, q.Total);
                q.Days = WaAccount.ServiceDays;
                q.Amount = count * q.Unit;
            }
            return q;
        }

        // ---------- the lines ----------
        static WaLine Read(SqlDataReader r)
        {
            return new WaLine
            {
                Id = (int)r["Id"], Instance = r["Instance"] as string, Number = r["Number"] as string, Want = r["Want"] as string,
                Staff = r["Staff"] as string, Status = r["Status"] as string,
                LastAt = r["LastAt"] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r["LastAt"], DateTimeKind.Utc),
            };
        }

        static WaLine MainOf(StoreInfo s)
        {
            return new WaLine { Id = 0, Main = true, Instance = s.WaInstance, Number = s.WaNumber, Staff = s.WaStaff, Status = s.WaStatus, LastAt = s.WaLastAt };
        }

        // every line of the store, the main number first (when it has one), then the extra ones oldest first
        public static List<WaLine> All(SqlConnection c, StoreInfo s)
        {
            var list = new List<WaLine>();
            if (!string.IsNullOrEmpty(s.WaInstance)) list.Add(MainOf(s));
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.WaLines WHERE StoreId=@s ORDER BY Id", "@s", s.Id))
            using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(Read(r));
            return list;
        }

        public static int Extra(SqlConnection c, int storeId)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaLines WHERE StoreId=@s", "@s", storeId)) return (int)cmd.ExecuteScalar();
        }

        public static WaLine Get(SqlConnection c, StoreInfo s, int id)
        {
            if (id == 0) return string.IsNullOrEmpty(s.WaInstance) ? null : MainOf(s);
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.WaLines WHERE Id=@id AND StoreId=@s", "@id", id, "@s", s.Id))
            using (var r = cmd.ExecuteReader()) return r.Read() ? Read(r) : null;
        }

        // the main number works with the service; an extra one while it's among the first Allowed numbers (main first, then oldest)
        public static bool Works(SqlConnection c, StoreInfo s, int line)
        {
            if (line == 0) return WaAccount.HasService(s);
            if (!WaAccount.HasService(s)) return false;
            int before;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaLines WHERE StoreId=@s AND Id<@id", "@s", s.Id, "@id", line)) before = (int)cmd.ExecuteScalar();
            return 2 + before <= Allowed(s);
        }

        public static string InstanceOf(SqlConnection c, StoreInfo s, int line)
        {
            if (line == 0) return s.WaInstance;
            using (var cmd = Db.Cmd(c, "SELECT Instance FROM dbo.WaLines WHERE Id=@id AND StoreId=@s", "@id", line, "@s", s.Id)) return cmd.ExecuteScalar() as string;
        }

        // The store (and line) a number on Sellybot's account belongs to: a store's main number, or one of its extra numbers
        public static StoreInfo Route(SqlConnection c, string instance, out int line)
        {
            line = 0;
            if (string.IsNullOrEmpty(instance)) return null;
            var s = WaAccount.StoreForInstance(c, instance);
            if (s != null) return s;
            int storeId = 0;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id, StoreId FROM dbo.WaLines WHERE Instance=@i", "@i", instance))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { line = (int)r["Id"]; storeId = (int)r["StoreId"]; }
            return storeId == 0 ? null : Db.StoreById(c, storeId);
        }

        // One of the store's own numbers: a chat between two of them is an alert to someone on the team, never a customer
        public static bool IsOwn(SqlConnection c, StoreInfo s, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key == s.WaNumber) return true;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaLines WHERE StoreId=@s AND (Number=@k OR Want=@k)", "@s", s.Id, "@k", key))
                return (int)cmd.ExecuteScalar() > 0;
        }

        // the warning the dashboard shows on a number (null = all good; sets when it last answered)
        public static void SetStatus(SqlConnection c, int storeId, int line, string status)
        {
            if (line == 0) { WhatsAppBot.SetStatus(c, storeId, status); return; }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.WaLines SET Status=@st" + (status == null ? ", LastAt=SYSUTCDATETIME()" : "") + " WHERE Id=@l AND StoreId=@s",
                "@st", (object)status ?? DBNull.Value, "@l", line, "@s", storeId)) cmd.ExecuteNonQuery();
        }

        // The number the owner's reports and alerts go out from: the one they picked (Stores.ReportLine) while it's linked and
        // working, otherwise the main number
        public static WaLine ReportSender(SqlConnection c, StoreInfo s)
        {
            if (s.ReportLine > 0)
            {
                var l = Get(c, s, s.ReportLine);
                if (l != null && l.Linked && Works(c, s, l.Id)) return l;
            }
            return MainOf(s);
        }

        // instances held by a store (main or extra): never handed to another one
        public static HashSet<string> Taken(SqlConnection c)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = Db.Cmd(c, "SELECT WaInstance FROM dbo.Stores WHERE WaInstance IS NOT NULL UNION SELECT Instance FROM dbo.WaLines WHERE Instance IS NOT NULL"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) taken.Add((string)r[0]);
            return taken;
        }

        // Takes the first free number of Sellybot's account for an extra line (the same rules as WaAccount.AssignFree). null = none free.
        public static async Task<string> AssignFree(SqlConnection c, int storeId, int line)
        {
            var token = WaAccount.PlatformToken(c);
            if (token == null || WaAccount.AutoAssign(c) == "off") return null;
            var all = await Wapilot.Instances(token);
            var taken = Taken(c);
            var reserved = WaAccount.Reserved(c);
            foreach (var i in all.Where(x => !taken.Contains(x.Id) && !reserved.Contains(x.Id) && !WaAccount.Linked((x.Status ?? "").ToUpperInvariant())))
            {
                int n;
                try
                {
                    using (var cmd = Db.Cmd(c, @"UPDATE dbo.WaLines SET Instance=@i, Number=NULL, Status=NULL WHERE Id=@l AND StoreId=@s AND Instance IS NULL
                        AND NOT EXISTS (SELECT 1 FROM dbo.Stores WITH (UPDLOCK, HOLDLOCK) WHERE WaInstance=@i)", "@i", i.Id, "@l", line, "@s", storeId))
                        n = cmd.ExecuteNonQuery();
                }
                catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { continue; }   // another line took it a moment ago
                if (n == 0) return null;   // this line has a number already (or is gone)
                if (WaAccount.Stopped((i.Status ?? "").ToUpperInvariant())) try { await Wapilot.Start(token, i.Id); } catch (WaException ex) { Log.Error(ex); }
                return i.Id;
            }
            return null;
        }

        // ---------- who answers most ----------
        public class Stat { public int Chats, Bot, Staff, StaffChats, Alerts; }

        // per line, since `from`: customers who wrote, the assistant's replies, replies typed by hand on the phone (and to how many
        // customers), and the alerts the number's holder got (handoffs and unanswered orders)
        public static Dictionary<int, Stat> Stats(SqlConnection c, StoreInfo s, List<WaLine> lines, DateTime from)
        {
            var d = lines.ToDictionary(l => l.Id, l => new Stat());
            using (var cmd = Db.Cmd(c, @"SELECT Line, COUNT(DISTINCT CASE WHEN FromCustomer=1 THEN ChatId END) AS Chats,
                    SUM(CASE WHEN FromCustomer=0 AND MsgId IS NULL THEN 1 ELSE 0 END) AS Bot,
                    SUM(CASE WHEN FromCustomer=0 AND MsgId LIKE 'owner:%' THEN 1 ELSE 0 END) AS Staff,
                    COUNT(DISTINCT CASE WHEN FromCustomer=0 AND MsgId LIKE 'owner:%' THEN ChatId END) AS StaffChats
                FROM dbo.WaMessages WHERE StoreId=@s AND CreatedAt >= @f GROUP BY Line", "@s", s.Id, "@f", from))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    Stat st; if (!d.TryGetValue((int)r["Line"], out st)) continue;   // a number that was removed
                    st.Chats = Convert.ToInt32(r["Chats"]); st.Bot = Convert.ToInt32(r["Bot"]); st.Staff = Convert.ToInt32(r["Staff"]); st.StaffChats = Convert.ToInt32(r["StaffChats"]);
                }
            foreach (var l in lines.Where(x => !string.IsNullOrEmpty(x.Number)))
                using (var cmd = Db.Cmd(c, @"SELECT (SELECT COUNT(*) FROM dbo.Handoffs WHERE StoreId=@s AND ToPhone=@p AND CreatedAt >= @f)
                    + (SELECT COUNT(*) FROM dbo.OrderConfirms WHERE StoreId=@s AND AlertTo LIKE @like AND AlertedAt >= @f)
                    + (SELECT COUNT(*) FROM dbo.EoConfirms WHERE StoreId=@s AND AlertTo LIKE @like AND AlertedAt >= @f)", "@s", s.Id, "@p", l.Number, "@like", "%" + l.Number, "@f", from))
                    d[l.Id].Alerts = Convert.ToInt32(cmd.ExecuteScalar());
            return d;
        }

        // the line whose holder answered most by hand (0 replies = nobody)
        public static int? Top(Dictionary<int, Stat> stats)
        {
            var best = stats.Where(x => x.Value.Staff > 0).OrderByDescending(x => x.Value.Staff).ThenByDescending(x => x.Value.StaffChats).FirstOrDefault();
            return best.Value == null ? (int?)null : best.Key;
        }
    }

    // ---------- the team: alerts that need a person, to the employees' numbers in turn ----------
    public static class Team
    {
        public class Member { public int Line; public string Name, Phone; }

        // Every working, linked number with an employee's name on it, while the store has two or more linked numbers (an alert
        // goes out from another of them, so it arrives with a notification). Empty = alerts go to the owner as before.
        public static List<Member> Members(SqlConnection c, StoreInfo s, List<WaLine> lines = null)
        {
            lines = lines ?? WaLines.All(c, s);
            int allowed = WaLines.Allowed(s);
            var working = lines.Take(allowed).Where(l => l.Linked).ToList();
            if (working.Count < 2) return new List<Member>();
            return working.Where(l => !string.IsNullOrWhiteSpace(l.Staff)).Select(l => new Member { Line = l.Id, Name = l.Staff.Trim(), Phone = l.Number }).ToList();
        }

        // whose turn it is next (no change), for the dashboard
        public static Member Next(SqlConnection c, StoreInfo s, List<Member> team)
        {
            if (team.Count == 0) return null;
            int turn;
            using (var cmd = Db.Cmd(c, "SELECT TeamTurn FROM dbo.Stores WHERE Id=@s", "@s", s.Id)) turn = Convert.ToInt32(cmd.ExecuteScalar());
            return team[turn % team.Count];
        }

        public class Sent { public string Error, Name, Phone; }

        // An alert that needs a person: to the next one on the team, from another of the store's numbers; to the owner when there's
        // no team. Error = null when it went out. Name / Phone = who it went to (Name null = the owner).
        public static async Task<Sent> Send(SqlConnection c, StoreInfo s, string text, string idem)
        {
            var lines = WaLines.All(c, s);
            var team = Members(c, s, lines);
            if (team.Count == 0)
            {
                var r = new Sent { Phone = s.OwnerPhone };
                r.Error = string.IsNullOrEmpty(s.OwnerPhone) ? "مفيش رقم ليك في الإعدادات" : await WaNotify.ToOwner(c, s, text, idem);
                return r;
            }
            var why = WaNotify.CantSend(c, s);
            int turn;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET TeamTurn = TeamTurn + 1 OUTPUT DELETED.TeamTurn WHERE Id=@s", "@s", s.Id)) turn = Convert.ToInt32(cmd.ExecuteScalar());
            var m = team[((turn % team.Count) + team.Count) % team.Count];
            var res = new Sent { Name = m.Name, Phone = m.Phone };
            if (why != null) { res.Error = why; return res; }
            // from another working number: the owner's report number when it isn't theirs, else the main one, else any
            int allowed = WaLines.Allowed(s);
            var working = lines.Take(allowed).Where(l => l.Linked && l.Id != m.Line).ToList();
            var from = working.FirstOrDefault(l => l.Id == s.ReportLine) ?? working.FirstOrDefault();
            if (from == null) { res.Error = WaNotify.NotLinked; return res; }
            try
            {
                var token = WaAccount.Token(c, s);
                foreach (var part in Wapilot.Split(text)) { await Wapilot.Send(token, from.Instance, m.Phone + "@c.us", part, idem); idem += "+"; }
            }
            catch (WaException ex) { res.Error = ex.Message; }
            return res;
        }

        // "منى" for a team member, "انت" for the owner (the text the dashboard shows next to an alert)
        public static string Who(Sent r) { return r == null ? "" : r.Name ?? "انت"; }
    }

    // api/wa-lines.ashx — the store's WhatsApp numbers and team (the «📱 أرقام الواتساب والفريق» card of the WhatsApp tab).
    // GET = every number with its holder, state and numbers; POST ?action=add|link|qr|staff|unlink|remove|report-line.
    // The main number is linked, unlinked and removed from api/whatsapp.ashx as before; here it only takes its holder's name.
    public class WaLinesHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, await State(c, s, true)); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                    // the QR is asked for every few seconds while it's on screen: its own, larger allowance
                    if (!(action == "qr" ? Web.Allow("wa-lines-qr:" + s.Id, 600, TimeSpan.FromHours(1)) : Web.Allow("wa-lines:" + s.Id, 60, TimeSpan.FromHours(1))))
                    { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                    var body = Web.Body(req, 2000);
                    int id = Web.Int(body, "id") ?? -1;

                    if (action == "staff")
                    {
                        string name = (Web.Str(body, "name", 80) ?? "").Trim();
                        if (name.Length > 40) { Web.Error(res, 400, "bad_name", "الاسم طويل، خليه أقل من 40 حرف."); return; }
                        if (name.Any(char.IsControl)) { Web.Error(res, 400, "bad_name", "الاسم فيه حروف مش مظبوطة."); return; }
                        var line = WaLines.Get(c, s, id);
                        if (line == null) { Web.Error(res, 404, "not_found", "الرقم ده مش موجود."); return; }
                        using (var cmd = Db.Cmd(c, id == 0 ? "UPDATE dbo.Stores SET WaStaff=@n WHERE Id=@s" : "UPDATE dbo.WaLines SET Staff=@n WHERE Id=@id AND StoreId=@s",
                            "@n", name.Length > 0 ? (object)name : DBNull.Value, "@s", s.Id, "@id", id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, Db.StoreById(c, s.Id), false));
                        return;
                    }
                    if (action == "report-line")
                    {
                        var line = WaLines.Get(c, s, Math.Max(0, id));
                        if (line == null) { Web.Error(res, 404, "not_found", "الرقم ده مش موجود."); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ReportLine=@l WHERE Id=@s", "@l", line.Id, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, Db.StoreById(c, s.Id), false));
                        return;
                    }

                    if (action == "add")
                    {
                        if (string.IsNullOrEmpty(s.WaInstance)) { Web.Error(res, 400, "no_main", "اربط رقم المتجر الأساسي الأول، وبعدين ضيف أرقام تانية."); return; }
                        if (!WaAccount.IsPlatform(s)) { Web.Error(res, 400, "own_account", "الأرقام الإضافية شغالة مع الربط بالـ QR بس، ورقمك الأساسي مربوط بحساب Wapilot بتاعك."); return; }
                        if (!WaAccount.HasService(s)) { Web.Error(res, 400, "no_service", "خدمة الواتساب مش مفعّلة على متجرك دلوقتي. فعّلها أو جدّدها من «باقتي»."); return; }
                        int have = 1 + WaLines.Extra(c, s.Id);
                        if (have >= WaLines.Max) { Web.Error(res, 400, "too_many", "أقصى عدد " + WaLines.Max + " أرقام للمتجر."); return; }
                        if (have >= WaLines.Allowed(s)) { Web.Error(res, 402, "need_pay", "اشتراكك شامل " + WaLines.Allowed(s) + " رقم. ضيف رقم للاشتراك من «باقتي» الأول."); return; }
                        string raw = (Web.Str(body, "phone", 40) ?? "").Trim(), want = raw.Length == 0 ? null : WaChat.Phone(raw);
                        if (raw.Length > 0 && want == null) { Web.Error(res, 400, "bad_phone", "رقم الواتساب ده مش مظبوط، اكتبه كده مثلًا 01001234567."); return; }
                        if (want != null && WaLines.IsOwn(c, s, want)) { Web.Error(res, 400, "same_number", "الرقم ده مربوط خلاص على متجرك."); return; }
                        string staff = (Web.Str(body, "staff", 80) ?? "").Trim();
                        if (staff.Length > 40 || staff.Any(char.IsControl)) { Web.Error(res, 400, "bad_name", "اسم الموظف طويل، خليه أقل من 40 حرف."); return; }
                        int newId;
                        using (var cmd = Db.Cmd(c, "INSERT INTO dbo.WaLines(StoreId, Want, Staff) OUTPUT INSERTED.Id VALUES(@s, @w, @n)",
                            "@s", s.Id, "@w", (object)want ?? DBNull.Value, "@n", staff.Length > 0 ? (object)staff : DBNull.Value)) newId = (int)cmd.ExecuteScalar();
                        var d = await Assign(c, s, newId);
                        Web.Json(res, 200, d);
                        return;
                    }

                    var ln = id > 0 ? WaLines.Get(c, s, id) : null;
                    if (ln == null) { Web.Error(res, 404, "not_found", "الرقم ده مش موجود."); return; }

                    if (action == "link")
                    {
                        // a number added while none was free: try again to give it one
                        if (!string.IsNullOrEmpty(ln.Instance)) { Web.Json(res, 200, await State(c, s, false)); return; }
                        Web.Json(res, 200, await Assign(c, s, ln.Id));
                        return;
                    }
                    if (action == "qr")
                    {
                        var d = new Dictionary<string, object>();
                        if (string.IsNullOrEmpty(ln.Instance)) { d["error"] = "لسه بنجهّزلك رقم ربط. هيظهر هنا الـ QR أول ما يجهز."; Web.Json(res, 200, d); return; }
                        if (!WaLines.Works(c, s, ln.Id)) { d["error"] = "الرقم ده مش ضمن اشتراكك دلوقتي. ضيفه للاشتراك من «باقتي»."; Web.Json(res, 200, d); return; }
                        var token = WaAccount.PlatformToken(c);
                        if (token == null) { d["error"] = "الواتساب مش متاح دلوقتي، كلّم الدعم."; Web.Json(res, 200, d); return; }
                        try
                        {
                            var st = await Wapilot.Status(token, ln.Instance);
                            string status = (st.Status ?? "").ToUpperInvariant();
                            d["live"] = st.Status ?? "";
                            if (WaAccount.Linked(status))
                            {
                                // the same phone linked to two of the store's numbers would answer every customer twice
                                if (!string.IsNullOrEmpty(st.Number) && (st.Number == s.WaNumber || Dup(c, s, st.Number, ln.Id)))
                                {
                                    try { await Wapilot.Logout(token, ln.Instance); } catch (WaException) { }
                                    d["linked"] = false; d["error"] = "الموبايل ده مربوط خلاص كرقم تاني على متجرك. امسح الكود من موبايل الرقم الجديد.";
                                    Web.Json(res, 200, d); return;
                                }
                                d["linked"] = true;
                                if (!string.IsNullOrEmpty(st.Number) && st.Number != ln.Number)
                                    using (var cmd = Db.Cmd(c, "UPDATE dbo.WaLines SET Number=@n, Status=NULL WHERE Id=@id", "@n", st.Number, "@id", ln.Id)) cmd.ExecuteNonQuery();
                                d["number"] = st.Number ?? ln.Number ?? "";
                            }
                            else
                            {
                                d["linked"] = false;
                                if (WaAccount.Stopped(status) && Web.Allow("wa-start:" + s.Id + ":" + ln.Id, 6, TimeSpan.FromMinutes(10))) await Wapilot.Start(token, ln.Instance);
                                d["qr"] = await Wapilot.Qr(token, ln.Instance) ?? "";
                            }
                        }
                        catch (WaException ex)
                        {
                            d["error"] = ex.Message;
                            if (Wapilot.NoSubscription(ex.Code)) WaAccount.TellAdmin(c, s, WaAccount.SubSubject, "(رسالة تلقائية) رقم ربط واتساب إضافي اتخصص لمتجري (" + ln.Instance + ") محتاج اشتراك من عندكم، فكود الربط مش بيظهر.");
                        }
                        Web.Json(res, 200, d);
                        return;
                    }
                    if (action == "unlink")
                    {
                        // logs the phone out of this number (to link another phone); the number stays with the store
                        if (!string.IsNullOrEmpty(ln.Instance))
                            try { await Wapilot.Logout(WaAccount.PlatformToken(c), ln.Instance); }
                            catch (WaException ex) { if (ex.Status != 404) { Web.Error(res, 400, "wapilot", ex.Message); return; } }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.WaLines SET Number=NULL, Status=NULL WHERE Id=@id", "@id", ln.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, s, false));
                        return;
                    }
                    if (action == "remove")
                    {
                        // the phone is logged out first, so the number goes back to the pool clean; its chats stay for the history
                        if (!string.IsNullOrEmpty(ln.Instance))
                            try { await Wapilot.Logout(WaAccount.PlatformToken(c), ln.Instance); }
                            catch (WaException ex) { if (ex.Status != 404) Log.Error(ex); }
                        using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaLines WHERE Id=@id AND StoreId=@s; UPDATE dbo.Stores SET ReportLine=0 WHERE Id=@s AND ReportLine=@id;",
                            "@id", ln.Id, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, await State(c, Db.StoreById(c, s.Id), false));
                        return;
                    }
                    Web.Error(res, 400, "bad_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static bool Dup(SqlConnection c, StoreInfo s, string number, int notLine)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.WaLines WHERE StoreId=@s AND Number=@n AND Id<>@id", "@s", s.Id, "@n", number, "@id", notLine))
                return (int)cmd.ExecuteScalar() > 0;
        }

        // gives the line a free number of Sellybot's account; none free = one ticket so the admin makes one (it's picked up later)
        static async Task<Dictionary<string, object>> Assign(SqlConnection c, StoreInfo s, int line)
        {
            string picked = null, note = null;
            try { picked = await WaLines.AssignFree(c, s.Id, line); }
            catch (WaException ex) { Log.Error(ex); note = "مقدرناش نجهّز رقم دلوقتي، جرّب كمان شوية."; }
            if (picked == null && note == null)
            {
                WaAccount.TellAdmin(c, s, WaLines.AskSubject, "(رسالة تلقائية) عايز أربط رقم واتساب إضافي لمتجري، ومفيش رقم ربط فاضي دلوقتي.");
                note = "كل أرقام الربط مستخدمة دلوقتي. بعتنا للدعم وهنجهّزلك رقم في أقرب وقت، وهيظهرلك هنا الـ QR أول ما يجهز.";
            }
            var d = await State(c, s, false);
            d["added"] = line;
            if (note != null) d["note"] = note;
            return d;
        }

        static string Iso(DateTime? t) { return t.HasValue ? t.Value.ToString("o") : ""; }

        public static async Task<Dictionary<string, object>> State(SqlConnection c, StoreInfo s, bool live)
        {
            var lines = WaLines.All(c, s);
            int allowed = WaLines.Allowed(s);
            // a number still waiting for a free one on Sellybot's account: try again now and then (the admin may have made one)
            if (live && WaAccount.IsPlatform(s) && WaAccount.HasService(s))
                foreach (var l in lines.Where(x => !x.Main && string.IsNullOrEmpty(x.Instance)).Take(2))
                    if (Web.Allow("wa-line-retry:" + l.Id, 1, TimeSpan.FromMinutes(5)))
                        try { var got = await WaLines.AssignFree(c, s.Id, l.Id); if (got != null) l.Instance = got; } catch (WaException ex) { Log.Error(ex); }
            // live state of the extra numbers (the main one's is in api/whatsapp.ashx): is the phone still linked?
            var liveState = new Dictionary<int, string>();
            var token = live ? WaAccount.PlatformToken(c) : null;
            if (token != null)
            {
                var checks = lines.Where(x => !x.Main && !string.IsNullOrEmpty(x.Instance)).Select(async x =>
                {
                    try { var st = await Wapilot.Status(token, x.Instance); return new { x, st }; } catch (WaException) { return new { x, st = (Wapilot.Instance)null }; }
                }).ToList();
                foreach (var r in await Task.WhenAll(checks))
                {
                    if (r.st == null) continue;
                    liveState[r.x.Id] = r.st.Status ?? "";
                    bool linkedNow = WaAccount.Linked((r.st.Status ?? "").ToUpperInvariant());
                    if (linkedNow && !string.IsNullOrEmpty(r.st.Number) && r.st.Number != r.x.Number && r.st.Number != s.WaNumber)
                    {
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.WaLines SET Number=@n WHERE Id=@id", "@n", r.st.Number, "@id", r.x.Id)) cmd.ExecuteNonQuery();
                        r.x.Number = r.st.Number;
                    }
                    if (!linkedNow && WaAccount.Stopped((r.st.Status ?? "").ToUpperInvariant()) && !string.IsNullOrEmpty(r.x.Number))
                    {
                        // the phone was unlinked from WhatsApp itself: the number needs scanning again
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.WaLines SET Number=NULL WHERE Id=@id", "@id", r.x.Id)) cmd.ExecuteNonQuery();
                        r.x.Number = null;
                    }
                }
            }
            var from = DateTime.UtcNow.AddDays(-30);
            var stats = WaLines.Stats(c, s, lines, from);
            int? top = lines.Count > 1 ? WaLines.Top(stats) : null;
            var team = Team.Members(c, s, lines);
            var next = Team.Next(c, s, team);
            var q = WaLines.Quote(c, s, 1, true);
            var list = new List<Dictionary<string, object>>();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i]; var st = stats[l.Id]; string lv; liveState.TryGetValue(l.Id, out lv);
                list.Add(new Dictionary<string, object> {
                    { "id", l.Id }, { "main", l.Main }, { "index", i + 1 }, { "label", l.Label(i) },
                    { "number", l.Number ?? "" }, { "want", l.Want ?? "" }, { "staff", l.Staff ?? "" },
                    { "assigned", !string.IsNullOrEmpty(l.Instance) }, { "linked", l.Linked }, { "live", lv ?? "" },
                    { "works", i < allowed && WaAccount.HasService(s) }, { "status", l.Status ?? "" }, { "lastAt", Iso(l.LastAt) },
                    { "top", top.HasValue && top.Value == l.Id },
                    { "stats", new Dictionary<string, object> { { "chats", st.Chats }, { "bot", st.Bot }, { "staff", st.Staff }, { "staffChats", st.StaffChats }, { "alerts", st.Alerts } } } });
            }
            return new Dictionary<string, object> {
                { "lines", list }, { "allowed", allowed }, { "max", WaLines.Max }, { "service", WaAccount.HasService(s) },
                { "platform", WaAccount.IsPlatform(s) }, { "mainConnected", !string.IsNullOrEmpty(s.WaInstance) },
                { "canAdd", lines.Count < allowed && lines.Count < WaLines.Max }, { "paid", s.WaCount }, { "inPlan", Billing.WaInPlan(s) },
                // what one more number costs: a month of it, and (while the service runs) now, for the days left until its renewal
                { "price", WaAccount.ServicePrice(c) }, { "multi", WaLines.MultiPrice(c) },
                { "addUnit", q.Unit }, { "addNow", q.Add ? q.Amount : 0m }, { "addDays", q.Add ? q.Days : 0 },
                { "team", team.Select(m => m.Name).ToList() }, { "next", next == null ? "" : next.Name },
                { "ownerPhone", s.OwnerPhone ?? "" }, { "reportLine", s.ReportLine }, { "handoff", s.HandoffOn }, { "follow", s.OcFollow } };
        }
    }
}
