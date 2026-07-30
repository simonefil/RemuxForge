using RemuxForge.Core.Audio;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Costruisce richieste audio coerenti tra preview e processing effettivo
    /// </summary>
    public class PipelineAudioProcessingRequestBuilder
    {
        #region Metodi pubblici

        /// <summary>
        /// Costruisce la richiesta audio usando solo tracce che finiranno nel file di output
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="sourceInfo">Metadata source</param>
        /// <param name="langInfo">Metadata language</param>
        /// <param name="sourceTracks">Tracce source</param>
        /// <param name="sourceAudioIds">ID audio source mantenuti</param>
        /// <param name="audioTracks">Tracce audio language importate</param>
        /// <param name="needsMerge">True se merge language attivo</param>
        /// <param name="filterSourceAudio">True se filtro source audio attivo</param>
        /// <param name="effectiveAudioDelay">Delay audio effettivo</param>
        /// <returns>Richiesta audio per planner/render</returns>
        public AudioProcessingRequest Build(FileProcessingRecord record, Options options, MkvFileInfo sourceInfo, MkvFileInfo langInfo, List<TrackInfo> sourceTracks, List<int> sourceAudioIds, List<TrackInfo> audioTracks, bool needsMerge, bool filterSourceAudio, int effectiveAudioDelay)
        {
            AudioProcessingRequest request = new AudioProcessingRequest();
            List<TrackInfo> finalSourceAudioTracks = this.ResolveFinalSourceAudioTracks(sourceTracks, sourceAudioIds, filterSourceAudio);
            bool deepAudioRequired = record.DeepAnalysisApplied && record.DeepAnalysisMap != null && record.DeepAnalysisMap.Operations.Count > 0 && !options.SubOnly;
            bool sourceFillRequired = options.AudioSourceFillThresholdMs > 0;
            bool mandatoryLangProcessing = OptionsValidator.RequiresTimelineAudioProcessing(options, needsMerge);

            request.Record = record;
            request.Options = options;
            request.SourceFilePath = record.SourceFilePath;
            request.LanguageFilePath = needsMerge ? record.LangFilePath : "";
            request.SourceInfo = sourceInfo;
            request.LangInfo = langInfo;
            request.LangEditMap = record.DeepAnalysisMap;
            request.EffectiveAudioDelayMs = effectiveAudioDelay;
            request.MandatoryLangProcessing = mandatoryLangProcessing;

            if (options.AudioProcessingScope == "all")
            {
                this.AddSourceGenericTracks(request, finalSourceAudioTracks);
                this.AddLangGenericTracks(request, audioTracks);
            }
            else if (options.AudioProcessingScope == "lang")
            {
                if (needsMerge)
                {
                    this.AddLangGenericTracks(request, audioTracks);
                }
                else
                {
                    this.AddSourceGenericTracks(request, finalSourceAudioTracks);
                }
            }

            if (mandatoryLangProcessing || deepAudioRequired || sourceFillRequired)
            {
                this.AddMissingLangTracks(request, audioTracks);
            }

            return request;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiunge tracce source allo scope generico
        /// </summary>
        private void AddSourceGenericTracks(AudioProcessingRequest request, List<TrackInfo> tracks)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                request.SourceTracksToProcess.Add(tracks[i]);
                request.GenericSourceTrackIds.Add(tracks[i].Id);
            }
        }

        /// <summary>
        /// Aggiunge tracce language allo scope generico
        /// </summary>
        private void AddLangGenericTracks(AudioProcessingRequest request, List<TrackInfo> tracks)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                request.LangTracksToProcess.Add(tracks[i]);
                request.GenericLangTrackIds.Add(tracks[i].Id);
            }
        }

        /// <summary>
        /// Aggiunge tracce language richieste da deep/source-fill senza marcarle come generiche
        /// </summary>
        private void AddMissingLangTracks(AudioProcessingRequest request, List<TrackInfo> tracks)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                bool containsTrack = false;
                for (int trackIndex = 0; trackIndex < request.LangTracksToProcess.Count; trackIndex++)
                {
                    if (request.LangTracksToProcess[trackIndex].Id == tracks[i].Id)
                    {
                        containsTrack = true;
                        break;
                    }
                }

                if (!containsTrack)
                {
                    request.LangTracksToProcess.Add(tracks[i]);
                }
            }
        }

        /// <summary>
        /// Risolve le tracce audio sorgente che saranno presenti nell'output
        /// </summary>
        /// <param name="sourceTracks">Tracce source</param>
        /// <param name="sourceAudioIds">ID audio source mantenuti</param>
        /// <param name="filterSourceAudio">True se il filtro source audio è attivo</param>
        /// <returns>Tracce source audio finali</returns>
        private List<TrackInfo> ResolveFinalSourceAudioTracks(List<TrackInfo> sourceTracks, List<int> sourceAudioIds, bool filterSourceAudio)
        {
            List<TrackInfo> result = new List<TrackInfo>();
            if (sourceTracks == null)
            {
                return result;
            }

            for (int i = 0; i < sourceTracks.Count; i++)
            {
                if (!string.Equals(sourceTracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (filterSourceAudio && !sourceAudioIds.Contains(sourceTracks[i].Id))
                {
                    continue;
                }
                result.Add(sourceTracks[i]);
            }

            return result;
        }

        #endregion
    }
}
