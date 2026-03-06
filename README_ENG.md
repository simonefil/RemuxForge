# MergeLanguageTracks

Cross-platform application to merge audio tracks and subtitles from MKV files in different languages. Available in CLI mode and with a TUI graphical interface.

## What is it for?

It allows you to combine audio tracks and subtitles from MKV files of different releases, useful when you have a version with superior video quality but want to integrate audio or subtitles from another version.

The application automatically processes entire seasons, matching corresponding episodes and applying automatic synchronization to compensate for possible editing or speed differences between releases.

## Usage Modes

### TUI (Graphical Interface)

Launching the application without parameters opens the graphical interface based on Terminal.Gui.

The interface is organized in three panels: episode table, selected episode detail, real-time log. Menu bar at the top, status bar with shortcut keys at the bottom.

![Main interface (Nord theme)](images/nord.png)

**Menu:**

- **File**: Configuration (F2), Exit (Ctrl+Q)
- **Actions**: Scan files (F5), Analyze selected (F6), Analyze all (F7), Skip/Unskip (F8), Process selected (F9), Process all (F10)
- **Theme**: change graphical theme
- **Help**: Info and help (F1)

**Shortcut keys:**

| Key | Action |
|-----|--------|
| F1 | Help |
| F2 | Open configuration |
| F5 | Scan folders and match episodes |
| F6 | Analyze selected episode |
| F7 | Analyze all pending episodes |
| F8 | Skip/Unskip selected episode |
| F9 | Merge selected episode |
| F10 | Merge all analyzed episodes |
| Enter | Edit manual delay for episode |
| Ctrl+Q | Exit |

**Configuration (F2):**

The configuration dialog groups all options:

![Configuration dialog](images/config.png)

- **Folders**: Source, Language, Destination, with browse button for each. Checkbox for overwrite source and recursive search.
- **Language and Tracks**: Target language, Audio codec, Keep source audio/codec/sub, Subtitles only, Audio only.
- **Synchronization**: Frame-sync (checkbox), Audio delay (ms), Sub delay (ms).
- **Advanced**: Match pattern (regex), File extensions, Tools folder, mkvmerge path.

**Themes:**

8 themes available from the Theme menu:

| Nord (default) | DOS Blue |
|:-:|:-:|
| ![Nord](images/nord.png) | ![DOS Blue](images/dos.png) |

| Matrix | Cyberpunk |
|:-:|:-:|
| ![Matrix](images/matrix.png) | ![Cyberpunk](images/cyberpunk.png) |

| Solarized Dark | Solarized Light |
|:-:|:-:|
| ![Solarized Dark](images/solarized-dark.png) | ![Solarized Light](images/solarized-light.png) |

| Cybergum | Everforest |
|:-:|:-:|
| ![Cybergum](images/cybergum.png) | ![Everforest](images/everforest.png) |

### CLI (Command Line)

