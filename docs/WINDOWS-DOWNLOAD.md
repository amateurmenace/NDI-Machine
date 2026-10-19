# Windows EXE download

Download **NDI-Machine-0.5.3-win-x64.zip** from the [v0.5.3 release](https://github.com/amateurmenace/NDI-Machine/releases/tag/v0.5.3), extract the whole ZIP to a writable folder, then open **NDI Machine.exe**. Keep the `engine`, `launcher`, `build/Release`, `config` and `notices` folders beside the EXE. This is a portable x64 Windows package, not an installer. The executables are unsigned.

The package contains the tested **control 0.5.3** failover fix, **desktop/launcher behavior 0.5.2**, and the owned native helpers. The desktop still displays v0.5.2 because the hotfix changed only the control host. .NET 8.0.32 is included; a separate .NET installation is not needed. `BUILD-INFO.json` identifies the application source commit and executable hashes. Verify the ZIP against the release's `SHA256SUMS.txt`.

## First run

1. Install the official x64 **NDI 6 Runtime**, **Blackmagic Desktop Video** and **Microsoft Visual C++ x64 Redistributable**. ATEM cloud integration also requires **Blackmagic ATEM Switchers**. These vendor runtimes are not bundled. The engine expects the NDI runtime at `C:\Program Files\NDI\NDI 6 Runtime\v6`, or available through Windows PATH.
2. For a new workstation, copy `config/commissioning.example.json` to `config/local.commissioning.json` and replace every placeholder with commissioned hardware identities, sources, audio endpoint and router mapping. The example is deliberately not a usable hardware configuration. See the [control and commissioning documentation](https://github.com/amateurmenace/NDI-Machine/blob/main/docs/CONTROL-API.md). Opening the EXE without commissioning can display the GUI, but cannot start a usable local engine.
3. Open **NDI Machine.exe**. Use **Start control host** if the engine is offline. In **Production**, use **Edit output** and **Apply** to start a selected stream. Opening the GUI or starting the host does not automatically start receivers. **Stop source** releases an output; closing the GUI leaves existing background streams running.
4. Companion is optional for ordinary NDI-to-DeckLink output. For failover and Videohub/ATEM routing, install the separate `ndi-machine-0.5.2.tgz` module through Companion's Modules page and commission the connection using the [Companion instructions](https://github.com/amateurmenace/NDI-Machine/blob/main/docs/COMPANION.md). No Companion layout or device settings are imported by this ZIP.

To enable Companion's ability to open the app, start `launcher/NDIMachine.Launcher.exe` from the extracted package. It remains in the background and opens the GUI/control host only on explicit PRIME. This portable download does not register Windows startup automatically.

For an existing commissioned installation, preserve its private `config/local.*` files and arrange an application update separately. Extracting this ZIP does not update an already-running host or launcher. Do not run two installations against the same hardware. The public package includes no saved routes, audio endpoint IDs, credentials, armed intent or workstation configuration.

## Dependencies and notices

- NDI information/runtime: [ndi.video](https://ndi.video/).
- Blackmagic software: [Blackmagic support](https://www.blackmagicdesign.com/support/).
- Microsoft runtime: [Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist).
- Project code is MIT licensed; see `LICENSE`. Included open-source dependency notices are in `notices`. Separately installed vendor software remains subject to its own terms. This package grants no vendor SDK or codec license.

NDI® is a registered trademark of Vizrt NDI AB. This independent engineering project is not sponsored by or affiliated with NDI or Blackmagic Design. See `DEPENDENCIES-LICENSES.md` for the dependency record.

This engineering prerelease includes software tests and an operator-confirmed failover retry. Physical audio calibration/lip-sync, the full codec matrix, cloud TAKE and sustained soak qualification remain open.
