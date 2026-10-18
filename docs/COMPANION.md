# Fail-over toggle commissioning

Companion 5.1.0 uses custom module `ndi-machine` 0.2.1, labelled `ndi_machine`. At the user's request, existing button **1/1/1** is a **FAIL-OVER toggle (Fiber / Network)**. It has one `toggle` press action, no release action, and no blind two-step cycling. Text shows `$(ndi_machine:status)` and `$(ndi_machine:next_action)`. The action headline is **Toggle video + ZOOM + ATEM audio**.

Each press obtains fresh Videohub route readback:

| Actual protected routes | Next press |
|---|---|
| All fiber inputs 9/10/11/12 | Verify exact backup video/audio and committed revisions, then take network inputs 8/6/7/5. |
| All network inputs 8/6/7/5 | Return to fiber 9/10/11/12, even when the media API is offline. |
| Mixed, unrelated, unavailable or locked | Block the toggle and explain the conflict. |

Destinations are physical Videohub outputs 33/34/35/36, feeding ATEM 1/2/3/4. Network media uses the exact commissioned DeckLink IDs, AUD CAM 1-4, 1080p29.97 and fixed Dante USB receive 1/2. Machine-specific identities remain in ignored local configuration.

Direction comes from the router, including after module restart. No routing occurs on startup/reconnect. Both directions preflight all routes/locks, reject intervening changes or connection-epoch changes, and verify each crosspoint; ACK is insufficient. Overlapping presses are blocked during an operation. Partial/unknown results never trigger global rollback or advance a stored toggle step.

After all four video routes confirm, the same operation applies the requested audio direction:

| Direction | Existing Companion button pressed | ATEM inputs ON | All other input audio |
|---|---|---|---|
| Network | ZOOM DANTE, 1/1/3 | Input 1 | OFF |
| Fiber | ZOOM MADI, 1/1/2 | XLR, MADI 1/2, MADI 3/4 | OFF |

The existing Zoom buttons control both the X32 channels and Zoom Mac audio devices. They are invoked through Companion's [documented HTTP button API](https://companion.free/user-guide/v5.1/remote-control/http-remote-control/), once per successful direction change, without retries. Preserve their positions and the `x32`/`zoomswitch` connection labels. HTTP success proves dispatch only; the existing Zoom feedback remains the independent device-state indication.

ATEM control is commissioned to the Television Studio HD8 ISO (model 27). Read-only discovery found 29 input strips: SDI 1-8, XLR, RCA, mic, 16 MADI pairs and two media players. Network enables input ID 1; Fiber enables 1001, 1501 and 1502. Every other discovered source is Off, including split mono sources. Only mix options change; master output, gains, processing and channel configuration remain unchanged. The full resulting profile must match ATEM readback. `audio_profile` reports NETWORK/FIBER/MIXED/UNKNOWN independently of video routes.

ATEM topology and the X32/Zoom connection status are checked before video writes. A blocked or partial video operation does not dispatch Zoom or change ATEM audio. Failure after video confirmation reports PARTIAL/UNKNOWN without rollback; a concurrent press remains blocked until audio finishes. Reconnect never applies a profile. A mid-operation audio change stops further writes. At installation the video routes read FIBER and the ATEM mix read MIXED; setup preserved both. This audio sequence has not been live-pressed by Codex.

Standalone Take All remains available for deliberate recovery from a mix of commissioned normal/backup paths. Standalone Return Normal retains its stricter unchanged-take ownership ledger. The new toggle explicitly authorizes inversion of the freshly observed complete group. Keep the existing Blackmagic Videohub connection for independent manual recovery.

Colors: red blocked/stale/unresolved, amber FIBER with network take ready, green NETWORK with fiber return available, blue working. NETWORK/green confirms router crosspoints and does not require backup engines to remain healthy, because fiber return must still work if the media PC fails. `engine_ready` reports media health separately. Physical signal and ATEM tally remain UNKNOWN.

At configuration time all destinations already selected network inputs, so the button shows **NETWORK / RETURN FIBER**. It was configured without a live press or route write. Physical take/return remains Not Run. Secret-excluded export: `reports/private/NDI-Machine-toggle-20261018.companionconfig`; screenshot: `companion-failover-toggle.jpg`. These private artifacts are excluded from Git.

The button was found reset to the original unprogrammed template again at the start of this change. The operator reports no imports currently running. Companion's local log records earlier full imports with buttons set to `reset-and-import` at 14:59:20 and 15:05:08 America/New_York on 2026-10-18, consistent with those resets. The initiating client is not identified, and no later full import appeared through 15:35. Read-only SQLite inspection confirmed the commissioned toggle, four feedbacks and notes are saved; database `quick_check` passed. Preserve this button and verify action/feedback bindings after upgrades or imports. The separate XL-page template at 21/1/1 remains unprogrammed; this commissioning applies to Director button 1/1/1.

## Build and verification

Use `scripts/Build-Companion.ps1`; dependencies are pinned. Runtime is Companion's Node 26.11.1 and packaging uses its Node 22. The official packager inventories dependency licenses. Only the custom module is MIT; SDK redistribution has separate terms.

31 tests passed: the existing 20 routing tests plus complete audio profiles, missing/unsupported sources, split mono inputs, Zoom dispatch selection/failure, no replay, intervening audio changes, preflight before video, audio only after complete video, partial failure and the busy lock spanning audio. TypeScript compilation, official packaging/license inventory, packaged import under Node 26 permissions and read-only packaged ATEM connection passed. Physical listening and live audio-switch qualification remain Not Run. Secret-excluded final backup: `reports/private/NDI-Machine-audio-toggle-20261018.companionconfig`; screenshot: `companion-failover-audio.jpg`.

Build packaging keeps `atem-connection` 3.10.3 external, with pinned Yarn 4.18.1 for the official packager. `scripts/patch-atem.mjs` verifies the pinned dependency and applies two local fixes to both development and packaged copies: stop late UDP callbacks during socket close, and lazily load the unused font renderer. The latter avoids unnecessary native-addon permissions. `Build-Companion.ps1` finishes with a hardware-free smoke check of the actual artifact. Do not install the superseded 0.2.0 package, whose flattened dependency bundle cannot initialize.

References: [Videohub protocol](https://documents.blackmagicdesign.com/DeveloperManuals/VideohubEthernetProtocol.pdf), [official Companion template](https://github.com/bitfocus/companion-module-template-ts).
