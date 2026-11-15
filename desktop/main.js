// Sellybot for Windows: the store owner's dashboard (https://sellybot.online) in its own window.
// Nothing of the site is copied here: every page loads live from the site, so whatever changes there shows up in the app at once,
// and there is nothing to reinstall. This file only adds what a browser gives for free: one window, copy/paste menu, reload and
// zoom keys, an offline screen, and links that leave sellybot.online (WhatsApp, Shopify, EasyOrders...) opening in the browser.
// The login stays saved between runs (the app's own cookies, removed on uninstall).
// SELLYBOT_URL points it at another copy of the site (local tests), e.g. SELLYBOT_URL=http://localhost:5710/dashboard.html
const { app, BrowserWindow, Menu, shell, clipboard, session, screen } = require("electron");
const path = require("path");
const fs = require("fs");

const HOME = process.env.SELLYBOT_URL || "https://sellybot.online/dashboard.html";
const ORIGIN = new URL(HOME).origin;
const OFFLINE = path.join(__dirname, "offline.html");
const ICON = path.join(__dirname, "build", "icon.png");
const STATE = () => path.join(app.getPath("userData"), "window.json");

// pages of the site stay in the app; everything else goes to the default browser
function inside(url) { try { return new URL(url).origin === ORIGIN; } catch { return false; } }
function isOffline(url) { return url.startsWith("file:") && decodeURIComponent(new URL(url).pathname).endsWith("offline.html"); }
function openOutside(url) { if (/^(https?|mailto|tel):/i.test(url)) shell.openExternal(url); }

// ---------- one copy of the app ----------
if (!app.requestSingleInstanceLock()) { app.quit(); return; }
app.on("second-instance", () => {
  const w = BrowserWindow.getAllWindows()[0];
  if (w) { if (w.isMinimized()) w.restore(); w.show(); w.focus(); }
});
app.setAppUserModelId("online.sellybot.desktop");   // taskbar grouping and the name on Windows notifications

// ---------- window size and place, as the owner left them ----------
function savedBounds() {
  try {
    const b = JSON.parse(fs.readFileSync(STATE(), "utf8"));
    const area = screen.getDisplayMatching(b).workArea;   // still on a connected screen?
    if (b.width >= 400 && b.height >= 400 && b.x < area.x + area.width - 50 && b.y < area.y + area.height - 50 && b.x + b.width > area.x + 50 && b.y >= area.y - 10) return b;
  } catch { }
  return null;
}
function saveBounds(win) {
  try { fs.writeFileSync(STATE(), JSON.stringify({ ...win.getNormalBounds(), maximized: win.isMaximized() })); } catch { }
}

