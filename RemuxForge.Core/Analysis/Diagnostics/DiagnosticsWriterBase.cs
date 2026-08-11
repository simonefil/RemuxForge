using RemuxForge.Core.Configuration;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Analysis.Diagnostics
{
    /// <summary>
    /// Base class per scrittura diagnostiche di analisi video/audio
    /// </summary>
    public class DiagnosticsWriterBase
    {
        /// <summary>
        /// Costruisce il percorso base del file diagnostico
        /// </summary>
        /// <param name="folderName">Nome cartella diagnositica</param>
        /// <param name="episodeId">Id episodio</param>
        /// <returns>Path completo del file</returns>
        protected string BuildDiagnosticsBasePath(string folderName, string episodeId)
        {
            string folder = Path.Combine(AppSettingsService.Instance.ConfigFolder, folderName);
            string safeEpisode = this.SanitizeFileName(episodeId);
            string baseName = string.IsNullOrEmpty(safeEpisode) ? "episode" : safeEpisode;
            Directory.CreateDirectory(folder);

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            return Path.Combine(folder, baseName + "-" + timestamp);
        }

        /// <summary>
        /// Serializza un payload JSON in modo safe
        /// </summary>
        protected void WriteJson<T>(string filePath, T payload)
        {
            JsonSerializerOptions serializerOptions = new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                WriteIndented = true
            };
            string json = JsonSerializer.Serialize(payload, serializerOptions);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Sanitizza un valore per l'uso in nomi file
        /// </summary>
        protected string SanitizeFileName(string value)
        {
            if (value == null)
            {
                return "";
            }

            string result = "";
            char[] invalidChars = Path.GetInvalidFileNameChars();
            bool invalid;

            for (int i = 0; i < value.Length; i++)
            {
                invalid = false;
                for (int c = 0; c < invalidChars.Length; c++)
                {
                    if (value[i] == invalidChars[c])
                    {
                        invalid = true;
                        break;
                    }
                }

                result += invalid ? "_" : value[i].ToString();
            }

            return result;
        }
    }
}
