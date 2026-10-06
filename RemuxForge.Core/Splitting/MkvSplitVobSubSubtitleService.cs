using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using static RemuxForge.Core.Subtitles.VobSubSubtitleUtils;

namespace RemuxForge.Core.Splitting
{
    /// <summary>Clipping di occorrenze DVD statiche; parsing IDX/PES/SPU e packetizzazione sono condivisi con Subtitles.</summary>
    internal static class MkvSplitVobSubSubtitleService
    {
        private static void Require(bool valid, TrackInfo track, string detail)
        {
            if (!valid) throw new InvalidDataException("VobSub track " + track.Id + ": " + detail);
        }

        public static MkvSplitSubtitleTrack Read(string path, TrackInfo track)
        {
            VobSubIndexDocument document = VobSubIndexDocument.Load(path);
            MkvSplitSubtitleTrack result = new MkvSplitSubtitleTrack { Track = track, Extension = ".idx", SourceContent = document.Serialize() };
            int languages = 0;
            int streamId = 0x20;
            foreach (string raw in document.Lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                {
                    languages++;
                    Match index = Regex.Match(line, @"index:\s*(\d+)", RegexOptions.IgnoreCase);
                    Require(index.Success, track, "IDX language index missing");
                    streamId = 0x20 + int.Parse(index.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                if (line.StartsWith("timestamp:", StringComparison.OrdinalIgnoreCase))
                    Require(TryParseEntryLine(raw, out _, out _), track, "invalid IDX timestamp");
                Require(!line.StartsWith("delay:", StringComparison.OrdinalIgnoreCase), track, "unexpected IDX delay in extracted track");
            }
            Require(languages == 1 && streamId >= 0x20 && streamId <= 0x3f, track, "extraction must contain one DVD subtitle language");
            using FileStream source = File.OpenRead(Path.ChangeExtension(path, ".sub"));
            for (int index = 0; index < document.Entries.Count; index++)
            {
                VobSubIndexEntry entry = document.Entries[index];
                long end = index + 1 < document.Entries.Count ? document.Entries[index + 1].FilePosition : source.Length;
                double time = entry.TimestampMs / 1000.0;
                Require(entry.FilePosition >= 0 && end > entry.FilePosition && end <= source.Length, track, "invalid IDX filepos at event " + time);
                byte[] block = new byte[checked((int)(end - entry.FilePosition))];
                source.Position = entry.FilePosition; source.ReadExactly(block);
                Require(TryExtractPacketizedSpu(block, out byte[] spu, out byte[] pack, out byte[] pes, out byte substream), track, "invalid packetized SPU at event " + time);
                Require(substream == streamId, track, "unexpected subtitle substream");
                Require(TryParseSpu(spu, 0, spu.Length, out VobSubSpuInfo info, true, out string error, false), track, error);
                Require(info.SequenceOffsets.Count == 2, track, "event " + time + " requires dynamic/multiple SPU control-sequence handling");
                int first = info.SequenceOffsets[0];
                int second = info.SequenceOffsets[1];
                List<int> showCommands = info.SequenceCommandOffsets[first];
                List<int> stopCommands = info.SequenceCommandOffsets[second];
                Require(second > first && ReadUInt16BigEndian(spu, second + 2) == second
                    && showCommands.Any(position => spu[position] == 0 || spu[position] == 1)
                    && !showCommands.Any(position => spu[position] == 2) && spu[showCommands.Last()] == 0xff
                    && stopCommands.Count == 2 && spu[stopCommands[0]] == 2 && spu[stopCommands[1]] == 0xff,
                    track, "event " + time + " does not have a static show/stop control pair");
                double startSeconds = time + ReadUInt16BigEndian(spu, first) * 1024.0 / 90000;
                double endSeconds = time + ReadUInt16BigEndian(spu, second) * 1024.0 / 90000;
                Require(endSeconds > startSeconds, track, "invalid SPU duration at event " + time);
                result.Events.Add(new MkvSplitSubtitleEvent { SourceLineIndex = entry.LineIndex, StartSeconds = startSeconds, EndSeconds = endSeconds,
                    BinaryPayload = spu, PacketPackHeader = pack, PacketPesHeader = pes, SubstreamId = substream,
                    StartControlOffset = first, EndControlOffset = second });
            }
            return result;
        }

        public static void Write(MkvSplitSubtitleTrack track, MkvSplitOutputProjection output, string path)
        {
            using FileStream sub = File.Create(Path.ChangeExtension(path, ".sub"));
            VobSubIndexDocument document = VobSubIndexDocument.Parse(track.SourceContent);
            List<VobSubIndexEntryRewrite> occurrences = new List<VobSubIndexEntryRewrite>();
            foreach (MkvSplitClipProjection clip in output.Clips)
                foreach (MkvSplitSubtitleEvent item in track.Events)
                {
                    double start = Math.Max(item.StartSeconds, clip.SourceStartSeconds);
                    double end = Math.Min(item.EndSeconds, clip.SourceEndSeconds);
                    if (end <= start) continue;
                    start += clip.ResultStartSeconds - clip.SourceStartSeconds;
                    end += clip.ResultStartSeconds - clip.SourceStartSeconds;
                    int duration = checked((int)Math.Floor((end - start) * 90000 / 1024));
                    if (duration == 0) continue;
                    if (duration > ushort.MaxValue) throw new InvalidDataException("VobSub display exceeds SPU date range");
                    byte[] spu = (byte[])item.BinaryPayload.Clone();
                    WriteUInt16BigEndian(spu, item.StartControlOffset, 0);
                    WriteUInt16BigEndian(spu, item.EndControlOffset, duration);
                    byte[] pes = RewritePesTimestamps(item.PacketPesHeader, checked((long)Math.Round(start * 90000)));
                    byte[] block = BuildPacketizedSpuBlock(spu, item.PacketPackHeader, pes, item.SubstreamId, out string error);
                    if (block == null) throw new InvalidDataException(error);
                    occurrences.Add(new VobSubIndexEntryRewrite(item.SourceLineIndex, (long)Math.Round(start * 1000), sub.Position));
                    sub.Write(block);
                }
            document.ReplaceEntries(occurrences);
            document.Save(path);
        }
    }
}
