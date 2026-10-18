# NDI Machine desktop

Launch **NDI Machine.exe** from the complete `dist/NDI Machine` folder. The GUI is a self-contained x64 WPF executable with .NET 8.0.32. Keep `engine`, `launcher`, `build/Release`, `config` and `notices` beside it. Installed NDI Runtime, Desktop Video and Microsoft VC++ x64 runtime are prerequisites; proprietary runtimes are not bundled.

The GUI connects to the local engine at port 9860. Closing it leaves media and control running. **Start control host** launches the packaged host when offline and restores validated armed intent. Opening the GUI itself never prepares outputs or switches the Videohub. It can be used for regular production independently of Companion: choose each slot's source, audio and SDI connector in **Edit output**. Confirmed custom settings survive GUI/control restarts. **Prepare backups** is an explicit restoration of the emergency preset, not a requirement for ordinary operation.

The optional lightweight launcher listens on localhost port 9861. Run `scripts/Register-Launcher.ps1` to start it and register it for the current user's Windows sign-in; `-NoStartup` starts it for this session only. On this commissioned workstation it is registered. It launches only this package's GUI/control host when Companion's **CLICK TO PRIME** is pressed. Companion then explicitly prepares the emergency preset, without changing Videohub routes or ATEM/Zoom audio. A separate press performs the toggle. No media or GUI is automatically started merely by the launcher signing in. To remove sign-in registration, remove the `NDI Machine Launcher` value from `HKCU:\Software\Microsoft\Windows\CurrentVersion\Run`.

## Output controls

**Edit output** opens the source, receiver, DeckLink and audio controls for that slot:

- Exact NDI source name and optional transport address (IP with optional port or the SDK's vendor HX address), populated from discovery.
- Highest or lowest receive bandwidth. Low bandwidth changes the actual SDI source quality; it is not required for GUI thumbnails.
- Fit/letterbox, fill/crop or stretch sizing. Output stays 1920x1080p30000/1001 UYVY for the commissioned ATEM. Other received sizes/rates are converted. Matching UYVY is copied exactly; other sizes use nearest-neighbour SDR scaling. The SDK deinterlaces and frame-synchronizes the input. HDR conversion is not implemented.
- Any enumerated compatible, free DeckLink connector by persistent identity. No profile changes, index-based assignments or stealing occupied outputs. The commissioned OBS Key/Fill card is reserved. Changing connectors interrupts only that output after preparation; incompatible or unavailable hardware is rejected.
- Audio from an explicit Windows capture endpoint, a Windows playback device's loopback mix, the selected NDI source, or silence. Windows device defaults are never changed. This is WASAPI shared mode, not ASIO or per-application process capture.
- Independent left/right channel selection, mute, gain from -60 to +12 dB and 0-500 ms additional audio delay. Stereo 48 kHz is embedded on SDI channels 1/2. This selects one stereo source per output, not a multi-source mixer.

Settings prepare before frame-boundary commit. Confirmation requires actual worker identity, displayed operation and revision; timeouts are never replayed automatically. Confirmed settings are saved for worker recovery. An occupied or unknown ownership mutex prevents takeover. A changed connector or old-worker upgrade requires a short worker restart. Other outputs remain independent.

**Prepare backups** deliberately restores the commissioned sources/connectors and highest bandwidth: Dante USB I/O Module receive 1/2 at unity on **output 1 only**, with outputs **2-4 silent**. It does not change Videohub routes, Zoom or ATEM audio. Custom settings may make network failover readiness unavailable until the preset is restored.

## Discovery and monitoring

Sources are discovered automatically when the GUI connects and refreshed before an output editor opens. The picker shows every returned source, with editable name entry and its own **Refresh sources** button; refreshing preserves the pending selection. Discovery does not open video receivers. **Discovery / IPs** saves up to 32 external sender IPs and an optional group list, then refreshes discovery. Select the returned named source in an output editor. The IP must be reachable and the sender must expose an NDI source; this is not an RTSP/HTTP decoder. The editor also accepts a direct NDI transport address when known. Discovery can only list sources visible to the runtime's network/group configuration; it is not a guarantee of every sender across isolated networks.

GUI previews reuse the already decoded output through bounded local shared memory: 320x180 BGRA, at most 4 fps, no additional NDI connection or network subscription. The GUI can pause previews. A stale, failed or mismatched revision clears its preview. Previews are optional and never block SDI scheduling; they do not prove downstream signal.

Expand **Receiver statistics & audio meters** for input dimensions and advertised frame rate, measured arrival rate, receiver connection count, video/audio totals, receiver drops and queue depths, SDI late/dropped/fallback/repeat counters, stereo peak dBFS, clipped samples, configured gain/delay, audio buffer occupancy and clock correction. These are SDK/software observations, not network bitrate or end-to-end latency measurements. Codec and hardware-decode selection are managed by the NDI runtime; the standard SDK does not establish which codec/decoder is active.

Windows GPU counters observed VideoDecode activity for all four commissioned HX3 workers on this workstation. Video scheduling, conversion, previews and external-audio resampling use CPU work. External audio is adapted to the SDI consumption clock to control drift; this does not establish lip-sync. Measure picture/sound alignment at the ATEM and set audio delay accordingly. Physical listening, calibrated lip-sync and sustained soak tests remain unqualified.

**Diagnostics** saves local state, source/device identities and recent GUI activity. Review before sharing. The map in the sidebar is a commissioned plan; Companion provides live crosspoint verification. **Open Companion** opens the current emergency controls. The desktop does not duplicate that emergency sequence.

## Build

Run `scripts/Build-Desktop.ps1` after building native targets. `-NativeBuildDirectory build\preview\Release` selects an isolated native build; `-OutputDirectory 'dist\NDI Machine-staging'` builds separately from a running package. `-IncludeLocalConfiguration` creates a private workstation package; do not publish that directory or zip. The script runs control tests, publishes the GUI, host and launcher, and copies owned native executables plus open-source notices. All build/package products are ignored by Git.

See Microsoft's [single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview), [NDI receiver documentation](https://docs.ndi.video/all/developing-with-ndi/sdk/ndi-recv), and [WASAPI loopback documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording). GUI, control and media remain separate processes so a GUI closure cannot stop SDI.
