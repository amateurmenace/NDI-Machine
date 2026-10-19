# Companion production router

## Desktop production routing (0.6.0)

The desktop uses this same coordinator and operation lock through a loopback API. **Edit output → Also route Videohub → ATEM destination → Apply** prepares the exact selected physical output before routing. **Route to ATEM** routes a healthy running output without changing its source/audio settings. **Return default** restores the selected ATEM destination; **Return ALL to default** restores all eight saved normal routes after review. Cloud replacements on affected inputs are unlinked; receiver processes and ATEM mixer/program/preview are unchanged by return actions.

The normal map remains `productionMap.normalInputs`, not a snapshot taken after a temporary route. At installation it matched current Videohub readback: ATEM1–8 / VH33–40 use inputs 9,10,11,12,18,17,33,34. The GUI displays these defaults. Upgrade only the existing NDI Machine connection to module 0.6.0; no button/layout import is needed.

## Stream Deck controls

The home NDI and CLOUD buttons open separate quick selectors. Each uses a large text heading and two full-width status lines for the actual feeds on ATEM inputs 3 and 4. Icons were removed at the operator’s request; source names, destinations and TAKE have priority. Choose a source, choose Input 3 or Input 4, then TAKE without leaving that page. NDI can select both destinations; each configured ATEM cloud source links to one input. Choosing or browsing never starts receivers or changes live routing.

More inputs opens the full eight-input selector with the current source. BACK returns to the same source category. Return to SDI stages the normal routes for all eight inputs; adjust the selected inputs if needed, then TAKE. This restores Videohub outputs 33–40 to inputs 9,10,11,12,18,17,33,34 and unlinks cloud sources on those ATEM inputs. Receivers stay available until Stop source is pressed in the app.

| Surface | Home NDI / Cloud / Return SDI | Quick NDI / Cloud | More inputs |
|---|---|---|---|
| Mk.2 | 1/1/3, 1/1/4, 1/2/0 | 37 / 38 | 39 |
| XL | 21/1/4, 21/1/5, 21/3/0 | 40 / 41 | 42 |

Coordinates are page/row/column, with one-based pages and zero-based grid cells. Page 43 is optional Mk.2 stream detail, reached from the health button. It also holds home-button templates below the physical three-row grid. The regular routing flow uses just the quick page. No NDI-to-cloud category switch is placed inside either directory.

NDI discovery is passive and updated periodically. The directory excludes only A Cam 1–3, SB Cam 1–4, AUD Cam 1–4, SC Cam 1–3 and E Cam 1–3, ignoring case, separators and SDK channel suffixes. The desktop picker remains unfiltered. Sources visible to the NDI runtime are listed dynamically; isolated networks/groups still require Discovery / IPs.

TAKE revalidates the selected name and transport URL. Concurrent background and TAKE discovery share one request. The module handles older-engine discovery-busy replies with bounded read-only retries. It never retries an uncertain mutation. A preparation request includes the physical DeckLink persistent ID even when the native settings snapshot omits it. Errors appear on TAKE and health, with details on the status display.

The allocator uses an explicitly commissioned physical device-to-Videohub-input map. It can reuse a healthy identical receiver, or retune an output only when no unselected Videohub destination consumes it. All 40 destinations are considered. It prepares and confirms media before changing selected crosspoints, then unlinks any cloud replacement on those ATEM inputs. Locks, ownership, stale/replaced workers, concurrent route changes and reserved OBS hardware remain protected. General NDI routes embed that source's NDI audio; they do not change the ATEM mixer profile.

Cloud discovery and linking use the installed official ATEM SDK, through `atem-remote.exe` and the control host. The configured Blackmagic cloud router sources are obtained from the switcher; no cloud server address is required. Linking replaces the physical connector's video/audio, and unlinking restores it. A source already linked elsewhere must be returned to SDI there before moving it. Neither route path changes ATEM program/preview selection or macros.

Live NDI health shows received-frame age, SDI drops/late counts and audio peak/age. Cloud shows connection and linked-input state. The SDK does not expose cloud bitrate or end-to-end latency here, so those readings are labelled unavailable. These are software metrics, not physical picture, sound or lip-sync qualification.

The failover buttons remain at 1/1/0 and 21/1/1. Their Down/Up actions call the coordinator in `companion/src/main.ts`; the video/undo sequence is in `coordinator.ts`, Zoom/ATEM audio in `audio.ts`, and production routing/cloud unlinking in `production.ts`. One visible Companion step calls that sequence. Network restores the emergency preset and enables ATEM audio Input 1 only; Fiber enables only XLR and MADI 1/2 + 3/4. Hold three seconds and release restores the captured pre-change state. Module restart clears session-only undo.

Installation uses single-page imports for owned pages 37–43, plus exactly six targeted home-cell copies. It never imports an existing home page, resets the full configuration, edits SQLite, or creates replacement connections. Page grid bounds must fit the existing global grid; imports may not expand it. Before/after secret-excluded exports must verify every unrelated button, page, connection, trigger, variable, image library and surface is unchanged. Private exports and runtime commissioning stay outside Git.
