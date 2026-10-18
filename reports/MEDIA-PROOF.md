# Media proof - 18 October 2026

Hardware/SDK snapshot: three SDK-reported DeckLink 8K Pro groups, Desktop Video 16.3, SDK 16.0, NDI SDK/runtime 6.3.1.0. The selected output group was already in the four-subdevice half-duplex profile. No profiles or Videohub crosspoints were changed. The operator identified the four NDI Machine outputs and confirmed ATEM 1080p29.97.

| Test | Result | Evidence boundary |
|---|---|---|
| Read-only DeckLink inventory | Passed | 12 subdevices across three SDK groups; four target outputs support 1080p29.97 UYVY |
| Rational timing and media-core unit tests | Passed | Includes 72h-equivalent arithmetic; not a real-time soak |
| AUD CAM 1 receiver probe, 12 seconds | Passed for decoded video | 318 progressive frames after startup, zero receiver-reported video drops; no audio packets |
| AUD CAM 1 scheduled SDI, 10 seconds | Passed at scheduling layer | 298 completions, zero late/dropped/fallback; 2 input repeats |
| Four simultaneous camera-to-SDI diagnostics, 15 seconds each | Passed at scheduling layer | See table below |
| Physical picture/cable/connector validation at ATEM | Not Run | No independent observation recorded |
| Physical embedded audio calibration and lip-sync | Not Run | Audio packets alone do not prove physical levels, mapping or synchronization |
| H.264 and H.265 HX3 qualification | Not Run | Operator described cameras as HX3; SDK codec/acceleration metadata did not establish variants |
| Independent full-bandwidth NDI qualification | Not Run | No independently identified full-bandwidth source tested yet |
| Bars/tone output | Implemented; Not Run | Diagnostic supports labelled bars and calibrated test tones |
| 24h / 72h real-time soaks | Not Run | Short diagnostics are not sustained-load qualification |

| Output | Source | Scheduled completions | Late | Dropped | Fallback | Repeated input timestamps | Received audio packets |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 | AUD CAM 1 | 448 | 0 | 0 | 0 | 2 | 0 |
| 2 | AUD CAM 2 | 448 | 0 | 0 | 0 | 1 | 723 |
| 3 | AUD CAM 3 | 448 | 0 | 0 | 0 | 3 | 0 |
| 4 | AUD CAM 4 | 448 | 0 | 0 | 0 | 1 | 0 |

Each process prepared a source, enabled its explicit stable output identity, ran at hardware completion pacing, and stopped after its declared duration. The runs overlap but are not frame-synchronous starts. Stop-time buffers were bounded at four video frames and 4,925-6,361 audio sample frames in the four-output test. No conclusion about 24h memory, handles, drift, or recovery can be drawn from this short run.

The live Videohub identified itself as Blackmagic Videohub 40x40 12G, protocol 2.8. Its existing labelled backup inputs already route to ATEM inputs 1-4. Normal-return sources have not been commissioned. Only read-only protocol inspection was performed.

Raw device identities, network endpoints, matrix state and JSON run results are in ignored `reports/private/` on the development machine. This public report intentionally contains the engineering results without publishing that private snapshot.
