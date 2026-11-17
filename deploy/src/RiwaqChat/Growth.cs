// api/growth.ashx — the store owner's settings for the WhatsApp growth features (the «🚀 مميزات الواتساب» section of the
// WhatsApp tab): their own number (for the weekly report and handoff alerts), abandoned carts, back in stock, the weekly
// report and handoff. GET = the state of each, with what's missing; POST ?action=owner|carts|restock|report|handoff.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public class GrowthHandler : HttpTaskAsyncHandler
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
                    if (req.HttpMethod == "GET") { Web.Json(res, 200, State(c, s)); return; }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!Web.Allow("growth:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
                    var body = Web.Body(req, 2000);
                    object en; bool on = body.TryGetValue("enabled", out en) && en is bool && (bool)en;

                    if (action == "owner")
                    {
                        string raw = (Web.Str(body, "phone", 40) ?? "").Trim();
                        string phone = raw.Length == 0 ? null : WaNotify.Phone(raw);
                        if (raw.Length > 0 && phone == null) { Web.Error(res, 400, "bad_phone", "اكتب رقمك صح، مثلًا 01001234567."); return; }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET OwnerPhone=@p WHERE Id=@s", "@p", (object)phone ?? DBNull.Value, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, State(c, Db.StoreById(c, s.Id)));
                        return;
                    }

                    if (action == "carts")
                    {
                        int delay = Web.Int(body, "delay") ?? s.CartDelay;
                        if (!Carts.Delays.Contains(delay)) delay = 60;
                        string coupon = (Web.Str(body, "coupon", 40) ?? "").Trim();
                        if (coupon.Length > 30 || coupon.Any(ch => char.IsControl(ch))) { Web.Error(res, 400, "bad_coupon", "كود الخصم طويل أوي، خليه أقل من 30 حرف."); return; }
                        bool hooked = s.CartHooked;
                        if (on)
                        {
                            var stop = Needs(c, s, Carts.Feature);
                            if (stop != null) { Deny(res, c, s, stop); return; }
                            var creds = ShopifyAdmin.For(s);
                            if (creds == null) { Web.Error(res, 400, "no_shopify", "استرجاع السلة شغال مع Shopify بس: اعمل ربط كامل لمتجرك على Shopify الأول (بالـ Client ID والـ secret) من «داتا المتجر»."); return; }
                            if (!ShopifyAdmin.Has(s.ShopifyScopes, "read_orders")) { Web.Error(res, 400, "no_scope", "التطبيق ناقصه صلاحية read_orders. ضيفها في Scopes واعمل Release للنسخة ووافق عليها، وبعدين اربط تاني."); return; }
                            if (!hooked)
                            {
                                if (!Web.Allow("cart-hook:" + s.Id, 10, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية وجرّب تاني."); return; }
                                hooked = await ShopifyAdmin.RegisterCartWebhooks(creds, "https://" + req.Url.Host + req.ApplicationPath);
                                if (!hooked) { Web.Error(res, 502, "hook_failed", "Shopify مارضيتش تبعتلنا السلات المتروكة. اتأكد إن التطبيق فيه صلاحية read_orders واربط تاني، ولو فضلت كلّم الدعم."); return; }
                            }
                        }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET CartOn=@o, CartDelay=@d, CartCoupon=@cp, CartHooked=@h WHERE Id=@s",
                            "@o", on, "@d", delay, "@cp", coupon.Length > 0 ? (object)coupon : DBNull.Value, "@h", hooked, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, State(c, Db.StoreById(c, s.Id)));
                        return;
                    }

                    if (action == "restock")
                    {
                        if (on)
                        {
                            var stop = Needs(c, s, Restock.Feature);
                            if (stop != null) { Deny(res, c, s, stop); return; }
                            if (!Restock.CanWatch(s)) { Web.Error(res, 400, "no_store", "عشان نعرف إن المنتج رجع، لازم متجرك يكون مربوط: ربط Shopify الكامل أو EasyOrders من «داتا المتجر»."); return; }
                        }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET RestockOn=@o WHERE Id=@s", "@o", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, State(c, Db.StoreById(c, s.Id)));
                        return;
                    }

                    if (action == "report")
                    {
                        if (on)
                        {
                            var stop = Needs(c, s, WeeklyReport.Feature);
                            if (stop != null) { Deny(res, c, s, stop); return; }
                            if (string.IsNullOrEmpty(s.OwnerPhone)) { Web.Error(res, 400, "no_owner", "اكتب رقمك انت على الواتساب الأول، عشان التقرير يوصلك عليه."); return; }
                        }
                        // switching it on: the first one comes next Saturday ("ابعتلي تقرير دلوقتي" sends one at once)
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET ReportOn=@o, ReportAt=CASE WHEN @o=1 AND ReportOn=0 THEN SYSUTCDATETIME() ELSE ReportAt END WHERE Id=@s", "@o", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, State(c, Db.StoreById(c, s.Id)));
                        return;
                    }
                    if (action == "handoff")
                    {
                        if (on)
                        {
                            var stop = Needs(c, s, Handoff.Feature);
                            if (stop != null) { Deny(res, c, s, stop); return; }
                        }
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET HandoffOn=@o WHERE Id=@s", "@o", on, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Web.Json(res, 200, State(c, Db.StoreById(c, s.Id)));
                        return;
                    }
                    if (action == "report-now")
                    {
                        var stop = Needs(c, s, WeeklyReport.Feature);
                        if (stop != null) { Deny(res, c, s, stop); return; }
                        if (string.IsNullOrEmpty(s.OwnerPhone)) { Web.Error(res, 400, "no_owner", "اكتب رقمك انت على الواتساب الأول، عشان التقرير يوصلك عليه."); return; }
                        if (!Web.Allow("report-now:" + s.Id, 3, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "بعتنا تقارير كتير في الساعة دي، جرّب بعدين."); return; }
                        var text = await WeeklyReport.Build(c, s);
                        var why = await WaNotify.ToOwner(c, s, text, "sb-report-now-" + s.Id + "-" + DateTime.UtcNow.Ticks);
                        if (why != null) { Web.Error(res, 502, "not_sent", "التقرير ماتبعتش: " + why); return; }
                        Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "text", text } });
                        return;
                    }

                    Web.Error(res, 400, "bad_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        // What stands in the way of switching a feature on: "plan:<feature>" (its plan gate), "service", "whatsapp", or null
        static string Needs(SqlConnection c, StoreInfo s, string feature)
        {
            if (!PlanGate.Allows(c, s, feature)) return "plan:" + feature;
            if (!WaAccount.HasService(s)) return "service";
            if (string.IsNullOrEmpty(s.WaInstance) || string.IsNullOrEmpty(s.WaNumber)) return "whatsapp";
            return null;
        }

        static void Deny(HttpResponse res, SqlConnection c, StoreInfo s, string stop)
        {
            if (stop.StartsWith("plan:")) { PlanGate.Deny(res, c, s, stop.Substring(5)); return; }
            if (stop == "service") { Web.Error(res, 400, "no_service", "المميزات دي بتشتغل على خدمة الواتساب. فعّلها أو جدّدها من «باقتي» الأول."); return; }
            Web.Error(res, 400, "no_whatsapp", "اربط واتساب المتجر الأول من صفحة الواتساب، عشان الرسايل تتبعت من رقمك.");
        }

        public static Dictionary<string, object> State(SqlConnection c, StoreInfo s)
        {
            bool shopifyFull = ShopifyAdmin.For(s) != null;
            var d = new Dictionary<string, object> {
                { "ownerPhone", s.OwnerPhone ?? "" }, { "storeNumber", s.WaNumber ?? "" },
                { "service", WaAccount.HasService(s) }, { "whatsapp", !string.IsNullOrEmpty(s.WaInstance) && !string.IsNullOrEmpty(s.WaNumber) },
                { "shopify", shopifyFull }, { "easyorders", EasyOrders.Connected(s) },
                { "carts", new Dictionary<string, object> {
                    { "on", s.CartOn }, { "delay", Carts.Delays.Contains(s.CartDelay) ? s.CartDelay : 60 }, { "coupon", s.CartCoupon ?? "" }, { "hooked", s.CartHooked },
                    { "scope", ShopifyAdmin.Has(s.ShopifyScopes, "read_orders") },
                    { "stats", Carts.Stats(c, s.Id) }, { "recent", s.CartOn || s.CartHooked ? (object)Carts.Recent(c, s.Id) : new object[0] } } },
                { "images", new Dictionary<string, object> { { "on", WaAccount.HasService(s) && PlanGate.Allows(c, s, WaImages.Feature) } } },
                { "restock", new Dictionary<string, object>(Restock.State(c, s.Id)) { { "on", s.RestockOn }, { "canWatch", Restock.CanWatch(s) } } },
                { "handoff", new Dictionary<string, object> { { "on", s.HandoffOn }, { "recent", Handoff.Recent(c, s.Id) } } },
                { "report", new Dictionary<string, object> { { "on", s.ReportOn }, { "lastAt", s.ReportAt.HasValue ? s.ReportAt.Value.ToString("o") : "" } } },
            };
            return d;
        }
    }
}
