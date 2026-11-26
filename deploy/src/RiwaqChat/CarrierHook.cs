// ship-hook.ashx (and the older api/carrier-hook.ashx, still answering): what the companies tell us. Outside /api/, so the site's own limits there
// never hold a company back. Bosta: ?c=bosta&h=<the store's own address> with the store's secret in Authorization (both were sent with the
// delivery). J&T: ?c=jt&h=<the link's own address>, every push signed with that link's privateKey (the merchant's own, or the admin's fallback
// for a link without its own); the admin's fallback address ?c=jt&h=<JtHook> takes only the waybills of links on the fallback keys. Anything
// else is refused, and so is a shipment that isn't that store's. ShipBlu and Aramex don't notify (they're asked).
// api/carriers-admin.ashx: the super admin's part: closing it, the lowest plan, the trial's merchants, the numbers, and (hidden) J&T's fallback keys.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public class CarrierHookHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        const int MaxBody = 256 * 1024;

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response; res.TrySkipIisCustomErrors = true; res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; } if (!Db.Configured || req.ContentLength > MaxBody) { res.StatusCode = Db.Configured ? 413 : 503; return; }
            string who = req.QueryString["c"] ?? "", h = req.QueryString["h"] ?? "", ip = Web.Ip(req);
            try
            {
                using (var c = Db.Open())
                {
                    if (!Shops.Ready) { res.StatusCode = 503; return; }
                    if (!Regex.IsMatch(h, "^[a-f0-9]{24,64}$")) { Refuse(res, ip); return; }
                    if (who == "bosta") Bosta(c, req, res, h, ip); else if (who == "jt") Jt(c, req, res, h, ip); else Refuse(res, ip);
                }
            }
            catch (HttpException) { res.StatusCode = 413; }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) res.StatusCode = 500; }
        }
        // someone guessing gets 429 after a few (the secrets are 96 and 192 bits; the companies' own requests are never held back by this)
        static void Refuse(HttpResponse res, string ip) { res.StatusCode = Web.Allow("ship-hook:" + ip, 30, TimeSpan.FromMinutes(10)) ? 401 : 429; }
        static bool Same(string a, string b) { return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && Secrets.Same(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b)); }
        static string Body(HttpRequest req) { using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) return sr.ReadToEnd(); }

        static void Bosta(SqlConnection c, HttpRequest req, HttpResponse res, string h, string ip)
        {
            var k = Carriers.LinkByHook(c, h); string key = Regex.Replace(req.Headers["Authorization"] ?? "", "^Bearer +", "", RegexOptions.IgnoreCase).Trim();
            if (k == null || k.Company != "bosta" || !Same(key, k.HookSecret)) { Refuse(res, ip); return; }
            if (!Web.Allow("ship-hook-s:" + k.StoreId, 6000, TimeSpan.FromHours(1))) { res.StatusCode = 429; return; }
            var e = CarrierBosta.FromHook(ShopJson.Obj(Body(req))); if (e == null) { res.StatusCode = 400; return; }
            var x = Carriers.ByTracking(c, k.StoreId, "bosta", e.Tracking, e.Ref); if (x == null) { res.StatusCode = 404; return; }
            if (!Carriers.Apply(c, x.StoreId, x.Id, e, "hook")) { res.StatusCode = 503; return; }
            Carriers.Heard(c, k.StoreId, "bosta");
            res.ContentType = "application/json"; res.Write("{\"success\":true}");
        }

        static void Jt(SqlConnection c, HttpRequest req, HttpResponse res, string h, string ip)
        {
            // the link's own address, or the admin's fallback one (any store whose link has no keys of its own)
            var k = Carriers.LinkByHook(c, h); string account, priv; int store = 0;
            if (k != null && k.Company == "jt") { account = k.Account; priv = k.Private; store = k.StoreId; }
            else if (CarrierJt.Ready(c) && Same(h, Billing.Get(c, "JtHook"))) { account = Secrets.Unprotect(Billing.Get(c, "JtAccountBox")); priv = Secrets.Unprotect(Billing.Get(c, "JtKeyBox")); }
            else { Refuse(res, ip); return; }
            if (string.IsNullOrEmpty(priv) || string.IsNullOrEmpty(account)) { Refuse(res, ip); return; }
            // a form field; parsed by hand, so a courier's free text never trips ASP.NET's request check
            string raw = Body(req), biz = null; foreach (var p in raw.Split('&')) if (p.StartsWith("bizContent=", StringComparison.Ordinal)) biz = HttpUtility.UrlDecode(p.Substring(11), Encoding.UTF8);
            if (biz == null || !Same(req.Headers["apiAccount"], account) || !Same(req.Headers["digest"], CarrierJt.Digest(biz, priv))) { Refuse(res, ip); return; }
            if (!Web.Allow("ship-hook-jt:" + store, store == 0 ? 60000 : 6000, TimeSpan.FromHours(1))) { res.StatusCode = 429; return; }
            var list = biz.TrimStart().StartsWith("[", StringComparison.Ordinal) ? ShopJson.Arr(biz).OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>> { ShopJson.Obj(biz) }; bool busy = false; int known = 0, all = 0;
            var fallback = new Dictionary<int, bool>(); var heard = new HashSet<int>();
            // only a trace push (its «details») proves the scans reach us: an order-status push alone (courier assigned, at the warehouse) still
            // counts as an event, but the link keeps being asked, or a delivery would never arrive when the trace push isn't approved
            foreach (var w in list)
            {
                bool trace = ShopJson.List(ShopJson.At(w, "details")).Count > 0;
                foreach (var e in CarrierJt.Scans(w))
                {
                    all++; var x = Carriers.ByTracking(c, store, "jt", e.Tracking, e.Ref); if (x == null || (!string.IsNullOrEmpty(e.Ref) && x.Ref != e.Ref)) continue;   // another program's waybill, or a number that isn't this order's
                    // the fallback address: only for a store whose link signs with the fallback keys
                    if (store == 0) { bool fb; if (!fallback.TryGetValue(x.StoreId, out fb)) { var lk = Carriers.Link(c, x.StoreId, "jt"); fallback[x.StoreId] = fb = lk != null && !lk.Own; } if (!fb) continue; }
                    known++; if (trace) heard.Add(x.StoreId); if (!Carriers.Apply(c, x.StoreId, x.Id, e, "hook")) busy = true;
                }
            }
            foreach (var sid in heard) Carriers.Heard(c, sid, "jt");
            if (busy) { res.StatusCode = 503; return; }
            if (all > 0 && known == 0) { res.StatusCode = 404; return; }
            res.ContentType = "application/json; charset=utf-8"; res.Write("{\"code\":\"1\",\"msg\":\"success\",\"data\":\"SUCCESS\"}");
        }
    }

    public static partial class Carriers
    {
        // the store's shipment with this number; our reference finds one whose number isn't saved yet. storeId 0 = any store (J&T's push is signed for all of them).
        public static CarrierShip ByTracking(SqlConnection c, int storeId, string company, string tracking, string reference)
        {
            CarrierShip x = null;
            if (!string.IsNullOrEmpty(tracking)) x = Ships(c, 1, "Company=@c AND Tracking=@t AND (@s=0 OR StoreId=@s) ORDER BY Id DESC", "@c", company, "@t", tracking, "@s", storeId).FirstOrDefault();
            if (x == null && !string.IsNullOrEmpty(reference)) x = Ships(c, 1, "Company=@c AND Ref=@r AND Tracking IS NULL AND State IN ('making','unsure') AND (@s=0 OR StoreId=@s) ORDER BY Id DESC", "@c", company, "@r", reference, "@s", storeId).FirstOrDefault();
            return x;
        }
        public static string JtHook(SqlConnection c) { var h = Billing.Get(c, "JtHook"); if (string.IsNullOrEmpty(h)) { h = Shops.Token(20); Billing.Set(c, "JtHook", h); } return h; }
        // a notification for this store checked out (the first one stops J&T's 4-hour round for the link)
        internal static void Heard(SqlConnection c, int storeId, string company) { Exec(c, "UPDATE dbo.Carriers SET HeardAt=SYSUTCDATETIME() WHERE StoreId=@s AND Company=@c AND HeardAt IS NULL", "@s", storeId, "@c", company); }

        public static Dictionary<string, object> AdminJson(SqlConnection c, HttpRequest req)
        {
            var l = new List<object>();
            foreach (var co in All)
            {
                int linked = Num(c, "SELECT COUNT(*) FROM dbo.Carriers WHERE Company=@c", "@c", co.Id), broke = Num(c, "SELECT COUNT(*) FROM dbo.Carriers WHERE Company=@c AND LastError IS NOT NULL", "@c", co.Id);
                using (var cmd = Db.Cmd(c, "SELECT COUNT(*), ISNULL(SUM(CASE WHEN Ours='delivered' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN Ours='returned' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN State='unsure' THEN 1 ELSE 0 END),0) FROM dbo.Shipments WHERE Company=@c AND State<>'failed' AND CreatedAt>DATEADD(DAY,-30,SYSUTCDATETIME())", "@c", co.Id)) using (var r = cmd.ExecuteReader()) { r.Read(); l.Add(D("id", co.Id, "name", co.Name, "linked", linked, "broke", broke, "sent", (int)r[0], "delivered", (int)r[1], "returned", (int)r[2], "unsure", (int)r[3])); }
            }
            string min = Billing.Get(c, "MinPlan_" + Feature); int pid; var plan = int.TryParse(min ?? "", out pid) ? Billing.Plan(c, pid) : null; string last = Secrets.Unprotect(Billing.Get(c, "JtAccountBox")) ?? "";
            return D("list", l, "areas", All.ToDictionary(x => x.Id, x => (object)CarrierAreas.Count(c, x.Id)), "testers", Billing.Get(c, "CarrierTesters") ?? "", "plan", plan == null ? "" : plan.Name, "off", Billing.Get(c, "CarriersOff") == "1",
                "jt", D("set", CarrierJt.Ready(c), "last4", last.Length > 4 ? last.Substring(last.Length - 4) : "", "hook", CarrierJt.Ready(c) ? WaAccount.Origin(req) + HookPath + "?c=jt&h=" + JtHook(c) : ""));
        }
        public static void AdminPost(SqlConnection c, Dictionary<string, object> b)
        {
            switch (Str(b, "a", 20))
            {
                case "testers": Billing.Set(c, "CarrierTesters", string.Join(",", Str(b, "testers", 2000).Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries).Where(x => Web.SlugRx.IsMatch(x.ToLowerInvariant())).Select(x => x.ToLowerInvariant()).Distinct().Take(50))); return;
                case "off": Billing.Set(c, "CarriersOff", ShopJson.Bool(ShopJson.At(b, "off")) ? "1" : ""); return;   // closed to every store but the testers
                case "jt":
                    {
                        string account = Str(b, "account", 60), key = Str(b, "key", 200);
                        if (!Regex.IsMatch(account, @"^\d{6,30}$") || !Regex.IsMatch(key, @"^[\x21-\x7e]{16,100}$")) throw new ShopException("jt", "راجع apiAccount و privateKey من Personal center في J&T Open Platform.");
                        Billing.Set(c, "JtAccountBox", Secrets.Protect(account)); Billing.Set(c, "JtKeyBox", Secrets.Protect(key)); return;
                    }
                case "jt-clear": Billing.Set(c, "JtAccountBox", ""); Billing.Set(c, "JtKeyBox", ""); return;
                default: throw new ShopException("not_found", "مش موجود.");
            }
        }
    }

    public class CarrierAdminHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response; res.Cache.SetCacheability(HttpCacheability.NoCache); if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    if (!MetaAdminHandler.SignedIn(ctx, c)) { Web.Error(res, 401, "not_logged_in"); return; }
                    if (!Shops.Ready) { Web.Error(res, 503, "not_ready"); return; }
                    if (req.HttpMethod == "POST") { if (!Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; } Carriers.AdminPost(c, Web.Body(req, 5000)); }
                    Web.Json(res, 200, Carriers.AdminJson(c, req));
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (ShopException ex) { Web.Error(res, 400, ex.Code, ex.Message); }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }
    }
}
