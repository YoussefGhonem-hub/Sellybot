// SellyStore: paying by card, mobile wallet or installments through the merchant's OWN Paymob account. The money goes from the customer to
// the merchant at Paymob; it never passes through Sellybot, and Sellybot takes nothing from it. Paymob's Intention API with the merchant's
// secret key (Authorization: Token <secret>), the customer pays on Paymob's own page (unified checkout: the public key + the intention's
// client secret), and Paymob tells us on api/card-hook.ashx (the transaction processed callback, signed with the merchant's HMAC secret).
// The redirect back to the order's page never marks anything: only that callback, or the server asking Paymob about the transaction (with
// the merchant's API key, optional). An order waiting for its payment is «unpaid», its stock held; not paid in 30 minutes it is cancelled by
// itself (Run, with the background jobs) and the stock comes back. The moment an order becomes paid is Paid(); everything that follows a
// payment (by card, or a transfer the owner approved) is AfterPaid() — the place for what other features give when an order is paid.
//   ShopDigital.cs   products that are a file, a link or codes          shop/card.js        the store's side (checkout, order page)
//   shop-admin/card.js   the owner's side (payment settings, the order's payment and refund, digital products)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RiwaqChat
{
    public class CardInst { public string K; public int Id; public bool On; }
    // a store's payment settings (one row in ShopCardSet; the keys decrypted, in memory only)
    public class CardSet
    {
        public int StoreId, CardId, WalletId, DigiDays = 7, DigiMax = 5; public bool CardOn, DigiOn, Live; public decimal InstMin;
        public string Secret, Public, Hmac, Api, HookId, LastError; public DateTime? WorkedAt, LinkedAt;
        public Dictionary<string, object> Last4 = new Dictionary<string, object>(); public List<CardInst> Inst = new List<CardInst>();
        public bool Linked { get { return !string.IsNullOrEmpty(Secret) && !string.IsNullOrEmpty(Public) && !string.IsNullOrEmpty(Hmac); } }
        public bool Open { get { return CardOn && Linked; } }
        public int IdOf(string way) { if (way == "card") return CardId; if (way == "wallet") return WalletId; var i = Inst.FirstOrDefault(x => x.K == way && x.On); return i == null ? 0 : i.Id; }
        public IEnumerable<string> Ways { get { return new[] { "card", "wallet" }.Concat(ShopCard.Companies.Select(x => x[0])).Where(w => IdOf(w) > 0); } }
    }
    public class CardException : Exception { public readonly bool Unsure, BadKey; public CardException(string m, bool unsure = false, bool badKey = false) : base(m) { Unsure = unsure; BadKey = badKey; } }

    public static partial class ShopCard
    {
        public const int PayMinutes = 30, TryMinutes = 25;
        // the installment companies a merchant may have on their Paymob account: key, Arabic, English
        public static readonly string[][] Companies = { new[] { "valu", "valU", "valU" }, new[] { "souhoola", "سهولة", "Souhoola" }, new[] { "sympl", "سيمبل", "Sympl" }, new[] { "aman", "أمان", "Aman" }, new[] { "forsa", "فورسا", "Forsa" }, new[] { "halan", "حالا", "Halan" }, new[] { "contact", "كونتكت", "Contact" }, new[] { "premium", "بريميوم", "Premium" } };
        public static bool IsWay(string k) { return k == "card" || k == "wallet" || Companies.Any(x => x[0] == k); }
        public static bool IsInst(string k) { return Companies.Any(x => x[0] == k); }
        // a way's name for the customer and the owner; null = not one of ours
        public static string Name(string k, bool en = false)
        {
            if (k == "card") return en ? "Card" : "بالكارت";
            if (k == "wallet") return en ? "Mobile wallet (online)" : "محفظة موبايل (أونلاين)";
            var c = Companies.FirstOrDefault(x => x[0] == k); return c == null ? null : en ? "Installments with " + c[2] : "بالتقسيط مع " + c[1];
        }
        static string T(ShopFrontHandler.Ctx x, string ar, string en) { return x.Lang == "en" ? en : ar; }
        static string Str(Dictionary<string, object> b, string k, int n) { return ShopJson.Str(ShopJson.At(b, k), n); }
        static object Nz(string s) { return string.IsNullOrWhiteSpace(s) ? null : s; }
        internal static void Exec(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) cmd.ExecuteNonQuery(); }
        internal static int Num(SqlConnection c, string sql, params object[] a) { using (var cmd = Db.Cmd(c, sql, a)) { var v = cmd.ExecuteScalar(); return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v); } }
        internal static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        static void Ok(HttpResponse res, params object[] kv) { var d = D(kv); d["ok"] = true; Web.Json(res, 200, d); }
        static string Iso(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc).ToString("o") : ""; }
        static DateTime? Dt(object v) { return v is DateTime ? DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc) : (DateTime?)null; }
        static long Cents(decimal egp) { return (long)Math.Round(egp * 100m, MidpointRounding.AwayFromZero); }
        static string Last(string s) { return string.IsNullOrEmpty(s) ? "" : s.Length > 4 ? s.Substring(s.Length - 4) : s; }

        public const string Schema = @"
IF OBJECT_ID('dbo.ShopCardSet') IS NULL
BEGIN
  CREATE TABLE dbo.ShopCardSet(StoreId INT NOT NULL PRIMARY KEY REFERENCES dbo.Stores(Id) ON DELETE CASCADE, CardOn BIT NOT NULL DEFAULT 0, DigiOn BIT NOT NULL DEFAULT 0,
    SecretBox NVARCHAR(MAX) NULL, PublicBox NVARCHAR(MAX) NULL, HmacBox NVARCHAR(MAX) NULL, ApiBox NVARCHAR(MAX) NULL, Last4 NVARCHAR(200) NULL, Live BIT NOT NULL DEFAULT 0,
    CardId INT NULL, WalletId INT NULL, Inst NVARCHAR(2000) NULL, InstMin DECIMAL(12,2) NOT NULL DEFAULT 0, HookId NVARCHAR(40) NULL,
    LinkedAt DATETIME2 NULL, WorkedAt DATETIME2 NULL, LastError NVARCHAR(300) NULL, LastErrorAt DATETIME2 NULL, DigiDays INT NOT NULL DEFAULT 7, DigiMax INT NOT NULL DEFAULT 5);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopCardSet_Hook ON dbo.ShopCardSet(HookId) WHERE HookId IS NOT NULL;';
END
-- one row per intention (a try): our reference at Paymob is ss<store>-<order no>-<try>. State: new, made, pending, failed, paid
IF OBJECT_ID('dbo.ShopCardPays') IS NULL
BEGIN
  CREATE TABLE dbo.ShopCardPays(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, Try INT NOT NULL, Ref NVARCHAR(50) NOT NULL,
    Way NVARCHAR(10) NOT NULL, Amount BIGINT NOT NULL, IntentId NVARCHAR(80) NULL, Secret NVARCHAR(300) NULL, PmOrder BIGINT NULL, State NVARCHAR(8) NOT NULL DEFAULT 'new', Pre BIT NOT NULL DEFAULT 0,
    Txn NVARCHAR(40) NULL, Kind NVARCHAR(20) NULL, Pan NVARCHAR(8) NULL, Err NVARCHAR(300) NULL, Ip NVARCHAR(64) NULL, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), DoneAt DATETIME2 NULL,
    TellAt DATETIME2 NULL, ToldAt DATETIME2 NULL);
  CREATE UNIQUE INDEX UX_ShopCardPays_Ref ON dbo.ShopCardPays(StoreId, Ref);
  CREATE UNIQUE INDEX UX_ShopCardPays_Try ON dbo.ShopCardPays(OrderId, Try);
  CREATE INDEX IX_ShopCardPays_Tell ON dbo.ShopCardPays(State, TellAt);
END
-- money given back through Paymob. Kind: void / refund. State: making, done, failed, unsure (no answer: the owner or Paymob's answer settles it)
IF OBJECT_ID('dbo.ShopCardRefunds') IS NULL
BEGIN
  CREATE TABLE dbo.ShopCardRefunds(Id BIGINT IDENTITY(1,1) PRIMARY KEY, StoreId INT NOT NULL REFERENCES dbo.Stores(Id) ON DELETE CASCADE, OrderId BIGINT NOT NULL, PayId BIGINT NOT NULL,
    Amount BIGINT NOT NULL, Kind NVARCHAR(6) NOT NULL, State NVARCHAR(8) NOT NULL DEFAULT 'making', PayTxn NVARCHAR(40) NULL, Txn NVARCHAR(40) NULL, Err NVARCHAR(300) NULL, Ck NVARCHAR(60) NULL, RetId INT NULL, Auto BIT NOT NULL DEFAULT 0,
    Who NVARCHAR(10) NOT NULL DEFAULT 'owner', CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), DoneAt DATETIME2 NULL);
  CREATE INDEX IX_ShopCardRefunds_Order ON dbo.ShopCardRefunds(OrderId);
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopCardRefunds_Ck ON dbo.ShopCardRefunds(StoreId, Ck) WHERE Ck IS NOT NULL;';
  EXEC sp_executesql N'CREATE UNIQUE INDEX UX_ShopCardRefunds_Ret ON dbo.ShopCardRefunds(RetId) WHERE RetId IS NOT NULL;';
