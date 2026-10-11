using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static RemuxForge.Core.Subtitles.VobSubSubtitleUtils;

namespace RemuxForge.Core.Splitting.Subtitles
{
    /// <summary>
    /// Clipping di occorrenze DVD con sequenze di controllo statiche, dinamiche o multiple; parsing IDX/PES/SPU e packetizzazione sono condivisi con Subtitles.
    /// </summary>
    internal sealed class MkvSplitVobSubSubtitleClipper : MkvSplitBitmapSubtitleClipperBase<MkvSplitVobSubSubtitleEvent>
    {
        #region Variabili di classe

        /// <summary>
        /// Formato VobSub, estratto in IDX con il SUB accanto
        /// </summary>
        private static readonly MkvSplitSubtitleFormat[] s_formats = new MkvSplitSubtitleFormat[]
        {
            new MkvSplitSubtitleFormat(".idx", new[] { "VobSub" }, new[] { "S_VOBSUB" })
        };

        #endregion

        #region Proprietà

        /// <summary>
        /// VobSub
        /// </summary>
        protected override MkvSplitSubtitleFormat[] Formats => s_formats;

        /// <summary>
        /// Errore di una traccia VobSub
        /// </summary>
        protected override string TrackErrorKey => "split.montage.vobsubTrackError";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive la coppia IDX/SUB di un output con le occorrenze ritagliate e riposizionate (MkvSplitVobSubSubtitleEvent.TryClip e
        /// Serialize). Un output senza occorrenze riceve uno SPU che non mostra niente
        /// </summary>
        /// <param name="track">Traccia letta dal sorgente</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File IDX di destinazione, con il SUB accanto</param>
        public override void Write(MkvSplitSubtitleTrack<MkvSplitVobSubSubtitleEvent> track, MkvSplitOutputProjection output, string path)
        {
            using FileStream sub = File.Create(Path.ChangeExtension(path, ".sub"));
            VobSubIndexDocument document = VobSubIndexDocument.Parse(track.SourceContent);
            List<VobSubIndexEntryRewrite> occurrences = new List<VobSubIndexEntryRewrite>();
            foreach ((MkvSplitVobSubSubtitleEvent item, double sourceStart, double sourceEnd, double shift) in Clip(track, output))
            {
                if (!item.TryClip(sourceStart, sourceEnd, shift, out double resultStart, out List<MkvSplitSpuControlSequence> sequences))
                    continue;
                byte[] spu = item.Serialize(sequences);
                byte[] pes = RewritePesTimestamps(item.PacketPesHeader, checked((long)Math.Round(resultStart * 90000)));
                byte[] block = BuildPacketizedSpuBlock(spu, item.PacketPackHeader, pes, item.SubstreamId, out string error);
                if (block == null)
                    throw new InvalidDataException(error);
                occurrences.Add(new VobSubIndexEntryRewrite(item.SourceLineIndex, (long)Math.Round(resultStart * 1000), sub.Position));
                sub.Write(block);
            }
            // Conservare una traccia vuota, non scartarla: mkvmerge non crea la traccia da un IDX senza entry, e uno SPU col solo STP_DSP non mostra niente
            if (occurrences.Count == 0)
            {
                MkvSplitVobSubSubtitleEvent first = track.Events[0];
                byte[] block = BuildPacketizedSpuBlock(new byte[] { 0, 10, 0, 4, 0, 0, 0, 4, 2, 0xff }, first.PacketPackHeader, RewritePesTimestamps(first.PacketPesHeader, 0),
                    first.SubstreamId, out string error);
                if (block == null)
                    throw new InvalidDataException(error);
                occurrences.Add(new VobSubIndexEntryRewrite(first.SourceLineIndex, 0, 0));
                sub.Write(block);
            }
            document.ReplaceEntries(occurrences);
            document.Save(path);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Legge una coppia IDX/SUB estratta con la catena delle sequenze di controllo di ogni SPU.
        /// Senza STP_DSP finale l'evento finisce sull'entry successiva, e l'ultimo dopo l'intervallo medio fra le entry, come in mkvmerge;
        /// gli SPU che non si mostrano mai non diventano eventi
        /// </summary>
        /// <param name="path">File IDX estratto, con il SUB accanto</param>
        /// <param name="result">Traccia da riempire</param>
        /// <param name="videoEndSeconds">Fine del video, che chiude l'unico SPU senza stop di una traccia con una sola entry</param>
        protected override void ReadEvents(string path, MkvSplitSubtitleTrack<MkvSplitVobSubSubtitleEvent> result, double videoEndSeconds)
        {
            TrackInfo track = result.Track;
            VobSubIndexDocument document = VobSubIndexDocument.Load(path);
            result.SourceContent = document.Serialize();
            int languages = 0;
            int streamId = 0x20;
            foreach (string raw in document.Lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                {
                    languages++;
                    Match index = Regex.Match(line, @"index:\s*(\d+)", RegexOptions.IgnoreCase);
                    Require(index.Success, track, AppText.T("split.montage.vobsubIdxIndexMissing"));
                    streamId = 0x20 + int.Parse(index.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                if (line.StartsWith("timestamp:", StringComparison.OrdinalIgnoreCase))
                    Require(TryParseEntryLine(raw, out _, out _), track, AppText.T("split.montage.vobsubIdxTimestamp"));
                Require(!line.StartsWith("delay:", StringComparison.OrdinalIgnoreCase), track, AppText.T("split.montage.vobsubIdxDelay"));
            }
            Require(languages == 1 && streamId >= 0x20 && streamId <= 0x3f, track, AppText.T("split.montage.vobsubOneLanguage"));
            using FileStream source = File.OpenRead(Path.ChangeExtension(path, ".sub"));
            for (int index = 0; index < document.Entries.Count; index++)
            {
                VobSubIndexEntry entry = document.Entries[index];
                long end = index + 1 < document.Entries.Count ? document.Entries[index + 1].FilePosition : source.Length;
                double time = entry.TimestampMs / 1000.0;
                Require(entry.FilePosition >= 0 && end > entry.FilePosition && end <= source.Length, track, AppText.F("split.montage.vobsubFilepos", time));
                byte[] block = new byte[checked((int)(end - entry.FilePosition))];
                source.Position = entry.FilePosition;
                source.ReadExactly(block);
                Require(TryExtractPacketizedSpu(block, out byte[] spu, out byte[] pack, out byte[] pes, out byte substream), track, AppText.F("split.montage.vobsubPacketizedSpu", time));
                Require(substream == streamId, track, AppText.T("split.montage.vobsubSubstream"));
                bool parsed = TryParseSpu(spu, 0, spu.Length, out VobSubSpuInfo info, true, out string error, false);
                Require(parsed, track, string.IsNullOrEmpty(error) ? AppText.F("split.montage.vobsubSpuInvalid", time) : error);
                // La riscrittura sostituisce la tabella di controllo in coda allo SPU: i dati dei pixel devono starle davanti
                Require(info.PixelOffsetCommandOffsets.All(position => ReadUInt16BigEndian(spu, position) >= 4 && ReadUInt16BigEndian(spu, position) < info.CommandOffset
                    && ReadUInt16BigEndian(spu, position + 2) >= 4 && ReadUInt16BigEndian(spu, position + 2) < info.CommandOffset),
                    track, AppText.F("split.montage.vobsubPixelData", time));
                List<MkvSplitSpuControlSequence> sequences = new List<MkvSplitSpuControlSequence>();
                bool visible = false;
                double startSeconds = double.NaN;
                double endSeconds = double.NaN;
                foreach (int offset in info.SequenceOffsets)
                {
                    List<int> commands = info.SequenceCommandOffsets[offset];
                    Require(spu[commands.Last()] == 0xff, track, AppText.F("split.montage.vobsubControlSequence", time));
                    Require(sequences.Count == 0 || ReadUInt16BigEndian(spu, offset) >= sequences[^1].Delay, track, AppText.F("split.montage.vobsubControlOrder", time));
                    MkvSplitSpuControlSequence sequence = new MkvSplitSpuControlSequence
                    {
                        Delay = ReadUInt16BigEndian(spu, offset),
                        Offset = offset,
                        Seconds = time + ReadUInt16BigEndian(spu, offset) * 1024.0 / 90000
                    };
                    for (int command = 0; command + 1 < commands.Count; command++)
                        sequence.Commands.Add(spu[commands[command]..commands[command + 1]]);
                    bool wasVisible = visible;
                    visible = sequence.IsVisibleAfter(visible);
                    sequences.Add(sequence);
                    if (visible && double.IsNaN(startSeconds))
                        startSeconds = sequence.Seconds;
                    if (visible)
                        endSeconds = double.NaN;
                    else if (wasVisible)
                        endSeconds = sequence.Seconds;
                }
                // Uno SPU che non si mostra mai non ha niente da ritagliare: è anche quello che conserva una traccia vuota
                if (double.IsNaN(startSeconds))
                    continue;
                // mkvmerge (r_vobsub.cpp) dà all'SPU senza stop la distanza dall'entry successiva e all'ultima la media delle distanze;
                // con una sola entry la media è 0, e la fine diventa quella del video
                if (double.IsNaN(endSeconds) && index + 1 < document.Entries.Count)
                    endSeconds = document.Entries[index + 1].TimestampMs / 1000.0;
                else if (double.IsNaN(endSeconds) && document.Entries.Count > 1)
                    endSeconds = (entry.TimestampMs + (document.Entries[^1].TimestampMs - document.Entries[0].TimestampMs) / (document.Entries.Count - 1.0)) / 1000.0;
                else if (double.IsNaN(endSeconds))
                    endSeconds = videoEndSeconds;
                Require(endSeconds > startSeconds, track, AppText.F("split.montage.vobsubDuration", time));
                result.Events.Add(new MkvSplitVobSubSubtitleEvent
                {
                    SourceLineIndex = entry.LineIndex,
                    StartSeconds = startSeconds,
                    EndSeconds = endSeconds,
                    BinaryPayload = spu,
                    PacketPackHeader = pack,
                    PacketPesHeader = pes,
                    SubstreamId = substream,
                    ControlSequences = sequences
                });
            }
        }

        #endregion
    }
}
