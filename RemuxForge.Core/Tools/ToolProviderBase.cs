using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RemuxForge.Core.Tools
{
    /// <summary>
    /// Classe base per i provider di tool esterni (ffmpeg, mkvmerge, mediainfo)
    /// Fornisce i metodi condivisi di ricerca eseguibili
    /// </summary>
    public abstract class ToolProviderBase
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso risolto dell'eseguibile
        /// </summary>
        protected string _resolvedPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        protected ToolProviderBase()
        {
            this._resolvedPath = "";
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Cerca un eseguibile nelle directory specificate
        /// </summary>
        /// <param name="executableName">Nome dell'eseguibile da cercare</param>
        /// <param name="searchPaths">Array di directory in cui cercare</param>
        /// <returns>Percorso completo se trovato, stringa vuota altrimenti</returns>
        protected static string SearchInPaths(string executableName, string[] searchPaths)
        {
            string result = "";
            string candidate;
            for (int i = 0; i < searchPaths.Length; i++)
            {
                candidate = Path.Combine(searchPaths[i], executableName);
                if (File.Exists(candidate))
                {
                    result = candidate;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Cerca un eseguibile nel PATH di sistema
        /// </summary>
        /// <param name="executableName">Nome dell'eseguibile da cercare</param>
        /// <returns>Percorso completo dell'eseguibile, stringa vuota se non trovato</returns>
        protected internal static string FindInSystemPath(string executableName)
        {
            string result = "";
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            char separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            string[] paths;
            string candidate;
            if (pathEnv != null)
            {
                paths = pathEnv.Split(separator);

                for (int i = 0; i < paths.Length; i++)
                {
                    candidate = Path.Combine(paths[i], executableName);
                    if (File.Exists(candidate))
                    {
                        result = candidate;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Restituisce l'estensione eseguibile per la piattaforma corrente
        /// </summary>
        /// <returns>".exe" su Windows, stringa vuota altrimenti</returns>
        protected internal static string GetExecutableExtension()
        {
            string result = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                result = ".exe";
            }

            return result;
        }

        #endregion
    }
}
