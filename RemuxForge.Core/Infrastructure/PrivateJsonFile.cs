using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// File JSON di configurazione con credenziali nella cartella di configurazione, scritto in modo atomico e leggibile solo dal
    /// proprietario
    /// </summary>
    /// <typeparam name="T">Tipo serializzato</typeparam>
    public class PrivateJsonFile<T> where T : class
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// Opzioni JSON del file: indentato, nomi senza distinzione di maiuscole, enum come testo
        /// </summary>
        private readonly JsonSerializerOptions _serializerOptions;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella configurazione RemuxForge</param>
        /// <param name="fileName">Nome del file</param>
        public PrivateJsonFile(string configFolder, string fileName)
        {
            this._filePath = Path.Combine(configFolder != null ? configFolder : "", fileName);
            this._serializerOptions = new JsonSerializerOptions();
            this._serializerOptions.WriteIndented = true;
            this._serializerOptions.PropertyNameCaseInsensitive = true;
            this._serializerOptions.Converters.Add(new JsonStringEnumConverter(null, false));
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge il file
        /// </summary>
        /// <returns>Contenuto letto, null se il file non esiste o contiene null</returns>
        public T Read()
        {
            if (!File.Exists(this._filePath))
                return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(this._filePath), this._serializerOptions);
        }

        /// <summary>
        /// Scrive il file con scrittura atomica privata
        /// </summary>
        /// <param name="value">Contenuto da scrivere</param>
        public void Write(T value)
        {
            PrivateFileWriter.WriteAtomic(this._filePath, JsonSerializer.Serialize(value, this._serializerOptions));
        }

        #endregion
    }
}
