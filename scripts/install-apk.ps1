function Install-PhoneBoxApk {
    param([Parameter(Mandatory)][string]$Adb, [Parameter(Mandatory)][string]$ApkPath, [Parameter(Mandatory)][string]$Serial)
    $target = @('-s', $Serial)
    $output = & $Adb @target install -r $ApkPath 2>&1
    $code = $LASTEXITCODE
    if ($output -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
        throw 'APK signing key differs from the installed app. The installed app and its data were kept. Build with the original signing key.'
    }
    if ($code -ne 0 -or -not ($output -match '^Success\s*$')) { throw ('APK update failed; the installed app was kept. ' + ($output -join ' ')) }
    $output | Select-Object -Last 1
}
