using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Toolbox.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Archivio di un progetto di traduzione: progetto, job, registri, sorgenti, glossario e file tradotti parziali in uno .zip
    /// col manifest degli hash. L'import controlla tutto prima di scrivere, estrae in una cartella temporanea dentro la
    /// configurazione, scrive i sorgenti nella cartella scelta e sposta le cartelle al loro posto
    /// </summary>
    public sealed class TranslationProjectArchive
    {
        #region Costanti

        /// <summary>
        /// Estensione dell'archivio
        /// </summary>
        public const string EXTENSION = ".rf-translation.zip";

        /// <summary>
        /// Voce del manifest
        /// </summary>
        private const string MANIFEST_ENTRY = "manifest.json";

        /// <summary>
        /// Cartella del progetto nell'archivio
        /// </summary>
        private const string PROJECT_FOLDER = "project/";

        /// <summary>
        /// Cartella dei job nell'archivio
        /// </summary>
        private const string JOBS_FOLDER = "jobs/";

        /// <summary>
        /// Cartella dei sorgenti nell'archivio
        /// </summary>
        private const string SOURCES_FOLDER = "sources/";

        /// <summary>
        /// Cartella dei file tradotti parziali nell'archivio
        /// </summary>
        private const string PARTIAL_FOLDER = "partial/";

        /// <summary>
        /// Glossario come file a sé nell'archivio
        /// </summary>
        private const string GLOSSARY_ENTRY = "glossary/glossary.json";

        /// <summary>
        /// Prefisso della cartella temporanea dell'import dentro la configurazione
        /// </summary>
        private const string IMPORT_FOLDER_PREFIX = ".translation-import-";

        /// <summary>
        /// Tetto del manifest in byte
        /// </summary>
        private const long MAX_MANIFEST_BYTES = 8L * 1024 * 1024;

        /// <summary>
        /// Tetto delle voci dell'archivio
        /// </summary>
        private const int MAX_ENTRIES = 10000;

        /// <summary>
        /// Tetto della somma delle dimensioni delle voci, tutte lette in memoria dal controllo
        /// </summary>
        private const long MAX_TOTAL_BYTES = 512L * 1024 * 1024;

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Identificativo di progetto e job come lo genera RemuxForge: GUID in forma N
        /// </summary>
        private static readonly Regex s_idPattern = new Regex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Identificativo di un file del progetto come lo genera RemuxForge: f e il numero progressivo
        /// </summary>
        private static readonly Regex s_fileIdPattern = new Regex(@"\Af[0-9]+\z", RegexOptions.CultureInvariant);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Progetti su disco
        /// </summary>
        private readonly TranslationProjectStore _projects;

        /// <summary>
        /// Job su disco
        /// </summary>
        private readonly TranslationJobStore _jobs;

        /// <summary>
        /// Cartella di configurazione, che ospita la cartella temporanea dell'import
        /// </summary>
        private readonly string _configFolder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="projects">Progetti su disco</param>
        /// <param name="jobs">Job su disco</param>
        /// <param name="configFolder">Cartella di configurazione</param>
        public TranslationProjectArchive(TranslationProjectStore projects, TranslationJobStore jobs, string configFolder)
        {
            this._projects = projects;
            this._jobs = jobs;
            this._configFolder = configFolder;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive l'archivio di un progetto col job attivo nella cartella scelta, senza sovrascrivere: il progetto tiene solo i file
        /// del job, i percorsi assoluti dei sorgenti diventano relativi all'archivio, i parziali si ricompongono dalle battute
        /// accettate. Un sorgente mancante o cambiato dà l'eccezione col motivo: l'import vuole tutti i sorgenti del progetto
        /// </summary>
        /// <param name="project">Progetto salvato, col job attivo</param>
        /// <param name="folder">Cartella di destinazione</param>
        /// <param name="name">Nome dell'archivio senza estensione</param>
        /// <returns>File scritto</returns>
        public string Export(TranslationProject project, string folder, string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            TranslationProjectArchiveManifest manifest = new TranslationProjectArchiveManifest();
            manifest.AppVersion = Utils.GetVersion();
            manifest.ProjectId = project.Id;
            manifest.ProjectName = project.Name;
            manifest.JobIds = new List<string>(project.JobIds);
            manifest.ActiveJobId = project.ActiveJobId;
            manifest.SourceLanguage = project.SourceLanguage;
            manifest.TargetLanguage = project.TargetLanguage;

            TranslationJob job = this._jobs.Load(project.ActiveJobId);
            List<TranslationProjectFile> files = project.Files.FindAll(f => job.FileIds.Contains(f.Id));
            string path = new OutputPathAllocator(new string[0]).Reserve(Path.Combine(folder, name + EXTENSION));
            using (StagedOutputFile staged = new StagedOutputFile(path))
            {
                using (FileStream stream = new FileStream(staged.TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    // Copia del progetto coi soli file del job e i percorsi dei sorgenti dentro l'archivio
                    TranslationProject copy = TranslationJsonFile.Parse<TranslationProject>(TranslationJsonFile.Serialize(project), project.Id);
                    copy.Files.RemoveAll(f => !job.FileIds.Contains(f.Id));
                    Dictionary<string, TranslationJobFileState> activeStates = new Dictionary<string, TranslationJobFileState>(StringComparer.Ordinal);
                    foreach (TranslationProjectFile file in copy.Files)
                    {
                        string archivePath = SOURCES_FOLDER + file.Id + "/" + Path.GetFileName(file.Path);
                        if (!File.Exists(file.Path))
                            throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.sourceMissing", file.Path));
                        byte[] source = File.ReadAllBytes(file.Path);
                        if (ComputeSha256(source) != file.SourceSha256)
                            throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.sourceChanged", file.Path));

                        this.AddEntry(zip, manifest, archivePath, source, TranslationArchiveRole.Source);
                        manifest.Sources.Add(new TranslationArchiveSource { FileId = file.Id, ArchivePath = archivePath, OriginalFileName = Path.GetFileName(file.Path), Sha256 = file.SourceSha256 });
                        file.Path = archivePath;
                    }

                    this.AddEntry(zip, manifest, PROJECT_FOLDER + TranslationProjectStore.PROJECT_FILE, TranslationJsonFile.Serialize(copy), TranslationArchiveRole.Project);
                    this.AddFile(zip, manifest, PROJECT_FOLDER + TranslationProjectStore.CALLS_FILE, Path.Combine(this._projects.GetFolder(project.Id), TranslationProjectStore.CALLS_FILE),
                        TranslationArchiveRole.ProjectCalls);

                    foreach (string jobId in project.JobIds)
                    {
                        string jobFolder = this._jobs.GetFolder(jobId);
                        string prefix = JOBS_FOLDER + jobId + "/";
                        this.AddFile(zip, manifest, prefix + TranslationJobStore.JOB_FILE, Path.Combine(jobFolder, TranslationJobStore.JOB_FILE), TranslationArchiveRole.Job);
                        this.AddFile(zip, manifest, prefix + TranslationJobStore.CALLS_FILE, Path.Combine(jobFolder, TranslationJobStore.CALLS_FILE), TranslationArchiveRole.JobCalls);
                        foreach (TranslationProjectFile file in files)
                        {
                            string statePath = Path.Combine(jobFolder, TranslationJobStore.FILES_FOLDER, file.Id + ".json");
                            this.AddFile(zip, manifest, prefix + TranslationJobStore.FILES_FOLDER + "/" + file.Id + ".json", statePath, TranslationArchiveRole.JobFile);
                            if (jobId == project.ActiveJobId && File.Exists(statePath))
                                activeStates[file.Id] = this._jobs.LoadFile(jobId, file.Id);
                        }
                    }

                    // Parziali del job attivo: le battute accettate col testo tradotto, le altre col testo d'origine
                    foreach (TranslationProjectFile file in files)
                    {
                        TranslationJobFileState state;
                        if (!activeStates.TryGetValue(file.Id, out state))
                            continue;

                        SubtitleTranslationDocument document = SubtitleTranslationDocument.Read(File.ReadAllBytes(file.Path), file.Format, file.Encoding);
                        Dictionary<int, string> translated = new Dictionary<int, string>();
                        foreach (TranslatedCueState translation in state.Translations)
                        {
                            if (translation.Text != null)
                                translated[translation.Id] = translation.Text;
                        }

                        this.AddEntry(zip, manifest, PARTIAL_FOLDER + file.Id + "/" + TranslationExporter.GetOutputName(file.Path, project.SourceLanguage, project.TargetLanguage), document.Write(translated), TranslationArchiveRole.Partial);
                    }

                    if (project.Glossary != null)
                        this.AddEntry(zip, manifest, GLOSSARY_ENTRY, TranslationJsonFile.Serialize(project.Glossary), TranslationArchiveRole.Glossary);

                    ZipArchiveEntry entry = zip.CreateEntry(MANIFEST_ENTRY, CompressionLevel.Optimal);
                    using (Stream target = entry.Open())
                    {
                        byte[] bytes = TranslationJsonFile.Serialize(manifest);
                        target.Write(bytes, 0, bytes.Length);
                    }
                }

                staged.Commit();
            }

            return path;
        }

        /// <summary>
        /// Controlla un archivio senza scrivere niente: voci, manifest, hash, sorgenti e coerenza dei job
        /// </summary>
        /// <param name="zipPath">Archivio scelto</param>
        /// <returns>Contenuto controllato, col progetto già presente se c'è</returns>
        public TranslationArchiveInspection Inspect(string zipPath)
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                return this.Check(zip, zipPath);
            }
        }

        /// <summary>
        /// Importa un archivio: i sorgenti nella cartella scelta, progetto e job nella configurazione. Un progetto con lo stesso
        /// identificativo si sostituisce solo con la conferma; un job con l'identificativo di un altro progetto prende un id nuovo;
        /// un job rimasto in corso torna in uno stato da cui il giro prosegue. Il progetto sostituito si cancella solo a import riuscito
        /// </summary>
        /// <param name="zipPath">Archivio scelto</param>
        /// <param name="sourceFolder">Cartella dei sorgenti</param>
        /// <param name="replaceExisting">True se l'utente ha confermato la sostituzione del progetto presente</param>
        /// <returns>Progetto importato</returns>
        public TranslationProject Import(string zipPath, string sourceFolder, bool replaceExisting)
        {
            string temp = Path.Combine(this._configFolder, IMPORT_FOLDER_PREFIX + Guid.NewGuid().ToString("N"));
            List<string> written = new List<string>();
            List<KeyValuePair<string, string>> parked = new List<KeyValuePair<string, string>>();
            List<string> installed = new List<string>();
            bool completed = false;

            try
            {
                TranslationArchiveInspection inspection;
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    inspection = this.Check(zip, zipPath);
                }
                if (inspection.Existing != null && !replaceExisting)
                    throw new InvalidOperationException(AppText.F("toolbox.subtitleTranslation.archive.projectExists", inspection.Existing.Name));

                TranslationProject project = inspection.Project;
                Dictionary<string, string> jobIds = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string jobId in inspection.Manifest.JobIds)
                {
                    string id = jobId;
                    if (this._jobs.Exists(jobId) && !(inspection.Existing != null && inspection.Existing.JobIds.Contains(jobId)))
                        id = Guid.NewGuid().ToString("N");
                    jobIds[jobId] = id;
                }

                // Sorgenti nella cartella scelta: stesso nome e stesso hash si usano, altrimenti il suffisso della politica comune
                OutputPathAllocator allocator = new OutputPathAllocator(new string[0]);
                foreach (TranslationProjectFile file in project.Files)
                {
                    TranslationArchiveSource source = inspection.Manifest.Sources.Find(s => s.FileId == file.Id);
                    string desired = Path.Combine(sourceFolder, source.OriginalFileName);
                    if (File.Exists(desired) && ComputeSha256(File.ReadAllBytes(desired)) == source.Sha256 && !project.Files.Exists(f => !ReferenceEquals(f, file) && f.Path == desired))
                    {
                        file.Path = desired;
                        continue;
                    }

                    string target = allocator.Reserve(desired);
                    using (StagedOutputFile staged = new StagedOutputFile(target))
                    {
                        File.WriteAllBytes(staged.TempPath, inspection.Contents[source.ArchivePath]);
                        staged.Commit();
                    }
                    written.Add(target);
                    file.Path = target;
                }

                // Cartelle estratte nella configurazione, coi job rinominati e quelli rimasti in corso fermati
                string projectTemp = Path.Combine(temp, "project");
                Directory.CreateDirectory(projectTemp);
                File.WriteAllBytes(Path.Combine(projectTemp, TranslationProjectStore.CALLS_FILE), inspection.Contents[PROJECT_FOLDER + TranslationProjectStore.CALLS_FILE]);
                project.JobIds = new List<string>();
                foreach (string jobId in inspection.Manifest.JobIds)
                    project.JobIds.Add(jobIds[jobId]);
                project.ActiveJobId = string.IsNullOrEmpty(project.ActiveJobId) ? project.ActiveJobId : jobIds[project.ActiveJobId];
                TranslationJsonFile.Write(Path.Combine(projectTemp, TranslationProjectStore.PROJECT_FILE), project);

                foreach (TranslationJob job in inspection.Jobs)
                {
                    string jobTemp = Path.Combine(temp, "jobs", jobIds[job.Id]);
                    string prefix = JOBS_FOLDER + job.Id + "/";
                    Directory.CreateDirectory(Path.Combine(jobTemp, TranslationJobStore.FILES_FOLDER));
                    foreach (TranslationArchiveFile entry in inspection.Manifest.Files)
                    {
                        if (entry.Role == TranslationArchiveRole.JobFile && entry.Path.StartsWith(prefix, StringComparison.Ordinal))
                            File.WriteAllBytes(Path.Combine(jobTemp, TranslationJobStore.FILES_FOLDER, Path.GetFileName(entry.Path)), inspection.Contents[entry.Path]);
                    }
                    File.WriteAllBytes(Path.Combine(jobTemp, TranslationJobStore.CALLS_FILE), inspection.Contents[prefix + TranslationJobStore.CALLS_FILE]);

                    job.Id = jobIds[job.Id];
                    job.RecoverAfterCrash();
                    TranslationJsonFile.Write(Path.Combine(jobTemp, TranslationJobStore.JOB_FILE), job);
                }

                // Il progetto presente coi suoi job si sposta nel temporaneo e lascia il posto ai nuovi; i suoi sorgenti restano dove sono
                string projectFolder = this._projects.GetFolder(project.Id);
                if (inspection.Existing != null)
                {
                    string existingTemp = Path.Combine(temp, "existing");
                    Directory.CreateDirectory(Path.Combine(existingTemp, "jobs"));
                    if (Directory.Exists(projectFolder))
                    {
                        Directory.Move(projectFolder, Path.Combine(existingTemp, "project"));
                        parked.Add(new KeyValuePair<string, string>(projectFolder, Path.Combine(existingTemp, "project")));
                    }
                    foreach (string jobId in inspection.Existing.JobIds)
                    {
                        string jobFolder = this._jobs.GetFolder(jobId);
                        if (!Directory.Exists(jobFolder))
                            continue;

                        Directory.Move(jobFolder, Path.Combine(existingTemp, "jobs", jobId));
                        parked.Add(new KeyValuePair<string, string>(jobFolder, Path.Combine(existingTemp, "jobs", jobId)));
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(projectFolder));
                Directory.CreateDirectory(this._jobs.Root);
                Directory.Move(projectTemp, projectFolder);
                installed.Add(projectFolder);
                foreach (string jobId in project.JobIds)
                {
                    Directory.Move(Path.Combine(temp, "jobs", jobId), this._jobs.GetFolder(jobId));
                    installed.Add(this._jobs.GetFolder(jobId));
                }

                TranslationProject imported = this._projects.Load(project.Id);
                completed = true;
                return imported;
            }
            finally
            {
                // Import fallito: via le cartelle installate e i sorgenti scritti, il progetto presente torna al suo posto
                bool restored = true;
                if (!completed)
                {
                    foreach (string folder in installed)
                    {
                        try
                        {
                            Directory.Delete(folder, true);
                        }
                        catch (Exception ex)
                        {
                            if (!(ex is IOException || ex is UnauthorizedAccessException))
                                throw;
                        }
                    }

                    for (int i = parked.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            Directory.Move(parked[i].Value, parked[i].Key);
                        }
                        catch (Exception ex)
                        {
                            if (!(ex is IOException || ex is UnauthorizedAccessException))
                                throw;
                            restored = false;
                        }
                    }

                    foreach (string path in written)
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ex)
                        {
                            if (!(ex is IOException || ex is UnauthorizedAccessException))
                                throw;
                        }
                    }
                }

                // Il temporaneo resta se tiene ancora una parte del progetto presente che non è tornata al suo posto
                if (restored)
                {
                    try
                    {
                        if (Directory.Exists(temp))
                            Directory.Delete(temp, true);
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is UnauthorizedAccessException))
                            throw;
                    }
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Controlli dell'import nell'ordine: voci, manifest, hash, sorgenti, coerenza; il primo che fallisce ferma l'import. Le voci
        /// si leggono una volta sola, entro le dimensioni del manifest
        /// </summary>
        /// <param name="zip">Archivio aperto</param>
        /// <param name="zipPath">Archivio, per i messaggi</param>
        /// <returns>Contenuto controllato, coi byte delle voci</returns>
        private TranslationArchiveInspection Check(ZipArchive zip, string zipPath)
        {
            string invalid = AppText.F("toolbox.subtitleTranslation.archive.invalid", Path.GetFileName(zipPath));
            ZipArchiveEntry manifestEntry = zip.GetEntry(MANIFEST_ENTRY);
            if (manifestEntry == null || zip.Entries.Count > MAX_ENTRIES)
                throw new InvalidDataException(invalid);

            TranslationProjectArchiveManifest manifest = TranslationJsonFile.Parse<TranslationProjectArchiveManifest>(ReadEntry(manifestEntry, MAX_MANIFEST_BYTES, invalid), invalid);
            if (manifest.Files == null || manifest.Sources == null || manifest.JobIds == null || manifest.FileSchemaVersions == null || manifest.Files.Count > MAX_ENTRIES ||
                manifest.Files.Exists(f => f == null || string.IsNullOrEmpty(f.Path) || f.SizeBytes < 0 || f.SizeBytes > MAX_TOTAL_BYTES) ||
                manifest.Sources.Exists(s => s == null) || manifest.JobIds.Exists(j => j == null || !s_idPattern.IsMatch(j)))
                throw new InvalidDataException(invalid);

            long total = 0;
            foreach (TranslationArchiveFile file in manifest.Files)
                total += file.SizeBytes;
            if (total > MAX_TOTAL_BYTES)
                throw new InvalidDataException(invalid);

            // Voci: niente percorsi assoluti, niente risalite, niente doppioni, niente voci fuori dal manifest
            string root = Path.GetFullPath(Path.Combine(this._configFolder, IMPORT_FOLDER_PREFIX + "check")) + Path.DirectorySeparatorChar;
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;
                string full = Path.GetFullPath(Path.Combine(root, name));
                if (Path.IsPathRooted(name) || name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("\\", StringComparison.Ordinal) ||
                    Array.IndexOf(name.Replace('\\', '/').Split('/'), "..") >= 0 || !full.StartsWith(root, StringComparison.Ordinal) || !names.Add(name))
                    throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.entryUnsafe", name));

                if (name != MANIFEST_ENTRY && !manifest.Files.Exists(f => f.Path == name))
                    throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.entryUnlisted", name));
            }

            // Manifest: tipo giusto e versioni note
            if (manifest.Kind != TranslationProjectArchiveManifest.KIND)
                throw new InvalidDataException(invalid);
            if (manifest.SchemaVersion > TranslationProjectArchiveManifest.SCHEMA_VERSION ||
                manifest.FileSchemaVersions.Project > TranslationProject.SCHEMA_VERSION || manifest.FileSchemaVersions.Job > TranslationJob.SCHEMA_VERSION ||
                manifest.FileSchemaVersions.JobFile > TranslationJobFileState.SCHEMA_VERSION || manifest.FileSchemaVersions.Calls > TranslationCallRecord.SCHEMA_VERSION ||
                manifest.FileSchemaVersions.Glossary > TranslationArchiveSchemaVersions.GLOSSARY_SCHEMA_VERSION)
                throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.tooNew", Path.GetFileName(zipPath)));

            // Hash e dimensione di ogni voce: la dimensione dichiarata dalla voce prima di aprirla, quella letta entro il manifest
            Dictionary<string, byte[]> contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (TranslationArchiveFile file in manifest.Files)
            {
                string mismatch = AppText.F("toolbox.subtitleTranslation.archive.hashMismatch", file.Path);
                ZipArchiveEntry entry = zip.GetEntry(file.Path);
                if (entry == null || entry.Length != file.SizeBytes)
                    throw new InvalidDataException(mismatch);

                byte[] bytes = ReadEntry(entry, file.SizeBytes, mismatch);
                if (bytes.LongLength != file.SizeBytes || ComputeSha256(bytes) != file.Sha256)
                    throw new InvalidDataException(mismatch);
                contents[file.Path] = bytes;
            }

            byte[] projectBytes;
            if (!contents.TryGetValue(PROJECT_FOLDER + TranslationProjectStore.PROJECT_FILE, out projectBytes) || !contents.ContainsKey(PROJECT_FOLDER + TranslationProjectStore.CALLS_FILE))
                throw new InvalidDataException(invalid);
            TranslationProject project = TranslationJsonFile.Parse<TranslationProject>(projectBytes, invalid);
            if (project.SchemaVersion > TranslationProject.SCHEMA_VERSION)
                throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.tooNew", Path.GetFileName(zipPath)));

            // Identificativi nel formato in cui li genera RemuxForge, perché diventano nomi di cartelle e di file
            if (project.Files == null || project.JobIds == null || project.Files.Exists(f => f == null || f.Id == null || !s_fileIdPattern.IsMatch(f.Id)) ||
                project.Id == null || !s_idPattern.IsMatch(project.Id) || project.JobIds.Exists(j => j == null || !s_idPattern.IsMatch(j)))
                throw new InvalidDataException(invalid);
            HashSet<string> fileIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (TranslationProjectFile file in project.Files)
            {
                if (!fileIds.Add(file.Id))
                    throw new InvalidDataException(invalid);
            }

            // Sorgenti: ognuno con l'hash del progetto e un nome di file semplice
            foreach (TranslationProjectFile file in project.Files)
            {
                TranslationArchiveSource source = manifest.Sources.Find(s => s.FileId == file.Id);
                byte[] bytes;
                if (source == null || string.IsNullOrEmpty(source.ArchivePath) || !contents.TryGetValue(source.ArchivePath, out bytes) || ComputeSha256(bytes) != file.SourceSha256 ||
                    source.Sha256 != file.SourceSha256 || string.IsNullOrEmpty(source.OriginalFileName) || source.OriginalFileName == "." || source.OriginalFileName == ".." ||
                    source.OriginalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || source.OriginalFileName.IndexOfAny(new char[] { '/', '\\' }) >= 0)
                    throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.sourceInvalid", file.Id));
            }

            // Coerenza: job del progetto, ognuno coi suoi file fra quelli del progetto e lo stato di ognuno sugli stessi sorgenti
            if (manifest.ProjectId != project.Id || project.JobIds.Count != manifest.JobIds.Count || !project.JobIds.TrueForAll(j => manifest.JobIds.Contains(j)) ||
                new HashSet<string>(manifest.JobIds, StringComparer.Ordinal).Count != manifest.JobIds.Count ||
                (!string.IsNullOrEmpty(project.ActiveJobId) && !manifest.JobIds.Contains(project.ActiveJobId)))
                throw new InvalidDataException(invalid);

            TranslationArchiveInspection inspection = new TranslationArchiveInspection(manifest, project, contents);
            foreach (string jobId in manifest.JobIds)
            {
                string prefix = JOBS_FOLDER + jobId + "/";
                byte[] jobBytes;
                if (!contents.TryGetValue(prefix + TranslationJobStore.JOB_FILE, out jobBytes) || !contents.ContainsKey(prefix + TranslationJobStore.CALLS_FILE))
                    throw new InvalidDataException(invalid);

                TranslationJob job = TranslationJsonFile.Parse<TranslationJob>(jobBytes, invalid);
                if (job.SchemaVersion > TranslationJob.SCHEMA_VERSION)
                    throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.tooNew", Path.GetFileName(zipPath)));
                if (job.Id != jobId || job.ProjectId != project.Id || job.FileIds == null || new HashSet<string>(job.FileIds, StringComparer.Ordinal).Count != job.FileIds.Count ||
                    !job.FileIds.TrueForAll(id => id != null && fileIds.Contains(id) && contents.ContainsKey(prefix + TranslationJobStore.FILES_FOLDER + "/" + id + ".json")))
                    throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.jobMismatch", jobId));

                foreach (TranslationArchiveFile file in manifest.Files)
                {
                    if (file.Role != TranslationArchiveRole.JobFile || !file.Path.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    TranslationJobFileState state = TranslationJsonFile.Parse<TranslationJobFileState>(contents[file.Path], invalid);
                    if (state.SchemaVersion > TranslationJobFileState.SCHEMA_VERSION)
                        throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.tooNew", Path.GetFileName(zipPath)));
                    TranslationProjectFile projectFile = state.FileId != null ? project.Files.Find(f => f.Id == state.FileId) : null;
                    if (projectFile == null || state.SourceSha256 != projectFile.SourceSha256 || file.Path != prefix + TranslationJobStore.FILES_FOLDER + "/" + state.FileId + ".json" ||
                        state.Cues == null || state.Blocks == null || state.Translations == null || state.CollectedNewTerms == null)
                        throw new InvalidDataException(AppText.F("toolbox.subtitleTranslation.archive.jobMismatch", jobId));
                }
                inspection.Jobs.Add(job);
            }

            try
            {
                inspection.Existing = this._projects.Load(project.Id);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException))
                    throw;
                inspection.Existing = null;
            }

            return inspection;
        }

        /// <summary>
        /// Aggiunge una voce all'archivio e al manifest
        /// </summary>
        /// <param name="zip">Archivio in scrittura</param>
        /// <param name="manifest">Manifest</param>
        /// <param name="path">Percorso nell'archivio</param>
        /// <param name="bytes">Contenuto</param>
        /// <param name="role">Ruolo della voce</param>
        private void AddEntry(ZipArchive zip, TranslationProjectArchiveManifest manifest, string path, byte[] bytes, TranslationArchiveRole role)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (Stream target = entry.Open())
            {
                target.Write(bytes, 0, bytes.Length);
            }

            manifest.Files.Add(new TranslationArchiveFile { Path = path, Sha256 = ComputeSha256(bytes), SizeBytes = bytes.LongLength, Role = role });
        }

        /// <summary>
        /// Aggiunge all'archivio un file della configurazione, vuoto se non c'è ancora, come il registro senza chiamate
        /// </summary>
        /// <param name="zip">Archivio in scrittura</param>
        /// <param name="manifest">Manifest</param>
        /// <param name="path">Percorso nell'archivio</param>
        /// <param name="source">File sul disco</param>
        /// <param name="role">Ruolo della voce</param>
        private void AddFile(ZipArchive zip, TranslationProjectArchiveManifest manifest, string path, string source, TranslationArchiveRole role)
        {
            if (role == TranslationArchiveRole.JobFile && !File.Exists(source))
                return;

            this.AddEntry(zip, manifest, path, File.Exists(source) ? File.ReadAllBytes(source) : new byte[0], role);
        }

        /// <summary>
        /// Legge una voce dell'archivio senza decomprimere oltre il tetto, contro una voce che si gonfia più di quanto dichiara; una
        /// voce guasta, come quella col CRC diverso dai dati, dà lo stesso messaggio
        /// </summary>
        /// <param name="entry">Voce</param>
        /// <param name="limit">Byte ammessi</param>
        /// <param name="message">Messaggio per la voce guasta o oltre il tetto</param>
        /// <returns>Byte della voce</returns>
        private static byte[] ReadEntry(ZipArchiveEntry entry, long limit, string message)
        {
            if (entry.Length > limit)
                throw new InvalidDataException(message);

            byte[] buffer = new byte[81920];
            try
            {
                using (Stream source = entry.Open())
                using (MemoryStream target = new MemoryStream())
                {
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (target.Length + read > limit)
                            throw new InvalidDataException(message);
                        target.Write(buffer, 0, read);
                    }
                    return target.ToArray();
                }
            }
            catch (InvalidDataException ex)
            {
                // Anche la voce oltre il tetto passa di qui, con lo stesso messaggio
                throw new InvalidDataException(message, ex);
            }
        }

        /// <summary>
        /// SHA-256 in esadecimale minuscolo
        /// </summary>
        /// <param name="bytes">Contenuto</param>
        /// <returns>Hash</returns>
        private static string ComputeSha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        #endregion
    }

    /// <summary>
    /// Ruolo di una voce dell'archivio
    /// </summary>
    public enum TranslationArchiveRole
    {
        /// <summary>project.json</summary>
        Project,

        /// <summary>Registro delle chiamate del progetto</summary>
        ProjectCalls,

        /// <summary>job.json</summary>
        Job,

        /// <summary>Stato di un file del job</summary>
        JobFile,

        /// <summary>Registro delle chiamate del job</summary>
        JobCalls,

        /// <summary>Sottotitolo sorgente</summary>
        Source,

        /// <summary>File tradotto parziale</summary>
        Partial,

        /// <summary>Glossario come file a sé</summary>
        Glossary
    }

    /// <summary>
    /// Manifest dell'archivio: versioni, job inclusi, hash di ogni voce e sorgenti
    /// </summary>
    public sealed class TranslationProjectArchiveManifest
    {
        #region Costanti

        /// <summary>
        /// Versione del formato dell'archivio
        /// </summary>
        public const int SCHEMA_VERSION = 1;

        /// <summary>
        /// Tipo dell'archivio
        /// </summary>
        public const string KIND = "remuxforge.translationArchive";

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationProjectArchiveManifest()
        {
            this.SchemaVersion = SCHEMA_VERSION;
            this.Kind = KIND;
            this.AppVersion = "";
            this.CreatedAt = DateTimeOffset.Now;
            this.ProjectId = "";
            this.ProjectName = "";
            this.JobIds = new List<string>();
            this.ActiveJobId = null;
            this.SourceLanguage = "";
            this.TargetLanguage = "";
            this.FileSchemaVersions = new TranslationArchiveSchemaVersions();
            this.Files = new List<TranslationArchiveFile>();
            this.Sources = new List<TranslationArchiveSource>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Versione del formato
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Tipo dell'archivio
        /// </summary>
        public string Kind { get; set; }

        /// <summary>
        /// Versione di RemuxForge che ha esportato
        /// </summary>
        public string AppVersion { get; set; }

        /// <summary>
        /// Data dell'export
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>
        /// Progetto
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// Nome del progetto
        /// </summary>
        public string ProjectName { get; set; }

        /// <summary>
        /// Job inclusi
        /// </summary>
        public List<string> JobIds { get; set; }

        /// <summary>
        /// Job attivo
        /// </summary>
        public string ActiveJobId { get; set; }

        /// <summary>
        /// Lingua d'origine
        /// </summary>
        public string SourceLanguage { get; set; }

        /// <summary>
        /// Lingua d'arrivo
        /// </summary>
        public string TargetLanguage { get; set; }

        /// <summary>
        /// Versioni dei file interni
        /// </summary>
        public TranslationArchiveSchemaVersions FileSchemaVersions { get; set; }

        /// <summary>
        /// Voci dell'archivio tranne il manifest
        /// </summary>
        public List<TranslationArchiveFile> Files { get; set; }

        /// <summary>
        /// Sorgenti per file del progetto
        /// </summary>
        public List<TranslationArchiveSource> Sources { get; set; }

        #endregion
    }

    /// <summary>
    /// Versioni dei file interni dell'archivio
    /// </summary>
    public sealed class TranslationArchiveSchemaVersions
    {
        #region Costanti

        /// <summary>
        /// Versione del formato del glossario come file a sé
        /// </summary>
        public const int GLOSSARY_SCHEMA_VERSION = 1;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationArchiveSchemaVersions()
        {
            this.Project = TranslationProject.SCHEMA_VERSION;
            this.Job = TranslationJob.SCHEMA_VERSION;
            this.JobFile = TranslationJobFileState.SCHEMA_VERSION;
            this.Calls = TranslationCallRecord.SCHEMA_VERSION;
            this.Glossary = GLOSSARY_SCHEMA_VERSION;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// project.json
        /// </summary>
        public int Project { get; set; }

        /// <summary>
        /// job.json
        /// </summary>
        public int Job { get; set; }

        /// <summary>
        /// Stati dei file del job
        /// </summary>
        public int JobFile { get; set; }

        /// <summary>
        /// Righe dei registri
        /// </summary>
        public int Calls { get; set; }

        /// <summary>
        /// Glossario come file a sé
        /// </summary>
        public int Glossary { get; set; }

        #endregion
    }

    /// <summary>
    /// Voce dell'archivio con hash e dimensione
    /// </summary>
    public sealed class TranslationArchiveFile
    {
        #region Proprietà

        /// <summary>
        /// Percorso nell'archivio
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// SHA-256
        /// </summary>
        public string Sha256 { get; set; }

        /// <summary>
        /// Dimensione in byte
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// Ruolo
        /// </summary>
        public TranslationArchiveRole Role { get; set; }

        #endregion
    }

    /// <summary>
    /// Sorgente di un file del progetto nell'archivio
    /// </summary>
    public sealed class TranslationArchiveSource
    {
        #region Proprietà

        /// <summary>
        /// File del progetto
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Percorso nell'archivio
        /// </summary>
        public string ArchivePath { get; set; }

        /// <summary>
        /// Nome originale del file
        /// </summary>
        public string OriginalFileName { get; set; }

        /// <summary>
        /// SHA-256
        /// </summary>
        public string Sha256 { get; set; }

        #endregion
    }

    /// <summary>
    /// Archivio controllato: manifest, progetto e job dell'archivio, progetto già presente con lo stesso identificativo
    /// </summary>
    public sealed class TranslationArchiveInspection
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="manifest">Manifest</param>
        /// <param name="project">Progetto dell'archivio</param>
        /// <param name="contents">Byte controllati delle voci per percorso</param>
        public TranslationArchiveInspection(TranslationProjectArchiveManifest manifest, TranslationProject project, Dictionary<string, byte[]> contents)
        {
            this.Manifest = manifest;
            this.Project = project;
            this.Contents = contents;
            this.Jobs = new List<TranslationJob>();
            this.Existing = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Manifest
        /// </summary>
        public TranslationProjectArchiveManifest Manifest { get; }

        /// <summary>
        /// Progetto dell'archivio, coi percorsi dei sorgenti relativi
        /// </summary>
        public TranslationProject Project { get; }

        /// <summary>
        /// Job dell'archivio
        /// </summary>
        public List<TranslationJob> Jobs { get; }

        /// <summary>
        /// Byte controllati delle voci per percorso, che l'import scrive senza rileggere l'archivio
        /// </summary>
        public Dictionary<string, byte[]> Contents { get; }

        /// <summary>
        /// Progetto già presente con lo stesso identificativo, null se non c'è
        /// </summary>
        public TranslationProject Existing { get; set; }

        #endregion
    }
}
