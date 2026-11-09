<#
.SYNOPSIS
  Builds release artifacts into dist\:
    PhoneBox-<version>.apk               Android panel (release-signed if a key is configured)
    PhoneBoxAgent-<version>-<rid>.zip    agent for win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64 (needs the .NET 10 runtime)
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$version = ([xml](Get-Content (Join-Path $root 'agent\PhoneBox.Agent\PhoneBox.Agent.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

Write-Host "==> Android APK ($version)"
Push-Location (Join-Path $root 'android')
try { .\gradlew.bat assembleRelease --console=plain -q } finally { Pop-Location }
if ($LASTEXITCODE) { throw 'Android release build failed' }
Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') (Join-Path $dist "PhoneBox-$version.apk") -Force

# Framework-dependent builds for every supported system. Zip files lose the Unix executable bit; install.sh and
# the agent's updater set it again.
$targets = [ordered]@{
    'win-x64' = 'net10.0-windows10.0.19041.0'
    'linux-x64' = 'net10.0'; 'linux-arm64' = 'net10.0'
    'osx-arm64' = 'net10.0'; 'osx-x64' = 'net10.0'
}
foreach ($rid in $targets.Keys) {
    Write-Host "==> Agent $rid ($version)"
    $out = Join-Path ([IO.Path]::GetTempPath()) ("phonebox-agent-$version-$rid-" + [guid]::NewGuid().ToString('N'))
    dotnet publish (Join-Path $root 'agent\PhoneBox.Agent') -c Release -f $targets[$rid] -r $rid --self-contained false -o $out --nologo -v q --disable-build-servers
    if ($LASTEXITCODE) { throw "publish failed for $rid" }
    $zip = Join-Path $dist "PhoneBoxAgent-$version-$rid.zip"
    Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
}

Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
