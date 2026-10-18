# Fail-over toggle commissioning

Companion 5.1.0 uses custom module `ndi-machine` 0.1.4, labelled `ndi_machine`. At the user's request, existing button **1/1/1** is a **FAIL-OVER toggle (Fiber / Network)**. It has one `toggle` press action, no release action, and no blind two-step cycling. Text shows `$(ndi_machine:status)` and `$(ndi_machine:next_action)`.

Each press obtains fresh Videohub route readback:

| Actual protected routes | Next press |
|---|---|
| All fiber inputs 9/10/11/12 | Verify exact backup video/audio and committed revisions, then take network inputs 8/6/7/5. |
| All network inputs 8/6/7/5 | Return to fiber 9/10/11/12, even when the media API is offline. |
| Mixed, unrelated, unavailable or locked | Block the toggle and explain the conflict. |

Destinations are physical Videohub outputs 33/34/35/36, feeding ATEM 1/2/3/4. Network media uses the exact commissioned DeckLink IDs, AUD CAM 1-4, 1080p29.97 and fixed Dante USB receive 1/2. Machine-specific identities remain in ignored local configuration.

Direction comes from the router, including after module restart. No routing occurs on startup/reconnect. Both directions preflight all routes/locks, reject intervening changes or connection-epoch changes, and verify each crosspoint; ACK is insufficient. Overlapping presses are blocked during an operation. Partial/unknown results never trigger global rollback or advance a stored toggle step.

Standalone Take All remains available for deliberate recovery from a mix of commissioned normal/backup paths. Standalone Return Normal retains its stricter unchanged-take ownership ledger. The new toggle explicitly authorizes inversion of the freshly observed complete group. Keep the existing Blackmagic Videohub connection for independent manual recovery.

Colors: red blocked/stale/unresolved, amber FIBER with network take ready, green NETWORK with fiber return available, blue working. NETWORK/green confirms router crosspoints and does not require backup engines to remain healthy, because fiber return must still work if the media PC fails. `engine_ready` reports media health separately. Physical signal and ATEM tally remain UNKNOWN.

At configuration time all destinations already selected network inputs, so the button shows **NETWORK / RETURN FIBER**. It was configured without a live press or route write. Physical take/return remains Not Run. Secret-excluded export: `reports/private/NDI-Machine-toggle-20261018.companionconfig`; screenshot: `companion-failover-toggle.jpg`. These private artifacts are excluded from Git.

The button was found reset to the original unprogrammed template again at the start of this change. The responsible import source is not established. Old generated layout imports can overwrite it; preserve this button and verify action/feedback bindings after upgrades or imports.

## Build and verification

Use `scripts/Build-Companion.ps1`; dependencies are pinned. Runtime is Companion's Node 26.11.1 and packaging uses its Node 22. The official packager inventories dependency licenses. Only the custom module is MIT; SDK redistribution has separate terms.

20 tests passed: protocol fragmentation/readback, exact media readiness, revisions, partial results, operator changes, return ownership, actual-route toggling, startup on network, media-offline fiber return, failed-take preservation, mixed/locked/stale routes, concurrent presses and reconnect during return. TypeScript compilation and packaging passed. Physical take/return, remote authenticated control and full media action/layout coverage remain Not Run or unfinished.

References: [Videohub protocol](https://documents.blackmagicdesign.com/DeveloperManuals/VideohubEthernetProtocol.pdf), [official Companion template](https://github.com/bitfocus/companion-module-template-ts).