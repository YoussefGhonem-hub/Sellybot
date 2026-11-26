// Handoff to the owner. The assistant calls call_owner when a customer asks for a person from the store, is upset or
// complaining, or needs something that matters and isn't in the store's data (wholesale, a problem with an order, a return).
// The owner gets a WhatsApp alert on their own number (Stores.OwnerPhone, from the store's number) with why, what the
// customer wants and a wa.me link to them. On WhatsApp the customer's number is known; on the site chat the assistant asks
// for it first. When the owner answers on the store's WhatsApp themselves, the bot already steps aside for that chat
// (WhatsAppBot.PauseHours). One alert per customer per 2 hours. Every handoff is listed in the dashboard too.
// Plan gate "handoff" (open until the super admin picks a minimum) on top of the WhatsApp service.
// A store with more than one WhatsApp number and employees' names on them (WaLines.cs): the alert goes to them in turn
// (Team.Send) instead of the owner, and says which of the store's numbers the customer wrote to (Handoffs.Line / ToName / ToPhone).
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public static class Handoff
    {
        public const string Feature = "handoff";
        public const string ToolName = "call_owner";
        const int RepeatHours = 2, NoteHours = 3;

        public static bool On(SqlConnection c, StoreInfo s) { return s != null && s.HandoffOn && WaAccount.HasService(s) && PlanGate.Allows(c, s, Feature); }

        // channel "wa": chatKey = the customer's number; "web": chatKey = the site chat's session (the number comes in the call)
        // line: the store's WhatsApp number the customer wrote to (0 = the main one)
        public static ChatHandler.ExtraTool Tool(StoreInfo s, string channel, string chatKey, int line = 0)
        {
            bool web = channel != "wa";   // the site chat, Messenger (fb), Instagram (ig) and Telegram (tg): the customer's number comes in the call
            var props = new Dictionary<string, JsonElement>
            {
                ["reason"] = JsonSerializer.SerializeToElement(new { type = "string", description = "السبب في كلمتين، مثلًا: عايز يكلّم حد، شكوى، طلب جملة، مشكلة في أوردر." }),
                ["summary"] = JsonSerializer.SerializeToElement(new { type = "string", description = "الزبون عايز إيه بالظبط في جملة أو اتنين، بكل التفاصيل اللي قالها (المنتج، رقم الأوردر، الكمية...)." }),
            };
            var required = new List<string> { "reason", "summary" };
            if (web)
            {
                props["phone"] = JsonSerializer.SerializeToElement(new { type = "string", description = "رقم موبايل الزبون (واتساب) زي ما كتبه، عشان صاحب المتجر يكلّمه." });
                required.Add("phone");
            }
            return new ChatHandler.ExtraTool
            {
                Def = new Tool
                {
                    Name = ToolName,
                    Description = "ينبّه صاحب المتجر على الواتساب إن الزبون ده محتاج يكلّمه بنفسه، ومعاه السبب وملخص طلبه" + (web ? " ورقم الزبون" : "") + ".",
                    InputSchema = new InputSchema { Properties = props, Required = required },
                },
                Run = tu => Alert(s.Id, channel, chatKey, ChatHandler.Arg(tu, "reason"), ChatHandler.Arg(tu, "summary"), web ? ChatHandler.Arg(tu, "phone") : null, line),
                Prompt = "\n- لو الزبون طلب يكلّم حد من المتجر، أو عنده شكوى أو زعلان، أو عايز حاجة مهمة مش في بيانات المتجر (طلب جملة، مشكلة في أوردر، استرجاع أو استبدال): " +
                         (web ? "اطلب رقم موبايله (واتساب) لو ماقالوش، وبعدين " : "") + "استخدم أداة " + ToolName + " عشان صاحب المتجر يكلّمه بنفسه. ماتوعدش بحاجة مش في إيدك.",
            };
        }

        static async Task<string> Alert(int storeId, string channel, string chatKey, string reason, string summary, string phoneArg, int line)
        {
            reason = Cut((reason ?? "").Trim(), 120); summary = Cut((summary ?? "").Trim(), 400);
            if (summary.Length == 0) return "اكتب ملخص اللي الزبون عايزه.";
            try
            {
                using (var c = Db.Open())
                {
                    var s = Db.StoreById(c, storeId);
                    if (!On(c, s)) return "التحويل لصاحب المتجر مش متاح. قول للزبون يكلّم المتجر مباشرة.";
                    string phone = channel == "wa" ? WaChat.Digits(chatKey) : WaChat.Phone(phoneArg);
                    if (phone == null) return "محتاج رقم موبايل الزبون (واتساب) الأول. اطلبه منه، وبعدين استخدم الأداة تاني ومعاها الرقم.";
                    using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Handoffs WHERE StoreId=@s AND (ChatKey=@k OR Phone=@p) AND CreatedAt > DATEADD(HOUR,-@h,SYSUTCDATETIME())",
                        "@s", s.Id, "@k", Cut(chatKey, 100), "@p", phone, "@h", RepeatHours))
                        if ((int)cmd.ExecuteScalar() > 0) return "صاحب المتجر اتبلّغ خلاص من شوية. طمّن الزبون إن حد من المتجر هيكلّمه، وماتحددش ميعاد.";

                    long id;
                    using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Handoffs(StoreId, Channel, ChatKey, Phone, Reason, Status, Line) OUTPUT INSERTED.Id VALUES(@s, @ch, @k, @p, @r, 'pending', @l)",
                        "@s", s.Id, "@ch", channel, "@k", Cut(chatKey, 100), "@p", phone, "@r", Cut((reason.Length > 0 ? reason + " — " : "") + summary, 400),
                        "@l", channel == "wa" ? (object)line : DBNull.Value))
                        id = Convert.ToInt64(cmd.ExecuteScalar());
                    if (channel != "web") Inbox.Flag(c, s.Id, channel == "wa" ? Inbox.WaChan(line) : channel, chatKey);   // SellyChat: waiting for someone
                    // which of the store's numbers the customer wrote to, when it has more than one
                    string wroteTo = null;
                    if (channel == "wa")
                    {
                        var lines = WaLines.All(c, s);
                        int at = lines.FindIndex(l => l.Id == line);
                        if (lines.Count > 1 && at >= 0) wroteTo = lines[at].Label(at);
                    }
                    // to the team in turn (employees' numbers), or the owner's own number when there's no team
                    var sent = await Team.Send(c, s, Message(channel, reason, summary, phone, wroteTo), "sb-hand-" + id);
                    string status, note = sent.Error;
                    if (sent.Name == null && string.IsNullOrEmpty(s.OwnerPhone)) { status = "noalert"; note = "مفيش رقم ليك في الإعدادات، فالتنبيه ماوصلكش على الواتساب"; }
                    else status = sent.Error == null ? "sent" : "failed";
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Handoffs SET Status=@st, Note=@n, ToName=@tn, ToPhone=@tp WHERE Id=@id", "@st", status, "@n", (object)Cut(note, 300) ?? DBNull.Value,
                        "@tn", (object)Cut(sent.Name, 60) ?? DBNull.Value, "@tp", (object)sent.Phone ?? DBNull.Value, "@id", id)) cmd.ExecuteNonQuery();
                    string person = sent.Name != null ? "حد من فريق المتجر (" + sent.Name + ")" : "صاحب المتجر";
                    return status == "sent"
                        ? "✅ " + person + " اتبلّغ وهيكلّم الزبون" + (channel == "wa" ? " هنا" : " على رقمه") + " في أقرب وقت. قول للزبون كده بجملة قصيرة ومطمّنة، وماتحددش ميعاد."
                        : "✅ طلب الزبون اتسجّل عند المتجر وهيتواصلوا معاه في أقرب وقت. قول للزبون كده بجملة قصيرة، وماتحددش ميعاد.";
                }
            }
            catch (Exception ex) { Log.Error(ex); return "حصلت مشكلة وإحنا بنبلّغ صاحب المتجر. اعتذر للزبون وقوله يكلّم المتجر مباشرة."; }
        }

        // wroteTo: which of the store's numbers the customer wrote to (only when it has more than one)
        static string Message(string channel, string reason, string summary, string phone, string wroteTo = null)
        {
            var sb = new StringBuilder("🙋 *زبون محتاجك*").Append(channel == "wa" ? " (على الواتساب)" : channel == "fb" ? " (على المسنجر)" : channel == "ig" ? " (على الإنستجرام)" : channel == "tg" ? " (على تيليجرام)" : " (من شات موقعك)").Append('\n');
            if (wroteTo != null) sb.Append("📲 كتب على رقم: ").Append(wroteTo).Append('\n');
            if (reason.Length > 0) sb.Append("السبب: *").Append(reason).Append("*\n");
            sb.Append("📝 ").Append(summary).Append("\n\n");
            sb.Append("📱 رقمه: +").Append(phone).Append('\n');
            sb.Append("👈 كلّمه على الواتساب: https://wa.me/").Append(phone).Append('\n');
            if (channel == "wa") sb.Append("\n💡 لو رديت عليه من رقم المتجر").Append(wroteTo != null ? " اللي كتب عليه" : "").Append("، المساعد بيسكت في المحادثة دي ساعتين.");
            else if (channel == "fb" || channel == "ig") sb.Append("\n💡 لو رديت عليه بنفسك من ").Append(channel == "fb" ? "رسايل الصفحة" : "رسايل الإنستجرام").Append("، المساعد بيسكت في المحادثة دي ساعتين.");
            return sb.ToString();
        }

        // For the assistant on WhatsApp: this customer was handed to the owner a little while ago
        public static string Note(SqlConnection c, int storeId, string chatKey)
        {
            using (var cmd = Db.Cmd(c, "SELECT COUNT(*) FROM dbo.Handoffs WHERE StoreId=@s AND Channel='wa' AND ChatKey=@k AND CreatedAt > DATEADD(HOUR,-@h,SYSUTCDATETIME())",
                "@s", storeId, "@k", chatKey, "@h", NoteHours))
                return (int)cmd.ExecuteScalar() > 0
                    ? "\n- الزبون ده طلب يكلّم صاحب المتجر من شوية، وصاحب المتجر اتبلّغ. لو كتب تاني، ساعده في اللي تقدر عليه وقوله إن صاحب المتجر هيرد عليه بنفسه، وماتحددش ميعاد."
                    : "";
        }

        // ---------- dashboard ----------
        public static List<Dictionary<string, object>> Recent(SqlConnection c, int storeId, int n = 15)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = Db.Cmd(c, "SELECT TOP (@n) Channel, Phone, Reason, Status, Note, CreatedAt, ToName FROM dbo.Handoffs WHERE StoreId=@s ORDER BY Id DESC", "@n", n, "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> {
                        { "channel", r["Channel"] as string }, { "phone", r["Phone"] as string ?? "" }, { "reason", r["Reason"] as string ?? "" },
                        { "status", r["Status"] as string }, { "note", r["Note"] as string ?? "" }, { "to", r["ToName"] as string ?? "" },   // the team member it went to ("" = the owner)
                        { "at", DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc).ToString("o") } });
            return list;
        }

        static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }
    }
}
