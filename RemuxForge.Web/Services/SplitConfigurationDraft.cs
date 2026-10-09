using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Modalità di taglio presentate dal wizard Split e loro corrispondenza con le opzioni del core
    /// </summary>
    public static class SplitCutModes
    {
        #region Costanti

        /// <summary>Blocchi di N capitoli</summary>
        public const string CHAPTERS_PER_EPISODE = "chapters-per-episode";

        /// <summary>Gruppi di capitoli indicati riga per riga</summary>
        public const string PATTERN = "pattern";

        /// <summary>Un file per capitolo</summary>
        public const string CHAPTERS_EACH = "chapters-each";

        /// <summary>Punti di taglio</summary>
        public const string SPLIT_AT = "split-at";

        /// <summary>Intervalli da estrarre</summary>
        public const string RANGES = "ranges";

        /// <summary>Inizio e fine da conservare</summary>
        public const string TRIM = "trim";

        /// <summary>Parti disegnate nell'editor</summary>
        public const string MANUAL = "manual";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Modalità nell'ordine delle card, con l'icona di ciascuna
        /// </summary>
        public static readonly (string Mode, string Icon)[] Cards =
        {
            (CHAPTERS_PER_EPISODE, "view_module"),
            (PATTERN, "dashboard_customize"),
            (CHAPTERS_EACH, "format_list_numbered"),
            (SPLIT_AT, "content_cut"),
            (RANGES, "playlist_add_check"),
            (TRIM, "crop"),
            (MANUAL, "edit_note")
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Ricava la modalità di taglio dalle opzioni Split salvate
        /// </summary>
        /// <param name="split">Opzioni Split</param>
        /// <returns>Identificativo della modalità, vuoto se nessuna</returns>
        public static string Detect(MkvSplitOptions split)
        {
            if (split == null)
                return "";
            if (!string.IsNullOrEmpty(split.Pattern))
                return PATTERN;
            if (!string.IsNullOrEmpty(split.Ranges))
                return RANGES;
            if (!string.IsNullOrEmpty(split.SplitAt))
                return SPLIT_AT;
            if (!string.IsNullOrEmpty(split.TrimStart) || !string.IsNullOrEmpty(split.TrimEnd))
                return TRIM;
            if (split.ChaptersEach)
                return CHAPTERS_EACH;
            if (split.ChaptersPerEpisode > 0)
                return CHAPTERS_PER_EPISODE;
            if (split.Manual)
                return MANUAL;
            return "";
        }

        /// <summary>
        /// Modalità del core corrispondente alla scelta del wizard
        /// </summary>
        /// <param name="cutMode">Identificativo della modalità</param>
        /// <returns>Modalità del core</returns>
        public static MkvSplitMode CoreMode(string cutMode)
        {
            return cutMode switch
            {
                PATTERN => MkvSplitMode.Pattern,
                TRIM => MkvSplitMode.Trim,
                CHAPTERS_EACH => MkvSplitMode.ChaptersEach,
                CHAPTERS_PER_EPISODE => MkvSplitMode.ChaptersPerEpisode,
                MANUAL => MkvSplitMode.Manual,
                SPLIT_AT => MkvSplitMode.SplitAt,
                _ => MkvSplitMode.Ranges
            };
        }

        /// <summary>
        /// Etichetta localizzata della modalità, la stessa del menu del wizard
        /// </summary>
        /// <param name="cutMode">Identificativo della modalità</param>
        /// <returns>Etichetta, vuota se la modalità non è valorizzata</returns>
        public static string Label(string cutMode)
        {
            return cutMode switch
            {
                CHAPTERS_PER_EPISODE => AppText.T("web.config.option.chaptersPerEpisode"),
                PATTERN => AppText.T("web.config.option.chapterPattern"),
                CHAPTERS_EACH => AppText.T("web.config.option.chaptersEach"),
                SPLIT_AT => AppText.T("web.config.option.splitAt"),
                RANGES => AppText.T("web.config.option.ranges"),
                TRIM => AppText.T("web.config.option.trim"),
                MANUAL => AppText.T("web.config.option.manual"),
                _ => ""
            };
        }

        /// <summary>
        /// Etichetta della modalità scelta nella configurazione: il core può rappresentare internamente
        /// un solo intervallo come ritaglio, ma l'utente deve rivedere la modalità che ha scelto
        /// </summary>
        /// <param name="options">Opzioni Split correnti</param>
        /// <returns>Etichetta della modalità configurata, vuota se nessuna</returns>
        public static string ConfiguredLabel(Options options)
        {
            return options == null ? "" : Label(Detect(options.Split));
        }

        #endregion
    }

    /// <summary>
    /// Riga di una regola Split: inizio e fine, oppure numero di capitoli o punto di taglio nel solo inizio
    /// </summary>
    public sealed class SplitRuleRow
    {
        /// <summary>
        /// Inizio, numero di capitoli del gruppo o punto di taglio
        /// </summary>
        public string Start { get; set; } = "";

        /// <summary>
        /// Fine, usata soltanto dagli intervalli
        /// </summary>
        public string End { get; set; } = "";
    }

    /// <summary>
    /// Conversione fra la sintassi delle regole del core e le righe del wizard, con la validazione di ogni riga
    /// </summary>
    public static class SplitRuleSyntax
    {
        #region Costanti

        /// <summary>Righe con numero di capitoli</summary>
        public const string PATTERN = "pattern";

        /// <summary>Righe con inizio e fine</summary>
        public const string RANGES = "ranges";

        /// <summary>Righe con un punto di taglio</summary>
        public const string POINTS = "points";

        /// <summary>Parola riservata del core per la fine del file</summary>
        public const string END = "END";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Converte la regola del core in righe; le voci vuote restano righe, così ogni riga scritta
        /// dall'utente viene validata anche quando è vuota
        /// </summary>
        /// <param name="mode">Tipo di righe</param>
        /// <param name="value">Regola separata da virgole</param>
        /// <returns>Righe della regola</returns>
        public static List<SplitRuleRow> Parse(string mode, string value)
        {
            List<SplitRuleRow> rows = new List<SplitRuleRow>();
            if (string.IsNullOrEmpty(value))
                return rows;

            foreach (string item in value.Split(','))
            {
                int separator = mode == RANGES ? item.IndexOf('-') : -1;
                rows.Add(new SplitRuleRow
                {
                    Start = (separator >= 0 ? item.Substring(0, separator) : item).Trim(),
                    End = mode == RANGES ? (separator >= 0 ? item.Substring(separator + 1).Trim() : END) : ""
                });
            }
            return rows;
        }

        /// <summary>
        /// Converte le righe nella regola del core
        /// </summary>
        /// <param name="mode">Tipo di righe</param>
        /// <param name="rows">Righe della regola</param>
        /// <returns>Regola separata da virgole</returns>
        public static string Format(string mode, IEnumerable<SplitRuleRow> rows)
        {
            return string.Join(",", rows.Select(row => mode == RANGES ? (row.Start ?? "").Trim() + "-" + (row.End ?? "").Trim() : (row.Start ?? "").Trim()));
        }

        /// <summary>
        /// Valida una riga
        /// </summary>
        /// <param name="mode">Tipo di righe</param>
        /// <param name="start">Inizio, numero di capitoli o punto</param>
        /// <param name="end">Fine, solo per gli intervalli</param>
        /// <returns>Messaggio localizzato, vuoto se la riga è valida</returns>
        public static string RowError(string mode, string start, string end)
        {
            if (mode == PATTERN)
            {
                bool valid = int.TryParse((start ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int chapters) && chapters > 0;
                return valid ? "" : AppText.T("web.splitWizard.validation.row.pattern");
            }

            bool startValid = TryParseTime(start, false, out double startValue, out bool startIsFrame);
            if (mode == POINTS)
                return startValid ? "" : AppText.T("web.splitWizard.validation.row.points");

            bool endValid = TryParseTime(end, true, out double endValue, out bool endIsFrame);
            bool ordered = (end ?? "").Trim() == END || startIsFrame != endIsFrame || endValue > startValue;
            return startValid && endValid && ordered ? "" : AppText.T("web.splitWizard.validation.row.ranges");
        }

        /// <summary>
        /// Interpreta un tempo nella sintassi del core: ore, minuti e secondi, secondi, f e numero di fotogramma, END
        /// </summary>
        /// <param name="text">Testo da interpretare</param>
        /// <param name="allowEnd">True se END è ammesso</param>
        /// <param name="value">Secondi oppure indice di fotogramma</param>
        /// <param name="isFrame">True se il valore è un fotogramma</param>
        /// <returns>True se il tempo è valido e non negativo</returns>
        public static bool TryParseTime(string text, bool allowEnd, out double value, out bool isFrame)
        {
            value = 0;
            isFrame = false;
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
                return false;
            if (trimmed == END)
                return allowEnd;
            try
            {
                (double parsed, bool frame) = MkvSplitSegmentService.ParseTime(trimmed, 0);
                value = parsed;
                isFrame = frame;
                return double.IsFinite(parsed) && parsed >= 0;
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                return false;
            }
        }

        #endregion
    }

    /// <summary>
    /// Bozza della configurazione Split del wizard: campi, validazione per campo e opzioni risultanti
    /// </summary>
    public sealed class SplitConfigurationDraft
    {
        #region Costanti

        /// <summary>Passo Sorgente</summary>
        public const string SECTION_SOURCE = "Source";

        /// <summary>Passo Taglio</summary>
        public const string SECTION_CUT = "Cut";

        /// <summary>Passo Nomi dei file</summary>
        public const string SECTION_NAMING = "Naming";

        /// <summary>Passo Destinazione</summary>
        public const string SECTION_DESTINATION = "Destination";

        #endregion

        #region Costruttore

        /// <summary>
        /// Precompila la bozza dalle opzioni Split correnti
        /// </summary>
        /// <param name="options">Opzioni correnti, null per una configurazione nuova</param>
        public SplitConfigurationDraft(Options options)
        {
            MkvSplitOptions split = options?.Split ?? new MkvSplitOptions();
            this.Source = options != null && !string.IsNullOrEmpty(options.SourceFolder) ? options.SourceFolder : split.SourcePath ?? "";
            this.OutputDir = split.OutputDir ?? "";
            this.Recursive = options?.Recursive ?? false;
            this.Force = split.Force;
            this.Extensions = options != null && options.FileExtensions.Count > 0 ? string.Join(",", options.FileExtensions) : "mkv";
            this.CutMode = SplitCutModes.Detect(split);
            this.Pattern = split.Pattern ?? "";
            this.Ranges = split.Ranges ?? "";
            this.SplitAt = split.SplitAt ?? "";
            this.TrimStart = split.TrimStart ?? "";
            this.TrimEnd = split.TrimEnd ?? "";
            this.ChaptersPerEpisode = split.ChaptersPerEpisode > 0 ? split.ChaptersPerEpisode : 1;
            this.Snap = split.Snap;
            this.Template = split.OutputTemplate ?? "";
            this.StartNumber = split.StartNumber;
            this.SyncSourceKind();
        }

        #endregion

        #region Proprietà

        /// <summary>File o cartella sorgente</summary>
        public string Source { get; private set; }

        /// <summary>True se la sorgente è un file esistente, calcolato quando la sorgente cambia</summary>
        public bool SourceIsFile { get; private set; }

        /// <summary>Cartella di destinazione, vuota per scrivere accanto al sorgente</summary>
        public string OutputDir { get; set; }

        /// <summary>Ricerca nelle sottocartelle</summary>
        public bool Recursive { get; set; }

        /// <summary>Sostituisce i file di uscita già presenti</summary>
        public bool Force { get; set; }

        /// <summary>Estensioni separate da virgola</summary>
        public string Extensions { get; set; }

        /// <summary>Modalità di taglio scelta</summary>
        public string CutMode { get; private set; }

        /// <summary>Capitoli per episodio, null se il campo è vuoto</summary>
        public int? ChaptersPerEpisode { get; set; }

        /// <summary>Gruppi di capitoli nella sintassi del core</summary>
        public string Pattern { get; set; }

        /// <summary>Intervalli nella sintassi del core</summary>
        public string Ranges { get; set; }

        /// <summary>Punti di taglio nella sintassi del core</summary>
        public string SplitAt { get; set; }

        /// <summary>Inizio della parte da conservare</summary>
        public string TrimStart { get; set; }

        /// <summary>Fine della parte da conservare</summary>
        public string TrimEnd { get; set; }

        /// <summary>Allineamento dei tagli</summary>
        public MkvSplitSnapMode Snap { get; set; }

        /// <summary>Schema del nome dei file</summary>
        public string Template { get; set; }

        /// <summary>Primo numero di episodio, null se il campo è vuoto</summary>
        public int? StartNumber { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Imposta la sorgente e ricalcola se è un file: il disco si legge solo qui, non a ogni render
        /// </summary>
        /// <param name="path">Percorso scelto</param>
        public void SetSource(string path)
        {
            this.Source = path ?? "";
            this.SyncSourceKind();
        }

        /// <summary>
        /// Cambia modalità e aggiorna lo schema del nome quando è ancora quello predefinito di una modalità
        /// </summary>
        /// <param name="cutMode">Nuova modalità</param>
        public void SetCutMode(string cutMode)
        {
            string current = (this.Template ?? "").Trim();
            bool untouched = current.Length == 0 || Enum.GetValues<MkvSplitMode>().Any(mode => MkvSplitSegmentService.DefaultTemplate(mode) == current);
            this.CutMode = cutMode ?? "";
            if (untouched)
                this.Template = MkvSplitSegmentService.DefaultTemplate(SplitCutModes.CoreMode(this.CutMode));
        }

        /// <summary>
        /// Tipo di righe della modalità corrente
        /// </summary>
        /// <param name="cutMode">Modalità</param>
        /// <returns>Tipo di righe, vuoto se la modalità non usa righe</returns>
        public static string RuleMode(string cutMode)
        {
            return cutMode switch
            {
                SplitCutModes.PATTERN => SplitRuleSyntax.PATTERN,
                SplitCutModes.RANGES => SplitRuleSyntax.RANGES,
                SplitCutModes.SPLIT_AT => SplitRuleSyntax.POINTS,
                _ => ""
            };
        }

        /// <summary>
        /// Campo validato della modalità a righe
        /// </summary>
        /// <param name="cutMode">Modalità</param>
        /// <returns>Nome del campo</returns>
        public static string RuleField(string cutMode)
        {
            return cutMode switch
            {
                SplitCutModes.PATTERN => "Pattern",
                SplitCutModes.RANGES => "Ranges",
                _ => "SplitAt"
            };
        }

        /// <summary>
        /// Regola della modalità a righe corrente
        /// </summary>
        /// <returns>Regola nella sintassi del core</returns>
        public string RuleValue()
        {
            return this.CutMode switch
            {
                SplitCutModes.PATTERN => this.Pattern,
                SplitCutModes.RANGES => this.Ranges,
                SplitCutModes.SPLIT_AT => this.SplitAt,
                _ => ""
            };
        }

        /// <summary>
        /// Aggiorna la regola della modalità a righe corrente
        /// </summary>
        /// <param name="value">Regola nella sintassi del core</param>
        public void SetRuleValue(string value)
        {
            if (this.CutMode == SplitCutModes.PATTERN)
                this.Pattern = value ?? "";
            else if (this.CutMode == SplitCutModes.RANGES)
                this.Ranges = value ?? "";
            else if (this.CutMode == SplitCutModes.SPLIT_AT)
                this.SplitAt = value ?? "";
        }

        /// <summary>
        /// Estensioni normalizzate, senza punto iniziale e senza duplicati
        /// </summary>
        /// <returns>Estensioni</returns>
        public List<string> ExtensionList()
        {
            return (this.Extensions ?? "").Split(',').Select(item => item.Trim().TrimStart('.')).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Valida tutti i passi; ogni errore porta il campo e il passo a cui appartiene
        /// </summary>
        /// <param name="checkDisk">True per controllare l'esistenza della sorgente e della destinazione</param>
        /// <returns>Errori trovati</returns>
        public List<PipelineInitializationIssue> Validate(bool checkDisk = true)
        {
            List<PipelineInitializationIssue> errors = new List<PipelineInitializationIssue>();
            string source = (this.Source ?? "").Trim();

            // Sorgente
            if (source.Length == 0)
                errors.Add(Issue("required", "web.splitWizard.validation.sourceRequired", "Source", SECTION_SOURCE));
            else if (checkDisk && !File.Exists(source) && !Directory.Exists(source))
                errors.Add(new PipelineInitializationIssue("notFound", AppText.F("web.splitWizard.validation.sourceNotFound", source), "Source", SECTION_SOURCE));
            if (this.ExtensionList().Count == 0)
                errors.Add(Issue("required", "web.splitWizard.validation.extensionsRequired", "FileExtensions", SECTION_SOURCE));

            // Taglio
            if (string.IsNullOrEmpty(this.CutMode))
                errors.Add(Issue("required", "web.splitWizard.validation.modeRequired", "CutMode", SECTION_CUT));
            else if (this.CutMode == SplitCutModes.CHAPTERS_PER_EPISODE)
            {
                if (!this.ChaptersPerEpisode.HasValue)
                    errors.Add(Issue("required", "web.splitWizard.validation.numberRequired", "ChaptersPerEpisode", SECTION_CUT));
                else if (this.ChaptersPerEpisode.Value < 1)
                    errors.Add(Issue("range", "web.splitWizard.validation.chaptersPerEpisodeMin", "ChaptersPerEpisode", SECTION_CUT));
            }
            else if (RuleMode(this.CutMode).Length > 0)
                this.ValidateRows(errors);
            else if (this.CutMode == SplitCutModes.TRIM)
                this.ValidateTrim(errors);

            // Nomi dei file
            foreach (string templateError in MkvSplitSegmentService.ValidateTemplate((this.Template ?? "").Trim()))
                errors.Add(new PipelineInitializationIssue("template", templateError, "Template", SECTION_NAMING));
            if (!this.StartNumber.HasValue)
                errors.Add(Issue("required", "web.splitWizard.validation.numberRequired", "StartNumber", SECTION_NAMING));
            else if (this.StartNumber.Value < 0)
                errors.Add(Issue("range", "web.splitWizard.validation.startNumberMin", "StartNumber", SECTION_NAMING));

            // Destinazione
            string output = (this.OutputDir ?? "").Trim();
            if (checkDisk && output.Length > 0 && File.Exists(output))
                errors.Add(Issue("folder", "web.splitWizard.validation.outputIsFile", "OutputDir", SECTION_DESTINATION));

            return errors;
        }

        /// <summary>
        /// Costruisce le opzioni Split; la modalità scelta è l'unica valorizzata
        /// </summary>
        /// <returns>Opzioni pronte per l'orchestratore</returns>
        public Options BuildOptions()
        {
            Options options = new Options();
            options.Mode = Options.MODE_SPLIT;
            options.SourceFolder = (this.Source ?? "").Trim();
            options.Recursive = this.Recursive;
            options.Split.SourcePath = options.SourceFolder;
            options.Split.OutputDir = (this.OutputDir ?? "").Trim();
            if (this.CutMode == SplitCutModes.PATTERN)
                options.Split.Pattern = this.Pattern.Trim();
            else if (this.CutMode == SplitCutModes.RANGES)
                options.Split.Ranges = this.Ranges.Trim();
            else if (this.CutMode == SplitCutModes.SPLIT_AT)
                options.Split.SplitAt = this.SplitAt.Trim();
            else if (this.CutMode == SplitCutModes.TRIM)
            {
                options.Split.TrimStart = this.TrimStart.Trim();
                options.Split.TrimEnd = this.TrimEnd.Trim();
            }
            else if (this.CutMode == SplitCutModes.CHAPTERS_EACH)
                options.Split.ChaptersEach = true;
            else if (this.CutMode == SplitCutModes.CHAPTERS_PER_EPISODE)
                options.Split.ChaptersPerEpisode = this.ChaptersPerEpisode ?? 0;
            else if (this.CutMode == SplitCutModes.MANUAL)
                options.Split.Manual = true;
            options.Split.OutputTemplate = (this.Template ?? "").Trim();
            options.Split.StartNumber = this.StartNumber ?? 0;
            options.Split.Snap = this.Snap;
            options.Split.Force = this.Force;
            options.FileExtensions.Clear();
            options.FileExtensions.AddRange(this.ExtensionList());
            return options;
        }

        /// <summary>
        /// Nomi prodotti dallo schema su tre parti d'esempio, senza leggere il disco
        /// </summary>
        /// <returns>Nomi d'esempio, vuoto se lo schema contiene errori</returns>
        public List<string> NamePreview()
        {
            List<string> result = new List<string>();
            string template = (this.Template ?? "").Trim();
            if (MkvSplitSegmentService.ValidateTemplate(template).Count > 0)
                return result;

            string sample = AppText.T("web.splitWizard.sampleSource") + ".mkv";
            string source = (this.Source ?? "").Trim();
            string inputFile = source.Length == 0 ? sample : this.SourceIsFile ? source : Path.Combine(source, sample);
            List<MkvSplitSegment> samples = new List<MkvSplitSegment>();
            for (int i = 0; i < 3; i++)
            {
                MkvSplitSegment segment = new MkvSplitSegment();
                segment.Num = i + 1;
                segment.Episode = i + 1;
                segment.StartTs = i * 1300.0;
                segment.EndTs = (i + 1) * 1300.0;
                segment.Chapters.Add(new ChapterMark { Name = AppText.F("web.config.preview.chapter", i + 1), StartNs = (long)(i * 1300.0 * 1000000000L) });
                samples.Add(segment);
            }

            MkvSplitOptions preview = new MkvSplitOptions();
            preview.OutputTemplate = template;
            preview.StartNumber = this.StartNumber ?? 0;
            try
            {
                new MkvSplitSegmentService().ApplyNaming(samples, preview, SplitCutModes.CoreMode(this.CutMode), inputFile);
            }
            catch (FormatException)
            {
                return result;
            }
            result.AddRange(samples.Select(segment => segment.File));
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ricalcola se la sorgente è un file
        /// </summary>
        private void SyncSourceKind()
        {
            string source = (this.Source ?? "").Trim();
            this.SourceIsFile = source.Length > 0 && File.Exists(source);
        }

        /// <summary>
        /// Valida le righe della modalità corrente
        /// </summary>
        /// <param name="errors">Errori da completare</param>
        private void ValidateRows(List<PipelineInitializationIssue> errors)
        {
            string mode = RuleMode(this.CutMode);
            string field = RuleField(this.CutMode);
            List<SplitRuleRow> rows = SplitRuleSyntax.Parse(mode, this.RuleValue());
            if (rows.Count == 0)
            {
                errors.Add(Issue("required", "web.splitWizard.validation.rowsRequired", field, SECTION_CUT));
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                string error = SplitRuleSyntax.RowError(mode, rows[i].Start, rows[i].End);
                if (error.Length > 0)
                    errors.Add(new PipelineInitializationIssue("row", AppText.F("web.splitWizard.validation.rowError", i + 1, error), field, SECTION_CUT));
            }
        }

        /// <summary>
        /// Valida i due punti del ritaglio
        /// </summary>
        /// <param name="errors">Errori da completare</param>
        private void ValidateTrim(List<PipelineInitializationIssue> errors)
        {
            string start = (this.TrimStart ?? "").Trim();
            string end = (this.TrimEnd ?? "").Trim();
            if (start.Length == 0 && end.Length == 0)
            {
                errors.Add(Issue("required", "web.splitWizard.validation.trimRequired", "TrimStart", SECTION_CUT));
                return;
            }
            bool startValid = SplitRuleSyntax.TryParseTime(start, false, out double startValue, out bool startIsFrame);
            bool endValid = SplitRuleSyntax.TryParseTime(end, true, out double endValue, out bool endIsFrame);
            if (start.Length > 0 && !startValid)
                errors.Add(Issue("time", "web.splitWizard.validation.timeInvalid", "TrimStart", SECTION_CUT));
            if (end.Length > 0 && !endValid)
                errors.Add(Issue("time", "web.splitWizard.validation.timeInvalid", "TrimEnd", SECTION_CUT));
            if (startValid && endValid && end != SplitRuleSyntax.END && startIsFrame == endIsFrame && endValue <= startValue)
                errors.Add(Issue("order", "web.splitWizard.validation.trimOrder", "TrimEnd", SECTION_CUT));
        }

        /// <summary>
        /// Crea un errore con messaggio localizzato
        /// </summary>
        /// <param name="code">Codice</param>
        /// <param name="key">Chiave del messaggio</param>
        /// <param name="field">Campo</param>
        /// <param name="section">Passo</param>
        /// <returns>Errore</returns>
        private static PipelineInitializationIssue Issue(string code, string key, string field, string section)
        {
            return new PipelineInitializationIssue(code, AppText.T(key), field, section);
        }

        #endregion
    }

    /// <summary>
    /// Decide se dopo l'analisi serve il passo di verifica dei piani
    /// </summary>
    public static class SplitReviewGate
    {
        /// <summary>
        /// True se almeno un file ha un piano non eseguibile, un errore o un avviso: la verifica
        /// resta aperta; altrimenti i piani passano direttamente all'elenco
        /// </summary>
        /// <param name="records">Record analizzati</param>
        /// <returns>True se la verifica è necessaria</returns>
        public static bool RequiresReview(IEnumerable<MkvSplitRecord> records)
        {
            foreach (MkvSplitRecord record in records ?? Enumerable.Empty<MkvSplitRecord>())
            {
                // Esclusi e manuali da definire non hanno un piano da verificare: si completano dall'elenco
                if (record.Status == MkvSplitStatus.Skipped || record.Status == MkvSplitStatus.Undefined)
                    continue;
                if (record.Status == MkvSplitStatus.PlanInvalid || record.Status == MkvSplitStatus.Error)
                    return true;
                if (record.MontageProjection != null && (!record.MontageProjection.IsValid || record.MontageProjection.Diagnostics.Count > 0))
                    return true;
                if (record.Plan != null && (!record.Plan.IsValid || record.Plan.Warnings.Count > 0))
                    return true;
                if (record.Plan == null && record.MontageProjection == null)
                    return true;
            }
            return false;
        }
    }
}
