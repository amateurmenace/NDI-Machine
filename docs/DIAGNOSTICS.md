# Native diagnostic tools

These tools are development proofs, not the finished router. They are complete native C++20 programs using the installed standard NDI SDK and DeckLink SDK 16.0. Source lookup, automatic reconnection and framesync use the SDK. The output format is strictly 1920x1080 progressive 30000/1001, 8-bit UYVY, 48 kHz stereo. Unsupported incoming dimensions, rate, fielding, or pixel layout produce black after startup; they are not relabelled as supported.

## Read-only tools

`scripts/Test-DeckLinkSdk.ps1` compiles and runs SDK inventory, saving the local result to ignored `reports/decklink-sdk-inventory.json`. It lists stable device/group IDs, active/available profiles, subdevice indices, reference lock when exposed, busy flags, audio capacity, and the initial mode/pixel-format capability. Busy flags do not reveal the occupying application.

`scripts/Probe-Ndi.ps1` discovers sources and optionally receives an exact named source for a bounded duration. `-Groups` and `-ExtraIps` map to the supported NDI finder settings. Configure Discovery Server and adapter selection with installed NDI Access Manager. This diagnostic does not implement per-receiver NIC binding. Audio/video packet counts and last-arrival ages are separate. Receiver codec and GPU acceleration are Unknown without trustworthy metadata.

## Output proof

Running these commands **enables the chosen physical SDI output** for the specified duration and stops it afterward. Use a free, commissioned connector and independently monitor its destination. Read the stable ID from the live inventory; never substitute an enumeration index. No profile is changed.

```powershell
# Local example: use the stable ID identified in this studio's inventory.
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Test-Sdi.ps1' -Slot 1 -Device 0xe6a968c0 -Source 'AUD CAM 1 (Chan 1)' -Seconds 10

# Labelled bars with left 1-kHz and right 2-kHz tones at -20 dBFS.
& 'A:\___ENGINEERING PROJECTS\NDI Machine\scripts\Test-Sdi.ps1' -Slot 1 -Device 0xe6a968c0 -Bars -Seconds 10
```

Source preparation has a ten-second deadline and precedes output enable. Each output owns four preallocated video/audio buffers and a bounded callback completion queue. Hardware completion callbacks wake a separate scheduling loop; they perform no conversion, logging, network wait, disk access, or allocation. Video is timestamped in 1001/30000 increments. Audio is timestamped on an integer 48-kHz timeline with 1601/1602-sample blocks. Partial writes must make bounded positive progress. Named slot and device mutexes prevent another instance of these tools from claiming the same output. Only slots 1-4 exist.

The input format must be commissioned as SDR Rec.709. The prototype does not qualify HDR metadata/conversion. Audio follows video only: stale/missing video or audio mutes NDI audio. It applies SMPTE calibration (NDI float / 10), a startup gain ramp, finite-sample sanitization, and hard clipping to signed 32-bit PCM. No limiter or external audio is implemented. This diagnostic has no live source-switch command, fixed/custom mix, preview, or supervisor.

`scripts/Test-FourOutputs.ps1` accepts a local JSON file with four distinct objects containing `slot`, `device` (stable hex ID), and `source` (exact discovery name) under `outputs`. It runs four separate processes and saves each result in ignored `reports/private/`. This is concurrent scheduling, not an atomic or synchronized multi-card start.

## Interpreting results

`completed`, `late`, `dropped`, and unexpected `flushed` describe DeckLink callbacks while running. `inputRepeats` counts repeated NDI frame timestamps and is separate from output deadline misses. Stop-time intentional flushing is excluded from healthy-run counters. `fallbackFrames` counts frames where missing/stale/unsupported NDI video produced black. `physicalSignalVerified` remains Unknown: successful scheduling cannot prove the cable, monitor, embedded audio levels, or downstream switcher input.

The application/API/audio-host/Companion/emergency coordinator remains future implementation. See `reports/RELEASE-STATUS.md` for the exact boundary.
