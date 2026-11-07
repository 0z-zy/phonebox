<#
.SYNOPSIS
  Builds the release artifacts and publishes them as a GitHub release (tag v<version>).
  Running agents pick it up within 6 hours (or via Settings > Updates > Check now) and update
  themselves and the phone app.

.DESCRIPTION
  The version comes from agent/PhoneBox.Agent/PhoneBox.Agent.csproj; keep android/app/build.gradle.kts
  (versionName, and a higher versionCode) in sync before releasing. The APK is signed with your release key,
  which must be the same key as the app installed on the phone, otherwise Android refuses the update.

.EXAMPLE
  .\scripts\release.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'agent\PhoneBox.Agent\PhoneBox.Agent.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$gradle = Get-Content (Join-Path $root 'android\app\build.gradle.kts') -Raw
if ($gradle -notmatch "versionName = ""$([regex]::Escape($version))""") { throw "android versionName does not match $version; update build.gradle.kts first" }
if (git -C $root status --porcelain) { throw 'commit your changes first' }

& (Join-Path $PSScriptRoot 'build.ps1')
git -C $root push
$assets = @(Join-Path $root "dist\PhoneBox-$version.apk") + (Get-ChildItem (Join-Path $root 'dist') -Filter "PhoneBoxAgent-$version-*.zip").FullName
Get-FileHash $assets -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $(Split-Path $_.Path -Leaf)" } | Set-Content (Join-Path $root "dist\SHA256SUMS-$version.txt")
$assets += Join-Path $root "dist\SHA256SUMS-$version.txt"
gh release create "v$version" @assets `
    --title "PhoneBox $version" --generate-notes

# Point the Scoop bucket and the winget manifests at the new release.
$sha = (Get-FileHash (Join-Path $root "dist\PhoneBoxAgent-$version-win-x64.zip") -Algorithm SHA256).Hash.ToLower()
$bucket = Join-Path $root 'bucket\phonebox.json'
$json = Get-Content $bucket -Raw
$json = $json -replace '"version": "[^"]*"', "`"version`": `"$version`"" `
              -replace 'download/v\d[^/]*/PhoneBoxAgent-\d[^"]*-win-x64\.zip', "download/v$version/PhoneBoxAgent-$version-win-x64.zip" `
              -replace '"hash": "[0-9a-f]{64}"', "`"hash`": `"$sha`""
[IO.File]::WriteAllText($bucket, $json)
python (Join-Path $root 'packaging\winget\make-manifests.py') $version $sha
git -C $root add bucket/phonebox.json packaging/winget/manifests
git -C $root commit -m "Scoop and winget manifests for $version"
git -C $root push
Write-Host "winget: submit packaging\winget\manifests\e\ersinkoc\PhoneBox\$version with 'wingetcreate submit <folder>'"
