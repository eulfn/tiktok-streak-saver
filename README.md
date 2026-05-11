# Feener

> **Before installing, read the [known issues](https://github.com/eulfn/streak-tiktok/issues/1).**

Feener is an Android background service that automatically sends messages to your TikTok friends on a 23-hour cycle to maintain streaks. It is a fork of [TiktokStreakSaver](https://github.com/Jon2G/TiktokStreakSaver) by Jon2G, extended with additional features, stability improvements, and a redesigned interface.

---

## Requirements

- Android 7.0 (API 24) or higher
- A TikTok account
- An active internet connection

---

## Installation

### From a pre-built release

1. Download the APK from the [latest release](https://github.com/eulfn/streak-tiktok/releases/latest).
2. Enable **Install from unknown sources** on your device.
3. Open the downloaded APK and install it.

### From source

```bash
git clone https://github.com/eulfn/streak-tiktok.git
cd streak-tiktok/src/Feener
dotnet workload install maui-android
dotnet restore
dotnet build -f net9.0-android -c Release
```

---

## Usage

1. Open the app and tap **Login to TikTok**.
2. Sign in through the in-app WebView.
3. Add friends by tapping **Add** and entering their TikTok username.
4. Set the message to send in the **Message to Send** field.
5. Toggle **Scheduling** on.
6. Grant the following permissions when prompted:
   - Battery optimization exemption
   - Exact alarm access
   - Notification access

---

## How it works

```
App start
  └─ Schedule 23-hour alarm
       └─ AlarmReceiver fires
            └─ Start foreground service
                 └─ Load TikTok in WebView
                      └─ For each friend: find chat → send message
                           └─ Complete → re-arm alarm (if scheduling is ON) → stop service
```

---

## Configuration

**Interval** — defaults to 23 hours. To change it, update `DefaultIntervalHours` in `Services/SettingsService.cs`:

```csharp
public const int DefaultIntervalHours = 23;
```

**Message** — configurable in the app UI. Defaults to `Streak`.

---

## What's different from TiktokStreakSaver

### Rebranding
- Renamed to Feener with package ID `com.fen.loid`
- New adaptive app icon with dark mode variant
- Custom notification icon and splash screen

### New features
- **In-app updater** — downloads and installs the latest APK directly, with a GitHub fallback
- **Skip unreachable users** — continues the automation run when a user cannot be found, rather than aborting
- **Auto-disable missing users** — when skip is enabled, users that repeatedly fail are automatically disabled to prevent future failures
- **Formatted release notes** — update dialogs render Markdown via a built-in converter
- **Pull-to-refresh** — refreshes schedule status and the friend list on demand
- **Friend management** — edit display names, clear friend lists, and wipe activity history
- **Import/Export** — back up and restore friend configuration

### UI
- Full light and dark mode with semantic color tokens
- Card-based layout with consistent spacing and typography
- Inter typeface throughout
- Dynamic status bar color tied to the active theme
- Per-friend notification progress during automation runs
- Sticky automation controls pinned to the bottom of the screen

### Stability fixes
- Changed `StartCommandResult.Sticky` to `NotSticky` to stop the service from restarting automatically and draining battery when idle
- Added alarm cancellation in `RunNow` to prevent duplicate scheduled runs after a manual trigger
- Guarded `ScheduleNextRun` in `CompleteService` so the alarm only re-arms when scheduling is toggled on
- Fixed the automation chain firing multiple times on a single page load
- Fixed a duplicate update dialog appearing on pull-to-refresh
- Fixed a race condition in the silent update check triggered after the welcome screen
- Fixed timer and handler leaks in session validation and download failure paths
- Fixed a `NullReferenceException` in the background service on slow network connections
- Removed duplicate event handler registrations across page navigations

### Build and CI
- Release pipeline triggered automatically on Git tags matching `v*`
- Signed APK output with keystore validation
- APK identity report on each build (package name, version code, signing fingerprint)
- Version number derived from the Git tag rather than hardcoded in the project file
- Release notes generated automatically from commit history

---

## Project structure

```
src/Feener/
  Models/                          Data models
  Services/
    SettingsService.cs             Preferences and friend list persistence
    SessionService.cs              TikTok session validation
    UpdateService.cs               Update check and APK download
    TikTokWebViewHelper.cs         WebView cookie and session management
  Platforms/Android/
    Services/StreakService.cs      Foreground service and automation logic
    StreakScheduler.cs             AlarmManager scheduling and manual runs
    Receivers/AlarmReceiver.cs     Alarm broadcast receiver
    Receivers/BootReceiver.cs      Boot-completed receiver
  Resources/Raw/
    tiktok_automation.js           JavaScript injected into the TikTok WebView
  AboutPopupPage.xaml              Update dialog and welcome screen
  MainPage.xaml                    Main UI
  LoginPage.xaml                   TikTok login WebView
.github/workflows/
  android-release.yml              CI/CD release pipeline
```

---

## Credits

Original project: [TiktokStreakSaver](https://github.com/Jon2G/TiktokStreakSaver) by [Jon2G](https://github.com/Jon2G).  
Modified and maintained by [@eulfen](https://github.com/eulfn).

---

## Disclaimer

This application is intended for educational purposes only. Use it responsibly and in accordance with [TikTok's Terms of Service](https://www.tiktok.com/legal/page/global/terms-of-service/en). The developers accept no responsibility for account restrictions or bans that may result from its use.

---

## License

MIT License. See [LICENSE](LICENSE) for details.
