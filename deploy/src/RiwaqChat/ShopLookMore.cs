// The store's look, the last phase: season looks (ShopSeasons.cs), trying two looks (ShopAb.cs) and the merchant's own domain
// (ShopDomains.cs). Their tables, and the owner's actions in api/shop.ashx (ShopDesk passes here what it doesn't know).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public static class ShopLookMore
    {
        public const string Schema = ShopSeasons.Schema + ShopAb.Schema + ShopDomains.Schema;

        public static bool Get(SqlConnection c, StoreInfo s, Shop sh, string a, HttpRequest req, HttpResponse res)
        {
            switch (a)
            {
                case "sea": Web.Json(res, 200, ShopSeasons.OwnerJson(c, sh)); return true;
                case "ab": Web.Json(res, 200, ShopAb.OwnerJson(c, s, sh)); return true;
                case "dom": Web.Json(res, 200, ShopDomains.OwnerJson(c, sh)); return true;
            }
            return false;
        }

        public static bool Post(SqlConnection c, StoreInfo s, Shop sh, string a, Dictionary<string, object> b, HttpResponse res, out Func<Task> later)
        {
            later = null;
            switch (a)
            {
                case "sea-seen": ShopSeasons.Seen(c, sh, ShopJson.Str(ShopJson.At(b, "k"), 20)); Web.Json(res, 200, new Dictionary<string, object> { { "ok", true } }); return true;
                case "ab-save": ShopAb.Save(c, sh, b); break;
                case "ab-start": ShopAb.Start(c, s, sh); break;
                case "ab-stop": ShopAb.Stop(c, s, sh, b); break;
                case "ab-drop": ShopAb.Drop(c, sh); break;
                case "dom-add": later = ShopDomains.Add(c, s, sh, b, res); return true;
                case "dom-check": later = ShopDomains.Check(c, sh, res); return true;
                case "dom-del": later = ShopDomains.Drop(c, sh, res); return true;
                default: return false;
            }
            Web.Json(res, 200, new Dictionary<string, object> { { "ok", true }, { "ab", ShopAb.OwnerJson(c, s, Shops.ByStore(c, sh.StoreId)) } });
            return true;
        }
    }
}
