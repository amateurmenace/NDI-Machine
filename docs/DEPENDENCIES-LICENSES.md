# Dependency and license record

Versions tested on 18 October 2026 are pinned in `config/dependencies.lock.json` and CMake. Local SDKs and binaries are excluded from source control. This repository contains newly authored project code; generated DeckLink interface files are build outputs. No vendor sample implementation is redistributed.

| Dependency | Selected version | Local prerequisite / distribution boundary |
|---|---|---|
| DeckLink SDK | 16.0 | Obtain from the [official developer page](https://www.blackmagicdesign.com/developer/products/capture-and-playback/sdk-and-software); extract into `.deps/Blackmagic DeckLink SDK 16.0` or override CMake `DECKLINK_SDK`. Installed EULA and header notices reviewed. SDK/sample redistribution has conditions; no SDK files are included here. |
| Desktop Video driver/runtime | 16.3.0.0, API 16.3 | Already installed; driver/firmware updates are separate maintenance operations. |
| NDI SDK/runtime | 6.3.1.0 | User-installed standard SDK. Local seven-page license reviewed. Distribution is limited to specifically designated SDK files and subject to its terms; no DLLs or Tools are redistributed here. |
| nlohmann/json | 3.12.0 | MIT; fetched from the versioned official release with SHA-256 pinning. Its MIT notice is copied into the desktop package. |
| ASIO / Dante Virtual Soundcard | Adapter not implemented; DVS 4.5.2.3 observed | ASIO SDK licensing review and capture adapter remain pending. DVS installation/activation and Dante Controller subscriptions are prerequisites, not supplied by this project. |
| .NET | SDK 8.0.301; packaged runtime 8.0.32 | Self-contained WPF and ASP.NET Core executables include runtime licenses and third-party notices. |
| Companion | 5.1.0; custom ndi-machine module 0.3.0 | MIT module source; packaged dependency notices included by the module build. |

NDI® is a registered trademark of Vizrt NDI AB. Information and SDK/runtime access: [ndi.video](https://ndi.video/). The requested `NDI Machine` directory/repository name is the working project name. Before releasing a product under that name, follow the vendor's [product naming and trademark requirements](https://docs.ndi.video/all/developing-with-ndi/sdk/licensing), including contacting the NDI team about product-name use. No contact or permission is claimed here.

Project-authored source is MIT licensed in the root LICENSE. Public source availability is not production qualification, a codec license, or permission to redistribute an entire SDK. The local EXE package excludes proprietary SDKs/runtimes. Public binary distribution, codec obligations, selected SDK terms at distribution time, and final product attribution remain release checks. Current online NDI documentation describes newer SDK terms; the installed 6.3.1 license is retained locally and must not be replaced by assumptions about that newer version.
