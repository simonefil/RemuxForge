using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using static RemuxForge.Core.Subtitles.VobSubSubtitleUtils;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Clipping di occorrenze DVD con sequenze di controllo statiche, dinamiche o multiple; parsing IDX/PES/SPU e packetizzazione sono condivisi con Subtitles.
    /// </summary>
    internal static class MkvSplitVobSubSubtitleService
    {
        #region Metodi pubblici

        /// <summary>
        /// Legge una coppia IDX/SUB estratta con la catena delle sequenze di controllo di ogni SPU.
        /// Senza STP_DSP finale l'evento finisce sull'entry successiva, e l'ultimo dopo l'intervallo medio fra le entry, come in mkvmerge;
        /// gli SPU che non si mostrano mai non diventano eventi
        /// </summary>
        /// <param name="path">File IDX estratto, con il SUB accanto</param>
        /// <param name="track">Traccia sorgente</param>
        /// <param name="videoEndSeconds">Fine del video, che chiude l'unico SPU senza stop di una traccia con una sola entry</param>
        /// <returns>Traccia letta</returns>
        public static MkvSplitSubtitleTrack Read(string path, TrackInfo track, double videoEndSeconds)
        {
            VobSubIndexDocument document = VobSubIndexDocument.Load(path);
            MkvSplitSubtitleTrack result = new MkvSplitSubtitleTrack
            {
                Track = track,
                Extension = ".idx",
                SourceContent = document.Serialize()
            };
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
                    bool wasVisible = visible;
                    for (int command = 0; command + 1 < commands.Count; command++)
                    {
                        sequence.Commands.Add(spu[commands[command]..commands[command + 1]]);
                        // FSTA_DSP e STA_DSP mostrano, STP_DSP nasconde
                        if (spu[commands[command]] <= 2)
                            visible = spu[commands[command]] != 2;
                    }
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
                result.Events.Add(new MkvSplitSubtitleEvent
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
            return result;
        }

        /// <summary>
        /// Scrive la coppia IDX/SUB di un output con le occorrenze ritagliate e riposizionate.
        /// Le sequenze prima dell'inizio si fondono in una a ritardo 0, quelle dopo la fine si tolgono e la fine riceve STP_DSP;
        /// una catena che cambia solo nei ritardi resta byte per byte quella del sorgente. Un output senza occorrenze riceve uno SPU che non mostra niente
        /// </summary>
        /// <param name="track">Traccia letta dal sorgente</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File IDX di destinazione, con il SUB accanto</param>
        public static void Write(MkvSplitSubtitleTrack track, MkvSplitOutputProjection output, string path)
        {
            using FileStream sub = File.Create(Path.ChangeExtension(path, ".sub"));
            VobSubIndexDocument document = VobSubIndexDocument.Parse(track.SourceContent);
            List<VobSubIndexEntryRewrite> occurrences = new List<VobSubIndexEntryRewrite>();
            foreach (MkvSplitClipProjection clip in output.Clips)
            {
                double shift = clip.ResultStartSeconds - clip.SourceStartSeconds;
                foreach (MkvSplitSubtitleEvent item in track.Events)
                {
                    double start = Math.Max(item.StartSeconds, clip.SourceStartSeconds);
                    double end = Math.Min(item.EndSeconds, clip.SourceEndSeconds);
                    if (end <= start)
                        continue;
                    // Stato persistente all'inizio dell'occorrenza: per ogni comando vale l'ultimo, e i tre di visualizzazione si escludono.
                    // Se all'inizio il sottotitolo è nascosto, l'occorrenza parte dalla sequenza che lo mostra
                    List<byte[]> state = new List<byte[]>();
                    bool visible = false;
                    int next = 0;
                    for (; next < item.ControlSequences.Count; next++)
                    {
                        MkvSplitSpuControlSequence sequence = item.ControlSequences[next];
                        if (sequence.Seconds > start && (visible || sequence.Seconds >= end))
                            break;
                        start = Math.Max(start, sequence.Seconds);
                        foreach (byte[] command in sequence.Commands)
                        {
                            int same = state.FindIndex(existing => existing[0] == command[0] || (existing[0] <= 2 && command[0] <= 2));
                            if (same >= 0)
                                state[same] = command;
                            else
                                state.Add(command);
                            if (command[0] <= 2)
                                visible = command[0] != 2;
                        }
                    }
                    if (!visible)
                        continue;
                    start += shift;
                    end += shift;
                    List<MkvSplitSpuControlSequence> sequences = new List<MkvSplitSpuControlSequence> { new MkvSplitSpuControlSequence { Commands = state } };
                    for (; next < item.ControlSequences.Count && item.ControlSequences[next].Seconds + shift < end; next++)
                    {
                        MkvSplitSpuControlSequence sequence = item.ControlSequences[next];
                        sequences.Add(new MkvSplitSpuControlSequence
                        {
                            Delay = checked((int)Math.Floor((sequence.Seconds + shift - start) * 90000 / 1024)),
                            Commands = sequence.Commands
                        });
                        foreach (byte[] command in sequence.Commands.Where(command => command[0] <= 2))
                            visible = command[0] != 2;
                    }
                    if (visible)
                        sequences.Add(new MkvSplitSpuControlSequence { Delay = checked((int)Math.Floor((end - start) * 90000 / 1024)), Commands = new List<byte[]> { new byte[] { 2 } } });
                    if (sequences[^1].Delay == 0)
                        continue;
                    if (sequences.Max(sequence => sequence.Delay) > ushort.MaxValue)
                        throw new InvalidDataException(AppText.T("split.montage.vobsubDateRange"));
                    byte[] spu;
                    if (sequences.Count == item.ControlSequences.Count
                        && sequences.Select((sequence, index) => sequence.Commands.SelectMany(command => command)
                            .SequenceEqual(item.ControlSequences[index].Commands.SelectMany(command => command))).All(same => same))
                    {
                        spu = (byte[])item.BinaryPayload.Clone();
                        for (int index = 0; index < sequences.Count; index++)
                            WriteUInt16BigEndian(spu, item.ControlSequences[index].Offset, sequences[index].Delay);
                    }
                    else
                    {
                        // La tabella riscritta segue i dati dei pixel; l'ultima sequenza punta a se stessa e lo SPU resta di dimensione pari, come quelli dei DVD
                        int commandOffset = ReadUInt16BigEndian(item.BinaryPayload, 2);
                        List<byte> table = new List<byte>();
                        for (int index = 0; index < sequences.Count; index++)
                        {
                            int position = commandOffset + table.Count;
                            int following = index + 1 < sequences.Count ? position + 5 + sequences[index].Commands.Sum(command => command.Length) : position;
                            table.AddRange(new[] { (byte)(sequences[index].Delay >> 8), (byte)sequences[index].Delay, (byte)(following >> 8), (byte)following });
                            table.AddRange(sequences[index].Commands.SelectMany(command => command));
                            table.Add(0xff);
                        }
                        if ((commandOffset + table.Count) % 2 != 0)
                            table.Add(0xff);
                        spu = item.BinaryPayload.Take(commandOffset).Concat(table).ToArray();
                        if (spu.Length > ushort.MaxValue)
                            throw new InvalidDataException(AppText.T("remux.subtitles.vobsubSpuTooLarge"));
                        WriteUInt16BigEndian(spu, 0, spu.Length);
                    }
                    byte[] pes = RewritePesTimestamps(item.PacketPesHeader, checked((long)Math.Round(start * 90000)));
                    byte[] block = BuildPacketizedSpuBlock(spu, item.PacketPackHeader, pes, item.SubstreamId, out string error);
                    if (block == null)
                        throw new InvalidDataException(error);
                    occurrences.Add(new VobSubIndexEntryRewrite(item.SourceLineIndex, (long)Math.Round(start * 1000), sub.Position));
                    sub.Write(block);
                }
            }
            // Conservare una traccia vuota, non scartarla: mkvmerge non crea la traccia da un IDX senza entry, e uno SPU col solo STP_DSP non mostra niente
            if (occurrences.Count == 0)
            {
                MkvSplitSubtitleEvent first = track.Events[0];
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

        #region Metodi privati

        /// <summary>
        /// Interrompe la lettura con InvalidDataException se la condizione non è soddisfatta
        /// </summary>
        /// <param name="valid">Condizione richiesta</param>
        /// <param name="track">Traccia in lettura</param>
        /// <param name="detail">Dettaglio dell'errore</param>
        private static void Require(bool valid, TrackInfo track, string detail)
        {
            if (!valid)
                throw new InvalidDataException(AppText.F("split.montage.vobsubTrackError", track.Id, detail));
        }

        #endregion
    }
}
