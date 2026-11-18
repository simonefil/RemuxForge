using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Toolbox.Common
{
    /// <summary>
    /// Prenota i percorsi finali dei file prodotti da un lotto: un percorso occupato sul disco, già prenotato o uguale a un input
    /// del lotto riceve un suffisso numerico a partire da 2, prima dell'estensione (film.mkv, film.2.mkv) oppure, per i nomi che
    /// finiscono con la lingua, prima della lingua, che resta l'ultima parola del nome (film.eng.srt, film.2.eng.srt)
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
        /// e deve essere libero anche lui. Il file sostituibile, che il chiamante ha riconosciuto come suo, conta libero sul disco e si
        /// sovrascrive; resta occupato se è un input o è già prenotato dal lotto
        /// </summary>
        /// <param name="desiredPath">Percorso voluto</param>
        /// <param name="endsWithLanguage">True se l'ultima parola del nome è la lingua: il suffisso va prima di lei</param>
        /// <param name="replaceablePath">File esistente che il chiamante può sovrascrivere, null se nessuno</param>
        /// <returns>Percorso prenotato, quello voluto oppure con il suffisso</returns>
        public string Reserve(string desiredPath, bool endsWithLanguage = false, string replaceablePath = null)
        {
            string fullPath = Path.GetFullPath(desiredPath);
            string folder = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            int languageDot = endsWithLanguage ? name.LastIndexOf('.') : -1;
            string head = languageDot > 0 ? name.Substring(0, languageDot) : name;
            string language = name.Substring(head.Length);
            PathIdentity replaceable = replaceablePath != null ? new PathIdentity(replaceablePath) : null;
            string candidate = fullPath;
            string companion = FileHelper.GetCompanionPath(candidate);

            // Occupato = presente sul disco (file o cartella, salvo il sostituibile) oppure già preso dal lotto, per il principale e per l'accoppiato
            for (int suffix = 2;
                (File.Exists(candidate) && !new PathIdentity(candidate).Equals(replaceable)) || Directory.Exists(candidate) || this._taken.Contains(new PathIdentity(candidate)) ||
                (companion.Length > 0 && (File.Exists(companion) || Directory.Exists(companion) || this._taken.Contains(new PathIdentity(companion))));
                suffix++)
            {
                candidate = Path.Combine(folder, head + "." + suffix + language + extension);
                companion = FileHelper.GetCompanionPath(candidate);
            }

            this._taken.Add(new PathIdentity(candidate));
            if (companion.Length > 0)
                this._taken.Add(new PathIdentity(companion));

            return candidate;
        }

        /// <summary>
        /// Controlla una cartella di uscita scelta: deve essere il percorso completo di una cartella esistente, che non si crea in silenzio
        /// </summary>
        /// <param name="outputFolder">Cartella scelta</param>
        /// <returns>Motivo per cui la cartella non va bene, vuoto se va bene</returns>
        public static string ValidateFolder(string outputFolder)
        {
            string folder = (outputFolder ?? "").Trim();

            if (folder.Length == 0)
                return AppText.T("toolbox.output.folderRequired");
            if (!Path.IsPathFullyQualified(folder))
                return AppText.T("toolbox.output.folderNotAbsolute");
            if (File.Exists(folder))
                return AppText.T("toolbox.output.folderIsFile");
            if (!Directory.Exists(folder))
                return AppText.T("toolbox.output.folderMissing");

            return "";
        }

        /// <summary>
        /// Restituisce al lotto un percorso prenotato e il suo accoppiato, quando il file non è stato scritto: un'uscita fallita o
        /// interrotta non sposta su un suffisso i file che vengono dopo
        /// </summary>
        /// <param name="reservedPath">Percorso restituito da Reserve</param>
        public void Release(string reservedPath)
        {
            string companion = FileHelper.GetCompanionPath(reservedPath);

            this._taken.Remove(new PathIdentity(reservedPath));
            if (companion.Length > 0)
                this._taken.Remove(new PathIdentity(companion));
        }

        #endregion
    }
}
