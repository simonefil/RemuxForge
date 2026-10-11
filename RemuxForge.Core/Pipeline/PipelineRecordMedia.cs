using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Contesto di elaborazione di un record: i Matroska da leggere e il Lang da scrivere nel comando di mkvmerge
    /// </summary>
    /// <remarks>
    /// Il dry-run si decide qui, una volta sola: non normalizza il Source, normalizza il Lang solo quando capitoli Lang o Deep
    /// sui sottotitoli lo leggono con mkvextract, e il comando mostrato resta sul Lang originale, con gli stessi ID.
    /// I temporanei vengono dal registro condiviso: chi tiene già lo stesso file (analisi appena conclusa, editor aperto) li presta.
    /// </remarks>
    public sealed class PipelineRecordMedia : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Lease dei Matroska normalizzati, null quando si leggono gli originali
        /// </summary>
        private readonly NormalizedMediaPair _media;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="sourcePath">Matroska da cui si legge il Source</param>
        /// <param name="langPath">Matroska da cui si legge il Lang</param>
        /// <param name="langCommandPath">Lang scritto nel comando di mkvmerge</param>
        /// <param name="media">Lease dei Matroska normalizzati, null quando si leggono gli originali</param>
        private PipelineRecordMedia(string sourcePath, string langPath, string langCommandPath, NormalizedMediaPair media)
        {
            this.SourcePath = sourcePath;
            this.LangPath = langPath;
            this.LangCommandPath = langCommandPath;
            this._media = media;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Matroska da cui si legge il Source
        /// </summary>
        public string SourcePath { get; }

        /// <summary>
        /// Matroska da cui si legge il Lang
        /// </summary>
        public string LangPath { get; }

        /// <summary>
        /// Lang scritto nel comando di mkvmerge: in dry-run l'originale, altrimenti il Matroska letto
        /// </summary>
        public string LangCommandPath { get; }

        /// <summary>
        /// Matroska temporanei letti dall'elaborazione, da togliere dalla cache delle informazioni quando finisce
        /// </summary>
        public List<string> TemporaryPaths
        {
            get
            {
                List<string> result = new List<string>();
                foreach (NormalizedMediaLease lease in new NormalizedMediaLease[] { this._media?.Source, this._media?.Lang })
                {
                    if (lease != null && lease.IsTemporary)
                        result.Add(lease.Path);
                }
                return result;
            }
        }

        #endregion

        #region Metodi pubblici statici

        /// <summary>
        /// File originali letti così come sono, per l'anteprima del comando e del piano audio fuori dall'elaborazione
        /// </summary>
        /// <param name="record">Record della coppia</param>
        /// <returns>Contesto sui file originali, senza nulla da rilasciare</returns>
        public static PipelineRecordMedia Originals(FileProcessingRecord record)
        {
            return new PipelineRecordMedia(record.SourceFilePath, record.LangFilePath, record.LangFilePath, null);
        }

        /// <summary>
        /// Prepara i Matroska dell'elaborazione: mkvextract e la mappatura ID→stream di ffmpeg valgono solo su Matroska,
        /// quindi gli altri container passano da un temporaneo rilasciato a fine elaborazione (D12)
        /// </summary>
        /// <param name="record">Record da elaborare</param>
        /// <param name="options">Opzioni della pipeline: dry-run, capitoli Lang e mkvmerge</param>
        /// <param name="needsMerge">True se il flusso importa tracce dal Lang</param>
        /// <param name="error">Motivo del fallimento della normalizzazione, vuoto se riuscita</param>
        /// <returns>Contesto da rilasciare con Dispose, null con errore</returns>
        public static PipelineRecordMedia Open(FileProcessingRecord record, Options options, bool needsMerge, out string error)
        {
            NormalizedMediaPair media = null;
            if (!options.DryRun)
                media = NormalizedMediaStore.Instance.AcquirePair(record.SourceFilePath, needsMerge ? record.LangFilePath : "", false, options.MkvMergePath);
            else if (needsMerge && (options.CopyLangChapters || record.DeepAnalysisApplied))
                media = new NormalizedMediaPair(null, NormalizedMediaStore.Instance.Acquire(record.LangFilePath, false, options.MkvMergePath));

            error = media != null ? media.Open(CancellationToken.None) : "";
            if (error.Length > 0)
            {
                media.Dispose();
                return null;
            }

            string langPath = media?.Lang != null ? media.Lang.Path : record.LangFilePath;
            return new PipelineRecordMedia(media?.Source != null ? media.Source.Path : record.SourceFilePath, langPath, options.DryRun ? record.LangFilePath : langPath, media);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rilascia i lease dei Matroska normalizzati; l'ultimo lease di un file cancella il suo temporaneo
        /// </summary>
        public void Dispose()
        {
            this._media?.Dispose();
        }

        #endregion
    }
}
