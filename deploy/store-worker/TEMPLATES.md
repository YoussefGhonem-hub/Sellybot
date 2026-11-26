# SellyStore templates: what one is, and the rules it keeps

A store's page is: text from the server (for search engines and slow phones) + `#boot` JSON + `core.css`, `core.js`, `t/<id>.css`, `t/<id>.js`, `run.js` (all in `deploy/site/shop/`).
`core.js` is the engine: it loads the catalog (`/_d/c-<version>.json`), keeps the cart, asks the server for every sum (`/_a/quote`), and draws what every store needs (checkout, thank you, tracking, pages, search, favourites, footer, menu).
**A template only draws.** `t/atelier.js` + `t/atelier.css` is the worked example. Read both, and `core.js`, before writing one.

## The object

```js
(() => {
const t = T.<id> = {
  id, f: "family=...",                 // Google Fonts query (the same as the design)
  fd: [arabicDisplay, englishDisplay], ft: [arabicText, englishText],
  tk: { light: {...}, dark: {...} },   // colours and corners; "ac" is replaced by the owner's colour when they chose one
  sw: [...]                            // a few colours that suit this design (shown to the owner)
};
Object.assign(t, {
  init() {},                           // once, before the first drawing: S.ui.* defaults
  hd: () => html,                      // the header (use hb.menu/search/lang/theme/bag/back and brand("wm"))
  home: () => html, list: a => html, pdp: p => html,      // required
  cart: () => html,                    // only if the design has its own cart PAGE
  cartS: () => html, cartTitle, cartSide: "end|bottom|mid",  // or its own cart SHEET (the old templates)
  cartSide: "drop", dropH: key => html, dropAt: () => element, // or the bag as a dropdown from the header's bag (below: every new template)
  usesSlides: true, acMin: { dark: 3 },  // the template draws the owner's slider itself; the least contrast of --ac on the page (default 1.6)
  co: () => html,                      // only if the design's checkout is really different; otherwise the engine's (restyle it in CSS)
  ft0: v => html,                      // only if the design's footer is different; it must still end with foot()
  views: {ok, track, search, fav, 404, page, cats, ...}, // a page the engine draws, drawn by the design instead (arg = what follows its name in the address); only when it really differs
  act: {name(arg1, arg2, el){}},       // data-a="name:arg1:arg2"
  chg: {name(value, arg1){}},          // data-c="name:arg1"  (select, checkbox: value is true/false)
  inp: {name(value, arg1){}},          // data-i="name:arg1"  (typing, ranges)
  sub: {name(form){}},                 // <form data-s="name">
  after(view) {}, onS(top) {}, live: ["home"], bagbar: () => html /* an element with data-bagbar */, got(p) {}, placed(order) {}
});
})();
```

## The data (never invent any)

- `t.P` all products on sale, `t.cats` top categories (`{id,s,n:[ar,en],im,d,sub:[...],home,menu}`), `CATS[id]`, `P(id)`, `V(variantId)`.
- Product: `{id,s,n:[ar,en],pr,old,lo,hi,cs:[categoryIds],c(main),top(main's parent or itself),im:[{p,w,h,a}],o:[{n:[ar,en],v:[[ar,en,hex]]}],vs:[{id,k,vals:[ar...],can,pr,old,im,st}],av,st,b:[badges],t(created),sd(sold),un(unit word or ""),br(brand),mn,mx,ft,u(countdown end),of(offer id),om,sp:[[key,value,keyEn,valueEn]],x:{},tg(tags),kw,d:[ar,en],full,rel:[ids],sg(size guide)}`.
- `d`, all pictures, `rel`, `sg` come after `need(p)` (the engine calls it when a product page opens, and redraws).
- `p.x` = what the owner typed in this template's own extra fields. **Every extra is optional**: a product without it must look right (that part simply isn't drawn).
- A product can have 0 to 3 options with any names (لون، مقاس، وزن، سعة...). Never assume. `colorOpt(p)`, `sizeOpt(p)`, `sel(p)`, `varOf(p,s)`, `canVal`, `valN`, `optsUI(p)` do the work; a press is `data-a="os:<productId>:<optionIndex>:<valueIndex>"`.
- Quantities are whole numbers of the product's unit (`p.un`: "كيلو", "علبة", "متر"...). Weights are options of the product, each with its own price.
- The look the owner chose: `BOOT.look` (hero {img,imgM,title,text,btn,link}, sections, ann, badges, trust, menu, footer, opts). `BOOT.look.opts["<id>.<name>"]` = this template's own texts and switches, always with a default in code. `BOOT.shop` = the store (name, contact, ship, order...).

