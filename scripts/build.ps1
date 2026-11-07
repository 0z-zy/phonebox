<#
.SYNOPSIS
  Builds release artifacts into dist\:
    PhoneBox-<version>.apk               Android panel (release-signed if a key is configured)
    PhoneBoxAgent-<version>-win-x64.zip  Windows agent (needs the .NET 10 runtime)
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$version = ([xml](Get-Content (Join-Path $root 'agent\PhoneBox.Agent\PhoneBox.Agent.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

Write-Host "==> Android APK ($version)"
Push-Location (Join-Path $root 'android')
try { .\gradlew.bat assembleRelease --console=plain -q } finally { Pop-Location }
Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') (Join-Path $dist "PhoneBox-$version.apk") -Force

Write-Host "==> Windows agent ($version)"
$out = Join-Path $env:TEMP "phonebox-agent-$version"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'agent\PhoneBox.Agent') -c Release -r win-x64 --self-contained false -o $out --nologo -v q --disable-build-servers
$zip = Join-Path $dist "PhoneBoxAgent-$version-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out\*" -DestinationPath $zip

Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
