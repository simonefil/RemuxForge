using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Audio
{
    /// <summary>
    /// Calcola il piano audio condiviso da preview, dry-run e render effettivo
    /// </summary>
    public class AudioProcessingPlanner
    {
        #region Variabili di classe

        /// <summary>
        /// Servizio mkvtools usato per match lingua source-fill
        /// </summary>
        private readonly MkvToolsService _mkvToolsService;

        /// <summary>
        /// Percorso ffmpeg per fallback lettura durata media
        /// </summary>
        private readonly string _ffmpegPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore planner audio
        /// </summary>
        /// <param name="mkvToolsService">Servizio mkvtools</param>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        public AudioProcessingPlanner(MkvToolsService mkvToolsService, string ffmpegPath)
        {
            this._mkvToolsService = mkvToolsService;
            this._ffmpegPath = ffmpegPath != null ? ffmpegPath : "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce il piano audio per una richiesta pipeline
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="probeMissingDurations">True per usare ffmpeg quando mkvmerge non espone durate</param>
        /// <returns>Piano audio deterministico per la richiesta</returns>
        public AudioProcessingPlan BuildPlan(AudioProcessingRequest request, bool probeMissingDurations)
        {
            AudioProcessingPlan result = new AudioProcessingPlan();

            if (request == null || request.Options == null)
            {
                return result;
            }

            if (request.SourceTracksToProcess != null)
            {
                for (int i = 0; i < request.SourceTracksToProcess.Count; i++)
                {
                    result.SourceTracks.Add(this.BuildTrackPlan(request, request.SourceTracksToProcess[i], true, request.GenericSourceTrackIds.Contains(request.SourceTracksToProcess[i].Id), probeMissingDurations));
                }
            }

            if (request.LangTracksToProcess != null)
            {
                for (int i = 0; i < request.LangTracksToProcess.Count; i++)
                {
                    result.LangTracks.Add(this.BuildTrackPlan(request, request.LangTracksToProcess[i], false, request.GenericLangTrackIds.Contains(request.LangTracksToProcess[i].Id), probeMissingDurations));
                }
            }

            return result;
        }

        /// <summary>
        /// True se il piano userà davvero segmenti source, non solo stretch/delay/render lang
        /// </summary>
        /// <param name="plan">Piano source-fill</param>
        /// <returns>True se verranno usati segmenti audio source</returns>
        public bool HasActualSourceFill(AudioSourceFillPlan plan)
        {
            if (plan == null)
            {
                return false;
            }

            return plan.StartFillMs > 0 || plan.EndFillMs > 0 || (plan.SourceFilledOperations != null && plan.SourceFilledOperations.Count > 0);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce il piano di una singola traccia secondo la stessa priorità del render effettivo
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="track">Traccia da pianificare</param>
        /// <param name="isSource">True se la traccia arriva dal source</param>
        /// <param name="genericProcessing">True se rientra nello scope audio generico</param>
        /// <param name="probeMissingDurations">True per usare ffmpeg quando mancano durate metadata</param>
        /// <returns>Piano traccia audio</returns>
        private AudioTrackProcessingPlan BuildTrackPlan(AudioProcessingRequest request, TrackInfo track, bool isSource, bool genericProcessing, bool probeMissingDurations)
        {
            AudioTrackProcessingPlan result = new AudioTrackProcessingPlan();
            bool sourceFillActive;
            double stretchRatio;

            result.IsSource = isSource;
            result.Track = track;
            result.GenericProcessing = genericProcessing;
            result.TimelinePolicyRenderRequired = !isSource && request.MandatoryLangProcessing;

            if (track == null)
            {
                result.ErrorMessage = "Traccia audio non valida";
                return result;
            }

            if (CodecMapping.IsSpatialCodec(track))
            {
                result.ErrorMessage = "Traccia audio spaziale/object selezionata per processing";
                return result;
            }

            if (!isSource)
            {
                stretchRatio = this.ResolveStretchRatio(request, out string stretchError);
                result.StretchFactor = request.Record != null ? request.Record.StretchFactor : "";
                result.StretchRatio = stretchRatio;
                bool validTempo = AudioTempoFilterBuilder.TryBuild(stretchRatio, out double audioTempo, out string audioTempoFilter, out string tempoError);
                if (!string.IsNullOrEmpty(stretchError) || !validTempo)
                {
                    result.ErrorMessage = !string.IsNullOrEmpty(stretchError) ? stretchError : tempoError;
                    return result;
                }

                result.AudioTempo = audioTempo;
                result.AudioTempoFilter = audioTempoFilter;
                result.StretchRender = !AudioTempoFilterBuilder.IsIdentity(stretchRatio);
            }

            sourceFillActive = request.Options.AudioSourceFillThresholdMs > 0 &&
                !string.IsNullOrEmpty(request.Options.AudioSourceFillLanguage) &&
                (request.Options.AudioSourceFillStart || request.Options.AudioSourceFillEnd || request.Options.AudioSourceFillInsertSilence);

            if (!isSource && sourceFillActive)
            {
                /*
                 * SOURCE FILL
                 */
                result.SourceFillConfigured = true;
                result.SourceFillTrack = this.SelectSourceFillTrack(request.SourceInfo, request.Options.AudioSourceFillLanguage);
                result.SourceFillPlan = this.BuildSourceFillPlan(request, result.SourceFillTrack, track, probeMissingDurations);
                result.SourceFillHasWork = result.SourceFillPlan != null && result.SourceFillPlan.HasWork;
                result.ActualSourceFill = this.HasActualSourceFill(result.SourceFillPlan);
                if (result.SourceFillHasWork && result.SourceFillTrack == null)
                {
                    result.ErrorMessage = "Audio source fill fallito: nessuna traccia source in lingua " + request.Options.AudioSourceFillLanguage + " per lang track " + track.Id;
                    return result;
                }
            }

            if (result.SourceFillHasWork && result.SourceFillPlan != null)
            {
                result.RenderRequired = true;
                result.BypassAudioDelay = result.SourceFillPlan.StartFillMs > 0 || result.SourceFillPlan.MaterializesExternalSync;
            }
            else if (!isSource && request.LangEditMap != null && request.LangEditMap.Operations != null && request.LangEditMap.Operations.Count > 0)
            {
                result.DeepEditRender = true;
                result.RenderRequired = true;
            }
            else if (!isSource && (result.StretchRender || result.TimelinePolicyRenderRequired))
            {
                result.RenderRequired = true;
            }
            else if (genericProcessing)
            {
                result.GenericRenderRequired = CodecMapping.RequiresGenericAudioRender(track, request.Options);
                result.RenderRequired = result.GenericRenderRequired;
            }

            if (genericProcessing)
            {
                result.GenericRenderRequired = CodecMapping.RequiresGenericAudioRender(track, request.Options);
                result.RenderRequired = result.RenderRequired || result.GenericRenderRequired;
            }

            return result;
        }

        /// <summary>
        /// Calcola quali porzioni source devono riempire inizio, fine o gap della traccia lang
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="sourceTrack">Traccia source candidata</param>
        /// <param name="langTrack">Traccia lang da completare</param>
        /// <param name="probeMissingDurations">True per usare ffmpeg quando mancano durate metadata</param>
        /// <returns>Piano source fill calcolato</returns>
        private AudioSourceFillPlan BuildSourceFillPlan(AudioProcessingRequest request, TrackInfo sourceTrack, TrackInfo langTrack, bool probeMissingDurations)
        {
            AudioSourceFillPlan result = new AudioSourceFillPlan();
            List<EditOperation> editOperations = this.GetSourceFillEditOperations(request.LangEditMap);
            int sourceDurationMs = this.ResolveTrackDurationMs(request.SourceInfo, sourceTrack);
            double stretchRatio = this.ResolveStretchRatio(request, out _);
            int langDurationMs;

            if (sourceDurationMs <= 0)
            {
                sourceDurationMs = this.ResolveVideoDurationMs(request.SourceInfo);
            }

            langDurationMs = this.ResolveTrackDurationMs(request.LangInfo, langTrack);
            if (langDurationMs <= 0 && probeMissingDurations)
            {
                langDurationMs = this.ResolveMediaDurationMs(request.LanguageFilePath);
            }

            result.StretchRatio = stretchRatio;
            AudioTempoFilterBuilder.TryBuild(stretchRatio, out double audioTempo, out _, out _);
            result.LangTempo = audioTempo;
            result.InitialSilenceMs = !AudioTempoFilterBuilder.IsIdentity(stretchRatio) && request.EffectiveAudioDelayMs > 0 ? request.EffectiveAudioDelayMs : 0;
            result.InitialTrimMs = !AudioTempoFilterBuilder.IsIdentity(stretchRatio) && request.EffectiveAudioDelayMs < 0 ? -request.EffectiveAudioDelayMs : 0;

            if (request.Options.AudioSourceFillStart && request.EffectiveAudioDelayMs > request.Options.AudioSourceFillThresholdMs)
            {
                result.StartFillMs = request.EffectiveAudioDelayMs;
                result.InitialSilenceMs = 0;
                result.InitialTrimMs = 0;
            }

            if (request.Options.AudioSourceFillEnd && sourceDurationMs > 0 && langDurationMs > 0)
            {
                int materializedDelayMs = result.StartFillMs + result.InitialSilenceMs - result.InitialTrimMs;
                if (materializedDelayMs == 0)
                {
                    materializedDelayMs = request.EffectiveAudioDelayMs;
                }
                int renderedLangDurationMs = (int)Math.Round(langDurationMs * stretchRatio) + materializedDelayMs + this.ComputeEditMapDurationDeltaMs(editOperations, stretchRatio);
                int endFillMs = sourceDurationMs - renderedLangDurationMs;
                if (endFillMs > request.Options.AudioSourceFillThresholdMs)
                {
                    result.EndFillMs = endFillMs;
                    result.SourceDurationMs = sourceDurationMs;
                }
            }

            // La deep analysis materializza testa e coda come operazioni invece che come delay di
            // contenitore: le tre spunte dicono dove il riempimento è ammesso, non se esiste
            for (int i = 0; i < editOperations.Count; i++)
            {
                EditOperation operation = editOperations[i];
                result.InsertOperations.Add(operation);
                if (!string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) ||
                    EditMapTimelineHelper.LanguageDurationToRenderedDurationMs(operation.DurationMs, stretchRatio) <= request.Options.AudioSourceFillThresholdMs)
                {
                    continue;
                }

                bool allowed;
                if (string.Equals(operation.Scope, EditOperation.SCOPE_HEAD, StringComparison.Ordinal))
                {
                    allowed = request.Options.AudioSourceFillStart;
                }
                else if (string.Equals(operation.Scope, EditOperation.SCOPE_TAIL, StringComparison.Ordinal))
                {
                    allowed = request.Options.AudioSourceFillEnd;
                }
                else
                {
                    allowed = request.Options.AudioSourceFillInsertSilence;
                }

                if (allowed)
                {
                    result.SourceFilledOperations.Add(operation);
                }
            }

            return result;
        }

        /// <summary>
        /// Risolve lo stretch applicato alla traccia language
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="errorMessage">Errore di parsing, vuoto quando il rapporto è valido</param>
        /// <returns>Rapporto stretch o 1.0</returns>
        private double ResolveStretchRatio(AudioProcessingRequest request, out string errorMessage)
        {
            double result = 1.0;
            errorMessage = "";
            if (request != null && request.Record != null && !string.IsNullOrEmpty(request.Record.StretchFactor))
            {
                if (!SpeedCorrectionService.TryParseStretchFactor(request.Record.StretchFactor, out result, out _))
                {
                    errorMessage = "Fattore stretch audio non valido: " + request.Record.StretchFactor;
                    result = 1.0;
                }
            }

            return result;
        }

        /// <summary>
        /// Legge la durata container via ffmpeg quando mkvmerge non la espone
        /// </summary>
        /// <param name="filePath">File da misurare</param>
        /// <returns>Durata in millisecondi o zero</returns>
        private int ResolveMediaDurationMs(string filePath)
        {
            ProcessResult processResult;
            if (string.IsNullOrEmpty(this._ffmpegPath) || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return 0;
            }

            processResult = ProcessRunner.Run(this._ffmpegPath, new string[] { "-hide_banner", "-i", filePath });
            return this.ParseFfmpegDurationMs(!string.IsNullOrEmpty(processResult.Stderr) ? processResult.Stderr : processResult.Stdout);
        }

        /// <summary>
        /// Estrae la durata dal formato ffmpeg "Duration: HH:MM:SS.xx"
        /// </summary>
        /// <param name="output">Output testuale ffmpeg</param>
        /// <returns>Durata in millisecondi o zero</returns>
        private int ParseFfmpegDurationMs(string output)
        {
            int marker;
            int end;
            string value;
            TimeSpan duration;
            if (output == null)
            {
                return 0;
            }

            marker = output.IndexOf("Duration:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return 0;
            }

            marker += "Duration:".Length;
            end = output.IndexOf(",", marker, StringComparison.Ordinal);
            if (end <= marker)
            {
                return 0;
            }

            value = output.Substring(marker, end - marker).Trim();
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
            {
                return (int)Math.Round(duration.TotalMilliseconds);
            }

            return 0;
        }

        /// <summary>
        /// Estrae dall'EditMap le operazioni eleggibili per source fill
        /// </summary>
        /// <param name="editMap">Mappa operazioni deep-analysis</param>
        /// <returns>Operazioni usate dal piano source-fill</returns>
        private List<EditOperation> GetSourceFillEditOperations(EditMap editMap)
        {
            List<EditOperation> result = new List<EditOperation>();
            if (editMap == null || editMap.Operations == null)
            {
                return result;
            }

            for (int i = 0; i < editMap.Operations.Count; i++)
            {
                EditOperation operation = editMap.Operations[i];
                if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) || string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    result.Add(operation);
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola quanto l'EditMap cambia la durata della traccia renderizzata
        /// </summary>
        /// <param name="operations">Operazioni EditMap</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <returns>Delta durata in millisecondi</returns>
        private int ComputeEditMapDurationDeltaMs(List<EditOperation> operations, double stretchRatio)
        {
            int result = 0;
            if (operations == null)
            {
                return result;
            }

            for (int i = 0; i < operations.Count; i++)
            {
                result += EditMapTimelineHelper.GetRenderedOperationDeltaMs(operations[i], stretchRatio);
            }

            return result;
        }

        /// <summary>
        /// Seleziona la traccia source migliore per lingua richiesta
        /// </summary>
        /// <param name="sourceInfo">Info file source</param>
        /// <param name="sourceLanguage">Lingua source richiesta</param>
        /// <returns>Traccia source selezionata, oppure null</returns>
        private TrackInfo SelectSourceFillTrack(MkvFileInfo sourceInfo, string sourceLanguage)
        {
            TrackInfo result = null;
            if (sourceInfo == null || sourceInfo.Tracks == null)
            {
                return result;
            }

            for (int i = 0; i < sourceInfo.Tracks.Count; i++)
            {
                TrackInfo candidate = sourceInfo.Tracks[i];
                if (!string.Equals(candidate.Type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!this._mkvToolsService.IsLanguageMatch(candidate, sourceLanguage))
                {
                    continue;
                }
                if (result == null || candidate.Bitrate > result.Bitrate)
                {
                    result = candidate;
                }
            }

            return result;
        }

        /// <summary>
        /// Risolve la durata traccia in millisecondi con fallback alla durata video/container
        /// </summary>
        /// <param name="fileInfo">Info file MKV</param>
        /// <param name="track">Traccia da misurare</param>
        /// <returns>Durata in millisecondi, oppure zero</returns>
        private int ResolveTrackDurationMs(MkvFileInfo fileInfo, TrackInfo track)
        {
            if (track != null && track.TrackDurationNs > 0)
            {
                return (int)Math.Round(track.TrackDurationNs / 1000000.0);
            }

            return this.ResolveVideoDurationMs(fileInfo);
        }

        /// <summary>
        /// Risolve la durata video o container in millisecondi
        /// </summary>
        /// <param name="fileInfo">Info file MKV</param>
        /// <returns>Durata in millisecondi, oppure zero</returns>
        private int ResolveVideoDurationMs(MkvFileInfo fileInfo)
        {
            if (fileInfo != null && fileInfo.Tracks != null)
            {
                for (int i = 0; i < fileInfo.Tracks.Count; i++)
                {
                    TrackInfo track = fileInfo.Tracks[i];
                    if (string.Equals(track.Type, "video", StringComparison.OrdinalIgnoreCase) && track.TrackDurationNs > 0)
                    {
                        return (int)Math.Round(track.TrackDurationNs / 1000000.0);
                    }
                }
            }

            return fileInfo != null && fileInfo.ContainerDurationNs > 0 ? (int)Math.Round(fileInfo.ContainerDurationNs / 1000000.0) : 0;
        }

        #endregion
    }
}
