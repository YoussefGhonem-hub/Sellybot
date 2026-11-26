// Aramex: the JSON form of their WCF services, <AramexBase>/<Shipping|Location|Tracking>/Service_1_0.svc/json/<Method> (their WSDL schemas and
// manual, read 2026-11-26 through open-source clients that mirror them: aramex.com refuses automated readers). Every request carries the merchant's
// own API user in ClientInfo: UserName, Password, AccountNumber, AccountPin, AccountEntity, AccountCountryCode (all six encrypted: the password in
// KeyBox, the rest in MoreBox). Errors come with HTTP 200: HasErrors and Notifications [{Code, Message}] (ERR01 user / password, ERR02 the
// account, ERR03 blocked, ERR30 the same ForeignHAWB again).
// What their API doesn't have, said to the owner as it is: no notifications (the open shipments are asked about every hour), no cancelling a
// shipment (only a pickup), no finding a shipment by our reference, no return shipment («المندوب يجيب المرتجع» isn't offered). One shipment per
// order all the same: our reference goes as ForeignHAWB, which Aramex keeps unique, so a press that got no answer is simply made again and
// Aramex refuses a second one; then the owner types the number from their own account (Carriers.Fix, checked with TrackShipments).
// Cities: Aramex addresses by city name (FetchCities); its English names are matched to our governorates and cities by the list below, and
// the owner picks by hand what isn't in it (the pick is kept, CarrierAreas).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public class CarrierAramex : Carrier
    {
        static string Base { get { return Web.Setting("AramexBase", "https://ws.aramex.net/ShippingAPI.V2").TrimEnd('/'); } }
        public override string Id { get { return "aramex"; } }
        public override string Name { get { return "أرامكس"; } }
        public override int PollMinutes { get { return 60; } }
        public override int PollBatch { get { return 30; } }
        public override int MaxLabels { get { return 1; } }
        public override bool GuardsDup { get { return true; } }
        static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }

        // ---------- linking: the six values of the API user the merchant's Aramex rep gives ----------
        public override void Take(CarrierLink k, Dictionary<string, object> b, CarrierLink old)
        {
            Func<string, int, string> t = (n, m) => ShopJson.Str(ShopJson.At(b, n), m).Trim();
            k.More["u"] = t("u", 80); k.More["n"] = Regex.Replace(t("acc", 30), @"\s", ""); k.More["pin"] = Regex.Replace(t("pin", 20), @"\s", ""); k.More["ent"] = t("ent", 10).ToUpperInvariant();
            var cc = t("cc", 4).ToUpperInvariant(); k.More["cc"] = cc.Length == 0 ? "EG" : cc; k.Key = (k.Key ?? "").Trim(); k.Code = null; k.Fresh = old == null || old.M("n") != k.M("n");
        }
        public override string BadInput(CarrierLink k)
        {
            if (!Regex.IsMatch(k.M("u"), @"^[\x21-\x7e]{3,80}$")) return "اكتب اسم المستخدم (UserName) بتاع يوزر الربط اللي مندوب أرامكس ادّهولك.";
            if (k.Key.Length < 3 || k.Key.Length > 60) return "اكتب الباسورد (Password) بتاع يوزر الربط.";
            if (!Regex.IsMatch(k.M("n"), @"^\d{3,20}$")) return "اكتب رقم الحساب (AccountNumber) أرقام بس.";
            if (!Regex.IsMatch(k.M("pin"), @"^[A-Za-z0-9]{3,12}$")) return "اكتب الـ PIN (AccountPin) زي ما مندوب أرامكس ادّهولك.";
            if (!Regex.IsMatch(k.M("ent"), @"^[A-Z]{3}$")) return "اكتب كود الفرع (AccountEntity): 3 حروف إنجليزي زي CAI.";
            return Regex.IsMatch(k.M("cc"), @"^[A-Z]{2}$") ? null : "اكتب كود الدولة (AccountCountryCode): حرفين زي EG.";
        }
        public override string Broken(CarrierLink k) { return k.M("u").Length == 0 || k.M("n").Length == 0 ? "بيانات أرامكس ناقصة. اربطها تاني." : null; }
        public override string Tail(CarrierLink k) { var n = k.M("n"); return n.Length > 4 ? n.Substring(n.Length - 4) : ""; }
        public override string NotReady(CarrierLink k)
        {
            if (k.S("sn").Length < 2 || k.S("sp").Length == 0 || k.S("sa").Length == 0 || k.S("ss").Length < 3) return "اكتب عنوان الاستلام (الاسم والموبايل والمدينة والشارع) في إعدادات أرامكس الأول.";
            return Web.ValidEmail(k.S("email")) ? null : "أرامكس بتطلب إيميل على كل شحنة. اكتبه في إعدادات أرامكس.";
        }

        // ---------- their servers ----------
        Dictionary<string, object> Client(CarrierLink k) { return D("UserName", k.M("u"), "Password", k.Key, "Version", "v1.0", "AccountNumber", k.M("n"), "AccountPin", k.M("pin"), "AccountEntity", k.M("ent"), "AccountCountryCode", k.M("cc"), "Source", 24); }
        async Task<Dictionary<string, object>> Call(CarrierLink k, string svc, string method, Dictionary<string, object> body)
        {
            if (Broken(k) != null) throw new CarrierException(Broken(k), "key", badKey: true);
            body["ClientInfo"] = Client(k); body["Transaction"] = D("Reference1", "", "Reference2", "", "Reference3", "", "Reference4", "", "Reference5", "");
            var q = new HttpRequestMessage(HttpMethod.Post, Base + "/" + svc + "/Service_1_0.svc/json/" + method) { Content = new StringContent(ShopJson.Write(body), Encoding.UTF8, "application/json") };
            q.Headers.TryAddWithoutValidation("Accept", "application/json");
            var r = await Carriers.Send(Name, q);
            if (r.Status >= 500 || r.Status == 408) throw new CarrierException("أرامكس فيها مشكلة دلوقتي. جرّب كمان شوية.", "net", unsure: true);
            if (r.Status == 429) throw new CarrierException("أرامكس بتقول طلبات كتير. استنى دقيقة.", "busy");
            if (r.Status == 401 || r.Status == 403) throw new CarrierException("أرامكس مش قابلة بيانات الربط. اتأكد منها من مندوب أرامكس واربط تاني.", "key", badKey: true);
            if (!r.Good || r.Root == null) throw new CarrierException("أرامكس رفضت الطلب (رد " + r.Status + ").", "refused", unsure: r.Good);
            var d = r.Body; if (ShopJson.Bool(ShopJson.At(d, "HasErrors")) || Notes(d, true).Count > 0) throw Why(Notes(d, false));
            return d;
        }
        // every notification in the answer: the envelope's and each shipment's / pickup's (err: only those of a part that failed)
        static List<KeyValuePair<string, string>> Notes(Dictionary<string, object> d, bool err)
        {
            var l = new List<KeyValuePair<string, string>>(); Action<object> add = v => { foreach (var n in ShopJson.List(v).OfType<Dictionary<string, object>>()) l.Add(new KeyValuePair<string, string>(ShopJson.Str(ShopJson.At(n, "Code"), 20).ToUpperInvariant(), ShopJson.Str(ShopJson.At(n, "Message"), 300))); };
            if (!err) add(ShopJson.At(d, "Notifications"));
            foreach (var part in new[] { "Shipments", "ProcessedShipmentHolds" }) foreach (var x in ShopJson.List(ShopJson.At(d, part)).OfType<Dictionary<string, object>>()) if (ShopJson.Bool(ShopJson.At(x, "HasErrors"))) add(ShopJson.At(x, "Notifications"));
            var pp = ShopJson.Sub(d, "ProcessedPickup"); foreach (var x in ShopJson.List(ShopJson.At(pp, "ProcessedShipments")).OfType<Dictionary<string, object>>()) if (ShopJson.Bool(ShopJson.At(x, "HasErrors"))) add(ShopJson.At(x, "Notifications"));
            return l;
        }
        static CarrierException Why(List<KeyValuePair<string, string>> l)
        {
            Func<string, bool> has = code => l.Any(x => x.Key == code); string all = string.Join(" ", l.Select(x => x.Value));
            if (has("ERR01")) return new CarrierException("اسم المستخدم أو الباسورد بتوع أرامكس مش صح. اتأكد منهم من مندوب أرامكس.", "ERR01", badKey: true);
            if (has("ERR02")) return new CarrierException("بيانات الحساب عند أرامكس (رقم الحساب أو الـ PIN أو الفرع أو الدولة) مش صح. اتأكد منها من مندوب أرامكس.", "ERR02", badKey: true);
            if (has("ERR03")) return new CarrierException("حساب أرامكس ده موقوف. كلّم مندوب أرامكس.", "ERR03", badKey: true);
            if (has("ERR30")) return new CarrierException("أرامكس عندها الشحنة دي من ضغطة قبل كده مالقيتش رد، ومابتقولش رقمها. دوّر عليها في حسابك على أرامكس برقم المرجع، واكتب رقمها في الأوردر.", "dup");
            if (has("ERR17") || has("ERR18") || Regex.IsMatch(all, @"pickup\s*date|date.*(past|7 days)", RegexOptions.IgnoreCase)) return new CarrierException("أرامكس بتقبل ميعاد استلام من النهارده لحد 7 أيام بس.", "date");
            if (Regex.IsMatch(all, @"\bcity\b", RegexOptions.IgnoreCase)) return new CarrierException("أرامكس مش عارفة المدينة دي. اختارها تاني من القايمة.", "area");
            if (Regex.IsMatch(all, @"e-?mail", RegexOptions.IgnoreCase)) return new CarrierException("أرامكس بتطلب إيميل. اكتبه في إعدادات أرامكس.", "email");
            var first = l.FirstOrDefault(x => x.Value.Length > 0); return new CarrierException("أرامكس رفضت: " + (first.Value == null ? "من غير سبب" : Carriers.Cut(first.Value, 180)), string.IsNullOrEmpty(first.Key) ? "refused" : first.Key);
        }
        // their dates: /Date(ms)/ (JavaScriptSerializer writes a DateTime that way, and reads it back as one), else any written date (ISO)
        static DateTime? When(object v)
        {
            if (v is DateTime) return DateTime.SpecifyKind(((DateTime)v).ToUniversalTime(), DateTimeKind.Utc);
            var t = ShopJson.Str(v, 60); var m = Regex.Match(t, @"Date\((-?\d+)"); long ms;
            if (m.Success && long.TryParse(m.Groups[1].Value, out ms)) return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
            DateTime d; return t.Length > 0 && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null;
        }

        public override async Task Check(CarrierLink k)
        {
            await Call(k, "Location", "FetchCities", D("CountryCode", "EG", "State", null, "NameStartsWith", null));
            // the account itself (number, PIN, branch): a waybill that doesn't exist, asked with them
            await Call(k, "Tracking", "TrackShipments", D("Shipments", new[] { "0" }, "GetLastTrackingUpdateOnly", true));
            k.AccName = null;
        }

        // ---------- sending ----------
        static string[] Lines(string s) { var w = Regex.Replace(s ?? "", @"\s+", " ").Trim(); var l = new List<string>(); while (w.Length > 0 && l.Count < 3) { int n = w.Length <= 50 ? w.Length : Math.Max(w.LastIndexOf(' ', 50), 20); l.Add(w.Substring(0, n).Trim()); w = w.Substring(n).Trim(); } while (l.Count < 3) l.Add(""); return l.ToArray(); }
        static Dictionary<string, object> Party(string acct, string person, string company, string phone, string phone2, string email, string city, string street, string cc)
        {
            var a = Lines(street);
            return D("Reference1", "", "Reference2", "", "AccountNumber", acct, "PartyAddress", D("Line1", a[0], "Line2", a[1], "Line3", a[2], "City", city, "StateOrProvinceCode", "", "PostCode", "", "CountryCode", cc),
                "Contact", D("Department", "", "PersonName", Carriers.Cut(person, 50), "Title", "", "CompanyName", Carriers.Cut(company, 50), "PhoneNumber1", phone, "PhoneNumber1Ext", "", "PhoneNumber2", phone2 ?? "", "PhoneNumber2Ext", "", "FaxNumber", "", "CellPhone", phone, "EmailAddress", email, "Type", ""));
        }
        CarrierAreaRow From(CarrierLink k) { using (var c = Db.Open()) { var a = CarrierAreas.ByKey(c, Id, k.S("sa")); if (a == null) throw new CarrierException("مدينة الاستلام اتغيّرت عند أرامكس. اختارها تاني من الإعدادات.", "sender"); return a; } }
        static string Store(int storeId) { using (var c = Db.Open()) { var s = Db.StoreById(c, storeId); var sh = Shops.ByStore(c, storeId); return sh != null ? sh.Name : s == null ? "" : s.Name; } }
        decimal Kg(CarrierLink k, decimal kg) { return kg > 0 ? Math.Max(0.1m, kg) : Math.Max(0.1m, ShopJson.Dec(ShopJson.At(k.Set, "weight"), 1)); }

        public override async Task<CarrierMade> Create(CarrierLink k, CarrierParcel p)
        {
            var from = From(k); string store = Store(k.StoreId); var now = DateTime.UtcNow;
            var details = D("Dimensions", null, "ActualWeight", D("Unit", "KG", "Value", Kg(k, p.Kg)), "ChargeableWeight", null, "DescriptionOfGoods", Carriers.Cut(p.Goods, 100), "GoodsOriginCountry", "EG", "NumberOfPieces", 1, "ProductGroup", "DOM", "ProductType", k.S("pt", "ONP"), "PaymentType", "P", "PaymentOptions", "",
                "CustomsValueAmount", null, "CashOnDeliveryAmount", p.Cod > 0 ? D("CurrencyCode", "EGP", "Value", p.Cod) : null, "InsuranceAmount", null, "CashAdditionalAmount", null, "CashAdditionalAmountDescription", "", "CollectAmount", null, "Services", p.Cod > 0 ? "CODS" : "", "Items", new object[0]);
            var x = D("Reference1", p.Ref, "Reference2", "#" + p.OrderNo, "Reference3", "", "ForeignHAWB", p.Ref,
                "Shipper", Party(k.M("n"), k.S("sn"), store, ShopOrders.Local(k.S("sp")), null, k.S("email"), from.AreaId, k.S("ss"), "EG"),
                "Consignee", Party("", p.Name, p.Name, p.Phone, p.Phone2, p.Email, p.Area.AreaId, p.Address + (string.IsNullOrWhiteSpace(p.Landmark) ? "" : "، علامة مميزة: " + p.Landmark), "EG"),
                "ThirdParty", null, "ShippingDateTime", now, "DueDate", now.AddDays(1), "Comments", Carriers.Cut(p.Note, 150), "PickupLocation", "", "OperationsInstructions", "", "AccountingInstrcutions", "", "Details", details, "Attachments", new object[0], "PickupGUID", "", "Number", null);
            var r = await Call(k, "Shipping", "CreateShipments", D("Shipments", new[] { x }, "LabelInfo", D("ReportID", 9729, "ReportType", "URL")));
            var s = ShopJson.List(ShopJson.At(r, "Shipments")).OfType<Dictionary<string, object>>().FirstOrDefault(); string id = s == null ? "" : ShopJson.Str(ShopJson.At(s, "ID"), 60);
            return new CarrierMade { Tracking = id, Id = id };
        }
        public override Task<CarrierMade> Find(CarrierLink k, string reference) { throw new CarrierException("أرامكس مابتسمحش ندوّر على الشحنة برقم الأوردر. دوّر عليها في حسابك على أرامكس بالمرجع " + reference + "، ولو لقيتها اكتب رقمها في الأوردر.", "nofind"); }
        // the owner's number (Carriers.Fix): Aramex knows a shipment by it
        public async Task<bool> Exists(CarrierLink k, string waybill)
        {
            var r = await Call(k, "Tracking", "TrackShipments", D("Shipments", new[] { waybill }, "GetLastTrackingUpdateOnly", true));
            return !ShopJson.List(ShopJson.At(r, "NonExistingWaybills")).Select(x => ShopJson.Str(x, 40)).Contains(waybill);
        }

        // ---------- the label: PrintLabel, the file itself in the answer (RPT) ----------
        public override async Task<byte[]> Labels(CarrierLink k, List<CarrierShip> l, string size)
        {
            var r = await Call(k, "Shipping", "PrintLabel", D("ShipmentNumber", l[0].Tracking, "ProductGroup", "DOM", "OriginEntity", k.M("ent"), "LabelInfo", D("ReportID", 9729, "ReportType", "RPT")));
            var lb = ShopJson.Sub(r, "ShipmentLabel"); var f = ShopJson.At(lb, "LabelFileContents"); byte[] pdf = null;
            var nums = ShopJson.List(f); if (nums.Count > 60) try { pdf = nums.Select(x => (byte)ShopJson.Int(x)).ToArray(); } catch { pdf = null; }   // WCF writes a byte[] as numbers
            if (pdf == null || !(pdf.Length > 4 && pdf[0] == '%' && pdf[1] == 'P')) pdf = Carriers.PdfIn(new Carriers.Reply { Root = r, Bytes = new byte[0] });
            if (pdf == null)
            {
                // only a link: fetched the careful way, and only from Aramex itself
                Uri u; var url = ShopJson.Str(ShopJson.At(lb, "LabelURL"), 600);
                if (Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == "https" && Regex.IsMatch(u.Host, @"(^|\.)aramex\.(net|com)$", RegexOptions.IgnoreCase)) { var g = await ShopFetch.Get(url, 8000000); if (g.Bytes != null && g.Bytes.Length > 60 && g.Bytes[0] == '%' && g.Bytes[1] == 'P') pdf = g.Bytes; }
            }
            if (pdf == null) throw new CarrierException("أرامكس مابعتتش بوليصة " + l[0].Tracking + ".", "label"); return pdf;
        }

        // ---------- the courier: CreatePickup at the sender's address, the waiting shipments with it ----------
        public override async Task<string> Pickup(CarrierLink k, DateTime day, int parcels, string place, string note, List<CarrierShip> waiting)
        {
            var from = From(k); int h1 = ShopJson.Int(ShopJson.At(k.Set, "from"), 10), h2 = ShopJson.Int(ShopJson.At(k.Set, "to"), 18);
            Func<int, DateTime> at = h => Carriers.FromCairo(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + h.ToString("00", CultureInfo.InvariantCulture) + ":00") ?? DateTime.UtcNow;
            var who = Party(k.M("n"), k.S("sn"), Store(k.StoreId), ShopOrders.Local(k.S("sp")), null, k.S("email"), from.AreaId, k.S("ss"), "EG"); decimal w = Kg(k, 0);
            var item = D("ProductGroup", "DOM", "ProductType", k.S("pt", "ONP"), "NumberOfShipments", parcels, "PackageType", "Box", "Payment", "P", "ShipmentWeight", D("Unit", "KG", "Value", w * parcels), "ShipmentVolume", D("Unit", "Cm3", "Value", 1),
                "NumberOfPieces", parcels, "CashAmount", null, "ExtraCharges", null, "ShipmentDimensions", null, "Comments", "");   // zeros are refused («less or equal zero»); their WooCommerce plugin sends none
            var pk = D("PickupAddress", ShopJson.Sub(who, "PartyAddress"), "PickupContact", ShopJson.Sub(who, "Contact"), "PickupLocation", "Reception", "PickupDate", at(h1), "ReadyTime", at(h1), "LastPickupTime", at(h2), "ClosingTime", at(h2), "Comments", Carriers.Cut(note, 150),
                "Reference1", "", "Reference2", "", "Vehicle", "Car", "Shipments", null, "PickupItems", new[] { item }, "Status", "Ready",
                "ExistingShipments", waiting.Count == 0 ? null : waiting.Where(x => !string.IsNullOrEmpty(x.Tracking)).Take(100).Select(x => D("Number", x.Tracking, "OriginEntity", k.M("ent"), "ProductGroup", "DOM")).ToArray(), "Branch", "", "RouteCode", "");
            var r = await Call(k, "Shipping", "CreatePickup", D("Pickup", pk, "LabelInfo", null));
            return ShopJson.Str(ShopJson.At(r, "ProcessedPickup", "ID"), 30);
        }
        public override bool CanCancel { get { return false; } }
        public override Task Cancel(CarrierLink k, CarrierShip x) { throw new CarrierException("أرامكس مابتسمحش تلغي الشحنة من بره حسابها. الغيها من حسابك على أرامكس أو من مندوبها، وهنعرف لوحدنا خلال ساعة (أو دوس «حدّث الحالة»).", "nocancel"); }

        // ---------- what happened: TrackShipments, every update of each waybill ----------
        public override async Task<List<CarrierEvent>> Ask(CarrierLink k, List<CarrierShip> l)
        {
            var r = await Call(k, "Tracking", "TrackShipments", D("Shipments", l.Select(x => x.Tracking).ToArray(), "GetLastTrackingUpdateOnly", false)); var o = new List<CarrierEvent>();
            // WCF writes the dictionary as [{Key, Value}]; some gateways as an object
            var res = ShopJson.At(r, "TrackingResults"); var groups = new List<KeyValuePair<string, List<object>>>();
            foreach (var kv in ShopJson.List(res).OfType<Dictionary<string, object>>()) groups.Add(new KeyValuePair<string, List<object>>(ShopJson.Str(ShopJson.At(kv, "Key"), 60), ShopJson.List(ShopJson.At(kv, "Value"))));
            var map = res as Dictionary<string, object>; if (map != null) foreach (var kv in map) groups.Add(new KeyValuePair<string, List<object>>(kv.Key, ShopJson.List(kv.Value)));
            foreach (var g in groups)
                foreach (var t in g.Value.OfType<Dictionary<string, object>>())
                {
                    var e = Map(ShopJson.Str(ShopJson.At(t, "UpdateCode"), 10), ShopJson.Str(ShopJson.At(t, "UpdateDescription"), 200), ShopJson.Str(ShopJson.At(t, "Comments"), 200) + " " + ShopJson.Str(ShopJson.At(t, "ProblemCode"), 40));
                    var wb = ShopJson.Str(ShopJson.At(t, "WaybillNumber"), 60); e.Tracking = wb.Length > 0 ? wb : g.Key; e.At = When(ShopJson.At(t, "UpdateDateTime")); o.Add(e);
                }
            return o.OrderBy(e => e.At ?? DateTime.MaxValue).ToList();
        }
        // their update codes (the table their tracking manual and open-source clients carry), then the words when a code isn't known
        static readonly Dictionary<string, string> Codes = Pairs("made:SH014,SH203,SH278,SH308,SH312;picked:SH012,SH314;transit:SH001,SH160,SH047,SH406,SH434,SH022,SH073,SH252,SH077,SH475,SH110,SH272,SH259,SH260,SH495,SH222,SH007,SH070,SH295,SH296;out:SH003,SH004;" +
            "delivered:SH005,SH006,SH234,SH496,SH534;attempt:SH033,SH157,SH294,SH515,SH375,SH480,SH044,SH076,SH376;returning:SH474,SH498;returned:SH069,SH407,SH559;lost:SH280;hold:SH008,SH275,SH381,SH071,SH237,SH538,SH539,SH164,SH499,SH533,SH560,SH154,SH043,SH156");
        static readonly HashSet<string> Loud = new HashSet<string> { "SH071", "SH237", "SH538", "SH539", "SH560", "SH154", "SH043", "SH156" };
        static Dictionary<string, string> Pairs(string s) { var d = new Dictionary<string, string>(); foreach (var g in s.Split(';')) { var p = g.Split(':'); if (p.Length == 2) foreach (var c in p[1].Split(',')) d[c] = p[0]; } return d; }
        public static CarrierEvent Map(string code, string said, string more)
        {
            code = (code ?? "").Trim().ToUpperInvariant(); string ours, t = (said ?? "").ToLowerInvariant();
            if (!Codes.TryGetValue(code, out ours))
                ours = Regex.IsMatch(t, "returned to shipper") ? "returned" : Regex.IsMatch(t, "to be returned|return to shipper") ? "returning" : Regex.IsMatch(t, "out for delivery") ? "out" : Regex.IsMatch(t, @"attempt|unable to deliver|not reach|rescheduled|not delivered|undelivered|could not be delivered") ? "attempt"
                    : Regex.IsMatch(t, @"\bdelivered\b|collected by consignee") ? "delivered" : Regex.IsMatch(t, "picked up from shipper|pickup completed") ? "picked" : Regex.IsMatch(t, "cancel") ? "cancelled" : Regex.IsMatch(t, "on hold|held") ? "hold"
                    : Regex.IsMatch(t, "transit|departed|received at|forwarded|facility") ? "transit" : Regex.IsMatch(t, "record created|data received") ? "made" : "";
            var e = new CarrierEvent { Code = code.Length > 0 ? code : Carriers.Cut(said ?? "?", 30), Ours = ours, Loud = Loud.Contains(code) };
            if (ours == "attempt")
            {
                var w = (t + " " + (more ?? "")).ToLowerInvariant();
                e.Reason = Regex.IsMatch(w, "reject|refus") ? "refused" : Regex.IsMatch(w, @"cannot be reached|unreachable|no answer|not answer|switched off") ? "noanswer" : Regex.IsMatch(w, "resched|postpone|delay") ? "later" : Regex.IsMatch(w, "address") ? "address" : "";
                e.Text = e.Reason == "refused" ? "الزبون رفض الاستلام" : e.Reason == "noanswer" ? "الزبون مش بيرد" : e.Reason == "later" ? "التسليم اتأجّل" : e.Reason == "address" ? "العنوان محتاج يتظبط" : string.IsNullOrWhiteSpace(said) ? null : said;
            }
            else if (ours == "hold" || ours == "lost") e.Text = string.IsNullOrWhiteSpace(said) ? null : said;
            return e;
        }

        // ---------- cities: FetchCities for Egypt, matched to our governorates by the list ----------
        public override async Task<List<CarrierAreaRow>> Areas(CarrierLink k)
        {
            var r = await Call(k, "Location", "FetchCities", D("CountryCode", "EG", "State", null, "NameStartsWith", null)); var l = new List<CarrierAreaRow>();
            foreach (var name in ShopJson.List(ShopJson.At(r, "Cities")).Select(x => ShopJson.Str(x, 120)).Where(x => x.Length > 0).Distinct())
            {
                Known g; var key = Fold(name);
                if (Egypt.TryGetValue(key, out g)) l.Add(new CarrierAreaRow { GovId = g.Gov.ToString(CultureInfo.InvariantCulture), Gov = ShopGeo.Name(g.Gov), GovEn = ShopGeo.Name(g.Gov, true), CityId = name, City = g.Ar, CityEn = name, AreaId = name, Area = g.Ar, AreaEn = name });
                else l.Add(new CarrierAreaRow { GovId = "0", Gov = "مدن تانية", GovEn = "Other", CityId = name, City = name, CityEn = name, AreaId = name, Area = name, AreaEn = name });
            }
            return l;
        }
        class Known { public int Gov; public string Ar; }
        // an English place name, the way the list keeps it: no «El», «City», «of»; «6th» = «6»
        static readonly Dictionary<string, string> Nth = new Dictionary<string, string> { { "first", "1" }, { "1st", "1" }, { "fifth", "5" }, { "5th", "5" }, { "sixth", "6" }, { "6th", "6" }, { "tenth", "10" }, { "10th", "10" }, { "15th", "15" } };
        public static string Fold(string s)
        {
            var w = Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x != "el" && x != "al" && x != "city" && x != "of" && x != "the").Select(x => { string n; return Nth.TryGetValue(x, out n) ? n : x; });
            return string.Concat(w);
        }
        // our governorate, our city's name, and the English names it goes by
        const string List_ = "1|القاهرة|Cairo;1|مدينة نصر|Nasr City;1|مصر الجديدة|Heliopolis/Masr El Gedida;1|المعادي|Maadi;1|حلوان|Helwan;1|شبرا|Shubra/Shoubra;1|التجمع الخامس|Fifth Settlement/El Tagamoa El Khames;1|التجمع الأول|First Settlement;1|القاهرة الجديدة|New Cairo;1|الرحاب|Rehab;1|مدينتي|Madinaty;1|الشروق|Shorouk;1|بدر|Badr;1|العبور|Obour;1|المقطم|Mokattam/Moqattam;1|وسط البلد|Downtown/Down Town/Wust El Balad;1|الزمالك|Zamalek;1|جاردن سيتي|Garden City/Garden;1|السيدة زينب|Sayeda Zeinab/Sayeda Zainab;1|مصر القديمة|Old Cairo/Masr El Kadima;1|دار السلام|Dar El Salam;1|البساتين|Basateen;1|الزيتون|Zeitoun/Zaitoun;1|حدائق القبة|Hadayek El Kobba/Hadaek El Kobba;1|المطرية|Matareya/Matariya;1|عين شمس|Ain Shams;1|المرج|Marg;1|السلام|Salam;1|النزهة|Nozha;1|روض الفرج|Rod El Farag;1|العباسية|Abbassia/Abbasiya;1|عابدين|Abdeen/Abdin;1|منشأة ناصر|Manshiyat Naser;1|طره|Tora/Torah;1|المعصرة|Maasara;1|15 مايو|15 May;1|التبين|Tebbin;1|العاصمة الإدارية|New Administrative Capital/Administrative Capital;1|بولاق أبو العلا|Boulaq Abu El Ela/Bulaq;"
            + "2|الجيزة|Giza;2|الدقي|Dokki/Doqqi;2|المهندسين|Mohandessin/Mohandeseen/Mohandiseen;2|العجوزة|Agouza;2|إمبابة|Imbaba;2|الهرم|Haram/Pyramids;2|فيصل|Faisal;2|العمرانية|Omraneya/Omrania;2|بولاق الدكرور|Boulaq El Dakrour/Bulaq Al Dakrur;2|الوراق|Warraq;2|أوسيم|Ausim/Osim;2|كرداسة|Kerdasa;2|6 أكتوبر|6th of October/October;2|الشيخ زايد|Sheikh Zayed;2|حدائق الأهرام|Hadayek El Ahram/Pyramids Gardens;2|حدائق أكتوبر|Hadayek October/October Gardens;2|الحوامدية|Hawamdeya/Hawamdiyah;2|البدرشين|Badrashin;2|العياط|Ayat;2|الصف|Saf;2|المنيب|Moneeb/Munib;2|الكيت كات|Kit Kat;"
            + "3|الإسكندرية|Alexandria;3|سموحة|Smouha;3|سيدي جابر|Sidi Gaber;3|ستانلي|Stanley;3|رشدي|Roushdy/Rushdy;3|جليم|Gleem/Glim;3|لوران|Louran/Laurent;3|سان ستيفانو|San Stefano;3|ميامي|Miami;3|العصافرة|Asafra;3|المندرة|Mandara;3|سيدي بشر|Sidi Bishr;3|المنتزه|Montaza/Montazah;3|المعمورة|Maamoura;3|أبو قير|Abu Qir/Abu Kir;3|محطة الرمل|Raml Station/Mahatet El Raml;3|العطارين|Attarin;3|المنشية|Mansheya/Manshia;3|الجمرك|Gomrok;3|بحري|Bahary/Bahari;3|محرم بك|Moharam Bek/Moharram Bey;3|العجمي|Agamy/Agami;3|الدخيلة|Dekheila;3|العامرية|Amreya/Ameria;3|برج العرب|Borg El Arab;3|الإبراهيمية|Ibrahimia;3|سبورتنج|Sporting;3|كليوباترا|Cleopatra;3|الشاطبي|Shatby;"
            + "4|بنها|Banha/Benha;4|شبرا الخيمة|Shubra El Kheima/Shoubra El Kheima;4|قليوب|Qalyub/Kaliub;4|القناطر الخيرية|Qanater/El Qanater El Khayreya;4|الخانكة|Khanka;4|الخصوص|Khosous;4|طوخ|Toukh;4|قها|Qaha;5|المنصورة|Mansoura;5|طلخا|Talkha;5|ميت غمر|Mit Ghamr;5|دكرنس|Dekernes;5|أجا|Aga;5|السنبلاوين|Sinbillawin/Senbellawein;5|بلقاس|Belqas;5|المنزلة|Manzala;5|شربين|Sherbin;"
            + "6|الزقازيق|Zagazig;6|العاشر من رمضان|10th of Ramadan/Tenth of Ramadan;6|بلبيس|Belbeis/Bilbeis;6|منيا القمح|Minya El Qamh;6|أبو حماد|Abu Hammad;6|فاقوس|Faqous;6|ههيا|Hihya;7|طنطا|Tanta;7|المحلة الكبرى|El Mahalla El Kubra/Mahalla;7|كفر الزيات|Kafr El Zayat;7|زفتى|Zefta/Zifta;7|السنطة|Santa;7|سمنود|Samanoud;8|شبين الكوم|Shebin El Kom/Shibin El Kom;8|مدينة السادات|Sadat City/Sadat;8|منوف|Menouf;8|أشمون|Ashmoun;8|قويسنا|Quesna;8|الباجور|Bagour;8|تلا|Tala;"
            + "9|دمنهور|Damanhour;9|كفر الدوار|Kafr El Dawar;9|رشيد|Rashid/Rosetta;9|إدكو|Edku/Idku;9|إيتاي البارود|Itay El Barud;9|كوم حمادة|Kom Hamada;9|وادي النطرون|Wadi El Natrun;10|كفر الشيخ|Kafr El Sheikh;10|دسوق|Desouk;10|بلطيم|Baltim;10|فوه|Fuwah/Fowa;11|دمياط|Damietta;11|دمياط الجديدة|New Damietta;11|رأس البر|Ras El Bar;11|فارسكور|Faraskour;12|بورسعيد|Port Said;12|بورفؤاد|Port Fouad;13|الإسماعيلية|Ismailia;13|فايد|Fayed;13|القنطرة شرق|Qantara Sharq/Qantara East;13|التل الكبير|Tal El Kebir;14|السويس|Suez;14|العين السخنة|Ain Sokhna;"
            + "15|العريش|Arish;16|شرم الشيخ|Sharm El Sheikh;16|الطور|Tor;16|دهب|Dahab;16|نويبع|Nuweiba;16|طابا|Taba;16|سانت كاترين|Saint Catherine/St Catherine;17|الغردقة|Hurghada;17|رأس غارب|Ras Gharib;17|سفاجا|Safaga;17|القصير|Quseir;17|مرسى علم|Marsa Alam;17|الجونة|Gouna;18|مرسى مطروح|Marsa Matrouh/Matrouh;18|العلمين|Alamein;18|العلمين الجديدة|New Alamein;18|الضبعة|Dabaa;18|سيوة|Siwa;18|الساحل الشمالي|North Coast/Sahel;"
            + "19|الفيوم|Fayoum/Faiyum;19|سنورس|Sinnuris;19|إطسا|Itsa;19|طامية|Tamiya;20|بني سويف|Beni Suef/Bani Sweif;20|الواسطى|Wasta;20|ببا|Beba;20|الفشن|Fashn;21|المنيا|Minya;21|ملوي|Mallawi;21|مغاغة|Maghagha;21|بني مزار|Beni Mazar;21|سمالوط|Samalut;21|أبو قرقاص|Abu Qurqas;21|دير مواس|Deir Mawas;22|أسيوط|Assiut/Asyut;22|ديروط|Dairut;22|منفلوط|Manfalut;22|أبو تيج|Abu Tig;22|القوصية|Qusiya;"
            + "23|سوهاج|Sohag;23|أخميم|Akhmim;23|جرجا|Girga;23|طهطا|Tahta;23|البلينا|Balyana;24|قنا|Qena;24|نجع حمادي|Nag Hammadi;24|قوص|Qus;24|دشنا|Deshna;25|الأقصر|Luxor;25|إسنا|Esna;25|أرمنت|Armant;26|أسوان|Aswan;26|كوم أمبو|Kom Ombo;26|إدفو|Edfu;26|دراو|Daraw;26|أبو سمبل|Abu Simbel;27|الخارجة|Kharga;27|الداخلة|Dakhla;27|الفرافرة|Farafra";
        static readonly Dictionary<string, Known> Egypt = Build();
        static Dictionary<string, Known> Build()
        {
            var d = new Dictionary<string, Known>();
            foreach (var row in List_.Split(';')) { var p = row.Split('|'); if (p.Length != 3) continue; var k = new Known { Gov = int.Parse(p[0], CultureInfo.InvariantCulture), Ar = p[1] }; foreach (var en in p[2].Split('/')) { var f = Fold(en); if (f.Length > 0 && !d.ContainsKey(f)) d[f] = k; } }
            return d;
        }
    }

    public static partial class Carriers
    {
        // «لقيتها في حسابي»: a shipment we aren't sure of, at a company that can't be asked by our reference; the number the owner found there,
        // checked with the company, becomes its number (once: a number another shipment has is refused)
        static Func<Task> Fix(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = ById(c, s.Id, Int(b, "id")); if (x == null || x.State != "unsure") throw new ShopException("state", "الشحنة دي مش محتاجة رقم.");
            var co = Of(x.Company) as CarrierAramex; if (co == null) throw new ShopException("state", "الشحنة دي بنسأل عنها الشركة لوحدنا.");
            string no = Regex.Replace(ShopJson.Digits(Str(b, "tracking", 40)), @"\s", ""); if (!Regex.IsMatch(no, @"^\d{6,20}$")) throw new ShopException("tracking", "اكتب رقم الشحنة زي ما هو في حسابك على " + co.Name + " (أرقام بس).");
            if (Num(c, "SELECT COUNT(*) FROM dbo.Shipments WHERE Company=@c AND Tracking=@t", "@c", x.Company, "@t", no) > 0) throw new ShopException("tracking", "الرقم ده متسجّل لشحنة تانية.");
            var k = Need(c, s.Id, x.Company); if (!Web.Allow("ship-fix:" + s.Id, 20, TimeSpan.FromHours(1))) throw new ShopException("too_many", "محاولات كتير. استنى شوية.");
            return async () =>
            {
                bool there; try { there = await co.Exists(k, no); } catch (CarrierException ex) { throw Said(ex); }
                if (!there) throw new ShopException("tracking", co.Name + " مش لاقية شحنة بالرقم ده.");
                using (var c2 = Db.Open()) { Made(c2, x, new CarrierMade { Tracking = no, Id = no }); ShopOrders.Log_(c2, null, s.Id, long.Parse(x.OrderId, CultureInfo.InvariantCulture), "owner", "كتبت رقم شحنة " + co.Name + " من حسابك: " + no); Ok(res, "ship", ShipJson(c2, ById(c2, s.Id, x.Id), true)); }
            };
        }
    }
}