END
" + ShopDigital.Schema;

        // ---------- the settings, kept per store and read again when the catalog is (ShopCache.Load -> Load) ----------
        static readonly ConcurrentDictionary<int, CardSet> Sets = new ConcurrentDictionary<int, CardSet>();
        public static void Load(SqlConnection c, ShopData d) { var k = Read(c, d.Row.StoreId); Sets[d.Row.StoreId] = k; ShopDigital.Load(c, d, k); }
        public static CardSet Of(int storeId) { CardSet k; if (Sets.TryGetValue(storeId, out k)) return k; try { using (var c = Db.Open()) { k = Read(c, storeId); Sets[storeId] = k; return k; } } catch (Exception ex) { Log.Error(ex); return new CardSet { StoreId = storeId }; } }
        static CardSet Read(SqlConnection c, int storeId)
        {
            var k = new CardSet { StoreId = storeId };
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopCardSet WHERE StoreId=@s", "@s", storeId)) using (var r = cmd.ExecuteReader())
                if (r.Read())
                {
                    k.CardOn = (bool)r["CardOn"]; k.DigiOn = (bool)r["DigiOn"]; k.Live = (bool)r["Live"]; k.Secret = Secrets.Unprotect(r["SecretBox"] as string); k.Public = Secrets.Unprotect(r["PublicBox"] as string); k.Hmac = Secrets.Unprotect(r["HmacBox"] as string); k.Api = Secrets.Unprotect(r["ApiBox"] as string);
                    k.Last4 = ShopJson.Obj(r["Last4"] as string); k.CardId = r["CardId"] as int? ?? 0; k.WalletId = r["WalletId"] as int? ?? 0; k.InstMin = (decimal)r["InstMin"]; k.HookId = r["HookId"] as string; k.LastError = r["LastError"] as string;
                    k.LinkedAt = Dt(r["LinkedAt"]); k.WorkedAt = Dt(r["WorkedAt"]); k.DigiDays = (int)r["DigiDays"]; k.DigiMax = (int)r["DigiMax"];
                    k.Inst = ShopJson.Arr(r["Inst"] as string).OfType<Dictionary<string, object>>().Select(x => new CardInst { K = ShopJson.Str(ShopJson.At(x, "k"), 10), Id = ShopJson.Int(ShopJson.At(x, "id")), On = ShopJson.Bool(ShopJson.At(x, "on")) }).Where(x => IsInst(x.K) && x.Id > 0).ToList();
                }
            return k;
        }
        static void Saved(SqlConnection c, int storeId) { Sets[storeId] = Read(c, storeId); Shops.Bump(c, storeId); }

        // ---------- what the rest of the store asks ----------
        // the store takes this way now (null = any of ours)
        public static bool Has(Shop sh, string way) { if (sh == null || (way != null && !IsWay(way))) return false; var k = Of(sh.StoreId); return k.Open && (way == null ? k.Ways.Any() : k.IdOf(way) > 0); }
        public static bool Digital(Shop sh) { return sh != null && Of(sh.StoreId).DigiOn; }
        // the order waits for Paymob: no alert, no confirmation until it is paid
        public static bool Holds(ShopOrder o) { return o != null && IsWay(o.Pay) && o.Deposit > 0 && o.PayState != "paid"; }

        // before an order is saved (ShopOrders.Place): a digital line is paid first, installments above the owner's minimum
        public static void Check(SqlConnection c, Shop row, ShopQuote q, string lang)
        {
            bool en = lang == "en";
            if (q.Pay == "cod" && q.Lines.Any(l => l.P != null && l.P.Digi != null))
                throw new ShopException("digital_pay", en ? "Digital products are paid first: by card or by a transfer." : "المنتجات الرقمية لازم تتدفع الأول: بالكارت أو بتحويل.");
            if (IsInst(q.Pay) && q.Total < Of(row.StoreId).InstMin)
                throw new ShopException("inst_min", en ? "Installments are for orders from EGP " + Of(row.StoreId).InstMin.ToString("#,0.##", CultureInfo.InvariantCulture) + "." : "التقسيط للطلبات من " + ShopOrders.Money(Of(row.StoreId).InstMin) + ".");
        }
        // right after an order from the store's page is saved (ShopActions.Order): a card order waits for its payment; the e-mail for Paymob and the digital links
        public static void Placed(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, ShopOrders.Placed p)
        {
            if (p == null || p.Again || p.Order == null || p.Order.Test) return; var o = p.Order;
            string email = Str(b, "email", 120); if (!Web.ValidEmail(email)) email = null;
            if (email != null && (IsWay(o.Pay) || o.Items.Any(i => { ShopProd pr; return x.Data.ById.TryGetValue(i.ProductId, out pr) && pr.Digi != null; })))
            { var ex = new Dictionary<string, object>(o.Extras); ex["email"] = email; Exec(c, "UPDATE dbo.ShopOrders SET Extras=@e WHERE Id=@i AND StoreId=@s", "@e", ShopJson.Write(ex), "@i", o.Id, "@s", o.StoreId); }
            if (IsWay(o.Pay) && o.Deposit > 0)
            {
                Exec(c, "UPDATE dbo.ShopOrders SET Status='unpaid', PayState='card', Seen=1 WHERE Id=@i AND StoreId=@s AND Status='new' AND PayState='wait'", "@i", o.Id, "@s", o.StoreId);
                o.Status = "unpaid"; o.PayState = "card";
            }
        }
        // the store's script: the ways the customer can pay online (null = nothing of this feature on the page)
        public static object FrontJson(SqlConnection c, ShopFrontHandler.Ctx x)
        {
            var k = Of(x.Row.StoreId); if (!k.Open && !k.DigiOn) return null;
            return D("ways", k.Open ? k.Ways.Select(w => D("k", w, "n", ShopView.Pair(Name(w), Name(w, true)), "min", IsInst(w) ? k.InstMin : 0)).ToArray() : new object[0], "digi", k.DigiOn, "test", k.Open && !k.Live, "mins", PayMinutes,
                "v", ShopPages.Ver("card.js"));
        }
        // the page loads shop/card.js only when there is something for it
        public static string Tag(Dictionary<string, object> shop) { return shop.ContainsKey("card") && shop["card"] != null ? "<script src=\"/_s/card.js?v=" + ShopPages.Ver("card.js") + "\" defer></script>" : ""; }
        // what the assistant knows (one line in its store information)
        public static string BotNote(Shop sh, ShopData d)
        {
            var k = Of(sh.StoreId); var sb = new StringBuilder();
            if (k.Open && k.Ways.Any()) { var w = k.Ways.ToList(); sb.Append("الدفع أونلاين من صفحة الطلب على Paymob: ").Append(string.Join("، ", w.Select(x => Name(x)))).Append(w.Any(IsInst) && k.InstMin > 0 ? " (التقسيط للطلبات من " + ShopOrders.Money(k.InstMin) + "، وخطط التقسيط بتظهر في صفحة Paymob)" : "").Append(". الطلب لازم يتدفع خلال ").Append(PayMinutes).Append(" دقيقة وإلا بيتلغى لوحده.\n"); }
            var dg = d.Products.Where(p => p.Digi != null && p.Shown).Take(12).ToList();
            if (k.DigiOn && dg.Count > 0) sb.Append("منتجات رقمية (مفيش شحن، بتوصل لينك بعد الدفع على الواتساب وفي صفحة الطلب، ومابترجعش بعد التحميل): ").Append(string.Join("، ", dg.Select(p => p.Name))).Append(". بتتطلب من صفحتها في المتجر بس، مش من الشات.\n");
            return sb.ToString();
        }

        // ---------- Paymob ----------
        static string Base { get { return (Web.Setting("PaymobBase") ?? "https://accept.paymob.com").TrimEnd('/'); } }
        // docs (developers.paymob.com, unified checkout redirection): https://eg.checkout.paymob.com/?publicKey=...&clientSecret=...
        static string Checkout { get { return Web.Setting("PaymobCheckout") ?? "https://eg.checkout.paymob.com/"; } }
        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; int sec; if (!int.TryParse(Web.Setting("CardTimeoutSec"), out sec) || sec < 1) sec = 20;   // a setting only for tests
            var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(sec), MaxResponseContentBufferSize = 4000000 }; h.DefaultRequestHeaders.UserAgent.ParseAdd("Sellybot/1.0"); return h;
        }
        class Reply { public int Status; public string Text; public object Root; public Dictionary<string, object> Body { get { return Root as Dictionary<string, object> ?? new Dictionary<string, object>(); } } public bool Good { get { return Status >= 200 && Status < 300; } } }
        static async Task<Reply> Call(HttpMethod m, string path, string auth, object body)
        {
            var q = new HttpRequestMessage(m, Base + path); if (auth != null) q.Headers.TryAddWithoutValidation("Authorization", auth);
            if (body != null) q.Content = new StringContent(ShopJson.Write(body), Encoding.UTF8, "application/json");
            HttpResponseMessage r;
            try { r = await Http.SendAsync(q).ConfigureAwait(false); }
            catch (Exception ex)
            {
                var w = (ex as HttpRequestException)?.InnerException as WebException; bool never = w != null && (w.Status == WebExceptionStatus.ConnectFailure || w.Status == WebExceptionStatus.NameResolutionFailure);
                Log.Error(new Exception("Paymob " + m + " " + path + ": " + (w == null ? ex.GetType().Name : w.Status.ToString())));
                throw new CardException("Paymob مش بترد دلوقتي.", unsure: !never);
            }
            using (r)
            {
                var o = new Reply { Status = (int)r.StatusCode };
                try { o.Text = await r.Content.ReadAsStringAsync().ConfigureAwait(false); } catch (Exception) { throw new CardException("Paymob قطعت في نص الرد.", unsure: true); }
                if (o.Text.Length > 0 && (o.Text[0] == '{' || o.Text[0] == '[')) try { o.Root = Web.Js.DeserializeObject(o.Text); } catch { }
                if (o.Status == 401 || o.Status == 403) throw new CardException("Paymob مش قابلة المفتاح.", badKey: true);
                return o;
            }
        }
        static string Said(Reply r) { var b = r.Body; foreach (var k in new[] { "detail", "message", "error" }) { var v = ShopJson.Str(ShopJson.At(b, k), 200); if (v.Length > 0) return v; } var dm = ShopJson.Str(ShopJson.At(b, "data", "message"), 200); return dm.Length > 0 ? dm : "HTTP " + r.Status; }

        // a new intention for one try; null fields = Paymob didn't give them
        class Made { public string Id, Secret; public long PmOrder; }
        static async Task<Made> Intent(CardSet k, Shop sh, ShopOrder o, string way, long cents, string reference, string backUrl, string hookUrl)
        {
            var parts = (o.Name ?? "").Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries); string email = ShopJson.Str(o.Extras.ContainsKey("email") ? o.Extras["email"] : null, 120);
            if (!Web.ValidEmail(email)) email = "order-" + o.No + "@" + sh.Slug + "." + Shops.Domain;   // Paymob asks for one; the customer didn't give it
            Func<string, string> na = s => string.IsNullOrWhiteSpace(s) ? "NA" : (s.Length > 90 ? s.Substring(0, 90) : s);
            var body = D("amount", cents, "currency", "EGP", "payment_methods", new[] { k.IdOf(way) },
                "items", new[] { D("name", na((o.Lang == "en" && !string.IsNullOrEmpty(sh.NameEn) ? sh.NameEn : sh.Name) + " #" + o.No), "amount", cents, "description", na(string.Join("، ", o.Items.Take(5).Select(i => i.Name + (i.Qty > 1 ? " × " + i.Qty : "")))), "quantity", 1) },
                "billing_data", D("first_name", na(parts.Length > 0 ? parts[0] : ""), "last_name", na(parts.Length > 1 ? parts[1] : parts.Length > 0 ? parts[0] : ""), "phone_number", "+2" + ShopOrders.Local(o.Phone), "email", email,
                    "street", na(o.Address), "building", "NA", "floor", "NA", "apartment", "NA", "city", na(o.City), "state", na(ShopGeo.Name(o.Gov)), "country", "EG"),
                "special_reference", reference, "notification_url", hookUrl, "redirection_url", backUrl, "extras", D("store", sh.Slug, "order", o.No));
            var r = await Call(HttpMethod.Post, "/v1/intention/", "Token " + k.Secret, body).ConfigureAwait(false);
            if (!r.Good) throw new CardException(r.Status == 404 ? "رقم الـ Integration ده مش موجود في حساب Paymob، أو مش من نفس النوع (تجربة / Live) زي المفتاح." : "Paymob رفضت: " + Said(r));
            var m = new Made { Id = ShopJson.Str(ShopJson.At(r.Body, "id"), 80), Secret = ShopJson.Str(ShopJson.At(r.Body, "client_secret"), 300) }; long po; long.TryParse(ShopJson.Str(ShopJson.At(r.Body, "intention_order_id"), 20), out po); m.PmOrder = po;
            if (m.Secret.Length < 10) throw new CardException("Paymob مارجّعتش بيانات الدفع.");
            return m;
        }
        // ---------- asking Paymob (the merchant's API key, optional) ----------
        static readonly ConcurrentDictionary<int, KeyValuePair<string, DateTime>> Tokens = new ConcurrentDictionary<int, KeyValuePair<string, DateTime>>();
        static async Task<string> Bearer(CardSet k)
        {
            KeyValuePair<string, DateTime> t; if (Tokens.TryGetValue(k.StoreId, out t) && t.Value > DateTime.UtcNow) return t.Key;
            var r = await Call(HttpMethod.Post, "/api/auth/tokens", null, D("api_key", k.Api)).ConfigureAwait(false); var tok = ShopJson.Str(ShopJson.At(r.Body, "token"), 2000);
            if (!r.Good || tok.Length < 10) throw new CardException("الـ API Key مش شغال.", badKey: true);
            Tokens[k.StoreId] = new KeyValuePair<string, DateTime>(tok, DateTime.UtcNow.AddMinutes(45)); return tok;
        }
        // Paymob's own record of a transaction (by its id, or the newest one of our reference); null = none
        static async Task<Dictionary<string, object>> Ask(CardSet k, string txn, string reference)
        {
            if (string.IsNullOrEmpty(k.Api)) return null; var tok = "Bearer " + await Bearer(k).ConfigureAwait(false);
            var r = txn != null ? await Call(HttpMethod.Get, "/api/acceptance/transactions/" + Uri.EscapeDataString(txn), tok, null).ConfigureAwait(false) : await Call(HttpMethod.Post, "/api/ecommerce/orders/transaction_inquiry", tok, D("merchant_order_id", reference)).ConfigureAwait(false);
            if (!r.Good) return null;
            var list = r.Root is object[] ? ((object[])r.Root).OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>> { r.Body };
            return list.Where(t => t.ContainsKey("id")).OrderByDescending(t => ShopJson.Bool(ShopJson.At(t, "success"))).FirstOrDefault();
        }

        // ---------- HMAC (developers.paymob.com: HMAC transaction callback) ----------
        // the values of these keys, in this order, joined; HMAC-SHA512 with the merchant's HMAC secret, lowercase hex. GET (the redirect) has id / order_id flat.
        static readonly string[] Keys = { "amount_cents", "created_at", "currency", "error_occured", "has_parent_transaction", "id", "integration_id", "is_3d_secure", "is_auth", "is_capture", "is_refunded", "is_standalone_payment", "is_voided", "order.id", "owner", "pending", "source_data.pan", "source_data.sub_type", "source_data.type", "success" };
        static string Val(object v) { return v == null ? "" : v is bool ? ((bool)v ? "true" : "false") : Convert.ToString(v, CultureInfo.InvariantCulture); }
        public static string Hmac(Dictionary<string, object> obj, string secret)
        {
            var sb = new StringBuilder(); foreach (var key in Keys) sb.Append(Val(ShopJson.At(obj, key.Split('.'))));
            using (var h = new HMACSHA512(Encoding.UTF8.GetBytes(secret ?? ""))) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(x => x.ToString("x2")));
        }
        static bool Same(string a, string b) { return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && Secrets.Same(Encoding.UTF8.GetBytes(a.ToLowerInvariant()), Encoding.UTF8.GetBytes(b.ToLowerInvariant())); }
        // a successful payment, as Paymob says it (not pending, not a refund or a void of another transaction, not only held on the card)
        static bool Success(Dictionary<string, object> t) { Func<string, bool> b = k => ShopJson.Bool(ShopJson.At(t, k)); return b("success") && !b("pending") && !b("is_voided") && !b("is_refunded") && !b("has_parent_transaction") && !b("is_auth") && !b("error_occured"); }
        static string RefOf(Dictionary<string, object> t) { return ShopJson.Str(ShopJson.At(t, "order", "merchant_order_id"), 60); }

        // ---------- a try's row ----------
        class Pay { public long Id, OrderId, Amount, PmOrder; public int StoreId, Try; public string Ref, Way, State, Secret, Txn, Kind, Pan, Err; public bool Pre; public DateTime CreatedAt; public DateTime? DoneAt; }
        static List<Pay> Pays(SqlConnection c, string where, params object[] a)
        {
            var l = new List<Pay>(); using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopCardPays WHERE " + where, a)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Pay { Id = Convert.ToInt64(r["Id"]), StoreId = (int)r["StoreId"], OrderId = Convert.ToInt64(r["OrderId"]), Try = (int)r["Try"], Ref = (string)r["Ref"], Way = (string)r["Way"], Amount = Convert.ToInt64(r["Amount"]), PmOrder = r["PmOrder"] is long ? (long)r["PmOrder"] : 0, State = (string)r["State"], Secret = r["Secret"] as string, Txn = r["Txn"] as string, Kind = r["Kind"] as string, Pan = r["Pan"] as string, Err = r["Err"] as string, Pre = (bool)r["Pre"], CreatedAt = Dt(r["CreatedAt"]).Value, DoneAt = Dt(r["DoneAt"]) });
            return l;
        }
        static string HookUrl(CardSet k) { string origin = Web.Setting("CardHookBase") ?? EasyOrders.Origin(HttpContext.Current == null ? null : HttpContext.Current.Request); return origin.TrimEnd('/') + "/api/card-hook.ashx?s=" + k.HookId; }
        // the owner hears once a day that Paymob refused (a wrong id, a key that stopped working)
        static void Fault(SqlConnection c, CardSet k, string why)
        {
            Exec(c, "UPDATE dbo.ShopCardSet SET LastError=@e, LastErrorAt=SYSUTCDATETIME() WHERE StoreId=@s", "@e", why.Length > 300 ? why.Substring(0, 300) : why, "@s", k.StoreId); k.LastError = why;
            if (Web.Allow("card-fault:" + k.StoreId, 1, TimeSpan.FromHours(24))) ShopHooks.Tell(k.StoreId, "pay", "⚠️ *الدفع بالكارت في متجرك وقف*\n" + why + "\n\nافتح «متجري» ثم «الدفع والفروع» وراجع بيانات Paymob.", "sb-card-fault-" + k.StoreId + "-" + DateTime.UtcNow.ToString("yyyyMMdd"));
        }

        // ================= the customer: POST /_a/card {do, tok} =================
        public static void Front(SqlConnection c, ShopFrontHandler.Ctx x, Dictionary<string, object> b, HttpResponse res)
        {
            string a = Str(b, "do", 10);
            if (!Web.Allow("card-a:" + x.Ip, 120, TimeSpan.FromMinutes(10))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            var o = ShopOrders.ByToken(c, x.Row.StoreId, Regex.Replace(Str(b, "tok", 40), "[^a-f0-9]", ""));
            if (o == null || o.Test || Str(b, "tok", 40).Length != 32) { Web.Allow("card-bad:" + x.Ip, 1, TimeSpan.FromSeconds(1)); throw new ShopException("no_order", T(x, "الطلب ده مش موجود.", "This order isn't here.")); }
            var k = Of(x.Row.StoreId);
            if (a == "state") { Web.Json(res, 200, State(c, x, k, o)); return; }
            if (x.Preview) throw new ShopException("preview", T(x, "المعاينة مابتحفظش حاجة.", "The preview saves nothing."));
            if (a == "go") { Go(c, x, k, o, Str(b, "way", 10), res); return; }
            if (a == "check") { Check(c, x, k, o, Regex.Replace(Str(b, "id", 30), @"\D", "")); Web.Json(res, 200, State(c, x, k, ShopOrders.ById(c, o.StoreId, o.Id))); return; }
            if (a == "cod") { ToCod(c, x, k, o); Web.Json(res, 200, State(c, x, k, ShopOrders.ById(c, o.StoreId, o.Id))); return; }
            Web.Error(res, 404, "not_found");
        }
        static bool Digi(ShopFrontHandler.Ctx x, ShopOrder o) { return o.Items.Any(i => { ShopProd p; return x.Data.ById.TryGetValue(i.ProductId, out p) && p.Digi != null; }); }
        // a COD order not paid yet may be paid by card from its page (the chat's orders): no fee for cash on it, nothing sent before
        static bool PreOk(CardSet k, ShopOrder o) { return k.Open && o.Pay == "cod" && string.IsNullOrEmpty(o.PayState) && o.Paid == 0 && o.Deposit == 0 && o.Fees == 0 && o.Total > 0 && (o.Status == "new" || o.Status == "noreply" || o.Status == "confirmed") && !o.Extras.ContainsKey("branch"); }
        static int MinsLeft(ShopOrder o) { return Math.Max(0, PayMinutes - (int)Math.Floor((DateTime.UtcNow - o.CreatedAt).TotalMinutes)); }
        static Dictionary<string, object> State(SqlConnection c, ShopFrontHandler.Ctx x, CardSet k, ShopOrder o)
        {
            var tries = Pays(c, "OrderId=@o AND StoreId=@s ORDER BY Try DESC", "@o", o.Id, "@s", o.StoreId); var last = tries.FirstOrDefault(); bool waiting = o.Status == "unpaid" && o.PayState == "card", pre = PreOk(k, o);
            var ways = k.Open ? k.Ways.Where(w => !IsInst(w) || o.Total >= k.InstMin).ToList() : new List<string>();
            bool cod = waiting && ShopPrice.Cod(x.Row) && !Digi(x, o) && !ShopJson.Bool(ShopJson.At(ShopJson.Sub(ShopJson.Sub(x.Row.Settings, "pay"), "deposit"), "on"));
            decimal codFee = cod && !o.Extras.ContainsKey("branch") ? ShopJson.Dec(ShopJson.At(ShopJson.Sub(x.Row.Settings, "order"), "codFee")) : 0;
            return D("ok", true, "no", o.No, "status", o.Status, "pay", o.Pay, "ps", o.PayState ?? "", "total", o.Total, "paid", o.Paid, "test", k.Open && !k.Live,
                "wait", waiting, "mins", waiting ? MinsLeft(o) : 0, "can", (waiting && (DateTime.UtcNow - o.CreatedAt).TotalMinutes < TryMinutes) || pre, "pre", pre, "ways", waiting || pre ? ways.Select(w => D("k", w, "n", ShopView.Pair(Name(w), Name(w, true)))).ToArray() : new object[0],
                "cod", cod, "codFee", codFee, "last", last == null ? null : D("state", last.State, "way", last.Way, "at", Iso(last.CreatedAt)), "timeout", o.Status == "cancelled" && o.PayState == "card" && o.Paid == 0 && (o.CancelReason ?? "").StartsWith("مادفعش", StringComparison.Ordinal) && tries.All(t => t.State != "paid"),
                "dg", ShopDigital.CustomerLinks(c, x, o));
        }
        // a new try (or the one just made, when it's pressed again): the address of Paymob's page
        static void Go(SqlConnection c, ShopFrontHandler.Ctx x, CardSet k, ShopOrder o, string way, HttpResponse res)
        {
            bool waiting = o.Status == "unpaid" && o.PayState == "card", pre = !waiting && PreOk(k, o);
            if (!k.Open) throw new ShopException("off", T(x, "الدفع بالكارت مش متاح دلوقتي في المتجر ده.", "Card payment isn't available in this store right now."));
            if (!waiting && !pre) throw new ShopException("no_pay", o.PayState == "paid" ? T(x, "الطلب ده اتدفع خلاص.", "This order is already paid.") : T(x, "الطلب ده مش مستني دفع.", "This order isn't waiting for a payment."));
            if (waiting && (DateTime.UtcNow - o.CreatedAt).TotalMinutes >= TryMinutes) throw new ShopException("late", T(x, "وقت الدفع للطلب ده خلص. اعمل طلب جديد.", "The time to pay this order has ended. Please place a new order."));
            if (!IsWay(way) || k.IdOf(way) <= 0) way = IsWay(o.Pay) && k.IdOf(o.Pay) > 0 ? o.Pay : k.Ways.First();
            if (IsInst(way) && o.Total < k.InstMin) throw new ShopException("inst_min", T(x, "التقسيط للطلبات من " + ShopOrders.Money(k.InstMin) + ".", "Installments are for orders from EGP " + k.InstMin.ToString("#,0.##", CultureInfo.InvariantCulture) + "."));
            if (!Web.Allow("card-go:" + o.Id, 12, TimeSpan.FromMinutes(30)) || !Web.Allow("card-goi:" + x.Ip, 30, TimeSpan.FromHours(1))) throw new ShopException("too_many", T(x, "محاولات كتير. استنى شوية.", "Too many tries. Please wait a while."));
            // what is still due: a part paid another way (a gift card) stays paid
            long cents = Cents(o.Total - o.Paid); if (cents <= 0) throw new ShopException("no_pay", T(x, "الطلب ده اتدفع خلاص.", "This order is already paid."));
            // the same way pressed again within a few minutes: the same page at Paymob
            var again = Pays(c, "OrderId=@o AND StoreId=@s AND State='made' AND Way=@w AND Amount=@a AND CreatedAt>DATEADD(MINUTE,-20,SYSUTCDATETIME()) ORDER BY Try DESC", "@o", o.Id, "@s", o.StoreId, "@w", way, "@a", cents).FirstOrDefault();
            if (again != null && again.Secret != null) { Ok(res, "url", Url(k, again.Secret)); return; }
            int n = Num(c, "SELECT ISNULL(MAX(Try),0) FROM dbo.ShopCardPays WHERE OrderId=@o", "@o", o.Id) + 1; string reference = "ss" + o.StoreId + "-" + o.No + "-" + n; long id;
            try { using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopCardPays(StoreId, OrderId, Try, Ref, Way, Amount, Pre, Ip) OUTPUT INSERTED.Id VALUES(@s, @o, @t, @r, @w, @a, @p, @ip)", "@s", o.StoreId, "@o", o.Id, "@t", n, "@r", reference, "@w", way, "@a", cents, "@p", pre, "@ip", x.Ip)) id = Convert.ToInt64(cmd.ExecuteScalar()); }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { throw new ShopException("busy", T(x, "بنجهّز صفحة الدفع. جرّب تاني بعد ثانية.", "The payment page is being prepared. Try again in a second.")); }
            Made m;
            try { m = Task.Run(() => Intent(k, x.Row, o, way, cents, reference, x.Row.Url + x.Prefix + "/thanks/" + o.Token + "?pm=1", HookUrl(k))).GetAwaiter().GetResult(); }
            catch (CardException ex)
            {
                Exec(c, "UPDATE dbo.ShopCardPays SET State='failed', Err=@e WHERE Id=@i", "@e", ex.Message.Length > 300 ? ex.Message.Substring(0, 300) : ex.Message, "@i", id);
                if (!ex.Unsure) Fault(c, k, ex.Message);
                throw new ShopException("paymob", T(x, "الدفع أونلاين مش شغال دلوقتي. جرّب تاني بعد شوية" + (waiting && ShopPrice.Cod(x.Row) && !Digi(x, o) ? "، أو ادفع كاش عند الاستلام." : "."), "Online payment isn't working right now. Please try again in a while."));
            }
            Exec(c, "UPDATE dbo.ShopCardPays SET State='made', IntentId=@n, Secret=@cs, PmOrder=@po WHERE Id=@i AND State='new'", "@n", Nz(m.Id), "@cs", m.Secret, "@po", m.PmOrder > 0 ? (object)m.PmOrder : null, "@i", id);
            if (waiting && way != o.Pay) Exec(c, "UPDATE dbo.ShopOrders SET Pay=@w WHERE Id=@i AND StoreId=@s AND PayState='card'", "@w", way, "@i", o.Id, "@s", o.StoreId);
            ShopOrders.Log_(c, null, o.StoreId, o.Id, "customer", "فتح صفحة الدفع على Paymob (" + Name(way) + "، محاولة " + n + ")");
            Ok(res, "url", Url(k, m.Secret));
        }
        static string Url(CardSet k, string secret) { return Checkout + (Checkout.Contains("?") ? "&" : "?") + "publicKey=" + Uri.EscapeDataString(k.Public) + "&clientSecret=" + Uri.EscapeDataString(secret); }
        // back from Paymob: what Paymob itself says about that transaction (only with the API key); the address alone proves nothing
        static void Check(SqlConnection c, ShopFrontHandler.Ctx x, CardSet k, ShopOrder o, string txn)
        {
            if (string.IsNullOrEmpty(k.Api) || txn.Length == 0 || o.PayState == "paid" || !Web.Allow("card-chk:" + o.Id, 8, TimeSpan.FromMinutes(10))) return;
            Dictionary<string, object> t; try { t = Task.Run(() => Ask(k, txn, null)).GetAwaiter().GetResult(); } catch (CardException) { return; }
            if (t != null) Apply(c, k, t, "ask");
        }
        // the customer chose cash on delivery instead (the store allows it, nothing digital): the order goes on like any other
        static void ToCod(SqlConnection c, ShopFrontHandler.Ctx x, CardSet k, ShopOrder o)
        {
            var st = State(c, x, k, o); if (!(bool)st["cod"]) throw new ShopException("no_cod", T(x, "الدفع عند الاستلام مش متاح للطلب ده.", "Cash on delivery isn't available for this order."));
            decimal fee = (decimal)st["codFee"]; var fees = ShopJson.Arr(o.FeesJson); if (fee > 0) fees.Add(new object[] { "cod", ShopView.Pair("رسوم الدفع عند الاستلام", "Cash on delivery fee"), fee });
            int n; using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET Pay='cod', PayState=NULL, Deposit=0, Status='new', StatusAt=SYSUTCDATETIME(), Fees=Fees+@f, FeesJson=@fj, Total=Total+@f, Seen=0 WHERE Id=@i AND StoreId=@s AND Status='unpaid' AND PayState='card'", "@f", fee, "@fj", fees.Count == 0 ? null : ShopJson.Write(fees), "@i", o.Id, "@s", o.StoreId)) n = cmd.ExecuteNonQuery();
            if (n == 0) throw new ShopException("moved", T(x, "الطلب اتغيّر حالًا. حدّث الصفحة.", "The order changed a moment ago. Reload the page."));
            ShopOrders.Log_(c, null, o.StoreId, o.Id, "customer", "اختار الدفع كاش عند الاستلام بدل الكارت" + (fee > 0 ? " (رسوم " + ShopOrders.Money(fee) + ")" : ""));
            ShopHooks.OnPlaced(x.Store, o.Id);
        }

        // ================= what Paymob says (the callback, or its answer when we asked) =================
        // false = not ours / not this store's; true = handled (also when it changes nothing)
        static bool Apply(SqlConnection c, CardSet k, Dictionary<string, object> t, string via)
        {
            string reference = RefOf(t), txn = Val(ShopJson.At(t, "id")); long po; long.TryParse(Val(ShopJson.At(t, "order", "id")), out po);
            var p = (reference.Length > 0 ? Pays(c, "StoreId=@s AND Ref=@r", "@s", k.StoreId, "@r", reference) : new List<Pay>()).FirstOrDefault() ?? (po > 0 ? Pays(c, "StoreId=@s AND PmOrder=@p", "@s", k.StoreId, "@p", po).FirstOrDefault() : null);
            if (p == null) return false;
            if (ShopJson.Bool(ShopJson.At(t, "has_parent_transaction"))) { RefundSeen(c, k, p, t); return true; }   // a refund or a void of our payment
            long cents; long.TryParse(Val(ShopJson.At(t, "amount_cents")), out cents); string cur = Val(ShopJson.At(t, "currency"));
            if (Success(t))
            {
                if (cents != p.Amount || cur != "EGP") { Mark(c, p, "failed", "المبلغ اللي اتدفع (" + (cents / 100m).ToString("0.##", CultureInfo.InvariantCulture) + " " + cur + ") غير المطلوب"); ShopOrders.Log_(c, null, p.StoreId, p.OrderId, "system", "Paymob قالت إنه اتدفع مبلغ غير المطلوب، فماتسجّلش. راجع العملية " + txn + " في حساب Paymob."); return true; }
                Paid(c, p.StoreId, p.Id, txn, Val(ShopJson.At(t, "source_data", "type")), Val(ShopJson.At(t, "source_data", "pan")), via);
            }
            else if (ShopJson.Bool(ShopJson.At(t, "pending")) && !ShopJson.Bool(ShopJson.At(t, "success"))) Mark(c, p, "pending", null);
            else if (!ShopJson.Bool(ShopJson.At(t, "success")))
            {
                var why = ShopJson.Str(ShopJson.At(t, "data", "message"), 200); if (why.Length == 0) why = ShopJson.Str(ShopJson.At(t, "data", "txn_response_code"), 60);
                if (Mark(c, p, "failed", why.Length == 0 ? "الدفع مانجحش" : why)) ShopOrders.Log_(c, null, p.StoreId, p.OrderId, "system", "محاولة الدفع مانجحتش" + (why.Length > 0 ? ": " + why : ""));
            }
            return true;
        }
        static bool Mark(SqlConnection c, Pay p, string state, string err) { using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopCardPays SET State=@st, Err=@e WHERE Id=@i AND State IN ('new','made','pending','failed')", "@st", state, "@e", Nz(err), "@i", p.Id)) return cmd.ExecuteNonQuery() > 0; }

        // ---------- THE moment an order becomes paid by Paymob ----------
        // Once per transaction whatever happens (the try's row is claimed first). An order that can't take the money (it was paid already, or
        // it was cancelled and its stock is gone) gives it back by itself. Ends with AfterPaid. Returns what happened.
        public static string Paid(SqlConnection c, int storeId, long payId, string txn, string kind, string pan, string via)
        {
            var p = Pays(c, "Id=@i AND StoreId=@s", "@i", payId, "@s", storeId).FirstOrDefault(); if (p == null) return "gone";
            using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopCardPays SET State='paid', Txn=@t, Kind=@k, Pan=@p, Err=NULL, DoneAt=SYSUTCDATETIME() WHERE Id=@i AND State<>'paid'", "@t", txn, "@k", Nz(kind), "@p", Nz(pan.Length > 8 ? pan.Substring(pan.Length - 4) : pan), "@i", payId))
                if (cmd.ExecuteNonQuery() == 0) return p.Txn == txn ? "again" : Extra(c, p, txn, "تاني عملية ناجحة لنفس صفحة الدفع");
            p.Txn = txn; decimal egp = p.Amount / 100m; string mode;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    string st, ps, pay; decimal paid, total;
                    using (var cmd = Db.Cmd(c, "SELECT Status, PayState, Paid, Pay, Total FROM dbo.ShopOrders WITH (UPDLOCK, ROWLOCK) WHERE Id=@i AND StoreId=@s", "@i", p.OrderId, "@s", storeId)) { cmd.Transaction = tx; using (var r = cmd.ExecuteReader()) { if (!r.Read()) { tx.Rollback(); return "gone"; } st = (string)r[0]; ps = r[1] as string; paid = (decimal)r[2]; pay = (string)r[3]; total = (decimal)r[4]; } }
                    Func<string, bool> set = sql => { using (var cmd = Db.Cmd(c, sql, "@a", egp, "@w", p.Way, "@i", p.OrderId, "@s", storeId)) { cmd.Transaction = tx; return cmd.ExecuteNonQuery() == 1; } };
                    if (ps == "card" && st != "cancelled" && st != "returned") mode = set("UPDATE dbo.ShopOrders SET PayState='paid', Paid=Paid+@a, Pay=@w, Seen=0 WHERE Id=@i AND StoreId=@s AND PayState='card'") ? "paid" : "extra";
                    else if (pay == "cod" && string.IsNullOrEmpty(ps) && paid == 0 && (st == "new" || st == "noreply" || st == "confirmed") && Cents(total) == p.Amount) mode = set("UPDATE dbo.ShopOrders SET PayState='paid', Paid=@a, Deposit=@a, Pay=@w, Seen=0 WHERE Id=@i AND StoreId=@s AND ISNULL(PayState,'')='' AND Paid=0") ? "pre" : "extra";
                    else if (ps == "card" && st == "cancelled") mode = "revive";
                    else mode = "extra";
                    tx.Commit();
                }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            // paid a moment after the 30 minutes: the order comes back when its stock is still there, otherwise the money goes back
            if (mode == "revive")
            {
                var e = ShopOrders.SetStatus(c, storeId, p.OrderId, "new", "system", null);
                if (e != null) return Extra(c, p, txn, "الأوردر كان اتلغى وكميته خلصت");
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopOrders SET PayState='paid', Paid=Paid+@a, Pay=@w, Seen=0 WHERE Id=@i AND StoreId=@s AND PayState='card'", "@a", egp, "@w", p.Way, "@i", p.OrderId, "@s", storeId)) mode = cmd.ExecuteNonQuery() == 1 ? "paid" : "extra";
                if (mode == "extra") return Extra(c, p, txn, "الأوردر اتغيّر");
                ShopOrders.Log_(c, null, storeId, p.OrderId, "system", "الدفع وصل بعد ما الأوردر اتلغى، فرجع يشتغل");
            }
            if (mode == "extra") return Extra(c, p, txn, "الأوردر مش مستني الدفع ده");
            Exec(c, "UPDATE dbo.ShopCardSet SET WorkedAt=ISNULL(WorkedAt, SYSUTCDATETIME()), LastError=NULL WHERE StoreId=@s", "@s", storeId); var k = Of(storeId); if (!k.WorkedAt.HasValue) k.WorkedAt = DateTime.UtcNow; k.LastError = null;
            ShopOrders.Log_(c, null, storeId, p.OrderId, "system", "اتدفع " + ShopOrders.Money(egp) + " " + Name(p.Way) + " على Paymob (رقم العملية " + txn + (string.IsNullOrEmpty(pan) ? "" : "، كارت آخره " + (pan.Length > 4 ? pan.Substring(pan.Length - 4) : pan)) + ")" + (k.Live ? "" : " · وضع التجربة"));
            var tell = WaNotify.QuietUntil(); Exec(c, "UPDATE dbo.ShopCardPays SET TellAt=ISNULL(@t, SYSUTCDATETIME()) WHERE Id=@i", "@t", tell.HasValue ? (object)tell.Value : null, "@i", p.Id);   // the database's clock: the send compares with it
            var o = ShopOrders.ById(c, storeId, p.OrderId); var s = Db.StoreById(c, storeId);
            if (mode == "paid")
            {
                if (o.Status == "unpaid" || o.Status == "new" || o.Status == "noreply") ShopOrders.SetStatus(c, storeId, o.Id, "confirmed", "system", null);   // paid = confirmed by the customer
                if (s != null) ShopHooks.OnPlaced(s, o.Id, true);   // the owner's alert, «زبايني», the abandoned cart, low stock; no «1 / 2» question: it's paid
            }
            else ShopHooks.Tell(storeId, "pay", "💳 *زبون دفع أوردر " + o.NoText + " بالكارت*\n" + o.Name + " · " + ShopOrders.Money(egp), "sb-card-pre-" + p.Id);
            AfterPaid(c, storeId, o.Id, "card");
            if (tell == null) HostingEnvironment.QueueBackgroundWorkItem(ct => Receipts(ct));
            return mode;
        }
        // Everything that follows a payment, once the order is paid: by card (Paid), or a transfer the owner approved (ShopPay.Review).
        // Each part keeps its own «once» (the digital links have one row per line).
        public static void AfterPaid(SqlConnection c, int storeId, long orderId, string via)
        {
            try { ShopDigital.Deliver(c, storeId, orderId, false); } catch (Exception ex) { Log.Error(ex); }
            try { OnPaidMore(c, storeId, orderId, via); } catch (Exception ex) { Log.Error(ex); }
        }
        // another feature's part (gift cards issued when their order is paid): implement it in that feature's own file
        static partial void OnPaidMore(SqlConnection c, int storeId, long orderId, string via);

        // a payment the order can't take: back to the customer by itself, and the owner is told
        static string Extra(SqlConnection c, Pay p, string txn, string why)
        {
            var o = ShopOrders.ById(c, p.StoreId, p.OrderId); long id;
            try { using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopCardRefunds(StoreId, OrderId, PayId, Amount, Kind, Ck, Auto, Who, PayTxn) OUTPUT INSERTED.Id VALUES(@s, @o, @p, @a, 'refund', @ck, 1, 'system', @t)", "@s", p.StoreId, "@o", p.OrderId, "@p", p.Id, "@a", p.Amount, "@ck", "auto-" + txn, "@t", txn)) id = Convert.ToInt64(cmd.ExecuteScalar()); }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { return "again"; }
            ShopOrders.Log_(c, null, p.StoreId, p.OrderId, "system", "دفع زيادة " + ShopOrders.Money(p.Amount / 100m) + " (" + why + ")، والفلوس بترجع للزبون لوحدها");
            ShopHooks.Tell(p.StoreId, "pay", "💳 *دفع زيادة في أوردر " + (o == null ? "" : o.NoText) + "*\n" + why + ".\nبنرجّع " + ShopOrders.Money(p.Amount / 100m) + " للزبون على كارته لوحدنا، وتقدر تتابع من صفحة الأوردر.", "sb-card-extra-" + id);
            int sid = p.StoreId; string t = txn; HostingEnvironment.QueueBackgroundWorkItem(async ct => { try { await DoRefund(sid, id, t); } catch (Exception ex) { Log.Error(ex); } });
            return "extra";
        }

        // ================= refunds =================
        class Back { public long Id, OrderId, PayId, Amount; public string Kind, State, Txn, PayTxn, Err, Who; public int? RetId; public bool Auto; public DateTime CreatedAt; public DateTime? DoneAt; }
        static List<Back> Backs(SqlConnection c, string where, params object[] a)
        {
            var l = new List<Back>(); using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.ShopCardRefunds WHERE " + where, a)) using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(new Back { Id = Convert.ToInt64(r["Id"]), OrderId = Convert.ToInt64(r["OrderId"]), PayId = Convert.ToInt64(r["PayId"]), Amount = Convert.ToInt64(r["Amount"]), Kind = (string)r["Kind"], State = (string)r["State"], Txn = r["Txn"] as string, PayTxn = r["PayTxn"] as string, Err = r["Err"] as string, Who = (string)r["Who"], RetId = r["RetId"] as int?, Auto = (bool)r["Auto"], CreatedAt = Dt(r["CreatedAt"]).Value, DoneAt = Dt(r["DoneAt"]) });
            return l;
        }
        // what can still go back on the order's card payment (the extra payments go back by themselves)
        static long Left(SqlConnection c, SqlTransaction tx, Pay p)
        {
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(Amount),0) FROM dbo.ShopCardRefunds WHERE PayId=@p AND Auto=0 AND State IN ('making','done','unsure')", "@p", p.Id)) { cmd.Transaction = tx; return p.Amount - Convert.ToInt64(cmd.ExecuteScalar()); }
        }
        static Pay Main(SqlConnection c, int storeId, long orderId) { return Pays(c, "StoreId=@s AND OrderId=@o AND State='paid' AND NOT EXISTS (SELECT 1 FROM dbo.ShopCardRefunds f WHERE f.PayId=dbo.ShopCardPays.Id AND f.Auto=1 AND f.PayTxn=dbo.ShopCardPays.Txn) ORDER BY Id", "@s", storeId, "@o", orderId).FirstOrDefault(); }
        // «رجّع فلوس الكارت»: all or part; ret = a return the owner received (its amount, already counted on the order)
        static Func<Task> RefundFor(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            var o = ShopOrders.ById(c, sh.StoreId, ShopJson.Int(ShopJson.At(b, "id"))); if (o == null) throw new ShopException("gone", "الأوردر ده مش موجود.");
            var k = Of(sh.StoreId); if (!k.Linked) throw new ShopException("link", "بيانات Paymob مش موجودة. اربطها الأول من «الدفع والفروع».");
            var p = Main(c, sh.StoreId, o.Id); if (p == null) throw new ShopException("no_card", "الأوردر ده مااتدفعش بالكارت.");
            int ret = ShopJson.Int(ShopJson.At(b, "ret")); long cents;
            string ck = Regex.Replace(Str(b, "ck", 60), "[^A-Za-z0-9-]", ""); if (ret > 0 && ck.Length < 8) ck = "return-" + sh.StoreId + "-" + ret;   // a return goes back once anyway (one refund a return)
            if (ck.Length < 8) throw new ShopException("ck", "حدّث الصفحة وجرّب تاني.");
            if (ret > 0)
            {
                decimal? rf = null; string st = null, kind = null; using (var cmd = Db.Cmd(c, "SELECT Refund, Status, Kind FROM dbo.ShopReturns WHERE Id=@i AND StoreId=@s AND OrderId=@o", "@i", ret, "@s", sh.StoreId, "@o", o.Id)) using (var r = cmd.ExecuteReader()) if (r.Read()) { rf = r[0] as decimal?; st = (string)r[1]; kind = (string)r[2]; }
                if (kind != "return" || (st != "received" && st != "done") || !rf.HasValue || rf.Value <= 0) throw new ShopException("ret", "المرتجع ده مافيهوش فلوس ترجع.");
                cents = Cents(rf.Value);
            }
            else cents = Cents(ShopJson.Dec(ShopJson.At(b, "amount")));
            if (cents < 100) throw new ShopException("amount", "اكتب المبلغ اللي هيرجع (جنيه واحد على الأقل).");
            if (!Web.Allow("card-rf:" + sh.StoreId, 30, TimeSpan.FromHours(1))) throw new ShopException("too_many", "استرجاعات كتير في ساعة. استنى شوية.");
            long id;
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    // the order's row is held: two presses at once can't both pass the amount left
                    using (var cmd = Db.Cmd(c, "SELECT Id FROM dbo.ShopOrders WITH (UPDLOCK, ROWLOCK) WHERE Id=@i AND StoreId=@s", "@i", o.Id, "@s", sh.StoreId)) { cmd.Transaction = tx; cmd.ExecuteScalar(); }
                    using (var cmd = Db.Cmd(c, "SELECT TOP 1 Id FROM dbo.ShopCardRefunds WHERE StoreId=@s AND Ck=@k", "@s", sh.StoreId, "@k", ck)) { cmd.Transaction = tx; var had = cmd.ExecuteScalar(); if (had != null) { tx.Rollback(); return Answer(sh, o.Id, Convert.ToInt64(had), res); } }
                    var left = Left(c, tx, p); if (cents > left) { tx.Rollback(); throw new ShopException("amount", left <= 0 ? "فلوس الكارت رجعت كلها قبل كده." : "أقصى مبلغ يرجع " + ShopOrders.Money(left / 100m) + "."); }
                    // a void cancels the payment the same day, before it is settled (card payments only); anything else is a refund
                    bool same = p.DoneAt.HasValue && Carriers.Cairo(p.DoneAt.Value).Date == Carriers.Cairo(DateTime.UtcNow).Date, full = cents == p.Amount && left == p.Amount;
                    string kind = same && full && (p.Kind ?? "card").ToLowerInvariant() == "card" ? "void" : "refund";
                    using (var cmd = Db.Cmd(c, "INSERT INTO dbo.ShopCardRefunds(StoreId, OrderId, PayId, Amount, Kind, Ck, RetId, PayTxn) OUTPUT INSERTED.Id VALUES(@s, @o, @p, @a, @k, @ck, @r, @t)", "@t", p.Txn, "@s", sh.StoreId, "@o", o.Id, "@p", p.Id, "@a", cents, "@k", kind, "@ck", ck, "@r", ret > 0 ? (object)ret : null)) { cmd.Transaction = tx; id = Convert.ToInt64(cmd.ExecuteScalar()); }
                    tx.Commit();
                }
                catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627) { try { tx.Rollback(); } catch { } throw new ShopException("ret", "المرتجع ده فلوسه رجعت على الكارت قبل كده."); }
                catch (ShopException) { throw; }
                catch { try { tx.Rollback(); } catch { } throw; }
            }
            int sid = sh.StoreId; long oid = o.Id;
            return async () => { await DoRefund(sid, id, null).ConfigureAwait(false); using (var c2 = Db.Open()) Web.Json(res, 200, OrderCard(c2, sid, oid)); };
        }
        static Func<Task> Answer(Shop sh, long orderId, long refundId, HttpResponse res) { int sid = sh.StoreId; return () => { using (var c2 = Db.Open()) Web.Json(res, 200, OrderCard(c2, sid, orderId)); return Task.CompletedTask; }; }
        // asks Paymob once (no connection held while it answers); txn = the transaction to give back when it isn't the try's own
        static async Task DoRefund(int storeId, long refundId, string txn)
        {
            Back r; Pay p; CardSet k;
            using (var c = Db.Open()) { r = Backs(c, "Id=@i AND StoreId=@s", "@i", refundId, "@s", storeId).FirstOrDefault(); if (r == null || r.State != "making") return; p = Pays(c, "Id=@i", "@i", r.PayId).FirstOrDefault(); k = Of(storeId); }
            string id = r.PayTxn ?? txn ?? p.Txn; string state = "failed", err = null, back = null, kind = r.Kind;
            if (!k.Linked) err = "بيانات Paymob مش موجودة";
            else
            {
                for (int round = 0; round < 2; round++)
                {
                    try
                    {
                        long tid; long.TryParse(id, out tid);
                        var a = kind == "void" ? await Call(HttpMethod.Post, "/api/acceptance/void_refund/void", "Token " + k.Secret, D("transaction_id", tid)).ConfigureAwait(false) : await Call(HttpMethod.Post, "/api/acceptance/void_refund/refund", "Token " + k.Secret, D("transaction_id", tid, "amount_cents", r.Amount)).ConfigureAwait(false);
                        if (a.Good && ShopJson.Bool(ShopJson.At(a.Body, "success"))) { state = "done"; back = Val(ShopJson.At(a.Body, "id")); err = null; break; }
                        err = Said(a); if (kind == "void") { kind = "refund"; continue; }   // not voidable any more (settled): a refund
                        break;
                    }
                    catch (CardException ex) { state = ex.Unsure ? "unsure" : "failed"; err = ex.Message; break; }
                }
            }
            using (var c = Db.Open())
            {
                if (Num(c, "UPDATE dbo.ShopCardRefunds SET State=@st, Kind=@k, Txn=@t, Err=@e, DoneAt=CASE WHEN @st='done' THEN SYSUTCDATETIME() ELSE NULL END WHERE Id=@i AND State='making'; SELECT @@ROWCOUNT", "@st", state, "@k", kind, "@t", Nz(back), "@e", Nz(err == null ? null : err.Length > 300 ? err.Substring(0, 300) : err), "@i", r.Id) == 0) return;
                if (state == "done") Done(c, storeId, r, kind);
                else ShopOrders.Log_(c, null, storeId, r.OrderId, "system", (state == "unsure" ? "مش متأكدين إن الفلوس رجعت (Paymob مارّدتش): راجعها في حساب Paymob" : "الفلوس مارجعتش: " + err) + " · " + ShopOrders.Money(r.Amount / 100m));
            }
        }
        static void Done(SqlConnection c, int storeId, Back r, string kind)
        {
            // the money that went back is the order's (Refund), except a return's (counted already) and an extra payment's
            if (!r.Auto && !r.RetId.HasValue) Exec(c, "UPDATE dbo.ShopOrders SET Refund=Refund+@m WHERE Id=@i AND StoreId=@s", "@m", r.Amount / 100m, "@i", r.OrderId, "@s", storeId);
            ShopOrders.Log_(c, null, storeId, r.OrderId, r.Auto ? "system" : "owner", (kind == "void" ? "اتلغت عملية الكارت" : "رجع للزبون على الكارت") + " " + ShopOrders.Money(r.Amount / 100m) + (r.RetId.HasValue ? " (مرتجع)" : r.Auto ? " (دفع زيادة)" : ""));
        }
        // Paymob's callback for a refund or a void of ours: settles one that had no answer
        static void RefundSeen(SqlConnection c, CardSet k, Pay p, Dictionary<string, object> t)
        {
            if (!ShopJson.Bool(ShopJson.At(t, "success"))) return; long cents; long.TryParse(Val(ShopJson.At(t, "amount_cents")), out cents);
            var r = Backs(c, "PayId=@p AND State='unsure' AND Amount=@a ORDER BY Id", "@p", p.Id, "@a", cents).FirstOrDefault(); if (r == null) return;
            if (Num(c, "UPDATE dbo.ShopCardRefunds SET State='done', Txn=@t, DoneAt=SYSUTCDATETIME() WHERE Id=@i AND State='unsure'; SELECT @@ROWCOUNT", "@t", Val(ShopJson.At(t, "id")), "@i", r.Id) > 0) Done(c, p.StoreId, r, r.Kind);
        }

        // ================= the background lane: every few minutes =================
        public static async Task Run(CancellationToken ct)
        {
            if (!Shops.Ready) return;
            var due = new List<KeyValuePair<int, long>>();
            using (var c = Db.Open())
                using (var cmd = Db.Cmd(c, @"SELECT TOP 50 o.StoreId, o.Id FROM dbo.ShopOrders o WHERE o.Status='unpaid' AND o.PayState='card' AND o.CreatedAt<DATEADD(MINUTE,-@m,SYSUTCDATETIME())
                    AND NOT EXISTS (SELECT 1 FROM dbo.ShopCardPays p WHERE p.OrderId=o.Id AND p.CreatedAt>DATEADD(MINUTE,-5,SYSUTCDATETIME())) ORDER BY o.Id", "@m", PayMinutes)) using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new KeyValuePair<int, long>((int)r[0], Convert.ToInt64(r[1])));
            foreach (var d in due)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    // the callback may have been lost: Paymob is asked about each try first (with the API key)
                    var k = Of(d.Key); List<Pay> tries; using (var c = Db.Open()) tries = Pays(c, "OrderId=@o AND StoreId=@s AND State<>'new'", "@o", d.Value, "@s", d.Key);
                    bool paid = false;
                    if (!string.IsNullOrEmpty(k.Api)) foreach (var p in tries) { Dictionary<string, object> t = null; try { t = await Ask(k, null, p.Ref).ConfigureAwait(false); } catch (CardException) { } if (t != null && Success(t) && RefOf(t) == p.Ref) using (var c = Db.Open()) { Apply(c, k, t, "ask"); paid = true; } }
                    if (paid) continue;
                    using (var c = Db.Open()) ShopOrders.SetStatus(c, d.Key, d.Value, "cancelled", "system", "مادفعش خلال " + PayMinutes + " دقيقة، ومفيش فلوس اتخصمت");
                }
                catch (Exception ex) { Log.Error(ex); }
            }
            try { await Receipts(ct).ConfigureAwait(false); } catch (Exception ex) { Log.Error(ex); }
            try { await ShopDigital.SendDue(ct).ConfigureAwait(false); } catch (Exception ex) { Log.Error(ex); }
            // refunds with no answer: Paymob says how much of the payment went back (with the API key)
            try
            {
                List<Back> l; using (var c = Db.Open()) l = Backs(c, "State='unsure' AND CreatedAt>DATEADD(DAY,-7,SYSUTCDATETIME()) AND (DoneAt IS NULL)");
                foreach (var r in l.Take(20))
                {
                    Pay p; int sid; using (var c = Db.Open()) { p = Pays(c, "Id=@i", "@i", r.PayId).FirstOrDefault(); } if (p == null) continue; sid = p.StoreId; var k = Of(sid); if (string.IsNullOrEmpty(k.Api)) continue;
                    Dictionary<string, object> t = null; try { t = await Ask(k, r.PayTxn ?? p.Txn, null).ConfigureAwait(false); } catch (CardException) { } if (t == null) continue;
                    long back; long.TryParse(Val(ShopJson.At(t, "refunded_amount_cents")), out back); bool voided = ShopJson.Bool(ShopJson.At(t, "is_voided"));
                    using (var c = Db.Open())
                    {
                        long before; using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(Amount),0) FROM dbo.ShopCardRefunds WHERE PayId=@p AND State='done' AND ISNULL(PayTxn,'')=@t", "@p", p.Id, "@t", r.PayTxn ?? "")) before = Convert.ToInt64(cmd.ExecuteScalar());
                        bool happened = voided || back >= before + r.Amount;
                        if (happened && Num(c, "UPDATE dbo.ShopCardRefunds SET State='done', DoneAt=SYSUTCDATETIME() WHERE Id=@i AND State='unsure'; SELECT @@ROWCOUNT", "@i", r.Id) > 0) Done(c, sid, r, r.Kind);
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        // «اتدفع» to the customer from the store's WhatsApp: the order's summary without the «1 / 2» question (it's paid). Never at night.
        static async Task Receipts(CancellationToken ct)
        {
            if (WaNotify.QuietUntil() != null) return;
            var l = new List<long>(); using (var c = Db.Open()) using (var cmd = Db.Cmd(c, "SELECT TOP 30 Id FROM dbo.ShopCardPays WHERE State='paid' AND ToldAt IS NULL AND TellAt<=SYSUTCDATETIME() AND TellAt>DATEADD(DAY,-2,SYSUTCDATETIME()) ORDER BY Id")) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(Convert.ToInt64(r[0]));
            foreach (var id in l)
            {
                if (ct.IsCancellationRequested) return;
                using (var c = Db.Open())
                {
                    if (Num(c, "UPDATE dbo.ShopCardPays SET ToldAt=SYSUTCDATETIME() WHERE Id=@i AND ToldAt IS NULL; SELECT @@ROWCOUNT", "@i", id) == 0) continue;   // once
                    var p = Pays(c, "Id=@i", "@i", id).First(); var o = ShopOrders.ById(c, p.StoreId, p.OrderId); var s = Db.StoreById(c, p.StoreId); var sh = Shops.ByStore(c, p.StoreId);
                    if (o == null || s == null || sh == null || o.Phone.Length == 0 || o.PayState != "paid" || o.Status == "cancelled" || Num(c, "SELECT COUNT(*) FROM dbo.ShopCardRefunds WHERE PayId=@p AND Auto=1 AND PayTxn=@t", "@p", p.Id, "@t", p.Txn ?? "") > 0) continue;
                    bool en = o.Lang == "en"; string text;
                    if (p.Pre) text = en ? "✅ We received your payment of EGP " + (p.Amount / 100m).ToString("#,0.##", CultureInfo.InvariantCulture) + " for order " + o.NoText + ". Thank you!" : "✅ وصلنا دفعك " + ShopOrders.Money(p.Amount / 100m) + " لطلب " + o.NoText + ". شكرًا!";
                    else
                    {
                        text = ShopHooks.ConfirmText(sh, o); int cut = text.IndexOf(en ? "\n\nPlease confirm" : "\n\nمن فضلك أكّد", StringComparison.Ordinal); if (cut > 0) text = text.Substring(0, cut);
                        text += en ? "\n\n✅ Your payment arrived, and we are preparing your order." : "\n\n✅ دفعك وصل، وهنبدأ نجهّز طلبك.";
                    }
                    var why = await WaNotify.ToCustomer(c, s, o.Phone, text, "sb-card-paid-" + p.Id).ConfigureAwait(false);
                    ShopOrders.Log_(c, null, p.StoreId, o.Id, "system", why == null ? "رسالة «دفعك وصل» اتبعتت على الواتساب" : "رسالة «دفعك وصل» ماتبعتتش: " + why);
                }
            }
        }

        // ================= the owner (api/shop.ashx) =================
        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            if (a == "card") { Web.Json(res, 200, Tab(c, s, sh)); return true; }
            if (a == "card-order") { Web.Json(res, 200, OrderCard(c, sh.StoreId, ShopJson.Int(req.QueryString["id"]))); return true; }
            return ShopDigital.Get(c, s, sh, a, req, res);
        }
        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null;
            switch (a)
            {
                case "card-link": later = Link(c, s, sh, b, res); return true;
                case "card-set": SaveSet(c, sh, b); Web.Json(res, 200, Tab(c, s, sh)); return true;
                case "card-unlink":
                    if (Num(c, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Status='unpaid' AND PayState='card'", "@s", sh.StoreId) > 0) throw new ShopException("waiting", "فيه أوردرات مستنية الدفع دلوقتي. استنى نص ساعة وجرّب تاني.");
                    Exec(c, "UPDATE dbo.ShopCardSet SET CardOn=0, SecretBox=NULL, PublicBox=NULL, HmacBox=NULL, ApiBox=NULL, Last4=NULL, LinkedAt=NULL, WorkedAt=NULL, LastError=NULL WHERE StoreId=@s", "@s", sh.StoreId); Tokens.TryRemove(sh.StoreId, out _); Saved(c, sh.StoreId); Web.Json(res, 200, Tab(c, s, sh)); return true;
                case "card-refund": later = RefundFor(c, s, sh, b, res); return true;
                case "card-mark":
                    {
                        // a refund Paymob didn't answer: the owner looked in their Paymob account
                        var r = Backs(c, "Id=@i AND StoreId=@s AND State='unsure'", "@i", ShopJson.Int(ShopJson.At(b, "rid")), "@s", sh.StoreId).FirstOrDefault(); if (r == null) throw new ShopException("gone", "الاسترجاع ده اتحسم خلاص.");
                        bool yes = ShopJson.Bool(ShopJson.At(b, "done"));
                        if (Num(c, "UPDATE dbo.ShopCardRefunds SET State=@st, DoneAt=CASE WHEN @st='done' THEN SYSUTCDATETIME() ELSE NULL END, Who='owner' WHERE Id=@i AND State='unsure'; SELECT @@ROWCOUNT", "@st", yes ? "done" : "failed", "@i", r.Id) > 0) { if (yes) Done(c, sh.StoreId, r, r.Kind); else ShopOrders.Log_(c, null, sh.StoreId, r.OrderId, "owner", "الاسترجاع مااتعملش في Paymob (" + ShopOrders.Money(r.Amount / 100m) + ")"); }
                        Web.Json(res, 200, OrderCard(c, sh.StoreId, r.OrderId)); return true;
                    }
            }
            return ShopDigital.Post(c, s, sh, a, b, res, out later);
        }
        static Dictionary<string, object> Tab(SqlConnection c, StoreInfo s, Shop sh)
        {
            var k = Read(c, sh.StoreId); Sets[sh.StoreId] = k;
            return D("ok", true, "on", k.CardOn, "linked", k.Linked, "live", k.Live, "last4", k.Last4, "api", !string.IsNullOrEmpty(k.Api), "card", k.CardId, "wallet", k.WalletId, "instMin", k.InstMin, "worked", k.WorkedAt.HasValue ? Iso(k.WorkedAt.Value) : "", "err", k.LastError ?? "", "at", k.LinkedAt.HasValue ? Iso(k.LinkedAt.Value) : "",
                "inst", Companies.Select(x => { var i = k.Inst.FirstOrDefault(y => y.K == x[0]); return D("k", x[0], "n", x[1], "id", i == null ? 0 : i.Id, "on", i != null && i.On); }).ToArray(),
                "digi", D("on", k.DigiOn, "days", k.DigiDays, "max", k.DigiMax, "count", Num(c, "SELECT COUNT(*) FROM dbo.ShopDigital WHERE StoreId=@s", "@s", sh.StoreId), "mb", Math.Round(ShopDigital.UsedBytes(c, sh.StoreId) / 1048576.0, 1), "maxMb", ShopDigital.MaxStoreBytes / 1048576),
                "transfer", ShopPrice.PayWays(sh).Count, "waiting", Num(c, "SELECT COUNT(*) FROM dbo.ShopOrders WHERE StoreId=@s AND Status='unpaid' AND PayState='card'", "@s", sh.StoreId));
        }
        static void Row(SqlConnection c, int storeId) { Exec(c, "IF NOT EXISTS (SELECT 1 FROM dbo.ShopCardSet WHERE StoreId=@s) INSERT INTO dbo.ShopCardSet(StoreId) VALUES(@s)", "@s", storeId); }
        // the keys: checked for their shape here (Paymob has no harmless call for the secret key: the first payment proves them); the API key is checked
        static Func<Task> Link(SqlConnection c, StoreInfo s, Shop sh, Dictionary<string, object> b, HttpResponse res)
        {
            if (!Web.Allow("card-link:" + sh.StoreId, 20, TimeSpan.FromHours(1))) throw new ShopException("too_many", "محاولات كتير. استنى شوية وجرّب تاني.");
            var old = Read(c, sh.StoreId); Func<string, int, string> t = (k, n) => Regex.Replace(Str(b, k, n), @"\s", "");
            string sk = t("secret", 300), pk = t("public", 300), hm = t("hmac", 200), api = t("api", 1000); int card = ShopJson.Int(ShopJson.At(b, "card")), wallet = ShopJson.Int(ShopJson.At(b, "wallet"));
            if (sk.Length == 0) sk = old.Secret ?? ""; if (pk.Length == 0) pk = old.Public ?? ""; if (hm.Length == 0) hm = old.Hmac ?? ""; bool dropApi = ShopJson.Bool(ShopJson.At(b, "apiOff")); if (api.Length == 0 && !dropApi) api = old.Api ?? "";
            if (!Regex.IsMatch(sk, @"^[A-Za-z0-9_\-]{20,300}$")) throw new ShopException("secret", "الـ Secret Key مش مظبوط. انسخه من Paymob: Settings ثم API Keys.");
            if (!Regex.IsMatch(pk, @"^[A-Za-z0-9_\-]{20,300}$")) throw new ShopException("public", "الـ Public Key مش مظبوط. انسخه من نفس الصفحة.");
            if (!Regex.IsMatch(hm, @"^[A-Za-z0-9]{16,200}$")) throw new ShopException("hmac", "الـ HMAC مش مظبوط. انسخه من Paymob: Settings ثم Account info.");
            if (sk.Contains("_sk_") && pk.Contains("_pk_") && sk.Contains("_test_") != pk.Contains("_test_")) throw new ShopException("mix", "مفتاح تجربة ومفتاح Live مع بعض. خليهم الاتنين من نفس الوضع.");
            if (sk == pk) throw new ShopException("public", "الـ Public Key نفس الـ Secret Key. انسخ كل واحد من مكانه.");
            if (card < 0 || card > 999999999 || wallet < 0 || wallet > 999999999 || (card == 0 && wallet == 0 && !ShopJson.List(ShopJson.At(b, "inst")).Any())) throw new ShopException("ids", "اكتب رقم الـ Integration بتاع الكارت (من Paymob: Developers ثم Payment Integrations).");
            var inst = new List<object>(); foreach (var x in ShopJson.List(ShopJson.At(b, "inst")).OfType<Dictionary<string, object>>()) { string key = Str(x, "k", 10); int id = ShopJson.Int(ShopJson.At(x, "id")); if (IsInst(key) && id > 0 && id < 1000000000 && !inst.Cast<Dictionary<string, object>>().Any(y => (string)y["k"] == key)) inst.Add(D("k", key, "id", id, "on", ShopJson.Bool(ShopJson.At(x, "on"), true))); }
            decimal min = Math.Max(0, ShopJson.Dec(ShopJson.At(b, "instMin"))); bool live = !sk.Contains("_test_"); int sid = sh.StoreId;
            var last = D("s", Last(sk), "p", Last(pk), "h", Last(hm), "a", Last(api));
            return async () =>
            {
                if (api.Length > 0 && api != old.Api)
                {
                    if (!Regex.IsMatch(api, @"^[A-Za-z0-9_=.\-]{20,1000}$")) throw new ShopException("api", "الـ API Key مش مظبوط.");
                    try { Tokens.TryRemove(sid, out _); await Bearer(new CardSet { StoreId = sid, Api = api }).ConfigureAwait(false); } catch (CardException ex) { throw new ShopException("api", ex.BadKey ? "Paymob مش قابلة الـ API Key ده. انسخه تاني من Settings ثم API Keys." : "Paymob مش بترد دلوقتي، فماقدرناش نجرّب الـ API Key. جرّب كمان شوية أو سيبه فاضي."); }
                }
                using (var c2 = Db.Open())
                {
                    Row(c2, sid); bool keysChanged = sk != old.Secret || pk != old.Public || hm != old.Hmac;
                    Exec(c2, @"UPDATE dbo.ShopCardSet SET SecretBox=@sk, PublicBox=@pk, HmacBox=@hm, ApiBox=@api, Last4=@l, Live=@live, CardId=@c, WalletId=@w, Inst=@i, InstMin=@m, CardOn=CASE WHEN @on IS NULL THEN CardOn ELSE @on END,
                        HookId=ISNULL(HookId, @h), LinkedAt=CASE WHEN @kc=1 THEN SYSUTCDATETIME() ELSE ISNULL(LinkedAt, SYSUTCDATETIME()) END, WorkedAt=CASE WHEN @kc=1 THEN NULL ELSE WorkedAt END, LastError=NULL WHERE StoreId=@s",
                        "@sk", Secrets.Protect(sk), "@pk", Secrets.Protect(pk), "@hm", Secrets.Protect(hm), "@api", api.Length == 0 ? null : Secrets.Protect(api), "@l", ShopJson.Write(last), "@live", live, "@c", card > 0 ? (object)card : null, "@w", wallet > 0 ? (object)wallet : null,
                        "@i", ShopJson.Write(inst), "@m", min, "@on", b.ContainsKey("on") ? (object)ShopJson.Bool(ShopJson.At(b, "on")) : null, "@h", Shops.Token(12), "@kc", keysChanged, "@s", sid);
                    Saved(c2, sid); Web.Json(res, 200, Tab(c2, s, Shops.ByStore(c2, sid)));
                }
            };
        }
        static void SaveSet(SqlConnection c, Shop sh, Dictionary<string, object> b)
        {
            Row(c, sh.StoreId); var k = Read(c, sh.StoreId);
            if (b.ContainsKey("on") && ShopJson.Bool(ShopJson.At(b, "on")) && !k.Linked) throw new ShopException("link", "اربط حساب Paymob الأول.");
            bool digi = b.ContainsKey("digi") ? ShopJson.Bool(ShopJson.At(b, "digi")) : k.DigiOn; int days = Math.Max(1, Math.Min(365, ShopJson.Int(ShopJson.At(b, "days"), k.DigiDays))), max = Math.Max(1, Math.Min(100, ShopJson.Int(ShopJson.At(b, "max"), k.DigiMax)));
            Exec(c, "UPDATE dbo.ShopCardSet SET CardOn=CASE WHEN @on IS NULL THEN CardOn ELSE @on END, DigiOn=@d, DigiDays=@dd, DigiMax=@dm, InstMin=CASE WHEN @m IS NULL THEN InstMin ELSE @m END WHERE StoreId=@s",
                "@on", b.ContainsKey("on") ? (object)ShopJson.Bool(ShopJson.At(b, "on")) : null, "@d", digi, "@dd", days, "@dm", max, "@m", b.ContainsKey("instMin") ? (object)Math.Max(0, ShopJson.Dec(ShopJson.At(b, "instMin"))) : null, "@s", sh.StoreId);
            Saved(c, sh.StoreId);
        }
        // an order's payment at Paymob and its refunds, and its digital links, for the owner's order page; null = nothing of this feature on it
        public static Dictionary<string, object> OrderCard(SqlConnection c, int storeId, long orderId)
        {
            var o = ShopOrders.ById(c, storeId, orderId); if (o == null) return D("ok", true);
            var pays = Pays(c, "StoreId=@s AND OrderId=@o ORDER BY Try", "@s", storeId, "@o", orderId); var backs = Backs(c, "StoreId=@s AND OrderId=@o ORDER BY Id", "@s", storeId, "@o", orderId); var main = pays.Count == 0 ? null : Main(c, storeId, orderId);
            long left = main == null ? 0 : Left(c, null, main); var k = Of(storeId);
            var rets = new List<object>(); if (main != null) using (var cmd = Db.Cmd(c, "SELECT r.Id, r.Refund FROM dbo.ShopReturns r WHERE r.StoreId=@s AND r.OrderId=@o AND r.Kind='return' AND r.Status IN ('received','done') AND r.Refund>0 AND NOT EXISTS (SELECT 1 FROM dbo.ShopCardRefunds f WHERE f.RetId=r.Id AND f.State<>'failed')", "@s", storeId, "@o", orderId)) using (var r = cmd.ExecuteReader()) while (r.Read()) rets.Add(D("id", (int)r[0], "amount", (decimal)r[1]));
            return D("ok", true, "card", IsWay(o.Pay) || pays.Count > 0, "way", Name(o.Pay) ?? "", "test", !k.Live && k.Linked, "wait", o.Status == "unpaid" && o.PayState == "card", "mins", o.Status == "unpaid" ? MinsLeft(o) : 0,
                "pays", pays.Select(p => D("try", p.Try, "way", Name(p.Way) ?? p.Way, "state", p.State, "amount", p.Amount / 100m, "txn", p.Txn ?? "", "pan", p.Pan ?? "", "kind", p.Kind ?? "", "err", p.Err ?? "", "at", Iso(p.CreatedAt), "done", p.DoneAt.HasValue ? Iso(p.DoneAt.Value) : "", "ref", p.Ref, "extra", backs.Any(x => x.Auto && x.PayTxn == p.Txn))).ToArray(),
                "refunds", backs.Select(r => D("id", r.Id, "amount", r.Amount / 100m, "kind", r.Kind, "state", r.State, "err", r.Err ?? "", "auto", r.Auto, "ret", r.RetId ?? 0, "at", Iso(r.CreatedAt))).ToArray(),
                "left", left / 100m, "canVoid", main != null && main.DoneAt.HasValue && Carriers.Cairo(main.DoneAt.Value).Date == Carriers.Cairo(DateTime.UtcNow).Date && left == main.Amount, "rets", rets, "dg", ShopDigital.OwnerLinks(c, storeId, o));
        }
    }

    // api/card-hook.ashx?s=<the store's hook>&hmac=<...>: Paymob's transaction processed callback. Anything not signed with that store's HMAC secret is refused.
    public class CardHookHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }
        public void ProcessRequest(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response; res.TrySkipIisCustomErrors = true; res.Cache.SetCacheability(HttpCacheability.NoCache); string ip = Web.Ip(req);
            if (req.HttpMethod != "POST") { res.StatusCode = 405; return; } if (!Db.Configured || req.ContentLength > 256 * 1024) { res.StatusCode = Db.Configured ? 413 : 503; return; }
            // the address may come back as ?s=x&hmac=y or ?s=x?hmac=y: both are read
            string q = req.Url.Query ?? "", s = Regex.Match(q, "[?&]s=([a-f0-9]{24,48})").Groups[1].Value, h = Regex.Match(q, "[?&]hmac=([A-Fa-f0-9]{128})").Groups[1].Value;
            Func<bool> refuse = () => { res.StatusCode = Web.Allow("card-hook:" + ip, 30, TimeSpan.FromMinutes(10)) ? 401 : 429; return true; };
            try
            {
                string raw; using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) raw = sr.ReadToEnd();
                using (var c = Db.Open())
                {
                    if (!Shops.Ready) { res.StatusCode = 503; return; }
                    if (s.Length == 0) { refuse(); return; }
                    int storeId = ShopCard.Num(c, "SELECT StoreId FROM dbo.ShopCardSet WHERE HookId=@h", "@h", s); var k = storeId == 0 ? null : ShopCard.Of(storeId);
                    if (k == null || !k.Linked) { refuse(); return; }
                    var body = ShopJson.Obj(raw); var t = ShopJson.Sub(body, "obj"); if (h.Length == 0) h = ShopJson.Str(ShopJson.At(body, "hmac"), 200);
                    if (ShopJson.Str(ShopJson.At(body, "type")) != "TRANSACTION") { res.ContentType = "application/json"; res.Write("{\"ok\":true}"); return; }   // card tokens: nothing for us
                    if (!ShopCardHmac(t, k.Hmac, h)) { ShopOrders.Fail(storeId, "card-hook", "إشعار من غير توقيع صحيح من " + ip); refuse(); return; }
                    if (!Web.Allow("card-hook-s:" + storeId, 6000, TimeSpan.FromHours(1))) { res.StatusCode = 429; return; }
                    res.ContentType = "application/json"; res.Write(ShopCard.FromHook(c, k, t) ? "{\"ok\":true}" : "{\"ok\":false}");
                }
            }
            catch (Exception ex) { Log.Error(ex); if (!res.HeadersWritten) res.StatusCode = 500; }
        }
        static bool ShopCardHmac(Dictionary<string, object> t, string secret, string got) { return t.Count > 0 && got.Length == 128 && Secrets.Same(Encoding.UTF8.GetBytes(ShopCard.Hmac(t, secret)), Encoding.UTF8.GetBytes(got.ToLowerInvariant())); }
    }

    public static partial class ShopCard
    {
        public static bool FromHook(SqlConnection c, CardSet k, Dictionary<string, object> t) { return Apply(c, k, t, "hook"); }
    }
}
