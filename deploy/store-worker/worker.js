// SellyStore on Cloudflare: every <name>.sellystore.online reaches this Worker (a proxied wildcard record and the routes
//   *.sellystore.online/*   and   sellystore.online/*).
// It takes the store's name from the address and asks our site for the page (shopfront.ashx), with a secret and the visitor's address.
// Settings of the Worker (Settings > Variables and secrets):
//   SHOP_KEY   secret   the key shown in admin > المتاجر
//   IMAGES     R2 bucket binding (the stores' pictures); without it the pictures come from our site
// What it does besides passing the request on:
//   - keeps a public page for up to a minute (the site says how long), and if our site doesn't answer it shows the last copy it has;
//   - pictures: img.sellystore.online from R2, then from our site;
//   - never keeps a preview, an order or anything a customer sends;
//   - a merchant's own domain (Cloudflare for SaaS custom hostnames: the route */* sends them here too): our site says which store
//     it is, kept a few minutes; anything else gets a plain page;
//   - «تجربة شكلين»: while a store tries two looks, each visitor gets a random cookie (sbab) and each half has its own kept page.
// There is nothing secret in this file.

const ROOT = "sellystore.online";
const SITE = "https://sellybot.online";
const SLUG = /^[a-z0-9](?:[a-z0-9-]{1,28})[a-z0-9]$/;   // the same rule as in Shop.cs
const IMG = /^\d+\/[a-z0-9]{8,40}(-s)?\.(webp|jpg|png|mp4|webm)$/, VID = /\.(mp4|webm)$/;
const STALE = 86400;        // a page kept for when our site is down: a day
const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const HOST = /^(?=.{4,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$/;
// «تجربة شكلين»: <test>-<half>-<who>. The same list as ShopAb.cs: these never get a half and always see A.
const AB = /(?:^|;\s*)sbab=(\d{1,9})-([ab])-([a-f0-9]{16})(?=;|$)/;
const BOT = /bot|crawl|spider|slurp|facebookexternalhit|facebookcatalog|meta-externalagent|whatsapp|telegram|preview|lighthouse|headless|embedly|pinterest|skypeuri|discord|slack|bytespider|petalbot|yandex|baidu|semrush|ahrefs|mj12|python|curl|wget|go-http|java\/|okhttp/i;
const hex = n => [...crypto.getRandomValues(new Uint8Array(n))].map(b => ("0" + b.toString(16)).slice(-2)).join("");
const abCookie = (v, https) => "sbab=" + v + "; Path=/; Max-Age=" + (v ? 2592000 : 0) + "; HttpOnly; SameSite=Lax" + (https ? "; Secure" : "");

// a merchant's own domain -> {s: the store's name, m: where it answers once live} or null; kept 5 minutes (none: 1 minute)
const HOSTS = new Map();
async function hostOf(host, request, env, site) {
  if (!HOST.test(host) || !env.SHOP_KEY) return null;
  const now = Date.now(), e = HOSTS.get(host); if (e && e.until > now) return e.v;
  let v = null;
  try {
    const r = await fetch(site + "/shopfront.ashx?_p=" + encodeURIComponent("_host/" + host), { headers: { "x-shop-key": env.SHOP_KEY, "x-shop-name": "_host", "x-shop-path": "/" + host, "x-shop-ip": request.headers.get("cf-connecting-ip") || "0.0.0.0" } });
    if (r.status === 200) { const j = await r.json(); v = j && typeof j.s === "string" && SLUG.test(j.s) ? { s: j.s, m: typeof j.m === "string" && HOST.test(j.m) ? j.m : "" } : null; }
    else if (r.status !== 404) throw new Error("site " + r.status);
  } catch (x) { return e ? e.v : { down: true }; }   // our site is restarting: the last answer we had
  if (HOSTS.size >= 5000) HOSTS.delete(HOSTS.keys().next().value);
  HOSTS.set(host, { v, until: now + (v ? 300000 : 60000) });
  return v;
}

function page(title, text, status) {
  const body = `<!doctype html><html lang="ar" dir="rtl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex"><title>${esc(title)}</title><style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f3f5f4;color:#17211d;font:17px/1.8 system-ui,'Segoe UI',Tahoma,sans-serif;padding:20px}main{text-align:center;max-width:420px}h1{font-size:26px;margin:0 0 8px}p{margin:0;color:#55635d}@media(prefers-color-scheme:dark){body{background:#121815;color:#e7eeea}p{color:#a1b0a9}}</style></head><body><main><h1>${esc(title)}</h1><p>${esc(text)}</p></main></body></html>`;
  return new Response(body, { status: status || 200, headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store", "x-content-type-options": "nosniff", "x-robots-tag": "noindex" } });
}

async function sameKey(a, b) {
  if (!a || !b) return false;
  const e = new TextEncoder(), [x, y] = await Promise.all([crypto.subtle.digest("SHA-256", e.encode(a)), crypto.subtle.digest("SHA-256", e.encode(b))]);
  const p = new Uint8Array(x), q = new Uint8Array(y); let d = 0;
  for (let i = 0; i < p.length; i++) d |= p[i] ^ q[i];
  return d === 0;
}

// the request as our site gets it: only what it needs, never the visitor's other headers
function toSite(request, url, env, name, site, extra) {
  const h = new Headers();
  for (const k of ["content-type", "accept", "accept-language", "user-agent", "referer"]) { const v = request.headers.get(k); if (v) h.set(k, v); }
  const pv = /(?:^|;\s*)(sbpv=[a-f0-9]{32})/.exec(request.headers.get("cookie") || ""), ab = AB.exec(request.headers.get("cookie") || "");
  const ck = [pv && pv[1], ab && "sbab=" + ab[1] + "-" + ab[2] + "-" + ab[3]].filter(Boolean).join("; "); if (ck) h.set("cookie", ck);
  for (const k in extra || {}) if (extra[k]) h.set(k, extra[k]);
  h.set("x-shop-key", env.SHOP_KEY || ""); h.set("x-shop-name", name); h.set("x-shop-path", encodeURI(decodeURI(url.pathname)));
  h.set("x-shop-ip", request.headers.get("cf-connecting-ip") || "0.0.0.0");
  h.set("x-shop-country", (request.cf && request.cf.country) || ""); h.set("x-shop-query", url.search);
  const init = { method: request.method, headers: h, redirect: "manual" };
  if (request.method === "POST") init.body = request.body, init.duplex = "half";
  // every page has its own address on our site too, so nothing on the way can mix two of them
  return new Request(site + "/shopfront.ashx?_p=" + encodeURIComponent(name + url.pathname) + (url.search ? "&" + url.search.slice(1) : ""), init);
}

// what the visitor gets: the site's answer with our own caching and security headers
function clean(r, extra) {
  const h = new Headers();
  for (const k of ["content-type", "content-security-policy", "x-robots-tag", "location", "retry-after", "etag"]) { const v = r.headers.get(k); if (v) h.set(k, v); }
  const cookies = r.headers.getSetCookie ? r.headers.getSetCookie() : []; for (const c of cookies) if (c.startsWith("sbpv=")) h.append("set-cookie", c);
  h.set("x-content-type-options", "nosniff"); h.set("referrer-policy", "strict-origin-when-cross-origin"); h.set("strict-transport-security", "max-age=15552000");
  for (const k in extra || {}) h.set(k, extra[k]);
  return h;
}

async function image(request, url, env, ctx, site) {
  const path = url.pathname.slice(1);
  if (path.startsWith("_put/")) {
    if (!(await sameKey(request.headers.get("x-shop-key"), env.SHOP_KEY))) return new Response("no", { status: 403 });
    const key = path.slice(5);
    if (!IMG.test(key) || !env.IMAGES) return new Response("no bucket", { status: 400 });
    if (request.method === "PUT") { const body = await request.arrayBuffer(); if (body.byteLength > (VID.test(key) ? 6500000 : 2000000)) return new Response("big", { status: 413 }); await env.IMAGES.put(key, body, { httpMetadata: { contentType: request.headers.get("content-type") || "image/webp" } }); return new Response("ok"); }
    if (request.method === "DELETE") { await env.IMAGES.delete(key); return new Response("ok"); }
    return new Response("no", { status: 405 });
  }
  if (!IMG.test(path) || (request.method !== "GET" && request.method !== "HEAD")) return new Response("", { status: 404 });
  const cache = caches.default, ck = new Request(url.origin + "/" + path), hit = await cache.match(ck);
  if (hit) return hit;
  const head = { "cache-control": "public, max-age=31536000, immutable", "content-security-policy": "default-src 'none'", "x-content-type-options": "nosniff", "access-control-allow-origin": "*", "cross-origin-resource-policy": "cross-origin" };
  // a video is given in the pieces a phone asks for, so it isn't kept here as one answer
  if (VID.test(path)) {
    const range = request.headers.get("range"), vh = { ...head, "accept-ranges": "bytes", "content-type": path.endsWith(".mp4") ? "video/mp4" : "video/webm" };
    if (env.IMAGES) {
      const o = await env.IMAGES.get(path, range ? { range: request.headers } : undefined);
      if (o) { if (range && o.range) { const a = o.range.offset || 0, len = o.range.length || o.size - a; return new Response(o.body, { status: 206, headers: { ...vh, "content-range": "bytes " + a + "-" + (a + len - 1) + "/" + o.size, "content-length": String(len) } }); } return new Response(o.body, { headers: { ...vh, "content-length": String(o.size) } }); }
    }
    const q = toSite(new Request(url, { method: "GET", headers: request.headers }), url, env, "img", site); if (range) q.headers.set("range", range);
    const r = await fetch(q); if (r.status !== 200 && r.status !== 206) return new Response("", { status: r.status === 416 ? 416 : 404, headers: { "cache-control": "public, max-age=60" } });
    for (const k of ["content-range", "content-length"]) { const v = r.headers.get(k); if (v) vh[k] = v; }
    return new Response(request.method === "HEAD" ? null : r.body, { status: r.status, headers: vh });
  }
  let res = null;
  if (env.IMAGES) { const o = await env.IMAGES.get(path); if (o) res = new Response(o.body, { headers: { ...head, "content-type": (o.httpMetadata && o.httpMetadata.contentType) || "image/webp" } }); }
  if (!res) {
    const r = await fetch(toSite(new Request(url, { method: "GET", headers: request.headers }), url, env, "img", site));
    if (r.status !== 200) return new Response("", { status: 404, headers: { "cache-control": "public, max-age=60" } });
    res = new Response(r.body, { headers: { ...head, "content-type": r.headers.get("content-type") || "image/webp" } });
  }
  if (request.method === "GET") ctx.waitUntil(cache.put(ck, res.clone()));
  return res;
}

// the tests run this with their own SITE / ROOT
export function make(opt) {
  const root = (opt && opt.root) || ROOT, site = (opt && opt.site) || SITE, https = !(opt && opt.http);
  return {
    async fetch(request, env, ctx) {
      const url = new URL(request.url), host = url.hostname.toLowerCase();
      if (https && url.protocol === "http:") return Response.redirect("https://" + host + url.pathname + url.search, 301);
      if (host === "www." + root) return Response.redirect((https ? "https://" : "http://") + root + "/", 301);
      let name = host === root ? "" : host.endsWith("." + root) ? host.slice(0, -(root.length + 1)) : null, custom = "";
      if (name === null) {
        // a merchant's own domain: our site says whose (a host nobody has gets the plain page)
        const h = await hostOf(host, request, env, site);
        if (!h) return page("مفيش متجر على العنوان ده", "اتأكد من اللينك واكتبه تاني.", 404);
        if (h.down) return page("المتجر بيتحدّث دلوقتي", "جرّب تفتحه تاني بعد دقيقة.", 503);
        // the bare name and its www: the store answers on one of them
        if (h.m && h.m !== host) return Response.redirect((https ? "https://" : "http://") + h.m + (url.port ? ":" + url.port : "") + url.pathname + url.search, 301);
        name = h.s; custom = host;
      }
      if (name === "img" && !custom) return image(request, url, env, ctx, site);
      if (name === "status" && !custom) {
        if (!(await sameKey(request.headers.get("x-shop-key"), env.SHOP_KEY))) return page("SellyStore", "", 404);
        const t = Date.now(); let up = 0; try { up = (await fetch(site + "/api/health.ashx")).status; } catch (e) { }
        return Response.json({ ok: up === 200, site: up, ms: Date.now() - t, r2: !!env.IMAGES, v: 3 });
      }
      // "xn--" is how a browser writes non-Latin letters: look-alike names, so never a store
      if (name && (!SLUG.test(name) || name.startsWith("xn--") || name.includes("--"))) return page("مفيش متجر بالاسم ده", "اتأكد من اللينك واكتبه تاني.", 404);
      if (!env.SHOP_KEY) return page(name ? "المتجر لسه بيجهز" : "SellyStore", "استنّانا قريب.", 503);
      if (!["GET", "HEAD", "POST"].includes(request.method)) return new Response("", { status: 405 });
      let path; try { path = decodeURI(url.pathname); } catch (e) { return page("الصفحة دي مش موجودة", "", 404); }
      const cookie = request.headers.get("cookie") || "", cache = caches.default, asset = path.startsWith("/_s/") || path === "/widget.js" || path === "/api/logo.ashx", data = path.startsWith("/_d/"), preview = /(?:^|;\s*)sbpv=/.test(cookie);
      const keyUrl = url.origin + url.pathname + url.search;
      // «تجربة شكلين»: a person's half (their cookie's, or a random one on a first visit) has its own kept page; bots and the store's files have none
      const ab = AB.exec(cookie), ua = request.headers.get("user-agent") || "", pg = !asset && !data && !path.startsWith("/_") && !path.startsWith("/api/");
      const pick = pg && ua && !BOT.test(ua) ? (ab ? ab[2] : (crypto.getRandomValues(new Uint8Array(1))[0] & 1 ? "b" : "a")) : "", half = a => keyUrl + (url.search ? "&" : "?") + "_ab=" + a;
      // the cookie a visitor gets with a kept half they didn't have yet
      const give = o => { const m = /^(\d{1,9})-([ab])$/.exec(o && o.headers.get("x-shop-ab") || ""); return m && pick && (!ab || ab[1] !== m[1] || ab[2] !== m[2]) ? abCookie(m[1] + "-" + m[2] + "-" + (ab ? ab[3] : hex(8)), https) : ""; };

      if (path === "/_purge") {
        if (request.method !== "POST" || !(await sameKey(request.headers.get("x-shop-key"), env.SHOP_KEY))) return new Response("", { status: 404 });
        for (const k of [url.origin + "/", url.origin + "/?_ab=a", url.origin + "/?_ab=b"]) await cache.delete(new Request(k));
        return new Response("ok");
      }

      // ---- what may be kept: the shared files, the versioned data, public pages ----
      // (an address that already says _ab is never kept: only this Worker writes that, for the halves)
      const keepable = request.method === "GET" && !(preview && !asset) && !(pg && /[?&]_ab=/.test(url.search));
      let old = null;
      if (keepable) {
        const fresh = o => !!o && (asset || data || Date.now() < +(o.headers.get("x-keep-until") || 0));
        if (pick) old = await cache.match(new Request(half(pick)));
        // the page everyone gets when no test is on; not for a person who already has a half, nor a copy made for a bot while a test is on
        if (!fresh(old) && !(pick && ab)) { const o = await cache.match(new Request(keyUrl)); if (o && !(pick && o.headers.get("x-shop-ab") === "0") && (!old || fresh(o))) old = o; }
        if (fresh(old)) return show(old, asset || data, give(old));
      }
      let r;
      try {
        const ac = new AbortController(), timer = old ? setTimeout(() => ac.abort(), 8000) : null;
        r = await fetch(toSite(request, url, env, name, site, { "x-shop-pick": pick, "x-shop-host": custom }), old ? { signal: ac.signal } : undefined);
        if (timer) clearTimeout(timer);
      } catch (e) { r = null; }
      // our site is restarting or down: the last copy of the page, up to a day old
      if (!r || r.status >= 520 || r.status === 502 || r.status === 504 || (r.status === 503 && !r.headers.get("retry-after"))) {
        if (old) return show(old, asset || data, give(old));
        return request.method === "POST" ? Response.json({ error: "down", message: "المتجر بيتحدّث دلوقتي. جرّب تاني بعد دقيقة." }, { status: 503 }) : page("المتجر بيتحدّث دلوقتي", "جرّب تفتحه تاني بعد دقيقة.", 503);
      }
      const keep = +(r.headers.get("x-shop-keep") || 0), cc = r.headers.get("cache-control") || "";
      const age = /immutable/.test(cc) ? 31536000 : +((/max-age=(\d+)/.exec(cc) || [])[1] || 0);
      if (asset || data) {
        const out = new Response(r.body, { status: r.status, headers: clean(r, { "cache-control": r.status === 200 && age ? cc : "no-store" }) });
        if (keepable && r.status === 200 && age > 0) ctx.waitUntil(cache.put(new Request(keyUrl), out.clone()));
        return out;
      }
      // the site says which half this page is (<test>-<a|b>), "0" (a test is on, this visitor isn't in it) or nothing (no test)
      const abh = pg ? r.headers.get("x-shop-ab") || "" : "", am = /^(\d{1,9})-([ab])$/.exec(abh);
      const setAb = am ? (!ab || ab[1] !== am[1] || ab[2] !== am[2] ? abCookie(am[1] + "-" + am[2] + "-" + (ab ? ab[3] : hex(8)), https) : "") : ab && pg && !abh && r.status < 500 ? abCookie("", https) : "";
      if (keepable && keep > 0 && r.status === 200) {
        const body = await r.arrayBuffer();
        ctx.waitUntil(cache.put(new Request(am ? half(am[2]) : keyUrl), new Response(body, { status: 200, headers: clean(r, { "cache-control": "public, max-age=" + STALE, "x-keep-until": String(Date.now() + keep * 1000), ...(abh ? { "x-shop-ab": abh } : {}) }) })));
        const h = clean(r, { "cache-control": "public, max-age=0, must-revalidate" }); if (setAb) h.append("set-cookie", setAb);
        return new Response(body, { status: 200, headers: h });
      }
      const h = clean(r, { "cache-control": r.headers.get("content-type") === "text/event-stream; charset=utf-8" ? "no-cache" : "no-store" }); if (setAb) h.append("set-cookie", setAb);
      return new Response(r.body, { status: r.status, headers: h });
    }
  };
}
// a copy from the cache, with the headers a visitor should get (ck: their new half's cookie)
function show(r, long, ck) {
  const h = new Headers(r.headers); h.delete("x-keep-until"); h.delete("x-shop-ab");
  if (!long) h.set("cache-control", "public, max-age=0, must-revalidate");
  h.set("x-shop-cache", "hit"); if (ck) h.append("set-cookie", ck);
  return new Response(r.body, { status: r.status, headers: h });
}

export default make();
