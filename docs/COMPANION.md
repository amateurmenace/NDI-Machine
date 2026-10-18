# Failover controls in Companion

The custom **ndi-machine 0.3.2** module owns the entire emergency sequence. The connection label is `ndi_machine`. The Director button is currently **1/1/0** and the mirrored XL button is **21/1/1** (page/row/column). Both use **one Companion step**:

| Button event | Module action |
|---|---|
| Press | `toggle_down` — start hold timing; no routing |
| Release before 3 seconds | `toggle_up` — run the toggle |
| Release after holding 3–60 seconds | `toggle_up` — undo the last change |

The release action measures the hold; separate Companion duration groups and a blind two-step counter are not used. A lost release or stale hold never triggers a timer-driven write. The module source implementing the sequence is `companion/src/coordinator.ts`, Zoom/ATEM audio is in `audio.ts`, and button timing is in `hold.ts`. The button's Step 1 tab therefore shows only one press action and one release action.

## Prime the app

When the GUI or engine is stopped, the button shows **PRIME / CLICK TO PRIME**. Its first press/release opens the packaged app and engine, waits for connection, restores the commissioned backup media, then stops. It sends no Videohub, ATEM-mix or Zoom commands. A separate subsequent press runs the displayed Fiber/Network action. A press begun in PRIME stays a prime request even if connection completes while it is held; holding PRIME does not undo or toggle.

The independent fixed launcher on localhost port 9861 must be running. `scripts/Register-Launcher.ps1` starts it and registers current-user sign-in startup. Only this package's GUI/engine can be launched; Companion shell/restricted-module access remains disabled. The launcher never starts media by itself. Failed startup/preparation reports the failure without automatically repeating mutations. Priming restores emergency defaults, so save any desired production choices before using it. Ordinary GUI use preserves custom choices.

## Toggle and revert

Fresh complete network route readback means the next short press returns to fiber. Every other known, unlocked arrangement takes network, overwriting the four existing destination routes. Unknown ports, locks, stale state, unreachable ATEM/Zoom dependencies or unready network media still block. The button never changes Videohub locks.

| Direction | Videohub inputs → outputs 33/34/35/36 | Existing Zoom button | ATEM input audio ON |
|---|---|---|---|
| Network | Commissioned backup path for each destination | ZOOM DANTE, **1/1/2** | The explicit `atemInput` of the single `audioMode: fixed` path |
| Fiber | 9/10/11/12 | ZOOM MADI, **1/1/1** | XLR + MADI 1/2 + 3/4 |

All other ATEM input audio is Off. Master, gains, processing and channel splits are untouched. The media failover preset embeds Dante USB receive 1/2 **only on SDI output 1**; outputs 2–4 are silent. Version 0.3.2 derives the enabled ATEM input from that path's explicit `atemInput`, rather than assuming slot number equals ATEM channel. The plan must declare exactly one fixed-audio path and mark other paths silent; missing/ambiguous audio mapping is rejected. The control host exports `atemInput` from local commissioning. The plan's per-path `audioMode` must match fresh committed worker state. Verify the physical DeckLink → Videohub input and Videohub destination → ATEM input cabling; software readback cannot detect mispatched cables. `network_audio_input` exposes the selected ATEM channel.

Before the first write, the module saves the exact previous four crosspoints, full ATEM input mix options (including AFV) and known Zoom selection. **Hold three seconds then release** restores that snapshot, rather than assuming the old state was Fiber. The snapshot also remains available after a partial change. It is memory-only and is cleared by module restart/configuration changes. No snapshot means no undo. Zoom restoration invokes the previous canonical Zoom button; it does not restore unrelated arbitrary X32 state.

Both directions preflight hardware, verify every video route, then invoke the existing Zoom button and apply/verify the ATEM mix. HTTP success confirms Zoom dispatch, not downstream completion; retain its existing device feedback. Changes during the operation abort further writes. Partial/unknown results never automatically roll back or replay. Returning to fiber remains available when backup media is offline; taking network always requires ready media, including from mixed routes.

Feedbacks: `ready`, `active`, `blocked`, `working` (blue for PRIME/PRIMING as well as active work). Variables include `status`, `reason`, `route_mode`, `next_action`, `button_text`, `audio_profile`, `engine_ready` and `undo_available`. Green NETWORK is crosspoint confirmation, not proof of physical video/audio. Keep independent router controls available.

## Persistence and layout imports

Companion full imports with `buttons: reset-and-import` have repeatedly replaced the button IDs and bindings. On 2026-10-18 the log recorded imports at 16:42:22, 16:50:56, 16:52:18 and 17:14:04 America/New_York, in addition to earlier imports. The log does not identify the initiating client. The 17:14 import preserved the new down/up bindings and four feedbacks on both surfaces; earlier imports did not.

When exporting/generating layouts, preserve the module connection ID, both action bindings, four feedbacks and the Zoom locations. Verify them after each import. Avoid a full import to repair an individual button. Use the module's FAIL-OVER preset or the live button editor. Private secret-excluded exports and UI proof belong under ignored `reports/private/`.

## Build and verification

Run `scripts/Build-Companion.ps1`. Dependencies are pinned; Companion uses Node 26 and the official packaging tool uses Node 22. The package keeps `atem-connection` external and applies the documented pinned socket/font-loading patches. Packaging includes license inventory and a hardware-free import smoke check.

Routing/audio/hold tests cover overwrite, exact undo, busy protection, partial outcomes, lost acknowledgements, locks, stale state, intervening changes and audio sequencing. Real module installation and read-only readiness were checked. Automated tests do not establish physical listening, lip-sync or broadcast qualification; Codex has not pressed the live failover button during this update.

References: [Companion HTTP API](https://companion.free/user-guide/v5.1/remote-control/http-remote-control/), [Videohub protocol](https://documents.blackmagicdesign.com/DeveloperManuals/VideohubEthernetProtocol.pdf).
