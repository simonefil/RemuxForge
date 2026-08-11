using RemuxForge.Core.Analysis.Diagnostics;
using RemuxForge.Core.Models;
using System;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Scrive diagnostica frame-sync in formato JSON per tuning e regressioni
    /// </summary>
    public class FrameSyncDiagnosticsWriter : DiagnosticsWriterBase
    {
        #region Costanti

        /// <summary>
        /// Nome cartella diagnostica
        /// </summary>
        private const string DIAGNOSTICS_FOLDER_NAME = "framesync-diagnostics";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive un file JSON diagnostico per un episodio elaborato con frame-sync
        /// </summary>
        /// <param name="record">Record elaborazione</param>
        /// <param name="options">Opzioni operative</param>
        /// <returns>Percorso file scritto, vuoto se non scritto</returns>
        public string Write(FileProcessingRecord record, Options options)
        {
            string result = "";
            string baseName;
            string candidateCsvPath;
            string pointCsvPath;
            string geometryCsvPath;
            string audioCsvPath;
            FrameSyncDiagnosticsPayload payload;
            if (record == null || record.FrameSyncResult == null)
            {
                return result;
            }

            baseName = this.BuildDiagnosticsBasePath(DIAGNOSTICS_FOLDER_NAME, record.EpisodeId);
            result = baseName + ".json";
            candidateCsvPath = baseName + "-candidates.csv";
            pointCsvPath = baseName + "-points.csv";
            geometryCsvPath = baseName + "-geometry.csv";
            audioCsvPath = baseName + "-audio.csv";

            payload = new FrameSyncDiagnosticsPayload();
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
            payload.CandidateCsvPath = candidateCsvPath;
            payload.PointCsvPath = pointCsvPath;
            payload.GeometryCsvPath = geometryCsvPath;
            payload.AudioCsvPath = audioCsvPath;
            payload.Result = record.FrameSyncResult;

            this.WriteJson(result, payload);
            this.WriteCandidateCsv(candidateCsvPath, record);
            this.WritePointCsv(pointCsvPath, record);
            this.WriteGeometryCsv(geometryCsvPath, record);
            this.WriteAudioCsv(audioCsvPath, record);

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Scrive CSV candidati iniziali
        /// </summary>
        /// <param name="filePath">Path CSV</param>
        /// <param name="record">Record elaborazione</param>
        private void WriteCandidateCsv(string filePath, FileProcessingRecord record)
        {
            using (StreamWriter writer = new StreamWriter(filePath, false))
            {
                writer.WriteLine("episode,source_file,language_file,success,ambiguous,offset_ms,source,votes,matched_cuts,descriptor_votes,descriptor_agreement,visual_score,blur_score,temporal_score,edge_score,block_score,motion_score,hash_score,combined_score,second_best_score,margin");

                if (record.FrameSyncResult.Initial != null && record.FrameSyncResult.Initial.Candidates != null)
                {
                    for (int i = 0; i < record.FrameSyncResult.Initial.Candidates.Count; i++)
                    {
                        FrameSyncCandidate candidate = record.FrameSyncResult.Initial.Candidates[i];
                        writer.Write(this.EscapeCsv(record.EpisodeId));
                        writer.Write(',');
                        writer.Write(this.EscapeCsv(record.SourceFileName));
                        writer.Write(',');
                        writer.Write(this.EscapeCsv(record.LangFileName));
                        writer.Write(',');
                        writer.Write(record.FrameSyncResult.Success ? "true" : "false");
                        writer.Write(',');
                        writer.Write(record.FrameSyncResult.Ambiguous ? "true" : "false");
                        writer.Write(',');
                        writer.Write(candidate.OffsetMs.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(this.EscapeCsv(candidate.Source));
                        writer.Write(',');
                        writer.Write(candidate.VoteCount.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.MatchedCuts.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.DescriptorVotes.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.DescriptorAgreement.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.VisualScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.BlurScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.TemporalScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.EdgeScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.BlockScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.MotionScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.HashScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.CombinedScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(candidate.SecondBestScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.WriteLine(candidate.Margin.ToString("F6", CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        /// <summary>
        /// Scrive CSV checkpoint frame-sync
        /// </summary>
        /// <param name="filePath">Path CSV</param>
        /// <param name="record">Record elaborazione</param>
        private void WritePointCsv(string filePath, FileProcessingRecord record)
        {
            using (StreamWriter writer = new StreamWriter(filePath, false))
            {
                writer.WriteLine("episode,source_file,language_file,checkpoint_percent,expected_offset_ms,best_offset_ms,best_score,blur_score,second_best_score,margin,descriptor_votes,descriptor_agreement,motion_score,source_variance,language_variance,source_black_ratio,language_black_ratio,accepted,reject_reason,match_method,timing_ms,extract_ms,scene_cut_ms,candidate_ms");

                if (record.FrameSyncResult.Points != null)
                {
                    for (int i = 0; i < record.FrameSyncResult.Points.Count; i++)
                    {
                        FrameSyncPointResult point = record.FrameSyncResult.Points[i];
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
                        writer.Write(point.BestScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.BlurScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.SecondBestScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.Margin.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.DescriptorVotes.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.DescriptorAgreement.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.MotionScore.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.SourceVariance.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.LanguageVariance.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.SourceBlackRatio.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.LanguageBlackRatio.ToString("F6", CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.Accepted ? "true" : "false");
                        writer.Write(',');
                        writer.Write(this.EscapeCsv(point.RejectReason));
                        writer.Write(',');
                        writer.Write(this.EscapeCsv(point.MatchMethod));
                        writer.Write(',');
                        writer.Write(point.TimingMs.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.ExtractMs.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(point.SceneCutMs.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.WriteLine(point.CandidateMs.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        /// <summary>
        /// Scrive CSV geometria source/lang
        /// </summary>
        /// <param name="filePath">Path CSV</param>
        /// <param name="record">Record elaborazione</param>
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
        /// Scrive CSV audio globale
        /// </summary>
        /// <param name="filePath">Path CSV</param>
        /// <param name="record">Record elaborazione</param>
        private void WriteAudioCsv(string filePath, FileProcessingRecord record)
        {
            using (StreamWriter writer = new StreamWriter(filePath, false))
            {
                writer.WriteLine("episode,source_file,language_file,success,offset_ms,video_offset_ms,audio_video_delta_ms,confirmed_video_initial,rejected_video_initial,score,margin,coverage,envelope_score,silence_score,onset_score,derivative_score,silence_run_score,chunk_score,candidate_count,window_ms,timing_ms,extraction_ms,correlation_ms,source_cache_hit,language_cache_hit,failure_reason");

                if (record.FrameSyncResult.AudioGlobal == null)
                {
                    return;
                }

                AudioGlobalFingerprintResult audio = record.FrameSyncResult.AudioGlobal;
                writer.Write(this.EscapeCsv(record.EpisodeId));
                writer.Write(',');
                writer.Write(this.EscapeCsv(record.SourceFileName));
                writer.Write(',');
                writer.Write(this.EscapeCsv(record.LangFileName));
                writer.Write(',');
                writer.Write(audio.Success ? "true" : "false");
                writer.Write(',');
                writer.Write(audio.OffsetMs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.VideoOffsetMs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.AudioVideoDeltaMs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.ConfirmedVideoInitial ? "true" : "false");
                writer.Write(',');
                writer.Write(audio.RejectedVideoInitial ? "true" : "false");
                writer.Write(',');
                writer.Write(audio.Score.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.Margin.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.Coverage.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.EnvelopeScore.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.SilenceScore.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.OnsetScore.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.DerivativeScore.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.SilenceRunScore.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.ChunkScore.ToString("F6", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.CandidateCount.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.WindowMs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.TimingMs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.ExtractionMs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.CorrelationMs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(audio.SourceCacheHit ? "true" : "false");
                writer.Write(',');
                writer.Write(audio.LanguageCacheHit ? "true" : "false");
                writer.Write(',');
                writer.WriteLine(this.EscapeCsv(audio.FailureReason));
            }
        }

        /// <summary>
        /// Scrive una riga CSV geometria
        /// </summary>
        /// <param name="writer">Writer CSV</param>
        /// <param name="record">Record elaborazione</param>
        /// <param name="role">Ruolo file</param>
        /// <param name="geometry">Geometria</param>
        private void WriteGeometryCsvRow(StreamWriter writer, FileProcessingRecord record, string role, FrameSyncGeometryInfo geometry)
        {
            if (geometry == null)
            {
                return;
            }

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
        /// Escape CSV minimale
        /// </summary>
        /// <param name="value">Valore originale</param>
        /// <returns>Valore escapato</returns>
        private string EscapeCsv(string value)
        {
            string result = value;

            if (result == null)
            {
                result = "";
            }

            result = result.Replace("\"", "\"\"");

            if (result.IndexOf(',') >= 0 || result.IndexOf('"') >= 0 || result.IndexOf('\n') >= 0 || result.IndexOf('\r') >= 0)
            {
                result = "\"" + result + "\"";
            }

            return result;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Payload JSON diagnostico frame-sync
        /// </summary>
        private class FrameSyncDiagnosticsPayload
        {
            /// <summary>
            /// Timestamp generazione
            /// </summary>
            public string GeneratedAt { get; set; }

            /// <summary>
            /// ID episodio
            /// </summary>
            public string EpisodeId { get; set; }

            /// <summary>
            /// Nome file sorgente
            /// </summary>
            public string SourceFileName { get; set; }

            /// <summary>
            /// Nome file lingua
            /// </summary>
            public string LanguageFileName { get; set; }

            /// <summary>
            /// Path file sorgente
            /// </summary>
            public string SourceFilePath { get; set; }

            /// <summary>
            /// Path file lingua
            /// </summary>
            public string LanguageFilePath { get; set; }

            /// <summary>
            /// Tempo frame-sync in millisecondi
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
            /// Modalità speed correction usata
            /// </summary>
            public string SpeedCorrectionMode { get; set; }

            /// <summary>
            /// Stretch factor manuale richiesto
            /// </summary>
            public string ManualStretchFactor { get; set; }

            /// <summary>
            /// Path CSV candidati
            /// </summary>
            public string CandidateCsvPath { get; set; }

            /// <summary>
            /// Path CSV checkpoint
            /// </summary>
            public string PointCsvPath { get; set; }

            /// <summary>
            /// Path CSV geometria
            /// </summary>
            public string GeometryCsvPath { get; set; }

            /// <summary>
            /// Path CSV audio globale
            /// </summary>
            public string AudioCsvPath { get; set; }

            /// <summary>
            /// Risultato frame-sync
            /// </summary>
            public FrameSyncResult Result { get; set; }
        }

        #endregion
    }
}
