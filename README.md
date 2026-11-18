# WAI Link

See and control your Android phone and your iPhone from a Windows PC, in one window.

- **Android:** live screen, mouse and keyboard control, hardware keys, Wi-Fi or USB (built on scrcpy).
- **iPhone:** live screen, taps, swipes, long press, typing, hardware buttons, clipboard sync.
  On iOS 27 and later the iPhone sends real video (about 20-55 fps) and its sound to the PC.
  Older iOS versions use screenshots (about 14 fps).
- One rotate button turns the shell between portrait and landscape; nothing rotates on its own.
- A local HTTP API (`http://127.0.0.1:17380/api/...`) for scripts and AI agents: status, screenshot, tap, swipe, type, keys.

Everything stays on your PC. Nothing is sent to a server.

## Download and run

1. Download `WAILink-<version>-win-x64.zip` from [Releases](../../releases) and unzip it to a folder you
   can write to, such as Desktop, Downloads or `D:\Apps`.
2. Run `WAI Link.exe`. No install and no .NET needed. The app is not code-signed, so Windows may show
   "Windows protected your PC": click **More info > Run anyway**.

Everything WAI Link saves (settings, logs, iPhone pairing, received files) goes into the `data` folder next to
`WAI Link.exe`. To move or remove WAI Link, move or delete the whole folder. If that folder is read-only
(for example under `Program Files`), WAI Link uses `%LOCALAPPDATA%\WAI Link\data` instead.

### Settings worth knowing

- **Android speed tweaks** (style menu, off by default): when an Android phone connects, WAI Link changes
  phone settings for speed: animations off, fewer cached apps, lower brightness, account auto-sync off, Play
  Store kept from running in the background. These changes stay on the phone.
- **Phone page** (`http://<your PC>:51820`): a page your iPhone can open for file drops and clipboard sync.
  It only answers your iPhone or links that include the access key; WAI Link's API status shows the full link.
- The local API on port 17380 only accepts requests from this PC and refuses requests made by web pages.

### Android setup

1. On the phone, turn on **Developer options > USB debugging**.
2. Connect it with a USB cable and allow the PC when the phone asks.
3. In WAI Link, pick **Android**.

### iPhone setup (one time)

You need iOS 17 or later, a USB cable, and a free Apple ID.

1. Install Apple's **Apple Devices** app (or iTunes) so Windows has the iPhone USB driver.
2. Install [Sideloadly](https://sideloadly.io).
3. Connect the iPhone by USB, unlock it and tap **Trust**.
4. Run `iphone\Setup-iPhone.cmd`. It shows the Developer Mode switch on the iPhone
   (**Settings > Privacy & Security > Developer Mode**, turn it on and restart) and opens Sideloadly with
   WebDriverAgent. Sign in with your Apple ID and press **Start**.
5. On the iPhone, trust your Apple ID under **Settings > General > VPN & Device Management**.
6. In WAI Link, pick **iPhone**. It connects over Wi-Fi when the PC and iPhone are on the same network;
   if it doesn't find the phone, connect the USB cable.

Notes:
- With a free Apple ID, WebDriverAgent expires after 7 days. Run Sideloadly again to renew it.
- iOS may ask for your passcode to "Enable UI Automation" when WAI Link starts. Enter it on the iPhone.
- While WAI Link shows the iPhone on iOS 27+, iOS displays a screen-sharing indicator.

## Build from source

Needs Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download), [uv](https://docs.astral.sh/uv/) and git.

```powershell
.\scripts\build.ps1          # builds dist\ (the portable app)
.\scripts\build.ps1 -Zip     # also creates release\WAILink-<version>-win-x64.zip
```

The build downloads scrcpy and Python, installs the pinned packages from `iphone\requirements.txt` and applies
`iphone\patches\pymobiledevice3-11.50.3.patch`. Downloads and caches stay in `.build\` inside the repository.

## Repository layout

| Path | What it is |
|---|---|
| `PhoneBridge/` | the app (WinForms shell) |
| `WAILink.Android/`, `WAILink.Apple/`, `WAILink.Agent/` | Android control, iPhone control, local HTTP API |
| `iphone/bridge/` | Python bridge between WAI Link and the iPhone (touch, buttons, video, sound) |
| `iphone/patches/` | changes to pymobiledevice3 for Wi-Fi tunnelling and touch input |
| `scripts/build.ps1` | builds the portable app and release zip |
| `scripts/android/` | optional Android tune-up scripts (debloat, speed tweaks) |

## Contributors

- **[jokwa135](https://github.com/jokwa135)**: creator; design, direction and testing.
- **Claude** (Anthropic): iOS 27 hardware video and sound, bridge recovery, the glass shell and rotation,
  the portable layout and build.
- **Argon** (Gemini): the iPhone bridge (`iphone/bridge/wda_hid_bridge.py`) and the pymobiledevice3
  Wi-Fi tunnel and touch-input changes (`iphone/patches`).
- **Codex** (OpenAI): development work on WAI Link.
- **Hermes**: development sessions on WAI Link.

## License

WAI Link is licensed under the [GNU GPL v3](LICENSE). It bundles third-party software under their own licenses;
see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
