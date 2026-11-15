# Sellybot for Windows

The store owner's dashboard (https://sellybot.online/dashboard.html) in its own Windows app, built with Electron.

The app copies nothing from the site: every page loads live. Site changes show up in the app at once, with no new release. Only changes in this folder need a release.

What the app adds on top of the site:
- one window, with a desktop shortcut and a Start-menu shortcut
- the login stays saved between runs
- a right-click copy/paste menu
- F5 reloads; Ctrl + / Ctrl − / Ctrl 0 zoom
- an offline screen that retries by itself
- updates itself from GitHub Releases

Pages on sellybot.online stay inside the app. Any other link (WhatsApp, Shopify, EasyOrders…) opens in the default browser.

## Run and build locally

```
npm install
npm start          # runs the app against the live site
npm run dist       # builds dist/Sellybot-Setup.exe (plus latest.yml and the .blockmap)
```

- To point the app at a local copy of the site: `SELLYBOT_URL=http://localhost:5710/dashboard.html npm start`
- Inside VS Code's terminal, `ELECTRON_RUN_AS_NODE=1` is set, and Electron then starts as plain Node. Clear it first: `env -u ELECTRON_RUN_AS_NODE npm start`
  - Installed builds don't have this problem: the `runAsNode` fuse is off.

## Where owners get it

The page is `deploy/site/download.html` (https://sellybot.online/download.html). It has the download button, a guide that plays the 4 install steps (including the SmartScreen warning), and an FAQ.

The dashboard links to it from two places:
- the **حمّل البرنامج** button in the nav bar
- the account menu

Both show only on Windows, and never inside the app. The site tells it's inside the app from the `SellybotDesktop/<version>` token in the user agent.

## How owners hear about updates

**A new feature on the site:** add an entry at the top of `deploy/site/whats-new.js`, then deploy the site.
- Open dashboards (and the app) re-read that file every 10 minutes and when the window comes back to the front. They then show a bar «✨ جديد في Sellybot: …» with a reload button.
- In the app, a Windows notification appears too if the window is in the background.
- After the reload, the window «الجديد في Sellybot» shows what's new.

**A new version of the app:** it downloads in the background.
- `preload.js` tells the dashboard, which shows «نسخة جديدة من البرنامج جاهزة» with **أعد التشغيل**.
- A Windows notification says the same. Otherwise the update installs when the app is closed.
- To try this locally: `SELLYBOT_FAKE_UPDATE=1.2.3 npm start`.

## Release a new version

1. Raise `version` in `package.json`.
2. Push to `main`.
3. In GitHub, open Actions → **Desktop app release** → **Run workflow**.

The workflow builds the installer and creates the release `v<version>`, attaching `Sellybot-Setup.exe`, `latest.yml` and the `.blockmap`.
- The dashboard's download link always serves the newest release.
- Installed apps download the update in the background and install it when they're closed.

The installer is not code-signed. On first run, Windows SmartScreen shows "Windows protected your PC", and the owner clicks **More info → Run anyway**. A code-signing certificate removes that warning. Once you have one, set `CSC_LINK` and `CSC_KEY_PASSWORD` in the workflow.
