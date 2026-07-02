using RemuxForge.Core.Audio;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Costruisce il testo del pannello dettaglio episodio
    /// </summary>
    public class DetailPanelTextBuilder
    {
        #region Metodi pubblici

        /// <summary>
        /// Costruisce il testo dettaglio per il record selezionato
        /// </summary>
        /// <param name="record">Record selezionato</param>
        /// <param name="options">Opzioni correnti</param>
        /// <returns>Stringa con dettaglio completo</returns>
        public string Build(FileProcessingRecord record, Options options)
        {
            string result = "";
            StringBuilder sb;
            if (record == null)
            {
                return result;
            }

            sb = new StringBuilder(512);

            this.AppendHeader(sb, record);
            this.AppendSourceFile(sb, record);
            this.AppendLanguageFile(sb, record);
            this.AppendTracks(sb, record, options);
            this.AppendSync(sb, record);
            this.AppendErrors(sb, record);
            this.AppendProcessingTimes(sb, record);
            this.AppendResult(sb, record);
            this.AppendEncoding(sb, record);

            result = sb.ToString();
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiunge intestazione record
        /// </summary>
        private void AppendHeader(StringBuilder sb, FileProcessingRecord record)
        {
            sb.Append("--- ").Append(record.EpisodeId).Append(" [").Append(Utils.GetStatusText(record.Status)).Append("] ---\n\n");
        }

        /// <summary>
        /// Aggiunge informazioni file sorgente
        /// </summary>
        private void AppendSourceFile(StringBuilder sb, FileProcessingRecord record)
        {
            sb.Append(AppText.T("web.detail.sourceFile")).Append('\n');
            sb.Append("  ").Append(record.SourceFileName).Append('\n');
            sb.Append(AppText.F("web.detail.sizeLine", Utils.FormatSize(record.SourceSize))).Append('\n');
            sb.Append('\n');
        }

        /// <summary>
        /// Aggiunge informazioni file lingua
        /// </summary>
        private void AppendLanguageFile(StringBuilder sb, FileProcessingRecord record)
        {
            sb.Append(AppText.T("web.detail.languageFile")).Append('\n');
            sb.Append("  ").Append(record.LangFileName.Length > 0 ? record.LangFileName : AppText.T("web.common.none")).Append('\n');
            if (record.LangSize > 0)
            {
                sb.Append(AppText.F("web.detail.sizeLine", Utils.FormatSize(record.LangSize))).Append('\n');
            }
        }

        /// <summary>
        /// Aggiunge tracce sorgente, importate e risultato finale
        /// </summary>
        private void AppendTracks(StringBuilder sb, FileProcessingRecord record, Options options)
        {
            bool filterAudio;
            bool filterSub;
            sb.Append('\n').Append(AppText.T("web.detail.sourceTracks")).Append('\n');
            sb.Append(AppText.F("web.detail.audioLine", Utils.FormatTrackList(record.SourceAudioTracks))).Append('\n');
            sb.Append(AppText.F("web.detail.subLine", Utils.FormatTrackList(record.SourceSubTracks))).Append('\n');

            if (record.KeptSourceAudioIds.Count > 0 || record.KeptSourceSubIds.Count > 0)
            {
                sb.Append('\n').Append(AppText.T("web.detail.keptSourceTracks")).Append('\n');
                sb.Append(AppText.F("web.detail.audioLine", Utils.FormatTrackListByIds(record.SourceAudioTracks, record.KeptSourceAudioIds))).Append('\n');
                sb.Append(AppText.F("web.detail.subLine", Utils.FormatTrackListByIds(record.SourceSubTracks, record.KeptSourceSubIds))).Append('\n');
            }

            if (record.ImportedAudioTracks.Count > 0 || record.ImportedSubTracks.Count > 0)
            {
                sb.Append('\n').Append(AppText.T("web.detail.importTracks")).Append('\n');
                sb.Append(AppText.F("web.detail.audioLine", this.FormatImportedAudioTrackList(record.ImportedAudioTracks, record.AudioProcessingPreview, options))).Append('\n');
                sb.Append(AppText.F("web.detail.subLine", Utils.FormatTrackList(record.ImportedSubTracks))).Append('\n');
            }

            filterAudio = record.KeptSourceAudioIds.Count > 0;
            filterSub = record.KeptSourceSubIds.Count > 0;
            if (record.ImportedAudioTracks.Count > 0 || record.ImportedSubTracks.Count > 0 || filterAudio || filterSub)
            {
                sb.Append('\n').Append(AppText.T("web.detail.finalResult")).Append('\n');
                sb.Append(AppText.F("web.detail.audioLine", this.FormatResultAudioTrackList(record, options, filterAudio))).Append('\n');
                sb.Append(AppText.F("web.detail.subLine", Utils.FormatResultTrackList(record.SourceSubTracks, record.KeptSourceSubIds, record.ImportedSubTracks, "", filterSub))).Append('\n');
            }

            this.AppendAudioProcessingPreview(sb, record, options);
        }

        /// <summary>
        /// Formatta tracce audio importate con piano processing effettivo
        /// </summary>
        private string FormatImportedAudioTrackList(List<TrackInfo> tracks, AudioProcessingPlan plan, Options options)
        {
            StringBuilder sb = new StringBuilder();

            if (tracks == null || tracks.Count == 0)
            {
                return "-";
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                if (i > 0) { sb.Append(" | "); }
                sb.Append(this.FormatAudioTrackWithPlan(tracks[i], plan != null ? plan.FindLangTrack(tracks[i].Id) : null, options));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Formatta il risultato audio finale con piano processing effettivo
        /// </summary>
        private string FormatResultAudioTrackList(FileProcessingRecord record, Options options, bool filterAudio)
        {
            StringBuilder sb = new StringBuilder();
            int count = 0;

            if (record.SourceAudioTracks != null)
            {
                for (int i = 0; i < record.SourceAudioTracks.Count; i++)
                {
                    if (filterAudio && !record.KeptSourceAudioIds.Contains(record.SourceAudioTracks[i].Id))
                    {
                        continue;
                    }

                    if (count > 0) { sb.Append(" | "); }
                    sb.Append(this.FormatAudioTrackWithPlan(record.SourceAudioTracks[i], record.AudioProcessingPreview != null ? record.AudioProcessingPreview.FindSourceTrack(record.SourceAudioTracks[i].Id) : null, options));
                    count++;
                }
            }

            if (record.ImportedAudioTracks != null)
            {
                for (int i = 0; i < record.ImportedAudioTracks.Count; i++)
                {
                    if (count > 0) { sb.Append(" | "); }
                    sb.Append(this.FormatAudioTrackWithPlan(record.ImportedAudioTracks[i], record.AudioProcessingPreview != null ? record.AudioProcessingPreview.FindLangTrack(record.ImportedAudioTracks[i].Id) : null, options));
                    count++;
                }
            }

            return count > 0 ? sb.ToString() : "-";
        }

        /// <summary>
        /// Formatta una traccia audio con suffisso processing se previsto
        /// </summary>
        private string FormatAudioTrackWithPlan(TrackInfo track, AudioTrackProcessingPlan plan, Options options)
        {
            string result = Utils.FormatTrackCompact(track);
            string summary = this.FormatAudioProcessingSummary(plan, options, false);

            if (summary.Length > 0)
            {
                result += " -> " + summary;
            }

            return result;
        }

        /// <summary>
        /// Aggiunge dettaglio operativo del piano audio
        /// </summary>
        private void AppendAudioProcessingPreview(StringBuilder sb, FileProcessingRecord record, Options options)
        {
            List<AudioTrackProcessingPlan> tracks;
            if (record.AudioProcessingPreview == null)
            {
                return;
            }

            tracks = record.AudioProcessingPreview.GetAllTracks();
            if (tracks.Count == 0)
            {
                return;
            }

            sb.Append('\n').Append(AppText.T("web.detail.audioProcessing")).Append('\n');
            for (int i = 0; i < tracks.Count; i++)
            {
                string origin = tracks[i].IsSource ? "SRC" : "LANG";
                string summary = this.FormatAudioProcessingSummary(tracks[i], options, true);
                sb.Append(AppText.F("web.detail.audioProcessingTrackLine", origin, tracks[i].Track.Id, summary.Length > 0 ? summary : AppText.T("web.detail.audioNoRender"))).Append('\n');
            }
        }

        /// <summary>
        /// Formatta il motivo operativo di una traccia audio
        /// </summary>
        private string FormatAudioProcessingSummary(AudioTrackProcessingPlan plan, Options options, bool includeSkip)
        {
            List<string> parts = new List<string>();
            string target;

            if (plan == null || options == null)
            {
                return "";
            }
            if (plan.ErrorMessage.Length > 0)
            {
                return AppText.F("web.detail.audioError", plan.ErrorMessage);
            }

            target = options.AudioFormat.Length > 0 ? Utils.FormatAudioFormat(options.AudioFormat) : "";
            if (plan.RenderRequired && target.Length > 0)
            {
                parts.Add(target);
            }

            if (plan.SourceFillHasWork)
            {
                parts.Add(this.FormatSourceFillSummary(plan, options));
            }
            else if (plan.DeepEditRender)
            {
                parts.Add(AppText.T("web.detail.audioDeepEditMap"));
            }
            else if (plan.GenericRenderRequired)
            {
                this.AddGenericAudioReasons(parts, plan, options);
            }
            else if (includeSkip && plan.SourceFillConfigured)
            {
                parts.Add(AppText.T("web.detail.audioSourceFillNoWork"));
            }
            else if (includeSkip)
            {
                parts.Add(plan.GenericProcessing ? AppText.T("web.detail.audioSkipCompatible") : AppText.T("web.detail.audioSkipOutsideScope"));
            }

            if (plan.BypassAudioDelay)
            {
                parts.Add(AppText.T("web.detail.audioDelayMaterialized"));
            }
            if (plan.RenderRequired && !plan.GenericRenderRequired)
            {
                this.AddRenderPostProcessingReasons(parts, plan, options);
            }

            return string.Join(", ", parts);
        }

        /// <summary>
        /// Aggiunge motivi del processing audio generico
        /// </summary>
        private void AddGenericAudioReasons(List<string> parts, AudioTrackProcessingPlan plan, Options options)
        {
            bool hasReason = false;

            if (!CodecMapping.IsTargetAudioFormat(plan.Track, options.AudioFormat))
            {
                parts.Add(AppText.T("web.detail.audioCodecConversion"));
                hasReason = true;
            }
            if (options.AudioDownsample24To16 && (plan.Track.BitsPerSample <= 0 || plan.Track.BitsPerSample > 16))
            {
                parts.Add("24->16 bit");
                hasReason = true;
            }
            if (options.AudioPeakNormalize)
            {
                parts.Add(AppText.F("web.detail.audioNormalize", options.AudioPeakTargetDb.ToString("F2", CultureInfo.InvariantCulture)));
                hasReason = true;
            }
            if (!hasReason)
            {
                parts.Add(AppText.T("web.detail.audioGenericProcessing"));
            }
        }

        /// <summary>
        /// Aggiunge post-processing comune ai render deep/source-fill
        /// </summary>
        private void AddRenderPostProcessingReasons(List<string> parts, AudioTrackProcessingPlan plan, Options options)
        {
            if (options.AudioDownsample24To16 && (plan.Track.BitsPerSample <= 0 || plan.Track.BitsPerSample > 16))
            {
                parts.Add("24->16 bit");
            }
            if (options.AudioPeakNormalize)
            {
                if (plan.ActualSourceFill)
                {
                    parts.Add(AppText.F("web.detail.audioPreNormalizeSourceLang", options.AudioPeakTargetDb.ToString("F2", CultureInfo.InvariantCulture)));
                }
                else
                {
                    parts.Add(AppText.F("web.detail.audioNormalize", options.AudioPeakTargetDb.ToString("F2", CultureInfo.InvariantCulture)));
                }
            }
        }

        /// <summary>
        /// Formatta riepilogo source-fill della traccia
        /// </summary>
        private string FormatSourceFillSummary(AudioTrackProcessingPlan plan, Options options)
        {
            List<string> parts = new List<string>();
            AudioSourceFillPlan sourceFillPlan = plan.SourceFillPlan;
            int insertSourceCount = 0;
            int insertSourceMs = 0;

            if (sourceFillPlan == null)
            {
                return AppText.T("web.detail.audioSourceFill");
            }

            parts.Add(plan.ActualSourceFill ? AppText.T("web.detail.audioSourceFill") : AppText.T("web.detail.audioSourceFillTimelineRender"));
            if (sourceFillPlan.StartFillMs > 0)
            {
                parts.Add(AppText.F("web.detail.audioSourceFillStart", this.FormatDurationSeconds(sourceFillPlan.StartFillMs)));
            }
            if (sourceFillPlan.EndFillMs > 0)
            {
                parts.Add(AppText.F("web.detail.audioSourceFillEnd", this.FormatDurationSeconds(sourceFillPlan.EndFillMs)));
            }
            if (sourceFillPlan.InsertOperations != null)
            {
                for (int i = 0; i < sourceFillPlan.InsertOperations.Count; i++)
                {
                    EditOperation operation = sourceFillPlan.InsertOperations[i];
                    int renderedOperationMs = EditMapTimelineHelper.LanguageDurationToRenderedDurationMs(operation.DurationMs, sourceFillPlan.StretchRatio);
                    if (string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) &&
                        options.AudioSourceFillInsertSilence &&
                        renderedOperationMs > options.AudioSourceFillThresholdMs)
                    {
                        insertSourceCount++;
                        insertSourceMs += renderedOperationMs;
                    }
                }
            }
            if (insertSourceCount > 0)
            {
                parts.Add(AppText.F("web.detail.audioSourceFillInsertSource", insertSourceCount, this.FormatDurationSeconds(insertSourceMs)));
            }
            else if (sourceFillPlan.InsertOperations != null && sourceFillPlan.InsertOperations.Count > 0)
            {
                parts.Add(AppText.F("web.detail.audioEditOps", sourceFillPlan.InsertOperations.Count));
            }

            return string.Join(", ", parts);
        }

        /// <summary>
        /// Formatta una durata breve in secondi
        /// </summary>
        private string FormatDurationSeconds(int durationMs)
        {
            return (durationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>
        /// Aggiunge sezione sincronizzazione
        /// </summary>
        private void AppendSync(StringBuilder sb, FileProcessingRecord record)
        {
            sb.Append('\n').Append(AppText.T("web.detail.sync")).Append('\n');
            sb.Append(AppText.F("web.detail.audioDelayLine", Utils.FormatDelay(record.AudioDelayApplied))).Append('\n');
            sb.Append(AppText.F("web.detail.subDelayLine", Utils.FormatDelay(record.SubDelayApplied))).Append('\n');
            if (record.StretchFactor.Length > 0)
            {
                sb.Append(AppText.F("web.detail.stretchLine", record.StretchFactor)).Append('\n');
            }
            if (record.SpeedCorrectionApplied)
            {
                sb.Append(AppText.T("web.detail.speedCorrectionApplied")).Append('\n');
            }
            if (record.FrameSyncResult != null)
            {
                this.AppendFrameSyncSummary(sb, record.FrameSyncResult);
            }
            if (record.DeepAnalysisApplied && record.DeepAnalysisMap != null)
            {
                this.AppendDeepAnalysisSummary(sb, record.DeepAnalysisMap);
            }
        }

        /// <summary>
        /// Aggiunge riepilogo operativo DeepAnalysis
        /// </summary>
        private void AppendDeepAnalysisSummary(StringBuilder sb, EditMap editMap)
        {
            sb.Append(AppText.T("web.detail.deepApplied")).Append('\n');
            if (editMap.StretchFactor.Length > 0)
            {
                sb.Append(AppText.F("web.detail.deepStretchLine", editMap.StretchFactor)).Append('\n');
            }

            this.AppendEditOperations(sb, editMap.Operations);
        }

        /// <summary>
        /// Aggiunge operazioni EditMap
        /// </summary>
        private void AppendEditOperations(StringBuilder sb, List<EditOperation> operations)
        {
            sb.Append(AppText.F("web.detail.editOperations", operations.Count)).Append('\n');
            if (operations.Count == 0)
            {
                sb.Append(AppText.T("web.detail.noLocalEdits")).Append('\n');
                return;
            }

            for (int i = 0; i < operations.Count; i++)
            {
                EditOperation op = operations[i];
                sb.Append("    ").Append(i + 1).Append(". ");
                sb.Append(this.FormatEditOperationType(op.Type)).Append(" @ lang ");
                sb.Append(this.FormatTimestamp(op.LangTimestampMs)).Append(", ");
                sb.Append(AppText.T("web.detail.sourceShort")).Append(' ');
                sb.Append(this.FormatTimestamp(op.SourceTimestampMs)).Append(", ");
                sb.Append(AppText.F("web.detail.durationSeconds", (op.DurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture))).Append('\n');
            }
        }

        /// <summary>
        /// Aggiunge riepilogo operativo FrameSync
        /// </summary>
        private void AppendFrameSyncSummary(StringBuilder sb, FrameSyncResult frameSyncResult)
        {
            int accepted = 0;
            int total = 0;
            if (frameSyncResult.Points != null)
            {
                total = frameSyncResult.Points.Count;
                for (int i = 0; i < frameSyncResult.Points.Count; i++)
                {
                    if (frameSyncResult.Points[i].Accepted)
                    {
                        accepted++;
                    }
                }
            }

            sb.Append(AppText.F("web.detail.frameSyncLine", frameSyncResult.Success ? "OK" : AppText.T("web.detail.failed"))).Append('\n');
            sb.Append(AppText.F("web.detail.frameSyncOffset", this.FormatOptionalDelay(frameSyncResult.OffsetMs))).Append('\n');
            sb.Append(AppText.F("web.detail.confidenceLine", frameSyncResult.Confidence.ToString("P0", CultureInfo.InvariantCulture))).Append('\n');
            if (total > 0)
            {
                sb.Append(AppText.F("web.detail.checkpointValid", accepted, total)).Append('\n');
            }
            if (frameSyncResult.FailureReason.Length > 0)
            {
                sb.Append(AppText.F("web.detail.reasonLine", frameSyncResult.FailureReason)).Append('\n');
            }
        }

        /// <summary>
        /// Aggiunge errori e motivi skip
        /// </summary>
        private void AppendErrors(StringBuilder sb, FileProcessingRecord record)
        {
            if (record.ErrorMessage.Length > 0)
            {
                sb.Append('\n').Append(AppText.T("web.detail.error")).Append('\n');
                sb.Append("  ").Append(record.ErrorMessage).Append('\n');
            }
            if (record.SkipReason.Length > 0)
            {
                sb.Append('\n').Append(AppText.T("web.detail.skipped")).Append('\n');
                sb.Append("  ").Append(record.SkipReason).Append('\n');
            }
        }

        /// <summary>
        /// Aggiunge tempi elaborazione
        /// </summary>
        private void AppendProcessingTimes(StringBuilder sb, FileProcessingRecord record)
        {
            if (record.SpeedCorrectionTimeMs > 0 || record.FrameSyncTimeMs > 0 || record.DeepAnalysisTimeMs > 0 || record.MergeTimeMs > 0)
            {
                sb.Append('\n').Append(AppText.T("web.detail.processingTimes")).Append('\n');
                if (record.SpeedCorrectionTimeMs > 0) { sb.Append(AppText.F("web.detail.speedTime", record.SpeedCorrectionTimeMs)).Append('\n'); }
                if (record.FrameSyncTimeMs > 0) { sb.Append("  Frame-sync: ").Append(record.FrameSyncTimeMs).Append(" ms\n"); }
                if (record.DeepAnalysisTimeMs > 0) { sb.Append("  Deep analysis: ").Append(record.DeepAnalysisTimeMs).Append(" ms\n"); }
                if (record.MergeTimeMs > 0) { sb.Append("  Merge:      ").Append(record.MergeTimeMs).Append(" ms\n"); }
            }
        }

        /// <summary>
        /// Aggiunge risultato file
        /// </summary>
        private void AppendResult(StringBuilder sb, FileProcessingRecord record)
        {
            if (record.ResultSize > 0)
            {
                sb.Append('\n').Append(AppText.T("web.detail.result")).Append('\n');
                sb.Append(AppText.F("web.detail.sizeLine", Utils.FormatSize(record.ResultSize))).Append('\n');
                if (record.ResultFilePath.Length > 0)
                {
                    sb.Append(AppText.F("web.detail.fileLine", record.ResultFilePath)).Append('\n');
                }
            }
        }

        /// <summary>
        /// Aggiunge sezione encoding
        /// </summary>
        private void AppendEncoding(StringBuilder sb, FileProcessingRecord record)
        {
            if (record.EncodingProfileName.Length == 0)
            {
                return;
            }

            sb.Append('\n').Append(AppText.T("web.detail.encoding")).Append('\n');
            sb.Append(AppText.F("web.detail.profileLine", record.EncodingProfileName)).Append('\n');
            if (record.EncodedSize > 0 && record.ResultSize > 0)
            {
                long riduzione = 100 - (record.EncodedSize * 100 / record.ResultSize);
                sb.Append(AppText.F("web.detail.sizeChangeLine", Utils.FormatSize(record.ResultSize), Utils.FormatSize(record.EncodedSize)));
                sb.Append(AppText.F("web.detail.reductionSuffix", riduzione)).Append('\n');
            }
            if (record.EncodingTimeMs > 0)
            {
                sb.Append(AppText.F("web.detail.timeLine", record.EncodingTimeMs)).Append('\n');
            }
            if (record.EncodingCommand.Length > 0)
            {
                sb.Append(AppText.T("web.detail.commandAvailable")).Append('\n');
            }
        }

        /// <summary>
        /// Formatta un delay opzionale
        /// </summary>
        private string FormatOptionalDelay(int value)
        {
            if (value == int.MinValue)
            {
                return AppText.T("web.common.naLower");
            }

            return Utils.FormatDelay(value);
        }

        /// <summary>
        /// Formatta tipo operazione edit
        /// </summary>
        private string FormatEditOperationType(string operationType)
        {
            string result = operationType;

            if (operationType == EditOperation.CUT_SEGMENT)
            {
                result = AppText.T("web.detail.editCut");
            }
            else if (operationType == EditOperation.INSERT_SILENCE)
            {
                result = AppText.T("web.detail.editInsert");
            }

            return result;
        }

        /// <summary>
        /// Formatta timestamp in minutaggio
        /// </summary>
        private string FormatTimestamp(int timestampMs)
        {
            if (timestampMs < 0) { timestampMs = 0; }

            int totalSeconds = timestampMs / 1000;
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;
            int millis = timestampMs % 1000;

            if (hours > 0)
            {
                return hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + seconds.ToString("00", CultureInfo.InvariantCulture) + "." + millis.ToString("000", CultureInfo.InvariantCulture);
            }

            return minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + seconds.ToString("00", CultureInfo.InvariantCulture) + "." + millis.ToString("000", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
