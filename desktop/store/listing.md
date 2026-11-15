# Sellybot on the Microsoft Store: what goes in each field of the submission

Partner Center → Apps and games → Sellybot → **Start submission**. Fill the sections in this order.

Product identity (already in `package.json` → `build.appx`):
- Name: `yghonem.Sellybot`
- Publisher: `CN=DC32D674-82C7-4212-BB8E-A827CDDA1332`
- Store ID: `9PF6W2MNNRK8`
- Store page: https://apps.microsoft.com/detail/9PF6W2MNNRK8

---

## 1. Pricing and availability
- **Markets:** all markets, or at least Egypt and the Arab countries.
- **Visibility:** Public audience, discoverable in the Store.
- **Pricing:** Free.

## 2. Properties
- **Category:** Business
- **Privacy policy URL:** https://sellybot.online/privacy.html
- **Website:** https://sellybot.online/about.html
- **Support contact info:** https://sellybot.online/download.html#faq, or the support email you choose.
- **Product declarations:** leave the defaults, plus one box:
  - Tick **"This product allows users to make purchases, but does not use the Microsoft Store commerce system"**. The «باقتي» tab lets an owner choose a plan and send a payment request (InstaPay or a transfer, activated by the admin). That is a purchase inside the app. Store policy 10.8.2 allows outside payment for non-game apps, but it has to be declared.
  - Do not tick "accessibility guidelines" unless it has actually been tested.
- **System requirements:** Windows 10 version 1809 or later, 64-bit. Internet connection: required.

## 3. Age ratings (IARC questionnaire)
- **Type:** "All other app types", not a game.
- Answer "No" to violence, sex, gambling, alcohol, drugs, and crude language.
- **User interaction:** "No". Owners only see their own store data and talk to Sellybot support. No users meet each other in the app.
- **Shares location:** No.
- **Digital purchases:** Yes. Owners can buy a plan or a top-up from the «باقتي» tab, paying outside the Store.
- The expected result is 3+ / Everyone.

## 4. Packages
- Upload `desktop/dist/Sellybot-Store-<version>.appx`, built with `npm run dist:store` inside `desktop/`.
- The package is not signed: the Store signs it.
- If the form asks why it needs **runFullTrust**, answer:
  > Sellybot is an Electron desktop app (a Win32 app packaged as MSIX); runFullTrust is required for desktop apps.

## 5. Store listings

Add two languages: **Arabic** first, then **English (United States)**. Arabic is the first language our users speak.

### Arabic (العربية)

**Product name:** Sellybot

**Description:**
```
Sellybot على الكمبيوتر: لوحة تحكم متجرك في برنامج لوحده على Windows.

Sellybot مساعد ذكي بيرد على زباين متجرك 24 ساعة من داتا متجرك نفسها: على شات موقعك وعلى واتساب. بيجاوب على أسئلة الأسعار والمقاسات والشحن، ويتابع الأوردرات، ويأكّد الطلبات مع الزباين، ومربوط بـ Shopify و EasyOrders.

البرنامج بيديك نفس لوحة التحكم بكل مميزاتها وبنفس الشكل، بس أسرع في إيدك:
• أيقونة على سطح المكتب: دوسة واحدة وتبقى في لوحة متجرك
• بيفتكر تسجيل دخولك
• كل ميزة جديدة بتظهر فيه على طول، ومن غير ما تنزّل حاجة
• آمن: بيفتح لوحة Sellybot بس، وأي لينك تاني بيفتح في المتصفح

من اللوحة تقدر:
• ترفع داتا متجرك (PDF و Word و Excel ونصوص) والمساعد يتعلّم منها
• تربط متجرك على Shopify أو EasyOrders والمنتجات تتحدّث لوحدها
• تختار شكل الشات من أكتر من 30 شكل وأي لون يناسب براندك
• تربط واتساب متجرك والمساعد يرد عليه
• تتابع الاستهلاك والباقة والدعم الفني

محتاج حساب على sellybot.online (فيه تجربة مجانية).
```

