# Changelog

## Unreleased

**Claude Code permission prompts on the phone.** When Claude Code asks for permission, the phone shows the request
with Allow and Deny (through a `PermissionRequest` hook that the settings page installs). If the phone is not
connected or nobody answers in time, Claude Code asks in the terminal as usual. Optionally only when you are away.

**Live connection.** The phone keeps one server-sent-events stream open (`/api/stream`) instead of polling: page
switches, notifications and permission prompts arrive at once. Polling remains as the fallback.

**Linux and macOS.** The agent runs on Linux (x64, arm64) and macOS (Apple silicon, Intel): system readings, now
playing (MPRIS / Spotify and Music), volume, processes and app icons, shortcuts, command widgets (bash / zsh),
Claude token from the macOS Keychain. `scripts/install.sh` sets it up as a systemd user service or a launchd agent.

**Rules.** Settings › Rules: when an AI session needs you, music plays, an app runs, the CPU is busy, you are away,
at certain times or when a pushed value is on, show a page, a notification or push a value, optionally only while
it lasts.

**Live preview in the layout editor.** Switch the canvas to "Live preview" to run the real panel (served by the
agent at `/panel/`) with your unsaved changes and live data; it follows the page you select.

**Calendars.** Add iCalendar feeds (Google's secret iCal address, Outlook's published calendar, iCloud, any ICS
or `webcal://` link) in Settings › General; the new Agenda widget lists the coming days or, when small, the current or
next event. Recurring events, time zones and moved or cancelled occurrences are handled. A rule can react to "a
calendar event is on" (e.g. show an on-air sign during meetings). The feed addresses never leave the computer.

**Unit tests** for the agent (`agent/PhoneBox.Agent.Tests`, run in CI): calendar parsing and rule time windows.

**On air.** A new widget lights up while the computer's microphone or camera is in use (Windows: the same data
as the taskbar's privacy icons; Linux: running capture streams and open video devices), or while a pushed value is on.
Rules can react to the microphone and the camera too.

**Phone sensors and kiosk.** Brightness can follow the room (light sensor), the night screen can come on whenever
the room is dark, and a hand near the phone wakes it (proximity sensor). Screen pinning keeps Home and Recents on
the panel, and the panel can become the phone's home app, so it starts by itself after a reboot; neither needs root.

**Fixes.** macOS reads processes from one `ps` snapshot (the list stayed empty with many processes); the phone no
longer opens a second live stream after a quick pause and resume; `install.ps1 -Apk` rebuilds the APK when the app's
sources are newer than the one in `dist\` (new `-BuildApk` forces it).

**Live demo.** <https://ersinkoc.github.io/phonebox/> runs the real panel and layout editor in the browser with
made-up data.

## 0.6.2 — 2026-11-07

**Widgets.** Pages are now 12 × 6 grids of widgets instead of fixed screens.

- 41 widget types: clocks (digital, analog, world), calendar, time progress, countdown, pomodoro, quick timer,
  stopwatch, weather, forecast, sun & moon, air quality, now playing, volume, metric tiles, gauges, charts,
  memory & disks, processes, top processes, AI quotas, quota ring, AI sessions, GitHub, shortcut grid, big button,
  phone battery, note, checklist, counter, breathe, quote, title, image / webcam, Hacker News, exchange rates,
  crypto, command output, pushed values and notifications.
- 18 page templates: Home, AI quotas, Sessions, System, Processes, Shortcuts, Music, Clock, Focus, World,
  Dashboard, DevOps, Feeds, Ambient, Markets, Daily, Status, Calm.
- Visual layout editor in the settings page: drag to move, drag the corner to resize, per-widget options,
  a searchable widget palette grouped by category, and a template picker.
- Existing 0.6.1 settings migrate automatically (page order, home tiles, shortcut columns, kill permission).

**Getting around.** Tap the page dots or long-press a page for an overview of all pages; the phone's volume keys
flip pages (can be turned off); the page title flashes when it changes.

**Webhooks.** The agent accepts data as well as serving it: `POST /api/push/<key>` (shown by "Pushed value"
widgets), `/api/notify` (toast, vibration, notifications widget), `/api/goto/<page>` and `/api/refresh`.
Other machines on the network or Tailscale use a separate webhook key that never opens the phone API.

**Command widgets** run a PowerShell command on the PC every N seconds and show its output; the phone never sees
the command.

**Tools.** `tools/demo-server.py` runs the panel and the settings page in a desktop browser with made-up data.

## 0.6.1

- Automatic updates from GitHub releases: the agent installs the new APK on the phone over USB and replaces
  itself.

## 0.6.0

- Wi-Fi & Tailscale mode: pair once over USB, then HTTPS to the PC with a pinned certificate and a secret key.

## 0.5.0

- First public release: home, AI quotas, sessions & GitHub, system, processes and shortcut pages; settings page;
  tray agent; night mode.
