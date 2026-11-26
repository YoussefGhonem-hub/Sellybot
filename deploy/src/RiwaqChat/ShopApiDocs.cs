// /api/v1/docs: the page a merchant's developer reads, in Arabic (?lang=en for English). Written from the lists the handler itself uses
// (ShopApi.Routes, Scopes, Errors, ShopApiHooks.Events), so it can't promise an address or a field that isn't there.
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;

namespace RiwaqChat
{
    public static partial class ShopApi
    {
        public static string Base(HttpRequest req)
        {
            var host = (Web.Setting("CanonicalHost") ?? "").Trim();
            return (host.Length > 0 ? "https://" + host : req.Url.GetLeftPart(UriPartial.Authority)) + "/api/v1";
        }
        static string H(string s) { return HttpUtility.HtmlEncode(s ?? ""); }

        static void Docs(HttpRequest req, HttpResponse res)
        {
            bool en = req.QueryString["lang"] == "en"; Func<string, string, string> T = (a, e) => en ? e : a; string b = Base(req);
            var h = new StringBuilder(30000);
            h.Append("<!doctype html><html lang=\"").Append(en ? "en" : "ar").Append("\" dir=\"").Append(en ? "ltr" : "rtl").Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\">")
             .Append("<title>").Append(T("SellyStore API: شرح للمبرمج", "SellyStore API for developers")).Append("</title><style>")
             .Append(":root{--bg:#f6f7f6;--card:#fff;--ink:#17211d;--muted:#5a6762;--line:#dde3e0;--brand:#0f7a5c;--code:#f1f4f2;--get:#1f6fd1;--post:#0f7a5c;--patch:#a86a00}")
             .Append("@media(prefers-color-scheme:dark){:root{--bg:#101513;--card:#172019;--ink:#e6eee9;--muted:#9fb0a8;--line:#2a3631;--brand:#4cc79f;--code:#0c110f;--get:#6aa8ff;--post:#4cc79f;--patch:#f0c36a}}")
             .Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.75 system-ui,'Segoe UI',Tahoma,sans-serif}main{max-width:960px;margin:0 auto;padding:20px 16px 60px}")
             .Append("h1{font-size:26px;margin:8px 0}h2{font-size:21px;margin:34px 0 10px;padding-top:6px;border-top:1px solid var(--line)}h3{font-size:17px;margin:0 0 4px}p{margin:6px 0}a{color:var(--brand)}.sub{color:var(--muted)}")
             .Append(".card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px 16px;margin:12px 0}code,pre{font:13.5px/1.6 ui-monospace,Consolas,monospace;direction:ltr;unicode-bidi:isolate}code{background:var(--code);padding:1px 5px;border-radius:5px;display:inline-block;max-width:100%;overflow-wrap:anywhere;vertical-align:bottom}")
             .Append("pre{background:var(--code);border:1px solid var(--line);border-radius:10px;padding:10px 12px;overflow-x:auto;margin:8px 0;text-align:left;white-space:pre}")
             .Append(".rt{display:flex;flex-wrap:wrap;gap:8px;align-items:center;direction:ltr;justify-content:flex-end}html[dir=ltr] .rt{justify-content:flex-start}.m{font:700 12px/1 ui-monospace,Consolas,monospace;padding:5px 8px;border-radius:6px;color:#fff;background:var(--get)}.m.POST{background:var(--post)}.m.PATCH{background:var(--patch);color:#1b1300}")
             .Append(".p{font:600 15px ui-monospace,Consolas,monospace;word-break:break-all}.sc{font-size:12.5px;border:1px solid var(--line);border-radius:99px;padding:2px 10px;color:var(--muted)}")
             .Append(".tw{overflow-x:auto}table{width:100%;border-collapse:collapse;margin:8px 0;font-size:14.5px}th,td{border-bottom:1px solid var(--line);padding:7px 8px;text-align:start;vertical-align:top}th{color:var(--muted);font-weight:600}")
             .Append(".top{display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap}.lang{border:1px solid var(--line);border-radius:99px;padding:4px 14px;text-decoration:none;background:var(--card)}ol,ul{padding-inline-start:22px;margin:6px 0}")
             .Append("</style></head><body><main>");
            h.Append("<div class=\"top\"><span class=\"sub\">SellyStore · Sellybot</span><a class=\"lang\" href=\"?lang=").Append(en ? "ar" : "en").Append("\">").Append(en ? "عربي" : "English").Append("</a></div>");
            h.Append("<h1>").Append(T("ربط برنامجك بمتجر SellyStore", "Connect your program to a SellyStore store")).Append("</h1><p class=\"sub\">")
             .Append(T("لبرامج الحسابات والمخازن: اقرا المنتجات والأوردرات والزباين، وغيّر الكمية والأسعار وحالة الأوردر، واستقبل اللي بيحصل أول بأول.", "For accounting and warehouse programs: read products, orders and customers, change stock, prices and an order's status, and hear what happens as it happens.")).Append("</p>");

