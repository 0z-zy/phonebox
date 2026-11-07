# Removes (or restores) the packages listed in debloat-list.txt for the current user (user 0).
# Usage:
#   .\debloat.ps1                     -> remove listed packages
#   .\debloat.ps1 -Restore            -> restore listed packages
#   .\debloat.ps1 -Serial <serial>    -> target a specific device when several are connected
param([switch]$Restore, [string]$Serial)

$adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
if (-not $adb) {
    $adb = @("$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe") +
        (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -Filter 'Google.PlatformTools*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'platform-tools\adb.exe' }) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $adb) { throw "adb not found (winget install Google.PlatformTools)" }

$target = if ($Serial) { @('-s', $Serial) } else { @() }
$pkgs = Get-Content "$PSScriptRoot\debloat-list.txt" |
    ForEach-Object { ($_ -split '#')[0].Trim() } | Where-Object { $_ }

foreach ($p in $pkgs) {
    $out = if ($Restore) { & $adb @target shell cmd package install-existing $p 2>&1 }
           else { & $adb @target shell pm uninstall -k --user 0 $p 2>&1 }
    "{0,-50} {1}" -f $p, ($out -join ' ')
}
