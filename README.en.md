# MediaDesk

MediaDesk is a Windows application for organizing local photos and videos. Browse a media queue, preview files, edit metadata, and search directories to find and organize your material. Optional AI integration assists with names, remarks, and intelligent search.

[简体中文](README.md)

## Download and install

The current version is **v0.1.1**. Published builds are available on the repository's [Releases](https://github.com/skyflyH/MediaDesk-AgentSearch/releases) page. Local debug packages are generated in `dist/` and may not have been uploaded to Releases. End users do not need to clone the source or install development tools.

1. Install Microsoft Edge WebView2 Runtime and the .NET 10 Desktop Runtime x64.
2. Download `MediaDesk-<version>-win-x64.zip` from the release Assets.
3. Extract the entire archive to a local directory and double-click **`MediaDesk.exe`**. No CMD or PowerShell launcher is required. Do not run it inside the archive or move the EXE away from its bundled files.
4. Before updating, quit the old version from the tray and extract the new version into a new directory. Default user data and model settings are stored outside the application directory.

Download the matching `.zip.sha256` file and compare it with `Get-FileHash <archive-path> -Algorithm SHA256` if desired. A matching hash verifies file integrity, not Windows publisher signing.

## Features

- Import files or directories into a tree-based queue with list, medium-thumbnail, large-thumbnail, and media-type views.
- Preview photos, RAW images, and videos. Video support depends on installed codecs and optional FFmpeg.
- Edit names, remarks, and authors; automatically save drafts; process batches and save copies.
- Store RAW remarks in companion XMP files. Metadata write support varies by format, and the UI reports the outcome.
- Search names, directories, remarks, dates, and media properties; open directories or locate source items in File Explorer.
- Configure OpenAI-compatible endpoints for naming, remark suggestions, and natural-language intelligent search. Save multiple providers and models and check their connections.
- Use the tray, global quick search, configurable shortcuts, login startup, themes, fonts, and backgrounds. A single click on the tray icon opens the main window.
- Standard text fields support cut, copy, and paste shortcuts. Shortcut recording fields retain their key capture behavior.

## Supported file formats

Import and directory indexing currently recognize the following extensions, case-insensitively.

| Type | Formats and extensions |
| --- | --- |
| JPEG | `.jpg`, `.jpeg` |
| PNG | `.png` |
| HEIF / HEIC | `.heif`, `.heic` |
| WebP | `.webp` |
| GIF | `.gif` |
| BMP | `.bmp` |
| TIFF | `.tif`, `.tiff` |
| Video containers | MP4 `.mp4`, QuickTime `.mov`, M4V `.m4v`, WebM `.webm`, Matroska `.mkv`, AVI `.avi` |

RAW extensions are listed by manufacturer below. This is the application's supported extension list, rather than the full list supported by LibRaw.

| Manufacturer / standard | RAW extensions |
| --- | --- |
| Canon | `.cr2`, `.cr3` |
| Nikon | `.nef`, `.nrw` |
| Sony | `.arw` |
| Fujifilm | `.raf` |
| Panasonic | `.rw2` |
| Olympus / OM SYSTEM | `.orf` |
| Pentax | `.pef` |
| Adobe Digital Negative, including DNG files from some Leica, Ricoh, mobile, and other devices | `.dng` |

**Preview and metadata capabilities:**

- RAW previews use LibRaw to extract embedded previews or perform half-size decoding when necessary. Compatibility depends on the camera model, RAW compression, and file integrity. Recognizing an extension does not mean every camera model has been tested.
- HEIC / HEIF, TIFF, and some other image previews depend on Windows image decoders. Some systems require additional image extensions. GIF thumbnails and full previews may behave differently.
- Video extensions identify containers, not guaranteed playback support for every codec inside them. Direct playback depends on WebView2 / system decoding support. Optional FFmpeg can generate compatible preview proxies when direct playback fails, keeping the source video unchanged.
- Reading and writing remarks, authors, and capture properties depends on the format, Windows property handlers, and optional ExifTool. RAW remarks support companion `.xmp` files; these are auxiliary files, not independently imported photos or videos. The UI reports the actual write outcome.

