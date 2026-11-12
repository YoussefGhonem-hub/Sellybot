# Manual deploy from this PC (deploy.bat). Same steps as the GitHub "uat" deploy (ci-deploy.ps1),
# with the values taken from deploy.settings.ps1 and small local files that git ignores.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

function Fail($t) { Write-Host ""; Write-Host "X $t" -ForegroundColor Red; exit 1 }
function Plain($sec) { [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)) }
function RandomToken($bytes) { $b = New-Object byte[] $bytes; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($b); ([BitConverter]::ToString($b)).Replace("-", "").ToLower() }
function FromFileOrAsk($file, $prompt) {
  $path = Join-Path $here $file
  if (Test-Path $path) { return (Get-Content -Raw $path).Trim() }
  $v = (Plain (Read-Host $prompt -AsSecureString)).Trim()
  if ((Read-Host "Save it in $file on this PC so you are not asked next time? (Y/N)") -match '^[Yy]') { [IO.File]::WriteAllText($path, $v) }
  return $v
}
function Generated($file, $make) { $path = Join-Path $here $file; if (-not (Test-Path $path)) { [IO.File]::WriteAllText($path, (& $make)) }; (Get-Content -Raw $path).Trim() }

Write-Host "=== Store chat deploy ===" -ForegroundColor Cyan
$settings = Join-Path $here "deploy.settings.ps1"
if (-not (Test-Path $settings)) { Fail "deploy.settings.ps1 is missing. Copy deploy.settings.example.ps1 to deploy.settings.ps1 and fill it in." }
. $settings

$env:WEBDEPLOY_SERVER  = $ServerHost
$env:WEBDEPLOY_SITE    = $SiteName
$env:WEBDEPLOY_USER    = $UserName
$env:DB_SERVER         = $DbServer
$env:DB_NAME           = $DbName
$env:DB_USER           = $DbUser
$env:ANTHROPIC_API_KEY = FromFileOrAsk "anthropic-key.txt" "Paste your Anthropic API key (sk-ant-...)"
$env:DB_PASSWORD       = FromFileOrAsk "db-password.txt" "Database password for $DbUser"
$env:AUTH_SECRET       = Generated "auth-secret.txt" { RandomToken 32 }
$env:ADMIN_SIGNUP_CODE = Generated "admin-code.txt" { "admin-" + (RandomToken 6) }
$env:WEBDEPLOY_PASSWORD = Plain (Read-Host "SmarterASP Web Deploy password for $UserName" -AsSecureString)

& (Join-Path $here "ci-deploy.ps1")
$code = $LASTEXITCODE
$env:WEBDEPLOY_PASSWORD = $null; $env:DB_PASSWORD = $null; $env:ANTHROPIC_API_KEY = $null
if ($code -ne 0) { exit $code }

Write-Host ""
Write-Host "Dashboard for store owners:   <your site>/dashboard.html" -ForegroundColor Yellow
Write-Host "Chat for a store:             <your site>/?store=<store id>" -ForegroundColor Yellow
Write-Host "Your ADMIN CODE (keep it private): $env:ADMIN_SIGNUP_CODE" -ForegroundColor Cyan
