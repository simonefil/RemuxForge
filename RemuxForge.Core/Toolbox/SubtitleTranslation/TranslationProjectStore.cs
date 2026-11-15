using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        private const string PROJECT_FILE = "project.json";

        /// <summary>
        /// Registro delle chiamate del progetto
        /// </summary>
        private const string CALLS_FILE = "calls.jsonl";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Opzioni JSON dei file della traduzione: nomi camelCase, enum col nome del membro, date con offset
        /// </summary>
        private static readonly JsonSerializerOptions s_options = CreateOptions(true);

        /// <summary>
        /// Opzioni JSON delle righe del registro, una per riga
        /// </summary>
        private static readonly JsonSerializerOptions s_lineOptions = CreateOptions(false);

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
                try
                {
                    project = JsonSerializer.Deserialize<TranslationProject>(File.ReadAllText(path, Encoding.UTF8), s_options);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.projectUnreadable", path), ex);
                }
            }

            if (project == null)
                throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.projectUnreadable", path));
            if (project.SchemaVersion > TranslationProject.SCHEMA_VERSION)
                throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.projectTooNew", path));

            return project;
        }

        /// <summary>
        /// Crea un progetto nuovo, non ancora salvato
        /// </summary>
        /// <returns>Progetto con identificativo e date</returns>
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

            project.UpdatedAt = DateTimeOffset.Now;
            string folder = Path.Combine(this._root, project.Id);
            string path = Path.Combine(folder, PROJECT_FILE);
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string json = JsonSerializer.Serialize(project, s_options);

            lock (this._lock)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(true);
                    }

                    File.Move(tempPath, path, true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // La scrittura ha già propagato l'errore principale; la pulizia resta best-effort
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // La scrittura ha già propagato l'errore principale; la pulizia resta best-effort
                    }
                }
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
            string folder = Path.Combine(this._root, projectId);
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, s_lineOptions) + "\n");

            lock (this._lock)
            {
                Directory.CreateDirectory(folder);
                using (FileStream stream = new FileStream(Path.Combine(folder, CALLS_FILE), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (stream.Length > 0)
                    {
                        stream.Seek(-1, SeekOrigin.End);
                        if (stream.ReadByte() != '\n')
                            stream.WriteByte((byte)'\n');
                    }

                    stream.Seek(0, SeekOrigin.End);
                    stream.Write(line, 0, line.Length);
                    stream.Flush(true);
                }
            }
        }

        /// <summary>
        /// Legge il registro delle chiamate del progetto; una riga illeggibile, come l'ultima troncata da una chiusura brusca, si salta
        /// </summary>
        /// <param name="projectId">Identificativo del progetto</param>
        /// <returns>Chiamate nell'ordine del registro</returns>
        public List<TranslationCallRecord> ReadCalls(string projectId)
        {
            string path = Path.Combine(this._root, projectId, CALLS_FILE);
            List<TranslationCallRecord> calls = new List<TranslationCallRecord>();
            string[] lines;

            lock (this._lock)
            {
                if (!File.Exists(path))
                    return calls;
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line.Trim()))
                    continue;

                try
                {
                    TranslationCallRecord record = JsonSerializer.Deserialize<TranslationCallRecord>(line, s_lineOptions);
                    if (record != null)
                        calls.Add(record);
                }
                catch (JsonException)
                {
                    // Riga incompleta: il registro è in sola aggiunta e le righe intere restano valide
                }
            }

            return calls;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Crea le opzioni JSON dei file della traduzione
        /// </summary>
        /// <param name="indented">True per i file interi, false per le righe del registro</param>
        /// <returns>Opzioni del serializzatore</returns>
        private static JsonSerializerOptions CreateOptions(bool indented)
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = indented;
            options.PropertyNameCaseInsensitive = true;
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.Converters.Add(new JsonStringEnumConverter(null, false));
            return options;
        }

        #endregion
    }
}
