# Implementation and qualification plan

Milestones are gates, not claims of completion. A missing physical test does not prevent independent software work.

| Gate | Implementation | Required evidence |
|---|---|---|
| A | Read-only SDK inventory, NDI discovery/probe, explicit connector bars/tone | Exact stable IDs, profiles/modes, physical bars/audio, full-NDI and both HX3 codecs |
| B | One C++ scheduled output, rational audio, NDI framesync, source preparation/fallback, IPC | Physical SDI picture/audio, transitions, counters, timing |
| C | Four independently owned workers, reconnectable host, device ownership | Individual and four-channel isolation and capacity |
| D | Calibration/mixer/bounded buffers, external capture, clock adaptation | DVS and USB physical calibration, drift, host failure |
| E | WPF/API/Companion/Videohub coordinator, actual-state feedback | Companion 5.1.0, Stream Decks, emergency routes and conflicts |
| F | Portable package, reproducible scripts, operator/qualification docs | 24h development + 72h release soak, physical capture, license review |

Initial work uses installed SDK versions, preserving all hardware configuration. The media engine is native C++20; .NET hosts control and WPF only. Hardware qualification requires an operator and independent SDI monitoring. No automatic profile change or guessed crosspoint is permitted.
