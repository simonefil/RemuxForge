using RemuxForge.Core.Localization;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Modifiche dirette di un file dai dati del catalogo: tag ai livelli di serie, stagione ed episodio, titolo del segmento e copertina.
    /// Un dato vuoto non produce modifiche e un tag o un titolo già uguale non si riscrive; la copertina, quando c'è, si scrive sempre
    /// </summary>
    public static class MediaCatalogChangeBuilder
    {
        #region Costanti

        /// <summary>
        /// Modello di default del titolo del segmento per le serie
        /// </summary>
        public const string SERIES_TITLE_TEMPLATE = "<Series> - S<Season:2>E<Episode:2> - <Title>";

        /// <summary>
        /// Modello di default del titolo del segmento per i film
        /// </summary>
        public const string MOVIE_TITLE_TEMPLATE = "<Title>";

        /// <summary>
        /// Nome dell'allegato della copertina JPEG
        /// </summary>
        private const string COVER_JPEG_NAME = "cover.jpg";

        /// <summary>
        /// Nome dell'allegato della copertina PNG
        /// </summary>
        private const string COVER_PNG_NAME = "cover.png";

        /// <summary>
        /// Campo del titolo del segmento
        /// </summary>
        private const string SEGMENT_TITLE_FIELD = "container_title";

        /// <summary>
        /// Separatore dei valori multipli: generi e persone
        /// </summary>
        private const string LIST_SEPARATOR = ", ";

        /// <summary>
        /// Separatore delle sinossi di un file con più episodi
        /// </summary>
        private const string SYNOPSIS_SEPARATOR = "\n\n";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Poster da allegare a un file della serie: quello della stagione, altrimenti quello della serie
        /// </summary>
        /// <param name="series">Serie</param>
        /// <param name="episodes">Episodi del file</param>
        /// <returns>Indirizzo del poster, vuoto se il catalogo non ne ha</returns>
        public static string GetCoverUrl(MediaCatalogSeries series, List<MediaCatalogEpisode> episodes)
        {
            foreach (MediaCatalogSeason season in series.Seasons)
            {
                if (season.Number == episodes[0].SeasonNumber && season.PosterUrl.Length > 0)
                    return season.PosterUrl;
            }

            return series.PosterUrl;
        }

        /// <summary>
        /// Modifiche di un file abbinato a uno o più episodi consecutivi; numeri, ID e data sono quelli del primo episodio
        /// </summary>
        /// <param name="fileInfo">Dati attuali del file, con tag e allegati letti</param>
        /// <param name="provider">Catalogo di provenienza</param>
        /// <param name="series">Serie</param>
        /// <param name="episodes">Episodi del file, in ordine</param>
        /// <param name="titleTemplate">Modello del titolo del segmento</param>
        /// <param name="coverSource">File della copertina, vuoto per non scriverla</param>
        /// <returns>Modifiche</returns>
        public static List<MkvMetadataChange> BuildSeriesChanges(MkvMetadataFileInfo fileInfo, MediaCatalogProvider provider, MediaCatalogSeries series, List<MediaCatalogEpisode> episodes, string titleTemplate, string coverSource)
        {
            List<MkvMetadataChange> changes = new List<MkvMetadataChange>();
            string description = AppText.F("metadata.catalog.ruleDescription", series.Title);
            MediaCatalogEpisode first = episodes[0];

            AddSegmentTitle(changes, fileInfo, description, MediaCatalogPlaceholders.Expand(titleTemplate, MediaCatalogPlaceholders.FromEpisodes(series, episodes)));
            AddTag(changes, fileInfo, description, "TITLE", MetadataTagTargetLevels.COLLECTION, series.Title);
            AddTag(changes, fileInfo, description, "TITLE", MetadataTagTargetLevels.EPISODE, string.Join(MediaCatalogPlaceholders.TITLE_SEPARATOR, episodes.Select(episode => episode.Title).Where(title => title.Length > 0)));
            AddTag(changes, fileInfo, description, "SYNOPSIS", MetadataTagTargetLevels.EPISODE, string.Join(SYNOPSIS_SEPARATOR, episodes.Select(episode => episode.Overview).Where(overview => overview.Length > 0)));
            if (provider == MediaCatalogProvider.Tvdb)
            {
                AddTag(changes, fileInfo, description, "TVDB", MetadataTagTargetLevels.COLLECTION, FormatId(series.Id));
                AddTag(changes, fileInfo, description, "TVDB", MetadataTagTargetLevels.EPISODE, FormatId(first.Id));
            }
            else
            {
                AddTag(changes, fileInfo, description, "TMDB", MetadataTagTargetLevels.COLLECTION, series.Id.HasValue ? "tv/" + FormatId(series.Id) : "");
            }

            AddTag(changes, fileInfo, description, "DATE_RELEASED", MetadataTagTargetLevels.EPISODE, FormatDate(first.AiredDate));
            AddTag(changes, fileInfo, description, "PART_NUMBER", MetadataTagTargetLevels.SEASON, first.SeasonNumber.ToString(CultureInfo.InvariantCulture));
            AddTag(changes, fileInfo, description, "PART_NUMBER", MetadataTagTargetLevels.EPISODE, first.EpisodeNumber.ToString(CultureInfo.InvariantCulture));
            AddTag(changes, fileInfo, description, "GENRE", MetadataTagTargetLevels.COLLECTION, string.Join(LIST_SEPARATOR, series.Genres));
            AddTag(changes, fileInfo, description, "DIRECTOR", MetadataTagTargetLevels.EPISODE, JoinPeople(episodes.SelectMany(episode => episode.Directors)));
            AddTag(changes, fileInfo, description, "WRITTEN_BY", MetadataTagTargetLevels.EPISODE, JoinPeople(episodes.SelectMany(episode => episode.Writers)));
            AddTag(changes, fileInfo, description, "ACTOR", MetadataTagTargetLevels.EPISODE, JoinPeople(episodes.SelectMany(episode => episode.Actors)));
            AddCover(changes, fileInfo, description, coverSource);
            return changes;
        }

        /// <summary>
        /// Modifiche di un film
        /// </summary>
        /// <param name="fileInfo">Dati attuali del file, con tag e allegati letti</param>
        /// <param name="provider">Catalogo di provenienza</param>
        /// <param name="movie">Film</param>
        /// <param name="titleTemplate">Modello del titolo del segmento</param>
        /// <param name="coverSource">File della copertina, vuoto per non scriverla</param>
        /// <returns>Modifiche</returns>
        public static List<MkvMetadataChange> BuildMovieChanges(MkvMetadataFileInfo fileInfo, MediaCatalogProvider provider, MediaCatalogMovie movie, string titleTemplate, string coverSource)
        {
            List<MkvMetadataChange> changes = new List<MkvMetadataChange>();
            string description = AppText.F("metadata.catalog.ruleDescription", movie.Title);

            AddSegmentTitle(changes, fileInfo, description, MediaCatalogPlaceholders.Expand(titleTemplate, MediaCatalogPlaceholders.FromMovie(movie)));
            AddTag(changes, fileInfo, description, "TITLE", MetadataTagTargetLevels.EPISODE, movie.Title);
            AddTag(changes, fileInfo, description, "SYNOPSIS", MetadataTagTargetLevels.EPISODE, movie.Overview);
            if (provider == MediaCatalogProvider.Tvdb)
                AddTag(changes, fileInfo, description, "TVDB2", MetadataTagTargetLevels.EPISODE, movie.Id.HasValue ? "movies/" + FormatId(movie.Id) : "");
            else
                AddTag(changes, fileInfo, description, "TMDB", MetadataTagTargetLevels.EPISODE, movie.Id.HasValue ? "movie/" + FormatId(movie.Id) : "");

            AddTag(changes, fileInfo, description, "DATE_RELEASED", MetadataTagTargetLevels.EPISODE, FormatDate(movie.ReleaseDate));
            AddTag(changes, fileInfo, description, "GENRE", MetadataTagTargetLevels.EPISODE, string.Join(LIST_SEPARATOR, movie.Genres));
            AddTag(changes, fileInfo, description, "DIRECTOR", MetadataTagTargetLevels.EPISODE, JoinPeople(movie.Directors));
            AddTag(changes, fileInfo, description, "WRITTEN_BY", MetadataTagTargetLevels.EPISODE, JoinPeople(movie.Writers));
            AddTag(changes, fileInfo, description, "ACTOR", MetadataTagTargetLevels.EPISODE, JoinPeople(movie.Actors));
            AddCover(changes, fileInfo, description, coverSource);
            return changes;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiunge il titolo del segmento
        /// </summary>
        /// <param name="changes">Modifiche</param>
        /// <param name="fileInfo">Dati attuali del file</param>
        /// <param name="description">Descrizione della provenienza</param>
        /// <param name="title">Titolo composto dal modello</param>
        private static void AddSegmentTitle(List<MkvMetadataChange> changes, MkvMetadataFileInfo fileInfo, string description, string title)
        {
            MetadataFieldDefinition field;
            string value = title.Trim();

            if (value.Length == 0 || string.Equals(value, fileInfo.ContainerTitle, StringComparison.Ordinal) || !MetadataFieldRegistry.TryGet(SEGMENT_TITLE_FIELD, out field))
                return;

            MkvMetadataChange change = new MkvMetadataChange();
            change.RuleDescription = description;
            change.Scope = MkvMetadataTargetScope.Container;
            change.OperationType = MkvMetadataOperationType.SetField;
            change.FieldKey = SEGMENT_TITLE_FIELD;
            change.MkvPropEditProperty = field.MkvPropEditProperty;
            change.BeforeValue = fileInfo.ContainerTitle;
            change.AfterValue = value;
            changes.Add(change);
        }

        /// <summary>
        /// Aggiunge un tag di contenitore al livello indicato
        /// </summary>
        /// <param name="changes">Modifiche</param>
        /// <param name="fileInfo">Dati attuali del file</param>
        /// <param name="description">Descrizione della provenienza</param>
        /// <param name="name">Nome del tag</param>
        /// <param name="level">Livello di target</param>
        /// <param name="value">Valore dal catalogo</param>
        private static void AddTag(List<MkvMetadataChange> changes, MkvMetadataFileInfo fileInfo, string description, string name, int level, string value)
        {
            string before;
            Dictionary<string, string> source = level == MetadataTagTargetLevels.EPISODE ? fileInfo.Tags : fileInfo.LeveledTags;
            string key = level == MetadataTagTargetLevels.EPISODE ? name : MetadataTagKey.Build(level, name);
            string after = value != null ? value.Trim() : "";

            if (after.Length == 0)
                return;

            if (source == null || !source.TryGetValue(key, out before))
                before = "";

            if (string.Equals(before, after, StringComparison.Ordinal))
                return;

            MkvMetadataChange change = new MkvMetadataChange();
            change.RuleDescription = description;
            change.Scope = MkvMetadataTargetScope.Container;
            change.OperationType = MkvMetadataOperationType.SetTagField;
            change.FieldKey = name;
            change.TagTargetTypeValue = level;
            change.BeforeValue = before;
            change.AfterValue = after;
            changes.Add(change);
        }

        /// <summary>
        /// Aggiunge la copertina come cover.jpg o cover.png secondo il formato, sostituendo quella che il file ha già anche se di formato diverso
        /// </summary>
        /// <param name="changes">Modifiche</param>
        /// <param name="fileInfo">Dati attuali del file</param>
        /// <param name="description">Descrizione della provenienza</param>
        /// <param name="coverSource">File o indirizzo della copertina, vuoto per non scriverla</param>
        private static void AddCover(List<MkvMetadataChange> changes, MkvMetadataFileInfo fileInfo, string description, string coverSource)
        {
            MkvMetadataAttachmentInfo existing = null;

            if (string.IsNullOrEmpty(coverSource))
                return;

            // Nell'anteprima la sorgente è l'indirizzo del poster, nella scrittura il file scaricato: in entrambi l'estensione dice il formato
            string name = string.Equals(Path.GetExtension(coverSource.Split('?')[0]), ".png", StringComparison.OrdinalIgnoreCase) ? COVER_PNG_NAME : COVER_JPEG_NAME;
            for (int i = 0; fileInfo.Attachments != null && i < fileInfo.Attachments.Count; i++)
            {
                string fileName = fileInfo.Attachments[i].FileName;
                if (string.Equals(fileName, COVER_JPEG_NAME, StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, COVER_PNG_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    existing = fileInfo.Attachments[i];
                    break;
                }
            }

            MkvMetadataChange change = new MkvMetadataChange();
            change.RuleDescription = description;
            change.Scope = MkvMetadataTargetScope.Container;
            change.OperationType = MkvMetadataOperationType.SetAttachment;
            change.BeforeValue = existing != null ? existing.FileName : "";
            change.AfterValue = name;
            change.AttachmentName = name;
            change.AttachmentSourcePath = coverSource;
            change.AttachmentMimeType = MetadataContainerReader.GuessMimeType(name);
            changes.Add(change);
        }

        /// <summary>
        /// Persone senza doppioni, nell'ordine dei crediti
        /// </summary>
        /// <param name="people">Persone</param>
        /// <returns>Elenco separato da virgole</returns>
        private static string JoinPeople(IEnumerable<string> people)
        {
            return string.Join(LIST_SEPARATOR, people.Where(person => !string.IsNullOrWhiteSpace(person)).Select(person => person.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ID numerico in forma invariante, vuoto se assente
        /// </summary>
        /// <param name="id">ID</param>
        /// <returns>Testo</returns>
        private static string FormatId(long? id)
        {
            return id.HasValue ? id.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        /// <summary>
        /// Data nel formato dei tag Matroska, vuota se assente
        /// </summary>
        /// <param name="date">Data</param>
        /// <returns>Testo AAAA-MM-GG</returns>
        private static string FormatDate(DateTime? date)
        {
            return date.HasValue ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        }

        #endregion
    }
}
