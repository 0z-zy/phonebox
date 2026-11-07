# UMIDIGI BISON tune-up (no root)

Optional clean-up used on the phone that PhoneBox was built for (UMIDIGI BISON, Android 11, MediaTek Helio P60).
Everything here is reversible and works without unlocking the bootloader.

## Remove bloat
```powershell
.\debloat.ps1            # remove the packages in debloat-list.txt for user 0
.\debloat.ps1 -Restore   # bring them all back
```
`pm uninstall -k --user 0` only hides a package for the current user; the APK stays on the system partition.
A factory reset also brings everything back.

## Settings that helped
```sh
adb shell settings put global window_animation_scale 0.5
adb shell settings put global transition_animation_scale 0.5
adb shell settings put global animator_duration_scale 0.5
adb shell settings put global wifi_scan_always_enabled 0      # no background Wi-Fi scanning
adb shell settings put global ble_scan_always_enabled 0       # no background Bluetooth scanning
adb shell settings put global stay_on_while_plugged_in 0      # the panel app keeps the screen on by itself
adb shell settings put global private_dns_mode hostname       # system-wide ad/tracker blocking
adb shell settings put global private_dns_specifier dns.adguard-dns.com
adb shell cmd package bg-dexopt-job                           # precompile apps (takes ~5 min)
adb shell sm idle-maint run                                   # fstrim + idle maintenance
```
Defaults to revert to: animation scales `1.0`, both scan settings `1`, private DNS `off`.

## Notes
- The last firmware is from April 2021 (security patch 2021-03-05); keep Chrome, WebView and Play services updated.
- The bootloader can be unlocked (OEM unlock allowed, Treble supported) if you ever want a GSI and root —
  this wipes the phone.
- A panel that is always plugged in keeps the battery at 100 %; without root there is no charge limit,
  so consider a smart plug or USB timer to cycle charging.