For scriptable and automated processing.

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -d "D:\Output" -fs
```

## Synchronization

The application offers two automatic synchronization systems, both based on visual analysis of video frames via ffmpeg.

### Speed Correction (Automatic)

This is common with European TV series and movies: the Italian release is at 25fps (PAL standard) while the American one is at 23.976fps (NTSC). The audio is slightly faster in one of the two versions and a simple merge would produce a desync that worsens over time.

The application automatically detects this situation by comparing the FPS of both files and corrects it without any options needed. The correction is done entirely in mkvmerge via time-stretching, without audio re-encoding.

**How it works:**

1. Compares the speed of both files by reading video track information via mkvmerge. If the difference is negligible (less than 0.1%) it does nothing
2. Extracts initial video frames from both files via ffmpeg and converts them to low-resolution grayscale images for fast comparison
3. Identifies "scene cuts" in both files, i.e. points where the image changes abruptly (editing cut, shot change). These cuts are identical in both versions regardless of language
4. Matches cuts between source and language to calculate the initial delay. Since the two files have different speeds, the delay is not constant: it grows over time. The algorithm compensates for this "drift" in the calculation
5. Verifies the result at 9 points distributed along the entire video (at 10%, 20%, ... 90% of the duration). At each point it extracts a short segment, finds scene cuts and confirms the calculated delay is correct. If a point fails, it retries with a longer segment. At least 5 valid points out of 9 are required
6. Calculates the correction factor and applies it via mkvmerge to the imported audio and subtitle tracks, without touching the video and without re-encoding

### Frame-Sync (Optional)

When source and language have the same FPS but audio or subtitle tracks are not temporally aligned (longer intro, seconds of black, different credits at the beginning), a fixed offset is needed to realign them. Frame-sync automatically calculates this delay.

Enabled with **-fs** from CLI or from the checkbox in TUI configuration.

**How it works:**

1. Extracts initial video frames from both files (2 minutes from source, 3 from language) and converts them to low-resolution grayscale images
2. Identifies scene cuts (shot changes) in both files
3. For each possible pair of cuts between source and language, calculates what the delay would be if those two cuts corresponded to the same moment in the video
4. Uses a "voting" system: the delay that receives the most coherent votes is selected as a candidate. If many cuts suggest the same offset, the probability of it being correct is high
5. Verifies the candidate by comparing the "visual signature" around the cuts: if frames before and after the cut are similar between source and language (same scene), the match is confirmed
6. Confirms the result at 9 points distributed along the video (10%, 20%, ... 90%). For each point it extracts a short segment, finds local scene cuts and verifies the delay is consistent. If a point fails, it retries with a longer segment. At least 5 valid points out of 9 are required

**When Frame-Sync is needed and when not:**

- **Not needed** if both versions are identical except for the audio language (same encode, same cut). Direct merge works
- **Not needed** if the difference is only in speed (23.976 vs 25 fps). The automatic correction handles this case
- **Needed** when there's a fixed offset between the two versions: longer intro, seconds of black, different cut at the beginning
- **Does not work** if differences are mid-episode (scenes cut or added in the middle). In that case no constant delay can correct the misalignment

**Manual delay:**

The parameters **-ad** and **-sd** specify an offset in milliseconds that is **added** to the frame-sync or speed correction result. In the TUI it's possible to set different delays per episode via Enter.

## Use Cases

**1. Add Italian dubbing to an English release**

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -d "D:\Output" -fs
```

**2. Overwrite the source files**

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -o -fs
```

**3. Replace a lossy track with a lossless one**

The file already has Italian AC3 lossy. You want to replace it with DTS-HD MA from another release.

```bash
MergeLanguageTracks -s "D:\Series" -l "D:\Series.ITA.HDMA" -t ita -ac "DTS-HD MA" -ksa eng,jpn -d "D:\Output" -fs
```

With **-ksa eng,jpn** you keep only English and Japanese from the source. With **-ac "DTS-HD MA"** you only take the lossless track from the Italian release.

**4. Multilanguage remux from different releases**

Each step takes the previous output as source.

```bash
MergeLanguageTracks -s "D:\Movie.US" -l "D:\Movie.ITA" -t ita -d "D:\Temp1" -fs
MergeLanguageTracks -s "D:\Temp1" -l "D:\Movie.FRA" -t fra -d "D:\Temp2" -fs
MergeLanguageTracks -s "D:\Temp2" -l "D:\Movie.GER" -t ger -d "D:\Output" -fs
```

**5. Anime with non-standard naming**

Many fansubs use "- 05" instead of S01E05. With **-m** you specify a custom regex. With **-so** you take only subtitles.

```bash
MergeLanguageTracks -s "D:\Anime.BD" -l "D:\Anime.Fansub" -t ita -m "- (\d+)" -so -d "D:\Output" -fs
```

**6. Daily show with dates in the filename**

```bash
MergeLanguageTracks -s "D:\Show.US" -l "D:\Show.ITA" -t ita -m "(\d{4})\.(\d{2})\.(\d{2})" -d "D:\Output"
```

**7. Filter subtitles from the source**

The source has 10 subtitle tracks in useless languages. With **-kss** you keep only the ones you want.

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -so -kss eng -d "D:\Output" -fs
```

**8. Anime: keep only Japanese audio and import eng+ita**

The trick **-kss und** discards all subtitles from the source because no track has language "und".

```bash
MergeLanguageTracks -s "D:\Anime.BD.JPN" -l "D:\Anime.ITA" -t eng,ita -ksa jpn -kss und -d "D:\Output" -fs
```

**9. Dry run on a complex configuration**

With **-n** verify matching and tracks without executing.

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -ac "E-AC-3" -ksa eng -kss eng -d "D:\Output" -fs -n
```

**10. Keep only DTS tracks from the source**

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -ksac DTS -d "D:\Output" -fs
```

**11. Keep only English lossless audio from the source**