## Search and intelligent search

Standard search finds file names, remarks, and directory clues. Intelligent search interprets natural language and combines names, remarks, authors, directories, and readable metadata. Filters include media type, format, creation or capture dates, capture time of day, file size, shutter speed, aperture, ISO, focal length, exposure compensation, resolution, video duration, frame rate, and bitrate. Missing metadata is not treated as satisfying a condition.

The agent uses actual candidates to interpret relationships between keywords and expand closely related terms. For example, when asked to find vehicles photographed by a named person, it can first search that person's name in real names and remarks, then combine those clues with trains, cars, or motorcycles instead of assuming that “vehicle” means only a car.

Results are grouped: **best matches** appear first with a shared explanation; **partial keyword matches** appear below with matched clues and missing or unconfirmed conditions. Individual cards no longer repeat match explanations. Unchecked candidates are labeled separately. Invalid final output receives at most one format correction without repeating local searches, and every returned item must still pass provenance checks against this search's local results.

If an initial index is still loading and no results are found, the UI suggests waiting for indexing to finish before searching again. Saving source names or remarks from the organizer synchronizes their search index without restarting the app. Automatically saved editing drafts are separate from changes written to source files.

Intelligent search primarily uses text and metadata. It does not automatically upload local images to an online multimodal model. Optional local photo semantic search requires a separately deployed Chinese-CLIP model; default packages do not include its weights. Video scene recognition is not provided.

## Architecture

Directory search persists its index and metadata snapshots in SQLite. Existing indexes are searchable while background synchronization runs. Adding a directory or upgrading to persistent snapshots requires an initial background validation.

Where NTFS USN journal access is available, persisted checkpoints replay changes across restarts. Unsupported filesystems, unavailable journals, or restricted permissions fall back to directory watching with a background snapshot comparison at startup. Unchanged files reuse metadata. Journal gaps and event-buffer overflows trigger reconciliation; offline directories retain their index. The app neither elevates itself nor creates or modifies the system journal.

A single synchronization queue coalesces events and bounds pending paths. Overlapping roots share monitoring; removing a root releases its watcher. Idle operation does not repeatedly traverse directories. Subsequent queries see synchronized changes, and ordinary searches refresh once after initial synchronization completes.

Thumbnail work is queued for the current view. Switching views or moving items out of view cancels pending work that is no longer needed and prioritizes the active preview. Work already decoding through system APIs may need to finish before it can be released.

| Component | Implementation |
| --- | --- |
| UI | Vue 3, TypeScript, Vite |
| WebView2 edition | .NET 10 WinForms host, system WebView2, Node.js business bridge |
| Local service | C# / .NET 10, communicating over JSON Lines |
| Storage and media | SQLite / FTS5, LibRaw, Windows imaging and property APIs, ONNX Runtime |
| AI integration | LangChain and OpenAI-compatible endpoints |

## Requirements

Development and builds require Windows x64, Node.js 22 or later (22 LTS recommended), npm, and the .NET 10 SDK. Initial setup needs access to npm and NuGet.

Running the application requires Microsoft Edge WebView2 Runtime and the .NET 10 Desktop Runtime x64. Building also requires the WebView2 SDK files described below.

Optional components:

- FFmpeg for compatible preview proxies: set `APM_FFMPEG` to the executable path or add it to `PATH`.
- ExifTool for embedded metadata support in some formats: set `APM_EXIFTOOL` or add it to `PATH`.
- Chinese-CLIP ONNX model for photo semantic search: set `APM_PHOTO_MODEL_DIR` to the model directory. Model weights are not included in the repository.

## Development

Run from the project root:

```powershell
$env:ELECTRON_SKIP_BINARY_DOWNLOAD = "1"
npm ci
npm run dev
```

`npm run dev` builds the service, Vue UI, and native host, then starts the WebView2 application. Prepare the WebView2 SDK first. This command does not provide hot reload; quit and rerun it after changes.

Use `npm run build:renderer` to build only the UI. Electron-related development dependencies and configuration remain temporarily for shared business API types and historical compatibility tests; the environment variable above skips the Electron runtime download. The release contains neither Electron nor a separate Chromium distribution.

