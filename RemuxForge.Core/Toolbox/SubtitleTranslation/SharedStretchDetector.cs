using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Trova le tratte ripetute fra i file del job: almeno tre battute consecutive in ordine di tempo col testo pulito identico, a
    /// meno degli spazi, in almeno due file dello stesso formato; i tempi non contano. Poi, in ogni file, la tratta dei cartelli in
    /// movimento: un cartello ridisegnato a ogni fotogramma si traduce una volta e si copia. Ogni battuta sta al più in una tratta
    /// e una tratta non taglia mai una frase spezzata
    /// </summary>
    public static class SharedStretchDetector
    {
        #region Costanti

        /// <summary>
        /// Battute consecutive minime di un'occorrenza
        /// </summary>
        public const int MIN_CUES = 3;

        /// <summary>
        /// Durata massima di un fotogramma di un cartello in movimento, anche disegnato ogni due fotogrammi
        /// </summary>
        public const long MAX_SIGN_FRAME_MS = 100;

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Spazi e a capo ripetuti
        /// </summary>
        private static readonly Regex s_spaces = new Regex(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Trova le tratte dei file: scorrendo i file in ordine, la prima battuta libera che continua per almeno tre battute in un
        /// altro file apre una tratta lunga quanto la corsa comune più lunga; poi ogni corsa libera di almeno tre battute uguali a
        /// righe consecutive della tratta ne diventa un'occorrenza, anche se ne copre solo una parte. Alla fine ogni file aggiunge
        /// la tratta dei suoi cartelli in movimento
        /// </summary>
        /// <param name="files">Identificativo e documento preparato di ogni file, nell'ordine del progetto</param>
        /// <returns>Tratte numerate da 1, con la prima occorrenza intera</returns>
        public static List<SharedStretch> Detect(IReadOnlyList<KeyValuePair<string, SubtitleTranslationDocument>> files)
        {
            List<FileKeys> keyed = new List<FileKeys>();
            foreach (KeyValuePair<string, SubtitleTranslationDocument> file in files)
                keyed.Add(new FileKeys(file.Key, file.Value));
            Dictionary<string, List<KeyValuePair<FileKeys, int>>> index = CreateIndex(keyed);
            List<SharedStretch> stretches = new List<SharedStretch>();

            foreach (FileKeys file in keyed)
            {
                for (int p = 0; p + MIN_CUES <= file.Keys.Length; p++)
                {
                    List<KeyValuePair<FileKeys, int>> hits;
                    if (file.Assigned[p] || !index.TryGetValue(Gram(file.Keys, p), out hits))
                        continue;

                    // Lunghezza della tratta: la corsa comune più lunga con un altro file, sulle battute libere
                    int free = p;
                    while (free < file.Keys.Length && !file.Assigned[free])
                        free++;
                    int length = 0;
                    foreach (KeyValuePair<FileKeys, int> hit in hits)
                    {
                        if (hit.Key != file)
                            length = Math.Max(length, Common(file.Keys, p, free, hit.Key, hit.Value));
                    }

                    int first = p;
                    int last = p + length;
                    Trim(file, ref first, ref last);
                    if (last - first < MIN_CUES)
                        continue;

                    SharedStretch stretch = new SharedStretch();
                    stretch.Index = stretches.Count + 1;
                    stretch.Format = file.Format;
                    string[] lines = new string[last - first];
                    for (int i = first; i < last; i++)
                    {
                        stretch.Lines.Add(file.Cues[i].CleanText);
                        lines[i - first] = file.Keys[i];
                    }
                    Claim(stretch, file, first, last, 0);
                    AddOccurrences(stretch, lines, index);

                    // Le occorrenze di un solo file, dopo i tagli sulle frasi spezzate, non fanno una tratta
                    if (stretch.Occurrences.TrueForAll(o => o.FileId == file.FileId))
                    {
                        foreach (SharedStretchOccurrence occurrence in stretch.Occurrences)
                        {
                            int start = file.Cues.FindIndex(c => c.Id == occurrence.Ids[0]);
                            for (int i = 0; i < occurrence.Ids.Count; i++)
                                file.Assigned[start + i] = false;
                        }
                        continue;
                    }

                    stretches.Add(stretch);
                    p = last - 1;
                }
            }

            foreach (FileKeys file in keyed)
                AddMovingSigns(stretches, file);
            return stretches;
        }

        /// <summary>
        /// Aggiunge alle tratte fra i file le occorrenze di un file ripreparato, che non ne ha, e la tratta dei suoi cartelli in
        /// movimento; le tratte dei cartelli in movimento degli altri file restano fuori, perché le loro righe non sono consecutive
        /// </summary>
        /// <param name="stretches">Tratte del job</param>
        /// <param name="fileId">File del job</param>
        /// <param name="document">Documento preparato del file</param>
        public static void Attach(List<SharedStretch> stretches, string fileId, SubtitleTranslationDocument document)
        {
            FileKeys file = new FileKeys(fileId, document);
            Dictionary<string, List<KeyValuePair<FileKeys, int>>> index = CreateIndex(new List<FileKeys> { file });
            foreach (SharedStretch stretch in stretches)
            {
                if (stretch.MovingSigns)
                    continue;
                string[] lines = new string[stretch.Lines.Count];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = Key(stretch.Format, stretch.Lines[i]);
                AddOccurrences(stretch, lines, index);
            }
            AddMovingSigns(stretches, file);
        }

        /// <summary>
        /// Toglie dalle tratte le occorrenze di un file escluso o ricominciato. Senza la prima occorrenza la tratta prende come
        /// prima la più lunga rimasta e si restringe alle sue righe; le battute delle altre occorrenze che restano fuori diventano
        /// tratte di un solo file perché nessun blocco del loro file le copre: da tradurre, o tradotte con le righe della tratta,
        /// che arrivano come copie anche al file che era bloccato quando la tratta si è salvata
        /// </summary>
        /// <param name="stretches">Tratte del job</param>
        /// <param name="fileId">File del job</param>
        public static void Detach(List<SharedStretch> stretches, string fileId)
        {
            int next = 0;
            foreach (SharedStretch stretch in stretches)
                next = Math.Max(next, stretch.Index);

            foreach (SharedStretch stretch in new List<SharedStretch>(stretches))
            {
                bool master = stretch.Occurrences[0].FileId == fileId;
                stretch.Occurrences.RemoveAll(o => o.FileId == fileId);
                if (stretch.Occurrences.Count == 0)
                {
                    stretches.Remove(stretch);
                    continue;
                }
                if (!master)
                    continue;

                SharedStretchOccurrence longest = stretch.Occurrences[0];
                foreach (SharedStretchOccurrence occurrence in stretch.Occurrences)
                {
                    if (occurrence.Ids.Count > longest.Ids.Count)
                        longest = occurrence;
                }
                stretch.Occurrences.Remove(longest);
                stretch.Occurrences.Insert(0, longest);
                int start = longest.Offset;
                int end = start + longest.Ids.Count;

                foreach (SharedStretchOccurrence occurrence in stretch.Occurrences)
                {
                    // Battute prima di start e dopo end: le prime head e quelle da tail in poi
                    int head = Math.Min(Math.Max(0, start - occurrence.Offset), occurrence.Ids.Count);
                    int tail = Math.Max(head, Math.Min(occurrence.Ids.Count, end - occurrence.Offset));
                    foreach (KeyValuePair<int, int> part in new KeyValuePair<int, int>[] { new KeyValuePair<int, int>(0, head), new KeyValuePair<int, int>(tail, occurrence.Ids.Count - tail) })
                    {
                        if (part.Value == 0)
                            continue;
                        SharedStretch solo = new SharedStretch();
                        solo.Index = ++next;
                        solo.Format = stretch.Format;
                        solo.Status = stretch.Status;
                        solo.CallSeqs.AddRange(stretch.CallSeqs);
                        solo.Lines = stretch.Lines.GetRange(occurrence.Offset + part.Key, part.Value);
                        for (int i = 0; i < part.Value && stretch.Translations.Count > 0; i++)
                            solo.Translations.Add(stretch.Translations[occurrence.Offset + part.Key + i].Clone(i));
                        solo.Occurrences.Add(new SharedStretchOccurrence { FileId = occurrence.FileId, Offset = 0, Ids = occurrence.Ids.GetRange(part.Key, part.Value) });
                        stretches.Add(solo);
                    }

                    occurrence.Ids = occurrence.Ids.GetRange(head, tail - head);
                    occurrence.Offset = occurrence.Offset + head - start;
                }

                stretch.Occurrences.RemoveAll(o => o.Ids.Count == 0);
                stretch.Lines = stretch.Lines.GetRange(start, end - start);
                if (stretch.Translations.Count > 0)
                {
                    stretch.Translations = stretch.Translations.GetRange(start, end - start);
                    for (int i = 0; i < stretch.Translations.Count; i++)
                        stretch.Translations[i].Id = i;
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Chiave di confronto di una battuta: formato e testo pulito con gli spazi ridotti
        /// </summary>
        /// <param name="format">Formato del file</param>
        /// <param name="text">Testo pulito</param>
        /// <returns>Chiave</returns>
        private static string Key(SubtitleTranslationFormat format, string text)
        {
            return ((int)format).ToString(CultureInfo.InvariantCulture) + " " + s_spaces.Replace(text, " ").Trim();
        }

        /// <summary>
        /// Chiave di tre battute consecutive
        /// </summary>
        /// <param name="keys">Chiavi delle battute</param>
        /// <param name="start">Prima battuta</param>
        /// <returns>Chiave delle tre battute</returns>
        private static string Gram(string[] keys, int start)
        {
            return keys[start] + "\n" + keys[start + 1] + "\n" + keys[start + 2];
        }

        /// <summary>
        /// Posizioni di ogni terna di battute consecutive dei file
        /// </summary>
        /// <param name="files">File con le chiavi</param>
        /// <returns>File e posizione per chiave della terna</returns>
        private static Dictionary<string, List<KeyValuePair<FileKeys, int>>> CreateIndex(List<FileKeys> files)
        {
            Dictionary<string, List<KeyValuePair<FileKeys, int>>> index = new Dictionary<string, List<KeyValuePair<FileKeys, int>>>(StringComparer.Ordinal);
            foreach (FileKeys file in files)
            {
                for (int p = 0; p + MIN_CUES <= file.Keys.Length; p++)
                {
                    string gram = Gram(file.Keys, p);
                    List<KeyValuePair<FileKeys, int>> hits;
                    if (!index.TryGetValue(gram, out hits))
                    {
                        hits = new List<KeyValuePair<FileKeys, int>>();
                        index[gram] = hits;
                    }
                    hits.Add(new KeyValuePair<FileKeys, int>(file, p));
                }
            }

            return index;
        }

        /// <summary>
        /// Battute uguali di seguito fra le righe da start a end e le battute libere di un file da una posizione
        /// </summary>
        /// <param name="lines">Chiavi delle righe</param>
        /// <param name="start">Prima riga</param>
        /// <param name="end">Riga dopo l'ultima confrontabile</param>
        /// <param name="file">File confrontato</param>
        /// <param name="position">Prima battuta del file</param>
        /// <returns>Lunghezza della corsa comune</returns>
        private static int Common(string[] lines, int start, int end, FileKeys file, int position)
        {
            int length = 0;
            while (start + length < end && position + length < file.Keys.Length && !file.Assigned[position + length]
                && string.Equals(lines[start + length], file.Keys[position + length], StringComparison.Ordinal))
                length++;
            return length;
        }

        /// <summary>
        /// Restringe una corsa di battute perché non tagli una frase spezzata del file, né all'inizio né alla fine
        /// </summary>
        /// <param name="file">File della corsa</param>
        /// <param name="first">Prima battuta</param>
        /// <param name="last">Battuta dopo l'ultima</param>
        private static void Trim(FileKeys file, ref int first, ref int last)
        {
            while (first < last && first > 0 && file.Cues[first].Group.HasValue && file.Cues[first].Group == file.Cues[first - 1].Group)
                first++;
            while (last > first && last < file.Cues.Count && file.Cues[last].Group.HasValue && file.Cues[last].Group == file.Cues[last - 1].Group)
                last--;
        }

        /// <summary>
        /// Aggiunge alla tratta un'occorrenza e ne segna le battute come prese
        /// </summary>
        /// <param name="stretch">Tratta</param>
        /// <param name="file">File dell'occorrenza</param>
        /// <param name="first">Prima battuta</param>
        /// <param name="last">Battuta dopo l'ultima</param>
        /// <param name="offset">Riga della tratta della prima battuta</param>
        private static void Claim(SharedStretch stretch, FileKeys file, int first, int last, int offset)
        {
            SharedStretchOccurrence occurrence = new SharedStretchOccurrence();
            occurrence.FileId = file.FileId;
            occurrence.Offset = offset;
            for (int i = first; i < last; i++)
            {
                file.Assigned[i] = true;
                occurrence.Ids.Add(file.Cues[i].Id.Value);
            }
            stretch.Occurrences.Add(occurrence);
        }

        /// <summary>
        /// Aggiunge alla tratta le corse libere di almeno tre battute uguali a righe consecutive, da qualunque riga comincino: la
        /// più lunga per prima, così una sigla accorciata con un ritornello ripetuto si allinea dove copre più righe
        /// </summary>
        /// <param name="stretch">Tratta</param>
        /// <param name="lines">Chiavi delle righe della tratta</param>
        /// <param name="index">Posizioni delle terne dei file</param>
        private static void AddOccurrences(SharedStretch stretch, string[] lines, Dictionary<string, List<KeyValuePair<FileKeys, int>>> index)
        {
            while (true)
            {
                FileKeys best = null;
                int bestFirst = 0;
                int bestLast = 0;
                int bestOffset = 0;
                for (int j = 0; j + MIN_CUES <= lines.Length; j++)
                {
                    List<KeyValuePair<FileKeys, int>> hits;
                    if (!index.TryGetValue(Gram(lines, j), out hits))
                        continue;

                    foreach (KeyValuePair<FileKeys, int> hit in hits)
                    {
                        int first = hit.Value;
                        int last = first + Common(lines, j, lines.Length, hit.Key, hit.Value);
                        Trim(hit.Key, ref first, ref last);
                        if (last - first > bestLast - bestFirst)
                        {
                            best = hit.Key;
                            bestFirst = first;
                            bestLast = last;
                            bestOffset = j + first - hit.Value;
                        }
                    }
                }

                if (bestLast - bestFirst < MIN_CUES)
                    return;
                Claim(stretch, best, bestFirst, bestLast, bestOffset);
            }
        }

        /// <summary>
        /// Aggiunge la tratta dei cartelli in movimento di un file, se ne ha. Un cartello in movimento sono almeno due battute libere
        /// fuori dai gruppi, lunghe al più MAX_SIGN_FRAME_MS, con lo stesso stile e lo stesso testo: lo stesso cartello ridisegnato a
        /// ogni fotogramma con tag diversi. La prima battuta in ordine di tempo di ogni cartello è una riga della prima occorrenza, tradotta una volta
        /// sola in una consegna per tutti i cartelli del file; ogni altra battuta è un'occorrenza di quella riga
        /// </summary>
        /// <param name="stretches">Tratte del job</param>
        /// <param name="file">File con le battute già prese dalle tratte fra i file</param>
        private static void AddMovingSigns(List<SharedStretch> stretches, FileKeys file)
        {
            List<string> order = new List<string>();
            Dictionary<string, List<int>> frames = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < file.Cues.Count; i++)
            {
                SubtitleCue cue = file.Cues[i];
                if (file.Assigned[i] || cue.Group.HasValue || cue.EndMs - cue.StartMs > MAX_SIGN_FRAME_MS)
                    continue;
                string sign = cue.Style + "\n" + file.Keys[i];
                if (!frames.ContainsKey(sign))
                {
                    order.Add(sign);
                    frames[sign] = new List<int>();
                }
                frames[sign].Add(i);
            }

            order.RemoveAll(sign => frames[sign].Count < 2);
            if (order.Count == 0)
                return;

            SharedStretch stretch = new SharedStretch();
            foreach (SharedStretch existing in stretches)
                stretch.Index = Math.Max(stretch.Index, existing.Index);
            stretch.Index++;
            stretch.Format = file.Format;
            stretch.MovingSigns = true;
            SharedStretchOccurrence master = new SharedStretchOccurrence { FileId = file.FileId, Offset = 0 };
            stretch.Occurrences.Add(master);
            foreach (string sign in order)
            {
                List<int> positions = frames[sign];
                int line = stretch.Lines.Count;
                stretch.Lines.Add(file.Cues[positions[0]].CleanText);
                master.Ids.Add(file.Cues[positions[0]].Id.Value);
                foreach (int position in positions)
                {
                    file.Assigned[position] = true;
                    if (position != positions[0])
                        stretch.Occurrences.Add(new SharedStretchOccurrence { FileId = file.FileId, Offset = line, Ids = new List<int> { file.Cues[position].Id.Value } });
                }
            }
            stretches.Add(stretch);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Battute traducibili di un file con le chiavi di confronto e quelle già prese da una tratta
        /// </summary>
        private sealed class FileKeys
        {
            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="fileId">File del job</param>
            /// <param name="document">Documento preparato</param>
            public FileKeys(string fileId, SubtitleTranslationDocument document)
            {
                this.FileId = fileId;
                this.Format = document.Format;
                this.Cues = new List<SubtitleCue>();
                foreach (SubtitleCue cue in document.Cues)
                {
                    if (cue.Id.HasValue)
                        this.Cues.Add(cue);
                }
                this.Cues.Sort(SubtitleCue.CompareByTime);
                this.Keys = new string[this.Cues.Count];
                for (int i = 0; i < this.Keys.Length; i++)
                    this.Keys[i] = Key(this.Format, this.Cues[i].CleanText);
                this.Assigned = new bool[this.Cues.Count];
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// File del job
            /// </summary>
            public string FileId { get; }

            /// <summary>
            /// Formato del file
            /// </summary>
            public SubtitleTranslationFormat Format { get; }

            /// <summary>
            /// Battute traducibili in ordine di tempo, quello dei gruppi e dei blocchi
            /// </summary>
            public List<SubtitleCue> Cues { get; }

            /// <summary>
            /// Chiavi di confronto delle battute
            /// </summary>
            public string[] Keys { get; }

            /// <summary>
            /// Battute già in una tratta
            /// </summary>
            public bool[] Assigned { get; }

            #endregion
        }

        #endregion
    }
}
