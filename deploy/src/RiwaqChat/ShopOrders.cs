// A store's orders: placing one (from the store's page, later from the chat), its statuses, stock, history, edits, export.
// The price, the discount and the shipping are worked out again here from the database at the moment of the order, and the quantity is
// taken in the same transaction, so two people never buy the last piece. One order however many times the button is pressed
// (ClientKey). Statuses: new -> confirmed (the customer answered 1, or the owner) -> shipped -> out (for delivery) -> delivered;
// noreply, cancelled, returned. A cancelled or returned order gives its quantity back once.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class ShopOrderItem { public long Id; public int ProductId, VariantId, Qty, Returned; public string Name, Opts, Sku, Image, Extra; public decimal Price, Full, Discount; public decimal? Cost; public decimal Sum { get { return Price * Qty - Discount; } } }
    public class ShopOrder
    {
        public long Id; public int StoreId, No, Gov; public string Token, Status, Channel, Lang, Name, Phone, Phone2, City, Area, Address, Landmark, Note, Coupon, OffersJson, Pay, StaffNote, CancelReason, Carrier, Tracking, Src, FeesJson, ExtrasJson, PayState, Proof;
        public decimal Subtotal, Discount, Ship, Total, Fees, Refund, Deposit, Paid; public bool Dup, Test, Restocked, Seen; public DateTime CreatedAt, StatusAt; public DateTime? ConfirmedAt, ShippedAt, DeliveredAt;
        public List<ShopOrderItem> Items = new List<ShopOrderItem>();
        public string NoText { get { return "#" + No; } }
        // what the courier still collects (cash), after what was sent before
        public decimal Due { get { return Math.Max(0, Total - Paid); } }
        Dictionary<string, object> ex; public Dictionary<string, object> Extras { get { return ex ?? (ex = ShopJson.Obj(ExtrasJson)); } }
    }

    public class ShopOrderIn
    {
        public List<ShopLine> Lines = new List<ShopLine>(); public string Name, Phone, Phone2, City, Area, Address, Landmark, Note, Coupon, ClientKey, Lang, Device, Ip, Src, Channel = "web"; public int Gov; public decimal? Expected; public bool Agreed, Test, ByOwner;
        public ShopHow How = new ShopHow(); public string GiftMsg, RecvName, RecvPhone, Otp, Proof, Day, DayPart;
    }

    public static class ShopOrders
    {
        public static readonly string[] Statuses = { "new", "noreply", "confirmed", "shipped", "out", "delivered", "cancelled", "returned" };
        public static string StatusText(string s, bool en = false)
        {
            switch (s)
            {
                case "new": return en ? "New" : "جديد"; case "noreply": return en ? "No answer" : "مردّش"; case "confirmed": return en ? "Confirmed" : "اتأكد"; case "shipped": return en ? "Shipped" : "اتشحن"; case "out": return en ? "Out for delivery" : "خرج للتوصيل";
                case "delivered": return en ? "Delivered" : "اتسلّم"; case "cancelled": return en ? "Cancelled" : "ملغي"; case "returned": return en ? "Returned" : "رجع"; case "unpaid": return en ? "Waiting for payment" : "مستني الدفع"; default: return s;
            }
        }
        static bool Back(string s) { return s == "cancelled" || s == "returned"; }

        // ---------- an Egyptian mobile ----------
        public static string Mobile(string raw)
        {
            var n = Offers.Norm(raw);
            return n != null && Regex.IsMatch(n, @"^201[0125]\d{8}$") ? n : null;
        }
        public static string Local(string phone) { return phone != null && phone.StartsWith("20") && phone.Length == 12 ? "0" + phone.Substring(2) : phone; }

        // ---------- placing an order ----------
        public class Placed { public ShopOrder Order; public ShopQuote Quote; public bool Again; }

        public static Placed Place(SqlConnection c, StoreInfo s, Shop row, ShopOrderIn o)
        {
            string key = string.IsNullOrEmpty(o.ClientKey) ? null : Regex.Replace(o.ClientKey, "[^A-Za-z0-9-]", "");
            if (key != null && key.Length > 40) key = key.Substring(0, 40);
            if (key != null && key.Length < 8) key = null;
            if (key != null) { var had = ByKey(c, row.StoreId, key); if (had != null) return new Placed { Order = had, Again = true }; }

            bool digi = ShopDigital.Fill(c, row, o);   // digital lines only: no governorate, no address
            // the customer's details
            Func<string, int, string> cut = (t, n) => { t = Regex.Replace((t ?? "").Trim(), @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", ""); return t.Length > n ? t.Substring(0, n) : t; };
            string name = cut(o.Name, 80), address = cut(o.Address, 300), phone = Mobile(o.Phone), phone2 = string.IsNullOrWhiteSpace(o.Phone2) ? null : Mobile(o.Phone2);
            var bad = new Dictionary<string, object>();
            if (name.Length < 2) bad["name"] = "اكتب اسمك";
            if (phone == null) bad["phone"] = "اكتب رقم موبايل مصري صح";
            if (!string.IsNullOrWhiteSpace(o.Phone2) && phone2 == null) bad["phone2"] = "الرقم التاني مش صح";
            bool pick = !string.IsNullOrEmpty(o.How.Branch) && ShopPrice.Branch(row, o.How.Branch) != null; if (!pick) o.How.Branch = null;
            if (pick) { var br = ShopPrice.Branch(row, o.How.Branch); if (ShopGeo.Get(o.Gov) == null) o.Gov = ShopJson.Int(ShopJson.At(br, "gov")); if (cut(o.City, 60).Length < 2) o.City = "استلام من المحل"; if (address.Length < 5) address = "استلام من المحل: " + ShopJson.Str(ShopJson.At(br, "n"), 60); }
            string recv = cut(o.RecvName, 80), recvPhone = string.IsNullOrWhiteSpace(o.RecvPhone) ? null : Mobile(o.RecvPhone);
            if (recv.Length > 0 && recvPhone == null) bad["recvPhone"] = "اكتب رقم موبايل المستلم";
            if (ShopGeo.Get(o.Gov) == null && !digi) bad["gov"] = "اختار المحافظة";
            if (cut(o.City, 60).Length < 2 && !o.ByOwner) bad["city"] = "اختار المدينة أو المنطقة";
            if (address.Length < 5) bad["address"] = "اكتب العنوان بالتفصيل";
            if (!o.Agreed && !o.ByOwner) bad["agree"] = "وافق على الشروط الأول";
            if (bad.Count > 0) throw new ShopException("fields", "راجع البيانات المكتوبة بالأحمر.", bad);
            if (o.Lines.Count == 0) throw new ShopException("empty", "السلة فاضية.");
            if (!o.ByOwner && !o.Test)
            {
                // «ماتشحنلوش»: the owner marked this number; the store doesn't take its orders by itself (their switch)
                if (ShopJson.Bool(ShopJson.At(ShopJson.Sub(row.Settings, "order"), "block"), true) && Crm.Of(c, row.StoreId, phone).Mark == "block") throw new ShopException("blocked", "مش قادرين نسجّل طلبك من هنا. كلّم المتجر على واتساب وهو يساعدك.");
                if (ShopAccount.OtpNeeded(c, s, row, phone) && !ShopAccount.CheckOtp(c, row.StoreId, phone, o.Otp)) throw new ShopException("otp", string.IsNullOrWhiteSpace(o.Otp) ? "اكتب الكود اللي وصلك على الواتساب." : "الكود غلط أو وقته خلص.");
            }
            ShopShipDay.Check(c, row, o);   // the delivery day the customer chose is one on offer

            // twice at most: the catalog may have changed between reading it and taking the store's row
            for (int attempt = 0; ; attempt++)
            {
                var now = DateTime.UtcNow;
                var d = ShopCache.Get(c, row);
                if (d == null) throw new ShopException("closed", "المتجر مقفول دلوقتي.");
                var q = ShopPrice.Quote(c, d, o.Lines.Select(l => new ShopLine { VariantId = l.VariantId, Qty = l.Qty, Extra = l.Extra }).ToList(), o.Coupon, o.Gov, phone, now, true, o.How, o.City);
                if (q.Stop == "vacation") throw new ShopException("vacation", "المتجر واخد إجازة ومش بيستقبل طلبات دلوقتي.");
                if (q.Stop == "nodelivery") throw new ShopException("nodelivery", "للأسف مابنوصّلش لمحافظة " + ShopGeo.Name(o.Gov) + " دلوقتي.");
                if (q.Stop == "min") throw new ShopException("min", "أقل طلب " + q.Min.ToString("#,0.##", CultureInfo.InvariantCulture) + " ج.م.", ShopPrice.Json(q));
                if (q.Stop == "empty" || q.Lines.Any(l => l.Err != null)) throw new ShopException("cart", "فيه حاجة في سلتك اتغيّرت. راجعها وأكّد تاني.", ShopPrice.Json(q));
                if (!string.IsNullOrWhiteSpace(o.Coupon) && !q.CouponUsed) throw new ShopException("coupon", q.CouponErr ?? "الكود مش شغال.", ShopPrice.Json(q));
                ShopShipPick.Check(o, q);   // «الزبون يختار شركة الشحن»: one of the ways on now
                if (!o.ByOwner) ShopCard.Check(c, row, q, o.Lang);   // a digital line is paid first; installments above the owner's minimum
                // the customer saw another total (a price, an offer or the shipping moved): show the new one first
                if (o.Expected.HasValue && Math.Abs(o.Expected.Value - q.Total) >= 1) throw new ShopException("changed", "الإجمالي اتغيّر من آخر مرة شفته. راجع السعر الجديد وأكّد تاني.", ShopPrice.Json(q));
                if (o.Test) return new Placed { Quote = q, Order = new ShopOrder { No = 0, Token = "", Total = q.Total, Subtotal = q.Sub, Discount = q.Discount, Ship = q.Ship, Fees = q.FeesSum, Pay = q.Pay, Deposit = q.Deposit, Name = name, Phone = phone, Gov = o.Gov, City = cut(o.City, 60), Address = address, Status = "new", CreatedAt = now, Test = true, Lang = o.Lang == "en" ? "en" : "ar" } };

                if (attempt == 0 && !o.ByOwner)
                {
                    // five orders an hour from one number or one phone (the address is looser: many customers share one on mobile networks)
                    var hour = TimeSpan.FromHours(1);
                    if (!Web.Allow("shop-o:" + row.StoreId + ":" + phone, 5, hour) || (!string.IsNullOrEmpty(o.Device) && !Web.Allow("shop-od:" + row.StoreId + ":" + o.Device, 5, hour)) || !Web.Allow("shop-oi:" + o.Ip, 40, hour))
                        throw new ShopException("too_many", "عملت طلبات كتير في وقت قصير. استنى شوية أو كلّمنا على واتساب.");
                }
                using (var tx = c.BeginTransaction())
                {
                    bool done = false;
                    try
                    {
                        int no, ver;
                        // the store's row is held until the order is saved: orders of one store go one after the other
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET OrderNo=OrderNo+1, StockVer=StockVer+1 OUTPUT INSERTED.OrderNo, INSERTED.Ver WHERE StoreId=@s", "@s", row.StoreId))
                        { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) { r.Read(); no = (int)r[0]; ver = (int)r[1]; } }
                        if (ver != d.Ver) { tx.Rollback(); done = true; if (attempt < 3) continue; throw new ShopException("busy", "المتجر بيتحدّث دلوقتي. جرّب تاني بعد ثواني."); }
                        if (key != null)
                            using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id FROM dbo.ShopOrders WHERE StoreId=@s AND ClientKey=@k", "@s", row.StoreId, "@k", key))
                            { cmd.Transaction = tx; var v = cmd.ExecuteScalar(); if (v != null) { tx.Rollback(); done = true; return new Placed { Order = ById(c, row.StoreId, Convert.ToInt64(v)), Again = true }; } }

                        var took = new List<object[]>();
                        foreach (var g in q.Lines.GroupBy(l => l.VariantId).OrderBy(g => g.Key).ToList())
                        {
                            var l = g.First(); int n = g.Sum(x => x.Qty);
                            if (!l.P.Track) { took.Add(new object[] { l, n, null }); continue; }
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopVariants SET Stock=Stock-@n OUTPUT INSERTED.Stock WHERE Id=@v AND StoreId=@s AND Active=1 AND (Stock>=@n OR @free=1)", "@n", n, "@v", g.Key, "@s", row.StoreId, "@free", l.P.OutMode == "order" || l.V.PreLeft > 0 ? 1 : 0))
                            {
                                cmd.Transaction = tx; var left = cmd.ExecuteScalar();
                                if (left == null)
                                {
                                    if (g.All(x => x.Gift > 0)) { foreach (var x in g.ToList()) q.Lines.Remove(x); continue; }   // the gift ran out: the order goes on without it
                                    tx.Rollback(); done = true;
                                    // someone bought it a moment ago: the sum with what is left
                                    var d2 = ShopCache.Get(c, row);
                                    throw new ShopException("cart", "«" + l.P.Name + "» خلص أو باقي منه أقل من اللي طلبته.", ShopPrice.Json(ShopPrice.Quote(c, d2, o.Lines.Select(x => new ShopLine { VariantId = x.VariantId, Qty = x.Qty, Extra = x.Extra }).ToList(), o.Coupon, o.Gov, phone, now, true, o.How, o.City)));
                                }
                                took.Add(new object[] { l, n, (int)left });
                            }
                        }
                        if (q.CouponId > 0 && q.CouponUsed)
                            // the store's row is held, so two orders at once can't both be a number's first
                            using (var cmd = Db.Cmd(c, @"UPDATE dbo.ShopCoupons SET Uses=Uses+1 WHERE Id=@id AND StoreId=@s AND (MaxUses IS NULL OR Uses<MaxUses)
                                AND (PerPhone IS NULL OR PerPhone>(SELECT COUNT(*) FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.Phone=@p AND o.Coupon=ShopCoupons.Code AND o.Status<>'cancelled' AND o.Test=0))
                                AND (FirstOnly=0 OR NOT EXISTS (SELECT 1 FROM dbo.ShopOrders o WHERE o.StoreId=@s AND o.Phone=@p AND o.Status<>'cancelled' AND o.Test=0))", "@id", q.CouponId, "@s", row.StoreId, "@p", phone))
                            { cmd.Transaction = tx; if (cmd.ExecuteNonQuery() == 0) { tx.Rollback(); done = true; throw new ShopException("coupon", "الكود ده خلص، اتستخدم العدد المسموح."); } }

                        bool dup = false;
                        using (var cmd = Db.Cmd(c, @"SELECT COUNT(*) FROM dbo.ShopOrders x WHERE x.StoreId=@s AND x.Phone=@p AND x.CreatedAt>DATEADD(DAY,-1,SYSUTCDATETIME()) AND x.Status<>'cancelled'
                            AND EXISTS (SELECT 1 FROM dbo.ShopOrderItems i WHERE i.OrderId=x.Id AND i.ProductId IN (" + string.Join(",", q.Lines.Select(l => l.P.Id).Distinct()) + "))", "@s", row.StoreId, "@p", phone))
                        { cmd.Transaction = tx; dup = (int)cmd.ExecuteScalar() > 0; }

                        long id; string token = Shops.Token(16); string proof = o.ByOwner || o.Proof == null ? null : ShopAccount.PicIn(c, tx, row.StoreId, o.Proof);
                        var exd = new Dictionary<string, object>(); if (q.Fees.Any(f => f.Kind == "gift") && cut(o.GiftMsg, 200).Length > 0) exd["gift"] = cut(o.GiftMsg, 200); if (recv.Length > 0) exd["recv"] = new Dictionary<string, object> { { "n", recv }, { "p", recvPhone } };
                        if (q.Branch != null) { var br = ShopPrice.Branch(row, q.Branch); exd["branch"] = new Dictionary<string, object> { { "id", q.Branch }, { "n", ShopJson.Str(ShopJson.At(br, "n"), 60) }, { "ad", ShopJson.Str(ShopJson.At(br, "ad"), 200) } }; }
                        ShopShipDay.Extra(o, exd); ShopShipPick.Extra(q, exd);
                        string extras = exd.Count == 0 ? null : ShopJson.Write(exd);
                        using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.ShopOrders(StoreId, No, Token, ClientKey, Channel, Lang, Name, Phone, Phone2, Gov, City, Area, Address, Landmark, Note, Subtotal, Discount, Ship, Total, Coupon, Offers, Dup, Ip, Device, Src, Pay, Fees, FeesJson, Extras, PayState, Deposit, Proof)
                            OUTPUT INSERTED.Id VALUES(@s, @no, @t, @k, @ch, @lang, @n, @p, @p2, @g, @city, @area, @a, @lm, @note, @sub, @disc, @ship, @total, @cp, @of, @dup, @ip, @dev, @src, @pay, @fees, @fj, @ex, @ps, @dep, @proof)",
                            "@pay", q.Pay, "@fees", q.FeesSum, "@fj", q.Fees.Count == 0 ? null : ShopJson.Write(q.Fees.Select(f => new object[] { f.Kind, ShopView.Pair(f.Name, f.NameEn), f.Amount })), "@ex", extras, "@ps", q.Deposit > 0 ? (proof != null ? "sent" : "wait") : null, "@dep", q.Deposit, "@proof", proof,
                            "@s", row.StoreId, "@no", no, "@t", token, "@k", key, "@ch", o.Channel, "@lang", o.Lang == "en" ? "en" : "ar", "@n", name, "@p", phone, "@p2", phone2, "@g", o.Gov, "@city", Nz(cut(o.City, 60)), "@area", Nz(cut(o.Area, 80)), "@a", address, "@lm", Nz(cut(o.Landmark, 120)),
                            "@note", Nz(cut(o.Note, 400)), "@sub", q.Sub, "@disc", q.Discount, "@ship", q.Ship, "@total", q.Total, "@cp", q.CouponUsed ? q.Coupon : null,
                            "@of", q.Offers.Count == 0 ? null : ShopJson.Write(q.Offers.Select(x => new Dictionary<string, object> { { "id", x.Id }, { "n", x.Name }, { "a", x.Amount } })), "@dup", dup, "@ip", Nz(cut(o.Ip, 64)), "@dev", Nz(cut(o.Device, 40)), "@src", Nz(cut(o.Src, 200))))
                        { cmd.Transaction = tx; id = Convert.ToInt64(cmd.ExecuteScalar()); }
                        ShopShipDay.Take(c, tx, row, id, o);   // the day's last place goes to one order
                        foreach (var l in q.Lines)
                            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopOrderItems(StoreId, OrderId, ProductId, VariantId, Name, Opts, Sku, Image, Qty, Price, Full_, Discount, Cost, Extra) VALUES(@s, @o, @p, @v, @n, @op, @sku, @im, @q, @pr, @f, @d, @c, @x)",
                                "@s", row.StoreId, "@o", id, "@p", l.P.Id, "@v", l.VariantId, "@n", l.P.Name, "@op", Nz(l.V.Label), "@sku", Nz(l.V.Sku ?? l.P.Sku), "@im", Nz(l.V.Image ?? (l.P.Pics.Count > 0 ? l.P.Pics[0].Path : null)), "@q", l.Qty, "@pr", l.Unit, "@f", l.Full, "@d", l.Disc, "@c", l.P.CostOf(l.V).HasValue ? (object)l.P.CostOf(l.V).Value : null, "@x", Nz(cut(l.Extra, 600))))
                            { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                        ShopRew.Take(c, tx, row, id, q, phone, o.Lang, o.Ip);   // the gift card's balance, the points, the friend's gift: taken here or the order isn't made
                        foreach (var t in took)
                        {
                            var l = (ShopLine)t[0];
                            if (t[2] != null) Stock(c, tx, row.StoreId, l.P.Id, l.VariantId, -(int)t[1], (int)t[2], "order", id, null);
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopProducts SET Sold=Sold+@n WHERE Id=@p AND StoreId=@s", "@n", (int)t[1], "@p", l.P.Id, "@s", row.StoreId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                        }
                        Log_(c, tx, row.StoreId, id, o.ByOwner ? "owner" : "customer", o.ByOwner ? "اتسجل بإيد التاجر" : "الطلب اتعمل " + (o.Channel == "chat" ? "من الشات" : o.Channel == "wa" ? "من شات الواتساب" : o.Channel == "msgr" ? "من المسنجر" : o.Channel == "tg" ? "من تيليجرام" : "من المتجر"));
                        Daily(c, tx, row.StoreId, "Orders", 1);
                        tx.Commit(); done = true;
                        var order = ById(c, row.StoreId, id);
                        return new Placed { Order = order, Quote = q };
                    }
                    catch (SqlException ex) when (!done && (ex.Number == 2601 || ex.Number == 2627) && key != null)
                    {
                        tx.Rollback();
                        var had = ByKey(c, row.StoreId, key); if (had != null) return new Placed { Order = had, Again = true };
                        throw;
                    }
                    catch { if (!done) try { tx.Rollback(); } catch { } throw; }
                }
            }
        }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }

        public static void Stock(SqlConnection c, SqlTransaction tx, int storeId, int productId, int variantId, int delta, int after, string reason, long? orderId, string note)
        {
            if (ShopGoods.Moving(c, tx, storeId, productId, variantId, ref delta, ref after, reason, orderId)) return;   // a pre-order's pieces never left the shelf (ShopPre.cs)
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopStock(StoreId, ProductId, VariantId, Delta, After_, Reason, OrderId, Note) VALUES(@s, @p, @v, @d, @a, @r, @o, @n)", "@s", storeId, "@p", productId, "@v", variantId, "@d", delta, "@a", after, "@r", reason, "@o", orderId.HasValue ? (object)orderId.Value : null, "@n", note))
            { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
            ShopGoods.Moved(c, tx, storeId, productId, variantId, delta, after, reason, orderId);   // the places keep the total (ShopPlaces.cs)
        }
        public static void Log_(SqlConnection c, SqlTransaction tx, int storeId, long orderId, string who, string what)
        {
            who = ShopStaff.By(who);   // a staff member's change carries their name (ShopStaff.cs)
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopOrderLog(StoreId, OrderId, Who, What) VALUES(@s, @o, @w, @t)", "@s", storeId, "@o", orderId, "@w", who, "@t", what.Length > 300 ? what.Substring(0, 300) : what)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        }
        // one of the day's numbers (Cairo's day)
        public static void Daily(SqlConnection c, SqlTransaction tx, int storeId, string col, int n)
        {
            using (var cmd = Db.Cmd(c, "MERGE dbo.ShopDaily AS d USING (SELECT @s AS StoreId, CAST(DATEADD(HOUR, 2, SYSUTCDATETIME()) AS DATE) AS Day) AS x ON d.StoreId=x.StoreId AND d.Day=x.Day WHEN MATCHED THEN UPDATE SET " + col + "=" + col + "+@n WHEN NOT MATCHED THEN INSERT (StoreId, Day, " + col + ") VALUES (@s, x.Day, @n);", "@s", storeId, "@n", n))
            { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        }

        // ---------- reading ----------
        static DateTime? Dt(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }
        static ShopOrder Read(System.Data.IDataRecord r)
        {
            return new ShopOrder { Id = Convert.ToInt64(r["Id"]), StoreId = (int)r["StoreId"], No = (int)r["No"], Token = (string)r["Token"], Status = (string)r["Status"], Channel = (string)r["Channel"], Lang = ((string)r["Lang"]).Trim(), Name = (string)r["Name"], Phone = (string)r["Phone"], Phone2 = r["Phone2"] as string, Gov = (int)r["Gov"],
                City = r["City"] as string, Area = r["Area"] as string, Address = (string)r["Address"], Landmark = r["Landmark"] as string, Note = r["Note"] as string, Subtotal = (decimal)r["Subtotal"], Discount = (decimal)r["Discount"], Ship = (decimal)r["Ship"], Total = (decimal)r["Total"], Coupon = r["Coupon"] as string, OffersJson = r["Offers"] as string,
                Pay = (string)r["Pay"], Dup = (bool)r["Dup"], Test = (bool)r["Test"], StaffNote = r["StaffNote"] as string, CancelReason = r["CancelReason"] as string, Carrier = r["Carrier"] as string, Tracking = r["Tracking"] as string, Restocked = (bool)r["Restocked"], Seen = (bool)r["Seen"], Src = r["Src"] as string, Fees = (decimal)r["Fees"], FeesJson = r["FeesJson"] as string, ExtrasJson = r["Extras"] as string, Refund = (decimal)r["Refund"], PayState = r["PayState"] as string, Deposit = (decimal)r["Deposit"], Paid = (decimal)r["Paid"], Proof = r["Proof"] as string,
                CreatedAt = Dt(r["CreatedAt"]).Value, StatusAt = Dt(r["StatusAt"]).Value, ConfirmedAt = Dt(r["ConfirmedAt"]), ShippedAt = Dt(r["ShippedAt"]), DeliveredAt = Dt(r["DeliveredAt"]) };
        }
        static ShopOrder One(SqlConnection c, string where, params object[] args)
        {
            ShopOrder o;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 * FROM dbo.ShopOrders WHERE " + where, args)) using (var r = cmd.ExecuteReader()) o = r.Read() ? Read(r) : null;
            if (o != null) Items(c, new List<ShopOrder> { o });
            return o;
        }
        public static ShopOrder ById(SqlConnection c, int storeId, long id) { return One(c, "StoreId=@s AND Id=@i", "@s", storeId, "@i", id); }
        public static ShopOrder ByNo(SqlConnection c, int storeId, int no) { return One(c, "StoreId=@s AND No=@n", "@s", storeId, "@n", no); }
        public static ShopOrder ByToken(SqlConnection c, int storeId, string token) { return One(c, "StoreId=@s AND Token=@t", "@s", storeId, "@t", token ?? ""); }
        static ShopOrder ByKey(SqlConnection c, int storeId, string key) { return One(c, "StoreId=@s AND ClientKey=@k", "@s", storeId, "@k", key); }
        public static void Items(SqlConnection c, List<ShopOrder> list)
        {
            if (list.Count == 0) return;
            var by = list.ToDictionary(o => o.Id);
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopOrderItems WHERE OrderId IN (" + string.Join(",", by.Keys) + ") ORDER BY Id"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    by[Convert.ToInt64(r["OrderId"])].Items.Add(new ShopOrderItem { Id = Convert.ToInt64(r["Id"]), ProductId = (int)r["ProductId"], VariantId = (int)r["VariantId"], Name = (string)r["Name"], Opts = r["Opts"] as string, Sku = r["Sku"] as string, Image = r["Image"] as string, Qty = (int)r["Qty"],
                        Price = (decimal)r["Price"], Full = (decimal)r["Full_"], Discount = (decimal)r["Discount"], Cost = r["Cost"] as decimal?, Extra = r["Extra"] as string, Returned = (int)r["Returned"] });
        }

        // the customer's own view: by number and phone («طلبي فين؟») or by the secret link of the thank-you page
        public static Dictionary<string, object> CustomerJson(ShopOrder o, bool full)
        {
            var d = new Dictionary<string, object>
            {
                { "no", o.No }, { "status", o.Status }, { "at", ShopView.Unix(o.CreatedAt) }, { "statusAt", ShopView.Unix(o.StatusAt) }, { "total", o.Total }, { "sub", o.Subtotal }, { "off", o.Discount }, { "ship", o.Ship }, { "tracking", o.Tracking ?? "" }, { "carrier", o.Carrier ?? "" },
                { "fees", ShopJson.Arr(o.FeesJson) }, { "pay", o.Pay }, { "dep", o.Deposit }, { "paid", o.Paid }, { "ps", o.PayState ?? "" }, { "ex", full ? o.Extras : o.Extras.Where(k => k.Key == "branch" || (k.Key == "by" && (o.Status == "shipped" || o.Status == "out"))).ToDictionary(k => k.Key, k => k.Value) }, { "refund", o.Refund },
                { "gov", o.Gov }, { "city", o.City ?? "" }, { "name", o.Name.Split(' ')[0] },
                { "items", o.Items.Select(i => new Dictionary<string, object> { { "p", i.ProductId }, { "n", i.Name }, { "o", i.Opts ?? "" }, { "q", i.Qty }, { "t", i.Sum }, { "im", i.Image ?? "" }, { "x", i.Extra ?? "" } }).ToArray() }
            };
            if (full) { d["phone"] = Local(o.Phone); d["address"] = o.Address; }
            return d;
        }

        // ---------- statuses ----------
        // who: owner / customer / bot / carrier. Gives the quantity back when an order is cancelled or returned (restock), and takes it
        // again when the owner brings it back to life. null = done, otherwise why not.
        public static string SetStatus(SqlConnection c, int storeId, long id, string to, string who, string reason = null, bool restock = true)
        {
            if (!Statuses.Contains(to)) return "الحالة دي مش معروفة.";
            var o = ById(c, storeId, id);
            if (o == null) return "الأوردر ده مش موجود.";
            if (o.Status == to) return null;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    using (var cmd = Db.Cmd(c, "SELECT Status FROM dbo.ShopOrders WITH (UPDLOCK, ROWLOCK) WHERE Id=@i AND StoreId=@s", "@i", id, "@s", storeId)) { cmd.Transaction = tx; if ((string)cmd.ExecuteScalar() != o.Status) { tx.Rollback(); return "حالة الأوردر اتغيّرت حالًا. حدّث الصفحة وجرّب تاني."; } }
                    bool moved = false;
                    if (Back(to) && !Back(o.Status) && restock && !o.Restocked) { Return(c, tx, o, 1, to); moved = true; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Restocked=1 WHERE Id=@i", "@i", id)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); } }
                    else if (!Back(to) && o.Restocked)
                    {
                        var no = Return(c, tx, o, -1, "reopen");
                        if (no != null) { tx.Rollback(); return no; }
                        moved = true; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Restocked=0 WHERE Id=@i", "@i", id)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                    }
                    // a cancelled order's coupon can be used again
                    if (to == "cancelled" && o.Coupon != null) using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopCoupons SET Uses=CASE WHEN Uses>0 THEN Uses-1 ELSE 0 END WHERE StoreId=@s AND Code=@k", "@s", storeId, "@k", o.Coupon)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                    else if (o.Status == "cancelled" && o.Coupon != null) using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopCoupons SET Uses=Uses+1 WHERE StoreId=@s AND Code=@k", "@s", storeId, "@k", o.Coupon)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                    { var rw = ShopRew.OnStatus(c, tx, o, to); if (rw != null) { tx.Rollback(); return rw; } }   // gift cards, points, «هات صاحبك»
                    using (var cmd = Db.Cmd(c, @"UPDATE dbo.ShopOrders SET Status=@t, StatusAt=SYSUTCDATETIME(), Seen=1, CancelReason=CASE WHEN @t IN ('cancelled','returned') THEN @r ELSE NULL END,
                        ConfirmedAt=CASE WHEN @t='confirmed' AND ConfirmedAt IS NULL THEN SYSUTCDATETIME() ELSE ConfirmedAt END, ShippedAt=CASE WHEN @t IN ('shipped','out') AND ShippedAt IS NULL THEN SYSUTCDATETIME() ELSE ShippedAt END,
                        DeliveredAt=CASE WHEN @t='delivered' THEN SYSUTCDATETIME() WHEN @t IN ('returned') THEN DeliveredAt ELSE NULL END WHERE Id=@i AND StoreId=@s", "@t", to, "@r", Nz(reason), "@i", id, "@s", storeId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                    Log_(c, tx, storeId, id, who, StatusText(o.Status) + " ← " + StatusText(to) + (string.IsNullOrWhiteSpace(reason) ? "" : "، السبب: " + reason) + (moved ? (Back(to) ? "، والكمية رجعت" : "، والكمية اتخصمت تاني") : ""));
                    if (moved) using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@s", storeId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            ShopHooks.OnStatus(c, storeId, id, o.Status, to, who);
            return null;
        }

        internal static string Return(SqlConnection c, SqlTransaction tx, ShopOrder o, int sign, string reason)
        {
            foreach (var g in o.Items.GroupBy(i => i.VariantId).OrderBy(g => g.Key))
            {
                // what came back by a return request is already counted
                int n = g.Sum(i => Math.Max(0, i.Qty - i.Returned)); if (n == 0) continue;
                using (var cmd = Db.Cmd(c, @"UPDATE v SET Stock=v.Stock+@d OUTPUT INSERTED.Stock FROM dbo.ShopVariants v JOIN dbo.ShopProducts p ON p.Id=v.ProductId
                    WHERE v.Id=@v AND v.StoreId=@s AND p.Track=1 AND (@d>0 OR v.Stock+@d>=0 OR p.OutMode='order')", "@d", sign * n, "@v", g.Key, "@s", o.StoreId))
                {
                    cmd.Transaction = tx; var left = cmd.ExecuteScalar();
                    if (left == null)
                    {
                        // not tracked (or the product is gone): nothing to move. Tracked with too little left: the owner is told.
                        using (var chk = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.ShopVariants v JOIN dbo.ShopProducts p ON p.Id=v.ProductId WHERE v.Id=@v AND v.StoreId=@s AND p.Track=1", "@v", g.Key, "@s", o.StoreId)) { chk.Transaction = tx; if (sign < 0 && (int)chk.ExecuteScalar() > 0) return "مفيش كمية كفاية من «" + g.First().Name + "» عشان الأوردر يرجع يشتغل."; }
                        continue;
                    }
                    Stock(c, tx, o.StoreId, g.First().ProductId, g.Key, sign * n, (int)left, reason, o.Id, null);
                }
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopProducts SET Sold=CASE WHEN Sold-@n>0 THEN Sold-@n ELSE 0 END WHERE Id=@p AND StoreId=@s", "@n", sign * n, "@p", g.First().ProductId, "@s", o.StoreId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
            }
            return null;
        }

        // ---------- the owner edits an order (size, quantity, address) before it's shipped ----------
        public static string Edit(SqlConnection c, Shop row, long id, Dictionary<string, object> b)
        {
            var o = ById(c, row.StoreId, id);
            if (o == null) return "الأوردر ده مش موجود.";
            if (o.Status == "delivered" || Back(o.Status)) return "الأوردر ده خلص، ماينفعش يتعدّل.";
            var d = ShopCache.Get(c, row); var now = DateTime.UtcNow;
            var changes = new List<string>();
            string name = ShopJson.Str(ShopJson.At(b, "name"), 80), phone = Mobile(ShopJson.Str(ShopJson.At(b, "phone"), 24)), phone2 = ShopJson.Str(ShopJson.At(b, "phone2"), 24).Length == 0 ? null : Mobile(ShopJson.Str(ShopJson.At(b, "phone2"), 24));
            int gov = ShopJson.Int(ShopJson.At(b, "gov"), o.Gov); string city = ShopJson.Str(ShopJson.At(b, "city"), 60), area = ShopJson.Str(ShopJson.At(b, "area"), 80), address = ShopJson.Str(ShopJson.At(b, "address"), 300), lm = ShopJson.Str(ShopJson.At(b, "landmark"), 120), note = ShopJson.Str(ShopJson.At(b, "note"), 400);
            if (name.Length < 2 || phone == null || ShopGeo.Get(gov) == null || address.Length < 5) return "راجع الاسم والموبايل والمحافظة والعنوان.";
            if (name != o.Name) changes.Add("الاسم"); if (phone != o.Phone) changes.Add("الموبايل"); if (gov != o.Gov) changes.Add("المحافظة"); if (address != o.Address || city != (o.City ?? "") || area != (o.Area ?? "")) changes.Add("العنوان");

            var lines = ShopJson.List(ShopJson.At(b, "items")).OfType<Dictionary<string, object>>().Select(x => new ShopLine { VariantId = ShopJson.Int(ShopJson.At(x, "v")), Qty = Math.Max(0, Math.Min(999, ShopJson.Int(ShopJson.At(x, "q")))), Extra = ShopJson.Str(ShopJson.At(x, "x"), 600) }).Where(x => x.Qty > 0).ToList();
            bool sameItems = lines.Count == o.Items.Count && lines.Zip(o.Items, (x, y) => x.VariantId == y.VariantId && x.Qty == y.Qty).All(x => x);
            if (lines.Count == 0) return "الأوردر لازم يفضل فيه منتج واحد على الأقل. لو عايز تلغيه، غيّر حالته لملغي.";
            string days; bool off; decimal min;
            decimal ship = b.ContainsKey("ship") ? Math.Max(0, ShopJson.Dec(ShopJson.At(b, "ship"))) : (gov != o.Gov || ShopShipRate.CityMoves(d, gov, o.City, city) || (!sameItems && ShopShipRate.Weighs(d, gov))) && o.Ship > 0 ? ShopShipPick.Refee(c, d, o, ShopShipRate.Fee(c, d, gov, city, ShopShipRate.On(d) ? ShopPrice.Quote(c, d, lines.Select(l => new ShopLine { VariantId = l.VariantId, Qty = l.Qty, Extra = l.Extra }).ToList(), null, gov, phone, now, false).Lines : null, ShopPrice.ShipFee(d, gov, out days, out off, out min) ?? 0)) : o.Ship;   // the company the customer chose keeps its own fee
            decimal extraOff = b.ContainsKey("off") ? Math.Max(0, ShopJson.Dec(ShopJson.At(b, "off"))) : -1;

            using (var tx = c.BeginTransaction())
            {
                try
                {
                    decimal sub = o.Subtotal, disc = o.Discount;
                    if (!sameItems)
                    {
                        // the quantities move by the difference; a piece keeps the price it was sold at, a new one takes today's
                        var before = o.Items.GroupBy(i => i.VariantId).ToDictionary(g => g.Key, g => g.Sum(i => i.Qty)); var after = lines.GroupBy(l => l.VariantId).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
                        foreach (var vid in before.Keys.Union(after.Keys).OrderBy(x => x))
                        {
                            int x, y; before.TryGetValue(vid, out x); after.TryGetValue(vid, out y); int more = y - x; if (more == 0) continue;
                            ShopVar v; if (!d.Vars.TryGetValue(vid, out v)) { if (more > 0) { tx.Rollback(); return "فيه منتج مش موجود في المتجر."; } continue; }
                            var p = d.ById[v.ProductId];
                            if (p.Track)
                                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopVariants SET Stock=Stock-@n OUTPUT INSERTED.Stock WHERE Id=@v AND StoreId=@s AND (@n<0 OR Stock>=@n OR @free=1)", "@n", more, "@v", vid, "@s", row.StoreId, "@free", p.OutMode == "order" || v.PreLeft > 0 ? 1 : 0))
                                { cmd.Transaction = tx; var left = cmd.ExecuteScalar(); if (left == null) { tx.Rollback(); return "مفيش كمية كفاية من «" + p.Name + "»."; } Stock(c, tx, row.StoreId, p.Id, vid, -more, (int)left, "edit", id, null); }
                            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopProducts SET Sold=CASE WHEN Sold+@n>0 THEN Sold+@n ELSE 0 END WHERE Id=@p AND StoreId=@s", "@n", more, "@p", p.Id, "@s", row.StoreId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                        }
                        using (var cmd = Db.Cmd(c, "DELETE FROM dbo.ShopOrderItems WHERE OrderId=@o AND StoreId=@s", "@o", id, "@s", row.StoreId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                        sub = 0; disc = 0; var pool = o.Items.ToList();   // the same size twice with other words on it: each line keeps its own
                        foreach (var l in lines)
                        {
                            var had = pool.FirstOrDefault(i => i.VariantId == l.VariantId && (i.Extra ?? "") == (l.Extra ?? "")) ?? pool.FirstOrDefault(i => i.VariantId == l.VariantId); if (had != null) pool.Remove(had); ShopVar v; d.Vars.TryGetValue(l.VariantId, out v); var p = v == null ? null : d.ById[v.ProductId];
                            if (had == null && p == null) { tx.Rollback(); return "فيه منتج مش موجود في المتجر."; }
                            decimal? old; ShopOffer of; decimal price = had != null ? had.Price : ShopPrice.Unit(d, p, v, now, out old, out of), full = had != null ? had.Full : p.PriceOf(v);
                            decimal off1 = had != null && had.Qty > 0 ? Math.Round(had.Discount / had.Qty * Math.Min(l.Qty, had.Qty), 2) : 0;
                            sub += price * l.Qty; disc += off1;
                            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopOrderItems(StoreId, OrderId, ProductId, VariantId, Name, Opts, Sku, Image, Qty, Price, Full_, Discount, Cost, Extra) VALUES(@s, @o, @p, @v, @n, @op, @sku, @im, @q, @pr, @f, @d, @c, @x)",
                                "@s", row.StoreId, "@o", id, "@p", had != null ? had.ProductId : p.Id, "@v", l.VariantId, "@n", had != null ? had.Name : p.Name, "@op", Nz(had != null ? had.Opts : v.Label), "@sku", Nz(had != null ? had.Sku : v.Sku ?? p.Sku), "@im", Nz(had != null ? had.Image : v.Image ?? (p.Pics.Count > 0 ? p.Pics[0].Path : null)), "@q", l.Qty, "@pr", price, "@f", full, "@d", off1,
                                "@c", had != null ? (had.Cost.HasValue ? (object)had.Cost.Value : null) : (p.CostOf(v).HasValue ? (object)p.CostOf(v).Value : null), "@x", Nz(had != null ? had.Extra : l.Extra)))
                            { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                        }
                        changes.Add("المنتجات");
                    }
                    if (extraOff >= 0) { if (Math.Abs(extraOff - disc) >= 1) changes.Add("الخصم"); disc = Math.Min(sub, extraOff); }
                    if (Math.Abs(ship - o.Ship) >= 1) changes.Add("الشحن");
                    decimal total = sub - disc + ship + o.Fees;
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Name=@n, Phone=@p, Phone2=@p2, Gov=@g, City=@city, Area=@area, Address=@a, Landmark=@lm, Note=@note, Subtotal=@sub, Discount=@disc, Ship=@ship, Total=@total, Seen=1 WHERE Id=@i AND StoreId=@s",
                        "@n", name, "@p", phone, "@p2", phone2, "@g", gov, "@city", Nz(city), "@area", Nz(area), "@a", address, "@lm", Nz(lm), "@note", Nz(note), "@sub", sub, "@disc", disc, "@ship", ship, "@total", total, "@i", id, "@s", row.StoreId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                    if (changes.Count > 0) Log_(c, tx, row.StoreId, id, "owner", "اتعدّل: " + string.Join("، ", changes) + (total != o.Total ? " (الإجمالي من " + o.Total.ToString("#,0.##", CultureInfo.InvariantCulture) + " لـ " + total.ToString("#,0.##", CultureInfo.InvariantCulture) + ")" : ""));
                    if (!sameItems) using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET StockVer=StockVer+1 WHERE StoreId=@s", "@s", row.StoreId)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            ShopHooks.OnEdit(c, row.StoreId, id);
            return null;
        }

        // ---------- the order as a WhatsApp message would list it ----------
        public static string ItemsText(ShopOrder o, int max = 15)
        {
            var sb = new StringBuilder(); int n = 0;
            foreach (var i in o.Items)
            {
                if (++n > max) { sb.Append("• …\n"); break; }
                sb.Append("• ").Append(i.Name); if (!string.IsNullOrEmpty(i.Opts)) sb.Append(" (").Append(i.Opts).Append(")"); if (i.Qty > 1) sb.Append(" × ").Append(i.Qty); sb.Append("\n");
            }
            return sb.ToString();
        }
        public static string Money(decimal v) { return v.ToString(v == Math.Floor(v) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture) + " ج.م"; }
        public static string AddressText(ShopOrder o) { return string.Join("، ", new[] { ShopGeo.Name(o.Gov), o.City, o.Area, o.Address, string.IsNullOrEmpty(o.Landmark) ? null : "علامة مميزة: " + o.Landmark }.Where(x => !string.IsNullOrWhiteSpace(x))); }

        public static void FailAt(string slug, string what, string detail)
        {
            try { int id; using (var c = Db.Open()) { var sh = Shops.BySlug(c, slug); if (sh == null) return; id = sh.StoreId; } Fail(id, what, detail); } catch (Exception ex) { Log.Error(ex); }
        }
        public static void Fail(int storeId, string what, string detail)
        {
            // a customer who couldn't order: the admin hears of it at once (once in half an hour for a store)
            if (what == "order" && Web.Allow("shop-fail:" + storeId, 1, TimeSpan.FromMinutes(30))) HostingEnvironment.QueueBackgroundWorkItem(ct => ShopHooks.Failed(storeId, detail));
            try { using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopFails(StoreId, What, Detail) VALUES(@s, @w, @d)", "@s", storeId, "@w", what, "@d", detail != null && detail.Length > 600 ? detail.Substring(0, 600) : detail)) cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    // What the rest of Sellybot does when something happens to an order (WhatsApp, «زبايني», alerts): ShopBot.cs
    public static partial class ShopHooks
    {
        public static void OnPlaced(StoreInfo s, long orderId, bool quiet = false) { int sid = s.Id; HostingEnvironment.QueueBackgroundWorkItem(ct => Placed(sid, orderId, quiet)); ShopApiHooks.OnOrder(sid, orderId, null, null); }
        public static void OnStatus(SqlConnection c, int storeId, long id, string from, string to, string who) { ShopTrack.Changed(c, storeId, id, to); HostingEnvironment.QueueBackgroundWorkItem(ct => Changed(storeId, id, from, to, who)); ShopApiHooks.OnOrder(storeId, id, from, to); }
        public static void OnEdit(SqlConnection c, int storeId, long id) { HostingEnvironment.QueueBackgroundWorkItem(ct => Edited(storeId, id)); }
    }
}
