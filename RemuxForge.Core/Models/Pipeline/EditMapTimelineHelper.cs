using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Tipo di segmento compilato della timeline EditMap
    /// </summary>
    public enum EditMapTimelineSegmentKind
    {
        /// <summary>Intervallo lineare con frame Source e Language associati</summary>
        Mapped,
        /// <summary>Intervallo Source privo di frame Language</summary>
        InsertedGap,
        /// <summary>Intervallo Language rimosso su una giunzione Source a durata zero</summary>
        CutJump
    }

    /// <summary>
    /// Esito della proiezione di un timestamp attraverso la EditMap
    /// </summary>
    public enum EditMapMappingKind
    {
        /// <summary>Timestamp associato correttamente</summary>
        Mapped,
        /// <summary>Timestamp Source interno a un gap senza frame Language</summary>
        NoLanguageFrame,
        /// <summary>Timestamp Language interno a un cut senza frame Source</summary>
        NoSourceFrame,
        /// <summary>Timestamp esterno al dominio disponibile</summary>
        OutsideTimeline
    }

    /// <summary>
    /// Codici stabili degli errori e warning strutturali EditMap
    /// </summary>
    public static class EditMapValidationCode
    {
        public const string INVALID_STRETCH = "invalid_stretch";
        public const string UNKNOWN_OPERATION = "unknown_operation";
        public const string NEGATIVE_TIMESTAMP = "negative_timestamp";
        public const string INVALID_DURATION = "invalid_duration";
        public const string INVALID_GAIN = "invalid_gain";
        public const string LANGUAGE_TIMESTAMP_OUT_OF_RANGE = "language_timestamp_out_of_range";
        public const string CUT_OUT_OF_RANGE = "cut_out_of_range";
        public const string DUPLICATE_BOUNDARY = "duplicate_boundary";
        public const string CUT_OVERLAP = "cut_overlap";
        public const string OPERATION_INSIDE_CUT = "operation_inside_cut";
        public const string SOURCE_BOUNDARY_OUT_OF_RANGE = "source_boundary_out_of_range";
        public const string SOURCE_BOUNDARY_NOT_MONOTONIC = "source_boundary_not_monotonic";
        public const string SOURCE_DURATION_MISMATCH = "source_duration_mismatch";
        public const string SOURCE_BOUNDARY_NORMALIZED = "source_boundary_normalized";
        public const string SCOPE_NORMALIZED = "scope_normalized";
    }

    /// <summary>
    /// Singola segnalazione prodotta dalla validazione EditMap
    /// </summary>
    public class EditMapValidationIssue
    {
        /// <summary>Codice stabile della segnalazione</summary>
        public string Code { get; set; }

        /// <summary>Indice dell'operazione interessata, -1 per errori globali</summary>
        public int OperationIndex { get; set; }

        /// <summary>Valore temporale collegato alla segnalazione</summary>
        public double ValueMs { get; set; }

        /// <summary>
        /// Costruttore
        /// </summary>
        public EditMapValidationIssue()
        {
            this.Code = "";
            this.OperationIndex = -1;
            this.ValueMs = 0.0;
        }
    }

    /// <summary>
    /// Esito completo della validazione strutturale EditMap
    /// </summary>
    public class EditMapValidationResult
    {
        /// <summary>Errori che impediscono l'applicazione della mappa</summary>
        public List<EditMapValidationIssue> Errors { get; set; }

        /// <summary>Segnalazioni informative che non impediscono l'applicazione</summary>
        public List<EditMapValidationIssue> Warnings { get; set; }

        /// <summary>True quando non esistono errori strutturali</summary>
        public bool IsValid { get { return this.Errors.Count == 0; } }

        /// <summary>
        /// Costruttore
        /// </summary>
        public EditMapValidationResult()
        {
            this.Errors = new List<EditMapValidationIssue>();
            this.Warnings = new List<EditMapValidationIssue>();
        }
    }

    /// <summary>
    /// Segmento temporale compilato da una EditMap normalizzata
    /// </summary>
    public class EditMapTimelineSegment
    {
        /// <summary>Tipo del segmento</summary>
        public EditMapTimelineSegmentKind Kind { get; set; }

        /// <summary>Inizio nella timeline Source/common</summary>
        public double SourceStartMs { get; set; }

        /// <summary>Fine nella timeline Source/common</summary>
        public double SourceEndMs { get; set; }

        /// <summary>Inizio nella timeline Language originale</summary>
        public double LanguageStartMs { get; set; }

        /// <summary>Fine nella timeline Language originale</summary>
        public double LanguageEndMs { get; set; }
    }

    /// <summary>
    /// Risultato di una conversione Source/Language
    /// </summary>
    public class EditMapMappingResult
    {
        /// <summary>Tipo di mapping risolto</summary>
        public EditMapMappingKind Kind { get; set; }

        /// <summary>Timestamp Source richiesto o risolto</summary>
        public double SourceTimestampMs { get; set; }

        /// <summary>Timestamp Language richiesto o risolto</summary>
        public double LanguageTimestampMs { get; set; }

        /// <summary>True quando entrambi i timestamp sono associati</summary>
        public bool IsMapped { get { return this.Kind == EditMapMappingKind.Mapped; } }
    }

    /// <summary>
    /// Proiezione compilata e bidirezionale di una EditMap
    /// </summary>
    public class EditMapProjection
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        internal EditMapProjection()
        {
            this.Map = new EditMap();
            this.Segments = new List<EditMapTimelineSegment>();
            this.Validation = new EditMapValidationResult();
            this.StretchRatio = 1.0;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Mappa un timestamp Source/common nella timeline Language originale
        /// </summary>
        /// <param name="sourceTimestampMs">Timestamp Source/common</param>
        /// <returns>Risultato esplicito, compresi gap e punti fuori timeline</returns>
        public EditMapMappingResult MapSourceToLanguage(double sourceTimestampMs)
        {
            EditMapMappingResult result = new EditMapMappingResult();
            result.Kind = EditMapMappingKind.OutsideTimeline;
            result.SourceTimestampMs = sourceTimestampMs;
            result.LanguageTimestampMs = double.NaN;

            if (!double.IsFinite(sourceTimestampMs) || sourceTimestampMs < 0.0 || (this.SourceDurationMs > 0.0 && sourceTimestampMs > this.SourceDurationMs))
                return result;

            for (int i = 0; i < this.Segments.Count; i++)
            {
                EditMapTimelineSegment segment = this.Segments[i];
                if (segment.Kind == EditMapTimelineSegmentKind.InsertedGap && ContainsSource(segment, sourceTimestampMs, false))
                {
                    result.Kind = EditMapMappingKind.NoLanguageFrame;
                    return result;
                }

                if (segment.Kind != EditMapTimelineSegmentKind.Mapped || !ContainsSource(segment, sourceTimestampMs, i == this.Segments.Count - 1))
                    continue;

                result.Kind = EditMapMappingKind.Mapped;
                result.LanguageTimestampMs = segment.LanguageStartMs + (sourceTimestampMs - segment.SourceStartMs) / this.StretchRatio;
                return result;
            }

            return result;
        }

        /// <summary>
        /// Mappa un timestamp Language originale nella timeline Source/common
        /// </summary>
        /// <param name="languageTimestampMs">Timestamp Language originale</param>
        /// <returns>Risultato esplicito, compresi cut e punti fuori timeline</returns>
        public EditMapMappingResult MapLanguageToSource(double languageTimestampMs)
        {
            EditMapMappingResult result = new EditMapMappingResult();
            result.Kind = EditMapMappingKind.OutsideTimeline;
            result.SourceTimestampMs = double.NaN;
            result.LanguageTimestampMs = languageTimestampMs;

            if (!double.IsFinite(languageTimestampMs) || languageTimestampMs < 0.0 || (this.LanguageDurationMs > 0.0 && languageTimestampMs > this.LanguageDurationMs))
                return result;

            for (int i = 0; i < this.Segments.Count; i++)
            {
                EditMapTimelineSegment segment = this.Segments[i];
                if (segment.Kind == EditMapTimelineSegmentKind.CutJump && ContainsLanguage(segment, languageTimestampMs, false))
                {
                    result.Kind = EditMapMappingKind.NoSourceFrame;
                    result.SourceTimestampMs = segment.SourceStartMs;
                    return result;
                }

                if (segment.Kind != EditMapTimelineSegmentKind.Mapped || !ContainsLanguage(segment, languageTimestampMs, i == this.Segments.Count - 1))
                    continue;

                result.SourceTimestampMs = segment.SourceStartMs + (languageTimestampMs - segment.LanguageStartMs) * this.StretchRatio;
                if (result.SourceTimestampMs < 0.0 || (this.SourceDurationMs > 0.0 && result.SourceTimestampMs > this.SourceDurationMs))
                    return result;

                result.Kind = EditMapMappingKind.Mapped;
                return result;
            }

            return result;
        }

        #endregion

        #region Proprietà

        /// <summary>Copia normalizzata della EditMap</summary>
        public EditMap Map { get; internal set; }

        /// <summary>Segmenti ordinati della proiezione</summary>
        public List<EditMapTimelineSegment> Segments { get; internal set; }

        /// <summary>Esito della validazione strutturale</summary>
        public EditMapValidationResult Validation { get; internal set; }

        /// <summary>Rapporto Language → Source usato dalla proiezione</summary>
        public double StretchRatio { get; internal set; }

        /// <summary>Durata Source indicizzata</summary>
        public double SourceDurationMs { get; internal set; }

        /// <summary>Durata Language indicizzata</summary>
        public double LanguageDurationMs { get; internal set; }

        /// <summary>Tolleranza Source usata per riconoscere un'operazione di coda</summary>
        internal double SourceTailToleranceMs { get; set; }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Verifica appartenenza di un timestamp a un segmento Source semiaperto
        /// </summary>
        private static bool ContainsSource(EditMapTimelineSegment segment, double timestampMs, bool includeEnd)
        {
            return timestampMs >= segment.SourceStartMs && (timestampMs < segment.SourceEndMs || (includeEnd && timestampMs <= segment.SourceEndMs));
        }

        /// <summary>
        /// Verifica appartenenza di un timestamp a un segmento Language semiaperto
        /// </summary>
        private static bool ContainsLanguage(EditMapTimelineSegment segment, double timestampMs, bool includeEnd)
        {
            return timestampMs >= segment.LanguageStartMs && (timestampMs < segment.LanguageEndMs || (includeEnd && timestampMs <= segment.LanguageEndMs));
        }

        #endregion
    }

    /// <summary>
    /// Autorità unica per conversioni, normalizzazione e validazione temporale della EditMap
    /// </summary>
    public static class EditMapTimelineHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Compila una EditMap in segmenti bidirezionali e ne normalizza i campi derivati
        /// </summary>
        /// <param name="editMap">Mappa sorgente</param>
        /// <param name="sourceDurationMs">Durata Source indicizzata</param>
        /// <param name="languageDurationMs">Durata Language indicizzata</param>
        /// <param name="sourceTailToleranceMs">Durata dell'ultimo frame Source, usata come tolleranza di coda</param>
        /// <returns>Proiezione compilata con validazione e copia normalizzata</returns>
        public static EditMapProjection BuildProjection(EditMap editMap, double sourceDurationMs, double languageDurationMs, double sourceTailToleranceMs = 0.0)
        {
            EditMapProjection result = new EditMapProjection();
            EditMap normalizedMap = Clone(editMap);
            result.Map = normalizedMap;
            result.SourceDurationMs = Math.Max(0.0, sourceDurationMs);
            result.LanguageDurationMs = Math.Max(0.0, languageDurationMs);
            result.SourceTailToleranceMs = double.IsFinite(sourceTailToleranceMs) ? Math.Max(0.0, sourceTailToleranceMs) : 0.0;

            if (!TryParseStretchFactor(normalizedMap.StretchFactor, out double stretchRatio, out string normalizedStretch))
            {
                if (!string.IsNullOrEmpty(normalizedMap.StretchFactor))
                    AddIssue(result.Validation.Errors, EditMapValidationCode.INVALID_STRETCH, -1, 0.0);
                stretchRatio = 1.0;
                normalizedStretch = "";
            }
            result.StretchRatio = stretchRatio;
            normalizedMap.StretchFactor = normalizedStretch;

            normalizedMap.Operations.Sort(CompareOperations);
            ValidateAndNormalizeOperations(result);
            BuildSegments(result);
            ValidateCompiledTimeline(result);
            return result;
        }

        /// <summary>
        /// Crea una copia profonda di una EditMap e delle sue operazioni
        /// </summary>
        /// <param name="source">Mappa sorgente</param>
        /// <returns>Copia indipendente, mai null</returns>
        public static EditMap Clone(EditMap source)
        {
            EditMap result = new EditMap();
            if (source == null)
                return result;

            result.InitialDelayMs = source.InitialDelayMs;
            result.StretchFactor = source.StretchFactor ?? "";
            result.AnalysisTimeMs = source.AnalysisTimeMs;
            if (source.Operations == null)
                return result;

            for (int i = 0; i < source.Operations.Count; i++)
            {
                EditOperation operation = source.Operations[i];
                if (operation == null)
                    continue;
                result.Operations.Add(new EditOperation
                {
                    Type = operation.Type ?? "",
                    LangTimestampMs = operation.LangTimestampMs,
                    DurationMs = operation.DurationMs,
                    GainDb = operation.GainDb,
                    SourceTimestampMs = operation.SourceTimestampMs,
                    VisualSourceTimestampMs = operation.VisualSourceTimestampMs,
                    Scope = operation.Scope ?? EditOperation.SCOPE_BODY
                });
            }

            return result;
        }

        /// <summary>
        /// Converte un fattore decimale o frazionario in rapporto positivo normalizzato
        /// </summary>
        /// <param name="value">Fattore serializzato</param>
        /// <param name="ratio">Rapporto numerico</param>
        /// <param name="normalized">Forma normalizzata</param>
        /// <returns>True se il fattore è valido</returns>
        public static bool TryParseStretchFactor(string value, out double ratio, out string normalized)
        {
            ratio = 0.0;
            normalized = "";
            string text = value != null ? value.Trim() : "";
            if (string.IsNullOrEmpty(text))
            {
                ratio = 1.0;
                return true;
            }

            int separatorIndex = text.IndexOf('/');
            if (separatorIndex >= 0)
            {
                if (separatorIndex == 0 || separatorIndex == text.Length - 1 || text.IndexOf('/', separatorIndex + 1) >= 0)
                    return false;
                if (!double.TryParse(text.Substring(0, separatorIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) ||
                    !double.TryParse(text.Substring(separatorIndex + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) ||
                    numerator <= 0.0 || denominator <= 0.0 || !double.IsFinite(numerator) || !double.IsFinite(denominator))
                    return false;
                ratio = numerator / denominator;
                if (!double.IsFinite(ratio) || ratio <= 0.0)
                    return false;
                normalized = text;
                return true;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) || ratio <= 0.0 || !double.IsFinite(ratio))
                return false;
            normalized = ratio.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Converte una durata della timeline Language originale nella durata renderizzata dopo lo stretch
        /// </summary>
        public static int LanguageDurationToRenderedDurationMs(int languageDurationMs, double stretchRatio)
        {
            if (languageDurationMs <= 0)
                return 0;
            if (stretchRatio <= 0.0)
                return languageDurationMs;
            return Math.Max(1, (int)Math.Round(languageDurationMs * stretchRatio, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Restituisce il delta durata firmato prodotto da una operazione EditMap sulla timeline Source
        /// </summary>
        public static int GetRenderedOperationDeltaMs(EditOperation operation, double stretchRatio)
        {
            if (operation == null)
                return 0;
            int durationMs = LanguageDurationToRenderedDurationMs(operation.DurationMs, stretchRatio);
            if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                return durationMs;
            if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                return -durationMs;
            return 0;
        }

        /// <summary>
        /// Calcola il delta renderizzato prodotto dalle operazioni precedenti a un indice
        /// </summary>
        public static int GetRenderedDeltaBeforeMs(List<EditOperation> operations, int operationIndex, double stretchRatio)
        {
            int result = 0;
            if (operations == null || operationIndex <= 0)
                return result;
            int limit = Math.Min(operationIndex, operations.Count);
            for (int i = 0; i < limit; i++)
                result += GetRenderedOperationDeltaMs(operations[i], stretchRatio);
            return result;
        }

        /// <summary>
        /// Mappa un timestamp Language originale nella timeline renderizzata usando le operazioni precedenti
        /// </summary>
        public static int LanguageTimestampToRenderedTimestampMs(int languageTimestampMs, List<EditOperation> operations, int operationIndex, double stretchRatio)
        {
            int renderedTimestampMs = languageTimestampMs <= 0 ? 0 : LanguageDurationToRenderedDurationMs(languageTimestampMs, stretchRatio);
            return renderedTimestampMs + GetRenderedDeltaBeforeMs(operations, operationIndex, stretchRatio);
        }

        /// <summary>
        /// Mappa un timestamp renderizzato nella timeline Language originale usando le operazioni precedenti
        /// </summary>
        public static int RenderedTimestampToLanguageTimestampMs(int renderedTimestampMs, List<EditOperation> operations, int operationIndex, double stretchRatio)
        {
            int adjustedMs = renderedTimestampMs - GetRenderedDeltaBeforeMs(operations, operationIndex, stretchRatio);
            if (adjustedMs <= 0)
                return 0;
            if (stretchRatio <= 0.0)
                return adjustedMs;
            return Math.Max(0, (int)Math.Round(adjustedMs / stretchRatio, MidpointRounding.AwayFromZero));
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valida le operazioni e normalizza timestamp Source e scope derivati
        /// </summary>
        private static void ValidateAndNormalizeOperations(EditMapProjection projection)
        {
            List<EditOperation> operations = projection.Map.Operations;
            double previousCutEndMs = -1.0;
            double previousSourceBoundaryMs = double.NegativeInfinity;
            double cumulativeDeltaMs = 0.0;

            for (int i = 0; i < operations.Count; i++)
            {
                EditOperation operation = operations[i];
                bool isCut = string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal);
                bool isInsert = string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal);
                if (!isCut && !isInsert)
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.UNKNOWN_OPERATION, i, 0.0);
                if (operation.LangTimestampMs < 0)
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.NEGATIVE_TIMESTAMP, i, operation.LangTimestampMs);
                if (operation.DurationMs <= 0)
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.INVALID_DURATION, i, operation.DurationMs);
                if (!double.IsFinite(operation.GainDb))
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.INVALID_GAIN, i, operation.GainDb);
                if (projection.LanguageDurationMs > 0.0 && operation.LangTimestampMs > projection.LanguageDurationMs)
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.LANGUAGE_TIMESTAMP_OUT_OF_RANGE, i, operation.LangTimestampMs);
                if (isCut && projection.LanguageDurationMs > 0.0 && operation.LangTimestampMs + operation.DurationMs > projection.LanguageDurationMs)
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.CUT_OUT_OF_RANGE, i, operation.LangTimestampMs + operation.DurationMs);
                if (i > 0 && operation.LangTimestampMs == operations[i - 1].LangTimestampMs)
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.DUPLICATE_BOUNDARY, i, operation.LangTimestampMs);
                if (previousCutEndMs > operation.LangTimestampMs)
                    AddIssue(projection.Validation.Errors, isCut ? EditMapValidationCode.CUT_OVERLAP : EditMapValidationCode.OPERATION_INSIDE_CUT, i, operation.LangTimestampMs);

                double sourceBoundaryMs = projection.Map.InitialDelayMs + operation.LangTimestampMs * projection.StretchRatio + cumulativeDeltaMs;
                if (projection.SourceDurationMs > 0.0 && (sourceBoundaryMs < 0.0 || sourceBoundaryMs > projection.SourceDurationMs))
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.SOURCE_BOUNDARY_OUT_OF_RANGE, i, sourceBoundaryMs);
                if (sourceBoundaryMs < previousSourceBoundaryMs)
                    AddIssue(projection.Validation.Errors, EditMapValidationCode.SOURCE_BOUNDARY_NOT_MONOTONIC, i, sourceBoundaryMs);

                int normalizedSourceTimestampMs = RoundTimestamp(sourceBoundaryMs);
                if (operation.SourceTimestampMs != normalizedSourceTimestampMs || operation.VisualSourceTimestampMs != normalizedSourceTimestampMs)
                    AddIssue(projection.Validation.Warnings, EditMapValidationCode.SOURCE_BOUNDARY_NORMALIZED, i, sourceBoundaryMs);
                operation.SourceTimestampMs = normalizedSourceTimestampMs;
                operation.VisualSourceTimestampMs = normalizedSourceTimestampMs;

                string normalizedScope = ResolveScope(operation, projection, sourceBoundaryMs);
                if (!string.Equals(operation.Scope, normalizedScope, StringComparison.Ordinal))
                    AddIssue(projection.Validation.Warnings, EditMapValidationCode.SCOPE_NORMALIZED, i, 0.0);
                operation.Scope = normalizedScope;

                if (isCut)
                    previousCutEndMs = Math.Max(previousCutEndMs, operation.LangTimestampMs + operation.DurationMs);
                cumulativeDeltaMs += isInsert ? operation.DurationMs * projection.StretchRatio : isCut ? -operation.DurationMs * projection.StretchRatio : 0.0;
                previousSourceBoundaryMs = sourceBoundaryMs;
            }
        }

        /// <summary>
        /// Compila la sequenza mapped/gap/jump della proiezione
        /// </summary>
        private static void BuildSegments(EditMapProjection projection)
        {
            double currentLanguageMs = 0.0;
            double currentSourceMs = projection.Map.InitialDelayMs;
            List<EditOperation> operations = projection.Map.Operations;

            if (currentSourceMs > 0.0)
                projection.Segments.Add(CreateSegment(EditMapTimelineSegmentKind.InsertedGap, 0.0, currentSourceMs, 0.0, 0.0));

            for (int i = 0; i < operations.Count; i++)
            {
                EditOperation operation = operations[i];
                double boundaryLanguageMs = Math.Max(currentLanguageMs, operation.LangTimestampMs);
                double mappedSourceEndMs = currentSourceMs + (boundaryLanguageMs - currentLanguageMs) * projection.StretchRatio;
                if (boundaryLanguageMs > currentLanguageMs)
                    projection.Segments.Add(CreateSegment(EditMapTimelineSegmentKind.Mapped, currentSourceMs, mappedSourceEndMs, currentLanguageMs, boundaryLanguageMs));
                currentLanguageMs = boundaryLanguageMs;
                currentSourceMs = mappedSourceEndMs;

                double renderedDurationMs = Math.Max(0.0, operation.DurationMs * projection.StretchRatio);
                if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                {
                    projection.Segments.Add(CreateSegment(EditMapTimelineSegmentKind.InsertedGap, currentSourceMs, currentSourceMs + renderedDurationMs, currentLanguageMs, currentLanguageMs));
                    currentSourceMs += renderedDurationMs;
                }
                else if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    projection.Segments.Add(CreateSegment(EditMapTimelineSegmentKind.CutJump, currentSourceMs, currentSourceMs, currentLanguageMs, currentLanguageMs + operation.DurationMs));
                    currentLanguageMs += operation.DurationMs;
                }
            }

            if (projection.LanguageDurationMs > currentLanguageMs)
            {
                double sourceEndMs = currentSourceMs + (projection.LanguageDurationMs - currentLanguageMs) * projection.StretchRatio;
                projection.Segments.Add(CreateSegment(EditMapTimelineSegmentKind.Mapped, currentSourceMs, sourceEndMs, currentLanguageMs, projection.LanguageDurationMs));
                currentSourceMs = sourceEndMs;
            }

            if (projection.SourceDurationMs > currentSourceMs)
                projection.Segments.Add(CreateSegment(EditMapTimelineSegmentKind.InsertedGap, Math.Max(0.0, currentSourceMs), projection.SourceDurationMs, projection.LanguageDurationMs, projection.LanguageDurationMs));
        }

        /// <summary>
        /// Verifica coerenza della durata finale compilata
        /// </summary>
        private static void ValidateCompiledTimeline(EditMapProjection projection)
        {
            if (projection.SourceDurationMs <= 0.0 || projection.LanguageDurationMs <= 0.0)
                return;

            double renderedEndMs = projection.Map.InitialDelayMs + projection.LanguageDurationMs * projection.StretchRatio;
            for (int i = 0; i < projection.Map.Operations.Count; i++)
                renderedEndMs += GetRenderedOperationDeltaMs(projection.Map.Operations[i], projection.StretchRatio);
            if (Math.Abs(renderedEndMs - projection.SourceDurationMs) > 1.0)
                AddIssue(projection.Validation.Warnings, EditMapValidationCode.SOURCE_DURATION_MISMATCH, -1, renderedEndMs - projection.SourceDurationMs);
        }

        /// <summary>
        /// Crea un segmento temporale
        /// </summary>
        private static EditMapTimelineSegment CreateSegment(EditMapTimelineSegmentKind kind, double sourceStartMs, double sourceEndMs, double languageStartMs, double languageEndMs)
        {
            return new EditMapTimelineSegment
            {
                Kind = kind,
                SourceStartMs = sourceStartMs,
                SourceEndMs = sourceEndMs,
                LanguageStartMs = languageStartMs,
                LanguageEndMs = languageEndMs
            };
        }

        /// <summary>
        /// Calcola lo scope derivato dalla posizione reale dell'operazione
        /// </summary>
        private static string ResolveScope(EditOperation operation, EditMapProjection projection, double sourceBoundaryMs)
        {
            if (operation.LangTimestampMs == 0)
                return EditOperation.SCOPE_HEAD;

            if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
            {
                if (projection.LanguageDurationMs > 0.0 && operation.LangTimestampMs + operation.DurationMs >= projection.LanguageDurationMs)
                    return EditOperation.SCOPE_TAIL;
            }
            else if (projection.SourceDurationMs > 0.0)
            {
                double sourceEndMs = sourceBoundaryMs + operation.DurationMs * projection.StretchRatio;
                if (sourceEndMs >= projection.SourceDurationMs - projection.SourceTailToleranceMs)
                    return EditOperation.SCOPE_TAIL;
            }
            else if (projection.LanguageDurationMs > 0.0 && operation.LangTimestampMs >= projection.LanguageDurationMs)
            {
                return EditOperation.SCOPE_TAIL;
            }

            return EditOperation.SCOPE_BODY;
        }

        /// <summary>
        /// Ordina le operazioni per timestamp Language conservando un ordine deterministico
        /// </summary>
        private static int CompareOperations(EditOperation left, EditOperation right)
        {
            int result = left.LangTimestampMs.CompareTo(right.LangTimestampMs);
            if (result != 0)
                return result;
            return string.CompareOrdinal(left.Type, right.Type);
        }

        /// <summary>
        /// Aggiunge una segnalazione alla collezione richiesta
        /// </summary>
        private static void AddIssue(List<EditMapValidationIssue> issues, string code, int operationIndex, double valueMs)
        {
            issues.Add(new EditMapValidationIssue { Code = code, OperationIndex = operationIndex, ValueMs = valueMs });
        }

        /// <summary>
        /// Arrotonda un timestamp ai millisecondi interi del modello persistente
        /// </summary>
        private static int RoundTimestamp(double timestampMs)
        {
            return (int)Math.Round(timestampMs, MidpointRounding.AwayFromZero);
        }

        #endregion
    }
}