            // ---------- start ----------
            h.Append("<h2>").Append(T("البداية", "Getting started")).Append("</h2><div class=\"card\"><ol><li>")
             .Append(T("صاحب المتجر يعمل مفتاح من «متجري» ثم «ربط ببرامج تانية»، ويختار المفتاح يقدر يعمل إيه. المفتاح بيظهر مرة واحدة بس.", "The store's owner makes a key in «متجري», then «ربط ببرامج تانية», and picks what it may do. The key is shown once only."))
             .Append("</li><li>").Append(T("ابعته في كل طلب في الـ header ده:", "Send it with every request in this header:")).Append(" <code>Authorization: Bearer ssk_…</code></li><li>")
             .Append(T("العنوان الأساسي:", "The base address:")).Append(" <code>").Append(H(b)).Append("</code></li></ol><pre>curl ").Append(H(b)).Append("/me \\\n  -H \"Authorization: Bearer ssk_xxxxxxxx\"</pre>");
            h.Append("<ul><li>").Append(T("كل حاجة JSON بـ UTF-8، والفلوس بالجنيه المصري (EGP).", "Everything is JSON in UTF-8; money is in Egyptian pounds (EGP)."))
             .Append("</li><li>").Append(T("الأوقات ISO 8601 بتوقيت UTC، زي ", "Times are ISO 8601 in UTC, like ")).Append("<code>2026-11-26T09:30:00Z</code>").Append(T(". يوم من غير وقت (", ". A day without a time (")).Append("<code>2026-11-26</code>").Append(T(") يعني يوم المتجر بتوقيت القاهرة.", ") means the store's day, Cairo time."))
             .Append("</li><li>").Append(T("القوايم صفحات: ", "Lists come in pages: ")).Append("<code>page</code>, <code>per_page</code>").Append(T("، والرد فيه ", "; the answer has ")).Append("<code>data</code>, <code>total</code>, <code>has_more</code>, <code>server_time</code>.")
             .Append("</li><li>").Append(T("عشان تزامن: احفظ ", "To sync: keep ")).Append("<code>server_time</code>").Append(T(" من آخر رد وابعته المرة الجاية في ", " from your last answer and send it next time as ")).Append("<code>updated_since</code>").Append(T(". ممكن يرجعلك حاجة مرتين، مش هيفوتك حاجة.", ". Something may come twice; nothing is missed."))
             .Append("</li><li>").Append(T("أقصى ", "At most ")).Append(PerMinute).Append(T(" طلب في الدقيقة لكل مفتاح، وبعدها ", " requests a minute per key; after that ")).Append("<code>429</code>").Append(T(" ومعاه ", " with ")).Append("<code>Retry-After</code>.")
             .Append("</li><li>").Append(T("لو برنامجك مايعرفش يبعت PATCH، ابعت POST على نفس العنوان.", "If your program can't send PATCH, send POST to the same address.")).Append("</li><li>")
             .Append(T("كل تغيير بيتسجّل عند صاحب المتجر باسم المفتاح.", "Every change is logged for the store's owner with the key's name.")).Append("</li></ul></div>");

            // ---------- scopes ----------
            h.Append("<h2>").Append(T("المفتاح يقدر يعمل إيه", "What a key may do")).Append("</h2><div class=\"card tw\"><table><tr><th>scope</th><th>").Append(T("يعني", "Means")).Append("</th></tr>");
            foreach (var s in Scopes) h.Append("<tr><td><code>").Append(s[0]).Append("</code></td><td>").Append(H(T(s[1], s[2]))).Append("</td></tr>");
            h.Append("</table></div>");

