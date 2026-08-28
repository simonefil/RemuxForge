using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Audio
{
    /// <summary>
    /// Crea placeholder audio coerenti con il piano audio senza eseguire ffmpeg
    /// </summary>
    public static class AudioProcessingDryRunHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Aggiunge placeholder dei soli render previsti dal piano
        /// </summary>
        /// <param name="plan">Piano audio calcolato</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="convertedSourceTracks">Output placeholder source</param>
        /// <param name="convertedLangTracks">Output placeholder language</param>
        /// <param name="processedSourceAudioInfo">Metadata stimati source</param>
        /// <param name="processedLangAudioInfo">Metadata stimati language</param>
        /// <param name="audioDelayBypassedLangIds">Tracce language che materializzano il delay nel render</param>
        public static void AddPlaceholders(AudioProcessingPlan plan, Options options, Dictionary<int, string> convertedSourceTracks, Dictionary<int, string> convertedLangTracks, Dictionary<int, TrackInfo> processedSourceAudioInfo, Dictionary<int, TrackInfo> processedLangAudioInfo, HashSet<int> audioDelayBypassedLangIds)
        {
            if (plan == null || options == null)
            {
                return;
            }

            AddSourcePlaceholders(plan, options, convertedSourceTracks, processedSourceAudioInfo);
            AddLangPlaceholders(plan, options, convertedLangTracks, processedLangAudioInfo, audioDelayBypassedLangIds);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiunge placeholder source per i render previsti
        /// </summary>
        private static void AddSourcePlaceholders(AudioProcessingPlan plan, Options options, Dictionary<int, string> convertedSourceTracks, Dictionary<int, TrackInfo> processedSourceAudioInfo)
        {
            for (int i = 0; i < plan.SourceTracks.Count; i++)
            {
                AudioTrackProcessingPlan trackPlan = plan.SourceTracks[i];
                if (!trackPlan.RenderRequired || trackPlan.Track == null)
                {
                    continue;
                }

                convertedSourceTracks[trackPlan.Track.Id] = "<processed-audio:source-track-" + trackPlan.Track.Id + ">";
                processedSourceAudioInfo[trackPlan.Track.Id] = CloneAudioInfoForDryRun(trackPlan.Track, trackPlan, options);
            }
        }

        /// <summary>
        /// Aggiunge placeholder language per i render previsti
        /// </summary>
        private static void AddLangPlaceholders(AudioProcessingPlan plan, Options options, Dictionary<int, string> convertedLangTracks, Dictionary<int, TrackInfo> processedLangAudioInfo, HashSet<int> audioDelayBypassedLangIds)
        {
            for (int i = 0; i < plan.LangTracks.Count; i++)
            {
                AudioTrackProcessingPlan trackPlan = plan.LangTracks[i];
                if (!trackPlan.RenderRequired || trackPlan.Track == null)
                {
                    continue;
                }

                convertedLangTracks[trackPlan.Track.Id] = "<processed-audio:lang-track-" + trackPlan.Track.Id + ">";
                processedLangAudioInfo[trackPlan.Track.Id] = CloneAudioInfoForDryRun(trackPlan.Track, trackPlan, options);
                if (trackPlan.BypassAudioDelay)
                {
                    audioDelayBypassedLangIds.Add(trackPlan.Track.Id);
                }
            }
        }

        /// <summary>
        /// Crea metadati audio stimati per preview dry-run
        /// </summary>
        /// <param name="source">Traccia sorgente</param>
        /// <param name="trackPlan">Piano operativo della traccia</param>
        /// <param name="options">Opzioni audio correnti</param>
        /// <returns>Metadata stimati del file processato</returns>
        private static TrackInfo CloneAudioInfoForDryRun(TrackInfo source, AudioTrackProcessingPlan trackPlan, Options options)
        {
            TrackInfo result = new TrackInfo();
            int ac3SampleRate;

            result.Id = source.Id;
            result.Type = source.Type;
            result.Codec = Utils.FormatAudioFormat(options.AudioFormat);
            result.Language = source.Language;
            result.LanguageIetf = source.LanguageIetf;
            result.Name = source.Name;
            result.DefaultTrack = source.DefaultTrack;
            result.ForcedTrack = source.ForcedTrack;
            result.DefaultDurationNs = source.DefaultDurationNs;
            result.VideoFrameCount = source.VideoFrameCount;
            result.TrackDurationNs = source.TrackDurationNs;
            if (trackPlan != null && trackPlan.StretchRender && result.TrackDurationNs > 0)
            {
                result.TrackDurationNs = (long)System.Math.Round(result.TrackDurationNs * trackPlan.StretchRatio);
            }
            if (trackPlan != null && trackPlan.RenderRequired)
            {
                if (result.TrackDurationNs > 0)
                {
                    long renderedOriginNs = (long)System.Math.Round(trackPlan.InitialTimelineOffsetMs * trackPlan.StretchRatio * 1000000.0);
                    result.TrackDurationNs = System.Math.Max(0, result.TrackDurationNs + renderedOriginNs);
                }
                result.MinimumTimestampNs = 0;
            }
            result.Channels = source.Channels;
            result.BitsPerSample = options.AudioDownsample24To16 ? 16 : source.BitsPerSample;
            result.SamplingFrequency = source.SamplingFrequency;
            result.Bitrate = source.Bitrate;
            if (options.AudioFormat == "ac3")
            {
                ac3SampleRate = source.SamplingFrequency;
                if (ac3SampleRate != 32000 && ac3SampleRate != 44100 && ac3SampleRate != 48000)
                    ac3SampleRate = 48000;

                result.Channels = AudioChannelHelper.GetAc3ChannelCount(source.Channels);
                result.SamplingFrequency = ac3SampleRate;
                result.Bitrate = AppSettingsService.Instance.GetAc3BitrateForChannels(source.Channels) * 1000;
            }
            return result;
        }

        #endregion
    }
}
