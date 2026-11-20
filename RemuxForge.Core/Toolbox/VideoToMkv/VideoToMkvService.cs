using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Toolbox.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Core.Toolbox.VideoToMkv
{
    /// <summary>
    /// Porta un file video in Matroska con mkvmerge, senza ricodifica e con le stesse tracce. Il Matroska si scrive in un temporaneo
    /// accanto al percorso finale e prende il nome finale solo se mkvmerge riesce e le tracce coincidono con quelle dell'ingresso
    /// </summary>
    public sealed class VideoToMkvService
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile mkvmerge
        /// </summary>
        private readonly string _mkvMergePath;

        /// <summary>
        /// Lettore delle tracce, sullo stesso mkvmerge della conversione
        /// </summary>
        private readonly MkvToolsService _mkvTools;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso eseguibile mkvmerge</param>
        public VideoToMkvService(string mkvMergePath)
        {
            this._mkvMergePath = mkvMergePath;
            this._mkvTools = new MkvToolsService(mkvMergePath);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Converte un file in Matroska. Un file già Matroska o con un contenitore non gestito si salta; l'uscita ha il nome del file
        /// con estensione .mkv, accanto all'originale o nella cartella indicata, e un nome occupato riceve il suffisso numerico
        /// dell'allocatore. Lo stop dell'operazione corrente interrompe mkvmerge e non lascia file; un file non scritto restituisce
        /// il nome prenotato al lotto
        /// </summary>
        /// <param name="inputPath">File da convertire</param>
        /// <param name="outputFolder">Cartella di uscita, vuota per scrivere accanto all'originale</param>
        /// <param name="allocator">Allocatore dei percorsi del lotto</param>
        /// <param name="cancellationToken">Annullamento che interrompe la lettura delle tracce</param>
        /// <param name="onProgress">Avanzamento di mkvmerge in percentuale</param>
        /// <returns>Esito della conversione</returns>
        public VideoToMkvResult Convert(string inputPath, string outputFolder, OutputPathAllocator allocator, CancellationToken cancellationToken, Action<int> onProgress)
        {
            VideoToMkvResult result = new VideoToMkvResult();
            List<string> errors = new List<string>();
            string folderError = string.IsNullOrEmpty(outputFolder) ? "" : OutputPathAllocator.ValidateFolder(outputFolder);

            if (MediaContainerFormats.IsMatroska(inputPath) || !MediaContainerFormats.IsSupported(inputPath))
            {
                result.Status = VideoToMkvStatus.Skipped;
                result.Message = MediaContainerFormats.IsMatroska(inputPath) ? AppText.T("toolbox.videoToMkv.skippedMatroska") : AppText.F("toolbox.videoToMkv.skippedUnsupported", Path.GetExtension(inputPath));
                return result;
            }

            if (folderError.Length > 0 || !File.Exists(inputPath))
            {
                result.Status = VideoToMkvStatus.Failed;
                result.Message = folderError.Length > 0 ? folderError : AppText.F("toolbox.videoToMkv.inputMissing", Path.GetFileName(inputPath));
                return result;
            }

            // Un file che mkvmerge non riconosce non ha tracce: l'errore chiaro lo dà la conversione, che fallisce
            List<TrackInfo> sourceTracks = this._mkvTools.GetFileInfoIsolated(inputPath, 0, cancellationToken, null)?.Tracks ?? new List<TrackInfo>();
            string folder = string.IsNullOrEmpty(outputFolder) ? Path.GetDirectoryName(Path.GetFullPath(inputPath)) : outputFolder;
            string finalPath = allocator.Reserve(Path.Combine(folder, Path.GetFileNameWithoutExtension(inputPath) + MediaContainerFormats.OUTPUT_EXTENSION));

            try
            {
                StagedOutputFile output = new StagedOutputFile(finalPath, false);
                try
                {
                    ProcessResult run = MkvToolNixGuiMode.Run(this._mkvMergePath, MediaContainerNormalizer.BuildRemuxArguments(sourceTracks, inputPath, output.TempPath),
                        onProgress, result.Warnings.Add, errors);

                    // Un mkvmerge ucciso dallo stop ha un exit code qualsiasi: lo stop si riconosce prima del codice
                    if (ProcessRunner.IsStopRequested())
                    {
                        result.Status = VideoToMkvStatus.Stopped;
                        result.Message = AppText.T("toolbox.videoToMkv.stopped");
                    }
                    else if (run.ExitCode != 0 && run.ExitCode != MkvToolNixGuiMode.WARNING_EXIT_CODE)
                    {
                        if (errors.Count == 0)
                            errors.Add(run.Stderr.Trim().Length > 0 ? run.Stderr.Trim() : AppText.F("toolbox.videoToMkv.exitCode", run.ExitCode));

                        // In riga il motivo senza percorsi: mkvmerge li cita completi, il dettaglio li conserva
                        result.Status = VideoToMkvStatus.Failed;
                        result.Details = string.Join(Environment.NewLine, errors);
                        result.Message = string.Join(" ", errors).Replace(Path.GetFullPath(inputPath), Path.GetFileName(inputPath)).Replace(output.TempPath, Path.GetFileName(finalPath));
                    }
                    else if (!MediaContainerNormalizer.KeepsTracks(sourceTracks, this._mkvTools.GetFileInfoIsolated(output.TempPath, 0, cancellationToken, null)?.Tracks ?? new List<TrackInfo>()))
                    {
                        result.Status = VideoToMkvStatus.Failed;
                        result.Message = AppText.T("toolbox.videoToMkv.tracksChanged");
                    }
                    else
                    {
                        output.Commit();
                        result.Status = result.Warnings.Count > 0 ? VideoToMkvStatus.ConvertedWithWarnings : VideoToMkvStatus.Converted;
                        result.OutputPath = finalPath;
                    }
                }
                finally
                {
                    // Un temporaneo che non si cancella resta sul disco: la riga lo dice, con il percorso
                    List<string> leftovers = output.Discard();
                    if (leftovers.Count > 0)
                    {
                        result.Message = (result.Message + " " + string.Join(" ", leftovers)).Trim();
                        // Il dettaglio, quando c'è, prende il posto del messaggio nel log e nel suggerimento della riga
                        if (result.Details.Length > 0)
                            result.Details += Environment.NewLine + string.Join(Environment.NewLine, leftovers);
                    }
                }
            }
            finally
            {
                if (result.OutputPath.Length == 0)
                    allocator.Release(finalPath);
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Stato di un file nella conversione in Matroska
    /// </summary>
    public enum VideoToMkvStatus
    {
        /// <summary>In attesa di conversione</summary>
        Pending,

        /// <summary>Conversione in corso</summary>
        Running,

        /// <summary>Matroska scritto</summary>
        Converted,

        /// <summary>Matroska scritto, con avvisi di mkvmerge</summary>
        ConvertedWithWarnings,

        /// <summary>File non convertibile, lasciato com'è</summary>
        Skipped,

        /// <summary>Conversione non riuscita</summary>
        Failed,

        /// <summary>Conversione interrotta dallo stop</summary>
        Stopped
    }

    /// <summary>
    /// Esito della conversione di un file
    /// </summary>
    public sealed class VideoToMkvResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VideoToMkvResult()
        {
            this.Status = VideoToMkvStatus.Pending;
            this.OutputPath = "";
            this.Message = "";
            this.Details = "";
            this.Warnings = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato finale del file
        /// </summary>
        public VideoToMkvStatus Status { get; set; }

        /// <summary>
        /// Matroska scritto, vuoto se la conversione non è riuscita
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// Motivo del salto, dell'errore o dell'interruzione, senza percorsi completi
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Errori di mkvmerge così come li ha scritti, vuoto se il motivo non viene da mkvmerge
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// Avvisi di mkvmerge, anche con conversione riuscita
        /// </summary>
        public List<string> Warnings { get; }

        #endregion
    }
}
