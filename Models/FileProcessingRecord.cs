using System;
using System.Collections.Generic;
using System.Text;

namespace MergeLanguageTracks
{
    /// <summary>
    /// Record dei dati di elaborazione per un singolo file.
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
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Formatta la dimensione file in formato leggibile.
        /// </summary>
        /// <param name="bytes">Dimensione in bytes.</param>
        /// <returns>Stringa formattata (es. "1.5 GB").</returns>
        public static string FormatSize(long bytes)
        {
            string result = "";

            if (bytes >= 1073741824)
            {
                result = Math.Round(bytes / 1073741824.0, 2) + " GB";
            }
            else if (bytes >= 1048576)
            {
                result = Math.Round(bytes / 1048576.0, 1) + " MB";
            }
            else if (bytes >= 1024)
            {
                result = Math.Round(bytes / 1024.0, 1) + " KB";
            }
            else
            {
                result = bytes + " B";
            }

            return result;
        }

        /// <summary>
        /// Formatta una lista di lingue come stringa.
        /// </summary>
        /// <param name="langs">Lista di codici lingua.</param>
        /// <returns>Stringa formattata (es. "eng,ita,jpn").</returns>
        public static string FormatLangs(List<string> langs)
        {
            string result = "-";

            if (langs != null && langs.Count > 0)
            {
                result = string.Join(",", langs);
            }

            return result;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Identificatore episodio estratto dal pattern.
        /// </summary>
        public string EpisodeId { get; set; }

        /// <summary>
        /// Nome file sorgente.
        /// </summary>
        public string SourceFileName { get; set; }

        /// <summary>
        /// Dimensione file sorgente in bytes.
        /// </summary>
        public long SourceSize { get; set; }

        /// <summary>
        /// Lingue tracce audio nel file sorgente.
        /// </summary>
        public List<string> SourceAudioLangs { get; set; }

        /// <summary>
        /// Lingue tracce sottotitoli nel file sorgente.
        /// </summary>
        public List<string> SourceSubLangs { get; set; }

        /// <summary>
        /// Nome file lingua.
        /// </summary>
        public string LangFileName { get; set; }

        /// <summary>
        /// Dimensione file lingua in bytes.
        /// </summary>
        public long LangSize { get; set; }

        /// <summary>
        /// Lingue tracce audio nel file lingua.
        /// </summary>
        public List<string> LangAudioLangs { get; set; }

        /// <summary>
        /// Lingue tracce sottotitoli nel file lingua.
        /// </summary>
        public List<string> LangSubLangs { get; set; }

        /// <summary>
        /// Nome file risultato.
        /// </summary>
        public string ResultFileName { get; set; }

        /// <summary>
        /// Dimensione file risultato in bytes.
        /// </summary>
        public long ResultSize { get; set; }

        /// <summary>
        /// Lingue tracce audio nel file risultato.
        /// </summary>
        public List<string> ResultAudioLangs { get; set; }

        /// <summary>
        /// Lingue tracce sottotitoli nel file risultato.
        /// </summary>
        public List<string> ResultSubLangs { get; set; }

        /// <summary>
        /// Delay audio applicato in millisecondi.
        /// </summary>
        public int AudioDelayApplied { get; set; }

        /// <summary>
        /// Delay sottotitoli applicato in millisecondi.
        /// </summary>
        public int SubDelayApplied { get; set; }

        /// <summary>
        /// Tempo di esecuzione Frame Sync in millisecondi.
        /// </summary>
        public long FrameSyncTimeMs { get; set; }

        /// <summary>
        /// Tempo di esecuzione merge mkvmerge in millisecondi.
        /// </summary>
        public long MergeTimeMs { get; set; }

        /// <summary>
        /// Indica se l'elaborazione e' stata completata con successo.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Tempo di esecuzione correzione velocita' in millisecondi.
        /// </summary>
        public long SpeedCorrectionTimeMs { get; set; }

        /// <summary>
        /// Fattore di stretch applicato, vuoto se nessuna correzione.
        /// </summary>
        public string StretchFactor { get; set; }

        /// <summary>
        /// Indica se la correzione velocita' e' stata applicata.
        /// </summary>
        public bool SpeedCorrectionApplied { get; set; }

        /// <summary>
        /// Motivo dello skip o errore, se applicabile.
        /// </summary>
        public string SkipReason { get; set; }

        /// <summary>
        /// Stato corrente del file nel pipeline di elaborazione
        /// </summary>
        public FileStatus Status { get; set; }

        /// <summary>
        /// Override delay audio per-file impostato dall'utente nella TUI
        /// </summary>
        public int ManualAudioDelayMs { get; set; }

        /// <summary>
        /// Override delay sottotitoli per-file impostato dall'utente nella TUI
        /// </summary>
        public int ManualSubDelayMs { get; set; }

        /// <summary>
        /// Log catturato durante l'analisi del file
        /// </summary>
        public List<string> AnalysisLog { get; set; }

        /// <summary>
        /// Messaggio di errore se l'elaborazione e' fallita
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

        #endregion
    }
}
