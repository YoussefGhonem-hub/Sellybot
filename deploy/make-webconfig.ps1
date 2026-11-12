# Writes site\web.config from web.template.config + the assembly binding redirects of the latest build.
# Secrets stay as PUT-... placeholders here; deploy.ps1 fills them in only on the copy it uploads.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cfg = Join-Path $here "src\RiwaqChat\bin\Release\net48\RiwaqChat.dll.config"
if (-not (Test-Path $cfg)) { throw "Build output not found: $cfg (run dotnet build first)" }
[xml]$red = Get-Content -Raw $cfg
$runtime = $red.configuration.runtime.OuterXml
$tpl = [IO.File]::ReadAllText((Join-Path $here "web.template.config"))
$xml = $tpl.Replace("<!--RUNTIME-->", $runtime)
[xml]$check = $xml   # throws if the result is not valid XML
[IO.File]::WriteAllText((Join-Path $here "site\web.config"), $xml, (New-Object Text.UTF8Encoding $false))
# Stamp a version on widget.js in our own pages so browsers pick up a new build right away (it is cached 10 minutes).
$stamp = (Get-Item (Join-Path $here "site\widget.js")).LastWriteTimeUtc.ToString("yyyyMMddHHmmss")
foreach ($page in "index.html", "dashboard.html") {
  $p = Join-Path $here "site\$page"
  $html = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
  $new = [regex]::Replace($html, 'src="widget\.js(\?v=\d+)?"', "src=`"widget.js?v=$stamp`"")
  if ($new -ne $html) { [IO.File]::WriteAllText($p, $new, (New-Object Text.UTF8Encoding $false)) }
}
$bin = Join-Path $here "site\bin"
New-Item -ItemType Directory -Force $bin | Out-Null
Get-ChildItem $bin -Filter *.dll | Remove-Item -Force
Get-ChildItem (Split-Path $cfg) -Filter *.dll | Copy-Item -Destination $bin -Force
