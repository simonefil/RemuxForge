# ![icon](icons/icon-48.png) RemuxForge

RemuxForge is a cross-platform MKV utility for technical MKV workflows:

- **Remux**: import audio tracks and subtitles from another MKV release, with optional speed correction, frame-sync or Deep Analysis for edited releases.
- **Split**: cut HEVC/AVC MKV files into frame-perfect segments while preserving VFR timing, chapters, audio and subtitles.
- **Metadata**: batch-edit MKV container, track metadata and managed Matroska tags through reusable rule presets, with separate analysis and apply steps.
- **Bulk Rename**: preview and apply batch filename changes from the Metadata WebUI, using stacked rename methods.

It ships as a scriptable CLI, a WebUI for local browsers or headless servers, and a native desktop shell for Windows and macOS. The browser and desktop editions use the same Blazor UI and processing engine; the [wiki](https://github.com/simonefil/RemuxForge/wiki) documents them in full.

## Requirements

- [MKVToolNix](https://mkvtoolnix.download/) (`mkvmerge`, `mkvextract`, `mkvpropedit`)
- [ffmpeg](https://ffmpeg.org/) (`ffmpeg`, `ffprobe`)
- [MediaInfo CLI](https://mediaarea.net/en/MediaInfo) (`mediainfo`)
- UTF-8 locale on Linux

The optional Vulkan SIFT backend for Deep Analysis requires a Vulkan 1.2 loader and a compatible compute device with timeline semaphore support. CPU SIFT is the default and does not require Vulkan. On macOS, Vulkan is provided through MoltenVK.

Tool paths are auto-detected or configurable from the WebUI under **Settings > Tool paths**. On Windows and Linux, ffmpeg and ffprobe are downloaded automatically when missing; on macOS they must be installed manually.

Supported targets:

| Platform | Architectures |
|----------|---------------|
| Windows | x64 |
| Linux | x64, ARM64 |
| macOS | x64, ARM64 |
| Docker | x64 |

## Quick start

Download the desktop installer, CLI or WebUI archive for your platform from the [releases page](https://github.com/simonefil/RemuxForge/releases).

Desktop:

- On Windows, run the installer and launch RemuxForge from the Start menu.
- On macOS, open the DMG and copy RemuxForge to Applications.

The desktop shell starts the local WebUI automatically. Its settings and runtime data are stored in the operating system's application-data directory, outside the installed application bundle.

WebUI:

```bash
# Windows
RemuxForge.Web.exe

# Linux/macOS
chmod +x RemuxForge.Web
./RemuxForge.Web
```

Open `http://localhost:5000`. The port can be changed with `REMUXFORGE_PORT` or `--port <number>`.

Docker:

```bash
docker run -d \
  --name remuxforge \
  -p 5000:5000 \
  -e REMUXFORGE_DATA_DIR=/data \
  -v /path/to/config:/data:rw \
  -v /path/to/media:/media:rw \
  draknodd/remuxforge:latest
```

Paths typed in the WebUI are resolved inside the container. See [Docker](https://github.com/simonefil/RemuxForge/wiki/Docker) for Compose, permissions and GPU acceleration, and [Installation](https://github.com/simonefil/RemuxForge/wiki/Installation) for the data directory and environment variables.

## CLI

```bash
RemuxForge.Cli --mode remux|split|metadata [options]
RemuxForge.Cli --help
```

Import Italian audio into an English release, correcting a constant offset:

```bash
RemuxForge.Cli --mode remux -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -d "D:\Output" -fs
```

Cut a disc into episodes by chapter groups:

```bash
RemuxForge.Cli --mode split --source "disc1.mkv" --pattern "5,5,5,6" --output-dir "D:\Output"
```

Apply a metadata preset to a library:

```bash
RemuxForge.Cli --mode metadata --source "D:\Series" --preset "D:\Presets\anime-audio-titles.json" --output-dir "D:\Output" --recursive
```

Every flag, its accepted values and the validation rules are in the [CLI Reference](https://github.com/simonefil/RemuxForge/wiki/CLI-Reference). Preset authoring, bulk rename and manual metadata edit are WebUI-only.

## Documentation

The [wiki](https://github.com/simonefil/RemuxForge/wiki) is written for the WebUI.

| | |
|---|---|
| Install and run | [Installation](https://github.com/simonefil/RemuxForge/wiki/Installation) · [Docker](https://github.com/simonefil/RemuxForge/wiki/Docker) · [WebUI Basics](https://github.com/simonefil/RemuxForge/wiki/WebUI-Basics) |
| Remux | [Mode](https://github.com/simonefil/RemuxForge/wiki/Remux-Mode) · [Synchronization](https://github.com/simonefil/RemuxForge/wiki/Remux-Synchronization) · [Audio and Video](https://github.com/simonefil/RemuxForge/wiki/Remux-Audio-and-Video) |
| Split | [Mode](https://github.com/simonefil/RemuxForge/wiki/Split-Mode) |
| Metadata | [Mode](https://github.com/simonefil/RemuxForge/wiki/Metadata-Mode) · [Presets](https://github.com/simonefil/RemuxForge/wiki/Metadata-Presets) · [Bulk Rename](https://github.com/simonefil/RemuxForge/wiki/Bulk-Rename) |
| Worked examples | [Remux](https://github.com/simonefil/RemuxForge/wiki/Examples-Remux) · [Split](https://github.com/simonefil/RemuxForge/wiki/Examples-Split) · [Metadata](https://github.com/simonefil/RemuxForge/wiki/Examples-Metadata) |
| Reference | [CLI](https://github.com/simonefil/RemuxForge/wiki/CLI-Reference) · [Settings](https://github.com/simonefil/RemuxForge/wiki/Settings-Reference) · [Metadata fields](https://github.com/simonefil/RemuxForge/wiki/Metadata-Reference) · [Codecs and languages](https://github.com/simonefil/RemuxForge/wiki/Codec-and-Language-Reference) |
| How it works | [Internals](https://github.com/simonefil/RemuxForge/wiki/Internals) |
| Something is wrong | [Troubleshooting](https://github.com/simonefil/RemuxForge/wiki/Troubleshooting) |

## Build from source

The CLI and WebUI require the .NET 10 SDK. Desktop packages additionally require Node.js/npm, Rust and the platform prerequisites documented by Tauri.
The Windows MSI must be built on a native Windows host; `release.sh` delegates that build to the configured Windows worker.

```bash
dotnet build RemuxForge.Cli -c Release
dotnet build RemuxForge.Web -c Release

docker build -t remuxforge .

cd RemuxForge.Desktop
npm install
npm run build

# Or select an explicit desktop release target
npm run build:macos-arm64
npm run build:windows-x64
```

The desktop build publishes `RemuxForge.Web` as a self-contained sidecar and bundles its static assets automatically. Explicit-target installers are written under `RemuxForge.Desktop/src-tauri/target/<target>/release/bundle`; native untargeted builds use `RemuxForge.Desktop/src-tauri/target/release/bundle`.
Public desktop artifacts additionally require platform signing: Authenticode on Windows, and Developer ID signing plus notarization/stapling on macOS.
The .NET CLI and WebUI can be published for every RID from one machine. Native Windows is required for the WiX MSI and remains the preferred environment for execution testing and Authenticode signing. `release.sh` runs on the Linux release host, delegates native desktop builds to the configured workers and writes `SHA256SUMS` for all collected artifacts.

Use build-only mode to compile and preserve release artifacts without building or pushing Docker images, creating Git tags or publishing a GitHub release:

```bash
./release.sh v1.2.3 "Local build test" --build-only

# Build only one native desktop target
./release.sh v1.2.3 "Windows desktop build test" --build-only --target desktop-win-x64
./release.sh v1.2.3 "macOS desktop build test" --build-only --target desktop-osx-arm64
```

Target selection is intentionally restricted to build-only mode so that a partial artifact set cannot be published as a release. Windows workers can authenticate with either `WINDOWS_SSH_IDENTITY_FILE` or `WINDOWS_SSH_PASSWORD` in `.env`.

Project layout, publish commands and the localization rules are in [Development](https://github.com/simonefil/RemuxForge/wiki/Development).

## Contributing

Contributions are welcome when they are technical, reproducible and scoped.

- Open an issue for bugs or behavioral changes.
- Include sample command lines, tool versions and relevant MediaInfo/ffmpeg output when reporting processing issues.
- Keep pull requests focused on one change.
- Run the relevant build or test command before submitting.
- Do not include copyrighted media samples in the repository.

## License

RemuxForge is licensed under the [GNU GPLv3](LICENSE).

## Sponsor

If RemuxForge is useful to you, sponsorship is available through [Buy Me a Coffee](https://www.buymeacoffee.com/simonefil).
