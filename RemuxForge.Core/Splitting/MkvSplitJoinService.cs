using RemuxForge.Core.Chapters;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

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
            MkvFileInfo info = MkvSplitExternalTools.Instance.GetFileInfo(filePath);
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

            ChapterReadResult chapters = MkvSplitExternalTools.Instance.ReadChapters(filePath);
            if (chapters.Error.Length > 0)
            {
                part.Error = AppText.F("split.join.partChapters", name, chapters.Error);
                return part;
            }
            part.Chapters = chapters.Chapters;
            part.ChapterWarnings = chapters.Warnings;
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
                if (string.Equals(part.FilePath, outputPath, StringComparison.OrdinalIgnoreCase))
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
        public void Execute(MkvSplitJoinPlan plan, string outputPath, bool force, Func<bool> stopRequested)
        {
            if (!plan.IsValid)
                throw new InvalidOperationException(string.Join(" ", plan.Issues));

            string outputDir = Path.GetDirectoryName(outputPath);
            string temporary = Path.Combine(outputDir, TEMP_PREFIX + Guid.NewGuid().ToString("N"));
            try
            {
                this.EnsurePartsUnchanged(plan);
                Directory.CreateDirectory(temporary);
                string joined = Path.Combine(temporary, "joined.mkv");
                List<string> args = new List<string> { "-o", joined };

                // I capitoli delle parti non entrano: quelli dell'output sono gli stessi concatenati dalla classe capitoli
                for (int i = 0; i < plan.Parts.Count; i++)
                {
                    if (i > 0)
                        args.Add("+");
                    args.Add("--no-chapters");
                    args.Add(plan.Parts[i].FilePath);
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

                this.ValidateOutputFile(plan, joined);
                this.EnsurePartsUnchanged(plan);
                ThrowIfStopped(stopRequested);
                File.Move(joined, outputPath, force);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.join.completed", Path.GetFileName(outputPath), plan.Chapters.Count));
            }
            finally
            {
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
                    if (actual.BitsPerSample != expected.BitsPerSample)
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
        /// <param name="joined">File prodotto</param>
        private void ValidateOutputFile(MkvSplitJoinPlan plan, string joined)
        {
            MkvFileInfo info = MkvSplitExternalTools.Instance.GetFileInfo(joined);
            if (info == null || info.Tracks.Count != plan.Parts[0].Tracks.Count || info.ContainerDurationNs <= 0)
                throw new InvalidOperationException(AppText.T("split.join.outputMismatch"));
            // Gli avvisi di mkvmerge non fermano l'unione: il file unito deve però contenere ogni fotogramma delle parti
            int expected = 0;
            foreach (MkvSplitJoinPart part in plan.Parts)
                expected += MkvSplitExternalTools.Instance.CountPackets(part.FilePath);
            if (MkvSplitExternalTools.Instance.CountPackets(joined) != expected)
                throw new InvalidOperationException(AppText.T("split.join.outputMismatch"));
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
