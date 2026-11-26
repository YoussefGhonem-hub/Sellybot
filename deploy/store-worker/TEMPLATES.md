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
  cartS: () => html, cartTitle, cartSide: "end|bottom|mid",  // or its own cart SHEET
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
- Class names the engine's CSS uses inside `.st`; don't reuse them for something else: `.sec .sl .sm .mi .ci .no .stk0 .tot .opt .fl .err .chips .sheet .toast .ln .og .cs .zs .crk .sk .wrapk .lg .spk .f2`.

## What the engine also has (each one only when there is data for it)

- Pages the engine draws itself (more.js, loaded the first time one is needed; a template restyles them, or replaces one with a function of the same name): `account` and its parts (sign in with a WhatsApp code, orders, an order, a return, addresses, codes, details), `rate`, `pay`, `compare`, `blog`, `post`, `cats`. Links: `url("account")` + `go:account`, `go:account:orders`, `go:compare`, `go:blog`, `go:post:<slug>`, `go:cats`. `BOOT.shop.acct` = the owner has accounts on; `ME()` = who signed in on this phone, or null.
- Reviews: `p.rt`, `p.rv`, `rate(p)` (small), `stars(n)`, `revBox(p)` (the whole block; the engine adds it under a product page that has no `class="rvk`). Home: `BOOT.shop.revs`, `BOOT.shop.ugc`, `BOOT.shop.posts`.
- A product: `p.ask` what the customer writes or sends with it (inside `optsUI`; `askUI(p)` for a template with its own options), `p.gw` what goes with it, `p.vd` a short video after `need(p)` (`vidBox(p)`, added by the engine when there is no `class="vdk`), `watch:<id>` on a sold-out one, `cmp:<id>` to compare (`S.cmp`, `t.cmpBar`).
- The bag: `gifts()` (drawn inside `totals()`), `upBox(n)` / `upsell(n)` what can be added (`upadd:<variant>`), `bill().fees`, `.dep`, `.branch`.
- Checkout: `formBits.how()` (pickup; already inside `where()`), `pay()` (ways to pay, gift wrap), `who()` (another recipient). When the owner turns on «ميعاد التوصيل», `ship2.js` adds the day picker (`#ddw`, `.ddg` buttons) at the end of `where()`, and asks the sum again when the city changes: a template that draws its own checkout keeps calling `formBits.where()`.
- Lists: `facets(l).sp` specs worth filtering by, `filt(l, {sp: {[k]: value}, rt: 4})`.
- A form with `data-s="news"`, a `tel` input and a `.err` line signs a number up for the store's WhatsApp news (only when `BOOT.shop.news`).
- A category with `lo` is reached by its link only (`t.cats` leaves it out).
- Their classes (restyle, don't reuse): `.acc .acc-n .acc-b .sbg .lgk .lgc .tls .ork .stp .tmk .inf .rtk .rit .adc .cdc .str .rvk .rvs .rvl .pyb .cmk2 .blg .ctg .cth .upk .upi .brk .faqk .vdk .ugg .rvg .pcs .pca .optl .cdi .sts .rtl`.

## The rules

1. **Money.** A price shown is `pr/lo/hi/old` of the server or `m(n)` of a sum of `lo`/variant prices. The cart's totals come from `bill()` / `totals()`. No template ever makes a discount, a "bundle price", a delivery price or a delivery date of its own. What the demo did as a bundle discount becomes "add them all" at their normal prices; a real discount is an offer the owner makes (it arrives as `old`, `OFFERS`, and lines in `bill().offs`).
2. **Honesty.** No invented reviews, ratings, viewers, "sold today", stock, countdowns, "same day delivery", origins, guarantees or medical claims. Countdown only with `cd(p.u)`. Low stock only with `stockLine(p, v)` / the `low` badge. Delivery and returns only with `promise()`. Anything that needs a fact about the product comes from `p.x`, `p.sp`, `p.tg` or the description, and disappears without it.
3. **Every text** in Arabic and English: `_("عربي", "English")`, names through `E()/pn()/pnt()`, everything else that came from the owner through `esc()`. Egyptian Arabic, short, no idioms, no «مش/ليه/إيه» overload, no em dashes.
4. **Real addresses.** A link to a product or category is `<a href="${url("p", id)}" data-a="go:p:${id}">`.
5. **Both directions, both themes.** Logical CSS (`inset-inline`, `margin-inline`), text contrast ≥ 4.5 in light and dark, the owner's colour only through `--ac` (fills, with `--on` on it) and `--ai` (text on the page). No horizontal scroll from 320 to 1920. Touch targets ≥ 40px. `prefers-reduced-motion` and `BOOT.look.motion === "none"` mean no movement (`still()`).
6. **Any store.** The design has to stand with 1 product or 500, with no categories, no pictures (`im(null)` draws a box), long names, no hero picture, no English, missing description, sold-out products, options with 12 values. Lists longer than about 60 products are drawn in steps (a "show more" button).
7. **The owner's home sections**: `secs([kinds this template draws itself])` must appear on the home page, so what the owner added shows up. Headline and button of the hero come from `BOOT.look.hero`, with a default when empty.
8. `cartS`/`cart` show `l.err` with `errT(l.err)`, `opts(l)`, `freeBar()`, the offers lines, and the checkout button (`data-a="bagco"`). Removing is `rm:<i>`, the quantity `step(l.i, l)`.
9. CSS is scoped by `.st.t-<id>`; keep the design's own CSS, and only add to it. Nothing outside the store is loaded except Google Fonts.
