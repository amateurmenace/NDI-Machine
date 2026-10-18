# Development status — 0.3.2

This is a working engineering build, not a physically qualified broadcast release. Historical evidence is in MEDIA-PROOF.md, CONTROL-PROOF.md and SDK-UPDATE.md; this page describes the current scope.

## Implemented and built

- Self-contained Windows x64 WPF `NDI Machine.exe`, packaged control host and independent native workers. Closing the GUI or control host leaves media running.
- Four source/output editors: automatic network discovery, exact NDI name and optional IP:port or vendor HX transport address, highest/lowest receive bandwidth, fit/fill/stretch SDR conversion to 1080p29.97, stable DeckLink connector selection with mode/ownership/reserved-card checks.
- Windows capture and playback-loopback audio, selected NDI audio or silence; independent stereo channels, gain, mute and 0-500 ms audio delay. Shared capture readers adapt to the SDI clock. No device-default changes.
- Previews reuse decoded output through local shared memory at 320x180 and at most 4 fps, without another NDI receiver. Input/SDI counters, receiver queues/drops, peak meters, clipping, buffer occupancy and clock correction are exposed.
- Manual external-IP/group discovery, source inventory, local diagnostics and persisted per-output intent. Existing custom workers survive host upgrades; explicit Apply/Prepare performs changes, missing workers recover with bounded backoff.
- Companion 0.3.2: short-release Fiber/Network toggle overwrites known unlocked routes; hold at least three seconds and release restores preceding routes, ATEM mix and known Zoom selection. Undo is session-only. Reconnect never writes or replays.
- Network invokes ZOOM DANTE and enables the explicitly commissioned ATEM input receiving the one audio-bearing path. Fiber invokes ZOOM MADI and enables only XLR + MADI 1/2 + 3/4. Gains, master and processing are unchanged. The module owns the sequence behind the button's Down/Up actions.

The commissioned preset now embeds Dante USB I/O Module receive 1/2 on **output 1 only**, with outputs **2-4 silent**. The stored network input map **6/7/8/5** to Videohub destinations **33/34/35/36** is disputed by the latest physical observation below; fiber is **9/10/11/12**. Director failover is **1/1/0**, XL **21/1/1**; Zoom DANTE **1/1/2**, MADI **1/1/1**.

## Validation

- Native CTest: **5/5** (read-only SDK inventory, media arithmetic/queues, audio bridge, previews and output settings/conversion/delay).
- Control/desktop simulated tests: **25/25**, including failed prepare, revision/epoch races, displayed-state confirmation, no replay after lost ACK, reserved/busy/duplicate hardware, silent readiness and preservation of custom outputs.
- Companion tests: **43/43**; package smoke import succeeds under the installed Node 26 permission model without connecting to hardware.
- Actual WPF visual-tree rendering verified dashboard and editor layout. This is an application render, not a physical output test.
- New keyed Dante USB capture path delivered 144960 frames in three seconds, nonzero samples and no timestamp/nonfinite errors. Multichannel and alternate-rate devices have not been qualified.
- All four upgraded workers are active with previews. Fresh snapshots show zero SDI late/dropped/fallback frames. Output 1 has nonzero USB samples; outputs 2-4 report silent/muted with zero peaks. Worker identities survived the subsequent GUI/control restart. Physical signal and lip-sync remain unqualified.
- Windows GPU counters showed VideoDecode activity for all four upgraded HX3 workers on NVIDIA RTX 5060 Ti. Conversion, scheduling, previews and external-audio resampling also use CPU work. No blanket codec or GPU guarantee is claimed.

## Activation state

After the operator stopped the previously blocked old host, all four workers were upgraded sequentially under the existing restart authorization. The installed 0.3.1 GUI/control and Companion module are active. PRIME opened the GUI/engine at 17:46:39 local, and the module confirmed backup preparation at 17:46:42. A separate operator toggle to Network was confirmed at 17:48:35. The operator confirmed they were testing the button. Codex did not press the live toggle. The launcher is registered at current-user sign-in and opens no app/media until explicitly primed. Companion shell permissions remain disabled.

Discovery fixes found 18 sources in the initial query and 19 as the network changed. The installed GUI reports 19; sources refresh automatically on connection and when opening an editor. SDK-provided vendor HX addresses are accepted. The existing four workers remain healthy. Regressions fixed repeated state reads attempting to reuse a parented JSON node and native `0.0` versus managed `0` causing false settings mismatches.

During operator testing, the audio-bearing AUD CAM 1 feed appeared on ATEM Input 3 despite the saved plan expecting Input 1. The saved physical map is therefore not yet requalified. Version 0.3.2 is built in staging: it carries explicit `atemInput` through commissioning and derives Network audio from the single fixed-audio path. Tests cover Input 3, exact undo, Fiber restoration and rejection of ambiguous mapping. Deployment of this mapping change awaits the operator's four-camera observations; do not claim the current physical/audio map is correct. The previous network map 6/7/8/5 is historical pending correction.

Companion's installed Down/Up actions and four feedbacks were restored. Full-layout reset/imports were logged at 16:42:22, 16:50:56, 16:52:18 and 17:14:04 local on 2026-10-18. The latest import retained the repaired bindings. The log does not identify the initiating client. A secret-excluded backup is saved privately; verify actions after future imports.

## Remaining qualification and scope

Physical SDI picture/audio, independent connector observations, calibrated ATEM levels/lip-sync, separate full-NDI and HX3 H.264/H.265 qualification, real 24/72-hour soaks, crash/device/profile-change recovery and actual failover/undo listening tests remain Not Run or Unknown. Counters, thumbnails and ACKs do not prove downstream signal.

ASIO, mixing multiple audio sources together, per-application process capture, configurable video delay, HDR conversion, additional SDI standards, advanced fallback choices, authenticated LAN control and an installer remain outside this build. General Windows endpoints are exposed through WASAPI shared mode. Audio delay is available but lip-sync is not automatically calibrated.

Source is intended for the user-authorized GitHub repository. Build products, private commissioning, SDKs and diagnostic captures are excluded. The local EXE package requires installed NDI Runtime, Desktop Video and VC++ x64 runtime; it contains private commissioning and must not be uploaded as a public release.
