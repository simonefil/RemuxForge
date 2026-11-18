using System;
using System.Security.Cryptography;
using System.Text;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Chiave di cache delle chiamate della traduzione, mandata come header session_id: UUID versione 5 (SHA-1) nel namespace URL
    /// del nome remuxforge/translation/&lt;projectId&gt;/&lt;fileId&gt;/&lt;model&gt;/&lt;kind&gt;. Si ricalcola ogni volta
    /// </summary>
    public static class TranslationSessionKey
    {
        #region Costanti

        /// <summary>
        /// Namespace URL di RFC 9562, in ordine di rete
        /// </summary>
        private static readonly byte[] NAMESPACE_URL = new byte[] { 0x6b, 0xa7, 0xb8, 0x11, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8 };

        /// <summary>
        /// Segnaposto del file per le chiamate su più file
        /// </summary>
        public const string NO_FILE = "-";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Calcola la chiave di cache
        /// </summary>
        /// <param name="projectId">Identificativo del progetto</param>
        /// <param name="fileId">Identificativo del file, NO_FILE per le chiamate su più file</param>
        /// <param name="model">Modello</param>
        /// <param name="kind">Tipo della chiave (A0, A1, A2, B, C, D1, D2)</param>
        /// <returns>UUID in minuscolo con i trattini</returns>
        public static string Create(string projectId, string fileId, string model, string kind)
        {
            byte[] name = Encoding.UTF8.GetBytes("remuxforge/translation/" + projectId + "/" + fileId + "/" + model + "/" + kind);
            byte[] data = new byte[NAMESPACE_URL.Length + name.Length];
            Buffer.BlockCopy(NAMESPACE_URL, 0, data, 0, NAMESPACE_URL.Length);
            Buffer.BlockCopy(name, 0, data, NAMESPACE_URL.Length, name.Length);

            byte[] hash = SHA1.HashData(data);
            byte[] uuid = new byte[16];
            Array.Copy(hash, uuid, 16);

            // Versione 5 nei 4 bit alti del byte 6, variante RFC nei 2 bit alti del byte 8
            uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
            uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);

            return new Guid(uuid, true).ToString("D");
        }

        #endregion
    }
}
