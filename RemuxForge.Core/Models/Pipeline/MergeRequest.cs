using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Raggruppa tutti i parametri per la costruzione del comando mkvmerge
    /// </summary>
    public class MergeRequest
    {
        #region Costruttore

        /// <summary>
        /// Costruttore: inizializza tutti i tipi riferimento
        /// </summary>
        public MergeRequest()
        {
            this.SourceFile = "";
            this.LanguageFile = "";
            this.OutputFile = "";
            this.SourceAudioIds = new List<int>();
            this.SourceAudioTracks = new List<TrackInfo>();
            this.SourceSubIds = new List<int>();
            this.LangAudioTracks = new List<TrackInfo>();
            this.LangSubTracks = new List<TrackInfo>();
            this.SubtitleStretchFactor = "";
            this.AudioFormat = "";
            this.ConvertedSourceTracks = new Dictionary<int, string>();
            this.ConvertedLangTracks = new Dictionary<int, string>();
            this.RequiredProcessedLangTrackIds = new HashSet<int>();
            this.ProcessedLangSubTracks = new Dictionary<int, string>();
            this.AudioDelayBypassedLangIds = new HashSet<int>();
            this.ProcessedSourceAudioInfo = new Dictionary<int, TrackInfo>();
            this.ProcessedLangAudioInfo = new Dictionary<int, TrackInfo>();
            this.SourceTitle = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso file sorgente
        /// </summary>
        public string SourceFile { get; set; }

        /// <summary>
        /// Percorso file lingua
        /// </summary>
        public string LanguageFile { get; set; }

        /// <summary>
        /// Percorso file output
        /// </summary>
        public string OutputFile { get; set; }

        /// <summary>
        /// ID tracce audio sorgente da mantenere
        /// </summary>
        public List<int> SourceAudioIds { get; set; }

        /// <summary>
        /// Tracce audio sorgente con metadati completi
        /// </summary>
        public List<TrackInfo> SourceAudioTracks { get; set; }

        /// <summary>
        /// ID tracce sottotitoli sorgente da mantenere
        /// </summary>
        public List<int> SourceSubIds { get; set; }

        /// <summary>
        /// Tracce audio dal file lingua
        /// </summary>
        public List<TrackInfo> LangAudioTracks { get; set; }

        /// <summary>
        /// Tracce sottotitoli dal file lingua
        /// </summary>
        public List<TrackInfo> LangSubTracks { get; set; }

        /// <summary>
        /// Ritardo audio in millisecondi
        /// </summary>
        public int AudioDelayMs { get; set; }

        /// <summary>
        /// Ritardo sottotitoli in millisecondi
        /// </summary>
        public int SubDelayMs { get; set; }

        /// <summary>
        /// Se filtrare le tracce audio sorgente
        /// </summary>
        public bool FilterSourceAudio { get; set; }

        /// <summary>
        /// Se filtrare le tracce sottotitoli sorgente
        /// </summary>
        public bool FilterSourceSubs { get; set; }

        /// <summary>
        /// Fattore di stretch temporale applicato esclusivamente ai sottotitoli tramite mkvmerge
        /// </summary>
        public string SubtitleStretchFactor { get; set; }

        /// <summary>
        /// Formato audio processato o stringa vuota se nessuna conversione
        /// </summary>
        public string AudioFormat { get; set; }

        /// <summary>
        /// Mappa trackId sorgente -> percorso file audio convertito. Le tracce in questa mappa
        /// vengono aggiunte come input separati in mkvmerge al posto della traccia originale
        /// </summary>
        public Dictionary<int, string> ConvertedSourceTracks { get; set; }

        /// <summary>
        /// Mappa trackId lingua -> percorso file audio convertito. Le tracce in questa mappa
        /// vengono aggiunte come input separati in mkvmerge al posto della traccia originale
        /// </summary>
        public Dictionary<int, string> ConvertedLangTracks { get; set; }

        /// <summary>
        /// ID tracce Language che devono essere sostituite da un output FFmpeg
        /// </summary>
        public HashSet<int> RequiredProcessedLangTrackIds { get; set; }

        /// <summary>
        /// Mappa trackId lingua sub -> percorso file sub processato dalla timeline edit o dal canvas rewrite
        /// Le tracce in questa mappa vengono aggiunte come input separati in mkvmerge
        /// </summary>
        public Dictionary<int, string> ProcessedLangSubTracks { get; set; }

        /// <summary>
        /// ID tracce lang per cui il delay audio è stato sostituito da audio source fill
        /// </summary>
        public HashSet<int> AudioDelayBypassedLangIds { get; set; }

        /// <summary>
        /// Info effettive dei file audio sorgente processati
        /// </summary>
        public Dictionary<int, TrackInfo> ProcessedSourceAudioInfo { get; set; }

        /// <summary>
        /// Info effettive dei file audio lang processati
        /// </summary>
        public Dictionary<int, TrackInfo> ProcessedLangAudioInfo { get; set; }

        /// <summary>
        /// Titolo segmento del file sorgente (container title), stringa vuota se assente
        /// </summary>
        public string SourceTitle { get; set; }

        #endregion
    }
}
