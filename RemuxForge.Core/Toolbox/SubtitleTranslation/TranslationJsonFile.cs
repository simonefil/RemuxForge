using RemuxForge.Core.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// File JSON della traduzione nella cartella di configurazione: scrittura atomica dei file interi e registri in sola aggiunta,
    /// una riga JSON per voce. Nomi camelCase, enum col nome del membro, date con offset, UTF-8 senza BOM
    /// </summary>
    public static class TranslationJsonFile
    {
        #region Variabili statiche

        /// <summary>
        /// Opzioni JSON dei file interi
        /// </summary>
        private static readonly JsonSerializerOptions s_options = CreateOptions(true);

        /// <summary>
        /// Opzioni JSON delle righe dei registri, una per riga
        /// </summary>
        private static readonly JsonSerializerOptions s_lineOptions = CreateOptions(false);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive un file intero in modo atomico, leggibile solo dal proprietario
        /// </summary>
        /// <typeparam name="T">Tipo del contenuto</typeparam>
        /// <param name="path">File da scrivere</param>
        /// <param name="value">Contenuto</param>
        public static void Write<T>(string path, T value)
        {
            PrivateFileWriter.WriteAtomic(path, JsonSerializer.Serialize(value, s_options));
        }

        /// <summary>
        /// Legge un file intero col controllo della versione del formato, fatto prima del contenuto: un file di una versione
        /// precedente può avere valori che il formato di oggi non legge più, e si rifiuta col motivo invece che come file guasto.
        /// Ogni rifiuto dà InvalidDataException col messaggio indicato
        /// </summary>
        /// <typeparam name="T">Tipo del contenuto</typeparam>
        /// <param name="path">File da leggere</param>
        /// <param name="current">Versione del formato di oggi</param>
        /// <param name="tooNewMessage">Messaggio per il file di una versione più recente</param>
        /// <param name="outdatedMessage">Messaggio per il file di una versione precedente</param>
        /// <param name="unreadableMessage">Messaggio per il file mancante o guasto</param>
        /// <returns>Contenuto letto</returns>
        public static T ReadVersioned<T>(string path, int current, string tooNewMessage, string outdatedMessage, string unreadableMessage) where T : class
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                if (!(ex is FileNotFoundException || ex is DirectoryNotFoundException))
                    throw;
                throw new InvalidDataException(unreadableMessage, ex);
            }

            int version = 0;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(bytes))
                {
                    JsonElement element;
                    int value;
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("schemaVersion", out element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
                        version = value;
                }
            }
            catch (JsonException)
            {
                // Contenuto guasto: lo segnala la lettura del contenuto
            }

            if (version > current)
                throw new InvalidDataException(tooNewMessage);
            if (version > 0 && version < current)
                throw new InvalidDataException(outdatedMessage);

            T content;
            try
            {
                using (StreamReader reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8))
                {
                    content = JsonSerializer.Deserialize<T>(reader.ReadToEnd(), s_options);
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(unreadableMessage, ex);
            }

            if (content == null)
                throw new InvalidDataException(unreadableMessage);
            return content;
        }

        /// <summary>
        /// Legge un contenuto JSON da byte, per i file dell'archivio; un contenuto guasto dà InvalidDataException
        /// </summary>
        /// <typeparam name="T">Tipo del contenuto</typeparam>
        /// <param name="bytes">Byte UTF-8</param>
        /// <param name="unreadableMessage">Messaggio per il contenuto guasto</param>
        /// <returns>Contenuto letto</returns>
        public static T Parse<T>(byte[] bytes, string unreadableMessage) where T : class
        {
            T value;
            try
            {
                value = JsonSerializer.Deserialize<T>(bytes, s_options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(unreadableMessage, ex);
            }

            if (value == null)
                throw new InvalidDataException(unreadableMessage);
            return value;
        }

        /// <summary>
        /// Serializza un contenuto come lo scrive Write, per i file dell'archivio
        /// </summary>
        /// <typeparam name="T">Tipo del contenuto</typeparam>
        /// <param name="value">Contenuto</param>
        /// <returns>Byte UTF-8 senza BOM</returns>
        public static byte[] Serialize<T>(T value)
        {
            return new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(value, s_options));
        }

        /// <summary>
        /// Copia profonda di un contenuto, passando dalla forma in cui lo scrive Write
        /// </summary>
        /// <typeparam name="T">Tipo del contenuto</typeparam>
        /// <param name="value">Contenuto</param>
        /// <returns>Copia</returns>
        public static T Clone<T>(T value)
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, s_options), s_options);
        }

        /// <summary>
        /// Accoda una riga a un registro; dopo un'ultima riga troncata da una chiusura brusca la riga nuova parte a capo
        /// </summary>
        /// <typeparam name="T">Tipo della riga</typeparam>
        /// <param name="path">Registro</param>
        /// <param name="value">Riga da accodare</param>
        public static void AppendLine<T>(string path, T value)
        {
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, s_lineOptions) + "\n");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                        stream.WriteByte((byte)'\n');
                }

                stream.Seek(0, SeekOrigin.End);
                stream.Write(line, 0, line.Length);
                stream.Flush(true);
            }
        }

        /// <summary>
        /// Legge un registro; una riga illeggibile, come l'ultima troncata da una chiusura brusca, si salta
        /// </summary>
        /// <typeparam name="T">Tipo della riga</typeparam>
        /// <param name="path">Registro</param>
        /// <returns>Righe nell'ordine del registro, vuote se il registro non c'è</returns>
        public static List<T> ReadLines<T>(string path) where T : class
        {
            List<T> values = new List<T>();
            if (!File.Exists(path))
                return values;

            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrEmpty(line.Trim()))
                    continue;

                try
                {
                    T value = JsonSerializer.Deserialize<T>(line, s_lineOptions);
                    if (value != null)
                        values.Add(value);
                }
                catch (JsonException)
                {
                    // Riga incompleta: il registro è in sola aggiunta e le righe intere restano valide
                }
            }

            return values;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Crea le opzioni JSON dei file della traduzione
        /// </summary>
        /// <param name="indented">True per i file interi, false per le righe dei registri</param>
        /// <returns>Opzioni del serializzatore</returns>
        private static JsonSerializerOptions CreateOptions(bool indented)
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = indented;
            options.PropertyNameCaseInsensitive = true;
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.Converters.Add(new JsonStringEnumConverter(null, false));
            return options;
        }

        #endregion
    }
}
