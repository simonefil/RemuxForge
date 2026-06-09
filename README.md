# ![icon](icons/icon-48.png) RemuxForge

RemuxForge is a cross-platform MKV utility for two technical workflows:

- **Remux**: import audio tracks and subtitles from another MKV release, with optional speed correction, frame-sync or Deep Analysis for edited releases.
- **Split**: cut HEVC/AVC MKV files into frame-perfect segments while preserving VFR timing, chapters, audio and subtitles.

It ships as a scriptable CLI and as a WebUI for local browsers or headless servers.

## Requirements

- [MKVToolNix](https://mkvtoolnix.download/) (`mkvmerge`, `mkvextract`, `mkvpropedit`)
- [ffmpeg](https://ffmpeg.org/) (`ffmpeg`, `ffprobe`)
- [MediaInfo CLI](https://mediaarea.net/en/MediaInfo) (`mediainfo`)
- UTF-8 locale on Linux

Tool paths are auto-detected or configurable from the WebUI under **Settings > Tool paths**.

Supported targets:

| Platform | Architectures |
|----------|---------------|
| Windows | x64 |
| Linux | x64, ARM64 |
| macOS | x64, ARM64 |
| Docker | x64 |

## Installation

Download the CLI or WebUI archive for your platform from the [releases page](https://github.com/simonefil/RemuxForge/releases), then extract it.

CLI:

```bash
# Windows
RemuxForge.Cli.exe --help

# Linux/macOS
chmod +x RemuxForge.Cli
./RemuxForge.Cli --help
```

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
  -e REMUXFORGE_PORT=5000 \
  -e REMUXFORGE_DATA_DIR=/data \
  -v /path/to/config:/data:rw \
  -v /path/to/media:/media:rw \
  draknodd/remuxforge:latest
```

Docker Compose:

```yaml
services:
  remuxforge:
    image: draknodd/remuxforge:latest
    container_name: remuxforge
    restart: unless-stopped
    user: "1000:1000"
    ports:
      - "5000:5000"
    environment:
      - REMUXFORGE_PORT=5000
      - REMUXFORGE_DATA_DIR=/data
    volumes:
      - /path/to/config:/data:rw
      - /path/to/media:/media:rw
```

Environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `REMUXFORGE_PORT` | WebUI HTTP port | `5000` |
| `REMUXFORGE_DATA_DIR` | Configuration/data directory | Executable directory |
| `REMUXFORGE_LOG_FILE` | Optional log file path | Disabled |

## CLI Syntax

General form:

```bash
RemuxForge.Cli --mode remux|split [options]
```

Remux example:

```bash
RemuxForge.Cli --mode remux -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -d "D:\Output" -fs
```

Split example:

```bash
RemuxForge.Cli --mode split --source "disc1.mkv" --pattern "5,5,5,6" --output-dir "D:\Output"
```

Common options:

| Short | Long | Description |
|-------|------|-------------|
| | `--mode` | Required. `remux` or `split` |
| `-s` | `--source` | Source folder for remux, source MKV/folder for split |
| `-r` | `--recursive` | Search subfolders |
| `-nr` | `--no-recursive` | Disable subfolder search |
| `-ext` | `--extensions` | File extensions to scan, default `mkv` |
| `-n` | `--dry-run` | Print planned actions without writing files |
| `-h` | `--help` | Show built-in help |
| | `--lang` | UI/CLI language: `en` or `it` |

Main remux options:

| Short | Long | Description |
|-------|------|-------------|
| `-l` | `--language` | Folder containing tracks to import |
| `-t` | `--target-language` | ISO 639-2 language code(s), for example `ita` or `eng,ita` |
| `-d` | `--destination` | Output folder |
| `-o` | `--overwrite` | Overwrite source files |
| `-fs` | `--framesync` | Calculate a constant visual sync delay |
| `-da` | `--deep-analysis` | Build a cut/insert map for edited releases |
| | `--speed-correction` | `off`, `auto`, `manual` |
| | `--stretch-factor` | Manual speed factor, for example `25025/24000` |
| | `--subtitle-canvas-rewrite` | Rewrite imported bitmap subtitle canvas/coordinates when analysis geometry allows it |
| `-ac` | `--audio-codec` | Import only matching audio codecs |
| `-so` | `--sub-only` | Import subtitles only |
| `-ao` | `--audio-only` | Import audio only |
| `-ksa` | `--keep-source-audio` | Keep only these source audio languages |
| `-ksac` | `--keep-source-audio-codec` | Keep only these source audio codecs |
| `-kss` | `--keep-source-subs` | Keep only these source subtitle languages |
| | `--audio-format` | `flac`, `lpcm`, `aac`, `opus` |
| | `--audio-scope` | `disabled`, `lang`, `all` |
| `-ep` | `--encoding-profile` | Post-merge video encoding profile |

Main split options:

| Long | Description |
|------|-------------|
| `--pattern "5,5,5,6"` | Group chapters into segments |
| `--ranges "T1-T2,T3-T4"` | Explicit ranges; `T` accepts timecodes, seconds, `f<frame>`, `END` |
| `--split-at "T1,T2"` | Split at the given points |
| `--trim-start T` | Drop content before `T` |
| `--trim-end T` | Drop content after `T` |
| `--chapters-each` | Create one segment per chapter |
| `--source-raw FILE` | Alternate source for PTS extraction |
| `--output-dir DIR` | Output directory |
| `--output-template TPL` | Custom output filename template |
| `--snap off|before|after|nearest` | Move start to a keyframe instead of re-encoding the first GOP |
| `--force` | Overwrite existing output files |

See the [CLI Reference](https://github.com/simonefil/RemuxForge/wiki/CLI-Reference) for the complete option list.

## Documentation

- [Wiki home](https://github.com/simonefil/RemuxForge/wiki)
- [Getting Started](https://github.com/simonefil/RemuxForge/wiki/Getting-Started)
- [WebUI Guide](https://github.com/simonefil/RemuxForge/wiki/WebUI-Guide)
- [CLI Guide](https://github.com/simonefil/RemuxForge/wiki/CLI-Guide)
- [Remux Guide](https://github.com/simonefil/RemuxForge/wiki/Remux-Guide)
- [Split Guide](https://github.com/simonefil/RemuxForge/wiki/Split-Guide)
- [CLI Reference](https://github.com/simonefil/RemuxForge/wiki/CLI-Reference)

## Build from Source

Requires .NET 10 SDK.

```bash
dotnet build RemuxForge.Cli -c Release

cd RemuxForge.Web
libman restore
cd ..
dotnet build RemuxForge.Web -c Release
```

Docker image:

```bash
docker build -t remuxforge .
```

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
