using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Toolbox.Common
{
    /// <summary>
    /// File di uscita scritto prima con un nome temporaneo nella cartella di destinazione e rinominato solo a lavoro finito,
    /// così uno Stop o un errore non lasciano mai un file parziale con il nome finale. Il file accoppiato (il .sub di un .idx)
    /// si scrive, rinomina e cancella insieme al principale
    /// </summary>
    public sealed class StagedOutputFile : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Percorsi temporanei: il principale e l'eventuale accoppiato
        /// </summary>
        private readonly string[] _tempPaths;

        /// <summary>
        /// Percorsi finali, nello stesso ordine dei temporanei
        /// </summary>
        private readonly string[] _finalPaths;

        /// <summary>
        /// True quando i temporanei hanno preso il nome finale
        /// </summary>
        private bool _committed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Crea il temporaneo vuoto accanto al percorso finale; se la cartella non si scrive l'errore arriva qui, prima di lanciare
        /// il tool che lo riempie
        /// </summary>
        /// <param name="finalPath">Percorso finale, già prenotato e libero</param>
        public StagedOutputFile(string finalPath)
        {
            string fullPath = Path.GetFullPath(finalPath);
            string folder = Path.GetDirectoryName(fullPath);
            string tempPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(fullPath) + ".remuxforge." + Guid.NewGuid().ToString("N") + ".tmp" + Path.GetExtension(fullPath));
            string companion = FileHelper.GetCompanionPath(fullPath);

            this._finalPaths = companion.Length > 0 ? new string[] { fullPath, companion } : new string[] { fullPath };
            this._tempPaths = companion.Length > 0 ? new string[] { tempPath, FileHelper.GetCompanionPath(tempPath) } : new string[] { tempPath };
            try
            {
                Directory.CreateDirectory(folder);
                using (File.Create(tempPath))
                {
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException(AppText.F("toolbox.output.folderNotWritable", folder, ex.Message), ex);
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Dà ai temporanei il nome finale, che deve essere libero; se un file della coppia non si rinomina, quelli già rinominati
        /// tornano temporanei e nessun nome finale resta occupato
        /// </summary>
        public void Commit()
        {
            int moved = 0;

            if (this._committed)
                throw new InvalidOperationException(AppText.F("toolbox.output.alreadyCommitted", this.FinalPath));

            try
            {
                for (; moved < this._tempPaths.Length; moved++)
                    File.Move(this._tempPaths[moved], this._finalPaths[moved]);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                for (int i = 0; i < moved; i++)
                    File.Move(this._finalPaths[i], this._tempPaths[i]);

                throw new IOException(AppText.F("toolbox.output.commitFailed", this._finalPaths[moved], ex.Message), ex);
            }

            this._committed = true;
        }

        /// <summary>
        /// Sostituisce un file esistente: l'originale (con il suo accoppiato) passa a un backup con nome univoco, i temporanei prendono
        /// il nome finale e il backup si cancella; a qualunque errore l'originale torna al suo posto intatto e il messaggio dice
        /// quale file non si è potuto spostare
        /// </summary>
        /// <param name="originalPath">File da sostituire, di norma lo stesso percorso finale</param>
        public void CommitReplacing(string originalPath)
        {
            string originalFull = Path.GetFullPath(originalPath);
            string originalCompanion = FileHelper.GetCompanionPath(originalFull);
            string backupSuffix = ".remuxforge." + Guid.NewGuid().ToString("N") + ".bak";
            List<string> originals = new List<string> { originalFull };
            int backedUp = 0;

            if (originalCompanion.Length > 0 && File.Exists(originalCompanion))
                originals.Add(originalCompanion);

            try
            {
                for (; backedUp < originals.Count; backedUp++)
                    File.Move(originals[backedUp], originals[backedUp] + backupSuffix);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                for (int i = 0; i < backedUp; i++)
                    File.Move(originals[i] + backupSuffix, originals[i]);

                throw new IOException(AppText.F("toolbox.output.replaceFailed", originals[backedUp], ex.Message), ex);
            }

            try
            {
                this.Commit();
            }
            catch
            {
                // Commit ha già liberato i nomi finali: gli originali tornano dal backup
                foreach (string original in originals)
                    File.Move(original + backupSuffix, original);

                throw;
            }

            foreach (string original in originals)
                FileHelper.DeleteTempFile(original + backupSuffix);
        }

        /// <summary>
        /// Senza commit cancella i temporanei, compresi i file parziali lasciati da un tool interrotto
        /// </summary>
        public void Dispose()
        {
            if (!this._committed)
            {
                foreach (string tempPath in this._tempPaths)
                    FileHelper.DeleteTempFile(tempPath);
            }
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso finale del file principale
        /// </summary>
        public string FinalPath
        {
            get { return this._finalPaths[0]; }
        }

        /// <summary>
        /// Percorso temporaneo in cui il tool scrive il file principale; il tool scrive l'accoppiato accanto con lo stesso nome
        /// </summary>
        public string TempPath
        {
            get { return this._tempPaths[0]; }
        }

        #endregion
    }
}
