# Minimal ACME v2 (Let's Encrypt) client, HTTP-01 challenge, using only .NET built-in crypto (PowerShell 7).
#   -Stage order     : account (agrees to Let's Encrypt terms) + order + writes proof files into the site folder
#   -Stage validate  : checks the proof files are reachable, asks Let's Encrypt to verify, waits for the result
#   -Stage finalize  : new certificate key + CSR, downloads the certificate, writes PFX (+ PEM) and its password
param(
  [Parameter(Mandatory)][ValidateSet('order', 'validate', 'finalize')][string]$Stage,
  [switch]$Staging,
  [string]$StateDir = 'D:\hermes-files\deploy\ssl',
  [string[]]$Domains = @('sellybot.online', 'www.sellybot.online'),
  [string]$ChallengeDir = 'D:\hermes-files\deploy\site\.well-known\acme-challenge'
)
$ErrorActionPreference = 'Stop'
$prefix = if ($Staging) { 'staging-' } else { '' }
$dirUrl = if ($Staging) { 'https://acme-staging-v02.api.letsencrypt.org/directory' } else { 'https://acme-v02.api.letsencrypt.org/directory' }
$keyFile = Join-Path $StateDir ($prefix + 'account-key.xml')
$stateFile = Join-Path $StateDir ($prefix + 'order.json')
New-Item -ItemType Directory -Force $StateDir | Out-Null

function B64U([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
function B64UStr([string]$s) { B64U ([Text.Encoding]::UTF8.GetBytes($s)) }

# ---- account key (kept on this PC only) ----
$rsa = [Security.Cryptography.RSA]::Create(2048)
if (Test-Path $keyFile) { $rsa.FromXmlString([IO.File]::ReadAllText($keyFile)) } else { [IO.File]::WriteAllText($keyFile, $rsa.ToXmlString($true)) }
$pub = $rsa.ExportParameters($false)
$e = B64U $pub.Exponent; $n = B64U $pub.Modulus
$jwk = [ordered]@{ e = $e; kty = 'RSA'; n = $n }
$thumb = B64U ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('{"e":"' + $e + '","kty":"RSA","n":"' + $n + '"}')))

