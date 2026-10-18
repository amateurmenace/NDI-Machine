# NDI Machine emergency coordinator

Version 0.2.1, built for Companion 5.1, module-base 2.1 and Node 26. Configure the commissioned router IP, localhost media API, emergency plan, ATEM HD8 ISO IP and localhost Companion API. Ports are physical one-based; the adapter converts once to wire numbering. Existing ZOOM buttons are page/row/column 1/1/3 (DANTE) and 1/1/2 (MADI). Preserve these locations and the x32/zoomswitch connection labels.

FAIL-OVER TOGGLE reads actual routes on every press. All fiber means take verified network backups; all network means return to commissioned fiber inputs. Mixed or unrelated routes block. Failed commands never advance a stored step. Startup/reconnect sends no route writes, and overlapping presses are blocked during an operation.

Network take verifies exact fresh hardware sources, 1080p29.97, Dante USB receive 1/2 and committed revisions through the real media API. Fiber return remains available if that API is offline. Both directions preflight locks and paths, reject intervening changes/reconnects, and verify individual crosspoints. Partial/unknown outcomes never cause global rollback or automatic replay. Standalone Return Normal retains its stricter unchanged-take ledger. Prepare Backups starts media without routing.

Feedback: red blocked/stale/unresolved, amber fiber with network take ready, green network with fiber return available, blue working. Variables show route mode, next toggle action and separate engine health. Green does not prove healthy backup media, physical SDI or ATEM tally. Signal remains UNKNOWN. Keep the existing Blackmagic Videohub module for independent manual recovery.

After all four video routes are confirmed, Network presses the existing ZOOM DANTE button and sets only ATEM Input 1 audio ON. Fiber presses ZOOM MADI and sets only XLR, MADI 1/2 and MADI 3/4 ON. All other input audio sources are OFF, including media players, RCA, mic and remaining MADI pairs. Master output, gains, processing and channel configuration are unchanged. ATEM readback must match the full profile. Zoom HTTP success confirms button dispatch, not completion of the Mac/X32 operations; use their existing feedback for independent confirmation.

ATEM audio topology and both Zoom-related connections are checked before video changes. Failed video switching does not trigger audio. A failure after video confirmation reports PARTIAL/UNKNOWN without rollback or replay. The busy lock spans video and audio. Audio changes occur only on explicit actions, never at startup or reconnect. The audio_profile variable reports ATEM state separately from video routes. Live audio writes and physical listening tests remain required for commissioning.

The pinned MIT atem-connection 3.10.3 dependency stays external to preserve runtime transport paths and receives reproducible patches during build (also applied to the packaged copy). The unused native font renderer is loaded lazily, avoiding unnecessary native-addon permissions. The socket-close patch is used to detach late UDP message callbacks before closing. This prevents a pending-ACK shutdown crash observed during read-only inventory.

Do not use the editor's Test button during a live show unless a deliberate hardware take is intended. Old layout imports may overwrite the button; verify its action and feedbacks afterward.
