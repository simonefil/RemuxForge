using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Configuration
{
    /// <summary>
    /// Metodi utility per mappatura e formattazione canali audio
    /// </summary>
    public static class AudioChannelHelper
    {
        #region Variabili statiche

        /// <summary>
        /// Alias layout testuali MediaInfo ordinati dal più specifico al più generico
        /// </summary>
        private static readonly KeyValuePair<string, string>[] s_channelLayoutAliases = new KeyValuePair<string, string>[]
        {
            new KeyValuePair<string, string>("9.1.6", "9.1.6"),
            new KeyValuePair<string, string>("9.1.4", "9.1.4"),
            new KeyValuePair<string, string>("7.1.6", "7.1.6"),
            new KeyValuePair<string, string>("7.1.4", "7.1.4"),
            new KeyValuePair<string, string>("7.1.2", "7.1.2"),
            new KeyValuePair<string, string>("7.1", "7.1"),
            new KeyValuePair<string, string>("6.1", "6.1"),
            new KeyValuePair<string, string>("5.1.4", "5.1.4"),
            new KeyValuePair<string, string>("5.1.2", "5.1.2"),
            new KeyValuePair<string, string>("5.1", "5.1"),
            new KeyValuePair<string, string>("5.0", "5.0"),
            new KeyValuePair<string, string>("4.1", "4.1"),
            new KeyValuePair<string, string>("4.0", "4.0"),
            new KeyValuePair<string, string>("3.1", "3.1"),
            new KeyValuePair<string, string>("3.0", "3.0"),
            new KeyValuePair<string, string>("2.1", "2.1"),
            new KeyValuePair<string, string>("2.0", "2.0"),
            new KeyValuePair<string, string>("1.0", "1.0"),
            new KeyValuePair<string, string>("quad", "4.0"),
            new KeyValuePair<string, string>("stereo", "2.0"),
            new KeyValuePair<string, string>("mono", "1.0")
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce il nome layout canali standard per libopus in base al numero di canali
        /// Necessario per normalizzare layout non standard (es. 5.1 side, 7.1 side)
        /// </summary>
        /// <param name="channels">Numero di canali audio</param>
        /// <returns>Nome layout standard o stringa vuota se mono/stereo (non serve remap)</returns>
        public static string GetStandardChannelLayout(int channels)
        {
            string result = "";
            if (channels == 3)
                result = "2.1";
            else if (channels == 4)
                result = "quad";
            else if (channels == 5)
                result = "5.0";
            else if (channels == 6)
                result = "5.1";
            else if (channels == 7)
                result = "6.1";
            else if (channels == 8)
                result = "7.1";

            return result;
        }

        /// <summary>
        /// Determina il channel layout per ffmpeg dal numero di canali
        /// Usato per generazione silenzio e operazioni che richiedono sempre un layout valido
        /// </summary>
        /// <param name="channels">Numero canali</param>
        /// <returns>Stringa channel layout (sempre un valore valido)</returns>
        public static string GetChannelLayout(int channels)
        {
            string layout;

            if (channels <= 1)
                layout = "mono";
            else if (channels == 2)
                layout = "stereo";
            else if (channels == 3)
                layout = "2.1";
            else if (channels == 4)
                layout = "quad";
            else if (channels == 5)
                layout = "5.0";
            else if (channels == 6)
                layout = "5.1";
            else if (channels == 7)
                layout = "6.1";
            else
                layout = "7.1";

            return layout;
        }

        /// <summary>
        /// Determina il layout canali finale supportato dall'encoder AC-3
        /// </summary>
        /// <param name="channels">Numero canali sorgente</param>
        /// <returns>Layout FFmpeg compatibile con AC-3</returns>
        public static string GetAc3ChannelLayout(int channels)
        {
            string layout;

            if (channels > 6)
                layout = "5.1";
            else
                layout = GetChannelLayout(channels);

            return layout;
        }

        /// <summary>
        /// Determina il numero canali finale supportato dall'encoder AC-3
        /// </summary>
        /// <param name="channels">Numero canali sorgente</param>
        /// <returns>Numero canali finale per AC-3</returns>
        public static int GetAc3ChannelCount(int channels)
        {
            int result;

            if (channels <= 1)
                result = 1;
            else if (channels > 6)
                result = 6;
            else
                result = channels;

            return result;
        }

        /// <summary>
        /// Formatta il layout canali in formato numerico per display (1.0, 2.0, 5.1, 7.1)
        /// </summary>
        /// <param name="channels">Numero canali audio</param>
        /// <returns>Stringa layout o vuota se canali non validi</returns>
        public static string FormatChannels(int channels)
        {
            string result = "";
            if (channels == 1)
                result = "1.0";
            else if (channels == 2)
                result = "2.0";
            else if (channels == 3)
                result = "2.1";
            else if (channels == 4)
                result = "4.0";
            else if (channels == 5)
                result = "5.0";
            else if (channels == 6)
                result = "5.1";
            else if (channels == 7)
                result = "6.1";
            else if (channels == 8)
                result = "7.1";
            else if (channels == 10)
                result = "7.1.2";
            else if (channels == 12)
                result = "7.1.4";
            else if (channels == 16)
                result = "9.1.6";
            else if (channels > 0)
                result = channels.ToString(CultureInfo.InvariantCulture) + "ch";

            return result;
        }

        /// <summary>
        /// Formatta il layout canali partendo da un valore testuale MediaInfo
        /// </summary>
        /// <param name="channels">Numero canali testuale</param>
        /// <returns>Stringa layout o vuota se canali non validi</returns>
        public static string FormatChannels(string channels)
        {
            string text = channels != null ? channels.Trim() : "";

            if (string.IsNullOrEmpty(text))
                return "";

            for (int i = 0; i < s_channelLayoutAliases.Length; i++)
            {
                if (text.StartsWith(s_channelLayoutAliases[i].Key, StringComparison.OrdinalIgnoreCase))
                    return s_channelLayoutAliases[i].Value;
            }

            return FormatChannels(ParseChannelCount(text));
        }

        /// <summary>
        /// Estrae il numero canali da un valore testuale
        /// </summary>
        /// <param name="channels">Valore canali testuale</param>
        /// <returns>Numero canali, oppure zero</returns>
        public static int ParseChannelCount(string channels)
        {
            int result = 0;
            string text;

            if (channels == null)
                return result;

            text = channels.Trim();
            if (string.IsNullOrEmpty(text))
                return result;

            if (text.StartsWith("9.1.6", StringComparison.OrdinalIgnoreCase))
                return 16;
            if (text.StartsWith("9.1.4", StringComparison.OrdinalIgnoreCase))
                return 14;
            if (text.StartsWith("7.1.6", StringComparison.OrdinalIgnoreCase))
                return 14;
            if (text.StartsWith("7.1.4", StringComparison.OrdinalIgnoreCase))
                return 12;
            if (text.StartsWith("7.1.2", StringComparison.OrdinalIgnoreCase))
                return 10;
            if (text.StartsWith("5.1.4", StringComparison.OrdinalIgnoreCase))
                return 10;
            if (text.StartsWith("5.1.2", StringComparison.OrdinalIgnoreCase))
                return 8;
            if (text.StartsWith("1.0", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "mono", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (text.StartsWith("2.0", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "stereo", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (text.StartsWith("2.1", StringComparison.OrdinalIgnoreCase) || text.StartsWith("3.0", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (text.StartsWith("3.1", StringComparison.OrdinalIgnoreCase) || text.StartsWith("4.0", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "quad", StringComparison.OrdinalIgnoreCase))
                return 4;
            if (text.StartsWith("4.1", StringComparison.OrdinalIgnoreCase) || text.StartsWith("5.0", StringComparison.OrdinalIgnoreCase))
                return 5;
            if (text.StartsWith("5.1", StringComparison.OrdinalIgnoreCase))
                return 6;
            if (text.StartsWith("6.1", StringComparison.OrdinalIgnoreCase))
                return 7;
            if (text.StartsWith("7.1", StringComparison.OrdinalIgnoreCase))
                return 8;

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    result = (result * 10) + (text[i] - '0');
                }
                else if (result > 0)
                {
                    break;
                }
            }

            return result;
        }

        #endregion
    }
}
