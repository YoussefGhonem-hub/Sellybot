// Bosta (docs.bosta.co, read 2026-11-26): the merchant's API key in Authorization as it is. Every delivery carries our notification address and a
// secret header, so Bosta tells us each change; the delivery is also asked about once a day for what never arrived.
// Not tried on a real account yet: the search's, the labels' and the pickups' answers are read loosely, whatever shape they come in.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public class CarrierBosta : Carrier
    {
        const string Egypt = "60e4482c7cb7d4bc4849c4d5";   // Bosta's id of the country
        static string Base { get { return Web.Setting("BostaBase", "https://app.bosta.co/api/v2").TrimEnd('/'); } }
        public override string Id { get { return "bosta"; } }
        public override string Name { get { return "بوسطة"; } }
        public override int PollMinutes { get { return 24 * 60; } }
        public override decimal MaxCod { get { return 30000; } }
        public override string BadInput(CarrierLink k) { return Regex.IsMatch(k.Key, @"^[\x21-\x7e]{20,500}$") ? null : "المفتاح ده مش كامل. انسخه من بوسطة زي ما هو والصقه هنا."; }

        async Task<Carriers.Reply> Call(CarrierLink k, HttpMethod m, string path, object body = null)
        {
            var q = new HttpRequestMessage(m, Base + path); q.Headers.TryAddWithoutValidation("Accept", "application/json"); if (k != null) q.Headers.TryAddWithoutValidation("Authorization", k.Key);
            if (body != null) q.Content = new StringContent(ShopJson.Write(body), Encoding.UTF8, "application/json");
            var r = await Carriers.Send(Name, q); if (r.Good && ShopJson.Bool(ShopJson.At(r.Body, "success"), true)) return r;
            string code = ShopJson.Str(ShopJson.At(r.Body, "errorCode"), 10), msg = ShopJson.Str(ShopJson.At(r.Body, "message"), 200), ar;
            if (r.Status >= 500 || r.Status == 408) throw new CarrierException("بوسطة فيها مشكلة دلوقتي. جرّب كمان شوية.", "net", unsure: true);
            if (r.Status == 429) throw new CarrierException("بوسطة بتقول طلبات كتير. استنى دقيقة.", "busy");
            if (r.Status == 401 || code == "1007" || code == "1028") throw new CarrierException("مفتاح بوسطة مش شغال. اعمل مفتاح جديد من Settings ثم API Integration والصقه تاني.", code, badKey: true);
            if (r.Status == 403 || code == "1008") throw new CarrierException("المفتاح ده مالوش صلاحية للطلب ده. اعمل مفتاح Read/Write (أو Full access عشان الإلغاء).", "forbidden");
            if (Regex.IsMatch(msg, "uniqueBusinessReference|business ?reference.*(exist|duplicat|unique)|(exist|duplicat).*business ?reference", RegexOptions.IgnoreCase)) throw new CarrierException("الأوردر ده متسجّل عند بوسطة قبل كده.", "dup");
            throw new CarrierException(Errors.TryGetValue(code, out ar) ? ar : "بوسطة رفضت: " + (msg.Length == 0 ? "رد " + r.Status : msg), code);
        }
        static readonly Dictionary<string, string> Errors = new Dictionary<string, string> {
            { "1010", "حساب بوسطة ده مش حساب تاجر." }, { "1073", "ضيف عنوان استلام في حسابك على بوسطة الأول." }, { "2150", "بوسطة بتطلب صورة للمنتج عشان الزبون يفتح الشحنة." }, { "2151", "لينك صورة المنتج مش شغال عند بوسطة." }, { "2152", "صور المنتج لازم كلها بنفس الطريقة." }, { "3001", "بوسطة مش عارفة المدينة دي. اختار المنطقة تاني." }, { "3002", "بوسطة مش عارفة المنطقة دي. اختارها تاني." }, { "3003", "بوسطة مش عارفة الحي ده. اختار المنطقة تاني." }, { "3007", "بوسطة بتحصّل لحد 30,000 جنيه بس." }, { "3009", "اختار منطقة الأوردر عند بوسطة." },
            { "1077", "عنوان الاستلام ده مالوش مسؤول. ضيفه من حسابك على بوسطة." }, { "1078", "فيه طلب استلام للعنوان ده في نفس اليوم." }, { "1080", "مفيش استلام يوم الجمعة." }, { "1081", "فات ميعاد استلام النهارده. اختار بكرة." }, { "1083", "اليوم ده عدّى." }, { "2022", "اليوم ده إجازة عند بوسطة." }, { "2024", "مندوب بوسطة جايلك النهارده خلاص." }, { "2025", "مندوب بوسطة استلم منك النهارده." }, { "2026", "مندوب بوسطة ماعرفش يستلم النهارده. اختار بكرة." }, { "2027", "طلبت استلام النهارده قبل كده." } };
        static object At(Carriers.Reply r, params string[] p) { return ShopJson.At(r.Body, p); }

        public override async Task Check(CarrierLink k)
        {
            var r = await Call(k, HttpMethod.Get, "/pickup-locations"); k.Places = new List<object>(); var l = ShopJson.List(At(r, "data", "list")); if (l.Count == 0) l = ShopJson.List(At(r, "data"));
            foreach (var x in l.OfType<Dictionary<string, object>>().Take(30))
            {
                string id = ShopJson.Str(ShopJson.At(x, "_id"), 60); if (id.Length == 0) continue; var ad = ShopJson.Sub(x, "address");
                k.Places.Add(new Dictionary<string, object> { { "id", id }, { "n", ShopJson.Str(ShopJson.At(x, "locationName"), 80) }, { "ad", string.Join("، ", new[] { ShopJson.Str(ShopJson.At(ad, "city", "name"), 60), ShopJson.Str(ShopJson.At(ad, "firstLine"), 120) }.Where(y => y.Length > 0)) }, { "def", ShopJson.Bool(ShopJson.At(x, "isDefault")) }, { "cn", ShopJson.Str(ShopJson.At(x, "contactPerson", "name"), 60) }, { "cp", ShopJson.Str(ShopJson.At(x, "contactPerson", "phone"), 20) } });
            }
            if (k.Places.Count > 0) k.AccName = ShopJson.Str(ShopJson.At((Dictionary<string, object>)k.Places.OfType<Dictionary<string, object>>().OrderByDescending(p => ShopJson.Bool(ShopJson.At(p, "def"))).First(), "n"), 80);
        }
        static Dictionary<string, object> PlaceOf(CarrierLink k, string id) { var l = k.Places.OfType<Dictionary<string, object>>().ToList(); return l.FirstOrDefault(p => ShopJson.Str(ShopJson.At(p, "id")) == id) ?? l.FirstOrDefault(p => ShopJson.Bool(ShopJson.At(p, "def"))) ?? l.FirstOrDefault(); }

        public override async Task<CarrierMade> Create(CarrierLink k, CarrierParcel p)
        {
            var name = p.Name.Trim().Split(new[] { ' ' }, 2); var size = k.S("size", "SMALL"); bool bulky = size.EndsWith("Bulky", StringComparison.Ordinal), open = k.B("open", false); var place = PlaceOf(k, k.S("place"));
            var to = new Dictionary<string, object> { { "city", p.Area.GovEn ?? p.Area.Gov }, { "districtId", p.Area.AreaId }, { "firstLine", p.Address.Length < 6 ? p.Area.Label + "، " + p.Address : p.Address } };
            if (p.Area.CityId.Length > 0) to["zoneId"] = p.Area.CityId; if (!string.IsNullOrWhiteSpace(p.Landmark)) to["secondLine"] = p.Landmark;
            var who = new Dictionary<string, object> { { "firstName", name[0] }, { "lastName", name.Length > 1 ? name[1] : name[0] }, { "fullName", p.Name }, { "phone", p.Phone } }; if (p.Phone2 != null) who["secondPhone"] = p.Phone2;
            var b = new Dictionary<string, object> { { "type", 10 }, { "specs", new Dictionary<string, object> { { "packageType", bulky ? size : "Parcel" }, { "size", size }, { "packageDetails", new Dictionary<string, object> { { "itemsCount", p.Items }, { "description", p.Goods } } } } }, { "cod", p.Cod }, { "dropOffAddress", to }, { "receiver", who }, { "businessReference", p.Ref }, { "uniqueBusinessReference", p.Ref }, { "allowToOpenPackage", open } };
            if (k.B("value", true) && p.Value > 0) b["goodsInfo"] = new Dictionary<string, object> { { "amount", p.Value } }; if (p.Note.Length > 0) b["notes"] = p.Note; if (place != null) b["businessLocationId"] = ShopJson.Str(ShopJson.At(place, "id"));
            if (open) b["deliveryImages"] = new[] { new Dictionary<string, object> { { "type", "ALLOW_TO_OPEN_THE_PACKAGE" }, { "images", p.Pics } } };
            if (p.HookUrl != null) { b["webhookUrl"] = p.HookUrl; b["webhookCustomHeaders"] = new Dictionary<string, object> { { "Authorization", "Bearer " + p.HookSecret } }; }
            var r = await Call(k, HttpMethod.Post, "/deliveries?apiVersion=1", b);
            string t = ShopJson.Str(At(r, "data", "trackingNumber"), 60); return new CarrierMade { Tracking = t, Id = ShopJson.Str(At(r, "data", "_id"), 60) };
        }

        public override async Task<CarrierMade> Find(CarrierLink k, string reference)
        {
            Carriers.Reply r; try { r = await Call(k, HttpMethod.Post, "/deliveries/search", new Dictionary<string, object> { { "businessReference", reference } }); }
            catch (CarrierException ex) when (ex.Code == "forbidden" || ex.BadKey) { throw new CarrierException("المفتاح مش مسموح له يدوّر على الشحنات عند بوسطة.", "nofind"); }
            CarrierMade found = null; Action<object> walk = null;
            walk = v =>
            {
                var d = v as Dictionary<string, object>;
                if (d != null) { string t = ShopJson.Str(ShopJson.At(d, "trackingNumber"), 60); int st = ShopJson.Int(ShopJson.At(d, "state", "code")); if (t.Length > 0 && (ShopJson.Str(ShopJson.At(d, "businessReference")) == reference || ShopJson.Str(ShopJson.At(d, "uniqueBusinessReference")) == reference) && st != 48 && st != 49) found = new CarrierMade { Tracking = t, Id = ShopJson.Str(ShopJson.At(d, "_id"), 60) }; else foreach (var x in d.Values) walk(x); return; }
                foreach (var x in ShopJson.List(v)) walk(x);
            };
            walk(r.Root); return found;
        }

        public override async Task<byte[]> Labels(CarrierLink k, List<CarrierShip> l, string size)
        {
            var r = await Call(k, HttpMethod.Post, "/deliveries/mass-awb", new Dictionary<string, object> { { "trackingNumbers", string.Join(",", l.Select(x => x.Tracking)) }, { "requestedAwbType", size == "A6" ? "A6" : "A4" }, { "lang", "ar" } });
            var pdf = Carriers.PdfIn(r); if (pdf == null) throw new CarrierException("بوسطة مابعتتش البوليصة. اللي خلص أو اتلغى مابيتطبعش.", "label"); return pdf;
        }

        public override async Task<string> Pickup(CarrierLink k, DateTime day, int parcels, string place, string note, List<CarrierShip> waiting)
        {
            var p = PlaceOf(k, place); if (p == null) throw new CarrierException("ضيف عنوان استلام في حسابك على بوسطة، وبعدين اربط المفتاح تاني.", "place");
            var b = new Dictionary<string, object> { { "businessLocationId", ShopJson.Str(ShopJson.At(p, "id")) }, { "scheduledDate", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "numberOfParcels", parcels }, { "packageType", k.S("size").EndsWith("Bulky", StringComparison.Ordinal) ? k.S("size") : "Normal" }, { "repeatedData", new Dictionary<string, object> { { "repeatedType", "One Time" } } } };
            string cn = ShopJson.Str(ShopJson.At(p, "cn")), cp = ShopJson.Str(ShopJson.At(p, "cp")); if (cn.Length > 0 && cp.Length > 0) b["contactPerson"] = new Dictionary<string, object> { { "name", cn }, { "phone", cp } }; if (note.Length > 0) b["notes"] = note;
            var r = await Call(k, HttpMethod.Post, "/pickups", b); return ShopJson.Str(At(r, "data", "puid"), 30);
        }

        // Read/Write keys can't DELETE (the docs' «Types of API keys»): the merchant cancels it on Bosta's own page, or links a Full access key
        public override async Task Cancel(CarrierLink k, CarrierShip x)
        {
            try { await Call(k, HttpMethod.Delete, "/deliveries/business/" + Uri.EscapeDataString(x.Tracking) + "/terminate"); }
            catch (CarrierException ex) when (ex.Code == "forbidden" || ex.BadKey) { throw new CarrierException("مفتاحك Read/Write، وبوسطة بتسمح بالإلغاء لمفتاح Full access بس. الغيها من حسابك على بوسطة، وهنعرف لوحدنا.", "forbidden"); }
        }

        public override async Task<List<CarrierEvent>> Ask(CarrierLink k, List<CarrierShip> l)
        {
            var o = new List<CarrierEvent>();
            foreach (var x in l)
            {
                var r = await Call(k, HttpMethod.Get, "/deliveries/business/" + Uri.EscapeDataString(x.Tracking)); int st = ShopJson.Int(At(r, "data", "state", "code"), -1); if (st < 0) continue;
                string type = ShopJson.Str(At(r, "data", "type", "code"), 10); var e = Map(st, type == "10" ? "SEND" : type.Length > 0 && type != "10" ? ShopJson.Str(At(r, "data", "type", "value"), 40) : "", 0, null); e.Tracking = x.Tracking; e.Attempts = ShopJson.Int(At(r, "data", "deliveryAttemptsLength")); o.Add(e);
            }
            return o;
        }

        public override async Task<List<CarrierAreaRow>> Areas(CarrierLink k)
        {
            var r = await Call(null, HttpMethod.Get, "/cities/getAllDistricts?countryId=" + Egypt); var l = new List<CarrierAreaRow>();
            foreach (var g in ShopJson.List(At(r, "data")).OfType<Dictionary<string, object>>())
                foreach (var a in ShopJson.List(ShopJson.At(g, "districts")).OfType<Dictionary<string, object>>())
                {
                    Func<Dictionary<string, object>, string, string> t = (d, n) => ShopJson.Str(ShopJson.At(d, n), 150); string gn = t(g, "cityOtherName"), zn = t(a, "zoneOtherName"), an = t(a, "districtOtherName");
                    l.Add(new CarrierAreaRow { GovId = t(g, "cityId"), Gov = gn.Length > 0 ? gn : t(g, "cityName"), GovEn = t(g, "cityName"), CityId = t(a, "zoneId"), City = zn.Length > 0 ? zn : t(a, "zoneName"), CityEn = t(a, "zoneName"), AreaId = t(a, "districtId"), Area = an.Length > 0 ? an : t(a, "districtName"), AreaEn = t(a, "districtName"), Closed = !ShopJson.Bool(ShopJson.At(a, "dropOffAvailability"), true) });
                }
            return l;
        }

        // ---------- what a state means (the docs' table; 41 is «to the customer» on a delivery and «back to you» on a return) ----------
        public static CarrierEvent Map(int state, string type, int exception, string said)
        {
            type = (type ?? "").ToUpperInvariant(); bool back = type.Contains("RTO") || type.Contains("RETURN") || type.Contains("EXCHANGE") || type == "CRP";
            var e = new CarrierEvent { Code = state + (exception > 0 ? ":" + exception : "") };
            switch (state)
            {
                case 21: e.Ours = back ? "" : "picked"; break;
                case 24: case 30: e.Ours = back ? "" : "transit"; break;   // also when the «picked up» notification never came
                case 41: e.Ours = back ? "returning" : "out"; e.Blind = type.Length == 0; break;
                case 45: e.Ours = back ? "" : "delivered"; break;
                case 46: case 60: e.Ours = "returned"; break;
                case 47: if (back || (exception >= 20 && exception <= 30)) { e.Ours = "hold"; e.Loud = true; } else e.Ours = "attempt"; break;
                case 48: case 49: e.Ours = "cancelled"; break;
                case 100: case 101: e.Ours = "lost"; e.Text = state == 100 ? "بوسطة سجّلتها مفقودة" : "بوسطة سجّلتها متكسرة"; break;
                case 102: case 105: e.Ours = "hold"; break;
                case 103: e.Ours = "hold"; e.Loud = true; e.Text = "بوسطة ماعرفتش ترجّعهالك 3 مرات ومستنية قرارك"; break;
            }
            KeyValuePair<string, string> w; if (exception > 0 && Why.TryGetValue(exception, out w)) { e.Reason = w.Key; e.Text = w.Value; } else if (exception > 0 && !string.IsNullOrWhiteSpace(said)) e.Text = said;
            return e;
        }
        static readonly Dictionary<int, KeyValuePair<string, string>> Why = new Dictionary<int, KeyValuePair<string, string>> {
            { 1, W("away", "الزبون مش في العنوان") }, { 2, W("address", "الزبون غيّر العنوان") }, { 3, W("later", "الزبون أجّل ليوم تاني") }, { 4, W("open", "الزبون عايز يفتح الشحنة") }, { 5, W("address", "العنوان أو الموبايل مش واضح") }, { 6, W("sender", "اتلغى بطلب منك") }, { 7, W("noanswer", "الزبون مش بيرد") }, { 8, W("refused", "الزبون رفض الاستلام") }, { 12, W("zone", "العنوان بره تغطية بوسطة") }, { 13, W("address", "العنوان مش واضح") }, { 14, W("phone", "رقم الموبايل غلط") }, { 100, W("weather", "الجو وحش") }, { 101, W("doubt", "بوسطة شاكة في المستلم") },
            { 20, W("me", "عنوانك للمرتجع اتغيّر") }, { 21, W("me", "أجّلت استلام المرتجع") }, { 22, W("me", "عنوانك أو رقمك للمرتجع مش واضح") }, { 23, W("me", "مابتردّش على مندوب المرتجع") }, { 24, W("me", "رفضت استلام المرتجع") }, { 25, W("me", "مندوب المرتجع مالقاكش في العنوان") }, { 26, W("me", "المرتجع متكسر") }, { 27, W("me", "المرتجع فاضي") }, { 28, W("me", "المرتجع ناقص") }, { 29, W("me", "المرتجع مش بتاعك") }, { 30, W("me", "المرتجع اتفتح") } };
        static KeyValuePair<string, string> W(string k, string t) { return new KeyValuePair<string, string>(k, t); }

        public static CarrierEvent FromHook(Dictionary<string, object> b)
        {
            int st = ShopJson.Int(ShopJson.At(b, "state"), -1); if (st < 0) return null; var e = Map(st, ShopJson.Str(ShopJson.At(b, "type"), 40), ShopJson.Int(ShopJson.At(b, "exceptionCode")), ShopJson.Str(ShopJson.At(b, "exceptionReason"), 150));
            e.Tracking = ShopJson.Str(ShopJson.At(b, "trackingNumber"), 60); e.Ref = ShopJson.Str(ShopJson.At(b, "businessReference"), 60); e.TheirId = ShopJson.Str(ShopJson.At(b, "_id"), 60); e.Attempts = ShopJson.Int(ShopJson.At(b, "numberOfAttempts"));
            decimal ms; if (decimal.TryParse(ShopJson.Str(ShopJson.At(b, "timeStamp"), 20), NumberStyles.Number, CultureInfo.InvariantCulture, out ms) && ms > 1600000000000m && ms < 4000000000000m) e.At = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((double)ms);
            return e;
        }
    }
}
