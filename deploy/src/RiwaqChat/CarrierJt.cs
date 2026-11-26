// J&T Express Egypt (the J&T Open Platform's own pages, open.jtjms-eg.com, read 2026-11-26). Two signatures: the header's is base64(md5(bizContent +
// Sellybot's privateKey)); the customer's, inside bizContent, is base64(md5(customerCode + PWD + privateKey)), PWD = MD5(password + J&T's fixed word) in capitals.
// Sellybot's apiAccount and privateKey come from the admin (encrypted) after J&T certifies the company: until then it's «قريب» and can't be linked.
// J&T pushes every scan to one address of ours, and the open waybills are asked about every few hours as a second source.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RiwaqChat
{
    public class CarrierJt : Carrier
    {
        const string Word = "jadada236t2";   // written in J&T's public help page: part of how every customer's password is signed
        public override string Id { get { return "jt"; } }
        public override string Name { get { return "J&T"; } }
        public override int PollMinutes { get { return 240; } }
        public override int PollBatch { get { return 30; } }
        public override bool PickupCall { get { return false; } }   // the courier's window goes with the order
        static string Base(CarrierLink k) { return Web.Setting("JtBase", k.Demo ? "https://demoopenapi.jtjms-eg.com/webopenplatformapi/api" : "https://openapi.jtjms-eg.com/webopenplatformapi/api").TrimEnd('/'); }
        public static bool Ready(SqlConnection c) { return !string.IsNullOrEmpty(Billing.Get(c, "JtAccountBox")) && !string.IsNullOrEmpty(Billing.Get(c, "JtKeyBox")); }
        public override string Closed(SqlConnection c) { return Ready(c) ? null : "ربط J&T قريب: مستنيين اعتماد Sellybot عندهم."; }
        public override void Prepare(SqlConnection c, CarrierLink k) { k.Account = Secrets.Unprotect(Billing.Get(c, "JtAccountBox")); k.Private = Secrets.Unprotect(Billing.Get(c, "JtKeyBox")); k.Demo = Billing.Get(c, "JtDemo") == "1"; }
        public override string BadInput(CarrierLink k) { if (!Regex.IsMatch(k.Code ?? "", "^[A-Za-z0-9]{5,30}$")) return "اكتب كود العميل زي ما فرع J&T ادّهولك (زي J0086...)."; return k.Key.Length >= 4 && k.Key.Length <= 100 ? null : "اكتب الباسورد اللي فرع J&T ادّهولك."; }
        public override string NotReady(CarrierLink k) { return k.S("sn").Length < 2 || k.S("sp").Length == 0 || k.S("sa").Length == 0 || k.S("ss").Length < 3 ? "اكتب عنوان الاستلام (الاسم والموبايل والمنطقة والشارع) في إعدادات J&T الأول." : null; }

        static byte[] Md5(string s) { using (var h = MD5.Create()) return h.ComputeHash(Encoding.UTF8.GetBytes(s)); }
        public static string Digest(string biz, string privateKey) { return Convert.ToBase64String(Md5(biz + privateKey)); }
        public static string Sign(CarrierLink k) { string pwd = string.Concat(Md5(k.Key + Word).Select(b => b.ToString("X2"))); return Convert.ToBase64String(Md5(k.Code + pwd + k.Private)); }

        async Task<Dictionary<string, object>> Call(CarrierLink k, string path, Dictionary<string, object> biz, bool signed = true)
        {
            if (signed) { biz["customerCode"] = k.Code; biz["digest"] = Sign(k); } string json = ShopJson.Write(biz);
            var q = new HttpRequestMessage(HttpMethod.Post, Base(k) + path) { Content = new StringContent("bizContent=" + Uri.EscapeDataString(json), Encoding.UTF8, "application/x-www-form-urlencoded") };
            q.Headers.TryAddWithoutValidation("apiAccount", k.Account); q.Headers.TryAddWithoutValidation("digest", Digest(json, k.Private)); q.Headers.TryAddWithoutValidation("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            var r = await Carriers.Send(Name, q); string code = ShopJson.Str(ShopJson.At(r.Body, "code"), 20), msg = ShopJson.Str(ShopJson.At(r.Body, "msg"), 200), ar;
            if (r.Good && code == "1") return r.Body;
            if (r.Status >= 500 || (r.Good && code.Length == 0)) throw new CarrierException("J&T فيها مشكلة دلوقتي. جرّب كمان شوية.", "net", unsure: true);
            if (code == "145003031") throw new CarrierException("كود العميل أو الباسورد غلط. اسأل فرع J&T عنهم.", code, badKey: true);
            if (code == "145002001" || code == "145003101") throw new CarrierException("الأوردر ده متسجّل عند J&T قبل كده.", "dup");
            throw new CarrierException(Errors.TryGetValue(code, out ar) ? ar : "J&T رفضت: " + (msg.Length == 0 ? "رد " + r.Status : msg), code.Length == 0 ? "refused" : code);
        }
        static readonly Dictionary<string, string> Errors = new Dictionary<string, string> {
            { "145003060", "J&T مش عارفة المنطقة دي. اختارها تاني." }, { "145003061", "J&T مش عارفة المدينة دي. اختار المنطقة تاني." }, { "145003062", "J&T مش عارفة المحافظة دي. اختار المنطقة تاني." }, { "145003083", "بيانات عنوان الاستلام ناقصة. كمّلها في إعدادات J&T." }, { "145003084", "بيانات الزبون ناقصة. راجع الأوردر." }, { "145003085", "الأوردر مفيهوش رقم موبايل." }, { "145003086", "العنوان ناقص. راجع الأوردر." }, { "145003088", "ميعاد الاستلام ناقص. راجعه في إعدادات J&T." }, { "145003092", "الوزن من 0.01 لـ 30 كيلو." }, { "145003109", "العنوان طويل على J&T. قصّره." }, { "145003110", "اسم الشارع طويل على J&T. قصّره." },
            { "145003112", "التحصيل عند الاستلام مش متفعّل على حسابك في J&T. كلّم الفرع يفعّله." }, { "145003113", "حسابك في J&T مش شهري. كلّم الفرع." }, { "145003201", "المندوب استلم الشحنة خلاص." }, { "145003202", "الشحنة دي ملغية عند J&T." }, { "121003006", "الشحنة دي مابتتطبعش في حالتها دلوقتي." } };

        // their own check of a code and password; when it isn't open to our account, a question that carries the same signature
        public override async Task Check(CarrierLink k)
        {
            try { await Call(k, "/vip/checkCusPwd", new Dictionary<string, object>()); }
            catch (CarrierException ex) when (!ex.BadKey && ex.Code != "net" && !ex.Down)
            {
                try { await Call(k, "/order/getOrders", new Dictionary<string, object> { { "command", 1 }, { "serialNumber", new[] { "SS-LINK-CHECK" } } }); }
                catch (CarrierException e2) when (e2.Code == "145003064") { }   // «no such order»: the signature was right
                catch (CarrierException e2) when (!e2.BadKey && e2.Code != "net" && !e2.Down)
                {
                    // getOrders may be closed to our account too (an Egyptian developer said so): a label that doesn't exist, signed the same way
                    try { await Call(k, "/order/printOrder", new Dictionary<string, object> { { "billCode", "SSLINKCHECK0" }, { "printSize", 0 } }); } catch (CarrierException e3) when (!e3.BadKey && e3.Code != "net" && !e3.Down) { }
                }
            }
            k.AccName = k.Code;
        }

        static Dictionary<string, object> Person(string name, string phone, CarrierAreaRow a, string street) { var d = new Dictionary<string, object> { { "name", Carriers.Cut(name, 50) }, { "mobile", phone }, { "phone", phone }, { "countryCode", "EGY" }, { "prov", a == null ? "" : a.Gov }, { "city", a == null ? "" : a.City }, { "area", a == null ? "" : a.Area }, { "street", Carriers.Cut(street, 200) } }; return d; }

        public override async Task<CarrierMade> Create(CarrierLink k, CarrierParcel p)
        {
            CarrierAreaRow from; using (var c = Db.Open()) from = CarrierAreas.ByKey(c, Id, k.S("sa")); if (from == null) throw new CarrierException("منطقة الاستلام اتغيّرت عند J&T. اختارها تاني من الإعدادات.", "sender");
            // the courier's window: today's while it's still on, else the next working day's
            var now = Carriers.Cairo(DateTime.UtcNow); int h1 = ShopJson.Int(ShopJson.At(k.Set, "from"), 10), h2 = ShopJson.Int(ShopJson.At(k.Set, "to"), 18); var day = now.Hour < h2 - 1 ? now.Date : now.Date.AddDays(1); if (day.DayOfWeek == DayOfWeek.Friday) day = day.AddDays(1); var start = day.AddHours(h1); if (start < now) start = now;
            var to = Person(p.Name, p.Phone, p.Area, p.Address + (string.IsNullOrWhiteSpace(p.Landmark) ? "" : "، علامة مميزة: " + p.Landmark)); if (p.Phone2 != null) { to["phone"] = p.Phone2; to["alternateReceiverPhoneNo"] = p.Phone2; } var sender = Person(k.S("sn"), ShopOrders.Local(k.S("sp")), from, k.S("ss"));
            var biz = new Dictionary<string, object> { { "txlogisticId", p.Ref }, { "expressType", "EZ" }, { "orderType", "1" }, { "serviceType", "01" }, { "deliveryType", "04" }, { "payType", "PP_PM" }, { "operateType", 1 }, { "goodsType", k.S("goods", "ITN16") }, { "weight", p.Kg > 0 ? Math.Max(0.01m, Math.Min(30, p.Kg)) : ShopJson.Dec(ShopJson.At(k.Set, "weight"), 1) }, { "totalQuantity", "1" }, { "itemsValue", p.Cod.ToString("0.##", CultureInfo.InvariantCulture) }, { "priceCurrency", "EGP" }, { "remark", Carriers.Cut(p.Note, 200) }, { "pickInfo", Carriers.Cut(p.Goods, 500) },
                { "sendStartTime", start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) }, { "sendEndTime", day.AddHours(h2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) }, { "sender", sender }, { "receiver", to },
                { "items", p.Lines.Take(20).Select(i => new Dictionary<string, object> { { "itemType", k.S("goods", "ITN16") }, { "itemName", Carriers.Cut(i.Name, 30) }, { "number", i.Qty }, { "itemValue", i.Price.ToString("0.##", CultureInfo.InvariantCulture) }, { "priceCurrency", "EGP" }, { "desc", Carriers.Cut(i.Opts ?? i.Name, 100) } }).ToArray() } };
            if (k.B("value", true) && p.Value > 0) biz["offerFee"] = p.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (p.Day != null) { biz["expectDeliveryStartTime"] = p.Day + (p.Part == "pm" ? " 15:00:00" : " 09:00:00"); biz["expectDeliveryEndTime"] = p.Day + (p.Part == "am" ? " 15:00:00" : " 21:00:00"); }   // the day the customer chose (ShopShipDay)
            var r = await Call(k, "/order/addOrder", biz); return new CarrierMade { Tracking = ShopJson.Str(ShopJson.At(r, "data", "billCode"), 60), Id = ShopJson.Str(ShopJson.At(r, "data", "txlogisticId"), 60) };
        }

        public override async Task<CarrierMade> Find(CarrierLink k, string reference)
        {
            Dictionary<string, object> r;
            try { r = await Call(k, "/order/getOrders", new Dictionary<string, object> { { "command", 1 }, { "serialNumber", new[] { reference } } }); }
            catch (CarrierException ex) when (ex.Code == "145003064") { return null; }
            catch (CarrierException ex) when (!ex.Unsure && !ex.Down && !ex.BadKey) { throw new CarrierException("J&T مش بتسمح لنا ندوّر على الشحنة. دوّر عليها في حسابك برقم " + reference + ".", "nofind"); }
            var x = ShopJson.List(ShopJson.At(r, "data")).OfType<Dictionary<string, object>>().FirstOrDefault(o => ShopJson.Str(ShopJson.At(o, "txlogisticId")) == reference && ShopJson.Str(ShopJson.At(o, "billCode")).Length > 0 && !Regex.IsMatch(ShopJson.Str(ShopJson.At(o, "orderStatus")), "cancel|取消", RegexOptions.IgnoreCase));
            return x == null ? null : new CarrierMade { Tracking = ShopJson.Str(ShopJson.At(x, "billCode"), 60), Id = reference };
        }

        public override int MaxLabels { get { return 1; } }
        public override async Task<byte[]> Labels(CarrierLink k, List<CarrierShip> l, string size)
        {
            var r = await Call(k, "/order/printOrder", new Dictionary<string, object> { { "billCode", l[0].Tracking }, { "printSize", size == "A6" ? 0 : 2 }, { "printCod", 1 }, { "showCustomerOrderId", 1 } });
            var pdf = Carriers.PdfIn(new Carriers.Reply { Root = r, Bytes = new byte[0] }); if (pdf == null) throw new CarrierException("J&T مابعتتش بوليصة " + l[0].Tracking + ".", "label"); return pdf;
        }
        public override Task<string> Pickup(CarrierLink k, DateTime day, int parcels, string place, string note, List<CarrierShip> waiting) { throw new CarrierException("مندوب J&T بييجي مع الأوردر نفسه.", "pickup"); }
        public override async Task Cancel(CarrierLink k, CarrierShip x) { await Call(k, "/order/cancelOrder", new Dictionary<string, object> { { "orderType", "1" }, { "txlogisticId", x.Ref }, { "reason", "Cancelled by the seller" } }); }

        public override async Task<List<CarrierEvent>> Ask(CarrierLink k, List<CarrierShip> l)
        {
            var r = await Call(k, "/logistics/trace", new Dictionary<string, object> { { "billCodes", string.Join(",", l.Select(x => x.Tracking)) } }, false); var o = new List<CarrierEvent>();
            foreach (var w in ShopJson.List(ShopJson.At(r, "data")).OfType<Dictionary<string, object>>()) o.AddRange(Scans(w));
            return o;
        }
        public override async Task<List<CarrierAreaRow>> Areas(CarrierLink k)
        {
            var r = await Call(k, "/location/getLocation", new Dictionary<string, object> { { "countryCode", "EGY" } }, false); Func<Dictionary<string, object>, string, string> t = (d, n) => ShopJson.Str(ShopJson.At(d, n), 150);
            return ShopJson.List(ShopJson.At(r, "data")).OfType<Dictionary<string, object>>().Select(d => new CarrierAreaRow { GovId = t(d, "provCode"), Gov = t(d, "prov"), CityId = t(d, "cityCode"), City = t(d, "city"), AreaId = t(d, "areaCode"), Area = t(d, "area") }).ToList();
        }
        // one waybill's scans, oldest first (they come newest first)
        public static List<CarrierEvent> Scans(Dictionary<string, object> w)
        {
            string bill = ShopJson.Str(ShopJson.At(w, "billCode"), 60), reference = ShopJson.Str(ShopJson.At(w, "txlogisticId"), 60); var l = ShopJson.List(ShopJson.At(w, "details")).OfType<Dictionary<string, object>>().ToList(); if (l.Count == 0 && ShopJson.At(w, "scanType") != null) l.Add(w);   // the order's own status push has no list
            var o = new List<CarrierEvent>();
            foreach (var d in l)
            {
                string type = ShopJson.Str(ShopJson.At(d, "scanType"), 60), said = ShopJson.Str(ShopJson.At(d, "probleDescription"), 150); if (said.Length == 0) said = ShopJson.Str(ShopJson.At(d, "problemReason"), 150); var e = Map(ShopJson.Str(ShopJson.At(d, "scanTypeCode"), 4), type, said);
                e.Tracking = bill.Length > 0 ? bill : ShopJson.Str(ShopJson.At(d, "billCode"), 60); e.Ref = reference; e.At = Carriers.FromCairo(ShopJson.Str(ShopJson.At(d, "scanTime"), 30)) ?? Carriers.FromCairo(ShopJson.Str(ShopJson.At(d, "time"), 30)); o.Add(e);
            }
            return o.OrderBy(e => e.At ?? DateTime.MaxValue).ToList();
        }
        // by the number when it comes, else by the words (the trace's own example has the words alone)
        public static CarrierEvent Map(string code, string type, string said)
        {
            int n; if (!int.TryParse(code, out n)) { var t = (type ?? "").ToLowerInvariant(); n = Regex.IsMatch(t, "return sign|退件签收") ? 13 : Regex.IsMatch(t, "return scan|退件扫描") ? 14 : Regex.IsMatch(t, "sign|签收") ? 10 : Regex.IsMatch(t, "problem|问题件") ? 11 : Regex.IsMatch(t, "delivery scan|out of warehouse|出仓") ? 5 : Regex.IsMatch(t, "pickup scan|collection|揽收|已取件") ? 1 : Regex.IsMatch(t, "arriv|到件") ? 4 : Regex.IsMatch(t, "sending|发件") ? 3 : Regex.IsMatch(t, "取消|cancel") ? 99 : 0; }
            var e = new CarrierEvent { Code = n > 0 ? n.ToString(CultureInfo.InvariantCulture) : Carriers.Cut(type ?? "?", 30) };
            switch (n)
            {
                case 1: e.Ours = "picked"; break; case 5: e.Ours = "out"; break; case 10: e.Ours = "delivered"; break; case 13: e.Ours = "returned"; break; case 14: e.Ours = "returning"; break; case 99: e.Ours = "cancelled"; break;
                case 3: case 4: case 6: case 7: case 8: case 9: case 12: case 15: e.Ours = "transit"; break;
                case 11:
                    e.Ours = "attempt"; var w = (said ?? "").ToLowerInvariant();
                    e.Reason = Regex.IsMatch(w, "refus|reject|رفض|拒") ? "refused" : Regex.IsMatch(w, "no answer|not answer|unreach|switched off|لا يرد|مش بيرد|لم يرد|مغلق|无人接听|电话") ? "noanswer" : Regex.IsMatch(w, "postpone|resched|delay|تأجيل|أجل|اجل|改约") ? "later" : Regex.IsMatch(w, "address|عنوان|地址") ? "address" : ""; e.Text = said.Length == 0 ? null : said; break;
            }
            return e;
        }
    }
}
