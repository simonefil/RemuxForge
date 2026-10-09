using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Aggiunge agli URL dei file statici un'impronta del contenuto, così il browser scarica di nuovo un file appena cambia
    /// </summary>
    public class StaticAssetVersionService
    {
        #region Variabili di classe

        private readonly IFileProvider _fileProvider;
        private readonly ConcurrentDictionary<string, string> _urls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="environment">Ambiente web con la cartella dei file statici</param>
        public StaticAssetVersionService(IWebHostEnvironment environment)
        {
            this._fileProvider = environment.WebRootFileProvider;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce l'URL del file con l'impronta del contenuto in query string
        /// </summary>
        /// <param name="path">Percorso relativo alla cartella dei file statici</param>
        /// <returns>URL versionato, oppure il percorso invariato se il file non esiste</returns>
        public string Url(string path)
        {
            return this._urls.GetOrAdd(path, this.BuildUrl);
        }

        /// <summary>
        /// Costruisce la import map che associa ogni modulo JavaScript di una cartella al suo URL versionato,
        /// così gli import da C# e quelli fra moduli caricano sempre la versione corrente
        /// </summary>
        /// <param name="folder">Cartella dei moduli, relativa alla cartella dei file statici</param>
        /// <returns>JSON della import map</returns>
        public string ImportMap(string folder)
        {
            Dictionary<string, string> imports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (IFileInfo file in this._fileProvider.GetDirectoryContents(folder))
            {
                if (file.IsDirectory || !file.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    continue;
                string path = folder + "/" + file.Name;
                imports["/" + path] = "/" + this.Url(path);
            }
            return JsonSerializer.Serialize(new { imports });
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola l'impronta SHA-256 del file e la accoda al percorso
        /// </summary>
        /// <param name="path">Percorso relativo alla cartella dei file statici</param>
        /// <returns>URL versionato</returns>
        private string BuildUrl(string path)
        {
            IFileInfo file = this._fileProvider.GetFileInfo(path);
            if (!file.Exists)
                return path;

            using Stream stream = file.CreateReadStream();
            byte[] hash = SHA256.HashData(stream);
            return path + "?v=" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        #endregion
    }
}
