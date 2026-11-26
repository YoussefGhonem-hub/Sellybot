// ShipBlu (docs.shipblu.com, its Postman collection and JS client, read 2026-11-26; last changed November 2023): the merchant's key in
// «Authorization: Api-Key ...». Their docs have no notifications, so the open shipments are asked about every hour, and no cancelling, so that stays on their page.
// Not tried on a real account: the pickup request's body isn't written anywhere, and a shipment is found by reading the newest ones for our reference.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public partial class CarrierShipBlu : Carrier
    {
        static string Base { get { return Web.Setting("ShipBluBase", "https://api.shipblu.com/api/v1").TrimEnd('/'); } }
        public override string Id { get { return "shipblu"; } }
        public override string Name { get { return "ShipBlu"; } }
        public override int PollMinutes { get { return 60; } }
        public override string BadInput(CarrierLink k) { k.Key = Regex.Replace(k.Key, "^Api-Key +", "", RegexOptions.IgnoreCase); return Regex.IsMatch(k.Key, @"^[\x21-\x7e]{16,300}$") ? null : "المفتاح ده مش كامل. انسخه من ShipBlu زي ما هو والصقه هنا."; }

        async Task<Carriers.Reply> Call(CarrierLink k, HttpMethod m, string path, object body = null)
        {
            var q = new HttpRequestMessage(m, Base + path); q.Headers.TryAddWithoutValidation("Accept", "application/json, application/pdf"); q.Headers.TryAddWithoutValidation("Authorization", "Api-Key " + k.Key);
            if (body != null) q.Content = new StringContent(ShopJson.Write(body), Encoding.UTF8, "application/json");
            var r = await Carriers.Send(Name, q); if (r.Good) return r;
            if (r.Status >= 500 || r.Status == 408) throw new CarrierException("ShipBlu فيها مشكلة دلوقتي. جرّب كمان شوية.", "net", unsure: true);
            if (r.Status == 429) throw new CarrierException("ShipBlu بتقول طلبات كتير. استنى دقيقة.", "busy");
            if (r.Status == 401 || r.Status == 403) throw new CarrierException("مفتاح ShipBlu مش شغال. اعمل مفتاح جديد من Integrations والصقه تاني.", "key", badKey: true);
            string why = First(r.Root, ""); if (why.StartsWith("customer.address.zone", StringComparison.Ordinal)) throw new CarrierException("ShipBlu مش بتوصّل للمنطقة دي، أو المنطقة اتغيّرت عندهم. اختارها تاني.", "zone");
            if (why.StartsWith("customer.email", StringComparison.Ordinal)) throw new CarrierException("ShipBlu بتطلب إيميل للشحنات. اكتبه في إعدادات ShipBlu.", "email"); if (why.StartsWith("customer.phone", StringComparison.Ordinal)) throw new CarrierException("ShipBlu مش قابلة رقم موبايل الزبون. راجعه في الأوردر.", "phone");
            throw new CarrierException("ShipBlu رفضت: " + (why.Length == 0 ? "رد " + r.Status : Carriers.Cut(why, 180)), "refused");
        }
        // their errors are nested by field: {"customer":{"address":{"zone":["..."]}}} -> "customer.address.zone: ..."
        static string First(object v, string path)
        {
            var d = v as Dictionary<string, object>; if (d != null) { foreach (var kv in d) { var s = First(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key); if (s.Length > 0) return s; } return ""; }
            var l = ShopJson.List(v); if (l.Count > 0) return First(l[0], path); var t = ShopJson.Str(v, 200); return t.Length == 0 ? "" : path.Length == 0 || path == "detail" ? t : path + ": " + t;
        }
        static List<Dictionary<string, object>> Rows(Carriers.Reply r) { var l = ShopJson.List(r.Root); if (l.Count == 0) l = ShopJson.List(ShopJson.At(r.Body, "results")); return l.OfType<Dictionary<string, object>>().ToList(); }
        static string T(Dictionary<string, object> d, string k, int n = 60) { return ShopJson.Str(ShopJson.At(d, k), n); }

        public override async Task Check(CarrierLink k)
        {
            var l = Rows(await Call(k, HttpMethod.Get, "/merchants/")); if (l.Count == 0) throw new CarrierException("المفتاح شغال بس مالوش حساب تاجر على ShipBlu.", "key", badKey: true); k.AccName = T(l[0], "name", 100);
        }

        public override async Task<CarrierMade> Create(CarrierLink k, CarrierParcel p)
        {
            int size; if (!int.TryParse(k.S("size", "1"), out size) || size < 1 || size > 9) size = 1; int zone; int.TryParse(p.Area.AreaId, out zone);
            var who = new Dictionary<string, object> { { "full_name", p.Name }, { "phone", p.Phone }, { "address", new Dictionary<string, object> { { "line_1", p.Address }, { "line_2", string.IsNullOrWhiteSpace(p.Landmark) ? p.Area.Area : p.Landmark }, { "zone", zone } } } }; if (p.Phone2 != null) who["secondary_phone"] = p.Phone2; if (p.Email.Length > 0) who["email"] = p.Email;
            var b = new Dictionary<string, object> { { "customer", who }, { "packages", new[] { new Dictionary<string, object> { { "package_size", size }, { "description", p.Goods }, { "fragile", false } } } }, { "cash_amount", p.Cod }, { "merchant_order_reference", p.Ref }, { "is_customer_allowed_to_open_packages", k.B("open", false) } };
            if (p.Note.Length > 0) b["order_notes"] = p.Note; if (k.B("value", true) && p.Value > 0) b["declared_value"] = p.Value; if (p.Day != null) b["preferred_date"] = p.Day;
            var r = await Call(k, HttpMethod.Post, "/delivery-orders/", b); return new CarrierMade { Tracking = T(r.Body, "tracking_number"), Id = T(r.Body, "id") };
        }

        // the list's filters aren't all written: whatever it gives is read for our reference, newest pages first
        public override async Task<CarrierMade> Find(CarrierLink k, string reference)
        {
            for (int page = 0; page < 3; page++)
            {
                var r = await Call(k, HttpMethod.Get, "/delivery-orders/?limit=100&offset=" + page * 100 + "&merchant_order_reference=" + Uri.EscapeDataString(reference)); var l = Rows(r);
                var x = l.FirstOrDefault(o => T(o, "merchant_order_reference") == reference && !string.Equals(T(o, "status"), "CANCELLED", StringComparison.OrdinalIgnoreCase) && T(o, "tracking_number").Length > 0); if (x != null) return new CarrierMade { Tracking = T(x, "tracking_number"), Id = T(x, "id") };
                if (l.Count < 100) break;
            }
            return null;
        }

        public override async Task<byte[]> Labels(CarrierLink k, List<CarrierShip> l, string size)
        {
            var r = await Call(k, HttpMethod.Get, "/orders/shipping-label/?tracking_numbers=" + Uri.EscapeDataString(string.Join(",", l.Select(x => x.Tracking))).Replace("%2C", ",") + "&type=pdf" + (size == "A6" ? "&one_per_page=True" : ""));
            var pdf = Carriers.PdfIn(r); if (pdf == null) throw new CarrierException("ShipBlu مابعتتش البوليصة.", "label"); return pdf;
        }

        public override async Task<string> Pickup(CarrierLink k, DateTime day, int parcels, string place, string note, List<CarrierShip> waiting)
        {
            if (waiting.Count == 0) throw new CarrierException("مفيش شحنات مستنية الاستلام. ابعت الأوردرات الأول.", "none");
            var b = new Dictionary<string, object> { { "tracking_numbers", waiting.Select(x => x.Tracking).ToArray() }, { "orders", waiting.Select(x => x.TheirId).Where(x => !string.IsNullOrEmpty(x)).Select(x => { int n; return int.TryParse(x, out n) ? (object)n : x; }).ToArray() }, { "pickup_date", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } };
            await Call(k, HttpMethod.Post, "/delivery-orders/request-pickup/", b); return "";
        }

        public override Task Cancel(CarrierLink k, CarrierShip x) { throw new CarrierException("ShipBlu مابتسمحش بالإلغاء من بره حسابها. الغيها من حسابك على ShipBlu، وهنعرف لوحدنا خلال ساعة (أو دوس «حدّث الحالة»).", "nocancel"); }

        public override async Task<List<CarrierEvent>> Ask(CarrierLink k, List<CarrierShip> l)
        {
            var o = new List<CarrierEvent>();
            foreach (var x in l)
            {
                var d = Rows(await Call(k, HttpMethod.Get, "/delivery-orders/?tracking_number=" + Uri.EscapeDataString(x.Tracking))).FirstOrDefault(y => T(y, "tracking_number") == x.Tracking); if (d == null) continue;
                var e = Map(T(d, "status", 40)); e.Tracking = x.Tracking; e.Attempts = ShopJson.Int(ShopJson.At(d, "delivery_attempts"));
                // asked once an hour: an attempt that failed in between shows only in the count
                if (e.Attempts > x.Attempts && (e.Ours == "transit" || e.Ours == "made")) o.Add(new CarrierEvent { Tracking = x.Tracking, Code = "DELIVERY_ATTEMPTED", Ours = "attempt", Attempts = e.Attempts });
                o.Add(e);
            }
            return o;
        }

        public override async Task<List<CarrierAreaRow>> Areas(CarrierLink k)
        {
            var l = new List<CarrierAreaRow>();
            foreach (var g in Rows(await Call(k, HttpMethod.Get, "/governorates/")).Where(x => T(x, "id").Length > 0))
                foreach (var y in Rows(await Call(k, HttpMethod.Get, "/governorates/" + T(g, "id") + "/cities/")))
                    foreach (var z in Rows(await Call(k, HttpMethod.Get, "/cities/" + T(y, "id") + "/zones/")))
                    {
                        string ge, ce, ze; l.Add(new CarrierAreaRow { GovId = T(g, "id"), Gov = Two(T(g, "name", 120), out ge), GovEn = ge, CityId = T(y, "id"), City = Two(T(y, "name", 120), out ce), CityEn = ce, AreaId = T(z, "id"), Area = Two(T(z, "name", 150), out ze), AreaEn = ze });
                    }
            return l;
        }
        // «القاهرة - Cairo»
        static string Two(string s, out string en)
        {
            en = null; var m = Regex.Match(s, @"^(.*[؀-ۿ].*?)\s+-\s+([A-Za-z0-9].*)$"); if (m.Success) { en = m.Groups[2].Value.Trim(); return m.Groups[1].Value.Trim(); }
            m = Regex.Match(s, @"^([A-Za-z0-9].*?)\s+-\s+(.*[؀-ۿ].*)$"); if (m.Success) { en = m.Groups[1].Value.Trim(); return m.Groups[2].Value.Trim(); }
            return s;
        }

        // their tutorial prints the statuses in capitals, their API and public clients send them in small letters: either is read
        public static CarrierEvent Map(string status)
        {
            status = (status ?? "").Trim().ToUpperInvariant(); var e = new CarrierEvent { Code = status };
            switch (status)
            {
                case "CREATED": case "PICKUP_REQUESTED": case "PICKUP_RESCHEDULED": case "OUT_FOR_PICKUP": e.Ours = "made"; break;
                case "PICKED_UP": e.Ours = "picked"; break;
                case "IN_TRANSIT": case "EN_ROUTE": case "REATTEMPT_DELIVERY": e.Ours = "transit"; break;
                case "ON_HOLD": case "OPS_DELAY": e.Ours = "hold"; break;
                case "LOST": case "DAMAGED": e.Ours = "lost"; e.Text = status == "LOST" ? "ShipBlu سجّلتها مفقودة" : "ShipBlu سجّلتها متكسّرة"; break;
                case "OUT_FOR_DELIVERY": e.Ours = "out"; break;
                case "DELIVERY_ATTEMPTED": e.Ours = "attempt"; break;
                case "DELIVERED": e.Ours = "delivered"; break;
                case "RETURN_TO_ORIGIN": case "RETURNED_TO_ORIGIN": case "OUT_FOR_RETURN": e.Ours = "returning"; break;
                case "RETURN_REFUSED": e.Ours = "hold"; e.Loud = true; e.Text = "رفضت استلام المرتجع"; break;
                case "RETURN_POSTPONED": e.Ours = "hold"; e.Text = "المرتجع اتأجّل لبكرة"; break;
                case "RETURNED": e.Ours = "returned"; break;
                case "CANCELLED": e.Ours = "cancelled"; break;
            }
            return e;
        }
    }
}
