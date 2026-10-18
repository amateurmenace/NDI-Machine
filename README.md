# NDI Machine

Native Windows NDI-to-SDI router under development. Four persistent native workers, shared Dante USB audio capture, a reconnectable local control API and a guarded Companion fail-over button are implemented. This is a working engineering milestone, not a production-qualified release. The full WPF interface, general audio mixer and remaining capabilities are tracked in `reports/RELEASE-STATUS.md`. The user's briefs are preserved in `docs/`.

The project is now pinned to the latest official DeckLink SDK **16.0**. Read-only compilation and hardware enumeration passed against installed Desktop Video **16.3**. See `reports/SDK-UPDATE.md` and `reports/RELEASE-STATUS.md`.

Workers retain media across control-host restarts and use prepare/commit source switching. The fixed stereo audio path uses the exact commissioned Dante USB I/O Module receive 1/2 endpoint, bounded shared buffers and independent adaptive resamplers. Four concurrent outputs passed short SDK scheduling tests with video and audio. Independent physical picture/audio, lip-sync, codec variants and sustained soak qualification remain pending. See `reports/MEDIA-PROOF.md` and `reports/CONTROL-PROOF.md`.

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

For the commissioned persistent backups, build the control host and Companion module, then follow `docs/CONTROL-API.md` and `docs/COMPANION.md`. `Start-Control.ps1` attaches to existing workers. `Prepare-Backups.ps1` explicitly starts the commissioned SDI outputs and saves recovery intent; it sends no Videohub commands. Subsequent control-host starts restore that saved intent. Closing Companion or the control host leaves media running.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build 'A:\___ENGINEERING PROJECTS\NDI Machine\control\NDIMachine.Control.csproj' -c Release
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Build-Companion.ps1'
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Start-Control.ps1'
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Prepare-Backups.ps1'
```

Run builds from the project directory so `global.json` selects the tested .NET SDK. The commissioning file is machine-specific and intentionally excluded from Git; `config/commissioning.example.json` documents its shape. Never copy example device IDs onto hardware without commissioning them.

NDI® is a registered trademark of Vizrt NDI AB. [NDI information and downloads](https://ndi.video/). The repository uses the requested working project name; final product naming and binary distribution remain subject to vendor terms described in `docs/DEPENDENCIES-LICENSES.md`.
