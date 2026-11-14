// api/chat.ashx?store=<slug> — the public chat. Each store gets its own data, bot name, welcome and monthly limit.
// - GET  -> {ok, name, botName, welcome}  (used by the page to set itself up)
// - POST -> {messages:[...]} streams the reply as SSE (same chunk shape the page already reads)
// The Anthropic key stays in web.config; the system prompt is always built here, never taken from the browser.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Anthropic.Models.Messages;

namespace RiwaqChat
{
    public class ChatHandler : HttpTaskAsyncHandler
    {
        const int MaxUserChars = 600;
        const int MaxHistory = 12;
        const string Fallback = "مقدرش أساعد في ده 🙏 بس اسألني عن أي منتج في المتجر وأنا أساعدك.";
        const string Oops = "حصلت مشكلة صغيرة 😕 جرّب تاني بعد شوية.";

        public override bool IsReusable { get { return true; } }

        public override async Task ProcessRequestAsync(HttpContext ctx)
        {
            HttpRequest req = ctx.Request;
            HttpResponse res = ctx.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            if (Claude.ApiKey == null) { Web.Error(res, 503, "not_configured"); return; }

            string legacySlug = Web.Setting("LegacyStore", "riwaq");
            string slug = (req.QueryString["store"] ?? legacySlug).Trim().ToLowerInvariant();
            if (slug.Length > 40) { Web.Error(res, 404, "no_store"); return; }

            // ---- which store? ----
            SqlConnection conn = null;
            StoreInfo store = null;
            try
            {
                if (Db.Configured)
                {
                    conn = Db.Open();
                    store = Db.StoreBySlug(conn, slug);
                    PlanGate.Apply(conn, store);   // what the store's plan doesn't include is off (styles, order tracking)
                }
                if (store == null && slug != legacySlug) { Web.Error(res, 404, "no_store"); return; }

                // ---- cross-site embedding: only the store's own sites (same-site requests carry no Origin or our own) ----
                string origin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !SameSite(req, origin))
                {
                    var allowed = (store == null || store.AllowedOrigins == null) ? new string[0] : store.AllowedOrigins.Split(',');
                    if (!allowed.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)) { Web.Error(res, 403, "origin_not_allowed"); return; }
                    res.AddHeader("Access-Control-Allow-Origin", origin);
                    res.AddHeader("Vary", "Origin");
                    if (req.HttpMethod == "OPTIONS")
                    {
                        res.AddHeader("Access-Control-Allow-Methods", "GET, POST");
                        res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                        res.StatusCode = 204;
                        return;
                    }
                }

                if (store != null && store.Status != "active") { Web.Error(res, 403, "store_off"); return; }

