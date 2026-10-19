# Development status — 0.6.0

## Desktop production routing — current change

Desktop and Companion0.6.0 add an opt-in editor checkbox to prepare the selected NDI output and route its physical Videohub input to ATEM1–8, a Route to ATEM action for a running source, individual Return default controls and a main Return ALL to default action. Saved normal routes matched current readback before the update: 9,10,11,12,18,17,33,34 to VH33–40. Defaults remain in the existing Companion configuration. Returns unlink cloud replacements and keep receivers running; mixer/program/preview are unchanged.

Managed checks **40/40**, Companion checks **68/68** and the packaged Node26 module smoke check pass. New coverage exercises exact physical slot routing, preparation failure, lock/consumer/revision/worker/router changes, individual/all defaults, stale client state and idempotent commands without replay. Native sources are unchanged from the prior isolated build that passed **5/5**. Actual WPF dashboard/editor rendering was inspected with the live read-only routing API.

Companion0.6.0 is installed on the existing connection. Secret-excluded configuration exports confirm that only its moduleVersionId changed; all buttons and other settings are identical. No live route, media, ATEM mixer or failover command was issued for this update. All eight default routes remained in place and receivers were idle during GUI verification. The release contains desktop0.6.0, control0.5.3, unchanged launcher behavior0.5.2 and Companion0.6.0. Physical routing/return qualification for this new GUI remains unperformed.

The GUI0.6.0 EXE was activated in the existing commissioned package and the A-drive copy updated. The previous GUI window closed gracefully; engine, launcher and audio processes stayed running. Engine instance, output identities and all eight current routes were verified unchanged. The public v0.6.0 package contains22 verified files including eight x64 executables and the matching Companion module, with example commissioning only. Every ZIP entry hash matches its source. Application source commit: 21e6464cedd90b739924e5072dda9e9f9720c557.