## The files, and what the engine already does

- `t/<id>.js`, `t/<id>.css`, `t/<id>.meta.json` (`id`, `n`, `for`, `about`, `look` colours, `sort`, `extras`, `opts`; `hold: true` = merchants see it only after the admin ticks it) and `t/<id>.webp`, a picture of the home page on a phone (624x781). A new template is only these files: the server lists what it finds. Ids: lowercase letters, 3 to 16.
- `secs(skip)` draws every section the owner added, also `look` (a set with its picture): pass in `skip` the kinds the template draws itself.
- `closeS()` and the phone's Back are handled together: never push history for a sheet.
- The chat bubble rises above a bar that sticks to the bottom (fixed or sticky, under 220px high).
- `viewed(p)` when the template opens a product in a window of its own. `addV` returns false at 100 different lines. `BOOT.shop.order.maxQty` is the most of one product.
- `im()` puts the owner's focus point as `object-position`: use `object-fit: cover` and never override it with !important.
- «Most wanted» lists only products with `p.sd > 0 || p.ft`.
- The owner's editor sends the draft look while they type and the page redraws: read `BOOT.look` when drawing, never keep a copy from load time.
- Class names the engine's CSS uses inside `.st`; don't reuse them for something else: `.sec .sl .sm .mi .ci .no .stk0 .tot .opt .fl .err .chips .sheet .toast .ln .og .cs .zs .crk .sk .wrapk .lg .spk .f2 .cdd .hs-s .cktr`.

## What the engine also has (each one only when there is data for it)

