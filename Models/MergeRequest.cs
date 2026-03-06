using System.Collections.Generic;

namespace MergeLanguageTracks
{
    /// <summary>
    /// Raggruppa tutti i parametri per la costruzione del comando mkvmerge
    /// </summary>
    public class MergeRequest
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso file sorgente
        /// </summary>
        public string SourceFile;

        /// <summary>
        /// Percorso file lingua
        /// </summary>
        public string LanguageFile;

        /// <summary>
        /// Percorso file output
        /// </summary>
        public string OutputFile;

        /// <summary>
        /// ID tracce audio sorgente da mantenere
        /// </summary>
        public List<int> SourceAudioIds;

        /// <summary>
        /// ID tracce sottotitoli sorgente da mantenere
        /// </summary>
        public List<int> SourceSubIds;

        /// <summary>
        /// Tracce audio dal file lingua
        /// </summary>
        public List<TrackInfo> LangAudioTracks;

        /// <summary>
        /// Tracce sottotitoli dal file lingua
        /// </summary>
        public List<TrackInfo> LangSubTracks;

        /// <summary>
        /// Ritardo audio in millisecondi
        /// </summary>
        public int AudioDelayMs;

        /// <summary>
        /// Ritardo sottotitoli in millisecondi
        /// </summary>
        public int SubDelayMs;

        /// <summary>
        /// Se filtrare le tracce audio sorgente
        /// </summary>
        public bool FilterSourceAudio;

        /// <summary>
        /// Se filtrare le tracce sottotitoli sorgente
        /// </summary>
        public bool FilterSourceSubs;

        /// <summary>
        /// Fattore di stretch temporale per mkvmerge --sync
        /// </summary>
        public string StretchFactor;

        #endregion
    }
}
