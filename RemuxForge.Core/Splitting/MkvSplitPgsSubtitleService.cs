using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using static RemuxForge.Core.Subtitles.PgsSubtitleUtils;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Ricomposizione di display-set PGS senza decodifica grafica. Ogni show emesso è autonomo rispetto all'epoch.
    /// </summary>
    internal static class MkvSplitPgsSubtitleService
    {
        #region Metodi pubblici

        /// <summary>
        /// Legge un file SUP e lo converte in eventi autonomi, ciascuno con i segmenti necessari a ridisegnarlo
        /// </summary>
        /// <param name="path">File SUP estratto</param>
        /// <param name="track">Traccia sorgente</param>
        /// <param name="videoEnd">Fine del video in secondi, usata se l'ultimo display-set non viene cancellato</param>
        /// <returns>Traccia letta</returns>
        public static MkvSplitSubtitleTrack Read(string path, TrackInfo track, double videoEnd)
        {
            byte[] bytes = File.ReadAllBytes(path);
            MkvSplitSubtitleTrack result = new MkvSplitSubtitleTrack
            {
                Track = track,
                Extension = ".sup"
            };
            Dictionary<int, PgsObjectDefinition> objects = new Dictionary<int, PgsObjectDefinition>();
            Dictionary<int, Dictionary<byte, byte[]>> palettes = new Dictionary<int, Dictionary<byte, byte[]>>();
            Dictionary<int, byte> paletteVersions = new Dictionary<int, byte>();
            Dictionary<byte, byte[]> windows = new Dictionary<byte, byte[]>();
            byte[] pcs = null;
            double timestamp = 0;
            double lastTimestamp = -1;
            int setStart = 0;
            MkvSplitSubtitleEvent active = null;
            for (int offset = 0; offset < bytes.Length;)
            {
                Require(TryGetPacketLength(bytes, offset, out int packetLength), track, "invalid SUP packet at " + offset);
                int length = packetLength - SUP_PACKET_HEADER_SIZE;
                byte type = bytes[offset + 10];
                byte[] data = bytes.AsSpan(offset + SUP_PACKET_HEADER_SIZE, length).ToArray();
                double pts = ReadUInt32BigEndian(bytes, offset + 2) / 90000.0;
                offset += packetLength;
                if (type == SEGMENT_PRESENTATION)
                {
                    Require(pcs == null && length >= 11, track, "missing END or short PCS");
                    Require(pts >= lastTimestamp, track, "wrapped/non-monotonic PTS");
                    pcs = data;
                    timestamp = pts;
                    lastTimestamp = pts;
                    result.BitmapPresentationHeader ??= data.AsSpan(0, 11).ToArray();
                    if ((data[7] & 0x80) != 0)
                    {
                        objects.Clear();
                        palettes.Clear();
                        paletteVersions.Clear();
                        windows.Clear();
                    }
                }
                else if (type == SEGMENT_PALETTE)
                {
                    Require(pcs != null && length >= 2 && (length - 2) % 5 == 0, track, "invalid palette");
                    if (!palettes.TryGetValue(data[0], out Dictionary<byte, byte[]> entries))
                        palettes[data[0]] = entries = new Dictionary<byte, byte[]>();
                    paletteVersions[data[0]] = data[1];
                    for (int index = 2; index < length; index += 5)
                        entries[data[index]] = data.AsSpan(index, 5).ToArray();
                }
                else if (type == SEGMENT_OBJECT)
                {
                    Require(pcs != null && length >= 4, track, "invalid object fragment");
                    // Assembly/validazione ODS affidati al collector condiviso alla chiusura del display-set.
                }
                else if (type == SEGMENT_WINDOW)
                {
                    Require(pcs != null && length >= 1 && length == 1 + data[0] * 9, track, "invalid window segment");
                    for (int position = 1; position < length; position += 9)
                        windows[data[position]] = data.AsSpan(position, 9).ToArray();
                }
                else if (type == SEGMENT_END)
                {
                    Require(pcs != null && length == 0, track, "END without PCS");
                    PgsSubtitleCanvasRewriteReport report = new PgsSubtitleCanvasRewriteReport();
                    Require(CollectDisplaySetObjectDefinitions(bytes, setStart, offset, report, out Dictionary<int, PgsObjectDefinition> definitions), track, report.ErrorMessage);
                    foreach (PgsObjectDefinition definition in definitions.Values)
                        objects[definition.ObjectId] = definition;
                    if (active != null)
                        active.EndSeconds = timestamp;
                    active = null;
                    int count = pcs[10];
                    List<MkvSplitBitmapSegment> segments = new List<MkvSplitBitmapSegment>
                    {
                        new MkvSplitBitmapSegment
                        {
                            Type = SEGMENT_PRESENTATION,
                            Data = pcs
                        }
                    };
                    if (count > 0)
                    {
                        if (windows.Count > 0)
                        {
                            Require(windows.Count <= byte.MaxValue, track, "too many windows");
                            List<byte> windowData = new List<byte> { (byte)windows.Count };
                            foreach (byte[] window in windows.Values)
                                windowData.AddRange(window);
                            segments.Add(new MkvSplitBitmapSegment
                            {
                                Type = SEGMENT_WINDOW,
                                Data = windowData.ToArray()
                            });
                        }
                        Require(palettes.TryGetValue(pcs[9], out Dictionary<byte, byte[]> entries), track, "missing palette " + pcs[9]);
                        List<byte> palette = new List<byte> { pcs[9], paletteVersions[pcs[9]] };
                        foreach (byte[] entry in entries.Values)
                            palette.AddRange(entry);
                        segments.Add(new MkvSplitBitmapSegment
                        {
                            Type = SEGMENT_PALETTE,
                            Data = palette.ToArray()
                        });
                        int position = 11;
                        HashSet<int> emitted = new HashSet<int>();
                        for (int index = 0; index < count; index++)
                        {
                            Require(position + 8 <= pcs.Length, track, "truncated object reference");
                            int id = ReadUInt16BigEndian(pcs, position);
                            bool crop = (pcs[position + 3] & 0x80) != 0;
                            position += crop ? 16 : 8;
                            Require(position <= pcs.Length && objects.TryGetValue(id, out _), track, "missing object " + id);
                            if (emitted.Add(id))
                            {
                                Require(BuildObjectDefinitionPackets(objects[id], out List<byte[]> packets, out string error), track, error);
                                foreach (byte[] packet in packets)
                                {
                                    segments.Add(new MkvSplitBitmapSegment
                                    {
                                        Type = SEGMENT_OBJECT,
                                        Data = packet.AsSpan(SUP_PACKET_HEADER_SIZE).ToArray()
                                    });
                                }
                            }
                        }
                        Require(position == pcs.Length, track, "non-standard PCS layout");
                        active = new MkvSplitSubtitleEvent
                        {
                            StartSeconds = timestamp,
                            EndSeconds = double.NaN,
                            BitmapSegments = segments
                        };
                        result.Events.Add(active);
                    }
                    else
                    {
                        Require(pcs.Length == 11, track, "non-standard clear PCS layout");
                    }
                    pcs = null;
                    setStart = offset;
                }
                else
                {
                    Require(false, track, "unknown segment " + type);
                }
            }
            Require(pcs == null, track, "unfinished display-set");
            if (active != null)
            {
                Require(double.IsFinite(videoEnd) && videoEnd >= active.StartSeconds, track, "missing clear and video end");
                active.EndSeconds = videoEnd;
            }
            result.Events.RemoveAll(item => item.EndSeconds == item.StartSeconds);
            Require(result.Events.All(item => double.IsFinite(item.EndSeconds) && item.EndSeconds > item.StartSeconds), track, "invalid display interval");
            return result;
        }

        /// <summary>
        /// Scrive il file SUP di un output: ogni occorrenza è un display-set completo seguito da un display-set di cancellazione
        /// </summary>
        /// <param name="track">Traccia letta dal sorgente</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File SUP di destinazione</param>
        public static void Write(MkvSplitSubtitleTrack track, MkvSplitOutputProjection output, string path)
        {
            using FileStream stream = File.Create(path);
            int composition = 0;
            bool any = false;
            foreach (MkvSplitClipProjection clip in output.Clips)
            {
                foreach (MkvSplitSubtitleEvent item in track.Events)
                {
                    double start = Math.Max(item.StartSeconds, clip.SourceStartSeconds);
                    double end = Math.Min(item.EndSeconds, clip.SourceEndSeconds);
                    if (end <= start)
                        continue;
                    start += clip.ResultStartSeconds - clip.SourceStartSeconds;
                    end += clip.ResultStartSeconds - clip.SourceStartSeconds;
                    if (Math.Round(start * 90000) == Math.Round(end * 90000))
                        continue;
                    byte[] header = null;
                    foreach (MkvSplitBitmapSegment segment in item.BitmapSegments)
                    {
                        byte[] data = segment.Data;
                        if (segment.Type == SEGMENT_PRESENTATION)
                        {
                            data = (byte[])data.Clone();
                            WriteUInt16BigEndian(data, 5, composition++);
                            data[7] = 0x80;
                            data[8] = 0;
                            header = data;
                        }
                        Packet(stream, segment.Type, data, start);
                    }
                    Packet(stream, SEGMENT_END, Array.Empty<byte>(), start);
                    Clear(stream, header, end, composition++);
                    any = true;
                }
            }
            // Conservare una traccia vuota, non scartarla: un display-set clear è un payload PGS valido.
            if (!any)
            {
                if (track.BitmapPresentationHeader == null)
                    throw new InvalidDataException("PGS has no presentation header");
                Clear(stream, track.BitmapPresentationHeader, 0, composition);
            }
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
                throw new InvalidDataException("PGS track " + track.Id + ": " + detail);
        }

        /// <summary>
        /// Scrive un pacchetto SUP con PTS e DTS uguali al tempo indicato
        /// </summary>
        /// <param name="stream">Stream di destinazione</param>
        /// <param name="type">Tipo di segmento</param>
        /// <param name="data">Dati del segmento</param>
        /// <param name="seconds">Tempo del pacchetto in secondi</param>
        private static void Packet(Stream stream, byte type, byte[] data, double seconds)
        {
            double ticks = Math.Round(seconds * 90000, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(ticks) || ticks < 0 || ticks > uint.MaxValue || data.Length > ushort.MaxValue)
                throw new InvalidDataException("PGS timestamp/segment exceeds SUP limits");
            uint value = (uint)ticks;
            byte[] header = new byte[SUP_PACKET_HEADER_SIZE];
            header[0] = (byte)'P';
            header[1] = (byte)'G';
            header[10] = type;
            WriteUInt32BigEndian(header, 2, value);
            WriteUInt32BigEndian(header, 6, value);
            WriteUInt16BigEndian(header, 11, data.Length);
            stream.Write(header);
            stream.Write(data);
        }

        /// <summary>
        /// Scrive un display-set di cancellazione derivato da un'intestazione PCS
        /// </summary>
        /// <param name="stream">Stream di destinazione</param>
        /// <param name="source">PCS di partenza</param>
        /// <param name="seconds">Tempo della cancellazione in secondi</param>
        /// <param name="composition">Numero di composizione</param>
        private static void Clear(Stream stream, byte[] source, double seconds, int composition)
        {
            byte[] clear = source.AsSpan(0, 11).ToArray();
            WriteUInt16BigEndian(clear, 5, composition);
            clear[7] = 0x80;
            clear[8] = 0;
            clear[10] = 0;
            Packet(stream, SEGMENT_PRESENTATION, clear, seconds);
            Packet(stream, SEGMENT_END, Array.Empty<byte>(), seconds);
        }

        #endregion
    }
}
