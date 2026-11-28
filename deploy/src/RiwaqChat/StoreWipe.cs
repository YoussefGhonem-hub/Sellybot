// Deleting a store from the admin console (api/superadmin.ashx «store-wipe» shows what goes, «store-delete» does it).
// Everything the store owns goes at once:
//   - every row (each table's StoreId cascades from dbo.Stores), its rented store (SellyStore) included;
//   - the rented store's pictures and digital files on disk (App_Data/shopimg/<id>, App_Data/shop-files/<id>);
//   - its own domain on Cloudflare (queued in ShopDomainDrops, the "due" job removes it) and the pages the Worker keeps;
//   - its links outside: the Telegram bot stops coming to us, the Messenger / Instagram subscription is dropped,
//     and Sellybot's WhatsApp numbers it used are logged out so they go back to the pool clean;
//   - the copies kept in memory (chat look, logo, store data...).
// A main store takes its linked stores with it (ShopBrands.cs): they have no login of their own.
// Sellybot's own store (SiteBot.Slug) can't be deleted.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class StoreWipe
    {
        // this store and, when it's a main store, the stores linked to it
        static List<StoreInfo> Group(SqlConnection c, StoreInfo s)
        {
            var all = new List<StoreInfo> { s };
            if (s.ParentId.HasValue) return all;
            var ids = new List<int>();
            using (var cmd = Db.Cmd(c, "SELECT Id FROM dbo.Stores WHERE ParentId=@s AND Id<>@s ORDER BY Id", "@s", s.Id)) using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt32(0));
            foreach (var id in ids) { var k = Db.StoreById(c, id); if (k != null) all.Add(k); }
            return all;
        }

        static string In(IEnumerable<int> ids) { return string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))); }

        // a table that may not be there yet (the SellyStore tables come with Shops.Ready) counts 0
        static decimal Scalar(SqlConnection c, string table, string what, IList<int> ids, string where = null)
        {
            var sql = "IF OBJECT_ID('dbo." + table + "') IS NULL SELECT 0 ELSE SELECT " + what + " FROM dbo." + table + " WHERE StoreId IN (" + In(ids) + ")" + (where == null ? "" : " AND " + where);
            using (var cmd = Db.Cmd(c, sql)) { var v = cmd.ExecuteScalar(); return v == null || v is DBNull ? 0 : Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
        }
        static int Count(SqlConnection c, string table, IList<int> ids, string where = null) { return (int)Scalar(c, table, "COUNT(*)", ids, where); }

        static List<string> Strings(SqlConnection c, string table, string sql)
        {
            var l = new List<string>();
            using (var cmd = Db.Cmd(c, "IF OBJECT_ID('dbo." + table + "') IS NOT NULL " + sql)) using (var r = cmd.ExecuteReader())
                do { while (r.Read()) for (int i = 0; i < r.FieldCount; i++) { var v = r[i] as string; if (!string.IsNullOrEmpty(v)) l.Add(v); } } while (r.NextResult());
            return l;
        }

        public static string Refuse(StoreInfo s)
        {
            return s != null && string.Equals(s.Slug, SiteBot.Slug, StringComparison.OrdinalIgnoreCase) ? "ده متجر Sellybot نفسه (الشات اللي على الموقع)، مينفعش يتمسح." : null;
        }

        // what goes with the store, for the admin to read before typing the store's id
        public static Dictionary<string, object> Info(SqlConnection c, StoreInfo s)
        {
            var g = Group(c, s); var ids = g.Select(x => x.Id).ToList();
            var shops = new List<Dictionary<string, object>>();
            if (Shops.Ready)
                foreach (var x in g)
                {
                    var sh = Shops.ByStore(c, x.Id); if (sh == null) continue;
                    var host = Strings(c, "ShopDomains", "SELECT Host FROM dbo.ShopDomains WHERE StoreId=" + x.Id.ToString(CultureInfo.InvariantCulture)).FirstOrDefault();
                    shops.Add(new Dictionary<string, object> { { "store", x.Name }, { "name", sh.Name }, { "slug", sh.Slug }, { "status", sh.Status }, { "url", Shops.Address(sh.Slug) }, { "domain", host } });
                }
            return new Dictionary<string, object>
            {
                { "refuse", Refuse(s) },
                { "linked", g.Skip(1).Select(x => new Dictionary<string, object> { { "id", x.Id }, { "name", x.Name }, { "slug", x.Slug } }).ToList() },
                { "shops", shops },
                { "products", Count(c, "ShopProducts", ids) }, { "orders", Count(c, "ShopOrders", ids) }, { "customers", Count(c, "ShopCustomers", ids) },
                { "images", Count(c, "ShopImages", ids) }, { "knowledge", Count(c, "KnowledgeItems", ids) }, { "chats", Count(c, "InboxChats", ids) },
                { "subscriptions", Count(c, "Subscriptions", ids) }, { "paidEgp", Scalar(c, "Subscriptions", "SUM(PaidEgp)", ids) }, { "tickets", Count(c, "Tickets", ids) },
                { "wa", g.Count(x => !string.IsNullOrEmpty(x.WaInstance)) + Count(c, "WaLines", ids) }, { "meta", Count(c, "MetaLinks", ids) }, { "tg", Count(c, "TgLinks", ids) },
            };
        }

        public static Dictionary<string, object> Delete(SqlConnection c, StoreInfo s)
        {
            var g = Group(c, s); var ids = g.Select(x => x.Id).ToList(); var list = In(ids);

            // ---- what lives outside the database, read before the rows go ----
            var shopSlugs = new List<string>();
            if (Shops.Ready) foreach (var x in g) { var sh = Shops.ByStore(c, x.Id); if (sh != null) shopSlugs.Add(sh.Slug); }
            var cfIds = Strings(c, "ShopDomains", "SELECT CfId, CfWww FROM dbo.ShopDomains WHERE StoreId IN (" + list + ")");
            var tgTokens = new List<string>();
            foreach (var x in g) { var l = Tg.Link(c, x.Id); if (l != null && !string.IsNullOrEmpty(l.Token)) tgTokens.Add(l.Token); }
            var meta = new List<string[]>();   // account, token, app secret
            var chans = new List<Tuple<int, string>>();
            using (var cmd = Db.Cmd(c, "SELECT StoreId, Channel FROM dbo.MetaLinks WHERE StoreId IN (" + list + ")")) using (var r = cmd.ExecuteReader()) while (r.Read()) chans.Add(Tuple.Create(r.GetInt32(0), r.GetString(1)));
            foreach (var ch in chans)
            {
                var l = MetaApp.Link(c, ch.Item1, ch.Item2); if (l == null || string.IsNullOrEmpty(l.Token)) continue;
                var k = MetaApp.ForLink(c, l); meta.Add(new[] { l.AccountId, l.Token, k != null ? k.Secret : null });
            }
            // only Sellybot's own numbers (a store's own Wapilot account stays as the owner left it)
            var waInst = g.Where(WaAccount.IsPlatform).Select(x => x.WaInstance).ToList();
            waInst.AddRange(Strings(c, "WaLines", "SELECT Instance FROM dbo.WaLines WHERE StoreId IN (" + list + ")"));
            var waToken = waInst.Count > 0 ? WaAccount.PlatformToken(c) : null;

            // ---- the rows: one transaction, the foreign keys take every table with them ----
            using (var tx = c.BeginTransaction())
            {
                foreach (var id in cfIds.Distinct())
                    using (var cmd = Db.Cmd(c, "IF OBJECT_ID('dbo.ShopDomainDrops') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.ShopDomainDrops WHERE CfId=@i) INSERT INTO dbo.ShopDomainDrops(CfId) VALUES(@i)", "@i", id))
                    { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                using (var cmd = Db.Cmd(c, "DELETE FROM dbo.Referrals WHERE ReferrerId IN (" + list + "); DELETE FROM dbo.Stores WHERE Id IN (" + list + ")"))
                { cmd.Transaction = tx; cmd.CommandTimeout = 300; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }

            // ---- files on disk and copies in memory ----
            foreach (var x in g)
            {
                Rmdir(ShopImages.Dir, x.Id); Rmdir(ShopDigital.Dir, x.Id);
                LogoHandler.Forget(x.Slug); ChatLook.Forget(x.Slug);
                ShopCache.Forget(x.Id); Crm.Forget(x.Id); ShopAb.Forget(x.Id); ShopApiHooks.Forget(x.Id); ShopShipPick.Forget(x.Id);
            }
            ShopDomains.Touch();
            foreach (var slug in shopSlugs) ShopAdminHandler.Purge(c, slug);

            // ---- the services outside (best effort, after the answer) ----
            if (tgTokens.Count > 0 || meta.Count > 0 || (waToken != null && waInst.Count > 0))
                HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    foreach (var t in tgTokens) try { await Tg.Call(t, "deleteWebhook"); } catch (Exception ex) { if (!(ex is TgException)) Log.Error(ex); }
                    foreach (var m in meta) try { await MetaApp.Call(HttpMethod.Delete, Uri.EscapeDataString(m[0]) + "/subscribed_apps", m[1], m[2]); } catch (Exception ex) { if (!(ex is MetaException)) Log.Error(ex); }
                    if (waToken != null) foreach (var i in waInst.Distinct()) try { await Wapilot.Release(waToken, i); } catch (Exception ex) { if (!(ex is WaException)) Log.Error(ex); }
                });

            return new Dictionary<string, object> { { "ok", true }, { "stores", g.Select(x => x.Name).ToList() }, { "shops", shopSlugs } };
        }

        // <dir>/<store id>: only a folder named by the id, under the folder the files live in
        static void Rmdir(string root, int storeId)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || storeId <= 0) return;
                var d = Path.Combine(root, storeId.ToString(CultureInfo.InvariantCulture));
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
