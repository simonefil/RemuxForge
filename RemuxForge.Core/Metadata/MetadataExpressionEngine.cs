using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Motore espressioni metadata con token e funzioni consentite
    /// </summary>
    public class MetadataExpressionEngine
    {
        #region Metodi pubblici

        /// <summary>
        /// Valuta template metadata con snapshot originale
        /// </summary>
        /// <param name="template">Template o espressione</param>
        /// <param name="fileInfo">Info file corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <param name="originalFileInfo">Info file originale</param>
        /// <param name="originalTrack">Traccia originale</param>
        /// <returns>Valore valutato</returns>
        public string Evaluate(string template, MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track, MkvMetadataFileInfo originalFileInfo, MkvMetadataTrackInfo originalTrack)
        {
            string result = template != null ? template : "";
            result = Regex.Replace(result, @"\{([^{}]+)\}", match => this.EvaluateExpression(match.Groups[1].Value, fileInfo, track, originalFileInfo, originalTrack));
            result = Regex.Replace(result, @"\[([^\[\]]+)\]", match => this.ResolveTokenOrLiteral(match.Value, match.Groups[1].Value, fileInfo, track, originalFileInfo, originalTrack));
            return result;
        }

        /// <summary>
        /// Risolve un token senza parentesi quadre
        /// </summary>
        /// <param name="token">Token</param>
        /// <param name="fileInfo">Info file</param>
        /// <param name="track">Traccia corrente</param>
        /// <returns>Valore token</returns>
        public string ResolveToken(string token, MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track)
        {
            return this.ResolveToken(token, fileInfo, track, null, null);
        }

        /// <summary>
        /// Risolve un token senza parentesi quadre con snapshot originale opzionale
        /// </summary>
        /// <param name="token">Token</param>
        /// <param name="fileInfo">Info file corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <param name="originalFileInfo">Info file originale</param>
        /// <param name="originalTrack">Traccia originale</param>
        /// <returns>Valore token</returns>
        public string ResolveToken(string token, MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track, MkvMetadataFileInfo originalFileInfo, MkvMetadataTrackInfo originalTrack)
        {
            string key = token != null ? token.Trim() : "";

            if (key.StartsWith("original.", StringComparison.OrdinalIgnoreCase))
            {
                key = key.Substring("original.".Length);
                return this.ResolveTokenFromContext(key, originalFileInfo, originalTrack);
            }

            if (key.StartsWith("current.", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("current.".Length);

            return this.ResolveTokenFromContext(key, fileInfo, track);
        }

        /// <summary>
        /// Valida sintassi espressioni e funzioni consentite
        /// </summary>
        /// <param name="template">Template da validare</param>
        /// <returns>Errori</returns>
        public List<string> Validate(string template)
        {
            List<string> errors = new List<string>();
            string text = template != null ? template : "";

            foreach (Match match in Regex.Matches(text, @"\{([^{}]+)\}"))
            {
                this.ValidateExpression(match.Groups[1].Value, match.Groups[1].Index, errors);
            }

            return errors;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Risolve un token nel contesto file/traccia indicato
        /// </summary>
        /// <param name="key">Chiave token normalizzata</param>
        /// <param name="fileInfo">Info file da interrogare</param>
        /// <param name="track">Traccia da interrogare</param>
        /// <returns>Valore token risolto</returns>
        private string ResolveTokenFromContext(string key, MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track)
        {
            string value;

            if (track != null && track.Fields != null && track.Fields.TryGetValue(key, out value))
                return value != null ? value : "";

            if (fileInfo != null && fileInfo.Fields != null && fileInfo.Fields.TryGetValue(key, out value))
                return value != null ? value : "";

            if (key == "file_folder" && fileInfo != null && !string.IsNullOrEmpty(fileInfo.FilePath))
                return Path.GetDirectoryName(fileInfo.FilePath) ?? "";

            if (key == "file_relative_folder")
                return "";

            if (key.StartsWith("mi.current.", StringComparison.OrdinalIgnoreCase) && track != null)
                return GetRaw(track.RawFields, key.Substring("mi.current.".Length));

            if (key.StartsWith("mi.video.", StringComparison.OrdinalIgnoreCase) && track != null && string.Equals(track.TrackKind, "video", StringComparison.OrdinalIgnoreCase))
                return GetRaw(track.RawFields, key.Substring("mi.video.".Length));

            if (key.StartsWith("mi.audio.", StringComparison.OrdinalIgnoreCase) && track != null && string.Equals(track.TrackKind, "audio", StringComparison.OrdinalIgnoreCase))
                return GetRaw(track.RawFields, key.Substring("mi.audio.".Length));

            if (key.StartsWith("mi.text.", StringComparison.OrdinalIgnoreCase) && track != null && string.Equals(track.TrackKind, "subtitles", StringComparison.OrdinalIgnoreCase))
                return GetRaw(track.RawFields, key.Substring("mi.text.".Length));

            if (key.StartsWith("mi.general.", StringComparison.OrdinalIgnoreCase) && fileInfo != null)
                return GetRaw(fileInfo.RawGeneral, key.Substring("mi.general.".Length));

            return "";
        }

        /// <summary>
        /// Risolve un token conosciuto mantenendo letterali le quadre non registrate
        /// </summary>
        /// <param name="literal">Testo originale con parentesi quadre</param>
        /// <param name="token">Token senza parentesi quadre</param>
        /// <param name="fileInfo">Info file corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <param name="originalFileInfo">Info file originale</param>
        /// <param name="originalTrack">Traccia originale</param>
        /// <returns>Valore token o testo letterale originale</returns>
        private string ResolveTokenOrLiteral(string literal, string token, MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track, MkvMetadataFileInfo originalFileInfo, MkvMetadataTrackInfo originalTrack)
        {
            if (!this.IsKnownToken(token))
                return literal != null ? literal : "";

            return this.ResolveToken(token, fileInfo, track, originalFileInfo, originalTrack);
        }

        /// <summary>
        /// Valuta una singola espressione con pipeline di funzioni
        /// </summary>
        /// <param name="expression">Espressione senza parentesi graffe esterne</param>
        /// <param name="fileInfo">Info file corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <param name="originalFileInfo">Info file originale</param>
        /// <param name="originalTrack">Traccia originale</param>
        /// <returns>Valore espressione valutato</returns>
        private string EvaluateExpression(string expression, MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track, MkvMetadataFileInfo originalFileInfo, MkvMetadataTrackInfo originalTrack)
        {
            List<string> parts = SplitPipeline(expression);
            if (parts.Count == 0)
                return "";

            string value = this.Evaluate(parts[0], fileInfo, track, originalFileInfo, originalTrack);
            for (int i = 1; i < parts.Count; i++)
            {
                value = this.ApplyFunction(value, parts[i]);
            }

            return value;
        }

        /// <summary>
        /// Applica una funzione consentita al valore corrente
        /// </summary>
        /// <param name="value">Valore corrente della pipeline</param>
        /// <param name="functionCall">Chiamata funzione testuale</param>
        /// <returns>Valore trasformato</returns>
        private string ApplyFunction(string value, string functionCall)
        {
            string name;
            List<string> args;
            int trimCount;
            int start;
            int length;
            int leftCount;
            int rightCount;
            double number;
            double operand;
            int decimals;

            ParseFunction(functionCall, out name, out args);

            switch (name)
            {
                case "Trim":
                    return value.Trim();
                case "TrimEnd":
                    if (args.Count == 1 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out trimCount) && trimCount > 0 && value.Length >= trimCount)
                        return value.Substring(0, value.Length - trimCount);

                    return value.TrimEnd();
                case "ToUpper":
                    return value.ToUpperInvariant();
                case "ToLower":
                    return value.ToLowerInvariant();
                case "Replace":
                    if (args.Count >= 2)
                        return value.Replace(args[0], args[1]);

                    break;
                case "RegexReplace":
                    if (args.Count >= 2)
                        return Regex.Replace(value, args[0], args[1]);

                    break;
                case "Substring":
                    if (args.Count >= 1)
                    {
                        start = ParseExpressionInt(args[0]);
                        length = args.Count >= 2 ? ParseExpressionInt(args[1]) : value.Length - start;
                        if (start < 0)
                            start = 0;

                        if (start >= value.Length)
                            return "";

                        if (length < 0)
                            length = 0;

                        if (start + length > value.Length)
                            length = value.Length - start;

                        return value.Substring(start, length);
                    }

                    break;
                case "Left":
                    if (args.Count == 1)
                    {
                        leftCount = ParseExpressionInt(args[0]);
                        if (leftCount < 0)
                            leftCount = 0;

                        if (leftCount > value.Length)
                            leftCount = value.Length;

                        return value.Substring(0, leftCount);
                    }
                    break;
                case "Right":
                    if (args.Count == 1)
                    {
                        rightCount = ParseExpressionInt(args[0]);
                        if (rightCount < 0)
                            rightCount = 0;

                        if (rightCount > value.Length)
                            rightCount = value.Length;

                        return value.Substring(value.Length - rightCount);
                    }
                    break;
                case "NormalizeSpaces":
                    return Regex.Replace(value.Trim(), @"\s+", " ");
                case "Add":
                    if (args.Count == 1 && TryParseExpressionDouble(value, out number) && TryParseExpressionDouble(args[0], out operand))
                        return FormatExpressionNumber(number + operand);

                    break;
                case "Sub":
                    if (args.Count == 1 && TryParseExpressionDouble(value, out number) && TryParseExpressionDouble(args[0], out operand))
                        return FormatExpressionNumber(number - operand);

                    break;
                case "Mul":
                    if (args.Count == 1 && TryParseExpressionDouble(value, out number) && TryParseExpressionDouble(args[0], out operand))
                        return FormatExpressionNumber(number * operand);

                    break;
                case "Div":
                    if (args.Count == 1 && TryParseExpressionDouble(value, out number) && TryParseExpressionDouble(args[0], out operand))
                    {
                        if (operand == 0.0)
                            throw new InvalidOperationException(AppText.T("metadata.expression.divideByZero"));

                        return FormatExpressionNumber(number / operand);
                    }

                    break;
                case "Round":
                    if (TryParseExpressionDouble(value, out number))
                    {
                        decimals = args.Count == 1 ? ParseExpressionInt(args[0]) : 0;
                        if (decimals < 0)
                            decimals = 0;
                        if (decimals > 15)
                            decimals = 15;

                        return FormatExpressionNumber(Math.Round(number, decimals, MidpointRounding.AwayFromZero));
                    }

                    break;
                case "Floor":
                    if (TryParseExpressionDouble(value, out number))
                        return FormatExpressionNumber(Math.Floor(number));

                    break;
                case "Ceil":
                    if (TryParseExpressionDouble(value, out number))
                        return FormatExpressionNumber(Math.Ceiling(number));

                    break;
                case "Format":
                    if (args.Count == 1 && TryParseExpressionDouble(value, out number))
                        return number.ToString(args[0], CultureInfo.InvariantCulture);

                    break;
            }

            return value;
        }

        /// <summary>
        /// Valida token e funzioni di una singola espressione
        /// </summary>
        /// <param name="expression">Espressione senza parentesi graffe esterne</param>
        /// <param name="basePosition">Posizione iniziale dell'espressione nel template</param>
        /// <param name="errors">Lista errori da popolare</param>
        private void ValidateExpression(string expression, int basePosition, List<string> errors)
        {
            List<string> parts = SplitPipeline(expression);
            if (parts.Count == 0)
            {
                errors.Add(AppText.F("metadata.expression.emptyAt", basePosition));
                return;
            }

            foreach (Match match in Regex.Matches(parts[0], @"\[([^\[\]]+)\]"))
            {
                if (!this.IsKnownToken(match.Groups[1].Value))
                    errors.Add(AppText.F("metadata.expression.unknownTokenAt", match.Groups[1].Value, basePosition + match.Groups[1].Index));
            }

            for (int i = 1; i < parts.Count; i++)
            {
                string name;
                List<string> args;
                double operand;

                ParseFunction(parts[i], out name, out args);
                if (!MetadataUiCatalog.IsKnownFunction(name))
                {
                    errors.Add(AppText.F("metadata.expression.unknownFunctionAt", name, basePosition + FindExpressionPartPosition(expression, parts, i)));
                }
                else if (string.Equals(name, "Div", StringComparison.Ordinal) && args.Count == 1 && TryParseExpressionDouble(args[0], out operand) && operand == 0.0)
                {
                    errors.Add(AppText.F("metadata.expression.divideByZeroAt", basePosition + FindExpressionPartPosition(expression, parts, i)));
                }
            }
        }

        /// <summary>
        /// Trova la posizione relativa di una parte della pipeline
        /// </summary>
        /// <param name="expression">Espressione completa</param>
        /// <param name="parts">Parti della pipeline</param>
        /// <param name="partIndex">Indice parte richiesta</param>
        /// <returns>Posizione relativa della parte</returns>
        private static int FindExpressionPartPosition(string expression, List<string> parts, int partIndex)
        {
            int searchStart = 0;

            for (int i = 0; i <= partIndex && i < parts.Count; i++)
            {
                int found = expression.IndexOf(parts[i], searchStart, StringComparison.Ordinal);
                if (found < 0)
                    return 0;

                if (i == partIndex)
                    return found;

                searchStart = found + parts[i].Length;
            }

            return 0;
        }

        /// <summary>
        /// Verifica se un token è riconosciuto dal motore metadata
        /// </summary>
        /// <param name="token">Token senza parentesi quadre</param>
        /// <returns>Vero se il token è valido</returns>
        private bool IsKnownToken(string token)
        {
            MetadataFieldDefinition field;
            string key = token != null ? token.Trim() : "";

            if (key.StartsWith("original.", StringComparison.OrdinalIgnoreCase) || key.StartsWith("current.", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(key.IndexOf(".", StringComparison.Ordinal) + 1);

            if (key.StartsWith("mi.", StringComparison.OrdinalIgnoreCase))
                return true;

            if (MetadataFieldRegistry.TryGet(key, out field))
                return field.IsReadable && field.Visibility != MetadataFieldVisibility.Hidden;

            return key == "file_folder" || key == "file_relative_folder";
        }

        /// <summary>
        /// Divide una espressione nelle parti della pipeline
        /// </summary>
        /// <param name="expression">Espressione da dividere</param>
        /// <returns>Parti della pipeline</returns>
        private static List<string> SplitPipeline(string expression)
        {
            List<string> result = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuote = false;
            int parenDepth = 0;
            int bracketDepth = 0;

            // Divide la pipeline ignorando separatori dentro token, funzioni e stringhe
            for (int i = 0; i < expression.Length; i++)
            {
                char c = expression[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    current.Append(c);
                }
                else if (!inQuote && c == '[')
                {
                    bracketDepth++;
                    current.Append(c);
                }
                else if (!inQuote && c == ']')
                {
                    if (bracketDepth > 0)
                        bracketDepth--;

                    current.Append(c);
                }
                else if (!inQuote && bracketDepth == 0 && c == '(')
                {
                    parenDepth++;
                    current.Append(c);
                }
                else if (!inQuote && bracketDepth == 0 && c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;

                    current.Append(c);
                }
                else if (!inQuote && bracketDepth == 0 && parenDepth == 0 && (c == ':' || IsDotFunctionSeparator(expression, i)))
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString());
            return result;
        }

        /// <summary>
        /// Verifica se un punto introduce una funzione pipeline
        /// </summary>
        /// <param name="expression">Espressione completa</param>
        /// <param name="index">Indice del punto da verificare</param>
        /// <returns>Vero se il punto è un separatore funzione</returns>
        private static bool IsDotFunctionSeparator(string expression, int index)
        {
            int pos = index + 1;
            if (index < 0 || index >= expression.Length || expression[index] != '.')
                return false;

            // Accetta solo ".NomeFunzione(" come separatore pipeline
            while (pos < expression.Length && char.IsWhiteSpace(expression[pos]))
            {
                pos++;
            }

            if (pos >= expression.Length || (!char.IsLetter(expression[pos]) && expression[pos] != '_'))
                return false;

            while (pos < expression.Length && (char.IsLetterOrDigit(expression[pos]) || expression[pos] == '_'))
            {
                pos++;
            }

            while (pos < expression.Length && char.IsWhiteSpace(expression[pos]))
            {
                pos++;
            }

            return pos < expression.Length && expression[pos] == '(';
        }

        /// <summary>
        /// Estrae nome e argomenti da una chiamata funzione
        /// </summary>
        /// <param name="functionCall">Chiamata funzione testuale</param>
        /// <param name="name">Nome funzione estratto</param>
        /// <param name="args">Argomenti funzione estratti</param>
        private static void ParseFunction(string functionCall, out string name, out List<string> args)
        {
            args = new List<string>();
            string text = functionCall != null ? functionCall.Trim() : "";
            int paren = text.IndexOf('(');
            if (paren < 0)
            {
                name = text;
                return;
            }

            name = text.Substring(0, paren).Trim();
            string argsText = text.Substring(paren + 1);
            if (argsText.EndsWith(")", StringComparison.Ordinal))
                argsText = argsText.Substring(0, argsText.Length - 1);

            args = SplitArguments(argsText);
        }

        /// <summary>
        /// Divide gli argomenti di una funzione
        /// </summary>
        /// <param name="argsText">Testo argomenti senza parentesi esterne</param>
        /// <returns>Argomenti separati</returns>
        private static List<string> SplitArguments(string argsText)
        {
            List<string> result = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuote = false;

            // Divide gli argomenti solo sulle virgole fuori dalle stringhe
            for (int i = 0; i < argsText.Length; i++)
            {
                char c = argsText[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && c == ',')
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0 || !string.IsNullOrEmpty(argsText))
                result.Add(current.ToString().Trim());

            return result;
        }

        /// <summary>
        /// Converte testo in intero invariant con valore predefinito zero
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>Valore intero</returns>
        private static int ParseExpressionInt(string value)
        {
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                result = 0;

            return result;
        }

        /// <summary>
        /// Converte testo o valore MediaInfo in numero usabile dalle funzioni matematiche
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <param name="number">Numero estratto</param>
        /// <returns>True se il valore contiene un numero</returns>
        private static bool TryParseExpressionDouble(string value, out double number)
        {
            string text = value != null ? value.Trim() : "";
            if (string.IsNullOrEmpty(text))
            {
                number = 0.0;
                return false;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return true;

            number = MetadataValueNormalizer.ParseDoubleWithUnit(text);
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Formatta un numero expression senza zeri decimali inutili
        /// </summary>
        /// <param name="value">Valore numerico</param>
        /// <returns>Numero in formato invariant</returns>
        private static string FormatExpressionNumber(double value)
        {
            return value.ToString("0.############", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Legge un campo raw MediaInfo
        /// </summary>
        /// <param name="raw">Mappa campi raw</param>
        /// <param name="key">Chiave campo raw</param>
        /// <returns>Valore raw o stringa vuota</returns>
        private static string GetRaw(Dictionary<string, string> raw, string key)
        {
            string result;
            if (raw != null && raw.TryGetValue(key, out result) && result != null)
                return result;

            return "";
        }

        #endregion
    }
}
