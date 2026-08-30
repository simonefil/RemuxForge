using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
        /// Riscrive i campi di sola lettura che espongono allegati e capitoli alle condizioni
        /// </summary>
        /// <param name="fileInfo">Info file da aggiornare</param>
        public static void WriteContainerFields(MkvMetadataFileInfo fileInfo)
        {
            List<MkvMetadataAttachmentInfo> attachments = fileInfo != null ? fileInfo.Attachments : null;
            List<MkvMetadataChapterInfo> chapters = fileInfo != null ? fileInfo.Chapters : null;
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
            fileInfo.Fields["chapter_count"] = (chapters != null ? chapters.Count : 0).ToString(CultureInfo.InvariantCulture);
            fileInfo.Fields["chapter_first_name"] = chapters != null && chapters.Count > 0 ? chapters[0].Name : "";
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
            List<MkvMetadataChapterInfo> result = new List<MkvMetadataChapterInfo>();
            ProcessResult processResult;
            string xml;

            // mkvextract scrive su stdout con il nome di destinazione "-": un file
            // senza capitoli non produce niente, e non e' un errore
            processResult = ProcessRunner.Run(this._mkvExtractPath, new string[] { filePath, "chapters", "-" }, MKVMERGE_JSON_TIMEOUT_MS);
            if (processResult.ExitCode != 0)
                throw new InvalidOperationException(AppText.F("metadata.reader.chaptersFailed", LastLine(processResult.Stderr)));

            xml = processResult.Stdout != null ? processResult.Stdout.Trim('\uFEFF', ' ', '\r', '\n', '\t') : "";
            if (xml.Length == 0)
                return result;

            XmlDocument document = new XmlDocument();
            document.XmlResolver = null;
            document.LoadXml(xml);

            XmlNodeList atoms = document.GetElementsByTagName("ChapterAtom");
            for (int i = 0; i < atoms.Count; i++)
            {
                result.Add(ParseChapter(atoms[i]));
            }

            return result;
        }

        /// <summary>
        /// Costruisce il documento XML capitoli nel formato che mkvpropedit accetta
        /// </summary>
        /// <param name="chapters">Capitoli da scrivere</param>
        /// <returns>XML capitoli</returns>
        public static string BuildChaptersXml(List<MkvMetadataChapterInfo> chapters)
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            builder.AppendLine("<Chapters>");
            builder.AppendLine("  <EditionEntry>");

            for (int i = 0; chapters != null && i < chapters.Count; i++)
            {
                MkvMetadataChapterInfo chapter = chapters[i];
                builder.AppendLine("    <ChapterAtom>");
                if (!string.IsNullOrEmpty(chapter.Uid))
                    builder.AppendLine("      <ChapterUID>" + chapter.Uid + "</ChapterUID>");

                builder.AppendLine("      <ChapterTimeStart>" + FormatChapterTime(chapter.StartMs) + "</ChapterTimeStart>");
                if (chapter.EndMs > 0)
                    builder.AppendLine("      <ChapterTimeEnd>" + FormatChapterTime(chapter.EndMs) + "</ChapterTimeEnd>");

                builder.AppendLine("      <ChapterDisplay>");
                builder.AppendLine("        <ChapterString>" + EscapeXml(chapter.Name) + "</ChapterString>");
                builder.AppendLine("        <ChapterLanguage>" + EscapeXml(!string.IsNullOrEmpty(chapter.Language) ? chapter.Language : "und") + "</ChapterLanguage>");
                builder.AppendLine("      </ChapterDisplay>");
                builder.AppendLine("    </ChapterAtom>");
            }

            builder.AppendLine("  </EditionEntry>");
            builder.AppendLine("</Chapters>");

            return builder.ToString();
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
                    StartMs = chapters[i].StartMs,
                    EndMs = chapters[i].EndMs,
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
        /// Converte un nodo ChapterAtom in un capitolo
        /// </summary>
        /// <param name="atom">Nodo XML del capitolo</param>
        /// <returns>Capitolo</returns>
        private static MkvMetadataChapterInfo ParseChapter(XmlNode atom)
        {
            MkvMetadataChapterInfo chapter = new MkvMetadataChapterInfo();
            XmlNode node;

            node = atom.SelectSingleNode("ChapterUID");
            if (node != null)
                chapter.Uid = node.InnerText.Trim();

            node = atom.SelectSingleNode("ChapterTimeStart");
            if (node != null)
                chapter.StartMs = ParseChapterTime(node.InnerText);

            node = atom.SelectSingleNode("ChapterTimeEnd");
            if (node != null)
                chapter.EndMs = ParseChapterTime(node.InnerText);

            // Un capitolo puo' avere un nome per lingua: la UI ne governa uno solo,
            // e il primo display e' quello che i player mostrano per primo
            node = atom.SelectSingleNode("ChapterDisplay/ChapterString");
            if (node != null)
                chapter.Name = node.InnerText;

            node = atom.SelectSingleNode("ChapterDisplay/ChapterLanguage");
            if (node != null)
                chapter.Language = node.InnerText.Trim();

            return chapter;
        }

        /// <summary>
        /// Converte un timestamp capitolo hh:mm:ss.nnnnnnnnn in millisecondi
        /// </summary>
        /// <param name="text">Testo del timestamp</param>
        /// <returns>Millisecondi</returns>
        private static double ParseChapterTime(string text)
        {
            string[] parts = (text != null ? text : "").Trim().Split(':');
            double result = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                double value;
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return 0;

                result = result * 60 + value;
            }

            return result * 1000;
        }

        /// <summary>
        /// Converte millisecondi nel timestamp capitolo hh:mm:ss.nnnnnnnnn
        /// </summary>
        /// <param name="milliseconds">Millisecondi</param>
        /// <returns>Timestamp</returns>
        private static string FormatChapterTime(double milliseconds)
        {
            TimeSpan span = TimeSpan.FromMilliseconds(milliseconds >= 0 ? milliseconds : 0);

            return ((int)span.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" +
                span.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                span.Seconds.ToString("00", CultureInfo.InvariantCulture) + "." +
                (span.Milliseconds * 1000000).ToString("000000000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Rende sicuro un testo dentro un nodo XML
        /// </summary>
        /// <param name="text">Testo</param>
        /// <returns>Testo con le entita' sostituite</returns>
        private static string EscapeXml(string text)
        {
            string result = text != null ? text : "";

            result = result.Replace("&", "&amp;");
            result = result.Replace("<", "&lt;");
            result = result.Replace(">", "&gt;");

            return result;
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
