namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Utility binarie condivise per sottotitoli PGS/SUP
    /// </summary>
    internal static class PgsSubtitleUtils
    {
        #region Costanti

        /// <summary>
        /// Dimensione header packet SUP: magic, PTS, DTS, tipo segmento e lunghezza
        /// </summary>
        public const int SUP_PACKET_HEADER_SIZE = 13;

        /// <summary>
        /// Segment type Object Definition Segment
        /// </summary>
        public const int SEGMENT_OBJECT = 0x15;

        /// <summary>
        /// Segment type Presentation Composition Segment
        /// </summary>
        public const int SEGMENT_PRESENTATION = 0x16;

        /// <summary>
        /// Segment type Window Definition Segment
        /// </summary>
        public const int SEGMENT_WINDOW = 0x17;

        /// <summary>
        /// Segment type End Of Display Set
        /// </summary>
        public const int SEGMENT_END = 0x80;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge e valida la lunghezza di un packet SUP
        /// </summary>
        /// <param name="data">Buffer SUP</param>
        /// <param name="pos">Offset packet</param>
        /// <param name="packetLength">Lunghezza packet completa</param>
        /// <returns>True se il packet è valido</returns>
        public static bool TryGetPacketLength(byte[] data, int pos, out int packetLength)
        {
            int size;
            packetLength = 0;
            if (pos + SUP_PACKET_HEADER_SIZE > data.Length || data[pos] != (byte)'P' || data[pos + 1] != (byte)'G')
            {
                return false;
            }

            size = ReadUInt16BigEndian(data, pos + 11);
            packetLength = SUP_PACKET_HEADER_SIZE + size;
            return pos + packetLength <= data.Length;
        }

        /// <summary>
        /// Legge un unsigned 16 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset lettura</param>
        /// <returns>Valore letto</returns>
        public static int ReadUInt16BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// Scrive un unsigned 16 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset scrittura</param>
        /// <param name="value">Valore da scrivere</param>
        public static void WriteUInt16BigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 8) & 0xff);
            data[offset + 1] = (byte)(value & 0xff);
        }

        /// <summary>
        /// Legge un unsigned 32 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset lettura</param>
        /// <returns>Valore letto</returns>
        public static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        }

        /// <summary>
        /// Scrive un unsigned 32 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset scrittura</param>
        /// <param name="value">Valore da scrivere</param>
        public static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)((value >> 24) & 0xff);
            data[offset + 1] = (byte)((value >> 16) & 0xff);
            data[offset + 2] = (byte)((value >> 8) & 0xff);
            data[offset + 3] = (byte)(value & 0xff);
        }

        #endregion
    }
}
