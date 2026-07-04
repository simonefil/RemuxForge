using RemuxForge.Core.Models;
using System;
using System.IO;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive sottotitoli PGS/SUP applicando cut e insert ai timestamp packet
    /// </summary>
    internal class PgsSubtitleTimelineRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Riscrive un file PGS/SUP applicando le operazioni dell'edit map
        /// </summary>
        /// <param name="inputFile">File PGS/SUP originale</param>
        /// <param name="outputFile">File PGS/SUP riscritto</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <returns>True se il file riscritto contiene packet validi</returns>
        public bool Rewrite(string inputFile, string outputFile, EditMap editMap)
        {
            byte[] data = File.ReadAllBytes(inputFile);
            MemoryStream output = new MemoryStream();
            int pos = 0;
            int setStart;
            int setEnd;

            // Il formato SUP/PGS è una sequenza di display-set terminati da segment type 0x80
            while (pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE <= data.Length)
            {
                setStart = pos;
                if (!this.TryFindDisplaySetEnd(data, setStart, out setEnd))
                {
                    return false;
                }

                if (!this.WriteMappedDisplaySet(data, setStart, setEnd, editMap, output))
                {
                    return false;
                }

                pos = setEnd;
            }

            File.WriteAllBytes(outputFile, output.ToArray());
            return output.Length > 0;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Trova la fine del display-set PGS corrente
        /// </summary>
        /// <param name="data">Buffer SUP</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset subito dopo il display-set</param>
        /// <returns>True se il display-set è completo</returns>
        private bool TryFindDisplaySetEnd(byte[] data, int start, out int end)
        {
            int pos = start;
            int packetLength;
            int segmentType;
            end = start;

            while (pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE <= data.Length)
            {
                if (!PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength))
                {
                    return false;
                }

                segmentType = data[pos + 10];
                pos += packetLength;
                if (segmentType == PgsSubtitleUtils.SEGMENT_END)
                {
                    end = pos;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Scrive un display-set completo applicando lo stesso delta temporale a tutti i packet
        /// </summary>
        /// <param name="data">Buffer SUP</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <param name="output">Stream output</param>
        /// <returns>True se il display-set è valido</returns>
        private bool WriteMappedDisplaySet(byte[] data, int start, int end, EditMap editMap, MemoryStream output)
        {
            long firstPtsMs = (long)Math.Round(PgsSubtitleUtils.ReadUInt32BigEndian(data, start + 2) / 90.0);
            long mappedFirstPtsMs = SubtitleTimelineMapper.MapPacketTimestamp(firstPtsMs, editMap);
            long deltaMs;
            int pos = start;
            int packetLength;
            byte[] packet;

            // Un display-set che cade dentro un cut va scartato interamente per non lasciare PCS/ODS/END incompleti
            if (mappedFirstPtsMs < 0)
            {
                return true;
            }

            deltaMs = mappedFirstPtsMs - firstPtsMs;
            while (pos < end)
            {
                if (!PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) || pos + packetLength > end)
                {
                    return false;
                }

                packet = new byte[packetLength];
                Array.Copy(data, pos, packet, 0, packetLength);
                this.OffsetPacketTimestamp(packet, 2, deltaMs);
                this.OffsetPacketTimestamp(packet, 6, deltaMs);
                output.Write(packet, 0, packet.Length);
                pos += packetLength;
            }

            return true;
        }

        /// <summary>
        /// Applica un delta in millisecondi a un timestamp packet
        /// </summary>
        /// <param name="packet">Packet SUP</param>
        /// <param name="offset">Offset timestamp</param>
        /// <param name="deltaMs">Delta in millisecondi</param>
        private void OffsetPacketTimestamp(byte[] packet, int offset, long deltaMs)
        {
            long timestampMs = (long)Math.Round(PgsSubtitleUtils.ReadUInt32BigEndian(packet, offset) / 90.0);
            long mappedMs = timestampMs + deltaMs;
            if (mappedMs < 0)
            {
                mappedMs = 0;
            }

            PgsSubtitleUtils.WriteUInt32BigEndian(packet, offset, (uint)Math.Round(mappedMs * 90.0));
        }

        #endregion
    }
}
