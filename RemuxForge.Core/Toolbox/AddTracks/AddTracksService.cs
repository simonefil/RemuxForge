using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Toolbox.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace RemuxForge.Core.Toolbox.AddTracks
{
    /// <summary>
    /// Aggiunge a un video la traccia di un file esterno con mkvmerge, senza ricodifica: le tracce del video restano com'erano,
    /// salvo il flag predefinita tolto a quelle del tipo di una traccia aggiunta predefinita, e la nuova prende lingua, nome,
    /// flag, ritardo e codifica indicati, in coda alle tracce dello stesso tipo. Il Matroska si scrive in un
    /// temporaneo e prende il nome finale, o sostituisce l'originale, solo se le tracce lette sul risultato sono quelle attese
    /// </summary>
    public sealed class AddTracksService
    {
        #region Costanti

        /// <summary>
        /// Codifica con cui mkvmerge legge un sottotitolo di testo senza BOM quando non gliene si indica un'altra
        /// </summary>
        public const string DEFAULT_SUBTITLE_CHARSET = "UTF-8";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Estensioni ammesse per i file di sottotitoli
        /// </summary>
        public static readonly string[] SubtitleExtensions = new string[] { ".srt", ".ass", ".ssa", ".sup", ".idx" };

        /// <summary>
        /// Estensioni ammesse per i file audio
        /// </summary>
        public static readonly string[] AudioExtensions = new string[] { ".ac3", ".eac3", ".dts", ".thd", ".aac", ".flac", ".opus", ".mka" };

        /// <summary>
        /// Ordine dei tipi di traccia nel file prodotto: la traccia aggiunta va dopo l'ultima del suo tipo o di un tipo che la precede
        /// </summary>
        private static readonly string[] s_typeOrder = new string[] { "video", "audio", "subtitles" };

        /// <summary>
        /// Sottotitoli di testo, la cui codifica si controlla prima di aggiungerli
        /// </summary>
        private static readonly string[] s_textSubtitleExtensions = new string[] { ".srt", ".ass", ".ssa" };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile mkvmerge
        /// </summary>
        private readonly string _mkvMergePath;

        /// <summary>
        /// Lettore delle tracce, sullo stesso mkvmerge che scrive
        /// </summary>
        private readonly MkvToolsService _mkvTools;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso eseguibile mkvmerge</param>
        public AddTracksService(string mkvMergePath)
        {
            this._mkvMergePath = mkvMergePath;
            this._mkvTools = new MkvToolsService(mkvMergePath);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge un file di tracce esterno: deve avere un'estensione ammessa e contenere una sola traccia audio o di sottotitoli
        /// </summary>
        /// <param name="filePath">File da leggere</param>
        /// <param name="cancellationToken">Annullamento che interrompe mkvmerge</param>
        /// <returns>Traccia del file</returns>
        /// <exception cref="InvalidOperationException">Il file non si può aggiungere; il messaggio dice perché</exception>
        /// <exception cref="OperationCanceledException">La lettura è stata annullata</exception>
        public ExternalTrack ReadTrackFile(string filePath, CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(filePath);
            string name = Path.GetFileName(fullPath);
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            string companion = FileHelper.GetCompanionPath(fullPath);
            string error = "";
            bool needsCharset = false;
            MkvFileInfo info;
            TrackInfo track;

            if (Array.IndexOf(SubtitleExtensions, extension) < 0 && Array.IndexOf(AudioExtensions, extension) < 0)
                throw new InvalidOperationException(AppText.F("toolbox.addTracks.extensionNotAllowed", extension.Length > 0 ? extension : name, string.Join(" ", SubtitleExtensions) + " " + string.Join(" ", AudioExtensions)));
            if (!File.Exists(fullPath))
                throw new InvalidOperationException(AppText.F("toolbox.addTracks.fileMissing", name));
            if (companion.Length > 0 && !File.Exists(companion))
                throw new InvalidOperationException(AppText.F("toolbox.addTracks.companionMissing", Path.GetFileName(companion)));

            // mkvmerge legge un sottotitolo di testo senza BOM come UTF-8 e scarta le battute che non lo sono: un file così vuole
            // la sua codifica. Con un BOM la codifica la dichiara il file. Un UTF-16 senza BOM (byte nulli) mkvmerge non lo legge
            // con nessuna codifica, e passerebbe da UTF-8 valido: non si aggiunge
            if (Array.IndexOf(s_textSubtitleExtensions, extension) >= 0)
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                bool hasBom = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ||
                    (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)));

                if (!hasBom)
                {
                    if (Array.IndexOf(bytes, (byte)0) >= 0)
                        throw new InvalidOperationException(AppText.F("toolbox.addTracks.utf16WithoutBom", name));
                    try
                    {
                        new UTF8Encoding(false, true).GetString(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        needsCharset = true;
                    }
                }
            }

            // mkvmerge termina senza errori anche su un file che non riconosce: nessuna traccia vuol dire file illeggibile
            info = this._mkvTools.GetFileInfoIsolated(fullPath, 0, cancellationToken, message => error = message);
            if (info == null || info.Tracks.Count == 0)
                throw new InvalidOperationException(error.Length > 0 ? error.Replace(fullPath, name) : AppText.F("toolbox.addTracks.trackUnreadable", name));
            if (info.Tracks.Count > 1)
                throw new InvalidOperationException(AppText.F("toolbox.addTracks.trackMultiple", info.Tracks.Count));

            track = info.Tracks[0];
            if (track.Type != "audio" && track.Type != "subtitles")
                throw new InvalidOperationException(AppText.F("toolbox.addTracks.trackKindNotAllowed", track.Type));

            return new ExternalTrack(fullPath, track.Id, track.Type, track.Codec ?? "", needsCharset);
        }

        /// <summary>
        /// Aggiunge la traccia esterna al video. Con la cartella di uscita il Matroska nasce lì; senza, un Matroska si sostituisce
        /// (con backup e ripristino se la sostituzione fallisce) e un altro contenitore lascia l'originale e produce un .mkv accanto.
        /// Lo stop interrompe mkvmerge senza lasciare file, e un nome prenotato non usato torna al lotto
        /// </summary>
        /// <param name="videoPath">Video a cui aggiungere la traccia</param>
        /// <param name="track">Traccia esterna letta con ReadTrackFile</param>
        /// <param name="properties">Proprietà della traccia aggiunta</param>
        /// <param name="outputFolder">Cartella di uscita, vuota per sovrascrivere</param>
        /// <param name="allocator">Allocatore dei percorsi del lotto</param>
        /// <param name="cancellationToken">Annullamento che interrompe la lettura delle tracce</param>
        /// <param name="onProgress">Avanzamento di mkvmerge in percentuale</param>
        /// <returns>Esito dell'aggiunta</returns>
        public AddTracksResult Add(string videoPath, ExternalTrack track, AddedTrackProperties properties, string outputFolder, OutputPathAllocator allocator, CancellationToken cancellationToken, Action<int> onProgress)
        {
            AddTracksResult result = new AddTracksResult();
            List<string> errors = new List<string>();
            string fullVideo = Path.GetFullPath(videoPath);
            string videoName = Path.GetFileName(fullVideo);
            string failure = string.IsNullOrEmpty(outputFolder) ? "" : OutputPathAllocator.ValidateFolder(outputFolder);
            bool replace = string.IsNullOrEmpty(outputFolder) && MediaContainerFormats.IsMatroska(fullVideo);
            bool sourceIsMatroska = MediaContainerFormats.IsMatroska(fullVideo);
            string videoError = "";
            string finalPath = "";
            List<TrackInfo> sourceTracks = new List<TrackInfo>();
            int insertAt;

            if (failure.Length == 0 && !File.Exists(fullVideo))
                failure = AppText.F("toolbox.addTracks.fileMissing", videoName);
            if (failure.Length == 0 && !File.Exists(track.Path))
                failure = AppText.F("toolbox.addTracks.fileMissing", Path.GetFileName(track.Path));
            if (failure.Length == 0)
            {
                sourceTracks = this._mkvTools.GetFileInfoIsolated(fullVideo, 0, cancellationToken, message => videoError = message)?.Tracks ?? new List<TrackInfo>();
                if (sourceTracks.Count == 0)
                    failure = videoError.Length > 0 ? videoError.Replace(fullVideo, videoName) : AppText.F("toolbox.addTracks.videoUnreadable", videoName);
            }
            if (failure.Length > 0)
            {
                result.Status = ToolboxItemStatus.Failed;
                result.Message = failure;
                return result;
            }

            // In coda al proprio tipo: dopo l'ultima traccia dello stesso tipo o di un tipo che lo precede (video, audio, sottotitoli)
            insertAt = 0;
            for (int i = 0; i < sourceTracks.Count; i++)
            {
                int rank = Array.IndexOf(s_typeOrder, sourceTracks[i].Type);
                if (rank >= 0 && rank <= Array.IndexOf(s_typeOrder, track.Type))
                    insertAt = i + 1;
            }

            try
            {
                finalPath = replace ? fullVideo : allocator.Reserve(OutputPathAllocator.GetMatroskaPath(fullVideo, outputFolder));
                StagedOutputFile output = new StagedOutputFile(finalPath, false);
                try
                {
                    MkvmergeCommand command = new MkvmergeCommand(output.TempPath);
                    List<string> videoOptions = new List<string>();
                    List<string> trackOptions = new List<string>();
                    string id = track.TrackId.ToString(CultureInfo.InvariantCulture);
                    int reference = 0;
                    ProcessResult run;
                    List<TrackInfo> outputTracks;

                    // Le opzioni valgono solo per la traccia del file esterno; lingua e nome vuoti lasciano quelli del file. Dal file
                    // esterno arriva la traccia soltanto: capitoli, allegati e tag globali di un .mka restano quelli del video
                    trackOptions.AddRange(new string[] { "--no-chapters", "--no-attachments", "--no-global-tags" });
                    if (properties.Language.Length > 0)
                        trackOptions.AddRange(new string[] { "--language", id + ":" + properties.Language });
                    if (properties.Name.Length > 0)
                        trackOptions.AddRange(new string[] { "--track-name", id + ":" + properties.Name });
                    trackOptions.AddRange(new string[] { "--default-track-flag", id + ":" + (properties.IsDefault ? "yes" : "no"), "--forced-display-flag", id + ":" + (properties.IsForced ? "yes" : "no") });
                    if (properties.DelayMs != 0)
                        trackOptions.AddRange(new string[] { "--sync", id + ":" + properties.DelayMs.ToString(CultureInfo.InvariantCulture) });
                    // La codifica scelta vale solo per un sottotitolo che non è UTF-8 e non ha BOM: un file UTF-8 la subirebbe come
                    // testo da convertire, e con un BOM mkvmerge segue il BOM
                    if (track.NeedsCharset && properties.SubtitleCharset != DEFAULT_SUBTITLE_CHARSET)
                        trackOptions.AddRange(new string[] { "--sub-charset", id + ":" + properties.SubtitleCharset });

                    // Una traccia aggiunta predefinita è l'unica del suo tipo: le tracce del video dello stesso tipo perdono il flag
                    if (properties.IsDefault)
                    {
                        foreach (TrackInfo sourceTrack in sourceTracks.FindAll(t => t.Type == track.Type))
                            videoOptions.AddRange(new string[] { "--default-track-flag", sourceTrack.Id.ToString(CultureInfo.InvariantCulture) + ":0" });
                    }
                    int videoInput = command.AddInput(fullVideo, videoOptions.ToArray());
                    int trackInput = command.AddInput(track.Path, trackOptions.ToArray());

                    for (int i = 0; i <= sourceTracks.Count; i++)
                    {
                        if (i == insertAt)
                            command.Place(reference++, trackInput, track.TrackId);
                        if (i < sourceTracks.Count)
                            command.Place(reference++, videoInput, sourceTracks[i].Id);
                    }
                    command.AddTrackOrder();
                    // Un Matroska tiene la sua traccia MPEG-4 com'è: native_mpeg4 la riscriverebbe
                    if (!sourceIsMatroska && MediaContainerNormalizer.NeedsNativeMpeg4(sourceTracks))
                        command.AddGlobalOptions("--engage", "native_mpeg4");

                    run = MkvToolNixGuiMode.Run(this._mkvMergePath, command.ToArguments(), onProgress, result.Warnings.Add, errors);

                    if (result.StopIfRequested())
                        return result;

                    // In riga il motivo senza percorsi: mkvmerge li cita completi, il dettaglio li conserva
                    if (MkvToolNixGuiMode.Failed(run, errors, AppText.F("toolbox.addTracks.exitCode", run.ExitCode)))
                    {
                        result.Fail(errors, message => message.Replace(fullVideo, videoName).Replace(track.Path, Path.GetFileName(track.Path)).Replace(output.TempPath, Path.GetFileName(finalPath)));
                        return result;
                    }

                    // Il risultato deve avere le tracce del video, nello stesso ordine, più quella aggiunta al suo posto; da un Matroska
                    // anche lingua, nome e flag delle tracce del video restano gli stessi, salvo il predefinito tolto a quelle del tipo
                    // della traccia aggiunta predefinita
                    outputTracks = this._mkvTools.GetFileInfoIsolated(output.TempPath, 0, cancellationToken, null)?.Tracks ?? new List<TrackInfo>();
                    bool expected = outputTracks.Count == sourceTracks.Count + 1 && outputTracks[insertAt].Type == track.Type && outputTracks[insertAt].Codec == track.Codec;
                    for (int i = 0; expected && i < sourceTracks.Count; i++)
                    {
                        TrackInfo before = sourceTracks[i];
                        TrackInfo after = outputTracks[i < insertAt ? i : i + 1];
                        bool defaultCleared = properties.IsDefault && before.Type == track.Type;
                        expected = before.Type == after.Type && (!sourceIsMatroska || (before.Codec == after.Codec &&
                            LanguageValidator.NormalizeToIso6392(before.Language) == LanguageValidator.NormalizeToIso6392(after.Language) &&
                            (before.Name ?? "") == (after.Name ?? "") && (defaultCleared || before.DefaultTrack == after.DefaultTrack) && before.ForcedTrack == after.ForcedTrack));
                    }
                    if (!expected)
                    {
                        result.Status = ToolboxItemStatus.Failed;
                        result.Message = AppText.T("toolbox.addTracks.tracksChanged");
                        return result;
                    }

                    // Predefinita vuol dire l'unica predefinita del suo tipo; si guarda il risultato, che da MP4, AVI o TS è l'unico
                    // posto in cui i flag si leggono
                    TrackInfo added = outputTracks[insertAt];
                    if (properties.IsDefault && (!added.DefaultTrack || outputTracks.Exists(t => t != added && t.Type == track.Type && t.DefaultTrack)))
                    {
                        result.Status = ToolboxItemStatus.Failed;
                        result.Message = AppText.T("toolbox.addTracks.defaultNotUnique");
                        return result;
                    }

                    // Un nome finale che non si riesce a dare, o un originale che non si riesce a sostituire, lasciano l'originale com'era
                    try
                    {
                        // Un backup che non si cancella non toglie nulla al risultato: diventa un avviso, con il percorso
                        if (replace)
                            result.Warnings.AddRange(output.CommitReplacing(fullVideo));
                        else
                            output.Commit();
                    }
                    catch (IOException ex)
                    {
                        result.Status = ToolboxItemStatus.Failed;
                        result.Message = ex.Message;
                        return result;
                    }

                    result.Complete(finalPath);
                    result.Replaced = replace;
                }
                finally
                {
                    // Un temporaneo che non si cancella resta sul disco: la riga lo dice, con il percorso
                    result.AddLeftovers(output.Discard());
                }
            }
            catch (IOException ex)
            {
                // Cartella non scrivibile alla creazione del temporaneo
                result.Status = ToolboxItemStatus.Failed;
                result.Message = ex.Message;
            }
            finally
            {
                if (!replace && finalPath.Length > 0 && result.OutputPath.Length == 0)
                    allocator.Release(finalPath);
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Traccia di un file esterno da aggiungere
    /// </summary>
    public sealed class ExternalTrack
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="path">Percorso completo del file</param>
        /// <param name="trackId">ID della traccia nel file, quello di mkvmerge</param>
        /// <param name="type">Tipo della traccia secondo mkvmerge: audio o subtitles</param>
        /// <param name="codec">Codec della traccia secondo mkvmerge</param>
        /// <param name="needsCharset">True per un sottotitolo di testo senza BOM che non è UTF-8</param>
        public ExternalTrack(string path, int trackId, string type, string codec, bool needsCharset)
        {
            this.Path = path;
            this.TrackId = trackId;
            this.Type = type;
            this.Codec = codec;
            this.NeedsCharset = needsCharset;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// ID della traccia nel file, quello di mkvmerge
        /// </summary>
        public int TrackId { get; }

        /// <summary>
        /// Tipo della traccia secondo mkvmerge: audio o subtitles
        /// </summary>
        public string Type { get; }

        /// <summary>
        /// Codec della traccia secondo mkvmerge
        /// </summary>
        public string Codec { get; }

        /// <summary>
        /// True per un sottotitolo di testo senza BOM che non è UTF-8: mkvmerge lo legge solo con la sua codifica
        /// </summary>
        public bool NeedsCharset { get; }

        /// <summary>
        /// True se la traccia è di sottotitoli
        /// </summary>
        public bool IsSubtitles
        {
            get { return this.Type == "subtitles"; }
        }

        #endregion
    }

    /// <summary>
    /// Proprietà date alla traccia aggiunta, uguali per tutti i video del lotto
    /// </summary>
    public sealed class AddedTrackProperties
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="language">Lingua ISO 639-2, vuota per lasciare quella del file</param>
        /// <param name="name">Nome della traccia, vuoto per lasciare quello del file</param>
        /// <param name="isDefault">True per segnarla predefinita</param>
        /// <param name="isForced">True per segnarla forzata</param>
        /// <param name="delayMs">Ritardo in millisecondi, negativo per anticiparla</param>
        /// <param name="subtitleCharset">Codifica dei sottotitoli di testo che non sono UTF-8, come la nomina mkvmerge</param>
        public AddedTrackProperties(string language, string name, bool isDefault, bool isForced, int delayMs, string subtitleCharset)
        {
            this.SubtitleCharset = string.IsNullOrEmpty(subtitleCharset) ? AddTracksService.DEFAULT_SUBTITLE_CHARSET : subtitleCharset;
            this.Language = language ?? "";
            this.Name = name ?? "";
            this.IsDefault = isDefault;
            this.IsForced = isForced;
            this.DelayMs = delayMs;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Lingua ISO 639-2, vuota per lasciare quella del file
        /// </summary>
        public string Language { get; }

        /// <summary>
        /// Nome della traccia, vuoto per lasciare quello del file
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// True se la traccia è predefinita
        /// </summary>
        public bool IsDefault { get; }

        /// <summary>
        /// True se la traccia è forzata
        /// </summary>
        public bool IsForced { get; }

        /// <summary>
        /// Ritardo in millisecondi, negativo per anticiparla
        /// </summary>
        public int DelayMs { get; }

        /// <summary>
        /// Codifica dei sottotitoli di testo che non sono UTF-8, come la nomina mkvmerge
        /// </summary>
        public string SubtitleCharset { get; }

        #endregion
    }

    /// <summary>
    /// Esito dell'aggiunta di una traccia a un video
    /// </summary>
    public sealed class AddTracksResult : ToolboxItemResult
    {
        #region Proprietà

        /// <summary>
        /// True se il Matroska scritto ha sostituito l'originale
        /// </summary>
        public bool Replaced { get; set; }

        #endregion
    }
}
