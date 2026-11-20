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
        /// Converte un file in Matroska. L'uscita ha il nome del file con estensione .mkv, accanto all'originale o nella cartella
        /// indicata, e un nome occupato riceve il suffisso numerico dell'allocatore. Lo stop dell'operazione corrente interrompe
        /// mkvmerge e non lascia file; un file non scritto restituisce il nome prenotato al lotto
        /// </summary>
        /// <param name="inputPath">File da convertire</param>
        /// <param name="outputFolder">Cartella di uscita, vuota per scrivere accanto all'originale</param>
        /// <param name="allocator">Allocatore dei percorsi del lotto</param>
        /// <param name="cancellationToken">Annullamento che interrompe la lettura delle tracce</param>
        /// <param name="onProgress">Avanzamento di mkvmerge in percentuale</param>
        /// <returns>Esito della conversione</returns>
        public ToolboxItemResult Convert(string inputPath, string outputFolder, OutputPathAllocator allocator, CancellationToken cancellationToken, Action<int> onProgress)
        {
            ToolboxItemResult result = new ToolboxItemResult();
            List<string> errors = new List<string>();
            string folderError = string.IsNullOrEmpty(outputFolder) ? "" : OutputPathAllocator.ValidateFolder(outputFolder);

            if (folderError.Length > 0 || !File.Exists(inputPath))
            {
                result.Status = ToolboxItemStatus.Failed;
                result.Message = folderError.Length > 0 ? folderError : AppText.F("toolbox.videoToMkv.inputMissing", Path.GetFileName(inputPath));
                return result;
            }

            // Un file che mkvmerge non riconosce non ha tracce: l'errore chiaro lo dà la conversione, che fallisce
            List<TrackInfo> sourceTracks = this._mkvTools.GetFileInfoIsolated(inputPath, 0, cancellationToken, null)?.Tracks ?? new List<TrackInfo>();
            string finalPath = allocator.Reserve(OutputPathAllocator.GetMatroskaPath(inputPath, outputFolder));

            try
            {
                StagedOutputFile output = new StagedOutputFile(finalPath, false);
                try
                {
                    ProcessResult run = MkvToolNixGuiMode.Run(this._mkvMergePath, MediaContainerNormalizer.BuildRemuxArguments(sourceTracks, inputPath, output.TempPath),
                        onProgress, result.Warnings.Add, errors);

                    if (result.StopIfRequested())
                        return result;

                    // In riga il motivo senza percorsi: mkvmerge li cita completi, il dettaglio li conserva
                    if (MkvToolNixGuiMode.Failed(run, errors, AppText.F("toolbox.videoToMkv.exitCode", run.ExitCode)))
                        result.Fail(errors, message => message.Replace(Path.GetFullPath(inputPath), Path.GetFileName(inputPath)).Replace(output.TempPath, Path.GetFileName(finalPath)));
                    else if (!MediaContainerNormalizer.KeepsTracks(sourceTracks, this._mkvTools.GetFileInfoIsolated(output.TempPath, 0, cancellationToken, null)?.Tracks ?? new List<TrackInfo>()))
                    {
                        result.Status = ToolboxItemStatus.Failed;
                        result.Message = AppText.T("toolbox.videoToMkv.tracksChanged");
                    }
                    else
                    {
                        output.Commit();
                        result.Complete(finalPath);
                    }
                }
                finally
                {
                    // Un temporaneo che non si cancella resta sul disco: la riga lo dice, con il percorso
                    result.AddLeftovers(output.Discard());
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
}
