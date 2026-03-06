using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MergeLanguageTracks
{
    public static class CodecMapping
    {
        #region Variabili di classe

        /// <summary>
        /// Mappa nomi codec utente a stringhe codec esatte mkvmerge
        /// </summary>
        private static readonly Dictionary<string, string[]> s_codecMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Dolby
            { "AC3",       new[] { "AC-3" } },
            { "AC-3",      new[] { "AC-3" } },
            { "DD",        new[] { "AC-3" } },
            { "EAC3",      new[] { "E-AC-3" } },
            { "E-AC-3",    new[] { "E-AC-3" } },
            { "DD+",       new[] { "E-AC-3" } },
            { "DDP",       new[] { "E-AC-3" } },
            { "TRUEHD",    new[] { "TrueHD" } },
            { "ATMOS",     new[] { "TrueHD", "E-AC-3" } },
            { "MLP",       new[] { "MLP" } },

            // DTS - matching esatto per distinguere DTS core da DTS-HD
            { "DTS",       new[] { "DTS" } },
            { "DTS-HD",    new[] { "DTS-HD Master Audio", "DTS-HD High Resolution" } },
            { "DTS-HD MA", new[] { "DTS-HD Master Audio" } },
            { "DTS-HDMA",  new[] { "DTS-HD Master Audio" } },
            { "DTS-HD HR", new[] { "DTS-HD High Resolution" } },
            { "DTS-HDHR",  new[] { "DTS-HD High Resolution" } },
            { "DTS-ES",    new[] { "DTS-ES" } },
            { "DTS:X",     new[] { "DTS:X" } },
            { "DTSX",      new[] { "DTS:X" } },

            // Lossless
            { "FLAC",      new[] { "FLAC" } },
            { "PCM",       new[] { "PCM" } },
            { "LPCM",      new[] { "PCM" } },
            { "WAV",       new[] { "PCM" } },
            { "ALAC",      new[] { "ALAC" } },

            // Lossy
            { "AAC",       new[] { "AAC" } },
            { "HE-AAC",    new[] { "AAC" } },
            { "MP3",       new[] { "MPEG Audio", "MP3" } },
            { "MP2",       new[] { "MP2", "MPEG Audio Layer 2" } },
            { "OPUS",      new[] { "Opus" } },
            { "VORBIS",    new[] { "Vorbis" } }
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce i pattern codec esatti mkvmerge per una stringa codec fornita dall'utente.
        /// </summary>
        /// <param name="userCodec">La stringa codec fornita dall'utente.</param>
        /// <returns>Un array di pattern codec esatti, o null se non riconosciuto.</returns>
        public static string[] GetCodecPatterns(string userCodec)
        {
            string[] result = null;
            string normalized = userCodec.Trim().ToUpper();

            // Lookup diretto
            if (s_codecMap.ContainsKey(normalized))
            {
                result = s_codecMap[normalized];
            }
            else
            {
                // Fallback: rimuovi trattini, spazi, due punti per match fuzzy
                string strippedInput = Regex.Replace(normalized, @"[\s\-:]", "");

                foreach (KeyValuePair<string, string[]> entry in s_codecMap)
                {
                    string strippedKey = Regex.Replace(entry.Key.ToUpper(), @"[\s\-:]", "");
                    if (strippedKey == strippedInput)
                    {
                        result = entry.Value;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Restituisce una stringa separata da virgole di tutte le chiavi alias codec riconosciute.
        /// </summary>
        /// <returns>Una stringa che elenca tutti gli alias codec.</returns>
        public static string GetAllCodecNames()
        {
            return string.Join(", ", s_codecMap.Keys);
        }

        /// <summary>
        /// Verifica se un codec traccia corrisponde a uno dei pattern specificati.
        /// </summary>
        /// <param name="trackCodec">La stringa codec dalla traccia MKV.</param>
        /// <param name="patterns">L'array di pattern codec esatti con cui confrontare.</param>
        /// <returns>True se il codec traccia corrisponde a qualche pattern, false altrimenti.</returns>
        public static bool MatchesCodec(string trackCodec, string[] patterns)
        {
            bool matched = false;

            for (int i = 0; i < patterns.Length; i++)
            {
                if (string.Equals(trackCodec, patterns[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            return matched;
        }

        #endregion
    }
}
