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
