using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Applica alle tracce sottotitoli importate le operazioni temporali prodotte dalla deep analysis
    /// </summary>
    public class PipelineDeepEditApplier
    {
        #region Metodi pubblici

        /// <summary>
        /// Applica la EditMap della deep analysis alle tracce sottotitoli importate
        ///
        /// Le tracce senza cue vengono rimosse dalla lista di merge, mentre i file riscritti
        /// vengono registrati nella mappa dei sottotitoli processati
        /// </summary>
        /// <param name="record">Record del file in elaborazione con la EditMap prodotta dalla deep analysis</param>
        /// <param name="subtitleTracks">Lista delle tracce sottotitoli importate da aggiornare</param>
        /// <param name="processedLangSubTracks">Mappa in cui registrare i file riscritti per ID traccia</param>
        /// <param name="options">Opzioni operative, inclusa la configurazione di mkvmerge</param>
        /// <param name="ffmpegPath">Percorso dell'eseguibile ffmpeg richiesto per la riscrittura</param>
        /// <returns>True se l'elaborazione è completata o non sono presenti tracce da riscrivere, false se la EditMap non è applicabile o si verifica un errore</returns>
        public bool ApplySubtitles(FileProcessingRecord record, List<TrackInfo> subtitleTracks, Dictionary<int, string> processedLangSubTracks, Options options, string ffmpegPath)
        {
            string tempFolder;
            int ffmpegTimeoutMs;
            SubtitleTimelineEditService subtitleService;
            string splitLabel;
            string processedFile;
            bool emptyTrack;

            // Verifica che la deep analysis abbia prodotto operazioni applicabili
            if (!record.DeepAnalysisApplied || record.DeepAnalysisMap == null || record.DeepAnalysisMap.Operations.Count == 0)
            {
                return false;
            }

            // L'assenza di tracce evita la creazione di file temporanei e non richiede ulteriori operazioni
            if (subtitleTracks == null || subtitleTracks.Count == 0)
            {
                return true;
            }

            if (string.IsNullOrEmpty(ffmpegPath))
            {
                this.FailApply(record, AppText.T("deep.temporal.applySubtitles.ffmpegRequired"));
                return false;
            }

            // Prepara il servizio condiviso e un'etichetta stabile per i file temporanei
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.applySubtitles.start"));
            ConsoleHelper.Progress(LogSection.Deep, 92, AppText.T("deep.temporal.applySubtitles.progress"));

            tempFolder = AppSettingsService.Instance.GetTempFolder();
            ffmpegTimeoutMs = AppSettingsService.Instance.Settings.Advanced.SubtitleEdit.FfmpegTimeoutMs;
            subtitleService = new SubtitleTimelineEditService(ffmpegPath, tempFolder, ffmpegTimeoutMs, options.MkvMergePath);
            splitLabel = Path.GetFileNameWithoutExtension(record.LangFilePath);

            // Elabora le tracce importate una alla volta per associare ogni output al relativo ID
            for (int s = 0; s < subtitleTracks.Count; s++)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, AppText.F("deep.temporal.applySubtitles.track", subtitleTracks[s].Id, subtitleTracks[s].Codec, record.DeepAnalysisMap.Operations.Count));
                processedFile = subtitleService.Apply(record.LangFilePath, subtitleTracks[s].Id, subtitleTracks[s].Codec, record.DeepAnalysisMap, splitLabel, out emptyTrack);

                if (!string.IsNullOrEmpty(processedFile))
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, AppText.T("deep.temporal.applySubtitles.completed"));
                    processedLangSubTracks[subtitleTracks[s].Id] = processedFile;
                }
                else if (emptyTrack)
                {
                    // Una traccia senza cue non può essere inclusa nel merge e viene rimossa dalla lista corrente
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, AppText.F("deep.temporal.applySubtitles.emptyTrack", subtitleTracks[s].Id));
                    subtitleTracks.RemoveAt(s);
                    s--;
                }
                else
                {
                    // Interrompe l'applicazione per evitare di proseguire con un merge parziale
                    this.FailApply(record, AppText.F("deep.temporal.applySubtitles.failedTrack", subtitleTracks[s].Id));
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Imposta il record in errore dopo un fallimento dell'applicazione della EditMap
        /// </summary>
        /// <param name="record">Record del file da aggiornare</param>
        /// <param name="message">Messaggio di errore da registrare e mostrare nei log</param>
        private void FailApply(FileProcessingRecord record, string message)
        {
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  " + message);
            record.ErrorMessage = message;
            record.Status = FileStatus.Error;
        }

        #endregion
    }
}
