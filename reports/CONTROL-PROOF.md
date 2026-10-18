# Persistent media and Companion evidence, 2026-10-18

Status: software integration and short hardware scheduling checks passed. Physical downstream signal remains **Unknown**. No Videohub route write or live emergency button press was performed.

- Four simultaneous 20-second SDI tests with a single Dante USB capture host: outputs completed 598/598/599/598 frames, zero late/dropped/fallback frames, zero shared-audio overruns/underruns/stale blocks, and nonzero stereo samples on every output. Capture reported one startup discontinuity and no timestamp/nonfinite errors. Short startup resampler correction is not a long-term hardware drift measurement.
- Persistent worker source preparation: an unavailable candidate failed after ten seconds while the existing source completed another 300 frames. AUD CAM 1 changed to AUD CAM 2, then back to AUD CAM 1. Committed revisions were 1, 2, 3. Repeating the same commit request returned the same operation. No late/dropped/fallback frames were reported during this test.
- Control host started the exact commissioned audio endpoint and four stable DeckLink device identities. An initial startup race (IPC available before first completed SDI frame) caused a prepare error while media still started correctly. Fixed the host to wait for the first committed revision instead of preparing a duplicate receiver during preroll. A subsequent prepare completed successfully.
- Restarted and rebuilt only the .NET control host. All four native worker instance IDs remained unchanged, frame counters advanced, and source revisions stayed at 1. The restarted host reattached without duplicate ownership or media shutdown.
- Live HTTP/WebSocket checks passed: fresh nonce echoed; repeat prepare ID returned the same operation; modified uncommissioned plan rejected; cross-origin and foreign Host requests rejected; missing nonce rejected; WebSocket returned a complete current state; worker IDs/revisions unchanged after validation tests.
- Native CTest: 3/3. Companion: 13/13 protocol/coordinator tests. TypeScript compilation and official module packaging/license inventory passed. .NET 8.0.301 x64 Release build: zero warnings/errors.
- Companion 5.1.0 module 0.1.2 installed. Existing FAIL-OVER button 1/1/1 invokes one guarded take_all action. It shows BACKUP because destinations 33/34/35/36 already read inputs 8/6/7/5. All four destination locks read U. Return Normal is a separate action, using sources 9/10/11/12 and refusing unowned or interveningly changed paths.

Raw evidence and Companion exports are machine-specific and excluded from Git under `reports/private/`. Relevant captures: `four-outputs-20261018-144126`, `worker-switch-20261018-144905.json`, `supervisor-restart.json`, `control-api-validation.json`, and `companion-failover-configured.jpg`.

Still Not Run: independently observed SDI picture/audio and lip-sync, physical take/return, hardware fault injection, alternate codecs, and real-time 24h/72h soaks. Source video, software readiness, and router crosspoint readback do not establish the physical cable or signal at the ATEM.

## Toggle update

User-requested actual-route toggle added in module 0.1.4 and installed on button 1/1/1. 20 tests passed, including repeated network/fiber transitions, startup on network without take ownership, media-offline return, failed take, mixed/locked/stale routes, concurrent presses and connection changes. UI shows NETWORK / RETURN FIBER. No live route writes or physical toggle test performed.
