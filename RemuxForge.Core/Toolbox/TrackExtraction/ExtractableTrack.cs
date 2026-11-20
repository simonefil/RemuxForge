using RemuxForge.Core.Localization;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.TrackExtraction
{
    /// <summary>
    /// Tipo di una traccia nell'estrazione
    /// </summary>
    public enum TrackExtractionKind
    {
        /// <summary>Traccia video, mostrata ma non estraibile</summary>
        Video,

        /// <summary>Traccia audio</summary>
        Audio,

        /// <summary>Traccia di sottotitoli</summary>
        Subtitles,

        /// <summary>Traccia di un altro tipo, ad esempio i pulsanti dei menu, mostrata ma non estraibile</summary>
        Other
    }

    /// <summary>
    /// Traccia di un file, con il numero nella numerazione dello strumento che la estrae: l'ID di mkvmerge per i Matroska,
    /// l'indice dello stream di ffmpeg per gli altri contenitori
    /// </summary>
    public sealed class ExtractableTrack
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="number">Numero della traccia per mkvextract o ffmpeg</param>
        /// <param name="kind">Tipo della traccia</param>
        /// <param name="type">Tipo come lo indica lo strumento che legge il file</param>
        /// <param name="codec">Codec come lo descrive lo strumento che legge il file</param>
        /// <param name="codecId">Codec ID Matroska o nome del codec di ffmpeg</param>
        /// <param name="format">Formato di estrazione, null se il codec non si estrae</param>
        /// <param name="language">Lingua ISO 639-2, und se il file non la indica</param>
        /// <param name="name">Nome della traccia, vuoto se manca</param>
        /// <param name="isDefault">True se la traccia è predefinita</param>
        /// <param name="isForced">True se la traccia è forzata</param>
        public ExtractableTrack(int number, TrackExtractionKind kind, string type, string codec, string codecId, TrackExtractionFormat format, string language, string name, bool isDefault, bool isForced)
        {
            this.Number = number;
            this.Kind = kind;
            this.Type = type;
            this.Codec = codec;
            this.CodecId = codecId;
            this.Format = kind == TrackExtractionKind.Audio || kind == TrackExtractionKind.Subtitles ? format : null;
            this.Language = language;
            this.Name = name;
            this.IsDefault = isDefault;
            this.IsForced = isForced;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Numero della traccia per lo strumento che la estrae
        /// </summary>
        public int Number { get; }

        /// <summary>
        /// Tipo della traccia
        /// </summary>
        public TrackExtractionKind Kind { get; }

        /// <summary>
        /// Tipo come lo indica mkvmerge o ffprobe
        /// </summary>
        public string Type { get; }

        /// <summary>
        /// Codec come lo descrive mkvmerge o ffprobe
        /// </summary>
        public string Codec { get; }

        /// <summary>
        /// Codec ID Matroska per mkvmerge, nome del codec per ffprobe
        /// </summary>
        public string CodecId { get; }

        /// <summary>
        /// Formato di estrazione, null se la traccia non si estrae
        /// </summary>
        public TrackExtractionFormat Format { get; }

        /// <summary>
        /// Lingua ISO 639-2, und se il file non la indica; è anche la lingua del nome del file estratto
        /// </summary>
        public string Language { get; }

        /// <summary>
        /// Nome della traccia, vuoto se manca
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// True se la traccia è predefinita
        /// </summary>
        public bool IsDefault { get; }

        /// <summary>
        /// True se la traccia è forzata
        /// </summary>
        public bool IsForced { get; }

        /// <summary>
        /// Perché la traccia non si estrae, vuoto se si estrae
        /// </summary>
        public string UnavailableReason
        {
            get
            {
                return this.Kind switch
                {
                    TrackExtractionKind.Video => AppText.T("toolbox.trackExtraction.videoNotExtracted"),
                    TrackExtractionKind.Other => AppText.F("toolbox.trackExtraction.kindNotExtracted", this.Type),
                    _ => this.Format == null ? AppText.F("toolbox.trackExtraction.formatNotExtracted", this.CodecId.Length > 0 ? this.CodecId : this.Codec) : ""
                };
            }
        }

        /// <summary>
        /// True per le tracce audio e di sottotitoli in un formato che si estrae
        /// </summary>
        public bool IsExtractable
        {
            get { return this.Format != null; }
        }

        #endregion
    }

    /// <summary>
    /// File letto per l'estrazione: le sue tracce e la durata che serve all'avanzamento di ffmpeg
    /// </summary>
    public sealed class TrackExtractionSource
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="path">Percorso completo del file</param>
        /// <param name="isMatroska">True se le tracce si estraggono con mkvextract</param>
        /// <param name="durationSeconds">Durata del file, 0 se non nota</param>
        /// <param name="tracks">Tracce nell'ordine del file</param>
        public TrackExtractionSource(string path, bool isMatroska, double durationSeconds, List<ExtractableTrack> tracks)
        {
            this.Path = path;
            this.IsMatroska = isMatroska;
            this.DurationSeconds = durationSeconds;
            this.Tracks = tracks;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// True se le tracce si estraggono con mkvextract, false con ffmpeg
        /// </summary>
        public bool IsMatroska { get; }

        /// <summary>
        /// Durata del file in secondi, 0 se non nota
        /// </summary>
        public double DurationSeconds { get; }

        /// <summary>
        /// Tracce nell'ordine del file, video comprese
        /// </summary>
        public IReadOnlyList<ExtractableTrack> Tracks { get; }

        #endregion
    }
}
