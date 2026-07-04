using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive canvas e coordinate di sottotitoli ASS/SSA
    /// </summary>
    internal class AssSubtitleCanvasRewriter : ISubtitleCanvasRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Indica se il rewriter gestisce la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>True se il codec è ASS/SSA</returns>
        public bool CanHandle(TrackInfo track)
        {
            string codec = track != null && track.Codec != null ? track.Codec.ToLowerInvariant() : "";
            return codec.Contains("substation alpha") ||
                codec.Contains("substationalpha") ||
                codec.Contains("s_text/ass") ||
                codec.Contains("s_text/ssa") ||
                codec == "ass" ||
                codec == "ssa";
        }

        /// <summary>
        /// Restituisce l'estensione del file principale gestito dal rewriter
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Estensione ASS o SSA</returns>
        public string GetPrimaryExtension(TrackInfo track)
        {
            string codec = track != null && track.Codec != null ? track.Codec.ToLowerInvariant() : "";
            return codec.Contains("ssa") ? ".ssa" : ".ass";
        }

        /// <summary>
        /// Riscrive il file ASS/SSA estratto
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="track">Traccia sottotitoli</param>
        /// <param name="inputFile">File input</param>
        /// <param name="outputFile">File output</param>
        /// <param name="result">Risultato riscrittura</param>
        /// <returns>True se riscrittura riuscita</returns>
        public bool Rewrite(SubtitleCanvasRewriteContext context, TrackInfo track, string inputFile, string outputFile, out SubtitleCanvasRewriteResult result)
        {
            AssSubtitleDocument document;
            SubtitleCanvasTransform scriptTransform;
            string content;
            int inputPlayResX;
            int inputPlayResY;
            int outputPlayResX;
            int outputPlayResY;
            bool hadLayoutResX;
            bool hadLayoutResY;
            bool scaledBorderAndShadow;
            AssTextFile textFile;

            result = new SubtitleCanvasRewriteResult();
            result.Format = this.GetPrimaryExtension(track).Equals(".ssa", StringComparison.OrdinalIgnoreCase) ? "SSA" : "ASS";

            if (context == null || context.Transform == null)
            {
                result.ErrorMessage = "contesto canvas ASS mancante";
                return false;
            }

            textFile = this.ReadTextFile(inputFile);
            content = textFile.Content;
            document = AssSubtitleDocument.Parse(content);
            scaledBorderAndShadow = this.ResolveScaledBorderAndShadow(document, result);

            // Risolve PlayRes di input/output e converte il crop video nello spazio script ASS
            if (!document.TryGetScriptInfoInt("PlayResX", out inputPlayResX))
            {
                inputPlayResX = context.Transform.InputDisplayWidth > 0 ? context.Transform.InputDisplayWidth : context.Transform.InputCanvasWidth;
                result.Increment("missing-playres");
            }
            if (!document.TryGetScriptInfoInt("PlayResY", out inputPlayResY))
            {
                inputPlayResY = context.Transform.InputDisplayHeight > 0 ? context.Transform.InputDisplayHeight : context.Transform.InputCanvasHeight;
                result.Increment("missing-playres");
            }

            outputPlayResX = context.Transform.OutputDisplayWidth > 0 ? context.Transform.OutputDisplayWidth : context.Transform.OutputCanvasWidth;
            outputPlayResY = context.Transform.OutputDisplayHeight > 0 ? context.Transform.OutputDisplayHeight : context.Transform.OutputCanvasHeight;
            scriptTransform = context.Transform.CreateCoordinateTransform(inputPlayResX, inputPlayResY, outputPlayResX, outputPlayResY);

            // Aggiorna solo le risoluzioni script già semanticamente presenti nel file
            hadLayoutResX = document.TryGetScriptInfoInt("LayoutResX", out _);
            hadLayoutResY = document.TryGetScriptInfoInt("LayoutResY", out _);
            document.SetOrAddScriptInfoInt("PlayResX", outputPlayResX);
            document.SetOrAddScriptInfoInt("PlayResY", outputPlayResY);
            if (hadLayoutResX)
            {
                document.SetOrAddScriptInfoInt("LayoutResX", outputPlayResX);
            }
            if (hadLayoutResY)
            {
                document.SetOrAddScriptInfoInt("LayoutResY", outputPlayResY);
            }

            // Riscrive le sezioni renderizzabili e abortisce se un tag geometrico non è parsabile in sicurezza
            this.RewriteBody(document, scriptTransform, inputPlayResX, inputPlayResY, outputPlayResX, outputPlayResY, scaledBorderAndShadow, result);
            if (result.Get("parse-errors") > 0)
            {
                result.ErrorMessage = "ASS canvas rewrite incompleto: tag geometrici non parsabili";
                return false;
            }

            result.Summary = "styles=" + result.Get("styles").ToString(CultureInfo.InvariantCulture) +
                ", dialogue=" + result.Get("dialogue").ToString(CultureInfo.InvariantCulture) +
                ", pos=" + result.Get("pos").ToString(CultureInfo.InvariantCulture) +
                ", move=" + result.Get("move").ToString(CultureInfo.InvariantCulture) +
                ", clip=" + result.Get("clip").ToString(CultureInfo.InvariantCulture) +
                ", vector=" + (result.Get("vector-clip") + result.Get("vector-drawing") + result.Get("drawing")).ToString(CultureInfo.InvariantCulture) +
                ", t=" + result.Get("transform").ToString(CultureInfo.InvariantCulture) +
                ", comment=" + result.Get("comment").ToString(CultureInfo.InvariantCulture) +
                ", warn=" + result.Get("renderer-warnings").ToString(CultureInfo.InvariantCulture) +
                ", fallback-playres=" + result.Get("missing-playres").ToString(CultureInfo.InvariantCulture);

            this.WriteTextFile(outputFile, document.Serialize(), textFile);
            return true;
        }

        /// <summary>
        /// Valida output ASS/SSA in modo leggero
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="outputFile">File output</param>
        /// <returns>True se file presente e non vuoto</returns>
        public bool ValidateOutput(SubtitleCanvasRewriteContext context, string outputFile)
        {
            FileInfo info = new FileInfo(outputFile);
            return info.Exists && info.Length > 0;
        }

        #endregion

        #region Metodi privati - Rewrite documento

        /// <summary>
        /// Riscrive sezioni Style ed Events
        /// </summary>
        /// <param name="document">Documento ASS/SSA da riscrivere</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="inputPlayResX">PlayResX input</param>
        /// <param name="inputPlayResY">PlayResY input</param>
        /// <param name="outputPlayResX">PlayResX output</param>
        /// <param name="outputPlayResY">PlayResY output</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        private void RewriteBody(AssSubtitleDocument document, SubtitleCanvasTransform transform, int inputPlayResX, int inputPlayResY, int outputPlayResX, int outputPlayResY, bool scaledBorderAndShadow, SubtitleCanvasRewriteResult result)
        {
            Dictionary<string, AssStyleInfo> styles = new Dictionary<string, AssStyleInfo>(StringComparer.OrdinalIgnoreCase);
            AssStyleFormat styleFormat = new AssStyleFormat(false);
            AssEventFormat eventFormat = new AssEventFormat();
            string trimmed;
            bool inStyles = false;
            bool inEvents = false;
            bool legacySsaStyles = false;

            // Scorre il documento preservando le righe non gestite e cambiando stato solo sulle sezioni ASS note
            for (int i = 0; i < document.Lines.Count; i++)
            {
                trimmed = document.Lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    inStyles = string.Equals(trimmed, "[V4+ Styles]", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                    legacySsaStyles = string.Equals(trimmed, "[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                    inEvents = string.Equals(trimmed, "[Events]", StringComparison.OrdinalIgnoreCase);
                    if (inStyles)
                    {
                        styleFormat = new AssStyleFormat(legacySsaStyles);
                    }
                    continue;
                }

                // I Format: possono cambiare l'ordine dei campi, quindi vanno risolti prima delle righe dati
                if (inStyles && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    styleFormat = new AssStyleFormat(trimmed.Substring(7), legacySsaStyles);
                    continue;
                }

                // Gli style definiscono font, bordi, margini e alignment usati poi dagli eventi
                if (inStyles && trimmed.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                {
                    document.Lines[i] = this.RewriteStyleLine(document.Lines[i], styleFormat, transform, inputPlayResX, inputPlayResY, outputPlayResX, outputPlayResY, scaledBorderAndShadow, styles, result);
                    continue;
                }

                if (inEvents && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    eventFormat = new AssEventFormat(trimmed.Substring(7));
                    continue;
                }

                // Dialogue e Comment condividono lo stesso layout di campi: cambia solo il contatore di report
                if (inEvents && trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                {
                    document.Lines[i] = this.RewriteEventLine(document.Lines[i], "Dialogue:", eventFormat, styles, transform, inputPlayResX, inputPlayResY, outputPlayResX, outputPlayResY, scaledBorderAndShadow, "dialogue", result);
                }
                else if (inEvents && trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                {
                    document.Lines[i] = this.RewriteEventLine(document.Lines[i], "Comment:", eventFormat, styles, transform, inputPlayResX, inputPlayResY, outputPlayResX, outputPlayResY, scaledBorderAndShadow, "comment", result);
                }
            }
        }

        /// <summary>
        /// Riscrive una riga Style
        /// </summary>
        /// <param name="line">Riga Style originale</param>
        /// <param name="format">Formato campi Style</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="inputPlayResX">PlayResX input</param>
        /// <param name="inputPlayResY">PlayResY input</param>
        /// <param name="outputPlayResX">PlayResX output</param>
        /// <param name="outputPlayResY">PlayResY output</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <param name="styles">Mappa style popolata per gli eventi successivi</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Riga Style riscritta</returns>
        private string RewriteStyleLine(string line, AssStyleFormat format, SubtitleCanvasTransform transform, int inputPlayResX, int inputPlayResY, int outputPlayResX, int outputPlayResY, bool scaledBorderAndShadow, Dictionary<string, AssStyleInfo> styles, SubtitleCanvasRewriteResult result)
        {
            string prefix = "Style:";
            string[] fields = AssSubtitleUtils.SplitFields(line.Substring(prefix.Length), format.FieldCount);
            AssStyleInfo styleInfo = new AssStyleInfo();
            int margin;
            double scaleX;
            double scaleY;
            double averageScale;

            if (format.FieldCount <= 0 || fields.Length < format.FieldCount)
            {
                result.Increment("parse-errors");
                return line;
            }

            scaleX = transform.ScaleX;
            scaleY = transform.ScaleY;
            averageScale = (scaleX + scaleY) / 2.0;

            // Memorizza nome e alignment dello style per mappare correttamente i margini evento
            styleInfo.Name = this.GetField(fields, format.NameIndex).Trim();
            styleInfo.Alignment = this.ReadIntField(fields, format.AlignmentIndex, 2);

            // Font e bordi vengono scalati seguendo il target libass moderno
            this.ScaleField(fields, format.FontSizeIndex, scaleY);
            this.ScalePercentField(fields, format.ScaleXIndex, scaleY != 0.0 ? scaleX / scaleY : 1.0);
            this.ScalePercentField(fields, format.ScaleYIndex, 1.0);
            this.ScaleField(fields, format.SpacingIndex, scaleX);
            this.ScaleField(fields, format.OutlineIndex, scaledBorderAndShadow ? averageScale : 1.0);
            this.ScaleField(fields, format.ShadowIndex, scaledBorderAndShadow ? averageScale : 1.0);
            if (scaledBorderAndShadow && Math.Abs(scaleX - scaleY) > 0.000001 && (format.OutlineIndex >= 0 || format.ShadowIndex >= 0))
            {
                result.Increment("renderer-warnings");
            }

            // I margini style sono distanze dall'ancora determinata dall'alignment, non semplici width/height
            if (format.MarginLIndex >= 0 && AssSubtitleUtils.TryParseInt(fields[format.MarginLIndex], out margin))
            {
                fields[format.MarginLIndex] = AssSubtitleUtils.FormatMargin(AssSubtitleUtils.MapMargin(margin, styleInfo.Alignment, true, false, inputPlayResX, outputPlayResX, transform));
            }
            if (format.MarginRIndex >= 0 && AssSubtitleUtils.TryParseInt(fields[format.MarginRIndex], out margin))
            {
                fields[format.MarginRIndex] = AssSubtitleUtils.FormatMargin(AssSubtitleUtils.MapMargin(margin, styleInfo.Alignment, true, true, inputPlayResX, outputPlayResX, transform));
            }
            if (format.MarginVIndex >= 0 && AssSubtitleUtils.TryParseInt(fields[format.MarginVIndex], out margin))
            {
                fields[format.MarginVIndex] = AssSubtitleUtils.FormatMargin(AssSubtitleUtils.MapMargin(margin, styleInfo.Alignment, false, styleInfo.Alignment <= 3, inputPlayResY, outputPlayResY, transform));
            }

            // Lo style riscritto diventa riferimento per i Dialogue/Comment successivi
            if (!string.IsNullOrEmpty(styleInfo.Name))
            {
                styles[styleInfo.Name] = styleInfo;
            }

            result.Increment("styles");
            return prefix + string.Join(",", fields);
        }

        /// <summary>
        /// Riscrive una riga evento
        /// </summary>
        /// <param name="line">Riga evento originale</param>
        /// <param name="prefix">Prefisso evento da preservare</param>
        /// <param name="format">Formato campi evento</param>
        /// <param name="styles">Mappa style già riscritti</param>
        /// <param name="transform">Trasformazione coordinate script da applicare</param>
        /// <param name="inputPlayResX">PlayResX input</param>
        /// <param name="inputPlayResY">PlayResY input</param>
        /// <param name="outputPlayResX">PlayResX output</param>
        /// <param name="outputPlayResY">PlayResY output</param>
        /// <param name="scaledBorderAndShadow">True se border e shadow vanno scalati</param>
        /// <param name="counterName">Nome contatore report da incrementare</param>
        /// <param name="result">Report aggiornato durante la riscrittura</param>
        /// <returns>Riga evento riscritta</returns>
        private string RewriteEventLine(string line, string prefix, AssEventFormat format, Dictionary<string, AssStyleInfo> styles, SubtitleCanvasTransform transform, int inputPlayResX, int inputPlayResY, int outputPlayResX, int outputPlayResY, bool scaledBorderAndShadow, string counterName, SubtitleCanvasRewriteResult result)
        {
            string[] fields = AssSubtitleUtils.SplitFields(line.Substring(prefix.Length), format.FieldCount);
            AssStyleInfo styleInfo;
            string styleName;
            int alignment = 2;
            int margin;

            if (format.FieldCount <= 0 || fields.Length < format.FieldCount)
            {
                result.Increment("parse-errors");
                return line;
            }

            styleName = this.GetField(fields, format.StyleIndex).Trim();

            // Risolve alignment effettivo dalla riga Style già processata, con fallback ASS bottom-center
            if (!string.IsNullOrEmpty(styleName) && styles.TryGetValue(styleName, out styleInfo))
            {
                alignment = styleInfo.Alignment;
            }

            // I margini evento override prevalgono su quelli di style e vanno mappati nello spazio script
            if (format.MarginLIndex >= 0 && AssSubtitleUtils.TryParseInt(fields[format.MarginLIndex], out margin) && margin > 0)
            {
                fields[format.MarginLIndex] = AssSubtitleUtils.FormatMargin(AssSubtitleUtils.MapMargin(margin, alignment, true, false, inputPlayResX, outputPlayResX, transform));
                result.Increment("margins");
            }
            if (format.MarginRIndex >= 0 && AssSubtitleUtils.TryParseInt(fields[format.MarginRIndex], out margin) && margin > 0)
            {
                fields[format.MarginRIndex] = AssSubtitleUtils.FormatMargin(AssSubtitleUtils.MapMargin(margin, alignment, true, true, inputPlayResX, outputPlayResX, transform));
                result.Increment("margins");
            }
            if (format.MarginVIndex >= 0 && AssSubtitleUtils.TryParseInt(fields[format.MarginVIndex], out margin) && margin > 0)
            {
                fields[format.MarginVIndex] = AssSubtitleUtils.FormatMargin(AssSubtitleUtils.MapMargin(margin, alignment, false, alignment <= 3, inputPlayResY, outputPlayResY, transform));
                result.Increment("margins");
            }

            // Il campo Text contiene override tag, clip e drawing: viene riscritto solo dopo aver sistemato i margini
            if (format.TextIndex >= 0 && format.TextIndex < fields.Length)
            {
                fields[format.TextIndex] = AssSubtitleUtils.RewriteOverrideText(fields[format.TextIndex], transform, scaledBorderAndShadow, result);
            }

            result.Increment(counterName);
            return prefix + string.Join(",", fields);
        }

        #endregion

        #region Metodi privati - Utility

        /// <summary>
        /// Legge un campo con fallback vuoto
        /// </summary>
        /// <param name="fields">Campi della riga ASS</param>
        /// <param name="index">Indice campo da leggere</param>
        /// <returns>Valore campo o stringa vuota</returns>
        private string GetField(string[] fields, int index)
        {
            if (index < 0 || index >= fields.Length)
            {
                return "";
            }

            return fields[index];
        }

        /// <summary>
        /// Legge un intero da un campo con fallback
        /// </summary>
        /// <param name="fields">Campi della riga ASS</param>
        /// <param name="index">Indice campo da leggere</param>
        /// <param name="fallback">Valore fallback</param>
        /// <returns>Intero letto o fallback</returns>
        private int ReadIntField(string[] fields, int index, int fallback)
        {
            int result;
            if (index < 0 || index >= fields.Length || !AssSubtitleUtils.TryParseInt(fields[index], out result))
            {
                result = fallback;
            }

            return result;
        }

        /// <summary>
        /// Scala un campo numerico
        /// </summary>
        /// <param name="fields">Campi della riga ASS</param>
        /// <param name="index">Indice campo da scalare</param>
        /// <param name="scale">Fattore scala</param>
        private void ScaleField(string[] fields, int index, double scale)
        {
            if (index >= 0 && index < fields.Length)
            {
                fields[index] = AssSubtitleUtils.ScaleNumberString(fields[index], scale);
            }
        }

        /// <summary>
        /// Scala un campo percentuale ASS
        /// </summary>
        /// <param name="fields">Campi della riga ASS</param>
        /// <param name="index">Indice campo da scalare</param>
        /// <param name="scale">Fattore scala</param>
        private void ScalePercentField(string[] fields, int index, double scale)
        {
            this.ScaleField(fields, index, scale);
        }

        /// <summary>
        /// Legge un file testo preservando encoding quando riconoscibile
        /// </summary>
        /// <param name="filePath">Percorso file da leggere</param>
        /// <returns>Contenuto testo con informazioni encoding</returns>
        private AssTextFile ReadTextFile(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            AssTextFile result = new AssTextFile();
            int offset = 0;

            // Riconosce BOM comuni per preservare encoding e preambolo del file estratto
            if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf)
            {
                result.Encoding = new UTF8Encoding(true);
                result.HasPreamble = true;
                offset = 3;
            }
            else if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe)
            {
                result.Encoding = Encoding.Unicode;
                result.HasPreamble = true;
                offset = 2;
            }
            else if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff)
            {
                result.Encoding = Encoding.BigEndianUnicode;
                result.HasPreamble = true;
                offset = 2;
            }

            // Decodifica dal primo byte testuale, lasciando al writer il ripristino del preambolo
            result.Content = result.Encoding.GetString(data, offset, data.Length - offset);
            return result;
        }

        /// <summary>
        /// Scrive un file testo con lo stesso encoding riconosciuto in lettura
        /// </summary>
        /// <param name="filePath">Percorso file da scrivere</param>
        /// <param name="content">Contenuto testo da scrivere</param>
        /// <param name="source">Informazioni encoding del file sorgente</param>
        private void WriteTextFile(string filePath, string content, AssTextFile source)
        {
            byte[] textBytes = source.Encoding.GetBytes(content);
            byte[] preamble = source.HasPreamble ? source.Encoding.GetPreamble() : new byte[0];
            byte[] output = new byte[preamble.Length + textBytes.Length];

            if (preamble.Length > 0)
            {
                Array.Copy(preamble, 0, output, 0, preamble.Length);
            }
            Array.Copy(textBytes, 0, output, preamble.Length, textBytes.Length);
            File.WriteAllBytes(filePath, output);
        }

        /// <summary>
        /// Risolve ScaledBorderAndShadow, default libass/Aegisub moderno: yes
        /// </summary>
        /// <param name="document">Documento ASS/SSA da leggere</param>
        /// <param name="result">Report aggiornato durante la lettura</param>
        /// <returns>True se border e shadow vanno scalati</returns>
        private bool ResolveScaledBorderAndShadow(AssSubtitleDocument document, SubtitleCanvasRewriteResult result)
        {
            bool value;
            if (document.TryGetScriptInfoBool("ScaledBorderAndShadow", out value))
            {
                if (!value)
                {
                    result.Increment("unscaled-border-shadow");
                }

                return value;
            }

            return true;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Indici campi Style
        /// </summary>
        private class AssStyleFormat
        {
            /// <summary>
            /// Costruttore default
            /// </summary>
            /// <param name="legacySsa">True se il formato è SSA V4</param>
            public AssStyleFormat(bool legacySsa)
            {
                this.NameIndex = 0;
                this.FontSizeIndex = 2;
                this.ScaleXIndex = -1;
                this.ScaleYIndex = -1;
                this.SpacingIndex = -1;
                this.OutlineIndex = -1;
                this.ShadowIndex = -1;
                this.AlignmentIndex = -1;
                this.MarginLIndex = -1;
                this.MarginRIndex = -1;
                this.MarginVIndex = -1;
                this.FieldCount = legacySsa ? 18 : 23;
                if (legacySsa)
                {
                    // SSA [V4 Styles] non ha ScaleX/ScaleY/Spacing e usa indici diversi per bordi/margini
                    this.OutlineIndex = 10;
                    this.ShadowIndex = 11;
                    this.AlignmentIndex = 12;
                    this.MarginLIndex = 13;
                    this.MarginRIndex = 14;
                    this.MarginVIndex = 15;
                }
                else
                {
                    // ASS [V4+ Styles] standard: campi estesi usati da libass/Aegisub
                    this.ScaleXIndex = 11;
                    this.ScaleYIndex = 12;
                    this.SpacingIndex = 13;
                    this.OutlineIndex = 16;
                    this.ShadowIndex = 17;
                    this.AlignmentIndex = 18;
                    this.MarginLIndex = 19;
                    this.MarginRIndex = 20;
                    this.MarginVIndex = 21;
                }
            }

            /// <summary>
            /// Costruttore da riga Format
            /// </summary>
            /// <param name="format">Contenuto della riga Format</param>
            /// <param name="legacySsa">True se il formato è SSA V4</param>
            public AssStyleFormat(string format, bool legacySsa)
                : this(legacySsa)
            {
                this.FieldCount = AssSubtitleUtils.CountFields(format);
                this.NameIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Name");
                this.FontSizeIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Fontsize");
                this.ScaleXIndex = AssSubtitleUtils.ResolveFieldIndex(format, "ScaleX");
                this.ScaleYIndex = AssSubtitleUtils.ResolveFieldIndex(format, "ScaleY");
                this.SpacingIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Spacing");
                this.OutlineIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Outline");
                this.ShadowIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Shadow");
                this.AlignmentIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Alignment");
                this.MarginLIndex = AssSubtitleUtils.ResolveFieldIndex(format, "MarginL");
                this.MarginRIndex = AssSubtitleUtils.ResolveFieldIndex(format, "MarginR");
                this.MarginVIndex = AssSubtitleUtils.ResolveFieldIndex(format, "MarginV");
            }

            /// <summary>
            /// Numero campi attesi nella riga Style
            /// </summary>
            public int FieldCount { get; private set; }

            /// <summary>
            /// Indice campo Name
            /// </summary>
            public int NameIndex { get; private set; }

            /// <summary>
            /// Indice campo FontSize
            /// </summary>
            public int FontSizeIndex { get; private set; }

            /// <summary>
            /// Indice campo ScaleX
            /// </summary>
            public int ScaleXIndex { get; private set; }

            /// <summary>
            /// Indice campo ScaleY
            /// </summary>
            public int ScaleYIndex { get; private set; }

            /// <summary>
            /// Indice campo Spacing
            /// </summary>
            public int SpacingIndex { get; private set; }

            /// <summary>
            /// Indice campo Outline
            /// </summary>
            public int OutlineIndex { get; private set; }

            /// <summary>
            /// Indice campo Shadow
            /// </summary>
            public int ShadowIndex { get; private set; }

            /// <summary>
            /// Indice campo Alignment
            /// </summary>
            public int AlignmentIndex { get; private set; }

            /// <summary>
            /// Indice campo MarginL
            /// </summary>
            public int MarginLIndex { get; private set; }

            /// <summary>
            /// Indice campo MarginR
            /// </summary>
            public int MarginRIndex { get; private set; }

            /// <summary>
            /// Indice campo MarginV
            /// </summary>
            public int MarginVIndex { get; private set; }
        }

        /// <summary>
        /// Indici campi Dialogue
        /// </summary>
        private class AssEventFormat
        {
            /// <summary>
            /// Costruttore default
            /// </summary>
            public AssEventFormat()
            {
                this.FieldCount = 10;
                this.StyleIndex = 3;
                this.MarginLIndex = 5;
                this.MarginRIndex = 6;
                this.MarginVIndex = 7;
                this.TextIndex = 9;
            }

            /// <summary>
            /// Costruttore da riga Format
            /// </summary>
            public AssEventFormat(string format)
                : this()
            {
                this.FieldCount = AssSubtitleUtils.CountFields(format);
                this.StyleIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Style");
                this.MarginLIndex = AssSubtitleUtils.ResolveFieldIndex(format, "MarginL");
                this.MarginRIndex = AssSubtitleUtils.ResolveFieldIndex(format, "MarginR");
                this.MarginVIndex = AssSubtitleUtils.ResolveFieldIndex(format, "MarginV");
                this.TextIndex = AssSubtitleUtils.ResolveFieldIndex(format, "Text");
            }

            /// <summary>
            /// Numero campi attesi nella riga evento
            /// </summary>
            public int FieldCount { get; private set; }

            /// <summary>
            /// Indice campo Style
            /// </summary>
            public int StyleIndex { get; private set; }

            /// <summary>
            /// Indice campo MarginL
            /// </summary>
            public int MarginLIndex { get; private set; }

            /// <summary>
            /// Indice campo MarginR
            /// </summary>
            public int MarginRIndex { get; private set; }

            /// <summary>
            /// Indice campo MarginV
            /// </summary>
            public int MarginVIndex { get; private set; }

            /// <summary>
            /// Indice campo Text
            /// </summary>
            public int TextIndex { get; private set; }
        }

        /// <summary>
        /// Informazioni style usate per gli eventi
        /// </summary>
        private class AssStyleInfo
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            public AssStyleInfo()
            {
                this.Name = "";
                this.Alignment = 2;
            }

            /// <summary>
            /// Nome dello style
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Alignment ASS/SSA associato allo style
            /// </summary>
            public int Alignment { get; set; }
        }

        /// <summary>
        /// Testo ASS con encoding originale
        /// </summary>
        private class AssTextFile
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            public AssTextFile()
            {
                this.Content = "";
                this.Encoding = new UTF8Encoding(false);
            }

            /// <summary>
            /// Contenuto testuale normalizzato
            /// </summary>
            public string Content { get; set; }

            /// <summary>
            /// Encoding originale da preservare in scrittura
            /// </summary>
            public Encoding Encoding { get; set; }

            /// <summary>
            /// True se il file originale aveva preambolo BOM
            /// </summary>
            public bool HasPreamble { get; set; }
        }

        #endregion
    }
}
