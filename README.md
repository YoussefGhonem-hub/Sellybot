# Sellybot

An AI shopping assistant for online stores. Each store owner signs up, adds their store data (text, PDF, Word, Excel), picks a chat bubble style and color, and pastes one line of code into their Shopify theme. Their customers then get instant answers from the store's own data, in Arabic, powered by Claude.

## What's inside

| Path | What it is |
|---|---|
| `deploy/site/widget.js` | The chat itself (Shadow DOM). Bubble mode for store websites, page mode for the chat link, 10 bubble styles. |
| `deploy/site/index.html` | The chat link page: `/?store=<store id>` opens the chat directly. |
| `deploy/site/dashboard.html` | Store owner dashboard: data, chat look, install code, assistant settings, admin. |
| `deploy/src/RiwaqChat/` | Backend (ASP.NET, .NET Framework 4.8, official Anthropic C# SDK): chat API with streaming, search mode for big catalogs, accounts, file text extraction, usage limits. |
| `deploy/web.template.config` | IIS / ASP.NET config template. Secrets are placeholders filled in at deploy time. |
| `deploy/ci-deploy.ps1` | Build + publish to SmarterASP with Web Deploy (used by GitHub Actions and by `deploy.ps1`). |
| `.github/workflows/deploy-uat.yml` | Auto deploy on every push to the `uat` branch. |

## Run it on your PC

Needs Windows, the .NET SDK, IIS Express and SQL Server LocalDB.

1. Copy `deploy/deploy.settings.example.ps1` to `deploy/deploy.settings.ps1` and fill it in.
2. Double-click `deploy/run-local.bat`.
3. Open `http://localhost:5700/dashboard.html` (chat: `http://localhost:5700/?store=riwaq`).

## Deploy

- **Automatic (UAT):** push to the `uat` branch. GitHub Actions builds and publishes to SmarterASP.
  One-time setup: run `deploy/set-github-secrets.bat` (or add the secrets below by hand in *Settings → Secrets and variables → Actions*).
- **Manual:** double-click `deploy/deploy.bat`.

GitHub secrets used by the workflow:

| Secret | Example / meaning |
|---|---|
| `WEBDEPLOY_SERVER` | `winXXXX.site4now.net` |
| `WEBDEPLOY_SITE` | SmarterASP site name |
| `WEBDEPLOY_USER` | Web Deploy user |
| `WEBDEPLOY_PASSWORD` | Web Deploy password |
| `DB_SERVER`, `DB_NAME`, `DB_USER`, `DB_PASSWORD` | SQL Server database |
| `ANTHROPIC_API_KEY` | Claude API key |
| `AUTH_SECRET` | Random 32+ characters, signs dashboard logins |
| `ADMIN_SIGNUP_CODE` | Code that makes a new sign-up an admin |

No secret is stored in this repository: `.gitignore` keeps out the settings file, keys, passwords and store data.