                if (req.HttpMethod == "GET")
                {
                    Web.Json(res, 200, new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "name", store == null ? "رِواق" : store.Name },
                        { "botName", store == null ? "" : store.BotName },
                        { "welcome", store == null ? null : store.Welcome },
                        { "bubbleStyle", store == null ? "bear" : store.BubbleStyle },
                        { "brandColor", store == null ? "#e8774a" : store.BrandColor }, { "chatTitle", store == null ? null : store.ChatTitle },
                        { "suggestions", store == null || string.IsNullOrEmpty(store.Suggestions) ? new string[0] : store.Suggestions.Split('\n') },
                    });
                    return;
                }
                if (req.HttpMethod != "POST") { Web.Error(res, 405, "method"); return; }

                if (!Web.Allow("chat-m:" + Web.Ip(req), 8, TimeSpan.FromMinutes(1)) || !Web.Allow("chat-h:" + Web.Ip(req), 60, TimeSpan.FromHours(1)))
                { Web.Error(res, 429, "rate_limited"); return; }

                // ---- monthly limit per store ----
                // (a plan whose end date passed allows nothing until the super admin renews it)
                // subscription ended: the chat stops until the super admin renews it (the widget says "not available, contact the store")
                if (store != null && Billing.Expired(store)) { Web.Error(res, 403, "plan_expired"); return; }
                if (store != null && Billing.LimitReached(conn, store)) { Web.Error(res, 402, "limit_reached"); return; }

                // keep the store's Shopify data fresh (refreshes in the background when it is older than a few hours)
                if (store != null) { try { Shopify.RefreshIfOld(store); } catch (Exception ex) { Log.Error(ex); } }
                if (store != null) { try { EasyOrders.RefreshIfOld(store); } catch (Exception ex) { Log.Error(ex); } }

                // ---- read the conversation ----
                ArrayList incoming;
                string chatSid = null;   // the widget's chat session (ordering from the chat keeps its cart per session)
                try
                {
                    var input = Web.Body(req, 20000);
                    object m;
                    if (!input.TryGetValue("messages", out m) || !(m is ArrayList)) throw new Exception();
                    incoming = (ArrayList)m;
                    object sidVal; if (input.TryGetValue("sid", out sidVal)) chatSid = ChatOrders.WebSid(sidVal as string);
                }
                catch { Web.Error(res, 400, "bad_request"); return; }

                var messages = new List<MessageParam>();
                int start = Math.Max(0, incoming.Count - MaxHistory);
                string lastRole = "";
                for (int i = start; i < incoming.Count; i++)
                {
                    var item = incoming[i] as Dictionary<string, object>;
                    if (item == null) continue;
                    string role = item.ContainsKey("role") ? item["role"] as string : null;
                    string content = item.ContainsKey("content") ? item["content"] as string : null;
                    if ((role != "user" && role != "assistant") || string.IsNullOrEmpty(content)) continue;
                    if (role == "user" && content.Length > MaxUserChars) { Web.Error(res, 400, "message_too_long"); return; }
                    if (content.Length > 4000) content = content.Substring(0, 4000);
                    if (messages.Count == 0 && role != "user") continue;
                    if (role == lastRole) continue;
                    if (role == "user") messages.Add(new UserMessageParam { Content = content });
                    else messages.Add(new AssistantMessageParam { Content = content });
                    lastRole = role;
                }
                if (lastRole != "user") { Web.Error(res, 400, "bad_request"); return; }

                int maxTokens;
                if (!int.TryParse(Web.Setting("MaxReplyTokens", "1500"), out maxTokens)) maxTokens = 1500;
                StoreIndex idx = null;
                string system = store == null ? Claude.LegacyPrompt() : Claude.StorePrompt(conn, store, out idx);
                var sysBlocks = new List<TextBlockParam> { new TextBlockParam { Text = system, CacheControl = new CacheControlEphemeral() } };
                // ordering from the chat: its instructions + this chat's order so far, after the cached prompt
                var octx = store == null ? null : ChatOrders.ForChannel(conn, store, chatSid, Web.Ip(req));
                if (octx != null) sysBlocks.Add(new TextBlockParam { Text = ChatOrders.PromptPart(store) + "\n\n" + ChatOrders.StateNote(octx) });
                var parameters = new MessageCreateParams
                {
                    Model = Claude.Model,
                    MaxTokens = maxTokens,
                    System = sysBlocks,
                    Messages = messages,
                };

                res.StatusCode = 200;
                res.ContentType = "text/event-stream; charset=utf-8";
                res.BufferOutput = false;
                res.AddHeader("X-Accel-Buffering", "no");

                long inTok = 0, outTok = 0, cacheRead = 0, cacheWrite = 0;
                bool wrote = false;

                // ---- big store (search) and/or order tracking (Shopify): let Claude use its tools, then send the answer ----
                bool orderTool = store != null && OrderTracking.On(store);   // Shopify or EasyOrders
                if (Claude.UseSearch(idx) || orderTool || octx != null)
                {
                    try
                    {
                        var u = await ToolLoop(parameters, messages, Claude.UseSearch(idx) ? idx : null, orderTool ? store : null, Web.Ip(req), octx);
                        inTok = u.In; cacheRead = u.CacheRead; cacheWrite = u.CacheWrite; outTok = u.Out;
                        string answer = string.IsNullOrWhiteSpace(u.Text) ? Fallback : u.Text;
                        for (int i = 0; i < answer.Length; i += 60) Send(res, answer.Substring(i, Math.Min(60, answer.Length - i)));
                    }
                    catch (Exception ex) { Log.Error(ex); Send(res, Oops); }
                    Done(res);
                    if (store != null) { try { Db.AddUsage(conn, store.Id, 1, inTok, cacheRead, cacheWrite, outTok); } catch (Exception ex) { Log.Error(ex); } }
                    return;
                }

                // ---- normal store: all data is in the prompt, stream the reply ----
                try
                {
                    await foreach (RawMessageStreamEvent ev in Claude.Client().Messages.CreateStreaming(parameters))
                    {
                        if (!res.IsClientConnected) break;
                        if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                        {
                            if (!string.IsNullOrEmpty(text.Text)) { Send(res, text.Text); wrote = true; }
                        }
                        else if (ev.TryPickStart(out var started))
                        {
                            inTok = started.Message.Usage.InputTokens;
                            cacheRead = started.Message.Usage.CacheReadInputTokens ?? 0;
                            cacheWrite = started.Message.Usage.CacheCreationInputTokens ?? 0;
                        }
                        else if (ev.TryPickDelta(out var md)) outTok = md.Usage.OutputTokens;
                    }
                    if (!wrote) Send(res, Fallback);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    if (!wrote) Send(res, Oops);
                }
                Done(res);

                if (store != null) { try { Db.AddUsage(conn, store.Id, 1, inTok, cacheRead, cacheWrite, outTok); } catch (Exception ex) { Log.Error(ex); } }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                if (!res.HeadersWritten) Web.Error(res, 500, "server_error");
            }
            finally
            {
                if (conn != null) conn.Dispose();
            }
        }

        public class LoopResult { public string Text; public long In, CacheRead, CacheWrite, Out; }

        // The same assistant as the site chat, without streaming (WhatsApp and other channels).
        // channelNote is added after the cached system prompt, so the prompt cache still works.
        public static async Task<LoopResult> Answer(SqlConnection conn, StoreInfo store, List<MessageParam> messages, string ip, string channelNote)
        {
            int maxTokens;
            if (!int.TryParse(Web.Setting("MaxReplyTokens", "1500"), out maxTokens)) maxTokens = 1500;
            StoreIndex idx;
            string system = Claude.StorePrompt(conn, store, out idx);
            var sys = new List<TextBlockParam> { new TextBlockParam { Text = system, CacheControl = new CacheControlEphemeral() } };
            if (!string.IsNullOrEmpty(channelNote)) sys.Add(new TextBlockParam { Text = channelNote });
            // WhatsApp ("wa:<chat>"): the chat is the customer's own number, so ordering needs no extra code
            var octx = ip != null && ip.StartsWith("wa:") ? ChatOrders.ForChannel(conn, store, ip, ip) : null;
            if (octx != null) sys.Add(new TextBlockParam { Text = ChatOrders.PromptPart(store) + "\n\n" + ChatOrders.StateNote(octx) });
            var parameters = new MessageCreateParams { Model = Claude.Model, MaxTokens = maxTokens, System = sys, Messages = messages };

            bool orderTool = OrderTracking.On(store);   // Shopify or EasyOrders
            if (Claude.UseSearch(idx) || orderTool || octx != null)
                return await ToolLoop(parameters, messages, Claude.UseSearch(idx) ? idx : null, orderTool ? store : null, ip, octx);

            Message resp = await Claude.Client().Messages.Create(parameters);
            var r = new LoopResult { In = resp.Usage.InputTokens, CacheRead = resp.Usage.CacheReadInputTokens ?? 0, CacheWrite = resp.Usage.CacheCreationInputTokens ?? 0, Out = resp.Usage.OutputTokens };
            var sb = new StringBuilder();
            foreach (ContentBlock block in resp.Content) if (block.TryPickText(out TextBlock t)) sb.Append(t.Text);
            r.Text = sb.ToString().Trim();
            return r;
        }

        // Up to 3 tool rounds (data search and/or order lookup), then the answer.
        // Thinking and tool calls are echoed back unchanged, as the API requires.
        static async Task<LoopResult> ToolLoop(MessageCreateParams first, List<MessageParam> conversation, StoreIndex idx, StoreInfo orderStore, string ip, OrderCtx octx = null)
        {
            var tools = new List<ToolUnion>();
            if (octx != null) foreach (var t in ChatOrders.Tools(octx)) tools.Add(t);
            if (idx != null) tools.Add(SearchTool());
            if (orderStore != null) tools.Add(new Tool
            {
                Name = ShopifyAdmin.OrderToolName,
                Description = "يجيب حالة أوردر الزبون من المتجر: اتشحن ولا لأ، حالة الدفع، شركة الشحن ولينك التتبع، ومعاد التوصيل المتوقع. " +
                              "محتاج رقم الأوردر والإيميل أو رقم الموبايل اللي الطلب اتعمل بيه، وبيرجّع التفاصيل بس لو الاتنين مطابقين لنفس الأوردر.",
                InputSchema = new InputSchema
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["order_number"] = JsonSerializer.SerializeToElement(new { type = "string", description = "رقم الأوردر زي ما الزبون كتبه، مثلًا 1052 أو #1052." }),
                        ["contact"] = JsonSerializer.SerializeToElement(new { type = "string", description = "الإيميل أو رقم الموبايل اللي الزبون قال إنه طلب بيه، زي ما كتبه." }),
                    },
                    Required = new List<string> { "order_number", "contact" },
                },
            });
            var result = new LoopResult();
            var msgs = new List<MessageParam>(conversation);
            int maxRounds = octx != null ? 5 : 4; // up to 3 tool rounds (4 while ordering) + the final answer
            for (int round = 0; round < maxRounds; round++)
            {
                bool last = round == maxRounds - 1;
                var p = new MessageCreateParams
                {
                    Model = first.Model,
                    MaxTokens = first.MaxTokens,
                    System = first.System,
                    Messages = msgs,
                    Tools = tools,
                    ToolChoice = last ? (ToolChoice)new ToolChoiceNone() : new ToolChoiceAuto(),
                };
                Message resp = await Claude.Client().Messages.Create(p);
                result.In += resp.Usage.InputTokens;
                result.CacheRead += resp.Usage.CacheReadInputTokens ?? 0;
                result.CacheWrite += resp.Usage.CacheCreationInputTokens ?? 0;
                result.Out += resp.Usage.OutputTokens;

                var text = new StringBuilder();
                var assistant = new List<ContentBlockParam>();
                var toolResults = new List<ContentBlockParam>();
                foreach (ContentBlock block in resp.Content)
                {
                    if (block.TryPickText(out TextBlock t)) { text.Append(t.Text); assistant.Add(new TextBlockParam { Text = t.Text }); }
                    else if (block.TryPickThinking(out ThinkingBlock th)) assistant.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock rt)) assistant.Add(new RedactedThinkingBlockParam { Data = rt.Data });
                    else if (block.TryPickToolUse(out ToolUseBlock tu))
                    {
                        assistant.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                        string found;
                        if (tu.Name == Claude.SearchToolName && idx != null) found = Search.Run(idx, Arg(tu, "query"));
                        else if (octx != null && ChatOrders.ToolNames.Contains(tu.Name)) found = await ChatOrders.Run(tu.Name, tu.Input, octx);
                        else if (tu.Name == ShopifyAdmin.OrderToolName && orderStore != null)
                        {
                            try { found = await OrderTracking.Lookup(orderStore, ip, Arg(tu, "order_number"), Arg(tu, "contact")); }
                            catch (Exception ex) { Log.Error(ex); found = "مقدرناش نوصل لبيانات الأوردرات دلوقتي. اعتذر للزبون وانصحه يكلّم المتجر."; }
                        }
                        else found = "أداة غير معروفة.";
                        toolResults.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = found });
                    }
                }
                if (toolResults.Count == 0) { result.Text = text.ToString().Trim(); return result; }
                msgs.Add(new AssistantMessageParam { Content = assistant });
                msgs.Add(new UserMessageParam { Content = toolResults });
            }
            return result;
        }

        static string Arg(ToolUseBlock tu, string name)
        {
            JsonElement v;
            return tu.Input != null && tu.Input.TryGetValue(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        }

        static Tool SearchTool()
        {
            return new Tool
            {
                Name = Claude.SearchToolName,
                Description = "يدوّر في بيانات المتجر (المنتجات، الأسعار، المواصفات، الشحن، السياسات، الأسئلة الشائعة) ويرجّع أقرب الأجزاء للبحث، ومع كل جزء اسم الملف وتاريخ آخر تحديث.",
                InputSchema = new InputSchema
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "كلمات البحث: اسم المنتج أو الموضوع، ممكن بأكتر من صيغة (عربي/إنجليزي، مفرد/جمع)." }),
                    },
                    Required = new List<string> { "query" },
                },
            };
        }

        static bool SameSite(HttpRequest req, string origin)
        {
            Uri o;
            return Uri.TryCreate(origin, UriKind.Absolute, out o) && string.Equals(o.Authority, req.Url.Authority, StringComparison.OrdinalIgnoreCase);
        }

        static void Send(HttpResponse res, string text)
        {
            var chunk = new Dictionary<string, object>
            {
                { "choices", new object[] { new Dictionary<string, object> { { "delta", new Dictionary<string, object> { { "content", text } } } } } }
            };
            res.Write("data: " + Web.Js.Serialize(chunk) + "\n\n");
            res.Flush();
        }

        static void Done(HttpResponse res)
        {
            try { res.Write("data: [DONE]\n\n"); res.Flush(); } catch { }
        }
    }
}
