using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MergeLanguageTracks
{
    internal class Program
    {
        #region Classi interne

        /// <summary>
        /// Contesto servizi e configurazione per elaborazione file
        /// </summary>
        private class ProcessingContext
        {
            /// <summary>
            /// Servizio mkvmerge
            /// </summary>
            public MkvToolsService Service;

            /// <summary>
            /// Servizio frame-sync
            /// </summary>
            public FrameSyncService FrameSyncService;

            /// <summary>
            /// Statistiche elaborazione
            /// </summary>
            public ProcessingStats Stats;

            /// <summary>
            /// Lista record elaborazione
            /// </summary>
            public List<FileProcessingRecord> Records;

            /// <summary>
            /// Opzioni riga di comando
            /// </summary>
            public Options Opts;

            /// <summary>
            /// Pattern codec per filtro tracce lingua
            /// </summary>
            public string[] CodecPatterns;

            /// <summary>
            /// Pattern codec per filtro tracce audio sorgente
            /// </summary>
            public string[] SourceAudioCodecPatterns;

            /// <summary>
            /// Se filtrare le tracce audio sorgente
            /// </summary>
            public bool FilterSourceAudio;

            /// <summary>
            /// Se filtrare i sottotitoli sorgente
            /// </summary>
            public bool FilterSourceSubs;

            /// <summary>
            /// Percorso ffmpeg risolto
            /// </summary>
            public string FfmpegPath;
        }

        /// <summary>
        /// Stato mutabile durante elaborazione di un singolo file
        /// </summary>
        private class ProcessingState
        {
            /// <summary>
            /// Delay audio effettivo in ms
            /// </summary>
            public int EffectiveAudioDelay;

            /// <summary>
            /// Delay sottotitoli effettivo in ms
            /// </summary>
            public int EffectiveSubDelay;

            /// <summary>
            /// Fattore stretch per mkvmerge
            /// </summary>
            public string StretchFactor;

            /// <summary>
            /// Se correzione velocita' attiva
            /// </summary>
            public bool SpeedCorrectionActive;
        }

        #endregion

        #region Entry point

        /// <summary>
        /// Entry point
        /// </summary>
        /// <param name="args">Argomenti riga di comando</param>
        /// <returns>Codice uscita: 0 successo, 1 errore</returns>
        static int Main(string[] args)
        {
            bool done = false;
            int exitCode = 0;
            Options opts = null;
            string[] codecPatterns = null;
            MkvToolsService tempService = null;
            FrameSyncService frameSyncService = null;
            FfmpegProvider ffmpegProvider = null;
            string resolvedFfmpegPath = "";
            ProcessingContext ctx = null;
            string extList = "";
            List<string> sourceFiles = null;
            List<string> languageFiles = null;
            Dictionary<string, string> languageIndex = null;
            string langFileName = "";
            string langEpisodeId = "";

            // Nessun argomento: avvia TUI interattiva
            if (args.Length == 0)
            {
                TuiApp tuiApp = new TuiApp();
                tuiApp.Run();
                done = true;
            }

            // Parsing argomenti
            if (!done)
            {
                opts = Options.Parse(args);
                if (opts.ErrorMessage.Length > 0)
                {
                    ConsoleHelper.WriteRed("Errore: " + opts.ErrorMessage);
                    ConsoleHelper.WriteDarkGray("Usa -h per vedere tutte le opzioni.");
                    exitCode = 1;
                    done = true;
                }
            }

            // Help
            if (!done && opts.Help)
            {
                PrintHelp();
                done = true;
            }

            // Normalizzazione percorsi e validazione
            if (!done)
            {
                NormalizeOptionsPaths(opts);
                if (!ValidateOptions(opts))
                {
                    exitCode = 1;
                    done = true;
                }
            }

            // Creazione cartella destinazione
            if (!done && !opts.Overwrite && !Directory.Exists(opts.DestinationFolder))
            {
                ConsoleHelper.WriteYellow("Creazione cartella destinazione: " + opts.DestinationFolder);
                Directory.CreateDirectory(opts.DestinationFolder);
            }

            // Risoluzione pattern codec per filtro tracce lingua
            if (!done && opts.AudioCodec.Count > 0)
            {
                codecPatterns = ResolveCodecPatterns(opts.AudioCodec);
            }

            // Verifica mkvmerge
            if (!done)
            {
                tempService = new MkvToolsService(opts.MkvMergePath);
                if (!tempService.VerifyMkvMerge())
                {
                    ConsoleHelper.WriteRed("mkvmerge non trovato. Installa MKVToolNix o specifica -mkv");
                    exitCode = 1;
                    done = true;
                }
                else
                {
                    ConsoleHelper.WriteGreen("Trovato mkvmerge: " + opts.MkvMergePath);
                }
            }

            // Inizializzazione frame-sync
            if (!done && opts.FrameSync)
            {
                ffmpegProvider = new FfmpegProvider(opts.ToolsFolder);
                if (!ffmpegProvider.Resolve())
                {
                    ConsoleHelper.WriteRed("ffmpeg non trovato e impossibile scaricarlo. Installalo manualmente.");
                    exitCode = 1;
                    done = true;
                }
                else
                {
                    ConsoleHelper.WriteGreen("Trovato ffmpeg: " + ffmpegProvider.FfmpegPath);
                    resolvedFfmpegPath = ffmpegProvider.FfmpegPath;
                    frameSyncService = new FrameSyncService(resolvedFfmpegPath);
                }
            }

            // Elaborazione file
            if (!done)
            {
                // Crea contesto elaborazione
                ctx = new ProcessingContext();
                ctx.Service = new MkvToolsService(opts.MkvMergePath);
                ctx.FrameSyncService = frameSyncService;
                ctx.Stats = new ProcessingStats();
                ctx.Records = new List<FileProcessingRecord>();
                ctx.Opts = opts;
                ctx.CodecPatterns = codecPatterns;
                ctx.FfmpegPath = resolvedFfmpegPath;

                // Risoluzione pattern codec per filtro tracce audio sorgente
                if (opts.KeepSourceAudioCodec.Count > 0)
                {
                    ctx.SourceAudioCodecPatterns = ResolveCodecPatterns(opts.KeepSourceAudioCodec);
                }

                // Flag filtraggio tracce sorgente
                ctx.FilterSourceAudio = (opts.KeepSourceAudioLangs.Count > 0 || opts.KeepSourceAudioCodec.Count > 0);
                ctx.FilterSourceSubs = (opts.KeepSourceSubtitleLangs.Count > 0);

                // Banner
                ConsoleHelper.WriteCyan("\n========================================");
                ConsoleHelper.WriteCyan("  MKV Language Track Merger");
                ConsoleHelper.WriteCyan("========================================\n");

                // Configurazione
                PrintConfiguration(opts, codecPatterns);

                // Trova file sorgente
                extList = string.Join(", ", opts.FileExtensions);
                sourceFiles = FindVideoFiles(opts.SourceFolder, opts.FileExtensions, opts.Recursive);
                ConsoleHelper.WriteGreen("Trovati " + sourceFiles.Count + " file sorgente (" + extList + ")\n");

                // Costruisci indice file lingua
                ConsoleHelper.WriteYellow("Indicizzazione cartella lingua...");
                languageFiles = FindVideoFiles(opts.LanguageFolder, opts.FileExtensions, opts.Recursive);
                languageIndex = new Dictionary<string, string>();

                for (int i = 0; i < languageFiles.Count; i++)
                {
                    langFileName = Path.GetFileName(languageFiles[i]);
                    langEpisodeId = GetEpisodeIdentifier(langFileName, opts.MatchPattern);
                    if (langEpisodeId.Length > 0)
                    {
                        languageIndex[langEpisodeId] = languageFiles[i];
                    }
                }

                ConsoleHelper.WriteGreen("Indicizzati " + languageIndex.Count + " file lingua\n");

                // Elabora ogni file sorgente
                for (int i = 0; i < sourceFiles.Count; i++)
                {
                    ProcessFile(sourceFiles[i], languageIndex, ctx);
                }

                // Report e riepilogo
                PrintDetailedReport(ctx.Records, ctx.Opts.DryRun);
                PrintSummary(ctx.Stats);
            }

            return exitCode;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Normalizza percorsi opzioni e applica default
        /// </summary>
        /// <param name="opts">Opzioni da normalizzare</param>
        private static void NormalizeOptionsPaths(Options opts)
        {
            string appDir = "";

            // Normalizza percorsi
            if (opts.SourceFolder.Length > 0)
            {
                opts.SourceFolder = NormalizePath(opts.SourceFolder);
            }
            if (opts.LanguageFolder.Length > 0)
            {
                opts.LanguageFolder = NormalizePath(opts.LanguageFolder);
            }
            if (opts.DestinationFolder.Length > 0)
            {
                opts.DestinationFolder = NormalizePath(opts.DestinationFolder);
            }

            // Modalita' singola sorgente: se -l non specificato, usa -s come lingua
            if (opts.LanguageFolder.Length == 0 && opts.SourceFolder.Length > 0)
            {
                opts.LanguageFolder = opts.SourceFolder;
            }

            // Cartella tools di default
            if (opts.ToolsFolder.Length == 0)
            {
                appDir = AppContext.BaseDirectory;
                opts.ToolsFolder = Path.Combine(appDir, "tools");
            }
        }

        /// <summary>
        /// Risolve una lista di nomi codec nei rispettivi pattern di matching
        /// </summary>
        /// <param name="codecs">Lista nomi codec</param>
        /// <returns>Array di pattern codec risolti</returns>
        private static string[] ResolveCodecPatterns(List<string> codecs)
        {
            List<string> allPatterns = new List<string>();
            string[] patterns = null;

            for (int c = 0; c < codecs.Count; c++)
            {
                patterns = CodecMapping.GetCodecPatterns(codecs[c]);
                for (int p = 0; p < patterns.Length; p++)
                {
                    if (!allPatterns.Contains(patterns[p]))
                    {
                        allPatterns.Add(patterns[p]);
                    }
                }
            }

            return allPatterns.ToArray();
        }

        /// <summary>
        /// Stampa il testo di aiuto completo
        /// </summary>
        private static void PrintHelp()
        {
            string helpText = @"
USAGE: MergeLanguageTracks [OPTIONS]

Unisce tracce audio e sottotitoli da file MKV in lingue diverse.
Supporta sincronizzazione automatica tramite confronto visivo frame.

OPZIONI OBBLIGATORIE:
  -s,   --source <path>          Cartella con i file MKV sorgente
  -t,   --target-language <code> Codice/i lingua ISO 639-2 (es: ita, eng  oppure: eng,ita)

OPZIONI SORGENTE:
  -l,   --language <path>        Cartella con i file MKV nella lingua da importare
                                 Se omesso, usa la cartella sorgente (modalita' singola sorgente)

OPZIONI OUTPUT (mutuamente esclusive, una obbligatoria):
  -d,   --destination <path>     Cartella di output
  -o,   --overwrite              Sovrascrive i file sorgente

OPZIONI SYNC:
  -fs,  --framesync              Abilita sync tramite confronto visivo frame
  -ad,  --audio-delay <ms>       Delay manuale audio in ms (sommato a frame-sync se -fs)
  -sd,  --subtitle-delay <ms>    Delay manuale sottotitoli in ms

  NOTA: La correzione velocita' (es. PAL 25fps vs NTSC 23.976fps) e'
        automatica e non richiede opzioni. Necessita di ffmpeg

OPZIONI FILTRO:
  -ac,  --audio-codec <codec>    Importa solo audio con codec specifico (es: E-AC-3 oppure DTS,E-AC-3)
  -so,  --sub-only               Importa solo sottotitoli (ignora audio)
  -ao,  --audio-only             Importa solo audio (ignora sottotitoli)
  -ksa, --keep-source-audio      Lingue audio da mantenere nel sorgente (es: eng,jpn)
  -ksac,--keep-source-audio-codec Codec audio da mantenere nel sorgente (es: DTS,E-AC-3)
  -kss, --keep-source-subs       Lingue sub da mantenere nel sorgente

OPZIONI MATCHING:
  -m,   --match-pattern <regex>  Pattern per matching episodi (default: S(\d+)E(\d+))
  -r,   --recursive              Cerca ricorsivamente nelle sottocartelle (default: true)
  -ext, --extensions <list>      Estensioni file da cercare (default: mkv). Separa con virgola: mkv,mp4,avi

OPZIONI TOOL:
  -mkv,   --mkvmerge-path <path> Percorso mkvmerge (default: cerca in PATH)
  -tools, --tools-folder <path>  Cartella per tool scaricati (ffmpeg)

ALTRE OPZIONI:
  -n,   --dry-run                Mostra cosa verrebbe fatto senza eseguire
  -h,   --help                   Mostra questo messaggio

CODEC AUDIO (per -ac):
  Dolby:
    AC-3        Dolby Digital (DD, AC3)
    E-AC-3      Dolby Digital Plus (DD+, EAC3, include Atmos lossy)
    TrueHD      Dolby TrueHD (include Atmos lossless)
    MLP         Meridian Lossless Packing (base di TrueHD)

  DTS:
    DTS         DTS Core / Digital Surround
    DTS-HD MA   DTS-HD Master Audio (lossless)
    DTS-HD HR   DTS-HD High Resolution
    DTS-ES      DTS Extended Surround
    DTS:X       DTS:X (object-based, estensione di DTS-HD MA)

  Lossless:
    FLAC        Free Lossless Audio Codec
    PCM         Audio non compresso (LPCM, WAV)
    ALAC        Apple Lossless

  Lossy:
    AAC         Advanced Audio Coding (LC, HE-AAC, HE-AACv2)
    MP3         MPEG Audio Layer 3
    MP2         MPEG Audio Layer 2
    Opus        Opus (WebM, alta qualita' a basso bitrate)
    Vorbis      Ogg Vorbis

  IMPORTANTE: il matching e' ESATTO, non parziale!
        -ac ""DTS""      -> matcha SOLO DTS core, NON DTS-HD MA
        -ac ""DTS-HD""   -> matcha DTS-HD MA e DTS-HD HR
        -ac ""DTS-HDMA"" -> matcha SOLO DTS-HD Master Audio
        -ac ""ATMOS""    -> matcha TrueHD e E-AC-3 (entrambi possono avere Atmos)

  Alias comuni accettati:
        EAC3, DDP, DD+ -> E-AC-3
        AC3, DD        -> AC-3
        DTSX           -> DTS:X
        LPCM, WAV      -> PCM

ESEMPI:
  # Unisci tracce italiane con frame-sync
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -d ""D:\Out"" -fs

  # Dry run (mostra cosa farebbe senza eseguire)
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -d ""D:\Out"" -fs -n

  # Solo audio E-AC-3 italiano
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -ac ""E-AC-3"" -d ""D:\Out"" -fs

  # Importa audio DTS o E-AC-3 italiano
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -ac ""DTS,E-AC-3"" -d ""D:\Out"" -fs

  # Solo sottotitoli (no audio)
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -so -d ""D:\Out"" -fs

  # Sovrascrive i file sorgente (no cartella destinazione)
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -o -fs

  # Mantieni solo eng/jpn audio e eng sub dal sorgente
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -ksa eng,jpn -kss eng -d ""D:\Out""

  # Mantieni solo tracce DTS dal sorgente (qualsiasi lingua)
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -ksac DTS -d ""D:\Out""

  # Mantieni solo eng con codec DTS dal sorgente
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -ksa eng -ksac DTS -d ""D:\Out""

  # Pattern custom (1x01 invece di S01E01)
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -m ""(\d+)x(\d+)"" -d ""D:\Out""

  # Cerca anche file MP4 e AVI oltre a MKV
  MergeLanguageTracks -s ""D:\EN"" -l ""D:\IT"" -t ita -ext mkv,mp4,avi -d ""D:\Out"" -fs

  # Singola sorgente: applica delay 960ms alle tracce ita, mantieni jpn+eng audio e eng+jpn sub
  MergeLanguageTracks -s ""D:\Serie"" -t ita -ksa jpn,eng -kss eng,jpn -ad 960 -sd 960 -o

CODICI LINGUA (ISO 639-2):
  Comuni: ita, eng, jpn, ger/deu, fra/fre, spa, por, rus, chi/zho, kor
  Altri:  ara, hin, pol, tur, nld/dut, swe, nor, dan, fin, hun, ces/cze
  Speciali: und (undefined), mul (multiple), zxx (no language)

REQUISITI:
  - MKVToolNix (mkvmerge) nel PATH
  - ffmpeg per frame-sync (scaricato automaticamente se mancante)

NOTE:
  Correzione velocita' (stretch): rileva automaticamente differenze FPS tra
  sorgente e lingua (es. PAL 25fps vs NTSC 23.976fps). Corregge tramite
  mkvmerge --sync senza ricodifica. Richiede ffmpeg per la verifica.

  Frame-sync: rileva i tagli scena nei frame grayscale 320x240 e li confronta
  tra sorgente e lingua per trovare il delay. Verifica a 9 punti distribuiti
  nel video con retry adattivo. Copre offset fino a +-60 secondi.

  Entrambe le funzionalita' richiedono ffmpeg (scaricato automaticamente).
";
            Console.WriteLine(helpText);
        }

        /// <summary>
        /// Normalizza un percorso alla forma assoluta
        /// </summary>
        /// <param name="path">Percorso da normalizzare</param>
        /// <returns>Percorso assoluto normalizzato</returns>
        private static string NormalizePath(string path)
        {
            string result = path;

            if (path.Length > 0)
            {
                result = Path.GetFullPath(path);
                result = result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return result;
        }

        /// <summary>
        /// Estrae l'identificatore episodio da un nome file
        /// </summary>
        /// <param name="fileName">Nome file da cui estrarre</param>
        /// <param name="pattern">Pattern regex con gruppi di cattura</param>
        /// <returns>Identificatore episodio o stringa vuota</returns>
        private static string GetEpisodeIdentifier(string fileName, string pattern)
        {
            string result = "";

            Match match = Regex.Match(fileName, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (match.Groups.Count > 1)
                {
                    // Unisci tutti i gruppi di cattura con underscore
                    StringBuilder sb = new StringBuilder();
                    for (int g = 1; g < match.Groups.Count; g++)
                    {
                        if (g > 1)
                        {
                            sb.Append("_");
                        }
                        sb.Append(match.Groups[g].Value);
                    }
                    result = sb.ToString();
                }
                else
                {
                    // Nessun gruppo di cattura, usa il match completo come identificatore
                    result = match.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Formatta un delay in millisecondi con segno
        /// </summary>
        /// <param name="delayMs">Ritardo in millisecondi</param>
        /// <returns>Stringa formattata con segno</returns>
        private static string FormatDelay(int delayMs)
        {
            string result = "0ms";

            if (delayMs > 0)
            {
                result = "+" + delayMs + "ms";
            }
            else if (delayMs < 0)
            {
                result = delayMs + "ms";
            }

            return result;
        }

        /// <summary>
        /// Formatta le informazioni traccia per output console
        /// </summary>
        /// <param name="tracks">Lista tracce da formattare</param>
        /// <returns>Stringa formattata multilinea</returns>
        private static string FormatTrackInfo(List<TrackInfo> tracks)
        {
            string result = "  Nessuna";

            if (tracks.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.AppendLine();
                    }
                    string name = (tracks[i].Name.Length > 0) ? " - \"" + tracks[i].Name + "\"" : "";
                    string lang = (tracks[i].Language.Length > 0) ? "[" + tracks[i].Language + "]" : "[und]";
                    sb.Append("  Track " + tracks[i].Id + ": " + tracks[i].Codec + " " + lang + name);
                }
                result = sb.ToString();
            }

            return result;
        }

        /// <summary>
        /// Formatta una lista di ID traccia separati da virgola
        /// </summary>
        /// <param name="trackIds">Lista ID traccia</param>
        /// <returns>Stringa separata da virgola o "Nessuna"</returns>
        private static string FormatTrackIdList(List<int> trackIds)
        {
            string result = "Nessuna";

            if (trackIds.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < trackIds.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(trackIds[i]);
                }
                result = sb.ToString();
            }

            return result;
        }

        /// <summary>
        /// Raccoglie tutti i file video da una directory
        /// </summary>
        /// <param name="folder">Cartella dove cercare</param>
        /// <param name="extensions">Lista estensioni senza punto</param>
        /// <param name="recursive">Se cercare nelle sottocartelle</param>
        /// <returns>Lista percorsi completi ai file trovati</returns>
        private static List<string> FindVideoFiles(string folder, List<string> extensions, bool recursive)
        {
            List<string> files = new List<string>();
            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            // Cerca per ogni estensione
            for (int e = 0; e < extensions.Count; e++)
            {
                string pattern = "*." + extensions[e];
                string[] found = Directory.GetFiles(folder, pattern, searchOption);
                for (int i = 0; i < found.Length; i++)
                {
                    files.Add(found[i]);
                }
            }

            return files;
        }

        /// <summary>
        /// Valida tutte le opzioni richieste
        /// </summary>
        /// <param name="opts">Opzioni da validare</param>
        /// <returns>True se tutte le validazioni passano</returns>
        private static bool ValidateOptions(Options opts)
        {
            bool valid = true;
            Regex langRegex = new Regex(@"^[a-z]{2,3}$");

            // Verifica parametri obbligatori
            if (opts.SourceFolder.Length == 0 || opts.TargetLanguage.Count == 0)
            {
                ConsoleHelper.WriteRed("Errore: parametri obbligatori mancanti.");
                ConsoleHelper.WriteYellow("Uso: MergeLanguageTracks -s <source> [-l <lang>] -t <lingua> [-d <dest> | -o] [-fs] [-n]");
                ConsoleHelper.WriteDarkGray("     Usa -h per vedere tutte le opzioni.");
                valid = false;
            }

            // Valida esistenza cartella sorgente
            if (valid && !Directory.Exists(opts.SourceFolder))
            {
                ConsoleHelper.WriteRed("Errore: cartella sorgente non trovata: " + opts.SourceFolder);
                valid = false;
            }

            // Valida esistenza cartella lingua
            if (valid && !Directory.Exists(opts.LanguageFolder))
            {
                ConsoleHelper.WriteRed("Errore: cartella lingua non trovata: " + opts.LanguageFolder);
                valid = false;
            }

            // Valida formato codice lingua
            if (valid)
            {
                for (int i = 0; i < opts.TargetLanguage.Count && valid; i++)
                {
                    if (!langRegex.IsMatch(opts.TargetLanguage[i].ToLower()))
                    {
                        ConsoleHelper.WriteRed("Errore: lingua non valida '" + opts.TargetLanguage[i] + "'. Usa codice ISO 639-2 (es: ita, eng, jpn)");
                        valid = false;
                    }
                }
            }

            // Valida lingue target contro lista ISO 639-2
            if (valid)
            {
                valid = ValidateLanguageList(opts.TargetLanguage, "", true);
            }

            // Valida KeepSourceAudioLangs
            if (valid)
            {
                valid = ValidateLanguageList(opts.KeepSourceAudioLangs, "-ksa", false);
            }

            // Valida KeepSourceSubtitleLangs
            if (valid)
            {
                valid = ValidateLanguageList(opts.KeepSourceSubtitleLangs, "-kss", false);
            }

            // Valida codec audio sorgente se specificato
            if (valid)
            {
                valid = ValidateCodecList(opts.KeepSourceAudioCodec, "-ksac");
            }

            // Valida mutua esclusione SubOnly e AudioOnly
            if (valid && opts.SubOnly && opts.AudioOnly)
            {
                ConsoleHelper.WriteRed("Errore: -so e -ao non possono essere usati insieme.");
                valid = false;
            }

            // Valida modalita' output: -o e -d sono mutuamente esclusive
            if (valid && opts.Overwrite && opts.DestinationFolder.Length > 0)
            {
                ConsoleHelper.WriteRed("Errore: -o (--overwrite) e -d (--destination) non possono essere usati insieme.");
                valid = false;
            }

            // Almeno uno tra -o e -d deve essere specificato
            if (valid && !opts.Overwrite && opts.DestinationFolder.Length == 0)
            {
                ConsoleHelper.WriteRed("Errore: specificare -d <cartella> oppure -o per sovrascrivere i sorgente.");
                valid = false;
            }

            // Valida codec audio se specificato
            if (valid)
            {
                valid = ValidateCodecList(opts.AudioCodec, "-ac");
            }

            return valid;
        }

        /// <summary>
        /// Valida una lista di codici lingua ISO 639-2 con suggerimenti in caso di errore
        /// </summary>
        /// <param name="langs">Lista lingue da validare</param>
        /// <param name="fieldName">Nome parametro per messaggio errore (es: "-ksa"), vuoto per target</param>
        /// <param name="showGenericHelp">Se mostrare help generico quando non ci sono suggerimenti</param>
        /// <returns>True se tutte le lingue sono valide</returns>
        private static bool ValidateLanguageList(List<string> langs, string fieldName, bool showGenericHelp)
        {
            bool valid = true;
            List<string> suggestions = null;
            string suffix = fieldName.Length > 0 ? " in " + fieldName : "";

            for (int i = 0; i < langs.Count && valid; i++)
            {
                if (!LanguageValidator.IsValid(langs[i]))
                {
                    ConsoleHelper.WriteRed("Errore: lingua '" + langs[i] + "'" + suffix + " non riconosciuta.");
                    suggestions = LanguageValidator.GetSimilar(langs[i], 3);
                    if (suggestions.Count > 0)
                    {
                        ConsoleHelper.WriteYellow("Forse intendevi: " + string.Join(", ", suggestions) + "?");
                    }
                    else if (showGenericHelp)
                    {
                        ConsoleHelper.WriteYellow("Usa codici ISO 639-2 (es: ita, eng, jpn, ger, fra, spa)");
                    }
                    valid = false;
                }
            }

            return valid;
        }

        /// <summary>
        /// Valida una lista di nomi codec contro il mapping codec
        /// </summary>
        /// <param name="codecs">Lista codec da validare</param>
        /// <param name="fieldName">Nome parametro per messaggio errore (es: "-ac")</param>
        /// <returns>True se tutti i codec sono validi</returns>
        private static bool ValidateCodecList(List<string> codecs, string fieldName)
        {
            bool valid = true;
            string[] patterns = null;

            for (int i = 0; i < codecs.Count && valid; i++)
            {
                patterns = CodecMapping.GetCodecPatterns(codecs[i]);
                if (patterns == null)
                {
                    ConsoleHelper.WriteRed("Errore: codec '" + codecs[i] + "' in " + fieldName + " non riconosciuto.");
                    ConsoleHelper.WriteYellow("Codec validi: " + CodecMapping.GetAllCodecNames());
                    valid = false;
                }
            }

            return valid;
        }

        /// <summary>
        /// Stampa il riepilogo configurazione corrente
        /// </summary>
        /// <param name="opts">Opzioni validate</param>
        /// <param name="codecPatterns">Pattern codec risolti o null</param>
        private static void PrintConfiguration(Options opts, string[] codecPatterns)
        {
            ConsoleHelper.WriteYellow("Configurazione:");
            ConsoleHelper.WritePlain("  Cartella sorgente:   " + opts.SourceFolder);
            if (string.Equals(opts.SourceFolder, opts.LanguageFolder, StringComparison.OrdinalIgnoreCase))
            {
                ConsoleHelper.WriteCyan("  Modalita':           Singola sorgente (lingua = sorgente)");
            }
            else
            {
                ConsoleHelper.WritePlain("  Cartella lingua:     " + opts.LanguageFolder);
            }
            ConsoleHelper.WritePlain("  Lingua target:       " + string.Join(", ", opts.TargetLanguage));
            ConsoleHelper.WritePlain("  Pattern matching:    " + opts.MatchPattern);
            ConsoleHelper.WritePlain("  Estensioni file:     " + string.Join(", ", opts.FileExtensions));
            if (opts.Overwrite)
            {
                ConsoleHelper.WritePlain("  Modalita' output:    Overwrite (sovrascrive sorgente)");
            }
            else
            {
                ConsoleHelper.WritePlain("  Modalita' output:    Destination");
                ConsoleHelper.WritePlain("  Cartella output:     " + opts.DestinationFolder);
            }

            // Mostra configurazione sync
            if (opts.FrameSync)
            {
                ConsoleHelper.WriteGreen("  Frame-sync:          ATTIVO");
                if (opts.AudioDelay != 0 || opts.SubtitleDelay != 0)
                {
                    ConsoleHelper.WriteDarkYellow("  Offset manuale:      Audio " + FormatDelay(opts.AudioDelay) + ", Sub " + FormatDelay(opts.SubtitleDelay) + " (sommato a frame-sync)");
                }
            }
            else
            {
                ConsoleHelper.WritePlain("  Delay audio:         " + FormatDelay(opts.AudioDelay));
                ConsoleHelper.WritePlain("  Delay sottotitoli:   " + FormatDelay(opts.SubtitleDelay));
            }

            // Mostra flag filtro
            if (opts.SubOnly)
            {
                ConsoleHelper.WriteCyan("  Solo sottotitoli:    SI (audio ignorato)");
            }
            if (opts.AudioOnly)
            {
                ConsoleHelper.WriteCyan("  Solo audio:          SI (sottotitoli ignorati)");
            }
            else if (opts.AudioCodec.Count > 0 && codecPatterns != null)
            {
                ConsoleHelper.WriteGreen("  Codec selezionato: " + string.Join(", ", opts.AudioCodec) + " -> matcha: " + string.Join(", ", codecPatterns));
            }

            // Mostra filtri tracce sorgente
            if (opts.KeepSourceAudioLangs.Count > 0)
            {
                ConsoleHelper.WritePlain("  Mantieni audio src:  " + string.Join(", ", opts.KeepSourceAudioLangs));
            }
            if (opts.KeepSourceAudioCodec.Count > 0)
            {
                ConsoleHelper.WritePlain("  Codec audio src:     " + string.Join(", ", opts.KeepSourceAudioCodec));
            }
            if (opts.KeepSourceSubtitleLangs.Count > 0)
            {
                ConsoleHelper.WritePlain("  Mantieni sub src:    " + string.Join(", ", opts.KeepSourceSubtitleLangs));
            }

            Console.WriteLine();
        }

        /// <summary>
        /// Estrae le lingue uniche delle tracce audio
        /// </summary>
        /// <param name="tracks">Lista tracce</param>
        /// <returns>Lista codici lingua unici</returns>
        private static List<string> GetAudioLanguages(List<TrackInfo> tracks)
        {
            List<string> langs = new List<string>();

            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (string.Equals(tracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        string lang = tracks[i].Language.Length > 0 ? tracks[i].Language : "und";
                        if (!langs.Contains(lang))
                        {
                            langs.Add(lang);
                        }
                    }
                }
            }

            return langs;
        }

        /// <summary>
        /// Estrae le lingue uniche delle tracce sottotitoli
        /// </summary>
        /// <param name="tracks">Lista tracce</param>
        /// <returns>Lista codici lingua unici</returns>
        private static List<string> GetSubtitleLanguages(List<TrackInfo> tracks)
        {
            List<string> langs = new List<string>();

            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (string.Equals(tracks[i].Type, "subtitles", StringComparison.OrdinalIgnoreCase))
                    {
                        string lang = tracks[i].Language.Length > 0 ? tracks[i].Language : "und";
                        if (!langs.Contains(lang))
                        {
                            langs.Add(lang);
                        }
                    }
                }
            }

            return langs;
        }

        /// <summary>
        /// Stampa il report dettagliato con tabelle
        /// </summary>
        /// <param name="records">Lista record elaborazione</param>
        /// <param name="isDryRun">Se in modalita' dry run</param>
        private static void PrintDetailedReport(List<FileProcessingRecord> records, bool isDryRun)
        {
            // Filtra solo i record elaborati con successo (o dry run)
            List<FileProcessingRecord> validRecords = new List<FileProcessingRecord>();
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Success || (isDryRun && records[i].LangFileName.Length > 0))
                {
                    validRecords.Add(records[i]);
                }
            }

            if (validRecords.Count > 0)
            {
            ConsoleHelper.WriteCyan("\n========================================");
            ConsoleHelper.WriteCyan("  Report Dettagliato");
            ConsoleHelper.WriteCyan("========================================\n");

            // Tabella 1: Source Files
            ConsoleHelper.WriteYellow("SOURCE FILES:");
            ConsoleHelper.WritePlain("  " + PadRight("Episode", 12) + PadRight("Audio", 20) + PadRight("Subtitles", 20) + PadRight("Size", 12));
            ConsoleHelper.WriteDarkGray("  " + new string('-', 64));

            for (int i = 0; i < validRecords.Count; i++)
            {
                FileProcessingRecord r = validRecords[i];
                string line = "  " + PadRight(r.EpisodeId, 12) + PadRight(FileProcessingRecord.FormatLangs(r.SourceAudioLangs), 20) + PadRight(FileProcessingRecord.FormatLangs(r.SourceSubLangs), 20) + PadRight(FileProcessingRecord.FormatSize(r.SourceSize), 12);
                ConsoleHelper.WritePlain(line);
            }

            Console.WriteLine();

            // Tabella 2: Language Files
            ConsoleHelper.WriteYellow("LANGUAGE FILES:");
            ConsoleHelper.WritePlain("  " + PadRight("Episode", 12) + PadRight("Audio", 20) + PadRight("Subtitles", 20) + PadRight("Size", 12));
            ConsoleHelper.WriteDarkGray("  " + new string('-', 64));

            for (int i = 0; i < validRecords.Count; i++)
            {
                FileProcessingRecord r = validRecords[i];
                string line = "  " + PadRight(r.EpisodeId, 12) + PadRight(FileProcessingRecord.FormatLangs(r.LangAudioLangs), 20) + PadRight(FileProcessingRecord.FormatLangs(r.LangSubLangs), 20) + PadRight(FileProcessingRecord.FormatSize(r.LangSize), 12);
                ConsoleHelper.WritePlain(line);
            }

            Console.WriteLine();

            // Tabella 3: Result Files
            ConsoleHelper.WriteYellow("RESULT FILES:");
            ConsoleHelper.WritePlain("  " + PadRight("Episode", 12) + PadRight("Audio", 15) + PadRight("Subtitles", 15) + PadRight("Size", 10) + PadRight("Delay", 12) + PadRight("FrmSync", 10) + PadRight("Speed", 10) + PadRight("Merge", 10));
            ConsoleHelper.WriteDarkGray("  " + new string('-', 94));

            for (int i = 0; i < validRecords.Count; i++)
            {
                FileProcessingRecord r = validRecords[i];
                string sizeStr = isDryRun ? "N/A" : FileProcessingRecord.FormatSize(r.ResultSize);
                string delayStr = FormatDelay(r.AudioDelayApplied);
                string frameSyncStr = r.FrameSyncTimeMs > 0 ? r.FrameSyncTimeMs + "ms" : "-";
                string speedStr = r.SpeedCorrectionTimeMs > 0 ? r.SpeedCorrectionTimeMs + "ms" : "-";
                string mergeStr = r.MergeTimeMs > 0 ? r.MergeTimeMs + "ms" : (isDryRun ? "N/A" : "-");

                string line = "  " + PadRight(r.EpisodeId, 12) + PadRight(FileProcessingRecord.FormatLangs(r.ResultAudioLangs), 15) + PadRight(FileProcessingRecord.FormatLangs(r.ResultSubLangs), 15) + PadRight(sizeStr, 10) + PadRight(delayStr, 12) + PadRight(frameSyncStr, 10) + PadRight(speedStr, 10) + PadRight(mergeStr, 10);
                ConsoleHelper.WritePlain(line);
            }

            Console.WriteLine();
            }
        }

        /// <summary>
        /// Pad a destra una stringa per allineamento tabellare
        /// </summary>
        /// <param name="text">Testo da allineare</param>
        /// <param name="width">Larghezza totale</param>
        /// <returns>Stringa con padding</returns>
        private static string PadRight(string text, int width)
        {
            string result = "";

            if (text.Length >= width)
            {
                result = text.Substring(0, width - 1) + " ";
            }
            else
            {
                result = text + new string(' ', width - text.Length);
            }

            return result;
        }

        /// <summary>
        /// Stampa il riepilogo elaborazione finale
        /// </summary>
        /// <param name="stats">Statistiche elaborazione</param>
        private static void PrintSummary(ProcessingStats stats)
        {
            ConsoleHelper.WriteCyan("\n========================================");
            ConsoleHelper.WriteCyan("  Riepilogo");
            ConsoleHelper.WriteCyan("========================================");
            ConsoleHelper.WriteGreen("  Elaborati:     " + stats.Processed);
            ConsoleHelper.WriteYellow("  Saltati:       " + stats.Skipped);
            ConsoleHelper.WriteYellow("  Senza match:   " + stats.NoMatch);
            ConsoleHelper.WriteYellow("  Senza tracce:  " + stats.NoTracks);

            if (stats.SyncFailed > 0)
            {
                ConsoleHelper.WriteYellow("  Sync falliti:  " + stats.SyncFailed);
            }

            if (stats.Errors > 0)
            {
                ConsoleHelper.WriteRed("  Errori:        " + stats.Errors);
            }
            else
            {
                ConsoleHelper.WriteGreen("  Errori:        " + stats.Errors);
            }

            Console.WriteLine();
        }

        /// <summary>
        /// Elabora un singolo file sorgente
        /// </summary>
        /// <param name="sourceFilePath">Percorso file MKV sorgente</param>
        /// <param name="languageIndex">Indice episodeId -> percorso file lingua</param>
        /// <param name="ctx">Contesto elaborazione</param>
        private static void ProcessFile(string sourceFilePath, Dictionary<string, string> languageIndex, ProcessingContext ctx)
        {
            string sourceFileName = Path.GetFileName(sourceFilePath);
            FileProcessingRecord record = new FileProcessingRecord();
            FileInfo sourceFileInfo = new FileInfo(sourceFilePath);
            string episodeId = "";
            string languageFilePath = "";
            FileInfo langFileInfo = null;
            MkvFileInfo sourceInfo = null;
            MkvFileInfo langInfo = null;
            List<TrackInfo> sourceTracks = null;
            List<TrackInfo> langTracks = null;
            ProcessingState state = new ProcessingState();
            List<int> sourceAudioIds = new List<int>();
            List<int> sourceSubIds = new List<int>();
            List<TrackInfo> audioTracks = null;
            List<TrackInfo> subtitleTracks = null;
            string tempOutput = "";
            string finalOutput = "";
            List<string> mergeArgs = null;
            bool done = false;

            // Inizializza stato delay con valori opzioni
            state.EffectiveAudioDelay = ctx.Opts.AudioDelay;
            state.EffectiveSubDelay = ctx.Opts.SubtitleDelay;
            state.StretchFactor = "";
            state.SpeedCorrectionActive = false;

            // Crea record per questo file
            record.SourceFileName = sourceFileName;
            record.SourceSize = sourceFileInfo.Length;

            ConsoleHelper.WriteDarkGray("----------------------------------------");
            ConsoleHelper.WriteWhite("Elaborazione: " + sourceFileName);

            // Estrai identificatore episodio
            episodeId = GetEpisodeIdentifier(sourceFileName, ctx.Opts.MatchPattern);

            if (episodeId.Length == 0)
            {
                ConsoleHelper.WriteYellow("  [SKIP] Impossibile estrarre ID episodio dal nome file");
                record.SkipReason = "No episode ID";
                ctx.Records.Add(record);
                ctx.Stats.Skipped++;
                done = true;
            }

            // Cerca file lingua corrispondente
            if (!done)
            {
                record.EpisodeId = episodeId;
                ConsoleHelper.WriteDarkGray("  ID Episodio: " + episodeId);

                if (!languageIndex.ContainsKey(episodeId))
                {
                    ConsoleHelper.WriteYellow("  [SKIP] Nessun file lingua corrispondente");
                    record.SkipReason = "No match";
                    ctx.Records.Add(record);
                    ctx.Stats.NoMatch++;
                    done = true;
                }
            }

            // Ottieni info tracce per entrambi i file
            if (!done)
            {
                languageFilePath = languageIndex[episodeId];
                record.LangFileName = Path.GetFileName(languageFilePath);

                langFileInfo = new FileInfo(languageFilePath);
                record.LangSize = langFileInfo.Length;

                ConsoleHelper.WriteDarkCyan("  Match: " + Path.GetFileName(languageFilePath));

                sourceInfo = ctx.Service.GetFileInfo(sourceFilePath);
                langInfo = ctx.Service.GetFileInfo(languageFilePath);
                sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;
                langTracks = (langInfo != null) ? langInfo.Tracks : null;

                record.SourceAudioLangs = GetAudioLanguages(sourceTracks);
                record.SourceSubLangs = GetSubtitleLanguages(sourceTracks);
                record.LangAudioLangs = GetAudioLanguages(langTracks);
                record.LangSubLangs = GetSubtitleLanguages(langTracks);

                if (langTracks == null)
                {
                    ConsoleHelper.WriteRed("  [ERRORE] Impossibile leggere info tracce file lingua");
                    record.SkipReason = "Track read error";
                    ctx.Records.Add(record);
                    ctx.Stats.Errors++;
                    done = true;
                }
            }

            // Rilevamento e correzione mismatch velocita'
            if (!done && sourceInfo != null && langInfo != null)
            {
                done = !HandleSpeedCorrection(record, sourceFilePath, languageFilePath, sourceInfo, langInfo, ctx, state);
            }

            // Frame-sync solo se non in modalita' correzione velocita'
            if (!done && !state.SpeedCorrectionActive && ctx.Opts.FrameSync && ctx.FrameSyncService != null)
            {
                done = !HandleFrameSync(record, sourceFilePath, languageFilePath, ctx, state);
            }

            // Ottieni ID tracce sorgente da mantenere
            if (!done && sourceTracks != null)
            {
                if (ctx.FilterSourceAudio)
                {
                    sourceAudioIds = ctx.Service.GetSourceTrackIds(sourceTracks, "audio", ctx.Opts.KeepSourceAudioLangs, ctx.SourceAudioCodecPatterns);
                    ConsoleHelper.WriteDarkYellow("\n  Audio sorgente da mantenere: " + FormatTrackIdList(sourceAudioIds));
                }
                if (ctx.FilterSourceSubs)
                {
                    sourceSubIds = ctx.Service.GetSourceTrackIds(sourceTracks, "subtitles", ctx.Opts.KeepSourceSubtitleLangs, null);
                    ConsoleHelper.WriteDarkYellow("  Sub sorgente da mantenere:   " + FormatTrackIdList(sourceSubIds));
                }
            }

            // Raccogli tracce lingua
            if (!done)
            {
                done = !CollectLanguageTracks(record, langTracks, ctx, out audioTracks, out subtitleTracks);
            }

            // Costruisci output, esegui merge e registra risultato
            if (!done)
            {
                DetermineOutputPath(sourceFilePath, ctx, out tempOutput, out finalOutput);

                // Costruisci richiesta merge
                MergeRequest mergeReq = new MergeRequest();
                mergeReq.SourceFile = sourceFilePath;
                mergeReq.LanguageFile = languageFilePath;
                mergeReq.OutputFile = tempOutput;
                mergeReq.SourceAudioIds = sourceAudioIds;
                mergeReq.SourceSubIds = sourceSubIds;
                mergeReq.LangAudioTracks = audioTracks;
                mergeReq.LangSubTracks = subtitleTracks;
                mergeReq.AudioDelayMs = state.EffectiveAudioDelay;
                mergeReq.SubDelayMs = state.EffectiveSubDelay;
                mergeReq.FilterSourceAudio = ctx.FilterSourceAudio;
                mergeReq.FilterSourceSubs = ctx.FilterSourceSubs;
                mergeReq.StretchFactor = state.StretchFactor;
                mergeArgs = ctx.Service.BuildMergeArguments(mergeReq);

                ConsoleHelper.WriteDarkGray("\n  Output: " + finalOutput);
                if (state.SpeedCorrectionActive)
                {
                    ConsoleHelper.WriteDarkGray("  Delay applicato: Audio " + FormatDelay(state.EffectiveAudioDelay) + ", Sub " + FormatDelay(state.EffectiveSubDelay) + ", stretch: " + state.StretchFactor);
                }
                else
                {
                    ConsoleHelper.WriteDarkGray("  Delay applicato: Audio " + FormatDelay(state.EffectiveAudioDelay) + ", Sub " + FormatDelay(state.EffectiveSubDelay));
                }

                record.AudioDelayApplied = state.EffectiveAudioDelay;
                record.SubDelayApplied = state.EffectiveSubDelay;
                record.ResultFileName = Path.GetFileName(finalOutput);

                CalculateResultLanguages(record, sourceTracks, sourceAudioIds, audioTracks, subtitleTracks, ctx);
                ExecuteAndRecord(record, ctx, mergeArgs, tempOutput, finalOutput);
            }
        }

        /// <summary>
        /// Gestisce rilevamento e correzione mismatch velocita'
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="sourceFilePath">Percorso file sorgente</param>
        /// <param name="languageFilePath">Percorso file lingua</param>
        /// <param name="sourceInfo">Info file sorgente</param>
        /// <param name="langInfo">Info file lingua</param>
        /// <param name="ctx">Contesto elaborazione</param>
        /// <param name="state">Stato mutabile elaborazione</param>
        /// <returns>true se elaborazione puo' continuare, false se fallita</returns>
        private static bool HandleSpeedCorrection(FileProcessingRecord record, string sourceFilePath, string languageFilePath, MkvFileInfo sourceInfo, MkvFileInfo langInfo, ProcessingContext ctx, ProcessingState state)
        {
            bool result = true;
            double detectedSourceFps = 0.0;
            double detectedLangFps = 0.0;
            bool speedMismatch = false;
            string ffmpegPath = "";
            FfmpegProvider ffmpegProvider = null;
            long sourceDefaultDuration = 0;
            long langDefaultDuration = 0;
            int sourceDurationMs = 0;
            SpeedCorrectionService speedService = null;
            bool speedOk = false;

            speedMismatch = SpeedCorrectionService.DetectSpeedMismatch(sourceInfo, langInfo, out detectedSourceFps, out detectedLangFps);

            if (speedMismatch)
            {
                ConsoleHelper.WriteCyan("\n  [SPEED] Mismatch velocita' rilevato: source " + detectedSourceFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps, lang " + detectedLangFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps");

                // Risolvi ffmpeg se non ancora disponibile
                ffmpegPath = ctx.FfmpegPath;
                if (ffmpegPath.Length == 0)
                {
                    ConsoleHelper.WriteDarkYellow("  [SPEED] Risoluzione ffmpeg per frame matching...");
                    ffmpegProvider = new FfmpegProvider(ctx.Opts.ToolsFolder);
                    if (ffmpegProvider.Resolve())
                    {
                        ffmpegPath = ffmpegProvider.FfmpegPath;
                        ConsoleHelper.WriteGreen("  [SPEED] ffmpeg trovato: " + ffmpegPath);
                    }
                    else
                    {
                        ConsoleHelper.WriteWarning("  [SPEED] ffmpeg non disponibile, correzione velocita' saltata");
                    }
                }

                if (ffmpegPath.Length > 0)
                {
                    // Trova default_duration per tracce video
                    for (int t = 0; t < sourceInfo.Tracks.Count; t++)
                    {
                        if (string.Equals(sourceInfo.Tracks[t].Type, "video", StringComparison.OrdinalIgnoreCase) && sourceInfo.Tracks[t].DefaultDurationNs > 0)
                        {
                            sourceDefaultDuration = sourceInfo.Tracks[t].DefaultDurationNs;
                            break;
                        }
                    }
                    for (int t = 0; t < langInfo.Tracks.Count; t++)
                    {
                        if (string.Equals(langInfo.Tracks[t].Type, "video", StringComparison.OrdinalIgnoreCase) && langInfo.Tracks[t].DefaultDurationNs > 0)
                        {
                            langDefaultDuration = langInfo.Tracks[t].DefaultDurationNs;
                            break;
                        }
                    }

                    // Durata sorgente in ms dal container
                    sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);

                    speedService = new SpeedCorrectionService(ffmpegPath);
                    speedOk = speedService.FindDelayAndVerify(sourceFilePath, languageFilePath, sourceDefaultDuration, langDefaultDuration, sourceDurationMs);

                    record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

                    if (speedOk)
                    {
                        state.StretchFactor = speedService.StretchFactor;
                        state.EffectiveAudioDelay = speedService.SyncDelayMs + ctx.Opts.AudioDelay;
                        state.EffectiveSubDelay = speedService.SyncDelayMs + ctx.Opts.SubtitleDelay;
                        state.SpeedCorrectionActive = true;

                        record.SpeedCorrectionApplied = true;
                        record.StretchFactor = speedService.StretchFactor;

                        ConsoleHelper.WriteGreen("  [SPEED] Correzione applicata: delay iniziale=" + speedService.InitialDelayMs + "ms, sync delay=" + speedService.SyncDelayMs + "ms, stretch=" + state.StretchFactor + " (tempo: " + speedService.ExecutionTimeMs + "ms)");
                        ConsoleHelper.WriteDarkGray("  [SPEED] Verifica: " + speedService.GetDetailSummary());
                    }
                    else
                    {
                        ConsoleHelper.WriteRed("  [SPEED] Correzione velocita' fallita, file saltato");
                        record.SkipReason = "Speed correction fallita";
                        record.Success = false;
                        ctx.Records.Add(record);
                        ctx.Stats.SyncFailed++;
                        result = false;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gestisce sincronizzazione frame-sync
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="sourceFilePath">Percorso file sorgente</param>
        /// <param name="languageFilePath">Percorso file lingua</param>
        /// <param name="ctx">Contesto elaborazione</param>
        /// <param name="state">Stato mutabile elaborazione</param>
        /// <returns>true se elaborazione puo' continuare, false se fallita</returns>
        private static bool HandleFrameSync(FileProcessingRecord record, string sourceFilePath, string languageFilePath, ProcessingContext ctx, ProcessingState state)
        {
            bool result = true;
            int frameSyncOffset = 0;

            ConsoleHelper.WriteCyan("\n  [FRAME-SYNC] Sincronizzazione tramite confronto visivo...");

            frameSyncOffset = ctx.FrameSyncService.RefineOffset(sourceFilePath, languageFilePath);
            record.FrameSyncTimeMs = ctx.FrameSyncService.FrameSyncTimeMs;

            if (frameSyncOffset != int.MinValue)
            {
                ConsoleHelper.WriteGreen("  [FRAME-SYNC] Offset: " + FormatDelay(frameSyncOffset) + " (tempo: " + ctx.FrameSyncService.FrameSyncTimeMs + "ms)");
                ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Dettaglio: " + ctx.FrameSyncService.GetDetailSummary());

                // Calcola delay effettivi con offset frame-sync
                state.EffectiveAudioDelay = frameSyncOffset + ctx.Opts.AudioDelay;
                state.EffectiveSubDelay = frameSyncOffset + ctx.Opts.SubtitleDelay;

                if (ctx.Opts.AudioDelay != 0 || ctx.Opts.SubtitleDelay != 0)
                {
                    ConsoleHelper.WriteDarkYellow("  [FRAME-SYNC] Offset finale (sync + manuale): Audio " + FormatDelay(state.EffectiveAudioDelay) + ", Sub " + FormatDelay(state.EffectiveSubDelay));
                }
            }
            else
            {
                ConsoleHelper.WriteRed("  [FRAME-SYNC] Sincronizzazione fallita");
                ctx.Stats.SyncFailed++;
                record.SkipReason = "Frame sync fallito";
                record.Success = false;
                ctx.Records.Add(record);
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Raccoglie tracce audio e sottotitoli dal file lingua
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="langTracks">Lista tracce file lingua</param>
        /// <param name="ctx">Contesto elaborazione</param>
        /// <param name="audioTracks">Tracce audio trovate</param>
        /// <param name="subtitleTracks">Tracce sottotitoli trovate</param>
        /// <returns>true se almeno una traccia trovata, false altrimenti</returns>
        private static bool CollectLanguageTracks(FileProcessingRecord record, List<TrackInfo> langTracks, ProcessingContext ctx, out List<TrackInfo> audioTracks, out List<TrackInfo> subtitleTracks)
        {
            bool result = true;
            string tl = "";
            List<TrackInfo> foundAudio = null;
            List<TrackInfo> foundSubs = null;
            string codecSuffix = "";

            audioTracks = new List<TrackInfo>();
            subtitleTracks = new List<TrackInfo>();

            for (int t = 0; t < ctx.Opts.TargetLanguage.Count; t++)
            {
                tl = ctx.Opts.TargetLanguage[t];

                // Tracce audio (a meno che SubOnly)
                if (!ctx.Opts.SubOnly)
                {
                    foundAudio = ctx.Service.GetFilteredTracks(langTracks, tl, "audio", ctx.CodecPatterns);
                    for (int a = 0; a < foundAudio.Count; a++)
                    {
                        audioTracks.Add(foundAudio[a]);
                    }
                }

                // Tracce sottotitoli (a meno che AudioOnly)
                if (!ctx.Opts.AudioOnly)
                {
                    foundSubs = ctx.Service.GetFilteredTracks(langTracks, tl, "subtitles", null);
                    for (int s = 0; s < foundSubs.Count; s++)
                    {
                        subtitleTracks.Add(foundSubs[s]);
                    }
                }
            }

            // Mostra tracce trovate
            codecSuffix = (ctx.Opts.AudioCodec.Count > 0) ? " / " + string.Join(",", ctx.Opts.AudioCodec) : "";
            ConsoleHelper.WriteMagenta("\n  Audio file lingua (" + string.Join(",", ctx.Opts.TargetLanguage) + codecSuffix + "):");
            ConsoleHelper.WritePlain(FormatTrackInfo(audioTracks));

            ConsoleHelper.WriteMagenta("\n  Sottotitoli file lingua (" + string.Join(",", ctx.Opts.TargetLanguage) + "):");
            ConsoleHelper.WritePlain(FormatTrackInfo(subtitleTracks));

            // Salta se nessuna traccia trovata
            if (audioTracks.Count == 0 && subtitleTracks.Count == 0)
            {
                ConsoleHelper.WriteYellow("\n  [SKIP] Nessuna traccia corrispondente trovata");
                record.SkipReason = "No matching tracks";
                ctx.Records.Add(record);
                ctx.Stats.NoTracks++;
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Determina percorso output temporaneo e finale
        /// </summary>
        /// <param name="sourceFilePath">Percorso file sorgente</param>
        /// <param name="ctx">Contesto elaborazione</param>
        /// <param name="tempOutput">Percorso output temporaneo</param>
        /// <param name="finalOutput">Percorso output finale</param>
        private static void DetermineOutputPath(string sourceFilePath, ProcessingContext ctx, out string tempOutput, out string finalOutput)
        {
            string sourceDir = "";
            string sourceNameNoExt = "";
            string normalizedSource = "";
            string normalizedFolder = "";
            string relativePath = "";
            string destDir = "";

            if (ctx.Opts.Overwrite)
            {
                // Usa file temp, poi sostituisci originale
                sourceDir = Path.GetDirectoryName(sourceFilePath);
                sourceNameNoExt = Path.GetFileNameWithoutExtension(sourceFilePath);
                tempOutput = Path.Combine(sourceDir, sourceNameNoExt + "_TEMP.mkv");
                finalOutput = sourceFilePath;
            }
            else
            {
                // Modalita' Destination: preserva struttura directory
                normalizedSource = NormalizePath(sourceFilePath);
                normalizedFolder = NormalizePath(ctx.Opts.SourceFolder);
                relativePath = normalizedSource.Substring(normalizedFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                finalOutput = Path.Combine(ctx.Opts.DestinationFolder, relativePath);
                tempOutput = finalOutput;

                // Crea sottodirectory destinazione se necessario
                destDir = Path.GetDirectoryName(finalOutput);
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
            }
        }

        /// <summary>
        /// Calcola le lingue audio e sottotitoli del file risultante
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="sourceTracks">Tracce file sorgente</param>
        /// <param name="sourceAudioIds">ID tracce audio sorgente da mantenere</param>
        /// <param name="audioTracks">Tracce audio importate</param>
        /// <param name="subtitleTracks">Tracce sottotitoli importate</param>
        /// <param name="ctx">Contesto elaborazione</param>
        private static void CalculateResultLanguages(FileProcessingRecord record, List<TrackInfo> sourceTracks, List<int> sourceAudioIds, List<TrackInfo> audioTracks, List<TrackInfo> subtitleTracks, ProcessingContext ctx)
        {
            List<string> resultAudioLangs = new List<string>();
            List<string> resultSubLangs = new List<string>();
            string lang = "";
            string srcLang = "";
            bool keepThis = false;

            // Audio dal sorgente
            if (!ctx.FilterSourceAudio)
            {
                for (int i = 0; i < record.SourceAudioLangs.Count; i++)
                {
                    if (!resultAudioLangs.Contains(record.SourceAudioLangs[i]))
                    {
                        resultAudioLangs.Add(record.SourceAudioLangs[i]);
                    }
                }
            }
            else if (sourceTracks != null)
            {
                // Ricava le lingue dalle tracce con ID in sourceAudioIds
                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    if (!string.Equals(sourceTracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!sourceAudioIds.Contains(sourceTracks[i].Id))
                    {
                        continue;
                    }
                    lang = sourceTracks[i].Language.Length > 0 ? sourceTracks[i].Language : "und";
                    if (!resultAudioLangs.Contains(lang))
                    {
                        resultAudioLangs.Add(lang);
                    }
                }
            }

            // Audio importate dal file lingua
            for (int i = 0; i < audioTracks.Count; i++)
            {
                lang = audioTracks[i].Language.Length > 0 ? audioTracks[i].Language : "und";
                if (!resultAudioLangs.Contains(lang))
                {
                    resultAudioLangs.Add(lang);
                }
            }

            // Sottotitoli dal sorgente
            if (!ctx.FilterSourceSubs)
            {
                for (int i = 0; i < record.SourceSubLangs.Count; i++)
                {
                    if (!resultSubLangs.Contains(record.SourceSubLangs[i]))
                    {
                        resultSubLangs.Add(record.SourceSubLangs[i]);
                    }
                }
            }
            else
            {
                // Aggiungi solo le lingue nel sorgente che sono nella lista keep
                for (int i = 0; i < record.SourceSubLangs.Count; i++)
                {
                    srcLang = record.SourceSubLangs[i];
                    keepThis = false;
                    for (int k = 0; k < ctx.Opts.KeepSourceSubtitleLangs.Count; k++)
                    {
                        if (string.Equals(srcLang, ctx.Opts.KeepSourceSubtitleLangs[k], StringComparison.OrdinalIgnoreCase))
                        {
                            keepThis = true;
                            break;
                        }
                    }
                    if (keepThis && !resultSubLangs.Contains(srcLang))
                    {
                        resultSubLangs.Add(srcLang);
                    }
                }
            }

            // Sottotitoli importati dal file lingua
            for (int i = 0; i < subtitleTracks.Count; i++)
            {
                lang = subtitleTracks[i].Language.Length > 0 ? subtitleTracks[i].Language : "und";
                if (!resultSubLangs.Contains(lang))
                {
                    resultSubLangs.Add(lang);
                }
            }

            record.ResultAudioLangs = resultAudioLangs;
            record.ResultSubLangs = resultSubLangs;
        }

        /// <summary>
        /// Esegue merge o dry-run e registra risultato
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="ctx">Contesto elaborazione</param>
        /// <param name="mergeArgs">Argomenti merge</param>
        /// <param name="tempOutput">Percorso output temporaneo</param>
        /// <param name="finalOutput">Percorso output finale</param>
        private static void ExecuteAndRecord(FileProcessingRecord record, ProcessingContext ctx, List<string> mergeArgs, string tempOutput, string finalOutput)
        {
            Stopwatch mergeStopwatch = null;
            string mergeOutput = "";
            int mergeExitCode = 0;
            FileInfo resultFileInfo = null;

            if (ctx.Opts.DryRun)
            {
                ConsoleHelper.WriteCyan("\n  [DRY-RUN] Comando che verrebbe eseguito:");
                ConsoleHelper.WriteDarkGray("  " + ctx.Service.FormatMergeCommand(mergeArgs));

                // In dry run segna come success per includerlo nel report
                record.Success = true;
                ctx.Records.Add(record);
            }
            else
            {
                ConsoleHelper.WriteYellow("\n  Unione in corso...");

                // Misura tempo merge
                mergeStopwatch = new Stopwatch();
                mergeStopwatch.Start();

                mergeExitCode = ctx.Service.ExecuteMerge(mergeArgs, out mergeOutput);

                mergeStopwatch.Stop();
                record.MergeTimeMs = mergeStopwatch.ElapsedMilliseconds;

                // Exit code 0 e 1 sono entrambi considerati successo da mkvmerge
                if (mergeExitCode == 0 || mergeExitCode == 1)
                {
                    ConsoleHelper.WriteGreen("  [OK] Unione completata");

                    // Gestisci modalita' overwrite: sostituisci originale
                    if (ctx.Opts.Overwrite)
                    {
                        File.Delete(finalOutput);
                        File.Move(tempOutput, finalOutput);
                        ConsoleHelper.WriteGreen("  [OK] File originale sostituito");
                    }

                    // Ottieni dimensione file risultato
                    if (File.Exists(finalOutput))
                    {
                        resultFileInfo = new FileInfo(finalOutput);
                        record.ResultSize = resultFileInfo.Length;
                    }

                    record.Success = true;
                    ctx.Records.Add(record);
                    ctx.Stats.Processed++;
                }
                else
                {
                    ConsoleHelper.WriteRed("  [ERRORE] mkvmerge fallito con codice " + mergeExitCode);
                    if (mergeOutput.Length > 0)
                    {
                        ConsoleHelper.WriteDarkRed("  Output: " + mergeOutput);
                    }

                    // Pulisci output temp fallito
                    if (File.Exists(tempOutput))
                    {
                        // Cleanup best-effort, errore ignorato
                        try { File.Delete(tempOutput); } catch { }
                    }

                    record.SkipReason = "Merge failed: " + mergeExitCode;
                    ctx.Records.Add(record);
                    ctx.Stats.Errors++;
                }
            }
        }

        #endregion
    }
}
