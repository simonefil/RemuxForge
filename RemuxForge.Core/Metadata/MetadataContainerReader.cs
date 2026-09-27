using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Legge dal contenitore MKV quello che MediaInfo non espone in forma utilizzabile
    /// </summary>
    public class MetadataContainerReader
    {
        #region Costanti

        /// <summary>
        /// Timeout lettura JSON di mkvmerge
        /// </summary>
        private const int MKVMERGE_JSON_TIMEOUT_MS = 120000;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso mkvmerge
        /// </summary>
        private string _mkvMergePath;

        /// <summary>
        /// Percorso mkvextract
        /// </summary>
        private string _mkvExtractPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso mkvmerge</param>
        public MetadataContainerReader(string mkvMergePath)
            : this(mkvMergePath, "")
        {
        }

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso mkvmerge</param>
        /// <param name="mkvExtractPath">Percorso mkvextract</param>
        public MetadataContainerReader(string mkvMergePath, string mkvExtractPath)
        {
            this._mkvMergePath = !string.IsNullOrEmpty(mkvMergePath) ? mkvMergePath : "mkvmerge";
            this._mkvExtractPath = !string.IsNullOrEmpty(mkvExtractPath) ? mkvExtractPath : "mkvextract";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Popola allegati e capitoli del record leggendoli dal contenitore
        /// </summary>
        /// <param name="record">Record metadata</param>
        public void PopulateContainerInfo(MkvMetadataRecord record)
        {
            List<MkvMetadataAttachmentInfo> attachments;
            List<MkvMetadataChapterInfo> chapters;

            if (record == null || string.IsNullOrEmpty(record.InputFile))
                return;

            attachments = this.ReadAttachments(record.InputFile);
            chapters = this.ReadChapters(record.InputFile);

            if (record.FileInfo != null)
            {
                record.FileInfo.Attachments = attachments;
                record.FileInfo.Chapters = chapters;
                WriteContainerFields(record.FileInfo);
            }

            if (record.OriginalFileInfo != null)
            {
                record.OriginalFileInfo.Attachments = CloneAttachments(attachments);
                record.OriginalFileInfo.Chapters = CloneChapters(chapters);
                WriteContainerFields(record.OriginalFileInfo);
            }
        }

        /// <summary>
        /// Riscrive i campi di sola lettura che espongono gli allegati alle condizioni
        /// </summary>
        /// <param name="fileInfo">Info file da aggiornare</param>
        public static void WriteContainerFields(MkvMetadataFileInfo fileInfo)
        {
            List<MkvMetadataAttachmentInfo> attachments = fileInfo != null ? fileInfo.Attachments : null;
            List<string> names = new List<string>();

            if (fileInfo == null)
                return;

            for (int i = 0; attachments != null && i < attachments.Count; i++)
            {
                names.Add(attachments[i].FileName);
            }

            // Una condizione "contiene cover.jpg" su una stringa sola copre il caso
            // reale senza introdurre un tipo di condizione nuovo per gli allegati
            fileInfo.Fields["attachment_count"] = names.Count.ToString(CultureInfo.InvariantCulture);
            fileInfo.Fields["attachment_names"] = string.Join(", ", names);
        }

        /// <summary>
        /// Legge gli allegati di un file MKV
        /// </summary>
        /// <param name="filePath">File MKV</param>
        /// <returns>Allegati presenti, vuoto se la lettura non riesce</returns>
        public List<MkvMetadataAttachmentInfo> ReadAttachments(string filePath)
        {
            List<MkvMetadataAttachmentInfo> result = new List<MkvMetadataAttachmentInfo>();
            ProcessResult processResult;
            JsonDocument document = null;

            processResult = ProcessRunner.Run(this._mkvMergePath, new string[] { "-J", filePath }, MKVMERGE_JSON_TIMEOUT_MS);
            if (processResult.ExitCode != 0 || string.IsNullOrEmpty(processResult.Stdout.Trim()))
                throw new InvalidOperationException(AppText.F("metadata.reader.attachmentsFailed", LastLine(processResult.Stderr)));

            try
            {
                document = JsonDocument.Parse(processResult.Stdout);
                JsonElement attachments;
                if (!document.RootElement.TryGetProperty("attachments", out attachments) || attachments.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (JsonElement item in attachments.EnumerateArray())
                {
                    result.Add(ParseAttachment(item));
                }
            }
            finally
            {
                if (document != null)
                    document.Dispose();
            }

            return result;
        }

        /// <summary>
        /// Legge i capitoli di un file MKV
        /// </summary>
        /// <param name="filePath">File MKV</param>
        /// <returns>Capitoli presenti, vuoto se il file non ne ha</returns>
        public List<MkvMetadataChapterInfo> ReadChapters(string filePath)
        {
            return ParseChapters(this.ReadChaptersXml(filePath));
        }

        /// <summary>
        /// Legge il blocco capitoli di un file MKV cosi' come lo scrive mkvextract
        /// </summary>
        /// <param name="filePath">File MKV</param>
        /// <returns>XML capitoli, stringa vuota se il file non ne ha</returns>
        public string ReadChaptersXml(string filePath)
        {
            ProcessResult processResult;

            // mkvextract scrive su stdout con il nome di destinazione "-": un file
            // senza capitoli non produce niente, e non e' un errore
            processResult = ProcessRunner.Run(this._mkvExtractPath, new string[] { filePath, "chapters", "-" }, MKVMERGE_JSON_TIMEOUT_MS);
            if (processResult.ExitCode != 0)
                throw new InvalidOperationException(AppText.F("metadata.reader.chaptersFailed", LastLine(processResult.Stderr)));

            return processResult.Stdout != null ? processResult.Stdout.Trim('\uFEFF', ' ', '\r', '\n', '\t') : "";
        }

        /// <summary>
        /// Converte l'XML capitoli nella lista piatta che l'editor mostra, edizione per edizione
        /// </summary>
        /// <param name="xml">XML capitoli</param>
        /// <returns>Capitoli in ordine di documento, con edizione e profondita'</returns>
        public static List<MkvMetadataChapterInfo> ParseChapters(string xml)
        {
            List<MkvMetadataChapterInfo> result = new List<MkvMetadataChapterInfo>();
            List<XmlElement> editions;
            XmlDocument document = LoadChaptersDocument(xml);
            int atomIndex = 0;

            if (document == null)
                return result;

            editions = GetChildElements(document.DocumentElement, "EditionEntry");
            for (int i = 0; i < editions.Count; i++)
            {
                AddChapterAtoms(editions[i], i, GetChildText(editions[i], "EditionFlagOrdered").Trim() == "1", "", 0, result, ref atomIndex);
            }

            return result;
        }

        /// <summary>
        /// Applica all'XML originale i capitoli voluti, toccando solo i nodi che cambiano
        /// </summary>
        /// <param name="originalXml">XML capitoli letto dal file, vuoto se non ne ha</param>
        /// <param name="chapters">Capitoli come devono risultare</param>
        /// <returns>XML da scrivere, stringa vuota se non resta nessun capitolo</returns>
        public static string ApplyChapterEdits(string originalXml, List<MkvMetadataChapterInfo> chapters)
        {
            Dictionary<string, MkvMetadataChapterInfo> wanted = new Dictionary<string, MkvMetadataChapterInfo>(StringComparer.Ordinal);
            Dictionary<string, bool> existing = new Dictionary<string, bool>(StringComparer.Ordinal);
            List<XmlElement> touchedParents = new List<XmlElement>();
            List<XmlElement> atoms = new List<XmlElement>();
            List<XmlElement> editions;
            XmlDocument document = LoadChaptersDocument(originalXml);
            Random random = new Random();

            for (int i = 0; chapters != null && i < chapters.Count; i++)
            {
                wanted[chapters[i].Uid] = chapters[i];
            }

            if (document == null)
            {
                document = new XmlDocument();
                document.AppendChild(document.CreateXmlDeclaration("1.0", "UTF-8", null));
                document.AppendChild(document.CreateElement("Chapters"));
            }

            editions = GetChildElements(document.DocumentElement, "EditionEntry");
            for (int i = 0; i < editions.Count; i++)
            {
                CollectChapterAtoms(editions[i], atoms);
            }

            // Tutto quello che l'editor non tocca resta byte per byte: edizioni, flag,
            // display in altre lingue, capitoli annidati e precisione al nanosecondo
            for (int i = 0; i < atoms.Count; i++)
            {
                XmlElement atom = atoms[i];
                string key = GetChapterKey(atom, i);
                MkvMetadataChapterInfo chapter;
                existing[key] = true;

                if (!wanted.TryGetValue(key, out chapter))
                {
                    atom.ParentNode.RemoveChild(atom);
                    continue;
                }

                if (UpdateChapterAtom(document, atom, chapter) && !touchedParents.Contains((XmlElement)atom.ParentNode))
                    touchedParents.Add((XmlElement)atom.ParentNode);
            }

            for (int i = 0; chapters != null && i < chapters.Count; i++)
            {
                MkvMetadataChapterInfo chapter = chapters[i];
                XmlElement edition;
                if (existing.ContainsKey(chapter.Uid))
                    continue;

                // Un capitolo nuovo sta al primo livello della sua edizione: su un file
                // senza capitoli l'edizione si crea insieme al primo capitolo
                while (editions.Count <= chapter.EditionIndex)
                {
                    XmlElement created = document.CreateElement("EditionEntry");
                    document.DocumentElement.AppendChild(created);
                    editions.Add(created);
                }

                edition = editions[chapter.EditionIndex];
                edition.AppendChild(CreateChapterAtom(document, chapter, random));
                if (!touchedParents.Contains(edition))
                    touchedParents.Add(edition);
            }

            for (int i = 0; i < touchedParents.Count; i++)
            {
                SortChapterAtoms(touchedParents[i]);
            }

            // Matroska non ammette un'edizione vuota: se l'utente ne ha tolto ogni
            // capitolo, sparisce l'edizione, e senza edizioni sparisce il blocco
            for (int i = 0; i < editions.Count; i++)
            {
                if (GetChildElements(editions[i], "ChapterAtom").Count == 0)
                    editions[i].ParentNode.RemoveChild(editions[i]);
            }

            if (GetChildElements(document.DocumentElement, "EditionEntry").Count == 0)
                return "";

            return document.OuterXml;
        }

        /// <summary>
        /// Converte un timestamp capitolo hh:mm:ss.nnnnnnnnn in nanosecondi
        /// </summary>
        /// <param name="text">Timestamp, anche mm:ss o ss, con fino a nove decimali</param>
        /// <param name="nanoseconds">Nanosecondi</param>
        /// <returns>Vero se il testo e' un timestamp valido</returns>
        public static bool TryParseChapterTime(string text, out long nanoseconds)
        {
            string[] parts = (text != null ? text : "").Trim().Split(':');
            string seconds = parts[parts.Length - 1];
            string fraction = "";
            long whole = 0;
            int dot = seconds.IndexOf('.');

            nanoseconds = 0;
            if (parts.Length > 3 || seconds.Length == 0)
                return false;

            if (dot >= 0)
            {
                fraction = seconds.Substring(dot + 1);
                seconds = seconds.Substring(0, dot);
            }

            if (fraction.Length > 9 || !IsDigits(fraction) || !IsDigits(seconds) || seconds.Length == 0)
                return false;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length == 0 || !IsDigits(parts[i]))
                    return false;

                whole = whole * 60 + long.Parse(parts[i], CultureInfo.InvariantCulture);
            }

            // Minuti e secondi oltre 59 si rifiutano solo se c'e' un'unita' sopra di loro:
            // "90" da solo sono novanta secondi, "01:90" e' un errore di battitura
            if (parts.Length > 1 && long.Parse(seconds, CultureInfo.InvariantCulture) > 59)
                return false;

            if (parts.Length > 2 && long.Parse(parts[1], CultureInfo.InvariantCulture) > 59)
                return false;

            whole = whole * 60 + long.Parse(seconds, CultureInfo.InvariantCulture);
            nanoseconds = whole * 1000000000L + (fraction.Length > 0 ? long.Parse(fraction.PadRight(9, '0'), CultureInfo.InvariantCulture) : 0);
            return true;
        }

        /// <summary>
        /// Converte nanosecondi nel timestamp capitolo hh:mm:ss.nnnnnnnnn
        /// </summary>
        /// <param name="nanoseconds">Nanosecondi</param>
        /// <returns>Timestamp a nove decimali</returns>
        public static string FormatChapterTime(long nanoseconds)
        {
            long value = nanoseconds >= 0 ? nanoseconds : 0;
            long totalSeconds = value / 1000000000L;

            return (totalSeconds / 3600).ToString("00", CultureInfo.InvariantCulture) + ":" +
                (totalSeconds / 60 % 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture) + "." +
                (value % 1000000000L).ToString("000000000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Clona una lista di capitoli
        /// </summary>
        /// <param name="chapters">Capitoli da clonare</param>
        /// <returns>Copia indipendente</returns>
        public static List<MkvMetadataChapterInfo> CloneChapters(List<MkvMetadataChapterInfo> chapters)
        {
            List<MkvMetadataChapterInfo> result = new List<MkvMetadataChapterInfo>();

            for (int i = 0; chapters != null && i < chapters.Count; i++)
            {
                result.Add(new MkvMetadataChapterInfo
                {
                    Uid = chapters[i].Uid,
                    StartNs = chapters[i].StartNs,
                    EndNs = chapters[i].EndNs,
                    EditionIndex = chapters[i].EditionIndex,
                    EditionOrdered = chapters[i].EditionOrdered,
                    ParentUid = chapters[i].ParentUid,
                    Depth = chapters[i].Depth,
                    Name = chapters[i].Name,
                    Language = chapters[i].Language
                });
            }

            return result;
        }

        /// <summary>
        /// Estrae un allegato in un file temporaneo e ne restituisce il contenuto
        /// </summary>
        /// <param name="filePath">File MKV</param>
        /// <param name="attachmentId">Id dell'allegato come lo numera mkvmerge</param>
        /// <returns>Contenuto dell'allegato, null se l'estrazione non riesce</returns>
        public byte[] ExtractAttachment(string filePath, int attachmentId)
        {
            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remuxforge-attachment-" + Guid.NewGuid().ToString("N"));
            ProcessResult processResult;

            try
            {
                processResult = ProcessRunner.Run(this._mkvExtractPath, new string[] { filePath, "attachments", attachmentId.ToString(CultureInfo.InvariantCulture) + ":" + tempFile }, MKVMERGE_JSON_TIMEOUT_MS);
                if (processResult.ExitCode != 0 || !System.IO.File.Exists(tempFile))
                    return null;

                return System.IO.File.ReadAllBytes(tempFile);
            }
            finally
            {
                if (System.IO.File.Exists(tempFile))
                    System.IO.File.Delete(tempFile);
            }
        }

        /// <summary>
        /// Clona una lista di allegati
        /// </summary>
        /// <param name="attachments">Allegati da clonare</param>
        /// <returns>Copia indipendente</returns>
        public static List<MkvMetadataAttachmentInfo> CloneAttachments(List<MkvMetadataAttachmentInfo> attachments)
        {
            List<MkvMetadataAttachmentInfo> result = new List<MkvMetadataAttachmentInfo>();

            for (int i = 0; attachments != null && i < attachments.Count; i++)
            {
                result.Add(new MkvMetadataAttachmentInfo
                {
                    Id = attachments[i].Id,
                    FileName = attachments[i].FileName,
                    MimeType = attachments[i].MimeType,
                    Description = attachments[i].Description,
                    Size = attachments[i].Size,
                    Uid = attachments[i].Uid
                });
            }

            return result;
        }

        /// <summary>
        /// Deduce il tipo MIME di un allegato dall'estensione del file
        /// </summary>
        /// <param name="fileName">Nome file</param>
        /// <returns>Tipo MIME, stringa vuota se sconosciuto</returns>
        public static string GuessMimeType(string fileName)
        {
            string extension = System.IO.Path.GetExtension(fileName != null ? fileName : "").ToLowerInvariant();

            if (extension == ".jpg" || extension == ".jpeg") { return "image/jpeg"; }
            if (extension == ".png") { return "image/png"; }
            if (extension == ".webp") { return "image/webp"; }
            if (extension == ".gif") { return "image/gif"; }
            if (extension == ".ttf") { return "font/ttf"; }
            if (extension == ".otf") { return "font/otf"; }
            if (extension == ".woff") { return "font/woff"; }
            if (extension == ".woff2") { return "font/woff2"; }
            if (extension == ".txt") { return "text/plain"; }
            if (extension == ".xml") { return "text/xml"; }
            if (extension == ".pdf") { return "application/pdf"; }

            return "";
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Carica l'XML capitoli senza risolvere la DTD che mkvextract dichiara
        /// </summary>
        /// <param name="xml">XML capitoli</param>
        /// <returns>Documento, null se l'XML e' vuoto</returns>
        private static XmlDocument LoadChaptersDocument(string xml)
        {
            XmlDocument document;

            if (string.IsNullOrEmpty(xml != null ? xml.Trim() : null))
                return null;

            document = new XmlDocument();
            document.XmlResolver = null;
            document.LoadXml(xml);
            return document.DocumentElement != null ? document : null;
        }

        /// <summary>
        /// Aggiunge alla lista i capitoli figli di un nodo, ricorsivamente in ordine di documento
        /// </summary>
        /// <param name="parent">Edizione o capitolo padre</param>
        /// <param name="editionIndex">Indice edizione</param>
        /// <param name="ordered">Vero se l'edizione e' ordinata</param>
        /// <param name="parentUid">Chiave del capitolo padre, vuota al primo livello</param>
        /// <param name="depth">Profondita' dei figli</param>
        /// <param name="result">Lista da popolare</param>
        /// <param name="atomIndex">Contatore dei capitoli visti, per le chiavi dei capitoli senza UID</param>
        private static void AddChapterAtoms(XmlElement parent, int editionIndex, bool ordered, string parentUid, int depth, List<MkvMetadataChapterInfo> result, ref int atomIndex)
        {
            List<XmlElement> atoms = GetChildElements(parent, "ChapterAtom");

            for (int i = 0; i < atoms.Count; i++)
            {
                MkvMetadataChapterInfo chapter = new MkvMetadataChapterInfo();
                XmlElement display = GetFirstChildElement(atoms[i], "ChapterDisplay");
                long time;

                chapter.Uid = GetChapterKey(atoms[i], atomIndex);
                chapter.EditionIndex = editionIndex;
                chapter.EditionOrdered = ordered;
                chapter.ParentUid = parentUid;
                chapter.Depth = depth;
                if (TryParseChapterTime(GetChildText(atoms[i], "ChapterTimeStart"), out time))
                    chapter.StartNs = time;

                if (TryParseChapterTime(GetChildText(atoms[i], "ChapterTimeEnd"), out time))
                    chapter.EndNs = time;

                // Un capitolo puo' avere un nome per lingua: l'editor governa il primo,
                // che e' quello che i player mostrano, e lascia intatti gli altri
                if (display != null)
                {
                    chapter.Name = GetChildText(display, "ChapterString");
                    chapter.Language = GetChildText(display, "ChapterLanguage").Trim();
                }

                result.Add(chapter);
                atomIndex++;
                AddChapterAtoms(atoms[i], editionIndex, ordered, chapter.Uid, depth + 1, result, ref atomIndex);
            }
        }

        /// <summary>
        /// Raccoglie i nodi ChapterAtom di un nodo nello stesso ordine di AddChapterAtoms
        /// </summary>
        /// <param name="parent">Edizione o capitolo padre</param>
        /// <param name="result">Lista da popolare</param>
        private static void CollectChapterAtoms(XmlElement parent, List<XmlElement> result)
        {
            List<XmlElement> atoms = GetChildElements(parent, "ChapterAtom");

            for (int i = 0; i < atoms.Count; i++)
            {
                result.Add(atoms[i]);
                CollectChapterAtoms(atoms[i], result);
            }
        }

        /// <summary>
        /// Restituisce la chiave di un capitolo: l'UID, o la posizione se il file non lo dichiara
        /// </summary>
        /// <param name="atom">Nodo capitolo</param>
        /// <param name="atomIndex">Posizione del capitolo in ordine di documento</param>
        /// <returns>Chiave del capitolo</returns>
        private static string GetChapterKey(XmlElement atom, int atomIndex)
        {
            string uid = GetChildText(atom, "ChapterUID").Trim();

            return uid.Length > 0 ? uid : "#" + atomIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Porta un nodo capitolo esistente allo stato voluto, lasciando stare cio' che non cambia
        /// </summary>
        /// <param name="document">Documento capitoli</param>
        /// <param name="atom">Nodo capitolo</param>
        /// <param name="chapter">Stato voluto</param>
        /// <returns>Vero se l'inizio e' cambiato e i fratelli vanno riordinati</returns>
        private static bool UpdateChapterAtom(XmlDocument document, XmlElement atom, MkvMetadataChapterInfo chapter)
        {
            XmlElement display = GetFirstChildElement(atom, "ChapterDisplay");
            XmlElement end = GetFirstChildElement(atom, "ChapterTimeEnd");
            long current;
            long endTime;
            bool moved = false;

            if (!TryParseChapterTime(GetChildText(atom, "ChapterTimeStart"), out current) || current != chapter.StartNs)
            {
                SetChildText(document, atom, "ChapterTimeStart", FormatChapterTime(chapter.StartNs));
                moved = true;

                // Una fine che non sta piu' dopo l'inizio renderebbe il capitolo invalido:
                // senza fine il player la ricava dal capitolo successivo
                if (end != null && TryParseChapterTime(end.InnerText, out endTime) && endTime <= chapter.StartNs)
                    atom.RemoveChild(end);
            }

            if (display == null)
            {
                if (!string.IsNullOrEmpty(chapter.Name))
                    atom.AppendChild(CreateChapterDisplay(document, chapter));
            }
            else if (!string.Equals(GetChildText(display, "ChapterString"), chapter.Name, StringComparison.Ordinal))
            {
                SetChildText(document, display, "ChapterString", chapter.Name != null ? chapter.Name : "");
            }

            return moved;
        }

        /// <summary>
        /// Crea il nodo di un capitolo aggiunto dall'editor
        /// </summary>
        /// <param name="document">Documento capitoli</param>
        /// <param name="chapter">Capitolo da creare</param>
        /// <param name="random">Generatore per l'UID se il capitolo non ne porta uno valido</param>
        /// <returns>Nodo ChapterAtom</returns>
        private static XmlElement CreateChapterAtom(XmlDocument document, MkvMetadataChapterInfo chapter, Random random)
        {
            XmlElement atom = document.CreateElement("ChapterAtom");
            ulong uid;

            if (!ulong.TryParse(chapter.Uid, NumberStyles.None, CultureInfo.InvariantCulture, out uid) || uid == 0)
                uid = (ulong)random.NextInt64(1, long.MaxValue);

            SetChildText(document, atom, "ChapterUID", uid.ToString(CultureInfo.InvariantCulture));
            SetChildText(document, atom, "ChapterTimeStart", FormatChapterTime(chapter.StartNs));
            atom.AppendChild(CreateChapterDisplay(document, chapter));
            return atom;
        }

        /// <summary>
        /// Crea il display con nome e lingua di un capitolo
        /// </summary>
        /// <param name="document">Documento capitoli</param>
        /// <param name="chapter">Capitolo</param>
        /// <returns>Nodo ChapterDisplay</returns>
        private static XmlElement CreateChapterDisplay(XmlDocument document, MkvMetadataChapterInfo chapter)
        {
            XmlElement display = document.CreateElement("ChapterDisplay");

            SetChildText(document, display, "ChapterString", chapter.Name != null ? chapter.Name : "");
            SetChildText(document, display, "ChapterLanguage", !string.IsNullOrEmpty(chapter.Language) ? chapter.Language : "und");
            return display;
        }

        /// <summary>
        /// Rimette in ordine di inizio i capitoli figli di un nodo, salvo nelle edizioni ordinate
        /// </summary>
        /// <param name="parent">Edizione o capitolo padre</param>
        private static void SortChapterAtoms(XmlElement parent)
        {
            XmlElement edition = parent;
            List<XmlElement> original;
            List<XmlElement> atoms;

            while (edition != null && edition.Name != "EditionEntry")
            {
                edition = edition.ParentNode as XmlElement;
            }

            // In un'edizione ordinata la sequenza dei capitoli e' l'ordine di riproduzione,
            // non una conseguenza dei tempi: spostarli cambierebbe il film
            if (edition != null && GetChildText(edition, "EditionFlagOrdered").Trim() == "1")
                return;

            original = GetChildElements(parent, "ChapterAtom");
            atoms = new List<XmlElement>(original);

            // List.Sort non e' stabile: a parita' di inizio decide la posizione di partenza
            atoms.Sort(delegate (XmlElement left, XmlElement right)
            {
                long leftTime;
                long rightTime;
                TryParseChapterTime(GetChildText(left, "ChapterTimeStart"), out leftTime);
                TryParseChapterTime(GetChildText(right, "ChapterTimeStart"), out rightTime);
                int compared = leftTime.CompareTo(rightTime);
                return compared != 0 ? compared : original.IndexOf(left).CompareTo(original.IndexOf(right));
            });

            for (int i = 0; i < atoms.Count; i++)
            {
                parent.RemoveChild(atoms[i]);
            }

            for (int i = 0; i < atoms.Count; i++)
            {
                parent.AppendChild(atoms[i]);
            }
        }

        /// <summary>
        /// Restituisce gli elementi figli diretti con un nome
        /// </summary>
        /// <param name="parent">Nodo padre</param>
        /// <param name="name">Nome elemento</param>
        /// <returns>Elementi in ordine di documento</returns>
        private static List<XmlElement> GetChildElements(XmlElement parent, string name)
        {
            List<XmlElement> result = new List<XmlElement>();

            for (XmlNode node = parent != null ? parent.FirstChild : null; node != null; node = node.NextSibling)
            {
                XmlElement element = node as XmlElement;
                if (element != null && element.Name == name)
                    result.Add(element);
            }

            return result;
        }

        /// <summary>
        /// Restituisce il primo elemento figlio diretto con un nome
        /// </summary>
        /// <param name="parent">Nodo padre</param>
        /// <param name="name">Nome elemento</param>
        /// <returns>Elemento o null</returns>
        private static XmlElement GetFirstChildElement(XmlElement parent, string name)
        {
            List<XmlElement> elements = GetChildElements(parent, name);

            return elements.Count > 0 ? elements[0] : null;
        }

        /// <summary>
        /// Restituisce il testo del primo figlio diretto con un nome
        /// </summary>
        /// <param name="parent">Nodo padre</param>
        /// <param name="name">Nome elemento</param>
        /// <returns>Testo, stringa vuota se il figlio non c'e'</returns>
        private static string GetChildText(XmlElement parent, string name)
        {
            XmlElement element = GetFirstChildElement(parent, name);

            return element != null ? element.InnerText : "";
        }

        /// <summary>
        /// Imposta il testo di un figlio diretto, creandolo se manca
        /// </summary>
        /// <param name="document">Documento</param>
        /// <param name="parent">Nodo padre</param>
        /// <param name="name">Nome elemento</param>
        /// <param name="text">Testo</param>
        private static void SetChildText(XmlDocument document, XmlElement parent, string name, string text)
        {
            XmlElement element = GetFirstChildElement(parent, name);

            if (element == null)
            {
                element = document.CreateElement(name);
                parent.AppendChild(element);
            }

            element.InnerText = text;
        }

        /// <summary>
        /// Indica se un testo e' fatto solo di cifre decimali
        /// </summary>
        /// <param name="text">Testo</param>
        /// <returns>Vero se ogni carattere e' una cifra, anche per il testo vuoto</returns>
        private static bool IsDigits(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Converte un elemento JSON in un allegato
        /// </summary>
        /// <param name="item">Elemento JSON</param>
        /// <returns>Allegato</returns>
        private static MkvMetadataAttachmentInfo ParseAttachment(JsonElement item)
        {
            MkvMetadataAttachmentInfo attachment = new MkvMetadataAttachmentInfo();
            JsonElement value;

            if (item.TryGetProperty("id", out value) && value.ValueKind == JsonValueKind.Number)
                attachment.Id = value.GetInt32();

            if (item.TryGetProperty("file_name", out value) && value.ValueKind == JsonValueKind.String)
                attachment.FileName = value.GetString();

            if (item.TryGetProperty("content_type", out value) && value.ValueKind == JsonValueKind.String)
                attachment.MimeType = value.GetString();

            if (item.TryGetProperty("description", out value) && value.ValueKind == JsonValueKind.String)
                attachment.Description = value.GetString();

            if (item.TryGetProperty("size", out value) && value.ValueKind == JsonValueKind.Number)
                attachment.Size = value.GetInt64();

            if (item.TryGetProperty("properties", out value) && value.ValueKind == JsonValueKind.Object)
            {
                JsonElement uid;
                if (value.TryGetProperty("uid", out uid) && uid.ValueKind == JsonValueKind.Number)
                    attachment.Uid = uid.GetUInt64().ToString(CultureInfo.InvariantCulture);
            }

            return attachment;
        }

        /// <summary>
        /// Restituisce l'ultima riga non vuota di un output
        /// </summary>
        /// <param name="text">Testo</param>
        /// <returns>Ultima riga</returns>
        private static string LastLine(string text)
        {
            string[] lines = (text != null ? text : "").Split('\n');

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    return lines[i].Trim();
            }

            return "";
        }

        #endregion
    }
}
