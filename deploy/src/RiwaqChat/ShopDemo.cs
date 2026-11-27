// «شوفه بمنتجات تجريبية»: Sellybot's own sample store at demo.<domain>, the same products in every template, so a merchant can
// see each look filled before typing anything of theirs. It's built from App_Data/shopdemo (demo.json + img/) by the same code the
// dashboard saves with, and built again whenever those files change (the hash in AppSettings ShopDemoHash).
// It's always shown as a preview (ShopFront.cs): no visit counted, nothing measured, never indexed, and no order or anything else a
// visitor sends is kept (ShopActions.Run lets only the cart's sum and the search through). ?t=<template> shows it in any template
// open to merchants (ShopTemplates.IsOpen), so a held one stays hidden here too.
// Its account (Stores slug "sellystore-demo") can't sign in, has no chat (status suspended) and counts as the platform's own (IsAdmin).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace RiwaqChat
{
    public static class ShopDemo
    {
        public const string Slug = "demo";                     // a name merchants can't take (Shops.Names)
        const string StoreSlug = "sellystore-demo", Email = "shopdemo@sellybot.online", HashKey = "ShopDemoHash";
        static int known = -1;                                 // the demo store's id: -1 = not looked up yet, 0 = none
        const int Version = 3;                                 // raise it when Build changes: the store is built again on the next start

        // started with the site, away from the first request (the pictures may go to R2). A deploy starts a second copy of the site
        // and then stops the first: whichever copy is left keeps trying every 30 seconds until the store is built (Sync says so).
        public static void Start()
        {
            try
            {
                HostingEnvironment.QueueBackgroundWorkItem(async ct =>
                {
                    var dir = HostingEnvironment.MapPath("~/App_Data/shopdemo");
                    for (int i = 0; i < 40 && !ct.IsCancellationRequested; i++)
                    {
                        try { using (var c = Db.Open()) if (Sync(c, dir)) return; } catch (Exception ex) { Log.Error(ex); }
                        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (TaskCanceledException) { return; }
                    }
                });
            }
            catch (Exception ex) { Log.Error(ex); }   // never stop the site over this
        }

        public static int Id(SqlConnection c)
        {
            if (known >= 0) return known;
            using (var cmd = Db.Cmd(c, "SELECT TOP 1 s.Id FROM dbo.Stores s JOIN dbo.Shops sh ON sh.StoreId=s.Id WHERE s.Slug=@s AND s.OwnerEmail=@e AND sh.Slug=@d", "@s", StoreSlug, "@e", Email, "@d", Slug))
            { var v = cmd.ExecuteScalar(); known = v == null ? 0 : (int)v; }
            return known;
        }
        public static bool Is(SqlConnection c, Shop sh) { return sh != null && sh.Slug == Slug && sh.StoreId == Id(c); }

        // where the dashboard shows it; null until it's built
        public static string Url(SqlConnection c) { return Id(c) > 0 ? Shops.Address(Slug) : null; }

        // the look a visit gets: its own, in the template the address asks for (?t=), when merchants may pick that one
        public static Dictionary<string, object> Look(SqlConnection c, Dictionary<string, object> look, string query)
        {
            var m = Regex.Match(query ?? "", "[?&]t=([a-z]{3,16})(&|$)");
            if (m.Success && ShopTemplates.IsOpen(c, m.Groups[1].Value)) look["template"] = m.Groups[1].Value;
            return look;
        }
        // the templates the bar on top offers: [id, name] in the order merchants see them
        public static object[] Templates(SqlConnection c)
        {
            var open = new HashSet<string>(ShopTemplates.List(c));
            return ShopTemplates.Cards().Where(x => open.Contains(x.Id)).Select(x => (object)new[] { x.Id, x.Name[0], x.Name[1] }).ToArray();
        }

        // true = the store is as the files say (or can't be made: logged); false = another copy is building it now, try again later
        public static bool Sync(SqlConnection c, string dir)
        {
            if (!Shops.Ready || string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "demo.json"))) return true;
            string raw = File.ReadAllText(Path.Combine(dir, "demo.json"), Encoding.UTF8).TrimStart('﻿');
            var pics = Directory.Exists(Path.Combine(dir, "img")) ? Directory.GetFiles(Path.Combine(dir, "img"), "*.jpg").OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>();
            string hash;
            using (var sha = SHA256.Create())
            {
                var all = new StringBuilder("v" + Version + "|" + raw);
                foreach (var f in pics) all.Append('\0').Append(Path.GetFileName(f)).Append('\0').Append(new FileInfo(f).Length);
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(all.ToString()))).Replace("-", "").ToLowerInvariant();
            }
            if (Billing.Get(c, HashKey) == hash && Id(c) > 0) return true;   // nothing changed

            // one copy at a time (the two copies of the site overlap for a moment after a deploy); the other one just leaves it
            using (var cmd = Db.Cmd(c, "DECLARE @r INT; EXEC @r = sp_getapplock @Resource='shopdemo', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @r")) if ((int)cmd.ExecuteScalar() < 0) return false;
            try
            {
                if (Billing.Get(c, HashKey) == hash && Id(c) > 0) return true;
                Build(c, ShopJson.Obj(raw), Path.Combine(dir, "img"));
                Billing.Set(c, HashKey, hash);
                return true;
            }
            finally
            {
                known = -1;
                using (var cmd = Db.Cmd(c, "EXEC sp_releaseapplock @Resource='shopdemo', @LockOwner='Session'")) cmd.ExecuteNonQuery();
            }
        }

        static void Build(SqlConnection c, Dictionary<string, object> j, string imgDir)
        {
            var name = ShopJson.List(ShopJson.At(j, "name")); string ar = ShopJson.Str(name.Count > 0 ? name[0] : "", 80), en = ShopJson.Str(name.Count > 1 ? name[1] : "", 80);

            // the account: made once, never one a person owns
            int id = 0; string owner = null;
            using (var cmd = Db.Cmd(c, "SELECT Id, OwnerEmail FROM dbo.Stores WHERE Slug=@s", "@s", StoreSlug)) using (var r = cmd.ExecuteReader()) if (r.Read()) { id = (int)r["Id"]; owner = r["OwnerEmail"] as string; }
            if (id != 0 && !string.Equals(owner, Email, StringComparison.OrdinalIgnoreCase)) { Log.Error(new InvalidOperationException("shopdemo: the name " + StoreSlug + " belongs to an account")); return; }
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Shops WHERE Slug=@d AND StoreId<>@i", "@d", Slug, "@i", id)) if ((int)cmd.ExecuteScalar() > 0) { Log.Error(new InvalidOperationException("shopdemo: a store already has the link " + Slug)); return; }
            if (id == 0)
            {
                byte[] rnd = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
                using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Stores (Slug, Name, OwnerEmail, PasswordHash, IsAdmin, MonthlyTokenLimit, Welcome, BotName) OUTPUT INSERTED.Id VALUES (@s, @n, @e, @h, 1, 0, NULL, '')",
                    "@s", StoreSlug, "@n", ar, "@e", Email, "@h", Web.HashPassword(Convert.ToBase64String(rnd)))) id = (int)cmd.ExecuteScalar();
            }
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Stores SET Name=@n, Status='suspended', IsAdmin=1 WHERE Id=@i", "@n", ar, "@i", id)) cmd.ExecuteNonQuery();
            var s = Db.StoreById(c, id);
            var sh = Shops.Create(c, s); if (sh == null) return;
            using (var cmd = Db.Cmd(c, "UPDATE dbo.Shops SET Slug=@d, Name=@n, NameEn=@ne, Biz='general' WHERE StoreId=@i", "@d", Slug, "@n", ar, "@ne", en.Length > 0 ? (object)en : DBNull.Value, "@i", id)) cmd.ExecuteNonQuery();

            // what an older copy put in goes first: products, sections and their pictures
            var old = new List<string>(); using (var cmd = Db.Cmd(c, "SELECT Path FROM dbo.ShopImages WHERE StoreId=@s", "@s", id)) using (var r = cmd.ExecuteReader()) while (r.Read()) old.Add((string)r[0]);
            ShopImages.Delete(c, id, old);
            using (var cmd = Db.Cmd(c, @"DELETE FROM dbo.ShopProductCategories WHERE StoreId=@s; DELETE FROM dbo.ShopStock WHERE StoreId=@s; DELETE FROM dbo.ShopVariants WHERE StoreId=@s;
                DELETE FROM dbo.ShopProducts WHERE StoreId=@s; DELETE FROM dbo.ShopCategories WHERE StoreId=@s; DELETE FROM dbo.ShopRedirects WHERE StoreId=@s", "@s", id)) cmd.ExecuteNonQuery();
            Shops.Bump(c, id);

            // the pictures, each its own upload as a merchant's is (big + small): a picture belongs to one product, section or slide
            Func<string, string> pic = k =>
            {
                if (string.IsNullOrEmpty(k)) return "";
                string big = Path.Combine(imgDir, k + ".jpg"), small = Path.Combine(imgDir, k + ".s.jpg"); if (!File.Exists(big)) return "";
                int w = 800, h = 800; Size(big, ref w, ref h);
                var x = ShopImages.Save(c, id, null, Convert.ToBase64String(File.ReadAllBytes(big)), File.Exists(small) ? Convert.ToBase64String(File.ReadAllBytes(small)) : null, w, h, "");
                return (string)x["path"];
            };

            var cats = new Dictionary<string, int>();
            foreach (var k in ShopJson.List(ShopJson.At(j, "cats")).OfType<Dictionary<string, object>>())
            {
                var n = ShopJson.List(ShopJson.At(k, "n")); var d = ShopJson.List(ShopJson.At(k, "d"));
                sh = Shops.ByStore(c, id);
                cats[ShopJson.Str(ShopJson.At(k, "k"), 20)] = ShopOwner.SaveCatFrom(c, sh, new Dictionary<string, object> { { "name", Pick(n, 0) }, { "nameEn", Pick(n, 1) }, { "descr", Pick(d, 0) }, { "descrEn", Pick(d, 1) }, { "slug", ShopJson.Str(ShopJson.At(k, "k"), 20) }, { "image", pic(ShopJson.Str(ShopJson.At(k, "im"), 40)) }, { "inMenu", true }, { "inHome", true } });
            }

            sh = Shops.ByStore(c, id); var now = DateTime.UtcNow;
            foreach (var p in ShopJson.List(ShopJson.At(j, "products")).OfType<Dictionary<string, object>>())
            {
                var n = ShopJson.List(ShopJson.At(p, "n")); var d = ShopJson.List(ShopJson.At(p, "d")); int cat; cats.TryGetValue(ShopJson.Str(ShopJson.At(p, "c"), 20), out cat);
                decimal price = ShopJson.Dec(ShopJson.At(p, "pr")); object stockV = ShopJson.At(p, "stock"); bool track = stockV != null; int stock = ShopJson.Int(stockV);
                var opts = ShopJson.List(ShopJson.At(p, "o")).OfType<Dictionary<string, object>>().ToList(); var vp = ShopJson.List(ShopJson.At(p, "vp"));
                // every mix of the options, each first value at the price (a second weight or size may cost more: vp)
                var combos = new List<List<string>> { new List<string>() };
                foreach (var o in opts) combos = combos.SelectMany(cmb => ShopJson.List(ShopJson.At(o, "v")).Select(v => cmb.Concat(new[] { ShopJson.Str(ShopJson.List(v).FirstOrDefault(), 40) }).ToList())).ToList();
                var vars = opts.Count == 0 ? new List<object>() : combos.Select(cmb =>
                {
                    int at = opts.Count == 1 ? ShopJson.List(ShopJson.At(opts[0], "v")).Select(v => ShopJson.Str(ShopJson.List(v).FirstOrDefault(), 40)).ToList().IndexOf(cmb[0]) : -1;
                    var v2 = new Dictionary<string, object> { { "o", cmb.ToArray() }, { "stock", track ? stock : 0 }, { "active", true } };
                    if (at >= 0 && at < vp.Count) v2["price"] = ShopJson.Dec(vp[at]);
                    return (object)v2;
                }).ToList();
                var b = new Dictionary<string, object>
                {
                    { "name", Pick(n, 0) }, { "nameEn", Pick(n, 1) }, { "descr", Pick(d, 0) }, { "descrEn", Pick(d, 1) }, { "slug", ShopJson.Str(ShopJson.At(p, "s"), 80) },
                    { "price", price }, { "old", ShopJson.Dec(ShopJson.At(p, "old")) }, { "unit", ShopJson.Str(ShopJson.At(p, "unit"), 20) }, { "feat", ShopJson.Bool(ShopJson.At(p, "feat")) },
                    { "track", track }, { "stock", stock }, { "out", "show" }, { "cats", cat > 0 ? new object[] { cat } : new object[0] }, { "main", cat },
                    { "options", opts.Select(o => (object)new Dictionary<string, object> { { "n", ShopJson.At(o, "n") }, { "v", ShopJson.At(o, "v") } }).ToArray() }, { "vars", vars.ToArray() },
                    { "specs", ShopJson.At(p, "sp") ?? new object[0] },
                    { "images", ShopJson.List(ShopJson.At(p, "im")).Select(k => (object)pic(ShopJson.Str(k, 40))).Where(x => ((string)x).Length > 0).ToArray() }
                };
                int pid = ShopOwner.SaveProductFrom(c, sh, b, null);
                // how it looks after a while of selling: when it came in (the «جديد» ones are the recent) and how many went
                using (var cmd = Db.Cmd(c, "UPDATE dbo.ShopProducts SET Sold=@n, CreatedAt=@t, UpdatedAt=@t WHERE StoreId=@s AND Id=@i", "@n", Math.Max(0, ShopJson.Int(ShopJson.At(p, "sd"))), "@t", now.AddDays(-Math.Max(0, ShopJson.Int(ShopJson.At(p, "age")))).AddMinutes(-pid % 600), "@s", id, "@i", pid)) cmd.ExecuteNonQuery();
            }

            // its settings: delivery with a free line, a place and opening hours, no phone or WhatsApp (they'd be someone's)
            sh = Shops.ByStore(c, id);
            var set = ShopLook.CleanSettings(new Dictionary<string, object>
            {
                { "contact", new Dictionary<string, object> { { "phone", "" }, { "wa", "" }, { "email", "" }, { "address", "القاهرة، مصر" }, { "hours", "كل يوم من 10 الصبح لـ 10 بالليل" }, { "waBtn", false } } },
                { "ship", new Dictionary<string, object> { { "mode", "flat" }, { "fee", 50 }, { "freeOver", 1500 }, { "days", "1-3" } } },
                { "returnsDays", 14 }
            }, sh.Settings);
            set["shipSeen"] = true; set["returnsSeen"] = true;
            Shops.SaveSettings(c, sh, set);

            // its look: the slides on top (the first is the hero, the engine puts the hero first: W.slides), then the products
            var slides = ShopJson.List(ShopJson.At(j, "slides")).OfType<Dictionary<string, object>>().Select(x => (object)new Dictionary<string, object>
            {
                { "img", pic(ShopJson.Str(ShopJson.At(x, "im"), 40)) }, { "imgM", pic("hm" + ShopJson.Str(ShopJson.At(x, "im"), 40).Substring(1)) },
                { "title", ShopJson.At(x, "t") }, { "text", ShopJson.At(x, "x") }, { "btn", ShopJson.At(x, "b") },
                { "link", cats.ContainsKey(ShopJson.Str(ShopJson.At(x, "l"), 20)) ? "/c/" + ShopJson.Str(ShopJson.At(x, "l"), 20) : "/c/all" }, { "x", 50 }, { "y", 50 }
            }).ToArray();
            var look = ShopLook.Start(s);
            look["langs"] = "both"; look["first"] = "ar";
            look["ann"] = new Dictionary<string, object> { { "on", true }, { "text", ShopJson.At(j, "ann") }, { "link", "" } };
            if (slides.Length > 0) look["hero"] = slides[0];
            look["sections"] = new object[]
            {
                new Dictionary<string, object> { { "t", "slider" }, { "on", slides.Length > 1 }, { "items", slides.Skip(1).ToArray() } },
                new Dictionary<string, object> { { "t", "cats" }, { "on", true } }, new Dictionary<string, object> { { "t", "best" }, { "on", true } },
                new Dictionary<string, object> { { "t", "new" }, { "on", true } }, new Dictionary<string, object> { { "t", "sale" }, { "on", true } }, new Dictionary<string, object> { { "t", "trust" }, { "on", true } }
            };
            var footer = ShopJson.Sub(look, "footer"); footer["about"] = ShopJson.At(j, "about_text"); look["footer"] = footer;
            var json = ShopJson.Write(ShopLook.Clean(look, id));
            using (var cmd = Db.Cmd(c, @"UPDATE dbo.Shops SET Draft=@l, Live=@l, DraftAt=NULL, Status='live', RentUntil='2099-01-01', ReviewChange=0, ReviewNote=NULL,
                    ApprovedAt=ISNULL(ApprovedAt, SYSUTCDATETIME()), PublishedAt=SYSUTCDATETIME(), FreeUntil=ISNULL(FreeUntil, SYSUTCDATETIME()) WHERE StoreId=@s", "@l", json, "@s", id)) cmd.ExecuteNonQuery();
            Shops.Bump(c, id);
        }

        static string Pick(List<object> l, int i) { return ShopJson.Str(l.Count > i ? l[i] : "", 8000); }

        // a JPEG's width and height from its SOF marker (no image library needed)
        static void Size(string file, ref int w, ref int h)
        {
            try
            {
                var b = File.ReadAllBytes(file); int i = 2;
                while (i + 9 < b.Length && b[i] == 0xFF)
                {
                    int m = b[i + 1], len = (b[i + 2] << 8) | b[i + 3];
                    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) { h = (b[i + 5] << 8) | b[i + 6]; w = (b[i + 7] << 8) | b[i + 8]; return; }
                    i += 2 + len;
                }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
