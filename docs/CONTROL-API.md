# Local control host

The current API binds only `127.0.0.1:9860`. It does not expose media over HTTP and does not write Videohub routes. Native media deadlines remain in independent C++ processes. The Companion coordinator performs explicit router operations after checking this API.

Build with x64 .NET SDK 8.0.301 using `scripts/Build.ps1` (omit `-NativeOnly`). Start with `scripts/Start-Control.ps1`. Create a commissioned `config/local.commissioning.json` using the example schema and actual stable identities. Run `scripts/Prepare-Backups.ps1` to deliberately start SDI backups; this is not a read-only command.

Preparation saves an armed intent atomically in ignored `config/local.supervisor.json` with an LKG copy. Subsequent control-host starts restore intent only if the commissioning fingerprint matches. The host first queries named pipes and checks ownership before launching anything. Missing owners are retried with bounded backoff. An unresponsive but still-owned worker is not force-killed. Existing media survives control host and Companion exit. No Windows auto-start task or service has been installed.

| Endpoint | Behavior |
|---|---|
| `GET /api/v1/state?nonce=<unique value>` | Fresh bounded named-pipe queries; echoes nonce and returns host instance ID, actual committed worker state and physical signal Unknown. A missing/busy pipe returns unavailable, never cached healthy. |
| `GET /api/v1/commission` | The exact locally commissioned emergency plan. |
| `POST /api/v1/emergency/prepare` | JSON `{schemaVersion:1,plan:<exact plan>}` and UUID `Idempotency-Key`. Returns HTTP 202 and operation ID. Starts/reattaches workers, or prepares then commits sources. No Videohub routing. |
| `GET /api/v1/operations/<id>` | Accepted, committed or failed result for the current host instance. A 202 response alone is not readiness. |
| `WS /api/v1/events` | Complete current state snapshots, approximately twice per second; at most four clients, bounded send time. Reconnect receives a new full state. |

Native pipes use a four-byte little-endian length followed by UTF-8 JSON, maximum 64 KiB. Requests contain `schemaVersion:1`, `id` and `method`. Responses echo schema and ID; the client sends a one-byte receipt before closing. Pipes reject remote clients and permit the current user and SYSTEM. Output pipes are `NDIMachine.output.1.v1` through `.4.v1`; shared capture uses `NDIMachine.audio.v1`.

Worker methods are state, prepare (source), commit (preparedOperationId, expectedRevision), and stop. Preparation failure preserves the current source. A commit is reported in state only after a frame from that receiver actually completes. Successful mutations retain a bounded request cache. HTTP operation cache is also bounded and local to one host instance; clients must resynchronize on instance changes and never replay stale commands.

Host and Origin validation prevent unrelated browser origins from issuing local commands. This is a local same-user control boundary, not remote authentication. Authenticated LAN control is not yet implemented.

Current recovery/operation limits: matching progressive 1080p29.97 UYVY only; exact fixed WASAPI stereo 1/2; black/stale-audio fallback; no automatic profile changes. Any reported late/dropped SDI frame blocks readiness for that worker's current lifetime. Broader mixer, GUI and qualified unattended-recovery controls remain unfinished.

For maintenance shutdown, first stop this project's control host, set its saved armed intent to false, then send `stop` to the exact project-owned output pipes and audio pipe using `scripts/Worker-Ipc.ps1`. Stopping a worker while the armed supervisor is running requests recovery, so it can restart that worker. Do not stop unrelated DeckLink, OBS or Companion processes.

References: [Microsoft named-pipe APIs](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeclientstream.connectasync?view=net-8.0), [ASP.NET Core WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-8.0).
