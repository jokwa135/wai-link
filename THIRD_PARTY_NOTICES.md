# Third-party software

WAI Link bundles or downloads the following software. Each keeps its own license.

| Component | Used for | License |
|---|---|---|
| [scrcpy](https://github.com/Genymobile/scrcpy) 5.0.1 (`lib\wailink-render.exe`, `lib\wailink-server`, `lib\scrcpy-server`) | Android screen and input | Apache-2.0 |
| [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools) (`lib\adb.exe`, `AdbWin*.dll`), as shipped with scrcpy | Android connection | Apache-2.0 |
| [FFmpeg](https://ffmpeg.org) libraries (`lib\av*.dll`, `swresample`), as shipped with scrcpy | Android video decoding | LGPL-2.1+ |
| [SDL3](https://www.libsdl.org) (`lib\SDL3.dll`) | Android renderer window | Zlib |
| [libusb](https://libusb.info) (`lib\libusb-1.0.dll`) | USB | LGPL-2.1+ |
| [Python](https://www.python.org) 3.12 (`iphone\python`, from python-build-standalone) | iPhone side | PSF-2.0 |
| [pymobiledevice3](https://github.com/doronz88/pymobiledevice3) 11.50.3, with WAI Link's patch in `iphone\patches` | iPhone connection, video, sound, touch | GPL-3.0 |
| [PyAV](https://github.com/PyAV-Org/PyAV) (with its FFmpeg build) | iPhone video and sound decoding | BSD-3-Clause (FFmpeg: LGPL-2.1+) |
| [Pillow](https://python-pillow.org) | iPhone image handling | MIT-CMU |
| Other Python packages listed in `iphone\requirements.txt` | dependencies of the above | see each package |
| [WebDriverAgent](https://github.com/appium/WebDriverAgent) (`iphone\WebDriverAgentRunner.ipa`, unsigned) | iPhone automation (taps, typing) | BSD-3-Clause |
| [.NET](https://dotnet.microsoft.com) runtime (inside `WAI Link.exe`) | the app | MIT |

The full license texts are on each project's site. The pymobiledevice3 patch is published here under GPL-3.0
together with the rest of WAI Link.