**What's new in this version:**
```
• أول نسخة من Sellybot على Microsoft Store
• شريط بيقولك لما تنزل ميزة جديدة، ونافذة «الجديد في Sellybot»
```

**Product features** (one feature per line in the form):
```
مساعد ذكي بيرد على زباينك 24 ساعة من داتا متجرك
لوحة تحكم متجرك في برنامج لوحده على سطح المكتب
ربط Shopify و EasyOrders والمنتجات بتتحدّث لوحدها
المساعد على واتساب متجرك
تتبع الأوردرات وتأكيد الطلبات مع الزباين
أكتر من 30 شكل للشات وأي لون
بيفتكر تسجيل دخولك وبيتحدّث لوحده
```

**Search terms** (7 at most):
```
متجر
شات بوت
خدمة عملاء
Shopify
EasyOrders
واتساب
chatbot
```

**Screenshots:** the files in `desktop/store/screenshots/`, in name order. Each is 1920×1080.

**Store logos:** upload `desktop/store/logos/box-1080.png` as the **1:1 Box art**. Leave the 9:16 poster art empty: it is only needed for Xbox. Leave trailers empty too.

**Copyright and trademark info:** © 2026 Sellybot

### English (United States)

**Description:**
```
Sellybot for Windows: your store's dashboard in its own app.

Sellybot is an AI assistant that answers your store's customers 24/7 from your own store data, on your website chat and on WhatsApp. It answers questions about prices, sizes and shipping, tracks orders, confirms orders with customers, and connects to Shopify and EasyOrders.

The app gives you the same dashboard, with every feature and the same look, one click away:
• A desktop icon: one click and you're in your dashboard
• Stays signed in
• New features show up right away, with nothing to reinstall
• Safe: it only opens the Sellybot dashboard; any other link opens in your browser

From the dashboard you can:
• Upload your store data (PDF, Word, Excel or text) for the assistant to learn from
• Link your Shopify or EasyOrders store and keep products in sync
• Pick a chat style from 30+ designs and any brand color
• Connect your store's WhatsApp number
• Follow your usage, your plan and support

Requires a sellybot.online account (free trial available). The dashboard is in Arabic.
```

**What's new in this version:**
```
• First release of Sellybot on the Microsoft Store
• An update bar that tells you when a new feature ships, and a "What's new" window
```

**Product features:**
```
AI assistant that answers your customers 24/7 from your store data
Your store dashboard in its own desktop app
Shopify and EasyOrders integration with automatic product sync
The assistant on your store's WhatsApp
Order tracking and order confirmation with customers
30+ chat styles and any brand color
Stays signed in and updates itself
```

**Search terms:** `store`, `chatbot`, `customer service`, `Shopify`, `EasyOrders`, `WhatsApp`, `AI assistant`

## 6. Submission options → Additional Testing Information

Keep the publishing hold on "Publish this submission as soon as it passes certification". Then open the **Additional Testing Information** link.

**Credentials:** add the test account with **+ New credential**, once for each row. Credentials go only in this table, never in the description, the repo or a chat:
- Name `Email`, Value: the test account's email
- Name `Password`, Value: the test account's password

**Description** (no credentials here):
```
Sellybot is the desktop client of Sellybot (https://sellybot.online), a web dashboard for store owners whose AI assistant answers their customers. The app shows the dashboard; links to other websites open in the default browser.

To test: open the app and sign in with the email and password listed under Credentials. Then browse the tabs at the top (store data, chat look, add to your store, WhatsApp, assistant, plan).

The dashboard is in Arabic. runFullTrust is required because this is an Electron desktop app (Win32 packaged as MSIX).

Purchases: in the "My plan" (باقتي) tab an owner can choose a plan and send a request after paying by bank or InstaPay transfer, outside the Microsoft Store. The admin activates the plan after checking the payment. This is declared in Properties (purchases that don't use the Store commerce system).
```

Then click **Submit to the Store**. Certification usually takes 1 to 3 business days.
