# Local control host

The current API binds only `127.0.0.1:9860`. It does not expose media over HTTP and does not write Videohub routes. Native media deadlines remain in independent C++ processes. The Companion coordinator performs explicit router operations after checking this API.

Build with x64 .NET SDK 8.0.301 using `scripts/Build.ps1` (omit `-NativeOnly`), then `scripts/Build-Desktop.ps1`. Start with the GUI's **Start control host** or `scripts/Start-Control.ps1`, which prefers the packaged host/root; `-Development` selects the source build. Create a commissioned `config/local.commissioning.json` using the example schema and actual stable identities. Run `scripts/Prepare-Backups.ps1` to deliberately restore and start the commissioned SDI backups; this is not a read-only command.

Preparation saves an armed intent atomically in ignored `config/local.supervisor.json` with an LKG copy. Subsequent control-host starts restore intent only if the commissioning fingerprint matches. The host first queries named pipes and checks ownership before launching anything. Missing owners are retried with bounded backoff. An unresponsive but still-owned worker is not force-killed. Existing media survives control host and Companion exit. The optional launcher has a current-user sign-in entry; it does not start the engine or media until an explicit PRIME request.

| Endpoint | Behavior |
|---|---|
| `GET /api/v1/state?nonce=<unique value>` | Fresh bounded named-pipe queries; echoes nonce and returns host instance ID, actual committed worker state and physical signal Unknown. A missing/busy pipe returns unavailable, never cached healthy. |
| `GET /api/v1/commission` | The exact locally commissioned emergency plan. |
| `GET /api/v1/sources` | NDI finder results, cached at most five seconds; no video subscription. |
| `GET /api/v1/inventory` | Read-only DeckLink persistent identities, supported mode/busy flags, and active Windows capture/render endpoints. |
| `GET /api/v1/discovery` / `PUT /api/v1/discovery` | Read/save discovery settings: `extraIps` comma-separated string (maximum 32 addresses) and `groups` string. Response schema is 1. Saving invalidates finder cache. |
| `PUT /api/v1/outputs/<slot>/settings` | Prepare and apply source, receiver, hardware and audio settings using expected host/worker identity and revision. UUID `Idempotency-Key` required. |
| `PUT /api/v1/outputs/<slot>/route` | Compatibility source-only update with the same identity/revision guards; preserves desired audio/hardware settings. |
| `POST /api/v1/emergency/prepare` | JSON `{schemaVersion:1,plan:<exact plan>}` and UUID `Idempotency-Key`. Returns HTTP 202 and operation ID. Restores commissioned settings one output at a time, including audio only on output 1. Upgrades legacy workers when necessary. No Videohub routing. |
| `GET /api/v1/operations/<id>` | Accepted, preparing, committed, failed or unknown result for the current host instance. A 202 response alone is not readiness. |
| `WS /api/v1/events` | Complete current state snapshots, approximately twice per second; at most four clients, bounded send time. Reconnect receives a new full state. |

Native pipes use a four-byte little-endian length followed by UTF-8 JSON, maximum 64 KiB. Requests contain `schemaVersion:1`, `id` and `method`. Responses echo schema and ID; the client sends a one-byte receipt before closing. Pipes reject remote clients and permit the current user and SYSTEM. Output pipes are `NDIMachine.output.1.v1` through `.4.v1`; shared capture uses `NDIMachine.audio.<audioKey>.v1`. The key is a SHA-256 identity for the endpoint and selected channel pair. The older default capture pipe `NDIMachine.audio.v1` remains compatible.

Worker methods are state, prepare (`settings`, or legacy `source`), commit (`preparedOperationId`, `expectedRevision`), and stop. Preparation failure preserves current settings. A commit is reported in state only after a frame from that receiver actually completes. Successful mutations retain a bounded request cache. HTTP operation cache is bounded to 128 commands and local to one host instance; clients must resynchronize on instance changes and never replay an unknown command.

Settings requests include `schemaVersion:1`, `slot`, `expectedInstanceId`, `expectedWorkerInstanceId`, `expectedRevision` and `settings`. Read actual state immediately before editing. Settings fields are `device` (persistent hex identity), `source`, `sourceUrl` (optional IP:port or SDK-discovered `NDI-HX-<vendor>://IP`), `bandwidth` (`highest`/`lowest`), `sizing` (`fit`/`fill`/`stretch`), `audioMode` (`fixed`/`ndi`/`silent`), `audioEndpoint`, `left`, `right`, `gainDb`, `muted` and `delayMs`. Bounds and behavior are documented in `DESKTOP.md`. The server derives `audioKey`; clients do not choose capture process identities.

The manager validates active endpoints and target hardware, primes capture, prepares NDI, then rechecks worker identity/revision. Same-device settings commit at a frame boundary. A connector change or explicit legacy-worker upgrade stops only that slot, waits for ownership release and launches its replacement. No forced takeover occurs. Once a write is sent, failure is reported as unknown and never automatically replayed. Background recovery preserves existing custom workers and starts only missing owners from confirmed saved intent.

Host and Origin validation prevent unrelated browser origins from issuing local commands. This is a local same-user control boundary, not remote authentication. Authenticated LAN control is not yet implemented.

## Local launcher

`NDIMachine.Launcher.exe` independently binds `127.0.0.1:9861`. `GET /api/v1/state?nonce=<value>` echoes a bounded nonce and reports whether the exact packaged GUI and control executables are running. `POST /api/v1/prime` launches only these two fixed executables if absent; it accepts no command or executable path. Starts are serialized and rate-limited. Host/Origin checks match the loopback boundary, and shell execution is disabled. It does not prepare outputs or route video/audio. Companion separately waits for connection, sends one emergency-prepare request and waits for confirmation, with no automatic mutation replay. `scripts/Register-Launcher.ps1` installs the optional current-user sign-in entry. Companion shell/restricted-module permissions do not need to be enabled.

Output remains 1080p29.97 UYVY. Supported decoded UYVY/BGRA/BGRX input sizes and rates are converted; WASAPI capture/loopback or selected NDI audio feeds SDI stereo 1/2. Black/stale-audio fallback and no automatic profile changes remain invariants. Any reported late/dropped SDI frame blocks readiness for that worker's current lifetime. Physical signal, lip-sync, broader codec variants, device-loss recovery and sustained operation remain unqualified.

For maintenance shutdown, first stop this project's control host, set its saved armed intent to false in the active package root, then send `stop` to the exact project-owned output pipes and capture pipes using `scripts/Worker-Ipc.ps1`. Obtain capture keys from actual worker settings before stopping workers. Stopping a worker while the armed supervisor is running requests recovery, so it can restart that worker. Do not stop unrelated DeckLink, OBS or Companion processes.

References: [Microsoft named-pipe APIs](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeclientstream.connectasync?view=net-8.0), [ASP.NET Core WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-8.0).
