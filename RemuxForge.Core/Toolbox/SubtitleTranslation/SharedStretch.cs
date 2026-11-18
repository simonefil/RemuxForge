using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Tratta di battute ripetuta in più file del job, come sigle e riassunti: si traduce una volta sola nel file della prima
    /// occorrenza e la traduzione si copia identica in tutte le occorrenze. La tratta tiene la traduzione di ogni riga; i file ne
    /// tengono le copie, e ogni modifica a una copia passa alla tratta e alle altre copie
    /// </summary>
    public class SharedStretch
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SharedStretch()
        {
            this.Index = 0;
            this.Format = SubtitleTranslationFormat.Srt;
            this.Lines = new List<string>();
            this.Status = TranslationBlockStatus.Pending;
            this.CallSeqs = new List<int>();
            this.Occurrences = new List<SharedStretchOccurrence>();
            this.Translations = new List<TranslatedCueState>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Battute di un file che stanno in una tratta, con la tratta
        /// </summary>
        /// <param name="stretches">Tratte del job</param>
        /// <param name="fileId">File del job</param>
        /// <returns>Tratta per id della battuta, vuoto senza tratte nel file</returns>
        public static Dictionary<int, SharedStretch> MapFile(IEnumerable<SharedStretch> stretches, string fileId)
        {
            Dictionary<int, SharedStretch> shared = new Dictionary<int, SharedStretch>();
            foreach (SharedStretch stretch in stretches)
            {
                foreach (SharedStretchOccurrence occurrence in stretch.Occurrences)
                {
                    if (occurrence.FileId != fileId)
                        continue;
                    foreach (int id in occurrence.Ids)
                        shared[id] = stretch;
                }
            }

            return shared;
        }

        /// <summary>
        /// True se la battuta sta nella prima occorrenza, quella che si traduce e si rivede; le altre ne sono copie
        /// </summary>
        /// <param name="fileId">File della battuta</param>
        /// <param name="id">Id della battuta</param>
        /// <returns>True per la prima occorrenza</returns>
        public bool IsMaster(string fileId, int id)
        {
            return this.Occurrences[0].FileId == fileId && this.Occurrences[0].Ids.Contains(id);
        }

        /// <summary>
        /// Scrive la traduzione della tratta in tutte le occorrenze del file; da chiamare a tratta tradotta
        /// </summary>
        /// <param name="state">Stato del file</param>
        public void CopyInto(TranslationJobFileState state)
        {
            foreach (SharedStretchOccurrence occurrence in this.Occurrences)
            {
                if (occurrence.FileId != state.FileId)
                    continue;
                for (int i = 0; i < occurrence.Ids.Count; i++)
                    Put(state, this.Translations[occurrence.Offset + i].Clone(occurrence.Ids[i]));
            }
        }

        /// <summary>
        /// Porta la traduzione di una battuta della tratta, appena cambiata, nella riga della tratta e in tutte le altre occorrenze
        /// della stessa riga nei file dati
        /// </summary>
        /// <param name="source">Stato del file della battuta cambiata</param>
        /// <param name="id">Id della battuta cambiata</param>
        /// <param name="states">Stati dei file del job da allineare</param>
        /// <returns>Stati cambiati, da salvare</returns>
        public List<TranslationJobFileState> Spread(TranslationJobFileState source, int id, IEnumerable<TranslationJobFileState> states)
        {
            SharedStretchOccurrence from = this.Occurrences.Find(o => o.FileId == source.FileId && o.Ids.Contains(id));
            int line = from.Offset + from.Ids.IndexOf(id);
            TranslatedCueState translation = source.Translations.Find(t => t.Id == id);
            this.Translations[line] = translation.Clone(line);

            List<TranslationJobFileState> changed = new List<TranslationJobFileState>();
            foreach (TranslationJobFileState state in states)
            {
                foreach (SharedStretchOccurrence occurrence in this.Occurrences)
                {
                    int index = line - occurrence.Offset;
                    if (occurrence.FileId != state.FileId || index < 0 || index >= occurrence.Ids.Count || occurrence == from)
                        continue;
                    Put(state, translation.Clone(occurrence.Ids[index]));
                    if (!changed.Contains(state))
                        changed.Add(state);
                }
            }

            return changed;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Mette una traduzione nello stato del file al posto di quella con lo stesso id, in ordine di id
        /// </summary>
        /// <param name="state">Stato del file</param>
        /// <param name="translation">Traduzione con l'id della battuta del file</param>
        private static void Put(TranslationJobFileState state, TranslatedCueState translation)
        {
            int index = state.Translations.FindIndex(t => t.Id == translation.Id);
            if (index >= 0)
            {
                state.Translations[index] = translation;
                return;
            }

            state.Translations.Add(translation);
            state.Translations.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Numero della tratta nel job, da 1
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Formato dei file della tratta: si confrontano solo file dello stesso formato, che il validatore giudica uguale
        /// </summary>
        public SubtitleTranslationFormat Format { get; set; }

        /// <summary>
        /// Testo pulito di ogni riga, come nella prima occorrenza
        /// </summary>
        public List<string> Lines { get; set; }

        /// <summary>
        /// Da tradurre o tradotta; la tratta si salva solo intera
        /// </summary>
        public TranslationBlockStatus Status { get; set; }

        /// <summary>
        /// Chiamate della traduzione della tratta
        /// </summary>
        public List<int> CallSeqs { get; set; }

        /// <summary>
        /// Occorrenze nei file; la prima copre tutte le righe e dà file e contesto della chiamata
        /// </summary>
        public List<SharedStretchOccurrence> Occurrences { get; set; }

        /// <summary>
        /// Traduzione di ogni riga, con l'indice della riga come id; vuota finché la tratta non è tradotta
        /// </summary>
        public List<TranslatedCueState> Translations { get; set; }

        #endregion
    }

    /// <summary>
    /// Occorrenza di una tratta in un file: battute consecutive che coprono un tratto delle righe, la tratta intera o una sua parte
    /// come una sigla accorciata
    /// </summary>
    public class SharedStretchOccurrence
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SharedStretchOccurrence()
        {
            this.FileId = "";
            this.Offset = 0;
            this.Ids = new List<int>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File del job
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Riga della tratta della prima battuta
        /// </summary>
        public int Offset { get; set; }

        /// <summary>
        /// Id delle battute del file, una per riga a partire da Offset
        /// </summary>
        public List<int> Ids { get; set; }

        #endregion
    }
}
