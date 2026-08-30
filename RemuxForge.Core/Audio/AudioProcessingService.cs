using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace RemuxForge.Core.Audio
{
    /// <summary>
    /// Gestisce tutto il processing audio che produce file temporanei per il merge
    /// </summary>
    public class AudioProcessingService
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso dell'eseguibile ffmpeg
        /// </summary>
        private readonly string _ffmpegPath;

        /// <summary>
        /// Cartella per i file temporanei audio
        /// </summary>
        private readonly string _tempFolder;

        /// <summary>
        /// Servizio usato per leggere i metadati dei file audio prodotti
        /// </summary>
        private readonly MkvToolsService _mkvToolsService;

        /// <summary>
        /// Pianificatore delle operazioni audio
        /// </summary>
        private readonly AudioProcessingPlanner _planner;

        /// <summary>
        /// File audio finali creati durante il processing corrente
        /// </summary>
        private readonly List<string> _createdFiles;

        /// <summary>
        /// File temporanei intermedi creati durante il processing corrente
        /// </summary>
        private readonly List<string> _transientFiles;

        /// <summary>
        /// Sincronizzazione per le raccolte condivise tra i render paralleli
        /// </summary>
        private readonly object _lock;

        /// <summary>
        /// Ultimo errore ffmpeg associato al contesto asincrono corrente
        /// </summary>
        private readonly System.Threading.AsyncLocal<string> _lastFfmpegError;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso dell'eseguibile ffmpeg</param>
        /// <param name="tempFolder">Cartella per i file temporanei audio</param>
        /// <param name="mkvToolsService">Servizio per leggere i metadati dei file MKV</param>
        public AudioProcessingService(string ffmpegPath, string tempFolder, MkvToolsService mkvToolsService)
        {
            this._ffmpegPath = ffmpegPath;
            this._tempFolder = tempFolder;
            this._mkvToolsService = mkvToolsService;
            this._planner = new AudioProcessingPlanner(mkvToolsService, ffmpegPath);
            this._createdFiles = new List<string>();
            this._transientFiles = new List<string>();
            this._lock = new object();
            this._lastFfmpegError = new System.Threading.AsyncLocal<string>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Processa le tracce audio richieste
        /// </summary>
        /// <param name="request">Richiesta completa di processing audio</param>
        /// <returns>Risultato complessivo del processing audio</returns>
        public AudioProcessingResult Process(AudioProcessingRequest request)
        {
            AudioProcessingResult result = new AudioProcessingResult();
            List<AudioTrackJob> jobs = new List<AudioTrackJob>();
            AudioProcessingPlan plan;
            int maxParallel;
            string errorMessage;

            if (request == null || request.Options == null || request.Record == null)
            {
                result.Success = false;
                result.ErrorMessage = "Richiesta processing audio non valida";
                return result;
            }

            if ((request.SourceTracksToProcess == null || request.SourceTracksToProcess.Count == 0) &&
                (request.LangTracksToProcess == null || request.LangTracksToProcess.Count == 0))
            {
                result.Success = true;
                result.EffectiveAudioDelayMs = request.EffectiveAudioDelayMs;
                return result;
            }

            if (string.IsNullOrEmpty(request.Options.AudioFormat))
            {
                errorMessage = "Processing audio richiesto ma formato audio non impostato";
                ConsoleHelper.Write(LogSection.Conv, LogLevel.Error, "  " + errorMessage);
                request.Record.ErrorMessage = errorMessage;
                request.Record.Status = FileStatus.Error;
                result.Success = false;
                result.ErrorMessage = errorMessage;
                return result;
            }

            plan = request.Plan != null ? request.Plan : this._planner.BuildPlan(request, true);
            request.Plan = plan;
            request.Record.AudioProcessingPreview = plan;

            // La richiesta può contenere tracce source e lang: da qui in poi ogni job è indipendente
            if (plan.SourceTracks != null)
            {
                for (int i = 0; i < plan.SourceTracks.Count; i++)
                {
                    jobs.Add(new AudioTrackJob(true, plan.SourceTracks[i].Track, plan.SourceTracks[i].GenericProcessing, plan.SourceTracks[i]));
                }
            }

            if (plan.LangTracks != null)
            {
                for (int i = 0; i < plan.LangTracks.Count; i++)
                {
                    jobs.Add(new AudioTrackJob(false, plan.LangTracks[i].Track, plan.LangTracks[i].GenericProcessing, plan.LangTracks[i]));
                }
            }
            if (jobs.Count == 0)
            {
                result.Success = true;
                result.EffectiveAudioDelayMs = request.EffectiveAudioDelayMs;
                return result;
            }

            this.LogAudioProcessingPlan(request, jobs);

            maxParallel = ParallelismHelper.ResolveDefaultMaxDegree();

            try
            {
                // I render audio non condividono stato ffmpeg; si sincronizza solo la raccolta risultati
                Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = maxParallel }, job =>
                {
                    AudioTrackProcessResult trackResult = this.ProcessJob(request, job);
                    lock (this._lock)
                    {
                        if (!trackResult.Success && result.Success)
                        {
                            result.Success = false;
                            result.ErrorMessage = trackResult.ErrorMessage;
                        }
                        else if (trackResult.Success && !string.IsNullOrEmpty(trackResult.OutputFile))
                        {
                            if (job.IsSource)
                            {
                                result.SourceOutputFiles[job.Track.Id] = trackResult.OutputFile;
                                result.SourceOutputInfo[job.Track.Id] = trackResult.OutputInfo;
                            }
                            else
                            {
                                result.LangOutputFiles[job.Track.Id] = trackResult.OutputFile;
                                result.LangOutputInfo[job.Track.Id] = trackResult.OutputInfo;
                                if (trackResult.BypassAudioDelay)
                                {
                                    result.AudioDelayBypassedLangIds.Add(job.Track.Id);
                                }
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "Processing audio fallito: " + ex.Message;
            }

            if (!result.Success)
            {
                this.DeleteCreatedFiles();
                request.Record.ErrorMessage = result.ErrorMessage;
                request.Record.Status = FileStatus.Error;
                return result;
            }

            if (plan.LangTracks != null)
            {
                for (int i = 0; i < plan.LangTracks.Count; i++)
                {
                    AudioTrackProcessingPlan requiredPlan = plan.LangTracks[i];
                    if (requiredPlan.RenderRequired &&
                        (requiredPlan.Track == null ||
                         !result.LangOutputFiles.ContainsKey(requiredPlan.Track.Id) ||
                         string.IsNullOrEmpty(result.LangOutputFiles[requiredPlan.Track.Id]) ||
                         !File.Exists(result.LangOutputFiles[requiredPlan.Track.Id])))
                    {
                        result.Success = false;
                        result.ErrorMessage = "Output audio Language obbligatorio mancante per track " + (requiredPlan.Track != null ? requiredPlan.Track.Id.ToString(CultureInfo.InvariantCulture) : "?");
                        this.DeleteCreatedFiles();
                        request.Record.ErrorMessage = result.ErrorMessage;
                        request.Record.Status = FileStatus.Error;
                        return result;
                    }
                }
            }

            result.Success = true;
            result.EffectiveAudioDelayMs = request.EffectiveAudioDelayMs;
            this.DeleteTransientFiles();
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Processa una singola traccia audio scegliendo il flusso operativo necessario
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="job">Job traccia da processare</param>
        /// <returns>Risultato della traccia processata</returns>
        private AudioTrackProcessResult ProcessJob(AudioProcessingRequest request, AudioTrackJob job)
        {
            AudioTrackProcessResult result = new AudioTrackProcessResult();
            AudioTrackProcessingPlan trackPlan = job.Plan;
            AudioSourceFillPlan fillPlan = trackPlan != null ? trackPlan.SourceFillPlan : null;
            TrackInfo sourceFillTrack = trackPlan != null ? trackPlan.SourceFillTrack : null;
            string outputFile;

            if (job.Track == null)
            {
                result.ErrorMessage = "Processing audio fallito: traccia non valida";
                return result;
            }

            if (CodecMapping.IsSpatialCodec(job.Track))
            {
                result.ErrorMessage = "Traccia audio spaziale/object selezionata per processing: track " + job.Track.Id + " (" + job.Track.Codec + ")";
                return result;
            }

            if (trackPlan != null && !string.IsNullOrEmpty(trackPlan.ErrorMessage))
            {
                result.ErrorMessage = trackPlan.ErrorMessage;
                return result;
            }

            outputFile = this.CreateOutputPath(request.Record, job.Track, request.Options.AudioFormat, job.IsSource ? "src" : "lang");

            // Priorità: source fill modifica la timeline completa, poi EditMap deep-analysis, infine conversione semplice
            if (trackPlan != null && trackPlan.SourceFillHasWork)
            {
                if (sourceFillTrack == null)
                {
                    result.ErrorMessage = "Audio source fill fallito: tracce non valide per lang track " + job.Track.Id;
                    return result;
                }
                if (!this.ProcessSourceFill(request, sourceFillTrack, job.Track, fillPlan, outputFile, result))
                {
                    return result;
                }
            }
            else if (trackPlan != null && trackPlan.DeepEditRender)
            {
                if (!this.ProcessEditMap(request, job.Track, outputFile, result))
                {
                    return result;
                }
            }
            else if (trackPlan != null && trackPlan.RenderRequired)
            {
                if (!this.ProcessSimple(request, job.IsSource ? request.SourceFilePath : request.LanguageFilePath, job.Track, this.FormatAudioTrackLabel(job.IsSource, job.Track), trackPlan, outputFile, result))
                {
                    return result;
                }
            }
            else
            {
                result.Success = true;
                ConsoleHelper.Write(LogSection.Conv, LogLevel.Notice, "  " + this.FormatAudioTrackLabel(job.IsSource, job.Track) + " già nel formato richiesto, processing saltato");
                return result;
            }

            result.OutputFile = outputFile;
            result.OutputInfo = this.ResolveOutputInfo(outputFile, job.Track, request.Options);
            result.Success = true;
            ConsoleHelper.Write(LogSection.Conv, LogLevel.Success, "  " + this.FormatAudioTrackLabel(job.IsSource, job.Track) + " -> " + Utils.FormatAudioFormat(request.Options.AudioFormat) + " (" + Path.GetFileName(outputFile) + ")");
            return result;
        }

        /// <summary>
        /// Esegue conversione o post-processing audio senza modifiche di timeline
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="inputFile">File di input</param>
        /// <param name="track">Traccia da processare</param>
        /// <param name="trackLabel">Etichetta traccia da usare nei log</param>
        /// <param name="trackPlan">Piano di elaborazione della traccia</param>
        /// <param name="outputFile">File audio temporaneo finale</param>
        /// <param name="result">Risultato della traccia</param>
        /// <returns>True se ffmpeg ha prodotto il file finale</returns>
        private bool ProcessSimple(AudioProcessingRequest request, string inputFile, TrackInfo track, string trackLabel, AudioTrackProcessingPlan trackPlan, string outputFile, AudioTrackProcessResult result)
        {
            List<string> args;
            string tempFile;
            double gainDb;

            ConsoleHelper.Write(LogSection.Conv, LogLevel.Notice, "  Processing " + trackLabel);

            if (request.Options.AudioPeakNormalize)
            {
                // La normalizzazione peak richiede un render temporaneo completo per misurare il picco reale
                tempFile = this.RenderSimpleTemp(request, inputFile, track, trackPlan);
                if (string.IsNullOrEmpty(tempFile))
                {
                    result.ErrorMessage = "Peak normalization fallita: impossibile creare temp audio per track " + track.Id + this.FormatLastFfmpegError();
                    return false;
                }
                if (!this.MeasurePeakGain(tempFile, request.Options.AudioPeakTargetDb, out gainDb))
                {
                    result.ErrorMessage = "Peak normalization fallita: peak non rilevato per track " + track.Id;
                    return false;
                }
                args = this.BuildEncodeFromTempArgs(tempFile, track, request.Options, outputFile, gainDb);
            }
            else
            {
                args = new List<string>();
                args.Add("-nostdin");
                args.Add("-hide_banner");
                args.Add("-y");
                args.Add("-i");
                args.Add(inputFile);
                args.Add("-map");
                args.Add("0:" + track.Id);
                args.Add("-af");
                args.Add(this.BuildSimpleFilter(track, request.Options, trackPlan, false));
                this.AddCodecArgs(args, track, request.Options);
                args.Add(outputFile);
            }

            return this.RunFfmpeg(args, outputFile, result);
        }

        /// <summary>
        /// Renderizza una traccia lang applicando le operazioni EditMap della deep-analysis
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="track">Traccia lang da processare</param>
        /// <param name="outputFile">File audio temporaneo finale</param>
        /// <param name="result">Risultato della traccia</param>
        /// <returns>True se il render e l'eventuale normalizzazione sono riusciti</returns>
        private bool ProcessEditMap(AudioProcessingRequest request, TrackInfo track, string outputFile, AudioTrackProcessResult result)
        {
            List<string> args;
            string tempFile;
            double gainDb;

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, AppText.F("deep.temporal.audio.render", this.FormatAudioTrackLabel(false, track), request.LangEditMap.Operations.Count));

            if (request.Options.AudioPeakNormalize)
            {
                // L'EditMap viene renderizzata prima in PCM temporaneo: solo dopo si misura il peak
                tempFile = this.CreatePeakTempPath(request.Record, track);
                args = this.BuildEditMapArgs(request.LanguageFilePath, track, request.LangEditMap, request.Options, request.Plan.FindLangTrack(track.Id), tempFile, true);
                this.AddPeakTempCodecArgsBeforeOutput(args);
                tempFile = this.RunFfmpegToTemp(args, tempFile) ? tempFile : "";
                if (string.IsNullOrEmpty(tempFile))
                {
                    result.ErrorMessage = "Deep audio render fallito su temp track " + track.Id + this.FormatLastFfmpegError();
                    return false;
                }
                if (!this.MeasurePeakGain(tempFile, request.Options.AudioPeakTargetDb, out gainDb))
                {
                    result.ErrorMessage = "Peak normalization fallita: peak non rilevato per track " + track.Id;
                    return false;
                }
                args = this.BuildEncodeFromTempArgs(tempFile, track, request.Options, outputFile, gainDb);
            }
            else
            {
                args = this.BuildEditMapArgs(request.LanguageFilePath, track, request.LangEditMap, request.Options, request.Plan.FindLangTrack(track.Id), outputFile, false);
            }

            return this.RunFfmpeg(args, outputFile, result);
        }

        /// <summary>
        /// Renderizza una traccia lang sostituendo le parti mancanti con audio source
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="sourceTrack">Traccia source usata per riempire i gap</param>
        /// <param name="langTrack">Traccia lang da processare</param>
        /// <param name="plan">Piano source fill calcolato</param>
        /// <param name="outputFile">File audio temporaneo finale</param>
        /// <param name="result">Risultato della traccia</param>
        /// <returns>True se il render e l'eventuale normalizzazione sono riusciti</returns>
        private bool ProcessSourceFill(AudioProcessingRequest request, TrackInfo sourceTrack, TrackInfo langTrack, AudioSourceFillPlan plan, string outputFile, AudioTrackProcessResult result)
        {
            List<string> args;
            string tempFile;
            double gainDb;

            ConsoleHelper.Write(LogSection.Conv, LogLevel.Notice, "  Audio source fill " + this.FormatAudioTrackLabel(false, langTrack) + " da " + this.FormatAudioTrackLabel(true, sourceTrack));

            if (request.Options.AudioPeakNormalize)
            {
                // Anche il source fill va misurato dopo il concat, altrimenti il target peak sarebbe parziale
                tempFile = this.CreatePeakTempPath(request.Record, langTrack);
                args = this.BuildSourceFillArgs(request, sourceTrack, langTrack, plan, tempFile, true);
                this.AddPeakTempCodecArgsBeforeOutput(args);
                tempFile = this.RunFfmpegToTemp(args, tempFile) ? tempFile : "";
                if (string.IsNullOrEmpty(tempFile))
                {
                    result.ErrorMessage = "Audio source fill fallito su temp track " + langTrack.Id + this.FormatLastFfmpegError();
                    return false;
                }
                if (!this.MeasurePeakGain(tempFile, request.Options.AudioPeakTargetDb, out gainDb))
                {
                    result.ErrorMessage = "Peak normalization fallita: peak non rilevato per track " + langTrack.Id;
                    return false;
                }
                args = this.BuildEncodeFromTempArgs(tempFile, langTrack, request.Options, outputFile, gainDb);
            }
            else
            {
                args = this.BuildSourceFillArgs(request, sourceTrack, langTrack, plan, outputFile, false);
            }

            if (plan.StartFillMs > 0 || plan.MaterializesExternalSync)
            {
                result.BypassAudioDelay = true;
            }

            return this.RunFfmpeg(args, outputFile, result);
        }

        /// <summary>
        /// Renderizza una traccia in PCM temporaneo per misurazione peak
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="inputFile">File di input</param>
        /// <param name="track">Traccia da renderizzare</param>
        /// <param name="trackPlan">Piano di elaborazione della traccia</param>
        /// <returns>Path del file temporaneo, oppure stringa vuota se fallisce</returns>
        private string RenderSimpleTemp(AudioProcessingRequest request, string inputFile, TrackInfo track, AudioTrackProcessingPlan trackPlan)
        {
            string tempFile = this.CreatePeakTempPath(request.Record, track);
            List<string> args = new List<string>();

            args.Add("-nostdin");
            args.Add("-hide_banner");
            args.Add("-y");
            args.Add("-i");
            args.Add(inputFile);
            args.Add("-map");
            args.Add("0:" + track.Id);
            args.Add("-af");
            args.Add(this.BuildSimpleFilter(track, request.Options, trackPlan, true));
            this.AddPeakTempCodecArgs(args);
            args.Add(tempFile);

            return this.RunFfmpegToTemp(args, tempFile) ? tempFile : "";
        }

        /// <summary>
        /// Costruisce gli argomenti ffmpeg per render EditMap
        /// </summary>
        /// <param name="inputFile">File lang di input</param>
        /// <param name="track">Traccia lang da renderizzare</param>
        /// <param name="editMap">Mappa operazioni deep-analysis</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="trackPlan">Piano di elaborazione della traccia</param>
        /// <param name="outputFile">File di output</param>
        /// <param name="forPeakTemp">True se l'output è un PCM temporaneo per peak</param>
        /// <returns>Lista argomenti ffmpeg</returns>
        private List<string> BuildEditMapArgs(string inputFile, TrackInfo track, EditMap editMap, Options options, AudioTrackProcessingPlan trackPlan, string outputFile, bool forPeakTemp)
        {
            List<string> args = new List<string>();
            string filter = this.BuildEditMapFilter(track, editMap, options, trackPlan, forPeakTemp);

            args.Add("-nostdin");
            args.Add("-hide_banner");
            args.Add("-y");
            args.Add("-i");
            args.Add(inputFile);
            args.Add("-filter_complex");
            args.Add(filter);
            args.Add("-map");
            args.Add("[outa]");
            if (!forPeakTemp)
            {
                this.AddCodecArgs(args, track, options);
            }
            args.Add(outputFile);

            return args;
        }

        /// <summary>
        /// Costruisce gli argomenti ffmpeg per render source fill
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="sourceTrack">Traccia source usata per riempire i gap</param>
        /// <param name="langTrack">Traccia lang da renderizzare</param>
        /// <param name="plan">Piano source fill calcolato</param>
        /// <param name="outputFile">File di output</param>
        /// <param name="forPeakTemp">True se l'output è un PCM temporaneo per peak</param>
        /// <returns>Lista argomenti ffmpeg</returns>
        private List<string> BuildSourceFillArgs(AudioProcessingRequest request, TrackInfo sourceTrack, TrackInfo langTrack, AudioSourceFillPlan plan, string outputFile, bool forPeakTemp)
        {
            List<string> args = new List<string>();
            string filter = this.BuildSourceFillFilter(sourceTrack, langTrack, plan, request.Options, forPeakTemp);

            args.Add("-nostdin");
            args.Add("-hide_banner");
            args.Add("-y");
            args.Add("-i");
            args.Add(request.SourceFilePath);
            args.Add("-i");
            args.Add(request.LanguageFilePath);
            args.Add("-filter_complex");
            args.Add(filter);
            args.Add("-map");
            args.Add("[outa]");
            if (!forPeakTemp)
            {
                this.AddCodecArgs(args, langTrack, request.Options);
            }
            args.Add(outputFile);

            return args;
        }

        /// <summary>
        /// Costruisce il filtro ffmpeg concat per applicare tagli e silenzi dell'EditMap
        /// </summary>
        /// <param name="track">Traccia lang da filtrare</param>
        /// <param name="editMap">Mappa operazioni deep-analysis</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="trackPlan">Piano di elaborazione della traccia</param>
        /// <param name="forPeakTemp">True se il filtro produce PCM temporaneo per peak</param>
        /// <returns>Filtro ffmpeg completo con output [outa]</returns>
        private string BuildEditMapFilter(TrackInfo track, EditMap editMap, Options options, AudioTrackProcessingPlan trackPlan, bool forPeakTemp)
        {
            List<AudioFilterSegment> segments = new List<AudioFilterSegment>();
            int currentLangMs = 0;

            for (int i = 0; i < editMap.Operations.Count; i++)
            {
                EditOperation operation = editMap.Operations[i];
                if (operation.LangTimestampMs > currentLangMs)
                {
                    // Copia la parte lang valida fino al prossimo punto operativo
                    this.AddTimelineIntervalSegments(segments, 0, track, trackPlan.InitialTimelineOffsetMs, currentLangMs, operation.LangTimestampMs, 1.0, 1.0);
                }

                if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                {
                    // INSERT_SILENCE allunga la timeline lang con audio muto del formato corretto
                    segments.Add(new AudioFilterSegment(0, track.Id, 0, operation.DurationMs, true));
                    currentLangMs = operation.LangTimestampMs;
                }
                else if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    // CUT_SEGMENT salta la finestra lang indicata e riprende dal bordo successivo
                    currentLangMs = operation.LangTimestampMs + operation.DurationMs;
                }
                else
                {
                    currentLangMs = operation.LangTimestampMs;
                }
            }

            // Aggiunge la coda lang non coperta da operazioni esplicite
            this.AddTimelineIntervalSegments(segments, 0, track, trackPlan.InitialTimelineOffsetMs, currentLangMs, -1, 1.0, 1.0);

            return this.BuildConcatFilter(segments, track, options, forPeakTemp, 0, trackPlan != null ? trackPlan.AudioTempoFilter : "");
        }

        /// <summary>
        /// Costruisce il filtro ffmpeg concat per combinare audio lang e porzioni source con ID di stream espliciti
        /// </summary>
        /// <param name="sourceTrack">Traccia source nell'input</param>
        /// <param name="langTrack">Traccia language da usare per il filtro</param>
        /// <param name="plan">Piano source fill calcolato</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="forPeakTemp">True se il filtro produce PCM temporaneo per peak</param>
        /// <returns>Filtro ffmpeg completo con output [outa]</returns>
        private string BuildSourceFillFilter(TrackInfo sourceTrack, TrackInfo langTrack, AudioSourceFillPlan plan, Options options, bool forPeakTemp)
        {
            List<AudioFilterSegment> segments = new List<AudioFilterSegment>();
            int currentLangMs = 0;

            if (plan.StartFillMs > 0)
            {
                // Delay positivo: l'inizio mancante viene preso dalla traccia source
                this.AddTimelineIntervalSegments(segments, 0, sourceTrack, plan.SourceInitialTimelineOffsetMs, 0, plan.StartFillMs, 1.0, 1.0);
            }
            else if (plan.InitialSilenceMs > 0)
            {
                // Se lo stretch viene materializzato nel render, anche il delay va incorporato nel file
                segments.Add(new AudioFilterSegment(1, langTrack.Id, 0, plan.InitialSilenceMs, true));
            }

            plan.InsertOperations.Sort((a, b) => a.LangTimestampMs.CompareTo(b.LangTimestampMs));
            for (int i = 0; i < plan.InsertOperations.Count; i++)
            {
                EditOperation operation = plan.InsertOperations[i];
                int sourceOperationDurationMs = EditMapTimelineHelper.LanguageDurationToRenderedDurationMs(operation.DurationMs, plan.StretchRatio);
                if (operation.LangTimestampMs > currentLangMs)
                {
                    // Mantiene lang fino al gap rilevato dalla deep-analysis
                    this.AddTimelineIntervalSegments(segments, 1, langTrack, plan.LangInitialTimelineOffsetMs, currentLangMs, operation.LangTimestampMs, plan.StretchRatio, plan.LangTempo);
                }

                if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                {
                    if (plan.SourceFilledOperations.Contains(operation))
                    {
                        // Dove il piano lo ammette usa lo stesso intervallo temporale della source invece di generare silenzio
                        this.AddTimelineIntervalSegments(segments, 0, sourceTrack, plan.SourceInitialTimelineOffsetMs, operation.SourceTimestampMs, operation.SourceTimestampMs + sourceOperationDurationMs, 1.0, 1.0, operation.GainDb);
                    }
                    else
                    {
                        // Qui lo stretch è già materializzato dal filtro, quindi il silenzio deve durare quanto l'output
                        segments.Add(new AudioFilterSegment(1, langTrack.Id, 0, sourceOperationDurationMs, true));
                    }
                    currentLangMs = operation.LangTimestampMs;
                }
                else if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    currentLangMs = operation.LangTimestampMs + operation.DurationMs;
                }
            }

            this.AddTimelineIntervalSegments(segments, 1, langTrack, plan.LangInitialTimelineOffsetMs, currentLangMs, -1, plan.StretchRatio, plan.LangTempo);

            if (plan.EndFillMs > 0 && plan.SourceDurationMs > plan.EndFillMs)
            {
                // Se lang finisce prima della source, completa la coda usando gli ultimi ms source
                this.AddTimelineIntervalSegments(segments, 0, sourceTrack, plan.SourceInitialTimelineOffsetMs, plan.SourceDurationMs - plan.EndFillMs, plan.SourceDurationMs, 1.0, 1.0);
            }

            return this.BuildConcatFilter(segments, langTrack, options, forPeakTemp, plan.InitialTrimMs, "");
        }

        /// <summary>
        /// Aggiunge un intervallo conservando il gap che precede il primo packet della traccia
        /// </summary>
        /// <param name="segments">Segmenti audio in costruzione</param>
        /// <param name="inputIndex">Indice input ffmpeg</param>
        /// <param name="track">Traccia originale con timestamp minimo</param>
        /// <param name="trackStartMs">Origine della traccia rispetto alla timeline video</param>
        /// <param name="startMs">Inizio richiesto nel dominio nativo</param>
        /// <param name="endMs">Fine richiesta nel dominio nativo, oppure -1 per la coda</param>
        /// <param name="silenceScale">Moltiplicatore con cui materializzare il gap</param>
        /// <param name="tempo">Tempo ffmpeg da applicare alla parte con campioni</param>
        /// <param name="gainDb">Gain in decibel da applicare alla parte con campioni</param>
        private void AddTimelineIntervalSegments(List<AudioFilterSegment> segments, int inputIndex, TrackInfo track, int trackStartMs, int startMs, int endMs, double silenceScale, double tempo, double gainDb = 0.0)
        {
            int intervalStartMs = Math.Max(0, startMs);
            int gapEndMs = endMs >= 0 ? Math.Min(endMs, trackStartMs) : trackStartMs;
            int trackDurationMs = track != null && track.TrackDurationNs > 0 ? (int)Math.Round(track.TrackDurationNs / 1000000.0) : 0;
            int trackEndMs = trackDurationMs > 0 ? trackStartMs + trackDurationMs : -1;

            if (gapEndMs > intervalStartMs)
            {
                int silenceDurationMs = (int)Math.Round((gapEndMs - intervalStartMs) * silenceScale, MidpointRounding.AwayFromZero);
                if (silenceDurationMs > 0)
                    segments.Add(new AudioFilterSegment(inputIndex, track.Id, 0, silenceDurationMs, true));
            }

            int mediaTimelineStartMs = Math.Max(intervalStartMs, trackStartMs);
            int mediaTimelineEndMs = endMs;
            if (trackEndMs >= 0 && (mediaTimelineEndMs < 0 || mediaTimelineEndMs > trackEndMs))
                mediaTimelineEndMs = trackEndMs;
            if (mediaTimelineEndMs < 0 || mediaTimelineEndMs > mediaTimelineStartMs)
            {
                // FFmpeg ribasa il primo packet audio a zero: i trim vanno riportati
                // dal dominio del container al dominio dei campioni decodificati
                int decodedStartMs = Math.Max(0, mediaTimelineStartMs - trackStartMs);
                int decodedEndMs = mediaTimelineEndMs >= 0 ? Math.Max(0, mediaTimelineEndMs - trackStartMs) : -1;
                segments.Add(new AudioFilterSegment(inputIndex, track.Id, decodedStartMs, decodedEndMs, false, tempo, gainDb));
            }

            if (endMs >= 0 && trackEndMs >= 0)
            {
                int missingStartMs = Math.Max(intervalStartMs, trackEndMs);
                if (endMs > missingStartMs)
                {
                    int silenceDurationMs = (int)Math.Round((endMs - missingStartMs) * silenceScale, MidpointRounding.AwayFromZero);
                    if (silenceDurationMs > 0)
                        segments.Add(new AudioFilterSegment(inputIndex, track.Id, 0, silenceDurationMs, true));
                }
            }
        }

        /// <summary>
        /// Costruisce il filtro concat comune a EditMap e source fill, con trim iniziale opzionale
        /// </summary>
        /// <param name="segments">Segmenti audio da concatenare</param>
        /// <param name="track">Traccia audio usata per risolvere formato e layout</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="forPeakTemp">True se il filtro produce PCM temporaneo per peak</param>
        /// <param name="initialTrimMs">Durata del trim iniziale in millisecondi</param>
        /// <param name="globalTempoFilter">Filtro atempo globale, vuoto se non richiesto</param>
        /// <returns>Filtro ffmpeg completo con output [outa]</returns>
        private string BuildConcatFilter(List<AudioFilterSegment> segments, TrackInfo track, Options options, bool forPeakTemp, int initialTrimMs, string globalTempoFilter)
        {
            string filter = "";
            string concatInputs = "";
            string layout = options.AudioFormat == "ac3" ? AudioChannelHelper.GetAc3ChannelLayout(track.Channels) : AudioChannelHelper.GetChannelLayout(track.Channels);
            string sampleRate = (track.SamplingFrequency > 0 ? track.SamplingFrequency : 48000).ToString(CultureInfo.InvariantCulture);

            for (int i = 0; i < segments.Count; i++)
            {
                AudioFilterSegment segment = segments[i];
                string label = "a" + i.ToString(CultureInfo.InvariantCulture);
                if (segment.IsSilence)
                {
                    filter += "anullsrc=channel_layout=" + layout + ":sample_rate=" + sampleRate + ",atrim=duration=" + (segment.EndMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + ",aformat=sample_fmts=flt[" + label + "];";
                }
                else
                {
                    // Ogni segmento riparte da PTS zero per evitare buchi o overlap nel concat ffmpeg
                    filter += "[" + segment.InputIndex + ":" + segment.TrackId + "]atrim=start=" + (segment.StartMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
                    if (segment.EndMs > 0)
                    {
                        filter += ":end=" + (segment.EndMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
                    }
                    filter += ",asetpts=PTS-STARTPTS";
                    if (Math.Abs(segment.Tempo - 1.0) > 0.0001)
                    {
                        if (!AudioTempoFilterBuilder.TryBuildFromTempo(segment.Tempo, out string segmentTempoFilter, out _))
                        {
                            throw new InvalidOperationException("Tempo audio FFmpeg non valido");
                        }
                        filter += "," + segmentTempoFilter;
                    }
                    if (Math.Abs(segment.GainDb) > 0.000001)
                    {
                        filter += ",volume=" + segment.GainDb.ToString("0.######", CultureInfo.InvariantCulture) + "dB";
                    }
                    filter += ",aformat=sample_fmts=flt:sample_rates=" + sampleRate + ":channel_layouts=" + layout + "[" + label + "];";
                }
                concatInputs += "[" + label + "]";
            }

            filter += concatInputs + "concat=n=" + segments.Count + ":v=0:a=1";
            if (!string.IsNullOrEmpty(globalTempoFilter))
            {
                filter += "," + globalTempoFilter;
            }
            if (initialTrimMs > 0)
            {
                filter += ",atrim=start=" + (initialTrimMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + ",asetpts=PTS-STARTPTS";
            }
            string post = this.BuildPostFilter(track, options, forPeakTemp);
            if (!string.IsNullOrEmpty(post))
            {
                filter += "," + post;
            }
            filter += "[outa]";
            return filter;
        }

        /// <summary>
        /// Costruisce il filtro semplice combinando stretch materializzato e post-processing
        /// </summary>
        /// <param name="track">Traccia audio da filtrare</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="trackPlan">Piano di elaborazione della traccia</param>
        /// <param name="forPeakTemp">True se il filtro produce PCM temporaneo per peak</param>
        /// <returns>Filtro audio completo, vuoto se non sono necessarie trasformazioni</returns>
        private string BuildSimpleFilter(TrackInfo track, Options options, AudioTrackProcessingPlan trackPlan, bool forPeakTemp)
        {
            string filter = trackPlan != null ? trackPlan.AudioTempoFilter : "";
            int renderedTimelineOffsetMs = trackPlan != null ? (int)Math.Round(trackPlan.InitialTimelineOffsetMs * trackPlan.StretchRatio, MidpointRounding.AwayFromZero) : 0;
            string postFilter = this.BuildPostFilter(track, options, forPeakTemp);

            if (renderedTimelineOffsetMs > 0)
            {
                if (!string.IsNullOrEmpty(filter))
                    filter += ",";
                filter += "adelay=" + renderedTimelineOffsetMs.ToString(CultureInfo.InvariantCulture) + ":all=1";
            }
            else if (renderedTimelineOffsetMs < 0)
            {
                if (!string.IsNullOrEmpty(filter))
                    filter += ",";
                filter += "atrim=start=" + (-renderedTimelineOffsetMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + ",asetpts=PTS-STARTPTS";
            }

            if (!string.IsNullOrEmpty(filter) && !string.IsNullOrEmpty(postFilter))
            {
                filter += ",";
            }
            filter += postFilter;
            return filter;
        }

        /// <summary>
        /// Costruisce il post-filtro audio comune per formato interno e dither
        /// </summary>
        /// <param name="track">Traccia audio usata per risolvere layout e sample rate target</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="forPeakTemp">True se il filtro produce PCM temporaneo per peak</param>
        /// <returns>Filtro audio da appendere alla catena ffmpeg</returns>
        private string BuildPostFilter(TrackInfo track, Options options, bool forPeakTemp)
        {
            string filter = "aformat=sample_fmts=flt";
            int channels = track != null ? track.Channels : 0;

            if (options.AudioFormat == "ac3")
            {
                filter += ":sample_rates=" + this.ResolveAc3SampleRate(track).ToString(CultureInfo.InvariantCulture) + ":channel_layouts=" + AudioChannelHelper.GetAc3ChannelLayout(channels);
            }

            if (options.AudioDownsample24To16 && !forPeakTemp)
            {
                // Il dither deve essere l'ultima trasformazione PCM, dopo l'eventuale gain di normalizzazione
                filter += ",aresample=resampler=soxr:precision=28:dither_method=shibata:osf=s16";
            }

            return filter;
        }

        /// <summary>
        /// Aggiunge encoder e parametri codec per il formato audio di destinazione
        /// </summary>
        /// <param name="args">Lista argomenti ffmpeg da modificare</param>
        /// <param name="track">Traccia da codificare</param>
        /// <param name="options">Opzioni correnti</param>
        private void AddCodecArgs(List<string> args, TrackInfo track, Options options)
        {
            string format = options.AudioFormat;
            int bits = this.ResolveOutputBits(track, options);

            if (format == "flac")
            {
                args.Add("-c:a");
                args.Add("flac");
                args.Add("-compression_level");
                args.Add(AppSettingsService.Instance.Settings.Flac.CompressionLevel.ToString(CultureInfo.InvariantCulture));
                args.Add("-sample_fmt");
                args.Add(bits <= 16 ? "s16" : "s32");
                args.Add("-bits_per_raw_sample");
                args.Add(bits <= 16 ? "16" : "24");
            }
            else if (format == "lpcm")
            {
                args.Add("-c:a");
                if (bits <= 16) { args.Add("pcm_s16le"); }
                else if (bits <= 24) { args.Add("pcm_s24le"); }
                else { args.Add("pcm_s32le"); }
            }
            else if (format == "aac")
            {
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-aac_coder");
                args.Add("twoloop");
                args.Add("-b:a");
                args.Add(AppSettingsService.Instance.GetAacBitrateForChannels(track.Channels).ToString(CultureInfo.InvariantCulture) + "k");
            }
            else if (format == "opus")
            {
                args.Add("-c:a");
                args.Add("libopus");
                args.Add("-b:a");
                args.Add(AppSettingsService.Instance.GetOpusBitrateForChannels(track.Channels).ToString(CultureInfo.InvariantCulture) + "k");
                if (track.Channels > 2)
                {
                    args.Add("-mapping_family");
                    args.Add("1");
                }
            }
            else if (format == "ac3")
            {
                args.Add("-c:a");
                args.Add("ac3");
                args.Add("-b:a");
                args.Add(AppSettingsService.Instance.GetAc3BitrateForChannels(track.Channels).ToString(CultureInfo.InvariantCulture) + "k");
                args.Add("-ar");
                args.Add(this.ResolveAc3SampleRate(track).ToString(CultureInfo.InvariantCulture));
                args.Add("-ac");
                args.Add(AudioChannelHelper.GetAc3ChannelCount(track.Channels).ToString(CultureInfo.InvariantCulture));
                args.Add("-channel_layout");
                args.Add(AudioChannelHelper.GetAc3ChannelLayout(track.Channels));
            }
        }

        /// <summary>
        /// Risolve il sample rate finale supportato dall'encoder AC-3
        /// </summary>
        /// <param name="track">Traccia audio sorgente</param>
        /// <returns>Sample rate AC-3 in Hz</returns>
        private int ResolveAc3SampleRate(TrackInfo track)
        {
            int sampleRate = track != null ? track.SamplingFrequency : 0;
            int result;

            if (sampleRate == 32000 || sampleRate == 44100 || sampleRate == 48000)
                result = sampleRate;
            else
                result = 48000;

            return result;
        }

        /// <summary>
        /// Aggiunge il codec PCM usato per i file temporanei di normalizzazione peak
        /// </summary>
        /// <param name="args">Lista argomenti ffmpeg da modificare</param>
        private void AddPeakTempCodecArgs(List<string> args)
        {
            args.Add("-c:a");
            args.Add("pcm_f32le");
        }

        /// <summary>
        /// Inserisce il codec PCM temporaneo prima del file di output già presente negli argomenti
        /// </summary>
        /// <param name="args">Lista argomenti ffmpeg da modificare</param>
        private void AddPeakTempCodecArgsBeforeOutput(List<string> args)
        {
            string output = args[args.Count - 1];
            args.RemoveAt(args.Count - 1);
            this.AddPeakTempCodecArgs(args);
            args.Add(output);
        }

        /// <summary>
        /// Costruisce gli argomenti ffmpeg per codificare il PCM temporaneo applicando gain peak
        /// </summary>
        /// <param name="tempFile">File PCM temporaneo</param>
        /// <param name="track">Traccia originale usata per metadati codec</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="outputFile">File audio temporaneo finale</param>
        /// <param name="gainDb">Gain da applicare in dB</param>
        /// <returns>Lista argomenti ffmpeg</returns>
        private List<string> BuildEncodeFromTempArgs(string tempFile, TrackInfo track, Options options, string outputFile, double gainDb)
        {
            List<string> args = new List<string>();
            string filter = "volume=" + gainDb.ToString("F6", CultureInfo.InvariantCulture) + "dB";

            if (options.AudioDownsample24To16)
            {
                filter += ",aresample=resampler=soxr:precision=28:dither_method=shibata:osf=s16";
            }

            args.Add("-nostdin");
            args.Add("-hide_banner");
            args.Add("-y");
            args.Add("-i");
            args.Add(tempFile);
            args.Add("-af");
            args.Add(filter);
            this.AddCodecArgs(args, track, options);
            args.Add(outputFile);

            return args;
        }

        /// <summary>
        /// Misura il peak del PCM temporaneo e calcola il gain per raggiungere il target
        /// </summary>
        /// <param name="tempFile">File PCM temporaneo da analizzare</param>
        /// <param name="targetDb">Target peak in dB</param>
        /// <param name="gainDb">Gain calcolato in dB</param>
        /// <returns>True se il peak è stato letto correttamente</returns>
        private bool MeasurePeakGain(string tempFile, double targetDb, out double gainDb)
        {
            List<string> args = new List<string>();
            ProcessResult processResult;
            double peakDb;

            gainDb = 0.0;
            args.Add("-nostdin");
            args.Add("-hide_banner");
            args.Add("-i");
            args.Add(tempFile);
            args.Add("-af");
            args.Add("astats=metadata=0:reset=0");
            args.Add("-f");
            args.Add("null");
            args.Add("-");

            processResult = ProcessRunner.Run(this._ffmpegPath, args.ToArray());
            if (processResult.ExitCode != 0)
            {
                return false;
            }

            if (!this.TryParsePeak(processResult.Stderr, out peakDb))
            {
                return false;
            }

            gainDb = targetDb - peakDb;
            ConsoleHelper.Write(LogSection.Conv, LogLevel.Debug, "  Peak: " + peakDb.ToString("F2", CultureInfo.InvariantCulture) + " dB, gain: " + gainDb.ToString("F2", CultureInfo.InvariantCulture) + " dB");
            return true;
        }

        /// <summary>
        /// Estrae il peak complessivo dall'output astats di ffmpeg
        /// </summary>
        /// <param name="stderr">Output stderr ffmpeg</param>
        /// <param name="peakDb">Peak rilevato in dB</param>
        /// <returns>True se il valore è stato trovato e parsato</returns>
        private bool TryParsePeak(string stderr, out double peakDb)
        {
            string[] lines = stderr.Split('\n');
            peakDb = 0.0;

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                int idx = line.IndexOf("Overall.Peak_level", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    idx = line.IndexOf("Peak level dB", StringComparison.OrdinalIgnoreCase);
                }
                if (idx >= 0)
                {
                    string[] parts = line.Split(':');
                    if (parts.Length >= 2 && double.TryParse(parts[parts.Length - 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out peakDb))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Esegue ffmpeg per produrre un file audio finale e registra il cleanup in caso di errore globale
        /// </summary>
        /// <param name="args">Argomenti ffmpeg</param>
        /// <param name="outputFile">File audio atteso</param>
        /// <param name="result">Risultato della traccia da valorizzare in caso di errore</param>
        /// <returns>True se ffmpeg ha prodotto un file valido senza fallback vietati</returns>
        private bool RunFfmpeg(List<string> args, string outputFile, AudioTrackProcessResult result)
        {
            ProcessResult processResult = ProcessRunner.Run(this._ffmpegPath, args.ToArray());
            if (processResult.ExitCode == 0 && File.Exists(outputFile) && !this.HasForbiddenAudioFallback(processResult.Stderr))
            {
                lock (this._lock)
                {
                    this._createdFiles.Add(outputFile);
                }
                return true;
            }

            FileHelper.DeleteTempFile(outputFile);
            // Alcune build ffmpeg loggano fallback non accettabili senza exit code esplicito
            result.ErrorMessage = "ffmpeg audio fallito: " + this.ResolveFfmpegError(processResult);
            return false;
        }

        /// <summary>
        /// Esegue ffmpeg per produrre un file temporaneo intermedio
        /// </summary>
        /// <param name="args">Argomenti ffmpeg</param>
        /// <param name="tempFile">File temporaneo atteso</param>
        /// <returns>True se ffmpeg ha prodotto un file valido senza fallback vietati</returns>
        private bool RunFfmpegToTemp(List<string> args, string tempFile)
        {
            ProcessResult processResult = ProcessRunner.Run(this._ffmpegPath, args.ToArray());
            this._lastFfmpegError.Value = "";
            if (processResult.ExitCode == 0 && File.Exists(tempFile) && !this.HasForbiddenAudioFallback(processResult.Stderr))
            {
                lock (this._lock)
                {
                    this._transientFiles.Add(tempFile);
                }
                return true;
            }

            FileHelper.DeleteTempFile(tempFile);
            this._lastFfmpegError.Value = this.ResolveFfmpegError(processResult);
            return false;
        }

        /// <summary>
        /// Converte l'output ffmpeg in un errore utente sintetico
        /// </summary>
        /// <param name="processResult">Risultato del processo ffmpeg</param>
        /// <returns>Messaggio errore normalizzato</returns>
        private string ResolveFfmpegError(ProcessResult processResult)
        {
            string output = !string.IsNullOrEmpty(processResult.Stderr) ? processResult.Stderr : processResult.Stdout;

            if (output.IndexOf("Requested resampling engine is unavailable", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ffmpeg non supporta il resampler soxr richiesto";
            }

            if (output.IndexOf("Requested noise shaping dither not available", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ffmpeg non può applicare il dither shibata alla frequenza richiesta";
            }

            return this.LastErrorLine(output);
        }

        /// <summary>
        /// Rileva fallback audio che ffmpeg può segnalare senza fallire il processo
        /// </summary>
        /// <param name="stderr">Output stderr ffmpeg</param>
        /// <returns>True se il fallback rende l'output non accettabile</returns>
        private bool HasForbiddenAudioFallback(string stderr)
        {
            return stderr.IndexOf("Requested noise shaping dither not available", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Formatta l'ultimo errore ffmpeg temporaneo per append al messaggio corrente
        /// </summary>
        /// <returns>Errore formattato, oppure stringa vuota</returns>
        private string FormatLastFfmpegError()
        {
            return !string.IsNullOrEmpty(this._lastFfmpegError.Value) ? ": " + this._lastFfmpegError.Value : "";
        }

        /// <summary>
        /// Legge i metadati della traccia audio prodotta usando fallback sulla traccia originale
        /// </summary>
        /// <param name="outputFile">File audio prodotto</param>
        /// <param name="fallback">Traccia originale usata come fallback</param>
        /// <param name="options">Opzioni correnti</param>
        /// <returns>Metadati traccia da passare al merge finale</returns>
        private TrackInfo ResolveOutputInfo(string outputFile, TrackInfo fallback, Options options)
        {
            TrackInfo result = this.CloneTrack(fallback);
            MkvFileInfo info = this._mkvToolsService.GetFileInfo(outputFile);
            bool outputInfoFound = false;

            if (info != null && info.Tracks != null)
            {
                for (int i = 0; i < info.Tracks.Count; i++)
                {
                    if (string.Equals(info.Tracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        result = info.Tracks[i];
                        outputInfoFound = true;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(result.Codec))
            {
                result.Codec = options.AudioFormat;
            }
            if (options.AudioFormat == "ac3")
            {
                result.Codec = "AC-3";
                if (!outputInfoFound)
                {
                    result.Channels = AudioChannelHelper.GetAc3ChannelCount(fallback.Channels);
                    result.SamplingFrequency = this.ResolveAc3SampleRate(fallback);
                    result.Bitrate = AppSettingsService.Instance.GetAc3BitrateForChannels(fallback.Channels) * 1000;
                }
            }
            if (options.AudioDownsample24To16)
            {
                result.BitsPerSample = 16;
            }

            // Il file temporaneo descrive il formato prodotto, non l'identità della traccia originale.
            result.Language = fallback.Language;
            result.LanguageIetf = fallback.LanguageIetf;
            result.Name = fallback.Name;
            result.DefaultTrack = fallback.DefaultTrack;
            result.ForcedTrack = fallback.ForcedTrack;

            return result;
        }

        /// <summary>
        /// Clona i metadati audio necessari quando il probing del file prodotto non basta
        /// </summary>
        /// <param name="source">Traccia sorgente da clonare</param>
        /// <returns>Copia dei metadati traccia</returns>
        private TrackInfo CloneTrack(TrackInfo source)
        {
            TrackInfo result = new TrackInfo();
            result.Id = source.Id;
            result.Type = source.Type;
            result.Codec = source.Codec;
            result.Language = source.Language;
            result.LanguageIetf = source.LanguageIetf;
            result.Name = source.Name;
            result.DefaultTrack = source.DefaultTrack;
            result.ForcedTrack = source.ForcedTrack;
            result.DefaultDurationNs = source.DefaultDurationNs;
            result.VideoFrameCount = source.VideoFrameCount;
            result.TrackDurationNs = source.TrackDurationNs;
            result.MinimumTimestampNs = 0;
            result.Channels = source.Channels;
            result.BitsPerSample = source.BitsPerSample;
            result.SamplingFrequency = source.SamplingFrequency;
            result.Bitrate = source.Bitrate;
            return result;
        }

        /// <summary>
        /// Risolve la bit depth finale compatibile con formato e opzioni audio
        /// </summary>
        /// <param name="track">Traccia da codificare</param>
        /// <param name="options">Opzioni correnti</param>
        /// <returns>Bit depth finale da usare per l'encoder</returns>
        private int ResolveOutputBits(TrackInfo track, Options options)
        {
            int bits = track.BitsPerSample;
            if (options.AudioDownsample24To16)
            {
                return 16;
            }
            if (bits <= 0)
            {
                bits = 16;
            }
            if (bits > 24 && options.AudioFormat == "flac")
            {
                bits = 24;
            }
            return bits;
        }

        /// <summary>
        /// Scrive nel log il piano audio effettivo prima del render parallelo
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="jobs">Job audio generati dalla richiesta</param>
        private void LogAudioProcessingPlan(AudioProcessingRequest request, List<AudioTrackJob> jobs)
        {
            string target;
            string downsample;
            string normalize;
            bool generic;
            bool render;
            string reason;

            target = Utils.FormatAudioFormat(request.Options.AudioFormat);
            downsample = request.Options.AudioDownsample24To16 ? "si" : "no";
            normalize = request.Options.AudioPeakNormalize ? request.Options.AudioPeakTargetDb.ToString("F2", CultureInfo.InvariantCulture) + " dB" : "no";

            ConsoleHelper.Write(LogSection.Conv, LogLevel.Debug, "  Audio request: format=" + target + ", scope=" + request.Options.AudioProcessingScope + ", normalize=" + normalize + ", 24to16=" + downsample + ", jobs=" + jobs.Count);
            for (int i = 0; i < jobs.Count; i++)
            {
                generic = jobs[i].GenericProcessing;
                render = jobs[i].Plan != null && jobs[i].Plan.RenderRequired;
                reason = this.FormatAudioPlanReason(jobs[i].Plan);
                ConsoleHelper.Write(
                    LogSection.Conv,
                    LogLevel.Debug,
                    "    " + this.FormatAudioTrackLabel(jobs[i].IsSource, jobs[i].Track) +
                    ", generic=" + (generic ? "si" : "no") +
                    ", render=" + (render ? "si" : "no") +
                    ", motivo=" + reason +
                    ", track-start=" + (jobs[i].Plan != null ? jobs[i].Plan.InitialTimelineOffsetMs.ToString(CultureInfo.InvariantCulture) : "0") + "ms" +
                    this.FormatAudioTempoLog(jobs[i].Plan));
            }
        }

        /// <summary>
        /// Formatta il motivo principale del piano audio
        /// </summary>
        /// <param name="plan">Piano traccia</param>
        /// <returns>Motivo sintetico</returns>
        private string FormatAudioPlanReason(AudioTrackProcessingPlan plan)
        {
            string result = "skip";

            if (plan == null)
            {
                return result;
            }
            if (!string.IsNullOrEmpty(plan.ErrorMessage))
            {
                result = "errore";
            }
            else if (plan.SourceFillHasWork)
            {
                result = plan.ActualSourceFill ? "source-fill" : "source-fill-render";
            }
            else if (plan.DeepEditRender)
            {
                result = "deep-edit-map";
            }
            else if (plan.StretchRender)
            {
                result = "stretch-materializzato";
            }
            else if (plan.TimelinePolicyRenderRequired)
            {
                result = "policy-speed-deep";
            }
            else if (plan.GenericRenderRequired)
            {
                result = "generic";
            }
            else if (plan.SourceFillConfigured)
            {
                result = "source-fill-no-work";
            }

            return result;
        }

        /// <summary>
        /// Formatta rapporto stretch e tempo FFmpeg per il log del piano
        /// </summary>
        /// <param name="plan">Piano traccia</param>
        /// <returns>Dettaglio di stretch e tempo, oppure stringa vuota</returns>
        private string FormatAudioTempoLog(AudioTrackProcessingPlan plan)
        {
            if (plan == null || plan.IsSource)
            {
                return "";
            }

            return ", stretch=" + (!string.IsNullOrEmpty(plan.StretchFactor) ? plan.StretchFactor : "1") +
                ", ratio=" + plan.StretchRatio.ToString("0.########", CultureInfo.InvariantCulture) +
                ", tempo=" + plan.AudioTempo.ToString("0.########", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formatta una traccia audio per log distinguendo source e lang
        /// </summary>
        /// <param name="isSource">True se la traccia arriva dal file source</param>
        /// <param name="track">Traccia audio da formattare</param>
        /// <returns>Etichetta traccia completa</returns>
        private string FormatAudioTrackLabel(bool isSource, TrackInfo track)
        {
            string origin = isSource ? "SRC" : "LANG";
            return origin + " audio track " + track.Id + " [" + this.FormatAudioTrackDetails(track) + "]";
        }

        /// <summary>
        /// Formatta i metadati principali di una traccia audio per log
        /// </summary>
        /// <param name="track">Traccia audio da formattare</param>
        /// <returns>Dettaglio compatto lingua/codec/canali/bitrate tecnico</returns>
        private string FormatAudioTrackDetails(TrackInfo track)
        {
            string language;
            string channels;
            string result;

            language = !string.IsNullOrEmpty(track.Language) ? track.Language : "und";
            channels = AudioChannelHelper.FormatChannels(track.Channels);
            result = language + " " + track.Codec;

            if (!string.IsNullOrEmpty(channels))
            {
                result += " " + channels;
            }
            if (track.BitsPerSample > 0)
            {
                result += " " + track.BitsPerSample + "bit";
            }
            if (track.SamplingFrequency > 0)
            {
                result += " " + (track.SamplingFrequency / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "kHz";
            }

            return result;
        }

        /// <summary>
        /// Crea un path temporaneo per la traccia audio finale
        /// </summary>
        /// <param name="record">Record file corrente</param>
        /// <param name="track">Traccia audio</param>
        /// <param name="format">Formato audio destinazione</param>
        /// <param name="prefix">Prefisso source/lang</param>
        /// <returns>Path temporaneo completo</returns>
        private string CreateOutputPath(FileProcessingRecord record, TrackInfo track, string format, string prefix)
        {
            string extension = ".mka";
            string label = !string.IsNullOrEmpty(record.EpisodeId) ? record.EpisodeId : "track";

            if (format == "flac") { extension = ".flac"; }
            else if (format == "lpcm") { extension = ".wav"; }
            else if (format == "aac") { extension = ".m4a"; }
            else if (format == "opus") { extension = ".ogg"; }
            else if (format == "ac3") { extension = ".ac3"; }

            return Path.Combine(this._tempFolder, "audio_" + prefix + "_" + label + "_t" + track.Id + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
        }

        /// <summary>
        /// Crea un path temporaneo PCM per la normalizzazione peak
        /// </summary>
        /// <param name="record">Record file corrente</param>
        /// <param name="track">Traccia audio</param>
        /// <returns>Path temporaneo completo</returns>
        private string CreatePeakTempPath(FileProcessingRecord record, TrackInfo track)
        {
            string label = !string.IsNullOrEmpty(record.EpisodeId) ? record.EpisodeId : "track";
            return Path.Combine(this._tempFolder, "audio_peak_" + label + "_t" + track.Id + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
        }

        /// <summary>
        /// Elimina tutti i file creati dal service dopo un fallimento globale
        /// </summary>
        private void DeleteCreatedFiles()
        {
            lock (this._lock)
            {
                for (int i = 0; i < this._createdFiles.Count; i++)
                {
                    FileHelper.DeleteTempFile(this._createdFiles[i]);
                }
                for (int i = 0; i < this._transientFiles.Count; i++)
                {
                    FileHelper.DeleteTempFile(this._transientFiles[i]);
                }
                this._createdFiles.Clear();
                this._transientFiles.Clear();
            }
        }

        /// <summary>
        /// Elimina solo i file temporanei intermedi dopo un processing riuscito
        /// </summary>
        private void DeleteTransientFiles()
        {
            lock (this._lock)
            {
                for (int i = 0; i < this._transientFiles.Count; i++)
                {
                    FileHelper.DeleteTempFile(this._transientFiles[i]);
                }
                this._transientFiles.Clear();
            }
        }

        /// <summary>
        /// Recupera l'ultima riga non vuota da un output testuale
        /// </summary>
        /// <param name="text">Testo da analizzare</param>
        /// <returns>Ultima riga non vuota, oppure stringa vuota</returns>
        private string LastErrorLine(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    return line;
                }
            }

            return "";
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Job interno per una traccia audio da processare
        /// </summary>
        private class AudioTrackJob
        {
            /// <summary>
            /// Costruttore job audio
            /// </summary>
            /// <param name="isSource">True se la traccia arriva dal file source</param>
            /// <param name="track">Traccia da processare</param>
            /// <param name="genericProcessing">True se la traccia richiede solo processing generico</param>
            /// <param name="plan">Piano operativo della traccia</param>
            public AudioTrackJob(bool isSource, TrackInfo track, bool genericProcessing, AudioTrackProcessingPlan plan)
            {
                this.IsSource = isSource;
                this.Track = track;
                this.GenericProcessing = genericProcessing;
                this.Plan = plan;
            }

            /// <summary>
            /// True se la traccia arriva dal file source
            /// </summary>
            public bool IsSource { get; set; }

            /// <summary>
            /// Traccia audio da processare
            /// </summary>
            public TrackInfo Track { get; set; }

            /// <summary>
            /// True se la traccia richiede solo processing generico
            /// </summary>
            public bool GenericProcessing { get; set; }

            /// <summary>
            /// Piano operativo della traccia
            /// </summary>
            public AudioTrackProcessingPlan Plan { get; set; }
        }

        /// <summary>
        /// Risultato interno del processing di una singola traccia
        /// </summary>
        private class AudioTrackProcessResult
        {
            /// <summary>
            /// Costruttore risultato job audio
            /// </summary>
            public AudioTrackProcessResult()
            {
                this.Success = false;
                this.ErrorMessage = "";
                this.OutputFile = "";
                this.OutputInfo = null;
                this.BypassAudioDelay = false;
            }

            /// <summary>
            /// True se la traccia è stata processata correttamente
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// Messaggio errore della traccia
            /// </summary>
            public string ErrorMessage { get; set; }

            /// <summary>
            /// File audio temporaneo prodotto
            /// </summary>
            public string OutputFile { get; set; }

            /// <summary>
            /// Metadata della traccia prodotta
            /// </summary>
            public TrackInfo OutputInfo { get; set; }

            /// <summary>
            /// True se il delay audio finale non deve essere applicato a questa traccia
            /// </summary>
            public bool BypassAudioDelay { get; set; }
        }

        /// <summary>
        /// Segmento audio elementare usato per comporre filtri concat
        /// </summary>
        private class AudioFilterSegment
        {
            /// <summary>
            /// Costruttore segmento audio per filtro concat
            /// </summary>
            /// <param name="inputIndex">Indice input ffmpeg</param>
            /// <param name="trackId">ID traccia nell'input</param>
            /// <param name="startMs">Inizio segmento in ms</param>
            /// <param name="endMs">Fine segmento in ms, oppure -1 per coda</param>
            /// <param name="isSilence">True se il segmento è silenzio generato</param>
            public AudioFilterSegment(int inputIndex, int trackId, int startMs, int endMs, bool isSilence) : this(inputIndex, trackId, startMs, endMs, isSilence, 1.0)
            {
            }

            /// <summary>
            /// Costruttore segmento audio per filtro concat con tempo esplicito
            /// </summary>
            /// <param name="inputIndex">Indice input ffmpeg</param>
            /// <param name="trackId">ID traccia nell'input</param>
            /// <param name="startMs">Inizio segmento in millisecondi</param>
            /// <param name="endMs">Fine segmento in millisecondi, oppure -1 per la coda</param>
            /// <param name="isSilence">True se il segmento è silenzio generato</param>
            /// <param name="tempo">Tempo ffmpeg da applicare al segmento</param>
            public AudioFilterSegment(int inputIndex, int trackId, int startMs, int endMs, bool isSilence, double tempo) : this(inputIndex, trackId, startMs, endMs, isSilence, tempo, 0.0)
            {
            }

            /// <summary>
            /// Costruttore segmento audio con tempo e gain espliciti
            /// </summary>
            /// <param name="inputIndex">Indice input ffmpeg</param>
            /// <param name="trackId">ID traccia nell'input</param>
            /// <param name="startMs">Inizio segmento in millisecondi</param>
            /// <param name="endMs">Fine segmento in millisecondi, oppure -1 per la coda</param>
            /// <param name="isSilence">True se il segmento è silenzio generato</param>
            /// <param name="tempo">Tempo ffmpeg da applicare al segmento</param>
            /// <param name="gainDb">Gain in decibel da applicare al segmento</param>
            public AudioFilterSegment(int inputIndex, int trackId, int startMs, int endMs, bool isSilence, double tempo, double gainDb)
            {
                this.InputIndex = inputIndex;
                this.TrackId = trackId;
                this.StartMs = startMs;
                this.EndMs = endMs;
                this.IsSilence = isSilence;
                this.Tempo = tempo;
                this.GainDb = gainDb;
            }

            /// <summary>
            /// Indice input ffmpeg
            /// </summary>
            public int InputIndex { get; set; }

            /// <summary>
            /// ID traccia nell'input
            /// </summary>
            public int TrackId { get; set; }

            /// <summary>
            /// Inizio segmento in millisecondi
            /// </summary>
            public int StartMs { get; set; }

            /// <summary>
            /// Fine segmento in millisecondi, oppure -1 per la coda
            /// </summary>
            public int EndMs { get; set; }

            /// <summary>
            /// True se il segmento è silenzio generato
            /// </summary>
            public bool IsSilence { get; set; }

            /// <summary>
            /// Tempo ffmpeg da applicare al segmento
            /// </summary>
            public double Tempo { get; set; }

            /// <summary>
            /// Gain in decibel da applicare al segmento
            /// </summary>
            public double GainDb { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Richiesta completa di processing audio
    /// </summary>
    public class AudioProcessingRequest
    {
        /// <summary>
        /// Costruttore richiesta processing audio
        /// </summary>
        public AudioProcessingRequest()
        {
            this.SourceFilePath = "";
            this.LanguageFilePath = "";
            this.SourceTracksToProcess = new List<TrackInfo>();
            this.LangTracksToProcess = new List<TrackInfo>();
            this.GenericSourceTrackIds = new HashSet<int>();
            this.GenericLangTrackIds = new HashSet<int>();
            this.MandatoryLangProcessing = false;
            this.Plan = null;
        }

        /// <summary>
        /// Record file corrente
        /// </summary>
        public FileProcessingRecord Record { get; set; }

        /// <summary>
        /// Opzioni operative correnti
        /// </summary>
        public Options Options { get; set; }

        /// <summary>
        /// Percorso file source
        /// </summary>
        public string SourceFilePath { get; set; }

        /// <summary>
        /// Percorso file language
        /// </summary>
        public string LanguageFilePath { get; set; }

        /// <summary>
        /// Tracce source da processare
        /// </summary>
        public List<TrackInfo> SourceTracksToProcess { get; set; }

        /// <summary>
        /// Tracce language da processare
        /// </summary>
        public List<TrackInfo> LangTracksToProcess { get; set; }

        /// <summary>
        /// ID tracce source con processing generico
        /// </summary>
        public HashSet<int> GenericSourceTrackIds { get; set; }

        /// <summary>
        /// ID tracce language con processing generico
        /// </summary>
        public HashSet<int> GenericLangTrackIds { get; set; }

        /// <summary>
        /// True se Speed Correction o DeepAnalysis impongono il render di tutte le tracce Language
        /// </summary>
        public bool MandatoryLangProcessing { get; set; }

        /// <summary>
        /// EditMap da applicare alle tracce language
        /// </summary>
        public EditMap LangEditMap { get; set; }

        /// <summary>
        /// Metadata file source
        /// </summary>
        public MkvFileInfo SourceInfo { get; set; }

        /// <summary>
        /// Metadata file language
        /// </summary>
        public MkvFileInfo LangInfo { get; set; }

        /// <summary>
        /// Delay audio effettivo dopo le decisioni pipeline
        /// </summary>
        public int EffectiveAudioDelayMs { get; set; }

        /// <summary>
        /// Piano audio pre-calcolato da pipeline o dry-run
        /// </summary>
        public AudioProcessingPlan Plan { get; set; }
    }

    /// <summary>
    /// Risultato del processing audio
    /// </summary>
    public class AudioProcessingResult
    {
        /// <summary>
        /// Costruttore risultato processing audio
        /// </summary>
        public AudioProcessingResult()
        {
            this.Success = true;
            this.ErrorMessage = "";
            this.SourceOutputFiles = new Dictionary<int, string>();
            this.LangOutputFiles = new Dictionary<int, string>();
            this.SourceOutputInfo = new Dictionary<int, TrackInfo>();
            this.LangOutputInfo = new Dictionary<int, TrackInfo>();
            this.AudioDelayBypassedLangIds = new HashSet<int>();
            this.EffectiveAudioDelayMs = 0;
        }

        /// <summary>
        /// True se il processing audio complessivo è riuscito
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Messaggio errore complessivo
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// File temporanei prodotti per tracce source
        /// </summary>
        public Dictionary<int, string> SourceOutputFiles { get; set; }

        /// <summary>
        /// File temporanei prodotti per tracce language
        /// </summary>
        public Dictionary<int, string> LangOutputFiles { get; set; }

        /// <summary>
        /// Metadata output per tracce source processate
        /// </summary>
        public Dictionary<int, TrackInfo> SourceOutputInfo { get; set; }

        /// <summary>
        /// Metadata output per tracce language processate
        /// </summary>
        public Dictionary<int, TrackInfo> LangOutputInfo { get; set; }

        /// <summary>
        /// ID tracce language che non devono ricevere delay audio finale
        /// </summary>
        public HashSet<int> AudioDelayBypassedLangIds { get; set; }

        /// <summary>
        /// Delay audio effettivo da applicare dopo il processing
        /// </summary>
        public int EffectiveAudioDelayMs { get; set; }
    }
}
