# Runs the whole multi-store site on this PC (nothing is uploaded):
#   chat:       http://localhost:5700/?store=riwaq
#   dashboard:  http://localhost:5700/dashboard.html
# Uses a local SQL Server database (LocalDB "chatbot_local") that keeps its data between runs.
# Close the window (or press Q in it) to stop the site.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
$Port = 5700
$local = Join-Path $here "_local"
# Local-only default store: created on the first run with Riwaq's data, as an admin account.
# Its login comes from deploy.settings.ps1 (ignored by git), so no password lives in the repository.
$SeedSlug = "riwaq"; $SeedName = "Riwaq"; $SeedEmail = "admin@riwaq.local"; $SeedPassword = $null
$settingsFile = Join-Path $here "deploy.settings.ps1"
if (Test-Path $settingsFile) { . $settingsFile }
$DbName = "chatbot_local"   # always the local database here, whatever the settings file says
if (-not $SeedPassword -or $SeedPassword -eq "choose-a-local-password") {
  $SeedPassword = Read-Host "Choose a password for the local admin store '$SeedSlug'"
}

function Say($t, $c = "Gray") { Write-Host $t -ForegroundColor $c }
function Fail($t) { Write-Host ""; Write-Host "X $t" -ForegroundColor Red; exit 1 }
function RandomToken($bytes) { $b = New-Object byte[] $bytes; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($b); ([BitConverter]::ToString($b)).Replace("-", "").ToLower() }
function Xml($s) { [Security.SecurityElement]::Escape($s) }

Say "=== Store chat - local run ===" Cyan

# 1) Anthropic key: anthropic-key.txt, else the one Hermes uses, else ask
$keyFile = Join-Path $here "anthropic-key.txt"
$apiKey = $null
if (Test-Path $keyFile) { $apiKey = (Get-Content -Raw $keyFile).Trim() }
if (-not $apiKey) {
  $hermesEnv = Join-Path $env:LOCALAPPDATA "hermes\.env"
  if (Test-Path $hermesEnv) {
    $line = Select-String -LiteralPath $hermesEnv -Pattern '^\s*ANTHROPIC_API_KEY\s*=' | Select-Object -First 1
    if ($line) { $apiKey = ($line.Line -split '=', 2)[1].Trim().Trim('"').Trim("'") }
  }
}
if (-not $apiKey) {
  $sec = Read-Host "Paste your Anthropic API key (sk-ant-...)" -AsSecureString
  $apiKey = ([Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))).Trim()
}
if ($apiKey -notmatch '^sk-ant-') { Fail "No valid Anthropic API key found." }

# 2) Local secrets (separate from the live site's)
$secretFile = Join-Path $here "auth-secret.local.txt"
if (-not (Test-Path $secretFile)) { [IO.File]::WriteAllText($secretFile, (RandomToken 32)) }
$authSecret = (Get-Content -Raw $secretFile).Trim()
$codeFile = Join-Path $here "admin-code.txt"
if (-not (Test-Path $codeFile)) { [IO.File]::WriteAllText($codeFile, "admin-" + (RandomToken 6)) }
$adminCode = (Get-Content -Raw $codeFile).Trim()
Say "OK  keys ready" Green

# 3) Local database
& sqllocaldb start MSSQLLocalDB | Out-Null
$master = New-Object System.Data.SqlClient.SqlConnection "Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=True"
$master.Open()
$cmd = $master.CreateCommand(); $cmd.CommandText = "IF DB_ID('$DbName') IS NULL CREATE DATABASE [$DbName]"; [void]$cmd.ExecuteNonQuery()
$master.Close()
Say "OK  local database '$DbName' ready" Green

