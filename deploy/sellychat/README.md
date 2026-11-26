# SellyChat: صندوق رسايل واحد للتاجر وفريقه

SellyChat هو [Chatwoot](https://github.com/chatwoot/chatwoot) بنسخته المفتوحة (Community Edition، رخصة MIT)، وعليه اسم Sellybot ولوجوه. بيشتغل على سيرفر Linux لوحده، جنب موقع Sellybot.

- Sellybot هو اللي ماسك القنوات: الواتساب عن طريق Wapilot، والمسنجر والإنستجرام، وبوت تيليجرام. بيبعت نسخة من كل رسالة لـ SellyChat، ولما حد يرد من SellyChat، Sellybot هو اللي بيوصّل الرد للزبون.
- الكود اللي بيربط Sellybot بـ SellyChat في [SellyChat.cs](../src/RiwaqChat/SellyChat.cs). صفحة التاجر «💬 محادثاتي» في لوحة التحكم، وصفحة الأدمن في admin.html ← SellyChat.

## الملفات هنا

| الملف | بيعمل إيه |
|---|---|
| `Dockerfile` | صورة Chatwoot (`-ce`، من غير فولدر enterprise) + أيقوناتنا واسم SellyChat بدل Chatwoot في نصوص اللوحة |
| `brand/` | اللوجو والأيقونة |
| `compose.yaml` | SellyChat و Postgres و Redis و Caddy (الـ HTTPS) |
| `Caddyfile` | شهادة HTTPS أوتوماتيك للدومين |
| `setup.sh` | بيسطّب كل حاجة، وتقدر تشغّله تاني بعد أي تحديث |
| `setup.rb` | الاسم واللينكات، وحساب الأدمن، ومفتاح Sellybot (Platform App) |

الأسرار بتتعمل على السيرفر نفسه في `.env` و `.env.db`، وعمرها ما بتدخل git.

## أول تشغيل

1. **سيرفر:** Ubuntu 24.04، 2 معالج و 4 جيجا رام و 40 جيجا مساحة على الأقل، من أي شركة.
2. **الدومين على Cloudflare:** اعمل سجل `A` باسم `chat` يشاور على IP السيرفر، وخليه **DNS only** (السحابة رمادي) في الأول، عشان Caddy يقدر يطلّع الشهادة.
3. **الجدار الناري:** افتح بس 22 و 80 و 443.
4. **على السيرفر:**
   ```sh
   sudo apt-get update && sudo apt-get install -y git
   git clone --depth 1 https://github.com/YoussefGhonem-hub/Sellybot.git sellybot && cd sellybot/deploy/sellychat
   sudo SC_DOMAIN=chat.sellybot.online SC_ADMIN_EMAIL=<إيميلك> sh setup.sh
   ```
   - هياخد حوالي 10 دقايق أول مرة.
   - في الآخر هيطبع حاجتين: `PLATFORM_TOKEN=...`، وباسورد حساب الأدمن (بيظهر مرة واحدة بس، احفظه).
5. **في Sellybot:** افتح admin.html ← المميزات ← SellyChat، واكتب:
   - العنوان: `https://chat.sellybot.online`
   - المفتاح: اللي بعد `PLATFORM_TOKEN=`
   - بعدين دوس «حفظ والتأكد منه».
6. **التجربة:**
   - متجر Sellybot نفسه بيشوف «💬 محادثاتي» على طول.
   - عشان متاجر معينة تجرّبه، اكتب الـ slug بتاعها في «متاجر تجرّبه قبل الكل».
   - ولما تحب تفتحه للتجار، اختار أقل باقة فيه من «الباقات والخصومات» ← المميزات.
7. **لو عايز Cloudflare يحمي السيرفر:** بعد ما الشهادة تطلع، خلي السحابة برتقاني، وفي SSL/TLS اختار **Full (strict)**.

## تحديث Chatwoot

1. شوف آخر نسخة `-ce` على [Docker Hub](https://hub.docker.com/r/chatwoot/chatwoot/tags?name=ce)، وغيّر `ARG CHATWOOT=` في `Dockerfile`.
2. شغّل `setup.sh` تاني. هو بيبني الصورة وبيعمل الـ migrations لوحده.
3. اتأكد إن كلمة Chatwoot مش راجعة تظهر في اللوحة.

## الباك أب

قاعدة البيانات (كل يوم بـ cron مثلًا):

```sh
docker compose exec -T postgres pg_dump -U postgres chatwoot | gzip > /root/sellychat-$(date +%F).sql.gz
```

والصور اللي اتبعتت محفوظة في الـ volume اللي اسمه `sellychat_storage`.

## ملاحظات

- **اسم Chatwoot:** النسخة المفتوحة مابتسمحش بتغيير الاسم واللوجو من الإعدادات (ده في الجزء المدفوع). عشان كده بنغيّرهم في الصورة نفسها، وده مسموح في رخصة MIT. والاسم واللوجو بتوع Chatwoot علامة تجارية، فمانقولش للتجار إن الخدمة «من Chatwoot».
- **تطبيق الموبايل:** تطبيق Chatwoot على المتاجر باسمهم، فمش بنعرضه على التجار. التاجر بيفتح SellyChat من المتصفح ويضيفه للشاشة الرئيسية.
- **الإيميلات:** من غير SMTP، SellyChat مش بيبعت إيميلات (زي «نسيت الباسورد»). باسورد الموظف بيتعمل من لوحة Sellybot، وصاحب المتجر بيدخل من هناك.
