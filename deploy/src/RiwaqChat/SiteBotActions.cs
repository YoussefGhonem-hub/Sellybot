// Sellybot's own chat (store "sellybot") can do things for the visitor, not only answer:
//  - open_form: the assistant opens a guided form inside the chat (sign up, log in, subscribe to a plan, the WhatsApp service,
//    a support ticket). The widget (site/sitebot-flows.js) asks the questions one by one, checks each answer, and sends them
//    straight to the same APIs the dashboard uses. Passwords and payment numbers never reach Claude.
//    The tool only records which form to open; ChatHandler sends it to the widget as an SSE "action" event after the reply.
//  - my_account: when the visitor is signed in to Sellybot on this browser, their plan, messages left, WhatsApp service and
//    latest requests, so the assistant can answer "when does my plan end?" or "was my request approved?".
//  - api/sitebot.ashx?action=check: is this store id / email free (the sign-up form checks each answer as it comes).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public static class SiteBotActions
    {
        public const string FormToolName = "open_form", AccountToolName = "my_account";
        static readonly string[] Forms = { "signup", "login", "plan", "whatsapp", "ticket" };
        static readonly Dictionary<string, string> FormNames = new Dictionary<string, string> {
            { "signup", "إنشاء حساب" }, { "login", "تسجيل الدخول" }, { "plan", "الاشتراك في باقة" }, { "whatsapp", "اشتراك الواتساب" }, { "ticket", "تذكرة دعم" } };
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // What one chat turn can do; Action (JSON) is the form the widget opens after the reply, or null
        public class Turn { public List<ChatHandler.ExtraTool> Tools = new List<ChatHandler.ExtraTool>(); public string Action; }

        // Called while the chat handler still has its connection (it is closed before Claude writes): the tools open their own.
        public static Turn For(SqlConnection c, HttpContext ctx, StoreInfo store)
        {
            if (store == null || c == null || store.Slug != SiteBot.Slug) return null;
            StoreInfo me = null;
            try { me = Me.Require(ctx, c); } catch (Exception ex) { Log.Error(ex); }
            if (me != null && me.Slug == SiteBot.Slug) me = null;   // the super admin looking at Sellybot's own store isn't a customer
            int? myId = me != null ? me.Id : (int?)null;
            string myName = me != null ? me.Name : null;

            var t = new Turn();
            var formProps = new Dictionary<string, JsonElement>
            {
                ["form"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string", @enum = Forms,
                    description = "signup = حساب جديد وتجربة مجانية، login = تسجيل دخول، plan = اشتراك أو تجديد أو ترقية باقة، whatsapp = اشتراك خدمة الواتساب لوحدها، ticket = تذكرة دعم لمشكلة في حسابه."
                }),
                ["plan"] = JsonSerializer.SerializeToElement(new { type = "string", description = "مع form=plan بس: اسم الباقة لو الزائر حددها (الأساسية / المتقدمة / الاحترافية)." }),
            };
            t.Tools.Add(new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = FormToolName,
                    Description = "يفتح للزائر فورم جوه الشات بيسأله الأسئلة واحدة واحدة ويبعتها بأمان: إنشاء حساب، تسجيل دخول، اشتراك في باقة، اشتراك الواتساب، أو تذكرة دعم.",
                    InputSchema = new InputSchema { Properties = formProps, Required = new List<string> { "form" } },
                },
                Run = tu => Task.FromResult(Open(t, ChatHandler.Arg(tu, "form"), ChatHandler.Arg(tu, "plan"), myName)),
                Prompt = "\n- انت تقدر تعمل حاجات للزائر من جوه الشات بأداة " + FormToolName + ": لو عايز يعمل حساب أو يبدأ التجربة المجانية، أو يسجّل دخول، أو يشترك أو يجدد أو يرقّي باقة، " +
                         "أو يشترك في الواتساب، أو عنده مشكلة في حسابه ومحتاج الدعم، افتحله الفورم المناسب على طول بدل ما تبعتله لينك (اللينك لو هو طلبه بس). " +
                         "الفورم هو اللي بيسأله الأسئلة واحدة واحدة: انت ماتسألش أسئلته بنفسك، وماتطلبش باسورد أو رقم تحويل في الشات أبدًا. " +
                         "لما تفتح فورم، ردك سطر واحد قصير يمهّد له (مثلًا «تمام، هسألك كام سؤال سريع 👇»)." +
                         (myName != null ? "\n- الزائر ده مسجّل دخول على Sellybot بحساب متجر «" + myName + "». لو سأل عن حسابه (باقته، رسايله، طلباته، الواتساب) استخدم أداة " + AccountToolName + "."
                                         : "\n- الزائر ده مش مسجّل دخول على Sellybot في المتصفح ده. لو سأل عن حسابه، اعرض عليه يسجّل دخول من الشات."),
            });
            if (myId.HasValue)
                t.Tools.Add(new ChatHandler.ExtraTool
                {
                    Def = new Tool
                    {
                        Name = AccountToolName,
                        Description = "بيجيب بيانات حساب الزائر نفسه على Sellybot: باقته وميعاد انتهائها أو التجربة، الرسايل اللي فاضلة الشهر ده، خدمة الواتساب، وآخر طلباته وردود الإدارة.",
                        InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement>(), Required = new List<string>() },
                    },
                    Run = tu => Task.FromResult(Account(myId.Value)),
                    Prompt = "",
                });
            return t;
        }

        static string Open(Turn t, string form, string plan, string myName)
        {
            form = (form ?? "").Trim().ToLowerInvariant();
            if (!Forms.Contains(form)) return "الفورم ده مش موجود. الموجود: " + string.Join("، ", Forms) + ".";
            var a = new Dictionary<string, object> { { "form", form } };
            if (form == "plan" && !string.IsNullOrWhiteSpace(plan)) a["plan"] = plan.Trim().Length > 40 ? plan.Trim().Substring(0, 40) : plan.Trim();
            t.Action = JsonSerializer.Serialize(a, Json);
            string note = "تمام: «" + FormNames[form] + "» هيظهر للزائر جوه الشات بعد ردك، وهيسأله الأسئلة واحدة واحدة. " +
                          "ردك دلوقتي: سطر واحد قصير بتكلّم بيه الزائر نفسه، زي «تمام، هسألك كام سؤال سريع 👇». ماتشرحش الخطوات، وماتقولش كلمة «فورم» أو «الزائر»، وماتسألش انت أي سؤال منها.";
            if (myName != null && (form == "signup" || form == "login")) note += " (معلومة ليك مش للزائر: هو مسجّل دخول بالفعل بحساب «" + myName + "»، والخطوات هتقوله كده.)";
            if (myName == null && (form == "plan" || form == "whatsapp" || form == "ticket")) note += " (معلومة ليك مش للزائر: هو مش مسجّل دخول، والخطوات هتعرض عليه يسجّل دخول أو يعمل حساب الأول.)";
            return note;
        }

        static string Account(int storeId)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (s == null) return "الحساب ده مش موجود. اعرض على الزائر يسجّل دخول تاني.";
                    var d = Me.Describe(c, s);
                    var requests = new List<Dictionary<string, object>>();
                    using (var cmd = Db.Cmd(c, "SELECT TOP 3 * FROM dbo.PlanRequests WHERE StoreId=@s ORDER BY Id DESC", "@s", s.Id))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) requests.Add(Billing.RequestJson(r));
                    var info = new Dictionary<string, object>
                    {
                        { "store", s.Name }, { "storeId", s.Slug }, { "email", s.OwnerEmail }, { "chatLink", "https://sellybot.online/?store=" + s.Slug },
                        { "plan", d["plan"] }, { "usageThisMonth", d["usage"] }, { "whatsappService", d.ContainsKey("waService") ? d["waService"] : null },
                        { "shopifyLinked", PlanGate.ShopifyLinked(s) }, { "easyordersLinked", EasyOrders.Connected(s) },
                        { "pendingRequest", d.ContainsKey("pendingRequest") ? d["pendingRequest"] : "" }, { "latestRequests", requests },
                    };
                    return "بيانات حساب الزائر نفسه (قولها له ببساطة ومن غير أرقام داخلية زي id؛ التواريخ UTC):\n" + JsonSerializer.Serialize(info, Json);
                }
            }
            catch (Exception ex) { Log.Error(ex); return "مقدرناش نجيب بيانات الحساب دلوقتي. قول للزائر يشوفها في «باقتي» في لوحة التحكم."; }
        }
    }

    // api/sitebot.ashx?action=check&slug=... | &email=...  -> { ok, message?, taken?, suggest? }
    // The chat's sign-up form checks each answer before the next question (the same rules as the sign-up itself).
    public class SiteBotHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            if ((req.QueryString["action"] ?? "") != "check") { Web.Error(res, 404, "unknown_action"); return; }
            if (!Web.Allow("sbchk:" + Web.Ip(req), 60, TimeSpan.FromMinutes(10))) { Web.Error(res, 429, "rate_limited", "جرّبت كتير، استنى شوية."); return; }
            try
            {
                using (var c = Db.Open())
                {
                    string slug = req.QueryString["slug"], email = req.QueryString["email"];
                    if (slug != null)
                    {
                        slug = slug.Trim().ToLowerInvariant();
                        if (!Web.SlugRx.IsMatch(slug)) { Web.Json(res, 200, new Dictionary<string, object> { { "ok", false }, { "message", "المعرّف لازم يكون حروف إنجليزي صغيرة وأرقام وشرطة (-) بس، من 3 لـ 30 حرف، ومايبدأش أو يخلصش بشرطة. مثلًا: my-store" } }); return; }
                        if (Web.Reserved(slug) || slug == SiteBot.Slug || Db.StoreBySlug(c, slug) != null)
                        { Web.Json(res, 200, new Dictionary<string, object> { { "ok", false }, { "taken", true }, { "message", "المعرّف «" + slug + "» مستخدم، اختار غيره." }, { "suggest", Free(c, slug) } }); return; }
                        Web.Json(res, 200, new Dictionary<string, object> { { "ok", true } });
                        return;
                    }
                    if (email != null)
                    {
                        email = email.Trim();
                        if (!Web.ValidEmail(email)) { Web.Json(res, 200, new Dictionary<string, object> { { "ok", false }, { "message", "الإيميل ده شكله مش مظبوط. اكتبه كامل، مثلًا name@gmail.com" } }); return; }
                        if (Db.StoreByEmail(c, email) != null) { Web.Json(res, 200, new Dictionary<string, object> { { "ok", false }, { "taken", true }, { "message", "الإيميل ده عنده حساب على Sellybot بالفعل." } }); return; }
                        // a store id made from the email, for the next question
                        string local = email.Split('@')[0].ToLowerInvariant();
                        string baseSlug = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(local, "[^a-z0-9]+", "-"), "^-+|-+$", "");
                        if (baseSlug.Length > 24) baseSlug = baseSlug.Substring(0, 24).Trim('-');
                        var sug = baseSlug.Length >= 3 ? Free(c, baseSlug) : new List<string>();
                        Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "suggest", sug } });
                        return;
                    }
                    Web.Error(res, 400, "bad_request");
                }
            }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error"); }
        }

        // up to 3 free store ids close to `wanted`
        static List<string> Free(SqlConnection c, string wanted)
        {
            var list = new List<string>();
            foreach (var x in new[] { wanted, wanted + "-store", wanted + "-shop", wanted + "1", wanted + "-eg", wanted + "2" })
            {
                if (x.EndsWith("-store-store") || x.EndsWith("-shop-shop") || x.EndsWith("-store-shop") || x.EndsWith("-shop-store")) continue;
                if (list.Count == 3) break;
                if (Web.SlugRx.IsMatch(x) && !Web.Reserved(x) && x != SiteBot.Slug && Db.StoreBySlug(c, x) == null) list.Add(x);
            }
            return list;
        }
    }
}
