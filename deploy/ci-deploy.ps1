# Builds the site and publishes it to SmarterASP with Web Deploy.
# Used by GitHub Actions (push to the "uat" branch) and by deploy.ps1 on your PC.
# Everything comes from environment variables, so no secret is ever stored in the repository:
#   WEBDEPLOY_SERVER   e.g. winXXXX.site4now.net
#   WEBDEPLOY_SITE     e.g. abc123-001-site1
#   WEBDEPLOY_USER     e.g. abc123-001
#   WEBDEPLOY_PASSWORD
#   DB_SERVER, DB_NAME, DB_USER, DB_PASSWORD
#   ANTHROPIC_API_KEY, AUTH_SECRET, ADMIN_SIGNUP_CODE
# Options: -DryRun builds and prepares everything but does not upload.
param([switch]$DryRun, [switch]$SkipBuild)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$inCI = $env:GITHUB_ACTIONS -eq "true"

function Say($t, $c = "Gray") { Write-Host $t -ForegroundColor $c }
function Fail($t) { if ($inCI) { Write-Host "::error::$t" } else { Write-Host "X $t" -ForegroundColor Red }; exit 1 }
function Xml($s) { [Security.SecurityElement]::Escape($s) }
function CsValue($v) { if ($v -match '[;"''=]' -or $v -ne $v.Trim()) { return '"' + $v.Replace('"', '""') + '"' } return $v }

# ---- 1) settings ----
$names = "WEBDEPLOY_SERVER", "WEBDEPLOY_SITE", "WEBDEPLOY_USER", "WEBDEPLOY_PASSWORD", "DB_SERVER", "DB_NAME", "DB_USER", "DB_PASSWORD", "ANTHROPIC_API_KEY", "AUTH_SECRET", "ADMIN_SIGNUP_CODE"
$cfg = @{}
$missing = @()
foreach ($n in $names) {
  $v = [Environment]::GetEnvironmentVariable($n)
  if ($v) { $v = ($v -replace '[\x00-\x1F\x7F]', '').Trim() }   # e.g. a Ctrl+V typed into a hidden prompt (0x16) crashes Web Deploy
  if ([string]::IsNullOrWhiteSpace($v)) { $missing += $n } else { $cfg[$n] = $v }
}
if ($DryRun) { foreach ($n in $missing) { $cfg[$n] = ("dry-run-" + $n.ToLower()).PadRight(40, "x") }; $missing = @() }
if ($missing.Count -gt 0) {
  if ($inCI) { Write-Host "::warning::Deploy skipped: these GitHub secrets are not set yet: $($missing -join ', '). Add them in the repository Settings > Secrets and variables > Actions (or run deploy/set-github-secrets.ps1)."; exit 0 }
  Fail "Missing settings: $($missing -join ', ')"
}
if (-not $DryRun -and $cfg.ANTHROPIC_API_KEY -notmatch '^sk-ant-') { Fail "ANTHROPIC_API_KEY does not look like an Anthropic key (it should start with sk-ant-)." }
if ($cfg.AUTH_SECRET.Length -lt 32) { Fail "AUTH_SECRET must be at least 32 characters." }
if ($cfg.WEBDEPLOY_PASSWORD -match '[,"'' ]') { Fail "The Web Deploy password has a comma, quote or space, which Web Deploy can't take. Change it in SmarterASP." }
Say "OK  settings loaded" Green

# ---- 2) build ----
if (-not $SkipBuild) {
  & dotnet build (Join-Path $here "src\RiwaqChat\RiwaqChat.csproj") -c Release --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { Fail "Building the backend failed." }
}
& (Join-Path $here "make-webconfig.ps1")
Say "OK  backend and config built" Green

# ---- 3) upload folder (_out) with the secrets filled in ----
$out = Join-Path $here "_out"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Copy-Item -Recurse (Join-Path $here "site") $out
$products = Join-Path $here "..\data\products.txt"
if (Test-Path $products) { Copy-Item $products (Join-Path $out "App_Data\products.txt") -Force }
$conn = "Data Source=" + (CsValue $cfg.DB_SERVER) + ";Initial Catalog=" + (CsValue $cfg.DB_NAME) + ";User ID=" + (CsValue $cfg.DB_USER) +
        ";Password=" + (CsValue $cfg.DB_PASSWORD) + ";Encrypt=True;TrustServerCertificate=True"
$wc = Join-Path $out "web.config"
$xml = [IO.File]::ReadAllText($wc)
$xml = $xml.Replace("PUT-ANTHROPIC-KEY-HERE", (Xml $cfg.ANTHROPIC_API_KEY)).Replace("PUT-DB-CONNECTION-HERE", (Xml $conn)).Replace("PUT-AUTH-SECRET-HERE", (Xml $cfg.AUTH_SECRET)).Replace("PUT-ADMIN-CODE-HERE", (Xml $cfg.ADMIN_SIGNUP_CODE))
if ($xml -match 'PUT-') { Remove-Item -Recurse -Force $out; Fail "web.config still has an empty setting." }
[IO.File]::WriteAllText($wc, $xml, (New-Object Text.UTF8Encoding $false))
Say "OK  site prepared ($((Get-ChildItem -Recurse -File $out).Count) files)" Green

if ($DryRun) { Say "DRY RUN: not uploading. Prepared files are in $out" Yellow; exit 0 }

# ---- 4) publish ----
$msdeploy = "C:\Program Files\IIS\Microsoft Web Deploy V3\msdeploy.exe"
if (-not (Test-Path $msdeploy)) { Remove-Item -Recurse -Force $out; Fail "Web Deploy (msdeploy.exe) is not installed." }
$serviceUrl = "https://$($cfg.WEBDEPLOY_SERVER):8172/MsDeploy.axd?site=$($cfg.WEBDEPLOY_SITE)"
Say "..  uploading to SmarterASP ($($cfg.WEBDEPLOY_SITE))"
$msArgs = @(
  "-verb:sync",
  "-source:contentPath=$out",
  "-dest:contentPath=$($cfg.WEBDEPLOY_SITE),computerName=$serviceUrl,userName=$($cfg.WEBDEPLOY_USER),password=$($cfg.WEBDEPLOY_PASSWORD),authType=Basic",
  "-allowUntrusted",
  "-enableRule:DoNotDeleteRule",
  "-retryAttempts:3"
)
& $msdeploy $msArgs
$code = $LASTEXITCODE
Remove-Item -Recurse -Force $out
if ($code -ne 0) { Fail "Upload failed (msdeploy exit $code)." }
Say "DONE. The site is live on $($cfg.WEBDEPLOY_SITE)." Green
