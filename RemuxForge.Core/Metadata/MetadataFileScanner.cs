using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Scanner file MKV per modalità Metadata
    /// </summary>
    public class MetadataFileScanner
    {
        #region Variabili di classe

        /// <summary>
        /// Reader MediaInfo usato per popolare i record metadata
        /// </summary>
        private readonly MetadataMediaInfoReader _reader;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="reader">Reader MediaInfo</param>
        public MetadataFileScanner(MetadataMediaInfoReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            this._reader = reader;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scansiona input Metadata
        /// </summary>
        /// <param name="sourcePath">File o cartella sorgente</param>
        /// <param name="recursive">Vero per sottocartelle</param>
        /// <returns>Record metadata</returns>
        public List<MkvMetadataRecord> Scan(string sourcePath, bool recursive)
        {
            List<MkvMetadataRecord> records = new List<MkvMetadataRecord>();
            string source = sourcePath != null ? sourcePath.Trim() : "";
            string rootFolder;

            if (source.Length == 0)
                throw new InvalidOperationException(AppText.T("metadata.scanner.inputNotConfigured"));

            if (File.Exists(source))
            {
                string fullPath = Path.GetFullPath(source);
                if (string.Equals(Path.GetExtension(fullPath), ".mkv", StringComparison.OrdinalIgnoreCase))
                    records.Add(this.CreateRecord(fullPath, Path.GetDirectoryName(fullPath)));
            }
            else if (Directory.Exists(source))
            {
                rootFolder = Path.GetFullPath(source);
                this.ScanFolder(records, rootFolder, rootFolder, recursive, true);
            }
            else
            {
                throw new FileNotFoundException(AppText.T("metadata.scanner.inputNotFound"), source);
            }

            records.Sort((a, b) => string.Compare(a.InputFile, b.InputFile, StringComparison.OrdinalIgnoreCase));
            return records;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Scansiona una cartella metadata e le sottocartelle abilitate
        /// </summary>
        /// <param name="records">Lista record da popolare</param>
        /// <param name="rootFolder">Cartella radice della scansione</param>
        /// <param name="currentFolder">Cartella corrente</param>
        /// <param name="recursive">Vero per scansionare sottocartelle</param>
        /// <param name="isRoot">Vero se la cartella corrente è la root selezionata</param>
        private void ScanFolder(List<MkvMetadataRecord> records, string rootFolder, string currentFolder, bool recursive, bool isRoot)
        {
            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(currentFolder, "*.mkv", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                if (isRoot)
                    throw;

                return;
            }
            catch (IOException)
            {
                if (isRoot)
                    throw;

                return;
            }

            for (int i = 0; i < files.Length; i++)
            {
                records.Add(this.CreateRecord(Path.GetFullPath(files[i]), rootFolder));
            }

            if (!recursive)
                return;

            try
            {
                directories = Directory.GetDirectories(currentFolder, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                if (isRoot)
                    throw;

                return;
            }
            catch (IOException)
            {
                if (isRoot)
                    throw;

                return;
            }

            for (int i = 0; i < directories.Length; i++)
            {
                this.ScanFolder(records, rootFolder, directories[i], true, false);
            }
        }

        /// <summary>
        /// Crea un record metadata leggendo il file con MediaInfo
        /// </summary>
        /// <param name="filePath">Percorso file MKV</param>
        /// <param name="rootFolder">Cartella radice della scansione</param>
        /// <returns>Record metadata</returns>
        private MkvMetadataRecord CreateRecord(string filePath, string rootFolder)
        {
            MkvMetadataRecord record = new MkvMetadataRecord();
            FileInfo fileInfo = new FileInfo(filePath);

            record.InputFile = filePath;
            record.FileSize = fileInfo.Length;
            record.RelativeFolder = BuildRelativeFolder(fileInfo.DirectoryName, rootFolder);
            record.Status = AppText.T("metadata.status.pending");

            try
            {
                record.FileInfo = this._reader.ReadFile(filePath);
                record.OriginalFileInfo = MetadataModelCloner.CloneFileInfo(record.FileInfo);
                record.Status = AppText.T("metadata.status.scanned");
            }
            catch (Exception ex)
            {
                record.Status = AppText.T("metadata.status.error");
                record.ErrorMessage = ex.Message;
                record.AnalysisStatus = MkvMetadataAnalysisStatus.Error;
            }

            return record;
        }

        /// <summary>
        /// Costruisce la cartella relativa del file rispetto alla root selezionata
        /// </summary>
        /// <param name="folder">Cartella file</param>
        /// <param name="rootFolder">Cartella radice</param>
        /// <returns>Cartella relativa, oppure stringa vuota</returns>
        private static string BuildRelativeFolder(string folder, string rootFolder)
        {
            if (folder == null || rootFolder == null || rootFolder.Length == 0)
                return "";

            try
            {
                string relative = Path.GetRelativePath(rootFolder, folder);
                if (relative == ".")
                    return "";

                return relative;
            }
            catch
            {
                return "";
            }
        }

        #endregion
    }
}
