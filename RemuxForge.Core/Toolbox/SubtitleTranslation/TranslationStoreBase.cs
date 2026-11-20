using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Base degli store della traduzione: una cartella per elemento dentro la cartella di configurazione, col registro calls.jsonl
    /// in sola aggiunta
    /// </summary>
    public abstract class TranslationStoreBase
    {
        #region Costanti

        /// <summary>
        /// Registro delle chiamate di un elemento
        /// </summary>
        public const string CALLS_FILE = "calls.jsonl";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Cartella degli elementi
        /// </summary>
        protected readonly string _root;

        /// <summary>
        /// Protegge i file scritti da più chiamanti
        /// </summary>
        protected readonly object _lock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella di configurazione di RemuxForge</param>
        /// <param name="folderName">Cartella degli elementi dentro la cartella di configurazione</param>
        protected TranslationStoreBase(string configFolder, string folderName)
        {
            this._root = Path.Combine(configFolder ?? "", folderName);
            this._lock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Cancella un elemento con tutto il suo stato su disco
        /// </summary>
        /// <param name="id">Identificativo dell'elemento</param>
        public void Delete(string id)
        {
            string folder = this.GetFolder(id);

            lock (this._lock)
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        /// <summary>
        /// Accoda una riga al registro delle chiamate dell'elemento; dopo un'ultima riga troncata da una chiusura brusca la riga
        /// nuova parte a capo
        /// </summary>
        /// <param name="id">Identificativo dell'elemento</param>
        /// <param name="record">Chiamata da registrare</param>
        public void AppendCall(string id, TranslationCallRecord record)
        {
            lock (this._lock)
            {
                TranslationJsonFile.AppendLine(Path.Combine(this.GetFolder(id), CALLS_FILE), record);
            }
        }

        /// <summary>
        /// Legge il registro delle chiamate dell'elemento; una riga illeggibile, come l'ultima troncata da una chiusura brusca, si salta
        /// </summary>
        /// <param name="id">Identificativo dell'elemento</param>
        /// <returns>Chiamate nell'ordine del registro</returns>
        public List<TranslationCallRecord> ReadCalls(string id)
        {
            lock (this._lock)
            {
                return TranslationJsonFile.ReadLines<TranslationCallRecord>(Path.Combine(this.GetFolder(id), CALLS_FILE));
            }
        }

        /// <summary>
        /// Cartella di un elemento
        /// </summary>
        /// <param name="id">Identificativo dell'elemento</param>
        /// <returns>Percorso della cartella, anche se non esiste</returns>
        public string GetFolder(string id)
        {
            return Path.Combine(this._root, id);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Cartella degli elementi
        /// </summary>
        public string Root
        {
            get { return this._root; }
        }

        #endregion
    }
}
