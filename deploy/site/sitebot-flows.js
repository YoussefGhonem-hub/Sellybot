/*
 * Sellybot's own chat: forms inside the chat. widget.js loads this for store "sellybot" only, when the assistant opens a form
 * (the open_form tool -> an SSE "action" event) or the page asks for one (window.storeChat.flow("signup")).
 * Each form asks one question at a time, checks the answer, and sends everything to the same APIs the dashboard uses
 * (api/auth.ashx, api/plans.ashx, api/support.ashx, api/sitebot.ashx). Passwords and payment numbers never reach the assistant.
 * h = the host from widget.js: say, html, ask, choose, api, busy, note, err.
 */
(function () {
  var TITLES = { signup: "إنشاء حساب", login: "تسجيل الدخول", plan: "الاشتراك في باقة", whatsapp: "اشتراك الواتساب", ticket: "تذكرة دعم" };
  var SUPPORT = "https://wa.me/201229391155";

  function en(v) {   // Arabic and Persian digits -> 0-9
    return String(v == null ? "" : v).replace(/[٠-٩]/g, function (d) { return String(d.charCodeAt(0) - 0x660); }).replace(/[۰-۹]/g, function (d) { return String(d.charCodeAt(0) - 0x6F0); });
  }
  function fmt(n) { return Number(n || 0).toLocaleString("en-US"); }
  function normRef(v) { return en(v).toUpperCase().replace(/[^A-Z0-9]/g, ""); }
  function mobile(v) {   // an Egyptian mobile in any common form -> 01xxxxxxxxx, or null
    var d = en(v).replace(/\D/g, "");
    if (/^201[0125]\d{8}$/.test(d)) d = "0" + d.slice(2);
    else if (/^1[0125]\d{8}$/.test(d)) d = "0" + d;
    return /^01[0125]\d{8}$/.test(d) ? d : null;
  }
  function ownerPhone(v) {   // the owner's WhatsApp at sign-up (the server checks the same): Egyptian mobile, or a country code + 10-15 digits
    var d = en(v).replace(/\D/g, "");
    if (d.indexOf("00") === 0) d = d.slice(2);
    if (/^01[0125]\d{8}$/.test(d)) d = "2" + d; else if (/^1[0125]\d{8}$/.test(d)) d = "20" + d;
    if (d.charAt(0) === "0" || d.length < 10 || d.length > 15) return null;
    return d.indexOf("20") === 0 && !/^201[0125]\d{8}$/.test(d) ? null : d;
  }
  function dateAr(iso) { try { return new Date(iso).toLocaleDateString("ar-EG-u-nu-latn", { day: "numeric", month: "long", year: "numeric" }); } catch (e) { return ""; } }
  function dashUrl(h) { return h.base + "dashboard.html"; }
  function goDash(h) { if (/dashboard\.html$/i.test(location.pathname)) location.reload(); else location.href = dashUrl(h); }
  async function whoAmI(h) { var r = await h.api("api/auth.ashx?action=me"); return r.ok && r.data && r.data.slug ? r.data : null; }
  function msgOf(r, fallback) { return (r && r.data && r.data.message) || fallback || "حصلت مشكلة، جرّب تاني."; }

  // the buttons after an account is ready
  async function afterAccount(h, stop, store, isNew) {
    await h.say(isNew
      ? "🎉 **حسابك اتعمل، وانت مسجّل دخول دلوقتي!**\nتجربتك المجانية بدأت: **شهر و500 رسالة** تجرّب بيها المساعد على داتا متجرك.\nلينك الشات بتاعك: " + h.base + "?store=" + store.slug
      : "أهلاً بيك تاني يا **" + store.name + "** 👋 انت مسجّل دخول دلوقتي.");
    if (stop()) return { done: true };
    var pick = await h.choose([
      { label: "افتح لوحة التحكم ←", v: "dash" },
      { label: "💎 اشترك في باقة", v: "plan" },
      { label: "تمام كده 👍", v: "ok" }]);
    if (pick.v === "dash") { goDash(h); return { done: true }; }
    if (pick.v === "plan") return plan(h, stop, {});
    await h.say("تمام 🙌 لو عايز أي حاجة تانية اسألني هنا. ولما تفتح لوحة التحكم ابدأ بـ «داتا المتجر» وارفع منتجاتك أو اربط متجرك.");
    return { done: true };
  }

  // ---------------------------------------------------------------- sign up
  async function signup(h, stop, a) {
    var cur = await whoAmI(h);
    if (cur) { await h.say("انت مسجّل دخول بالفعل بحساب **" + cur.name + "** 👋"); return afterAccount(h, stop, cur, false); }
    await h.say("يلا بينا 🚀 هعملك حسابك في **5 أسئلة** بس، وتجربتك المجانية تبدأ على طول (شهر و500 رسالة، من غير كارت).\nأولًا: **اسم متجرك أو نشاطك** إيه؟");
    var f = { name: "", email: "", phone: "", slug: "", password: "" }, step = "name", suggest = [];
    while (!stop()) {
      if (step === "name") {
        f.name = (await h.ask({ placeholder: "مثلًا: متجر النور", max: 100, check: function (v) { return v.trim().length < 2 ? "اكتب اسم المتجر (حرفين على الأقل) 🙏" : null; } })).trim();
        await h.say("حلو 👌 **إيميلك** إيه؟ هتسجّل بيه الدخول بعد كده.");
        step = "email"; continue;
      }
      if (step === "email") {
        var em = (await h.ask({ type: "email", placeholder: "name@gmail.com", max: 200, autocomplete: "email",
          check: function (v) { return /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(v.trim()) ? null : "الإيميل ده شكله مش مظبوط. اكتبه كامل، مثلًا name@gmail.com"; } })).trim();
        if (stop()) break;
        var r = await h.api("api/sitebot.ashx?action=check&email=" + encodeURIComponent(em));
        if (!r.ok) { await h.say(msgOf(r, "مقدرناش نتأكد من الإيميل. اكتبه تاني.")); continue; }
        if (!r.data.ok) {
          if (!r.data.taken) { await h.say(r.data.message); continue; }
          await h.say("الإيميل ده عنده حساب على Sellybot بالفعل 🙂");
          var p = await h.choose([{ label: "ده حسابي، سجّلني دخول", v: "login" }, { label: "هكتب إيميل تاني", v: "again" }]);
          if (p.v === "login") return login(h, stop, { email: em, cont: a && a.cont });
          await h.say("تمام، اكتب الإيميل التاني:"); continue;
        }
        f.email = em; suggest = r.data.suggest || [];
        if (!f.phone) {
          await h.say("تمام ✅ **رقمك على الواتساب** إيه؟\nرقمك الشخصي (مش لازم يكون رقم المتجر): عليه بيوصلك التقرير الأسبوعي والتنبيهات المهمة، ونتواصل معاك منه لو احتجت مساعدة. مش بيظهر لزباينك.");
          step = "phone"; continue;
        }
        step = "slugask"; continue;
      }
      if (step === "phone") {
        var ph = ownerPhone(await h.ask({ type: "tel", placeholder: "مثلًا: 01001234567", max: 20, autocomplete: "tel",
          check: function (v) { return ownerPhone(v) ? null : "اكتب رقم واتساب صح، مثلًا 01001234567 (أو برمز الدولة لو برّه مصر)."; } }));
        if (stop()) break;
        f.phone = ph;
        if (f.slug) { await h.say("تمام ✅ اكتب **الباسورد** تاني 🔒"); step = "password"; continue; }   // back here after the server refused the number
        step = "slugask"; continue;
      }
      if (step === "slugask") {
        await h.say("تمام ✅ دلوقتي اختار **معرّف لمتجرك** بالإنجليزي (حروف صغيرة وأرقام وشرطة -). هيبان في لينك الشات بتاعك، مثلًا:\n" + h.base + "?store=" + (suggest[0] || "my-store") +
          (suggest.length ? "\nاختار واحد من دول أو اكتب اللي يعجبك:" : ""));
        step = "slug"; continue;
      }
      if (step === "slug") {
        var sl;
        if (suggest.length) {
          var c = await h.choose(suggest.map(function (x) { return { label: x, v: x }; }), { typed: true, placeholder: "أو اكتب معرّف تاني..." });
          sl = c.typed != null ? c.typed : c.v;
        } else sl = await h.ask({ placeholder: "مثلًا: my-store", max: 30 });
        if (stop()) break;
        sl = en(sl).trim().toLowerCase().replace(/\s+/g, "-");
        if (!/^[a-z0-9](?:[a-z0-9-]{1,28})[a-z0-9]$/.test(sl)) { await h.say("المعرّف لازم يكون حروف إنجليزي صغيرة وأرقام وشرطة (-) بس، من 3 لـ 30 حرف، ومايبدأش أو يخلصش بشرطة. مثلًا: my-store"); suggest = []; continue; }
        var rs = await h.api("api/sitebot.ashx?action=check&slug=" + encodeURIComponent(sl));
        if (rs.ok && rs.data && rs.data.ok === false) { suggest = rs.data.suggest || []; await h.say(rs.data.message + (suggest.length ? " جرّب واحد من دول:" : "")); continue; }
        f.slug = sl;
        await h.say("آخر سؤال 🔒 اختار **باسورد** لحسابك (8 حروف أو أرقام على الأقل).\nالباسورد مش بيتبعت للمساعد ولا بيتحفظ في المحادثة.");
        step = "password"; continue;
      }
      if (step === "password") {
        f.password = await h.ask({ type: "password", placeholder: "الباسورد (8 على الأقل)", max: 100, autocomplete: "new-password",
          check: function (v) { return v.length < 8 ? "الباسورد لازم يكون 8 حروف أو أرقام على الأقل." : null; } });
        step = "send"; continue;
      }
      if (step === "send") {
        if (stop()) break;
        h.busy(true); await h.say("⏳ بعمل حسابك…");
        var res = await h.api("api/auth.ashx?action=signup", { name: f.name, slug: f.slug, email: f.email, phone: f.phone, password: f.password });
        h.busy(false); f.password = "";
        if (res.ok && res.data && res.data.slug) {
          if (window.sbTrack) window.sbTrack("CompleteRegistration");   // Sellybot's ads (pixel.js): a new account
          h.note("الزائر عمل حساب جديد على Sellybot من الشات: متجر «" + res.data.name + "» بالمعرّف " + res.data.slug + "، وهو مسجّل دخول دلوقتي وفي التجربة المجانية");
          if (a && a.cont) { await h.say("🎉 حسابك اتعمل وانت مسجّل دخول دلوقتي! نكمّل 👇"); return { store: res.data }; }
          return afterAccount(h, stop, res.data, true);
        }
        var code = res.data && res.data.error;
        await h.say("⚠️ " + msgOf(res));
        if (code === "slug_taken" || code === "reserved_slug" || code === "bad_slug") { step = "slug"; suggest = []; await h.say("اكتب معرّف تاني:"); continue; }
        if (code === "email_taken" || code === "bad_email") { step = "email"; await h.say("اكتب إيميل تاني:"); continue; }
        if (code === "bad_phone") { step = "phone"; await h.say("اكتب رقمك على الواتساب تاني:"); continue; }
        if (code === "weak_password") { step = "password"; continue; }
        if (code === "bad_name") { step = "name"; continue; }
        return { done: false };
      }
    }
    return { cancelled: true, silent: true };
  }

  // ---------------------------------------------------------------- log in
  async function login(h, stop, a) {
    var cur = await whoAmI(h);
    if (cur) { await h.say("انت مسجّل دخول بالفعل بحساب **" + cur.name + "** 👋"); return afterAccount(h, stop, cur, false); }
    var id = a && a.email;
    if (!id) {
      await h.say("تمام 🔑 اكتب **الإيميل أو معرّف المتجر** بتاعك:");
      id = (await h.ask({ placeholder: "الإيميل أو معرّف المتجر", max: 200, autocomplete: "username", check: function (v) { return v.trim().length < 3 ? "اكتب الإيميل أو معرّف المتجر." : null; } })).trim();
    }
    for (var tries = 0; tries < 3 && !stop(); tries++) {
      await h.say(tries ? "اكتب الباسورد تاني:" : "اكتب **الباسورد** 🔒 (مش بيتبعت للمساعد):");
      var pw = await h.ask({ type: "password", placeholder: "الباسورد", max: 100, autocomplete: "current-password" });
      if (stop()) break;
      h.busy(true);
      var r = await h.api("api/auth.ashx?action=login", { email: id, password: pw });
      h.busy(false); pw = "";
      if (r.ok && r.data && r.data.role === "superadmin") { await h.say("ده حساب الإدارة 👑 افتح لوحة الإدارة من هنا: " + h.base + "admin.html"); return { done: true }; }
      if (r.ok && r.data && r.data.slug) {
        h.note("الزائر سجّل دخول من الشات بحساب متجر «" + r.data.name + "»");
        if (a && a.cont) { await h.say("أهلاً يا **" + r.data.name + "** 👋 انت مسجّل دخول دلوقتي. نكمّل 👇"); return { store: r.data }; }
        return afterAccount(h, stop, r.data, false);
      }
      if (r.status === 429) { await h.say("⚠️ " + msgOf(r)); return { done: false }; }
      await h.say("⚠️ " + msgOf(r, "الإيميل أو الباسورد مش مظبوطين."));
      var p = await h.choose([{ label: "جرّب تاني", v: "again" }, { label: "نسيت الباسورد", v: "forgot" }]);
      if (p.v === "forgot") {
        await h.say("ولا يهمك 🙏 كلّمنا على واتساب الدعم وهنساعدك ترجع حسابك: " + SUPPORT + "\nابعتلنا الإيميل أو معرّف المتجر.");
        return { done: true };
      }
    }
    return { done: false };
  }

  // ---------------------------------------------------------------- an account first (plan, WhatsApp, ticket)
  async function needAccount(h, stop, why) {
    var cur = await whoAmI(h);
    if (cur) return cur;
    await h.say(why + " لازم تكون مسجّل دخول على حسابك الأول.");
    var p = await h.choose([{ label: "عندي حساب، سجّلني دخول", v: "login" }, { label: "🚀 اعمل حساب جديد", v: "signup" }]);
    if (stop()) return null;
    var res = p.v === "login" ? await login(h, stop, { cont: true }) : await signup(h, stop, { cont: true });
    return res && res.store ? res.store : null;   // signed in: the form that asked carries on
  }

  // the payment part of a request: total, where to pay, then method / reference / the number paid from / note
  async function pay(h, stop, total, extraWhat, lines) {
    var pub = (await h.api("api/plans.ashx")).data || {};
    var num = en(pub.payNumber || "").replace(/\D/g, "") || "01229391155";
    var box = h.html("💳 <b>المطلوب تحوّله: " + fmt(total) + " ج.م</b>" + (extraWhat ? "<br><small>" + h.esc(extraWhat) + "</small>" : "") +
      (lines || []).map(function (x) { return "<br><small>" + h.esc(x) + "</small>"; }).join("") +
      "<br>حوّل على <b>InstaPay</b> أو <b>أورانج كاش</b> للرقم: <b dir=\"ltr\">" + num + "</b> <button type=\"button\" class=\"copy\">نسخ</button>" +
      (pub.payQr ? "<img class=\"qr\" alt=\"InstaPay QR\" src=\"" + h.base + "api/plans.ashx?payqr=1&v=" + encodeURIComponent(pub.payQr) + "\">" : "") +
      (pub.paymentInfo ? "<div>" + h.md(String(pub.paymentInfo)) + "</div>" : ""));
    var cp = box.querySelector(".copy");
    if (cp) cp.onclick = function () { try { navigator.clipboard.writeText(num); cp.textContent = "اتنسخ ✓"; } catch (e) {} };
    var p = await h.choose([{ label: "✅ حوّلت", v: "paid" }, { label: "⏸️ هحوّل بعدين", v: "later" }]);
    if (p.v === "later") {
      await h.say("تمام 👍 لما تحوّل، قولّي «حوّلت» هنا أو كمّل الطلب من «باقتي» في لوحة التحكم.");
      return null;
    }
    var f = {};
    await h.say("حوّلت بإيه؟");
    var m = await h.choose([{ label: "InstaPay", v: "InstaPay" }, { label: "أورانج كاش", v: "أورانج كاش" }, { label: "كاش", v: "كاش" }]);
    f.method = m.v;
    await h.say("اكتب **الرقم المرجعي** اللي ظهرلك بعد التحويل (في رسالة InstaPay أو أورانج كاش):");
    f.refNo = normRef(await h.ask({ placeholder: "الرقم المرجعي", max: 40,
      check: function (v) { var x = normRef(v); return x.length < 6 || x.length > 30 ? "الرقم المرجعي لازم يكون من 6 لـ 30 حرف أو رقم (إنجليزي). هتلاقيه في رسالة التحويل." : null; } }));
    await h.say("وإيه **الرقم أو الحساب** اللي حوّلت منه؟");
    f.ref = (await h.ask({ placeholder: "مثلًا: 01001234567", max: 100, check: function (v) { return v.trim().length < 3 ? "اكتب الرقم أو الحساب اللي حوّلت منه." : null; } })).trim();
    await h.say("أي ملاحظة للإدارة؟ (اختياري)");
    var n = await h.choose([{ label: "مفيش", v: "" }], { typed: true, placeholder: "اكتب ملاحظتك أو دوس «مفيش»" });
    f.note = n.typed != null ? n.typed.slice(0, 500) : "";
    return stop() ? null : f;
  }

  // sends a request; on a fixable error asks that one thing again
  async function sendRequest(h, stop, body, payInfo, doneText) {
    for (var i = 0; i < 4 && !stop(); i++) {
      h.busy(true); await h.say("⏳ ببعت الطلب…");
      var r = await h.api("api/plans.ashx?action=request", Object.assign({}, body, payInfo));
      h.busy(false);
      if (r.ok && r.data && r.data.ok) { await h.say(doneText); return true; }
      var code = r.data && r.data.error;
      await h.say("⚠️ " + msgOf(r));
      if (code === "need_refno" || code === "ref_used") {
        payInfo.refNo = normRef(await h.ask({ placeholder: "الرقم المرجعي", max: 40,
          check: function (v) { var x = normRef(v); return x.length < 6 || x.length > 30 ? "الرقم المرجعي لازم يكون من 6 لـ 30 حرف أو رقم (إنجليزي)." : null; } }));
        continue;
      }
      if (code === "need_ref") { payInfo.ref = (await h.ask({ placeholder: "الرقم أو الحساب اللي حوّلت منه", max: 100 })).trim(); continue; }
      if (code === "bad_wa_phone") { body.waPhone = await askPhone(h); continue; }
      return false;   // has_pending, next_waiting, no_plan, wa_running...: the message says it all
    }
    return false;
  }

  // what a request comes to, from the server (the same sum that will be recorded): the old price, «دعوة تاجر» months, the WhatsApp numbers
  async function quote(h, body) {
    h.busy(true);
    var r = await h.api("api/plans.ashx?action=request", Object.assign({ dry: true }, body));
    h.busy(false);
    if (r.ok && r.data && r.data.dry) return r.data;
    await h.say("⚠️ " + msgOf(r, "مقدرناش نحسب المبلغ دلوقتي. جرّب تاني، أو ابعت الطلب من «باقتي» في لوحة التحكم."));
    return null;
  }
  function numbersAr(n) { return n === 1 ? "رقم واحد" : n === 2 ? "رقمين" : fmt(n) + (n <= 10 ? " أرقام" : " رقم"); }
  // the lines under the amount: why it's this much
  function why(q) {
    var l = [];
    if (q.oldPrice != null && q.regular > q.oldPrice) l.push("🔒 بسعرك القديم " + fmt(q.oldPrice) + " ج.م بدل " + fmt(q.regular));
    if (q.refAmount > 0) l.push("🎁 خصم دعوة تاجر " + fmt(q.refPct) + "%: −" + fmt(q.refAmount) + " ج.م");
    if (q.waAmount > 0) l.push("📱 الواتساب: " + (q.waAdd ? numbersAr(q.waCount) + " × " + fmt(q.waUnit) + " ج.م × " + fmt(q.waDays) + " يوم من 30 = " + fmt(q.waAmount) + " ج.م"
      : q.waCount > 1 || q.waTotal > 1 ? numbersAr(q.waCount) + " × " + fmt(q.waUnit) + " = " + fmt(q.waAmount) + " ج.م في الشهر" : fmt(q.waAmount) + " ج.م في الشهر"));
    return l;
  }

  async function askPhone(h) {
    return mobile(await h.ask({ type: "tel", placeholder: "01001234567", max: 20, check: function (v) { return mobile(v) ? null : "اكتب رقم موبايل مصري صح، مثلًا 01001234567."; } }));
  }

  async function pendingStop(h, mine) {
    var p = (mine.requests || []).filter(function (x) { return x.status === "pending"; })[0];
    if (!p) return false;
    await h.say("عندك طلب لسه بيتراجع" + (p.at ? " من يوم " + dateAr(p.at) : "") + " ⏳ أول ما الإدارة ترد هتلاقي الرد في «باقتي»، وتقدر تسألني عنه هنا.\nلو عايز تغيّره، الغيه من «باقتي» وابعت طلب جديد.");
    return true;
  }

  // ---------------------------------------------------------------- a plan: new, renew, upgrade or a lower one
  async function plan(h, stop, a) {
    var me = await needAccount(h, stop, "عشان تشترك في باقة،");
    if (!me) return { done: true };
    var pub = (await h.api("api/plans.ashx")).data || {}, mineR = await h.api("api/plans.ashx?mine=1"), mine = mineR.data || {};
    if (!mineR.ok) { await h.say("⚠️ " + msgOf(mineR, "مقدرناش نجيب بيانات حسابك دلوقتي.")); return { done: false }; }
    if (await pendingStop(h, mine)) return { done: true };
    var plans = (pub.plans || []), changes = {};
    (mine.changes || []).forEach(function (c) { changes[c.planId] = c; });
    if (!plans.length) { await h.say("مفيش باقات متاحة دلوقتي 🙏 كلّمنا: " + SUPPORT); return { done: false }; }
    function price(p) { return p.finalPrice != null ? p.finalPrice : p.priceEgp; }
    function sub(p) {
      var c = changes[p.id] || {}, mineP = c.newPrice != null ? c.newPrice : price(p), wa = p.includesWa ? " · الواتساب جواها" : "";   // mineP: with the store's old price, if it has one
      if (c.blocked) return "مش متاحة دلوقتي";
      if (c.kind === "upgrade") return "ترقية · تدفع " + fmt(c.amount) + " ج.م بس" + wa;
      if (c.kind === "renew") return "تجديد · " + fmt(mineP) + " ج.م" + wa;
      if (c.kind === "downgrade") return "تبدأ بعد باقتك · " + fmt(mineP) + " ج.م" + wa;
      return fmt(mineP) + " ج.م في الشهر" + wa;
    }
    var chosen = null, want = String((a && a.plan) || "").replace(/\s/g, "");
    if (want) chosen = plans.filter(function (p) { var n = String(p.name).replace(/\s/g, ""); return n === want || n.indexOf(want) >= 0 || want.indexOf(n) >= 0; })[0] || null;
    if (!chosen) {
      await h.say("اختار الباقة 💎" + (me.plan && me.plan.name ? " (انت دلوقتي على باقة " + me.plan.name + ")" : ""));
      var pk = await h.choose(plans.map(function (p) { return { label: p.name + (p.isFeatured ? " ⭐" : ""), sub: sub(p), plan: p }; }));
      chosen = pk.plan;
    } else await h.say("باقة **" + chosen.name + "**: " + sub(chosen));
    if (stop()) return { cancelled: true, silent: true };
    var ch = changes[chosen.id] || { kind: "new" };
    if (ch.blocked) { await h.say("⚠️ " + ch.blocked); return { done: false }; }
    if (ch.kind === "upgrade") await h.say("⬆️ الترقية بتشتغل أول ما نأكد الدفع، وبتدفع **فرق السعر عن الـ " + fmt(ch.days) + " يوم اللي فاضلين** بس: " + fmt(ch.amount) + " ج.م. وميعاد التجديد بيفضل زي ما هو.");
    else if (ch.kind === "downgrade") await h.say("⬇️ هتفضل على باقة " + (ch.curName || "الحالية") + " لحد آخر يوم فيها، وباقة " + chosen.name + " تبدأ لوحدها يوم " + dateAr(ch.startsAt) + ".");
    else if (ch.kind === "renew") await h.say("🔁 التجديد بيبدأ بعد ما مدتك الحالية تخلص، فمش هتخسر ولا يوم.");
    var body = { planId: chosen.id, months: 1 };
    // the WhatsApp service with it (its own monthly price), unless it already runs. A plan that includes it («الاقتصادية») has nothing to add:
    // the number is only so we know it.
    var wa = me.waService || {}, waPrice = (pub.waAddon && pub.waAddon.price) || 300;
    if (chosen.includesWa) {
      await h.say("📱 **الواتساب جوه الباقة**، من غير أي زيادة: بيتفعّل لوحده أول ما الإدارة توافق ويتجدد معاها. تحب تكتب رقم الواتساب اللي هتربطه؟ (اختياري)");
      var wn = await h.choose([{ label: "📲 أكتب الرقم", v: "yes" }, { label: "بعدين", v: "no" }]);
      if (wn.v === "yes") { await h.say("اكتب **رقم الواتساب** اللي هتربطه بالمساعد:"); body.waPhone = await askPhone(h); }
    } else if (wa.inPlan) {
      await h.say("📱 الواتساب جوه باقتك «" + (wa.planName || "الحالية") + "»" + (wa.until ? " لحد يوم " + dateAr(wa.until) : "") + ". باقة " + chosen.name + " مش فيها الواتساب، فبعد التاريخ ده بيبقى باشتراك شهري لوحده بـ " + fmt(waPrice) + " ج.م، وهنفكّرك قبلها.");
    } else if (!wa.on && !wa.always) {
      await h.say("تحب تضيف **خدمة الواتساب** معاها؟ المساعد يرد على زباينك على رقم واتساب متجرك (" + fmt(waPrice) + " ج.م في الشهر).");
      var w = await h.choose([{ label: "📲 أيوه، ضيفها", v: "yes" }, { label: "لأ، الباقة بس", v: "no" }]);
      if (w.v === "yes") {
        await h.say("اكتب **رقم الواتساب** اللي هتربطه بالمساعد:");
        body.wa = true; body.waPhone = await askPhone(h);
      }
    }
    if (stop()) return { cancelled: true, silent: true };
    var q = await quote(h, body);
    if (!q) return { done: false };
    var payInfo = { method: "—", ref: "—", refNo: "", note: "" };
    if (q.total > 0) {
      var pi = await pay(h, stop, q.total, "باقة " + chosen.name + (chosen.includesWa ? " (الواتساب جواها)" : body.wa ? " + خدمة الواتساب" : ""), why(q));
      if (!pi) return { done: true };
      payInfo = pi;
    }
    var ok = await sendRequest(h, stop, body, payInfo,
      "✅ **اتبعت طلبك!** " + (ch.kind === "downgrade" ? "باقة " + chosen.name + " هتبدأ بعد ما باقتك الحالية تخلص." : "هنفعّل باقة " + chosen.name + (body.wa || chosen.includesWa ? " والواتساب" : "") + " أول ما نتأكد من التحويل، وعادةً في نفس اليوم.") +
      (chosen.includesWa ? " وبعدها تربط رقمك من «🔗 ربط بحساباتك» ← «💬 واتساب»." : "") +
      "\nهتلاقي حالة الطلب ورد الإدارة في «باقتي»، وتقدر تسألني عنه هنا في أي وقت.");
    if (ok) h.note("الزائر بعت من الشات طلب اشتراك في باقة " + chosen.name + (body.wa ? " مع خدمة الواتساب" : "") + "، والطلب مستني مراجعة الإدارة");
    return { done: ok };
  }

  // ---------------------------------------------------------------- the WhatsApp service alone
  async function whatsapp(h, stop) {
    var me = await needAccount(h, stop, "عشان تشترك في الواتساب،");
    if (!me) return { done: true };
    var pub = (await h.api("api/plans.ashx")).data || {}, mineR = await h.api("api/plans.ashx?mine=1"), mine = mineR.data || {};
    if (!mineR.ok) { await h.say("⚠️ " + msgOf(mineR, "مقدرناش نجيب بيانات حسابك دلوقتي.")); return { done: false }; }
    if (await pendingStop(h, mine)) return { done: true };
    var wa = me.waService || {}, price = (pub.waAddon && pub.waAddon.price) || 300;
    if (wa.always) { await h.say("الواتساب شغال عندك على طول ✅ مش محتاج اشتراك."); return { done: true }; }
    // one number, or several for the team: 300 for one, 260 each for more (WaLines.cs); a plan with WhatsApp inside has its first number
    var multi = Math.min(wa.multi || (pub.waAddon && pub.waAddon.multi) || price, price), have = Math.max(wa.count || 1, wa.lines || 1, 1), free = Math.max(0, 10 - Math.max(wa.lines || 0, wa.paidOn || wa.inPlan ? (wa.allowed || 0) : 0));
    var mode = "new", body = { kind: "wa" };
    if (wa.inPlan) {
      await h.say("📱 الواتساب **جوه باقتك «" + (wa.planName || "الحالية") + "»**" + (wa.until ? " لحد يوم " + dateAr(wa.until) : "") + "، وبيتجدد معاها، فمش محتاج اشتراك لوحده ✅\nلو عندك موظفين على أرقام تانية، كل **رقم زيادة** بـ " + fmt(multi) + " ج.م في الشهر.");
      var extra = Math.max(wa.count || 1, (wa.lines || 1) - 1, 1), oA = [];   // the paid numbers on top of the plan's own
      if (wa.paidOn) oA.push({ label: "🔁 جدّد أرقامك الزيادة", sub: numbersAr(extra) + " × " + fmt(multi) + " = " + fmt(extra * multi) + " ج.م، شغالة لحد يوم " + dateAr(wa.paidUntil), v: "renew" });
      if (free > 0) oA.push({ label: "➕ ضيف رقم زيادة", sub: wa.paidOn ? "بتدفع الأيام الباقية بس لحد ميعاد تجديدها" : "", v: "add" });
      if (!oA.length) return { done: true };
      var pA = await h.choose(oA.concat([{ label: "تمام كده 👍", v: "ok" }]));
      if (pA.v === "ok") { await h.say("تمام 🙌 رقمك بتربطه من «🔗 ربط بحساباتك» ← «💬 واتساب»."); return { done: true }; }
      mode = pA.v; have = extra;
    } else if (wa.paidOn) {
      await h.say("خدمة الواتساب شغالة عندك لحد يوم " + dateAr(wa.paidUntil || wa.until) + " ✅ (" + numbersAr(have) + ")");
      var opts = [{ label: "🔁 جدّد الخدمة", sub: (have > 1 ? numbersAr(have) + " × " + fmt(multi) + " = " : "") + fmt(have > 1 ? have * multi : price) + " ج.م، والشهر الجديد بيتضاف بعد آخر يوم", v: "renew" }];
      if (free > 0) opts.push({ label: "➕ ضيف رقم زيادة", sub: "بتدفع الأيام الباقية بس لحد ميعاد تجديدك", v: "add" });
      opts.push({ label: "لأ، شكرًا", v: "no" });
      var pB = await h.choose(opts);
      if (pB.v === "no") { await h.say("تمام 👍 هنفكّرك قبل ميعاد التجديد."); return { done: true }; }
      mode = pB.v;
    } else await h.say("📲 خدمة الواتساب: المساعد يرد على زباينك على **رقم واتساب متجرك** 24 ساعة، بـ " + fmt(price) + " ج.م في الشهر (مع أي باقة أو من غيرها)." + (wa.until ? " آخر اشتراك خلص يوم " + dateAr(wa.until) + "." : ""));
    if (stop()) return { cancelled: true, silent: true };
    var n = mode === "renew" ? have : 1;
    if (mode !== "renew") {
      var most = mode === "add" ? Math.min(free, 10) : 10, cs = [];
      await h.say(mode === "add" ? "كام رقم زيادة؟" : "كام رقم واتساب هتربط؟ رقم واحد بـ " + fmt(price) + "، ولو أكتر من رقم **كل الأرقام بـ " + fmt(multi) + " للرقم**.");
      for (var i = 1; i <= Math.min(3, most); i++) cs.push({ label: numbersAr(i), sub: mode === "add" ? "" : fmt(i > 1 ? i * multi : price) + " ج.م في الشهر", v: i });
      if (mode === "new" && have > 3) cs.unshift({ label: "زي ما كانوا: " + numbersAr(have), sub: fmt(have * multi) + " ج.م في الشهر", v: have });
      if (most > 3) cs.push({ label: "أكتر", v: 0 });
      n = (await h.choose(cs)).v;
      if (!n) {
        await h.say("اكتب **عدد الأرقام** (من 4 لـ " + fmt(most) + "):");
        n = +en(await h.ask({ type: "number", placeholder: "مثلًا 5", max: 2, check: function (v) { var x = +en(v); return x >= 1 && x <= most && x === Math.floor(x) ? null : "اكتب رقم من 1 لـ " + fmt(most) + "."; } }));
      }
    }
    body.count = n; if (mode === "add" && wa.paidOn) body.mode = "add";
    // the number to link: known already on a renewal; more numbers may be linked later
    var known = wa.number || ((mine.requests || []).filter(function (r) { return r.waPhone; })[0] || {}).waPhone || "";
    if (mode === "renew" && known) body.waPhone = known;
    else if (mode === "add") {
      await h.say("تحب تكتب الرقم الجديد اللي هتربطه؟ (اختياري، وتقدر تربطه بعدين من «👥 أرقام الواتساب والفريق»)");
      var pn = await h.choose([{ label: "📲 أكتب الرقم", v: "yes" }, { label: "بعدين", v: "no" }]);
      if (pn.v === "yes") { await h.say("اكتب **رقم الواتساب** الجديد:"); body.waPhone = await askPhone(h); }
    } else {
      await h.say(n > 1 ? "اكتب **رقم الواتساب الأساسي** اللي هتربطه بالمساعد (والباقي تربطهم بعدين):" : "اكتب **رقم الواتساب** اللي هتربطه بالمساعد:");
      body.waPhone = await askPhone(h);
    }
    if (stop()) return { cancelled: true, silent: true };
    var q = await quote(h, body);
    if (!q) return { done: false };
    var what = mode === "add" ? "أرقام واتساب زيادة" : mode === "renew" ? "تجديد خدمة الواتساب شهر" : "اشتراك الواتساب شهر", pi = await pay(h, stop, q.total, what + " (" + numbersAr(q.waCount) + ")", why(q));
    if (!pi) return { done: true };
    var ok = await sendRequest(h, stop, body, pi,
      mode === "add" ? "✅ **اتبعت طلبك!** الأرقام الزيادة بتشتغل أول ما نتأكد من التحويل، وبعدها تربطها من «💬 واتساب» ← «👥 أرقام الواتساب والفريق»."
      : mode === "renew" ? "✅ **اتبعت طلب التجديد!** الشهر الجديد هيتضاف بعد آخر يوم أول ما نتأكد من التحويل."
      : "✅ **اتبعت طلب الواتساب!** هنفعّله أول ما نتأكد من التحويل، وبعدها تربط رقمك من تاب «واتساب» في لوحة التحكم.");
    if (ok) h.note("الزائر بعت من الشات طلب " + what + " (" + numbersAr(q.waCount) + ")، والطلب مستني مراجعة الإدارة");
    return { done: ok };
  }

  // ---------------------------------------------------------------- a support ticket
  async function ticket(h, stop) {
    var me = await needAccount(h, stop, "عشان نفتحلك تذكرة دعم،");
    if (!me) return { done: true };
    await h.say("تمام 🎧 المشكلة في إيه؟");
    var c = await h.choose([{ label: "🛠️ مشكلة تقنية", v: "technical" }, { label: "💳 الدفع والاشتراك", v: "billing" }, { label: "📦 داتا المتجر", v: "data" },
                            { label: "💡 اقتراح", v: "suggestion" }, { label: "حاجة تانية", v: "other" }]);
    await h.say("اكتب **عنوان قصير** للمشكلة:");
    var subject = (await h.ask({ placeholder: "مثلًا: المساعد مش بيرد على الواتساب", max: 150, check: function (v) { return v.trim().length < 3 ? "اكتب عنوان للمشكلة." : null; } })).trim();
    await h.say("اشرحلي **التفاصيل** (إيه اللي حصل، وامتى):");
    var body = (await h.ask({ placeholder: "التفاصيل", max: 4000, check: function (v) { return v.trim().length < 5 ? "اكتب تفاصيل أكتر شوية 🙏" : null; } })).trim();
    if (stop()) return { cancelled: true, silent: true };
    h.busy(true);
    var r = await h.api("api/support.ashx?action=create", { subject: subject, body: body, category: c.v });
    h.busy(false);
    if (r.ok && r.data && r.data.ok) {
      await h.say("✅ **اتفتحت تذكرة رقم #" + r.data.id + "**. هنرد عليك في «الدعم الفني» في لوحة التحكم، وهيظهرلك تنبيه أول ما نرد.\nولو مستعجل، كلّمنا على واتساب: " + SUPPORT);
      h.note("الزائر فتح من الشات تذكرة دعم رقم " + r.data.id + " بعنوان «" + subject + "»");
      return { done: true };
    }
    await h.say("⚠️ " + msgOf(r));
    return { done: false };
  }

  var FLOWS = { signup: signup, login: login, plan: plan, whatsapp: whatsapp, ticket: ticket };
  window.SellybotFlows = {
    title: function (form) { return TITLES[form] || ""; },
    run: function (act, h, stop) { var f = FLOWS[act && act.form]; return f ? f(h, stop, act) : Promise.resolve({ done: false }); }
  };
})();
