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
        /// True quando i temporanei non sono più di questo oggetto: hanno preso il nome finale, sono stati scartati, oppure un
        /// ripristino non è riuscito e i file restano dove sono, come dice il messaggio d'errore
        /// </summary>
        private bool _released;

        #endregion

        #region Costruttore

        /// <summary>
        /// Crea il temporaneo vuoto accanto al percorso finale, creando la cartella se manca; se la cartella non si scrive l'errore
        /// arriva qui, prima di lanciare il tool che lo riempie
        /// </summary>
        /// <param name="finalPath">Percorso finale, già prenotato e libero</param>
        public StagedOutputFile(string finalPath) : this(finalPath, true)
        {
        }

        /// <summary>
        /// Crea il temporaneo vuoto accanto al percorso finale; se la cartella manca o non si scrive l'errore arriva qui, prima di
        /// lanciare il tool che lo riempie
        /// </summary>
        /// <param name="finalPath">Percorso finale, già prenotato e libero</param>
        /// <param name="createFolder">True per creare la cartella se manca, false se la cartella è stata scelta e deve esistere</param>
        public StagedOutputFile(string finalPath, bool createFolder)
        {
            string fullPath = Path.GetFullPath(finalPath);
            string folder = Path.GetDirectoryName(fullPath);
            string tempPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(fullPath) + ".rf-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp" + Path.GetExtension(fullPath));
            string companion = FileHelper.GetCompanionPath(fullPath);

            this._finalPaths = companion.Length > 0 ? new string[] { fullPath, companion } : new string[] { fullPath };
            this._tempPaths = companion.Length > 0 ? new string[] { tempPath, FileHelper.GetCompanionPath(tempPath) } : new string[] { tempPath };
            try
            {
                if (createFolder)
                    Directory.CreateDirectory(folder);
                using (new FileStream(tempPath, FileMode.CreateNew))
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

            if (this._released)
                throw new InvalidOperationException(AppText.F("toolbox.output.alreadyCommitted", this.FinalPath));

            try
            {
                for (; moved < this._tempPaths.Length; moved++)
                    File.Move(this._tempPaths[moved], this._finalPaths[moved]);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                List<string> failures = MoveBack(this._finalPaths, this._tempPaths, moved);

                // Un file rimasto col nome finale tiene con sé l'accoppiato temporaneo: Dispose non li separa
                if (failures.Count > 0)
                    this._released = true;

                failures.Insert(0, AppText.F("toolbox.output.commitFailed", this._finalPaths[moved], ex.Message));
                throw new IOException(string.Join(Environment.NewLine, failures), ex);
            }

            this._released = true;
        }

        /// <summary>
        /// Sostituisce un file esistente: l'originale (con il suo accoppiato) passa a un backup con nome univoco, i temporanei prendono
        /// il nome finale e il backup si cancella; a qualunque errore l'originale torna al suo posto, e se non ci riesce il messaggio
        /// nomina il file e il backup in cui è rimasto
        /// </summary>
        /// <param name="originalPath">File da sostituire, di norma lo stesso percorso finale</param>
        /// <returns>Un messaggio per ogni backup che non si è potuto cancellare, con il percorso e il motivo</returns>
        public List<string> CommitReplacing(string originalPath)
        {
            List<string> leftovers = new List<string>();
            string originalFull = Path.GetFullPath(originalPath);
            string originalCompanion = FileHelper.GetCompanionPath(originalFull);
            string backupSuffix = ".rf-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";
            List<string> originals = new List<string> { originalFull };
            List<string> backups = new List<string>();
            int backedUp = 0;

            if (this._released)
                throw new InvalidOperationException(AppText.F("toolbox.output.alreadyCommitted", this.FinalPath));

            if (!File.Exists(originalFull))
                throw new FileNotFoundException(AppText.F("toolbox.output.originalMissing", originalFull), originalFull);

            if (originalCompanion.Length > 0 && File.Exists(originalCompanion))
                originals.Add(originalCompanion);

            foreach (string original in originals)
                backups.Add(original + backupSuffix);

            try
            {
                for (; backedUp < originals.Count; backedUp++)
                    File.Move(originals[backedUp], backups[backedUp]);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                List<string> failures = MoveBack(backups, originals, backedUp);
                failures.Insert(0, AppText.F("toolbox.output.replaceFailed", originals[backedUp], ex.Message));
                throw new IOException(string.Join(Environment.NewLine, failures), ex);
            }

            try
            {
                this.Commit();
            }
            catch (IOException ex)
            {
                // Commit ha già liberato i nomi finali che ha potuto: gli originali tornano dal backup
                List<string> failures = MoveBack(backups, originals, originals.Count);
                if (failures.Count == 0)
                    throw;

                failures.Insert(0, ex.Message);
                throw new IOException(string.Join(Environment.NewLine, failures), ex);
            }

            foreach (string backup in backups)
            {
                try
                {
                    File.Delete(backup);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    leftovers.Add(AppText.F("toolbox.output.leftover", backup, ex.Message));
                }
            }

            return leftovers;
        }

        /// <summary>
        /// Senza commit cancella i temporanei, compresi i file parziali lasciati da un tool interrotto; dopo un commit non fa nulla
        /// </summary>
        /// <returns>Un messaggio per ogni temporaneo che non si è potuto cancellare, con il percorso e il motivo</returns>
        public List<string> Discard()
        {
            List<string> leftovers = new List<string>();

            if (this._released)
                return leftovers;

            this._released = true;
            foreach (string tempPath in this._tempPaths)
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    leftovers.Add(AppText.F("toolbox.output.leftover", tempPath, ex.Message));
                }
            }

            return leftovers;
        }

        /// <summary>
        /// Scarta i temporanei non usati senza lanciare: chi deve dire dei file rimasti chiama Discard prima
        /// </summary>
        public void Dispose()
        {
            this.Discard();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Riporta indietro i primi file spostati, uno per uno: un ripristino che fallisce non ferma gli altri
        /// </summary>
        /// <param name="sources">Percorsi in cui i file si trovano ora</param>
        /// <param name="targets">Percorsi a cui tornano, nello stesso ordine</param>
        /// <param name="count">Quanti file riportare</param>
        /// <returns>Un messaggio per ogni file non riportato, con il percorso in cui è rimasto</returns>
        private static List<string> MoveBack(IList<string> sources, IList<string> targets, int count)
        {
            List<string> failures = new List<string>();

            for (int i = 0; i < count; i++)
            {
                try
                {
                    File.Move(sources[i], targets[i]);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    failures.Add(AppText.F("toolbox.output.restoreFailed", sources[i], targets[i], ex.Message));
                }
            }

            return failures;
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
