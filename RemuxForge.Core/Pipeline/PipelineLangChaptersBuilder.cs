using RemuxForge.Core.Chapters;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// «Copia capitoli da lang»: legge i capitoli del file lingua e li porta nel tempo del source con la stessa
    /// trasformazione dei sottotitoli lang (C9): EditMap della deep analysis, poi stretch e ritardo sottotitoli
    /// </summary>
    public class PipelineLangChaptersBuilder
    {
        #region Costanti

        /// <summary>
        /// Nanosecondi in un millisecondo
        /// </summary>
        private const long NS_PER_MS = 1000000L;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce l'XML dei capitoli lang ricalcolati per il record
        /// </summary>
        /// <param name="record">Record in elaborazione</param>
        /// <param name="sourceDurationNs">Durata del contenitore source: i capitoli oltre vengono eliminati (C7)</param>
        /// <param name="effectiveSubDelayMs">Ritardo sottotitoli effettivo (sync + globale + manuale)</param>
        /// <param name="stretchFactor">Fattore di stretch dei sottotitoli, vuoto se assente</param>
        /// <param name="mkvExtractPath">Percorso di mkvextract</param>
        /// <param name="tempFolder">Cartella dei temporanei</param>
        /// <param name="chaptersFile">XML scritto, vuoto se il lang non ha capitoli (C8)</param>
        /// <returns>True se si può procedere; false con il record già in errore</returns>
        public bool Build(FileProcessingRecord record, long sourceDurationNs, int effectiveSubDelayMs, string stretchFactor, string mkvExtractPath, string tempFolder, out string chaptersFile)
        {
            chaptersFile = "";
            ConsoleHelper.Write(LogSection.Merge, LogLevel.Phase, AppText.T("remux.langChapters.start"));

            ChapterReadResult read = ChapterTimelineService.Read(mkvExtractPath, record.LangFilePath);
            if (read.Error.Length > 0)
            {
                // Capitoli ordered o illeggibili bloccano il file: copiarli in parte darebbe un output ingannevole (C16)
                this.Fail(record, AppText.F("remux.langChapters.readFailed", read.Error));
                return false;
            }
            foreach (string warning in read.Warnings)
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "  " + warning);

            if (read.Chapters.Count == 0)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Notice, AppText.T("remux.langChapters.none"));
                return true;
            }
            if (!EditMapTimelineHelper.TryParseStretchFactor(stretchFactor, out double ratio, out _))
            {
                this.Fail(record, AppText.F("remux.langChapters.invalidStretch", stretchFactor));
                return false;
            }

            EditMap editMap = record.DeepAnalysisApplied && record.DeepAnalysisMap != null && record.DeepAnalysisMap.Operations.Count > 0 ? record.DeepAnalysisMap : null;
            Func<long, ChapterMapResult> mapper = BuildMapper(editMap, ratio, effectiveSubDelayMs * NS_PER_MS);
            int junctions = 0;
            foreach (ChapterMark chapter in read.Chapters)
            {
                if (mapper(chapter.StartNs).Kind == ChapterMapKind.Junction)
                    junctions++;
            }

            List<ChapterMark> mapped = ChapterTimelineService.Map(read.Chapters, mapper, sourceDurationNs);
            ConsoleHelper.Write(LogSection.Merge, LogLevel.Info, AppText.F("remux.langChapters.summary", mapped.Count, read.Chapters.Count, junctions, read.Chapters.Count - mapped.Count));
            if (mapped.Count == 0)
                return true;

            chaptersFile = Path.Combine(tempFolder, "remuxforge-lang-chapters-" + Guid.NewGuid().ToString("N") + ".xml");
            ChapterTimelineService.WriteXmlFile(mapped, chaptersFile);
            return true;
        }

        /// <summary>
        /// Trasformazione di un istante lang nel tempo del source, nello stesso ordine dei sottotitoli:
        /// operazioni EditMap come SubtitleTimelineMapper (un istante in un taglio va alla giunzione, C7),
        /// poi mkvmerge --sync d,f = t × f + d
        /// </summary>
        /// <param name="editMap">EditMap applicata ai sottotitoli, null se assente</param>
        /// <param name="stretchRatio">Fattore di stretch</param>
        /// <param name="delayNs">Ritardo sottotitoli effettivo in nanosecondi</param>
        /// <returns>Mappa per ChapterTimelineService.Map</returns>
        public static Func<long, ChapterMapResult> BuildMapper(EditMap editMap, double stretchRatio, long delayNs)
        {
            return timeNs =>
            {
                long result = timeNs;
                bool junction = false;

                if (editMap != null)
                {
                    long cumulativeShiftNs = 0;
                    foreach (EditOperation op in editMap.Operations)
                    {
                        long operationStartNs = op.LangTimestampMs * NS_PER_MS + cumulativeShiftNs;
                        long durationNs = op.DurationMs * NS_PER_MS;
                        if (string.Equals(op.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                        {
                            if (result >= operationStartNs && result < operationStartNs + durationNs)
                            {
                                result = operationStartNs;
                                junction = true;
                            }
                            else if (result >= operationStartNs + durationNs)
                            {
                                result -= durationNs;
                            }
                            cumulativeShiftNs -= durationNs;
                        }
                        else if (string.Equals(op.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                        {
                            if (result >= operationStartNs)
                                result += durationNs;
                            cumulativeShiftNs += durationNs;
                        }
                    }
                }

                long output = (long)Math.Round(result * stretchRatio, MidpointRounding.AwayFromZero) + delayNs;
                return new ChapterMapResult(junction ? ChapterMapKind.Junction : ChapterMapKind.Mapped, output);
            };
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Mette il record in errore
        /// </summary>
        /// <param name="record">Record</param>
        /// <param name="message">Messaggio localizzato</param>
        private void Fail(FileProcessingRecord record, string message)
        {
            ConsoleHelper.Write(LogSection.Merge, LogLevel.Error, "  " + message);
            record.ErrorMessage = message;
            record.Status = FileStatus.Error;
        }

        #endregion
    }
}
