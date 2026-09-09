using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Verification
{
    /// <summary>
    /// Misura un solo offset audio dopo il solver video, senza decidere operazioni o durate
    /// </summary>
    internal class AudioOffsetEstimator
    {
        #region Costanti

        /// <summary>Durata delle finestre indipendenti nei tratti mantenuti</summary>
        private const double WINDOW_MS = 8000.0;
        /// <summary>Distanza delle finestre dalle giunzioni video</summary>
        private const double GUARD_MS = 3000.0;
        /// <summary>Correlazione minima degli inviluppi nella lingua comune</summary>
        private const double MIN_CORRELATION = 0.8;
        /// <summary>Dispersione massima, pari a due campioni degli inviluppi</summary>
        private const double MAX_SPREAD_MS = 20.0;
        /// <summary>Margine minimo rispetto a un altro massimo distante almeno cento millisecondi</summary>
        private const double MIN_PEAK_MARGIN = 0.03;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che tutti i tratti abbastanza lunghi sostengano lo stesso residuo
        /// </summary>
        /// <param name="pair">Timeline video già normalizzate</param>
        /// <param name="outcome">Risultato video definitivo</param>
        /// <param name="audio">Inviluppi con lingua comune verificata</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Misure e compensazione, oppure motivo dell'astensione</returns>
        public DeepAudioOffsetDiagnostic Measure(PairSignals pair, EditAnalysisOutcome outcome, AudioEnvelopePair audio, CancellationToken cancellation)
        {
            DeepAudioOffsetDiagnostic result = new DeepAudioOffsetDiagnostic();
            if (audio == null || !audio.SharedLanguage)
            {
                result.Reason = audio == null ? "audio_unavailable" : "no_shared_language";
                return result;
            }
            List<DeepAnalysisPlateau> plateaus = new EditMapConverter().BuildPlateaus(pair, outcome);
            List<double> residuals = new List<double>();
            bool missingPlateau = false;
            foreach (DeepAnalysisPlateau plateau in plateaus)
            {
                double start = Math.Max(plateau.StartMs, Math.Max(audio.OriginMs, audio.LanguageStartMs - plateau.OffsetMs)) + GUARD_MS;
                double end = Math.Min(plateau.EndMs, audio.LanguageEndMs - plateau.OffsetMs) - GUARD_MS;
                if (end - start < WINDOW_MS)
                    continue;
                int accepted = 0;
                double previousEnd = double.NegativeInfinity;
                for (int i = 0; i < 3; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double time = start + (end - start - WINDOW_MS) * (i + 0.5) / 3.0;
                    if (time < previousEnd)
                        continue;
                    previousEnd = time + WINDOW_MS;
                    DeepAudioOffsetWindow window = Correlate(audio, time, plateau.OffsetMs, cancellation);
                    result.Windows.Add(window);
                    if (!window.Reliable)
                        continue;
                    residuals.Add(window.ResidualMs);
                    accepted++;
                }
                if (accepted == 0)
                    missingPlateau = true;
            }
            if (residuals.Count < 3 || missingPlateau)
            {
                result.Reason = "insufficient_distributed_evidence";
                return result;
            }
            residuals.Sort();
            result.SpreadMs = residuals[residuals.Count - 1] - residuals[0];
            if (result.SpreadMs > MAX_SPREAD_MS)
            {
                result.Reason = "non_constant_offset";
                return result;
            }
            double median = residuals.Count % 2 == 1 ? residuals[residuals.Count / 2] : (residuals[residuals.Count / 2 - 1] + residuals[residuals.Count / 2]) / 2.0;
            result.Accepted = true;
            result.Reason = "";
            result.LanguageOffsetMs = (int)Math.Round(-median / pair.Stretch, MidpointRounding.AwayFromZero);
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Confronta finestre degli inviluppi sulla loro griglia, con controllo di dinamica e massimi concorrenti
        /// </summary>
        /// <param name="audio">Segnali sulla griglia comune</param>
        /// <param name="startMs">Inizio source richiesto</param>
        /// <param name="videoOffsetMs">Offset video congelato</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Misura locale anche quando non affidabile</returns>
        private static DeepAudioOffsetWindow Correlate(AudioEnvelopePair audio, double startMs, double videoOffsetMs, CancellationToken cancellation)
        {
            double step = AudioEnvelopeExtractor.STEP_MS;
            int length = (int)(WINDOW_MS / step);
            int sourceIndex = (int)Math.Round((startMs - audio.OriginMs) / step);
            DeepAudioOffsetWindow result = new DeepAudioOffsetWindow { SourceStartMs = audio.OriginMs + sourceIndex * step };
            if (sourceIndex < 0 || sourceIndex + length > audio.Source.Length)
                return result;
            double mean = 0.0;
            for (int i = 0; i < length; i++)
                mean += audio.Source[sourceIndex + i];
            mean /= length;
            double energy = 0.0;
            for (int i = 0; i < length; i++)
                energy += Math.Pow(audio.Source[sourceIndex + i] - mean, 2);
            if (energy / length < 1.0)
                return result;

            int radius = (int)(EditAnalysisProfile.AUDIO_SCAN_RADIUS_MS / step);
            double[] scores = new double[2 * radius + 1];
            Array.Fill(scores, -1.0);
            int best = -1;
            for (int k = 0; k < scores.Length; k++)
            {
                cancellation.ThrowIfCancellationRequested();
                double target = result.SourceStartMs + videoOffsetMs + (k - radius) * step;
                if (target < audio.LanguageStartMs || target + WINDOW_MS > audio.LanguageEndMs)
                    continue;
                int languageIndex = (int)Math.Round((target - audio.OriginMs) / step);
                if (languageIndex < 0 || languageIndex + length > audio.Language.Length)
                    continue;
                double languageMean = 0.0;
                for (int i = 0; i < length; i++)
                    languageMean += audio.Language[languageIndex + i];
                languageMean /= length;
                double cross = 0.0;
                double languageEnergy = 0.0;
                for (int i = 0; i < length; i++)
                {
                    double left = audio.Source[sourceIndex + i] - mean;
                    double right = audio.Language[languageIndex + i] - languageMean;
                    cross += left * right;
                    languageEnergy += right * right;
                }
                if (languageEnergy / length < 1.0)
                    continue;
                scores[k] = cross / Math.Sqrt(energy * languageEnergy);
                if (best < 0 || scores[k] > scores[best])
                    best = k;
            }
            if (best < 0)
                return result;
            double second = -1.0;
            for (int k = 0; k < scores.Length; k++)
            {
                if (Math.Abs(k - best) * step >= 100.0)
                    second = Math.Max(second, scores[k]);
            }
            result.Correlation = scores[best];
            result.ResidualMs = (best - radius) * step;
            result.Reliable = best > 0 && best < scores.Length - 1 && result.Correlation >= MIN_CORRELATION && result.Correlation - second >= MIN_PEAK_MARGIN;
            return result;
        }

        #endregion
    }
}