// ---------- what every window of the app gets ----------
function wire(win) {
  const wc = win.webContents;

  // links: new windows of the site open as app windows, the rest in the browser
  wc.setWindowOpenHandler(({ url }) => {
    if (inside(url)) return { action: "allow", overrideBrowserWindowOptions: { icon: ICON, autoHideMenuBar: true, backgroundColor: "#ffffff", webPreferences: prefs() } };
    openOutside(url);
    return { action: "deny" };
  });
  wc.on("did-create-window", w => wire(w));
  const guard = (e, url) => { if (!inside(url) && !isOffline(url)) { e.preventDefault(); openOutside(url); } };
  wc.on("will-navigate", guard);
  wc.on("will-redirect", guard);

  // no internet / site unreachable: a friendly screen that comes back by itself
  wc.on("did-fail-load", (e, code, desc, url, isMain) => {
    if (!isMain || code === -3 || !inside(url)) return;   // -3 = cancelled (a new navigation started)
    wc.loadFile(OFFLINE, { query: { back: url } });
  });

  // right-click: cut / copy / paste (Windows has no menu for it inside an app)
  wc.on("context-menu", (e, p) => {
    const items = [];
    if (p.isEditable) items.push({ role: "cut", label: "قص" }, { role: "copy", label: "نسخ" }, { role: "paste", label: "لصق" }, { type: "separator" }, { role: "selectAll", label: "تحديد الكل" });
    else if (p.selectionText && p.selectionText.trim()) items.push({ role: "copy", label: "نسخ" });
    if (p.linkURL && !p.isEditable) items.push({ label: "نسخ اللينك", click: () => clipboard.writeText(p.linkURL) });
    if (p.mediaType === "image" && p.srcURL) items.push({ label: "نسخ الصورة", click: () => wc.copyImageAt(p.x, p.y) });
    if (items.length) Menu.buildFromTemplate(items).popup({ window: win });
  });

  // keys a browser has: F5 / Ctrl+R reload, Ctrl + / - / 0 zoom, Alt+← back, Ctrl+P print
  wc.on("before-input-event", (e, i) => {
    if (i.type !== "keyDown") return;
    const k = i.key.toLowerCase(), ctrl = i.control || i.meta, nav = wc.navigationHistory;
    let done = true;
    if (k === "f5" || (ctrl && k === "r")) wc.reload();
    else if (ctrl && (k === "=" || k === "+")) wc.setZoomLevel(Math.min(wc.getZoomLevel() + 0.5, 4));
    else if (ctrl && k === "-") wc.setZoomLevel(Math.max(wc.getZoomLevel() - 0.5, -3));
    else if (ctrl && k === "0") wc.setZoomLevel(0);
    else if (i.alt && k === "arrowleft" && nav.canGoBack()) nav.goBack();
    else if (i.alt && k === "arrowright" && nav.canGoForward()) nav.goForward();
    else if (ctrl && k === "p") wc.print();
    else if (k === "f12" && !app.isPackaged) wc.toggleDevTools();
    else done = false;
    if (done) e.preventDefault();
  });
}

function prefs() {
  return { contextIsolation: true, sandbox: true, nodeIntegration: false, spellcheck: false };
}

function createMain() {
  const b = savedBounds();
  const win = new BrowserWindow({
    width: b ? b.width : 1280, height: b ? b.height : 820, x: b ? b.x : undefined, y: b ? b.y : undefined,
    minWidth: 380, minHeight: 500, title: "Sellybot", icon: ICON, show: false,
    autoHideMenuBar: true, backgroundColor: "#ffffff", webPreferences: prefs(),
  });
  win.removeMenu();
  wire(win);
  win.once("ready-to-show", () => { if (b && b.maximized) win.maximize(); win.show(); });
  win.on("close", () => saveBounds(win));
  win.loadURL(HOME);
  return win;
}

app.whenReady().then(() => {
  // the site can tell it's inside the app (to hide "download the app"), and gets nothing it didn't ask for
  const s = session.defaultSession;
  s.setUserAgent(s.getUserAgent().replace(/\s*Electron\/\S+/, "").replace(/\s*sellybot(-desktop)?\/\S+/i, "") + " SellybotDesktop/" + app.getVersion());
  const allowed = new Set(["clipboard-sanitized-write", "clipboard-read", "notifications", "fullscreen"]);
  s.setPermissionRequestHandler((wc, perm, cb, d) => cb(allowed.has(perm) && inside(d.requestingUrl || wc.getURL())));
  s.setPermissionCheckHandler((wc, perm, origin) => allowed.has(perm) && inside(origin));
  Menu.setApplicationMenu(null);
  createMain();
  updates();
});

// ---------- updates of the app itself ----------
// The site updates by itself; only this shell needs new versions (a new Electron brings Chromium's security fixes).
// It looks at GitHub Releases quietly at start and every 6 hours, downloads in the background and installs when the app is closed.
// No internet or no new release: nothing happens.
function updates() {
  if (!app.isPackaged) return;
  let au;
  try { au = require("electron-updater").autoUpdater; } catch { return; }
  au.logger = null;
  au.autoDownload = true;
  au.autoInstallOnAppQuit = true;
  au.on("error", () => { });
  const check = () => au.checkForUpdates().catch(() => { });
  check();
  setInterval(check, 6 * 60 * 60 * 1000);
}

app.on("window-all-closed", () => app.quit());
