// Features that only some plans include. The super admin picks, per feature, the lowest plan that has it
// (AppSettings "MinPlan_<feature>"; WhatsApp keeps its own "WaMinPlan" and its auto-assign rule in WaAccount).
// No minimum set = the feature is open to every store, which is how everything worked before.
//   shopify : linking a Shopify store (quick or full) and order tracking in the chat
//   easyorders : linking an EasyOrders store, its order tracking and order confirmation (follows shopify's minimum until set)
//   bothstores : Shopify AND EasyOrders linked together (one of them is the norm: two catalogs cost more of the quota);
//                until the super admin picks a minimum it's the top active plan, never "open to everyone"
//   files   : uploading PDF, Word and Excel files (typed text, .txt, .csv and .md stay open)
//   styles  : the fun and "any store" bubble styles and custom colors (classic styles, the bear and the 12 swatches stay open)
// The dashboard asks api/locks.ashx which features are locked and shows an upgrade card with the plan to suggest.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class PlanGate
    {
        public static readonly string[] Features = { "shopify", "files", "styles", "chatorders", "easyorders", "bothstores" };
        public static readonly HashSet<string> FreeStyles = new HashSet<string> { "classic", "pill", "agent", "minimal", "orb", "duo", "wave", "bear" };
        public static readonly HashSet<string> FreeColors = new HashSet<string> { "#e8774a", "#e8a33d", "#d64545", "#e84a8a", "#9b5de5", "#5b5bd6", "#1a73e8", "#0ea5a4", "#2f8f5b", "#6b8e23", "#1f3a5f", "#222222" };
        public static readonly HashSet<string> LockedFileTypes = new HashSet<string> { "pdf", "docx", "xlsx" };
        public const string DefaultStyle = "bear", DefaultColor = "#e8774a";

        static readonly Dictionary<string, string> Names = new Dictionary<string, string> {
            { "shopify", "ربط Shopify وتتبع الأوردرات" }, { "files", "رفع ملفات PDF و Word و Excel" }, { "styles", "أشكال الشات والألوان" }, { "chatorders", "الطلب من الشات" },
            { "easyorders", "ربط EasyOrders وتتبع الأوردرات" },
            { "bothstores", "ربط Shopify و EasyOrders مع بعض" } };

        public static int MinPlanId(SqlConnection c, string feature)
        {
            int id;
            if (int.TryParse(Billing.Get(c, "MinPlan_" + feature) ?? "", out id) && id > 0) return id;
            // EasyOrders is the same kind of feature as Shopify: until the super admin picks its own minimum, it follows Shopify's
            if (feature == "easyorders") return MinPlanId(c, "shopify");
            // both stores at once is an upsell: until a minimum is picked, only the top active plan has it
            if (feature == "bothstores") { var top = Billing.Plans(c, true).LastOrDefault(p => p.IsActive); return top != null ? top.Id : 0; }
            return 0;
        }

        // ---- one store system per plan: Shopify or EasyOrders; both together need "bothstores" ----
        public static bool ShopifyLinked(StoreInfo s) { return !string.IsNullOrEmpty(s.ShopifyDomain) || !string.IsNullOrEmpty(s.ShopifyClientId); }

        // The system already linked that stands in the way of linking `linking` ("shopify" / "easyorders"), or null
        public static string OtherStore(StoreInfo s, string linking)
        {
            if (linking == "shopify") return EasyOrders.Connected(s) ? "EasyOrders" : null;
            return ShopifyLinked(s) ? "Shopify" : null;
        }

        public static bool BothBlocked(SqlConnection c, StoreInfo s, string linking) { return OtherStore(s, linking) != null && !Allows(c, s, "bothstores"); }

        public static void DenyBoth(HttpResponse res, SqlConnection c, StoreInfo s, string linking)
        {
            PlanInfo offer; Allows(c, s, "bothstores", out offer);
            string other = OtherStore(s, linking), mine = linking == "shopify" ? "Shopify" : "EasyOrders";
            Web.Error(res, 403, "plan_locked", "متجرك مربوط بـ " + other + "، وباقتك بتسمح بمتجر واحد بس. ربط " + mine + " و " + other + " مع بعض"
                + (offer != null ? " متاح من باقة «" + offer.Name + "». رقّي باقتك من «باقتي»، أو ألغي ربط " + other + " الأول." : " مش متاح في باقتك. ألغي ربط " + other + " الأول."));
        }

        public static bool Allows(SqlConnection c, StoreInfo s, string feature) { PlanInfo o; return Allows(c, s, feature, out o); }

        public static bool Allows(SqlConnection c, StoreInfo s, string feature, out PlanInfo offer)
        {
            offer = null;
            int minId = MinPlanId(c, feature);
            if (minId == 0) return true;
            return Check(c, s, minId, out offer);
        }

        // Is the store on minId's plan or a higher one (active, not expired)? When not, offer = the plan to suggest:
        // its own plan to renew if that one qualifies, otherwise the first active plan from the minimum up.
        // Plans rank by SortOrder, then price. minId 0 = any paid plan.
        public static bool Check(SqlConnection c, StoreInfo s, int minId, out PlanInfo offer)
        {
            offer = null;
            var all = Billing.Plans(c, true);
            int minRank = minId > 0 ? all.FindIndex(p => p.Id == minId) : -1;   // a deleted minimum = any paid plan
            int myRank = s.PlanId.HasValue ? all.FindIndex(p => p.Id == s.PlanId.Value) : -1;
            bool paid = s.PlanId.HasValue && !Billing.Expired(s);
            if (paid && (minRank < 0 || myRank >= minRank)) return true;
            if (!paid && myRank >= 0 && myRank >= minRank && all[myRank].IsActive) { offer = all[myRank]; return false; }
            for (int i = Math.Max(0, Math.Max(minRank, paid ? myRank + 1 : 0)); i < all.Count; i++)
                if (all[i].IsActive) { offer = all[i]; break; }
            return false;
        }

        // What the dashboard needs to draw an upgrade card for this plan
        public static Dictionary<string, object> OfferJson(SqlConnection c, StoreInfo s, PlanInfo offer)
        {
            var cur = s.PlanId.HasValue ? Billing.Plan(c, s.PlanId.Value) : null;
            return new Dictionary<string, object> {
                { "id", offer.Id }, { "name", offer.Name }, { "price", Billing.FinalPrice(offer) }, { "listPrice", offer.PriceEgp }, { "days", offer.DurationDays },
                { "renew", s.PlanId == offer.Id }, { "expired", Billing.Expired(s) }, { "current", cur != null ? cur.Name : "" } };
        }

        // feature -> offer (locked) or null (open), for every gated feature
        public static Dictionary<string, object> Locks(SqlConnection c, StoreInfo s)
        {
            var d = new Dictionary<string, object>();
            foreach (var f in Features)
            {
                PlanInfo offer;
                if (Allows(c, s, f, out offer)) d[f] = null;
                else d[f] = offer != null ? OfferJson(c, s, offer) : (object)new Dictionary<string, object> { { "id", 0 }, { "name", "" } };
            }
            return d;
        }

        // The error a locked action returns (the dashboard shows the upgrade card before it gets here)
        public static void Deny(HttpResponse res, SqlConnection c, StoreInfo s, string feature)
        {
            PlanInfo offer; Allows(c, s, feature, out offer);
            Web.Error(res, 403, "plan_locked", Names[feature] + (offer != null ? " متاح من باقة «" + offer.Name + "». رقّي باقتك من «باقتي»." : " مش متاح في باقتك."));
        }

        // Applied to a store before it answers customers: what its plan doesn't include is switched off
        // (a store that moved to a lower plan keeps its settings; they come back when it upgrades).
        public static void Apply(SqlConnection c, StoreInfo s)
        {
            if (s == null) return;
            try
            {
                if (s.ShopifyOrders && !Allows(c, s, "shopify")) s.ShopifyOrders = false;
                if (s.EoOrders && !Allows(c, s, "easyorders")) s.EoOrders = false;
                bool lockedStyle = !FreeStyles.Contains(s.BubbleStyle ?? ""), lockedColor = !FreeColors.Contains((s.BrandColor ?? "").ToLowerInvariant());
                if ((lockedStyle || lockedColor) && !Allows(c, s, "styles"))
                {
                    if (lockedStyle) s.BubbleStyle = DefaultStyle;
                    if (lockedColor) s.BrandColor = DefaultColor;
                }
            }
            catch (Exception ex) { Log.Error(ex); }   // never stop a chat over this
        }
    }

    // api/locks.ashx
    //   store owner (GET): { shopify, files, styles } -> null when open, or the plan to offer
    //   super admin (GET ?admin=1): { features: [{key, name, minPlan}], plans: [...] };  (POST ?admin=1) { feature, minPlan }
    public class LocksHandler : HttpTaskAsyncHandler
    {
        public override bool IsReusable { get { return true; } }

        public override Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return Task.CompletedTask; }
            try
            {
                using (var c = Db.Open())
                {
                    if (req.QueryString["admin"] == "1")
                    {
                        var adminId = AdminAuth.Current(ctx);
                        bool ok = false;
                        if (adminId.HasValue) using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Admins WHERE Id=@id", "@id", adminId.Value)) ok = (int)cmd.ExecuteScalar() > 0;
                        if (!ok) { Web.Error(res, 401, "not_logged_in"); return Task.CompletedTask; }
                        if (req.HttpMethod == "POST")
                        {
                            if (!Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return Task.CompletedTask; }
                            var b = Web.Body(req, 1000);
                            var f = (Web.Str(b, "feature", 20) ?? "").ToLowerInvariant();
                            if (!PlanGate.Features.Contains(f)) { Web.Error(res, 400, "bad_request"); return Task.CompletedTask; }
                            int mp; Billing.Set(c, "MinPlan_" + f, int.TryParse(Web.Str(b, "minPlan", 12) ?? "", out mp) && mp > 0 ? mp.ToString() : "");
                        }
                        Web.Json(res, 200, AdminState(c));
                        return Task.CompletedTask;
                    }

                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return Task.CompletedTask; }
                    Web.Json(res, 200, PlanGate.Locks(c, s));
                }
            }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
            return Task.CompletedTask;
        }

        static Dictionary<string, object> AdminState(SqlConnection c)
        {
            var names = new Dictionary<string, string> { { "shopify", "🛍️ ربط Shopify وتتبع الأوردرات" }, { "files", "📄 رفع ملفات PDF و Word و Excel" }, { "styles", "🎨 أشكال الشات المميزة والألوان الخاصة" }, { "chatorders", "🛒 الطلب من الشات (Shopify)" }, { "easyorders", "🧡 ربط EasyOrders وتتبع الأوردرات" }, { "bothstores", "🔗 ربط Shopify و EasyOrders مع بعض" } };
            return new Dictionary<string, object> {
                { "features", PlanGate.Features.Select(f => new Dictionary<string, object> { { "key", f }, { "name", names[f] }, { "minPlan", PlanGate.MinPlanId(c, f) } }).ToList() },
                { "plans", Billing.Plans(c, false).Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "price", Billing.FinalPrice(p) } }).ToList() } };
        }
    }
}
