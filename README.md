# Meet Mic Sync

A small Windows utility that syncs the **Lenovo laptop microphone mute key** with **mute in Google Meet**.

## The problem

Many Lenovo laptops have a dedicated microphone mute key. In Microsoft Teams it usually works as expected: press once to mute in the call, press again to unmute.

**Google Meet** does not behave the same way. Meet runs in the browser and often treats a hardware mic mute as a device failure, not as a normal mute. Instead of quietly muting you, it may show a microphone error.

Meet itself can mute just fine with its own button or **Ctrl+D**. The Lenovo hardware key simply does not talk to Meet on its own.

## How it works

Meet Mic Sync runs in the background and stays light on the system: it does not poll constantly — it only reacts when you press the key.

When you press the Lenovo mute key, the app:

1. detects the press (via Windows / Lenovo signals);
2. finds the open Google Meet window;
3. toggles the microphone **inside Meet** (same idea as Meet’s mute button or Ctrl+D).

## Requirements

- Windows 10 or 11
- A Lenovo laptop with a mic mute key (usually via Lenovo Hotkey / Vantage)
- Google Meet in **Chrome**, Edge, or Brave

No separate .NET install is needed if you download the **self-contained** `.exe` from Releases.

## Download and run (recommended)

1. Open this repository’s [Releases](../../releases) page.
2. Download `MeetMicSync.exe`.
3. Double-click it (you can keep it in Downloads for a quick test).
4. Look for the **Meet Mic Sync** icon in the system tray (near the clock).

### Start automatically with Windows

1. Right-click the tray icon.
2. Choose **Start with Windows…**
3. Read the confirmation dialog and click **Yes** if you agree.

The dialog explains exactly what will happen. In short, the app will:

- copy itself to your user Programs folder  
  (`%LOCALAPPDATA%\Programs\MeetMicSync\`) — no administrator rights;
- create a Startup shortcut so it launches when you sign in;
- restart from that folder (so Startup does not depend on a file left in Downloads).

To undo later: tray icon → **Remove from Windows Startup…** (also asks for confirmation). This only removes the shortcut; program files stay on disk.

## Build from source (optional)

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

Self-contained single `.exe` (easiest to share):

```powershell
git clone <this-repository-url>
cd meet-mic-sync
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
.\publish\MeetMicSync.exe
```

## Usage

1. Start **Meet Mic Sync**.
2. Open Google Meet and join a call (the Meet tab must be the active tab in the browser window).
3. Press the Lenovo mute key — Meet’s microphone should toggle along with it.

### Tray menu

| Item | What it does |
| --- | --- |
| **Test Meet mute (Ctrl+D)** | Test finding Meet and toggling mute without the Lenovo key |
| **Start with Windows…** | Copy to a user folder + add Startup shortcut (with confirmation) |
| **Remove from Windows Startup…** | Remove the Startup shortcut (with confirmation) |
| **Open log** | Open the log file (only useful after logging is enabled) |
| **Enable logging** / **Disable logging** | Turn diagnostic logging on or off (off by default) |
| **Exit** | Quit the app |

## Notes

- The Meet tab must be **active** in the browser window (the window title should contain `Meet`). If another tab is selected in the same window, the app may not find the call.
- This is an unofficial utility and is not affiliated with Google or Lenovo.
- Tested on **Lenovo ThinkBook 16p Gen 2** (machine type `20YM`). Behavior can vary by Lenovo model. If Meet mute does not toggle, check the log from the tray menu.