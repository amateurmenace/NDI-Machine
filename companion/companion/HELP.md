# NDI Machine emergency coordinator

This module is built for Companion 5.1 with the official module-base 2.1 API and Node 26 runtime.

Enter the commissioned router IP, localhost media API URL and emergency plan JSON. Port numbers in the plan are physical (one-based); the protocol adapter converts to zero-based wire numbering.

Prepare Backups requests media preparation without routing. FAIL-OVER verifies exact fresh hardware sources, fixed 1080p29.97 output, Dante USB receive 1/2 audio, configuration revisions and operation identities; checks all planned crosspoints and locks; then takes and verifies each configured path. The operation is not atomic. Partial or unknown outcomes never trigger a global rollback. No command is replayed after reconnect. Return Normal refuses paths changed by an operator or since a connection loss.

The native production control API must be running and implement the documented readiness contract. Diagnostic executables do not satisfy readiness. If that API is unavailable, the button is BLOCKED and sends no route writes.

Feedback: red blocked/stale, amber ready, green ready plus backup crosspoints confirmed, blue working. Green does not mean independently verified SDI or ATEM tally. The signal variable remains UNKNOWN. Keep the existing Blackmagic Videohub module for independent manual recovery, including if this module or the media PC fails.

Do not use the button editor's Test control on a live show unless a deliberate hardware take is intended.
