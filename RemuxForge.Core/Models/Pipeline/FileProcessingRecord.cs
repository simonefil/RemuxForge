using RemuxForge.Core.Audio;
using RemuxForge.Core.Localization;
using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Record dei dati di elaborazione per un singolo file
    /// </summary>
    public class FileProcessingRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FileProcessingRecord()
        {
            this.EpisodeId = "";
            this.SourceFileName = "";
            this.SourceSize = 0;
            this.SourceAudioLangs = new List<string>();
            this.SourceSubLangs = new List<string>();
            this.LangFileName = "";
            this.LangSize = 0;
            this.LangAudioLangs = new List<string>();
            this.LangSubLangs = new List<string>();
            this.ResultFileName = "";
            this.ResultSize = 0;
            this.ResultAudioLangs = new List<string>();
            this.ResultSubLangs = new List<string>();
            this.AudioDelayApplied = 0;
            this.SubDelayApplied = 0;
            this.FrameSyncTimeMs = 0;
            this.MergeTimeMs = 0;
            this.SpeedCorrectionTimeMs = 0;
            this.StretchFactor = "";
            this.SpeedCorrectionApplied = false;
            this.Success = false;
            this.SkipReason = "";
            this.Status = FileStatus.Pending;
            this.ManualAudioDelayMs = 0;
            this.ManualSubDelayMs = 0;
            this.AnalysisLog = new List<string>();
            this.ErrorMessage = "";
            this.SourceFilePath = "";
            this.LangFilePath = "";
            this.SyncOffsetMs = 0;
            this.MergeCommand = "";
            this.EncodingProfileName = "";
            this.EncodingTimeMs = 0;
            this.EncodedSize = 0;
            this.EncodingCommand = "";
            this.ResultFilePath = "";
            this.SourceAudioTracks = new List<TrackInfo>();
            this.SourceSubTracks = new List<TrackInfo>();
            this.KeptSourceAudioIds = new List<int>();
            this.KeptSourceSubIds = new List<int>();
            this.ImportedAudioTracks = new List<TrackInfo>();
            this.ImportedSubTracks = new List<TrackInfo>();
            this.DisplayAudioFormat = "";
            this.DeepAnalysisMap = null;
            this.DeepAnalysisTimeMs = 0;
            this.DeepAnalysisApplied = false;
            this.DeepAnalysisMapManuallyEdited = false;
            this.DeepAnalysisResult = null;
            this.DeepAnalysisDiagnosticsPath = "";
            this.FrameSyncResult = null;
            this.AudioProcessingPreview = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Ripulisce tutti i risultati derivati lasciando intatti identità file e delay manuali
        /// </summary>
        public void ResetDerivedState()
        {
            this.SourceAudioLangs.Clear();
            this.SourceSubLangs.Clear();
            this.LangAudioLangs.Clear();
            this.LangSubLangs.Clear();
            this.ResultFileName = "";
            this.ResultSize = 0;
            this.ResultAudioLangs.Clear();
            this.ResultSubLangs.Clear();
            this.AudioDelayApplied = 0;
            this.SubDelayApplied = 0;
            this.FrameSyncTimeMs = 0;
            this.FrameSyncResult = null;
            this.MergeTimeMs = 0;
            this.SpeedCorrectionTimeMs = 0;
            this.StretchFactor = "";
            this.SpeedCorrectionApplied = false;
            this.Success = false;
            this.SkipReason = "";
            this.AnalysisLog.Clear();
            this.ErrorMessage = "";
            this.SyncOffsetMs = 0;
            this.MergeCommand = "";
            this.EncodingProfileName = "";
            this.EncodingTimeMs = 0;
            this.EncodedSize = 0;
            this.EncodingCommand = "";
            this.ResultFilePath = "";
            this.SourceAudioTracks.Clear();
            this.SourceSubTracks.Clear();
            this.KeptSourceAudioIds.Clear();
            this.KeptSourceSubIds.Clear();
            this.ImportedAudioTracks.Clear();
            this.ImportedSubTracks.Clear();
            this.DisplayAudioFormat = "";
            this.DeepAnalysisMap = null;
            this.DeepAnalysisTimeMs = 0;
            this.DeepAnalysisApplied = false;
            this.DeepAnalysisMapManuallyEdited = false;
            this.DeepAnalysisResult = null;
            this.DeepAnalysisDiagnosticsPath = "";
            this.AudioProcessingPreview = null;
        }

        /// <summary>
        /// Riporta in attesa un'analisi interrotta, lasciando invariati gli stati gia' stabili
        /// </summary>
        public void RestoreAfterCancelledAnalysis()
        {
            if (this.Status != FileStatus.Analyzing)
                return;

            this.Status = FileStatus.Pending;
            this.ErrorMessage = "";
        }

        #endregion

        #region Proprietà

        /// <summary>Input del lavoro, preservato da ResetDerivedState; clonare separatamente nell'orchestratore.</summary>
        public RemuxPairTrackSelection ExplicitTrackSelection { get; set; }

        /// <summary>Distingue lo skip derivato dalla selezione da uno skip manuale o di matching.</summary>
        public bool SkippedByTrackSelection { get; private set; }

        /// <summary>Copia detached dell'input e della provenienza dello skip per i clone dell'orchestratore.</summary>
        public void CopyTrackSelectionStateFrom(FileProcessingRecord source)
        {
            this.ExplicitTrackSelection = source.ExplicitTrackSelection?.Clone();
            this.SkippedByTrackSelection = source.SkippedByTrackSelection;
        }

        /// <summary>Usare per lo skip workspace anche quando il record era già saltato dalla selezione.</summary>
        public void SetManualSkip(string reason)
        {
            this.SkippedByTrackSelection = false;
            this.Status = FileStatus.Skipped;
            this.SkipReason = reason;
        }

        public void ApplyTrackSelection(RemuxPairTrackSelection selection)
        {
            this.ExplicitTrackSelection = selection?.Clone();
            this.ApplySelectionSkip(selection != null && !selection.HasLangTracks);
        }

        /// <summary>Eligibility risolta dal Core, valida anche per regole opt-in; preserva gli skip manuali.</summary>
        public void ApplySelectionSkip(bool noLangTracks)
        {
            bool wasSelectionSkip = this.SkippedByTrackSelection;
            // Un record saltato per altri motivi non diventa uno skip derivato e non viene riattivato.
            if (this.Status == FileStatus.Skipped && !wasSelectionSkip) return;
            this.SkippedByTrackSelection = noLangTracks;
            if (this.SkippedByTrackSelection && (this.Status != FileStatus.Skipped || wasSelectionSkip))
            {
                this.ResetDerivedState();
                this.Status = FileStatus.Skipped;
            }
            else if (wasSelectionSkip && !this.SkippedByTrackSelection)
            {
                this.ResetDerivedState();
                this.Status = FileStatus.Pending;
            }
        }

        /// <summary>
        /// Identificatore episodio estratto dal pattern
        /// </summary>
        public string EpisodeId { get; set; }

        /// <summary>
        /// Nome file sorgente
        /// </summary>
        public string SourceFileName { get; set; }

        /// <summary>
        /// Dimensione file sorgente in bytes
        /// </summary>
        public long SourceSize { get; set; }

        /// <summary>
        /// Lingue tracce audio nel file sorgente
        /// </summary>
        public List<string> SourceAudioLangs { get; set; }

        /// <summary>
        /// Lingue tracce sottotitoli nel file sorgente
        /// </summary>
        public List<string> SourceSubLangs { get; set; }

        /// <summary>
        /// Nome file lingua
        /// </summary>
        public string LangFileName { get; set; }

        /// <summary>
        /// Dimensione file lingua in bytes
        /// </summary>
        public long LangSize { get; set; }

        /// <summary>
        /// Lingue tracce audio nel file lingua
        /// </summary>
        public List<string> LangAudioLangs { get; set; }

        /// <summary>
        /// Lingue tracce sottotitoli nel file lingua
        /// </summary>
        public List<string> LangSubLangs { get; set; }

        /// <summary>
        /// Nome file risultato
        /// </summary>
        public string ResultFileName { get; set; }

        /// <summary>
        /// Dimensione file risultato in bytes
        /// </summary>
        public long ResultSize { get; set; }

        /// <summary>
        /// Lingue tracce audio nel file risultato
        /// </summary>
        public List<string> ResultAudioLangs { get; set; }

        /// <summary>
        /// Lingue tracce sottotitoli nel file risultato
        /// </summary>
        public List<string> ResultSubLangs { get; set; }

        /// <summary>
        /// Delay audio applicato in millisecondi
        /// </summary>
        public int AudioDelayApplied { get; set; }

        /// <summary>
        /// Delay sottotitoli applicato in millisecondi
        /// </summary>
        public int SubDelayApplied { get; set; }

        /// <summary>
        /// Tempo di esecuzione Frame Sync in millisecondi
        /// </summary>
        public long FrameSyncTimeMs { get; set; }

        /// <summary>
        /// Risultato dettagliato frame-sync, null se non eseguito
        /// </summary>
        public FrameSyncResult FrameSyncResult { get; set; }

        /// <summary>
        /// Tempo di esecuzione merge mkvmerge in millisecondi
        /// </summary>
        public long MergeTimeMs { get; set; }

        /// <summary>
        /// Indica se l'elaborazione è stata completata con successo
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Tempo di esecuzione correzione velocità in millisecondi
        /// </summary>
        public long SpeedCorrectionTimeMs { get; set; }

        /// <summary>
        /// Fattore di stretch applicato, vuoto se nessuna correzione
        /// </summary>
        public string StretchFactor { get; set; }

        /// <summary>
        /// Indica se la correzione velocità è stata applicata
        /// </summary>
        public bool SpeedCorrectionApplied { get; set; }

        /// <summary>
        /// Motivo dello skip o errore, se applicabile
        /// </summary>
        private string _skipReason;
        public string SkipReason
        {
            get => this.SkippedByTrackSelection ? AppText.T("remuxConfiguration.noLangSelected") : this._skipReason;
            set => this._skipReason = value;
        }

        /// <summary>
        /// Stato corrente del file nel pipeline di elaborazione
        /// </summary>
        public FileStatus Status { get; set; }

        /// <summary>
        /// Override delay audio per-file impostato dall'utente nella UI
        /// </summary>
        public int ManualAudioDelayMs { get; set; }

        /// <summary>
        /// Override delay sottotitoli per-file impostato dall'utente nella UI
        /// </summary>
        public int ManualSubDelayMs { get; set; }

        /// <summary>
        /// Log catturato durante l'analisi del file
        /// </summary>
        public List<string> AnalysisLog { get; set; }

        /// <summary>
        /// Messaggio di errore se l'elaborazione è fallita
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Percorso completo al file sorgente
        /// </summary>
        public string SourceFilePath { get; set; }

        /// <summary>
        /// Percorso completo al file lingua
        /// </summary>
        public string LangFilePath { get; set; }

        /// <summary>
        /// Offset sync auto-calcolato in millisecondi (da speed correction o frame-sync)
        /// </summary>
        public int SyncOffsetMs { get; set; }

        /// <summary>
        /// Comando mkvmerge risultante
        /// </summary>
        public string MergeCommand { get; set; }

        /// <summary>
        /// Nome del profilo di encoding applicato, vuoto se nessuno
        /// </summary>
        public string EncodingProfileName { get; set; }

        /// <summary>
        /// Tempo di esecuzione encoding ffmpeg in millisecondi
        /// </summary>
        public long EncodingTimeMs { get; set; }

        /// <summary>
        /// Dimensione file dopo encoding in bytes
        /// </summary>
        public long EncodedSize { get; set; }

        /// <summary>
        /// Comando ffmpeg di encoding generato
        /// </summary>
        public string EncodingCommand { get; set; }

        /// <summary>
        /// Percorso completo del file risultato
        /// </summary>
        public string ResultFilePath { get; set; }

        /// <summary>
        /// Tracce audio presenti nel file sorgente
        /// </summary>
        public List<TrackInfo> SourceAudioTracks { get; set; }

        /// <summary>
        /// Tracce sottotitoli presenti nel file sorgente
        /// </summary>
        public List<TrackInfo> SourceSubTracks { get; set; }

        /// <summary>
        /// ID tracce audio sorgente mantenute dopo filtro
        /// </summary>
        public List<int> KeptSourceAudioIds { get; set; }

        /// <summary>
        /// ID tracce sottotitoli sorgente mantenute dopo filtro
        /// </summary>
        public List<int> KeptSourceSubIds { get; set; }

        /// <summary>
        /// Tracce audio importate dal file lingua
        /// </summary>
        public List<TrackInfo> ImportedAudioTracks { get; set; }

        /// <summary>
        /// Tracce sottotitoli importate dal file lingua
        /// </summary>
        public List<TrackInfo> ImportedSubTracks { get; set; }

        /// <summary>
        /// Formato conversione audio per display, vuoto se nessuna
        /// </summary>
        public string DisplayAudioFormat { get; set; }

        /// <summary>
        /// EditMap prodotta dalla deep analysis, null se non eseguita
        /// </summary>
        public EditMap DeepAnalysisMap { get; set; }

        /// <summary>
        /// Tempo di esecuzione deep analysis in millisecondi
        /// </summary>
        public long DeepAnalysisTimeMs { get; set; }

        /// <summary>
        /// Indica se la deep analysis è stata eseguita con successo
        /// </summary>
        public bool DeepAnalysisApplied { get; set; }

        /// <summary>
        /// Indica che la EditMap attiva è stata sostituita manualmente dopo lo scan
        /// </summary>
        public bool DeepAnalysisMapManuallyEdited { get; set; }

        /// <summary>
        /// Risultato diagnostico DeepAnalysis, null se la modalità non è stata eseguita
        /// </summary>
        public DeepAnalysisResult DeepAnalysisResult { get; set; }

        /// <summary>
        /// Percorso server-side della diagnostica Deep Analysis dell'episodio
        /// </summary>
        public string DeepAnalysisDiagnosticsPath { get; set; }

        /// <summary>
        /// Piano audio usato da preview dettaglio e dry-run, null se non calcolato
        /// </summary>
        public AudioProcessingPlan AudioProcessingPreview { get; set; }

        #endregion
    }
}
