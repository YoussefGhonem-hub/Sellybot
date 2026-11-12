# One-time setup: saves the deploy settings as GitHub Actions secrets for the repository,
# so a push to the "uat" branch can deploy by itself. You run it; you type the passwords.
# Needs the GitHub CLI logged in (gh auth login). GitHub encrypts each value; nobody can read it back, only Actions use it.
param([string]$Repo = "YoussefGhonem-hub/Sellybot")
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# Ctrl+V in a hidden prompt types an invisible character (0x16) instead of pasting, so those are removed
# and an empty answer is asked again. Paste with a RIGHT-CLICK in the window.
function CleanText($s) { if ($null -eq $s) { return "" }; ($s -replace '[\x00-\x1F\x7F]', '').Trim() }
function Plain($sec) { CleanText ([Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))) }
function AskSecret($prompt) {
  while ($true) {
    $v = Plain (Read-Host $prompt -AsSecureString)
    if ($v) { return $v }
    Write-Host "   Nothing was entered. To paste here, RIGHT-CLICK in this window (Ctrl+V does not paste in this box), then press Enter." -ForegroundColor Yellow
  }
}
function RandomToken($bytes) { $b = New-Object byte[] $bytes; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($b); ([BitConverter]::ToString($b)).Replace("-", "").ToLower() }
function FileOrAsk($file, $prompt) { $p = Join-Path $here $file; if (Test-Path $p) { return CleanText (Get-Content -Raw $p) }; return AskSecret $prompt }
function Generated($file, $make) { $p = Join-Path $here $file; if (-not (Test-Path $p)) { [IO.File]::WriteAllText($p, (& $make)) }; (Get-Content -Raw $p).Trim() }
function Put($name, $value) {
  if ([string]::IsNullOrWhiteSpace($value)) { Write-Host "skip $name (empty)" -ForegroundColor Yellow; return }
  gh secret set $name --repo $Repo --body $value | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Could not set $name" }
  Write-Host "OK  $name" -ForegroundColor Green
}

. (Join-Path $here "deploy.settings.ps1")
Write-Host "Saving deploy secrets to $Repo ..." -ForegroundColor Cyan
Put "WEBDEPLOY_SERVER" $ServerHost
Put "WEBDEPLOY_SITE" $SiteName
Put "WEBDEPLOY_USER" $UserName
Put "DB_SERVER" $DbServer
Put "DB_NAME" $DbName
Put "DB_USER" $DbUser
Put "AUTH_SECRET" (Generated "auth-secret.txt" { RandomToken 32 })
Put "ADMIN_SIGNUP_CODE" (Generated "admin-code.txt" { "admin-" + (RandomToken 6) })
Put "ANTHROPIC_API_KEY" (FileOrAsk "anthropic-key.txt" "Anthropic API key (sk-ant-...)")
Put "DB_PASSWORD" (FileOrAsk "db-password.txt" "Database password for $DbUser")
Write-Host "Tip: to paste the password, RIGHT-CLICK in this window (Ctrl+V does not paste here). Nothing shows while you type or paste." -ForegroundColor DarkGray
Put "WEBDEPLOY_PASSWORD" (AskSecret "SmarterASP Web Deploy password for $UserName")
Write-Host ""
Write-Host "Done. Every push to the 'uat' branch now deploys automatically." -ForegroundColor Green
Write-Host "To deploy right now without a new commit: GitHub > Actions > Deploy UAT > Run workflow." -ForegroundColor Gray
