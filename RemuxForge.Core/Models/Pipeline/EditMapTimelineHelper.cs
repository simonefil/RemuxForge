using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Helper per conversioni temporali comuni tra EditMap, timeline lang originale e timeline renderizzata/source.
    /// </summary>
    public static class EditMapTimelineHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Converte una durata misurata sulla timeline source/finale nella durata equivalente sulla timeline lang originale.
        /// </summary>
        /// <param name="sourceDurationMs">Durata in millisecondi source/finale</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction applicato alla timeline lang</param>
        /// <returns>Durata equivalente in millisecondi lang</returns>
        public static int SourceDurationToLanguageDurationMs(int sourceDurationMs, double inverseRatio)
        {
            if (sourceDurationMs <= 0)
            {
                return 0;
            }

            if (inverseRatio <= 0.0)
            {
                return sourceDurationMs;
            }

            return Math.Max(1, (int)Math.Round(sourceDurationMs * inverseRatio));
        }

        /// <summary>
        /// Converte una durata della timeline lang originale nella durata equivalente sulla timeline source/finale.
        /// </summary>
        /// <param name="languageDurationMs">Durata in millisecondi lang originale</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction applicato alla timeline lang</param>
        /// <returns>Durata equivalente in millisecondi source/finale</returns>
        public static int LanguageDurationToSourceDurationMs(int languageDurationMs, double inverseRatio)
        {
            if (languageDurationMs <= 0)
            {
                return 0;
            }

            if (inverseRatio <= 0.0)
            {
                return languageDurationMs;
            }

            return Math.Max(1, (int)Math.Round(languageDurationMs / inverseRatio));
        }

        /// <summary>
        /// Converte una durata della timeline lang originale nella durata renderizzata dopo lo stretch.
        /// </summary>
        /// <param name="languageDurationMs">Durata in millisecondi lang originale</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <returns>Durata equivalente in millisecondi renderizzati</returns>
        public static int LanguageDurationToRenderedDurationMs(int languageDurationMs, double stretchRatio)
        {
            if (languageDurationMs <= 0)
            {
                return 0;
            }

            if (stretchRatio <= 0.0)
            {
                return languageDurationMs;
            }

            return Math.Max(1, (int)Math.Round(languageDurationMs * stretchRatio));
        }

        /// <summary>
        /// Restituisce il delta durata firmato prodotto da una operazione EditMap sulla timeline renderizzata/source.
        /// </summary>
        /// <param name="operation">Operazione EditMap</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <returns>Delta durata firmato in millisecondi</returns>
        public static int GetRenderedOperationDeltaMs(EditOperation operation, double stretchRatio)
        {
            int durationMs;
            if (operation == null)
            {
                return 0;
            }

            durationMs = LanguageDurationToRenderedDurationMs(operation.DurationMs, stretchRatio);
            if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
            {
                return durationMs;
            }

            if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
            {
                return -durationMs;
            }

            return 0;
        }

        /// <summary>
        /// Restituisce il delta offset source/finale prodotto da una operazione EditMap usando inverseRatio.
        /// </summary>
        /// <param name="operation">Operazione EditMap</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction applicato alla timeline lang</param>
        /// <returns>Delta offset firmato in millisecondi source/finale</returns>
        public static int GetSourceOperationDeltaMs(EditOperation operation, double inverseRatio)
        {
            int durationMs;
            if (operation == null)
            {
                return 0;
            }

            durationMs = LanguageDurationToSourceDurationMs(operation.DurationMs, inverseRatio);
            if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
            {
                return durationMs;
            }

            if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
            {
                return -durationMs;
            }

            return 0;
        }

        /// <summary>
        /// Calcola il delta renderizzato prodotto dalle operazioni precedenti a un indice
        /// </summary>
        /// <param name="operations">Operazioni editmap ordinate</param>
        /// <param name="operationIndex">Indice esclusivo dell'operazione corrente</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <returns>Delta cumulativo in millisecondi renderizzati</returns>
        public static int GetRenderedDeltaBeforeMs(List<EditOperation> operations, int operationIndex, double stretchRatio)
        {
            int result = 0;
            int limit;
            if (operations == null || operationIndex <= 0)
            {
                return result;
            }

            limit = operationIndex;
            if (limit > operations.Count)
            {
                limit = operations.Count;
            }

            for (int i = 0; i < limit; i++)
            {
                result += GetRenderedOperationDeltaMs(operations[i], stretchRatio);
            }

            return result;
        }

        /// <summary>
        /// Mappa un timestamp lang originale nella timeline renderizzata usando le operazioni precedenti
        /// </summary>
        /// <param name="languageTimestampMs">Timestamp nella timeline lang originale</param>
        /// <param name="operations">Operazioni editmap ordinate</param>
        /// <param name="operationIndex">Indice esclusivo dell'operazione corrente</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <returns>Timestamp renderizzato in millisecondi</returns>
        public static int LanguageTimestampToRenderedTimestampMs(int languageTimestampMs, List<EditOperation> operations, int operationIndex, double stretchRatio)
        {
            int renderedTimestampMs;
            if (languageTimestampMs <= 0)
            {
                renderedTimestampMs = 0;
            }
            else
            {
                renderedTimestampMs = LanguageDurationToRenderedDurationMs(languageTimestampMs, stretchRatio);
            }

            return renderedTimestampMs + GetRenderedDeltaBeforeMs(operations, operationIndex, stretchRatio);
        }

        /// <summary>
        /// Mappa un timestamp renderizzato nella timeline lang originale usando le operazioni precedenti
        /// </summary>
        /// <param name="renderedTimestampMs">Timestamp renderizzato in millisecondi</param>
        /// <param name="operations">Operazioni editmap ordinate</param>
        /// <param name="operationIndex">Indice esclusivo dell'operazione corrente</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <returns>Timestamp lang originale in millisecondi</returns>
        public static int RenderedTimestampToLanguageTimestampMs(int renderedTimestampMs, List<EditOperation> operations, int operationIndex, double stretchRatio)
        {
            int adjustedMs = renderedTimestampMs - GetRenderedDeltaBeforeMs(operations, operationIndex, stretchRatio);
            if (adjustedMs <= 0)
            {
                return 0;
            }

            if (stretchRatio <= 0.0)
            {
                return adjustedMs;
            }

            return Math.Max(0, (int)Math.Round(adjustedMs / stretchRatio));
        }

        #endregion
    }
}