By combining **-ksa** and **-ksac**, you keep only tracks matching both criteria.

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -ksa eng -ksac "DTS-HDMA,TrueHD" -d "D:\Output" -fs
```

**12. Import multiple codecs from the language file**

```bash
MergeLanguageTracks -s "D:\Series.ENG" -l "D:\Series.ITA" -t ita -ac "E-AC-3,DTS" -d "D:\Output" -fs
```

**13. Single source: apply delay and filter tracks**

Without **-l**, the application uses the source folder as language too. Allows remuxing with filters and delays without a separate release.

```bash
MergeLanguageTracks -s "D:\Series" -t ita -ksa jpn,eng -kss eng,jpn -ad 960 -sd 960 -o
```

## Report

At the end of processing a summary report is displayed. In TUI mode the detail is visible in the side panel for each episode.

From CLI the report shows 3 tables:

```
========================================
  Detailed Report
========================================

SOURCE FILES:
  Episode     Audio               Subtitles           Size
  ----------------------------------------------------------------
  01_05       eng,jpn             eng                 4.2 GB

LANGUAGE FILES:
  Episode     Audio               Subtitles           Size
  ----------------------------------------------------------------
  01_05       ita                 ita                 2.1 GB

RESULT FILES:
  Episode     Audio          Subtitles      Size      Delay       FrmSync   Speed     Merge
  ------------------------------------------------------------------------------------------
  01_05       eng,jpn,ita    eng,ita        4.3 GB    +150ms      -         1250ms    12500ms
```

**Result Files columns:**
- **Delay**: offset applied to imported tracks
- **FrmSync**: frame-sync processing time (if active, otherwise "-")
- **Speed**: speed correction processing time (if active, otherwise "-")
- **Merge**: mkvmerge execution time

In dry run mode, Size and Merge show "N/A" because the merge is not executed.

## Audio Codecs

When you specify **-ac** or **-ksac** to filter codecs, the matching is **EXACT**, not partial. Both support multiple comma-separated values.

**Why this matters:**

If a file has both DTS (core) and DTS-HD MA, and you write **-ac "DTS"**, it takes ONLY the DTS core, not the DTS-HD. If you want DTS-HD Master Audio, you must write **-ac "DTS-HDMA"**. If you want both, write **-ac "DTS,DTS-HDMA"**.

Codec names are case-insensitive. If a codec is not recognized with direct lookup, a match without hyphens, spaces and colons is attempted.

**Dolby:**

| Codec | Accepted Aliases | Description |
|-------|-----------------|-------------|
| AC-3 | AC3, DD | Dolby Digital, the classic lossy 5.1 |
| E-AC-3 | EAC3, DD+, DDP | Dolby Digital Plus, used for lossy Atmos on streaming |
| TrueHD | TRUEHD | Dolby TrueHD, lossless, used for Atmos on Blu-ray |
| MLP | | Meridian Lossless Packing (TrueHD base) |
| ATMOS | | Special alias: matches both TrueHD and E-AC-3 |

**DTS:**

| Codec | Accepted Aliases | Description |
|-------|-----------------|-------------|
| DTS | | DTS Core/Digital Surround only (does NOT match DTS-HD) |
| DTS-HD | | Matches both DTS-HD Master Audio and DTS-HD High Resolution |
| DTS-HD MA | DTS-HDMA | DTS-HD Master Audio, lossless |
| DTS-HD HR | DTS-HDHR | DTS-HD High Resolution |
| DTS-ES | | DTS Extended Surround (6.1) |
| DTS:X | DTSX | Object-based, extension of DTS-HD MA |

**Lossless:**

| Codec | Accepted Aliases | Description |
|-------|-----------------|-------------|
| FLAC | | Free Lossless Audio Codec |
| PCM | LPCM, WAV | Raw uncompressed audio |
| ALAC | | Apple Lossless |

**Lossy:**

| Codec | Accepted Aliases | Description |
|-------|-----------------|-------------|
| AAC | HE-AAC | Advanced Audio Coding |
| MP3 | | MPEG Audio Layer 3 |
| MP2 | | MPEG Audio Layer 2 |
| Opus | OPUS | Opus (WebM) |
| Vorbis | VORBIS | Ogg Vorbis |

## Language Codes

Language codes are ISO 639-2 (3 letters). The most common ones:

- **ita** - Italian
- **eng** - English
- **jpn** - Japanese
- **ger** or **deu** - German
- **fra** or **fre** - French
- **spa** - Spanish
- **por** - Portuguese
- **rus** - Russian
- **chi** or **zho** - Chinese
- **kor** - Korean
- **und** - Undefined (unspecified language)

If you mistype a code, the application suggests the correct one via the LanguageValidator:

```
Language 'italian' not recognized.
Did you mean: ita?
```

## Requirements

- [MKVToolNix](https://mkvtoolnix.download/) installed (mkvmerge must be in PATH or specified with **-mkv**)
- ffmpeg for frame-sync and speed correction (automatically downloaded to the tools folder if missing)
- UTF-8 locale on Linux (required for filenames with non-ASCII characters)

**Supported platforms** (from csproj RuntimeIdentifiers):

- Windows (x64)
- Linux (x64, ARM64)
- macOS (x64, ARM64)

## Build

Requires .NET 10 SDK. The project uses Terminal.Gui 2.0.0-develop.5118.

```bash
# Build for the current platform
dotnet build -c Release

