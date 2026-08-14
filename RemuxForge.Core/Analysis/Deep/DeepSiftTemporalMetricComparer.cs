using RemuxForge.Core.Models;
using System;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Confronta score e metriche temporali tramite rappresentazioni intere quantizzate per rendere deterministici confronti e spareggi
    /// </summary>
    internal static class DeepSiftTemporalMetricComparer
    {
        #region Costanti

        /// <summary>
        /// Fattore di scala per quantizzare score e margini con sei cifre decimali
        /// </summary>
        private const double METRIC_QUANTIZATION = 1000000.0;

        /// <summary>
        /// Fattore di scala per quantizzare i valori temporali espressi in millisecondi alla precisione del microsecondo
        /// </summary>
        private const double MILLISECOND_QUANTIZATION = 1000.0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Confronta la confidence SIFT composta delle due coppie applicando il margine minimo richiesto
        /// </summary>
        /// <param name="candidate">Coppia la cui confidence deve essere verificata</param>
        /// <param name="alternative">Coppia da usare come confronto</param>
        /// <param name="minimumMargin">Margine minimo che la confidence candidata deve mantenere</param>
        /// <returns>True quando la candidata è superiore all'alternativa secondo il margine richiesto</returns>
        public static bool HasHigherConfidence(DeepSiftAcceptedPairDiagnostic candidate, DeepSiftAcceptedPairDiagnostic alternative, double minimumMargin)
        {
            long candidateScore = QuantizeMetric(candidate.Score);
            long alternativeScore = QuantizeMetric(alternative.Score);
            long requiredMargin = QuantizeMetric(minimumMargin);
            if (requiredMargin == 0)
                return candidateScore > alternativeScore;
            return candidateScore - alternativeScore >= requiredMargin;
        }

        /// <summary>
        /// Converte una metrica in un intero deterministico conservando sei cifre decimali
        /// </summary>
        /// <param name="value">Score, rapporto o margine da quantizzare</param>
        /// <returns>Rappresentazione intera quantizzata con gli estremi di <see cref="long"/> per i valori non finiti</returns>
        public static long QuantizeMetric(double value)
        {
            if (double.IsNaN(value))
                return long.MinValue;
            return Quantize(value, METRIC_QUANTIZATION);
        }

        /// <summary>
        /// Converte un valore temporale espresso in millisecondi in un intero alla precisione del microsecondo
        /// </summary>
        /// <param name="value">PTS, copertura, dispersione o altra metrica espressa in millisecondi</param>
        /// <returns>Rappresentazione intera quantizzata con gli estremi di <see cref="long"/> per i valori non finiti</returns>
        public static long QuantizeMilliseconds(double value)
        {
            if (double.IsNaN(value))
                return long.MinValue;
            return Quantize(value, MILLISECOND_QUANTIZATION);
        }

        /// <summary>
        /// Calcola la semilarghezza dell'intervallo di incertezza dell'offset tra i PTS della coppia
        /// </summary>
        /// <param name="pair">Coppia SIFT con PTS e durate di frame e campionamento</param>
        /// <param name="scale">Rapporto temporale source-language applicato ai PTS language</param>
        /// <returns>Semilarghezza dell'intervallo di incertezza in millisecondi</returns>
        public static double GetPairUncertaintyMs(DeepSiftAcceptedPairDiagnostic pair, double scale)
        {
            double sourceFrameMs = pair.SourceFrameDurationMs > 0.0 ? pair.SourceFrameDurationMs : 1.0;
            double sourceSamplingMs = pair.SourceSamplingDurationMs > 0.0 ? pair.SourceSamplingDurationMs : sourceFrameMs;
            double languageFrameMs = pair.LanguageFrameDurationMs > 0.0 ? pair.LanguageFrameDurationMs : 1.0;
            double languageSamplingMs = pair.LanguageSamplingDurationMs > 0.0 ? pair.LanguageSamplingDurationMs : languageFrameMs;
            return Math.Max(1.0, (Math.Max(sourceFrameMs, sourceSamplingMs) + (Math.Max(languageFrameMs, languageSamplingMs) / scale)) * 0.5);
        }

        /// <summary>
        /// Calcola la semilarghezza dell'intervallo di incertezza sostituendo durate non finite o non positive con valori validi per il tracking globale
        /// </summary>
        /// <param name="pair">Coppia SIFT con PTS e durate di frame e campionamento</param>
        /// <param name="scale">Rapporto temporale source-language applicato ai PTS language</param>
        /// <returns>Semilarghezza dell'intervallo di incertezza in millisecondi con un minimo di 1 millisecondo</returns>
        public static double GetFinitePairUncertaintyMs(DeepSiftAcceptedPairDiagnostic pair, double scale)
        {
            double sourceQuantizationMs = GetEffectivePtsQuantizationMs(pair.SourceFrameDurationMs, pair.SourceSamplingDurationMs);
            double languageQuantizationMs = GetEffectivePtsQuantizationMs(pair.LanguageFrameDurationMs, pair.LanguageSamplingDurationMs) / scale;
            return Math.Max(1.0, (sourceQuantizationMs + languageQuantizationMs) * 0.5);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Converte un valore in un intero arrotondato e limitato all'intervallo di <see cref="long"/>
        /// </summary>
        /// <param name="value">Valore da quantizzare, eventualmente non finito</param>
        /// <param name="scale">Fattore di scala da applicare prima dell'arrotondamento</param>
        /// <returns>Valore intero arrotondato e limitato all'intervallo di <see cref="long"/></returns>
        private static long Quantize(double value, double scale)
        {
            if (double.IsPositiveInfinity(value))
                return long.MaxValue;
            if (double.IsNegativeInfinity(value))
                return long.MinValue;
            double scaled = value * scale;
            if (scaled >= long.MaxValue)
                return long.MaxValue;
            if (scaled <= long.MinValue)
                return long.MinValue;
            return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Determina il passo temporale effettivo sostituendo durate non finite o non positive con un fallback valido
        /// </summary>
        /// <param name="frameDurationMs">Durata nominale del frame in millisecondi</param>
        /// <param name="samplingDurationMs">Passo temporale di campionamento in millisecondi</param>
        /// <returns>Massimo passo temporale positivo usato per la quantizzazione PTS</returns>
        private static double GetEffectivePtsQuantizationMs(double frameDurationMs, double samplingDurationMs)
        {
            double frameQuantizationMs = frameDurationMs > 0.0 && double.IsFinite(frameDurationMs) ? frameDurationMs : 1.0;
            double samplingQuantizationMs = samplingDurationMs > 0.0 && double.IsFinite(samplingDurationMs) ? samplingDurationMs : frameQuantizationMs;
            return Math.Max(frameQuantizationMs, samplingQuantizationMs);
        }

        #endregion
    }
}
