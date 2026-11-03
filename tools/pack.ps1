$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$proj = Get-Content (Join-Path $root 'src\CommonwealthLink.csproj')
$version = $proj.Project.PropertyGroup.Version
Push-Location (Join-Path $root 'src')
try { dotnet publish -c Release -o publish | Out-Host; if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' } } finally { Pop-Location }
$name = "CommonwealthLink-$version"
$out = Join-Path $root "dist\$name"
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $root 'src\publish\CommonwealthLink.exe') $out -Force
Copy-Item (Join-Path $root 'README.md') $out -Force
Copy-Item (Join-Path $root 'LICENSE') $out -Force
$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Get-Item $zip | Select-Object FullName, Length | Format-List | Out-Host
