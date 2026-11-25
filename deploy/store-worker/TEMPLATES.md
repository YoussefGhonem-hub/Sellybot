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
