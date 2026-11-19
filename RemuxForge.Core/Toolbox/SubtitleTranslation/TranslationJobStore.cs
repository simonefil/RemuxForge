using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Job di traduzione nella cartella di configurazione: translation-jobs/&lt;id&gt;/job.json e files/&lt;fileId&gt;.json scritti in
    /// modo atomico, calls.jsonl in sola aggiunta. I file in parallelo scrivono ciascuno il proprio stato; testata e registro
    /// passano dal lock dello store
    /// </summary>
    public class TranslationJobStore
    {
        #region Costanti

        /// <summary>
        /// Cartella dei job dentro la cartella di configurazione
        /// </summary>
        public const string FOLDER_NAME = "translation-jobs";

        /// <summary>
        /// Testata del job
        /// </summary>
        public const string JOB_FILE = "job.json";

        /// <summary>
        /// Cartella degli stati dei file del job
        /// </summary>
        public const string FILES_FOLDER = "files";

        /// <summary>
        /// Registro delle chiamate del job
        /// </summary>
        public const string CALLS_FILE = "calls.jsonl";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Cartella dei job
        /// </summary>
        private readonly string _root;

        /// <summary>
        /// Protegge testata e registro dei job
        /// </summary>
        private readonly object _lock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella di configurazione di RemuxForge</param>
        public TranslationJobStore(string configFolder)
        {
            this._root = Path.Combine(configFolder ?? "", FOLDER_NAME);
            this._lock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea un job nuovo, non ancora salvato
        /// </summary>
        /// <param name="projectId">Progetto di origine</param>
        /// <returns>Job con identificativo e date</returns>
        public TranslationJob Create(string projectId)
        {
            TranslationJob job = new TranslationJob();
            job.Id = Guid.NewGuid().ToString("N");
            job.ProjectId = projectId;
            return job;
        }

        /// <summary>
        /// True se la cartella del job esiste
        /// </summary>
        /// <param name="id">Identificativo del job</param>
        /// <returns>True se il job c'è su disco</returns>
        public bool Exists(string id)
        {
            return Directory.Exists(this.GetFolder(id));
        }

        /// <summary>
        /// Legge la testata di un job; un file guasto, di una versione più recente o di una precedente che non si apre più dà
        /// InvalidDataException
        /// </summary>
        /// <param name="id">Identificativo del job</param>
        /// <returns>Testata letta</returns>
        public TranslationJob Load(string id)
        {
            string path = Path.Combine(this.GetFolder(id), JOB_FILE);
            TranslationJob job;

            lock (this._lock)
            {
                CheckSchemaVersion(path, TranslationJob.SCHEMA_VERSION);
                job = TranslationJsonFile.Read<TranslationJob>(path, AppText.F("toolbox.subtitleTranslation.jobUnreadable", path));
            }

            return job;
        }

        /// <summary>
        /// Salva la testata con scrittura atomica e ne aggiorna l'ultima modifica
        /// </summary>
        /// <param name="job">Testata da salvare</param>
        public void Save(TranslationJob job)
        {
            if (job == null)
                throw new ArgumentNullException(nameof(job));

            lock (this._lock)
            {
                job.UpdatedAt = DateTimeOffset.Now;
                TranslationJsonFile.Write(Path.Combine(this.GetFolder(job.Id), JOB_FILE), job);
            }
        }

        /// <summary>
        /// Legge lo stato di un file del job; un file guasto, di una versione più recente o di una precedente che non si apre più dà
        /// InvalidDataException
        /// </summary>
        /// <param name="jobId">Identificativo del job</param>
        /// <param name="fileId">File del progetto</param>
        /// <returns>Stato del file</returns>
        public TranslationJobFileState LoadFile(string jobId, string fileId)
        {
            string path = Path.Combine(this.GetFolder(jobId), FILES_FOLDER, fileId + ".json");
            CheckSchemaVersion(path, TranslationJobFileState.SCHEMA_VERSION);
            return TranslationJsonFile.Read<TranslationJobFileState>(path, AppText.F("toolbox.subtitleTranslation.jobUnreadable", path));
        }

        /// <summary>
        /// Salva lo stato di un file del job con scrittura atomica; ogni file ha il suo JSON e i file in parallelo non si toccano
        /// </summary>
        /// <param name="jobId">Identificativo del job</param>
        /// <param name="file">Stato del file</param>
        public void SaveFile(string jobId, TranslationJobFileState file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            TranslationJsonFile.Write(Path.Combine(this.GetFolder(jobId), FILES_FOLDER, file.FileId + ".json"), file);
        }

        /// <summary>
        /// Cancella lo stato di un file tolto dal job
        /// </summary>
        /// <param name="jobId">Identificativo del job</param>
        /// <param name="fileId">File del progetto</param>
        public void DeleteFile(string jobId, string fileId)
        {
            string path = Path.Combine(this.GetFolder(jobId), FILES_FOLDER, fileId + ".json");
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>
        /// Cancella un job con tutto il suo stato su disco
        /// </summary>
        /// <param name="id">Identificativo del job</param>
        public void Delete(string id)
        {
            string folder = this.GetFolder(id);

            lock (this._lock)
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        /// <summary>
        /// Cancella i job di un progetto letti dalle testate, compreso quello che il progetto non riporta perché una chiusura brusca
        /// è arrivata fra il salvataggio del job e quello del progetto; una testata illeggibile lascia la sua cartella
        /// </summary>
        /// <param name="projectId">Identificativo del progetto</param>
        public void DeleteOfProject(string projectId)
        {
            if (!Directory.Exists(this._root))
                return;

            foreach (string folder in Directory.GetDirectories(this._root))
            {
                string id = Path.GetFileName(folder);
                TranslationJob job;
                try
                {
                    job = this.Load(id);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is JsonException || ex is InvalidDataException || ex is UnauthorizedAccessException))
                        throw;
                    continue;
                }

                if (job.ProjectId == projectId)
                    this.Delete(id);
            }
        }

        /// <summary>
        /// Accoda una riga al registro delle chiamate del job
        /// </summary>
        /// <param name="jobId">Identificativo del job</param>
        /// <param name="record">Chiamata da registrare</param>
        public void AppendCall(string jobId, TranslationCallRecord record)
        {
            lock (this._lock)
            {
                TranslationJsonFile.AppendLine(Path.Combine(this.GetFolder(jobId), CALLS_FILE), record);
            }
        }

        /// <summary>
        /// Legge il registro delle chiamate del job; una riga illeggibile, come l'ultima troncata da una chiusura brusca, si salta
        /// </summary>
        /// <param name="jobId">Identificativo del job</param>
        /// <returns>Chiamate nell'ordine del registro</returns>
        public List<TranslationCallRecord> ReadCalls(string jobId)
        {
            lock (this._lock)
            {
                return TranslationJsonFile.ReadLines<TranslationCallRecord>(Path.Combine(this.GetFolder(jobId), CALLS_FILE));
            }
        }

        /// <summary>
        /// Cartella di un job
        /// </summary>
        /// <param name="id">Identificativo del job</param>
        /// <returns>Percorso della cartella, anche se non esiste</returns>
        public string GetFolder(string id)
        {
            return Path.Combine(this._root, id);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Rifiuta un file del job di un'altra versione prima di leggerlo: uno di una versione precedente può avere valori che il
        /// formato di oggi non legge più, e si rifiuta col motivo invece che come file guasto
        /// </summary>
        /// <param name="path">File del job</param>
        /// <param name="current">Versione del formato di oggi</param>
        private static void CheckSchemaVersion(string path, int current)
        {
            int version = File.Exists(path) ? TranslationJsonFile.ReadSchemaVersion(File.ReadAllBytes(path)) : 0;
            if (version > current)
                throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.jobTooNew", path));
            if (version > 0 && version < current)
                throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.jobOutdated", path));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Cartella dei job
        /// </summary>
        public string Root
        {
            get { return this._root; }
        }

        #endregion
    }
}
