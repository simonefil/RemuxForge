using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Audio
{
    /// <summary>
    /// Piano audio calcolato prima del render ffmpeg
    /// </summary>
    public class AudioProcessingPlan
    {
        #region Costruttore

        /// <summary>
        /// Costruttore piano audio
        /// </summary>
        public AudioProcessingPlan()
        {
            this.SourceTracks = new List<AudioTrackProcessingPlan>();
            this.LangTracks = new List<AudioTrackProcessingPlan>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Cerca il piano di una traccia source
        /// </summary>
        /// <param name="trackId">ID traccia source</param>
        /// <returns>Piano traccia o null</returns>
        public AudioTrackProcessingPlan FindSourceTrack(int trackId)
        {
            return this.FindTrack(this.SourceTracks, trackId);
        }

        /// <summary>
        /// Cerca il piano di una traccia language
        /// </summary>
        /// <param name="trackId">ID traccia language</param>
        /// <returns>Piano traccia o null</returns>
        public AudioTrackProcessingPlan FindLangTrack(int trackId)
        {
            return this.FindTrack(this.LangTracks, trackId);
        }

        /// <summary>
        /// Restituisce tutte le tracce pianificate in ordine source, poi language
        /// </summary>
        /// <returns>Lista piani traccia</returns>
        public List<AudioTrackProcessingPlan> GetAllTracks()
        {
            List<AudioTrackProcessingPlan> result = new List<AudioTrackProcessingPlan>();
            result.AddRange(this.SourceTracks);
            result.AddRange(this.LangTracks);
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Cerca una traccia in una lista di piani
        /// </summary>
        /// <param name="tracks">Lista piani</param>
        /// <param name="trackId">ID traccia da cercare</param>
        /// <returns>Piano traccia o null</returns>
        private AudioTrackProcessingPlan FindTrack(List<AudioTrackProcessingPlan> tracks, int trackId)
        {
            AudioTrackProcessingPlan result = null;

            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Track != null && tracks[i].Track.Id == trackId)
                {
                    result = tracks[i];
                    break;
                }
            }

            return result;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Piani delle tracce source
        /// </summary>
        public List<AudioTrackProcessingPlan> SourceTracks { get; set; }

        /// <summary>
        /// Piani delle tracce language
        /// </summary>
        public List<AudioTrackProcessingPlan> LangTracks { get; set; }

        #endregion
    }

    /// <summary>
    /// Piano operativo di una singola traccia audio
    /// </summary>
    public class AudioTrackProcessingPlan
    {
        #region Costruttore

        /// <summary>
        /// Costruttore piano traccia audio
        /// </summary>
        public AudioTrackProcessingPlan()
        {
            this.Track = null;
            this.SourceFillTrack = null;
            this.SourceFillPlan = null;
            this.StretchFactor = "";
            this.StretchRatio = 1.0;
            this.AudioTempo = 1.0;
            this.AudioTempoFilter = "";
            this.InitialTimelineOffsetMs = 0;
            this.ErrorMessage = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se la traccia arriva dal file source
        /// </summary>
        public bool IsSource { get; set; }

        /// <summary>
        /// Traccia audio pianificata
        /// </summary>
        public TrackInfo Track { get; set; }

        /// <summary>
        /// True se la traccia rientra nello scope audio generico
        /// </summary>
        public bool GenericProcessing { get; set; }

        /// <summary>
        /// True se il processing generico richiede un render
        /// </summary>
        public bool GenericRenderRequired { get; set; }

        /// <summary>
        /// True se Speed Correction o DeepAnalysis impongono il render Language
        /// </summary>
        public bool TimelinePolicyRenderRequired { get; set; }

        /// <summary>
        /// True se il render deve materializzare lo stretch nei campioni
        /// </summary>
        public bool StretchRender { get; set; }

        /// <summary>
        /// Fattore stretch logico originale
        /// </summary>
        public string StretchFactor { get; set; }

        /// <summary>
        /// Moltiplicatore della durata finale
        /// </summary>
        public double StretchRatio { get; set; }

        /// <summary>
        /// Moltiplicatore di velocità usato da FFmpeg
        /// </summary>
        public double AudioTempo { get; set; }

        /// <summary>
        /// Catena atempo FFmpeg risolta
        /// </summary>
        public string AudioTempoFilter { get; set; }

        /// <summary>
        /// Origine temporale iniziale della traccia nel container, in millisecondi nel dominio nativo
        /// </summary>
        public int InitialTimelineOffsetMs { get; set; }

        /// <summary>
        /// True se la traccia language deve materializzare la EditMap
        /// </summary>
        public bool DeepEditRender { get; set; }

        /// <summary>
        /// True se source-fill è configurato per questa traccia
        /// </summary>
        public bool SourceFillConfigured { get; set; }

        /// <summary>
        /// True se il piano source-fill contiene lavoro
        /// </summary>
        public bool SourceFillHasWork { get; set; }

        /// <summary>
        /// True se il source-fill usa davvero segmenti source
        /// </summary>
        public bool ActualSourceFill { get; set; }

        /// <summary>
        /// True se la traccia deve produrre un file temporaneo processato
        /// </summary>
        public bool RenderRequired { get; set; }

        /// <summary>
        /// True se il render incorpora delay o stretch esterno
        /// </summary>
        public bool BypassAudioDelay { get; set; }

        /// <summary>
        /// Traccia source scelta per source-fill
        /// </summary>
        public TrackInfo SourceFillTrack { get; set; }

        /// <summary>
        /// Dettaglio operativo source-fill
        /// </summary>
        public AudioSourceFillPlan SourceFillPlan { get; set; }

        /// <summary>
        /// Errore determinabile già in fase preview
        /// </summary>
        public string ErrorMessage { get; set; }

        #endregion
    }

    /// <summary>
    /// Piano operativo per riempire porzioni audio language dal source
    /// </summary>
    public class AudioSourceFillPlan
    {
        #region Costruttore

        /// <summary>
        /// Costruttore piano source fill
        /// </summary>
        public AudioSourceFillPlan()
        {
            this.InsertOperations = new List<EditOperation>();
            this.SourceFilledOperations = new List<EditOperation>();
            this.StretchRatio = 1.0;
            this.LangTempo = 1.0;
            this.SourceInitialTimelineOffsetMs = 0;
            this.LangInitialTimelineOffsetMs = 0;
            this.InitialSilenceMs = 0;
            this.InitialTrimMs = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Durata da riempire all'inizio in millisecondi
        /// </summary>
        public int StartFillMs { get; set; }

        /// <summary>
        /// Durata da riempire alla fine in millisecondi
        /// </summary>
        public int EndFillMs { get; set; }

        /// <summary>
        /// Durata video Source autorevole per il fill di coda
        /// </summary>
        public int SourceDurationMs { get; set; }

        /// <summary>
        /// Fattore stretch da materializzare sui segmenti language
        /// </summary>
        public double StretchRatio { get; set; }

        /// <summary>
        /// Tempo ffmpeg corrispondente allo stretch richiesto
        /// </summary>
        public double LangTempo { get; set; }

        /// <summary>
        /// Origine della traccia source rispetto al video source, in millisecondi
        /// </summary>
        public int SourceInitialTimelineOffsetMs { get; set; }

        /// <summary>
        /// Origine della traccia language rispetto al video language, in millisecondi
        /// </summary>
        public int LangInitialTimelineOffsetMs { get; set; }

        /// <summary>
        /// Silenzio iniziale da materializzare quando il sync esterno viene bypassato
        /// </summary>
        public int InitialSilenceMs { get; set; }

        /// <summary>
        /// Trim iniziale da materializzare quando il sync esterno negativo viene bypassato
        /// </summary>
        public int InitialTrimMs { get; set; }

        /// <summary>
        /// Operazioni insert silence da sostituire con audio source
        /// </summary>
        public List<EditOperation> InsertOperations { get; set; }

        /// <summary>
        /// Sottoinsieme delle operazioni che vanno riempite davvero con audio source
        /// </summary>
        public List<EditOperation> SourceFilledOperations { get; set; }

        /// <summary>
        /// True se il piano contiene almeno una operazione
        /// </summary>
        public bool HasWork
        {
            get { return this.StartFillMs > 0 || this.EndFillMs > 0 || this.InsertOperations.Count > 0; }
        }

        /// <summary>
        /// True se il render incorpora delay/stretch e il merge non deve riapplicare --sync
        /// </summary>
        public bool MaterializesExternalSync
        {
            get { return this.InitialSilenceMs > 0 || this.InitialTrimMs > 0 || Math.Abs(this.StretchRatio - 1.0) > 0.0001; }
        }

        #endregion
    }
}
