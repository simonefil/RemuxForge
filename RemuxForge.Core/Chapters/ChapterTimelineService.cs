using RemuxForge.Core.Localization;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace RemuxForge.Core.Chapters
{
    /// <summary>
    /// Autorita' unica dei capitoli: lettura dell'edizione piatta, trasformazioni temporali e scrittura XML
    /// </summary>
    public static class ChapterTimelineService
    {
        #region Metodi pubblici

        /// <summary>
        /// Legge i capitoli di un file MKV
        /// </summary>
        /// <param name="mkvExtractPath">Percorso mkvextract</param>
        /// <param name="filePath">File MKV</param>
        /// <returns>Capitoli dell'edizione predefinita, avvisi ed eventuale errore</returns>
        public static ChapterReadResult Read(string mkvExtractPath, string filePath)
        {
            ChapterReadResult result;
            string xml;

            try
            {
                xml = new MetadataContainerReader("", mkvExtractPath).ReadChaptersXml(filePath);
            }
            catch (InvalidOperationException ex)
            {
                result = new ChapterReadResult();
                result.Error = ex.Message;
                return result;
            }

            return FromXml(xml);
        }

        /// <summary>
        /// Converte l'XML capitoli nell'edizione piatta
        /// </summary>
        /// <param name="xml">XML capitoli come lo scrive mkvextract</param>
        /// <returns>Capitoli dell'edizione predefinita, avvisi ed eventuale errore</returns>
        public static ChapterReadResult FromXml(string xml)
        {
            ChapterReadResult result = new ChapterReadResult();
            List<MkvMetadataChapterInfo> atoms;
            int editionIndex;
            int otherEditions;
            int nested = 0;
            HashSet<int> seenEditions = new HashSet<int>();

            try
            {
                atoms = MetadataContainerReader.ParseChapters(xml);
                editionIndex = MetadataContainerReader.GetDefaultEditionIndex(xml);
            }
            catch (XmlException ex)
            {
                result.Error = AppText.F("chapters.invalidXml", ex.Message);
                return result;
            }

            if (editionIndex < 0)
                return result;

            for (int i = 0; i < atoms.Count; i++)
            {
                MkvMetadataChapterInfo atom = atoms[i];

                if (atom.EditionIndex != editionIndex)
                {
                    seenEditions.Add(atom.EditionIndex);
                    continue;
                }

                // I capitoli ordered rimandano ad altri segmenti: spostarli nel tempo cambierebbe cosa riproducono
                if (atom.EditionOrdered)
                {
                    result.Chapters.Clear();
                    result.Error = AppText.T("chapters.orderedUnsupported");
                    return result;
                }

                if (atom.Depth > 0)
                {
                    nested++;
                    continue;
                }

                result.Chapters.Add(new ChapterMark
                {
                    StartNs = atom.StartNs,
                    EndNs = atom.EndNs > atom.StartNs ? atom.EndNs : ChapterMark.NO_END,
                    Name = atom.Name != null ? atom.Name : "",
                    Language = atom.Language != null ? atom.Language : ""
                });
            }

            otherEditions = seenEditions.Count;
            if (otherEditions > 0)
                result.Warnings.Add(AppText.F("chapters.editionsSkipped", otherEditions));

            if (nested > 0)
                result.Warnings.Add(AppText.F("chapters.nestedSkipped", nested));

            SortByStart(result.Chapters);
            return result;
        }

        /// <summary>
        /// Capitoli di un taglio: restano quelli con inizio in [start, end), ribasati a zero, con la fine troncata al taglio
        /// </summary>
        /// <param name="chapters">Capitoli nel tempo del sorgente</param>
        /// <param name="startNs">Inizio del tratto conservato, incluso</param>
        /// <param name="endNs">Fine del tratto conservato, esclusa</param>
        /// <returns>Capitoli nel tempo del taglio</returns>
        public static List<ChapterMark> Cut(List<ChapterMark> chapters, long startNs, long endNs)
        {
            List<ChapterMark> result = new List<ChapterMark>();

            for (int i = 0; chapters != null && i < chapters.Count; i++)
            {
                ChapterMark chapter = chapters[i];
                ChapterMark cut;

                if (chapter.StartNs < startNs || chapter.StartNs >= endNs)
                    continue;

                cut = chapter.Clone();
                cut.StartNs = chapter.StartNs - startNs;
                cut.EndNs = chapter.HasEnd ? Math.Min(chapter.EndNs, endNs) - startNs : ChapterMark.NO_END;
                if (cut.HasEnd && cut.EndNs <= cut.StartNs)
                    cut.EndNs = ChapterMark.NO_END;

                result.Add(cut);
            }

            SortByStart(result);
            return result;
        }

        /// <summary>
        /// Capitoli di un montaggio: ogni tratto contribuisce i capitoli del proprio taglio, nella sua posizione del risultato
        /// </summary>
        /// <param name="chapters">Capitoli nel tempo del sorgente</param>
        /// <param name="ranges">Tratti del sorgente nell'ordine del risultato, anche ripetuti</param>
        /// <returns>Capitoli nel tempo del risultato</returns>
        public static List<ChapterMark> Montage(List<ChapterMark> chapters, IList<ChapterRange> ranges)
        {
            List<ChapterMark> result = new List<ChapterMark>();
            long offset = 0;

            for (int i = 0; ranges != null && i < ranges.Count; i++)
            {
                List<ChapterMark> cut = Cut(chapters, ranges[i].StartNs, ranges[i].EndNs);

                for (int j = 0; j < cut.Count; j++)
                {
                    cut[j].StartNs += offset;
                    if (cut[j].HasEnd)
                        cut[j].EndNs += offset;

                    result.Add(cut[j]);
                }

                offset += Math.Max(0, ranges[i].EndNs - ranges[i].StartNs);
            }

            return result;
        }

        /// <summary>
        /// Capitoli di un'unione di parti: ogni parte si sposta della durata delle precedenti; al confine prevale il capitolo della parte successiva
        /// </summary>
        /// <param name="parts">Parti nell'ordine dell'unione</param>
        /// <returns>Capitoli nel tempo del file unito</returns>
        public static List<ChapterMark> Concatenate(IList<ChapterPart> parts)
        {
            List<ChapterMark> result = new List<ChapterMark>();
            long offset = 0;

            for (int i = 0; parts != null && i < parts.Count; i++)
            {
                ChapterPart part = parts[i];
                List<ChapterMark> chapters = CloneSorted(part.Chapters);
                bool nextStartsWithChapter = i + 1 < parts.Count && HasChapterAt(parts[i + 1].Chapters, 0);
                bool hasNext = i + 1 < parts.Count;

                if (part.DurationNs <= 0)
                    throw new ArgumentException(AppText.F("chapters.invalidPartDuration", i + 1));

                for (int j = 0; j < chapters.Count; j++)
                {
                    ChapterMark chapter = chapters[j];

                    if (chapter.StartNs < 0)
                        continue;

                    // Un capitolo esattamente sul confine segna l'inizio della parte successiva:
                    // resta solo se quella parte non ne ha gia' uno suo in apertura
                    if (chapter.StartNs >= part.DurationNs)
                    {
                        if (chapter.StartNs != part.DurationNs || !hasNext || nextStartsWithChapter)
                            continue;

                        chapter.EndNs = ChapterMark.NO_END;
                    }
                    else if (chapter.HasEnd)
                    {
                        chapter.EndNs = Math.Min(chapter.EndNs, part.DurationNs);
                    }

                    chapter.StartNs += offset;
                    if (chapter.HasEnd)
                        chapter.EndNs += offset;

                    result.Add(chapter);
                }

                offset += part.DurationNs;
            }

            return result;
        }

        /// <summary>
        /// Capitoli trasformati da una mappa temporale: oltre la durata si eliminano, dentro un tratto rimosso vanno alla giunzione se non coincidono con un altro capitolo.
        /// Prima dell'inizio vengono portati a 0: resta solo l'ultimo, cioè quello in corso all'inizio del risultato, se a 0 non c'è già un capitolo
        /// </summary>
        /// <param name="chapters">Capitoli nel tempo di partenza</param>
        /// <param name="mapper">Mappa da istante di partenza a istante del risultato</param>
        /// <param name="durationNs">Durata del risultato</param>
        /// <returns>Capitoli nel tempo del risultato, ordinati</returns>
        public static List<ChapterMark> Map(List<ChapterMark> chapters, Func<long, ChapterMapResult> mapper, long durationNs)
        {
            List<ChapterMark> mapped = new List<ChapterMark>();
            List<ChapterMark> junctions = new List<ChapterMark>();
            ChapterMark beforeStart = null;
            long beforeStartNs = 0;
            List<ChapterMark> result;

            for (int i = 0; chapters != null && i < chapters.Count; i++)
            {
                ChapterMark chapter = chapters[i].Clone();
                ChapterMapResult start = mapper(chapters[i].StartNs);

                if (start.Kind == ChapterMapKind.Drop || start.TimeNs >= durationNs)
                    continue;

                chapter.StartNs = Math.Max(0, start.TimeNs);
                chapter.EndNs = ChapterMark.NO_END;
                if (chapters[i].HasEnd)
                {
                    ChapterMapResult end = mapper(chapters[i].EndNs);
                    long endNs = end.Kind == ChapterMapKind.Drop ? durationNs : Math.Min(end.TimeNs, durationNs);

                    if (endNs > chapter.StartNs)
                        chapter.EndNs = endNs;
                }

                if (start.TimeNs < 0)
                {
                    // Un capitolo finito prima dell'inizio non copre nulla del risultato
                    if (chapters[i].HasEnd && !chapter.HasEnd)
                        continue;
                    if (beforeStart == null || start.TimeNs >= beforeStartNs)
                    {
                        beforeStart = chapter;
                        beforeStartNs = start.TimeNs;
                    }
                }
                else if (start.Kind == ChapterMapKind.Junction)
                    junctions.Add(chapter);
                else
                    mapped.Add(chapter);
            }

            result = mapped;
            if (beforeStart != null && !HasChapterAt(result, 0))
                result.Add(beforeStart);
            for (int i = 0; i < junctions.Count; i++)
            {
                if (!HasChapterAt(result, junctions[i].StartNs))
                    result.Add(junctions[i]);
            }

            SortByStart(result);
            return result;
        }

        /// <summary>
        /// Serializza i capitoli in XML Matroska di una sola edizione
        /// </summary>
        /// <param name="chapters">Capitoli</param>
        /// <returns>XML per mkvmerge --chapters o mkvpropedit</returns>
        public static string ToXml(List<ChapterMark> chapters)
        {
            StringBuilder builder = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();

            settings.Indent = true;
            settings.Encoding = new UTF8Encoding(false);
            settings.OmitXmlDeclaration = true;

            using (XmlWriter writer = XmlWriter.Create(builder, settings))
            {
                writer.WriteStartElement("Chapters");
                writer.WriteStartElement("EditionEntry");
                for (int i = 0; chapters != null && i < chapters.Count; i++)
                {
                    ChapterMark chapter = chapters[i];

                    writer.WriteStartElement("ChapterAtom");
                    writer.WriteElementString("ChapterTimeStart", MetadataContainerReader.FormatChapterTime(chapter.StartNs));
                    if (chapter.HasEnd)
                        writer.WriteElementString("ChapterTimeEnd", MetadataContainerReader.FormatChapterTime(chapter.EndNs));

                    writer.WriteStartElement("ChapterDisplay");
                    writer.WriteElementString("ChapterString", chapter.Name != null ? chapter.Name : "");
                    if (!string.IsNullOrEmpty(chapter.Language))
                        writer.WriteElementString("ChapterLanguage", chapter.Language);

                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + Environment.NewLine + builder.ToString();
        }

        /// <summary>
        /// Scrive i capitoli in un file XML Matroska
        /// </summary>
        /// <param name="chapters">Capitoli</param>
        /// <param name="filePath">File di destinazione</param>
        public static void WriteXmlFile(List<ChapterMark> chapters, string filePath)
        {
            File.WriteAllText(filePath, ToXml(chapters), new UTF8Encoding(false));
        }

        /// <summary>
        /// Copia indipendente di una lista di capitoli
        /// </summary>
        /// <param name="chapters">Capitoli</param>
        /// <returns>Copia</returns>
        public static List<ChapterMark> Clone(List<ChapterMark> chapters)
        {
            List<ChapterMark> result = new List<ChapterMark>();

            for (int i = 0; chapters != null && i < chapters.Count; i++)
                result.Add(chapters[i].Clone());

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Copia ordinata per inizio
        /// </summary>
        /// <param name="chapters">Capitoli</param>
        /// <returns>Copia ordinata</returns>
        private static List<ChapterMark> CloneSorted(List<ChapterMark> chapters)
        {
            List<ChapterMark> result = Clone(chapters);

            SortByStart(result);
            return result;
        }

        /// <summary>
        /// Ordina per inizio conservando l'ordine dei capitoli con lo stesso inizio
        /// </summary>
        /// <param name="chapters">Capitoli da ordinare</param>
        private static void SortByStart(List<ChapterMark> chapters)
        {
            // OrderBy e' stabile: a parita' di inizio resta l'ordine di partenza
            List<ChapterMark> sorted = chapters.OrderBy(chapter => chapter.StartNs).ToList();

            chapters.Clear();
            chapters.AddRange(sorted);
        }

        /// <summary>
        /// Vero se un capitolo inizia esattamente all'istante dato
        /// </summary>
        /// <param name="chapters">Capitoli</param>
        /// <param name="timeNs">Istante</param>
        /// <returns>Vero se presente</returns>
        private static bool HasChapterAt(List<ChapterMark> chapters, long timeNs)
        {
            for (int i = 0; chapters != null && i < chapters.Count; i++)
            {
                if (chapters[i].StartNs == timeNs)
                    return true;
            }

            return false;
        }

        #endregion
    }
}
