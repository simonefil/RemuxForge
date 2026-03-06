using System;
using System.Collections.Generic;

namespace MergeLanguageTracks
{
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
            { "german", "ger" }, { "tedesco", "ger" }, { "deutsch", "deu" },
            { "french", "fra" }, { "francese", "fra" },
            { "spanish", "spa" }, { "spagnolo", "spa" },
            { "portuguese", "por" }, { "portoghese", "por" },
            { "russian", "rus" }, { "russo", "rus" },
            { "chinese", "chi" }, { "cinese", "chi" },
            { "korean", "kor" }, { "coreano", "kor" },
            { "arabic", "ara" }, { "arabo", "ara" },
            { "dutch", "dut" }, { "olandese", "dut" },
            { "polish", "pol" }, { "polacco", "pol" },
            { "turkish", "tur" }, { "turco", "tur" },
            { "greek", "gre" }, { "greco", "gre" },
            { "hebrew", "heb" }, { "ebraico", "heb" },
            { "hindi", "hin" },
            { "thai", "tha" }, { "tailandese", "tha" },
            { "vietnamese", "vie" }, { "vietnamita", "vie" },
            { "swedish", "swe" }, { "svedese", "swe" },
            { "norwegian", "nor" }, { "norvegese", "nor" },
            { "danish", "dan" }, { "danese", "dan" },
            { "finnish", "fin" }, { "finlandese", "fin" },
            { "hungarian", "hun" }, { "ungherese", "hun" },
            { "czech", "cze" }, { "ceco", "cze" },
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
        /// Lista ordinata di tutti i codici lingua validi
        /// </summary>
        private static readonly List<string> s_sortedLanguages;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore statico.
        /// </summary>
        static LanguageValidator()
        {
            s_sortedLanguages = new List<string>(s_validLanguages);
            s_sortedLanguages.Sort(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Valida se il codice lingua dato esiste nella lista ISO 639-2.
        /// </summary>
        /// <param name="lang">Il codice lingua da validare.</param>
        /// <returns>True se valido, false altrimenti.</returns>
        public static bool IsValid(string lang)
        {
            string normalized = lang.ToLower().Trim();
            return s_validLanguages.Contains(normalized);
        }

        /// <summary>
        /// Trova codici lingua simili all'input dato per suggerimenti user-friendly.
        /// </summary>
        /// <param name="lang">Il codice lingua o nome non valido inserito dall'utente.</param>
        /// <param name="maxResults">Numero massimo di suggerimenti da restituire.</param>
        /// <returns>Una lista di codici lingua validi suggeriti.</returns>
        public static List<string> GetSimilar(string lang, int maxResults)
        {
            List<string> suggestions = new List<string>();
            string normalized = lang.ToLower().Trim();

            // Controlla prima le mappature nomi comuni
            if (s_commonNames.ContainsKey(normalized))
            {
                suggestions.Add(s_commonNames[normalized]);
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
    }
}
