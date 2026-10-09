using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemuxForge.Core.Pipeline
{
    public sealed class ResolvedRemuxTracks
    {
        public List<int> SourceAudioIds { get; set; } = new List<int>();
        public List<int> SourceSubIds { get; set; } = new List<int>();
        public List<TrackInfo> LangAudioTracks { get; set; } = new List<TrackInfo>();
        public List<TrackInfo> LangSubTracks { get; set; } = new List<TrackInfo>();
        public bool FilterSourceAudio { get; set; }
        public bool FilterSourceSubs { get; set; }
    }

    /// <summary>Unico punto di risoluzione per preview, media-ready, audio e merge.</summary>
    public static class PipelineTrackSelectionResolver
    {
        public static ResolvedRemuxTracks Resolve(FileProcessingRecord record, Options options,
            List<TrackInfo> sourceTracks, List<TrackInfo> langTracks, MkvToolsService service,
            string[] codecPatterns, string[] sourceAudioCodecPatterns)
        {
            RemuxPairTrackSelection selection = record.ExplicitTrackSelection;
            if (selection == null && options.ExplicitTrackSelection != null)
                throw new InvalidOperationException(AppText.F("remuxConfiguration.selectionMissingPairFile", record.SourceFilePath));
            ResolvedRemuxTracks result = new ResolvedRemuxTracks();
            result.FilterSourceAudio = selection != null || options.KeepSourceAudioLangs.Count > 0 || options.KeepSourceAudioCodec.Count > 0;
            result.FilterSourceSubs = selection != null || options.KeepSourceSubtitleLangs.Count > 0;
            if (selection != null)
            {
                if (selection.PairKey != RemuxPairTrackSelection.CreatePairKey(record.SourceFilePath, record.LangFilePath))
                    throw new InvalidOperationException(AppText.T("remuxConfiguration.selectionWrongPair"));
                result.SourceAudioIds = ResolveIds(sourceTracks, "audio", selection.SourceAudioIds).Select(track => track.Id).ToList();
                result.SourceSubIds = ResolveIds(sourceTracks, "subtitles", selection.SourceSubIds).Select(track => track.Id).ToList();
            }
            else
            {
                if (sourceTracks != null)
                {
                    result.SourceAudioIds = service.GetSourceTrackIds(sourceTracks, "audio", options.KeepSourceAudioLangs, sourceAudioCodecPatterns);
                    result.SourceSubIds = service.GetSourceTrackIds(sourceTracks, "subtitles", options.KeepSourceSubtitleLangs, null);
                }
            }
            ResolveLanguage(record, options, langTracks, service, codecPatterns, out List<TrackInfo> audio, out List<TrackInfo> subs);
            result.LangAudioTracks = audio;
            result.LangSubTracks = subs;
            return result;
        }

        public static void ResolveLanguage(FileProcessingRecord record, Options options, List<TrackInfo> tracks,
            MkvToolsService service, string[] codecPatterns, out List<TrackInfo> audio, out List<TrackInfo> subs)
        {
            audio = new List<TrackInfo>();
            subs = new List<TrackInfo>();
            if (record.ExplicitTrackSelection != null)
            {
                audio = ResolveIds(tracks, "audio", record.ExplicitTrackSelection.LangAudioIds);
                subs = ResolveIds(tracks, "subtitles", record.ExplicitTrackSelection.LangSubIds);
            }
            else
            {
                if (options.ExplicitTrackSelection != null) throw new InvalidOperationException(AppText.T("remuxConfiguration.selectionMissingPair"));
                if (tracks == null) return;
                HashSet<int> audioIds = new HashSet<int>();
                HashSet<int> subIds = new HashSet<int>();
                foreach (string language in options.TargetLanguage)
                {
                    // Alias equivalenti possono selezionare lo stesso ID: importa una sola volta, in ordine di prima selezione.
                    if (!options.SubOnly)
                        foreach (TrackInfo track in service.GetFilteredTracks(tracks, language, "audio", codecPatterns))
                            if (audioIds.Add(track.Id)) audio.Add(track);
                    if (!options.AudioOnly)
                        foreach (TrackInfo track in service.GetFilteredTracks(tracks, language, "subtitles", null))
                            if (subIds.Add(track.Id)) subs.Add(track);
                }
            }
        }

        /// <summary>Audio visualizzabile: inventario legacy, soli ID selezionati per il contratto esplicito.</summary>
        public static List<TrackInfo> ResolveDisplayAudio(FileProcessingRecord record, bool source, List<TrackInfo> inventory = null)
        {
            List<TrackInfo> tracks = inventory ?? (source ? record.SourceAudioTracks : record.ImportedAudioTracks);
            if (record.ExplicitTrackSelection == null)
                return new List<TrackInfo>(tracks ?? new List<TrackInfo>());
            HashSet<int> ids = source ? record.ExplicitTrackSelection.SourceAudioIds : record.ExplicitTrackSelection.LangAudioIds;
            // Dopo l'invalidazione i dati derivati possono essere vuoti fino alla prossima analisi.
            // La visualizzazione non ricostruisce inventari né recupera tracce escluse.
            if (inventory == null)
                return (tracks ?? new List<TrackInfo>()).Where(track =>
                    string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase) && ids.Contains(track.Id)).ToList();
            return ResolveIds(tracks, "audio", ids);
        }

        /// <summary>Source-fill: ID espliciti prima del match lingua; legacy usa l'intero inventario.
        /// Conserva la preferenza storica: bitrate maggiore, primo in inventario a parità.</summary>
        public static TrackInfo ResolveSourceFillTrack(RemuxPairTrackSelection selection, List<TrackInfo> sourceTracks,
            string language, MkvToolsService service)
        {
            List<TrackInfo> candidates = selection != null ? ResolveIds(sourceTracks, "audio", selection.SourceAudioIds) :
                sourceTracks ?? new List<TrackInfo>();
            TrackInfo result = null;
            foreach (TrackInfo candidate in candidates)
            {
                if (!string.Equals(candidate.Type, "audio", StringComparison.OrdinalIgnoreCase) || !service.IsLanguageMatch(candidate, language))
                    continue;
                if (result == null || candidate.Bitrate > result.Bitrate)
                    result = candidate;
            }
            return result;
        }

        public static string SourceFillSelectionError(RemuxPairTrackSelection selection, string language) =>
            AppText.F("remuxConfiguration.sourceFillUnavailable", nameof(Options.AudioSourceFillLanguage), selection.SourceFilePath, language);

        private static List<TrackInfo> ResolveIds(List<TrackInfo> tracks, string type, HashSet<int> ids)
        {
            if (ids == null) throw new InvalidOperationException(AppText.T("remuxConfiguration.selectionMissingIds"));
            List<TrackInfo> result = (tracks ?? new List<TrackInfo>()).Where(track =>
                string.Equals(track.Type, type, StringComparison.OrdinalIgnoreCase) && ids.Contains(track.Id)).ToList();
            if (!ids.SetEquals(result.Select(track => track.Id)))
                throw new InvalidOperationException(AppText.F("remuxConfiguration.selectionIdsMissingInventory", type));
            return result;
        }
    }
}
