using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Progetti di traduzione nella cartella di configurazione: translation-projects/&lt;id&gt;/project.json, scritto in modo
    /// atomico, e il registro calls.jsonl delle chiamate di analisi, in sola aggiunta
    /// </summary>
    public class TranslationProjectStore
    {
        #region Costanti

        /// <summary>
        /// Cartella dei progetti dentro la cartella di configurazione
        /// </summary>
        public const string FOLDER_NAME = "translation-projects";

        /// <summary>
        /// File del progetto
        /// </summary>
        public const string PROJECT_FILE = "project.json";

        /// <summary>
        /// Registro delle chiamate del progetto
        /// </summary>
        public const string CALLS_FILE = "calls.jsonl";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Cartella dei progetti
        /// </summary>
        private readonly string _root;

        /// <summary>
        /// Protegge la scrittura dei file dei progetti
        /// </summary>
        private readonly object _lock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella di configurazione di RemuxForge</param>
        public TranslationProjectStore(string configFolder)
        {
            this._root = Path.Combine(configFolder ?? "", FOLDER_NAME);
            this._lock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i progetti salvati, dal più recente; un progetto illeggibile resta fuori dall'elenco
        /// </summary>
        /// <returns>Progetti letti</returns>
        public List<TranslationProject> List()
        {
            List<TranslationProject> projects = new List<TranslationProject>();
            if (!Directory.Exists(this._root))
                return projects;

            foreach (string folder in Directory.GetDirectories(this._root))
            {
                try
                {
                    projects.Add(this.Load(Path.GetFileName(folder)));
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is JsonException || ex is InvalidDataException || ex is UnauthorizedAccessException))
                        throw;
                }
            }

            projects.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return projects;
        }

        /// <summary>
        /// Legge un progetto; un file guasto o di una versione più recente dà InvalidDataException
        /// </summary>
        /// <param name="id">Identificativo del progetto</param>
        /// <returns>Progetto letto</returns>
        public TranslationProject Load(string id)
        {
            string path = Path.Combine(this._root, id, PROJECT_FILE);
            TranslationProject project;

            lock (this._lock)
            {
                project = TranslationJsonFile.Read<TranslationProject>(path, AppText.F("toolbox.subtitleTranslation.projectUnreadable", path));
            }

            if (project.SchemaVersion > TranslationProject.SCHEMA_VERSION)
                throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.projectTooNew", path));

            return project;
        }

        /// <summary>
        /// Crea un progetto nuovo, non ancora salvato
        /// </summary>
        /// <returns>Progetto con identificativo, date e tabella degli stili vuota</returns>
        public TranslationProject Create()
        {
            TranslationProject project = new TranslationProject();
            project.Id = Guid.NewGuid().ToString("N");
            project.Styles = new List<TranslationProjectStyle>();
            return project;
        }

        /// <summary>
        /// Salva un progetto con scrittura atomica e ne aggiorna l'ultima modifica
        /// </summary>
        /// <param name="project">Progetto da salvare</param>
        public void Save(TranslationProject project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            project.UpdatedAt = DateTimeOffset.Now;
            lock (this._lock)
            {
                TranslationJsonFile.Write(Path.Combine(this._root, project.Id, PROJECT_FILE), project);
            }
        }

        /// <summary>
        /// Cancella un progetto con tutto il suo stato su disco
        /// </summary>
        /// <param name="id">Identificativo del progetto</param>
        public void Delete(string id)
        {
            string folder = Path.Combine(this._root, id);

            lock (this._lock)
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        /// <summary>
        /// Accoda una riga al registro delle chiamate del progetto; dopo un'ultima riga troncata da una chiusura brusca la riga
        /// nuova parte a capo
        /// </summary>
        /// <param name="projectId">Identificativo del progetto</param>
        /// <param name="record">Chiamata da registrare</param>
        public void AppendCall(string projectId, TranslationCallRecord record)
        {
            lock (this._lock)
            {
                TranslationJsonFile.AppendLine(Path.Combine(this._root, projectId, CALLS_FILE), record);
            }
        }

        /// <summary>
        /// Legge il registro delle chiamate del progetto; una riga illeggibile, come l'ultima troncata da una chiusura brusca, si salta
        /// </summary>
        /// <param name="projectId">Identificativo del progetto</param>
        /// <returns>Chiamate nell'ordine del registro</returns>
        public List<TranslationCallRecord> ReadCalls(string projectId)
        {
            lock (this._lock)
            {
                return TranslationJsonFile.ReadLines<TranslationCallRecord>(Path.Combine(this._root, projectId, CALLS_FILE));
            }
        }

        /// <summary>
        /// Cartella di un progetto
        /// </summary>
        /// <param name="id">Identificativo del progetto</param>
        /// <returns>Percorso della cartella, anche se non esiste</returns>
        public string GetFolder(string id)
        {
            return Path.Combine(this._root, id);
        }

        #endregion
    }
}
