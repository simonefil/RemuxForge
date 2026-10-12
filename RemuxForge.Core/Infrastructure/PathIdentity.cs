using System;
using System.IO;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Identità di un percorso sul disco: due percorsi sono uguali se indicano lo stesso file o la stessa cartella, con percorso completo,
    /// separatore finale, link simbolici e maiuscole secondo il file system, non come testo
    /// </summary>
    public sealed class PathIdentity : IEquatable<PathIdentity>
    {
        #region Variabili di classe

        /// <summary>
        /// Forma canonica: la parte esistente con i link risolti e il nome scritto sul disco, la parte che non esiste così com'è,
        /// in maiuscolo quando il file system non distingue le maiuscole
        /// </summary>
        private readonly string _value;

        #endregion

        #region Costruttore

        /// <summary>
        /// Risolve l'identità di un percorso
        /// </summary>
        /// <param name="path">Percorso assoluto o relativo</param>
        public PathIdentity(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? "";
            string[] names = full.Substring(root.Length).Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            int depth = 0;
            int index = 0;
            while (index < names.Length)
            {
                FileSystemInfo entry = FindEntry(current, names[index]);
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
                string tail = string.Join(Path.DirectorySeparatorChar, names, index, names.Length - index);
                current = Path.Combine(current, IsCaseInsensitive(current) ? tail.ToUpperInvariant() : tail);
            }

            this._value = current;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se due percorsi indicano lo stesso file o la stessa cartella sul disco; un percorso vuoto non coincide con nessuno
        /// </summary>
        /// <param name="first">Primo percorso</param>
        /// <param name="second">Secondo percorso</param>
        /// <returns>True se i percorsi coincidono sul disco</returns>
        public static bool IsSame(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
            {
                return false;
            }

            return new PathIdentity(first).Equals(new PathIdentity(second));
        }

        /// <summary>
        /// Vero se l'altra identità indica lo stesso percorso sul disco
        /// </summary>
        /// <param name="other">Identità da confrontare</param>
        /// <returns>True se le forme canoniche coincidono</returns>
        public bool Equals(PathIdentity other)
        {
            return other != null && string.Equals(this._value, other._value, StringComparison.Ordinal);
        }

        /// <summary>
        /// Vero se l'oggetto è un'identità dello stesso percorso
        /// </summary>
        /// <param name="obj">Oggetto da confrontare</param>
        /// <returns>True se le forme canoniche coincidono</returns>
        public override bool Equals(object obj)
        {
            return this.Equals(obj as PathIdentity);
        }

        /// <summary>
        /// Hash della forma canonica, coerente con Equals
        /// </summary>
        /// <returns>Hash ordinale</returns>
        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(this._value);
        }

        /// <summary>
        /// Forma canonica del percorso
        /// </summary>
        /// <returns>Percorso canonico</returns>
        public override string ToString()
        {
            return this._value;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Voce di una cartella con il nome come è scritto sul disco: quello identico se esiste, altrimenti l'unico che differisce
        /// solo per le maiuscole, che esiste soltanto se il file system non le distingue
        /// </summary>
        /// <param name="folder">Cartella esistente</param>
        /// <param name="name">Nome cercato</param>
        /// <returns>Voce trovata, null se il nome non esiste o la cartella non si legge</returns>
        private static FileSystemInfo FindEntry(string folder, string name)
        {
            FileSystemInfo entry = null;
            string pattern = name.IndexOfAny(new char[] { '*', '?' }) >= 0 ? "*" : name;
            string requested = Path.Combine(folder, name);
            try
            {
                // Un nome che il sistema non trova non ha un corrispondente sul disco, nemmeno con altre maiuscole
                if (File.Exists(requested) || Directory.Exists(requested) || new FileInfo(requested).LinkTarget != null)
                {
                    foreach (FileSystemInfo candidate in new DirectoryInfo(folder).EnumerateFileSystemInfos(pattern, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = true }))
                    {
                        if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                        {
                            entry = candidate;
                            break;
                        }
                        if (entry == null && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
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

            return entry;
        }

        /// <summary>
        /// Le maiuscole non contano se la cartella scritta con le maiuscole invertite esiste senza essere una cartella sorella;
        /// senza lettere da invertire vale l'impostazione predefinita del sistema
        /// </summary>
        /// <param name="folder">Ultima cartella esistente del percorso</param>
        /// <returns>True se il file system della cartella non distingue le maiuscole</returns>
        private static bool IsCaseInsensitive(string folder)
        {
            bool caseInsensitive = !OperatingSystem.IsLinux();
            for (string probe = folder; !string.IsNullOrEmpty(probe); probe = Path.GetDirectoryName(probe))
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

            return caseInsensitive;
        }

        #endregion
    }
}
