using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Candidati ricorrenti per l'analisi: nomi e termini che il codice trova nel testo pulito di tutti i file del progetto, con il
    /// conteggio per file. Il modello li filtra: il rumore residuo costa solo token
    /// </summary>
    public class RecurringTermExtractor
    {
        #region Costanti

        /// <summary>
        /// Occorrenze minime nel progetto di un candidato presente in un solo file
        /// </summary>
        public const int MIN_COUNT = 2;

        /// <summary>
        /// Candidati massimi, per conteggio decrescente
        /// </summary>
        public const int MAX_CANDIDATES = 80;

        /// <summary>
        /// Vocativi minimi di una parola che compare solo a inizio frase
        /// </summary>
        private const int MIN_VOCATIVES = 2;

        /// <summary>
        /// Lingua d'origine con l'elenco delle parole comuni e degli appellativi
        /// </summary>
        private const string ENGLISH = "eng";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Parole comuni inglesi che a inizio parola maiuscola non sono nomi
        /// </summary>
        private static readonly HashSet<string> s_englishCommonWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "I", "I'm", "I'll", "I've", "I'd", "It", "It's", "The", "A", "An", "And", "But", "Or", "So", "Oh", "Hey", "Yes", "No", "Okay",
            "OK", "What", "Why", "How", "Who", "Where", "When", "This", "That", "There", "Then", "Well", "You", "You're", "We", "We're",
            "He", "She", "They", "Look", "Come", "Let", "Just", "Mr", "Mrs", "Ms", "Dr", "Don't", "Is", "Are", "Do", "Did", "Can", "If",
            "In", "On", "At", "For", "Of", "To", "My", "Your", "Our", "All", "Not", "Now", "Go", "Get", "Wait", "Please", "Thank",
            "Thanks", "Sorry", "Right", "Good", "Uh", "Um", "Huh", "Ah", "Hmm", "Yeah", "God", "Man", "One", "Two", "Here", "Me", "Him",
            "Her", "Us", "Them", "Was", "Were", "Have", "Has", "Had", "Be", "Been", "Will", "Would", "Could", "Should", "Maybe", "Really",
            "Because", "With", "From", "As", "Like", "Some", "Any", "Every", "Never", "Always", "Still", "Even", "Only", "Also", "Too",
            "Very", "Gonna", "Wanna", "Gotta", "Let's"
        };

        /// <summary>
        /// Appellativi abbreviati inglesi davanti a un nome (Mr. Adler); quelli per esteso formano già una sequenza (Agent Burke)
        /// </summary>
        private static readonly Regex s_englishTitle = new Regex(@"(?<![\p{L}])(Mr|Mrs|Ms|Dr)\.? (\p{Lu}[\p{L}\p{M}\-]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Etichetta di chi parla all'inizio della riga (HELEN:)
        /// </summary>
        private static readonly Regex s_speakerLabel = new Regex(@"^(\p{Lu}[\p{Lu}0-9 .'\-]*\p{Lu}):\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Parola: lettera iniziale, poi lettere, segni, apostrofi e trattini
        /// </summary>
        private static readonly Regex s_word = new Regex(@"\p{L}[\p{L}\p{M}'\-]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Sintassi dei segni dei tratti formattati
        /// </summary>
        private static readonly Regex s_markSyntax = new Regex("⟦[0-9]+ |⟧", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Estrae i candidati ricorrenti da tutti i file del progetto
        /// </summary>
        /// <param name="files">Battute di ogni file, per identificativo del file nell'ordine del progetto</param>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <returns>Candidati per conteggio decrescente, al massimo MAX_CANDIDATES</returns>
        public List<RecurringCandidate> Extract(IReadOnlyList<KeyValuePair<string, IReadOnlyList<SubtitleCue>>> files, string sourceLanguage)
        {
            bool english = LanguageValidator.NormalizeToIso6392(sourceLanguage) == ENGLISH;
            Dictionary<string, TermStats> stats = new Dictionary<string, TermStats>(StringComparer.Ordinal);
            List<RecurringCandidate> candidates = new List<RecurringCandidate>();

            foreach (KeyValuePair<string, IReadOnlyList<SubtitleCue>> file in files)
            {
                foreach (SubtitleCue cue in file.Value)
                {
                    if (!cue.Id.HasValue)
                        continue;

                    string[] lines = s_markSyntax.Replace(cue.CleanText, "").Replace('’', '\'').Split('\n');

                    // Una riga di continuazione apre una frase se la riga prima ne chiude una
                    for (int i = 0; i < lines.Length; i++)
                        this.ScanLine(lines[i], i == 0 || this.FollowsSentenceEnd(lines[i - 1], lines[i - 1].Length), english, file.Key, stats);
                }
            }

            // Una parola vale se compare anche a metà frase o come vocativo ripetuto; sequenze ed etichette valgono sempre
            foreach (KeyValuePair<string, TermStats> entry in stats)
            {
                TermStats term = entry.Value;
                bool counted = term.IsSequence || term.Total(term.Middle) > 0 || term.Total(term.Vocatives) >= MIN_VOCATIVES;
                if (!counted)
                    continue;

                RecurringCandidate candidate = new RecurringCandidate(entry.Key);
                foreach (KeyValuePair<string, int> count in term.Middle)
                    candidate.Add(count.Key, count.Value);
                foreach (KeyValuePair<string, int> count in term.Start)
                    candidate.Add(count.Key, count.Value);
                candidates.Add(candidate);
            }

            // Una parola che compare solo dentro una sequenza, con lo stesso conteggio, è già nella sequenza (New e York in New York)
            List<RecurringCandidate> sequences = candidates.FindAll(c => c.Term.IndexOf(' ') >= 0);
            candidates.RemoveAll(c => c.Term.IndexOf(' ') < 0 && sequences.Exists(s => s.Total == c.Total && Array.IndexOf(s.Term.Replace(". ", " ").Split(' '), c.Term) >= 0));

            candidates.RemoveAll(c => c.Total < MIN_COUNT && c.Counts.Count < 2);
            candidates.Sort((a, b) => a.Total != b.Total ? b.Total.CompareTo(a.Total) : string.CompareOrdinal(a.Term, b.Term));
            if (candidates.Count > MAX_CANDIDATES)
                candidates.RemoveRange(MAX_CANDIDATES, candidates.Count - MAX_CANDIDATES);

            return candidates;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Conta parole maiuscole, sequenze, appellativi ed etichette di una riga di battuta
        /// </summary>
        /// <param name="line">Riga del testo pulito, senza segni</param>
        /// <param name="firstLine">True per la prima riga della battuta, che apre una frase</param>
        /// <param name="english">True con l'inglese come lingua d'origine</param>
        /// <param name="fileId">File della battuta</param>
        /// <param name="stats">Conteggi in costruzione</param>
        private void ScanLine(string line, bool firstLine, bool english, string fileId, Dictionary<string, TermStats> stats)
        {
            string text = line;
            bool sentenceStart = firstLine;
            List<string> sequence = new List<string>();
            int sequenceEnd = -1;

            // Etichetta di chi parla: normalizzata (HELEN → Helen, DR. SMITH → Dr. Smith) e contata con il nome
            Match label = s_speakerLabel.Match(text);
            if (label.Success)
            {
                char[] name = label.Groups[1].Value.ToLowerInvariant().ToCharArray();
                for (int i = 0; i < name.Length; i++)
                {
                    if (i == 0 || !char.IsLetter(name[i - 1]))
                        name[i] = char.ToUpperInvariant(name[i]);
                }
                GetStats(stats, new string(name), false).Add(fileId, true, false);
                text = text.Substring(label.Length);
                sentenceStart = true;
            }

            // Un trattino di dialogo apre una frase, anche su una riga di continuazione
            string trimmed = text.TrimStart();
            if (!string.IsNullOrEmpty(trimmed) && (trimmed[0] == '-' || trimmed[0] == '–' || trimmed[0] == '—'))
                sentenceStart = true;

            if (english)
            {
                foreach (Match title in s_englishTitle.Matches(text))
                    GetStats(stats, title.Groups[1].Value + ". " + title.Groups[2].Value, true).Add(fileId, true, false);
            }

            foreach (Match match in s_word.Matches(text))
            {
                string word = match.Value.TrimEnd('-');
                bool start = sentenceStart || this.FollowsSentenceEnd(text, match.Index);
                sentenceStart = false;

                // Possessivo tolto; le altre contrazioni non sono nomi
                if (word.EndsWith("'s", StringComparison.Ordinal))
                    word = word.Substring(0, word.Length - 2);
                bool candidateWord = !string.IsNullOrEmpty(word) && word.IndexOf('\'') < 0 && char.IsUpper(word[0]) && !(english && s_englishCommonWords.Contains(word));

                // La sequenza continua solo fra parole maiuscole separate da uno spazio, senza punteggiatura in mezzo
                bool joins = sequence.Count > 0 && match.Index == sequenceEnd + 1 && text[sequenceEnd] == ' ';
                if (!joins || string.IsNullOrEmpty(word) || !char.IsUpper(word[0]))
                {
                    this.CloseSequence(sequence, english, fileId, stats);
                    sequence.Clear();
                }
                if (!string.IsNullOrEmpty(word) && char.IsUpper(word[0]) && word.IndexOf('\'') < 0)
                {
                    sequence.Add(word);
                    sequenceEnd = match.Index + match.Length;
                }

                if (!candidateWord)
                    continue;

                int after = match.Index + match.Length;
                bool vocative = start && (after >= text.Length || ",!?.".IndexOf(text[after]) >= 0);
                GetStats(stats, word, false).Add(fileId, !start, vocative);
            }

            this.CloseSequence(sequence, english, fileId, stats);
        }

        /// <summary>
        /// Conta una sequenza di parole maiuscole chiusa, senza le parole comuni ai bordi
        /// </summary>
        /// <param name="sequence">Parole della sequenza</param>
        /// <param name="english">True con l'inglese come lingua d'origine</param>
        /// <param name="fileId">File della battuta</param>
        /// <param name="stats">Conteggi in costruzione</param>
        private void CloseSequence(List<string> sequence, bool english, string fileId, Dictionary<string, TermStats> stats)
        {
            int first = 0;
            int last = sequence.Count - 1;
            while (english && first <= last && s_englishCommonWords.Contains(sequence[first]))
                first++;
            while (english && last >= first && s_englishCommonWords.Contains(sequence[last]))
                last--;

            if (last - first >= 1)
                GetStats(stats, string.Join(" ", sequence.GetRange(first, last - first + 1)), true).Add(fileId, true, false);
        }

        /// <summary>
        /// Indica se una posizione segue la fine di una frase: punto, esclamativo, interrogativo o puntini, ma non l'abbreviazione di
        /// un appellativo (Mr. Adler); oppure un trattino di dialogo a metà riga (- Ciao. - Shoot!)
        /// </summary>
        /// <param name="text">Riga</param>
        /// <param name="index">Inizio della parola, o lunghezza della riga per sapere se la riga chiude una frase</param>
        /// <returns>True se la posizione apre una frase</returns>
        private bool FollowsSentenceEnd(string text, int index)
        {
            int position = index - 1;
            while (position >= 0 && (text[position] == ' ' || text[position] == '"' || text[position] == '“'))
                position--;
            if (position >= 0 && position < index - 1 && "-–—".IndexOf(text[position]) >= 0 && (position == 0 || text[position - 1] == ' '))
                return true;
            if (position < 0 || ".!?…".IndexOf(text[position]) < 0)
                return false;

            if (text[position] == '.')
            {
                int wordStart = position;
                while (wordStart > 0 && char.IsLetter(text[wordStart - 1]))
                    wordStart--;
                string previous = text.Substring(wordStart, position - wordStart);
                if (previous == "Mr" || previous == "Mrs" || previous == "Ms" || previous == "Dr")
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Restituisce i conteggi di un termine, creandoli al primo uso
        /// </summary>
        /// <param name="stats">Conteggi in costruzione</param>
        /// <param name="term">Termine</param>
        /// <param name="sequence">True per sequenze e appellativi, che valgono in ogni posizione</param>
        /// <returns>Conteggi del termine</returns>
        private static TermStats GetStats(Dictionary<string, TermStats> stats, string term, bool sequence)
        {
            TermStats result;
            if (!stats.TryGetValue(term, out result))
            {
                result = new TermStats(sequence);
                stats[term] = result;
            }

            return result;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Conteggi di un termine per file: a metà frase, a inizio frase e come vocativo a inizio frase
        /// </summary>
        private sealed class TermStats
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="isSequence">True per sequenze e appellativi</param>
            public TermStats(bool isSequence)
            {
                this.IsSequence = isSequence;
                this.Middle = new Dictionary<string, int>(StringComparer.Ordinal);
                this.Start = new Dictionary<string, int>(StringComparer.Ordinal);
                this.Vocatives = new Dictionary<string, int>(StringComparer.Ordinal);
            }

            /// <summary>
            /// Conta un'occorrenza
            /// </summary>
            /// <param name="fileId">File</param>
            /// <param name="middle">True a metà frase</param>
            /// <param name="vocative">True per un vocativo a inizio frase</param>
            public void Add(string fileId, bool middle, bool vocative)
            {
                Dictionary<string, int> target = middle ? this.Middle : this.Start;
                int count;
                target[fileId] = (target.TryGetValue(fileId, out count) ? count : 0) + 1;
                if (vocative)
                    this.Vocatives[fileId] = (this.Vocatives.TryGetValue(fileId, out count) ? count : 0) + 1;
            }

            /// <summary>
            /// Somma i conteggi di tutti i file
            /// </summary>
            /// <param name="counts">Conteggi per file</param>
            /// <returns>Totale</returns>
            public int Total(Dictionary<string, int> counts)
            {
                int total = 0;
                foreach (int count in counts.Values)
                    total += count;
                return total;
            }

            /// <summary>
            /// True per sequenze e appellativi
            /// </summary>
            public bool IsSequence { get; }

            /// <summary>
            /// Occorrenze a metà frase per file
            /// </summary>
            public Dictionary<string, int> Middle { get; }

            /// <summary>
            /// Occorrenze a inizio frase per file
            /// </summary>
            public Dictionary<string, int> Start { get; }

            /// <summary>
            /// Vocativi a inizio frase per file
            /// </summary>
            public Dictionary<string, int> Vocatives { get; }
        }

        #endregion
    }

    /// <summary>
    /// Candidato ricorrente con il conteggio per file
    /// </summary>
    public class RecurringCandidate
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="term">Termine</param>
        public RecurringCandidate(string term)
        {
            this.Term = term;
            this.Counts = new Dictionary<string, int>(StringComparer.Ordinal);
            this.Total = 0;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Somma occorrenze in un file
        /// </summary>
        /// <param name="fileId">File</param>
        /// <param name="count">Occorrenze</param>
        public void Add(string fileId, int count)
        {
            int current;
            this.Counts[fileId] = (this.Counts.TryGetValue(fileId, out current) ? current : 0) + count;
            this.Total += count;
        }

        /// <summary>
        /// Occorrenze in un file
        /// </summary>
        /// <param name="fileId">File</param>
        /// <returns>Occorrenze, zero se il termine non c'è</returns>
        public int CountIn(string fileId)
        {
            int count;
            return this.Counts.TryGetValue(fileId, out count) ? count : 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Termine
        /// </summary>
        public string Term { get; }

        /// <summary>
        /// Occorrenze per file
        /// </summary>
        public Dictionary<string, int> Counts { get; }

        /// <summary>
        /// Occorrenze nel progetto
        /// </summary>
        public int Total { get; private set; }

        #endregion
    }
}
