// «الفاتورة»: one invoice for an order, written once here and used by the printed page (ShopDesk.Print "invoice", which the owner's
// «فاتورة» / «فواتير» open) and by its «نزّلها صورة» / «نزّلها PDF» (shop-admin/invoice.js draws this same page in the browser, so Arabic
// looks as it does on screen). In the order's language. Everything the order holds, and only what it holds: the store's logo and name, the
// seller's details that are written (legal name, address, phone, commercial register, tax number), the number and the day, the customer (or
// the branch they collect from, or the gift's recipient), every line with its options and the customer's words, the offers, the coupon, the
// shipping, the fees, what was paid (a transfer, a card, a gift card) and what is left on delivery, the way of paying, the delivery day, who
// delivers (ShopSelf) or the company and its number, the notes. The logo comes inside the page (a data address), so drawing it needs nothing else.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class ShopInvoice
    {
        static string H(string s) { return HttpUtility.HtmlEncode(s ?? ""); }
        static string B(string s) { return "<bdi>" + H(s) + "</bdi>"; }   // words someone typed: their own direction, whatever the invoice's
        const string Css = ".inv{min-height:auto;font-size:13.5px}.inv h1{margin:0;font-size:22px}.inv .top{display:flex;justify-content:space-between;gap:6mm;align-items:flex-start}.inv .who{display:flex;gap:4mm;align-items:center}.inv .lg{width:22mm;height:22mm;flex:none;background:center/contain no-repeat}.inv .meta{text-align:end;white-space:nowrap}.inv .meta b{font-size:19px;display:block}"
            + ".inv .pp{display:grid;grid-template-columns:1fr 1fr;gap:4mm;margin:4mm 0}.inv .box{border:1px solid #ccc;border-radius:2mm;padding:3mm}.inv .box h3{margin:0 0 1mm;font-size:12px;color:#555;font-weight:600}.inv td small{display:block;color:#555}.inv .q,.inv .m{white-space:nowrap}.inv .m{text-align:end}.inv .tot{width:78mm;margin-inline-start:auto;margin-top:3mm}.inv .tot td:last-child{text-align:end;white-space:nowrap}.inv .tot .b td{font-weight:700;font-size:16px;border-top:2px solid #000}.inv .tot .d td{font-weight:700;font-size:15px;background:#f3f3f3}"
            + ".inv .notes{margin-top:4mm;display:grid;gap:1mm}.inv .notes p{margin:0}.inv .foot{margin-top:5mm;border-top:1px solid #ccc;padding-top:2mm;color:#444;font-size:11.5px}.bar{flex-wrap:wrap}.bar button:disabled{opacity:.6}@media (max-width:820px){.pg.inv{width:auto;margin:8px;padding:6mm}.inv .pp{grid-template-columns:1fr}}";

        // the page's part: the styles, the logo once, and the script with the two download buttons
        public static string Head(SqlConnection c, Shop sh)
        {
            var lg = Logo(sh); return "<style>" + Css + (lg.Length > 0 ? ".inv .lg{background-image:url(" + lg + ")}" : "") + "</style><script src=\"../shop-admin/invoice.js?v=20261126a\" defer></script>";
        }
        static readonly ConcurrentDictionary<string, KeyValuePair<DateTime, string>> Logos = new ConcurrentDictionary<string, KeyValuePair<DateTime, string>>();
        // the store's logo as a data address (its small copy: from the disk, or from the store's own picture address)
        static string Logo(Shop sh)
        {
            var path = ShopJson.Str(ShopJson.At(sh.Look(false), "logo"), 160); if (path.Length == 0 || !path.StartsWith(sh.StoreId + "/", StringComparison.Ordinal)) return "";
            KeyValuePair<DateTime, string> had; if (Logos.TryGetValue(path, out had) && DateTime.UtcNow - had.Key < TimeSpan.FromMinutes(30)) return had.Value;
            string uri = "";
            try
            {
                var b = ShopImages.Read(path, true) ?? ShopImages.Read(path, false);
                // on R2 only: asked off the request's context, so waiting for it here can't lock
                if (b == null) { var t = Task.Run(() => ShopFetch.Get(ShopImages.Url(path, true), 400000)); if (t.Wait(TimeSpan.FromSeconds(8)) && t.Result.Bytes != null) b = t.Result.Bytes; }
                string type = b == null || b.Length < 12 ? null : b[0] == 0x89 && b[1] == 'P' ? "image/png" : b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg" : b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "image/webp" : null;
                if (type != null && b.Length <= 400000) uri = "data:" + type + ";base64," + Convert.ToBase64String(b);
            }
            catch (Exception ex) { Log.Error(ex); }
            Logos[path] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, uri); return uri;
        }

        public static string Html(SqlConnection c, Shop sh, ShopOrder o)
        {
            bool en = o.Lang == "en"; Func<string, string, string> T = (ar, e) => en ? e : ar; Func<decimal, string> M = v => en ? "EGP " + v.ToString(v == Math.Floor(v) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture) : ShopOrders.Money(v);
            var legal = ShopJson.Sub(sh.Settings, "legal"); var tax = ShopJson.Sub(sh.Settings, "tax"); Func<string, string> L = k => ShopJson.Str(ShopJson.At(legal, k), 200);
            string store = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name, phone = L("phone").Length > 0 ? L("phone") : ShopJson.Str(ShopJson.At(ShopJson.Sub(sh.Settings, "contact"), "phone"));
            var h = new StringBuilder("<div class=\"pg inv\" dir=\"").Append(en ? "ltr" : "rtl").Append("\" lang=\"").Append(en ? "en" : "ar").Append("\" data-no=\"").Append(o.No).Append("\"><div class=\"top\"><div class=\"who\">");
            if (ShopJson.Str(ShopJson.At(sh.Look(false), "logo")).Length > 0) h.Append("<div class=\"lg\" role=\"img\" aria-label=\"").Append(H(store)).Append("\"></div>");
            h.Append("<div><h1>").Append(B(store)).Append("</h1>");
            foreach (var line in new[] { L("seller"), L("address") }.Where(x => x.Length > 0)) h.Append(B(line)).Append("<br>");
            if (phone.Length > 0) h.Append("<span class=\"num\">").Append(H(phone)).Append("</span><br>");
            if (L("reg").Length > 0) h.Append(T("سجل تجاري: ", "Commercial register: ")).Append("<span class=\"num\">").Append(H(L("reg"))).Append("</span><br>");
            if (L("tax").Length > 0) h.Append(T("بطاقة ضريبية: ", "Tax number: ")).Append("<span class=\"num\">").Append(H(L("tax"))).Append("</span>");
            h.Append("</div></div><div class=\"meta\"><b>").Append(T("فاتورة", "Invoice")).Append("</b>").Append(T("أوردر ", "Order ")).Append("<span class=\"num\">").Append(H(o.NoText)).Append("</span><br><span class=\"num\">").Append(Carriers.Cairo(o.CreatedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("</span></div></div>");

            // who and where
            var br = ShopJson.Sub(o.Extras, "branch"); var rc = ShopJson.Sub(o.Extras, "recv");
            h.Append("<div class=\"pp\"><div class=\"box\"><h3>").Append(T("العميل", "Customer")).Append("</h3><b>").Append(B(o.Name)).Append("</b><br><span class=\"num\">").Append(H(ShopOrders.Local(o.Phone))).Append(o.Phone2 == null ? "" : " · " + H(ShopOrders.Local(o.Phone2))).Append("</span></div><div class=\"box\"><h3>");
            if (br.Count > 0) h.Append(T("الاستلام من الفرع", "Collected from")).Append("</h3>").Append(B(ShopJson.Str(ShopJson.At(br, "n")))).Append(ShopJson.Str(ShopJson.At(br, "ad")).Length > 0 ? "<br>" + B(ShopJson.Str(ShopJson.At(br, "ad"))) : "");
            else { h.Append(T("التوصيل لـ", "Deliver to")).Append("</h3>"); if (rc.Count > 0) h.Append("<b>").Append(B(ShopJson.Str(ShopJson.At(rc, "n")))).Append("</b> · <span class=\"num\">").Append(H(ShopOrders.Local(ShopJson.Str(ShopJson.At(rc, "p"))))).Append("</span><br>"); h.Append(B(ShopOrders.AddressText(o))); }
            h.Append("</div></div>");

            // the lines
            h.Append("<table><thead><tr><th>").Append(T("المنتج", "Item")).Append("</th><th class=\"q\">").Append(T("الكمية", "Qty")).Append("</th><th class=\"m\">").Append(T("سعر القطعة", "Unit price")).Append("</th><th class=\"m\">").Append(T("الإجمالي", "Total")).Append("</th></tr></thead><tbody>");
            foreach (var i in o.Items)
            {
                h.Append("<tr><td>").Append(B(i.Name)); if (!string.IsNullOrEmpty(i.Opts)) h.Append("<small>").Append(B(i.Opts)).Append("</small>"); if (!string.IsNullOrEmpty(i.Extra)) h.Append("<small>").Append(B(i.Extra)).Append("</small>");
                if (i.Discount > 0) h.Append("<small>").Append(T("خصم ", "Discount ")).Append(H(M(i.Discount))).Append("</small>"); if (i.Returned > 0) h.Append("<small>").Append(T("رجع منه ", "Returned: ")).Append(i.Returned).Append("</small>");
                h.Append("</td><td class=\"q num\">").Append(i.Qty).Append("</td><td class=\"m\">").Append(H(M(i.Price))).Append("</td><td class=\"m\">").Append(H(M(i.Sum))).Append("</td></tr>");
            }
            h.Append("</tbody></table><table class=\"tot\"><tr><td>").Append(T("المنتجات", "Items")).Append("</td><td>").Append(H(M(o.Subtotal))).Append("</td></tr>");
            // the offers one by one (points and a friend's gift are among them), then the coupon: the rest of the discount
            decimal offers = 0;
            foreach (var f in ShopJson.Arr(o.OffersJson).OfType<Dictionary<string, object>>())
            {
                decimal a = ShopJson.Dec(ShopJson.At(f, "a")); if (a <= 0) continue; offers += a; var n = ShopJson.At(f, "n"); var nl = ShopJson.List(n);
                string name = nl.Count > 0 ? ShopJson.Str(en && nl.Count > 1 && ShopJson.Str(nl[1]).Length > 0 ? nl[1] : nl[0], 80) : ShopJson.Str(n, 80);
                h.Append("<tr><td>").Append(B(name.Length > 0 ? name : T("عرض", "Offer"))).Append("</td><td>−").Append(H(M(a))).Append("</td></tr>");
            }
            decimal rest = o.Discount - offers; if (rest > 0) h.Append("<tr><td>").Append(o.Coupon != null ? T("كود الخصم ", "Coupon ") + "<span class=\"num\">" + H(o.Coupon) + "</span>" : T("خصم", "Discount")).Append("</td><td>−").Append(H(M(rest))).Append("</td></tr>");
            h.Append("<tr><td>").Append(br.Count > 0 ? T("الاستلام من الفرع", "Pickup") : T("الشحن", "Delivery")).Append("</td><td>").Append(o.Ship > 0 ? H(M(o.Ship)) : T("مجاني", "Free")).Append("</td></tr>");
            foreach (var f in ShopJson.Arr(o.FeesJson).Select(ShopJson.List).Where(f => f.Count > 2)) { var nm = ShopJson.List(f[1]); h.Append("<tr><td>").Append(H(ShopJson.Str(en && nm.Count > 1 ? nm[1] : nm.FirstOrDefault(), 60))).Append("</td><td>").Append(H(M(ShopJson.Dec(f[2])))).Append("</td></tr>"); }
            h.Append("<tr class=\"b\"><td>").Append(T("الإجمالي", "Total")).Append("</td><td>").Append(H(M(o.Total))).Append("</td></tr>");
            decimal pct = ShopJson.Bool(ShopJson.At(tax, "on")) ? ShopJson.Dec(ShopJson.At(tax, "pct")) : 0, taxAmt = pct > 0 ? Math.Round((o.Total - o.Ship) * pct / (100 + pct), 2) : 0;
            if (taxAmt > 0) h.Append("<tr><td><small>").Append(T("منهم ضريبة ", "Including VAT ")).Append(pct.ToString("0.##", CultureInfo.InvariantCulture)).Append("%</small></td><td><small>").Append(H(M(taxAmt))).Append("</small></td></tr>");
            // what was paid: a gift card, and the rest by the order's way (a transfer or a card, once it arrived)
            decimal gc = ShopJson.Dec(ShopJson.At(ShopJson.Sub(ShopJson.Sub(o.Extras, "rw"), "gc"), "a")), other = Math.Max(0, o.Paid - gc);
            if (gc > 0) h.Append("<tr><td>").Append(T("اتدفع بكارت هدية ", "Paid by gift card ")).Append("<span class=\"num\">").Append(H(ShopJson.Str(ShopJson.At(ShopJson.Sub(ShopJson.Sub(o.Extras, "rw"), "gc"), "c"), 30))).Append("</span></td><td>−").Append(H(M(gc))).Append("</td></tr>");
            if (other > 0) h.Append("<tr><td>").Append(T("اتدفع ", "Paid by ")).Append(H(ShopPay.WayName(o.Pay, en))).Append(o.Deposit > 0 && o.Deposit < o.Total ? T(" (عربون)", " (deposit)") : "").Append("</td><td>−").Append(H(M(other))).Append("</td></tr>");
            else if (o.Deposit > 0 && o.PayState != "paid") h.Append("<tr><td><small>").Append(T("عربون مطلوب بـ ", "Deposit asked by ")).Append(H(ShopPay.WayName(o.Pay, en))).Append(T(" (لسه ماوصلش)", " (not received yet)")).Append("</small></td><td><small>").Append(H(M(o.Deposit))).Append("</small></td></tr>");
            if (o.Refund > 0) h.Append("<tr><td>").Append(T("رجع للعميل", "Refunded")).Append("</td><td>").Append(H(M(o.Refund))).Append("</td></tr>");
            h.Append("<tr class=\"d\"><td>").Append(br.Count > 0 ? T("المطلوب عند الاستلام", "Due on collection") : T("المطلوب عند التوصيل", "Due on delivery")).Append("</td><td>").Append(o.Due > 0 ? H(M(o.Due)) : T("مفيش، مدفوع", "Nothing, paid")).Append("</td></tr></table>");

            // the rest of what the order says
            var notes = new List<string> { "<b>" + T("الدفع: ", "Payment: ") + "</b>" + H(ShopPay.WayName(o.Pay, en)) };
            string day = ShopJson.Str(ShopJson.At(o.Extras, "day"), 10), part = ShopJson.Str(ShopJson.At(o.Extras, "dpart"), 4); if (day.Length == 10) notes.Add("<b>" + T("يوم التوصيل: ", "Delivery day: ") + "</b>" + H(ShopShipDay.Text(day, en) + (part.Length > 0 ? " " + ShopShipDay.PartText(part, en) : "")));
            var pk = ShopShipPick.NameOf(o, en); if (pk != null && string.IsNullOrEmpty(o.Carrier)) notes.Add("<b>" + T("هيوصل مع: ", "Delivered by: ") + "</b>" + H(pk));   // the company the customer chose
            var by = ShopJson.Sub(o.Extras, "by");
            if (!string.IsNullOrEmpty(o.Tracking) || !string.IsNullOrEmpty(o.Carrier)) notes.Add("<b>" + T("الشحن مع: ", "Shipped with: ") + "</b>" + B(o.Carrier ?? "") + (string.IsNullOrEmpty(o.Tracking) ? "" : " · " + T("رقم التتبع ", "tracking ") + "<span class=\"num\">" + H(o.Tracking) + "</span>"));
            else if (ShopJson.Str(ShopJson.At(by, "n")).Length > 0) notes.Add("<b>" + T("هيوصّله: ", "Delivered by: ") + "</b>" + B(ShopJson.Str(ShopJson.At(by, "n"), 40)) + " · <span class=\"num\">" + H(ShopJson.Str(ShopJson.At(by, "p"), 20)) + "</span>");
            if (o.Extras.ContainsKey("gift")) notes.Add("<b>" + T("رسالة الهدية: ", "Gift message: ") + "</b>" + B(ShopJson.Str(o.Extras["gift"], 200)));
            if (!string.IsNullOrWhiteSpace(o.Note)) notes.Add("<b>" + T("ملاحظة العميل: ", "Customer's note: ") + "</b>" + B(o.Note));
            h.Append("<div class=\"notes\">").Append(string.Join("", notes.Select(x => "<p>" + x + "</p>"))).Append("</div><p class=\"foot\">").Append(T("الاسترجاع خلال ", "Returns within ")).Append(ShopAccount.ReturnDays(sh)).Append(T(" يوم من الاستلام", " days of delivery")).Append(" · <span class=\"num\">").Append(H(sh.Url)).Append("</span></p></div>");
            return h.ToString();
        }
    }
}
