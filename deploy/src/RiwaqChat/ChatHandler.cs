// api/chat.ashx?store=<slug> — the public chat. Each store gets its own data, bot name, welcome and monthly limit.
// - GET  -> {ok, name, botName, welcome}  (used by the page to set itself up)
// - POST -> {messages:[...]} streams the reply as SSE (same chunk shape the page already reads)
// The Anthropic key stays in web.config; the system prompt is always built here, never taken from the browser.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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

            // ---- the chat's look (GET) and the browser's cross-site check (OPTIONS): from memory (ChatLook) ----
            // Every page of every store website asks for this when the chat loads, even when nobody opens the chat,
            // so it mustn't be a database query each time.
            if (req.HttpMethod == "GET" || req.HttpMethod == "OPTIONS")
            {
                try
                {
                    var look = ChatLook.Get(slug, Web.Ip(req));
                    if (look == null) { Web.Error(res, 429, "rate_limited"); return; }
                    if (!look.Found && slug != legacySlug) { Web.Error(res, 404, "no_store"); return; }
                    if (!Embed(req, res, look.Origins)) return;
                    if (look.Found && look.Status != "active") { Web.Error(res, 403, "store_off"); return; }
                    if (req.HttpMethod == "GET") Web.Json(res, 200, ctx.Items["shop"] == null ? look.Json : ShopBot.Look(ctx, look.Json));   // a store on SellyStore offers its own questions
                    else Web.Error(res, 405, "method");
                }
                catch (Exception ex) { Log.Error(ex); Web.Error(res, 500, "server_error"); }
                return;
            }
            if (req.HttpMethod != "POST") { Web.Error(res, 405, "method"); return; }

            // before the database, so a flood of messages from one place never reaches it
            if (!Web.Allow("chat-m:" + Web.Ip(req), 8, TimeSpan.FromMinutes(1)) || !Web.Allow("chat-h:" + Web.Ip(req), 60, TimeSpan.FromHours(1)))
            { Web.Error(res, 429, "rate_limited"); return; }

            // ---- the chat itself (POST): the store is read fresh ----
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
                if (!Embed(req, res, store == null || store.AllowedOrigins == null ? new string[0] : store.AllowedOrigins.Split(','))) return;
                if (store != null && store.Status != "active") { Web.Error(res, 403, "store_off"); return; }

                // ---- monthly limit per store ----
                // (a plan whose end date passed allows nothing until the super admin renews it)
                // subscription ended: the chat stops until the super admin renews it (the widget says "not available, contact the store")
                if (store != null && Billing.Expired(store)) { Web.Error(res, 403, "plan_expired"); return; }
                // the free trial is one month from signing up, once: after it the chat stops the same way until the store subscribes
                if (store != null && Billing.TrialEnded(conn, store)) { Web.Error(res, 403, "trial_ended"); return; }
                if (store != null && Billing.LimitReached(conn, store)) { Web.Error(res, 402, "limit_reached"); return; }

                // keep the store's Shopify data fresh (refreshes in the background when it is older than a few hours)
                if (store != null) { try { Shopify.RefreshIfOld(store); } catch (Exception ex) { Log.Error(ex); } }
                if (store != null) { try { EasyOrders.RefreshIfOld(store); } catch (Exception ex) { Log.Error(ex); } }

                // ---- read the conversation ----
                ArrayList incoming;
                var shopCtx = ctx.Items["shop"] as ShopFrontHandler.Ctx;   // the chat inside the store's own page (SellyStore)
                Dictionary<string, object> shopPage = null;                 // what the customer is looking at there
                string chatSid = null;   // the widget's chat session (ordering from the chat keeps its cart per session)
                try
                {
                    var input = Web.Body(req, 20000);
                    object m;
                    if (!input.TryGetValue("messages", out m) || !(m is ArrayList)) throw new Exception();
                    incoming = (ArrayList)m;
                    object sidVal; if (input.TryGetValue("sid", out sidVal)) chatSid = ChatOrders.WebSid(sidVal as string);
                    object pageVal; if (shopCtx != null && input.TryGetValue("shop", out pageVal)) shopPage = pageVal as Dictionary<string, object>;
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
                if (store != null && store.Slug == SiteBot.Slug) maxTokens = Math.Max(maxTokens, 3500);   // سيلّي explains a task step by step: a long how-to must not be cut in the middle
                StoreIndex idx = null;
                // the store's own page (SellyStore): its products, shipping, offers and orders, read when asked (ShopBot.cs).
                // While it's open it is THE store: no Shopify / EasyOrders data, ordering or order tracking next to it (Claude.StorePrompt).
                var shop = ShopBot.For(conn, store, shopCtx != null && shopCtx.Preview);
                string system = store == null ? Claude.LegacyPrompt() : Claude.StorePrompt(conn, store, out idx, shop != null);
                var sysBlocks = new List<TextBlockParam> { new TextBlockParam { Text = system, CacheControl = new CacheControlEphemeral() } };
                // ordering from the chat (Shopify): its instructions + this chat's order so far, after the cached prompt
                var octx = store == null || shop != null ? null : ChatOrders.ForChannel(conn, store, chatSid, Web.Ip(req));
                if (octx != null) sysBlocks.Add(new TextBlockParam { Text = ChatOrders.PromptPart(store) + "\n\n" + ChatOrders.StateNote(octx) });
                if (shop != null)
                {
                    var sd = ShopCache.Get(conn, shop);
                    sysBlocks.Add(new TextBlockParam { Text = ShopBot.Prompt(conn, store, shop, sd, null) + ShopBot.PageNote(sd, shopPage) });
                }
                else if (store != null && octx == null) sysBlocks.Add(new TextBlockParam { Text = ChatOrders.NoOrderNote(store) });   // where the order is really made
                // handoff to the owner from the site chat (the assistant asks for the customer's number first)
                List<ExtraTool> extra = null;
                if (store != null && Handoff.On(conn, store)) extra = new List<ExtraTool> { Handoff.Tool(store, "web", chatSid ?? "ip:" + Web.Ip(req)) };
                if (shop != null) { if (extra == null) extra = new List<ExtraTool>(); extra.AddRange(ShopBot.Tools(store.Id, null, Web.Ip(req), shopCtx != null && shopCtx.Preview)); }
                // Sellybot's own chat: forms the visitor fills inside the chat (sign up, log in, subscribe...) and their own account
                var siteBot = SiteBotActions.For(conn, ctx, store);
                if (siteBot != null) { if (extra == null) extra = new List<ExtraTool>(); extra.AddRange(siteBot.Tools); }
                if (extra != null) sysBlocks.Add(new TextBlockParam { Text = "المحادثة دي على موقع المتجر." + string.Concat(extra.Select(x => x.Prompt)) });
                var parameters = new MessageCreateParams
                {
                    Model = Claude.Model,
                    MaxTokens = maxTokens,
                    System = sysBlocks,
                    Messages = messages,
                };
                bool orderTool = store != null && shop == null && OrderTracking.On(store);   // Shopify or EasyOrders (never next to the store's own)

                // Everything the reply needs from the database is read: the connection goes back to the pool before Claude writes.
                // A reply takes seconds, and holding a connection for it let ~100 chats at once empty the pool and stop the whole
                // site (dashboard included). The tools open their own short connections, and SaveUsage opens a new one at the end.
                if (conn != null) { conn.Dispose(); conn = null; }

                res.StatusCode = 200;
                res.ContentType = "text/event-stream; charset=utf-8";
                res.BufferOutput = false;
                res.AddHeader("X-Accel-Buffering", "no");

                long inTok = 0, outTok = 0, cacheRead = 0, cacheWrite = 0;
                bool wrote = false;

                // ---- big store (search) and/or order tracking (Shopify): let Claude use its tools, then send the answer ----
                if (Claude.UseSearch(idx) || orderTool || octx != null || extra != null)
                {
                    try
                    {
                        var u = await ToolLoop(parameters, messages, Claude.UseSearch(idx) ? idx : null, orderTool ? store : null, Web.Ip(req), octx, extra);
                        inTok = u.In; cacheRead = u.CacheRead; cacheWrite = u.CacheWrite; outTok = u.Out;
                        string answer = string.IsNullOrWhiteSpace(u.Text) ? Fallback : u.Text;
                        for (int i = 0; i < answer.Length; i += 60) Send(res, answer.Substring(i, Math.Min(60, answer.Length - i)));
                        if (siteBot != null && siteBot.Action != null) SendEvent(res, "action", siteBot.Action);   // the widget opens that form
                    }
                    catch (Exception ex) { Log.Error(ex); Send(res, Oops); }
                    Done(res);
                    SaveUsage(store, inTok, cacheRead, cacheWrite, outTok);
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

                SaveUsage(store, inTok, cacheRead, cacheWrite, outTok);
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

        // A tool a feature adds to the conversation (Restock: notify_when_back, Handoff: call_owner), with its line for the prompt
        public class ExtraTool { public Tool Def; public Func<ToolUseBlock, Task<string>> Run; public string Prompt; }

        // What one reply needs from the database (Prepare, while the caller has a connection open), then the reply itself
        // (Run, with no connection held: Claude takes seconds, and many chats at once holding connections emptied the pool).
        public class Prepared
        {
            internal MessageCreateParams Params; internal List<MessageParam> Messages; internal StoreIndex Idx;
            internal StoreInfo OrderStore; internal string Ip; internal OrderCtx Octx; internal List<ExtraTool> Extra;
        }

        // The same assistant as the site chat, without streaming (WhatsApp and other channels).
        // channelNote is added after the cached system prompt, so the prompt cache still works.
        public static async Task<LoopResult> Answer(SqlConnection conn, StoreInfo store, List<MessageParam> messages, string ip, string channelNote, List<ExtraTool> extra = null)
        {
            return await Run(Prepare(conn, store, messages, ip, channelNote, extra));
        }

        public static Prepared Prepare(SqlConnection conn, StoreInfo store, List<MessageParam> messages, string ip, string channelNote, List<ExtraTool> extra = null, bool shopPreview = false)
        {
            int maxTokens;
            if (!int.TryParse(Web.Setting("MaxReplyTokens", "1500"), out maxTokens)) maxTokens = 1500;
            if (store != null && store.Slug == SiteBot.Slug) maxTokens = Math.Max(maxTokens, 3500);   // سيلّي: long step-by-step answers
            StoreIndex idx;
            // the store's own page (SellyStore), on every channel: ShopBot.cs. While it's open it is THE store: no Shopify /
            // EasyOrders data, ordering or order tracking next to it (Claude.StorePrompt, and the callers leave out their own tools)
            var shop = ShopBot.For(conn, store, shopPreview);
            string system = Claude.StorePrompt(conn, store, out idx, shop != null);
            var sys = new List<TextBlockParam> { new TextBlockParam { Text = system, CacheControl = new CacheControlEphemeral() } };
            if (extra != null && extra.Count > 0) channelNote = (channelNote ?? "") + string.Concat(extra.Select(x => x.Prompt ?? ""));
            if (!string.IsNullOrEmpty(channelNote)) sys.Add(new TextBlockParam { Text = channelNote });
            // WhatsApp ("wa:<chat>"): the chat is the customer's own number, so ordering (Shopify) needs no extra code
            var octx = shop == null && ip != null && ip.StartsWith("wa:") ? ChatOrders.ForChannel(conn, store, ip, ip) : null;
            if (octx != null) sys.Add(new TextBlockParam { Text = ChatOrders.PromptPart(store) + "\n\n" + ChatOrders.StateNote(octx) });
            if (shop != null)
            {
                string own = ip != null && ip.StartsWith("wa:") ? WaChat.Digits(ip.Substring(3)) : null;
                sys.Add(new TextBlockParam { Text = ShopBot.Prompt(conn, store, shop, ShopCache.Get(conn, shop), own) });
                extra = extra == null ? new List<ExtraTool>() : new List<ExtraTool>(extra);
                extra.AddRange(ShopBot.Tools(store.Id, own, ip, shopPreview));
            }
            else if (octx == null) sys.Add(new TextBlockParam { Text = ChatOrders.NoOrderNote(store) });   // where the order is really made
            var parameters = new MessageCreateParams { Model = Claude.Model, MaxTokens = maxTokens, System = sys, Messages = messages };
            bool orderTool = shop == null && OrderTracking.On(store);   // Shopify or EasyOrders (never next to the store's own)
            return new Prepared { Params = parameters, Messages = messages, Idx = idx, OrderStore = orderTool ? store : null, Ip = ip, Octx = octx, Extra = extra };
        }

        public static async Task<LoopResult> Run(Prepared p)
        {
            var parameters = p.Params; var idx = p.Idx; var extra = p.Extra;
            if (Claude.UseSearch(idx) || p.OrderStore != null || p.Octx != null || (extra != null && extra.Count > 0))
                return await ToolLoop(parameters, p.Messages, Claude.UseSearch(idx) ? idx : null, p.OrderStore, p.Ip, p.Octx, extra);

            Message resp = await Claude.Client().Messages.Create(parameters);
            var r = new LoopResult { In = resp.Usage.InputTokens, CacheRead = resp.Usage.CacheReadInputTokens ?? 0, CacheWrite = resp.Usage.CacheCreationInputTokens ?? 0, Out = resp.Usage.OutputTokens };
            var sb = new StringBuilder();
            foreach (ContentBlock block in resp.Content) if (block.TryPickText(out TextBlock t)) sb.Append(t.Text);
            r.Text = sb.ToString().Trim();
            return r;
        }

        // Up to 3 tool rounds (data search and/or order lookup), then the answer.
        // Thinking and tool calls are echoed back unchanged, as the API requires.
        static async Task<LoopResult> ToolLoop(MessageCreateParams first, List<MessageParam> conversation, StoreIndex idx, StoreInfo orderStore, string ip, OrderCtx octx = null, List<ExtraTool> extra = null)
        {
            var tools = new List<ToolUnion>();
            if (octx != null) foreach (var t in ChatOrders.Tools(octx)) tools.Add(t);
            if (extra != null) foreach (var x in extra) tools.Add(x.Def);
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
            int maxRounds = octx != null || (extra != null && extra.Any(x => x.Def.Name == ShopBot.TProducts)) ? 5 : 4; // up to 3 tool rounds (4 while ordering) + the final answer
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
                        else if (extra != null && extra.Any(x => x.Def.Name == tu.Name))
                        {
                            try { found = await extra.First(x => x.Def.Name == tu.Name).Run(tu); }
                            catch (Exception ex) { Log.Error(ex); found = "حصلت مشكلة. اعتذر للزبون بلطف."; }
                        }
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

        internal static string Arg(ToolUseBlock tu, string name)
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

        // The usage of one reply, on its own short connection (the chat's connection was given back before Claude wrote).
        static void SaveUsage(StoreInfo store, long inTok, long cacheRead, long cacheWrite, long outTok)
        {
            if (store == null) return;
            try { using (var c = Db.Open()) Db.AddUsage(c, store.Id, 1, inTok, cacheRead, cacheWrite, outTok); }
            catch (Exception ex) { Log.Error(ex); }
        }

        // Cross-site embedding: only the store's own sites (same-site requests carry no Origin, or our own).
        // false = already answered (403, or the browser's OPTIONS check).
        static bool Embed(HttpRequest req, HttpResponse res, string[] allowed)
        {
            string origin = req.Headers["Origin"];
            if (HttpContext.Current != null && HttpContext.Current.Items["shop"] != null) return true;   // the store's own page on SellyStore (ShopFront.cs)
            if (string.IsNullOrEmpty(origin) || SameSite(req, origin)) return true;
            if (!allowed.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)) { Web.Error(res, 403, "origin_not_allowed"); return false; }
            res.AddHeader("Access-Control-Allow-Origin", origin);
            res.AddHeader("Vary", "Origin");
            if (req.HttpMethod != "OPTIONS") return true;
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST");
            res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            res.StatusCode = 204;
            return false;
        }

        // What the chat shows before the first message (GET): name, look, welcome, suggestions. null store = the old built-in store.
        internal static Dictionary<string, object> LookJson(StoreInfo store)
        {
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "name", store == null ? "رِواق" : store.Name },
                { "botName", store == null ? "" : store.BotName },
                { "welcome", store == null ? null : store.Welcome },
                { "bubbleStyle", store == null ? "bear" : store.BubbleStyle },
                { "brandColor", store == null ? "#e8774a" : store.BrandColor }, { "chatTitle", store == null ? null : store.ChatTitle },
                { "logo", LogoHandler.Url(store) }, { "logoFit", LogoHandler.Fit(store) },   // the "logo" bubble style (StoreLogo.cs)
                { "suggestions", store == null || string.IsNullOrEmpty(store.Suggestions) ? new string[0] : store.Suggestions.Split('\n') },
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

        // a named SSE event (the widget ignores names it doesn't know), e.g. "action" for Sellybot's own chat forms
        static void SendEvent(HttpResponse res, string name, string json)
        {
            try { res.Write("event: " + name + "\ndata: " + json + "\n\n"); res.Flush(); } catch { }
        }

        static void Done(HttpResponse res)
        {
            try { res.Write("data: [DONE]\n\n"); res.Flush(); } catch { }
        }
    }

    // The chat's look and the sites allowed to show it, per store, kept in memory for a minute.
    // Every page of every store website asks for it when the chat loads (most visitors never open the chat), so without
    // this each page view was a database query. Saving the settings in the dashboard forgets the store's copy at once;
    // other changes (the plan, a pause by the admin) show within the minute. The real chat (POST) always reads the store fresh.
    public static class ChatLook
    {
        public class Entry { public bool Found; public string Status; public string[] Origins; public Dictionary<string, object> Json; public DateTime Until; }

        static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>();
        static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);
        const int MaxEntries = 20000;   // ids nobody has (someone guessing) are kept too, so they don't reach the database again
        static long nextSweep;

        // null = this visitor asked for too many stores that weren't in memory (someone guessing ids)
        public static Entry Get(string slug, string ip)
        {
            var now = DateTime.UtcNow;
            Entry e;
            if (Cache.TryGetValue(slug, out e) && e.Until > now) return e;
            if (!Web.Allow("chat-look:" + ip, 120, TimeSpan.FromMinutes(1))) return null;
            e = Load(slug);
            e.Until = now + Keep;
            Sweep(now);
            Cache[slug] = e;
            return e;
        }

        public static void Forget(string slug)
        {
            Entry e;
            if (!string.IsNullOrEmpty(slug)) Cache.TryRemove(slug.ToLowerInvariant(), out e);
        }

        static Entry Load(string slug)
        {
            StoreInfo store = null;
            if (Db.Configured)
                using (var c = Db.Open())
                {
                    store = Db.StoreBySlug(c, slug);
                    PlanGate.Apply(c, store);   // a style the plan doesn't include shows as the default
                }
            return new Entry
            {
                Found = store != null,
                Status = store == null ? null : store.Status,
                Origins = store == null || store.AllowedOrigins == null ? new string[0] : store.AllowedOrigins.Split(','),
                Json = ChatHandler.LookJson(store),
            };
        }

        // drops the old copies every few minutes, and everything when someone fills it with made-up ids
        static void Sweep(DateTime now)
        {
            long due = Interlocked.Read(ref nextSweep);
            if (now.Ticks < due && Cache.Count < MaxEntries) return;
            if (Interlocked.CompareExchange(ref nextSweep, now.AddMinutes(5).Ticks, due) != due) return;   // another request is on it
            foreach (var kv in Cache) { Entry x; if (kv.Value.Until <= now) Cache.TryRemove(kv.Key, out x); }
            if (Cache.Count >= MaxEntries) Cache.Clear();
        }
    }
}
