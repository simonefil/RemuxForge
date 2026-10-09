using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Ancora visuale da confrontare con SIFT, con PTS e frame preprocessato
    /// </summary>
    public class DeepSiftVisualAnchor
    {
        #region Costruttore

        /// <summary>
        /// Inizializza un'ancora visuale con un buffer frame vuoto
        /// </summary>
        public DeepSiftVisualAnchor()
        {
            this.Frame = Array.Empty<byte>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Indice ordinato dell'ancora nella relativa timeline
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Indice del frame originale usato soltanto per diagnostica
        /// </summary>
        public int FrameIndex { get; set; }

        /// <summary>
        /// PTS dell'ancora in millisecondi
        /// </summary>
        public double PtsMs { get; set; }

        /// <summary>
        /// Durata PTS rappresentata dall'ancora in millisecondi
        /// </summary>
        public double DurationMs { get; set; }

        /// <summary>
        /// Durata del frame originale, distinta dalla durata temporale rappresentata
        /// </summary>
        public double FrameDurationMs { get; set; }

        /// <summary>
        /// Buffer grayscale row-major già normalizzato per SIFT
        /// </summary>
        [JsonIgnore]
        public byte[] Frame { get; set; }

        /// <summary>
        /// Larghezza del buffer grayscale
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Altezza del buffer grayscale
        /// </summary>
        public int Height { get; set; }

        #endregion
    }

    /// <summary>
    /// Stato di una cella della matrice visuale globale
    /// </summary>
    public enum DeepSiftMatchState : byte
    {
        /// <summary>
        /// Coppia rifiutata dai criteri geometrici
        /// </summary>
        Rejected = 0,

        /// <summary>
        /// Coppia accettata dai criteri geometrici
        /// </summary>
        Accepted = 1
    }

    /// <summary>
    /// Cella compatta della matrice SIFT backend-neutral
    /// </summary>
    public struct DeepSiftMatchCell
    {
        #region Variabili di classe

        /// <summary>
        /// Score SIFT memorizzato in precisione singola
        /// </summary>
        private float _score;

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato della prova visuale
        /// </summary>
        public DeepSiftMatchState State { get; set; }

        /// <summary>
        /// Score SIFT monotono usato dal solver
        /// </summary>
        public double Score
        {
            get { return this._score; }
            set { this._score = (float)value; }
        }

        #endregion
    }

    /// <summary>
    /// Matrice locale dei risultati SIFT del batch corrente
    /// </summary>
    public class DeepSiftMatchMatrix
    {
        #region Variabili di classe

        /// <summary>
        /// Celle della matrice disposte per riga source e colonna language
        /// </summary>
        private readonly DeepSiftMatchCell[] _cells;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruisce una matrice le cui celle sono inizialmente rifiutate
        /// </summary>
        /// <param name="sourceCount">Numero di ancore source</param>
        /// <param name="languageCount">Numero di ancore language</param>
        public DeepSiftMatchMatrix(int sourceCount, int languageCount)
        {
            if (sourceCount < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceCount));
            if (languageCount < 0)
                throw new ArgumentOutOfRangeException(nameof(languageCount));

            this.SourceCount = sourceCount;
            this.LanguageCount = languageCount;
            this._cells = new DeepSiftMatchCell[checked(sourceCount * languageCount)];
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Recupera la cella indicizzata per riga source e colonna language
        /// </summary>
        /// <param name="sourceIndex">Indice della riga source</param>
        /// <param name="languageIndex">Indice della colonna language</param>
        /// <returns>Cella memorizzata nella posizione richiesta</returns>
        public DeepSiftMatchCell Get(int sourceIndex, int languageIndex)
        {
            this.ValidateIndexes(sourceIndex, languageIndex);
            return this._cells[(sourceIndex * this.LanguageCount) + languageIndex];
        }

        /// <summary>
        /// Imposta la cella indicizzata per riga source e colonna language
        /// </summary>
        /// <param name="sourceIndex">Indice della riga source</param>
        /// <param name="languageIndex">Indice della colonna language</param>
        /// <param name="cell">Cella da memorizzare nella posizione richiesta</param>
        public void Set(int sourceIndex, int languageIndex, DeepSiftMatchCell cell)
        {
            this.ValidateIndexes(sourceIndex, languageIndex);
            this._cells[(sourceIndex * this.LanguageCount) + languageIndex] = cell;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Numero di righe source
        /// </summary>
        public int SourceCount { get; }

        /// <summary>
        /// Numero di colonne language
        /// </summary>
        public int LanguageCount { get; }

        /// <summary>
        /// Numero di celle accettate
        /// </summary>
        public int AcceptedCellCount { get; internal set; }

        /// <summary>
        /// Numero di celle elaborate
        /// </summary>
        public long ProcessedCellCount { get; internal set; }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Verifica gli indici della matrice
        /// </summary>
        /// <param name="sourceIndex">Indice della riga source da verificare</param>
        /// <param name="languageIndex">Indice della colonna language da verificare</param>
        private void ValidateIndexes(int sourceIndex, int languageIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= this.SourceCount)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex));
            if (languageIndex < 0 || languageIndex >= this.LanguageCount)
                throw new ArgumentOutOfRangeException(nameof(languageIndex));
        }

        #endregion
    }

    /// <summary>
    /// Risultato backend-neutral del matching SIFT con evidenze e metriche diagnostiche
    /// </summary>
    public class DeepSiftBatchMatchResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza il risultato con le collezioni e le stringhe vuote predefinite
        /// </summary>
        public DeepSiftBatchMatchResult()
        {
            this.BackendName = "";
            this.RejectReason = "";
            this.AcceptedPairs = new List<DeepSiftAcceptedPairDiagnostic>();
            this.RejectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome backend
        /// </summary>
        public string BackendName { get; set; }

        /// <summary>
        /// Matrice completa prodotta dal backend
        /// </summary>
        [JsonIgnore]
        public DeepSiftMatchMatrix Matrix { get; set; }

        /// <summary>
        /// Motivo del rifiuto del batch
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Numero di ancore source elaborate
        /// </summary>
        public int SourceAnchorCount { get; set; }

        /// <summary>
        /// Numero di ancore language elaborate
        /// </summary>
        public int LanguageAnchorCount { get; set; }

        /// <summary>
        /// Ancore source informative effettivamente indicizzate dalla matrice
        /// </summary>
        [JsonIgnore]
        public List<DeepSiftVisualAnchor> SourceAnchors { get; set; }

        /// <summary>
        /// Ancore language informative effettivamente indicizzate dalla matrice
        /// </summary>
        [JsonIgnore]
        public List<DeepSiftVisualAnchor> LanguageAnchors { get; set; }

        /// <summary>
        /// Numero di celle elaborate, persistito fuori dalla matrice
        /// </summary>
        public long ProcessedCellCount { get; set; }

        /// <summary>
        /// Numero di celle accettate, persistito fuori dalla matrice
        /// </summary>
        public int AcceptedCellCount { get; set; }

        /// <summary>
        /// Coppie rifiutate raggruppate per motivo diagnostico del backend
        /// </summary>
        public Dictionary<string, int> RejectionCounts { get; set; }

        /// <summary>
        /// Evidenze visuali accettate persistite per il replay del solver temporale
        /// </summary>
        public List<DeepSiftAcceptedPairDiagnostic> AcceptedPairs { get; set; }

        /// <summary>
        /// Ancore source senza descriptor utilizzabili
        /// </summary>
        public int SourceFeaturelessAnchorCount { get; set; }

        /// <summary>
        /// Ancore language senza descriptor utilizzabili
        /// </summary>
        public int LanguageFeaturelessAnchorCount { get; set; }

        /// <summary>
        /// Tempo estrazione descriptor
        /// </summary>
        public long FeatureExtractionMs { get; set; }

        /// <summary>
        /// Tempo matching e geometria
        /// </summary>
        public long MatchingMs { get; set; }

        /// <summary>
        /// Tempo cumulativo CPU del nearest-neighbour matching
        /// </summary>
        public long DescriptorMatchingMs { get; set; }

        /// <summary>
        /// Tempo cumulativo CPU di RANSAC e validazione geometrica
        /// </summary>
        public long GeometryMs { get; set; }

        /// <summary>
        /// Tempo di upload e preparazione buffer Vulkan
        /// </summary>
        public long UploadMs { get; set; }

        /// <summary>
        /// Tempo di readback degli output Vulkan
        /// </summary>
        public long ReadbackMs { get; set; }

        /// <summary>
        /// Indica se l'operazione è stata cancellata
        /// </summary>
        public bool Cancelled { get; set; }

        #endregion
    }

    /// <summary>
    /// Evidenza visuale temporale indipendente dalla matrice e replayabile da diagnostica
    /// </summary>
    public class DeepSiftAcceptedPairDiagnostic
    {
        /// <summary>
        /// Indice dell'ancora source
        /// </summary>
        public int SourceAnchorIndex { get; set; }

        /// <summary>
        /// Indice dell'ancora language
        /// </summary>
        public int LanguageAnchorIndex { get; set; }

        /// <summary>
        /// PTS source in millisecondi
        /// </summary>
        public double SourcePtsMs { get; set; }

        /// <summary>
        /// PTS language in millisecondi
        /// </summary>
        public double LanguagePtsMs { get; set; }

        /// <summary>
        /// Punteggio normalizzato del match geometrico
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Omografia 3x3 row-major dalla source alla language, mantenuta soltanto in memoria
        /// </summary>
        [JsonIgnore]
        public double[] Homography { get; set; }
    }
}
