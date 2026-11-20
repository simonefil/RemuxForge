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
    public class TranslationProjectStore : TranslationStoreBase
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

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella di configurazione di RemuxForge</param>
        public TranslationProjectStore(string configFolder)
            : base(configFolder, FOLDER_NAME)
        {
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
        /// Legge un progetto; un file guasto, di una versione più recente o di una precedente che non si apre più dà
        /// InvalidDataException
        /// </summary>
        /// <param name="id">Identificativo del progetto</param>
        /// <returns>Progetto letto</returns>
        public TranslationProject Load(string id)
        {
            string path = Path.Combine(this.GetFolder(id), PROJECT_FILE);
            TranslationProject project;

            lock (this._lock)
            {
                project = TranslationJsonFile.ReadVersioned<TranslationProject>(path, TranslationProject.SCHEMA_VERSION, AppText.F("toolbox.subtitleTranslation.projectTooNew", path),
                    AppText.F("toolbox.subtitleTranslation.projectOutdated", path), AppText.F("toolbox.subtitleTranslation.projectUnreadable", path));
            }

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

            lock (this._lock)
            {
                project.UpdatedAt = DateTimeOffset.Now;
                TranslationJsonFile.Write(Path.Combine(this.GetFolder(project.Id), PROJECT_FILE), project);
            }
        }

        #endregion
    }
}
