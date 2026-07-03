using RemuxForge.Core.Models;
using System;
using System.Globalization;
using System.Text;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Normalizzatore valori metadata letti da MediaInfo e preset
    /// </summary>
    public static class MetadataValueNormalizer
    {
        #region Metodi pubblici

        /// <summary>
        /// Normalizza valori booleani testuali nel formato MKV 1/0
        /// </summary>
        /// <param name="value">Valore booleano testuale</param>
        /// <returns>1, 0, valore originale normalizzato o stringa vuota</returns>
        public static string NormalizeBoolean(string value)
        {
            string text = value != null ? value.Trim() : "";
            if (text.Length == 0)
                return "";

            if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
                return "1";

            if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "n", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
                return "0";

            return text;
        }

        /// <summary>
        /// Verifica se un valore testuale rappresenta vero
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Vero se il valore rappresenta true</returns>
        public static bool IsTruthy(string value)
        {
            string text = value != null ? value.Trim() : "";
            return string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "y", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converte un valore testuale MediaInfo in intero
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Valore intero, oppure zero</returns>
        public static int ParseInt(string value)
        {
            int result;
            string normalized = ExtractFirstIntegerText(value);
            if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                result = 0;

            return result;
        }

        /// <summary>
        /// Converte un valore testuale MediaInfo in long
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Valore long, oppure zero</returns>
        public static long ParseLong(string value)
        {
            long result;
            string normalized = ExtractFirstIntegerText(value);
            if (!long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                result = 0;

            return result;
        }

        /// <summary>
        /// Converte un valore testuale con unità opzionale in double confrontabile
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Valore numerico normalizzato</returns>
        public static double ParseDoubleWithUnit(string value)
        {
            double result;
            string text = value != null ? value.Trim() : "";
            string numberText = ExtractFirstDecimalText(value);
            string unit = ExtractFirstUnitText(value);

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return result;

            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                result = 0.0;

            return ApplyNumericUnit(result, unit);
        }

        /// <summary>
        /// Verifica se un valore contiene un'unità testuale esplicita
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>True se il valore contiene lettere</returns>
        public static bool HasExplicitUnit(string value)
        {
            string text = value != null ? value : "";

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLetter(text[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Indica se il tipo metadata è numerico o confrontabile come numero
        /// </summary>
        /// <param name="valueType">Tipo valore metadata</param>
        /// <returns>Vero se il tipo è numerico</returns>
        public static bool IsNumericValueType(MetadataFieldValueType valueType)
        {
            return valueType == MetadataFieldValueType.Integer ||
                valueType == MetadataFieldValueType.Decimal ||
                valueType == MetadataFieldValueType.Bytes ||
                valueType == MetadataFieldValueType.Duration;
        }

        /// <summary>
        /// Formatta una frequenza di campionamento Hz in kHz
        /// </summary>
        /// <param name="samplingRate">Frequenza in Hz</param>
        /// <returns>Frequenza in kHz, oppure stringa vuota</returns>
        public static string FormatSamplingRateKhz(string samplingRate)
        {
            string text = samplingRate != null ? samplingRate.Trim() : "";
            string numberText = ExtractFirstDecimalText(text);
            double number;
            double khz;

            if (numberText.Length == 0 || !double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return "";

            if (number <= 0)
                return "";

            if (text.IndexOf("khz", StringComparison.OrdinalIgnoreCase) >= 0)
                khz = number;
            else
                khz = number / 1000.0;

            return khz.ToString("0.#", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Estrae il primo numero intero da una stringa MediaInfo
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Numero testuale, oppure stringa vuota</returns>
        private static string ExtractFirstIntegerText(string value)
        {
            StringBuilder result = new StringBuilder();
            string text;

            if (value == null)
                return "";

            text = value.Trim();
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    result.Append(text[i]);
                }
                else if (result.Length > 0 && !char.IsWhiteSpace(text[i]))
                {
                    break;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Estrae il primo numero decimale da una stringa MediaInfo
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Numero decimale testuale, oppure stringa vuota</returns>
        private static string ExtractFirstDecimalText(string value)
        {
            StringBuilder result = new StringBuilder();
            bool hasDecimalSeparator = false;
            bool hasSign = false;
            string text;

            if (value == null)
                return "";

            text = value.Trim();
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    result.Append(text[i]);
                }
                else if ((text[i] == '-' || text[i] == '+') && result.Length == 0 && !hasSign)
                {
                    result.Append(text[i]);
                    hasSign = true;
                }
                else if ((text[i] == '.' || text[i] == ',') && result.Length > 0 && !hasDecimalSeparator)
                {
                    result.Append('.');
                    hasDecimalSeparator = true;
                }
                else if (result.Length > 0 && !char.IsWhiteSpace(text[i]))
                {
                    break;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Estrae la prima unità testuale da una stringa MediaInfo o condizione
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Unità testuale minuscola o stringa vuota</returns>
        private static string ExtractFirstUnitText(string value)
        {
            StringBuilder result = new StringBuilder();
            bool numberFound = false;
            bool unitStarted = false;
            string text;

            if (value == null)
                return "";

            text = value.Trim();
            for (int i = 0; i < text.Length; i++)
            {
                if (!numberFound)
                {
                    if (char.IsDigit(text[i]))
                        numberFound = true;

                    continue;
                }

                if (char.IsLetter(text[i]))
                {
                    result.Append(char.ToLowerInvariant(text[i]));
                    unitStarted = true;
                }
                else if (unitStarted)
                {
                    break;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Applica il moltiplicatore dell'unità numerica riconosciuta
        /// </summary>
        /// <param name="value">Valore numerico base</param>
        /// <param name="unit">Unità testuale normalizzata</param>
        /// <returns>Valore numerico convertito</returns>
        private static double ApplyNumericUnit(double value, string unit)
        {
            if (unit == "kb")
                return value * 1000.0;
            if (unit == "mb")
                return value * 1000.0 * 1000.0;
            if (unit == "gb")
                return value * 1000.0 * 1000.0 * 1000.0;
            if (unit == "tb")
                return value * 1000.0 * 1000.0 * 1000.0 * 1000.0;
            if (unit == "kib")
                return value * 1024.0;
            if (unit == "mib")
                return value * 1024.0 * 1024.0;
            if (unit == "gib")
                return value * 1024.0 * 1024.0 * 1024.0;
            if (unit == "tib")
                return value * 1024.0 * 1024.0 * 1024.0 * 1024.0;
            if (unit == "khz")
                return value * 1000.0;
            if (unit == "mhz")
                return value * 1000.0 * 1000.0;
            if (unit == "ms")
                return value;
            if (unit == "s" || unit == "sec" || unit == "secs" || unit == "second" || unit == "seconds")
                return value * 1000.0;
            if (unit == "m" || unit == "min" || unit == "mins" || unit == "minute" || unit == "minutes")
                return value * 60.0 * 1000.0;
            if (unit == "h" || unit == "hr" || unit == "hour" || unit == "hours")
                return value * 60.0 * 60.0 * 1000.0;

            return value;
        }

        #endregion
    }
}
