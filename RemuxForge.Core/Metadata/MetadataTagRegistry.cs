using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Registro tag Matroska gestiti dalla UI Metadata
    /// </summary>
    public static class MetadataTagRegistry
    {
        #region Variabili statiche

        private static readonly List<string> s_tagNames = BuildTagNames();

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce i tag gestibili da UI
        /// </summary>
        /// <returns>Lista tag</returns>
        public static List<string> GetEditableTagNames()
        {
            return new List<string>(s_tagNames);
        }

        /// <summary>
        /// Verifica se un tag è gestito dalla UI
        /// </summary>
        /// <param name="tagName">Nome tag</param>
        /// <returns>True se consentito</returns>
        public static bool IsAllowed(string tagName)
        {
            string text = tagName != null ? tagName.Trim() : "";
            for (int i = 0; i < s_tagNames.Count; i++)
            {
                if (string.Equals(s_tagNames[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Metodi privati

        private static List<string> BuildTagNames()
        {
            List<string> result = new List<string>();
            result.Add("TITLE");
            result.Add("SUBTITLE");
            result.Add("DESCRIPTION");
            result.Add("COMMENT");
            result.Add("SUMMARY");
            result.Add("SYNOPSIS");
            result.Add("DATE_RELEASED");
            result.Add("GENRE");
            result.Add("PART_NUMBER");
            result.Add("PART_TOTAL");
            result.Add("DIRECTOR");
            result.Add("PRODUCER");
            result.Add("WRITTEN_BY");
            result.Add("COMPOSER");
            result.Add("ENCODER");
            result.Add("SOURCE");
            result.Add("LANGUAGE");
            result.Add("ORIGINAL_MEDIA_TYPE");
            return result;
        }

        #endregion
    }
}
