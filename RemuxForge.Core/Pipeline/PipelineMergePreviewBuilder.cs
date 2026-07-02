using RemuxForge.Core.Audio;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Costruzione preview comando mkvmerge per record analizzati
    /// </summary>
    public class PipelineMergePreviewBuilder
    {
        #region Variabili di classe

        private PipelineTrackMapper _trackMapper;
        private PipelineOutputManager _outputManager;
        private PipelineAudioProcessingRequestBuilder _audioRequestBuilder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="trackMapper">Mapper tracce pipeline</param>
        /// <param name="outputManager">Gestore output pipeline</param>
        public PipelineMergePreviewBuilder(PipelineTrackMapper trackMapper, PipelineOutputManager outputManager)
        {
            this._trackMapper = trackMapper;
            this._outputManager = outputManager;
            this._audioRequestBuilder = new PipelineAudioProcessingRequestBuilder();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce anteprima e argomenti merge per un record
        /// </summary>
        /// <param name="record">Record in elaborazione</param>
        /// <param name="options">Opzioni operative</param>
        /// <param name="mkvService">Servizio mkvmerge</param>
        /// <param name="fileInfoProvider">Provider metadata file</param>
        /// <param name="needsMerge">True se serve merge</param>
        /// <param name="needsRemux">True se serve remux</param>
        /// <param name="filterSourceAudio">True se filtrare audio sorgente</param>
        /// <param name="filterSourceSubs">True se filtrare sottotitoli sorgente</param>
        /// <param name="codecPatterns">Pattern codec lingua</param>
        /// <param name="sourceAudioCodecPatterns">Pattern codec audio sorgente</param>
        /// <param name="ffmpegPath">Percorso ffmpeg per fallback durata preview audio</param>
        public void Build(FileProcessingRecord record, Options options, MkvToolsService mkvService, Func<string, MkvFileInfo> fileInfoProvider, bool needsMerge, bool needsRemux, bool filterSourceAudio, bool filterSourceSubs, string[] codecPatterns, string[] sourceAudioCodecPatterns, string ffmpegPath)
        {
            int effectiveAudioDelay = record.SyncOffsetMs + options.AudioDelay + record.ManualAudioDelayMs;
            int effectiveSubDelay = record.SyncOffsetMs + options.SubtitleDelay + record.ManualSubDelayMs;
            string stretchFactor = record.StretchFactor;
            MkvFileInfo sourceInfo;
            MkvFileInfo langInfo = null;
            List<TrackInfo> sourceTracks;
            List<TrackInfo> langTracks = null;
            List<int> sourceAudioIds = new List<int>();
            List<int> sourceSubIds = new List<int>();
            List<TrackInfo> audioTracks = new List<TrackInfo>();
            List<TrackInfo> subtitleTracks = new List<TrackInfo>();
            Dictionary<int, string> convertedSourceTracks = new Dictionary<int, string>();
            Dictionary<int, string> convertedLangTracks = new Dictionary<int, string>();
            Dictionary<int, TrackInfo> processedSourceAudioInfo = new Dictionary<int, TrackInfo>();
            Dictionary<int, TrackInfo> processedLangAudioInfo = new Dictionary<int, TrackInfo>();
            HashSet<int> audioDelayBypassedLangIds = new HashSet<int>();
            string outputPath;
            List<string> mergeArgs;
            bool hasWork;
            sourceInfo = fileInfoProvider(record.SourceFilePath);
            sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;

            if (needsMerge && record.LangFilePath.Length > 0)
            {
                langInfo = fileInfoProvider(record.LangFilePath);
                langTracks = (langInfo != null) ? langInfo.Tracks : null;
            }

            if (sourceTracks != null)
            {
                if (filterSourceAudio)
                {
                    sourceAudioIds = mkvService.GetSourceTrackIds(sourceTracks, "audio", options.KeepSourceAudioLangs, sourceAudioCodecPatterns);
                }
                if (filterSourceSubs)
                {
                    sourceSubIds = mkvService.GetSourceTrackIds(sourceTracks, "subtitles", options.KeepSourceSubtitleLangs, null);
                }

                if (needsMerge && langTracks != null)
                {
                    this._trackMapper.CollectLanguageTracks(record, langTracks, mkvService, options, codecPatterns, out audioTracks, out subtitleTracks);
                }

                hasWork = needsMerge ? (audioTracks.Count > 0 || subtitleTracks.Count > 0) : needsRemux;

                record.KeptSourceAudioIds = sourceAudioIds;
                record.KeptSourceSubIds = sourceSubIds;
                record.ImportedAudioTracks = audioTracks;
                record.ImportedSubTracks = subtitleTracks;
                record.DisplayAudioFormat = Utils.FormatAudioFormat(options.AudioFormat);

                if (hasWork)
                {
                    this.BuildAudioPreview(record, options, mkvService, sourceInfo, langInfo, sourceTracks, sourceAudioIds, audioTracks, needsMerge, filterSourceAudio, ffmpegPath, convertedSourceTracks, convertedLangTracks, processedSourceAudioInfo, processedLangAudioInfo, audioDelayBypassedLangIds, ref effectiveAudioDelay);
                    outputPath = this._outputManager.ComputeFinalOutputPath(record.SourceFilePath, options);

                    MergeRequest mergeReq = new MergeRequest();
                    mergeReq.SourceFile = record.SourceFilePath;
                    mergeReq.LanguageFile = needsMerge ? record.LangFilePath : "";
                    mergeReq.OutputFile = outputPath;
                    mergeReq.SourceAudioIds = sourceAudioIds;
                    mergeReq.SourceAudioTracks = this._trackMapper.FilterTracksByIds(sourceTracks, sourceAudioIds);
                    mergeReq.SourceSubIds = sourceSubIds;
                    mergeReq.LangAudioTracks = audioTracks;
                    mergeReq.LangSubTracks = subtitleTracks;
                    mergeReq.AudioDelayMs = effectiveAudioDelay;
                    mergeReq.SubDelayMs = effectiveSubDelay;
                    mergeReq.FilterSourceAudio = filterSourceAudio || convertedSourceTracks.Count > 0;
                    mergeReq.FilterSourceSubs = filterSourceSubs;
                    mergeReq.StretchFactor = stretchFactor;
                    mergeReq.AudioFormat = options.AudioFormat;
                    mergeReq.SourceTitle = (sourceInfo != null) ? sourceInfo.ContainerTitle : "";
                    mergeReq.ConvertedSourceTracks = convertedSourceTracks;
                    mergeReq.ConvertedLangTracks = convertedLangTracks;
                    mergeReq.ProcessedSourceAudioInfo = processedSourceAudioInfo;
                    mergeReq.ProcessedLangAudioInfo = processedLangAudioInfo;
                    mergeReq.AudioDelayBypassedLangIds = audioDelayBypassedLangIds;
                    mergeArgs = mkvService.BuildMergeArguments(mergeReq);

                    record.MergeCommand = mkvService.FormatMergeCommand(mergeArgs);
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola il piano audio preview e crea placeholder processati coerenti con dry-run
        /// </summary>
        /// <param name="record">Record in elaborazione</param>
        /// <param name="options">Opzioni operative</param>
        /// <param name="mkvService">Servizio mkvmerge</param>
        /// <param name="sourceInfo">Metadata source</param>
        /// <param name="langInfo">Metadata language</param>
        /// <param name="sourceTracks">Tracce source</param>
        /// <param name="sourceAudioIds">ID audio source mantenuti</param>
        /// <param name="audioTracks">Tracce audio language importate</param>
        /// <param name="needsMerge">True se merge language attivo</param>
        /// <param name="filterSourceAudio">True se filtro source audio attivo</param>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <param name="convertedSourceTracks">Placeholder source processati</param>
        /// <param name="convertedLangTracks">Placeholder language processati</param>
        /// <param name="processedSourceAudioInfo">Metadata stimati source</param>
        /// <param name="processedLangAudioInfo">Metadata stimati language</param>
        /// <param name="audioDelayBypassedLangIds">Tracce language con delay materializzato</param>
        /// <param name="effectiveAudioDelay">Delay audio effettivo modificabile</param>
        private void BuildAudioPreview(FileProcessingRecord record, Options options, MkvToolsService mkvService, MkvFileInfo sourceInfo, MkvFileInfo langInfo, List<TrackInfo> sourceTracks, List<int> sourceAudioIds, List<TrackInfo> audioTracks, bool needsMerge, bool filterSourceAudio, string ffmpegPath, Dictionary<int, string> convertedSourceTracks, Dictionary<int, string> convertedLangTracks, Dictionary<int, TrackInfo> processedSourceAudioInfo, Dictionary<int, TrackInfo> processedLangAudioInfo, HashSet<int> audioDelayBypassedLangIds, ref int effectiveAudioDelay)
        {
            AudioProcessingRequest request;
            AudioProcessingPlanner planner;
            AudioProcessingPlan plan;
            bool deepAudioRequired;
            bool processingPossible;

            deepAudioRequired = record.DeepAnalysisApplied && record.DeepAnalysisMap != null && record.DeepAnalysisMap.Operations.Count > 0 && !options.SubOnly;
            processingPossible = options.AudioProcessingScope != "disabled" || options.AudioSourceFillThresholdMs > 0 || deepAudioRequired;
            if (!processingPossible || options.AudioFormat.Length == 0)
            {
                record.AudioProcessingPreview = null;
                return;
            }

            request = this._audioRequestBuilder.Build(record, options, sourceInfo, langInfo, sourceTracks, sourceAudioIds, audioTracks, needsMerge, filterSourceAudio, effectiveAudioDelay);
            if (request.SourceTracksToProcess.Count == 0 && request.LangTracksToProcess.Count == 0)
            {
                record.AudioProcessingPreview = null;
                return;
            }

            planner = new AudioProcessingPlanner(mkvService, ffmpegPath);
            plan = planner.BuildPlan(request, true);
            request.Plan = plan;
            record.AudioProcessingPreview = plan;
            AudioProcessingDryRunHelper.AddPlaceholders(plan, options, convertedSourceTracks, convertedLangTracks, processedSourceAudioInfo, processedLangAudioInfo, audioDelayBypassedLangIds);

            this.EnsureSourceAudioIdsForProcessedTracks(sourceTracks, sourceAudioIds, convertedSourceTracks, filterSourceAudio);

            if (audioDelayBypassedLangIds.Count > 0)
            {
                effectiveAudioDelay = 0;
                record.AudioDelayApplied = effectiveAudioDelay;
            }
        }

        /// <summary>
        /// Garantisce che il comando preview mantenga anche le tracce source non renderizzate quando almeno una source viene processata
        /// </summary>
        /// <param name="sourceTracks">Tracce file sorgente</param>
        /// <param name="sourceAudioIds">ID audio sorgente da aggiornare</param>
        /// <param name="convertedSourceTracks">Tracce source processate come file separati</param>
        /// <param name="filterSourceAudio">True se il filtro source audio e' attivo</param>
        private void EnsureSourceAudioIdsForProcessedTracks(List<TrackInfo> sourceTracks, List<int> sourceAudioIds, Dictionary<int, string> convertedSourceTracks, bool filterSourceAudio)
        {
            if (convertedSourceTracks == null || convertedSourceTracks.Count == 0)
            {
                return;
            }

            if (!filterSourceAudio && sourceTracks != null)
            {
                /*
                 * SOURCE AUDIO NON FILTRATO
                 */
                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    if (string.Equals(sourceTracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase) && !sourceAudioIds.Contains(sourceTracks[i].Id))
                    {
                        sourceAudioIds.Add(sourceTracks[i].Id);
                    }
                }
                return;
            }

            foreach (int sourceTrackId in convertedSourceTracks.Keys)
            {
                if (!sourceAudioIds.Contains(sourceTrackId))
                {
                    sourceAudioIds.Add(sourceTrackId);
                }
            }
        }

        #endregion
    }
}