# Publish as standalone executable (single file, compressed)
dotnet publish -c Release -r win-x64 --self-contained true
dotnet publish -c Release -r linux-x64 --self-contained true
dotnet publish -c Release -r linux-arm64 --self-contained true
dotnet publish -c Release -r osx-x64 --self-contained true
dotnet publish -c Release -r osx-arm64 --self-contained true
```

## Parameter Reference

### Required

| Short | Long | Description |
|-------|------|-------------|
| -s | --source | Folder with source MKV files |
| -t | --target-language | Language code of tracks to import (e.g.: ita). Separate with comma for multiple languages: ita,eng |

### Source

| Short | Long | Description |
|-------|------|-------------|
| -l | --language | Folder with MKV files to take tracks from. If omitted, uses the source folder |

### Output (mutually exclusive, one required)

| Short | Long | Description |
|-------|------|-------------|
| -d | --destination | Folder where resulting files will be saved |
| -o | --overwrite | Overwrite source files (flag, no value) |

### Sync

| Short | Long | Description |
|-------|------|-------------|
| -fs | --framesync | Synchronization via visual frame comparison (scene-cut) |
| -ad | --audio-delay | Manual delay in ms for audio (added to frame-sync/speed if active) |
| -sd | --subtitle-delay | Manual delay in ms for subtitles |

Speed correction (stretch) is always automatic and requires no parameters.

### Filters

| Short | Long | Description |
|-------|------|-------------|
| -ac | --audio-codec | Audio codec to import from language file. Separate with comma: DTS,E-AC-3 |
| -so | --sub-only | Import only subtitles, ignore audio |
| -ao | --audio-only | Import only audio, ignore subtitles |
| -ksa | --keep-source-audio | Audio languages to KEEP in the source (others are removed) |
| -ksac | --keep-source-audio-codec | Audio codecs to KEEP in the source. Separate with comma: DTS,TrueHD |
| -kss | --keep-source-subs | Subtitle languages to KEEP in the source |

### Matching

| Short | Long | Description | Default |
|-------|------|-------------|---------|
| -m | --match-pattern | Regex for episode matching | S(\d+)E(\d+) |
| -r | --recursive | Search in subfolders | active |
| -ext | --extensions | File extensions to search for. Separate with comma: mkv,mp4,avi | mkv |

### Common Regex Patterns

The application uses captured groups from the regex to match files. Each group in parentheses is concatenated with "_" to create the unique episode ID.

| Format | Example File | Pattern |
|--------|--------------|---------|
| Standard | Series.S01E05.mkv | S(\d+)E(\d+) |
| With dot | Series.S01.E05.mkv | S(\d+)\.E(\d+) |
| Format 1x05 | Series.1x05.mkv | (\d+)x(\d+) |
| Episode only | Anime - 05.mkv | - (\d+) |
| 3-digit episode | Anime - 005.mkv | - (\d{3}) |
| Daily show | Show.2024.01.15.mkv | (\d{4})\.(\d{2})\.(\d{2}) |

**How it works:** The pattern **S(\d+)E(\d+)** captures two groups (season and episode). For "S01E05" it creates the ID "01_05". Source and language files with the same ID are matched together.

### Other

| Short | Long | Description | Default |
|-------|------|-------------|---------|
| -n | --dry-run | Show what it would do without executing | |
| -h | --help | Show built-in help | |
| -mkv | --mkvmerge-path | Custom path to mkvmerge | mkvmerge (searches PATH) |
| -tools | --tools-folder | Folder for downloaded ffmpeg | |
