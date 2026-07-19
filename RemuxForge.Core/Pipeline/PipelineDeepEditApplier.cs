using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Applicazione EditMap deep-analysis alle tracce importate
    /// </summary>
    public class PipelineDeepEditApplier
    {
        #region Metodi pubblici

        /// <summary>
        /// Applica l'EditMap DeepAnalysis ai soli sottotitoli importati
        /// </summary>
        /// <param name="record">Record in elaborazione</param>
        /// <param name="subtitleTracks">Tracce sottotitolo lingua</param>
        /// <param name="processedLangSubTracks">Tracce sottotitolo processate</param>
        /// <param name="options">Opzioni operative</param>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <returns>True se l'applicazione è completata o non necessaria</returns>
        public bool ApplySubtitles(FileProcessingRecord record, List<TrackInfo> subtitleTracks, Dictionary<int, string> processedLangSubTracks, Options options, string ffmpegPath)
        {
            string tempFolder;
            int ffmpegTimeoutMs;
            SubtitleTimelineEditService subtitleService;
            string splitLabel;
            string processedFile;
            bool emptyTrack;

            if (!record.DeepAnalysisApplied || record.DeepAnalysisMap == null || record.DeepAnalysisMap.Operations.Count == 0)
            {
                return false;
            }

            if (subtitleTracks == null || subtitleTracks.Count == 0)
            {
                return true;
            }

            if (string.IsNullOrEmpty(ffmpegPath))
            {
                this.FailApply(record, "Deep analysis richiede ffmpeg per applicare tagli/insert ai sottotitoli importati");
                return false;
            }

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Applicazione taglia-cuci ai sottotitoli lang...");
            ConsoleHelper.Progress(LogSection.Deep, 92, "Deep: sottotitoli");

            tempFolder = AppSettingsService.Instance.GetTempFolder();
            ffmpegTimeoutMs = AppSettingsService.Instance.Settings.Advanced.SubtitleEdit.FfmpegTimeoutMs;
            subtitleService = new SubtitleTimelineEditService(ffmpegPath, tempFolder, ffmpegTimeoutMs, options.MkvMergePath);
            splitLabel = Path.GetFileNameWithoutExtension(record.LangFilePath);

            for (int s = 0; s < subtitleTracks.Count; s++)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Traccia sub " + subtitleTracks[s].Id + " (" + subtitleTracks[s].Codec + "): " + record.DeepAnalysisMap.Operations.Count + " operazioni da applicare");
                processedFile = subtitleService.Apply(record.LangFilePath, subtitleTracks[s].Id, subtitleTracks[s].Codec, record.DeepAnalysisMap, splitLabel, out emptyTrack);

                if (!string.IsNullOrEmpty(processedFile))
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, "  Timestamp sottotitoli riscritti -> output OK");
                    processedLangSubTracks[subtitleTracks[s].Id] = processedFile;
                }
                else if (emptyTrack)
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Traccia sub " + subtitleTracks[s].Id + " mantenuta invariata perché vuota");
                }
                else
                {
                    this.FailApply(record, "Deep analysis fallita sulla traccia sottotitoli lang " + subtitleTracks[s].Id);
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Registra un fallimento hard dell'applicazione edit map
        /// </summary>
        /// <param name="record">Record in elaborazione</param>
        /// <param name="message">Messaggio errore</param>
        private void FailApply(FileProcessingRecord record, string message)
        {
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  " + message);
            record.ErrorMessage = message;
            record.Status = FileStatus.Error;
        }

        #endregion
    }
}