$dir = Invoke-RestMethod $dirUrl
$script:nonce = $null; $script:kid = $null
function NewNonce { $r = Invoke-WebRequest $dir.newNonce -Method Head -UseBasicParsing; $script:nonce = @($r.Headers['Replay-Nonce'])[0] }
function Post([string]$url, $payload, [switch]$UseJwk) {
  for ($try = 0; $try -lt 4; $try++) {
    if (-not $script:nonce) { NewNonce }
    $prot = [ordered]@{ alg = 'RS256'; nonce = $script:nonce; url = $url }
    if ($UseJwk) { $prot.jwk = $jwk } else { $prot.kid = $script:kid }
    $protB = B64UStr ($prot | ConvertTo-Json -Compress)
    $payB = if ($null -eq $payload) { '' } else { B64UStr ($payload | ConvertTo-Json -Compress -Depth 10) }
    $sig = B64U ($rsa.SignData([Text.Encoding]::ASCII.GetBytes("$protB.$payB"), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
    $body = [ordered]@{ protected = $protB; payload = $payB; signature = $sig } | ConvertTo-Json -Compress
    $r = Invoke-WebRequest $url -Method Post -Body $body -ContentType 'application/jose+json' -SkipHttpErrorCheck -UseBasicParsing
    $script:nonce = @($r.Headers['Replay-Nonce'])[0]
    if ([int]$r.StatusCode -ge 400) {
      $err = $null; try { $err = $r.Content | ConvertFrom-Json } catch {}
      if ($err -and "$($err.type)" -like '*badNonce') { $script:nonce = $null; continue }
      throw "ACME error $([int]$r.StatusCode): $($err.type) $($err.detail)"
    }
    return $r
  }
  throw 'ACME: too many bad nonces'
}
function Json($r) { $c = $r.Content; if ($c -is [byte[]]) { $c = [Text.Encoding]::UTF8.GetString($c) }; $c | ConvertFrom-Json }
function Account { $r = Post $dir.newAccount @{ termsOfServiceAgreed = $true } -UseJwk; $script:kid = @($r.Headers['Location'])[0] }

switch ($Stage) {
  'order' {
    Account
    $r = Post $dir.newOrder @{ identifiers = @($Domains | ForEach-Object { @{ type = 'dns'; value = $_ } }) }
    $orderUrl = @($r.Headers['Location'])[0]; $o = Json $r
    New-Item -ItemType Directory -Force $ChallengeDir | Out-Null
    $items = @()
    foreach ($u in $o.authorizations) {
      $a = Json (Post $u $null)
      $ch = $a.challenges | Where-Object { $_.type -eq 'http-01' } | Select-Object -First 1
      [IO.File]::WriteAllText((Join-Path $ChallengeDir $ch.token), "$($ch.token).$thumb", (New-Object Text.UTF8Encoding $false))
      $items += [ordered]@{ domain = $a.identifier.value; authz = $u; challenge = $ch.url; token = $ch.token; status = $a.status }
    }
    [IO.File]::WriteAllText($stateFile, ([ordered]@{ kid = $script:kid; order = $orderUrl; finalize = $o.finalize; items = $items } | ConvertTo-Json -Depth 5))
    "order created ($($o.status)), expires $($o.expires)"
    $items | ForEach-Object { "  $($_.domain): status=$($_.status) proof file=$($_.token)" }
  }
  'validate' {
    $st = Get-Content $stateFile -Raw | ConvertFrom-Json; $script:kid = $st.kid
    foreach ($it in $st.items) {
      $url = "http://$($it.domain)/.well-known/acme-challenge/$($it.token)"
      $got = try { (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 20).Content } catch { "ERROR: $($_.Exception.Message)" }
      if ($got -is [byte[]]) { $got = [Text.Encoding]::UTF8.GetString($got) }
      if ("$got".Trim() -ne "$($it.token).$thumb") { throw "proof file not reachable on $($it.domain): $url -> $got" }
      "  self-check OK: $($it.domain)"
    }
    foreach ($it in $st.items) {
      $a = Json (Post $it.authz $null)
      if ($a.status -eq 'valid') { "  $($it.domain): already valid"; continue }
      [void](Post $it.challenge @{})
      for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $a = Json (Post $it.authz $null)
        if ($a.status -ne 'pending') { break }
      }
      $ch = $a.challenges | Where-Object { $_.type -eq 'http-01' }
      "  $($it.domain): $($a.status)" + $(if ($ch.error) { " - $($ch.error.type): $($ch.error.detail)" } else { '' })
      if ($a.status -ne 'valid') { throw "validation failed for $($it.domain)" }
    }
  }
  'finalize' {
    $st = Get-Content $stateFile -Raw | ConvertFrom-Json; $script:kid = $st.kid
    $certKey = [Security.Cryptography.RSA]::Create(2048)
    $req = [Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$($Domains[0])", $certKey, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new(); foreach ($d in $Domains) { $san.AddDnsName($d) }
    $req.CertificateExtensions.Add($san.Build())
    [void](Post $st.finalize @{ csr = (B64U $req.CreateSigningRequest()) })
    for ($i = 0; $i -lt 30; $i++) { $o = Json (Post $st.order $null); if ($o.status -eq 'valid' -or $o.status -eq 'invalid') { break }; Start-Sleep -Seconds 2 }
    if ($o.status -ne 'valid') { throw "order not valid: $($o.status)" }
    $pemR = Post $o.certificate $null; $pem = $pemR.Content; if ($pem -is [byte[]]) { $pem = [Text.Encoding]::UTF8.GetString($pem) }
    $blocks = [regex]::Matches($pem, '-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----') | ForEach-Object Value
    $col = [Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    $leaf = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($blocks[0])
    [void]$col.Add([Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($leaf, $certKey))
    foreach ($b in $blocks | Select-Object -Skip 1) { [void]$col.Add([Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($b)) }
    $pw = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
    # TripleDES/SHA1 PKCS#12 so older Windows/IIS servers can import it
    $pfx = $col.ExportPkcs12([Security.Cryptography.X509Certificates.Pkcs12ExportPbeParameters]::Pkcs12TripleDesSha1, $pw)
    $base = Join-Path $StateDir ($prefix + $Domains[0])
    [IO.File]::WriteAllBytes("$base.pfx", $pfx)
    [IO.File]::WriteAllText("$base-pfx-password.txt", $pw)
    [IO.File]::WriteAllText("$base-fullchain.pem", $pem)
    [IO.File]::WriteAllText("$base-key.pem", $certKey.ExportRSAPrivateKeyPem())
    "certificate issued: $($leaf.Subject) | names: $(($Domains) -join ', ') | valid $($leaf.NotBefore.ToString('yyyy-MM-dd')) -> $($leaf.NotAfter.ToString('yyyy-MM-dd')) | issuer: $($leaf.Issuer)"
    "files: $base.pfx (+ password, fullchain, key)"
  }
}
