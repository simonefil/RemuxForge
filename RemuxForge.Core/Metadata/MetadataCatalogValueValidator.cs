using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Valida e normalizza valori metadata rispetto allo schema dichiarato dal catalogo
    /// </summary>
    public static class MetadataCatalogValueValidator
    {
        #region Metodi pubblici

        /// <summary>
        /// Valida un valore destinato a un campo o tag metadata
        /// </summary>
        /// <param name="valueType">Tipo logico del valore</param>
        /// <param name="inputKind">Tipo input dichiarato</param>
        /// <param name="allowedValues">Valori ammessi per select</param>
        /// <param name="label">Label del campo o tag</param>
        /// <param name="value">Valore sorgente</param>
        /// <param name="allowEmpty">Vero se il valore vuoto è consentito</param>
        /// <param name="normalizedValue">Valore normalizzato</param>
        /// <param name="errorMessage">Errore validazione</param>
        /// <returns>Vero se valido</returns>
        public static bool Validate(MetadataFieldValueType valueType, MetadataFieldInputKind inputKind, List<MetadataInputOption> allowedValues, string label, string value, bool allowEmpty, out string normalizedValue, out string errorMessage)
        {
            normalizedValue = value != null ? value.Trim() : "";
            errorMessage = "";

            if (string.IsNullOrEmpty(normalizedValue))
            {
                if (allowEmpty)
                    return true;

                errorMessage = AppText.F("metadata.validation.valueRequired", label);
                return false;
            }

            if (inputKind == MetadataFieldInputKind.Select)
            {
                if (allowedValues != null)
                {
                    for (int i = 0; i < allowedValues.Count; i++)
                    {
                        if (allowedValues[i] != null && string.Equals(allowedValues[i].Value, normalizedValue, StringComparison.Ordinal))
                            return true;
                    }
                }

                errorMessage = AppText.F("metadata.validation.invalidSelectValue", label, normalizedValue);
                return false;
            }

            if (inputKind == MetadataFieldInputKind.Boolean || valueType == MetadataFieldValueType.Boolean)
            {
                normalizedValue = MetadataValueNormalizer.NormalizeBoolean(normalizedValue);
                if (normalizedValue == "1" || normalizedValue == "0")
                    return true;

                errorMessage = AppText.F("metadata.validation.invalidBooleanValue", label, normalizedValue);
                return false;
            }

            if (inputKind == MetadataFieldInputKind.LanguageSelect || valueType == MetadataFieldValueType.Language)
            {
                string languageValue = normalizedValue;
                if (LanguageValidator.TryNormalizeToIso6392(languageValue, out normalizedValue))
                    return true;

                errorMessage = AppText.F("metadata.validation.invalidLanguageValue", label, languageValue);
                return false;
            }

            if (inputKind == MetadataFieldInputKind.Number || valueType == MetadataFieldValueType.Integer)
            {
                if (long.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    return true;

                errorMessage = AppText.F("metadata.validation.invalidIntegerValue", label, normalizedValue);
                return false;
            }

            if (inputKind == MetadataFieldInputKind.Decimal || valueType == MetadataFieldValueType.Decimal)
            {
                if (double.TryParse(normalizedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return true;

                errorMessage = AppText.F("metadata.validation.invalidDecimalValue", label, normalizedValue);
                return false;
            }

            return true;
        }

        #endregion
    }
}
