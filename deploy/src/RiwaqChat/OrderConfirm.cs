// Order confirmation on WhatsApp (Shopify stores).
// A new order (Shopify "orders/create" webhook) -> a WhatsApp message from the store's number to the shipping phone with the
// order details. The customer answers 1 (confirm) or 2 (cancel); a cancel is applied in Shopify at once (orderCancel, needs
// write_orders) and the order gets a tag either way. Anything else the customer writes goes to the assistant as usual.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class OrderConfirm
    {
        public const string Scope = "write_orders";
        const int AnswerDays = 7;   // an answer older than this no longer cancels anything
        const string TagConfirmed = "واتساب: مؤكد", TagCancelled = "واتساب: الزبون لغى";

        // ---------- 1. a new order ----------
        public static void OnOrder(SqlConnection c, StoreInfo s, byte[] body)
        {
            if (!s.OcOn || s.Status != "active") return;
            JsonElement o;
            try { using (var doc = JsonDocument.Parse(body)) o = doc.RootElement.Clone(); }
            catch { return; }
            long orderId;
            if (!long.TryParse(Str(o, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out orderId)) return;
            if (!string.IsNullOrEmpty(Str(o, "cancelled_at"))) return;

            string name = Str(o, "name") ?? ("#" + Str(o, "order_number"));
            var ship = Obj(o, "shipping_address"); var bill = Obj(o, "billing_address"); var cust = Obj(o, "customer");
            string first = First(Str(ship, "first_name"), Str(cust, "first_name"), Str(bill, "first_name"));
            string country = First(Str(ship, "country_code"), Str(bill, "country_code"));
            var dflt = cust.HasValue ? Obj(cust.Value, "default_address") : null;
            string phone = new[] { Str(ship, "phone"), Str(o, "phone"), Str(bill, "phone"), Str(cust, "phone"), Str(dflt, "phone") }
                .Select(p => Phone(p, country)).FirstOrDefault(p => p != null);
            string currency = Str(o, "currency") ?? "";
            string total = Money(Str(o, "total_price"), currency);
            bool cod = IsCod(o);

            string status = "pending", note = null;
            if ((Str(o, "tags") ?? "").Contains(ChatOrders.TagChat)) { status = "skipped"; note = "الطلب اتعمل من الشات ورقم الزبون اتأكد هناك"; }
            else if (s.OcScope == "cod" && !cod) { status = "skipped"; note = "مدفوع أونلاين (التأكيد شغال على الدفع عند الاستلام بس)"; }
            else if (phone == null) { status = "skipped"; note = "الأوردر مفيهوش رقم موبايل"; }
            else if (Billing.Expired(s)) { status = "skipped"; note = "الباقة خلصت"; }
            else if (!PlanGate.Allows(c, s, "shopify") || !WaAccount.PlanAllows(c, s, out _)) { status = "skipped"; note = "باقتك الحالية مش شاملة ربط Shopify والواتساب"; }
            else if (!Web.Allow("oc:" + s.Id, 300, TimeSpan.FromHours(1))) { status = "skipped"; note = "أوردرات كتير في ساعة واحدة"; }

            long rowId;
            try
            {
                using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.OrderConfirms(StoreId, OrderId, OrderName, Customer, Phone, Total, Status, Note)
                    OUTPUT INSERTED.Id VALUES(@s, @o, @n, @cu, @p, @t, @st, @no)",
                    "@s", s.Id, "@o", orderId, "@n", Cut(name, 40), "@cu", Cut(first, 100), "@p", phone, "@t", Cut(total, 40), "@st", status, "@no", note))
                    rowId = Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return; }   // same order delivered twice
            if (status != "pending") return;

            string text = Message(s, o, name, first, total, cod);
            int storeId = s.Id;
            HostingEnvironment.QueueBackgroundWorkItem(ct => Send(storeId, rowId, phone, text));
        }

        static async Task Send(int storeId, long rowId, string phone, string text)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    string token = s == null ? null : WaAccount.Token(c, s);
                    if (s == null || string.IsNullOrEmpty(s.WaInstance) || token == null || string.IsNullOrEmpty(s.WaNumber))
                    { Mark(c, rowId, "failed", "واتساب المتجر مش مربوط، فالرسالة ماتبعتتش"); return; }
                    long? mine = WhatsAppBot.Save(c, s.Id, phone, false, text, null);   // part of the chat history, and recognised when Wapilot echoes it
                    string chat = phone + "@c.us";
                    try
                    {
                        string idem = "sb-oc-" + rowId;
                        bool buttons = await Wapilot.TrySendButtons(token, s.WaInstance, chat, Body(text),
                            new[] { new[] { "sb_oc_yes", "✅ تأكيد الطلب" }, new[] { "sb_oc_no", "❌ إلغاء الطلب" } }, idem);
                        if (!buttons) await Wapilot.Send(token, s.WaInstance, chat, text, idem);
                    }
                    catch (WaException ex)
                    {
                        if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                        Mark(c, rowId, "failed", ex.Message);
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        static string Message(StoreInfo s, JsonElement o, string name, string first, string total, bool cod)
        {
            var sb = new StringBuilder();
            sb.Append("أهلًا").Append(string.IsNullOrWhiteSpace(first) ? "" : " " + first.Trim()).Append(" 👋\n");
            sb.Append("شكرًا إنك طلبت من *").Append(s.Name).Append("*!\n\n");
            sb.Append("🧾 *طلب رقم ").Append(name).Append("*\n");
            JsonElement items;
            if (o.TryGetProperty("line_items", out items) && items.ValueKind == JsonValueKind.Array)
            {
                int n = 0;
                foreach (var li in items.EnumerateArray())
                {
                    if (++n > 15) { sb.Append("• …\n"); break; }
                    string title = Str(li, "title") ?? Str(li, "name") ?? "";
                    string variant = Str(li, "variant_title");
                    int q; int.TryParse(Str(li, "quantity") ?? "1", out q);
                    sb.Append("• ").Append(title.Trim());
                    if (!string.IsNullOrWhiteSpace(variant) && variant != "Default Title") sb.Append(" (").Append(variant.Trim()).Append(")");
                    if (q > 1) sb.Append(" × ").Append(q);
                    sb.Append("\n");
                }
            }
            if (total != null) sb.Append("💰 الإجمالي: *").Append(total).Append("*\n");
            if (cod) sb.Append("💵 الدفع: كاش عند الاستلام\n");
            else if ((Str(o, "financial_status") ?? "") == "paid") sb.Append("💳 الدفع: مدفوع\n");
            var ship = Obj(o, "shipping_address");
            string addr = string.Join("، ", new[] { Str(ship, "address1"), Str(ship, "address2"), Str(ship, "city") }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            if (addr.Length > 0) sb.Append("📍 التوصيل: ").Append(addr.Length > 200 ? addr.Substring(0, 200) : addr).Append("\n");
            sb.Append("\nمن فضلك أكّد طلبك عشان نبدأ نجهّزه:\n");
            sb.Append("اكتب *1* لتأكيد الطلب ✅\n");
            sb.Append("اكتب *2* لإلغاء الطلب ❌");
            return sb.ToString();
        }

        // the message without the "write 1 / 2" lines (used with real buttons)
        static string Body(string text) { int i = text.IndexOf("\nاكتب *1*", StringComparison.Ordinal); return i > 0 ? text.Substring(0, i).TrimEnd() : text; }

        // ---------- 2. the customer's answer ----------
        // Called for every text a customer sends to the store's number, before the assistant. True = handled here.
        public static bool Intercept(SqlConnection c, StoreInfo s, string chatKey, string chat, string text, long rowId)
        {
            bool? yes = Decide(text);
            if (yes == null) return false;
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.OrderConfirms WHERE StoreId=@s AND Status='pending' AND CreatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME())", "@s", s.Id, "@d", AnswerDays))
                if ((int)cmd.ExecuteScalar() == 0) return false;

            int storeId = s.Id;
            string phone = WaChat.Digits(chatKey);
            if (phone != null)
            {
                long? oc = Pending(c, storeId, phone);
                if (oc == null) return false;
                if (!Claim(c, oc.Value, yes.Value)) return false;
                HostingEnvironment.QueueBackgroundWorkItem(ct => Act(storeId, oc.Value, yes.Value, chat, phone));
                return true;
            }
            // "...@lid": ask Wapilot for the number behind it, then either answer the order or let the assistant reply
            HostingEnvironment.QueueBackgroundWorkItem(async ct =>
            {
                try
                {
                    long? oc = null; string num = null;
                    using (var c2 = Db.Open())
                    {
                        var s2 = Db.StoreById(c2, storeId);
                        var token = s2 == null ? null : WaAccount.Token(c2, s2);
                        if (token != null && !string.IsNullOrEmpty(s2.WaInstance)) num = await Wapilot.LidPhone(token, s2.WaInstance, chat);
                        if (num != null) { oc = Pending(c2, storeId, num); if (oc != null && !Claim(c2, oc.Value, yes.Value)) oc = null; }
                    }
                    if (oc != null) await Act(storeId, oc.Value, yes.Value, chat, chatKey);
                    else await WhatsAppBot.Reply(storeId, chatKey, chat, rowId, ct);
                }
                catch (Exception ex) { Log.Error(ex); }
            });
            return true;
        }

        static long? Pending(SqlConnection c, int storeId, string phone)
        {
            string tail = phone.Length > 9 ? phone.Substring(phone.Length - 9) : phone;
            using (var cmd = Db.Cmd(c, @"SELECT TOP 1 Id FROM dbo.OrderConfirms WHERE StoreId=@s AND Status='pending' AND Phone IS NOT NULL
                AND RIGHT(Phone, 9) = @t AND CreatedAt > DATEADD(DAY,-@d,SYSUTCDATETIME()) ORDER BY Id DESC", "@s", storeId, "@t", tail, "@d", AnswerDays))
            {
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? (long?)null : Convert.ToInt64(v);
            }
        }

        // only one answer wins, even when the customer sends "1" twice
        static bool Claim(SqlConnection c, long id, bool yes)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.OrderConfirms SET Status=@st, AnsweredAt=SYSUTCDATETIME() WHERE Id=@id AND Status='pending'",
                "@st", yes ? "confirmed" : "cancelled", "@id", id))
                return cmd.ExecuteNonQuery() == 1;
        }

        static async Task Act(int storeId, long id, bool yes, string chat, string chatKey)
        {
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (s == null) return;
                    long orderId; string name;
                    using (var cmd = Db.Cmd(c, "SELECT OrderId, OrderName FROM dbo.OrderConfirms WHERE Id=@id", "@id", id))
                    using (var r = cmd.ExecuteReader()) { if (!r.Read()) return; orderId = (long)r["OrderId"]; name = (string)r["OrderName"]; }
                    string gid = "gid://shopify/Order/" + orderId.ToString(CultureInfo.InvariantCulture);
                    var creds = ShopifyAdmin.For(s);
                    string reply;
                    if (yes)
                    {
                        if (creds != null) await ShopifyAdmin.AddTags(creds, gid, TagConfirmed);
                        reply = "تمام ✅ طلبك " + name + " اتأكد، وهنبدأ نجهّزه على طول. شكرًا لثقتك في *" + s.Name + "* 🙏";
                    }
                    else
                    {
                        string err = creds == null ? "Shopify مش مربوط" : !ShopifyAdmin.Has(s.ShopifyScopes, Scope) ? "التطبيق ناقصه صلاحية write_orders"
                            : await ShopifyAdmin.CancelOrder(creds, gid, "الزبون لغى الطلب من رسالة التأكيد على واتساب (Sellybot)");
                        if (err == null)
                        {
                            await ShopifyAdmin.AddTags(creds, gid, TagCancelled);
                            reply = "تم إلغاء طلبك " + name + " ❌\nلو غيّرت رأيك أو محتاج أي حاجة، ابعتلنا هنا في أي وقت.";
                        }
                        else
                        {
                            Mark(c, id, "failed", "الزبون رفض الطلب، بس الإلغاء في Shopify مانجحش: " + err + ". الغيه بإيدك.");
                            reply = "وصلنا إنك عايز تلغي طلبك " + name + "، وهنتواصل معاك نأكد الإلغاء 🙏";
                        }
                    }
                    var token = WaAccount.Token(c, s);
                    if (token == null || string.IsNullOrEmpty(s.WaInstance)) return;
                    long? mine = WhatsAppBot.Save(c, s.Id, chatKey, false, reply, null);
                    try { await Wapilot.Send(token, s.WaInstance, chat, reply, "sb-oc-ans-" + id); }
                    catch (WaException ex)
                    {
                        Log.Error(ex);
                        if (mine.HasValue) using (var cmd = Db.Cmd(c, "DELETE FROM dbo.WaMessages WHERE Id=@id", "@id", mine.Value)) cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        // "1", "تأكيد", "ايوه", "✅" -> true; "2", "إلغاء", "❌" -> false; anything else (a question, a long message) -> null.
        // Cancelling is strict on purpose: a plain "لا" may answer something the assistant asked, and must not cancel an order.
        static readonly HashSet<string> Yes = new HashSet<string> { "1", "تاكيد", "تاكيد الطلب", "اكد", "اكد الطلب", "اكدت", "اكيد", "موكد", "موافق", "موافقه", "نعم", "ايوه", "ايوا",
            "اه", "تمام", "تمام اكد", "ايوه اكد", "اوكي", "ok", "okay", "yes", "confirm", "confirmed" };
        static readonly HashSet<string> No = new HashSet<string> { "2", "الغاء", "الغاء الطلب", "الغي", "الغي الطلب", "الغيه", "الغيه لو سمحت", "الغي لو سمحت", "رفض", "ارفض",
            "كنسل", "cancel", "cancel order" };
        public static bool? Decide(string text)
        {
            var t = ShopifyAdmin.ToLatinDigits(text ?? "").Trim();
            if (t.Length == 0 || t.Length > 40) return null;
            bool hasYes = t.Contains("✅") || t.Contains("👍") || t.Contains("✔"), hasNo = t.Contains("❌") || t.Contains("👎") || t.Contains("✖");
            if (hasYes != hasNo) return hasYes;
            t = t.ToLowerInvariant().Replace("ـ", "");
            t = Regex.Replace(t, "[ً-ْ]", "");                                  // tashkeel
            t = t.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي').Replace('ؤ', 'و');
            t = Regex.Replace(t, @"[^\p{L}\p{N}\s]", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            if (Yes.Contains(t)) return true;
            if (No.Contains(t)) return false;
            return null;
        }

        // a line for the assistant when the customer writes something else while an order waits for their answer
        public static string PendingNote(SqlConnection c, int storeId, string chatKey)
        {
            var phone = WaChat.Digits(chatKey);
            if (phone == null) return "";
            var id = Pending(c, storeId, phone);
            if (id == null) return "";
            using (var cmd = Db.Cmd(c, "SELECT OrderName FROM dbo.OrderConfirms WHERE Id=@id", "@id", id.Value))
            {
                var name = cmd.ExecuteScalar() as string;
                return "\n- الزبون ده عنده طلب " + name + " مستني تأكيده (اتبعتله رسالة بتفاصيله). لو كلامه معناه إنه موافق أو عايز يلغي، قوله يكتب 1 للتأكيد أو 2 للإلغاء. ماتقولش إن الطلب اتأكد أو اتلغى بنفسك.";
            }
        }

        // ---------- dashboard ----------
        public static List<Dictionary<string, object>> Recent(SqlConnection c, int storeId, int n = 15)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP (@n) OrderName, Customer, Total, Status, Note, CreatedAt, AnsweredAt FROM dbo.OrderConfirms WHERE StoreId=@s ORDER BY Id DESC", "@n", n, "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> {
                        { "order", r["OrderName"] as string }, { "customer", r["Customer"] as string ?? "" }, { "total", r["Total"] as string ?? "" },
                        { "status", r["Status"] as string }, { "note", r["Note"] as string ?? "" },
                        { "at", DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc).ToString("o") },
                        { "answeredAt", r["AnsweredAt"] == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)r["AnsweredAt"], DateTimeKind.Utc).ToString("o") } });
            return list;
        }

        // ---------- helpers ----------
        static void Mark(SqlConnection c, long id, string status, string note)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.OrderConfirms SET Status=@st, Note=@n WHERE Id=@id", "@st", status, "@n", Cut(note, 300), "@id", id)) cmd.ExecuteNonQuery();
        }

        static bool IsCod(JsonElement o)
        {
            var names = new List<string> { Str(o, "gateway") };
            JsonElement g;
            if (o.TryGetProperty("payment_gateway_names", out g) && g.ValueKind == JsonValueKind.Array)
                names.AddRange(g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
            return names.Any(n => n != null && Regex.IsMatch(n, @"cash|\bcod\b|on delivery|الاستلام|كاش", RegexOptions.IgnoreCase));
        }

        static readonly Dictionary<string, string> CallingCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "EG", "20" }, { "SA", "966" }, { "AE", "971" }, { "KW", "965" }, { "QA", "974" }, { "BH", "973" }, { "OM", "968" }, { "JO", "962" },
            { "LB", "961" }, { "IQ", "964" }, { "SY", "963" }, { "PS", "970" }, { "YE", "967" }, { "LY", "218" }, { "TN", "216" }, { "DZ", "213" },
            { "MA", "212" }, { "SD", "249" }, { "TR", "90" }, { "US", "1" }, { "CA", "1" }, { "GB", "44" }, { "DE", "49" }, { "FR", "33" } };

        // shipping phone as typed at checkout -> international digits ("01001234567" + EG -> "201001234567")
        public static string Phone(string raw, string country)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = ShopifyAdmin.ToLatinDigits(raw).Trim();
            bool intl = t.StartsWith("+") || t.StartsWith("00");
            var d = Regex.Replace(t, @"\D", "");
            if (t.StartsWith("00")) d = d.Substring(2);
            if (!intl)
            {
                string cc = null;
                if (country != null) CallingCodes.TryGetValue(country, out cc);
                if (cc != null && d.StartsWith("0")) d = cc + d.TrimStart('0');            // 0501234567 + SA -> 966501234567
                else if (cc != null && !d.StartsWith(cc) && d.Length <= 10) d = cc + d;     // 1001234567 + EG -> 201001234567
                else if (cc == null && d.Length == 11 && d.StartsWith("01")) d = "2" + d;   // Egyptian mobile, no country given
            }
            return d.Length >= 8 && d.Length <= 15 ? d : null;
        }

        static string Money(string raw, string currency)
        {
            decimal n;
            if (!decimal.TryParse(raw ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out n)) return null;
            var s = n.ToString(n == Math.Floor(n) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(currency) ? s : s + " " + currency;
        }

        static string First(params string[] v) { return v.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); }
        static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }
        static JsonElement? Obj(JsonElement e, string name) { JsonElement v; return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Object ? v : (JsonElement?)null; }
        static string Str(JsonElement? e, string name) { return e.HasValue ? Str(e.Value, name) : null; }
        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return null;
        }
    }
}
