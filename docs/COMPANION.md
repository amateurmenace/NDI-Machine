# Emergency button commissioning

The live Companion 5.1.0 instance has module `ndi-machine` 0.1.2, labelled `ndi_machine`. Button **1/1/1** is the existing **FAIL-OVER (Fiber to Network)** button. Its single press action calls the emergency coordinator; no fixed-delay route macro is used. Four feedbacks reflect readiness, confirmed backup routes, blocked/stale state and work in progress. A separate text element displays `$(ndi_machine:status)`.

The control API at `http://127.0.0.1:9860` now polls real persistent worker state. Four exact sources and fixed USB stereo audio report healthy. The button displays **BACKUP** because all protected destinations already selected the backup inputs before this work. It automatically shows BLOCKED if media or router readiness cannot be established. The button was configured but never pressed or tested on live routing. SDK diagnostics do not satisfy readiness.

The commissioned take maps physical inputs 8/6/7/5 to Videohub outputs 33/34/35/36. These feed ATEM inputs 1/2/3/4. Return Normal is a separate module action using sources 9/10/11/12. All media uses the exact commissioned DeckLink IDs and AUD CAM names, 1080p29.97, fixed Dante USB I/O receive channels 1/2. Device and endpoint IDs are in ignored local configuration, not public presets.

The coordinator checks fresh media identity, format, audio, committed operation/revision, all current routes and locks. It verifies each crosspoint from reported state; ACK is insufficient. Group operations may partially complete. It never rolls back the whole matrix, replays a take after reconnect, or returns automatically. Return Normal refuses paths with intervening changes or a changed router connection epoch. Keep the existing Blackmagic Videohub connection for independent manual recovery.

Colors: red blocked/stale or unresolved partial/unknown outcome, amber ready to take, green ready with all backup crosspoints confirmed, blue working. Readiness includes current locks and allowed routes as well as media. Partial/unknown results retain their explanation until another deliberate operation. Crosspoint confirmation does not prove physical SDI or ATEM tally; the signal variable remains UNKNOWN.

An export without connection secrets is saved in `reports/private/NDI-Machine-failover-final-20261018.companionconfig`, with a screenshot of the configured action. Re-importing an old generated Companion layout may overwrite this button. The export contains other local configuration and is excluded from Git.

## Build and verification

Use `scripts/Build-Companion.ps1`. Dependencies are pinned in package-lock.json. Runtime uses Companion's bundled Node 26.11.1; packaging tools run under bundled Node 22 to satisfy their engine range. The package tool includes third-party license inventory. Only the custom module is MIT licensed; SDK redistribution remains a separate review.

13 tests cover fragmented TCP blocks, wire numbering, ACK without readback, disconnected media, locks, exact source/audio readiness, revisions, partial take, operator changes, return ownership, readiness feedback conditions and connection loss. The real adapter read current protected routes and unlocked states without sending route writes. Real worker/API readiness and control-host restart passed; physical take/return, remote authenticated control and complete media action/preset coverage remain Not Run or unfinished.

Protocol source: [Blackmagic Videohub Ethernet Protocol](https://documents.blackmagicdesign.com/DeveloperManuals/VideohubEthernetProtocol.pdf). API-generation reference: [official Companion TypeScript template](https://github.com/bitfocus/companion-module-template-ts).
