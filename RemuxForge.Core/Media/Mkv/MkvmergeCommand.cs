using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Media.Mkv
{
    /// <summary>
    /// Argomenti di un comando mkvmerge costruiti in ordine: file di uscita, input con le loro opzioni, ordine delle tracce e
    /// opzioni globali. Le tracce si collocano per traccia di riferimento (input e ID nell'input); l'ordine finale le elenca
    /// nella sequenza delle tracce di riferimento
    /// </summary>
    public sealed class MkvmergeCommand
    {
        #region Variabili di classe

        /// <summary>
        /// Argomenti nell'ordine di scrittura
        /// </summary>
        private readonly List<string> _arguments;

        /// <summary>
        /// Collocazione di ogni traccia di riferimento, come input:ID, nell'ordine in cui sono state collocate
        /// </summary>
        private readonly Dictionary<int, string> _placements;

        /// <summary>
        /// Tracce di riferimento nell'ordine in cui sono state collocate
        /// </summary>
        private readonly List<int> _placementOrder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore sul file di uscita
        /// </summary>
        /// <param name="outputFile">File scritto da mkvmerge</param>
        public MkvmergeCommand(string outputFile)
        {
            this._arguments = new List<string> { "-o", outputFile };
            this._placements = new Dictionary<int, string>();
            this._placementOrder = new List<int>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Numero di input aggiunti: è anche l'indice che riceverà il prossimo input
        /// </summary>
        public int InputCount { get; private set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Opzioni di traccia che riportano lingua, nome, traccia predefinita e forzata di una traccia di riferimento
        /// </summary>
        /// <param name="id">ID della traccia nell'input a cui si applicano</param>
        /// <param name="track">Traccia da cui si leggono le proprietà</param>
        /// <returns>Opzioni da mettere fra quelle dell'input</returns>
        public static string[] TrackOptions(int id, TrackInfo track)
        {
            return new string[] { "--language", id + ":" + (string.IsNullOrEmpty(track.LanguageIetf) ? (string.IsNullOrEmpty(track.Language) ? "und" : track.Language) : track.LanguageIetf),
                "--track-name", id + ":" + (track.Name ?? ""), "--default-track-flag", id + ":" + (track.DefaultTrack ? "yes" : "no"),
                "--forced-display-flag", id + ":" + (track.ForcedTrack ? "yes" : "no") };
        }

        /// <summary>
        /// Aggiunge un input preceduto dalle sue opzioni
        /// </summary>
        /// <param name="file">File di input</param>
        /// <param name="options">Opzioni che mkvmerge applica a questo input, nessuna se vuote</param>
        /// <returns>Indice dell'input, quello da usare per collocarne le tracce</returns>
        public int AddInput(string file, params string[] options)
        {
            this._arguments.AddRange(options);
            this._arguments.Add(file);
            return this.InputCount++;
        }

        /// <summary>
        /// Aggiunge opzioni globali dopo gli input e l'ordine già scritti
        /// </summary>
        /// <param name="options">Opzioni nell'ordine di scrittura</param>
        public void AddGlobalOptions(params string[] options)
        {
            this._arguments.AddRange(options);
        }

        /// <summary>
        /// Colloca una traccia di riferimento in un input
        /// </summary>
        /// <param name="reference">ID della traccia di riferimento, unico nel comando</param>
        /// <param name="input">Indice dell'input che porta la traccia</param>
        /// <param name="trackId">ID della traccia nell'input</param>
        /// <exception cref="System.ArgumentException">Traccia di riferimento già collocata</exception>
        public void Place(int reference, int input, int trackId)
        {
            this._placements.Add(reference, input + ":" + trackId);
            this._placementOrder.Add(reference);
        }

        /// <summary>
        /// Indica se una traccia di riferimento è stata collocata
        /// </summary>
        /// <param name="reference">ID della traccia di riferimento</param>
        /// <returns>True se la traccia è collocata</returns>
        public bool IsPlaced(int reference)
        {
            return this._placements.ContainsKey(reference);
        }

        /// <summary>
        /// Scrive l'ordine delle tracce nella sequenza in cui sono state collocate
        /// </summary>
        public void AddTrackOrder()
        {
            this.AddTrackOrder(this._placementOrder);
        }

        /// <summary>
        /// Scrive l'ordine delle tracce nella sequenza indicata
        /// </summary>
        /// <param name="references">Tracce di riferimento nell'ordine voluto, tutte collocate</param>
        /// <exception cref="KeyNotFoundException">Una traccia di riferimento non è collocata</exception>
        public void AddTrackOrder(IEnumerable<int> references)
        {
            List<string> order = new List<string>();
            foreach (int reference in references)
                order.Add(this._placements[reference]);
            this._arguments.Add("--track-order");
            this._arguments.Add(string.Join(",", order));
        }

        /// <summary>
        /// Argomenti del comando, nell'ordine in cui sono stati aggiunti
        /// </summary>
        /// <returns>Argomenti per mkvmerge</returns>
        public List<string> ToArguments()
        {
            return new List<string>(this._arguments);
        }

        #endregion
    }
}
