// «بلّغني لما يرجع» on WhatsApp. A customer asks for something that's out of stock (or a size that ran out): the assistant
// offers to tell them when it's back and, when they say yes, calls the notify_when_back tool. Sellybot then watches that one
// product / variant in the store system and, as soon as it can be ordered again, sends ONE message with its link.
//   - Shopify (full link): products are found by name and checked by id (GraphQL). An inventory webhook makes the next check run now.
//   - EasyOrders: the product list (it has no stock webhook), at most every 30 minutes per product.
//   - Jobs runs Check every few minutes; quiet hours (WaNotify.QuietUntil) hold messages for the morning; «وقف» stops them;
//     a request waits 60 days at most. The owner sees which products customers wait for most.
// Plan gate "restock" (open until the super admin picks a minimum) on top of the WhatsApp service.
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
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public static class Restock
    {
        public const string Feature = "restock";
        public const string ToolName = "notify_when_back";
        const int WaitDays = 60, CheckEveryMin = 30, MaxPerPhone = 5;

        public static bool CanWatch(StoreInfo s) { return ShopifyAdmin.For(s) != null || EasyOrders.For(s) != null; }
        // a rented store (SellyStore) watches its own stock: the customer asks from the product's page, and the owner's switch is the store's own
        public static bool ShopOn(SqlConnection c, StoreInfo s, Shop sh) { return s != null && sh != null && ShopJson.Bool(ShopJson.At(ShopJson.Sub(sh.Settings, "restock"), "on"), true) && WaAccount.HasService(s) && PlanGate.Allows(c, s, Feature); }
        public static void FromShop(SqlConnection c, StoreInfo s, Shop sh, ShopProd p, ShopVar v, string phone, string prefix)
        {
            string key = p.Id.ToString(CultureInfo.InvariantCulture) + (v != null && v.Opts.Length > 0 ? "|" + v.Id.ToString(CultureInfo.InvariantCulture) : "");
            using (var cmd = Db.Cmd(c, @"IF NOT EXISTS (SELECT 1 FROM dbo.RestockWaits WHERE StoreId=@s AND Source='shop' AND Phone=@p AND ProductKey=@k AND Status='waiting') AND (SELECT COUNT(*) FROM dbo.RestockWaits WHERE StoreId=@s AND Phone=@p AND Status='waiting') < 10
                INSERT INTO dbo.RestockWaits(StoreId, Source, ProductKey, Product, Variant, Url, Phone, Status) VALUES(@s, 'shop', @k, @n, @v, @u, @p, 'waiting')", "@s", s.Id, "@k", key, "@n", Cut(p.Name, 200), "@v", v == null || v.Label.Length == 0 ? null : Cut(v.Label, 100), "@u", Cut(sh.Url + prefix + "/p/" + p.Slug, 600), "@p", phone)) cmd.ExecuteNonQuery();
        }

        public static bool On(SqlConnection c, StoreInfo s)
        {
            return s != null && s.RestockOn && CanWatch(s) && WaAccount.HasService(s) && PlanGate.Allows(c, s, Feature);
        }

        // ---------- 1. the assistant's tool (WhatsApp chats) ----------
        public static ChatHandler.ExtraTool Tool(StoreInfo s, string phone)
        {
            return new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = ToolName,
                    Description = "يسجّل طلب الزبون إنه يوصله رسالة واتساب أول ما منتج (أو مقاس/لون معيّن منه) خلصان يرجع المخزون. " +
                                  "استخدمه بس بعد ما الزبون يوافق إنك تبلّغه. لو المنتج طلع متاح دلوقتي، الأداة هتقولك وتديك لينكه.",
                    InputSchema = new InputSchema
                    {
                        Properties = new Dictionary<string, JsonElement>
                        {
                            ["product"] = JsonSerializer.SerializeToElement(new { type = "string", description = "اسم المنتج زي ما هو مكتوب في بيانات المتجر." }),
                            ["variant"] = JsonSerializer.SerializeToElement(new { type = "string", description = "المقاس أو اللون اللي الزبون عايزه لو المنتج ليه اختيارات، مثلًا L أو أسود. سيبه فاضي لو مش مهم." }),
                        },
                        Required = new List<string> { "product" },
                    },
                },
                Run = tu => Register(s.Id, phone, ChatHandler.Arg(tu, "product"), ChatHandler.Arg(tu, "variant")),
                Prompt = "\n- لو الزبون سأل على منتج أو مقاس/لون خلصان: قوله إنه خلصان، واسأله لو يحب نبلّغه هنا على الواتساب أول ما يرجع. لو وافق، استخدم أداة " + ToolName + ".",
            };
        }

        class Pick { public string Source, Key, Product, Variant, Url; public bool Available; public List<string> Options; }

        static async Task<string> Register(int storeId, string phone, string product, string variant)
        {
            if (string.IsNullOrWhiteSpace(product)) return "اكتب اسم المنتج.";
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (!On(c, s)) return "التنبيه لما المنتج يرجع مش متاح في المتجر ده. قول للزبون يتابع صفحة المنتج أو يسأل تاني بعدين.";
                    if (WaOptOut.Has(c, s.Id, phone)) return "الزبون ده طلب قبل كده مايوصلهوش رسايل من المتجر، فمينفعش نسجّله. قوله يتابع صفحة المنتج.";
                    Pick p;
                    try { p = await Find(s, product, variant); }
                    catch (Exception ex) { Log.Error(ex); return "مقدرناش نوصل لمخزون المتجر دلوقتي. اعتذر للزبون وقوله يسأل تاني بعد شوية."; }
                    if (p == null) return "مش لاقي منتج بالاسم ده في المتجر. اتأكد من الاسم من بيانات المتجر، أو اسأل الزبون يوضّح.";
                    if (p.Options != null) return "المنتج ده ليه اختيارات، ومش واضح أنهي واحد الزبون عايزه. الاختيارات: " + string.Join("، ", p.Options.Take(20)) + ". اسأله يختار، وبعدين سجّل تاني.";
                    string label = p.Product + (string.IsNullOrEmpty(p.Variant) ? "" : " (" + p.Variant + ")");
                    if (p.Available) return "«" + label + "» متاح دلوقتي ومش خلصان" + (string.IsNullOrEmpty(p.Url) ? "" : "، ولينكه: " + p.Url) + ". قول للزبون إنه موجود ويقدر يطلبه.";
                    using (var cmd = Db.Cmd(c, @"SELECT
                        (SELECT COUNT(*) FROM dbo.RestockWaits WHERE StoreId=@s AND Phone=@p AND ProductKey=@k AND Status='waiting'),
                        (SELECT COUNT(*) FROM dbo.RestockWaits WHERE StoreId=@s AND Phone=@p AND Status='waiting')", "@s", s.Id, "@p", phone, "@k", p.Key))
                    using (var r = cmd.ExecuteReader())
                    {
                        r.Read();
                        if (Convert.ToInt32(r[0]) > 0) return "الزبون متسجّل خلاص على «" + label + "». قوله إننا هنبعتله هنا أول ما يرجع.";
                        if (Convert.ToInt32(r[1]) >= MaxPerPhone) return "الزبون مستني " + MaxPerPhone + " منتجات خلاص، ومينفعش أكتر من كده. قوله هنبلّغه بيهم أول ما يرجعوا.";
                    }
                    using (var cmd = Db.Cmd(c, @"INSERT INTO dbo.RestockWaits(StoreId, Source, ProductKey, Product, Variant, Url, Phone, Status) VALUES(@s, @src, @k, @n, @v, @u, @p, 'waiting')",
                        "@s", s.Id, "@src", p.Source, "@k", p.Key, "@n", Cut(p.Product, 200), "@v", (object)Cut(p.Variant, 100) ?? DBNull.Value,
                        "@u", (object)Cut(p.Url, 600) ?? DBNull.Value, "@p", phone))
                        cmd.ExecuteNonQuery();
                    return "✅ اتسجّل: أول ما «" + label + "» يرجع هنبعتله رسالة هنا على الواتساب. قوله كده في جملة قصيرة، وإنه يقدر يبعت «وقف» لو غيّر رأيه.";
                }
            }
            catch (Exception ex) { Log.Error(ex); return "حصلت مشكلة وإحنا بنسجّل. اعتذر للزبون وقوله يسأل تاني بعد شوية."; }
        }

        // The product (and variant) the customer means, with whether it can be ordered now
        static async Task<Pick> Find(StoreInfo s, string product, string variant)
        {
            var creds = ShopifyAdmin.For(s);
            if (creds != null)
            {
                const string q = "query($q: String!) { products(first: 15, query: $q) { nodes { id title handle onlineStoreUrl " +
                                 "variants(first: 60) { nodes { id title availableForSale } } } } }";
                var all = new List<JsonElement>();
                foreach (var term in new[] { product.Trim(), Words(product).OrderByDescending(w => w.Length).FirstOrDefault() }.Where(t => !string.IsNullOrEmpty(t)).Distinct())
                {
                    var g = await ShopifyAdmin.Query(creds, q, new { q = term }, CancellationToken.None);
                    JsonElement ps, nodes;
                    if (g.Data.TryGetProperty("products", out ps) && ps.TryGetProperty("nodes", out nodes)) all.AddRange(nodes.EnumerateArray());
                    if (all.Count > 0) break;
                }
                var best = all.Select(n => new { n, score = Score(product, Str(n, "title")) }).Where(x => x.score >= 0.5).OrderByDescending(x => x.score).FirstOrDefault();
                if (best == null) return null;
                var n0 = best.n;
                string url = Str(n0, "onlineStoreUrl") ?? (string.IsNullOrEmpty(s.ShopifyDomain) || Str(n0, "handle") == null ? null : "https://" + s.ShopifyDomain + "/products/" + Str(n0, "handle"));
                var vars = new List<KeyValuePair<string, KeyValuePair<string, bool>>>();   // id -> (title, available)
                JsonElement vs, vn;
                if (n0.TryGetProperty("variants", out vs) && vs.TryGetProperty("nodes", out vn))
                    foreach (var v in vn.EnumerateArray())
                    {
                        JsonElement af; bool avail = v.TryGetProperty("availableForSale", out af) && af.ValueKind == JsonValueKind.True;
                        vars.Add(new KeyValuePair<string, KeyValuePair<string, bool>>(Str(v, "id"), new KeyValuePair<string, bool>(Str(v, "title") ?? "", avail)));
                    }
                bool single = vars.Count <= 1 || vars.All(v => v.Value.Key == "Default Title");
                if (single || string.IsNullOrWhiteSpace(variant))
                {
                    // no variant asked for: some sizes left and some not -> ask which; otherwise the whole product
                    bool any = vars.Any(v => v.Value.Value), every = vars.All(v => v.Value.Value);
                    if (!single && any && !every) return new Pick { Options = vars.Select(v => v.Value.Key + (v.Value.Value ? " (متاح)" : " (خلصان)")).ToList() };
                    return new Pick { Source = "shopify", Key = Str(n0, "id"), Product = Str(n0, "title"), Url = url, Available = any };
                }
                var vbest = vars.Select(v => new { v, score = Score(variant, v.Value.Key) }).Where(x => x.score >= 0.5).OrderByDescending(x => x.score).FirstOrDefault();
                if (vbest == null) return new Pick { Options = vars.Select(v => v.Value.Key + (v.Value.Value ? " (متاح)" : " (خلصان)")).ToList() };
                return new Pick { Source = "shopify", Key = Str(n0, "id") + "|" + vbest.v.Key, Product = Str(n0, "title"), Variant = vbest.v.Value.Key, Url = url, Available = vbest.v.Value.Value };
            }
            var k = EasyOrders.For(s);
            if (k == null) return null;
            var items = await EasyOrders.StockList(k, s.EoUrl);
            var byProduct = items.GroupBy(i => i.Id).Select(gp => new { gp, score = Score(product, gp.First().Name) }).Where(x => x.score >= 0.5).OrderByDescending(x => x.score).FirstOrDefault();
            if (byProduct == null) return null;
            var rows = byProduct.gp.ToList();
            var first = rows[0];
            if (rows.Count == 1 && string.IsNullOrEmpty(first.VariantId))
                return new Pick { Source = "eo", Key = first.Id, Product = first.Name, Url = first.Url, Available = first.Available };
            if (string.IsNullOrWhiteSpace(variant))
            {
                if (rows.All(r => !r.Available)) return new Pick { Source = "eo", Key = first.Id, Product = first.Name, Url = first.Url, Available = false };
                return new Pick { Options = rows.Select(r => r.Variant + (r.Available ? " (متاح)" : " (خلصان)")).ToList() };
            }
            var vb = rows.Select(r => new { r, score = Score(variant, r.Variant) }).Where(x => x.score >= 0.5).OrderByDescending(x => x.score).FirstOrDefault();
            if (vb == null) return new Pick { Options = rows.Select(r => r.Variant + (r.Available ? " (متاح)" : " (خلصان)")).ToList() };
            return new Pick { Source = "eo", Key = first.Id + "|" + vb.r.VariantId, Product = first.Name, Variant = vb.r.Variant, Url = first.Url, Available = vb.r.Available };
        }

        // ---------- 2. watching the stock (Jobs) ----------
        class Waiting { public long Id; public string Phone, Product, Variant, Url; }

        public static async Task Check(SqlConnection c, CancellationToken ct)
        {
            var due = new List<KeyValuePair<int, string>>();   // store -> product key
            using (var cmd = Db.Cmd(c, @"SELECT TOP 40 StoreId, ProductKey FROM dbo.RestockWaits WHERE Status='waiting' AND Source<>'shop'
                AND (CheckedAt IS NULL OR CheckedAt < DATEADD(MINUTE,-@m,SYSUTCDATETIME())) GROUP BY StoreId, ProductKey ORDER BY MIN(ISNULL(CheckedAt,'2000-01-01'))", "@m", CheckEveryMin))
            using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new KeyValuePair<int, string>((int)r[0], (string)r[1]));
            bool quiet = WaNotify.QuietUntil().HasValue;
            if (!quiet) try { await CheckShops(c, ct); } catch (Exception ex) { Log.Error(ex); }
            if (due.Count == 0) return;
            foreach (var g in due.GroupBy(d => d.Key))
            {
                if (ct.IsCancellationRequested) return;
                var s = Db.StoreById(c, g.Key);
                var keys = g.Select(x => x.Value).ToList();
                if (s == null || !On(c, s) || WaNotify.CantSend(c, s) != null) { Touch(c, s == null ? g.Key : s.Id, keys); continue; }   // off for now: look again later
                HashSet<string> back;
                try { back = await Back(s, keys); }
                catch (Exception ex) { Log.Error(ex); Touch(c, s.Id, keys); continue; }
                if (quiet) back.Clear();   // at night nothing goes out; the next morning's check finds it again
                Touch(c, s.Id, keys);
                foreach (var key in back) await SendAll(c, s, key);
            }
        }

        // the rented stores: their stock is here, so every run looks
        static async Task CheckShops(SqlConnection c, CancellationToken ct)
        {
            if (!Shops.Ready) return;
            var due = new List<KeyValuePair<int, string>>();
            using (var cmd = Db.Cmd(c, "SELECT DISTINCT StoreId, ProductKey FROM dbo.RestockWaits WHERE Status='waiting' AND Source='shop'")) using (var r = cmd.ExecuteReader()) while (r.Read()) due.Add(new KeyValuePair<int, string>((int)r[0], (string)r[1]));
            foreach (var g in due.GroupBy(x => x.Key))
            {
                if (ct.IsCancellationRequested) return;
                var s = Db.StoreById(c, g.Key); var sh = s == null ? null : Shops.ByStore(c, g.Key);
                if (sh == null || !Shops.Open(c, s, sh) || !ShopOn(c, s, sh) || WaNotify.CantSend(c, s) != null) continue;
                var d = ShopCache.Get(c, sh); if (d == null) continue;
                foreach (var kv in g)
                {
                    var parts = kv.Value.Split('|'); int pid, vid = 0; ShopProd p; ShopVar v;
                    if (!int.TryParse(parts[0], out pid) || !d.ById.TryGetValue(pid, out p) || p.Status != "active") continue;
                    bool back = parts.Length == 2 ? int.TryParse(parts[1], out vid) && d.Vars.TryGetValue(vid, out v) && p.Can(v) : p.Available;
                    if (back) await SendAll(c, s, kv.Value);
                }
            }
        }

        // the keys (of those asked about) that can be ordered again
        static async Task<HashSet<string>> Back(StoreInfo s, List<string> keys)
        {
            var back = new HashSet<string>();
            var creds = ShopifyAdmin.For(s);
            if (creds != null)
            {
                var ids = keys.Select(k => k.Contains("|") ? k.Split('|')[1] : k).Where(x => x.StartsWith("gid://shopify/")).Distinct().ToList();
                if (ids.Count == 0) return back;
                const string q = "query($ids: [ID!]!) { nodes(ids: $ids) { __typename ... on ProductVariant { id availableForSale } " +
                                 "... on Product { id status variants(first: 60) { nodes { availableForSale } } } } }";
                var g = await ShopifyAdmin.Query(creds, q, new { ids }, CancellationToken.None);
                var avail = new HashSet<string>();
                JsonElement nodes;
                if (g.Data.TryGetProperty("nodes", out nodes) && nodes.ValueKind == JsonValueKind.Array)
                    foreach (var n in nodes.EnumerateArray())
                    {
                        if (n.ValueKind != JsonValueKind.Object) continue;
                        string id = Str(n, "id"); JsonElement af, vs, vn;
                        if (n.TryGetProperty("availableForSale", out af)) { if (af.ValueKind == JsonValueKind.True) avail.Add(id); }
                        else if (Str(n, "status") == "ACTIVE" && n.TryGetProperty("variants", out vs) && vs.TryGetProperty("nodes", out vn) && vn.EnumerateArray().Any(v => v.TryGetProperty("availableForSale", out af) && af.ValueKind == JsonValueKind.True)) avail.Add(id);
                    }
                foreach (var k in keys) if (avail.Contains(k.Contains("|") ? k.Split('|')[1] : k)) back.Add(k);
                return back;
            }
            var eo = EasyOrders.For(s);
            if (eo == null) return back;
            var items = await EasyOrders.StockList(eo, s.EoUrl);
            foreach (var k in keys)
            {
                var parts = k.Split('|');
                var hit = parts.Length == 2 ? items.Where(i => i.Id == parts[0] && i.VariantId == parts[1]) : items.Where(i => i.Id == parts[0]);
                if (hit.Any(i => i.Available)) back.Add(k);
            }
            return back;
        }

        static async Task SendAll(SqlConnection c, StoreInfo s, string key)
        {
            var list = new List<Waiting>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 200 Id, Phone, Product, Variant, Url FROM dbo.RestockWaits WHERE StoreId=@s AND ProductKey=@k AND Status='waiting' ORDER BY Id", "@s", s.Id, "@k", key))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new Waiting { Id = (long)r["Id"], Phone = (string)r["Phone"], Product = (string)r["Product"], Variant = r["Variant"] as string, Url = r["Url"] as string });
            foreach (var w in list)
            {
                if (WaOptOut.Has(c, s.Id, w.Phone)) { Mark(c, w.Id, "expired", "الزبون بعت «وقف»"); continue; }
                if (!Web.Allow("restock:" + s.Id, 100, TimeSpan.FromHours(1))) return;   // the rest go next run
                var why = await WaNotify.ToCustomer(c, s, w.Phone, Message(s, w), "sb-rs-" + w.Id);
                if (why == null) using (var cmd = Db.Cmd(c, "UPDATE dbo.RestockWaits SET Status='sent', SentAt=SYSUTCDATETIME(), Note=NULL WHERE Id=@id", "@id", w.Id)) cmd.ExecuteNonQuery();
                else Mark(c, w.Id, "failed", why);
            }
        }

        static string Message(StoreInfo s, Waiting w)
        {
            var sb = new StringBuilder("🔔 خبر حلو! *").Append(w.Product).Append(string.IsNullOrEmpty(w.Variant) ? "" : " (" + w.Variant + ")")
                .Append("* رجع تاني في *").Append(s.Name).Append("* 🎉\n");
            if (!string.IsNullOrEmpty(w.Url)) sb.Append("\nاطلبه من هنا قبل ما يخلص تاني 👇\n").Append(w.Url).Append('\n');
            else sb.Append("\nابعتلنا هنا لو عايز تطلبه.\n");
            sb.Append("\n_لو مش عايز رسايل زي دي، ابعت «وقف»._");
            return sb.ToString();
        }

        // A Shopify inventory webhook: look at this store's waiting products in the next run
        public static void StockChanged(SqlConnection c, StoreInfo s)
        {
            if (s == null || !s.RestockOn) return;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.RestockWaits SET CheckedAt=NULL WHERE StoreId=@s AND Status='waiting' AND CheckedAt IS NOT NULL", "@s", s.Id))
                if (cmd.ExecuteNonQuery() > 0 && Web.Allow("rs-kick:" + s.Id, 1, TimeSpan.FromMinutes(2))) Jobs.Kick();
        }

        public static void Expire(SqlConnection c)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.RestockWaits SET Status='expired', Note=N'عدّى " + WaitDays + " يوم ومارجعش' WHERE Status='waiting' AND CreatedAt < DATEADD(DAY,-@d,SYSUTCDATETIME())", "@d", WaitDays))
                cmd.ExecuteNonQuery();
        }

        // ---------- dashboard ----------
        public static Dictionary<string, object> State(SqlConnection c, int storeId)
        {
            var top = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, @"SELECT TOP 8 Product, Variant, COUNT(*) AS N FROM dbo.RestockWaits WHERE StoreId=@s AND Status='waiting'
                GROUP BY Product, Variant ORDER BY COUNT(*) DESC, MAX(Id) DESC", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) top.Add(new Dictionary<string, object> { { "product", r["Product"] as string }, { "variant", r["Variant"] as string ?? "" }, { "count", (int)r["N"] } });
            var recent = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP 15 Product, Variant, Status, Note, CreatedAt, SentAt FROM dbo.RestockWaits WHERE StoreId=@s AND Status<>'waiting' ORDER BY ISNULL(SentAt, CreatedAt) DESC", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    recent.Add(new Dictionary<string, object> { { "product", r["Product"] as string }, { "variant", r["Variant"] as string ?? "" }, { "status", r["Status"] as string },
                        { "note", r["Note"] as string ?? "" }, { "at", Iso(r["SentAt"] == DBNull.Value ? r["CreatedAt"] : r["SentAt"]) } });
            int waiting, sent;
            using (var cmd = Db.Cmd(c, @"SELECT ISNULL(SUM(CASE WHEN Status='waiting' THEN 1 ELSE 0 END),0), ISNULL(SUM(CASE WHEN Status='sent' AND SentAt > DATEADD(DAY,-30,SYSUTCDATETIME()) THEN 1 ELSE 0 END),0)
                FROM dbo.RestockWaits WHERE StoreId=@s", "@s", storeId))
            using (var r = cmd.ExecuteReader()) { r.Read(); waiting = Convert.ToInt32(r[0]); sent = Convert.ToInt32(r[1]); }
            return new Dictionary<string, object> { { "waiting", waiting }, { "sent", sent }, { "top", top }, { "recent", recent } };
        }

        // ---------- helpers ----------
        // how much of what was asked (words) is in the name: 0..1, Arabic letters folded (أ/إ/آ -> ا, ة -> ه, ى -> ي)
        static double Score(string asked, string name)
        {
            var a = Words(asked).ToList(); var n = new HashSet<string>(Words(name));
            if (a.Count == 0 || n.Count == 0) return 0;
            double hit = a.Count(w => n.Contains(w) || n.Any(x => x.Length > 2 && w.Length > 2 && (x.StartsWith(w) || w.StartsWith(x))));
            return hit / a.Count;
        }

        static IEnumerable<string> Words(string s)
        {
            s = ShopifyAdmin.ToLatinDigits(s ?? "").ToLowerInvariant();
            s = Regex.Replace(s, "[ً-ْـ]", "");                   // tashkeel, tatweel
            s = s.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي');
            return Regex.Split(s, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0 && w != "ال" && w != "و").Select(w => w.StartsWith("ال") && w.Length > 3 ? w.Substring(2) : w);
        }

        static void Touch(SqlConnection c, int storeId, List<string> keys)
        {
            foreach (var k in keys)
                using (var cmd = Db.Cmd(c, "UPDATE dbo.RestockWaits SET CheckedAt=SYSUTCDATETIME() WHERE StoreId=@s AND ProductKey=@k AND Status='waiting'", "@s", storeId, "@k", k)) cmd.ExecuteNonQuery();
        }

        static void Mark(SqlConnection c, long id, string status, string note)
        {
            using (var cmd = Db.Cmd(c, "UPDATE dbo.RestockWaits SET Status=@st, Note=@n WHERE Id=@id", "@st", status, "@n", (object)Cut(note, 300) ?? DBNull.Value, "@id", id)) cmd.ExecuteNonQuery();
        }

        static string Iso(object v) { return v == null || v == DBNull.Value ? "" : DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc).ToString("o"); }
        static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }
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
