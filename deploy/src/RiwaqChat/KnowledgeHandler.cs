// api/knowledge.ashx — a store owner's data: add text, upload files (txt/md/csv/pdf/docx/xlsx), edit, delete.
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Threading.Tasks;
using System.Web;

namespace RiwaqChat
{
    public class KnowledgeHandler : HttpTaskAsyncHandler
    {
        // One part (a file or a text) and the whole store. Above Claude.FullModeMaxChars the store uses search mode,
        // so big catalogs (thousands of products) are fine; the cap only protects the server's memory.
        public const int MaxItemChars = 3000000;
        public static int MaxStoreChars
        {
            get { int v; return int.TryParse(Web.Setting("MaxStoreChars", "5000000"), out v) ? v : 5000000; }
        }
        const int MaxFileBytes = 10 * 1024 * 1024;

        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            if (!Db.Configured) { Web.Error(res, 503, "not_configured"); return; }
            try
            {
                using (var c = Db.Open())
                {
                    var s = Me.Require(ctx, c);
                    if (s == null) { Web.Error(res, 401, "not_logged_in"); return; }

                    if (req.HttpMethod == "GET")
                    {
                        int id;
                        if (int.TryParse(req.QueryString["id"], out id)) { var one = Item(c, s.Id, id, true); if (one == null) Web.Error(res, 404, "not_found"); else Web.Json(res, 200, one); }
                        else Web.Json(res, 200, new Dictionary<string, object> { { "items", List(c, s.Id) } });
                        return;
                    }
                    if (req.HttpMethod != "POST" || !Web.CsrfOk(req)) { Web.Error(res, 400, "bad_request"); return; }
                    if (!Web.Allow("kn:" + s.Id, 60, TimeSpan.FromHours(1))) { Web.Error(res, 429, "rate_limited", "تعديلات كتير، استنى شوية."); return; }

                    string action = (req.QueryString["action"] ?? "").ToLowerInvariant();

                    if (action == "upload")
                    {
                        var file = req.Files.Count > 0 ? req.Files[0] : null;
                        if (file == null || file.ContentLength == 0) { Web.Error(res, 400, "no_file", "اختار ملف."); return; }
                        if (file.ContentLength > MaxFileBytes) { Web.Error(res, 400, "file_too_big", "الملف أكبر من 10 ميجا."); return; }
                        string fileName = Path.GetFileName(file.FileName);
                        string type = Extract.TypeOf(fileName);
                        if (type == null) { Web.Error(res, 400, "bad_type", "نوع الملف مش مدعوم. المسموح: txt, md, csv, pdf, docx, xlsx"); return; }
                        byte[] data;
                        using (var ms = new MemoryStream()) { file.InputStream.CopyTo(ms); data = ms.ToArray(); }
                        // stop early if the database is already full (before spending time or Claude tokens reading the file)
                        var early = Storage.CheckRoom(c, 0);
                        if (early != null) { Web.Error(res, 507, "storage_full", early); return; }

                        string text;
                        try
                        {
                            if (type == "txt") text = Extract.PlainText(data);
                            else if (type == "docx") text = Extract.Word(data);
                            else if (type == "xlsx") text = Extract.Excel(data);
                            else
                            {
                                var r = await Claude.PdfToText(data);
                                text = r.Text;
                                Db.AddUsage(c, s.Id, 0, r.In, r.CacheRead, r.CacheWrite, r.Out);
                            }
                        }
                        catch (Exception ex) { Log.Error(ex); Web.Error(res, 400, "unreadable", "مقدرناش نقرا الملف ده. اتأكد إنه مش تالف أو محمي بباسورد."); return; }

                        if (string.IsNullOrWhiteSpace(text)) { Web.Error(res, 400, "empty", "الملف مافيهوش نص نقدر نقراه."); return; }
                        string title = Web.Str(new Dictionary<string, object> { { "t", req.Form["title"] } }, "t", 200);
                        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(fileName);
                        var err = CheckSize(c, s.Id, 0, text);
                        if (err != null) { Web.Error(res, 400, "too_much", err); return; }
                        var room = Storage.CheckRoom(c, text.Length);
                        if (room != null) { Web.Error(res, 507, "storage_full", room); return; }
                        int id = Insert(c, s.Id, title, type, fileName, text);
                        Web.Json(res, 200, Item(c, s.Id, id, true));
                        return;
                    }

                    var b = Web.Body(req, MaxItemChars * 3 + 5000);
                    if (action == "add")
                    {
                        string title = Web.Str(b, "title", 200);
                        string content = Web.Str(b, "content", MaxItemChars);
                        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) { Web.Error(res, 400, "empty", "اكتب عنوان ومحتوى."); return; }
                        var err = CheckSize(c, s.Id, 0, content);
                        if (err != null) { Web.Error(res, 400, "too_much", err); return; }
                        var room = Storage.CheckRoom(c, content.Length);
                        if (room != null) { Web.Error(res, 507, "storage_full", room); return; }
                        int id = Insert(c, s.Id, title, "text", null, content);
                        Web.Json(res, 200, Item(c, s.Id, id, true));
                        return;
                    }
                    if (action == "update")
                    {
                        var id = Web.Int(b, "id");
                        string title = Web.Str(b, "title", 200);
                        string content = Web.Str(b, "content", MaxItemChars);
                        if (id == null || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) { Web.Error(res, 400, "empty", "اكتب عنوان ومحتوى."); return; }
                        var err = CheckSize(c, s.Id, id.Value, content);
                        if (err != null) { Web.Error(res, 400, "too_much", err); return; }
                        var room = Storage.CheckRoom(c, content.Length);
                        if (room != null) { Web.Error(res, 507, "storage_full", room); return; }
                        int n;
                        using (var cmd = Db.Cmd(c, "UPDATE dbo.KnowledgeItems SET Title=@t, Content=@c, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id AND StoreId=@s",
                            "@t", title, "@c", content, "@id", id.Value, "@s", s.Id)) n = cmd.ExecuteNonQuery();
                        if (n == 0) { Web.Error(res, 404, "not_found"); return; }
                        Db.BumpVersion(c, s.Id);
                        Web.Json(res, 200, Item(c, s.Id, id.Value, true));
                        return;
                    }
                    if (action == "delete")
                    {
                        var id = Web.Int(b, "id");
                        if (id == null) { Web.Error(res, 400, "bad_request"); return; }
                        using (var cmd = Db.Cmd(c, "DELETE FROM dbo.KnowledgeItems WHERE Id=@id AND StoreId=@s", "@id", id.Value, "@s", s.Id)) cmd.ExecuteNonQuery();
                        Db.BumpVersion(c, s.Id);
                        Web.Json(res, 200, new { ok = true });
                        return;
                    }
                    Web.Error(res, 404, "unknown_action");
                }
            }
            catch (BadRequestException ex) { Web.Error(res, 400, "bad_request", ex.Message); }
            catch (Exception ex) when (Storage.IsFull(ex)) { Log.Error(ex); Web.Error(res, 507, "storage_full", Storage.FullMessage); }
            catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error", "حصلت مشكلة، جرّب تاني."); }
        }

        static string CheckSize(SqlConnection c, int storeId, int exceptId, string content)
        {
            if (content.Length > MaxItemChars) return "الجزء ده أكبر من " + MaxItemChars.ToString("N0") + " حرف. قسّمه لأكتر من جزء.";
            long others;
            using (var cmd = Db.Cmd(c, "SELECT ISNULL(SUM(CAST(LEN(Content) AS BIGINT)),0) FROM dbo.KnowledgeItems WHERE StoreId=@s AND Id<>@id", "@s", storeId, "@id", exceptId))
                others = Convert.ToInt64(cmd.ExecuteScalar());
            if (others + content.Length > MaxStoreChars)
                return "كده الداتا هتعدّي الحد (" + MaxStoreChars.ToString("N0") + " حرف للمتجر). امسح أو اختصر حاجات قديمة الأول.";
            return null;
        }

        static int Insert(SqlConnection c, int storeId, string title, string type, string fileName, string content)
        {
            int id;
            using (var cmd = Db.Cmd(c, "INSERT INTO dbo.KnowledgeItems (StoreId, Title, SourceType, FileName, Content) OUTPUT INSERTED.Id VALUES (@s,@t,@ty,@f,@c)",
                "@s", storeId, "@t", title, "@ty", type, "@f", fileName, "@c", content))
                id = (int)cmd.ExecuteScalar();
            Db.BumpVersion(c, storeId);
            return id;
        }

        static List<Dictionary<string, object>> List(SqlConnection c, int storeId)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT Id, Title, SourceType, FileName, LEN(Content) AS Chars, UpdatedAt FROM dbo.KnowledgeItems WHERE StoreId=@s ORDER BY Id DESC", "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object>
                    {
                        { "id", r["Id"] }, { "title", r["Title"] }, { "type", r["SourceType"] }, { "fileName", r["FileName"] as string },
                        { "chars", r["Chars"] }, { "updated", ((DateTime)r["UpdatedAt"]).ToString("yyyy-MM-dd HH:mm") },
                    });
            return list;
        }

        static Dictionary<string, object> Item(SqlConnection c, int storeId, int id, bool withContent)
        {
            using (var cmd = Db.Cmd(c, "SELECT * FROM dbo.KnowledgeItems WHERE Id=@id AND StoreId=@s", "@id", id, "@s", storeId))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                var d = new Dictionary<string, object>
                {
                    { "id", r["Id"] }, { "title", r["Title"] }, { "type", r["SourceType"] }, { "fileName", r["FileName"] as string },
                    { "chars", ((string)r["Content"]).Length }, { "updated", ((DateTime)r["UpdatedAt"]).ToString("yyyy-MM-dd HH:mm") },
                };
                if (withContent) d["content"] = r["Content"];
                return d;
            }
        }
    }

    public static class Log
    {
        public static void Error(Exception ex)
        {
            try
            {
                var path = System.Web.Hosting.HostingEnvironment.MapPath("~/App_Data/errors.log");
                lock (typeof(Log)) File.AppendAllText(path, DateTime.UtcNow.ToString("o") + "\t" + ex.GetType().Name + "\t" + ex.Message + Environment.NewLine);
            }
            catch { }
        }
    }
}