# 4) Build and prepare the local site folder
& dotnet build (Join-Path $here "src\RiwaqChat\RiwaqChat.csproj") -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Building the backend failed. Run: dotnet build src\RiwaqChat -c Release" }
& (Join-Path $here "make-webconfig.ps1")
# stop an earlier local run of this site
Get-CimInstance Win32_Process -Filter "Name='iisexpress.exe'" | Where-Object { $_.CommandLine -like "*$local*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1
if (Test-Path $local) { Get-ChildItem $local -Exclude App_Data | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $local | Out-Null
Get-ChildItem (Join-Path $here "site") | Copy-Item -Destination $local -Recurse -Force
Copy-Item (Join-Path $here "..\data\products.txt") (Join-Path $local "App_Data\products.txt") -Force
$wc = Join-Path $local "web.config"
$xml = [IO.File]::ReadAllText($wc)
$xml = $xml.Replace("PUT-ANTHROPIC-KEY-HERE", (Xml $apiKey)).Replace("PUT-DB-CONNECTION-HERE", (Xml "Server=(localdb)\MSSQLLocalDB;Database=$DbName;Integrated Security=True")).Replace("PUT-AUTH-SECRET-HERE", (Xml $authSecret)).Replace("PUT-ADMIN-CODE-HERE", (Xml $adminCode))
[IO.File]::WriteAllText($wc, $xml, (New-Object Text.UTF8Encoding $false))
Say "OK  site ready in _local" Green

# 5) Start the site, let it create its tables, then make sure the default Riwaq account exists
$iis = Start-Process "C:\Program Files\IIS Express\iisexpress.exe" -ArgumentList "/path:`"$local`" /port:$Port /clr:v4.0 /systray:false" -NoNewWindow -PassThru
$up = $false
for ($i = 0; $i -lt 30 -and -not $up; $i++) {
  Start-Sleep -Seconds 1
  try { Invoke-WebRequest "http://localhost:$Port/api/auth.ashx?action=me" -UseBasicParsing -TimeoutSec 10 | Out-Null; $up = $true }
  catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { $up = $true } }
}
if (-not $up) { Fail "The local site did not start." }

function HashPassword($pw) {
  $salt = New-Object byte[] 16; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($salt)
  $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($pw, $salt, 100000, [Security.Cryptography.HashAlgorithmName]::SHA256)
  "pbkdf2`$100000`$" + [Convert]::ToBase64String($salt) + "`$" + [Convert]::ToBase64String($kdf.GetBytes(32))
}
$db = New-Object System.Data.SqlClient.SqlConnection "Server=(localdb)\MSSQLLocalDB;Database=$DbName;Integrated Security=True"
$db.Open()
$q = $db.CreateCommand(); $q.CommandText = "SELECT COUNT(*) FROM dbo.Stores WHERE Slug=@s"; [void]$q.Parameters.AddWithValue("@s", $SeedSlug)
if ([int]$q.ExecuteScalar() -eq 0) {
  $ins = $db.CreateCommand()
  $ins.CommandText = "INSERT INTO dbo.Stores (Slug, Name, OwnerEmail, PasswordHash, IsAdmin, MonthlyTokenLimit, Welcome) OUTPUT INSERTED.Id VALUES (@slug, @name, @email, @hash, 1, 20000000, @welcome)"
  [void]$ins.Parameters.AddWithValue("@slug", $SeedSlug)
  [void]$ins.Parameters.AddWithValue("@name", $SeedName)
  [void]$ins.Parameters.AddWithValue("@email", $SeedEmail)
  [void]$ins.Parameters.AddWithValue("@hash", (HashPassword $SeedPassword))
  [void]$ins.Parameters.AddWithValue("@welcome", "أهلاً بيك في **رِواق**! 🧽`nأنا **دبدوب** 🐻، اسألني عن أي بقعة أو منتج وأنا أساعدك تختار الأنسب 🫧")
  $storeId = [int]$ins.ExecuteScalar()
  $data = [IO.File]::ReadAllText((Join-Path $here "..\data\products.txt"), [Text.Encoding]::UTF8).Trim()
  $kn = $db.CreateCommand()
  $kn.CommandText = "INSERT INTO dbo.KnowledgeItems (StoreId, Title, SourceType, FileName, Content) VALUES (@s, @t, 'txt', 'products.txt', @c)"
  [void]$kn.Parameters.AddWithValue("@s", $storeId)
  [void]$kn.Parameters.AddWithValue("@t", "بيانات رِواق")
  [void]$kn.Parameters.AddWithValue("@c", $data)
  [void]$kn.ExecuteNonQuery()
  Say "OK  default store '$SeedSlug' created with Riwaq's data" Green
} else { Say "OK  default store '$SeedSlug' already exists" Green }
$db.Close()

Say ""
Say "Dashboard:  http://localhost:$Port/dashboard.html" Yellow
Say "   login:   $SeedSlug  (or $SeedEmail)   password: $SeedPassword" Yellow
Say "Chat:       http://localhost:$Port/?store=$SeedSlug" Yellow
Say "Keep this window open. Close it to stop the site." Gray
Say ""
Start-Process "http://localhost:$Port/dashboard.html"
# Keep this window alive while the local site runs (closing the window stops it).
$site = Get-CimInstance Win32_Process -Filter "Name='iisexpress.exe'" | Where-Object { $_.CommandLine -like "*$local*" } | Select-Object -First 1
if ($site) { Wait-Process -Id $site.ProcessId -ErrorAction SilentlyContinue } else { Wait-Process -Id $iis.Id -ErrorAction SilentlyContinue }
