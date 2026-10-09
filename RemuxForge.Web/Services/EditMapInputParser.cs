using System.Globalization;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Interpreta i valori digitati nei campi dell'editor EditMap
    /// </summary>
    public static class EditMapInputParser
    {
        #region Metodi pubblici

        /// <summary>
        /// Interpreta un timestamp nei formati ss, mm:ss e hh:mm:ss, con millesimi opzionali dopo virgola o punto.
        /// I segmenti si leggono da destra: l'ultimo sono i secondi, il penultimo i minuti, il primo le ore
        /// </summary>
        /// <param name="text">Testo digitato</param>
        /// <param name="milliseconds">Istante risolto in millisecondi</param>
        /// <returns>True quando il testo è un istante non negativo nel formato atteso</returns>
        public static bool TryParseTimestamp(string text, out double milliseconds)
        {
            milliseconds = 0.0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] segments = text.Trim().Split(':');
            if (segments.Length > 3)
                return false;

            // L'ultimo segmento porta i secondi e gli eventuali millesimi
            string last = segments[segments.Length - 1];
            int separator = last.IndexOfAny(new[] { '.', ',' });
            string secondsText = separator >= 0 ? last.Substring(0, separator) : last;
            string fractionText = separator >= 0 ? last.Substring(separator + 1) : "";
            if (separator >= 0 && (fractionText.Length == 0 || fractionText.Length > 3 || !IsDigits(fractionText)))
                return false;
            if (!TryParseSegment(secondsText, segments.Length > 1, out int seconds))
                return false;

            long totalMs = seconds * 1000L;
            if (fractionText.Length > 0)
                totalMs += int.Parse(fractionText.PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture);

            // Minuti e ore: il segmento più a sinistra non ha limite superiore, quelli interni restano sotto 60
            long[] unitsMs = { 60000L, 3600000L };
            for (int i = segments.Length - 2, unit = 0; i >= 0; i--, unit++)
            {
                if (!TryParseSegment(segments[i], i > 0, out int value))
                    return false;
                totalMs += value * unitsMs[unit];
            }

            milliseconds = totalMs;
            return double.IsFinite(milliseconds);
        }

        /// <summary>
        /// Interpreta un guadagno in dB accettando sia la virgola sia il punto come separatore decimale
        /// </summary>
        /// <param name="text">Testo digitato</param>
        /// <param name="gainDb">Guadagno risolto in dB</param>
        /// <returns>True quando il testo è un numero finito</returns>
        public static bool TryParseGain(string text, out double gainDb)
        {
            gainDb = 0.0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.Trim();
            // Una sola virgola senza punto è il separatore decimale italiano
            if (normalized.IndexOf('.') < 0 && normalized.IndexOf(',') >= 0 && normalized.IndexOf(',') == normalized.LastIndexOf(','))
                normalized = normalized.Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out gainDb) && double.IsFinite(gainDb);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Interpreta un segmento intero del timestamp
        /// </summary>
        /// <param name="text">Cifre del segmento</param>
        /// <param name="bounded">True quando il segmento ha un segmento più grande alla sua sinistra e deve restare sotto 60</param>
        /// <param name="value">Valore del segmento</param>
        /// <returns>True quando il segmento è composto di sole cifre nel limite previsto</returns>
        private static bool TryParseSegment(string text, bool bounded, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || !IsDigits(text) || (bounded && text.Length > 2))
                return false;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                return false;
            return !bounded || value < 60;
        }

        /// <summary>
        /// Verifica che il testo contenga soltanto cifre ASCII
        /// </summary>
        /// <param name="text">Testo da verificare</param>
        /// <returns>True quando ogni carattere è una cifra da 0 a 9</returns>
        private static bool IsDigits(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsAsciiDigit(text[i]))
                    return false;
            }
            return true;
        }

        #endregion
    }
}
