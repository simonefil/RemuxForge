using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Applica la timeline edit map a cue testuali e timestamp bitmap
    /// </summary>
    internal static class SubtitleTimelineMapper
    {
        #region Metodi pubblici

        /// <summary>
        /// Applica cut/insert a un cue sottotitolo, splittandolo o rimuovendolo quando necessario
        /// </summary>
        /// <param name="startMs">Inizio cue nella timeline lingua originale</param>
        /// <param name="endMs">Fine cue nella timeline lingua originale</param>
        /// <param name="editMap">EditMap da applicare</param>
        /// <returns>Intervalli cue risultanti nella timeline editata</returns>
        public static List<SubtitleCueInterval> ApplyOperationsToCue(long startMs, long endMs, EditMap editMap)
        {
            List<SubtitleCueInterval> result = new List<SubtitleCueInterval>();
            SubtitleCueInterval interval = new SubtitleCueInterval();
            long cumulativeShiftMs = 0;
            interval.StartMs = startMs;
            interval.EndMs = endMs;
            result.Add(interval);

            for (int i = 0; i < editMap.Operations.Count; i++)
            {
                EditOperation op = editMap.Operations[i];
                long operationStartMs = op.LangTimestampMs + cumulativeShiftMs;
                long durationMs = op.DurationMs;

                // I cut possono spezzare il cue in due intervalli o eliminarlo interamente
                if (string.Equals(op.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    ApplyCutToIntervals(result, operationStartMs, durationMs);
                    cumulativeShiftMs -= durationMs;
                }
                else if (string.Equals(op.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                {
                    // Gli insert spostano solo gli intervalli successivi al punto di inserimento
                    ApplyInsertToIntervals(result, operationStartMs, durationMs);
                    cumulativeShiftMs += durationMs;
                }
            }

            RemoveInvalidIntervals(result);
            return result;
        }

        /// <summary>
        /// Mappa un timestamp singolo sulla timeline editata. Ritorna -1 se cade dentro un taglio
        /// </summary>
        /// <param name="timestampMs">Timestamp nella timeline lingua originale</param>
        /// <param name="editMap">EditMap da applicare</param>
        /// <returns>Timestamp mappato, oppure -1 se rimosso da un cut</returns>
        public static long MapPacketTimestamp(long timestampMs, EditMap editMap)
        {
            long result = timestampMs;
            long cumulativeShiftMs = 0;
            for (int i = 0; i < editMap.Operations.Count; i++)
            {
                EditOperation op = editMap.Operations[i];
                long operationStartMs = op.LangTimestampMs + cumulativeShiftMs;
                long durationMs = op.DurationMs;

                // Un timestamp dentro un cut non deve piu' essere muxato
                if (string.Equals(op.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    if (result >= operationStartMs && result < operationStartMs + durationMs)
                    {
                        return -1;
                    }
                    if (result >= operationStartMs + durationMs)
                    {
                        result -= durationMs;
                    }
                    cumulativeShiftMs -= durationMs;
                }
                else if (string.Equals(op.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                {
                    // Gli insert non invalidano il timestamp, lo traslano se e' successivo
                    if (result >= operationStartMs)
                    {
                        result += durationMs;
                    }
                    cumulativeShiftMs += durationMs;
                }
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Applica un taglio a tutti gli intervalli cue gia' normalizzati sulla timeline corrente
        /// </summary>
        /// <param name="intervals">Intervalli da modificare in-place</param>
        /// <param name="cutStartMs">Inizio taglio sulla timeline corrente</param>
        /// <param name="durationMs">Durata taglio</param>
        private static void ApplyCutToIntervals(List<SubtitleCueInterval> intervals, long cutStartMs, long durationMs)
        {
            long cutEndMs = cutStartMs + durationMs;

            for (int i = 0; i < intervals.Count; i++)
            {
                SubtitleCueInterval interval = intervals[i];

                // Prima del taglio il cue resta invariato; dopo il taglio viene traslato indietro
                if (interval.EndMs <= cutStartMs)
                {
                    continue;
                }

                if (interval.StartMs >= cutEndMs)
                {
                    interval.StartMs -= durationMs;
                    interval.EndMs -= durationMs;
                }
                else if (interval.StartMs >= cutStartMs && interval.EndMs <= cutEndMs)
                {
                    // Cue interamente rimosso dal cut: verra' eliminato nel pass finale
                    interval.StartMs = 0;
                    interval.EndMs = 0;
                }
                else if (interval.StartMs < cutStartMs && interval.EndMs > cutEndMs)
                {
                    // Cue che attraversa tutto il taglio: si accorcia senza creare un secondo evento
                    interval.EndMs -= durationMs;
                }
                else if (interval.StartMs < cutStartMs && interval.EndMs > cutStartMs)
                {
                    interval.EndMs = cutStartMs;
                }
                else if (interval.StartMs < cutEndMs && interval.EndMs > cutEndMs)
                {
                    interval.StartMs = cutStartMs;
                    interval.EndMs -= durationMs;
                }
            }
        }

        /// <summary>
        /// Applica un inserimento di silenzio/spazio timeline agli intervalli cue
        /// </summary>
        /// <param name="intervals">Intervalli da modificare in-place</param>
        /// <param name="insertMs">Timestamp inserimento sulla timeline corrente</param>
        /// <param name="durationMs">Durata inserimento</param>
        private static void ApplyInsertToIntervals(List<SubtitleCueInterval> intervals, long insertMs, long durationMs)
        {
            for (int i = 0; i < intervals.Count; i++)
            {
                SubtitleCueInterval interval = intervals[i];
                if (interval.StartMs >= insertMs)
                {
                    // Cue dopo l'insert: trasla interamente in avanti
                    interval.StartMs += durationMs;
                    interval.EndMs += durationMs;
                }
                else if (interval.EndMs > insertMs)
                {
                    // Cue che attraversa l'insert: resta ancorato all'inizio e aumenta la durata
                    interval.EndMs += durationMs;
                }
            }
        }

        /// <summary>
        /// Elimina intervalli vuoti o invertiti prodotti dai tagli
        /// </summary>
        /// <param name="intervals">Lista intervalli da pulire</param>
        private static void RemoveInvalidIntervals(List<SubtitleCueInterval> intervals)
        {
            for (int i = intervals.Count - 1; i >= 0; i--)
            {
                if (intervals[i].EndMs <= intervals[i].StartMs)
                {
                    intervals.RemoveAt(i);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Intervallo cue sottotitolo dopo applicazione edit map
    /// </summary>
    internal class SubtitleCueInterval
    {
        #region Proprieta

        /// <summary>
        /// Timestamp iniziale del cue
        /// </summary>
        public long StartMs { get; set; }

        /// <summary>
        /// Timestamp finale del cue
        /// </summary>
        public long EndMs { get; set; }

        #endregion
    }
}
