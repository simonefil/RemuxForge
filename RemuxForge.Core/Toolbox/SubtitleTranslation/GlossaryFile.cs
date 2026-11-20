using ClosedXML.Excel;
using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// File .xlsx di «Esporta glossario» e «Importa glossario»: un foglio per parte con l'intestazione in riga 1 e una voce per
    /// riga, più il foglio Serie con formato, versione, lingue e testi
    /// </summary>
    public static class GlossaryFile
    {
        #region Costanti

        /// <summary>
        /// Valore di Formato nel foglio Serie
        /// </summary>
        public const string FORMAT = "remuxforge.glossary";

        /// <summary>
        /// Versione del formato, scritta e letta
        /// </summary>
        public const int VERSION = 2;

        /// <summary>
        /// File non leggibile come glossario
        /// </summary>
        public const string FILE_INVALID = "GLOSSARY_FILE_INVALID";

        /// <summary>
        /// Lingue del file diverse da quelle del progetto
        /// </summary>
        public const string LANGUAGE_MISMATCH = "GLOSSARY_LANGUAGE_MISMATCH";

        /// <summary>
        /// Foglio dei termini
        /// </summary>
        private const string TERMS_SHEET = "Termini";

        /// <summary>
        /// Foglio dei personaggi
        /// </summary>
        private const string CHARACTERS_SHEET = "Personaggi";

        /// <summary>
        /// Foglio delle forme di cortesia
        /// </summary>
        private const string ADDRESSING_SHEET = "Registro";

        /// <summary>
        /// Foglio della serie
        /// </summary>
        private const string SERIES_SHEET = "Serie";

        /// <summary>
        /// Valore di Forma fissa e Maiuscole esatte per vero
        /// </summary>
        private const string YES = "sì";

        /// <summary>
        /// Valore di Forma fissa e Maiuscole esatte per falso
        /// </summary>
        private const string NO = "no";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Intestazioni dei fogli delle voci
        /// </summary>
        private static readonly Dictionary<string, string[]> s_headers = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { TERMS_SHEET, new string[] { "Originale", "Resa", "Tipo", "Forma fissa", "Maiuscole esatte", "Nota" } },
            { CHARACTERS_SHEET, new string[] { "Nome", "Genere", "Ruolo", "Rapporti" } },
            { ADDRESSING_SHEET, new string[] { "Chi parla", "A chi", "Forma", "Nota" } },
            { SERIES_SHEET, new string[] { "Campo", "Valore" } }
        };

        /// <summary>
        /// Righe del foglio Serie, nell'ordine di scrittura
        /// </summary>
        private static readonly string[] s_seriesFields = new string[] { "Formato", "Versione", "Lingua d'origine", "Lingua d'arrivo", "Serie", "Tono", "Regole di registro" };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive il glossario nel file .xlsx
        /// </summary>
        /// <param name="glossary">Glossario, bozza o approvato</param>
        /// <param name="path">File da scrivere</param>
        public static void Export(Glossary glossary, string path)
        {
            using (XLWorkbook workbook = new XLWorkbook())
            {
                IXLWorksheet terms = AddSheet(workbook, TERMS_SHEET);
                for (int i = 0; i < glossary.Terms.Count; i++)
                {
                    GlossaryTerm term = glossary.Terms[i];
                    WriteRow(terms, i + 2, term.Source, term.Target, term.Kind.ToString(), term.Invariable ? YES : NO, term.ExactCase == true ? YES : NO, term.Note);
                }

                IXLWorksheet characters = AddSheet(workbook, CHARACTERS_SHEET);
                for (int i = 0; i < glossary.Characters.Count; i++)
                {
                    GlossaryCharacter character = glossary.Characters[i];
                    WriteRow(characters, i + 2, character.Name, character.Gender.ToString(), character.Role, character.Relationships);
                }

                IXLWorksheet addressing = AddSheet(workbook, ADDRESSING_SHEET);
                for (int i = 0; i < glossary.Addressing.Count; i++)
                {
                    GlossaryAddressing pair = glossary.Addressing[i];
                    WriteRow(addressing, i + 2, pair.Speaker, pair.Addressee, pair.Form, pair.Note);
                }

                IXLWorksheet series = AddSheet(workbook, SERIES_SHEET);
                string[] values = new string[] { FORMAT, VERSION.ToString(CultureInfo.InvariantCulture), glossary.SourceLanguage, glossary.TargetLanguage, glossary.Series, glossary.Tone, glossary.RegisterRules };
                for (int i = 0; i < s_seriesFields.Length; i++)
                    WriteRow(series, i + 2, s_seriesFields[i], values[i]);

                workbook.SaveAs(path);
            }
        }

        /// <summary>
        /// Legge un glossario dal file .xlsx: il primo problema ferma la lettura
        /// </summary>
        /// <param name="path">File da leggere</param>
        /// <param name="sourceLanguage">Lingua d'origine del progetto</param>
        /// <param name="targetLanguage">Lingua d'arrivo del progetto</param>
        /// <returns>Voci con origine Import e testi del file, oppure il problema</returns>
        public static GlossaryImportResult Import(string path, string sourceLanguage, string targetLanguage)
        {
            GlossaryImportResult result = new GlossaryImportResult();
            string projectSource = LanguageValidator.NormalizeToIso6392(sourceLanguage);
            string projectTarget = LanguageValidator.NormalizeToIso6392(targetLanguage);
            string[] forms = TranslationRequestBuilder.GetAddressForms(targetLanguage);
            XLWorkbook workbook;

            try
            {
                workbook = new XLWorkbook(path);
            }
            catch (Exception ex)
            {
                // ClosedXML segnala con eccezioni di tipi diversi un file che non è un .xlsx o è rovinato: per l'utente il motivo è
                // sempre lo stesso
                if (ex is OutOfMemoryException)
                    throw;
                return result.Fail(FILE_INVALID, "", ex.Message);
            }

            using (workbook)
            {
                Dictionary<string, IXLWorksheet> sheets = new Dictionary<string, IXLWorksheet>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string[]> header in s_headers)
                {
                    IXLWorksheet sheet;
                    if (!workbook.Worksheets.TryGetWorksheet(header.Key, out sheet))
                        return result.Fail(FILE_INVALID, header.Key, "");
                    for (int column = 0; column < header.Value.Length; column++)
                    {
                        if (ReadCell(sheet, 1, column + 1) != header.Value[column])
                            return result.Fail(FILE_INVALID, CellPath(sheet, 1, column + 1), "");
                    }
                    sheets[header.Key] = sheet;
                }

                // Foglio Serie: formato, versione e lingue prima delle voci
                Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
                IXLWorksheet series = sheets[SERIES_SHEET];
                for (int row = 2; row <= (series.LastRowUsed()?.RowNumber() ?? 1); row++)
                {
                    string field = ReadCell(series, row, 1);
                    if (!string.IsNullOrEmpty(field))
                        fields[field] = ReadCell(series, row, 2);
                }

                string format;
                string versionText;
                int version;
                if (!fields.TryGetValue("Formato", out format) || format != FORMAT || !fields.TryGetValue("Versione", out versionText) ||
                    !int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) || version != VERSION)
                    return result.Fail(FILE_INVALID, SERIES_SHEET, "");

                string fileSource;
                string fileTarget;
                fields.TryGetValue("Lingua d'origine", out fileSource);
                fields.TryGetValue("Lingua d'arrivo", out fileTarget);
                fileSource = LanguageValidator.NormalizeToIso6392(fileSource ?? "");
                fileTarget = LanguageValidator.NormalizeToIso6392(fileTarget ?? "");
                if (fileSource != projectSource || fileTarget != projectTarget)
                {
                    result.SourceLanguage = fileSource;
                    result.TargetLanguage = fileTarget;
                    return result.Fail(LANGUAGE_MISMATCH, SERIES_SHEET, "");
                }

                string seriesText;
                string tone;
                string registerRules;
                Glossary glossary = new Glossary();
                glossary.SourceLanguage = projectSource;
                glossary.TargetLanguage = projectTarget;
                glossary.Series = fields.TryGetValue("Serie", out seriesText) ? seriesText : "";
                glossary.Tone = fields.TryGetValue("Tono", out tone) ? tone : "";
                glossary.RegisterRules = fields.TryGetValue("Regole di registro", out registerRules) ? registerRules : "";

                GlossaryBuilder builder = new GlossaryBuilder(sourceLanguage, targetLanguage);
                IXLWorksheet terms = sheets[TERMS_SHEET];
                Dictionary<string, string> seenTerms = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int row = 2; row <= (terms.LastRowUsed()?.RowNumber() ?? 1); row++)
                {
                    if (IsEmptyRow(terms, row, 6))
                        continue;

                    GlossaryTerm term = new GlossaryTerm();
                    term.Source = ReadCell(terms, row, 1);
                    term.Target = ReadCell(terms, row, 2);
                    term.Note = ReadCell(terms, row, 6);
                    term.Origin = GlossaryOrigin.Import;
                    if (string.IsNullOrEmpty(term.Source))
                        return result.Fail("GLOSSARY_EMPTY", CellPath(terms, row, 1), "");
                    if (string.IsNullOrEmpty(term.Target))
                        return result.Fail("GLOSSARY_EMPTY", CellPath(terms, row, 2), "");

                    string kindText = ReadCell(terms, row, 3);
                    if (string.IsNullOrEmpty(kindText))
                        return result.Fail("GLOSSARY_FIELD_MISSING", CellPath(terms, row, 3), "");
                    if (Array.IndexOf(Enum.GetNames<GlossaryTermKind>(), kindText) < 0)
                        return result.Fail("GLOSSARY_ENUM", CellPath(terms, row, 3), kindText);
                    term.Kind = Enum.Parse<GlossaryTermKind>(kindText);

                    string invariable = ReadCell(terms, row, 4);
                    if (string.IsNullOrEmpty(invariable))
                        return result.Fail("GLOSSARY_FIELD_MISSING", CellPath(terms, row, 4), "");
                    if (invariable != YES && invariable != NO)
                        return result.Fail("GLOSSARY_ENUM", CellPath(terms, row, 4), invariable);
                    term.Invariable = invariable == YES;

                    string exactCase = ReadCell(terms, row, 5);
                    if (string.IsNullOrEmpty(exactCase))
                        return result.Fail("GLOSSARY_FIELD_MISSING", CellPath(terms, row, 5), "");
                    if (exactCase != YES && exactCase != NO)
                        return result.Fail("GLOSSARY_ENUM", CellPath(terms, row, 5), exactCase);
                    term.ExactCase = exactCase == YES;

                    string key = builder.TermKey(term.Source);
                    if (seenTerms.ContainsKey(key))
                        return result.Fail("GLOSSARY_DUPLICATE", CellPath(terms, row, 1), seenTerms[key]);
                    seenTerms[key] = CellPath(terms, row, 1);
                    glossary.Terms.Add(term);
                }

                IXLWorksheet characters = sheets[CHARACTERS_SHEET];
                Dictionary<string, string> seenCharacters = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int row = 2; row <= (characters.LastRowUsed()?.RowNumber() ?? 1); row++)
                {
                    if (IsEmptyRow(characters, row, 4))
                        continue;

                    GlossaryCharacter character = new GlossaryCharacter();
                    character.Name = ReadCell(characters, row, 1);
                    character.Role = ReadCell(characters, row, 3);
                    character.Relationships = ReadCell(characters, row, 4);
                    character.Origin = GlossaryOrigin.Import;
                    if (string.IsNullOrEmpty(character.Name))
                        return result.Fail("GLOSSARY_EMPTY", CellPath(characters, row, 1), "");

                    string genderText = ReadCell(characters, row, 2);
                    if (string.IsNullOrEmpty(genderText))
                        return result.Fail("GLOSSARY_FIELD_MISSING", CellPath(characters, row, 2), "");
                    if (Array.IndexOf(Enum.GetNames<CharacterGender>(), genderText) < 0)
                        return result.Fail("GLOSSARY_ENUM", CellPath(characters, row, 2), genderText);
                    character.Gender = Enum.Parse<CharacterGender>(genderText);

                    string key = GlossaryBuilder.CharacterKey(character.Name);
                    if (seenCharacters.ContainsKey(key))
                        return result.Fail("GLOSSARY_DUPLICATE", CellPath(characters, row, 1), seenCharacters[key]);
                    seenCharacters[key] = CellPath(characters, row, 1);
                    glossary.Characters.Add(character);
                }

                // Le lingue senza forme di cortesia non hanno forme da leggere
                IXLWorksheet addressing = sheets[ADDRESSING_SHEET];
                Dictionary<string, string> seenAddressing = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int row = 2; forms != null && row <= (addressing.LastRowUsed()?.RowNumber() ?? 1); row++)
                {
                    if (IsEmptyRow(addressing, row, 4))
                        continue;

                    GlossaryAddressing pair = new GlossaryAddressing();
                    pair.Speaker = ReadCell(addressing, row, 1);
                    pair.Addressee = ReadCell(addressing, row, 2);
                    pair.Form = ReadCell(addressing, row, 3);
                    pair.Note = ReadCell(addressing, row, 4);
                    pair.Origin = GlossaryOrigin.Import;
                    if (string.IsNullOrEmpty(pair.Speaker))
                        return result.Fail("GLOSSARY_EMPTY", CellPath(addressing, row, 1), "");
                    if (string.IsNullOrEmpty(pair.Addressee))
                        return result.Fail("GLOSSARY_EMPTY", CellPath(addressing, row, 2), "");
                    if (string.IsNullOrEmpty(pair.Form))
                        return result.Fail("GLOSSARY_FIELD_MISSING", CellPath(addressing, row, 3), "");
                    if (Array.IndexOf(forms, pair.Form) < 0)
                        return result.Fail("GLOSSARY_ENUM", CellPath(addressing, row, 3), pair.Form);

                    string key = GlossaryBuilder.AddressingKey(pair.Speaker, pair.Addressee);
                    if (seenAddressing.ContainsKey(key))
                        return result.Fail("GLOSSARY_DUPLICATE", CellPath(addressing, row, 1), seenAddressing[key]);
                    seenAddressing[key] = CellPath(addressing, row, 1);
                    glossary.Addressing.Add(pair);
                }

                result.Glossary = glossary;
                return result;
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiunge un foglio con l'intestazione in grassetto
        /// </summary>
        /// <param name="workbook">Cartella di lavoro</param>
        /// <param name="name">Nome del foglio</param>
        /// <returns>Foglio</returns>
        private static IXLWorksheet AddSheet(XLWorkbook workbook, string name)
        {
            IXLWorksheet sheet = workbook.Worksheets.Add(name);
            string[] header = s_headers[name];
            WriteRow(sheet, 1, header);
            sheet.Row(1).Style.Font.Bold = true;
            for (int column = 1; column <= header.Length; column++)
                sheet.Column(column).Width = 28;
            return sheet;
        }

        /// <summary>
        /// Scrive una riga di testi
        /// </summary>
        /// <param name="sheet">Foglio</param>
        /// <param name="row">Riga, da 1</param>
        /// <param name="values">Valori dalla colonna A</param>
        private static void WriteRow(IXLWorksheet sheet, int row, params string[] values)
        {
            for (int column = 0; column < values.Length; column++)
                sheet.Cell(row, column + 1).Value = values[column] ?? "";
        }

        /// <summary>
        /// Legge il testo di una cella, senza spazi agli estremi
        /// </summary>
        /// <param name="sheet">Foglio</param>
        /// <param name="row">Riga, da 1</param>
        /// <param name="column">Colonna, da 1</param>
        /// <returns>Testo della cella</returns>
        private static string ReadCell(IXLWorksheet sheet, int row, int column)
        {
            return sheet.Cell(row, column).GetFormattedString(CultureInfo.InvariantCulture).Trim();
        }

        /// <summary>
        /// Indica se una riga di voci è vuota
        /// </summary>
        /// <param name="sheet">Foglio</param>
        /// <param name="row">Riga, da 1</param>
        /// <param name="columns">Colonne del foglio</param>
        /// <returns>True se nessuna colonna ha testo</returns>
        private static bool IsEmptyRow(IXLWorksheet sheet, int row, int columns)
        {
            for (int column = 1; column <= columns; column++)
            {
                if (!string.IsNullOrEmpty(ReadCell(sheet, row, column)))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Percorso di una cella nella forma Foglio!B5
        /// </summary>
        /// <param name="sheet">Foglio</param>
        /// <param name="row">Riga, da 1</param>
        /// <param name="column">Colonna, da 1</param>
        /// <returns>Percorso della cella</returns>
        private static string CellPath(IXLWorksheet sheet, int row, int column)
        {
            return sheet.Name + "!" + sheet.Cell(row, column).Address.ToString();
        }

        #endregion
    }

    /// <summary>
    /// Esito della lettura di un glossario .xlsx
    /// </summary>
    public class GlossaryImportResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryImportResult()
        {
            this.Glossary = null;
            this.Code = "";
            this.Location = "";
            this.Detail = "";
            this.SourceLanguage = "";
            this.TargetLanguage = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Registra il problema che ferma la lettura
        /// </summary>
        /// <param name="code">Codice del problema</param>
        /// <param name="location">Foglio o cella</param>
        /// <param name="detail">Valore letto, cella dell'altra voce o messaggio del file illeggibile</param>
        /// <returns>Questo esito</returns>
        public GlossaryImportResult Fail(string code, string location, string detail)
        {
            this.Code = code;
            this.Location = location;
            this.Detail = detail;
            return this;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Glossario letto, null se la lettura si è fermata
        /// </summary>
        public Glossary Glossary { get; set; }

        /// <summary>
        /// Codice del problema, vuoto se la lettura è riuscita
        /// </summary>
        public string Code { get; private set; }

        /// <summary>
        /// Foglio o cella del problema
        /// </summary>
        public string Location { get; private set; }

        /// <summary>
        /// Dettaglio del problema
        /// </summary>
        public string Detail { get; private set; }

        /// <summary>
        /// Lingua d'origine del file, per GLOSSARY_LANGUAGE_MISMATCH
        /// </summary>
        public string SourceLanguage { get; set; }

        /// <summary>
        /// Lingua d'arrivo del file, per GLOSSARY_LANGUAGE_MISMATCH
        /// </summary>
        public string TargetLanguage { get; set; }

        #endregion
    }
}
