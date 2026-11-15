// The only bridge between the app and the pages it shows: window.sellybotApp, read by the dashboard (dashboard.html,
// "what's new + updates"). It can tell the page that a new version of the app is downloaded, and restart into it.
// main.js answers only pages of sellybot.online. Nothing else of Electron or Node reaches the page (contextIsolation + sandbox).
const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("sellybotApp", {
  // cb({ version }) once a new version is downloaded (right away if it already is)
  onUpdate(cb) {
    if (typeof cb !== "function") return;
    let told = false;
    const tell = info => { if (info && !told) { told = true; cb({ version: String(info.version || "") }); } };
    ipcRenderer.on("sb-update-ready", (e, info) => tell(info));
    ipcRenderer.invoke("sb-update-state").then(tell, () => { });
  },
  // close and install the downloaded version, then open again
  restart() { ipcRenderer.invoke("sb-restart"); },
});
