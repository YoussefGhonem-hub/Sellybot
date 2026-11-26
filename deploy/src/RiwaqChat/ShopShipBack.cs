// «المندوب يجيب المرتجع»: from a return or exchange request (ShopReturns) one press makes a shipment with the company the order went with
// (or the one the owner picks). Bosta (docs.bosta.co api.yaml, read 2026-11-26): type 25 «Customer Return Pickup» (cod 0: a pickup
// collects nothing; their CRP takes only a refund, never a positive amount) and type 30 «Exchange» (brings the new piece, takes the old one,
// cod = the difference when the owner wants it). ShipBlu (their Postman collection): POST /returns/, and POST /exchanges/ on an order that was
// delivered with them. J&T: their open platform has no return or exchange order, so the button says so.
// The shipment is a row of Shipments (Source 'ret', one open for an order) with its ShipBacks row; the companies' notifications and the
// hourly asking reach it as they reach the others (Carriers.Apply -> Happened here). It never moves the order: the request moves, and when
// the parcel is back the owner presses «وصلتني» and the quantity comes back the way returns already do (ShopReturns.Do «received»).
// The customer hears once, on WhatsApp, that the courier is coming, with the tracking link; never at night (it waits for the morning).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class ShopShipBack
    {
        class Back { public long ShipmentId; public int ReturnId; public long OrderId; public string Kind; public decimal Collect; public DateTime? ToldAt, TellDue, MadeAt; public string TellNote; }
        class Req { public int Id; public long OrderId; public string Kind, Status; public List<Dictionary<string, object>> Items; }
        static Back BackOf(SqlConnection c, long shipId)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShipBacks WHERE ShipmentId=@i", "@i", shipId)) using (var r = cmd.ExecuteReader())
                return r.Read() ? new Back { ShipmentId = Convert.ToInt64(r["ShipmentId"]), ReturnId = (int)r["ReturnId"], OrderId = Convert.ToInt64(r["OrderId"]), Kind = (string)r["Kind"], Collect = (decimal)r["Collect"], ToldAt = D(r["ToldAt"]), TellDue = D(r["TellDue"]), MadeAt = D(r["MadeAt"]), TellNote = r["TellNote"] as string } : null;
        }
        static DateTime? D(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }
        static Req ReqOf(SqlConnection c, int storeId, int id)
        {
            using (var cmd = Db.Cmd(c, "SELECT Id, OrderId, Kind, Status, Items FROM dbo.ShopReturns WHERE Id=@i AND StoreId=@s", "@i", id, "@s", storeId)) using (var r = cmd.ExecuteReader())
                return r.Read() ? new Req { Id = (int)r[0], OrderId = Convert.ToInt64(r[1]), Kind = (string)r[2], Status = (string)r[3], Items = ShopJson.Arr((string)r[4]).OfType<Dictionary<string, object>>().ToList() } : null;
        }
        static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        static string Cut(string s, int n) { return Carriers.Cut(s, n); }
        public static bool On(SqlConnection c, StoreInfo s, Shop sh) { return s != null && Carriers.On(c, s) && ShopShip2.On(sh, "sback"); }
        // the assistant: what a customer may ask about a return («المندوب هييجي ياخده؟»)
        public static string BotText(SqlConnection c, Shop sh)
        {
            try { var s = Db.StoreById(c, sh.StoreId); return On(c, s, sh) && Carriers.Links(c, s.Id).Any(k => k.Company != "jt") ? "بعد ما المتجر يوافق على طلب الاسترجاع أو الاستبدال ممكن يبعت مندوب شركة الشحن ياخد المنتج من عند الزبون، والزبون بيوصله رقم التتبع على الواتساب. ماتوعدش بميعاد المندوب. " : ""; }
            catch (Exception ex) { Log.Error(ex); return ""; }
        }
        static int UnsureWait { get { int v; return int.TryParse(Web.Setting("CarrierUnsureSec"), out v) && v >= 0 ? v : 60; } }
        static readonly Dictionary<string, string> Track = new Dictionary<string, string> { { "bosta", "https://bosta.co/ar-eg/tracking-shipments?shipment-number={0}" }, { "shipblu", "https://go.shipblu.com/en/{0}" } };
        // the company's own tracking page (their sites read the number from these links; web.config BostaTrackUrl / ShipBluTrackUrl may change them)
        static string TrackUrl(string co, string t) { string f; if (!Track.TryGetValue(co, out f)) return null; f = Web.Setting(co == "bosta" ? "BostaTrackUrl" : "ShipBluTrackUrl", f); return string.Format(CultureInfo.InvariantCulture, f, Uri.EscapeDataString(t ?? "")); }
        static string Kinds(string k) { return k == "exchange" ? "الاستبدال" : "المرتجع"; }

        // ---------- in the returns list (ShopReturns.List): each request's pickup, and what can be done ----------
        public static void Mark(SqlConnection c, Shop sh, List<Dictionary<string, object>> l)
        {
            try
            {
                var s = Db.StoreById(c, sh.StoreId); if (!On(c, s, sh) || l.Count == 0) return; var links = Carriers.Links(c, s.Id);
                foreach (var d in l)
                {
                    int rid = (int)d["id"]; var x = Carriers.Ships(c, 1, "StoreId=@s AND Source='ret' AND Id IN (SELECT ShipmentId FROM dbo.ShipBacks WHERE StoreId=@s AND ReturnId=@r) ORDER BY Id DESC", "@s", s.Id, "@r", rid).FirstOrDefault();
                    string st = (string)d["st"];
                    // received from the returns tab itself: the parcel needs no «وصلتني» any more
                    if (x != null && x.BackAt.HasValue && !x.GotAt.HasValue && (st == "received" || st == "done")) { Exec(c, "UPDATE dbo.Shipments SET GotAt=SYSUTCDATETIME() WHERE Id=@i AND GotAt IS NULL", "@i", x.Id); x.GotAt = DateTime.UtcNow; }
                    var last = Carriers.OfOrder(c, s.Id, (long)d["order"]);
                    d["pk"] = ShopShip2.D("ship", x == null ? null : Json(c, x), "can", st == "new" || st == "approved", "def", last != null && links.Any(k => k.Company == last.Company) ? last.Company : links.Count > 0 ? links[0].Company : "",
                        "cos", links.Select(k => ShopShip2.D("id", k.Company, "name", Carriers.Of(k.Company).Name, "no", Why(k.Company, (string)d["kind"], last) ?? "")).ToArray());
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        // why a company can't take it; null = it can
        static string Why(string co, string kind, CarrierShip delivered)
        {
            if (co == "jt") return "المندوب يجيب المرتجع مش متاحة مع J&T.";
            if (co == "shipblu" && kind == "exchange" && (delivered == null || delivered.Company != "shipblu" || delivered.Ours != "delivered" || string.IsNullOrEmpty(delivered.TheirId))) return "الاستبدال مع ShipBlu بيتعمل بس على أوردر اتسلّم معاهم.";
            return null;
        }
        static Dictionary<string, object> Json(SqlConnection c, CarrierShip x)
        {
            var b = BackOf(c, x.Id); string t; Carriers.OursText.TryGetValue(x.Ours ?? "", out t);
            string text = x.State == "making" ? "بتتبعت دلوقتي" : x.State == "unsure" ? "مش متأكدين إنها اتسجّلت" : x.State == "failed" ? "ماتبعتتش" : x.State == "off" ? "اتلغت" : t ?? "اتسجّلت عند الشركة";
            var ev = new List<object>(); using (var cmd = Db.Cmd(c, "SELECT TOP 20 Text, Ours, At FROM dbo.ShipmentEvents WHERE ShipmentId=@i AND StoreId=@s ORDER BY At DESC, Id DESC", "@i", x.Id, "@s", x.StoreId)) using (var r = cmd.ExecuteReader()) while (r.Read()) ev.Add(ShopShip2.D("t", r[0] as string ?? "", "ours", (string)r[1], "at", ShopShip2.Iso(D(r[2]))));
            return ShopShip2.D("id", x.Id, "c", x.Company, "cName", Carriers.Of(x.Company) == null ? x.Company : Carriers.Of(x.Company).Name, "tracking", x.Tracking ?? "", "url", TrackUrl(x.Company, x.Tracking) ?? "", "state", x.State, "ours", x.Ours ?? "", "text", text, "why", x.ReasonText ?? "", "err", x.Error ?? "",
                "collect", x.Cod, "kind", b == null ? "" : b.Kind, "told", ShopShip2.Iso(b == null ? null : b.ToldAt), "tellDue", ShopShip2.Iso(b == null ? null : b.TellDue), "tellNote", b == null ? "" : b.TellNote ?? "", "back", x.BackAt.HasValue, "got", x.GotAt.HasValue,
                "canCancel", x.State == "live" && (x.Ours == "asked" || x.Ours == "made"), "events", ev);
        }

        // ---------- the owner's calls ----------
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res) { return false; }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null; if (!On(c, s, sh)) return false;
            switch (a)
            {
                case "sback-go": later = Go(c, s, sh, b, res); return true;
                case "sback-ask": later = AskOne(c, s, b, res); return true;
                case "sback-cancel": later = CancelOne(c, s, b, res); return true;
                case "sback-label": later = Label(c, s, b, res); return true;
                case "sback-got": Got(c, s, sh, Mine(c, s, b), b, res); return true;
            }
            return false;
        }
        static CarrierShip Mine(SqlConnection c, StoreInfo s, Dictionary<string, object> b) { var x = Carriers.ById(c, s.Id, ShopShip2.Int(b, "id")); if (x == null || x.Source != "ret") throw new ShopException("gone", "الشحنة دي مش موجودة."); return x; }
        static CarrierLink Need(SqlConnection c, int sid, string company)
        {
            var co = Carriers.Of(company); if (co == null) throw new ShopException("company", "شركة الشحن دي مش موجودة.");
            var k = Carriers.Link(c, sid, company); if (k == null || string.IsNullOrEmpty(k.Key)) throw new ShopException("link", "اربط حسابك على " + co.Name + " الأول من تبويب «الشحن».");
            var no = co.Closed(c); if (no != null) throw new ShopException("closed", no); return k;
        }

        // «المندوب يجيب المرتجع» {id: the request, c: the company, collect: the exchange's difference, area: the customer's area at the company, note}
        static Func<Task> Go(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var q = ReqOf(c, s.Id, ShopShip2.Int(b, "id")); if (q == null) throw new ShopException("gone", "الطلب ده مش موجود.");
            if (q.Status != "new" && q.Status != "approved") throw new ShopException("state", "الطلب ده «" + ShopReturns.StatusText(q.Status) + "»، ماينفعش نبعتله مندوب.");
            var o = ShopOrders.ById(c, s.Id, q.OrderId); if (o == null) throw new ShopException("gone", "الأوردر ده مش موجود.");
            var delivered = Carriers.OfOrder(c, s.Id, o.Id); string cid = ShopShip2.Str(b, "c", 10);
            if (cid.Length == 0) cid = delivered != null && Carriers.Link(c, s.Id, delivered.Company) != null ? delivered.Company : Carriers.Links(c, s.Id).Select(k => k.Company).FirstOrDefault() ?? "";
            var co = Carriers.Of(cid); if (co == null) throw new ShopException("company", "اربط شركة شحن الأول من تبويب «الشحن».");
            var why = Why(co.Id, q.Kind, delivered); if (why != null) throw new ShopException(co.Id == "jt" ? "jt" : "kind", why);
            var k = Need(c, s.Id, co.Id); var need = co.NotReady(k); if (need != null) throw new ShopException("set", need);
            decimal collect = q.Kind == "exchange" ? ShopShip2.Clamp(ShopJson.Dec(ShopJson.At(b, "collect")), 0, co.MaxCod > 0 ? co.MaxCod : 30000) : 0;
            CarrierAreaRow area; string ak = ShopShip2.Str(b, "area", 140);
            if (ak.Length > 0) { area = CarrierAreas.ByKey(c, co.Id, ak); if (area == null || area.Closed) throw new ShopException("area", "اختار المنطقة من القايمة تاني."); CarrierAreas.Remember(c, s.Id, co.Id, o.Gov, o.City, o.Area, area); }
            else
            {
                var h = CarrierAreas.Match(c, s.Id, co.Id, o.Gov, o.City, o.Area);
                if (!h.Sure) throw new ShopException("area", h.Closed ? co.Name + " مابتوصّلش لمنطقة الزبون." : "اختار منطقة الزبون عند " + co.Name + ".", ShopShip2.D("more", h.More.Take(12).Select(x => ShopShip2.D("k", x.Key, "t", x.Label)).ToArray()));
                area = h.Row;
            }
            // one open pickup for an order; a press that got no answer is looked up before anything is made again
            var last = Carriers.Ships(c, 1, "StoreId=@s AND Source='ret' AND OrderId=@o ORDER BY Id DESC", "@s", s.Id, "@o", o.Id.ToString(CultureInfo.InvariantCulture)).FirstOrDefault(); var lb = last == null ? null : BackOf(c, last.Id);
            if (last != null && last.State == "live") { if (lb != null && lb.ReturnId == q.Id) { Web.Json(res, 200, ShopShip2.D("ok", true, "had", true)); return null; } throw new ShopException("busy", "فيه مندوب رايح للأوردر ده بالفعل لطلب تاني. استنى لما يخلص."); }
            if (!Web.Allow("sback:" + s.Id, 60, TimeSpan.FromHours(1))) throw new ShopException("too_many", "طلبات كتير. استنى شوية.");
            bool look; var row = Claim(c, o, q, co, area, collect, last, lb, out look);
            var p = Parcel(c, s, sh, o, q, k, area, row.Ref, collect, ShopShip2.Str(b, "note", 150)); string theirOrder = delivered == null ? null : delivered.TheirId; int sid = s.Id;
            return async () =>
            {
                CarrierException err = null; CarrierMade made = null; bool maybe = look;
                try
                {
                    if (look) { made = await FindBack(co, k, row, q.Kind); maybe = made != null; }
                    if (made == null)
                        try { made = await Create(co, k, p, q.Kind == "exchange", theirOrder); maybe = true; }
                        catch (CarrierException ex) when (ex.Unsure || ex.Code == "dup") { maybe = true; try { made = await FindBack(co, k, row, q.Kind); } catch (CarrierException) { } if (made == null) throw; }
                    if (string.IsNullOrEmpty(made.Tracking)) throw new CarrierException(co.Name + " سجّلت الشحنة من غير رقم تتبع.", "notrack", unsure: true);
                }
                catch (CarrierException ex) { err = ex; }
                catch (Exception ex) { Log.Error(ex); maybe = true; err = new CarrierException("حصلت مشكلة عندنا.", "bug", unsure: true); }
                using (var c2 = Db.Open())
                {
                    if (err == null) { Carriers.Made(c2, row, made); await Finish(c2, sid, row.Id); Web.Json(res, 200, ShopShip2.D("ok", true, "ship", Json(c2, Carriers.ById(c2, sid, row.Id)))); return; }
                    string text = maybe ? err.Message + " مش متأكدين إن الشحنة اتسجّلت، فهنسأل الشركة عنها قبل أي محاولة تانية." : err.Message;
                    Exec(c2, "UPDATE dbo.Shipments SET State=@st, Error=@e, ClaimedAt=SYSUTCDATETIME() WHERE Id=@i AND State='making'", "@st", maybe ? "unsure" : "failed", "@e", Cut(text, 300), "@i", row.Id);
                    ShopOrders.Log_(c2, null, sid, o.Id, "system", Cut("مندوب " + Kinds(q.Kind) + " ماتطلبش من " + co.Name + ": " + text, 290));
                    if (err.BadKey) Carriers.Fault(c2, k, err.Message);
                    throw new ShopException(err.BadKey ? "badkey" : err.Code == "net" ? "net" : "carrier", text);
                }
            };
        }
        static CarrierShip Claim(SqlConnection c, ShopOrder o, Req q, Carrier co, CarrierAreaRow area, decimal collect, CarrierShip last, Back lb, out bool look)
        {
            string oid = o.Id.ToString(CultureInfo.InvariantCulture); look = false;
            if (last != null && lb != null && lb.ReturnId == q.Id && (last.State == "failed" || last.State == "unsure" || last.State == "making"))
            {
                // the same request again: the company may already have the one that got no answer
                if (last.Company != co.Id && last.State != "failed") throw new ShopException("unsure", "لسه مش متأكدين من شحنة " + Carriers.Of(last.Company).Name + " بتاعة الطلب ده. اتأكد منها الأول.");
                look = last.State != "failed";
                using (var cmd = Db.Cmd(c, "UPDATE dbo.Shipments SET State='making', ClaimedAt=SYSUTCDATETIME(), Company=@c, Cod=@cod, Area=@a, Phone=@p, Error=NULL WHERE Id=@i AND State=@st AND (State='failed' OR ClaimedAt<DATEADD(SECOND,-@w,SYSUTCDATETIME()))", "@c", co.Id, "@cod", collect, "@a", Cut(area.Label, 200), "@p", o.Phone, "@i", last.Id, "@st", last.State, "@w", UnsureWait))
                    if (cmd.ExecuteNonQuery() == 0) throw new ShopException("busy", look ? "لسه بنتأكد من المحاولة اللي فاتت. استنى دقيقة وجرّب تاني." : "بيتطلب دلوقتي.");
                Exec(c, "UPDATE dbo.ShipBacks SET Collect=@m WHERE ShipmentId=@i", "@m", collect, "@i", last.Id);
                return Carriers.ById(c, o.StoreId, last.Id);
            }
            if (last != null && (last.State == "unsure" || last.State == "making")) throw new ShopException("unsure", "فيه شحنة للأوردر ده لسه مش متأكدين منها. اتأكد منها الأول من «الشحن».");
            int n = Carriers.Num(c, "SELECT COUNT(*) FROM dbo.ShipBacks WHERE StoreId=@s AND ReturnId=@r", "@s", o.StoreId, "@r", q.Id); long id;
            try
            {
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Shipments(StoreId, Source, OrderId, OrderName, Company, Ref, Phone, State, Cod, Area) OUTPUT INSERTED.Id VALUES(@s, 'ret', @o, @n, @c, @r, @p, 'making', @cod, @a)", "@s", o.StoreId, "@o", oid, "@n", Cut(o.NoText + (q.Kind == "exchange" ? " (استبدال)" : " (مرتجع)"), 40), "@c", co.Id, "@r", "SR" + o.StoreId + "-" + q.Id + (n == 0 ? "" : "-" + (n + 1)), "@p", o.Phone, "@cod", collect, "@a", Cut(area.Label, 200)))
                    id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { throw new ShopException("busy", "بيتطلب دلوقتي."); }
            Exec(c, "INSERT INTO dbo.ShipBacks(ShipmentId, StoreId, ReturnId, OrderId, Kind, Collect) VALUES(@i, @s, @r, @o, @k, @m)", "@i", id, "@s", o.StoreId, "@r", q.Id, "@o", o.Id, "@k", q.Kind, "@m", collect);
            return Carriers.ById(c, o.StoreId, id);
        }
        static CarrierParcel Parcel(SqlConnection c, StoreInfo s, Shop sh, ShopOrder o, Req q, CarrierLink k, CarrierAreaRow area, string reference, decimal collect, string note)
        {
            Func<Dictionary<string, object>, string, string> line = (i, t) => ShopJson.Str(ShopJson.At(i, "n"), 120) + (t.Length > 0 ? " (" + t + ")" : "") + " × " + ShopJson.Int(ShopJson.At(i, "q"));
            string goods = Cut(string.Join("، ", q.Items.Select(i => line(i, ShopJson.Str(ShopJson.At(i, "o"), 80)))), 250), fresh = Cut(string.Join("، ", q.Items.Select(i => line(i, ShopJson.Str(ShopJson.At(i, "toText"), 80).Length > 0 ? ShopJson.Str(ShopJson.At(i, "toText"), 80) : ShopJson.Str(ShopJson.At(i, "o"), 80)))), 250);
            string origin = Web.Setting("CarrierHookBase") ?? EasyOrders.Origin(HttpContext.Current == null ? null : HttpContext.Current.Request);
            string notes = Regex.Replace(string.Join(" - ", new[] { (q.Kind == "exchange" ? "استبدال أوردر " : "مرتجع أوردر ") + o.NoText, note, k.S("note") }.Where(x => !string.IsNullOrWhiteSpace(x))), @"\s+", " ");
            return new CarrierParcel { Ref = reference, OrderNo = o.No.ToString(CultureInfo.InvariantCulture), Name = o.Name, Phone = ShopOrders.Local(o.Phone), Phone2 = o.Phone2 == null || o.Phone2 == o.Phone ? null : ShopOrders.Local(o.Phone2), Email = k.S("email"), Address = string.Join("، ", new[] { o.City, o.Area, o.Address }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Landmark = o.Landmark, Note = notes, Goods = goods, NewGoods = fresh, Items = Math.Max(1, q.Items.Sum(i => ShopJson.Int(ShopJson.At(i, "q")))), Cod = collect, Value = q.Items.Sum(i => ShopJson.Dec(ShopJson.At(i, "t"))), Area = area, Lines = o.Items,
                HookUrl = string.IsNullOrEmpty(origin) ? null : origin + "/api/carrier-hook.ashx?c=" + k.Company + "&h=" + k.HookId, HookSecret = k.HookSecret };
        }
        static Task<CarrierMade> Create(Carrier co, CarrierLink k, CarrierParcel p, bool exchange, string theirOrder)
        {
            var b = co as CarrierBosta; if (b != null) return b.CreateBack(k, p, exchange);
            var sb = co as CarrierShipBlu; if (sb != null) return sb.CreateBack(k, p, exchange, theirOrder);
            throw new CarrierException("المندوب يجيب المرتجع مش متاحة مع " + co.Name + ".", "kind");
        }
        // null = surely not there
        static async Task<CarrierMade> FindBack(Carrier co, CarrierLink k, CarrierShip x, string kind)
        {
            if (co is CarrierBosta) return await co.Find(k, x.Ref);
            var sb = co as CarrierShipBlu; if (sb != null) return await sb.FindBack(k, x.Ref, kind == "exchange");
            return null;
        }
        // Carriers.Sure: a pickup that got no answer
        public static async Task<CarrierMade> Find(CarrierLink k, CarrierShip x) { using (var c = Db.Open()) { var b = BackOf(c, x.Id); return await FindBack(Carriers.Of(x.Company), k, x, b == null ? "return" : b.Kind); } }

        // made: the request is approved, the order's log says it, and the customer hears (once; in the morning if it's night)
        static async Task Finish(SqlConnection c, int sid, long shipId)
        {
            var x = Carriers.ById(c, sid, shipId); var b = BackOf(c, shipId); if (x == null || b == null || string.IsNullOrEmpty(x.Tracking)) return;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShipBacks SET MadeAt=SYSUTCDATETIME() WHERE ShipmentId=@i AND MadeAt IS NULL", "@i", shipId)) if (cmd.ExecuteNonQuery() == 0) return;
            // a pickup the courier hasn't started: not «waiting for the courier at your place» (that's an exchange's new piece)
            if (b.Kind != "exchange") Exec(c, "UPDATE dbo.Shipments SET Ours='asked' WHERE Id=@i AND Ours='made'", "@i", shipId);
            var co = Carriers.Of(x.Company); var s = Db.StoreById(c, sid); var sh = Shops.ByStore(c, sid);
            ShopOrders.Log_(c, null, sid, b.OrderId, "owner", Cut("مندوب " + co.Name + " هييجي ياخد " + Kinds(b.Kind) + "، رقم التتبع " + x.Tracking + (b.Collect > 0 ? "، ويحصّل فرق " + ShopOrders.Money(b.Collect) : ""), 290));
            var q = ReqOf(c, sid, b.ReturnId);
            if (q != null && q.Status == "new" && s != null && sh != null) try { ShopReturns.Do(c, s, sh, ShopShip2.D("id", q.Id, "st", "approved", "note", "", "tell", false)); } catch (ShopException) { }
            await Tell(c, sid, shipId);
        }
        static async Task Tell(SqlConnection c, int sid, long shipId)
        {
            var night = WaNotify.QuietUntil(); if (night.HasValue) { Exec(c, "UPDATE dbo.ShipBacks SET TellDue=@d WHERE ShipmentId=@i AND ToldAt IS NULL", "@d", night.Value, "@i", shipId); return; }
            // written first: never twice, whatever happens after
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShipBacks SET ToldAt=SYSUTCDATETIME(), TellDue=NULL WHERE ShipmentId=@i AND ToldAt IS NULL", "@i", shipId)) if (cmd.ExecuteNonQuery() == 0) return;
            var x = Carriers.ById(c, sid, shipId); var b = BackOf(c, shipId); var s = Db.StoreById(c, sid); var sh = Shops.ByStore(c, sid); var o = ShopOrders.ById(c, sid, b.OrderId);
            string no = s == null || sh == null || o == null || o.Phone.Length == 0 ? "مفيش رقم للزبون" : x.State != "live" ? "الشحنة مابقتش مفتوحة" : Billing.Expired(s) ? "الباقة خلصت" : WaNotify.CantSend(c, s);
            if (no == null) no = await WaNotify.ToCustomer(c, s, o.Phone, Text(sh, o, x, b), "sb-ship-back-" + shipId);
            Exec(c, "UPDATE dbo.ShipBacks SET TellNote=@n WHERE ShipmentId=@i", "@n", no == null ? null : Cut("الرسالة ماتبعتتش: " + no, 200), "@i", shipId);
            if (o != null) ShopOrders.Log_(c, null, sid, o.Id, "system", no == null ? "اتبعتله على الواتساب إن مندوب " + Kinds(b.Kind) + " جاي" : Cut("رسالة مندوب " + Kinds(b.Kind) + " ماتبعتتش: " + no, 290));
        }
        static string Text(Shop sh, ShopOrder o, CarrierShip x, Back b)
        {
            bool en = o.Lang == "en", ex = b.Kind == "exchange"; string first = (o.Name ?? "").Trim().Split(' ')[0], store = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name, co = Carriers.Of(x.Company).Name, url = TrackUrl(x.Company, x.Tracking);
            if (en)
                return "Hello " + first + " 👋\nAbout your " + (ex ? "exchange" : "return") + " for order *" + o.NoText + "* from *" + store + "*: a " + (x.Company == "bosta" ? "Bosta" : co) + " courier will come " + (ex ? "with the new item and take the old one" : "to pick up the item") + (b.Collect > 0 ? ", and you'll pay the difference of *EGP " + b.Collect.ToString("#,0.##", CultureInfo.InvariantCulture) + "*" : "") + ".\n\n📦 Tracking number: " + x.Tracking + (url == null ? "" : "\n🔗 " + url) + "\n\nPlease have it ready in its bag or box, and keep your phone near you 🙏";
            return "أهلًا " + first + " 👋\nبخصوص طلب " + (ex ? "الاستبدال" : "الاسترجاع") + " لأوردرك *" + o.NoText + "* من *" + store + "*: مندوب " + co + " هييجي " + (ex ? "يسلّمك البديل وياخد القديم" : "ياخد المنتج") + (b.Collect > 0 ? "، وهتدفعله الفرق *" + ShopOrders.Money(b.Collect) + "*" : "") + ".\n\n📦 رقم التتبع: " + x.Tracking + (url == null ? "" : "\n🔗 تابعه من هنا: " + url) + "\n\nجهّز المنتج في كيسه أو علبته، وخلّي موبايلك جنبك 🙏";
        }

        // ---------- what the company says (Carriers.Happened passes the 'ret' rows here) ----------
        // ours: asked (registered), picked (the exchange's new piece left you), going (to the customer), took (the old piece is with the courier),
        // attempt, returning, returned, cancelled, lost, hold; "" = nothing for us
        static string Map(string company, string code, string kind, out bool loud, out string text)
        {
            loud = false; text = null; code = code ?? "";
            if (company == "bosta")
            {
                int st; int ex = 0; var p = code.Split(':'); int.TryParse(p[0], out st); if (p.Length > 1) int.TryParse(p[1], out ex);
                switch (st)
                {
                    case 10: case 11: case 20: return "asked";
                    case 21: return kind == "exchange" ? "picked" : "";
                    case 22: return "going";
                    case 23: return "took";
                    case 24: case 30: case 41: return "returning";
                    case 46: case 60: return "returned";
                    case 47: if (ex >= 20 && ex <= 30) { loud = true; return "hold"; } return "attempt";
                    case 48: case 49: return "cancelled";
                    case 100: case 101: text = st == 100 ? "بوسطة سجّلته مفقود" : "بوسطة سجّلته متكسر"; return "lost";
                    case 103: loud = true; text = "بوسطة مستنية قرارك"; return "hold";
                    case 102: case 105: return "hold";
                }
                return "";
            }
            // ShipBlu: a return's status, or an exchange's «return|delivery» statuses
            var two = code.Split('|'); string r = two[0], d = two.Length > 1 ? two[1] : "";
            switch (r)
            {
                case "PICKED_UP": return "took";
                case "IN_TRANSIT": case "EN_ROUTE": case "OUT_FOR_RETURN": return "returning";
                case "RETURNED": return "returned";
                case "RETURN_ATTEMPTED": return "attempt";
                case "RETURN_REFUSED": loud = true; text = "الزبون رفض يسلّم المرتجع"; return "hold";
                case "RETURN_POSTPONED": text = "الاستلام اتأجّل"; return "hold";
                case "CANCELLED": return "cancelled";
            }
            switch (d) { case "PICKED_UP": return "picked"; case "OUT_FOR_DELIVERY": return "going"; case "DELIVERED": return "took"; case "DELIVERY_ATTEMPTED": return "attempt"; case "CANCELLED": return "cancelled"; }
            return r == "CREATED" || r == "RETURN_REQUESTED" || r == "PICKUP_REQUESTED" || d == "CREATED" || d == "PICKUP_REQUESTED" ? "asked" : "";
        }
        public static void Happened(SqlConnection c, CarrierShip x, CarrierEvent e, string src)
        {
            var b = BackOf(c, x.Id); if (b == null) return; bool loud; string said; var ours = Map(x.Company, e.Code, b.Kind, out loud, out said); if (!string.IsNullOrEmpty(e.Text)) said = e.Text;
            if (!e.At.HasValue && (x.Status ?? "") == e.Code) return;   // asked again: nothing new
            var now = DateTime.UtcNow; var at = e.At ?? now; if (at > now.AddMinutes(10)) at = now; at = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            string t; Carriers.OursText.TryGetValue(ours, out t); if (!Carriers.Event(c, x.StoreId, x.Id, e.Code, ours, Cut((t ?? e.Code) + (string.IsNullOrEmpty(said) ? "" : ": " + said), 300), at, src)) return;
            if (x.State == "off" || x.State == "failed" || x.State == "done" || (x.LastEventAt.HasValue && at < x.LastEventAt.Value)) return;   // closed, or late: kept in the list only
            string to = ours == "" || ours == "hold" ? x.Ours : ours;
            // cancelled before the courier took anything: closed, so another pickup can be asked for
            bool early = x.Ours == null || x.Ours == "asked" || x.Ours == "made" || x.Ours == "going", end = to == "returned" || to == "lost" || to == "cancelled";
            Exec(c, "UPDATE dbo.Shipments SET Status=@st, Ours=@o, LastEventAt=@a, CheckedAt=SYSUTCDATETIME(), Attempts=Attempts+@n, ReasonText=CASE WHEN @txt IS NULL THEN ReasonText ELSE @txt END, State=CASE WHEN @end=1 THEN @fin ELSE State END, DoneAt=CASE WHEN @end=1 THEN SYSUTCDATETIME() ELSE DoneAt END, BackAt=CASE WHEN @o='returned' THEN @a ELSE BackAt END WHERE Id=@i",
                "@st", Cut(e.Code, 40), "@o", to, "@a", at, "@n", ours == "attempt" ? 1 : 0, "@txt", string.IsNullOrEmpty(said) ? null : Cut(said, 200), "@end", end ? 1 : 0, "@fin", to == "cancelled" && early ? "off" : "done", "@i", x.Id);
            if (to == x.Ours && !loud) return;
            var co = Carriers.Of(x.Company); string kind = Kinds(b.Kind), track = "\nرقم التتبع: " + x.Tracking, idem = "sb-ship-back-" + x.Id + "-";
            if (to != x.Ours && !string.IsNullOrEmpty(t)) ShopOrders.Log_(c, null, x.StoreId, b.OrderId, "carrier", Cut(kind + " (" + co.Name + "): " + t + (string.IsNullOrEmpty(said) ? "" : "، " + said), 290));
            if (to == "returned") ShopHooks.Tell(x.StoreId, "return", "📦 *" + kind + " أوردر " + x.OrderName.Split(' ')[0] + " رجعلك* (" + co.Name + ")" + track + "\n\nلما يبقى في إيدك افتح «المرتجعات» ودوس «وصلتني» عشان الكمية ترجع.", idem + "got");
            else if (ours == "attempt") ShopHooks.Tell(x.StoreId, "return", "🚚 *مندوب " + kind + " ماقدرش يستلم من الزبون*: أوردر " + x.OrderName.Split(' ')[0] + " (" + co.Name + ")" + (string.IsNullOrEmpty(said) ? "" : "\nالسبب: " + said) + track + "\n\nكلّم الزبون.", idem + "try-" + Regex.Replace(e.Code, @"\W", "_") + "-" + at.Ticks);
            else if (to == "cancelled") ShopHooks.Tell(x.StoreId, "return", "❌ *شحنة " + kind + " أوردر " + x.OrderName.Split(' ')[0] + " اتلغت عند " + co.Name + "*" + track + (early ? "\nتقدر تطلب مندوب تاني من «المرتجعات»." : ""), idem + "off");
            else if (to == "lost") ShopHooks.Tell(x.StoreId, "return", "⚠️ *" + kind + " أوردر " + x.OrderName.Split(' ')[0] + ": " + (said ?? "ضاع أو اتكسر") + "*\nكلّم " + co.Name + "." + track, idem + "lost");
            else if (loud) ShopHooks.Tell(x.StoreId, "return", "⚠️ *" + kind + " أوردر " + x.OrderName.Split(' ')[0] + " محتاجك* (" + co.Name + ")" + (string.IsNullOrEmpty(said) ? "" : "\n" + said) + track, idem + "hold-" + Regex.Replace(e.Code, @"\W", "_"));
        }

        // ---------- asking the companies (Carriers.Ask passes every list here first) ----------
        public static async Task<List<CarrierEvent>> Ask(Carrier co, CarrierLink k, List<CarrierShip> l)
        {
            var mine = l.Where(x => x.Source == "ret").ToList(); var rest = l.Where(x => x.Source != "ret").ToList();
            var o = rest.Count > 0 ? await co.Ask(k, rest) : new List<CarrierEvent>();
            foreach (var x in mine)
            {
                if (string.IsNullOrEmpty(x.Tracking)) continue; string kind; using (var c = Db.Open()) { var b = BackOf(c, x.Id); kind = b == null ? "return" : b.Kind; }
                CarrierEvent e = null; var bo = co as CarrierBosta; var sb = co as CarrierShipBlu;
                if (bo != null) e = await bo.AskBack(k, x); else if (sb != null) e = await sb.AskBack(k, x, kind == "exchange");
                if (e != null) { e.Tracking = x.Tracking; o.Add(e); }
            }
            return o;
        }
        static Func<Task> AskOne(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = Mine(c, s, b); var k = Need(c, s.Id, x.Company); if (!Web.Allow("ship-ask:" + x.Id, 6, TimeSpan.FromMinutes(5))) throw new ShopException("too_many", "لسه سائلين من شوية. استنى دقيقة.");
            return async () =>
            {
                try
                {
                    if (x.State == "unsure") { var m = await Find(k, x); using (var c2 = Db.Open()) if (m != null && !string.IsNullOrEmpty(m.Tracking)) { Carriers.Made(c2, x, m); await Finish(c2, x.StoreId, x.Id); } }
                    else { var got = await Ask(Carriers.Of(x.Company), k, new List<CarrierShip> { x }); using (var c2 = Db.Open()) foreach (var e in got) Carriers.Apply(c2, x.StoreId, x.Id, e, "ask"); }
                }
                catch (CarrierException ex) { if (ex.BadKey) using (var c2 = Db.Open()) Carriers.Fault(c2, k, ex.Message); throw new ShopException(ex.Code ?? "carrier", ex.Message); }
                using (var c2 = Db.Open()) ShopShip2.Ok(res, "ship", Json(c2, Carriers.ById(c2, s.Id, x.Id)));
            };
        }
        static Func<Task> CancelOne(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = Mine(c, s, b); if (x.State != "live") throw new ShopException("state", "الشحنة دي مش مفتوحة."); if (!(x.Ours == "asked" || x.Ours == "made")) throw new ShopException("picked", "المندوب اتحرّك خلاص، فالإلغاء من شركة الشحن نفسها.");
            var k = Need(c, s.Id, x.Company);
            return async () =>
            {
                try { await Carriers.Of(x.Company).Cancel(k, x); } catch (CarrierException ex) { throw new ShopException(ex.Code ?? "carrier", ex.Message); }
                using (var c2 = Db.Open())
                {
                    using (var cmd = Db.Cmd(c2, "UPDATE dbo.Shipments SET State='off', Ours='cancelled', DoneAt=SYSUTCDATETIME() WHERE Id=@i AND State='live'", "@i", x.Id)) if (cmd.ExecuteNonQuery() > 0) { Carriers.Event(c2, x.StoreId, x.Id, "off", "cancelled", "اتلغت عند " + Carriers.Of(x.Company).Name, DateTime.UtcNow, "own"); var bb = BackOf(c2, x.Id); if (bb != null) ShopOrders.Log_(c2, null, x.StoreId, bb.OrderId, "owner", "مندوب " + Kinds(bb.Kind) + " اتلغى عند " + Carriers.Of(x.Company).Name); }
                    ShopShip2.Ok(res, "ship", Json(c2, Carriers.ById(c2, s.Id, x.Id)));
                }
            };
        }
        static Func<Task> Label(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = Mine(c, s, b); if (string.IsNullOrEmpty(x.Tracking) || x.State == "off") throw new ShopException("none", "الشحنة دي مالهاش بوليصة."); var k = Need(c, s.Id, x.Company);
            if (!Web.Allow("ship-label:" + s.Id, 60, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", "طلبات كتير. استنى شوية.");
            return async () =>
            {
                byte[] pdf; try { pdf = await Carriers.Of(x.Company).Labels(k, new List<CarrierShip> { x }, k.S("label", "A4")); } catch (CarrierException ex) { throw new ShopException("labels", ex.Message); }
                using (var c2 = Db.Open()) Exec(c2, "UPDATE dbo.Shipments SET PrintedAt=SYSUTCDATETIME() WHERE Id=@i", "@i", x.Id);
                res.Cache.SetCacheability(HttpCacheability.NoCache); res.AddHeader("X-Content-Type-Options", "nosniff"); res.ContentType = "application/pdf"; res.AddHeader("Content-Disposition", "inline; filename=return-" + x.Id + ".pdf"); res.BinaryWrite(pdf);
            };
        }
        // «وصلتني»: the parcel is in the owner's hands: the request is «received», which brings the quantity back (unless it's damaged)
        public static void Got(SqlConnection c, StoreInfo s, Shop sh, CarrierShip x, Dictionary<string, object> b, HttpResponse res)
        {
            var bk = BackOf(c, x.Id); if (bk == null || !x.BackAt.HasValue) throw new ShopException("state", "المرتجع لسه مارجعش.");
            var q = ReqOf(c, s.Id, bk.ReturnId); bool moved = false;
            if (q != null && (q.Status == "new" || q.Status == "approved"))
            {
                var d = ShopShip2.D("id", q.Id, "st", "received", "restock", ShopJson.Bool(ShopJson.At(b, "restock"), true), "tell", false); if (ShopJson.At(b, "refund") != null) d["refund"] = ShopJson.At(b, "refund");
                try { ShopReturns.Do(c, s, sh, d); moved = true; } catch (ShopException ex) when (ex.Code == "busy" || ex.Code == "move") { }
            }
            Exec(c, "UPDATE dbo.Shipments SET GotAt=SYSUTCDATETIME() WHERE Id=@i AND GotAt IS NULL", "@i", x.Id);
            ShopShip2.Ok(res, "moved", moved, "ship", Json(c, Carriers.ById(c, s.Id, x.Id)));
        }

        // ---------- the background lane (Carriers.Run): the messages that waited for the morning, pickups found after no answer ----------
        public static async Task Run(CancellationToken ct)
        {
            var l = new List<KeyValuePair<int, long>>();
            using (var c = Db.Open())
            using (var cmd = Db.Cmd(c, "SELECT TOP 40 b.StoreId, b.ShipmentId FROM dbo.ShipBacks b JOIN dbo.Shipments x ON x.Id=b.ShipmentId WHERE x.State='live' AND ((b.MadeAt IS NULL AND x.Tracking IS NOT NULL) OR (b.ToldAt IS NULL AND b.TellDue IS NOT NULL AND b.TellDue<=SYSUTCDATETIME()))"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(new KeyValuePair<int, long>((int)r[0], Convert.ToInt64(r[1])));
            foreach (var kv in l)
            {
                if (ct.IsCancellationRequested || WaNotify.QuietUntil().HasValue) return;
                try { using (var c = Db.Open()) { var b = BackOf(c, kv.Value); if (b.MadeAt == null) await Finish(c, kv.Key, kv.Value); else await Tell(c, kv.Key, kv.Value); } } catch (Exception ex) { Log.Error(ex); }
            }
        }
    }

    // ---------- the companies' own calls for it ----------
    public partial class CarrierBosta
    {
        internal async Task<CarrierMade> CreateBack(CarrierLink k, CarrierParcel p, bool exchange)
        {
            var name = p.Name.Trim().Split(new[] { ' ' }, 2); var size = k.S("size", "SMALL"); bool bulky = size.EndsWith("Bulky", StringComparison.Ordinal); var place = PlaceOf(k, k.S("place"));
            var at = new Dictionary<string, object> { { "city", p.Area.GovEn ?? p.Area.Gov }, { "districtId", p.Area.AreaId }, { "firstLine", p.Address.Length < 6 ? p.Area.Label + "، " + p.Address : p.Address } };
            if (p.Area.CityId.Length > 0) at["zoneId"] = p.Area.CityId; if (!string.IsNullOrWhiteSpace(p.Landmark)) at["secondLine"] = p.Landmark;
            var who = new Dictionary<string, object> { { "firstName", name[0] }, { "lastName", name.Length > 1 ? name[1] : name[0] }, { "fullName", p.Name }, { "phone", p.Phone } }; if (p.Phone2 != null) who["secondPhone"] = p.Phone2;
            Func<string, Dictionary<string, object>> spec = t => new Dictionary<string, object> { { "packageType", bulky ? size : "Parcel" }, { "size", size }, { "packageDetails", new Dictionary<string, object> { { "itemsCount", p.Items }, { "description", t } } } };
            // a CRP collects from the customer at their address (pickupAddress); an exchange is delivered there (dropOffAddress) and brings the old piece back
            var b = new Dictionary<string, object> { { "type", exchange ? 30 : 25 }, { "cod", exchange ? p.Cod : 0 }, { "receiver", who }, { "returnSpecs", spec(p.Goods) }, { "businessReference", p.Ref }, { "uniqueBusinessReference", p.Ref } };
            if (exchange) { b["dropOffAddress"] = at; b["specs"] = spec(p.NewGoods ?? p.Goods); } else b["pickupAddress"] = at;
            if (p.Note.Length > 0) b["returnNotes"] = p.Note; if (place != null) b["businessLocationId"] = ShopJson.Str(ShopJson.At(place, "id"));
            if (p.HookUrl != null) { b["webhookUrl"] = p.HookUrl; b["webhookCustomHeaders"] = new Dictionary<string, object> { { "Authorization", "Bearer " + p.HookSecret } }; }
            var r = await Call(k, HttpMethod.Post, "/deliveries?apiVersion=1", b);
            return new CarrierMade { Tracking = ShopJson.Str(At(r, "data", "trackingNumber"), 60), Id = ShopJson.Str(At(r, "data", "_id"), 60) };
        }
        internal async Task<CarrierEvent> AskBack(CarrierLink k, CarrierShip x)
        {
            var r = await Call(k, HttpMethod.Get, "/deliveries/business/" + Uri.EscapeDataString(x.Tracking)); int st = ShopJson.Int(At(r, "data", "state", "code"), -1); if (st < 0) return null;
            return new CarrierEvent { Code = st.ToString(CultureInfo.InvariantCulture), Attempts = ShopJson.Int(At(r, "data", "deliveryAttemptsLength")) };
        }
    }
    public partial class CarrierShipBlu
    {
        Dictionary<string, object> Who(CarrierParcel p)
        {
            int zone; int.TryParse(p.Area.AreaId, out zone);
            var who = new Dictionary<string, object> { { "full_name", p.Name }, { "phone", p.Phone }, { "address", new Dictionary<string, object> { { "line_1", p.Address }, { "line_2", string.IsNullOrWhiteSpace(p.Landmark) ? p.Area.Area : p.Landmark }, { "zone", zone } } } };
            if (p.Phone2 != null) who["secondary_phone"] = p.Phone2; if (p.Email.Length > 0) who["email"] = p.Email; return who;
        }
        internal async Task<CarrierMade> CreateBack(CarrierLink k, CarrierParcel p, bool exchange, string theirOrder)
        {
            int size; if (!int.TryParse(k.S("size", "1"), out size) || size < 1 || size > 9) size = 1;
            Func<string, object[]> pk = t => new object[] { new Dictionary<string, object> { { "package_size", size }, { "description", t }, { "fragile", false } } };
            if (!exchange)
            {
                var b = new Dictionary<string, object> { { "customer", Who(p) }, { "packages", pk(p.Goods) }, { "cash_amount", 0 }, { "merchant_order_reference", p.Ref } }; if (p.Note.Length > 0) b["order_notes"] = p.Note;
                var r = await Call(k, HttpMethod.Post, "/returns/", b); return new CarrierMade { Tracking = T(r.Body, "tracking_number"), Id = T(r.Body, "id") };
            }
            int order; int.TryParse(theirOrder ?? "", out order); if (order <= 0) throw new CarrierException("الاستبدال مع ShipBlu بيتعمل بس على أوردر اتسلّم معاهم.", "kind");
            var e = new Dictionary<string, object> { { "order", order }, { "old_packages", pk(p.Goods) }, { "new_packages", pk(p.NewGoods ?? p.Goods) }, { "cash_amount", p.Cod }, { "refund", false }, { "refund_amount", 0 } }; if (p.Note.Length > 0) e["notes"] = p.Note;
            var x = await Call(k, HttpMethod.Post, "/exchanges/", e); string id = T(x.Body, "id"), t = ShopJson.Str(ShopJson.At(x.Body, "delivery_order", "tracking_number"), 60);
            if (t.Length == 0 && id.Length > 0) t = ShopJson.Str(ShopJson.At((await Call(k, HttpMethod.Get, "/exchanges/" + Uri.EscapeDataString(id) + "/")).Body, "delivery_order", "tracking_number"), 60);
            return new CarrierMade { Tracking = t, Id = id };
        }
        // a return by our reference in the newest ones; an exchange has no reference of ours, so it can't be looked up
        internal async Task<CarrierMade> FindBack(CarrierLink k, string reference, bool exchange)
        {
            if (exchange) throw new CarrierException("ShipBlu مابتسمحش ندوّر على الاستبدال. دوّر عليه في حسابك عندهم.", "nofind");
            for (int page = 0; page < 3; page++)
            {
                var l = Rows(await Call(k, HttpMethod.Get, "/returns/?limit=100&offset=" + page * 100));
                var y = l.FirstOrDefault(o => T(o, "merchant_order_reference") == reference && T(o, "status") != "CANCELLED" && T(o, "tracking_number").Length > 0); if (y != null) return new CarrierMade { Tracking = T(y, "tracking_number"), Id = T(y, "id") };
                if (l.Count < 100) break;
            }
            return null;
        }
        internal async Task<CarrierEvent> AskBack(CarrierLink k, CarrierShip x, bool exchange)
        {
            if (string.IsNullOrEmpty(x.TheirId)) return null;
            if (!exchange) { var r = await Call(k, HttpMethod.Get, "/returns/" + Uri.EscapeDataString(x.TheirId) + "/"); var st = T(r.Body, "status", 40); return st.Length == 0 ? null : new CarrierEvent { Code = st }; }
            var e = await Call(k, HttpMethod.Get, "/exchanges/" + Uri.EscapeDataString(x.TheirId) + "/");
            string rs = ShopJson.Str(ShopJson.At(e.Body, "return_order", "status"), 40), ds = ShopJson.Str(ShopJson.At(e.Body, "delivery_order", "status"), 40);
            return rs.Length == 0 && ds.Length == 0 ? null : new CarrierEvent { Code = rs + "|" + ds };
        }
    }
}
