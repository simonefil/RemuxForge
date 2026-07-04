using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Rename
{
    /// <summary>
    /// Esegue Advanced Rename con strategia two-pass
    /// </summary>
    public class RenameExecutionService
    {
        #region Metodi pubblici

        /// <summary>
        /// Esegue rename dei file modificati
        /// </summary>
        /// <param name="previewItems">Preview validata</param>
        /// <returns>Risultato esecuzione</returns>
        public RenameExecutionResult Execute(List<RenamePreviewItem> previewItems)
        {
            RenameExecutionResult result = new RenameExecutionResult();

            if (!this.ValidatePreview(previewItems, result))
                return result;

            List<RenamePreviewItem> toRename = new List<RenamePreviewItem>();
            for (int i = 0; i < previewItems.Count; i++)
            {
                if (previewItems[i].OriginalName != previewItems[i].NewName)
                    toRename.Add(previewItems[i]);
            }

            if (toRename.Count == 0)
                return result;

            List<RenameTempEntry> tempEntries = new List<RenameTempEntry>();
            string tempSuffix = ".remuxforge_rename_temp_" + Guid.NewGuid().ToString("N");

            for (int i = 0; i < toRename.Count; i++)
            {
                RenamePreviewItem item = toRename[i];
                string directory = Path.GetDirectoryName(item.OriginalFullPath);
                string tempName = item.NewName + tempSuffix;
                string tempPath = Path.Combine(directory, tempName);

                try
                {
                    File.Move(item.OriginalFullPath, tempPath);
                    RenameTempEntry tempEntry = new RenameTempEntry();
                    tempEntry.Item = item;
                    tempEntry.TempPath = tempPath;
                    tempEntries.Add(tempEntry);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.FailCount++;
                    result.ErrorMessage = AppText.F("rename.error.tempRename", item.OriginalName, ex.Message);
                    result.Errors.Add(result.ErrorMessage);
                    this.RollbackTempEntries(tempEntries);
                    return result;
                }
            }

            for (int i = 0; i < tempEntries.Count; i++)
            {
                RenameTempEntry tempEntry = tempEntries[i];
                try
                {
                    File.Move(tempEntry.TempPath, tempEntry.Item.TargetFullPath);
                    result.SuccessCount++;
                }
                catch (Exception ex)
                {
                    result.FailCount++;
                    string error = AppText.F("rename.error.finalRename", tempEntry.Item.NewName, ex.Message);
                    result.Errors.Add(error);
                    if (string.IsNullOrEmpty(result.ErrorMessage))
                        result.ErrorMessage = error;

                    this.RollbackSingle(tempEntry);
                }
            }

            result.Success = result.FailCount == 0;
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valida che la preview sia eseguibile
        /// </summary>
        /// <param name="previewItems">Elementi preview</param>
        /// <param name="result">Risultato esecuzione da popolare</param>
        /// <returns>Vero se la preview è valida</returns>
        private bool ValidatePreview(List<RenamePreviewItem> previewItems, RenameExecutionResult result)
        {
            if (previewItems == null)
            {
                result.Success = false;
                result.ErrorMessage = AppText.T("rename.error.nullPreview");
                result.Errors.Add(result.ErrorMessage);
                return false;
            }

            for (int i = 0; i < previewItems.Count; i++)
            {
                if (previewItems[i].HasConflict || previewItems[i].HasError)
                {
                    result.Success = false;
                    result.ErrorMessage = AppText.T("rename.error.previewConflicts");
                    result.Errors.Add(result.ErrorMessage);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Esegue rollback di tutte le rinomine temporanee già completate
        /// </summary>
        /// <param name="tempEntries">Rinomine temporanee</param>
        private void RollbackTempEntries(List<RenameTempEntry> tempEntries)
        {
            for (int i = 0; i < tempEntries.Count; i++)
            {
                this.RollbackSingle(tempEntries[i]);
            }
        }

        /// <summary>
        /// Esegue rollback di una singola rinomina temporanea
        /// </summary>
        /// <param name="tempEntry">Rinomina temporanea</param>
        private void RollbackSingle(RenameTempEntry tempEntry)
        {
            try
            {
                if (File.Exists(tempEntry.TempPath) && !File.Exists(tempEntry.Item.OriginalFullPath))
                    File.Move(tempEntry.TempPath, tempEntry.Item.OriginalFullPath);
            }
            catch (IOException)
            {
            }
        }

        #endregion

        #region Classi private

        /// <summary>
        /// Rinomina temporanea usata dal two-pass rename
        /// </summary>
        private class RenameTempEntry
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            public RenameTempEntry()
            {
                this.Item = new RenamePreviewItem();
                this.TempPath = "";
            }

            /// <summary>
            /// Elemento preview associato
            /// </summary>
            public RenamePreviewItem Item { get; set; }

            /// <summary>
            /// Percorso temporaneo creato nella prima passata
            /// </summary>
            public string TempPath { get; set; }
        }

        #endregion
    }
}
