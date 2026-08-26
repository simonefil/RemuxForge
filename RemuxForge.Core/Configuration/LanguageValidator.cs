using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Configuration
{
    /// <summary>
    /// Valida e suggerisce codici lingua ISO usati dalle opzioni
    /// </summary>
    public static class LanguageValidator
    {
        #region Variabili di classe

        /// <summary>
        /// Set completo dei codici lingua ISO 639-2 validi
        /// </summary>
        private static readonly HashSet<string> s_validLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // A
            "aar", "abk", "ace", "ach", "ada", "ady", "afa", "afh", "afr", "ain",
            "aka", "akk", "alb", "sqi", "ale", "alg", "alt", "amh", "ang", "anp",
            "apa", "ara", "arc", "arg", "arm", "hye", "arn", "arp", "art", "arw",
            "asm", "ast", "ath", "aus", "ava", "ave", "awa", "aym", "aze",
            // B
            "bad", "bai", "bak", "bal", "bam", "ban", "baq", "eus", "bas", "bat",
            "bej", "bel", "bem", "ben", "ber", "bho", "bih", "bik", "bin", "bis",
            "bla", "bnt", "bod", "tib", "bos", "bra", "bre", "btk", "bua", "bug",
            "bul", "bur", "mya", "byn",
            // C
            "cad", "cai", "car", "cat", "cau", "ceb", "cel", "ces", "cze", "cha",
            "chb", "che", "chg", "chi", "zho", "chk", "chm", "chn", "cho", "chp",
            "chr", "chu", "chv", "chy", "cmc", "cnr", "cop", "cor", "cos", "cpe",
            "cpf", "cpp", "cre", "crh", "crp", "csb", "cus", "cym", "wel",
            // D
            "dak", "dan", "dar", "day", "del", "den", "deu", "ger", "dgr", "din",
            "div", "doi", "dra", "dsb", "dua", "dum", "dut", "nld", "dyu", "dzo",
            // E
            "efi", "egy", "eka", "ell", "gre", "elx", "eng", "enm", "epo", "est",
            "ewe", "ewo",
            // F
            "fan", "fao", "fas", "per", "fat", "fij", "fil", "fin", "fiu", "fon",
            "fra", "fre", "frm", "fro", "frr", "frs", "fry", "ful", "fur",
            // G
            "gaa", "gay", "gba", "gem", "geo", "kat", "gez", "gil", "gla", "gle",
            "glg", "glv", "gmh", "goh", "gon", "gor", "got", "grb", "grc", "grn",
            "gsw", "guj", "gwi",
            // H
            "hai", "hat", "hau", "haw", "heb", "her", "hil", "him", "hin", "hit",
            "hmn", "hmo", "hrv", "hsb", "hun", "hup",
            // I
            "iba", "ibo", "ice", "isl", "ido", "iii", "ijo", "iku", "ile", "ilo",
            "ina", "inc", "ind", "ine", "inh", "ipk", "ira", "iro", "ita",
            // J
            "jav", "jbo", "jpn", "jpr", "jrb",
            // K
            "kaa", "kab", "kac", "kal", "kam", "kan", "kar", "kas", "kau", "kaw",
            "kaz", "kbd", "kha", "khi", "khm", "kho", "kik", "kin", "kir", "kmb",
            "kok", "kom", "kon", "kor", "kos", "kpe", "krc", "krl", "kro", "kru",
            "kua", "kum", "kur", "kut",
            // L
            "lad", "lah", "lam", "lao", "lat", "lav", "lez", "lim", "lin", "lit",
            "lol", "loz", "ltz", "lua", "lub", "lug", "lui", "lun", "luo", "lus",
            // M
            "mac", "mkd", "mad", "mag", "mah", "mai", "mak", "mal", "man", "mao",
            "mri", "map", "mar", "mas", "may", "msa", "mdf", "mdr", "men", "mga",
            "mic", "min", "mis", "mkh", "mlg", "mlt", "mnc", "mni", "mno", "moh",
            "mon", "mos", "mul", "mun", "mus", "mwl", "mwr", "myn",
            // N
            "nah", "nai", "nap", "nau", "nav", "nbl", "nde", "ndo", "nds", "nep",
            "new", "nia", "nic", "niu", "nno", "nob", "nog", "non", "nor", "nqo",
            "nso", "nub", "nwc", "nya", "nym", "nyn", "nyo", "nzi",
            // O
            "oci", "oji", "ori", "orm", "osa", "oss", "ota", "oto",
            // P
            "paa", "pag", "pal", "pam", "pan", "pap", "pau", "peo", "phi", "phn",
            "pli", "pol", "pon", "por", "pra", "pro", "pus",
            // Q
            "que",
            // R
            "raj", "rap", "rar", "roa", "roh", "rom", "ron", "rum", "run", "rup", "rus",
            // S
            "sad", "sag", "sah", "sai", "sal", "sam", "san", "sas", "sat", "scn",
            "sco", "sel", "sem", "sga", "sgn", "shn", "sid", "sin", "sio", "sit",
            "sla", "slo", "slk", "slv", "sma", "sme", "smi", "smj", "smn", "smo",
            "sms", "sna", "snd", "snk", "sog", "som", "son", "sot", "spa", "srd",
            "srn", "srp", "srr", "ssa", "ssw", "suk", "sun", "sus", "sux", "swa",
            "swe", "syc", "syr",
            // T
            "tah", "tai", "tam", "tat", "tel", "tem", "ter", "tet", "tgk", "tgl",
            "tha", "tig", "tir", "tiv", "tkl", "tlh", "tli", "tmh", "tog", "ton",
            "tpi", "tsi", "tsn", "tso", "tuk", "tum", "tup", "tur", "tut", "tvl",
            "twi", "tyv",
            // U
            "udm", "uga", "uig", "ukr", "umb", "und", "urd", "uzb",
            // V
            "vai", "ven", "vie", "vol", "vot",
            // W
            "wak", "wal", "war", "was", "wen", "wln", "wol",
            // X
            "xal", "xho",
            // Y
            "yao", "yap", "yid", "yor",
            // Z
            "zap", "zbl", "zen", "zgh", "zha", "znd", "zul", "zun", "zxx", "zza"
        };

        /// <summary>
        /// Mappa nomi comuni delle lingue ai codici ISO 639-2
        /// </summary>
        private static readonly Dictionary<string, string> s_commonNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "italian", "ita" }, { "italiano", "ita" },
            { "english", "eng" }, { "inglese", "eng" },
            { "japanese", "jpn" }, { "giapponese", "jpn" },
            { "german", "deu" }, { "tedesco", "deu" }, { "deutsch", "deu" },
            { "french", "fra" }, { "francese", "fra" },
            { "spanish", "spa" }, { "spagnolo", "spa" },
            { "portuguese", "por" }, { "portoghese", "por" },
            { "russian", "rus" }, { "russo", "rus" },
            { "chinese", "zho" }, { "cinese", "zho" },
            { "korean", "kor" }, { "coreano", "kor" },
            { "arabic", "ara" }, { "arabo", "ara" },
            { "dutch", "nld" }, { "olandese", "nld" },
            { "polish", "pol" }, { "polacco", "pol" },
            { "turkish", "tur" }, { "turco", "tur" },
            { "greek", "ell" }, { "greco", "ell" },
            { "hebrew", "heb" }, { "ebraico", "heb" },
            { "hindi", "hin" },
            { "thai", "tha" }, { "tailandese", "tha" },
            { "vietnamese", "vie" }, { "vietnamita", "vie" },
            { "swedish", "swe" }, { "svedese", "swe" },
            { "norwegian", "nor" }, { "norvegese", "nor" },
            { "danish", "dan" }, { "danese", "dan" },
            { "finnish", "fin" }, { "finlandese", "fin" },
            { "hungarian", "hun" }, { "ungherese", "hun" },
            { "czech", "ces" }, { "ceco", "ces" },
            { "romanian", "ron" }, { "rumeno", "ron" },
            { "bulgarian", "bul" }, { "bulgaro", "bul" },
            { "croatian", "hrv" }, { "croato", "hrv" },
            { "serbian", "srp" }, { "serbo", "srp" },
            { "ukrainian", "ukr" }, { "ucraino", "ukr" },
            { "indonesian", "ind" }, { "indonesiano", "ind" },
            { "malay", "may" }, { "malese", "may" },
            { "latin", "lat" }, { "latino", "lat" },
            { "undefined", "und" }, { "unknown", "und" }
        };

        /// <summary>
        /// Mappa ISO 639-1 verso ISO 639-2 indipendente dalla culture installata nel runtime
        /// </summary>
        private static readonly Dictionary<string, string> s_iso6391Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "af", "afr" }, { "ak", "aka" }, { "am", "amh" }, { "ar", "ara" },
            { "as", "asm" }, { "az", "aze" }, { "ba", "bak" }, { "be", "bel" },
            { "bg", "bul" }, { "bm", "bam" }, { "bn", "ben" }, { "bo", "bod" },
            { "br", "bre" }, { "bs", "bos" }, { "ca", "cat" }, { "ce", "che" },
            { "co", "cos" }, { "cs", "ces" }, { "cv", "chv" }, { "cy", "cym" },
            { "da", "dan" }, { "de", "deu" }, { "dv", "div" }, { "dz", "dzo" },
            { "ee", "ewe" }, { "el", "ell" }, { "en", "eng" }, { "eo", "epo" },
            { "es", "spa" }, { "et", "est" }, { "eu", "eus" }, { "fa", "fas" },
            { "ff", "ful" }, { "fi", "fin" }, { "fo", "fao" }, { "fr", "fra" },
            { "fy", "fry" }, { "ga", "gle" }, { "gd", "gla" }, { "gl", "glg" },
            { "gn", "grn" }, { "gu", "guj" }, { "gv", "glv" }, { "ha", "hau" },
            { "he", "heb" }, { "hi", "hin" }, { "hr", "hrv" }, { "ht", "hat" },
            { "hu", "hun" }, { "hy", "hye" }, { "ia", "ina" }, { "id", "ind" },
            { "ie", "ile" }, { "ig", "ibo" }, { "ii", "iii" }, { "io", "ido" },
            { "is", "isl" }, { "it", "ita" }, { "iu", "iku" }, { "ja", "jpn" },
            { "jv", "jav" }, { "ka", "kat" }, { "ki", "kik" }, { "kk", "kaz" },
            { "kl", "kal" }, { "km", "khm" }, { "kn", "kan" }, { "ko", "kor" },
            { "ks", "kas" }, { "ku", "kur" }, { "kw", "cor" }, { "ky", "kir" },
            { "lb", "ltz" }, { "lg", "lug" }, { "ln", "lin" }, { "lo", "lao" },
            { "lt", "lit" }, { "lu", "lub" }, { "lv", "lav" }, { "mg", "mlg" },
            { "mi", "mri" }, { "mk", "mkd" }, { "ml", "mal" }, { "mn", "mon" },
            { "mr", "mar" }, { "ms", "msa" }, { "mt", "mlt" }, { "my", "mya" },
            { "nb", "nob" }, { "nd", "nde" }, { "ne", "nep" }, { "nl", "nld" },
            { "nn", "nno" }, { "no", "nor" }, { "nr", "nbl" }, { "nv", "nav" },
            { "ny", "nya" }, { "oc", "oci" }, { "om", "orm" }, { "or", "ori" },
            { "os", "oss" }, { "pa", "pan" }, { "pl", "pol" }, { "ps", "pus" },
            { "pt", "por" }, { "qu", "que" }, { "rm", "roh" }, { "rn", "run" },
            { "ro", "ron" }, { "ru", "rus" }, { "rw", "kin" }, { "sa", "san" },
            { "sc", "srd" }, { "sd", "snd" }, { "se", "sme" }, { "sg", "sag" },
            { "si", "sin" }, { "sk", "slk" }, { "sl", "slv" }, { "sm", "smo" },
            { "sn", "sna" }, { "so", "som" }, { "sq", "sqi" }, { "sr", "srp" },
            { "ss", "ssw" }, { "st", "sot" }, { "su", "sun" }, { "sv", "swe" },
            { "sw", "swa" }, { "ta", "tam" }, { "te", "tel" }, { "tg", "tgk" },
            { "th", "tha" }, { "ti", "tir" }, { "tk", "tuk" }, { "tn", "tsn" },
            { "to", "ton" }, { "tr", "tur" }, { "ts", "tso" }, { "tt", "tat" },
            { "ug", "uig" }, { "uk", "ukr" }, { "ur", "urd" }, { "uz", "uzb" },
            { "ve", "ven" }, { "vi", "vie" }, { "wa", "wln" }, { "wo", "wol" },
            { "xh", "xho" }, { "yi", "yid" }, { "yo", "yor" }, { "za", "zha" },
            { "zh", "zho" }, { "zu", "zul" }
        };

        /// <summary>
        /// Alias ISO 639-2 bibliografici convertiti nel codice terminologico canonico
        /// </summary>
        private static readonly Dictionary<string, string> s_iso6392CanonicalAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "alb", "sqi" },
            { "arm", "hye" },
            { "baq", "eus" },
            { "bur", "mya" },
            { "chi", "zho" },
            { "cze", "ces" },
            { "dut", "nld" },
            { "fre", "fra" },
            { "geo", "kat" },
            { "ger", "deu" },
            { "gre", "ell" },
            { "ice", "isl" },
            { "mac", "mkd" },
            { "mao", "mri" },
            { "may", "msa" },
            { "per", "fas" },
            { "rum", "ron" },
            { "slo", "slk" },
            { "tib", "bod" },
            { "wel", "cym" }
        };

        /// <summary>
        /// Lista ordinata dei codici lingua ISO 639-2 canonici
        /// </summary>
        private static readonly List<string> s_sortedLanguages;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore statico
        /// </summary>
        static LanguageValidator()
        {
            HashSet<string> canonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string language in s_validLanguages)
            {
                canonical.Add(NormalizeIso6392Alias(language));
            }

            s_sortedLanguages = new List<string>(canonical);
            s_sortedLanguages.Sort(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Valida se il codice lingua dato esiste nella lista ISO 639-2
        /// </summary>
        /// <param name="lang">Il codice lingua da validare</param>
        /// <returns>True se valido, false altrimenti</returns>
        public static bool IsValid(string lang)
        {
            return TryNormalizeToIso6392(lang, out _);
        }

        /// <summary>
        /// Normalizza una lingua utente, ISO 639-1, ISO 639-2 o BCP 47 nel codice ISO 639-2 RemuxForge
        /// </summary>
        /// <param name="lang">Codice o nome lingua</param>
        /// <param name="normalized">Codice ISO 639-2 normalizzato</param>
        /// <returns>Vero se la lingua è riconosciuta</returns>
        public static bool TryNormalizeToIso6392(string lang, out string normalized)
        {
            string text = lang != null ? lang.Trim().ToLowerInvariant().Replace('_', '-') : "";
            string mapped;

            normalized = "";
            if (string.IsNullOrEmpty(text))
                return false;

            if (s_commonNames.TryGetValue(text, out mapped))
            {
                normalized = NormalizeIso6392Alias(mapped);
                return true;
            }

            if (text.Length == 3 && s_validLanguages.Contains(text))
            {
                normalized = NormalizeIso6392Alias(text);
                return true;
            }

            if (TryNormalizeLanguageCode(text, out normalized))
                return true;

            int separator = text.IndexOf('-', StringComparison.Ordinal);
            if (separator > 0 && TryNormalizeLanguageCode(text.Substring(0, separator), out normalized))
                return true;

            return false;
        }

        /// <summary>
        /// Normalizza una lingua nel codice ISO 639-2 RemuxForge o restituisce stringa vuota
        /// </summary>
        /// <param name="lang">Codice o nome lingua</param>
        /// <returns>Codice ISO 639-2 o stringa vuota</returns>
        public static string NormalizeToIso6392(string lang)
        {
            string normalized;
            if (TryNormalizeToIso6392(lang, out normalized))
                return normalized;

            return "";
        }

        /// <summary>
        /// Restituisce tutti i codici lingua ISO 639-2 validi
        /// </summary>
        /// <returns>Lista ordinata dei codici lingua</returns>
        public static List<string> GetAll()
        {
            return new List<string>(s_sortedLanguages);
        }

        /// <summary>
        /// Trova codici lingua simili all'input dato per suggerimenti user-friendly
        /// </summary>
        /// <param name="lang">Il codice lingua o nome non valido inserito dall'utente</param>
        /// <param name="maxResults">Numero massimo di suggerimenti da restituire</param>
        /// <returns>Una lista di codici lingua validi suggeriti</returns>
        public static List<string> GetSimilar(string lang, int maxResults)
        {
            List<string> suggestions = new List<string>();
            string normalized = lang != null ? lang.ToLowerInvariant().Trim() : "";
            string normalizedLanguage;

            if (string.IsNullOrEmpty(normalized))
                return suggestions;

            if (TryNormalizeToIso6392(normalized, out normalizedLanguage))
                suggestions.Add(normalizedLanguage);

            // Controlla prima le mappature nomi comuni
            if (s_commonNames.ContainsKey(normalized))
            {
                string mapped = NormalizeIso6392Alias(s_commonNames[normalized]);
                if (!suggestions.Contains(mapped))
                    suggestions.Add(mapped);
            }

            // Trova codici che iniziano con lo stesso prefisso
            int prefixLen = Math.Min(2, normalized.Length);
            string prefix = normalized.Substring(0, prefixLen);
            for (int i = 0; i < s_sortedLanguages.Count; i++)
            {
                if (suggestions.Count >= maxResults)
                {
                    break;
                }
                if (s_sortedLanguages[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!suggestions.Contains(s_sortedLanguages[i]))
                    {
                        suggestions.Add(s_sortedLanguages[i]);
                    }
                }
            }

            // Trova codici che contengono l'input come sottostringa
            if (suggestions.Count < maxResults)
            {
                for (int i = 0; i < s_sortedLanguages.Count; i++)
                {
                    if (suggestions.Count >= maxResults)
                    {
                        break;
                    }
                    string code = s_sortedLanguages[i];
                    bool containsInput = code.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool inputContainsCode = normalized.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (containsInput || inputContainsCode)
                    {
                        if (!suggestions.Contains(code))
                        {
                            suggestions.Add(code);
                        }
                    }
                }
            }

            // Tronca al numero massimo di risultati
            if (suggestions.Count > maxResults)
            {
                suggestions.RemoveRange(maxResults, suggestions.Count - maxResults);
            }

            return suggestions;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Normalizza alias ISO 639-2 bibliografici nel codice terminologico usato da RemuxForge
        /// </summary>
        /// <param name="language">Codice ISO 639-2</param>
        /// <returns>Codice canonico</returns>
        private static string NormalizeIso6392Alias(string language)
        {
            string mapped;
            string text = language != null ? language.Trim().ToLowerInvariant() : "";
            if (s_iso6392CanonicalAliases.TryGetValue(text, out mapped))
                return mapped;

            return text;
        }

        /// <summary>
        /// Converte codici ISO 639-1 o culture disponibili in ISO 639-2
        /// </summary>
        /// <param name="language">Codice lingua</param>
        /// <param name="normalized">Codice ISO 639-2 normalizzato</param>
        /// <returns>Vero se la conversione è riuscita</returns>
        private static bool TryNormalizeLanguageCode(string language, out string normalized)
        {
            string mapped;
            normalized = "";

            if (s_iso6391Languages.TryGetValue(language, out mapped))
            {
                normalized = NormalizeIso6392Alias(mapped);
                return true;
            }

            try
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(language);
                string candidate = NormalizeIso6392Alias(culture.ThreeLetterISOLanguageName);
                if (s_validLanguages.Contains(candidate))
                {
                    normalized = candidate;
                    return true;
                }
            }
            catch (CultureNotFoundException)
            {
            }

            return false;
        }

        #endregion
    }
}
