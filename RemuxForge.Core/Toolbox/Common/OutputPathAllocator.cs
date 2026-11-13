using RemuxForge.Core.Infrastructure;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Toolbox.Common
{
    /// <summary>
    /// Prenota i percorsi finali dei file prodotti da un lotto: un percorso occupato sul disco, già prenotato o uguale a un input
    /// del lotto riceve un suffisso numerico prima dell'estensione, a partire da 2 (film.eng.srt, film.eng.2.srt)
    /// </summary>
    public sealed class OutputPathAllocator
    {
        #region Variabili di classe

        /// <summary>
        /// Identità dei percorsi che il lotto non può usare: gli input e quelli già prenotati
        /// </summary>
        private readonly HashSet<PathIdentity> _taken = new HashSet<PathIdentity>();

        #endregion

        #region Costruttore

        /// <summary>
        /// Crea l'allocatore di un lotto
        /// </summary>
        /// <param name="inputPaths">File di input del lotto, mai usati come uscita</param>
        public OutputPathAllocator(IEnumerable<string> inputPaths)
        {
            foreach (string inputPath in inputPaths)
                this._taken.Add(new PathIdentity(inputPath));
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Prenota il primo percorso libero a partire da quello voluto; il file accoppiato (il .sub di un .idx) riceve lo stesso suffisso
        /// e deve essere libero anche lui
        /// </summary>
        /// <param name="desiredPath">Percorso voluto</param>
        /// <returns>Percorso prenotato, quello voluto oppure con il suffisso</returns>
        public string Reserve(string desiredPath)
        {
            string fullPath = Path.GetFullPath(desiredPath);
            string folder = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            string candidate = fullPath;
            string companion = FileHelper.GetCompanionPath(candidate);
            bool IsFree(string path) => !File.Exists(path) && !Directory.Exists(path) && !this._taken.Contains(new PathIdentity(path));

            for (int suffix = 2; !IsFree(candidate) || (companion.Length > 0 && !IsFree(companion)); suffix++)
            {
                candidate = Path.Combine(folder, name + "." + suffix + extension);
                companion = FileHelper.GetCompanionPath(candidate);
            }

            this._taken.Add(new PathIdentity(candidate));
            if (companion.Length > 0)
                this._taken.Add(new PathIdentity(companion));

            return candidate;
        }

        #endregion
    }
}
