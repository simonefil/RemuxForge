using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.IO;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Metodi utility per operazioni su file
    /// </summary>
    public static class FileHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Elimina un file temporaneo in modo sicuro, loggando eventuali errori
        /// </summary>
        /// <param name="filePath">Percorso del file da eliminare</param>
        public static void DeleteTempFile(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("remux.process.tempFileDeleteFailed", filePath, ex.Message));
                }
            }
        }

        /// <summary>
        /// Elimina un file sottotitolo temporaneo e gli eventuali sidecar muxabili (.sub di un .idx)
        /// </summary>
        /// <param name="filePath">Percorso del file sottotitolo principale</param>
        public static void DeleteTempSubtitleFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            DeleteTempFile(filePath);
            if (string.Equals(Path.GetExtension(filePath), ".idx", StringComparison.OrdinalIgnoreCase))
            {
                DeleteTempFile(Path.ChangeExtension(filePath, ".sub"));
            }
        }

        /// <summary>
        /// Elimina una directory temporanea in modo sicuro, loggando eventuali errori
        /// </summary>
        /// <param name="directoryPath">Percorso della directory da eliminare</param>
        public static void DeleteTempDirectory(string directoryPath)
        {
            if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
            {
                try
                {
                    Directory.Delete(directoryPath, true);
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("remux.process.tempDirDeleteFailed", directoryPath, ex.Message));
                }
            }
        }

        /// <summary>
        /// Indica se due percorsi indicano lo stesso file o la stessa cartella sul disco: percorso completo, separatore finale,
        /// link simbolici e maiuscole secondo il file system, non come testo
        /// </summary>
        /// <param name="first">Primo percorso</param>
        /// <param name="second">Secondo percorso</param>
        /// <returns>True se i percorsi coincidono sul disco</returns>
        public static bool IsSamePath(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
            {
                return false;
            }

            return string.Equals(GetPathIdentity(first), GetPathIdentity(second), StringComparison.Ordinal);
        }

        /// <summary>
        /// Forma canonica di un percorso per il confronto: la parte esistente con i link risolti e il nome scritto sul disco,
        /// la parte che non esiste così com'è, in maiuscolo quando il file system non distingue le maiuscole
        /// </summary>
        /// <param name="path">Percorso assoluto o relativo</param>
        /// <returns>Identità del percorso</returns>
        public static string GetPathIdentity(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? "";
            string[] names = full.Substring(root.Length).Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            int depth = 0;
            int index = 0;
            while (index < names.Length)
            {
                FileSystemInfo entry = null;
                // Il nome come è scritto sul disco: quello identico se esiste, altrimenti l'unico che differisce solo per le maiuscole,
                // che esiste soltanto se il file system non le distingue
                string pattern = names[index].IndexOfAny(new char[] { '*', '?' }) >= 0 ? "*" : names[index];
                string requested = Path.Combine(current, names[index]);
                try
                {
                    // Un nome che il sistema non trova non ha un corrispondente sul disco, nemmeno con altre maiuscole
                    if (File.Exists(requested) || Directory.Exists(requested) || new FileInfo(requested).LinkTarget != null)
                    {
                        foreach (FileSystemInfo candidate in new DirectoryInfo(current).EnumerateFileSystemInfos(pattern, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = true }))
                        {
                            if (string.Equals(candidate.Name, names[index], StringComparison.Ordinal))
                            {
                                entry = candidate;
                                break;
                            }
                            if (entry == null && string.Equals(candidate.Name, names[index], StringComparison.OrdinalIgnoreCase))
                            {
                                entry = candidate;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    entry = null;
                }

                if (entry == null)
                {
                    break;
                }

                FileSystemInfo target = entry.LinkTarget != null && depth < 40 ? entry.ResolveLinkTarget(true) : null;
                if (target != null)
                {
                    // Il bersaglio del link può contenere altri link: si riparte da lui con i nomi che restano
                    depth++;
                    string rest = string.Join(Path.DirectorySeparatorChar, names, index + 1, names.Length - index - 1);
                    full = rest.Length > 0 ? Path.Combine(target.FullName, rest) : target.FullName;
                    root = Path.GetPathRoot(full) ?? "";
                    names = full.Substring(root.Length).Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    current = root;
                    index = 0;
                }
                else
                {
                    current = Path.Combine(current, entry.Name);
                    index++;
                }
            }

            if (index < names.Length)
            {
                // Le maiuscole non contano se la cartella scritta con le maiuscole invertite esiste senza essere una cartella sorella;
                // senza lettere da invertire vale l'impostazione predefinita del sistema
                bool caseInsensitive = !OperatingSystem.IsLinux();
                for (string probe = current; !string.IsNullOrEmpty(probe); probe = Path.GetDirectoryName(probe))
                {
                    string name = Path.GetFileName(probe);
                    string parent = Path.GetDirectoryName(probe);
                    char[] flipped = name.ToCharArray();
                    for (int i = 0; i < flipped.Length; i++)
                        flipped[i] = char.IsUpper(flipped[i]) ? char.ToLowerInvariant(flipped[i]) : char.ToUpperInvariant(flipped[i]);
                    string other = new string(flipped);
                    if (!string.IsNullOrEmpty(parent) && !string.Equals(other, name, StringComparison.Ordinal))
                    {
                        caseInsensitive = Directory.Exists(Path.Combine(parent, other)) &&
                            Array.FindIndex(Directory.GetDirectories(parent), d => string.Equals(Path.GetFileName(d), other, StringComparison.Ordinal)) < 0;
                        break;
                    }
                }

                string tail = string.Join(Path.DirectorySeparatorChar, names, index, names.Length - index);
                current = Path.Combine(current, caseInsensitive ? tail.ToUpperInvariant() : tail);
            }

            return current;
        }

        #endregion
    }
}
