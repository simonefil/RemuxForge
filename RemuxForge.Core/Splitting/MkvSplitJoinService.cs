using RemuxForge.Core.Chapters;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Unione di file interi in sequenza con mkvmerge append e capitoli concatenati
    /// </summary>
    public class MkvSplitJoinService
    {
        #region Costanti

        /// <summary>
        /// Prefisso della cartella temporanea creata nella cartella di destinazione
        /// </summary>
        private const string TEMP_PREFIX = ".remuxforge-join-";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Suffisso di parte in coda al nome: p1, pt1, part1, cd1, disc1, disk1. La parola chiave deve seguire
        /// un separatore o una cifra (file1p1, Film - Part 2), così «Ep1» o «Concept1» non vengono toccati
        /// </summary>
        private static readonly Regex s_partSuffixRe = new Regex(@"^(?<base>.*?[^\s._-])(?:[\s._-]+|(?<=\d))(?:part|pt|cd|disc|disk|p)[\s._-]*\d{1,3}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Matrice intra predefinita di MPEG-4 Part 2 (ISO/IEC 14496-2), in ordine raster come ff_mpeg4_default_intra_matrix di libavcodec
        /// </summary>
        private static readonly int[] s_mpeg4DefaultIntraMatrix = new int[]
        {
             8, 17, 18, 19, 21, 23, 25, 27,
            17, 18, 19, 21, 23, 25, 27, 28,
            20, 21, 22, 23, 24, 26, 28, 30,
            21, 22, 23, 24, 26, 28, 30, 32,
            22, 23, 24, 26, 28, 30, 32, 35,
            23, 24, 26, 28, 30, 32, 35, 38,
            25, 26, 28, 30, 32, 35, 38, 41,
            27, 28, 30, 32, 35, 38, 41, 45
        };

        /// <summary>
        /// Matrice non intra predefinita di MPEG-4 Part 2 (ISO/IEC 14496-2), in ordine raster come ff_mpeg4_default_non_intra_matrix di libavcodec
        /// </summary>
        private static readonly int[] s_mpeg4DefaultNonIntraMatrix = new int[]
        {
            16, 17, 18, 19, 20, 21, 22, 23,
            17, 18, 19, 20, 21, 22, 23, 24,
            18, 19, 20, 21, 22, 23, 24, 25,
            19, 20, 21, 22, 23, 24, 26, 27,
            20, 21, 22, 23, 25, 26, 27, 28,
            21, 22, 23, 24, 26, 27, 28, 30,
            22, 23, 24, 26, 27, 28, 30, 31,
            23, 24, 25, 27, 28, 30, 31, 33
        };

        /// <summary>
        /// Posizione raster di ogni coefficiente nell'ordine zigzag con cui il VOL scrive le matrici caricate, come ff_zigzag_direct di libavcodec
        /// </summary>
        private static readonly int[] s_zigzag = new int[]
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge una parte: tracce e durata da mkvmerge, capitoli dalla classe capitoli
        /// </summary>
        /// <param name="filePath">File da leggere</param>
        /// <returns>Parte letta, con Error valorizzato se non utilizzabile</returns>
        public MkvSplitJoinPart ReadPart(string filePath)
        {
            MkvSplitJoinPart part = new MkvSplitJoinPart();
            string name = Path.GetFileName(filePath);

            if (!File.Exists(filePath))
            {
                part.Identity.FullPath = Path.GetFullPath(filePath);
                part.Error = AppText.F("split.join.partMissing", name);
                return part;
            }

            part.Identity = MkvSplitSourceIdentity.FromFile(filePath);
            // Si uniscono solo i container accettati da Remux e Split (D02, D20)
            if (!MediaContainerFormats.IsSupported(filePath))
            {
                part.Error = AppText.F("split.join.partUnsupported", name, string.Join(", ", MediaContainerFormats.Extensions));
                return part;
            }

            // Tracce, durata e capitoli si leggono sul Matroska che verrà accodato: il confronto fra parti vale sulle parti normalizzate (D20)
            using (NormalizedMediaLease media = NormalizedMediaStore.Instance.Acquire(filePath, true))
            {
                string error = media.Open(CancellationToken.None);
                if (error.Length > 0)
                {
                    part.Error = error;
                    return part;
                }

                MkvFileInfo info = MkvSplitExternalTools.Instance.GetFileInfo(media.Path);
                if (info == null)
                {
                    part.Error = AppText.F("split.join.partUnreadable", name);
                    return part;
                }
                if (info.ContainerDurationNs <= 0)
                {
                    part.Error = AppText.F("split.join.partNoDuration", name);
                    return part;
                }

                part.DurationNs = info.ContainerDurationNs;
                part.Tracks = info.Tracks;
                // Il VOL si legge sulla parte normalizzata finché il temporaneo esiste: il piano confronta le firme senza rinormalizzare
                TrackInfo video = info.Tracks.Find(track => track.Type == "video");
                if (video != null && video.Codec == MkvSplitCodecCapabilities.MPEG4_PART2_CODEC)
                    part.Mpeg4Quantization = this.ReadMpeg4Quantization(video.CodecPrivateData);

                ChapterReadResult chapters = MkvSplitExternalTools.Instance.ReadChapters(media.Path);
                if (chapters.Error.Length > 0)
                {
                    part.Error = AppText.F("split.join.partChapters", name, chapters.Error);
                    return part;
                }
                part.Chapters = chapters.Chapters;
                part.ChapterWarnings = chapters.Warnings;
            }
            return part;
        }

        /// <summary>
        /// Costruisce il piano: compatibilità delle parti rispetto alla prima (C13) e capitoli concatenati (C6, C18)
        /// </summary>
        /// <param name="parts">Parti nell'ordine di unione</param>
        /// <returns>Piano con eventuali motivi di blocco</returns>
        public MkvSplitJoinPlan BuildPlan(List<MkvSplitJoinPart> parts)
        {
            MkvSplitJoinPlan plan = new MkvSplitJoinPlan();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ChapterPart> chapterParts = new List<ChapterPart>();
            string firstQuantization = null;
            string firstMpeg4Name = "";

            plan.Parts.AddRange(parts);
            if (parts.Count < 2)
                plan.Issues.Add(AppText.T("split.join.tooFewParts"));

            for (int i = 0; i < parts.Count; i++)
            {
                MkvSplitJoinPart part = parts[i];
                string name = Path.GetFileName(part.FilePath);

                if (!seen.Add(part.FilePath))
                    plan.Issues.Add(AppText.F("split.join.duplicatePart", name));
                if (part.Error.Length > 0)
                {
                    plan.Issues.Add(part.Error);
                    continue;
                }
                if (i > 0 && parts[0].Error.Length == 0)
                    this.CompareTracks(parts[0], part, plan.Issues);

                // MPEG-4 Part 2: l'avviso sulla quantizzazione compare prima della conferma e l'esecuzione lo riporta nel log.
                // Il riferimento è la prima parte con la firma leggibile
                TrackInfo video = part.Tracks.Find(track => track.Type == "video");
                if (video != null && video.Codec == MkvSplitCodecCapabilities.MPEG4_PART2_CODEC)
                {
                    if (part.Mpeg4Quantization.Length == 0)
                        plan.Warnings.Add(AppText.F("split.join.mpeg4QuantizationUnreadable", name));
                    else if (firstQuantization == null)
                    {
                        firstQuantization = part.Mpeg4Quantization;
                        firstMpeg4Name = name;
                    }
                    else if (part.Mpeg4Quantization != firstQuantization)
                        plan.Warnings.Add(AppText.F("split.join.mpeg4Quantization", name, firstMpeg4Name));
                }

                plan.TotalDurationNs += part.DurationNs;
                chapterParts.Add(new ChapterPart { Chapters = part.Chapters, DurationNs = part.DurationNs });
            }

            // I capitoli si calcolano solo su parti tutte leggibili, altrimenti gli spostamenti sarebbero sbagliati
            if (chapterParts.Count == parts.Count && parts.Count > 0)
                plan.Chapters = ChapterTimelineService.Concatenate(chapterParts);

            return plan;
        }

        /// <summary>
        /// Propone il nome dell'output togliendo il suffisso di parte dal nome della prima parte (C12).
        /// Senza un suffisso riconoscibile non propone nulla (C20)
        /// </summary>
        /// <param name="firstPartPath">Percorso della prima parte</param>
        /// <returns>Nome file proposto oppure stringa vuota</returns>
        public static string ProposeOutputName(string firstPartPath)
        {
            if (string.IsNullOrWhiteSpace(firstPartPath))
                return "";

            Match match = s_partSuffixRe.Match(Path.GetFileNameWithoutExtension(firstPartPath));
            if (!match.Success)
                return "";

            string baseName = match.Groups["base"].Value.TrimEnd(' ', '.', '_', '-');
            return baseName.Length > 0 ? baseName + ".mkv" : "";
        }

        /// <summary>
        /// Cartella dell'output: quella configurata per Split, altrimenti quella della prima parte
        /// </summary>
        /// <param name="configuredOutputDir">Cartella output delle opzioni Split</param>
        /// <param name="firstPartPath">Percorso della prima parte</param>
        /// <returns>Cartella di destinazione</returns>
        public static string ResolveOutputDir(string configuredOutputDir, string firstPartPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredOutputDir))
                return Path.GetFullPath(configuredOutputDir);
            return string.IsNullOrWhiteSpace(firstPartPath) ? "" : Path.GetDirectoryName(Path.GetFullPath(firstPartPath));
        }

        /// <summary>
        /// Verifica il percorso di output prima di avviare l'unione
        /// </summary>
        /// <param name="plan">Piano dell'unione</param>
        /// <param name="outputDir">Cartella di destinazione</param>
        /// <param name="fileName">Nome file scelto dall'utente</param>
        /// <param name="force">Sovrascrittura consentita dalle opzioni Split</param>
        /// <param name="outputPath">Percorso completo risultante</param>
        /// <returns>Motivi localizzati che bloccano l'output</returns>
        public List<string> ValidateOutput(MkvSplitJoinPlan plan, string outputDir, string fileName, bool force, out string outputPath)
        {
            List<string> issues = new List<string>();
            string name = (fileName ?? "").Trim();
            outputPath = "";

            if (name.Length == 0)
            {
                issues.Add(AppText.T("split.join.outputNameRequired"));
                return issues;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != Path.GetFileName(name))
            {
                issues.Add(AppText.F("split.join.outputNameInvalid", name));
                return issues;
            }
            if (!string.Equals(Path.GetExtension(name), ".mkv", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(AppText.T("split.join.outputExtension"));
                return issues;
            }
            if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
            {
                issues.Add(AppText.F("split.join.outputDirMissing", outputDir ?? ""));
                return issues;
            }

            outputPath = Path.GetFullPath(Path.Combine(outputDir, name));
            foreach (MkvSplitJoinPart part in plan.Parts)
            {
                if (FileHelper.IsSamePath(part.FilePath, outputPath))
                {
                    issues.Add(AppText.F("split.join.outputIsPart", name));
                    return issues;
                }
            }
            if (File.Exists(outputPath) && !force)
                issues.Add(AppText.F("split.join.outputExists", name));

            return issues;
        }

        /// <summary>
        /// Esegue l'unione in una cartella temporanea accanto all'output e pubblica il file solo a lavoro concluso
        /// </summary>
        /// <param name="plan">Piano valido</param>
        /// <param name="outputPath">Percorso validato da ValidateOutput</param>
        /// <param name="force">Sovrascrittura consentita</param>
        /// <param name="stopRequested">Richiesta di stop cooperativa</param>
        /// <param name="cancellationToken">Annullamento collegato allo stop, che interrompe anche la normalizzazione delle parti</param>
        public void Execute(MkvSplitJoinPlan plan, string outputPath, bool force, Func<bool> stopRequested, CancellationToken cancellationToken = default)
        {
            if (!plan.IsValid)
                throw new InvalidOperationException(string.Join(" ", plan.Issues));

            string outputDir = Path.GetDirectoryName(outputPath);
            string temporary = Path.Combine(outputDir, TEMP_PREFIX + Guid.NewGuid().ToString("N"));
            List<NormalizedMediaLease> media = new List<NormalizedMediaLease>();
            try
            {
                this.EnsurePartsUnchanged(plan);
                Directory.CreateDirectory(temporary);
                string joined = Path.Combine(temporary, "joined.mkv");
                List<string> args = new List<string> { "-o", joined };

                // Un temporaneo per parte, dall'avvio alla fine dell'unione: mkvmerge accoda solo Matroska con le stesse tracce lette dal piano
                foreach (MkvSplitJoinPart part in plan.Parts)
                {
                    ThrowIfStopped(stopRequested);
                    NormalizedMediaLease normalized = NormalizedMediaStore.Instance.Acquire(part.FilePath, true);
                    media.Add(normalized);
                    string error = normalized.Open(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (error.Length > 0)
                        throw new InvalidOperationException(error);
                }

                // Gli avvisi del piano (quantizzazione MPEG-4, D24) restano anche nel log dell'esecuzione
                foreach (string warning in plan.Warnings)
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Warning, warning);

                // MPEG-4 Part 2: mkvmerge accoda tenendo solo il VOL dei dati privati della prima parte, quindi ogni parte
                // porta il proprio VOL reinserito a ogni keyframe. ffmpeg lavora sulla sola prima traccia video, mkvmerge la ricompone
                // con le altre tracce della parte normalizzata, altre tracce video comprese, nello stesso ordine, con gli stessi ID e le stesse proprietà
                List<string> appended = new List<string>();
                for (int i = 0; i < plan.Parts.Count; i++)
                {
                    MkvSplitJoinPart part = plan.Parts[i];
                    TrackInfo video = part.Tracks.Find(track => track.Type == "video");
                    if (video == null || video.Codec != MkvSplitCodecCapabilities.MPEG4_PART2_CODEC)
                    {
                        appended.Add(media[i].Path);
                        continue;
                    }

                    ThrowIfStopped(stopRequested);
                    string videoPath = Path.Combine(temporary, "vol-" + i + ".mkv");
                    string recomposed = Path.Combine(temporary, "part-" + i + ".mkv");
                    MkvSplitExternalTools.Instance.RunFfmpeg(new string[] { "-y", "-v", "error", "-i", media[i].Path,
                        "-map", "0:v:0", "-c", "copy", "-bsf:v", "dump_extra=freq=k", videoPath });
                    List<string> recompose = new List<string> { "-o", recomposed };
                    MkvSplitExternalTools.AddTrackOptions(recompose, 0, video);
                    recompose.AddRange(new string[] { "--no-chapters", "--no-attachments", "--no-global-tags", "--no-track-tags", videoPath,
                        "--video-tracks", "!" + video.Id, media[i].Path,
                        "--track-order", string.Join(",", part.Tracks.ConvertAll(track => track.Id == video.Id ? "0:0" : "1:" + track.Id)) });
                    MkvSplitExternalTools.Instance.RunMkvmerge(recompose);
                    appended.Add(recomposed);
                }

                // I capitoli delle parti non entrano: quelli dell'output sono gli stessi concatenati dalla classe capitoli
                for (int i = 0; i < appended.Count; i++)
                {
                    if (i > 0)
                        args.Add("+");
                    args.Add("--no-chapters");
                    args.Add(appended[i]);
                }
                if (plan.Chapters.Count > 0)
                {
                    string chapters = Path.Combine(temporary, "chapters.xml");
                    ChapterTimelineService.WriteXmlFile(plan.Chapters, chapters);
                    args.Add("--chapters");
                    args.Add(chapters);
                }

                ThrowIfStopped(stopRequested);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.F("split.join.running", plan.Parts.Count, Path.GetFileName(outputPath)));
                ProcessResult result = MkvSplitExternalTools.Instance.RunMkvmergeResult(args);
                ThrowIfStopped(stopRequested);

                // Come nel resto di Split l'uscita 1 sono avvisi con il file completo: lo verifica il controllo sul file unito
                if (result.ExitCode == 1)
                    MkvSplitExternalTools.LogMkvmergeWarnings(result);
                else if (result.ExitCode != 0)
                    throw new InvalidOperationException(AppText.F("split.join.mkvmergeFailed", result.ExitCode, MkvSplitExternalTools.MkvmergeMessages(result)));

                this.ValidateOutputFile(plan, appended, joined);
                this.EnsurePartsUnchanged(plan);
                ThrowIfStopped(stopRequested);
                File.Move(joined, outputPath, force);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.join.completed", Path.GetFileName(outputPath), plan.Chapters.Count));
            }
            finally
            {
                foreach (NormalizedMediaLease normalized in media)
                    normalized.Dispose();
                try
                {
                    if (Directory.Exists(temporary))
                        Directory.Delete(temporary, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Confronta le tracce di una parte con quelle della prima: mkvmerge accoda in silenzio una parte con meno tracce
        /// e segnala solo con un avviso una risoluzione diversa, quindi il controllo va fatto prima
        /// </summary>
        /// <param name="first">Prima parte</param>
        /// <param name="part">Parte da confrontare</param>
        /// <param name="issues">Motivi di blocco</param>
        private void CompareTracks(MkvSplitJoinPart first, MkvSplitJoinPart part, List<string> issues)
        {
            string name = Path.GetFileName(part.FilePath);
            if (part.Tracks.Count != first.Tracks.Count)
            {
                issues.Add(AppText.F("split.join.trackCount", name, part.Tracks.Count, first.Tracks.Count));
                return;
            }

            for (int t = 0; t < first.Tracks.Count; t++)
            {
                TrackInfo expected = first.Tracks[t];
                TrackInfo actual = part.Tracks[t];
                int number = t + 1;

                if (!string.Equals(actual.Type, expected.Type, StringComparison.Ordinal))
                {
                    issues.Add(AppText.F("split.join.trackType", name, number, actual.Type, expected.Type));
                    continue;
                }
                if (!string.Equals(actual.Codec, expected.Codec, StringComparison.Ordinal))
                    issues.Add(AppText.F("split.join.trackCodec", name, number, actual.Codec, expected.Codec));
                if (expected.Type == "video" && !string.Equals(actual.PixelDimensions, expected.PixelDimensions, StringComparison.Ordinal))
                    issues.Add(AppText.F("split.join.videoSize", name, number, actual.PixelDimensions, expected.PixelDimensions));
                if (expected.Type == "audio")
                {
                    if (actual.Channels != expected.Channels)
                        issues.Add(AppText.F("split.join.audioChannels", name, number, actual.Channels, expected.Channels));
                    if (actual.SamplingFrequency != expected.SamplingFrequency)
                        issues.Add(AppText.F("split.join.audioSampling", name, number, actual.SamplingFrequency, expected.SamplingFrequency));
                    // La profondità conta solo per l'audio lossless: nei formati con perdita è un campo d'intestazione che cambia
                    // secondo il muxer (MP3 in AVI 0, nell'MKV scritto da ffmpeg 32)
                    if (actual.BitsPerSample != expected.BitsPerSample && CodecMapping.IsLosslessCodec(expected.Codec))
                        issues.Add(AppText.F("split.join.audioBits", name, number, actual.BitsPerSample, expected.BitsPerSample));
                }
            }
        }

        /// <summary>
        /// Blocca se una parte è cambiata su disco dopo la lettura
        /// </summary>
        /// <param name="plan">Piano dell'unione</param>
        private void EnsurePartsUnchanged(MkvSplitJoinPlan plan)
        {
            foreach (MkvSplitJoinPart part in plan.Parts)
            {
                if (!File.Exists(part.FilePath) || !part.Identity.Matches(MkvSplitSourceIdentity.FromFile(part.FilePath)))
                    throw new InvalidOperationException(AppText.F("split.join.partChanged", Path.GetFileName(part.FilePath)));
            }
        }

        /// <summary>
        /// Controlla che il file prodotto abbia le tracce e i fotogrammi attesi
        /// </summary>
        /// <param name="plan">Piano dell'unione</param>
        /// <param name="appended">File accodati, nell'ordine del piano</param>
        /// <param name="joined">File prodotto</param>
        private void ValidateOutputFile(MkvSplitJoinPlan plan, List<string> appended, string joined)
        {
            MkvFileInfo info = MkvSplitExternalTools.Instance.GetFileInfo(joined);
            if (info == null || info.Tracks.Count != plan.Parts[0].Tracks.Count || info.ContainerDurationNs <= 0)
                throw new InvalidOperationException(AppText.T("split.join.outputMismatch"));
            // Le tracce dell'output sono quelle della prima parte normalizzata, nello stesso ordine e con le stesse proprietà,
            // anche quando la parte MPEG-4 è stata ricomposta attorno al video con il VOL reinserito
            for (int t = 0; t < info.Tracks.Count; t++)
            {
                TrackInfo expectedTrack = plan.Parts[0].Tracks[t];
                TrackInfo actual = info.Tracks[t];
                if (actual.Id != expectedTrack.Id || actual.Type != expectedTrack.Type || actual.Codec != expectedTrack.Codec
                    || actual.Language != expectedTrack.Language || actual.LanguageIetf != expectedTrack.LanguageIetf || actual.Name != expectedTrack.Name
                    || actual.DefaultTrack != expectedTrack.DefaultTrack || actual.ForcedTrack != expectedTrack.ForcedTrack)
                    throw new InvalidOperationException(AppText.T("split.join.outputMismatch"));
            }
            // Gli avvisi di mkvmerge non fermano l'unione: il file unito deve però contenere ogni pacchetto video delle parti
            int expected = 0;
            foreach (string part in appended)
                expected += MkvSplitExternalTools.Instance.CountPackets(part);
            if (MkvSplitExternalTools.Instance.CountPackets(joined) != expected)
                throw new InvalidOperationException(AppText.T("split.join.outputMismatch"));
        }

        /// <summary>
        /// Legge dal VOL nei dati privati della traccia video quant_type e matrici di quantizzazione caricate.
        /// I campi precedenti si leggono solo per arrivarci, secondo la sintassi di ISO/IEC 14496-2 §6.2.3
        /// </summary>
        /// <param name="hex">Dati privati della traccia video della parte normalizzata, con MPEG-4 Part 2 nativo, già letti da mkvmerge</param>
        /// <returns>Firma della quantizzazione, oppure stringa vuota se il VOL non è leggibile</returns>
        private string ReadMpeg4Quantization(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                return "";

            // Start code del VOL: 00 00 01 20..2F
            byte[] data = Convert.FromHexString(hex);
            int volStart = MpegStartCode.Find(data, 0, data.Length, code => (code & 0xF0) == 0x20);
            if (volStart < 0)
                return "";
            long position = (volStart + 4) * 8L;

            bool overrun = false;
            int Bits(int count)
            {
                int value = 0;
                for (int b = 0; b < count; b++)
                {
                    if (position >= data.Length * 8L)
                    {
                        overrun = true;
                        return 0;
                    }
                    value = (value << 1) | ((data[position >> 3] >> (7 - (int)(position & 7))) & 1);
                    position++;
                }
                return value;
            }

            // random_accessible_vol, video_object_type_indication
            Bits(1);
            Bits(8);
            int verid = 1;
            if (Bits(1) == 1)
            {
                verid = Bits(4);
                Bits(3);
            }
            // aspect_ratio_info, con par_width e par_height se esteso
            if (Bits(4) == 15)
                Bits(16);
            // vol_control_parameters: chroma_format, low_delay, vbv_parameters
            if (Bits(1) == 1)
            {
                Bits(3);
                if (Bits(1) == 1)
                {
                    Bits(15); Bits(1); Bits(15); Bits(1); Bits(15); Bits(1);
                    Bits(3); Bits(11); Bits(1); Bits(15); Bits(1);
                }
            }
            // video_object_layer_shape: 0 rettangolare, 2 solo binaria, 3 scala di grigi
            int shape = Bits(2);
            if (shape == 3 && verid != 1)
                Bits(4);
            Bits(1);
            int resolution = Bits(16);
            Bits(1);
            if (Bits(1) == 1)
            {
                int incrementBits = 1;
                while ((1 << incrementBits) < resolution)
                    incrementBits++;
                Bits(incrementBits);
            }
            // Senza texture o in scala di grigi la quantizzazione non segue nella forma attesa
            if (shape == 2 || shape == 3)
                return "";
            if (shape == 0)
            {
                Bits(1); Bits(13); Bits(1); Bits(13); Bits(1);
            }
            // interlaced, obmc_disable, sprite_enable
            Bits(1);
            Bits(1);
            int sprite = Bits(verid == 1 ? 1 : 2);
            if (sprite == 1 || sprite == 2)
            {
                if (sprite != 2)
                {
                    Bits(13); Bits(1); Bits(13); Bits(1); Bits(13); Bits(1); Bits(13); Bits(1);
                }
                Bits(6); Bits(2); Bits(1);
                if (sprite != 2)
                    Bits(1);
            }
            if (verid != 1 && shape != 0)
                Bits(1);
            // not_8_bit: quant_precision e bits_per_pixel
            if (Bits(1) == 1)
            {
                Bits(4);
                Bits(4);
            }

            // quant_type e, con la quantizzazione MPEG, le matrici effettive intra e non intra: si parte dalla predefinita e
            // i valori caricati, in ordine zigzag e chiusi da uno zero, la sostituiscono ripetendo l'ultimo fino al 64° come
            // libavcodec. Così una matrice caricata uguale alla predefinita ha la stessa firma della predefinita non caricata
            int quantType = Bits(1);
            StringBuilder signature = new StringBuilder("quant_type=" + quantType);
            if (quantType == 1)
            {
                for (int matrix = 0; matrix < 2; matrix++)
                {
                    int[] effective = (int[])(matrix == 0 ? s_mpeg4DefaultIntraMatrix : s_mpeg4DefaultNonIntraMatrix).Clone();
                    if (Bits(1) == 1)
                    {
                        int last = 0;
                        int k = 0;
                        for (; k < 64; k++)
                        {
                            int value = Bits(8);
                            if (value == 0)
                                break;
                            last = value;
                            effective[s_zigzag[k]] = value;
                        }
                        for (; k < 64; k++)
                            effective[s_zigzag[k]] = last;
                    }
                    signature.Append(matrix == 0 ? ";intra=" : ";inter=").Append(string.Join(",", effective));
                }
            }
            return overrun ? "" : signature.ToString();
        }

        /// <summary>
        /// Interrompe se è stato chiesto lo stop
        /// </summary>
        /// <param name="stopRequested">Richiesta di stop cooperativa</param>
        private static void ThrowIfStopped(Func<bool> stopRequested)
        {
            if (stopRequested != null && stopRequested())
                throw new OperationCanceledException();
        }

        #endregion
    }
}
