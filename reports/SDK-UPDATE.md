# DeckLink SDK update - 18 October 2026

The project now selects Blackmagic Desktop Video SDK **16.0**, the latest SDK listed on the [official developer page](https://www.blackmagicdesign.com/developer/products/capture-and-playback/sdk-and-software) when checked. It was released 8 April 2026. The Desktop Video software/driver uses a separate release sequence; the installed driver remains **16.3.0.0**.

SDK location: `A:\___ENGINEERING PROJECTS\NDI Machine\.deps\Blackmagic DeckLink SDK 16.0`.

Used the already-downloaded `C:\Users\Broadcast Pix\Downloads\Blackmagic_DeckLink_SDK_16.0.zip`. Windows download metadata records Blackmagic's `swr.cloud.blackmagicdesign.com` host and `www.blackmagicdesign.com` referrer. Archive paths were checked before extracting 1,959 entries. The version header declares SDK 16.0. The locally calculated SHA-256 is recorded in `config/dependencies.lock.json`; it is an archive fingerprint, not a vendor checksum.

| Check | Result |
|---|---|
| Latest SDK identified from official listing | Passed: 16.0 |
| SDK extracted alongside old version | Passed; old SDK 15.3 untouched |
| x64 MIDL generation from SDK 16.0 IDL | Passed |
| MSVC C++20 compile and link | Passed |
| Project CMake Release build and CTest | Passed; 1/1 read-only inventory test |
| COM activation against installed Desktop Video | Passed; runtime API reports 16.3 |
| Read-only card enumeration | Passed; 12 subdevices / 3 device groups |
| Intended NDI Machine outputs support 1080p29.97 / UYVY query | Passed on all four |
| Physical SDI picture, embedded audio and connector mapping | Not Run |
| Playback and long-duration driver compatibility | Not Run |

No card profile, output enable state, driver, firmware, OBS setting, or Videohub route was changed by this check. `Key / Fill 1` returned busy flags 2. The check does not identify which application occupies it.

Repeat the read-only compile/runtime check from PowerShell:

```powershell
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Test-DeckLinkSdk.ps1'
```

Machine-readable results are in `reports/decklink-sdk-inventory.json`. The native binary is `build/sdk-check/decklink-sdk-check.exe`. Proprietary SDK files stay under ignored `.deps/` and are excluded from future source packages. The SDK license is retained at `.deps/Blackmagic DeckLink SDK 16.0/End User License Agreement.pdf`; redistribution review remains a release prerequisite.
