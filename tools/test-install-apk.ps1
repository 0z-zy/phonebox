$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/install-apk.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('phonebox-installer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$fakeAdb = Join-Path $fixture 'adb.ps1'
$env:PHONEBOX_INSTALL_TEST_LOG = Join-Path $fixture 'calls.txt'
@'
Add-Content -LiteralPath $env:PHONEBOX_INSTALL_TEST_LOG -Value ($args -join ' ')
if ($args -contains 'uninstall') { Write-Output 'Success'; exit 0 }
if ($env:PHONEBOX_INSTALL_TEST_MODE -eq 'signature') { Write-Output 'Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]'; exit 1 }
if ($env:PHONEBOX_INSTALL_TEST_MODE -eq 'failure') { Write-Output 'Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]'; exit 1 }
Write-Output 'Success'; exit 0
'@ | Set-Content -LiteralPath $fakeAdb -Encoding utf8
try {
    foreach ($mode in @('signature', 'failure')) {
        $env:PHONEBOX_INSTALL_TEST_MODE = $mode
        $failed = $false
        try { Install-PhoneBoxApk -Adb $fakeAdb -ApkPath 'test.apk' -Serial 'test-phone' | Out-Null } catch { $failed = $true }
        if (-not $failed) { throw "$mode install must report failure" }
        if ((Get-Content -LiteralPath $env:PHONEBOX_INSTALL_TEST_LOG) -match 'uninstall') { throw 'Installer must never remove the existing app automatically' }
    }
    $env:PHONEBOX_INSTALL_TEST_MODE = 'success'
    Install-PhoneBoxApk -Adb $fakeAdb -ApkPath 'test.apk' -Serial 'test-phone' | Out-Null
    Write-Output 'PASS: signature mismatch and other failures preserve the installed app; successful updates work'
} finally {
    [IO.File]::Delete($fakeAdb)
    [IO.File]::Delete($env:PHONEBOX_INSTALL_TEST_LOG)
    [IO.Directory]::Delete($fixture)
    $env:PHONEBOX_INSTALL_TEST_LOG = $null
    $env:PHONEBOX_INSTALL_TEST_MODE = $null
}