            // ---------- the routes ----------
            h.Append("<h2>").Append(T("العناوين", "Endpoints")).Append("</h2>");
            foreach (var r in Routes)
            {
                h.Append("<div class=\"card\"><div class=\"rt\"><span class=\"m ").Append(r.Method).Append("\">").Append(r.Method).Append("</span><span class=\"p\">/api/v1").Append(H(r.Path)).Append("</span>").Append(r.Scope == null ? "" : "<span class=\"sc\">" + r.Scope + "</span>").Append("</div><p>").Append(H(T(r.Ar, r.En))).Append("</p>");
                if (r.Params.Length > 0) { h.Append("<div class=\"tw\"><table>"); foreach (var p in r.Params) h.Append("<tr><td><code>").Append(H(p[0])).Append("</code></td><td>").Append(H(T(p[1], p[2]))).Append("</td></tr>"); h.Append("</table></div>"); }
                if (r.Sample != null) h.Append("<pre>").Append(H(r.Sample)).Append("</pre>");
                h.Append("</div>");
            }
            h.Append("<div class=\"card\"><h3>").Append(T("شكل المنتج", "A product")).Append("</h3><p>").Append(T("المقاس (variant) هو اللي ليه كمية وSKU. المنتج من غير مقاسات ليه مقاس واحد من غير اختيارات.", "A size (variant) is what has a stock and a SKU. A product with no sizes has one variant with no options.")).Append("</p><pre>")
             .Append(H("{ \"id\": 9, \"name\": \"عباية كريب\", \"sku\": \"ABY\", \"status\": \"active\", \"price\": 500, \"old_price\": 650, \"track_stock\": true, \"stock_total\": 14,\n  \"variants\": [ { \"id\": 55, \"sku\": \"ABY-M\", \"options\": [\"M\"], \"price\": 500, \"stock\": 7, \"active\": true } ],\n  \"updated_at\": \"2026-11-26T09:30:00Z\" }")).Append("</pre>")
             .Append("<h3>").Append(T("شكل الأوردر", "An order")).Append("</h3><pre>")
             .Append(H("{ \"id\": 31, \"number\": 1042, \"status\": \"confirmed\", \"customer\": { \"name\": \"…\", \"phone\": \"01012345678\" },\n  \"shipping_address\": { \"governorate\": \"القاهرة\", \"city\": \"المعادي\", \"address\": \"…\" },\n  \"items\": [ { \"product_id\": 9, \"variant_id\": 55, \"sku\": \"ABY-M\", \"quantity\": 2, \"unit_price\": 500, \"total\": 1000 } ],\n  \"subtotal\": 1000, \"discount\": 0, \"shipping\": 60, \"total\": 1060, \"paid\": 0, \"due\": 1060, \"currency\": \"EGP\" }")).Append("</pre>")
             .Append("<p>").Append(T("الحالات: ", "Statuses: ")).Append(string.Join(", ", ShopOrders.Statuses.Select(x => "<code>" + x + "</code> (" + H(ShopOrders.StatusText(x, en)) + ")"))).Append("</p></div>");

