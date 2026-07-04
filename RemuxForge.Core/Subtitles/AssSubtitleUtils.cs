using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Utility per parsing e rewrite geometrico ASS/SSA
    /// </summary>
    internal static class AssSubtitleUtils
    {
        #region Variabili statiche

        /// <summary>
        /// Regex coordinate \pos e \org
        /// </summary>
        private static readonly Regex s_pointTagRegex = new Regex(@"\\(pos|org)\(([^)]*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Regex coordinate \move
        /// </summary>
        private static readonly Regex s_moveTagRegex = new Regex(@"\\move\(([^)]*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Regex clip rettangolari o vettoriali
        /// </summary>
        private static readonly Regex s_clipTagRegex = new Regex(@"\\(i?clip)\(([^)]*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Regex tag numerico semplice
        /// </summary>
        private static readonly Regex s_numberTagRegex = new Regex(@"\\(?<name>fs(?!c)|fscx|fscy|fsp|bord|xbord|ybord|shad|xshad|yshad|blur|be|pbo)(?<value>[+-]?\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        #endregion

        #region Metodi pubblici - Campi ASS

        /// <summary>
        /// Risolve l'indice di un campo da una riga Format
        /// </summary>
        /// <param name="format">Contenuto dopo Format:</param>
        /// <param name="fieldName">Nome campo da cercare</param>
        /// <returns>Indice campo, -1 se assente</returns>
        public static int ResolveFieldIndex(string format, string fieldName)
        {
            string[] fields = (format ?? "").Split(',');
            string name;
            for (int i = 0; i < fields.Length; i++)
            {
                name = fields[i].Trim();
                if (string.Equals(name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Conta i campi di una riga Format
        /// </summary>
        /// <param name="format">Contenuto dopo Format:</param>
        /// <returns>Numero campi dichiarati</returns>
        public static int CountFields(string format)
        {
            return string.IsNullOrEmpty(format) ? 0 : format.Split(',').Length;
        }

        /// <summary>
        /// Divide una riga ASS rispettando il numero campi dichiarato
        /// </summary>
        /// <param name="body">Contenuto dopo il prefisso riga</param>
        /// <param name="fieldCount">Numero campi dichiarati</param>
        /// <returns>Campi divisi</returns>
        public static string[] SplitFields(string body, int fieldCount)
        {
            return fieldCount > 0 ? (body ?? "").Split(new char[] { ',' }, fieldCount) : (body ?? "").Split(',');
        }

        /// <summary>
        /// Prova a leggere un intero da un campo ASS
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <param name="number">Numero letto</param>
        /// <returns>True se valido</returns>
        public static bool TryParseInt(string value, out int number)
        {
            return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>
        /// Formatta un margine ASS a quattro cifre quando possibile
        /// </summary>
        /// <param name="value">Valore margine</param>
        /// <returns>Margine formattato</returns>
        public static string FormatMargin(int value)
        {
            if (value < 0)
            {
                value = 0;
            }

            return value.ToString("0000", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Metodi pubblici - Override tags

        /// <summary>
        /// Riscrive override tag geometrici nel testo Dialogue
        /// </summary>
        /// <param name="text">Testo ASS originale</param>
        /// <param name="transform">Trasformazione nello spazio script</param>
        /// <param name="scaledBorderAndShadow">True se border/shadow sono scalati nello spazio script</param>
        /// <param name="result">Report comune aggiornato</param>
        /// <returns>Testo riscritto</returns>
        public static string RewriteOverrideText(string text, SubtitleCanvasTransform transform, bool scaledBorderAndShadow, SubtitleCanvasRewriteResult result)
        {
            StringBuilder output = new StringBuilder();
            int pos = 0;
            int blockStart;
            int blockEnd;
            string block;
            string textChunk;
            int drawingScale = 0;

            if (string.IsNullOrEmpty(text))
            {
                return text ?? "";
            }

            if (Math.Abs(transform.ScaleX - transform.ScaleY) > 0.000001 && ContainsRotationOrShear(text))
            {
                // Non-uniform scale con rotazioni/shear richiede trasformazione affine completa: meglio fallire che spostare cartelli male
                result.Increment("parse-errors");
                return text;
            }

            // Scorre i blocchi override preservando testo normale e tag sconosciuti
            while (pos < text.Length)
            {
                blockStart = text.IndexOf('{', pos);
                if (blockStart < 0)
                {
                    textChunk = text.Substring(pos);
                    output.Append(drawingScale > 0 ? RewriteDrawingPath(textChunk, drawingScale, transform, false, result) : textChunk);
                    break;
                }

                blockEnd = text.IndexOf('}', blockStart + 1);
                if (blockEnd < 0)
                {
                    textChunk = text.Substring(pos);
                    output.Append(drawingScale > 0 ? RewriteDrawingPath(textChunk, drawingScale, transform, false, result) : textChunk);
                    break;
                }

                textChunk = text.Substring(pos, blockStart - pos);
                output.Append(drawingScale > 0 ? RewriteDrawingPath(textChunk, drawingScale, transform, false, result) : textChunk);
                block = text.Substring(blockStart + 1, blockEnd - blockStart - 1);
                output.Append('{').Append(RewriteOverrideBlock(block, transform, scaledBorderAndShadow, result)).Append('}');

                // \pN modifica il significato del testo successivo: da testo normale a path drawing ASS
                drawingScale = ResolveDrawingScale(block, drawingScale);
                pos = blockEnd + 1;
            }

            return output.ToString();
        }

        #endregion

        #region Metodi pubblici - Scale helpers

        /// <summary>
        /// Scala un margine ASS in base all'allineamento
        /// </summary>
        /// <param name="value">Margine originale</param>
        /// <param name="alignment">Allineamento ASS 1..9</param>
        /// <param name="horizontal">True per margine orizzontale</param>
        /// <param name="rightOrBottom">True per margine destro o basso</param>
        /// <param name="inputSize">Dimensione script input</param>
        /// <param name="outputSize">Dimensione script output</param>
        /// <param name="transform">Trasformazione script</param>
        /// <returns>Margine trasformato</returns>
        public static int MapMargin(int value, int alignment, bool horizontal, bool rightOrBottom, int inputSize, int outputSize, SubtitleCanvasTransform transform)
        {
            int anchor;
            int mappedAnchor;

            if (value <= 0)
            {
                return 0;
            }

            // Il margine è una distanza dall'ancora effettiva dettata dall'allineamento
            if (rightOrBottom)
            {
                anchor = inputSize - value;
                mappedAnchor = horizontal ? transform.MapX(anchor) : transform.MapY(anchor);
                return Math.Max(0, outputSize - mappedAnchor);
            }

            if (!horizontal && alignment >= 4 && alignment <= 6)
            {
                return Math.Max(0, transform.MapHeight(value));
            }

            anchor = value;
            mappedAnchor = horizontal ? transform.MapX(anchor) : transform.MapY(anchor);
            return Math.Max(0, mappedAnchor);
        }

        /// <summary>
        /// Scala un valore numerico secondo asse X o Y
        /// </summary>
        /// <param name="value">Valore originale</param>
        /// <param name="scale">Fattore scala</param>
        /// <returns>Valore scalato</returns>
        public static string ScaleNumberString(string value, double scale)
        {
            double number;
            return !double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? value : FormatNumber(number * scale);
        }

        #endregion

        #region Metodi privati - Override block

        /// <summary>
        /// Riscrive un singolo blocco override ASS
        /// </summary>
        /// <param name="block">Contenuto del blocco override senza parentesi graffe</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Blocco override riscritto</returns>
        private static string RewriteOverrideBlock(string block, SubtitleCanvasTransform transform, bool scaledBorderAndShadow, SubtitleCanvasRewriteResult result)
        {
            List<string> protectedTransforms = new List<string>();
            string rewritten = block;

            // Le trasformazioni \t(...) vengono protette per evitare doppia trasformazione dei tag interni
            rewritten = ProtectTransformTags(rewritten, transform, scaledBorderAndShadow, result, protectedTransforms);

            // Coordinate puntuali: \pos(x,y) e \org(x,y)
            rewritten = s_pointTagRegex.Replace(rewritten, match => RewritePointTag(match, transform, result));

            // Coordinate di movimento: \move(x1,y1,x2,y2[,t1,t2])
            rewritten = s_moveTagRegex.Replace(rewritten, match => RewriteMoveTag(match, transform, result));

            // Clip rettangolari e vettoriali
            rewritten = s_clipTagRegex.Replace(rewritten, match => RewriteClipTag(match, transform, result));

            // Valori dimensionali comuni
            rewritten = s_numberTagRegex.Replace(rewritten, match => RewriteNumberTag(match, transform, scaledBorderAndShadow, result));

            return RestoreTransformTags(rewritten, protectedTransforms);
        }

        /// <summary>
        /// Riscrive \pos e \org
        /// </summary>
        /// <param name="match">Match del tag da riscrivere</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Tag riscritto o valore originale se non parsabile</returns>
        private static string RewritePointTag(Match match, SubtitleCanvasTransform transform, SubtitleCanvasRewriteResult result)
        {
            string[] args = SplitArgumentList(match.Groups[2].Value);
            double x;
            double y;
            if (args.Length != 2 || !TryParseDouble(args[0], out x) || !TryParseDouble(args[1], out y))
            {
                result.Increment("parse-errors");
                return match.Value;
            }

            result.Increment(match.Groups[1].Value.Equals("pos", StringComparison.OrdinalIgnoreCase) ? "pos" : "org");
            return "\\" + match.Groups[1].Value + "(" + FormatNumber(transform.MapX(x)) + "," + FormatNumber(transform.MapY(y)) + ")";
        }

        /// <summary>
        /// Riscrive \move
        /// </summary>
        /// <param name="match">Match del tag da riscrivere</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Tag riscritto o valore originale se non parsabile</returns>
        private static string RewriteMoveTag(Match match, SubtitleCanvasTransform transform, SubtitleCanvasRewriteResult result)
        {
            string[] args = SplitArgumentList(match.Groups[1].Value);
            double x1;
            double y1;
            double x2;
            double y2;
            StringBuilder output;

            // I primi quattro argomenti sono coordinate; gli eventuali tempi finali vengono preservati
            if (args.Length < 4 || !TryParseDouble(args[0], out x1) || !TryParseDouble(args[1], out y1) || !TryParseDouble(args[2], out x2) || !TryParseDouble(args[3], out y2))
            {
                result.Increment("parse-errors");
                return match.Value;
            }

            output = new StringBuilder();
            output.Append(@"\move(");
            output.Append(FormatNumber(transform.MapX(x1))).Append(',');
            output.Append(FormatNumber(transform.MapY(y1))).Append(',');
            output.Append(FormatNumber(transform.MapX(x2))).Append(',');
            output.Append(FormatNumber(transform.MapY(y2)));
            for (int i = 4; i < args.Length; i++)
            {
                output.Append(',').Append(args[i].Trim());
            }
            output.Append(')');

            result.Increment("move");
            return output.ToString();
        }

        /// <summary>
        /// Riscrive \clip e \iclip rettangolari
        /// </summary>
        /// <param name="match">Match del tag da riscrivere</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Tag clip riscritto o valore originale se non parsabile</returns>
        private static string RewriteClipTag(Match match, SubtitleCanvasTransform transform, SubtitleCanvasRewriteResult result)
        {
            string tagName = match.Groups[1].Value;
            string[] args = SplitArgumentList(match.Groups[2].Value);
            double x1;
            double y1;
            double x2;
            double y2;
            double clipScale;
            string drawing;

            // Se non è un clip rettangolare prova le due forme vettoriali ASS: \clip(drawing) e \clip(scale,drawing)
            if (args.Length != 4 || !TryParseDouble(args[0], out x1) || !TryParseDouble(args[1], out y1) || !TryParseDouble(args[2], out x2) || !TryParseDouble(args[3], out y2))
            {
                if (args.Length == 1)
                {
                    drawing = RewriteDrawingPath(args[0], 1.0, transform, true, result);
                    result.Increment("vector-clip");
                    return "\\" + tagName + "(" + drawing + ")";
                }

                if (args.Length == 2 && TryParseDouble(args[0], out clipScale))
                {
                    drawing = RewriteDrawingPath(args[1], clipScale, transform, true, result);
                    result.Increment("vector-clip");
                    return "\\" + tagName + "(" + FormatNumber(clipScale) + "," + drawing + ")";
                }

                result.Increment("parse-errors");
                return match.Value;
            }

            // Clip rettangolare: le quattro coordinate vivono nello stesso spazio script di \pos e \move
            result.Increment("clip");
            return "\\" + tagName + "(" +
                FormatNumber(transform.MapX(x1)) + "," +
                FormatNumber(transform.MapY(y1)) + "," +
                FormatNumber(transform.MapX(x2)) + "," +
                FormatNumber(transform.MapY(y2)) + ")";
        }

        /// <summary>
        /// Riscrive tag numerici dimensionali
        /// </summary>
        /// <param name="match">Match del tag numerico da riscrivere</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Tag numerico riscritto</returns>
        private static string RewriteNumberTag(Match match, SubtitleCanvasTransform transform, bool scaledBorderAndShadow, SubtitleCanvasRewriteResult result)
        {
            string name = match.Groups["name"].Value.ToLowerInvariant();
            string value = match.Groups["value"].Value;
            double scale;
            double number;

            scale = ResolveNumberTagScale(name, transform, scaledBorderAndShadow);
            result.Increment(name);

            // Border/shadow non uniformi sono renderer-sensitive: li segnala anche quando il rewrite resta valido
            if (scaledBorderAndShadow && Math.Abs(transform.ScaleX - transform.ScaleY) > 0.000001 && IsBorderShadowBlurTag(name))
            {
                result.Increment("renderer-warnings");
            }

            // \bord e \shad vengono splittati in assi separati solo quando serve una scala non uniforme
            if (scaledBorderAndShadow && Math.Abs(transform.ScaleX - transform.ScaleY) > 0.000001 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                if (name == "bord")
                {
                    result.Increment("split-border-shadow");
                    return @"\xbord" + FormatNumber(number * transform.ScaleX) + @"\ybord" + FormatNumber(number * transform.ScaleY);
                }

                if (name == "shad")
                {
                    result.Increment("split-border-shadow");
                    return @"\xshad" + FormatNumber(number * transform.ScaleX) + @"\yshad" + FormatNumber(number * transform.ScaleY);
                }
            }

            return "\\" + match.Groups["name"].Value + ScaleNumberString(value, scale);
        }

        /// <summary>
        /// Risolve scala del tag numerico ASS
        /// </summary>
        /// <param name="name">Nome tag normalizzato</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <returns>Fattore scala da applicare al valore</returns>
        private static double ResolveNumberTagScale(string name, SubtitleCanvasTransform transform, bool scaledBorderAndShadow)
        {
            // I tag asse-specifici seguono direttamente X o Y; quelli generici usano media o restano invariati
            if (name == "fsp" || name == "xbord" || name == "xshad")
            {
                return name == "fsp" || scaledBorderAndShadow ? transform.ScaleX : 1.0;
            }

            if (name == "fs" || name == "fscx" || name == "ybord" || name == "yshad" || name == "pbo")
            {
                if ((name == "ybord" || name == "yshad") && !scaledBorderAndShadow)
                {
                    return 1.0;
                }

                return name == "fscx" && transform.ScaleY != 0.0 ? transform.ScaleX / transform.ScaleY : transform.ScaleY;
            }

            if (name == "fscy")
            {
                return 1.0;
            }

            if (name == "bord" || name == "shad" || name == "blur" || name == "be")
            {
                return scaledBorderAndShadow ? (transform.ScaleX + transform.ScaleY) / 2.0 : 1.0;
            }

            return 1.0;
        }

        /// <summary>
        /// Indica se un tag è renderer-sensitive per border/shadow
        /// </summary>
        /// <param name="name">Nome tag normalizzato</param>
        /// <returns>True se il tag riguarda border, shadow o blur</returns>
        private static bool IsBorderShadowBlurTag(string name)
        {
            return name == "bord" || name == "xbord" || name == "ybord" ||
                name == "shad" || name == "xshad" || name == "yshad" ||
                name == "blur" || name == "be";
        }

        #endregion

        #region Metodi privati - Transform e drawing

        /// <summary>
        /// Riscrive tag \t(...) ricorsivamente
        /// </summary>
        /// <param name="block">Contenuto del blocco override da processare</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <param name="protectedTransforms">Lista dei transform riscritti e protetti</param>
        /// <returns>Blocco con transform sostituiti da placeholder privati</returns>
        private static string ProtectTransformTags(string block, SubtitleCanvasTransform transform, bool scaledBorderAndShadow, SubtitleCanvasRewriteResult result, List<string> protectedTransforms)
        {
            StringBuilder output = new StringBuilder();
            int pos = 0;
            int tagStart;
            int contentStart;
            int contentEnd;
            string content;
            string replacement;

            // Cerca \t( e usa matching parentesi per non rompere clip o tag annidati
            while (pos < block.Length)
            {
                tagStart = block.IndexOf(@"\t(", pos, StringComparison.OrdinalIgnoreCase);
                if (tagStart < 0)
                {
                    output.Append(block.Substring(pos));
                    break;
                }

                contentStart = tagStart + 3;
                contentEnd = FindMatchingParenthesis(block, contentStart - 1);
                if (contentEnd < 0)
                {
                    result.Increment("parse-errors");
                    output.Append(block.Substring(pos));
                    break;
                }

                output.Append(block.Substring(pos, tagStart - pos));
                content = block.Substring(contentStart, contentEnd - contentStart);

                // Riscrive il contenuto del transform una sola volta e lo protegge dal pass regex esterno
                replacement = @"\t(" + RewriteTransformContent(content, transform, scaledBorderAndShadow, result) + ")";
                output.Append('\uE000').Append(protectedTransforms.Count.ToString(CultureInfo.InvariantCulture)).Append('\uE001');
                protectedTransforms.Add(replacement);
                result.Increment("transform");
                pos = contentEnd + 1;
            }

            return output.ToString();
        }

        /// <summary>
        /// Ripristina blocchi \t(...) protetti
        /// </summary>
        /// <param name="value">Testo contenente placeholder privati</param>
        /// <param name="protectedTransforms">Transform riscritti da ripristinare</param>
        /// <returns>Testo con transform ripristinati</returns>
        private static string RestoreTransformTags(string value, List<string> protectedTransforms)
        {
            string result = value;
            for (int i = 0; i < protectedTransforms.Count; i++)
            {
                result = result.Replace("\uE000" + i.ToString(CultureInfo.InvariantCulture) + "\uE001", protectedTransforms[i]);
            }

            return result;
        }

        /// <summary>
        /// Riscrive il contenuto di \t preservando tempi e accelerazione
        /// </summary>
        /// <param name="content">Contenuto interno del transform ASS</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Contenuto transform riscritto</returns>
        private static string RewriteTransformContent(string content, SubtitleCanvasTransform transform, bool scaledBorderAndShadow, SubtitleCanvasRewriteResult result)
        {
            int tagStart = content.IndexOf('\\');
            if (tagStart < 0)
            {
                result.Increment("parse-errors");
                return content;
            }

            return content.Substring(0, tagStart) + RewriteOverrideBlock(content.Substring(tagStart), transform, scaledBorderAndShadow, result);
        }

        /// <summary>
        /// Trova la parentesi chiusa corrispondente
        /// </summary>
        /// <param name="value">Testo in cui cercare</param>
        /// <param name="openIndex">Indice della parentesi aperta</param>
        /// <returns>Indice della parentesi chiusa, -1 se assente</returns>
        private static int FindMatchingParenthesis(string value, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < value.Length; i++)
            {
                if (value[i] == '(')
                {
                    depth++;
                }
                else if (value[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Risolve il drawing scale corrente da un blocco override
        /// </summary>
        /// <param name="block">Blocco override ASS</param>
        /// <param name="currentScale">Scala drawing corrente</param>
        /// <returns>Nuova scala drawing corrente</returns>
        private static int ResolveDrawingScale(string block, int currentScale)
        {
            MatchCollection matches = Regex.Matches(block, @"\\p(?<scale>[+-]?\d+)", RegexOptions.IgnoreCase);
            int scale;

            if (matches.Count == 0)
            {
                return currentScale;
            }

            if (!int.TryParse(matches[matches.Count - 1].Groups["scale"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out scale) || scale <= 0)
            {
                return 0;
            }

            return 1 << Math.Min(20, scale - 1);
        }

        /// <summary>
        /// Riscrive un path drawing ASS
        /// </summary>
        /// <param name="drawing">Path drawing originale</param>
        /// <param name="drawingScale">Scala drawing ASS corrente</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="absoluteCoordinates">True se il path usa coordinate assolute</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Path drawing riscritto o originale se non parsabile</returns>
        private static string RewriteDrawingPath(string drawing, double drawingScale, SubtitleCanvasTransform transform, bool absoluteCoordinates, SubtitleCanvasRewriteResult result)
        {
            MatchCollection tokens = Regex.Matches(drawing ?? "", @"[A-Za-z]|[+-]?\d+(?:\.\d+)?");
            StringBuilder output = new StringBuilder();
            int pos = 0;
            string command;

            // Il parser è command-driven: preserva semantica, normalizzando gli spazi del path
            while (pos < tokens.Count)
            {
                command = tokens[pos].Value.ToLowerInvariant();
                if (!IsDrawingCommand(command))
                {
                    result.Increment("parse-errors");
                    return drawing;
                }

                AppendToken(output, command);
                pos++;
                if (command == "m" || command == "n" || command == "l" || command == "p")
                {
                    // Comandi a punto singolo: move/line/extend portano una sola coppia x,y
                    if (!AppendDrawingPoint(tokens, ref pos, output, drawingScale, transform, absoluteCoordinates))
                    {
                        result.Increment("parse-errors");
                        return drawing;
                    }
                }
                else if (command == "b")
                {
                    // Bezier cubico: tre punti di controllo consecutivi
                    for (int i = 0; i < 3; i++)
                    {
                        if (!AppendDrawingPoint(tokens, ref pos, output, drawingScale, transform, absoluteCoordinates))
                        {
                            result.Increment("parse-errors");
                            return drawing;
                        }
                    }
                }
                else if (command == "s")
                {
                    // Spline: numero variabile di punti fino al prossimo comando drawing
                    while (pos < tokens.Count && !IsDrawingCommand(tokens[pos].Value.ToLowerInvariant()))
                    {
                        if (!AppendDrawingPoint(tokens, ref pos, output, drawingScale, transform, absoluteCoordinates))
                        {
                            result.Increment("parse-errors");
                            return drawing;
                        }
                    }
                }
            }

            result.Increment(absoluteCoordinates ? "vector-drawing" : "drawing");
            return output.ToString();
        }

        /// <summary>
        /// Aggiunge un punto drawing trasformato
        /// </summary>
        /// <param name="tokens">Token del path drawing</param>
        /// <param name="pos">Indice corrente, avanzato al punto successivo</param>
        /// <param name="output">Buffer output del path riscritto</param>
        /// <param name="drawingScale">Scala drawing ASS corrente</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="absoluteCoordinates">True se il punto usa coordinate assolute</param>
        /// <returns>True se il punto è stato letto e scritto</returns>
        private static bool AppendDrawingPoint(MatchCollection tokens, ref int pos, StringBuilder output, double drawingScale, SubtitleCanvasTransform transform, bool absoluteCoordinates)
        {
            double x;
            double y;

            if (pos + 1 >= tokens.Count || !TryParseDouble(tokens[pos].Value, out x) || !TryParseDouble(tokens[pos + 1].Value, out y))
            {
                return false;
            }

            if (absoluteCoordinates)
            {
                x = transform.MapX(x / drawingScale) * drawingScale;
                y = transform.MapY(y / drawingScale) * drawingScale;
            }
            else
            {
                x *= transform.ScaleX;
                y *= transform.ScaleY;
            }

            AppendToken(output, FormatNumber(x));
            AppendToken(output, FormatNumber(y));
            pos += 2;
            return true;
        }

        /// <summary>
        /// Indica se il token è un comando drawing
        /// </summary>
        /// <param name="value">Token da verificare</param>
        /// <returns>True se il token è un comando drawing ASS</returns>
        private static bool IsDrawingCommand(string value)
        {
            return value == "m" || value == "n" || value == "l" || value == "b" || value == "s" || value == "p" || value == "c";
        }

        /// <summary>
        /// Indica se il testo contiene trasformazioni renderer-sensitive con scale non uniforme
        /// </summary>
        /// <param name="text">Testo ASS da verificare</param>
        /// <returns>True se contiene rotazioni o shear</returns>
        private static bool ContainsRotationOrShear(string text)
        {
            return Regex.IsMatch(text ?? "", @"\\(frx|fry|frz|fr(?![a-z])|fax|fay)", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Aggiunge un token normalizzando lo spazio
        /// </summary>
        /// <param name="output">Buffer output del path riscritto</param>
        /// <param name="token">Token da aggiungere</param>
        private static void AppendToken(StringBuilder output, string token)
        {
            if (output.Length > 0)
            {
                output.Append(' ');
            }

            output.Append(token);
        }

        #endregion

        #region Metodi privati - Parsing numerico

        /// <summary>
        /// Divide argomenti separati da virgole
        /// </summary>
        /// <param name="value">Lista argomenti originale</param>
        /// <returns>Argomenti divisi e trimmeti</returns>
        private static string[] SplitArgumentList(string value)
        {
            string[] raw = (value ?? "").Split(',');
            List<string> result = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                result.Add(raw[i].Trim());
            }

            return result.ToArray();
        }

        /// <summary>
        /// Prova a leggere un double ASS
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <param name="number">Numero letto</param>
        /// <returns>True se il valore è un double valido</returns>
        private static bool TryParseDouble(string value, out double number)
        {
            return double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>
        /// Formatta un numero ASS compatto
        /// </summary>
        /// <param name="value">Numero da formattare</param>
        /// <returns>Numero formattato con invariant culture</returns>
        private static string FormatNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