- Pages the engine draws itself (more.js, loaded the first time one is needed; a template restyles them, or replaces one with a function of the same name): `account` and its parts (sign in with a WhatsApp code, orders, an order, a return, addresses, codes, details), `rate`, `pay`, `compare`, `blog`, `post`, `cats`. Links: `url("account")` + `go:account`, `go:account:orders`, `go:compare`, `go:blog`, `go:post:<slug>`, `go:cats`. `BOOT.shop.acct` = the owner has accounts on; `ME()` = who signed in on this phone, or null.
- Reviews: `p.rt`, `p.rv`, `rate(p)` (small), `stars(n)`, `revBox(p)` (the whole block; the engine adds it under a product page that has no `class="rvk`). Home: `BOOT.shop.revs`, `BOOT.shop.ugc`, `BOOT.shop.posts`.
- A product: `p.ask` what the customer writes or sends with it (inside `optsUI`; `askUI(p)` for a template with its own options), `p.gw` what goes with it, `p.vd` a short video after `need(p)` (`vidBox(p)`, added by the engine when there is no `class="vdk`), `watch:<id>` on a sold-out one, `cmp:<id>` to compare (`S.cmp`, `t.cmpBar`).
- The bag: `gifts()` (drawn inside `totals()`), `upBox(n)` / `upsell(n)` what can be added (`upadd:<variant>`), `bill().fees`, `.dep`, `.branch`.
- Checkout: `formBits.how()` (pickup; already inside `where()`), `pay()` (ways to pay, gift wrap), `who()` (another recipient). When the owner turns on «ميعاد التوصيل», `ship2.js` adds the day picker (`#ddw`, `.ddg` buttons) at the end of `where()`, and asks the sum again when the city changes: a template that draws its own checkout keeps calling `formBits.where()`.
- «الزبون يختار شركة الشحن» (ShopShipPick.cs): when the owner turns it on with two ways or more, `ship2.js` draws the ways (the store's own delivery and the linked companies) after `where()` as a radio group with each fee and days, from the sum (`S.q.copts`, `S.q.car`), and sends the choice (`car`) with the sum and the order. A template that draws its own checkout keeps calling `formBits.where()` and gets it; `bill().days` is the chosen way's. Nothing to draw by hand. A step that repeats the delivery reads the chosen way from the sum (`S.q.copts.find(o => o.k === (S.co.car || S.q.car))`: `fee`, `free`, `d`, `n`), never a fee of its own. A way can be out of the free delivery (the owner's choice): then `freeOut()` is true (also on a pickup) and a template's own free-delivery bar draws nothing, as `freeBar()` does.
- **What the customer can count on** (`ckTrust()`): `<ul class="cktr" data-cktr>` with only what is true for this store and this order, each `<li>` an icon and a line: `.c` «هنبعتلك على واتساب نأكد الطلب» only when `BOOT.shop.order.confirm` (the owner keeps the confirmation message on AND the store's WhatsApp can send now: the server decides), `.p` «مفيش دفع دلوقتي» only while cash on delivery is the chosen way with no deposit, `.r` the returns days with a link to the returns page only when the owner set both, `.w` a WhatsApp link only when the store has a number and the order's own WhatsApp button is off. Nothing true = `hidden`. core.css draws it plain; the template restyles `.cktr`. When it has `.r`, hide the returns line `place()` writes itself (`#plc > p.mutp`) so it isn't said twice.
- **A second place for the total and the button** (a phone's bar): `retot()` keeps `[data-cktot]` (= `m(bill().tot)`), `[data-ckpl]` (disabled exactly like `#plb`: busy or `bill().stop`) and `[data-cktr]` (drawn again) in step on every change of the bag, the sum, the code or the way to pay. A `data-a="place"` button anywhere places the same order (the same checks and errors as `#plb`); show it only while `#plb` is off the screen.
- Lists: `facets(l).sp` specs worth filtering by, `filt(l, {sp: {[k]: value}, rt: 4})`.
- A form with `data-s="news"`, a `tel` input and a `.err` line signs a number up for the store's WhatsApp news (only when `BOOT.shop.news`).
- A category with `lo` is reached by its link only (`t.cats` leaves it out).
- Gift cards, points, «هات صاحبك» (`rew.js` + `rew.css`, loaded only when the owner turned one on; `BOOT.shop.rw`): the engine draws the pages `gift` and `r` (`url("gift")`, `go:gift`), `menuItems()` may end with `{t: "gift"}`, `totals()` carries the card's part and what is left, the code box comes after `formBits.coupon()`, and the account has `account/rew`. Their classes: `.rw-card .rwg .rwk .rwc .rwp .rwt .rwn .rwr .rwa .rwl .rwi .rwic .rwbal`.
- Their classes (restyle, don't reuse): `.acc .acc-n .acc-b .sbg .lgk .lgc .tls .ork .stp .tmk .inf .rtk .rit .adc .cdc .str .rvk .rvs .rvl .pyb .cmk2 .blg .ctg .cth .upk .upi .brk .faqk .vdk .ugg .rvg .pcs .pca .optl .cdi .sts .rtl`.

## The bag as a dropdown, and the owner's slider (the engine owns the behaviour, the template draws)

- **The dropdown** (`cartSide: "drop"`): every bag button is `data-a="bag"`; the engine gives them `aria-expanded` / `aria-controls="cdd"` / `aria-haspopup`. It appends `<div class="cddb" hidden></div><div id="cdd" class="cdd" role="dialog" hidden>` to `dropAt()` (default: the `header` of the first bag button; it may be `#st` itself, which is never drawn again, so the panel stays the same element) after every drawing, and fills it with `dropH(key)`: the lines (`step(l.i, l)` or `q:<i>:<±1>`, `rm:<i>`, `opts(l)`, `errT(l.err)`; mark `.new` on the line whose `l.k === key`), the template's free-delivery bar, the totals, a `bagco` button and a «عرض السلة» link (`go:cart`), and a close button `data-a="bagx"` (a `data-a="bag"` inside it works too). Empty: a short line and a way to the products.
- The engine calls `dropH` again on every change while it is open, keeping the inside's scroll and the focused button; `key` is the added line for 1.5 s after an add, otherwise "". After a redraw of the page it is back in place at once: same scroll, still open, its timer going on (`.cdd` gets `animation:none` then).
- **Opening**: a bag press opens it (focus goes in), a bag press while open closes it. After a card's «ضيف للسلة» the template calls `dropOpen(key)` with the key `buy()` returns (`buy()` itself shows no note with "drop"; the engine's own card add, quick chooser and «زوّد على طلبك» open it too). Opened by an add it never takes focus and closes 4 s later, not while hovered or focused (then 2 s after leaving, never before the 4 s). `dropOpen` returns false on the cart and checkout pages (they change themselves): a template may show a short note there. `dropOpen("")` = opened after "add them all" (nothing marked). `dropClose()`.
- **Closing**: the bag, ✕, Escape and a press outside (focus back to the bag that opened it, the header's or the dock's, unless the press lands on a field or a button), a link inside it, a page change, Back (no history step: one Back goes back). `S.drop` is true exactly while it is open; `#st` has `data-drop="1"` then (the phone's backdrop, a header that hides on scroll must stay). The chat bubble steps aside while it is open.
- **The template's CSS** places it: under the header, its edge lined up with the bag, `max-height: var(--cddmax)` (set by the engine: the room under its top), lines scrolling inside with its top and bottom fixed; full width under the header on phones with a backdrop. `.cdd` opens with a CSS animation and `.cdd.out` closes (180 ms, `inert` meanwhile: a press goes to the page under it). **The backdrop** `.cddb` is an empty sibling right before `#cdd`: shown and hidden with it (`.cddb.out` while closing, back in place after a redraw), no style from the engine (empty = invisible), a press on it is an outside press. Style it if the design has one (`.cddb{position:fixed;inset:0;...}`); selly and alwan draw theirs in CSS and leave it empty. Class names of the inside are the template's (prefix them: `.y-dd*`, `.a-dd*`).
- **The slider**: `slides()` = `[{img, imgM, title:[ar,en], text, btn, link, x, y, src:"hero"|"slider", i}]`: «أول الصفحة» (`BOOT.look.hero`) when it has a picture or a title, then the items of the FIRST «سلايدر صور» section that is on; an empty language shows the other one; a button with no link goes to all the products (`link(s.link)` makes the address). 0 slides = no slider (the home must still look right), 1 = no arrows, dots or autoplay. `slidePic(s, cls, first)` = the picture (`imgM` on phones, the focus point, `fetchpriority` on the first); no picture = the owner's colour (`--ac`) in the template's CSS.
- Markup the template draws: `<section data-hs aria-roledescription="carousel" aria-label>` with `.hs-s` slides (`role="group" aria-roledescription="slide"`), `[data-hs-go="-1|1"]` arrows, `[data-hs-to=i]` dots (buttons with labels), optional `[data-hs-play]` and `[data-hs-now]`. Then `slider(root, {onShow(i), auto: false})` in `after()`: autoplay 6.5 s with `--hs-p` (0..1) on the root, `.on` / `.was` on slides, `data-dir`, `inert` + `aria-hidden` on hidden slides, arrows, dots, swipe, arrow keys (by the direction), pause on hover / focus / off the screen / a sheet / reduced motion / `motion: none` (`.paused` on the root). With `usesSlides: true`, `secs()` skips only that first slider section; a second one is the engine's.
- In the owner's preview, while they type, the slider shows the slide that changed and waits (8 s), with `.ed` on the root: no entrance animation on every key.
- `stockOf(p, v)` = the stock fact as data `{k: "out"|"low"|"order"|"pre"|"", n, t}` (`stockLine` draws it).

## Season looks, look B and the merchant's domain (the engine's: a template gets them with no code)

- **Season look** (`season.js`, loaded by the engine only while `BOOT.look.season` can show): between its two days the announcement bar becomes `.annb.seab` (the greeting, a few inline SVG drawings, then the owner's own line), the products of the owner's chosen offer get `badge(p)` = `<em class="bdg sea">`, and when the owner typed no headline `BOOT.look.hero.title` holds the season's while it runs: read the headline from `BOOT.look.hero` as always. Colours are set on `#st`: `--sea-b` (the band), `--sea-f` (text on it, at 4.5 or more in both themes) and `--sea-d` (the drawings), with `#st[data-sea="<season>"]`.
- **A template may restyle** `.seab` (height, font, corners, spacing, where the drawings sit) and `.bdg.sea` like its other badges, and may add one small touch of its own under `#st[data-sea]` (a border or a corner in `--sea-d`). It keeps the colours from `--sea-*`, never hides the greeting, never puts anything over products, prices or buttons, moves nothing when `still()`, and never adds a countdown or a promise (a countdown is only `cd(p.u)` of a real offer).
- **Look B** («تجربة شكلين») is the same look with another hero, colour, order of `sections` or template: nothing to do, as long as the template reads `BOOT.look` when drawing (above).
- **The merchant's domain**: links stay relative (`url()`); `BOOT.shop.url` is the store's address, the merchant's own domain once it's live. Never write `.sellystore.online` in a template.

## The rules

1. **Money.** A price shown is `pr/lo/hi/old` of the server or `m(n)` of a sum of `lo`/variant prices. The cart's totals come from `bill()` / `totals()`. No template ever makes a discount, a "bundle price", a delivery price or a delivery date of its own. What the demo did as a bundle discount becomes "add them all" at their normal prices; a real discount is an offer the owner makes (it arrives as `old`, `OFFERS`, and lines in `bill().offs`).
2. **Honesty.** No invented reviews, ratings, viewers, "sold today", stock, countdowns, "same day delivery", origins, guarantees or medical claims. Countdown only with `cd(p.u)`. Low stock only with `stockLine(p, v)` / the `low` badge. Delivery and returns only with `promise()`. Anything that needs a fact about the product comes from `p.x`, `p.sp`, `p.tg` or the description, and disappears without it.
3. **Every text** in Arabic and English: `_("عربي", "English")`, names through `E()/pn()/pnt()`, everything else that came from the owner through `esc()`. Egyptian Arabic, short, no idioms, no «مش/ليه/إيه» overload, no em dashes.
4. **Real addresses.** A link to a product or category is `<a href="${url("p", id)}" data-a="go:p:${id}">`.
5. **Both directions, both themes.** Logical CSS (`inset-inline`, `margin-inline`), text contrast ≥ 4.5 in light and dark, the owner's colour only through `--ac` (fills, with `--on` on it) and `--ai` (text on the page). No horizontal scroll from 320 to 1920. Touch targets ≥ 40px. `prefers-reduced-motion` and `BOOT.look.motion === "none"` mean no movement (`still()`).
6. **Any store.** The design has to stand with 1 product or 500, with no categories, no pictures (`im(null)` draws a box), long names, no hero picture, no English, missing description, sold-out products, options with 12 values. Lists longer than about 60 products are drawn in steps (a "show more" button).
7. **The owner's home sections**: `secs([kinds this template draws itself])` must appear on the home page, so what the owner added shows up. Headline and button of the hero come from `BOOT.look.hero`, with a default when empty.
8. `cartS`/`cart`/`dropH` show `l.err` with `errT(l.err)`, `opts(l)`, `freeBar()`, the offers lines, and the checkout button (`data-a="bagco"`). Removing is `rm:<i>`, the quantity `step(l.i, l)`.
9. CSS is scoped by `.st.t-<id>`; keep the design's own CSS, and only add to it. Nothing outside the store is loaded except Google Fonts.