            // ---------- webhooks ----------
            h.Append("<h2>Webhooks</h2><div class=\"card\"><p>").Append(T("صاحب المتجر بيحط عنوان برنامجك (https) ويختار الأحداث، فبنبعتلك POST بـ JSON أول ما الحدث يحصل.", "The store's owner adds your program's address (https) and picks the events; we POST JSON to it as soon as one happens.")).Append("</p><div class=\"tw\"><table><tr><th>type</th><th>").Append(T("إمتى", "When")).Append("</th><th>data</th></tr>");
            foreach (var e in ShopApiHooks.Events) h.Append("<tr><td><code>").Append(e[0]).Append("</code></td><td>").Append(H(T(e[1], e[2]))).Append("</td><td><code>").Append(e[0].StartsWith("order") ? "order" + (e[0] == "order.status_changed" ? ", from, to" : "") : e[0] == "stock.low" ? "product_id, variant_id, sku, stock, low_at" : "product (deleted: true)").Append("</code></td></tr>");
            h.Append("<tr><td><code>ping</code></td><td>").Append(T("لما صاحب المتجر يدوس «جرّب»", "When the owner presses «جرّب» (test)")).Append("</td><td><code>message</code></td></tr></table></div><pre>")
             .Append(H("POST https://your-program.example/hook\nX-SellyStore-Event: order.created\nX-SellyStore-Event-Id: evt_3f9c…\nX-SellyStore-Timestamp: 1764149400\nX-SellyStore-Signature: sha256=8d1e…\n\n{ \"id\": \"evt_3f9c…\", \"type\": \"order.created\", \"created_at\": \"2026-11-26T09:30:00Z\", \"store\": { \"slug\": \"nour-shop\", \"name\": \"…\" }, \"data\": { \"order\": { … } } }")).Append("</pre><ul><li>")
             .Append(T("ردّ بـ 2xx في أقل من 10 ثواني. أي رد تاني (أو تحويل 3xx) بنعتبره ماوصلش ونبعته تاني بعد دقيقة، 5، 15، ساعة، 3، 6، 12، يعني يوم تقريبًا، وبعدها بنقف وصاحب المتجر يقدر يبعته تاني.", "Answer 2xx within 10 seconds. Anything else (or a 3xx redirect) counts as not received; we send it again after a minute, 5, 15, an hour, 3, 6 and 12, about a day in all, then stop, and the owner can send it again."))
             .Append("</li><li>").Append(T("نفس الحدث ممكن يوصل مرتين: استخدم ", "The same event may arrive twice: use ")).Append("<code>X-SellyStore-Event-Id</code>").Append(T(" عشان ماتعملوش مرتين.", " so you don't act on it twice."))
             .Append("</li><li>").Append(T("اتأكد من التوقيع: احسب HMAC-SHA256 بالسر اللي صاحب المتجر أخده (whsec_…) لـ ", "Check the signature: HMAC-SHA256 with the secret the owner got (whsec_…) of ")).Append("<code>timestamp + \".\" + body</code>").Append(T(" (الـ body زي ما وصل بالظبط)، وقارنه بـ ", " (the body exactly as received), compared with ")).Append("<code>X-SellyStore-Signature</code>").Append(T("، وارفض لو الوقت أقدم من 5 دقايق.", ", and refuse it when the time is more than 5 minutes old.")).Append("</li></ul>")
             .Append("<pre>").Append(H("// Node.js\nconst crypto = require(\"crypto\");\nfunction ok(req, raw, secret) {\n  const ts = req.headers[\"x-sellystore-timestamp\"], sig = req.headers[\"x-sellystore-signature\"] || \"\";\n  if (Math.abs(Date.now() / 1000 - Number(ts)) > 300) return false;\n  const want = \"sha256=\" + crypto.createHmac(\"sha256\", secret).update(ts + \".\" + raw).digest(\"hex\");\n  return sig.length === want.length && crypto.timingSafeEqual(Buffer.from(sig), Buffer.from(want));\n}")).Append("</pre>")
             .Append("<pre>").Append(H("// PHP\n$raw = file_get_contents('php://input');\n$ts = $_SERVER['HTTP_X_SELLYSTORE_TIMESTAMP'];\n$want = 'sha256=' . hash_hmac('sha256', $ts . '.' . $raw, $secret);\n$ok = abs(time() - (int)$ts) <= 300 && hash_equals($want, $_SERVER['HTTP_X_SELLYSTORE_SIGNATURE'] ?? '');")).Append("</pre></div>");

            // ---------- errors ----------
            h.Append("<h2>").Append(T("الأخطاء", "Errors")).Append("</h2><div class=\"card\"><p>").Append(T("أي خطأ بيرجع بنفس الشكل، والـ code ثابت تقدر تعتمد عليه:", "Every error has the same shape, and its code is stable enough to rely on:")).Append("</p><pre>")
             .Append(H("{ \"error\": { \"code\": \"forbidden\", \"message\": \"This key may not change stock…\", \"message_ar\": \"المفتاح ده مش مسموحله…\" } }")).Append("</pre><div class=\"tw\"><table><tr><th>HTTP</th><th>code</th><th>").Append(T("يعني", "Means")).Append("</th></tr>");
            foreach (var e in Errors) h.Append("<tr><td>").Append(e[0]).Append("</td><td><code>").Append(H(e[1])).Append("</code></td><td>").Append(H(T(e[2], e[3]))).Append("</td></tr>");
            h.Append("</table></div><p>").Append(T("في ", "In ")).Append("<code>/products/bulk</code>").Append(T(" الرد 200، ولكل تغيير نتيجته في ", " the answer is 200, with each change's result in ")).Append("<code>results</code> (<code>ok</code>, <code>error</code>).</p></div>");
            h.Append("<p class=\"sub\">").Append(T("الصفحة دي بتتكتب من نفس القايمة اللي السيرفر بيشتغل بيها.", "This page is written from the same list the server works from.")).Append("</p></main></body></html>");
            res.ContentType = "text/html; charset=utf-8"; res.AddHeader("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"); res.AddHeader("X-Robots-Tag", "noindex");
            res.Write(h.ToString());
        }
    }
}