## Checks and tests

```powershell
npm run typecheck
npm run test:desktop
npm run test:service
```

Native dialogs, tray interactions, global focus, and File Explorer integration require validation in an interactive Windows desktop session. Unit tests do not replace these checks.

If your environment cannot start the default testhost, specify the installed dotnet host, for example:

```powershell
dotnet test tests/MediaLibrary.Service.Tests/MediaLibrary.Service.Tests.csproj -- RunConfiguration.DotNetHostPath="C:\Program Files\dotnet\dotnet.exe"
```

Some service tests skip when external fixtures are absent: `APM_TEST_PHOTO_MODEL` points to an exported model and parity data, `APM_TEST_SECOND_VOLUME` to a writable test directory on another volume, and `APM_TEST_FORMATS` to media format fixtures.

## Build and package

The native host builds against external WebView2 SDK files. Obtain the Microsoft WebView2 SDK (currently `1.0.2535.41`) and place these files in `artifacts/webview-sdk/`:

```text
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.WinForms.dll
WebView2Loader.dll                 # x64
```

Extract them from the matching Microsoft.Web.WebView2 NuGet package. SDK binaries and large model files are not committed. The native project also accepts an MSBuild `WebViewSdkDirectory` property for another SDK location.

Install 7-Zip at `C:\Program Files\7-Zip\7z.exe`, or set `SEVENZIP_PATH`, then run:

```powershell
npm run build
npm run package:win
```

`npm run build` builds the service, UI, and native host and assembles a runnable directory at `artifacts/webview-app/`. `npm run package:win` rebuilds and creates the WebView2 ZIP; `npm run package:webview` is an alias:

- `dist/MediaDesk-0.1.1-win-x64.zip`
- `dist/MediaDesk-0.1.1-win-x64.zip.sha256`

The version in the filename comes from `package.json`. Archives are checked for integrity. Packaging uses an explicit file allowlist and excludes personal databases, model keys, diagnostic profiles, and PDB files.

Fully extract the WebView2 ZIP, then double-click `MediaDesk.exe`. Release packages exclude CMD / PS1 launchers, and login startup also uses the EXE directly. Other bundled files remain required. The host prefers a working Node.js 22+ installation on `PATH`, then tries the bundled runtime; `APM_WEBVIEW_NODE` can specify an absolute runtime path.

## Data and model configuration

The database, service caches, and model settings default to `%LOCALAPPDATA%/ai-photos-manager`. Model API keys use Windows DPAPI encryption for the current user. UI preferences use the WebView2 profile. Closing the main window normally hides it to the tray; use the application or tray exit action to quit completely.

Configure the endpoint, model name, API key, and data scopes in application settings. Standard organization and filtering can work offline. Enabled AI features may send content within the selected scopes to the configured provider. No online model account or key is supplied.

`scripts/export-photo-model.py` exports a pinned Chinese-CLIP checkpoint to ONNX and verifies numerical parity. It requires a separate Python environment with PyTorch, Transformers, ONNX, ONNX Runtime, NumPy, and Pillow, plus the local checkpoint specified by the script. It is not required for normal application startup.

## Project structure

```text
apps/webview/  Windows desktop host and browser/Node.js bridges
apps/desktop/  Shared UI, business logic, IPC types, and regression tests
src/           .NET local service
tests/         Service tests and fixtures
resources/     Icons and branding
scripts/       Build, startup, packaging, and model export tools
```

## Feedback

Bug reports and suggestions are welcome through GitHub Issues. Include your MediaDesk version, Windows version, reproduction steps, and error messages. For media compatibility problems, include the file format. Remove API keys, personal paths, and other sensitive information before sharing logs.

## Compatibility

- Version `0.1.1` supports Windows x64 and is under active development.
- Media decoding and metadata writing depend on the file format, system codecs, and optional tools. Not all formats support the same metadata operations.
- Photo semantic search requires a separately configured local model. Online AI features require a working model endpoint.
- Release packages need only the runtime prerequisites. Building from source requires the development dependencies listed above.

## License

The project is currently marked `UNLICENSED`; no open-source license has been granted. Third-party components and models remain subject to their respective licenses.
