// «كروت هدايا» (ShopRew.cs has the hooks). A card is bought on the store's /gift page: an order of its own (Channel 'gift', one line that
// is no product) paid by a transfer or a wallet whose picture the owner approves, never cash. Only then does the card exist (OnPaid: the one
// function whatever marks an order paid calls). Its code is 12 letters and digits with no look-alikes, sent on WhatsApp to the person it's
// for at the time the buyer chose (never at night), then the buyer hears it arrived. At checkout it's a payment: it pays the smaller of its
// balance and what is left of the order, and the rest stays on it; a cancelled or returned order gives back what it took. Every movement
// is a row of ShopGiftMoves.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public class ShopGiftCard
    {
        public int Id, StoreId, Sends; public long? OrderId; public string Code, Status, Kind, BuyerName, BuyerPhone, ToName, ToPhone, Msg, Lang, SendErr, Note;
        public decimal Amount, Balance; public DateTime? ExpiresAt, SendAt, SentAt; public DateTime CreatedAt;
        public bool Expired(DateTime now) { return ExpiresAt.HasValue && ExpiresAt.Value <= now; }
    }

    public static class ShopGift
    {
        public const string Channel = "gift";
        const string Abc = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // no I, L, O, 0, 1
        static Dictionary<string, object> Set(Shop sh) { return ShopJson.Sub(sh.Settings, "gcard"); }
        public static bool On(Shop sh) { return sh != null && ShopJson.Bool(ShopJson.At(Set(sh), "on")); }
        static decimal Clamp(decimal v, decimal a, decimal b) { return Math.Max(a, Math.Min(b, v)); }
        static string M(decimal v) { return ShopOrders.Money(v); }
        static string En(decimal v) { return "EGP " + v.ToString("#,0.##", CultureInfo.InvariantCulture); }

        // ---------- the owner's settings ----------
        public static Dictionary<string, object> Clean(Dictionary<string, object> v)
        {
            var amounts = v.ContainsKey("amounts") ? ShopJson.List(ShopJson.At(v, "amounts")).Select(a => Math.Round(ShopJson.Dec(a), 0)).Where(a => a >= 10 && a <= 100000).Distinct().OrderBy(a => a).Take(8).ToList() : new List<decimal> { 250, 500, 1000 };
            decimal min = Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "min"), 100), 0), 10, 100000), max = Clamp(Math.Round(ShopJson.Dec(ShopJson.At(v, "max"), 5000), 0), min, 100000);
            return ShopRew.D("on", ShopJson.Bool(ShopJson.At(v, "on")), "amounts", amounts, "free", ShopJson.Bool(ShopJson.At(v, "free")), "min", min, "max", max, "months", Math.Max(0, Math.Min(60, ShopJson.Int(ShopJson.At(v, "months"), 12))));
        }
        public static object PublicSet(Shop sh) { var s = Clean(Set(sh)); s.Remove("on"); s["pay"] = ShopPrice.PayWays(sh).Count > 0; return s; }
        static int Months(Shop sh) { return (int)Clean(Set(sh))["months"]; }

        // ---------- codes ----------
        public static string NewCode(int n = 12)
        {
            var sb = new StringBuilder(n); var b = new byte[16];
            using (var g = RandomNumberGenerator.Create()) while (sb.Length < n) { g.GetBytes(b); foreach (var x in b) if (x < 248 && sb.Length < n) sb.Append(Abc[x % 31]); }   // 248 = 8 × 31: every letter as likely
            return sb.ToString();
        }
        public static string Show(string code) { return string.IsNullOrEmpty(code) ? "" : Regex.Replace(code, "(.{4})(?!$)", "$1-"); }
        public static string Mask(string code) { return string.IsNullOrEmpty(code) || code.Length < 4 ? "" : "••••" + code.Substring(code.Length - 4); }

        const string Cols = "Id, StoreId, Code, Amount, Balance, ExpiresAt, Status, Kind, OrderId, BuyerName, BuyerPhone, ToName, ToPhone, Msg, Lang, SendAt, SentAt, Sends, SendErr, Note, CreatedAt";
        static DateTime? Dt(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }
        static ShopGiftCard Read(IDataRecord r)
        {
            return new ShopGiftCard { Id = (int)r["Id"], StoreId = (int)r["StoreId"], Code = (string)r["Code"], Amount = (decimal)r["Amount"], Balance = (decimal)r["Balance"], ExpiresAt = Dt(r["ExpiresAt"]), Status = (string)r["Status"], Kind = (string)r["Kind"],
                OrderId = r["OrderId"] is long ? (long?)(long)r["OrderId"] : null, BuyerName = r["BuyerName"] as string, BuyerPhone = r["BuyerPhone"] as string, ToName = r["ToName"] as string, ToPhone = r["ToPhone"] as string, Msg = r["Msg"] as string, Lang = ((string)r["Lang"]).Trim(),
                SendAt = Dt(r["SendAt"]), SentAt = Dt(r["SentAt"]), Sends = (int)r["Sends"], SendErr = r["SendErr"] as string, Note = r["Note"] as string, CreatedAt = Dt(r["CreatedAt"]).Value };
        }
        static ShopGiftCard One(SqlConnection c, SqlTransaction tx, string where, params object[] a) { using (var cmd = ShopRew.Cmd(c, tx, "SELECT TOP 1 " + Cols + " FROM dbo.ShopGiftCards " + where, a)) using (var r = cmd.ExecuteReader()) return r.Read() ? Read(r) : null; }
        public static ShopGiftCard Find(SqlConnection c, int storeId, string code) { return code == null || code.Length != 12 ? null : One(c, null, "WHERE StoreId=@s AND Code=@c", "@s", storeId, "@c", code); }
        static void Move(SqlConnection c, SqlTransaction tx, int storeId, int cardId, long? orderId, string kind, decimal delta, decimal after, string who, string note)
        {
            ShopRew.Exec(c, tx, "INSERT INTO dbo.ShopGiftMoves(StoreId, CardId, OrderId, Kind, Delta, After_, Who, Note) VALUES(@s, @c, @o, @k, @d, @a, @w, @n)", "@s", storeId, "@c", cardId, "@o", orderId.HasValue ? (object)orderId.Value : null, "@k", kind, "@d", delta, "@a", after, "@w", who, "@n", note);
        }

        // guessing codes at checkout: a few wrong ones from one place, then a quarter of an hour of nothing
        static readonly ConcurrentDictionary<string, DateTime> Blocked = new ConcurrentDictionary<string, DateTime>();
        static bool Barred(string key, DateTime now) { DateTime t; return Blocked.TryGetValue(key, out t) && t > now; }
        static void Missed(string key, DateTime now) { if (!Web.Allow("shop-gcx:" + key, 8, TimeSpan.FromMinutes(15))) Blocked[key] = now.AddMinutes(15); if (Blocked.Count > 5000) foreach (var k in Blocked.Where(x => x.Value <= now).Select(x => x.Key).ToList()) { DateTime t; Blocked.TryRemove(k, out t); } }

        // ---------- the cart's sum: a payment, after the total ----------
        public static void Quote(SqlConnection c, ShopData d, ShopQuote q, ShopRewIn w, ShopRewQ r, DateTime now)
        {
            var sh = d.Row; string key = sh.StoreId + ":" + (w.Ip ?? "");
            if (!On(sh)) { r.CardErr = "bad"; return; }
            if (Barred(key, now)) { r.CardErr = "many"; return; }
            var g = Find(c, sh.StoreId, w.Card);
            if (g == null) { Missed(key, now); r.CardErr = "bad"; return; }
            if (g.Status != "active") { r.CardErr = "stop"; return; }
            if (g.Expired(now)) { r.CardErr = "exp"; return; }
            if (g.Balance <= 0) { r.CardErr = "zero"; return; }
            r.CardId = g.Id; r.CardCode = g.Code; r.CardUse = Math.Min(g.Balance, q.Total); r.CardLeft = g.Balance - r.CardUse; r.CardUntil = g.ExpiresAt;
        }

        // ---------- the order takes it (inside the order's transaction) ----------
        public static object TakeUse(SqlConnection c, SqlTransaction tx, int storeId, long orderId, ShopRewQ r, bool en)
        {
            var left = ShopRew.One(c, tx, "UPDATE dbo.ShopGiftCards SET Balance=Balance-@a OUTPUT INSERTED.Balance WHERE Id=@i AND StoreId=@s AND Status='active' AND Balance>=@a AND (ExpiresAt IS NULL OR ExpiresAt>SYSUTCDATETIME())", "@a", r.CardUse, "@i", r.CardId, "@s", storeId);
            if (left == null) throw new ShopException("card", en ? "Your gift card's balance changed a moment ago. Check the total and confirm again." : "رصيد كارت الهدية اتغيّر حالًا. راجع الإجمالي وأكّد تاني.");
            Move(c, tx, storeId, r.CardId, orderId, "use", -r.CardUse, (decimal)left, "customer", null);
            ShopRew.Exec(c, tx, "UPDATE dbo.ShopOrders SET Paid=Paid+@a WHERE Id=@o AND StoreId=@s", "@a", r.CardUse, "@o", orderId, "@s", storeId);
            ShopOrders.Log_(c, tx, storeId, orderId, "customer", "اتدفع " + M(r.CardUse) + " بكارت هدية " + Mask(r.CardCode));
            return ShopRew.D("id", r.CardId, "c", Mask(r.CardCode), "a", r.CardUse);
        }
        // the order moved: it holds what it used while it's alive, and nothing once cancelled or returned (null = fine)
        public static string Settle(SqlConnection c, SqlTransaction tx, ShopOrder o, bool live, Dictionary<string, object> gc)
        {
            int id = ShopJson.Int(ShopJson.At(gc, "id")); decimal a = ShopJson.Dec(ShopJson.At(gc, "a")); if (id <= 0 || a <= 0) return null;
            if (ShopRew.One(c, tx, "SELECT Balance FROM dbo.ShopGiftCards WITH (UPDLOCK, ROWLOCK) WHERE Id=@i AND StoreId=@s", "@i", id, "@s", o.StoreId) == null) return null;
            decimal now = ShopRew.Dec(c, tx, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopGiftMoves WHERE CardId=@i AND OrderId=@o AND Kind IN ('use','back')", "@i", id, "@o", o.Id), diff = (live ? -a : 0) - now;
            if (diff > 0)
            {
                var after = (decimal)ShopRew.One(c, tx, "UPDATE dbo.ShopGiftCards SET Balance=Balance+@d OUTPUT INSERTED.Balance WHERE Id=@i", "@d", diff, "@i", id);
                Move(c, tx, o.StoreId, id, o.Id, "back", diff, after, "system", "الأوردر " + o.NoText + (o.Status == "returned" ? " رجع" : " اتلغى"));
                ShopRew.Exec(c, tx, "UPDATE dbo.ShopOrders SET Paid=CASE WHEN Paid-@d>0 THEN Paid-@d ELSE 0 END WHERE Id=@o", "@d", diff, "@o", o.Id);
                ShopOrders.Log_(c, tx, o.StoreId, o.Id, "system", "رجع " + M(diff) + " لكارت الهدية " + ShopJson.Str(ShopJson.At(gc, "c")));
            }
            else if (diff < 0)
            {
                var after = ShopRew.One(c, tx, "UPDATE dbo.ShopGiftCards SET Balance=Balance-@d OUTPUT INSERTED.Balance WHERE Id=@i AND Balance>=@d", "@d", -diff, "@i", id);
                if (after == null) return "رصيد كارت الهدية اللي دفع الأوردر ده اتصرف في حاجة تانية، فماينفعش يرجع يشتغل.";
                Move(c, tx, o.StoreId, id, o.Id, "use", diff, (decimal)after, "system", "الأوردر " + o.NoText + " رجع يشتغل");
                ShopRew.Exec(c, tx, "UPDATE dbo.ShopOrders SET Paid=CASE WHEN Paid-@d>Total THEN Total ELSE Paid-@d END WHERE Id=@o", "@d", diff, "@o", o.Id);
            }
            return null;
        }
        // a card's own order was cancelled or returned: the card stops (its balance kept, so bringing the order back starts it again)
        public static void SettleOrder(SqlConnection c, SqlTransaction tx, ShopOrder o, bool live)
        {
            var g = One(c, tx, "WITH (UPDLOCK, ROWLOCK) WHERE OrderId=@o AND StoreId=@s", "@o", o.Id, "@s", o.StoreId); if (g == null) return;
            if (!live && g.Status == "active")
            {
                ShopRew.Exec(c, tx, "UPDATE dbo.ShopGiftCards SET Status='stopped' WHERE Id=@i", "@i", g.Id);
                Move(c, tx, o.StoreId, g.Id, o.Id, "stop", 0, g.Balance, "system", "أوردر الكارت " + o.NoText + (o.Status == "returned" ? " رجع" : " اتلغى"));
            }
            else if (live && g.Status == "stopped" && ShopRew.One(c, tx, "SELECT TOP 1 Kind FROM dbo.ShopGiftMoves WHERE CardId=@i AND OrderId=@o ORDER BY Id DESC", "@i", g.Id, "@o", o.Id) as string == "stop")
            {
                ShopRew.Exec(c, tx, "UPDATE dbo.ShopGiftCards SET Status='active' WHERE Id=@i", "@i", g.Id);
                Move(c, tx, o.StoreId, g.Id, o.Id, "start", 0, g.Balance, "system", "أوردر الكارت رجع يشتغل");
            }
        }

        // ---------- the card's order is paid: the card exists ----------
        // Called wherever an order becomes paid (today: the owner approves the transfer, ShopPay.Review → ShopRew.Reviewed). Once per order.
        public static void OnPaid(SqlConnection c, int storeId, long orderId)
        {
            if (!Shops.Ready) return;
            var o = ShopOrders.ById(c, storeId, orderId);
            if (o == null || o.Channel != Channel || o.PayState != "paid" || o.Paid < o.Total || o.Total <= 0 || o.Status == "cancelled" || o.Status == "returned") return;
            var sh = Shops.ByStore(c, storeId); if (sh == null) return;
            var g = ShopJson.Sub(o.Extras, "gcard"); int months = Months(sh); var now = DateTime.UtcNow;
            long at = ShopJson.Int(ShopJson.At(g, "at")); DateTime sendAt = at > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(at) : now; if (sendAt < now) sendAt = now;
            string toName = ShopJson.Str(ShopJson.At(g, "to"), 80), toPhone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(g, "tp"), 24)); int id = 0; string code = null;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    if (ShopRew.Num(c, tx, "SELECT COUNT(*) FROM dbo.ShopGiftCards WITH (UPDLOCK, HOLDLOCK) WHERE OrderId=@o", "@o", o.Id) > 0) { tx.Rollback(); return; }
                    for (int i = 0; i < 5 && id == 0; i++)
                    {
                        code = NewCode(); if (ShopRew.Num(c, tx, "SELECT COUNT(*) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Code=@c", "@s", storeId, "@c", code) > 0) continue;
                        id = (int)ShopRew.One(c, tx, @"INSERT INTO dbo.ShopGiftCards(StoreId, Code, Amount, Balance, ExpiresAt, Kind, OrderId, BuyerName, BuyerPhone, ToName, ToPhone, Msg, Lang, SendAt)
                            OUTPUT INSERTED.Id VALUES(@s, @c, @a, @a, @e, 'sold', @o, @bn, @bp, @tn, @tp, @m, @l, @at)", "@s", storeId, "@c", code, "@a", o.Subtotal, "@e", months > 0 ? (object)now.AddMonths(months) : null, "@o", o.Id,
                            "@bn", o.Name, "@bp", o.Phone.Length == 0 ? null : o.Phone, "@tn", toName.Length == 0 ? null : toName, "@tp", toPhone, "@m", ShopJson.Str(ShopJson.At(g, "msg"), 300).Length == 0 ? null : ShopJson.Str(ShopJson.At(g, "msg"), 300), "@l", o.Lang == "en" ? "en" : "ar", "@at", sendAt);
                    }
                    if (id == 0) throw new Exception("gift card: no free code");
                    Move(c, tx, storeId, id, o.Id, "issue", o.Subtotal, o.Subtotal, "system", null);
                    ShopOrders.Log_(c, tx, storeId, o.Id, "system", "كارت الهدية اتعمل " + Mask(code) + (toPhone == null ? "" : "، وهيتبعت لـ " + toName + (sendAt > now.AddMinutes(5) ? " يوم " + ShopRew.Day(sendAt) : " دلوقتي")));
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            // the card is the thing that was sold: the order is done
            ShopOrders.SetStatus(c, storeId, o.Id, "delivered", "system");
        }

        // ---------- the /gift page: buying one ----------
        public static void Buy(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            var sh = x.Row; Func<string, string, string> t = (ar, en) => ShopRew.T(x, ar, en);
            if (!On(sh)) throw new ShopException("off", t("كروت الهدايا مش متاحة في المتجر ده.", "Gift cards aren't available in this store."));
            if (x.Preview) throw new ShopException("preview", t("المعاينة مابتحفظش حاجة.", "The preview saves nothing."));
            var ways = ShopPrice.PayWays(sh); if (ways.Count == 0) throw new ShopException("nopay", t("المتجر لسه ماحددش طريقة تحويل، فمش هينفع تشتري كارت دلوقتي.", "The store has no way to receive a transfer yet, so a card can't be bought now."));
            if (ShopPrice.Vacation(x.Data, x.Now)) throw new ShopException("vacation", t("المتجر واخد إجازة ومش بيستقبل طلبات دلوقتي.", "The store is on a break and isn't taking orders."));
            var set = Clean(Set(sh)); decimal amount = Math.Round(ShopJson.Dec(ShopJson.At(b, "amount")), 0);
            bool okAmt = ((List<decimal>)set["amounts"]).Contains(amount) || ((bool)set["free"] && amount >= (decimal)set["min"] && amount <= (decimal)set["max"]);
            string name = ShopRew.Clip(ShopJson.Str(ShopJson.At(b, "name"), 100), 80), phone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24)), toName = ShopRew.Clip(ShopJson.Str(ShopJson.At(b, "toName"), 100), 80), toPhone = ShopOrders.Mobile(ShopJson.Str(ShopJson.At(b, "toPhone"), 24)), msg = ShopRew.Clip(ShopJson.Str(ShopJson.At(b, "msg"), 400), 300);
            var bad = new Dictionary<string, object>();
            if (!okAmt) bad["amount"] = t("اختار مبلغ الكارت", "Choose the card's amount");
            if (name.Length < 2) bad["name"] = t("اكتب اسمك", "Enter your name"); if (phone == null) bad["phone"] = t("اكتب رقم موبايل مصري صح", "Enter a valid Egyptian mobile number");
            if (toName.Length < 2) bad["toName"] = t("اكتب اسم اللي هيوصله الكارت", "Enter the name of the person it's for"); if (toPhone == null) bad["toPhone"] = t("اكتب رقم واتساب مصري صح", "Enter a valid Egyptian WhatsApp number");
            // when: now (as soon as the transfer is approved) or a day, at 10 in the morning Cairo time
            string when = ShopJson.Str(ShopJson.At(b, "when"), 10); DateTime? sendAt = null; DateTime day;
            if (when.Length > 0 && when != "now")
            {
                var today = ShopRew.Local(x.Now).Date;
                if (!DateTime.TryParseExact(when, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) || day < today || day > today.AddDays(90)) bad["when"] = t("اختار يوم من النهارده لحد 3 شهور", "Choose a day within the next 3 months");
                else sendAt = ShopRew.Utc(day.AddHours(10));
            }
            if (!ShopJson.Bool(ShopJson.At(b, "agree"))) bad["agree"] = t("وافق على الشروط الأول", "Please agree first");
            if (bad.Count > 0) throw new ShopException("fields", t("راجع البيانات المكتوبة بالأحمر.", "Check the fields in red."), bad);
            string pay = ShopJson.Str(ShopJson.At(b, "pay"), 6); if (!ways.Any(w => ShopJson.Str(ShopJson.At(w, "k")) == pay)) pay = ShopJson.Str(ShopJson.At(ways[0], "k"));
            string key = Regex.Replace(ShopJson.Str(ShopJson.At(b, "ck"), 60), "[^A-Za-z0-9-]", ""); if (key.Length > 40) key = key.Substring(0, 40); if (key.Length < 8) key = null; else key = "gc-" + key.Substring(0, Math.Min(37, key.Length));
            if (key != null) { var had = ShopRew.One(c, null, "SELECT TOP 1 Id FROM dbo.ShopOrders WHERE StoreId=@s AND ClientKey=@k", "@s", sh.StoreId, "@k", key); if (had != null) { var o0 = ShopOrders.ById(c, sh.StoreId, Convert.ToInt64(had)); ShopRew.Ok(res, "no", o0.No, "token", o0.Token, "dep", o0.Deposit, "again", true); return; } }
            if (!Web.Allow("shop-gcbuy:" + x.Ip, 6, TimeSpan.FromHours(1)) || !Web.Allow("shop-gcbuys:" + sh.StoreId, 100, TimeSpan.FromHours(24))) throw new ShopException("too_many", t("طلبت كروت كتير في وقت قصير. استنى شوية.", "Too many cards in a short time. Please wait a while."));

            var ex = ShopRew.D("gcard", ShopRew.D("to", toName, "tp", toPhone, "msg", msg, "at", sendAt.HasValue ? ShopView.Unix(sendAt.Value) : 0));
            string forWho = "لـ " + toName + " · " + (sendAt.HasValue ? "يتبعت يوم " + ShopRew.Day(sendAt.Value) : "يتبعت أول ما التحويل يوصل");
            long id; int no; string token = Shops.Token(16);
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    using (var cmd = ShopRew.Cmd(c, tx, "UPDATE dbo.Shops SET OrderNo=OrderNo+1 OUTPUT INSERTED.OrderNo WHERE StoreId=@s", "@s", sh.StoreId)) no = (int)cmd.ExecuteScalar();
                    if (key != null && ShopRew.One(c, tx, "SELECT TOP 1 Id FROM dbo.ShopOrders WHERE StoreId=@s AND ClientKey=@k", "@s", sh.StoreId, "@k", key) != null) { tx.Rollback(); throw new ShopException("again", t("الطلب ده اتسجّل بالفعل.", "This order is already saved.")); }
                    id = Convert.ToInt64(ShopRew.One(c, tx, @"INSERT INTO dbo.ShopOrders(StoreId, No, Token, ClientKey, Channel, Lang, Name, Phone, Gov, Address, Subtotal, Discount, Ship, Total, Pay, Fees, Extras, PayState, Deposit, Ip, Device, Src, RateAskedAt)
                        OUTPUT INSERTED.Id VALUES(@s, @no, @t, @k, @ch, @l, @n, @p, 0, N'كارت هدية', @a, 0, 0, @a, @pay, 0, @ex, 'wait', @a, @ip, @dev, @src, SYSUTCDATETIME())",
                        "@s", sh.StoreId, "@no", no, "@t", token, "@k", key, "@ch", Channel, "@l", x.Lang == "en" ? "en" : "ar", "@n", name, "@p", phone, "@a", amount, "@pay", pay, "@ex", ShopJson.Write(ex), "@ip", x.Ip,
                        "@dev", Regex.Replace(ShopJson.Str(ShopJson.At(b, "dev"), 40), "[^A-Za-z0-9-]", "") is string dv && dv.Length > 0 ? dv : null, "@src", ShopJson.Str(ShopJson.At(b, "src"), 200).Length == 0 ? null : ShopJson.Str(ShopJson.At(b, "src"), 200)));
                    ShopRew.Exec(c, tx, "INSERT INTO dbo.ShopOrderItems(StoreId, OrderId, ProductId, VariantId, Name, Opts, Qty, Price, Full_, Discount, Extra) VALUES(@s, @o, 0, 0, N'كارت هدية', @op, 1, @a, @a, 0, @x)", "@s", sh.StoreId, "@o", id, "@op", M(amount), "@a", amount, "@x", forWho);
                    ShopOrders.Log_(c, tx, sh.StoreId, id, "customer", "اتطلب كارت هدية بـ " + M(amount) + " " + forWho);
                    tx.Commit();
                }
                catch (SqlException e) when (e.Number == 2601 || e.Number == 2627) { try { tx.Rollback(); } catch { } throw new ShopException("again", t("الطلب ده اتسجّل بالفعل.", "This order is already saved.")); }
                catch (ShopException) { throw; }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            ShopHooks.Tell(sh.StoreId, "gcard", "🎁 *حد طلب كارت هدية* #" + no + "\n" + name + " · " + ShopOrders.Local(phone) + " · " + M(amount) + "\n" + forWho + "\n\nالكارت بيطلع بعد ما توافق على صورة التحويل من «متجري» ثم «الطلبات».", "sb-shop-gcn-" + id);
            ShopRew.Ok(res, "no", no, "token", token, "dep", amount, "again", false);
        }

        // ---------- «رصيد كارتك» on the page, and the assistant with the code (the same limits) ----------
        static bool Limit(int storeId, string ip) { return Web.Allow("shop-gcb:" + storeId + ":" + (ip ?? ""), 6, TimeSpan.FromMinutes(10)) && Web.Allow("shop-gcbs:" + storeId, 300, TimeSpan.FromHours(1)); }
        public static void Balance(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            if (!On(x.Row)) throw new ShopException("off", ShopRew.T(x, "كروت الهدايا مش متاحة في المتجر ده.", "Gift cards aren't available in this store."));
            if (!Limit(x.Row.StoreId, x.Ip)) throw new ShopException("too_many", ShopRew.T(x, "دوّرت كتير. استنى ربع ساعة.", "Too many tries. Please wait a quarter of an hour."));
            var g = Find(c, x.Row.StoreId, ShopRew.Code(ShopJson.Str(ShopJson.At(b, "code"), 40))); var now = DateTime.UtcNow;
            if (g == null) throw new ShopException("none", ShopRew.T(x, "مفيش كارت بالكود ده. اتأكد منه.", "There's no card with this code. Check it."));
            ShopRew.Ok(res, "bal", g.Balance, "amount", g.Amount, "until", g.ExpiresAt.HasValue ? ShopView.Unix(g.ExpiresAt.Value) : 0, "st", g.Status != "active" ? "stop" : g.Expired(now) ? "exp" : "ok", "code", Mask(g.Code));
        }
        static string BotBalance(SqlConnection c, Shop sh, string code, string ip)
        {
            if (!Limit(sh.StoreId, ip)) return "الزبون دوّر كتير. قوله يستنى ربع ساعة ويجرّب تاني.";
            var g = Find(c, sh.StoreId, ShopRew.Code(code)); var now = DateTime.UtcNow;
            if (g == null) return "مفيش كارت بالكود ده. اطلب منه يتأكد من الكود (12 حرف ورقم).";
            if (g.Status != "active") return "الكارت ده موقوف من المتجر. ينصحه يكلّم المتجر.";
            if (g.Expired(now)) return "الكارت ده صلاحيته خلصت يوم " + ShopRew.Day(g.ExpiresAt.Value) + ".";
            return "رصيد الكارت " + M(g.Balance) + " من " + M(g.Amount) + (g.ExpiresAt.HasValue ? "، وصالح لحد " + ShopRew.Day(g.ExpiresAt.Value) : "") + ". بيستخدمه وهو بيأكد الطلب في خانة «كارت هدية».";
        }
        public static string BotText(Shop sh)
        {
            var s = Clean(Set(sh)); var l = (List<decimal>)s["amounts"]; int mo = (int)s["months"];
            return "كروت هدايا: الزبون يشتري كارت من " + sh.Url + "/gift" + (l.Count > 0 ? " بـ " + string.Join(" أو ", l.Select(M)) : "") + ((bool)s["free"] ? (l.Count > 0 ? "، أو" : "") + " أي مبلغ من " + M((decimal)s["min"]) + " لـ " + M((decimal)s["max"]) : "") + "، بيدفعه تحويل قبل ما الكارت يطلع، والكارت بيتبعت على واتساب اللي هيوصله في الميعاد اللي اختاره" + (mo > 0 ? "، وصالح " + mo + " شهر" : "") + ". بيتدفع بيه وهو بيأكد الطلب في خانة «كارت هدية». رصيد كارت: من أداة store_gift_card بالكود اللي الزبون كتبه بس.";
        }
        public static void Tool(List<ChatHandler.ExtraTool> list, int storeId, string ip, bool preview)
        {
            using (var c = Db.Open()) { var s = Db.StoreById(c, storeId); var sh = ShopBot.For(c, s, preview); if (sh == null || !On(sh)) return; }
            list.Add(new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = "store_gift_card",
                    Description = "رصيد كارت هدية من المتجر ده وصلاحيته، بالكود اللي الزبون كتبه (12 حرف ورقم). ماتخمّنش كود، ولو مفيش كود اطلبه منه.",
                    InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement> { ["code"] = JsonSerializer.SerializeToElement(new { type = "string", description = "كود الكارت زي ABCD-EFGH-JKMN" }) }, Required = new List<string> { "code" } }
                },
                Run = tu =>
                {
                    JsonElement v; string code = tu.Input != null && tu.Input.TryGetValue("code", out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                    using (var c = Db.Open()) { var s = Db.StoreById(c, storeId); var sh = ShopBot.For(c, s, preview); return Task.FromResult(sh == null || !On(sh) ? "كروت الهدايا مش متاحة دلوقتي." : BotBalance(c, sh, code, ip ?? "chat")); }
                },
                Prompt = null
            });
        }

        // ---------- the owner ----------
        static Dictionary<string, object> Row(ShopGiftCard g, int no, DateTime now)
        {
            return ShopRew.D("id", g.Id, "code", Show(g.Code), "amount", g.Amount, "bal", g.Balance, "until", g.ExpiresAt.HasValue ? g.ExpiresAt.Value.ToString("o") : "", "exp", g.Expired(now), "st", g.Status, "kind", g.Kind, "no", no, "order", g.OrderId ?? 0,
                "to", g.ToName ?? "", "toPhone", g.ToPhone == null ? "" : ShopOrders.Local(g.ToPhone), "buyer", g.BuyerName ?? "", "buyerPhone", g.BuyerPhone == null ? "" : ShopOrders.Local(g.BuyerPhone), "msg", g.Msg ?? "", "note", g.Note ?? "",
                "sendAt", g.SendAt.HasValue ? g.SendAt.Value.ToString("o") : "", "sentAt", g.SentAt.HasValue ? g.SentAt.Value.ToString("o") : "", "err", g.SendErr ?? "", "at", g.CreatedAt.ToString("o"));
        }
        public static object Owner(SqlConnection c, Shop sh, string q)
        {
            var now = DateTime.UtcNow; var l = new List<object>(); string code = ShopRew.Code(q), ph = Regex.Replace(ShopJson.Digits(q ?? ""), @"\D", "").TrimStart('0');
            using (var cmd = Db.Cmd(c, "SELECT TOP 200 g." + Cols.Replace(", ", ", g.") + ", ISNULL(o.No,0) AS No FROM dbo.ShopGiftCards g LEFT JOIN dbo.ShopOrders o ON o.Id=g.OrderId WHERE g.StoreId=@s AND (@q='' OR g.Code=@c OR (@p<>'' AND (g.ToPhone LIKE '%'+@p+'%' OR g.BuyerPhone LIKE '%'+@p+'%')) OR g.ToName LIKE '%'+@q+'%' OR g.BuyerName LIKE '%'+@q+'%') ORDER BY g.Id DESC",
                "@s", sh.StoreId, "@q", (q ?? "").Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]"), "@c", code, "@p", ph.Length >= 4 ? ph : ""))
            using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(Row(Read(r), (int)r["No"], now));
            object[] a = { "@s", sh.StoreId };
            return ShopRew.D("list", l,
                "sold", ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Kind='sold'", a), "soldEgp", ShopRew.Dec(c, null, "SELECT ISNULL(SUM(Amount),0) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Kind='sold'", a),
                "hand", ShopRew.Dec(c, null, "SELECT ISNULL(SUM(Amount),0) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Kind='hand'", a),
                "used", -ShopRew.Dec(c, null, "SELECT ISNULL(SUM(Delta),0) FROM dbo.ShopGiftMoves WHERE StoreId=@s AND Kind IN ('use','back')", a),
                "out", ShopRew.Dec(c, null, "SELECT ISNULL(SUM(Balance),0) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Status='active' AND (ExpiresAt IS NULL OR ExpiresAt>SYSUTCDATETIME())", a),
                "waiting", ShopRew.Num(c, null, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Channel='gift' AND Status IN ('new','noreply','confirmed') AND ISNULL(PayState,'')<>'paid'", a));
        }
        public static object OwnerCard(SqlConnection c, Shop sh, int id)
        {
            var g = One(c, null, "WHERE Id=@i AND StoreId=@s", "@i", id, "@s", sh.StoreId); if (g == null) return null;
            var moves = new List<object>(); var now = DateTime.UtcNow;
            using (var cmd = Db.Cmd(c, "SELECT m.Kind, m.Delta, m.After_, m.Who, m.Note, m.At, m.OrderId, o.No FROM dbo.ShopGiftMoves m LEFT JOIN dbo.ShopOrders o ON o.Id=m.OrderId WHERE m.CardId=@i AND m.StoreId=@s ORDER BY m.Id DESC", "@i", id, "@s", sh.StoreId)) using (var r = cmd.ExecuteReader())
                while (r.Read()) moves.Add(ShopRew.D("k", (string)r[0], "d", (decimal)r[1], "a", (decimal)r[2], "who", (string)r[3], "note", r[4] as string ?? "", "at", DateTime.SpecifyKind((DateTime)r[5], DateTimeKind.Utc).ToString("o"), "order", r[6] is long ? r[6] : 0, "no", r[7] is int ? r[7] : 0));
            var d = Row(g, ShopRew.Num(c, null, "SELECT No FROM dbo.ShopOrders WHERE Id=@o", "@o", g.OrderId ?? 0), now); d["moves"] = moves; d["text"] = Text(sh, g);
            return d;
        }
        // compensation, a prize: a card the owner gives with no order (with a reason), sent now when it has a number
        public static int Issue(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b)
        {
            decimal amount = Math.Round(ShopJson.Dec(ShopJson.At(b, "amount")), 0); if (amount < 10 || amount > 100000) throw new ShopException("amount", "اكتب مبلغ الكارت من 10 لـ 100,000 جنيه.");
            string toName = ShopRew.Clip(ShopJson.Str(ShopJson.At(b, "toName"), 100), 80), raw = ShopJson.Str(ShopJson.At(b, "toPhone"), 24), toPhone = raw.Trim().Length == 0 ? null : ShopOrders.Mobile(raw), note = ShopRew.Clip(ShopJson.Str(ShopJson.At(b, "note"), 250), 200), msg = ShopRew.Clip(ShopJson.Str(ShopJson.At(b, "msg"), 400), 300);
            if (raw.Trim().Length > 0 && toPhone == null) throw new ShopException("phone", "رقم الواتساب مش صح.");
            if (toPhone != null && toName.Length < 2) throw new ShopException("name", "اكتب اسم اللي هيوصله الكارت.");
            if (note.Length < 3) throw new ShopException("note", "اكتب السبب (مثلًا: تعويض عن أوردر اتأخر).");
            if (!Web.Allow("shop-gchand:" + sh.StoreId, 60, TimeSpan.FromHours(24))) throw new ShopException("many", "عملت كروت كتير النهارده. كمّل بكرة.");
            int months = b.ContainsKey("months") ? Math.Max(0, Math.Min(60, ShopJson.Int(ShopJson.At(b, "months")))) : Months(sh); var now = DateTime.UtcNow; int id = 0;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    for (int i = 0; i < 5 && id == 0; i++)
                    {
                        var code = NewCode(); if (ShopRew.Num(c, tx, "SELECT COUNT(*) FROM dbo.ShopGiftCards WHERE StoreId=@s AND Code=@c", "@s", sh.StoreId, "@c", code) > 0) continue;
                        id = (int)ShopRew.One(c, tx, "INSERT INTO dbo.ShopGiftCards(StoreId, Code, Amount, Balance, ExpiresAt, Kind, ToName, ToPhone, Msg, Lang, SendAt, Note) OUTPUT INSERTED.Id VALUES(@s, @c, @a, @a, @e, 'hand', @tn, @tp, @m, 'ar', @at, @n)",
                            "@s", sh.StoreId, "@c", code, "@a", amount, "@e", months > 0 ? (object)now.AddMonths(months) : null, "@tn", toName.Length == 0 ? null : toName, "@tp", toPhone, "@m", msg.Length == 0 ? null : msg, "@at", toPhone == null ? null : (object)now, "@n", note);
                    }
                    if (id == 0) throw new Exception("gift card: no free code");
                    Move(c, tx, sh.StoreId, id, null, "hand", amount, amount, "owner", note);
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            return id;
        }
        public static void Stop(SqlConnection c, Shop sh, int id, bool stop)
        {
            var g = One(c, null, "WHERE Id=@i AND StoreId=@s", "@i", id, "@s", sh.StoreId); if (g == null) throw new ShopException("gone", "الكارت ده مش موجود.");
            if (ShopRew.Exec(c, null, "UPDATE dbo.ShopGiftCards SET Status=@t WHERE Id=@i AND StoreId=@s AND Status<>@t", "@t", stop ? "stopped" : "active", "@i", id, "@s", sh.StoreId) == 1)
                Move(c, null, sh.StoreId, id, null, stop ? "stop" : "start", 0, g.Balance, "owner", stop ? "وقّفته" : "شغّلته تاني");
        }
        public static void Resend(SqlConnection c, StoreInfo s, Shop sh, int id)
        {
            var g = One(c, null, "WHERE Id=@i AND StoreId=@s", "@i", id, "@s", sh.StoreId); if (g == null) throw new ShopException("gone", "الكارت ده مش موجود.");
            if (g.ToPhone == null) throw new ShopException("phone", "الكارت ده مالوش رقم يتبعتله.");
            if (g.Status != "active") throw new ShopException("stop", "الكارت ده موقوف. شغّله الأول.");
            if (WaNotify.CantSend(c, s) != null) throw new ShopException("wa", "واتساب المتجر مش مربوط. ابعت الكود بنفسك من زرار «ابعته من موبايلك».");
            if (!Web.Allow("shop-gcre:" + id, 3, TimeSpan.FromHours(24))) throw new ShopException("many", "اتبعت كذا مرة النهارده.");
            ShopRew.Exec(c, null, "UPDATE dbo.ShopGiftCards SET SentAt=NULL, SendTry=NULL, SendErr=NULL, SendAt=SYSUTCDATETIME(), Sends=Sends+1 WHERE Id=@i AND StoreId=@s", "@i", id, "@s", sh.StoreId);
        }

        // ---------- the message ----------
        public static string Text(Shop sh, ShopGiftCard g)
        {
            bool en = g.Lang == "en"; string store = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name, from = (g.BuyerName ?? "").Trim().Split(' ')[0], url = sh.Url + (en && ShopJson.Str(ShopJson.At(sh.Look(false), "langs")) == "both" && ShopJson.Str(ShopJson.At(sh.Look(false), "first")) != "en" ? "/en" : "");
            var t = new StringBuilder();
            if (en)
            {
                t.Append("🎁 ").Append(string.IsNullOrEmpty(g.ToName) ? "Hello" : "*" + g.ToName + "*").Append(", ").Append(g.Kind == "sold" && from.Length > 0 ? from + " sent you a gift card from *" + store + "*" : "*" + store + "* sent you a gift card").Append("\n💳 Worth: *").Append(En(g.Amount)).Append("*\n");
                if (!string.IsNullOrEmpty(g.Msg)) t.Append("\n«").Append(g.Msg).Append("»\n\n");
                t.Append("🔑 Code: *").Append(Show(g.Code)).Append("*\n🛍️ Choose what you like on ").Append(url).Append(" and type the code in “Gift card” when you place the order.");
                if (g.ExpiresAt.HasValue) t.Append("\n⏳ Valid until ").Append(ShopRew.Day(g.ExpiresAt.Value, true));
            }
            else
            {
                t.Append("🎁 ").Append(string.IsNullOrEmpty(g.ToName) ? "أهلًا" : "*" + g.ToName + "*").Append("، ").Append(g.Kind == "sold" && from.Length > 0 ? from + " بعتلك كارت هدية من *" + store + "*" : "*" + store + "* بعتلك كارت هدية").Append("\n💳 قيمته: *").Append(M(g.Amount)).Append("*\n");
                if (!string.IsNullOrEmpty(g.Msg)) t.Append("\n«").Append(g.Msg).Append("»\n\n");
                t.Append("🔑 الكود: *").Append(Show(g.Code)).Append("*\n🛍️ اختار اللي يعجبك من ").Append(url).Append(" واكتب الكود في خانة «كارت هدية» وانت بتأكد الطلب.");
                if (g.ExpiresAt.HasValue) t.Append("\n⏳ صالح لحد ").Append(ShopRew.Day(g.ExpiresAt.Value));
            }
            return t.ToString();
        }

        // ---------- the job: cards whose time came (claimed first, so one is never sent twice) ----------
        public static async Task SendDue(SqlConnection c, CancellationToken ct)
        {
            var due = new List<int[]>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 30 Id, StoreId FROM dbo.ShopGiftCards WHERE SentAt IS NULL AND Status='active' AND ToPhone IS NOT NULL AND SendAt<=SYSUTCDATETIME() AND SendAt>DATEADD(DAY,-7,SYSUTCDATETIME()) AND (SendTry IS NULL OR SendTry<DATEADD(MINUTE,-30,SYSUTCDATETIME())) ORDER BY SendAt"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new[] { (int)r[0], (int)r[1] });
            foreach (var grp in due.GroupBy(x => x[1]))
            {
                var s = Db.StoreById(c, grp.Key); var sh = Shops.ByStore(c, grp.Key); if (s == null || sh == null) continue;
                var why0 = WaNotify.CantSend(c, s) ?? (Billing.Expired(s) ? "الباقة خلصت" : null);
                foreach (var k in grp)
                {
                    if (ct.IsCancellationRequested) return;
                    if (ShopRew.Exec(c, null, "UPDATE dbo.ShopGiftCards SET SendTry=SYSUTCDATETIME() WHERE Id=@i AND SentAt IS NULL AND (SendTry IS NULL OR SendTry<DATEADD(MINUTE,-30,SYSUTCDATETIME()))", "@i", k[0]) == 0) continue;
                    if (why0 != null) { ShopRew.Exec(c, null, "UPDATE dbo.ShopGiftCards SET SendErr=@e WHERE Id=@i", "@e", why0, "@i", k[0]); continue; }
                    var g = One(c, null, "WHERE Id=@i", "@i", k[0]); if (g == null) continue;
                    var why = await WaNotify.ToCustomer(c, s, g.ToPhone, Text(sh, g), "sb-shop-gc-" + g.Id + "-" + g.Sends);
                    if (why != null) { ShopRew.Exec(c, null, "UPDATE dbo.ShopGiftCards SET SendErr=@e WHERE Id=@i", "@e", why.Length > 200 ? why.Substring(0, 200) : why, "@i", g.Id); continue; }
                    ShopRew.Exec(c, null, "UPDATE dbo.ShopGiftCards SET SentAt=SYSUTCDATETIME(), SendErr=NULL WHERE Id=@i", "@i", g.Id);
                    if (g.OrderId.HasValue) ShopOrders.Log_(c, null, g.StoreId, g.OrderId.Value, "system", "كارت الهدية اتبعت لـ " + g.ToName + " على الواتساب");
                    // the buyer hears it arrived, once
                    if (g.Kind == "sold" && !string.IsNullOrEmpty(g.BuyerPhone) && g.BuyerPhone != g.ToPhone && ShopRew.Exec(c, null, "UPDATE dbo.ShopGiftCards SET ToldAt=SYSUTCDATETIME() WHERE Id=@i AND ToldAt IS NULL", "@i", g.Id) == 1)
                    {
                        bool en = g.Lang == "en"; string store = en && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name;
                        await WaNotify.ToCustomer(c, s, g.BuyerPhone, en ? "🎁 The gift card you sent to " + g.ToName + " from *" + store + "* has reached them on WhatsApp ✅ (" + En(g.Amount) + ", code " + Mask(g.Code) + ")" : "🎁 كارت الهدية اللي بعته لـ " + g.ToName + " من *" + store + "* وصله على الواتساب ✅ (" + M(g.Amount) + "، الكود " + Mask(g.Code) + ")", "sb-shop-gct-" + g.Id);
                    }
                }
            }
        }
    }
}