Published prerelease [v0.6.0](https://github.com/amateurmenace/NDI-Machine/releases/tag/v0.6.0) is tagged at4b08c292ec5c47a6b646f1bb3eb4899c139f15ee. Windows ZIP, Companion module and checksum assets all match local byte counts and SHA256 digests. Source is pushed to main. The sections below record earlier milestones; current version and test counts above supersede their historical values.

This engineering build is installed on the commissioned workstation. Source delivery excludes proprietary SDKs, binaries, private commissioning and diagnostic captures. Historical media evidence is in MEDIA-PROOF.md and CONTROL-PROOF.md. Software checks do not establish physical broadcast qualification.

## Installed control hotfix — 0.5.3

An operator failover attempt exposed a numeric-type bug in group preparation when any receiver is idle: local revision zero was Int32, while the expected-revision guard reads Int64. Preparation failed before media writes. The corrected value is Int64; a regression reproduced the original exception and now passes both all-idle and mixed-running preflight. Control/desktop checks pass **37/37**. Native and Companion code are unchanged.

The operator stopped the old host, and `scripts/Install-Control-053.ps1` installed the tested control-only 0.5.3 build into the existing package. The running AG-2 worker retained its identity and revision; slots2–4 remained idle and all eight inputs retained normal SDI status. Only the NDI Machine Companion connection was refreshed to clear the previous failed outcome. No live failover press or router/audio write was made. GUI, launcher and Companion remain 0.5.2. The operator subsequently retried successfully on 2026-10-19. Read-only monitoring observed WORKING → NETWORK, all four AUD CAM workers healthy with zero SDI late/dropped frames, Zoom DANTE and the Network audio profile targeting Input1. The operator confirmed “ok it worked.” Current physical mappings matched the commissioned plan. Logs are in `reports/private/failover-watch-live.jsonl`; calibrated listening/lip-sync and long soak qualification remain open. Activation evidence is retained privately in `engine-hotfix-053-proof.json` and `control-053-20261018-235739/after.json`.

## Current behavior

- Self-contained Windows x64 WPF EXE, local control host and independent native media workers. Closing the GUI preserves running streams. Cold host startup starts no receivers; Apply or PRIME explicitly starts them. Stop source and Engine controls release receivers and support an idle engine restart.
- ATEM Input 1–4 dashboard cards, ordered by the commissioned connector map, with the actual NDI Machine slot/Videohub input underneath and read-only live ATEM destinations. Commands and previews retain physical slot identity.
- Per-output NDI source/DeckLink selection; Windows capture or playback-loopback audio, NDI audio or silence; stereo channel selection, gain/mute/audio delay; receiver settings, manual IP/group discovery and software statistics. SDI output remains 1080p29.97 UYVY.
- Previews reuse already decoded media through local shared memory, 320x180 at up to 4 fps, with no extra NDI subscription. Windows GPU counters previously observed HX3 VideoDecode activity on RTX 5060 Ti; conversion, scheduling, previews and external-audio resampling also use CPU work. This is not a guarantee for all codecs/senders.
- Companion quick NDI and Cloud selectors: source → Input 3/4 → TAKE on one page, More inputs for all eight. Text-only home buttons have large labels and stacked live status. Return SDI stages the normal routes for review before TAKE. GUI Return SDI uses the same coordinator after its confirmation dialog.
- NDI discovery is shared between simultaneous readers. Source changes use the discovered transport URL and retain persistent device identity. Idle slots return promptly, preventing their pipe timeouts from making an active peer's snapshot appear stale.
- Official ATEM SDK cloud source discovery/link/unlink, including fresh topology expectations and no replay after uncertain writes. Cloud bitrate/latency are labelled unavailable where the SDK does not expose them. Neither production routing path changes ATEM program/preview or macros.
- Companion failover restores the emergency preset over production assignments, then verifies and routes. PRIME opens/connects/prepares before a separate toggle. Hold three seconds then release restores the session's captured previous state. Unknown ownership, locks and genuine readiness failures remain guarded. No automatic takeover on reconnect.

## Commissioned mapping

| Physical slot | Device suffix | Videohub input | Preferred ATEM input |
|---|---|---|---|
| 1 | c0 | 8 | 3 |
| 2 | c1 | 6 | 1 |
| 3 | c2 | 7 | 2 |
| 4 | c3 | 5 | 4 |

Persistent device prefix is 0xe6a968. Network retains VH inputs 6,7,8,5 to outputs 33–36. The latest operator instruction enables **ATEM Input 1 audio only**, superseding the earlier Input 3 choice. The emergency preset embeds Dante USB I/O Module receive 1/2 on **physical slot 2 / VH6**; the other three are silent. Fiber enables only XLR and MADI 1/2 + 3/4. Gains/master/processing remain unchanged.

Normal ATEM1–8 / VH33–40 routes are 9,10,11,12,18,17,33,34. Failover stays at 1/1/0 and 21/1/1. Zoom DANTE stays at 1/1/2, MADI at 1/1/1. Production NDI audio follows its selected source and does not change the ATEM mixer profile.

## Validation and deployment

- Native CTest **5/5**, desktop/control **36/36**, Companion **59/59**; packaged module smoke check passes under Node26 permissions without hardware connections.
- GUI/control/launcher 0.5.2 installed in `dist/NDI Machine-0.5.2`; desktop shortcut and launcher registration updated. `A:\NDI Machine.exe` matches the installed 0.5.2.0 EXE, SHA256 `0B2F90B5B4AD0DF597CE817164E05B7141736BE844C21E0BE50D6608F838D1EC`. The copied EXE connects through the fixed installed package; it does not replace the package's dependencies.
- Activation preserved the existing media worker. Fresh snapshots with idle peers completed in under 1 ms after the timeout correction. GUI accessibility confirms v0.5.2, ATEM1–4 order and the matching physical output labels.
- Read-only Panasonic AG-2 receive checks delivered 237 video frames in eight seconds, with audio and zero reported receiver drops. The operator subsequently confirmed “AG-2 is on input 3” after stopping old receivers and retrying. Later missing frames were explicitly attributed by the operator to changing/stopping the sender. The old-worker preparation timeout's exact cause is not proven; a complete successful TAKE after the final freshness correction remains to be operator-tested.
- Companion 0.5.2 installed in the existing connection. Final icon-free home and quick pages visually inspected in the admin UI. Secret-excluded before/after exports confirm only owned pages37–43, six authorized home cells and the ndi_machine module version changed. All other buttons, connections, variables, triggers, surfaces, image library and global grid are identical. Evidence is kept privately in `router-final-preservation-proof.json`, `NDI-Text-final.companionconfig` and the final UI screenshots.
- No live TAKE, emergency toggle, router or ATEM audio write was performed for the final label/layout update. Operator testing and intentional sender changes were preserved.

## Remaining qualification

Physical audio and calibrated lip-sync, the broader full-NDI/HX3 H.264/H.265 matrix, cloud TAKE, connector-cycle takeover, actual undo listening and real 24/72-hour soaks remain Not Run or Unknown. The AG-2 picture confirmation is an operator observation for that route, not complete signal qualification. SDI counters and ACKs do not prove downstream audio/video.

ASIO, multi-source mixing, per-application process capture, configurable video delay, HDR conversion, additional SDI standards and authenticated LAN control remain outside this build. Audio delay can be adjusted manually; lip-sync is not automatically calibrated. Installed NDI Runtime, Desktop Video and VC++ x64 runtime are prerequisites. The commissioned package is private and must not be uploaded as a public release.
