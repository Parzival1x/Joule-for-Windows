# Joule for Windows

A Windows port of [Joule](https://github.com/DysphoDE/joule), the macOS app by [DysphoDE](https://github.com/DysphoDE),
built as an individual project by [Abhishek Jangra](https://github.com/Parzival1x) (see [Credits](#credits)). It shows where a laptop's battery goes:
live power, per-app energy, a plain-language explanation of every lost percent, and alerts when something drains it.

The macOS original is SwiftUI on top of Apple-only APIs, so this is a rewrite in C# / WPF (.NET 10).
The measuring, storage, report, alert and explanation logic is ported one-to-one, and the same checks run against it.

## Features

- **Now**: battery level, whole-PC watts split into apps / Windows services / display & hardware.
  "Where did the X % go?" for the current battery session, with a timeline of battery, power and the app in front.
  Also the apps drawing power right now, and what keeps the PC awake.
- **History**: 1 h / today / 24 h / 7 days / 30 days. Energy, battery used, average power, day strips
  (battery, charging, adapter, sleep), battery and power charts, energy by source, top consumers.
- **Apps**: a sortable table with energy, battery %, share, average power, CPU and GPU time, background share
  and context switches per second. Clicking a row shows the app's history, its processes, and Quit / Show in Explorer / Mute.
- **Battery**: health ring (full vs. design capacity), cycle count, voltage, battery flow, sleep drain per night,
  and battery sessions with projected runtime. "Where?" opens the breakdown of any past session.
- **Devices**: every battery in one place – this PC, Bluetooth mice, keyboards, headsets and phones, PlayStation and Xbox
  controllers, Logitech mice and keyboards on a USB receiver, and a UPS. Every paired device is read and its levels kept; the page and tray panel show the ones connected right now,
  each with its level, a 7-day chart and how many days the charge lasts.
- **Alerts**: an app pulling a lot of power (foreground or background), high total drain, something keeping the PC awake,
  battery lost during sleep, low battery, a device running low. Each alert also appears as a Windows notification. Thresholds are adjustable and apps can be muted.
- **Tray**: by default a battery whose fill is the average level of this PC and every connected device; it turns into a red
  low-battery icon when any of them runs low and stays so until that device is charged. Watts, PC percent or the Joule icon are
  also available. Click it for the compact panel.
- **Device notifications**: a notification when a device connects or disconnects (brief drop-outs of a waking mouse are ignored).
  The Devices page updates the moment Windows or a Logitech receiver reports the change.
- **True to the screen**: per-monitor DPI aware (PerMonitorV2). Joule follows Windows' display scaling exactly – 100 % is
  native pixels, 150 % or 200 % on a 4K screen stays crisp – and moving it between monitors with different scaling re-renders it sharply.
- Uses about 0.1 % CPU in the background. Nothing leaves the PC.

## How it measures on Windows

| What | Source | Notes |
|---|---|---|
| Whole-PC power on battery | Battery driver discharge rate (`IOCTL_BATTERY_QUERY_STATUS`) | Measured, includes the display |
| Whole-PC power on the adapter | CPU package power + a baseline learned on battery | Estimated, shown with "≈" |
| Energy per process | Intel/AMD RAPL core energy (`Energy Meter` counters), shared out by each process's CPU cycles | Measured CPU energy, attributed by cycles |
| CPU time, cycles, context switches | `NtQuerySystemInformation` process list | Every process, no admin needed |
| GPU time per app | `GPU Engine(*)\Running Time` counters | Time, not energy |
| Helpers → app | Same install folder = one app; WebView2 and crash handlers count for the app that started them | |
| What keeps the PC awake | `powercfg /requests` | Windows only reveals this to administrators (see below) |
| Sleep | Suspend/resume events, plus a clock check for modern standby | |
| Display & hardware | Whole-PC power minus all processes | Computed remainder |
| Bluetooth device batteries | The level Windows keeps per paired device (`DEVPKEY` 104EA319…,2) and its timestamp | Last value stays after disconnecting; shown with its age |
| PlayStation controllers | Battery byte of the controller's HID input report | Read only; Joule never switches the controller's mode |
| Xbox controllers | `XInputGetBatteryInformation` | Four steps: full, medium, low, empty |
| Logitech on a receiver (Bolt, Unifying, Lightspeed) | HID++ 2.0 battery features (0x1004, 0x1000, 0x1001) on the receiver's vendor channel | Read only, own software id, works alongside Logi Options+ |
| UPS | Non-system batteries from the battery driver | Listed as a device, not added to the PC's battery |

PCs without a readable energy meter fall back to estimating process energy from CPU time. Those values are marked "estimated".

**Administrator rights:** everything except "Keeping awake" works as a normal user. To see sleep blockers, choose
*Settings → General → Enable …*. This creates a logon task that starts Joule with administrator rights; Windows asks for consent once.

## Install

Run `Joule.exe`. Nothing needs to be installed: the exe is self-contained and includes the .NET runtime.
Turn on **Open at login** in Settings so Joule records all the time. History is stored in `%LOCALAPPDATA%\Joule\history.sqlite`
(per-minute data for 7 days, hourly data for 90 days).

Command-line flags:

```
Joule.exe --demo                    two weeks of sample data, records nothing
Joule.exe --background              start in the tray without opening the window
Joule.exe --demo --snapshot <dir>   render every page to PNG and exit
```

## Build from source

Requires the .NET 10 SDK.

```
dotnet run --project src/Joule.Checks            # 35 checks for the measuring core
dotnet run --project src/Joule.Checks -- probe 5 # one live measurement printed to the console
dotnet run --project src/Joule.Checks -- devices # battery levels of all devices
dotnet publish src/Joule/Joule.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

There are no third-party packages. SQLite comes from `winsqlite3.dll`, which ships with Windows 10 and 11.

```
src/Joule.Core    measuring, storage, reports, alerts, explanations (no UI)
src/Joule         WPF app: window, tray icon and panel, settings
src/Joule.Checks  the checks and the probe command
```

## Credits

**Original app and idea – [DysphoDE](https://github.com/DysphoDE).**
Joule was created by DysphoDE as a macOS menu-bar app for MacBooks: **[github.com/DysphoDE/joule](https://github.com/DysphoDE/joule)**.
The idea, the design and the core of the app are theirs: where every lost percent goes, the plain-language explanation
of it, the session timeline, the alert rules and the way energy is shared out between apps. The idea of bringing the
same app to Windows comes from that original project too. If you use a Mac, get it from its
[releases page](https://github.com/DysphoDE/joule/releases/latest).

**Windows port – [Abhishek Jangra](https://github.com/Parzival1x)** (ajangra619@gmail.com).
An individual project, not affiliated with or endorsed by the original author. It ports Joule to C# / WPF, replaces the
Apple-only measurements with Windows ones, and adds the Devices page (Bluetooth devices, game controllers, Logitech
receivers, UPS), the battery tray icon with low-battery state, and device connect / disconnect notifications.

## License

MIT, like the original. [LICENSE](LICENSE) keeps DysphoDE's original copyright notice alongside the one for the Windows port.
