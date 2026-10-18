# NDI Machine emergency coordinator

Built for Companion 5.1, module-base 2.1 and Node 26. Configure the commissioned router IP, localhost media API and emergency plan. Ports are physical one-based; the adapter converts once to wire numbering.

FAIL-OVER TOGGLE reads actual routes on every press. All fiber means take verified network backups; all network means return to commissioned fiber inputs. Mixed or unrelated routes block. Failed commands never advance a stored step. Startup/reconnect sends no route writes, and overlapping presses are blocked during an operation.

Network take verifies exact fresh hardware sources, 1080p29.97, Dante USB receive 1/2 and committed revisions through the real media API. Fiber return remains available if that API is offline. Both directions preflight locks and paths, reject intervening changes/reconnects, and verify individual crosspoints. Partial/unknown outcomes never cause global rollback or automatic replay. Standalone Return Normal retains its stricter unchanged-take ledger. Prepare Backups starts media without routing.

Feedback: red blocked/stale/unresolved, amber fiber with network take ready, green network with fiber return available, blue working. Variables show route mode, next toggle action and separate engine health. Green does not prove healthy backup media, physical SDI or ATEM tally. Signal remains UNKNOWN. Keep the existing Blackmagic Videohub module for independent manual recovery.

Do not use the editor's Test button during a live show unless a deliberate hardware take is intended. Old layout imports may overwrite the button; verify its action and feedbacks afterward.