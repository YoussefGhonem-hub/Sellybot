// The store's products as a file that Facebook / Instagram (Commerce Manager) and Google Merchant read by themselves:
// <store>/feed/facebook.csv and <store>/feed/google.xml. One row for each size and colour, grouped by its product; the price, the offer
// running now and what is available are today's. product_type is the category's path and custom_label_0 its name, so an ad set can be
// made for one category.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace RiwaqChat
{
    public static class ShopFeed
    {
        class Row { public string Id, Group, Title, Text, Link, Image, More, Brand, Color, Size, Type, Cat, Label; public decimal Price; public decimal? Sale; public bool In; }

        static List<Row> Rows(ShopData d, DateTime now)
        {
            var l = new List<Row>(); var best = ShopView.Best(d);
            foreach (var p in d.Shown)
            {
                if (p.Pics.Count == 0) continue;   // both refuse a product without a picture
                ShopCat k = null, up = null; d.CatById.TryGetValue(p.MainCat, out k); if (k != null && k.ParentId.HasValue) d.CatById.TryGetValue(k.ParentId.Value, out up);
                int ci = p.Options.FindIndex(o => Regex.IsMatch(o.Ar + o.En, "لون|colou?r", RegexOptions.IgnoreCase)), si = p.Options.FindIndex(o => Regex.IsMatch(o.Ar + o.En, "مقاس|size", RegexOptions.IgnoreCase));
                foreach (var v in p.Vars.Where(x => x.Active))
                {
                    decimal? old; ShopOffer of; var u = ShopPrice.Unit(d, p, v, now, out old, out of); var vals = v.Vals;
                    l.Add(new Row
                    {
                        Id = v.Id.ToString(CultureInfo.InvariantCulture), Group = p.Id.ToString(CultureInfo.InvariantCulture), Title = Cut(p.Name + (vals.Length > 0 ? " - " + v.Label : ""), 150), Text = Cut(ShopPages.Plain(string.IsNullOrWhiteSpace(p.Descr) ? p.Name : p.Descr, 4000), 4000), Link = d.Row.Url + "/p/" + p.Slug,
                        Image = ShopImages.Url(string.IsNullOrEmpty(v.Image) ? p.Pics[0].Path : v.Image), More = string.Join(",", p.Pics.Skip(1).Take(5).Select(x => ShopImages.Url(x.Path))), Brand = string.IsNullOrEmpty(p.Brand) ? d.Row.Name : p.Brand,
                        Color = ci >= 0 && ci < vals.Length ? vals[ci] : "", Size = si >= 0 && si < vals.Length ? vals[si] : "", Type = k == null ? "" : up == null ? k.Name : up.Name + " > " + k.Name, Cat = up != null ? up.Name : k == null ? "" : k.Name, Label = old.HasValue ? "خصم" : best.Contains(p.Id) ? "الأكتر طلبًا" : (now - p.CreatedAt).TotalDays < 14 ? "جديد" : "",
                        Price = old ?? u, Sale = old.HasValue ? u : (decimal?)null, In = p.Can(v)
                    });
                }
            }
            return l;
        }
        static string Cut(string s, int n) { return s != null && s.Length > n ? s.Substring(0, n) : s ?? ""; }
        static string M(decimal v) { return v.ToString("0.00", CultureInfo.InvariantCulture) + " EGP"; }

        public static void Write(ShopFrontHandler.Ctx x, string file, HttpResponse res)
        {
            if (file != "facebook.csv" && file != "google.xml") { res.StatusCode = 404; return; }
            var rows = Rows(x.Data, x.Now); var sb = new StringBuilder(rows.Count * 600 + 500);
            res.Cache.SetCacheability(HttpCacheability.Public); res.Cache.SetMaxAge(TimeSpan.FromMinutes(20)); res.AddHeader("X-Robots-Tag", "noindex");
            if (file == "facebook.csv")
            {
                // a text that starts like a formula is never run by a spreadsheet
                Func<string, string> q = s => { s = Regex.Replace(s ?? "", @"[\u0000-\u001F]+", " "); if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; };
                sb.Append("id,item_group_id,title,description,availability,condition,price,sale_price,link,image_link,additional_image_link,brand,color,size,product_type,custom_label_0,custom_label_1\n");
                foreach (var r in rows) sb.Append(string.Join(",", new[] { r.Id, r.Group, r.Title, r.Text, r.In ? "in stock" : "out of stock", "new", M(r.Price), r.Sale.HasValue ? M(r.Sale.Value) : "", r.Link, r.Image, r.More, r.Brand, r.Color, r.Size, r.Type, r.Cat, r.Label }.Select(q))).Append('\n');
                res.ContentType = "text/csv; charset=utf-8"; res.Write("﻿" + sb);
                return;
            }
            Func<string, string> e = s => SecurityElement.Escape(new string((s ?? "").Where(ch => ch >= 0x20 || ch == '\n').ToArray()));
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rss version=\"2.0\" xmlns:g=\"http://base.google.com/ns/1.0\"><channel><title>").Append(e(x.Row.Name)).Append("</title><link>").Append(e(x.Row.Url)).Append("</link><description>").Append(e(x.Row.Name)).Append("</description>\n");
            foreach (var r in rows)
            {
                Action<string, string> t = (n, v) => { if (!string.IsNullOrEmpty(v)) sb.Append("<g:").Append(n).Append('>').Append(e(v)).Append("</g:").Append(n).Append('>'); };
                sb.Append("<item>"); t("id", r.Id); t("item_group_id", r.Group); t("title", r.Title); t("description", r.Text); t("link", r.Link); t("image_link", r.Image); foreach (var m in r.More.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) t("additional_image_link", m);
                t("availability", r.In ? "in_stock" : "out_of_stock"); t("condition", "new"); t("price", M(r.Price)); if (r.Sale.HasValue) t("sale_price", M(r.Sale.Value)); t("brand", r.Brand); t("color", r.Color); t("size", r.Size); t("product_type", r.Type); t("custom_label_0", r.Cat); t("custom_label_1", r.Label); t("identifier_exists", "no"); sb.Append("</item>\n");
            }
            res.ContentType = "application/xml; charset=utf-8"; res.Write(sb.Append("</channel></rss>").ToString());
        }
    }
}
