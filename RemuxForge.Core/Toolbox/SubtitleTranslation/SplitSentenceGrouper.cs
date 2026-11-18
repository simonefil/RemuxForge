using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Riconosce le frasi spezzate: battute consecutive in ordine di tempo che continuano la stessa frase viaggiano insieme nello
    /// stesso blocco e il modello le traduce come una frase sola, ridistribuita sulle stesse battute. Si uniscono solo battute dello
    /// stesso stile e dello stesso tipo, e mai i cartelli; nelle scritture senza maiuscole l'iniziale minuscola non conta
    /// </summary>
    public class SplitSentenceGrouper
    {
        #region Costanti

        /// <summary>
        /// Pausa massima fra due battute della stessa frase
        /// </summary>
        public const long MAX_GROUP_PAUSE_MS = 1500;

        /// <summary>
        /// Sovrapposizione fra due battute consecutive che conta come pausa nulla
        /// </summary>
        public const long MAX_GROUP_OVERLAP_MS = 100;

        /// <summary>
        /// Battute massime per gruppo
        /// </summary>
        public const int MAX_GROUP_CUES = 4;

        /// <summary>
        /// Lingua d'origine in cui i nomi comuni hanno l'iniziale maiuscola
        /// </summary>
        private const string GERMAN = "deu";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Punteggiatura che chiude una frase in fondo al testo
        /// </summary>
        private static readonly Regex s_terminalEnd = new Regex(@"([.!?:…\])。！？」』؟]|[.!?][""'”’)\]]|--|—|♪)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Puntini di continuazione in fondo al testo
        /// </summary>
        private static readonly Regex s_ellipsisEnd = new Regex(@"(\.\.\.|…)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Puntini di continuazione all'inizio del testo
        /// </summary>
        private static readonly Regex s_ellipsisStart = new Regex(@"^(\.\.\.|…)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Trattino iniziale di una battuta di dialogo
        /// </summary>
        private static readonly Regex s_dashStart = new Regex(@"^[-‐–—]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Etichetta di chi parla all'inizio del testo (ALL:, LORRAINE:)
        /// </summary>
        private static readonly Regex s_speakerLabel = new Regex(@"^[A-Z][A-Z0-9 .'-]+:\s", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Trattino dopo una punteggiatura finale: un altro interlocutore nella stessa battuta (neck. - Caffrey.)
        /// </summary>
        private static readonly Regex s_innerSpeakerChange = new Regex(@"[.!?:…\])][""'”’)\]]*\s+[-‐–—]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Sintassi dei segni dei tratti formattati
        /// </summary>
        private static readonly Regex s_markSyntax = new Regex("⟦[0-9]+ |⟧", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Spazi ripetuti
        /// </summary>
        private static readonly Regex s_spaces = new Regex(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Assegna il gruppo alle battute traducibili che formano una frase spezzata, numerando i gruppi da 1 in ordine di tempo
        /// </summary>
        /// <param name="cues">Battute del file; si scorrono le sole traducibili</param>
        /// <param name="sourceLanguage">Lingua d'origine</param>
        /// <returns>Numero di gruppi</returns>
        public int Group(IReadOnlyList<SubtitleCue> cues, string sourceLanguage)
        {
            bool germanSource = LanguageValidator.NormalizeToIso6392(sourceLanguage) == GERMAN;
            List<SubtitleCue> translatable = new List<SubtitleCue>();
            foreach (SubtitleCue cue in cues)
            {
                if (cue.Id.HasValue)
                {
                    cue.Group = null;
                    translatable.Add(cue);
                }
            }
            translatable.Sort(SubtitleCue.CompareByTime);

            // Testo su cui si giudica: senza la sintassi dei segni, a capo come spazi, spazi ripetuti ridotti; righe per il cambio
            // d'interlocutore
            string[] plain = new string[translatable.Count];
            bool[] speakerChange = new bool[translatable.Count];
            for (int i = 0; i < translatable.Count; i++)
            {
                string text = s_markSyntax.Replace(translatable[i].CleanText, "");
                plain[i] = s_spaces.Replace(text, " ").Trim();
                string[] lines = text.Split('\n');
                for (int l = 1; l < lines.Length; l++)
                    speakerChange[i] |= s_dashStart.IsMatch(lines[l].Trim());
                speakerChange[i] |= s_innerSpeakerChange.IsMatch(plain[i]);
            }

            // Coppie consecutive che continuano la stessa frase
            bool[] continues = new bool[Math.Max(0, translatable.Count - 1)];
            for (int i = 0; i < continues.Length; i++)
            {
                SubtitleCue a = translatable[i];
                SubtitleCue b = translatable[i + 1];
                long pause = b.StartMs - a.EndMs;
                string next = plain[i + 1];
                bool lowerStart = !string.IsNullOrEmpty(next) && char.IsLower(next, 0);

                // Le scritture senza maiuscole (giapponese, cinese, arabo, ebraico) non hanno iniziali minuscole e si uniscono solo
                // coi puntini; coi nomi comuni maiuscoli (tedesco) l'iniziale non decide
                continues[i] = pause <= MAX_GROUP_PAUSE_MS && pause >= -MAX_GROUP_OVERLAP_MS
                    && string.Equals(a.Style, b.Style, StringComparison.Ordinal)
                    && a.Kind == b.Kind && a.Kind != SubtitleCueKind.Sign
                    && !string.IsNullOrEmpty(plain[i]) && !string.IsNullOrEmpty(next)
                    && !s_dashStart.IsMatch(next)
                    && !s_speakerLabel.IsMatch(next)
                    && !speakerChange[i] && !speakerChange[i + 1]
                    && (lowerStart || s_ellipsisStart.IsMatch(next) || germanSource)
                    && (s_ellipsisEnd.IsMatch(plain[i]) || !s_terminalEnd.IsMatch(plain[i]));
            }

            // Catene fino a MAX_GROUP_CUES battute; la successiva apre un gruppo nuovo
            int groups = 0;
            int start = 0;
            while (start < translatable.Count - 1)
            {
                int end = start;
                while (end < translatable.Count - 1 && end - start + 1 < MAX_GROUP_CUES && continues[end])
                    end++;
                if (end > start)
                {
                    groups++;
                    for (int k = start; k <= end; k++)
                        translatable[k].Group = groups;
                }
                start = end + 1;
            }

            return groups;
        }

        #endregion
    }
}
