# NDI Machine

Native Windows NDI-to-SDI application with a self-contained WPF EXE, four independent media workers and a Companion failover toggle. The desktop provides low-overhead previews, per-output NDI/DeckLink selection, Windows capture or playback-loopback audio, NDI audio, channel/gain/mute/delay controls, receiver statistics and manual IP discovery. This is an engineering build; physical signal, lip-sync and sustained operation remain unqualified. The user's briefs are preserved in `docs/`.

The project is now pinned to the latest official DeckLink SDK **16.0**. Read-only compilation and hardware enumeration passed against installed Desktop Video **16.3**. See `reports/SDK-UPDATE.md` and `reports/RELEASE-STATUS.md`.

Workers retain media across GUI/control-host restarts and use prepare/commit settings changes. The commissioned failover preset embeds Dante USB I/O Module receive 1/2 on the physical output feeding ATEM Input 1 (currently NDI Machine 2); the other three outputs are silent. Each output can instead select a different stereo source. Shared capture and independent adaptive resamplers control audio drift. See `docs/DESKTOP.md` for controls and packaging, and `reports/RELEASE-STATUS.md` for validation and remaining work.

Select **Production** for everyday NDI-to-DeckLink routing, independently of Companion and Videohub. **Edit output** selects any discovered source, compatible available connector and audio settings. The view selection is remembered and never changes media. Discovery refreshes automatically; manual IP/group discovery is available. Select **Failover** to inspect the saved emergency plan. Pressing the Companion failover button restores emergency media over custom production assignments before taking Network; production settings no longer cause BLOCKED. Hold three seconds and release to restore the previous production outputs, crosspoints and audio. When the app is stopped, **CLICK TO PRIME** first opens/connects/prepares it; a separate click toggles. Register the fixed local launcher with `scripts/Register-Launcher.ps1` after building the package.

Companion also provides [quick NDI/cloud routing](docs/PRODUCTION-ROUTER.md): choose a source, Input 3 or 4, then TAKE on one screen. More inputs exposes all eight ATEM destinations. Home buttons use large text with current Input 3/4 feeds. The app quadrants are ordered and labeled ATEM Inputs 1–4, with physical output identities and live destination labels underneath. Production opens without starting receivers; Stop source and Engine controls release streams and restart the engine idle.

Build the EXE after the native targets:

```powershell
.\scripts\Build-Desktop.ps1 -IncludeLocalConfiguration
```

Launch `dist\NDI Machine\NDI Machine.exe` and keep its companion folders beside it. The optional local configuration makes this a private workstation package; do not publish it. A clean source build without that switch includes only the example configuration. Installed NDI Runtime, Desktop Video and VC++ x64 runtime are required.

Open the folder in VS Code:

```powershell
code 'A:\___ENGINEERING PROJECTS\NDI Machine'
```

In VS Code, choose Terminal > New Terminal, select PowerShell, and run:

```powershell
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Setup-Dependencies.ps1'
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Build.ps1' -NativeOnly
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Native.ps1'
```

To rebuild the standalone SDK check and save a refreshed inventory:

```powershell
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Test-DeckLinkSdk.ps1'
```

These commands enumerate hardware without enabling SDI output, changing profiles, or altering other applications. `.deps/` contains the locally extracted SDK, excluded from source control. No SDK installer, driver update, reboot, or firmware update was required.

Discover NDI sources, then probe one using its exact discovery name:

```powershell
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Probe-Ndi.ps1'
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Probe-Ndi.ps1' -Source 'AUD CAM 1 (Chan 1)' -Seconds 12
```

NDI probing opens one real receiver for the requested duration. Audio packet freshness is measured separately from video; codec and acceleration are reported as Unknown where the standard SDK does not establish them.

For explicit physical output tests, read `docs/DIAGNOSTICS.md`. Those commands enable a selected SDI connector and are separate from read-only inventory.

For the commissioned persistent backups, build the control host and Companion module, then follow `docs/CONTROL-API.md` and `docs/COMPANION.md`. `Start-Control.ps1` attaches to existing workers. `Prepare-Backups.ps1` explicitly starts the commissioned SDI outputs and saves recovery intent; it sends no Videohub commands. Subsequent control-host starts remain idle until Apply or PRIME; already-running workers continue. Closing Companion or the control host leaves media running.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build 'A:\___ENGINEERING PROJECTS\NDI Machine\control\NDIMachine.Control.csproj' -c Release
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Build-Companion.ps1'
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Start-Control.ps1'
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Prepare-Backups.ps1'
```

Run builds from the project directory so `global.json` selects the tested .NET SDK. The commissioning file is machine-specific and intentionally excluded from Git; `config/commissioning.example.json` documents its shape. Never copy example device IDs onto hardware without commissioning them.

NDI® is a registered trademark of Vizrt NDI AB. [NDI information and downloads](https://ndi.video/). The repository uses the requested working project name; final product naming and binary distribution remain subject to vendor terms described in `docs/DEPENDENCIES-LICENSES.md`.
