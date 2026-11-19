// Handoff to the owner. The assistant calls call_owner when a customer asks for a person from the store, is upset or
// complaining, or needs something that matters and isn't in the store's data (wholesale, a problem with an order, a return).
// The owner gets a WhatsApp alert on their own number (Stores.OwnerPhone, from the store's number) with why, what the
// customer wants and a wa.me link to them. On WhatsApp the customer's number is known; on the site chat the assistant asks
// for it first. When the owner answers on the store's WhatsApp themselves, the bot already steps aside for that chat
// (WhatsAppBot.PauseHours). One alert per customer per 2 hours. Every handoff is listed in the dashboard too.
// Plan gate "handoff" (open until the super admin picks a minimum) on top of the WhatsApp service.
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
        public static ChatHandler.ExtraTool Tool(StoreInfo s, string channel, string chatKey)
        {
            bool web = channel != "wa";   // the site chat, Messenger (fb) and Instagram (ig): the customer's number comes in the call
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
                Run = tu => Alert(s.Id, channel, chatKey, ChatHandler.Arg(tu, "reason"), ChatHandler.Arg(tu, "summary"), web ? ChatHandler.Arg(tu, "phone") : null),
                Prompt = "\n- لو الزبون طلب يكلّم حد من المتجر، أو عنده شكوى أو زعلان، أو عايز حاجة مهمة مش في بيانات المتجر (طلب جملة، مشكلة في أوردر، استرجاع أو استبدال): " +
                         (web ? "اطلب رقم موبايله (واتساب) لو ماقالوش، وبعدين " : "") + "استخدم أداة " + ToolName + " عشان صاحب المتجر يكلّمه بنفسه. ماتوعدش بحاجة مش في إيدك.",
            };
        }

        static async Task<string> Alert(int storeId, string channel, string chatKey, string reason, string summary, string phoneArg)
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
                    using (var cmd = Db.Cmd(c, "INSERT INTO dbo.Handoffs(StoreId, Channel, ChatKey, Phone, Reason, Status) OUTPUT INSERTED.Id VALUES(@s, @ch, @k, @p, @r, 'pending')",
                        "@s", s.Id, "@ch", channel, "@k", Cut(chatKey, 100), "@p", phone, "@r", Cut((reason.Length > 0 ? reason + " — " : "") + summary, 400)))
                        id = Convert.ToInt64(cmd.ExecuteScalar());
                    string status, note = null;
                    if (string.IsNullOrEmpty(s.OwnerPhone)) { status = "noalert"; note = "مفيش رقم ليك في الإعدادات، فالتنبيه ماوصلكش على الواتساب"; }
                    else
                    {
                        var why = await WaNotify.ToOwner(c, s, Message(channel, reason, summary, phone), "sb-hand-" + id);
                        status = why == null ? "sent" : "failed"; note = why;
                    }
                    using (var cmd = Db.Cmd(c, "UPDATE dbo.Handoffs SET Status=@st, Note=@n WHERE Id=@id", "@st", status, "@n", (object)Cut(note, 300) ?? DBNull.Value, "@id", id)) cmd.ExecuteNonQuery();
                    return status == "sent"
                        ? "✅ صاحب المتجر اتبلّغ وهيكلّم الزبون" + (channel == "wa" ? " هنا" : " على رقمه") + " في أقرب وقت. قول للزبون كده بجملة قصيرة ومطمّنة، وماتحددش ميعاد."
                        : "✅ طلب الزبون اتسجّل عند صاحب المتجر وهيتواصل معاه في أقرب وقت. قول للزبون كده بجملة قصيرة، وماتحددش ميعاد.";
                }
            }
            catch (Exception ex) { Log.Error(ex); return "حصلت مشكلة وإحنا بنبلّغ صاحب المتجر. اعتذر للزبون وقوله يكلّم المتجر مباشرة."; }
        }

        static string Message(string channel, string reason, string summary, string phone)
        {
            var sb = new StringBuilder("🙋 *زبون محتاجك*").Append(channel == "wa" ? " (على الواتساب)" : channel == "fb" ? " (على المسنجر)" : channel == "ig" ? " (على الإنستجرام)" : " (من شات موقعك)").Append('\n');
            if (reason.Length > 0) sb.Append("السبب: *").Append(reason).Append("*\n");
            sb.Append("📝 ").Append(summary).Append("\n\n");
            sb.Append("📱 رقمه: +").Append(phone).Append('\n');
            sb.Append("👈 كلّمه على الواتساب: https://wa.me/").Append(phone).Append('\n');
            if (channel == "wa") sb.Append("\n💡 لو رديت عليه من رقم المتجر، المساعد بيسكت في المحادثة دي ساعتين.");
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
            using (var cmd = Db.Cmd(c, "SELECT TOP (@n) Channel, Phone, Reason, Status, Note, CreatedAt FROM dbo.Handoffs WHERE StoreId=@s ORDER BY Id DESC", "@n", n, "@s", storeId))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new Dictionary<string, object> {
                        { "channel", r["Channel"] as string }, { "phone", r["Phone"] as string ?? "" }, { "reason", r["Reason"] as string ?? "" },
                        { "status", r["Status"] as string }, { "note", r["Note"] as string ?? "" },
                        { "at", DateTime.SpecifyKind((DateTime)r["CreatedAt"], DateTimeKind.Utc).ToString("o") } });
            return list;
        }

        static string Cut(string s, int n) { return s == null ? null : (s.Length > n ? s.Substring(0, n) : s); }
    }
}
