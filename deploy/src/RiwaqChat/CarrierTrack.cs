// What happens to a shipment after it's sent: the company says something (its notification, or when we ask), the order moves with it
// (ShopOrders.SetStatus, so the WhatsApp messages, «زبايني» and the store's numbers follow by themselves), and the owner hears what needs them.
// A return never gives the quantity back by itself: the owner presses «وصلتني» when the parcel is in their hands (Carriers.Got).
// «إنقاذ الأوردر»: a failed attempt sends the customer one WhatsApp message that fits the reason, never at night, one for an attempt; when they
// answer, the owner is told. Events come twice and out of order: each is kept once (ShipmentEvents), and one older than the last moves nothing.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static partial class Carriers
    {
        // ---------- one shipment at a time (two notifications of one parcel arrive together) ----------
        static bool Lock(SqlConnection c, long id) { using (var cmd = Db.Cmd(c, "DECLARE @r INT; EXEC @r = sp_getapplock @Resource=@n, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=12000; SELECT @r", "@n", "ship:" + id)) return Convert.ToInt32(cmd.ExecuteScalar()) >= 0; }
        static void Unlock(SqlConnection c, long id) { try { Exec(c, "EXEC sp_releaseapplock @Resource=@n, @LockOwner='Session'", "@n", "ship:" + id); } catch (Exception ex) { Log.Error(ex); } }

        // src: hook (the company told us) / ask (we asked). false = busy, tell the company to try again.
        public static bool Apply(SqlConnection c, int storeId, long shipId, CarrierEvent e, string src)
        {
            if (!Lock(c, shipId)) return false;
            try { Happened(c, storeId, shipId, e, src); return true; } finally { Unlock(c, shipId); }
        }
        static string Say(CarrierEvent e) { string t; return !OursText.TryGetValue(e.Ours, out t) ? e.Text : string.IsNullOrEmpty(e.Text) ? t : t + ": " + e.Text; }
        static bool SameState(CarrierShip x, CarrierEvent e) { var st = x.Status ?? ""; return e.Code == st || st.StartsWith(e.Code + ":", StringComparison.Ordinal); }

        static void Happened(SqlConnection c, int storeId, long shipId, CarrierEvent e, string src)
        {
            var x = ById(c, storeId, shipId); if (x != null && x.Source == "ret") { ShopShipBack.Happened(c, x, e, src); return; }   // a return pickup (ShopShipBack) never moves the order
            if (x == null || x.Source != "shop") return; long oid = long.Parse(x.OrderId, CultureInfo.InvariantCulture);
            // the notification came before our own answer was saved
            if (string.IsNullOrEmpty(x.Tracking) && !string.IsNullOrEmpty(e.Tracking) && (x.State == "making" || x.State == "unsure")) { Made(c, x, new CarrierMade { Tracking = e.Tracking, Id = e.TheirId }); x = ById(c, storeId, shipId); }
            // what asking gives has no time of its own: only a change counts
            if (!e.At.HasValue && SameState(x, e) && !(e.Ours == "attempt" && e.Attempts > x.Attempts)) return;
            if (!e.At.HasValue && e.Ours == "attempt" && e.Attempts > 0 && e.Attempts <= x.Attempts) return;
            if (!e.At.HasValue && e.Ours == "made" && !x.Picked) { Exec(c, "UPDATE dbo.Shipments SET Status=@st WHERE Id=@i", "@st", Cut(e.Code, 40), "@i", x.Id); return; }
            var now = DateTime.UtcNow; var at = e.At ?? now; if (at > now.AddMinutes(10)) at = now; at = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            if (!Event(c, storeId, x.Id, e.Code, e.Ours, Say(e), at, src)) return;
            if (x.State == "off" || x.State == "failed") return;                         // cancelled by us: kept in the list, moves nothing
            if (x.LastEventAt.HasValue && at < x.LastEventAt.Value) return;              // late
            if (x.State == "done")
            {
                // delivered, and then it comes back: the owner decides what that means for the order
                if (x.Ours == "delivered" && (e.Ours == "returning" || e.Ours == "returned")) ShopHooks.Tell(storeId, "ship", "↩️ *" + Of(x.Company).Name + " بتقول إن أوردر " + x.OrderName + " راجع بعد ما اتسلّم*\nراجعه معاهم. رقم التتبع: " + x.Tracking, "sb-ship-odd-" + x.Id);
                Exec(c, "UPDATE dbo.Shipments SET LastEventAt=@a, Status=@st WHERE Id=@i", "@a", at, "@st", Cut(e.Code, 40), "@i", x.Id); return;
            }
            string ours = e.Ours; if (ours == "" || ours == "hold" || (ours == "transit" && (x.Ours == "out" || x.Ours == "attempt" || x.Ours == "returning")) || (ours == "out" && e.Blind && x.Ours == "returning")) ours = x.Ours ?? "made";
            int tries = e.Ours == "attempt" ? Math.Max(x.Attempts, e.Attempts > 0 ? e.Attempts : x.Attempts + 1) : Math.Max(x.Attempts, e.Attempts); bool again = e.Ours == "attempt" && tries > x.Rescued, moves = ours != x.Ours;
            bool end = ours == "delivered" || ours == "returned" || ours == "lost" || (ours == "cancelled" && x.Picked);
            Exec(c, "UPDATE dbo.Shipments SET Status=@st, Ours=@o, LastEventAt=@a, Attempts=@n, Reason=CASE WHEN @att=1 THEN @rs ELSE Reason END, ReasonText=CASE WHEN @att=1 OR @txt IS NOT NULL THEN @txt ELSE ReasonText END, CheckedAt=SYSUTCDATETIME(), State=CASE WHEN @end=1 THEN 'done' ELSE State END, DoneAt=CASE WHEN @end=1 THEN SYSUTCDATETIME() ELSE DoneAt END, RescueDue=CASE WHEN @keep=1 THEN RescueDue ELSE NULL END, BackAt=CASE WHEN @o='returned' THEN @a ELSE BackAt END WHERE Id=@i",
                "@st", Cut(e.Code, 40), "@o", ours, "@a", at, "@n", tries, "@att", e.Ours == "attempt" ? 1 : 0, "@rs", Nz(Cut(e.Reason, 12)), "@txt", Nz(Cut(e.Text, 200)), "@end", end ? 1 : 0, "@keep", ours == "attempt" ? 1 : 0, "@i", x.Id);
            if (e.Cod.HasValue && ours == "delivered") ShopShipCash.Collected(c, x, e.Cod.Value);
            var co = Of(x.Company); string why = string.IsNullOrEmpty(e.Text) ? "" : "\nالسبب: " + e.Text, track = "\nرقم التتبع: " + x.Tracking, idem = "sb-ship-" + x.Id + "-";
            var o = ShopOrders.ById(c, storeId, oid); if (o == null) return;

            string to = Next(ours, o.Status);
            if (to != null && moves)
            {
                string no = null; for (int i = 0; i < 3; i++) { no = ShopOrders.SetStatus(c, storeId, oid, to, "carrier", to == "returned" ? "رجع من " + co.Name + (string.IsNullOrEmpty(x.ReasonText) ? "" : " (" + x.ReasonText + ")") : null, false); if (no == null) break; o = ShopOrders.ById(c, storeId, oid); to = Next(ours, o.Status); if (to == null) break; }
                if (no != null && to != null) ShopOrders.Log_(c, null, storeId, oid, "system", Cut(co.Name + " قالت «" + OursText[ours] + "» والأوردر ماتحرّكش: " + no, 290));
            }
            else if (moves && (ours == "delivered" || ours == "out") && (o.Status == "cancelled" || o.Status == "returned"))
                ShopHooks.Tell(storeId, "ship", "⚠️ *" + co.Name + ": أوردر " + o.NoText + " " + OursText[ours] + "، وهو عندك «" + ShopOrders.StatusText(o.Status) + "»*\nراجعه." + track, idem + "odd-" + ours);

            if (again) { HostingEnvironment.QueueBackgroundWorkItem(ct => Failed(storeId, shipId, e.Reason ?? "", e.Text, tries)); }
            else if (moves && ours == "returning") ShopHooks.Tell(storeId, "ship", "↩️ *أوردر " + o.NoText + " راجع ليك* (" + co.Name + ")\n" + o.Name + " · " + ShopOrders.Local(o.Phone) + why + track, idem + "back");
            else if (moves && ours == "returned") ShopHooks.Tell(storeId, "ship", "📦 *أوردر " + o.NoText + " رجع* (" + co.Name + ")\n" + o.Name + why + track + "\n\nلما يبقى في إيدك افتح الأوردر ودوس «وصلتني» عشان الكمية ترجع.", idem + "got");
            else if (moves && ours == "cancelled")
            {
                if (!x.Picked) Off(c, x, "الشحنة اتلغت عند " + co.Name, "carrier");
                ShopHooks.Tell(storeId, "ship", "❌ *شحنة أوردر " + o.NoText + " اتلغت عند " + co.Name + "*" + why + track + (x.Picked ? "" : "\nتقدر تبعته تاني."), idem + "off");
            }
            else if (moves && ours == "lost") ShopHooks.Tell(storeId, "ship", "⚠️ *أوردر " + o.NoText + ": " + (e.Text ?? "ضاع أو اتكسر") + "*\nكلّم " + co.Name + "." + track, idem + "lost");
            else if (e.Ours == "hold" && e.Loud) ShopHooks.Tell(storeId, "ship", "⚠️ *أوردر " + o.NoText + " محتاجك* (" + co.Name + ")" + why + track, idem + "hold-" + Regex.Replace(e.Code, @"\W", "_"));
        }
        // where an event takes the order, or null (never backwards, and never out of cancelled / returned)
        static string Next(string ours, string st)
        {
            bool early = st == "new" || st == "noreply" || st == "confirmed";
            switch (ours)
            {
                case "picked": case "transit": return early ? "shipped" : null;
                case "out": return early || st == "shipped" ? "out" : null;
                case "attempt": return early || st == "shipped" ? "out" : null;   // its «out for delivery» never reached us
                case "delivered": return early || st == "shipped" || st == "out" ? "delivered" : null;
                case "returned": return early || st == "shipped" || st == "out" ? "returned" : null;
                default: return null;
            }
        }

        // ---------- «إنقاذ الأوردر» ----------
        // which message fits: where (the address), when (another day), why (refused); null = nothing to ask the customer
        static string Kind(string reason)
        {
            switch (reason) { case "away": case "address": case "phone": return "where"; case "noanswer": case "later": return "when"; case "refused": case "open": return "why"; case "sender": case "zone": case "weather": case "doubt": case "me": return null; default: return "any"; }
        }
        static bool CanRescue(string state, string ours, string order) { return state == "live" && ours == "attempt" && (order == "shipped" || order == "out" || order == "confirmed"); }

        static async Task Failed(int storeId, long shipId, string reason, string said, int tries)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId); var x = ById(c, storeId, shipId); if (s == null || x == null) return; var o = ShopOrders.ById(c, storeId, long.Parse(x.OrderId, CultureInfo.InvariantCulture)); if (o == null) return; var co = Of(x.Company); var k = Link(c, storeId, x.Company);
                    string kind = Kind(reason), did; var night = WaNotify.QuietUntil();
                    if (kind == null) did = "مابعتناش حاجة للزبون.";
                    else if (!On(c, s) || k != null && !k.B("rescue", true)) did = "رسالة الإنقاذ متوقفة، فكلّمه إنت.";
                    else if (night.HasValue) { Exec(c, "UPDATE dbo.Shipments SET RescueDue=@d WHERE Id=@i AND Rescued<@n", "@d", night.Value, "@i", x.Id, "@n", tries); did = "هنبعت للزبون الصبح، ولو ردّ هنقولك."; }
                    else { var no = await Rescue(c, s, x, o, tries); did = no == "" ? null : no == null ? "بعتنا للزبون على الواتساب، ولو ردّ هنقولك." : "ماقدرناش نبعتله (" + no + ")، فكلّمه إنت."; }
                    if (did == null) return;
                    ShopHooks.Tell(storeId, "ship", "🚚 *محاولة تسليم فشلت: أوردر " + o.NoText + "* (" + co.Name + "، المحاولة " + tries + ")\n" + o.Name + " · " + ShopOrders.Local(o.Phone) + " · " + ShopOrders.Money(x.Cod) + (string.IsNullOrEmpty(said) ? "" : "\nالسبب: *" + said + "*") + (reason == "refused" ? "\nالشحنة غالبًا هترجعلك لو ماغيّرش رأيه." : "") + "\n" + did, "sb-ship-" + x.Id + "-try-" + tries);
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        // null = sent, "" = another run has it, otherwise why not. One for an attempt, whoever gets here first.
        static async Task<string> Rescue(SqlConnection c, StoreInfo s, CarrierShip x, ShopOrder o, int tries)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shipments SET Rescued=@n, RescueDue=NULL WHERE Id=@i AND Rescued<@n AND State='live' AND Ours='attempt'", "@n", tries, "@i", x.Id)) if (cmd.ExecuteNonQuery() == 0) return "";
            x = ById(c, s.Id, x.Id); if (!CanRescue(x.State, x.Ours, o.Status)) return ""; string why = null; var sh = Shops.ByStore(c, s.Id);
            if (sh == null) why = "المتجر مش موجود"; else if (Billing.Expired(s)) why = "الباقة خلصت"; else if (!Web.Allow("ship-rescue:" + s.Id, 200, TimeSpan.FromHours(1))) why = "رسايل كتير في ساعة واحدة"; else why = WaNotify.CantSend(c, s);
            if (why == null) why = await WaNotify.ToCustomer(c, s, o.Phone, RescueText(sh, o, Of(x.Company).Name, Kind(x.Reason ?? "") ?? "any", x.Reason ?? ""), "sb-ship-rescue-" + x.Id + "-" + tries);
            if (why == null) { Exec(c, "UPDATE dbo.Shipments SET RescuedAt=SYSUTCDATETIME(), AnsweredAt=NULL WHERE Id=@i", "@i", x.Id); ShopOrders.Log_(c, null, s.Id, o.Id, "system", "اتبعتله رسالة إنقاذ بعد المحاولة " + tries); }
            else ShopOrders.Log_(c, null, s.Id, o.Id, "system", Cut("رسالة الإنقاذ ماتبعتتش: " + why, 290));
            return why;
        }
        static string RescueText(Shop sh, ShopOrder o, string company, string kind, string reason)
        {
            bool en = o.Lang == "en"; string first = (o.Name ?? "").Trim().Split(' ')[0], store = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name, addr = Cut(ShopOrders.AddressText(o), 220);
            if (en)
            {
                string h = "Hello " + first + " 👋\nAbout your order *" + o.NoText + "* from *" + store + "*: ";
                switch (kind)
                {
                    case "where": return h + "the courier tried to deliver it and couldn't find you at the address.\n\n📍 " + addr + "\n\nIs this right? If it needs a correction, a landmark or another number, just reply here and the courier will come again 🙏";
                    case "when": return h + (reason == "later" ? "the delivery was postponed." : "the courier called and couldn't reach you.") + "\n\nWhen would you like it? Reply with the day and the time, and keep your phone near you 🙏";
                    case "why": return h + "the courier says it wasn't accepted.\n\nIf something is wrong with the product, the price or the delivery, tell us here and we'll try to fix it before it goes back 🙏";
                    default: return h + "the courier tried to deliver it and couldn't.\n\nWhen would you like it? If the address needs a correction, send it here 🙏\n📍 " + addr;
                }
            }
            string a = "أهلًا " + first + " 👋\nبخصوص أوردرك *" + o.NoText + "* من *" + store + "*: ";
            switch (kind)
            {
                case "where": return a + "مندوب " + company + " حاول يوصّله ومالقاكش في العنوان.\n\n📍 " + addr + "\n\nالعنوان ده صح؟ لو محتاج تعديل أو علامة مميزة أو رقم تاني نكلّمك عليه، ابعته هنا والمندوب يجيلك تاني 🙏";
                case "when": return a + (reason == "later" ? "التسليم اتأجّل." : "مندوب " + company + " كلّمك ومقدرش يوصلك.") + "\n\nتحب يجيلك امتى؟ اكتب اليوم والوقت المناسب، وخلّي موبايلك جنبك 🙏";
                case "why": return a + (reason == "open" ? "المندوب بيقول إنك عايز تفتح الشحنة قبل ما تستلم." : "المندوب بلّغنا إن الاستلام اترفض.") + "\n\nلو فيه حاجة مش مظبوطة في المنتج أو السعر أو التوصيل، قولّنا هنا وهنحاول نحلّها قبل ما يرجع 🙏";
                default: return a + "مندوب " + company + " حاول يوصّله ومقدرش.\n\nتحب يجيلك امتى؟ ولو العنوان محتاج تعديل ابعته هنا 🙏\n📍 " + addr;
            }
        }
        // the messages that waited for the morning
        static async Task RescueDue(CancellationToken ct)
        {
            if (WaNotify.QuietUntil().HasValue) return;
            List<CarrierShip> l; using (var c = Db.Open()) l = Ships(c, 40, "RescueDue IS NOT NULL AND RescueDue<=SYSUTCDATETIME() ORDER BY RescueDue");
            foreach (var x in l)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using (var c = Db.Open())
                    {
                        var s = Db.StoreById(c, x.StoreId); var o = s == null ? null : ShopOrders.ById(c, x.StoreId, long.Parse(x.OrderId, CultureInfo.InvariantCulture));
                        if (o == null || !On(c, s) || !CanRescue(x.State, x.Ours, o.Status)) { Exec(c, "UPDATE dbo.Shipments SET RescueDue=NULL WHERE Id=@i", "@i", x.Id); continue; }
                        var no = await Rescue(c, s, x, o, x.Attempts); if (!string.IsNullOrEmpty(no)) ShopHooks.Tell(x.StoreId, "ship", "🚚 أوردر " + x.OrderName + ": ماقدرناش نبعت للزبون رسالة الإنقاذ (" + no + ")، فكلّمه إنت.", "sb-ship-" + x.Id + "-mno-" + x.Attempts);
                    }
                }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        // ---------- the customer answers the rescue message (every WhatsApp message passes here, WhatsAppBot.Handle) ----------
        const int AnswerDays = 3;
        public static void Answered(SqlConnection c, StoreInfo s, string chatKey, string chat, string text)
        {
            if (!Shops.Ready || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                if (Num(c, "SELECT COUNT(*) FROM dbo.Shipments WHERE StoreId=@s AND RescuedAt>DATEADD(DAY,-@d,SYSUTCDATETIME()) AND AnsweredAt IS NULL", "@s", s.Id, "@d", AnswerDays) == 0) return;
                int sid = s.Id; var phone = WaChat.Digits(chatKey); if (phone != null) { Take(c, sid, phone, text); return; }
                // WhatsApp hid the number behind an id: asked for only when a message is waiting for an answer
                HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { using (var c2 = Db.Open()) { var s2 = Db.StoreById(c2, sid); var token = s2 == null ? null : WaAccount.Token(c2, s2); var n = token == null || string.IsNullOrEmpty(s2.WaInstance) ? null : await Wapilot.LidPhone(token, s2.WaInstance, chat); if (n != null) Take(c2, sid, n, text); } } catch (Exception ex) { Log.Error(ex); } });
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        static void Take(SqlConnection c, int sid, string phone, string text)
        {
            long id; using (var cmd = Db.Cmd(c, "WITH x AS (SELECT TOP 1 * FROM dbo.Shipments WHERE StoreId=@s AND RescuedAt>DATEADD(DAY,-@d,SYSUTCDATETIME()) AND AnsweredAt IS NULL AND RIGHT(Phone,9)=@t ORDER BY RescuedAt DESC) UPDATE x SET AnsweredAt=SYSUTCDATETIME() OUTPUT INSERTED.Id", "@s", sid, "@d", AnswerDays, "@t", phone.Length > 9 ? phone.Substring(phone.Length - 9) : phone)) { var v = cmd.ExecuteScalar(); if (v == null) return; id = Convert.ToInt64(v); }
            var x = ById(c, sid, id); if (x == null) return; text = Cut(Regex.Replace(text, @"\s+", " ").Trim(), 300); long oid = long.Parse(x.OrderId, CultureInfo.InvariantCulture);
            ShopOrders.Log_(c, null, sid, oid, "customer", Cut("ردّ على رسالة الإنقاذ: " + text, 290)); Event(c, sid, id, "answer:" + x.Rescued, "", "الزبون ردّ: " + text, DateTime.UtcNow, "own");
            ShopHooks.Tell(sid, "ship", "💬 *الزبون ردّ على رسالة الإنقاذ: أوردر " + x.OrderName + "* (" + Of(x.Company).Name + ")\n«" + text + "»\n\n👈 كلّمه: https://wa.me/" + phone + "\nرقم التتبع: " + x.Tracking, "sb-ship-" + id + "-ans-" + x.Rescued);
        }
        // for the assistant, when that customer goes on writing
        public static string Note(SqlConnection c, int storeId, string chatKey)
        {
            var phone = WaChat.Digits(chatKey); if (phone == null || !Shops.Ready) return "";
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 OrderName FROM dbo.Shipments WHERE StoreId=@s AND RescuedAt>DATEADD(DAY,-@d,SYSUTCDATETIME()) AND State='live' AND RIGHT(Phone,9)=@t ORDER BY RescuedAt DESC", "@s", storeId, "@d", AnswerDays, "@t", phone.Length > 9 ? phone.Substring(phone.Length - 9) : phone))
            {
                var n = cmd.ExecuteScalar() as string;
                return n == null ? "" : "\n- المندوب ماقدرش يسلّم الزبون ده طلبه " + n + "، واتبعتله رسالة بتسأله عن العنوان أو الميعاد أو سبب الرفض. ردّه وصل لصاحب المتجر. اشكره، وخد منه أي تفاصيل ناقصة (العنوان، اليوم، رقم تاني)، وقوله إن المتجر هيبلّغ شركة الشحن. ماتوعدش بميعاد، وماتقولش إن العنوان اتغيّر أو الطلب اتلغى.";
            }
        }

        // ---------- asking the companies ----------
        // «حدّث الحالة»
        static Func<Task> AskNow(SqlConnection c, StoreInfo s, Dictionary<string, object> b, HttpResponse res)
        {
            var x = ById(c, s.Id, Int(b, "id")); if (x == null) throw new ShopException("gone", "الشحنة دي مش موجودة."); var k = Need(c, s.Id, x.Company); if (!Web.Allow("ship-ask:" + x.Id, 6, TimeSpan.FromMinutes(5))) throw new ShopException("too_many", "لسه سائلين من شوية. استنى دقيقة.");
            return async () =>
            {
                try { if (x.State == "unsure") await Sure(k, x); else await Ask(k, new List<CarrierShip> { x }); } catch (CarrierException ex) { if (ex.BadKey) using (var c2 = Db.Open()) Fault(c2, k, ex.Message); throw Said(ex); }
                using (var c2 = Db.Open()) Ok(res, "ship", ShipJson(c2, ById(c2, s.Id, x.Id), true));
            };
        }
        static async Task Ask(CarrierLink k, List<CarrierShip> l)
        {
            var got = await ShopShipBack.Ask(Of(k.Company), k, l); var by = l.GroupBy(x => x.Tracking ?? "").ToDictionary(g => g.Key, g => g.First());
            using (var c = Db.Open()) { Exec(c, "UPDATE dbo.Shipments SET CheckedAt=SYSUTCDATETIME() WHERE Id IN (" + string.Join(",", l.Select(x => x.Id)) + ")"); foreach (var e in got) { CarrierShip x; if (by.TryGetValue(e.Tracking ?? "", out x)) Apply(c, x.StoreId, x.Id, e, "ask"); } }
        }
        // a press that got no answer: is it there?
        static async Task Sure(CarrierLink k, CarrierShip x)
        {
            var m = await (x.Source == "ret" ? ShopShipBack.Find(k, x) : Of(x.Company).Find(k, x.Ref));
            using (var c = Db.Open())
            {
                if (m != null) { if (!string.IsNullOrEmpty(m.Tracking)) Made(c, x, m); return; }
                // surely not there (and long enough after the press): the order is free to be sent again
                if (DateTime.UtcNow - x.ClaimedAt > TimeSpan.FromMinutes(5)) Exec(c, "UPDATE dbo.Shipments SET State='failed', Error=@e WHERE Id=@i AND State='unsure'", "@e", "الشحنة ماتسجّلتش عند " + Of(x.Company).Name + ". ابعتها تاني.", "@i", x.Id);
            }
        }

        // the background lane (Jobs): areas once a week, the rescues that waited for the morning, presses without an answer, the open shipments
        public static async Task Run(CancellationToken ct)
        {
            if (!Shops.Ready) return; var until = DateTime.UtcNow.AddMinutes(4);
            foreach (var co in All) { try { await CarrierAreas.Refresh(co, false, ct); } catch (Exception ex) { Log.Error(ex); } if (ct.IsCancellationRequested) return; }
            try { await RescueDue(ct); } catch (Exception ex) { Log.Error(ex); }
            try { await ShopShipBack.Run(ct); } catch (Exception ex) { Log.Error(ex); }
            try { await Unsure(ct, until); } catch (Exception ex) { Log.Error(ex); }
            foreach (var co in All) try { await Poll(co, ct, until); } catch (Exception ex) { Log.Error(ex); }
        }
        static async Task Unsure(CancellationToken ct, DateTime until)
        {
            List<CarrierShip> l; using (var c = Db.Open()) l = Ships(c, 40, "(State='unsure' OR (State='making' AND ClaimedAt<DATEADD(MINUTE,-5,SYSUTCDATETIME()))) AND ClaimedAt<DATEADD(SECOND,-60,SYSUTCDATETIME()) AND ClaimedAt>DATEADD(DAY,-3,SYSUTCDATETIME()) ORDER BY ISNULL(CheckedAt,'2000-01-01')");
            foreach (var x in l)
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow > until) return; CarrierLink k;
                using (var c = Db.Open()) { Exec(c, "UPDATE dbo.Shipments SET CheckedAt=SYSUTCDATETIME(), State=CASE WHEN State='making' THEN 'unsure' ELSE State END WHERE Id=@i", "@i", x.Id); k = Link(c, x.StoreId, x.Company); if (k == null || string.IsNullOrEmpty(k.Key)) continue; }
                try { await Sure(k, x); } catch (CarrierException) { } catch (Exception ex) { Log.Error(ex); }
            }
        }
        static async Task Poll(Carrier co, CancellationToken ct, DateTime until)
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < until)
            {
                var l = new List<CarrierShip>();
                using (var c = Db.Open())
                {
                    // claimed here, so two worker processes don't ask twice; a store whose key broke waits an hour
                    var ids = new List<long>();
                    using (var cmd = Db.Cmd(c, @"WITH x AS (SELECT TOP 60 s.* FROM dbo.Shipments s JOIN dbo.Carriers k ON k.StoreId=s.StoreId AND k.Company=s.Company WHERE s.State='live' AND s.Company=@c AND s.CreatedAt>DATEADD(DAY,-60,SYSUTCDATETIME())
                        AND (s.CheckedAt IS NULL OR s.CheckedAt<DATEADD(MINUTE,-@m,SYSUTCDATETIME())) AND (s.LastEventAt IS NULL OR s.LastEventAt<DATEADD(MINUTE,-@m,SYSUTCDATETIME()) OR @m<=60) AND (k.LastErrorAt IS NULL OR k.LastErrorAt<DATEADD(HOUR,-1,SYSUTCDATETIME())) ORDER BY ISNULL(s.CheckedAt,'2000-01-01'))
                        UPDATE x SET CheckedAt=SYSUTCDATETIME() OUTPUT INSERTED.Id", "@c", co.Id, "@m", co.PollMinutes)) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Convert.ToInt64(r[0]));
                    if (ids.Count > 0) l = Ships(c, 0, "Id IN (" + string.Join(",", ids) + ")");
                }
                if (l.Count == 0) return;
                foreach (var g in l.GroupBy(x => x.StoreId))
                {
                    CarrierLink k; using (var c = Db.Open()) k = Link(c, g.Key, co.Id); if (k == null || string.IsNullOrEmpty(k.Key)) continue;
                    foreach (var part in g.Select((x, i) => new { x, i }).GroupBy(y => y.i / co.PollBatch).Select(y => y.Select(z => z.x).ToList()))
                    {
                        if (ct.IsCancellationRequested || DateTime.UtcNow > until) return;
                        try { await Ask(k, part); } catch (CarrierException ex) { if (ex.BadKey) { using (var c = Db.Open()) Fault(c, k, ex.Message); break; } if (ex.Down || ex.Code == "busy") return; } catch (Exception ex) { Log.Error(ex); }
                    }
                }
                if (l.Count < 60) return;
            }
        }
    }
}
