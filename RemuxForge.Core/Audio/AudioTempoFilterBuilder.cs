using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Audio
{
    /// <summary>
    /// Costruisce catene atempo FFmpeg sicure a partire dal rapporto di durata RemuxForge
    /// </summary>
    public static class AudioTempoFilterBuilder
    {
        #region Costanti

        private const double IDENTITY_TOLERANCE = 0.0001;
        private const double MIN_TEMPO = 0.5;
        private const double MAX_TEMPO = 2.0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Converte un rapporto di durata nella catena atempo FFmpeg equivalente
        /// </summary>
        /// <param name="stretchRatio">Moltiplicatore della durata finale</param>
        /// <param name="audioTempo">Moltiplicatore di velocità FFmpeg risolto</param>
        /// <param name="filter">Catena atempo, vuota per identità</param>
        /// <param name="errorMessage">Errore di validazione, vuoto se valido</param>
        /// <returns>True se il rapporto è valido</returns>
        public static bool TryBuild(double stretchRatio, out double audioTempo, out string filter, out string errorMessage)
        {
            audioTempo = 1.0;
            filter = "";
            errorMessage = "";

            if (double.IsNaN(stretchRatio) || double.IsInfinity(stretchRatio) || stretchRatio <= 0.0)
            {
                errorMessage = "Fattore stretch audio non valido";
                return false;
            }

            audioTempo = 1.0 / stretchRatio;
            return TryBuildFromTempo(audioTempo, out filter, out errorMessage);
        }

        /// <summary>
        /// Costruisce una catena atempo da un moltiplicatore di velocità già risolto
        /// </summary>
        /// <param name="audioTempo">Moltiplicatore di velocità FFmpeg</param>
        /// <param name="filter">Catena atempo, vuota per identità</param>
        /// <param name="errorMessage">Errore di validazione, vuoto se valido</param>
        /// <returns>True se il tempo è valido</returns>
        public static bool TryBuildFromTempo(double audioTempo, out string filter, out string errorMessage)
        {
            List<double> factors = new List<double>();
            double remainingTempo = audioTempo;

            filter = "";
            errorMessage = "";

            if (double.IsNaN(audioTempo) || double.IsInfinity(audioTempo) || audioTempo <= 0.0)
            {
                errorMessage = "Tempo audio FFmpeg non valido";
                return false;
            }

            if (Math.Abs(audioTempo - 1.0) <= IDENTITY_TOLERANCE)
            {
                return true;
            }

            while (remainingTempo < MIN_TEMPO)
            {
                factors.Add(MIN_TEMPO);
                remainingTempo /= MIN_TEMPO;
            }

            while (remainingTempo > MAX_TEMPO)
            {
                factors.Add(MAX_TEMPO);
                remainingTempo /= MAX_TEMPO;
            }

            if (Math.Abs(remainingTempo - 1.0) > IDENTITY_TOLERANCE)
            {
                factors.Add(remainingTempo);
            }

            for (int i = 0; i < factors.Count; i++)
            {
                if (i > 0)
                {
                    filter += ",";
                }
                filter += "atempo=" + factors[i].ToString("0.########", CultureInfo.InvariantCulture);
            }

            return true;
        }

        /// <summary>
        /// True se il rapporto rappresenta l'identità entro la tolleranza audio locale
        /// </summary>
        /// <param name="stretchRatio">Rapporto di durata</param>
        /// <returns>True per identità</returns>
        public static bool IsIdentity(double stretchRatio)
        {
            return Math.Abs(stretchRatio - 1.0) <= IDENTITY_TOLERANCE;
        }

        #endregion
    }
}
