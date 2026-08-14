using RemuxForge.Core.Analysis.Diagnostics;
using RemuxForge.Core.Models;
using System;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Scrive la diagnostica FrameSync SIFT in JSON e CSV
    /// </summary>
    public class FrameSyncDiagnosticsWriter : DiagnosticsWriterBase
    {
        #region Costanti

        /// <summary>
        /// Nome della cartella diagnostica FrameSync
        /// </summary>
        private const string DIAGNOSTICS_FOLDER_NAME = "framesync-diagnostics";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive la diagnostica completa di un episodio elaborato con FrameSync
        /// </summary>
        /// <param name="record">Record elaborato</param>
        /// <param name="options">Opzioni operative</param>
        /// <returns>Percorso del file JSON oppure stringa vuota</returns>
        public string Write(FileProcessingRecord record, Options options)
        {
            if (record == null || record.FrameSyncResult == null)
                return "";

            string baseName = this.BuildDiagnosticsBasePath(DIAGNOSTICS_FOLDER_NAME, record.EpisodeId);
            string jsonPath = baseName + ".json";
            FrameSyncDiagnosticsPayload payload = new FrameSyncDiagnosticsPayload();
            payload.GeneratedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            payload.EpisodeId = record.EpisodeId;
            payload.SourceFileName = record.SourceFileName;
            payload.LanguageFileName = record.LangFileName;
            payload.SourceFilePath = record.SourceFilePath;
            payload.LanguageFilePath = record.LangFilePath;
            payload.FrameSyncTimeMs = record.FrameSyncTimeMs;
            payload.AudioDelayApplied = record.AudioDelayApplied;
            payload.SubtitleDelayApplied = record.SubDelayApplied;
            payload.SpeedCorrectionMode = options != null ? options.SpeedCorrectionMode : "";
            payload.ManualStretchFactor = options != null ? options.ManualStretchFactor : "";
            payload.CandidateCsvPath = baseName + "-candidates.csv";
            payload.PointCsvPath = baseName + "-points.csv";
            payload.GeometryCsvPath = baseName + "-geometry.csv";
            payload.Result = record.FrameSyncResult;

            this.WriteJson(jsonPath, payload);
            this.WriteCandidateCsv(payload.CandidateCsvPath, record);
            this.WritePointCsv(payload.PointCsvPath, record);
            this.WriteGeometryCsv(payload.GeometryCsvPath, record);
            return jsonPath;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Scrive i modi temporali prodotti dalla ricerca iniziale
        /// </summary>
        private void WriteCandidateCsv(string filePath, FileProcessingRecord record)
        {
            using (StreamWriter writer = new StreamWriter(filePath, false))
            {
                writer.WriteLine("episode,source_file,language_file,phase,success,ambiguous,offset_ms,backend,processed_pairs,accepted_pairs,strong_pairs,ambiguous_pairs,source_coverage_ms,language_coverage_ms,mean_score,dispersion_ms");
                if (record.FrameSyncResult.Initial != null && record.FrameSyncResult.Initial.Candidates != null)
                {
                    for (int candidateIndex = 0; candidateIndex < record.FrameSyncResult.Initial.Candidates.Count; candidateIndex++)
                        this.WriteCandidateCsvRow(writer, record, "initial", record.FrameSyncResult.Initial.Candidates[candidateIndex]);
                }
                if (record.FrameSyncResult.PrecisionCandidate != null)
                    this.WriteCandidateCsvRow(writer, record, "precision", record.FrameSyncResult.PrecisionCandidate);
            }
        }

        /// <summary>
        /// Scrive una riga candidata iniziale o full-rate
        /// </summary>
        private void WriteCandidateCsvRow(StreamWriter writer, FileProcessingRecord record, string phase, FrameSyncCandidate candidate)
        {
            writer.Write(this.EscapeCsv(record.EpisodeId));
            writer.Write(',');
            writer.Write(this.EscapeCsv(record.SourceFileName));
            writer.Write(',');
            writer.Write(this.EscapeCsv(record.LangFileName));
            writer.Write(',');
            writer.Write(this.EscapeCsv(phase));
            writer.Write(',');
            writer.Write(record.FrameSyncResult.Success ? "true" : "false");
            writer.Write(',');
            writer.Write(record.FrameSyncResult.Ambiguous ? "true" : "false");
            writer.Write(',');
            writer.Write(candidate.OffsetMs.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(this.EscapeCsv(candidate.Backend));
            writer.Write(',');
            writer.Write(candidate.ProcessedPairCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(candidate.AcceptedPairCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(candidate.StrongPairCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(candidate.AmbiguousPairCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(candidate.SourceCoverageMs.ToString("F3", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(candidate.LanguageCoverageMs.ToString("F3", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(candidate.MeanScore.ToString("F6", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(candidate.DispersionMs.ToString("F3", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Scrive i risultati dei checkpoint SIFT locali
        /// </summary>
        private void WritePointCsv(string filePath, FileProcessingRecord record)
        {
            using (StreamWriter writer = new StreamWriter(filePath, false))
            {
                writer.WriteLine("episode,source_file,language_file,checkpoint_percent,expected_offset_ms,best_offset_ms,backend,processed_pairs,accepted_pairs,strong_pairs,source_coverage_ms,language_coverage_ms,mean_score,dispersion_ms,accepted,reject_reason,timing_ms,extract_ms,match_ms");
                for (int pointIndex = 0; pointIndex < record.FrameSyncResult.Points.Count; pointIndex++)
                {
                    FrameSyncPointResult point = record.FrameSyncResult.Points[pointIndex];
                    writer.Write(this.EscapeCsv(record.EpisodeId));
                    writer.Write(',');
                    writer.Write(this.EscapeCsv(record.SourceFileName));
                    writer.Write(',');
                    writer.Write(this.EscapeCsv(record.LangFileName));
                    writer.Write(',');
                    writer.Write(point.CheckpointPercent.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.ExpectedOffsetMs.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.BestOffsetMs.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(this.EscapeCsv(point.Backend));
                    writer.Write(',');
                    writer.Write(point.ProcessedPairCount.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.AcceptedPairCount.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.StrongPairCount.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.SourceCoverageMs.ToString("F3", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.LanguageCoverageMs.ToString("F3", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.BestScore.ToString("F6", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.DispersionMs.ToString("F3", CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.Accepted ? "true" : "false");
                    writer.Write(',');
                    writer.Write(this.EscapeCsv(point.RejectReason));
                    writer.Write(',');
                    writer.Write(point.TimingMs.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.Write(point.ExtractMs.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.WriteLine(point.MatchMs.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>
        /// Scrive la geometria source e language usata dal preprocess SIFT
        /// </summary>
        private void WriteGeometryCsv(string filePath, FileProcessingRecord record)
        {
            using (StreamWriter writer = new StreamWriter(filePath, false))
            {
                writer.WriteLine("episode,role,file_path,width,height,sar_num,sar_den,dar_num,dar_den,display_width,display_height,display_aspect,has_black_border_crop,crop_left,crop_right,crop_top,crop_bottom,geometry_crop_to_four_three,manual_analysis_crop_px,crop_mode");
                this.WriteGeometryCsvRow(writer, record, "source", record.FrameSyncResult.SourceGeometry);
                this.WriteGeometryCsvRow(writer, record, "language", record.FrameSyncResult.LanguageGeometry);
            }
        }

        /// <summary>
        /// Scrive una riga della diagnostica geometrica
        /// </summary>
        private void WriteGeometryCsvRow(StreamWriter writer, FileProcessingRecord record, string role, FrameSyncGeometryInfo geometry)
        {
            if (geometry == null)
                return;
            writer.Write(this.EscapeCsv(record.EpisodeId));
            writer.Write(',');
            writer.Write(this.EscapeCsv(role));
            writer.Write(',');
            writer.Write(this.EscapeCsv(geometry.FilePath));
            writer.Write(',');
            writer.Write(geometry.Width.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.Height.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.SarNum.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.SarDen.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.DarNum.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.DarDen.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.DisplayWidth.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.DisplayHeight.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.DisplayAspect.ToString("F6", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.HasBlackBorderCrop ? "true" : "false");
            writer.Write(',');
            writer.Write(geometry.CropLeft.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.CropRight.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.CropTop.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.CropBottom.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(geometry.GeometryCropToFourThree ? "true" : "false");
            writer.Write(',');
            writer.Write(this.EscapeCsv(geometry.ManualAnalysisCropPx));
            writer.Write(',');
            writer.WriteLine(this.EscapeCsv(geometry.CropMode));
        }

        /// <summary>
        /// Applica l'escape CSV minimo a una stringa
        /// </summary>
        private string EscapeCsv(string value)
        {
            string result = (value ?? "").Replace("\"", "\"\"");
            if (result.IndexOf(',') >= 0 || result.IndexOf('"') >= 0 || result.IndexOf('\n') >= 0 || result.IndexOf('\r') >= 0)
                result = "\"" + result + "\"";
            return result;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Payload JSON della diagnostica FrameSync
        /// </summary>
        private sealed class FrameSyncDiagnosticsPayload
        {
            /// <summary>
            /// Timestamp di generazione
            /// </summary>
            public string GeneratedAt { get; set; }

            /// <summary>
            /// Identificativo episodio
            /// </summary>
            public string EpisodeId { get; set; }

            /// <summary>
            /// Nome del file sorgente
            /// </summary>
            public string SourceFileName { get; set; }

            /// <summary>
            /// Nome del file lingua
            /// </summary>
            public string LanguageFileName { get; set; }

            /// <summary>
            /// Percorso del file sorgente
            /// </summary>
            public string SourceFilePath { get; set; }

            /// <summary>
            /// Percorso del file lingua
            /// </summary>
            public string LanguageFilePath { get; set; }

            /// <summary>
            /// Tempo FrameSync in millisecondi
            /// </summary>
            public long FrameSyncTimeMs { get; set; }

            /// <summary>
            /// Delay audio applicato
            /// </summary>
            public int AudioDelayApplied { get; set; }

            /// <summary>
            /// Delay sottotitoli applicato
            /// </summary>
            public int SubtitleDelayApplied { get; set; }

            /// <summary>
            /// Modalità Speed Correction
            /// </summary>
            public string SpeedCorrectionMode { get; set; }

            /// <summary>
            /// Fattore di stretch manuale
            /// </summary>
            public string ManualStretchFactor { get; set; }

            /// <summary>
            /// Percorso CSV dei candidati
            /// </summary>
            public string CandidateCsvPath { get; set; }

            /// <summary>
            /// Percorso CSV dei checkpoint
            /// </summary>
            public string PointCsvPath { get; set; }

            /// <summary>
            /// Percorso CSV della geometria
            /// </summary>
            public string GeometryCsvPath { get; set; }

            /// <summary>
            /// Risultato FrameSync completo
            /// </summary>
            public FrameSyncResult Result { get; set; }
        }

        #endregion
    }
}
