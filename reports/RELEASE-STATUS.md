# Development status — 0.4.0

This is a working engineering build, not a physically qualified broadcast release. Historical evidence is in MEDIA-PROOF.md, CONTROL-PROOF.md and SDK-UPDATE.md; this page describes the current scope.

## Implemented and built

- Self-contained Windows x64 WPF `NDI Machine.exe`, packaged control host and independent native workers. Closing the GUI or control host leaves media running.
- Four source/output editors: automatic network discovery, exact NDI name and optional IP:port or vendor HX transport address, highest/lowest receive bandwidth, fit/fill/stretch SDR conversion to 1080p29.97, stable DeckLink connector selection with mode/ownership/reserved-card checks.
- Windows capture and playback-loopback audio, selected NDI audio or silence; independent stereo channels, gain, mute and 0-500 ms audio delay. Shared capture readers adapt to the SDI clock. No device-default changes.
- Previews reuse decoded output through local shared memory at 320x180 and at most 4 fps, without another NDI receiver. Input/SDI counters, receiver queues/drops, peak meters, clipping, buffer occupancy and clock correction are exposed.
- Manual external-IP/group discovery, source inventory, local diagnostics and persisted per-output intent. Existing custom workers survive host upgrades; explicit Apply/Prepare performs changes, missing workers recover with bounded backoff.
- Separate Production/Failover views, persisted locally. Production shows actual output health and provides ordinary NDI/DeckLink/audio routing without polling Companion. Mode selection changes no media.
- Companion 0.4.1: an explicit short release restores emergency media over custom production assignments, verifies readiness and takes Network over known unlocked routes. Hold three seconds and release restores preceding production settings, routes, ATEM mix and known Zoom selection. Undo is session-only. Reconnect never writes or replays. The updated control host is required for automatic production takeover.
- Network invokes ZOOM DANTE and enables the explicitly commissioned ATEM input receiving the one audio-bearing path. Fiber invokes ZOOM MADI and enables only XLR + MADI 1/2 + 3/4. Gains, master and processing are unchanged. The module owns the sequence behind the button's Down/Up actions.

The commissioned preset embeds Dante USB I/O Module receive 1/2 on **output 1 only**, with outputs **2-4 silent**. The operator confirmed that audio reaches **ATEM Input 3** and explicitly requested retaining video input map **6/7/8/5** to destinations **33/34/35/36**. `audio.networkAtemInput: 3` configures the audio destination independently of the video paths. Fiber remains **9/10/11/12**. Director failover is **1/1/0**, XL **21/1/1**; Zoom DANTE **1/1/2**, MADI **1/1/1**.

## Validation

- Native CTest: **5/5** (read-only SDK inventory, media arithmetic/queues, audio bridge, previews and output settings/conversion/delay).
- Control/desktop simulated tests: **28/28**, including failed prepare, revision/epoch races, displayed-state confirmation, no replay after lost ACK, reserved/busy/duplicate hardware, silent readiness and preservation of custom outputs.
- Companion tests: **51/51**; package smoke import succeeds under the installed Node 26 permission model without connecting to hardware.
- Actual WPF visual-tree rendering verified dashboard and editor layout. This is an application render, not a physical output test.
- New keyed Dante USB capture path delivered 144960 frames in three seconds, nonzero samples and no timestamp/nonfinite errors. Multichannel and alternate-rate devices have not been qualified.
- All four upgraded workers are active with previews. Fresh snapshots show zero SDI late/dropped/fallback frames. Output 1 has nonzero USB samples; outputs 2-4 report silent/muted with zero peaks. Worker identities survived the subsequent GUI/control restart. Physical signal and lip-sync remain unqualified.
- Windows GPU counters showed VideoDecode activity for all four upgraded HX3 workers on NVIDIA RTX 5060 Ti. Conversion, scheduling, previews and external-audio resampling also use CPU work. No blanket codec or GPU guarantee is claimed.

## Activation state

Earlier activation upgraded all four workers sequentially under the operator's restart authorization. PRIME opened the then-installed 0.3.1 GUI/engine at 17:46:39 local, and the module confirmed backup preparation at 17:46:42. A separate operator toggle to Network was confirmed at 17:48:35. The operator confirmed they were testing the button. Codex did not press the live toggle. The launcher is registered at current-user sign-in and opens no app/media until explicitly primed. Companion shell permissions remain disabled.

Discovery fixes found 18 sources in the initial query and 19 as the network changed. The installed GUI reports 19; sources refresh automatically on connection and when opening an editor. SDK-provided vendor HX addresses are accepted. The existing four workers remain healthy. Regressions fixed repeated state reads attempting to reuse a parented JSON node and native `0.0` versus managed `0` causing false settings mismatches.

The operator subsequently confirmed Input 3 and requested leaving video routing unchanged. GUI 0.4.0 is now open in Production mode; Companion 0.4.1 is installed with `networkAudioInput: 3` and both buttons retain Down/Up actions. Read-only hardware observation found Fiber crosspoints 9/10/11/12 and only XLR + MADI 1/2 + 3/4 ON. No router or ATEM audio write was made for this update.

The operator manually stopped the old control host after automatic approval review rejected its termination. **Control host 0.4.0 is now installed and active**, alongside GUI 0.4.0 and Companion 0.4.1. Fresh verification at 19:28 America/New_York on 2026-10-18 confirms `emergency-restore`, armed recovery, all four healthy emergency-configured outputs, and an exact match between the host and Companion plans with Network audio Input 3. The four worker identities and configuration revisions remained unchanged, with zero late/dropped/fallback SDI frames. All 40 Videohub crosspoints remained unchanged; the emergency destinations were still Fiber. Saved desired output settings were preserved while their commissioning fingerprint was migrated for the audio-destination metadata correction. Automatic production takeover is now available on the next deliberate failover press. No live takeover/undo was performed during activation.

Current automated validation: desktop/control **28/28**, Companion **51/51**, including connector-cycle ownership checks, production takeover and undo, stale worker/host protection, Input 3 selection and no retry after lost acknowledgements. Native media code is unchanged from the previous **5/5** run. The actual WPF Production view was rendered and inspected using live telemetry; this is an application render, not a desktop screenshot. Live connector-cycle takeover and physical listening remain untested.

Companion's installed Down/Up actions and four feedbacks were restored. Full-layout reset/imports were logged at 16:42:22, 16:50:56, 16:52:18 and 17:14:04 local on 2026-10-18. The latest import retained the repaired bindings. The log does not identify the initiating client. A secret-excluded backup is saved privately; verify actions after future imports.

## Remaining qualification and scope

Physical SDI picture/audio, independent connector observations, calibrated ATEM levels/lip-sync, separate full-NDI and HX3 H.264/H.265 qualification, real 24/72-hour soaks, crash/device/profile-change recovery and actual failover/undo listening tests remain Not Run or Unknown. Counters, thumbnails and ACKs do not prove downstream signal.

ASIO, mixing multiple audio sources together, per-application process capture, configurable video delay, HDR conversion, additional SDI standards, advanced fallback choices, authenticated LAN control and an installer remain outside this build. General Windows endpoints are exposed through WASAPI shared mode. Audio delay is available but lip-sync is not automatically calibrated.

Source is intended for the user-authorized GitHub repository. Build products, private commissioning, SDKs and diagnostic captures are excluded. The local EXE package requires installed NDI Runtime, Desktop Video and VC++ x64 runtime; it contains private commissioning and must not be uploaded as a public release.
